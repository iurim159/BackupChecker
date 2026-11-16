# BackupChecker

Tool a riga di comando per Windows 11 che controlla i backup dei database SQL Server su più server
e produce un report Excel (`.xlsx`) con **un foglio per server** più un foglio di riepilogo.

## Come funziona

Il tool **non crea utenti e non modifica nulla sui server**: nel codice non c'è nessun comando di
scrittura (CREATE, ALTER, DROP, GRANT, INSERT, UPDATE, DELETE), solo SELECT.

1. **Su ogni server, a mano:** esegui in SSMS lo script di creazione, collegato come amministratore
   (es. d.munteanu), cambia la password ed eseguilo. Crea il login `bkp_readonly` e alla fine mostra una
   verifica dei suoi permessi.
2. **Import (una volta):** metti IP, `bkp_readonly` e password in `servers.csv` e lancia
   `BackupChecker importa servers.csv`. Per ogni server il tool si collega e **controlla che il login sia
   davvero in sola lettura**. Se trova ruoli amministrativi (sysadmin, securityadmin, ...), permessi come
   CONTROL SERVER / ALTER ANY LOGIN o permessi di scrittura su msdb, rifiuta quel server.
3. **Report (ogni volta che vuoi):** `BackupChecker report`. Prima di leggere ogni server ripete lo stesso
   controllo, poi genera il file Excel.

Le password sono salvate cifrate con Windows DPAPI nel file `credenziali-sola-lettura.dat` **accanto all'exe**
(il file si può leggere solo dallo stesso utente Windows sullo stesso PC). Se la cartella dell'exe non è scrivibile
si usa `%LOCALAPPDATA%\BackupChecker`. Dopo l'import puoi cancellare `servers.csv`.

## Permessi del login `bkp_readonly`

| Permesso | Perché serve |
|---|---|
| `VIEW ANY DATABASE` | elenco dei database (`sys.databases`) |
| `SELECT` su `msdb.dbo.backupset` | data/ora dell'ultimo backup FULL |
| `SELECT` su `msdb.dbo.sysjobs`, `sysjobsteps` | individuare il job che fa il backup di ogni database |
| `SELECT` su `msdb.dbo.sysjobschedules`, `sysschedules` | orario di avvio pianificato del job |
| `SELECT` su `msdb.dbo.sysjobhistory` | esito dell'ultima esecuzione del job |
| ruolo `db_denydatawriter` in `msdb` | divieto esplicito di scrittura |

Il tool rifiuta il login se trova privilegi amministrativi, permessi di scrittura o la possibilità di
avviare/fermare job (`sp_start_job`, `sp_stop_job`). I job falliti vengono solo segnalati, mai riavviati.

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

1. Crea `bkp_readonly` su ogni server (vedi sopra).
2. Copia `servers.example.csv` in `servers.csv` accanto all'exe e compilalo:

   ```
   server;utente;password;nome
   192.168.1.10;bkp_readonly;PasswordLettura1;Gestionale
   192.168.1.20\SQLEXPRESS;bkp_readonly;PasswordLettura2;Magazzino
   192.168.1.30,1444;bkp_readonly;PasswordLettura3;
   ```

   - `server`: IP o nome. Istanza nominata `IP\ISTANZA`, porta diversa `IP,porta`.
   - `nome` (facoltativo): nome del foglio Excel. Se vuoto si usa il nome SQL del server (`@@SERVERNAME`).
   - Se una password contiene `;` mettila tra virgolette: `"pa;ss"`.

3. Avvia `BackupChecker.exe` e scegli dal menu, oppure da riga di comando:

   ```
   BackupChecker importa servers.csv    verifica i login in sola lettura e li salva cifrati
   BackupChecker report                 genera il report Excel
   BackupChecker list                   elenca i server configurati
   BackupChecker rimuovi 192.168.1.10   toglie un server dall'elenco (sul server non tocca nulla)
   ```

Per aggiungere server o cambiare una password basta rieseguire `importa` con un CSV che contiene solo quelle righe.

### Opzioni del report

```
BackupChecker report --output D:\Report --ore-soglia 24
```

| Opzione | Default | Significato |
|---|---|---|
| `--output` | cartella `Report` accanto all'exe | cartella del file |
| `--ore-soglia` | 24 | oltre queste ore dall'ultimo backup FULL l'esito è `KO - NON RECENTE` |

Codici di uscita: `0` tutto ok, `1` errore, `2` alcuni server in errore. Così si può schedulare il report
con l'Utilità di pianificazione di Windows (`BackupChecker.exe report`). Il task va eseguito con lo
stesso utente Windows che ha fatto l'importazione, per via della cifratura DPAPI.

## Contenuto del report

Un foglio per server (nome dalla colonna `nome` del CSV), un database utente per riga
(master, model, msdb, tempdb e snapshot esclusi), colonne:

`Istanza | Database | Stato | Recovery | Data ultimo backup | Orario avvio | Orario fine | Esito`

- **Data ultimo backup / Orario fine:** dall'ultimo backup FULL registrato in msdb.
- **Orario avvio:** orario pianificato del job SQL Agent che esegue il backup del database (`N/D` se non c'è).
- **Esito** (nell'ordine):
  `KO - DATABASE <stato>` se non ONLINE · `KO - JOB FALLITO (da riavviare)` se l'ultima esecuzione del job
  pianificato è fallita o annullata · `KO - MAI BACKUPPATO` · `KO - NON RECENTE` oltre la soglia · `OK`.
  Se nessun job fa il backup del database si aggiunge `| NESSUN JOB`.
- Colori: verde OK, giallo OK senza job, rosso KO.

## Requisiti sui server

- SQL Server 2008 o successivo, con **autenticazione mista** (SQL Server e Windows) attiva
  (lo script SQL lo controlla e si ferma se manca).
- Porta SQL (di default 1433, o SQL Browser UDP 1434 per le istanze nominate) raggiungibile dal PC.

## Limiti noti

- Lo storico viene da `msdb`: i backup fatti con strumenti che non passano da SQL Server
  (ad es. snapshot VM senza integrazione VSS/SQL Writer) non risultano.
- Per i database in Availability Group i backup fatti su un'altra replica sono registrati nel `msdb`
  di quella replica: conviene includere nel CSV tutte le repliche.
- La connessione è cifrata con `TrustServerCertificate=true` (molti server usano certificati autofirmati).
  Server molto vecchi senza supporto TLS 1.2 potrebbero rifiutare la connessione da Windows 11.
