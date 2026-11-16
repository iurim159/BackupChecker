using Microsoft.Data.SqlClient;

namespace BackupChecker;

public static class Sql
{
    public static string ConnectionString(string server, string user, string password, string database = "master")
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            UserID = user,
            Password = password,
            InitialCatalog = database,
            ConnectTimeout = 15,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,      // molti server usano certificati autofirmati
            ApplicationName = "BackupChecker",
            PersistSecurityInfo = false,
            Pooling = false,
        };
        return b.ConnectionString;
    }

    public static async Task<SqlConnection> OpenAsync(string server, string user, string password, CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionString(server, user, password));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public static async Task<object?> ScalarAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    public static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        SqlException se when se.Number == 18456 => "Login fallito: utente o password errati, oppure il server accetta solo l'autenticazione Windows.",
        SqlException se when se.Number is -2 or 258 => "Timeout: il server non ha risposto in tempo.",
        SqlException se when se.Number is 53 or 2 or -1 or 26 or 40 => "Server non raggiungibile (IP/istanza errati, firewall o servizio SQL fermo). " + se.Message,
        OperationCanceledException => "Operazione annullata o timeout.",
        _ => ex.Message,
    };
}
