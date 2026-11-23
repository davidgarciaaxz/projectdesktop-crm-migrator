using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace ProjectOpsExcelImporter;

/// <summary>
/// Valida un fichero JSON contra un JSON Schema (Draft 2020-12). No usa Dataverse ni la lógica de importación.
/// </summary>
public static class SchemaValidator
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitInvalid = 2;

    public static int Run(string jsonPath, string schemaPath, string? logPath = null)
    {
        var log = new List<string>();
        var code = RunCore(jsonPath, schemaPath, log);
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var status = code switch { ExitOk => "VÁLIDO", ExitInvalid => "INVÁLIDO (no cumple el schema)", _ => "ERROR (no se pudo validar)" };
            var header = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {status}: {Path.GetFileName(jsonPath)} | JSON={Path.GetFullPath(jsonPath)} SCHEMA={Path.GetFullPath(schemaPath)} EXIT={code}";
            File.AppendAllLines(logPath, new[] { header }.Concat(log).Append(string.Empty));
        }
        return code;
    }

    private static int RunCore(string jsonPath, string schemaPath, List<string> log)
    {
        // 1) Existencia de ficheros
        if (!File.Exists(jsonPath))
        {
            Err(log, $"ERROR: no existe el fichero JSON: {Path.GetFullPath(jsonPath)}");
            return ExitError;
        }
        if (!File.Exists(schemaPath))
        {
            Err(log, $"ERROR: no existe el fichero de schema: {Path.GetFullPath(schemaPath)}");
            return ExitError;
        }

        // 2) JSON de datos sintácticamente válido
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(File.ReadAllText(jsonPath), documentOptions: new JsonDocumentOptions());
        }
        catch (JsonException ex)
        {
            Err(log, $"ERROR: el JSON no es sintácticamente válido ({Path.GetFileName(jsonPath)}): {ex.Message}");
            return ExitError;
        }

        // 3) Schema: JSON válido
        JsonNode? schemaNode;
        try
        {
            schemaNode = JsonNode.Parse(File.ReadAllText(schemaPath));
        }
        catch (JsonException ex)
        {
            Err(log, $"ERROR: el schema no es un JSON válido ({Path.GetFileName(schemaPath)}): {ex.Message}");
            return ExitError;
        }
        if (schemaNode is null)
        {
            Console.Error.WriteLine("ERROR: el schema está vacío (null).");
            return ExitError;
        }

        // 4) Schema: interpretable como JSON Schema 2020-12 (validación contra el metaschema)
        try
        {
            var metaResult = MetaSchemas.Draft202012.Evaluate(schemaNode, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!metaResult.IsValid)
            {
                Console.Error.WriteLine("ERROR: el schema no es un JSON Schema Draft 2020-12 válido:");
                WriteErrors(metaResult, schemaNode, log);
                return ExitError;
            }
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException)
        {
            Err(log, $"ERROR: el schema no puede interpretarse como JSON Schema: {ex.Message}");
            return ExitError;
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaNode.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException)
        {
            Err(log, $"ERROR: el schema no puede interpretarse como JSON Schema: {ex.Message}");
            return ExitError;
        }

        // 5) Validación completa de los datos
        EvaluationResults result;
        try
        {
            result = schema.Evaluate(data, new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        catch (Exception ex) when (ex is JsonSchemaException or InvalidOperationException)
        {
            Err(log, $"ERROR: fallo al evaluar el schema: {ex.Message}");
            return ExitError;
        }

        if (result.IsValid)
        {
            Console.WriteLine($"OK: {Path.GetFileName(jsonPath)} es válido contra {Path.GetFileName(schemaPath)} (Draft 2020-12).");
            return ExitOk;
        }

        Err(log, $"INVÁLIDO: {Path.GetFileName(jsonPath)} no cumple {Path.GetFileName(schemaPath)}:");
        var count = WriteErrors(result, data, log);
        Err(log, $"Total de errores: {count}");
        return ExitInvalid;
    }

    private static int WriteErrors(EvaluationResults result, JsonNode? instance, List<string> log)
    {
        var count = 0;
        foreach (var detail in result.Details.Where(d => !d.IsValid && d.Errors is { Count: > 0 }))
        {
            foreach (var error in detail.Errors!)
            {
                count++;
                var location = detail.InstanceLocation.ToString();
                var value = DescribeValue(instance, location);
                var line = $"  [{(string.IsNullOrEmpty(location) ? "#" : location)}] {error.Key}: {error.Value}  | valor: {value}  | schema: {detail.SchemaLocation}";
                Console.Error.WriteLine(line);
                log.Add(line);
            }
        }
        if (count == 0 && !result.IsValid) { count = 1; Console.Error.WriteLine("  El documento no cumple el schema."); }
        return count;
    }

    private static void Err(List<string> log, string msg) { Console.Error.WriteLine(msg); log.Add(msg); }

    private static string DescribeValue(JsonNode? root, string pointer)
    {
        try
        {
            var node = root;
            foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
                node = node switch
                {
                    JsonObject o => o.TryGetPropertyValue(seg, out var v) ? v : null,
                    JsonArray arr when int.TryParse(seg, out var i) && i >= 0 && i < arr.Count => arr[i],
                    _ => null
                };
                if (node is null) return "null/(no existe)";
            }
            var text = node?.ToJsonString() ?? "null";
            return text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (Exception) { return "?"; }
    }
}
