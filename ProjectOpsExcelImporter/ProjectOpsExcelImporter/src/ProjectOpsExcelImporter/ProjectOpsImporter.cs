using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter;

public sealed class ProjectOpsImporter
{
    private readonly AppSettings _settings;
    private readonly DataverseClient _client;
    /// <summary>Espacio de nombres de los GUID deterministas (tareas, dependencias, asignaciones). Compartido con la fase de tareas del import de calendarios.</summary>
    public static readonly Guid IdNamespace = Guid.Parse("B06E3CB9-23CD-4378-9C9A-57BA17EB3A1D");
    private readonly Guid _idNamespace = IdNamespace;

    public ProjectOpsImporter(AppSettings settings, DataverseClient client)
    {
        _settings = settings;
        _client = client;
    }

    public async Task<ImportState> ImportAsync(
        WorkbookProject workbook,
        ResourceMap resourceMap,
        string statePath,
        CancellationToken cancellationToken)
    {
        var state = ImportState.Load(statePath) ?? new ImportState();
        EnsureDeterministicIds(workbook, state);

        if (state.ProjectId == Guid.Empty)
        {
            Console.WriteLine("Creando proyecto y bucket predeterminado...");
            state.ProjectId = await CreateProjectAsync(workbook.Project, cancellationToken).ConfigureAwait(false);
            state.BucketId = await GetDefaultBucketAsync(state.ProjectId, cancellationToken).ConfigureAwait(false);
            state.Save(statePath);
        }

        await CreateTasksAsync(workbook, state, statePath, cancellationToken)
            .ConfigureAwait(false);

        if (_settings.ImportDependencies)
            await CreateDependenciesAsync(workbook, state, statePath, cancellationToken)
                .ConfigureAwait(false);

        if (_settings.ImportResources)
            await CreateTeamMembersAsync(workbook, resourceMap, state, statePath, cancellationToken)
                .ConfigureAwait(false);

        if (_settings.ImportAssignments)
            await CreateAssignmentsAsync(workbook, state, statePath, cancellationToken)
                .ConfigureAwait(false);

        if (_settings.ImportProgress)
            await UpdateProgressAsync(workbook, state, statePath, cancellationToken)
                .ConfigureAwait(false);
                return state;
            }

    private void EnsureDeterministicIds(WorkbookProject workbook, ImportState state)
    {
        foreach (var task in workbook.Tasks)
            state.TaskIds.TryAdd(task.SourceId, DeterministicGuid.Create(_idNamespace, $"{workbook.Project.SourceGuid:D}:task:{task.SourceGuid:D}"));
    }

    private async Task<Guid> CreateProjectAsync(ProjectRow project, CancellationToken cancellationToken)
    {
        var entity = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_project",
            ["msdyn_subject"] = project.Name
        };
        if (project.Start.HasValue) entity["msdyn_scheduledstart"] = AtTime(project.Start.Value, 9);
        ApplyMappings(entity, project.RawValues, _settings.ProjectColumnMappings);
        var response = await _client.PostActionAsync("msdyn_CreateProjectV1", new JsonObject { ["Project"] = entity }, cancellationToken).ConfigureAwait(false);
        return JsonHelpers.RequiredGuid(response, "ProjectId", "msdyn_projectid");
    }

    private async Task<Guid> GetDefaultBucketAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString($"_msdyn_project_value eq {projectId:D}");
        var response = await _client.GetAsync($"msdyn_projectbuckets?$select=msdyn_projectbucketid&$filter={filter}&$top=1", cancellationToken).ConfigureAwait(false);
        var values = response["value"]?.AsArray() ?? throw new InvalidDataException("Dataverse no devolvió el array value al buscar el bucket.");
        if (values.Count == 0) throw new InvalidDataException("No se encontró el bucket predeterminado del proyecto.");
        return JsonHelpers.RequiredGuid(values[0]!.AsObject(), "msdyn_projectbucketid");
    }

    // private async Task CreateTasksAsync(WorkbookProject workbook, ImportState state, string statePath, CancellationToken cancellationToken)
    // {
    //     var query = workbook.Tasks
    //     .Where(t => !state.CreatedTasks.Contains(t.SourceId))
    //     .OrderBy(t => t.ExcelRow);

    //     var pending = _settings.MaxTasksToImport.HasValue
    //         ? query.Take(_settings.MaxTasksToImport.Value).ToList()
    //         : query.ToList();
    // foreach (var batch in pending.Chunk(_settings.BatchSize))
    //     {
    //         var entities = batch.Select(t => BuildTaskEntity(t, state)).ToList();
    //         Console.WriteLine($"Creando {entities.Count} tareas...");
    //         await ExecuteCreateBatchAsync(state.ProjectId, entities, cancellationToken).ConfigureAwait(false);
    //         var last = batch[^1];
    //         await _client.WaitForRecordAsync("msdyn_projecttasks", state.TaskIds[last.SourceId], cancellationToken).ConfigureAwait(false);
    //         foreach (var task in batch) state.CreatedTasks.Add(task.SourceId);
    //         state.Save(statePath);
    //     }
    // }

    private async Task CreateTasksAsync(
        WorkbookProject workbook,
        ImportState state,
        string statePath,
        CancellationToken cancellationToken)
    {
        // Primero limitamos el alcance de la PoC.
        // Con MaxTasksToImport = 5 siempre trabajaremos
        // exclusivamente con las primeras 5 tareas del Excel.
        var scope = _settings.MaxTasksToImport.HasValue
            ? workbook.Tasks
                .OrderBy(t => t.ExcelRow)
                .Take(_settings.MaxTasksToImport.Value)
                .ToList()
            : workbook.Tasks
                .OrderBy(t => t.ExcelRow)
                .ToList();

        var pending = new List<ProjectTaskRow>();

        foreach (var task in scope)
        {
            // Si nuestro state ya sabe que fue creada, no hacemos nada.
            if (state.CreatedTasks.Contains(task.SourceId))
                continue;

            var taskId = state.TaskIds[task.SourceId];

            // Reconciliamos con Dataverse por si una ejecución anterior
            // creó la tarea pero falló antes de actualizar import-state.
            var exists = await _client.RecordExistsAsync(
                "msdyn_projecttasks",
                taskId,
                cancellationToken
            ).ConfigureAwait(false);

            if (exists)
            {
                Console.WriteLine(
                    $"Tarea {task.SourceId} ya existe: " +
                    $"{task.Name} ({taskId:D}). Se recupera el estado.");

                state.CreatedTasks.Add(task.SourceId);
                state.Save(statePath);

                continue;
            }

            pending.Add(task);
        }

        foreach (var batch in pending.Chunk(_settings.BatchSize))
        {
            var entities = batch
                .Select(t => BuildTaskEntity(t, state))
                .ToList();

            Console.WriteLine($"Creando {entities.Count} tareas...");


            Console.WriteLine();
            Console.WriteLine($"Creando lote de {batch.Length} tareas:");

            foreach (var task in batch)
            {
                var parentText = task.ParentSourceId.HasValue
                    ? task.ParentSourceId.Value.ToString()
                    : "-";

                Console.WriteLine(
                    $"  Id={task.SourceId,-4} " +
                    $"Esquema={task.OutlineNumber,-14} " +
                    $"Nivel={task.OutlineLevel,-2} " +
                    $"Padre={(task.ParentSourceId?.ToString() ?? "-"),-4} " +
                    $"Dur={task.DurationDays,8:0.00} " +
                    $"Work={task.WorkHours,10:0.00} " +
                    $"Nombre={task.Name}");
                
            }


            await ExecuteCreateBatchAsync(
                state.ProjectId,
                entities,
                cancellationToken
            ).ConfigureAwait(false);

            var last = batch[^1];

            await _client.WaitForRecordAsync(
                "msdyn_projecttasks",
                state.TaskIds[last.SourceId],
                cancellationToken
            ).ConfigureAwait(false);

            foreach (var task in batch)
                state.CreatedTasks.Add(task.SourceId);

            state.Save(statePath);
        }
    }


    private JsonObject BuildTaskEntity(ProjectTaskRow task, ImportState state)
    {
        var entity = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask",
            ["msdyn_projecttaskid"] = state.TaskIds[task.SourceId].ToString("D"),
            ["msdyn_subject"] = task.Name,
            ["msdyn_project@odata.bind"] = $"/msdyn_projects({state.ProjectId:D})",
            ["msdyn_projectbucket@odata.bind"] = $"/msdyn_projectbuckets({state.BucketId:D})",
            ["msdyn_LinkStatus"] = 192350000,
            ["msdyn_outlinelevel"] = task.OutlineLevel
        };
        if (task.ParentSourceId.HasValue)
            entity["msdyn_parenttask@odata.bind"] = $"/msdyn_projecttasks({state.TaskIds[task.ParentSourceId.Value]:D})";

        if (!task.IsSummary)
        {
            if (task.Start.HasValue) entity["msdyn_scheduledstart"] = AtTime(task.Start.Value, 9);
            if (_settings.IncludeScheduledEnd && task.Finish.HasValue) entity["msdyn_scheduledend"] = AtTime(task.Finish.Value, 17);
            entity["msdyn_duration"] = Math.Round(task.DurationDays,2,MidpointRounding.AwayFromZero);
            entity["msdyn_effort"] = Math.Round(task.WorkHours,2,MidpointRounding.AwayFromZero);
        }
        ApplyMappings(entity, task.RawValues, _settings.TaskColumnMappings);
        return entity;
    }

/*     private async Task CreateDependenciesAsync(WorkbookProject workbook, ImportState state, string statePath, CancellationToken cancellationToken)
    {
        var pending = workbook.Tasks.SelectMany(t => t.Dependencies)
            .Select(d => (Dependency: d, Id: DependencyId(workbook.Project.SourceGuid, d)))
            .Where(x => !state.CreatedDependencies.Contains(x.Id))
            .ToList();
        foreach (var batch in pending.Chunk(_settings.BatchSize))
        {
            var entities = batch.Select(x => BuildDependencyEntity(x.Dependency, x.Id, state)).ToList();
            Console.WriteLine($"Creando {entities.Count} dependencias...");
            await ExecuteCreateBatchAsync(state.ProjectId, entities, cancellationToken).ConfigureAwait(false);
            await _client.WaitForRecordAsync("msdyn_projecttaskdependencies", batch[^1].Id, cancellationToken).ConfigureAwait(false);
            foreach (var item in batch) state.CreatedDependencies.Add(item.Id);
            state.Save(statePath);
        }
    } */

    private async Task CreateDependenciesAsync(
    WorkbookProject workbook,
    ImportState state,
    string statePath,
    CancellationToken cancellationToken)
{
    var query = workbook.Tasks
        .SelectMany(t => t.Dependencies)

        // Solo relaciones cuyas dos tareas ya han sido importadas
        .Where(d =>
            state.CreatedTasks.Contains(d.PredecessorSourceId) &&
            state.CreatedTasks.Contains(d.SuccessorSourceId))

        .Select(d => (
            Dependency: d,
            Id: DependencyId(workbook.Project.SourceGuid, d)))

        // Evitar repetir dependencias ya procesadas
        .Where(x => !state.CreatedDependencies.Contains(x.Id));

    var pending = _settings.MaxDependenciesToImport.HasValue
        ? query.Take(_settings.MaxDependenciesToImport.Value).ToList()
        : query.ToList();

    foreach (var batch in pending.Chunk(_settings.BatchSize))
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Creando lote de {batch.Length} dependencias:");

        foreach (var item in batch)
        {
            var d = item.Dependency;

            Console.WriteLine(
                $"  {d.PredecessorSourceId,-4} -> " +
                $"{d.SuccessorSourceId,-4} " +
                $"Tipo={d.Type,-15} " +
                $"Lag={d.LagDays,7:0.##}d " +
                $"Excel={d.RawExpression}");
        }

        var entities = batch
            .Select(x => BuildDependencyEntity(
                x.Dependency,
                x.Id,
                state))
            .ToList();

        await ExecuteCreateBatchAsync(
            state.ProjectId,
            entities,
            cancellationToken)
            .ConfigureAwait(false);

        await _client.WaitForRecordAsync(
            "msdyn_projecttaskdependencies",
            batch[^1].Id,
            cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in batch)
            state.CreatedDependencies.Add(item.Id);

        state.Save(statePath);
    }
}

    private JsonObject BuildDependencyEntity(TaskDependency dependency, Guid id, ImportState state)
    {
        var typeKey = dependency.Type.ToString();
        if (!_settings.DependencyLinkTypeValues.TryGetValue(typeKey, out var linkType))
            throw new InvalidOperationException($"Falta DependencyLinkTypeValues.{typeKey} en la configuración.");
            var entity = new JsonObject
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttaskdependency",
                ["msdyn_projecttaskdependencyid"] = id.ToString("D"),

                ["msdyn_Project@odata.bind"] =
                    $"/msdyn_projects({state.ProjectId:D})",

                ["msdyn_PredecessorTask@odata.bind"] =
                    $"/msdyn_projecttasks({state.TaskIds[dependency.PredecessorSourceId]:D})",

                ["msdyn_SuccessorTask@odata.bind"] =
                    $"/msdyn_projecttasks({state.TaskIds[dependency.SuccessorSourceId]:D})",

                ["msdyn_linktype"] = linkType
            };
        if (_settings.IncludeDependencyDescription)
            entity["msdyn_description"] = $"Excel: {dependency.RawExpression}; lag calculado: {dependency.LagDays} días";
        if (!string.IsNullOrWhiteSpace(_settings.DependencyLagCustomField) && dependency.LagDays != 0)
            entity[_settings.DependencyLagCustomField] = dependency.LagDays;
        return entity;
    }

    private async Task CreateTeamMembersAsync(WorkbookProject workbook, ResourceMap resourceMap, ImportState state, string statePath, CancellationToken cancellationToken)
    {
        var entries = resourceMap.Resources.ToDictionary(r => r.SourceName, StringComparer.OrdinalIgnoreCase);
        var names = workbook.Tasks.Where(t => !t.IsSummary).SelectMany(t => t.Resources).Select(r => r.SourceName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (state.TeamMemberIds.ContainsKey(name)) continue;
            entries.TryGetValue(name, out var mapping);
            if (mapping?.ProjectTeamId is Guid existing)
            {
                state.TeamMemberIds[name] = existing;
                state.Save(statePath);
                continue;
            }
            if (mapping is null && !_settings.CreateMissingResourcesAsGeneric)
                throw new InvalidOperationException($"No hay mapping para el recurso '{name}'.");
            if (mapping is { CreateAsGeneric: false, BookableResourceId: null })
                throw new InvalidOperationException($"El recurso '{name}' no tiene ProjectTeamId ni BookableResourceId y CreateAsGeneric=false.");

            var teamId = DeterministicGuid.Create(_idNamespace, $"{workbook.Project.SourceGuid:D}:team:{name.ToUpperInvariant()}");
            var entity = new JsonObject
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projectteam",
                ["msdyn_projectteamid"] = teamId.ToString("D"),
                ["msdyn_project@odata.bind"] = $"/msdyn_projects({state.ProjectId:D})",
                ["msdyn_name"] = name
            };
            if (mapping?.BookableResourceId is Guid bookable)
                entity[$"{_settings.BookableResourceNavigationProperty}@odata.bind"] = $"/bookableresources({bookable:D})";
            Console.WriteLine($"Creando miembro de equipo: {name}");
            await _client.PostActionAsync("msdyn_CreateTeamMemberV1", new JsonObject { ["TeamMember"] = entity }, cancellationToken).ConfigureAwait(false);
            state.TeamMemberIds[name] = teamId;
            state.Save(statePath);
        }
    }

    private async Task CreateAssignmentsAsync(WorkbookProject workbook, ImportState state, string statePath, CancellationToken cancellationToken)
    {
        var pending = workbook.Tasks.Where(t => !t.IsSummary)
            .SelectMany(t => t.Resources.GroupBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase).Select(g => (Task: t, Resource: g.First())))
            .Select(x => (x.Task, x.Resource, Id: AssignmentId(workbook.Project.SourceGuid, x.Task.SourceGuid, x.Resource.SourceName)))
            .Where(x => !state.CreatedAssignments.Contains(x.Id))
            .ToList();
        foreach (var batch in pending.Chunk(_settings.BatchSize))
        {
            var entities = batch.Select(x => BuildAssignmentEntity(x.Task, x.Resource, x.Id, state)).ToList();
            Console.WriteLine($"Creando {entities.Count} asignaciones...");
            await ExecuteCreateBatchAsync(state.ProjectId, entities, cancellationToken).ConfigureAwait(false);
            await _client.WaitForRecordAsync("msdyn_resourceassignments", batch[^1].Id, cancellationToken).ConfigureAwait(false);
            foreach (var item in batch) state.CreatedAssignments.Add(item.Id);
            state.Save(statePath);
        }
    }

    private JsonObject BuildAssignmentEntity(ProjectTaskRow task, TaskResource resource, Guid id, ImportState state)
    {
        if (!state.TeamMemberIds.TryGetValue(resource.SourceName, out var teamId))
            throw new InvalidOperationException($"No existe miembro de equipo para '{resource.SourceName}'.");
        var assignmentName = $"{resource.SourceName} - {task.Name}";
        if (assignmentName.Length > 100) assignmentName = assignmentName[..100];
        return new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_resourceassignment",
            ["msdyn_resourceassignmentid"] = id.ToString("D"),
            ["msdyn_name"] = assignmentName,
            ["msdyn_projectid@odata.bind"] = $"/msdyn_projects({state.ProjectId:D})",
            ["msdyn_taskid@odata.bind"] = $"/msdyn_projecttasks({state.TaskIds[task.SourceId]:D})",
            ["msdyn_projectteamid@odata.bind"] = $"/msdyn_projectteams({teamId:D})"
        };
    }

    private async Task UpdateProgressAsync(WorkbookProject workbook, ImportState state, string statePath, CancellationToken cancellationToken)
    {
        var pending = workbook.Tasks.Where(t => !t.IsSummary && !state.UpdatedProgressTasks.Contains(t.SourceId))
            .Where(t => _settings.ProgressPolicy.Equals("All", StringComparison.OrdinalIgnoreCase) ? t.Progress is >= 0 and <= 1 : t.Progress is 0 or 1)
            .ToList();
        foreach (var batch in pending.Chunk(_settings.BatchSize))
        {
            var entities = batch.Select(t => new JsonObject
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.msdyn_projecttask",
                ["msdyn_projecttaskid"] = state.TaskIds[t.SourceId].ToString("D"),
                ["msdyn_progress"] = t.Progress
            }).ToList();
            Console.WriteLine($"Actualizando avance de {entities.Count} tareas...");
            await ExecuteUpdateBatchAsync(state.ProjectId, entities, cancellationToken).ConfigureAwait(false);
            foreach (var task in batch) state.UpdatedProgressTasks.Add(task.SourceId);
            state.Save(statePath);
        }
    }

    // private async Task ExecuteCreateBatchAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken cancellationToken)
    // {
    //     var operationSetId = await CreateOperationSetAsync(projectId, $"Importación Excel: crear {entities.Count} registros", cancellationToken).ConfigureAwait(false);
    //     var array = new JsonArray(); foreach (var entity in entities) array.Add(entity);
    //     await _client.PostActionAsync("msdyn_PssCreateV2", new JsonObject { ["EntityCollection"] = array, ["OperationSetId"] = operationSetId.ToString("D") }, cancellationToken).ConfigureAwait(false);
    //     await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, cancellationToken).ConfigureAwait(false);
    // }
private async Task ExecuteCreateBatchAsync(
    Guid projectId,
    IReadOnlyList<JsonObject> entities,
    CancellationToken cancellationToken)
{
    var operationSetId = await CreateOperationSetAsync(
        projectId,
        $"Importación Excel: crear {entities.Count} registros",
        cancellationToken
    ).ConfigureAwait(false);

    var array = new JsonArray();

    foreach (var entity in entities)
        array.Add(entity);

    var requestBody = new JsonObject
    {
        ["EntityCollection"] = array,
        ["OperationSetId"] = operationSetId.ToString("D")
    };

    // Console.WriteLine("JSON enviado a msdyn_PssCreateV2:");
    // Console.WriteLine(
    //     requestBody.ToJsonString(
    //         new System.Text.Json.JsonSerializerOptions
    //         {
    //             WriteIndented = true
    //         }
    //     )
    // );

    await _client.PostActionAsync(
        "msdyn_PssCreateV2",
        requestBody,
        cancellationToken
    ).ConfigureAwait(false);

    await _client.PostActionAsync(
        "msdyn_ExecuteOperationSetV1",
        new JsonObject
        {
            ["OperationSetId"] = operationSetId.ToString("D")
        },
        cancellationToken
    ).ConfigureAwait(false);
}


    private async Task ExecuteUpdateBatchAsync(Guid projectId, IReadOnlyList<JsonObject> entities, CancellationToken cancellationToken)
    {
        var operationSetId = await CreateOperationSetAsync(projectId, $"Importación Excel: actualizar {entities.Count} registros", cancellationToken).ConfigureAwait(false);
        var array = new JsonArray(); foreach (var entity in entities) array.Add(entity);
        await _client.PostActionAsync("msdyn_PssUpdateV2", new JsonObject { ["EntityCollection"] = array, ["OperationSetId"] = operationSetId.ToString("D") }, cancellationToken).ConfigureAwait(false);
        await _client.PostActionAsync("msdyn_ExecuteOperationSetV1", new JsonObject { ["OperationSetId"] = operationSetId.ToString("D") }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> CreateOperationSetAsync(Guid projectId, string description, CancellationToken cancellationToken)
    {
        var response = await _client.PostActionAsync("msdyn_CreateOperationSetV1", new JsonObject
        {
            ["ProjectId"] = projectId.ToString("D"),
            ["Description"] = description
        }, cancellationToken).ConfigureAwait(false);
        return JsonHelpers.RequiredGuid(response, "OperationSetId", "msdyn_operationsetid");
    }

    private static string AtTime(DateOnly date, int hour) => $"{date:yyyy-MM-dd}T{hour:00}:00:00";

    private static void ApplyMappings(JsonObject entity, IReadOnlyDictionary<string, object?> rawValues, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var mapping in mappings.Where(m => !string.IsNullOrWhiteSpace(m.Value)))
        {
            if (!rawValues.TryGetValue(mapping.Key, out var value) || value is null) continue;
            entity[mapping.Value] = JsonHelpers.ToNode(value);
        }
    }

    private Guid DependencyId(Guid projectSourceGuid, TaskDependency d)
        => DeterministicGuid.Create(_idNamespace, $"{projectSourceGuid:D}:dep:{d.PredecessorSourceId}:{d.SuccessorSourceId}:{d.Type}:{d.LagDays}");

    private Guid AssignmentId(Guid projectSourceGuid, Guid taskSourceGuid, string resource)
        => DeterministicGuid.Create(_idNamespace, $"{projectSourceGuid:D}:assignment:{taskSourceGuid:D}:{resource.ToUpperInvariant()}");
}
