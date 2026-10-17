using System.Net;
using System.Net.Http.Json;
using MiniMes.Contracts.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 5b: GET /api/stations/{id}/waiting-serials?take=n, what the machine simulator scans next.
[Collection(ApiCollection.Name)]
public class WaitingSerialsTests(ApiFixture api)
{
    private readonly ScanTests scan = new(api);

    [Fact]
    public async Task Boards_wait_at_their_next_step_oldest_serial_first()
    {
        var (wo, serials) = await scan.ReleasedAsync(5);
        var op = await api.LoginOperatorAsync();
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01" }) await scan.OpenSessionAsync(station, wo.Id);

        var before = await WaitingAsync(op, "L1-SMT-01");
        var firstThree = await WaitingAsync(op, "L1-SMT-01", take: 3);
        await scan.ScanAsync(op, "L1-SMT-01", serials[0]);
        var smtAfter = await WaitingAsync(op, "L1-SMT-01");
        var asm = await WaitingAsync(op, "L1-ASM-01");
        var testBefore = await WaitingAsync(op, "L1-TEST-01");
        await scan.ScanAsync(op, "L1-ASM-01", serials[0]);
        var test = await WaitingAsync(op, "L1-TEST-01");

        Assert.Equal([new WaitingSerialDto(serials[0], 10, "SMT")], before);
        Assert.Equal(serials[..3], firstThree.Select(w => w.SerialNo));
        Assert.Equal(serials[1], Assert.Single(smtAfter).SerialNo);
        Assert.Equal([new WaitingSerialDto(serials[0], 20, "ASSY")], asm);
        Assert.Empty(testBefore);
        Assert.Equal([new WaitingSerialDto(serials[0], 30, "TEST")], test);
    }

    // An NG board waits for Repair, not for the test it failed; the next good board is offered instead.
    [Fact]
    public async Task Ng_boards_do_not_wait_at_the_test()
    {
        var (wo, serials) = await scan.ReleasedAsync(2);
        var op = await api.LoginOperatorAsync();
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01" }) await scan.OpenSessionAsync(station, wo.Id);
        foreach (var board in serials.Take(2))
        {
            await scan.ScanAsync(op, "L1-SMT-01", board);
            await scan.ScanAsync(op, "L1-ASM-01", board);
        }

        await scan.ScanAsync(op, "L1-TEST-01", serials[0], "NG", "SLD-BRG");

        Assert.Equal(serials[1], Assert.Single(await WaitingAsync(op, "L1-TEST-01", take: 5)).SerialNo);
    }

    // [v2] While FAI is pending only the sample runs: before it is chosen any unstarted board may become it;
    // afterwards SMT has nothing to offer and the sample waits at the next step.
    [Fact]
    public async Task Pending_fai_offers_only_the_sample()
    {
        var (wo, serials) = await scan.ReleasedAsync(3, requireFai: true);
        var op = await api.LoginOperatorAsync();
        await scan.OpenSessionAsync("L1-SMT-01", wo.Id);
        await scan.OpenSessionAsync("L1-ASM-01", wo.Id);

        var before = await WaitingAsync(op, "L1-SMT-01", take: 5);
        await scan.ScanAsync(op, "L1-SMT-01", serials[0]);

        Assert.Equal(serials, before.Select(w => w.SerialNo));
        Assert.Empty(await WaitingAsync(op, "L1-SMT-01", take: 5));
        Assert.Equal(serials[0], Assert.Single(await WaitingAsync(op, "L1-ASM-01", take: 5)).SerialNo);
    }

    [Fact]
    public async Task Nothing_waits_without_a_session_or_off_the_routing_and_take_is_bounded()
    {
        var (wo, _) = await scan.ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        await scan.OpenSessionAsync("L1-REPAIR", wo.Id);
        var pack = await scan.StationIdAsync("L1-PACK-01");
        await op.DeleteAsync($"/api/stations/{pack}/sessions/{(await scan.OpenSessionAsync("L1-PACK-01", wo.Id)).Id}");

        Assert.Empty(await WaitingAsync(op, "L1-PACK-01"));    // logged out
        Assert.Empty(await WaitingAsync(op, "L1-REPAIR"));     // Repair has no routing step
        Assert.Equal(HttpStatusCode.BadRequest, (await op.GetAsync($"/api/stations/{pack}/waiting-serials?take=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.GetAsync($"/api/stations/{pack}/waiting-serials?take=101")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/stations/{int.MaxValue}/waiting-serials")).StatusCode);
    }

    private async Task<List<WaitingSerialDto>> WaitingAsync(HttpClient client, string stationCode, int take = 1)
        => (await client.GetFromJsonAsync<List<WaitingSerialDto>>($"/api/stations/{await scan.StationIdAsync(stationCode)}/waiting-serials?take={take}"))!;
}
