using MPXJ.Net;
using MppReaderTest.Models;
using Task = MPXJ.Net.Task;

namespace MppReaderTest;

public sealed partial class ProjectInspector
{
    private const int RateTableCount = 5;

    // ---------------------------------------------------------------- Resources

    private void ReadResources()
    {
        foreach (var r in _resources)
        {
            var ctx = $"Resource[UID={Safe(() => r.UniqueID, "Resource.UniqueID")}]";
            var calendar = Safe(() => r.Calendar, ctx + ".Calendar");
            var dump = new ResourceDump
            {
                Id = Safe(() => r.ID, ctx + ".ID"),
                UniqueId = Safe(() => r.UniqueID, ctx + ".UniqueID"),
                Guid = Safe(() => r.GUID, ctx + ".GUID"),
                Name = Safe(() => r.Name, ctx + ".Name"),
                Initials = Safe(() => r.Initials, ctx + ".Initials"),
                Code = Safe(() => r.Code, ctx + ".Code"),
                Group = Safe(() => r.Group, ctx + ".Group"),
                EmailAddress = Safe(() => r.EmailAddress, ctx + ".EmailAddress"),
                Type = Safe(() => r.Type, ctx + ".Type")?.ToString(),
                MaterialLabel = Safe(() => r.MaterialLabel, ctx + ".MaterialLabel"),
                IsNull = Safe(() => r.Null, ctx + ".Null"),
                IsGeneric = Safe(() => r.Generic, ctx + ".Generic"),
                IsActive = Safe(() => r.Active, ctx + ".Active"),
                IsEnterprise = Safe(() => r.Enterprise, ctx + ".Enterprise"),
                CalendarUniqueId = Safe(() => r.CalendarUniqueID, ctx + ".CalendarUniqueID"),
                CalendarName = calendar?.Name,
                BaseCalendar = Safe(() => r.BaseCalendar, ctx + ".BaseCalendar"),
                MaxUnits = Safe(() => r.MaxUnits, ctx + ".MaxUnits"),
                AvailableFrom = Safe(() => r.AvailableFrom, ctx + ".AvailableFrom"),
                AvailableTo = Safe(() => r.AvailableTo, ctx + ".AvailableTo"),
                Work = Fmt.ToDump(Safe(() => r.Work, ctx + ".Work")),
                ActualWork = Fmt.ToDump(Safe(() => r.ActualWork, ctx + ".ActualWork")),
                RemainingWork = Fmt.ToDump(Safe(() => r.RemainingWork, ctx + ".RemainingWork")),
                Cost = Safe(() => r.Cost, ctx + ".Cost"),
                ActualCost = Safe(() => r.ActualCost, ctx + ".ActualCost"),
                RemainingCost = Safe(() => r.RemainingCost, ctx + ".RemainingCost"),
                StandardRate = Fmt.ToDump(Safe(() => r.StandardRate, ctx + ".StandardRate")),
                OvertimeRate = Fmt.ToDump(Safe(() => r.OvertimeRate, ctx + ".OvertimeRate")),
                CostPerUse = Safe(() => r.CostPerUse, ctx + ".CostPerUse"),
                AccrueAt = Safe(() => r.AccrueAt, ctx + ".AccrueAt")?.ToString(),
                Notes = Safe(() => r.Notes, ctx + ".Notes"),
                AssignmentCount = Safe(() => r.TaskAssignments?.Count, ctx + ".TaskAssignments") ?? 0,
            };

            var availability = Safe(() => r.Availability?.ToList(), ctx + ".Availability") ?? new List<Availability>();
            foreach (var a in availability)
            {
                dump.Availability.Add(new AvailabilityDump
                {
                    Start = NormalizeStart(a.Range?.Start),
                    End = NormalizeEnd(a.Range?.End),
                    Units = a.Units
                });
            }

            _dump.Resources.Add(dump);
        }
    }

    private static bool IsOpenStart(DateTime? d) => d.HasValue && d.Value <= DateTimeHelper.StartDateNA;
    private static bool IsOpenEnd(DateTime? d) => d.HasValue && d.Value >= DateTimeHelper.EndDateNA;
    private static DateTime? NormalizeStart(DateTime? d) => IsOpenStart(d) ? null : d;
    private static DateTime? NormalizeEnd(DateTime? d) => IsOpenEnd(d) ? null : d;

    // ---------------------------------------------------------------- Rate tables

    private void ReadRateTables()
    {
        foreach (var r in _resources)
        {
            var uid = Safe(() => r.UniqueID, "Resource.UniqueID");
            for (var tableIndex = 0; tableIndex < RateTableCount; tableIndex++)
            {
                var idx = tableIndex;
                var table = Safe(() => r.GetCostRateTable(idx), $"Resource.GetCostRateTable({idx})");
                if (table == null) continue;
                var populated = Safe(() => table.TableIsPopulated, "CostRateTable.TableIsPopulated");
                var entries = Safe(() => table.ToList(), "CostRateTable entries") ?? new List<CostRateTableEntry>();
                for (var e = 0; e < entries.Count; e++)
                {
                    var entry = entries[e];
                    var start = Safe(() => entry.StartDate, "CostRateTableEntry.StartDate");
                    var end = Safe(() => entry.EndDate, "CostRateTableEntry.EndDate");
                    _dump.ResourceRateTables.Add(new RateTableEntryDump
                    {
                        ResourceId = r.ID,
                        ResourceUniqueId = uid,
                        ResourceName = r.Name,
                        RateTable = Fmt.RateTableLetter(idx),
                        RateTableIndex = idx,
                        TableIsPopulated = populated,
                        EntryIndex = e,
                        EffectiveFrom = NormalizeStart(start),
                        EffectiveTo = NormalizeEnd(end),
                        EffectiveFromIsOpen = start == null || IsOpenStart(start),
                        EffectiveToIsOpen = end == null || IsOpenEnd(end),
                        StandardRate = Fmt.ToDump(Safe(() => entry.StandardRate, "CostRateTableEntry.StandardRate")),
                        OvertimeRate = Fmt.ToDump(Safe(() => entry.OvertimeRate, "CostRateTableEntry.OvertimeRate")),
                        CostPerUse = Safe(() => entry.CostPerUse, "CostRateTableEntry.CostPerUse"),
                    });
                }
            }
        }
    }

    // ---------------------------------------------------------------- Assignments

    private void ReadAssignments()
    {
        foreach (var a in _assignments)
        {
            var ctx = $"Assignment[UID={Safe(() => a.UniqueID, "ResourceAssignment.UniqueID")}]";
            var task = Safe(() => a.Task, ctx + ".Task");
            var resource = Safe(() => a.Resource, ctx + ".Resource");
            var rateIndex = Safe(() => a.CostRateTableIndex, ctx + ".CostRateTableIndex");
            var work = Safe(() => a.Work, ctx + ".Work");
            var overtime = Safe(() => a.OvertimeWork, ctx + ".OvertimeWork");
            var cost = Safe(() => a.Cost, ctx + ".Cost");
            var start = Safe(() => a.Start, ctx + ".Start");

            if (task == null) Warn("asg-no-task", "Hay asignaciones sin tarea asociada.");
            var resourceGuid = Safe(() => a.Get(AssignmentField.AssignmentResourceGuid) as Guid?, ctx + ".AssignmentResourceGuid");
            if (resource == null)
            {
                Warn("asg-no-resource", "Hay asignaciones sin recurso en el fichero (Resource = null). En MS Project corresponden al recurso interno " +
                                        "'sin asignar' (trabajo de tareas sin recurso); su ResourceGuid se informa en el volcado.");
            }

            var dump = new AssignmentDump
            {
                UniqueId = Safe(() => a.UniqueID, ctx + ".UniqueID"),
                Guid = Safe(() => a.GUID, ctx + ".GUID"),
                TaskId = task?.ID,
                TaskUniqueId = Safe(() => a.TaskUniqueID, ctx + ".TaskUniqueID") ?? task?.UniqueID,
                TaskName = task?.Name,
                TaskMissing = task == null,
                ResourceId = resource?.ID,
                ResourceUniqueId = Safe(() => a.ResourceUniqueID, ctx + ".ResourceUniqueID") ?? resource?.UniqueID,
                ResourceName = resource?.Name,
                ResourceGuid = resourceGuid,
                ResourceType = resource?.Type?.ToString(),
                ResourceMissing = resource == null,
                Start = start,
                Finish = Safe(() => a.Finish, ctx + ".Finish"),
                ActualStart = Safe(() => a.ActualStart, ctx + ".ActualStart"),
                ActualFinish = Safe(() => a.ActualFinish, ctx + ".ActualFinish"),
                Units = Safe(() => a.Units, ctx + ".Units"),
                RemainingUnits = Safe(() => a.RemainingUnits, ctx + ".RemainingUnits"),
                Work = Fmt.ToDump(work),
                ActualWork = Fmt.ToDump(Safe(() => a.ActualWork, ctx + ".ActualWork")),
                RemainingWork = Fmt.ToDump(Safe(() => a.RemainingWork, ctx + ".RemainingWork")),
                OvertimeWork = Fmt.ToDump(overtime),
                Cost = cost,
                ActualCost = Safe(() => a.ActualCost, ctx + ".ActualCost"),
                RemainingCost = Safe(() => a.RemainingCost, ctx + ".RemainingCost"),
                OvertimeCost = Safe(() => a.OvertimeCost, ctx + ".OvertimeCost"),
                BaselineCost = Safe(() => a.BaselineCost, ctx + ".BaselineCost"),
                Baseline6Cost = Safe(() => a.GetBaselineCost(6), ctx + ".GetBaselineCost(6)"),
                CostRateTableIndex = rateIndex,
                CostRateTable = Fmt.RateTableLetter(rateIndex),
                RateSource = Safe(() => a.RateSource, ctx + ".RateSource")?.ToString(),
                OverrideRate = Fmt.ToDump(Safe(() => a.OverrideRate, ctx + ".OverrideRate")),
                PercentWorkComplete = Safe(() => a.PercentageWorkComplete, ctx + ".PercentageWorkComplete"),
                EffectiveCalendarName = Safe(() => a.EffectiveCalendar?.Name, ctx + ".EffectiveCalendar"),
                WorkContour = Safe(() => a.WorkContour?.Name, ctx + ".WorkContour"),
            };

            dump.CostValidation = ValidateCost(resource, rateIndex, start ?? task?.Start, work, overtime, cost);
            ReadTimephased(a, dump.Timephased);
            _dump.ResourceAssignments.Add(dump);
        }
    }

    private CostValidationDump? ValidateCost(Resource? resource, int rateIndex, DateTime? date, Duration? work, Duration? overtime, double? cost)
    {
        const string note = "SOLO VALIDACIÓN: no sustituye al coste leído del MPP. Puede no coincidir por cambios de tarifa durante la asignación, " +
                            "calendarios, horas extra, costes por uso, perfiles de trabajo (contornos), costes reales introducidos manualmente u otras reglas de Microsoft Project.";
        if (resource == null || work == null) return null;
        var type = Safe(() => resource.Type, "Resource.Type");
        if (type == ResourceType.Cost)
        {
            return new CostValidationDump { CostReadFromMpp = cost, Formula = "Recurso de tipo Cost: el importe se introduce directamente, no se calcula.", Note = note };
        }

        var table = Safe(() => resource.GetCostRateTable(rateIndex), "Resource.GetCostRateTable");
        CostRateTableEntry? entry = null;
        if (table != null && table.Count > 0)
        {
            entry = date.HasValue ? Safe(() => table.GetEntryByDate(date.Value), "CostRateTable.GetEntryByDate") : null;
            entry ??= table[0];
        }
        if (entry == null) return new CostValidationDump { CostReadFromMpp = cost, Formula = "Sin tabla de tarifas disponible.", Note = note };

        var std = entry.StandardRate;
        var ot = entry.OvertimeRate;
        var perUse = entry.CostPerUse ?? 0;
        var v = new CostValidationDump
        {
            CostReadFromMpp = cost,
            RateLookupDate = date,
            StandardRateUsed = Fmt.ToDump(std),
            OvertimeRateUsed = Fmt.ToDump(ot),
            CostPerUseUsed = perUse,
            Note = note
        };

        if (type == ResourceType.Material)
        {
            var qty = work.DurationValue;
            v.RegularQuantity = qty;
            v.CalculatedCostForValidation = qty * (std?.Amount ?? 0) + perUse;
            v.Formula = $"Material: cantidad ({Fmt.Num(qty)}) x tarifa estándar ({Fmt.RateText(std)}) + coste por uso ({Fmt.Num(perUse)}) [tabla {Fmt.RateTableLetter(rateIndex)}]";
        }
        else
        {
            var totalHours = ToHours(work);
            var otHours = ToHours(overtime) ?? 0;
            var stdHourly = HourlyAmount(std);
            var otHourly = HourlyAmount(ot);
            if (totalHours == null || stdHourly == null)
            {
                v.Formula = "No se pudo convertir el trabajo o la tarifa a horas.";
                return v;
            }
            var regular = totalHours.Value - otHours;
            v.RegularQuantity = regular;
            v.OvertimeQuantity = otHours;
            v.CalculatedCostForValidation = regular * stdHourly.Value + otHours * (otHourly ?? 0) + perUse;
            v.Formula = $"Trabajo: horas normales ({Fmt.Num(regular)}) x tarifa estándar/h ({Fmt.Num(stdHourly)}) + horas extra ({Fmt.Num(otHours)}) x tarifa extra/h ({Fmt.Num(otHourly)}) + coste por uso ({Fmt.Num(perUse)}) [tabla {Fmt.RateTableLetter(rateIndex)}, tarifa vigente en {Fmt.Date(date)}]";
        }

        if (cost.HasValue && v.CalculatedCostForValidation.HasValue)
        {
            v.Difference = cost.Value - v.CalculatedCostForValidation.Value;
        }
        return v;
    }

    private (double MinutesPerDay, double MinutesPerWeek, double DaysPerMonth) TimeDefaults()
    {
        var p = _project.ProjectProperties;
        return (Safe(() => p.MinutesPerDay, "MinutesPerDay") ?? 480,
                Safe(() => p.MinutesPerWeek, "MinutesPerWeek") ?? 2400,
                Safe(() => p.DaysPerMonth, "DaysPerMonth") ?? 20);
    }

    private double? ToHours(Duration? d)
    {
        if (d?.Units == null) return null;
        if (d.Units == TimeUnit.Hours) return d.DurationValue;
        var (mpd, mpw, dpm) = TimeDefaults();
        return Safe(() => Duration.ConvertUnits(_project, d.DurationValue, d.Units.Value, TimeUnit.Hours, mpd, mpw, dpm)?.DurationValue,
            "Duration.ConvertUnits");
    }

    private double? HourlyAmount(Rate? rate)
    {
        if (rate?.Units == null) return rate?.Amount;
        var (mpd, mpw, dpm) = TimeDefaults();
        var hoursPerUnit = rate.Units.Value switch
        {
            TimeUnit.Minutes or TimeUnit.ElapsedMinutes => 1.0 / 60,
            TimeUnit.Hours or TimeUnit.ElapsedHours => 1.0,
            TimeUnit.Days => mpd / 60,
            TimeUnit.ElapsedDays => 24.0,
            TimeUnit.Weeks => mpw / 60,
            TimeUnit.ElapsedWeeks => 168.0,
            TimeUnit.Months => dpm * mpd / 60,
            TimeUnit.Years => 12 * dpm * mpd / 60,
            _ => (double?)null
        };
        return hoursPerUnit is > 0 ? rate.Amount / hoursPerUnit.Value : null;
    }

    private void ReadTimephased(ResourceAssignment a, TimephasedInfoDump target)
    {
        try
        {
            target.PlannedWork = MapWork(a.RawTimephasedPlannedWork);
            target.ActualRegularWork = MapWork(a.RawTimephasedActualRegularWork);
            target.RemainingRegularWork = MapWork(a.RawTimephasedRemainingRegularWork);
            target.ActualCost = MapCost(a.RawTimephasedActualCost);
        }
        catch (Exception ex)
        {
            target.Error = $"{ex.GetType().Name}: {ex.Message}";
            Warn("timephased", $"Error leyendo datos timephased de asignaciones: {ex.GetType().Name}: {ex.Message}");
        }

        static List<TimephasedItemDump> MapWork(IList<TimephasedWork>? items) =>
            items?.Select(i => new TimephasedItemDump
            {
                Start = i.Start,
                Finish = i.Finish,
                TotalAmount = Fmt.Dur(i.TotalAmount),
                AmountPerHour = Fmt.Dur(i.AmountPerHour)
            }).ToList() ?? new List<TimephasedItemDump>();

        static List<TimephasedItemDump> MapCost(IList<TimephasedCost>? items) =>
            items?.Select(i => new TimephasedItemDump
            {
                Start = i.Start,
                Finish = i.Finish,
                TotalAmount = Fmt.Num(i.TotalAmount),
                AmountPerHour = Fmt.Num(i.AmountPerHour)
            }).ToList() ?? new List<TimephasedItemDump>();
    }
}
