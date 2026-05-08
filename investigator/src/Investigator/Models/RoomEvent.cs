using System.Text.Json;
using System.Text.Json.Serialization;

namespace Investigator.Models;

[JsonDerivedType(typeof(TextMessage), "text")]
[JsonDerivedType(typeof(ToolRequest), "tool_request")]
[JsonDerivedType(typeof(ToolResponse), "tool_response")]
[JsonDerivedType(typeof(LlmContext), "llm_context")]
[JsonDerivedType(typeof(ExternalInput), "external_input")]
[JsonDerivedType(typeof(AgentTurn), "agent_turn")]
[JsonDerivedType(typeof(SessionEnded), "session_ended")]
public abstract record RoomEvent(int Seq, string From, DateTimeOffset Timestamp)
{
    public string? To { get; init; }

    public record TextMessage(int Seq, string From, DateTimeOffset Timestamp,
        string Text)
        : RoomEvent(Seq, From, Timestamp);

    public record ToolRequest(int Seq, string From, DateTimeOffset Timestamp,
        string Tool, JsonElement Input,
        string? DisplayCommand = null,
        int? ParentSeq = null)
        : RoomEvent(Seq, From, Timestamp);

    public record ToolResponse(int Seq, string From, DateTimeOffset Timestamp,
        string Tool, string Output,
        int RequestSeq,
        int ExitCode = 0,
        string? OutputFile = null,
        bool TimedOut = false,
        string? Summary = null,
        bool Concluded = false,
        int? ParentSeq = null)
        : RoomEvent(Seq, From, Timestamp);

    public record LlmContext(int Seq, string From, DateTimeOffset Timestamp,
        IReadOnlyList<LlmMessage> Messages,
        int Removed = 0,
        UsageInfo? Usage = null,
        string? ThinkingText = null,
        string? ModelProfile = null,
        decimal InputPrice = 0,
        decimal OutputPrice = 0,
        decimal CacheReadPrice = 0,
        decimal CacheCreatePrice = 0,
        bool IsInboxBatch = false,
        bool IsConcludedBatch = false)
        : RoomEvent(Seq, From, Timestamp);

    public record ExternalInput(int Seq, string From, DateTimeOffset Timestamp,
        string Text)
        : RoomEvent(Seq, From, Timestamp);

    public record AgentTurn(int Seq, string From, DateTimeOffset Timestamp,
        bool IsNewTurn = false,
        string? ThinkingText = null,
        UsageInfo? Usage = null,
        string? ModelProfile = null,
        int CompactedMessages = 0,
        decimal InputPrice = 0,
        decimal OutputPrice = 0,
        decimal CacheReadPrice = 0,
        decimal CacheCreatePrice = 0)
        : RoomEvent(Seq, From, Timestamp);

    public record SessionEnded(int Seq, string From, DateTimeOffset Timestamp)
        : RoomEvent(Seq, From, Timestamp);
}
