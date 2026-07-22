using System.ComponentModel;
using System.Text.Json;
using Investigator.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Investigator.Mcp;

[McpServerResourceType]
public sealed class InvestigatorMcpResources(
    IOptions<OcOptions> ocOptions,
    IOptions<AwsOptions> awsOptions)
{
    [McpServerResource(
        UriTemplate = "investigator://clusters",
        Name = "Configured Clusters",
        MimeType = "application/json")]
    [Description("List of OpenShift clusters the investigator can access via run_oc")]
    public string GetClusters()
    {
        var clusters = ocOptions.Value.Clusters
            .Where(c => c.Name is not null)
            .Select(c => new { name = c.Name, type = c.Type })
            .ToList();

        return JsonSerializer.Serialize(new { clusters });
    }

    [McpServerResource(
        UriTemplate = "investigator://aws-accounts",
        Name = "Configured AWS Accounts",
        MimeType = "application/json")]
    [Description("List of AWS accounts and cluster profiles the investigator can access via run_aws")]
    public string GetAwsAccounts()
    {
        var clusters = awsOptions.Value.Clusters
            .Where(c => c.Name is not null)
            .Select(c => new { name = c.Name, type = "cluster", region = c.Region })
            .ToList();

        var accounts = awsOptions.Value.Accounts
            .Where(a => a.Name is not null)
            .Select(a => new { name = a.Name, type = "standalone", region = a.Region })
            .ToList();

        return JsonSerializer.Serialize(new { clusters, accounts });
    }
}

[McpServerResourceType]
public sealed class InvestigatorSkillResources(IWebHostEnvironment env)
{
    [McpServerResource(
        UriTemplate = "investigator://skills/{name}",
        Name = "Investigation Skill",
        MimeType = "text/markdown")]
    [Description("Investigation playbook / skill document")]
    public string GetSkill(string name)
    {
        var safeName = Path.GetFileNameWithoutExtension(name);
        var skillPath = Path.Combine(env.ContentRootPath, "skills", $"{safeName}.md");

        if (!File.Exists(skillPath))
            throw new FileNotFoundException($"Skill not found: {name}");

        return File.ReadAllText(skillPath);
    }
}
