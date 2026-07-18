set windows-shell := ["pwsh.exe", "-NoLogo", "-Command"]
set shell := ["bash", "-c"]

solution        := "WithLoveShop.slnx"
configuration   := "Debug"

# List available recipes
default:
    @just --list

# Show project info
info:
    @echo "Solution  : {{solution}}"
    @echo "Config    : {{configuration}}"

# Remove all build output
clean:
    dotnet clean {{solution}} --configuration {{configuration}} --nologo -v q
    @echo "Clean complete."

# Restore NuGet packages
restore:
    dotnet restore {{solution}}

# Build the solution
build: restore
    dotnet build {{solution}} --configuration {{configuration}} --no-restore

# Alias: build
compile: build

# Start the full application stack via Aspire AppHost
run:
    dotnet run --project src/WithLove.AppHost

# Deploy the application to Azure using the shared production defaults.
# Requires .secrets.env in the repo root — copy .secrets.env.example and fill in your values.
#   just deploy-azure           # deploy to azureprod (uses cached state)
#   just deploy-azure staging   # deploy to a different environment
deploy environment="azureprod":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! -f .secrets.env ]]; then
        echo "Error: .secrets.env not found. Copy .secrets.env.example and fill in your values." >&2
        exit 1
    fi
    source .secrets.env
    aspire deploy --environment {{environment}}

# Like deploy-azure, but drops the cached deployment state first.
# Use this after changing Azure__Location, Azure__ResourceGroup, or similar infra-level settings.
#   just deploy-azure-clean           # deploy to azureprod with fresh state
#   just deploy-azure-clean staging   # deploy to staging with fresh state
deploy-clean environment="azureprod":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! -f .secrets.env ]]; then
        echo "Error: .secrets.env not found. Copy .secrets.env.example and fill in your values." >&2
        exit 1
    fi
    source .secrets.env
    aspire deploy --environment {{environment}} --clear-cache

# Destroy the Azure deployment and wait until the resource group is fully gone.
# aspire destroy returns as soon as ARM accepts the request; the actual teardown is
# async and can take 2-5 minutes. This recipe blocks until deletion is complete so
# it's safe to redeploy immediately after.
# Also attempts to terminate the database setup workflow in Temporal Cloud. This only
# affects a workflow that is currently RUNNING — if it has already completed, terminate
# is a no-op (and AllowDuplicate in the code handles the re-run on next deploy anyway).
# The value here is stopping a mid-run workflow from burning 90 min of retries against
# a SQL Server that no longer exists.
#   just destroy                    # destroy azureprod, wait up to 600s
#   just destroy staging            # destroy a named environment
#   just destroy azureprod 300      # custom timeout in seconds
destroy environment="azureprod" timeout="600":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! -f .secrets.env ]]; then
        echo "Error: .secrets.env not found. Copy .secrets.env.example and fill in your values." >&2
        exit 1
    fi
    source .secrets.env
    # Best-effort: stop a running db-setup workflow so it doesn't spin against a
    # deleted SQL Server. Silent no-op if the workflow is already completed or absent.
    temporal workflow terminate \
        --workflow-id withlove-db-setup \
        --namespace "${Parameters__temporal_namespace}" \
        --address "${Parameters__temporal_address}" \
        --api-key "${Parameters__temporal_api_key}" \
        --reason "Azure resources being destroyed" \
        2>/dev/null && echo "Terminated running db-setup workflow." || true
    aspire destroy --environment {{environment}}
    if az group show --name "${Azure__ResourceGroup}" &>/dev/null; then
        echo "Waiting for resource group '${Azure__ResourceGroup}' to finish deleting (timeout: {{timeout}}s)..."
        az group wait --name "${Azure__ResourceGroup}" --deleted --timeout {{timeout}}
        echo "Resource group fully deleted."
    else
        echo "Resource group '${Azure__ResourceGroup}' already gone."
    fi

# Run all tests
test:
    dotnet test tests/WithLove.ProductsAPI.Tests/WithLove.ProductsAPI.Tests.csproj --logger "console;verbosity=normal"

# Run unit tests only (fast, no Docker required)
test-unit:
    dotnet test tests/WithLove.ProductsAPI.Tests/WithLove.ProductsAPI.Tests.csproj \
        --filter "Category=Unit" \
        --logger "console;verbosity=normal"

# Run integration tests only (requires Docker for SQL Server + Redis)
test-integration:
    dotnet test tests/WithLove.ProductsAPI.Tests/WithLove.ProductsAPI.Tests.csproj \
        --filter "Category=Integration" \
        --logger "console;verbosity=normal"
