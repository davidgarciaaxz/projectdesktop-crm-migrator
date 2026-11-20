using Microsoft.Identity.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectOpsExcelImporter;

public sealed class DelegatedTokenProvider
{
    private readonly AppSettings _settings;
    private readonly IPublicClientApplication _app;

    public DelegatedTokenProvider(AppSettings settings)
    {
        _settings = settings;
        _app = PublicClientApplicationBuilder.Create(settings.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, settings.TenantId)
            .WithRedirectUri(settings.RedirectUri)
            .Build();
    }

    private static void PrintTokenDiagnostics(AuthenticationResult result)
{
    Console.WriteLine("Identidad autenticada:");
    Console.WriteLine($"  Username: {result.Account?.Username}");
    Console.WriteLine($"  HomeAccountId: {result.Account?.HomeAccountId?.Identifier}");
    Console.WriteLine($"  TenantId: {result.TenantId}");
    Console.WriteLine($"  ExpiresOn: {result.ExpiresOn:O}");

    var parts = result.AccessToken.Split('.');

    if (parts.Length < 2)
    {
        Console.WriteLine("  No se pudo interpretar el access token como JWT.");
        return;
    }

    var payload = parts[1]
        .Replace('-', '+')
        .Replace('_', '/');

    payload = payload.PadRight(
        payload.Length + ((4 - payload.Length % 4) % 4),
        '=');

    var json = Encoding.UTF8.GetString(
        Convert.FromBase64String(payload));

    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;

    PrintClaim(root, "aud");
    PrintClaim(root, "tid");
    PrintClaim(root, "oid");
    PrintClaim(root, "preferred_username");
    PrintClaim(root, "upn");
    PrintClaim(root, "scp");
}

    private static void PrintClaim(JsonElement root, string claimName)
    {
        var value = root.TryGetProperty(claimName, out var claim)
            ? claim.ToString()
            : "(no presente)";

        Console.WriteLine($"  {claimName}: {value}");
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{_settings.EnvironmentUrl.TrimEnd('/')}/user_impersonation" };
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        try
        {
            var account = accounts.FirstOrDefault();
            if (account is not null)
                return (await _app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
        }
        catch (MsalUiRequiredException) { }

       // AuthenticationResult result;
       // if (_settings.AuthMode.Equals("DeviceCode", StringComparison.OrdinalIgnoreCase))
       // {
       //     result = await _app.AcquireTokenWithDeviceCode(scopes, code =>
       //     {
       //         Console.WriteLine(code.Message);
        //        return Task.CompletedTask;
       //     }).ExecuteAsync(cancellationToken).ConfigureAwait(false);
       // }
       // else
       // {
        //    result = await _app.AcquireTokenInteractive(scopes)
         //       .WithPrompt(Prompt.SelectAccount)
          //      .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        //} 

        AuthenticationResult result;

    if (_settings.AuthMode.Equals(
        "DeviceCode",
        StringComparison.OrdinalIgnoreCase))
    {
        result = await _app.AcquireTokenWithDeviceCode(
            scopes,
            code =>
            {
                Console.WriteLine(code.Message);
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
    else
    {
        result = await _app
            .AcquireTokenInteractive(scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }


        PrintTokenDiagnostics(result);
        return result.AccessToken;
    }
}

public sealed class DataverseClient : IDisposable
{
    private readonly DelegatedTokenProvider _tokens;
    private readonly HttpClient _http;
private readonly AppSettings _settings;  
    public DataverseClient(AppSettings settings)
    {
        _settings = settings;
        _tokens = new DelegatedTokenProvider(settings);
        _http = new HttpClient { BaseAddress = new Uri($"{settings.EnvironmentUrl.TrimEnd('/')}/api/data/{settings.ApiVersion.Trim('/')}/") };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("OData-Version", "4.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("OData-MaxVersion", "4.0");
    }

    public async Task<JsonObject> PostActionAsync(string action, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new StringContent(body.ToJsonString(JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonObject> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // public async Task<bool> RecordExistsAsync(string entitySet, Guid id, CancellationToken cancellationToken)
    // {
    //     using var request = new HttpRequestMessage(HttpMethod.Get, $"{entitySet}({id:D})?$select={PrimaryKeyFor(entitySet)}");
    //     var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
    //     request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    //     using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    //     if (response.StatusCode == HttpStatusCode.NotFound) return false;
    //     await EnsureSuccessAsync(response, request, cancellationToken).ConfigureAwait(false);
    //     return true;
    // }

        public async Task<bool> RecordExistsAsync(
        string entitySet,
        Guid id,
        CancellationToken cancellationToken)
    {
        var relativeUrl =
            $"{entitySet}({id:D})?$select={PrimaryKeyFor(entitySet)}";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request =
                new HttpRequestMessage(HttpMethod.Get, relativeUrl);

            var token = await _tokens
                .GetTokenAsync(cancellationToken)
                .ConfigureAwait(false);

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;

            if (response.IsSuccessStatusCode)
                return true;

            var transient =
                response.StatusCode == HttpStatusCode.InternalServerError ||
                response.StatusCode == HttpStatusCode.BadGateway ||
                response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.GatewayTimeout ||
                (int)response.StatusCode == 429;

            if (!transient || attempt == 3)
            {
                await EnsureSuccessAsync(
                    response,
                    request,
                    cancellationToken).ConfigureAwait(false);

                return false;
            }

            var seconds = attempt * 5;

            Console.WriteLine(
                $"Dataverse devolvió {(int)response.StatusCode} " +
                $"consultando {entitySet}({id:D}). " +
                $"Reintento {attempt}/3 en {seconds}s...");

            await Task.Delay(
                TimeSpan.FromSeconds(seconds),
                cancellationToken);
        }

        return false;
    }

    public async Task WaitForRecordAsync(string entitySet, Guid id, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_settings.PersistTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await RecordExistsAsync(entitySet, id, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(TimeSpan.FromSeconds(_settings.PersistPollSeconds), cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"El registro {entitySet}({id:D}) no se confirmó antes del timeout.");
    }

    private async Task<JsonObject> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Dataverse devolvió {(int)response.StatusCode} {response.ReasonPhrase} en {request.Method} {request.RequestUri}. Respuesta: {body}", null, response.StatusCode);
    }

    private static string PrimaryKeyFor(string entitySet) => entitySet switch
    {
        "msdyn_projecttasks" => "msdyn_projecttaskid",
        "msdyn_projecttaskdependencies" => "msdyn_projecttaskdependencyid",
        "msdyn_resourceassignments" => "msdyn_resourceassignmentid",
        "msdyn_projectteams" => "msdyn_projectteamid",
        "msdyn_projects" => "msdyn_projectid",
        _ => throw new ArgumentOutOfRangeException(nameof(entitySet), entitySet, "Entity set no reconocido.")
    };

    public void Dispose() => _http.Dispose();
}

public static class JsonHelpers
{
    public static Guid RequiredGuid(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var property = obj.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property.Value is not null && Guid.TryParse(property.Value.ToString(), out var guid)) return guid;
        }
        throw new InvalidDataException($"La respuesta no contiene un GUID en: {string.Join(", ", names)}. Respuesta: {obj.ToJsonString()}");
    }

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        DateTime dt => JsonValue.Create(dt.ToString("O")),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O")),
        DateOnly date => JsonValue.Create(date.ToString("yyyy-MM-dd")),
        bool b => JsonValue.Create(b),
        byte b => JsonValue.Create(b),
        short s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create(f),
        double d => JsonValue.Create(d),
        decimal d => JsonValue.Create(d),
        Guid g => JsonValue.Create(g.ToString("D")),
        _ => JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))
    };
}
