using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Andon;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Andon;
using MiniMes.Domain.Andon;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

public sealed class AndonOptions
{
    public const string SectionName = "Andon";

    // X of spec 2.9: how long a machine must stay Down before the system raises the Andon itself.
    public int AutoRaiseAfterSeconds { get; set; } = (int)AndonAutoRaise.DefaultAfter.TotalSeconds;
}

// Spec M4 2.9: every 5 s, machines Down for X seconds or more get a Machine Andon from "system" (RaiseAndonCommand,
// the same use case as the kiosk button). Reads the runtime store (RAM); the database only when a machine is due.
// One automatic call per Down period: a call of this machine raised since the Down began (open or already closed)
// means nothing to do. Without it, a supervisor resolving while the machine is still Down would get a new call
// 5 s later, and an API restart would not know what it had raised before.
// [v2] Step 7b: the same tick escalates the calls nobody acknowledged (EscalateAndonsCommand). Computed from RaisedAt
// and the EscalationLevel stored in the database, so a restart neither loses nor repeats an escalation.
public sealed class AndonWatcherService(
    MachineRuntimeStore store,
    MachineDirectory machines,
    IServiceScopeFactory scopes,
    IOptions<AndonOptions> options,
    TimeProvider clock,
    ILogger<AndonWatcherService> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Andon watcher failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await AutoRaiseAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), sender, ct);
        await sender.Send(new EscalateAndonsCommand(), ct);
    }

    private async Task AutoRaiseAsync(AppDbContext db, ISender sender, CancellationToken ct)
    {
        var due = AndonAutoRaise.Due(store.All(), clock.GetUtcNow().UtcDateTime,
            TimeSpan.FromSeconds(options.Value.AutoRaiseAfterSeconds));
        foreach (var down in due)
        {
            var covered = await db.AndonCalls.AsNoTracking().AnyAsync(a =>
                a.MachineId == down.MachineId && a.Type == AndonType.Machine
                && (a.Status <= AndonStatus.Acknowledged || a.RaisedAt >= down.DownSince), ct);
            if (covered) continue;

            var result = await sender.Send(new RaiseAndonCommand(down.MachineId, AndonTypes.Machine, Auto: true), ct);
            if (result.Created)
                log.LogWarning("Andon {AndonId} raised automatically for {Machine}: Down since {DownSince:O}",
                    result.Andon.Id, machines.CodeOf(down.MachineId), down.DownSince);
        }
    }
}
