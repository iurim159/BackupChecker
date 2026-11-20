using Microsoft.Data.SqlClient;

namespace BackupChecker;

/// <summary>
/// Verifica che il login usato dal tool sia davvero in sola lettura.
/// Se trova un qualunque privilegio amministrativo o di scrittura, il tool si rifiuta di usarlo.
/// Le verifiche sono solo SELECT su funzioni di sistema: non modificano nulla.
/// </summary>
public static class ReadOnlyGuard
{
    private const string CheckQuery = """
        SET NOCOUNT ON;
        SELECT 'ruolo server sysadmin'      , ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0)      UNION ALL
        SELECT 'ruolo server serveradmin'   , ISNULL(IS_SRVROLEMEMBER('serveradmin'), 0)   UNION ALL
        SELECT 'ruolo server securityadmin' , ISNULL(IS_SRVROLEMEMBER('securityadmin'), 0) UNION ALL
        SELECT 'ruolo server setupadmin'    , ISNULL(IS_SRVROLEMEMBER('setupadmin'), 0)    UNION ALL
        SELECT 'ruolo server processadmin'  , ISNULL(IS_SRVROLEMEMBER('processadmin'), 0)  UNION ALL
        SELECT 'ruolo server diskadmin'     , ISNULL(IS_SRVROLEMEMBER('diskadmin'), 0)     UNION ALL
        SELECT 'ruolo server dbcreator'     , ISNULL(IS_SRVROLEMEMBER('dbcreator'), 0)     UNION ALL
        SELECT 'ruolo server bulkadmin'     , ISNULL(IS_SRVROLEMEMBER('bulkadmin'), 0)     UNION ALL
        SELECT 'permesso CONTROL SERVER'    , ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER'), 0)     UNION ALL
        SELECT 'permesso ALTER ANY LOGIN'   , ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN'), 0)    UNION ALL
        SELECT 'permesso ALTER ANY DATABASE', ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY DATABASE'), 0) UNION ALL
        SELECT 'permesso CREATE ANY DATABASE', ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'), 0) UNION ALL
        SELECT 'INSERT su msdb.dbo.backupset', ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupset', 'OBJECT', 'INSERT'), 0) UNION ALL
        SELECT 'UPDATE su msdb.dbo.backupset', ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupset', 'OBJECT', 'UPDATE'), 0) UNION ALL
        SELECT 'DELETE su msdb.dbo.backupset', ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupset', 'OBJECT', 'DELETE'), 0) UNION ALL
        SELECT 'EXECUTE su msdb.dbo.sp_start_job', ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sp_start_job', 'OBJECT', 'EXECUTE'), 0) UNION ALL
        SELECT 'EXECUTE su msdb.dbo.sp_stop_job', ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sp_stop_job', 'OBJECT', 'EXECUTE'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.backupset', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupset', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.backupmediafamily', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupmediafamily', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.sysjobs', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobs', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.sysjobsteps', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobsteps', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.sysjobschedules', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobschedules', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.sysschedules', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysschedules', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.sysjobhistory', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobhistory', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.sysjobactivity', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobactivity', 'OBJECT', 'SELECT'), 0) UNION ALL
        SELECT 'manca SELECT su msdb.dbo.syssessions', 1 - ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.syssessions', 'OBJECT', 'SELECT'), 0);
        """;

    /// <summary>Restituisce l'elenco dei problemi trovati (vuoto = login in sola lettura e utilizzabile).</summary>
    public static async Task<List<string>> CheckAsync(SqlConnection conn, CancellationToken ct)
    {
        var problems = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CheckQuery;
        cmd.CommandTimeout = 60;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!r.IsDBNull(1) && Convert.ToInt32(r.GetValue(1)) != 0)
                problems.Add(r.GetString(0));
        }
        return problems;
    }

    /// <summary>Lancia un'eccezione se il login non è in sola lettura.</summary>
    public static async Task EnsureAsync(SqlConnection conn, string login, CancellationToken ct)
    {
        var p = await CheckAsync(conn, ct);
        var privilegi = p.Where(x => !x.StartsWith("manca")).ToList();
        if (privilegi.Count > 0)
            throw new InvalidOperationException(
                $"Il login '{login}' NON è in sola lettura ({string.Join(", ", privilegi)}). Per sicurezza il tool non lo usa.");
        var mancanti = p.Where(x => x.StartsWith("manca")).ToList();
        if (mancanti.Count > 0)
            throw new InvalidOperationException(
                $"Il login '{login}' non ha i permessi necessari ({string.Join(", ", mancanti)}). Concedere i permessi SELECT mancanti in msdb.");
    }
}
