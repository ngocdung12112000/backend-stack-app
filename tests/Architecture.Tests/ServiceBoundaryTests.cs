using System.Xml.Linq;

namespace Architecture.Tests;

/// <summary>
/// Enforces the repo's dependency rules by inspecting ProjectReference items in every csproj:
/// services never reference each other, and layers inside a service only point inwards.
/// </summary>
public class ServiceBoundaryTests
{
    private static readonly string Root = FindRepoRoot();

    private sealed record Proj(string Path, string Name, string? Service, string? Layer, IReadOnlyList<string> References);

    private static readonly Lazy<IReadOnlyList<Proj>> Projects = new(LoadProjects);

    [Fact]
    public void Scan_FindsServiceProjects()
    {
        // Guards against the rules below passing vacuously.
        Assert.Contains(Projects.Value, p => p.Service == "catalog" && p.Layer == "Api");
        Assert.Contains(Projects.Value, p => p.Service == "identity" && p.Layer == "Api");
    }

    [Fact]
    public void Services_DoNotReference_OtherServices()
    {
        var violations = Projects.Value
            .Where(p => p.Service is not null)
            .SelectMany(p => p.References
                .Select(r => (p, Target: ServiceOf(r)))
                .Where(x => x.Target is not null && x.Target != p.Service))
            .Select(x => $"{x.p.Name} (service '{x.p.Service}') references service '{x.Target}'")
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void BuildingBlocks_DoNotReference_Services()
    {
        var violations = Projects.Value
            .Where(p => p.Path.Contains($"{Path.DirectorySeparatorChar}building-blocks{Path.DirectorySeparatorChar}"))
            .Where(p => p.References.Any(r => ServiceOf(r) is not null))
            .Select(p => p.Name)
            .ToList();

        Assert.True(violations.Count == 0, "Building blocks must not depend on services: " + string.Join(", ", violations));
    }

    [Fact]
    public void Domain_HasNo_ServiceLayerDependencies()
    {
        AssertLayerRule("Domain", forbidden: ["Application", "Infrastructure", "Api"]);
    }

    [Fact]
    public void Application_DoesNotReference_InfrastructureOrApi()
    {
        AssertLayerRule("Application", forbidden: ["Infrastructure", "Api"]);
    }

    [Fact]
    public void Infrastructure_DoesNotReference_Api()
    {
        AssertLayerRule("Infrastructure", forbidden: ["Api"]);
    }

    private static void AssertLayerRule(string layer, string[] forbidden)
    {
        var violations = Projects.Value
            .Where(p => p.Layer == layer)
            .SelectMany(p => p.References
                .Select(r => (p, Target: LayerOf(r)))
                .Where(x => x.Target is not null && forbidden.Contains(x.Target)))
            .Select(x => $"{x.p.Name} must not reference a {x.Target} project")
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // ---- helpers -------------------------------------------------------------------------

    private static string? ServiceOf(string projectPath) => Segment(projectPath, "services");

    private static string? LayerOf(string projectPath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(projectPath);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? null : name[(dot + 1)..] switch
        {
            "Domain" or "Application" or "Infrastructure" or "Api" => name[(dot + 1)..],
            _ => null,
        };
    }

    /// <summary>Returns the folder name right after <paramref name="folder"/> in a path, if present.</summary>
    private static string? Segment(string path, string folder)
    {
        var parts = path.Split('\\', '/');
        var i = Array.IndexOf(parts, folder);
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    private static IReadOnlyList<Proj> LoadProjects() =>
        Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")
                     && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"))
            .Select(f =>
            {
                var dir = System.IO.Path.GetDirectoryName(f)!;
                var refs = XDocument.Load(f).Descendants("ProjectReference")
                    .Select(e => (string?)e.Attribute("Include"))
                    .Where(i => i is not null)
                    .Select(i => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, i!.Replace('\\', System.IO.Path.DirectorySeparatorChar))))
                    .ToList();
                var relative = System.IO.Path.GetRelativePath(Root, f);
                var service = Segment(relative, "services");
                return new Proj(f, System.IO.Path.GetFileNameWithoutExtension(f), service, service is null ? null : LayerOf(f), refs);
            })
            .ToList();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "StackShop.Backend.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("StackShop.Backend.sln not found above test output.");
    }
}
