# Proceso: importación

Comando: `import --json <fichero> --schema <schema> --config <appsettings.json> [--state <state.json>] [opciones]`

Fases: validación del schema, calendario MPP, Calendar Template manual (buscar), proyecto (se localiza por `axz_mppguid` o se crea; inicio real), calendario propio del proyecto, WorkWeeks, excepciones y, según las opciones: tareas (`--tasks`), dependencias (`--dependencies`), miembros genéricos y asignaciones (`--resources`), esfuerzo (`--work`) y avance (`--progress`). Cada ejecución termina con un bloque `[Timing]` con la duración de cada fase.
Sin ninguna opción de fase, con `CalendarImport.StopAfterCalendar=true` (predeterminado) termina tras el calendario.

Exit codes: 0 ok, 1 error funcional, 2 configuración/schema, 3 conexión, 4 plantilla, 5 proyecto, 6 calendario del proyecto, 7 WorkWeeks, 8 excepciones, 9 tareas, 10 dependencias, 11 recursos y asignaciones.
Descripción completa del proceso, mapeo y limitaciones: [README.md](README.md).

**Las Calendar Templates son manuales.** El importador nunca crea, actualiza ni borra plantillas (`msdyn_workhourtemplate`). Resuelve la plantilla por configuración (`calendarImport.calendarTemplates`: fingerprint del horario base → nombre o Id) y exige que exista exactamente una. Plantillas iniciales: `MPP - Calendario 09-13 15-19` y `MPP - Calendario 08-17 V08-13:30` (ver `appsettings.json` / `appsettings.example.json`). Si no hay correspondencia o no existe, termina con exit 4 e imprime el fingerprint y la línea de configuración a añadir.

**Estado actual:** implementado y probado contra Dataverse: plantilla manual, proyecto (con GUID propio; el GUID del MPP se guarda en `axz_mppguid` y el nombre del fichero en `axz_mppfile`; inicio real en `msdyn_actualstart`), calendario del proyecto, excepciones, tareas (con notas), dependencias, miembros genéricos del equipo, asignaciones, esfuerzo y avance. **Pendiente:** WorkWeeks con vigencia (usa `--workweeks false` si el fichero los tiene), la conversión de la hora de inicio a UTC y las tareas hoja que superan el límite de duración de Project Operations (`E_LIMITEXCEEDED_LEAFTASKDURATION`; afecta, entre los ejemplos, al menos al fichero 10).

## 0. Validar antes el JSON

Ver [SCRIPTS-validacion.md](SCRIPTS-validacion.md). `import` ya valida el schema internamente y no conecta si falla.

## 1. Descubrir metadatos de calendarios (solo lectura, requiere login)

```powershell
$proj = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
dotnet run --project "$proj\src\ProjectOpsExcelImporter" -- calendar-metadata --config "$proj\appsettings.json" --out "$proj\calendar-metadata.json"
```

Con el resultado se rellena `calendarImport.dataverseMetadata` en `appsettings.json` (ya rellenado con los valores confirmados).

## 2. Inspeccionar una plantilla existente (solo lectura, requiere login)

Vuelca una `msdyn_workhourtemplate`, su `calendar` y sus `calendarrule`:

```powershell
$proj = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
dotnet run --project "$proj\src\ProjectOpsExcelImporter" -- calendar-dump --config "$proj\appsettings.json" --template-id "<guid-plantilla>" --out "$proj\calendar-dump.json"
```

## 3. Ejecutar la importación de un fichero

```powershell
$proj = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
dotnet run --project "$proj\src\ProjectOpsExcelImporter" -- import `
  --json "C:\AXPROYECTOS\OESIA_ProjectEjemplos\JSON\13_project-dump.json" `
  --schema "$proj\project-dump.schema.json" `
  --config "$proj\appsettings.json" `
  --state "$proj\import-state-13.json"
$LASTEXITCODE
```

- Es reanudable: relanzar con el mismo `--state` no duplica proyecto, WorkWeeks ni excepciones.
- Si el JSON cambia, el `SourceHash` del state no coincide y el import se rechaza.

## 4. Importar un proyecto completo con log

Crea el proyecto con su plantilla, aplica las excepciones, crea las tareas por orden de `Id` (con sus notas), las dependencias, los miembros genéricos del equipo, las asignaciones, el esfuerzo y el avance. Guarda un log con la hora de cada línea y **una línea por cada llamada a Dataverse** (`--trace true`); al final del log aparece el bloque `[Timing]` con la duración de cada fase.

```powershell
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$proj = "C:\AXPROYECTOS\OESIA_ProjectMigrator\projectdesktop-crm-migrator\ProjectOpsExcelImporter\ProjectOpsExcelImporter"
$json = "C:\AXPROYECTOS\OESIA_ProjectEjemplos\_analisis\202610 Planificación I-0010.1.1.1 (Avance hasta 31-10-2026)\test-project-dump.json"
$logDir = Join-Path $proj "logs"
$log    = Join-Path $logDir ("import-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
$state  = Join-Path $proj "import-state-test.json"
New-Item -ItemType Directory -Force $logDir | Out-Null

Get-Process ProjectOpsExcelImporter -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build "$proj\src\ProjectOpsExcelImporter" -c Debug -v q
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación" }
$dll = "$proj\src\ProjectOpsExcelImporter\bin\Debug\net8.0\ProjectOpsExcelImporter.dll"

# El proyecto se localiza en Dataverse por axz_mppguid, así que el state solo sirve para la ejecución en curso: se empieza limpio.
Remove-Item $state -ErrorAction SilentlyContinue

dotnet $dll import --json $json --schema "$proj\project-dump.schema.json" --config "$proj\appsettings.json" `
  --state $state --tasks true --dependencies true --progress true --workweeks false --trace true 2>&1 |
  ForEach-Object { "[{0:HH:mm:ss}] {1}" -f (Get-Date), $_ } | Tee-Object -FilePath $log
"EXIT=$LASTEXITCODE   Log: $log"
```

- `--tasks true`: crea las tareas tras las excepciones y termina. Con `--max-tasks N` o `--task-ids 12,45` se limita a unas pocas (más sus ancestros).
- `--dependencies true`: tras las tareas crea las dependencias entre ellas (tipo y retardo) y termina. Las que enlazan con tareas **resumen** se omiten, con aviso, salvo `--summary-dependencies true`. Con `--max-dependencies N` se limita a las N primeras.
- `--resources true`: tras las tareas (y las dependencias, si se piden) crea **un miembro genérico del equipo del proyecto por cada recurso Work que tiene alguna asignación** y sus asignaciones (proyecto, tarea y miembro: sin horas, unidades, tarifas ni costes). **No crea ningún recurso reservable, rol ni calendario**. Los recursos Material y las asignaciones sin recurso se omiten con aviso. Con `--task-ids` solo se procesan los recursos usados en esas tareas.
  - Modelo: recurso Work del MPP → miembro genérico (`msdyn_projectteam`) → asignación → tarea. Todos los miembros apuntan al recurso reservable genérico **común** de Project Operations; el nombre del recurso del MPP es el **Nombre de puesto** (`msdyn_name`).
  - Configuración (`calendarImport`): `genericBookableResourceId` (recurso genérico existente), `defaultResourcingUnitId` (unidad de dotación), `defaultResourceRoleId` (rol de los recursos sin mapping) y `resourceRoleMappings` (opcional: `mppResourceGuid` / `mppResourceUniqueId` / `mppResourceName` → `projectOperationsRoleId`). Orden de resolución del rol: GUID, UniqueId, nombre exacto y único, rol predeterminado. Los GUID se validan al empezar (deben existir y estar activos); sin rol resoluble el recurso y sus asignaciones no se crean y la fase termina con exit 11.
  - Identidad: el miembro tiene un GUID derivado del proyecto importado y del GUID del recurso del MPP (o de su UniqueId si no tiene GUID); se conserva en el state (`resources`). Reanudar reutiliza el miembro y las asignaciones existentes. Si ya existe un miembro con ese id que apunta a otro recurso reservable, la fase se detiene sin modificarlo.
  - El log incluye `[Team member]`, `[Assignment]`, `[Task planning]` (Start/Finish/Duration/Effort antes y después, sin corregir) y `[Role report]` (roles compartidos, recursos con rol predeterminado y tarifas del MPP distintas bajo el mismo rol; las tarifas solo se leen para este informe).
- `--work true` (implica `--resources true`): tras crear las asignaciones fija el **esfuerzo de cada tarea** (`msdyn_effort`) con la suma del `Work` de sus asignaciones en el MPP, mediante `msdyn_PssUpdateV2`. Es idempotente (no reescribe si ya coincide) y omite, con aviso, las tareas con alguna asignación sin `Work` en horas. Con el proyecto en modo **Duración fija** la duración y las fechas no cambian (verificado en las tareas 532 y 533). **No** envía trabajo completado ni restante: la API rechaza actualizar asignaciones y `msdyn_effortcompleted`; el avance se importa con `--progress`. Comando de prueba aislado: `assignment-work-test --json … --config … --task-ids 532,533 --level task --fields effort [--execute true]`.
- `--progress true` (implica `--work true`): tras fijar el esfuerzo envía el **avance** (`PercentComplete` del MPP) a las tareas hoja con avance mayor que 0, con `msdyn_progress` y `msdyn_PssUpdateV2`. La API recibe una **fracción** (1 = 100 %; 100 se rechaza) y la columna la devuelve como porcentaje. El trabajo completado y el restante **no se envían**: los deriva la plataforma (verificado en las tareas 532 y 533: completado 1 h, restante 0 h). Requiere `msdyn_allowpercentcompleteupdate = true` en el proyecto: el importador lo activa (un proyecto nuevo nace con false) antes de enviar el avance y no lo revierte. Omite los hitos y las tareas con esfuerzo 0 (la plataforma rechaza el avance de una tarea sin esfuerzo y tumba el lote) y es idempotente. El log incluye `[Task progress]`: avance y trabajo real del MPP frente a completado/restante en Project Operations, con aviso si no coinciden.
- `--workweeks false`: omite los WorkWeeks, que aún no se soportan. Sin esa opción, un fichero con WorkWeeks termina con exit 7.
- `--trace true`: una línea por llamada HTTP (hora, método, destino, estado y milisegundos). Quítalo si solo quieres el resumen.
- Reimportar tras borrar el proyecto funciona sin cambiar nada: el proyecto borrado ya no aparece por `axz_mppguid` y se crea uno nuevo con otro GUID.
- Códigos de salida: 0 correcto, 5 proyecto, 6 calendario del proyecto, 7 WorkWeeks, 8 excepciones, 9 tareas, 10 dependencias, 11 recursos y asignaciones.
- Si borras el proyecto en Dataverse, borra también su state (`$state`, como hace el script): un state que apunta a un proyecto inexistente termina con exit 5.
- Una tarea hoja con duración superior al límite de Project Operations hace fallar el lote de tareas con `E_LIMITEXCEEDED_LEAFTASKDURATION` (exit 9); el error indica la posición de la tarea en el lote, no su nombre.

## 5. Importar varios ficheros a la vez

[import-batch-parallel.ps1](import-batch-parallel.ps1) importa todos los `*_project-dump.json` de una carpeta con `--tasks --dependencies --progress`, varios a la vez. Requiere **PowerShell 7**.

```powershell
.\import-batch-parallel.ps1                     # 3 a la vez, todos los ficheros
.\import-batch-parallel.ps1 -Parallel 2         # 2 a la vez
.\import-batch-parallel.ps1 -Only 3,4,6         # solo esos ficheros (por su número)
.\import-batch-parallel.ps1 -NoBuild            # sin compilar
.\import-batch-parallel.ps1 -KeepState          # no borrar el state de cada fichero
```

- Lanza primero los ficheros más grandes. Genera en `logs\batch-<fecha>\` un log por fichero, `resumen.csv` (estado, segundos y tamaño de cada proyecto) y `fases.csv` (segundos por fase).
- Cada fichero es un proceso aparte con **su propio inicio de sesión** (la caché del token es solo en memoria): hay que estar delante. Con 3 a la vez se abren 3 ventanas al empezar y otra por cada fichero que entra después.
- Por defecto borra el state de cada fichero antes de importarlo: el proyecto se localiza por `axz_mppguid` y un state de un proyecto borrado detendría la importación (exit 5).
- Para estimar el tiempo de una carga completa usa **"Duración real del lote"**; la suma de tiempos por fichero no es la duración del lote.
- Una alternativa sin PowerShell 7: lanzar el mismo script en varias terminales con `-Only` distinto (la primera compila; las demás con `-NoBuild`), sin repetir un fichero en dos terminales.
