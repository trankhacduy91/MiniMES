using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MiniMes.Application.Common;
using MiniMes.Domain.Audit;
using MiniMes.Domain.Common;

namespace MiniMes.Infrastructure.Persistence.Interceptors;

// Every SaveChanges goes through here, so no use case can "forget" to audit.
// Data and audit rows commit or fail together:
//  - Modified/Deleted rows are added before the save -> same SaveChanges, same transaction.
//  - Added rows only get their identity Id after the save, so they are written by a second
//    SaveChanges in SavedChanges. Both saves run in one transaction: ours if we had to open it,
//    or the caller's (e.g. WO release in M2), which we reuse and never commit for them.
// Scoped: one instance per DbContext, so the fields below hold the state of the current save.
public sealed class AuditSaveChangesInterceptor(ICurrentUser currentUser, TimeProvider time) : SaveChangesInterceptor
{
    private const string Masked = "***";
    private const string SystemUser = "system"; // seeder, background jobs: no HTTP user

    private List<EntityEntry> _pendingAdded = [];
    private IDbContextTransaction? _ownTransaction;
    private bool _savingAuditRows; // the 2nd SaveChanges passes through this interceptor again

    // ---------- async ----------

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db && !_savingAuditRows && BeforeSave(db))
            _ownTransaction = await db.Database.BeginTransactionAsync(ct);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (eventData.Context is not { } db || _savingAuditRows) return result;
        try
        {
            if (AddLogsForAddedEntries(db))
            {
                _savingAuditRows = true;
                await db.SaveChangesAsync(ct);
            }
            if (_ownTransaction is not null) await _ownTransaction.CommitAsync(ct);
        }
        catch
        {
            if (_ownTransaction is not null) await _ownTransaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await ResetAsync();
        }
        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        if (!_savingAuditRows) await ResetAsync(); // disposing an uncommitted transaction rolls it back
    }

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken ct = default)
    {
        if (!_savingAuditRows) await ResetAsync();
    }

    // ---------- sync (same logic, for code that calls SaveChanges()) ----------

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db && !_savingAuditRows && BeforeSave(db))
            _ownTransaction = db.Database.BeginTransaction();
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is not { } db || _savingAuditRows) return result;
        try
        {
            if (AddLogsForAddedEntries(db))
            {
                _savingAuditRows = true;
                db.SaveChanges();
            }
            _ownTransaction?.Commit();
        }
        catch
        {
            _ownTransaction?.Rollback();
            throw;
        }
        finally
        {
            Reset();
        }
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (!_savingAuditRows) Reset();
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (!_savingAuditRows) Reset();
    }

    // ---------- core ----------

    /// <returns>true when we must open a transaction (Added entries and none open yet).</returns>
    private bool BeforeSave(DbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var now = time.GetUtcNow().UtcDateTime;
        var by = ChangedBy();

        foreach (var e in db.ChangeTracker.Entries<AuditableEntity>().Where(e => e.State == EntityState.Added))
        {
            e.Entity.CreatedAt = now;
            e.Entity.CreatedBy = by;
        }

        // ToList first: we add AuditLog entries to the same ChangeTracker below.
        var audited = db.ChangeTracker.Entries().Where(e => e.Entity is IAudited).ToList();
        var logs = new List<AuditLog>();

        foreach (var entry in audited)
        {
            switch (entry.State)
            {
                case EntityState.Modified:
                    var key = RecordKey(entry);
                    foreach (var p in entry.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
                    {
                        var ignored = IsIgnored(p);
                        logs.Add(new AuditLog(TableName(entry), key, p.Metadata.Name,
                            ignored ? Masked : Format(p.OriginalValue),
                            ignored ? Masked : Format(p.CurrentValue), by, now));
                    }
                    break;

                case EntityState.Deleted:
                    logs.Add(new AuditLog(TableName(entry), RecordKey(entry), AuditLog.AllColumns,
                        ToJson(entry, original: true), null, by, now));
                    break;
            }
        }

        db.Set<AuditLog>().AddRange(logs);
        _pendingAdded = audited.Where(e => e.State == EntityState.Added).ToList();
        return _pendingAdded.Count > 0 && db.Database.CurrentTransaction is null;
    }

    /// <returns>true when AuditLog rows were added and a second save is needed.</returns>
    private bool AddLogsForAddedEntries(DbContext db)
    {
        if (_pendingAdded.Count == 0) return false;

        var now = time.GetUtcNow().UtcDateTime;
        var by = ChangedBy();
        db.Set<AuditLog>().AddRange(_pendingAdded.Select(entry =>
            new AuditLog(TableName(entry), RecordKey(entry), AuditLog.AllColumns,
                null, ToJson(entry, original: false), by, now)));
        _pendingAdded = [];
        return true;
    }

    private string ChangedBy() => currentUser.EmployeeCode ?? SystemUser;

    private static string TableName(EntityEntry entry) => entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name;

    // Composite keys (UserRoles) become "3,Admin".
    private static string RecordKey(EntityEntry entry)
        => string.Join(",", entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => Format(p.CurrentValue)));

    private static string ToJson(EntityEntry entry, bool original)
        => JsonSerializer.Serialize(entry.Properties.ToDictionary(
            p => p.Metadata.Name,
            p => IsIgnored(p) ? Masked : Format(original ? p.OriginalValue : p.CurrentValue)));

    private static bool IsIgnored(PropertyEntry p)
        => p.Metadata.PropertyInfo?.GetCustomAttribute<AuditIgnoreAttribute>() is not null;

    // Invariant culture + ISO dates: the audit text must not depend on the server's locale.
    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private async Task ResetAsync()
    {
        if (_ownTransaction is not null) await _ownTransaction.DisposeAsync();
        ClearState();
    }

    private void Reset()
    {
        _ownTransaction?.Dispose();
        ClearState();
    }

    private void ClearState()
    {
        _ownTransaction = null;
        _pendingAdded = [];
        _savingAuditRows = false;
    }
}
