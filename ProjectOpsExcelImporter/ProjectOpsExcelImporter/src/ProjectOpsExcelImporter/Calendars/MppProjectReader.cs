using System.Globalization;
using System.Text.Json;

namespace ProjectOpsExcelImporter.Calendars;

public sealed record MppDay(string Day, string? DefinedDayType, IReadOnlyList<string> DefinedPeriods, bool? EffectiveWorking, IReadOnlyList<string> EffectivePeriods);

public sealed record MppWorkWeek(string? Name, DateOnly From, DateOnly To, IReadOnlyList<MppDay> Days);

public sealed record MppCalendar(int UniqueId, string Name, IReadOnlyList<MppDay> Days, IReadOnlyList<MppWorkWeek> WorkWeeks);

public sealed record MppException(
    int CalendarUniqueId, string? CalendarName, string? Name, DateOnly From, DateOnly To, bool Working,
    IReadOnlyList<string> Periods, bool IsRecurring, string? RecurrenceType, int? Frequency, int? Occurrences,
    bool UseEndDate, DateOnly? RecurrenceStart, DateOnly? RecurrenceFinish, bool Relative, string? DayOfWeek,
    int? DayNumber, int? MonthNumber, IReadOnlyList<string> WeeklyDays, string? RecurrenceDescription,
    int? ExpandedOccurrenceCount);

public sealed class MppProject
{
    public string? Title { get; init; }
    /// <summary>GUID del proyecto en Project Desktop (ProjectProperties.GUID); se usa como msdyn_projectid.</summary>
    public Guid? ProjectGuid { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? FinishDate { get; init; }
    public DateTime? MaxTaskFinish { get; init; }
    public int? DefaultCalendarUniqueId { get; init; }
    public List<MppCalendar> Calendars { get; init; } = [];
    public List<MppException> Exceptions { get; init; } = [];
    public List<MppTask> Tasks { get; init; } = [];
    public List<MppDependency> Dependencies { get; init; } = [];
    public List<MppResource> Resources { get; init; } = [];
    public List<MppResourceAssignment> ResourceAssignments { get; init; } = [];
}

/// <summary>Un recurso de Resources[]: identidad y tipo. La tarifa estándar se lee SOLO para el informe de roles; nunca se envía a Project Operations.</summary>
public sealed class MppResource
{
    /// <summary>Tarifa estándar del MPP (solo informe, no se importa) y su unidad.</summary>
    public decimal? StandardRate { get; init; }
    public string? StandardRateUnits { get; init; }
    public int UniqueId { get; init; }
    public int Id { get; init; }
    /// <summary>Guid del recurso en Project Desktop: identidad de origen.</summary>
    public Guid? Guid { get; init; }
    public string? Name { get; init; }
    /// <summary>Work o Material.</summary>
    public string Type { get; init; } = string.Empty;
    public bool IsNull { get; init; }
}

/// <summary>Una asignación de ResourceAssignments[] (solo las referencias a tarea y recurso).</summary>
public sealed class MppResourceAssignment
{
    public int UniqueId { get; init; }
    public Guid? Guid { get; init; }
    public int? TaskUniqueId { get; init; }
    public string? TaskName { get; init; }
    public bool TaskMissing { get; init; }
    public int? ResourceUniqueId { get; init; }
    public Guid? ResourceGuid { get; init; }
    public string? ResourceName { get; init; }
    public bool ResourceMissing { get; init; }
    /// <summary>Trabajo de la asignación en horas (Work, ActualWork, RemainingWork). Solo lo usa la prueba assignment-work-test; la importación no lo envía.</summary>
    public decimal? WorkHours { get; init; }
    public decimal? ActualWorkHours { get; init; }
    public decimal? RemainingWorkHours { get; init; }
}

/// <summary>Una relación de Dependencies[] del project-dump.json (predecesora y sucesora por UniqueId).</summary>
public sealed class MppDependency
{
    public int RelationUniqueId { get; init; }
    public int PredecessorUniqueId { get; init; }
    public int SuccessorUniqueId { get; init; }
    /// <summary>FinishStart, StartStart, FinishFinish o StartFinish.</summary>
    public string RelationType { get; init; } = string.Empty;
    /// <summary>Positivo = retardo; negativo = adelanto.</summary>
    public decimal LagValue { get; init; }
    public string LagUnits { get; init; } = "Days";
    public string? LagText { get; init; }
}

/// <summary>Datos de una tarea de Tasks[] de project-dump.json necesarios para la fase de tareas (nada de costes, recursos ni dependencias).</summary>
public sealed class MppTask
{
    public int UniqueId { get; init; }
    public int Id { get; init; }
    /// <summary>Guid de la tarea en Project Desktop; base del GUID determinista que ya usa el importador.</summary>
    public Guid? Guid { get; init; }
    public string Name { get; init; } = string.Empty;
    public int OutlineLevel { get; init; }
    /// <summary>UniqueId de la tarea padre; 0 = tarea resumen del proyecto (no se importa), es decir, tarea raíz.</summary>
    public int ParentUniqueId { get; init; }
    public bool IsSummary { get; init; }
    public bool IsMilestone { get; init; }
    public bool IsNull { get; init; }
    public bool IsProjectSummaryTask { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime? Start { get; init; }
    /// <summary>Solo para comparación y registro: nunca se envía a Project Operations.</summary>
    public DateTime? Finish { get; init; }
    public decimal? DurationValue { get; init; }
    public string? DurationUnits { get; init; }
    /// <summary>PercentComplete del MPP (0-100). Solo lo usa assignment-work-test; la importación no lo envía.</summary>
    public decimal? PercentComplete { get; init; }
    /// <summary>Notas de la tarea en el MPP (texto tal cual, con sus tabuladores y saltos de línea). Va a msdyn_descriptionplaintext.</summary>
    public string? Notes { get; init; }
}

/// <summary>Lee de project-dump.json únicamente lo necesario para proyecto y calendarios.</summary>
public static class MppProjectReader
{
    public const string PropTitle = "ProjectProperties.ProjectTitle";
    public const string PropGuid = "ProjectProperties.GUID";
    public const string PropStart = "ProjectProperties.StartDate";
    public const string PropFinish = "ProjectProperties.FinishDate";
    public const string PropDefaultCalendar = "ProjectProperties.DefaultCalendarUniqueID";

    public static MppProject ReadFile(string path) => Parse(File.ReadAllText(path));

    public static MppProject Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var props = ReadProperties(root);
            return new MppProject
            {
                Title = props.GetValueOrDefault(PropTitle) is { ValueKind: JsonValueKind.String } t ? t.GetString() : null,
                ProjectGuid = props.GetValueOrDefault(PropGuid) is { ValueKind: JsonValueKind.String } g && Guid.TryParse(g.GetString(), out var projectGuid) ? projectGuid : null,
                StartDate = ToDateTime(props.GetValueOrDefault(PropStart)),
                FinishDate = ToDateTime(props.GetValueOrDefault(PropFinish)),
                MaxTaskFinish = ReadMaxTaskFinish(root),
                DefaultCalendarUniqueId = ToInt(props.GetValueOrDefault(PropDefaultCalendar)),
                Calendars = Array(root, "Calendars").Select(ReadCalendar).ToList(),
                Exceptions = Array(root, "CalendarExceptions").Select(ReadException).ToList(),
                Tasks = Array(root, "Tasks").Select(ReadTask).ToList(),
                Dependencies = Array(root, "Dependencies").Select(ReadDependency).ToList(),
                Resources = Array(root, "Resources").Select(ReadResource).ToList(),
                ResourceAssignments = Array(root, "ResourceAssignments").Select(ReadAssignment).ToList()
            };
        }
        catch (CalendarImportException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new CalendarImportException(ImportExitCodes.Functional, "CalendarResolution", $"No se pudo interpretar project-dump.json: {ex.Message}", ex);
        }
    }

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("Project", out var project) && project.TryGetProperty("Properties", out var props) && props.ValueKind == JsonValueKind.Array)
            foreach (var p in props.EnumerateArray())
                if (p.TryGetProperty("MpxjProperty", out var key) && key.GetString() is { } k && p.TryGetProperty("Value", out var v))
                    result[k] = v.Clone();
        return result;
    }

    private static DateTime? ReadMaxTaskFinish(JsonElement root)
    {
        DateTime? max = null;
        foreach (var task in Array(root, "Tasks"))
        {
            if (task.TryGetProperty("Finish", out var f) && ToDateTime(f) is { } d && (max is null || d > max)) max = d;
        }
        return max;
    }

    private static MppCalendar ReadCalendar(JsonElement c) => new(
        c.GetProperty("UniqueId").GetInt32(),
        Str(c, "Name") ?? string.Empty,
        Array(c, "Days").Select(ReadDay).ToList(),
        Array(c, "WorkWeeks").Select(w => new MppWorkWeek(
            Str(w, "Name"), Date(w, "From") ?? throw Missing("WorkWeeks.From"), Date(w, "To") ?? throw Missing("WorkWeeks.To"),
            Array(w, "Days").Select(ReadDay).ToList())).ToList());

    private static MppDay ReadDay(JsonElement d) => new(
        Str(d, "Day") ?? string.Empty, Str(d, "DefinedDayType"), Strings(d, "DefinedPeriods"),
        d.TryGetProperty("EffectiveWorking", out var ew) && ew.ValueKind is JsonValueKind.True or JsonValueKind.False ? ew.GetBoolean() : null,
        Strings(d, "EffectivePeriods"));

    private static MppException ReadException(JsonElement e) => new(
        e.GetProperty("CalendarUniqueId").GetInt32(), Str(e, "CalendarName"), Str(e, "Name"),
        Date(e, "FromDate") ?? throw Missing("CalendarExceptions.FromDate"), Date(e, "ToDate") ?? throw Missing("CalendarExceptions.ToDate"),
        Bool(e, "Working"), Strings(e, "Periods"), Bool(e, "IsRecurring"), Str(e, "RecurrenceType"), Int(e, "Frequency"),
        Int(e, "Occurrences"), Bool(e, "UseEndDate"), Date(e, "RecurrenceStart"), Date(e, "RecurrenceFinish"),
        Bool(e, "Relative"), Str(e, "DayOfWeek"), Int(e, "DayNumber"), Int(e, "MonthNumber"), Strings(e, "WeeklyDays"),
        Str(e, "RecurrenceDescription"), Int(e, "ExpandedOccurrenceCount"));

    private static MppTask ReadTask(JsonElement t)
    {
        decimal? durationValue = null;
        string? durationUnits = null;
        if (t.TryGetProperty("Duration", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var dec)) durationValue = dec;
            durationUnits = Str(d, "Units");
        }
        return new MppTask
        {
            UniqueId = Int(t, "UniqueId") ?? throw Missing("Tasks.UniqueId"),
            Id = Int(t, "Id") ?? 0,
            Guid = Str(t, "Guid") is { } g && System.Guid.TryParse(g, out var guid) ? guid : null,
            Name = Str(t, "Name") ?? string.Empty,
            OutlineLevel = Int(t, "OutlineLevel") ?? 0,
            ParentUniqueId = Int(t, "ParentUniqueId") ?? 0,
            IsSummary = Bool(t, "IsSummary"),
            IsMilestone = Bool(t, "IsMilestone"),
            IsNull = Bool(t, "IsNull"),
            IsProjectSummaryTask = Bool(t, "IsProjectSummaryTask"),
            IsActive = !t.TryGetProperty("IsActive", out var active) || active.ValueKind != JsonValueKind.False,
            Start = t.TryGetProperty("Start", out var s) ? ToDateTime(s) : null,
            Finish = t.TryGetProperty("Finish", out var f) ? ToDateTime(f) : null,
            DurationValue = durationValue,
            DurationUnits = durationUnits,
            PercentComplete = t.TryGetProperty("PercentComplete", out var pc) && pc.ValueKind == JsonValueKind.Number && pc.TryGetDecimal(out var pcValue) ? pcValue : null,
            Notes = Str(t, "Notes")
        };
    }

    private static MppResource ReadResource(JsonElement r) => new()
    {
        UniqueId = Int(r, "UniqueId") ?? throw Missing("Resources.UniqueId"),
        Id = Int(r, "Id") ?? 0,
        Guid = Str(r, "Guid") is { } g && System.Guid.TryParse(g, out var guid) ? guid : null,
        Name = Str(r, "Name"),
        Type = Str(r, "Type") ?? string.Empty,
        IsNull = Bool(r, "IsNull"),
        StandardRate = r.TryGetProperty("StandardRate", out var rate) && rate.ValueKind == JsonValueKind.Object
                       && rate.TryGetProperty("Amount", out var amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out var dec) ? dec : null,
        StandardRateUnits = r.TryGetProperty("StandardRate", out var rateUnits) && rateUnits.ValueKind == JsonValueKind.Object ? Str(rateUnits, "Units") : null
    };

    private static MppResourceAssignment ReadAssignment(JsonElement a) => new()
    {
        UniqueId = Int(a, "UniqueId") ?? 0,
        Guid = Str(a, "Guid") is { } g && System.Guid.TryParse(g, out var guid) ? guid : null,
        TaskUniqueId = Int(a, "TaskUniqueId"),
        TaskName = Str(a, "TaskName"),
        TaskMissing = Bool(a, "TaskMissing"),
        ResourceUniqueId = Int(a, "ResourceUniqueId"),
        ResourceGuid = Str(a, "ResourceGuid") is { } rg && System.Guid.TryParse(rg, out var resourceGuid) ? resourceGuid : null,
        ResourceName = Str(a, "ResourceName"),
        ResourceMissing = Bool(a, "ResourceMissing"),
        WorkHours = Hours(a, "Work"),
        ActualWorkHours = Hours(a, "ActualWork"),
        RemainingWorkHours = Hours(a, "RemainingWork")
    };

    /// <summary>Lee un objeto { Value, Units } de trabajo y lo devuelve en horas; null si falta o la unidad no es Hours/Minutes.</summary>
    private static decimal? Hours(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object
            || !w.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var d)) return null;
        return Str(w, "Units") switch { "Hours" => d, "Minutes" => d / 60m, _ => null };
    }

    private static MppDependency ReadDependency(JsonElement d) => new()
    {
        RelationUniqueId = Int(d, "RelationUniqueId") ?? 0,
        PredecessorUniqueId = Int(d, "PredecessorUniqueId") ?? throw Missing("Dependencies.PredecessorUniqueId"),
        SuccessorUniqueId = Int(d, "SuccessorUniqueId") ?? throw Missing("Dependencies.SuccessorUniqueId"),
        RelationType = Str(d, "RelationType") ?? throw Missing("Dependencies.RelationType"),
        LagValue = d.TryGetProperty("LagValue", out var lag) && lag.ValueKind == JsonValueKind.Number && lag.TryGetDecimal(out var lagValue) ? lagValue : 0m,
        LagUnits = Str(d, "LagUnits") ?? "Days",
        LagText = Str(d, "LagText")
    };

    private static Exception Missing(string what) => new FormatException($"Falta el campo obligatorio {what}.");

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static List<string> Strings(JsonElement parent, string name)
        => Array(parent, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();

    private static string? Str(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) ? ToInt(v) : null;

    private static DateOnly? Date(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateOnly.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static int? ToInt(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out var i) => i,
        JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null
    };

    private static DateTime? ToDateTime(JsonElement v)
        => v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>Resuelve el calendario por DefaultCalendarUniqueID (nunca por nombre).</summary>
public static class MppCalendarResolver
{
    public static MppCalendar Resolve(MppProject project)
    {
        if (project.DefaultCalendarUniqueId is not { } id)
            throw new CalendarImportException(ImportExitCodes.Functional, "CalendarResolution",
                $"Project.Properties no contiene un valor válido para {MppProjectReader.PropDefaultCalendar}. No se puede determinar el calendario base del proyecto.");
        return project.Calendars.FirstOrDefault(c => c.UniqueId == id)
            ?? throw new CalendarImportException(ImportExitCodes.Functional, "CalendarResolution",
                $"El calendario predeterminado UniqueId={id} no aparece en Calendars[].");
    }
}
