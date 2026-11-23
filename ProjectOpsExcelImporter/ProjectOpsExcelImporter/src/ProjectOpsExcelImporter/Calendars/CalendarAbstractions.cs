namespace ProjectOpsExcelImporter.Calendars;

/// <summary>Error de una fase concreta con el exit code que le corresponde.</summary>
public sealed class CalendarImportException : Exception
{
    public int ExitCode { get; }
    public string Stage { get; }

    public CalendarImportException(int exitCode, string stage, string message, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
        Stage = stage;
    }
}

public enum CalendarMetadataArea { CalendarTemplate, ProjectCreation, ProjectCalendar, CalendarRules }

/// <summary>Faltan nombres lógicos de Dataverse sin confirmar (ver comando calendar-metadata).</summary>
public sealed class CalendarMetadataNotConfiguredException : Exception
{
    public CalendarMetadataArea Area { get; }
    public IReadOnlyList<string> MissingKeys { get; }

    public CalendarMetadataNotConfiguredException(CalendarMetadataArea area, IReadOnlyList<string> missingKeys, string message)
        : base(message)
    {
        Area = area;
        MissingKeys = missingKeys;
    }

    public int ExitCode => Area switch
    {
        CalendarMetadataArea.CalendarTemplate => ImportExitCodes.CalendarTemplate,
        CalendarMetadataArea.ProjectCreation => ImportExitCodes.ProjectCreation,
        CalendarMetadataArea.ProjectCalendar => ImportExitCodes.ProjectCalendar,
        _ => ImportExitCodes.WorkWeeks
    };
}

public sealed record CalendarTemplateRecord(Guid Id, string Name);

public sealed record ProjectCalendarInfo(Guid CalendarId, string WeeklyCanonicalPattern);

public enum ApplyOutcome { Created, Reused, Conflict }

public sealed record ApplyResult(ApplyOutcome Outcome, string? ConflictMessage = null);

/// <summary>Las Calendar Templates se crean y mantienen manualmente: este repositorio es deliberadamente de solo lectura.</summary>
public interface ICalendarTemplateRepository
{
    /// <summary>Falla con <see cref="CalendarMetadataNotConfiguredException"/> si faltan metadatos confirmados. No debe conectar.</summary>
    void EnsureConfigured();
    /// <summary>Solo lectura: plantillas cuyo nombre coincide (sin distinguir mayúsculas).</summary>
    Task<IReadOnlyList<CalendarTemplateRecord>> FindByNameAsync(string name, CancellationToken cancellationToken);
    /// <summary>Solo lectura: null si no existe.</summary>
    Task<CalendarTemplateRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}

public interface IProjectOperationsProjectService
{
    void EnsureConfigured();
    Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Id de la Calendar Template enlazada al proyecto, o null si no tiene ninguna. Solo lectura.</summary>
    Task<Guid?> GetLinkedTemplateIdAsync(Guid projectId, CancellationToken cancellationToken);
    /// <summary>Localiza el proyecto ya importado por el GUID de Project Desktop guardado en su campo. Null si no existe; varias coincidencias = error.</summary>
    Task<Guid?> FindProjectBySourceGuidAsync(Guid mppProjectGuid, CancellationToken cancellationToken);
    /// <summary>Crea el proyecto con GUID propio de Project Operations y guarda <paramref name="mppProjectGuid"/> en su campo de origen.</summary>
    /// <param name="sourceFileName">Nombre del fichero importado (sin ruta); se guarda en el proyecto si hay columna configurada.</param>
    Task<ProjectCreationResult> CreateProjectAsync(string projectName, DateTime? startDate, CalendarTemplateResolution template, Guid mppProjectGuid, string? sourceFileName, CancellationToken cancellationToken);
    /// <summary>
    /// Guarda en msdyn_actualstart del proyecto el "Comienzo" de la primera fila del MPP (la tarea resumen del proyecto). Idempotente: si ya tiene ese valor no escribe.
    /// Se envía igual que el inicio de las tareas (sin zona horaria, que Dataverse lee como UTC).
    /// </summary>
    Task<ProjectActualStartResult> EnsureProjectActualStartAsync(Guid projectId, DateTime actualStart, CancellationToken cancellationToken);
    /// <summary>
    /// Pone msdyn_allowpercentcompleteupdate = true en el proyecto ("Permitir actualización del porcentaje completado"): un proyecto nuevo nace con false y así no se
    /// puede importar el avance de las tareas. Idempotente: si ya está en true no escribe. No se revierte al terminar.
    /// </summary>
    Task<ProjectSettingResult> EnsureAllowPercentCompleteUpdateAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>Valor anterior de un ajuste booleano del proyecto y si hubo que escribirlo.</summary>
public sealed record ProjectSettingResult(bool? Before, bool Changed);

/// <summary>Valor anterior de msdyn_actualstart (UTC) y si hubo que escribirlo.</summary>
public sealed record ProjectActualStartResult(DateTime? Before, bool Changed);

public interface IProjectCalendarRepository
{
    void EnsureConfigured();
    Task<ProjectCalendarInfo?> TryGetProjectCalendarAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ApplyResult> EnsureWorkWeekAsync(Guid projectCalendarId, NormalizedWorkWeek workWeek, CancellationToken cancellationToken);
    Task<ApplyResult> EnsureExceptionAsync(Guid projectCalendarId, NormalizedCalendarException exception, CancellationToken cancellationToken);
}
