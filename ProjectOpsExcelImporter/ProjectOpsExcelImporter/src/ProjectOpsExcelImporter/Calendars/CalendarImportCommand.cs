using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace ProjectOpsExcelImporter.Calendars;

public sealed record CalendarImportServices(
    ICalendarTemplateRepository Templates,
    IProjectOperationsProjectService Projects,
    IProjectCalendarRepository Calendars,
    IProjectTaskService? Tasks = null,
    IProjectDependencyService? Dependencies = null,
    IProjectResourceService? Resources = null);

public sealed record CalendarImportCommandResult(int ExitCode, bool ContinueWithTaskImport);

/// <summary>
/// Fases de calendario del comando import. Valida el schema (reutilizando <see cref="SchemaValidator"/>) ANTES de
/// crear ningún servicio de Dataverse: si falla, la fábrica de servicios no se invoca.
/// </summary>
public static class CalendarImportCommand
{
    public const string DefaultSchemaFileName = "project-dump.schema.json";

    public static string ResolveSchemaPath(string jsonPath, string? schemaOption)
        => !string.IsNullOrWhiteSpace(schemaOption)
            ? schemaOption
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? ".", DefaultSchemaFileName);

    public static async Task<CalendarImportCommandResult> RunAsync(
        string jsonPath, string? schemaOption, CalendarImportSettings settings, string statePath,
        Func<CalendarImportServices> servicesFactory, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var schemaPath = ResolveSchemaPath(jsonPath, schemaOption);

        // 1) Schema: mismo servicio que validate-schema. Termina aquí si falla (sin tocar Dataverse).
        var schemaExit = SchemaValidator.Run(jsonPath, schemaPath);
        if (schemaExit != SchemaValidator.ExitOk) return new CalendarImportCommandResult(schemaExit, false);

        try
        {
            settings.Validate();
            var project = MppProjectReader.ReadFile(jsonPath);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jsonPath)));
            var services = servicesFactory();
            var orchestrator = new ProjectImportOrchestrator(settings, services.Templates, services.Projects, services.Calendars,
                new ImportStateService(), output, delay, services.Tasks, services.Dependencies, services.Resources);
            await orchestrator.RunAsync(new CalendarImportRequest(jsonPath, schemaPath, statePath, hash, project), cancellationToken).ConfigureAwait(false);
            // Con ImportTasks la importación termina tras la fase de tareas: no se continúa con el flujo antiguo.
            return new CalendarImportCommandResult(ImportExitCodes.Ok, !settings.StopAfterCalendar && !settings.ImportTasks && !settings.ImportDependencies && !settings.ImportResources);
        }
        catch (OperationCanceledException) { throw; }
        catch (CalendarMetadataNotConfiguredException ex)
        {
            error.WriteLine($"ERROR: {ex.Message}");
            return new CalendarImportCommandResult(ex.ExitCode, false);
        }
        catch (CalendarImportException ex)
        {
            error.WriteLine($"ERROR [{ex.Stage}]: {ex.Message}");
            return new CalendarImportCommandResult(ex.ExitCode, false);
        }
        catch (MsalException ex)
        {
            error.WriteLine($"ERROR: autenticación/conexión con Dataverse: {ex.Message}");
            return new CalendarImportCommandResult(ImportExitCodes.Connection, false);
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"ERROR: configuración: {ex.Message}");
            return new CalendarImportCommandResult(ImportExitCodes.Configuration, false);
        }
    }
}
