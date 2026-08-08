# WithLove Gift Shop

<img src="docs/image.png" alt="WithLove Gift Shop" width="70%" />

WithLove is a sample e-commerce applicaiton that shows how AI can be integrated into a web application. It includes a curated gift shop with hybrid search (full-text + vector), and an AI-powered chat shopping assistant. 
The sample also uses OpenAI models for inference and embedding generation.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for Redis, SQL Server, and Temporal containers)
- [Stripe CLI](https://github.com/stripe/stripe-cli) — for local webhook forwarding (`brew install stripe/stripe-cli/stripe` on macOS)
- **OpenAI API key** — used by the chat assistant and embedding generation
- **Stripe API keys** (test mode) — used for checkout
- **Optional: Arize AX API key and Space ID** — sends detailed chat-agent traces to Arize AX

## Configuration

API keys are defined once in the AppHost using [Aspire parameters](https://learn.microsoft.com/dotnet/aspire/fundamentals/external-parameters) and automatically injected into each project as environment variables. No need to duplicate keys across project appsettings files.

Set up secrets using the Aspire CLI (Aspire 13.2+). Run from the repo root — Aspire auto-discovers the AppHost:

```bash
aspire secret set Parameters:openai-api-key "<your-openai-key>"
aspire secret set Parameters:stripe-api-key "<your-stripe-secret-key>"
aspire secret set Parameters:stripe-public-key "<your-stripe-public-key>"
aspire secret set Parameters:stripe-webhook-secret "<whsec_...>"  # printed by stripe listen on first run
aspire secret set Parameters:redisCache-password "<local-redis-password>"
```

Verify your secrets are stored:

```bash
aspire secret list
```

To retrieve a single secret:

```bash
aspire secret get Parameters:openai-api-key
```

The AppHost injects these values into the appropriate projects.

### Optional: detailed GenAI telemetry in Arize AX

The chat assistant emits OpenTelemetry GenAI spans for every model call and tool execution. To
send those traces to [Arize AX](https://arize.com/docs/ax), set these environment variables in the
same shell used to run Aspire:

```bash
export ARIZE_API_KEY="<your-arize-api-key>"
export ARIZE_SPACE_ID="<your-arize-space-id>"
export ARIZE_PROJECT_NAME="withlove-giftshop-demo"
```

Arize configuration is optional. When all three variables are present, the workflow server exports
traces to AX over OTLP/HTTP. When they are absent, the app keeps its normal Aspire telemetry setup.
Set `OTEL_EXPORTER_OTLP_ENDPOINT` to use another OTLP collector instead; it takes precedence over
the Arize configuration.

The trace for a chat turn is structured as one `invoke_agent LA` span, with model `chat` spans and
`execute_tool` spans beneath it. This keeps the tool call, its result, and the follow-up model call
correlated under the same trace.

By default, request and response contents are not captured. This avoids exporting customer and
prompt data. Enable full content capture only in an environment where that data is approved for
telemetry:

```bash
export WITHLOVE_GENAI_CAPTURE_CONTENT=true
```

## Running Locally

### Prerequisites Check

Before running, ensure:

1. **Docker Desktop is running** — Required for Redis, SQL Server, and Temporal containers

### Build & Run

Build the solution:

```bash
dotnet build
```

Run the Aspire AppHost

```bash
aspire run
```

Opening Aspire dashboard should show:

- **Aspire Dashboard** — resource health, logs, traces, and metrics
- **Shop Frontend** — the Blazor Web app storefront
- **Products API** — REST endpoints with Scalar docs at `/scalar`
- **Redis Insight** — cache inspection dashboard
- **DbGate** — SQL Server browser
- **Stripe CLI** — local webhook forwarding
- **Temporal Server** — local dev server

To stop, press `Ctrl+C` in the terminal.

## Key Features

- **Hybrid Search** — Full-text search (SQL Server FTS) combined with vector similarity (OpenAI embeddings), merged via Reciprocal Rank Fusion
- **Chat Assistant (LA)** — Temporal-backed conversational shopping assistant using `Microsoft.Extensions.AI` with tool calling for product search, cart management, and recommendations
- **Stripe Web elements** — Server-side Checkout Sessions with the Payment and Address elements integrated
- **FusionCache + Redis** — Multi-layer caching with tag-based invalidation and Redis backplane for cross-instance sync
- **Temporal Workflows** — Durable database setup, Stripe order processing, customer onboarding, and long-lived chat sessions

## Temporal Workflows

The application uses Temporal for durable, long-lived operations:

| Workflow | Purpose | Key Features |
|----------|---------|--------------|
| **ChatAgentWorkflow** | Long-lived chat session per user | 24h idle timeout; resumable via `IdConflictPolicy.UseExisting`; Update/Query/Signal pattern for async message handling |
| **DatabaseSetupWorkflow** | Schema initialization on app startup | Runs full-text search index creation, vector column setup, and initial data seeding; executes once per deployment |
| **StripeCheckoutOrderWorkflow** | Order processing pipeline | Coordinates Stripe Checkout Session creation, webhook verification, and order fulfillment with retry logic |
| **CustomerOnboardingWorkflow** | New customer registration flow | Creates Stripe customer record and links to user account; ensures customer data is synced with payment processor |

**Access Temporal UI**: The Aspire dashboard provides a **Temporal UI** link showing all workflows, executions, task queues, and event histories.

## Azure Deployment

The application deploys to Azure Container Apps via the Aspire CLI.

### Additional Prerequisites

- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) — authenticated with `az login`
- [Temporal CLI](https://docs.temporal.io/cli) — for workflow management during teardown
- [just](https://just.systems) — task runner (`brew install just` on macOS)
- A [Temporal Cloud](https://cloud.temporal.io) account — the app uses Temporal Cloud in production (not the local container)

### Setup

Copy the secrets template and fill in your values:

```bash
cp .secrets.env.example .secrets.env
# Edit .secrets.env — Azure subscription details, API keys, Temporal Cloud credentials
```

### Deploy

```bash
just deploy          # deploy to azureprod (incremental — reuses cached infra state)
just deploy-clean    # deploy with fresh state (use after changing location or resource group)
```

### Destroy

```bash
just destroy         # tear down all Azure resources and wait for full deletion
```

See [docs/azure-deployment.md](docs/azure-deployment.md) for detailed configuration, environment variables, and troubleshooting.

## License

This project is licensed under the MIT License. See `LICENSE` for details.
