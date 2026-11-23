import { rrulestr } from "rrule";
import { CalendarNormalizationResult, CalendarRuleNode, CalendarTimeBlockDto, CalendarWarning, DateRange, ResourceCalendarEvent } from "../models";
import { CalendarRuleClassifier } from "./calendarRuleMappings";

function parseDate(value?: string): Date | undefined {
    if (!value || !/^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/i.test(value)) return undefined;
    const parsed = new Date(value);
    return Number.isFinite(parsed.getTime()) ? parsed : undefined;
}

export class CalendarRuleNormalizer {
    public normalizeExpanded(blocks: CalendarTimeBlockDto[], tree: CalendarRuleNode[], range: DateRange, calendarId: string): CalendarNormalizationResult {
        const events: ResourceCalendarEvent[] = [];
        const warnings: CalendarWarning[] = [];
        const ruleIndex = new Map<string, CalendarRuleNode>();
        const indexRules = (nodes: CalendarRuleNode[]): void => {
            for (const node of nodes) {
                ruleIndex.set(node.rule.calendarruleid.toLowerCase(), node);
                indexRules(node.children);
            }
        };
        indexRules(tree);
        const seen = new Set<string>();
        const classifier = new CalendarRuleClassifier();
        let discardedDuplicates = 0;
        for (const block of blocks) {
            const sourceId = block.SourceId?.replace(/[{}]/g, "").toLowerCase();
            const sourceCalendarId = block.CalendarId?.replace(/[{}]/g, "").toLowerCase() ?? calendarId;
            const start = parseDate(block.Start);
            const end = parseDate(block.End);
            const node = sourceId ? ruleIndex.get(sourceId) : undefined;
            if (!start || !end || end <= start) {
                warnings.push({ code: "invalidInterval", ruleId: sourceId, calendarId: sourceCalendarId });
                continue;
            }
            if (start >= range.end || end <= range.start) continue;
            const type = classifier.classifyTimeBlock(block);
            const key = JSON.stringify([sourceCalendarId, sourceId, type, start.toISOString(), end.toISOString(),
                block.TimeCode, block.SubCode, block.Effort, block.DisplayText, block.IsActivity, block.SourceTypeCode, block.ActivityStatusCode]);
            if (seen.has(key)) {
                discardedDuplicates += 1;
                continue;
            }
            seen.add(key);
            if (type === "unknown") warnings.push({ code: "unknownRule", ruleId: sourceId ?? key, calendarId: sourceCalendarId });
            events.push({
                id: key, type, title: block.DisplayText ?? node?.rule.name ?? "", start, end,
                durationMinutes: (+end - +start) / 60000,
                allDay: +end - +start >= 86400000 && start.getUTCHours() === 0,
                capacity: block.Effort, sourceCalendarId, sourceRuleId: node?.rule.calendarruleid,
                isRecurring: Boolean(node?.rule.pattern), recurrencePattern: node?.rule.pattern,
                timeZoneCode: node?.rule.timezonecode, rawRule: node?.rule, rawTimeBlock: block, hierarchyDepth: node?.depth,
            });
        }
        return { events: events.sort((left, right) => +left.start - +right.start), warnings, unplacedRules: [], discardedDuplicates };
    }

    public normalize(tree: CalendarRuleNode[], range: DateRange): CalendarNormalizationResult {
        const events: ResourceCalendarEvent[] = [];
        const warnings: CalendarWarning[] = [];
        const unplacedRules: CalendarRuleNode[] = [];
        const seen = new Set<string>();
        const classifier = new CalendarRuleClassifier();
        let discardedDuplicates = 0;

        const visit = (node: CalendarRuleNode, inheritedExtent: boolean): void => {
            const rule = node.rule;
            const warn = (code: CalendarWarning["code"]): void => {
                warnings.push({ code, ruleId: rule.calendarruleid, calendarId: node.sourceCalendarId });
            };
            if (rule.innercalendarid) {
                for (const child of node.children) visit(child, true);
                unplacedRules.push(node);
                warn("nestedExtent");
                return;
            }
            const type = classifier.classify(rule);
            if (type === "unknown") warn("unknownRule");
            if (inheritedExtent || rule.offset !== undefined && rule.offset !== 0) {
                unplacedRules.push(node);
                warn("nestedExtent");
                return;
            }
            const start = parseDate(rule.starttime);
            let end = parseDate(rule.endtime);
            if (!end && !rule.endtime && start && rule.duration !== undefined && rule.duration > 0) {
                end = new Date(start.getTime() + rule.duration * 60000);
            }
            if (!start || !end || !Number.isFinite(end.getTime()) || end <= start) {
                unplacedRules.push(node);
                warn("invalidInterval");
                return;
            }
            if (rule.rank !== undefined && rule.rank !== 0) warn("priority");
            const durationMs = end.getTime() - start.getTime();
            let starts = [start];
            if (rule.pattern?.trim()) {
                const supportedPattern = /^(?:DTSTART[^\n]*\r?\n)?RRULE:FREQ=(DAILY|WEEKLY|MONTHLY|YEARLY)(;|$)/i.test(rule.pattern.trim());
                if (rule.timezonecode !== undefined || !supportedPattern || /BYSECOND|BYMINUTE/i.test(rule.pattern)) {
                    unplacedRules.push(node);
                    warn("recurrence");
                    return;
                }
                try {
                    const recurrence = rrulestr(rule.pattern, { dtstart: start });
                    starts = recurrence.between(new Date(range.start.getTime() - durationMs), range.end, true, (_, index) => index < 10000);
                    if (starts.length >= 10000) {
                        unplacedRules.push(node);
                        warn("truncated");
                        return;
                    }
                } catch {
                    unplacedRules.push(node);
                    warn("recurrence");
                    return;
                }
            }
            const effectiveStart = parseDate(rule.effectiveintervalstart);
            const effectiveEnd = parseDate(rule.effectiveintervalend);
            if (rule.effectiveintervalstart && !effectiveStart || rule.effectiveintervalend && !effectiveEnd) {
                unplacedRules.push(node);
                warn("invalidInterval");
                return;
            }
            for (const occurrence of starts) {
                const occurrenceEnd = new Date(occurrence.getTime() + durationMs);
                const clippedStart = new Date(Math.max(occurrence.getTime(), effectiveStart?.getTime() ?? -Infinity));
                const clippedEnd = new Date(Math.min(occurrenceEnd.getTime(), effectiveEnd?.getTime() ?? Infinity));
                if (clippedEnd <= clippedStart) continue;
                const key = `${node.sourceCalendarId}:${rule.calendarruleid}:${type}:${clippedStart.toISOString()}:${clippedEnd.toISOString()}`;
                if (seen.has(key)) {
                    discardedDuplicates += 1;
                    warn("duplicate");
                    continue;
                }
                seen.add(key);
                events.push({
                    id: key, type, title: rule.name ?? "", start: clippedStart, end: clippedEnd,
                    durationMinutes: (clippedEnd.getTime() - clippedStart.getTime()) / 60000,
                    allDay: clippedEnd.getTime() - clippedStart.getTime() >= 86400000 && clippedStart.getUTCHours() === 0,
                    capacity: rule.effort, sourceCalendarId: node.sourceCalendarId, sourceRuleId: rule.calendarruleid,
                    isRecurring: Boolean(rule.pattern?.trim()), recurrencePattern: rule.pattern, timeZoneCode: rule.timezonecode,
                    rawRule: rule, hierarchyDepth: node.depth,
                });
            }
        };

        for (const node of tree) visit(node, false);
        return { events: events.sort((left, right) => left.start.getTime() - right.start.getTime()), warnings, unplacedRules, discardedDuplicates };
    }
}