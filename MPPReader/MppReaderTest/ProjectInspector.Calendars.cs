using MPXJ.Net;
using MppReaderTest.Models;

namespace MppReaderTest;

public sealed partial class ProjectInspector
{
    private static readonly DayOfWeek[] WeekOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    private void ReadCalendars()
    {
        var calendars = Safe(() => _project.Calendars?.ToList(), "ProjectFile.Calendars") ?? new List<ProjectCalendar>();
        var defaultUid = Safe(() => _project.DefaultCalendar?.UniqueID, "ProjectFile.DefaultCalendar");

        foreach (var cal in calendars)
        {
            var ctx = $"Calendar[UID={Safe(() => cal.UniqueID, "ProjectCalendar.UniqueID")}]";
            var parent = Safe(() => cal.Parent, ctx + ".Parent");
            var tasks = Safe(() => cal.Tasks?.ToList(), ctx + ".Tasks") ?? new List<MPXJ.Net.Task>();
            var resources = Safe(() => cal.Resources?.ToList(), ctx + ".Resources") ?? new List<Resource>();
            var derived = Safe(() => cal.DerivedCalendars?.ToList(), ctx + ".DerivedCalendars") ?? new List<ProjectCalendar>();

            var dump = new CalendarDump
            {
                UniqueId = Safe(() => cal.UniqueID, ctx + ".UniqueID"),
                Name = Safe(() => cal.Name, ctx + ".Name"),
                ParentUniqueId = Safe(() => cal.ParentUniqueID, ctx + ".ParentUniqueID") ?? parent?.UniqueID,
                ParentName = parent?.Name,
                IsBaseCalendar = parent == null,
                IsDerived = Safe(() => cal.Derived, ctx + ".Derived"),
                IsProjectDefault = defaultUid != null && defaultUid == cal.UniqueID,
                CalendarType = Safe(() => cal.Type, ctx + ".Type")?.ToString(),
                IsPersonal = Safe(() => cal.Personal, ctx + ".Personal"),
                MinutesPerDay = Safe(() => cal.MinutesPerDay, ctx + ".MinutesPerDay"),
                MinutesPerWeek = Safe(() => cal.MinutesPerWeek, ctx + ".MinutesPerWeek"),
                DaysPerMonth = Safe(() => cal.DaysPerMonth, ctx + ".DaysPerMonth"),
                TaskUniqueIds = tasks.Select(t => t.UniqueID).ToList(),
                TaskNames = tasks.Select(t => t.Name ?? "").ToList(),
                ResourceUniqueIds = resources.Select(r => r.UniqueID).ToList(),
                ResourceNames = resources.Select(r => r.Name ?? "").ToList(),
                DerivedCalendarNames = derived.Select(d => d.Name ?? "").ToList(),
            };

            foreach (var day in WeekOrder)
            {
                var defined = ReadDays(cal, day, ctx);
                defined.EffectiveWorking = Safe(() => (bool?)cal.IsWorkingDay(day), $"{ctx}.IsWorkingDay({day})");
                var effective = Safe(() => cal.GetHours(day)?.ToList(), $"{ctx}.GetHours({day})");
                defined.EffectivePeriods = effective?.Select(Fmt.Period).ToList() ?? new List<string>();
                dump.Days.Add(defined);
            }

            var weeks = Safe(() => cal.WorkWeeks?.ToList(), ctx + ".WorkWeeks") ?? new List<ProjectCalendarWeek>();
            foreach (var week in weeks)
            {
                var range = Safe(() => week.DateRange, ctx + ".WorkWeek.DateRange");
                var weekDump = new CalendarWorkWeekDump
                {
                    Name = Safe(() => week.Name, ctx + ".WorkWeek.Name"),
                    From = range?.Start,
                    To = range?.End
                };
                foreach (var day in WeekOrder) weekDump.Days.Add(ReadDays(week, day, ctx + ".WorkWeek"));
                dump.WorkWeeks.Add(weekDump);
            }

            var exceptions = Safe(() => cal.CalendarExceptions?.ToList(), ctx + ".CalendarExceptions") ?? new List<ProjectCalendarException>();
            dump.ExceptionCount = exceptions.Count;
            foreach (var ex in exceptions) _dump.CalendarExceptions.Add(ReadException(cal, ex, ctx));

            _dump.Calendars.Add(dump);
        }
    }

    private CalendarDayDump ReadDays(ProjectCalendarDays days, DayOfWeek day, string ctx)
    {
        var type = Safe(() => days.GetCalendarDayType(day), $"{ctx}.GetCalendarDayType({day})");
        var hours = Safe(() => days.GetCalendarHours(day)?.ToList(), $"{ctx}.GetCalendarHours({day})");
        return new CalendarDayDump
        {
            Day = day.ToString(),
            DefinedDayType = type?.ToString(),
            DefinedPeriods = hours?.Select(Fmt.Period).ToList() ?? new List<string>()
        };
    }

    private CalendarExceptionDump ReadException(ProjectCalendar cal, ProjectCalendarException ex, string ctx)
    {
        var periods = Safe(() => ex.ToList(), ctx + ".Exception.Periods") ?? new List<TimeOnlyRange>();
        var rec = Safe(() => ex.Recurring, ctx + ".Exception.Recurring");
        var dump = new CalendarExceptionDump
        {
            CalendarUniqueId = cal.UniqueID,
            CalendarName = cal.Name,
            Name = Safe(() => ex.Name, ctx + ".Exception.Name"),
            FromDate = Safe(() => ex.FromDate, ctx + ".Exception.FromDate"),
            ToDate = Safe(() => ex.ToDate, ctx + ".Exception.ToDate"),
            Working = Safe(() => ex.Working, ctx + ".Exception.Working"),
            Periods = periods.Select(Fmt.Period).ToList(),
            IsRecurring = rec != null,
            ExpandedOccurrenceCount = Safe(() => ex.ExpandedExceptions?.Count, ctx + ".Exception.ExpandedExceptions") ?? 0,
        };

        if (rec != null)
        {
            dump.RecurrenceType = Safe(() => rec.RecurrenceType, "RecurringData.RecurrenceType")?.ToString();
            dump.Frequency = Safe(() => rec.Frequency, "RecurringData.Frequency");
            dump.Occurrences = Safe(() => rec.Occurrences, "RecurringData.Occurrences");
            dump.UseEndDate = Safe(() => rec.UseEndDate, "RecurringData.UseEndDate");
            dump.RecurrenceStart = Safe(() => rec.StartDate, "RecurringData.StartDate");
            dump.RecurrenceFinish = Safe(() => rec.FinishDate, "RecurringData.FinishDate");
            dump.Relative = Safe(() => rec.Relative, "RecurringData.Relative");
            dump.DayOfWeek = Safe(() => rec.DayOfWeek, "RecurringData.DayOfWeek")?.ToString();
            dump.DayNumber = Safe(() => rec.DayNumber, "RecurringData.DayNumber");
            dump.MonthNumber = Safe(() => rec.MonthNumber, "RecurringData.MonthNumber");
            dump.RecurrenceDescription = Safe(() => rec.ToString(), "RecurringData.ToString");
            if (dump.RecurrenceType == nameof(RecurrenceType.Weekly))
            {
                dump.WeeklyDays = WeekOrder.Where(d => Safe(() => rec.GetWeeklyDay(d), "RecurringData.GetWeeklyDay")).Select(d => d.ToString()).ToList();
            }
        }

        return dump;
    }
}
