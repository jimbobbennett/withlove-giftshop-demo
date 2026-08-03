# Azure Deployment Guide — WithLove Gift Shop

This guide walks through deploying the WithLove Gift Shop to Azure Container Apps using the Aspire CLI. It assumes you know .NET but have not deployed an Aspire app before.

## Prerequisites

- .NET 10 SDK
- Azure CLI (`az`) — authenticate with `az login`
- Aspire CLI — ships with the .NET Aspire workload (`dotnet workload install aspire`)
- Temporal Cloud account with a namespace provisioned and an API key
- Stripe account with API keys
- OpenAI account with an API key (used for embeddings and chat)

## Architecture overview

```
Internet
   │
   ▼
Azure Container Apps Environment ("withlove-env")
   ├── shopSite      (external HTTPS — user traffic + Stripe webhooks)
   ├── productsApi   (internal)
   └── workflowServer (internal)
         │
         ├── Azure SQL Database     (replaces local SQL Server container)
         ├── Redis container (ACA)  (same image as dev; no managed Redis)
         └── Temporal Cloud         (replaces local Temporal dev container)
```

The AppHost's Azure publish configuration swaps local development services for Azure-managed equivalents — except Redis. Azure Managed Redis has no Balanced SKUs available in US regions on this subscription and Azure Cache for Redis is being retired, so a Redis container is used in all environments. Cart data is ephemeral per deployment (no persistent volume in ACA). All secrets flow through Azure Key Vault, which injects values into each Container App as environment variables.

**Scaling configuration (set in AppHost):**

| Service | Min replicas | Max replicas | Scale trigger |
|---|---|---|---|
| shopSite | 1 | 10 | 100 concurrent HTTP requests per replica |
| workflowServer | 1 | 5 | CPU utilization 70% |
| productsApi | default | default | default |

workflowServer never scales to zero because it must continuously poll Temporal Cloud for tasks.

The AppHost represents the existing namespace with `TemporalCommunity.Aspire.Hosting` and
injects its connection settings into the Web and Workflow Server container apps. The Temporal
Cloud resource is excluded from deployment manifests, so deployment does not create the
namespace or register its search attributes.

shopSite uses sticky sessions — required for Blazor InteractiveServer (SignalR).

workflowServer uses TCP-only health probes because the HTTP `/health` endpoint is only exposed in development.

## Step 1 — Gather parameter values

`aspire deploy` manages its own parameter state separately from `aspire secret set`. On the first run it prompts interactively for every parameter value and caches the answers at:

```
~/.aspire/deployments/<apphost-sha256>/azureprod.json
```

Subsequent runs read from that cache — you will not be prompted again unless you add new parameters or pass `--clear-cache`.

Collect the following values before running Step 3:

| Parameter | Where to find it |
|---|---|
| `openai-api-key` | OpenAI dashboard → API keys |
| `stripe-api-key` | Stripe Dashboard → Developers → API keys → Secret key |
| `stripe-public-key` | Stripe Dashboard → Developers → API keys → Publishable key |
| `temporal-address` | Temporal Cloud → Namespace → gRPC endpoint (e.g. `your-ns.tmprl.cloud:7233`) |
| `temporal-namespace` | Temporal Cloud → Namespace name (e.g. `your-ns.acct`) |
| `temporal-api-key` | Temporal Cloud → API keys |
| `stripe-webhook-secret` | Enter `whsec_placeholder` for now — replaced in Step 5 |

> **Note:** `aspire secret set` stores values in the AppHost's local dev user secrets for `aspire run`. Those values are not read by `aspire deploy`. Use the interactive prompts (Step 3) or environment variables (see CI deploy section) to supply values to the deploy pipeline.

## Step 2 — Register Temporal Cloud search attributes (one-time)

WithLove uses two custom search attributes for workflow correlation. Register them in your Temporal Cloud namespace using `tcld`:

```bash
tcld namespace search-attribute add \
  --namespace your-ns.acct \
  --search-attribute-name StripeSessionId \
  --search-attribute-type Keyword

tcld namespace search-attribute add \
  --namespace your-ns.acct \
  --search-attribute-name CustomerId \
  --search-attribute-type Keyword
```

Alternatively, use the Temporal Cloud UI: **Namespace -> Search Attributes -> Add**.

These attributes must exist before the workflowServer starts or workflow searches will fail.

## Step 3 — Deploy

```bash
az login

# Optional: preview the steps without deploying
aspire deploy --list-steps --environment azureprod

# Deploy
aspire deploy --environment azureprod
```

The first run is interactive. Aspire prompts for:

- Azure subscription
- Azure region (for example, `eastus`)
- Resource group name (for example, `withlove-rg`)

After the deploy completes, the shopSite external URL is printed in the output, for example:

```
https://shopsite.victoriousbeach-abc123.eastus.azurecontainerapps.io
```

Keep this URL — you need it in the next step.

## Step 4 — Create the Stripe Event Destination

The Stripe CLI container used in development is replaced by a Stripe Event Destination in production. This destination sends webhook events to shopSite.

1. Go to **Stripe Dashboard -> Workbench -> Webhooks -> Create an event destination**
2. Event source: **Your account**
3. Payload format: **Snapshot** (preserves the v1 object format — no code changes needed)
4. Subscribe to events: `checkout.session.completed`, `checkout.session.expired`
5. Destination type: **Webhook endpoint**
6. URL: `https://{your-shopsite-domain}/stripe/webhook`
7. Save. On the destination detail page, click **"Click to reveal"** next to the signing secret and copy the `whsec_...` value.

## Step 5 — Update the webhook secret and redeploy

`aspire deploy` manages its own parameter cache (see Step 1) independently of `aspire secret set`. Running `aspire secret set` here would update the local dev user secrets — not the deploy cache — so the placeholder would remain and Stripe webhook verification would fail.

Pass the real secret as an environment variable on the deploy command instead:

```bash
Parameters__stripe_webhook_secret="whsec_<your-real-secret>" \
aspire deploy --environment azureprod
```

This injects the value directly into the deploy pipeline for this run without altering the cache or requiring `--clear-cache`. The deploy writes the updated secret to Key Vault and Container Apps picks it up.

## Verification checklist

After deploy completes:

1. Navigate to the shopSite external URL — the app loads and products display
2. Complete a test purchase using a [Stripe test card](https://docs.stripe.com/testing) — the order confirmation page appears and the webhook arrives
3. Navigate to `/account/loyalty` — points are earned and the Temporal workflow is running
4. In the Temporal Cloud UI, confirm a `loyalty-{userId}` workflow shows status Running
5. In the Azure portal, open the workflowServer Container App logs and confirm: `Connected to Temporal Cloud, processing tasks from with-love-tasks`
6. Confirm workflowServer has at least one replica running:

```bash
az containerapp replica list \
  --resource-group withlove-rg \
  --name workflowserver
```

## Non-interactive / CI deploy

Pass all values as environment variables on the `aspire deploy` process. Parameter names with dashes become underscores in environment variable names:

```bash
Azure__SubscriptionId="<subscription-id>" \
Azure__Location="eastus" \
Azure__ResourceGroup="withlove-rg" \
Parameters__openai_api_key="<key>" \
Parameters__stripe_api_key="<key>" \
Parameters__stripe_public_key="<key>" \
Parameters__temporal_address="your-ns.tmprl.cloud:7233" \
Parameters__temporal_namespace="your-ns.acct" \
Parameters__temporal_api_key="<key>" \
Parameters__stripe_webhook_secret="whsec_..." \
aspire deploy --environment azureprod --non-interactive
```

## Secret rotation

**Stripe webhook secret:** Rotate in Stripe Dashboard -> Webhooks -> select destination -> **Rotate secret**. Stripe accepts signatures from both the old and new secret for 24 hours. Pass the new value via environment variable on the deploy command (same as Step 5 — `aspire secret set` updates local dev secrets, not the deploy cache):

```bash
Parameters__stripe_webhook_secret="whsec_<new-secret>" \
aspire deploy --environment azureprod
```

**All other secrets:** Pass the new value as an environment variable on the deploy command. Key Vault secrets are picked up by Container Apps within approximately 30 minutes automatically — a forced restart is not required for non-critical rotations.

```bash
Parameters__openai_api_key="<new-key>" aspire deploy --environment azureprod
```

## Teardown

```bash
aspire destroy --environment azureprod
```

This removes all Azure resources provisioned for this environment.

## Generated files (do not commit)

Aspire generates the following files during deploy. They are listed in `.gitignore` and must not be committed:

```
/infra/              # Bicep templates (regenerated from AppHost on each deploy)
azure.yaml           # azd manifest
aspire-manifest.json
.azure/              # azd environment state
```

If you prefer the `azd` CLI over `aspire deploy`:

```bash
azd auth login
azd init          # regenerates azure.yaml and infra/ from the AppHost
azd up --environment azureprod
```

`azd up` and `aspire deploy` produce the same deployment — they use the same underlying Bicep generation from the AppHost.

## Useful commands

| Command | Purpose |
|---|---|
| `aspire secret list` | Show all stored secrets (values masked) |
| `aspire secret get <key>` | Read a single secret value |
| `aspire secret delete <key>` | Remove a secret |
| `aspire secret path` | Show path to the secrets JSON file |
| `aspire deploy --list-steps --environment azureprod` | Preview deploy steps without executing |
| `az containerapp logs show --name shopsite --resource-group withlove-rg` | Stream shopSite logs |
| `az containerapp replica list --name workflowserver --resource-group withlove-rg` | Check workflowServer replicas |
