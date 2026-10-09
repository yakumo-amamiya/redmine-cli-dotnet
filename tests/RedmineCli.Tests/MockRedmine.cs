using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RedmineCli.Tests;

/// <summary>A request the mock received.</summary>
public sealed record RecordedRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Raw,
    JsonNode? Json,
    string? ClientCertificateSubject);

/// <summary>What a route answers: JSON, or a body with headers.</summary>
public sealed record MockResponse(
    int Status = 200, JsonNode? Json = null, string? Body = null, IReadOnlyDictionary<string, string>? Headers = null, byte[]? Bytes = null);

/// <summary>
/// The smallest Redmine stand-in: routes keyed by "METHOD /path", and every request recorded.
/// Unknown routes answer 404 with {}.
/// </summary>
public sealed class MockRedmine : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<RecordedRequest> _requests = [];

    private MockRedmine(WebApplication app)
    {
        _app = app;
    }

    public ConcurrentDictionary<string, Func<RecordedRequest, MockResponse>> Routes { get; } = new();

    public string BaseUrl { get; private set; } = "";

    public int Port => new Uri(BaseUrl).Port;

    public static async Task<MockRedmine> StartAsync(Action<HttpsConnectionAdapterOptions>? https = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (https is not null)
            {
                listen.UseHttps(https);
            }
        }));
        var app = builder.Build();
        var mock = new MockRedmine(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        mock.BaseUrl = address.TrimEnd('/');
        return mock;
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void ClearRequests()
    {
        lock (_requests)
        {
            _requests.Clear();
        }
    }

    public List<RecordedRequest> Hit(string method, string path) => Requests.Where(r => r.Method == method && r.Path == path).ToList();

    public void Route(string key, Func<RecordedRequest, MockResponse> handler) => Routes[key] = handler;

    public void Route(string key, string json, int status = 200) => Routes[key] = _ => new MockResponse(status, JsonNode.Parse(json));

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var raw = buffer.ToArray();
        JsonNode? json = null;
        try
        {
            json = raw.Length == 0 ? null : JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            json = null;
        }
        var entry = new RecordedRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "",
            context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
            context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
            raw,
            json,
            context.Connection.ClientCertificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        lock (_requests)
        {
            _requests.Add(entry);
        }
        if (!Routes.TryGetValue($"{entry.Method} {entry.Path}", out var handler))
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{}");
            return;
        }
        var result = handler(entry);
        context.Response.StatusCode = result.Status;
        if (result.Json is not null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(result.Json.ToJsonString());
            return;
        }
        foreach (var (key, value) in result.Headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[key] = value;
        }
        if (result.Body is not null)
        {
            await context.Response.WriteAsync(result.Body);
        }
        if (result.Bytes is not null)
        {
            await context.Response.Body.WriteAsync(result.Bytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
