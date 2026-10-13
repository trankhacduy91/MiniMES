using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.MasterData.Items;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.Application.MasterData.Import;

// [v2] Spec M1 2.10 / rules 15-16: check the WHOLE file first; one error = nothing written. Dry run and
// commit run the same checks (data may change between the two calls). New items, BOM and routing
// versions are created; BOM/Routing always as Draft (import is a shortcut for typing, not for approving).
public sealed record ImportMasterDataCommand(Stream File, bool DryRun) : IRequest<ImportResultDto>;

public sealed class ImportMasterDataHandler(
    IAppDbContext db,
    IValidator<CreateItemCommand> itemValidator,
    IValidator<BomLineInput> lineValidator,
    IValidator<RoutingStepInput> stepValidator) : IRequestHandler<ImportMasterDataCommand, ImportResultDto>
{
    private const string Items = ExcelMasterDataReader.ItemsSheet;
    private const string BomLines = ExcelMasterDataReader.BomLinesSheet;
    private const string Steps = ExcelMasterDataReader.RoutingStepsSheet;

    // An item as the import sees it: already in the DB (Id > 0) or a new row of the file (Id < 0, temporary).
    private sealed record KnownItem(int Id, string Code, ItemType? Type, bool IsActive);

    public async Task<ImportResultDto> Handle(ImportMasterDataCommand cmd, CancellationToken ct)
    {
        var errors = new List<ImportErrorDto>();
        var file = ExcelMasterDataReader.Read(cmd.File, errors);

        var items = await ValidateItemsAsync(file, errors, ct);
        await ValidateBomLinesAsync(file, items, errors, ct);
        var workCenters = await ValidateRoutingStepsAsync(file, items, errors, ct);

        var bomCount = file.BomLines.Select(l => l.ParentCode).Distinct().Count();
        var routingCount = file.RoutingSteps.Select(s => s.ItemCode).Distinct().Count();
        var sorted = errors.OrderBy(e => SheetOrder(e.Sheet)).ThenBy(e => e.Row).ToList();
        if (sorted.Count > 0 || cmd.DryRun)
            return new ImportResultDto(cmd.DryRun, false, file.Items.Count, bomCount, routingCount, sorted);

        await CommitAsync(file, items, workCenters, ct);
        return new ImportResultDto(false, true, file.Items.Count, bomCount, routingCount, []);
    }

    // ---------- Items ----------

    private async Task<Dictionary<string, KnownItem>> ValidateItemsAsync(MasterDataFile file, List<ImportErrorDto> errors, CancellationToken ct)
    {
        var known = new Dictionary<string, KnownItem>();
        var tempId = 0;
        foreach (var row in file.Items)
        {
            if (known.ContainsKey(row.Code))
            {
                errors.Add(new(Items, row.Row, "Code", $"Code '{row.Code}' appears twice in the file."));
                continue;
            }
            // The same validator as POST /api/items: one set of rules for both paths.
            var result = itemValidator.Validate(ToCommand(row));
            errors.AddRange(result.Errors.Select(e => new ImportErrorDto(Items, row.Row, e.PropertyName, e.ErrorMessage)));
            known[row.Code] = new KnownItem(--tempId, row.Code,
                Enum.TryParse<ItemType>(row.Type, out var type) ? type : null, IsActive: true);
        }

        var fileCodes = known.Keys.ToList();
        var existing = await db.Items.AsNoTracking().Where(i => fileCodes.Contains(i.Code)).Select(i => i.Code).ToListAsync(ct);
        foreach (var row in file.Items.Where(r => existing.Contains(r.Code)))
            errors.Add(new(Items, row.Row, "Code", $"Code '{row.Code}' already exists."));

        // Items the BOM/routing sheets refer to but the Items sheet does not define: look them up in the DB.
        var referenced = file.BomLines.SelectMany(l => new[] { l.ParentCode, l.ComponentCode })
            .Concat(file.RoutingSteps.Select(s => s.ItemCode))
            .Where(c => !known.ContainsKey(c)).Distinct().ToList();
        var fromDb = await db.Items.AsNoTracking().Where(i => referenced.Contains(i.Code))
            .Select(i => new KnownItem(i.Id, i.Code, i.Type, i.IsActive)).ToListAsync(ct);
        foreach (var i in fromDb) known[i.Code] = i;
        return known;
    }

    private static CreateItemCommand ToCommand(ItemRow r)
        => new(r.Code, r.Name, r.Type, r.Uom, r.IsSerialized, r.IsLotControlled, r.SkipIqc);

    // ---------- BOM lines ----------

    private async Task ValidateBomLinesAsync(MasterDataFile file, Dictionary<string, KnownItem> items,
        List<ImportErrorDto> errors, CancellationToken ct)
    {
        foreach (var line in file.BomLines)
        {
            var result = lineValidator.Validate(new BomLineInput(1, line.QtyPer, line.ScrapRate, line.OperationSeq));
            errors.AddRange(result.Errors.Select(e => new ImportErrorDto(BomLines, line.Row, e.PropertyName, e.ErrorMessage)));

            CheckParent(items, line.ParentCode, "BOM", BomLines, line.Row, "ParentCode", errors);
            if (!items.TryGetValue(line.ComponentCode, out var component))
                errors.Add(new(BomLines, line.Row, "ComponentCode", $"Item '{line.ComponentCode}' is neither in the Items sheet nor in the database."));
            else if (!component.IsActive)
                errors.Add(new(BomLines, line.Row, "ComponentCode", $"Item '{line.ComponentCode}' is inactive."));
            if (line.ComponentCode == line.ParentCode)
                errors.Add(new(BomLines, line.Row, "ComponentCode", "A BOM line cannot use the parent item itself."));
        }
        foreach (var dup in file.BomLines.GroupBy(l => (l.ParentCode, l.ComponentCode)).Where(g => g.Count() > 1))
            errors.Add(new(BomLines, dup.Skip(1).First().Row, "ComponentCode",
                $"'{dup.Key.ComponentCode}' appears twice under '{dup.Key.ParentCode}'; use one line with the total QtyPer."));

        await CheckOpenDraftsAsync(file.BomLines.Select(l => (l.ParentCode, l.Row)), items,
            ids => db.Boms.Where(b => ids.Contains(b.ItemId) && b.Status == VersionStatus.Draft).Select(b => b.ItemId),
            BomLines, "ParentCode", "BOM", errors, ct);
        await CheckCycleAsync(file, items, errors, ct);
    }

    // Spec M1 2.4 on the combined graph: current versions in the DB + the BOMs of the file.
    private async Task CheckCycleAsync(MasterDataFile file, Dictionary<string, KnownItem> items,
        List<ImportErrorDto> errors, CancellationToken ct)
    {
        var lines = file.BomLines.Where(l => items.ContainsKey(l.ParentCode) && items.ContainsKey(l.ComponentCode)).ToList();
        var fileParents = lines.Select(l => items[l.ParentCode].Id).ToHashSet();

        var dbEdges = await db.Boms.AsNoTracking()
            .Where(b => b.EffectiveTo == null)
            .SelectMany(b => b.Lines.Select(l => new { b.ItemId, l.ComponentItemId }))
            .ToListAsync(ct);
        var graph = dbEdges.Concat(lines.Select(l => new { ItemId = items[l.ParentCode].Id, ComponentItemId = items[l.ComponentCode].Id }))
            .GroupBy(e => e.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ComponentItemId).Distinct().ToList());

        if (BomCycleDetector.FindCycle(graph) is not { } cycle || !cycle.Any(fileParents.Contains)) return;

        var dbIds = cycle.Where(id => id > 0).ToList();
        var codes = items.Values.Where(i => i.Id < 0).ToDictionary(i => i.Id, i => i.Code);
        foreach (var i in await db.Items.AsNoTracking().Where(i => dbIds.Contains(i.Id)).ToListAsync(ct))
            codes[i.Id] = i.Code;
        var path = string.Join(" -> ", cycle.Select(id => codes[id]));
        var firstRow = lines.First(l => cycle.Contains(items[l.ParentCode].Id)).Row;
        errors.Add(new(BomLines, firstRow, "ComponentCode", $"These lines would create a BOM cycle: {path}."));
    }

    // ---------- Routing steps ----------

    private async Task<Dictionary<string, (int Id, bool IsActive)>> ValidateRoutingStepsAsync(MasterDataFile file,
        Dictionary<string, KnownItem> items, List<ImportErrorDto> errors, CancellationToken ct)
    {
        var wcCodes = file.RoutingSteps.Select(s => s.WorkCenterCode).Distinct().ToList();
        var workCenters = await db.WorkCenters.AsNoTracking().Where(w => wcCodes.Contains(w.Code))
            .ToDictionaryAsync(w => w.Code, w => (w.Id, w.IsActive), ct);

        foreach (var step in file.RoutingSteps)
        {
            var result = stepValidator.Validate(new RoutingStepInput(step.Seq, step.OperationCode, 1,
                step.IdealCycleTimeSec, step.IsInspection, step.IsMandatory, step.SopUrl));
            errors.AddRange(result.Errors.Select(e => new ImportErrorDto(Steps, step.Row, e.PropertyName, e.ErrorMessage)));

            CheckParent(items, step.ItemCode, "routing", Steps, step.Row, "ItemCode", errors);
            if (!workCenters.TryGetValue(step.WorkCenterCode, out var wc))
                errors.Add(new(Steps, step.Row, "WorkCenterCode", $"Work center '{step.WorkCenterCode}' does not exist."));
            else if (!wc.IsActive)
                errors.Add(new(Steps, step.Row, "WorkCenterCode", $"Work center '{step.WorkCenterCode}' is inactive."));
        }
        foreach (var dup in file.RoutingSteps.GroupBy(s => (s.ItemCode, s.Seq)).Where(g => g.Count() > 1))
            errors.Add(new(Steps, dup.Skip(1).First().Row, "Seq", $"Seq {dup.Key.Seq} appears twice for '{dup.Key.ItemCode}'."));

        await CheckOpenDraftsAsync(file.RoutingSteps.Select(s => (s.ItemCode, s.Row)), items,
            ids => db.Routings.Where(r => ids.Contains(r.ItemId) && r.Status == VersionStatus.Draft).Select(r => r.ItemId),
            Steps, "ItemCode", "routing", errors, ct);
        return workCenters;
    }

    // ---------- shared checks ----------

    // Only Semi/Finished items, active, have a BOM or routing (as in the API, step 5).
    private static void CheckParent(Dictionary<string, KnownItem> items, string code, string what, string sheet, int row,
        string column, List<ImportErrorDto> errors)
    {
        if (!items.TryGetValue(code, out var item))
            errors.Add(new(sheet, row, column, $"Item '{code}' is neither in the Items sheet nor in the database."));
        else if (!item.IsActive)
            errors.Add(new(sheet, row, column, $"Item '{code}' is inactive."));
        else if (item.Type == ItemType.Raw)
            errors.Add(new(sheet, row, column, $"Item '{code}' is a Raw material; only Semi/Finished items have a {what}."));
    }

    // One Draft per item (step 5): an existing item that already has one must be finished in the editor first.
    private static async Task CheckOpenDraftsAsync(IEnumerable<(string Code, int Row)> parents, Dictionary<string, KnownItem> items,
        Func<List<int>, IQueryable<int>> draftsOf, string sheet, string column, string what,
        List<ImportErrorDto> errors, CancellationToken ct)
    {
        var firstRows = parents.GroupBy(p => p.Code).ToDictionary(g => g.Key, g => g.First().Row);
        var dbIds = firstRows.Keys.Where(items.ContainsKey).Select(c => items[c].Id).Where(id => id > 0).ToList();
        if (dbIds.Count == 0) return;

        var withDraft = await draftsOf(dbIds).ToListAsync(ct);
        foreach (var (code, row) in firstRows.Where(p => items.TryGetValue(p.Key, out var i) && withDraft.Contains(i.Id)))
            errors.Add(new(sheet, row, column, $"Item '{code}' already has a Draft {what}; finish or approve it before importing."));
    }

    private static int SheetOrder(string sheet) => sheet switch { "File" => 0, Items => 1, BomLines => 2, _ => 3 };

    // ---------- commit: one transaction, all or nothing ----------

    private async Task CommitAsync(MasterDataFile file, Dictionary<string, KnownItem> items,
        Dictionary<string, (int Id, bool IsActive)> workCenters, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);

        // 1st save: the new items get their ids, which the BOM lines need.
        var created = file.Items.Select(r => Item.Create(r.Code, r.Name, Enum.Parse<ItemType>(r.Type), r.Uom,
            r.IsSerialized, r.IsLotControlled, r.SkipIqc)).ToList();
        db.Items.AddRange(created);
        await db.SaveChangesAsync(ct);
        var idOf = items.Values.Where(i => i.Id > 0).ToDictionary(i => i.Code, i => i.Id);
        foreach (var i in created) idOf[i.Code] = i.Id;

        // 2nd save: BOM and routing drafts, version = max + 1 for items that already had versions.
        foreach (var group in file.BomLines.GroupBy(l => l.ParentCode))
        {
            var itemId = idOf[group.Key];
            var version = await db.Boms.Where(b => b.ItemId == itemId).MaxAsync(b => (int?)b.Version, ct) ?? 0;
            var bom = Bom.CreateDraft(itemId, version + 1);
            foreach (var l in group) bom.AddLine(idOf[l.ComponentCode], l.QtyPer, l.ScrapRate, l.OperationSeq);
            db.Boms.Add(bom);
        }
        foreach (var group in file.RoutingSteps.GroupBy(s => s.ItemCode))
        {
            var itemId = idOf[group.Key];
            var version = await db.Routings.Where(r => r.ItemId == itemId).MaxAsync(r => (int?)r.Version, ct) ?? 0;
            var routing = Routing.CreateDraft(itemId, version + 1);
            foreach (var s in group.OrderBy(s => s.Seq))
                routing.AddStep(s.Seq, s.OperationCode, workCenters[s.WorkCenterCode].Id, s.IdealCycleTimeSec,
                    s.IsInspection, s.IsMandatory, s.SopUrl);
            db.Routings.Add(routing);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct); // any exception before this line rolls back both saves
    }
}
