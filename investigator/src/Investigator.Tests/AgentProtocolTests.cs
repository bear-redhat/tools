using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Tests;

/// <summary>
/// Two independent call sites depend on this rule agreeing: InvestigationRoom uses it to
/// end the agent's turn, and ProjectedEventLog uses it to report an outstanding question.
/// If they diverge, a parked investigation looks like a working one.
/// </summary>
public class AgentProtocolTests
{
    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    [Theory]
    [InlineData("user")]
    [InlineData("client")]
    public void MessageAddressedToTheHuman_IsAQuestion(string recipient)
    {
        Assert.True(AgentProtocol.IsQuestionForHuman("message", recipient));
        Assert.True(AgentProtocol.IsQuestionForHuman("message", Input(new { to = recipient })));
    }

    [Theory]
    [InlineData("sharp-badger")]
    [InlineData("little-bear")]
    [InlineData("")]
    public void MessageAddressedToAnAgent_IsNot(string recipient)
    {
        Assert.False(AgentProtocol.IsQuestionForHuman("message", recipient));
        Assert.False(AgentProtocol.IsQuestionForHuman("message", Input(new { to = recipient })));
    }

    [Fact]
    public void OtherToolsAddressedToTheUser_AreNotQuestions()
    {
        Assert.False(AgentProtocol.IsQuestionForHuman("conclude", "user"));
        Assert.False(AgentProtocol.IsQuestionForHuman("present_finding", Input(new { to = "user" })));
    }

    [Fact]
    public void MissingOrMalformedRecipient_IsNotAQuestion()
    {
        Assert.False(AgentProtocol.IsQuestionForHuman("message", (string?)null));
        Assert.False(AgentProtocol.IsQuestionForHuman("message", Input(new { text = "no recipient" })));
        // A non-object input must not throw -- tool input is model-authored.
        Assert.False(AgentProtocol.IsQuestionForHuman("message", Input("just a string")));
        Assert.False(AgentProtocol.IsQuestionForHuman(null, "user"));
    }

    [Fact]
    public void ToolsAreNotAdvertisedAsReadOnlyUnlessTheyOptIn()
    {
        // The MCP layer surfaces this as readOnlyHint. Defaulting to false matters:
        // every tool was previously advertised read-only, including run_shell.
        var definition = new ToolDefinition(
            Name: "example",
            Description: "d",
            ParameterSchema: default,
            DefaultTimeout: TimeSpan.FromSeconds(1));

        Assert.False(definition.ReadOnlyHint);
    }
}
