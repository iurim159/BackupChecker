using System.Text;

namespace BackupChecker;

/// <summary>Riga del file servers.csv: credenziali del login IN SOLA LETTURA di un server.</summary>
public sealed record ServerEntry(string Server, string User, string Password, string? DisplayName);

public static class ServerList
{
    /// <summary>
    /// Legge il CSV (separatore ';' oppure ','; righe che iniziano con '#' ignorate;
    /// campi tra virgolette supportati, con "" per le virgolette interne).
    /// Colonne: server;utente;password;nome(facoltativo)
    /// </summary>
    public static List<ServerEntry> Load(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToList();

        var result = new List<ServerEntry>();
        if (lines.Count == 0) return result;

        char sep = lines[0].Contains(';') ? ';' : ',';
        int lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            var f = SplitCsv(line, sep);
            if (lineNo == 1 && IsHeader(f)) continue;
            if (f.Count < 3)
                throw new FormatException($"Riga non valida (servono almeno server;utente;password): \"{line}\"");

            var server = f[0].Trim();
            if (server.Length == 0) continue;
            var name = f.Count > 3 && !string.IsNullOrWhiteSpace(f[3]) ? f[3].Trim() : null;
            // La password NON viene trimmata: gli spazi potrebbero farne parte.
            result.Add(new ServerEntry(server, f[1].Trim(), f[2], name));
        }

        var dup = result.GroupBy(r => r.Server, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new FormatException($"Server duplicato nel file: {dup.Key}");
        return result;
    }

    private static bool IsHeader(List<string> f)
    {
        var first = f[0].Trim().ToLowerInvariant();
        return first is "server" or "ip" or "host" or "indirizzo";
    }

    public static List<string> SplitCsv(string line, char sep)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) inQuotes = true;
            else if (c == sep) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
