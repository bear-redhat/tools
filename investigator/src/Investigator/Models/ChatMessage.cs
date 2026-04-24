namespace Investigator.Models;

public record ChatMessage(
    ChatRole Role,
    string Content,
    DateTimeOffset Timestamp,
    EvidenceChain? Evidence = null,
    FixSuggestion? Fix = null);

public enum ChatRole { User, Assistant }
