using MiniMes.Domain.Common;
using MiniMes.Domain.Quality;

namespace MiniMes.UnitTests.Quality;

// Spec M7 step 8: every transition of the [v2] 4-state NCR and every rule it refuses (2.5, Q11-Q14).
public class NcrTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc);

    private static Ncr New(params (HoldTargetType, long)[] targets)
        => new("NCR-000001", NcrSource.SpcAlert, "17", null, "Run of 7 on 3V3_RAIL", 87,
            targets.Length == 0 ? [(HoldTargetType.Serial, 5)] : targets);

    private static Ncr UnderReview(params (HoldTargetType, long)[] targets)
    {
        var ncr = New(targets);
        ncr.StartReview("qc01");
        return ncr;
    }

    private static Ncr Proposed(NcrDisposition d = NcrDisposition.Rework, params (HoldTargetType, long)[] targets)
    {
        var ncr = UnderReview(targets);
        ncr.ProposeDisposition(d, "Rework U3 solder joints", null, "qc01", T0);
        return ncr;
    }

    private static Ncr Dispositioned()
    {
        var ncr = Proposed();
        ncr.Approve("sup01", T0.AddMinutes(5));
        return ncr;
    }

    [Fact]
    public void The_happy_path_is_open_review_disposition_close_with_two_people()
    {
        var ncr = New();
        Assert.Equal(NcrStatus.Open, ncr.Status);

        ncr.StartReview("qc01");
        ncr.ProposeDisposition(NcrDisposition.Rework, " Rework U3 ", null, "qc01", T0);
        Assert.Equal(NcrStatus.UnderReview, ncr.Status);                       // proposing is not a state [v2]
        ncr.Approve("sup01", T0.AddMinutes(5));
        ncr.Close("sup01", activeHolds: 0, " CAPA-26-014 ", T0.AddHours(1));

        Assert.Equal((NcrStatus.Closed, NcrDisposition.Rework, "Rework U3", "qc01", "qc01", "sup01", "CAPA-26-014"),
            (ncr.Status, ncr.Disposition, ncr.DispositionNote, ncr.ReviewedBy, ncr.ProposedBy, ncr.ApprovedBy, ncr.CapaReference));
        Assert.Equal((T0, T0.AddMinutes(5), T0.AddHours(1)), (ncr.ProposedAt, ncr.ApprovedAt, ncr.ClosedAt));
    }

    // Q11: no self-approval, compared by user code, whatever the case.
    [Theory]
    [InlineData("qc01")]
    [InlineData("QC01")]
    public void The_proposer_cannot_approve(string approver)
    {
        var ncr = Proposed();
        Assert.Throws<DomainException>(() => ncr.Approve(approver, T0));
        Assert.Equal(NcrStatus.UnderReview, ncr.Status);
    }

    [Fact]
    public void Approve_needs_a_proposal()
        => Assert.Throws<DomainException>(() => UnderReview().Approve("sup01", T0));

    // Q13: return to vendor is for lots only.
    [Fact]
    public void Return_to_vendor_is_refused_when_a_target_is_a_serial()
    {
        var mixed = UnderReview((HoldTargetType.Lot, 3), (HoldTargetType.Serial, 5));
        Assert.Throws<DomainException>(() => mixed.ProposeDisposition(NcrDisposition.ReturnToVendor, "Wrong value", null, "qc01", T0));
        Assert.Null(mixed.Disposition);

        var lots = UnderReview((HoldTargetType.Lot, 3), (HoldTargetType.Lot, 4));
        lots.ProposeDisposition(NcrDisposition.ReturnToVendor, "Wrong value, SCAR-2611-03", null, "qc01", T0);
        Assert.Equal(NcrDisposition.ReturnToVendor, lots.Disposition);
    }

    // Q14: a note always; a concession reference only with use-as-is.
    [Fact]
    public void A_proposal_needs_a_note_and_the_concession_belongs_to_use_as_is()
    {
        var ncr = UnderReview();
        Assert.Throws<DomainException>(() => ncr.ProposeDisposition(NcrDisposition.UseAsIs, " ", null, "qc01", T0));
        Assert.Throws<DomainException>(() => ncr.ProposeDisposition(NcrDisposition.Scrap, "Burnt", "DEV-2611-01", "qc01", T0));

        ncr.ProposeDisposition(NcrDisposition.UseAsIs, "Within the customer's deviation", " DEV-2611-01 ", "qc01", T0);
        Assert.Equal("DEV-2611-01", ncr.ConcessionRef);
    }

    // [v2] QC may change the proposal while UnderReview; the latest one counts, with its author.
    [Fact]
    public void A_new_proposal_replaces_the_previous_one()
    {
        var ncr = Proposed(NcrDisposition.UseAsIs);
        ncr.ProposeDisposition(NcrDisposition.Scrap, "Burnt pads", null, "qc02", T0.AddMinutes(1));

        Assert.Equal((NcrDisposition.Scrap, "qc02", (DateTime?)T0.AddMinutes(1)), (ncr.Disposition, ncr.ProposedBy, ncr.ProposedAt));
        ncr.Approve("qc01", T0.AddMinutes(2));                                 // qc01 no longer the proposer
        Assert.Equal(NcrStatus.Dispositioned, ncr.Status);
    }

    [Fact]
    public void Reject_proposal_wipes_it_and_stays_under_review()
    {
        var ncr = Proposed();
        ncr.RejectProposal("sup01");

        Assert.Equal((NcrStatus.UnderReview, (NcrDisposition?)null, (string?)null, (string?)null), (ncr.Status, ncr.Disposition, ncr.DispositionNote, ncr.ProposedBy));
        Assert.Throws<DomainException>(() => ncr.RejectProposal("sup01"));    // nothing left to reject
        Assert.Throws<DomainException>(() => ncr.Approve("sup01", T0));
    }

    // Q12: not closed while a hold of this NCR is active.
    [Fact]
    public void Close_is_refused_while_holds_are_active()
    {
        var ncr = Dispositioned();
        Assert.Throws<DomainException>(() => ncr.Close("sup01", activeHolds: 3, null, T0));
        Assert.Equal(NcrStatus.Dispositioned, ncr.Status);
    }

    [Fact]
    public void Cancel_from_open_or_under_review_with_a_reason_and_no_active_hold()
    {
        var open = New();
        open.Cancel("qc01", "Duplicate of NCR-000002", 0, T0);
        Assert.Equal((NcrStatus.Cancelled, "Duplicate of NCR-000002"), (open.Status, open.CancelReason));

        var review = Proposed();
        Assert.Throws<DomainException>(() => review.Cancel("qc01", " ", 0, T0));
        Assert.Throws<DomainException>(() => review.Cancel("qc01", "Opened by mistake", activeHolds: 1, T0));
        review.Cancel("qc01", "Opened by mistake", 0, T0);
        Assert.Equal(NcrStatus.Cancelled, review.Status);

        Assert.Throws<DomainException>(() => Dispositioned().Cancel("qc01", "Too late", 0, T0));   // effects applied
    }

    // Every transition that is not on the diagram.
    public static TheoryData<string, Action<Ncr>> Illegal => new()
    {
        { "Open: propose", n => n.ProposeDisposition(NcrDisposition.Scrap, "x", null, "qc01", T0) },
        { "Open: approve", n => n.Approve("sup01", T0) },
        { "Open: reject", n => n.RejectProposal("sup01") },
        { "Open: close", n => n.Close("sup01", 0, null, T0) },
    };

    [Theory]
    [MemberData(nameof(Illegal))]
    public void Transitions_not_on_the_diagram_are_refused_from_open(string name, Action<Ncr> act)
    {
        var ncr = New();
        Assert.Throws<DomainException>(() => act(ncr));
        Assert.Equal(NcrStatus.Open, ncr.Status);
        _ = name;
    }

    [Fact]
    public void Nothing_moves_a_closed_or_cancelled_ncr()
    {
        var closed = Dispositioned();
        closed.Close("sup01", 0, null, T0);
        var cancelled = New();
        cancelled.Cancel("qc01", "Mistake", 0, T0);

        foreach (var ncr in new[] { closed, cancelled })
        {
            Assert.Throws<DomainException>(() => ncr.StartReview("qc01"));
            Assert.Throws<DomainException>(() => ncr.ProposeDisposition(NcrDisposition.Scrap, "x", null, "qc01", T0));
            Assert.Throws<DomainException>(() => ncr.Approve("sup01", T0));
            Assert.Throws<DomainException>(() => ncr.Close("sup01", 0, null, T0));
            Assert.Throws<DomainException>(() => ncr.Cancel("qc01", "again", 0, T0));
        }
        Assert.Throws<DomainException>(() => UnderReview().StartReview("qc01"));
        Assert.Throws<DomainException>(() => Dispositioned().Approve("sup02", T0));
    }

    [Fact]
    public void An_alert_linked_to_a_cancelled_ncr_can_be_linked_again()
    {
        var alert = new SpcAlert(1, SpcRuleCode.WE4, 10, T0);
        alert.LinkNcr(7);
        Assert.Throws<DomainException>(() => alert.LinkNcr(8));
        Assert.Throws<DomainException>(() => alert.UnlinkNcr(8));

        alert.UnlinkNcr(7);
        alert.LinkNcr(8);
        Assert.Equal(8, alert.NcrId);
    }
}
