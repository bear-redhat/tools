using System.Collections.Concurrent;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly string _primaryName;
    private readonly string _defaultName;
    private readonly Dictionary<string, ModelOptions> _profileMap;
    private readonly IReadOnlyDictionary<string, ProviderCredentials> _credentials;
    private readonly ConcurrentDictionary<string, ILlmClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    public LlmClientFactory(
        IOptions<LlmOptions> llmOptions,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;

        var opts = llmOptions.Value;
        _primaryName = opts.Primary;
        _defaultName = opts.Default;
        _profileMap = opts.Models;
        _credentials = opts.Providers;

        var logger = loggerFactory.CreateLogger<LlmClientFactory>();

        if (!_profileMap.ContainsKey(_primaryName))
            throw new InvalidOperationException($"Primary profile '{_primaryName}' not found in Models dictionary. Available: {string.Join(", ", _profileMap.Keys)}");
        if (!_profileMap.ContainsKey(_defaultName))
            throw new InvalidOperationException($"Default profile '{_defaultName}' not found in Models dictionary. Available: {string.Join(", ", _profileMap.Keys)}");

        logger.LogInformation("Loaded {Count} model profiles, primary='{Primary}', default='{Default}'",
            _profileMap.Count, _primaryName, _defaultName);
        foreach (var (name, m) in _profileMap)
            logger.LogInformation("  Profile '{Name}': provider={Provider}, model={Model}", name, m.Provider, m.Model);
    }

    public IReadOnlyDictionary<string, ModelOptions> Models => _profileMap;
    public string PrimaryProfileName => _primaryName;
    public string DefaultProfileName => _defaultName;

    public ModelOptions GetModelOptions(string? profileName = null)
    {
        var name = profileName ?? _defaultName;
        if (_profileMap.TryGetValue(name, out var options))
            return options;

        throw new ArgumentException($"Unknown model profile '{name}'. Available: {string.Join(", ", _profileMap.Keys)}");
    }

    public ILlmClient GetClient(string? profileName = null)
    {
        var name = profileName ?? _defaultName;
        var options = GetModelOptions(name);
        return _clients.GetOrAdd(name, _ => CreateClient(name, options));
    }

    private ILlmClient CreateClient(string name, ModelOptions options)
    {
        var http = _httpFactory.CreateClient($"llm-{name}");
        var creds = _credentials[options.Provider];

        return options.Provider.ToLowerInvariant() switch
        {
            "bedrock" => new BedrockClient(http, name, options, creds, _loggerFactory.CreateLogger<BedrockClient>()),
            "vertex" => new VertexAiClient(http, name, options,
                new GoogleAccessTokenProvider(creds.ServiceAccountKeyPath, _loggerFactory.CreateLogger<GoogleAccessTokenProvider>()),
                _loggerFactory.CreateLogger<VertexAiClient>()),
            "vertex-gemini" => new GeminiClient(http, name, options,
                new GoogleAccessTokenProvider(creds.ServiceAccountKeyPath, _loggerFactory.CreateLogger<GoogleAccessTokenProvider>()),
                _loggerFactory.CreateLogger<GeminiClient>()),
            _ => throw new InvalidOperationException(
                $"Unknown provider '{options.Provider}' for profile '{name}'. Supported: bedrock, vertex, vertex-gemini"),
        };
    }
}
