namespace MppReaderTest.Models;

public sealed class CalendarDump
{
    /// <summary>MPXJ only exposes UniqueID for calendars; there is no separate ID.</summary>
    public int? UniqueId { get; set; }
    public string? Name { get; set; }
    public int? ParentUniqueId { get; set; }
    public string? ParentName { get; set; }
    public bool IsBaseCalendar { get; set; }
    public bool IsDerived { get; set; }
    public bool IsProjectDefault { get; set; }
    public string? CalendarType { get; set; }
    public bool IsPersonal { get; set; }
    public int? MinutesPerDay { get; set; }
    public int? MinutesPerWeek { get; set; }
    public int? DaysPerMonth { get; set; }
    public List<CalendarDayDump> Days { get; set; } = new();
    public List<CalendarWorkWeekDump> WorkWeeks { get; set; } = new();
    public int ExceptionCount { get; set; }
    public List<int?> TaskUniqueIds { get; set; } = new();
    public List<string> TaskNames { get; set; } = new();
    public List<int?> ResourceUniqueIds { get; set; } = new();
    public List<string> ResourceNames { get; set; } = new();
    public List<string> DerivedCalendarNames { get; set; } = new();
}

public sealed class CalendarDayDump
{
    public string Day { get; set; } = "";
    /// <summary>Raw day type defined in this calendar (Default = inherited from base calendar).</summary>
    public string? DefinedDayType { get; set; }
    public List<string> DefinedPeriods { get; set; } = new();
    /// <summary>Effective result after resolving inheritance from the base calendar.</summary>
    public bool? EffectiveWorking { get; set; }
    public List<string> EffectivePeriods { get; set; } = new();
}

public sealed class CalendarWorkWeekDump
{
    public string? Name { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public List<CalendarDayDump> Days { get; set; } = new();
}

public sealed class CalendarExceptionDump
{
    public int? CalendarUniqueId { get; set; }
    public string? CalendarName { get; set; }
    public string? Name { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public bool Working { get; set; }
    public List<string> Periods { get; set; } = new();
    public bool IsRecurring { get; set; }
    public string? RecurrenceType { get; set; }
    public int? Frequency { get; set; }
    public int? Occurrences { get; set; }
    public bool UseEndDate { get; set; }
    public DateOnly? RecurrenceStart { get; set; }
    public DateOnly? RecurrenceFinish { get; set; }
    public bool Relative { get; set; }
    public string? DayOfWeek { get; set; }
    public int? DayNumber { get; set; }
    public int? MonthNumber { get; set; }
    public List<string> WeeklyDays { get; set; } = new();
    public string? RecurrenceDescription { get; set; }
    public int ExpandedOccurrenceCount { get; set; }
}
