using System.Text.Json.Nodes;
using ProjectOpsExcelImporter.Calendars;
using Xunit;
using static ProjectOpsExcelImporter.Tests.Dump;

namespace ProjectOpsExcelImporter.Tests;

internal static class ResourceConfig
{
    public static readonly Guid Generic = Guid.Parse("7352915d-47c8-f111-a05a-0022486f619f");
    public static readonly Guid Unit = Guid.Parse("0fbb4e57-47c8-f111-a05a-0022486f619f");
    public static readonly Guid DefaultRole = Guid.Parse("11bb4e57-47c8-f111-a05a-0022486f619f");
    public static readonly Guid RoleManager = Guid.Parse("10bb4e57-47c8-f111-a05a-0022486f619f");
    public static readonly Guid RoleArchitect = Guid.Parse("d5c16967-bce1-f111-84e8-e4fade0d2649");

    public static CalendarImportSettings Settings(params ResourceRoleMapping[] maps) => new()
    {
        ImportResources = true, TaskReadbackTimeoutSeconds = 0,
        GenericBookableResourceId = Generic, DefaultResourcingUnitId = Unit, DefaultResourceRoleId = DefaultRole,
        ResourceRoleMappings = [.. maps]
    };
}

internal sealed class FakeResources : IProjectResourceService
{
    /// <summary>Miembros del equipo de ESTE proyecto que "existen" en Dataverse.</summary>
    public Dictionary<Guid, TeamMemberSnapshot> Team { get; } = [];
    public HashSet<Guid> ExistingAssignments { get; } = [];
    /// <summary>Roles / registros configurados que no existen (la validación inicial falla).</summary>
    public HashSet<Guid> Missing { get; } = [];
    public List<IReadOnlyCollection<Guid>> ValidatedRoleIds { get; } = [];
    public List<(PlannedResource Resource, JsonObject Entity)> TeamCreates { get; } = [];
    public List<JsonObject> AssignmentEntities { get; } = [];
    private readonly CalendarImportSettings _settings;
    /// <summary>Se invoca tras crear un lote de asignaciones; sirve para simular que Project Operations cambia la planificación.</summary>
    public Action? OnCreateAssignments { get; set; }

    public FakeResources(CalendarImportSettings? settings = null) => _settings = settings ?? ResourceConfig.Settings();

    public Task<ResourceConfigCheck> ValidateConfigurationAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        ValidatedRoleIds.Add(roleIds);
        foreach (var id in new[] { _settings.GenericBookableResourceId!.Value, _settings.DefaultResourcingUnitId!.Value }.Concat(roleIds))
            if (Missing.Contains(id)) throw new CalendarImportException(ImportExitCodes.Resources, ResourceImportPlanner.Stage, $"El registro {id:D} no existe en Dataverse.");
        return Task.FromResult(new ResourceConfigCheck("Recurso genérico", "Unidad de prueba", roleIds.ToDictionary(r => r, r => $"Rol {r.ToString("D")[..4]}")));
    }

    public Task<IReadOnlyList<TeamMemberSnapshot>> GetProjectTeamAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TeamMemberSnapshot>>(Team.Values.ToList());

    public Task CreateTeamMemberAsync(Guid projectId, PlannedResource resource, CancellationToken ct)
    {
        var entity = ResourceEntityBuilder.TeamMember(resource, projectId, _settings);
        TeamCreates.Add((resource, entity));
        Team[resource.ProjectTeamMemberId] = new TeamMemberSnapshot(resource.ProjectTeamMemberId, ResourceEntityBuilder.PositionName(resource.Source),
            _settings.GenericBookableResourceId, resource.RoleId, _settings.DefaultResourcingUnitId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<Guid>> GetExistingAssignmentIdsAsync(Guid projectId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(ExistingAssignments.ToHashSet());

    public Task CreateAssignmentsAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken ct)
    {
        foreach (var e in entities) { AssignmentEntities.Add(e); ExistingAssignments.Add(Guid.Parse(e["msdyn_resourceassignmentid"]!.GetValue<string>())); }
        OnCreateAssignments?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Actualizaciones de esfuerzo recibidas (tarea, horas) y acción que simula el recálculo de la plataforma.</summary>
    public List<(Guid TaskId, decimal Hours)> EffortUpdates { get; } = [];
    public Action<Guid, decimal>? OnEffortUpdate { get; set; }
    public Task UpdateTaskEffortAsync(Guid projectId, IReadOnlyList<(Guid TaskId, decimal Hours)> updates, CancellationToken ct)
    {
        foreach (var u in updates) { EffortUpdates.Add(u); OnEffortUpdate?.Invoke(u.TaskId, u.Hours); }
        return Task.CompletedTask;
    }

    /// <summary>Actualizaciones de avance recibidas (tarea, porcentaje 0-100) y acción que simula la plataforma.</summary>
    public List<(Guid TaskId, decimal Percent)> ProgressUpdates { get; } = [];
    public Action<Guid, decimal>? OnProgressUpdate { get; set; }
    public Task UpdateTaskProgressAsync(Guid projectId, IReadOnlyList<(Guid TaskId, decimal Percent)> updates, CancellationToken ct)
    {
        foreach (var u in updates) { ProgressUpdates.Add(u); OnProgressUpdate?.Invoke(u.TaskId, u.Percent); }
        return Task.CompletedTask;
    }

    public Task WaitForAssignmentAsync(Guid assignmentId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<AssignmentSnapshot>> ReadAssignmentsAsync(Guid projectId, CancellationToken ct)
    {
        static Guid Id(JsonObject e, string key) => Guid.Parse(e[key]!.GetValue<string>().Split('(', ')')[1]);
        return Task.FromResult<IReadOnlyList<AssignmentSnapshot>>(AssignmentEntities
            .Select(e => new AssignmentSnapshot(Guid.Parse(e["msdyn_resourceassignmentid"]!.GetValue<string>()), Id(e, "msdyn_taskid@odata.bind"), Id(e, "msdyn_projectteamid@odata.bind")))
            .ToList());
    }
}

public class ResourcePlannerTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    internal static MppProject Parse(object[] assignments, object[]? resources = null)
        => MppProjectReader.Parse(Build([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))],
            tasks:
            [
                TaskRow(1, "Fase", 1, 0, summary: true),
                TaskRow(2, "A", 2, 1, "2026-11-16T09:00:00", "2026-11-16T17:00:00"),
                TaskRow(3, "B", 2, 1, "2026-11-17T09:00:00", "2026-11-17T17:00:00"),
                TaskRow(4, "C", 2, 1, "2026-11-18T09:00:00", "2026-11-18T17:00:00")
            ],
            resources: resources ??
            [
                Resource(0, null, id: 0),                        // marcador "sin recurso" de Project
                Resource(10, "Ingeniero A", id: 1),
                Resource(11, "Ingeniero B", id: 2),
                Resource(12, "Ingeniero C (sin uso)", id: 3),
                Resource(20, "Cámara", "Material", id: 4)
            ],
            resourceAssignments: assignments));

    internal static ResourcePlan Plan(object[] assignments, object[]? resources = null, TaskSelection? selection = null, CalendarImportSettings? settings = null)
    {
        var project = Parse(assignments, resources);
        var taskPlan = TaskImportPlanner.Build(project, new TaskDurationRules(), selection ?? new TaskSelection(), ProjectId);
        return ResourceImportPlanner.Build(project, taskPlan, ProjectId, settings ?? ResourceConfig.Settings());
    }

    // 1 y 2
    [Fact]
    public void Un_recurso_Work_utilizado_tiene_miembro_generico_y_uno_no_utilizado_no()
    {
        var plan = Plan([Assignment(1, 2, 10)]);
        var resource = Assert.Single(plan.Resources);
        Assert.Equal(10, resource.Source.UniqueId);
        Assert.NotEqual(Guid.Empty, resource.ProjectTeamMemberId);
        Assert.DoesNotContain(plan.Resources, r => r.Source.UniqueId == 12);               // Work sin asignaciones: sin miembro
    }

    // 6
    [Fact]
    public void Los_recursos_Material_se_omiten_con_sus_asignaciones()
    {
        var plan = Plan([Assignment(1, 2, 10), Assignment(2, 2, 20), Assignment(3, 3, 20)]);
        Assert.DoesNotContain(plan.Resources, r => r.Source.UniqueId == 20);
        var material = Assert.Single(plan.SkippedMaterials);
        Assert.Equal(20, material.Resource.UniqueId);
        Assert.Equal([2, 3], material.Assignments.Select(a => a.UniqueId));
        Assert.Single(plan.Assignments);
        Assert.Equal(2, plan.SkippedAssignments.Count(s => s.Reason.Contains("Material")));
    }

    // 7
    [Fact]
    public void Las_asignaciones_sin_recurso_se_omiten_sin_crear_un_miembro_ficticio()
    {
        var plan = Plan([Assignment(1, 2, 10), Assignment(2, 3, null), Assignment(3, 4, 0, resourceMissing: true)]);
        Assert.Single(plan.Assignments);
        Assert.Equal(2, plan.SkippedAssignments.Count(s => s.Reason == "asignación sin recurso"));
        Assert.Single(plan.Resources);                                                    // no hay miembro "Unassigned"
    }

    // 8 y 9
    [Fact]
    public void Un_recurso_en_varias_tareas_es_un_miembro_y_dos_recursos_en_una_tarea_son_dos_miembros()
    {
        var one = Plan([Assignment(1, 2, 10), Assignment(2, 3, 10), Assignment(3, 4, 10)]);
        Assert.Single(one.Resources);
        Assert.Equal(3, one.Resources[0].AssignmentCount);
        Assert.Equal(3, one.Assignments.Count);
        Assert.Equal(3, one.Assignments.Select(a => a.AssignmentId).Distinct().Count());                    // una asignación independiente por tarea
        Assert.Single(one.Assignments.Select(a => a.Resource.ProjectTeamMemberId).Distinct());               // todas con el mismo miembro

        var two = Plan([Assignment(1, 2, 10), Assignment(2, 2, 11)]);                                       // los dos en la tarea 2
        Assert.Equal(2, two.Resources.Count);
        Assert.NotEqual(two.Resources[0].ProjectTeamMemberId, two.Resources[1].ProjectTeamMemberId);
        Assert.Equal(2, two.Assignments.Count);
        Assert.All(two.Assignments, a => Assert.Equal(2, a.Task.Source.UniqueId));
    }

    [Fact]
    public void La_identidad_es_el_GUID_del_recurso_del_MPP_y_el_miembro_depende_del_proyecto()
    {
        var plan = Plan([Assignment(1, 2, 10)]);
        var guid = Guid.Parse(ResourceGuid(10));
        var resource = plan.Resources[0];
        Assert.Equal(guid.ToString("D"), resource.Key);
        Assert.Equal(ResourceImportPlanner.ProjectTeamMemberId(ProjectId, guid.ToString("D")), resource.ProjectTeamMemberId);
        Assert.NotEqual(guid, resource.ProjectTeamMemberId);                                                 // nunca el GUID del MPP directamente
        Assert.NotEqual(resource.ProjectTeamMemberId, ResourceImportPlanner.ProjectTeamMemberId(Guid.NewGuid(), guid.ToString("D")));
        Assert.Equal(resource.ProjectTeamMemberId, Plan([Assignment(9, 4, 10)]).Resources[0].ProjectTeamMemberId);   // estable
    }

    [Fact]
    public void Sin_GUID_la_identidad_es_el_UniqueId_del_recurso()
    {
        var resources = new object[] { new { Id = 1, UniqueId = 10, Guid = (string?)null, Name = "Sin guid", Type = "Work", IsNull = false } };
        var plan = Plan([Assignment(1, 2, 10)], resources);
        var resource = Assert.Single(plan.Resources);
        Assert.Equal("uid:10", resource.Key);
        Assert.Equal(ResourceImportPlanner.ProjectTeamMemberId(ProjectId, "uid:10"), resource.ProjectTeamMemberId);
    }

    [Fact]
    public void La_tarea_se_resuelve_por_su_UniqueId_y_el_recurso_por_su_UniqueId_no_por_el_nombre()
    {
        // El nombre de la asignación no coincide con el del recurso: manda ResourceUniqueId.
        var plan = Plan([Assignment(1, 3, 11, resourceName: "NOMBRE ANTIGUO")]);
        var assignment = Assert.Single(plan.Assignments);
        Assert.Equal(3, assignment.Task.Source.UniqueId);
        Assert.Equal("B", assignment.Task.Source.Name);
        Assert.Equal(11, assignment.Resource.Source.UniqueId);
        Assert.Equal("Ingeniero B", assignment.Resource.Source.Name);
    }

    [Fact]
    public void Las_asignaciones_a_tareas_resumen_o_no_seleccionadas_se_omiten()
    {
        var onSummary = Plan([Assignment(1, 1, 10)]);
        Assert.Empty(onSummary.Assignments);
        Assert.Contains("resumen", Assert.Single(onSummary.SkippedAssignments).Reason);

        var limited = Plan([Assignment(1, 2, 10), Assignment(2, 3, 11)], selection: new TaskSelection(UniqueIds: [2]));
        Assert.Single(limited.Assignments);
        Assert.Single(limited.Resources);                                                  // 11 solo se usa en una tarea no seleccionada
    }

    // 4
    [Fact]
    public void El_mapping_asigna_el_rol_configurado_con_el_orden_GUID_UniqueId_nombre()
    {
        var byGuid = new ResourceRoleMapping { MppResourceGuid = Guid.Parse(ResourceGuid(10)), ProjectOperationsRoleId = ResourceConfig.RoleManager };
        var byUid = new ResourceRoleMapping { MppResourceUniqueId = 11, ProjectOperationsRoleId = ResourceConfig.RoleArchitect };
        var byName = new ResourceRoleMapping { MppResourceName = "Ingeniero C", ProjectOperationsRoleId = ResourceConfig.RoleArchitect };
        var assignments = new object[] { Assignment(1, 2, 10), Assignment(2, 3, 11), Assignment(3, 4, 12) };
        var resources = new object[] { Resource(10, "Ingeniero A", id: 1), Resource(11, "Ingeniero B", id: 2), Resource(12, "Ingeniero C", id: 3) };

        var plan = Plan(assignments, resources, settings: ResourceConfig.Settings(byGuid, byUid, byName));

        Assert.Equal((ResourceConfig.RoleManager, ResourceRoleResolver.ByGuid), (plan.Resources[0].RoleId, plan.Resources[0].RoleMethod));
        Assert.Equal((ResourceConfig.RoleArchitect, ResourceRoleResolver.ByUniqueId), (plan.Resources[1].RoleId, plan.Resources[1].RoleMethod));
        Assert.Equal((ResourceConfig.RoleArchitect, ResourceRoleResolver.ByName), (plan.Resources[2].RoleId, plan.Resources[2].RoleMethod));

        // El GUID gana al nombre aunque el nombre apunte a otro rol.
        var conflicting = ResourceConfig.Settings(byGuid, new ResourceRoleMapping { MppResourceName = "Ingeniero A", ProjectOperationsRoleId = ResourceConfig.RoleArchitect });
        Assert.Equal(ResourceConfig.RoleManager, Plan([Assignment(1, 2, 10)], settings: conflicting).Resources[0].RoleId);
    }

    // 5
    [Fact]
    public void Sin_mapping_especifico_se_usa_DefaultResourceRoleId()
    {
        var plan = Plan([Assignment(1, 2, 10)]);
        var resource = Assert.Single(plan.Resources);
        Assert.Equal(ResourceConfig.DefaultRole, resource.RoleId);
        Assert.Equal(ResourceRoleResolver.ByDefault, resource.RoleMethod);
    }

    [Fact]
    public void Sin_rol_resoluble_el_recurso_y_sus_asignaciones_no_se_crean()
    {
        var settings = ResourceConfig.Settings();
        settings.DefaultResourceRoleId = null;
        var plan = Plan([Assignment(1, 2, 10), Assignment(2, 3, 10), Assignment(3, 4, 11)], settings: settings);
        Assert.Empty(plan.Resources);
        Assert.Empty(plan.Assignments);
        Assert.Equal([10, 11], plan.Unresolved.Select(u => u.Source.UniqueId).Order());
        Assert.Equal(3, plan.SkippedAssignments.Count(s => s.Reason == ResourceImportPlanner.NoRoleReason));
    }

    [Fact]
    public void Un_nombre_con_roles_distintos_en_el_mapping_es_ambiguo_y_no_se_usa_el_predeterminado()
    {
        var settings = ResourceConfig.Settings(
            new ResourceRoleMapping { MppResourceName = "Ingeniero A", ProjectOperationsRoleId = ResourceConfig.RoleManager },
            new ResourceRoleMapping { MppResourceName = "Ingeniero A", ProjectOperationsRoleId = ResourceConfig.RoleArchitect });
        var plan = Plan([Assignment(1, 2, 10)], settings: settings);
        Assert.Empty(plan.Resources);
        Assert.Contains("distintos", Assert.Single(plan.Unresolved).Error);
    }

    // 3
    [Fact]
    public void El_nombre_del_recurso_del_MPP_es_el_nombre_de_puesto_del_miembro_generico_con_el_recurso_comun_el_rol_y_la_unidad()
    {
        var plan = Plan([Assignment(1, 2, 10)]);
        var settings = ResourceConfig.Settings();
        var resource = plan.Resources[0];

        var team = ResourceEntityBuilder.TeamMember(resource, ProjectId, settings);
        Assert.Equal("Ingeniero A", team["msdyn_name"]!.GetValue<string>());                              // msdyn_name = "Nombre de puesto"
        Assert.Equal($"/bookableresources({ResourceConfig.Generic:D})", team["msdyn_bookableresourceid@odata.bind"]!.GetValue<string>());
        Assert.Equal($"/bookableresourcecategories({ResourceConfig.DefaultRole:D})", team["msdyn_resourcecategory@odata.bind"]!.GetValue<string>());
        Assert.Equal($"/msdyn_organizationalunits({ResourceConfig.Unit:D})", team["msdyn_organizationalunit@odata.bind"]!.GetValue<string>());
        Assert.Equal($"/msdyn_projects({ProjectId:D})", team["msdyn_project@odata.bind"]!.GetValue<string>());
        Assert.Equal(resource.ProjectTeamMemberId.ToString("D"), team["msdyn_projectteamid"]!.GetValue<string>());
        foreach (var forbidden in new[] { "msdyn_hours", "msdyn_effort", "msdyn_percentage", "msdyn_start", "msdyn_finish", "msdyn_billingtype", "msdyn_calendarid" })
            Assert.False(team.ContainsKey(forbidden), forbidden);
        Assert.Equal(7, team.Count);

        var assignment = ResourceEntityBuilder.Assignment(plan.Assignments[0], ProjectId, resource.ProjectTeamMemberId);
        Assert.Equal(
            ["@odata.type", "msdyn_name", "msdyn_projectid@odata.bind", "msdyn_projectteamid@odata.bind", "msdyn_resourceassignmentid", "msdyn_taskid@odata.bind"],
            assignment.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var forbidden in new[] { "msdyn_effort", "msdyn_effortcompleted", "msdyn_effortremaining", "msdyn_plannedwork", "msdyn_cost", "msdyn_units", "msdyn_resourceunits" })
            Assert.False(assignment.ContainsKey(forbidden), forbidden);
    }

    // 12 (parte estructural)
    [Fact]
    public void El_servicio_no_expone_ninguna_operacion_para_crear_recursos_reservables()
    {
        foreach (var type in new[] { typeof(IProjectResourceService), typeof(DataverseProjectResourceService) })
            Assert.DoesNotContain(type.GetMethods().Select(m => m.Name), n => n.Contains("Bookable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ResourceEntityBuilder).GetMethods().Select(m => m.Name), n => n.Contains("Bookable", StringComparison.OrdinalIgnoreCase));
    }
}

public class ProjectActualStartTests
{
    private static async Task<(string Output, FakeProjects Projects)> RunAsync(string json, FakeProjects? projects = null, string? statePath = null, bool importProgress = false)
    {
        var parsed = MppProjectReader.Parse(json);
        var normalized = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(parsed));
        var templates = new FakeTemplates();
        templates.Items.Add(new CalendarTemplateRecord(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "Plantilla"));      // GUID fijo: reanudar exige la misma plantilla
        projects ??= new FakeProjects();
        var settings = new CalendarImportSettings { CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = normalized.Fingerprint, Name = "Plantilla" }], ImportProgress = importProgress };
        var writer = new StringWriter();
        var orchestrator = new ProjectImportOrchestrator(settings, templates, projects, new FakeCalendars { Pattern = normalized.CanonicalPattern }, new ImportStateService(), writer, (_, _) => Task.CompletedTask);
        statePath ??= Path.Combine(Files.TempDir(), "state.json");
        await orchestrator.RunAsync(new CalendarImportRequest("x.json", "x.schema.json", statePath, new string('A', 64), parsed), CancellationToken.None);
        return (writer.ToString(), projects);
    }

    private static string Json(bool withSummaryTask) => Build([Calendar(1, "TBT", Tbt())],
        tasks: withSummaryTask
            ? [TaskRow(0, "PROYECTO", 0, 0, "2026-09-28T09:00:00", "2031-02-10T13:00:00", 1004, summary: true, projectSummary: true),
               TaskRow(1, "Primera", 1, 0, "2026-11-16T08:00:00", "2026-11-17T17:00:00")]
            : [TaskRow(1, "Primera", 1, 0, "2026-11-16T08:00:00", "2026-11-17T17:00:00")],
        start: "2026-10-05T09:00:00");

    [Fact]
    public async Task El_comienzo_de_la_primera_fila_del_MPP_se_guarda_en_msdyn_actualstart_del_proyecto_y_no_el_StartDate()
    {
        var (output, projects) = await RunAsync(Json(withSummaryTask: true));

        var write = Assert.Single(projects.ActualStartWrites);
        Assert.Equal(new DateTime(2026, 9, 28, 9, 0, 0), write.Start);                      // Comienzo de la tarea 0, no el StartDate del proyecto (05/10)
        Assert.Contains("Actual start", output);
        Assert.Contains("msdyn_actualstart: SET", output);
    }

    [Fact]
    public async Task Reanudar_sobre_un_proyecto_existente_no_reescribe_msdyn_actualstart_si_ya_coincide()
    {
        var projects = new FakeProjects();
        var statePath = Path.Combine(Files.TempDir(), "state.json");
        await RunAsync(Json(true), projects, statePath);

        var (output, _) = await RunAsync(Json(true), projects, statePath);

        Assert.Single(projects.ActualStartWrites);                                          // sigue siendo una sola escritura
        Assert.Contains("REUSED (already equal)", output);
    }

    [Fact]
    public async Task Un_proyecto_ya_existente_con_otro_msdyn_actualstart_se_actualiza()
    {
        var projects = new FakeProjects();
        var statePath = Path.Combine(Files.TempDir(), "state.json");
        await RunAsync(Json(true), projects, statePath);
        projects.ActualStarts[projects.Existing.Single()] = new DateTime(2026, 10, 5, 9, 0, 0);   // alguien lo cambió

        var (output, _) = await RunAsync(Json(true), projects, statePath);

        Assert.Equal(2, projects.ActualStartWrites.Count);
        Assert.Equal(new DateTime(2026, 9, 28, 9, 0, 0), projects.ActualStarts[projects.Existing.Single()]);
        Assert.Contains("SET (before 2026-10-05 09:00)", output);
    }

    [Fact]
    public async Task Con_importacion_de_avance_el_proyecto_recien_creado_pasa_msdyn_allowpercentcompleteupdate_a_true()
    {
        var (output, projects) = await RunAsync(Json(true), importProgress: true);

        var write = Assert.Single(projects.AllowPercentWrites);                             // el proyecto nuevo nace con false: se activa
        Assert.True(projects.AllowPercentUpdate[write]);
        Assert.Contains("msdyn_allowpercentcompleteupdate = true: SET (before false)", output);
    }

    [Fact]
    public async Task Reanudar_con_avance_no_reescribe_el_ajuste_si_ya_esta_en_true_y_sin_avance_no_se_toca()
    {
        var projects = new FakeProjects();
        var statePath = Path.Combine(Files.TempDir(), "state.json");
        await RunAsync(Json(true), projects, statePath, importProgress: true);

        var (output, _) = await RunAsync(Json(true), projects, statePath, importProgress: true);
        Assert.Single(projects.AllowPercentWrites);                                         // sin nueva escritura
        Assert.Contains("REUSED (already true)", output);

        var untouched = new FakeProjects();
        await RunAsync(Json(true), untouched, importProgress: false);
        Assert.Empty(untouched.AllowPercentWrites);                                         // sin --progress el ajuste del proyecto no se modifica
    }

    [Fact]
    public async Task Sin_tarea_resumen_del_proyecto_no_se_escribe_y_se_avisa()
    {
        var (output, projects) = await RunAsync(Json(withSummaryTask: false));

        Assert.Empty(projects.ActualStartWrites);
        Assert.Contains("msdyn_actualstart not set", output);
    }
}

public class ResourceImporterTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static object[] Assignments() =>
    [
        Assignment(1, 2, 10), Assignment(2, 3, 10), Assignment(3, 4, 11),
        Assignment(4, 2, 20),                   // Material
        Assignment(5, 3, null)                  // sin recurso
    ];

    private static async Task<(FakeTasks Tasks, MppProject Project)> TasksCreatedAsync(object[]? assignments = null)
    {
        var project = ResourcePlannerTests.Parse(assignments ?? Assignments());
        var tasks = new FakeTasks();
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        await tasks.CreateTasksAsync(ProjectId, plan.Tasks.Select(t => TaskEntityBuilder.Build(t, ProjectId, tasks.Bucket)).ToList(), CancellationToken.None);
        return (tasks, project);
    }

    private static ProjectResourceImporter NewImporter(FakeResources resources, FakeTasks tasks, StringWriter writer, CalendarImportSettings? settings = null)
        => new(resources, tasks, settings ?? ResourceConfig.Settings(), writer, (_, _) => Task.CompletedTask);

    // 1, 2, 3, 6, 7, 8 y 12 con el flujo completo
    [Fact]
    public async Task Crea_un_miembro_generico_por_recurso_usado_sus_asignaciones_y_omite_material_y_sin_recurso_sin_crear_recursos_reservables()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        var state = new ImportState();
        var writer = new StringWriter();

        var summary = await NewImporter(resources, tasks, writer).RunAsync(ProjectId, project, state, () => { }, CancellationToken.None);

        Assert.Equal(2, summary.WorkResourcesUsed);                                       // 10 y 11; el 12 (Work sin uso) no
        Assert.Equal(2, resources.TeamCreates.Count);
        Assert.Equal(["Ingeniero A", "Ingeniero B"], resources.TeamCreates.Select(t => t.Entity["msdyn_name"]!.GetValue<string>()).Order());
        Assert.All(resources.TeamCreates, t => Assert.Equal($"/bookableresources({ResourceConfig.Generic:D})", t.Entity["msdyn_bookableresourceid@odata.bind"]!.GetValue<string>()));
        Assert.Equal(3, summary.AssignmentsCreated);                                      // el recurso 10 en dos tareas + el 11
        Assert.Equal(2, summary.AssignmentsSkipped);
        Assert.Equal(1, summary.MaterialSkipped);
        Assert.Equal(3, resources.AssignmentEntities.Count);
        Assert.Equal(2, resources.AssignmentEntities.Select(e => e["msdyn_projectteamid@odata.bind"]!.GetValue<string>()).Distinct().Count());
        // El state conserva la identidad de origen y los ids de Project Operations.
        Assert.Equal([10, 11], state.Resources!.Keys.OrderBy(k => k));
        var r10 = state.Resources[10];
        Assert.Equal(Guid.Parse(ResourceGuid(10)), r10.MppResourceGuid);
        Assert.Equal("Ingeniero A", r10.ResourceName);
        Assert.Equal(ProjectId, r10.ProjectId);
        Assert.Equal(ResourceConfig.Generic, r10.GenericBookableResourceId);
        Assert.Equal(ResourceConfig.DefaultRole, r10.RoleId);
        Assert.Equal(ResourceConfig.Unit, r10.ResourcingUnitId);
        Assert.Equal(resources.Team.Keys.Single(k => k == r10.ProjectTeamMemberId), r10.ProjectTeamMemberId);
        Assert.Equal(3, state.CreatedAssignments.Count);
        // El log tiene los bloques pedidos.
        var text = writer.ToString();
        foreach (var label in new[] { "[Team member]", "[Assignment]", "MPP UniqueID", "MPP GUID", "MPP Resource Name", "Position name", "Role resolution", "Resourcing unit", "ProjectTeamMemberId", "ResourceAssignmentId", "[Task planning]", "Before assignment", "After assignment", "[Role report]" })
            Assert.Contains(label, text);
        Assert.Contains("default role (DefaultResourceRoleId)", text);
        Assert.Contains("Material resource skipped: UniqueID 20", text);
        Assert.Contains("has no resource; skipped", text);
        Assert.Contains("Skipped (Material resources are not imported yet)", text);
        Assert.Contains("Bookable resources    : 0 created", text);
    }

    // 10 y 11
    [Fact]
    public async Task Reanudar_no_duplica_miembros_del_equipo_ni_asignaciones()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        var state = new ImportState();
        await NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, state, () => { }, CancellationToken.None);

        var second = await NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, state, () => { }, CancellationToken.None);

        Assert.Equal(2, resources.TeamCreates.Count);                                      // sin duplicar miembros del equipo
        Assert.Equal(3, resources.AssignmentEntities.Count);                               // sin duplicar asignaciones
        Assert.Equal(0, second.TeamCreated);
        Assert.Equal(2, second.TeamReused);
        Assert.Equal(0, second.AssignmentsCreated);
        Assert.Equal(3, second.AssignmentsReused);
    }

    [Fact]
    public async Task Reanudar_sin_state_reutiliza_los_miembros_ya_existentes_en_Dataverse()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        await NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        var second = await NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);   // state nuevo

        Assert.Equal(2, resources.TeamCreates.Count);
        Assert.Equal(2, second.TeamReused);
        Assert.Equal(3, resources.AssignmentEntities.Count);
    }

    [Fact]
    public async Task Un_miembro_existente_que_apunta_a_otro_recurso_reservable_es_un_error_y_no_se_modifica()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        var planned = ResourcePlannerTests.Plan(Assignments()).Resources[0];
        resources.Team[planned.ProjectTeamMemberId] = new TeamMemberSnapshot(planned.ProjectTeamMemberId, "x", Guid.NewGuid(), planned.RoleId, ResourceConfig.Unit);

        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None));

        Assert.Equal(ImportExitCodes.Resources, ex.ExitCode);
        Assert.Contains("no al genérico configurado", ex.Message);
        Assert.Empty(resources.AssignmentEntities);
    }

    [Fact]
    public async Task Los_roles_mapeados_se_envian_en_el_miembro_y_se_informa_del_metodo_de_resolucion()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var settings = ResourceConfig.Settings(new ResourceRoleMapping { MppResourceGuid = Guid.Parse(ResourceGuid(10)), ProjectOperationsRoleId = ResourceConfig.RoleManager });
        var resources = new FakeResources(settings);
        var writer = new StringWriter();

        await NewImporter(resources, tasks, writer, settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        var a = resources.TeamCreates.Single(t => t.Resource.Source.UniqueId == 10).Entity;
        var b = resources.TeamCreates.Single(t => t.Resource.Source.UniqueId == 11).Entity;
        Assert.Equal($"/bookableresourcecategories({ResourceConfig.RoleManager:D})", a["msdyn_resourcecategory@odata.bind"]!.GetValue<string>());
        Assert.Equal($"/bookableresourcecategories({ResourceConfig.DefaultRole:D})", b["msdyn_resourcecategory@odata.bind"]!.GetValue<string>());
        var text = writer.ToString();
        Assert.Contains("mapping by MPP Resource GUID", text);
        Assert.Contains("default role (DefaultResourceRoleId)", text);
        Assert.Contains("Using default role", text);
    }

    [Fact]
    public async Task El_informe_detecta_roles_compartidos_y_tarifas_distintas_bajo_el_mismo_rol()
    {
        var project = ResourcePlannerTests.Parse(
            [Assignment(1, 2, 10), Assignment(2, 3, 11)],
            [
                new { Id = 1, UniqueId = 10, Guid = ResourceGuid(10), Name = "Ingeniero A", Type = "Work", IsNull = false, StandardRate = new { Amount = 50m, Units = "Hours" } },
                new { Id = 2, UniqueId = 11, Guid = ResourceGuid(11), Name = "Ingeniero B", Type = "Work", IsNull = false, StandardRate = new { Amount = 80m, Units = "Hours" } }
            ]);
        var tasks = new FakeTasks();
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        await tasks.CreateTasksAsync(ProjectId, plan.Tasks.Select(t => TaskEntityBuilder.Build(t, ProjectId, tasks.Bucket)).ToList(), CancellationToken.None);
        var writer = new StringWriter();

        await NewImporter(new FakeResources(), tasks, writer).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        var text = writer.ToString();
        Assert.Contains("Shared role", text);
        Assert.Contains("is used by 2 MPP resources", text);
        Assert.Contains("Different MPP rates", text);
        Assert.Contains("50/Hours (Ingeniero A)", text);
        Assert.Contains("80/Hours (Ingeniero B)", text);
        Assert.Contains("rates are NOT imported", text);
    }

    [Fact]
    public async Task Sin_rol_resoluble_se_crea_el_resto_y_la_importacion_termina_con_error_11()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var settings = ResourceConfig.Settings(new ResourceRoleMapping { MppResourceGuid = Guid.Parse(ResourceGuid(10)), ProjectOperationsRoleId = ResourceConfig.RoleManager });
        settings.DefaultResourceRoleId = null;                                            // el recurso 11 se queda sin rol
        var resources = new FakeResources(settings);
        var writer = new StringWriter();

        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => NewImporter(resources, tasks, writer, settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None));

        Assert.Equal(ImportExitCodes.Resources, ex.ExitCode);
        Assert.Contains("'Ingeniero B'", ex.Message);
        var member = Assert.Single(resources.TeamCreates);                                // solo el 10
        Assert.Equal(10, member.Resource.Source.UniqueId);
        Assert.Equal(2, resources.AssignmentEntities.Count);                              // las dos tareas del 10; ninguna del 11
        Assert.Contains("team member and assignments not created", writer.ToString());
    }

    [Fact]
    public async Task La_configuracion_se_valida_al_inicio_y_un_registro_inexistente_detiene_la_fase_sin_crear_nada()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        resources.Missing.Add(ResourceConfig.DefaultRole);

        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None));

        Assert.Equal(ImportExitCodes.Resources, ex.ExitCode);
        Assert.Empty(resources.TeamCreates);
        Assert.Empty(resources.AssignmentEntities);
        Assert.Contains(ResourceConfig.DefaultRole, resources.ValidatedRoleIds.Single());  // el rol predeterminado se validó
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("unit")]
    public async Task Sin_recurso_generico_o_unidad_configurados_la_fase_falla_con_un_mensaje_claro(string missing)
    {
        var (tasks, project) = await TasksCreatedAsync();
        var settings = ResourceConfig.Settings();
        if (missing == "generic") settings.GenericBookableResourceId = null; else settings.DefaultResourcingUnitId = null;

        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => NewImporter(new FakeResources(ResourceConfig.Settings()), tasks, new StringWriter(), settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None));

        Assert.Equal(ImportExitCodes.Resources, ex.ExitCode);
        Assert.Contains(missing == "generic" ? "GenericBookableResourceId" : "DefaultResourcingUnitId", ex.Message);
    }

    [Fact]
    public async Task Las_asignaciones_se_enlazan_con_la_tarea_por_UniqueId_y_con_el_miembro_del_recurso_por_UniqueId()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        var state = new ImportState();
        await NewImporter(resources, tasks, new StringWriter()).RunAsync(ProjectId, project, state, () => { }, CancellationToken.None);

        var taskPlan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var taskB = taskPlan.Tasks.Single(t => t.Source.UniqueId == 3);
        var forTaskB = resources.AssignmentEntities.Single(e => e["msdyn_taskid@odata.bind"]!.GetValue<string>() == $"/msdyn_projecttasks({taskB.TaskId:D})");
        Assert.Equal($"/msdyn_projectteams({state.Resources![10].ProjectTeamMemberId:D})", forTaskB["msdyn_projectteamid@odata.bind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Informa_de_la_planificacion_de_las_tareas_afectadas_antes_y_despues_sin_corregirla()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var resources = new FakeResources();
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var taskA = plan.Tasks.Single(t => t.Source.UniqueId == 2);
        tasks.Overrides[taskA.TaskId] = new TaskSnapshot(taskA.TaskId, "A", new DateTime(2026, 11, 16, 9, 0, 0), new DateTime(2026, 11, 16, 17, 0, 0), 1m, 8m);
        // Tras crear las asignaciones, Project Operations cambia el esfuerzo de la tarea A.
        resources.OnCreateAssignments = () => tasks.Overrides[taskA.TaskId] = new TaskSnapshot(taskA.TaskId, "A", new DateTime(2026, 11, 16, 9, 0, 0), new DateTime(2026, 11, 16, 17, 0, 0), 1m, 16m);
        var writer = new StringWriter();

        var summary = await NewImporter(resources, tasks, writer).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        Assert.Equal(3, summary.TasksAffected);
        Assert.Equal(1, summary.TasksChanged);
        var text = writer.ToString();
        Assert.Contains("Effort 8 h", text);
        Assert.Contains("Effort 16 h", text);
        Assert.Contains("CHANGED Effort", text);
    }

    private static CalendarImportSettings WorkSettings(bool importWork = true) { var s = ResourceConfig.Settings(); s.ImportWork = importWork; return s; }

    [Fact]
    public async Task El_esfuerzo_de_la_tarea_es_la_suma_del_Work_de_sus_asignaciones_y_no_se_repite_al_reanudar()
    {
        var (tasks, project) = await TasksCreatedAsync([Assignment(1, 2, 10, work: 1m), Assignment(2, 2, 11, work: 2m), Assignment(3, 3, 10, work: 4m)]);
        var settings = WorkSettings();
        var resources = new FakeResources(settings);
        resources.OnEffortUpdate = (id, hours) => tasks.Overrides[id] = new TaskSnapshot(id, "x", null, null, 1m, hours);   // la plataforma refleja el esfuerzo
        var taskPlan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var task2 = taskPlan.Tasks.Single(t => t.Source.UniqueId == 2).TaskId;
        var task3 = taskPlan.Tasks.Single(t => t.Source.UniqueId == 3).TaskId;
        var writer = new StringWriter();

        var first = await NewImporter(resources, tasks, writer, settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        Assert.Equal(2, first.EffortUpdated);
        Assert.Contains((task2, 3m), resources.EffortUpdates);                              // 1 h + 2 h
        Assert.Contains((task3, 4m), resources.EffortUpdates);
        Assert.Contains("[Task work]", writer.ToString());
        Assert.Contains("MPP Work (sum)", writer.ToString());

        var second = await NewImporter(resources, tasks, new StringWriter(), settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(2, resources.EffortUpdates.Count);                                     // sin nuevas escrituras
        Assert.Equal(0, second.EffortUpdated);
        Assert.Equal(2, second.EffortAlreadySet);
    }

    [Fact]
    public async Task Una_tarea_con_una_asignacion_sin_Work_en_horas_se_omite_y_no_se_inventa_un_valor()
    {
        var (tasks, project) = await TasksCreatedAsync([Assignment(1, 2, 10, work: 1m), Assignment(2, 2, 11), Assignment(3, 3, 10, work: 2m)]);
        var settings = WorkSettings();
        var resources = new FakeResources(settings);
        var writer = new StringWriter();

        var summary = await NewImporter(resources, tasks, writer, settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        Assert.Equal(1, summary.EffortSkipped);
        var update = Assert.Single(resources.EffortUpdates);
        Assert.Equal(2m, update.Hours);
        Assert.Contains("alguna asignación no tiene Work en horas", writer.ToString());
    }

    [Fact]
    public async Task Sin_ImportWork_no_se_envia_ningun_esfuerzo()
    {
        var (tasks, project) = await TasksCreatedAsync([Assignment(1, 2, 10, work: 1m)]);
        var resources = new FakeResources();

        await NewImporter(resources, tasks, new StringWriter(), WorkSettings(importWork: false)).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        Assert.Empty(resources.EffortUpdates);
    }

    private static CalendarImportSettings ProgressSettings(bool importProgress = true) { var s = ResourceConfig.Settings(); s.ImportWork = true; s.ImportProgress = importProgress; return s; }

    private static async Task<(FakeTasks Tasks, MppProject Project)> ProgressProjectAsync()
    {
        var project = MppProjectReader.Parse(Build([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))],
            tasks:
            [
                TaskRow(1, "Fase", 1, 0, summary: true, percent: 80),                                       // resumen: lo calcula la plataforma
                TaskRow(2, "Mitad", 2, 1, "2026-11-16T09:00:00", "2026-11-17T09:00:00", 1, percent: 50),
                TaskRow(3, "Completa", 2, 1, "2026-11-18T09:00:00", "2026-11-19T09:00:00", 1, percent: 100),
                TaskRow(4, "Sin avance", 2, 1, "2026-11-20T09:00:00", "2026-11-21T09:00:00", 1, percent: 0),
                TaskRow(5, "Hito", 2, 1, "2026-11-22T09:00:00", "2026-11-22T09:00:00", 0, milestone: true, percent: 100),
                TaskRow(6, "Sin esfuerzo", 2, 1, "2026-11-23T09:00:00", "2026-11-24T09:00:00", 1, percent: 100)
            ]));
        var tasks = new FakeTasks();
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        await tasks.CreateTasksAsync(ProjectId, plan.Tasks.Select(t => TaskEntityBuilder.Build(t, ProjectId, tasks.Bucket)).ToList(), CancellationToken.None);
        // Las tareas 2, 3 y 4 tienen esfuerzo en Project Operations; la 6 no (esfuerzo 0).
        foreach (var uid in new[] { 2, 3, 4 })
        {
            var id = plan.Tasks.Single(t => t.Source.UniqueId == uid).TaskId;
            tasks.Overrides[id] = new TaskSnapshot(id, "x", null, null, 1m, 8m);
        }
        var noEffort = plan.Tasks.Single(t => t.Source.UniqueId == 6).TaskId;
        tasks.Overrides[noEffort] = new TaskSnapshot(noEffort, "x", null, null, 1m, 0m);
        return (tasks, project);
    }

    [Fact]
    public async Task El_avance_del_MPP_se_envia_a_las_tareas_hoja_con_avance_y_no_se_repite_al_reanudar()
    {
        var (tasks, project) = await ProgressProjectAsync();
        var settings = ProgressSettings();
        var resources = new FakeResources(settings);
        resources.OnProgressUpdate = (id, percent) => tasks.Overrides[id] = new TaskSnapshot(id, "x", null, null, 1m, 8m, percent, 8m * percent / 100m, 8m * (100m - percent) / 100m);   // esfuerzo 8 h, como en Project Operations
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var half = plan.Tasks.Single(t => t.Source.UniqueId == 2).TaskId;
        var full = plan.Tasks.Single(t => t.Source.UniqueId == 3).TaskId;
        var writer = new StringWriter();

        var first = await NewImporter(resources, tasks, writer, settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        Assert.Equal(2, first.ProgressUpdated);                                             // la 2 (50 %) y la 3 (100 %); no el resumen, ni el 0 %, ni el hito, ni la de esfuerzo 0
        Assert.Equal(2, resources.ProgressUpdates.Count);
        Assert.Contains((half, 50m), resources.ProgressUpdates);
        Assert.Contains((full, 100m), resources.ProgressUpdates);
        Assert.Equal(2, first.ProgressSkipped);                                             // el hito y la tarea sin esfuerzo
        var text = writer.ToString();
        Assert.Contains("[Task progress]", text);
        Assert.Contains("Skipped (hito", text);
        Assert.Contains("tarea sin esfuerzo", text);

        var second = await NewImporter(resources, tasks, new StringWriter(), settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(2, resources.ProgressUpdates.Count);                                   // sin nuevas escrituras
        Assert.Equal(0, second.ProgressUpdated);
        Assert.Equal(2, second.ProgressAlreadySet);
    }

    [Fact]
    public async Task Sin_ImportProgress_no_se_envia_ningun_avance()
    {
        var (tasks, project) = await ProgressProjectAsync();
        var settings = ProgressSettings(importProgress: false);
        var resources = new FakeResources(settings);

        await NewImporter(resources, tasks, new StringWriter(), settings).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);

        Assert.Empty(resources.ProgressUpdates);
    }

    [Fact]
    public void La_actualizacion_de_avance_envia_una_fraccion_y_nada_mas()
    {
        var entity = ResourceEntityBuilder.TaskProgress(Guid.NewGuid(), 100m);
        Assert.Equal(1m, entity["msdyn_progress"]!.GetValue<decimal>());                    // 100 % = 1 (verificado: la API rechaza 100)
        Assert.Equal(0.5m, ResourceEntityBuilder.TaskProgress(Guid.NewGuid(), 50m)["msdyn_progress"]!.GetValue<decimal>());
        Assert.Equal(["@odata.type", "msdyn_progress", "msdyn_projecttaskid"], entity.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var forbidden in new[] { "msdyn_effortcompleted", "msdyn_effortremaining", "msdyn_effort", "msdyn_duration", "msdyn_start", "msdyn_finish" })
            Assert.False(entity.ContainsKey(forbidden), forbidden);
    }

    [Fact]
    public void La_actualizacion_de_tarea_solo_lleva_el_esfuerzo()
    {
        var entity = ResourceEntityBuilder.TaskEffort(Guid.NewGuid(), 3m);
        Assert.Equal(["@odata.type", "msdyn_effort", "msdyn_projecttaskid"], entity.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var forbidden in new[] { "msdyn_effortcompleted", "msdyn_effortremaining", "msdyn_progress", "msdyn_duration", "msdyn_start", "msdyn_finish" })
            Assert.False(entity.ContainsKey(forbidden), forbidden);
    }

    [Fact]
    public async Task Las_asignaciones_de_tareas_que_no_existen_en_Dataverse_se_omiten_y_no_se_crean_miembros()
    {
        var project = ResourcePlannerTests.Parse(Assignments());
        var resources = new FakeResources();
        var summary = await NewImporter(resources, new FakeTasks(), new StringWriter()).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(0, summary.AssignmentsCreated);
        Assert.Empty(resources.TeamCreates);                                               // sin asignaciones utilizables no se crea ningún miembro
    }

    [Fact]
    public async Task El_orquestador_ejecuta_los_recursos_despues_de_las_dependencias_y_termina_ahi()
    {
        var json = Build([Calendar(1, "TBT", Tbt())],
            tasks:
            [
                TaskRow(1, "A", 1, 0, "2026-11-16T08:00:00", "2026-11-16T17:00:00"),
                TaskRow(2, "B", 1, 0, "2026-11-17T08:00:00", "2026-11-17T17:00:00")
            ],
            dependencies: [Dependency(1, 1, 2)],
            resources: [Resource(10, "Ingeniero A")],
            resourceAssignments: [Assignment(1, 2, 10)]);
        var parsed = MppProjectReader.Parse(json);
        var normalized = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(parsed));
        var templates = new FakeTemplates();
        templates.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), "Plantilla"));
        var calendars = new FakeCalendars { Pattern = normalized.CanonicalPattern };
        var tasks = new FakeTasks();
        var deps = new FakeDependencies();
        var resources = new FakeResources();
        var settings = ResourceConfig.Settings();
        settings.ImportTasks = true; settings.ImportDependencies = true;
        settings.CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = normalized.Fingerprint, Name = "Plantilla" }];
        var writer = new StringWriter();
        var orchestrator = new ProjectImportOrchestrator(settings, templates, new FakeProjects(), calendars, new ImportStateService(), writer, (_, _) => Task.CompletedTask, tasks, deps, resources);
        var statePath = Path.Combine(Files.TempDir(), "state.json");
        await orchestrator.RunAsync(new CalendarImportRequest("x.json", "x.schema.json", statePath, new string('A', 64), parsed), CancellationToken.None);

        var text = writer.ToString();
        Assert.True(text.IndexOf("[Dependencies]", StringComparison.Ordinal) < text.IndexOf("[Resources]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[Resources]", StringComparison.Ordinal) < text.IndexOf("[8/8] Result", StringComparison.Ordinal));
        Assert.Contains("Import stopped after the Resources and Assignments phase.", text);
        Assert.Single(resources.TeamCreates);
        Assert.Single(resources.AssignmentEntities);
        Assert.Equal("Resources", ImportState.Load(statePath)!.CompletedStages![^1]);
    }
}
