using MPXJ.Net;
using MppReaderTest.Models;
using Task = MPXJ.Net.Task;

namespace MppReaderTest;

/// <summary>
/// Extracts everything MPXJ.Net exposes from a ProjectFile into simple dump models.
/// Each read is guarded individually; failures are accumulated as warnings instead of aborting.
/// </summary>
public sealed partial class ProjectInspector
{
    private readonly ProjectFile _project;
    private readonly ProjectDump _dump;
    private readonly bool _dynamicDump;
    private readonly Dictionary<string, (string Message, int Count)> _warnings = new();
    private readonly List<string> _errors = new();
    private readonly List<Task> _tasks;
    private readonly List<Resource> _resources;
    private readonly List<ResourceAssignment> _assignments;

    public ProjectInspector(ProjectFile project, string sourceFile, string? readerName, bool dynamicDump)
    {
        _project = project;
        _dynamicDump = dynamicDump;
        _dump = new ProjectDump
        {
            SourceFile = sourceFile,
            GeneratedAtUtc = DateTime.UtcNow,
            ReaderUsed = readerName,
            MpxjNetVersion = MpxjVersion()
        };
        _tasks = Safe(() => project.Tasks.ToList(), "ProjectFile.Tasks") ?? new List<Task>();
        _resources = Safe(() => project.Resources.ToList(), "ProjectFile.Resources") ?? new List<Resource>();
        _assignments = Safe(() => project.ResourceAssignments.ToList(), "ProjectFile.ResourceAssignments") ?? new List<ResourceAssignment>();
        if (readerName != null && readerName != nameof(MPPReader))
        {
            Warn("reader", $"UniversalProjectReader detectó el lector '{readerName}' en lugar de MPPReader.");
        }
    }

    public static string MpxjVersion()
    {
        var asm = typeof(ProjectFile).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return info ?? asm.GetName().Version?.ToString() ?? "unknown";
    }

    public ProjectDump Inspect()
    {
        Step("Project information", ReadProjectInformation);
        Step("View tables", ReadViewTables);
        Step("Tasks", ReadTasks);
        Step("Visible columns", ReadVisibleColumns);
        Step("Dependencies", ReadDependencies);
        Step("Resources", ReadResources);
        Step("Resource rate tables", ReadRateTables);
        Step("Resource assignments", ReadAssignments);
        Step("Calendars", ReadCalendars);
        Step("Custom fields", ReadCustomFields);
        if (_dynamicDump) Step("Dynamic field dump", ReadDynamicFields);
        else _dump.DynamicFields.Note = "Volcado dinámico desactivado mediante --no-dynamic.";
        Step("Summary", BuildSummary);
        Step("Migration readiness", BuildReadiness);

        _dump.Warnings = _warnings.Values
            .Select(w => w.Count > 1 ? $"{w.Message} (x{w.Count})" : w.Message)
            .ToList();
        _dump.Errors = _errors.ToList();
        _dump.Summary.Warnings = _dump.Warnings.Count;
        _dump.Summary.Errors = _dump.Errors.Count;
        return _dump;
    }

    private void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _errors.Add($"Error en la sección '{name}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Records a warning once per key; repeated occurrences only increase the counter.</summary>
    private void Warn(string key, string message)
    {
        _warnings[key] = _warnings.TryGetValue(key, out var existing) ? (existing.Message, existing.Count + 1) : (message, 1);
    }

    private T? Safe<T>(Func<T?> getter, string context)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            Warn("safe:" + context, $"No se pudo leer {context}: {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    // ---------------------------------------------------------------- Project information

    private void ReadProjectInformation()
    {
        var p = _project.ProjectProperties;
        var info = _dump.Project;
        var currency = Safe(() => p.CurrencyCode ?? p.CurrencySymbol, "ProjectProperties.Currency");

        void Add(string name, string mpxj, Func<object?> getter, string? unit = null)
        {
            var value = Safe(getter, "ProjectProperties." + mpxj);
            info.Properties.Add(new PropertyEntry
            {
                Name = name,
                MpxjProperty = "ProjectProperties." + mpxj,
                Value = Fmt.ToJsonValue(value),
                FormattedValue = Fmt.Format(value),
                Unit = unit
            });
        }

        Add("Nombre del proyecto", nameof(p.Name), () => p.Name);
        Add("Título", nameof(p.ProjectTitle), () => p.ProjectTitle);
        Add("Asunto", nameof(p.Subject), () => p.Subject);
        Add("Autor", nameof(p.Author), () => p.Author);
        Add("Último autor", nameof(p.LastAuthor), () => p.LastAuthor);
        Add("Compañía", nameof(p.Company), () => p.Company);
        Add("Responsable", nameof(p.Manager), () => p.Manager);
        Add("Comentarios", nameof(p.Comments), () => p.Comments);
        Add("Palabras clave", nameof(p.Keywords), () => p.Keywords);
        Add("Categoría", nameof(p.Category), () => p.Category);
        Add("Notas", nameof(p.Notes), () => p.Notes);
        Add("Fecha de creación", nameof(p.CreationDate), () => p.CreationDate);
        Add("Fecha de última modificación (LastSaved)", nameof(p.LastSaved), () => p.LastSaved);
        Add("Fecha de comienzo", nameof(p.StartDate), () => p.StartDate);
        Add("Fecha de fin", nameof(p.FinishDate), () => p.FinishDate);
        Add("Fecha de estado", nameof(p.StatusDate), () => p.StatusDate);
        Add("Fecha actual configurada", nameof(p.CurrentDate), () => p.CurrentDate);
        Add("Programar desde (tipo de programación)", nameof(p.ScheduleFrom), () => p.ScheduleFrom);
        Add("Nuevas tareas manuales", nameof(p.NewTasksAreManual), () => p.NewTasksAreManual);
        Add("Nuevas tareas condicionadas por esfuerzo", nameof(p.NewTasksEffortDriven), () => p.NewTasksEffortDriven);
        Add("Tipo de tarea predeterminado", nameof(p.DefaultTaskType), () => p.DefaultTaskType);
        Add("Calendario predeterminado", nameof(p.DefaultCalendar), () => p.DefaultCalendar?.Name);
        Add("Calendario predeterminado (UID)", nameof(p.DefaultCalendarUniqueID), () => p.DefaultCalendarUniqueID);
        Add("Moneda (código)", nameof(p.CurrencyCode), () => p.CurrencyCode);
        Add("Símbolo de moneda", nameof(p.CurrencySymbol), () => p.CurrencySymbol);
        Add("Formato de moneda: posición del símbolo", nameof(p.SymbolPosition), () => p.SymbolPosition);
        Add("Formato de moneda: decimales", nameof(p.CurrencyDigits), () => p.CurrencyDigits);
        Add("Formato de moneda: separador de miles", nameof(p.ThousandsSeparator), () => p.ThousandsSeparator.ToString());
        Add("Formato de moneda: separador decimal", nameof(p.DecimalSeparator), () => p.DecimalSeparator.ToString());
        Add("Unidades de duración", nameof(p.DefaultDurationUnits), () => p.DefaultDurationUnits);
        Add("Unidades de trabajo", nameof(p.DefaultWorkUnits), () => p.DefaultWorkUnits);
        Add("Minutos por día", nameof(p.MinutesPerDay), () => p.MinutesPerDay, "minutes");
        Add("Minutos por semana", nameof(p.MinutesPerWeek), () => p.MinutesPerWeek, "minutes");
        Add("Minutos por mes", nameof(p.MinutesPerMonth), () => p.MinutesPerMonth, "minutes");
        Add("Minutos por año", nameof(p.MinutesPerYear), () => p.MinutesPerYear, "minutes");
        Add("Días por mes", nameof(p.DaysPerMonth), () => p.DaysPerMonth, "days");
        Add("Hora de inicio predeterminada", nameof(p.DefaultStartTime), () => p.DefaultStartTime);
        Add("Hora de fin predeterminada", nameof(p.DefaultEndTime), () => p.DefaultEndTime);
        Add("Inicio de semana", nameof(p.WeekStartDay), () => p.WeekStartDay);
        Add("Mes de inicio del año fiscal", nameof(p.FiscalYearStartMonth), () => p.FiscalYearStartMonth);
        Add("Tasa estándar predeterminada", nameof(p.DefaultStandardRate), () => p.DefaultStandardRate, currency + "/time");
        Add("Tasa horas extra predeterminada", nameof(p.DefaultOvertimeRate), () => p.DefaultOvertimeRate, currency + "/time");
        Add("Acumulación costo fijo predeterminada", nameof(p.DefaultFixedCostAccrual), () => p.DefaultFixedCostAccrual);
        Add("Respetar restricciones", nameof(p.HonorConstraints), () => p.HonorConstraints);
        Add("Múltiples rutas críticas", nameof(p.MultipleCriticalPaths), () => p.MultipleCriticalPaths);
        Add("Límite de margen crítico", nameof(p.CriticalSlackLimit), () => p.CriticalSlackLimit);
        Add("Línea base para valor acumulado", nameof(p.BaselineForEarnedValue), () => p.BaselineForEarnedValue);
        Add("Fecha de línea base", nameof(p.BaselineDate), () => p.BaselineDate);
        for (var i = 1; i <= 10; i++)
        {
            var idx = i;
            Add($"Fecha de línea base{idx}", $"GetBaselineDate({idx})", () => p.GetBaselineDate(idx));
        }
        Add("Mostrar tarea resumen del proyecto", nameof(p.ShowProjectSummaryTask), () => p.ShowProjectSummaryTask);
        Add("Coste del proyecto", nameof(p.Cost), () => p.Cost, currency);
        Add("Coste real del proyecto", nameof(p.ActualCost), () => p.ActualCost, currency);
        Add("Coste previsto del proyecto", nameof(p.BaselineCost), () => p.BaselineCost, currency);
        Add("Trabajo del proyecto", nameof(p.Work), () => p.Work);
        Add("Trabajo real del proyecto", nameof(p.ActualWork), () => p.ActualWork);
        Add("Duración del proyecto", nameof(p.Duration), () => p.Duration);
        Add("% completado del proyecto", nameof(p.PercentageComplete), () => p.PercentageComplete, "%");
        Add("GUID del proyecto", nameof(p.GUID), () => p.GUID);
        Add("Revisión", nameof(p.Revision), () => p.Revision);
        Add("Plantilla", nameof(p.Template), () => p.Template);
        Add("Base de hipervínculo", nameof(p.HyperlinkBase), () => p.HyperlinkBase);
        Add("Aplicación", nameof(p.FullApplicationName), () => p.FullApplicationName);
        Add("Versión de aplicación", nameof(p.ApplicationVersion), () => p.ApplicationVersion);
        Add("Tipo de fichero MPP", nameof(p.MppFileType), () => p.MppFileType);
        Add("Aplicación del fichero", nameof(p.FileApplication), () => p.FileApplication);
        Add("Tipo de fichero", nameof(p.FileType), () => p.FileType);
        Add("Ruta del fichero de proyecto", nameof(p.ProjectFilePath), () => p.ProjectFilePath);
        Add("Fichero de pool de recursos", nameof(p.ResourcePoolFile), () => p.ResourcePoolFile);
        info.Properties.Add(new PropertyEntry
        {
            Name = "Comienzo más temprano (calculado por MPXJ)",
            MpxjProperty = "ProjectFile.EarliestStartDate",
            Value = Safe(() => _project.EarliestStartDate, "ProjectFile.EarliestStartDate"),
            FormattedValue = Fmt.Date(Safe(() => _project.EarliestStartDate, "ProjectFile.EarliestStartDate"))
        });
        info.Properties.Add(new PropertyEntry
        {
            Name = "Fin más tardío (calculado por MPXJ)",
            MpxjProperty = "ProjectFile.LatestFinishDate",
            Value = Safe(() => _project.LatestFinishDate, "ProjectFile.LatestFinishDate"),
            FormattedValue = Fmt.Date(Safe(() => _project.LatestFinishDate, "ProjectFile.LatestFinishDate"))
        });

        var customProps = Safe(() => p.CustomProperties, "ProjectProperties.CustomProperties");
        if (customProps != null)
        {
            foreach (var kv in customProps)
            {
                info.CustomProperties.Add(new PropertyEntry
                {
                    Name = kv.Key,
                    MpxjProperty = "ProjectProperties.CustomProperties",
                    Value = Fmt.ToJsonValue(kv.Value),
                    FormattedValue = Fmt.Format(kv.Value)
                });
            }
        }

        var explicitNames = new HashSet<string>(info.Properties.Select(x => x.MpxjProperty.Replace("ProjectProperties.", "")), StringComparer.OrdinalIgnoreCase);
        var populated = Safe(() => p.PopulatedFields?.ToList(), "ProjectProperties.PopulatedFields") ?? new List<IFieldType>();
        foreach (var field in populated.OrderBy(f => f.FieldName))
        {
            var member = FieldCatalog.MemberName(field);
            if (explicitNames.Contains(member)) continue;
            var value = Safe(() => p.Get(field), "ProjectProperties.Get(" + member + ")");
            if (Fmt.IsEmpty(value)) continue;
            info.OtherPopulatedProjectFields.Add(new PropertyEntry
            {
                Name = field.FieldName,
                MpxjProperty = FieldCatalog.Ident(field),
                Value = Fmt.ToJsonValue(value),
                FormattedValue = Fmt.Format(value),
                Unit = field.DataType?.ToString()
            });
        }

        var currencies = Safe(() => _project.Currencies?.ToList(), "ProjectFile.Currencies");
        if (currencies != null)
        {
            foreach (var c in currencies)
            {
                info.Currencies.Add(new CurrencyDump
                {
                    UniqueId = c.UniqueID,
                    CurrencyId = c.CurrencyId,
                    Name = c.Name,
                    Symbol = c.Symbol,
                    ExchangeRate = c.ExchangeRate,
                    PositiveFormat = c.PositiveCurrencyFormat,
                    NegativeFormat = c.NegativeCurrencyFormat
                });
            }
        }
    }

    // ---------------------------------------------------------------- Summary

    private void BuildSummary()
    {
        var s = _dump.Summary;
        var tasks = _dump.Tasks;
        var realTasks = tasks.Where(t => !t.IsProjectSummaryTask).ToList();
        var leafTasks = realTasks.Where(t => !t.IsSummary).ToList();
        var summaryTask = tasks.FirstOrDefault(t => t.IsProjectSummaryTask);

        s.Tasks = realTasks.Count;
        s.ProjectSummaryTaskPresent = summaryTask != null;
        s.SummaryTasks = realTasks.Count(t => t.IsSummary);
        s.Milestones = realTasks.Count(t => t.IsMilestone);
        s.InactiveTasks = realTasks.Count(t => t.IsInactive);
        s.Dependencies = _dump.Dependencies.Count;
        s.Resources = _dump.Resources.Count(r => !IsPlaceholderResource(r));
        s.WorkResources = _dump.Resources.Count(r => !IsPlaceholderResource(r) && r.Type == nameof(ResourceType.Work));
        s.MaterialResources = _dump.Resources.Count(r => !IsPlaceholderResource(r) && r.Type == nameof(ResourceType.Material));
        s.CostResources = _dump.Resources.Count(r => !IsPlaceholderResource(r) && r.Type == nameof(ResourceType.Cost));
        s.ResourceAssignments = _dump.ResourceAssignments.Count;
        s.ResourceRateEntries = _dump.ResourceRateTables.Count;
        s.Calendars = _dump.Calendars.Count;
        s.CalendarExceptions = _dump.CalendarExceptions.Count;
        s.CustomFieldDefinitions = _dump.CustomFieldDefinitions.Count;
        s.CustomFieldValues = _dump.CustomFieldValues.Count;
        s.CustomFieldValuesNonEmpty = _dump.CustomFieldValues.Count(v => v.ValueState == CustomFieldValueState.DefinedWithValue);
        s.DynamicFieldValues = _dump.DynamicFieldValues.Count;

        s.TasksWithCost = realTasks.Count(t => (t.Cost ?? 0) != 0);
        s.TasksWithActualCost = realTasks.Count(t => (t.ActualCost ?? 0) != 0);
        s.TasksWithBaseline6Cost = realTasks.Count(t => (B6(t) ?? 0) != 0);

        s.TotalTaskCostAllTasks = realTasks.Sum(t => t.Cost ?? 0);
        s.TotalTaskCostNonSummaryTasks = leafTasks.Sum(t => t.Cost ?? 0);
        s.TotalTaskActualCostAllTasks = realTasks.Sum(t => t.ActualCost ?? 0);
        s.TotalTaskActualCostNonSummaryTasks = leafTasks.Sum(t => t.ActualCost ?? 0);
        s.TotalTaskRemainingCostAllTasks = realTasks.Sum(t => t.RemainingCost ?? 0);
        s.TotalTaskRemainingCostNonSummaryTasks = leafTasks.Sum(t => t.RemainingCost ?? 0);
        s.TotalTaskBaseline6CostAllTasks = realTasks.Sum(t => B6(t) ?? 0);
        s.TotalTaskBaseline6CostNonSummaryTasks = leafTasks.Sum(t => B6(t) ?? 0);
        s.ProjectSummaryTaskCost = summaryTask?.Cost;
        s.ProjectSummaryTaskBaseline6Cost = summaryTask == null ? null : B6(summaryTask);
        s.TotalsNote = "Totales solo como comprobación del contenido leído. 'AllTasks' incluye tareas resumen (que ya agregan a sus subtareas) " +
                       "y por tanto duplica importes; 'NonSummaryTasks' solo suma tareas no resumen. Ninguno excluye la tarea resumen del proyecto (UID 0) salvo indicación; ésta se muestra aparte.";

        static double? B6(TaskDump t) => t.Baselines.FirstOrDefault(b => b.Index == 6)?.Cost;
    }

    private static bool IsPlaceholderResource(ResourceDump r) => r.UniqueId == 0 && string.IsNullOrEmpty(r.Name);

    // ---------------------------------------------------------------- Migration readiness

    private void BuildReadiness()
    {
        var list = _dump.MigrationReadiness;
        var realTasks = _dump.Tasks.Where(t => !t.IsProjectSummaryTask).ToList();
        var resources = _dump.Resources.Where(r => !IsPlaceholderResource(r)).ToList();
        var workResources = resources.Where(r => r.Type == nameof(ResourceType.Work)).ToList();

        void Add(string item, ReadinessStatus status, ReadinessReason reason, int count, string observation) =>
            list.Add(new ReadinessCheckDump { Item = item, Status = status, Reason = reason, Count = count, Observation = observation });

        void Ratio(string item, int ok, int total, string api, string what)
        {
            if (total == 0)
            {
                Add(item, ReadinessStatus.NO, ReadinessReason.NotPresentInFile, 0, $"API disponible ({api}); el fichero no contiene {what}.");
            }
            else if (ok == total)
            {
                Add(item, ReadinessStatus.YES, ReadinessReason.ValuesRetrieved, ok, $"{ok}/{total} con valor. API: {api}.");
            }
            else if (ok > 0)
            {
                Add(item, ReadinessStatus.PARTIAL, ReadinessReason.ValuesRetrieved, ok, $"{ok}/{total} con valor. API: {api}.");
            }
            else
            {
                Add(item, ReadinessStatus.PARTIAL, ReadinessReason.DefinitionFoundWithoutValues, 0, $"API leída correctamente ({api}) pero 0/{total} con valor.");
            }
        }

        // Task hierarchy
        var withParent = realTasks.Count(t => t.ParentUniqueId != null);
        if (realTasks.Count == 0)
            Add("Task hierarchy", ReadinessStatus.NO, ReadinessReason.NotPresentInFile, 0, "El fichero no contiene tareas.");
        else
            Add("Task hierarchy", withParent > 0 ? ReadinessStatus.YES : ReadinessStatus.PARTIAL, ReadinessReason.ValuesRetrieved, realTasks.Count,
                $"Task.ParentTask/ChildTasks/OutlineLevel/OutlineNumber/WBS. {withParent} tareas con padre, {realTasks.Count(t => t.IsSummary)} resumen, niveles 1..{realTasks.Max(t => t.OutlineLevel ?? 0)}.");

        var scheduled = realTasks.Where(t => !t.IsNull).ToList();
        Ratio("Task dates and durations", scheduled.Count(t => t.Start != null && t.Finish != null && t.Duration != null), scheduled.Count,
            "Task.Start/Finish/Duration", "tareas");
        var workCount = scheduled.Count(t => t.Work != null);
        var workNonZero = scheduled.Count(t => (t.Work?.Value ?? 0) != 0);
        Add("Task work", workCount == 0 ? ReadinessStatus.NO : workNonZero > 0 ? ReadinessStatus.YES : ReadinessStatus.PARTIAL,
            workCount == 0 ? ReadinessReason.NotPresentInFile : workNonZero > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.DefinitionFoundWithoutValues,
            workNonZero, $"Task.Work/ActualWork/RemainingWork. {workCount} tareas con Work leído, {workNonZero} distinto de 0.");

        var deps = _dump.Dependencies.Count;
        Add("Predecessors and successors", deps > 0 ? ReadinessStatus.YES : ReadinessStatus.NO,
            deps > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.NotPresentInFile, deps,
            deps > 0 ? $"{deps} dependencias únicas (ProjectFile.Relations + Task.Predecessors/Successors), tipos: {string.Join(", ", _dump.Dependencies.GroupBy(d => d.Abbreviation).Select(g => $"{g.Key}={g.Count()}"))}."
                     : "API disponible (ProjectFile.Relations, Task.Predecessors); el fichero no contiene dependencias.");

        var b6Read = realTasks.Count(t => t.Baselines.Any(b => b.Index == 6));
        var b6NonZero = realTasks.Count(t => (t.Baselines.FirstOrDefault(b => b.Index == 6)?.Cost ?? 0) != 0);
        Add("Baseline6 Cost", b6NonZero > 0 ? ReadinessStatus.YES : b6Read > 0 ? ReadinessStatus.PARTIAL : ReadinessStatus.NO,
            b6NonZero > 0 ? ReadinessReason.ValuesRetrieved : b6Read > 0 ? ReadinessReason.DefinitionFoundWithoutValues : ReadinessReason.ReadError,
            b6NonZero, $"Task.GetBaselineCost(6) (= TaskField.Baseline6Cost, distinto de Task.BaselineCost). {b6NonZero} tareas con valor distinto de 0.");

        var costNonZero = realTasks.Count(t => (t.Cost ?? 0) != 0);
        Add("Standard task costs", costNonZero > 0 ? ReadinessStatus.YES : ReadinessStatus.PARTIAL,
            costNonZero > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.DefinitionFoundWithoutValues, costNonZero,
            $"Task.Cost/ActualCost/RemainingCost/FixedCost/CostVariance. {costNonZero} tareas con Cost distinto de 0, {realTasks.Count(t => (t.ActualCost ?? 0) != 0)} con ActualCost.");

        var defs = _dump.CustomFieldDefinitions.Count;
        var cfValues = _dump.CustomFieldValues.Count(v => v.ValueState == CustomFieldValueState.DefinedWithValue);
        Add("Custom fields", defs == 0 ? ReadinessStatus.NO : cfValues > 0 ? ReadinessStatus.YES : ReadinessStatus.PARTIAL,
            defs == 0 ? ReadinessReason.DefinitionNotFound : cfValues > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.DefinitionFoundWithoutValues,
            defs, $"{defs} definiciones (alias/valores/vistas), {cfValues} valores no vacíos ni cero. Fórmulas: API no disponible en MPXJ.");

        foreach (var name in CustomVisibleColumns)
        {
            var col = _dump.VisibleColumns.FirstOrDefault(c => c.DisplayName == name);
            if (col == null || !col.Resolved)
            {
                Add(name, ReadinessStatus.NO, ReadinessReason.DefinitionNotFound, 0,
                    "No se encontró ningún campo con ese alias en CustomFields ni en las tablas de vista del MPP.");
            }
            else if (col.TasksWithNonZeroValue > 0)
            {
                Add(name, ReadinessStatus.YES, ReadinessReason.ValuesRetrieved, col.TasksWithNonZeroValue,
                    $"{col.FieldType} ({col.DataType}) vía {col.AliasSource}. {col.TasksWithNonZeroValue} tareas con valor distinto de 0; tarea resumen del proyecto = {Fmt.Str(col.ProjectSummaryTaskValue)}. Fórmula: no expuesta por MPXJ.");
            }
            else if (col.ProjectSummaryTaskHasNonZeroValue)
            {
                Add(name, ReadinessStatus.YES, ReadinessReason.ValuesRetrieved, 1,
                    $"{col.FieldType} ({col.DataType}) vía {col.AliasSource}. Solo la tarea resumen del proyecto (UID 0) tiene valor distinto de 0: {col.ProjectSummaryTaskValue}; el resto de tareas tienen 0/vacío. Fórmula: no expuesta por MPXJ.");
            }
            else
            {
                Add(name, ReadinessStatus.PARTIAL, ReadinessReason.DefinitionFoundWithoutValues, 0,
                    $"{col.FieldType} ({col.DataType}) vía {col.AliasSource}; todas las tareas tienen valor vacío o 0. Si en MS Project es un campo calculado por fórmula, MPXJ solo lee el valor almacenado.");
            }
        }

        Add("Resources", resources.Count > 0 ? ReadinessStatus.YES : ReadinessStatus.NO,
            resources.Count > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.NotPresentInFile, resources.Count,
            $"Work={workResources.Count}, Material={resources.Count(r => r.Type == nameof(ResourceType.Material))}, Cost={resources.Count(r => r.Type == nameof(ResourceType.Cost))}.");

        Ratio("Resource calendars", workResources.Count(r => r.CalendarUniqueId != null), workResources.Count,
            "Resource.Calendar/CalendarUniqueID", "recursos de trabajo");
        RateCheck("Resource Standard Rate", resources, r => r.StandardRate?.Amount, "Resource.StandardRate");
        RateCheck("Resource Overtime Rate", resources, r => r.OvertimeRate?.Amount, "Resource.OvertimeRate");
        RateCheck("Resource Cost Per Use", resources, r => r.CostPerUse, "Resource.CostPerUse");

        var entries = _dump.ResourceRateTables.Count;
        var resWithChanges = _dump.ResourceRateTables.GroupBy(e => (e.ResourceUniqueId, e.RateTable)).Count(g => g.Count() > 1);
        var populatedTables = _dump.ResourceRateTables.Where(e => e.TableIsPopulated).Select(e => (e.ResourceUniqueId, e.RateTable)).Distinct().Count();
        Add("Resource rate tables", entries > 0 ? ReadinessStatus.YES : ReadinessStatus.NO,
            entries > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.NotPresentInFile, entries,
            $"Resource.GetCostRateTable(0..4) (A..E). {entries} entradas, {populatedTables} tablas pobladas, {resWithChanges} tablas con cambios de tarifa por fecha.");

        var asg = _dump.ResourceAssignments.Count;
        Add("Resource assignments", asg > 0 ? ReadinessStatus.YES : ReadinessStatus.NO,
            asg > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.NotPresentInFile, asg,
            $"ProjectFile.ResourceAssignments. Sin recurso: {_dump.ResourceAssignments.Count(a => a.ResourceMissing)}, sin tarea: {_dump.ResourceAssignments.Count(a => a.TaskMissing)}.");
        var asgWorkCost = _dump.ResourceAssignments.Count(a => a.Work != null && a.Cost != null);
        var asgCostNonZero = _dump.ResourceAssignments.Count(a => (a.Cost ?? 0) != 0);
        Add("Assignment work and cost", asg == 0 ? ReadinessStatus.NO : asgCostNonZero > 0 ? ReadinessStatus.YES : ReadinessStatus.PARTIAL,
            asg == 0 ? ReadinessReason.NotPresentInFile : asgCostNonZero > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.DefinitionFoundWithoutValues,
            asgWorkCost, $"{asgWorkCost}/{asg} con Work y Cost leídos; {asgCostNonZero} con Cost distinto de 0. Validación de coste: {_dump.ResourceAssignments.Count(a => a.CostValidation?.Difference is { } d && Math.Abs(d) < 0.01)} coinciden (<0.01).");

        var cals = _dump.Calendars.Count;
        Add("Project calendars", cals > 0 ? ReadinessStatus.YES : ReadinessStatus.NO,
            cals > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.NotPresentInFile, cals,
            $"{_dump.Calendars.Count(c => c.IsBaseCalendar)} base, {_dump.Calendars.Count(c => c.IsDerived)} derivados, {_dump.Calendars.Count(c => c.ResourceUniqueIds.Count > 0)} con recursos, {_dump.Calendars.Count(c => c.TaskUniqueIds.Count > 0)} con tareas.");
        var withPeriods = _dump.Calendars.Count(c => c.Days.Any(d => d.EffectivePeriods.Count > 0));
        Ratio("Working time periods", withPeriods, cals, "ProjectCalendar.GetCalendarHours/GetHours(DayOfWeek)", "calendarios");
        var exc = _dump.CalendarExceptions.Count;
        Add("Calendar exceptions", exc > 0 ? ReadinessStatus.YES : ReadinessStatus.NO,
            exc > 0 ? ReadinessReason.ValuesRetrieved : ReadinessReason.NotPresentInFile, exc,
            exc > 0 ? $"ProjectCalendar.CalendarExceptions. {_dump.CalendarExceptions.Count(e => e.IsRecurring)} recurrentes, {_dump.CalendarExceptions.Count(e => e.Working)} laborables con horario especial."
                    : "API disponible (ProjectCalendar.CalendarExceptions); el fichero no contiene excepciones.");

        if (_errors.Count > 0)
        {
            foreach (var r in list.Where(r => r.Status != ReadinessStatus.YES))
            {
                r.Observation += " (Ver sección ERRORS: hubo errores de lectura en alguna sección.)";
            }
        }

        void RateCheck(string item, List<ResourceDump> res, Func<ResourceDump, double?> getter, string api)
        {
            var read = res.Count(r => getter(r) != null);
            var nonZero = res.Count(r => (getter(r) ?? 0) != 0);
            if (res.Count == 0)
                Add(item, ReadinessStatus.NO, ReadinessReason.NotPresentInFile, 0, $"API disponible ({api}); no hay recursos.");
            else if (nonZero > 0)
                Add(item, ReadinessStatus.YES, ReadinessReason.ValuesRetrieved, nonZero, $"{api}: {nonZero}/{res.Count} recursos con valor distinto de 0 ({read} leídos).");
            else
                Add(item, ReadinessStatus.PARTIAL, ReadinessReason.DefinitionFoundWithoutValues, 0, $"{api} leído en {read}/{res.Count} recursos, pero todos son 0.");
        }
    }
}
