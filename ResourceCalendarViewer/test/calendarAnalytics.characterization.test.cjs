const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const {
    addDays,
    calculateMetrics,
    clippedMinutes,
    datesInRange,
    dailyMetrics,
    longestVacation,
    mergeIntervals,
    movePeriod,
    overlaps,
    utcDay,
    visibleRange,
} = require("../out/test-build/services/calendarAnalytics.js");

function date(value) {
    return new Date(value);
}

function event(id, type, start, end) {
    return {
        id,
        type,
        title: id,
        start: date(start),
        end: date(end),
        durationMinutes: (date(end).getTime() - date(start).getTime()) / 60000,
        allDay: false,
        sourceCalendarId: "calendar",
        isRecurring: false,
    };
}

describe("calendarAnalytics current-behavior characterization", () => {
    it("returns zero totals for an empty event collection", () => {
        const metrics = calculateMetrics([], {
            start: date("2024-01-01T00:00:00.000Z"),
            end: date("2024-01-02T00:00:00.000Z"),
        });

        assert.deepEqual(metrics.hours, {
            working: 0,
            break: 0,
            vacation: 0,
            absence: 0,
            nonWorking: 0,
            businessClosure: 0,
            exception: 0,
            unknown: 0,
        });
        assert.equal(metrics.availability, undefined);
        assert.equal(metrics.workingDays, 0);
        assert.equal(metrics.vacationDays, 0);
        assert.equal(metrics.exceptionDays, 0);
        assert.equal(metrics.exceptions, 0);
        assert.equal(metrics.totalHours, 0);
        assert.equal(metrics.knownCoverage, 0);
        assert.equal(metrics.averageWorkingHours, 0);
    });

    it("normalizes dates and calculates leap-month ranges using current UTC behavior", () => {
        assert.equal(utcDay(date("2024-02-29T23:59:59.999Z")).toISOString(), "2024-02-29T00:00:00.000Z");

        const range = visibleRange(date("2024-02-29T12:00:00.000Z"), "month");

        assert.equal(range.start.toISOString(), "2024-02-01T00:00:00.000Z");
        assert.equal(range.end.toISOString(), "2024-03-01T00:00:00.000Z");
    });

    it("starts weeks on Monday and treats the end as exclusive", () => {
        const range = visibleRange(date("2024-03-03T18:00:00.000Z"), "week");

        assert.equal(range.start.toISOString(), "2024-02-26T00:00:00.000Z");
        assert.equal(range.end.toISOString(), "2024-03-04T00:00:00.000Z");
        assert.deepEqual(datesInRange(range).map((day) => day.toISOString()), [
            "2024-02-26T00:00:00.000Z",
            "2024-02-27T00:00:00.000Z",
            "2024-02-28T00:00:00.000Z",
            "2024-02-29T00:00:00.000Z",
            "2024-03-01T00:00:00.000Z",
            "2024-03-02T00:00:00.000Z",
            "2024-03-03T00:00:00.000Z",
        ]);
    });

    it("moves month and week periods by their current UTC calendar boundaries", () => {
        assert.equal(movePeriod(date("2024-01-31T16:00:00.000Z"), "month", 1).toISOString(), "2024-02-01T00:00:00.000Z");
        assert.equal(movePeriod(date("2024-03-03T16:00:00.000Z"), "week", 1).toISOString(), "2024-03-10T16:00:00.000Z");
        assert.equal(addDays(date("2024-02-28T00:00:00.000Z"), 1).toISOString(), "2024-02-29T00:00:00.000Z");
    });

    it("excludes intervals that only touch a range boundary", () => {
        const range = { start: date("2024-01-01T10:00:00.000Z"), end: date("2024-01-01T11:00:00.000Z") };

        assert.equal(overlaps(event("before", "working", "2024-01-01T09:00:00.000Z", "2024-01-01T10:00:00.000Z"), range), false);
        assert.equal(overlaps(event("after", "working", "2024-01-01T11:00:00.000Z", "2024-01-01T12:00:00.000Z"), range), false);
        assert.equal(overlaps(event("inside", "working", "2024-01-01T10:30:00.000Z", "2024-01-01T11:30:00.000Z"), range), true);
        assert.equal(clippedMinutes(event("clipped", "working", "2024-01-01T09:30:00.000Z", "2024-01-01T10:30:00.000Z"), range), 30);
    });

    it("merges overlapping and adjacent intervals before totaling same-category time", () => {
        const range = { start: date("2024-01-01T00:00:00.000Z"), end: date("2024-01-02T00:00:00.000Z") };
        const intervals = [
            event("a", "working", "2024-01-01T09:00:00.000Z", "2024-01-01T11:00:00.000Z"),
            event("b", "working", "2024-01-01T10:00:00.000Z", "2024-01-01T12:00:00.000Z"),
            event("c", "working", "2024-01-01T12:00:00.000Z", "2024-01-01T13:00:00.000Z"),
            event("d", "working", "2024-01-01T14:00:00.000Z", "2024-01-01T15:00:00.000Z"),
        ];

        assert.deepEqual(mergeIntervals(intervals, range), [
            [date("2024-01-01T09:00:00.000Z").getTime(), date("2024-01-01T13:00:00.000Z").getTime()],
            [date("2024-01-01T14:00:00.000Z").getTime(), date("2024-01-01T15:00:00.000Z").getTime()],
        ]);
        assert.equal(calculateMetrics(intervals, range).hours.working, 5);
    });

    it("splits an interval crossing midnight across the current UTC daily buckets", () => {
        const intervals = [event("overnight", "working", "2024-01-01T23:00:00.000Z", "2024-01-02T01:00:00.000Z")];
        const days = dailyMetrics(intervals, {
            start: date("2024-01-01T00:00:00.000Z"),
            end: date("2024-01-03T00:00:00.000Z"),
        });

        assert.equal(days.length, 2);
        assert.equal(days[0].date.toISOString(), "2024-01-01T00:00:00.000Z");
        assert.equal(days[0].metrics.hours.working, 1);
        assert.equal(days[1].date.toISOString(), "2024-01-02T00:00:00.000Z");
        assert.equal(days[1].metrics.hours.working, 1);
    });

    it("characterizes working and vacation metrics when their intervals overlap", () => {
        const intervals = [
            event("work", "working", "2024-01-01T09:00:00.000Z", "2024-01-01T12:00:00.000Z"),
            event("leave", "vacation", "2024-01-01T11:00:00.000Z", "2024-01-01T13:00:00.000Z"),
        ];
        const metrics = calculateMetrics(intervals, {
            start: date("2024-01-01T00:00:00.000Z"),
            end: date("2024-01-02T00:00:00.000Z"),
        });

        assert.equal(metrics.hours.working, 3);
        assert.equal(metrics.hours.vacation, 2);
        assert.equal(metrics.totalHours, 4);
        assert.equal(metrics.availability, 60);
    });

    it("characterizes non-working and unknown interval effects on metrics", () => {
        const range = { start: date("2024-01-01T00:00:00.000Z"), end: date("2024-01-02T00:00:00.000Z") };
        const nonWorking = calculateMetrics([
            event("closed", "nonWorking", "2024-01-01T08:00:00.000Z", "2024-01-01T16:00:00.000Z"),
        ], range);
        const partial = calculateMetrics([
            event("unknown", "unknown", "2024-01-01T08:00:00.000Z", "2024-01-01T10:00:00.000Z"),
            event("known", "working", "2024-01-01T10:00:00.000Z", "2024-01-01T12:00:00.000Z"),
        ], range);

        assert.equal(nonWorking.hours.nonWorking, 8);
        assert.equal(nonWorking.availability, undefined);
        assert.equal(nonWorking.knownCoverage, 8 / 24 * 100);
        assert.equal(partial.hours.unknown, 2);
        assert.equal(partial.hours.absence, 0);
        assert.equal(partial.knownCoverage, 2 / 24 * 100);
    });

    it("counts distinct exception IDs and merges vacation intervals for the longest run", () => {
        const intervals = [
            event("same-exception", "exception", "2024-01-01T09:00:00.000Z", "2024-01-01T10:00:00.000Z"),
            event("same-exception", "exception", "2024-01-01T11:00:00.000Z", "2024-01-01T12:00:00.000Z"),
            event("vacation-a", "vacation", "2024-01-01T13:00:00.000Z", "2024-01-01T14:00:00.000Z"),
            event("vacation-b", "vacation", "2024-01-01T14:00:00.000Z", "2024-01-01T16:00:00.000Z"),
        ];
        const range = { start: date("2024-01-01T00:00:00.000Z"), end: date("2024-01-02T00:00:00.000Z") };

        assert.equal(calculateMetrics(intervals, range).exceptions, 1);
        assert.equal(longestVacation(intervals, range), 3);
    });
});