using System.Text;
using MppReaderTest;
using MppReaderTest.Models;

const int ExitOk = 0;
const int ExitBadArguments = 1;
const int ExitFileNotFound = 2;
const int ExitReadError = 3;
const int ExitOutputError = 4;
const int ExitUnexpected = 5;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
    var flags = new HashSet<string>(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase);
    var unknownFlags = flags.Where(f => f is not ("--no-dynamic" or "--dynamic-json" or "--help")).ToList();

    if (flags.Contains("--help") || positional.Count == 0 || positional.Count > 2 || unknownFlags.Count > 0)
    {
        if (unknownFlags.Count > 0) Console.Error.WriteLine($"Opción desconocida: {string.Join(", ", unknownFlags)}");
        PrintUsage();
        return flags.Contains("--help") && positional.Count == 0 && unknownFlags.Count == 0 ? ExitOk : ExitBadArguments;
    }

    var mppPath = Path.GetFullPath(positional[0]);
    var outputDir = Path.GetFullPath(positional.Count > 1 ? positional[1] : Path.Combine(Environment.CurrentDirectory, "output"));
    var dynamicDump = !flags.Contains("--no-dynamic");
    var dynamicInJson = dynamicDump && flags.Contains("--dynamic-json");

    Console.WriteLine($"Leyendo '{mppPath}' con MPXJ.Net {ProjectInspector.MpxjVersion()}...");
    var read = MppProjectReader.Read(mppPath);
    switch (read.Failure)
    {
        case ReadFailureKind.InvalidArguments:
            Console.Error.WriteLine($"ERROR: {read.Message}");
            PrintUsage();
            return ExitBadArguments;
        case ReadFailureKind.FileNotFound:
            Console.Error.WriteLine($"ERROR: {read.Message}");
            return ExitFileNotFound;
        case ReadFailureKind.ReadError:
            Console.Error.WriteLine($"ERROR: {read.Message}");
            return ExitReadError;
    }

    ProjectDump dump;
    try
    {
        dump = new ProjectInspector(read.Project!, mppPath, read.ReaderName, dynamicDump).Inspect();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: fallo inesperado analizando el proyecto leído: {ex}");
        return ExitUnexpected;
    }

    var txtPath = Path.Combine(outputDir, "project-dump.txt");
    var jsonPath = Path.Combine(outputDir, "project-dump.json");
    var csvPath = Path.Combine(outputDir, "custom-fields.csv");
    var dynCsvPath = Path.Combine(outputDir, "dynamic-fields.csv");
    try
    {
        Directory.CreateDirectory(outputDir);
        dump.DynamicFields.ValuesIncludedInJson = dynamicInJson;
        using (var tee = new TeeWriter(txtPath))
        {
            ConsoleReportWriter.Write(dump, tee);
        }
        CsvCustomFieldsWriter.Write(dump.CustomFieldValues, csvPath);
        if (dynamicDump) CsvCustomFieldsWriter.WriteDynamic(dump.DynamicFieldValues, dynCsvPath);
        // Individual dynamic values can reach hundreds of thousands of rows; by default they only go to the CSV.
        if (!dynamicInJson) dump.DynamicFieldValues = new List<DynamicFieldValueDump>();
        JsonReportWriter.Write(dump, jsonPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
    {
        Console.Error.WriteLine($"ERROR generando los ficheros de salida en '{outputDir}': {ex.GetType().Name}: {ex.Message}");
        return ExitOutputError;
    }

    Console.WriteLine();
    Console.WriteLine("=== OUTPUT FILES ===");
    Console.WriteLine($"  {txtPath}");
    Console.WriteLine($"  {jsonPath}");
    Console.WriteLine($"  {csvPath}");
    if (dynamicDump) Console.WriteLine($"  {dynCsvPath}");
    return ExitOk;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR inesperado: {ex}");
    return ExitUnexpected;
}

static void PrintUsage()
{
    Console.WriteLine("Uso:");
    Console.WriteLine("  dotnet run -- \"<ruta al fichero .mpp>\" [\"<carpeta de salida>\"] [--no-dynamic] [--dynamic-json]");
    Console.WriteLine();
    Console.WriteLine("  <carpeta de salida>  Opcional. Por defecto: .\\output en la carpeta actual.");
    Console.WriteLine("  --no-dynamic         Desactiva el volcado dinámico de todos los FieldType (diagnóstico).");
    Console.WriteLine("  --dynamic-json       Incluye también cada valor del volcado dinámico en project-dump.json (puede ocupar cientos de MB).");
    Console.WriteLine();
    Console.WriteLine("Códigos de salida: 0 OK, 1 argumentos, 2 fichero no encontrado, 3 error de lectura MPP, 4 error de salida, 5 inesperado.");
}
