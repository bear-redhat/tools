using System.Text.Json.Serialization;

namespace Investigator.Models;

public record EvidenceChain(IReadOnlyList<EvidenceStep> Steps);

public record EvidenceStep(
    int Step,
    string Reasoning,
    string Finding,
    string? Cluster,
    [property: JsonPropertyName("command")] string Proof,
    string? Source = null);
