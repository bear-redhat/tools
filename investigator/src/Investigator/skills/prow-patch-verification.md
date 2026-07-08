---
title: Verifying Patches with Prow CI
tags: [prow, patch, verify, ci, test, ci-operator, prowjob, remediation]
---

# Verifying Patches with Prow CI

When the Canopy Post drafts a patch for a component, verify it passes CI before presenting it to the Client. This procedure pushes the patched source to a scratch repo and submits a ProwJob against it.

## Prerequisites

- The scratch repo `bear-redhat/ci-verify` on GitHub (a single repo used for all components)
- `oc` access to `app.ci` (cluster config at `/secrets/app-ci-token`, server `https://api.ci.l2s4.p1.openshiftapps.com:6443`)
- A clone of `openshift/release` for CI configs and the step registry

## Procedure

### 1. Prepare the patched source

```bash
# Clone the component repo
git clone https://github.com/openshift/<component>.git /tmp/verify-<component>
cd /tmp/verify-<component>

# Apply the patch
git apply /path/to/patch-file.patch

# Commit
git add -A
git commit -m "ci-verify: <description>"

# Push to scratch repo on a unique branch
BRANCH="verify/<component>-$(date +%s)"
git remote add scratch https://github.com/bear-redhat/ci-verify.git
git push scratch HEAD:refs/heads/${BRANCH}

# Record the SHA
SHA=$(git rev-parse HEAD)
echo "Pushed to bear-redhat/ci-verify branch ${BRANCH} at ${SHA}"
```

### 2. Get the ci-operator config

Fetch the resolved ci-operator config for the component. Use the configresolver API:

```bash
curl -s "https://config.ci.openshift.org/config?org=openshift&repo=<component>&branch=<branch>" > /tmp/ci-config.yaml
```

Or read it from the local release repo clone:

```bash
cat /path/to/release/ci-operator/config/openshift/<component>/openshift-<component>-<branch>.yaml
```

**Important:** If the config references step registry workflows (e.g. `workflow: ipi-gcp`), you need a fully resolved config. The configresolver returns resolved configs by default. If using a local file with unresolved references, you must also provide `--registry /path/to/release/ci-operator/step-registry` when constructing the ProwJob.

### 3. Identify the test target

Look at the config's `tests:` section to find the right target:

- For unit tests: look for targets like `unit`, `verify`, `lint`
- For e2e tests: look for targets like `e2e-gcp-olm`, `e2e-aws-ovn`, etc.
- Container tests run without a cluster; multi-stage tests with `cluster_profile` provision one

Choose the most relevant test for verifying the patch. Unit tests are fast; e2e tests are thorough but require cloud leases.

### 4. Construct the ProwJob

Build a ProwJob YAML. Key points:
- `refs` points at the scratch repo branch (controls what gets cloned)
- `CONFIG_SPEC` embeds the component's ci-operator config (controls what gets built/tested)
- `path_alias` ensures code lands at the correct Go path

```yaml
apiVersion: prow.k8s.io/v1
kind: ProwJob
metadata:
  generateName: ci-verify-<component>-
  namespace: ci
  labels:
    prow.k8s.io/type: periodic
    prow.k8s.io/job: ci-verify-<component>-<target>
spec:
  type: periodic
  job: ci-verify-<component>-<target>
  cluster: build08
  refs:
    org: bear-redhat
    repo: ci-verify
    base_ref: <BRANCH>
    base_sha: <SHA>
    path_alias: github.com/openshift/<component>
  decoration_config:
    skip_cloning: true
    timeout: 4h0m0s
    grace_period: 30m0s
  agent: kubernetes
  pod_spec:
    serviceAccountName: ci-operator
    containers:
      - image: ci-operator:latest
        command:
          - ci-operator
        args:
          - --target=<target>
          - --lease-server-credentials-file=/etc/boskos/credentials
          - --secret-dir=/secrets/ci-pull-credentials
          - --image-import-pull-secret=/etc/pull-secret/.dockerconfigjson
          - --gcs-upload-secret=/secrets/gcs/service-account.json
        env:
          - name: CONFIG_SPEC
            value: "<base64+gzipped ci-operator config>"
        resources:
          requests:
            cpu: 10m
        volumeMounts:
          - name: boskos
            mountPath: /etc/boskos
            readOnly: true
          - name: pull-secret
            mountPath: /etc/pull-secret
            readOnly: true
          - name: ci-pull-credentials
            mountPath: /secrets/ci-pull-credentials
            readOnly: true
          - name: gcs-credentials
            mountPath: /secrets/gcs
            readOnly: true
    volumes:
      - name: boskos
        secret:
          secretName: boskos-credentials
      - name: pull-secret
        secret:
          secretName: registry-pull-credentials
      - name: ci-pull-credentials
        secret:
          secretName: ci-pull-credentials
      - name: gcs-credentials
        secret:
          secretName: gce-sa-credentials-gcs-publisher
```

**Encoding CONFIG_SPEC:** ci-operator accepts the config as base64-encoded gzipped YAML in the `CONFIG_SPEC` environment variable (the same format pj-rehearse uses):

```bash
CONFIG_SPEC=$(cat /tmp/ci-config.yaml | gzip | base64 -w0)
```

**path_alias:** Set this to the component's canonical import path. For Go repos, check `canonical_go_repository` in the ci-operator config. For non-Go repos, use `github.com/openshift/<component>`.

### 5. Submit the ProwJob

```bash
oc --server=https://api.ci.l2s4.p1.openshiftapps.com:6443 \
   --token=$(cat /secrets/app-ci-token) \
   apply -f /tmp/prowjob.yaml -n ci
```

Record the ProwJob name from the output.

### 6. Monitor the job

```bash
# Check status
oc --server=https://api.ci.l2s4.p1.openshiftapps.com:6443 \
   --token=$(cat /secrets/app-ci-token) \
   get prowjob <name> -n ci -o jsonpath='{.status.state}'

# Wait for completion (poll every 60s)
while true; do
  STATE=$(oc get prowjob <name> -n ci -o jsonpath='{.status.state}' 2>/dev/null)
  if [ "$STATE" = "success" ] || [ "$STATE" = "failure" ] || [ "$STATE" = "error" ] || [ "$STATE" = "aborted" ]; then
    echo "Job completed: $STATE"
    break
  fi
  echo "Job state: $STATE -- waiting..."
  sleep 60
done
```

### 7. Get results

```bash
# Get the Prow URL for the results
oc get prowjob <name> -n ci -o jsonpath='{.status.url}'

# Use the prow tool to fetch logs and JUnit results
prow action=resolve_url url="<prow-url>"
prow action=junit storage_path="<resolved-path>"
prow action=log storage_path="<resolved-path>"
```

### 8. Clean up

```bash
# Delete the scratch branch
git push scratch --delete ${BRANCH}
```

## Practical notes

- **For unit/verify tests:** These don't need a cluster. They run in a container from the built `src` or `ci-image`. Fast (5-15 min). Always start with these.
- **For e2e tests:** These provision an ephemeral cluster via IPI workflows. Slow (45-120 min). Use only when the patch affects runtime behaviour. They require Boskos leases (cloud quota).
- **Cluster selection:** Use `build08` (GCP) as default. The cluster must have the `ci-operator` service account and required secrets.
- **Existing job as template:** Instead of constructing from scratch, find an existing ProwJob for the same test target in `openshift/release/ci-operator/jobs/openshift/<component>/` and adapt its pod spec. This ensures all volume mounts and secrets are correct.
- **Multiple tests:** You can submit multiple ProwJobs in parallel for different targets (e.g. unit + e2e).

## Alternative: simpler approach for config-only changes

If the patch only changes CI configuration (YAML in openshift/release), use `pj-rehearse` instead of this procedure. Push the config change to a release PR and comment `/pj-rehearse <job-name>`.
