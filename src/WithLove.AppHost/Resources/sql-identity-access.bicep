@description('The Azure region used for the deployment script.')
param location string = resourceGroup().location

@description('The generated Azure SQL logical server name.')
param sqlServerName string

@description('The generated user-assigned identity configured as the SQL administrator.')
param sqlServerAdminName string

@description('The Azure SQL database that the application identity can access.')
param databaseName string

@description('The shared application user-assigned identity name.')
param principalName string

@description('The shared application identity client ID used as the Azure SQL external-user SID.')
param principalClientId string

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: sqlServerName
}

resource sqlServerAdmin 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sqlServerAdminName
}

resource sqlIdentityAccess 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: take('script-${uniqueString('sql-identity-access', principalName, databaseName, resourceGroup().id)}', 24)
  location: location
  kind: 'AzurePowerShell'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sqlServerAdmin.id}': {}
    }
  }
  properties: {
    azPowerShellVersion: '14.0'
    cleanupPreference: 'Always'
    retentionInterval: 'PT1H'
    timeout: 'PT30M'
    environmentVariables: [
      {
        name: 'DBNAME'
        value: databaseName
      }
      {
        name: 'DBSERVER'
        value: sqlServer.properties.fullyQualifiedDomainName
      }
      {
        name: 'PRINCIPALNAME'
        value: principalName
      }
      {
        name: 'ID'
        value: principalClientId
      }
      {
        name: 'SQLDNSSUFFIX'
        value: environment().suffixes.sqlServerHostname
      }
    ]
    scriptContent: '''
$sqlServerFqdn = "$env:DBSERVER"
$sqlDatabaseName = "$env:DBNAME"
$principalName = "$env:PRINCIPALNAME"
$id = "$env:ID"

# Principal names can contain apostrophes, so escape them before interpolation into T-SQL.
$escapedPrincipalName = $principalName.Replace("'", "''")

$sqlCmd = @"
DECLARE @name SYSNAME = '$escapedPrincipalName';
DECLARE @id UNIQUEIDENTIFIER = '$id';

-- The SID of an Entra principal is the raw bytes of its application/client ID.
DECLARE @sid VARBINARY(16) = CONVERT(VARBINARY(16), @id);
DECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), @sid, 1);

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Only reconcile external users created by this script.
DECLARE @existingSid VARBINARY(85) = (
    SELECT sid
    FROM sys.database_principals
    WHERE name = @name AND type = 'E'
);

-- A recreated managed identity keeps its name but receives a new client ID/SID.
IF @existingSid IS NOT NULL AND @existingSid <> @sid
BEGIN
    DECLARE @dropCmd NVARCHAR(MAX) = N'DROP USER ' + QUOTENAME(@name);
    EXEC (@dropCmd);
    SET @existingSid = NULL;
END

IF @existingSid IS NULL
BEGIN
    DECLARE @createCmd NVARCHAR(MAX) = N'CREATE USER ' + QUOTENAME(@name) + N' WITH SID = ' + @castId + N', TYPE = E;';
    EXEC (@createCmd);
END

DECLARE @roleCmd NVARCHAR(MAX) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@name);
EXEC (@roleCmd);

COMMIT TRANSACTION;
"@

Write-Host $sqlCmd

# Avoid Invoke-Sqlcmd. In the az14.0 deployment image its SqlServer module can bind to an
# incompatible Microsoft.Extensions.Caching.Memory assembly imported by the Az modules.
$sqlDnsSuffix = (Get-AzContext).Environment.SqlDatabaseDnsSuffix
if ([string]::IsNullOrWhiteSpace($sqlDnsSuffix)) {
    $sqlDnsSuffix = "$env:SQLDNSSUFFIX"
}
if ([string]::IsNullOrWhiteSpace($sqlDnsSuffix)) {
    throw "Azure SQL DNS suffix is unavailable in the deployment environment."
}
$sqlAudience = "https://" + $sqlDnsSuffix.TrimStart('.') + "/"
$connectionString = "Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"

$maxRetries = 5
$retryDelay = 60
$attempt = 0
$success = $false

while (-not $success -and $attempt -lt $maxRetries) {
    $attempt++
    Write-Host "Attempt $attempt of $maxRetries..."
    $connection = $null
    try {
        $tokenResponse = Get-AzAccessToken -ResourceUrl $sqlAudience
        $accessToken = if ($tokenResponse.Token -is [System.Security.SecureString]) {
            [System.Net.NetworkCredential]::new("", $tokenResponse.Token).Password
        } else {
            $tokenResponse.Token
        }

        $connection = New-Object System.Data.SqlClient.SqlConnection
        $connection.ConnectionString = $connectionString
        $connection.AccessToken = $accessToken
        $connection.Open()

        $command = $connection.CreateCommand()
        $command.CommandText = $sqlCmd
        [void]$command.ExecuteNonQuery()

        $success = $true
        Write-Host "SQL command succeeded on attempt $attempt."
    } catch {
        Write-Host "Attempt $attempt failed: $_"
        if ($attempt -lt $maxRetries) {
            Write-Host "Retrying in $retryDelay seconds..."
            Start-Sleep -Seconds $retryDelay
        } else {
            throw
        }
    } finally {
        if ($null -ne $connection) {
            $connection.Dispose()
        }
    }
}
'''
  }
}

output deploymentScriptName string = sqlIdentityAccess.name
