using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BackupChecker;

/// <summary>Credenziali del login in sola lettura di un server.</summary>
public sealed record ReadOnlyCredential(string Server, string? DisplayName, string Login, string Password, DateTime CreatedUtc);

/// <summary>
/// Archivio delle credenziali in sola lettura, cifrato con Windows DPAPI (ambito utente corrente):
/// il file è leggibile solo dallo stesso utente Windows sullo stesso PC.
/// </summary>
public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BackupChecker/v1/readonly-credentials");

    /// <summary>
    /// Modalità portatile: tutto (credenziali e report) sta nella cartella dell'exe.
    /// Se quella cartella non è scrivibile (es. Programmi) si usa %LOCALAPPDATA%\BackupChecker.
    /// </summary>
    public static string Folder { get; } = ScegliCartella();

    private static string ScegliCartella()
    {
        var exeDir = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(exeDir, ".scrittura-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return exeDir;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupChecker");
        }
    }

    public static string FilePath => Path.Combine(Folder, "credenziali-sola-lettura.dat");

    public static List<ReadOnlyCredential> Load()
    {
        if (!File.Exists(FilePath)) return new();
        var cipher = File.ReadAllBytes(FilePath);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<List<ReadOnlyCredential>>(plain) ?? new();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void Save(List<ReadOnlyCredential> items)
    {
        Directory.CreateDirectory(Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(items.OrderBy(i => i.Server, StringComparer.OrdinalIgnoreCase).ToList());
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void Upsert(ReadOnlyCredential cred)
    {
        var all = Load();
        all.RemoveAll(c => string.Equals(c.Server, cred.Server, StringComparison.OrdinalIgnoreCase));
        all.Add(cred);
        Save(all);
    }

    public static bool Remove(string server)
    {
        var all = Load();
        int n = all.RemoveAll(c => string.Equals(c.Server, server, StringComparison.OrdinalIgnoreCase));
        if (n > 0) Save(all);
        return n > 0;
    }
}
