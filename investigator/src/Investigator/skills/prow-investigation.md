---
title: Investigating Failed ProwJobs
tags: [prow, prowjob, spyglass, test-failure, ci, build-log, gcs, gcsweb, artifacts, junit, ci-operator, tide]
---

# Investigating Failed ProwJobs

Use the `prow` tool for all Prow CI interactions. It handles URL parsing, artifact fetching, log downloads, and Tide status natively.

## When a user shares a Prow link

Always start with `resolve_url` to extract structured coordinates:

```
prow action=resolve_url url="https://prow.ci.openshift.org/view/gs/test-platform-results/pr-logs/pull/org_repo/1234/job-name/1234567890"
```

This returns the bucket, storage_path, job_name, and build_id -- use these with other actions.

## Investigation workflow

1. **Get job status** -- confirms pass/fail, timestamps, metadata:
   ```
   prow action=job_status storage_path="pr-logs/pull/org_repo/1234/job-name/1234567890"
   ```

2. **Download the build log** -- the log is saved to disk, use `run_shell` to search it:
   ```
   prow action=log storage_path="pr-logs/pull/org_repo/1234/job-name/1234567890"
   ```
   Then: `run_shell grep -i 'error\|fail' "<path from above>" | head -50`

3. **Parse JUnit results** -- see which specific tests failed:
   ```
   prow action=junit storage_path="pr-logs/pull/org_repo/1234/job-name/1234567890"
   ```

4. **Browse artifacts** -- list must-gather data, container logs, step outputs:
   ```
   prow action=artifacts storage_path="pr-logs/pull/org_repo/1234/job-name/1234567890"
   prow action=artifacts storage_path="pr-logs/pull/org_repo/1234/job-name/1234567890" path="artifacts/test-name/"
   ```

5. **Check job config** -- `job_status` includes prowjob.json summary (cluster, refs, job type)

## Finding jobs without a URL

List recent runs by job name, org/repo, PR, state, or type:

```
prow action=jobs job_name="pull-ci-openshift-release" state="failure" count=10
prow action=jobs org="openshift" repo="release" pr=1234
```

## Tide merge status

Check why a PR isn't merging:

```
prow action=tide org="openshift" repo="release"
```

Shows required labels, blocking labels, and which PRs are ready/pending/missing requirements.

## Key artifacts in every job run

| File | Description |
|------|-------------|
| `finished.json` | Pass/fail result, timestamps, metadata (repo, commit, namespace) |
| `started.json` | Start time, metadata |
| `prowjob.json` | Full ProwJob spec (cluster, job type, refs, decoration config) |
| `build-log.txt` | The complete build/test log from ci-operator |
| `prowjob_junit.xml` | JUnit XML test results (which tests passed/failed) |
| `artifacts/` | Directory containing all test artifacts (must-gather, logs, etc.) |

## Common build log patterns

After downloading with `prow action=log`, search with `run_shell`:

- `"step .* failed"` -- a ci-operator step failed
- `"error creating"` -- resource creation failure
- `"OOMKilled"` -- memory limit exceeded
- `"DeadlineExceeded"` -- timeout
- `"ImagePullBackOff"` -- image not found or registry auth issue
- `"quota"` -- resource quota exhaustion
