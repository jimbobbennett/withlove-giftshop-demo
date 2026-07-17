using Azure.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace WithLove.Data;

/// <summary>
/// Injects an Azure AD access token into SQL connections via AccessTokenCallback,
/// bypassing SqlAuthenticationProviderManager entirely.
/// Only activates when the connection string contains "Authentication=" (Azure SQL managed identity).
/// </summary>
public sealed class AzureSqlTokenInterceptor : DbConnectionInterceptor
{
    private static readonly string[] Scopes = ["https://database.windows.net/.default"];
    private readonly TokenCredential _credential;

    public AzureSqlTokenInterceptor(TokenCredential credential)
        => _credential = credential;

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        SetTokenCallback(connection);
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        SetTokenCallback(connection);
        return base.ConnectionOpening(connection, eventData, result);
    }

    private void SetTokenCallback(DbConnection connection)
    {
        if (connection is SqlConnection sql && sql.AccessTokenCallback is null)
        {
            sql.AccessTokenCallback = async (authParams, ct) =>
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext(Scopes), ct);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            };
        }
    }

    /// <summary>
    /// Strips the <c>Authentication=</c> keyword from a SQL Server connection string and
    /// signals whether managed-identity token auth should be used instead.
    /// <para>
    /// Aspire injects <c>Authentication=Active Directory Default</c> into connection strings
    /// for Azure SQL resources. The SQL Client's built-in authentication provider conflicts
    /// with <see cref="AzureSqlTokenInterceptor"/>, so the keyword must be removed before
    /// passing the string to EF Core and token injection wired up separately.
    /// </para>
    /// </summary>
    /// <param name="raw">The raw connection string from configuration.</param>
    /// <returns>
    /// The sanitised connection string and a flag indicating whether
    /// <see cref="AzureSqlTokenInterceptor"/> should be registered.
    /// </returns>
    public static (string ConnectionString, bool UseTokenAuth) StripAuthenticationKeyword(string raw)
    {
        const string keyword = "Authentication=";
        if (!raw.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return (raw, false);

        // Split on ';', drop the Authentication=… segment, and rejoin.
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => !p.TrimStart().StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                       .ToArray();
        return (string.Join(';', parts), true);
    }
}
