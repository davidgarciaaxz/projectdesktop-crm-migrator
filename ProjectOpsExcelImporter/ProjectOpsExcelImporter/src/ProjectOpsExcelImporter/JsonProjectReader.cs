using System.Text.Json;

namespace ProjectOpsExcelImporter;

/// <summary>
/// Lee un proyecto desde JSON con el mismo formato que genera el comando <c>plan</c>
/// (objeto con "project" y "tasks"). Produce el mismo <see cref="WorkbookProject"/> que el lector de Excel,
/// de modo que validación e importación son idénticas para ambos orígenes.
/// </summary>
public sealed class JsonProjectReader
{
    private static readonly Guid GuidNamespace = Guid.Parse("3A11864E-38C5-4C98-9B62-687AF0D9CA1A");

    public WorkbookProject Read(string path, AppSettings settings)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("No se encontró el JSON.", path);

        JsonPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<JsonPlan>(File.ReadAllText(path), JsonDefaults.Options)
                   ?? throw new InvalidDataException("El JSON está vacío.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSON no válido: {ex.Message}", ex);
        }

        if (plan.Project is null) throw new InvalidDataException("Falta el objeto 'project' en el JSON.");
        if (string.IsNullOrWhiteSpace(plan.Project.Name)) throw new InvalidDataException("El proyecto no tiene 'name'.");
        if (plan.Tasks is null || plan.Tasks.Count == 0) throw new InvalidDataException("El JSON no contiene tareas ('tasks').");

        var issues = new List<ValidationIssue>(plan.ReaderIssues ?? []);
        var project = plan.Project;
        var tasks = plan.Tasks;

        if (project.SourceGuid == Guid.Empty)
        {
            project.SourceGuid = DeterministicGuid.Create(GuidNamespace, $"project:{project.SourceId}:{project.Name}");
            issues.Add(new ValidationIssue("Warning", "INVALID_SOURCE_GUID", "El proyecto no tiene sourceGuid; se generó un identificador estable."));
        }
        NormalizeRawValues(project.RawValues);

        var duplicate = tasks.GroupBy(t => t.SourceId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Id de tarea duplicado en el JSON: {duplicate.Key}.");

        // Si ninguna tarea trae excelRow, se usa la posición en el array (el importador ordena por este valor).
        var renumber = tasks.Any(t => t.ExcelRow <= 0);
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (renumber) task.ExcelRow = i + 1;
            if (string.IsNullOrWhiteSpace(task.Name))
            {
                task.Name = $"[SIN NOMBRE] Id {task.SourceId}";
                issues.Add(new ValidationIssue("Warning", "BLANK_TASK_NAME", $"La tarea Id {task.SourceId} no tiene nombre; se usará '{task.Name}'.", task.ExcelRow, task.SourceId));
            }
            else task.Name = task.Name.Trim();

            if (task.SourceGuid == Guid.Empty)
            {
                task.SourceGuid = DeterministicGuid.Create(GuidNamespace, $"json:{i}:id:{task.SourceId}");
                issues.Add(new ValidationIssue("Warning", "INVALID_SOURCE_GUID", $"GUID inválido; se generó un identificador estable para Id {task.SourceId}.", task.ExcelRow, task.SourceId));
            }

            // Los modelos son inmutables (record); se reconstruyen con el sucesor correcto cuando falta.
            for (var d = 0; d < task.Dependencies.Count; d++)
                if (task.Dependencies[d].SuccessorSourceId != task.SourceId)
                    task.Dependencies[d] = task.Dependencies[d] with { SuccessorSourceId = task.SourceId };

            NormalizeRawValues(task.RawValues);
        }

        BuildStructure(tasks, issues);

        return new WorkbookProject
        {
            Project = project,
            Tasks = tasks,
            ReaderIssues = issues,
            WorksheetName = Path.GetFileName(path)
        };
    }

    private static void BuildStructure(List<ProjectTaskRow> tasks, List<ValidationIssue> issues)
    {
        var byId = tasks.ToDictionary(t => t.SourceId);

        if (tasks.Any(t => t.ParentSourceId.HasValue))
        {
            // Jerarquía explícita por parentSourceId: se deriva el nivel recorriendo los padres.
            foreach (var task in tasks)
            {
                var level = 1;
                var current = task;
                var visited = new HashSet<int> { task.SourceId };
                while (current.ParentSourceId is { } parentId)
                {
                    if (!byId.TryGetValue(parentId, out var parent))
                    {
                        issues.Add(new ValidationIssue("Error", "MISSING_PARENT", $"La tarea padre {parentId} no existe.", task.ExcelRow, task.SourceId));
                        break;
                    }
                    if (!visited.Add(parent.SourceId))
                    {
                        issues.Add(new ValidationIssue("Error", "MISSING_PARENT", "Ciclo detectado en la jerarquía de tareas.", task.ExcelRow, task.SourceId));
                        break;
                    }
                    level++;
                    current = parent;
                }
                task.OutlineLevel = level;
                task.IsSummary = false;
            }
            foreach (var task in tasks.Where(t => t.ParentSourceId.HasValue && byId.ContainsKey(t.ParentSourceId.Value)))
                byId[task.ParentSourceId!.Value].IsSummary = true;
        }
        else
        {
            // Jerarquía por outlineLevel (orden del array); recalcula padres y tareas resumen.
            ExcelProjectReader.BuildHierarchy(tasks, issues);
        }
    }

    private static void NormalizeRawValues(Dictionary<string, object?> raw)
    {
        foreach (var key in raw.Keys.ToList())
        {
            if (raw[key] is not JsonElement element) continue;
            raw[key] = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.GetRawText()
            };
        }
    }

    // DTO tolerante: WorkbookProject exige propiedades 'required' que no tendrían sentido en un JSON escrito a mano.
    private sealed class JsonPlan
    {
        public ProjectRow? Project { get; set; }
        public List<ProjectTaskRow>? Tasks { get; set; }
        public List<ValidationIssue>? ReaderIssues { get; set; }
    }
}
