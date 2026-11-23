# ProjectOpsExcelImporter

Aplicación de consola **.NET 8 / C#** que importa un proyecto de **Microsoft Project (`.mpp`)** a **Dynamics 365 Project Operations Core**, usando la Dataverse Web API y las **Project Schedule APIs**.

> El nombre del proyecto es histórico: la primera versión leía un Excel. **El proceso actual parte del `project-dump.json`** que genera [MPPReader](../../MPPReader/README.md) (`MppReaderTest`, con MPXJ.Net). El flujo antiguo (Excel/plan) se describe en la sección 12 y no forma parte del proceso.

## 1. Qué importa y qué no

| Se importa | Cómo |
|---|---|
| Proyecto | Nombre, GUID del MPP (`axz_mppguid`), nombre del fichero (`axz_mppfile`), inicio programado, **inicio real** (`msdyn_actualstart` = "Comienzo" de la primera fila del MPP) |
| Calendario | Calendar Template **manual** elegida por la huella del horario base; calendario propio del proyecto; **excepciones** (días no laborables y laborables, recurrentes expandidas) con `msdyn_SaveCalendar` |
| Tareas | Jerarquía, orden por `Id` del MPP, inicio, duración, hitos, **notas** (texto plano y HTML) |
| Dependencias | FC / CC / FF / CF con retardo (en segundos) |
| Recursos | Un **miembro genérico del equipo por cada recurso Work usado**, con su rol y unidad de dotación |
| Asignaciones | Proyecto + tarea + miembro del equipo |
| Esfuerzo | `msdyn_effort` de cada tarea = suma del `Work` de sus asignaciones en el MPP |
| Avance | `PercentComplete` de las tareas hoja; Project Operations deriva el trabajo completado y el restante |

| No se importa (todavía) | Motivo |
|---|---|
| **WorkWeeks** (vigencias del calendario) | Sin implementar; los ficheros que los tienen deben usar `--workweeks false` |
| Recursos **Material** y asignaciones **sin recurso** | Se omiten con aviso en el log |
| Costes, tarifas, listas de precios, costes reales | Fuera de alcance |
| Calendarios de recurso | Se usa el recurso genérico común |
| Trabajo completado / restante directo | La API de planificación no lo admite; se deriva del avance |
| Líneas base, campos personalizados, datos timephased | Fuera de alcance |

## 2. Flujo

```text
project-dump.json  (MPXJ)
   │
   ├─ [1/8] Validación contra project-dump.schema.json            (antes de conectar)
   ├─ [2/8] Calendario base del MPP, WorkWeeks y excepciones       (sin conexión)
   ├─ [3/8] Calendar Template manual (se busca, nunca se crea)
   ├─ [4/8] Proyecto: se localiza por axz_mppguid o se crea (msdyn_CreateProjectV1)
   │        + msdyn_actualstart  (+ msdyn_allowpercentcompleteupdate = true con --progress)
   ├─ [5/8] Calendario del proyecto
   ├─ [6/8] WorkWeeks                       (no soportado: --workweeks false)
   ├─ [7/8] Excepciones del calendario      (msdyn_SaveCalendar)
   ├─ [Tasks]         tareas por lotes de 100: CreateOperationSet + PssCreateV2 + ExecuteOperationSet
   ├─ [Dependencies]  dependencias por lotes
   ├─ [Resources]     miembros genéricos (msdyn_CreateTeamMemberV1) → asignaciones (PSS)
   │                  → esfuerzo por tarea (PssUpdateV2) → avance (PssUpdateV2)
   ├─ [Timing]        duración de cada fase y tamaño del proyecto
   └─ [8/8] Resultado
```

Tareas, dependencias y asignaciones son entidades del motor de planificación: se crean con las Schedule APIs, nunca con un `POST` directo.

La importación **termina en la última fase activada** (`--tasks`, `--dependencies`, `--resources`/`--work`/`--progress`); sin ninguna se detiene tras las excepciones del calendario.

## 3. Requisitos

1. .NET SDK 8 (PowerShell 7 si se usa el script paralelo).
2. Entorno con Project Operations Core.
3. Usuario nominativo con licencia de Microsoft Project y permisos en Project Operations. **No** sirven Application Users, System Users ni Integration Users para las Schedule APIs.
4. App Registration de cliente público con permiso delegado **Dynamics CRM / user_impersonation** y Redirect URI `http://localhost` (o `DeviceCode`). La aplicación **no usa client secret**.
5. Preparado en Dataverse **antes** de importar (el importador **no crea metadatos**):
   - **Calendar Templates** (`msdyn_workhourtemplate`) creadas a mano, una por horario base distinto, y su correspondencia en `calendarImport.calendarTemplates` (huella → nombre o Id). Si falta, el importador termina con exit 4 e imprime la huella y la línea de configuración a añadir.
   - Campos de texto en `msdyn_project`: `axz_mppguid` (≥ 36 caracteres) y `axz_mppfile` (100 caracteres).
   - El **recurso reservable genérico** existente (`genericBookableResourceId`), la **unidad de dotación** (`defaultResourcingUnitId`) y los **roles** (`defaultResourceRoleId`, `resourceRoleMappings`). Se validan al empezar la fase de recursos: deben existir y estar activos.

## 4. Compilar y probar

```powershell
cd ProjectOpsExcelImporter
dotnet restore
dotnet build -c Debug
dotnet test tests/ProjectOpsExcelImporter.Tests      # 135 tests xUnit con servicios simulados (sin Dataverse)
dotnet run --project src/ProjectOpsExcelImporter -- self-test
```

No compiles mientras haya una importación en marcha: el ejecutable queda bloqueado.

## 5. Configurar

```powershell
Copy-Item appsettings.example.json appsettings.json
```

`appsettings.json` está excluido de Git y no debe contener secretos. Claves que usa el proceso:

| Clave | Para qué |
|---|---|
| `tenantId`, `clientId`, `environmentUrl`, `apiVersion`, `authMode`, `redirectUri` | Autenticación delegada y entorno |
| `hoursPerDay`, `daysPerWeek`, `daysPerMonth` | Conversión de duraciones y retardos |
| `calendarImport.calendarTemplates` | Huella del horario base → Calendar Template manual |
| `calendarImport.dataverseMetadata` | Nombres lógicos confirmados (`axz_mppguid`, `axz_mppfile`, calendarios, plantillas) |
| `calendarImport.genericBookableResourceId` | Recurso genérico común al que apuntan todos los miembros |
| `calendarImport.defaultResourceRoleId` | Rol de los recursos sin mapping |
| `calendarImport.defaultResourcingUnitId` | Unidad de dotación de los miembros |
| `calendarImport.resourceRoleMappings` | Opcional: recurso del MPP (por GUID, UniqueId o nombre exacto) → rol |
| `calendarImport.taskBatchSize` (100), `operationTimeoutSeconds` (180), `taskReadbackTimeoutSeconds` (60) | Lotes y esperas |

Orden de resolución del rol: GUID del recurso → UniqueId → nombre exacto y único → rol predeterminado. Si no se resuelve ninguno, el recurso y sus asignaciones no se crean y la fase termina con exit 11. No se crean roles nuevos.

Las claves de la raíz relacionadas con Excel (`worksheetName`, `taskColumnMappings`, `projectColumnMappings`, `progressPolicy`, `createMissingResourcesAsGeneric`, `dependencyLinkTypeValues`…) son del flujo antiguo y el proceso MPP no las usa.

## 6. Ejecutar

```powershell
dotnet run --project src/ProjectOpsExcelImporter -- import `
  --json "C:\ruta\project-dump.json" --schema project-dump.schema.json --config appsettings.json `
  --state import-state.json --tasks true --dependencies true --progress true --workweeks false --trace true
```

| Opción | Efecto |
|---|---|
| `--tasks true` | Crea las tareas (con `--max-tasks N` o `--task-ids 12,45` solo algunas, más sus ancestros) |
| `--dependencies true` | Crea las dependencias; las que enlazan con tareas resumen se omiten salvo `--summary-dependencies true` |
| `--resources true` | Miembros genéricos del equipo y asignaciones |
| `--work true` | Implica `--resources`: esfuerzo de cada tarea |
| `--progress true` | Implica `--work`: avance. Activa `msdyn_allowpercentcompleteupdate` en el proyecto |
| `--workweeks false` | Omite los WorkWeeks (aún no soportados) |
| `--trace true` | Una línea por llamada a Dataverse |
| `--state fichero` | Estado de la ejecución en curso |

Códigos de salida: `0` correcto · `1` error funcional · `2` configuración/schema · `3` conexión · `4` plantilla · `5` proyecto · `6` calendario del proyecto · `7` WorkWeeks · `8` excepciones · `9` tareas · `10` dependencias · `11` recursos y asignaciones.

Scripts listos para usar (validar carpetas, importar con log, importar varios ficheros en paralelo): [SCRIPTS.md](SCRIPTS.md).

### Importar una carpeta de ficheros

```powershell
.\import-batch-parallel.ps1 -Parallel 3                  # todos, 3 a la vez
.\import-batch-parallel.ps1 -Only 3,4,6 -NoBuild         # solo esos ficheros
```

Genera un log por fichero, `resumen.csv` y `fases.csv` en `logs\batch-<fecha>\`. Cada fichero se importa en **su propio proceso**, que pide su propio inicio de sesión (la caché del token es solo en memoria): hay que estar delante.

## 7. Reanudación e idempotencia

- El proyecto ya importado se **localiza en Dataverse por `axz_mppguid`**: relanzar un fichero lo reutiliza y no duplica nada.
- Tareas, dependencias, miembros del equipo y asignaciones usan **GUID deterministas** derivados del proyecto y de la identidad del MPP; lo ya creado se detecta y se reutiliza.
- Notas, esfuerzo y avance solo se escriben si el valor difiere del que ya hay.
- Los GUID del MPP se conservan como identidad de origen, pero **nunca se usan directamente** como identificadores de Dataverse.
- El **state** guarda lo hecho en la ejecución en curso. Si borras el proyecto en Dataverse, borra también su state: un state que apunta a un proyecto inexistente detiene la importación con exit 5 (no se recrea solo). El script en paralelo borra el state de cada fichero al empezar (`-KeepState` para conservarlo).
- Un proyecto borrado no se puede recrear con el mismo GUID en Project Operations: se crea uno nuevo con otro GUID.

## 8. Mapeo detallado

| MPP (`project-dump.json`) | Project Operations | Notas |
|---|---|---|
| `ProjectProperties.ProjectTitle` | `msdyn_project.msdyn_subject` | |
| `ProjectProperties.GUID` / nombre del fichero | `axz_mppguid` / `axz_mppfile` | Identidad de origen |
| `ProjectProperties.StartDate` | `msdyn_scheduledstart` | 09:00, sin zona |
| "Comienzo" de la tarea resumen del proyecto (primera fila) | `msdyn_actualstart` | `PATCH` idempotente |
| Horario base del calendario | `msdyn_workhourtemplate` (Calendar Template manual) | Por huella |
| `CalendarExceptions` | Reglas del calendario del proyecto | `msdyn_SaveCalendar` |
| Nombre / jerarquía / `Id` | `msdyn_subject` / `msdyn_parenttask` / `msdyn_displaysequence` = `Id`+1 | La tarea resumen del proyecto no se crea |
| `Start`, `Duration` | `msdyn_scheduledstart`, `msdyn_duration` (días) | No se envía `Finish`; las tareas resumen las calcula la plataforma |
| `Notes` | `msdyn_descriptionplaintext` + `msdyn_description` (HTML) | Una `<div>` por línea; los tabuladores se conservan con `&nbsp;` |
| Relación y tipo | `msdyn_projecttaskdependency`, `msdyn_linktype` | FC 192350000 · CC …01 · FF …02 · CF **…04** |
| Retardo | `msdyn_projecttaskdependencylinklag` | Segundos; `+` retardo, `−` adelanto |
| Recurso Work con asignaciones | `msdyn_projectteam` genérico | `msdyn_name` = nombre del recurso; recurso común; rol `msdyn_resourcecategory`; unidad `msdyn_organizationalunit` |
| Asignación | `msdyn_resourceassignment` | Proyecto, tarea y miembro |
| Σ `Work` de las asignaciones | `msdyn_effort` de la tarea | `PssUpdateV2` |
| `PercentComplete` | `msdyn_progress` | **Fracción 0–1** en la API (100 se rechaza); se guarda como porcentaje |

## 9. Limitaciones conocidas

- **Duración máxima de una tarea hoja**: Project Operations rechaza el lote entero (`E_LIMITEXCEEDED_LEAFTASKDURATION`). Se observó con una tarea de 1367 días; una de 695 días entró bien. El límite exacto no está confirmado. Afecta al menos a un fichero de ejemplo (el 10) y probablemente al 4.
- **WorkWeeks**: sin soporte; los ficheros con WorkWeeks pueden tener fechas de fin distintas a las del MPP.
- **Hora de inicio**: se envía sin zona y Dataverse la interpreta como UTC (puede verse 1–2 h más tarde en hora de Madrid).
- **La API de planificación no admite** actualizar asignaciones ni `msdyn_effortcompleted`; el avance de una tarea con esfuerzo 0 se rechaza y se omite con aviso.
- **Duración con decimales**: una duración de 0,125 días se guarda como 0,13.
- **Un lote de operaciones tiene un máximo de 100** entidades.
- El **recurso genérico** de proyecto es único en el entorno: todos los miembros genéricos apuntan a él.
- El avance parcial puede dar un trabajo completado ligeramente distinto al real del MPP (el log lo avisa).

## 10. Rendimiento

Cada ejecución imprime al final un bloque **`[Timing]`** con la duración de cada fase (y de las subfases de recursos), el total y el tamaño del proyecto, también si falla.

Medido con la versión anterior sobre el proyecto más grande (1987 tareas): del total, solo ~14 % son llamadas HTTP; el resto es **espera a que la plataforma procese cada lote** (~35 s por lote de 100 tareas o dependencias; ~170 s por lote de 100 asignaciones). La forma de reducir el tiempo de una carga completa es importar **varios proyectos a la vez**, no acelerar uno.

## 11. Diagnóstico (solo lectura salvo que se indique)

| Comando | Para qué |
|---|---|
| `validate-schema <json> <schema> [log]` | Valida un JSON contra el schema (sin Dataverse) |
| `tasks-preview --json … --config …` | Plan de tareas, dependencias, recursos, payloads y tareas que cruzan excepciones (sin Dataverse) |
| `whoami --config …` | Identidad con la que se conecta |
| `calendar-metadata`, `calendar-dump`, `dataverse-get --url …` | Metadatos y consultas puntuales |
| `calendar-save-test`, `assignment-work-test` | Pruebas **experimentales** de escritura; sin `--execute` son dry-run |

## 12. Código heredado (no forma parte del proceso)

`analyze`, `validate`, `plan` y `resources-template`, y el `import` antiguo que sigue a las fases de calendario, trabajan con un JSON de "plan" (`{ "project": {...}, "tasks": [...] }`) y no con el `project-dump.json`. La lectura de Excel se eliminó. Se conservan por compatibilidad: **no los uses para importar un MPP**.
