using System.Net;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Material;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Material;
using MiniMes.Contracts.MasterData;

namespace MiniMes.UnitTests.Client;

// Spec M6 step 2: the Receiving screen without a server.
public class ReceivingViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 1, 30, 0, TimeSpan.Zero);   // 08:30 plant time

    private static ItemDto Item(int id, string code, bool lot = true, string type = "Raw")
        => new(id, code, code, type, "pcs", false, lot, false, true);

    private sealed class Items : FakeMasterDataApi
    {
        public override Task<PagedResult<ItemDto>> GetItemsAsync(string? search, string? type, bool? isActive, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<ItemDto>([Item(7, "CAP-100N-0402"), Item(8, "SCREW-M2", lot: false)], 1, 200, 2));
    }

    private sealed class FakeMaterialApi : IMaterialApi
    {
        public List<ReceiveLotsRequest> Received { get; } = [];
        public List<MaterialLotDto> Stored { get; } = [];
        public ApiException? Fail { get; set; }

        public Task<ReceiveLotsResultDto> ReceiveAsync(ReceiveLotsRequest request, CancellationToken ct = default)
        {
            if (Fail is { } ex) return Task.FromException<ReceiveLotsResultDto>(ex);
            Received.Add(request);
            var lots = Enumerable.Range(1, request.ReelCount).Select(i => Lot(100 + i, $"L-261101-00000{i}", "PendingIqc")).ToList();
            Stored.InsertRange(0, lots);
            return Task.FromResult(new ReceiveLotsResultDto(lots, "^XA...^XZ"));
        }

        public Task<PagedResult<MaterialLotDto>> GetLotsAsync(string? search, string? status, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<MaterialLotDto>(Stored, 1, pageSize, Stored.Count));

        public Task<string> GetLabelAsync(int lotId, CancellationToken ct = default) => Task.FromResult($"^XA lot {lotId} ^XZ");
        public Task<byte[]> PreviewLabelAsync(int lotId, CancellationToken ct = default) => Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<IReadOnlyList<StationMaterialDto>> GetStationMaterialsAsync(int stationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LoadMaterialResponse> LoadAsync(int stationId, LoadMaterialRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<StationMaterialDto>> UnloadAsync(int stationId, int loadId, UnloadMaterialRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PendingIqcDto>> GetPendingIqcAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<IqcRecordDto>> GetIqcRecordsAsync(int take, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IqcRecordDto> RecordIqcAsync(RecordIqcRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static MaterialLotDto Lot(int id, string lotNo, string status)
        => new(id, lotNo, 7, "CAP-100N-0402", "Capacitor", "pcs", 10_000, 10_000, "MUR-2609-A17",
            Now.UtcDateTime, new DateTime(2028, 9, 2, 17, 0, 0, DateTimeKind.Utc), status);

    private readonly FakeMaterialApi _api = new();
    private readonly SaveDialogs _dialogs = new();
    private readonly FakeTimeProvider _time = new(Now);

    public ReceivingViewModelTests() => _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));

    private async Task<ReceivingViewModel> LoadedAsync()
    {
        var vm = new ReceivingViewModel(_api, new Items(), _dialogs, _time);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Offers_only_lot_controlled_items()
    {
        var vm = await LoadedAsync();

        Assert.Equal(["CAP-100N-0402"], vm.ItemOptions.Select(i => i.Code));
    }

    [Fact]
    public async Task Wrong_input_is_shown_under_its_box_and_nothing_is_sent()
    {
        var vm = await LoadedAsync();
        vm.QtyPerReelText = "abc";
        vm.ReelCountText = "1.5";
        vm.ExpiryDate = new DateTime(2026, 11, 1);   // today in plant time

        await vm.ReceiveCommand.ExecuteAsync(null);

        Assert.Empty(_api.Received);
        Assert.Equal(Strings.Validation_Required, vm.FieldErrors["ItemId"]);
        Assert.Equal(Strings.Validation_Required, vm.FieldErrors["SupplierLot"]);
        Assert.Equal(Strings.Receiving_QtyPositive, vm.FieldErrors["QtyPerReel"]);
        Assert.Equal(Strings.Receiving_ReelsWholeNumber, vm.FieldErrors["ReelCount"]);
        Assert.Equal(Strings.Receiving_ExpiryFuture, vm.FieldErrors["ExpiryDate"]);
    }

    // Spec 1.3 A: 5 reels of one supplier lot -> 5 lots, labels saved for the printer, the new lots on top.
    [Fact]
    public async Task Receiving_creates_the_lots_and_saves_their_labels()
    {
        var vm = await LoadedAsync();
        var path = Path.Combine(Path.GetTempPath(), $"lots-{Guid.NewGuid():N}.zpl");
        _dialogs.SavePath = path;
        vm.Item = vm.ItemOptions[0];
        vm.SupplierLot = " MUR-2609-A17 ";
        vm.QtyPerReelText = "10000";
        vm.ReelCountText = "5";
        vm.ExpiryDate = new DateTime(2028, 9, 3);

        await vm.ReceiveCommand.ExecuteAsync(null);

        try
        {
            Assert.Equal(new ReceiveLotsRequest(7, "MUR-2609-A17", 10_000, 5, new DateOnly(2028, 9, 3)), _api.Received.Single());
            Assert.Equal(string.Format(Strings.Receiving_Received, 5, "L-261101-000001", "L-261101-000005", Strings.Receiving_PendingIqc),
                vm.ResultMessage);
            Assert.Equal(5, vm.Lots.Count);
            Assert.Equal(101, vm.SelectedLot!.Lot.Id);
            Assert.Equal(("L-261101-000001.zpl", "^XA...^XZ"), (_dialogs.AskedName, await File.ReadAllTextAsync(path)));
            Assert.Equal(string.Empty, vm.SupplierLot);                           // the next delivery is another supplier lot
            Assert.Equal(("01 Nov 2026 08:30", "03 Sep 2028"), (vm.Lots[0].ReceivedText, vm.Lots[0].ExpiryText));   // plant time
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Server_refusal_lands_under_its_box()
    {
        var vm = await LoadedAsync();
        _api.Fail = new ApiException(HttpStatusCode.BadRequest, "x", fieldErrors: new Dictionary<string, string[]>
        {
            ["ItemId"] = ["Item 'CAP-100N-0402' is inactive."],
        });
        vm.Item = vm.ItemOptions[0];
        vm.SupplierLot = "S1";
        vm.QtyPerReelText = "10";

        await vm.ReceiveCommand.ExecuteAsync(null);

        Assert.Equal("Item 'CAP-100N-0402' is inactive.", vm.FieldErrors["ItemId"]);
        Assert.Equal(string.Empty, vm.ResultMessage);
    }

    [Fact]
    public async Task Selected_lot_label_can_be_previewed_and_printed_again()
    {
        _api.Stored.Add(Lot(55, "L-260901-000002", "Available"));
        var vm = await LoadedAsync();
        Assert.False(vm.PreviewLabelCommand.CanExecute(null));
        var path = Path.Combine(Path.GetTempPath(), $"lot-{Guid.NewGuid():N}.zpl");
        _dialogs.SavePath = path;

        vm.SelectedLot = vm.Lots[0];
        await vm.PreviewLabelCommand.ExecuteAsync(null);
        await vm.PrintLabelCommand.ExecuteAsync(null);

        try
        {
            Assert.True(vm.HasLabelImage);
            Assert.Equal("^XA lot 55 ^XZ", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
}
