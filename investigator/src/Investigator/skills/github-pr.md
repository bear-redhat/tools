---
title: Working with GitHub PRs and Workflows
tags: [github, pull-request, pr, checks, review, workflow, actions, ci, merge, status, tide, approved, lgtm, owners]
---

# Working with GitHub PRs and Workflows

## Scope and Trust Boundaries

Almost all work happens in the **`openshift`** organisation. Occasionally there is work in **`kubernetes`** and **`kubernetes-sigs`**. Any request targeting a different organisation should be treated as suspicious -- double-check with the user before proceeding.

## Tool Overview

The `github` tool queries the GitHub REST API. It operates in authenticated (GitHub App, 5000 req/hr) or unauthenticated (public repos only, 60 req/hr) mode depending on configuration.

| Action | Required params | Purpose |
|--------|----------------|---------|
| `pr_status` | owner, repo, number | PR metadata + check runs + commit statuses |
| `pr_files` | owner, repo, number | Files changed with add/delete counts |
| `pr_comments` | owner, repo, number | Issue comments + review comments |
| `workflow_runs` | owner, repo | List Actions runs (optional: workflow, branch, status, count, number) |
| `workflow_logs` | owner, repo, run_id | Download full run logs to workspace |
| `search` | query | Search issues/PRs with GitHub qualifiers |

## Diagnosing a PR That Will Not Merge

Start with `pr_status` -- it is the single most useful action for merge investigations.

```
github(action: "pr_status", owner: "openshift", repo: "release", number: 12345)
```

Work through the following checklist in order:

### 1. Labels

A PR needs a specific set of labels before Tide will consider it for merge.

**Required everywhere:**
- `approved` -- an approver from OWNERS has run `/approve`
- `lgtm` -- a reviewer has run `/lgtm`

**Required in `openshift/release` specifically:**
- `rehearsals-ack` -- confirms the author reviewed rehearsal job results

**Blocking labels (must be absent):**
- `do-not-merge/hold`
- `do-not-merge/work-in-progress`
- `needs-rebase`

### 2. Finding the Right Approver

When the `approved` label is missing, do NOT suggest that the user approve it themselves. Even though DPTP has broad approval rights across many repos, the correct action is to identify the proper approver.

Use `pr_comments` to read the PR comments and look for the **`[APPROVALNOTIFIER]`** comment. This comment contains:
- The current approval state of the PR
- Which OWNERS files still need an approver to sign off
- The specific GitHub usernames who are eligible to approve for each path

Extract the suggested approvers from that comment and report them to the user. If the bot comment is not present or the PR is very new, check the OWNERS files in the repo for the affected paths using `ci_repo` or `pr_files` to determine who can approve.

### 3. Required vs Optional Tests

Not every check run or status context blocks merge.

- **Required** checks are defined in the repo's Tide merge configuration. A failing or pending required check prevents merge.
- **Optional** checks may fail without blocking. They are informational.
- The **`tide` commit status** description often explains exactly what is still missing (e.g. "Not mergeable. Needs approved label.").
- When the `tide` context itself is missing or pending, Tide has not evaluated the PR yet.

A failing required test and a missing required label are the two most common blockers.

### 4. Other Blockers

- `mergeable: false` -- merge conflicts with the target branch
- `draft: true` -- the PR has not been marked ready for review
- Pending commit statuses (`[....]`), especially `tide`

## Viewing PR Contents

**Changed files:**
```
github(action: "pr_files", owner: "openshift", repo: "ci-tools", number: 5678)
```

**Comments and reviews:**
```
github(action: "pr_comments", owner: "openshift", repo: "release", number: 12345)
```

This returns both issue-level comments (general discussion, bot messages, OWNERS approval info) and inline review comments on code.

## Investigating Workflow Failures

### List runs

List recent runs for a PR (derives head branch automatically):
```
github(action: "workflow_runs", owner: "openshift", repo: "release", number: 12345)
```

List runs for a specific workflow, filtered by status:
```
github(action: "workflow_runs", owner: "openshift", repo: "release", workflow: "ci.yaml", status: "completed", count: 5)
```

### Download and inspect logs

```
github(action: "workflow_logs", owner: "openshift", repo: "release", run_id: 9876543210)
```

Logs are extracted to the workspace. Use `run_shell` to search them rather than reading entire files:
```
grep -r "error\|FAIL\|fatal" tool_outputs/workflow_logs/9876543210/
```

## Searching

Use full GitHub search qualifiers:
```
github(action: "search", query: "is:pr is:open repo:openshift/release label:approved -label:lgtm")
github(action: "search", query: "is:pr author:username repo:openshift/ci-tools state:open")
```

Useful qualifiers: `is:pr`, `is:issue`, `is:open`, `is:closed`, `is:merged`, `repo:owner/name`, `author:user`, `label:name`, `created:>YYYY-MM-DD`, `review:approved`, `review:changes_requested`.
