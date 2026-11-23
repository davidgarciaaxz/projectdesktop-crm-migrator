import { CalendarViewMode, DateRange, ResourceCalendarEvent, ResourceCalendarEventType } from "../models";

export const eventTypes: ResourceCalendarEventType[] = [
    "working", "break", "vacation", "absence", "nonWorking", "businessClosure", "exception", "unknown",
];

export interface CalendarMetrics {
    hours: Record<ResourceCalendarEventType, number>;
    availability?: number;
    workingDays: number;
    vacationDays: number;
    exceptionDays: number;
    exceptions: number;
    totalHours: number;
    knownCoverage: number;
    averageWorkingHours: number;
}

export interface DailyMetrics {
    date: Date;
    metrics: CalendarMetrics;
}

export function utcDay(date: Date): Date {
    return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
}

export function today(): Date {
    const now = new Date();
    return new Date(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate()));
}

export function addDays(date: Date, count: number): Date {
    const next = new Date(date);
    next.setUTCDate(next.getUTCDate() + count);
    return next;
}

export function visibleRange(date: Date, mode: Exclude<CalendarViewMode, "timeline">): DateRange {
    const start = utcDay(date);
    if (mode === "week") {
        start.setUTCDate(start.getUTCDate() - (start.getUTCDay() + 6) % 7);
        return { start, end: addDays(start, 7) };
    }
    start.setUTCDate(1);
    return { start, end: new Date(Date.UTC(start.getUTCFullYear(), start.getUTCMonth() + 1, 1)) };
}

export function movePeriod(date: Date, mode: "week" | "month", direction: number): Date {
    if (mode === "week") return addDays(date, direction * 7);
    return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + direction, 1));
}

export function datesInRange(range: DateRange): Date[] {
    const dates: Date[] = [];
    for (let day = utcDay(range.start); day < range.end; day = addDays(day, 1)) dates.push(day);
    return dates;
}

export function overlaps(event: ResourceCalendarEvent, range: DateRange): boolean {
    return event.start < range.end && event.end > range.start;
}

export function clippedMinutes(event: ResourceCalendarEvent, range: DateRange): number {
    return Math.max(0, Math.min(event.end.getTime(), range.end.getTime()) - Math.max(event.start.getTime(), range.start.getTime())) / 60000;
}

export function mergeIntervals(events: ResourceCalendarEvent[], range: DateRange): [number, number][] {
    const intervals = events.filter((event) => overlaps(event, range))
        .map((event): [number, number] => [Math.max(event.start.getTime(), range.start.getTime()), Math.min(event.end.getTime(), range.end.getTime())])
        .sort((left, right) => left[0] - right[0]);
    const merged: [number, number][] = [];
    for (const interval of intervals) {
        const last = merged[merged.length - 1];
        if (last && interval[0] <= last[1]) last[1] = Math.max(last[1], interval[1]);
        else merged.push([...interval]);
    }
    return merged;
}

function unionHours(events: ResourceCalendarEvent[], range: DateRange): number {
    return mergeIntervals(events, range).reduce((total, [start, end]) => total + (end - start) / 3600000, 0);
}

export function calculateMetrics(events: ResourceCalendarEvent[], range: DateRange): CalendarMetrics {
    const visible = events.filter((event) => overlaps(event, range));
    const hours = Object.fromEntries(
        eventTypes.map((type) => [type, unionHours(visible.filter((event) => event.type === type), range)])) as Record<ResourceCalendarEventType, number>;
    const dayCount = (type: ResourceCalendarEventType): number => datesInRange(range)
        .filter((date) => visible.some((event) => event.type === type && overlaps(event, { start: date, end: addDays(date, 1) }))).length;
    const denominator = hours.working + hours.vacation + hours.absence;
    const totalHours = unionHours(visible, range);
    const knownHours = unionHours(visible.filter((event) => event.type !== "unknown"), range);
    const rangeHours = (range.end.getTime() - range.start.getTime()) / 3600000;
    const workingDays = dayCount("working");
    return {
        hours, availability: denominator > 0 ? hours.working / denominator * 100 : undefined,
        workingDays, vacationDays: dayCount("vacation"), exceptionDays: dayCount("exception"),
        exceptions: new Set(visible.filter((event) => event.type === "exception").map((event) => event.id)).size,
        totalHours, knownCoverage: rangeHours > 0 ? knownHours / rangeHours * 100 : 0,
        averageWorkingHours: workingDays > 0 ? hours.working / workingDays : 0,
    };
}

export function dailyMetrics(events: ResourceCalendarEvent[], range: DateRange): DailyMetrics[] {
    return datesInRange(range).map((date) => ({ date, metrics: calculateMetrics(events, { start: date, end: addDays(date, 1) }) }));
}

export function longestVacation(events: ResourceCalendarEvent[], range: DateRange): number {
    return mergeIntervals(events.filter((event) => event.type === "vacation"), range)
        .reduce((longest, [start, end]) => Math.max(longest, (end - start) / 3600000), 0);
}