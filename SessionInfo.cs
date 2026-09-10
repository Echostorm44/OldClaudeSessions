namespace OldClaudeSessions;

/// <summary>Whether a session's process is still running on this machine.</summary>
internal enum Liveness
{
    /// <summary>No live process — the normal state for a session you want to resume.</summary>
    Ended,

    /// <summary>A Claude Code process holds this session and is waiting on you.</summary>
    Idle,

    /// <summary>A Claude Code process holds this session and is working.</summary>
    Busy,
}

/// <summary>
/// One Claude Code session as the table shows it. Assembled in two passes (see
/// <see cref="SessionScanner"/>): a fast pass over the small index files fills everything the
/// table needs to be useful immediately, and a background pass over the transcript fills
/// <see cref="Title"/>, <see cref="MessageCount"/> and <see cref="GitBranch"/>.
/// </summary>
internal sealed class SessionInfo
{
    /// <summary>The guid `claude --resume` wants.</summary>
    public required string SessionId { get; init; }

    /// <summary>Full path to the session's <c>.jsonl</c>, or null when it has been pruned.</summary>
    public string? TranscriptPath { get; set; }

    /// <summary>
    /// Whether this session can actually be resumed. Claude Code prunes old transcripts but keeps
    /// the prompt history forever, so most remembered sessions are gone — showing them as
    /// resumable would be a lie.
    /// </summary>
    public bool IsResumable => TranscriptPath is not null;

    /// <summary>The working directory the session ran in — the most recent one seen for it.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Leaf folder name of <see cref="ProjectPath"/>, for the narrow Project column.</summary>
    public string ProjectName
    {
        get
        {
            string p = ProjectPath.TrimEnd('\\', '/');
            int slash = p.LastIndexOfAny(['\\', '/']);
            return slash >= 0 && slash < p.Length - 1 ? p[(slash + 1)..] : p;
        }
    }

    /// <summary>Claude's own title for the session, once the transcript has been read.</summary>
    public string Title { get; set; } = "";

    /// <summary>The last thing you typed — the strongest "was this the one?" signal.</summary>
    public string LastPrompt { get; set; } = "";

    /// <summary>Local time of the first recorded activity, when known.</summary>
    public DateTime? FirstActivity { get; set; }

    /// <summary>Local time of the last recorded activity. Drives the default sort.</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>Wall-clock span of the session, when both ends are known.</summary>
    public TimeSpan? Duration =>
        FirstActivity is { } first && LastActivity > first ? LastActivity - first : null;

    /// <summary>Transcript size on disk. A 150 MB session is worth more than a 3 KB one.</summary>
    public long SizeBytes { get; set; }

    /// <summary>User + assistant messages in the transcript. -1 until the transcript is read.</summary>
    public int MessageCount { get; set; } = -1;

    /// <summary>Prompts you typed, counted from the global history file.</summary>
    public int PromptCount { get; set; }

    /// <summary>Whether a process still holds this session.</summary>
    public Liveness Liveness { get; set; } = Liveness.Ended;

    /// <summary>Claude Code version that wrote the session.</summary>
    public string Version { get; set; } = "";

    /// <summary>Git branch the session was working on, once the transcript has been read.</summary>
    public string GitBranch { get; set; } = "";

    /// <summary>True once the background transcript pass has filled the richer fields.</summary>
    public bool Enriched { get; set; }

    /// <summary>The command to put on the clipboard.</summary>
    public string ResumeCommand => $"claude --resume {SessionId} --dangerously-skip-permissions";

    /// <summary>The resume command prefixed with a cd, for when you are not already in the folder.</summary>
    public string ResumeCommandWithCd =>
        ProjectPath.Length == 0 ? ResumeCommand : $"cd \"{ProjectPath}\"; {ResumeCommand}";

    /// <summary>Best available one-line description: Claude's title, else the last prompt.</summary>
    public string DisplayTitle =>
        Title.Length > 0 ? Title
        : LastPrompt.Length > 0 ? LastPrompt
        : "(no title yet)";

    /// <summary>Everything the search box matches against.</summary>
    public string SearchText => $"{SessionId} {ProjectPath} {Title} {LastPrompt} {GitBranch}";
}
