using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Factory;

internal static class FactorySave
{
    // The "already exists" pre-check gives a friendly message, but two requests can still race:
    // the unique index is the real guard, so its violation is turned into the same 409.
    public static async Task SaveAsync(AppDbContext db, string duplicateMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ConflictException(duplicateMessage);
        }
    }
}

public sealed class SiteService(AppDbContext db) : ISiteService
{
    public async Task<IReadOnlyList<SiteDto>> ListAsync(CancellationToken ct)
        => await db.Sites.AsNoTracking().OrderBy(x => x.Code).Select(x => ToDto(x)).ToListAsync(ct);

    public async Task<SiteDto> GetAsync(int id, CancellationToken ct) => ToDto(await FindAsync(id, ct));

    public async Task<SiteDto> CreateAsync(CreateSiteRequest request, CancellationToken ct)
    {
        var duplicate = $"Site '{request.Code}' already exists.";
        if (await db.Sites.AnyAsync(x => x.Code == request.Code, ct)) throw new ConflictException(duplicate);

        var site = new Site(request.Code, request.Name);
        db.Sites.Add(site);
        await FactorySave.SaveAsync(db, duplicate, ct);
        return ToDto(site);
    }

    public async Task<SiteDto> UpdateAsync(int id, UpdateSiteRequest request, CancellationToken ct)
    {
        var site = await FindAsync(id, ct);
        site.Rename(request.Name);
        site.SetActive(request.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(site);
    }

    private async Task<Site> FindAsync(int id, CancellationToken ct)
        => await db.Sites.SingleOrDefaultAsync(x => x.Id == id, ct)
           ?? throw new KeyNotFoundException($"Site {id} not found.");

    private static SiteDto ToDto(Site x) => new(x.Id, x.Code, x.Name, x.IsActive);
}

public sealed class AreaService(AppDbContext db) : IAreaService
{
    public async Task<IReadOnlyList<AreaDto>> ListAsync(int? siteId, CancellationToken ct)
        => await db.Areas.AsNoTracking()
            .Where(x => siteId == null || x.SiteId == siteId)
            .OrderBy(x => x.Code).Select(x => ToDto(x)).ToListAsync(ct);

    public async Task<AreaDto> GetAsync(int id, CancellationToken ct) => ToDto(await FindAsync(id, ct));

    public async Task<AreaDto> CreateAsync(CreateAreaRequest request, CancellationToken ct)
    {
        var site = await db.Sites.SingleOrDefaultAsync(x => x.Id == request.SiteId, ct)
                   ?? throw new KeyNotFoundException($"Site {request.SiteId} not found.");

        var duplicate = $"Area '{request.Code}' already exists in site '{site.Code}'.";
        if (await db.Areas.AnyAsync(x => x.SiteId == site.Id && x.Code == request.Code, ct))
            throw new ConflictException(duplicate);

        var area = site.AddArea(request.Code, request.Name); // Domain rejects an inactive site
        await FactorySave.SaveAsync(db, duplicate, ct);
        return ToDto(area);
    }

    public async Task<AreaDto> UpdateAsync(int id, UpdateAreaRequest request, CancellationToken ct)
    {
        var area = await FindAsync(id, ct);
        area.Rename(request.Name);
        area.SetActive(request.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(area);
    }

    private async Task<Area> FindAsync(int id, CancellationToken ct)
        => await db.Areas.SingleOrDefaultAsync(x => x.Id == id, ct)
           ?? throw new KeyNotFoundException($"Area {id} not found.");

    private static AreaDto ToDto(Area x) => new(x.Id, x.SiteId, x.Code, x.Name, x.IsActive);
}

public sealed class LineService(AppDbContext db) : ILineService
{
    public async Task<IReadOnlyList<LineDto>> ListAsync(int? areaId, CancellationToken ct)
        => await db.Lines.AsNoTracking()
            .Where(x => areaId == null || x.AreaId == areaId)
            .OrderBy(x => x.Code).Select(x => ToDto(x)).ToListAsync(ct);

    public async Task<LineDto> GetAsync(int id, CancellationToken ct) => ToDto(await FindAsync(id, ct));

    public async Task<LineDto> CreateAsync(CreateLineRequest request, CancellationToken ct)
    {
        var area = await db.Areas.SingleOrDefaultAsync(x => x.Id == request.AreaId, ct)
                   ?? throw new KeyNotFoundException($"Area {request.AreaId} not found.");

        var duplicate = $"Line '{request.Code}' already exists in area '{area.Code}'.";
        if (await db.Lines.AnyAsync(x => x.AreaId == area.Id && x.Code == request.Code, ct))
            throw new ConflictException(duplicate);

        var line = area.AddLine(request.Code, request.Name);
        await FactorySave.SaveAsync(db, duplicate, ct);
        return ToDto(line);
    }

    public async Task<LineDto> UpdateAsync(int id, UpdateLineRequest request, CancellationToken ct)
    {
        var line = await FindAsync(id, ct);
        line.Rename(request.Name);
        line.SetActive(request.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(line);
    }

    private async Task<Line> FindAsync(int id, CancellationToken ct)
        => await db.Lines.SingleOrDefaultAsync(x => x.Id == id, ct)
           ?? throw new KeyNotFoundException($"Line {id} not found.");

    private static LineDto ToDto(Line x) => new(x.Id, x.AreaId, x.Code, x.Name, x.IsActive);
}

public sealed class StationService(AppDbContext db) : IStationService
{
    public async Task<IReadOnlyList<StationDto>> ListAsync(int? lineId, CancellationToken ct)
        => await db.Stations.AsNoTracking()
            .Where(x => lineId == null || x.LineId == lineId)
            .OrderBy(x => x.Code).Select(x => ToDto(x)).ToListAsync(ct);

    public async Task<StationDto> GetAsync(int id, CancellationToken ct) => ToDto(await FindAsync(id, ct));

    public async Task<StationDto> CreateAsync(CreateStationRequest request, CancellationToken ct)
    {
        var line = await db.Lines.SingleOrDefaultAsync(x => x.Id == request.LineId, ct)
                   ?? throw new KeyNotFoundException($"Line {request.LineId} not found.");

        // Station code is unique across the whole system (scans reference it directly).
        var duplicate = $"Station '{request.Code}' already exists.";
        if (await db.Stations.AnyAsync(x => x.Code == request.Code, ct)) throw new ConflictException(duplicate);

        var station = line.AddStation(request.Code, request.Name, Enum.Parse<StationType>(request.Type));
        await FactorySave.SaveAsync(db, duplicate, ct);
        return ToDto(station);
    }

    public async Task<StationDto> UpdateAsync(int id, UpdateStationRequest request, CancellationToken ct)
    {
        var station = await FindAsync(id, ct);
        station.Rename(request.Name);
        station.ChangeType(Enum.Parse<StationType>(request.Type));
        station.SetActive(request.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(station);
    }

    private async Task<Station> FindAsync(int id, CancellationToken ct)
        => await db.Stations.SingleOrDefaultAsync(x => x.Id == id, ct)
           ?? throw new KeyNotFoundException($"Station {id} not found.");

    private static StationDto ToDto(Station x) => new(x.Id, x.LineId, x.Code, x.Name, x.Type.ToString(), x.IsActive);
}
