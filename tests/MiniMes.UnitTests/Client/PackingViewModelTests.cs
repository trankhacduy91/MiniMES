using System.Globalization;
using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Packing;

namespace MiniMes.UnitTests.Client;

// Spec M3b step 6 / 2.5: the Packing screen of L1-PACK-01. Pack: boards into the open carton ("7 / 10"); Palletize:
// carton labels onto the open pallet; full = its own colour and sound, the label goes to the label folder; partial
// close = a reason, then the supervisor's badge in the scan box.
public class PackingViewModelTests
{
    private const string Board = "PCBA2610170000011", Carton = "C261103000123", Pallet = "P261103000004";
    private static readonly DateTime At = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);

    private static OpenCartonDto OpenCarton(int qty) => new(Carton, qty, At,
        Enumerable.Range(1, qty).Reverse().Select(i => new PackedSerialDto($"PCBA26101700000{i:D2}", At.AddSeconds(i), "EMP00123")).ToList());

    private static async Task<(FakeKiosk Kiosk, PackingViewModel Vm)> StationAsync(int inCarton = 7)
    {
        var kiosk = new FakeKiosk().Started(FakeKiosk.PackStation);
        kiosk.Packing.Status = kiosk.Packing.Status with { OpenCarton = inCarton == 0 ? null : OpenCarton(inCarton) };
        var vm = kiosk.PackingScreen();
        await vm.LoadAsync();
        return (kiosk, vm);
    }

    private static Task ScanAsync(PackingViewModel vm, string text)
    {
        vm.ScanInput = text;
        return vm.ScanCommand.ExecuteAsync(null);
    }

    private static PackResponse Packed(int qty, bool closed = false, string? zpl = null, bool duplicate = false)
        => new("Ok", "Packed", null, duplicate, false, Carton, qty, 10, closed, zpl);

    [Fact]
    public async Task Shows_the_open_carton_with_its_boards_newest_first()
    {
        var (_, vm) = await StationAsync();

        Assert.Equal(("7 / 10", string.Format(CultureInfo.InvariantCulture, Strings.Packing_Carton, Carton)), (vm.ProgressText, vm.ContainerTitle));
        Assert.Equal(7, vm.Lines.Count);
        Assert.StartsWith("PCBA2610170000007", vm.Lines[0].Text);
        Assert.Equal(Strings.Packing_PromptSerial, vm.Prompt);
    }

    [Fact]
    public async Task No_open_carton_says_the_next_board_opens_one()
    {
        var (_, vm) = await StationAsync(inCarton: 0);

        Assert.Equal(("0 / 10", Strings.Packing_NewCarton), (vm.ProgressText, vm.ContainerTitle));
        Assert.False(vm.OpenPartialCommand.CanExecute(null));   // nothing to close short
    }

    [Fact]
    public async Task A_board_goes_in_green_and_the_carton_is_read_again()
    {
        var (kiosk, vm) = await StationAsync();
        kiosk.Packing.Pack = _ => Task.FromResult(Packed(8));
        kiosk.Packing.Status = kiosk.Packing.Status with { OpenCarton = OpenCarton(8) };

        await ScanAsync(vm, $" {Board.ToLowerInvariant()} ");

        Assert.Equal([Board], kiosk.Packing.Packed);
        Assert.Equal((ScanState.Ok, Strings.Packing_Packed), (vm.State, vm.Headline));
        Assert.Equal($"{Carton}  8 / 10", vm.Message);
        Assert.Equal("8 / 10", vm.ProgressText);
        Assert.Equal([ScanState.Ok], kiosk.Feedback.Played);
    }

    // Spec 2.5: the full carton has its own colour and sound; the label the server made goes to the label folder.
    [Fact]
    public async Task Full_carton_plays_its_own_sound_and_prints_the_label()
    {
        var (kiosk, vm) = await StationAsync(inCarton: 9);
        kiosk.Packing.Pack = _ => Task.FromResult(Packed(10, closed: true, zpl: "^XA^FDC261103000123^FS^XZ"));
        kiosk.Packing.Status = kiosk.Packing.Status with { OpenCarton = null };

        await ScanAsync(vm, Board);

        Assert.Equal((ScanState.Full, Strings.Packing_CartonFull), (vm.State, vm.Headline));
        Assert.Equal([(Carton, "^XA^FDC261103000123^FS^XZ")], kiosk.Printer.Printed);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Strings.Packing_LabelPrinted, $"{Carton}-1.zpl"), vm.Message);
        Assert.Equal([ScanState.Full], kiosk.Feedback.Played);
        Assert.Equal(("0 / 10", Strings.Packing_NewCarton), (vm.ProgressText, vm.ContainerTitle));
    }

    [Fact]
    public async Task Label_folder_not_writable_is_said_but_the_carton_stays_closed()
    {
        var (kiosk, vm) = await StationAsync(inCarton: 9);
        kiosk.Packing.Pack = _ => Task.FromResult(Packed(10, closed: true, zpl: "^XA^XZ"));
        kiosk.Printer.Fails = new UnauthorizedAccessException("Access denied");

        await ScanAsync(vm, Board);

        Assert.Equal(ScanState.Full, vm.State);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Strings.Packing_LabelFailed, "Access denied"), vm.Message);
    }

    // The server could not make the label (it says so in its message): shown as is, nothing printed.
    [Fact]
    public async Task Missing_label_shows_the_servers_message()
    {
        var (kiosk, vm) = await StationAsync(inCarton: 9);
        kiosk.Packing.Pack = _ => Task.FromResult(Packed(10, closed: true) with { Message = "Carton full: closed. Label NOT printed: print it again." });

        await ScanAsync(vm, Board);

        Assert.Empty(kiosk.Printer.Printed);
        Assert.Contains("Label NOT printed", vm.Message);
    }

    [Theory]
    [InlineData("NotASerial")]
    [InlineData("SkippedStep")]
    [InlineData("WrongItem")]
    public async Task Blocked_board_shows_the_translated_reason(string code)
    {
        var (kiosk, vm) = await StationAsync();
        kiosk.Packing.Pack = _ => Task.FromResult(new PackResponse("Blocked", "server words", code, false, false, null, 0, 10, false));

        await ScanAsync(vm, Carton);

        Assert.Equal((ScanState.Blocked, Strings.ResourceManager.GetString("Block_" + code)), (vm.State, vm.Message));
        Assert.Equal($"{Carton} - server words", vm.Detail);
        Assert.Equal([ScanState.Blocked], kiosk.Feedback.Played);
    }

    [Fact]
    public async Task No_answer_turns_the_screen_offline()
    {
        var (kiosk, vm) = await StationAsync();
        kiosk.Packing.Pack = _ => Task.FromException<PackResponse>(new ApiException(null, "Server unreachable"));

        await ScanAsync(vm, Board);

        Assert.True(vm.IsOffline);
        Assert.Equal(Strings.Scan_NoResponse, vm.Headline);
        Assert.False(vm.ScanCommand.CanExecute(null));
    }

    // ---------- Palletize ----------

    private static async Task<(FakeKiosk Kiosk, PackingViewModel Vm)> PalletizingAsync()
    {
        var (kiosk, vm) = await StationAsync();
        kiosk.Packing.Status = kiosk.Packing.Status with
        {
            OpenPallet = new OpenPalletDto(Pallet, 12,
            [
                new PalletCartonDto("C261103000135", "WO-2610-00005", 10, false, true),
                new PalletCartonDto("C261103000134", "WO-2610-00005", 7, true, false),
            ]),
        };
        await vm.SwitchModeCommand.ExecuteAsync(PackingMode.Palletize);
        return (kiosk, vm);
    }

    [Fact]
    public async Task Palletize_mode_shows_the_open_pallet_and_its_cartons()
    {
        var (_, vm) = await PalletizingAsync();

        Assert.True(vm.IsPalletizeMode);
        Assert.Equal(("12 / 20", string.Format(CultureInfo.InvariantCulture, Strings.Packing_Pallet, Pallet)), (vm.ProgressText, vm.ContainerTitle));
        Assert.Equal([true, false], vm.Lines.Select(l => l.OnHold));
        Assert.Contains(Strings.Packing_Partial, vm.Lines[1].Text);
        Assert.Equal((Strings.Packing_PromptCarton, Strings.Packing_ClosePartialPallet), (vm.Prompt, vm.PartialButtonText));
    }

    [Fact]
    public async Task Scans_in_palletize_mode_go_onto_the_pallet_and_a_full_one_prints()
    {
        var (kiosk, vm) = await PalletizingAsync();
        kiosk.Packing.Palletize = c => Task.FromResult(new PalletizeResponse("Ok", "closed", null, false, Pallet, 20, 20, true, "^XA^XZ"));

        await ScanAsync(vm, Carton);

        Assert.Empty(kiosk.Packing.Packed);
        Assert.Equal([Carton], kiosk.Packing.Palletized);
        Assert.Equal((ScanState.Full, Strings.Packing_PalletFull), (vm.State, vm.Headline));
        Assert.Equal([(Pallet, "^XA^XZ")], kiosk.Printer.Printed);
    }

    // ---------- partial carton: reason, then the supervisor's badge ----------

    private static CartonDto ClosedShort(ClosePartialRequest r) => new(Carton, "WO-2610-00005", "PCB-A", 7, "Closed", true, null, false,
        At, At.AddHours(1), "sup01", r.Reason, [], "^XA^FDPARTIAL^FS^XZ");

    [Fact]
    public async Task Partial_carton_needs_a_reason_then_the_supervisors_badge()
    {
        var (kiosk, vm) = await StationAsync();
        kiosk.Packing.ClosePartialCarton = (_, r) => Task.FromResult(ClosedShort(r));
        kiosk.Packing.Status = kiosk.Packing.Status with { OpenCarton = null };

        vm.OpenPartialCommand.Execute(null);
        Assert.Equal((true, Strings.Packing_PromptReason), (vm.IsPartialOpen, vm.Prompt));
        vm.ChooseReasonCommand.Execute(Strings.Packing_ReasonWoEnd);
        Assert.Equal((PackingInput.SupervisorBadge, string.Format(CultureInfo.InvariantCulture, Strings.Packing_PromptBadge, Strings.Packing_ReasonWoEnd)),
            (vm.Input, vm.Prompt));
        await ScanAsync(vm, "sup01");

        Assert.Empty(kiosk.Packing.Packed);   // the badge is not a board
        Assert.Equal([(Carton, new ClosePartialRequest(Strings.Packing_ReasonWoEnd, "SUP01"))], kiosk.Packing.Partials);
        Assert.Equal((ScanState.Full, Strings.Packing_ClosedShortHeadline), (vm.State, vm.Headline));
        Assert.StartsWith(string.Format(CultureInfo.InvariantCulture, Strings.Packing_CartonClosedShort, Carton, 7, "sup01"), vm.Message);
        Assert.Equal([(Carton, "^XA^FDPARTIAL^FS^XZ")], kiosk.Printer.Printed);
        Assert.Equal((false, PackingInput.Code), (vm.IsPartialOpen, vm.Input));
    }

    // A badge that is not a supervisor's (400): the step stays, the right badge can be scanned. Anything else ends it.
    [Fact]
    public async Task Wrong_badge_keeps_the_badge_step_other_refusals_end_it()
    {
        var (kiosk, vm) = await StationAsync();
        var answers = new Queue<Func<ClosePartialRequest, Task<CartonDto>>>(
        [
            _ => Task.FromException<CartonDto>(new ApiException(HttpStatusCode.BadRequest, "Validation failed", null,
                new Dictionary<string, string[]> { ["SupervisorBadge"] = ["'EMP00123' is not an active supervisor's badge."] })),
            _ => Task.FromException<CartonDto>(new ApiException(HttpStatusCode.UnprocessableEntity, "Carton C261103000123 is full: it closes by itself.")),
        ]);
        kiosk.Packing.ClosePartialCarton = (_, r) => answers.Dequeue()(r);
        vm.OpenPartialCommand.Execute(null);
        vm.ChooseReasonCommand.Execute(Strings.Packing_ReasonShiftEnd);

        await ScanAsync(vm, "EMP00123");
        Assert.Equal((ScanState.Blocked, "'EMP00123' is not an active supervisor's badge.", PackingInput.SupervisorBadge, true),
            (vm.State, vm.Message, vm.Input, vm.IsPartialOpen));

        await ScanAsync(vm, "sup01");
        Assert.Equal((PackingInput.Code, false), (vm.Input, vm.IsPartialOpen));
        Assert.Empty(kiosk.Printer.Printed);
    }

    [Fact]
    public async Task Switching_mode_cancels_a_partial_close_in_progress()
    {
        var (_, vm) = await StationAsync();
        vm.OpenPartialCommand.Execute(null);
        vm.ChooseReasonCommand.Execute(Strings.Packing_ReasonWoEnd);

        await vm.SwitchModeCommand.ExecuteAsync(PackingMode.Palletize);

        Assert.Equal((false, PackingInput.Code, Strings.Packing_PromptCarton), (vm.IsPartialOpen, vm.Input, vm.Prompt));
    }
}

// The kiosk's label folder (user decision M3b step 6): one file per print, named by label number and plant time.
public class FolderLabelPrinterTests
{
    [Fact]
    public async Task Each_print_is_its_own_file_named_by_label_and_plant_time()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MiniMesLabelTest-" + Guid.NewGuid().ToString("N"));
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 13, 25, 20, 444, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Plant", TimeSpan.FromHours(7), "Plant", "Plant"));
        var printer = new FolderLabelPrinter(Microsoft.Extensions.Options.Options.Create(
            new MiniMes.Client.Core.ClientOptions { ApiBaseUrl = "http://x/", LabelOutputFolder = folder }), time);
        try
        {
            var first = await printer.PrintAsync("C261103000001", "^XA^XZ");
            time.Advance(TimeSpan.FromSeconds(5));
            var reprint = await printer.PrintAsync("C261103000001", "^XA^FDREPRINT^FS^XZ");

            Assert.Equal(Path.Combine(folder, "C261103000001-20261103T202520444.zpl"), first);   // 13:25 UTC = 20:25 plant
            Assert.NotEqual(first, reprint);
            Assert.Equal("^XA^XZ", await File.ReadAllTextAsync(first));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
