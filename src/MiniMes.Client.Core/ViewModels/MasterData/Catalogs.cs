using System.Globalization;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.MasterData;

// The small masters on the generic CatalogView. Each class: columns, form fields, API calls.
// Codes are fixed after creation (read-only when editing); "remove" = deactivate, except calendar rows.

public sealed class DefectCodesViewModel(IMasterDataApi api, IDialogService dialogs) : CatalogViewModel(dialogs)
{
    public override string Title => Strings.Menu_DefectCodes;
    public override IReadOnlyList<CatalogColumn> Columns { get; } =
    [
        new(Strings.Item_Code, "Code"), new(Strings.Item_Name, "Name", Width: 260), new(Strings.Catalog_Category, "Category"),
        new(Strings.Common_Active, "IsActive", IsCheckBox: true, Width: 60),
    ];

    protected override async Task<IEnumerable<object>> FetchAsync() => await api.GetDefectCodesAsync();
    protected override Task<IEnumerable<CatalogField>> NewFieldsAsync() => Task.FromResult<IEnumerable<CatalogField>>(
        [Text("Code", Strings.Item_Code), Text("Name", Strings.Item_Name), Text("Category", Strings.Catalog_Category, "Solder")]);
    protected override Task<IEnumerable<CatalogField>> EditFieldsAsync(object row)
    {
        var d = (DefectCodeDto)row;
        return Task.FromResult<IEnumerable<CatalogField>>(
            [Text("Code", Strings.Item_Code, d.Code, readOnly: true), Text("Name", Strings.Item_Name, d.Name), Text("Category", Strings.Catalog_Category, d.Category)]);
    }

    protected override async Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> f)
    {
        if (isNew) await api.CreateDefectCodeAsync(new(f["Code"].Text.Trim().ToUpperInvariant(), f["Name"].Text.Trim(), f["Category"].Text.Trim()));
        else await api.UpdateDefectCodeAsync(((DefectCodeDto)row!).Id, new(f["Name"].Text.Trim(), f["Category"].Text.Trim()));
        return true;
    }

    protected override Task RemoveRowAsync(object row) => api.DeactivateDefectCodeAsync(((DefectCodeDto)row).Id);
    protected override string Describe(object row) => ((DefectCodeDto)row).Code;
    protected override bool CanRemove(object row) => ((DefectCodeDto)row).IsActive;
}

public sealed class DowntimeReasonsViewModel(IMasterDataApi api, IDialogService dialogs) : CatalogViewModel(dialogs)
{
    private static readonly string[] Categories = ["Planned", "Unplanned"];

    public override string Title => Strings.Menu_DowntimeReasons;
    public override string? Hint => Strings.Downtime_Hint;
    public override IReadOnlyList<CatalogColumn> Columns { get; } =
    [
        new(Strings.Item_Code, "Code"), new(Strings.Item_Name, "Name", Width: 240), new(Strings.Catalog_Category, "Category", Width: 90),
        new(Strings.Downtime_Counts, "CountsAgainstAvailability", IsCheckBox: true, Width: 140),
        new(Strings.Common_Active, "IsActive", IsCheckBox: true, Width: 60),
    ];

    protected override async Task<IEnumerable<object>> FetchAsync() => await api.GetDowntimeReasonsAsync();
    protected override Task<IEnumerable<CatalogField>> NewFieldsAsync() => Task.FromResult<IEnumerable<CatalogField>>(
    [
        Text("Code", Strings.Item_Code), Text("Name", Strings.Item_Name), Choice("Category", Strings.Catalog_Category, Categories, "Unplanned"),
        Bool("CountsAgainstAvailability", Strings.Downtime_Counts, true),
    ]);
    protected override Task<IEnumerable<CatalogField>> EditFieldsAsync(object row)
    {
        var r = (DowntimeReasonDto)row;
        return Task.FromResult<IEnumerable<CatalogField>>(
        [
            Text("Code", Strings.Item_Code, r.Code, readOnly: true), Text("Name", Strings.Item_Name, r.Name),
            Choice("Category", Strings.Catalog_Category, Categories, r.Category), Bool("CountsAgainstAvailability", Strings.Downtime_Counts, r.CountsAgainstAvailability),
        ]);
    }

    protected override async Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> f)
    {
        var (name, category, counts) = (f["Name"].Text.Trim(), f["Category"].Text, f["CountsAgainstAvailability"].Flag);
        if (isNew) await api.CreateDowntimeReasonAsync(new(f["Code"].Text.Trim().ToUpperInvariant(), name, category, counts));
        else await api.UpdateDowntimeReasonAsync(((DowntimeReasonDto)row!).Id, new(name, category, counts));
        return true;
    }

    protected override Task RemoveRowAsync(object row) => api.DeactivateDowntimeReasonAsync(((DowntimeReasonDto)row).Id);
    protected override string Describe(object row) => ((DowntimeReasonDto)row).Code;
    protected override bool CanRemove(object row) => ((DowntimeReasonDto)row).IsActive;
}

public sealed class WorkCentersViewModel(IMasterDataApi api, IDialogService dialogs) : CatalogViewModel(dialogs)
{
    public override string Title => Strings.Menu_WorkCenters;
    public override IReadOnlyList<CatalogColumn> Columns { get; } =
    [
        new(Strings.Item_Code, "Code"), new(Strings.Item_Name, "Name", Width: 220), new(Strings.Item_Type, "Type", Width: 80),
        new(Strings.Common_Active, "IsActive", IsCheckBox: true, Width: 60),
    ];

    protected override async Task<IEnumerable<object>> FetchAsync() => await api.GetWorkCentersAsync();

    protected override async Task<IEnumerable<CatalogField>> NewFieldsAsync()
    {
        var lines = (await api.GetLinesAsync()).Where(l => l.IsActive).Select(l => l.Code).ToList();
        return [Choice("LineId", Strings.Catalog_Line, lines), Text("Code", Strings.Item_Code), Text("Name", Strings.Item_Name),
            Text("Type", Strings.Item_Type, "SMT", hint: Strings.WorkCenter_TypeHint)];
    }

    protected override Task<IEnumerable<CatalogField>> EditFieldsAsync(object row)
    {
        var w = (WorkCenterDto)row;
        return Task.FromResult<IEnumerable<CatalogField>>(
            [Text("Code", Strings.Item_Code, w.Code, readOnly: true), Text("Name", Strings.Item_Name, w.Name), Text("Type", Strings.Item_Type, w.Type)]);
    }

    protected override async Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> f)
    {
        var (name, type) = (f["Name"].Text.Trim(), f["Type"].Text.Trim().ToUpperInvariant());
        if (isNew)
        {
            var line = (await api.GetLinesAsync()).Single(l => l.Code == f["LineId"].Text);
            await api.CreateWorkCenterAsync(new(line.Id, f["Code"].Text.Trim().ToUpperInvariant(), name, type));
        }
        else await api.UpdateWorkCenterAsync(((WorkCenterDto)row!).Id, new(name, type));
        return true;
    }

    protected override Task RemoveRowAsync(object row) => api.DeactivateWorkCenterAsync(((WorkCenterDto)row).Id);
    protected override string Describe(object row) => ((WorkCenterDto)row).Code;
    protected override bool CanRemove(object row) => ((WorkCenterDto)row).IsActive;
}

public sealed class MachinesViewModel(IMasterDataApi api, IDialogService dialogs) : CatalogViewModel(dialogs)
{
    private static readonly string[] DataSources = ["Mqtt", "OpcUa", "Modbus"];

    public override string Title => Strings.Menu_Machines;
    public override string? Hint => Strings.Machine_Hint;
    public override IReadOnlyList<CatalogColumn> Columns { get; } =
    [
        new(Strings.Item_Code, "Code"), new(Strings.Item_Name, "Name", Width: 180), new(Strings.Item_Type, "Type"),
        new(Strings.Machine_TopicKey, "MqttTopicKey"), new(Strings.Machine_DataSource, "DataSource", Width: 80),
        new(Strings.Common_Active, "IsActive", IsCheckBox: true, Width: 60),
    ];

    protected override async Task<IEnumerable<object>> FetchAsync() => await api.GetMachinesAsync();

    protected override async Task<IEnumerable<CatalogField>> NewFieldsAsync()
    {
        var workCenters = (await api.GetWorkCentersAsync(isActive: true)).Select(w => w.Code).ToList();
        return [Choice("WorkCenterId", Strings.Routing_WorkCenter, workCenters), Text("Code", Strings.Item_Code), Text("Name", Strings.Item_Name),
            Text("Type", Strings.Item_Type, "PickAndPlace"), Text("MqttTopicKey", Strings.Machine_TopicKey, hint: Strings.Machine_TopicKeyHint),
            Choice("DataSource", Strings.Machine_DataSource, DataSources)];
    }

    protected override Task<IEnumerable<CatalogField>> EditFieldsAsync(object row)
    {
        var m = (MachineDto)row;
        return Task.FromResult<IEnumerable<CatalogField>>(
        [
            Text("Code", Strings.Item_Code, m.Code, readOnly: true), Text("MqttTopicKey", Strings.Machine_TopicKey, m.MqttTopicKey, readOnly: true),
            Text("Name", Strings.Item_Name, m.Name), Text("Type", Strings.Item_Type, m.Type), Choice("DataSource", Strings.Machine_DataSource, DataSources, m.DataSource),
        ]);
    }

    protected override async Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> f)
    {
        var (name, type, source) = (f["Name"].Text.Trim(), f["Type"].Text.Trim(), f["DataSource"].Text);
        if (isNew)
        {
            var wc = (await api.GetWorkCentersAsync(isActive: true)).Single(w => w.Code == f["WorkCenterId"].Text);
            await api.CreateMachineAsync(new(wc.Id, f["Code"].Text.Trim().ToUpperInvariant(), name, type, f["MqttTopicKey"].Text.Trim(), source));
        }
        else await api.UpdateMachineAsync(((MachineDto)row!).Id, new(name, type, source));
        return true;
    }

    protected override Task RemoveRowAsync(object row) => api.DeactivateMachineAsync(((MachineDto)row).Id);
    protected override string Describe(object row) => ((MachineDto)row).Code;
    protected override bool CanRemove(object row) => ((MachineDto)row).IsActive;
}

// Shift: days as "Mon,Tue,...", breaks as "10:00-10:15, 11:30-12:15". The rules (inside the shift, no
// overlap, total < length) stay on the server: its 400 lands on the Breaks box.
public sealed class ShiftsViewModel(IMasterDataApi api, IDialogService dialogs) : CatalogViewModel(dialogs)
{
    public override string Title => Strings.Menu_Shifts;
    public override string? Hint => Strings.Shift_Hint;
    public override IReadOnlyList<CatalogColumn> Columns { get; } =
    [
        new(Strings.Item_Code, "Code", Width: 60), new(Strings.Item_Name, "Name"), new(Strings.Shift_Start, "StartTime", Width: 70, Format: "HH:mm"),
        new(Strings.Shift_End, "EndTime", Width: 70, Format: "HH:mm"), new(Strings.Shift_Planned, "PlannedProductionMinutes", Width: 110),
        new(Strings.Shift_CrossesMidnight, "CrossesMidnight", IsCheckBox: true, Width: 90),
        new(Strings.Common_Active, "IsActive", IsCheckBox: true, Width: 60),
    ];

    protected override async Task<IEnumerable<object>> FetchAsync() => await api.GetShiftsAsync();

    protected override Task<IEnumerable<CatalogField>> NewFieldsAsync() => Task.FromResult(Form(null));
    protected override Task<IEnumerable<CatalogField>> EditFieldsAsync(object row) => Task.FromResult(Form((ShiftDto)row));

    private static IEnumerable<CatalogField> Form(ShiftDto? s) =>
    [
        Text("Code", Strings.Item_Code, s?.Code ?? "", readOnly: s is not null), Text("Name", Strings.Item_Name, s?.Name ?? ""),
        Text("StartTime", Strings.Shift_Start, s?.StartTime.ToString("HH:mm") ?? "06:00"),
        Text("EndTime", Strings.Shift_End, s?.EndTime.ToString("HH:mm") ?? "14:00"),
        Text("Days", Strings.Shift_Days, s is null ? "Mon,Tue,Wed,Thu,Fri,Sat" : string.Join(",", s.Days)),
        Text("Breaks", Strings.Shift_Breaks, s is null ? "" : string.Join(", ", s.Breaks.Select(b => $"{b.StartTime:HH:mm}-{b.EndTime:HH:mm}")),
            hint: Strings.Shift_BreaksHint),
    ];

    protected override async Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> f)
    {
        var ok = TryTime(f["StartTime"], out var start) & TryTime(f["EndTime"], out var end) & TryBreaks(f["Breaks"], out var breaks);
        if (!ok) return false;
        var days = f["Days"].Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (isNew) await api.CreateShiftAsync(new(f["Code"].Text.Trim().ToUpperInvariant(), f["Name"].Text.Trim(), start, end, days, breaks));
        else await api.UpdateShiftAsync(((ShiftDto)row!).Id, new(f["Name"].Text.Trim(), start, end, days, breaks));
        return true;
    }

    public static bool TryParseTime(string text, out TimeOnly time)
        => TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // "10:00-10:15, 11:30-12:15" -> breaks; empty = no breaks.
    public static bool TryParseBreaks(string text, out List<ShiftBreakDto> breaks)
    {
        breaks = [];
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length != 2 || !TryParseTime(ends[0], out var a) || !TryParseTime(ends[1], out var b)) return false;
            breaks.Add(new ShiftBreakDto(a, b));
        }
        return true;
    }

    private static bool TryTime(CatalogField field, out TimeOnly time)
    {
        if (TryParseTime(field.Text, out time)) return true;
        field.Error = Strings.Shift_TimeFormat;
        return false;
    }

    private static bool TryBreaks(CatalogField field, out List<ShiftBreakDto> breaks)
    {
        if (TryParseBreaks(field.Text, out breaks)) return true;
        field.Error = Strings.Shift_BreaksHint;
        return false;
    }

    protected override Task RemoveRowAsync(object row) => api.DeactivateShiftAsync(((ShiftDto)row).Id);
    protected override string Describe(object row) => ((ShiftDto)row).Code;
    protected override bool CanRemove(object row) => ((ShiftDto)row).IsActive;
}

// [v2] Holidays and overtime days of this year and next. A row is an exception, not history:
// "remove" really deletes it (the audit log keeps what it was) and the weekly pattern applies again.
public sealed class PlantCalendarViewModel(IMasterDataApi api, IDialogService dialogs, TimeProvider time) : CatalogViewModel(dialogs)
{
    public override string Title => Strings.Menu_PlantCalendar;
    public override string? Hint => Strings.Calendar_Hint;
    public override string RemoveLabel => Strings.Common_Remove;
    protected override string RemoveConfirmFormat => Strings.Calendar_ConfirmRemove;
    public override IReadOnlyList<CatalogColumn> Columns { get; } =
    [
        new(Strings.Calendar_Date, "Date", Width: 120, Format: "yyyy-MM-dd"), new(Strings.Calendar_WorkingDay, "IsWorkingDay", IsCheckBox: true, Width: 90),
        new(Strings.Calendar_ShiftCodes, "ShiftCodes", Width: 100), new(Strings.Calendar_Note, "Note", Width: 260),
    ];

    protected override async Task<IEnumerable<object>> FetchAsync()
    {
        // The API returns at most one year per call; next year matters too (Tet is often in January/February).
        var year = time.GetLocalNow().Year;
        var thisYear = await api.GetPlantCalendarAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        var nextYear = await api.GetPlantCalendarAsync(new DateOnly(year + 1, 1, 1), new DateOnly(year + 1, 12, 31));
        return thisYear.Concat(nextYear);
    }

    protected override Task<IEnumerable<CatalogField>> NewFieldsAsync() => Task.FromResult(Form(null));
    protected override Task<IEnumerable<CatalogField>> EditFieldsAsync(object row) => Task.FromResult(Form((PlantCalendarDayDto)row));

    private IEnumerable<CatalogField> Form(PlantCalendarDayDto? d) =>
    [
        Text("Date", Strings.Calendar_Date, (d?.Date ?? DateOnly.FromDateTime(time.GetLocalNow().Date)).ToString("yyyy-MM-dd"), readOnly: d is not null),
        Bool("IsWorkingDay", Strings.Calendar_WorkingDay, d?.IsWorkingDay ?? false),
        Text("ShiftCodes", Strings.Calendar_ShiftCodes, d?.ShiftCodes ?? "", hint: Strings.Calendar_ShiftCodesHint),
        Text("Note", Strings.Calendar_Note, d?.Note ?? ""),
    ];

    protected override async Task<bool> SaveRowAsync(bool isNew, object? row, IReadOnlyDictionary<string, CatalogField> f)
    {
        if (!DateOnly.TryParseExact(f["Date"].Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            f["Date"].Error = Strings.Calendar_DateFormat;
            return false;
        }
        await api.SetPlantCalendarDayAsync(date, new(f["IsWorkingDay"].Flag, NullIfBlank(f["ShiftCodes"].Text), NullIfBlank(f["Note"].Text)));
        return true;
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    protected override Task RemoveRowAsync(object row) => api.RemovePlantCalendarDayAsync(((PlantCalendarDayDto)row).Date);
    protected override string Describe(object row) => ((PlantCalendarDayDto)row).Date.ToString("yyyy-MM-dd");
    protected override bool CanRemove(object row) => true;
}
