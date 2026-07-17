using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.KeyVault;
using Temporalio.Common;
using WithLove.AppHost.Resources;

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions()
{
    Args = args,
    DashboardApplicationName = "WithLove Resource Dashboard",
});

// ACA environment — required for PublishAsAzureContainerApp in publish mode
builder.AddAzureContainerAppEnvironment("withlove-env");

var isTestMode = builder.Configuration["TESTING"] == "true";

// Secret Keys (always declared — needed in both dev and publish modes)
var openaiKey           = builder.AddParameter("openai-api-key",       secret: true);
var stripeApiKey        = builder.AddParameter("stripe-api-key",        secret: true);
var stripePublicKey     = builder.AddParameter("stripe-public-key",     secret: true);
var redisPassword       = builder.AddParameter("redis-password",         secret: true);

// Additional parameters for publish mode (Temporal Cloud + Stripe webhook)
var temporalAddress     = builder.AddParameter("temporal-address");
var temporalNamespace   = builder.AddParameter("temporal-namespace");
var temporalApiKey      = builder.AddParameter("temporal-api-key",      secret: true);
var stripeWebhookSecret = builder.AddParameter("stripe-webhook-secret", secret: true);

// Infrastructure — SQL Server
IResourceBuilder<IResourceWithConnectionString> productsDb;
if (builder.ExecutionContext.IsPublishMode)
{
    productsDb = builder.AddAzureSqlServer("sqlServer").AddDatabase("productsDatabase");
}
else
{
    var sql = builder.AddSqlServer("sqlServer")
                     .WithDockerfile("Resources/mssql-fts")
                     .WithDbGate();
    if (!isTestMode) sql.WithDataVolume("mssql-data");
    productsDb = sql.AddDatabase("productsDatabase");
}

// Infrastructure — Redis (use container in all modes; Azure Managed Redis has no Balanced SKUs
// available in US regions on this subscription, and Azure Cache for Redis is being retired).
// Pass the password explicitly so the server and all referenced clients receive one shared secret.
var redis = builder.AddRedis("redisCache", password: redisPassword);
if (!builder.ExecutionContext.IsPublishMode)
    redis.WithRedisInsight();
if (!isTestMode && !builder.ExecutionContext.IsPublishMode)
    redis.WithDataVolume("redis-data");
IResourceBuilder<IResourceWithConnectionString> redisCache = redis;

// Products API — needs OpenAI for embeddings
var productsApi = builder.AddProject<Projects.WithLove_ProductsAPI>("productsApi")
    .WithEnvironment("OPENAI_API_KEY", openaiKey)
    .WaitFor(redisCache)
    .WithReference(redisCache)
    .WaitFor(productsDb)
    .WithReference(productsDb);

// Scalar API docs UI — dev only; excluded from publish to avoid an HTTPS endpoint
// being generated for the container (ACA terminates TLS at the ingress, not Kestrel).
if (!builder.ExecutionContext.IsPublishMode)
{
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

if (!isTestMode)
{
    // Workflow Server — needs OpenAI for chat inference, Stripe for order processing
    var workflowServer = builder.AddProject<Projects.WithLove_WorkflowServer>("workflowServer")
        .WithEnvironment("OPENAI_API_KEY", openaiKey)
        .WaitFor(productsDb)
        .WithReference(productsDb)
        .WithReference(productsApi);

    // Web frontend — needs OpenAI for search, Stripe for checkout
    var shopSite = builder.AddProject<Projects.WithLove_Web>("shopSite")
        .WithEnvironment("OPENAI_API_KEY", openaiKey)
        .WaitFor(redisCache)
        .WithReference(redisCache)
        .WaitFor(productsDb)
        .WithReference(productsDb)
        .WaitFor(productsApi)
        .WithReference(productsApi);

    // Temporal: dev container vs Temporal Cloud
    if (!builder.ExecutionContext.IsPublishMode)
    {
        var temporalServer = builder.AddTemporalDevContainer("temporal-server", opts =>
        {
            opts.Namespace = "default";
            opts.SearchAttributes =
            [
                SearchAttributeKey.CreateKeyword("StripeSessionId"),
                SearchAttributeKey.CreateKeyword("CustomerId"),
            ];
            opts.DbFilename = "/home/temporal/temporal.db";
        });

        // Mount at /home/temporal — that directory is owned by the temporal user (uid=1000)
        // in the image. Docker's copy-up initializes a fresh named volume with the same
        // ownership, so the temporal process can write the SQLite file.
        temporalServer.WithVolume("temporal-data", "/home/temporal");

        workflowServer
            .WaitFor(temporalServer)
            .WithReference(temporalServer);

        shopSite
            .WaitFor(temporalServer)
            .WithReference(temporalServer);
    }
    else
    {
        // Temporal Cloud — inject address and namespace; API key comes from Key Vault below
        foreach (var svc in new[] { workflowServer, shopSite })
        {
            svc.WithEnvironment("TEMPORAL_ADDRESS",   temporalAddress)
               .WithEnvironment("TEMPORAL_NAMESPACE", temporalNamespace);
        }
    }

    // Stripe: CLI container (dev) vs direct Key Vault injection (publish)
    // AddStripe() reads Stripe:Default:* config section.
    // WithReference(stripe) in dev injects env vars that map to Stripe__Default__*.
    // In publish mode, Key Vault section injects Stripe__Default__* directly.
    if (!builder.ExecutionContext.IsPublishMode)
    {
        var stripe = builder.AddStripeCliContainer("stripe",
            apiKey: stripeApiKey, publishableKey: stripePublicKey);
        stripe.WithWebhookForwardTo(shopSite, "/stripe/webhook");
        workflowServer.WithReference(stripe);
        shopSite.WithReference(stripe);
    }
    // Publish mode: Stripe__Default__* env vars come from Key Vault below

    shopSite.WithExternalHttpEndpoints();

    // Key Vault — publish mode only; secrets injected into each service
    if (builder.ExecutionContext.IsPublishMode)
    {
        var keyVault = builder.AddAzureKeyVault("withlove-keyvault");
        var sharedIdentity = builder.AddAzureUserAssignedIdentity("withlove-identity");

        // KV secret names must not collide with parameter resource names (both live in the
        // Aspire resource collection). Use a "kv-" prefix to keep them distinct.
        keyVault.AddSecret("kv-openai-api-key",        openaiKey);
        keyVault.AddSecret("kv-stripe-api-key",        stripeApiKey);
        keyVault.AddSecret("kv-stripe-public-key",     stripePublicKey);
        keyVault.AddSecret("kv-stripe-webhook-secret", stripeWebhookSecret);
        keyVault.AddSecret("kv-temporal-api-key",      temporalApiKey);

        productsApi
            .WithAzureUserAssignedIdentity(sharedIdentity)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser)
            .WithReference(keyVault)
            .WithEnvironment("OPENAI_API_KEY", keyVault.GetSecret("kv-openai-api-key"));

        workflowServer
            .WithAzureUserAssignedIdentity(sharedIdentity)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser)
            .WithReference(keyVault)
            .WithEnvironment("OPENAI_API_KEY",          keyVault.GetSecret("kv-openai-api-key"))
            .WithEnvironment("Stripe__Default__ApiKey", keyVault.GetSecret("kv-stripe-api-key"))
            .WithEnvironment("TEMPORAL_API_KEY",        keyVault.GetSecret("kv-temporal-api-key"));

        shopSite
            .WithAzureUserAssignedIdentity(sharedIdentity)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser)
            .WithReference(keyVault)
            .WithEnvironment("OPENAI_API_KEY",                 keyVault.GetSecret("kv-openai-api-key"))
            .WithEnvironment("Stripe__Default__ApiKey",        keyVault.GetSecret("kv-stripe-api-key"))
            .WithEnvironment("Stripe__Default__PublicKey",     keyVault.GetSecret("kv-stripe-public-key"))
            .WithEnvironment("Stripe__Default__WebhookSecret", keyVault.GetSecret("kv-stripe-webhook-secret"))
            .WithEnvironment("TEMPORAL_API_KEY",               keyVault.GetSecret("kv-temporal-api-key"));
    }

    // ACA customization — KEDA scaling and health probes (publish mode only)
#pragma warning disable AZPROVISION001
    workflowServer.PublishAsAzureContainerApp((_, app) =>
    {
        // TCP-only probes — /health is dev-only in ServiceDefaults
        // (MapHealthCheckEndpoints is guarded by IsDevelopment())
        var container = app.Template.Containers[0].Value!;
        container.Probes =
        [
            new ContainerAppProbe
            {
                ProbeType           = ContainerAppProbeType.Liveness,
                TcpSocket           = new ContainerAppTcpSocketRequestInfo { Port = 8080 },
                InitialDelaySeconds = 5,
                PeriodSeconds       = 10,
                FailureThreshold    = 3,
            },
            new ContainerAppProbe
            {
                ProbeType        = ContainerAppProbeType.Readiness,
                TcpSocket        = new ContainerAppTcpSocketRequestInfo { Port = 8080 },
                PeriodSeconds    = 5,
                FailureThreshold = 3,
            },
        ];

        // CPU-based KEDA scaling — MinReplicas=1 (never scale to zero; worker must poll Temporal)
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 5;
        app.Template.Scale.Rules.Add(new ContainerAppScaleRule
        {
            Name   = "cpu-rule",
            Custom = new ContainerAppCustomScaleRule
            {
                CustomScaleRuleType = "cpu",
                Metadata =
                {
                    ["type"]  = "Utilization",
                    ["value"] = "70",
                },
            },
        });
    });

    shopSite.PublishAsAzureContainerApp((_, app) =>
    {
        // Sticky sessions for Blazor InteractiveServer / SignalR
        app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;

        // TCP readiness probe — MapHealthCheckEndpoints is gated by IsDevelopment() so
        // HTTP health endpoints don't exist in production; TCP is the correct fallback.
        var shopContainer = app.Template.Containers[0].Value!;
        shopContainer.Probes =
        [
            new ContainerAppProbe
            {
                ProbeType           = ContainerAppProbeType.Readiness,
                TcpSocket           = new ContainerAppTcpSocketRequestInfo { Port = 8080 },
                InitialDelaySeconds = 10,
                PeriodSeconds       = 5,
                FailureThreshold    = 3,
            },
        ];

        // HTTP-based KEDA scaling — MinReplicas=1, MaxReplicas=10, 100 concurrent requests per replica
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 10;
        app.Template.Scale.Rules.Add(new ContainerAppScaleRule
        {
            Name = "http-rule",
            Http = new ContainerAppHttpScaleRule
            {
                Metadata =
                {
                    ["concurrentRequests"] = "100",
                },
            },
        });
    });
#pragma warning restore AZPROVISION001
}

builder.Build().Run();
