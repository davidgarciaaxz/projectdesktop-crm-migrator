using System.Security.Cryptography;
using System.Text;

namespace ProjectOpsExcelImporter.Calendars;

public static class CalendarFingerprintService
{
    /// <summary>SHA-256 (hex mayúsculas) de la representación canónica.</summary>
    public static string Compute(string canonicalPattern)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPattern)));
}

public static class MppCalendarNormalizer
{
    private const string Stage = "CalendarResolution";

    /// <summary>Horario semanal base. Ignora nombre, UniqueId, WorkWeeks y excepciones.</summary>
    public static NormalizedCalendar NormalizeBase(MppCalendar calendar)
    {
        var days = new List<NormalizedCalendarDay>();
        foreach (var dow in CalendarCodes.WeekOrder)
        {
            var day = FindDay(calendar.Days, dow, $"calendario '{calendar.Name}'");
            var working = day.EffectiveWorking ?? day.DefinedDayType switch
            {
                "Working" => true,
                "NonWorking" => false,
                _ => throw Functional($"El día {dow} del calendario '{calendar.Name}' no indica si es laborable (EffectiveWorking/DefinedDayType).")
            };
            if (!working)
            {
                if (day.EffectivePeriods.Count > 0)
                    throw Functional($"El día {dow} del calendario '{calendar.Name}' es no laborable pero tiene periodos.");
                days.Add(new NormalizedCalendarDay(dow, DayScheduleKind.NonWorking, []));
                continue;
            }
            var periods = ParsePeriods(day.EffectivePeriods, $"{dow} del calendario '{calendar.Name}'");
            if (periods.Count == 0)
                throw Functional($"El día {dow} del calendario '{calendar.Name}' es laborable pero no tiene periodos; no se infiere ningún horario.");
            days.Add(new NormalizedCalendarDay(dow, DayScheduleKind.Working, periods));
        }
        var canonical = Canonical(days);
        return new NormalizedCalendar(days, canonical, CalendarFingerprintService.Compute(canonical), Display(days));
    }

    public static string Canonical(IEnumerable<NormalizedCalendarDay> days)
        => string.Join(";", days.Select(d => $"{CalendarCodes.Code(d.Day)}={d.Text}"));

    /// <summary>Ej.: "MON-FRI 09:00-13:00 | 15:00-19:00; SAT-SUN OFF".</summary>
    public static string Display(IReadOnlyList<NormalizedCalendarDay> days)
    {
        var groups = new List<(DayOfWeek From, DayOfWeek To, string Text)>();
        foreach (var d in days)
        {
            var text = d.Kind == DayScheduleKind.Working ? string.Join(" | ", d.Periods) : d.Kind == DayScheduleKind.Inherit ? "INHERIT" : "OFF";
            if (groups.Count > 0 && groups[^1].Text == text) groups[^1] = (groups[^1].From, d.Day, text);
            else groups.Add((d.Day, d.Day, text));
        }
        return string.Join("; ", groups.Select(g => (g.From == g.To ? CalendarCodes.Code(g.From) : $"{CalendarCodes.Code(g.From)}-{CalendarCodes.Code(g.To)}") + " " + g.Text));
    }

    /// <summary>
    /// WorkWeeks del calendario, en orden cronológico. Distingue herencia, no laborable y horario explícito.
    /// Duplicados exactos se descartan con advertencia; solapes distintos son un error.
    /// </summary>
    public static IReadOnlyList<NormalizedWorkWeek> NormalizeWorkWeeks(MppCalendar calendar, List<string> warnings)
    {
        var result = new List<NormalizedWorkWeek>();
        foreach (var ww in calendar.WorkWeeks)
        {
            var label = $"WorkWeek '{ww.Name}' ({ww.From:yyyy-MM-dd}..{ww.To:yyyy-MM-dd})";
            if (ww.From > ww.To) throw WorkWeekError($"{label}: la fecha inicial es posterior a la final.");
            if (ww.From < CalendarCodes.MinDate || ww.To > CalendarCodes.MaxDate)
                throw WorkWeekError($"{label}: fuera del rango soportado {CalendarCodes.MinDate:yyyy-MM-dd}..{CalendarCodes.MaxDate:yyyy-MM-dd}.");
            var days = new List<NormalizedCalendarDay>();
            foreach (var dow in CalendarCodes.WeekOrder)
            {
                var day = FindDay(ww.Days, dow, label, ImportExitCodes.WorkWeeks);
                days.Add(NormalizeWorkWeekDay(day, dow, label));
            }
            result.Add(new NormalizedWorkWeek(ww.Name, ww.From, ww.To, days));
        }

        var ordered = new List<NormalizedWorkWeek>();
        foreach (var ww in result.OrderBy(w => w.From).ThenBy(w => w.To))
        {
            if (ordered.Any(o => o.Key == ww.Key))
            {
                warnings.Add($"WorkWeek duplicada omitida: '{ww.Name}' {ww.From:yyyy-MM-dd}..{ww.To:yyyy-MM-dd}.");
                continue;
            }
            var overlap = ordered.FirstOrDefault(o => o.From <= ww.To && ww.From <= o.To);
            if (overlap is not null)
                throw WorkWeekError($"WorkWeeks solapadas: '{overlap.Name}' ({overlap.From:yyyy-MM-dd}..{overlap.To:yyyy-MM-dd}) y '{ww.Name}' ({ww.From:yyyy-MM-dd}..{ww.To:yyyy-MM-dd}).");
            ordered.Add(ww);
        }
        return ordered;
    }

    private static NormalizedCalendarDay NormalizeWorkWeekDay(MppDay day, DayOfWeek dow, string label)
    {
        switch (day.DefinedDayType)
        {
            case "Default":
                return new NormalizedCalendarDay(dow, DayScheduleKind.Inherit, []);
            case "NonWorking":
                if (day.DefinedPeriods.Count > 0) throw WorkWeekError($"{label}: {dow} es no laborable pero define periodos.");
                return new NormalizedCalendarDay(dow, DayScheduleKind.NonWorking, []);
            case "Working":
                // Periodos explícitos (DefinedPeriods); EffectivePeriods solo si no hay definidos.
                var source = day.DefinedPeriods.Count > 0 ? day.DefinedPeriods : day.EffectivePeriods;
                var periods = ParsePeriods(source, $"{dow} de {label}", ImportExitCodes.WorkWeeks);
                if (periods.Count == 0) throw WorkWeekError($"{label}: {dow} es laborable pero no define periodos; no se infiere ningún horario.");
                return new NormalizedCalendarDay(dow, DayScheduleKind.Working, periods);
            default:
                throw WorkWeekError($"{label}: {dow} tiene un DefinedDayType no soportado ('{day.DefinedDayType}').");
        }
    }

    /// <summary>Parsea, ordena por hora inicial y valida solapes.</summary>
    public static IReadOnlyList<NormalizedTimePeriod> ParsePeriods(IEnumerable<string> raw, string context, int exitCode = ImportExitCodes.Functional)
    {
        var list = new List<NormalizedTimePeriod>();
        foreach (var text in raw)
        {
            try { list.Add(NormalizedTimePeriod.Parse(text, context)); }
            catch (CalendarImportException ex) when (exitCode != ex.ExitCode)
            { throw new CalendarImportException(exitCode, ex.Stage, ex.Message); }
        }
        list.Sort((a, b) => a.StartMinute.CompareTo(b.StartMinute));
        for (var i = 1; i < list.Count; i++)
            if (list[i].StartMinute < list[i - 1].EndMinute)
                throw new CalendarImportException(exitCode, Stage, $"Periodos solapados en {context}: {list[i - 1]} y {list[i]}.");
        return list;
    }

    private static MppDay FindDay(IReadOnlyList<MppDay> days, DayOfWeek dow, string context, int exitCode = ImportExitCodes.Functional)
        => days.FirstOrDefault(d => Enum.TryParse<DayOfWeek>(d.Day, true, out var p) && p == dow)
           ?? throw new CalendarImportException(exitCode, Stage, $"Falta el día {dow} en {context}.");

    private static CalendarImportException Functional(string message) => new(ImportExitCodes.Functional, Stage, message);
    private static CalendarImportException WorkWeekError(string message) => new(ImportExitCodes.WorkWeeks, "WorkWeeks", message);
}

/// <summary>Genera nombres legibles y deterministas de Calendar Template.</summary>
public static class CalendarTemplateNaming
{
    /// <summary>
    /// Nombre compacto sin pérdida de información: periodos del lunes + desviaciones por día
    /// (L M X J V S D). Si no es posible o excede maxLength, usa el prefijo + 12 caracteres del fingerprint.
    /// </summary>
    public static string BuildName(string prefix, NormalizedCalendar weekly, int? maxLength)
    {
        var compact = TryCompact(weekly);
        var name = compact is null ? FingerprintName(prefix, weekly) : $"{prefix} {compact}";
        if (maxLength is { } max && name.Length > max) name = FingerprintName(prefix, weekly);
        if (maxLength is { } max2 && name.Length > max2) name = name[..max2];
        return name;
    }

    public static string FingerprintName(string prefix, NormalizedCalendar weekly) => $"{prefix} {weekly.Fingerprint[..12]}";

    private static string? TryCompact(NormalizedCalendar weekly)
    {
        var monday = weekly.Days[0];
        if (monday.Kind != DayScheduleKind.Working) return null;
        var baseText = string.Join(" ", monday.Periods.Select(p => p.ToCompact()));
        var parts = new List<string> { baseText };
        foreach (var d in weekly.Days.Skip(1))
        {
            var isWeekend = d.Day is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var same = d.Kind == DayScheduleKind.Working && d.Periods.SequenceEqual(monday.Periods);
            if (isWeekend ? d.Kind == DayScheduleKind.NonWorking : same) continue;
            var text = d.Kind == DayScheduleKind.Working ? string.Join(" ", d.Periods.Select(p => p.ToCompact())) : "OFF";
            parts.Add($"{CalendarCodes.SpanishLetter(d.Day)}{text}");
        }
        return string.Join(" ", parts);
    }
}
