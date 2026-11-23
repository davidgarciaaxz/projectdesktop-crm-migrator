using System.Globalization;

namespace ProjectOpsExcelImporter.Calendars;

/// <summary>Códigos de salida de las fases de calendario del comando import.</summary>
public static class ImportExitCodes
{
    public const int Ok = 0;
    public const int Functional = 1;
    public const int Configuration = 2;
    public const int Connection = 3;
    public const int CalendarTemplate = 4;
    public const int ProjectCreation = 5;
    public const int ProjectCalendar = 6;
    public const int WorkWeeks = 7;
    public const int CalendarExceptions = 8;
    public const int Tasks = 9;
    public const int Dependencies = 10;
    public const int Resources = 11;
}

public readonly record struct NormalizedTimePeriod(int StartMinute, int EndMinute)
{
    public static NormalizedTimePeriod Parse(string text, string context)
    {
        var parts = text.Trim().Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryParseTime(parts[0], out var start) || !TryParseTime(parts[1], out var end))
            throw new CalendarImportException(ImportExitCodes.Functional, "CalendarResolution", $"Periodo '{text}' no válido en {context}. Formato esperado HH:mm-HH:mm.");
        if (start >= end)
            throw new CalendarImportException(ImportExitCodes.Functional, "CalendarResolution", $"Periodo '{text}' no válido en {context}: la hora inicial debe ser anterior a la final.");
        return new NormalizedTimePeriod(start, end);
    }

    private static bool TryParseTime(string text, out int minutes)
    {
        minutes = 0;
        var p = text.Split(':');
        if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (m is < 0 or > 59 || h is < 0 or > 24 || (h == 24 && m != 0)) return false;
        minutes = h * 60 + m;
        return true;
    }

    /// <summary>HH:mm-HH:mm (24 horas).</summary>
    public override string ToString() => $"{Format(StartMinute)}-{Format(EndMinute)}";

    /// <summary>Forma compacta para nombres: "09-13" o "08-13:30".</summary>
    public string ToCompact() => $"{FormatCompact(StartMinute)}-{FormatCompact(EndMinute)}";

    private static string Format(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
    private static string FormatCompact(int minutes) => minutes % 60 == 0 ? $"{minutes / 60:00}" : Format(minutes);
}

/// <summary>Inherit = hereda el horario semanal base; NonWorking = día no laborable; Working = horario explícito.</summary>
public enum DayScheduleKind { Inherit, NonWorking, Working }

public sealed record NormalizedCalendarDay(DayOfWeek Day, DayScheduleKind Kind, IReadOnlyList<NormalizedTimePeriod> Periods)
{
    public string Text => Kind switch
    {
        DayScheduleKind.Inherit => "INHERIT",
        DayScheduleKind.NonWorking => "OFF",
        _ => string.Join("|", Periods)
    };
}

/// <summary>Horario semanal base normalizado + representación canónica y fingerprint (SHA-256).</summary>
public sealed record NormalizedCalendar(
    IReadOnlyList<NormalizedCalendarDay> Days,
    string CanonicalPattern,
    string Fingerprint,
    string DisplayPattern);

public sealed record NormalizedWorkWeek(string? Name, DateOnly From, DateOnly To, IReadOnlyList<NormalizedCalendarDay> Days)
{
    /// <summary>Clave estable (sin nombre) para idempotencia.</summary>
    public string Key => $"WW|{From:yyyy-MM-dd}|{To:yyyy-MM-dd}|" + string.Join(";", Days.Select(d => $"{CalendarCodes.Code(d.Day)}={d.Text}"));
}

public sealed record NormalizedCalendarException(
    int CalendarUniqueId,
    string? SourceName,
    string? RecurrenceRule,
    DateOnly From,
    DateOnly To,
    bool Working,
    IReadOnlyList<NormalizedTimePeriod> Periods,
    bool IsExpanded)
{
    /// <summary>Clave de idempotencia sin el id del calendario del proyecto (constante dentro de un state).</summary>
    public string Key => $"EX|{From:yyyy-MM-dd}|{To:yyyy-MM-dd}|{Working}|{string.Join("|", Periods)}";
    public string KeyFor(Guid projectCalendarId) => $"{projectCalendarId:D}|{Key}";
}

public sealed record CalendarTemplateResolution(Guid Id, string Name, string Fingerprint, bool Created, string Result, string? Warning = null);

public sealed record ProjectCreationResult(
    Guid ProjectId,
    string ProjectName,
    Guid CalendarTemplateId,
    string CalendarTemplateName,
    string CalendarFingerprint,
    Guid? DefaultBucketId,
    Guid? ProjectCalendarId,
    string CreationMethod,
    bool Created);

public sealed class StageCounters
{
    public int Found { get; set; }
    public int Created { get; set; }
    public int Reused { get; set; }
    public int Failed { get; set; }
}

public sealed record CalendarImportResult(
    bool Success,
    Guid ProjectId,
    string ProjectName,
    Guid CalendarTemplateId,
    string CalendarTemplateName,
    string CalendarFingerprint,
    Guid? DefaultBucketId,
    Guid ProjectCalendarId,
    StageCounters WorkWeeks,
    StageCounters Exceptions);

public static class CalendarCodes
{
    public static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static string Code(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON", DayOfWeek.Tuesday => "TUE", DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU", DayOfWeek.Friday => "FRI", DayOfWeek.Saturday => "SAT", _ => "SUN"
    };

    /// <summary>Letra española usada en los nombres de plantilla (L M X J V S D).</summary>
    public static char SpanishLetter(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 'L', DayOfWeek.Tuesday => 'M', DayOfWeek.Wednesday => 'X',
        DayOfWeek.Thursday => 'J', DayOfWeek.Friday => 'V', DayOfWeek.Saturday => 'S', _ => 'D'
    };

    /// <summary>Rango de fechas aceptado para WorkWeeks (supuesto conservador, pendiente de verificar contra el entorno).</summary>
    public static readonly DateOnly MinDate = new(1900, 1, 1);
    public static readonly DateOnly MaxDate = new(2100, 12, 31);
}
