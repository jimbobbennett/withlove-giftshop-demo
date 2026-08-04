using Azure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Temporalio.Common.EnvConfig;
using Temporalio.Extensions.OpenTelemetry;
using WithLove.Data;
using WithLove.Data.Models;
using WithLove.Web.Services;
using WithLove.Web.Middleware;
using Microsoft.AspNetCore.Components.Web;
using Stripe.Extensions.AspNetCore;
using WithLove.Web;
using WithLove.Web.Components;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.ConfigureOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource(Instrumentation.ActivitySourceName);
        tracing.AddSource(TracingInterceptor.ClientSource.Name);
        tracing.AddFusionCacheInstrumentation(opts =>
        {
            opts.IncludeMemoryLevel = true;
            opts.IncludeDistributedLevel = true;
            opts.IncludeBackplane = true;
        });
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(Instrumentation.ActivitySourceName);
        metrics.AddFusionCacheInstrumentation(opts =>
        {
            opts.IncludeMemoryLevel = true;
            opts.IncludeDistributedLevel = true;
            opts.IncludeBackplane = true;
        });
    });

builder.AddDefaultHealthChecks();

builder.Services.AddValidation();

builder.Services.AddSingleton<AzureSqlTokenInterceptor>(
    _ => new AzureSqlTokenInterceptor(new DefaultAzureCredential()));

builder.Services.AddDbContext<ProductsDbContext>((sp, options) =>
{
    var raw = builder.Configuration.GetConnectionString("productsDatabase") ?? string.Empty;
    var (connStr, useTokenAuth) = AzureSqlTokenInterceptor.StripAuthenticationKeyword(raw);

    var sqlOptions = new Action<Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder>(
        o => o.EnableRetryOnFailure());

    if (useTokenAuth)
    {
        var interceptor = sp.GetRequiredService<AzureSqlTokenInterceptor>();
        options.UseSqlServer(connStr, sqlOptions).AddInterceptors(interceptor);
    }
    else
    {
        options.UseSqlServer(connStr, sqlOptions);
    }
});

builder.EnrichSqlServerDbContext<ProductsDbContext>(
    configureSettings: settings =>
    {
        settings.DisableHealthChecks = false;
        settings.DisableTracing = false;
        settings.DisableRetry = false;
        settings.CommandTimeout = 20;
    });


builder.Services.AddIdentityCore<ShopUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;

        options.User.RequireUniqueEmail = true;
        
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequiredLength = 4;
        options.Password.RequiredUniqueChars = 0;
    })
    .AddEntityFrameworkStores<ProductsDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<ShopUserClaimsPrincipalFactory>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// Must run after AddIdentityCookies so it updates the application cookie scheme.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";

    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(2);
    options.SlidingExpiration = true;
});

builder.Services.AddAuthorizationBuilder();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddSingleton<Instrumentation>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .RegisterPersistentService<AnonymousCartSession>(RenderMode.InteractiveServer);

builder.Services.AddMemoryCache();
var redisConnectionString = builder.Configuration.GetConnectionString("redisCache")
    ?? throw new InvalidOperationException("The redisCache connection string is required.");
var redisOptions = ConfigurationOptions.Parse(redisConnectionString);

builder.Services.AddStackExchangeRedisCache(options =>
    options.ConfigurationOptions = redisOptions);

builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(new FusionCacheEntryOptions
    {
        Duration = TimeSpan.FromDays(30),
        IsFailSafeEnabled = false,
        AllowBackgroundDistributedCacheOperations = true,
    })
    .WithSystemTextJsonSerializer()
    .WithRegisteredDistributedCache()
    .WithBackplane(new RedisBackplane(new RedisBackplaneOptions
    {
        ConfigurationOptions = redisOptions,
    }));

builder.Services.AddHttpClient<IProductService, ProductApiService>(client =>
{
    client.BaseAddress = new Uri("https+http://productsApi");

    client.DefaultRequestHeaders.Add("X-WITHLOVE-API-VERSION", DateTime.Today.ToString("yyyy-MM-dd"));
});

builder.Services.AddStripe();

builder.Services.AddScoped<AnonymousCartSession>();

builder.Services.AddScoped<ICartService, FusionCacheCartService>();

// Fully qualified to avoid adding Stripe using directives to Program.cs.
// SessionService is scoped so it takes a fresh StripeClient per request scope.
builder.Services.AddScoped(sp =>
    new Stripe.Checkout.SessionService(sp.GetRequiredService<Stripe.StripeClient>()));
builder.Services.AddScoped<IOrderService, StripeOrderService>();

builder.Services.AddScoped<ChatService>();

builder.Services.AddScoped<ILoyaltyService, TemporalLoyaltyService>();

var connectOptions = ClientEnvConfig.LoadClientConnectOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = connectOptions.TargetHost;
    opts.Namespace = connectOptions.Namespace;
    opts.Interceptors = [new TracingInterceptor()];
    if (connectOptions.ApiKey is not null)
    {
        opts.ApiKey = connectOptions.ApiKey;
        opts.Tls = connectOptions.Tls; // TlsOptions; null is fine — SDK auto-enables TLS when ApiKey is set
    }
});

var app = builder.Build();

// Must run first so all subsequent middleware (including UseHsts and UseHttpsRedirection)
// sees the correct scheme from ACA's X-Forwarded-Proto header.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto
};
// Clear IP allowlists so the ACA ingress proxy is trusted regardless of its internal IP.
// This is safe because the container is not internet-reachable: ACA's managed ingress is the
// only entry point, and it strips/rewrites forwarded headers before they reach Kestrel.
// In a non-ACA environment (direct internet exposure), restore KnownIPNetworks with the
// specific proxy CIDR range instead of clearing it.
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();

app.UseMiddleware<AnonymousCartMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();
app.MapStaticAssets();

app.MapStripeWebhookHandler<StripeEventHandler>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost("/logout", async (SignInManager<ShopUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
});

app.MapHealthCheckEndpoints();

app.Run();
