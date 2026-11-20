namespace MppReaderTest.Models;

public sealed class AssignmentDump
{
    public int? UniqueId { get; set; }
    public Guid? Guid { get; set; }
    public int? TaskId { get; set; }
    public int? TaskUniqueId { get; set; }
    public string? TaskName { get; set; }
    public bool TaskMissing { get; set; }
    public int? ResourceId { get; set; }
    public int? ResourceUniqueId { get; set; }
    public string? ResourceName { get; set; }
    public Guid? ResourceGuid { get; set; }
    public string? ResourceType { get; set; }
    public bool ResourceMissing { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? Finish { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? ActualFinish { get; set; }
    public double? Units { get; set; }
    public double? RemainingUnits { get; set; }
    public DurationDump? Work { get; set; }
    public DurationDump? ActualWork { get; set; }
    public DurationDump? RemainingWork { get; set; }
    public DurationDump? OvertimeWork { get; set; }
    public double? Cost { get; set; }
    public double? ActualCost { get; set; }
    public double? RemainingCost { get; set; }
    public double? OvertimeCost { get; set; }
    public double? BaselineCost { get; set; }
    public double? Baseline6Cost { get; set; }
    public int CostRateTableIndex { get; set; }
    public string CostRateTable { get; set; } = "";
    public string? RateSource { get; set; }
    public RateDump? OverrideRate { get; set; }
    public double? PercentWorkComplete { get; set; }
    public string? EffectiveCalendarName { get; set; }
    public string? WorkContour { get; set; }
    public CostValidationDump? CostValidation { get; set; }
    public TimephasedInfoDump Timephased { get; set; } = new();
}

public sealed class CostValidationDump
{
    public double? CostReadFromMpp { get; set; }
    public double? CalculatedCostForValidation { get; set; }
    public double? Difference { get; set; }
    public DateTime? RateLookupDate { get; set; }
    public RateDump? StandardRateUsed { get; set; }
    public RateDump? OvertimeRateUsed { get; set; }
    public double? CostPerUseUsed { get; set; }
    public double? RegularQuantity { get; set; }
    public double? OvertimeQuantity { get; set; }
    public string Formula { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class TimephasedInfoDump
{
    public string? Error { get; set; }
    public List<TimephasedItemDump> PlannedWork { get; set; } = new();
    public List<TimephasedItemDump> ActualRegularWork { get; set; } = new();
    public List<TimephasedItemDump> RemainingRegularWork { get; set; } = new();
    public List<TimephasedItemDump> ActualCost { get; set; } = new();
}

public sealed class TimephasedItemDump
{
    public DateTime? Start { get; set; }
    public DateTime? Finish { get; set; }
    public string? TotalAmount { get; set; }
    public string? AmountPerHour { get; set; }
}
