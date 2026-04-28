# GCP IAM Setup for the Investigator

The Investigator uses a single Workload Identity Federation (WIF) identity
on the home GCP project, then impersonates per-project service accounts in
target projects (direct, no intermediary).

```
Pod SA token (OIDC) ──WIF──> investigator@HOME_PROJECT
                                   │
                         impersonate (direct)
                                   │
                       investigator@TARGET_PROJECT
```

## Home Project Setup

Run once in the home GCP project (where the WIF pool lives).

```bash
PROJECT_ID="HOME_PROJECT_ID"
OIDC_ISSUER_URL="https://OIDC_ISSUER"   # core-ci cluster OIDC issuer
POOL_ID="investigator-pool"
PROVIDER_ID="core-ci-oidc"
SA_NAME="investigator"
K8S_NS="investigator"
K8S_SA="investigator"

# 1. Create Workload Identity Pool
gcloud iam workload-identity-pools create "$POOL_ID" \
  --project="$PROJECT_ID" \
  --location="global" \
  --display-name="Investigator WIF Pool"

# 2. Create OIDC Provider
gcloud iam workload-identity-pools providers create-oidc "$PROVIDER_ID" \
  --project="$PROJECT_ID" \
  --location="global" \
  --workload-identity-pool="$POOL_ID" \
  --display-name="core-ci OIDC" \
  --issuer-uri="$OIDC_ISSUER_URL" \
  --attribute-mapping="google.subject=assertion.sub,attribute.namespace=assertion.kubernetes.io[\"namespace\"],attribute.service_account_name=assertion.kubernetes.io[\"serviceaccount\"][\"name\"]" \
  --attribute-condition="attribute.namespace == '$K8S_NS' && attribute.service_account_name == '$K8S_SA'"

# 3. Create home service account
gcloud iam service-accounts create "$SA_NAME" \
  --project="$PROJECT_ID" \
  --display-name="Investigator" \
  --description="Home SA for the Investigator pod (WIF-bound)."

# 4. Allow the K8s SA to authenticate as this SA via WIF
POOL_FULL="projects/$PROJECT_ID/locations/global/workloadIdentityPools/$POOL_ID"
gcloud iam service-accounts add-iam-policy-binding \
  "$SA_NAME@$PROJECT_ID.iam.gserviceaccount.com" \
  --project="$PROJECT_ID" \
  --role="roles/iam.workloadIdentityUser" \
  --member="principalSet://iam.googleapis.com/$POOL_FULL/attribute.service_account_name/$K8S_SA"
```

After running these commands, note the provider audience URI for the pod's
projected token and `credential-config.json`:

```bash
echo "//iam.googleapis.com/projects/$(gcloud projects describe $PROJECT_ID --format='value(projectNumber)')/locations/global/workloadIdentityPools/$POOL_ID/providers/$PROVIDER_ID"
```

Update `openshift/configmap.yaml` (`investigator-gcp-wif`) and
`openshift/deployment.yaml` (gcp-token projected volume audience) with
this value.

## Target Project Setup

Run for each GCP project the Investigator needs access to.

```bash
TARGET_PROJECT_ID="TARGET_PROJECT_ID"
HOME_SA_EMAIL="investigator@HOME_PROJECT_ID.iam.gserviceaccount.com"
SA_NAME="investigator"

# 1. Create target service account
gcloud iam service-accounts create "$SA_NAME" \
  --project="$TARGET_PROJECT_ID" \
  --display-name="Investigator" \
  --description="Target SA for the Investigator (impersonated by home SA)."

# 2. Grant home SA permission to impersonate this SA
gcloud iam service-accounts add-iam-policy-binding \
  "$SA_NAME@$TARGET_PROJECT_ID.iam.gserviceaccount.com" \
  --project="$TARGET_PROJECT_ID" \
  --role="roles/iam.serviceAccountTokenCreator" \
  --member="serviceAccount:$HOME_SA_EMAIL"

# 3. Grant target SA read-only access to the project
gcloud projects add-iam-policy-binding "$TARGET_PROJECT_ID" \
  --role="roles/viewer" \
  --member="serviceAccount:$SA_NAME@$TARGET_PROJECT_ID.iam.gserviceaccount.com"
```

Add additional `--role` bindings as needed for the target SA.
