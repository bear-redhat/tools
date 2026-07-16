using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

public sealed class UxProjector
{
    private readonly string _leadId;
    private readonly Dictionary<int, RoomEvent.ToolRequest> _pendingRequests = new();
    private readonly Dictionary<string, RoomEvent.ToolRequest> _pendingConcludes = new();
    private readonly Dictionary<string, string?> _concludeSummaries = new();
    private readonly HashSet<string> _trackedMemberIds = new();

    public UxProjector(string leadId) => _leadId = leadId;

    public IEnumerable<UxEvent> Project(RoomEvent evt)
    {
        if (evt is RoomEvent.ToolRequest tr)
        {
            _pendingRequests[tr.Seq] = tr;
            if (tr.Tool == "conclude" && tr.From != _leadId)
                _pendingConcludes[tr.From] = tr;
        }

        return evt switch
        {
            RoomEvent.TextMessage tm => ProjectText(tm),
            RoomEvent.ToolRequest req => ProjectToolRequest(req),
            RoomEvent.ToolResponse res => ProjectToolResponse(res),
            RoomEvent.AgentTurn at => ProjectAgentTurn(at),
            RoomEvent.SessionEnded => ProjectSessionEnded(),
            _ => [],
        };
    }

    private IEnumerable<UxEvent> ProjectText(RoomEvent.TextMessage tm)
    {
        if (tm.From == "user")
        {
            yield return new AddConversationItem(new ConversationItem.UserMessage
            {
                Content = tm.Text,
                Timestamp = tm.Timestamp,
            });
            yield break;
        }

        if (tm.From == _leadId)
        {
            yield return new AddConversationItem(new ConversationItem.AgentMessage
            {
                LeadId = _leadId,
                StepId = tm.Seq.ToString(),
                Content = tm.Text,
                RecipientId = tm.To,
                Timestamp = tm.Timestamp,
            });
            yield break;
        }

        if (tm.From == "system")
        {
            if (tm.To is not null)
                yield break;

            yield return new AddConversationItem(new ConversationItem.Error
            {
                LeadId = _leadId,
                StepId = tm.Seq.ToString(),
                Content = tm.Text,
                Timestamp = tm.Timestamp,
            });
            yield break;
        }

        if (tm.To == _leadId)
        {
            var scoutId = tm.From;

            if (_pendingConcludes.Remove(scoutId, out var concludeReq))
            {
                var (evidence, fix, _) = ParseConcludeInput(concludeReq.Input);

                yield return new AddConversationItem(new ConversationItem.ScoutReport
                {
                    LeadId = _leadId,
                    ScoutId = scoutId,
                    StepId = tm.Seq.ToString(),
                    Report = tm.Text,
                    Summary = _concludeSummaries.GetValueOrDefault(scoutId),
                    Evidence = evidence,
                    Fix = fix,
                    Timestamp = tm.Timestamp,
                });
            }
            else
            {
                yield return new AddConversationItem(new ConversationItem.ScoutQuestion
                {
                    LeadId = _leadId,
                    ScoutId = scoutId,
                    StepId = tm.Seq.ToString(),
                    Question = tm.Text,
                    Timestamp = tm.Timestamp,
                });
            }
            yield break;
        }

        yield return new AddConversationItem(new ConversationItem.ScoutThinking
        {
            ScoutId = tm.From,
            StepId = tm.Seq.ToString(),
            Content = tm.Text,
            Timestamp = tm.Timestamp,
        });
    }

    private IEnumerable<UxEvent> ProjectToolRequest(RoomEvent.ToolRequest tr)
    {
        var entry = new LogEntryModel
        {
            Sender = tr.From,
            StepId = tr.Seq.ToString(),
            Tool = tr.Tool,
            DisplayCommand = tr.DisplayCommand ?? tr.Tool,
            Timestamp = tr.Timestamp,
            Status = LogEntryStatus.Running,
            Context = AgentRunner.ExtractContext(tr.Tool, tr.Input),
        };

        if (tr.ParentSeq is not null)
        {
            yield return new AddChildLogEntry(tr.ParentSeq.Value.ToString(), entry);
            yield break;
        }

        yield return new AddLogEntry(entry);
    }

    private IEnumerable<UxEvent> ProjectToolResponse(RoomEvent.ToolResponse tres)
    {
        _pendingRequests.TryGetValue(tres.RequestSeq, out var req);

        if (tres.Tool == "delegate" && req is not null)
        {
            var role = Prop(req.Input, "role");
            var task = Prop(req.Input, "task");
            var model = Prop(req.Input, "model");
            var agentName = ExtractAgentName(tres.Output);
            var agentId = agentName.ToLowerInvariant().Replace(" ", "-");

            _trackedMemberIds.Add(agentId);
            yield return new AddMember(agentName, agentId);
            yield return new AddConversationItem(new ConversationItem.Dispatch
            {
                LeadId = _leadId,
                ScoutId = agentId,
                StepId = tres.Seq.ToString(),
                Task = task,
                Role = role,
                ModelProfile = model,
                Timestamp = tres.Timestamp,
            });
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "conclude" && req?.From == _leadId && req is not null)
        {
            var (evidence, fix, summary) = ParseConcludeInput(req.Input);

            yield return new SetInvestigating(false);
            yield return new SetMemberStatus(_leadId, MemberStatus.Idle);
            yield return new AddConversationItem(new ConversationItem.Conclusion
            {
                LeadId = _leadId,
                StepId = tres.Seq.ToString(),
                Content = summary,
                Headline = tres.Summary,
                Evidence = evidence,
                Fix = fix,
                Timestamp = tres.Timestamp,
            });
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "conclude")
        {
            if (req is not null)
            {
                yield return new SetMemberStatus(req.From, MemberStatus.Idle);
                _concludeSummaries[req.From] = tres.Summary;
            }
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "present_finding" && req is not null)
        {
            var title = Prop(req.Input, "title");
            var desc = Prop(req.Input, "description");
            yield return new AddConversationItem(new ConversationItem.Finding
            {
                LeadId = _leadId,
                StepId = tres.Seq.ToString(),
                Title = title,
                Description = desc,
                Summary = tres.Summary,
                Timestamp = tres.Timestamp,
            });
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "report_progress" && req is not null)
        {
            var title = Prop(req.Input, "title");
            var desc = Prop(req.Input, "description");
            yield return new AddConversationItem(new ConversationItem.Finding
            {
                LeadId = _leadId,
                StepId = tres.Seq.ToString(),
                Title = title,
                Description = desc,
                Summary = tres.Summary,
                Timestamp = tres.Timestamp,
            });
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "present_plan" && req is not null)
        {
            var plan = ParsePlanInput(req.Input);
            if (plan is not null)
            {
                yield return new SetPlan(plan);
                yield return new AddConversationItem(new ConversationItem.PlanItem
                {
                    LeadId = _leadId,
                    StepId = tres.Seq.ToString(),
                    Plan = plan,
                    Timestamp = tres.Timestamp,
                });
            }
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "update_step" && req is not null)
        {
            var stepId = Prop(req.Input, "id");
            var statusStr = Prop(req.Input, "status");
            if (stepId is not null && Enum.TryParse<StepStatus>(statusStr, true, out var status))
                yield return new UpdatePlanStep(stepId, status,
                    Prop(req.Input, "note"), Prop(req.Input, "patch_file"));
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "sign_off" && req is not null)
        {
            var (outcome, actions, verification, remaining, warnings) = ParseSignOffInput(req.Input);
            yield return new SetInvestigating(false);
            yield return new SetMemberStatus(_leadId, MemberStatus.Idle);
            yield return new AddConversationItem(new ConversationItem.SignOffItem
            {
                LeadId = _leadId,
                StepId = tres.Seq.ToString(),
                Outcome = outcome,
                ActionsTaken = actions,
                Verification = verification,
                Remaining = remaining,
                Warnings = warnings,
                Timestamp = tres.Timestamp,
            });
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "message" && req is not null)
        {
            var to = Prop(req.Input, "to");
            var isBlocking = req.From != _leadId || to is "user" or "client";
            if (isBlocking)
            {
                yield return new SetMemberStatus(req.From, MemberStatus.Idle);
                if (req.From == _leadId)
                    yield return new SetInvestigating(false);
            }
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "dismiss" && req is not null)
        {
            var name = Prop(req.Input, "agent_name");
            if (name is not null)
            {
                var scoutId = name.ToLowerInvariant().Replace(" ", "-");
                yield return new SetMemberStatus(scoutId, MemberStatus.Idle);
            }
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool == "casebook" && req is not null)
        {
            var action = Prop(req.Input, "action");
            if (action is "save" or "search")
            {
                yield return new AddConversationItem(new ConversationItem.CasebookActivity
                {
                    LeadId = _leadId,
                    StepId = tres.Seq.ToString(),
                    Action = action,
                    Title = action == "save" ? Prop(req.Input, "title") : null,
                    Query = action == "search" ? Prop(req.Input, "query") : null,
                    ResultCount = action == "search" ? ExtractResultCount(tres.Output) : null,
                    Timestamp = tres.Timestamp,
                });
            }
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        if (tres.Tool is "check_agents" or "recall" or "review_plan" or "commission_remedy" or "refer_back")
        {
            yield return new UpdateLogEntry(tres.RequestSeq, LogEntryStatus.Completed, tres.Output);
            yield break;
        }

        var entryStatus = tres.ExitCode == -2 ? LogEntryStatus.Aborted
                        : tres.TimedOut ? LogEntryStatus.TimedOut
                        : LogEntryStatus.Completed;
        yield return new UpdateLogEntry(
            tres.RequestSeq, entryStatus,
            tres.Output, tres.OutputFile, tres.ExitCode);
    }

    private IEnumerable<UxEvent> ProjectAgentTurn(RoomEvent.AgentTurn at)
    {
        TurnUsage? turnUsage = null;
        if (at.Usage is not null)
        {
            var cost = AgentRunner.ComputeCost(at.Usage,
                at.InputPrice, at.OutputPrice, at.CacheReadPrice, at.CacheCreatePrice);
            turnUsage = new TurnUsage
            {
                InputTokens = at.Usage.InputTokens,
                OutputTokens = at.Usage.OutputTokens,
                CacheReadTokens = at.Usage.CacheReadInputTokens,
                CacheCreateTokens = at.Usage.CacheCreationInputTokens,
                Cost = cost,
            };
        }

        if (!string.IsNullOrEmpty(at.ThinkingText))
        {
            if (at.From == _leadId)
            {
                yield return new AddConversationItem(new ConversationItem.Thinking
                {
                    LeadId = _leadId,
                    StepId = at.Seq.ToString(),
                    Content = at.ThinkingText,
                    Timestamp = at.Timestamp,
                    Usage = turnUsage,
                });
            }
            else
            {
                yield return new AddConversationItem(new ConversationItem.ScoutThinking
                {
                    ScoutId = at.From,
                    StepId = at.Seq.ToString(),
                    Content = at.ThinkingText,
                    Timestamp = at.Timestamp,
                    Usage = turnUsage,
                });
            }
        }

        if (at.Usage is not null)
        {
            yield return new AddUsage(at.From, at.Usage, turnUsage!.Cost, at.ModelProfile,
                at.InputPrice, at.OutputPrice, at.CacheReadPrice, at.CacheCreatePrice);
        }

        if (at.CompactedMessages > 0)
        {
            yield return new AddLogEntry(new LogEntryModel
            {
                Sender = at.From,
                StepId = at.Seq.ToString(),
                Tool = "compaction",
                DisplayCommand = $"Context compacted: {at.CompactedMessages} messages removed",
                Timestamp = at.Timestamp,
                Status = LogEntryStatus.Completed,
            });
            yield break;
        }

        if (at.IsNewTurn)
        {
            if (at.From == _leadId)
            {
                yield return new SetInvestigating(true);
                yield return new SetMemberStatus(_leadId, MemberStatus.Active);
            }
            else
            {
                yield return new SetMemberStatus(at.From, MemberStatus.Working);
            }
        }
    }

    private IEnumerable<UxEvent> ProjectSessionEnded()
    {
        yield return new SetInvestigating(false);
        yield return new SetMemberStatus(_leadId, MemberStatus.Idle);
        foreach (var id in _trackedMemberIds)
            yield return new SetMemberStatus(id, MemberStatus.Idle);
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ExtractAgentName(string output)
    {
        const string prefix = "Dispatched ";
        if (!output.StartsWith(prefix)) return output;
        var rest = output[prefix.Length..];
        var idx = rest.IndexOf(" (", StringComparison.Ordinal);
        return idx > 0 ? rest[..idx] : output;
    }

    private static string[]? ParseTags(JsonElement input)
    {
        if (!input.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
            return null;
        var tags = tagsEl.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        return tags.Length > 0 ? tags! : null;
    }

    private static string? ExtractMemoryId(string output)
    {
        const string prefix = "Memory saved (id: ";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = output.IndexOf(',', start);
        if (end < 0) end = output.IndexOf(')', start);
        return end > start ? output[start..end] : null;
    }

    private static int? ExtractResultCount(string output)
    {
        const string prefix = "Found ";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return output.Contains("Memory is empty") || output.Contains("No matching") ? 0 : null;
        start += prefix.Length;
        var end = output.IndexOf(' ', start);
        return end > start && int.TryParse(output[start..end], out var count) ? count : null;
    }

    internal static (EvidenceChain?, FixSuggestion?, string?) ParseConcludeInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return (null, null, null);

        var summary = Prop(input, "summary");

        EvidenceChain? evidence = null;
        if (input.TryGetProperty("evidence", out var ea) && ea.ValueKind == JsonValueKind.Array)
        {
            var steps = new List<EvidenceStep>();
            foreach (var item in ea.EnumerateArray())
            {
                steps.Add(new EvidenceStep(
                    Step: item.TryGetProperty("step", out var st) ? st.GetInt32() : steps.Count + 1,
                    Reasoning: Prop(item, "reasoning"),
                    Finding: Prop(item, "finding"),
                    Cluster: Prop(item, "cluster"),
                    Proof: Prop(item, "proof") ?? Prop(item, "command"),
                    Source: Prop(item, "source")));
            }
            evidence = new EvidenceChain(steps.OrderBy(s => s.Step).ToList());
        }

        FixSuggestion? fix = null;
        var hasFd = input.TryGetProperty("fix_description", out var fd) && fd.ValueKind == JsonValueKind.String;
        var hasFc = input.TryGetProperty("fix_commands", out var fc) && fc.ValueKind == JsonValueKind.Array;
        if (hasFd || hasFc)
        {
            fix = new FixSuggestion(
                Description: hasFd ? fd.GetString() : null,
                Commands: hasFc ? fc.EnumerateArray().Select(c => c.GetString()).Where(c => c is not null).ToList()! : null,
                Warning: Prop(input, "fix_warning"));
        }

        return (evidence, fix, summary);
    }

    private static (string?, IReadOnlyList<SignOffAction>, string?, string?, string?) ParseSignOffInput(JsonElement input)
    {
        var outcome = Prop(input, "outcome");
        var actions = new List<SignOffAction>();
        if (input.TryGetProperty("actions_taken", out var at) && at.ValueKind == JsonValueKind.Array)
            foreach (var item in at.EnumerateArray())
                actions.Add(new SignOffAction(Prop(item, "plan_step_id"), Prop(item, "summary")));
        return (outcome, actions, Prop(input, "verification"), Prop(input, "remaining"), Prop(input, "warnings"));
    }

    private static RemediationPlan? ParsePlanInput(JsonElement input)
    {
        if (!input.TryGetProperty("steps", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var steps = new List<RemediationStep>();
        foreach (var s in arr.EnumerateArray())
        {
            steps.Add(new RemediationStep
            {
                Id = Prop(s, "id"),
                Title = Prop(s, "title"),
                Rationale = Prop(s, "rationale"),
                Target = ParseTarget(s),
                Change = ParseChange(s),
                Validation = ParseValidation(s),
            });
        }
        return new RemediationPlan { Steps = steps };
    }

    private static RemediationTarget ParseTarget(JsonElement s) =>
        s.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object
            ? new(Prop(t, "type"), Prop(t, "cluster"), Prop(t, "resource"),
                  Prop(t, "namespace"), Prop(t, "repo"), Prop(t, "path"), Prop(t, "line_range"))
            : new(Type: null);

    private static RemediationChange ParseChange(JsonElement s) =>
        s.TryGetProperty("change", out var c) && c.ValueKind == JsonValueKind.Object
            ? new()
            {
                Type = Prop(c, "type"),
                CurrentValue = Prop(c, "current_value"),
                DesiredValue = Prop(c, "desired_value"),
                Commands = c.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
                    ? cmds.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToList()! : null,
                PatchFile = Prop(c, "patch_file"),
                Warnings = Prop(c, "warnings"),
            }
            : new();

    private static RemediationValidation ParseValidation(JsonElement s) =>
        s.TryGetProperty("validation", out var v) && v.ValueKind == JsonValueKind.Object
            ? new(Prop(v, "description"),
                  v.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
                      ? cmds.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToList()! : null,
                  Prop(v, "expected"))
            : new(null, null);
}
