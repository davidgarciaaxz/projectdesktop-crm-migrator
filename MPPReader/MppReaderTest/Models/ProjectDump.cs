namespace MppReaderTest.Models;

/// <summary>
/// Root of the diagnostic dump. These are plain output models for analysis only;
/// they are not the final Project Operations migration model.
/// </summary>
public sealed class ProjectDump
{
    public string SourceFile { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; }
    public string MpxjNetVersion { get; set; } = "";
    public string? ReaderUsed { get; set; }
    public ProjectInfoDump Project { get; set; } = new();
    public List<TaskDump> Tasks { get; set; } = new();
    public List<VisibleColumnDump> VisibleColumns { get; set; } = new();
    public List<ViewTableDump> ViewTables { get; set; } = new();
    public List<DependencyDump> Dependencies { get; set; } = new();
    public List<ResourceDump> Resources { get; set; } = new();
    public List<RateTableEntryDump> ResourceRateTables { get; set; } = new();
    public List<AssignmentDump> ResourceAssignments { get; set; } = new();
    public List<CalendarDump> Calendars { get; set; } = new();
    public List<CalendarExceptionDump> CalendarExceptions { get; set; } = new();
    public List<CustomFieldCatalogEntry> CustomFieldCatalog { get; set; } = new();
    public List<CustomFieldDefinitionDump> CustomFieldDefinitions { get; set; } = new();
    public List<CustomFieldValueDump> CustomFieldValues { get; set; } = new();
    public DynamicFieldDumpInfo DynamicFields { get; set; } = new();
    public List<DynamicFieldValueDump> DynamicFieldValues { get; set; } = new();
    public SummaryDump Summary { get; set; } = new();
    public List<ReadinessCheckDump> MigrationReadiness { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class PropertyEntry
{
    public string Name { get; set; } = "";
    public string MpxjProperty { get; set; } = "";
    public object? Value { get; set; }
    public string? FormattedValue { get; set; }
    public string? Unit { get; set; }
}

public sealed class ProjectInfoDump
{
    public List<PropertyEntry> Properties { get; set; } = new();
    public List<PropertyEntry> CustomProperties { get; set; } = new();
    public List<PropertyEntry> OtherPopulatedProjectFields { get; set; } = new();
    public List<CurrencyDump> Currencies { get; set; } = new();
}

public sealed class CurrencyDump
{
    public int? UniqueId { get; set; }
    public string? CurrencyId { get; set; }
    public string? Name { get; set; }
    public string? Symbol { get; set; }
    public double? ExchangeRate { get; set; }
    public string? PositiveFormat { get; set; }
    public string? NegativeFormat { get; set; }
}

public sealed class DurationDump
{
    public double Value { get; set; }
    public string? Units { get; set; }
    public string Text { get; set; } = "";
}

public sealed class RateDump
{
    public double Amount { get; set; }
    public string? Units { get; set; }
    public string Text { get; set; } = "";
}

public sealed class VisibleColumnDump
{
    public string DisplayName { get; set; } = "";
    public bool IsCustomColumn { get; set; }
    public string? MpxjProperty { get; set; }
    public string? FieldType { get; set; }
    public string? UnderlyingField { get; set; }
    public string? DataType { get; set; }
    public string? Unit { get; set; }
    public string? Entity { get; set; }
    public string? AliasSource { get; set; }
    public string? Formula { get; set; }
    public string? LookupTable { get; set; }
    public bool Resolved { get; set; }
    public int TasksWithValue { get; set; }
    public int TasksWithNonZeroValue { get; set; }
    public string? ProjectSummaryTaskValue { get; set; }
    public bool ProjectSummaryTaskHasNonZeroValue { get; set; }
    public List<string> FoundInViewTables { get; set; } = new();
    public string? Observation { get; set; }
}

public sealed class ViewTableDump
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool IsResourceTable { get; set; }
    public List<ViewColumnDump> Columns { get; set; } = new();
}

public sealed class ViewColumnDump
{
    public string? Title { get; set; }
    public string? FieldType { get; set; }
    public string? FieldName { get; set; }
    public string? DataType { get; set; }
}

public sealed class SummaryDump
{
    public int Tasks { get; set; }
    public bool ProjectSummaryTaskPresent { get; set; }
    public int SummaryTasks { get; set; }
    public int Milestones { get; set; }
    public int InactiveTasks { get; set; }
    public int Dependencies { get; set; }
    public int Resources { get; set; }
    public int WorkResources { get; set; }
    public int MaterialResources { get; set; }
    public int CostResources { get; set; }
    public int ResourceAssignments { get; set; }
    public int ResourceRateEntries { get; set; }
    public int Calendars { get; set; }
    public int CalendarExceptions { get; set; }
    public int CustomFieldDefinitions { get; set; }
    public int CustomFieldValues { get; set; }
    public int CustomFieldValuesNonEmpty { get; set; }
    public int DynamicFieldValues { get; set; }
    public int TasksWithCost { get; set; }
    public int TasksWithActualCost { get; set; }
    public int TasksWithBaseline6Cost { get; set; }
    public double TotalTaskCostAllTasks { get; set; }
    public double TotalTaskCostNonSummaryTasks { get; set; }
    public double TotalTaskActualCostAllTasks { get; set; }
    public double TotalTaskActualCostNonSummaryTasks { get; set; }
    public double TotalTaskRemainingCostAllTasks { get; set; }
    public double TotalTaskRemainingCostNonSummaryTasks { get; set; }
    public double TotalTaskBaseline6CostAllTasks { get; set; }
    public double TotalTaskBaseline6CostNonSummaryTasks { get; set; }
    public double? ProjectSummaryTaskCost { get; set; }
    public double? ProjectSummaryTaskBaseline6Cost { get; set; }
    public string TotalsNote { get; set; } = "";
    public int Warnings { get; set; }
    public int Errors { get; set; }
}

public enum ReadinessStatus
{
    YES,
    NO,
    PARTIAL
}

public enum ReadinessReason
{
    ValuesRetrieved,
    DefinitionFoundWithoutValues,
    DefinitionNotFound,
    ApiNotAvailable,
    ReadError,
    NotPresentInFile
}

public sealed class ReadinessCheckDump
{
    public string Item { get; set; } = "";
    public ReadinessStatus Status { get; set; }
    public ReadinessReason Reason { get; set; }
    public int Count { get; set; }
    public string Observation { get; set; } = "";
}
