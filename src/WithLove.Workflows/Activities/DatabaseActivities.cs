using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Stripe;
using Temporalio.Activities;
using WithLove.Data;

namespace WithLove.Workflows.Activities;

public class DatabaseActivities(
    ProductsDbContext dbContext,
    StripeClient stripeClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    [Activity]
    public async Task<MigrationResult> ApplyMigrationsAsync()
    {
        var logger = ActivityExecutionContext.Current.Logger;
        var created = await dbContext.Database.EnsureCreatedAsync();

        if (created)
        {
            logger.DatabaseSchemaCreated();
            return new MigrationResult(1, "Database schema created successfully");
        }

        logger.DatabaseAlreadyExists();
        return new MigrationResult(0, "Database already exists");
    }

    [Activity]
    public async Task ApplySchemaUpgradesAsync()
    {
        var logger = ActivityExecutionContext.Current.Logger;

        await dbContext.Database.ExecuteSqlRawAsync("""
            IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'WithLoveCatalog')
            BEGIN
                CREATE FULLTEXT CATALOG WithLoveCatalog AS DEFAULT;
            END
            """);

        await dbContext.Database.ExecuteSqlRawAsync("""
            IF NOT EXISTS (
                SELECT 1 FROM sys.fulltext_indexes
                WHERE object_id = OBJECT_ID('Products')
            )
            BEGIN
                CREATE FULLTEXT INDEX ON Products(Name, Description)
                    KEY INDEX PK_Products ON WithLoveCatalog
                    WITH CHANGE_TRACKING AUTO;
            END
            """);
        logger.EnsuredProductSearchIndexes();

        await dbContext.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE name = 'IX_AspNetUsers_StripeCustomerId'
                AND object_id = OBJECT_ID('AspNetUsers')
            )
            BEGIN
                CREATE INDEX IX_AspNetUsers_StripeCustomerId
                ON AspNetUsers (StripeCustomerId)
                WHERE StripeCustomerId IS NOT NULL
            END");

        logger.EnsuredStripeCustomerIndex();
    }

    [Activity]
    public async Task<SeedResult> SeedDatabaseAsync()
    {
        var categoriesSeeded = 0;
        var productsSeeded = 0;
        var logger = ActivityExecutionContext.Current.Logger;
        
        if (!await dbContext.Categories.AnyAsync())
        {
            var categories = SeedData.GetSeedCategories();
            dbContext.Categories.AddRange(categories);
            await dbContext.SaveChangesAsync();
            
            categoriesSeeded = categories.Count;
            logger.SeededCategories(categoriesSeeded);
        }
        else
        {
            logger.CategoriesAlreadySeeded();
        }

        // Load what's already in the DB (handles partial prior seeds)
        var existingByName = await dbContext.Products
            .ToDictionaryAsync(p => p.Name);

        // Category loading (unchanged)
        if (!dbContext.Categories.Local.Any())
            await dbContext.Categories.LoadAsync();
        var categoryByName = dbContext.Categories.Local.ToDictionary(c => c.Name);

        var seedProducts = SeedData.GetSeedProducts(categoryByName);

        var stripeProductService = stripeClient.V1.Products;
        var stripePriceService   = stripeClient.V1.Prices;

        foreach (var product in seedProducts)
        {
            // Already fully seeded on a prior run — skip
            if (existingByName.TryGetValue(product.Name, out var existing)
                && !string.IsNullOrEmpty(existing.StripePriceId))
                continue;

            // Deterministic idempotency keys — safe to repeat within a single 90-minute workflow run.
            // Cross-run safety is guaranteed only while keys remain valid (24h). A crash after a Stripe
            // call but before SaveChangesAsync, followed by a fresh workflow run >24h later, could
            // result in a duplicate Stripe product. Accepted limitation for this demo application.
            var productKey  = $"withlove-seed-product-{product.Name}";
            var priceKey    = $"withlove-seed-price-{product.Name}";
            var setPriceKey = $"withlove-seed-setprice-{product.Name}";

            var stripeProduct = await stripeProductService.CreateAsync(
                new ProductCreateOptions
                {
                    Name        = product.Name,
                    Description = product.Description,
                    Images      = product.ImageUrl is not null ? [product.ImageUrl] : null,
                },
                new RequestOptions { IdempotencyKey = productKey });

            var stripePrice = await stripePriceService.CreateAsync(
                new PriceCreateOptions
                {
                    Product    = stripeProduct.Id,
                    Currency   = "usd",
                    UnitAmount = (long)(product.Price * 100),
                },
                new RequestOptions { IdempotencyKey = priceKey });

            await stripeProductService.UpdateAsync(
                stripeProduct.Id,
                new ProductUpdateOptions { DefaultPrice = stripePrice.Id },
                new RequestOptions { IdempotencyKey = setPriceKey });

            // Persist per-product so a mid-loop crash doesn't lose completed work on retry.
            // Use the correct entity for each case to avoid EF Core identity conflicts.
            if (existingByName.TryGetValue(product.Name, out var existingRow))
            {
                // Partially-seeded row: update the tracked entity directly.
                // Do NOT call Update(product) — product.Id == 0 (fresh in-memory object) and would
                // be treated as a new insert rather than an update to the existing row.
                existingRow.StripePriceId = stripePrice.Id;
            }
            else
            {
                product.StripePriceId = stripePrice.Id;
                dbContext.Products.Add(product);
            }

            await dbContext.SaveChangesAsync();
            logger.CreatedStripeProduct(stripeProduct.Id, stripePrice.Id, product.Name);
            productsSeeded++;
        }

        return new SeedResult(categoriesSeeded, productsSeeded);
    }

    [Activity]
    public async Task<EmbeddingResult> GenerateEmbeddingsAsync()
    {
        var logger = ActivityExecutionContext.Current.Logger;

        var products = await dbContext.Products
            .Include(p => p.Category)
            .Where(p => p.IsEnabled && p.Embedding == null)
            .ToListAsync();

        if (products.Count == 0)
        {
            logger.ProductsAlreadyEmbedded();
            return new EmbeddingResult(0);
        }

        logger.GeneratingEmbeddings(products.Count);

        var texts = products.Select(GenerateEmbeddingText).ToList();
        var results = await embeddingGenerator.GenerateAsync(texts);

        for (int i = 0; i < products.Count; i++)
        {
            products[i].Embedding = new SqlVector<float>(results[i].Vector.ToArray());
        }

        await dbContext.SaveChangesAsync();
        logger.GeneratedEmbeddings(products.Count);

        return new EmbeddingResult(products.Count);
    }

    private static string GenerateEmbeddingText(WithLove.Data.Models.Product product)
    {
        var parts = new List<string> { product.Name };

        if (!string.IsNullOrWhiteSpace(product.Description))
            parts.Add(product.Description);

        if (product.Category?.Name is not null)
            parts.Add($"Category: {product.Category.Name}");

        if (!string.IsNullOrWhiteSpace(product.SubCategory))
            parts.Add($"Subcategory: {product.SubCategory}");

        if (product.Materials.Count > 0)
            parts.Add("Materials: " + string.Join(", ", product.Materials.Select(m => m.Name)));

        if (product.Features.Count > 0)
            parts.Add("Features: " + string.Join(", ", product.Features.Select(f => f.Title)));

        if (!string.IsNullOrWhiteSpace(product.StoryDescription))
            parts.Add(product.StoryDescription);

        return string.Join(". ", parts);
    }
}

public record MigrationResult(int AppliedCount, string Message);
public record SeedResult(int CategoriesSeeded, int ProductsSeeded);
public record EmbeddingResult(int ProductsEmbedded);
public record DatabaseSetupResult(MigrationResult Migration, SeedResult Seed, EmbeddingResult? Embedding);
