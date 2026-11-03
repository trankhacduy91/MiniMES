using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Packing;

namespace MiniMes.Client.Core.Api;

// M3b: the Packing screen of the kiosk (pack, palletize, partial close with a supervisor's badge).
public interface IPackingApi
{
    Task<PackResponse> PackAsync(int stationId, string serialNo, CancellationToken ct = default);
    Task<PalletizeResponse> PalletizeAsync(int stationId, string cartonNo, CancellationToken ct = default);
    Task<PackingStatusDto> GetStatusAsync(int stationId, CancellationToken ct = default);
    Task<CartonDto> ClosePartialCartonAsync(string cartonNo, ClosePartialRequest request, CancellationToken ct = default);
    Task<PalletDto> ClosePartialPalletAsync(string palletNo, ClosePartialRequest request, CancellationToken ct = default);
}

public sealed class PackingApi(ApiClient api) : IPackingApi
{
    private const string Stations = "api/stations";

    public Task<PackResponse> PackAsync(int stationId, string serialNo, CancellationToken ct = default)
        => api.PostAsync<PackRequest, PackResponse>($"{Stations}/{stationId}/pack", new PackRequest(serialNo), ct);

    public Task<PalletizeResponse> PalletizeAsync(int stationId, string cartonNo, CancellationToken ct = default)
        => api.PostAsync<PalletizeRequest, PalletizeResponse>($"{Stations}/{stationId}/palletize", new PalletizeRequest(cartonNo), ct);

    public Task<PackingStatusDto> GetStatusAsync(int stationId, CancellationToken ct = default)
        => api.GetAsync<PackingStatusDto>($"{Stations}/{stationId}/packing-status", ct);

    public Task<CartonDto> ClosePartialCartonAsync(string cartonNo, ClosePartialRequest request, CancellationToken ct = default)
        => api.PostAsync<ClosePartialRequest, CartonDto>($"api/cartons/{Uri.EscapeDataString(cartonNo)}/close-partial", request, ct);

    public Task<PalletDto> ClosePartialPalletAsync(string palletNo, ClosePartialRequest request, CancellationToken ct = default)
        => api.PostAsync<ClosePartialRequest, PalletDto>($"api/pallets/{Uri.EscapeDataString(palletNo)}/close-partial", request, ct);
}
