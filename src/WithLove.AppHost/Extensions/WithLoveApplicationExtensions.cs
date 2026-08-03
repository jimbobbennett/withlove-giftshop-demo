using Aspire.Hosting.Azure;
using Azure.Provisioning.KeyVault;
using Temporalio.Common;
using WithLove.AppHost.Resources;

namespace WithLove.AppHost.Extensions;

internal static partial class WithLoveApplicationExtensions
{
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
            ConfigureAzureDependencies(builder, application, parameters);
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
        if (isPublishMode)
        {
            productsDatabase = builder.AddAzureSqlServer("sqlServer").AddDatabase("productsDatabase");
        }
        else
        {
            var sqlServer = builder.AddSqlServer("sqlServer")
                .WithDockerfile("Resources/mssql-fts")
                .WithDbGate();

            if (!isTestMode)
                sqlServer.WithDataVolume("mssql-data");

            productsDatabase = sqlServer.AddDatabase("productsDatabase");
        }

        // Azure Managed Redis has no Balanced SKUs available in US regions on this subscription,
        // and Azure Cache for Redis is being retired. Use a password so all clients share one secret.
        var redis = builder.AddRedis("redisCache", password: parameters.RedisPassword);
        if (!isPublishMode)
            redis.WithRedisInsight();
        if (!isTestMode && !isPublishMode)
            redis.WithDataVolume("redis-data");

        IResourceBuilder<IResourceWithConnectionString> redisCache = redis;
        return new WithLoveInfrastructure(productsDatabase, redisCache);
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
            options.DbFilename = "/home/temporal/temporal.db";
        });

        // Docker copy-up preserves the temporal user's ownership of this volume.
        temporalServer.WithVolume("temporal-data", "/home/temporal");

        IResourceBuilder<Aspire.Hosting.IResourceWithServiceDiscovery> temporalService = temporalServer;
        application.WorkflowServer.WaitForAndReference(temporalService);
        application.ShopSite.WaitForAndReference(temporalService);

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
        WithLoveParameters parameters)
    {
        var keyVault = builder.AddAzureKeyVault("keyvault");
        var sharedIdentity = builder.AddAzureUserAssignedIdentity("withlove-identity");

        // Key Vault secret names must not collide with parameter resource names.
        keyVault.AddSecret("kv-openai-api-key", parameters.OpenAiKey);
        keyVault.AddSecret("kv-stripe-api-key", parameters.StripeApiKey);
        keyVault.AddSecret("kv-stripe-public-key", parameters.StripePublicKey);
        keyVault.AddSecret("kv-stripe-webhook-secret", parameters.StripeWebhookSecret);
        keyVault.AddSecret("kv-temporal-api-key", parameters.TemporalApiKey);

        var temporalCloud = TemporalCommunity.Aspire.Hosting.TemporalCloudResourceExtensions.AddTemporalCloud(
            builder,
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

        TemporalCommunity.Aspire.Hosting.TemporalCloudResourceExtensions.WithReference(
            workflowServer,
            temporalCloud);
        TemporalCommunity.Aspire.Hosting.TemporalCloudResourceExtensions.WithReference(
            shopSite,
            temporalCloud);
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
        IResourceBuilder<Aspire.Hosting.IResourceWithServiceDiscovery> dependency)
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
        IResourceBuilder<IResourceWithConnectionString> RedisCache);

    private sealed record WithLoveApplication(
        IResourceBuilder<ProjectResource> ProductsApi,
        IResourceBuilder<ProjectResource> WorkflowServer,
        IResourceBuilder<ProjectResource> ShopSite);
}
