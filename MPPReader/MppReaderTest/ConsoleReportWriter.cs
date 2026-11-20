using MppReaderTest.Models;

namespace MppReaderTest;

/// <summary>Renders the dump as a readable text report (console + project-dump.txt).</summary>
public static class ConsoleReportWriter
{
    public static void Write(ProjectDump d, TeeWriter o)
    {
        WriteFile(d, o);
        WriteProject(d, o);
        WriteTasks(d, o);
        WriteVisibleColumns(d, o);
        WriteViewTables(d, o);
        WriteCostsAndBaselines(d, o);
        WriteDependencies(d, o);
        WriteResources(d, o);
        WriteRateTables(d, o);
        WriteAssignments(d, o);
        WriteCalendars(d, o);
        WriteCustomFields(d, o);
        WriteDynamic(d, o);
        WriteSummary(d, o);
        WriteReadiness(d, o);
        WriteWarnings(d, o);
    }

    private static ILookup<(string, int?), CustomFieldValueDump> NonEmptyCustomValues(ProjectDump d) =>
        d.CustomFieldValues.Where(v => v.ValueState != CustomFieldValueState.DefinedNoValue)
            .ToLookup(v => (v.EntityType, v.EntityUniqueId));

    private static void WriteFile(ProjectDump d, TeeWriter o)
    {
        o.Header("FILE");
        o.WriteLine($"Source file      : {d.SourceFile}");
        o.WriteLine($"MPXJ.Net version : {d.MpxjNetVersion}");
        o.WriteLine($"Reader detected  : {Fmt.Str(d.ReaderUsed)}");
        o.WriteLine($"Generated (UTC)  : {Fmt.Date(d.GeneratedAtUtc)}");
    }

    private static void WriteProject(ProjectDump d, TeeWriter o)
    {
        o.Header("PROJECT INFORMATION");
        foreach (var p in d.Project.Properties)
        {
            o.WriteLine($"  {p.Name,-48} [{p.MpxjProperty}] = {Fmt.Truncate(p.FormattedValue, 200)}{(p.Unit != null ? $"  ({p.Unit})" : "")}");
        }
        if (d.Project.Currencies.Count > 0)
        {
            o.WriteLine("  Currencies:");
            foreach (var c in d.Project.Currencies)
                o.WriteLine($"    {c.CurrencyId} {c.Name} symbol={c.Symbol} rate={Fmt.Num(c.ExchangeRate)} +fmt={c.PositiveFormat} -fmt={c.NegativeFormat}");
        }
        o.WriteLine($"  Custom document properties ({d.Project.CustomProperties.Count}):");
        foreach (var p in d.Project.CustomProperties) o.WriteLine($"    {p.Name} = {Fmt.Truncate(p.FormattedValue, 200)}");
        o.WriteLine($"  Other populated ProjectField values ({d.Project.OtherPopulatedProjectFields.Count}):");
        foreach (var p in d.Project.OtherPopulatedProjectFields) o.WriteLine($"    {p.MpxjProperty} = {Fmt.Truncate(p.FormattedValue, 200)}");
    }

    private static void WriteTasks(ProjectDump d, TeeWriter o)
    {
        o.Header("TASKS");
        var summary = d.Tasks.FirstOrDefault(t => t.IsProjectSummaryTask);
        o.WriteLine(summary != null
            ? $"Project summary task present: YES (ID={Fmt.Int(summary.Id)}, UID=0, Name='{summary.Name}')"
            : "Project summary task present: NO");
        o.WriteLine($"Total task objects returned by MPXJ (incl. project summary task): {d.Tasks.Count}");

        foreach (var t in d.Tasks)
        {
            var indent = new string(' ', Math.Max(0, t.OutlineLevel ?? 0) * 2);
            var flags = new List<string>();
            if (t.IsProjectSummaryTask) flags.Add("PROJECT-SUMMARY");
            if (t.IsSummary) flags.Add("SUMMARY");
            if (t.IsMilestone) flags.Add("MILESTONE");
            if (t.IsCritical) flags.Add("CRITICAL");
            if (t.IsInactive) flags.Add("INACTIVE");
            if (t.IsManuallyScheduled) flags.Add("MANUAL");
            if (t.IsNull) flags.Add("NULL-TASK");
            o.WriteLine();
            o.WriteLine($"{indent}[{Fmt.Int(t.Id)}] {t.Name}  {(flags.Count > 0 ? "{" + string.Join(",", flags) + "}" : "")}");
            var p = indent + "    ";
            o.WriteLine($"{p}UID={Fmt.Int(t.UniqueId)} GUID={t.Guid?.ToString() ?? Fmt.Null} WBS={Fmt.Str(t.Wbs)} Outline={Fmt.Str(t.OutlineNumber)} Level={Fmt.Int(t.OutlineLevel)}");
            o.WriteLine($"{p}Parent: ID={Fmt.Int(t.ParentId)} UID={Fmt.Int(t.ParentUniqueId)} '{Fmt.Str(t.ParentName)}'  Children(ID)=[{string.Join(",", t.ChildIds)}]");
            o.WriteLine($"{p}Summary={Fmt.Bool(t.IsSummary)} Milestone={Fmt.Bool(t.IsMilestone)} Critical={Fmt.Bool(t.IsCritical)} Active={Fmt.Bool(t.IsActive)} Mode={Fmt.Str(t.TaskMode)} Priority={Fmt.Int(t.Priority)}");
            o.WriteLine($"{p}%Complete={Fmt.Num(t.PercentComplete)} %WorkComplete={Fmt.Num(t.PercentWorkComplete)} %Physical={Fmt.Num(t.PhysicalPercentComplete)}");
            o.WriteLine($"{p}Start={Fmt.Date(t.Start)} Finish={Fmt.Date(t.Finish)} ActualStart={Fmt.Date(t.ActualStart)} ActualFinish={Fmt.Date(t.ActualFinish)}");
            o.WriteLine($"{p}Duration={Fmt.Dur(t.Duration)} (estimated={Fmt.Bool(t.DurationIsEstimated)}) Work={Fmt.Dur(t.Work)} ActualWork={Fmt.Dur(t.ActualWork)} RemainingWork={Fmt.Dur(t.RemainingWork)}");
            o.WriteLine($"{p}Cost={Fmt.Num(t.Cost)} ActualCost={Fmt.Num(t.ActualCost)} RemainingCost={Fmt.Num(t.RemainingCost)} FixedCost={Fmt.Num(t.FixedCost)} FixedCostAccrual={Fmt.Str(t.FixedCostAccrual)} CostVariance={Fmt.Num(t.CostVariance)}");
            o.WriteLine($"{p}Constraint={Fmt.Str(t.ConstraintType)} ConstraintDate={Fmt.Date(t.ConstraintDate)} Deadline={Fmt.Date(t.Deadline)} TaskType={Fmt.Str(t.TaskType)} EffortDriven={Fmt.Bool(t.EffortDriven)}");
            o.WriteLine($"{p}Calendar={Fmt.Str(t.CalendarName)} (UID={Fmt.Int(t.CalendarUniqueId)}) EffectiveCalendar={Fmt.Str(t.EffectiveCalendarName)}");
            o.WriteLine($"{p}ResourceNames={Fmt.Str(t.ResourceNames)}  Predecessors={Fmt.Str(t.PredecessorsText)}  Successors={Fmt.Str(t.SuccessorsText)}");
            if (!string.IsNullOrEmpty(t.Notes)) o.WriteLine($"{p}Notes={Fmt.Truncate(t.Notes, 300)}");
            if (!string.IsNullOrEmpty(t.Hyperlink) || !string.IsNullOrEmpty(t.HyperlinkAddress))
                o.WriteLine($"{p}Hyperlink={Fmt.Str(t.Hyperlink)} Address={Fmt.Str(t.HyperlinkAddress)} SubAddress={Fmt.Str(t.HyperlinkSubAddress)}");
        }
    }

    private static void WriteVisibleColumns(ProjectDump d, TeeWriter o)
    {
        o.Header("VISIBLE COLUMNS (MICROSOFT PROJECT VIEW)");
        foreach (var c in d.VisibleColumns)
        {
            o.WriteLine($"  {c.DisplayName,-26} resolved={Fmt.Bool(c.Resolved)} {(c.IsCustomColumn ? "CUSTOM" : "STANDARD")}");
            o.WriteLine($"      MPXJ property : {Fmt.Str(c.MpxjProperty)}");
            o.WriteLine($"      FieldType     : {Fmt.Str(c.FieldType)}  underlying='{Fmt.Str(c.UnderlyingField)}'  dataType={Fmt.Str(c.DataType)}  unit={Fmt.Str(c.Unit)}  entity={Fmt.Str(c.Entity)}");
            o.WriteLine($"      Alias source  : {Fmt.Str(c.AliasSource)}  formula={Fmt.Str(c.Formula)}  lookup={Fmt.Str(c.LookupTable)}");
            o.WriteLine($"      Tasks with value={c.TasksWithValue}  non-zero={c.TasksWithNonZeroValue}  (excluding project summary task)  project summary task value={Fmt.Str(c.ProjectSummaryTaskValue)}");
            o.WriteLine($"      In view tables: {(c.FoundInViewTables.Count == 0 ? "(none)" : string.Join("; ", c.FoundInViewTables))}");
            if (c.Observation != null) o.WriteLine($"      Note: {c.Observation}");
        }

        o.WriteLine();
        o.WriteLine("  Values per task (ID | Name | column=value ...):");
        foreach (var t in d.Tasks)
        {
            var values = string.Join(" | ", t.VisibleColumnValues.Select(kv => $"{kv.Key}={kv.Value ?? Fmt.Null}"));
            o.WriteLine($"  [{Fmt.Int(t.Id)}] {Fmt.Truncate(t.Name, 60)} | {values}");
        }
    }

    private static void WriteViewTables(ProjectDump d, TeeWriter o)
    {
        o.Header("VIEW TABLES (COLUMN TITLES -> FIELD)");
        foreach (var t in d.ViewTables)
        {
            o.WriteLine($"  Table '{t.Name}' (ID={t.Id}, {(t.IsResourceTable ? "Resource" : "Task")}):");
            foreach (var c in t.Columns) o.WriteLine($"      '{c.Title}' -> {Fmt.Str(c.FieldType)} ({Fmt.Str(c.DataType)})");
        }
    }

    private static void WriteCostsAndBaselines(ProjectDump d, TeeWriter o)
    {
        o.Header("TASK COSTS AND BASELINES");
        o.WriteLine("  Baseline = Task.BaselineCost (Línea base). Baseline6 = Task.GetBaselineCost(6) (Costo de línea base6). Valores sin redondeo.");
        foreach (var t in d.Tasks)
        {
            var indent = new string(' ', Math.Max(0, t.OutlineLevel ?? 0) * 2);
            o.WriteLine();
            o.WriteLine($"{indent}[{Fmt.Int(t.Id)}] {t.Name}{(t.IsSummary ? "  {SUMMARY}" : "")}");
            o.WriteLine($"{indent}    Cost={Fmt.Num(t.Cost)} ActualCost={Fmt.Num(t.ActualCost)} RemainingCost={Fmt.Num(t.RemainingCost)} FixedCost={Fmt.Num(t.FixedCost)} CostVariance={Fmt.Num(t.CostVariance)}");
            var costs = string.Join(" ", t.Baselines.Select(b => $"{(b.Index == 0 ? "Baseline" : "Baseline" + b.Index)}Cost={Fmt.Num(b.Cost)}"));
            o.WriteLine($"{indent}    {costs}");
            var b0 = t.Baselines.FirstOrDefault(b => b.Index == 0);
            var b6 = t.Baselines.FirstOrDefault(b => b.Index == 6);
            o.WriteLine($"{indent}    BaselineStart={Fmt.Date(b0?.Start)} BaselineFinish={Fmt.Date(b0?.Finish)} BaselineWork={Fmt.Dur(b0?.Work)} BaselineDuration={Fmt.Dur(b0?.Duration)}");
            o.WriteLine($"{indent}    Baseline6Start={Fmt.Date(b6?.Start)} Baseline6Finish={Fmt.Date(b6?.Finish)} Baseline6Work={Fmt.Dur(b6?.Work)} Baseline6Duration={Fmt.Dur(b6?.Duration)}");
        }
    }

    private static void WriteDependencies(ProjectDump d, TeeWriter o)
    {
        o.Header("DEPENDENCIES");
        o.WriteLine($"  Unique dependencies: {d.Dependencies.Count}");
        foreach (var r in d.Dependencies)
        {
            o.WriteLine($"  {r.MsProjectPredecessorNotation,-14} Pred[ID={Fmt.Int(r.PredecessorId)} UID={Fmt.Int(r.PredecessorUniqueId)}] '{Fmt.Truncate(r.PredecessorName, 50)}' -> Succ[ID={Fmt.Int(r.SuccessorId)} UID={Fmt.Int(r.SuccessorUniqueId)}] '{Fmt.Truncate(r.SuccessorName, 50)}'");
            o.WriteLine($"                 Type={r.RelationTypeName} ({r.Abbreviation}) Lag={r.LagText} (value={Fmt.Num(r.LagValue)}, units={Fmt.Str(r.LagUnits)}) Driving={Fmt.Bool(r.Driving)} RelationUID={Fmt.Int(r.RelationUniqueId)} Source={r.Source}");
        }
    }

    private static void WriteResources(ProjectDump d, TeeWriter o)
    {
        o.Header("RESOURCES");
        var values = NonEmptyCustomValues(d);
        foreach (var r in d.Resources)
        {
            o.WriteLine();
            o.WriteLine($"  [{Fmt.Int(r.Id)}] {Fmt.Str(r.Name)}  Type={Fmt.Str(r.Type)}{(r.IsNull ? " {NULL}" : "")}{(r.IsGeneric ? " {GENERIC}" : "")}{(r.IsActive ? "" : " {INACTIVE}")}");
            o.WriteLine($"      UID={Fmt.Int(r.UniqueId)} GUID={r.Guid?.ToString() ?? Fmt.Null} Initials={Fmt.Str(r.Initials)} Code={Fmt.Str(r.Code)} Group={Fmt.Str(r.Group)} Email={Fmt.Str(r.EmailAddress)} MaterialLabel={Fmt.Str(r.MaterialLabel)}");
            o.WriteLine($"      Calendar={Fmt.Str(r.CalendarName)} (UID={Fmt.Int(r.CalendarUniqueId)}) BaseCalendar={Fmt.Str(r.BaseCalendar)} MaxUnits={Fmt.Num(r.MaxUnits)} AvailableFrom={Fmt.Date(r.AvailableFrom)} AvailableTo={Fmt.Date(r.AvailableTo)}");
            if (r.Availability.Count > 0)
                o.WriteLine($"      Availability: {string.Join("; ", r.Availability.Select(a => $"{Fmt.Date(a.Start)}..{Fmt.Date(a.End)} units={Fmt.Num(a.Units)}"))}");
            o.WriteLine($"      Work={Fmt.Dur(r.Work)} ActualWork={Fmt.Dur(r.ActualWork)} RemainingWork={Fmt.Dur(r.RemainingWork)}");
            o.WriteLine($"      Cost={Fmt.Num(r.Cost)} ActualCost={Fmt.Num(r.ActualCost)} RemainingCost={Fmt.Num(r.RemainingCost)}");
            o.WriteLine($"      StandardRate={Fmt.RateText(r.StandardRate)} OvertimeRate={Fmt.RateText(r.OvertimeRate)} CostPerUse={Fmt.Num(r.CostPerUse)} AccrueAt={Fmt.Str(r.AccrueAt)} Assignments={r.AssignmentCount}");
            if (!string.IsNullOrEmpty(r.Notes)) o.WriteLine($"      Notes={Fmt.Truncate(r.Notes, 300)}");
            var custom = values[("Resource", r.UniqueId)].ToList();
            if (custom.Count > 0)
                o.WriteLine($"      Custom fields: {string.Join("; ", custom.Select(v => $"{v.Alias ?? v.InternalName}={v.FormattedValue}"))}");
        }
    }

    private static void WriteRateTables(ProjectDump d, TeeWriter o)
    {
        o.Header("RESOURCE RATE TABLES");
        o.WriteLine("  Entries per resource and table (A..E). 'open' = sin límite de fecha.");
        foreach (var g in d.ResourceRateTables.GroupBy(e => (e.ResourceUniqueId, e.ResourceName)))
        {
            o.WriteLine($"  Resource UID={Fmt.Int(g.Key.ResourceUniqueId)} '{Fmt.Str(g.Key.ResourceName)}'");
            foreach (var e in g)
            {
                var from = e.EffectiveFromIsOpen ? "open" : Fmt.Date(e.EffectiveFrom);
                var to = e.EffectiveToIsOpen ? "open" : Fmt.Date(e.EffectiveTo);
                o.WriteLine($"      Table {e.RateTable} #{e.EntryIndex} [{from} .. {to}] Standard={Fmt.RateText(e.StandardRate)} (units={Fmt.Str(e.StandardRate?.Units)}) Overtime={Fmt.RateText(e.OvertimeRate)} (units={Fmt.Str(e.OvertimeRate?.Units)}) CostPerUse={Fmt.Num(e.CostPerUse)} populated={Fmt.Bool(e.TableIsPopulated)}");
            }
        }
    }

    private static void WriteAssignments(ProjectDump d, TeeWriter o)
    {
        o.Header("RESOURCE ASSIGNMENTS");
        o.WriteLine("  CostValidation es SOLO una comprobación; el coste válido es siempre el leído del MPP.");
        var values = NonEmptyCustomValues(d);
        foreach (var a in d.ResourceAssignments)
        {
            o.WriteLine();
            o.WriteLine($"  Assignment UID={Fmt.Int(a.UniqueId)} GUID={a.Guid?.ToString() ?? Fmt.Null}");
            o.WriteLine($"      Task[ID={Fmt.Int(a.TaskId)} UID={Fmt.Int(a.TaskUniqueId)}] '{Fmt.Str(a.TaskName)}'{(a.TaskMissing ? " {TASK MISSING}" : "")}");
            o.WriteLine($"      Resource[ID={Fmt.Int(a.ResourceId)} UID={Fmt.Int(a.ResourceUniqueId)} GUID={a.ResourceGuid?.ToString() ?? Fmt.Null}] '{Fmt.Str(a.ResourceName)}' type={Fmt.Str(a.ResourceType)}{(a.ResourceMissing ? " {RESOURCE MISSING / UNASSIGNED}" : "")}");
            o.WriteLine($"      Start={Fmt.Date(a.Start)} Finish={Fmt.Date(a.Finish)} ActualStart={Fmt.Date(a.ActualStart)} ActualFinish={Fmt.Date(a.ActualFinish)}");
            o.WriteLine($"      Units={Fmt.Num(a.Units)} Work={Fmt.Dur(a.Work)} ActualWork={Fmt.Dur(a.ActualWork)} RemainingWork={Fmt.Dur(a.RemainingWork)} OvertimeWork={Fmt.Dur(a.OvertimeWork)} %WorkComplete={Fmt.Num(a.PercentWorkComplete)}");
            o.WriteLine($"      Cost={Fmt.Num(a.Cost)} ActualCost={Fmt.Num(a.ActualCost)} RemainingCost={Fmt.Num(a.RemainingCost)} OvertimeCost={Fmt.Num(a.OvertimeCost)} BaselineCost={Fmt.Num(a.BaselineCost)} Baseline6Cost={Fmt.Num(a.Baseline6Cost)}");
            o.WriteLine($"      CostRateTable={a.CostRateTable} RateSource={Fmt.Str(a.RateSource)} OverrideRate={Fmt.RateText(a.OverrideRate)} Calendar={Fmt.Str(a.EffectiveCalendarName)} Contour={Fmt.Str(a.WorkContour)}");
            if (a.CostValidation is { } v)
            {
                o.WriteLine($"      CostValidation: read={Fmt.Num(v.CostReadFromMpp)} calculated={Fmt.Num(v.CalculatedCostForValidation)} difference={Fmt.Num(v.Difference)}");
                o.WriteLine($"                      {v.Formula}");
            }
            var tp = a.Timephased;
            o.WriteLine($"      Timephased items: plannedWork={tp.PlannedWork.Count} actualRegularWork={tp.ActualRegularWork.Count} remainingRegularWork={tp.RemainingRegularWork.Count} actualCost={tp.ActualCost.Count}{(tp.Error != null ? " error=" + tp.Error : "")} (detalle en JSON)");
            var custom = values[("Assignment", a.UniqueId)].ToList();
            if (custom.Count > 0)
                o.WriteLine($"      Custom fields: {string.Join("; ", custom.Select(c => $"{c.Alias ?? c.InternalName}={c.FormattedValue}"))}");
        }
    }

    private static void WriteCalendars(ProjectDump d, TeeWriter o)
    {
        o.Header("CALENDARS");
        foreach (var c in d.Calendars)
        {
            o.WriteLine();
            o.WriteLine($"  Calendar UID={Fmt.Int(c.UniqueId)} '{Fmt.Str(c.Name)}' {(c.IsBaseCalendar ? "{BASE}" : "{DERIVED}")}{(c.IsProjectDefault ? " {PROJECT DEFAULT}" : "")} type={Fmt.Str(c.CalendarType)} personal={Fmt.Bool(c.IsPersonal)}");
            o.WriteLine($"      Parent: UID={Fmt.Int(c.ParentUniqueId)} '{Fmt.Str(c.ParentName)}'  MinutesPerDay={Fmt.Int(c.MinutesPerDay)} MinutesPerWeek={Fmt.Int(c.MinutesPerWeek)} DaysPerMonth={Fmt.Int(c.DaysPerMonth)}");
            o.WriteLine($"      Tasks ({c.TaskUniqueIds.Count}): {string.Join(", ", c.TaskUniqueIds.Take(30))}{(c.TaskUniqueIds.Count > 30 ? ", ..." : "")}");
            o.WriteLine($"      Resources ({c.ResourceUniqueIds.Count}): {string.Join(", ", c.ResourceNames.Take(30))}{(c.ResourceNames.Count > 30 ? ", ..." : "")}");
            if (c.DerivedCalendarNames.Count > 0) o.WriteLine($"      Derived calendars ({c.DerivedCalendarNames.Count}): {string.Join(", ", c.DerivedCalendarNames.Take(30))}{(c.DerivedCalendarNames.Count > 30 ? ", ..." : "")}");
            foreach (var day in c.Days)
            {
                o.WriteLine($"      {day.Day}");
                o.WriteLine($"        Defined: {Fmt.Str(day.DefinedDayType)}{(day.DefinedPeriods.Count > 0 ? " " + string.Join(" ", day.DefinedPeriods) : "")}");
                o.WriteLine($"        Working: {(day.EffectiveWorking.HasValue ? Fmt.Bool(day.EffectiveWorking.Value) : Fmt.Null)}");
                foreach (var p in day.EffectivePeriods) o.WriteLine($"          {p}");
            }
            foreach (var w in c.WorkWeeks)
            {
                o.WriteLine($"      Work week '{Fmt.Str(w.Name)}' {Fmt.Date(w.From)} .. {Fmt.Date(w.To)}");
                foreach (var day in w.Days)
                    o.WriteLine($"        {day.Day,-9} {Fmt.Str(day.DefinedDayType)} {string.Join(" ", day.DefinedPeriods)}");
            }
            var exceptions = d.CalendarExceptions.Where(e => e.CalendarUniqueId == c.UniqueId).ToList();
            o.WriteLine($"      Exceptions ({exceptions.Count}):");
            foreach (var e in exceptions)
            {
                o.WriteLine($"        '{Fmt.Str(e.Name)}' {Fmt.Date(e.FromDate)} .. {Fmt.Date(e.ToDate)} {(e.Working ? "Working" : "Non-working")} {string.Join(" ", e.Periods)}");
                if (e.IsRecurring)
                {
                    o.WriteLine($"          Recurring: type={Fmt.Str(e.RecurrenceType)} frequency={Fmt.Int(e.Frequency)} occurrences={Fmt.Int(e.Occurrences)} useEndDate={Fmt.Bool(e.UseEndDate)} start={Fmt.Date(e.RecurrenceStart)} finish={Fmt.Date(e.RecurrenceFinish)} relative={Fmt.Bool(e.Relative)} dayOfWeek={Fmt.Str(e.DayOfWeek)} dayNumber={Fmt.Int(e.DayNumber)} month={Fmt.Int(e.MonthNumber)} weeklyDays=[{string.Join(",", e.WeeklyDays)}] expanded={e.ExpandedOccurrenceCount}");
                    o.WriteLine($"          {Fmt.Truncate(e.RecurrenceDescription, 300)}");
                }
            }
        }
    }

    private static void WriteCustomFields(ProjectDump d, TeeWriter o)
    {
        o.Header("CUSTOM FIELDS AND EXTENDED ATTRIBUTES");
        o.WriteLine("  Catálogo de familias (FieldTypes conocidos por MPXJ.Net):");
        foreach (var g in d.CustomFieldCatalog.GroupBy(c => (c.EntityType, c.Family)))
        {
            var used = g.Where(c => c.Status != CustomFieldCatalogStatus.NotDefined).ToList();
            o.WriteLine($"    {g.Key.EntityType,-10} {g.Key.Family,-30} known={g.Count(),3} used={used.Count,3}{(used.Count > 0 ? "  -> " + string.Join(", ", used.Select(u => $"{u.InternalName}{(u.Alias != null ? $"='{u.Alias}'" : "")} [{u.Status}]")) : "")}");
        }

        o.WriteLine();
        o.WriteLine($"  Definiciones encontradas ({d.CustomFieldDefinitions.Count}):");
        foreach (var c in d.CustomFieldDefinitions)
        {
            o.WriteLine($"    {c.FieldType}  entity={c.EntityType} internal='{c.InternalName}' alias='{Fmt.Str(c.Alias)}' dataType={Fmt.Str(c.DataType)}");
            o.WriteLine($"        aliasSources=[{string.Join(", ", c.AliasSources)}] views=[{string.Join("; ", c.ViewTableTitles)}]");
            o.WriteLine($"        definition={Fmt.Bool(c.HasCustomFieldDefinition)} uid={Fmt.Int(c.CustomFieldUniqueId)} populated={Fmt.Bool(c.IsPopulated)} enterprise={Fmt.Bool(c.IsEnterpriseCustomField)} formula={Fmt.Str(c.Formula)} (expuesta por MPXJ: {Fmt.Bool(c.FormulaExposedByMpxj)}) calculated={(c.IsCalculated?.ToString() ?? "unknown")} graphicalIndicator={Fmt.Bool(c.HasGraphicalIndicator)}");
            o.WriteLine($"        lookupTable={(c.HasLookupTable ? $"{c.LookupTable.Count} values GUID={c.LookupTableGuid}" : "none")} masks=[{string.Join(", ", c.Masks)}] entitiesWithValue={c.EntitiesWithValue} nonZero={c.EntitiesWithNonZeroValue}");
            foreach (var l in c.LookupTable.Take(50)) o.WriteLine($"          lookup UID={Fmt.Int(l.UniqueId)} parent={Fmt.Int(l.ParentUniqueId)} value='{l.Value}' description='{l.Description}'");
        }

        o.WriteLine();
        o.WriteLine("  Valores no vacíos de campos personalizados (todos los estados en custom-fields.csv / JSON):");
        foreach (var v in d.CustomFieldValues.Where(v => v.ValueState == CustomFieldValueState.DefinedWithValue))
        {
            o.WriteLine($"    {v.EntityType}[ID={Fmt.Int(v.EntityId)} UID={Fmt.Int(v.EntityUniqueId)}] {Fmt.Truncate(v.EntityName, 50)} :: {v.Alias ?? v.InternalName} ({v.InternalName}) = {v.FormattedValue} [{v.DotNetType}]");
        }
    }

    private static void WriteDynamic(ProjectDump d, TeeWriter o)
    {
        o.Header("DYNAMIC FIELD DUMP (DIAGNOSTIC)");
        var i = d.DynamicFields;
        o.WriteLine($"  Enabled={Fmt.Bool(i.Enabled)}  {i.Note}");
        o.WriteLine($"  FieldTypes tested={i.FieldTypesTested} reads={i.ReadsAttempted} non-null values={i.NonNullValues} read errors={i.ReadErrors} skipped FieldTypes={i.SkippedFieldTypes.Count}");
        foreach (var kv in i.NonNullValuesByEntity) o.WriteLine($"    {kv.Key}: {kv.Value} values");
        if (i.FieldTypesWithErrors.Count > 0) o.WriteLine($"  FieldTypes with errors: {string.Join(", ", i.FieldTypesWithErrors)}");
        o.WriteLine($"  FieldTypes with at least one non-null value: {i.FieldStatistics.Count}");
        o.WriteLine("  Entity     FieldType                                          DataType         .NET       nonNull nonDefault sample");
        foreach (var s in i.FieldStatistics)
        {
            o.WriteLine($"  {s.EntityType,-10} {s.FieldType,-50} {Fmt.Str(s.DataType),-16} {Fmt.Str(s.DotNetType),-10} {s.NonNullCount,7} {s.NonDefaultCount,10} {Fmt.Truncate(s.SampleValue, 60)}");
        }
        o.WriteLine($"  Valores individuales: dynamic-fields.csv{(i.ValuesIncludedInJson ? " y project-dump.json (DynamicFieldValues)" : " (añadir --dynamic-json para incluirlos también en project-dump.json)")}.");
    }

    private static void WriteSummary(ProjectDump d, TeeWriter o)
    {
        var s = d.Summary;
        o.Header("SUMMARY");
        o.WriteLine($"  Tasks (excl. project summary task) : {s.Tasks}");
        o.WriteLine($"  Project summary task present       : {(s.ProjectSummaryTaskPresent ? "YES" : "NO")}");
        o.WriteLine($"  Summary Tasks                      : {s.SummaryTasks}");
        o.WriteLine($"  Milestones                         : {s.Milestones}");
        o.WriteLine($"  Inactive Tasks                     : {s.InactiveTasks}");
        o.WriteLine($"  Dependencies (unique)              : {s.Dependencies}");
        o.WriteLine($"  Resources                          : {s.Resources}");
        o.WriteLine($"  Work Resources                     : {s.WorkResources}");
        o.WriteLine($"  Material Resources                 : {s.MaterialResources}");
        o.WriteLine($"  Cost Resources                     : {s.CostResources}");
        o.WriteLine($"  Resource Assignments               : {s.ResourceAssignments}");
        o.WriteLine($"  Resource Rate Entries              : {s.ResourceRateEntries}");
        o.WriteLine($"  Calendars                          : {s.Calendars}");
        o.WriteLine($"  Calendar Exceptions                : {s.CalendarExceptions}");
        o.WriteLine($"  Custom Field Definitions           : {s.CustomFieldDefinitions}");
        o.WriteLine($"  Custom Field Values (all states)   : {s.CustomFieldValues}");
        o.WriteLine($"  Custom Field Values (with value)   : {s.CustomFieldValuesNonEmpty}");
        o.WriteLine($"  Dynamic Field Values (non-null)    : {s.DynamicFieldValues}");
        o.WriteLine($"  Tasks With Cost                    : {s.TasksWithCost}");
        o.WriteLine($"  Tasks With Actual Cost             : {s.TasksWithActualCost}");
        o.WriteLine($"  Tasks With Baseline6 Cost          : {s.TasksWithBaseline6Cost}");
        o.WriteLine("  --- Totales de comprobación (NO son totales económicos definitivos) ---");
        o.WriteLine($"  Total Task Cost           all tasks (incl. summaries) = {Fmt.Num(s.TotalTaskCostAllTasks)} | non-summary only = {Fmt.Num(s.TotalTaskCostNonSummaryTasks)}");
        o.WriteLine($"  Total Task Actual Cost    all tasks (incl. summaries) = {Fmt.Num(s.TotalTaskActualCostAllTasks)} | non-summary only = {Fmt.Num(s.TotalTaskActualCostNonSummaryTasks)}");
        o.WriteLine($"  Total Task Remaining Cost all tasks (incl. summaries) = {Fmt.Num(s.TotalTaskRemainingCostAllTasks)} | non-summary only = {Fmt.Num(s.TotalTaskRemainingCostNonSummaryTasks)}");
        o.WriteLine($"  Total Task Baseline6 Cost all tasks (incl. summaries) = {Fmt.Num(s.TotalTaskBaseline6CostAllTasks)} | non-summary only = {Fmt.Num(s.TotalTaskBaseline6CostNonSummaryTasks)}");
        o.WriteLine($"  Project summary task (UID 0): Cost = {Fmt.Num(s.ProjectSummaryTaskCost)} | Baseline6 Cost = {Fmt.Num(s.ProjectSummaryTaskBaseline6Cost)}");
        o.WriteLine($"  Nota: {s.TotalsNote}");
        o.WriteLine($"  Warnings                           : {s.Warnings}");
        o.WriteLine($"  Errors                             : {s.Errors}");
    }

    private static void WriteReadiness(ProjectDump d, TeeWriter o)
    {
        o.Header("MIGRATION READINESS CHECK");
        foreach (var r in d.MigrationReadiness)
        {
            o.WriteLine($"  {r.Item,-30} {r.Status,-8} count={r.Count,-6} reason={r.Reason}");
            o.WriteLine($"      {r.Observation}");
        }
    }

    private static void WriteWarnings(ProjectDump d, TeeWriter o)
    {
        o.Header("WARNINGS");
        if (d.Warnings.Count == 0) o.WriteLine("  (none)");
        foreach (var w in d.Warnings) o.WriteLine($"  - {w}");
        o.Header("ERRORS");
        if (d.Errors.Count == 0) o.WriteLine("  (none)");
        foreach (var e in d.Errors) o.WriteLine($"  - {e}");
    }
}
