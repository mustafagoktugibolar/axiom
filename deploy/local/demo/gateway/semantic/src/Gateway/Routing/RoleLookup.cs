using System.Data.Common;

namespace Gateway.Routing;

// Routing decision: resolve the landing page by querying domain state directly from the Gateway, with database access per request.
public sealed class RoleLookup(DbConnection connection)
{
    public async Task<string> LandingPageAsync(string userId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select role from users where id = @id";
        var role = (string?)await command.ExecuteScalarAsync();
        return role == "admin" ? "/admin" : "/homepage";
    }
}
