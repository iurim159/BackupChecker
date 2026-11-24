using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace BackupChecker;

/// <summary>Associazione server SQL -> bucket Wasabi.</summary>
public sealed record WasabiBucket(string Server, string Bucket, string Regione);

/// <summary>Chiavi (uguali per tutti i bucket) e associazioni server -> bucket.</summary>
public sealed record WasabiConfig(string AccessKey, string SecretKey, List<WasabiBucket> Buckets, DateTime CreatedUtc);

/// <summary>Configurazione Wasabi cifrata con DPAPI in wasabi.dat accanto all'exe.</summary>
public static class WasabiStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BackupChecker/v1/wasabi");

    public static string FilePath => Path.Combine(CredentialStore.Folder, "wasabi.dat");

    public static WasabiConfig? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<WasabiConfig>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Save(WasabiConfig cfg)
    {
        Directory.CreateDirectory(CredentialStore.Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(cfg);
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>wasabi.csv: server;bucket;regione  (separatore ; oppure ,; righe con # ignorate).</summary>
    public static List<WasabiBucket> LoadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
        var list = new List<WasabiBucket>();
        if (lines.Count == 0) return list;
        char sep = lines[0].Contains(';') ? ';' : ',';
        foreach (var line in lines)
        {
            var f = ServerList.SplitCsv(line, sep).Select(x => x.Trim()).ToList();
            if (f[0].Equals("server", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Count < 3 || f.Take(3).Any(string.IsNullOrEmpty))
                throw new FormatException($"Riga non valida in {Path.GetFileName(path)} (servono server;bucket;regione): \"{line}\"");
            list.Add(new WasabiBucket(f[0], f[1], f[2].ToLowerInvariant()));
        }
        return list;
    }
}

/// <summary>Un oggetto del bucket.</summary>
public sealed record OggettoCloud(string Key, long Size, DateTime? LastModified);

/// <summary>
/// Accesso in sola lettura a Wasabi (API S3, solo ListObjectsV2): elenca i file di un bucket.
/// Nessun download, nessuna scrittura.
/// </summary>
public static class WasabiClient
{
    public static string Endpoint(string regione) => $"https://s3.{regione}.wasabisys.com";

    public static AmazonS3Client Create(string accessKey, string secretKey, string regione) =>
        new(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = Endpoint(regione),
            AuthenticationRegion = regione,
            ForcePathStyle = true,          // i nomi dei bucket contengono punti
            Timeout = TimeSpan.FromSeconds(60),
            MaxErrorRetry = 2,
        });

    /// <summary>Elenca gli oggetti del bucket (tutti, oppure solo i primi <paramref name="max"/>).</summary>
    public static async Task<List<OggettoCloud>> ListAsync(string accessKey, string secretKey, string bucket, string regione,
                                                           int? max, CancellationToken ct)
    {
        using var s3 = Create(accessKey, secretKey, regione);
        var result = new List<OggettoCloud>();
        string? token = null;
        do
        {
            var req = new ListObjectsV2Request { BucketName = bucket, ContinuationToken = token };
            if (max is not null) req.MaxKeys = Math.Min(1000, max.Value - result.Count);
            var resp = await s3.ListObjectsV2Async(req, ct);
            foreach (var o in resp.S3Objects ?? new List<S3Object>())
            {
                long size = o.Size is long s ? s : 0;
                DateTime? lm = o.LastModified;
                result.Add(new OggettoCloud(o.Key, size, lm));
            }
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (token is not null && (max is null || result.Count < max));
        return result;
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        AmazonS3Exception s3 when s3.ErrorCode is "AccessDenied" => "accesso negato (la chiave non ha il permesso s3:ListBucket su questo bucket)",
        AmazonS3Exception s3 when s3.ErrorCode is "InvalidAccessKeyId" => "Access Key non valida",
        AmazonS3Exception s3 when s3.ErrorCode is "SignatureDoesNotMatch" => "Secret Key errata",
        AmazonS3Exception s3 when s3.ErrorCode is "NoSuchBucket" => "bucket inesistente",
        AmazonS3Exception s3 when s3.ErrorCode is "PermanentRedirect" or "AuthorizationHeaderMalformed" => "regione errata per questo bucket",
        AmazonS3Exception s3 => $"{s3.ErrorCode}: {s3.Message}",
        HttpRequestException => "Wasabi non raggiungibile (rete/proxy/firewall): " + ex.Message,
        OperationCanceledException => "timeout nella risposta di Wasabi",
        _ => ex.Message,
    };
}

/// <summary>
/// Verifica che il .bak dell'ultimo backup FULL sia presente anche su Wasabi, cercandolo per nome
/// in tutto il bucket (qualunque cartella) e confrontandone la dimensione.
/// Ogni bucket viene elencato una sola volta per esecuzione, anche se condiviso tra più server.
/// </summary>
public sealed class WasabiCheck
{
    private readonly WasabiConfig _cfg;
    private readonly ConcurrentDictionary<string, Lazy<Task<Dictionary<string, List<OggettoCloud>>>>> _indici = new();

    private readonly int _oreSoglia;

    /// <param name="oreSoglia">età massima accettata per l'ultima copia su Wasabi (la sincronizzazione è giornaliera).</param>
    public WasabiCheck(WasabiConfig cfg, int oreSoglia = 48) { _cfg = cfg; _oreSoglia = oreSoglia; }

    /// <summary>
    /// Bucket del server: prima per nome del foglio, poi per indirizzo, solo alla fine per nome SQL
    /// (@@SERVERNAME può essere uguale su macchine clonate).
    /// </summary>
    public WasabiBucket? BucketPer(ServerReport r) =>
        _cfg.Buckets.FirstOrDefault(b => Uguale(b.Server, r.DisplayName))
        ?? _cfg.Buckets.FirstOrDefault(b => Uguale(b.Server, r.Server))
        ?? _cfg.Buckets.FirstOrDefault(b => Uguale(b.Server, r.Istanza));

    private Task<Dictionary<string, List<OggettoCloud>>> Indice(WasabiBucket b, CancellationToken ct) =>
        _indici.GetOrAdd($"{b.Regione}|{b.Bucket}",
            _ => new Lazy<Task<Dictionary<string, List<OggettoCloud>>>>(() => CostruisciIndice(b, ct))).Value;

    private static bool Uguale(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public async Task ApplicaAsync(ServerReport r, CancellationToken ct)
    {
        if (!r.Ok) return;
        var conBackup = r.Databases.Where(d => d.FineBackup is not null).ToList();
        if (conBackup.Count == 0) return;

        var b = BucketPer(r);
        if (b is null) return;   // server senza bucket: nessun controllo cloud

        Dictionary<string, List<OggettoCloud>> indice;
        try
        {
            indice = await Indice(b, ct);
        }
        catch (Exception ex)
        {
            foreach (var d in conBackup) d.VerificaCloud = new(StatoFile.NonVerificabile, WasabiClient.FriendlyError(ex));
            return;
        }

        var ora = r.OraServer ?? DateTime.Now;
        var altri = _cfg.Buckets.Where(x => x.Bucket != b.Bucket).GroupBy(x => (x.Bucket, x.Regione)).Select(g => g.First()).ToList();
        foreach (var d in conBackup)
        {
            d.VerificaCloud = Verifica(d, indice, b.Bucket, ora, _oreSoglia);

            // Non è nel bucket previsto: lo cerco negli altri, per scoprire dove finiscono davvero i file.
            if (d.VerificaCloud.Stato == StatoFile.NonTrovato)
            {
                foreach (var altro in altri)
                {
                    Dictionary<string, List<OggettoCloud>> idx2;
                    try { idx2 = await Indice(altro, ct); } catch { continue; }
                    var v = Verifica(d, idx2, altro.Bucket, ora, _oreSoglia);
                    if (v.Stato != StatoFile.Ok) continue;
                    d.VerificaCloud = new(StatoFile.Ok,
                        $"trovata nel bucket {altro.Bucket} invece che in {b.Bucket} (correggere wasabi.csv): {v.Dettaglio}", v.Byte);
                    break;
                }
            }
            BackupInventory.ApplicaVerificaCloud(d);
        }
    }

    private async Task<Dictionary<string, List<OggettoCloud>>> CostruisciIndice(WasabiBucket b, CancellationToken ct)
    {
        var oggetti = await WasabiClient.ListAsync(_cfg.AccessKey, _cfg.SecretKey, b.Bucket, b.Regione, null, ct);
        return Indicizza(oggetti);
    }

    /// <summary>Indice nome-file -> oggetti (lo stesso nome può stare in più cartelle).</summary>
    public static Dictionary<string, List<OggettoCloud>> Indicizza(IEnumerable<OggettoCloud> oggetti)
    {
        var idx = new Dictionary<string, List<OggettoCloud>>(StringComparer.Ordinal);
        foreach (var o in oggetti)
        {
            if (o.Key.EndsWith('/')) continue;   // "cartelle"
            var name = BackupFiles.Split(o.Key).Name;
            if (!idx.TryGetValue(name, out var l)) idx[name] = l = new List<OggettoCloud>();
            l.Add(o);
        }
        return idx;
    }

    private static readonly Regex NomeConData = new(@"^(?<p>.+?)(?<d>\d{8}_\d{6})\.bak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 1) cerca il .bak dell'ultimo FULL (stesso nome, qualunque cartella) e ne controlla la dimensione;
    /// 2) se non c'è ancora (la copia su Wasabi avviene una volta al giorno), accetta l'ultima copia
    ///    dello stesso database, purché più recente di <paramref name="oreSoglia"/> ore.
    /// </summary>
    public static VerificaFile Verifica(DatabaseBackupInfo d, Dictionary<string, List<OggettoCloud>> indice, string bucket,
                                        DateTime ora, int oreSoglia = 48)
    {
        if (d.File.Count == 0) return new(StatoFile.NonVerificabile, "nessun file registrato in msdb");

        long totale = 0;
        var mancanti = new List<string>();
        var percorsi = new List<string>();
        foreach (var f in d.File)
        {
            var name = BackupFiles.Split(f.Path).Name;
            if (!indice.TryGetValue(name, out var trovati)) { mancanti.Add(name); continue; }
            var best = trovati.OrderByDescending(o => o.Size).First();
            totale += best.Size;
            percorsi.Add(best.Key);
        }

        if (mancanti.Count == 0)
        {
            if (d.ByteAttesi is > 0 && totale < d.ByteAttesi.Value * 0.95)
                return new(StatoFile.Incompleto, $"{totale / 1048576d:0.0} MB su {d.ByteAttesi.Value / 1048576d:0.0} MB attesi ({string.Join(", ", percorsi)})", totale);
            return new(StatoFile.Ok, string.Join(", ", percorsi), totale);
        }

        // Il backup più recente non è ancora su Wasabi: cerco l'ultima copia dello stesso database.
        var m = NomeConData.Match(BackupFiles.Split(d.File[0].Path).Name);
        if (!m.Success)
            return new(StatoFile.NonTrovato, $"non presente nel bucket {bucket}: {string.Join(", ", mancanti)}");

        var prefisso = m.Groups["p"].Value;
        var ultima = indice
            .Where(kv => kv.Key.StartsWith(prefisso, StringComparison.Ordinal))
            .Select(kv => (Nome: kv.Key, Match: NomeConData.Match(kv.Key), Obj: kv.Value.OrderByDescending(o => o.Size).First()))
            .Where(x => x.Match.Success && x.Match.Groups["p"].Value == prefisso && x.Obj.Size > 0)
            .Select(x => (x.Obj, Data: DataDalNome(x.Match.Groups["d"].Value) ?? x.Obj.LastModified?.ToLocalTime()))
            .Where(x => x.Data is not null)
            .OrderByDescending(x => x.Data)
            .FirstOrDefault();

        if (ultima.Obj is null)
            return new(StatoFile.NonTrovato, $"nessuna copia di {prefisso.TrimEnd('_')} nel bucket {bucket}");

        var eta = (ora - ultima.Data!.Value).TotalHours;
        if (eta > oreSoglia)
            return new(StatoFile.NonTrovato, $"ultima copia vecchia di {eta:0} ore: {ultima.Obj.Key}");

        return new(StatoFile.Ok, $"ultima copia {ultima.Obj.Key} ({eta:0} ore fa; il backup più recente sarà copiato alla prossima sincronizzazione)", ultima.Obj.Size);
    }

    private static DateTime? DataDalNome(string s) =>
        DateTime.TryParseExact(s, "yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
}
