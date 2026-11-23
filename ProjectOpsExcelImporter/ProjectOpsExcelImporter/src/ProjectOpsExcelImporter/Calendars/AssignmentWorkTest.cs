using System.Globalization;
using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter.Calendars;

/// <summary>
/// EXPERIMENTAL y aislado (no forma parte de la importación): actualiza el esfuerzo de las asignaciones ya creadas de las tareas indicadas con el trabajo del MPP
/// (Work -> msdyn_effort, ActualWork -> msdyn_effortcompleted, RemainingWork -> msdyn_effortremaining) mediante la Scheduling API (msdyn_PssUpdateV2),
/// y muestra antes y después la asignación y la tarea (Start, Finish, Duration, Effort). Sin --execute true es un dry-run: solo lee y muestra el payload.
/// </summary>
public static class AssignmentWorkTest
{
    public static async Task<int> RunAsync(MppProject project, CalendarImportSettings settings, DataverseClient client, bool execute, TextWriter o, CancellationToken ct)
    {
        var mppGuid = project.ProjectGuid ?? throw new InvalidOperationException("El JSON no tiene ProjectProperties.GUID.");
        var projectId = await new DataverseProjectOperationsProjectService(client, settings).FindProjectBySourceGuidAsync(mppGuid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No hay ningún proyecto con axz_mppguid = {mppGuid:D} en Dataverse.");
        var taskPlan = TaskImportPlanner.Build(project, settings.DurationRules, new TaskSelection(settings.TaskMaxCount, settings.TaskUniqueIds), projectId);
        var plan = ResourceImportPlanner.Build(project, taskPlan, projectId, settings);
        var items = plan.Assignments.ToList();
        if (items.Count == 0) { o.WriteLine("No hay asignaciones utilizables para las tareas indicadas (--task-ids)."); return 1; }

        var tasks = new DataverseProjectTaskService(client, settings.OperationTimeoutSeconds);
        var taskBefore = (await tasks.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var assignBefore = await ReadAssignmentsAsync(client, projectId, ct).ConfigureAwait(false);

        var entities = new List<JsonObject>();
        foreach (var a in items)
        {
            if (!assignBefore.ContainsKey(a.AssignmentId)) { o.WriteLine($"La asignación {a.AssignmentId:D} (UniqueID {a.Source.UniqueId}) no existe en Dataverse; ejecuta antes la importación de recursos."); return 1; }
            var entity = new JsonObject
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_resourceassignment",
                ["msdyn_resourceassignmentid"] = a.AssignmentId.ToString("D")
            };
            if (a.Source.WorkHours is { } work) entity["msdyn_effort"] = work;
            if (a.Source.ActualWorkHours is { } actual) entity["msdyn_effortcompleted"] = actual;
            if (a.Source.RemainingWorkHours is { } remaining) entity["msdyn_effortremaining"] = remaining;
            entities.Add(entity);
        }

        o.WriteLine($"[Assignment work test] proyecto {projectId:D}; {items.Count} asignación(es); modo {(execute ? "EXECUTE" : "DRY-RUN")}");
        o.WriteLine("Payload de msdyn_PssUpdateV2 (EntityCollection):");
        o.WriteLine(new JsonArray(entities.Select(e => (JsonNode?)e.DeepClone()).ToArray()).ToJsonString(JsonDefaults.Options));
        if (!execute) { o.WriteLine("Dry-run: no se ha escrito nada. Repite con --execute true."); return 0; }

        var operationSet = await client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Prueba MPP: trabajo de {entities.Count} asignaciones"
        }, ct).ConfigureAwait(false);
        var operationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        await client.PostActionAsync("msdyn_PssUpdateV2", new JsonObject { ["EntityCollection"] = new JsonArray(entities.Select(e => (JsonNode?)e.DeepClone()).ToArray()), ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);
        await client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);

        // Espera a que las asignaciones tengan los valores enviados (o a que el servicio informe de un error).
        var deadline = DateTime.UtcNow.AddSeconds(settings.OperationTimeoutSeconds);
        Dictionary<Guid, JsonNode> assignAfter;
        while (true)
        {
            assignAfter = await ReadAssignmentsAsync(client, projectId, ct).ConfigureAwait(false);
            if (items.All(a => Matches(a, assignAfter.GetValueOrDefault(a.AssignmentId)))) break;
            var set = await client.GetAsync($"msdyn_operationsets({operationSetId:D})?$select=msdyn_status,_msdyn_psserrorlog_value", ct).ConfigureAwait(false);
            if (Guid.TryParse(set["_msdyn_psserrorlog_value"]?.GetValue<string>(), out var logId))
            {
                var log = await client.GetAsync($"msdyn_psserrorlogs({logId:D})?$select=msdyn_errorcode,msdyn_log", ct).ConfigureAwait(false);
                o.WriteLine($"El servicio de planificación rechazó la actualización (OperationSet {operationSetId:D}): {log["msdyn_errorcode"]}: {log["msdyn_log"]}");
                return 2;
            }
            if (DateTime.UtcNow >= deadline)
            {
                o.WriteLine($"Las asignaciones no tomaron los valores enviados en {settings.OperationTimeoutSeconds} s (OperationSet {operationSetId:D}, estado {set["msdyn_status"]}); se muestra lo que hay.");
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        // Tareas: se lee hasta que dos lecturas seguidas coinciden (la plataforma recalcula de forma asíncrona).
        var affected = items.Select(a => a.Task.TaskId).Distinct().ToList();
        var stableDeadline = DateTime.UtcNow.AddSeconds(settings.TaskReadbackTimeoutSeconds);
        Dictionary<Guid, TaskSnapshot>? previous = null, taskAfter;
        while (true)
        {
            taskAfter = (await tasks.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
            if (previous is not null && affected.All(id => Same(previous.GetValueOrDefault(id), taskAfter.GetValueOrDefault(id)))) break;
            if (DateTime.UtcNow >= stableDeadline) break;
            previous = taskAfter;
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        assignAfter = await ReadAssignmentsAsync(client, projectId, ct).ConfigureAwait(false);

        foreach (var a in items)
        {
            o.WriteLine();
            o.WriteLine("[Assignment]");
            Line(o, "Assignment UniqueID", a.Source.UniqueId.ToString());
            Line(o, "Task", $"{a.Task.Source.UniqueId} '{a.Task.Source.Name}'");
            Line(o, "Resource", a.Resource.Source.Name ?? "-");
            Line(o, "MPP", $"Work {H(a.Source.WorkHours)} | Actual {H(a.Source.ActualWorkHours)} | Remaining {H(a.Source.RemainingWorkHours)}");
            Line(o, "Before (assignment)", Effort(assignBefore.GetValueOrDefault(a.AssignmentId)));
            Line(o, "After (assignment)", Effort(assignAfter.GetValueOrDefault(a.AssignmentId)));
            Line(o, "Before (task)", Planning(taskBefore.GetValueOrDefault(a.Task.TaskId)));
            Line(o, "After (task)", Planning(taskAfter.GetValueOrDefault(a.Task.TaskId)));
            Line(o, "Task result", Same(taskBefore.GetValueOrDefault(a.Task.TaskId), taskAfter.GetValueOrDefault(a.Task.TaskId)) ? "UNCHANGED" : "CHANGED");
            Line(o, "Assignment result", Matches(a, assignAfter.GetValueOrDefault(a.AssignmentId)) ? "MATCH (values taken)" : "DIFFERENCE (values not as sent)");
        }
        return 0;
    }

    /// <summary>
    /// Variante a nivel de TAREA (la API no admite actualizar msdyn_resourceassignment): envía a cada tarea la suma del trabajo de sus asignaciones
    /// (msdyn_effort, msdyn_effortcompleted, msdyn_effortremaining) con msdyn_PssUpdateV2 y muestra antes y después la tarea.
    /// </summary>
    public static async Task<int> RunTaskLevelAsync(MppProject project, CalendarImportSettings settings, DataverseClient client, bool execute, TextWriter o, CancellationToken ct,
        ISet<string>? fields = null, decimal progressFactor = 1m)
    {
        fields ??= new HashSet<string> { "effort", "effortcompleted", "effortremaining" };
        var mppGuid = project.ProjectGuid ?? throw new InvalidOperationException("El JSON no tiene ProjectProperties.GUID.");
        var projectId = await new DataverseProjectOperationsProjectService(client, settings).FindProjectBySourceGuidAsync(mppGuid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No hay ningún proyecto con axz_mppguid = {mppGuid:D} en Dataverse.");
        var taskPlan = TaskImportPlanner.Build(project, settings.DurationRules, new TaskSelection(settings.TaskMaxCount, settings.TaskUniqueIds), projectId);
        var plan = ResourceImportPlanner.Build(project, taskPlan, projectId, settings);
        var byTask = plan.Assignments.GroupBy(a => a.Task.TaskId).ToList();
        if (byTask.Count == 0) { o.WriteLine("No hay asignaciones utilizables para las tareas indicadas (--task-ids)."); return 1; }

        static decimal? Sum(IEnumerable<decimal?> values) => values.Any(v => v is null) ? null : values.Sum(v => v!.Value);
        var targets = byTask.Select(g => (Task: g.First().Task,
            Work: fields.Contains("effort") ? Sum(g.Select(a => a.Source.WorkHours)) : null,
            Actual: fields.Contains("effortcompleted") ? Sum(g.Select(a => a.Source.ActualWorkHours)) : null,
            Remaining: fields.Contains("effortremaining") ? Sum(g.Select(a => a.Source.RemainingWorkHours)) : null,
            Progress: fields.Contains("progress") ? g.First().Task.Source.PercentComplete * progressFactor : null,
            // Lo que Dataverse devuelve al leer: la API de planificación recibe una fracción (0-1) pero la columna guarda un porcentaje (0-100).
            ProgressStored: fields.Contains("progress") ? g.First().Task.Source.PercentComplete : null)).ToList();
        var before = await ReadTaskEffortAsync(client, projectId, ct).ConfigureAwait(false);

        var entities = targets.Select(t =>
        {
            var e = new JsonObject { ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask", ["msdyn_projecttaskid"] = t.Task.TaskId.ToString("D") };
            if (t.Work is { } w) e["msdyn_effort"] = w;
            if (t.Actual is { } a) e["msdyn_effortcompleted"] = a;
            if (t.Remaining is { } r) e["msdyn_effortremaining"] = r;
            if (t.Progress is { } pr) e["msdyn_progress"] = pr;
            return e;
        }).ToList();
        o.WriteLine($"[Task work test] proyecto {projectId:D}; {entities.Count} tarea(s); modo {(execute ? "EXECUTE" : "DRY-RUN")}");
        o.WriteLine("Payload de msdyn_PssUpdateV2 (EntityCollection):");
        o.WriteLine(new JsonArray(entities.Select(e => (JsonNode?)e.DeepClone()).ToArray()).ToJsonString(JsonDefaults.Options));
        if (!execute) { o.WriteLine("Dry-run: no se ha escrito nada. Repite con --execute true."); return 0; }

        var operationSet = await client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Prueba MPP: trabajo de {entities.Count} tareas"
        }, ct).ConfigureAwait(false);
        var operationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        await client.PostActionAsync("msdyn_PssUpdateV2", new JsonObject { ["EntityCollection"] = new JsonArray(entities.Select(e => (JsonNode?)e.DeepClone()).ToArray()), ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);
        await client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);

        bool Taken(Dictionary<Guid, JsonNode> rows) => targets.All(t => rows.TryGetValue(t.Task.TaskId, out var row)
            && (t.Work is null || Close(t.Work, Dec(row["msdyn_effort"]))) && (t.Actual is null || Close(t.Actual, Dec(row["msdyn_effortcompleted"]))) && (t.Remaining is null || Close(t.Remaining, Dec(row["msdyn_effortremaining"])))
            && (t.ProgressStored is null || Close(t.ProgressStored, Dec(row["msdyn_progress"]))));
        var deadline = DateTime.UtcNow.AddSeconds(settings.OperationTimeoutSeconds);
        Dictionary<Guid, JsonNode> after;
        while (true)
        {
            after = await ReadTaskEffortAsync(client, projectId, ct).ConfigureAwait(false);
            if (Taken(after)) break;
            var set = await client.GetAsync($"msdyn_operationsets({operationSetId:D})?$select=msdyn_status,_msdyn_psserrorlog_value", ct).ConfigureAwait(false);
            if (Guid.TryParse(set["_msdyn_psserrorlog_value"]?.GetValue<string>(), out var logId))
            {
                var log = await client.GetAsync($"msdyn_psserrorlogs({logId:D})?$select=msdyn_errorcode,msdyn_log", ct).ConfigureAwait(false);
                o.WriteLine($"El servicio de planificación rechazó la actualización (OperationSet {operationSetId:D}): {log["msdyn_errorcode"]}: {log["msdyn_log"]}");
                return 2;
            }
            if (DateTime.UtcNow >= deadline) { o.WriteLine($"Las tareas no tomaron los valores enviados en {settings.OperationTimeoutSeconds} s (OperationSet {operationSetId:D}, estado {set["msdyn_status"]}); se muestra lo que hay."); break; }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        // Estabilización: dos lecturas seguidas iguales.
        var stableDeadline = DateTime.UtcNow.AddSeconds(settings.TaskReadbackTimeoutSeconds);
        while (DateTime.UtcNow < stableDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var again = await ReadTaskEffortAsync(client, projectId, ct).ConfigureAwait(false);
            var same = targets.All(t => TaskRow(after, t.Task.TaskId) == TaskRow(again, t.Task.TaskId));
            after = again;
            if (same) break;
        }

        foreach (var t in targets)
        {
            o.WriteLine();
            o.WriteLine("[Task]");
            Line(o, "Task", $"{t.Task.Source.UniqueId} '{t.Task.Source.Name}'");
            Line(o, "MPP", $"Work {H(t.Work)} | Actual {H(t.Actual)} | Remaining {H(t.Remaining)} | Progress {t.Progress?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"} % | Duration {t.Task.Source.DurationValue} {t.Task.Source.DurationUnits}");
            Line(o, "Before", TaskRow(before, t.Task.TaskId));
            Line(o, "After", TaskRow(after, t.Task.TaskId));
            var row = after.GetValueOrDefault(t.Task.TaskId);
            var ok = row is not null && (t.Work is null || Close(t.Work, Dec(row["msdyn_effort"]))) && (t.Actual is null || Close(t.Actual, Dec(row["msdyn_effortcompleted"]))) && (t.Remaining is null || Close(t.Remaining, Dec(row["msdyn_effortremaining"])))
                     && (t.ProgressStored is null || Close(t.ProgressStored, Dec(row["msdyn_progress"])));
            Line(o, "Result", ok ? "MATCH (effort values taken)" : "DIFFERENCE (values not as sent)");
        }
        return 0;
    }

    private static bool Close(decimal? expected, decimal? actual) => expected is not null && actual is not null && Math.Abs(expected.Value - actual.Value) < 0.011m;

    private static async Task<Dictionary<Guid, JsonNode>> ReadTaskEffortAsync(DataverseClient client, Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await client.GetAsync($"msdyn_projecttasks?$select=msdyn_projecttaskid,msdyn_subject,msdyn_effort,msdyn_effortcompleted,msdyn_effortremaining,msdyn_progress,msdyn_duration,msdyn_scheduledstart,msdyn_scheduledend&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var map = new Dictionary<Guid, JsonNode>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (item is not null && Guid.TryParse(item["msdyn_projecttaskid"]?.GetValue<string>(), out var id)) map[id] = item;
        return map;
    }

    private static string TaskRow(Dictionary<Guid, JsonNode> rows, Guid id)
        => rows.TryGetValue(id, out var r)
            ? $"effort {H(Dec(r["msdyn_effort"]))} | completed {H(Dec(r["msdyn_effortcompleted"]))} | remaining {H(Dec(r["msdyn_effortremaining"]))} | progress {Dec(r["msdyn_progress"])?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"} | duration {Dec(r["msdyn_duration"])?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-"} d | {r["msdyn_scheduledstart"]} -> {r["msdyn_scheduledend"]}"
            : "-";

    private static async Task<Dictionary<Guid, JsonNode>> ReadAssignmentsAsync(DataverseClient client, Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_projectid_value eq {projectId:D}");
        var response = await client.GetAsync($"msdyn_resourceassignments?$select=msdyn_resourceassignmentid,msdyn_effort,msdyn_effortcompleted,msdyn_effortremaining,msdyn_start,msdyn_finish&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var map = new Dictionary<Guid, JsonNode>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (item is not null && Guid.TryParse(item["msdyn_resourceassignmentid"]?.GetValue<string>(), out var id)) map[id] = item;
        return map;
    }

    private static decimal? Dec(JsonNode? n)
        => n is JsonValue v ? (v.TryGetValue<decimal>(out var d) ? d : v.TryGetValue<double>(out var x) ? (decimal)x : null) : null;

    private static bool Matches(PlannedAssignment a, JsonNode? row)
    {
        if (row is null) return false;
        static bool Eq(decimal? expected, decimal? actual) => expected is null || (actual is not null && Math.Abs(expected.Value - actual.Value) < 0.011m);
        return Eq(a.Source.WorkHours, Dec(row["msdyn_effort"])) && Eq(a.Source.ActualWorkHours, Dec(row["msdyn_effortcompleted"])) && Eq(a.Source.RemainingWorkHours, Dec(row["msdyn_effortremaining"]));
    }

    private static bool Same(TaskSnapshot? a, TaskSnapshot? b)
        => a is not null && b is not null && a.Start == b.Start && a.End == b.End && a.DurationDays == b.DurationDays && a.Effort == b.Effort;

    private static string Effort(JsonNode? row)
        => row is null ? "-" : $"effort {H(Dec(row["msdyn_effort"]))} | completed {H(Dec(row["msdyn_effortcompleted"]))} | remaining {H(Dec(row["msdyn_effortremaining"]))} | {row["msdyn_start"]} -> {row["msdyn_finish"]}";

    private static string Planning(TaskSnapshot? s)
        => s is null ? "-" : $"Start {Fmt(s.Start)} | Finish {Fmt(s.End)} | Duration {s.DurationDays?.ToString(CultureInfo.InvariantCulture) ?? "-"} d | Effort {H(s.Effort)}";

    private static string H(decimal? v) => v is null ? "-" : v.Value.ToString("0.##", CultureInfo.InvariantCulture) + " h";
    private static string Fmt(DateTime? v) => v is { } d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (d.Kind == DateTimeKind.Utc ? "Z" : string.Empty) : "-";
    private static void Line(TextWriter o, string label, string value) => o.WriteLine($"      {label,-22}: {value}");
}
