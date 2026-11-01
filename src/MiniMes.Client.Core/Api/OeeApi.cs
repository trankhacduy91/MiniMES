using System.Globalization;
using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Oee;

namespace MiniMes.Client.Core.Api;

// M5 step 8: what the OEE dashboard reads and does. Every number is computed on the server (one OEE formula).
public interface IOeeApi
{
    // date + shift null = the running shift; exactly one of machine / line.
    Task<ShiftOeeDto> GetShiftAsync(DateOnly? date, string? shift, string? machine, string? line, CancellationToken ct = default);
    Task<ParetoDto> GetParetoAsync(DateOnly date, string shift, string? machine, string? line, string by, CancellationToken ct = default);
    Task<IReadOnlyList<UnclassifiedDowntimeDto>> GetUnclassifiedAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<DowntimeSegmentDto> AssignReasonAsync(long segmentId, int downtimeReasonId, string? note, CancellationToken ct = default);

    // [v2] Step 8b: the Excel files of spec 2.7, built on the server from the same queries.
    Task<byte[]> GetOeeExcelAsync(DateOnly from, DateOnly to, string? line, CancellationToken ct = default);
    Task<byte[]> GetParetoExcelAsync(DateOnly from, DateOnly to, string? line, string by, CancellationToken ct = default);
}

public sealed class OeeApi(ApiClient api) : IOeeApi
{
    public Task<ShiftOeeDto> GetShiftAsync(DateOnly? date, string? shift, string? machine, string? line, CancellationToken ct = default)
        => api.GetAsync<ShiftOeeDto>("api/oee/shift" + Query(("date", date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("shift", shift), ("machine", machine), ("line", line)), ct);

    public Task<ParetoDto> GetParetoAsync(DateOnly date, string shift, string? machine, string? line, string by, CancellationToken ct = default)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return api.GetAsync<ParetoDto>("api/downtime/pareto" + Query(("from", day), ("to", day), ("shift", shift),
            ("machine", machine), ("line", line), ("by", by)), ct);
    }

    public Task<IReadOnlyList<UnclassifiedDowntimeDto>> GetUnclassifiedAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<UnclassifiedDowntimeDto>>("api/downtime/unclassified" + Query(
            ("from", fromUtc.ToString("O", CultureInfo.InvariantCulture)), ("to", toUtc.ToString("O", CultureInfo.InvariantCulture))), ct);

    public Task<DowntimeSegmentDto> AssignReasonAsync(long segmentId, int downtimeReasonId, string? note, CancellationToken ct = default)
        => api.PutAsync<AssignDowntimeReasonRequest, DowntimeSegmentDto>($"api/downtime/{segmentId}/reason",
            new AssignDowntimeReasonRequest(downtimeReasonId, note), ct);

    public Task<byte[]> GetOeeExcelAsync(DateOnly from, DateOnly to, string? line, CancellationToken ct = default)
        => api.GetBytesAsync("api/reports/oee.xlsx" + Query(("from", Day(from)), ("to", Day(to)), ("line", line)), ct);

    public Task<byte[]> GetParetoExcelAsync(DateOnly from, DateOnly to, string? line, string by, CancellationToken ct = default)
        => api.GetBytesAsync("api/reports/downtime-pareto.xlsx" + Query(("from", Day(from)), ("to", Day(to)), ("line", line), ("by", by)), ct);

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Query(params (string Name, string? Value)[] parts)
    {
        var given = parts.Where(p => p.Value is not null).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}").ToList();
        return given.Count == 0 ? "" : "?" + string.Join("&", given);
    }
}
