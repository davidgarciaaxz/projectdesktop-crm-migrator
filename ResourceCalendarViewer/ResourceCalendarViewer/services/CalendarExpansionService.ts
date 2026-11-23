import { CalendarTimeBlockDto, DateRange } from "../models";
import { normalizeGuid } from "./ResourceCalendarService";

interface ExecuteResponse {
    ok: boolean;
    json(): Promise<unknown>;
}

interface ExecuteRequest {
    getMetadata(): {
        boundParameter: string;
        operationName: string;
        operationType: number;
        parameterTypes: Record<string, { typeName: string; structuralProperty: number }>;
    };
}

export type CalendarExecutor = (request: ExecuteRequest) => Promise<ExecuteResponse>;

export class ExpandCalendarRequest implements ExecuteRequest {
    public readonly entity: { id: string; entityType: string };
    public readonly Start: string;
    public readonly End: string;

    public constructor(calendarId: string, range: DateRange) {
        this.entity = { id: normalizeGuid(calendarId), entityType: "calendar" };
        this.Start = range.start.toISOString();
        this.End = range.end.toISOString();
    }

    public getMetadata(): ReturnType<ExecuteRequest["getMetadata"]> {
        return {
            boundParameter: "entity", operationName: "ExpandCalendar", operationType: 1,
            parameterTypes: {
                entity: { typeName: "mscrm.calendar", structuralProperty: 5 },
                Start: { typeName: "Edm.DateTimeOffset", structuralProperty: 1 },
                End: { typeName: "Edm.DateTimeOffset", structuralProperty: 1 },
            },
        };
    }
}

async function executeInModelDrivenApp(request: ExecuteRequest): Promise<ExecuteResponse> {
    const host = globalThis as unknown as { Xrm?: { WebApi?: { online?: { execute?: CalendarExecutor } } } };
    const online = host.Xrm?.WebApi?.online;
    if (!online?.execute) throw new Error("Calendar expansion requires an online model-driven app");
    return online.execute(request);
}

export class CalendarExpansionService {
    private readonly cache = new Map<string, Promise<CalendarTimeBlockDto[]>>();

    public constructor(private readonly execute: CalendarExecutor = executeInModelDrivenApp) {}

    public clear(): void {
        this.cache.clear();
    }

    public async expand(calendarId: string, range: DateRange): Promise<CalendarTimeBlockDto[]> {
        if (!Number.isFinite(+range.start) || !Number.isFinite(+range.end) || range.end <= range.start) throw new Error("Invalid expansion range");
        const request = new ExpandCalendarRequest(calendarId, range);
        const key = `${request.entity.id}:${request.Start}:${request.End}`;
        const cached = this.cache.get(key);
        if (cached) return cached;
        const pending = this.retrieve(request);
        const oldestKey = this.cache.keys().next().value;
        if (this.cache.size >= 12 && oldestKey !== undefined) this.cache.delete(oldestKey);
        this.cache.set(key, pending);
        try {
            return await pending;
        } catch (error: unknown) {
            if (this.cache.get(key) === pending) this.cache.delete(key);
            throw error;
        }
    }

    private async retrieve(request: ExpandCalendarRequest): Promise<CalendarTimeBlockDto[]> {
        const response = await this.execute(request);
        if (!response.ok) throw new Error("Dataverse calendar expansion failed");
        const body = await response.json();
        if (!body || typeof body !== "object" || !("result" in body) || !Array.isArray(body.result)) {
            throw new Error("Invalid calendar expansion response");
        }
        return (body.result as unknown[]).map((value) => {
            if (!value || typeof value !== "object") throw new Error("Invalid calendar time block");
            const row = value as Record<string, unknown>;
            if (typeof row.Start !== "string" || typeof row.End !== "string") throw new Error("Invalid calendar time block dates");
            return {
                Start: row.Start, End: row.End,
                TimeCode: typeof row.TimeCode === "string" || typeof row.TimeCode === "number" ? row.TimeCode : undefined,
                SubCode: typeof row.SubCode === "string" || typeof row.SubCode === "number" ? row.SubCode : undefined,
                CalendarId: typeof row.CalendarId === "string" ? row.CalendarId : undefined,
                SourceId: typeof row.SourceId === "string" ? row.SourceId : undefined,
                SourceTypeCode: typeof row.SourceTypeCode === "number" ? row.SourceTypeCode : undefined,
                DisplayText: typeof row.DisplayText === "string" ? row.DisplayText : undefined,
                Effort: typeof row.Effort === "number" && Number.isFinite(row.Effort) ? row.Effort : undefined,
                IsActivity: typeof row.IsActivity === "boolean" ? row.IsActivity : undefined,
                ActivityStatusCode: typeof row.ActivityStatusCode === "number" ? row.ActivityStatusCode : undefined,
            };
        });
    }
}