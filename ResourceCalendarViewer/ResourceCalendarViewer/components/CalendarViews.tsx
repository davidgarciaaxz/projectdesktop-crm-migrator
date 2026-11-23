import * as React from "react";
import { Badge, ProgressBar, Tooltip, mergeClasses, tokens } from "@fluentui/react-components";
import {
    CalendarRegular, ClockRegular, AirplaneRegular, PersonProhibitedRegular, PauseRegular,
    CalendarCancelRegular, WarningRegular, QuestionCircleRegular,
} from "@fluentui/react-icons";
import { DateRange, GetString, ResourceCalendarEvent, ResourceCalendarEventType } from "../models";
import { addDays, dailyMetrics, datesInRange, overlaps, today } from "../services/calendarAnalytics";
import { useCalendarStyles } from "./calendarStyles";

export const eventIcons: Record<ResourceCalendarEventType, React.ElementType> = {
    working: ClockRegular, break: PauseRegular, vacation: AirplaneRegular, absence: PersonProhibitedRegular,
    nonWorking: CalendarRegular, businessClosure: CalendarCancelRegular, exception: WarningRegular, unknown: QuestionCircleRegular,
};

export function formatDate(date: Date, locale: string, options: Intl.DateTimeFormatOptions = {}): string {
    return new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", timeZone: "UTC", ...options }).format(date);
}

export function formatNumber(value: number, locale: string): string {
    return new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(value);
}

export function eventLabel(event: ResourceCalendarEvent, getString: GetString, locale: string): string {
    const time = (date: Date): string => formatDate(date, locale, { hour: "2-digit", minute: "2-digit" });
    return `${getString(event.type)}: ${event.title} · ${time(event.start)} – ${time(event.end)} UTC · ${formatNumber(event.durationMinutes / 60, locale)} h`;
}

interface ViewProps {
    events: ResourceCalendarEvent[];
    range: DateRange;
    locale: string;
    getString: GetString;
    disabled: boolean;
    onEvent: (event: ResourceCalendarEvent) => void;
    selectedDate: Date;
    onDay: (date: Date) => void;
}

export function MonthView({ events, range, locale, getString, disabled, onDay, selectedDate }: ViewProps): React.ReactElement {
    const styles = useCalendarStyles();
    const first = addDays(range.start, -(range.start.getUTCDay() + 6) % 7);
    const cellCount = Math.ceil(((range.start.getUTCDay() + 6) % 7 + datesInRange(range).length) / 7) * 7;
    const days = dailyMetrics(events, { start: first, end: addDays(first, cellCount) });
    return <div className={styles.scroll}><div className={styles.month}>
        {Array.from({ length: 7 }, (_, index) => <div key={index} className={styles.weekday}>
            {formatDate(addDays(first, index), locale, { weekday: "short", day: undefined, month: undefined })}
        </div>)}
        {days.map(({ date, metrics }) => {
            const inMonth = date >= range.start && date < range.end;
            const dayEvents = events.filter((event) => overlaps(event, { start: date, end: addDays(date, 1) }));
            const known = dayEvents.some((event) => event.type !== "unknown");
            const types = [...new Set(dayEvents.map((event) => event.type))];
            const label = `${formatDate(date, locale)} · ${getString("availability")}: ${metrics.availability === undefined ? getString("notAvailable") : `${formatNumber(metrics.availability, locale)}%`}`;
            return <button key={date.toISOString()} type="button" disabled={disabled || !inMonth} onClick={() => onDay(date)} aria-label={label}
                className={mergeClasses(styles.day, !inMonth && styles.outside, +date === +today() && styles.today,
                    +date === +selectedDate && styles.selectedDay)}>
                <strong>{date.getUTCDate()}</strong>
                <span>{getString("working")}: {known ? formatNumber(metrics.hours.working, locale) : "—"} h</span>
                <span>{getString("vacation")}: {known ? formatNumber(metrics.hours.vacation, locale) : "—"} h</span>
                <span>{getString("absence")}: {known ? formatNumber(metrics.hours.absence, locale) : "—"} h</span>
                <span>{metrics.availability === undefined ? "—" : `${formatNumber(metrics.availability, locale)}%`}</span>
                <ProgressBar value={(metrics.availability ?? 0) / 100} aria-label={getString("availability")} />
                <div className={styles.row}>{types.map((type) => {
                    const Icon = eventIcons[type];
                    return <Tooltip key={type} content={getString(type)} relationship="label"><span className={styles[type]}><Icon /></span></Tooltip>;
                })}</div>
            </button>;
        })}
    </div></div>;
}

interface LayoutEvent {
    event: ResourceCalendarEvent;
    start: number;
    end: number;
    lane: number;
}

function layoutEvents(events: ResourceCalendarEvent[], day: Date): { items: LayoutEvent[]; lanes: number } {
    const end = addDays(day, 1);
    const sorted = events.filter((event) => overlaps(event, { start: day, end }))
        .map((event) => ({ event, start: Math.max(+event.start, +day), end: Math.min(+event.end, +end), lane: 0 }))
        .sort((left, right) => left.start - right.start || right.end - left.end);
    const lanes: number[] = [];
    for (const item of sorted) {
        let lane = lanes.findIndex((laneEnd) => laneEnd <= item.start);
        if (lane < 0) lane = lanes.length;
        item.lane = lane;
        lanes[lane] = item.end;
    }
    return { items: sorted, lanes: Math.max(1, lanes.length) };
}

export function WeekView({ events, range, locale, getString, disabled, onEvent }: ViewProps): React.ReactElement {
    const styles = useCalendarStyles();
    const days = datesInRange(range);
    return <div className={styles.scroll}><div className={styles.week}>
        <span className={styles.weekday}>UTC</span>
        {days.map((day) => <div key={+day} className={mergeClasses(styles.weekday, +day === +today() && styles.today)}>
            {formatDate(day, locale, { weekday: "short", day: "numeric", month: undefined })}
        </div>)}
        <div className={styles.weekColumn}>
            {Array.from({ length: 24 }, (_, hour) => <span key={hour} className={styles.timeLabel} style={{ top: `${hour / 24 * 100}%` }}>
                {String(hour).padStart(2, "0")}:00
            </span>)}
        </div>
        {days.map((day) => {
            const { items, lanes } = layoutEvents(events, day);
            return <div key={+day} className={styles.weekColumn}>
                {Array.from({ length: 24 }, (_, hour) => <div key={hour} className={styles.hourLine} style={{ top: `${hour / 24 * 100}%` }} />)}
                {items.map(({ event, start, end, lane }) => {
                    const Icon = eventIcons[event.type];
                    const label = eventLabel(event, getString, locale);
                    return <Tooltip key={event.id} content={label} relationship="description">
                        <button type="button" disabled={disabled} onClick={() => onEvent(event)} aria-label={label}
                            className={mergeClasses(styles.block, styles[event.type])}
                            style={{ top: `${(start - +day) / 86400000 * 100}%`, height: `${(end - start) / 86400000 * 100}%`,
                                left: `${lane / lanes * 100}%`, width: `${100 / lanes}%` }}>
                            <span><Icon /> {getString(event.type)}</span>
                            {(end - start) >= 3600000 && <>
                                <span>{formatDate(new Date(start), locale, { hour: "2-digit", minute: "2-digit", day: undefined, month: undefined })}
                                    {" – "}{formatDate(new Date(end), locale, { hour: "2-digit", minute: "2-digit", day: undefined, month: undefined })}</span>
                                <span>{formatNumber((end - start) / 3600000, locale)} h</span>
                            </>}
                        </button>
                    </Tooltip>;
                })}
            </div>;
        })}
    </div></div>;
}

export function TimelineView({ events, range, locale, getString, disabled, onEvent }: ViewProps): React.ReactElement {
    const styles = useCalendarStyles();
    return <div className={styles.scroll}><div className={styles.timeline}>
        <div className={styles.timelineRow}><span>UTC</span><div className={styles.header}><span>00:00</span><span>12:00</span><span>24:00</span></div></div>
        {datesInRange(range).map((day) => {
            const { items, lanes } = layoutEvents(events, day);
            return <div key={+day} className={styles.timelineRow}>
                <span className={styles.muted}>{formatDate(day, locale)}</span>
                <div className={styles.timelineTrack} style={{ height: `calc(${lanes} * ${tokens.spacingVerticalXXXL})` }}>
                    {items.map(({ event, start, end, lane }) => {
                        const Icon = eventIcons[event.type];
                        const label = eventLabel(event, getString, locale);
                        return <Tooltip key={event.id} content={label} relationship="description">
                            <button type="button" disabled={disabled} onClick={() => onEvent(event)} aria-label={label}
                                className={mergeClasses(styles.block, styles[event.type])}
                                style={{ left: `${(start - +day) / 86400000 * 100}%`, width: `${(end - start) / 86400000 * 100}%`,
                                    top: `${lane / lanes * 100}%`, height: `${100 / lanes}%` }}>
                                <span><Icon /> {getString(event.type)}</span>
                            </button>
                        </Tooltip>;
                    })}
                </div>
            </div>;
        })}
    </div></div>;
}

export function CalendarLegend({ getString }: { getString: GetString }): React.ReactElement {
    const styles = useCalendarStyles();
    return <div className={styles.row}>{Object.entries(eventIcons).map(([type, Icon]) =>
        <Badge key={type} appearance="outline" className={styles[type as ResourceCalendarEventType]} icon={<Icon />}>
            {getString(type)}
        </Badge>)}</div>;
}