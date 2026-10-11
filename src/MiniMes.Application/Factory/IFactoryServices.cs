using MiniMes.Contracts.Factory;

namespace MiniMes.Application.Factory;

// Factory configuration (ISA-95: Site -> Area -> Line -> Station). M0 uses plain services;
// M1 moves every use case to MediatR. Errors: KeyNotFoundException -> 404,
// ConflictException (duplicate code) -> 409, DomainException (e.g. inactive parent) -> 422.

public interface ISiteService
{
    Task<IReadOnlyList<SiteDto>> ListAsync(CancellationToken ct);
    Task<SiteDto> GetAsync(int id, CancellationToken ct);
    Task<SiteDto> CreateAsync(CreateSiteRequest request, CancellationToken ct);
    Task<SiteDto> UpdateAsync(int id, UpdateSiteRequest request, CancellationToken ct);
}

public interface IAreaService
{
    Task<IReadOnlyList<AreaDto>> ListAsync(int? siteId, CancellationToken ct);
    Task<AreaDto> GetAsync(int id, CancellationToken ct);
    Task<AreaDto> CreateAsync(CreateAreaRequest request, CancellationToken ct);
    Task<AreaDto> UpdateAsync(int id, UpdateAreaRequest request, CancellationToken ct);
}

public interface ILineService
{
    Task<IReadOnlyList<LineDto>> ListAsync(int? areaId, CancellationToken ct);
    Task<LineDto> GetAsync(int id, CancellationToken ct);
    Task<LineDto> CreateAsync(CreateLineRequest request, CancellationToken ct);
    Task<LineDto> UpdateAsync(int id, UpdateLineRequest request, CancellationToken ct);
}

public interface IStationService
{
    Task<IReadOnlyList<StationDto>> ListAsync(int? lineId, CancellationToken ct);
    Task<StationDto> GetAsync(int id, CancellationToken ct);
    Task<StationDto> CreateAsync(CreateStationRequest request, CancellationToken ct);
    Task<StationDto> UpdateAsync(int id, UpdateStationRequest request, CancellationToken ct);
}
