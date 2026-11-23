using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter.Calendars;

/// <summary>Resultado de la prueba experimental de msdyn_SaveCalendar (StatusCode null en dry-run).</summary>
public sealed record SaveCalendarTestResult(string CalendarEventInfoJson, string HttpBody, int? StatusCode, string? ResponseBody, string? InnerCalendarIds);

/// <summary>
/// Lee y escribe SOLO el calendario propio de un proyecto (nunca una Calendar Template).
/// Formato de calendar/calendarrule tomado de plantillas creadas con la UI de Project Operations (comando calendar-dump):
/// - regla semanal: pattern FREQ=WEEKLY;BYDAY=..., con calendario interno (type -1) de reglas simples
///   (offset/duration en minutos; subcode 1 = laborable, subcode 4 = descanso);
/// - día no laborable: pattern FREQ=DAILY;INTERVAL=1;COUNT=1, extentcode 2, descripción "Not Working",
///   con calendario interno de una regla simple (offset 0, duration 1440, timecode 2, subcode 0, extentcode 2).
/// WorkWeeks con vigencia y excepciones laborables aún no tienen un formato confirmado y se rechazan explícitamente.
/// </summary>
public sealed class DataverseProjectCalendarRepository : IProjectCalendarRepository
{
    private const string WeeklyPrefix = "FREQ=WEEKLY";
    private const string SingleDayPattern = "FREQ=DAILY;INTERVAL=1;COUNT=1";
    private const string NotWorkingDescription = "Not Working";
    private const int TimeZoneCode = 95;           // valor observado en las reglas de las plantillas manuales
    private const int WorkingSubcode = 1;
    private const int BreakSubcode = 4;

    private readonly DataverseClient _client;
    private readonly CalendarImportSettings _settings;
    private readonly Dictionary<Guid, HashSet<string>> _nonWorkingDays = [];
    private readonly Dictionary<Guid, HashSet<string>> _workingDays = [];
    private const string WorkingDayPattern = "FREQ=DAILY;COUNT=1";

    public DataverseProjectCalendarRepository(DataverseClient client, CalendarImportSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public void EnsureConfigured()
        => CalendarMetadataGuard.Check(_settings.DataverseMetadata, false, false, true, _settings.ApplyWorkWeeks || _settings.ApplyCalendarExceptions);

    public async Task<ProjectCalendarInfo?> TryGetProjectCalendarAsync(Guid projectId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var m = _settings.DataverseMetadata;
        var project = await _client.GetAsync($"msdyn_projects({projectId:D})?$select={m.ProjectCalendarAttribute}", cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(project[m.ProjectCalendarAttribute!]?.GetValue<string>(), out var calendarId)) return null;
        var rules = await LoadRulesAsync(calendarId, cancellationToken).ConfigureAwait(false);
        if (rules is null) return null;

        var days = new Dictionary<DayOfWeek, List<NormalizedTimePeriod>>();
        foreach (var rule in rules)
        {
            var pattern = Str(rule, "pattern");
            if (pattern is null || !pattern.StartsWith(WeeklyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!(Str(rule, "effectiveintervalend")?.StartsWith("9999", StringComparison.Ordinal) ?? false)) continue;   // solo la semana permanente
            if (!Guid.TryParse(Str(rule, "_innercalendarid_value"), out var innerId)) continue;
            var inner = await LoadRulesAsync(innerId, cancellationToken).ConfigureAwait(false) ?? [];
            var periods = ComputePeriods(inner);
            foreach (var day in ParseByDay(pattern)) days[day] = periods;
        }
        var canonical = string.Join(";", CalendarCodes.WeekOrder.Select(d =>
            $"{CalendarCodes.Code(d)}={(days.TryGetValue(d, out var p) && p.Count > 0 ? string.Join("|", p) : "OFF")}"));
        return new ProjectCalendarInfo(calendarId, canonical);
    }

    public Task<ApplyResult> EnsureWorkWeekAsync(Guid projectCalendarId, NormalizedWorkWeek workWeek, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "WorkWeeks con fechas de vigencia: el formato de calendarrule no está confirmado (las plantillas de muestra solo contienen la semana permanente). " +
            "Crea una plantilla de muestra que contenga un horario con vigencia y ejecuta calendar-dump para documentarlo.");

    public async Task<ApplyResult> EnsureExceptionAsync(Guid projectCalendarId, NormalizedCalendarException exception, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (exception.Working) return await EnsureWorkingExceptionAsync(projectCalendarId, exception, cancellationToken).ConfigureAwait(false);

        var covered = await NonWorkingDaysAsync(projectCalendarId, cancellationToken).ConfigureAwait(false);
        // Se escriben solo los tramos consecutivos que aún no están cubiertos; cada tramo es UNA llamada a msdyn_SaveCalendar.
        var created = 0;
        DateOnly? runStart = null;
        for (var day = exception.From; day <= exception.To.AddDays(1); day = day.AddDays(1))   // el día To+1 actúa de centinela y cierra el último tramo
        {
            if (day <= exception.To && !covered.Contains(DayKey(day)))
            {
                runStart ??= day;
                continue;
            }
            if (runStart is not { } start) continue;
            var last = day.AddDays(-1);
            await SaveNonWorkingRangeAsync(projectCalendarId, start, last, cancellationToken).ConfigureAwait(false);
            for (var d = start; d <= last; d = d.AddDays(1)) covered.Add(DayKey(d));
            created++;
            runStart = null;
        }
        return new ApplyResult(created > 0 ? ApplyOutcome.Created : ApplyOutcome.Reused);
    }

    /// <summary>
    /// Crea un día o rango no laborable con la acción msdyn_SaveCalendar (verificada en el entorno: genera la regla exterior y el calendario
    /// interno igual que la UI). calendarrule no admite Create ni actualización anidada por Web API, por eso no se escribe a mano.
    /// </summary>
    private async Task SaveNonWorkingRangeAsync(Guid projectCalendarId, DateOnly from, DateOnly toInclusive, CancellationToken ct)
    {
        var calendarEventInfo = new
        {
            CalendarId = projectCalendarId,
            EntityLogicalName = "msdyn_project",
            TimeZoneCode = TimeZoneCode,
            RulesAndRecurrences = new[]
            {
                new
                {
                    Rules = new[]
                    {
                        new
                        {
                            StartTime = $"{from:yyyy-MM-dd}T00:00:00Z",
                            EndTime = $"{toInclusive.AddDays(1):yyyy-MM-dd}T00:00:00Z",
                            Effort = 1,
                            WorkHourType = 2
                        }
                    }
                }
            }
        };
        // CalendarEventInfo va como STRING dentro del cuerpo (doble serialización).
        var body = System.Text.Json.JsonSerializer.Serialize(new { CalendarEventInfo = System.Text.Json.JsonSerializer.Serialize(calendarEventInfo) });
        var (status, response) = await _client.PostRawAsync("msdyn_SaveCalendar", body, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"msdyn_SaveCalendar devolvió {status} para {from:yyyy-MM-dd}..{toInclusive:yyyy-MM-dd}. Respuesta: {response}");
    }

    /// <summary>
    /// Día laborable excepcional con horario propio (puede ser partido). Se crea un día por llamada a msdyn_SaveCalendar.
    /// Formato verificado en el entorno: la API SUMA las duraciones de las Rules, así que se envía un bloque laborable (WorkHourType 0)
    /// que empieza en el primer tramo y dura la suma de los tramos, más un descanso (WorkHourType 1) por cada hueco entre tramos.
    /// Ej.: 09-13 y 15-19 => bloque 09:00-17:00 + descanso 13:00-15:00 => Dataverse guarda laborable 09:00+600 min y descanso 13:00+120 min.
    /// </summary>
    private async Task<ApplyResult> EnsureWorkingExceptionAsync(Guid projectCalendarId, NormalizedCalendarException exception, CancellationToken ct)
    {
        if (exception.Periods.Count == 0)
            throw new NotSupportedException($"Excepción laborable {exception.From:yyyy-MM-dd}..{exception.To:yyyy-MM-dd} sin periodos horarios.");
        var periods = exception.Periods.OrderBy(p => p.StartMinute).ToList();
        for (var i = 1; i < periods.Count; i++)
            if (periods[i].StartMinute < periods[i - 1].EndMinute)
                throw new NotSupportedException($"Excepción laborable {exception.From:yyyy-MM-dd}: los periodos {periods[i - 1]} y {periods[i]} se solapan.");

        var covered = await WorkingDaysAsync(projectCalendarId, ct).ConfigureAwait(false);
        var created = 0;
        for (var day = exception.From; day <= exception.To; day = day.AddDays(1))
        {
            if (covered.Contains(DayKey(day))) continue;      // ya existe un horario excepcional ese día (no se compara su contenido)
            var midnight = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc);
            string Stamp(int minute) => midnight.AddMinutes(minute).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var rules = new List<object>
            {
                new { StartTime = Stamp(periods[0].StartMinute), EndTime = Stamp(periods[0].StartMinute + periods.Sum(p => p.EndMinute - p.StartMinute)), Effort = 1, WorkHourType = 0 }
            };
            for (var i = 1; i < periods.Count; i++)
                rules.Add(new { StartTime = Stamp(periods[i - 1].EndMinute), EndTime = Stamp(periods[i].StartMinute), Effort = 1, WorkHourType = 1 });

            var info = new { CalendarId = projectCalendarId, EntityLogicalName = "msdyn_project", TimeZoneCode = TimeZoneCode, RulesAndRecurrences = new[] { new { Rules = rules } } };
            var body = System.Text.Json.JsonSerializer.Serialize(new { CalendarEventInfo = System.Text.Json.JsonSerializer.Serialize(info) });
            var (status, response) = await _client.PostRawAsync("msdyn_SaveCalendar", body, ct).ConfigureAwait(false);
            if (status is < 200 or >= 300)
                throw new InvalidOperationException($"msdyn_SaveCalendar devolvió {status} para el día laborable {day:yyyy-MM-dd}. Respuesta: {response}");
            covered.Add(DayKey(day));
            created++;
        }
        return new ApplyResult(created > 0 ? ApplyOutcome.Created : ApplyOutcome.Reused);
    }

    /// <summary>Días con un horario excepcional laborable ya guardado (regla de evento único sin INTERVAL, como la que genera msdyn_SaveCalendar).</summary>
    private async Task<HashSet<string>> WorkingDaysAsync(Guid projectCalendarId, CancellationToken ct)
    {
        if (_workingDays.TryGetValue(projectCalendarId, out var cached)) return cached;
        var set = new HashSet<string>();
        foreach (var rule in await LoadRulesAsync(projectCalendarId, ct).ConfigureAwait(false) ?? [])
        {
            if (Str(rule, "pattern") != WorkingDayPattern) continue;
            if (DateTime.TryParse(Str(rule, "starttime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
                set.Add(DayKey(DateOnly.FromDateTime(start)));
        }
        _workingDays[projectCalendarId] = set;
        return set;
    }

    private static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// EXPERIMENTAL, aislado: intenta crear UN día no laborable en el calendario propio de un proyecto mediante la acción
    /// msdyn_SaveCalendar, sin crear calendar ni calendarrule manualmente. No lo usa el flujo de import.
    /// Por defecto es dry-run: construye y registra el JSON y el body HTTP pero NO llama a Dataverse.
    /// </summary>
    public async Task<SaveCalendarTestResult> TestSaveCalendarNonWorkingDayAsync(
        Guid projectCalendarId, DateTime date, int timeZoneCode, CancellationToken cancellationToken,
        bool dryRun = true, TextWriter? log = null, DateTime? endDate = null,
        IReadOnlyList<(NormalizedTimePeriod Period, int WorkHourType)>? workingPeriods = null, int workHourType = 2)
    {
        log ??= Console.Out;
        var start = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);
        var last = endDate ?? date;                        // último día incluido (por defecto, el mismo día)
        var end = new DateTime(last.Year, last.Month, last.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);

        // 1) Objeto CalendarEventInfo (estructura indicada para la prueba) -> JSON interno.
        var calendarEventInfo = new
        {
            CalendarId = projectCalendarId,
            EntityLogicalName = "msdyn_project",
            TimeZoneCode = timeZoneCode,
            RulesAndRecurrences = new[]
            {
                new
                {
                    // Dataverse lo confirmó en el 400 de la primera prueba: la clave es "Rules" (array), no "Rule".
                    // Sin tramos: un día/rango completo (no laborable). Con tramos: una Rule por tramo horario del día (hipótesis a verificar).
                    Rules = workingPeriods is { Count: > 0 }
                        ? workingPeriods.Select(p => new
                        {
                            StartTime = start.AddMinutes(p.Period.StartMinute).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                            EndTime = start.AddMinutes(p.Period.EndMinute).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                            Effort = 1,
                            WorkHourType = p.WorkHourType
                        }).ToArray()
                        : new[]
                        {
                            new
                            {
                                StartTime = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                                EndTime = end.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                                Effort = 1,
                                WorkHourType = workHourType
                            }
                        }
                }
            }
        };
        var innerJson = System.Text.Json.JsonSerializer.Serialize(calendarEventInfo);

        // 2) Body exterior: CalendarEventInfo como STRING (segunda serialización, la comilla y las barras se escapan).
        var httpBody = System.Text.Json.JsonSerializer.Serialize(new { CalendarEventInfo = innerJson });

        log.WriteLine("=== msdyn_SaveCalendar (experimental) ===");
        log.WriteLine($"Modo               : {(dryRun ? "DRY-RUN (no se llama a Dataverse)" : "REAL")}");
        log.WriteLine($"projectCalendarId  : {projectCalendarId:D}");
        log.WriteLine($"date               : {date:yyyy-MM-dd}");
        log.WriteLine($"timeZoneCode       : {timeZoneCode}");
        log.WriteLine($"CalendarEventInfo  : {innerJson}");
        log.WriteLine($"POST               : msdyn_SaveCalendar");
        log.WriteLine($"HTTP body          : {httpBody}");

        if (dryRun) return new SaveCalendarTestResult(innerJson, httpBody, null, null, null);

        var (status, body) = await _client.PostRawAsync("msdyn_SaveCalendar", httpBody, cancellationToken).ConfigureAwait(false);
        string? innerIds = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && JsonNode.Parse(body) is JsonObject response && response["InnerCalendarIds"] is { } ids)
                innerIds = ids.ToJsonString();
        }
        catch (System.Text.Json.JsonException) { /* el cuerpo no es JSON: se registra tal cual */ }

        log.WriteLine($"HTTP status        : {status} ({(status is >= 200 and < 300 ? "OK" : "ERROR")})");
        log.WriteLine($"Response body      : {(string.IsNullOrEmpty(body) ? "(vacío)" : body)}");
        log.WriteLine($"InnerCalendarIds   : {innerIds ?? "(no aparece en la respuesta)"}");
        if (status is < 200 or >= 300) log.WriteLine($"ERROR Dataverse    : {body}");
        return new SaveCalendarTestResult(innerJson, httpBody, status, body, innerIds);
    }

    /// <summary>Días ya cubiertos por reglas "Not Working" de un solo evento (COUNT=1): desde starttime hasta effectiveintervalend (exclusivo).</summary>
    private async Task<HashSet<string>> NonWorkingDaysAsync(Guid projectCalendarId, CancellationToken ct)
    {
        if (_nonWorkingDays.TryGetValue(projectCalendarId, out var cached)) return cached;
        var set = new HashSet<string>();
        foreach (var rule in await LoadRulesAsync(projectCalendarId, ct).ConfigureAwait(false) ?? [])
        {
            if (Str(rule, "pattern") != SingleDayPattern || Str(rule, "description") != NotWorkingDescription) continue;
            const DateTimeStyles utc = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
            if (!DateTime.TryParse(Str(rule, "starttime"), CultureInfo.InvariantCulture, utc, out var start)) continue;
            var end = DateTime.TryParse(Str(rule, "effectiveintervalend"), CultureInfo.InvariantCulture, utc, out var parsedEnd) ? parsedEnd : start.AddDays(1);
            for (var day = DateOnly.FromDateTime(start); day < DateOnly.FromDateTime(end); day = day.AddDays(1)) set.Add(DayKey(day));
        }
        _nonWorkingDays[projectCalendarId] = set;
        return set;
    }

    /// <summary>calendarrule no admite RetrieveMultiple: las reglas se leen expandiendo la relación del calendario. Null si el calendario no existe.</summary>
    private async Task<JsonArray?> LoadRulesAsync(Guid calendarId, CancellationToken ct)
    {
        var m = _settings.DataverseMetadata;
        try
        {
            var calendar = await _client.GetAsync($"{m.CalendarEntitySet}({calendarId:D})?$expand=calendar_calendar_rules", ct).ConfigureAwait(false);
            return calendar["calendar_calendar_rules"] as JsonArray ?? [];
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>Intervalos laborables (subcode 1) menos descansos (subcode 4), en minutos desde medianoche.</summary>
    public static List<NormalizedTimePeriod> ComputePeriods(JsonArray innerRules)
    {
        var working = new List<(int Start, int End)>();
        var breaks = new List<(int Start, int End)>();
        foreach (var rule in innerRules)
        {
            var offset = (int?)rule?["offset"];
            var duration = (int?)rule?["duration"];
            var subcode = (int?)rule?["subcode"];
            if (offset is null || duration is null) continue;
            if (subcode == WorkingSubcode) working.Add((offset.Value, offset.Value + duration.Value));
            else if (subcode == BreakSubcode) breaks.Add((offset.Value, offset.Value + duration.Value));
        }
        var result = new List<NormalizedTimePeriod>();
        foreach (var (start, end) in working.OrderBy(w => w.Start))
        {
            var cursor = start;
            foreach (var (bStart, bEnd) in breaks.OrderBy(b => b.Start))
            {
                if (bEnd <= cursor || bStart >= end) continue;
                if (bStart > cursor) result.Add(new NormalizedTimePeriod(cursor, bStart));
                cursor = Math.Max(cursor, bEnd);
            }
            if (cursor < end) result.Add(new NormalizedTimePeriod(cursor, end));
        }
        return result;
    }

    private static IEnumerable<DayOfWeek> ParseByDay(string pattern)
    {
        var part = pattern.Split(';').FirstOrDefault(p => p.StartsWith("BYDAY=", StringComparison.OrdinalIgnoreCase));
        if (part is null) yield break;
        foreach (var code in part["BYDAY=".Length..].Split(',', StringSplitOptions.TrimEntries))
        {
            switch (code.ToUpperInvariant())
            {
                case "MO": yield return DayOfWeek.Monday; break;
                case "TU": yield return DayOfWeek.Tuesday; break;
                case "WE": yield return DayOfWeek.Wednesday; break;
                case "TH": yield return DayOfWeek.Thursday; break;
                case "FR": yield return DayOfWeek.Friday; break;
                case "SA": yield return DayOfWeek.Saturday; break;
                case "SU": yield return DayOfWeek.Sunday; break;
            }
        }
    }

    private static string? Str(JsonNode? rule, string name)
        => rule?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
