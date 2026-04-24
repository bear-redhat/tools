using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInvestigatorTools(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OcOptions>(config.GetSection("Tools:run_oc"));
        services.Configure<ShellOptions>(config.GetSection("Tools:run_shell"));
        services.Configure<ReleaseRepoOptions>(config.GetSection("Tools:release_repo"));
        services.Configure<SkillsOptions>(config.GetSection("Tools:skills"));
        services.Configure<ToolOutputOptions>(config.GetSection(ToolOutputOptions.Section));
        services.Configure<PluginOptions>(config.GetSection(PluginOptions.Section));
        services.Configure<WorkspaceOptions>(config.GetSection(WorkspaceOptions.Section));

        services.AddSingleton<OcExecutor>();
        services.AddSingleton<ShellExecutor>();
        services.AddSingleton<ReleaseRepoTool>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<WorkspaceManager>();

        services.AddSingleton<ToolRegistry>(sp =>
        {
            var registry = new ToolRegistry(
                sp.GetRequiredService<ILogger<ToolRegistry>>(),
                sp.GetRequiredService<IOptions<ToolOutputOptions>>());

            registry.Register(sp.GetRequiredService<OcExecutor>());
            registry.Register(sp.GetRequiredService<ShellExecutor>());
            registry.Register(sp.GetRequiredService<ReleaseRepoTool>());

            var skillsLib = new SkillsLibrary(
                sp.GetRequiredService<IEmbeddingClient>(),
                sp.GetRequiredService<ILogger<SkillsLibrary>>(),
                sp.GetRequiredService<IOptions<SkillsOptions>>());
            registry.Register(skillsLib);

            var pluginOpts = sp.GetRequiredService<IOptions<PluginOptions>>().Value;
            if (!string.IsNullOrEmpty(pluginOpts.Directory))
            {
                var pluginConfig = sp.GetRequiredService<IConfiguration>();
                var pluginLoader = sp.GetRequiredService<PluginLoader>();
                foreach (var plugin in pluginLoader.LoadPlugins(pluginOpts.Directory, pluginConfig))
                    registry.Register(plugin);
            }

            return registry;
        });

        return services;
    }

    public static IServiceCollection AddInvestigatorLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.Section));

        services.AddHttpClient();
        services.AddSingleton<ILlmClientFactory>(sp =>
            new LlmClientFactory(
                sp.GetRequiredService<IOptions<LlmOptions>>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));

        var llmSection = config.GetSection(LlmOptions.Section);
        var primaryName = llmSection["Primary"] ?? "";
        var primaryProvider = llmSection.GetSection($"Models:{primaryName}")["Provider"]?.ToLowerInvariant();
        switch (primaryProvider)
        {
            case "vertex" or "vertex-gemini":
                services.AddSingleton<GoogleAccessTokenProvider>(sp =>
                {
                    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
                    var providerName = opts.Models[opts.Primary].Provider;
                    var creds = opts.Providers.GetValueOrDefault(providerName);
                    return new GoogleAccessTokenProvider(creds?.ServiceAccountKeyPath,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GoogleAccessTokenProvider>());
                });
                services.AddHttpClient<VertexEmbeddingClient>();
                services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<VertexEmbeddingClient>());
                break;
            default:
                services.AddHttpClient<BedrockEmbeddingClient>();
                services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<BedrockEmbeddingClient>());
                break;
        }

        services.AddSingleton<SummarizationService>();
        return services;
    }

    public static IServiceCollection AddInvestigatorAuth(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));

        var authSection = config.GetSection(AuthOptions.Section);
        var oidcEnabled = !string.IsNullOrEmpty(authSection["ClientId"]) && !string.IsNullOrEmpty(authSection["Authority"]);
        var tokenAuthEnabled = !oidcEnabled && !string.IsNullOrEmpty(authSection["SharedToken"]);
        var authMode = oidcEnabled ? AuthMode.Oidc : tokenAuthEnabled ? AuthMode.Token : AuthMode.None;

        services.AddSingleton(new AuthSettings { Mode = authMode });
        services.AddScoped<CircuitAuthState>();

        if (oidcEnabled)
        {
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                options.Authority = authSection["Authority"];
                options.ClientId = authSection["ClientId"];
                options.ClientSecret = authSection["ClientSecret"];
                options.ResponseType = "code";
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;

                var scopes = authSection.GetSection("Scopes").Get<string[]>() ?? ["openid", "profile", "email"];
                foreach (var scope in scopes)
                    options.Scope.Add(scope);
            });
        }
        else
        {
            services.AddAuthentication("anonymous")
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AnonymousAuthHandler>(
                    "anonymous", _ => { });
        }

        services.AddAuthorization(options =>
        {
            if (!oidcEnabled)
            {
                options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAssertion(_ => true)
                    .Build();
            }
        });
        services.AddCascadingAuthenticationState();

        return services;
    }
}
