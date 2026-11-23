namespace ProjectOpsExcelImporter.Calendars;

/// <summary>Comprueba que los nombres lógicos de Dataverse estén confirmados y configurados. No conecta.</summary>
internal static class CalendarMetadataGuard
{
    private const string Hint = " Run calendar-metadata and configure CalendarImport.DataverseMetadata.";

    public static void Check(DataverseMetadataSettings m, bool template, bool projectTemplateLink, bool projectCalendar, bool rules)
    {
        var problems = new List<(CalendarMetadataArea Area, string Message, List<string> Keys)>();
        if (template)
            Add(problems, CalendarMetadataArea.CalendarTemplate, "Calendar Template entity metadata has not been confirmed.",
                (nameof(m.CalendarTemplateEntitySet), m.CalendarTemplateEntitySet), (nameof(m.CalendarTemplateIdAttribute), m.CalendarTemplateIdAttribute),
                (nameof(m.CalendarTemplateNameAttribute), m.CalendarTemplateNameAttribute));
        if (projectTemplateLink)
            Add(problems, CalendarMetadataArea.ProjectCreation, "The relationship between msdyn_project and the Calendar Template has not been confirmed.",
                (nameof(m.ProjectCalendarTemplateAttribute), m.ProjectCalendarTemplateAttribute), (nameof(m.ProjectSourceGuidAttribute), m.ProjectSourceGuidAttribute));
        if (projectCalendar)
            Add(problems, CalendarMetadataArea.ProjectCalendar, "The msdyn_project own-calendar metadata has not been confirmed.",
                (nameof(m.ProjectCalendarAttribute), m.ProjectCalendarAttribute), (nameof(m.CalendarEntitySet), m.CalendarEntitySet));
        if (rules)
            Add(problems, CalendarMetadataArea.CalendarRules, "Calendar rule entity metadata has not been confirmed.",
                (nameof(m.CalendarRuleEntitySet), m.CalendarRuleEntitySet));
        if (problems.Count == 0) return;
        throw new CalendarMetadataNotConfiguredException(
            problems[0].Area,
            problems.SelectMany(p => p.Keys).ToList(),
            string.Join(" ", problems.Select(p => $"{p.Message.TrimEnd('.')} (missing: {string.Join(", ", p.Keys)}).")) + Hint);
    }

    private static void Add(List<(CalendarMetadataArea, string, List<string>)> problems, CalendarMetadataArea area, string message, params (string Key, string? Value)[] items)
    {
        var missing = items.Where(i => string.IsNullOrWhiteSpace(i.Value)).Select(i => i.Key).ToList();
        if (missing.Count > 0) problems.Add((area, message, missing));
    }

    public static NotSupportedException NotImplemented(string what)
        => new($"{what}: the Dataverse metadata is configured but the mapping to Dataverse records has not been implemented yet. " +
               "Review the calendar-metadata output and implement this repository before use.");
}

/// <summary>
/// Solo lectura sobre las Calendar Templates manuales (msdyn_workhourtemplate según calendar-metadata).
/// Solo emite GET: no hay ningún método de creación, actualización o borrado.
/// </summary>
public sealed class DataverseCalendarTemplateRepository : ICalendarTemplateRepository
{
    private readonly DataverseClient _client;
    private readonly CalendarImportSettings _settings;

    public DataverseCalendarTemplateRepository(DataverseClient client, CalendarImportSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public void EnsureConfigured() => CalendarMetadataGuard.Check(_settings.DataverseMetadata, true, false, false, false);

    public async Task<IReadOnlyList<CalendarTemplateRecord>> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var m = Metadata();
        var literal = Uri.EscapeDataString(name.Replace("'", "''"));
        return await QueryAsync($"{m.CalendarTemplateEntitySet}?$select={m.CalendarTemplateIdAttribute},{m.CalendarTemplateNameAttribute}" +
                                $"&$filter={m.CalendarTemplateNameAttribute} eq '{literal}'", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalendarTemplateRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var m = Metadata();
        var found = await QueryAsync($"{m.CalendarTemplateEntitySet}?$select={m.CalendarTemplateIdAttribute},{m.CalendarTemplateNameAttribute}" +
                                     $"&$filter={m.CalendarTemplateIdAttribute} eq {id:D}", cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault();
    }

    private async Task<IReadOnlyList<CalendarTemplateRecord>> QueryAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var m = Metadata();
        var response = await _client.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
        var list = new List<CalendarTemplateRecord>();
        foreach (var item in response["value"] as System.Text.Json.Nodes.JsonArray ?? [])
        {
            var idText = item?[m.CalendarTemplateIdAttribute!]?.GetValue<string>();
            var nameText = item?[m.CalendarTemplateNameAttribute!]?.GetValue<string>();
            if (Guid.TryParse(idText, out var id) && nameText is not null) list.Add(new CalendarTemplateRecord(id, nameText));
        }
        return list;
    }

    private DataverseMetadataSettings Metadata()
    {
        EnsureConfigured();
        return _settings.DataverseMetadata;
    }
}

/// <summary>
/// ProjectExistsAsync usa el entity set msdyn_projects (confirmado por el código existente).
/// La creación con plantilla queda pendiente hasta confirmar cómo se aplica la Calendar Template.
/// </summary>
public sealed class DataverseProjectOperationsProjectService : IProjectOperationsProjectService
{
    private readonly DataverseClient _client;
    private readonly CalendarImportSettings _settings;

    public DataverseProjectOperationsProjectService(DataverseClient client, CalendarImportSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public void EnsureConfigured() => CalendarMetadataGuard.Check(_settings.DataverseMetadata, false, true, false, false);

    public Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken cancellationToken)
        => _client.RecordExistsAsync("msdyn_projects", projectId, cancellationToken);

    /// <summary>
    /// Crea el proyecto con msdyn_CreateProjectV1 (mismo patrón que el importador existente) enlazando la Calendar Template manual
    /// mediante el lookup confirmado en calendar-metadata. Si Dataverse no admite el enlace, falla sin crear nada.
    /// </summary>
    public async Task<Guid?> GetLinkedTemplateIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var lookup = $"_{_settings.DataverseMetadata.ProjectCalendarTemplateAttribute}_value";
        var response = await _client.GetAsync($"msdyn_projects({projectId:D})?$select={lookup}", cancellationToken).ConfigureAwait(false);
        return Guid.TryParse(response[lookup]?.GetValue<string>(), out var id) ? id : null;
    }

    /// <summary>Longitud máxima de axz_mppfile según los metadatos de tu entorno (comprobada con dataverse-get).</summary>
    private const int SourceFileMaxLength = 100;

    public async Task<Guid?> FindProjectBySourceGuidAsync(Guid mppProjectGuid, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var attribute = _settings.DataverseMetadata.ProjectSourceGuidAttribute!;
        var filter = Uri.EscapeDataString($"{attribute} eq '{mppProjectGuid:D}'");
        var response = await _client.GetAsync($"msdyn_projects?$select=msdyn_projectid&$filter={filter}&$top=5", cancellationToken).ConfigureAwait(false);
        var ids = (response["value"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(v => Guid.TryParse(v?["msdyn_projectid"]?.GetValue<string>(), out var id) ? id : (Guid?)null)
            .Where(id => id is not null).Select(id => id!.Value).ToList();
        if (ids.Count > 1)
            throw new CalendarImportException(ImportExitCodes.ProjectCreation, "ProjectCreation",
                $"Hay {ids.Count} proyectos con {attribute} = {mppProjectGuid:D} ({string.Join(", ", ids.Select(i => i.ToString("D")))}). Debe haber como máximo uno; borra los duplicados.");
        return ids.Count == 1 ? ids[0] : null;
    }

    // Crea el proyecto SIN fijar msdyn_projectid (lo genera Project Operations: un proyecto borrado no se puede recrear con el mismo GUID),
    // enlaza la Calendar Template manual y guarda el GUID del MPP y el nombre del fichero en sus campos.
    public async Task<ProjectCreationResult> CreateProjectAsync(string projectName, DateTime? startDate, CalendarTemplateResolution template, Guid mppProjectGuid, string? sourceFileName, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var m = _settings.DataverseMetadata;
        if (!string.IsNullOrWhiteSpace(m.ProjectSourceFileAttribute) && !string.IsNullOrWhiteSpace(sourceFileName) && sourceFileName.Length > SourceFileMaxLength)
            throw new InvalidOperationException($"El nombre del fichero '{sourceFileName}' tiene {sourceFileName.Length} caracteres y {m.ProjectSourceFileAttribute} admite {SourceFileMaxLength}. No se crea el proyecto.");
        var entity = new System.Text.Json.Nodes.JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_project",
            ["msdyn_subject"] = projectName,
            [m.ProjectSourceGuidAttribute!] = mppProjectGuid.ToString("D"),
            [$"{m.ProjectCalendarTemplateAttribute}@odata.bind"] = $"/{m.CalendarTemplateEntitySet}({template.Id:D})"
        };
        if (!string.IsNullOrWhiteSpace(m.ProjectSourceFileAttribute) && !string.IsNullOrWhiteSpace(sourceFileName))
            entity[m.ProjectSourceFileAttribute] = sourceFileName;
        if (startDate is { } start) entity["msdyn_scheduledstart"] = $"{start:yyyy-MM-dd}T09:00:00";
        var response = await _client.PostActionAsync("msdyn_CreateProjectV1", new System.Text.Json.Nodes.JsonObject { ["Project"] = entity }, cancellationToken).ConfigureAwait(false);
        var createdId = JsonHelpers.RequiredGuid(response, "ProjectId", "msdyn_projectid");
        return new ProjectCreationResult(createdId, projectName, template.Id, template.Name, template.Fingerprint, null, null, "msdyn_CreateProjectV1", true);
    }

    public async Task<ProjectSettingResult> EnsureAllowPercentCompleteUpdateAsync(Guid projectId, CancellationToken cancellationToken)
    {
        const string attribute = "msdyn_allowpercentcompleteupdate";
        async Task<bool?> ReadAsync()
        {
            var row = await _client.GetAsync($"msdyn_projects({projectId:D})?$select={attribute}", cancellationToken).ConfigureAwait(false);
            return row[attribute] is { } node ? node.GetValue<bool>() : null;
        }
        var before = await ReadAsync().ConfigureAwait(false);
        if (before == true) return new ProjectSettingResult(before, false);
        await _client.PatchAsync($"msdyn_projects({projectId:D})", new System.Text.Json.Nodes.JsonObject { [attribute] = true }, cancellationToken).ConfigureAwait(false);
        var after = await ReadAsync().ConfigureAwait(false);
        if (after != true) throw new InvalidOperationException($"{attribute} del proyecto {projectId:D} no quedó en true (valor actual: {after?.ToString() ?? "vacío"}).");
        return new ProjectSettingResult(before, true);
    }

    public async Task<ProjectActualStartResult> EnsureProjectActualStartAsync(Guid projectId, DateTime actualStart, CancellationToken cancellationToken)
    {
        const string attribute = "msdyn_actualstart";
        var target = DateTime.SpecifyKind(actualStart, DateTimeKind.Utc);
        async Task<DateTime?> ReadAsync()
        {
            var row = await _client.GetAsync($"msdyn_projects({projectId:D})?$select={attribute}", cancellationToken).ConfigureAwait(false);
            return row[attribute]?.GetValue<string>() is { } text
                   && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : null;
        }
        var before = await ReadAsync().ConfigureAwait(false);
        if (before == target) return new ProjectActualStartResult(before, false);
        await _client.PatchAsync($"msdyn_projects({projectId:D})", new System.Text.Json.Nodes.JsonObject
        {
            [attribute] = target.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        var after = await ReadAsync().ConfigureAwait(false);
        if (after != target)
            throw new InvalidOperationException($"{attribute} del proyecto {projectId:D} quedó en {after?.ToString("yyyy-MM-dd HH:mm") ?? "(vacío)"} y se envió {target:yyyy-MM-dd HH:mm}.");
        return new ProjectActualStartResult(before, true);
    }
}
