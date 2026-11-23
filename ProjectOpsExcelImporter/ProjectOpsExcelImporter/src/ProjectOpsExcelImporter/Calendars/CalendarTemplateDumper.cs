using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter.Calendars;

/// <summary>
/// Solo lectura: vuelca una plantilla msdyn_workhourtemplate, el calendar al que apunta su msdyn_calendarid
/// y las calendarrules (siguiendo innercalendarid), para documentar el formato real creado por la UI.
/// </summary>
public sealed class CalendarTemplateDumper(DataverseClient client, TextWriter output)
{
    private const int MaxDepth = 4;

    public async Task<JsonObject> DumpAsync(Guid templateId, CancellationToken ct)
    {
        var result = new JsonObject();
        var template = await client.GetAsync($"msdyn_workhourtemplates({templateId:D})", ct).ConfigureAwait(false);
        result["Template"] = template;
        output.WriteLine($"Plantilla: {template["msdyn_name"]} | msdyn_calendarid={template["msdyn_calendarid"]}");

        var calendarIdText = template["msdyn_calendarid"]?.GetValue<string>();
        if (!Guid.TryParse(calendarIdText, out var calendarId))
        {
            output.WriteLine("La plantilla no tiene un msdyn_calendarid que sea un GUID.");
            return result;
        }
        var visited = new HashSet<Guid>();
        result["Calendar"] = await DumpCalendarAsync(calendarId, 0, visited, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Solo lectura: vuelca un calendar por id con sus reglas (y calendarios internos).</summary>
    public async Task<JsonObject> DumpCalendarByIdAsync(Guid calendarId, CancellationToken ct)
        => new() { ["Calendar"] = await DumpCalendarAsync(calendarId, 0, [], ct).ConfigureAwait(false) };

    /// <summary>Solo lectura: vuelca un proyecto (plantilla enlazada y calendario propio) y las reglas de su calendario.</summary>
    public async Task<JsonObject> DumpProjectAsync(Guid projectId, CancellationToken ct)
    {
        var result = new JsonObject();
        var project = await client.GetAsync($"msdyn_projects({projectId:D})?$select=msdyn_subject,msdyn_calendarid,_msdyn_workhourtemplate_value", ct).ConfigureAwait(false);
        result["Project"] = project;
        output.WriteLine($"Proyecto: {project["msdyn_subject"]} | msdyn_calendarid={project["msdyn_calendarid"]} | plantilla={project["_msdyn_workhourtemplate_value"]}");
        if (Guid.TryParse(project["msdyn_calendarid"]?.GetValue<string>(), out var calendarId))
            result["Calendar"] = await DumpCalendarAsync(calendarId, 0, [], ct).ConfigureAwait(false);
        else output.WriteLine("El proyecto no tiene (todavía) un msdyn_calendarid que sea un GUID.");
        return result;
    }

    private async Task<JsonNode> DumpCalendarAsync(Guid calendarId, int depth, HashSet<Guid> visited, CancellationToken ct)
    {
        var node = new JsonObject { ["CalendarId"] = calendarId.ToString("D") };
        if (!visited.Add(calendarId) || depth > MaxDepth) { node["Skipped"] = "ya visitado o profundidad máxima"; return node; }
        JsonObject calendar;
        try
        {
            // calendarrule no admite RetrieveMultiple: las reglas se obtienen expandiendo la relación del calendario.
            calendar = await client.GetAsync($"calendars({calendarId:D})?$expand=calendar_calendar_rules", ct).ConfigureAwait(false);
        }
        catch (Exception ex) { node["CalendarError"] = ex.Message; return node; }

        var ruleArray = calendar["calendar_calendar_rules"] as JsonArray ?? [];
        calendar.Remove("calendar_calendar_rules");
        node["Calendar"] = calendar;
        output.WriteLine($"  calendar {calendarId:D}: {ruleArray.Count} calendarrules");
        var rulesOut = new JsonArray();
        foreach (var rule in ruleArray)
        {
            var copy = rule!.DeepClone().AsObject();
            var inner = rule["_innercalendarid_value"]?.GetValue<string>();
            if (Guid.TryParse(inner, out var innerId))
                copy["InnerCalendar"] = await DumpCalendarAsync(innerId, depth + 1, visited, ct).ConfigureAwait(false);
            rulesOut.Add(copy);
        }
        node["Rules"] = rulesOut;
        return node;
    }
}
