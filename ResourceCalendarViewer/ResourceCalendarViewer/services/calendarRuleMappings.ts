import { CalendarRuleDto, CalendarTimeBlockDto, ResourceCalendarEventType } from "../models";

export interface CalendarRuleMapping {
    timecode: number;
    subcode?: number;
    type: Exclude<ResourceCalendarEventType, "unknown">;
    verifiedSource: string;
}

export const calendarRuleMappings: readonly CalendarRuleMapping[] = [];

export enum CalendarTimeCode {
    Available = 0,
    Busy = 1,
    Unavailable = 2,
    Filter = 3,
}

export enum CalendarSubCode {
    Unspecified = 0,
    Schedulable = 1,
    Committed = 2,
    Uncommitted = 3,
    Break = 4,
    Holiday = 5,
    Vacation = 6,
    Appointment = 7,
    ResourceStartTime = 8,
    ResourceServiceRestriction = 9,
    ResourceCapacity = 10,
    ServiceRestriction = 11,
    ServiceCost = 12,
}

const TIME_CODE_SOURCE = "https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/timecode";
const SUB_CODE_SOURCE = "https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/subcode";

export const timeBlockMappings: readonly CalendarRuleMapping[] = [
    { timecode: CalendarTimeCode.Available, subcode: CalendarSubCode.Unspecified, type: "working", verifiedSource: TIME_CODE_SOURCE },
    { timecode: CalendarTimeCode.Available, subcode: CalendarSubCode.Schedulable, type: "working", verifiedSource: TIME_CODE_SOURCE },
    { timecode: CalendarTimeCode.Unavailable, subcode: CalendarSubCode.Unspecified, type: "nonWorking", verifiedSource: TIME_CODE_SOURCE },
    { timecode: CalendarTimeCode.Unavailable, subcode: CalendarSubCode.Break, type: "break", verifiedSource: SUB_CODE_SOURCE },
    { timecode: CalendarTimeCode.Unavailable, subcode: CalendarSubCode.Holiday, type: "businessClosure", verifiedSource: SUB_CODE_SOURCE },
    { timecode: CalendarTimeCode.Unavailable, subcode: CalendarSubCode.Vacation, type: "vacation", verifiedSource: SUB_CODE_SOURCE },
];

function enumValue(value: number | string | undefined, values: Record<string, number | string>): number | undefined {
    if (typeof value === "number") return value;
    if (value === undefined) return undefined;
    if (/^\d+$/.test(value)) return Number(value);
    const name = value.includes("'") ? value.split("'")[1] : value;
    const result = values[name];
    return typeof result === "number" ? result : undefined;
}

export class CalendarRuleClassifier {
    public classifyTimeBlock(block: CalendarTimeBlockDto): ResourceCalendarEventType {
        if (block.IsActivity === true) return "unknown";
        const timecode = enumValue(block.TimeCode, CalendarTimeCode);
        const subcode = enumValue(block.SubCode, CalendarSubCode);
        return timeBlockMappings.find((mapping) => mapping.timecode === timecode && mapping.subcode === subcode)?.type ?? "unknown";
    }

    public classify(rule: CalendarRuleDto): ResourceCalendarEventType {
        const exact = calendarRuleMappings.find((mapping) => mapping.timecode === rule.timecode && mapping.subcode === rule.subcode);
        if (exact) return exact.type;
        const general = calendarRuleMappings.find((mapping) => mapping.timecode === rule.timecode && mapping.subcode === undefined);
        return general?.type ?? "unknown";
    }
}