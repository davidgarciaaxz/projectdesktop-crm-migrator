using System.Text;
using System.Text.Json;
using MppReaderTest.Models;

namespace MppReaderTest;

/// <summary>Semicolon separated, UTF-8 (with BOM so Excel detects the encoding), RFC 4180 quoting.</summary>
public static class CsvCustomFieldsWriter
{
    private static readonly string[] CustomHeader =
    {
        "EntityType", "EntityId", "EntityUniqueId", "EntityName", "FieldType", "InternalName", "Alias", "DataType",
        "DotNetType", "Value", "FormattedValue", "IsEnterpriseCustomField", "Formula", "LookupTable"
    };

    private static readonly string[] DynamicHeader =
    {
        "EntityType", "EntityId", "EntityUniqueId", "EntityName", "FieldType", "DataType", "DotNetType", "Value", "FormattedValue"
    };

    public static void Write(IEnumerable<CustomFieldValueDump> values, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(";", CustomHeader));
        foreach (var v in values)
        {
            w.WriteLine(Row(v.EntityType, v.EntityId?.ToString(), v.EntityUniqueId?.ToString(), v.EntityName, v.FieldType, v.InternalName,
                v.Alias, v.DataType, v.DotNetType, RawText(v.Value), v.FormattedValue, v.IsEnterpriseCustomField ? "true" : "false", v.Formula, v.LookupTable));
        }
    }

    public static void WriteDynamic(IEnumerable<DynamicFieldValueDump> values, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(";", DynamicHeader));
        foreach (var v in values)
        {
            w.WriteLine(Row(v.EntityType, v.EntityId?.ToString(), v.EntityUniqueId?.ToString(), v.EntityName, v.FieldType, v.DataType,
                v.DotNetType, RawText(v.Value), v.FormattedValue));
        }
    }

    private static string? RawText(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime dt => dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, JsonReportWriter.Options).Replace("\r", "").Replace("\n", "")
    };

    private static string Row(params string?[] fields) => string.Join(";", fields.Select(Escape));

    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        var needsQuotes = field.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0;
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
