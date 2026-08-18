using System.Text.Json;

namespace Investigator.Models;

/// <summary>
/// Rules shared between the agent loop and anything that projects or inspects it.
///
/// "The lead asked the human something and is now parked" is decided in two places that
/// must agree: <c>InvestigationRoom</c> treats the call as conditionally terminal so the
/// turn ends, and <c>ProjectedEventLog</c> reports it as an outstanding question. Encoding
/// the rule twice meant renaming the tool or adding a second ask-the-human path would
/// silently leave a client believing an investigation was still working.
/// </summary>
public static class AgentProtocol
{
    /// <summary>Tool an agent uses to address the human or another agent.</summary>
    public const string MessageTool = "message";

    /// <summary>Recipients that mean "the person running this investigation".</summary>
    public static bool IsHumanRecipient(string? recipient) =>
        recipient is "user" or "client";

    /// <summary>
    /// True when a tool call is a question put to the human. Such a call ends the agent's
    /// turn, leaving it parked on its inbox until an answer arrives.
    /// </summary>
    public static bool IsQuestionForHuman(string? tool, string? recipient) =>
        tool == MessageTool && IsHumanRecipient(recipient);

    /// <inheritdoc cref="IsQuestionForHuman(string?, string?)"/>
    public static bool IsQuestionForHuman(string? tool, JsonElement input) =>
        tool == MessageTool
        && input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("to", out var to)
        && IsHumanRecipient(to.GetString());
}
