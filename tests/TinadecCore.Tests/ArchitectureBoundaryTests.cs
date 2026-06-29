namespace TinadecCore.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void TinadecCoreIsTheOnlyCSharpCoreRuntime()
    {
        var root = FindRepoRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "TinadecCore", "TinadecCore.csproj"));
        var solution = File.ReadAllText(Path.Combine(root, "TinadecOffice.slnx"));

        Assert.Contains("Microsoft.NET.Sdk.Web", project);
        Assert.Contains("src/TinadecCore/TinadecCore.csproj", solution);
        Assert.DoesNotContain("Tinadec." + "Agent" + "Core", solution);
        Assert.DoesNotContain("Elysia", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Electron", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vue", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoreArchitectureDocsDoNotDescribeLegacyRuntimeAsASeparateLayer()
    {
        var root = FindRepoRoot();
        var architecture = File.ReadAllText(Path.Combine(root, "docs", "architecture.md"));

        Assert.DoesNotContain("Agent " + "Core", architecture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tinadec." + "Agent" + "Core", architecture, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TinadecOffice.slnx"))) return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find TinadecOffice repository root.");
    }
}
