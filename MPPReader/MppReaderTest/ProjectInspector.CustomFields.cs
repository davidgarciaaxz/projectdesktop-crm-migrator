using System.Text.RegularExpressions;
using MPXJ.Net;
using MppReaderTest.Models;
using Task = MPXJ.Net.Task;

namespace MppReaderTest;

public sealed partial class ProjectInspector
{
    private sealed record EntityRef(string EntityType, int? Id, int? UniqueId, string? Name, IFieldContainer Container);

    private List<EntityRef> EntitiesFor(FieldTypeClass cls) => cls switch
    {
        FieldTypeClass.Task => _tasks.Select(t => new EntityRef("Task", t.ID, t.UniqueID, t.Name, t)).ToList(),
        FieldTypeClass.Resource => _resources.Select(r => new EntityRef("Resource", r.ID, r.UniqueID, r.Name, r)).ToList(),
        FieldTypeClass.Assignment => _assignments.Select(a => new EntityRef("Assignment", null, a.UniqueID,
            $"{a.Task?.Name ?? "(sin tarea)"} / {a.Resource?.Name ?? "(sin recurso)"}", a)).ToList(),
        FieldTypeClass.Project => new List<EntityRef> { new("Project", null, _project.ProjectProperties.UniqueID, _project.ProjectProperties.Name, _project.ProjectProperties) },
        _ => new List<EntityRef>()
    };

    private static string Family(IFieldType ft) => Regex.Replace(FieldCatalog.MemberName(ft), @"\d+", "");

    /// <summary>All custom/extended attribute FieldTypes known to MPXJ.Net, independent of the file contents.</summary>
    private List<IFieldType> KnownCustomFields()
    {
        var all = new List<IFieldType>();
        var failedLists = new List<string>();
        void AddList(string name, Func<System.Collections.IEnumerable?> getter)
        {
            // Some MPXJ.Net static lists fail while being enumerated (IKVM cast errors), so each one is guarded.
            try
            {
                var items = getter()?.Cast<IFieldType>().ToList();
                if (items != null) all.AddRange(items);
            }
            catch (Exception ex)
            {
                failedLists.Add($"{name} ({ex.GetType().Name})");
            }
        }

        AddList("FieldLists.CustomFields", () => FieldLists.CustomFields);
        AddList("TaskFieldList.CustomFields", () => TaskFieldList.CustomFields);
        AddList("ResourceFieldLists.CustomFields", () => ResourceFieldLists.CustomFields);
        AddList("AssignmentFieldLists.CustomFields", () => AssignmentFieldLists.CustomFields);
        AddList("TaskFieldList.EnterpriseCustomCost", () => TaskFieldList.EnterpriseCustomCost);
        AddList("TaskFieldList.EnterpriseCustomDate", () => TaskFieldList.EnterpriseCustomDate);
        AddList("TaskFieldList.EnterpriseCustomDuration", () => TaskFieldList.EnterpriseCustomDuration);
        AddList("TaskFieldList.EnterpriseCustomFlag", () => TaskFieldList.EnterpriseCustomFlag);
        AddList("TaskFieldList.EnterpriseCustomNumber", () => TaskFieldList.EnterpriseCustomNumber);
        AddList("TaskFieldList.EnterpriseCustomText", () => TaskFieldList.EnterpriseCustomText);
        AddList("ResourceFieldLists.EnterpriseCustomCost", () => ResourceFieldLists.EnterpriseCustomCost);
        AddList("ResourceFieldLists.EnterpriseCustomDate", () => ResourceFieldLists.EnterpriseCustomDate);
        AddList("ResourceFieldLists.EnterpriseCustomDuration", () => ResourceFieldLists.EnterpriseCustomDuration);
        AddList("ResourceFieldLists.EnterpriseCustomFlag", () => ResourceFieldLists.EnterpriseCustomFlag);
        AddList("ResourceFieldLists.EnterpriseCustomNumber", () => ResourceFieldLists.EnterpriseCustomNumber);
        AddList("ResourceFieldLists.EnterpriseCustomText", () => ResourceFieldLists.EnterpriseCustomText);
        AddList("AssignmentFieldLists.EnterpriseCustomCost", () => AssignmentFieldLists.EnterpriseCustomCost);
        AddList("AssignmentFieldLists.EnterpriseCustomDate", () => AssignmentFieldLists.EnterpriseCustomDate);
        AddList("AssignmentFieldLists.EnterpriseCustomDuration", () => AssignmentFieldLists.EnterpriseCustomDuration);
        AddList("AssignmentFieldLists.EnterpriseCustomFlag", () => AssignmentFieldLists.EnterpriseCustomFlag);
        AddList("AssignmentFieldLists.EnterpriseCustomNumber", () => AssignmentFieldLists.EnterpriseCustomNumber);
        AddList("AssignmentFieldLists.EnterpriseCustomText", () => AssignmentFieldLists.EnterpriseCustomText);
        AddList("AssignmentFieldLists.EnterpriseResourceMultiValue", () => AssignmentFieldLists.EnterpriseResourceMultiValue);
        AddList("AssignmentFieldLists.EnterpriseResourceOutlineCode", () => AssignmentFieldLists.EnterpriseResourceOutlineCode);

        // Complementary discovery by name over the full FieldType enumerations, independent of the lists above.
        var pattern = new Regex(@"^(Enterprise(Project|Resource)?)?(Text|Number|Date|Start|Finish|Duration|Cost|Flag|OutlineCode|MultiValue|CustomField)\d+$");
        AddList("TaskField.Values", () => TaskField.Values.Where(f => pattern.IsMatch(FieldCatalog.MemberName(f))));
        AddList("ResourceField.Values", () => ResourceField.Values.Where(f => pattern.IsMatch(FieldCatalog.MemberName(f))));
        AddList("AssignmentField.Values", () => AssignmentField.Values.Where(f => pattern.IsMatch(FieldCatalog.MemberName(f))));

        if (failedLists.Count > 0)
        {
            Warn("fieldlists", "Limitación MPXJ.Net: estas listas estáticas de FieldType no se pueden enumerar (error interno de conversión IKVM); " +
                               $"sus campos se descubren igualmente filtrando TaskField/ResourceField/AssignmentField.Values por nombre: {string.Join(", ", failedLists)}");
        }

        return all
            .Where(f => f != null && !IsHelperField(f))
            .GroupBy(FieldCatalog.Key)
            .Select(g => g.First())
            .OrderBy(f => f.FieldTypeClass).ThenBy(f => Family(f)).ThenBy(f => FieldNumber(f))
            .ToList();
    }

    // Duration units / outline code index entries are storage helpers of the real custom field.
    private static bool IsHelperField(IFieldType f)
    {
        var member = FieldCatalog.MemberName(f);
        return member.EndsWith("Units", StringComparison.Ordinal) || member.EndsWith("Index", StringComparison.Ordinal);
    }

    private static int FieldNumber(IFieldType f)
    {
        var m = Regex.Match(FieldCatalog.MemberName(f), @"(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private void ReadCustomFields()
    {
        var known = KnownCustomFields();
        var knownKeys = new HashSet<string>(known.Select(FieldCatalog.Key));

        var definitions = (Safe(() => _project.CustomFields.ToList(), "ProjectFile.CustomFields") ?? new List<CustomField>())
            .Where(cf => cf.FieldType != null)
            .GroupBy(cf => FieldCatalog.Key(cf.FieldType))
            .ToDictionary(g => g.Key, g => g.First());

        var populated = new Dictionary<string, IFieldType>();
        void AddPopulated(ISet<IFieldType>? set)
        {
            if (set == null) return;
            foreach (var f in set.Where(f => !IsHelperField(f) && (knownKeys.Contains(FieldCatalog.Key(f)) || f.FieldName.StartsWith("Enterprise", StringComparison.Ordinal))))
            {
                populated.TryAdd(FieldCatalog.Key(f), f);
            }
        }
        AddPopulated(Safe(() => _project.Tasks.PopulatedFields, "TaskContainer.PopulatedFields"));
        AddPopulated(Safe(() => _project.Resources.PopulatedFields, "ResourceContainer.PopulatedFields"));
        AddPopulated(Safe(() => _project.ResourceAssignments.PopulatedFields, "ResourceAssignmentContainer.PopulatedFields"));
        AddPopulated(Safe(() => _project.ProjectProperties.PopulatedFields, "ProjectProperties.PopulatedFields"));

        // Custom fields shown in view tables, with the column title used in that view.
        var viewTitles = new Dictionary<string, List<string>>();
        var viewFields = new Dictionary<string, IFieldType>();
        foreach (var table in Safe(() => _project.Tables?.ToList(), "ProjectFile.Tables") ?? new List<Table>())
        {
            foreach (var col in Safe(() => table.Columns?.ToList(), "Table.Columns") ?? new List<Column>())
            {
                if (col.FieldType == null) continue;
                var key = FieldCatalog.Key(col.FieldType);
                if (!knownKeys.Contains(key) && !definitions.ContainsKey(key)) continue;
                viewFields.TryAdd(key, col.FieldType);
                if (!viewTitles.TryGetValue(key, out var titles)) viewTitles[key] = titles = new List<string>();
                var label = $"{table.Name}: '{col.Title}'";
                if (!titles.Contains(label)) titles.Add(label);
            }
        }

        var allKeys = definitions.Keys.Concat(populated.Keys).Concat(viewFields.Keys).Distinct().ToList();
        var entityCache = new Dictionary<FieldTypeClass, List<EntityRef>>();

        foreach (var key in allKeys)
        {
            var field = definitions.TryGetValue(key, out var cfDef) ? cfDef.FieldType
                : populated.TryGetValue(key, out var pf) ? pf : viewFields[key];
            var def = BuildDefinition(field, cfDef, populated.ContainsKey(key), viewTitles.GetValueOrDefault(key));

            if (!entityCache.TryGetValue(field.FieldTypeClass, out var entities))
            {
                entityCache[field.FieldTypeClass] = entities = EntitiesFor(field.FieldTypeClass);
            }

            var lookupSummary = def.HasLookupTable ? $"{def.LookupTable.Count} valores ({string.Join(" | ", def.LookupTable.Take(10).Select(l => l.Value))}{(def.LookupTable.Count > 10 ? " | ..." : "")})" : null;
            var errorKey = "cf-read:" + def.FieldType;
            foreach (var e in entities)
            {
                object? value;
                try
                {
                    value = e.Container.Get(field);
                }
                catch (Exception ex)
                {
                    Warn(errorKey, $"Error leyendo {def.FieldType} en {e.EntityType}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                var state = Fmt.IsEmpty(value) ? CustomFieldValueState.DefinedNoValue
                    : value is false ? CustomFieldValueState.DefinedFalse
                    : Fmt.IsZero(value) ? CustomFieldValueState.DefinedZero
                    : CustomFieldValueState.DefinedWithValue;
                if (state != CustomFieldValueState.DefinedNoValue) def.EntitiesWithValue++;
                if (state == CustomFieldValueState.DefinedWithValue) def.EntitiesWithNonZeroValue++;

                _dump.CustomFieldValues.Add(new CustomFieldValueDump
                {
                    EntityType = e.EntityType,
                    EntityId = e.Id,
                    EntityUniqueId = e.UniqueId,
                    EntityName = e.Name,
                    FieldType = def.FieldType,
                    InternalName = def.InternalName,
                    Alias = def.Alias,
                    DataType = def.DataType,
                    DotNetType = Fmt.DotNetType(value),
                    Value = Fmt.ToJsonValue(value),
                    FormattedValue = Fmt.IsEmpty(value) ? null : Fmt.Format(value),
                    ValueState = state,
                    IsEnterpriseCustomField = def.IsEnterpriseCustomField,
                    Formula = def.Formula,
                    LookupTable = lookupSummary
                });
            }

            _dump.CustomFieldDefinitions.Add(def);
        }

        _dump.CustomFieldDefinitions = _dump.CustomFieldDefinitions
            .OrderBy(d => d.EntityType).ThenBy(d => d.Family).ThenBy(d => d.InternalName, StringComparer.Ordinal)
            .ToList();

        var defsByKey = _dump.CustomFieldDefinitions.ToDictionary(d => d.EntityType + ":" + d.InternalName);
        foreach (var f in known)
        {
            defsByKey.TryGetValue(f.FieldTypeClass + ":" + f.FieldName, out var d);
            _dump.CustomFieldCatalog.Add(new CustomFieldCatalogEntry
            {
                EntityType = f.FieldTypeClass.ToString(),
                FieldType = FieldCatalog.Ident(f),
                InternalName = f.FieldName,
                Family = Family(f),
                Alias = d?.Alias,
                Status = d == null ? CustomFieldCatalogStatus.NotDefined
                    : d.HasCustomFieldDefinition || d.AliasSources.Count > 0
                        ? (d.EntitiesWithNonZeroValue > 0 ? CustomFieldCatalogStatus.DefinedWithValues : CustomFieldCatalogStatus.DefinedWithoutValues)
                        : CustomFieldCatalogStatus.PopulatedWithoutDefinition
            });
        }

        Warn("formula", "MPXJ.Net no expone fórmulas de campos personalizados (CustomField no tiene propiedad Formula); se informa Formula = null e IsCalculated = null (desconocido).");
    }

    private CustomFieldDefinitionDump BuildDefinition(IFieldType field, CustomField? cf, bool isPopulated, List<string>? viewTitles)
    {
        var ident = FieldCatalog.Ident(field);
        var alias = cf == null ? null : Safe(() => cf.Alias, ident + ".Alias");
        var lookup = cf == null ? null : Safe(() => cf.LookupTable, ident + ".LookupTable");
        var lookupItems = lookup == null ? new List<CustomFieldValueItem>() : Safe(() => lookup.ToList(), ident + ".LookupTable items") ?? new List<CustomFieldValueItem>();

        var def = new CustomFieldDefinitionDump
        {
            EntityType = field.FieldTypeClass.ToString(),
            FieldType = ident,
            InternalName = field.FieldName,
            Family = Family(field),
            Alias = string.IsNullOrEmpty(alias) ? null : alias,
            DataType = field.DataType?.ToString(),
            CustomFieldUniqueId = cf == null ? null : Safe(() => cf.UniqueID, ident + ".UniqueID"),
            LookupTableGuid = lookup == null ? null : Safe(() => lookup.GUID, ident + ".LookupTable.GUID"),
            HasLookupTable = lookupItems.Count > 0,
            LookupTable = lookupItems.Select(i => new LookupValueDump
            {
                UniqueId = i.UniqueID,
                ParentUniqueId = i.ParentUniqueID,
                Value = Fmt.Format(i.Value),
                Description = i.Description
            }).ToList(),
            IsEnterpriseCustomField = field.FieldName.StartsWith("Enterprise", StringComparison.Ordinal)
                                      || (lookup != null && Safe(() => lookup.Enterprise, ident + ".LookupTable.Enterprise")),
            Formula = null,
            FormulaExposedByMpxj = false,
            IsCalculated = null,
            HasGraphicalIndicator = cf != null && Safe(() => cf.GraphicalIndicator, ident + ".GraphicalIndicator") != null,
            Masks = cf == null ? new List<string>() : (Safe(() => cf.Masks?.Select(m => m.ToString()).ToList(), ident + ".Masks") ?? new List<string>()),
            HasCustomFieldDefinition = cf != null,
            IsPopulated = isPopulated,
            ViewTableTitles = viewTitles ?? new List<string>()
        };

        if (def.Alias != null) def.AliasSources.Add("CustomFields alias");
        // A title different from the internal name means the column was renamed in the view.
        var renamedTitles = def.ViewTableTitles
            .Select(t => t[(t.IndexOf(": '", StringComparison.Ordinal) + 3)..].TrimEnd('\''))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, field.FieldName, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        if (renamedTitles.Count > 0)
        {
            def.AliasSources.Add("View table column title");
            def.Alias ??= renamedTitles[0];
            if (renamedTitles.Count > 1 || (def.Alias != null && renamedTitles.Any(t => t != def.Alias)))
            {
                Warn("multi-alias:" + ident, $"{ident} aparece con varios títulos: {string.Join(", ", renamedTitles.Prepend(def.Alias ?? "").Distinct())}");
            }
        }

        return def;
    }

    // ---------------------------------------------------------------- Dynamic field dump

    private void ReadDynamicFields()
    {
        var info = _dump.DynamicFields;
        info.Enabled = true;
        info.Note = "Se registran únicamente valores no nulos y no vacíos. Se omiten los FieldType de datos timephased en bruto (listas potencialmente enormes).";

        var sets = new (FieldTypeClass Class, IEnumerable<IFieldType> Fields)[]
        {
            (FieldTypeClass.Task, TaskField.Values.Cast<IFieldType>()),
            (FieldTypeClass.Resource, ResourceField.Values.Cast<IFieldType>()),
            (FieldTypeClass.Assignment, AssignmentField.Values.Cast<IFieldType>()),
        };

        var fieldErrors = new Dictionary<string, int>();
        var stats = new Dictionary<string, DynamicFieldStat>();
        foreach (var (cls, fields) in sets)
        {
            var usable = new List<IFieldType>();
            foreach (var f in fields)
            {
                var member = FieldCatalog.MemberName(f);
                if (member.StartsWith("RawTimephased", StringComparison.Ordinal) ||
                    f.DataType is DataType.TimephasedWorkList or DataType.TimephasedCostList)
                {
                    info.SkippedFieldTypes.Add(FieldCatalog.Ident(f));
                    continue;
                }
                usable.Add(f);
            }
            info.FieldTypesTested += usable.Count;

            var entities = EntitiesFor(cls);
            var count = 0;
            foreach (var e in entities)
            {
                foreach (var f in usable)
                {
                    info.ReadsAttempted++;
                    object? value;
                    try
                    {
                        value = e.Container.Get(f);
                    }
                    catch (Exception ex)
                    {
                        info.ReadErrors++;
                        var ident = FieldCatalog.Ident(f);
                        fieldErrors[ident] = fieldErrors.GetValueOrDefault(ident) + 1;
                        Warn("dyn:" + ident, $"Volcado dinámico: no se pudo leer {ident}: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (Fmt.IsEmpty(value)) continue;
                    count++;
                    var fieldIdent = FieldCatalog.Ident(f);
                    var formatted = Fmt.Format(value);
                    _dump.DynamicFieldValues.Add(new DynamicFieldValueDump
                    {
                        EntityType = e.EntityType,
                        EntityId = e.Id,
                        EntityUniqueId = e.UniqueId,
                        EntityName = e.Name,
                        FieldType = fieldIdent,
                        DataType = f.DataType?.ToString(),
                        DotNetType = Fmt.DotNetType(value),
                        Value = Fmt.ToJsonValue(value),
                        FormattedValue = formatted
                    });

                    if (!stats.TryGetValue(fieldIdent, out var stat))
                    {
                        stats[fieldIdent] = stat = new DynamicFieldStat
                        {
                            EntityType = e.EntityType,
                            FieldType = fieldIdent,
                            FieldName = f.FieldName,
                            DataType = f.DataType?.ToString(),
                            DotNetType = Fmt.DotNetType(value)
                        };
                    }
                    stat.NonNullCount++;
                    if (!Fmt.IsZero(value) && value is not false)
                    {
                        stat.NonDefaultCount++;
                        stat.SampleValue ??= Fmt.Truncate(formatted, 200);
                        stat.SampleEntity ??= $"{e.EntityType}[ID={Fmt.Int(e.Id)}, UID={Fmt.Int(e.UniqueId)}] {e.Name}";
                    }
                }
            }
            info.NonNullValuesByEntity[cls.ToString()] = count;
        }

        info.NonNullValues = _dump.DynamicFieldValues.Count;
        info.FieldStatistics = stats.Values.OrderBy(s => s.EntityType).ThenBy(s => s.FieldType, StringComparer.Ordinal).ToList();
        info.FieldTypesWithErrors = fieldErrors.Select(kv => $"{kv.Key} ({kv.Value})").ToList();
    }
}
