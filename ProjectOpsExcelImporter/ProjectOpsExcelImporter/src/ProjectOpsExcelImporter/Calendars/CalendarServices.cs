namespace ProjectOpsExcelImporter.Calendars;

/// <summary>Espera el calendario propio del proyecto (generación posiblemente asíncrona) y verifica su patrón.</summary>
public sealed class ProjectCalendarResolver
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private readonly IProjectCalendarRepository _repository;
    private readonly CalendarImportSettings _settings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ProjectCalendarResolver(IProjectCalendarRepository repository, CalendarImportSettings settings, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _repository = repository;
        _settings = settings;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Esperas antes de cada intento: inicial, x2 en cada reintento, máximo 30 s (2, 4, 8, 16, 30).</summary>
    public static IReadOnlyList<TimeSpan> BuildDelays(int attempts, int initialSeconds)
    {
        var list = new List<TimeSpan>();
        var current = TimeSpan.FromSeconds(initialSeconds);
        for (var i = 0; i < attempts; i++)
        {
            list.Add(current > MaxDelay ? MaxDelay : current);
            current = TimeSpan.FromSeconds(Math.Max(current.TotalSeconds * 2, 1));
        }
        return list;
    }

    public async Task<ProjectCalendarInfo> ResolveAsync(Guid projectId, string expectedCanonicalPattern, bool skipInitialDelay, CancellationToken cancellationToken)
    {
        var delays = BuildDelays(_settings.ProjectCalendarMaxRetries, _settings.ProjectCalendarInitialDelaySeconds);
        for (var attempt = 0; attempt < delays.Count; attempt++)
        {
            if (!(skipInitialDelay && attempt == 0))
                await _delay(delays[attempt], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var info = await _repository.TryGetProjectCalendarAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (info is null) continue;
            if (info.WeeklyCanonicalPattern != expectedCanonicalPattern)
                throw new CalendarImportException(ImportExitCodes.ProjectCalendar, "ProjectCalendarResolution",
                    $"El calendario del proyecto ({info.CalendarId:D}) no coincide con la Calendar Template seleccionada. Esperado: {expectedCanonicalPattern}. Encontrado: {info.WeeklyCanonicalPattern}.");
            return info;
        }
        throw new CalendarImportException(ImportExitCodes.ProjectCalendar, "ProjectCalendarResolution",
            $"El calendario propio del proyecto {projectId:D} no apareció tras {delays.Count} intentos. No se aplican WorkWeeks ni excepciones.");
    }
}

public sealed class ProjectWorkWeekImporter
{
    private readonly IProjectCalendarRepository _repository;
    public ProjectWorkWeekImporter(IProjectCalendarRepository repository) => _repository = repository;

    public async Task ApplyAsync(Guid projectCalendarId, IReadOnlyList<NormalizedWorkWeek> workWeeks, CalendarImportState state,
        StageCounters counters, Action saveState, CancellationToken cancellationToken)
    {
        counters.Found = workWeeks.Count;
        foreach (var ww in workWeeks.OrderBy(w => w.From))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.AppliedWorkWeekKeys.Contains(ww.Key)) { counters.Reused++; continue; }
            ApplyResult result;
            try { result = await _repository.EnsureWorkWeekAsync(projectCalendarId, ww, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException and not CalendarImportException)
            { counters.Failed++; throw new CalendarImportException(ImportExitCodes.WorkWeeks, "WorkWeeks", $"Error aplicando WorkWeek {ww.From:yyyy-MM-dd}..{ww.To:yyyy-MM-dd}: {ex.Message}", ex); }

            if (result.Outcome == ApplyOutcome.Conflict)
            {
                counters.Failed++;
                throw new CalendarImportException(ImportExitCodes.WorkWeeks, "WorkWeeks",
                    $"Conflicto en WorkWeek {ww.From:yyyy-MM-dd}..{ww.To:yyyy-MM-dd}: {result.ConflictMessage}");
            }
            if (result.Outcome == ApplyOutcome.Created) counters.Created++; else counters.Reused++;
            state.AppliedWorkWeekKeys.Add(ww.Key);
            state.WorkWeeksApplied = state.AppliedWorkWeekKeys.Count;
            saveState();
        }
    }
}

public sealed class ProjectCalendarExceptionImporter
{
    private const int SaveEvery = 20;
    private readonly IProjectCalendarRepository _repository;
    public ProjectCalendarExceptionImporter(IProjectCalendarRepository repository) => _repository = repository;

    public async Task ApplyAsync(Guid projectCalendarId, IReadOnlyList<NormalizedCalendarException> exceptions, CalendarImportState state,
        StageCounters counters, Action saveState, CancellationToken cancellationToken)
    {
        counters.Found = exceptions.Count;
        var sinceSave = 0;
        foreach (var ex in exceptions.OrderBy(e => e.From).ThenBy(e => e.To))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.AppliedExceptionKeys.Contains(ex.Key)) { counters.Reused++; continue; }
            ApplyResult result;
            try { result = await _repository.EnsureExceptionAsync(projectCalendarId, ex, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException and not CalendarImportException)
            {
                counters.Failed++;
                saveState();
                throw new CalendarImportException(ImportExitCodes.CalendarExceptions, "CalendarExceptions", $"Error aplicando excepción {ex.From:yyyy-MM-dd}..{ex.To:yyyy-MM-dd}: {e.Message}", e);
            }
            if (result.Outcome == ApplyOutcome.Conflict)
            {
                counters.Failed++;
                saveState();
                throw new CalendarImportException(ImportExitCodes.CalendarExceptions, "CalendarExceptions",
                    $"Conflicto en excepción {ex.From:yyyy-MM-dd}..{ex.To:yyyy-MM-dd}: {result.ConflictMessage}");
            }
            if (result.Outcome == ApplyOutcome.Created) counters.Created++; else counters.Reused++;
            state.AppliedExceptionKeys.Add(ex.Key);
            state.ExceptionsApplied = state.AppliedExceptionKeys.Count;
            if (++sinceSave >= SaveEvery) { saveState(); sinceSave = 0; }
        }
        saveState();
    }
}
