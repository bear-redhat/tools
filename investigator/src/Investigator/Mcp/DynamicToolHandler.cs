using System.Text.Json;
using Investigator.Contracts;
using Investigator.Services;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Investigator.Mcp;

/// <summary>
/// Dynamically registers MCP tools from the ToolRegistry.
/// Each registered IInvestigatorTool becomes an MCP tool prefixed with "raw_"
/// to distinguish it from capability-level tools. Tool calls route through
/// to ToolRegistry.InvokeAsync() using the original unprefixed name.
/// </summary>
public static class DynamicToolHandler
{
    private const string Prefix = "raw_";

    public static IReadOnlyList<McpServerTool> BuildToolsFromRegistry(
        ToolRegistry toolRegistry,
        IServiceProvider services)
    {
        var definitions = toolRegistry.GetToolDefinitions();
        var tools = new List<McpServerTool>(definitions.Count);

        foreach (var def in definitions)
        {
            var mcpName = $"{Prefix}{def.Name}";
            var aiFunction = new RegistryToolFunction(mcpName, def, services);
            var mcpTool = McpServerTool.Create(aiFunction, new McpServerToolCreateOptions
            {
                Name = mcpName,
                Description = def.Description,
                ReadOnly = true,
            });
            tools.Add(mcpTool);
        }

        return tools;
    }

    /// <summary>
    /// An AIFunction that wraps a ToolRegistry tool definition,
    /// preserving its existing JSON Schema and routing invocations to the registry.
    /// </summary>
    private sealed class RegistryToolFunction : AIFunction
    {
        private readonly string _mcpName;
        private readonly string _registryName;
        private readonly JsonElement _schema;
        private readonly string _description;
        private readonly IServiceProvider _services;

        public RegistryToolFunction(string mcpName, ToolDefinition def, IServiceProvider services)
        {
            _mcpName = mcpName;
            _registryName = def.Name;
            _description = def.Description;
            _schema = def.ParameterSchema;
            _services = services;
        }

        public override string Name => _mcpName;
        public override string Description => _description;
        public override JsonElement JsonSchema => _schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var toolRegistry = _services.GetRequiredService<ToolRegistry>();
            var sessionContext = _services.GetRequiredService<McpSessionContext>();

            var parameters = JsonSerializer.SerializeToElement(arguments);
            var context = sessionContext.CreateToolContext("mcp");
            var (_, _, truncated) = await toolRegistry.InvokeAsync(_registryName, parameters, context, cancellationToken);
            return truncated;
        }
    }
}
