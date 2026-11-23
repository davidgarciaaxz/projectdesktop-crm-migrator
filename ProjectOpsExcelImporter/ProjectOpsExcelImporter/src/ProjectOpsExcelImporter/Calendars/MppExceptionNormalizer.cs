using System.Globalization;

namespace ProjectOpsExcelImporter.Calendars;

public sealed class RecurrenceNotSupportedException(string message) : Exception(message);

/// <summary>
/// Expande recurrencias de CalendarExceptions a fechas concretas, únicamente si el cálculo es determinista
/// con los datos del JSON. Soporta Daily, Weekly (WeeklyDays), Monthly y Yearly (día/mes absolutos).
/// Cada ocurrencia es de un solo día (supuesto; el JSON no informa de una duración por ocurrencia).
/// </summary>
public static class RecurrenceExpander
{
    public static IReadOnlyList<DateOnly> Expand(MppException e, DateOnly windowStart, DateOnly windowEnd, int maxOccurrences)
    {
        if (e.Relative)
            throw new RecurrenceNotSupportedException("Las recurrencias relativas (p. ej. 'el segundo lunes') no están soportadas.");
        var type = e.RecurrenceType?.Trim().ToLowerInvariant() ?? throw new RecurrenceNotSupportedException("RecurrenceType vacío.");
        var start = e.RecurrenceStart ?? e.From;
        var freq = e.Frequency ?? 1;
        if (freq < 1) throw new RecurrenceNotSupportedException($"Frequency {freq} no válida.");
        if (e.UseEndDate && e.RecurrenceFinish is null)
            throw new RecurrenceNotSupportedException("UseEndDate=true pero RecurrenceFinish no está informado.");

        var limit = e.RecurrenceFinish; // límite superior de la regla, si existe
        var byCount = !e.UseEndDate && e.Occurrences.HasValue;
        var bounded = byCount || limit.HasValue;

        var all = new List<DateOnly>();
        foreach (var date in Generate(e, type, start, freq))
        {
            if (limit.HasValue && date > limit.Value) break;
            if (!bounded && date > windowEnd) break;
            all.Add(date);
            if (byCount && all.Count >= e.Occurrences!.Value) break;
            if (all.Count > maxOccurrences)
                throw new RecurrenceNotSupportedException($"La regla excede MaxExpandedOccurrencesPerRule ({maxOccurrences}).");
        }

        // Guarda de determinismo: nuestro cálculo debe coincidir con el recuento del propio JSON.
        if (bounded && e.ExpandedOccurrenceCount is { } expected && expected != all.Count)
            throw new RecurrenceNotSupportedException($"El cálculo produce {all.Count} ocurrencias pero el JSON indica ExpandedOccurrenceCount={expected}; la regla no se interpreta de forma inequívoca.");

        return all.Where(d => d >= windowStart && d <= windowEnd).Distinct().OrderBy(d => d).ToList();
    }

    private static IEnumerable<DateOnly> Generate(MppException e, string type, DateOnly start, int freq)
    {
        switch (type)
        {
            case "daily":
                for (var k = 0; ; k++) yield return start.AddDays(k * freq);
            case "weekly":
            {
                var days = e.WeeklyDays.Select(w => Enum.TryParse<DayOfWeek>(w, true, out var d) ? d
                    : throw new RecurrenceNotSupportedException($"Día de semana '{w}' no reconocido.")).Distinct().ToList();
                if (days.Count == 0) throw new RecurrenceNotSupportedException("Recurrencia semanal sin WeeklyDays.");
                var ordered = CalendarCodes.WeekOrder.Where(days.Contains).ToList();
                var monday = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var k = 0; ; k++)
                    foreach (var d in ordered)
                    {
                        var date = monday.AddDays(k * freq * 7 + Array.IndexOf(CalendarCodes.WeekOrder, d));
                        if (date >= start) yield return date;
                    }
            }
            case "monthly":
            {
                var day = e.DayNumber ?? throw new RecurrenceNotSupportedException("Recurrencia mensual sin DayNumber.");
                for (var k = 0; ; k++)
                {
                    var first = new DateOnly(start.Year, start.Month, 1).AddMonths(k * freq);
                    if (day < 1 || day > DateTime.DaysInMonth(first.Year, first.Month))
                        throw new RecurrenceNotSupportedException($"El día {day} no existe en {first:yyyy-MM}; no se infiere ajuste.");
                    var date = new DateOnly(first.Year, first.Month, day);
                    if (date >= start) yield return date;
                }
            }
            case "yearly":
            {
                var month = e.MonthNumber ?? throw new RecurrenceNotSupportedException("Recurrencia anual sin MonthNumber.");
                var day = e.DayNumber ?? throw new RecurrenceNotSupportedException("Recurrencia anual sin DayNumber.");
                for (var k = 0; ; k++)
                {
                    var year = start.Year + k * freq;
                    if (year > CalendarCodes.MaxDate.Year) yield break;
                    if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
                        throw new RecurrenceNotSupportedException($"La fecha {day}/{month}/{year} no existe; no se infiere ajuste.");
                    var date = new DateOnly(year, month, day);
                    if (date >= start) yield return date;
                }
            }
            default:
                throw new RecurrenceNotSupportedException($"RecurrenceType '{e.RecurrenceType}' no soportado.");
        }
    }
}

public static class MppExceptionNormalizer
{
    private const string Stage = "CalendarExceptions";

    public static IReadOnlyList<NormalizedCalendarException> Normalize(
        MppProject project, MppCalendar calendar, CalendarImportSettings settings, List<string> warnings)
    {
        var mode = settings.RecurringExceptionsMode;
        var items = new List<NormalizedCalendarException>();
        (DateOnly Start, DateOnly End)? window = null;

        foreach (var e in project.Exceptions.Where(x => x.CalendarUniqueId == calendar.UniqueId))
        {
            var label = $"Excepción '{e.Name}' ({e.From:yyyy-MM-dd}..{e.To:yyyy-MM-dd})";
            var periods = MppCalendarNormalizer.ParsePeriods(e.Periods, label, ImportExitCodes.CalendarExceptions);
            if (e.From > e.To) throw Error($"{label}: fecha inicial posterior a la final.");
            if (e.Working && periods.Count == 0) throw Error($"{label}: Working=true sin Periods; no se infiere ningún horario.");
            if (!e.Working && periods.Count > 0) throw Error($"{label}: Working=false pero define Periods.");

            if (!e.IsRecurring)
            {
                items.Add(new NormalizedCalendarException(e.CalendarUniqueId, e.Name, null, e.From, e.To, e.Working, periods, false));
                continue;
            }

            var rule = e.RecurrenceDescription ?? $"{e.RecurrenceType} (from {e.RecurrenceStart:yyyy-MM-dd})";
            if (mode.Equals(CalendarImportSettings.ModeFail, StringComparison.OrdinalIgnoreCase))
                throw Error($"{label}: recurrencia no permitida con RecurringExceptionsMode=Fail. Regla: {rule}");
            if (mode.Equals(CalendarImportSettings.ModeSkipWithWarning, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{label}: recurrencia omitida (SkipWithWarning). Regla completa: {rule}");
                continue;
            }

            window ??= ResolveWindow(project, settings);
            try
            {
                foreach (var date in RecurrenceExpander.Expand(e, window.Value.Start, window.Value.End, settings.MaxExpandedOccurrencesPerRule))
                    items.Add(new NormalizedCalendarException(e.CalendarUniqueId, e.Name, rule, date, date, e.Working, periods, true));
            }
            catch (RecurrenceNotSupportedException ex)
            {
                throw Error($"{label}: no se puede expandir la recurrencia. {ex.Message} Regla: {rule}");
            }
        }

        // Deduplicar fechas repetidas por distintas reglas; mismo rango con contenido distinto es conflicto.
        var result = new List<NormalizedCalendarException>();
        foreach (var item in items.OrderBy(i => i.From).ThenBy(i => i.To))
        {
            if (result.Any(r => r.Key == item.Key)) continue;
            var clash = result.FirstOrDefault(r => r.From == item.From && r.To == item.To);
            if (clash is not null)
                throw Error($"Excepciones en conflicto para {item.From:yyyy-MM-dd}..{item.To:yyyy-MM-dd}: '{clash.SourceName}' y '{item.SourceName}' definen horarios distintos.");
            result.Add(item);
        }
        return result;
    }

    private static (DateOnly, DateOnly) ResolveWindow(MppProject project, CalendarImportSettings settings)
    {
        var start = settings.RecurrenceExpansionStart.Equals("ProjectStart", StringComparison.OrdinalIgnoreCase)
            ? project.StartDate is { } s ? DateOnly.FromDateTime(s) : throw Error("No se puede expandir recurrencias: el proyecto no tiene StartDate.")
            : DateOnly.ParseExact(settings.RecurrenceExpansionStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateOnly end;
        if (settings.RecurrenceExpansionEnd.Equals("ProjectFinish", StringComparison.OrdinalIgnoreCase))
        {
            var finish = project.FinishDate ?? project.MaxTaskFinish
                ?? throw Error("No se puede expandir recurrencias: no hay FinishDate ni fechas de fin en Tasks[].");
            end = DateOnly.FromDateTime(finish);
        }
        else end = DateOnly.ParseExact(settings.RecurrenceExpansionEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (end < start) throw Error($"Intervalo de expansión no válido: {start:yyyy-MM-dd}..{end:yyyy-MM-dd}.");
        return (start, end);
    }

    private static CalendarImportException Error(string message) => new(ImportExitCodes.CalendarExceptions, Stage, message);
}
