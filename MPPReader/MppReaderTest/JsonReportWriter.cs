using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MppReaderTest.Models;

namespace MppReaderTest;

public static class JsonReportWriter
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keeps accented characters (SÁBANA, FACTURACIÓN...) readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Serializes only the plain dump models (never the MPXJ ProjectFile). DateTime values use ISO 8601.</summary>
    public static void Write(ProjectDump dump, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = Options.Encoder });
        JsonSerializer.Serialize(writer, dump, Options);
    }
}
