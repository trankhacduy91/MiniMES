using System.Net;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Execution;

namespace MiniMes.UnitTests.Client;

// Spec M3 step 7: the kiosk scan screen without a window. Time is a FakeTimeProvider: the 1.8 s flash and the
// 3 s scan timeout are advanced by hand, never waited for.
public class ScanViewModelTests
{
    private const string Board = "PCBA2610170000011";

    private static (FakeKiosk Kiosk, ScanViewModel Vm) Screen(Func<ScanRequest, CancellationToken, Task<ScanResponse>>? scan = null)
    {
        var kiosk = new FakeKiosk().Started();
        if (scan is not null) kiosk.Fake.Scan = scan;
        return (kiosk, kiosk.Scan());
    }

    private static Func<ScanRequest, CancellationToken, Task<ScanResponse>> Answer(ScanResponse r) => (_, _) => Task.FromResult(r);

    private static Task ScanAsync(ScanViewModel vm, string input = Board)
    {
        vm.ScanInput = input;
        return vm.ScanCommand.ExecuteAsync(null);
    }

    // The flash resets in a continuation of Task.Delay, posted to the test's synchronization context.
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Ok_flashes_green_with_the_next_step_then_returns_to_ready()
    {
        var (kiosk, vm) = Screen();

        await ScanAsync(vm, "  pcba2610170000011 ");   // scanner suffix + lower case

        Assert.Equal(Board, Assert.Single(kiosk.Fake.Scans).SerialNo);
        Assert.Equal(string.Empty, vm.ScanInput);       // ready for the next scan at once
        Assert.Equal((ScanState.Ok, Strings.Scan_Ok, string.Format(Strings.Scan_Next, "TEST"), Board), (vm.State, vm.Headline, vm.Message, vm.Detail));
        Assert.Equal([ScanState.Ok], kiosk.Feedback.Played);

        kiosk.Time.Advance(ScanViewModel.FlashDuration - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        Assert.Equal(ScanState.Ok, vm.State);
        kiosk.Time.Advance(TimeSpan.FromMilliseconds(1));
        await UntilAsync(() => vm.State == ScanState.Idle);
        Assert.Equal((Strings.Scan_Ready, string.Empty), (vm.Headline, vm.Message));
    }

    [Theory]
    [InlineData(true, null, "Duplicate")]
    [InlineData(false, null, "Completed")]
    public async Task Ok_message_says_duplicate_or_completed(bool duplicate, string? next, string expected)
    {
        var (_, vm) = Screen(Answer(new ScanResponse("Ok", "x", null, null, next, duplicate)));

        await ScanAsync(vm);

        Assert.Equal(expected == "Duplicate" ? Strings.Scan_Duplicate : Strings.Scan_Completed, vm.Message);
    }

    [Fact]
    public async Task Blocked_shows_the_translated_reason_and_the_server_wording()
    {
        var (kiosk, vm) = Screen(Answer(new ScanResponse("Blocked", "Expected step 10 SMT; this station does step 20.", "SkippedStep", null, null, false)));

        await ScanAsync(vm);

        Assert.Equal((ScanState.Blocked, Strings.Scan_Blocked, Strings.Block_SkippedStep), (vm.State, vm.Headline, vm.Message));
        Assert.Equal($"{Board} - Expected step 10 SMT; this station does step 20.", vm.Detail);
        Assert.Equal([ScanState.Blocked], kiosk.Feedback.Played);
    }

    [Fact]
    public async Task Unknown_reason_code_falls_back_to_the_server_message()
    {
        var (_, vm) = Screen(Answer(new ScanResponse("Blocked", "Something new.", "BrandNewReason", null, null, false)));

        await ScanAsync(vm);

        Assert.Equal(("Something new.", Board), (vm.Message, vm.Detail));
    }

    // Every code route check can return has a text for the operator.
    [Fact]
    public void Every_block_reason_has_a_string()
    {
        var missing = Enum.GetNames<BlockReason>().Where(r => r != nameof(BlockReason.None))
            .Where(r => string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString("Block_" + r, Strings.Culture)));

        Assert.Empty(missing);
    }

    // Spec 2.8 trap: without the version check, the first scan's timer would wipe the second scan's red screen.
    [Fact]
    public async Task A_newer_scan_keeps_the_screen_until_its_own_flash_ends()
    {
        var (kiosk, vm) = Screen();
        await ScanAsync(vm);
        kiosk.Time.Advance(TimeSpan.FromSeconds(1));
        kiosk.Fake.Scan = Answer(new ScanResponse("Blocked", "x", "MustGoRepair", null, null, false));
        await ScanAsync(vm, "PCBA2610170000029");

        kiosk.Time.Advance(TimeSpan.FromSeconds(1));   // the first flash ends here
        await Task.Delay(20);
        Assert.Equal(ScanState.Blocked, vm.State);

        kiosk.Time.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => vm.State == ScanState.Idle);
    }

    // Spec 2.7 trap: a fast scanner sends the next serial before the first answer; neither may be dropped.
    [Fact]
    public async Task Second_scan_is_sent_while_the_first_still_waits()
    {
        var first = new TaskCompletionSource<ScanResponse>();
        var (kiosk, vm) = Screen((r, _) => r.SerialNo == Board ? first.Task : Task.FromResult(new ScanResponse("Ok", "x", null, 40, "PACK", false)));

        var pending = ScanAsync(vm);
        // The KeyBinding asks CanExecute before every Enter: with the toolkit default (no concurrency) it is
        // false while the first scan runs, and the second Enter would be dropped without a sound.
        Assert.True(vm.ScanCommand.CanExecute(null));
        await ScanAsync(vm, "PCBA2610170000029");
        first.SetResult(new ScanResponse("Ok", "x", null, 30, "TEST", false));
        await pending;

        Assert.Equal([Board, "PCBA2610170000029"], kiosk.Fake.Scans.Select(s => s.SerialNo));
    }

    [Fact]
    public async Task Empty_input_sends_nothing()
    {
        var (kiosk, vm) = Screen();

        await ScanAsync(vm, "   ");

        Assert.Empty(kiosk.Fake.Scans);
        Assert.Equal(ScanState.Idle, vm.State);
    }

    // Spec 2.9: no answer within 3 s -> result unknown, offline, scanning off until the health probe succeeds.
    [Fact]
    public async Task No_answer_in_time_goes_offline_until_the_api_is_back()
    {
        var (kiosk, vm) = Screen((_, ct) =>
        {
            var never = new TaskCompletionSource<ScanResponse>();
            ct.Register(() => never.TrySetCanceled(ct));
            return never.Task;
        });

        var scan = ScanAsync(vm);
        kiosk.Time.Advance(ScanViewModel.ScanTimeout);
        await scan;

        Assert.True(vm.IsOffline);
        Assert.Equal((ScanState.Blocked, Strings.Scan_NoResponse, Strings.Scan_ResultUnknown), (vm.State, vm.Headline, vm.Message));
        Assert.False(vm.ScanCommand.CanExecute(null));

        kiosk.Fake.Online = false;
        await vm.CheckConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.IsOffline);
        kiosk.Fake.Online = true;
        await vm.CheckConnectionCommand.ExecuteAsync(null);
        Assert.False(vm.IsOffline);
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Health_probe_failure_turns_scanning_off()
    {
        var (kiosk, vm) = Screen();
        kiosk.Fake.Online = false;

        await vm.CheckConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsOffline);
        Assert.False(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Network_error_goes_offline_but_a_refused_request_does_not()
    {
        var (_, down) = Screen((_, _) => Task.FromException<ScanResponse>(new ApiException(null, Strings.Error_CannotReachServer)));
        var (_, refused) = Screen((_, _) => Task.FromException<ScanResponse>(new ApiException(HttpStatusCode.NotFound, "Station 2 not found or inactive.")));

        await ScanAsync(down);
        await ScanAsync(refused);

        Assert.True(down.IsOffline);
        Assert.False(refused.IsOffline);
        Assert.Equal((ScanState.Blocked, "Station 2 not found or inactive."), (refused.State, refused.Message));
    }

    // ---------- [v2] SOP and FAI banner ----------

    private static WorkOrderDetailDto Detail(string? fai, string? asmSop) => new(
        5, "WO-2610-00005", 1, "PCB-A", "Controller board A", 1, "L1", 200,
        new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc),
        "InProgress", 1, fai is not null, fai, null, 1, 1, null, null, null, null, null, null, null, 0, 200, [], "AAAAAAAAB9E=", [],
        [new WorkOrderOperationDto(10, "SMT", 1, "WC-SMT", 30m, false, true, "/sop/PCB-A/SMT-v1.pdf", 0, 0),
         new WorkOrderOperationDto(20, "ASSY", 2, "WC-ASSY", 60m, false, true, asmSop, 0, 0)]);

    [Fact]
    public async Task Sop_of_the_station_step_opens_in_the_overlay_and_a_scan_closes_it()
    {
        var (kiosk, vm) = Screen();
        kiosk.Dispatch.Detail = Detail("Pending", "/sop/PCB-A/ASSY-v1.pdf");

        await vm.LoadAsync();
        await vm.OpenSopCommand.ExecuteAsync(null);

        Assert.True(vm.HasSop);
        Assert.True(vm.IsFaiPending);
        Assert.Equal("/sop/PCB-A/ASSY-v1.pdf", kiosk.Fake.SopAsked);   // ASSY: the step of L1-ASM-01, not SMT
        Assert.True(vm.IsSopOpen);
        Assert.Equal(kiosk.Fake.Sop, await File.ReadAllBytesAsync(vm.SopFile!));

        await ScanAsync(vm);

        Assert.False(vm.IsSopOpen);
        Assert.Equal(ScanState.Ok, vm.State);
    }

    [Fact]
    public async Task No_sop_hides_the_button_and_no_fai_hides_the_banner()
    {
        var (kiosk, vm) = Screen();
        kiosk.Dispatch.Detail = Detail(fai: null, asmSop: null);

        await vm.LoadAsync();

        Assert.False(vm.HasSop);
        Assert.False(vm.OpenSopCommand.CanExecute(null));
        Assert.False(vm.IsFaiPending);
    }

    [Fact]
    public async Task Fai_block_shows_the_banner()
    {
        var (_, vm) = Screen(Answer(new ScanResponse("Blocked", "First article not approved yet (waiting for QC).", "FaiNotApproved", null, null, false)));

        await ScanAsync(vm);

        Assert.True(vm.IsFaiPending);
        Assert.Equal(Strings.Block_FaiNotApproved, vm.Message);
    }
}
