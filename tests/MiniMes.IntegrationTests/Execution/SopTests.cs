using System.Net;
using MiniMes.Infrastructure.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Execution;

// [v2] Spec M3 2.8b: GET /api/sop/{**path} serves the repo's docs/sop (the seeded routing points there).
[Collection(ApiCollection.Name)]
public class SopTests(ApiFixture api)
{
    [Fact]
    public async Task Operator_downloads_the_sop_of_a_seeded_step()
    {
        var response = await (await api.LoginOperatorAsync()).GetAsync("/api/sop/PCB-A/SMT-v1.pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync())[..4]);
    }

    [Fact]
    public async Task Unknown_file_is_404_and_a_token_is_required()
    {
        var unknown = await (await api.LoginOperatorAsync()).GetAsync("/api/sop/PCB-A/NOPE-v9.pdf");
        var anonymous = await api.Anonymous().GetAsync("/api/sop/PCB-A/SMT-v1.pdf");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // The store itself: nothing outside the folder, nothing but documents a kiosk shows.
    [Theory]
    [InlineData("PCB-A/SMT-v1.pdf", true)]
    [InlineData("PCB-A/../PCB-A/TEST-v1.pdf", true)]   // stays inside: fine
    [InlineData("../make-sop.cs", false)]               // leaves the folder
    [InlineData("make-sop.cs", false)]                  // inside, but not a document type
    [InlineData("../../src/MiniMes.Api/appsettings.json", false)]
    [InlineData("", false)]
    public void Store_serves_only_documents_inside_its_folder(string path, bool served)
    {
        var store = new SopFileStore(Path.Combine(RepoRoot(), "docs", "sop", "PCB-A", ".."));

        using var file = store.Open(path)?.Content;

        Assert.Equal(served, file is not null);
    }

    [Fact]
    public void Absolute_paths_are_refused()
        => Assert.Null(new SopFileStore(Path.Combine(RepoRoot(), "docs", "sop")).Open(Path.Combine(RepoRoot(), "README.md")));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiniMes.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
