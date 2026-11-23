namespace ProjectOpsExcelImporter.Calendars;

/// <summary>
/// Nombres lógicos de Dataverse. Se dejan a null hasta confirmarlos con el comando calendar-metadata.
/// </summary>
public sealed class DataverseMetadataSettings
{
    public string? CalendarTemplateEntitySet { get; set; }
    public string? CalendarTemplateIdAttribute { get; set; }
    public string? CalendarTemplateNameAttribute { get; set; }
    public string? ProjectCalendarAttribute { get; set; }
    public string? ProjectCalendarTemplateAttribute { get; set; }
    /// <summary>Columna de msdyn_project (texto, máx. 100) donde se guarda el nombre del fichero importado. Opcional: null = no se escribe.</summary>
    public string? ProjectSourceFileAttribute { get; set; }
    /// <summary>
    /// Columna de msdyn_project (texto, mín. 36) donde se guarda el GUID del proyecto en Project Desktop. OBLIGATORIA: el proyecto se crea con un
    /// GUID propio de Project Operations y se localiza por este campo (un proyecto borrado no se puede recrear con el mismo msdyn_projectid).
    /// </summary>
    public string? ProjectSourceGuidAttribute { get; set; }
    public string? CalendarEntitySet { get; set; }
    public string? CalendarRuleEntitySet { get; set; }
}

/// <summary>
/// Asocia el fingerprint del horario base del MPP con una Calendar Template creada y mantenida manualmente
/// en Project Operations. El importador nunca crea ni modifica Calendar Templates.
/// </summary>
/// <summary>Una entrada de ResourceRoleMappings: al menos una de las tres claves del recurso del MPP y el rol de Project Operations (bookableresourcecategory).</summary>
public sealed class ResourceRoleMapping
{
    public Guid? MppResourceGuid { get; set; }
    public int? MppResourceUniqueId { get; set; }
    public string? MppResourceName { get; set; }
    public Guid ProjectOperationsRoleId { get; set; }
}

public sealed class CalendarTemplateMapping
{
    /// <summary>SHA-256 (64 hex) del horario semanal normalizado; el importador lo imprime en [2/8].</summary>
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>Nombre exacto de la plantilla manual. Opcional si se indica Id.</summary>
    public string? Name { get; set; }
    /// <summary>Id de la plantilla manual. Opcional si se indica Name; si se indican ambos deben coincidir.</summary>
    public Guid? Id { get; set; }
}

public sealed class CalendarImportSettings
{
    public const string ModeExpand = "Expand";
    public const string ModeFail = "Fail";
    public const string ModeSkipWithWarning = "SkipWithWarning";

    /// <summary>Calendar Templates manuales que corresponden a cada horario base (por fingerprint).</summary>
    public List<CalendarTemplateMapping> CalendarTemplates { get; set; } = [];
    public bool ApplyWorkWeeks { get; set; } = true;
    public bool ApplyCalendarExceptions { get; set; } = true;
    /// <summary>Si es true, tras las excepciones se crean las tareas básicas (nombre, jerarquía, comienzo, duración) y la importación termina ahí.</summary>
    public bool ImportTasks { get; set; }
    /// <summary>Limita la fase de tareas a las N primeras tareas del fichero (más sus ancestros). Vacío = todas.</summary>
    public int? TaskMaxCount { get; set; }
    /// <summary>Limita la fase de tareas a estos UniqueIds del MPP (más sus ancestros). Tiene prioridad sobre TaskMaxCount.</summary>
    public List<int>? TaskUniqueIds { get; set; }
    /// <summary>Si es true, tras las tareas se crean las dependencias entre ellas y la importación termina ahí (sin recursos, asignaciones, costes ni progreso).</summary>
    public bool ImportDependencies { get; set; }
    /// <summary>Limita la fase de dependencias a las N primeras (por RelationUniqueId) entre las tareas seleccionadas. Vacío = todas.</summary>
    public int? DependencyMaxCount { get; set; }
    /// <summary>Si es false (predeterminado) se omiten, con aviso, las dependencias cuya predecesora o sucesora es una tarea resumen (aún no verificado que Project Operations las admita).</summary>
    public bool IncludeSummaryDependencies { get; set; }
    /// <summary>
    /// Columna del tipo de enlace que EXIGE el servicio de planificación (error ScheduleAPI-EV-0003: columnas obligatorias msdyn_linktype,
    /// msdyn_predecessortask, msdyn_project y msdyn_successortask). Es un conjunto de opciones global (msdyn_activitylinktype).
    /// </summary>
    public string DependencyLinkTypeAttribute { get; set; } = "msdyn_linktype";
    /// <summary>Columna nativa del retardo (entero, en SEGUNDOS; positivo = retardo, negativo = adelanto, según la descripción del metadato).</summary>
    public string DependencyLagAttribute { get; set; } = "msdyn_projecttaskdependencylinklag";
    /// <summary>
    /// Valores de msdyn_linktype leídos de los metadatos del entorno: Fin a comienzo 192350000, Comienzo a comienzo 192350001, Fin a fin 192350002,
    /// Comienzo a fin 192350004 (OJO: no es 192350003, como figura en la configuración del importador antiguo).
    /// </summary>
    public Dictionary<string, int> DependencyLinkTypeValues { get; set; } = new()
    {
        ["FinishToStart"] = 192350000, ["StartToStart"] = 192350001, ["FinishToFinish"] = 192350002, ["StartToFinish"] = 192350004
    };
    /// <summary>
    /// Si es true, tras las dependencias (o las tareas) se crea un MIEMBRO GENÉRICO del equipo del proyecto por cada recurso Work usado
    /// y se crean sus asignaciones (solo proyecto, tarea y miembro). La importación termina ahí. NO se crea ningún recurso reservable.
    /// </summary>
    public bool ImportResources { get; set; }
    /// <summary>
    /// Si es true (y ImportResources), tras crear las asignaciones se fija el ESFUERZO de cada tarea con la suma del Work de sus asignaciones en el MPP
    /// (msdyn_effort con msdyn_PssUpdateV2). La API no admite actualizar asignaciones ni msdyn_effortcompleted, y msdyn_progress fue rechazado en la prueba.
    /// </summary>
    public bool ImportWork { get; set; }
    /// <summary>
    /// Si es true (implica ImportWork), tras fijar el esfuerzo se envía el PercentComplete del MPP a las tareas hoja con avance (msdyn_progress con msdyn_PssUpdateV2,
    /// que recibe una FRACCIÓN 0-1 aunque la columna guarde 0-100). El trabajo completado y el restante los deriva la plataforma. Requiere
    /// msdyn_allowpercentcompleteupdate = true en el proyecto (valor comprobado; sin él no se ha probado).
    /// </summary>
    public bool ImportProgress { get; set; }
    /// <summary>GUID del recurso reservable genérico que ya existe en Project Operations ("Recurso genérico"). Todos los miembros genéricos apuntan a él. Obligatorio con ImportResources.</summary>
    public Guid? GenericBookableResourceId { get; set; }
    /// <summary>Rol (bookableresourcecategory) de los recursos del MPP sin mapping específico. Opcional: sin él, esos recursos se registran como error y no se crean.</summary>
    public Guid? DefaultResourceRoleId { get; set; }
    /// <summary>Unidad de dotación de recursos (msdyn_organizationalunit) de los miembros genéricos. Obligatoria con ImportResources.</summary>
    public Guid? DefaultResourcingUnitId { get; set; }
    /// <summary>Mapping opcional recurso del MPP -> rol de Project Operations (por GUID, por UniqueId o por nombre exacto).</summary>
    public List<ResourceRoleMapping> ResourceRoleMappings { get; set; } = [];
    /// <summary>Propiedad de navegación del lookup al recurso reservable en msdyn_projectteam (la que usa el importador existente).</summary>
    public string TeamMemberBookableResourceBind { get; set; } = "msdyn_bookableresourceid";
    /// <summary>Propiedad de navegación del lookup "Rol" de msdyn_projectteam (metadato confirmado: msdyn_resourcecategory -> bookableresourcecategory).</summary>
    public string TeamMemberRoleBind { get; set; } = "msdyn_resourcecategory";
    /// <summary>Propiedad de navegación del lookup "Unidad de dotación de recursos" de msdyn_projectteam (metadato confirmado: msdyn_organizationalunit).</summary>
    public string TeamMemberResourcingUnitBind { get; set; } = "msdyn_organizationalunit";
    /// <summary>Espera máxima a que el servicio de planificación termine un lote (se corta antes si informa de un error).</summary>
    public int OperationTimeoutSeconds { get; set; } = 180;
    public int TaskBatchSize { get; set; } = 100;
    public int TaskReadbackTimeoutSeconds { get; set; } = 60;
    /// <summary>Unidades de calendario para convertir duraciones; se rellenan desde AppSettings (HoursPerDay, DaysPerWeek, DaysPerMonth), no desde el JSON de configuración.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TaskDurationRules DurationRules { get; set; } = new();
    /// <summary>Si es true (predeterminado) import termina tras configurar el calendario del proyecto.</summary>
    public bool StopAfterCalendar { get; set; } = true;
    public string RecurringExceptionsMode { get; set; } = ModeExpand;
    /// <summary>"ProjectStart" o una fecha ISO (yyyy-MM-dd).</summary>
    public string RecurrenceExpansionStart { get; set; } = "ProjectStart";
    /// <summary>"ProjectFinish" o una fecha ISO (yyyy-MM-dd).</summary>
    public string RecurrenceExpansionEnd { get; set; } = "ProjectFinish";
    public int MaxExpandedOccurrencesPerRule { get; set; } = 1000;
    public int ProjectCalendarMaxRetries { get; set; } = 5;
    public int ProjectCalendarInitialDelaySeconds { get; set; } = 2;
    public DataverseMetadataSettings DataverseMetadata { get; set; } = new();

    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in CalendarTemplates)
        {
            var fp = mapping.Fingerprint?.Trim() ?? string.Empty;
            if (fp.Length != 64 || !fp.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"CalendarImport.CalendarTemplates: fingerprint '{mapping.Fingerprint}' no válido (se esperan 64 caracteres hexadecimales).");
            if (string.IsNullOrWhiteSpace(mapping.Name) && mapping.Id is null)
                throw new InvalidOperationException($"CalendarImport.CalendarTemplates: el fingerprint {fp} requiere Name o Id.");
            if (!seen.Add(fp))
                throw new InvalidOperationException($"CalendarImport.CalendarTemplates: fingerprint {fp} duplicado.");
        }
        if (!new[] { ModeExpand, ModeFail, ModeSkipWithWarning }.Contains(RecurringExceptionsMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"CalendarImport.RecurringExceptionsMode '{RecurringExceptionsMode}' no válido. Valores: {ModeExpand}, {ModeFail}, {ModeSkipWithWarning}.");
        if (TaskMaxCount is < 1) throw new InvalidOperationException("CalendarImport.TaskMaxCount debe ser >= 1.");
        if (DependencyMaxCount is < 1) throw new InvalidOperationException("CalendarImport.DependencyMaxCount debe ser >= 1.");
        if (OperationTimeoutSeconds < 10) throw new InvalidOperationException("CalendarImport.OperationTimeoutSeconds debe ser >= 10.");
        foreach (var key in new[] { "FinishToFinish", "FinishToStart", "StartToFinish", "StartToStart" })
            if (!DependencyLinkTypeValues.ContainsKey(key)) throw new InvalidOperationException($"CalendarImport.DependencyLinkTypeValues no contiene '{key}'.");
        if (string.IsNullOrWhiteSpace(DependencyLinkTypeAttribute) || string.IsNullOrWhiteSpace(DependencyLagAttribute))
            throw new InvalidOperationException("CalendarImport.DependencyLinkTypeAttribute y DependencyLagAttribute no pueden estar vacíos.");
        if (TaskBatchSize is < 1 or > 200) throw new InvalidOperationException("CalendarImport.TaskBatchSize debe estar entre 1 y 200.");
        if (TaskReadbackTimeoutSeconds < 0) throw new InvalidOperationException("CalendarImport.TaskReadbackTimeoutSeconds debe ser >= 0.");
        if (MaxExpandedOccurrencesPerRule < 1)
            throw new InvalidOperationException("CalendarImport.MaxExpandedOccurrencesPerRule debe ser >= 1.");
        if (ProjectCalendarMaxRetries < 1)
            throw new InvalidOperationException("CalendarImport.ProjectCalendarMaxRetries debe ser >= 1.");
        if (ProjectCalendarInitialDelaySeconds < 0)
            throw new InvalidOperationException("CalendarImport.ProjectCalendarInitialDelaySeconds debe ser >= 0.");
        ValidateBoundary(RecurrenceExpansionStart, "ProjectStart", nameof(RecurrenceExpansionStart));
        ValidateBoundary(RecurrenceExpansionEnd, "ProjectFinish", nameof(RecurrenceExpansionEnd));
    }

    private static void ValidateBoundary(string value, string keyword, string name)
    {
        if (!value.Equals(keyword, StringComparison.OrdinalIgnoreCase) && !DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))
            throw new InvalidOperationException($"CalendarImport.{name} debe ser '{keyword}' o una fecha yyyy-MM-dd.");
    }
}
