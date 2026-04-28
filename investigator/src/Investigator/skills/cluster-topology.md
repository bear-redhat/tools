---
title: Build Farm Cluster Topology
tags: [cluster, build-farm, prow, hive, hypershift, hosted-mgmt, app.ci, core-ci]
---

# Build Farm Cluster Topology

## app.ci

Central CI cluster. Runs the Prow control plane: deck, hook, plank, crier, sinker, tide, and all supporting controllers. All ProwJob scheduling originates here. ProwJob custom resources live in the `ci` namespace. This is where you query job status, check Prow component health, and inspect job configs.

Key namespaces: `ci` (ProwJobs, CI services), `ci-stale` (old namespaces pending cleanup).

## core-ci

The successor to app.ci, currently being migrated. Will take over as the central CI cluster running the Prow control plane and all supporting controllers. During the migration period, workloads and services are progressively moving from app.ci to core-ci. When investigating issues, check both clusters -- some components may already be running on core-ci while others remain on app.ci until migration completes.

Key namespaces: `ci` (ProwJobs, CI services).

## buildXX (build01, build02, build03, build04, build05, build09, build10, ...)

Build farm clusters. These execute the actual CI workloads dispatched from app.ci/core-ci: ci-operator pods, test step pods, build pods, and release image mirrors. Each cluster runs independently -- a job lands on whichever build cluster the dispatcher selects (configured per job or via cluster profiles).

AWS clusters: build01, build03, build05, build06, build07, build09, build10, build11. GCP clusters: build02, build04.

When a ProwJob fails, the pod that ran it lives on one of these clusters. Check `prowjob.json` for the `cluster` field to know which one, then inspect pods/events there.

Key namespaces: `ci-op-*` (ephemeral ci-operator test namespaces), `ci` (shared CI infrastructure).

## hosted-mgmt

Management cluster for the fleet's cluster lifecycle. Runs:

- **Hive**: manages ClusterPool and ClusterDeployment resources. CI test clusters are provisioned from cluster pools here. Check ClusterPool inventory, ClusterDeployment status, and provision/deprovision logs here.
- **HyperShift operator**: manages HostedCluster and NodePool resources for hosted control plane test clusters. Hosted control planes run as pods on this cluster while worker nodes run elsewhere.

Key namespaces: `hive` (Hive operator + cluster pools), `hypershift` (HyperShift operator), `clusters` or `clusters-*` (individual HostedCluster control planes).

## AWS Access

app.ci does not support STS (no OIDC provider configured), so it has no AWS access via `run_aws`.

Build farm clusters running on AWS (build01, build03, build05-07, build09-11, hosted-mgmt, core-ci) support AWS resource investigation via `run_aws`. Use the `cluster` parameter to target a cluster's underlying AWS account:

- `run_aws(cluster="build01", command="ec2 describe-instances")` -- auto-discovers the cluster's AWS account and region
- `run_aws(cluster="core-ci", command="s3 ls")` -- works on any AWS cluster with a CCO-managed credentials secret

For standalone AWS accounts not tied to a cluster (e.g. shared artifact storage), use the `account` parameter instead:

- `run_aws(account="ci-artifacts", command="s3 ls s3://ci-artifacts-bucket")`

The `cluster` and `account` parameters are mutually exclusive. Use `cluster` for cluster-linked AWS accounts, `account` for standalone ones. The system prompt lists which clusters and accounts are available.
