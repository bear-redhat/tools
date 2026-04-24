---
title: Investigating Failed ProwJobs
tags: [prow, prowjob, spyglass, test-failure, ci, build-log, gcs, gcsweb, artifacts, junit, ci-operator]
---

# Investigating Failed ProwJobs

## URL Patterns

Users often share Prow links. There are three types:

### 1. Spyglass link (single job run)

```
https://prow.ci.openshift.org/view/gs/{bucket}/{path}/{job-id}
```

The Spyglass page is a JavaScript SPA -- the raw HTML is empty scaffolding. Do NOT try to fetch it directly. Instead, transform it to a GCS web URL by swapping the prefix:

```
https://gcsweb-ci.apps.ci.l2s4.p1.openshiftapps.com/gcs/{bucket}/{path}/{job-id}/
```

Example:
- Spyglass: `https://prow.ci.openshift.org/view/gs/test-platform-results/pr-logs/pull/org_repo/1234/job-name/1234567890`
- GCS web: `https://gcsweb-ci.apps.ci.l2s4.p1.openshiftapps.com/gcs/test-platform-results/pr-logs/pull/org_repo/1234/job-name/1234567890/`

### 2. Deck search link (job listing)

```
https://prow.ci.openshift.org/?job=periodic-ci-secret-bootstrap
```

This is also a JavaScript SPA. To get job run data, query ProwJob resources directly via `run_oc` on the app.ci cluster:

```
oc get prowjobs -n ci -l prow.k8s.io/job={job-name} --sort-by=.metadata.creationTimestamp -o json
```

### 3. Direct GCS web link

```
https://gcsweb-ci.apps.ci.l2s4.p1.openshiftapps.com/gcs/...
```

These can be fetched directly with curl via `run_shell`.

## Fetching Artifacts

Once you have the GCS web URL, fetch artifacts with `run_shell`:

```bash
curl -s https://gcsweb-ci.apps.ci.l2s4.p1.openshiftapps.com/gcs/{bucket}/{path}/{job-id}/finished.json
```

### Key artifacts in every job run

| File | Description |
|------|-------------|
| `finished.json` | Pass/fail result, timestamps, metadata (repo, commit, namespace) |
| `started.json` | Start time, metadata |
| `prowjob.json` | Full ProwJob spec (cluster, job type, refs, decoration config) |
| `build-log.txt` | The complete build/test log from ci-operator |
| `prowjob_junit.xml` | JUnit XML test results (which tests passed/failed) |
| `artifacts/` | Directory containing all test artifacts (must-gather, logs, etc.) |

### Investigation workflow

1. **Start with `finished.json`** -- confirms the failure, gives you the work namespace and pod name
2. **Check `build-log.txt`** -- the full ci-operator log. Look for error messages, step failures, timeout messages
3. **Parse `prowjob_junit.xml`** -- if tests ran, see which specific test cases failed
4. **Browse `artifacts/`** -- may contain must-gather data, container logs, or step-specific outputs
5. **Check `prowjob.json`** -- if you need to understand the job configuration (which cluster it ran on, what refs were tested)

### Browsing artifact directories

Append `/` to any GCS web directory URL to list its contents. The HTML page contains links you can parse:

```bash
curl -s "https://gcsweb-ci.apps.ci.l2s4.p1.openshiftapps.com/gcs/{bucket}/{path}/{job-id}/artifacts/" | grep -oP 'href="[^"]*"'
```

### Common build log patterns

- `"step .* failed"` -- a ci-operator step failed
- `"error creating"` -- resource creation failure
- `"OOMKilled"` -- memory limit exceeded
- `"DeadlineExceeded"` -- timeout
- `"ImagePullBackOff"` -- image not found or registry auth issue
- `"quota"` -- resource quota exhaustion
