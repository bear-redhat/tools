namespace Investigator.Models;

public record FixSuggestion(
    string Description,
    IReadOnlyList<string> Commands,
    string? Warning = null);
