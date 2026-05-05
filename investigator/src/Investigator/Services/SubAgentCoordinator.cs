using System.Collections.Concurrent;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
namespace Investigator.Services;

public record SubAgentConfig(
    string Label,
    string[] Adjectives,
    string[] Animals,
    Func<string, string, string, string, IReadOnlyList<string>, TimeZoneInfo?, string> BuildPrompt,
    string LeadAgentName,
    ToolScope ToolScope);

internal sealed class SubAgentCoordinator
{
    private readonly SubAgentConfig _config;
    private readonly ConcurrentDictionary<string, AgentRoom.AgentSlot> _agents;
    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentOptions _agentOptions;
    private readonly IReadOnlyList<string> _toolSections;
    private readonly ILogger _logger;
    private readonly Func<AgentRoom.AgentSlot, AgentRunner.Config, CancellationToken, List<LlmMessage>?, Task> _runAgent;
    private readonly JsonElement _concludeSchema;
    private readonly RoomEventBus _bus;

    private readonly HashSet<string> _usedAgentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng = new();

    internal string WorkspacePath { get; set; } = "";
    internal string? UserId { get; set; }
    internal string? ConversationId { get; set; }
    internal TimeZoneInfo? ClientTimeZone { get; set; }

    internal SubAgentCoordinator(
        SubAgentConfig config,
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        IReadOnlyList<string> toolSections,
        ILogger logger,
        Func<AgentRoom.AgentSlot, AgentRunner.Config, CancellationToken, List<LlmMessage>?, Task> runAgent,
        JsonElement concludeSchema,
        RoomEventBus bus)
    {
        _config = config;
        _agents = agents;
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _agentOptions = agentOptions;
        _toolSections = toolSections;
        _logger = logger;
        _runAgent = runAgent;
        _concludeSchema = concludeSchema;
        _bus = bus;
        _messageSchema = JsonDocument.Parse($$"""
        {
            "type": "object",
            "properties": {
                "to": { "type": "string", "description": "Recipient name (always '{{_config.LeadAgentName}}' for {{_config.Label}}s)" },
                "text": { "type": "string", "description": "The message" }
            },
            "required": ["to", "text"]
        }
        """).RootElement.Clone();
    }

    internal void RegisterAgentName(string name)
    {
        _usedAgentNames.Add(name);
    }

    internal async Task<AgentRunner.ToolExecutionResult> HandleDelegate(JsonElement input, CancellationToken ct)
    {
        var agentName = GenerateAgentName();
        var role = input.TryGetProperty("role", out var r) ? r.GetString() ?? _config.Label.ToLowerInvariant() : _config.Label.ToLowerInvariant();
        var task = input.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
        var modelName = input.TryGetProperty("model", out var m) ? m.GetString() : null;

        ILlmClient subClient;
        string resolvedModel;
        try
        {
            _llmFactory.GetModelOptions(modelName);
            subClient = _llmFactory.GetClient(modelName);
            resolvedModel = modelName ?? _llmFactory.DefaultProfileName;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid model profile '{Model}' in delegate call, falling back to default", modelName);
            subClient = _llmFactory.GetClient();
            resolvedModel = _llmFactory.DefaultProfileName;
        }

        _logger.LogInformation("Dispatching {Label} {Name} ({Role}) using {Model}: {Task}", _config.Label, agentName, role, resolvedModel, task);

        var subSlotId = agentName.ToLowerInvariant().Replace(" ", "-");

        var subSlot = new AgentRoom.AgentSlot
        {
            Id = subSlotId,
            Name = agentName,
            Role = role,
            Inbox = _bus.Subscribe(subSlotId, evt => evt.To == subSlotId && evt is not RoomEvent.ToolResponse),
        };
        _agents[agentName] = subSlot;

        var modelOptions = _llmFactory.GetModelOptions(resolvedModel);
        var summarizerProfile = _llmFactory.DefaultProfileName;
        var summarizerOptions = _llmFactory.GetModelOptions(summarizerProfile);
        var subConfig = new AgentRunner.Config(
            Id: subSlot.Id,
            Name: agentName,
            Role: role,
            SystemPrompt: _config.BuildPrompt(agentName, role, task, WorkspacePath, _toolSections, ClientTimeZone),
            LlmClient: subClient,
            Tools: BuildSubAgentTools(),
            MaxToolCalls: _agentOptions.SubAgentMaxToolCalls,
            MaxRetries: _agentOptions.LlmRetries,
            WorkspacePath: WorkspacePath,
            CompactionMaxTokens: null,
            ThinkingBudget: modelOptions.ThinkingBudget,
            ContextWindowTokens: modelOptions.ContextWindowTokens,
            ModelProfile: resolvedModel,
            InputPricePerMToken: modelOptions.InputPricePerMToken,
            OutputPricePerMToken: modelOptions.OutputPricePerMToken,
            CacheReadPricePerMToken: modelOptions.CacheReadPricePerMToken,
            CacheCreationPricePerMToken: modelOptions.CacheCreationPricePerMToken,
            UserId: UserId,
            ConversationId: ConversationId,
            SummarizerClient: _llmFactory.GetClient(summarizerProfile),
            SummarizerModelOptions: summarizerOptions,
            TerminalToolNames: new HashSet<string> { "conclude", "message" });

        subSlot.RunTask = Task.Run(() => _runAgent(subSlot, subConfig, ct, null), ct);

        return new AgentRunner.ToolExecutionResult(
            Output: $"Dispatched {agentName} ({role}) using {resolvedModel}. They will report when finished -- do not duplicate their task.");
    }

    private readonly JsonElement _messageSchema;

    internal IReadOnlyList<ToolDefinition> BuildSubAgentTools()
    {
        var tools = _toolRegistry.GetToolDefinitions(_config.ToolScope).ToList();

        tools.Add(new ToolDefinition(
            Name: "conclude",
            Description: $"Call this when your assignment is complete. Provide your findings -- this delivers your report to {_config.LeadAgentName}.",
            ParameterSchema: _concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: "message",
            Description: $"Send a message to {_config.LeadAgentName} -- use this to ask a question or share an interim update. You will wait for their reply.",
            ParameterSchema: _messageSchema,
            DefaultTimeout: TimeSpan.Zero));

        return tools;
    }

    private string GenerateAgentName()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var adj = _config.Adjectives[_rng.Next(_config.Adjectives.Length)];
            var animal = _config.Animals[_rng.Next(_config.Animals.Length)];
            var name = $"{adj} {animal}";
            if (_usedAgentNames.Add(name))
                return name;
        }
        var fallback = $"{_config.Label}-{_usedAgentNames.Count + 1}";
        _usedAgentNames.Add(fallback);
        return fallback;
    }
}
