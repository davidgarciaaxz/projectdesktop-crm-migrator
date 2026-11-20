namespace MppReaderTest.Models;

public sealed class TaskDump
{
    public int? Id { get; set; }
    public int? UniqueId { get; set; }
    public Guid? Guid { get; set; }
    public string? Name { get; set; }
    public string? Wbs { get; set; }
    public string? OutlineNumber { get; set; }
    public int? OutlineLevel { get; set; }
    public int? ParentId { get; set; }
    public int? ParentUniqueId { get; set; }
    public string? ParentName { get; set; }
    public List<int?> ChildIds { get; set; } = new();
    public List<int?> ChildUniqueIds { get; set; } = new();
    public bool IsProjectSummaryTask { get; set; }
    public bool IsSummary { get; set; }
    public bool IsMilestone { get; set; }
    public bool IsCritical { get; set; }
    public bool IsActive { get; set; }
    public bool IsInactive { get; set; }
    public bool IsManuallyScheduled { get; set; }
    public string? TaskMode { get; set; }
    public bool IsNull { get; set; }
    public bool IsExternalTask { get; set; }
    public int? Priority { get; set; }
    public double? PercentComplete { get; set; }
    public double? PercentWorkComplete { get; set; }
    public double? PhysicalPercentComplete { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? Finish { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? ActualFinish { get; set; }
    public DurationDump? Duration { get; set; }
    public bool DurationIsEstimated { get; set; }
    public DurationDump? Work { get; set; }
    public DurationDump? ActualWork { get; set; }
    public DurationDump? RemainingWork { get; set; }
    public double? Cost { get; set; }
    public double? ActualCost { get; set; }
    public double? RemainingCost { get; set; }
    public double? FixedCost { get; set; }
    public string? FixedCostAccrual { get; set; }
    public double? CostVariance { get; set; }
    public string? ConstraintType { get; set; }
    public DateTime? ConstraintDate { get; set; }
    public DateTime? Deadline { get; set; }
    public string? TaskType { get; set; }
    public bool EffortDriven { get; set; }
    public int? CalendarUniqueId { get; set; }
    public string? CalendarName { get; set; }
    public string? EffectiveCalendarName { get; set; }
    public string? ResourceNames { get; set; }
    public List<string> AssignedResourceNames { get; set; } = new();
    public string? PredecessorsText { get; set; }
    public string? SuccessorsText { get; set; }
    public string? Notes { get; set; }
    public string? Hyperlink { get; set; }
    public string? HyperlinkAddress { get; set; }
    public string? HyperlinkSubAddress { get; set; }
    public string? HyperlinkScreenTip { get; set; }
    public List<BaselineDump> Baselines { get; set; } = new();
    /// <summary>Values of the MS Project visible columns requested for the migration analysis (display name -> formatted value).</summary>
    public Dictionary<string, string?> VisibleColumnValues { get; set; } = new();
}

public sealed class BaselineDump
{
    /// <summary>0 = "Baseline" (Línea base), 1..10 = "Baseline1".."Baseline10".</summary>
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public double? Cost { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? Finish { get; set; }
    public DurationDump? Work { get; set; }
    public DurationDump? Duration { get; set; }
    public double? FixedCost { get; set; }
}

public sealed class DependencyDump
{
    public int? RelationUniqueId { get; set; }
    public int? PredecessorId { get; set; }
    public int? PredecessorUniqueId { get; set; }
    public string? PredecessorName { get; set; }
    public int? SuccessorId { get; set; }
    public int? SuccessorUniqueId { get; set; }
    public string? SuccessorName { get; set; }
    public string? RelationType { get; set; }
    public string? RelationTypeName { get; set; }
    public string? Abbreviation { get; set; }
    public double? LagValue { get; set; }
    public string? LagUnits { get; set; }
    public string? LagText { get; set; }
    public bool Driving { get; set; }
    public string? MsProjectPredecessorNotation { get; set; }
    public string Source { get; set; } = "";
}
