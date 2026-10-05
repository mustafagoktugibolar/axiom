using Npgsql;

namespace Gateway.Routing;

// Picks a landing page per user role by reading the users table directly.
public sealed class RoleRouter(string connectionString)
{
    public async Task<string> LandingPageAsync(string userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select role from users where id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        var role = (string?)await command.ExecuteScalarAsync();
        return role == "admin" ? "/admin" : "/homepage";
    }
}
