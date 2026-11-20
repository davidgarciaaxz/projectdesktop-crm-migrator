namespace ProjectOpsExcelImporter;

public static class ProjectValidator
{
    public static ValidationReport Validate(WorkbookProject workbook)
    {
        var issues = new List<ValidationIssue>(workbook.ReaderIssues);
        var tasks = workbook.Tasks;
        var ids = tasks.Select(t => t.SourceId).ToHashSet();
        var duplicates = tasks.GroupBy(t => t.SourceId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var id in duplicates) issues.Add(new ValidationIssue("Error", "DUPLICATE_TASK_ID", $"Id de tarea duplicado: {id}.", SourceTaskId: id));

        var dependencies = tasks.SelectMany(t => t.Dependencies).ToList();
        foreach (var dep in dependencies)
        {
            if (!ids.Contains(dep.PredecessorSourceId))
                issues.Add(new ValidationIssue("Error", "MISSING_PREDECESSOR", $"La predecesora {dep.PredecessorSourceId} no existe.", SourceTaskId: dep.SuccessorSourceId));
            if (dep.PredecessorSourceId == dep.SuccessorSourceId)
                issues.Add(new ValidationIssue("Error", "SELF_DEPENDENCY", "La tarea depende de sí misma.", SourceTaskId: dep.SuccessorSourceId));
        }

        var duplicateDeps = dependencies.GroupBy(d => new { d.PredecessorSourceId, d.SuccessorSourceId, d.Type, d.LagDays }).Where(g => g.Count() > 1);
        foreach (var group in duplicateDeps)
            issues.Add(new ValidationIssue("Error", "DUPLICATE_DEPENDENCY", $"Dependencia duplicada {group.Key.PredecessorSourceId} -> {group.Key.SuccessorSourceId}.", SourceTaskId: group.Key.SuccessorSourceId));

        var links = new Dictionary<int, int>();
        foreach (var dep in dependencies)
        {
            links[dep.PredecessorSourceId] = links.GetValueOrDefault(dep.PredecessorSourceId) + 1;
            links[dep.SuccessorSourceId] = links.GetValueOrDefault(dep.SuccessorSourceId) + 1;
        }
        foreach (var pair in links.Where(p => p.Value > 20))
            issues.Add(new ValidationIssue("Error", "TASK_LINK_LIMIT", $"La tarea {pair.Key} tiene {pair.Value} vínculos; Project Operations admite 20 por tarea.", SourceTaskId: pair.Key));

        foreach (var task in tasks)
        {
            if (task.OutlineLevel > 10) issues.Add(new ValidationIssue("Error", "HIERARCHY_LIMIT", $"Nivel {task.OutlineLevel}; el máximo es 10.", task.ExcelRow, task.SourceId));
            if (!task.IsSummary && task.DurationDays > 1250) issues.Add(new ValidationIssue("Error", "LEAF_DURATION_LIMIT", $"Duración {task.DurationDays} días; el máximo de una tarea hoja es 1.250.", task.ExcelRow, task.SourceId));
            if (task.Resources.Select(r => r.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 50)
                issues.Add(new ValidationIssue("Error", "TASK_RESOURCE_LIMIT", "Más de 50 recursos asignados a una tarea.", task.ExcelRow, task.SourceId));
            if (task.Start is { Year: < 2000 or > 2149 } || task.Finish is { Year: < 2000 or > 2149 })
                issues.Add(new ValidationIssue("Error", "DATE_RANGE_LIMIT", "Fecha fuera del intervalo soportado 2000-2149.", task.ExcelRow, task.SourceId));
            if (task.Start.HasValue && task.Finish.HasValue && task.Finish < task.Start)
                issues.Add(new ValidationIssue("Error", "FINISH_BEFORE_START", "La fecha fin es anterior al comienzo.", task.ExcelRow, task.SourceId));
            if (task.IsSummary && task.Resources.Count > 0)
                issues.Add(new ValidationIssue("Warning", "SUMMARY_RESOURCES_SKIPPED", "Los recursos de tareas resumen no se importarán; Project Operations asigna recursos a tareas hoja.", task.ExcelRow, task.SourceId));
        }

        if (tasks.Count > 3000) issues.Add(new ValidationIssue("Error", "PROJECT_TASK_LIMIT", $"El proyecto tiene {tasks.Count} tareas; el máximo es 3.000."));
        if (dependencies.Count > 2000) issues.Add(new ValidationIssue("Error", "PROJECT_LINK_LIMIT", $"El proyecto tiene {dependencies.Count} dependencias; el máximo es 2.000."));
        var resourceNames = tasks.SelectMany(t => t.Resources).Select(r => r.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (resourceNames.Count > 1000) issues.Add(new ValidationIssue("Error", "PROJECT_RESOURCE_LIMIT", $"El proyecto tiene {resourceNames.Count} recursos; el máximo es 1.000."));

        var minDate = tasks.Where(t => t.Start.HasValue).Select(t => t.Start!.Value).DefaultIfEmpty().Min();
        var maxDate = tasks.Where(t => t.Finish.HasValue).Select(t => t.Finish!.Value).DefaultIfEmpty().Max();
        if (minDate != default && maxDate != default && maxDate.DayNumber - minDate.DayNumber > 5475)
            issues.Add(new ValidationIssue("Error", "PROJECT_DURATION_LIMIT", "La duración total supera 5.475 días."));

        var lags = dependencies.Count(d => d.LagDays != 0);
        if (lags > 0)
            issues.Add(new ValidationIssue("Warning", "DEPENDENCY_LAG_NOT_STANDARD", $"Hay {lags} dependencias con adelanto/retardo. Se conserva el texto y las fechas; para guardar el lag explícito configure DependencyLagCustomField."));

        var nonBinaryProgress = tasks.Count(t => t.Progress is > 0 and < 1);
        if (nonBinaryProgress > 0)
            issues.Add(new ValidationIssue("Warning", "NON_BINARY_PROGRESS", $"Hay {nonBinaryProgress} tareas con avance entre 0 y 100%. Solo se importará con ProgressPolicy=All y 'Allow percent complete update' habilitado."));

        var metrics = new Dictionary<string, object?>
        {
            ["worksheet"] = workbook.WorksheetName,
            ["taskRows"] = tasks.Count,
            ["summaryTasks"] = tasks.Count(t => t.IsSummary),
            ["leafTasks"] = tasks.Count(t => !t.IsSummary),
            ["milestones"] = tasks.Count(t => t.IsMilestone),
            ["maxHierarchyLevel"] = tasks.Max(t => t.OutlineLevel),
            ["dependencies"] = dependencies.Count,
            ["dependencyRows"] = tasks.Count(t => t.Dependencies.Count > 0),
            ["dependenciesWithLag"] = lags,
            ["dependencyTypes"] = dependencies.GroupBy(d => d.Type.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ["resourceAssignmentTokens"] = tasks.Sum(t => t.Resources.Count),
            ["distinctResources"] = resourceNames.Count,
            ["tasksWithWork"] = tasks.Count(t => t.WorkHours > 0),
            ["tasksWithCost"] = tasks.Count(t => t.Cost != 0),
            ["tasksWithNonBinaryProgress"] = nonBinaryProgress,
            ["projectStart"] = minDate == default ? null : minDate.ToString("yyyy-MM-dd"),
            ["projectFinish"] = maxDate == default ? null : maxDate.ToString("yyyy-MM-dd")
        };
        return new ValidationReport { ProjectName = workbook.Project.Name, Metrics = metrics, Issues = issues };
    }
}
