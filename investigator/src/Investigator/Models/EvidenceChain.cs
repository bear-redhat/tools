namespace Investigator.Models;

public record EvidenceChain(IReadOnlyList<EvidenceStep> Steps);

public record EvidenceStep(
    int Step,
    string Reasoning,
    string Finding,
    string? Cluster,
    string Command,
    string? Source = null);
