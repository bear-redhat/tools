using System.Reflection;
using System.Runtime.Loader;
using Investigator.Contracts;

namespace Investigator.Services;

public sealed class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;

    public PluginLoader(ILogger<PluginLoader> logger) => _logger = logger;

    public IReadOnlyList<IInvestigatorTool> LoadPlugins(string pluginDir, IConfiguration config)
    {
        var tools = new List<IInvestigatorTool>();

        if (!Directory.Exists(pluginDir))
        {
            _logger.LogInformation("Plugin directory {Dir} does not exist, skipping", pluginDir);
            return tools;
        }

        foreach (var dll in Directory.GetFiles(pluginDir, "*.dll"))
        {
            try
            {
                var context = new PluginLoadContext(dll);
                var assembly = context.LoadFromAssemblyPath(dll);

                var toolTypes = assembly.GetTypes()
                    .Where(t => typeof(IInvestigatorTool).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                foreach (var type in toolTypes)
                {
                    var instance = Activator.CreateInstance(type);
                    if (instance is IInvestigatorTool tool)
                    {
                        tools.Add(tool);
                        _logger.LogInformation("Loaded plugin tool {Name} from {Dll}", tool.Definition.Name, Path.GetFileName(dll));
                    }
                    else
                    {
                        _logger.LogWarning("Type {Type} in {Dll} implements IInvestigatorTool but could not be instantiated (got {Instance})",
                            type.FullName, Path.GetFileName(dll), instance?.GetType().FullName ?? "null");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {Dll}", dll);
            }
        }

        _logger.LogInformation("Plugin loading complete: {Count} tools loaded from {Dir}", tools.Count, pluginDir);
        return tools;
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }
    }
}
