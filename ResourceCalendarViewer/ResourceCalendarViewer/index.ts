import * as React from "react";
import { Theme, webLightTheme } from "@fluentui/react-components";
import { IInputs, IOutputs } from "./generated/ManifestTypes";
import { ResourceCalendarDashboard } from "./components/ResourceCalendarDashboard";
import { ResourceCalendarService } from "./services/ResourceCalendarService";
import { CalendarExpansionService } from "./services/CalendarExpansionService";

export class ResourceCalendarViewer implements ComponentFramework.ReactControl<IInputs, IOutputs> {
    private service: ResourceCalendarService;
    private expansionService: CalendarExpansionService;

    public init(context: ComponentFramework.Context<IInputs>): void {
        context.mode.trackContainerResize(true);
        this.service = new ResourceCalendarService(context.webAPI);
        this.expansionService = new CalendarExpansionService();
    }

    public updateView(context: ComponentFramework.Context<IInputs>): React.ReactElement {
        return React.createElement(ResourceCalendarDashboard, {
            resourceId: (context.parameters.resourceId.raw ?? "").replace(/[{}]/g, ""),
            service: this.service,
            expansionService: this.expansionService,
            getString: (key: string) => context.resources.getString(key),
            locale: (context.userSettings.languageId & 1023) === 10 ? "es-ES" : "en-US",
            disabled: context.mode.isControlDisabled || context.parameters.hostValue.security?.editable === false,
            masked: context.parameters.hostValue.security?.readable === false,
            theme: (context.fluentDesignLanguage?.tokenTheme as Theme | undefined) ?? webLightTheme,
            width: context.mode.allocatedWidth,
        });
    }

    public getOutputs(): IOutputs {
        return {};
    }

    public destroy(): void {
        this.expansionService.clear();
    }
}
