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
        bom.Approve(Now, previous: null);

        Assert.Throws<DomainException>(() => bom.AddLine(3, 1m, 0m, 10));
        Assert.Single(bom.Lines);
    }

    [Fact]
    public void Approve_sets_status_and_effective_from()
    {
        var bom = DraftWithOneLine();

        bom.Approve(Now, previous: null);

        Assert.Equal(VersionStatus.Approved, bom.Status);
        Assert.Equal(Now, bom.EffectiveFrom);
        Assert.Null(bom.EffectiveTo);
    }

    [Fact]
    public void Empty_bom_cannot_be_approved()
        => Assert.Throws<DomainException>(() => Bom.CreateDraft(ParentId, 1).Approve(Now, previous: null));

    [Fact]
    public void Approving_twice_is_rejected()
    {
        var bom = DraftWithOneLine();
        bom.Approve(Now, previous: null);

        Assert.Throws<DomainException>(() => bom.Approve(Now, previous: null));
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

    // ---------- step 5: versions and validity [From, To) ----------

    private static Bom ApprovedV1(DateTime from)
    {
        var v1 = DraftWithOneLine();
        v1.Approve(from, previous: null);
        return v1;
    }

    private static Bom DraftV2()
    {
        var v2 = Bom.CreateDraft(ParentId, 2);
        v2.AddLine(2, 13m, 0.02m, 10);
        return v2;
    }

    [Fact]
    public void Approving_v2_closes_v1_exactly_where_v2_starts()
    {
        var v1 = ApprovedV1(Now);
        var v2 = DraftV2();

        v2.Approve(Now.AddDays(21), v1);

        Assert.Equal(Now.AddDays(21), v1.EffectiveTo);   // half-open: at that instant only v2 is valid
        Assert.Equal(Now.AddDays(21), v2.EffectiveFrom);
        Assert.Null(v2.EffectiveTo);
    }

    [Theory]
    [InlineData(0)]     // same instant: v1 would be valid for zero time
    [InlineData(-1)]    // before v1 started: the ranges would overlap
    public void V2_cannot_start_before_or_with_v1(int days)
    {
        var v1 = ApprovedV1(Now);
        var v2 = DraftV2();

        Assert.Throws<DomainException>(() => v2.Approve(Now.AddDays(days), v1));
        Assert.Null(v1.EffectiveTo);                      // nothing changed on failure
        Assert.Equal(VersionStatus.Draft, v2.Status);
    }

    [Fact]
    public void Previous_must_be_the_open_approved_version_of_the_same_item()
    {
        var otherItem = Bom.CreateDraft(99, 1);
        otherItem.AddLine(2, 1m, 0m, 10);
        otherItem.Approve(Now, previous: null);
        var draft = Bom.CreateDraft(ParentId, 1);
        draft.AddLine(2, 1m, 0m, 10);
        var closed = ApprovedV1(Now);
        DraftV2().Approve(Now.AddDays(1), closed);         // closes v1

        Assert.Throws<InvalidOperationException>(() => DraftV2().Approve(Now.AddDays(2), otherItem));
        Assert.Throws<InvalidOperationException>(() => DraftV2().Approve(Now.AddDays(2), draft));
        Assert.Throws<InvalidOperationException>(() => DraftV2().Approve(Now.AddDays(2), closed));
    }

    [Fact]
    public void Clearing_lines_of_an_approved_bom_is_rejected()
    {
        var v1 = ApprovedV1(Now);

        Assert.Throws<DomainException>(v1.ClearLines);
        Assert.Single(v1.Lines);
    }

    [Fact]
    public void Clearing_lines_of_a_draft_allows_saving_a_new_set()
    {
        var bom = DraftWithOneLine();

        bom.ClearLines();
        bom.AddLine(2, 20m, 0m, 10);   // same component again is fine after clearing

        Assert.Equal(20m, Assert.Single(bom.Lines).QtyPer);
    }
}
