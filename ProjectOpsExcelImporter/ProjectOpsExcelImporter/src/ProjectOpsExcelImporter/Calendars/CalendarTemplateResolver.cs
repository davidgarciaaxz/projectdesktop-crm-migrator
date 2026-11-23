namespace ProjectOpsExcelImporter.Calendars;

/// <summary>
/// Resuelve, mediante la configuración (CalendarImport.CalendarTemplates), la Calendar Template manual que corresponde
/// al horario base del MPP y exige que exista exactamente una. Solo lectura: nunca crea ni modifica plantillas.
/// </summary>
public sealed class CalendarTemplateResolver
{
    private const string Stage = "CalendarTemplateProvisioning";
    private readonly ICalendarTemplateRepository _repository;
    private readonly CalendarImportSettings _settings;

    public CalendarTemplateResolver(ICalendarTemplateRepository repository, CalendarImportSettings settings)
    {
        _repository = repository;
        _settings = settings;
    }

    public async Task<CalendarTemplateResolution> ResolveAsync(NormalizedCalendar weekly, CancellationToken cancellationToken)
    {
        var mapping = _settings.CalendarTemplates.FirstOrDefault(m => string.Equals(m.Fingerprint?.Trim(), weekly.Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            var suggested = CalendarTemplateNaming.BuildName("MPP - Calendario", weekly, null);
            throw Fail($"No hay ninguna Calendar Template configurada para este horario base. Fingerprint: {weekly.Fingerprint}. Horario: {weekly.DisplayPattern}. " +
                       $"Crea la plantilla manualmente en Project Operations (por ejemplo '{suggested}') y añade a CalendarImport.CalendarTemplates: " +
                       $"{{ \"fingerprint\": \"{weekly.Fingerprint}\", \"name\": \"{suggested}\" }}");
        }

        CalendarTemplateRecord record;
        if (mapping.Id is { } id)
        {
            record = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw Fail($"La Calendar Template con Id {id:D} configurada para el fingerprint {weekly.Fingerprint} no existe en Dataverse. No se crea automáticamente.");
            if (!string.IsNullOrWhiteSpace(mapping.Name) && !string.Equals(record.Name, mapping.Name, StringComparison.OrdinalIgnoreCase))
                throw Fail($"La Calendar Template {id:D} se llama '{record.Name}' pero la configuración espera '{mapping.Name}'.");
        }
        else
        {
            var name = mapping.Name!.Trim();
            var matches = (await _repository.FindByNameAsync(name, cancellationToken).ConfigureAwait(false))
                .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                throw Fail($"No existe la Calendar Template '{name}' (fingerprint {weekly.Fingerprint}). Debe crearse manualmente en Project Operations; el importador no la crea.");
            if (matches.Count > 1)
                throw Fail($"Existen {matches.Count} Calendar Templates llamadas '{name}' ({string.Join(", ", matches.Select(m => m.Id.ToString("D")))}). Debe haber exactamente una; " +
                           "renombra las duplicadas o indica el Id en CalendarImport.CalendarTemplates.");
            record = matches[0];
        }
        return new CalendarTemplateResolution(record.Id, record.Name, weekly.Fingerprint, false, "FOUND (manual template)",
            "El horario interno de la plantilla manual no se verifica contra el fingerprint; la correspondencia es la configurada.");
    }

    private static CalendarImportException Fail(string message) => new(ImportExitCodes.CalendarTemplate, Stage, message);
}
