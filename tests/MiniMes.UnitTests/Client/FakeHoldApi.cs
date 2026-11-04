using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Quality;
using MiniMes.Contracts.Traceability;

namespace MiniMes.UnitTests.Client;

// M7 step 9: records what the Hold panel and the Holds screen send; answers are set by the test.
internal sealed class FakeHoldApi : IHoldApi
{
    public List<HoldTargetsRequest> Previews { get; } = [];
    public List<HoldTargetsRequest> Holds { get; } = [];
    public List<(Guid Batch, string Note)> Releases { get; } = [];
    public List<HoldBatchDto> Batches { get; } = [];
    public ApiException? Fail { get; set; }

    public HoldPreviewDto Preview { get; set; } = new("Lot", "L-CAP-B", 87, [new("Completed", 8), new("InProcess", 79)], 0, 3, 1,
        ["L-CAP-A", "L-CAP-B"], [new SkippedLotDto("L-CAP-P", "Consumed")]);

    public Task<IReadOnlyList<HoldBatchDto>> GetBatchesAsync(bool activeOnly = true, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HoldBatchDto>>(Batches.ToList());

    public Task<HoldPreviewDto> PreviewAsync(HoldTargetsRequest request, CancellationToken ct = default)
    {
        Previews.Add(request);
        return Fail is { } ex ? Task.FromException<HoldPreviewDto>(ex) : Task.FromResult(Preview);
    }

    public Task<HoldResultDto> HoldAsync(HoldTargetsRequest request, CancellationToken ct = default)
    {
        Holds.Add(request);
        return Task.FromResult(new HoldResultDto(Guid.NewGuid(), Preview.SerialCount, Preview.Lots.Count,
            request.CreateNcr ? 9 : null, request.CreateNcr ? "NCR-000009" : null));
    }

    public Task<ReleaseResultDto> ReleaseAsync(Guid batchId, string note, CancellationToken ct = default)
    {
        Releases.Add((batchId, note));
        return Task.FromResult(new ReleaseResultDto(batchId, 5, 4, 1));
    }
}
