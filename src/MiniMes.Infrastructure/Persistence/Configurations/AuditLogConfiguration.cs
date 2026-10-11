using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniMes.Domain.Audit;

namespace MiniMes.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLog"); // name used by trigger TR_AuditLog_BlockModify
        b.Property(x => x.TableName).HasMaxLength(128);
        b.Property(x => x.RecordKey).HasMaxLength(100);
        b.Property(x => x.ColumnName).HasMaxLength(128);
        b.Property(x => x.ChangedBy).HasMaxLength(100);
        // OldValue/NewValue stay nvarchar(max): Added/Deleted rows hold JSON of every column.

        // "History of record X" and "what changed between T1 and T2" are the two main queries.
        b.HasIndex(x => new { x.TableName, x.RecordKey });
        b.HasIndex(x => x.ChangedAt);
    }
}
