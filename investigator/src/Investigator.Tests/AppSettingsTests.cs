using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Investigator.Tests;

/// <summary>
/// Loads the real shipped configuration. A broken appsettings.json only shows up at pod
/// start otherwise, and the layering between the base file and the Development override
/// is easy to get subtly wrong.
/// </summary>
public class AppSettingsTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static string ProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Investigator", "appsettings.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate src/Investigator from the test output");
    }

    private static LlmOptions Load(string? environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(ProjectDir())
            .AddJsonFile("appsettings.json", optional: false);

        if (environment is not null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);

        var options = new LlmOptions();
        builder.Build().GetSection(LlmOptions.Section).Bind(options);
        return options;
    }

    [Fact]
    public void BaseConfiguration_TargetsVertexOnly()
    {
        var llm = Load(environment: null);

        Assert.NotEmpty(llm.Models);
        Assert.All(llm.Models.Values, m =>
            Assert.Equal("vertex", m.Provider, ignoreCase: true));

        // Bedrock support was removed; a lingering provider entry would fail at runtime.
        Assert.DoesNotContain("bedrock", llm.Providers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BaseConfiguration_LeavesProjectIdUnsetSoDeploymentsMustBeExplicit()
    {
        var llm = Load(environment: null);

        Assert.All(llm.Models.Values, m => Assert.True(string.IsNullOrWhiteSpace(m.ProjectId)));
    }

    [Fact]
    public void PrimaryAndDefaultProfiles_Exist()
    {
        var llm = Load(environment: null);

        Assert.Contains(llm.Primary, llm.Models.Keys);
        Assert.Contains(llm.Default, llm.Models.Keys);
    }

    [Fact]
    public void DevelopmentOverride_SuppliesEverythingTheFactoryRequires()
    {
        // Base leaves ProjectId blank on purpose, so the layered Development file has to
        // fill it in or `dotnet run` fails at startup.
        var llm = Load("Development");

        var factory = new LlmClientFactory(
            Options.Create(llm), new StubHttpClientFactory(), NullLoggerFactory.Instance);

        Assert.Equal(llm.Primary, factory.PrimaryProfileName);
        Assert.All(llm.Models.Values, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.ProjectId));
            Assert.False(string.IsNullOrWhiteSpace(m.Region));
        });
    }

    [Fact]
    public void DevelopmentOverride_KeepsBaseValuesItDoesNotRestate()
    {
        // Dictionary sections merge per key rather than replacing wholesale; token limits
        // and prices live only in the base file.
        var llm = Load("Development");

        var opus = llm.Models[llm.Primary];
        Assert.True(opus.MaxTokens > 0);
        Assert.True(opus.InputPricePerMToken > 0);
    }
}
