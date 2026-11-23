using System.Text.Json.Nodes;
using ProjectOpsExcelImporter.Calendars;
using Xunit;
using static ProjectOpsExcelImporter.Tests.Dump;

namespace ProjectOpsExcelImporter.Tests;

internal sealed class FakeDependencies : IProjectDependencyService
{
    public HashSet<Guid> Existing { get; } = [];
    public List<JsonObject> CreatedEntities { get; } = [];
    public List<int> BatchSizes { get; } = [];
    /// <summary>Se invoca al crear un lote; sirve para simular que Project Operations reprograma tareas.</summary>
    public Action? OnCreate { get; set; }
    /// <summary>Si se informa, sustituye lo que "devuelve Dataverse" para una dependencia.</summary>
    public Dictionary<Guid, DependencySnapshot> Overrides { get; } = [];
    public string LinkTypeAttribute { get; set; } = "msdyn_linktype";
    public string LagAttribute { get; set; } = "msdyn_projecttaskdependencylinklag";

    public Task<IReadOnlySet<Guid>> GetExistingDependencyIdsAsync(Guid projectId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(Existing.ToHashSet());
    public Task CreateDependenciesAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken ct)
    {
        BatchSizes.Add(entities.Count);
        foreach (var e in entities) { CreatedEntities.Add(e); Existing.Add(Guid.Parse(e["msdyn_projecttaskdependencyid"]!.GetValue<string>())); }
        OnCreate?.Invoke();
        return Task.CompletedTask;
    }
    public Task WaitForDependencyAsync(Guid dependencyId, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<DependencySnapshot>> ReadDependenciesAsync(Guid projectId, CancellationToken ct)
    {
        var list = new List<DependencySnapshot>();
        foreach (var e in CreatedEntities)
        {
            var id = Guid.Parse(e["msdyn_projecttaskdependencyid"]!.GetValue<string>());
            if (Overrides.TryGetValue(id, out var o)) { list.Add(o); continue; }
            Guid Task(string key) => Guid.Parse(e[key]!.GetValue<string>().Split('(', ')')[1]);
            list.Add(new DependencySnapshot(id, Task("msdyn_PredecessorTask@odata.bind"), Task("msdyn_SuccessorTask@odata.bind"),
                e[LinkTypeAttribute]!.GetValue<int>(), e[LagAttribute] is { } l ? l.GetValue<long>() : null));
        }
        return Task.FromResult<IReadOnlyList<DependencySnapshot>>(list);
    }
}

public class DependencyPlannerTests
{
    private static readonly TaskDurationRules Rules = new();
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static MppProject Parse(object[] dependencies)
        => MppProjectReader.Parse(Build([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))], tasks:
        [
            TaskRow(0, "PROYECTO", 0, 0, summary: true, projectSummary: true),
            TaskRow(1, "Fase", 1, 0, summary: true),
            TaskRow(2, "A", 2, 1, "2026-11-16T09:00:00", "2026-11-16T17:00:00"),
            TaskRow(3, "B", 2, 1, "2026-11-17T09:00:00", "2026-11-17T17:00:00"),
            TaskRow(4, "C", 2, 1, "2026-11-18T09:00:00", "2026-11-18T17:00:00")
        ], dependencies: dependencies));

    private static DependencyPlan Plan(object[] dependencies, CalendarImportSettings? settings = null, TaskSelection? selection = null)
    {
        var project = Parse(dependencies);
        var taskPlan = TaskImportPlanner.Build(project, Rules, selection ?? new TaskSelection(), ProjectId);
        return DependencyImportPlanner.Build(project, taskPlan, Rules, settings ?? new CalendarImportSettings(), ProjectId);
    }

    [Theory]
    [InlineData("FinishStart", "FinishToStart", 192350000)]
    [InlineData("StartStart", "StartToStart", 192350001)]
    [InlineData("FinishFinish", "FinishToFinish", 192350002)]
    [InlineData("StartFinish", "StartToFinish", 192350004)]
    public void El_tipo_de_relacion_se_traduce_al_valor_nativo_de_Project_Operations(string mpp, string key, int value)
    {
        var plan = Plan([Dependency(1, 2, 3, mpp)]);
        var d = Assert.Single(plan.Dependencies);
        Assert.Equal(key, d.LinkTypeKey);
        Assert.Equal(value, d.LinkTypeValue);
    }

    [Theory]
    [InlineData(9, "Days", 259200)]            // 9 días x 8 h x 3600 s
    [InlineData(-1, "Months", -576000)]        // -1 mes = -20 días de trabajo (adelanto)
    [InlineData(2, "Weeks", 288000)]           // 2 semanas = 10 días
    [InlineData(4, "Hours", 14400)]
    [InlineData(0, "Days", 0)]
    public void El_retardo_se_convierte_a_segundos_con_signo(double value, string units, long seconds)
        => Assert.Equal(seconds, DependencyImportPlanner.LagSeconds(new MppDependency { LagValue = (decimal)value, LagUnits = units }, Rules));

    [Fact]
    public void Una_unidad_de_retardo_o_un_tipo_desconocidos_son_un_error_con_exit_10()
    {
        var unit = Assert.Throws<CalendarImportException>(() => Plan([Dependency(1, 2, 3, lag: 1, units: "Parsecs")]));
        Assert.Equal(ImportExitCodes.Dependencies, unit.ExitCode);
        Assert.Throws<CalendarImportException>(() => Plan([Dependency(1, 2, 3, "Raro")]));
    }

    [Fact]
    public void Las_dependencias_con_tareas_resumen_se_omiten_salvo_que_se_pida_expresamente()
    {
        var withSummary = new object[] { Dependency(1, 2, 3), Dependency(2, 1, 3) };      // la 2 parte de una tarea resumen
        var plan = Plan(withSummary);
        Assert.Single(plan.Dependencies);
        Assert.Equal("enlaza con una tarea resumen", Assert.Single(plan.Skipped).Reason);
        Assert.Equal(2, Plan(withSummary, new CalendarImportSettings { IncludeSummaryDependencies = true }).Dependencies.Count);
    }

    [Fact]
    public void Las_dependencias_con_una_tarea_no_seleccionada_se_omiten()
    {
        var plan = Plan([Dependency(1, 2, 3), Dependency(2, 3, 4)], selection: new TaskSelection(UniqueIds: [2, 3]));
        Assert.Single(plan.Dependencies);
        Assert.Contains("no está seleccionada", Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void El_GUID_es_determinista_depende_del_proyecto_y_no_se_duplican_relaciones_iguales()
    {
        var a = Plan([Dependency(1, 2, 3, lag: 1)]).Dependencies[0].DependencyId;
        Assert.Equal(a, Plan([Dependency(1, 2, 3, lag: 1)]).Dependencies[0].DependencyId);
        Assert.NotEqual(a, Plan([Dependency(1, 2, 3, lag: 2)]).Dependencies[0].DependencyId);
        Assert.Equal(DeterministicGuid.Create(ProjectOpsImporter.IdNamespace, $"{ProjectId:D}:dep:2:3:FinishToStart:28800"), a);

        var duplicated = Plan([Dependency(1, 2, 3), Dependency(2, 2, 3)]);
        Assert.Single(duplicated.Dependencies);
        Assert.Contains("duplicada", Assert.Single(duplicated.Skipped).Reason);
    }

    [Fact]
    public void Limitar_por_numero_toma_las_primeras_por_RelationUniqueId()
    {
        var plan = Plan([Dependency(3, 3, 4), Dependency(1, 2, 3), Dependency(2, 2, 4)], new CalendarImportSettings { DependencyMaxCount = 2 });
        Assert.Equal([1, 2], plan.Dependencies.Select(d => d.Source.RelationUniqueId));
    }

    [Fact]
    public void Los_atributos_enviados_son_solo_los_minimos_y_el_retardo_solo_si_no_es_cero()
    {
        var settings = new CalendarImportSettings();
        var plan = Plan([Dependency(1, 2, 3, "StartStart", 2, "Days"), Dependency(2, 3, 4)]);
        var projectId = Guid.NewGuid();

        var withLag = DependencyEntityBuilder.Build(plan.Dependencies[0], projectId, settings);
        Assert.Equal(
            ["@odata.type", "msdyn_PredecessorTask@odata.bind", "msdyn_Project@odata.bind", "msdyn_SuccessorTask@odata.bind", "msdyn_linktype",
             "msdyn_projecttaskdependencyid", "msdyn_projecttaskdependencylinklag"],
            withLag.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(192350001, withLag["msdyn_linktype"]!.GetValue<int>());
        Assert.Equal(57600L, withLag["msdyn_projecttaskdependencylinklag"]!.GetValue<long>());
        Assert.Equal($"/msdyn_projects({projectId:D})", withLag["msdyn_Project@odata.bind"]!.GetValue<string>());

        var noLag = DependencyEntityBuilder.Build(plan.Dependencies[1], projectId, settings);
        Assert.False(noLag.ContainsKey("msdyn_projecttaskdependencylinklag"));
        Assert.True(noLag.ContainsKey("msdyn_linktype"));                       // columna obligatoria para el servicio de planificación
        foreach (var forbidden in new[] { "msdyn_description", "msdyn_projecttaskdependencylinktype", "msdyn_effort", "msdyn_progress" })
            Assert.False(noLag.ContainsKey(forbidden), forbidden);
    }
}

public class DependencyImporterTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static MppProject Parse() => MppProjectReader.Parse(Build([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))], tasks:
    [
        TaskRow(1, "A", 1, 0, "2026-11-16T09:00:00", "2026-11-16T17:00:00"),
        TaskRow(2, "B", 1, 0, "2026-11-16T09:00:00", "2026-11-16T17:00:00"),
        TaskRow(3, "C", 1, 0, "2026-11-17T09:00:00", "2026-11-17T17:00:00")
    ], dependencies: [Dependency(1, 1, 2), Dependency(2, 2, 3, "StartStart", 1)]));

    private static CalendarImportSettings Settings() => new() { ImportDependencies = true, TaskReadbackTimeoutSeconds = 0 };

    private static async Task<(FakeTasks Tasks, MppProject Project)> TasksCreatedAsync()
    {
        var project = Parse();
        var tasks = new FakeTasks();
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        await tasks.CreateTasksAsync(ProjectId, plan.Tasks.Select(t => TaskEntityBuilder.Build(t, ProjectId, tasks.Bucket)).ToList(), CancellationToken.None);
        return (tasks, project);
    }

    private static ProjectDependencyImporter NewImporter(FakeDependencies deps, FakeTasks tasks, StringWriter writer)
        => new(deps, tasks, Settings(), writer, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Crea_las_dependencias_las_lee_de_vuelta_las_registra_en_el_state_y_es_idempotente()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var deps = new FakeDependencies();
        var state = new ImportState();
        var writer = new StringWriter();

        var first = await NewImporter(deps, tasks, writer).RunAsync(ProjectId, project, state, () => { }, CancellationToken.None);
        Assert.Equal(2, first.Created);
        Assert.Equal(2, first.Match);
        Assert.Equal(0, first.Difference);
        Assert.Equal(2, state.CreatedDependencies.Count);
        var text = writer.ToString();
        Assert.Contains("[Dependency]", text);
        Assert.Contains("MPP RelationUniqueID", text);
        Assert.Contains("MPP Lag", text);
        Assert.Contains("s sent", text);
        Assert.Contains("Result                : MATCH", text);

        var second = await NewImporter(deps, tasks, new StringWriter()).RunAsync(ProjectId, project, state, () => { }, CancellationToken.None);
        Assert.Equal(0, second.Created);
        Assert.Equal(2, second.AlreadyExisting);
        Assert.Equal(2, deps.CreatedEntities.Count);                                          // no se recrea nada
    }

    [Fact]
    public async Task Una_diferencia_en_tipo_o_retardo_se_informa_sin_corregirla()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var deps = new FakeDependencies();
        var plan = DependencyImportPlanner.Build(project, TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId),
            new TaskDurationRules(), Settings(), ProjectId);
        var lagged = plan.Dependencies.Single(d => d.LagSeconds != 0);
        deps.Overrides[lagged.DependencyId] = new DependencySnapshot(lagged.DependencyId, lagged.Predecessor.TaskId, lagged.Successor.TaskId, lagged.LinkTypeValue, 0);
        var writer = new StringWriter();

        var summary = await NewImporter(deps, tasks, writer).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(1, summary.Match);
        Assert.Equal(1, summary.Difference);
        Assert.Contains("DIFFERENCE (lag)", writer.ToString());
    }

    [Fact]
    public async Task Informa_de_las_tareas_que_Project_Operations_reprograma_por_las_dependencias()
    {
        var (tasks, project) = await TasksCreatedAsync();
        var deps = new FakeDependencies();
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var successor = plan.Tasks.Single(t => t.Source.UniqueId == 2);
        // Antes de las dependencias B coincide con el MPP (16/11); tras crearlas, Project Operations la mueve al día siguiente.
        tasks.Overrides[successor.TaskId] = new TaskSnapshot(successor.TaskId, "B", new DateTime(2026, 11, 16, 9, 0, 0), new DateTime(2026, 11, 16, 17, 0, 0), 1m);
        deps.OnCreate =() => tasks.Overrides[successor.TaskId] = new TaskSnapshot(successor.TaskId, "B", new DateTime(2026, 11, 17, 9, 0, 0), new DateTime(2026, 11, 17, 17, 0, 0), 1m);
        var writer = new StringWriter();

        var summary = await NewImporter(deps, tasks, writer).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(1, summary.TasksMoved);
        Assert.True(summary.TasksMatchAfter < summary.TasksMatchBefore);
        var text = writer.ToString();
        Assert.Contains("[Tasks moved by the dependencies]", text);
        Assert.Contains("PO before", text);
        Assert.Contains("PO after", text);
        Assert.Contains("DIFFERENCE (it matched before)", text);
    }

    [Fact]
    public async Task Las_dependencias_cuyas_tareas_no_existen_en_Dataverse_se_omiten_sin_crearse()
    {
        var project = Parse();                                   // las tareas NO se han creado
        var deps = new FakeDependencies();
        var writer = new StringWriter();
        var summary = await NewImporter(deps, new FakeTasks(), writer).RunAsync(ProjectId, project, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(0, summary.Created);
        Assert.Equal(2, summary.Skipped);
        Assert.Empty(deps.CreatedEntities);
        Assert.Contains("no existe todavía en Dataverse", writer.ToString());
    }

    [Fact]
    public async Task El_orquestador_ejecuta_las_dependencias_despues_de_las_tareas_y_termina_ahi()
    {
        var json = Build([Calendar(1, "TBT", Tbt())], tasks:
        [
            TaskRow(1, "A", 1, 0, "2026-11-16T08:00:00", "2026-11-16T17:00:00"),
            TaskRow(2, "B", 1, 0, "2026-11-17T08:00:00", "2026-11-17T17:00:00")
        ], dependencies: [Dependency(1, 1, 2)]);
        var parsed = MppProjectReader.Parse(json);
        var normalized = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(parsed));
        var templates = new FakeTemplates();
        templates.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), "Plantilla"));
        var calendars = new FakeCalendars { Pattern = normalized.CanonicalPattern };
        var tasks = new FakeTasks();
        var deps = new FakeDependencies();
        var settings = new CalendarImportSettings
        {
            ImportTasks = true, ImportDependencies = true, TaskReadbackTimeoutSeconds = 0,
            CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = normalized.Fingerprint, Name = "Plantilla" }]
        };
        var writer = new StringWriter();
        var orchestrator = new ProjectImportOrchestrator(settings, templates, new FakeProjects(), calendars, new ImportStateService(), writer, (_, _) => Task.CompletedTask, tasks, deps);
        var statePath = Path.Combine(Files.TempDir(), "state.json");
        await orchestrator.RunAsync(new CalendarImportRequest("x.json", "x.schema.json", statePath, new string('A', 64), parsed), CancellationToken.None);

        var text = writer.ToString();
        Assert.True(text.IndexOf("[Tasks]", StringComparison.Ordinal) < text.IndexOf("[Dependencies]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[Dependencies]", StringComparison.Ordinal) < text.IndexOf("[8/8] Result", StringComparison.Ordinal));
        Assert.Contains("Import stopped after the Dependencies phase.", text);
        Assert.Single(deps.CreatedEntities);
        Assert.Equal("Dependencies", ImportState.Load(statePath)!.CompletedStages![^1]);
    }
}
