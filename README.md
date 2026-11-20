# BackupChecker

Tool a riga di comando per Windows 11 che controlla i backup dei database SQL Server su più server
e produce un report Excel (`.xlsx`) con **un foglio per server**. Gira sul PC Windows (server ponte); i server SQL
possono essere Windows o Linux. L'esito è OK solo se il job non è fallito né in corso, il backup ha meno di 24 ore
e il **file `.bak` è ancora al suo posto**.

## Come funziona

Il tool **non crea utenti e non modifica nulla sui server**: esegue solo SELECT con il login SQL
`bkp_readonly`, che crei tu a mano su ogni server. Non servono utenti sulle macchine (Windows o Linux):
anche il controllo dei file `.bak` lo fa SQL Server stesso.

1. **Su ogni server, a mano (SSMS, come amministratore):** crea `bkp_readonly` ed esegui
   `scripts\permessi_sql_bkp_readonly.sql` (solo permessi di lettura; mostra una verifica finale).
2. **Import (una volta):** metti IP, `bkp_readonly` e password in `servers.csv` e lancia
   `BackupChecker importa servers.csv`. Il tool controlla che il login sia davvero in sola lettura
   (nessun ruolo amministrativo, nessun permesso di scrittura o di avvio job); altrimenti rifiuta il server.
3. **Report (ogni mattina):** `BackupChecker report`. Ripete il controllo, genera l'Excel e alla fine
   scrive a schermo **ESITO: OK** oppure **ESITO: KO** con l'elenco dei database da guardare.

Le password sono salvate cifrate con Windows DPAPI nel file `credenziali-sola-lettura.dat` **accanto all'exe**
(il file si può leggere solo dallo stesso utente Windows sullo stesso PC). Dopo l'import puoi cancellare `servers.csv`.

## Controlli eseguiti

| # | Controllo | Da dove | Se va male |
|---|---|---|---|
| 1 | Database ONLINE | `sys.databases` | `KO - DATABASE <stato>` |
| 2 | Job di backup in esecuzione adesso | `msdb.dbo.sysjobactivity` | `KO - BACKUP IN CORSO` (ricontrollare più tardi) |
| 3 | Ultima esecuzione del job fallita/annullata | `msdb.dbo.sysjobhistory` | `KO - JOB FALLITO (da riavviare)` |
| 4 | Esiste un backup FULL | `msdb.dbo.backupset` | `KO - MAI BACKUPPATO` |
| 5 | Ultimo FULL più recente della soglia (24 h) | `msdb.dbo.backupset` | `KO - NON RECENTE` |
| 6 | Il file `.bak` dell'ultimo FULL esiste ancora | `sys.dm_os_file_exists` | `KO - FILE BACKUP NON TROVATO` |
| 7 | Il file ha la dimensione attesa (≥ 95%) | `sys.dm_os_enumerate_filesystem` | `KO - FILE BACKUP INCOMPLETO` |

Se nessun job fa il backup del database si aggiunge `| NESSUN JOB`. I job falliti vengono solo segnalati, mai riavviati.

L'integrità del file (che sia ripristinabile) la verifica il job stesso con `RESTORE VERIFYONLY` subito dopo
il backup: se fallisce, il job fallisce e il tool lo segnala al controllo 3.

## Permessi del login `bkp_readonly`

| Permesso | Perché serve |
|---|---|
| `VIEW ANY DATABASE` | elenco dei database |
| `SELECT` su `msdb.dbo.backupset`, `backupmediafamily` | ultimo backup FULL, dimensione e percorso del file |
| `SELECT` su `msdb.dbo.sysjobs`, `sysjobsteps` | individuare il job che fa il backup di ogni database |
| `SELECT` su `msdb.dbo.sysjobschedules`, `sysschedules` | orario di avvio pianificato |
| `SELECT` su `msdb.dbo.sysjobhistory` | esito dell'ultima esecuzione |
| `SELECT` su `msdb.dbo.sysjobactivity`, `syssessions` | job in esecuzione in questo momento |
| ruolo `db_denydatawriter` in `msdb` | divieto esplicito di scrittura |

`sys.dm_os_file_exists` e `sys.dm_os_enumerate_filesystem` sono funzioni di sola lettura (SQL Server 2017+),
utilizzabili di default da tutti i login: dicono solo se un file esiste e quanto è grande.

## Compilazione

Il risultato è **un solo `BackupChecker.exe`** che contiene già .NET e tutte le librerie: si copia su
qualsiasi Windows 10/11 x64 e funziona senza installare nulla, senza estrarre file in `%TEMP%`.

Per compilarlo ci sono due modi:

- **Senza installare nulla (consigliato):** tasto destro su `compila-portatile.ps1` → *Esegui con PowerShell*.
  Scarica il .NET SDK in una cartella temporanea, compila, e poi cancella tutto.
  Risultato: `BackupChecker-portatile\BackupChecker.exe`.
- **Con il .NET 8 SDK installato:** doppio clic su `build.cmd`. Risultato: `publish\BackupChecker.exe`.

## Modalità portatile

Tutto resta nella cartella dell'exe:

```
BackupChecker-portatile\
├── BackupChecker.exe
├── credenziali-sola-lettura.dat   (creato da "importa", cifrato)
└── Report\                        (file Excel generati)
```

Per eliminare il tool basta cancellare la cartella.

## Uso

1. Crea `bkp_readonly` su ogni server ed esegui `scripts\permessi_sql_bkp_readonly.sql`.
2. Compila `servers.csv` accanto all'exe:

   ```
   server;utente;password;nome
   192.168.1.10;bkp_readonly;PasswordLettura1;Gestionale
   192.168.1.20\SQLEXPRESS;bkp_readonly;PasswordLettura2;Magazzino
   ```

   - `server`: IP o nome. Istanza nominata `IP\ISTANZA`, porta diversa `IP,porta`.
   - `nome` (facoltativo): nome del foglio Excel.
   - Se una password contiene `;` mettila tra virgolette: `"pa;ss"`.

3. Comandi:

   ```
   BackupChecker importa servers.csv    verifica i login in sola lettura e li salva cifrati
   BackupChecker report                 genera il report Excel e scrive ESITO: OK / KO
   BackupChecker list                   elenca i server configurati
   BackupChecker rimuovi 192.168.1.10   toglie un server dall'elenco (sul server non tocca nulla)
   ```

### Opzioni del report

```
BackupChecker report --output D:\Report --ore-soglia 24
```

| Opzione | Default | Significato |
|---|---|---|
| `--output` | cartella `Report` accanto all'exe | cartella del file |
| `--ore-soglia` | 24 | oltre queste ore dall'ultimo backup FULL l'esito è `KO - NON RECENTE` |
| `--senza-file` | | non controlla i file `.bak` (solo msdb e job) |

Codici di uscita: `0` tutto ok, `1` errore, `2` alcuni server in errore. Così si può schedulare il report
con l'Utilità di pianificazione di Windows (`BackupChecker.exe report`). Il task va eseguito con lo
stesso utente Windows che ha fatto l'importazione, per via della cifratura DPAPI.

## Contenuto del report

Un foglio per server (nome dalla colonna `nome` del CSV), un database utente per riga
(master, model, msdb, tempdb e snapshot esclusi), colonne:

`Istanza | Database | Stato | Recovery | Data ultimo backup | Orario avvio | Orario fine | File backup | Dimensione file (MB) | Verifica file | Esito`

- **Data ultimo backup / Orario fine:** dall'ultimo backup FULL registrato in msdb.
- **Orario avvio:** orario pianificato del job che esegue il backup del database (`N/D` se non c'è).
- **File backup / Dimensione / Verifica file:** percorso del `.bak`, dimensione letta dal disco ed esito del controllo.
- **Esito:** vedi la tabella dei controlli. Colori: verde OK, giallo OK senza job, rosso KO.

## Requisiti sui server

- SQL Server con **autenticazione mista** attiva. Per il controllo dei file serve SQL Server **2017 o successivo**
  (Windows o Linux); sulle versioni precedenti l'esito è `KO - FILE NON VERIFICABILE` (oppure usa `--senza-file`).
- Dal PC del tool deve essere raggiungibile la porta SQL (1433 o quella dell'istanza).

## Limiti noti

- Viene controllato il file dell'**ultimo backup FULL**; DIFF e LOG no.
- Backup su URL/cloud o fatti da software esterni (dispositivo virtuale) non hanno un file da controllare:
  l'esito è `KO - FILE NON VERIFICABILE` con il motivo.
- Se un amministratore toglie a `public` il permesso su `sys.dm_os_file_exists`, va concesso a `bkp_readonly`.
- La connessione SQL è cifrata con `TrustServerCertificate=true` (molti server usano certificati autofirmati).
