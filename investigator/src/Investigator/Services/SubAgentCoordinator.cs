using System.Collections.Concurrent;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
namespace Investigator.Services;

public record SubAgentConfig(
    string Label,
    string[] Adjectives,
    string[] Animals,
    Func<string, string, string, string, IReadOnlyList<string>, string?, TimeZoneInfo?, string> BuildPrompt,
    string LeadAgentName,
    ToolScope ToolScope,
    Func<string, string, string, string, IReadOnlyList<string>, string?, TimeZoneInfo?, string>? BuildAnalystPrompt = null);

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
    private readonly JsonElement _delegateSchema;
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
        JsonElement delegateSchema,
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
        _delegateSchema = delegateSchema;
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

    internal async Task<AgentRunner.ToolExecutionResult> HandleDelegate(
        JsonElement input, CancellationToken ct,
        string dispatcherName, string dispatcherId)
    {
        var agentName = GenerateAgentName();
        var role = input.TryGetProperty("role", out var r) ? r.GetString() ?? _config.Label.ToLowerInvariant() : _config.Label.ToLowerInvariant();
        var task = input.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
        var modelName = input.TryGetProperty("model", out var m) ? m.GetString() : null;
        var tier = input.TryGetProperty("tier", out var ti) ? ti.GetString() ?? "field" : "field";
        var isAnalyst = tier == "analyst";

        var ccTargets = new List<string>();
        if (input.TryGetProperty("cc", out var ccArray) && ccArray.ValueKind == JsonValueKind.Array)
            foreach (var item in ccArray.EnumerateArray())
                if (item.GetString() is { } ccName)
                    ccTargets.Add(ccName.ToLowerInvariant().Replace(" ", "-"));

        List<LlmMessage>? initialMessages = null;
        if (input.TryGetProperty("briefing", out var briefArray) && briefArray.ValueKind == JsonValueKind.Array)
        {
            initialMessages = [];
            foreach (var item in briefArray.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var bt) ? bt.GetString() ?? "" : "";
                var content = item.TryGetProperty("content", out var bc) ? bc.GetString() ?? "" : "";
                initialMessages.Add(new LlmMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement($"[Briefing from {dispatcherName}] {title}:\n{content}")
                });
            }
        }

        if (isAnalyst && modelName is null)
            modelName = _llmFactory.PrimaryProfileName;

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

        var label = isAnalyst ? "Analyst" : _config.Label;
        _logger.LogInformation("Dispatching {Label} {Name} ({Role}) using {Model}: {Task}", label, agentName, role, resolvedModel, task);

        var subSlotId = agentName.ToLowerInvariant().Replace(" ", "-");

        var subSlot = new AgentRoom.AgentSlot
        {
            Id = subSlotId,
            Name = agentName,
            Role = role,
            DispatcherId = dispatcherId,
            TaskDescription = task,
            CcTargets = ccTargets,
            CanDelegate = isAnalyst,
            Inbox = _bus.Subscribe(subSlotId, evt => evt.To == subSlotId && evt is not RoomEvent.ToolResponse),
        };
        _agents[agentName] = subSlot;

        var buildPrompt = isAnalyst && _config.BuildAnalystPrompt is not null
            ? _config.BuildAnalystPrompt
            : _config.BuildPrompt;
        var tools = isAnalyst ? BuildAnalystTools(dispatcherName) : BuildSubAgentTools(dispatcherName);

        var modelOptions = _llmFactory.GetModelOptions(resolvedModel);
        var summarizerProfile = _llmFactory.DefaultProfileName;
        var summarizerOptions = _llmFactory.GetModelOptions(summarizerProfile);
        var subConfig = new AgentRunner.Config(
            Id: subSlot.Id,
            Name: agentName,
            Role: role,
            SystemPrompt: buildPrompt(agentName, role, task, WorkspacePath, _toolSections, ConversationId, ClientTimeZone),
            LlmClient: subClient,
            Tools: tools,
            MaxToolCalls: isAnalyst ? _agentOptions.MaxToolCalls : _agentOptions.SubAgentMaxToolCalls,
            MaxRetries: _agentOptions.LlmRetries,
            WorkspacePath: WorkspacePath,
            CompactionMaxTokens: isAnalyst ? modelOptions.MaxTokens * 4 : null,
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
            TerminalToolNames: new HashSet<string> { "conclude", "message" },
            ShouldSuppressNextTurn: () => subSlot.HasReported);

        subSlot.RunTask = Task.Run(() => _runAgent(subSlot, subConfig, ct, initialMessages), ct);

        return new AgentRunner.ToolExecutionResult(
            Output: $"Dispatched {agentName} ({role}) using {resolvedModel}. They will report when finished -- do not duplicate their task.");
    }

    private readonly JsonElement _messageSchema;

    internal IReadOnlyList<ToolDefinition> BuildSubAgentTools(string reportTo)
    {
        var tools = _toolRegistry.GetToolDefinitions(_config.ToolScope).ToList();

        tools.Add(new ToolDefinition(
            Name: "conclude",
            Description: $"Call this when your assignment is complete. Provide your findings -- this delivers your report to {reportTo}.",
            ParameterSchema: _concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        var msgSchema = BuildMessageSchema(reportTo, _config.Label);
        tools.Add(new ToolDefinition(
            Name: "message",
            Description: $"Send a message to {reportTo} -- use this to ask a question or share an interim update. You will wait for their reply.",
            ParameterSchema: msgSchema,
            DefaultTimeout: TimeSpan.Zero));

        return tools;
    }

    internal IReadOnlyList<ToolDefinition> BuildAnalystTools(string reportTo)
    {
        var tools = _toolRegistry.GetToolDefinitions(_config.ToolScope).ToList();

        tools.Add(new ToolDefinition(
            Name: "conclude",
            Description: $"Call this when your analysis is complete. Provide your synthesized findings -- this delivers your report to {reportTo}.",
            ParameterSchema: _concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        var msgSchema = BuildAnalystMessageSchema(reportTo);
        tools.Add(new ToolDefinition(
            Name: "message",
            Description: $"Send a message to another agent or to {reportTo}. When messaging a field agent, they resume with your reply. When messaging {reportTo}, you will wait for their response.",
            ParameterSchema: msgSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: "delegate",
            Description: $"Dispatch a field agent. Non-blocking -- returns immediately with their assigned name. They investigate independently and report back as a message. Provide a role, task, and optionally a model profile.",
            ParameterSchema: _delegateSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: "check_agents",
            Description: "Review the registry of agents currently in the field. Check before dispatching to avoid duplicating work already in progress.",
            ParameterSchema: s_emptySchema,
            DefaultTimeout: TimeSpan.Zero));

        return tools;
    }

    private static readonly JsonElement s_emptySchema = JsonDocument.Parse("""
    { "type": "object", "properties": {}, "required": [] }
    """).RootElement.Clone();

    private static JsonElement BuildMessageSchema(string reportTo, string label) =>
        JsonDocument.Parse($$"""
        {
            "type": "object",
            "properties": {
                "to": { "type": "string", "description": "Recipient name (always '{{reportTo}}' for {{label}}s)" },
                "text": { "type": "string", "description": "The message" }
            },
            "required": ["to", "text"]
        }
        """).RootElement.Clone();

    private static JsonElement BuildAnalystMessageSchema(string reportTo) =>
        JsonDocument.Parse($$"""
        {
            "type": "object",
            "properties": {
                "to": { "type": "string", "description": "Recipient name -- a field agent's name, or '{{reportTo}}'" },
                "text": { "type": "string", "description": "The message" }
            },
            "required": ["to", "text"]
        }
        """).RootElement.Clone();

    internal AgentRoom.AgentSlot ResumeAgent(IncompleteAgent agent, CancellationToken ct)
    {
        _usedAgentNames.Add(agent.Name);

        var modelName = agent.Model;
        if (agent.IsAnalyst && modelName is null)
            modelName = _llmFactory.PrimaryProfileName;

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
            _logger.LogWarning(ex, "Resumed agent '{Name}' model '{Model}' unavailable, falling back to default", agent.Name, modelName);
            subClient = _llmFactory.GetClient();
            resolvedModel = _llmFactory.DefaultProfileName;
        }

        var dispatcherName = _agents.Values.FirstOrDefault(s => s.Id == agent.DispatcherId)?.Name
            ?? _config.LeadAgentName;

        _logger.LogInformation("Resuming {Label} {Name} ({Role}) using {Model}: {Task}",
            agent.IsAnalyst ? "Analyst" : _config.Label, agent.Name, agent.Role, resolvedModel, agent.Task);

        var subSlot = new AgentRoom.AgentSlot
        {
            Id = agent.Id,
            Name = agent.Name,
            Role = agent.Role,
            DispatcherId = agent.DispatcherId,
            TaskDescription = agent.Task,
            CcTargets = agent.CcTargets.ToList(),
            CanDelegate = agent.IsAnalyst,
            Inbox = _bus.Subscribe(agent.Id, evt => evt.To == agent.Id && evt is not RoomEvent.ToolResponse),
        };
        _agents[agent.Name] = subSlot;

        var buildPrompt = agent.IsAnalyst && _config.BuildAnalystPrompt is not null
            ? _config.BuildAnalystPrompt
            : _config.BuildPrompt;
        var tools = agent.IsAnalyst ? BuildAnalystTools(dispatcherName) : BuildSubAgentTools(dispatcherName);

        var modelOptions = _llmFactory.GetModelOptions(resolvedModel);
        var summarizerProfile = _llmFactory.DefaultProfileName;
        var summarizerOptions = _llmFactory.GetModelOptions(summarizerProfile);
        var subConfig = new AgentRunner.Config(
            Id: subSlot.Id,
            Name: agent.Name,
            Role: agent.Role,
            SystemPrompt: buildPrompt(agent.Name, agent.Role, agent.Task, WorkspacePath, _toolSections, ConversationId, ClientTimeZone),
            LlmClient: subClient,
            Tools: tools,
            MaxToolCalls: agent.IsAnalyst ? _agentOptions.MaxToolCalls : _agentOptions.SubAgentMaxToolCalls,
            MaxRetries: _agentOptions.LlmRetries,
            WorkspacePath: WorkspacePath,
            CompactionMaxTokens: agent.IsAnalyst ? modelOptions.MaxTokens * 4 : null,
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
            TerminalToolNames: new HashSet<string> { "conclude", "message" },
            ShouldSuppressNextTurn: () => subSlot.HasReported);

        subSlot.RunTask = Task.Run(() => _runAgent(subSlot, subConfig, ct, agent.ReplayedMessages), ct);

        return subSlot;
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
