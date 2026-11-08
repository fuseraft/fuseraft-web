using System.Net;
using System.Text.Json;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Webhooks;

namespace FuseraftWeb.Tests;

public sealed class WebhookClientTests
{
    private static readonly AppNotification Failed = new(NotificationKind.JobFailed, "bob", "Scheduled job nightly failed (exit 3)",
        "audit <the> repo & more", "/schedule");

    [Fact]
    public void Sign_MatchesTheStandardWebhooksExample()
    {
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=",
            WebhookClient.Sign("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", """{"test": 2432232314}"""));
    }

    [Fact]
    public void JsonPayload_SaysWhatHappenedAndLinksBack()
    {
        using var doc = JsonDocument.Parse(WebhookClient.Payload(WebhookFormat.Json, Failed, "https://fuseraft.example.com"));
        var p = doc.RootElement;
        Assert.Equal(("job.failed", "https://fuseraft.example.com/schedule", "results", "err"),
            (p.GetProperty("type").GetString(), p.GetProperty("link").GetString(), p.GetProperty("category").GetString(), p.GetProperty("level").GetString()));
        Assert.Equal(Failed.Id.ToString("N"), p.GetProperty("id").GetString());
    }

    [Fact]
    public void SlackPayload_EscapesItsMarkup()
    {
        using var doc = JsonDocument.Parse(WebhookClient.Payload(WebhookFormat.Slack, Failed, "https://f.example.com"));
        Assert.Equal("*Scheduled job nightly failed (exit 3)*\naudit &lt;the&gt; repo &amp; more\n<https://f.example.com/schedule|Open in fuseraft>",
            doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void EveryKind_HasADottedType()
    {
        Assert.All(Enum.GetValues<NotificationKind>(), k => Assert.Matches("^[a-z]+(\\.[a-z]+)?$", WebhookClient.Type(k)));
    }

    [Fact]
    public async Task Send_SignsTheExactBodyItPosts()
    {
        var handler = new FakeHandler(HttpStatusCode.NoContent);
        using var client = new WebhookClient(handler, new WebhookOptions());
        var hook = new Webhook { OwnerId = "bob", Url = "https://hooks.example.com/x" };

        var (delivery, retry) = await client.SendAsync(hook, "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", Failed, null, default);

        Assert.True(delivery.Succeeded);
        Assert.False(retry);
        var (request, body) = Assert.Single(handler.Requests);
        var id = request.Headers.GetValues("webhook-id").Single();
        var ts = request.Headers.GetValues("webhook-timestamp").Single();
        Assert.Equal(WebhookClient.Sign("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", id, ts, body), request.Headers.GetValues("webhook-signature").Single());
        Assert.Equal(Failed.At.ToUnixTimeSeconds().ToString(), ts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Found, false)]
    public async Task Send_RetriesOnlyWhatMightWorkLater(HttpStatusCode status, bool expected)
    {
        using var client = new WebhookClient(new FakeHandler(status), new WebhookOptions());
        var (delivery, retry) = await client.SendAsync(new Webhook { OwnerId = "b", Url = "https://h.example.com" }, "whsec_AAAA", Failed, null, default);

        Assert.False(delivery.Succeeded);
        Assert.Equal((int)status, delivery.Status);
        Assert.Equal(expected, retry);
    }

    [Fact]
    public async Task TheRealClient_WontReachThisMachine()
    {
        using var client = new WebhookClient(Microsoft.Extensions.Options.Options.Create(new WebhookOptions()));
        var (delivery, retry) = await client.SendAsync(new Webhook { OwnerId = "b", Url = "https://localhost:9/x" }, "whsec_AAAA", Failed, null, default);

        Assert.Null(delivery.Status);
        Assert.Contains("private network", delivery.Error);
        Assert.True(retry);
    }
}

internal sealed class FakeHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
{
    private int _calls;
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        int call;
        lock (Requests) { Requests.Add((request, body)); call = _calls++; }
        return new HttpResponseMessage(statuses[Math.Min(call, statuses.Length - 1)]);
    }
}
