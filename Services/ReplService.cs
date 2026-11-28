using System.Collections.Concurrent;
using System.Text.Json;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Relay;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

public enum ReplStatus { Idle, Thinking, Error }

public sealed record ReplMessage(string Role, string Text, IReadOnlyList<string> ToolCalls, DateTimeOffset At)
{
    /// <summary>A reply's text, tool calls and reasoning in the order they streamed; a shell message's run.</summary>
    public IReadOnlyList<ReplPart> Parts { get; init; } = [];
    /// <summary>The files a reply changed.</summary>
    public IReadOnlyList<ReplFileChange> Changes { get; init; } = [];
    public ReplUsage? Usage { get; init; }
    /// <summary>For a system message: <c>info</c>, <c>warning</c> or <c>error</c>; null for command output.</summary>
    public string? Tone { get; init; }
    /// <summary>Shown again from the saved chat when it was resumed.</summary>
    public bool Replayed { get; init; }
    /// <summary>The names of the images sent with a user message.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];
    /// <summary>A user message sent while a reply ran, which the chat hasn't taken in yet.</summary>
    public bool Queued { get; set; }
}

/// <summary>An <c>ask_user</c> question waiting for the chat's owner.</summary>
public sealed record ReplQuestion(string Question, IReadOnlyList<string> Options, bool AllowOther);

/// <summary>Something in a chat its owner may want to hear about while looking elsewhere.</summary>
public enum ReplAlert { Replied, Question, Failed }

public sealed class ActiveReplSession : IDisposable
{
    /// <summary>Stable dictionary key assigned at creation; never changes.</summary>
    public string        Key       { get; init; } = "";
    /// <summary>Real session ID from the CLI's 'ready' event; starts equal to Key.</summary>
    public string        SessionId { get; set; }  = "";
    public string        OwnerId   { get; init; } = "";
    public string        ProfileId { get; init; } = "";
    public string        ModelId   { get; set; }  = "";
    /// <summary>The directory the chat runs in; its tools are sandboxed to it.</summary>
    public string?       Cwd       { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public ReplStatus Status         { get; set; } = ReplStatus.Idle;
    public string?    StreamingText  { get; set; }
    public string?    StreamingTools { get; set; }
    public string?    Error          { get; set; }
    /// <summary>The agent's open question; the reply waits until it's answered or skipped.</summary>
    public ReplQuestion? Question    { get; set; }
    /// <summary>The chat takes messages and commands while it's busy (fuseraft v1.20.0 or later).</summary>
    public bool       InputWhileBusy { get; init; }
    /// <summary>The chat's tools, from its <c>tools</c> event; null with a fuseraft from before v1.27.0.</summary>
    public WireChatTools? Tools      { get; set; }
    /// <summary>A tools change is on its way to the CLI and its answer hasn't come back.</summary>
    public bool       ToolsChanging  => Volatile.Read(ref QuietPending) > 0;

    // Quiet commands sent whose message_end hasn't arrived; that message_end isn't the end of a reply.
    internal int QuietPending;

    /// <summary>The slash commands the chat offers, from its <c>ready</c> event; empty before fuseraft v1.34.0.</summary>
    public IReadOnlyList<ReplCommand> Commands { get; set; } = [];
    public ReplStatusLine? StatusLine { get; set; }
    /// <summary>Every file the chat has changed, newest last, with its line counts added up.</summary>
    public List<ReplFileChange> ChangedFiles { get; } = [];
    /// <summary>Text the chat asked to copy (<c>/copy</c>); <see cref="CopySeq"/> counts the requests.</summary>
    public string? CopyText { get; set; }
    public int     CopySeq  { get; set; }

    internal readonly object Gate = new();
    internal List<ReplPart> StreamingParts { get; } = [];
    internal List<ReplFileChange> TurnChanges { get; } = [];
    internal DateTimeOffset? TurnStartedAt { get; set; }
    internal ReplShellPart? Shell { get; set; }

    /// <summary>The reply streaming now, in order.</summary>
    public ReplPart[] LiveParts() { lock (Gate) return [.. StreamingParts]; }

    public ReplMessage[] Transcript() { lock (Gate) return [.. Messages]; }

    internal void Add(ReplMessage message) { lock (Gate) Messages.Add(message); }

    public List<ReplMessage> Messages { get; } = [];

    internal IBridgeProcess? Bridge { get; set; }
    internal ApprovalRelay?  Relay  { get; set; }

    /// <summary>Ends the chat's process; once it has exited, its relay's files go too.</summary>
    public void Dispose()
    {
        if (Bridge is not { } bridge) return;
        bridge.Kill();
        _ = Task.Run(async () =>
        {
            try { await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch { }
            bridge.Dispose();
        });
    }
}

/// <summary>
/// Manages fuseraft REPL sessions by spawning <c>fuseraft repl --vscode</c> as a child
/// process and speaking the same JSON-line protocol used by the VS Code extension. The REPL
/// asks before writing files or running commands; those requests go to the <see cref="HitlBroker"/>.
/// Each chat belongs to the account that opened it and runs against that account's fuseraft home. Chats run in
/// relays, so stopping the server leaves them open and the next server adopts them.
/// </summary>
public sealed class ReplService : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveReplSession> _sessions = new();
    private readonly ModelProfileRegistry _profiles;
    private readonly FuseraftCli _cli;
    private readonly HitlBroker  _hitl;
    private volatile bool _stopping;

    /// <summary>The <see cref="BridgeTag.Kind"/> of a chat's relay.</summary>
    public const string Kind = "repl";

    // Older CLIs read a line sent while busy as the answer to the next approval: it's denied and the message lost.
    internal static readonly Version InputWhileBusySince = new(1, 20, 0);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event Action<string>? SessionChanged;
    /// <summary>Fired after <see cref="SessionChanged"/> when a chat replies, asks a question or fails.</summary>
    public event Action<ActiveReplSession, ReplAlert>? Alert;

    public ReplService(ModelProfileRegistry profiles, FuseraftCli cli, HitlBroker hitl)
    {
        _profiles = profiles;
        _cli      = cli;
        _hitl     = hitl;
    }

    // ── Session creation ─────────────────────────────────────────────────────

    public async Task<string> CreateSessionAsync(WebUser user, string profileId, string systemPrompt, string? workspaceDir = null)
    {
        var access  = WorkspaceAccess.For(user);
        var cwd     = string.IsNullOrWhiteSpace(workspaceDir) ? null : access.Check(workspaceDir);
        if (cwd is null && access.Roots.Count > 0)
            throw new ArgumentException("Pick a workspace for the chat.");
        var profile = await GetProfileAsync(user, profileId);
        var key     = NewKey();
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = key,
            OwnerId   = user.Id,
            ProfileId = profile.Id,
            ModelId   = profile.ModelId,
            Cwd       = cwd,
            InputWhileBusy = await InputWhileBusyAsync(),
        };
        _sessions[key] = session;

        var args = new List<string> { "repl", "--vscode", "--no-banner", "--model", profile.ModelId };
        args.AddRange(ConnectionArgs(profile));
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            args.AddRange(["--system", systemPrompt]);

        await SpawnAsync(user, key, session, profile, args);
        return key;
    }

    public async Task<string> ResumeSnapshotAsync(WebUser user, string snapshotSessionId)
    {
        var snapshot = (await ListSnapshotsAsync(user)).FirstOrDefault(s => s.SessionId == snapshotSessionId)
            ?? throw new InvalidOperationException($"Snapshot '{snapshotSessionId}' not found.");
        var cwd = Directory.Exists(snapshot.Cwd) ? snapshot.Cwd : null;
        if (cwd is not null && !WorkspaceAccess.For(user).Allows(cwd))
            throw new UnauthorizedAccessException($"That chat ran in '{cwd}', which is outside your workspaces.");

        var profiles = await _profiles.For(user).ListAsync();
        var profile  = profiles.FirstOrDefault(p => p.ModelId == snapshot.ModelId)
                    ?? profiles.FirstOrDefault()
                    ?? throw new InvalidOperationException("No model profiles configured.");

        var key     = NewKey();
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = snapshotSessionId,
            OwnerId   = user.Id,
            ProfileId = profile.Id,
            ModelId   = snapshot.ModelId,
            // A chat resumes in the directory it ran in, so its sandbox and paths still line up.
            Cwd       = cwd,
            InputWhileBusy = await InputWhileBusyAsync(),
        };
        _sessions[key] = session;

        var args = new List<string>
        {
            "repl", "--vscode", "--no-banner",
            "--model", snapshot.ModelId,
            "--resume", snapshotSessionId,
        };
        args.AddRange(ConnectionArgs(profile));
        await SpawnAsync(user, key, session, profile, args);
        return key;
    }

    private async Task SpawnAsync(WebUser user, string key, ActiveReplSession session, ModelProfile profile, List<string> args)
    {
        // This page answers ask_user questions; without it the agent isn't given the tool.
        var env = new Dictionary<string, string> { ["FUSERAFT_VSCODE_QUESTIONS"] = "1" };
        // Inject the profile's API key as FUSERAFT_API_KEY — same channel the VS Code
        // extension uses so the CLI picks it up in --vscode mode.
        if (await _profiles.For(user).GetKeyAsync(profile.Id) is { Length: > 0 } apiKey)
            env["FUSERAFT_API_KEY"] = apiKey;

        var tag = new BridgeTag
        {
            Kind = Kind, OwnerId = user.Id, StartedAt = session.StartedAt,
            Extra = { ["key"] = key, ["profileId"] = profile.Id, ["modelId"] = session.ModelId, ["sessionId"] = session.SessionId },
        };
        if (session.Cwd is not null) tag.Extra["cwd"] = session.Cwd;
        // Recorded for whichever server adopts the chat, which may find a different fuseraft installed.
        if (session.InputWhileBusy) tag.Extra["inputWhileBusy"] = "1";

        try
        {
            var bridge = await _cli.ForHome(user.Home).StartBridgeAsync(args, session.Cwd, env, tag);
            Attach(key, session, bridge);
        }
        catch (Exception ex)
        {
            session.Status = ReplStatus.Error;
            session.Error  = $"Could not start fuseraft: {ex.Message}";
            SessionChanged?.Invoke(key);
        }
    }

    private void Attach(string key, ActiveReplSession session, IBridgeProcess bridge)
    {
        session.Bridge = bridge;
        session.Relay  = new ApprovalRelay(_hitl, bridge.Input, ApprovalSource.Repl, ownerId: session.OwnerId);
        _ = Task.Run(() => ReadLoopAsync(key, bridge));
    }

    /// <summary>
    /// Takes over a chat a previous server left open. Its relay replays the conversation, including what was typed,
    /// so the chat reappears as it was, and approvals still waiting are asked again.
    /// </summary>
    public void AdoptRunning(BridgeTag tag, IBridgeProcess bridge, WebUser? owner)
    {
        if (owner is null || tag.Extra.GetValueOrDefault("key") is not { Length: > 0 } key)
        {
            bridge.Kill();
            bridge.Dispose();
            return;
        }
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = tag.Extra.GetValueOrDefault("sessionId") ?? key,
            OwnerId   = owner.Id,
            ProfileId = tag.Extra.GetValueOrDefault("profileId") ?? "",
            ModelId   = tag.Extra.GetValueOrDefault("modelId") ?? "",
            Cwd       = tag.Extra.GetValueOrDefault("cwd"),
            StartedAt = tag.StartedAt,
            InputWhileBusy = tag.Extra.GetValueOrDefault("inputWhileBusy") == "1",
        };
        _sessions[key] = session;
        Attach(key, session, bridge);
        SessionChanged?.Invoke(key);
    }

    // ── Stdout reader ────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(string key, IBridgeProcess bridge)
    {
        ActiveReplSession? died = null;
        try
        {
            while (await bridge.Output.ReadLineAsync() is { } raw)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t))
                        HandleEvent(key, t.GetString() ?? "", root);
                }
                catch { /* non-JSON diagnostic line — ignore */ }
            }
        }
        catch { /* pipe closed */ }
        finally
        {
            if (_stopping)
            {
                // The server let go; the chat stays open in its relay.
            }
            else if (!_sessions.TryGetValue(key, out var s))
            {
                // Ended normally (session_end); once the process is gone its relay's files can go.
                await bridge.WaitForExitAsync();
                bridge.Dispose();
            }
            else
            {
                // Still listed means no session_end arrived: the process died. Keep the chat on
                // screen with the reason instead of silently dropping back to the start form.
                _hitl.CancelSession(s.SessionId);
                s.Question = null;
                var exit = await bridge.WaitForExitAsync();
                s.Status         = ReplStatus.Error;
                s.Error          = $"fuseraft exited unexpectedly (exit {exit}). {bridge.StderrTail.Split('\n').LastOrDefault()}".Trim();
                s.StreamingText  = null;
                s.StreamingTools = null;
                died = s;
            }
            SessionChanged?.Invoke(key);
            if (died is not null) Alert?.Invoke(died, ReplAlert.Failed);
        }
    }

    // ── Event dispatcher ─────────────────────────────────────────────────────

    private void HandleEvent(string key, string type, JsonElement root)
    {
        if (!_sessions.TryGetValue(key, out var s)) return;
        ReplAlert? alert = null;

        switch (type)
        {
            case "ready":
                try
                {
                    FuseraftCli.CheckVersion(root);
                }
                catch (FuseraftCliException ex)
                {
                    s.Status = ReplStatus.Error;
                    s.Error  = ex.Message;
                    s.Bridge?.Kill();
                    break;
                }
                s.SessionId = Str(root, "sessionId") ?? s.SessionId;
                s.ModelId   = Str(root, "model")     ?? s.ModelId;
                s.Status    = ReplStatus.Idle;
                s.Commands  = ParseCommands(root);
                break;

            case "title":
                // Also sent when /switch, /fork switch or /clear moves the chat to another session.
                s.SessionId = Str(root, "sessionId") ?? s.SessionId;
                break;

            case "cleared":
                // /clear started a new chat in this process; the old one is saved and listed under Recent Chats.
                s.SessionId = Str(root, "sessionId") ?? s.SessionId;
                ClearTranscript(s);
                s.Question = null;
                s.Error    = null;
                break;

            case "tools":
                try { s.Tools = root.Deserialize<WireChatTools>(FuseraftCli.JsonOpts); }
                catch (JsonException) { }
                break;

            case "approval_request":
                if (s.Relay?.Handle(s.SessionId, root) is { } request)
                    _ = NoteAnswerAsync(key, s, request);
                break;

            case "question":
                var options = new List<string>();
                if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                    foreach (var o in opts.EnumerateArray())
                        if (o.GetString() is { Length: > 0 } option) options.Add(option);
                s.Question = new ReplQuestion(Str(root, "question") ?? "", options,
                    !root.TryGetProperty("allowOther", out var ao) || ao.ValueKind != JsonValueKind.False);
                alert = ReplAlert.Question;
                break;

            case RelayBridge.ReplayedInput:
                // Something typed before this server took over, replayed so the chat reads as it did.
                var typed = Str(root, "text") ?? "";
                var raw   = root.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("quiet", out var q) && q.ValueKind == JsonValueKind.True)
                    Interlocked.Increment(ref s.QuietPending);
                else if (raw && typed == "/clear")
                    ClearTranscript(s);
                else if (!raw)
                {
                    s.Add(new ReplMessage("user", typed, [], DateTimeOffset.UtcNow));
                    s.Status        = ReplStatus.Thinking;
                    s.StreamingText = "";
                }
                break;

            case "text":
            case "info":
                // Slash-command output, pre-rendered as Markdown.
                if (Str(root, "text") is { Length: > 0 } output)
                    s.Add(new ReplMessage("system", output, [], DateTimeOffset.UtcNow));
                break;

            case "token":
                var chunk = Str(root, "text") ?? "";
                s.StreamingText = (s.StreamingText ?? "") + chunk;
                s.Status = ReplStatus.Thinking;
                lock (s.Gate)
                {
                    if (s.StreamingParts.LastOrDefault() is ReplTextPart last) last.Text += chunk;
                    else s.StreamingParts.Add(new ReplTextPart { Text = chunk });
                }
                break;

            case "thinking":
                var thought = Str(root, "text") ?? "";
                lock (s.Gate)
                {
                    if (s.StreamingParts.LastOrDefault() is ReplThinkingPart lastThought) lastThought.Text += thought;
                    else s.StreamingParts.Add(new ReplThinkingPart { Text = thought });
                }
                break;

            case "tool_call":
                var name = Str(root, "name") ?? "";
                s.StreamingTools = s.StreamingTools is null ? name : s.StreamingTools + " → " + name;
                lock (s.Gate)
                    s.StreamingParts.Add(new ReplToolPart
                    {
                        Id      = Str(root, "id") ?? "",
                        Name    = name,
                        Summary = Str(root, "summary"),
                        Args    = root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Serialize(args, IndentedJson) : null,
                        Agent   = Str(root, "agent"),
                    });
                break;

            case "tool_result":
                var callId = Str(root, "id");
                lock (s.Gate)
                    // A message sent mid-reply moves the calls streamed so far into the transcript.
                    if (s.StreamingParts.Concat(s.Messages.LastOrDefault(m => m.Role == "assistant")?.Parts ?? [])
                            .OfType<ReplToolPart>().LastOrDefault(t => t.Id == callId && t.Ok is null) is { } call)
                    {
                        call.Ok      = !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;
                        call.Output  = Cap(Str(root, "output"), MaxToolOutput);
                        call.Elapsed = DateTimeOffset.UtcNow - call.StartedAt;
                    }
                break;

            case "file_changed":
                NoteChange(s, new ReplFileChange(Str(root, "path") ?? "", Str(root, "sigil") ?? "M",
                    Int(root, "added"), Int(root, "removed")));
                break;

            case "file_changes":
                // The turn's whole list, at its end: anything a file_changed didn't already report.
                if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                    foreach (var c in changes.EnumerateArray())
                        if (Str(c, "path") is { Length: > 0 } changed && !s.TurnChanges.Any(t => t.Path == changed))
                            NoteChange(s, new ReplFileChange(changed, Str(c, "sigil") ?? "M", 0, 0));
                break;

            case "status":
                s.StatusLine = new ReplStatusLine(
                    Str(root, "model") ?? s.ModelId, Int(root, "turn"),
                    root.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Number ? ctx.GetDouble() : 0,
                    Str(root, "level") ?? "clear", Strings(root, "modes"),
                    Long(root, "input") ?? 0, Long(root, "output") ?? 0, Long(root, "cached") ?? 0);
                if (Str(root, "model") is { Length: > 0 } model) s.ModelId = model;
                break;

            case "turn_start":
                s.TurnStartedAt ??= DateTimeOffset.UtcNow;
                s.Status = ReplStatus.Thinking;
                MarkDelivered(s);
                break;

            case "compacted":
                s.Add(new ReplMessage("system", "Earlier messages were summarized to make room in the context window.", [], DateTimeOffset.UtcNow) { Tone = "info" });
                break;

            case "replay":
                AddReplay(s, root);
                break;

            case "shell_start":
                var run = new ReplShellPart { Command = Str(root, "command") ?? "" };
                s.Shell  = run;
                s.Status = ReplStatus.Thinking;
                s.Add(new ReplMessage("shell", run.Command, [], DateTimeOffset.UtcNow) { Parts = [run] });
                break;

            case "shell_output":
                if (s.Shell is { } shell && shell.Output.Length < MaxShellOutput)
                    shell.Output += (Str(root, "text") ?? "") + "\n";
                break;

            case "shell_end":
                // A shell command sends no message_end.
                if (s.Shell is { } ended) ended.Done = true;
                s.Shell = null;
                if (s.StreamingParts.Count == 0) ResetStreaming(s);
                break;

            case "copy":
                s.CopyText = Str(root, "text");
                s.CopySeq++;
                break;

            case "message_end":
                // The end of a quiet command, which showed nothing in the chat.
                if (Volatile.Read(ref s.QuietPending) > 0)
                {
                    Interlocked.Decrement(ref s.QuietPending);
                    break;
                }
                var tools = new List<string>();
                if (root.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    foreach (var item in tc.EnumerateArray())
                        if (item.GetString() is { } tn) tools.Add(tn);
                var usage = new ReplUsage(Long(root, "inputTokens"), Long(root, "outputTokens"), Long(root, "cachedInputTokens"),
                    s.TurnStartedAt is { } began ? DateTimeOffset.UtcNow - began : null);
                if (EndReply(s, tools, usage, cancelled: false)) alert = ReplAlert.Replied;
                MarkDelivered(s);
                break;

            case "cancelled":
                s.Question = null;
                EndReply(s, [], null, cancelled: true);
                break;

            case "error":
                var err = Str(root, "text") ?? "Unknown error";
                s.Status        = ReplStatus.Error;
                s.Error         = err;
                s.StreamingText = null;
                s.StreamingTools = null;
                s.Add(new ReplMessage("system", $"Error: {err}", [], DateTimeOffset.UtcNow));
                alert = ReplAlert.Failed;
                break;

            case "applied":
                // A mid-turn command (/title, /hitl on|off) took effect without waiting for the reply.
                if (Str(root, "message") is { Length: > 0 } applied)
                    s.Add(new ReplMessage("system", applied, [], DateTimeOffset.UtcNow));
                break;

            case "interjection":
                // "carried" messages start the next turn, whose turn_start takes them in.
                if (Str(root, "status") is "joined" or "dropped") MarkDelivered(s);
                // "joined" needs no note: the reply already takes the message into account.
                var note = Str(root, "status") switch
                {
                    "carried" => "What you sent starts the next turn.",
                    "dropped" => "What you sent during the turn was dropped.",
                    _         => null,
                };
                if (note is not null)
                    s.Add(new ReplMessage("system", note, [], DateTimeOffset.UtcNow));
                break;

            case "warning":
                if (Str(root, "text") is { } warn)
                    s.Add(new ReplMessage("system", warn, [], DateTimeOffset.UtcNow));
                break;

            case "retrying":
                var att = root.TryGetProperty("attempt", out var ae) ? ae.GetInt32() : 0;
                var max = root.TryGetProperty("max",     out var me) ? me.GetInt32() : 0;
                s.Add(new ReplMessage("system", $"Retrying… ({att}/{max})", [], DateTimeOffset.UtcNow));
                break;

            case "plan":
                var planLines = new List<string>();
                if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                    foreach (var step in stepsEl.EnumerateArray())
                    {
                        var n    = step.TryGetProperty("step",        out var sn) ? sn.GetInt32()   : 0;
                        var desc = step.TryGetProperty("description", out var sd) ? sd.GetString() ?? "" : "";
                        var tool = step.TryGetProperty("tool",        out var st) ? st.GetString()       : null;
                        planLines.Add($"{n}. {desc}" + (tool is not null ? $"  `{tool}`" : ""));
                    }
                s.Add(new ReplMessage("system",
                    $"**Plan captured** ({planLines.Count} steps). Type `/execute` to run.\n\n" +
                    string.Join("\n", planLines),
                    [], DateTimeOffset.UtcNow));
                break;

            case "step_status":
                var sn2    = root.TryGetProperty("step",      out var ss2) ? ss2.GetInt32()    : 0;
                var stot   = root.TryGetProperty("total",     out var st2) ? st2.GetInt32()    : 0;
                var sstat  = root.TryGetProperty("status",    out var ss3) ? ss3.GetString() ?? "" : "";
                var sleft  = root.TryGetProperty("stepsLeft", out var sl)  ? sl.GetInt32()     : 0;
                var icon   = sstat == "complete" ? "✓" : sstat == "skipped" ? "↷" : "✗";
                var leftStr = sleft > 0 ? $" · {sleft} remaining" : "";
                s.Add(new ReplMessage("system", $"{icon} Step {sn2}/{stot} {sstat}{leftStr}", [], DateTimeOffset.UtcNow));
                break;

            case "session_end":
                s.Question      = null;
                s.Status        = ReplStatus.Idle;
                s.StreamingText = null;
                s.StreamingTools = null;
                s.Add(new ReplMessage("system", "Session ended.", [], DateTimeOffset.UtcNow));
                _sessions.TryRemove(key, out _);
                break;
        }

        SessionChanged?.Invoke(key);
        if (alert is { } a) Alert?.Invoke(s, a);
    }

    private const int MaxToolOutput  = 20_000;
    private const int MaxShellOutput = 200_000;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static List<ReplCommand> ParseCommands(JsonElement root)
    {
        var commands = new List<ReplCommand>();
        if (root.TryGetProperty("commands", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var c in list.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && Str(c, "usage") is { Length: > 0 } usage)
                    commands.Add(new ReplCommand(usage, Str(c, "description") ?? ""));
        return commands;
    }

    private static void NoteChange(ActiveReplSession s, ReplFileChange change)
    {
        if (change.Path.Length == 0) return;
        lock (s.Gate)
        {
            Merge(s.TurnChanges, change);
            Merge(s.ChangedFiles, change);
        }

        // One row per file: its latest sigil, its line counts added up, moved to the end.
        static void Merge(List<ReplFileChange> list, ReplFileChange change)
        {
            var i = list.FindIndex(c => c.Path == change.Path);
            if (i < 0) { list.Add(change); return; }
            var before = list[i];
            list.RemoveAt(i);
            // A file added and then edited is still new.
            var sigil = before.Sigil == "A" && change.Sigil == "M" ? "A" : change.Sigil;
            list.Add(change with { Sigil = sigil, Added = before.Added + change.Added, Removed = before.Removed + change.Removed });
        }
    }

    private static void ClearTranscript(ActiveReplSession s)
    {
        lock (s.Gate)
        {
            s.Messages.Clear();
            s.ChangedFiles.Clear();
        }
        s.StatusLine = null;
    }

    private static void MarkDelivered(ActiveReplSession s)
    {
        foreach (var m in s.Transcript())
            if (m.Queued) m.Queued = false;
    }

    /// <summary>Turns the reply streamed so far into a message; false when there was nothing to show.</summary>
    private static bool EndReply(ActiveReplSession s, IReadOnlyList<string> tools, ReplUsage? usage, bool cancelled)
    {
        bool replied;
        lock (s.Gate)
        {
            var parts   = s.StreamingParts.ToList();
            var text    = s.StreamingText ?? "";
            var changes = s.TurnChanges.ToList();
            replied = text.Length > 0;
            if (cancelled && parts.Count > 0)
            {
                text += "\n*(cancelled)*";
                parts.Add(new ReplTextPart { Text = "\n*(cancelled)*" });
            }
            foreach (var running in parts.OfType<ReplToolPart>().Where(t => t.Ok is null))
            {
                running.Ok      = false;
                running.Output ??= "(stopped)";
            }
            if (parts.Count > 0 || changes.Count > 0)
                s.Messages.Add(new ReplMessage("assistant", text, tools.Count > 0 ? tools : [.. parts.OfType<ReplToolPart>().Select(t => t.Name)],
                    DateTimeOffset.UtcNow) { Parts = parts, Changes = changes, Usage = usage });
        }
        ResetStreaming(s);
        return replied;
    }

    private static void ResetStreaming(ActiveReplSession s)
    {
        lock (s.Gate)
        {
            s.StreamingParts.Clear();
            s.TurnChanges.Clear();
        }
        s.Status         = ReplStatus.Idle;
        s.StreamingText  = null;
        s.StreamingTools = null;
        s.TurnStartedAt  = null;
    }

    /// <summary>Earlier turns of a resumed chat, which the CLI sends right after it's ready.</summary>
    private static void AddReplay(ActiveReplSession s, JsonElement root)
    {
        if (!root.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0)
            return;
        var total = Int(root, "total");
        var shown = turns.GetArrayLength();
        var compacted = root.TryGetProperty("compacted", out var cm) && cm.ValueKind == JsonValueKind.True;
        var lead = shown < total ? $"The last {shown} of this chat's {total} turns." : $"This chat's {shown} earlier turn{(shown == 1 ? "" : "s")}.";
        s.Add(new ReplMessage("system", lead + (compacted ? " Older ones were summarized." : ""), [], DateTimeOffset.UtcNow)
            { Tone = "info", Replayed = true });
        foreach (var turn in turns.EnumerateArray())
        {
            if (Str(turn, "user") is { Length: > 0 } user)
                s.Add(new ReplMessage("user", user, [], DateTimeOffset.UtcNow) { Replayed = true });
            var calls = new List<string>();
            if (turn.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                foreach (var call in tc.EnumerateArray())
                    if (Str(call, "name") is { Length: > 0 } n) calls.Add(n);
            if (Str(turn, "assistant") is { Length: > 0 } reply || calls.Count > 0)
                s.Add(new ReplMessage("assistant", Str(turn, "assistant") ?? "", calls, DateTimeOffset.UtcNow) { Replayed = true });
        }
    }

    // ── User actions ─────────────────────────────────────────────────────────

    public void SendMessage(WebUser user, string key, string text, IReadOnlyList<ReplImage>? images = null)
    {
        if (GetSession(user, key) is not { } s) return;
        if (s.Status == ReplStatus.Thinking && !s.InputWhileBusy) return;

        var trimmed = text.Trim();
        images ??= [];
        if (string.IsNullOrEmpty(trimmed) && images.Count == 0) return;

        var busy = s.Status == ReplStatus.Thinking;
        // Sent mid-reply: what has streamed so far came first, so it goes above the message.
        if (busy)
            lock (s.Gate)
                if (s.StreamingParts.Count > 0)
                {
                    s.Messages.Add(new ReplMessage("assistant", s.StreamingText ?? "",
                        [.. s.StreamingParts.OfType<ReplToolPart>().Select(t => t.Name)], DateTimeOffset.UtcNow) { Parts = [.. s.StreamingParts] });
                    s.StreamingParts.Clear();
                    s.StreamingText = "";
                }
        // A shell command shows as its own run, from shell_start; it isn't a message to the model.
        var shell = trimmed.StartsWith('!') && images.Count == 0;
        if (!shell)
            s.Add(new ReplMessage("user", trimmed, [], DateTimeOffset.UtcNow)
                { Images = [.. images.Select(i => i.Name)], Queued = busy });
        s.Status = ReplStatus.Thinking;
        if (!busy)
        {
            s.StreamingText  = "";
            s.StreamingTools = null;
            s.TurnStartedAt  = DateTimeOffset.UtcNow;
        }
        s.Error = null;

        SessionChanged?.Invoke(key);
        WriteStdin(s, trimmed, images);
    }

    /// <summary>Leaves a line in the chat once an approval is answered, since its card disappears.</summary>
    private async Task NoteAnswerAsync(string key, ActiveReplSession s, PendingApproval request)
    {
        var approved = await request.Tcs.Task;
        s.Add(new ReplMessage("system", ApprovalNote(request, approved), [], DateTimeOffset.UtcNow));
        SessionChanged?.Invoke(key);
    }

    internal static string ApprovalNote(PendingApproval r, bool approved) =>
        $"{(approved ? "✓ Approved" : "✕ Denied")} {r.Summary}";

    /// <summary>Answers the open question; a null or blank <paramref name="answer"/> skips it.</summary>
    public void AnswerQuestion(WebUser user, string key, string? answer)
    {
        if (GetSession(user, key) is not { Question: { } q } s) return;
        answer = string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
        if (answer is not null && !q.AllowOther && !q.Options.Contains(answer)) return;

        s.Question = null;
        s.Add(new ReplMessage("system",
            answer is null ? $"Skipped: {q.Question}" : $"{q.Question}\n\n**You answered:** {answer}",
            [], DateTimeOffset.UtcNow));
        SessionChanged?.Invoke(key);
        WriteQuestionResponse(s, answer);
    }

    /// <summary>
    /// Turns a tool category or a single tool on or off for this chat (<c>/tools enable|disable</c>), or saves the chat's
    /// tools as the defaults (<c>save</c>) or goes back to them (<c>reset</c>). Not while a reply runs: the line would
    /// join it as an interjection. The chat's next <c>tools</c> event shows the result.
    /// </summary>
    public bool ChangeTools(WebUser user, string key, string action, string? name = null)
    {
        if (GetSession(user, key) is not { Tools: { } tools } s || s.Status == ReplStatus.Thinking) return false;
        var known = name is not null && tools.Categories.Any(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (c.Tools ?? []).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        var command = (action, known) switch
        {
            ("enable" or "disable", true) => $"/tools {action} {name}",
            ("save" or "reset", _) when name is null => $"/tools {action}",
            _ => null,
        };
        if (command is null) return false;

        Interlocked.Increment(ref s.QuietPending);
        SessionChanged?.Invoke(key);
        var json = JsonSerializer.Serialize(new { type = "user_input", text = command, quiet = true }, _jsonOpts);
        try { s.Bridge?.Input.WriteLine(json); } catch { }
        return true;
    }

    public void ClearSession(WebUser user, string key)
    {
        if (GetSession(user, key) is not { } s) return;
        ClearTranscript(s);
        SessionChanged?.Invoke(key);
        // Also clear CLI history
        WriteStdinRaw(s, "/clear");
    }

    /// <summary>Stops the reply in progress; the chat stays open.</summary>
    public void CancelSession(WebUser user, string key)
    {
        if (GetSession(user, key) is not { Relay: { } relay } s) return;
        _hitl.CancelSession(s.SessionId);
        // Before v1.20.0 an interrupt left the question waiting for a line; skipping it first works on every version.
        if (s.Question is not null)
        {
            s.Question = null;
            WriteQuestionResponse(s, null);
        }
        _ = relay.InterruptAsync();
    }

    public void CloseSession(WebUser user, string key)
    {
        if (GetSession(user, key) is not null && _sessions.TryRemove(key, out var s))
        {
            s.Dispose();
            SessionChanged?.Invoke(key);
        }
    }

    /// <summary>The chat as Markdown, for a download.</summary>
    public string? Export(WebUser user, string key)
    {
        if (GetSession(user, key) is not { } s) return null;
        var md = new System.Text.StringBuilder();
        md.Append($"# fuseraft chat {s.SessionId}\n\n");
        md.Append($"{s.ModelId}{(s.Cwd is null ? "" : $" · `{s.Cwd}`")} · exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC\n");
        foreach (var m in s.Transcript())
        {
            md.Append('\n');
            switch (m.Role)
            {
                case "user":
                    md.Append("## You\n\n").Append(m.Text).Append('\n');
                    if (m.Images.Count > 0) md.Append($"\n*Images: {string.Join(", ", m.Images)}*\n");
                    break;
                case "assistant":
                    md.Append("## fuseraft\n\n");
                    if (m.Parts.Count == 0)
                    {
                        md.Append(m.Text).Append('\n');
                        if (m.ToolCalls.Count > 0) md.Append($"\n*Tools: {string.Join(", ", m.ToolCalls)}*\n");
                    }
                    foreach (var part in m.Parts)
                        md.Append(part switch
                        {
                            ReplTextPart t     => t.Text + "\n",
                            ReplThinkingPart t => $"\n> *Thinking:* {t.Text.ReplaceLineEndings(" ")}\n\n",
                            ReplToolPart t     => $"\n- `{t.Name}`{(t.Summary is { Length: > 0 } sum ? $" {sum}" : "")} {(t.Ok == false ? "✗" : "✓")}\n\n",
                            _                  => "",
                        });
                    if (m.Changes.Count > 0)
                        md.Append($"\n*Changed: {string.Join(", ", m.Changes.Select(c => $"{c.Sigil} {c.Path}"))}*\n");
                    break;
                case "shell":
                    var run = m.Parts.OfType<ReplShellPart>().FirstOrDefault();
                    md.Append($"```console\n$ {m.Text}\n{run?.Output}```\n");
                    break;
                default:
                    md.Append("> ").Append(m.Text.ReplaceLineEndings("\n> ")).Append('\n');
                    break;
            }
        }
        return md.ToString();
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    /// <summary>A chat <paramref name="user"/> may see and type into: only their own, admins included.</summary>
    public ActiveReplSession? GetSession(WebUser user, string key) =>
        _sessions.GetValueOrDefault(key) is { } s && s.OwnerId == user.Id ? s : null;

    public IEnumerable<ActiveReplSession> GetSessions(WebUser user) =>
        _sessions.Values.Where(s => s.OwnerId == user.Id).OrderByDescending(s => s.StartedAt);

    public Task<List<WireReplSession>> ListSnapshotsAsync(WebUser user) =>
        _cli.ForHome(user.Home).ListAsync<WireReplSession>("sessions", "--repl", "--json");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WriteStdin(ActiveReplSession s, string text, IReadOnlyList<ReplImage>? images = null)
    {
        var json = images is { Count: > 0 }
            ? JsonSerializer.Serialize(new { type = "user_input", text, images = images.Select(i => new { name = i.Name, data = i.DataUrl }) }, _jsonOpts)
            : JsonSerializer.Serialize(new { type = "user_input", text }, _jsonOpts);
        try { s.Bridge?.Input.WriteLine(json); } catch { }
    }

    private static void WriteQuestionResponse(ActiveReplSession s, string? answer)
    {
        var json = JsonSerializer.Serialize(new { type = "question_response", answer }, _jsonOpts);
        try { s.Bridge?.Input.WriteLine(json); } catch { }
    }

    private void WriteStdinRaw(ActiveReplSession s, string text)
    {
        // Non-JSON path: the CLI's ReadInput falls back to the raw string when
        // the line cannot be parsed as JSON.
        try { s.Bridge?.Input.WriteLine(text); } catch { }
    }

    private async Task<bool> InputWhileBusyAsync() => await _cli.VersionAsync() >= InputWhileBusySince;

    private async Task<ModelProfile> GetProfileAsync(WebUser user, string profileId)
    {
        var profiles = await _profiles.For(user).ListAsync();
        return profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException($"Profile '{profileId}' not found.");
    }

    /// <summary>
    /// The profile's connection, for this chat only: without it the REPL uses the connection in the account's
    /// config, if it has one, whatever profile was picked.
    /// </summary>
    internal static IEnumerable<string> ConnectionArgs(ModelProfile profile) => ConnectionArgs(profile.Provider, profile.Endpoint);

    internal static IEnumerable<string> ConnectionArgs(string? provider, string? endpoint)
    {
        // The Profiles page's "other" means OpenAI-compatible, which is the CLI's default for a custom endpoint.
        if (provider is { Length: > 0 } && provider != "other")
        {
            yield return "--provider";
            yield return provider;
        }
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            yield return "--endpoint";
            yield return endpoint.Trim();
        }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N")[..8];

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long? Long(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static List<string> Strings(JsonElement el, string prop)
    {
        var list = new List<string>();
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str) list.Add(str);
        return list;
    }

    private static string? Cap(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max:N0} more characters)";

    /// <summary>Lets go of every chat; their relays keep them open for the next server.</summary>
    public void Dispose()
    {
        _stopping = true;
        foreach (var s in _sessions.Values) s.Bridge?.Dispose();
        _sessions.Clear();
    }
}
