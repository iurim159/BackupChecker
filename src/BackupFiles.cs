using Microsoft.Data.SqlClient;

namespace BackupChecker;

/// <summary>File di un backup come registrato in msdb.dbo.backupmediafamily.</summary>
public sealed record BackupFile(int DeviceType, string Path);

public enum StatoFile { Ok, NonTrovato, Incompleto, NonVerificabile }

public sealed record VerificaFile(StatoFile Stato, string Dettaglio, long? Byte = null);

public static class BackupFiles
{
    /// <summary>Formato prodotto dalla query: "2>/mnt/x.bak|2>/mnt/y.bak".</summary>
    public static List<BackupFile> ParseList(string? s)
    {
        var list = new List<BackupFile>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (var item in s.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = item.IndexOf('>');
            if (i < 0) continue;
            int.TryParse(item[..i], out var type);
            var path = item[(i + 1)..].Trim();
            if (path.Length > 0) list.Add(new BackupFile(type, path));
        }
        return list;
    }

    /// <summary>Cartella e nome del file, sia per percorsi Linux (/) sia Windows (\).</summary>
    public static (string Dir, string Name) Split(string path)
    {
        int i = path.LastIndexOfAny(new[] { '/', '\\' });
        if (i < 0) return ("", path);
        var dir = path[..i];
        if (dir.Length == 0) dir = path[..1];                       // "/file.bak" -> "/"
        else if (dir.Length == 2 && dir[1] == ':') dir += "\\";     // "D:\file.bak" -> "D:\"
        return (dir, path[(i + 1)..]);
    }
}

/// <summary>
/// Verifica dei file di backup fatta da SQL Server stesso, con funzioni di sola lettura:
/// sys.dm_os_file_exists (esistenza) e sys.dm_os_enumerate_filesystem (dimensione).
/// Il tool non apre né modifica i file.
/// </summary>
public static class SqlFileCheck
{
    public static async Task<VerificaFile> CheckAsync(SqlConnection conn, IReadOnlyList<BackupFile> files, long? byteAttesi, CancellationToken ct)
    {
        if (files.Count == 0) return new(StatoFile.NonVerificabile, "nessun file registrato in msdb");

        long totale = 0;
        bool dimensioneNota = true;
        var mancanti = new List<string>();

        foreach (var f in files)
        {
            if (f.DeviceType == 9) return new(StatoFile.NonVerificabile, "backup su URL/cloud");
            if (f.DeviceType == 7 || f.Path.StartsWith("{", StringComparison.Ordinal))
                return new(StatoFile.NonVerificabile, "backup tramite software esterno (dispositivo virtuale)");

            var (esiste, cartellaEsiste) = await EsisteAsync(conn, f.Path, ct);
            if (!esiste)
            {
                mancanti.Add(cartellaEsiste ? f.Path : f.Path + " (cartella inesistente)");
                continue;
            }

            var size = await DimensioneAsync(conn, f.Path, ct);
            if (size is null) dimensioneNota = false;
            else totale += size.Value;
        }

        if (mancanti.Count > 0) return new(StatoFile.NonTrovato, "mancante: " + string.Join(", ", mancanti));
        if (!dimensioneNota) return new(StatoFile.Ok, "presente (dimensione non verificata)");
        if (byteAttesi is > 0 && totale < byteAttesi.Value * 0.95)
            return new(StatoFile.Incompleto, $"{totale / 1048576d:0.0} MB su {byteAttesi.Value / 1048576d:0.0} MB attesi", totale);
        return new(StatoFile.Ok, "presente", totale);
    }

    private static async Task<(bool Esiste, bool CartellaEsiste)> EsisteAsync(SqlConnection conn, string path, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_exists, parent_directory_exists FROM sys.dm_os_file_exists(@p);";
            cmd.CommandTimeout = 60;
            cmd.Parameters.AddWithValue("@p", path);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return (false, false);
            return (Convert.ToInt32(r.GetValue(0)) == 1, Convert.ToInt32(r.GetValue(1)) == 1);
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            throw new FileCheckNonDisponibile("SQL Server precedente al 2017: verifica dei file non disponibile");
        }
        catch (SqlException ex) when (ex.Number == 229 || ex.Number == 300 || ex.Number == 297)
        {
            throw new FileCheckNonDisponibile("permesso negato su sys.dm_os_file_exists");
        }
    }

    /// <summary>Dimensione del file; null se la funzione non è disponibile o non risponde.</summary>
    private static async Task<long?> DimensioneAsync(SqlConnection conn, string path, CancellationToken ct)
    {
        var (dir, name) = BackupFiles.Split(path);
        if (dir.Length == 0 || name.Length == 0 || name.IndexOfAny(new[] { '*', '?' }) >= 0) return null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP (1) size_in_bytes FROM sys.dm_os_enumerate_filesystem(@d, @n) WHERE is_directory = 0;";
            cmd.CommandTimeout = 60;
            cmd.Parameters.AddWithValue("@d", dir);
            cmd.Parameters.AddWithValue("@n", name);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is null or DBNull ? null : Convert.ToInt64(v);
        }
        catch (SqlException)
        {
            return null;
        }
    }
}

/// <summary>La verifica dei file non è possibile su tutto il server (versione o permessi).</summary>
public sealed class FileCheckNonDisponibile(string message) : Exception(message);
