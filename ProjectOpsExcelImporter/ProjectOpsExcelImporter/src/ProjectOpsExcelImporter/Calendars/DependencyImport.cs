using System.Globalization;
using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter.Calendars;

// Fase de dependencias del import: SOLO las relaciones entre tareas ya importadas (tipo y retardo).
// No se importan recursos, asignaciones, costes ni porcentaje completado. Las fechas de las tareas las recalcula Project Operations
// con las dependencias; aquí se observa y se compara con el MPP, sin corregir nada.

public sealed record PlannedDependency(MppDependency Source, Guid DependencyId, PlannedTask Predecessor, PlannedTask Successor, string LinkTypeKey, int LinkTypeValue, long LagSeconds);

public sealed record SkippedDependency(MppDependency Source, string Reason);

public sealed record DependencyPlan(IReadOnlyList<PlannedDependency> Dependencies, IReadOnlyList<SkippedDependency> Skipped);

public static class DependencyImportPlanner
{
    public const string Stage = "Dependencies";

    /// <summary>GUID determinista, con la misma fórmula y espacio de nombres que el importador existente (proyecto:dep:pred:succ:tipo:retardo).</summary>
    public static Guid DependencyId(Guid projectId, int predecessorUniqueId, int successorUniqueId, string linkTypeKey, long lagSeconds)
        => DeterministicGuid.Create(ProjectOpsImporter.IdNamespace, $"{projectId:D}:dep:{predecessorUniqueId}:{successorUniqueId}:{linkTypeKey}:{lagSeconds}");

    public static string LinkTypeKey(string mppRelationType) => mppRelationType switch
    {
        "FinishStart" => "FinishToStart",
        "StartStart" => "StartToStart",
        "FinishFinish" => "FinishToFinish",
        "StartFinish" => "StartToFinish",
        var other => throw Fail($"Tipo de relación '{other}' no soportado.")
    };

    /// <summary>
    /// Retardo en segundos (positivo = retardo, negativo = adelanto). Los días del MPP son días de trabajo de HoursPerDay horas;
    /// semanas y meses se convierten con DaysPerWeek/DaysPerMonth, como en la fase de tareas. Es un supuesto que se contrasta con las fechas que calcula Project Operations.
    /// </summary>
    public static long LagSeconds(MppDependency d, TaskDurationRules rules)
    {
        var secondsPerDay = rules.HoursPerDay * 3600m;
        var seconds = d.LagUnits.ToLowerInvariant() switch
        {
            "days" => d.LagValue * secondsPerDay,
            "hours" => d.LagValue * 3600m,
            "minutes" => d.LagValue * 60m,
            "weeks" => d.LagValue * rules.DaysPerWeek * secondsPerDay,
            "months" => d.LagValue * rules.DaysPerMonth * secondsPerDay,
            var other => throw Fail($"Unidad de retardo '{other}' no soportada en la relación {d.RelationUniqueId}.")
        };
        var rounded = (long)Math.Round(seconds, 0, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) > int.MaxValue)
            throw Fail($"El retardo de la relación {d.RelationUniqueId} ({d.LagValue} {d.LagUnits}) excede el rango del campo (entero de 32 bits en segundos).");
        return rounded;
    }

    public static DependencyPlan Build(MppProject project, TaskPlan taskPlan, TaskDurationRules rules, CalendarImportSettings settings, Guid projectId)
    {
        var byUid = taskPlan.Tasks.ToDictionary(t => t.Source.UniqueId);
        var planned = new List<PlannedDependency>();
        var skipped = new List<SkippedDependency>();
        var seen = new HashSet<Guid>();

        foreach (var d in project.Dependencies.OrderBy(x => x.RelationUniqueId))
        {
            if (!byUid.TryGetValue(d.PredecessorUniqueId, out var predecessor) || !byUid.TryGetValue(d.SuccessorUniqueId, out var successor))
            {
                skipped.Add(new SkippedDependency(d, "una de las tareas no está seleccionada o no es importable"));
                continue;
            }
            if (!settings.IncludeSummaryDependencies && (predecessor.Source.IsSummary || successor.Source.IsSummary))
            {
                skipped.Add(new SkippedDependency(d, "enlaza con una tarea resumen"));
                continue;
            }
            var key = LinkTypeKey(d.RelationType);
            if (!settings.DependencyLinkTypeValues.TryGetValue(key, out var value)) throw Fail($"Falta DependencyLinkTypeValues.{key} en la configuración.");
            var lag = LagSeconds(d, rules);
            var id = DependencyId(projectId, d.PredecessorUniqueId, d.SuccessorUniqueId, key, lag);
            if (!seen.Add(id))
            {
                skipped.Add(new SkippedDependency(d, "relación duplicada (misma predecesora, sucesora, tipo y retardo)"));
                continue;
            }
            planned.Add(new PlannedDependency(d, id, predecessor, successor, key, value, lag));
        }
        if (settings.DependencyMaxCount is { } max) planned = planned.Take(max).ToList();
        return new DependencyPlan(planned, skipped);
    }

    private static CalendarImportException Fail(string message) => new(ImportExitCodes.Dependencies, Stage, message);
}

/// <summary>Atributos que se envían a la Project Scheduling API para cada dependencia (nombres de enlace como el importador existente).</summary>
public static class DependencyEntityBuilder
{
    public static JsonObject Build(PlannedDependency d, Guid projectId, CalendarImportSettings settings)
    {
        var entity = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttaskdependency",
            ["msdyn_projecttaskdependencyid"] = d.DependencyId.ToString("D"),
            ["msdyn_Project@odata.bind"] = $"/msdyn_projects({projectId:D})",
            ["msdyn_PredecessorTask@odata.bind"] = $"/msdyn_projecttasks({d.Predecessor.TaskId:D})",
            ["msdyn_SuccessorTask@odata.bind"] = $"/msdyn_projecttasks({d.Successor.TaskId:D})",
            [settings.DependencyLinkTypeAttribute] = d.LinkTypeValue
        };
        if (d.LagSeconds != 0) entity[settings.DependencyLagAttribute] = d.LagSeconds;
        return entity;
    }
}

public sealed record DependencySnapshot(Guid Id, Guid? PredecessorTaskId, Guid? SuccessorTaskId, int? LinkType, long? LagSeconds);

public interface IProjectDependencyService
{
    Task<IReadOnlySet<Guid>> GetExistingDependencyIdsAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Crea las dependencias con la Project Scheduling API (OperationSet + PssCreate + ExecuteOperationSet).</summary>
    Task CreateDependenciesAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken cancellationToken);
    /// <summary>Espera a que exista la dependencia; si el servicio de planificación informa de un error, lo lanza enseguida.</summary>
    Task WaitForDependencyAsync(Guid dependencyId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DependencySnapshot>> ReadDependenciesAsync(Guid projectId, CancellationToken cancellationToken);
}

public sealed record DependencyImportSummary(int Selected, int Skipped, int Created, int AlreadyExisting, int Match, int Difference,
    int TasksMoved, int TasksMatchBefore, int TasksMatchAfter, int TasksTotal);

/// <summary>Crea las dependencias (idempotente), las lee de vuelta, y observa cómo reprograma Project Operations las tareas.</summary>
public sealed class ProjectDependencyImporter
{
    private const int MaxDetailedMatchBlocks = 60;
    private readonly IProjectDependencyService _dependencies;
    private readonly IProjectTaskService _tasks;
    private readonly CalendarImportSettings _settings;
    private readonly TextWriter _out;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ProjectDependencyImporter(IProjectDependencyService dependencies, IProjectTaskService tasks, CalendarImportSettings settings,
        TextWriter output, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _dependencies = dependencies;
        _tasks = tasks;
        _settings = settings;
        _out = output;
        _delay = delay ?? Task.Delay;
    }

    public async Task<DependencyImportSummary> RunAsync(Guid projectId, MppProject project, ImportState state, Action save, CancellationToken ct)
    {
        var taskPlan = TaskImportPlanner.Build(project, _settings.DurationRules, new TaskSelection(_settings.TaskMaxCount, _settings.TaskUniqueIds), projectId);
        var plan = DependencyImportPlanner.Build(project, taskPlan, _settings.DurationRules, _settings, projectId);
        var skipped = plan.Skipped.ToList();

        // Las dos tareas de cada dependencia tienen que existir ya en Dataverse.
        var before = (await _tasks.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var usable = new List<PlannedDependency>();
        foreach (var d in plan.Dependencies)
        {
            if (before.ContainsKey(d.Predecessor.TaskId) && before.ContainsKey(d.Successor.TaskId)) usable.Add(d);
            else skipped.Add(new SkippedDependency(d.Source, "una de las tareas no existe todavía en Dataverse"));
        }

        Line("Selected", $"{usable.Count} dependencies to apply; {skipped.Count} skipped (of {project.Dependencies.Count} in the JSON)");
        foreach (var group in skipped.GroupBy(s => s.Reason))
            Line("Skipped", $"{group.Count()}: {group.Key}" + (group.Count() <= 3 ? " (" + string.Join(", ", group.Select(g => $"{g.Source.PredecessorUniqueId}->{g.Source.SuccessorUniqueId}")) + ")" : string.Empty));

        var existing = await _dependencies.GetExistingDependencyIdsAsync(projectId, ct).ConfigureAwait(false);
        foreach (var d in usable.Where(d => existing.Contains(d.DependencyId))) state.CreatedDependencies.Add(d.DependencyId);
        var pending = usable.Where(d => !existing.Contains(d.DependencyId)).ToList();
        Line("Existing", $"{usable.Count - pending.Count} already in Dataverse (not recreated)");

        var created = 0;
        foreach (var batch in pending.Chunk(_settings.TaskBatchSize))
        {
            await _dependencies.CreateDependenciesAsync(projectId, batch.Select(d => DependencyEntityBuilder.Build(d, projectId, _settings)).ToList(), ct).ConfigureAwait(false);
            await _dependencies.WaitForDependencyAsync(batch[^1].DependencyId, ct).ConfigureAwait(false);
            foreach (var d in batch) state.CreatedDependencies.Add(d.DependencyId);
            created += batch.Length;
            save();
            Line("Created", $"{created}/{pending.Count}");
        }

        var (snapshots, complete) = await ReadBackDependenciesAsync(projectId, usable, ct).ConfigureAwait(false);
        var (match, difference) = CompareDependencies(usable, snapshots);

        // Efecto de las dependencias en las fechas: Project Operations reprograma las tareas sucesoras.
        var after = await ReadStableTasksAsync(projectId, taskPlan, ct).ConfigureAwait(false);
        var report = ReportTaskMovement(taskPlan, before, after);

        _out.WriteLine();
        _out.WriteLine("[Dependencies summary]");
        Line("Dependencies", $"{usable.Count} (MATCH {match}, DIFFERENCE {difference}); skipped {skipped.Count}");
        Line("Tasks moved", $"{report.Moved} of {taskPlan.Tasks.Count} changed their dates after the dependencies were created");
        Line("Tasks vs MPP", $"MATCH before {report.MatchBefore} -> after {report.MatchAfter} of {taskPlan.Tasks.Count}");
        if (!complete) Line("WARNING", "Not every dependency was readable before the read-back timed out.");
        Line("Note", "Differences are observations of what Project Operations calculated (times are UTC as returned); nothing is corrected automatically.");
        return new DependencyImportSummary(usable.Count, skipped.Count, created, usable.Count - pending.Count, match, difference,
            report.Moved, report.MatchBefore, report.MatchAfter, taskPlan.Tasks.Count);
    }

    private async Task<(Dictionary<Guid, DependencySnapshot> Snapshots, bool Complete)> ReadBackDependenciesAsync(Guid projectId, List<PlannedDependency> planned, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_settings.TaskReadbackTimeoutSeconds);
        while (true)
        {
            var all = (await _dependencies.ReadDependenciesAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(s => s.Id);
            var ready = planned.All(p => all.ContainsKey(p.DependencyId));
            if (ready || DateTime.UtcNow >= deadline) return (all, ready);
            await _delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private (int Match, int Difference) CompareDependencies(List<PlannedDependency> planned, Dictionary<Guid, DependencySnapshot> snapshots)
    {
        int match = 0, difference = 0, printedMatches = 0;
        foreach (var d in planned)
        {
            snapshots.TryGetValue(d.DependencyId, out var po);
            var reasons = new List<string>();
            if (po is null) reasons.Add("not found in Dataverse");
            else
            {
                if (po.PredecessorTaskId != d.Predecessor.TaskId) reasons.Add("predecessor");
                if (po.SuccessorTaskId != d.Successor.TaskId) reasons.Add("successor");
                if (po.LinkType != d.LinkTypeValue) reasons.Add("type");
                if ((po.LagSeconds ?? 0) != d.LagSeconds) reasons.Add("lag");
            }
            var isMatch = reasons.Count == 0;
            if (isMatch) match++; else difference++;
            if (isMatch && ++printedMatches > MaxDetailedMatchBlocks) continue;       // con muchas dependencias solo se detallan las diferencias

            _out.WriteLine();
            _out.WriteLine("[Dependency]");
            Line("MPP RelationUniqueID", d.Source.RelationUniqueId.ToString());
            Line("Predecessor", $"{d.Predecessor.Source.UniqueId} ({d.Predecessor.Source.Name})");
            Line("Successor", $"{d.Successor.Source.UniqueId} ({d.Successor.Source.Name})");
            Line("MPP Type", $"{d.Source.RelationType} -> {d.LinkTypeKey} = {d.LinkTypeValue}");
            Line("MPP Lag", $"{d.Source.LagValue.ToString(CultureInfo.InvariantCulture)} {d.Source.LagUnits} -> {d.LagSeconds} s sent");
            Line("Project Operations ID", d.DependencyId.ToString("D"));
            Line("PO Type", po?.LinkType?.ToString(CultureInfo.InvariantCulture) ?? "-");
            Line("PO Lag", po?.LagSeconds is { } lag ? $"{lag} s" : (po is null ? "-" : "0 s"));
            Line("Result", isMatch ? "MATCH" : $"DIFFERENCE ({string.Join(", ", reasons)})");
        }
        if (printedMatches > MaxDetailedMatchBlocks) Line("Note", $"{printedMatches - MaxDetailedMatchBlocks} more MATCH dependencies not detailed (only differences are listed beyond {MaxDetailedMatchBlocks}).");
        return (match, difference);
    }

    /// <summary>Project Operations recalcula las fechas de forma asíncrona: se lee hasta que dos lecturas seguidas coinciden.</summary>
    private async Task<Dictionary<Guid, TaskSnapshot>> ReadStableTasksAsync(Guid projectId, TaskPlan plan, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_settings.TaskReadbackTimeoutSeconds);
        Dictionary<Guid, TaskSnapshot>? previous = null;
        while (true)
        {
            var current = (await _tasks.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
            if (previous is not null && plan.Tasks.All(t => Same(previous, current, t.TaskId))) return current;
            if (DateTime.UtcNow >= deadline) return current;
            previous = current;
            await _delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private static bool Same(Dictionary<Guid, TaskSnapshot> a, Dictionary<Guid, TaskSnapshot> b, Guid id)
        => a.TryGetValue(id, out var x) && b.TryGetValue(id, out var y) && x.Start == y.Start && x.End == y.End;

    private (int Moved, int MatchBefore, int MatchAfter) ReportTaskMovement(TaskPlan plan, Dictionary<Guid, TaskSnapshot> before, Dictionary<Guid, TaskSnapshot> after)
    {
        int moved = 0, matchBefore = 0, matchAfter = 0;
        _out.WriteLine();
        _out.WriteLine("[Tasks moved by the dependencies]");
        foreach (var task in plan.Tasks)
        {
            before.TryGetValue(task.TaskId, out var b);
            after.TryGetValue(task.TaskId, out var a);
            var s = task.Source;
            var matchedBefore = DatesMatch(s, b);
            var matchedAfter = DatesMatch(s, a);
            if (matchedBefore) matchBefore++;
            if (matchedAfter) matchAfter++;
            if (b is null || a is null || (b.Start == a.Start && b.End == a.End)) continue;
            moved++;
            _out.WriteLine();
            _out.WriteLine("[Task]");
            Line("MPP UniqueID", s.UniqueId.ToString());
            Line("Name", s.Name);
            Line("MPP Start / Finish", $"{Fmt(s.Start)} / {Fmt(s.Finish)}");
            Line("PO before", $"{Fmt(b.Start)} / {Fmt(b.End)}");
            Line("PO after", $"{Fmt(a.Start)} / {Fmt(a.End)}");
            Line("Result vs MPP", matchedAfter ? (matchedBefore ? "MATCH (unchanged result)" : "MATCH (now matches the MPP)") : (matchedBefore ? "DIFFERENCE (it matched before)" : "DIFFERENCE"));
        }
        if (moved == 0) Line("Result", "no task changed its dates");
        return (moved, matchBefore, matchAfter);
    }

    private static bool DatesMatch(MppTask task, TaskSnapshot? po)
        => po is not null && po.Start is { } ps && po.End is { } pe
           && (task.Start is not { } ms || DateOnly.FromDateTime(ms) == DateOnly.FromDateTime(ps))
           && (task.Finish is not { } mf || DateOnly.FromDateTime(mf) == DateOnly.FromDateTime(pe));

    private static string Fmt(DateTime? value) => value is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (v.Kind == DateTimeKind.Utc ? "Z" : string.Empty) : "-";
    private void Line(string label, string value) => _out.WriteLine($"      {label,-22}: {value}");
}

/// <summary>Espera a un registro creado por un OperationSet, cortando enseguida si el servicio de planificación (PSS) informa de un error.</summary>
internal static class PssOperationSets
{
    public static async Task WaitForRecordOrFailureAsync(DataverseClient client, Guid operationSetId, string entitySet, Guid recordId, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            if (await client.RecordExistsAsync(entitySet, recordId, ct).ConfigureAwait(false)) return;
            var operationSet = await client.GetAsync($"msdyn_operationsets({operationSetId:D})?$select=msdyn_status,_msdyn_psserrorlog_value", ct).ConfigureAwait(false);
            if (Guid.TryParse(operationSet["_msdyn_psserrorlog_value"]?.GetValue<string>(), out var logId))
            {
                var log = await client.GetAsync($"msdyn_psserrorlogs({logId:D})?$select=msdyn_errorcode,msdyn_log", ct).ConfigureAwait(false);
                throw new InvalidOperationException($"El servicio de planificación rechazó el lote (OperationSet {operationSetId:D}): {log["msdyn_errorcode"]}: {log["msdyn_log"]}");
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"El registro {entitySet}({recordId:D}) no apareció en {timeoutSeconds} s y el OperationSet {operationSetId:D} no informa de ningún error (estado {operationSet["msdyn_status"]}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Lectura/escritura de dependencias en Dataverse con los nombres del importador existente (OperationSet + msdyn_PssCreateV2).</summary>
public sealed class DataverseProjectDependencyService : IProjectDependencyService
{
    private readonly DataverseClient _client;
    private readonly CalendarImportSettings _settings;
    private Guid _lastOperationSetId;

    public DataverseProjectDependencyService(DataverseClient client, CalendarImportSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<IReadOnlySet<Guid>> GetExistingDependencyIdsAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync($"msdyn_projecttaskdependencies?$select=msdyn_projecttaskdependencyid&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var set = new HashSet<Guid>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (Guid.TryParse(item?["msdyn_projecttaskdependencyid"]?.GetValue<string>(), out var id)) set.Add(id);
        return set;
    }

    public async Task CreateDependenciesAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken ct)
    {
        var operationSet = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Importación MPP: crear {entities.Count} dependencias"
        }, ct).ConfigureAwait(false);
        _lastOperationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");

        var array = new JsonArray();
        foreach (var entity in entities) array.Add(entity);
        await _client.PostActionAsync("msdyn_PssCreateV2", new JsonObject
        {
            ["EntityCollection"] = array,
            ["OperationSetId"] = _lastOperationSetId.ToString("D")
        }, ct).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = _lastOperationSetId.ToString("D") }, ct).ConfigureAwait(false);
    }

    public Task WaitForDependencyAsync(Guid dependencyId, CancellationToken ct)
        => PssOperationSets.WaitForRecordOrFailureAsync(_client, _lastOperationSetId, "msdyn_projecttaskdependencies", dependencyId, _settings.OperationTimeoutSeconds, ct);

    public async Task<IReadOnlyList<DependencySnapshot>> ReadDependenciesAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync(
            $"msdyn_projecttaskdependencies?$select=msdyn_projecttaskdependencyid,_msdyn_predecessortask_value,_msdyn_successortask_value,{_settings.DependencyLinkTypeAttribute},{_settings.DependencyLagAttribute}&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var list = new List<DependencySnapshot>();
        foreach (var item in response["value"] as JsonArray ?? [])
        {
            if (!Guid.TryParse(item?["msdyn_projecttaskdependencyid"]?.GetValue<string>(), out var id)) continue;
            list.Add(new DependencySnapshot(id,
                Guid.TryParse(item!["_msdyn_predecessortask_value"]?.GetValue<string>(), out var pred) ? pred : null,
                Guid.TryParse(item["_msdyn_successortask_value"]?.GetValue<string>(), out var succ) ? succ : null,
                item[_settings.DependencyLinkTypeAttribute] is JsonValue t && t.TryGetValue<int>(out var linkType) ? linkType : null,
                item[_settings.DependencyLagAttribute] is JsonValue l && l.TryGetValue<long>(out var lag) ? lag : null));
        }
        return list;
    }
}
