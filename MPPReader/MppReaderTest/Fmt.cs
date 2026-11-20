using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using MPXJ.Net;
using MppReaderTest.Models;
using Task = MPXJ.Net.Task;

namespace MppReaderTest;

/// <summary>
/// Formatting helpers. Values are never rounded: doubles use the round-trip ("R") format.
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Null => "(null)";

    public static string Num(double? value) => value.HasValue ? value.Value.ToString("R", Inv) : Null;

    public static string Int(int? value) => value.HasValue ? value.Value.ToString(Inv) : Null;

    public static string Date(DateTime? value) => value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", Inv) : Null;

    public static string Date(DateOnly? value) => value.HasValue ? value.Value.ToString("yyyy-MM-dd", Inv) : Null;

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Str(string? value) => string.IsNullOrEmpty(value) ? Null : value;

    public static DurationDump? ToDump(Duration? d) =>
        d == null ? null : new DurationDump { Value = d.DurationValue, Units = d.Units?.ToString(), Text = Dur(d) };

    public static RateDump? ToDump(Rate? r) =>
        r == null ? null : new RateDump { Amount = r.Amount, Units = r.Units?.ToString(), Text = RateText(r) };

    public static string Dur(Duration? d) => d == null ? Null : $"{d.DurationValue.ToString("R", Inv)} {UnitAbbreviation(d.Units)}";

    public static string Dur(DurationDump? d) => d == null ? Null : d.Text;

    public static string RateText(Rate? r) => r == null ? Null : $"{r.Amount.ToString("R", Inv)}/{UnitAbbreviation(r.Units)}";

    public static string RateText(RateDump? r) => r == null ? Null : r.Text;

    public static string UnitAbbreviation(TimeUnit? unit) => unit switch
    {
        null => "?",
        TimeUnit.Minutes => "m",
        TimeUnit.Hours => "h",
        TimeUnit.Days => "d",
        TimeUnit.Weeks => "w",
        TimeUnit.Months => "mo",
        TimeUnit.Years => "y",
        TimeUnit.Percent => "%",
        TimeUnit.ElapsedMinutes => "em",
        TimeUnit.ElapsedHours => "eh",
        TimeUnit.ElapsedDays => "ed",
        TimeUnit.ElapsedWeeks => "ew",
        TimeUnit.ElapsedMonths => "emo",
        TimeUnit.ElapsedYears => "ey",
        TimeUnit.ElapsedPercent => "e%",
        _ => unit.Value.ToString()
    };

    public static string Period(TimeOnlyRange? range)
    {
        if (range == null) return Null;
        var start = range.Start?.ToString("HH:mm", Inv) ?? "?";
        // MPXJ represents an end of day as 00:00.
        var end = range.End.HasValue
            ? (range.End.Value == TimeOnly.MinValue && range.Start != TimeOnly.MinValue ? "24:00" : range.End.Value.ToString("HH:mm", Inv))
            : "?";
        return $"{start}-{end}";
    }

    public static string RelationTypeName(RelationType? type) => type switch
    {
        RelationType.FinishStart => "Finish-to-Start",
        RelationType.StartStart => "Start-to-Start",
        RelationType.FinishFinish => "Finish-to-Finish",
        RelationType.StartFinish => "Start-to-Finish",
        _ => Null
    };

    public static string RelationAbbreviation(RelationType? type) => type switch
    {
        RelationType.FinishStart => "FS",
        RelationType.StartStart => "SS",
        RelationType.FinishFinish => "FF",
        RelationType.StartFinish => "SF",
        _ => "?"
    };

    public static string LagText(Duration? lag)
    {
        if (lag == null || lag.DurationValue == 0) return "";
        var sign = lag.DurationValue > 0 ? "+" : "-";
        return $"{sign}{Math.Abs(lag.DurationValue).ToString("R", Inv)}{UnitAbbreviation(lag.Units)}";
    }

    /// <summary>Equivalent of the MS Project "Predecessors" column entry, e.g. 12FS+2d or 12.</summary>
    public static string PredecessorNotation(int? otherTaskId, RelationType? type, Duration? lag)
    {
        var lagText = LagText(lag);
        var id = otherTaskId?.ToString(Inv) ?? "?";
        if (type == RelationType.FinishStart && lagText.Length == 0) return id;
        return $"{id}{RelationAbbreviation(type)}{lagText}";
    }

    public static string Truncate(string? value, int max)
    {
        if (value == null) return Null;
        var single = value.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single[..max] + "...";
    }

    /// <summary>Human readable formatting for any value returned by MPXJ.Net.</summary>
    public static string Format(object? value)
    {
        switch (value)
        {
            case null: return Null;
            case string s: return s;
            case DateTime dt: return Date(dt);
            case DateOnly d: return Date(d);
            case TimeOnly t: return t.ToString("HH:mm:ss", Inv);
            case double dbl: return dbl.ToString("R", Inv);
            case float f: return f.ToString("R", Inv);
            case decimal m: return m.ToString(Inv);
            case bool b: return Bool(b);
            case Enum e: return e.ToString();
            case Duration du: return Dur(du);
            case Rate r: return RateText(r);
            case Priority p: return p.Value.ToString(Inv);
            case Task task: return $"Task[ID={Int(task.ID)}, UID={Int(task.UniqueID)}, {task.Name}]";
            case Resource res: return $"Resource[ID={Int(res.ID)}, UID={Int(res.UniqueID)}, {res.Name}]";
            case ProjectCalendar cal: return $"Calendar[UID={Int(cal.UniqueID)}, {cal.Name}]";
            case Relation rel:
                return $"{PredecessorNotation(rel.PredecessorTask?.ID, rel.Type, rel.Lag)}->{Int(rel.SuccessorTask?.ID)}";
            case Notes n: return n.ToString();
            case IFieldType ft: return FieldCatalog.Ident(ft);
            case IConvertible c: return c.ToString(Inv);
            case IEnumerable seq:
                {
                    var sb = new StringBuilder("[");
                    var i = 0;
                    foreach (var item in seq)
                    {
                        if (i > 0) sb.Append(", ");
                        if (i == 200) { sb.Append("..."); break; }
                        sb.Append(Format(item));
                        i++;
                    }
                    return sb.Append(']').ToString();
                }
            default: return value.ToString() ?? "";
        }
    }

    /// <summary>Converts an MPXJ value to something System.Text.Json can serialize without touching MPXJ object graphs.</summary>
    public static object? ToJsonValue(object? value) => value switch
    {
        null => null,
        string or bool or int or long or short or byte or decimal or DateTime or DateOnly or TimeOnly or Guid => value,
        double d => double.IsFinite(d) ? d : d.ToString(Inv),
        float f => float.IsFinite(f) ? (double)f : f.ToString(Inv),
        Enum e => e.ToString(),
        Duration du => ToDump(du),
        Rate r => ToDump(r),
        Priority p => p.Value,
        _ => Format(value)
    };

    public static string? DotNetType(object? value) => value?.GetType().Name;

    public static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        ICollection c => c.Count == 0,
        _ => false
    };

    public static bool IsZero(object? value) => value switch
    {
        double d => d == 0,
        float f => f == 0,
        int i => i == 0,
        long l => l == 0,
        decimal m => m == 0,
        Duration du => du.DurationValue == 0,
        Rate r => r.Amount == 0,
        _ => false
    };

    public static string RateTableLetter(int index) => index is >= 0 and < 5 ? ((char)('A' + index)).ToString() : index.ToString(Inv);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}

/// <summary>
/// Resolves the public static member name (e.g. TaskField.Baseline6Cost) of an MPXJ.Net field type.
/// Reflection is limited to the static FieldType constants exposed by MPXJ.Net.
/// </summary>
public static class FieldCatalog
{
    private static readonly Dictionary<string, string> Names = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(TaskField), typeof(ResourceField), typeof(AssignmentField), typeof(ProjectField), typeof(ConstraintField) })
        {
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != type) continue;
                if (f.GetValue(null) is IFieldType ft) map.TryAdd(Key(ft), $"{type.Name}.{f.Name}");
            }
        }
        return map;
    }

    public static string Key(IFieldType ft) => $"{ft.FieldTypeClass}:{ft.FieldName}";

    public static string Ident(IFieldType? ft)
    {
        if (ft == null) return Fmt.Null;
        return Names.TryGetValue(Key(ft), out var name) ? name : $"{ft.FieldTypeClass}Field.{ft.FieldName}";
    }

    /// <summary>Member name without the class prefix, e.g. "Cost4".</summary>
    public static string MemberName(IFieldType ft)
    {
        var ident = Ident(ft);
        var dot = ident.IndexOf('.');
        return dot >= 0 ? ident[(dot + 1)..] : ident;
    }
}
