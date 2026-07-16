using Temporalio.Workflows;
using WithLove.Workflows.Activities;

namespace WithLove.Workflows.Workflows;

[Workflow]
public class DatabaseSetupWorkflow
{
    [WorkflowRun]
    public async Task<DatabaseSetupResult> RunAsync()
    { 
        Workflow.Logger.StartingDatabaseSetup();

        var migrationResult = await Workflow.ExecuteActivityAsync(
            (DatabaseActivities act) => act.ApplyMigrationsAsync(),
            new ActivityOptions
            {
                ScheduleToCloseTimeout = TimeSpan.FromMinutes(20),
                StartToCloseTimeout    = TimeSpan.FromMinutes(5),
                RetryPolicy = new()
                {
                    InitialInterval    = TimeSpan.FromSeconds(2),
                    BackoffCoefficient = 2.0f,
                    MaximumInterval    = TimeSpan.FromMinutes(1),
                    MaximumAttempts    = 5,
                }
            });

        Workflow.Logger.MigrationComplete(migrationResult.AppliedCount, migrationResult.Message);

        await Workflow.ExecuteActivityAsync(
            (DatabaseActivities act) => act.ApplySchemaUpgradesAsync(),
            new ActivityOptions
            {
                ScheduleToCloseTimeout = TimeSpan.FromMinutes(20),
                StartToCloseTimeout    = TimeSpan.FromMinutes(5),
                RetryPolicy = new()
                {
                    InitialInterval    = TimeSpan.FromSeconds(2),
                    BackoffCoefficient = 2.0f,
                    MaximumInterval    = TimeSpan.FromMinutes(1),
                    MaximumAttempts    = 5,
                }
            });

        Workflow.Logger.SchemaUpgradesApplied();

        var seedResult = await Workflow.ExecuteActivityAsync(
            (DatabaseActivities act) => act.SeedDatabaseAsync(),
            new ActivityOptions
            {
                ScheduleToCloseTimeout = TimeSpan.FromMinutes(15),
                StartToCloseTimeout    = TimeSpan.FromMinutes(5),
                RetryPolicy = new()
                {
                    InitialInterval    = TimeSpan.FromSeconds(2),
                    BackoffCoefficient = 2.0f,
                    MaximumInterval    = TimeSpan.FromMinutes(1),
                    MaximumAttempts    = 3,
                }
            });

        Workflow.Logger.SeedingComplete(seedResult.CategoriesSeeded, seedResult.ProductsSeeded);

        var embeddingResult = await Workflow.ExecuteActivityAsync(
            (DatabaseActivities act) => act.GenerateEmbeddingsAsync(),
            new ActivityOptions
            {
                ScheduleToCloseTimeout = TimeSpan.FromMinutes(15),
                StartToCloseTimeout    = TimeSpan.FromMinutes(5),
                RetryPolicy = new()
                {
                    InitialInterval    = TimeSpan.FromSeconds(2),
                    BackoffCoefficient = 2.0f,
                    MaximumInterval    = TimeSpan.FromMinutes(1),
                    MaximumAttempts    = 3,
                }
            });

        Workflow.Logger.EmbeddingGenerationComplete(embeddingResult.ProductsEmbedded);

        return new DatabaseSetupResult(migrationResult, seedResult, embeddingResult);
    }
}
