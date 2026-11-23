import { CalendarDto, CalendarRuleDto, CalendarRuleNode, CalendarWarning, ResourceCalendarData, ResourceDto } from "../models";

const MAX_DEPTH = 8;
const MAX_CALENDARS = 128;
const RULE_COLUMNS = [
    "calendarruleid", "name", "description", "starttime", "endtime", "duration", "effort", "pattern", "rank", "timecode",
    "subcode", "timezonecode", "_innercalendarid_value", "isvaried", "issimple", "effectiveintervalstart", "effectiveintervalend", "offset",
].join(",");

export function normalizeGuid(value: string): string {
    const normalized = value.replace(/[{}]/g, "").toLowerCase();
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(normalized)) throw new Error("Invalid identifier");
    return normalized;
}

function object(value: unknown): Record<string, unknown> {
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid Dataverse response");
    return value as Record<string, unknown>;
}

function text(value: unknown): string | undefined {
    return typeof value === "string" ? value : undefined;
}

function number(value: unknown): number | undefined {
    return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function boolean(value: unknown): boolean | undefined {
    return typeof value === "boolean" ? value : undefined;
}

function parseRule(value: unknown): CalendarRuleDto {
    const row = object(value);
    const innerId = text(row._innercalendarid_value) ?? text(row.innercalendarid);
    return {
        calendarruleid: normalizeGuid(text(row.calendarruleid) ?? ""),
        name: text(row.name), description: text(row.description), starttime: text(row.starttime), endtime: text(row.endtime),
        duration: number(row.duration), effort: number(row.effort), pattern: text(row.pattern), rank: number(row.rank),
        timecode: number(row.timecode), subcode: number(row.subcode), timezonecode: number(row.timezonecode),
        innercalendarid: innerId ? normalizeGuid(innerId) : undefined,
        isvaried: boolean(row.isvaried), issimple: boolean(row.issimple),
        effectiveintervalstart: text(row.effectiveintervalstart), effectiveintervalend: text(row.effectiveintervalend), offset: number(row.offset),
    };
}

export class ResourceCalendarService {
    public constructor(private readonly webAPI: ComponentFramework.WebApi) {}

    public async load(resourceId: string): Promise<ResourceCalendarData> {
        const resource = await this.getResource(normalizeGuid(resourceId));
        const warnings: CalendarWarning[] = [];
        if (!resource._calendarid_value) return { resource, tree: [], warnings, nestedCalendarCount: 0 };
        const cache = new Map<string, CalendarDto>();
        const visited = new Set<string>();
        let nodeCount = 0;

        const loadCalendar = async (calendarId: string, depth: number, ancestors: Set<string>): Promise<CalendarRuleNode[]> => {
            const normalizedId = normalizeGuid(calendarId);
            if (ancestors.has(normalizedId)) {
                warnings.push({ code: "cycle", calendarId: normalizedId });
                return [];
            }
            if (depth > MAX_DEPTH || nodeCount >= 4096) {
                warnings.push({ code: "depth", calendarId: normalizedId });
                console.warn("ResourceCalendarViewer: calendar traversal limit reached");
                return [];
            }
            let calendar = cache.get(normalizedId);
            if (!calendar) {
                if (visited.size >= MAX_CALENDARS) {
                    warnings.push({ code: "truncated", calendarId: normalizedId });
                    return [];
                }
                visited.add(normalizedId);
                calendar = await this.getCalendar(normalizedId, warnings);
                cache.set(normalizedId, calendar);
            }
            const path = new Set(ancestors);
            path.add(normalizedId);
            const nodes: CalendarRuleNode[] = [];
            for (const rule of calendar.calendar_calendar_rules) {
                nodeCount += 1;
                if (nodeCount > 4096) {
                    warnings.push({ code: "truncated", calendarId: normalizedId });
                    break;
                }
                const children = rule.innercalendarid ? await loadCalendar(rule.innercalendarid, depth + 1, path) : [];
                nodes.push({ rule, children, sourceCalendarId: normalizedId, depth });
            }
            return nodes;
        };

        const calendarId = normalizeGuid(resource._calendarid_value);
        const tree = await loadCalendar(calendarId, 0, new Set());
        return { resource, calendar: cache.get(calendarId), tree, warnings, nestedCalendarCount: Math.max(0, cache.size - 1) };
    }

    private async getResource(resourceId: string): Promise<ResourceDto> {
        const response: unknown = await this.webAPI.retrieveRecord(
            "bookableresource", resourceId, "?$select=bookableresourceid,name,timezone,axz_resourceimage,_calendarid_value");
        const row = object(response);
        const calendarId = text(row._calendarid_value);
        return {
            bookableresourceid: normalizeGuid(text(row.bookableresourceid) ?? ""),
            name: text(row.name) ?? "", timezone: number(row.timezone), axz_resourceimage: text(row.axz_resourceimage),
            _calendarid_value: calendarId ? normalizeGuid(calendarId) : undefined,
        };
    }

    private async getCalendar(calendarId: string, warnings: CalendarWarning[]): Promise<CalendarDto> {
        const response: unknown = await this.webAPI.retrieveRecord(
            "calendar", calendarId, `?$select=calendarid,name,type&$expand=calendar_calendar_rules($select=${RULE_COLUMNS})`);
        const row = object(response);
        if (!Array.isArray(row.calendar_calendar_rules)) throw new Error("Calendar rules missing from response");
        const rules: CalendarRuleDto[] = [];
        for (const value of row.calendar_calendar_rules as unknown[]) {
            try {
                rules.push(parseRule(value));
            } catch {
                warnings.push({ code: "invalidRule", calendarId });
            }
        }
        if (row["calendar_calendar_rules@odata.nextLink"]) warnings.push({ code: "truncated", calendarId });
        return { calendarid: normalizeGuid(text(row.calendarid) ?? ""), name: text(row.name), type: number(row.type), calendar_calendar_rules: rules };
    }
}