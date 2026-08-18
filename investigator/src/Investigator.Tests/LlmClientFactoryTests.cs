using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Investigator.Tests;

public class LlmClientFactoryTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static LlmClientFactory Build(params (string Name, ModelOptions Model)[] profiles)
    {
        var options = new LlmOptions
        {
            Primary = profiles[0].Name,
            Default = profiles[0].Name,
            Models = profiles.ToDictionary(p => p.Name, p => p.Model),
        };

        return new LlmClientFactory(
            Options.Create(options), new StubHttpClientFactory(), NullLoggerFactory.Instance);
    }

    private static ModelOptions Vertex(string? projectId = "proj-1", string? region = "us-east5") =>
        new()
        {
            Provider = "vertex",
            ProjectId = projectId,
            Region = region,
            Model = "claude-opus-4-6",
        };

    [Fact]
    public void FullyConfiguredVertexProfile_LoadsCleanly()
    {
        var factory = Build(("claude-opus", Vertex()));

        Assert.Equal("claude-opus", factory.PrimaryProfileName);
        Assert.Single(factory.Models);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingProjectId_FailsAtStartup_NotMidInvestigation(string? projectId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(("claude-opus", Vertex(projectId: projectId))));

        Assert.Contains("ProjectId", ex.Message);
        // The message has to say where to set it; this is what an operator sees in logs.
        Assert.Contains("Llm__Models__claude-opus__ProjectId", ex.Message);
    }

    [Fact]
    public void MissingRegion_AlsoFailsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(("claude-opus", Vertex(region: null))));

        Assert.Contains("Region", ex.Message);
    }

    [Fact]
    public void AnyMisconfiguredProfileFails_NotJustThePrimary()
    {
        // A secondary profile is reachable via the delegate tool's model argument, so it
        // must be validated too or a scout blows up on dispatch.
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            ("claude-opus", Vertex()),
            ("claude-sonnet", Vertex(projectId: null))));

        Assert.Contains("claude-sonnet", ex.Message);
    }

    [Fact]
    public void UnknownPrimaryProfile_IsRejected()
    {
        var options = new LlmOptions
        {
            Primary = "does-not-exist",
            Default = "claude-opus",
            Models = new Dictionary<string, ModelOptions> { ["claude-opus"] = Vertex() },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new LlmClientFactory(
            Options.Create(options), new StubHttpClientFactory(), NullLoggerFactory.Instance));

        Assert.Contains("does-not-exist", ex.Message);
    }
}
