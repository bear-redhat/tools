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

    [McpServerResource(
        UriTemplate = "investigator://protocol",
        Name = "Investigator Protocol",
        MimeType = "text/markdown")]
    [Description("How to start, follow, and steer an investigation from a client")]
    public string GetProtocol() => """
        # Driving an investigation

        An investigation runs on the server and keeps running whether or not you are
        watching. Nothing here blocks for long: `investigate` returns immediately, and
        `poll` is bounded to 50 seconds.

        ## Start

            investigate(message) -> { conversationId, url }

        ## Follow

        Every agent message, tool call and tool result is retained and addressed by a
        monotonic `seq`. Page it with a cursor:

            get_transcript(conversationId, sinceSeq: 0)   # from the beginning
            poll(conversationId, sinceSeq: <nextSeq>)     # wait for what happens next

        Both return `{ events, nextSeq, highestSeq, truncated, gap, running,
        pendingQuestion }`. Pass the returned `nextSeq` back as `sinceSeq` to continue.
        `truncated: true` means more is already available -- call again immediately rather
        than waiting. `gap: true` means your cursor predates what is still retained, so
        some events are gone; restart from 0 if you need full history.

        Event kinds: `text`, `tool_call`, `tool_result`, `turn`, `session_ended`.
        A `tool_result` carries `requestSeq` pointing at the `tool_call` it answers, plus
        `exitCode`, `summary` and, when the output was large, `outputFile`.

        ## Read large output

        Tool output in the transcript is clipped. The full text stays on disk:

            get_output(conversationId, outputFile, offset, limit)

        Prefer reading a range over pulling a whole build log into context.

        ## Answer and steer

        When `pendingQuestion` is non-null the investigator asked you something and its
        turn has ended -- it will wait indefinitely. Answer with:

            follow_up(conversationId, message)

        You can also interject at any time while it is working; the message lands after
        the current tool call completes, never mid-call. To redirect:

            steer(conversationId, "nudge", note: "...")            # tell the lead something
            steer(conversationId, "recall", agentName: "...")      # scout reports back now
            steer(conversationId, "stand_down", agentName: "...")  # abort its in-flight tool
            steer(conversationId, "cancel")                        # stop the investigation

        ## Remediation

        When the investigator commissions a fix, that work runs in a second room with its
        own agents and its own transcript. Every read and steering tool takes an optional
        `room` argument -- omit it for the investigation, pass `"remediation"` for the fix:

            get_transcript(conversationId, sinceSeq: 0, room: "remediation")
            poll(conversationId, sinceSeq: <nextSeq>, room: "remediation")
            steer(conversationId, "cancel", room: "remediation")

        `get_status` reports `hasRemediation`; `no_such_room` means it has not started.

        ## Results

            get_status(conversationId)     # phase, cost, whether it is waiting on you
            get_findings(conversationId)   # findings, scout reports, conclusion

        ## Raw tools

        `raw_*` tools run one infrastructure command directly, with no agent involved. Use
        them when you want to check something yourself rather than delegate it. Their
        output follows the same clip-plus-`outputFile` contract; page it with
        `raw_read_output`.
        """;
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
