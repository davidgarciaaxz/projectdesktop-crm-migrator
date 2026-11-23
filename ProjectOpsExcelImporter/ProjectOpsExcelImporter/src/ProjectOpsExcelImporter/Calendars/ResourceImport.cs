using System.Globalization;
using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter.Calendars;

// Fase de recursos y asignaciones. Para cada recurso Work del MPP que tiene alguna asignación se crea un MIEMBRO GENÉRICO DEL EQUIPO del proyecto:
//   recurso Work del MPP -> miembro genérico (msdyn_projectteam) -> Resource Assignment -> tarea
// Todos los miembros apuntan al recurso reservable genérico COMÚN y configurado de Project Operations (GenericBookableResourceId); el nombre del recurso
// del MPP es el "Nombre de puesto" (msdyn_name). El importador NO crea recursos reservables, roles, usuarios ni calendarios, y no importa horas, unidades,
// tarifas, costes ni progreso. Los recursos Material y las asignaciones sin recurso se omiten con aviso.

/// <summary>Rol resuelto para un recurso del MPP y cómo se resolvió; Error no nulo = no se pudo resolver.</summary>
public sealed record RoleResolution(Guid? RoleId, string Method, string? Error);

public sealed record PlannedResource(MppResource Source, string Key, Guid ProjectTeamMemberId, int AssignmentCount, Guid RoleId, string RoleMethod);

/// <summary>Recurso Work usado cuyo rol no se pudo resolver: no se crea su miembro del equipo ni sus asignaciones.</summary>
public sealed record UnresolvedResource(MppResource Source, int AssignmentCount, string Error);

public sealed record PlannedAssignment(MppResourceAssignment Source, PlannedResource Resource, PlannedTask Task, Guid AssignmentId);

/// <summary>Esfuerzo (horas) que se fijará en una tarea: suma del Work de sus asignaciones en el MPP.</summary>
public sealed record TaskWorkUpdate(PlannedTask Task, decimal EffortHours, int AssignmentCount);

/// <summary>Avance (porcentaje 0-100) que se fijará en una tarea hoja: el PercentComplete del MPP.</summary>
public sealed record TaskProgressUpdate(PlannedTask Task, decimal Percent);

public sealed record SkippedAssignment(MppResourceAssignment Source, string Reason);

public sealed record SkippedMaterial(MppResource Resource, IReadOnlyList<MppResourceAssignment> Assignments);

public sealed record ResourcePlan(
    IReadOnlyList<PlannedResource> Resources,
    IReadOnlyList<PlannedAssignment> Assignments,
    IReadOnlyList<SkippedAssignment> SkippedAssignments,
    IReadOnlyList<SkippedMaterial> SkippedMaterials,
    IReadOnlyList<UnresolvedResource> Unresolved);

/// <summary>Orden de resolución del rol: GUID del recurso, UniqueId, nombre exacto y único y, por último, DefaultResourceRoleId. Nunca crea roles.</summary>
public static class ResourceRoleResolver
{
    public const string ByGuid = "mapping by MPP Resource GUID";
    public const string ByUniqueId = "mapping by MPP Resource UniqueID";
    public const string ByName = "mapping by exact unique name";
    public const string ByDefault = "default role (DefaultResourceRoleId)";

    public static RoleResolution Resolve(MppResource resource, CalendarImportSettings settings)
    {
        var maps = settings.ResourceRoleMappings ?? [];

        if (resource.Guid is { } guid)
        {
            var hit = Distinct(maps.Where(m => m.MppResourceGuid == guid));
            if (hit.Count == 1) return new RoleResolution(hit[0], ByGuid, null);
            if (hit.Count > 1) return Ambiguous($"el GUID {guid:D}");
        }
        {
            var hit = Distinct(maps.Where(m => m.MppResourceUniqueId == resource.UniqueId));
            if (hit.Count == 1) return new RoleResolution(hit[0], ByUniqueId, null);
            if (hit.Count > 1) return Ambiguous($"el UniqueId {resource.UniqueId}");
        }
        if (!string.IsNullOrWhiteSpace(resource.Name))
        {
            var name = resource.Name.Trim();
            var hit = Distinct(maps.Where(m => !string.IsNullOrWhiteSpace(m.MppResourceName) && string.Equals(m.MppResourceName.Trim(), name, StringComparison.Ordinal)));
            if (hit.Count == 1) return new RoleResolution(hit[0], ByName, null);
            if (hit.Count > 1) return Ambiguous($"el nombre '{name}'");
        }
        if (settings.DefaultResourceRoleId is { } fallback && fallback != Guid.Empty) return new RoleResolution(fallback, ByDefault, null);
        return new RoleResolution(null, "-", "no hay mapping de rol para el recurso y no está configurado DefaultResourceRoleId");
    }

    private static List<Guid> Distinct(IEnumerable<ResourceRoleMapping> maps) => maps.Select(m => m.ProjectOperationsRoleId).Distinct().ToList();

    private static RoleResolution Ambiguous(string by) => new(null, "-", $"ResourceRoleMappings tiene roles distintos para {by}");
}

public static class ResourceImportPlanner
{
    public const string Stage = "Resources";
    public const string WorkType = "Work";
    public const string MaterialType = "Material";
    public const string NoRoleReason = "recurso sin rol resuelto";

    /// <summary>Identidad externa del recurso del MPP: su GUID y, si no lo tiene, su UniqueId.</summary>
    public static string ResourceKey(MppResource r) => r.Guid is { } g ? g.ToString("D") : $"uid:{r.UniqueId}";

    /// <summary>GUID del miembro del equipo: depende del proyecto importado y de la identidad del recurso del MPP.</summary>
    public static Guid ProjectTeamMemberId(Guid projectId, string resourceKey)
        => DeterministicGuid.Create(ProjectOpsImporter.IdNamespace, $"{projectId:D}:team:{resourceKey}");

    public static Guid AssignmentId(Guid projectId, Guid taskId, string resourceKey)
        => DeterministicGuid.Create(ProjectOpsImporter.IdNamespace, $"{projectId:D}:assignment:{taskId:D}:{resourceKey}");

    /// <summary>
    /// Esfuerzo por tarea: suma del Work (en horas) de las asignaciones planificadas de la tarea. Si alguna asignación de la tarea no tiene Work en horas,
    /// la tarea se omite (no se inventa un valor).
    /// </summary>
    public static (List<TaskWorkUpdate> Updates, List<(PlannedTask Task, string Reason)> Skipped) BuildWorkUpdates(IEnumerable<PlannedAssignment> assignments)
    {
        var updates = new List<TaskWorkUpdate>();
        var skipped = new List<(PlannedTask, string)>();
        foreach (var group in assignments.GroupBy(a => a.Task.TaskId))
        {
            var task = group.First().Task;
            if (group.Any(a => a.Source.WorkHours is null)) { skipped.Add((task, "alguna asignación no tiene Work en horas")); continue; }
            updates.Add(new TaskWorkUpdate(task, group.Sum(a => a.Source.WorkHours!.Value), group.Count()));
        }
        return (updates, skipped);
    }

    /// <summary>
    /// Avance por tarea: PercentComplete del MPP (0-100) de las tareas hoja con avance mayor que 0. Los hitos (duración 0) se omiten porque no se han
    /// probado; las tareas resumen las calcula Project Operations.
    /// </summary>
    public static (List<TaskProgressUpdate> Updates, List<(PlannedTask Task, string Reason)> Skipped) BuildProgressUpdates(IEnumerable<PlannedTask> tasks)
    {
        var updates = new List<TaskProgressUpdate>();
        var skipped = new List<(PlannedTask, string)>();
        foreach (var task in tasks.Where(t => !t.Source.IsSummary && t.Source.PercentComplete is > 0))
        {
            if (task.DurationDays == 0) { skipped.Add((task, "hito (el avance de hitos no está probado)")); continue; }
            updates.Add(new TaskProgressUpdate(task, Math.Min(100m, task.Source.PercentComplete!.Value)));
        }
        return (updates, skipped);
    }

    public static ResourcePlan Build(MppProject project, TaskPlan taskPlan, Guid projectId, CalendarImportSettings settings)
    {
        var tasks = taskPlan.Tasks.ToDictionary(t => t.Source.UniqueId);
        var resourcesByUid = project.Resources.GroupBy(r => r.UniqueId).ToDictionary(g => g.Key, g => g.First());
        var usable = new List<(MppResourceAssignment Assignment, MppResource Resource, PlannedTask Task)>();
        var skipped = new List<SkippedAssignment>();
        var materials = new Dictionary<int, List<MppResourceAssignment>>();

        foreach (var a in project.ResourceAssignments.OrderBy(x => x.UniqueId))
        {
            // 1) La tarea tiene que estar seleccionada y ser importable (y no ser un resumen: las calcula Project Operations).
            if (a.TaskMissing || a.TaskUniqueId is not { } taskUid || !tasks.TryGetValue(taskUid, out var task))
            {
                skipped.Add(new SkippedAssignment(a, "la tarea no está seleccionada o no es importable"));
                continue;
            }
            if (task.Source.IsSummary)
            {
                skipped.Add(new SkippedAssignment(a, "asignación a una tarea resumen"));
                continue;
            }
            // 2) Asignación sin recurso: se omite, sin crear ningún miembro ficticio.
            if (a.ResourceMissing || a.ResourceUniqueId is not { } resourceUid || !resourcesByUid.TryGetValue(resourceUid, out var resource)
                || resource.IsNull || (resource.UniqueId == 0 && string.IsNullOrWhiteSpace(resource.Name)))
            {
                skipped.Add(new SkippedAssignment(a, "asignación sin recurso"));
                continue;
            }
            // 3) Material: no se importa todavía.
            if (resource.Type.Equals(MaterialType, StringComparison.OrdinalIgnoreCase))
            {
                if (!materials.TryGetValue(resource.UniqueId, out var list)) materials[resource.UniqueId] = list = [];
                list.Add(a);
                skipped.Add(new SkippedAssignment(a, "recurso de tipo Material (no se importa todavía)"));
                continue;
            }
            if (!resource.Type.Equals(WorkType, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new SkippedAssignment(a, $"tipo de recurso '{resource.Type}' no soportado"));
                continue;
            }
            usable.Add((a, resource, task));
        }

        // Solo se crean miembros para los recursos Work con al menos una asignación utilizable, y solo si su rol se resuelve.
        var planned = new Dictionary<int, PlannedResource>();
        var unresolved = new List<UnresolvedResource>();
        var unresolvedUids = new HashSet<int>();
        foreach (var group in usable.GroupBy(u => u.Resource.UniqueId).OrderBy(g => g.First().Resource.Id))
        {
            var resource = group.First().Resource;
            var role = ResourceRoleResolver.Resolve(resource, settings);
            if (role.RoleId is not { } roleId)
            {
                unresolved.Add(new UnresolvedResource(resource, group.Count(), role.Error ?? "rol sin resolver"));
                unresolvedUids.Add(resource.UniqueId);
                continue;
            }
            var key = ResourceKey(resource);
            planned[resource.UniqueId] = new PlannedResource(resource, key, ProjectTeamMemberId(projectId, key), group.Count(), roleId, role.Method);
        }

        var assignments = new List<PlannedAssignment>();
        var seen = new HashSet<Guid>();
        foreach (var (assignment, resource, task) in usable)
        {
            if (unresolvedUids.Contains(resource.UniqueId)) { skipped.Add(new SkippedAssignment(assignment, NoRoleReason)); continue; }
            var plannedResource = planned[resource.UniqueId];
            var id = AssignmentId(projectId, task.TaskId, plannedResource.Key);
            if (!seen.Add(id)) { skipped.Add(new SkippedAssignment(assignment, "asignación duplicada (mismo recurso y tarea)")); continue; }
            assignments.Add(new PlannedAssignment(assignment, plannedResource, task, id));
        }
        var skippedMaterials = materials.Select(m => new SkippedMaterial(resourcesByUid[m.Key], m.Value)).OrderBy(m => m.Resource.Id).ToList();
        return new ResourcePlan(planned.Values.ToList(), assignments, skipped, skippedMaterials, unresolved);
    }
}

/// <summary>Atributos que se envían a Dataverse y a la Project Scheduling API.</summary>
public static class ResourceEntityBuilder
{
    /// <summary>"Nombre de puesto" del miembro genérico (msdyn_projectteam.msdyn_name, confirmado en los metadatos): el nombre del recurso del MPP.</summary>
    public static string PositionName(MppResource r) => string.IsNullOrWhiteSpace(r.Name) ? $"Resource {r.UniqueId}" : r.Name.Trim();

    /// <summary>
    /// Miembro genérico del equipo (msdyn_CreateTeamMemberV1): recurso reservable genérico común, rol y unidad de dotación configurados,
    /// y el nombre del recurso del MPP como nombre de puesto. No se crea ningún recurso reservable.
    /// </summary>
    public static JsonObject TeamMember(PlannedResource r, Guid projectId, CalendarImportSettings settings)
    {
        var generic = settings.GenericBookableResourceId ?? throw new InvalidOperationException("CalendarImport.GenericBookableResourceId no está configurado.");
        var unit = settings.DefaultResourcingUnitId ?? throw new InvalidOperationException("CalendarImport.DefaultResourcingUnitId no está configurado.");
        return new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projectteam",
            ["msdyn_projectteamid"] = r.ProjectTeamMemberId.ToString("D"),
            ["msdyn_project@odata.bind"] = $"/msdyn_projects({projectId:D})",
            ["msdyn_name"] = PositionName(r.Source),
            [$"{settings.TeamMemberBookableResourceBind}@odata.bind"] = $"/bookableresources({generic:D})",
            [$"{settings.TeamMemberRoleBind}@odata.bind"] = $"/bookableresourcecategories({r.RoleId:D})",
            [$"{settings.TeamMemberResourcingUnitBind}@odata.bind"] = $"/msdyn_organizationalunits({unit:D})"
        };
    }

    /// <summary>Actualización de la tarea (msdyn_PssUpdateV2): solo msdyn_effort, en horas.</summary>
    public static JsonObject TaskEffort(Guid taskId, decimal hours) => new()
    {
        ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask",
        ["msdyn_projecttaskid"] = taskId.ToString("D"),
        ["msdyn_effort"] = hours
    };

    /// <summary>
    /// Actualización de la tarea (msdyn_PssUpdateV2): solo msdyn_progress. La API de planificación recibe una FRACCIÓN (1 = 100 %; verificado: 100 se rechaza),
    /// aunque la columna la devuelva como porcentaje.
    /// </summary>
    public static JsonObject TaskProgress(Guid taskId, decimal percent) => new()
    {
        ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask",
        ["msdyn_projecttaskid"] = taskId.ToString("D"),
        ["msdyn_progress"] = percent / 100m
    };

    /// <summary>Asignación: solo proyecto, tarea y miembro del equipo (más el nombre técnico). Sin horas, unidades ni costes.</summary>
    public static JsonObject Assignment(PlannedAssignment a, Guid projectId, Guid teamMemberId)
    {
        var name = $"{PositionName(a.Resource.Source)} - {a.Task.Source.Name}";
        if (name.Length > 100) name = name[..100];
        return new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_resourceassignment",
            ["msdyn_resourceassignmentid"] = a.AssignmentId.ToString("D"),
            ["msdyn_name"] = name,
            ["msdyn_projectid@odata.bind"] = $"/msdyn_projects({projectId:D})",
            ["msdyn_taskid@odata.bind"] = $"/msdyn_projecttasks({a.Task.TaskId:D})",
            ["msdyn_projectteamid@odata.bind"] = $"/msdyn_projectteams({teamMemberId:D})"
        };
    }
}

public sealed record AssignmentSnapshot(Guid Id, Guid? TaskId, Guid? TeamMemberId);

/// <summary>Miembro del equipo existente en Dataverse (para reutilizarlo y para comprobar que coincide con lo planificado).</summary>
public sealed record TeamMemberSnapshot(Guid Id, string? Name, Guid? BookableResourceId, Guid? RoleId, Guid? ResourcingUnitId);

/// <summary>Nombres de los registros configurados (ya validados como existentes y activos).</summary>
public sealed record ResourceConfigCheck(string GenericResourceName, string ResourcingUnitName, IReadOnlyDictionary<Guid, string> RoleNames);

public interface IProjectResourceService
{
    /// <summary>
    /// Valida que el recurso genérico, la unidad de dotación y los roles indicados existen y están activos. Lanza CalendarImportException (exit 11)
    /// con un mensaje claro si alguno no existe o no está activo.
    /// </summary>
    Task<ResourceConfigCheck> ValidateConfigurationAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);
    /// <summary>Miembros del equipo de ESTE proyecto.</summary>
    Task<IReadOnlyList<TeamMemberSnapshot>> GetProjectTeamAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Crea el miembro genérico del equipo y espera a que exista.</summary>
    Task CreateTeamMemberAsync(Guid projectId, PlannedResource resource, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> GetExistingAssignmentIdsAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Crea las asignaciones con la Project Scheduling API (OperationSet + PssCreate + ExecuteOperationSet).</summary>
    Task CreateAssignmentsAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken cancellationToken);
    Task WaitForAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AssignmentSnapshot>> ReadAssignmentsAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Fija msdyn_effort (horas) de las tareas con msdyn_PssUpdateV2 y espera a que las tareas lo reflejen (o a que el servicio informe de un error).</summary>
    Task UpdateTaskEffortAsync(Guid projectId, IReadOnlyList<(Guid TaskId, decimal Hours)> updates, CancellationToken cancellationToken);
    /// <summary>
    /// Fija el avance de las tareas (porcentaje 0-100) con msdyn_PssUpdateV2 y espera a que lo reflejen (o a que el servicio informe de un error).
    /// La API recibe una fracción (0-1); la columna msdyn_progress la devuelve como porcentaje.
    /// </summary>
    Task UpdateTaskProgressAsync(Guid projectId, IReadOnlyList<(Guid TaskId, decimal Percent)> updates, CancellationToken cancellationToken);
}

public sealed record ResourceImportSummary(
    int WorkResourcesUsed, int TeamCreated, int TeamReused, int MaterialSkipped, int RolesUnresolved,
    int AssignmentsPlanned, int AssignmentsCreated, int AssignmentsReused, int AssignmentsSkipped, int TasksAffected, int TasksChanged,
    int EffortUpdated = 0, int EffortAlreadySet = 0, int EffortSkipped = 0,
    int ProgressUpdated = 0, int ProgressAlreadySet = 0, int ProgressSkipped = 0);

/// <summary>
/// Valida la configuración, crea o reutiliza un miembro genérico del equipo por cada recurso Work usado y después crea las asignaciones,
/// con los valores de planificación de las tareas afectadas antes y después.
/// </summary>
public sealed class ProjectResourceImporter
{
    private readonly IProjectResourceService _resources;
    private readonly IProjectTaskService _tasks;
    private readonly CalendarImportSettings _settings;
    private readonly TextWriter _out;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ProjectResourceImporter(IProjectResourceService resources, IProjectTaskService tasks, CalendarImportSettings settings,
        TextWriter output, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _resources = resources;
        _tasks = tasks;
        _settings = settings;
        _out = output;
        _delay = delay ?? Task.Delay;
    }

    private readonly List<(string Phase, TimeSpan Elapsed)> _timings = [];

    /// <summary>Duración de cada subfase de la última ejecución (para el bloque [Timing]).</summary>
    public IReadOnlyList<(string Phase, TimeSpan Elapsed)> PhaseTimings => _timings;

    public async Task<ResourceImportSummary> RunAsync(Guid projectId, MppProject project, ImportState state, Action save, CancellationToken ct)
    {
        _timings.Clear();
        var lap = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string phase) { _timings.Add((phase, lap.Elapsed)); lap.Restart(); }
        ValidateSettings();
        var taskPlan = TaskImportPlanner.Build(project, _settings.DurationRules, new TaskSelection(_settings.TaskMaxCount, _settings.TaskUniqueIds), projectId);
        var plan = ResourceImportPlanner.Build(project, taskPlan, projectId, _settings);

        // --- FASE 1: configuración (los GUID configurados se validan antes de tocar nada) ---
        var roleIds = plan.Resources.Select(r => r.RoleId)
            .Concat(_settings.DefaultResourceRoleId is { } d ? [d] : [])
            .Concat(_settings.ResourceRoleMappings.Select(m => m.ProjectOperationsRoleId))
            .Distinct().ToList();
        var config = await _resources.ValidateConfigurationAsync(roleIds, ct).ConfigureAwait(false);
        var generic = _settings.GenericBookableResourceId!.Value;
        var unit = _settings.DefaultResourcingUnitId!.Value;
        Line("Generic resource", $"{config.GenericResourceName} ({generic:D}) - shared by every generic team member; no bookable resource is created");
        Line("Resourcing unit", $"{config.ResourcingUnitName} ({unit:D})");
        Line("Default role", _settings.DefaultResourceRoleId is { } dr ? $"{RoleLabel(config, dr)}" : "(not configured)");
        Line("Role mappings", $"{_settings.ResourceRoleMappings.Count} configured");

        // Las tareas de las asignaciones tienen que existir ya en Dataverse.
        var before = (await _tasks.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var assignments = new List<PlannedAssignment>();
        var skipped = plan.SkippedAssignments.ToList();
        foreach (var a in plan.Assignments)
        {
            if (before.ContainsKey(a.Task.TaskId)) assignments.Add(a);
            else skipped.Add(new SkippedAssignment(a.Source, "la tarea no existe todavía en Dataverse"));
        }
        var resources = plan.Resources.Where(r => assignments.Any(a => a.Resource.Source.UniqueId == r.Source.UniqueId)).ToList();

        // --- FASE 2: recursos Work usados ---
        Line("Work resources used", $"{resources.Count + plan.Unresolved.Count} of {project.Resources.Count(r => r.Type == ResourceImportPlanner.WorkType)} Work resources in the JSON (unused ones are not processed)");
        Line("Generic team members", $"{resources.Count} to create or reuse; {plan.Unresolved.Count} without a resolvable role");
        Line("Material skipped", $"{plan.SkippedMaterials.Count} resources ({plan.SkippedMaterials.Sum(m => m.Assignments.Count)} assignments)");
        Line("Assignments", $"{assignments.Count} to create; {skipped.Count} skipped (of {project.ResourceAssignments.Count} in the JSON)");
        foreach (var group in skipped.GroupBy(s => s.Reason)) Line("Skipped", $"{group.Count()}: {group.Key}");
        foreach (var m in plan.SkippedMaterials)
            Line("WARNING", $"Material resource skipped: UniqueID {m.Resource.UniqueId}, GUID {m.Resource.Guid:D}, '{m.Resource.Name}'; assignments affected: {string.Join(", ", m.Assignments.Select(a => a.UniqueId))}");
        foreach (var s in skipped.Where(s => s.Reason == "asignación sin recurso"))
            Line("WARNING", $"Assignment {s.Source.UniqueId} (task {s.Source.TaskUniqueId} '{s.Source.TaskName}') has no resource; skipped.");

        state.Resources ??= [];
        foreach (var r in resources.Select(r => r.Source).Concat(plan.Unresolved.Select(u => u.Source)))
            if (!state.Resources.ContainsKey(r.UniqueId))
                state.Resources[r.UniqueId] = new ImportedResourceState { MppResourceUniqueId = r.UniqueId, MppResourceGuid = r.Guid ?? Guid.Empty, ResourceName = r.Name, ProjectId = projectId };

        Lap("Setup + configuration check + task read");

        // --- FASE 3: miembros genéricos del equipo (crear o reutilizar, nunca duplicar) ---
        var team = (await _resources.GetProjectTeamAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var teamIds = new Dictionary<int, (Guid Id, bool Created)>();
        foreach (var r in resources)
        {
            try
            {
                var notes = new List<string>();
                if (team.TryGetValue(r.ProjectTeamMemberId, out var existing))
                {
                    if (existing.BookableResourceId != generic)
                        throw new InvalidOperationException($"el miembro {r.ProjectTeamMemberId:D} ya existe en el proyecto pero apunta al recurso reservable {existing.BookableResourceId?.ToString("D") ?? "(ninguno)"} y no al genérico configurado {generic:D}; no se modifica. Elimina ese miembro del equipo y reanuda.");
                    if (!string.Equals(existing.Name, ResourceEntityBuilder.PositionName(r.Source), StringComparison.Ordinal)) notes.Add($"position name differs ('{existing.Name}')");
                    if (existing.RoleId != r.RoleId) notes.Add("role differs from the planned one");
                    if (existing.ResourcingUnitId != unit) notes.Add("resourcing unit differs from the planned one");
                    teamIds[r.Source.UniqueId] = (existing.Id, false);
                    PrintTeamMember(r, config, existing.Id, "Reused", notes.Count == 0 ? "existing team member kept" : "existing team member kept; WARNING " + string.Join("; ", notes));
                }
                else
                {
                    await _resources.CreateTeamMemberAsync(projectId, r, ct).ConfigureAwait(false);
                    teamIds[r.Source.UniqueId] = (r.ProjectTeamMemberId, true);
                    PrintTeamMember(r, config, r.ProjectTeamMemberId, "Created", "generic project team member created");
                }
                var item = state.Resources[r.Source.UniqueId];
                item.ProjectId = projectId; item.GenericBookableResourceId = generic; item.ProjectTeamMemberId = teamIds[r.Source.UniqueId].Id;
                item.RoleId = r.RoleId; item.ResourcingUnitId = unit;
                save();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not CalendarImportException)
            {
                PrintTeamMember(r, config, null, "Error", ex.Message);
                throw new CalendarImportException(ImportExitCodes.Resources, ResourceImportPlanner.Stage, $"Miembro del equipo de '{r.Source.Name}' (UniqueID {r.Source.UniqueId}): {ex.Message}", ex);
            }
        }
        foreach (var u in plan.Unresolved) PrintUnresolved(u);
        foreach (var m in plan.SkippedMaterials) PrintMaterial(m);
        Lap($"Team members ({resources.Count})");

        // --- FASE 4: asignaciones (proyecto, tarea y miembro genérico) ---
        var existingAssignments = await _resources.GetExistingAssignmentIdsAsync(projectId, ct).ConfigureAwait(false);
        var pending = assignments.Where(a => !existingAssignments.Contains(a.AssignmentId)).ToList();
        var created = 0;
        foreach (var batch in pending.Chunk(_settings.TaskBatchSize))
        {
            await _resources.CreateAssignmentsAsync(projectId, batch.Select(a => ResourceEntityBuilder.Assignment(a, projectId, teamIds[a.Resource.Source.UniqueId].Id)).ToList(), ct).ConfigureAwait(false);
            await _resources.WaitForAssignmentAsync(batch[^1].AssignmentId, ct).ConfigureAwait(false);
            foreach (var a in batch) state.CreatedAssignments.Add(a.AssignmentId);
            created += batch.Length;
            save();
            Line("Assignments created", $"{created}/{pending.Count}");
        }
        foreach (var a in assignments.Where(a => existingAssignments.Contains(a.AssignmentId))) state.CreatedAssignments.Add(a.AssignmentId);
        save();

        var readback = await ReadBackAssignmentsAsync(projectId, assignments, ct).ConfigureAwait(false);
        foreach (var a in assignments)
        {
            var wasCreated = !existingAssignments.Contains(a.AssignmentId);
            var po = readback.GetValueOrDefault(a.AssignmentId);
            var problems = new List<string>();
            if (po is null) problems.Add("not found in Dataverse");
            else
            {
                if (po.TaskId != a.Task.TaskId) problems.Add("task");
                if (po.TeamMemberId != teamIds[a.Resource.Source.UniqueId].Id) problems.Add("team member");
            }
            PrintAssignment(a, generic, teamIds[a.Resource.Source.UniqueId].Id,
                problems.Count > 0 ? $"Error ({string.Join(", ", problems)})" : wasCreated ? "Created" : "Reused");
        }
        foreach (var s in skipped) PrintSkippedAssignment(s);
        Lap($"Assignments ({assignments.Count}: create + wait + read-back)");

        // --- FASE 5 (opcional, ImportWork): esfuerzo de cada tarea = suma del Work de sus asignaciones en el MPP ---
        var affected = assignments.Select(a => a.Task).DistinctBy(t => t.TaskId).ToList();
        int effortUpdated = 0, effortAlready = 0, effortSkipped = 0;
        if (_settings.ImportWork && assignments.Count > 0)
        {
            // La plataforma recalcula el esfuerzo (duración x 100 %) tras crear las asignaciones: se espera a que se estabilice antes de fijarlo.
            var current = await ReadStableTasksAsync(projectId, affected, ct).ConfigureAwait(false);
            var (updates, workSkipped) = ResourceImportPlanner.BuildWorkUpdates(assignments);
            var pendingWork = new List<TaskWorkUpdate>();
            foreach (var u in updates)
            {
                var snapshot = current.GetValueOrDefault(u.Task.TaskId);
                if (snapshot?.Effort is { } e && Math.Abs(e - u.EffortHours) < 0.011m) { effortAlready++; PrintTaskWork(u, snapshot.Effort, "Reused (effort already equal)"); }
                else pendingWork.Add(u);
            }
            foreach (var (task, reason) in workSkipped) { effortSkipped++; PrintTaskWorkSkipped(task, reason); }
            if (pendingWork.Count > 0)
            {
                await _resources.UpdateTaskEffortAsync(projectId, pendingWork.Select(u => (u.Task.TaskId, u.EffortHours)).ToList(), ct).ConfigureAwait(false);
                foreach (var u in pendingWork) { PrintTaskWork(u, current.GetValueOrDefault(u.Task.TaskId)?.Effort, "Updated"); effortUpdated++; }
            }
        }

        if (_settings.ImportWork) Lap("Task effort (wait for stability + update)");

        // --- FASE 6 (opcional, ImportProgress): avance = PercentComplete del MPP; la plataforma deriva el trabajo completado y el restante ---
        int progressUpdated = 0, progressAlready = 0, progressSkipped = 0;
        if (_settings.ImportProgress)
        {
            var candidates = taskPlan.Tasks.Where(t => before.ContainsKey(t.TaskId)).ToList();
            var (pUpdates, pSkipped) = ResourceImportPlanner.BuildProgressUpdates(candidates);
            foreach (var (task, reason) in pSkipped) { progressSkipped++; PrintTaskProgressSkipped(task, reason); }
            if (pUpdates.Count > 0)
            {
                var progressTasks = pUpdates.Select(u => u.Task).ToList();
                var current = await ReadStableTasksAsync(projectId, progressTasks, ct).ConfigureAwait(false);
                var progressPending = new List<TaskProgressUpdate>();
                foreach (var u in pUpdates)
                {
                    // Project Operations rechazó el avance de una tarea con esfuerzo 0 (tarea 530 de la prueba: el lote entero falló con ScheduleAPI-SRV-0008).
                    if (current.GetValueOrDefault(u.Task.TaskId)?.Effort is not > 0) { progressSkipped++; PrintTaskProgressSkipped(u.Task, "tarea sin esfuerzo: la plataforma rechaza el avance de una tarea con esfuerzo 0"); continue; }
                    if (current.GetValueOrDefault(u.Task.TaskId)?.Progress is { } p && Math.Abs(p - u.Percent) < 0.011m) { progressAlready++; PrintTaskProgress(u, assignments, current.GetValueOrDefault(u.Task.TaskId), current.GetValueOrDefault(u.Task.TaskId), "Reused (progress already equal)"); }
                    else progressPending.Add(u);
                }
                if (progressPending.Count > 0)
                {
                    await _resources.UpdateTaskProgressAsync(projectId, progressPending.Select(u => (u.Task.TaskId, u.Percent)).ToList(), ct).ConfigureAwait(false);
                    var updated = await ReadStableTasksAsync(projectId, progressPending.Select(u => u.Task).ToList(), ct).ConfigureAwait(false);
                    foreach (var u in progressPending) { PrintTaskProgress(u, assignments, current.GetValueOrDefault(u.Task.TaskId), updated.GetValueOrDefault(u.Task.TaskId), "Updated"); progressUpdated++; }
                }
                affected = affected.Concat(progressTasks).DistinctBy(t => t.TaskId).ToList();      // sus fechas también se registran antes y después
            }
        }

        if (_settings.ImportProgress) Lap("Task progress (wait for stability + update)");

        // --- Planificación de las tareas afectadas, antes y después ---
        var after = await ReadStableTasksAsync(projectId, affected, ct).ConfigureAwait(false);
        var changed = PrintTaskPlanning(affected, before, after);

        PrintRoleReport(resources, plan.Unresolved, config);
        Lap("Planning read-back + role report");

        var assignmentsReused = assignments.Count - pending.Count;
        _out.WriteLine();
        _out.WriteLine("[Resources summary]");
        Line("Generic team members", $"{resources.Count} used (created {teamIds.Values.Count(t => t.Created)}, reused {teamIds.Values.Count(t => !t.Created)}); {plan.Unresolved.Count} without role (error)");
        Line("Bookable resources", $"0 created (all members use the generic resource {generic:D})");
        Line("Material skipped", $"{plan.SkippedMaterials.Count} resources, {plan.SkippedMaterials.Sum(m => m.Assignments.Count)} assignments");
        Line("Assignments", $"created {created}, reused {assignmentsReused}, skipped {skipped.Count}");
        Line("Tasks affected", $"{affected.Count}; {changed} changed Start/Finish/Duration/Effort after the assignments");
        if (_settings.ImportWork) Line("Task effort", $"updated {effortUpdated}, already equal {effortAlready}, skipped {effortSkipped} (msdyn_effort = sum of the MPP Work of the task assignments; completed/remaining/progress are not imported)");
        if (_settings.ImportProgress) Line("Task progress", $"updated {progressUpdated}, already equal {progressAlready}, skipped {progressSkipped} (msdyn_progress = MPP PercentComplete; completed/remaining are derived by Project Operations)");
        Line("Note", "No Work, Units, costs, rates, price lists or resource calendars were imported; differences are observations of what Project Operations calculated (times are UTC as returned).");
        var summary = new ResourceImportSummary(resources.Count, teamIds.Values.Count(t => t.Created), teamIds.Values.Count(t => !t.Created), plan.SkippedMaterials.Count, plan.Unresolved.Count,
            assignments.Count, created, assignmentsReused, skipped.Count, affected.Count, changed, effortUpdated, effortAlready, effortSkipped, progressUpdated, progressAlready, progressSkipped);
        if (plan.Unresolved.Count > 0)
            throw new CalendarImportException(ImportExitCodes.Resources, ResourceImportPlanner.Stage,
                $"{plan.Unresolved.Count} recurso(s) Work usados no tienen rol resoluble ({string.Join(", ", plan.Unresolved.Select(u => $"'{u.Source.Name}'"))}). No se crearon sus miembros ni sus asignaciones; configura ResourceRoleMappings o DefaultResourceRoleId.");
        return summary;
    }

    private void ValidateSettings()
    {
        if (_settings.GenericBookableResourceId is null || _settings.GenericBookableResourceId == Guid.Empty)
            throw Fail("CalendarImport.GenericBookableResourceId no está configurado (GUID del recurso reservable genérico existente).");
        if (_settings.DefaultResourcingUnitId is null || _settings.DefaultResourcingUnitId == Guid.Empty)
            throw Fail("CalendarImport.DefaultResourcingUnitId no está configurado (GUID de la unidad de dotación de recursos).");
        foreach (var (m, i) in _settings.ResourceRoleMappings.Select((m, i) => (m, i)))
        {
            if (m.ProjectOperationsRoleId == Guid.Empty) throw Fail($"ResourceRoleMappings[{i}]: falta ProjectOperationsRoleId.");
            if (m.MppResourceGuid is null && m.MppResourceUniqueId is null && string.IsNullOrWhiteSpace(m.MppResourceName))
                throw Fail($"ResourceRoleMappings[{i}]: indica MppResourceGuid, MppResourceUniqueId o MppResourceName.");
        }
    }

    private static CalendarImportException Fail(string message) => new(ImportExitCodes.Resources, ResourceImportPlanner.Stage, message);

    private static string RoleLabel(ResourceConfigCheck config, Guid roleId)
        => config.RoleNames.TryGetValue(roleId, out var name) ? $"{name} ({roleId:D})" : roleId.ToString("D");

    private async Task<Dictionary<Guid, AssignmentSnapshot>> ReadBackAssignmentsAsync(Guid projectId, List<PlannedAssignment> planned, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_settings.TaskReadbackTimeoutSeconds);
        while (true)
        {
            var all = (await _resources.ReadAssignmentsAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(s => s.Id);
            if (planned.All(p => all.ContainsKey(p.AssignmentId)) || DateTime.UtcNow >= deadline) return all;
            await _delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Project Operations recalcula de forma asíncrona: se lee hasta que dos lecturas seguidas de las tareas afectadas coinciden.</summary>
    private async Task<Dictionary<Guid, TaskSnapshot>> ReadStableTasksAsync(Guid projectId, List<PlannedTask> affected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_settings.TaskReadbackTimeoutSeconds);
        Dictionary<Guid, TaskSnapshot>? previous = null;
        while (true)
        {
            var current = (await _tasks.ReadTasksAsync(projectId, ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
            if (previous is not null && affected.All(t => SamePlanning(previous.GetValueOrDefault(t.TaskId), current.GetValueOrDefault(t.TaskId)))) return current;
            if (DateTime.UtcNow >= deadline) return current;
            previous = current;
            await _delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private static bool SamePlanning(TaskSnapshot? a, TaskSnapshot? b)
        => a is not null && b is not null && a.Start == b.Start && a.End == b.End && a.DurationDays == b.DurationDays && a.Effort == b.Effort
           && a.Progress == b.Progress && a.EffortCompleted == b.EffortCompleted && a.EffortRemaining == b.EffortRemaining;

    private int PrintTaskPlanning(List<PlannedTask> affected, Dictionary<Guid, TaskSnapshot> before, Dictionary<Guid, TaskSnapshot> after)
    {
        var changed = 0;
        foreach (var task in affected)
        {
            before.TryGetValue(task.TaskId, out var b);
            after.TryGetValue(task.TaskId, out var a);
            var same = SamePlanning(b, a);
            if (!same) changed++;
            _out.WriteLine();
            _out.WriteLine("[Task planning]");
            Line("MPP UniqueID", task.Source.UniqueId.ToString());
            Line("Name", task.Source.Name);
            Line("Before assignment", Planning(b));
            Line("After assignment", Planning(a));
            Line("Result", same ? "UNCHANGED" : "CHANGED " + string.Join(", ", Diffs(b, a)));
        }
        return changed;
    }

    private static IEnumerable<string> Diffs(TaskSnapshot? b, TaskSnapshot? a)
    {
        if (b is null || a is null) { yield return "(missing snapshot)"; yield break; }
        if (b.Start != a.Start) yield return "Start";
        if (b.End != a.End) yield return "Finish";
        if (b.DurationDays != a.DurationDays) yield return "Duration";
        if (b.Effort != a.Effort) yield return "Effort";
    }

    private static string Planning(TaskSnapshot? s)
        => s is null ? "-" : $"Start {Fmt(s.Start)} | Finish {Fmt(s.End)} | Duration {s.DurationDays?.ToString(CultureInfo.InvariantCulture) ?? "-"} d | Effort {s.Effort?.ToString(CultureInfo.InvariantCulture) ?? "-"} h";

    /// <summary>
    /// Informe para detectar: varios recursos con el mismo rol, recursos sin mapping (rol predeterminado o error) y recursos con tarifas distintas del MPP
    /// bajo el mismo rol (la tarifa solo se lee para este informe; no se importa).
    /// </summary>
    private void PrintRoleReport(List<PlannedResource> resources, IReadOnlyList<UnresolvedResource> unresolved, ResourceConfigCheck config)
    {
        _out.WriteLine();
        _out.WriteLine("[Role report]");
        foreach (var group in resources.GroupBy(r => r.RoleId).OrderByDescending(g => g.Count()))
            Line("Role", $"{RoleLabel(config, group.Key)}: {group.Count()} resource(s): {string.Join("; ", group.Select(r => r.Source.Name))}");
        var defaulted = resources.Where(r => r.RoleMethod == ResourceRoleResolver.ByDefault).ToList();
        Line("Using default role", defaulted.Count == 0 ? "0" : $"{defaulted.Count}: {string.Join("; ", defaulted.Select(r => r.Source.Name))}");
        Line("Without role (error)", unresolved.Count == 0 ? "0" : $"{unresolved.Count}: {string.Join("; ", unresolved.Select(u => $"{u.Source.Name} [{u.Error}]"))}");
        foreach (var group in resources.GroupBy(r => r.RoleId).Where(g => g.Count() > 1))
            Line("Shared role", $"{RoleLabel(config, group.Key)} is used by {group.Count()} MPP resources");
        foreach (var group in resources.GroupBy(r => r.RoleId))
        {
            var rates = group.Where(r => r.Source.StandardRate is > 0)
                .GroupBy(r => $"{r.Source.StandardRate?.ToString(CultureInfo.InvariantCulture)}/{r.Source.StandardRateUnits}").ToList();
            if (rates.Count > 1)
                Line("Different MPP rates", $"{RoleLabel(config, group.Key)}: {string.Join(" | ", rates.Select(g => $"{g.Key} ({string.Join(", ", g.Select(r => r.Source.Name))})"))} - rates are NOT imported; they would need distinct roles or price lines");
        }
    }

    private void PrintTeamMember(PlannedResource r, ResourceConfigCheck config, Guid? teamId, string result, string detail)
    {
        _out.WriteLine();
        _out.WriteLine("[Team member]");
        Line("MPP UniqueID", r.Source.UniqueId.ToString());
        Line("MPP GUID", r.Source.Guid?.ToString("D") ?? "-");
        Line("MPP Resource Name", r.Source.Name ?? "-");
        Line("Position name", ResourceEntityBuilder.PositionName(r.Source));
        Line("Role", RoleLabel(config, r.RoleId));
        Line("Role resolution", r.RoleMethod);
        Line("Resourcing unit", $"{config.ResourcingUnitName} ({_settings.DefaultResourcingUnitId:D})");
        Line("Generic bookable res.", _settings.GenericBookableResourceId?.ToString("D") ?? "-");
        Line("ProjectTeamMemberId", teamId?.ToString("D") ?? "-");
        Line("Assignments (usable)", r.AssignmentCount.ToString());
        Line("Result", $"{result} ({detail})");
    }

    private void PrintTaskWork(TaskWorkUpdate u, decimal? effortBefore, string result)
    {
        _out.WriteLine();
        _out.WriteLine("[Task work]");
        Line("MPP UniqueID", u.Task.Source.UniqueId.ToString());
        Line("Name", u.Task.Source.Name);
        Line("MPP Work (sum)", $"{u.EffortHours.ToString("0.##", CultureInfo.InvariantCulture)} h ({u.AssignmentCount} assignment(s))");
        Line("Effort before", effortBefore is { } e ? $"{e.ToString("0.##", CultureInfo.InvariantCulture)} h" : "-");
        Line("Result", result);
    }

    private void PrintTaskProgress(TaskProgressUpdate u, IReadOnlyList<PlannedAssignment> assignments, TaskSnapshot? before, TaskSnapshot? after, string result)
    {
        var mine = assignments.Where(a => a.Task.TaskId == u.Task.TaskId).ToList();
        decimal? mppActual = mine.Count > 0 && mine.All(a => a.Source.ActualWorkHours is not null) ? mine.Sum(a => a.Source.ActualWorkHours!.Value) : null;
        string H(decimal? v) => v is { } x ? $"{x.ToString("0.##", CultureInfo.InvariantCulture)} h" : "-";
        _out.WriteLine();
        _out.WriteLine("[Task progress]");
        Line("MPP UniqueID", u.Task.Source.UniqueId.ToString());
        Line("Name", u.Task.Source.Name);
        Line("MPP", $"PercentComplete {u.Percent.ToString("0.##", CultureInfo.InvariantCulture)} % | Actual work {H(mppActual)}");
        Line("PO before", $"progress {before?.Progress?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"} % | completed {H(before?.EffortCompleted)} | remaining {H(before?.EffortRemaining)}");
        Line("PO after", $"progress {after?.Progress?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"} % | completed {H(after?.EffortCompleted)} | remaining {H(after?.EffortRemaining)}");
        var note = mppActual is { } a && after?.EffortCompleted is { } c && Math.Abs(a - c) >= 0.011m ? $" (WARNING: PO completed {H(c)} differs from MPP actual work {H(a)})" : string.Empty;
        Line("Result", result + note);
    }

    private void PrintTaskProgressSkipped(PlannedTask task, string reason)
    {
        _out.WriteLine();
        _out.WriteLine("[Task progress]");
        Line("MPP UniqueID", task.Source.UniqueId.ToString());
        Line("Name", task.Source.Name);
        Line("Result", $"Skipped ({reason})");
    }

    private void PrintTaskWorkSkipped(PlannedTask task, string reason)
    {
        _out.WriteLine();
        _out.WriteLine("[Task work]");
        Line("MPP UniqueID", task.Source.UniqueId.ToString());
        Line("Name", task.Source.Name);
        Line("Result", $"Skipped ({reason})");
    }

    private void PrintUnresolved(UnresolvedResource u)
    {
        _out.WriteLine();
        _out.WriteLine("[Team member]");
        Line("MPP UniqueID", u.Source.UniqueId.ToString());
        Line("MPP GUID", u.Source.Guid?.ToString("D") ?? "-");
        Line("MPP Resource Name", u.Source.Name ?? "-");
        Line("Position name", "-");
        Line("Role", "-");
        Line("ProjectTeamMemberId", "-");
        Line("Assignments (usable)", u.AssignmentCount.ToString());
        Line("Result", $"Error ({u.Error}; team member and assignments not created)");
    }

    private void PrintMaterial(SkippedMaterial m)
    {
        _out.WriteLine();
        _out.WriteLine("[Resource]");
        Line("MPP UniqueID", m.Resource.UniqueId.ToString());
        Line("MPP GUID", m.Resource.Guid?.ToString("D") ?? "-");
        Line("Name", m.Resource.Name ?? "-");
        Line("Type", m.Resource.Type);
        Line("ProjectTeamMemberId", "-");
        Line("Assignments affected", string.Join(", ", m.Assignments.Select(a => a.UniqueId)));
        Line("Result", "Skipped (Material resources are not imported yet)");
    }

    private void PrintAssignment(PlannedAssignment a, Guid genericId, Guid teamId, string result)
    {
        _out.WriteLine();
        _out.WriteLine("[Assignment]");
        Line("Assignment UniqueID", a.Source.UniqueId.ToString());
        Line("Task UniqueID", a.Task.Source.UniqueId.ToString());
        Line("Task Name", a.Task.Source.Name);
        Line("Resource UniqueID", a.Resource.Source.UniqueId.ToString());
        Line("Resource Name", a.Resource.Source.Name ?? "-");
        Line("Generic bookable res.", genericId.ToString("D"));
        Line("ProjectTeamMemberId", teamId.ToString("D"));
        Line("ResourceAssignmentId", a.AssignmentId.ToString("D"));
        Line("Result", result);
    }

    private void PrintSkippedAssignment(SkippedAssignment s)
    {
        _out.WriteLine();
        _out.WriteLine("[Assignment]");
        Line("Assignment UniqueID", s.Source.UniqueId.ToString());
        Line("Task UniqueID", s.Source.TaskUniqueId?.ToString() ?? "-");
        Line("Task Name", s.Source.TaskName ?? "-");
        Line("Resource UniqueID", s.Source.ResourceUniqueId?.ToString() ?? "-");
        Line("Resource Name", s.Source.ResourceName ?? "-");
        Line("ProjectTeamMemberId", "-");
        Line("ResourceAssignmentId", "-");
        Line("Result", $"Skipped ({s.Reason})");
    }

    private static string Fmt(DateTime? value) => value is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (v.Kind == DateTimeKind.Utc ? "Z" : string.Empty) : "-";
    private void Line(string label, string value) => _out.WriteLine($"      {label,-22}: {value}");
}

public sealed class DataverseProjectResourceService : IProjectResourceService
{
    private readonly DataverseClient _client;
    private readonly CalendarImportSettings _settings;
    private Guid _lastOperationSetId;

    public DataverseProjectResourceService(DataverseClient client, CalendarImportSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<ResourceConfigCheck> ValidateConfigurationAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        var generic = _settings.GenericBookableResourceId ?? throw Fail("GenericBookableResourceId no está configurado.");
        var unit = _settings.DefaultResourcingUnitId ?? throw Fail("DefaultResourcingUnitId no está configurado.");

        var genericRow = await FindAsync("bookableresources", "bookableresourceid", generic, "name,statecode,resourcetype", ct).ConfigureAwait(false)
            ?? throw Fail($"GenericBookableResourceId {generic:D} no existe en Dataverse (bookableresources).");
        RequireActive(genericRow, "GenericBookableResourceId", generic);
        if (genericRow["resourcetype"]?.GetValue<int>() != 1)
            throw Fail($"GenericBookableResourceId {generic:D} ('{genericRow["name"]?.GetValue<string>()}') no es un recurso genérico (resourcetype distinto de 1).");

        var unitRow = await FindAsync("msdyn_organizationalunits", "msdyn_organizationalunitid", unit, "msdyn_name,statecode", ct).ConfigureAwait(false)
            ?? throw Fail($"DefaultResourcingUnitId {unit:D} no existe en Dataverse (msdyn_organizationalunits).");
        RequireActive(unitRow, "DefaultResourcingUnitId", unit);

        var roles = new Dictionary<Guid, string>();
        foreach (var roleId in roleIds.Distinct())
        {
            var row = await FindAsync("bookableresourcecategories", "bookableresourcecategoryid", roleId, "name,statecode", ct).ConfigureAwait(false)
                ?? throw Fail($"El rol {roleId:D} no existe en Dataverse (bookableresourcecategories).");
            RequireActive(row, "El rol", roleId);
            roles[roleId] = row["name"]?.GetValue<string>() ?? roleId.ToString("D");
        }
        return new ResourceConfigCheck(genericRow["name"]?.GetValue<string>() ?? generic.ToString("D"), unitRow["msdyn_name"]?.GetValue<string>() ?? unit.ToString("D"), roles);
    }

    private async Task<JsonNode?> FindAsync(string entitySet, string idAttribute, Guid id, string select, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"{idAttribute} eq {id:D}");
        var response = await _client.GetAsync($"{entitySet}?$select={select}&$filter={filter}&$top=1", ct).ConfigureAwait(false);
        return (response["value"] as JsonArray)?.FirstOrDefault();
    }

    private static void RequireActive(JsonNode row, string what, Guid id)
    {
        if (row["statecode"]?.GetValue<int>() != 0) throw Fail($"{what} {id:D} no está activo.");
    }

    private static CalendarImportException Fail(string message) => new(ImportExitCodes.Resources, ResourceImportPlanner.Stage, message);

    public async Task<IReadOnlyList<TeamMemberSnapshot>> GetProjectTeamAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync(
            $"msdyn_projectteams?$select=msdyn_projectteamid,msdyn_name,_msdyn_bookableresourceid_value,_msdyn_resourcecategory_value,_msdyn_organizationalunit_value&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        static Guid? G(JsonNode? n, string key) => Guid.TryParse(n?[key]?.GetValue<string>(), out var g) ? g : null;
        var list = new List<TeamMemberSnapshot>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (G(item, "msdyn_projectteamid") is { } id)
                list.Add(new TeamMemberSnapshot(id, item?["msdyn_name"]?.GetValue<string>(), G(item, "_msdyn_bookableresourceid_value"),
                    G(item, "_msdyn_resourcecategory_value"), G(item, "_msdyn_organizationalunit_value")));
        return list;
    }

    public async Task CreateTeamMemberAsync(Guid projectId, PlannedResource resource, CancellationToken ct)
    {
        await _client.PostActionAsync("msdyn_CreateTeamMemberV1",
            new JsonObject { ["TeamMember"] = ResourceEntityBuilder.TeamMember(resource, projectId, _settings) }, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(_settings.OperationTimeoutSeconds);
        var filter = Uri.EscapeDataString($"msdyn_projectteamid eq {resource.ProjectTeamMemberId:D}");
        while (true)
        {
            var response = await _client.GetAsync($"msdyn_projectteams?$select=msdyn_projectteamid&$filter={filter}&$top=1", ct).ConfigureAwait(false);
            if ((response["value"] as JsonArray)?.Count > 0) return;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"El miembro del equipo {resource.ProjectTeamMemberId:D} no apareció en {_settings.OperationTimeoutSeconds} s.");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlySet<Guid>> GetExistingAssignmentIdsAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_projectid_value eq {projectId:D}");
        var response = await _client.GetAsync($"msdyn_resourceassignments?$select=msdyn_resourceassignmentid&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var set = new HashSet<Guid>();
        foreach (var item in response["value"] as JsonArray ?? [])
            if (Guid.TryParse(item?["msdyn_resourceassignmentid"]?.GetValue<string>(), out var id)) set.Add(id);
        return set;
    }

    public async Task CreateAssignmentsAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken ct)
    {
        var operationSet = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Importación MPP: crear {entities.Count} asignaciones"
        }, ct).ConfigureAwait(false);
        _lastOperationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        var array = new JsonArray();
        foreach (var entity in entities) array.Add(entity);
        await _client.PostActionAsync("msdyn_PssCreateV2", new JsonObject { ["EntityCollection"] = array, ["OperationSetId"] = _lastOperationSetId.ToString("D") }, ct).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = _lastOperationSetId.ToString("D") }, ct).ConfigureAwait(false);
    }

    public Task WaitForAssignmentAsync(Guid assignmentId, CancellationToken ct)
        => PssOperationSets.WaitForRecordOrFailureAsync(_client, _lastOperationSetId, "msdyn_resourceassignments", assignmentId, _settings.OperationTimeoutSeconds, ct);

    private static decimal? ToDecimal(JsonNode? node)
        => node is JsonValue v ? (v.TryGetValue<decimal>(out var d) ? d : v.TryGetValue<double>(out var x) ? (decimal)x : null) : null;

    public async Task UpdateTaskEffortAsync(Guid projectId, IReadOnlyList<(Guid TaskId, decimal Hours)> updates, CancellationToken ct)
    {
        var operationSet = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Importación MPP: esfuerzo de {updates.Count} tareas"
        }, ct).ConfigureAwait(false);
        var operationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        var array = new JsonArray();
        foreach (var (taskId, hours) in updates) array.Add(ResourceEntityBuilder.TaskEffort(taskId, hours));
        await _client.PostActionAsync("msdyn_PssUpdateV2", new JsonObject { ["EntityCollection"] = array, ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_settings.OperationTimeoutSeconds);
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        while (true)
        {
            var response = await _client.GetAsync($"msdyn_projecttasks?$select=msdyn_projecttaskid,msdyn_effort&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
            var efforts = new Dictionary<Guid, decimal>();
            foreach (var item in response["value"] as JsonArray ?? [])
                if (Guid.TryParse(item?["msdyn_projecttaskid"]?.GetValue<string>(), out var id) && ToDecimal(item!["msdyn_effort"]) is { } effort) efforts[id] = effort;
            if (updates.All(u => efforts.TryGetValue(u.TaskId, out var e) && Math.Abs(e - u.Hours) < 0.011m)) return;
            var set = await _client.GetAsync($"msdyn_operationsets({operationSetId:D})?$select=msdyn_status,_msdyn_psserrorlog_value", ct).ConfigureAwait(false);
            if (Guid.TryParse(set["_msdyn_psserrorlog_value"]?.GetValue<string>(), out var logId))
            {
                var log = await _client.GetAsync($"msdyn_psserrorlogs({logId:D})?$select=msdyn_errorcode,msdyn_log", ct).ConfigureAwait(false);
                throw new InvalidOperationException($"El servicio de planificación rechazó el esfuerzo (OperationSet {operationSetId:D}): {log["msdyn_errorcode"]}: {log["msdyn_log"]}");
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Las tareas no tomaron el esfuerzo enviado en {_settings.OperationTimeoutSeconds} s (OperationSet {operationSetId:D}, estado {set["msdyn_status"]}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateTaskProgressAsync(Guid projectId, IReadOnlyList<(Guid TaskId, decimal Percent)> updates, CancellationToken ct)
    {
        var operationSet = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = $"Importación MPP: avance de {updates.Count} tareas"
        }, ct).ConfigureAwait(false);
        var operationSetId = JsonHelpers.RequiredGuid(operationSet, "OperationSetId", "msdyn_operationsetid");
        var array = new JsonArray();
        foreach (var (taskId, percent) in updates) array.Add(ResourceEntityBuilder.TaskProgress(taskId, percent));
        await _client.PostActionAsync("msdyn_PssUpdateV2", new JsonObject { ["EntityCollection"] = array, ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_settings.OperationTimeoutSeconds);
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        while (true)
        {
            var response = await _client.GetAsync($"msdyn_projecttasks?$select=msdyn_projecttaskid,msdyn_progress&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
            var stored = new Dictionary<Guid, decimal>();
            foreach (var item in response["value"] as JsonArray ?? [])
                if (Guid.TryParse(item?["msdyn_projecttaskid"]?.GetValue<string>(), out var id) && ToDecimal(item!["msdyn_progress"]) is { } progress) stored[id] = progress;
            if (updates.All(u => stored.TryGetValue(u.TaskId, out var p) && Math.Abs(p - u.Percent) < 0.011m)) return;
            var set = await _client.GetAsync($"msdyn_operationsets({operationSetId:D})?$select=msdyn_status,_msdyn_psserrorlog_value", ct).ConfigureAwait(false);
            if (Guid.TryParse(set["_msdyn_psserrorlog_value"]?.GetValue<string>(), out var logId))
            {
                var log = await _client.GetAsync($"msdyn_psserrorlogs({logId:D})?$select=msdyn_errorcode,msdyn_log", ct).ConfigureAwait(false);
                throw new InvalidOperationException($"El servicio de planificación rechazó el avance (OperationSet {operationSetId:D}): {log["msdyn_errorcode"]}: {log["msdyn_log"]}");
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Las tareas no tomaron el avance enviado en {_settings.OperationTimeoutSeconds} s (OperationSet {operationSetId:D}, estado {set["msdyn_status"]}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<AssignmentSnapshot>> ReadAssignmentsAsync(Guid projectId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"_msdyn_projectid_value eq {projectId:D}");
        var response = await _client.GetAsync(
            $"msdyn_resourceassignments?$select=msdyn_resourceassignmentid,_msdyn_taskid_value,_msdyn_projectteamid_value&$filter={filter}&$top=5000", ct).ConfigureAwait(false);
        var list = new List<AssignmentSnapshot>();
        foreach (var item in response["value"] as JsonArray ?? [])
        {
            if (!Guid.TryParse(item?["msdyn_resourceassignmentid"]?.GetValue<string>(), out var id)) continue;
            list.Add(new AssignmentSnapshot(id,
                Guid.TryParse(item!["_msdyn_taskid_value"]?.GetValue<string>(), out var task) ? task : null,
                Guid.TryParse(item["_msdyn_projectteamid_value"]?.GetValue<string>(), out var member) ? member : null));
        }
        return list;
    }
}
