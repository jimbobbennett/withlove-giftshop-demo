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
}
