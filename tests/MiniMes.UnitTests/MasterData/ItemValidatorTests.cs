using MiniMes.Application.MasterData.Items;

namespace MiniMes.UnitTests.MasterData;

// Validators are plain classes: testable without MediatR, HTTP or a database.
public class ItemValidatorTests
{
    private static CreateItemCommand Valid(string type = "Raw", string uom = "pcs", bool serialized = false,
        bool lot = true, bool skipIqc = false, string code = "RES-10K-0402")
        => new(code, "Resistor 10k 0402", type, uom, serialized, lot, skipIqc);

    private static List<string> Errors(CreateItemCommand cmd)
        => new CreateItemValidator().Validate(cmd).Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}").ToList();

    [Fact]
    public void Valid_raw_item_passes() => Assert.Empty(Errors(Valid()));

    [Fact]
    public void Raw_item_cannot_be_serialized()
    {
        var errors = Errors(Valid(serialized: true));

        Assert.Equal(["IsSerialized: A Raw item cannot be serialized."], errors);
    }

    [Fact]
    public void Finished_item_must_be_serialized()
        => Assert.Contains("IsSerialized: A Finished item must be serialized.", Errors(Valid("Finished", lot: false)));

    [Theory]
    [InlineData("res-10k")]   // lower case
    [InlineData("RES 10K")]
    [InlineData("")]
    public void Bad_code_is_reported_on_the_code_field(string code)
        => Assert.Contains(Errors(Valid(code: code)), e => e.StartsWith("Code:"));

    [Fact]
    public void Unknown_unit_is_reported()
        => Assert.Contains(Errors(Valid(uom: "xyz")), e => e.StartsWith("Uom:"));

    [Fact]
    public void Unknown_type_is_reported_and_flag_rules_are_skipped()
        => Assert.Equal(["Type: Type must be Raw, Semi or Finished."], Errors(Valid(type: "Gadget")));

    [Fact]
    public void Skip_iqc_only_for_raw()
        => Assert.Contains("SkipIqc: SkipIqc is only valid for Raw items.",
            Errors(Valid("Finished", serialized: true, lot: false, skipIqc: true)));

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public void Paging_out_of_range_is_rejected(int page, int pageSize)
        => Assert.False(new GetItemsValidator().Validate(new GetItemsQuery(null, null, null, page, pageSize)).IsValid);
}
