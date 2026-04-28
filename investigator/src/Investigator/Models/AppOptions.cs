namespace Investigator.Models;

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public string Primary { get; set; } = "";
    public string Default { get; set; } = "";
    public string? Summarizer { get; set; }
    public Dictionary<string, ProviderCredentials> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ModelOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderCredentials
{
    public string? BearerToken { get; set; }
    public string? ServiceAccountKeyPath { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? RoleArn { get; set; }
    public string? RoleSessionName { get; set; }
}

public sealed class ModelOptions
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Region { get; set; }
    public string? ProjectId { get; set; }
    public string? EmbeddingModel { get; set; }
    public int MaxTokens { get; set; } = 16000;
    public int ThinkingBudget { get; set; } = 10000;
    public int ContextWindowTokens { get; set; } = 1_000_000;
    public string Strengths { get; set; } = "";
}

public sealed class AgentOptions
{
    public const string Section = "Agent";
    public int MaxToolCalls { get; set; } = 128;
    public int LlmRetries { get; set; } = 3;
    public int SubAgentMaxToolCalls { get; set; } = 128;
}

public sealed class WorkspaceOptions
{
    public const string Section = "Workspace";
    public string RootPath { get; set; } = "";
}

public sealed class ToolOutputOptions
{
    public const string Section = "ToolOutput";
    public int HeadLines { get; set; } = 20;
    public int TailLines { get; set; } = 10;
}

public sealed class PluginOptions
{
    public const string Section = "Plugins";
    public string? Directory { get; set; }
}

public sealed class AuthOptions
{
    public const string Section = "Authentication";
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? SharedToken { get; set; }
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
}

public sealed class OcOptions
{
    public string? Path { get; set; }
    public List<ClusterOptions> Clusters { get; set; } = [];
}

public sealed class ClusterOptions
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Kubeconfig { get; set; }
    public string? Context { get; set; }
    public string? Server { get; set; }
    public string? TokenFile { get; set; }
    public string? CaFile { get; set; }
}

public sealed class AwsOptions
{
    public string? Path { get; set; }
    public List<AwsEntry> Clusters { get; set; } = [];
    public List<AwsEntry> Accounts { get; set; } = [];
}

public sealed class AwsEntry
{
    public string Name { get; set; } = "";
    public string? RoleArn { get; set; }
    public string? Region { get; set; }
    public string? Description { get; set; }
    public string? IntermediaryRoleArn { get; set; }
    public string? IntermediaryRegion { get; set; }
}

public sealed class ShellOptions
{
    public string? Path { get; set; }
    public bool? UseRunUser { get; set; }
}

public sealed class CiRepoOptions
{
    public string? Path { get; set; }
    public Dictionary<string, RepoConfig> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["release"] = new() { Url = "https://github.com/openshift/release.git" },
        ["ci-tools"] = new() { Url = "https://github.com/openshift/ci-tools.git" },
    };
}

public sealed class RepoConfig
{
    public string Url { get; set; } = "";
    public string? LocalPath { get; set; }
    public bool ShallowClone { get; set; } = true;
    /// <summary>
    /// Auto-pull when get_path finds the clone older than this. Null disables auto-pull.
    /// </summary>
    public TimeSpan? MaxAge { get; set; } = TimeSpan.FromHours(1);
}

public sealed class SkillsOptions
{
    public string Path { get; set; } = "skills";
}

public sealed class WebSearchOptions
{
    public string? GoogleApiKey { get; set; }
    public string? GoogleSearchEngineId { get; set; }
}

public sealed class WebBrowserOptions
{
    public int MaxContentChars { get; set; } = 8000;
    public int MaxElements { get; set; } = 50;
    public bool Headless { get; set; } = true;
    public int SessionIdleMinutes { get; set; } = 30;
}

public sealed class GitHubOptions
{
    public string? AppId { get; set; }
    public string? PrivateKeyFile { get; set; }
    public string? InstallationId { get; set; }
}

public sealed class ProwOptions
{
    public string DeckUrl { get; set; } = "https://prow.ci.openshift.org";
    public string GcsWebUrl { get; set; } = "https://gcsweb-ci.apps.ci.l2s4.p1.openshiftapps.com";
    public string DefaultBucket { get; set; } = "test-platform-results";
}
