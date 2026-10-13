using System.Globalization;
using MiniMes.Client.Core.Http;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.Api;

// Typed wrapper over the M1 endpoints: ViewModels call methods, never build URLs. An interface so
// ViewModel tests can use a fake instead of HTTP.
public interface IMasterDataApi
{
    // Items
    Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default);
    Task<ItemDto> GetItemAsync(int id, CancellationToken ct = default);
    Task<ItemDto> CreateItemAsync(CreateItemRequest request, CancellationToken ct = default);
    Task<ItemDto> UpdateItemAsync(int id, UpdateItemRequest request, CancellationToken ct = default);
    Task DeactivateItemAsync(int id, CancellationToken ct = default);

    // BOM
    Task<IReadOnlyList<BomSummaryDto>> GetBomsAsync(int itemId, CancellationToken ct = default);
    Task<BomDto> GetBomAsync(int id, CancellationToken ct = default);
    Task<BomDto> CreateBomDraftAsync(CreateDraftRequest request, CancellationToken ct = default);
    Task<BomDto> SaveBomLinesAsync(int bomId, SaveBomLinesRequest request, CancellationToken ct = default);
    Task<BomDto> ApproveBomAsync(int bomId, ApproveVersionRequest request, CancellationToken ct = default);
    Task<BomTreeNodeDto> GetBomTreeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default);
    Task<BomExplosionDto> ExplodeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default);

    // Routing
    Task<IReadOnlyList<RoutingSummaryDto>> GetRoutingsAsync(int itemId, CancellationToken ct = default);
    Task<RoutingDto> GetRoutingAsync(int id, CancellationToken ct = default);
    Task<RoutingDto> CreateRoutingDraftAsync(CreateDraftRequest request, CancellationToken ct = default);
    Task<RoutingDto> SaveRoutingStepsAsync(int routingId, SaveRoutingStepsRequest request, CancellationToken ct = default);
    Task<RoutingDto> ApproveRoutingAsync(int routingId, ApproveVersionRequest request, CancellationToken ct = default);

    // Catalogs and equipment
    Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkCenterDto>> GetWorkCentersAsync(bool? isActive = null, CancellationToken ct = default);
    Task<WorkCenterDto> CreateWorkCenterAsync(CreateWorkCenterRequest request, CancellationToken ct = default);
    Task<WorkCenterDto> UpdateWorkCenterAsync(int id, UpdateWorkCenterRequest request, CancellationToken ct = default);
    Task DeactivateWorkCenterAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<MachineDto>> GetMachinesAsync(CancellationToken ct = default);
    Task<MachineDto> CreateMachineAsync(CreateMachineRequest request, CancellationToken ct = default);
    Task<MachineDto> UpdateMachineAsync(int id, UpdateMachineRequest request, CancellationToken ct = default);
    Task DeactivateMachineAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(CancellationToken ct = default);
    Task<ShiftDto> CreateShiftAsync(CreateShiftRequest request, CancellationToken ct = default);
    Task<ShiftDto> UpdateShiftAsync(int id, UpdateShiftRequest request, CancellationToken ct = default);
    Task DeactivateShiftAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default);
    Task<DefectCodeDto> CreateDefectCodeAsync(CreateDefectCodeRequest request, CancellationToken ct = default);
    Task<DefectCodeDto> UpdateDefectCodeAsync(int id, UpdateDefectCodeRequest request, CancellationToken ct = default);
    Task DeactivateDefectCodeAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default);
    Task<DowntimeReasonDto> CreateDowntimeReasonAsync(CreateDowntimeReasonRequest request, CancellationToken ct = default);
    Task<DowntimeReasonDto> UpdateDowntimeReasonAsync(int id, UpdateDowntimeReasonRequest request, CancellationToken ct = default);
    Task DeactivateDowntimeReasonAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<PlantCalendarDayDto>> GetPlantCalendarAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<PlantCalendarDayDto> SetPlantCalendarDayAsync(DateOnly date, SetPlantCalendarDayRequest request, CancellationToken ct = default);
    Task RemovePlantCalendarDayAsync(DateOnly date, CancellationToken ct = default);

    // Import
    Task<byte[]> GetImportTemplateAsync(CancellationToken ct = default);
    Task<ImportResultDto> ImportMasterDataAsync(string fileName, byte[] content, bool dryRun, CancellationToken ct = default);
}

public sealed class MasterDataApi(ApiClient api) : IMasterDataApi
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Num(decimal n) => n.ToString(CultureInfo.InvariantCulture); // "0.5", never "0,5"
    private static string AsOf(DateTime? at) => at is { } a ? $"&asOfUtc={a.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}" : "";

    public Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default)
    {
        var query = $"api/items?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (!string.IsNullOrEmpty(type)) query += $"&type={type}";
        if (isActive is { } a) query += $"&isActive={a.ToString().ToLowerInvariant()}";
        return api.GetAsync<PagedResult<ItemDto>>(query, ct);
    }

    public Task<ItemDto> GetItemAsync(int id, CancellationToken ct = default) => api.GetAsync<ItemDto>($"api/items/{id}", ct);
    public Task<ItemDto> CreateItemAsync(CreateItemRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateItemRequest, ItemDto>("api/items", request, ct);
    public Task<ItemDto> UpdateItemAsync(int id, UpdateItemRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateItemRequest, ItemDto>($"api/items/{id}", request, ct);
    public Task DeactivateItemAsync(int id, CancellationToken ct = default) => api.PostAsync($"api/items/{id}/deactivate", ct);

    public Task<IReadOnlyList<BomSummaryDto>> GetBomsAsync(int itemId, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<BomSummaryDto>>($"api/boms?itemId={itemId}", ct);
    public Task<BomDto> GetBomAsync(int id, CancellationToken ct = default) => api.GetAsync<BomDto>($"api/boms/{id}", ct);
    public Task<BomDto> CreateBomDraftAsync(CreateDraftRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateDraftRequest, BomDto>("api/boms", request, ct);
    public Task<BomDto> SaveBomLinesAsync(int bomId, SaveBomLinesRequest request, CancellationToken ct = default)
        => api.PutAsync<SaveBomLinesRequest, BomDto>($"api/boms/{bomId}/lines", request, ct);
    public Task<BomDto> ApproveBomAsync(int bomId, ApproveVersionRequest request, CancellationToken ct = default)
        => api.PostAsync<ApproveVersionRequest, BomDto>($"api/boms/{bomId}/approve", request, ct);
    public Task<BomTreeNodeDto> GetBomTreeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default)
        => api.GetAsync<BomTreeNodeDto>($"api/items/{itemId}/bom-tree?qty={Num(qty)}{AsOf(asOfUtc)}", ct);
    public Task<BomExplosionDto> ExplodeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default)
        => api.GetAsync<BomExplosionDto>($"api/items/{itemId}/explosion?qty={Num(qty)}{AsOf(asOfUtc)}", ct);

    public Task<IReadOnlyList<RoutingSummaryDto>> GetRoutingsAsync(int itemId, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<RoutingSummaryDto>>($"api/routings?itemId={itemId}", ct);
    public Task<RoutingDto> GetRoutingAsync(int id, CancellationToken ct = default) => api.GetAsync<RoutingDto>($"api/routings/{id}", ct);
    public Task<RoutingDto> CreateRoutingDraftAsync(CreateDraftRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateDraftRequest, RoutingDto>("api/routings", request, ct);
    public Task<RoutingDto> SaveRoutingStepsAsync(int routingId, SaveRoutingStepsRequest request, CancellationToken ct = default)
        => api.PutAsync<SaveRoutingStepsRequest, RoutingDto>($"api/routings/{routingId}/steps", request, ct);
    public Task<RoutingDto> ApproveRoutingAsync(int routingId, ApproveVersionRequest request, CancellationToken ct = default)
        => api.PostAsync<ApproveVersionRequest, RoutingDto>($"api/routings/{routingId}/approve", request, ct);

    public Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<LineDto>>("api/lines", ct);
    public Task<IReadOnlyList<WorkCenterDto>> GetWorkCentersAsync(bool? isActive = null, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<WorkCenterDto>>(isActive is { } a ? $"api/work-centers?isActive={a.ToString().ToLowerInvariant()}" : "api/work-centers", ct);
    public Task<WorkCenterDto> CreateWorkCenterAsync(CreateWorkCenterRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateWorkCenterRequest, WorkCenterDto>("api/work-centers", request, ct);
    public Task<WorkCenterDto> UpdateWorkCenterAsync(int id, UpdateWorkCenterRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateWorkCenterRequest, WorkCenterDto>($"api/work-centers/{id}", request, ct);
    public Task DeactivateWorkCenterAsync(int id, CancellationToken ct = default) => api.PostAsync($"api/work-centers/{id}/deactivate", ct);

    public Task<IReadOnlyList<MachineDto>> GetMachinesAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<MachineDto>>("api/machines", ct);
    public Task<MachineDto> CreateMachineAsync(CreateMachineRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateMachineRequest, MachineDto>("api/machines", request, ct);
    public Task<MachineDto> UpdateMachineAsync(int id, UpdateMachineRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateMachineRequest, MachineDto>($"api/machines/{id}", request, ct);
    public Task DeactivateMachineAsync(int id, CancellationToken ct = default) => api.PostAsync($"api/machines/{id}/deactivate", ct);

    public Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<ShiftDto>>("api/shifts", ct);
    public Task<ShiftDto> CreateShiftAsync(CreateShiftRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateShiftRequest, ShiftDto>("api/shifts", request, ct);
    public Task<ShiftDto> UpdateShiftAsync(int id, UpdateShiftRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateShiftRequest, ShiftDto>($"api/shifts/{id}", request, ct);
    public Task DeactivateShiftAsync(int id, CancellationToken ct = default) => api.PostAsync($"api/shifts/{id}/deactivate", ct);

    public Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<DefectCodeDto>>("api/defect-codes", ct);
    public Task<DefectCodeDto> CreateDefectCodeAsync(CreateDefectCodeRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateDefectCodeRequest, DefectCodeDto>("api/defect-codes", request, ct);
    public Task<DefectCodeDto> UpdateDefectCodeAsync(int id, UpdateDefectCodeRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateDefectCodeRequest, DefectCodeDto>($"api/defect-codes/{id}", request, ct);
    public Task DeactivateDefectCodeAsync(int id, CancellationToken ct = default) => api.PostAsync($"api/defect-codes/{id}/deactivate", ct);

    public Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<DowntimeReasonDto>>("api/downtime-reasons", ct);
    public Task<DowntimeReasonDto> CreateDowntimeReasonAsync(CreateDowntimeReasonRequest request, CancellationToken ct = default)
        => api.PostAsync<CreateDowntimeReasonRequest, DowntimeReasonDto>("api/downtime-reasons", request, ct);
    public Task<DowntimeReasonDto> UpdateDowntimeReasonAsync(int id, UpdateDowntimeReasonRequest request, CancellationToken ct = default)
        => api.PutAsync<UpdateDowntimeReasonRequest, DowntimeReasonDto>($"api/downtime-reasons/{id}", request, ct);
    public Task DeactivateDowntimeReasonAsync(int id, CancellationToken ct = default) => api.PostAsync($"api/downtime-reasons/{id}/deactivate", ct);

    public Task<IReadOnlyList<PlantCalendarDayDto>> GetPlantCalendarAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => api.GetAsync<IReadOnlyList<PlantCalendarDayDto>>($"api/plant-calendar?from={D(from)}&to={D(to)}", ct);
    public Task<PlantCalendarDayDto> SetPlantCalendarDayAsync(DateOnly date, SetPlantCalendarDayRequest request, CancellationToken ct = default)
        => api.PutAsync<SetPlantCalendarDayRequest, PlantCalendarDayDto>($"api/plant-calendar/{D(date)}", request, ct);
    public Task RemovePlantCalendarDayAsync(DateOnly date, CancellationToken ct = default) => api.DeleteAsync($"api/plant-calendar/{D(date)}", ct);

    public Task<byte[]> GetImportTemplateAsync(CancellationToken ct = default) => api.GetBytesAsync("api/import/template", ct);
    public Task<ImportResultDto> ImportMasterDataAsync(string fileName, byte[] content, bool dryRun, CancellationToken ct = default)
        => api.PostFileAsync<ImportResultDto>($"api/import/master-data?dryRun={dryRun.ToString().ToLowerInvariant()}", "file", fileName, content, ct);
}
