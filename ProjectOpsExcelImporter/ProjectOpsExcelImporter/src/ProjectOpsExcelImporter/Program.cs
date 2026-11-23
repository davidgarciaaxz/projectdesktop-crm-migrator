using System.Text.Json;
using ProjectOpsExcelImporter.Calendars;

namespace ProjectOpsExcelImporter;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            var command = args[0].ToLowerInvariant();
            if (command == "validate-schema")
            {
                if (args.Length is not (3 or 4))
                {
                    Console.Error.WriteLine("Uso: validate-schema \"<ruta-json>\" \"<ruta-schema>\" [\"<fichero-log>\"]");
                    return 1;
                }
                return SchemaValidator.Run(args[1], args[2], args.Length == 4 ? args[3] : null);
            }
            // --execute es un indicador sin valor, exclusivo de calendar-save-test (el parser genérico exige valor).
            var executeFlag = command == "calendar-save-test" && args.Any(a => a.Equals("--execute", StringComparison.OrdinalIgnoreCase));
            var optionArgs = executeFlag ? args.Where(a => !a.Equals("--execute", StringComparison.OrdinalIgnoreCase)).ToArray() : args;
            var options = ParseOptions(optionArgs.Skip(1).ToArray());
            if (options.TryGetValue("trace", out var traceText) && traceText.Equals("true", StringComparison.OrdinalIgnoreCase))
                DataverseClient.Trace = Console.Out;     // una línea por llamada HTTP a Dataverse (hora, método, destino, estado, ms)
            if (command == "self-test") return RunSelfTest();
            if (command == "whoami")
            {
                var whoAmIConfigPath  = Required(options, "config");
                var connectionSettings = AppSettings.Load(whoAmIConfigPath);
                connectionSettings.ValidateForImport();

                using var client = new DataverseClient(connectionSettings);

                var response = await client
                    .GetAsync("WhoAmI", cts.Token)
                    .ConfigureAwait(false);

                Console.WriteLine("Conexión correcta con Dataverse.");
                Console.WriteLine($"UserId:         {response["UserId"]}");
                Console.WriteLine($"BusinessUnitId: {response["BusinessUnitId"]}");
                Console.WriteLine($"OrganizationId: {response["OrganizationId"]}");

                return 0;
            }
            if (command == "calendar-metadata")
            {
                // Solo lectura: no crea, actualiza ni borra registros.
                var metadataSettings = AppSettings.Load(Required(options, "config"));
                metadataSettings.ValidateForImport();
                using var metadataClient = new DataverseClient(metadataSettings);
                var explorer = new CalendarMetadataExplorer(metadataClient, Console.Out);
                var metadata = await explorer.ExploreAsync(metadataSettings.EnvironmentUrl, cts.Token).ConfigureAwait(false);
                if (options.TryGetValue("out", out var metadataPath))
                {
                    File.WriteAllText(metadataPath, metadata.ToJsonString(JsonDefaults.Options));
                    Console.WriteLine($"Metadatos escritos en {Path.GetFullPath(metadataPath)}");
                }
                explorer.PrintSummary(metadata);
                return 0;
            }
            if (command == "calendar-dump")
            {
                // Solo lectura: vuelca una plantilla de trabajo y sus calendar/calendarrule.
                var dumpSettings = AppSettings.Load(Required(options, "config"));
                dumpSettings.ValidateForImport();
                var dumpKey = options.ContainsKey("calendar-id") ? "calendar-id" : options.ContainsKey("project-id") ? "project-id" : "template-id";
                if (!Guid.TryParse(Required(options, dumpKey), out var dumpId))
                    throw new ArgumentException($"--{dumpKey} no es un GUID válido.");
                using var dumpClient = new DataverseClient(dumpSettings);
                var dumper = new CalendarTemplateDumper(dumpClient, Console.Out);
                var dump = await (dumpKey switch
                {
                    "calendar-id" => dumper.DumpCalendarByIdAsync(dumpId, cts.Token),
                    "project-id" => dumper.DumpProjectAsync(dumpId, cts.Token),
                    _ => dumper.DumpAsync(dumpId, cts.Token)
                }).ConfigureAwait(false);
                var dumpText = dump.ToJsonString(JsonDefaults.Options);
                if (options.TryGetValue("out", out var dumpPath))
                {
                    File.WriteAllText(dumpPath, dumpText);
                    Console.WriteLine($"Volcado escrito en {Path.GetFullPath(dumpPath)}");
                }
                else Console.WriteLine(dumpText);
                return 0;
            }
            if (command == "tasks-preview")
            {
                // Sin Dataverse: mapeo de tareas, atributos exactos y tareas que atraviesan CalendarExceptions.
                var previewSettings = AppSettings.Load(Required(options, "config"));
                ApplyTaskOptions(options, previewSettings);
                previewSettings.CalendarImport.Validate();
                var previewProject = MppProjectReader.ReadFile(Required(options, "json"));
                TaskPreview.Print(previewProject, previewSettings.CalendarImport,
                    new TaskSelection(previewSettings.CalendarImport.TaskMaxCount, previewSettings.CalendarImport.TaskUniqueIds), Console.Out);
                return 0;
            }
            if (command == "dataverse-get")
            {
                // Diagnóstico de solo lectura: un único GET relativo a /api/data/<version>/.
                var getSettings = AppSettings.Load(Required(options, "config"));
                getSettings.ValidateForImport();
                using var getClient = new DataverseClient(getSettings);
                var getResponse = await getClient.GetAsync(Required(options, "url"), cts.Token).ConfigureAwait(false);
                Console.WriteLine(getResponse.ToJsonString(JsonDefaults.Options));
                return 0;
            }
            if (command == "assignment-work-test")
            {
                // EXPERIMENTAL y aislado: esfuerzo (Work/ActualWork/RemainingWork del MPP) de las asignaciones de --task-ids. Sin --execute true es dry-run.
                var workSettings = AppSettings.Load(Required(options, "config"));
                ApplyTaskOptions(options, workSettings);
                workSettings.ValidateForImport();
                var workProject = MppProjectReader.ReadFile(Required(options, "json"));
                var workExecute = options.TryGetValue("execute", out var workExecuteText) && workExecuteText.Equals("true", StringComparison.OrdinalIgnoreCase);
                using var workClient = new DataverseClient(workSettings);
                return options.GetValueOrDefault("level", "assignment").Equals("task", StringComparison.OrdinalIgnoreCase)
                    ? await AssignmentWorkTest.RunTaskLevelAsync(workProject, workSettings.CalendarImport, workClient, workExecute, Console.Out, cts.Token,
                        options.TryGetValue("fields", out var workFields) ? workFields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null,
                        options.TryGetValue("progress-factor", out var factorText) ? decimal.Parse(factorText, System.Globalization.CultureInfo.InvariantCulture) : 1m).ConfigureAwait(false)
                    : await AssignmentWorkTest.RunAsync(workProject, workSettings.CalendarImport, workClient, workExecute, Console.Out, cts.Token).ConfigureAwait(false);
            }
            if (command == "calendar-save-test")
            {
                // EXPERIMENTAL y aislado: un único día no laborable mediante msdyn_SaveCalendar.
                // Sin --execute es dry-run: no se llama a Dataverse.
                var saveSettings = AppSettings.Load(Required(options, "config"));
                saveSettings.ValidateForImport();
                if (!Guid.TryParse(Required(options, "calendar-id"), out var saveCalendarId))
                    throw new ArgumentException("--calendar-id no es un GUID válido.");
                if (!DateTime.TryParseExact(Required(options, "date"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var saveDate))
                    throw new ArgumentException("--date debe tener el formato yyyy-MM-dd.");
                var saveZone = options.TryGetValue("timezone", out var zoneText) && int.TryParse(zoneText, out var zone) ? zone : 95;
                var execute = executeFlag;
                using var saveClient = new DataverseClient(saveSettings);
                var saveRepository = new DataverseProjectCalendarRepository(saveClient, saveSettings.CalendarImport);
                DateTime? saveEnd = null;
                if (options.TryGetValue("end-date", out var endText))
                {
                    if (!DateTime.TryParseExact(endText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedEnd))
                        throw new ArgumentException("--end-date debe tener el formato yyyy-MM-dd.");
                    saveEnd = parsedEnd;
                }
                // --periods "09:00-19:00@0,13:00-15:00@1": tramo y WorkHourType de cada Rule ("@n" opcional; por defecto --work-hour-type).
                var defaultType = options.TryGetValue("work-hour-type", out var defaultTypeText) && int.TryParse(defaultTypeText, out var dt) ? dt : 2;
                List<(NormalizedTimePeriod Period, int WorkHourType)>? savePeriods = null;
                if (options.TryGetValue("periods", out var periodsText))
                    savePeriods = periodsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item =>
                    {
                        var parts = item.Split('@', 2);
                        return (NormalizedTimePeriod.Parse(parts[0], "--periods"), parts.Length == 2 && int.TryParse(parts[1], out var t) ? t : defaultType);
                    }).ToList();
                var saveWorkHourType = options.TryGetValue("work-hour-type", out var typeText) && int.TryParse(typeText, out var type) ? type : 2;
                var saveResult = await saveRepository.TestSaveCalendarNonWorkingDayAsync(saveCalendarId, saveDate, saveZone, cts.Token, dryRun: !execute,
                    endDate: saveEnd, workingPeriods: savePeriods, workHourType: saveWorkHourType).ConfigureAwait(false);
                return saveResult.StatusCode is null or (>= 200 and < 300) ? 0 : 1;
            }
            if (command == "import")
            {
                // Fases de calendario (schema -> calendario MPP -> plantilla -> proyecto -> WorkWeeks -> excepciones).
                // Con CalendarImport.StopAfterCalendar=true (predeterminado) el comando termina aquí.
                var calendarResult = await RunCalendarImportAsync(options, cts.Token).ConfigureAwait(false);
                if (!calendarResult.ContinueWithTaskImport) return calendarResult.ExitCode;
            }
            var settings = options.TryGetValue("config", out var configPath) ? AppSettings.Load(configPath) : new AppSettings();
            var workbook = LoadProject(options, settings);
            var report = ProjectValidator.Validate(workbook);

            switch (command)
            {
                case "analyze":
                case "validate":
                    WriteReport(report);
                    if (options.TryGetValue("out", out var reportPath))
                        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonDefaults.Options));
                    return report.HasErrors ? 2 : 0;

                case "plan":
                    var planPath = Required(options, "out");
                    File.WriteAllText(planPath, JsonSerializer.Serialize(workbook, JsonDefaults.Options));
                    WriteReport(report);
                    Console.WriteLine($"Plan escrito en {Path.GetFullPath(planPath)}");
                    return report.HasErrors ? 2 : 0;

                case "resources-template":
                    var resourcePath = Required(options, "out");
                    var template = new ResourceMap
                    {
                        Resources = workbook.Tasks.SelectMany(t => t.Resources).Select(r => r.SourceName)
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .Select(n => new ResourceMapEntry { SourceName = n, CreateAsGeneric = true }).ToList()
                    };
                    File.WriteAllText(resourcePath, JsonSerializer.Serialize(template, JsonDefaults.Options));
                    Console.WriteLine($"Plantilla escrita en {Path.GetFullPath(resourcePath)} ({template.Resources.Count} recursos).");
                    return 0;

                case "import":
                    if (!options.ContainsKey("config")) throw new ArgumentException("El comando import requiere --config.");
                    settings.ValidateForImport();
                    WriteReport(report);
                    if (report.HasErrors)
                    {
                        Console.Error.WriteLine("Importación cancelada: corrige los errores de validación.");
                        return 2;
                    }
                    var statePath = options.GetValueOrDefault("state", "import-state.json");
                    var resources = ResourceMap.Load(options.GetValueOrDefault("resources"));
                    using (var client = new DataverseClient(settings))
                    {
                        var importer = new ProjectOpsImporter(settings, client);
                        var state = await importer.ImportAsync(workbook, resources, statePath, cts.Token).ConfigureAwait(false);
                        Console.WriteLine($"Importación enviada. ProjectId={state.ProjectId:D}; estado={Path.GetFullPath(statePath)}");
                    }
                    return 0;

                default:
                    throw new ArgumentException($"Comando desconocido: {command}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operación cancelada.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<CalendarImportCommandResult> RunCalendarImportAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var jsonPath = Required(options, "json");
        var settings = AppSettings.Load(Required(options, "config"));
        var statePath = options.GetValueOrDefault("state", "import-state.json");
        options.TryGetValue("schema", out var schemaPath);
        ApplyTaskOptions(options, settings);
        DataverseClient? client = null;
        try
        {
            return await CalendarImportCommand.RunAsync(jsonPath, schemaPath, settings.CalendarImport, statePath, () =>
            {
                // Se invoca solo después de validar el schema. Crear el cliente no abre ninguna conexión.
                settings.ValidateForImport();
                client = new DataverseClient(settings);
                return new CalendarImportServices(
                    new DataverseCalendarTemplateRepository(client, settings.CalendarImport),
                    new DataverseProjectOperationsProjectService(client, settings.CalendarImport),
                    new DataverseProjectCalendarRepository(client, settings.CalendarImport),
                    new DataverseProjectTaskService(client, settings.CalendarImport.OperationTimeoutSeconds),
                    new DataverseProjectDependencyService(client, settings.CalendarImport),
                    new DataverseProjectResourceService(client, settings.CalendarImport));
            }, Console.Out, Console.Error, cancellationToken).ConfigureAwait(false);
        }
        finally { client?.Dispose(); }
    }

    /// <summary>Opciones de la fase de tareas: --tasks true|false, --max-tasks N, --task-ids 12,45. Las unidades de duración vienen de AppSettings.</summary>
    private static void ApplyTaskOptions(IReadOnlyDictionary<string, string> options, AppSettings settings)
    {
        var calendar = settings.CalendarImport;
        calendar.DurationRules = new TaskDurationRules(settings.HoursPerDay, settings.DaysPerWeek, settings.DaysPerMonth);
        if (options.TryGetValue("tasks", out var tasks))
            calendar.ImportTasks = tasks.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? true
                : tasks.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException("--tasks debe ser true o false.");
        if (options.TryGetValue("workweeks", out var workWeeks))
            calendar.ApplyWorkWeeks = workWeeks.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? true
                : workWeeks.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException("--workweeks debe ser true o false.");
        if (options.TryGetValue("dependencies", out var dependencies))
            calendar.ImportDependencies = dependencies.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? true
                : dependencies.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException("--dependencies debe ser true o false.");
        if (options.TryGetValue("resources", out var resources))
            calendar.ImportResources = resources.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? true
                : resources.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException("--resources debe ser true o false.");
        if (options.TryGetValue("work", out var work) && work.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            calendar.ImportWork = true;                 // el esfuerzo se fija sobre las tareas con asignaciones: implica --resources true
            calendar.ImportResources = true;
        }
        if (options.TryGetValue("progress", out var progress) && progress.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            calendar.ImportProgress = true;             // el avance se fija después del esfuerzo: implica --work true y --resources true
            calendar.ImportWork = true;
            calendar.ImportResources = true;
        }
        if (options.TryGetValue("summary-dependencies", out var summaryDeps))
            calendar.IncludeSummaryDependencies = summaryDeps.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? true
                : summaryDeps.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException("--summary-dependencies debe ser true o false.");
        if (options.TryGetValue("max-dependencies", out var maxDeps))
            calendar.DependencyMaxCount = int.TryParse(maxDeps, out var nd) && nd >= 1 ? nd : throw new ArgumentException("--max-dependencies debe ser un entero >= 1.");
        if (options.TryGetValue("max-tasks", out var max))
            calendar.TaskMaxCount = int.TryParse(max, out var n) && n >= 1 ? n : throw new ArgumentException("--max-tasks debe ser un entero >= 1.");
        if (options.TryGetValue("task-ids", out var ids))
            calendar.TaskUniqueIds = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var id) ? id : throw new ArgumentException($"--task-ids: '{x}' no es un entero.")).ToList();
        if ((options.ContainsKey("max-tasks") || options.ContainsKey("task-ids") || calendar.ImportDependencies || calendar.ImportResources) && !options.ContainsKey("tasks")) calendar.ImportTasks = true;
    }

    private static WorkbookProject LoadProject(IReadOnlyDictionary<string, string> options, AppSettings settings)
        => new JsonProjectReader().Read(Required(options, "json"), settings);

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Opción no reconocida: {args[i]}");
            var key = args[i][2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Falta valor para --{key}.");
            result[key] = args[++i];
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Falta --{key}.");

    private static void WriteReport(ValidationReport report)
    {
        Console.WriteLine($"Proyecto: {report.ProjectName}");
        foreach (var metric in report.Metrics) Console.WriteLine($"  {metric.Key}: {FormatValue(metric.Value)}");
        foreach (var issue in report.Issues.OrderBy(i => i.Severity).ThenBy(i => i.ExcelRow))
            Console.WriteLine($"[{issue.Severity}] {issue.Code}: {issue.Message}{(issue.ExcelRow.HasValue ? $" (fila {issue.ExcelRow})" : string.Empty)}");
        Console.WriteLine($"Resultado: {(report.HasErrors ? "CON ERRORES" : "APTO PARA IMPORTAR")}");
    }

    private static string FormatValue(object? value)
        => value is null ? "-" : value is string s ? s : JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static int RunSelfTest()
    {
        var settings = new AppSettings();
        Assert(ValueParsers.ParseSpanishDecimal("94.332,47 horas") == 94332.47m, "decimal español");
        Assert(ValueParsers.ParseDurationDays("1.600 horas", settings) == 200m, "duración en horas");
        Assert(ValueParsers.ParseDurationDays("4,5 mss", settings) == 90m, "duración en meses");
        var deps = ValueParsers.ParseDependencies("8FC+10 días;9FF-2 sem.", 10, settings);
        Assert(deps.Count == 2 && deps[0].LagDays == 10m && deps[1].LagDays == -10m, "dependencias");
        var resources = ValueParsers.ParseResources("PEREZ, ANA[50%];MATERIAL[5.000]");
        Assert(resources.Count == 2 && resources[0].SourceName == "PEREZ, ANA" && resources[0].Units == 0.5m && resources[1].Quantity == 5000m, "recursos");
        Console.WriteLine("Self-test correcto.");
        return 0;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test falló: {name}.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
    ProjectOpsExcelImporter (.NET 8)

    Origen de datos (en analyze, validate, plan, resources-template e import):
    --json fichero.json     lee el proyecto desde JSON (mismo formato que genera 'plan')

    Comandos:
    analyze --json plan.json [--config appsettings.json] [--out analysis.json]
    validate --json fichero.json [--config appsettings.json]
    plan --json fichero.json --out import-plan.json [--config appsettings.json]
    resources-template --json fichero.json --out resources.json [--config appsettings.json]
    whoami --config appsettings.json
    calendar-metadata --config appsettings.json [--out calendar-metadata.json]   (solo lectura; documenta metadatos de calendarios)
    calendar-dump --config appsettings.json --template-id <guid> [--out calendar-dump.json]   (solo lectura; vuelca una msdyn_workhourtemplate)
    calendar-save-test --config appsettings.json --calendar-id <guid> --date yyyy-MM-dd [--timezone 95] [--execute]   (EXPERIMENTAL: un día no laborable vía msdyn_SaveCalendar; sin --execute es dry-run)
    assignment-work-test --json fichero.json --config appsettings.json --task-ids 532,533 [--execute true]   (EXPERIMENTAL: esfuerzo de las asignaciones de esas tareas vía msdyn_PssUpdateV2; sin --execute true es dry-run)
    tasks-preview --json fichero.json --config appsettings.json [--max-tasks N] [--task-ids 12,45]   (sin Dataverse: mapeo de tareas y tareas que atraviesan CalendarExceptions)
    import ... [--resources true]   (tras las tareas y dependencias: recursos Work usados como genéricos del proyecto, equipo y asignaciones; solo proyecto, tarea y recurso)
    import ... [--dependencies true] [--max-dependencies N] [--summary-dependencies true]   (tras las tareas crea las dependencias entre ellas y termina; las que enlazan con tareas resumen se omiten salvo --summary-dependencies true)
    import ... [--tasks true] [--max-tasks N] [--task-ids 12,45]   (tras las excepciones crea la estructura básica de tareas y termina; --max-tasks/--task-ids activan --tasks)
    import --json fichero.json --config appsettings.json [--schema project-dump.schema.json] [--resources resources.json] [--state import-state.json]
    validate-schema "<ruta-json>" "<ruta-schema>" ["<log>"]   (solo valida contra JSON Schema 2020-12; sin Dataverse)
    self-test

    La importación usa autenticación delegada (usuario nominativo) y las Project Schedule APIs.
    No guarde secretos en appsettings.json.
    """);
    }
}
