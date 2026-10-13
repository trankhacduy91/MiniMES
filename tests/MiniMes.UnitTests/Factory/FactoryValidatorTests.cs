using MiniMes.Application.Audit;
using MiniMes.Application.Factory;

namespace MiniMes.UnitTests.Factory;

// The M0 rules (formerly DataAnnotations on Contracts) now live in FluentValidation validators.
public class FactoryValidatorTests
{
    [Theory]
    [InlineData("L1 SMT")]   // space
    [InlineData("l1-smt")]   // lower case
    [InlineData("L1/SMT")]   // MQTT topic separator
    [InlineData("L1+SMT")]   // MQTT wildcard
    [InlineData("")]
    public void Bad_station_code_is_reported_on_the_code_field(string code)
    {
        var result = new CreateStationValidator().Validate(new CreateStationCommand(1, code, "x", "Smt"));

        Assert.Contains(result.Errors, e => e.PropertyName == "Code");
    }

    [Fact]
    public void Valid_station_passes()
        => Assert.True(new CreateStationValidator().Validate(new CreateStationCommand(1, "L1-SMT-01", "SMT", "Smt")).IsValid);

    [Theory]
    [InlineData("Oven")]
    [InlineData("smt")]  // enum names are case-sensitive, like Enum.Parse in the handler
    public void Unknown_station_type_is_rejected(string type)
    {
        var result = new UpdateStationValidator().Validate(new UpdateStationCommand(1, "x", type, true));

        Assert.Equal(["Type"], result.Errors.Select(e => e.PropertyName));
    }

    [Fact]
    public void Area_needs_a_site_id()
        => Assert.Contains(new CreateAreaValidator().Validate(new CreateAreaCommand(0, "SMT", "x")).Errors,
            e => e.PropertyName == "SiteId");

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public void Audit_paging_out_of_range_is_rejected_not_clamped(int page, int pageSize)
        => Assert.False(new GetAuditLogsValidator()
            .Validate(new GetAuditLogsQuery(null, null, null, null, page, pageSize)).IsValid);

    [Fact]
    public void Audit_range_must_go_forward()
    {
        var at = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(new GetAuditLogsValidator().Validate(new GetAuditLogsQuery(null, null, at, at, 1, 50)).IsValid);
    }
}
