using System.Collections.Concurrent;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
namespace Investigator.Services;

internal sealed class ScoutCoordinator
{
    private static readonly string[] s_adjectives =
        ["Sharp", "Swift", "Keen", "Quiet", "Steady", "Nimble", "Bold", "Clever", "Bright", "Wary",
         "Sturdy", "Quick", "Calm", "Watchful", "Diligent", "Faithful", "Plucky", "Resolute", "Canny", "Deft",
         "Earnest", "Gentle", "Hardy", "Tireless", "Thorough"];

    private static readonly string[] s_animals =
        ["Badger", "Owl", "Fox", "Rabbit", "Hedgehog", "Mole", "Otter", "Wren", "Hare", "Stoat",
         "Crow", "Finch", "Vole", "Shrew", "Rook", "Magpie", "Robin", "Sparrow", "Jackdaw", "Dormouse",
         "Newt", "Toad", "Pipit", "Dunnock", "Fieldfare"];

    private readonly ConcurrentDictionary<string, InvestigationRoom.AgentSlot> _agents;
    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentOptions _agentOptions;
    private readonly IReadOnlyList<string> _toolSections;
    private readonly ILogger _logger;
    private readonly Func<AgentEvent, ValueTask> _emitToUi;
    private readonly Func<InvestigationRoom.AgentSlot, AgentRunner.Config, CancellationToken, Task> _runAgent;
    private readonly JsonElement _concludeSchema;

    private readonly HashSet<string> _usedAgentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng = new();

    internal string WorkspacePath { get; set; } = "";

    internal ScoutCoordinator(
        ConcurrentDictionary<string, InvestigationRoom.AgentSlot> agents,
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        IReadOnlyList<string> toolSections,
        ILogger logger,
        Func<AgentEvent, ValueTask> emitToUi,
        Func<InvestigationRoom.AgentSlot, AgentRunner.Config, CancellationToken, Task> runAgent,
        JsonElement concludeSchema)
    {
        _agents = agents;
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _agentOptions = agentOptions;
        _toolSections = toolSections;
        _logger = logger;
        _emitToUi = emitToUi;
        _runAgent = runAgent;
        _concludeSchema = concludeSchema;
    }

    internal void RegisterAgentName(string name)
    {
        _usedAgentNames.Add(name);
    }

    internal async Task<AgentRunner.ToolExecutionResult> HandleDelegate(JsonElement input, CancellationToken ct)
    {
        var agentName = GenerateAgentName();
        var role = input.TryGetProperty("role", out var r) ? r.GetString() ?? "scout" : "scout";
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

        _logger.LogInformation("Dispatching {Name} ({Role}) using {Model}: {Task}", agentName, role, resolvedModel, task);

        await _emitToUi(new AgentEvent.SubAgentStarted($"sa-{agentName}-start", agentName, role, task, resolvedModel));

        var scoutSlot = new InvestigationRoom.AgentSlot
        {
            Id = agentName.ToLowerInvariant().Replace(" ", "-"),
            Name = agentName,
            Role = role,
        };
        _agents[agentName] = scoutSlot;

        var scoutConfig = new AgentRunner.Config(
            Id: scoutSlot.Id,
            Name: agentName,
            Role: role,
            SystemPrompt: InvestigationPrompts.BuildScoutSystemPrompt(agentName, role, task, WorkspacePath, _toolSections),
            LlmClient: subClient,
            Tools: BuildScoutTools(),
            InitialMessages: [new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(task) }],
            MaxToolCalls: _agentOptions.SubAgentMaxToolCalls,
            MaxRetries: _agentOptions.LlmRetries,
            WorkspacePath: WorkspacePath,
            CompactionMaxTokens: null);

        await scoutSlot.Inbox.Writer.WriteAsync(new RoomMessage("Little Bear", task), ct);
        scoutSlot.RunTask = Task.Run(() => _runAgent(scoutSlot, scoutConfig, ct), ct);

        return new AgentRunner.ToolExecutionResult(
            Output: $"Dispatched: {agentName} ({role}) using {resolvedModel}. They will report back when done -- do not duplicate their task.");
    }

    internal IReadOnlyList<ToolDefinition> BuildScoutTools()
    {
        var tools = _toolRegistry.GetToolDefinitions().ToList();

        tools.Add(new ToolDefinition(
            Name: "conclude",
            Description: "Call this when you have completed your assignment. Provide your findings -- this delivers your report to Little Bear.",
            ParameterSchema: _concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        return tools;
    }

    private string GenerateAgentName()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var adj = s_adjectives[_rng.Next(s_adjectives.Length)];
            var animal = s_animals[_rng.Next(s_animals.Length)];
            var name = $"{adj} {animal}";
            if (_usedAgentNames.Add(name))
                return name;
        }
        var fallback = $"Scout-{_usedAgentNames.Count + 1}";
        _usedAgentNames.Add(fallback);
        return fallback;
    }
}
