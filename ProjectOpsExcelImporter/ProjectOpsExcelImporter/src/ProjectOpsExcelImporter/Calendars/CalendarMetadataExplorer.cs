using System.Text.Json.Nodes;
using System.Xml;

namespace ProjectOpsExcelImporter.Calendars;

/// <summary>
/// Comando calendar-metadata: SOLO LECTURA. Localiza y documenta los metadatos de Dataverse necesarios para
/// Calendar Template, calendario del proyecto, calendar/calendarrule, WorkWeeks y excepciones.
/// </summary>
public sealed class CalendarMetadataExplorer
{
    private static readonly string[] SearchTerms =
    [
        "calendar", "calendar template", "work hour", "work template", "project calendar", "calendar rule",
        "work week", "exception", "msdyn_calendar", "msdyn_project", "msdyn_workhour", "msdyn_worktemplate"
    ];
    // 'msdyn_project' solo coincide exacto (si no arrastraría todas las msdyn_project*).
    private static readonly string[] AlwaysInclude = ["msdyn_project", "calendar", "calendarrule"];
    private static readonly string[] ActionTerms = ["calendar", "workhour", "work_hour", "worktemplate", "workweek"];

    private readonly DataverseClient _client;
    private readonly TextWriter _out;
    private readonly List<string> _unresolved = [];

    public CalendarMetadataExplorer(DataverseClient client, TextWriter output)
    {
        _client = client;
        _out = output;
    }

    public async Task<JsonObject> ExploreAsync(string environmentUrl, CancellationToken ct)
    {
        _out.WriteLine("Reading Dataverse metadata (read-only)...");
        var all = await _client.GetAsync("EntityDefinitions?$select=LogicalName,SchemaName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,IsCustomEntity,IsActivity,OwnershipType,MetadataId,DisplayName", ct).ConfigureAwait(false);
        var definitions = all["value"]?.AsArray() ?? [];
        var candidates = definitions.Select(d => d!.AsObject()).Where(IsCandidate).ToList();
        _out.WriteLine($"  {definitions.Count} entities, {candidates.Count} candidates.");

        var actions = await ReadActionsAsync(ct).ConfigureAwait(false);
        var entities = new JsonArray();
        foreach (var def in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var logical = def["LogicalName"]!.GetValue<string>();
            _out.WriteLine($"  - {logical}");
            entities.Add(new JsonObject
            {
                ["LogicalName"] = logical,
                ["SchemaName"] = def["SchemaName"]?.DeepClone(),
                ["EntitySetName"] = def["EntitySetName"]?.DeepClone(),
                ["PrimaryIdAttribute"] = def["PrimaryIdAttribute"]?.DeepClone(),
                ["PrimaryNameAttribute"] = def["PrimaryNameAttribute"]?.DeepClone(),
                ["IsCustomEntity"] = def["IsCustomEntity"]?.DeepClone(),
                ["IsActivity"] = def["IsActivity"]?.DeepClone(),
                ["OwnershipType"] = def["OwnershipType"]?.DeepClone(),
                ["MetadataId"] = def["MetadataId"]?.DeepClone(),
                ["DisplayNames"] = Labels(def),
                ["Attributes"] = await ReadAttributesAsync(logical, ct).ConfigureAwait(false),
                ["ManyToOneRelationships"] = await ReadRelationshipsAsync(logical, "ManyToOneRelationships", ct).ConfigureAwait(false),
                ["OneToManyRelationships"] = await ReadRelationshipsAsync(logical, "OneToManyRelationships", ct).ConfigureAwait(false),
                ["Actions"] = new JsonArray(actions.Where(a => ActionTargetsEntity(a!.AsObject(), logical)).Select(a => a!.DeepClone()).ToArray())
            });
        }

        return new JsonObject
        {
            ["GeneratedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["Environment"] = environmentUrl,
            ["SearchTerms"] = new JsonArray(SearchTerms.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["Summary"] = BuildSummary(entities, actions),
            ["Entities"] = entities,
            ["Actions"] = actions
        };
    }

    private static bool IsCandidate(JsonObject def)
    {
        var logical = def["LogicalName"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;
        if (AlwaysInclude.Contains(logical)) return true;
        var haystacks = new List<string> { logical, def["SchemaName"]?.GetValue<string>() ?? string.Empty, def["EntitySetName"]?.GetValue<string>() ?? string.Empty };
        haystacks.AddRange(Labels(def).Select(l => l!.GetValue<string>()));
        foreach (var raw in haystacks)
        {
            var h = raw.ToLowerInvariant();
            var compact = h.Replace(" ", string.Empty).Replace("_", string.Empty);
            foreach (var term in SearchTerms)
            {
                if (term == "msdyn_project") continue;
                if (h.Contains(term) || compact.Contains(term.Replace(" ", string.Empty).Replace("_", string.Empty))) return true;
            }
        }
        return false;
    }

    private static JsonArray Labels(JsonObject def)
    {
        var result = new JsonArray();
        if (def["DisplayName"]?["LocalizedLabels"] is JsonArray labels)
            foreach (var l in labels)
                if (l?["Label"]?.GetValue<string>() is { Length: > 0 } text) result.Add(text);
        return result;
    }

    private async Task<JsonArray> ReadAttributesAsync(string logical, CancellationToken ct)
    {
        var baseUrl = $"EntityDefinitions(LogicalName='{logical}')/Attributes";
        var attrs = await TryGetArrayAsync($"{baseUrl}?$select=LogicalName,SchemaName,AttributeType,IsPrimaryId,IsPrimaryName,IsCustomAttribute", $"{logical}.Attributes", ct).ConfigureAwait(false);
        var strings = (await TryGetArrayAsync($"{baseUrl}/Microsoft.Dynamics.CRM.StringAttributeMetadata?$select=LogicalName,MaxLength", $"{logical}.StringAttributes", ct).ConfigureAwait(false))
            .ToDictionary(a => a!["LogicalName"]!.GetValue<string>(), a => a!["MaxLength"]);
        var lookups = (await TryGetArrayAsync($"{baseUrl}/Microsoft.Dynamics.CRM.LookupAttributeMetadata?$select=LogicalName,Targets", $"{logical}.LookupAttributes", ct).ConfigureAwait(false))
            .ToDictionary(a => a!["LogicalName"]!.GetValue<string>(), a => a!["Targets"]);
        var result = new JsonArray();
        foreach (var a in attrs)
        {
            var name = a!["LogicalName"]!.GetValue<string>();
            result.Add(new JsonObject
            {
                ["LogicalName"] = name,
                ["SchemaName"] = a["SchemaName"]?.DeepClone(),
                ["AttributeType"] = a["AttributeType"]?.DeepClone(),
                ["IsPrimaryId"] = a["IsPrimaryId"]?.DeepClone(),
                ["IsPrimaryName"] = a["IsPrimaryName"]?.DeepClone(),
                ["IsCustomAttribute"] = a["IsCustomAttribute"]?.DeepClone(),
                ["MaxLength"] = strings.TryGetValue(name, out var m) ? m?.DeepClone() : null,
                ["LookupTargets"] = lookups.TryGetValue(name, out var t) ? t?.DeepClone() : null
            });
        }
        return result;
    }

    private Task<JsonArray> ReadRelationshipsAsync(string logical, string kind, CancellationToken ct)
        => TryGetArrayAsync($"EntityDefinitions(LogicalName='{logical}')/{kind}?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity,ReferencedAttribute,ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName",
            $"{logical}.{kind}", ct);

    private async Task<JsonArray> TryGetArrayAsync(string url, string what, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
            return response["value"]?.AsArray() is { } arr ? new JsonArray(arr.Select(x => x!.DeepClone()).ToArray()) : [];
        }
        catch (HttpRequestException ex)
        {
            _unresolved.Add($"{what}: {Truncate(ex.Message, 300)}");
            return [];
        }
    }

    /// <summary>Acciones/funciones del $metadata cuyo nombre o parámetros referencian calendarios (lectura en streaming).</summary>
    private async Task<JsonArray> ReadActionsAsync(CancellationToken ct)
    {
        var result = new JsonArray();
        try
        {
            var xml = await _client.GetRawAsync("$metadata", "application/xml", ct).ConfigureAwait(false);
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            JsonObject? current = null;
            var matches = false;
            while (reader.Read())
            {
                var isOp = reader.LocalName is "Action" or "Function";
                if (reader.NodeType == XmlNodeType.Element && isOp)
                {
                    current = new JsonObject
                    {
                        ["Kind"] = reader.LocalName,
                        ["Name"] = reader.GetAttribute("Name"),
                        ["IsBound"] = reader.GetAttribute("IsBound") == "true",
                        ["Parameters"] = new JsonArray()
                    };
                    var name = (reader.GetAttribute("Name") ?? string.Empty).ToLowerInvariant();
                    matches = ActionTerms.Any(name.Contains);
                    if (reader.IsEmptyElement) { if (matches) result.Add(current); current = null; }
                }
                else if (current is not null && reader.NodeType == XmlNodeType.Element && reader.LocalName == "Parameter")
                {
                    var type = reader.GetAttribute("Type") ?? string.Empty;
                    ((JsonArray)current["Parameters"]!).Add(new JsonObject { ["Name"] = reader.GetAttribute("Name"), ["Type"] = type, ["Nullable"] = reader.GetAttribute("Nullable") });
                    if (type.Contains("calendar", StringComparison.OrdinalIgnoreCase)) matches = true;
                }
                else if (current is not null && reader.NodeType == XmlNodeType.EndElement && isOp)
                {
                    if (matches) result.Add(current);
                    current = null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException)
        {
            _unresolved.Add($"$metadata (acciones): {Truncate(ex.Message, 300)}");
        }
        return result;
    }

    private static bool ActionTargetsEntity(JsonObject action, string logical)
    {
        if (action["Name"]?.GetValue<string>()?.Contains(logical, StringComparison.OrdinalIgnoreCase) == true) return true;
        return action["Parameters"] is JsonArray ps && ps.Any(p =>
        {
            var type = p?["Type"]?.GetValue<string>() ?? string.Empty;
            return type.EndsWith("." + logical, StringComparison.OrdinalIgnoreCase) || type.EndsWith("." + logical + ")", StringComparison.OrdinalIgnoreCase);
        });
    }

    private JsonObject BuildSummary(JsonArray entities, JsonArray actions)
    {
        static string L(JsonNode e) => e["LogicalName"]!.GetValue<string>();
        static bool Has(string? text, params string[] terms) => text is not null && terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

        var templates = entities.Where(e => (Has(L(e!), "template") && Has(L(e!), "calendar", "work"))
            || e!["DisplayNames"]!.AsArray().Any(d => Has(d!.GetValue<string>(), "calendar template", "work template", "plantilla de calendario"))).ToList();
        var project = entities.FirstOrDefault(e => L(e!) == "msdyn_project");
        var projectAttrs = project?["Attributes"]?.AsArray()
            .Where(a => Has(a!["LogicalName"]?.GetValue<string>(), "calendar", "template")
                || (a["LookupTargets"] as JsonArray)?.Any(t => Has(t?.GetValue<string>(), "calendar", "template")) == true).ToList() ?? [];
        var ruleEntities = entities.Where(e => Has(L(e!), "calendarrule", "workhour", "calendar_rule")).ToList();
        var candidateNames = entities.Select(e => L(e!)).ToHashSet();
        var projectRelationships = new List<JsonNode>();
        foreach (var kind in new[] { "ManyToOneRelationships", "OneToManyRelationships" })
            foreach (var r in project?[kind]?.AsArray() ?? [])
                if (candidateNames.Contains(r!["ReferencedEntity"]?.GetValue<string>() ?? "") || candidateNames.Contains(r["ReferencingEntity"]?.GetValue<string>() ?? "")
                    || Has(r["ReferencingAttribute"]?.GetValue<string>(), "calendar"))
                    projectRelationships.Add(new JsonObject { ["Direction"] = kind, ["Relationship"] = r.DeepClone() });

        var unresolved = new List<string>(_unresolved);
        if (templates.Count == 0) unresolved.Add("Calendar Template entity: no candidate found by name/label.");
        if (project is null) unresolved.Add("msdyn_project: entity not found.");
        else if (projectAttrs.Count == 0) unresolved.Add("msdyn_project: no attribute referencing calendar/template found.");
        if (ruleEntities.Count == 0) unresolved.Add("calendarrule / work-hour rule entity: not found.");
        if (projectRelationships.Count == 0) unresolved.Add("Relationship between msdyn_project and calendar/template entities: not found.");
        if (actions.Count == 0) unresolved.Add("Calendar-related actions/functions: none found in $metadata.");
        unresolved.Add("Calendar Template name max length: see PrimaryNameMaxLength of the confirmed template entity (Summary.CalendarTemplateCandidates).");

        static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.Select(i => i?.DeepClone()).ToArray());
        return new JsonObject
        {
            ["CalendarTemplateCandidates"] = Arr(templates.Select(t => (JsonNode?)new JsonObject
            {
                ["LogicalName"] = L(t!),
                ["EntitySetName"] = t!["EntitySetName"]?.DeepClone(),
                ["PrimaryNameAttribute"] = t["PrimaryNameAttribute"]?.DeepClone(),
                ["PrimaryNameMaxLength"] = t["Attributes"]?.AsArray()
                    .FirstOrDefault(a => a!["LogicalName"]?.GetValue<string>() == t["PrimaryNameAttribute"]?.GetValue<string>())?["MaxLength"]?.DeepClone()
            })),
            ["ProjectCalendarAttributes"] = Arr(projectAttrs),
            ["CalendarRuleEntities"] = Arr(ruleEntities.Select(r => (JsonNode?)new JsonObject { ["LogicalName"] = L(r!), ["EntitySetName"] = r!["EntitySetName"]?.DeepClone() })),
            ["ProjectRelationships"] = Arr(projectRelationships),
            ["CalendarActions"] = Arr(actions),
            ["UnresolvedMetadata"] = new JsonArray(unresolved.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray())
        };
    }

    public void PrintSummary(JsonObject result)
    {
        var s = result["Summary"]!.AsObject();
        void Section(string title, string key, Func<JsonNode, string> format)
        {
            _out.WriteLine();
            _out.WriteLine(title);
            var items = s[key]!.AsArray();
            if (items.Count == 0) _out.WriteLine("  (none)");
            foreach (var i in items) _out.WriteLine("  - " + format(i!));
        }
        Section("CALENDAR TEMPLATE CANDIDATES", "CalendarTemplateCandidates", i => $"{i["LogicalName"]} (set: {i["EntitySetName"]}, name attr: {i["PrimaryNameAttribute"]}, max length: {i["PrimaryNameMaxLength"]?.ToJsonString() ?? "?"})");
        Section("PROJECT CALENDAR ATTRIBUTES", "ProjectCalendarAttributes", i => $"{i["LogicalName"]} [{i["AttributeType"]}] targets: {i["LookupTargets"]?.ToJsonString() ?? "-"}");
        Section("CALENDAR RULE ENTITIES", "CalendarRuleEntities", i => $"{i["LogicalName"]} (set: {i["EntitySetName"]})");
        Section("PROJECT RELATIONSHIPS", "ProjectRelationships", i => $"{i["Direction"]}: {i["Relationship"]!["SchemaName"]} ({i["Relationship"]!["ReferencingEntity"]}.{i["Relationship"]!["ReferencingAttribute"]} -> {i["Relationship"]!["ReferencedEntity"]})");
        Section("CALENDAR ACTIONS", "CalendarActions", i => $"{i["Kind"]} {i["Name"]} ({string.Join(", ", i["Parameters"]!.AsArray().Select(p => p!["Name"] + ":" + p["Type"]))})");
        Section("UNRESOLVED METADATA", "UnresolvedMetadata", i => i.GetValue<string>());
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
