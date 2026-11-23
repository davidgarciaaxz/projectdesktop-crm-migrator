# Proceso: validación de JSON contra JSON Schema

Comando: `validate-schema "<json>" "<schema>" ["<log>"]`
Exit codes: 0 = válido, 1 = error (fichero/parsing/schema), 2 = no cumple el schema.
No se conecta a Dataverse.

## Validar todos los JSON de una carpeta

```powershell
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$proj   = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
$dir    = "C:\AXPROYECTOS\OESIA_ProjectEjemplos\JSON"
$schema = Join-Path $proj "project-dump.schema.json"
$log    = Join-Path $dir  "validation.log"

Get-Process ProjectOpsExcelImporter -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build "$proj\src\ProjectOpsExcelImporter" -c Debug -v q
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación" }
$dll = "$proj\src\ProjectOpsExcelImporter\bin\Debug\net8.0\ProjectOpsExcelImporter.dll"

Remove-Item $log -ErrorAction SilentlyContinue
$results = foreach ($f in Get-ChildItem $dir -Filter *.json | Where-Object { $_.Name -notlike "*.schema.json" }) {
    Write-Host "=== $($f.Name)" -ForegroundColor Cyan
    dotnet $dll validate-schema $f.FullName $schema $log | Out-Host
    [pscustomobject]@{ Fichero = $f.Name; ExitCode = $LASTEXITCODE }
}
$results | Format-Table -AutoSize
"Válidos: $(($results | ? ExitCode -eq 0).Count) / $($results.Count)   Log: $log"
```

Notas:
- `Stop-Process` cierra cualquier instancia de la herramienta: no lo lances con una importación en curso.
- Se compila en Debug porque Release falla si hay procesos bloqueando el `.exe`.
- El log se borra al empezar cada ejecución.

## Resumen del log por fichero

```powershell
Select-String -Path "C:\AXPROYECTOS\OESIA_ProjectEjemplos\JSON\validation.log" -Pattern "VÁLIDO|INVÁLIDO|ERROR \(" |
  ForEach-Object { ($_.Line -split ' \| ')[0] }
```

Formato de cada entrada del log:

```
[fecha] VÁLIDO: fichero.json | JSON=... SCHEMA=... EXIT=0
[fecha] INVÁLIDO (no cumple el schema): fichero.json | ... EXIT=2   (+ detalle: ruta, motivo, valor, schema)
[fecha] ERROR (no se pudo validar): fichero.json | ... EXIT=1
```

## Validar un solo fichero

```powershell
$proj = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
dotnet run --project "$proj\src\ProjectOpsExcelImporter" -- validate-schema "C:\ruta\fichero.json" "$proj\project-dump.schema.json" "C:\ruta\validation.log"
```
