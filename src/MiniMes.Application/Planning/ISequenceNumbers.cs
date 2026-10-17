namespace MiniMes.Application.Planning;

// SQL Server sequences (Infrastructure). Each call returns numbers nobody else will ever get, without locks;
// a rolled-back transaction leaves a gap, never a duplicate (spec M2 2.2).
public interface ISequenceNumbers
{
    Task<long> NextWorkOrderNumberAsync(CancellationToken ct);
}
