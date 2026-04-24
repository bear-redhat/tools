using System.Reflection;
using System.Runtime.Loader;
using Investigator.Contracts;

namespace Investigator.Services;

public static class PluginLoader
{
    public static List<Type> DiscoverToolTypes(string pluginDir, ILogger logger)
    {
        var types = new List<Type>();

        if (!Directory.Exists(pluginDir))
        {
            logger.LogInformation("Plugin directory {Dir} does not exist, skipping", pluginDir);
            return types;
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
                    types.Add(type);
                    logger.LogInformation("Discovered plugin tool type {Type} from {Dll}", type.FullName, Path.GetFileName(dll));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load plugin from {Dll}", dll);
            }
        }

        logger.LogInformation("Plugin discovery complete: {Count} tool types found in {Dir}", types.Count, pluginDir);
        return types;
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
