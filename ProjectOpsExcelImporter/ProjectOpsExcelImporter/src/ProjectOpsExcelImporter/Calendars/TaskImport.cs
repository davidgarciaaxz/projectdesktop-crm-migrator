using System.Globalization;
using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter.Calendars;

// Fase de tareas del import de calendarios: SOLO estructura básica (nombre, jerarquía, comienzo, duración).
// No se importan dependencias, recursos, asignaciones, costes ni porcentaje completado, y NO se envía Finish:
// el objetivo es observar qué calcula Project Operations a partir de Start + Duration + calendario del proyecto.

/// <summary>Unidades de calendario para convertir la duración del MPP (Weeks/Hours/Months) a días; vienen de AppSettings, como en el importador existente.</summary>
public sealed record TaskDurationRules(decimal HoursPerDay = 8m, decimal DaysPerWeek = 5m, decimal DaysPerMonth = 20m);

/// <summary>Limita la ejecución: por UniqueIds concretos o por las N primeras tareas del fichero. Los ancestros siempre se incluyen.</summary>
public sealed record TaskSelection(int? MaxTasks = null, IReadOnlyList<int>? UniqueIds = null);

public sealed record PlannedTask(MppTask Source, Guid TaskId, Guid? ParentTaskId, decimal? DurationDays, bool AddedAsAncestor);

public sealed record TaskPlan(IReadOnlyList<PlannedTask> Tasks, IReadOnlyList<string> Warnings);

public static class TaskImportPlanner
{
    public const string Stage = "Tasks";

    /// <summary>GUID determinista: misma fórmula y espacio de nombres que el importador existente (ProjectOpsImporter.EnsureDeterministicIds).</summary>
    public static Guid TaskId(Guid projectSourceGuid, Guid taskSourceGuid)
        => DeterministicGuid.Create(ProjectOpsImporter.IdNamespace, $"{projectSourceGuid:D}:task:{taskSourceGuid:D}");

    /// <param name="projectId">GUID real del proyecto en Project Operations; los GUID de las tareas se derivan de él (misma fórmula que el importador existente).
    /// Si es null (vista previa sin Dataverse) se usa el GUID del MPP como marcador.</param>
    public static TaskPlan Build(MppProject project, TaskDurationRules rules, TaskSelection selection, Guid? projectId = null)
    {
        var projectGuid = projectId ?? project.ProjectGuid ?? throw Fail($"El JSON no contiene un GUID de proyecto válido ({MppProjectReader.PropGuid}).");
        var warnings = new List<string>();

        // El orden de creación y de visualización sigue el campo Id del MPP (no el UniqueId ni el orden del array).
        // La tarea resumen del proyecto (UniqueId 0) no se importa: sus hijas son las tareas raíz. Las tareas nulas tampoco.
        var candidates = project.Tasks.Where(t => !t.IsNull && !t.IsProjectSummaryTask).OrderBy(t => t.Id).ToList();
        var byUid = candidates.ToDictionary(t => t.UniqueId);

        HashSet<int> selected;
        if (selection.UniqueIds is { Count: > 0 } ids)
        {
            var missing = ids.Where(id => !byUid.ContainsKey(id)).ToList();
            if (missing.Count > 0) throw Fail($"UniqueId de tarea no encontrado o no importable: {string.Join(", ", missing)}.");
            selected = [.. ids];
        }
        else if (selection.MaxTasks is { } max)
        {
            if (max < 1) throw Fail("El máximo de tareas debe ser >= 1.");
            selected = [.. candidates.Take(max).Select(t => t.UniqueId)];
        }
        else selected = [.. candidates.Select(t => t.UniqueId)];

        var requested = selected.ToHashSet();
        foreach (var uid in requested.ToList())                       // los ancestros son imprescindibles para conservar la jerarquía
        {
            var parentUid = byUid[uid].ParentUniqueId;
            while (parentUid != 0 && byUid.TryGetValue(parentUid, out var parent) && selected.Add(parent.UniqueId))
                parentUid = parent.ParentUniqueId;
        }

        var ordered = candidates.Where(t => selected.Contains(t.UniqueId)).ToList();
        var index = ordered.Select((t, i) => (t.UniqueId, i)).ToDictionary(x => x.UniqueId, x => x.i);
        var ids2 = new Dictionary<int, Guid>();
        foreach (var t in ordered)
        {
            var taskGuid = t.Guid ?? throw Fail($"La tarea UniqueId={t.UniqueId} no tiene Guid; es necesario para el GUID determinista.");
            ids2[t.UniqueId] = TaskId(projectGuid, taskGuid);
        }

        var planned = new List<PlannedTask>();
        foreach (var t in ordered)
        {
            Guid? parentId = null;
            if (t.ParentUniqueId != 0 && ids2.TryGetValue(t.ParentUniqueId, out var pid))
            {
                if (index[t.ParentUniqueId] > index[t.UniqueId])
                    throw Fail($"La tarea {t.UniqueId} aparece antes que su padre {t.ParentUniqueId}; el orden del fichero no permite crear la jerarquía.");
                parentId = pid;
            }
            else if (t.ParentUniqueId != 0)
                warnings.Add($"Tarea {t.UniqueId}: su padre {t.ParentUniqueId} no es importable; se crea como tarea raíz.");

            planned.Add(new PlannedTask(t, ids2[t.UniqueId], parentId, t.IsSummary ? null : ToDays(t, rules), !requested.Contains(t.UniqueId)));
            if (string.IsNullOrWhiteSpace(t.Name)) warnings.Add($"Tarea {t.UniqueId} sin nombre; se usará 'Task {t.UniqueId}'.");
        }
        return new TaskPlan(planned, warnings);
    }

    /// <summary>Duración en días (2 decimales, como el importador existente). Unidades no soportadas = error explícito.</summary>
    public static decimal? ToDays(MppTask task, TaskDurationRules rules)
    {
        if (task.DurationValue is not { } v) return null;
        var days = (task.DurationUnits ?? "Days").ToLowerInvariant() switch
        {
            "days" => v,
            "hours" => v / rules.HoursPerDay,
            "minutes" => v / (rules.HoursPerDay * 60m),
            "weeks" => v * rules.DaysPerWeek,
            "months" => v * rules.DaysPerMonth,
            var other => throw Fail($"Unidad de duración '{other}' no soportada en la tarea {task.UniqueId}.")
        };
        return Math.Round(days, 2, MidpointRounding.AwayFromZero);
    }

    private static CalendarImportException Fail(string message) => new(ImportExitCodes.Tasks, Stage, message);
}

/// <summary>Notas de la tarea del MPP -> msdyn_descriptionplaintext. Un único criterio de normalización para enviar y para comparar.</summary>
public static class TaskNotes
{
    /// <summary>Texto plano de la nota.</summary>
    public const string Attribute = "msdyn_descriptionplaintext";
    /// <summary>La misma nota en HTML (formato del editor de Project Operations: una &lt;div&gt; por línea).</summary>
    public const string HtmlAttribute = "msdyn_description";

    /// <summary>
    /// Nota ya normalizada -> HTML como el que guarda Project Operations: <c>&lt;div&gt;línea&lt;/div&gt;</c> por línea y <c>&lt;div&gt;&lt;br&gt;&lt;/div&gt;</c> para las líneas vacías
    /// (también las que solo tienen tabuladores). Se escapan &amp; &lt; &gt;, y para que HTML no los pierda cada tabulador pasa a 4 espacios no separables
    /// y las series de espacios se conservan con &amp;nbsp;.
    /// </summary>
    public static string ToHtml(string normalized)
    {
        var html = new System.Text.StringBuilder();
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Trim().Length == 0) { html.Append("<div><br></div>"); continue; }
            var text = line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\t", "    ");
            text = System.Text.RegularExpressions.Regex.Replace(text, " {2,}", m => " " + string.Concat(Enumerable.Repeat("&nbsp;", m.Length - 1)));
            if (text.StartsWith(' ')) text = "&nbsp;" + text[1..];
            html.Append("<div>").Append(text).Append("</div>");
        }
        return html.ToString();
    }

    /// <summary>Saltos de línea unificados (\n) y sin espacios finales; null si no hay texto. Los tabuladores y las líneas intermedias se conservan.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        return value.Length == 0 ? null : value;
    }
}

/// <summary>Notas de una tarea tal como están en Dataverse: texto plano y HTML.</summary>
public sealed record TaskNotesValue(string? Plain, string? Html);

/// <summary>Atributos que se envían a la Project Scheduling API para cada tarea (mismos nombres que el importador existente).</summary>
public static class TaskEntityBuilder
{
    public static JsonObject Build(PlannedTask task, Guid projectId, Guid bucketId)
    {
        var s = task.Source;
        var entity = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask",
            ["msdyn_projecttaskid"] = task.TaskId.ToString("D"),
            ["msdyn_subject"] = string.IsNullOrWhiteSpace(s.Name) ? $"Task {s.UniqueId}" : s.Name,
            ["msdyn_project@odata.bind"] = $"/msdyn_projects({projectId:D})",
            ["msdyn_projectbucket@odata.bind"] = $"/msdyn_projectbuckets({bucketId:D})",
            ["msdyn_LinkStatus"] = 192350000,
            ["msdyn_outlinelevel"] = s.OutlineLevel,
            // Orden visible de la tarea = campo Id del MPP (así no depende del orden ni del lote en que se creen).
            // Project Operations exige un valor decimal MAYOR que 1 (comprobado: rechaza 1), por eso Id + 1; el orden relativo se conserva.
            ["msdyn_displaysequence"] = (double)(s.Id + 1)
        };
        if (task.ParentTaskId is { } parent) entity["msdyn_parenttask@odata.bind"] = $"/msdyn_projecttasks({parent:D})";
        if (TaskNotes.Normalize(s.Notes) is { } notes)
        {
            entity[TaskNotes.Attribute] = notes;                          // texto plano
            entity[TaskNotes.HtmlAttribute] = TaskNotes.ToHtml(notes);    // mismo contenido en HTML
        }

        // Las tareas resumen las calcula Project Operations a partir de sus hijas. Finish no se envía nunca.
        if (!s.IsSummary)
        {
            if (s.Start is { } start) entity["msdyn_scheduledstart"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            if (task.DurationDays is { } days) entity["msdyn_duration"] = days;     // los hitos van con duración 0
        }
        return entity;
    }
}

public sealed record TaskSnapshot(Guid Id, string? Name, DateTime? Start, DateTime? End, decimal? DurationDays, decimal? Effort = null,
    decimal? Progress = null, decimal? EffortCompleted = null, decimal? EffortRemaining = null);

public interface IProjectTaskService
{
    Task<Guid> GetDefaultBucketIdAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> GetExistingTaskIdsAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Crea las tareas con la Project Scheduling API (OperationSet + PssCreate + ExecuteOperationSet).</summary>
    Task CreateTasksAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken cancellationToken);
    Task WaitForTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskSnapshot>> ReadTasksAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Notas actuales (texto plano y HTML) de las tareas del proyecto: id -> valores (null si están vacíos).</summary>
    Task<IReadOnlyDictionary<Guid, TaskNotesValue>> ReadTaskNotesAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Actualiza msdyn_descriptionplaintext y msdyn_description de las tareas con msdyn_PssUpdateV2 y espera a que lo reflejen (o a que el servicio informe de un error).</summary>
    Task UpdateTaskNotesAsync(Guid projectId, IReadOnlyList<(Guid TaskId, string Plain, string Html)> updates, CancellationToken cancellationToken);
}

public static class TaskCrossing
{
    public static HashSet<DateOnly> NonWorkingDays(IEnumerable<NormalizedCalendarException> exceptions)
    {
        var set = new HashSet<DateOnly>();
        foreach (var e in exceptions.Where(e => !e.Working))
            for (var d = e.From; d <= e.To; d = d.AddDays(1)) set.Add(d);
        return set;
    }

    /// <summary>Días no laborables (excepciones) que caen dentro del período Start..Finish del MPP.</summary>
    public static List<DateOnly> Crossed(MppTask task, HashSet<DateOnly> nonWorkingDays)
    {
        if (task.IsSummary || task.Start is not { } start || task.Finish is not { } finish) return [];
        var from = DateOnly.FromDateTime(start);
        var to = DateOnly.FromDateTime(finish);
        return nonWorkingDays.Where(d => d >= from && d <= to).OrderBy(d => d).ToList();
    }
}

public sealed record TaskImportSummary(int Selected, int Created, int AlreadyExisting, int Match, int Difference, int CrossingMatch, int CrossingDifference, bool ReadbackComplete,
    int NotesSent = 0, int NotesUpdated = 0, int NotesAlreadyEqual = 0);

/// <summary>Crea las tareas (idempotente), lee de Dataverse lo que calcula Project Operations y lo compara con el MPP sin corregir nada.</summary>
public sealed class ProjectTaskImporter
{
    private readonly IProjectTaskService _service;
    private readonly CalendarImportSettings _settings;
    private readonly TextWriter _out;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ProjectTaskImporter(IProjectTaskService service, CalendarImportSettings settings, TextWriter output, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service;
        _settings = settings;
        _out = output;
        _delay = delay ?? Task.Delay;
    }

    public async Task<TaskImportSummary> RunAsync(Guid projectId, MppProject project, IReadOnlyList<NormalizedCalendarException> exceptions,
        ImportState state, Action save, CancellationToken ct)
    {
        var plan = TaskImportPlanner.Build(project, _settings.DurationRules, new TaskSelection(_settings.TaskMaxCount, _settings.TaskUniqueIds), projectId);
        var summaryCount = plan.Tasks.Count(t => t.Source.IsSummary);
        var milestones = plan.Tasks.Count(t => !t.Source.IsSummary && t.DurationDays == 0);
        Line("Selected", $"{plan.Tasks.Count} ({summaryCount} summary, {plan.Tasks.Count - summaryCount} leaf, of which {milestones} milestones; {plan.Tasks.Count(t => t.AddedAsAncestor)} added as ancestors)");
        foreach (var w in plan.Warnings) Line("WARNING", w);

        var bucketId = await GetBucketAsync(projectId, ct).ConfigureAwait(false);
        state.BucketId = bucketId;
        var existing = await _service.GetExistingTaskIdsAsync(projectId, ct).ConfigureAwait(false);
        var pending = plan.Tasks.Where(t => !existing.Contains(t.TaskId)).ToList();
        foreach (var t in plan.Tasks) state.TaskIds[t.Source.UniqueId] = t.TaskId;                // UniqueID MPP -> GUID en Project Operations
        foreach (var t in plan.Tasks.Where(t => existing.Contains(t.TaskId))) state.CreatedTasks.Add(t.Source.UniqueId);
        save();
        Line("Existing", $"{plan.Tasks.Count - pending.Count} already in Dataverse (not recreated)");

        var created = 0;
        foreach (var batch in pending.Chunk(_settings.TaskBatchSize))
        {
            await _service.CreateTasksAsync(projectId, batch.Select(t => TaskEntityBuilder.Build(t, projectId, bucketId)).ToList(), ct).ConfigureAwait(false);
            await _service.WaitForTaskAsync(batch[^1].TaskId, ct).ConfigureAwait(false);
            foreach (var t in batch) state.CreatedTasks.Add(t.Source.UniqueId);
            created += batch.Length;
            save();
            Line("Created", $"{created}/{pending.Count}");
        }

        // Notas: las tareas nuevas ya las llevan en la creación; las que ya existían se actualizan solo si el texto difiere.
        var notesSent = pending.Count(t => TaskNotes.Normalize(t.Source.Notes) is not null);
        int notesUpdated = 0, notesEqual = 0;
        var pendingIds = pending.Select(t => t.TaskId).ToHashSet();
        var withNotes = plan.Tasks.Where(t => !pendingIds.Contains(t.TaskId) && TaskNotes.Normalize(t.Source.Notes) is not null).ToList();
        if (withNotes.Count > 0)
        {
            var current = await _service.ReadTaskNotesAsync(projectId, ct).ConfigureAwait(false);
            var changes = new List<(Guid TaskId, string Plain, string Html)>();
            foreach (var t in withNotes)
            {
                var target = TaskNotes.Normalize(t.Source.Notes)!;
                var targetHtml = TaskNotes.ToHtml(target);
                if (current.TryGetValue(t.TaskId, out var now) && TaskNotes.Normalize(now.Plain) == target && now.Html == targetHtml) notesEqual++;
                else changes.Add((t.TaskId, target, targetHtml));
            }
            foreach (var batch in changes.Chunk(_settings.TaskBatchSize))
            {
                await _service.UpdateTaskNotesAsync(projectId, batch, ct).ConfigureAwait(false);
                notesUpdated += batch.Length;
            }
        }
        Line("Notes", $"sent with creation {notesSent}; updated on existing tasks {notesUpdated}; already equal {notesEqual} (msdyn_descriptionplaintext + msdyn_description as HTML)");

        var (snapshots, complete) = await ReadBackAsync(projectId, plan, ct).ConfigureAwait(false);
        return Compare(plan, snapshots, complete, TaskCrossing.NonWorkingDays(exceptions), created, plan.Tasks.Count - pending.Count) with
        {
            NotesSent = notesSent, NotesUpdated = notesUpdated, NotesAlreadyEqual = notesEqual
        };
    }

    private async Task<Guid> GetBucketAsync(Guid projectId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await _service.GetDefaultBucketIdAsync(projectId, ct).ConfigureAwait(false); }
            catch (InvalidDataException) when (attempt < 5) { await _delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false); }
        }
    }

    /// <summary>Project Operations puede tardar en calcular fechas: se reintenta hasta que todas las tareas no resumen tengan Start y End.</summary>
    private async Task<(Dictionary<Guid, TaskSnapshot> Snapshots, bool Complete)> ReadBackAsync(Guid projectId, TaskPlan plan, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_settings.TaskReadbackTimeoutSeconds);
        while (true)
        {
            var all = (await _service.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(s => s.Id);
            var ready = plan.Tasks.Where(t => !t.Source.IsSummary).All(t => all.TryGetValue(t.TaskId, out var s) && s.Start is not null && s.End is not null);
            if (ready || DateTime.UtcNow >= deadline) return (all, ready);
            await _delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private TaskImportSummary Compare(TaskPlan plan, Dictionary<Guid, TaskSnapshot> snapshots, bool complete, HashSet<DateOnly> nonWorking, int created, int existing)
    {
        var byUid = plan.Tasks.ToDictionary(t => t.Source.UniqueId, t => t.Source);
        int match = 0, diff = 0, crossMatch = 0, crossDiff = 0;
        foreach (var task in plan.Tasks)
        {
            var s = task.Source;
            snapshots.TryGetValue(task.TaskId, out var po);
            var crossed = TaskCrossing.Crossed(s, nonWorking);
            var reasons = new List<string>();
            if (po is null) reasons.Add("not found in Dataverse");
            else
            {
                if (s.Start is { } ms && po.Start is { } ps && DateOnly.FromDateTime(ms) != DateOnly.FromDateTime(ps)) reasons.Add("start date");
                if (s.Finish is { } mf && po.End is { } pe && DateOnly.FromDateTime(mf) != DateOnly.FromDateTime(pe)) reasons.Add("finish date");
                if (po.Start is null || po.End is null) reasons.Add("PO dates not calculated yet");
                if (!s.IsSummary && task.DurationDays is { } md && po.DurationDays is { } pd && Math.Abs(md - pd) > 0.01m) reasons.Add("duration");
            }
            var isMatch = reasons.Count == 0;
            if (isMatch) match++; else diff++;
            if (crossed.Count > 0) { if (isMatch) crossMatch++; else crossDiff++; }

            _out.WriteLine();
            _out.WriteLine("[Task]");
            Line("MPP UniqueID", s.UniqueId.ToString());
            Line("Name", s.Name);
            Line("Parent", s.ParentUniqueId != 0 && byUid.TryGetValue(s.ParentUniqueId, out var parent) ? $"{parent.UniqueId} ({parent.Name})" : "-");
            Line("Kind", s.IsSummary ? "summary" : task.DurationDays == 0 ? "milestone" : "task");
            Line("MPP Start", Fmt(s.Start));
            Line("MPP Finish", Fmt(s.Finish) + "   (comparison only; not sent)");
            Line("MPP Duration", s.DurationValue is { } dv ? $"{dv.ToString(CultureInfo.InvariantCulture)} {s.DurationUnits}" + (task.DurationDays is { } d && s.DurationUnits != "Days" ? $"  (= {d.ToString(CultureInfo.InvariantCulture)} days sent)" : string.Empty) : "-");
            Line("Project Operations ID", task.TaskId.ToString("D"));
            Line("PO Start", Fmt(po?.Start));
            Line("PO Finish", Fmt(po?.End));
            Line("PO Duration", po?.DurationDays is { } pdur ? $"{pdur.ToString(CultureInfo.InvariantCulture)} days" : "-");
            if (crossed.Count > 0)
                Line("Crosses exceptions", $"{crossed.Count} non-working day(s): {string.Join(", ", crossed.Take(6).Select(c => c.ToString("yyyy-MM-dd")))}{(crossed.Count > 6 ? ", ..." : string.Empty)}");
            Line("Result", isMatch ? "MATCH" : $"DIFFERENCE ({string.Join(", ", reasons)})");
        }

        _out.WriteLine();
        _out.WriteLine("[Tasks summary]");
        Line("Tasks", plan.Tasks.Count.ToString());
        Line("MATCH / DIFFERENCE", $"{match} / {diff}");
        Line("Crossing exceptions", $"{crossMatch + crossDiff} tasks (MATCH {crossMatch}, DIFFERENCE {crossDiff})");
        if (!complete) Line("WARNING", "Project Operations had not finished calculating dates for every task when the read-back timed out.");
        Line("Note", "Differences are observations of what Project Operations calculated (times are UTC as returned); nothing is corrected automatically.");
        return new TaskImportSummary(plan.Tasks.Count, created, existing, match, diff, crossMatch, crossDiff, complete);
    }

    private static string Fmt(DateTime? value) => value is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (v.Kind == DateTimeKind.Utc ? "Z" : string.Empty) : "-";
    private void Line(string label, string value) => _out.WriteLine($"      {label,-20}: {value}");
}

/// <summary>Lectura/escritura en Dataverse con los nombres que ya usa el importador existente (OperationSet + msdyn_PssCreateV2).</summary>
public sealed class DataverseProjectTaskService : IProjectTaskService
{
    private readonly DataverseClient _client;
    private readonly int _operationTimeoutSeconds;
    private Guid _lastOperationSetId;

    public DataverseProjectTaskService(DataverseClient client, int operationTimeoutSeconds = 180)
    {
        _client = client;
        _operationTimeoutSeconds = operationTimeoutSeconds;
    }

    public async Task<Guid> GetDefaultBucketIdAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync($"msdyn_projectbuckets?$select=msdyn_projectbucketid&$filter={filter}&$top=1", ct).ConfigureAwait(false);
        var values = response["value"]?.AsArray() ?? throw new InvalidDataException("Dataverse no devolvió el array value al buscar el bucket.");
        if (values.Count == 0) throw new InvalidDataException("No se encontró el bucket predeterminado del proyecto.");
        return JsonHelpers.RequiredGuid(values[0]!.AsObject(), "msdyn_projectbucketid");
    }

    /// <summary>Hasta 5000 tareas (sin paginación); suficiente para las pruebas actuales.</summary>
    public async Task<IReadOnlySet<Guid>> GetExistingTaskIdsAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync($"msdyn_projecttasks?$select=msdyn_projecttaskid&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var set = new HashSet<Guid>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (Guid.TryParse(item?["msdyn_projecttaskid"]?.GetValue<string>(), out var id)) set.Add(id);
        return set;
    }

    public async Task CreateTasksAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken ct)
    {
        var operationSet = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Importación MPP: crear {entities.Count} tareas"
        }, ct).ConfigureAwait(false);
        var operationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        _lastOperationSetId = operationSetId;

        var array = new JsonArray();
        foreach (var entity in entities) array.Add(entity);
        await _client.PostActionAsync("msdyn_PssCreateV2", new JsonObject
        {
            ["EntityCollection"] = array,
            ["OperationSetId"] = operationSetId.ToString("D")
        }, ct).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);
    }

    // Si el servicio de planificación rechaza el lote, se informa enseguida con su mensaje en lugar de esperar al timeout.
    public Task WaitForTaskAsync(Guid taskId, CancellationToken ct)
        => _lastOperationSetId == Guid.Empty
            ? _client.WaitForRecordAsync("msdyn_projecttasks", taskId, ct)
            : PssOperationSets.WaitForRecordOrFailureAsync(_client, _lastOperationSetId, "msdyn_projecttasks", taskId, _operationTimeoutSeconds, ct);

    public async Task<IReadOnlyList<TaskSnapshot>> ReadTasksAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync(
            $"msdyn_projecttasks?$select=msdyn_projecttaskid,msdyn_subject,msdyn_scheduledstart,msdyn_scheduledend,msdyn_duration,msdyn_effort,msdyn_progress,msdyn_effortcompleted,msdyn_effortremaining&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var list = new List<TaskSnapshot>();
        foreach (var item in response["value"] as JsonArray ?? [])
        {
            if (!Guid.TryParse(item?["msdyn_projecttaskid"]?.GetValue<string>(), out var id)) continue;
            list.Add(new TaskSnapshot(id, item!["msdyn_subject"]?.GetValue<string>(),
                ParseUtc(item["msdyn_scheduledstart"]), ParseUtc(item["msdyn_scheduledend"]),
                item["msdyn_duration"] is { } d ? (decimal?)d.GetValue<decimal>() : null,
                item["msdyn_effort"] is { } e ? (decimal?)e.GetValue<decimal>() : null,
                item["msdyn_progress"] is { } pr ? (decimal?)pr.GetValue<decimal>() : null,
                item["msdyn_effortcompleted"] is { } ec ? (decimal?)ec.GetValue<decimal>() : null,
                item["msdyn_effortremaining"] is { } er ? (decimal?)er.GetValue<decimal>() : null));
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<Guid, TaskNotesValue>> ReadTaskNotesAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync($"msdyn_projecttasks?$select=msdyn_projecttaskid,{TaskNotes.Attribute},{TaskNotes.HtmlAttribute}&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var map = new Dictionary<Guid, TaskNotesValue>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (Guid.TryParse(item?["msdyn_projecttaskid"]?.GetValue<string>(), out var id))
                map[id] = new TaskNotesValue(item![TaskNotes.Attribute]?.GetValue<string>(), item[TaskNotes.HtmlAttribute]?.GetValue<string>());
        return map;
    }

    public async Task UpdateTaskNotesAsync(Guid projectId, IReadOnlyList<(Guid TaskId, string Plain, string Html)> updates, CancellationToken ct)
    {
        var operationSet = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Importación MPP: notas de {updates.Count} tareas"
        }, ct).ConfigureAwait(false);
        var operationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        var array = new JsonArray();
        foreach (var (taskId, plain, html) in updates)
            array.Add(new JsonObject
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask",
                ["msdyn_projecttaskid"] = taskId.ToString("D"),
                [TaskNotes.Attribute] = plain,
                [TaskNotes.HtmlAttribute] = html
            });
        await _client.PostActionAsync("msdyn_PssUpdateV2", new JsonObject { ["EntityCollection"] = array, ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_operationTimeoutSeconds);
        while (true)
        {
            var current = await ReadTaskNotesAsync(projectId, ct).ConfigureAwait(false);
            // Se espera al texto plano exacto y a que el HTML esté informado (no se exige igualdad exacta del HTML por si la plataforma lo reescribe).
            if (updates.All(u => current.TryGetValue(u.TaskId, out var now) && TaskNotes.Normalize(now.Plain) == u.Plain && !string.IsNullOrEmpty(now.Html))) return;
            var set = await _client.GetAsync($"msdyn_operationsets({operationSetId:D})?$select=msdyn_status,_msdyn_psserrorlog_value", ct).ConfigureAwait(false);
            if (Guid.TryParse(set["_msdyn_psserrorlog_value"]?.GetValue<string>(), out var logId))
            {
                var log = await _client.GetAsync($"msdyn_psserrorlogs({logId:D})?$select=msdyn_errorcode,msdyn_log", ct).ConfigureAwait(false);
                throw new InvalidOperationException($"El servicio de planificación rechazó las notas (OperationSet {operationSetId:D}): {log["msdyn_errorcode"]}: {log["msdyn_log"]}");
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Las tareas no tomaron las notas enviadas en {_operationTimeoutSeconds} s (OperationSet {operationSetId:D}, estado {set["msdyn_status"]}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private static DateTime? ParseUtc(JsonNode? node)
        => node?.GetValue<string>() is { } text
           && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
