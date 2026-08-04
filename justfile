set windows-shell := ["pwsh.exe", "-NoLogo", "-Command"]
set shell := ["bash", "-c"]

solution        := "WithLoveShop.slnx"
configuration   := "Debug"
apphost         := "src/WithLove.AppHost/WithLove.AppHost.csproj"

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

# Purge soft-deleted Key Vaults that were created by this AppHost environment.
# Key Vault names remain reserved after a normal delete, so this is required before
# recreating the same generated vault name in a disposable environment.
purge-deleted-keyvaults:
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! -f .secrets.env ]]; then
        echo "Error: .secrets.env not found. Copy .secrets.env.example and fill in your values." >&2
        exit 1
    fi
    source .secrets.env

    : "${Azure__SubscriptionId:?Azure__SubscriptionId is required}"
    : "${Azure__ResourceGroup:?Azure__ResourceGroup is required}"

    active_subscription="$(az account show --query id --output tsv)"
    if [[ "${active_subscription}" != "${Azure__SubscriptionId}" ]]; then
        echo "Azure CLI subscription '${active_subscription}' does not match configured subscription '${Azure__SubscriptionId}'." >&2
        exit 1
    fi

    vault_prefix="$(printf '%s' "/subscriptions/${Azure__SubscriptionId}/resourceGroups/${Azure__ResourceGroup}/providers/Microsoft.KeyVault/vaults/" | tr '[:upper:]' '[:lower:]')"
    deleted_vaults="$(az keyvault list-deleted \
        --subscription "${Azure__SubscriptionId}" \
        --resource-type vault \
        --query '[].name' \
        --output tsv)"

    if [[ -z "${deleted_vaults}" ]]; then
        echo "No soft-deleted Key Vaults found."
        exit 0
    fi

    purged_any=false
    while IFS= read -r vault_name; do
        [[ -z "${vault_name}" ]] && continue

        metadata="$(az keyvault show-deleted \
            --subscription "${Azure__SubscriptionId}" \
            --name "${vault_name}" \
            --output json 2>/dev/null || true)"
        [[ -z "${metadata}" ]] && continue

        vault_id="$(jq -r '.properties.vaultId // empty' <<< "${metadata}" | tr '[:upper:]' '[:lower:]')"
        deleted_location="$(jq -r '.properties.location // .location // empty' <<< "${metadata}")"
        purge_protection="$(jq -r '.properties.purgeProtectionEnabled // false' <<< "${metadata}")"
        [[ "${vault_id}" == "${vault_prefix}"* ]] || continue

        if [[ "${purge_protection}" == "true" ]]; then
            echo "Cannot purge ${vault_name}: purge protection is enabled." >&2
            exit 1
        fi

        echo "Purging soft-deleted AppHost Key Vault '${vault_name}'..."
        az keyvault purge \
            --subscription "${Azure__SubscriptionId}" \
            --name "${vault_name}" \
            --location "${deleted_location}" \
            --only-show-errors
        purged_any=true

        for purge_attempt in {1..12}; do
            if ! az keyvault show-deleted \
                --subscription "${Azure__SubscriptionId}" \
                --name "${vault_name}" \
                --output none 2>/dev/null; then
                break
            fi
            if (( purge_attempt == 12 )); then
                echo "Timed out waiting for Key Vault '${vault_name}' to leave the deleted state." >&2
                exit 1
            fi
            sleep 5
        done
    done <<< "${deleted_vaults}"

    if [[ "${purged_any}" == false ]]; then
        echo "No soft-deleted AppHost Key Vaults matched '${Azure__ResourceGroup}'."
    fi

# Deploy the application to Azure using the shared production defaults.
# Requires .secrets.env in the repo root — copy .secrets.env.example and fill in your values.
#   just deploy                      # deploy to azureprod (uses cached state)
#   just deploy staging              # deploy to a different environment
#   just deploy-clean                # deploy with fresh Aspire state
deploy environment="azureprod" reset_state="false":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! -f .secrets.env ]]; then
        echo "Error: .secrets.env not found. Copy .secrets.env.example and fill in your values." >&2
        exit 1
    fi
    source .secrets.env

    : "${Azure__SubscriptionId:?Azure__SubscriptionId is required}"
    : "${Azure__ResourceGroup:?Azure__ResourceGroup is required}"
    : "${Azure__Location:?Azure__Location is required}"

    active_account="$(az account show --query '{subscription:id,tenant:tenantId}' --output tsv)"
    IFS=$'\t' read -r active_subscription active_tenant <<< "${active_account}"
    if [[ "${active_subscription}" != "${Azure__SubscriptionId}" ]]; then
        echo "Azure CLI subscription '${active_subscription}' does not match configured subscription '${Azure__SubscriptionId}'." >&2
        exit 1
    fi
    if [[ -n "${Azure__TenantId:-}" && "${active_tenant}" != "${Azure__TenantId}" ]]; then
        echo "Azure CLI tenant '${active_tenant}' does not match configured tenant '${Azure__TenantId}'." >&2
        exit 1
    fi

    apphost_path="$(cd "$(dirname "{{apphost}}")" && pwd)/$(basename "{{apphost}}")"
    normalized_apphost_path="$(printf '%s' "${apphost_path}" | tr '[:upper:]' '[:lower:]')"
    if command -v shasum >/dev/null 2>&1; then
        apphost_sha="$(printf '%s' "${normalized_apphost_path}" | shasum -a 256 | awk '{print toupper($1)}')"
    elif command -v sha256sum >/dev/null 2>&1; then
        apphost_sha="$(printf '%s' "${normalized_apphost_path}" | sha256sum | awk '{print toupper($1)}')"
    else
        apphost_sha="$(printf '%s' "${normalized_apphost_path}" | openssl dgst -sha256 | awk '{print toupper($NF)}')"
    fi
    environment_name="$(printf '%s' "{{environment}}" | tr '[:upper:]' '[:lower:]')"
    deployment_state_file="${HOME}/.aspire/deployments/${apphost_sha}/${environment_name}.json"

    if [[ -f "${deployment_state_file}" ]]; then
        cached_subscription="$(jq -r '.["Azure:SubscriptionId"] // empty' "${deployment_state_file}")"
        cached_resource_group="$(jq -r '.["Azure:ResourceGroup"] // empty' "${deployment_state_file}")"
        cached_location="$(jq -r '.["Azure:Location"] // empty' "${deployment_state_file}")"

        if [[ "{{reset_state}}" == "true" ||
              "${cached_subscription}" != "${Azure__SubscriptionId}" ||
              "${cached_resource_group}" != "${Azure__ResourceGroup}" ||
              "${cached_location}" != "${Azure__Location}" ]]; then
            echo "Removing stale Aspire state for '{{environment}}' at ${deployment_state_file}." >&2
            rm -f -- "${deployment_state_file}"
        fi
    elif [[ "{{reset_state}}" == "true" ]]; then
        echo "No cached Aspire state exists for '{{environment}}'."
    fi

    wait_for_resource_group_ready() {
        local wait_attempt resource_group_state
        for ((wait_attempt = 1; wait_attempt <= 40; wait_attempt++)); do
            resource_group_state="$(az group show \
                --subscription "${Azure__SubscriptionId}" \
                --name "${Azure__ResourceGroup}" \
                --query properties.provisioningState \
                --output tsv 2>/dev/null || true)"

            if [[ -z "${resource_group_state}" || "${resource_group_state}" == "Succeeded" ]]; then
                return 0
            fi

            case "${resource_group_state}" in
                Deleting|Creating|Updating)
                    echo "Resource group '${Azure__ResourceGroup}' is ${resource_group_state}; waiting 15s..." >&2
                    sleep 15
                    ;;
                *)
                    echo "Resource group '${Azure__ResourceGroup}' is in unexpected state '${resource_group_state}'." >&2
                    return 1
                    ;;
            esac
        done

        echo "Timed out waiting for resource group '${Azure__ResourceGroup}' to become ready." >&2
        return 1
    }

    wait_for_resource_group_ready
    just purge-deleted-keyvaults

    deploy_args=(
        deploy
        --apphost "${apphost_path}"
        --environment "{{environment}}"
        --non-interactive
    )

    max_attempts=3
    retry_delay_seconds=60
    for ((attempt = 1; attempt <= max_attempts; attempt++)); do
        wait_for_resource_group_ready
        echo "Aspire deploy attempt ${attempt}/${max_attempts}..."
        set +e
        aspire "${deploy_args[@]}"
        deploy_status=$?
        set -e

        if (( deploy_status == 0 )); then
            redis_revision="$(az containerapp show \
                --subscription "${Azure__SubscriptionId}" \
                --resource-group "${Azure__ResourceGroup}" \
                --name rediscache \
                --query properties.latestRevisionName \
                --output tsv 2>/dev/null || true)"
            if [[ -n "${redis_revision}" ]]; then
                echo "Restarting Redis revision '${redis_revision}' to apply the configured password."
                az containerapp revision restart \
                    --subscription "${Azure__SubscriptionId}" \
                    --resource-group "${Azure__ResourceGroup}" \
                    --name rediscache \
                    --revision "${redis_revision}" \
                    --output none

                for redis_wait_attempt in {1..24}; do
                    redis_state="$(az containerapp revision show \
                        --subscription "${Azure__SubscriptionId}" \
                        --resource-group "${Azure__ResourceGroup}" \
                        --name rediscache \
                        --revision "${redis_revision}" \
                        --query '{health:properties.healthState,running:properties.runningState}' \
                        --output tsv 2>/dev/null || true)"
                    if [[ "${redis_state}" == $'Healthy\tRunning' ]]; then
                        echo "Redis revision '${redis_revision}' is healthy."
                        break
                    fi
                    if (( redis_wait_attempt == 24 )); then
                        echo "Timed out waiting for Redis revision '${redis_revision}' to become healthy." >&2
                        exit 1
                    fi
                    sleep 5
                done
            fi
            exit 0
        fi

        latest_aspire_log="$(ls -t "${HOME}/.aspire/logs"/cli_*.log 2>/dev/null | head -n 1 || true)"
        if [[ -z "${latest_aspire_log}" ]] || ! rg -qi 'pending delete operation|ResourceGroupBeingDeleted|resource group.*deprovisioning' "${latest_aspire_log}"; then
            exit "${deploy_status}"
        fi

        if (( attempt == max_attempts )); then
            echo "Aspire deploy still hit an Azure pending-delete conflict after ${max_attempts} attempts." >&2
            exit "${deploy_status}"
        fi

        echo "Azure is still releasing a generated deployment-script resource; retrying in ${retry_delay_seconds}s..." >&2
        sleep "${retry_delay_seconds}"
        retry_delay_seconds=$((retry_delay_seconds * 2))
    done

# Like deploy, but drops the cached deployment state first.
# Use this after changing Azure__Location, Azure__ResourceGroup, or similar infra-level settings.
#   just deploy-clean           # deploy to azureprod with fresh state
#   just deploy-clean staging   # deploy to staging with fresh state
deploy-clean environment="azureprod":
    just deploy "{{environment}}" true

# Destroy the Azure deployment and wait until the resource group is fully gone.
# aspire destroy returns as soon as ARM accepts the request; the actual teardown is
# async and the Container Apps environment can take more than 10 minutes to release.
# This recipe blocks until a direct resource-group existence check returns false so
# it's safe to redeploy immediately after.
# Also attempts to terminate the database setup workflow in Temporal Cloud. This only
# affects a workflow that is currently RUNNING — if it has already completed, terminate
# is a no-op (and AllowDuplicate in the code handles the re-run on next deploy anyway).
# The value here is stopping a mid-run workflow from burning 90 min of retries against
# a SQL Server that no longer exists.
#   just destroy                    # destroy azureprod, wait up to 1200s
#   just destroy staging            # destroy a named environment
#   just destroy azureprod 300      # custom timeout in seconds
destroy environment="azureprod" timeout="1200":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! -f .secrets.env ]]; then
        echo "Error: .secrets.env not found. Copy .secrets.env.example and fill in your values." >&2
        exit 1
    fi
    source .secrets.env

    : "${Azure__SubscriptionId:?Azure__SubscriptionId is required}"
    : "${Azure__ResourceGroup:?Azure__ResourceGroup is required}"

    active_account="$(az account show --query '{subscription:id,tenant:tenantId}' --output tsv)"
    IFS=$'\t' read -r active_subscription active_tenant <<< "${active_account}"
    if [[ "${active_subscription}" != "${Azure__SubscriptionId}" ]]; then
        echo "Azure CLI subscription '${active_subscription}' does not match configured subscription '${Azure__SubscriptionId}'." >&2
        exit 1
    fi
    if [[ -n "${Azure__TenantId:-}" && "${active_tenant}" != "${Azure__TenantId}" ]]; then
        echo "Azure CLI tenant '${active_tenant}' does not match configured tenant '${Azure__TenantId}'." >&2
        exit 1
    fi

    apphost_path="$(cd "$(dirname "{{apphost}}")" && pwd)/$(basename "{{apphost}}")"
    normalized_apphost_path="$(printf '%s' "${apphost_path}" | tr '[:upper:]' '[:lower:]')"
    if command -v shasum >/dev/null 2>&1; then
        apphost_sha="$(printf '%s' "${normalized_apphost_path}" | shasum -a 256 | awk '{print toupper($1)}')"
    elif command -v sha256sum >/dev/null 2>&1; then
        apphost_sha="$(printf '%s' "${normalized_apphost_path}" | sha256sum | awk '{print toupper($1)}')"
    else
        apphost_sha="$(printf '%s' "${normalized_apphost_path}" | openssl dgst -sha256 | awk '{print toupper($NF)}')"
    fi
    environment_name="$(printf '%s' "{{environment}}" | tr '[:upper:]' '[:lower:]')"
    deployment_state_file="${HOME}/.aspire/deployments/${apphost_sha}/${environment_name}.json"

    use_aspire_destroy=false
    if [[ -f "${deployment_state_file}" ]]; then
        cached_subscription="$(jq -r '.["Azure:SubscriptionId"] // empty' "${deployment_state_file}")"
        cached_resource_group="$(jq -r '.["Azure:ResourceGroup"] // empty' "${deployment_state_file}")"
        if [[ "${cached_subscription}" == "${Azure__SubscriptionId}" &&
              "${cached_resource_group}" == "${Azure__ResourceGroup}" ]]; then
            use_aspire_destroy=true
        else
            echo "Cached Aspire state targets '${cached_resource_group}' in '${cached_subscription}', not the configured resource group; using exact resource-group cleanup." >&2
        fi
    else
        echo "No Aspire state exists for '{{environment}}'; using exact resource-group cleanup if needed." >&2
    fi

    # Best-effort: stop a running db-setup workflow so it doesn't spin against a
    # deleted SQL Server. Silent no-op if the workflow is already completed or absent.
    temporal workflow terminate \
        --workflow-id withlove-db-setup \
        --namespace "${Parameters__temporal_namespace}" \
        --address "${Parameters__temporal_address}" \
        --api-key "${Parameters__temporal_api_key}" \
        --reason "Azure resources being destroyed" \
        2>/dev/null && echo "Terminated running db-setup workflow." || true
    if [[ "${use_aspire_destroy}" == "true" ]]; then
        set +e
        aspire destroy \
            --apphost "${apphost_path}" \
            --environment "{{environment}}" \
            --non-interactive \
            --yes
        destroy_status=$?
        set -e
        if (( destroy_status != 0 )); then
            echo "Aspire destroy failed with exit code ${destroy_status}; falling back to exact resource-group deletion." >&2
            use_aspire_destroy=false
        fi
    fi

    if [[ "${use_aspire_destroy}" != "true" ]]; then
        existing_resource_group_state="$(az group show \
            --subscription "${Azure__SubscriptionId}" \
            --name "${Azure__ResourceGroup}" \
            --query properties.provisioningState \
            --output tsv 2>/dev/null || true)"
        if [[ -n "${existing_resource_group_state}" && "${existing_resource_group_state}" != "Deleting" ]]; then
            az group delete \
                --subscription "${Azure__SubscriptionId}" \
                --name "${Azure__ResourceGroup}" \
                --yes \
                --no-wait
        elif [[ "${existing_resource_group_state}" == "Deleting" ]]; then
            echo "Resource group '${Azure__ResourceGroup}' is already deleting; continuing to wait."
        fi
    fi

    resource_group_exists="$(az group exists \
        --subscription "${Azure__SubscriptionId}" \
        --name "${Azure__ResourceGroup}" \
        --output tsv)"
    if [[ "${resource_group_exists}" == "true" ]]; then
        echo "Waiting for resource group '${Azure__ResourceGroup}' to finish deleting (timeout: {{timeout}}s)..."
        destroy_deadline=$((SECONDS + {{timeout}}))
        while [[ "${resource_group_exists}" == "true" ]]; do
            if (( SECONDS >= destroy_deadline )); then
                resource_group_state="$(az group show \
                    --subscription "${Azure__SubscriptionId}" \
                    --name "${Azure__ResourceGroup}" \
                    --query properties.provisioningState \
                    --output tsv 2>/dev/null || true)"
                echo "Timed out waiting for resource group '${Azure__ResourceGroup}' to disappear; current state is '${resource_group_state:-unknown}'." >&2
                exit 1
            fi
            sleep 15
            resource_group_exists="$(az group exists \
                --subscription "${Azure__SubscriptionId}" \
                --name "${Azure__ResourceGroup}" \
                --output tsv)"
        done
        echo "Resource group fully deleted."
    else
        echo "Resource group '${Azure__ResourceGroup}' already gone."
    fi
    just purge-deleted-keyvaults
    rm -f -- "${deployment_state_file}"

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
