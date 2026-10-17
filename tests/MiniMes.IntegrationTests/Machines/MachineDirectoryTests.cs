using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Contracts.Machines;
using MiniMes.Infrastructure.Machines;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 3: topic -> machine from master data; the whole UNS path must match.
[Collection(ApiCollection.Name)]
public class MachineDirectoryTests(ApiFixture api)
{
    private MachineDirectory Directory => api.Factory.Services.GetRequiredService<MachineDirectory>();

    [Fact]
    public async Task Seeded_machine_resolves_by_its_full_path()
    {
        var machine = await Directory.ResolveAsync(Topic("VN-BN01", "SMT-ASSY", "L1", "L1-SMT-01"), default);

        var expectedId = await api.WithDbAsync(db => db.Machines.Where(m => m.Code == "L1-SMT-01").Select(m => m.Id).SingleAsync());
        Assert.Equal((expectedId, "L1-SMT-01", "L1"), (machine!.Id, machine.Code, machine.Line));
    }

    [Theory]
    [InlineData("VN-BN01", "SMT-ASSY", "L2", "L1-SMT-01")]   // right machine, wrong line
    [InlineData("HN1", "SMT-ASSY", "L1", "L1-SMT-01")]       // wrong site
    [InlineData("VN-BN01", "SMT-ASSY", "L1", "l1-smt-01")]   // MQTT topics are case-sensitive
    [InlineData("VN-BN01", "SMT-ASSY", "L1", "L1-NOPE-01")]
    public async Task Anything_else_is_unknown(string site, string area, string line, string machine)
        => Assert.Null(await Directory.ResolveAsync(Topic(site, area, line, machine), default));

    private static ParsedTopic Topic(string site, string area, string line, string machine)
        => new(site, area, line, machine, MachineKinds.State, null);
}
