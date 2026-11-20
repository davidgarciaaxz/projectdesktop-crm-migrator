using System.Text.Json;

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
            var options = ParseOptions(args.Skip(1).ToArray());
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

    /// <summary>
    /// Elige el origen de datos: --source excel|json (opcional) con --excel o --json.
    /// Si no se indica --source, se deduce de qué opción de fichero se ha pasado.
    /// </summary>
    private static WorkbookProject LoadProject(IReadOnlyDictionary<string, string> options, AppSettings settings)
    {
        options.TryGetValue("excel", out var excelPath);
        options.TryGetValue("json", out var jsonPath);
        options.TryGetValue("source", out var source);

        if (string.IsNullOrWhiteSpace(source))
        {
            if (!string.IsNullOrWhiteSpace(excelPath) && !string.IsNullOrWhiteSpace(jsonPath))
                throw new ArgumentException("Indica solo uno de --excel o --json (o elige con --source excel|json).");
            if (string.IsNullOrWhiteSpace(excelPath) && string.IsNullOrWhiteSpace(jsonPath))
                throw new ArgumentException("Falta el origen de datos: usa --excel <fichero.xlsx> o --json <fichero.json>.");
            source = string.IsNullOrWhiteSpace(jsonPath) ? "excel" : "json";
        }

        return source.ToLowerInvariant() switch
        {
            "excel" => new ExcelProjectReader().Read(
                !string.IsNullOrWhiteSpace(excelPath) ? excelPath : throw new ArgumentException("Falta --excel."), settings),
            "json" => new JsonProjectReader().Read(
                !string.IsNullOrWhiteSpace(jsonPath) ? jsonPath : throw new ArgumentException("Falta --json."), settings),
            _ => throw new ArgumentException($"Origen desconocido '{source}'. Valores válidos: excel, json.")
        };
    }

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
    --excel fichero.xlsx    lee el proyecto desde Excel
    --json fichero.json     lee el proyecto desde JSON (mismo formato que genera 'plan')
    --source excel|json     opcional; por defecto se deduce de --excel / --json

    Comandos:
    analyze (--excel ProyectoPlantilla.xlsx | --json plan.json) [--config appsettings.json] [--out analysis.json]
    validate (--excel ... | --json ...) [--config appsettings.json]
    plan (--excel ... | --json ...) --out import-plan.json [--config appsettings.json]
    resources-template (--excel ... | --json ...) --out resources.json [--config appsettings.json]
    whoami --config appsettings.json
    import (--excel ... | --json ...) --config appsettings.json [--resources resources.json] [--state import-state.json]
    self-test

    La importación usa autenticación delegada (usuario nominativo) y las Project Schedule APIs.
    No guarde secretos en appsettings.json.
    """);
    }
}
