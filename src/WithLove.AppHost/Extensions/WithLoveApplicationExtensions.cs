using Aspire.Hosting.Azure;
using Azure.Provisioning.KeyVault;
using TemporalCommunity.Aspire.Hosting;
using Temporalio.Common;

namespace WithLove.AppHost.Extensions;

internal static partial class WithLoveApplicationExtensions
{
    private const string ProductsDatabaseResourceName = "productsDatabase";

    public static void AddWithLoveApplication(
        this IDistributedApplicationBuilder builder,
        bool isPublishMode,
        bool isTestMode)
    {
        var parameters = AddParameters(builder);
        var infrastructure = AddInfrastructure(builder, isPublishMode, isTestMode, parameters);
        var productsApi = AddProductsApi(builder, infrastructure, parameters);

        if (!isPublishMode)
            ConfigureScalarEndpoint(productsApi);

        // Integration tests exercise the Products API with disposable SQL Server and Redis containers.
        if (isTestMode)
            return;

        var application = AddFullApplication(builder, infrastructure, parameters, productsApi);

        if (isPublishMode)
            ConfigureAzureDependencies(builder, application, infrastructure, parameters);
        else
            ConfigureLocalDependencies(builder, application, parameters);

        ConfigureWorkflowServer(application.WorkflowServer);
        ConfigureShopSite(application.ShopSite);
    }

    private static WithLoveParameters AddParameters(IDistributedApplicationBuilder builder)
        => new(
            builder.AddParameter("openai-api-key", secret: true),
            builder.AddParameter("stripe-api-key", secret: true),
            builder.AddParameter("stripe-public-key", secret: true),
            builder.AddParameter("redis-password", secret: true),
            builder.AddParameter("temporal-address"),
            builder.AddParameter("temporal-namespace"),
            builder.AddParameter("temporal-api-key", secret: true),
            builder.AddParameter("stripe-webhook-secret", secret: true));

    private static WithLoveInfrastructure AddInfrastructure(
        IDistributedApplicationBuilder builder,
        bool isPublishMode,
        bool isTestMode,
        WithLoveParameters parameters)
    {
        IResourceBuilder<IResourceWithConnectionString> productsDatabase;
        IResourceBuilder<AzureSqlServerResource>? azureSqlServer = null;
        if (isPublishMode)
        {
            azureSqlServer = builder.AddAzureSqlServer("sqlServer")
                // Aspire 13.4.6 generates one incompatible SQL role script per consumer.
                // A single shared identity only needs one database principal, provisioned below.
                .ClearDefaultRoleAssignments();
            productsDatabase = azureSqlServer.AddDatabase(ProductsDatabaseResourceName);
        }
        else
        {
            var sqlServer = builder.AddSqlServer("sqlServer")
                .WithDockerfile("Resources/mssql-fts")
                .WithDbGate();

            if (!isTestMode)
                sqlServer.WithDataVolume("mssql-data");

            productsDatabase = sqlServer.AddDatabase(ProductsDatabaseResourceName);
        }

        // Azure Managed Redis has no Balanced SKUs available in US regions on this subscription,
        // and Azure Cache for Redis is being retired. Keep one stable password across deployments
        // so a reused Redis revision and newly deployed consumers cannot drift out of sync.
        // The disposable integration-test topology intentionally has no external parameters.
        var redis = isTestMode
            ? builder.AddRedis("redisCache")
            : builder.AddRedis("redisCache", password: parameters.RedisPassword);
        if (!isPublishMode)
            redis.WithRedisInsight();
        if (!isTestMode && !isPublishMode)
            redis.WithDataVolume("redis-data");

        IResourceBuilder<IResourceWithConnectionString> redisCache = redis;
        return new WithLoveInfrastructure(productsDatabase, redisCache, azureSqlServer);
    }

    private static IResourceBuilder<ProjectResource> AddProductsApi(
        IDistributedApplicationBuilder builder,
        WithLoveInfrastructure infrastructure,
        WithLoveParameters parameters)
    {
        var productsApi = builder.AddProject<Projects.WithLove_ProductsAPI>("productsApi")
            .WithEnvironment("OPENAI_API_KEY", parameters.OpenAiKey);

        productsApi.WaitForAndReference(infrastructure.RedisCache);
        productsApi.WaitForAndReference(infrastructure.ProductsDatabase);

        return productsApi;
    }

    private static void ConfigureScalarEndpoint(IResourceBuilder<ProjectResource> productsApi)
    {
        // Exclude Scalar from publish mode: ACA terminates TLS at ingress, not Kestrel.
        productsApi
            .WithEndpoint("scalar", callback: endpoint =>
            {
                endpoint.Port = 7001;
                endpoint.UriScheme = "https";
                endpoint.Transport = "http";
            })
            .WithUrlForEndpoint("scalar", url =>
            {
                url.DisplayText = "Scalar";
                url.Url = "/scalar";
            });
    }

    private static WithLoveApplication AddFullApplication(
        IDistributedApplicationBuilder builder,
        WithLoveInfrastructure infrastructure,
        WithLoveParameters parameters,
        IResourceBuilder<ProjectResource> productsApi)
    {
        var workflowServer = builder.AddProject<Projects.WithLove_WorkflowServer>("workflowServer")
            .WithEnvironment("OPENAI_API_KEY", parameters.OpenAiKey);

        workflowServer.WaitForAndReference(infrastructure.ProductsDatabase);
        workflowServer.WithReference(productsApi);

        var shopSite = builder.AddProject<Projects.WithLove_Web>("shopSite")
            .WithEnvironment("OPENAI_API_KEY", parameters.OpenAiKey);

        shopSite.WaitForAndReference(infrastructure.RedisCache);
        shopSite.WaitForAndReference(infrastructure.ProductsDatabase);
        shopSite.WaitForAndReference(productsApi);
        shopSite.WithExternalHttpEndpoints();

        return new WithLoveApplication(productsApi, workflowServer, shopSite);
    }

    private static void ConfigureLocalDependencies(
        IDistributedApplicationBuilder builder,
        WithLoveApplication application,
        WithLoveParameters parameters)
    {
        var temporalServer = builder.AddTemporalDevContainer("temporal-server", options =>
        {
            options.Namespace = "default";
            options.SearchAttributes =
            [
                SearchAttributeKey.CreateKeyword("StripeSessionId"),
                SearchAttributeKey.CreateKeyword("CustomerId"),
            ];
            options.DevServerOptions.DatabaseFilename = "/home/temporal/temporal.db";
        });

        // Docker copy-up preserves the temporal user's ownership of this volume.
        temporalServer.WithVolume("temporal-data", "/home/temporal");

        application.WorkflowServer.WaitForAndReference(temporalServer);
        application.ShopSite.WaitForAndReference(temporalServer);

        // Referencing the CLI container supplies the Stripe__Default__* configuration locally.
        var stripe = builder.AddStripeCliContainer(
            "stripe",
            apiKey: parameters.StripeApiKey,
            publishableKey: parameters.StripePublicKey);
        stripe.WithWebhookForwardTo(application.ShopSite, "/stripe/webhook");
        application.WorkflowServer.WithReference(stripe);
        application.ShopSite.WithReference(stripe);
    }

    private static void ConfigureAzureDependencies(
        IDistributedApplicationBuilder builder,
        WithLoveApplication application,
        WithLoveInfrastructure infrastructure,
        WithLoveParameters parameters)
    {
        var keyVault = builder.AddAzureKeyVault("keyvault");
        var sharedIdentity = builder.AddAzureUserAssignedIdentity("withlove-identity");
        var sqlIdentityAccess = AddAzureSqlIdentityAccess(builder, infrastructure, sharedIdentity);

        // Key Vault secret names must not collide with parameter resource names.
        keyVault.AddSecret("kv-openai-api-key", parameters.OpenAiKey);
        keyVault.AddSecret("kv-stripe-api-key", parameters.StripeApiKey);
        keyVault.AddSecret("kv-stripe-public-key", parameters.StripePublicKey);
        keyVault.AddSecret("kv-stripe-webhook-secret", parameters.StripeWebhookSecret);
        keyVault.AddSecret("kv-temporal-api-key", parameters.TemporalApiKey);

        var temporalCloud = builder.AddTemporalCloud(
            "temporal-cloud",
            parameters.TemporalAddress,
            parameters.TemporalNamespace,
            configure: options => options.ApiKey = keyVault.GetSecret("kv-temporal-api-key"));

        ConfigureKeyVaultAccess(application.ProductsApi, keyVault, sharedIdentity)
            .WithEnvironment("OPENAI_API_KEY", keyVault.GetSecret("kv-openai-api-key"));

        var workflowServer = ConfigureKeyVaultAccess(application.WorkflowServer, keyVault, sharedIdentity)
            .WithEnvironment("OPENAI_API_KEY", keyVault.GetSecret("kv-openai-api-key"))
            .WithEnvironment("Stripe__Default__ApiKey", keyVault.GetSecret("kv-stripe-api-key"));

        var shopSite = ConfigureKeyVaultAccess(application.ShopSite, keyVault, sharedIdentity)
            .WithEnvironment("OPENAI_API_KEY", keyVault.GetSecret("kv-openai-api-key"))
            .WithEnvironment("Stripe__Default__ApiKey", keyVault.GetSecret("kv-stripe-api-key"))
            .WithEnvironment("Stripe__Default__PublicKey", keyVault.GetSecret("kv-stripe-public-key"))
            .WithEnvironment("Stripe__Default__WebhookSecret", keyVault.GetSecret("kv-stripe-webhook-secret"));

        workflowServer.WithReference(temporalCloud);
        shopSite.WithReference(temporalCloud);

        // The output reference makes the Azure Container App modules depend on the completed
        // SQL access deployment. WaitFor alone only affects run-mode resource readiness.
        var sqlIdentityAccessMarker = sqlIdentityAccess.GetOutput("deploymentScriptName");
        application.ProductsApi
            .WithEnvironment("WITHLOVE_SQL_IDENTITY_ACCESS", sqlIdentityAccessMarker)
            .WaitFor(sqlIdentityAccess);
        application.WorkflowServer
            .WithEnvironment("WITHLOVE_SQL_IDENTITY_ACCESS", sqlIdentityAccessMarker)
            .WaitFor(sqlIdentityAccess);
        application.ShopSite
            .WithEnvironment("WITHLOVE_SQL_IDENTITY_ACCESS", sqlIdentityAccessMarker)
            .WaitFor(sqlIdentityAccess);
    }

    private static IResourceBuilder<AzureBicepResource> AddAzureSqlIdentityAccess(
        IDistributedApplicationBuilder builder,
        WithLoveInfrastructure infrastructure,
        IResourceBuilder<AzureUserAssignedIdentityResource> sharedIdentity)
    {
        var sqlServer = infrastructure.AzureSqlServer
            ?? throw new InvalidOperationException("Azure SQL identity access is only available in publish mode.");

        return builder.AddBicepTemplate(
                "sql-identity-access",
                "Resources/sql-identity-access.bicep")
            .WithParameter("sqlServerName", sqlServer.Resource.NameOutputReference)
            .WithParameter(
                "sqlServerAdminName",
                new BicepOutputReference("sqlServerAdminName", sqlServer.Resource))
            .WithParameter("databaseName", ProductsDatabaseResourceName)
            .WithParameter("principalName", sharedIdentity.Resource.NameOutputReference)
            .WithParameter("principalClientId", sharedIdentity.Resource.ClientId);
    }

    private static IResourceBuilder<ProjectResource> ConfigureKeyVaultAccess(
        IResourceBuilder<ProjectResource> project,
        IResourceBuilder<AzureKeyVaultResource> keyVault,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
        => project
            .WithAzureUserAssignedIdentity(identity)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser)
            .WithReference(keyVault);

    private static IResourceBuilder<ProjectResource> WaitForAndReference(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<IResourceWithConnectionString> dependency)
        => project.WaitFor(dependency).WithReference(dependency);

    private static IResourceBuilder<ProjectResource> WaitForAndReference(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<ProjectResource> dependency)
        => project.WaitFor(dependency).WithReference(dependency);

    private static IResourceBuilder<ProjectResource> WaitForAndReference(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<TemporalContainerResource> dependency)
        => project.WaitFor(dependency).WithReference(dependency);

    private sealed record WithLoveParameters(
        IResourceBuilder<ParameterResource> OpenAiKey,
        IResourceBuilder<ParameterResource> StripeApiKey,
        IResourceBuilder<ParameterResource> StripePublicKey,
        IResourceBuilder<ParameterResource> RedisPassword,
        IResourceBuilder<ParameterResource> TemporalAddress,
        IResourceBuilder<ParameterResource> TemporalNamespace,
        IResourceBuilder<ParameterResource> TemporalApiKey,
        IResourceBuilder<ParameterResource> StripeWebhookSecret);

    private sealed record WithLoveInfrastructure(
        IResourceBuilder<IResourceWithConnectionString> ProductsDatabase,
        IResourceBuilder<IResourceWithConnectionString> RedisCache,
        IResourceBuilder<AzureSqlServerResource>? AzureSqlServer);

    private sealed record WithLoveApplication(
        IResourceBuilder<ProjectResource> ProductsApi,
        IResourceBuilder<ProjectResource> WorkflowServer,
        IResourceBuilder<ProjectResource> ShopSite);
}
