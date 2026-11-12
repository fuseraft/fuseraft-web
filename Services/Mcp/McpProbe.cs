using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace fuseraft.Web.Services.Mcp;

public sealed record McpProbeResult(bool Ok, string? ServerName, IReadOnlyList<string> Tools, string? Error)
{
    public static McpProbeResult Failed(string error) => new(false, null, [], error);
}

/// <summary>
/// Checks an MCP server the way fuseraft will reach it: a Streamable HTTP <c>initialize</c>, then <c>tools/list</c>,
/// with the server's token. Only admins run it, against URLs they're adding.
/// </summary>
public sealed class McpProbe : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const int MaxToolPages = 20;

    private readonly HttpClient _http;

    public McpProbe() : this(new SocketsHttpHandler { AllowAutoRedirect = false }) { }

    internal McpProbe(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("fuseraft-web");
    }

    public async Task<McpProbeResult> ProbeAsync(string url, string? token, CancellationToken ct = default)
    {
        try
        {
            var init = await CallAsync(url, token, session: null, version: null, id: 1, "initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"]    = new JsonObject(),
                ["clientInfo"]      = new JsonObject { ["name"] = "fuseraft-web", ["version"] = "1" },
            }, ct);
            if (init.Error is not null) return McpProbeResult.Failed(init.Error);

            var session    = init.Session;
            var version    = init.Result?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion;
            var serverName = init.Result?["serverInfo"]?["name"]?.GetValue<string>();

            await NotifyInitializedAsync(url, token, session, version, ct);

            var tools  = new List<string>();
            string? cursor = null;
            for (var page = 0; page < MaxToolPages; page++)
            {
                var list = await CallAsync(url, token, session, version, id: 2 + page, "tools/list",
                    cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, ct);
                if (list.Error is not null) return McpProbeResult.Failed(list.Error);
                foreach (var t in list.Result?["tools"]?.AsArray() ?? [])
                    if (t?["name"]?.GetValue<string>() is { } name) tools.Add(name);
                cursor = list.Result?["nextCursor"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cursor)) break;
            }

            if (session is not null) await EndSessionAsync(url, token, session, ct);
            return new McpProbeResult(true, serverName, tools, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpProbeResult.Failed("The server didn't answer within 15 seconds.");
        }
        catch (HttpRequestException ex)
        {
            return McpProbeResult.Failed($"Couldn't reach it: {ex.Message}");
        }
    }

    private async Task<(JsonNode? Result, string? Session, string? Error)> CallAsync(string url, string? token,
        string? session, string? version, int id, string method, JsonObject @params, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params };
        using var request = Request(url, token, session, version, body);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (Problem(response) is { } problem) return (null, null, problem);

        var text = await response.Content.ReadAsStringAsync(ct);
        var message = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? FromEventStream(text, id)
            : TryParse(text);
        if (message is null)
            return (null, null, $"Its answer to {method} isn't an MCP message. Check that the URL ends at the MCP endpoint (often /mcp).");
        if (message["error"] is { } error)
            return (null, null, $"It refused {method}: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");

        var newSession = response.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.FirstOrDefault() : null;
        return (message["result"], newSession ?? session, null);
    }

    private async Task NotifyInitializedAsync(string url, string? token, string? session, string version, CancellationToken ct)
    {
        using var request  = Request(url, token, session, version,
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        using var response = await _http.SendAsync(request, ct);
    }

    private async Task EndSessionAsync(string url, string? token, string session, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Add("Mcp-Session-Id", session);
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException) { } // the server may not support ending sessions; nothing hangs on it
    }

    private static HttpRequestMessage Request(string url, string? token, string? session, string? version, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        if (version is not null) request.Headers.Add("MCP-Protocol-Version", version);
        return request;
    }

    private static string? Problem(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"It refused the token ({(int)response.StatusCode}). Check the token, or add one if the server needs it.",
        HttpStatusCode.NotFound => "Nothing answers at that URL (404). Check the path; many servers use /mcp.",
        HttpStatusCode.MethodNotAllowed => "That URL doesn't take MCP requests (405). It may only speak the older SSE transport.",
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
            $"It redirected ({(int)response.StatusCode}) to {response.Headers.Location}. Use that URL instead.",
        _ when !response.IsSuccessStatusCode => $"It answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        _ => null,
    };

    /// <summary>The JSON-RPC response with <paramref name="id"/> among a Server-Sent Events body's <c>data:</c> events.</summary>
    internal static JsonNode? FromEventStream(string text, int id)
    {
        var data = new StringBuilder();
        foreach (var raw in text.Split('\n').Append(""))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                if (TryParse(data.ToString()) is { } m && m["id"]?.GetValueKind() == JsonValueKind.Number && m["id"]!.GetValue<int>() == id)
                    return m;
                data.Clear();
            }
        }
        return null;
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
