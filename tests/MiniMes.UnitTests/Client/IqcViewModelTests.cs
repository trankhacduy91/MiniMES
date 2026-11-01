using System.Net;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Material;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Material;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// [v2] Spec M6 step 2b: the IQC screen without a server.
public class IqcViewModelTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 1, 30, 0, DateTimeKind.Utc);

    private sealed class Codes : FakeMasterDataApi
    {
        public override Task<IReadOnlyList<DefectCodeDto>> GetDefectCodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DefectCodeDto>>([
                new(1, "SLD-BRG", "Solder bridge", "Solder", true),
                new(2, "INC-WRONG-VALUE", "Wrong component value", "Incoming", true),
                new(3, "INC-OLD", "Retired", "Incoming", false)]);
    }

    private sealed class Dialogs(bool confirm) : IDialogService
    {
        public int Confirms { get; private set; }
        public bool Confirm(string message) { Confirms++; return confirm; }
        public string? PickFileToOpen(string filter) => null;
        public string? PickFileToSave(string defaultFileName, string filter) => null;
        public string? AskText(string title, string prompt) => null;
    }

    private sealed class FakeIqcApi : IMaterialApi
    {
        public List<PendingIqcDto> Pending { get; } = [new(7, "CAP-100N-0402", "Capacitor", "pcs", "MUR-2609-A17", 3, 30_000, Now, null)];
        public List<IqcRecordDto> Records { get; } = [];
        public List<RecordIqcRequest> Sent { get; } = [];
        public ApiException? Fail { get; set; }

        public Task<IReadOnlyList<PendingIqcDto>> GetPendingIqcAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingIqcDto>>(Pending.ToList());
        public Task<IReadOnlyList<IqcRecordDto>> GetIqcRecordsAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqcRecordDto>>(Records.ToList());

        public Task<IqcRecordDto> RecordIqcAsync(RecordIqcRequest r, CancellationToken ct = default)
        {
            if (Fail is { } ex) { Pending.Clear(); return Task.FromException<IqcRecordDto>(ex); }   // someone else inspected it
            Sent.Add(r);
            Pending.RemoveAll(p => p.ItemId == r.ItemId && p.SupplierLot == r.SupplierLot);
            var record = new IqcRecordDto(1, r.ItemId, "CAP-100N-0402", r.SupplierLot, 3, r.SampleQty, r.DefectQty, r.Result, r.DefectCode, r.Note, "qc01", Now);
            Records.Insert(0, record);
            return Task.FromResult(record);
        }

        public Task<ReceiveLotsResultDto> ReceiveAsync(ReceiveLotsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PagedResult<MaterialLotDto>> GetLotsAsync(string? search, string? status, int page, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetLabelAsync(int lotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> PreviewLabelAsync(int lotId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private readonly FakeIqcApi _api = new();

    private async Task<IqcViewModel> LoadedAsync(Dialogs? dialogs = null)
    {
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        var vm = new IqcViewModel(_api, new Codes(), dialogs ?? new Dialogs(true), time);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Lists_deliveries_waiting_and_only_active_incoming_defect_codes()
    {
        var vm = await LoadedAsync();

        Assert.Equal([("MUR-2609-A17", 3, "01 Nov 2026 08:30")], vm.Pending.Select(p => (p.Item.SupplierLot, p.Item.LotCount, p.ReceivedText)));
        Assert.Equal(["INC-WRONG-VALUE"], vm.DefectCodes.Select(d => d.Code));
        Assert.False(vm.PassCommand.CanExecute(null));   // nothing selected yet
    }

    [Fact]
    public async Task Pass_sends_one_result_for_the_supplier_lot_and_refreshes()
    {
        var vm = await LoadedAsync();
        vm.Selected = vm.Pending[0];
        vm.SampleQtyText = "5";

        await vm.PassCommand.ExecuteAsync(null);

        Assert.Equal(new RecordIqcRequest(7, "MUR-2609-A17", 5, 0, "Pass", null, null), _api.Sent.Single());
        Assert.Equal(string.Format(Strings.Iqc_Passed, 3, "CAP-100N-0402", "MUR-2609-A17"), vm.ResultMessage);
        Assert.Empty(vm.Pending);
        Assert.Single(vm.Records);
        Assert.Equal(string.Empty, vm.SampleQtyText);
    }

    [Theory]
    [InlineData("Fail", "5", "1", false, "", "DefectCode", nameof(Strings.Iqc_DefectCodeRequired))]
    [InlineData("Pass", "5", "1", false, "", "Note", nameof(Strings.Iqc_NoteRequired))]
    [InlineData("Pass", "0", "0", false, "", "SampleQty", nameof(Strings.Iqc_SamplesPositive))]
    [InlineData("Pass", "5", "6", false, "", "DefectQty", nameof(Strings.Iqc_DefectsRange))]
    public async Task Incomplete_results_are_shown_under_their_box_and_not_sent(string result, string samples, string defects,
        bool code, string note, string field, string message)
    {
        var vm = await LoadedAsync();
        vm.Selected = vm.Pending[0];
        (vm.SampleQtyText, vm.DefectQtyText, vm.Note) = (samples, defects, note);
        if (code) vm.DefectCode = vm.DefectCodes[0];

        await (result == "Pass" ? vm.PassCommand : vm.FailCommand).ExecuteAsync(null);

        Assert.Empty(_api.Sent);
        Assert.Equal(Strings.ResourceManager.GetString(message), vm.FieldErrors[field]);
    }

    // R17: rejecting cannot be undone by a normal action, so the screen asks first; "No" sends nothing.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Fail_asks_for_confirmation_first(bool confirm, int sent)
    {
        var dialogs = new Dialogs(confirm);
        var vm = await LoadedAsync(dialogs);
        vm.Selected = vm.Pending[0];
        (vm.SampleQtyText, vm.DefectQtyText, vm.DefectCode) = ("5", "3", vm.DefectCodes[0]);

        await vm.FailCommand.ExecuteAsync(null);

        Assert.Equal((1, sent), (dialogs.Confirms, _api.Sent.Count));
        if (confirm) Assert.Equal(("Fail", "INC-WRONG-VALUE"), (_api.Sent[0].Result, _api.Sent[0].DefectCode));
    }

    // Another inspector was faster: the 422 message is shown and the list no longer offers the delivery.
    [Fact]
    public async Task Already_inspected_shows_the_message_and_refreshes_the_list()
    {
        var vm = await LoadedAsync();
        vm.Selected = vm.Pending[0];
        vm.SampleQtyText = "5";
        _api.Fail = new ApiException(HttpStatusCode.UnprocessableEntity, "No lots are pending IQC (already inspected?).");

        await vm.PassCommand.ExecuteAsync(null);

        Assert.Equal("No lots are pending IQC (already inspected?).", vm.ErrorMessage);
        Assert.Empty(vm.Pending);
        Assert.Null(vm.Selected);
    }
}
