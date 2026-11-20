using MPXJ.Net;
using MppReaderTest.Models;
using Task = MPXJ.Net.Task;

namespace MppReaderTest;

public sealed partial class ProjectInspector
{
    public static readonly string[] CustomVisibleColumns = { "SÁBANA", "VARIACIÓN SÁBANA", "FACTURACIÓN", "COBROS" };

    private sealed record StandardColumn(string DisplayName, IFieldType Field, string MpxjProperty, string Unit);

    private List<StandardColumn> StandardColumns()
    {
        var currency = Safe(() => _project.ProjectProperties.CurrencyCode ?? _project.ProjectProperties.CurrencySymbol, "Currency") ?? "currency";
        return new List<StandardColumn>
        {
            new("% completado", TaskField.PercentComplete, "Task.PercentageComplete", "%"),
            new("Comienzo", TaskField.Start, "Task.Start", "DateTime"),
            new("Fin", TaskField.Finish, "Task.Finish", "DateTime"),
            new("Duración", TaskField.Duration, "Task.Duration", "Duration (value + TimeUnit)"),
            new("Trabajo", TaskField.Work, "Task.Work", "Duration (value + TimeUnit)"),
            new("Predecesoras", TaskField.Predecessors, "Task.Predecessors", "List<Relation>"),
            new("Sucesoras", TaskField.Successors, "Task.Successors", "List<Relation>"),
            new("Costo de línea base6", TaskField.Baseline6Cost, "Task.GetBaselineCost(6)", currency),
            new("Costo", TaskField.Cost, "Task.Cost", currency),
            new("Costo real", TaskField.ActualCost, "Task.ActualCost", currency),
            new("Variación de costo", TaskField.CostVariance, "Task.CostVariance", currency),
            new("Costo restante", TaskField.RemainingCost, "Task.RemainingCost", currency),
            new("Nombres de los recursos", TaskField.ResourceNames, "Task.ResourceNames", "text"),
        };
    }

    // ---------------------------------------------------------------- View tables

    private void ReadViewTables()
    {
        var tables = Safe(() => _project.Tables?.ToList(), "ProjectFile.Tables") ?? new List<Table>();
        foreach (var table in tables)
        {
            var dump = new ViewTableDump { Id = table.ID, Name = table.Name, IsResourceTable = table.ResourceFlag };
            var columns = Safe(() => table.Columns?.ToList(), $"Table[{table.Name}].Columns") ?? new List<Column>();
            foreach (var c in columns)
            {
                var ft = Safe(() => c.FieldType, "Column.FieldType");
                dump.Columns.Add(new ViewColumnDump
                {
                    Title = Safe(() => c.Title, "Column.Title"),
                    FieldType = ft == null ? null : FieldCatalog.Ident(ft),
                    FieldName = ft?.FieldName,
                    DataType = ft?.DataType?.ToString()
                });
            }
            _dump.ViewTables.Add(dump);
        }
    }

    // ---------------------------------------------------------------- Tasks

    private void ReadTasks()
    {
        foreach (var task in _tasks)
        {
            _dump.Tasks.Add(ReadTask(task));
        }
    }

    private TaskDump ReadTask(Task t)
    {
        var ctx = $"Task[UID={Safe(() => t.UniqueID, "Task.UniqueID")}]";
        var parent = Safe(() => t.ParentTask, ctx + ".ParentTask");
        var children = Safe(() => t.ChildTasks?.ToList(), ctx + ".ChildTasks") ?? new List<Task>();
        var mode = Safe(() => t.TaskMode, ctx + ".TaskMode");
        var active = Safe(() => t.Active, ctx + ".Active");
        var duration = Safe(() => t.Duration, ctx + ".Duration");
        var calendar = Safe(() => t.Calendar, ctx + ".Calendar");
        var uid = Safe(() => t.UniqueID, ctx + ".UniqueID");
        var id = Safe(() => t.ID, ctx + ".ID");

        var dump = new TaskDump
        {
            Id = id,
            UniqueId = uid,
            Guid = Safe(() => t.GUID, ctx + ".GUID"),
            Name = Safe(() => t.Name, ctx + ".Name"),
            Wbs = Safe(() => t.WBS, ctx + ".WBS"),
            OutlineNumber = Safe(() => t.OutlineNumber, ctx + ".OutlineNumber"),
            OutlineLevel = Safe(() => t.OutlineLevel, ctx + ".OutlineLevel"),
            ParentId = parent?.ID,
            ParentUniqueId = parent?.UniqueID,
            ParentName = parent?.Name,
            ChildIds = children.Select(c => c.ID).ToList(),
            ChildUniqueIds = children.Select(c => c.UniqueID).ToList(),
            IsProjectSummaryTask = uid == 0,
            IsSummary = Safe(() => t.Summary, ctx + ".Summary"),
            IsMilestone = Safe(() => t.Milestone, ctx + ".Milestone"),
            IsCritical = Safe(() => t.Critical, ctx + ".Critical"),
            IsActive = active,
            IsInactive = !active,
            TaskMode = mode?.ToString(),
            IsManuallyScheduled = mode == MPXJ.Net.TaskMode.ManuallyScheduled,
            IsNull = Safe(() => t.Null, ctx + ".Null"),
            IsExternalTask = Safe(() => t.ExternalTask, ctx + ".ExternalTask"),
            Priority = Safe(() => t.Priority?.Value, ctx + ".Priority"),
            PercentComplete = Safe(() => t.PercentageComplete, ctx + ".PercentageComplete"),
            PercentWorkComplete = Safe(() => t.PercentageWorkComplete, ctx + ".PercentageWorkComplete"),
            PhysicalPercentComplete = Safe(() => t.PhysicalPercentComplete, ctx + ".PhysicalPercentComplete"),
            Start = Safe(() => t.Start, ctx + ".Start"),
            Finish = Safe(() => t.Finish, ctx + ".Finish"),
            ActualStart = Safe(() => t.ActualStart, ctx + ".ActualStart"),
            ActualFinish = Safe(() => t.ActualFinish, ctx + ".ActualFinish"),
            Duration = Fmt.ToDump(duration),
            DurationIsEstimated = Safe(() => t.Estimated, ctx + ".Estimated"),
            Work = Fmt.ToDump(Safe(() => t.Work, ctx + ".Work")),
            ActualWork = Fmt.ToDump(Safe(() => t.ActualWork, ctx + ".ActualWork")),
            RemainingWork = Fmt.ToDump(Safe(() => t.RemainingWork, ctx + ".RemainingWork")),
            Cost = Safe(() => t.Cost, ctx + ".Cost"),
            ActualCost = Safe(() => t.ActualCost, ctx + ".ActualCost"),
            RemainingCost = Safe(() => t.RemainingCost, ctx + ".RemainingCost"),
            FixedCost = Safe(() => t.FixedCost, ctx + ".FixedCost"),
            FixedCostAccrual = Safe(() => t.FixedCostAccrual, ctx + ".FixedCostAccrual")?.ToString(),
            CostVariance = Safe(() => t.CostVariance, ctx + ".CostVariance"),
            ConstraintType = Safe(() => t.ConstraintType, ctx + ".ConstraintType")?.ToString(),
            ConstraintDate = Safe(() => t.ConstraintDate, ctx + ".ConstraintDate"),
            Deadline = Safe(() => t.Deadline, ctx + ".Deadline"),
            TaskType = Safe(() => t.Type, ctx + ".Type")?.ToString(),
            EffortDriven = Safe(() => t.EffortDriven, ctx + ".EffortDriven"),
            CalendarUniqueId = Safe(() => t.CalendarUniqueID, ctx + ".CalendarUniqueID"),
            CalendarName = calendar?.Name,
            EffectiveCalendarName = Safe(() => t.EffectiveCalendar?.Name, ctx + ".EffectiveCalendar"),
            ResourceNames = Safe(() => t.ResourceNames, ctx + ".ResourceNames"),
            Notes = Safe(() => t.Notes, ctx + ".Notes"),
            Hyperlink = Safe(() => t.Hyperlink, ctx + ".Hyperlink"),
            HyperlinkAddress = Safe(() => t.HyperlinkAddress, ctx + ".HyperlinkAddress"),
            HyperlinkSubAddress = Safe(() => t.HyperlinkSubAddress, ctx + ".HyperlinkSubAddress"),
            HyperlinkScreenTip = Safe(() => t.HyperlinkScreenTip, ctx + ".HyperlinkScreenTip"),
        };

        var assignments = Safe(() => t.ResourceAssignments?.ToList(), ctx + ".ResourceAssignments") ?? new List<ResourceAssignment>();
        dump.AssignedResourceNames = assignments.Select(a => a.Resource?.Name ?? "(sin recurso)").ToList();

        var preds = Safe(() => t.Predecessors?.ToList(), ctx + ".Predecessors") ?? new List<Relation>();
        var succs = Safe(() => t.Successors?.ToList(), ctx + ".Successors") ?? new List<Relation>();
        dump.PredecessorsText = string.Join(";", preds.Select(r => Fmt.PredecessorNotation(r.PredecessorTask?.ID, r.Type, r.Lag)));
        dump.SuccessorsText = string.Join(";", succs.Select(r => Fmt.PredecessorNotation(r.SuccessorTask?.ID, r.Type, r.Lag)));

        dump.Baselines.Add(new BaselineDump
        {
            Index = 0,
            Label = "Baseline (Línea base)",
            Cost = Safe(() => t.BaselineCost, ctx + ".BaselineCost"),
            Start = Safe(() => t.BaselineStart, ctx + ".BaselineStart"),
            Finish = Safe(() => t.BaselineFinish, ctx + ".BaselineFinish"),
            Work = Fmt.ToDump(Safe(() => t.BaselineWork, ctx + ".BaselineWork")),
            Duration = Fmt.ToDump(Safe(() => t.BaselineDuration, ctx + ".BaselineDuration")),
            FixedCost = Safe(() => t.BaselineFixedCost, ctx + ".BaselineFixedCost"),
        });
        for (var i = 1; i <= 10; i++)
        {
            var n = i;
            dump.Baselines.Add(new BaselineDump
            {
                Index = n,
                Label = $"Baseline{n} (Línea base{n})",
                Cost = Safe(() => t.GetBaselineCost(n), $"Task.GetBaselineCost({n})"),
                Start = Safe(() => t.GetBaselineStart(n), $"Task.GetBaselineStart({n})"),
                Finish = Safe(() => t.GetBaselineFinish(n), $"Task.GetBaselineFinish({n})"),
                Work = Fmt.ToDump(Safe(() => t.GetBaselineWork(n), $"Task.GetBaselineWork({n})")),
                Duration = Fmt.ToDump(Safe(() => t.GetBaselineDuration(n), $"Task.GetBaselineDuration({n})")),
                FixedCost = Safe(() => t.GetBaselineFixedCost(n), $"Task.GetBaselineFixedCost({n})"),
            });
        }

        // Cross-check: the explicit Baseline6 getter must match the generic FieldType access.
        var b6ByField = Safe(() => t.Get(TaskField.Baseline6Cost), "Task.Get(TaskField.Baseline6Cost)");
        var b6 = dump.Baselines[6].Cost;
        if (b6ByField is double d && b6.HasValue && d != b6.Value)
        {
            Warn("b6mismatch", $"Diferencia entre Task.GetBaselineCost(6) y Task.Get(TaskField.Baseline6Cost) en {ctx}: {Fmt.Num(b6)} vs {Fmt.Num(d)}");
        }

        return dump;
    }

    // ---------------------------------------------------------------- Visible columns

    private void ReadVisibleColumns()
    {
        var columns = new List<(VisibleColumnDump Dump, IFieldType? Field)>();

        foreach (var sc in StandardColumns())
        {
            columns.Add((new VisibleColumnDump
            {
                DisplayName = sc.DisplayName,
                IsCustomColumn = false,
                MpxjProperty = sc.MpxjProperty,
                FieldType = FieldCatalog.Ident(sc.Field),
                UnderlyingField = sc.Field.FieldName,
                DataType = sc.Field.DataType?.ToString(),
                Unit = sc.Unit,
                Entity = "Task",
                AliasSource = "Standard field",
                Resolved = true
            }, sc.Field));
        }

        foreach (var name in CustomVisibleColumns)
        {
            var (field, sources) = ResolveTaskAlias(name);
            var dump = new VisibleColumnDump
            {
                DisplayName = name,
                IsCustomColumn = true,
                Entity = "Task",
                Resolved = field != null,
                Formula = null,
            };
            if (field != null)
            {
                var cf = Safe(() => _project.CustomFields.Get(field), "CustomFields.Get");
                dump.MpxjProperty = $"Task.Get({FieldCatalog.Ident(field)})";
                dump.FieldType = FieldCatalog.Ident(field);
                dump.UnderlyingField = field.FieldName;
                dump.DataType = field.DataType?.ToString();
                dump.Unit = field.DataType == DataType.Currency
                    ? Safe(() => _project.ProjectProperties.CurrencyCode ?? _project.ProjectProperties.CurrencySymbol, "Currency")
                    : field.DataType?.ToString();
                dump.AliasSource = string.Join(" + ", sources);
                dump.LookupTable = cf?.LookupTable is { Count: > 0 } lt ? $"{lt.Count} valores" : null;
                dump.Observation = "Fórmula: MPXJ.Net no expone fórmulas de campos personalizados; se lee el valor almacenado en el MPP.";
            }
            else
            {
                dump.Observation = "Alias no encontrado ni en CustomFields ni en los títulos de columna de las tablas de vista.";
            }
            columns.Add((dump, field));
        }

        var taskByUid = _tasks.Where(t => t.UniqueID != null).GroupBy(t => t.UniqueID!.Value).ToDictionary(g => g.Key, g => g.First());
        foreach (var (dump, field) in columns)
        {
            if (field != null)
            {
                var key = FieldCatalog.Ident(field);
                dump.FoundInViewTables = _dump.ViewTables
                    .SelectMany(t => t.Columns.Where(c => c.FieldType == key).Select(c => $"{t.Name} ('{c.Title}')"))
                    .ToList();
            }

            foreach (var td in _dump.Tasks)
            {
                if (field == null || td.UniqueId == null || !taskByUid.TryGetValue(td.UniqueId.Value, out var task))
                {
                    td.VisibleColumnValues[dump.DisplayName] = null;
                    continue;
                }

                object? value;
                if (field == TaskField.Baseline6Cost || FieldCatalog.Ident(field) == "TaskField.Baseline6Cost")
                {
                    value = td.Baselines.FirstOrDefault(b => b.Index == 6)?.Cost;
                }
                else if (FieldCatalog.Ident(field) == "TaskField.Predecessors")
                {
                    value = td.PredecessorsText;
                }
                else if (FieldCatalog.Ident(field) == "TaskField.Successors")
                {
                    value = td.SuccessorsText;
                }
                else
                {
                    value = Safe(() => task.Get(field), $"Task.Get({FieldCatalog.Ident(field)})");
                }

                td.VisibleColumnValues[dump.DisplayName] = Fmt.IsEmpty(value) ? null : Fmt.Format(value);
                if (td.IsProjectSummaryTask)
                {
                    dump.ProjectSummaryTaskValue = Fmt.IsEmpty(value) ? null : Fmt.Format(value);
                    dump.ProjectSummaryTaskHasNonZeroValue = !Fmt.IsEmpty(value) && !Fmt.IsZero(value) && value is not false;
                    continue;
                }
                if (!Fmt.IsEmpty(value)) dump.TasksWithValue++;
                if (!Fmt.IsEmpty(value) && !Fmt.IsZero(value) && value is not false) dump.TasksWithNonZeroValue++;
            }

            _dump.VisibleColumns.Add(dump);
        }
    }

    /// <summary>
    /// Finds the underlying task field for a column alias. MS Project stores aliases in the custom field
    /// definitions, but a column can also be renamed only in a view table (Column.Title), so both are checked.
    /// </summary>
    private (IFieldType? Field, List<string> Sources) ResolveTaskAlias(string alias)
    {
        var target = Fmt.Normalize(alias);
        var sources = new List<string>();
        IFieldType? found = null;

        var defs = Safe(() => _project.CustomFields.ToList(), "ProjectFile.CustomFields") ?? new List<CustomField>();
        var byDefinition = defs.FirstOrDefault(cf => cf.FieldType?.FieldTypeClass == FieldTypeClass.Task && Fmt.Normalize(cf.Alias) == target);
        if (byDefinition?.FieldType != null)
        {
            found = byDefinition.FieldType;
            sources.Add("CustomFields alias");
        }

        var byApi = Safe(() => _project.Tasks.GetFieldTypeByAlias(alias), "TaskContainer.GetFieldTypeByAlias");
        if (byApi != null)
        {
            found ??= byApi;
            if (!sources.Contains("CustomFields alias")) sources.Add("TaskContainer.GetFieldTypeByAlias");
        }

        var tables = Safe(() => _project.Tables?.Where(t => !t.ResourceFlag).ToList(), "ProjectFile.Tables") ?? new List<Table>();
        foreach (var table in tables)
        {
            foreach (var col in Safe(() => table.Columns?.ToList(), "Table.Columns") ?? new List<Column>())
            {
                if (col.FieldType == null || Fmt.Normalize(col.Title) != target) continue;
                if (found != null && FieldCatalog.Ident(found) != FieldCatalog.Ident(col.FieldType))
                {
                    Warn("alias-conflict:" + alias, $"El alias '{alias}' apunta a {FieldCatalog.Ident(found)} en la definición pero a {FieldCatalog.Ident(col.FieldType)} en la tabla '{table.Name}'.");
                    continue;
                }
                found ??= col.FieldType;
                var src = $"View table column title ('{table.Name}')";
                if (!sources.Contains(src)) sources.Add(src);
            }
        }

        return (found, sources);
    }

    // ---------------------------------------------------------------- Dependencies

    private void ReadDependencies()
    {
        var seen = new Dictionary<string, DependencyDump>();

        void AddRelation(Relation r, string source)
        {
            var pred = r.PredecessorTask;
            var succ = r.SuccessorTask;
            var type = Safe(() => r.Type, "Relation.Type");
            var lag = Safe(() => r.Lag, "Relation.Lag");
            var key = $"{pred?.UniqueID}|{succ?.UniqueID}|{type}|{lag?.DurationValue}|{lag?.Units}";
            if (seen.TryGetValue(key, out var existing))
            {
                if (!existing.Source.Contains(source)) existing.Source += ", " + source;
                return;
            }

            seen[key] = new DependencyDump
            {
                RelationUniqueId = Safe(() => r.UniqueID, "Relation.UniqueID"),
                PredecessorId = pred?.ID,
                PredecessorUniqueId = pred?.UniqueID,
                PredecessorName = pred?.Name,
                SuccessorId = succ?.ID,
                SuccessorUniqueId = succ?.UniqueID,
                SuccessorName = succ?.Name,
                RelationType = type?.ToString(),
                RelationTypeName = Fmt.RelationTypeName(type),
                Abbreviation = Fmt.RelationAbbreviation(type),
                LagValue = lag?.DurationValue,
                LagUnits = lag?.Units?.ToString(),
                LagText = Fmt.Dur(lag),
                Driving = Safe(() => r.Driving, "Relation.Driving"),
                MsProjectPredecessorNotation = Fmt.PredecessorNotation(pred?.ID, type, lag),
                Source = source
            };
        }

        var relations = Safe(() => _project.Relations?.ToList(), "ProjectFile.Relations") ?? new List<Relation>();
        foreach (var r in relations) AddRelation(r, "ProjectFile.Relations");
        foreach (var t in _tasks)
        {
            foreach (var r in Safe(() => t.Predecessors?.ToList(), "Task.Predecessors") ?? new List<Relation>()) AddRelation(r, "Task.Predecessors");
            foreach (var r in Safe(() => t.Successors?.ToList(), "Task.Successors") ?? new List<Relation>()) AddRelation(r, "Task.Successors");
        }

        _dump.Dependencies = seen.Values
            .OrderBy(d => d.SuccessorId ?? int.MaxValue)
            .ThenBy(d => d.PredecessorId ?? int.MaxValue)
            .ToList();
    }
}
