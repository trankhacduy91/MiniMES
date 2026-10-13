using MiniMes.Client.Core.Api;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// Base fake for ViewModel tests: every call fails loudly unless a test overrides it, so a test
// notices when a screen calls an endpoint it did not expect. Generated from IMasterDataApi.
public class FakeMasterDataApi : IMasterDataApi
{
    public virtual Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<ItemDto> GetItemAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<ItemDto> CreateItemAsync(CreateItemRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<ItemDto> UpdateItemAsync(int id, UpdateItemRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task DeactivateItemAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<BomSummaryDto>> GetBomsAsync(int itemId, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<BomDto> GetBomAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<BomDto> CreateBomDraftAsync(CreateDraftRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<BomDto> SaveBomLinesAsync(int bomId, SaveBomLinesRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<BomDto> ApproveBomAsync(int bomId, ApproveVersionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<BomTreeNodeDto> GetBomTreeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<BomExplosionDto> ExplodeAsync(int itemId, decimal qty, DateTime? asOfUtc, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<RoutingSummaryDto>> GetRoutingsAsync(int itemId, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<RoutingDto> GetRoutingAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<RoutingDto> CreateRoutingDraftAsync(CreateDraftRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<RoutingDto> SaveRoutingStepsAsync(int routingId, SaveRoutingStepsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<RoutingDto> ApproveRoutingAsync(int routingId, ApproveVersionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<LineDto>> GetLinesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<WorkCenterDto>> GetWorkCentersAsync(bool? isActive = null, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkCenterDto> CreateWorkCenterAsync(CreateWorkCenterRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<WorkCenterDto> UpdateWorkCenterAsync(int id, UpdateWorkCenterRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task DeactivateWorkCenterAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<MachineDto>> GetMachinesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<MachineDto> CreateMachineAsync(CreateMachineRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<MachineDto> UpdateMachineAsync(int id, UpdateMachineRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task DeactivateMachineAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<ShiftDto> CreateShiftAsync(CreateShiftRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<ShiftDto> UpdateShiftAsync(int id, UpdateShiftRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task DeactivateShiftAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<DefectCodeDto> CreateDefectCodeAsync(CreateDefectCodeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<DefectCodeDto> UpdateDefectCodeAsync(int id, UpdateDefectCodeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task DeactivateDefectCodeAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<DowntimeReasonDto>> GetDowntimeReasonsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<DowntimeReasonDto> CreateDowntimeReasonAsync(CreateDowntimeReasonRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<DowntimeReasonDto> UpdateDowntimeReasonAsync(int id, UpdateDowntimeReasonRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task DeactivateDowntimeReasonAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<PlantCalendarDayDto>> GetPlantCalendarAsync(DateOnly from, DateOnly to, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<PlantCalendarDayDto> SetPlantCalendarDayAsync(DateOnly date, SetPlantCalendarDayRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task RemovePlantCalendarDayAsync(DateOnly date, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<byte[]> GetImportTemplateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<ImportResultDto> ImportMasterDataAsync(string fileName, byte[] content, bool dryRun, CancellationToken ct = default) => throw new NotImplementedException();
}
