using System.Globalization;
using System.Text.RegularExpressions;

namespace ProjectOpsExcelImporter;

public static partial class ValueParsers
{
    [GeneratedRegex(@"[-+]?\d[\d.]*?(?:,\d+)?(?=\s|$|\?|%|\])", RegexOptions.CultureInvariant)]
    private static partial Regex SpanishNumberRegex();

    [GeneratedRegex(@"(?<id>\d+)\s*(?<type>FC|CC|FF|CF)?\s*(?<lag>[+-].+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DependencyRegex();

    [GeneratedRegex(@"^(?<name>.*?)[_ ]*\[\s*(?<value>[0-9.,]+)\s*%\s*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePercentRegex();

    [GeneratedRegex(@"^(?<name>.*?)\[\s*(?<value>[0-9.]+(?:,[0-9]+)?)\s*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceQuantityRegex();

    [GeneratedRegex(@"(?<day>\d{1,2})/(?<month>\d{1,2})/(?<year>\d{2,4})", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    public static decimal ParseSpanishDecimal(object? value)
    {
        if (value is null) return 0m;
        if (value is decimal d) return d;
        if (value is double dbl) return Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
        if (value is float f) return Convert.ToDecimal(f, CultureInfo.InvariantCulture);
        if (value is int i) return i;
        if (value is long l) return l;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var match = SpanishNumberRegex().Match(text);
        if (!match.Success) return 0m;
        var number = match.Value.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(number, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }

    public static DateOnly? ParseDate(object? value)
    {
        if (value is DateTime dt) return DateOnly.FromDateTime(dt);
        if (value is DateTimeOffset dto) return DateOnly.FromDateTime(dto.DateTime);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = DateRegex().Match(text);
        if (!m.Success) return null;
        var day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups["month"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (year < 100) year += 2000;
        return DateOnly.TryParseExact($"{year:0000}-{month:00}-{day:00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : null;
    }

    public static decimal ParseDurationDays(object? value, AppSettings settings)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var amount = ParseSpanishDecimal(text);
        var normalized = text.ToLowerInvariant();
        if (normalized.Contains("hora", StringComparison.Ordinal)) return amount / settings.HoursPerDay;
        if (normalized.Contains("sem", StringComparison.Ordinal)) return amount * settings.DaysPerWeek;
        if (normalized.Contains("mes", StringComparison.Ordinal) || normalized.Contains(" ms", StringComparison.Ordinal) || normalized.Contains("mss", StringComparison.Ordinal))
            return amount * settings.DaysPerMonth;
        return amount;
    }

    public static decimal ParseWorkHours(object? value) => ParseSpanishDecimal(value);

    public static IReadOnlyList<TaskDependency> ParseDependencies(object? value, int successorId, AppSettings settings)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<TaskDependency>();
        foreach (var token in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DependencyRegex().Match(token);
            if (!match.Success) throw new FormatException($"Dependencia no reconocida: '{token}'.");
            var predecessor = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            var type = match.Groups["type"].Value.ToUpperInvariant() switch
            {
                "" or "FC" => DependencyType.FinishToStart,
                "CC" => DependencyType.StartToStart,
                "FF" => DependencyType.FinishToFinish,
                "CF" => DependencyType.StartToFinish,
                var t => throw new FormatException($"Tipo de dependencia no soportado: '{t}'.")
            };
            var lagRaw = match.Groups["lag"].Value.Trim();
            var lag = string.IsNullOrWhiteSpace(lagRaw) ? 0m : ParseDurationDays(lagRaw, settings);
            if (lagRaw.StartsWith("-", StringComparison.Ordinal)) lag = -Math.Abs(lag);
            result.Add(new TaskDependency(predecessor, successorId, type, lag, token));
        }
        return result;
    }

    public static IReadOnlyList<TaskResource> ParseResources(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<TaskResource>();
        foreach (var token in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var percent = ResourcePercentRegex().Match(token);
            if (percent.Success)
            {
                var name = NormalizeResourceName(percent.Groups["name"].Value);
                result.Add(new TaskResource(name, ParseSpanishDecimal(percent.Groups["value"].Value) / 100m, null, token));
                continue;
            }
            var quantity = ResourceQuantityRegex().Match(token);
            if (quantity.Success)
            {
                var name = NormalizeResourceName(quantity.Groups["name"].Value);
                result.Add(new TaskResource(name, null, ParseSpanishDecimal(quantity.Groups["value"].Value), token));
                continue;
            }
            result.Add(new TaskResource(NormalizeResourceName(token), null, null, token));
        }
        return result;
    }

    private static string NormalizeResourceName(string name) => name.Trim().TrimEnd('_').Trim();
}

public static class DeterministicGuid
{
    public static Guid Create(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var data = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, data, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, data, namespaceBytes.Length, nameBytes.Length);
        var hash = System.Security.Cryptography.SHA1.HashData(data);
        var newGuid = new byte[16];
        Array.Copy(hash, 0, newGuid, 0, 16);
        newGuid[6] = (byte)((newGuid[6] & 0x0F) | 0x50);
        newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);
        SwapByteOrder(newGuid);
        return new Guid(newGuid);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
