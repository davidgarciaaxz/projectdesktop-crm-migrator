export interface ResourceDto {
    bookableresourceid: string;
    name: string;
    timezone?: number;
    axz_resourceimage?: string;
    _calendarid_value?: string;
}

export interface CalendarRuleDto {
    calendarruleid: string;
    name?: string;
    description?: string;
    starttime?: string;
    endtime?: string;
    duration?: number;
    effort?: number;
    pattern?: string;
    rank?: number;
    timecode?: number;
    subcode?: number;
    timezonecode?: number;
    innercalendarid?: string;
    isvaried?: boolean;
    issimple?: boolean;
    effectiveintervalstart?: string;
    effectiveintervalend?: string;
    offset?: number;
}

export interface CalendarDto {
    calendarid: string;
    name?: string;
    type?: number;
    calendar_calendar_rules: CalendarRuleDto[];
}

export interface CalendarRuleNode {
    rule: CalendarRuleDto;
    children: CalendarRuleNode[];
    sourceCalendarId: string;
    depth: number;
}

export type ResourceCalendarEventType =
    "working" | "break" | "vacation" | "absence" | "nonWorking" | "businessClosure" | "exception" | "unknown";

export type CalendarViewMode = "week" | "month" | "timeline";

export interface CalendarTimeBlockDto {
    Start: string;
    End: string;
    TimeCode?: string | number;
    SubCode?: string | number;
    CalendarId?: string;
    SourceId?: string;
    SourceTypeCode?: number;
    DisplayText?: string;
    Effort?: number;
    IsActivity?: boolean;
    ActivityStatusCode?: number;
}

export interface ResourceCalendarEvent {
    id: string;
    type: ResourceCalendarEventType;
    title: string;
    start: Date;
    end: Date;
    durationMinutes: number;
    allDay: boolean;
    capacity?: number;
    sourceCalendarId: string;
    sourceRuleId?: string;
    isRecurring: boolean;
    recurrencePattern?: string;
    timeZoneCode?: number;
    rawRule?: CalendarRuleDto;
    rawTimeBlock?: CalendarTimeBlockDto;
    hierarchyDepth?: number;
}

export type WarningCode =
    "invalidRule" | "invalidInterval" | "unknownRule" | "recurrence" | "nestedExtent" | "priority"
    | "cycle" | "depth" | "duplicate" | "truncated" | "timeZone";

export interface CalendarWarning {
    code: WarningCode;
    ruleId?: string;
    calendarId?: string;
}

export interface CalendarNormalizationResult {
    events: ResourceCalendarEvent[];
    warnings: CalendarWarning[];
    unplacedRules: CalendarRuleNode[];
    discardedDuplicates: number;
}

export interface ResourceCalendarData {
    resource: ResourceDto;
    calendar?: CalendarDto;
    tree: CalendarRuleNode[];
    warnings: CalendarWarning[];
    nestedCalendarCount: number;
}

export interface DateRange {
    start: Date;
    end: Date;
}

export type GetString = (key: string) => string;