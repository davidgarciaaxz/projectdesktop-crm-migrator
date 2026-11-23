using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectOpsExcelImporter;

public sealed class WorkbookProject
{
    public required ProjectRow Project { get; init; }
    public required List<ProjectTaskRow> Tasks { get; init; }
    public required List<ValidationIssue> ReaderIssues { get; init; }
    public string WorksheetName { get; init; } = string.Empty;
}

public sealed class ProjectRow
{
    public required string Name { get; init; }
    public int SourceId { get; init; }
    public Guid SourceGuid { get; set; }
    public DateOnly? Start { get; init; }
    public DateOnly? Finish { get; init; }
    public decimal Progress { get; init; }
    public Dictionary<string, object?> RawValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectTaskRow
{
    public int ExcelRow { get; set; }
    public int SourceId { get; init; }
    public Guid SourceGuid { get; set; }
    public required string Name { get; set; }
    public int OutlineLevel { get; set; }
    public int? ParentSourceId { get; set; }
    public bool IsSummary { get; set; }
    public bool IsMilestone => DurationDays == 0;
    public bool IsEstimated { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? Finish { get; init; }
    public decimal DurationDays { get; init; }
    public decimal WorkHours { get; init; }
    public decimal Progress { get; init; }
    public decimal BaselineCost6 { get; init; }
    public decimal Cost { get; init; }
    public decimal ActualCost { get; init; }
    public decimal CostVariance { get; init; }
    public decimal Sabana { get; init; }
    public decimal VariacionSabana { get; init; }
    public decimal RemainingCost { get; init; }
    public decimal Facturacion { get; init; }
    public decimal Cobros { get; init; }
    public string? Text1 { get; init; }
    public string? Text2 { get; init; }
    public string? Text3 { get; init; }
    public string? OutlineNumber { get; set; }
    public List<TaskDependency> Dependencies { get; init; } = [];
    public List<TaskResource> Resources { get; init; } = [];
    public Dictionary<string, object?> RawValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record TaskDependency(
    int PredecessorSourceId,
    int SuccessorSourceId,
    DependencyType Type,
    decimal LagDays,
    string RawExpression);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyType
{
    FinishToStart,
    StartToStart,
    FinishToFinish,
    StartToFinish
}

public sealed record TaskResource(
    string SourceName,
    decimal? Units,
    decimal? Quantity,
    string RawExpression);

public sealed record ValidationIssue(
    string Severity,
    string Code,
    string Message,
    int? ExcelRow = null,
    int? SourceTaskId = null);

public sealed class ValidationReport
{
    public required string ProjectName { get; init; }
    public required Dictionary<string, object?> Metrics { get; init; }
    public required List<ValidationIssue> Issues { get; init; }
    [JsonIgnore] public bool HasErrors => Issues.Any(i => i.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
}

public sealed class AppSettings
{
    public int? MaxDependenciesToImport { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string EnvironmentUrl { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "v9.2";
    public string AuthMode { get; set; } = "Interactive";
    public string RedirectUri { get; set; } = "http://localhost";
    public string? WorksheetName { get; set; }
    public decimal HoursPerDay { get; set; } = 8m;
    public decimal DaysPerWeek { get; set; } = 5m;
    public decimal DaysPerMonth { get; set; } = 20m;
    public int BatchSize { get; set; } = 100;
    public int PersistTimeoutSeconds { get; set; } = 180;
    public int PersistPollSeconds { get; set; } = 3;
    public bool IncludeScheduledEnd { get; set; }
    public bool ImportProgress { get; set; } = true;

    public int? MaxTasksToImport { get; set; }
    public bool ImportDependencies { get; set; } = true;
    public bool ImportResources { get; set; } = true;
    public bool ImportAssignments { get; set; } = true;
    public string ProgressPolicy { get; set; } = "BinaryOnly";
    public bool CreateMissingResourcesAsGeneric { get; set; } = true;
    public bool IncludeDependencyDescription { get; set; } = true;
    public Dictionary<string, int> DependencyLinkTypeValues { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FinishToStart"] = 192350000,
        ["StartToStart"] = 192350001,
        ["FinishToFinish"] = 192350002,
        ["StartToFinish"] = 192350003
    };
    public string? DependencyLagCustomField { get; set; }
    public string BookableResourceNavigationProperty { get; set; } = "msdyn_bookableresourceid";
    public ProjectOpsExcelImporter.Calendars.CalendarImportSettings CalendarImport { get; set; } = new();
    public Dictionary<string, string> ProjectColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TaskColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options)
               ?? throw new InvalidOperationException($"No se pudo leer la configuración: {path}");
    }

    public void ValidateForImport()
    {
        if (!Guid.TryParse(TenantId, out _)) throw new InvalidOperationException("TenantId no es un GUID válido.");
        if (!Guid.TryParse(ClientId, out _)) throw new InvalidOperationException("ClientId no es un GUID válido.");
        if (!Uri.TryCreate(EnvironmentUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("EnvironmentUrl no es una URL válida.");
        if (BatchSize is < 1 or > 200) throw new InvalidOperationException("BatchSize debe estar entre 1 y 200.");
        if (HoursPerDay <= 0 || DaysPerWeek <= 0 || DaysPerMonth <= 0) throw new InvalidOperationException("Las unidades de calendario deben ser positivas.");
    }
}

public sealed class ResourceMap
{
    public List<ResourceMapEntry> Resources { get; set; } = [];
    public static ResourceMap Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new ResourceMap();
        return JsonSerializer.Deserialize<ResourceMap>(File.ReadAllText(path), JsonDefaults.Options) ?? new ResourceMap();
    }
}

public sealed class ResourceMapEntry
{
    public string SourceName { get; set; } = string.Empty;
    public Guid? ProjectTeamId { get; set; }
    public Guid? BookableResourceId { get; set; }
    public bool CreateAsGeneric { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>Identidad de un recurso Work del MPP y de sus registros en Project Operations (para reanudar sin duplicar).</summary>
public sealed class ImportedResourceState
{
    public int MppResourceUniqueId { get; set; }
    /// <summary>Guid del recurso en Project Desktop: identidad principal de origen.</summary>
    public Guid MppResourceGuid { get; set; }
    public string? ResourceName { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>Recurso reservable genérico común (configurado) al que apunta el miembro; el importador no crea recursos reservables.</summary>
    public Guid? GenericBookableResourceId { get; set; }
    public Guid? ProjectTeamMemberId { get; set; }
    public Guid? RoleId { get; set; }
    public Guid? ResourcingUnitId { get; set; }
}

public sealed class ImportState
{
    public Guid ProjectId { get; set; }
    public Guid BucketId { get; set; }
    public Dictionary<int, Guid> TaskIds { get; set; } = [];
    public Dictionary<string, Guid> TeamMemberIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> CreatedTasks { get; set; } = [];
    public HashSet<Guid> CreatedDependencies { get; set; } = [];
    public HashSet<Guid> CreatedAssignments { get; set; } = [];
    public HashSet<int> UpdatedProgressTasks { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }

    // --- Fases de calendario (aditivo; los states anteriores siguen cargando) ---
    public string? SourceJson { get; set; }
    public string? SourceHash { get; set; }
    public bool SchemaValidated { get; set; }
    public ProjectOpsExcelImporter.Calendars.CalendarImportState? Calendar { get; set; }
    public ProjectOpsExcelImporter.Calendars.ProjectImportState? Project { get; set; }
    public List<string>? CompletedStages { get; set; }
    /// <summary>Recursos Work del MPP importados como genéricos, por UniqueID del recurso en el MPP (aditivo; los states anteriores siguen cargando).</summary>
    public Dictionary<int, ImportedResourceState>? Resources { get; set; }

    public static ImportState? Load(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<ImportState>(File.ReadAllText(path), JsonDefaults.Options) : null;

    public void Save(string path)
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
