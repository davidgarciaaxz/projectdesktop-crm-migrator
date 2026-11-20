using ClosedXML.Excel;
using System.Globalization;
using System.Text;

namespace ProjectOpsExcelImporter;

public sealed class ExcelProjectReader
{
    private static readonly string[] RequiredHeaders =
    [
        "Nombre de tarea", "% completado", "Comienzo", "Fin", "Duración", "Trabajo",
        "Predecesoras", "Nombres de los recursos", "Id", "GUID",
    "Número de esquema"
    ];

    public WorkbookProject Read(string path, AppSettings settings)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("No se encontró el Excel.", path);
        using var workbook = new XLWorkbook(path);
        var worksheet = string.IsNullOrWhiteSpace(settings.WorksheetName)
            ? workbook.Worksheets.First()
            : workbook.Worksheet(settings.WorksheetName);
        var issues = new List<ValidationIssue>();
        var headerRow = FindHeaderRow(worksheet);
        var headers = BuildHeaderMap(worksheet, headerRow);
        foreach (var header in RequiredHeaders)
            if (!headers.ContainsKey(Normalize(header)))
                throw new InvalidDataException($"Falta la columna obligatoria '{header}'.");

        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
        var rows = new List<MutableTask>();
        ProjectRow? project = null;

        for (var rowNumber = headerRow + 1; rowNumber <= lastRow; rowNumber++)
        {
            var idValue = CellValue(worksheet, rowNumber, headers, "Id");
            if (idValue is null || string.IsNullOrWhiteSpace(Convert.ToString(idValue, CultureInfo.InvariantCulture))) continue;
            var sourceId = Convert.ToInt32(ValueParsers.ParseSpanishDecimal(idValue), CultureInfo.InvariantCulture);
            var raw = headers.Values.ToDictionary(
                h => h.Original,
                h => GetCellObject(worksheet.Cell(rowNumber, h.Column)),
                StringComparer.OrdinalIgnoreCase);
            var rawName = Convert.ToString(CellValue(worksheet, rowNumber, headers, "Nombre de tarea"), CultureInfo.InvariantCulture);
            var sourceGuidText = Convert.ToString(CellValue(worksheet, rowNumber, headers, "GUID"), CultureInfo.InvariantCulture)?.Trim();
            if (!Guid.TryParse(sourceGuidText, out var sourceGuid))
            {
                sourceGuid = DeterministicGuid.Create(Guid.Parse("3A11864E-38C5-4C98-9B62-687AF0D9CA1A"), $"row:{rowNumber}:id:{sourceId}");
                issues.Add(new ValidationIssue("Warning", "INVALID_SOURCE_GUID", $"GUID inválido; se generó un identificador estable para Id {sourceId}.", rowNumber, sourceId));
            }

            if (project is null)
            {
                project = new ProjectRow
                {
                    Name = string.IsNullOrWhiteSpace(rawName) ? $"Proyecto {sourceId}" : rawName.Trim(),
                    SourceId = sourceId,
                    SourceGuid = sourceGuid,
                    Start = ValueParsers.ParseDate(CellValue(worksheet, rowNumber, headers, "Comienzo")),
                    Finish = ValueParsers.ParseDate(CellValue(worksheet, rowNumber, headers, "Fin")),
                    Progress = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "% completado")),
                    RawValues = raw
                };
                continue;
            }

            var outlineNumber = Convert.ToString(
                CellValue(
                    worksheet,
                    rowNumber,
                    headers,
                    "Número de esquema"),
                CultureInfo.InvariantCulture
            )?.Trim();

            var outlineLevel = string.IsNullOrWhiteSpace(outlineNumber)
                ? -1
                : outlineNumber.Split(
                    '.',
                    StringSplitOptions.RemoveEmptyEntries).Length;

            var displayName = rawName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = $"[SIN NOMBRE] Id {sourceId}";
                issues.Add(new ValidationIssue("Warning", "BLANK_TASK_NAME", $"La tarea Id {sourceId} no tiene nombre; se usará '{displayName}'.", rowNumber, sourceId));
            }

            var durationRaw = CellValue(worksheet, rowNumber, headers, "Duración");
            var task = new ProjectTaskRow
            {
                ExcelRow = rowNumber,
                SourceId = sourceId,
                SourceGuid = sourceGuid,
                Name = displayName,
                OutlineNumber = outlineNumber,
                OutlineLevel = outlineLevel,
                Start = ValueParsers.ParseDate(CellValue(worksheet, rowNumber, headers, "Comienzo")),
                Finish = ValueParsers.ParseDate(CellValue(worksheet, rowNumber, headers, "Fin")),
                DurationDays = ValueParsers.ParseDurationDays(durationRaw, settings),
                WorkHours = ValueParsers.ParseWorkHours(CellValue(worksheet, rowNumber, headers, "Trabajo")),
                Progress = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "% completado")),
                BaselineCost6 = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "Costo de línea base6")),
                Cost = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "Costo")),
                ActualCost = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "Costo real")),
                CostVariance = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "Variación de costo")),
                Sabana = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "SÁBANA")),
                VariacionSabana = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "VARIACIÓN SÁBANA")),
                RemainingCost = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "Costo restante")),
                Facturacion = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "FACTURACIÓN")),
                Cobros = ValueParsers.ParseSpanishDecimal(CellValue(worksheet, rowNumber, headers, "COBROS")),
                Text1 = StringValue(CellValue(worksheet, rowNumber, headers, "Texto1")),
                Text2 = StringValue(CellValue(worksheet, rowNumber, headers, "Texto2")),
                Text3 = StringValue(CellValue(worksheet, rowNumber, headers, "Texto3")),
                IsEstimated = Convert.ToString(durationRaw, CultureInfo.InvariantCulture)?.Contains("?", StringComparison.Ordinal) == true,
                RawValues = raw
            };
            try
            {
                task.Dependencies.AddRange(ValueParsers.ParseDependencies(CellValue(worksheet, rowNumber, headers, "Predecesoras"), sourceId, settings));
            }
            catch (FormatException ex)
            {
                issues.Add(new ValidationIssue("Error", "INVALID_DEPENDENCY", ex.Message, rowNumber, sourceId));
            }
            task.Resources.AddRange(ValueParsers.ParseResources(CellValue(worksheet, rowNumber, headers, "Nombres de los recursos")));
            rows.Add(new MutableTask(task, outlineLevel));
        }

        if (project is null) throw new InvalidDataException("No se encontró la fila de proyecto tras la cabecera.");
        InferBlankLevels(rows, issues);
        BuildHierarchy(rows.Select(r => r.Task).ToList(), issues);
        if (headers.ContainsKey(Normalize("Sucesoras")) && rows.Any(r => r.Task.RawValues.TryGetValue("Sucesoras", out var value) && value is not null))
            issues.Add(new ValidationIssue("Warning", "SUCCESSOR_COLUMN_IGNORED", "La columna Sucesoras es redundante y contiene al menos un texto truncado; las dependencias se construyen desde Predecesoras, que contiene los 510 vínculos completos."));

        return new WorkbookProject
        {
            Project = project,
            Tasks = rows.Select(r => r.Task).ToList(),
            ReaderIssues = issues,
            WorksheetName = worksheet.Name
        };
    }

    private static int FindHeaderRow(IXLWorksheet worksheet)
    {
        var max = Math.Min(20, worksheet.LastRowUsed()?.RowNumber() ?? 1);
        for (var row = 1; row <= max; row++)
        {
            var values = worksheet.Row(row).CellsUsed().Select(c => Normalize(c.GetFormattedString())).ToHashSet();
            if (values.Contains(Normalize("Nombre de tarea")) && values.Contains(Normalize("Id"))) return row;
        }
        throw new InvalidDataException("No se encontró la fila de cabecera (Nombre de tarea / Id).");
    }

    private static Dictionary<string, HeaderCell> BuildHeaderMap(IXLWorksheet worksheet, int row)
    {
        var map = new Dictionary<string, HeaderCell>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in worksheet.Row(row).CellsUsed())
        {
            var original = cell.GetFormattedString().Trim();
            if (!string.IsNullOrEmpty(original)) map[Normalize(original)] = new HeaderCell(original, cell.Address.ColumnNumber);
        }
        return map;
    }

    private static object? CellValue(IXLWorksheet ws, int row, IReadOnlyDictionary<string, HeaderCell> headers, string header)
        => headers.TryGetValue(Normalize(header), out var item) ? GetCellObject(ws.Cell(row, item.Column)) : null;

    private static object? GetCellObject(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        return cell.DataType switch
        {
            XLDataType.Number => cell.GetDouble(),
            XLDataType.DateTime => cell.GetDateTime(),
            XLDataType.Boolean => cell.GetBoolean(),
            _ => cell.GetFormattedString()
        };
    }

    private static string? StringValue(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void InferBlankLevels(List<MutableTask> rows, List<ValidationIssue> issues)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Task.OutlineLevel >= 0) continue;
            var previous = i > 0 ? rows[i - 1].Task.OutlineLevel : 1;
            var next = i + 1 < rows.Count ? rows[i + 1].Task.OutlineLevel : previous;
            rows[i].Task.OutlineLevel = previous == next ? previous : Math.Max(previous, next);
            issues.Add(new ValidationIssue("Warning", "INFERRED_OUTLINE_LEVEL", $"Se infirió nivel {rows[i].Task.OutlineLevel} para la tarea sin nombre.", rows[i].Task.ExcelRow, rows[i].Task.SourceId));
        }
    }

    internal static void BuildHierarchy(List<ProjectTaskRow> tasks, List<ValidationIssue> issues)
    {
        var stack = new Dictionary<int, ProjectTaskRow>();
        var byId = tasks.ToDictionary(t => t.SourceId);
        foreach (var task in tasks)
        {
            if (task.OutlineLevel < 1)
            {
                issues.Add(new ValidationIssue("Error", "INVALID_OUTLINE_LEVEL", $"Nivel de esquema {task.OutlineLevel} no válido.", task.ExcelRow, task.SourceId));
                task.OutlineLevel = 1;
            }
            if (task.OutlineLevel > 1)
            {
                if (stack.TryGetValue(task.OutlineLevel - 1, out var parent)) task.ParentSourceId = parent.SourceId;
                else issues.Add(new ValidationIssue("Error", "MISSING_PARENT", $"No se encontró tarea padre para el nivel {task.OutlineLevel}.", task.ExcelRow, task.SourceId));
            }
            stack[task.OutlineLevel] = task;
            foreach (var key in stack.Keys.Where(k => k > task.OutlineLevel).ToList()) stack.Remove(key);
        }
        foreach (var task in tasks.Where(t => t.ParentSourceId.HasValue)) byId[task.ParentSourceId!.Value].IsSummary = true;
    }

    private static string Normalize(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString().Normalize(NormalizationForm.FormC).Trim();
    }

    private sealed record HeaderCell(string Original, int Column);
    private sealed record MutableTask(ProjectTaskRow Task, int LeadingSpaces);
}
