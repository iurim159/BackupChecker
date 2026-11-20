/* Allinea i permessi del login SQL [bkp_readonly] a quelli richiesti da BackupChecker.
   Eseguire come AMMINISTRATORE su ogni server dove il login esiste già.
   Solo permessi di LETTURA; rieseguibile senza problemi. */
USE [msdb];
GRANT SELECT ON dbo.backupset          TO [bkp_readonly];   -- ultimo backup FULL
GRANT SELECT ON dbo.backupmediafamily  TO [bkp_readonly];   -- percorso del file .bak
GRANT SELECT ON dbo.sysjobs            TO [bkp_readonly];   -- job di backup
GRANT SELECT ON dbo.sysjobsteps        TO [bkp_readonly];
GRANT SELECT ON dbo.sysjobschedules    TO [bkp_readonly];   -- orario pianificato
GRANT SELECT ON dbo.sysschedules       TO [bkp_readonly];
GRANT SELECT ON dbo.sysjobhistory      TO [bkp_readonly];   -- esito ultima esecuzione
GRANT SELECT ON dbo.sysjobactivity     TO [bkp_readonly];   -- job in esecuzione adesso
GRANT SELECT ON dbo.syssessions        TO [bkp_readonly];
GO

/* Verifica: colonne "_1" = 1, colonne "_0" = 0 */
EXECUTE AS LOGIN = N'bkp_readonly';
SELECT
    HAS_PERMS_BY_NAME('msdb.dbo.backupmediafamily', 'OBJECT', 'SELECT')  AS backupmediafamily_1,
    HAS_PERMS_BY_NAME('msdb.dbo.sysjobactivity',    'OBJECT', 'SELECT')  AS sysjobactivity_1,
    HAS_PERMS_BY_NAME('msdb.dbo.syssessions',       'OBJECT', 'SELECT')  AS syssessions_1,
    HAS_PERMS_BY_NAME('sys.dm_os_file_exists',      'OBJECT', 'SELECT')  AS file_exists_1,
    ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0)                              AS sysadmin_0,
    ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sp_start_job', 'OBJECT', 'EXECUTE'), 0) AS sp_start_job_0;
REVERT;
