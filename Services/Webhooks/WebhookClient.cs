using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using fuseraft.Web.Models;

namespace fuseraft.Web.Services.Webhooks;

/// <summary>
/// Posts a notification to a webhook, signed the Standard Webhooks way (<c>webhook-id</c>, <c>webhook-timestamp</c>,
/// <c>webhook-signature</c>), so receivers can check it came from this server with any Standard Webhooks library.
/// Redirects aren't followed, and private addresses are refused unless allowed (<see cref="WebhookAddress"/>).
/// </summary>
public sealed class WebhookClient : IDisposable
{
    // Bodies are JSON for machines, never embedded in HTML, so there's no call to escape quotes and angle brackets.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private const int MaxErrorBody = 200;

    private readonly HttpClient _http;

    public WebhookClient(IOptions<WebhookOptions> options) : this(Guarded(options.Value), options.Value) { }

    internal WebhookClient(HttpMessageHandler handler, WebhookOptions options)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("fuseraft-web");
    }

    private static SocketsHttpHandler Guarded(WebhookOptions options) => new()
    {
        AllowAutoRedirect = false,
        // A proxy would make the connect-time check judge the proxy's address instead of the webhook's.
        UseProxy          = false,
        ConnectCallback   = (context, ct) => WebhookAddress.ConnectAsync(context.DnsEndPoint, options, ct),
    };

    /// <summary>Sends once; the result says whether to try again.</summary>
    public async Task<(WebhookDelivery Delivery, bool Retry)> SendAsync(Webhook hook, string secret, AppNotification n,
        string? publicUrl, CancellationToken ct)
    {
        var body = Payload(hook.Format, n, publicUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var id        = n.Id.ToString("N");
        var timestamp = n.At.ToUnixTimeSeconds().ToString();
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", Sign(secret, id, timestamp, body));

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return (new(DateTimeOffset.UtcNow, n.Title, status, null), false);
            var text = await response.Content.ReadAsStringAsync(ct);
            var error = text.Length > MaxErrorBody ? text[..MaxErrorBody] + "…" : text;
            var retry = status >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
            return (new(DateTimeOffset.UtcNow, n.Title, status, string.IsNullOrWhiteSpace(error) ? response.ReasonPhrase ?? "Failed" : error), retry);
        }
        catch (HttpRequestException ex)
        {
            return (new(DateTimeOffset.UtcNow, n.Title, null, Innermost(ex)), true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new(DateTimeOffset.UtcNow, n.Title, null, $"No answer within {_http.Timeout.TotalSeconds:0} seconds"), true);
        }
    }

    internal static string Sign(string secret, string id, string timestamp, string body)
    {
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
    }

    internal static string Payload(WebhookFormat format, AppNotification n, string? publicUrl)
    {
        var link = publicUrl is null ? n.Link : publicUrl + n.Link;
        if (format == WebhookFormat.Slack)
        {
            var text = $"*{Escape(n.Title)}*";
            if (n.Body.Length > 0) text += "\n" + Escape(n.Body);
            if (publicUrl is not null) text += $"\n<{link}|Open in fuseraft>";
            return JsonSerializer.Serialize(new { text }, Json);
        }
        return JsonSerializer.Serialize(new
        {
            type     = Type(n.Kind),
            id       = n.Id.ToString("N"),
            at       = n.At,
            title    = n.Title,
            body     = n.Body,
            link,
            category = n.Category,
            level    = n.Level,
        }, Json);
    }

    internal static string Type(NotificationKind kind) => kind switch
    {
        NotificationKind.Approval        => "approval",
        NotificationKind.Question        => "question",
        NotificationKind.SessionFinished => "session.finished",
        NotificationKind.SessionFailed   => "session.failed",
        NotificationKind.ChatReplied     => "chat.replied",
        NotificationKind.ChatFailed      => "chat.failed",
        NotificationKind.TaskFinished    => "task.finished",
        NotificationKind.TaskFailed      => "task.failed",
        NotificationKind.JobFinished     => "job.finished",
        NotificationKind.JobFailed       => "job.failed",
        NotificationKind.Test            => "test",
        _                                => kind.ToString().ToLowerInvariant(),
    };

    // Slack reads these three as markup.
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    public void Dispose() => _http.Dispose();
}
