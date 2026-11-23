using ProjectOpsExcelImporter.Calendars;
using Xunit;
using static ProjectOpsExcelImporter.Tests.Dump;

namespace ProjectOpsExcelImporter.Tests;

public class CalendarNormalizationTests
{
    private static NormalizedCalendar Std(string name = "Estándar", int uid = 1)
        => MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(uid, name, WeekDays("09:00-13:00", "15:00-19:00"))], null, uid)));

    [Fact] // 1
    public void Resuelve_el_calendario_por_DefaultCalendarUniqueID_y_no_por_nombre()
    {
        var project = Project([Calendar(5, "Estándar", WeekDays("08:00-12:00")), Calendar(7, "Estándar", WeekDays("09:00-13:00"))], null, 7);
        var calendar = MppCalendarResolver.Resolve(project);
        Assert.Equal(7, calendar.UniqueId);
        Assert.Contains("09:00-13:00", MppCalendarNormalizer.NormalizeBase(calendar).CanonicalPattern);
    }

    [Fact] // 2
    public void Falla_si_el_calendario_no_esta_en_Calendars()
    {
        var ex = Assert.Throws<CalendarImportException>(() => MppCalendarResolver.Resolve(Project([Calendar(1, "A", WeekDays("09:00-13:00"))], null, 99)));
        Assert.Contains("99", ex.Message);
        Assert.Equal(ImportExitCodes.Functional, ex.ExitCode);
    }

    [Fact]
    public void Falla_si_DefaultCalendarUniqueID_esta_vacio()
        => Assert.Throws<CalendarImportException>(() => MppCalendarResolver.Resolve(Project([Calendar(1, "A", WeekDays("09:00-13:00"))], null, null)));

    [Fact] // 3
    public void Normaliza_09_13_15_19()
    {
        var c = Std();
        Assert.Equal("MON=09:00-13:00|15:00-19:00;TUE=09:00-13:00|15:00-19:00;WED=09:00-13:00|15:00-19:00;THU=09:00-13:00|15:00-19:00;FRI=09:00-13:00|15:00-19:00;SAT=OFF;SUN=OFF", c.CanonicalPattern);
        Assert.Equal("MON-FRI 09:00-13:00 | 15:00-19:00; SAT-SUN OFF", c.DisplayPattern);
        Assert.Equal("MPP - Calendario 09-13 15-19", CalendarTemplateNaming.BuildName("MPP - Calendario", c, null));
    }

    [Fact] // 4
    public void Normaliza_08_17_con_viernes_08_13_30_sin_inventar_pausas()
    {
        var c = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "TBT", Tbt())])));
        Assert.Equal("MON=08:00-17:00;TUE=08:00-17:00;WED=08:00-17:00;THU=08:00-17:00;FRI=08:00-13:30;SAT=OFF;SUN=OFF", c.CanonicalPattern);
        Assert.Equal(540, c.Days[0].Periods.Single().EndMinute - c.Days[0].Periods.Single().StartMinute); // un único periodo de 9 h
        Assert.Equal("MPP - Calendario 08-17 V08-13:30", CalendarTemplateNaming.BuildName("MPP - Calendario", c, null));
    }

    [Fact] // 5
    public void Nombres_distintos_con_el_mismo_horario_dan_el_mismo_fingerprint()
    {
        var a = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(362, "TBT MADRID", Tbt())], null, 362)));
        var b = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(369, "TBT SEVILLA", Tbt())], null, 369)));
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact] // 6
    public void Las_WorkWeeks_no_modifican_el_fingerprint_base()
    {
        var ww = new[] { new { Name = "Intensiva", From = "2025-07-01", To = "2025-09-01", Days = new[] { WwDay("Monday", "Working", ["08:00-15:00"]) } } };
        var plain = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt())])));
        var withWw = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt(), ww.Cast<object>().ToArray())])));
        Assert.Equal(plain.Fingerprint, withWw.Fingerprint);
    }

    [Fact] // 7
    public void Las_excepciones_no_modifican_el_fingerprint_base()
    {
        var plain = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt())])));
        var withEx = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt())], [Exception(1, "Festivo", "2025-01-06", "2025-01-06")])));
        Assert.Equal(plain.Fingerprint, withEx.Fingerprint);
    }

    [Fact] // 8
    public void El_orden_de_los_periodos_no_cambia_la_normalizacion()
    {
        var a = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", WeekDays("15:00-19:00", "09:00-13:00"))])));
        Assert.Equal(Std().CanonicalPattern, a.CanonicalPattern);
        Assert.Equal(Std().Fingerprint, a.Fingerprint);
    }

    [Fact] // 9
    public void Periodos_diferentes_dan_fingerprints_distintos()
    {
        var other = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-18:00"))])));
        Assert.NotEqual(Std().Fingerprint, other.Fingerprint);
    }

    [Fact]
    public void Periodos_solapados_son_un_error()
        => Assert.Throws<CalendarImportException>(() => MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", WeekDays("09:00-14:00", "13:00-19:00"))]))));

    [Fact]
    public void Nombre_largo_usa_fingerprint_si_excede_la_longitud_maxima()
    {
        var name = CalendarTemplateNaming.BuildName("MPP - Calendario", Std(), 20);
        Assert.StartsWith("MPP - Calendario ", name);
        Assert.True(name.Length <= 20);
    }

    [Fact]
    public void WorkWeeks_distinguen_herencia_no_laborable_y_horario_explicito()
    {
        var days = new object[]
        {
            WwDay("Monday", "Working", ["08:00-15:00"]), WwDay("Tuesday", "Default", []), WwDay("Wednesday", "NonWorking", []),
            WwDay("Thursday", "Default", []), WwDay("Friday", "Default", []), WwDay("Saturday", "Default", []), WwDay("Sunday", "Default", [])
        };
        var ww = new object[] { new { Name = "W", From = "2025-07-01", To = "2025-09-01", Days = days } };
        var calendar = MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt(), ww)]));
        var week = MppCalendarNormalizer.NormalizeWorkWeeks(calendar, []).Single();
        Assert.Equal(DayScheduleKind.Working, week.Days[0].Kind);
        Assert.Equal(DayScheduleKind.Inherit, week.Days[1].Kind);
        Assert.Equal(DayScheduleKind.NonWorking, week.Days[2].Kind);
    }

    [Fact]
    public void WorkWeeks_solapadas_son_un_error_y_duplicadas_se_omiten()
    {
        object Week(string from, string to) => new
        {
            Name = "W", From = from, To = to,
            Days = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }.Select(d => WwDay(d, "Default", [])).ToArray()
        };
        var warnings = new List<string>();
        var dup = MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt(), [Week("2025-01-01", "2025-02-01"), Week("2025-01-01", "2025-02-01")])]));
        Assert.Single(MppCalendarNormalizer.NormalizeWorkWeeks(dup, warnings));
        Assert.Single(warnings);
        var overlap = MppCalendarResolver.Resolve(Project([Calendar(1, "A", Tbt(), [Week("2025-01-01", "2025-02-01"), Week("2025-01-15", "2025-03-01")])]));
        Assert.Throws<CalendarImportException>(() => MppCalendarNormalizer.NormalizeWorkWeeks(overlap, []));
    }
}

public class RecurrenceTests
{
    private static CalendarImportSettings Settings(string mode) => new() { RecurringExceptionsMode = mode };

    private static (MppProject Project, MppCalendar Calendar) Yearly()
    {
        var p = Project([Calendar(1, "A", Tbt())],
            [Exception(1, "Año Nuevo", "2023-01-01", "2040-01-01", recurring: true, type: "Yearly", occurrences: 18, month: 1, dayNumber: 1,
                recStart: "2023-01-01", recFinish: "2040-01-01", expandedCount: 18)]);
        return (p, p.Calendars[0]);
    }

    [Fact] // 16 (Expand)
    public void Expand_genera_fechas_concretas_solo_dentro_del_proyecto()
    {
        var (p, c) = Yearly(); // proyecto 2024-01-01 .. 2026-12-31
        var items = MppExceptionNormalizer.Normalize(p, c, Settings("Expand"), []);
        Assert.Equal(["2024-01-01", "2025-01-01", "2026-01-01"], items.Select(i => i.From.ToString("yyyy-MM-dd")));
        Assert.All(items, i => { Assert.True(i.IsExpanded); Assert.Equal(1, i.CalendarUniqueId); Assert.Equal("Año Nuevo", i.SourceName); Assert.NotNull(i.RecurrenceRule); });
    }

    [Fact] // 16 (Fail)
    public void Fail_detiene_la_importacion()
    {
        var (p, c) = Yearly();
        var ex = Assert.Throws<CalendarImportException>(() => MppExceptionNormalizer.Normalize(p, c, Settings("Fail"), []));
        Assert.Equal(ImportExitCodes.CalendarExceptions, ex.ExitCode);
    }

    [Fact] // 16 (SkipWithWarning)
    public void SkipWithWarning_omite_y_registra_la_regla_completa()
    {
        var (p, c) = Yearly();
        var warnings = new List<string>();
        Assert.Empty(MppExceptionNormalizer.Normalize(p, c, Settings("SkipWithWarning"), warnings));
        Assert.Contains("Yearly", Assert.Single(warnings));
    }

    [Fact] // 16 (no interpretable)
    public void Recurrencia_relativa_no_se_aproxima_en_silencio()
    {
        var p = Project([Calendar(1, "A", Tbt())], [Exception(1, "R", "2024-01-01", "2025-01-01", recurring: true, type: "Monthly", relative: true, occurrences: 3, dayNumber: 2, recStart: "2024-01-01", recFinish: "2025-01-01", expandedCount: 3)]);
        var ex = Assert.Throws<CalendarImportException>(() => MppExceptionNormalizer.Normalize(p, p.Calendars[0], Settings("Expand"), []));
        Assert.Contains("relativas", ex.Message);
    }

    [Fact]
    public void El_recuento_distinto_al_del_JSON_se_rechaza()
    {
        var p = Project([Calendar(1, "A", Tbt())], [Exception(1, "X", "2023-01-01", "2040-01-01", recurring: true, type: "Yearly", occurrences: 18, month: 1, dayNumber: 1, recStart: "2023-01-01", recFinish: "2040-01-01", expandedCount: 17)]);
        Assert.Throws<CalendarImportException>(() => MppExceptionNormalizer.Normalize(p, p.Calendars[0], Settings("Expand"), []));
    }

    [Fact]
    public void Fechas_duplicadas_de_distintas_reglas_se_eliminan_y_Working_conserva_periodos()
    {
        var p = Project([Calendar(1, "A", Tbt())],
        [
            Exception(1, "Festivo A", "2025-01-06", "2025-01-06"),
            Exception(1, "Festivo B", "2025-01-06", "2025-01-06"),
            Exception(1, "Sábado laborable", "2025-02-01", "2025-02-01", working: true, periods: P("09:00-13:00"))
        ]);
        var items = MppExceptionNormalizer.Normalize(p, p.Calendars[0], Settings("Expand"), []);
        Assert.Equal(2, items.Count);
        Assert.Equal("09:00-13:00", Assert.Single(items.Single(i => i.Working).Periods).ToString());
    }
}

public class ProvisionerTests
{
    private static NormalizedCalendar Weekly()
        => MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "A", WeekDays("09:00-13:00", "15:00-19:00"))])));

    private const string TemplateName = "MPP - Calendario 09-13 15-19";

    private static CalendarImportSettings Config(Guid? id = null, string? name = TemplateName)
        => new() { CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = Weekly().Fingerprint, Name = name, Id = id }] };

    private static Task<CalendarTemplateResolution> Run(FakeTemplates repo, CalendarImportSettings s)
        => new CalendarTemplateResolver(repo, s).ResolveAsync(Weekly(), CancellationToken.None);

    [Fact]
    public async Task Resuelve_la_plantilla_manual_por_nombre_segun_la_configuracion()
    {
        var repo = new FakeTemplates();
        var existing = new CalendarTemplateRecord(Guid.NewGuid(), TemplateName);
        repo.Items.Add(existing);
        var result = await Run(repo, Config());
        Assert.Equal(existing.Id, result.Id);
        Assert.False(result.Created);
        Assert.StartsWith("FOUND", result.Result);
    }

    [Fact]
    public async Task Resuelve_por_Id_y_valida_el_nombre_si_se_indica()
    {
        var repo = new FakeTemplates();
        var existing = new CalendarTemplateRecord(Guid.NewGuid(), TemplateName);
        repo.Items.Add(existing);
        Assert.Equal(existing.Id, (await Run(repo, Config(existing.Id, null))).Id);
        var wrongName = await Assert.ThrowsAsync<CalendarImportException>(() => Run(repo, Config(existing.Id, "Otro")));
        Assert.Equal(ImportExitCodes.CalendarTemplate, wrongName.ExitCode);
        var missing = await Assert.ThrowsAsync<CalendarImportException>(() => Run(repo, Config(Guid.NewGuid(), null)));
        Assert.Equal(ImportExitCodes.CalendarTemplate, missing.ExitCode);
    }

    [Fact]
    public async Task No_crea_la_plantilla_si_no_existe_y_falla_con_exit_4()
    {
        var repo = new FakeTemplates();
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => Run(repo, Config()));
        Assert.Equal(ImportExitCodes.CalendarTemplate, ex.ExitCode);
        Assert.Contains(TemplateName, ex.Message);
        Assert.Contains("manualmente", ex.Message);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task Dos_plantillas_con_el_mismo_nombre_es_un_error_de_ambiguedad()
    {
        var repo = new FakeTemplates();
        repo.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), TemplateName));
        repo.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), TemplateName.ToUpperInvariant()));
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => Run(repo, Config()));
        Assert.Equal(ImportExitCodes.CalendarTemplate, ex.ExitCode);
        Assert.Contains("exactamente una", ex.Message);
        Assert.Equal(2, repo.Items.Count);
    }

    [Fact]
    public async Task Sin_correspondencia_configurada_explica_fingerprint_y_como_configurarla()
    {
        var repo = new FakeTemplates();
        repo.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), TemplateName));
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => Run(repo, new CalendarImportSettings()));
        Assert.Equal(ImportExitCodes.CalendarTemplate, ex.ExitCode);
        Assert.Contains(Weekly().Fingerprint, ex.Message);
        Assert.Contains("CalendarImport.CalendarTemplates", ex.Message);
    }

    [Fact]
    public void La_configuracion_de_plantillas_se_valida()
    {
        var ok = Config();
        ok.Validate();
        var badFingerprint = new CalendarImportSettings { CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = "abc", Name = "x" }] };
        Assert.Throws<InvalidOperationException>(badFingerprint.Validate);
        var noTarget = new CalendarImportSettings { CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = Weekly().Fingerprint }] };
        Assert.Throws<InvalidOperationException>(noTarget.Validate);
        var duplicated = Config();
        duplicated.CalendarTemplates.Add(new CalendarTemplateMapping { Fingerprint = Weekly().Fingerprint.ToLowerInvariant(), Name = "y" });
        Assert.Throws<InvalidOperationException>(duplicated.Validate);
    }

    [Fact]
    public void Backoff_2_4_8_16_30()
        => Assert.Equal([2, 4, 8, 16, 30], ProjectCalendarResolver.BuildDelays(5, 2).Select(d => (int)d.TotalSeconds));
}

public class OrchestratorTests
{
    private static string Json(object[]? exceptions = null, object[]? workWeeks = null)
        => Build([Calendar(1, "TBT", Tbt(), workWeeks)], exceptions);

    private const string TbtTemplateName = "MPP - Calendario 08-17 V08-13:30";
    private static readonly string TbtFingerprint = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(Project([Calendar(1, "TBT", Tbt())]))).Fingerprint;

    /// <summary>Configuración con la plantilla manual correspondiente al horario TBT.</summary>
    private static CalendarImportSettings TbtSettings()
        => new() { CalendarTemplates = [new CalendarTemplateMapping { Fingerprint = TbtFingerprint, Name = TbtTemplateName }] };

    /// <summary>Repositorio que contiene la plantilla manual (el importador no puede crearla).</summary>
    private static FakeTemplates NewTemplates()
    {
        var t = new FakeTemplates();
        t.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), TbtTemplateName));
        return t;
    }

    private static (ProjectImportOrchestrator Orchestrator, FakeTemplates T, FakeProjects P, FakeCalendars C, StringWriter Out, string StatePath)
        Setup(CalendarImportSettings? settings = null, string? statePath = null, FakeTemplates? t = null, FakeProjects? p = null, FakeCalendars? c = null)
    {
        t ??= NewTemplates();
        p ??= new FakeProjects();
        c ??= new FakeCalendars();
        var w = new StringWriter();
        statePath ??= Path.Combine(Files.TempDir(), "state.json");
        return (new ProjectImportOrchestrator(settings ?? TbtSettings(), t, p, c, new ImportStateService(), w, (_, _) => Task.CompletedTask), t, p, c, w, statePath);
    }

    private static CalendarImportRequest Request(string json, string statePath)
        => new("x.json", "x.schema.json", statePath, new string('A', 64), MppProjectReader.Parse(json));

    private static readonly object[] WorkWeek =
    [
        new
        {
            Name = "Intensiva", From = "2025-07-01", To = "2025-09-01",
            Days = new object[] { WwDay("Monday", "Working", ["08:00-15:00"]), WwDay("Tuesday", "Default", []), WwDay("Wednesday", "Default", []), WwDay("Thursday", "Default", []),
                                  WwDay("Friday", "Default", []), WwDay("Saturday", "Default", []), WwDay("Sunday", "Default", []) }
        }
    ];

    [Fact]
    public async Task Flujo_completo_usa_plantilla_manual_crea_proyecto_workweeks_y_excepciones_y_no_importa_tareas()
    {
        var (o, t, p, c, w, state) = Setup();
        var json = Json([Exception(1, "Festivo", "2025-01-06", "2025-01-06"), Exception(1, "Navidad", "2025-12-25", "2025-12-25")], WorkWeek);
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        var result = await o.RunAsync(Request(json, state), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(t.Items);                        // la plantilla manual no se crea ni se duplica
        Assert.Equal(t.Items[0].Id, ImportState.Load(state)!.Calendar!.CalendarTemplateId);
        Assert.False(ImportState.Load(state)!.Calendar!.CalendarTemplateCreated);
        Assert.Equal(1, p.CreateCalls);
        Assert.Single(c.CreatedWorkWeeks);
        Assert.Equal(2, c.CreatedExceptions.Count);
        var text = w.ToString();
        Assert.Contains("Import stopped after project calendar configuration because CalendarImport.StopAfterCalendar=true.", text);
        Assert.Contains("No Tasks, Dependencies, Resources or Assignments were imported.", text);
        var saved = ImportState.Load(state)!;
        Assert.Equal(["SchemaValidation", "CalendarResolution", "CalendarTemplateProvisioning", "ProjectCreation", "ProjectCalendarResolution", "WorkWeeks", "CalendarExceptions"], saved.CompletedStages);
        Assert.True(saved.SchemaValidated);
        Assert.Empty(saved.TaskIds);
        Assert.Empty(saved.CreatedTasks);
    }

    [Fact] // 13, 14, 15
    public async Task Reanudar_con_el_mismo_state_no_duplica_proyecto_plantilla_workweeks_ni_excepciones()
    {
        var (o, t, p, c, _, state) = Setup();
        var json = Json([Exception(1, "Festivo", "2025-01-06", "2025-01-06")], WorkWeek);
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        await o.RunAsync(Request(json, state), CancellationToken.None);
        var (o2, _, _, _, _, _) = Setup(null, state, t, p, c);
        var wwCalls = c.EnsureWorkWeekCalls;
        var exCalls = c.EnsureExceptionCalls;
        var second = await o2.RunAsync(Request(json, state), CancellationToken.None);

        Assert.Single(t.Items);
        Assert.Equal(1, p.CreateCalls);
        Assert.Equal(wwCalls, c.EnsureWorkWeekCalls);   // no vuelve a consultar/crear
        Assert.Equal(exCalls, c.EnsureExceptionCalls);
        Assert.Equal(1, second.WorkWeeks.Reused);
        Assert.Equal(1, second.Exceptions.Reused);
        Assert.Equal(0, second.WorkWeeks.Created + second.Exceptions.Created);
    }

    [Fact] // 14, 15
    public async Task WorkWeek_y_excepcion_ya_existentes_en_Dataverse_no_se_duplican()
    {
        var (o, _, _, c, _, state) = Setup();
        var json = Json([Exception(1, "Festivo", "2025-01-06", "2025-01-06")], WorkWeek);
        var project = MppProjectReader.Parse(json);
        var calendar = MppCalendarResolver.Resolve(project);
        c.Pattern = MppCalendarNormalizer.NormalizeBase(calendar).CanonicalPattern;
        c.ExistingWorkWeeks.Add(MppCalendarNormalizer.NormalizeWorkWeeks(calendar, []).Single().Key);
        c.ExistingExceptions.Add(MppExceptionNormalizer.Normalize(project, calendar, new CalendarImportSettings(), []).Single().Key);
        var result = await o.RunAsync(Request(json, state), CancellationToken.None);
        Assert.Empty(c.CreatedWorkWeeks);
        Assert.Empty(c.CreatedExceptions);
        Assert.Equal(1, result.WorkWeeks.Reused);
        Assert.Equal(1, result.Exceptions.Reused);
    }

    [Fact]
    public async Task Un_proyecto_del_state_que_ya_no_existe_es_un_error_y_no_se_recrea()
    {
        var (o, t, p, c, _, state) = Setup();
        var json = Json();
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        await o.RunAsync(Request(json, state), CancellationToken.None);
        p.Existing.Clear();
        var (o2, _, _, _, _, _) = Setup(null, state, t, p, c);
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o2.RunAsync(Request(json, state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.ProjectCreation, ex.ExitCode);
        Assert.Equal(1, p.CreateCalls);
    }

    [Fact]
    public async Task Metadatos_sin_confirmar_fallan_antes_de_crear_plantilla_o_proyecto()
    {
        var (o, _, p, _, _, state) = Setup(t: new FakeTemplates { Configured = false });
        var ex = await Assert.ThrowsAsync<CalendarMetadataNotConfiguredException>(() => o.RunAsync(Request(Json(), state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.CalendarTemplate, ex.ExitCode);
        Assert.Contains("Run calendar-metadata and configure CalendarImport.DataverseMetadata.", ex.Message);
        Assert.Equal(0, p.CreateCalls);
    }

    [Fact]
    public async Task El_proyecto_se_crea_con_GUID_propio_y_guarda_el_GUID_de_Project_Desktop_en_su_campo()
    {
        var (o, _, p, c, _, state) = Setup();
        var json = Json();
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        var result = await o.RunAsync(Request(json, state), CancellationToken.None);
        Assert.NotEqual(Guid.Parse(Dump.SourceGuid), result.ProjectId);                  // GUID aleatorio de Project Operations
        Assert.Equal([Guid.Parse(Dump.SourceGuid)], p.CreatedSourceGuids);                 // el del MPP va al campo axz_mppguid
        Assert.Equal(result.ProjectId, p.BySourceGuid[Guid.Parse(Dump.SourceGuid)]);
        Assert.Equal(["x.json"], p.SourceFileNames);      // solo el nombre del fichero, para guardarlo en el proyecto
    }

    [Fact]
    public async Task Si_el_proyecto_ya_existe_en_Dataverse_por_el_GUID_del_MPP_se_reutiliza_sin_state_y_sin_crear()
    {
        var json = Json();
        var (o, t, p, c, w, state) = Setup();
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        var existing = Guid.NewGuid();
        p.Existing.Add(existing);
        p.BySourceGuid[Guid.Parse(Dump.SourceGuid)] = existing;
        p.LinkedTemplates[existing] = t.Items[0].Id;
        var result = await o.RunAsync(Request(json, state), CancellationToken.None);
        Assert.Equal(existing, result.ProjectId);
        Assert.Equal(0, p.CreateCalls);
        Assert.Contains("REUSED (found in Dataverse by MPP GUID)", w.ToString());
    }

    [Fact]
    public async Task Un_proyecto_borrado_ya_no_se_encuentra_y_se_crea_uno_nuevo_con_otro_GUID()
    {
        var json = Json();
        var (o, _, p, c, _, state) = Setup();
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        var first = await o.RunAsync(Request(json, state), CancellationToken.None);
        p.BySourceGuid.Clear();                                                          // el proyecto se borra en Dataverse
        p.Existing.Clear();
        var (o2, _, _, c2, _, state2) = Setup(null, null, null, p, c);
        var second = await o2.RunAsync(Request(json, state2), CancellationToken.None);
        Assert.Equal(2, p.CreateCalls);
        Assert.NotEqual(first.ProjectId, second.ProjectId);                              // no reutiliza el GUID del proyecto borrado
    }

    [Fact]
    public async Task Si_el_proyecto_existente_tiene_otra_plantilla_se_detiene_sin_modificarlo()
    {
        var (o, _, p, _, _, state) = Setup();
        var existing = Guid.NewGuid();
        p.Existing.Add(existing);
        p.BySourceGuid[Guid.Parse(Dump.SourceGuid)] = existing;
        p.LinkedTemplates[existing] = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o.RunAsync(Request(Json(), state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.ProjectCreation, ex.ExitCode);
        Assert.Equal(0, p.CreateCalls);
    }

    [Fact]
    public async Task Sin_GUID_de_proyecto_en_el_JSON_no_se_crea_nada()
    {
        var json = Json().Replace(Dump.SourceGuid, "no-es-un-guid");
        var (o, _, p, _, _, state) = Setup();
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o.RunAsync(Request(json, state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.ProjectCreation, ex.ExitCode);
        Assert.Contains("GUID", ex.Message);
        Assert.Equal(0, p.CreateCalls);
    }

    [Fact]
    public async Task Sin_plantilla_manual_no_se_crea_ni_el_proyecto_ni_la_plantilla()
    {
        var (o, t, p, c, _, state) = Setup(t: new FakeTemplates());
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o.RunAsync(Request(Json(), state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.CalendarTemplate, ex.ExitCode);
        Assert.Empty(t.Items);
        Assert.Equal(0, p.CreateCalls);
        Assert.Equal(0, c.LookupCalls);
    }

    [Fact]
    public async Task Si_la_configuracion_apunta_a_otra_plantilla_que_la_del_proyecto_ya_creado_se_detiene()
    {
        var json = Json();
        var (o, t, p, c, _, state) = Setup();
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        await o.RunAsync(Request(json, state), CancellationToken.None);
        var other = new FakeTemplates();
        other.Items.Add(new CalendarTemplateRecord(Guid.NewGuid(), TbtTemplateName));
        var (o2, _, _, _, _, _) = Setup(null, state, other, p, c);
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o2.RunAsync(Request(json, state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.CalendarTemplate, ex.ExitCode);
        Assert.Equal(1, p.CreateCalls);
    }

    [Fact]
    public async Task El_calendario_del_proyecto_se_reintenta_y_si_no_aparece_no_aplica_nada()
    {
        var c = new FakeCalendars { MissingAttempts = 99 };
        var json = Json([Exception(1, "Festivo", "2025-01-06", "2025-01-06")], WorkWeek);
        var (o, _, _, _, _, state) = Setup(c: c);
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o.RunAsync(Request(json, state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.ProjectCalendar, ex.ExitCode);
        Assert.Equal(5, c.LookupCalls);
        Assert.Equal(0, c.EnsureWorkWeekCalls + c.EnsureExceptionCalls);
    }

    [Fact]
    public async Task El_calendario_del_proyecto_con_patron_distinto_a_la_plantilla_es_un_error()
    {
        var c = new FakeCalendars { Pattern = "MON=OFF" };
        var (o, _, _, _, _, state) = Setup(c: c);
        var ex = await Assert.ThrowsAsync<CalendarImportException>(() => o.RunAsync(Request(Json(), state), CancellationToken.None));
        Assert.Equal(ImportExitCodes.ProjectCalendar, ex.ExitCode);
    }

    [Fact]
    public async Task State_de_otro_json_se_rechaza()
    {
        var (o, _, _, c, _, state) = Setup();
        var json = Json();
        c.Pattern = MppCalendarNormalizer.NormalizeBase(MppCalendarResolver.Resolve(MppProjectReader.Parse(json))).CanonicalPattern;
        await o.RunAsync(Request(json, state), CancellationToken.None);
        var other = new CalendarImportRequest("y.json", "y.schema.json", state, new string('B', 64), MppProjectReader.Parse(json));
        await Assert.ThrowsAsync<CalendarImportException>(() => o.RunAsync(other, CancellationToken.None));
    }

    [Fact]
    public void Un_state_anterior_sin_los_campos_nuevos_sigue_cargando()
    {
        var path = Path.Combine(Files.TempDir(), "old.json");
        File.WriteAllText(path, "{\"projectId\":\"11111111-1111-1111-1111-111111111111\",\"bucketId\":\"22222222-2222-2222-2222-222222222222\",\"createdTasks\":[1,2]}");
        var state = ImportState.Load(path)!;
        Assert.Equal(2, state.CreatedTasks.Count);
        Assert.Null(state.Calendar);
    }
}

public class ImportCommandTests
{
    [Fact] // 17
    public async Task Si_falla_el_schema_no_se_crea_ningun_servicio_de_Dataverse()
    {
        var dir = Files.TempDir();
        var json = Path.Combine(dir, "bad.json");
        File.WriteAllText(json, "{\"SourceFile\": 123}");
        var factoryCalled = false;
        var result = await CalendarImportCommand.RunAsync(json, Files.Schema(), new CalendarImportSettings(), Path.Combine(dir, "s.json"),
            () => { factoryCalled = true; throw new InvalidOperationException("no debe conectar"); }, TextWriter.Null, TextWriter.Null, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(factoryCalled);
        Assert.False(File.Exists(Path.Combine(dir, "s.json")));
    }

    [Fact]
    public async Task Json_inexistente_o_schema_inexistente_terminan_sin_conectar()
    {
        var factoryCalled = false;
        Func<CalendarImportServices> factory = () => { factoryCalled = true; throw new InvalidOperationException(); };
        var dir = Files.TempDir();
        var r1 = await CalendarImportCommand.RunAsync(Path.Combine(dir, "nope.json"), null, new CalendarImportSettings(), "s.json", factory, TextWriter.Null, TextWriter.Null, CancellationToken.None);
        File.WriteAllText(Path.Combine(dir, "a.json"), "{}");
        var r2 = await CalendarImportCommand.RunAsync(Path.Combine(dir, "a.json"), null, new CalendarImportSettings(), "s.json", factory, TextWriter.Null, TextWriter.Null, CancellationToken.None);
        Assert.NotEqual(0, r1.ExitCode);
        Assert.NotEqual(0, r2.ExitCode); // no hay project-dump.schema.json junto al JSON
        Assert.False(factoryCalled);
    }

    [Fact]
    public void El_schema_se_resuelve_junto_al_json_si_no_se_indica()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath("C:\\x"), "project-dump.schema.json"), CalendarImportCommand.ResolveSchemaPath("C:\\x\\a.json", null), ignoreCase: true);
        Assert.Equal("S.json", CalendarImportCommand.ResolveSchemaPath("C:\\x\\a.json", "S.json"));
    }

    [Fact]
    public void Los_repositorios_Dataverse_pendientes_fallan_con_mensaje_especifico_sin_valores_inventados()
    {
        var settings = new CalendarImportSettings();
        var ex = Assert.Throws<CalendarMetadataNotConfiguredException>(() => new DataverseCalendarTemplateRepository(null!, settings).EnsureConfigured());
        Assert.StartsWith("Calendar Template entity metadata has not been confirmed", ex.Message);
        Assert.EndsWith("Run calendar-metadata and configure CalendarImport.DataverseMetadata.", ex.Message);
        Assert.Contains("CalendarTemplateEntitySet", ex.MissingKeys);
        Assert.All(new[]
        {
            settings.DataverseMetadata.CalendarTemplateEntitySet, settings.DataverseMetadata.CalendarTemplateIdAttribute, settings.DataverseMetadata.CalendarTemplateNameAttribute,
            settings.DataverseMetadata.ProjectCalendarAttribute, settings.DataverseMetadata.ProjectCalendarTemplateAttribute,
            settings.DataverseMetadata.CalendarEntitySet, settings.DataverseMetadata.CalendarRuleEntitySet
        }, Assert.Null);
    }

    [Fact]
    public void Los_periodos_del_calendario_interno_se_calculan_como_laborable_menos_descansos()
    {
        // Formato observado en la plantilla manual: 09:00-19:00 laborable (subcode 1) menos 13:00-15:00 (subcode 4).
        var split = System.Text.Json.Nodes.JsonNode.Parse("[{\"offset\":540,\"duration\":600,\"subcode\":1},{\"offset\":780,\"duration\":120,\"subcode\":4}]")!.AsArray();
        Assert.Equal(["09:00-13:00", "15:00-19:00"], DataverseProjectCalendarRepository.ComputePeriods(split).Select(p => p.ToString()));
        var single = System.Text.Json.Nodes.JsonNode.Parse("[{\"offset\":480,\"duration\":330,\"subcode\":1}]")!.AsArray();
        Assert.Equal(["08:00-13:30"], DataverseProjectCalendarRepository.ComputePeriods(single).Select(p => p.ToString()));
    }

    [Fact]
    public void Valores_predeterminados_de_configuracion()
    {
        var s = new CalendarImportSettings();
        Assert.True(s.StopAfterCalendar);
        Assert.Equal("Expand", s.RecurringExceptionsMode);
        Assert.Empty(s.CalendarTemplates);
        Assert.Equal(1000, s.MaxExpandedOccurrencesPerRule);
        Assert.Equal(5, s.ProjectCalendarMaxRetries);
        s.Validate();
        s.RecurringExceptionsMode = "Otra";
        Assert.Throws<InvalidOperationException>(s.Validate);
    }
}

/// <summary>Integración con los JSON reales: se ejecuta solo si PROJECTOPS_SAMPLE_JSON_DIR apunta a la carpeta (si no, no hace nada).</summary>
public class SampleFilesIntegrationTests
{
    [Fact]
    public void Todos_los_ficheros_de_ejemplo_se_normalizan_sin_error()
    {
        var dir = Environment.GetEnvironmentVariable("PROJECTOPS_SAMPLE_JSON_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*_project-dump.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var project = MppProjectReader.ReadFile(file);
            var calendar = MppCalendarResolver.Resolve(project);
            var weekly = MppCalendarNormalizer.NormalizeBase(calendar);
            var warnings = new List<string>();
            var ww = MppCalendarNormalizer.NormalizeWorkWeeks(calendar, warnings);
            var ex = MppExceptionNormalizer.Normalize(project, calendar, new CalendarImportSettings(), warnings);
            Console.WriteLine($"{Path.GetFileName(file)}: {CalendarTemplateNaming.BuildName("MPP - Calendario", weekly, null)} | WW={ww.Count} EX={ex.Count} warnings={warnings.Count}");
        }
    }
}
