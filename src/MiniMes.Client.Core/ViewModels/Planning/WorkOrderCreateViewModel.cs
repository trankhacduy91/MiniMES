using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Planning;

namespace MiniMes.Client.Core.ViewModels.Planning;

// New work order (spec M2 step 8, "form tạo WO với preview NVL"). The planner picks item, line, quantity and
// the planned window in plant time; Preview shows what Release would freeze (materials per step, routing),
// computed by the server from the master data in effect now. Save creates a Draft.
public partial class WorkOrderCreateViewModel(IWorkOrderApi api, IMasterDataApi masterData, IPageNavigator pages, TimeProvider time)
    : ObservableObject
{
    public const string TimeFormat = "HH:mm";

    public ObservableCollection<ItemDto> ItemOptions { get; } = [];
    public ObservableCollection<LineOption> LineOptions { get; } = [];
    public ObservableCollection<WorkOrderMaterialDto> Materials { get; } = [];
    public ObservableCollection<WorkOrderOperationDto> Operations { get; } = [];
    public IReadOnlyList<int> PriorityOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    public FieldErrors FieldErrors { get; } = new();

    [ObservableProperty]
    private ItemDto? _item;

    [ObservableProperty]
    private LineOption? _line;

    [ObservableProperty]
    private string _plannedQtyText = "200";

    [ObservableProperty]
    private DateTime _startDate;

    [ObservableProperty]
    private string _startTimeText = "08:00";

    [ObservableProperty]
    private DateTime _endDate;

    [ObservableProperty]
    private string _endTimeText = "12:00";

    [ObservableProperty]
    private int _priority = 5;

    [ObservableProperty]
    private bool _requireFai = true;

    [ObservableProperty]
    private string _previewInfo = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Items that can have an order (Semi, Finished; active) and active lines. Default window: tomorrow 08-12.
    public async Task InitAsync()
    {
        var tomorrow = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date.AddDays(1);
        StartDate = EndDate = tomorrow;
        await RunAsync(async () =>
        {
            ItemOptions.Clear();
            foreach (var type in new[] { "Finished", "Semi" })
                foreach (var i in (await masterData.GetItemsAsync(null, type, true, 1, 200)).Items) ItemOptions.Add(i);
            LineOptions.Clear();
            foreach (var l in (await masterData.GetLinesAsync()).Where(l => l.IsActive).OrderBy(l => l.Code))
                LineOptions.Add(new LineOption(l.Id, l.Code));
            Line = LineOptions.FirstOrDefault();
        });
    }

    // A new item or quantity makes the old preview wrong: clear it rather than show stale numbers.
    partial void OnItemChanged(ItemDto? value) => ClearPreview();
    partial void OnPlannedQtyTextChanged(string value) => ClearPreview();

    [RelayCommand]
    private async Task PreviewAsync()
    {
        FieldErrors.Clear();
        ClearPreview();
        if (!TryReadItemAndQty(out var itemId, out var qty)) return;
        await RunAsync(async () =>
        {
            var preview = await api.PreviewAsync(new PreviewWorkOrderRequest(itemId, qty));
            foreach (var m in preview.Materials) Materials.Add(m);
            foreach (var o in preview.Operations) Operations.Add(o);
            PreviewInfo = string.Format(Strings.WorkOrderCreate_PreviewInfo, preview.ItemCode, preview.PlannedQty,
                preview.BomVersion, preview.RoutingVersion);
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        FieldErrors.Clear();
        var ok = TryReadItemAndQty(out var itemId, out var qty);
        if (Line is null) { FieldErrors.Set("LineId", Strings.Validation_Required); ok = false; }
        var start = ReadPlantTime(StartDate, StartTimeText, "PlannedStart");
        var end = ReadPlantTime(EndDate, EndTimeText, "PlannedEnd");
        if (start is not null && end is not null && end <= start) { FieldErrors.Set("PlannedEnd", Strings.WorkOrderCreate_EndAfterStart); ok = false; }
        if (!ok || start is null || end is null) return;

        await RunAsync(async () =>
        {
            await api.CreateAsync(new CreateWorkOrderRequest(itemId, Line!.Id!.Value, qty, start.Value, end.Value, Priority, RequireFai));
            await pages.ShowAsync<WorkOrderListViewModel>();
        });
    }

    [RelayCommand]
    private Task CancelAsync() => pages.ShowAsync<WorkOrderListViewModel>();

    private bool TryReadItemAndQty(out int itemId, out int qty)
    {
        itemId = Item?.Id ?? 0;
        var ok = true;
        if (Item is null) { FieldErrors.Set("ItemId", Strings.Validation_Required); ok = false; }
        if (!int.TryParse(PlannedQtyText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out qty) || qty <= 0)
        {
            FieldErrors.Set("PlannedQty", Strings.WorkOrderCreate_QtyWholeNumber);
            ok = false;
        }
        return ok;
    }

    // The planner types plant time; the API stores UTC.
    private DateTime? ReadPlantTime(DateTime date, string timeText, string field)
    {
        if (!TimeOnly.TryParseExact(timeText.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            FieldErrors.Set(field, Strings.WorkOrderCreate_TimeFormat);
            return null;
        }
        var local = DateTime.SpecifyKind(date.Date + t.ToTimeSpan(), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, time.LocalTimeZone);
    }

    private void ClearPreview()
    {
        Materials.Clear();
        Operations.Clear();
        PreviewInfo = string.Empty;
    }

    // 400 lands under the right box (ItemId, LineId, PlannedQty, PlannedEnd, Priority); 422 (no approved BOM
    // or routing in the preview) goes to the page message.
    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (ApiException ex) when (ex.FieldErrors.Count > 0)
        {
            FieldErrors.SetAll(ex.FieldErrors);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
