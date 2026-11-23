using System.Globalization;

namespace ProjectOpsExcelImporter.Calendars;

/// <summary>
/// Vista previa SIN Dataverse del mapeo de tareas: qué tareas se crearían, con qué atributos exactos, y qué tareas atraviesan
/// un día no laborable (CalendarExceptions) para elegir casos de prueba.
/// </summary>
public static class TaskPreview
{
    public static void Print(MppProject project, CalendarImportSettings settings, TaskSelection selection, TextWriter output, int maxRows = 40, int crossingCandidates = 12)
    {
        var plan = TaskImportPlanner.Build(project, settings.DurationRules, selection);       // GUID de tareas de marcador: el real depende del proyecto que se cree
        var warnings = new List<string>();
        var calendar = MppCalendarResolver.Resolve(project);
        var exceptions = MppExceptionNormalizer.Normalize(project, calendar, settings, warnings);
        var nonWorking = TaskCrossing.NonWorkingDays(exceptions);

        output.WriteLine($"Proyecto           : {project.Title}   GUID={project.ProjectGuid:D}");
        output.WriteLine($"Calendario base    : {calendar.Name} (UniqueId {calendar.UniqueId}); {exceptions.Count} excepciones normalizadas, {nonWorking.Count} días no laborables");
        output.WriteLine($"Tareas en el JSON  : {project.Tasks.Count}; seleccionadas: {plan.Tasks.Count} ({plan.Tasks.Count(t => t.AddedAsAncestor)} añadidas como ancestros)");
        foreach (var w in plan.Warnings.Concat(warnings)) output.WriteLine($"WARNING            : {w}");

        output.WriteLine();
        output.WriteLine("UniqueId  Nivel Padre  Tipo       Comienzo MPP      Duración MPP -> días enviados   Nombre");
        foreach (var t in plan.Tasks.Take(maxRows))
        {
            var s = t.Source;
            var kind = s.IsSummary ? "resumen" : t.DurationDays == 0 ? "hito" : "tarea";
            var duration = s.IsSummary ? "(la calcula PO)" : $"{s.DurationValue?.ToString(CultureInfo.InvariantCulture)} {s.DurationUnits} -> {t.DurationDays?.ToString(CultureInfo.InvariantCulture)}";
            output.WriteLine($"{s.UniqueId,-9} {s.OutlineLevel,-5} {(s.ParentUniqueId == 0 ? "-" : s.ParentUniqueId.ToString()),-6} {kind,-10} " +
                             $"{(s.IsSummary ? "(la calcula PO)" : s.Start?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-"),-17} {duration,-30}  {Trim(s.Name, 50)}");
        }
        if (plan.Tasks.Count > maxRows) output.WriteLine($"... y {plan.Tasks.Count - maxRows} más.");

        var leaf = plan.Tasks.FirstOrDefault(t => !t.Source.IsSummary);
        if (leaf is not null)
        {
            output.WriteLine();
            output.WriteLine("Atributos exactos enviados para la primera tarea no resumen (los GUID de proyecto y bucket son marcadores):");
            output.WriteLine(TaskEntityBuilder.Build(leaf, Guid.Empty, Guid.Empty).ToJsonString(JsonDefaults.Options));
        }

        var dependencyPlan = DependencyImportPlanner.Build(project, plan, settings.DurationRules, settings, project.ProjectGuid ?? Guid.Empty);
        output.WriteLine();
        output.WriteLine($"Dependencias en el JSON: {project.Dependencies.Count}; entre las tareas seleccionadas se aplicarían {dependencyPlan.Dependencies.Count}; omitidas {dependencyPlan.Skipped.Count}" +
                         (settings.IncludeSummaryDependencies ? string.Empty : " (las que enlazan con tareas resumen se omiten salvo --summary-dependencies true)"));
        foreach (var group in dependencyPlan.Skipped.GroupBy(s => s.Reason))
            output.WriteLine($"  omitidas {group.Count(),5}: {group.Key}");
        var sample = dependencyPlan.Dependencies.FirstOrDefault(d => d.LagSeconds != 0) ?? dependencyPlan.Dependencies.FirstOrDefault();
        if (sample is not null)
        {
            output.WriteLine($"Atributos exactos de la dependencia {sample.Predecessor.Source.UniqueId} -> {sample.Successor.Source.UniqueId} ({sample.Source.RelationType}, retardo {sample.Source.LagValue.ToString(CultureInfo.InvariantCulture)} {sample.Source.LagUnits}); los GUID de proyecto y tareas son marcadores:");
            output.WriteLine(DependencyEntityBuilder.Build(sample, Guid.Empty, settings).ToJsonString(JsonDefaults.Options));
        }

        var resourcePlan = ResourceImportPlanner.Build(project, plan, project.ProjectGuid ?? Guid.Empty, settings);
        var workTotal = project.Resources.Count(r => r.Type == ResourceImportPlanner.WorkType);
        output.WriteLine();
        output.WriteLine($"Recursos Work del JSON: {workTotal}; usados por alguna asignación utilizable: {resourcePlan.Resources.Count + resourcePlan.Unresolved.Count} " +
                         $"(solo esos tendrían miembro genérico del equipo; NO se crea ningún recurso reservable)");
        output.WriteLine($"Miembros genéricos que se crearían: {resourcePlan.Resources.Count}; recursos usados sin rol resoluble (error, no se crean): {resourcePlan.Unresolved.Count}");
        output.WriteLine($"Recurso genérico común: {settings.GenericBookableResourceId?.ToString("D") ?? "(NO configurado)"}; unidad de dotación: {settings.DefaultResourcingUnitId?.ToString("D") ?? "(NO configurada)"}; " +
                         $"rol predeterminado: {settings.DefaultResourceRoleId?.ToString("D") ?? "(no configurado)"}; mappings de rol: {settings.ResourceRoleMappings.Count}");
        foreach (var group in resourcePlan.Resources.GroupBy(r => (r.RoleId, r.RoleMethod)))
            output.WriteLine($"  rol {group.Key.RoleId:D} ({group.Key.RoleMethod}): {group.Count()} recurso(s)");
        foreach (var u in resourcePlan.Unresolved) output.WriteLine($"  SIN ROL: UniqueId {u.Source.UniqueId} '{u.Source.Name}': {u.Error}");
        output.WriteLine($"Recursos Material omitidos: {resourcePlan.SkippedMaterials.Count} ({resourcePlan.SkippedMaterials.Sum(m => m.Assignments.Count)} asignaciones)");
        foreach (var m in resourcePlan.SkippedMaterials) output.WriteLine($"  Material: UniqueId {m.Resource.UniqueId} '{m.Resource.Name}' (asignaciones {string.Join(", ", m.Assignments.Select(a => a.UniqueId))})");
        output.WriteLine($"Asignaciones en el JSON: {project.ResourceAssignments.Count}; se crearían {resourcePlan.Assignments.Count}; omitidas {resourcePlan.SkippedAssignments.Count}");
        foreach (var group in resourcePlan.SkippedAssignments.GroupBy(s => s.Reason)) output.WriteLine($"  omitidas {group.Count(),4}: {group.Key}");
        if (resourcePlan.Resources.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Mapping MPP Resource -> miembro genérico del equipo -> asignaciones a tareas (el GUID del proyecto es un marcador):");
            foreach (var r in resourcePlan.Resources.Take(maxRows))
            {
                var tasksOf = resourcePlan.Assignments.Where(a => a.Resource.Source.UniqueId == r.Source.UniqueId).Select(a => a.Task.Source.UniqueId);
                output.WriteLine($"  MPP {r.Source.UniqueId,-5} '{Trim(r.Source.Name ?? "-", 40)}' -> miembro {r.ProjectTeamMemberId:D} (puesto '{ResourceEntityBuilder.PositionName(r.Source)}', rol {r.RoleId:D}) -> tareas {string.Join(", ", tasksOf)}");
            }
        }
        if (resourcePlan.Assignments.FirstOrDefault() is { } sampleAssignment && settings.GenericBookableResourceId is not null && settings.DefaultResourcingUnitId is not null)
        {
            var placeholderProject = Guid.Empty;
            output.WriteLine();
            output.WriteLine("Miembro genérico del equipo (msdyn_CreateTeamMemberV1, cuerpo { \"TeamMember\": ... }):");
            output.WriteLine(ResourceEntityBuilder.TeamMember(sampleAssignment.Resource, placeholderProject, settings).ToJsonString(JsonDefaults.Options));
            output.WriteLine("Asignación (Scheduling API, msdyn_PssCreateV2):");
            output.WriteLine(ResourceEntityBuilder.Assignment(sampleAssignment, placeholderProject, sampleAssignment.Resource.ProjectTeamMemberId).ToJsonString(JsonDefaults.Options));
        }

        var candidates = project.Tasks
            .Where(t => !t.IsNull && !t.IsProjectSummaryTask && !t.IsSummary && t.Start is not null && t.Finish is not null && t.DurationValue > 0)
            .Select(t => (Task: t, Crossed: TaskCrossing.Crossed(t, nonWorking)))
            .Where(x => x.Crossed.Count > 0)
            .OrderBy(x => (x.Task.Finish!.Value - x.Task.Start!.Value).TotalDays)
            .Take(crossingCandidates)
            .ToList();
        output.WriteLine();
        output.WriteLine($"Tareas (no resumen, duración > 0) que atraviesan algún día no laborable de las CalendarExceptions; las más cortas primero ({candidates.Count} mostradas):");
        foreach (var (task, crossed) in candidates)
            output.WriteLine($"  UniqueId {task.UniqueId,-6} {task.Start:yyyy-MM-dd} -> {task.Finish:yyyy-MM-dd}  {task.DurationValue?.ToString(CultureInfo.InvariantCulture)} {task.DurationUnits}  " +
                             $"cruza {crossed.Count}: {string.Join(", ", crossed.Take(4).Select(c => c.ToString("yyyy-MM-dd")))}  | {Trim(task.Name, 40)}");
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
