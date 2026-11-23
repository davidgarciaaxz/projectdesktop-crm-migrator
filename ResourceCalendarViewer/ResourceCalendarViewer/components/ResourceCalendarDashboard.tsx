import * as React from "react";
import {
    Avatar, Badge, Button, Card, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle, FluentProvider, Input, MessageBar,
    MessageBarActions, MessageBarBody, Skeleton, SkeletonItem, Tab, TabList, Theme, Tooltip, mergeClasses, tokens,
} from "@fluentui/react-components";
import { ArrowClockwiseRegular, CalendarTodayRegular, ChevronLeftRegular, ChevronRightRegular, DismissRegular } from "@fluentui/react-icons";
import {
    CalendarNormalizationResult, CalendarRuleNode, CalendarViewMode, GetString, ResourceCalendarData, ResourceCalendarEvent, ResourceCalendarEventType,
} from "../models";
import { ResourceCalendarService } from "../services/ResourceCalendarService";
import { CalendarRuleNormalizer } from "../services/CalendarRuleNormalizer";
import { CalendarExpansionService } from "../services/CalendarExpansionService";
import {
    addDays, calculateMetrics, dailyMetrics, longestVacation, movePeriod, overlaps, today, visibleRange,
} from "../services/calendarAnalytics";
import { CalendarLegend, eventIcons, eventLabel, formatDate, formatNumber, MonthView, TimelineView, WeekView } from "./CalendarViews";
import { useCalendarStyles } from "./calendarStyles";

export interface DashboardProps {
    resourceId: string;
    service: ResourceCalendarService;
    expansionService: CalendarExpansionService;
    getString: GetString;
    locale: string;
    disabled: boolean;
    masked: boolean;
    theme: Theme;
    width: number;
}

interface LoadState {
    id: string;
    status: "loading" | "loaded" | "error";
    revision: number;
    data?: ResourceCalendarData;
}

interface ExpansionState {
    key: string;
    status: "loading" | "loaded" | "error";
    result?: CalendarNormalizationResult;
}

type Selection = { event: ResourceCalendarEvent } | { node: CalendarRuleNode };

function flattenRules(nodes: CalendarRuleNode[]): CalendarRuleNode[] {
    return nodes.flatMap((node) => [node, ...flattenRules(node.children)]);
}

function safeImage(value?: string): string | undefined {
    if (!value) return undefined;
    if (/^https:\/\//i.test(value) || /^\/(?!\/)/.test(value) || /^data:image\/(png|jpeg|webp);base64,/i.test(value)) return value;
    if (/^[A-Za-z0-9+/=]+$/.test(value)) return `data:image/png;base64,${value}`;
    return undefined;
}

export function ResourceCalendarDashboard(props: DashboardProps): React.ReactElement {
    return <FluentProvider theme={props.theme} style={{ minWidth: 0, width: "100%" }}>
        <DashboardContent {...props} />
    </FluentProvider>;
}

function DashboardContent({ resourceId, service, expansionService, getString, locale, disabled, masked, width }: DashboardProps): React.ReactElement {
    const styles = useCalendarStyles();
    const [selectedDate, setSelectedDate] = React.useState(today);
    const [viewMode, setViewMode] = React.useState<CalendarViewMode>("month");
    const [periodMode, setPeriodMode] = React.useState<"week" | "month">("month");
    const [refresh, setRefresh] = React.useState(0);
    const [load, setLoad] = React.useState<LoadState>({ id: "", revision: 0, status: "loading" });
    const [expansion, setExpansion] = React.useState<ExpansionState>({ key: "", status: "loading" });
    const [selection, setSelection] = React.useState<Selection>();
    const range = visibleRange(selectedDate, periodMode);
    const visibleStartDate = range.start;
    const visibleEndDate = range.end;
    const startTime = +visibleStartDate;
    const endTime = +visibleEndDate;
    const loadedData = load.id === resourceId && load.revision === refresh && load.status === "loaded" ? load.data : undefined;
    const expansionKey = `${resourceId}:${loadedData?.calendar?.calendarid ?? ""}:${startTime}:${endTime}:${refresh}`;
    const needsExpansion = Boolean(loadedData?.calendar && loadedData.tree.length > 0);

    React.useEffect(() => {
        if (!resourceId || masked) return;
        let active = true;
        const loadData = async (): Promise<void> => {
            expansionService.clear();
            setLoad({ id: resourceId, revision: refresh, status: "loading" });
            setSelection(undefined);
            try {
                const data = await service.load(resourceId);
                if (active) setLoad({ id: resourceId, revision: refresh, status: "loaded", data });
            } catch {
                if (active) setLoad({ id: resourceId, revision: refresh, status: "error" });
            }
        };
        void loadData();
        return () => { active = false; };
    }, [resourceId, service, expansionService, refresh, masked]);

    React.useEffect(() => {
        if (!loadedData?.calendar || !needsExpansion || masked) return;
        let active = true;
        const data = loadedData;
        const calendarId = loadedData.calendar.calendarid;
        const expand = async (): Promise<void> => {
            setExpansion({ key: expansionKey, status: "loading" });
            setSelection(undefined);
            try {
                const requestedRange = { start: new Date(startTime), end: new Date(endTime) };
                const blocks = await expansionService.expand(calendarId, requestedRange);
                const result = new CalendarRuleNormalizer().normalizeExpanded(blocks, data.tree, requestedRange, calendarId);
                if (active) setExpansion({ key: expansionKey, status: "loaded", result });
            } catch {
                if (active) setExpansion({ key: expansionKey, status: "error" });
            }
        };
        void expand();
        return () => { active = false; };
    }, [loadedData, needsExpansion, expansionService, expansionKey, startTime, endTime, masked]);

    const iconButton = (key: string, icon: React.ReactElement, action: () => void): React.ReactElement =>
        <Tooltip content={getString(key)} relationship="label">
            <Button icon={icon} aria-label={getString(key)} onClick={action} disabled={disabled} />
        </Tooltip>;

    if (masked) return <div className={styles.root} aria-label={getString("masked")}>********</div>;
    if (!resourceId) return <div className={styles.root}><MessageBar intent="info"><MessageBarBody>{getString("saveResource")}</MessageBarBody></MessageBar></div>;
    const expansionPending = needsExpansion && (expansion.key !== expansionKey || expansion.status === "loading");
    if (load.id !== resourceId || load.revision !== refresh || load.status === "loading" || expansionPending) {
        return <div className={styles.root} aria-busy="true" aria-label={getString("loading")}>
        <Skeleton className={styles.skeleton}><SkeletonItem size={48} shape="rectangle" /><div className={styles.kpis}>
            {Array.from({ length: 8 }, (_, index) => <SkeletonItem key={index} size={64} shape="rectangle" />)}
        </div><SkeletonItem size={128} shape="rectangle" /></Skeleton>
        </div>;
    }
    if (load.status === "error" || !load.data) return <div className={styles.root}><MessageBar intent="error">
        <MessageBarBody>{getString("loadError")}</MessageBarBody>
        <MessageBarActions><Button onClick={() => setRefresh(refresh + 1)} disabled={disabled}>{getString("retry")}</Button></MessageBarActions>
    </MessageBar></div>;
    if (needsExpansion && expansion.status === "error") return <div className={styles.root}><MessageBar intent="error">
        <MessageBarBody>{getString("expansionError")}</MessageBarBody>
        <MessageBarActions><Button onClick={() => setRefresh(refresh + 1)} disabled={disabled}>{getString("retry")}</Button></MessageBarActions>
    </MessageBar></div>;

    const data = load.data;
    const normalized: CalendarNormalizationResult = expansion.result ?? { events: [], warnings: [], unplacedRules: [], discardedDuplicates: 0 };
    const events = normalized.events.filter((event) => overlaps(event, range));
    const warnings = [...data.warnings, ...normalized.warnings];
    const partial = normalized.warnings.some((warning) => warning.code !== "duplicate");
    const rules = flattenRules(data.tree);
    const knownEvents = normalized.warnings.length === 0 || events.some((event) => event.type !== "unknown");
    const metrics = calculateMetrics(events, range);
    const days = dailyMetrics(events, range);
    const availableDays = days.filter((day) => day.metrics.availability !== undefined)
        .sort((left, right) => (right.metrics.availability ?? 0) - (left.metrics.availability ?? 0)
            || right.metrics.hours.working - left.metrics.hours.working);
    const mostAvailable = availableDays[0];
    const leastAvailable = availableDays[availableDays.length - 1];
    const future = events.filter((event) => event.start >= new Date());
    const nextVacation = future.find((event) => event.type === "vacation");
    const nextAbsence = future.find((event) => event.type === "absence");
    const countLabel = (value: number): string => knownEvents ? formatNumber(value, locale) : "—";
    const kpis: { key: string; value: number; type: ResourceCalendarEventType; hours?: boolean }[] = [
        { key: "workingHours", value: metrics.hours.working, type: "working", hours: true },
        { key: "vacationHours", value: metrics.hours.vacation, type: "vacation", hours: true },
        { key: "absenceHours", value: metrics.hours.absence, type: "absence", hours: true },
        { key: "breakHours", value: metrics.hours.break, type: "break", hours: true },
        { key: "nonWorkingHours", value: metrics.hours.nonWorking, type: "nonWorking", hours: true },
        { key: "workingDays", value: metrics.workingDays, type: "working" },
        { key: "vacationDays", value: metrics.vacationDays, type: "vacation" },
        { key: "exceptions", value: metrics.exceptions, type: "exception" },
    ];
    const switchView = (value: unknown): void => {
        if (value !== "week" && value !== "month" && value !== "timeline") return;
        setViewMode(value);
        if (value !== "timeline") setPeriodMode(value);
    };
    const viewProps = {
        events, range, locale, getString, disabled, selectedDate,
        onEvent: (event: ResourceCalendarEvent): void => setSelection({ event }),
        onDay: (date: Date): void => { setSelectedDate(date); setViewMode("week"); setPeriodMode("week"); },
    };

    return <div className={styles.root} style={width > 0 ? { maxWidth: width, boxSizing: "border-box" } : undefined}>
        <header className={styles.header}>
            <div className={styles.identity}>
                <Avatar name={data.resource.name} image={safeImage(data.resource.axz_resourceimage) ? { src: safeImage(data.resource.axz_resourceimage) } : undefined} size={56} />
                <div className={styles.stack}>
                    <span className={styles.name}>{data.resource.name || getString("resource")}</span>
                    <span className={styles.muted}>{getString("timezone")}: {data.resource.timezone ?? "—"} · {getString("utcDisplay")}</span>
                    <span>{formatDate(visibleStartDate, locale)} – {formatDate(addDays(visibleEndDate, -1), locale, { year: "numeric" })}</span>
                </div>
            </div>
            <div className={styles.toolbar}>
                {iconButton("previous", <ChevronLeftRegular />, () => setSelectedDate(movePeriod(selectedDate, periodMode, -1)))}
                {iconButton("today", <CalendarTodayRegular />, () => setSelectedDate(today()))}
                {iconButton("next", <ChevronRightRegular />, () => setSelectedDate(movePeriod(selectedDate, periodMode, 1)))}
                {iconButton("refresh", <ArrowClockwiseRegular />, () => setRefresh(refresh + 1))}
                <Input type="date" value={selectedDate.toISOString().slice(0, 10)} disabled={disabled} aria-label={getString("chooseDate")}
                    onChange={(_, input) => {
                        const date = new Date(`${input.value}T00:00:00Z`);
                        if (Number.isFinite(date.getTime())) setSelectedDate(date);
                    }} />
            </div>
        </header>
        <TabList selectedValue={viewMode} onTabSelect={(_, tab) => switchView(tab.value)} aria-label={getString("view")}>
            <Tab value="week" disabled={disabled}>{getString("week")}</Tab>
            <Tab value="month" disabled={disabled}>{getString("month")}</Tab>
            <Tab value="timeline" disabled={disabled}>{getString("timeline")}</Tab>
        </TabList>
        {!data.calendar ? <MessageBar intent="info"><MessageBarBody>{getString("noCalendar")}</MessageBarBody></MessageBar>
            : rules.length === 0 ? <MessageBar intent="info"><MessageBarBody>{getString("noRules")}</MessageBarBody></MessageBar> : <>
                {partial && <MessageBar intent="warning"><MessageBarBody>{getString("partialData")}</MessageBarBody></MessageBar>}
                <div className={styles.kpis}>{kpis.map((kpi) => {
                    const Icon = eventIcons[kpi.type];
                    return <Card key={kpi.key} className={styles.kpi}>
                        <span className={styles.row}><Icon /><span className={styles.muted}>{getString(kpi.key)}</span></span>
                        <span className={styles.value}>{countLabel(kpi.value)}{kpi.hours && knownEvents ? " h" : ""}</span>
                        {partial && <span className={styles.muted}>{getString("partial")}</span>}
                    </Card>;
                })}</div>
                <section className={styles.section}>
                    <div className={styles.header}>
                        <h2 className={styles.sectionHeading}>{getString("availability")}</h2>
                        <Badge appearance="tint">{metrics.availability === undefined ? getString("notAvailable") : `${formatNumber(metrics.availability, locale)}%`}</Badge>
                    </div>
                    <span className={styles.muted}>{getString("availabilityFormula")}</span>
                    <CalendarLegend getString={getString} />
                    {viewMode === "month" ? <MonthView {...viewProps} /> : viewMode === "week" ? <WeekView {...viewProps} /> : <TimelineView {...viewProps} />}
                    {events.length === 0 && <MessageBar intent="info"><MessageBarBody>{getString("noIntervals")}</MessageBarBody></MessageBar>}
                </section>
                <section className={styles.chartGrid}>
                    <div className={styles.section}><h2 className={styles.sectionHeading}>{getString("distribution")}</h2>
                        <AvailabilityDonut hours={[metrics.hours.working, metrics.hours.vacation, metrics.hours.absence]} getString={getString} locale={locale} />
                    </div>
                    <div className={styles.section}><h2 className={styles.sectionHeading}>{getString("dailyWorking")}</h2>
                        <div className={styles.bars}>{days.map(({ date, metrics: day }) => {
                            const label = `${formatDate(date, locale)}: ${formatNumber(day.hours.working, locale)} h`;
                            return <Tooltip key={+date} content={label} relationship="label"><div className={styles.barColumn} role="img" aria-label={label}>
                                <div className={styles.bar} style={{ height: `${day.hours.working / 24 * 100}%` }} />
                                <span className={styles.muted}>{date.getUTCDate()}</span>
                            </div></Tooltip>;
                        })}</div>
                    </div>
                    <div className={styles.section}><h2 className={styles.sectionHeading}>{getString("heatmap")}</h2>
                        <div className={styles.heat}>{days.map(({ date, metrics: day }) => {
                            const type = day.hours.vacation > 0 ? "vacation" : day.hours.absence > 0 ? "absence"
                                : day.availability === undefined ? "unknown" : day.availability >= 80 ? "working" : day.availability > 0 ? "break" : "nonWorking";
                            const label = `${formatDate(date, locale)}: ${getString(type)} · ${day.availability === undefined ? "—" : `${formatNumber(day.availability, locale)}%`}`;
                            return <Tooltip key={+date} content={label} relationship="label">
                                <span className={mergeClasses(styles.heatCell, styles[type])} role="img" aria-label={label}>
                                    {date.getUTCDate()} {React.createElement(eventIcons[type])}
                                </span>
                            </Tooltip>;
                        })}</div>
                    </div>
                </section>
                <section className={styles.section}><h2 className={styles.sectionHeading}>{getString("insights")}</h2>
                    <div className={styles.insights}>{[
                        ["nextVacation", nextVacation ? formatDate(nextVacation.start, locale) : "—"],
                        ["nextAbsence", nextAbsence ? formatDate(nextAbsence.start, locale) : "—"],
                        ["mostAvailable", mostAvailable ? formatDate(mostAvailable.date, locale) : "—"],
                        ["leastAvailable", leastAvailable ? formatDate(leastAvailable.date, locale) : "—"],
                        ["longestVacation", knownEvents ? `${formatNumber(longestVacation(events, range), locale)} h` : "—"],
                        ["averageWorking", knownEvents ? `${formatNumber(metrics.averageWorkingHours, locale)} h` : "—"],
                        ["exceptionDays", countLabel(metrics.exceptionDays)],
                        ["totalHours", `${formatNumber(metrics.totalHours, locale)} h`],
                        ["knownCoverage", `${formatNumber(metrics.knownCoverage, locale)}%`],
                    ].map(([key, value]) => <div key={key} className={styles.stack}><span className={styles.muted}>{getString(key)}</span><strong>{value}</strong></div>)}</div>
                </section>
                <details><summary>{getString("dataQuality")} ({warnings.length})</summary><div className={styles.details}>
                    <dl className={styles.definition}>{[
                        ["unknownRules", new Set(warnings.filter((warning) => warning.code === "unknownRule").map((warning) => warning.ruleId)).size],
                        ["invalidRules", warnings.filter((warning) => warning.code === "invalidRule" || warning.code === "invalidInterval").length],
                        ["nestedCalendars", data.nestedCalendarCount], ["duplicates", normalized.discardedDuplicates],
                    ].map(([key, value]) => <React.Fragment key={key}><dt>{getString(String(key))}</dt><dd className={styles.detailsValue}>{value}</dd></React.Fragment>)}</dl>
                    {[...new Set(warnings.map((warning) => warning.code))].map((code) => <span key={code}>{getString(`warning_${code}`)}</span>)}
                    <div className={styles.rules}>{rules.map((node, index) => <Button key={`${node.sourceCalendarId}:${node.rule.calendarruleid}:${index}`}
                        appearance="subtle" disabled={disabled} icon={React.createElement(eventIcons.unknown)} onClick={() => setSelection({ node })}>
                        {node.rule.name ?? getString("rule")} · {getString("depth")} {node.depth}
                    </Button>)}</div>
                </div></details>
            </>}
        <EventDrawer selection={selection} onClose={() => setSelection(undefined)} getString={getString} locale={locale} />
    </div>;
}

function AvailabilityDonut({ hours, getString, locale }: { hours: number[]; getString: GetString; locale: string }): React.ReactElement {
    const styles = useCalendarStyles();
    const total = hours.reduce((sum, value) => sum + value, 0);
    const colors = [tokens.colorPaletteGreenForeground1, tokens.colorPaletteBlueForeground2, tokens.colorPalettePurpleForeground2];
    const types = ["working", "vacation", "absence"];
    let offset = 0;
    return <div className={styles.row}><svg viewBox="0 0 120 120" className={styles.donut} role="img" aria-label={getString("distribution")}>
        <title>{getString("distribution")}</title>
        <circle cx="60" cy="60" r="44" fill="none" stroke={tokens.colorNeutralBackground3} strokeWidth="14" />
        {hours.map((value, index) => {
            const fraction = total > 0 ? value / total * 100 : 0;
            const dashOffset = -offset;
            offset += fraction;
            return <circle key={index} cx="60" cy="60" r="44" fill="none" stroke={colors[index]} strokeWidth="14" pathLength="100"
                strokeDasharray={`${fraction} ${100 - fraction}`} strokeDashoffset={dashOffset} transform="rotate(-90 60 60)">
                <title>{getString(types[index])}: {formatNumber(value, locale)} h</title>
            </circle>;
        })}
        <text x="60" y="64" textAnchor="middle" fill={tokens.colorNeutralForeground1} fontSize={tokens.fontSizeBase400}>
            {total > 0 ? `${formatNumber(hours[0] / total * 100, locale)}%` : "—"}
        </text>
    </svg><div className={styles.stack}>{types.map((type, index) => <span key={type}>
        {getString(type)}: {total > 0 ? formatNumber(hours[index], locale) : "—"} h
    </span>)}</div></div>;
}

function EventDrawer({ selection, onClose, getString, locale }: {
    selection?: Selection; onClose: () => void; getString: GetString; locale: string;
}): React.ReactElement {
    const styles = useCalendarStyles();
    const event = selection && "event" in selection ? selection.event : undefined;
    const node = selection && "node" in selection ? selection.node : undefined;
    const rule = event?.rawRule ?? node?.rule;
    const fields: [string, string | number | undefined][] = event || rule ? [
        ["type", event ? getString(event.type) : getString("unknown")], ["title", event?.title ?? rule?.name],
        ["start", event ? formatDate(event.start, locale, { hour: "2-digit", minute: "2-digit", year: "numeric" }) : rule?.starttime],
        ["end", event ? formatDate(event.end, locale, { hour: "2-digit", minute: "2-digit", year: "numeric" }) : rule?.endtime],
        ["duration", event ? `${formatNumber(event.durationMinutes, locale)} ${getString("minutes")}` : rule?.duration],
        ["capacity", event?.capacity ?? rule?.effort], ["pattern", rule?.pattern], ["timezone", rule?.timezonecode],
        ["sourceCalendar", event?.sourceCalendarId ?? node?.sourceCalendarId], ["sourceRule", rule?.calendarruleid],
        ["depth", event?.hierarchyDepth ?? node?.depth],
    ] : [];
    return <Drawer open={Boolean(selection)} onOpenChange={(_, state) => { if (!state.open) onClose(); }} position="end" size="medium">
        <DrawerHeader><DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label={getString("close")} onClick={onClose} />}>
            {getString("details")}
        </DrawerHeaderTitle></DrawerHeader>
        <DrawerBody><div className={styles.details}>
            {event && <span>{eventLabel(event, getString, locale)}</span>}
            <span className={styles.muted}>{getString("utcDisplay")}</span>
            <dl className={styles.definition}>{fields.map(([key, value]) => <React.Fragment key={key}>
                <dt>{getString(key)}</dt><dd className={styles.detailsValue}>{value ?? "—"}</dd>
            </React.Fragment>)}</dl>
            <details><summary>{getString("rawValues")}</summary><dl className={styles.definition}>
                {Object.entries({ ...rule, ...event?.rawTimeBlock }).map(([key, value]) => <React.Fragment key={key}>
                    <dt>{key}</dt><dd className={styles.detailsValue}>{value === undefined ? "—" : String(value)}</dd>
                </React.Fragment>)}
            </dl></details>
        </div></DrawerBody>
    </Drawer>;
}