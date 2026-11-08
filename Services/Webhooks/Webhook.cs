using System.Text.Json.Serialization;

namespace fuseraft.Web.Services.Webhooks;

[JsonConverter(typeof(JsonStringEnumConverter<WebhookFormat>))]
public enum WebhookFormat { Json, Slack }

/// <summary>A URL that's sent its owner's notifications. The signing secret lives in <see cref="WebhookStore"/>.</summary>
public sealed class Webhook
{
    public string Id      { get; init; } = Guid.NewGuid().ToString("N");
    public required string OwnerId { get; init; }
    public string Name    { get; set; } = "";
    /// <summary>Saved encrypted, since some (Slack's, Discord's) are themselves the credential.</summary>
    [JsonIgnore] public string Url { get; set; } = "";
    public WebhookFormat Format { get; set; }
    public bool Enabled   { get; set; } = true;
    /// <summary>Approvals and questions, sent the moment they're asked.</summary>
    public bool Attention { get; set; } = true;
    /// <summary>Sessions, daemon tasks and scheduled jobs that finish or fail.</summary>
    public bool Results   { get; set; } = true;
    public bool Replies   { get; set; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    public bool Wants(string category) => category switch
    {
        "attention" => Attention,
        "replies"   => Replies,
        _           => Results,
    };

    internal Webhook Copy() => (Webhook)MemberwiseClone();
}

/// <summary>One attempt to deliver a notification to a webhook.</summary>
public sealed record WebhookDelivery(DateTimeOffset At, string Event, int? Status, string? Error)
{
    public bool Succeeded => Error is null && Status is >= 200 and < 300;
}
