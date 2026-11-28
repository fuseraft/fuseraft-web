namespace fuseraft.Web.Services;

/// <summary>One piece of a reply, in the order it streamed. Parts change while their reply streams.</summary>
public abstract class ReplPart;

public sealed class ReplTextPart : ReplPart
{
    public string Text { get; set; } = "";
}

/// <summary>Model reasoning, when the provider exposes it.</summary>
public sealed class ReplThinkingPart : ReplPart
{
    public string Text { get; set; } = "";
}

public sealed class ReplToolPart : ReplPart
{
    public string  Id      { get; init; } = "";
    public string  Name    { get; init; } = "";
    public string? Summary { get; init; }
    /// <summary>The call's arguments as indented JSON, or null when it had none.</summary>
    public string? Args    { get; init; }
    /// <summary>The subagent that made the call; null for the chat's own.</summary>
    public string? Agent   { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Null while the call runs.</summary>
    public bool?     Ok      { get; set; }
    public string?   Output  { get; set; }
    public TimeSpan? Elapsed { get; set; }
}

/// <summary>A <c>!command</c> and what it printed.</summary>
public sealed class ReplShellPart : ReplPart
{
    public string Command { get; init; } = "";
    public string Output  { get; set; } = "";
    public bool   Done    { get; set; }
}

/// <summary>A file a reply changed. <c>Sigil</c> is <c>A</c> (added), <c>M</c> (modified) or <c>D</c> (deleted).</summary>
public sealed record ReplFileChange(string Path, string Sigil, int Added, int Removed);

/// <summary>What a reply cost and how long it took.</summary>
public sealed record ReplUsage(long? Input, long? Output, long? Cached, TimeSpan? Elapsed);

/// <summary>The chat's status line: how full the context is (0–1) and its token totals.</summary>
public sealed record ReplStatusLine(string Model, int Turn, double Context, string Level, IReadOnlyList<string> Modes,
    long Input, long Output, long Cached);

/// <summary>A command from <c>/help</c>, as the chat's <c>ready</c> event lists them.</summary>
public sealed record ReplCommand(string Usage, string Description);

/// <summary>An image to send with the next message: its file name and a base64 data URL.</summary>
public sealed record ReplImage(string Name, string DataUrl);
