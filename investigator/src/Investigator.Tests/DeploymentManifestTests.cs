using System.Text.RegularExpressions;
using Investigator.Models;
using Microsoft.Extensions.Configuration;

namespace Investigator.Tests;

/// <summary>
/// The OpenShift manifests are the only place production configuration is expressed, and
/// nothing else checks them. Startup now fails hard on a missing Vertex ProjectId, so a
/// profile added to appsettings.json without a matching env var takes the pod down.
/// </summary>
public class DeploymentManifestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "openshift", "deployment.yaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate the repository root");
    }

    private static string Deployment() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "openshift", "deployment.yaml"));

    private static IReadOnlyList<string> ConfiguredProfiles()
    {
        var options = new LlmOptions();
        new ConfigurationBuilder()
            .SetBasePath(Path.Combine(RepoRoot(), "src", "Investigator"))
            .AddJsonFile("appsettings.json")
            .Build()
            .GetSection(LlmOptions.Section)
            .Bind(options);

        return options.Models.Keys.ToList();
    }

    /// <summary>Reads a literal `- name: X` / `value: "Y"` pair out of the manifest.</summary>
    private static string? EnvValue(string manifest, string name)
    {
        var match = Regex.Match(
            manifest,
            $@"^\s*-\s*name:\s*{Regex.Escape(name)}\s*\r?\n\s*value:\s*""?([^""\r\n]+)""?",
            RegexOptions.Multiline);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [Fact]
    public void EveryConfiguredProfile_HasAProjectIdInTheManifest()
    {
        var manifest = Deployment();

        Assert.NotEmpty(ConfiguredProfiles());
        Assert.All(ConfiguredProfiles(), profile =>
        {
            var value = EnvValue(manifest, $"Llm__Models__{profile}__ProjectId");
            Assert.False(string.IsNullOrWhiteSpace(value),
                $"openshift/deployment.yaml has no Llm__Models__{profile}__ProjectId; "
                + "the pod will fail to start.");
        });
    }

    [Fact]
    public void EveryConfiguredProfile_HasARegionInTheManifest()
    {
        var manifest = Deployment();

        Assert.All(ConfiguredProfiles(), profile =>
            Assert.False(string.IsNullOrWhiteSpace(
                EnvValue(manifest, $"Llm__Models__{profile}__Region"))));
    }

    [Fact]
    public void AllProfilesShareOneProjectAndRegion()
    {
        var manifest = Deployment();

        var projects = ConfiguredProfiles()
            .Select(p => EnvValue(manifest, $"Llm__Models__{p}__ProjectId"))
            .Distinct()
            .ToList();

        var regions = ConfiguredProfiles()
            .Select(p => EnvValue(manifest, $"Llm__Models__{p}__Region"))
            .Distinct()
            .ToList();

        // A profile pointed at the wrong project fails only when a scout is dispatched to
        // it, long after the pod looks healthy.
        Assert.Single(projects);
        Assert.Single(regions);
    }

    [Fact]
    public void ManifestPinsVertexAsTheProvider()
    {
        var manifest = Deployment();

        Assert.All(ConfiguredProfiles(), profile =>
            Assert.Equal("vertex", EnvValue(manifest, $"Llm__Models__{profile}__Provider")));

        // Bedrock support was deleted; an env var reintroducing it would throw at runtime.
        Assert.DoesNotContain("bedrock", manifest, StringComparison.OrdinalIgnoreCase);
    }
}
