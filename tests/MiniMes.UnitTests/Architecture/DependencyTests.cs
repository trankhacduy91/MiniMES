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
            ["Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.Data.SqlClient", "Serilog"]),
        new("MiniMes.Infrastructure", ["MiniMes.Application"], []),
        new("MiniMes.Api", ["MiniMes.Application", "MiniMes.Infrastructure"], []),
        // Clients only talk to the API: no path to the database, ever.
        new("MiniMes.Client.Core", ["MiniMes.Contracts"],
            ["Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient"]),
        // WPF apps see only Client.Core, so they cannot open a DB connection even by mistake.
        new("MiniMes.BackOffice.Wpf", ["MiniMes.Client.Core"],
            ["Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient"]),
        new("MiniMes.Kiosk.Wpf", ["MiniMes.Client.Core"],
            ["Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient"]),
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

    // Client.Core is shared by both WPF apps but must stay plain net10.0 so its ViewModels
    // are unit-tested on the Linux CI runner.
    [Fact]
    public void Client_core_has_no_windows_dependency()
    {
        var csproj = LoadCsproj("MiniMes.Client.Core");

        Assert.Equal("net10.0", csproj.Descendants("TargetFramework").Single().Value.Trim());
        Assert.Empty(csproj.Descendants("UseWPF"));
        Assert.Empty(csproj.Descendants("UseWindowsForms"));
    }

    // MediatR 13+ is commercially licensed; the project stays on 12.x (Apache 2.0), see CLAUDE.md.
    [Fact]
    public void MediatR_stays_on_version_12()
    {
        var version = LoadCsproj("MiniMes.Application").Descendants("PackageReference")
            .Single(e => (string?)e.Attribute("Include") == "MediatR")
            .Attribute("Version")!.Value;

        Assert.StartsWith("12.", version);
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
