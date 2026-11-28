namespace fuseraft.Web.Models;

// The server's side of fuseraft's wire protocol (docs/protocol.md in fuseraft-cli). These mirror the
// JSON, not fuseraft's internal types, so a fuseraft refactor can't break the server's build.

public sealed record WireSession
{
    public string    SessionId        { get; init; } = "";
    public string    Task             { get; init; } = "";
    public string?   WorkingDirectory { get; init; }
    public string?   ConfigPath       { get; init; }
    public DateTime  StartedAt        { get; init; }
    public DateTime  LastUpdatedAt    { get; init; }
    public bool      IsComplete       { get; init; }
    public int       TurnCount        { get; init; }
}

public sealed record WireSessionDetail
{
    public string                 SessionId        { get; init; } = "";
    public string                 Task             { get; init; } = "";
    public string?                WorkingDirectory { get; init; }
    public string                 ConfigPath       { get; init; } = "";
    public DateTime               StartedAt        { get; init; }
    public DateTime               LastUpdatedAt    { get; init; }
    public bool                   IsComplete       { get; init; }
    public List<WireAgentMessage> Messages         { get; init; } = [];
}

public sealed record WireAgentMessage
{
    public string             AgentName           { get; init; } = "";
    public string             Role                { get; init; } = "assistant";
    public string             Content             { get; init; } = "";
    public int                TurnIndex           { get; init; }
    public DateTime           Timestamp           { get; init; }
    public int?               InputTokens         { get; init; }
    public int?               OutputTokens        { get; init; }
    public bool               IsCompactionSummary { get; init; }
    public List<WireToolCall> ToolCalls           { get; init; } = [];

    /// <summary>A routing correction the orchestrator injected, not something an agent or human said.</summary>
    public bool IsOrchestratorCorrection => AgentName == "Orchestrator";
}

public sealed record WireToolCall
{
    public string  Name        { get; init; } = "";
    public string? ArgsSummary { get; init; }
    public bool    Succeeded   { get; init; }
}

public sealed record WireReplSession
{
    public string   SessionId        { get; init; } = "";
    public string   ModelId          { get; init; } = "";
    public string   Cwd              { get; init; } = "";
    public string?  Title            { get; init; }
    public string?  AutoTitle        { get; init; }
    public DateTime StartedAt        { get; init; }
    public DateTime LastUpdatedAt    { get; init; }
    public int      TurnIndex        { get; init; }
    public string   FirstUserMessage { get; init; } = "";

    public string DisplayName => Title ?? AutoTitle ?? FirstUserMessage;
}

public sealed record WireContextItem
{
    public string                Name        { get; init; } = "";
    public string?               Description { get; init; }
    public string                SourcePath  { get; init; } = "";
    public DateTime              ImportedAt  { get; init; }
    public long                  SizeBytes   { get; init; }
    public List<WireContextFile> Files       { get; init; } = [];
}

public sealed record WireContextFile
{
    public string RelativePath { get; init; } = "";
    public long   SizeBytes    { get; init; }
}

public sealed record WireScheduledJob
{
    public string          Name        { get; init; } = "";
    public string?         Description { get; init; }
    public string          Cron        { get; init; } = "";
    public string          Task        { get; init; } = "";
    public string?         Config      { get; init; }
    public string?         WorkDir     { get; init; }
    public bool            Enabled     { get; init; }
    public bool            Due         { get; init; }
    public DateTimeOffset  CreatedAt   { get; init; }
    public DateTimeOffset? LastRun     { get; init; }
    public DateTimeOffset? NextRun     { get; init; }
    /// <summary>0 when the last run succeeded; null if it hasn't run, or ran under fuseraft before 1.21.</summary>
    public int?            LastExitCode { get; init; }
}

public sealed record WireMemory
{
    public string Guid        { get; init; } = "";
    public string Name        { get; init; } = "";
    public string Type        { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body        { get; init; } = "";
}

public sealed record WireMemoryAgent
{
    public string Agent { get; init; } = "";
    public int    Count { get; init; }
}

public sealed record WireConfigSummary
{
    public string       Path          { get; init; } = "";
    public string?      Name          { get; init; }
    public string?      Description   { get; init; }
    public List<string> Agents        { get; init; } = [];
    public string?      SelectionType { get; init; }
    public string?      Error         { get; init; }

    /// <summary>The markdown agent files the team names, as full paths; empty before CLI 1.35.0.</summary>
    public List<string> AgentFiles    { get; init; } = [];

    /// <summary>How the flow runs, in team-file words: <c>steps</c>, or the pattern's name.</summary>
    public string? Flow => SelectionType == "statemachine" ? "steps" : SelectionType;

    /// <summary>The file is in a format fuseraft no longer loads, and <c>fuseraft migrate</c> converts it.</summary>
    public bool NeedsMigration => Error?.Contains("fuseraft migrate", StringComparison.Ordinal) == true;
}

public sealed record WireValidation
{
    public string             Path   { get; init; } = "";
    public bool               Valid  { get; init; }
    public List<WireIssue>    Issues { get; init; } = [];
    public WireConfigSummary? Config { get; init; }

    /// <summary>The team's markdown agent files, even when it doesn't load; empty before CLI 1.35.0.</summary>
    public List<string>       AgentFiles { get; init; } = [];

    /// <summary>The file is in a format fuseraft no longer loads, and <c>fuseraft migrate</c> converts it.</summary>
    public bool NeedsMigration => Issues.Any(i => i.Message.Contains("fuseraft migrate", StringComparison.Ordinal));
}

public sealed record WireIssue
{
    public string Level   { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record WireTemplate
{
    public string Key         { get; init; } = "";
    public string Label       { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed record WireInitResult
{
    public string       Template   { get; init; } = "";
    public string       Model      { get; init; } = "";
    public string?      Endpoint   { get; init; }
    public string       ConfigPath { get; init; } = "";
    public List<string> AgentFiles { get; init; } = [];
}

public sealed record WirePlugin
{
    public string             Name      { get; init; } = "";
    public string             Source    { get; init; } = "";
    public List<WireFunction> Functions { get; init; } = [];
}

public sealed record WireFunction
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>A model from <c>fuseraft models --json</c>.</summary>
public sealed record WireModel
{
    public string Id      { get; init; } = "";
    public bool   Current { get; init; }
}

/// <summary>A global setting from <c>fuseraft settings show --json</c>; <c>Value</c> is null when unset, which takes <c>Default</c>.</summary>
public sealed record WireSetting
{
    public string        Key         { get; init; } = "";
    /// <summary><c>text</c>, <c>bool</c>, <c>integer</c>, <c>number</c>, <c>choice</c> or <c>list</c>.</summary>
    public string        Kind        { get; init; } = "";
    public string        Description { get; init; } = "";
    public string?       Value       { get; init; }
    public string?       Default     { get; init; }
    public double?       Min         { get; init; }
    public double?       Max         { get; init; }
    public List<string>? Choices     { get; init; }
    public bool          Clearable   { get; init; }
}

/// <summary>
/// A tool category: from <c>fuseraft settings tools --json</c> (what new chats start with) or a chat's <c>tools</c> event
/// (what that chat has). <c>Tools</c> is null for skills and MCP servers until a chat loads them.
/// </summary>
public sealed record WireToolCategory
{
    public string          Name        { get; init; } = "";
    public string          Description { get; init; } = "";
    /// <summary>Off unless <c>repl.plugins</c> names it.</summary>
    public bool            Optional    { get; init; }
    public bool            Enabled     { get; init; }
    /// <summary>Chat only: <c>"safe mode"</c> when safe mode is keeping it off.</summary>
    public string?         Blocked     { get; init; }
    public List<WireTool>? Tools       { get; init; }

    public bool IsMcp => Name.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase);
    public string Label => IsMcp ? Name[4..] : Name;
}

public sealed record WireTool
{
    public string  Name        { get; init; } = "";
    public string  Description { get; init; } = "";
    public bool    Enabled     { get; init; }
    /// <summary>Chat only: <c>"safe mode"</c> or <c>"restricted"</c> when something else is keeping it off.</summary>
    public string? Blocked     { get; init; }
}

/// <summary>A chat's <c>tools</c> event: its tools, sent after <c>ready</c> and after anything changes them.</summary>
public sealed record WireChatTools
{
    public bool                   SafeMode        { get; init; }
    /// <summary>How many tools the model is offered.</summary>
    public int                    Active          { get; init; }
    /// <summary>False when the chat's switches differ from what new chats start with.</summary>
    public bool                   MatchesDefaults { get; init; }
    public List<WireToolCategory> Categories      { get; init; } = [];
}
