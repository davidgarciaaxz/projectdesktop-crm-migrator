# MppReaderTest

Prueba técnica para comprobar **qué información puede leerse directamente de un fichero Microsoft Project (`.mpp`) usando exclusivamente MPXJ.Net**, como paso previo a una futura migración a Dynamics 365 Project Operations Core.

La aplicación **no** integra con Dynamics 365, Dataverse ni Project Schedule API, **no** transforma datos al modelo de Project Operations y **no** usa Aspose.Tasks, Interop/COM ni Microsoft Project instalado.

## Versiones

| Componente | Versión |
|---|---|
| .NET (TargetFramework) | `net8.0` (C#, Nullable e ImplicitUsings habilitados) |
| MPXJ.Net (NuGet) | `16.11.0` |

MPXJ.Net es un wrapper .NET de la librería Java MPXJ (compilada con IKVM). **La primera compilación puede tardar varios minutos** mientras se restauran y preparan los ensamblados IKVM; las siguientes son inmediatas.

## Compilar

Desde la carpeta raíz (donde está `MppReaderTest.sln`):

```powershell
dotnet restore
dotnet build
```

## Ejecutar

Desde la carpeta del proyecto `MppReaderTest`:

```powershell
cd MppReaderTest
dotnet run -- "C:\Temp\Proyecto.mpp"
dotnet run -- "C:\Temp\Proyecto.mpp" "C:\Temp\MppOutput"
```

Ruta con espacios (las comillas son obligatorias):

```powershell
dotnet run -- "C:\Temp\Mi Proyecto.mpp" "C:\Temp\Mpp Output\Mi Proyecto"
```

- Primer argumento: ruta del `.mpp` (obligatorio).
- Segundo argumento: carpeta de salida (opcional). Si se omite se usa `output` dentro de la carpeta actual de ejecución.
- `--no-dynamic`: desactiva el volcado dinámico de todos los `FieldType` (más rápido).
- `--dynamic-json`: incluye además cada valor del volcado dinámico en `project-dump.json` (puede ocupar cientos de MB; por defecto esos valores solo van a `dynamic-fields.csv`).

### Códigos de salida

| Código | Significado |
|---|---|
| 0 | Ejecución correcta |
| 1 | Argumentos incorrectos (sin argumentos, extensión no `.mpp`, opción desconocida) |
| 2 | Fichero no encontrado |
| 3 | Error de lectura del MPP (formato no reconocido, dañado, versión no soportada, acceso denegado, fichero bloqueado) |
| 4 | Error generando los ficheros de salida |
| 5 | Error inesperado |

## Ficheros generados

| Fichero | Contenido |
|---|---|
| `project-dump.txt` | Copia exacta de lo mostrado en consola (UTF-8), organizada por secciones `=== ... ===`. |
| `project-dump.json` | Volcado estructurado (System.Text.Json, indentado, UTF-8, fechas ISO 8601, enums como texto, sin referencias circulares). Secciones: `Project`, `Tasks`, `VisibleColumns`, `ViewTables`, `Dependencies`, `Resources`, `ResourceRateTables`, `ResourceAssignments` (con validación de coste y datos timephased), `Calendars`, `CalendarExceptions`, `CustomFieldCatalog`, `CustomFieldDefinitions`, `CustomFieldValues`, `DynamicFields` (estadísticas por FieldType), `Summary`, `MigrationReadiness`, `Warnings`, `Errors`. |
| `custom-fields.csv` | Valores de campos personalizados de tareas, recursos y asignaciones. UTF-8, separador `;`, comillas cuando el valor contiene `;`, comillas o saltos de línea. Columnas: `EntityType;EntityId;EntityUniqueId;EntityName;FieldType;InternalName;Alias;DataType;DotNetType;Value;FormattedValue;IsEnterpriseCustomField;Formula;LookupTable`. |
| `dynamic-fields.csv` | Volcado de diagnóstico: todos los valores no nulos de todos los `TaskField`, `ResourceField` y `AssignmentField` legibles. |

No se serializa el `ProjectFile` de MPXJ: se usan modelos simples (`Models/*.cs`) solo para el volcado. No son el modelo definitivo de migración.

### Secciones del informe de texto

`FILE`, `PROJECT INFORMATION`, `TASKS` (indentado por Outline Level), `VISIBLE COLUMNS (MICROSOFT PROJECT VIEW)`, `VIEW TABLES`, `TASK COSTS AND BASELINES`, `DEPENDENCIES`, `RESOURCES`, `RESOURCE RATE TABLES`, `RESOURCE ASSIGNMENTS`, `CALENDARS`, `CUSTOM FIELDS AND EXTENDED ATTRIBUTES`, `DYNAMIC FIELD DUMP (DIAGNOSTIC)`, `SUMMARY`, `MIGRATION READINESS CHECK`, `WARNINGS`, `ERRORS`.

## Cómo se descubren las columnas personalizadas (SÁBANA, FACTURACIÓN...)

No se presupone qué `CostN`/`NumberN`/`TextN` corresponde a cada alias. Se combinan tres fuentes reales del MPP:

1. `ProjectFile.CustomFields`: definiciones con `Alias` (p. ej. `Cost4 = SÁBANA`).
2. `TaskContainer.GetFieldTypeByAlias(alias)`.
3. `ProjectFile.Tables[].Columns[].Title`: el título de una columna en una tabla de vista. En Microsoft Project se puede renombrar una columna **solo en la vista** sin crear un alias de campo; en ese caso el alias no aparece en `CustomFields` y solo se descubre aquí.

La comparación ignora mayúsculas y tildes. El origen del alias se informa en `AliasSource`/`AliasSources`.

## Estados YES / NO / PARTIAL (MIGRATION READINESS CHECK)

| Estado | Significado |
|---|---|
| `YES` | MPXJ.Net expone el dato y el fichero contiene valores que se han recuperado correctamente. |
| `PARTIAL` | El dato se ha leído, pero solo para parte de los elementos, o se ha leído en todos y todos los valores son 0/vacíos (no se puede confirmar frente a MS Project). |
| `NO` | No se ha podido recuperar. El campo `Reason` indica el motivo. |

`Reason` distingue: `ValuesRetrieved`, `DefinitionFoundWithoutValues`, `DefinitionNotFound`, `ApiNotAvailable`, `ReadError`, `NotPresentInFile`. Un `NO` con `NotPresentInFile` o `DefinitionNotFound` **no** significa que MPXJ no lo soporte, sino que el fichero probado no lo contiene.

## Advertencia sobre costes de tareas resumen

Las tareas resumen (y la tarea resumen del proyecto, UID 0) **ya agregan** los importes de sus subtareas. Sumar todas las tareas duplica importes. Por eso el resumen muestra por separado:

- suma de todas las tareas (incluye resúmenes, **duplica**);
- suma solo de tareas no resumen;
- valor de la tarea resumen del proyecto.

Estos totales son solo una comprobación del contenido leído, no un total económico definitivo.

La validación de coste de las asignaciones (`CostValidation`) es solo informativa: muestra el coste leído del MPP, un coste calculado (trabajo × tarifa vigente en la tabla de tarifas usada + coste por uso) y la diferencia. Puede no coincidir por cambios de tarifa durante la asignación, calendarios, horas extra, contornos, costes reales introducidos manualmente u otras reglas de Microsoft Project. **Nunca sustituye al coste leído.**

## Limitaciones detectadas (MPXJ.Net 16.11.0)

- **Fórmulas**: `CustomField` no expone fórmulas ni indica si el campo es calculado. Se lee el valor almacenado en el MPP; `Formula = null` e `IsCalculated = null` (desconocido).
- **Listas `Enterprise*` de `TaskFieldList`, `ResourceFieldLists` y `AssignmentFieldLists`**: lanzan `InvalidCastException` (error interno IKVM) al enumerarlas. Los campos se descubren igualmente filtrando `TaskField.Values`, `ResourceField.Values` y `AssignmentField.Values` por nombre. Se informa como warning.
- **Fichero no reconocido**: `UniversalProjectReader.GetProjectReaderProxy` lanza `NullReferenceException` en lugar de devolver `null`; se controla y se devuelve el código 3.
- **Calendarios**: MPXJ solo expone `UniqueID` (no hay un ID independiente).
- **"Tipo de duración"**: no existe como propiedad; se informan las unidades de la duración y el indicador `Estimated`.
- **Formato de moneda**: se exponen símbolo, código, posición del símbolo, decimales y separadores, pero no una cadena de formato completa.
- **Fecha actual (`CurrentDate`)**: en los ficheros probados coincide con el momento de la lectura (MPXJ la rellena si el MPP no la almacena).
- **Cero frente a vacío**: los campos numéricos/coste personalizados se devuelven como `0` cuando no tienen valor, por lo que no se puede distinguir "sin valor" de "valor 0" (estado `DefinedZero`).
- **Asignaciones sin recurso**: el trabajo de tareas sin recurso aparece como asignaciones con `Resource = null` (recurso interno "sin asignar", GUID terminado en `0000000000ff`). En esas tareas el trabajo de la tarea es 0.
- **Tablas de tarifas**: las fechas abiertas (`DateTimeHelper.StartDateNA/EndDateNA`) se informan como `null` con `EffectiveFromIsOpen/EffectiveToIsOpen = true`.
- **Volumen**: el volcado dinámico puede generar cientos de miles de valores; por defecto solo se escriben en `dynamic-fields.csv` y en el JSON se incluyen estadísticas por FieldType.

## Organización del código

| Fichero | Responsabilidad |
|---|---|
| `Program.cs` | Argumentos, orquestación y códigos de salida. |
| `MppProjectReader.cs` | Validación del fichero y lectura con `UniversalProjectReader`. |
| `ProjectInspector*.cs` | Extracción a modelos de volcado (proyecto, tareas, recursos, calendarios, campos personalizados, volcado dinámico, resumen y readiness). |
| `ConsoleReportWriter.cs` / `TeeWriter.cs` | Informe de texto simultáneo en consola y `project-dump.txt`. |
| `JsonReportWriter.cs` | `project-dump.json`. |
| `CsvCustomFieldsWriter.cs` | `custom-fields.csv` y `dynamic-fields.csv`. |
| `Fmt.cs` | Formateo sin pérdida de precisión e identificación de FieldTypes. |
| `Models/*.cs` | Modelos simples de salida. |
