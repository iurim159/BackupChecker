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
                "importa" or "import" => await ImportaQualsiasiAsync(opt.File ?? TrovaCsv(), cts.Token),
                "report" => await ReportAsync(opt, apri: false, cts.Token),
                "list" or "elenco" => Elenco(),
                "wasabi" => await WasabiImportaAsync(opt.File ?? TrovaFile("wasabi.csv"), cts.Token),
                "elenco-wasabi" or "wasabi-elenco" => await WasabiElencoAsync(opt.File ?? throw new ArgumentException("Indicare il server: BackupChecker elenco-wasabi SOL-MSSQL-C1-P3"), cts.Token),
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
            Console.WriteLine("  1) Importa un CSV (servers.csv per SQL oppure wasabi.csv per Wasabi)");
            Console.WriteLine("  2) Genera report Excel");
            Console.WriteLine("  3) Mostra server configurati");
            Console.WriteLine("  4) Togli un server dall'elenco");
            Console.WriteLine("  5) Mostra gli ultimi file di un bucket Wasabi");
            Console.WriteLine("  0) Esci");
            Console.Write("Scelta: ");
            var c = Console.ReadLine()?.Trim();
            try
            {
                switch (c)
                {
                    case "1": await ImportaQualsiasiAsync(ChiediCsv(), ct); break;
                    case "2": await ReportAsync(opt, apri: true, ct); break;
                    case "3": Elenco(); break;
                    case "4":
                        Console.Write("Server da togliere (come scritto nel CSV): ");
                        Rimuovi(Console.ReadLine()?.Trim() ?? "");
                        break;
                    case "5":
                        Console.Write("Server (come nel wasabi.csv): ");
                        await WasabiElencoAsync(Console.ReadLine()?.Trim() ?? "", ct);
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

        WasabiCheck? wasabi = null;
        if (!opt.SenzaWasabi)
        {
            var wcfg = WasabiStore.Load();
            if (wcfg is not null) wasabi = new WasabiCheck(wcfg, opt.OreSogliaWasabi);
        }

        Titolo($"Lettura backup da {creds.Count} server (login in sola lettura){(wasabi is null ? "" : " + copie su Wasabi")}");
        var gate = new SemaphoreSlim(Parallelismo);
        var reports = await Task.WhenAll(creds.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await BackupInventory.ReadAsync(c, opt.OreSoglia, !opt.SenzaFile, ct);
                if (wasabi is not null) await wasabi.ApplicaAsync(r, ct);
                r.NotaCloud = opt.SenzaWasabi ? "non controllato (--senza-wasabi)"
                            : wasabi is null ? "Wasabi non configurato"
                            : wasabi.BucketPer(r) is null ? "nessun bucket configurato" : null;
                if (r.Ok)
                {
                    int ko = r.Databases.Count(d => !d.IsOk);
                    string cloud = wasabi?.BucketPer(r) is { } b ? $" (Wasabi: {b.Bucket})" : "";
                    Riga(ko == 0 ? "[OK]     " : "[KO]     ", ko == 0 ? ConsoleColor.Green : ConsoleColor.Yellow,
                         c.Server, $"{r.Databases.Count} database - {ko} KO{cloud}");
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
                var det = d.VerificaFile is { Stato: not StatoFile.Ok } v && d.Esito.Contains("FILE") ? $" ({v.Dettaglio})"
                        : d.VerificaCloud is { Stato: not StatoFile.Ok } w && d.Esito.Contains("WASABI") ? $" ({w.Dettaglio})" : "";
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

    private static void Riga(string tag, ConsoleColor colore, string server, string msg)
    {
        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = colore;
            Console.Write("  " + tag);
            Console.ForegroundColor = prev;
            Console.WriteLine($"{server}: {msg}");
        }
    }

    private static string TrovaFile(string nome)
    {
        foreach (var dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var p = Path.Combine(dir, nome);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"File {nome} non trovato (cercato nella cartella corrente e in quella del programma).");
    }

    /// <summary>Lettura da tastiera senza mostrare i caratteri.</summary>
    private static string LeggiNascosto(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } continue; }
            if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString().Trim();
    }

    /// <summary>Un solo comando "importa": capisce dalle colonne se è il CSV dei server SQL o quello di Wasabi.</summary>
    private static Task<int> ImportaQualsiasiAsync(string csv, CancellationToken ct)
    {
        var header = File.ReadLines(csv).Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        bool isWasabi = header.Split(';', ',').Any(c => c.Trim().Equals("bucket", StringComparison.OrdinalIgnoreCase));
        return isWasabi ? WasabiImportaAsync(csv, ct) : ImportaAsync(csv, ct);
    }

    // ------------------------------------------------------------------ wasabi

    /// <summary>
    /// Legge wasabi.csv (server;bucket;regione), chiede Access Key e Secret Key a schermo,
    /// prova a elencare ogni bucket e, se tutto risponde, salva la configurazione cifrata.
    /// </summary>
    private static async Task<int> WasabiImportaAsync(string csv, CancellationToken ct)
    {
        var buckets = WasabiStore.LoadCsv(csv);
        if (buckets.Count == 0) { Errore("Nessuna riga in " + csv); return 1; }

        var server = CredentialStore.Load();
        foreach (var b in buckets.Where(b => !server.Any(c => string.Equals(c.DisplayName, b.Server, StringComparison.OrdinalIgnoreCase)
                                                           || string.Equals(c.Server, b.Server, StringComparison.OrdinalIgnoreCase))))
            Avviso($"Attenzione: '{b.Server}' non corrisponde a nessun server importato (usa il nome del foglio o l'IP del servers.csv).");

        var precedente = WasabiStore.Load();
        Titolo("Configurazione Wasabi (chiavi in sola lettura)");
        if (precedente is not null) Console.WriteLine("Premi Invio senza scrivere nulla per tenere le chiavi già salvate.");
        var ak = LeggiNascosto("Access Key: ");
        var sk = LeggiNascosto("Secret Key: ");
        if (ak.Length == 0 && sk.Length == 0 && precedente is not null) { ak = precedente.AccessKey; sk = precedente.SecretKey; }
        if (ak.Length == 0 || sk.Length == 0) { Errore("Access Key e Secret Key sono obbligatorie."); return 1; }

        int ok = 0;
        var distinti = buckets.Select(b => (b.Bucket, b.Regione)).Distinct().ToList();
        foreach (var (bucket, regione) in distinti)
        {
            try
            {
                var primi = await WasabiClient.ListAsync(ak, sk, bucket, regione, 5, ct);
                Riga(true, bucket, $"raggiungibile ({regione}), letti {primi.Count} file di prova");
                ok++;
            }
            catch (Exception ex) { Riga(false, bucket, WasabiClient.FriendlyError(ex)); }
        }

        Console.WriteLine();
        if (ok != distinti.Count)
        {
            Errore("Configurazione NON salvata: correggere i bucket in errore e rilanciare.");
            return 2;
        }
        WasabiStore.Save(new WasabiConfig(ak, sk, buckets, DateTime.UtcNow));
        Console.WriteLine($"Configurazione Wasabi salvata cifrata in: {WasabiStore.FilePath}");
        return 0;
    }

    /// <summary>Mostra gli ultimi file di un bucket: serve a vedere come sono organizzati i .bak.</summary>
    private static async Task<int> WasabiElencoAsync(string server, CancellationToken ct)
    {
        var cfg = WasabiStore.Load() ?? throw new InvalidOperationException("Wasabi non configurato: lanciare prima BackupChecker importa wasabi.csv");
        var b = cfg.Buckets.FirstOrDefault(x => string.Equals(x.Server, server, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(x.Bucket, server, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{server}' non è nel wasabi.csv importato.");
        Titolo($"Bucket {b.Bucket} ({b.Regione})");
        var all = await WasabiClient.ListAsync(cfg.AccessKey, cfg.SecretKey, b.Bucket, b.Regione, null, ct);
        Console.WriteLine($"File nel bucket: {all.Count:N0}   dimensione totale: {all.Sum(o => o.Size) / 1073741824d:N1} GB");
        Console.WriteLine("Ultimi 20 caricati:");
        foreach (var o in all.OrderByDescending(o => o.LastModified).Take(20))
            Console.WriteLine($"  {o.LastModified?.ToLocalTime():dd/MM/yyyy HH:mm}  {o.Size / 1048576d,10:N1} MB  {o.Key}");
        return 0;
    }

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
              BackupChecker importa servers.csv     verifica i login SQL in sola lettura e li salva cifrati
              BackupChecker importa wasabi.csv      configura le copie su Wasabi (chiede le chiavi a schermo)
              BackupChecker report [opzioni]        genera il report Excel (un foglio per server)
              BackupChecker list                    mostra i server configurati
              BackupChecker rimuovi <server>        toglie un server dall'elenco
              BackupChecker elenco-wasabi <server>  mostra gli ultimi file nel bucket Wasabi del server

            Il tool NON crea utenti e NON modifica nulla sui server: il login in sola lettura
            si crea a mano sul server.

            Opzioni del report:
              --output <cartella>     dove salvare il file (default: cartella Report accanto all'exe)
              --ore-soglia <n>        oltre queste ore dall'ultimo backup FULL l'esito è KO - NON RECENTE (default 24)
              --senza-file            non controlla la presenza dei file .bak (solo msdb e job)
              --senza-wasabi          non controlla le copie su Wasabi
              --ore-soglia-wasabi <n> età massima dell'ultima copia su Wasabi se il backup più recente
                                      non è ancora stato copiato (default 48)

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
    public bool SenzaWasabi { get; private set; }
    public int OreSogliaWasabi { get; private set; } = 48;

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
                case "--senza-wasabi": o.SenzaWasabi = true; break;
                case "--ore-soglia-wasabi":
                    o.OreSogliaWasabi = int.TryParse(Next(), out var w) && w > 0 ? w : throw new ArgumentException($"Valore non valido per {a}");
                    break;
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
