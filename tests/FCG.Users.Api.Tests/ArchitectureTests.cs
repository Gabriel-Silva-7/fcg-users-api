using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace FCG.Users.Api.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Domain", new string[] { })]
    [InlineData("Application", new[] { "Domain" })]
    [InlineData("Infrastructure", new[] { "Application", "Domain" })]
    [InlineData("Api", new[] { "Application", "Infrastructure" })]
    public void Layer_references_follow_the_dependency_direction(string layer, string[] expected)
    {
        var project = XDocument.Load(ProjectPath(layer));
        var actual = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
            .Order();
        Assert.Equal(expected.Select(dependency => $"FCG.Users.{dependency}").Order(), actual);
    }

    [Fact]
    public void Domain_has_no_external_packages_or_infrastructure_frameworks()
    {
        var projectPath = ProjectPath("Domain");
        var project = XDocument.Load(projectPath);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        using var assets = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json")));
        Assert.Empty(assets.RootElement.GetProperty("libraries").EnumerateObject());
        var references = Assembly.Load("FCG.Users.Domain").GetReferencedAssemblies();
        Assert.All(references, reference => Assert.True(reference.Name!.StartsWith("System.", StringComparison.Ordinal),
            $"Domain must remain independent: {reference.Name}"));
    }

    private static string ProjectPath(string layer)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FCG.Users.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", $"FCG.Users.{layer}", $"FCG.Users.{layer}.csproj");
    }
}
