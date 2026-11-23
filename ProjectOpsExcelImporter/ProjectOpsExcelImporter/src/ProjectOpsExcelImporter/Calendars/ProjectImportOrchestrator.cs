using Microsoft.Identity.Client;

namespace ProjectOpsExcelImporter.Calendars;

public sealed class CalendarImportState
{
    public int MppCalendarUniqueId { get; set; }
    public string? MppCalendarName { get; set; }
    public string? NormalizedPattern { get; set; }
    public string? Fingerprint { get; set; }
    public Guid? CalendarTemplateId { get; set; }
    public string? CalendarTemplateName { get; set; }
    /// <summary>Obsoleto: el importador ya no crea plantillas. Se conserva para leer states anteriores; siempre false.</summary>
    public bool CalendarTemplateCreated { get; set; }
    public Guid? ProjectCalendarId { get; set; }
    public int WorkWeeksApplied { get; set; }
    public int ExceptionsApplied { get; set; }
    public HashSet<string> AppliedWorkWeekKeys { get; set; } = [];
    public HashSet<string> AppliedExceptionKeys { get; set; } = [];
}

public sealed class ProjectImportState
{
    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public Guid? DefaultBucketId { get; set; }
    public string? CreationMethod { get; set; }
}

public static class ImportStages
{
    public const string SchemaValidation = "SchemaValidation";
    public const string CalendarResolution = "CalendarResolution";
    public const string CalendarTemplateProvisioning = "CalendarTemplateProvisioning";
    public const string ProjectCreation = "ProjectCreation";
    public const string ProjectCalendarResolution = "ProjectCalendarResolution";
    public const string WorkWeeks = "WorkWeeks";
    public const string CalendarExceptions = "CalendarExceptions";
    public const string Tasks = "Tasks";
    public const string Dependencies = "Dependencies";
    public const string Resources = "Resources";
}

/// <summary>Carga/guarda el state reutilizando <see cref="ImportState"/> (formato ampliado, compatible hacia atrás).</summary>
public sealed class ImportStateService
{
    public ImportState LoadOrCreate(string path, string sourceJson, string sourceHash)
    {
        var state = ImportState.Load(path) ?? new ImportState();
        if (state.SourceHash is not null && !state.SourceHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new CalendarImportException(ImportExitCodes.Functional, ImportStages.SchemaValidation,
                $"El state '{path}' pertenece a otro JSON (hash distinto: {state.SourceHash[..12]}… vs {sourceHash[..12]}…). Usa otro --state o bórralo.");
        state.SourceJson = sourceJson;
        state.SourceHash = sourceHash;
        state.CompletedStages ??= [];
        state.Calendar ??= new CalendarImportState();
        state.Project ??= new ProjectImportState();
        return state;
    }

    public void Save(ImportState state, string path) => state.Save(path);

    public static void MarkStage(ImportState state, string stage)
    {
        state.CompletedStages ??= [];
        if (!state.CompletedStages.Contains(stage)) state.CompletedStages.Add(stage);
    }

    public static bool IsDone(ImportState state, string stage) => state.CompletedStages?.Contains(stage) == true;
}

public sealed record CalendarImportRequest(string JsonPath, string SchemaPath, string StatePath, string SourceHash, MppProject Project);

/// <summary>Orquesta las fases de calendario. No contiene acceso a Dataverse: usa las interfaces.</summary>
public sealed class ProjectImportOrchestrator
{
    private readonly CalendarImportSettings _settings;
    private readonly ICalendarTemplateRepository _templates;
    private readonly IProjectOperationsProjectService _projects;
    private readonly IProjectCalendarRepository _calendars;
    private readonly IProjectTaskService? _tasks;
    private readonly IProjectDependencyService? _dependencies;
    private readonly IProjectResourceService? _resources;
    private readonly ImportStateService _stateService;
    private readonly TextWriter _out;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;

    public ProjectImportOrchestrator(
        CalendarImportSettings settings, ICalendarTemplateRepository templates, IProjectOperationsProjectService projects,
        IProjectCalendarRepository calendars, ImportStateService stateService, TextWriter output,
        Func<TimeSpan, CancellationToken, Task>? delay = null, IProjectTaskService? tasks = null, IProjectDependencyService? dependencies = null,
        IProjectResourceService? resources = null)
    {
        _tasks = tasks;
        _dependencies = dependencies;
        _resources = resources;
        _settings = settings;
        _templates = templates;
        _projects = projects;
        _calendars = calendars;
        _stateService = stateService;
        _out = output;
        _delay = delay;
    }

    public async Task<CalendarImportResult> RunAsync(CalendarImportRequest request, CancellationToken cancellationToken)
    {
        var bar = new string('=', 60);
        _out.WriteLine(bar);
        _out.WriteLine("PROJECT OPERATIONS IMPORT");
        _out.WriteLine(bar);
        Line("Source JSON", request.JsonPath, 0);
        Line("Schema", request.SchemaPath, 0);

        var state = _stateService.LoadOrCreate(request.StatePath, request.JsonPath, request.SourceHash);
        void Save() => _stateService.Save(state, request.StatePath);
        var warnings = new List<string>();
        var workWeekCounters = new StageCounters();
        var exceptionCounters = new StageCounters();
        var stage = ImportStages.SchemaValidation;
        // Tiempos por fase: se imprimen en el bloque [Timing] al final (también si la importación falla).
        var total = System.Diagnostics.Stopwatch.StartNew();
        var lap = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<(string Phase, TimeSpan Elapsed)>();
        void Lap(string phase) { timings.Add((phase, lap.Elapsed)); lap.Restart(); }
        var exceptionCount = 0;
        ProjectResourceImporter? resourceImporter = null;
        try
        {
            // [1/8] El schema ya fue validado por el comando antes de llegar aquí (y antes de cualquier conexión).
            _out.WriteLine();
            _out.WriteLine("[1/8] JSON Schema validation");
            Line("Result", "VALID");
            state.SchemaValidated = true;
            ImportStateService.MarkStage(state, ImportStages.SchemaValidation);
            Save();

            // [2/8] Resolución offline (sin Dataverse): calendario, WorkWeeks y excepciones.
            stage = ImportStages.CalendarResolution;
            var project = request.Project;
            if (string.IsNullOrWhiteSpace(project.Title))
                throw new CalendarImportException(ImportExitCodes.Functional, stage, $"{MppProjectReader.PropTitle} está vacío; no se puede nombrar el proyecto.");
            var mppCalendar = MppCalendarResolver.Resolve(project);
            var weekly = MppCalendarNormalizer.NormalizeBase(mppCalendar);
            var workWeeks = _settings.ApplyWorkWeeks ? MppCalendarNormalizer.NormalizeWorkWeeks(mppCalendar, warnings) : [];
            var exceptions = _settings.ApplyCalendarExceptions ? MppExceptionNormalizer.Normalize(project, mppCalendar, _settings, warnings) : [];
            exceptionCount = exceptions.Count;

            var cal = state.Calendar!;
            if (cal.Fingerprint is not null && cal.Fingerprint != weekly.Fingerprint)
                throw new CalendarImportException(ImportExitCodes.Functional, stage, "El horario base del JSON no coincide con el fingerprint guardado en el state.");
            cal.MppCalendarUniqueId = mppCalendar.UniqueId;
            cal.MppCalendarName = mppCalendar.Name;
            cal.NormalizedPattern = weekly.CanonicalPattern;
            cal.Fingerprint = weekly.Fingerprint;
            _out.WriteLine();
            _out.WriteLine("[2/8] MPP project calendar");
            Line("Name", mppCalendar.Name);
            Line("UniqueId", mppCalendar.UniqueId.ToString());
            Line("Pattern", weekly.DisplayPattern);
            Line("Fingerprint", weekly.Fingerprint[..16] + "...");
            ImportStateService.MarkStage(state, ImportStages.CalendarResolution);
            Save();

            // Metadatos sin confirmar: se detecta ANTES de crear nada y antes de conectar.
            stage = "MetadataCheck";
            Preflight();
            Lap("Offline resolution + metadata check");

            // [3/8] Calendar Template
            stage = ImportStages.CalendarTemplateProvisioning;
            var template = await ResolveTemplateAsync(state, weekly, cancellationToken).ConfigureAwait(false);
            cal.CalendarTemplateId = template.Id;
            cal.CalendarTemplateName = template.Name;
            ImportStateService.MarkStage(state, stage);
            Save();
            _out.WriteLine();
            _out.WriteLine("[3/8] Calendar Template (manual; never created by the importer)");
            Line("Name", template.Name);
            Line("Result", template.Result);
            Line("Id", template.Id.ToString("D"));
            if (template.Warning is not null) Line("Note", template.Warning);
            Lap("Calendar template lookup (incl. sign-in)");

            // [4/8] Proyecto
            stage = ImportStages.ProjectCreation;
            var proj = state.Project!;
            string projectResult;
            if (proj.ProjectId is { } existingId)
            {
                if (!await _projects.ProjectExistsAsync(existingId, cancellationToken).ConfigureAwait(false))
                    throw new CalendarImportException(ImportExitCodes.ProjectCreation, stage,
                        $"El proyecto {existingId:D} guardado en el state ya no existe en Dataverse. Borra el state para empezar de nuevo; no se recrea automáticamente.");
                projectResult = "REUSED (state)";
            }
            else
            {
                // El GUID de Project Desktop se guarda en un campo del proyecto (el msdyn_projectid es propio de Project Operations):
                // si ya hay un proyecto con ese GUID se reutiliza, no se duplica. Un proyecto borrado ya no aparece, y se crea uno nuevo.
                var mppGuid = project.ProjectGuid ?? throw new CalendarImportException(ImportExitCodes.ProjectCreation, stage,
                    $"El JSON no contiene un GUID de proyecto válido ({MppProjectReader.PropGuid}); no se puede localizar ni crear el proyecto.");
                if (await _projects.FindProjectBySourceGuidAsync(mppGuid, cancellationToken).ConfigureAwait(false) is { } foundId)
                {
                    var linked = await _projects.GetLinkedTemplateIdAsync(foundId, cancellationToken).ConfigureAwait(false);
                    if (linked != template.Id)
                        throw new CalendarImportException(ImportExitCodes.ProjectCreation, stage,
                            $"El proyecto {foundId:D} (GUID MPP {mppGuid:D}) ya existe en Dataverse con la Calendar Template {(linked?.ToString("D") ?? "(ninguna)")} y la configuración resuelve {template.Id:D} ('{template.Name}'). " +
                            "No se modifica un proyecto existente.");
                    proj.ProjectId = foundId;
                    proj.ProjectName = project.Title;
                    proj.CreationMethod = "existing (found by MPP GUID)";
                    state.ProjectId = foundId;
                    projectResult = "REUSED (found in Dataverse by MPP GUID)";
                }
                else
                {
                    var created = await _projects.CreateProjectAsync(project.Title!, project.StartDate, template, mppGuid, Path.GetFileName(request.JsonPath), cancellationToken).ConfigureAwait(false);
                    proj.ProjectId = created.ProjectId;
                    proj.ProjectName = created.ProjectName;
                    proj.DefaultBucketId = created.DefaultBucketId;
                    proj.CreationMethod = created.CreationMethod;
                    state.ProjectId = created.ProjectId;                       // compatibilidad con el flujo de tareas existente
                    if (created.DefaultBucketId is { } b) state.BucketId = b;
                    projectResult = "CREATED";
                }
            }
            ImportStateService.MarkStage(state, stage);
            Save();
            _out.WriteLine();
            _out.WriteLine("[4/8] Project");
            Line("Name", proj.ProjectName ?? project.Title!);
            Line("Result", projectResult);
            Line("ProjectId", proj.ProjectId!.Value.ToString("D"));
            Line("BucketId", proj.DefaultBucketId?.ToString("D") ?? "(n/a)");
            if (proj.CreationMethod is not null) Line("Method", proj.CreationMethod);

            // Inicio real del proyecto = "Comienzo" de la primera fila del MPP (la tarea resumen del proyecto), en msdyn_actualstart.
            var summaryStart = project.Tasks.FirstOrDefault(t => t.IsProjectSummaryTask)?.Start;
            if (summaryStart is { } mppStart)
            {
                try
                {
                    var actual = await _projects.EnsureProjectActualStartAsync(proj.ProjectId.Value, mppStart, cancellationToken).ConfigureAwait(false);
                    Line("Actual start", $"{mppStart:yyyy-MM-dd HH:mm} (MPP first row 'Comienzo') -> msdyn_actualstart: {(actual.Changed ? $"SET (before {(actual.Before is { } b0 ? b0.ToString("yyyy-MM-dd HH:mm") : "empty")})" : "REUSED (already equal)")}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not CalendarImportException)
                {
                    throw new CalendarImportException(ImportExitCodes.ProjectCreation, stage, $"No se pudo guardar msdyn_actualstart del proyecto: {ex.Message}", ex);
                }
            }
            else
            {
                Line("Actual start", "WARNING: the JSON has no project summary task (first row); msdyn_actualstart not set");
                warnings.Add("El JSON no tiene la tarea resumen del proyecto (primera fila): msdyn_actualstart no se informa.");
            }

            // Para importar el avance de las tareas el proyecto tiene que permitir actualizar el porcentaje completado (nace con false).
            if (_settings.ImportProgress)
            {
                try
                {
                    var allow = await _projects.EnsureAllowPercentCompleteUpdateAsync(proj.ProjectId.Value, cancellationToken).ConfigureAwait(false);
                    Line("Allow % update", $"msdyn_allowpercentcompleteupdate = true: {(allow.Changed ? $"SET (before {(allow.Before is { } b1 ? b1.ToString().ToLowerInvariant() : "empty")})" : "REUSED (already true)")}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not CalendarImportException)
                {
                    throw new CalendarImportException(ImportExitCodes.ProjectCreation, stage, $"No se pudo activar msdyn_allowpercentcompleteupdate en el proyecto: {ex.Message}", ex);
                }
            }
            Lap("Project (create or find + actual start)");

            // [5/8] Calendario propio del proyecto
            stage = ImportStages.ProjectCalendarResolution;
            var resolver = new ProjectCalendarResolver(_calendars, _settings, _delay);
            var hadCalendar = cal.ProjectCalendarId.HasValue;
            var info = await resolver.ResolveAsync(proj.ProjectId.Value, weekly.CanonicalPattern, hadCalendar, cancellationToken).ConfigureAwait(false);
            cal.ProjectCalendarId = info.CalendarId;
            ImportStateService.MarkStage(state, stage);
            Save();
            _out.WriteLine();
            _out.WriteLine("[5/8] Project calendar");
            Line("Result", "FOUND");
            Line("CalendarId", info.CalendarId.ToString("D"));
            Lap("Project calendar (wait + lookup)");

            // [6/8] WorkWeeks
            stage = ImportStages.WorkWeeks;
            _out.WriteLine();
            _out.WriteLine("[6/8] WorkWeeks");
            if (_settings.ApplyWorkWeeks)
            {
                try { await new ProjectWorkWeekImporter(_calendars).ApplyAsync(info.CalendarId, workWeeks, cal, workWeekCounters, Save, cancellationToken).ConfigureAwait(false); }
                finally { PrintCounters(workWeekCounters); }
                ImportStateService.MarkStage(state, stage);
                Save();
            }
            else Line("Result", "SKIPPED (CalendarImport.ApplyWorkWeeks=false)");
            Lap("WorkWeeks");

            // [7/8] CalendarExceptions
            stage = ImportStages.CalendarExceptions;
            _out.WriteLine();
            _out.WriteLine("[7/8] Calendar exceptions");
            if (_settings.ApplyCalendarExceptions)
            {
                try { await new ProjectCalendarExceptionImporter(_calendars).ApplyAsync(info.CalendarId, exceptions, cal, exceptionCounters, Save, cancellationToken).ConfigureAwait(false); }
                finally { PrintCounters(exceptionCounters); }
                ImportStateService.MarkStage(state, stage);
                Save();
            }
            else Line("Result", "SKIPPED (CalendarImport.ApplyCalendarExceptions=false)");
            Lap($"Calendar exceptions ({exceptionCount})");

            // [Tasks] Solo con CalendarImport.ImportTasks=true: estructura básica de tareas y comparación con lo que calcula Project Operations.
            if (_settings.ImportTasks)
            {
                stage = ImportStages.Tasks;
                var taskService = _tasks ?? throw new CalendarImportException(ImportExitCodes.Tasks, stage, "CalendarImport.ImportTasks=true pero no hay servicio de tareas configurado.");
                _out.WriteLine();
                _out.WriteLine("[Tasks] Basic task structure (name, hierarchy, start, duration; no dependencies, resources, assignments, costs or progress)");
                await new ProjectTaskImporter(taskService, _settings, _out, _delay).RunAsync(proj.ProjectId.Value, project, exceptions, state, Save, cancellationToken).ConfigureAwait(false);
                Lap("Tasks (create + notes + read-back)");
                ImportStateService.MarkStage(state, stage);
                Save();
            }

            // [Dependencies] Solo con CalendarImport.ImportDependencies=true: relaciones entre las tareas ya creadas (tipo y retardo).
            if (_settings.ImportDependencies)
            {
                stage = ImportStages.Dependencies;
                var dependencyService = _dependencies ?? throw new CalendarImportException(ImportExitCodes.Dependencies, stage, "CalendarImport.ImportDependencies=true pero no hay servicio de dependencias configurado.");
                var taskService = _tasks ?? throw new CalendarImportException(ImportExitCodes.Dependencies, stage, "La fase de dependencias necesita el servicio de tareas.");
                _out.WriteLine();
                _out.WriteLine("[Dependencies] Task dependencies (type and lag; no resources, assignments, costs or progress)");
                await new ProjectDependencyImporter(dependencyService, taskService, _settings, _out, _delay).RunAsync(proj.ProjectId.Value, project, state, Save, cancellationToken).ConfigureAwait(false);
                Lap("Dependencies");
                ImportStateService.MarkStage(state, stage);
                Save();
            }

            // [Resources] Solo con CalendarImport.ImportResources=true: recursos Work usados (genéricos), equipo del proyecto y asignaciones.
            if (_settings.ImportResources)
            {
                stage = ImportStages.Resources;
                var resourceService = _resources ?? throw new CalendarImportException(ImportExitCodes.Resources, stage, "CalendarImport.ImportResources=true pero no hay servicio de recursos configurado.");
                var taskService = _tasks ?? throw new CalendarImportException(ImportExitCodes.Resources, stage, "La fase de recursos necesita el servicio de tareas.");
                _out.WriteLine();
                _out.WriteLine("[Resources] Generic project team members (one per used Work resource, shared generic resource), role, resourcing unit and assignments (no work, units, rates, costs or resource calendars)");
                resourceImporter = new ProjectResourceImporter(resourceService, taskService, _settings, _out, _delay);
                try { await resourceImporter.RunAsync(proj.ProjectId.Value, project, state, Save, cancellationToken).ConfigureAwait(false); }
                finally { Lap("Resources, assignments, effort and progress (total)"); }
                ImportStateService.MarkStage(state, stage);
                Save();
            }

            _out.WriteLine();
            _out.WriteLine("[8/8] Result");
            Line("Status", "SUCCESS");
            foreach (var w in warnings) _out.WriteLine($"      WARNING     : {w}");
            _out.WriteLine();
            if (_settings.ImportResources)
            {
                _out.WriteLine("Import stopped after the Resources and Assignments phase.");
                _out.WriteLine("No Work, Units, Rates, Costs, Actuals, Progress, Resource Calendars, bookable resources or Material resources were imported.");
            }
            else if (_settings.ImportDependencies)
            {
                _out.WriteLine("Import stopped after the Dependencies phase.");
                _out.WriteLine("No Resources, Assignments, Costs or Progress were imported.");
            }
            else if (_settings.ImportTasks)
            {
                _out.WriteLine("Import stopped after the Tasks phase.");
                _out.WriteLine("No Dependencies, Resources, Assignments, Costs or Progress were imported.");
            }
            else if (_settings.StopAfterCalendar)
            {
                _out.WriteLine("Import stopped after project calendar configuration because CalendarImport.StopAfterCalendar=true.");
                _out.WriteLine("No Tasks, Dependencies, Resources or Assignments were imported.");
            }
            PrintTiming(timings, total.Elapsed, project, exceptionCount, resourceImporter, "SUCCESS");
            _out.WriteLine(bar);

            return new CalendarImportResult(true, proj.ProjectId.Value, proj.ProjectName ?? project.Title!, template.Id, template.Name,
                weekly.Fingerprint, proj.DefaultBucketId, info.CalendarId, workWeekCounters, exceptionCounters);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            PrintFailure(stage, ex, state, warnings);
            Lap($"{stage} (failed here)");
            PrintTiming(timings, total.Elapsed, request.Project, exceptionCount, resourceImporter, "FAILED");
            throw Translate(stage, ex);
        }
    }

    private void Preflight()
    {
        var problems = new List<CalendarMetadataNotConfiguredException>();
        foreach (var check in new Action[] { _templates.EnsureConfigured, _projects.EnsureConfigured, _calendars.EnsureConfigured })
        {
            try { check(); }
            catch (CalendarMetadataNotConfiguredException ex) { problems.Add(ex); }
        }
        if (problems.Count == 0) return;
        var keys = problems.SelectMany(p => p.MissingKeys).Distinct().ToList();
        throw new CalendarMetadataNotConfiguredException(problems[0].Area, keys,
            string.Join(" ", problems.Select(p => p.Message.Replace(" Run calendar-metadata and configure CalendarImport.DataverseMetadata.", string.Empty).TrimEnd()))
            + " Run calendar-metadata and configure CalendarImport.DataverseMetadata.");
    }

    /// <summary>Siempre se resuelve por configuración (solo lectura). Si el proyecto ya se creó con otra plantilla, se detiene.</summary>
    private async Task<CalendarTemplateResolution> ResolveTemplateAsync(ImportState state, NormalizedCalendar weekly, CancellationToken ct)
    {
        var cal = state.Calendar!;
        var resolved = await new CalendarTemplateResolver(_templates, _settings).ResolveAsync(weekly, ct).ConfigureAwait(false);
        if (state.Project?.ProjectId is not null && cal.CalendarTemplateId is { } storedId && storedId != resolved.Id)
            throw new CalendarImportException(ImportExitCodes.CalendarTemplate, ImportStages.CalendarTemplateProvisioning,
                $"El proyecto del state se creó con la Calendar Template {storedId:D} ('{cal.CalendarTemplateName}') y la configuración actual resuelve '{resolved.Name}' ({resolved.Id:D}). " +
                "Restaura la configuración o borra el state; el importador no cambia la plantilla de un proyecto ya creado.");
        return resolved;
    }

    private static Exception Translate(string stage, Exception ex) => ex switch
    {
        CalendarImportException or CalendarMetadataNotConfiguredException => ex,
        MsalException => new CalendarImportException(ImportExitCodes.Connection, stage, $"Error de autenticación/conexión con Dataverse: {ex.Message}", ex),
        _ => new CalendarImportException(StageExitCode(stage), stage, ex.Message, ex)
    };

    public static int StageExitCode(string stage) => stage switch
    {
        ImportStages.CalendarTemplateProvisioning => ImportExitCodes.CalendarTemplate,
        ImportStages.ProjectCreation => ImportExitCodes.ProjectCreation,
        ImportStages.ProjectCalendarResolution => ImportExitCodes.ProjectCalendar,
        ImportStages.WorkWeeks => ImportExitCodes.WorkWeeks,
        ImportStages.CalendarExceptions => ImportExitCodes.CalendarExceptions,
        ImportStages.Tasks => ImportExitCodes.Tasks,
        ImportStages.Dependencies => ImportExitCodes.Dependencies,
        ImportStages.Resources => ImportExitCodes.Resources,
        _ => ImportExitCodes.Functional
    };

    /// <summary>
    /// Bloque [Timing]: duración de cada fase, total y tamaño del proyecto, para estimar el tiempo de importar más proyectos.
    /// Una línea por fase con el formato fijo "      nombre : segundos s" (fácil de leer con un script).
    /// </summary>
    private void PrintTiming(List<(string Phase, TimeSpan Elapsed)> timings, TimeSpan total, MppProject project, int exceptionCount, ProjectResourceImporter? resourceImporter, string status)
    {
        string S(TimeSpan t) => t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var tasks = project.Tasks.Where(t => !t.IsNull && !t.IsProjectSummaryTask).ToList();
        _out.WriteLine();
        _out.WriteLine("[Timing]");
        foreach (var (phase, elapsed) in timings)
        {
            Line(phase, $"{S(elapsed),8} s", 6, 62);
            if (phase.StartsWith("Resources, assignments", StringComparison.Ordinal) && resourceImporter is not null)
                foreach (var (sub, subElapsed) in resourceImporter.PhaseTimings)
                    Line("  - " + sub, $"{S(subElapsed),8} s", 6, 62);
        }
        Line("TOTAL", $"{S(total),8} s  ({total:hh\\:mm\\:ss}) [{status}]", 6, 62);
        Line("Size", $"tasks {tasks.Count} (leaf {tasks.Count(t => !t.IsSummary)}), dependencies {project.Dependencies.Count}, " +
                      $"Work resources {project.Resources.Count(r => r.Type == "Work")}, assignments {project.ResourceAssignments.Count}, calendar exceptions {exceptionCount}", 6, 62);
    }

    private void PrintFailure(string stage, Exception ex, ImportState state, List<string> warnings)
    {
        _out.WriteLine();
        _out.WriteLine($"FAILED at stage {stage}: {ex.Message}");
        foreach (var w in warnings) _out.WriteLine($"      WARNING     : {w}");
        _out.WriteLine("Elements created so far (kept; nothing is deleted automatically):");
        var cal = state.Calendar;
        _out.WriteLine($"  Calendar Template : {(cal?.CalendarTemplateId is { } t ? $"{cal.CalendarTemplateName} ({t:D}) [manual]" : "-")}");
        _out.WriteLine($"  Project           : {(state.Project?.ProjectId is { } p ? p.ToString("D") : "-")}");
        _out.WriteLine($"  Project calendar  : {(cal?.ProjectCalendarId is { } c ? c.ToString("D") : "-")}");
        _out.WriteLine($"  WorkWeeks applied : {cal?.WorkWeeksApplied ?? 0}; exceptions applied: {cal?.ExceptionsApplied ?? 0}");
        _out.WriteLine("Re-run the same command with the same --state to resume without duplicating data.");
    }

    private void PrintCounters(StageCounters c)
    {
        Line("Found", c.Found.ToString());
        Line("Created", c.Created.ToString());
        Line("Reused", c.Reused.ToString());
        Line("Failed", c.Failed.ToString());
    }

    private void Line(string label, string value, int indent = 6, int width = 12)
        => _out.WriteLine($"{new string(' ', indent)}{label.PadRight(width)}: {value}");
}
