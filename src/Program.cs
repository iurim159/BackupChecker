using System.Diagnostics;
using System.Text;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace BackupChecker;

public static class Program
{
    private const int Parallelismo = 8;

    public static async Task<int> Main(string[] args)
    {
        // Driver di rete gestito: nessuna DLL nativa da estrarre (vedi anche il .csproj)
        AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Questo tool funziona solo su Windows (usa DPAPI per proteggere le credenziali).");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var opt = Opzioni.Parse(args);
            if (opt.Comando is null) return await MenuAsync(opt, cts.Token);

            return opt.Comando switch
            {
                "importa" or "import" => await ImportaAsync(opt.File ?? TrovaCsv(), cts.Token),
                "report" => await ReportAsync(opt, apri: false, cts.Token),
                "list" or "elenco" => Elenco(),
                "rimuovi" or "remove" => Rimuovi(opt.File ?? throw new ArgumentException("Indicare il server da rimuovere: BackupChecker rimuovi 192.168.1.10")),
                "help" or "-h" or "--help" or "/?" => Aiuto(),
                _ => Aiuto(1),
            };
        }
        catch (Exception ex)
        {
            Errore(ex.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------------ menu

    private static async Task<int> MenuAsync(Opzioni opt, CancellationToken ct)
    {
        while (true)
        {
            Console.WriteLine();
            Titolo("BackupChecker - controllo backup SQL Server");
            Console.WriteLine("  1) Importa/aggiorna server e credenziali in sola lettura (da servers.csv)");
            Console.WriteLine("  2) Genera report Excel");
            Console.WriteLine("  3) Mostra server configurati");
            Console.WriteLine("  4) Togli un server dall'elenco");
            Console.WriteLine("  0) Esci");
            Console.Write("Scelta: ");
            var c = Console.ReadLine()?.Trim();
            try
            {
                switch (c)
                {
                    case "1": await ImportaAsync(ChiediCsv(), ct); break;
                    case "2": await ReportAsync(opt, apri: true, ct); break;
                    case "3": Elenco(); break;
                    case "4":
                        Console.Write("Server da togliere (come scritto nel CSV): ");
                        Rimuovi(Console.ReadLine()?.Trim() ?? "");
                        break;
                    case "0" or null: return 0;
                }
            }
            catch (Exception ex) { Errore(ex.Message); }
        }
    }

    // ------------------------------------------------------------------ importa

    /// <summary>
    /// Legge servers.csv (credenziali dei login in sola lettura creati a mano), verifica su ogni server
    /// che il login sia davvero in sola lettura e salva le credenziali cifrate. Non esegue nessuna modifica sui server.
    /// </summary>
    private static async Task<int> ImportaAsync(string csv, CancellationToken ct)
    {
        var servers = ServerList.Load(csv);
        if (servers.Count == 0) { Errore("Nessun server nel file " + csv); return 1; }

        Titolo($"Verifica credenziali in sola lettura su {servers.Count} server");
        int ok = 0;
        var gate = new SemaphoreSlim(Parallelismo);
        var store = new object();

        await Task.WhenAll(servers.Select(async s =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await using (var conn = await Sql.OpenAsync(s.Server, s.User, s.Password, ct))
                    await ReadOnlyGuard.EnsureAsync(conn, s.User, ct);
                lock (store)
                {
                    CredentialStore.Upsert(new ReadOnlyCredential(s.Server, s.DisplayName, s.User, s.Password, DateTime.UtcNow));
                    ok++;
                }
                Riga(true, s.Server, $"login '{s.User}' verificato in sola lettura e salvato");
            }
            catch (Exception ex) { Riga(false, s.Server, Sql.FriendlyError(ex)); }
            finally { gate.Release(); }
        }));

        Console.WriteLine();
        Console.WriteLine($"Importati {ok} server su {servers.Count}. Credenziali cifrate in: {CredentialStore.FilePath}");
        if (ok > 0)
            Avviso($"Le credenziali sono ora salvate cifrate: puoi cancellare o spostare \"{csv}\".");
        return ok == servers.Count ? 0 : 2;
    }

    // ------------------------------------------------------------------ report

    private static async Task<int> ReportAsync(Opzioni opt, bool apri, CancellationToken ct)
    {
        var creds = CredentialStore.Load();
        if (creds.Count == 0)
        {
            Errore("Nessun server configurato. Eseguire prima l'importazione: BackupChecker importa servers.csv");
            return 1;
        }

        Titolo($"Lettura backup da {creds.Count} server (login in sola lettura)");
        var gate = new SemaphoreSlim(Parallelismo);
        var reports = await Task.WhenAll(creds.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await BackupInventory.ReadAsync(c, opt.OreSoglia, !opt.SenzaFile, ct);
                if (r.Ok)
                {
                    int ko = r.Databases.Count(d => !d.IsOk);
                    Riga(ko == 0, c.Server, $"{r.Databases.Count} database - {ko} KO");
                }
                else Riga(false, c.Server, r.Errore!);
                return r;
            }
            finally { gate.Release(); }
        }));

        // Ordine del file Excel = ordine alfabetico dei server
        var ordered = reports.OrderBy(r => r.DisplayName ?? r.Server, StringComparer.OrdinalIgnoreCase).ToList();

        var dir = opt.Output ?? Path.Combine(CredentialStore.Folder, "Report");
        Directory.CreateDirectory(dir);
        var now = DateTime.Now;
        var path = Path.Combine(dir, $"Report_Backup_{now:yyyy-MM-dd_HHmm}.xlsx");
        ExcelReport.Write(path, ordered);

        Console.WriteLine();
        Console.WriteLine("Report salvato in: " + path);
        Riepilogo(ordered, opt.SenzaFile);
        if (apri)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* Excel non installato */ }
        }
        return reports.All(r => r.Ok && r.Databases.All(d => d.IsOk)) ? 0 : 2;
    }

    /// <summary>Esito finale a colpo d'occhio: OK solo se tutti i server e tutti i database sono OK.</summary>
    private static void Riepilogo(IReadOnlyList<ServerReport> reports, bool senzaFile)
    {
        var problemi = new List<string>();
        foreach (var r in reports)
        {
            var nome = r.DisplayName ?? r.Server;
            if (!r.Ok) { problemi.Add($"{nome}: KO - SERVER NON RAGGIUNGIBILE ({r.Errore})"); continue; }
            foreach (var d in r.Databases.Where(d => !d.IsOk))
            {
                var det = d.VerificaFile is { Stato: not StatoFile.Ok } v && d.Esito.Contains("FILE") ? $" ({v.Dettaglio})" : "";
                problemi.Add($"{nome} / {d.Database}: {d.Esito}{det}");
            }
        }

        int nDb = reports.Sum(r => r.Databases.Count);
        Console.WriteLine();
        var prev = Console.ForegroundColor;
        if (problemi.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("==================================================");
            Console.WriteLine($"  ESITO: OK   {reports.Count} server, {nDb} database");
            Console.WriteLine(senzaFile ? "  (file .bak NON controllati: opzione --senza-file)" : "  backup recenti e file .bak presenti");
            Console.WriteLine("==================================================");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("==================================================");
            Console.WriteLine($"  ESITO: KO   {problemi.Count} problemi su {reports.Count} server, {nDb} database");
            Console.WriteLine("==================================================");
            Console.ForegroundColor = prev;
            foreach (var p in problemi) Console.WriteLine("  - " + p);
        }
        Console.ForegroundColor = prev;
    }

    // ------------------------------------------------------------------ elenco / rimozione

    private static int Elenco()
    {
        var creds = CredentialStore.Load();
        Titolo($"Server configurati: {creds.Count}");
        foreach (var c in creds)
            Console.WriteLine($"  {c.Server,-22} {c.DisplayName ?? "",-25} login: {c.Login,-14} aggiornato: {c.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        Console.WriteLine($"File credenziali: {CredentialStore.FilePath}");
        return 0;
    }

    private static int Rimuovi(string server)
    {
        if (CredentialStore.Remove(server)) { Riga(true, server, "tolto dall'elenco (sul server non viene modificato nulla)"); return 0; }
        Errore("Server non presente nell'elenco: " + server);
        return 1;
    }

    // ------------------------------------------------------------------ utilità console

    private static string TrovaCsv()
    {
        foreach (var dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var p = Path.Combine(dir, "servers.csv");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("File servers.csv non trovato (cercato nella cartella corrente e in quella del programma). Indicare il percorso: BackupChecker importa C:\\percorso\\servers.csv");
    }

    private static string ChiediCsv()
    {
        string? def = null;
        try { def = TrovaCsv(); } catch { }
        Console.Write(def is null ? "Percorso del file CSV: " : $"Percorso del file CSV [{def}]: ");
        var p = Console.ReadLine()?.Trim().Trim('"');
        var path = string.IsNullOrEmpty(p) ? def : p;
        if (path is null || !File.Exists(path)) throw new FileNotFoundException("File non trovato: " + path);
        return path;
    }

    private static readonly object ConsoleLock = new();

    private static void Riga(bool ok, string server, string msg)
    {
        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  [OK]     " : "  [ERRORE] ");
            Console.ForegroundColor = prev;
            Console.WriteLine($"{server}: {msg}");
        }
    }

    private static void Titolo(string t)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(t);
        Console.WriteLine(new string('-', Math.Min(t.Length, 100)));
        Console.ForegroundColor = prev;
    }

    private static void Errore(string msg)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERRORE: " + msg);
        Console.ForegroundColor = prev;
    }

    private static void Avviso(string msg)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg);
        Console.ForegroundColor = prev;
    }

    private static int Aiuto(int code = 0)
    {
        Console.WriteLine("""
            BackupChecker - controllo backup SQL Server

            Uso:
              BackupChecker                         menu interattivo
              BackupChecker importa [servers.csv]   verifica i login in sola lettura del CSV e li salva cifrati
              BackupChecker report [opzioni]        genera il report Excel (un foglio per server)
              BackupChecker list                    mostra i server configurati
              BackupChecker rimuovi <server>        toglie un server dall'elenco

            Il tool NON crea utenti e NON modifica nulla sui server: il login in sola lettura
            si crea a mano sul server.

            Opzioni del report:
              --output <cartella>     dove salvare il file (default: cartella Report accanto all'exe)
              --ore-soglia <n>        oltre queste ore dall'ultimo backup FULL l'esito è KO - NON RECENTE (default 24)
              --senza-file            non controlla la presenza dei file .bak (solo msdb e job)

            Codici di uscita: 0 = tutto ok, 1 = errore, 2 = alcuni server in errore.
            """);
        return code;
    }
}

public sealed class Opzioni
{
    public string? Comando { get; private set; }
    public string? File { get; private set; }
    public string? Output { get; private set; }
    public int OreSoglia { get; private set; } = 24;
    public bool SenzaFile { get; private set; }

    public static Opzioni Parse(string[] args)
    {
        var o = new Opzioni();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Manca il valore per {a}");

            switch (a.ToLowerInvariant())
            {
                case "--output" or "-o": o.Output = Next(); break;
                case "--senza-file": o.SenzaFile = true; break;
                case "--ore-soglia":
                    o.OreSoglia = int.TryParse(Next(), out var v) && v > 0 ? v : throw new ArgumentException($"Valore non valido per {a}");
                    break;
                default:
                    if (o.Comando is null) o.Comando = a.ToLowerInvariant();
                    else if (o.File is null) o.File = a;
                    else throw new ArgumentException("Argomento non riconosciuto: " + a);
                    break;
            }
        }
        return o;
    }
}
