using MiniMes.Domain.Material;

namespace MiniMes.UnitTests.Material;

// Spec M6 2.5 / R8: the gate before a scan OK. Items 1 and 2 are consumed at the step.
public class MaterialGateRuleTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);

    private static LoadedLot Lot(int itemId, MaterialLotStatus status = MaterialLotStatus.Available, DateTime? expiry = null)
        => new(itemId, $"L-{itemId}", status, expiry);

    public static TheoryData<string, LoadedLot[], string> Cases() => new()
    {
        { "both loaded", [Lot(1), Lot(2)], "" },
        { "nothing loaded", [], "1:NotLoaded 2:NotLoaded" },
        { "one missing", [Lot(1)], "2:NotLoaded" },
        { "other item loaded does not count", [Lot(1), Lot(3)], "2:NotLoaded" },
        { "consumed still passes (theoretical count, R10)", [Lot(1, MaterialLotStatus.Consumed), Lot(2)], "" },
        { "hold after loading", [Lot(1, MaterialLotStatus.Hold), Lot(2)], "1:LotOnHold" },
        { "pending IQC", [Lot(1, MaterialLotStatus.PendingIqc), Lot(2)], "1:LotNotUsable" },
        { "IQC rejected", [Lot(1), Lot(2, MaterialLotStatus.IqcRejected)], "2:LotNotUsable" },
        { "expired a minute ago", [Lot(1, expiry: Now.AddMinutes(-1)), Lot(2)], "1:LotExpired" },
        { "expires exactly now (boundary as MaterialVerifier)", [Lot(1, expiry: Now), Lot(2)], "1:LotExpired" },
        { "expires in a minute", [Lot(1, expiry: Now.AddMinutes(1)), Lot(2)], "" },
        { "hold wins over expired", [Lot(1, MaterialLotStatus.Hold, Now.AddDays(-1)), Lot(2)], "1:LotOnHold" },
        { "several problems, by item", [Lot(2, MaterialLotStatus.Hold)], "1:NotLoaded 2:LotOnHold" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
#pragma warning disable xUnit1026 // scenario names the case in the test explorer
    public void Check(string scenario, LoadedLot[] loaded, string expected)
#pragma warning restore xUnit1026
    {
        var findings = MaterialGateRule.Check([1, 2], loaded, Now);

        Assert.Equal(expected, string.Join(" ", findings.Select(f => $"{f.ItemId}:{f.Problem}")));
    }

    [Fact]
    public void A_step_that_consumes_nothing_always_passes()
        => Assert.Empty(MaterialGateRule.Check([], [], Now));

    [Fact]
    public void Finding_names_the_lot_on_the_station()
        => Assert.Equal("L-1", MaterialGateRule.Check([1], [Lot(1, MaterialLotStatus.Hold)], Now).Single().LotNo);
}
