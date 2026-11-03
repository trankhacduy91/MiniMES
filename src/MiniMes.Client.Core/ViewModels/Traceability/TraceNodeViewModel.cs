using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Traceability;

namespace MiniMes.Client.Core.ViewModels.Traceability;

// One node of the backward tree (spec M6 2.6). Children are loaded the first time the node is opened: a node that
// has some shows a "Loading..." placeholder so the TreeView draws the expander. A failed load can be retried by
// closing and opening the node again.
public partial class TraceNodeViewModel : ObservableObject
{
    private readonly ITraceApi? _api;
    private readonly TimeZoneInfo _zone;
    private bool _loaded;

    public string Kind { get; }
    public string Key { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsPlaceholder => _api is null;
    public ObservableCollection<TraceNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    public TraceNodeViewModel(ITraceApi api, string kind, string key, string title, string detail, bool hasChildren, TimeZoneInfo zone)
    {
        (_api, Kind, Key, Title, Detail, _zone) = (api, kind, key, title, detail, zone);
        if (hasChildren) Children.Add(Info(Strings.Trace_Loading));
    }

    // A text-only node (placeholder, "nothing recorded", error): never loads anything.
    private TraceNodeViewModel(string text)
    {
        (Kind, Key, Title, Detail, _zone, _loaded) = (string.Empty, string.Empty, text, string.Empty, TimeZoneInfo.Utc, true);
    }

    public static TraceNodeViewModel Info(string text) => new(text);

    public static TraceNodeViewModel Serial(ITraceApi api, string serialNo, TimeZoneInfo zone)
        => new(api, TraceNodeKind.Serial, serialNo, serialNo, Strings.Trace_SerialNode, hasChildren: true, zone);

    // The task of the current load, so tests (and a caller that needs it) can wait for it.
    public Task Loading { get; private set; } = Task.CompletedTask;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded && !IsPlaceholder) Loading = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;   // before the await: opening and closing fast must not call the API twice
        try
        {
            var children = await _api!.GetChildrenAsync(Kind, Key);
            Children.Clear();
            foreach (var c in children) Children.Add(FromDto(c));
            if (Children.Count == 0)
                Children.Add(Info(Kind == TraceNodeKind.Serial ? Strings.Trace_NoMaterial : Strings.Trace_NoParents));
        }
        catch (ApiException ex)
        {
            _loaded = false;
            Children.Clear();
            Children.Add(Info(string.Format(CultureInfo.InvariantCulture, Strings.Trace_LoadFailed, ex.Message)));
        }
    }

    private TraceNodeViewModel FromDto(TraceNodeDto d)
    {
        var supplier = d.SupplierLot is null ? string.Empty : string.Format(CultureInfo.InvariantCulture, Strings.Trace_SupplierLot, d.SupplierLot);
        var detail = d.ConsumedAt is { } at
            // A part replaced at Repair: where, when, which technician (no reel was loaded on a machine).
            ? d.FromRepair ? string.Format(CultureInfo.InvariantCulture, Strings.Trace_RepairDetail, d.StationCode, Local(at), d.OperatorCode) + supplier
            // A lot the board consumed: where, when, who scanned, who loaded the reel.
            : string.Format(CultureInfo.InvariantCulture, Strings.Trace_ConsumedDetail, d.StationCode, Local(at), d.OperatorCode, d.LoadedBy ?? "-") + supplier
            // A lot it was split from.
            : string.Format(CultureInfo.InvariantCulture, Strings.Trace_ParentDetail, d.Qty) + supplier;
        return new TraceNodeViewModel(_api!, d.Kind, d.Key, $"{d.Key}   {d.ItemCode}", detail, d.HasChildren, _zone);
    }

    private string Local(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
