using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Boms;

namespace MiniMes.UnitTests.MasterData;

public class BomTests
{
    private const int ParentId = 1;
    private static readonly DateTime Now = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    private static Bom DraftWithOneLine()
    {
        var bom = Bom.CreateDraft(ParentId, 1);
        bom.AddLine(componentItemId: 2, qtyPer: 12m, scrapRate: 0.02m, operationSeq: 10);
        return bom;
    }

    // spec step 2: Bom.AddLine on an Approved BOM must fail (approved = locked, change = new version).
    [Fact]
    public void Approved_bom_cannot_get_new_lines()
    {
        var bom = DraftWithOneLine();
        bom.Approve(Now);

        Assert.Throws<DomainException>(() => bom.AddLine(3, 1m, 0m, 10));
        Assert.Single(bom.Lines);
    }

    [Fact]
    public void Approve_sets_status_and_effective_from()
    {
        var bom = DraftWithOneLine();

        bom.Approve(Now);

        Assert.Equal(VersionStatus.Approved, bom.Status);
        Assert.Equal(Now, bom.EffectiveFrom);
        Assert.Null(bom.EffectiveTo);
    }

    [Fact]
    public void Empty_bom_cannot_be_approved()
        => Assert.Throws<DomainException>(() => Bom.CreateDraft(ParentId, 1).Approve(Now));

    [Fact]
    public void Approving_twice_is_rejected()
    {
        var bom = DraftWithOneLine();
        bom.Approve(Now);

        Assert.Throws<DomainException>(() => bom.Approve(Now));
    }

    [Fact]
    public void Line_cannot_use_the_parent_itself()
        => Assert.Throws<DomainException>(() => Bom.CreateDraft(ParentId, 1).AddLine(ParentId, 1m, 0m, 10));

    [Fact]
    public void Same_component_twice_is_rejected()
    {
        var bom = DraftWithOneLine();

        Assert.Throws<DomainException>(() => bom.AddLine(2, 5m, 0m, 20));
    }

    [Theory]
    [InlineData(0, 0, 10)]       // QtyPer must be > 0
    [InlineData(-1, 0, 10)]
    [InlineData(1, -0.01, 10)]   // ScrapRate is a fraction in [0, 1)
    [InlineData(1, 1, 10)]
    [InlineData(1, 2, 10)]       // the classic mistake: 2 meaning 2%
    [InlineData(1, 0, 0)]        // OperationSeq must be > 0
    public void Invalid_line_values_are_rejected(decimal qtyPer, decimal scrapRate, int seq)
        => Assert.Throws<DomainException>(() => Bom.CreateDraft(ParentId, 1).AddLine(2, qtyPer, scrapRate, seq));

    [Fact]
    public void Version_starts_at_one()
        => Assert.Throws<DomainException>(() => Bom.CreateDraft(ParentId, 0));
}
