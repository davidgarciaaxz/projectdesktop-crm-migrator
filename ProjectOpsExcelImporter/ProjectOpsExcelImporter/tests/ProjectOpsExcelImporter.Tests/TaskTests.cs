using System.Globalization;
using System.Text.Json.Nodes;
using ProjectOpsExcelImporter.Calendars;
using Xunit;
using static ProjectOpsExcelImporter.Tests.Dump;

namespace ProjectOpsExcelImporter.Tests;

internal sealed class FakeTasks : IProjectTaskService
{
    public HashSet<Guid> Existing { get; } = [];
    public List<JsonObject> CreatedEntities { get; } = [];
    public List<int> BatchSizes { get; } = [];
    public Guid Bucket { get; } = Guid.NewGuid();
    /// <summary>Si se informa, sustituye al cálculo por defecto (End = Start + duración en días naturales).</summary>
    public Dictionary<Guid, TaskSnapshot> Overrides { get; } = [];

    public Task<Guid> GetDefaultBucketIdAsync(Guid projectId, CancellationToken ct) => Task.FromResult(Bucket);
    public Task<IReadOnlySet<Guid>> GetExistingTaskIdsAsync(Guid projectId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(Existing.ToHashSet());
    public Task CreateTasksAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken ct)
    {
        BatchSizes.Add(entities.Count);
        foreach (var e in entities) { CreatedEntities.Add(e); Existing.Add(Guid.Parse(e["msdyn_projecttaskid"]!.GetValue<string>())); }
        return Task.CompletedTask;
    }
    public Task WaitForTaskAsync(Guid taskId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Notas (msdyn_descriptionplaintext) por tarea: las de las tareas creadas aquí y las que el test fije para tareas "ya importadas".</summary>
    public Dictionary<Guid, TaskNotesValue> Notes { get; } = [];
    public List<(Guid TaskId, string Plain, string Html)> NoteUpdates { get; } = [];
    public Task<IReadOnlyDictionary<Guid, TaskNotesValue>> ReadTaskNotesAsync(Guid projectId, CancellationToken ct)
    {
        var map = new Dictionary<Guid, TaskNotesValue>(Notes);
        foreach (var e in CreatedEntities)
            map.TryAdd(Guid.Parse(e["msdyn_projecttaskid"]!.GetValue<string>()),
                new TaskNotesValue(e["msdyn_descriptionplaintext"]?.GetValue<string>(), e["msdyn_description"]?.GetValue<string>()));
        return Task.FromResult<IReadOnlyDictionary<Guid, TaskNotesValue>>(map);
    }
    public Task UpdateTaskNotesAsync(Guid projectId, IReadOnlyList<(Guid TaskId, string Plain, string Html)> updates, CancellationToken ct)
    {
        foreach (var u in updates) { NoteUpdates.Add(u); Notes[u.TaskId] = new TaskNotesValue(u.Plain, u.Html); }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<TaskSnapshot>> ReadTasksAsync(Guid projectId, CancellationToken ct)
    {
        var list = new List<TaskSnapshot>();
        foreach (var e in CreatedEntities)
        {
            var id = Guid.Parse(e["msdyn_projecttaskid"]!.GetValue<string>());
            if (Overrides.TryGetValue(id, out var o)) { list.Add(o); continue; }
            DateTime? start = e["msdyn_scheduledstart"] is { } s ? DateTime.Parse(s.GetValue<string>(), CultureInfo.InvariantCulture) : null;
            decimal? duration = e["msdyn_duration"] is { } d ? d.GetValue<decimal>() : null;
            list.Add(new TaskSnapshot(id, e["msdyn_subject"]!.GetValue<string>(), start, start?.AddDays((double)(duration ?? 0)), duration));
        }
        return Task.FromResult<IReadOnlyList<TaskSnapshot>>(list);
    }
}

public class TaskPlannerTests
{
    private static MppProject Parse(params object[] tasks)
        => MppProjectReader.Parse(Build([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))], tasks: tasks));

    private static readonly TaskDurationRules Rules = new();

    private static object[] Hierarchy() =>
    [
        TaskRow(0, "PROYECTO", 0, 0, summary: true, projectSummary: true),
        TaskRow(1, "Fase 1", 1, 0, summary: true),
        TaskRow(2, "Tarea A", 2, 1, "2026-11-16T09:00:00", "2026-11-18T19:00:00", 3),
        TaskRow(3, "Hito", 2, 1, "2026-11-18T19:00:00", "2026-11-18T19:00:00", 0, milestone: true),
        TaskRow(4, "Nula", 1, 0, isNull: true),
        TaskRow(5, "Fase 2", 1, 0, summary: true),
        TaskRow(6, "Tarea B", 2, 5, "2026-11-19T09:00:00", "2026-11-19T19:00:00", 1)
    ];

    [Fact]
    public void Se_omiten_la_tarea_resumen_del_proyecto_y_las_nulas_y_se_conserva_la_jerarquia()
    {
        var plan = TaskImportPlanner.Build(Parse(Hierarchy()), Rules, new TaskSelection());
        Assert.Equal([1, 2, 3, 5, 6], plan.Tasks.Select(t => t.Source.UniqueId));
        Assert.Null(plan.Tasks[0].ParentTaskId);                                         // raíz: el padre 0 es la tarea resumen del proyecto
        Assert.Equal(plan.Tasks[0].TaskId, plan.Tasks[1].ParentTaskId);
        Assert.Equal(plan.Tasks[0].TaskId, plan.Tasks[2].ParentTaskId);
        Assert.Equal(plan.Tasks[3].TaskId, plan.Tasks[4].ParentTaskId);
    }

    [Fact]
    public void El_orden_sigue_el_campo_Id_del_MPP_aunque_el_array_venga_desordenado_y_se_envia_como_secuencia_de_visualizacion()
    {
        // UniqueId distinto de Id: el orden manda el Id (como en el fichero 1, donde UniqueId 241 tiene Id 2).
        var tasks = new object[]
        {
            new { Id = 4, UniqueId = 10, Guid = "00000000-0000-0000-0000-000000000010", Name = "D", OutlineLevel = 1, ParentUniqueId = 0, IsSummary = false, IsMilestone = false, IsNull = false, IsProjectSummaryTask = false, IsActive = true, Start = "2026-11-16T09:00:00", Finish = "2026-11-16T17:00:00", Duration = new { Value = 1m, Units = "Days" } },
            new { Id = 2, UniqueId = 241, Guid = "00000000-0000-0000-0000-000000000241", Name = "B", OutlineLevel = 1, ParentUniqueId = 0, IsSummary = false, IsMilestone = false, IsNull = false, IsProjectSummaryTask = false, IsActive = true, Start = "2026-11-16T09:00:00", Finish = "2026-11-16T17:00:00", Duration = new { Value = 1m, Units = "Days" } },
            new { Id = 3, UniqueId = 5, Guid = "00000000-0000-0000-0000-000000000005", Name = "C", OutlineLevel = 1, ParentUniqueId = 0, IsSummary = false, IsMilestone = false, IsNull = false, IsProjectSummaryTask = false, IsActive = true, Start = "2026-11-16T09:00:00", Finish = "2026-11-16T17:00:00", Duration = new { Value = 1m, Units = "Days" } }
        };
        var plan = TaskImportPlanner.Build(Parse(tasks), Rules, new TaskSelection());
        Assert.Equal(["B", "C", "D"], plan.Tasks.Select(t => t.Source.Name));
        Assert.Equal([3d, 4d, 5d], plan.Tasks.Select(t => TaskEntityBuilder.Build(t, Guid.NewGuid(), Guid.NewGuid())["msdyn_displaysequence"]!.GetValue<double>()));
    }

    [Fact]
    public void Los_GUID_de_las_tareas_se_derivan_del_GUID_real_del_proyecto_y_son_estables()
    {
        var project = Parse(Hierarchy());
        var realProjectA = Guid.NewGuid();
        var realProjectB = Guid.NewGuid();
        var a1 = TaskImportPlanner.Build(project, Rules, new TaskSelection(), realProjectA).Tasks[1].TaskId;
        var a2 = TaskImportPlanner.Build(project, Rules, new TaskSelection(), realProjectA).Tasks[1].TaskId;
        var b = TaskImportPlanner.Build(project, Rules, new TaskSelection(), realProjectB).Tasks[1].TaskId;
        Assert.Equal(a1, a2);                                    // reanudar el mismo proyecto no cambia los GUID de sus tareas
        Assert.NotEqual(a1, b);                                  // un proyecto nuevo (tras borrar el anterior) tiene tareas con GUID nuevos
        Assert.Equal(TaskImportPlanner.TaskId(realProjectA, project.Tasks.First(t => t.UniqueId == 2).Guid!.Value), a1);
    }

    [Fact]
    public void La_secuencia_de_visualizacion_es_siempre_mayor_que_1_incluso_para_la_tarea_con_Id_1()
    {
        var plan = TaskImportPlanner.Build(Parse(Hierarchy()), Rules, new TaskSelection());
        foreach (var task in plan.Tasks)
            Assert.True(TaskEntityBuilder.Build(task, Guid.NewGuid(), Guid.NewGuid())["msdyn_displaysequence"]!.GetValue<double>() > 1d);
    }

    [Fact]
    public void El_GUID_es_determinista_con_el_espacio_de_nombres_del_importador_existente()
    {
        var project = Parse(Hierarchy());
        var first = TaskImportPlanner.Build(project, Rules, new TaskSelection()).Tasks[1];
        var again = TaskImportPlanner.Build(project, Rules, new TaskSelection()).Tasks[1];
        Assert.Equal(first.TaskId, again.TaskId);
        var expected = DeterministicGuid.Create(ProjectOpsImporter.IdNamespace, $"{project.ProjectGuid:D}:task:{first.Source.Guid:D}");
        Assert.Equal(expected, first.TaskId);
        Assert.NotEqual(first.TaskId, TaskImportPlanner.TaskId(Guid.NewGuid(), first.Source.Guid!.Value));     // depende del proyecto
    }

    [Fact]
    public void Seleccionar_por_UniqueId_incluye_los_ancestros_y_marca_su_origen()
    {
        var plan = TaskImportPlanner.Build(Parse(Hierarchy()), Rules, new TaskSelection(UniqueIds: [6]));
        Assert.Equal([5, 6], plan.Tasks.Select(t => t.Source.UniqueId));
        Assert.True(plan.Tasks[0].AddedAsAncestor);
        Assert.False(plan.Tasks[1].AddedAsAncestor);
    }

    [Fact]
    public void Limitar_por_numero_toma_las_primeras_tareas_del_fichero_mas_sus_ancestros()
    {
        var plan = TaskImportPlanner.Build(Parse(Hierarchy()), Rules, new TaskSelection(MaxTasks: 2));
        Assert.Equal([1, 2], plan.Tasks.Select(t => t.Source.UniqueId));
    }

    [Fact]
    public void Un_UniqueId_inexistente_o_no_importable_es_un_error_con_exit_9()
    {
        var ex = Assert.Throws<CalendarImportException>(() => TaskImportPlanner.Build(Parse(Hierarchy()), Rules, new TaskSelection(UniqueIds: [4, 99])));
        Assert.Equal(ImportExitCodes.Tasks, ex.ExitCode);
        Assert.Contains("4", ex.Message);
        Assert.Contains("99", ex.Message);
    }

    [Theory]
    [InlineData("Days", 3, 3)]
    [InlineData("Hours", 16, 2)]
    [InlineData("Weeks", 2, 10)]
    [InlineData("Months", 1.5, 30)]
    public void La_duracion_se_convierte_a_dias_con_las_unidades_del_importador(string units, double value, double days)
    {
        var task = new MppTask { UniqueId = 1, DurationValue = (decimal)value, DurationUnits = units };
        Assert.Equal((decimal)days, TaskImportPlanner.ToDays(task, Rules));
    }

    [Fact]
    public void Una_unidad_de_duracion_desconocida_es_un_error()
        => Assert.Throws<CalendarImportException>(() => TaskImportPlanner.ToDays(new MppTask { UniqueId = 1, DurationValue = 1, DurationUnits = "Parsecs" }, Rules));

    [Fact]
    public void Los_atributos_enviados_son_solo_los_minimos_y_nunca_Finish_ni_esfuerzo_ni_costes()
    {
        var plan = TaskImportPlanner.Build(Parse(Hierarchy()), Rules, new TaskSelection());
        var project = Guid.NewGuid();
        var bucket = Guid.NewGuid();

        var leaf = TaskEntityBuilder.Build(plan.Tasks[1], project, bucket);
        Assert.Equal("Tarea A", leaf["msdyn_subject"]!.GetValue<string>());
        Assert.Equal("2026-11-16T09:00:00", leaf["msdyn_scheduledstart"]!.GetValue<string>());
        Assert.Equal(3m, leaf["msdyn_duration"]!.GetValue<decimal>());
        Assert.Equal($"/msdyn_projects({project:D})", leaf["msdyn_project@odata.bind"]!.GetValue<string>());
        Assert.Equal($"/msdyn_projecttasks({plan.Tasks[0].TaskId:D})", leaf["msdyn_parenttask@odata.bind"]!.GetValue<string>());
        Assert.Equal(2, leaf["msdyn_outlinelevel"]!.GetValue<int>());
        Assert.Equal(
            ["@odata.type", "msdyn_LinkStatus", "msdyn_displaysequence", "msdyn_duration", "msdyn_outlinelevel", "msdyn_parenttask@odata.bind", "msdyn_project@odata.bind",
             "msdyn_projectbucket@odata.bind", "msdyn_projecttaskid", "msdyn_scheduledstart", "msdyn_subject"],
            leaf.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var forbidden in new[] { "msdyn_scheduledend", "msdyn_finish", "msdyn_effort", "msdyn_progress", "msdyn_effortcompleted" })
            Assert.False(leaf.ContainsKey(forbidden), forbidden);

        var milestone = TaskEntityBuilder.Build(plan.Tasks[2], project, bucket);
        Assert.Equal(0m, milestone["msdyn_duration"]!.GetValue<decimal>());

        var summary = TaskEntityBuilder.Build(plan.Tasks[0], project, bucket);        // la calcula Project Operations
        Assert.False(summary.ContainsKey("msdyn_scheduledstart"));
        Assert.False(summary.ContainsKey("msdyn_duration"));
        Assert.False(summary.ContainsKey("msdyn_parenttask@odata.bind"));
    }

    [Fact]
    public void Las_tareas_que_atraviesan_un_dia_no_laborable_de_las_excepciones_se_detectan()
    {
        var task = new MppTask { UniqueId = 1, Start = new DateTime(2026, 12, 7, 9, 0, 0), Finish = new DateTime(2026, 12, 10, 19, 0, 0) };
        var days = TaskCrossing.NonWorkingDays([
            new NormalizedCalendarException(1, "Constitución", null, new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 8), false, [], false),
            new NormalizedCalendarException(1, "Sábado trabajado", null, new DateOnly(2026, 12, 9), new DateOnly(2026, 12, 9), true, [], false),
            new NormalizedCalendarException(1, "Fuera", null, new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 1), false, [], false)]);
        Assert.Equal([new DateOnly(2026, 12, 8)], TaskCrossing.Crossed(task, days));
    }
}

public class TaskImporterTests
{
    /// <summary>GUID real (fijo) del proyecto en estos tests: de él se derivan los GUID de las tareas.</summary>
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static MppProject Parse(params object[] tasks)
        => MppProjectReader.Parse(Build([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))], tasks: tasks));

    private static object[] Tasks() =>
    [
        TaskRow(1, "Fase", 1, 0, summary: true),
        TaskRow(2, "Normal", 2, 1, "2026-11-16T09:00:00", "2026-11-17T09:00:00", 1),
        TaskRow(3, "Cruza festivo", 2, 1, "2026-12-07T09:00:00", "2026-12-10T09:00:00", 3)
    ];

    private static CalendarImportSettings Settings(int batch = 100) => new() { ImportTasks = true, TaskBatchSize = batch, TaskReadbackTimeoutSeconds = 0 };

    private static (ProjectTaskImporter Importer, StringWriter Out) NewImporter(FakeTasks service, CalendarImportSettings settings)
    {
        var writer = new StringWriter();
        return (new ProjectTaskImporter(service, settings, writer, (_, _) => Task.CompletedTask), writer);
    }

    private static readonly NormalizedCalendarException[] Holiday =
        [new(1, "Constitución", null, new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 8), false, [], false)];

    private static object[] TasksWithNotes() =>
    [
        TaskRow(1, "Fase", 1, 0, summary: true, notes: "Resumen de la fase\r\n"),
        TaskRow(2, "Con notas", 2, 1, "2026-11-16T09:00:00", "2026-11-17T09:00:00", 1, notes: "\tViaje\t2\r\n\tTotal\t30.173,96 €\r\n\r\n"),
        TaskRow(3, "Sin notas", 2, 1, "2026-11-18T09:00:00", "2026-11-19T09:00:00", 1),
        TaskRow(4, "Notas en blanco", 2, 1, "2026-11-20T09:00:00", "2026-11-21T09:00:00", 1, notes: " \t\r\n ")
    ];

    [Fact]
    public async Task Las_notas_del_MPP_se_envian_en_la_creacion_a_msdyn_descriptionplaintext_conservando_tabuladores_y_sin_enviar_las_vacias()
    {
        var service = new FakeTasks();
        var project = Parse(TasksWithNotes());
        var (importer, writer) = NewImporter(service, Settings());

        var summary = await importer.RunAsync(ProjectId, project, Holiday, new ImportState(), () => { }, CancellationToken.None);

        string? NotesOf(int uid, string attribute = "msdyn_descriptionplaintext") => service.CreatedEntities.Single(e => e["msdyn_subject"]!.GetValue<string>() == project.Tasks.Single(t => t.UniqueId == uid).Name)[attribute]?.GetValue<string>();
        Assert.Equal("Resumen de la fase", NotesOf(1));                                     // las tareas resumen también llevan notas
        Assert.Equal("\tViaje\t2\n\tTotal\t30.173,96 €", NotesOf(2));                       // tabuladores y líneas intermedias intactos, saltos unificados, sin cola
        Assert.Null(NotesOf(3));                                                            // sin notas: el atributo no se envía
        Assert.Null(NotesOf(4));                                                            // solo espacios en blanco: tampoco
        // La misma nota en HTML (msdyn_description), con el formato del editor de Project Operations.
        Assert.Equal("<div>Resumen de la fase</div>", NotesOf(1, "msdyn_description"));
        Assert.Equal("<div>&nbsp;&nbsp;&nbsp;&nbsp;Viaje &nbsp;&nbsp;&nbsp;2</div><div>&nbsp;&nbsp;&nbsp;&nbsp;Total &nbsp;&nbsp;&nbsp;30.173,96 €</div>", NotesOf(2, "msdyn_description"));
        Assert.Null(NotesOf(3, "msdyn_description"));
        Assert.Null(NotesOf(4, "msdyn_description"));
        Assert.Equal(2, summary.NotesSent);
        Assert.Equal(0, summary.NotesUpdated);
        Assert.Contains("sent with creation 2", writer.ToString());
        Assert.Empty(service.NoteUpdates);                                                  // las nuevas no se actualizan después
    }

    [Fact]
    public async Task Las_tareas_ya_importadas_se_actualizan_solo_si_su_nota_difiere_y_reanudar_no_reescribe()
    {
        var service = new FakeTasks();
        var project = Parse(TasksWithNotes());
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        // Tres tareas ya existen: la 1 con la nota igual (Dataverse devuelve \r\n), la 2 sin nota y la 3 sin nota en el MPP.
        foreach (var t in plan.Tasks.Where(t => t.Source.UniqueId is 1 or 2 or 3)) service.Existing.Add(t.TaskId);
        service.Notes[plan.Tasks.Single(t => t.Source.UniqueId == 1).TaskId] = new TaskNotesValue("Resumen de la fase\r\n", "<div>Resumen de la fase</div>");
        service.Notes[plan.Tasks.Single(t => t.Source.UniqueId == 2).TaskId] = new TaskNotesValue(null, null);
        var (importer, writer) = NewImporter(service, Settings());

        var first = await importer.RunAsync(ProjectId, project, Holiday, new ImportState(), () => { }, CancellationToken.None);

        var update = Assert.Single(service.NoteUpdates);
        Assert.Equal(plan.Tasks.Single(t => t.Source.UniqueId == 2).TaskId, update.TaskId);
        Assert.Equal("\tViaje\t2\n\tTotal\t30.173,96 €", update.Plain);
        Assert.Equal("<div>&nbsp;&nbsp;&nbsp;&nbsp;Viaje &nbsp;&nbsp;&nbsp;2</div><div>&nbsp;&nbsp;&nbsp;&nbsp;Total &nbsp;&nbsp;&nbsp;30.173,96 €</div>", update.Html);
        Assert.Equal(1, first.NotesUpdated);
        Assert.Equal(1, first.NotesAlreadyEqual);                                           // la 1 ya coincidía (normalizando saltos de línea)
        Assert.Contains("updated on existing tasks 1", writer.ToString());

        var second = await NewImporter(service, Settings()).Importer.RunAsync(ProjectId, project, Holiday, new ImportState(), () => { }, CancellationToken.None);
        Assert.Single(service.NoteUpdates);                                                 // sin nuevas escrituras
        Assert.Equal(0, second.NotesUpdated);
        Assert.Equal(2, second.NotesAlreadyEqual);
    }

    [Fact]
    public async Task Una_tarea_con_el_texto_plano_igual_pero_sin_HTML_se_actualiza()
    {
        var service = new FakeTasks();
        var project = Parse(TasksWithNotes());
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var task1 = plan.Tasks.Single(t => t.Source.UniqueId == 1).TaskId;
        foreach (var t in plan.Tasks.Where(t => t.Source.UniqueId is 1 or 2 or 3)) service.Existing.Add(t.TaskId);
        service.Notes[task1] = new TaskNotesValue("Resumen de la fase", null);            // importada antes de guardar también el HTML
        service.Notes[plan.Tasks.Single(t => t.Source.UniqueId == 2).TaskId] = new TaskNotesValue(null, null);

        var summary = await NewImporter(service, Settings()).Importer.RunAsync(ProjectId, project, Holiday, new ImportState(), () => { }, CancellationToken.None);

        Assert.Equal(2, summary.NotesUpdated);
        Assert.Equal("<div>Resumen de la fase</div>", service.NoteUpdates.Single(u => u.TaskId == task1).Html);
    }

    [Fact]
    public void El_HTML_de_las_notas_sigue_el_formato_del_editor_de_Project_Operations()
    {
        Assert.Equal("<div>nota xavi</div>", TaskNotes.ToHtml("nota xavi"));                                   // una div por línea
        Assert.Equal("<div>a &amp; &lt;b&gt;</div><div><br></div><div><br></div><div>fin</div>",
            TaskNotes.ToHtml("a & <b>\n\n\t\nfin"));                                                           // escapado; líneas vacías o solo con tabuladores = <div><br></div>
        Assert.Equal("<div>&nbsp;&nbsp;&nbsp;&nbsp;x &nbsp;&nbsp;y</div>", TaskNotes.ToHtml("\tx   y"));       // los tabuladores y las series de espacios no se pierden
    }

    [Fact]
    public void La_normalizacion_de_notas_es_estable()
    {
        Assert.Null(TaskNotes.Normalize(null));
        Assert.Null(TaskNotes.Normalize("  \t \r\n"));
        Assert.Equal("a\n\tb", TaskNotes.Normalize("a\r\n\tb\r\n\r\n"));
        Assert.Equal(TaskNotes.Normalize("x\ny"), TaskNotes.Normalize("x\r\ny"));
    }

    [Fact]
    public async Task Crea_las_tareas_en_lotes_guarda_la_relacion_UniqueID_GUID_y_es_idempotente()
    {
        var service = new FakeTasks();
        var state = new ImportState();
        var project = Parse(Tasks());
        var (importer, _) = NewImporter(service, Settings(batch: 2));

        var first = await importer.RunAsync(ProjectId, project, Holiday, state, () => { }, CancellationToken.None);
        Assert.Equal(3, first.Created);
        Assert.Equal([2, 1], service.BatchSizes);                                           // lotes de 2 y 1
        Assert.Equal([1, 2, 3], state.TaskIds.Keys.OrderBy(k => k));
        Assert.Equal([1, 2, 3], state.CreatedTasks.OrderBy(k => k));
        Assert.Equal(service.Bucket, state.BucketId);

        var second = await NewImporter(service, Settings(batch: 2)).Importer.RunAsync(ProjectId, project, Holiday, state, () => { }, CancellationToken.None);
        Assert.Equal(0, second.Created);
        Assert.Equal(3, second.AlreadyExisting);
        Assert.Equal(3, service.CreatedEntities.Count);                                      // no se recrea nada
    }

    [Fact]
    public async Task Compara_con_lo_que_calcula_Project_Operations_y_no_corrige_las_diferencias()
    {
        var service = new FakeTasks();
        var project = Parse(Tasks());
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        var summaryTask = plan.Tasks.Single(t => t.Source.UniqueId == 1);
        service.Overrides[summaryTask.TaskId] = new TaskSnapshot(summaryTask.TaskId, "Fase", new DateTime(2026, 11, 16, 9, 0, 0), new DateTime(2026, 12, 11, 9, 0, 0), null);   // la calcula PO
        var crossing = plan.Tasks.Single(t => t.Source.UniqueId == 3);
        // Project Operations calcula 2 días más porque el 8/12 no es laborable: Finish distinto al del MPP.
        service.Overrides[crossing.TaskId] = new TaskSnapshot(crossing.TaskId, "Cruza festivo",
            new DateTime(2026, 12, 7, 9, 0, 0), new DateTime(2026, 12, 11, 9, 0, 0), 3m);

        var (importer, output) = NewImporter(service, Settings());
        var summary = await importer.RunAsync(ProjectId, project, Holiday, new ImportState(), () => { }, CancellationToken.None);
        var text = output.ToString();

        Assert.Equal(2, summary.Match);                                                     // resumen y tarea normal
        Assert.Equal(1, summary.Difference);
        Assert.Equal(1, summary.CrossingDifference);
        Assert.Contains("MPP UniqueID", text);
        Assert.Contains("Project Operations ID", text);
        Assert.Contains("Crosses exceptions", text);
        Assert.Contains("DIFFERENCE (finish date)", text);
        Assert.Contains("MPP Finish", text);
        Assert.Contains("comparison only; not sent", text);
        Assert.DoesNotContain(service.CreatedEntities, e => e.ContainsKey("msdyn_scheduledend"));
    }

    [Fact]
    public async Task Las_tareas_ya_existentes_en_Dataverse_no_se_vuelven_a_crear()
    {
        var service = new FakeTasks();
        var project = Parse(Tasks());
        var plan = TaskImportPlanner.Build(project, new TaskDurationRules(), new TaskSelection(), ProjectId);
        service.Existing.Add(plan.Tasks[0].TaskId);                                          // la tarea resumen ya existía
        var (importer, _) = NewImporter(service, Settings());
        var summary = await importer.RunAsync(ProjectId, project, Holiday, new ImportState(), () => { }, CancellationToken.None);
        Assert.Equal(2, summary.Created);
        Assert.Equal(1, summary.AlreadyExisting);
    }

    [Fact]
    public async Task El_orquestador_ejecuta_la_fase_de_tareas_despues_de_las_excepciones_y_termina_ahi()
    {
        var calendarJson = Build([Calendar(1, "TBT", Tbt())], [Exception(1, "Festivo", "2026-12-08", "2026-12-08")], tasks: Tasks());
        var tasks = new FakeTasks();
        var projects = new FakeProjects();
        var calendars = new FakeCalendars { Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(calendarJson))).CanonicalPattern };
        var templates = new FakeTemplates();
        var fingerprint = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(calendarJson))).Fingerprint;
        templates.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), "Plantilla"));
        var settings = new CalendarImportSettings
        {
            ImportTasks = true, TaskReadbackTimeoutSeconds = 0,
            CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = fingerprint, Name = "Plantilla" }]
        };
        var writer = new StringWriter();
        var orchestrator = new ProjectImportOrchestrator(settings, templates, projects, calendars, new ImportStateService(), writer, (_, _) => Task.CompletedTask, tasks);
        var statePath = Path.Combine(Files.TempDir(), "state.json");
        await orchestrator.RunAsync(new CalendarImportRequest("x.json", "x.schema.json", statePath, new string('A', 64), MppProjectReader.Parse(calendarJson)), CancellationToken.None);

        var text = writer.ToString();
        Assert.True(text.IndexOf("[7/8] Calendar exceptions", StringComparison.Ordinal) < text.IndexOf("[Tasks]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[Tasks]", StringComparison.Ordinal) < text.IndexOf("[8/8] Result", StringComparison.Ordinal));
        Assert.Contains("Import stopped after the Tasks phase.", text);
        Assert.Equal(3, tasks.CreatedEntities.Count);
        Assert.Equal(["SchemaValidation", "CalendarResolution", "CalendarTemplateProvisioning", "ProjectCreation", "ProjectCalendarResolution", "WorkWeeks", "CalendarExceptions", "Tasks"],
            ImportState.Load(statePath)!.CompletedStages);
    }
}
