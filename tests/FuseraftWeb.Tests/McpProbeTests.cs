using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using fuseraft.Web.Services.Mcp;

namespace FuseraftWeb.Tests;

public sealed class McpProbeTests
{
    [Fact]
    public async Task Probe_InitializesAndListsEveryPageOfTools()
    {
        var server = new FakeMcpServer { EventStream = true, Pages = [["list_databases", "list_tables"], ["query_database"]] };
        using var probe = new McpProbe(server);

        var result = await probe.ProbeAsync("http://localhost:8080/mcp", "tok-123");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("sql-mcp", result.ServerName);
        Assert.Equal(["list_databases", "list_tables", "query_database"], result.Tools);
        Assert.All(server.Requests.Where(r => r.Method == HttpMethod.Post), r => Assert.Equal("Bearer tok-123", r.Authorization));
        Assert.All(server.Requests.Skip(1), r => Assert.Equal("session-1", r.Session));
        Assert.Contains(server.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Probe_ReadsPlainJsonAnswers_WithoutASession()
    {
        var server = new FakeMcpServer { EventStream = false, Session = null, Pages = [["a"]] };
        using var probe = new McpProbe(server);

        var result = await probe.ProbeAsync("http://localhost:8080/mcp", token: null);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["a"], result.Tools);
        Assert.All(server.Requests, r => Assert.Null(r.Authorization));
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the token")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "SSE")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Probe_ExplainsAnHttpFailure(HttpStatusCode status, string explanation)
    {
        using var probe = new McpProbe(new FakeMcpServer { Status = status });

        var result = await probe.ProbeAsync("http://localhost:8080/mcp", "tok");

        Assert.False(result.Ok);
        Assert.Contains(explanation, result.Error);
    }

    [Fact]
    public async Task Probe_SaysWhenTheAnswerIsntMcp()
    {
        using var probe = new McpProbe(new FakeMcpServer { RawBody = "<html>hello</html>" });

        var result = await probe.ProbeAsync("http://localhost:8080/", null);

        Assert.Contains("isn't an MCP message", result.Error);
    }

    [Fact]
    public void FromEventStream_PicksTheResponseWithTheId()
    {
        var text = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n" +
                   "data: {\"jsonrpc\":\"2.0\",\"id\":7,\r\ndata: \"result\":{}}\r\n\r\n";

        var message = McpProbe.FromEventStream(text, 7);

        Assert.NotNull(message?["result"]);
    }

    private sealed class FakeMcpServer : HttpMessageHandler
    {
        public bool EventStream { get; init; }
        public string? Session { get; init; } = "session-1";
        public List<string[]> Pages { get; init; } = [[]];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? RawBody { get; init; }
        public List<(HttpMethod Method, string? Authorization, string? Session)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Mcp-Session-Id", out var s) ? s.First() : null));
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            if (RawBody is not null) return new HttpResponseMessage { Content = new StringContent(RawBody, Encoding.UTF8, "text/html") };

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var method = body["method"]!.GetValue<string>();
            if (body["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);

            JsonObject result;
            if (method == "initialize")
                result = new JsonObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JsonObject { ["name"] = "sql-mcp" } };
            else
            {
                var page = int.TryParse(body["params"]?["cursor"]?.GetValue<string>(), out var c) ? c : 0;
                result = new JsonObject { ["tools"] = new JsonArray([.. Pages[page].Select(n => (JsonNode)new JsonObject { ["name"] = n })]) };
                if (page + 1 < Pages.Count) result["nextCursor"] = (page + 1).ToString();
            }
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.GetValue<int>(), ["result"] = result }.ToJsonString();
            var response = new HttpResponseMessage
            {
                Content = EventStream
                    ? new StringContent($"event: message\ndata: {message}\n\n", Encoding.UTF8, "text/event-stream")
                    : new StringContent(message, Encoding.UTF8, "application/json"),
            };
            if (Session is not null) response.Headers.Add("Mcp-Session-Id", Session);
            return response;
        }
    }
}
