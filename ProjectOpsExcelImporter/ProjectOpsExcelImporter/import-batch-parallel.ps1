#requires -Version 7
<#
.SYNOPSIS
  Importa a Project Operations todos los ficheros *_project-dump.json de una carpeta, varios a la vez.

.EXAMPLE
  .\import-batch-parallel.ps1                         # 3 a la vez, todos los ficheros
  .\import-batch-parallel.ps1 -Parallel 2             # 2 a la vez
  .\import-batch-parallel.ps1 -Only 3,4,6             # solo esos ficheros (por su número)
  .\import-batch-parallel.ps1 -NoBuild                # sin compilar (si ya está compilado)
  .\import-batch-parallel.ps1 -KeepState              # no borra el state de cada fichero (ver NOTES)

.NOTES
  - Cada fichero se importa en su propio proceso: abre su propia ventana de inicio de sesión (elegir cuenta).
    Al arrancar se abren tantas como ficheros en paralelo; los siguientes piden otra al entrar en un hueco libre. Hay que estar delante.
  - Un fichero se puede relanzar: el proyecto ya importado se reutiliza (se localiza por axz_mppguid) y no se duplica nada.
  - Para estimar producción usa "Duración real del lote", no la suma de tiempos por fichero.
  - Los ficheros más grandes se lanzan primero para que el más largo no empiece el último.
#>
param(
    [int]$Parallel = 3,
    [string[]]$Only = @(),
    [switch]$NoBuild,
    [switch]$KeepState,      # por defecto se borra el state de cada fichero antes de importarlo (ver notas)
    [string]$JsonDir = "C:\AXPROYECTOS\OESIA_ProjectEjemplos\JSON"
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$proj     = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
$stamp    = Get-Date -Format "yyyyMMdd-HHmmss"
$logDir   = Join-Path $proj "logs\batch-$stamp"
$stateDir = Join-Path $proj "state-batch"
New-Item -ItemType Directory -Force $logDir, $stateDir | Out-Null

# Opciones de importación (ver SCRIPTS-importacion.md). --progress true implica --work true, --resources true y --tasks true.
$options = @("--tasks","true","--dependencies","true","--progress","true","--workweeks","false","--trace","true")

if (-not $NoBuild) {
    dotnet build "$proj\src\ProjectOpsExcelImporter" -c Debug -v q
    if ($LASTEXITCODE -ne 0) { throw "Falló la compilación (¿hay otra importación en marcha?)" }
}
$dll = "$proj\src\ProjectOpsExcelImporter\bin\Debug\net8.0\ProjectOpsExcelImporter.dll"
if (-not (Test-Path $dll)) { throw "No existe $dll; lanza el script sin -NoBuild." }

$files = Get-ChildItem $JsonDir -Filter "*_project-dump.json" | Sort-Object { [int](($_.Name -split '_')[0]) }
if ($Only.Count) { $files = $files | Where-Object { ($_.Name -split '_')[0] -in $Only } }
if (-not $files) { throw "No hay ficheros *_project-dump.json que importar en $JsonDir (filtro: $($Only -join ','))." }
$files = $files | Sort-Object Length -Descending

"{0} fichero(s), {1} a la vez. Logs: {2}" -f @($files).Count, $Parallel, $logDir
$batchClock = [Diagnostics.Stopwatch]::StartNew()

$summary = $files | ForEach-Object -ThrottleLimit $Parallel -Parallel {
    $f     = $_
    $name  = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    $log   = Join-Path $using:logDir "$name.log"
    $state = Join-Path $using:stateDir "$name.state.json"
    # El proyecto ya importado se localiza en Dataverse por axz_mppguid, así que el state no hace falta para reanudar. Un state antiguo que
    # apunta a un proyecto borrado detiene la importación (exit 5), por eso se borra antes de empezar (salvo -KeepState).
    if (-not $using:KeepState) { Remove-Item $state -ErrorAction SilentlyContinue }
    $opts  = $using:options          # el splatting directo con $using: no es fiable: se pasa por variable local
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ("[{0:HH:mm:ss}] INICIO {1}" -f (Get-Date), $f.Name) -ForegroundColor Cyan

    dotnet $using:dll import --json $f.FullName --schema "$using:proj\project-dump.schema.json" --config "$using:proj\appsettings.json" `
        --state $state @opts 2>&1 |
        ForEach-Object { "[{0:HH:mm:ss}] {1}" -f (Get-Date), $_ } | Out-File $log -Encoding utf8
    $exit = $LASTEXITCODE
    $clock.Stop()
    # Duración medida por el script (por si el importador no llegó a imprimir [Timing]).
    "[{0:HH:mm:ss}] SCRIPT TOTAL: {1:N1} s (exit {2})" -f (Get-Date), $clock.Elapsed.TotalSeconds, $exit | Add-Content $log
    Write-Host ("[{0:HH:mm:ss}] FIN    {1}  exit={2}  {3:N0} s" -f (Get-Date), $f.Name, $exit, $clock.Elapsed.TotalSeconds) -ForegroundColor $(if ($exit -eq 0) { 'Green' } else { 'Red' })

    $text = Get-Content $log -Raw
    [pscustomobject]@{
        Fichero  = $f.Name
        Exit     = $exit
        Estado   = $(if ($exit -eq 0) { "OK" } else { "ERROR" })
        Segundos = [math]::Round($clock.Elapsed.TotalSeconds, 1)
        Error    = $(if ($exit -ne 0) { (([regex]::Match($text, 'ERROR \[[^\]]*\]: ([^\r\n]*)').Groups[1].Value) -replace '\s+',' ') } else { "" })
    }
}
$batchClock.Stop()

# Tamaño de cada proyecto y tiempo por fase, leídos del bloque [Timing] de cada log.
$phases = @()
$rows = foreach ($s in ($summary | Sort-Object { [int](($_.Fichero -split '_')[0]) })) {
    $name   = [IO.Path]::GetFileNameWithoutExtension($s.Fichero)
    $text   = Get-Content (Join-Path $logDir "$name.log") -Raw
    $timing = if ($text -match '\[Timing\]') { ($text -split '\[Timing\]')[-1] } else { "" }
    foreach ($m in [regex]::Matches($timing, '(?m)^\[[\d:]+\]\s{2,}(.+?)\s*:\s+([\d.]+) s(?:\s|$)')) {
        $phases += [pscustomobject]@{ Fichero = $s.Fichero; Fase = $m.Groups[1].Value.Trim(); Segundos = [double]::Parse($m.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture) }
    }
    $size = [regex]::Match($timing, 'Size\s*:\s*tasks (\d+) \(leaf (\d+)\), dependencies (\d+), Work resources (\d+), assignments (\d+), calendar exceptions (\d+)')
    $s | Add-Member Tareas $size.Groups[1].Value -PassThru | Add-Member Dependencias $size.Groups[3].Value -PassThru | Add-Member Asignaciones $size.Groups[5].Value -PassThru
}

"`n===== RESUMEN (paralelismo $Parallel) ====="
$rows | Format-Table Fichero, Exit, Estado, Segundos, Tareas, Dependencias, Asignaciones, Error -AutoSize -Wrap
$rows   | Export-Csv (Join-Path $logDir "resumen.csv") -NoTypeInformation -Encoding UTF8
$phases | Export-Csv (Join-Path $logDir "fases.csv")   -NoTypeInformation -Encoding UTF8

$ok = @($rows | Where-Object { $_.Estado -eq "OK" })
"Correctos: {0} de {1}" -f $ok.Count, @($rows).Count
"Suma de tiempos por fichero : {0:hh\:mm\:ss}" -f [TimeSpan]::FromSeconds(($rows | Measure-Object Segundos -Sum).Sum)
"Duración real del lote       : {0:hh\:mm\:ss}   <- cifra útil para estimar" -f $batchClock.Elapsed
"Logs y CSV: $logDir"
