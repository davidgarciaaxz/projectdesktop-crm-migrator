namespace MppReaderTest.Models;

public sealed class ResourceDump
{
    public int? Id { get; set; }
    public int? UniqueId { get; set; }
    public Guid? Guid { get; set; }
    public string? Name { get; set; }
    public string? Initials { get; set; }
    public string? Code { get; set; }
    public string? Group { get; set; }
    public string? EmailAddress { get; set; }
    public string? Type { get; set; }
    public string? MaterialLabel { get; set; }
    public bool IsNull { get; set; }
    public bool IsGeneric { get; set; }
    public bool IsActive { get; set; }
    public bool IsEnterprise { get; set; }
    public int? CalendarUniqueId { get; set; }
    public string? CalendarName { get; set; }
    public string? BaseCalendar { get; set; }
    public double? MaxUnits { get; set; }
    public DateTime? AvailableFrom { get; set; }
    public DateTime? AvailableTo { get; set; }
    public List<AvailabilityDump> Availability { get; set; } = new();
    public DurationDump? Work { get; set; }
    public DurationDump? ActualWork { get; set; }
    public DurationDump? RemainingWork { get; set; }
    public double? Cost { get; set; }
    public double? ActualCost { get; set; }
    public double? RemainingCost { get; set; }
    public RateDump? StandardRate { get; set; }
    public RateDump? OvertimeRate { get; set; }
    public double? CostPerUse { get; set; }
    public string? AccrueAt { get; set; }
    public string? Notes { get; set; }
    public int AssignmentCount { get; set; }
}

public sealed class AvailabilityDump
{
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public double? Units { get; set; }
}

public sealed class RateTableEntryDump
{
    public int? ResourceId { get; set; }
    public int? ResourceUniqueId { get; set; }
    public string? ResourceName { get; set; }
    public string RateTable { get; set; } = "";
    public int RateTableIndex { get; set; }
    public bool TableIsPopulated { get; set; }
    public int EntryIndex { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool EffectiveFromIsOpen { get; set; }
    public bool EffectiveToIsOpen { get; set; }
    public RateDump? StandardRate { get; set; }
    public RateDump? OvertimeRate { get; set; }
    public double? CostPerUse { get; set; }
}
