using Microsoft.Data.SqlClient;

namespace BackupChecker;

/// <summary>Riga del censimento: un database.</summary>
public sealed class DatabaseBackupInfo
{
    public required string Database { get; init; }
    public required string Stato { get; init; }
    public required string Recovery { get; init; }
    public bool HaJob { get; init; }
    public bool JobAbilitato { get; init; }
    public int? EsitoJobPrima { get; init; }        // run_status ultima esecuzione: 0 fallito, 1 ok, 3 annullato
    public string? OrarioJob { get; init; }         // orario della pianificazione (HH:mm)
    public DateTime? InizioBackup { get; init; }    // ultimo backup FULL
    public DateTime? FineBackup { get; init; }

    public string Esito { get; set; } = "";
    public bool IsOk => Esito.StartsWith("OK", StringComparison.Ordinal);
}

public sealed class ServerReport
{
    public required string Server { get; init; }          // come indicato nel CSV (IP/istanza)
    public string? DisplayName { get; init; }
    public string? Istanza { get; set; }                   // @@SERVERNAME
    public DateTime? OraServer { get; set; }
    public List<DatabaseBackupInfo> Databases { get; } = new();
    public string? Errore { get; set; }
    public bool Ok => Errore is null;
}

/// <summary>
/// Censimento dei backup (stessa logica dello script di censimento, parte in sola lettura):
/// solo SELECT su sys.databases e sulle tabelle di msdb. Nessun job viene avviato.
/// </summary>
public static class BackupInventory
{
    private const string InfoQuery = """
        SET NOCOUNT ON;
        SELECT @@SERVERNAME, GETDATE();
        """;

    private const string CensimentoQuery = """
        SET NOCOUNT ON;
        WITH passi AS (
            SELECT st.job_id,
                   st.database_name COLLATE DATABASE_DEFAULT AS DbContesto,
                   N' ' + REPLACE(REPLACE(REPLACE(
                          REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                              st.command COLLATE DATABASE_DEFAULT,
                          CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), N'[', N''), N']', N''), N';', N' '),
                          N'  ', N' '), N'  ', N' '), N'  ', N' ') + N' ' AS Cmd
            FROM msdb.dbo.sysjobsteps st
        ),
        job_db AS (
            SELECT DISTINCT d.name AS DatabaseName, j.job_id, j.enabled AS JobAbilitato
            FROM sys.databases d
            JOIN passi p
              ON CHARINDEX(N' BACKUP DATABASE ' + d.name + N' ', p.Cmd) > 0
              OR (p.DbContesto = d.name AND p.Cmd LIKE N'% BACKUP DATABASE %')
            JOIN msdb.dbo.sysjobs j ON j.job_id = p.job_id
        )
        SELECT d.name                AS DatabaseName,
               d.state_desc          AS Stato,
               d.recovery_model_desc AS Recovery,
               CASE WHEN jj.job_id IS NULL THEN 0 ELSE 1 END AS HaJob,
               jj.JobAbilitato,
               jj.run_status         AS EsitoJobPrima,
               jj.OrarioJob,
               b.backup_start_date,
               b.backup_finish_date
        FROM sys.databases d
        OUTER APPLY (
            SELECT TOP (1) jd.job_id, jd.JobAbilitato, h.run_status,
                   (SELECT TOP (1) LEFT(STUFF(RIGHT('000000' + CAST(s.active_start_time AS varchar(6)), 6), 3, 0, ':'), 5)
                    FROM msdb.dbo.sysjobschedules js
                    JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
                    WHERE js.job_id = jd.job_id AND s.enabled = 1
                    ORDER BY s.active_start_time) AS OrarioJob
            FROM job_db jd
            OUTER APPLY (SELECT TOP (1) jh.run_status, jh.instance_id
                         FROM msdb.dbo.sysjobhistory jh
                         WHERE jh.job_id = jd.job_id AND jh.step_id = 0
                         ORDER BY jh.instance_id DESC) h
            WHERE jd.DatabaseName = d.name
            ORDER BY h.instance_id DESC
        ) jj
        OUTER APPLY (SELECT TOP (1) bs.backup_start_date, bs.backup_finish_date
                     FROM msdb.dbo.backupset bs
                     WHERE bs.database_name COLLATE SQL_Latin1_General_CP1_CI_AS
                         = d.name          COLLATE SQL_Latin1_General_CP1_CI_AS
                       AND bs.type = 'D'
                     ORDER BY bs.backup_finish_date DESC) b
        WHERE d.name NOT IN (N'tempdb', N'master', N'model', N'msdb')
          AND d.source_database_id IS NULL
        ORDER BY d.name;
        """;

    public static async Task<ServerReport> ReadAsync(ReadOnlyCredential cred, int oreSoglia, CancellationToken ct)
    {
        var rep = new ServerReport { Server = cred.Server, DisplayName = cred.DisplayName };
        try
        {
            await using var conn = await Sql.OpenAsync(cred.Server, cred.Login, cred.Password, ct);

            // Ad ogni report si ricontrolla che il login sia ancora in sola lettura.
            await ReadOnlyGuard.EnsureAsync(conn, cred.Login, ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = InfoQuery;
                cmd.CommandTimeout = 60;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    rep.Istanza = r.IsDBNull(0) ? null : r.GetString(0);
                    rep.OraServer = r.IsDBNull(1) ? null : r.GetDateTime(1);
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CensimentoQuery;
                cmd.CommandTimeout = 120;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    rep.Databases.Add(new DatabaseBackupInfo
                    {
                        Database = r.GetString(0),
                        Stato = r.IsDBNull(1) ? "" : r.GetString(1),
                        Recovery = r.IsDBNull(2) ? "" : r.GetString(2),
                        HaJob = Convert.ToInt32(r.GetValue(3)) == 1,
                        JobAbilitato = !r.IsDBNull(4) && Convert.ToInt32(r.GetValue(4)) == 1,
                        EsitoJobPrima = r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5)),
                        OrarioJob = r.IsDBNull(6) ? null : r.GetString(6),
                        InizioBackup = r.IsDBNull(7) ? null : r.GetDateTime(7),
                        FineBackup = r.IsDBNull(8) ? null : r.GetDateTime(8),
                    });
                }
            }

            var now = rep.OraServer ?? DateTime.Now;
            foreach (var db in rep.Databases) Valuta(db, now, oreSoglia);
        }
        catch (Exception ex)
        {
            rep.Errore = Sql.FriendlyError(ex);
        }
        return rep;
    }

    /// <summary>Esito come nello script di censimento (con @Esegui = 0).</summary>
    public static void Valuta(DatabaseBackupInfo db, DateTime oraServer, int oreSoglia)
    {
        bool jobFallito = db.HaJob && db.JobAbilitato && db.OrarioJob is not null
                          && db.EsitoJobPrima is 0 or 3;

        string esito;
        if (!string.Equals(db.Stato, "ONLINE", StringComparison.OrdinalIgnoreCase))
            esito = "KO - DATABASE " + db.Stato;
        else if (jobFallito)
            esito = "KO - JOB FALLITO (da riavviare)";
        else if (db.FineBackup is null)
            esito = "KO - MAI BACKUPPATO";
        else if (db.FineBackup < oraServer.AddHours(-oreSoglia))
            esito = "KO - NON RECENTE";
        else
            esito = "OK";

        if (!db.HaJob) esito += " | NESSUN JOB";
        db.Esito = esito;
    }
}
