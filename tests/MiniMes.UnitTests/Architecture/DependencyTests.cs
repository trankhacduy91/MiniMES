using System.Xml.Linq;

namespace MiniMes.UnitTests.Architecture;

// Reads .csproj instead of Assembly.GetReferencedAssemblies(): the compiler drops references
// that no code uses, so a wrong ProjectReference would stay invisible in an empty project.
public class DependencyTests
{
    private sealed record Rule(
        string Project,
        string[] AllowedProjects,
        string[]? ForbiddenPackagePrefixes); // null = no package at all

    // Add rows here when new projects appear (Client.Core, WPF apps from step 10).
    private static readonly Rule[] Rules =
    [
        new("MiniMes.Domain", [], null),
        new("MiniMes.Contracts", [], null),
        new("MiniMes.Application", ["MiniMes.Domain", "MiniMes.Contracts"],
            ["Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore.SqlServer", "Serilog"]),
        new("MiniMes.Infrastructure", ["MiniMes.Application"], []),
        new("MiniMes.Api", ["MiniMes.Application", "MiniMes.Infrastructure"], []),
    ];

    public static TheoryData<string> ProjectNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var rule in Rules) data.Add(rule.Project);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void Project_references_only_allowed_dependencies(string project)
    {
        var rule = Rules.Single(r => r.Project == project);
        var csproj = LoadCsproj(project);

        var projectRefs = csproj.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace("\\", "/")))
            .ToList();
        var packages = csproj.Descendants("PackageReference")
            .Select(e => (string)e.Attribute("Include")!)
            .ToList();
        var frameworks = csproj.Descendants("FrameworkReference")
            .Select(e => (string)e.Attribute("Include")!)
            .ToList();

        var badProjects = projectRefs.Except(rule.AllowedProjects).ToList();
        Assert.True(badProjects.Count == 0,
            $"{project} must not reference: {string.Join(", ", badProjects)}. " +
            $"Allowed: [{string.Join(", ", rule.AllowedProjects)}].");

        if (rule.ForbiddenPackagePrefixes is null)
        {
            Assert.True(packages.Count == 0 && frameworks.Count == 0,
                $"{project} must have no packages/frameworks, found: {string.Join(", ", packages.Concat(frameworks))}.");
            return;
        }

        var badPackages = packages.Concat(frameworks)
            .Where(p => rule.ForbiddenPackagePrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(badPackages.Count == 0,
            $"{project} must not use packages: {string.Join(", ", badPackages)}.");
    }

    [Fact]
    public void Domain_treats_warnings_as_errors()
    {
        var csproj = LoadCsproj("MiniMes.Domain");
        var value = csproj.Descendants("TreatWarningsAsErrors").Select(e => e.Value.Trim()).SingleOrDefault();

        Assert.Equal("true", value);
    }

    private static XDocument LoadCsproj(string project)
    {
        var path = Path.Combine(FindRepoRoot(), "src", project, $"{project}.csproj");
        Assert.True(File.Exists(path), $"Project file not found: {path}");
        return XDocument.Load(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiniMes.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("MiniMes.slnx not found above " + AppContext.BaseDirectory);
    }
}
