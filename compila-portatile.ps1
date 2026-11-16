# Compila BackupChecker.exe SENZA installare nulla sul PC.
# Scarica temporaneamente il .NET 8 SDK in una sottocartella, compila e poi cancella tutto.
# Risultato: cartella "BackupChecker-portatile" con un unico BackupChecker.exe.
#
# Uso: tasto destro sul file -> "Esegui con PowerShell"
#      oppure:  powershell -ExecutionPolicy Bypass -File .\compila-portatile.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tmp  = Join-Path $root '.compilazione-temp'
$out  = Join-Path $root 'BackupChecker-portatile'

# Tutto ciò che .NET/NuGet scriverebbe nel profilo utente va nella cartella temporanea
$env:DOTNET_CLI_HOME                   = $tmp
$env:NUGET_PACKAGES                    = Join-Path $tmp 'nuget'
$env:NUGET_HTTP_CACHE_PATH             = Join-Path $tmp 'nuget-http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO                     = '1'
$env:DOTNET_MULTILEVEL_LOOKUP          = '0'

try {
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $sdk = Join-Path $tmp 'sdk'

    Write-Host 'Scarico il .NET 8 SDK temporaneo...' -ForegroundColor Cyan
    $installer = Join-Path $tmp 'dotnet-install.ps1'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Channel 8.0 -InstallDir $sdk -NoPath

    Write-Host 'Compilo...' -ForegroundColor Cyan
    $dotnet = Join-Path $sdk 'dotnet.exe'
    & $dotnet publish (Join-Path $root 'src\BackupChecker.csproj') -c Release -o $out --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione fallita.' }
    & $dotnet build-server shutdown | Out-Null

    # Nella cartella finale tieni solo l'exe e l'esempio CSV
    Get-ChildItem $out | Where-Object { $_.Name -notin @('BackupChecker.exe', 'servers.example.csv') } | Remove-Item -Recurse -Force

    Write-Host ''
    Write-Host "Fatto: $out\BackupChecker.exe" -ForegroundColor Green
    Write-Host 'Puoi copiare la cartella dove vuoi: non serve installare nulla.'
}
finally {
    Write-Host 'Pulizia dei file temporanei...' -ForegroundColor Cyan
    Start-Sleep -Seconds 1
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $root 'src\bin'), (Join-Path $root 'src\obj') -ErrorAction SilentlyContinue
}
Read-Host 'Premi Invio per chiudere'
