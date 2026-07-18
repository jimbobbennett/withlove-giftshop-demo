using WithLove.AppHost.Extensions;

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DashboardApplicationName = "WithLove Resource Dashboard",
});

// Azure publish annotations are inert during local runs, but require a shared ACA environment.
builder.AddAzureContainerAppEnvironment("withlove-env");

var isTestMode = builder.Configuration["TESTING"] == "true";
var isPublishMode = builder.ExecutionContext.IsPublishMode;

builder.AddWithLoveApplication(isPublishMode, isTestMode);

builder.Build().Run();
