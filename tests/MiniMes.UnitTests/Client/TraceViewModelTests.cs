using System.Net;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Traceability;
using MiniMes.Contracts.Traceability;

namespace MiniMes.UnitTests.Client;

// Spec M6 step 8: the Trace screen and its lazy tree (spec 2.6) without a server.
public class TraceViewModelTests
{
    private static readonly DateTime At = new(2026, 10, 20, 2, 15, 0, DateTimeKind.Utc);   // 09:15 plant time
    private static readonly TimeZoneInfo Plant = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");

    private sealed class FakeTraceApi : ITraceApi
    {
        public Dictionary<(string Kind, string Key), IReadOnlyList<TraceNodeDto>> Children { get; } = [];
        public List<(string Kind, string Key)> ChildCalls { get; } = [];
        public ApiException? FailChildren { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public ForwardTraceResultDto Forward { get; set; } = new([], [], []);
        public List<(string? Lot, string? Supplier, string? Item)> ForwardCalls { get; } = [];
        public List<string> Holds { get; } = [];

        public async Task<IReadOnlyList<TraceNodeDto>> GetChildrenAsync(string kind, string key, CancellationToken ct = default)
        {
            ChildCalls.Add((kind, key));
            if (Gate is { } g) await g.Task;
            if (FailChildren is { } ex) throw ex;
            return Children.GetValueOrDefault((kind, key), []);
        }

        public Task<ForwardTraceResultDto> GetForwardAsync(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct = default)
        {
            ForwardCalls.Add((lotNo, supplierLot, itemCode));
            return Task.FromResult(Forward);
        }

        public Task<byte[]> GetForwardExcelAsync(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct = default)
            => Task.FromResult(new byte[] { 1, 2, 3 });

        public Task<HoldLotResultDto> HoldLotAsync(string lotNo, CancellationToken ct = default)
        {
            Holds.Add(lotNo);
            return Task.FromResult(new HoldLotResultDto(["L-CAP-A", "L-CAP-B"], [new SkippedLotDto("L-CAP-P", "Consumed")]));
        }
    }

    private sealed class Dialogs(bool confirm, string? savePath = null) : IDialogService
    {
        public int Confirms { get; private set; }
        public bool Confirm(string message) { Confirms++; return confirm; }
        public string? PickFileToOpen(string filter) => null;
        public string? PickFileToSave(string defaultFileName, string filter) => savePath;
        public string? AskText(string title, string prompt) => null;
    }

    private static TraceViewModel Vm(FakeTraceApi api, Dialogs? dialogs = null)
    {
        var time = new FakeTimeProvider(At);
        time.SetLocalTimeZone(Plant);
        return new TraceViewModel(api, dialogs ?? new Dialogs(true), time);
    }

    private static TraceNodeDto Consumed(string lot, string item, bool hasChildren)
        => new(TraceNodeKind.Lot, lot, item, "SUP-S1", "L1-SMT-01", At, "EMP00123", "EMP00456", null, hasChildren);

    [Fact]
    public async Task Backward_shows_the_board_with_its_lots_and_loads_a_parent_only_when_opened()
    {
        var api = new FakeTraceApi();
        api.Children[(TraceNodeKind.Serial, "TRACE-0150")] = [Consumed("L-CAP-B", "CAP-100NF", true), Consumed("L-RES-A", "RES-100K", false)];
        api.Children[(TraceNodeKind.Lot, "L-CAP-B")] = [new(TraceNodeKind.Lot, "L-CAP-P", "CAP-100NF", "SUP-S1", null, null, null, null, 2_000m, false)];
        var vm = Vm(api);
        vm.SearchText = " trace-0150 ";

        await vm.TraceCommand.ExecuteAsync(null);

        var root = Assert.Single(vm.RootNodes);
        Assert.True(vm.ShowTree);
        Assert.Equal(["L-CAP-B   CAP-100NF", "L-RES-A   RES-100K"], root.Children.Select(c => c.Title));
        Assert.Equal("L1-SMT-01  ·  2026-10-20 09:15:00  ·  scanned by EMP00123  ·  reel loaded by EMP00456  ·  supplier lot SUP-S1",
            root.Children[0].Detail);
        Assert.Equal([Strings.Trace_Loading], root.Children[0].Children.Select(c => c.Title));   // expander, nothing loaded yet
        Assert.Empty(root.Children[1].Children);
        Assert.Equal([(TraceNodeKind.Serial, "TRACE-0150")], api.ChildCalls);

        var capB = root.Children[0];
        capB.IsExpanded = true;
        await capB.Loading;

        Assert.Equal("L-CAP-P   CAP-100NF", Assert.Single(capB.Children).Title);
        Assert.StartsWith("parent lot, 2,000 taken", capB.Children[0].Detail);
    }

    [Fact]
    public async Task Opening_and_closing_fast_calls_the_api_once()
    {
        var api = new FakeTraceApi { Gate = new TaskCompletionSource() };
        var node = TraceNodeViewModel.Serial(api, "TRACE-0001", Plant);

        node.IsExpanded = true;
        node.IsExpanded = false;
        node.IsExpanded = true;
        api.Gate.SetResult();
        await node.Loading;

        Assert.Single(api.ChildCalls);
        Assert.Equal([Strings.Trace_NoMaterial], node.Children.Select(c => c.Title));   // nothing recorded: said, not an empty node
    }

    [Fact]
    public async Task A_failed_load_is_shown_and_retried_on_the_next_open()
    {
        var api = new FakeTraceApi { FailChildren = new ApiException(HttpStatusCode.ServiceUnavailable, "Server unreachable") };
        var node = TraceNodeViewModel.Serial(api, "TRACE-0001", Plant);

        node.IsExpanded = true;
        await node.Loading;
        Assert.Contains("Server unreachable", Assert.Single(node.Children).Title);

        api.FailChildren = null;
        api.Children[(TraceNodeKind.Serial, "TRACE-0001")] = [Consumed("L-CAP-A", "CAP-100NF", false)];
        node.IsExpanded = false;
        node.IsExpanded = true;
        await node.Loading;

        Assert.Equal(2, api.ChildCalls.Count);
        Assert.Equal("L-CAP-A   CAP-100NF", Assert.Single(node.Children).Title);
    }

    [Fact]
    public async Task Forward_lists_serials_with_a_summary_by_location()
    {
        var api = new FakeTraceApi
        {
            Forward = new(["L-CAP-B"], [new("CompletedUnpacked", 2), new("Wip", 1)],
            [
                new("TRACE-0121", "Completed", "WO-TRACE", 0, "L-CAP-B", "L1-SMT-01", At, "EMP00123", "CompletedUnpacked", null, null, null),
                new("TRACE-0122", "Completed", "WO-TRACE", 0, "L-CAP-B", "L1-SMT-01", At, "EMP00123", "CompletedUnpacked", null, null, null),
                new("TRACE-0123", "InProcess", "WO-TRACE", 0, "L-CAP-B", "L1-SMT-01", At, "EMP00123", "Wip", null, null, null),
            ]),
        };
        var vm = Vm(api);
        vm.IsLotMode = true;
        vm.SearchText = "l-cap-b";

        await vm.TraceCommand.ExecuteAsync(null);

        Assert.Equal([("L-CAP-B", (string?)null, (string?)null)], api.ForwardCalls);
        Assert.True(vm.ShowList);
        Assert.False(vm.ShowTree);
        Assert.Equal(3, vm.Serials.Count);
        Assert.Equal(("Completed, not packed", "2026-10-20 09:15:00"), (vm.Serials[0].LocationText, vm.Serials[0].ConsumedText));
        Assert.Equal("3 serial(s): Completed, not packed 2  ·  In production 1. Start lot(s): L-CAP-B", vm.Summary);
        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.HoldLotCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_supplier_lot_needs_the_item_code_and_an_empty_answer_is_said()
    {
        var api = new FakeTraceApi();
        var vm = Vm(api);
        vm.IsSupplierLotMode = true;
        vm.SearchText = "SUP-S1";

        Assert.False(vm.TraceCommand.CanExecute(null));
        vm.ItemCode = "cap-100nf";
        await vm.TraceCommand.ExecuteAsync(null);

        Assert.Equal([((string?)null, (string?)"SUP-S1", (string?)"cap-100nf")], api.ForwardCalls);
        Assert.Equal(Strings.Trace_NothingFound, vm.Summary);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.HoldLotCommand.CanExecute(null));   // no start lot: nothing to hold
    }

    [Fact]
    public async Task Hold_lot_asks_first_and_reports_held_and_unchanged_lots()
    {
        var api = new FakeTraceApi { Forward = new(["L-CAP-B"], [], []) };
        var no = Vm(api, new Dialogs(confirm: false));
        no.IsLotMode = true;
        no.SearchText = "L-CAP-B";
        await no.TraceCommand.ExecuteAsync(null);
        await no.HoldLotCommand.ExecuteAsync(null);
        Assert.Empty(api.Holds);

        var yes = Vm(api);
        yes.IsLotMode = true;
        yes.SearchText = "L-CAP-B";
        await yes.TraceCommand.ExecuteAsync(null);
        await yes.HoldLotCommand.ExecuteAsync(null);

        Assert.Equal(["L-CAP-B"], api.Holds);
        Assert.Equal("On hold: L-CAP-A, L-CAP-B. Unchanged: L-CAP-P (Consumed).", yes.ResultMessage);
    }

    [Fact]
    public async Task Export_writes_the_server_file_where_the_user_chose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trace-test-{Guid.NewGuid():N}.xlsx");
        var api = new FakeTraceApi
        {
            Forward = new(["L-CAP-B"], [new("Wip", 1)],
                [new("TRACE-0121", "InProcess", "WO-TRACE", 0, "L-CAP-B", "L1-SMT-01", At, "EMP00123", "Wip", null, null, null)]),
        };
        var vm = Vm(api, new Dialogs(true, path));
        vm.IsLotMode = true;
        vm.SearchText = "L-CAP-B";
        await vm.TraceCommand.ExecuteAsync(null);

        try
        {
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
            Assert.Contains(path, vm.ResultMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Every location the server can send has a text (M3b adds three; they are already translated).
    [Theory]
    [InlineData("Shipped")]
    [InlineData("OnPallet")]
    [InlineData("InCarton")]
    [InlineData("CompletedUnpacked")]
    [InlineData("Wip")]
    [InlineData("Scrapped")]
    public void Every_location_has_a_text(string location)
        => Assert.NotNull(Strings.ResourceManager.GetString("Trace_Location_" + location, Strings.Culture));
}
