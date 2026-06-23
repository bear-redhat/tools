using System.Text.Json.Serialization;

namespace Investigator.Models;

public sealed class RemediationPlan
{
    public List<RemediationStep> Steps { get; set; } = [];

    public RemediationPlan Snapshot() => new()
    {
        Steps = Steps.Select(s => s.Snapshot()).ToList(),
    };
}

public sealed class RemediationStep
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Rationale { get; set; }
    public required RemediationTarget Target { get; set; }
    public required RemediationChange Change { get; set; }
    public required RemediationValidation Validation { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? Note { get; set; }

    public RemediationStep Snapshot() => new()
    {
        Id = Id,
        Title = Title,
        Rationale = Rationale,
        Target = Target,
        Change = Change with { },
        Validation = Validation,
        Status = Status,
        Note = Note,
    };
}

public record RemediationTarget(
    string? Type,
    string? Cluster = null,
    string? Resource = null,
    string? Namespace = null,
    string? Repo = null,
    string? Path = null,
    string? LineRange = null);

public record RemediationChange
{
    public string? Type { get; init; }
    public string? CurrentValue { get; init; }
    public string? DesiredValue { get; init; }
    public IReadOnlyList<string>? Commands { get; init; }
    public string? PatchFile { get; set; }
    public string? Warnings { get; init; }
}

public record RemediationValidation(
    string? Description,
    IReadOnlyList<string>? Commands,
    string? Expected = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepStatus
{
    Pending,
    Preparing,
    Ready,
    AwaitingClient,
    Done,
    Verified,
    Failed,
    Blocked,
}
