using System.Text.Json;
using ProjectOpsExcelImporter.Calendars;

namespace ProjectOpsExcelImporter.Tests;

internal static class Dump
{
    public const string SourceGuid = "b9a3b3ae-94cd-f111-aa67-9c971b260c22";
    private static readonly string[] Days =["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public static object Day(string day, string[] periods)
        => new { Day = day, DefinedDayType = periods.Length > 0 ? "Working" : "NonWorking", DefinedPeriods = periods,
                 EffectiveWorking = periods.Length > 0, EffectivePeriods = periods };

    public static object WwDay(string day, string type, string[] defined)
        => new { Day = day, DefinedDayType = type, DefinedPeriods = defined, EffectiveWorking = (bool?)null, EffectivePeriods = Array.Empty<string>() };

    /// <summary>Lunes-viernes con los periodos indicados; sábado/domingo no laborables.</summary>
    public static object[] WeekDays(params string[] weekdayPeriods)
        => Days.Select(d => Day(d, d is "Saturday" or "Sunday" ? [] : weekdayPeriods)).ToArray();

    public static object[] Tbt()
        => Days.Select(d => Day(d, d switch
        {
            "Friday" => ["08:00-13:30"],
            "Saturday" or "Sunday" => [],
            _ => ["08:00-17:00"]
        })).ToArray();

    public static object Calendar(int uid, string name, object[] days, object[]? workWeeks = null)
        => new { UniqueId = uid, Name = name, Days = days, WorkWeeks = workWeeks ?? [] };

    public static object Exception(int calUid, string name, string from, string to, bool working = false, string[]? periods = null,
        bool recurring = false, string? type = null, int? occurrences = null, int? month = null, int? dayNumber = null,
        string? recStart = null, string? recFinish = null, int? expandedCount = 1, string[]? weekly = null, bool relative = false)
        => new
        {
            CalendarUniqueId = calUid, CalendarName = "x", Name = name, FromDate = from, ToDate = to, Working = working,
            Periods = periods ?? [], IsRecurring = recurring, RecurrenceType = type, Frequency = (int?)null, Occurrences = occurrences,
            UseEndDate = false, RecurrenceStart = recStart, RecurrenceFinish = recFinish, Relative = relative, DayOfWeek = (string?)null,
            DayNumber = dayNumber, MonthNumber = month, WeeklyDays = weekly ?? [], RecurrenceDescription = $"[{type} {name}]",
            ExpandedOccurrenceCount = expandedCount
        };

    public static string Build(object[] calendars, object[]? exceptions = null, int? defaultCalendarUid = 1,
        string title = "PROYECTO", string start = "2024-01-01T09:00:00", string finish = "2026-12-31T18:00:00", object[]? tasks = null, object[]? dependencies = null, object[]? resources = null, object[]? resourceAssignments = null)
        => JsonSerializer.Serialize(new
        {
            Project = new
            {
                Properties = new object[]
                {
                    new { MpxjProperty = "ProjectProperties.ProjectTitle", Value = (object?)title },
                    new { MpxjProperty = "ProjectProperties.GUID", Value = (object?)SourceGuid },
                    new { MpxjProperty = "ProjectProperties.StartDate", Value = (object?)start },
                    new { MpxjProperty = "ProjectProperties.FinishDate", Value = (object?)finish },
                    new { MpxjProperty = "ProjectProperties.DefaultCalendarUniqueID", Value = (object?)defaultCalendarUid }
                }
            },
            Tasks = tasks ?? [],
            Dependencies = dependencies ?? [],
            Resources = resources ?? [],
            ResourceAssignments = resourceAssignments ?? [],
            Calendars = calendars,
            CalendarExceptions = exceptions ?? []
        });

    public static MppProject Project(object[] calendars, object[]? exceptions = null, int? defaultCalendarUid = 1)
        => MppProjectReader.Parse(Build(calendars, exceptions, defaultCalendarUid));

    public static string[] P(params string[] periods) => periods;

    /// <summary>Guid de un recurso de prueba a partir de su UniqueId.</summary>
    public static string ResourceGuid(int uid) => $"00000000-0000-0000-0001-{uid:000000000000}";

    /// <summary>Recurso de Resources[] con los campos que lee el importador.</summary>
    public static object Resource(int uid, string? name, string type = "Work", int? id = null, bool isNull = false)
        => new { Id = id ?? uid, UniqueId = uid, Guid = ResourceGuid(uid), Name = name, Type = type, IsNull = isNull };

    /// <summary>Asignación de ResourceAssignments[]; con resourceUid null (o resourceMissing) no tiene recurso.</summary>
    public static object Assignment(int uid, int taskUid, int? resourceUid, string? resourceName = null, bool resourceMissing = false, bool taskMissing = false, decimal? work = null)
        => new
        {
            Work = work is null ? null : new { Value = work.Value, Units = "Hours" },
            UniqueId = uid, Guid = $"00000000-0000-0000-0002-{uid:000000000000}", TaskUniqueId = taskUid, TaskName = $"Tarea {taskUid}", TaskMissing = taskMissing,
            ResourceUniqueId = resourceUid, ResourceGuid = resourceUid is { } r ? ResourceGuid(r) : "a0cb8b78-2a8c-436d-0000-0000000000ff",
            ResourceName = resourceName, ResourceMissing = resourceMissing || resourceUid is null
        };

    /// <summary>Relación de Dependencies[] con los campos que lee el importador.</summary>
    public static object Dependency(int relation, int predecessorUid, int successorUid, string type = "FinishStart", decimal lag = 0, string units = "Days")
        => new { RelationUniqueId = relation, PredecessorUniqueId = predecessorUid, SuccessorUniqueId = successorUid, RelationType = type, LagValue = lag, LagUnits = units, LagText = $"{lag} {units}" };

    /// <summary>Tarea de Tasks[] con los campos que lee el importador (el Guid se deriva del UniqueId).</summary>
    public static object TaskRow(int uid, string name, int level, int parent, string? start = null, string? finish = null, decimal dur = 1, string units = "Days",
        bool summary = false, bool milestone = false, bool isNull = false, bool projectSummary = false, string? notes = null, decimal? percent = null)
        => new
        {
            Notes = notes,
            PercentComplete = percent,
            Id = uid, UniqueId = uid, Guid = $"00000000-0000-0000-0000-{uid:000000000000}", Name = name, OutlineLevel = level, ParentUniqueId = parent,
            IsSummary = summary, IsMilestone = milestone, IsNull = isNull, IsProjectSummaryTask = projectSummary, IsActive = true,
            Start = start, Finish = finish, Duration = new { Value = dur, Units = units }
        };
}

internal sealed class FakeTemplates : ICalendarTemplateRepository
{
    public List<CalendarTemplateRecord> Items { get; } = [];
    public bool Configured { get; set; } = true;

    public void EnsureConfigured()
    {
        if (!Configured)
            throw new CalendarMetadataNotConfiguredException(CalendarMetadataArea.CalendarTemplate, ["CalendarTemplateEntitySet"],
                "Calendar Template entity metadata has not been confirmed. Run calendar-metadata and configure CalendarImport.DataverseMetadata.");
    }
    public Task<IReadOnlyList<CalendarTemplateRecord>> FindByNameAsync(string name, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CalendarTemplateRecord>>(Items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList());
    public Task<CalendarTemplateRecord?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
}

internal sealed class FakeProjects : IProjectOperationsProjectService
{
    public HashSet<Guid> Existing { get; } = [];
    public int CreateCalls { get; private set; }
    public void EnsureConfigured() { }
    public Task<bool> ProjectExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Existing.Contains(id));
    public Dictionary<Guid, Guid?> LinkedTemplates { get; } = [];
    /// <summary>GUIDs de Project Desktop con los que se creó un proyecto.</summary>
    public List<Guid> CreatedSourceGuids { get; } = [];
    /// <summary>Proyectos ya existentes localizables por el GUID de Project Desktop (el campo axz_mppguid).</summary>
    public Dictionary<Guid, Guid> BySourceGuid { get; } = [];
    public Task<Guid?> FindProjectBySourceGuidAsync(Guid mppGuid, CancellationToken ct)
        => Task.FromResult<Guid?>(BySourceGuid.TryGetValue(mppGuid, out var id) ? id : null);
    public Task<Guid?> GetLinkedTemplateIdAsync(Guid id, CancellationToken ct) => Task.FromResult(LinkedTemplates.GetValueOrDefault(id));
    /// <summary>msdyn_actualstart de cada proyecto y las escrituras recibidas.</summary>
    /// <summary>msdyn_allowpercentcompleteupdate de cada proyecto (un proyecto nuevo nace con false) y las escrituras recibidas.</summary>
    public Dictionary<Guid, bool> AllowPercentUpdate { get; } = [];
    public List<Guid> AllowPercentWrites { get; } = [];
    public Task<ProjectSettingResult> EnsureAllowPercentCompleteUpdateAsync(Guid projectId, CancellationToken ct)
    {
        var before = AllowPercentUpdate.TryGetValue(projectId, out var current) ? current : false;   // por defecto false, como un proyecto nuevo
        if (before) return Task.FromResult(new ProjectSettingResult(true, false));
        AllowPercentWrites.Add(projectId);
        AllowPercentUpdate[projectId] = true;
        return Task.FromResult(new ProjectSettingResult(false, true));
    }
    public Dictionary<Guid, DateTime> ActualStarts { get; } = [];
    public List<(Guid ProjectId, DateTime Start)> ActualStartWrites { get; } = [];
    public Task<ProjectActualStartResult> EnsureProjectActualStartAsync(Guid projectId, DateTime actualStart, CancellationToken ct)
    {
        ActualStarts.TryGetValue(projectId, out var before);
        if (ActualStarts.TryGetValue(projectId, out var current) && current == actualStart) return Task.FromResult(new ProjectActualStartResult(current, false));
        ActualStartWrites.Add((projectId, actualStart));
        ActualStarts[projectId] = actualStart;
        return Task.FromResult(new ProjectActualStartResult(before == default ? null : before, true));
    }
    public List<string?> SourceFileNames { get; } = [];
    public Task<ProjectCreationResult> CreateProjectAsync(string name, DateTime? start, CalendarTemplateResolution t, Guid mppGuid, string? sourceFileName, CancellationToken ct)
    {
        CreateCalls++;
        SourceFileNames.Add(sourceFileName);
        CreatedSourceGuids.Add(mppGuid);
        var id = Guid.NewGuid();                    // GUID propio de Project Operations, distinto del del MPP
        BySourceGuid[mppGuid] = id;
        Existing.Add(id);
        LinkedTemplates[id] = t.Id;
        return Task.FromResult(new ProjectCreationResult(id, name, t.Id, t.Name, t.Fingerprint, Guid.NewGuid(), null, "fake", true));
    }
}

internal sealed class FakeCalendars : IProjectCalendarRepository
{
    public string Pattern { get; set; } = string.Empty;
    public int MissingAttempts { get; set; }
    public int LookupCalls { get; private set; }
    public HashSet<string> ExistingWorkWeeks { get; } = [];
    public HashSet<string> ExistingExceptions { get; } = [];
    public List<string> CreatedWorkWeeks { get; } = [];
    public List<string> CreatedExceptions { get; } = [];
    public int EnsureWorkWeekCalls { get; private set; }
    public int EnsureExceptionCalls { get; private set; }
    public Guid CalendarId { get; } = Guid.NewGuid();

    public void EnsureConfigured() { }
    public Task<ProjectCalendarInfo?> TryGetProjectCalendarAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult(++LookupCalls <= MissingAttempts ? null : new ProjectCalendarInfo(CalendarId, Pattern));
    public Task<ApplyResult> EnsureWorkWeekAsync(Guid calendarId, NormalizedWorkWeek ww, CancellationToken ct)
    {
        EnsureWorkWeekCalls++;
        if (ExistingWorkWeeks.Contains(ww.Key)) return Task.FromResult(new ApplyResult(ApplyOutcome.Reused));
        CreatedWorkWeeks.Add(ww.Key);
        return Task.FromResult(new ApplyResult(ApplyOutcome.Created));
    }
    public Task<ApplyResult> EnsureExceptionAsync(Guid calendarId, NormalizedCalendarException ex, CancellationToken ct)
    {
        EnsureExceptionCalls++;
        if (ExistingExceptions.Contains(ex.Key)) return Task.FromResult(new ApplyResult(ApplyOutcome.Reused));
        CreatedExceptions.Add(ex.Key);
        return Task.FromResult(new ApplyResult(ApplyOutcome.Created));
    }
}

internal static class Files
{
    /// <summary>Localiza project-dump.schema.json subiendo desde el directorio de ejecución.</summary>
    public static string Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "project-dump.schema.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("No se encontró project-dump.schema.json.");
    }

    public static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "poi-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
