using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

/*
  Creates a contained database user for each service's managed identity and grants it read/write.

  This is a deployment step and not Bicep, because it cannot be. Azure RBAC governs the *control*
  plane of a SQL server - who may change it - and has nothing to say about who may SELECT from a
  table. Data-plane access is granted inside the database with CREATE USER ... FROM EXTERNAL
  PROVIDER, which is T-SQL, which ARM cannot execute.

  It was found the way these things usually are: every service deployed, every identity had its
  Storage and Service Bus roles, and every one of them failed to reach the database.

  Written as a tool in the repo rather than a shell script because it needs an Entra access token
  for the SQL resource, and sqlcmd cannot take one on the command line. The one dependency this has
  - DefaultAzureCredential - is the same one the services use.
*/

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: grant-sql-access <sql-server-fqdn> <database> [identity-name ...]");
    return 64;
}

var server = args[0];
var database = args[1];
var identities = args.Skip(2).ToArray();

if (identities.Length == 0)
{
    Console.Error.WriteLine("No identities given; nothing to grant.");
    return 64;
}

var credential = new DefaultAzureCredential();
var token = await credential.GetTokenAsync(
    new TokenRequestContext(["https://database.windows.net/.default"]), CancellationToken.None);

await using var connection = new SqlConnection($"Server=tcp:{server},1433;Database={database};Encrypt=True;")
{
    AccessToken = token.Token,
};

await connection.OpenAsync();
Console.WriteLine($"Connected to {server}/{database}");

var failures = 0;

foreach (var identity in identities)
{
    // Bracket-quoted and the identity name is checked, because this is string-built T-SQL. The
    // names come from Bicep and cannot contain a bracket, but "cannot" is worth asserting when the
    // alternative is injection into a statement running as database owner.
    if (identity.Contains('[') || identity.Contains(']') || identity.Contains('\''))
    {
        Console.Error.WriteLine($"  {identity}: refused, the name contains a quoting character.");
        failures++;
        continue;
    }

    // Idempotent: a redeployment must not fail because the user already exists.
    var sql = $"""
        IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)
        BEGIN
            CREATE USER [{identity}] FROM EXTERNAL PROVIDER;
        END

        ALTER ROLE db_datareader ADD MEMBER [{identity}];
        ALTER ROLE db_datawriter ADD MEMBER [{identity}];
        """;

    try
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", identity);
        await command.ExecuteNonQueryAsync();

        // Deliberately not db_ddladmin. Services read and write rows; the schema is changed by the
        // migration step, which runs as the Entra admin. A service that can ALTER TABLE is a
        // service that can do it during a rolling deployment.
        Console.WriteLine($"  {identity}: reader + writer");
    }
    catch (SqlException exception)
    {
        Console.Error.WriteLine($"  {identity}: FAILED - {exception.Message}");
        failures++;
    }
}

Console.WriteLine(failures == 0 ? "All identities granted." : $"{failures} identity grant(s) failed.");

return failures == 0 ? 0 : 1;
