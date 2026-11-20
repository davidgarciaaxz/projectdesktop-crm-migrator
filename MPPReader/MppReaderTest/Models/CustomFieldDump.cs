namespace MppReaderTest.Models;

public enum CustomFieldCatalogStatus
{
    NotDefined,
    DefinedWithoutValues,
    DefinedWithValues,
    PopulatedWithoutDefinition
}

public enum CustomFieldValueState
{
    DefinedNoValue,
    DefinedZero,
    DefinedFalse,
    DefinedWithValue
}

/// <summary>One row per known custom FieldType (Text1..30, Cost1..10, ...), defined or not.</summary>
public sealed class CustomFieldCatalogEntry
{
    public string EntityType { get; set; } = "";
    public string FieldType { get; set; } = "";
    public string InternalName { get; set; } = "";
    public string Family { get; set; } = "";
    public string? Alias { get; set; }
    public CustomFieldCatalogStatus Status { get; set; }
}

public sealed class CustomFieldDefinitionDump
{
    public string EntityType { get; set; } = "";
    public string FieldType { get; set; } = "";
    public string InternalName { get; set; } = "";
    public string Family { get; set; } = "";
    public string? Alias { get; set; }
    public List<string> AliasSources { get; set; } = new();
    public List<string> ViewTableTitles { get; set; } = new();
    public string? DataType { get; set; }
    public int? CustomFieldUniqueId { get; set; }
    public Guid? LookupTableGuid { get; set; }
    public bool HasLookupTable { get; set; }
    public List<LookupValueDump> LookupTable { get; set; } = new();
    public bool IsEnterpriseCustomField { get; set; }
    public string? Formula { get; set; }
    public bool FormulaExposedByMpxj { get; set; }
    public bool? IsCalculated { get; set; }
    public bool HasGraphicalIndicator { get; set; }
    public List<string> Masks { get; set; } = new();
    public bool HasCustomFieldDefinition { get; set; }
    public bool IsPopulated { get; set; }
    public int EntitiesWithValue { get; set; }
    public int EntitiesWithNonZeroValue { get; set; }
}

public sealed class LookupValueDump
{
    public int? UniqueId { get; set; }
    public int? ParentUniqueId { get; set; }
    public string? Value { get; set; }
    public string? Description { get; set; }
}

public sealed class CustomFieldValueDump
{
    public string EntityType { get; set; } = "";
    public int? EntityId { get; set; }
    public int? EntityUniqueId { get; set; }
    public string? EntityName { get; set; }
    public string FieldType { get; set; } = "";
    public string InternalName { get; set; } = "";
    public string? Alias { get; set; }
    public string? DataType { get; set; }
    public string? DotNetType { get; set; }
    public object? Value { get; set; }
    public string? FormattedValue { get; set; }
    public CustomFieldValueState ValueState { get; set; }
    public bool IsEnterpriseCustomField { get; set; }
    public string? Formula { get; set; }
    public string? LookupTable { get; set; }
}

public sealed class DynamicFieldDumpInfo
{
    public bool Enabled { get; set; }
    public string Note { get; set; } = "";
    public int FieldTypesTested { get; set; }
    public long ReadsAttempted { get; set; }
    public int NonNullValues { get; set; }
    public int ReadErrors { get; set; }
    public List<string> SkippedFieldTypes { get; set; } = new();
    public List<string> FieldTypesWithErrors { get; set; } = new();
    public Dictionary<string, int> NonNullValuesByEntity { get; set; } = new();
    public bool ValuesIncludedInJson { get; set; }
    /// <summary>One row per FieldType that returned at least one non-null value.</summary>
    public List<DynamicFieldStat> FieldStatistics { get; set; } = new();
}

public sealed class DynamicFieldStat
{
    public string EntityType { get; set; } = "";
    public string FieldType { get; set; } = "";
    public string? FieldName { get; set; }
    public string? DataType { get; set; }
    public string? DotNetType { get; set; }
    public int NonNullCount { get; set; }
    /// <summary>Values that are not 0, false or a zero duration.</summary>
    public int NonDefaultCount { get; set; }
    public string? SampleValue { get; set; }
    public string? SampleEntity { get; set; }
}

public sealed class DynamicFieldValueDump
{
    public string EntityType { get; set; } = "";
    public int? EntityId { get; set; }
    public int? EntityUniqueId { get; set; }
    public string? EntityName { get; set; }
    public string FieldType { get; set; } = "";
    public string? DataType { get; set; }
    public string? DotNetType { get; set; }
    public object? Value { get; set; }
    public string? FormattedValue { get; set; }
}
