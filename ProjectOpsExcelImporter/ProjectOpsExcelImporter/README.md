# ProjectOpsExcelImporter

Aplicación de consola **.NET 8 / C#** para leer `ProyectoPlantilla.xlsx` y crear un proyecto de Dynamics 365 Project Operations Core mediante Dataverse Web API y las **Project Schedule APIs**.

La solución incluye:

- lectura del Excel con cabeceras en español;
- proyecto tomado de la primera fila de datos (fila 2; la fila 1 es la cabecera);
- WBS y jerarquía inferidas por la sangría del nombre;
- fechas, duración, esfuerzo, hitos y avance;
- dependencias FC/CC/FF/CF;
- recursos, miembros del equipo y asignaciones;
- mapeo opcional de campos personalizados para GUID/Id, `Texto1..3`, costes, SÁBANA, facturación y cobros;
- validación previa contra límites de Project Operations;
- lotes de hasta 200 operaciones y fichero de estado reanudable.

## 1. Análisis del Excel de entrada

El comando `analyze` genera un informe JSON (no versionado) con métricas del fichero: hoja, fila de proyecto, número de tareas, tareas resumen y hoja, niveles de jerarquía, hitos, dependencias (con lag/lead), recursos distintos, asignaciones, y tareas con trabajo o coste.

### Validaciones y limitaciones conocidas

- Project Operations admite un máximo de **20 vínculos por tarea** (predecesoras + sucesoras). El validador detiene la importación si alguna tarea lo supera, para evitar que un OperationSet falle; hay que eliminar o rediseñar los vínculos sobrantes.
- Las filas sin nombre se importan con un nombre temporal `[SIN NOMBRE] Id <n>`, conservando su nivel de esquema a partir de las filas vecinas.
- Las dependencias con adelanto/retardo conservan la expresión original en `msdyn_description`. El modelo estándar de dependencia no expone un campo de lag equivalente al de Project Desktop; el valor calculado puede guardarse en un campo personalizado configurando `dependencyLagCustomField`.
- La columna `Sucesoras` es redundante y puede contener valores truncados. La aplicación usa `Predecesoras`, que contiene los vínculos completos, para evitar duplicados o expresiones incompletas.
- Los costes del Excel no deben escribirse sobre campos calculados/rollup estándar. Se pueden cargar en campos personalizados del proyecto/tarea mediante los mappings de `appsettings.json`.

## 2. Arquitectura de la importación

```text
ProyectoPlantilla.xlsx
        │
        ├─ analyze / validate / plan (sin conexión a Dataverse)
        │
        └─ import
             ├─ msdyn_CreateProjectV1
             ├─ recuperar bucket predeterminado
             ├─ tareas por lotes: CreateOperationSet + PssCreateV2 + ExecuteOperationSet
             ├─ dependencias por lotes
             ├─ miembros de equipo con msdyn_CreateTeamMemberV1
             ├─ asignaciones por lotes
             └─ avance mediante PssUpdateV2
```

Microsoft documenta que las tareas, dependencias y asignaciones son entidades de programación administradas por el motor de Project, por lo que se crean con las Schedule APIs y no con un `Create` directo a `msdyn_projecttask`.

Documentación:

- https://learn.microsoft.com/en-us/dynamics365/project-operations/project-management/schedule-api-preview
- https://learn.microsoft.com/en-us/dynamics365/project-operations/project-management/scheduling-apis-powerautomate-v2
- https://learn.microsoft.com/en-us/dynamics365/project-operations/project-management/project-and-task-limitations
- https://learn.microsoft.com/en-us/dynamics365/project-operations/project-management/create-assignments

## 3. Requisitos

1. .NET SDK 8.
2. Entorno con Project Operations Core.
3. Usuario nominativo con licencia de Microsoft Project y permisos en Project Operations.
4. App Registration de tipo cliente público para autenticación delegada:
   - permiso delegado **Dynamics CRM / user_impersonation**;
   - consentimiento de administrador si lo exige el tenant;
   - Redirect URI `http://localhost` para `Interactive`, o habilitar public client flow para `DeviceCode`.
5. No usar Client Credentials/Application User: Microsoft indica que los Application Users, System Users e Integration Users no pueden ejecutar Schedule APIs.

La aplicación **no utiliza ni necesita un client secret**.

## 4. Compilar

```powershell
cd ProjectOpsExcelImporter
dotnet restore
dotnet build -c Release
dotnet run --project src/ProjectOpsExcelImporter -- self-test
```

## 5. Configurar

Copia el ejemplo y rellena únicamente los identificadores públicos:

```powershell
Copy-Item appsettings.example.json appsettings.json
```

```json
{
  "tenantId": "TENANT-GUID",
  "clientId": "APP-REGISTRATION-CLIENT-ID",
  "environmentUrl": "https://YOUR-ORG.crm.dynamics.com",
  "authMode": "Interactive",
  "redirectUri": "http://localhost"
}
```

No añadas secretos. `appsettings.json` está excluido de Git.

### Campos personalizados

Los mappings relacionan cabecera Excel → nombre lógico Dataverse. Los valores vacíos se ignoran.

```json
"taskColumnMappings": {
  "GUID": "axz_sourceguid",
  "Id": "axz_sourceid",
  "Texto1": "axz_texto1",
  "Costo": "axz_sourcecost",
  "FACTURACIÓN": "axz_facturacion"
}
```

Los campos deben existir previamente en `msdyn_projecttask` y tener un tipo compatible. Las Schedule APIs admiten campos personalizados, pero conviene incluir solo los necesarios.

### Tipos de dependencia

`dependencyLinkTypeValues` contiene los option-set values esperados. Antes de la primera carga en un entorno, confirma mediante metadata de Dataverse que los valores de `msdyn_linktype` coinciden. Si difieren, modifícalos en `appsettings.json` sin recompilar.

## 5b. Origen de datos: Excel o JSON

Todos los comandos (`analyze`, `validate`, `plan`, `resources-template`, `import`) aceptan:

- `--excel fichero.xlsx` → lee desde Excel;
- `--json fichero.json` → lee desde JSON;
- `--source excel|json` (opcional) → fuerza el origen; por defecto se deduce de `--excel`/`--json`. Pasar ambos sin `--source` es un error.

El JSON usa el mismo formato que genera `plan` (`{ "project": {...}, "tasks": [...] }`), por lo que se puede exportar con `plan`, editar y reimportar. Campos mínimos: `project.name` y por tarea `sourceId` y `name`. Opcionales: `sourceGuid` (si falta se genera uno estable), `excelRow` (si falta se usa la posición en el array), `parentSourceId` u `outlineLevel` para la jerarquía (si hay algún `parentSourceId` se usa este; si no, `outlineLevel` en orden de array), `dependencies`, `resources`, `rawValues` (para los mappings a campos personalizados). La validación y la importación son idénticas a las de Excel.

```powershell
dotnet run --project src/ProjectOpsExcelImporter -- import --json mi-proyecto.json --config appsettings.json
```

## 6. Validar el Excel

```powershell
dotnet run --project src/ProjectOpsExcelImporter -- analyze `
  --excel samples/ProyectoPlantilla.xlsx `
  --config appsettings.json `
  --out analysis.json
```

Para el Excel adjunto el proceso devuelve código 2 por `TASK_LINK_LIMIT` en la tarea 2. Corrige el Excel y repite hasta obtener `APTO PARA IMPORTAR`.

## 7. Revisar el plan normalizado

```powershell
dotnet run --project src/ProjectOpsExcelImporter -- plan `
  --excel samples/ProyectoPlantilla.xlsx `
  --config appsettings.json `
  --out import-plan.json
```

El plan JSON contiene todas las tareas, padres, fechas, duraciones, esfuerzos, costes de origen, dependencias y recursos. Permite revisar la transformación sin conectarse a Dataverse.

## 8. Recursos

`resources.template.json` se genera con el comando `resources-template` a partir del Excel (no se versiona porque contiene nombres de personas).

Opciones por recurso:

- `projectTeamId`: reutiliza un miembro del equipo ya creado para el proyecto.
- `bookableResourceId`: crea el miembro de equipo contra ese Bookable Resource.
- sin GUIDs y `createAsGeneric=true`: crea un miembro genérico con el nombre de origen.

Ejemplo:

```json
{
  "sourceName": "CHERCOLES FERRANDIZ, JUAN CARLOS",
  "projectTeamId": null,
  "bookableResourceId": "GUID-BOOKABLE-RESOURCE",
  "createAsGeneric": false,
  "notes": "Usuario nominativo"
}
```

Las asignaciones solo se crean en tareas hoja. Las asignaciones que el Excel coloca en tareas resumen se informan y se omiten. Project Operations distribuye el esfuerzo a partir del esfuerzo de la tarea y del calendario; las Schedule APIs no permiten establecer directamente `Effort`, `EffortCompleted`, `EffortRemaining` o `PlannedWork` en la creación de una asignación.

## 9. Importar

Después de corregir los errores del validador:

```powershell
dotnet run --project src/ProjectOpsExcelImporter -- import `
  --excel samples/ProyectoPlantilla.xlsx `
  --config appsettings.json `
  --resources resources.template.json `
  --state import-state.json
```

Se abrirá el inicio de sesión de Microsoft con el usuario nominativo. Para servidores sin navegador, configura `"authMode": "DeviceCode"`.

### Reanudación

`import-state.json` guarda:

- ProjectId y BucketId;
- GUIDs Dataverse generados para cada tarea;
- tareas, dependencias, asignaciones y avances ya procesados;
- miembros del equipo.

Si la ejecución se interrumpe, repite exactamente el mismo comando. No borres el fichero de estado mientras continúe la misma carga.

## 10. Mapeo aplicado

| Excel | Project Operations | Tratamiento |
|---|---|---|
| Primera fila de datos / Nombre | `msdyn_project.msdyn_subject` | Crea proyecto con `msdyn_CreateProjectV1` |
| Comienzo del proyecto | `msdyn_scheduledstart` | Se envía en la creación |
| Nombre de tarea | `msdyn_subject` | Se recorta la sangría |
| Sangría | `msdyn_parenttask` | Se infiere la jerarquía |
| Comienzo | `msdyn_scheduledstart` | 09:00 local del proyecto |
| Fin | `msdyn_scheduledend` | Opcional; desactivado por defecto para evitar sobre-restricción |
| Duración | `msdyn_duration` | Días; horas/sem./meses se convierten según configuración |
| Trabajo | `msdyn_effort` | Horas |
| 0 días | Hito | Duración 0 |
| % completado | `msdyn_progress` | Segunda fase; por defecto solo 0/100 % |
| Predecesoras | `msdyn_projecttaskdependency` | FC/CC/FF/CF y referencias GUID |
| Lag/lead | descripción + campo personalizado opcional | No se fuerza sobre un campo estándar inexistente |
| Recursos | `msdyn_projectteam` + `msdyn_resourceassignment` | Genérico o mapeado a Bookable Resource |
| GUID / Id / Texto / costes | campos personalizados | Configurables |

## 11. Decisiones de seguridad y consistencia

- Los GUID del Excel se conservan como identificadores de origen, pero **no se reutilizan directamente como `msdyn_projecttaskid`**. Se generan GUID deterministas nuevos para Dataverse.
- El importador no crea tareas mediante CRUD directo.
- Los OperationSet se limitan a `batchSize` (100 por defecto, máximo 200).
- Cada lote espera a que el último registro aparezca en Dataverse antes de continuar.
- Si la validación encuentra un límite excedido, la importación se cancela antes de modificar el entorno.
