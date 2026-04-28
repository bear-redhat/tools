using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using Microsoft.Extensions.Options;

#pragma warning disable CS0618 // GoogleCredential.FromFile -- CredentialFactory not yet available in all target environments

namespace Investigator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInvestigatorTools(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AwsOptions>(config.GetSection("Tools:run_aws"));
        services.Configure<OcOptions>(config.GetSection("Tools:run_oc"));
        services.Configure<ShellOptions>(config.GetSection("Tools:run_shell"));
        services.Configure<CiRepoOptions>(config.GetSection("Tools:ci_repo"));
        services.Configure<SkillsOptions>(config.GetSection("Tools:skills"));
        services.Configure<WebSearchOptions>(config.GetSection("Tools:web_search"));
        services.Configure<WebBrowserOptions>(config.GetSection("Tools:web_browse"));
        services.Configure<GitHubOptions>(config.GetSection("Tools:github"));
        services.Configure<ProwOptions>(config.GetSection("Tools:prow"));
        services.Configure<ToolOutputOptions>(config.GetSection(ToolOutputOptions.Section));
        services.Configure<PluginOptions>(config.GetSection(PluginOptions.Section));
        services.Configure<WorkspaceOptions>(config.GetSection(WorkspaceOptions.Section));

        services.AddSingleton<WorkspaceManager>();

        services.AddHttpClient("GitHub", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Investigator");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        services.AddHttpClient("Prow", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Investigator");
        });
        services.AddSingleton<GitHubAppAuth>();

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("GcsInit");
            StorageClient? client = null;

            var credFile = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (!string.IsNullOrEmpty(credFile) && File.Exists(credFile))
            {
                try
                {
                    var credential = GoogleCredential.FromFile(credFile);
                    client = StorageClient.Create(credential);
                    logger.LogInformation("GCS StorageClient created from {CredFile}", credFile);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to create GCS StorageClient from {CredFile}", credFile);
                }
            }

            if (client is null)
            {
                try
                {
                    var credential = GoogleCredential.GetApplicationDefault();
                    client = StorageClient.Create(credential);
                    logger.LogInformation("GCS StorageClient created from application default credentials");
                }
                catch
                {
                    logger.LogInformation("No GCP credentials available, ProwTool will use anonymous GCS Web fallback");
                }
            }

            return new GcsClientHolder(client);
        });

        services.AddSingleton<ToolRegistry>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ToolRegistry>>();

            var toolTypes = new List<Type>
            {
                typeof(OcExecutor),
                typeof(AwsExecutor),
                typeof(ShellExecutor),
                typeof(CiRepoTool),
                typeof(SkillsLibrary),
                typeof(WebSearchTool),
                typeof(WebBrowserTool),
                typeof(GitHubTool),
                typeof(ProwTool),
            };

            var pluginOpts = sp.GetRequiredService<IOptions<PluginOptions>>().Value;
            if (!string.IsNullOrEmpty(pluginOpts.Directory))
                toolTypes.AddRange(PluginLoader.DiscoverToolTypes(pluginOpts.Directory, logger));

            return new ToolRegistry(sp, toolTypes, logger,
                sp.GetRequiredService<IOptions<ToolOutputOptions>>());
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

    public record GcsClientHolder(StorageClient? Client);

    public static IServiceCollection AddInvestigatorAuth(this IServiceCollection services, IConfiguration config,
        IHostEnvironment environment)
    {
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));

        var authSection = config.GetSection(AuthOptions.Section);
        var oidcEnabled = !string.IsNullOrEmpty(authSection["ClientId"]) && !string.IsNullOrEmpty(authSection["Authority"]);
        var tokenEnabled = !string.IsNullOrEmpty(authSection["SharedToken"]);
        var authMode = (oidcEnabled, tokenEnabled) switch
        {
            (true, true)   => AuthMode.TokenAndOidc,
            (true, false)  => AuthMode.Oidc,
            (false, true)  => AuthMode.Token,
            _              => AuthMode.None,
        };

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

                if (environment.IsDevelopment())
                    options.RequireHttpsMetadata = false;

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
