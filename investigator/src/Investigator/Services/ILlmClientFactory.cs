using Investigator.Models;

namespace Investigator.Services;

public interface ILlmClientFactory
{
    ILlmClient GetClient(string? profileName = null);
    ModelOptions GetModelOptions(string? profileName = null);
    IReadOnlyDictionary<string, ModelOptions> Models { get; }
    string PrimaryProfileName { get; }
    string DefaultProfileName { get; }
}
