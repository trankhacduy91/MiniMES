using MiniMes.Contracts.Factory;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;

namespace MiniMes.UnitTests.Domain;

public class FactoryRulesTests
{
    [Theory]
    [InlineData("VN-BN01")]
    [InlineData("SMT-ASSY")]
    [InlineData("L1")]
    [InlineData("L1-REPAIR")]
    public void Valid_codes_are_accepted(string code)
        => FactoryRules.RequireCode(code);

    // Codes end up in MQTT topics: spaces, '/', '+', '#' and lowercase are not allowed.
    [Theory]
    [InlineData("L1 SMT")]
    [InlineData("l1-smt")]
    [InlineData("L1/SMT")]
    [InlineData("L1+")]
    [InlineData("L1#")]
    [InlineData("L1_SMT")]
    public void Invalid_codes_are_rejected(string code)
        => Assert.Throws<DomainException>(() => FactoryRules.RequireCode(code));

    [Fact]
    public void Code_longer_than_max_is_rejected()
        => Assert.Throws<DomainException>(() => FactoryRules.RequireCode(new string('A', FactoryRules.CodeMaxLength + 1)));

    [Fact]
    public void Inactive_site_cannot_get_new_area()
    {
        var site = new Site("VN-BN01", "Bac Ninh");
        site.SetActive(false);

        Assert.Throws<DomainException>(() => site.AddArea("SMT-ASSY", "SMT"));
    }

    [Fact]
    public void Inactive_line_cannot_get_new_station()
    {
        var line = new Site("VN-BN01", "Bac Ninh").AddArea("SMT-ASSY", "SMT").AddLine("L1", "Line 1");
        line.SetActive(false);

        Assert.Throws<DomainException>(() => line.AddStation("L1-SMT-01", "SMT", StationType.Smt));
    }

    // Contracts cannot reference Domain, so these two tests keep the duplicated rules in sync.
    [Fact]
    public void Contract_station_types_match_domain_enum()
        => Assert.Equal(Enum.GetNames<StationType>().Order(), StationTypes.All.Order());

    [Fact]
    public void Contract_code_pattern_matches_domain()
        => Assert.Equal(FactoryRules.CodePattern, CodeRules.Pattern);
}
