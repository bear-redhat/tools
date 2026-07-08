namespace Investigator.Models;

public record CaseFile(
    string ParentConversationId,
    string CaseStatement,
    IReadOnlyList<CaseFinding> Findings,
    string Summary,
    EvidenceChain? Evidence,
    FixSuggestion? Fix);

public record CaseFinding(string Title, string Description);

public record CaseReferral(
    string? Reason,
    EvidenceChain? DisprovalEvidence,
    string? SuggestedDirection);
