using System.Diagnostics;
using System.Text.Json;

namespace OldClaudeSessions;

/// <summary>
/// Reads Claude Code's on-disk state and turns it into <see cref="SessionInfo"/> rows.
///
/// The work is split in two because the inputs differ enormously in size. The small index files
/// (<c>history.jsonl</c>, <c>sessions/*.json</c>) total a few hundred KB and give us almost
/// everything; the transcripts are the long tail — a single active project's <c>.jsonl</c> can be
/// 150 MB, and reading them all up front would mean staring at an empty window exactly when you
/// are in a hurry. So <see cref="ScanFast"/> runs first and the window paints, then
/// <see cref="EnrichAsync"/> streams the transcripts in the background, newest first, caching
/// results so later launches skip the work entirely.
/// </summary>
internal static class SessionScanner
{
    /// <summary>Claude Code's config directory — <c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>.</summary>
    public static string ClaudeHome { get; } = ResolveClaudeHome();

    private static string ResolveClaudeHome()
    {
        string? overridden = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
    }

    private static string ProjectsDir => Path.Combine(ClaudeHome, "projects");
    private static string SessionsDir => Path.Combine(ClaudeHome, "sessions");
    private static string HistoryFile => Path.Combine(ClaudeHome, "history.jsonl");

    // ── Pass 1: the fast one ──────────────────────────────────────────

    /// <summary>
    /// Builds the table from the small index files only. Costs a directory walk plus a few hundred
    /// KB of parsing, so it is safe to call on the UI thread at startup.
    /// </summary>
    public static List<SessionInfo> ScanFast()
    {
        var byId = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);

        AddTranscripts(byId);
        AddLiveSessions(byId);
        AddHistory(byId);

        var all = byId.Values.ToList();
        all.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));
        return all;
    }

    /// <summary>
    /// Finds the transcripts. Their presence is what makes a session resumable, and their file
    /// stamps give us a last-activity time and a size without opening anything.
    /// </summary>
    private static void AddTranscripts(Dictionary<string, SessionInfo> byId)
    {
        if (!Directory.Exists(ProjectsDir))
        {
            return;
        }

        foreach (string projectDir in Directory.EnumerateDirectories(ProjectsDir))
        {
            // The folder name is a flattened path ("F--Code-CascadeUI"). It is a lossy encoding —
            // a folder with a real hyphen in its name decodes wrong — so it is only ever a
            // fallback; the exact cwd comes from the session and history records below.
            string fallbackPath = DecodeProjectSlug(Path.GetFileName(projectDir));

            foreach (string file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                var fileInfo = new FileInfo(file);

                SessionInfo session = GetOrAdd(byId, id);
                session.TranscriptPath = file;
                session.SizeBytes = fileInfo.Length;
                session.ProjectPath = fallbackPath;
                session.LastActivity = fileInfo.LastWriteTime;
            }
        }
    }

    /// <summary>
    /// Reads <c>sessions/&lt;pid&gt;.json</c> — one record per Claude Code process that has run
    /// recently. These carry the exact cwd and version, and let us mark sessions that are still
    /// open so you do not "recover" a window you already have.
    /// </summary>
    private static void AddLiveSessions(Dictionary<string, SessionInfo> byId)
    {
        if (!Directory.Exists(SessionsDir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(SessionsDir, "*.json"))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;   // a live process is rewriting it; skip this launch
            }

            using JsonDocument? doc = TryParse(text);
            if (doc is null || !doc.RootElement.TryGetProperty("sessionId", out JsonElement idElement))
            {
                continue;
            }

            string? id = idElement.GetString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            JsonElement root = doc.RootElement;
            SessionInfo session = GetOrAdd(byId, id);

            if (GetString(root, "cwd") is { Length: > 0 } cwd)
            {
                session.ProjectPath = cwd;
            }

            if (GetString(root, "version") is { Length: > 0 } version)
            {
                session.Version = version;
            }

            if (GetInt64(root, "startedAt") is { } startedAt)
            {
                session.FirstActivity = FromUnixMilliseconds(startedAt);
            }

            if (GetInt64(root, "updatedAt") is { } updatedAt)
            {
                DateTime updated = FromUnixMilliseconds(updatedAt);
                if (updated > session.LastActivity)
                {
                    session.LastActivity = updated;
                }
            }

            // The record persists after the process dies, so the stored status only counts if the
            // process is genuinely still there.
            if (IsProcessAlive(GetInt32(root, "pid"), GetString(root, "procStart")))
            {
                session.Liveness = GetString(root, "status") == "busy" ? Liveness.Busy : Liveness.Idle;
            }
        }
    }

    /// <summary>
    /// Reads the global prompt history. This is the only place that remembers sessions whose
    /// transcripts have been pruned, and it is the authority on which folder a session was last
    /// used in — a long session can move between projects, and the newest entry wins.
    /// </summary>
    private static void AddHistory(Dictionary<string, SessionInfo> byId)
    {
        if (!File.Exists(HistoryFile))
        {
            return;
        }

        foreach (string line in ReadLinesSafely(HistoryFile))
        {
            using JsonDocument? doc = TryParse(line);
            if (doc is null)
            {
                continue;
            }

            JsonElement root = doc.RootElement;
            if (GetString(root, "sessionId") is not { Length: > 0 } id)
            {
                continue;
            }

            SessionInfo session = GetOrAdd(byId, id);
            session.PromptCount++;

            if (GetString(root, "project") is { Length: > 0 } project)
            {
                session.ProjectPath = project;
            }

            if (GetString(root, "display") is { Length: > 0 } prompt)
            {
                session.LastPrompt = Flatten(prompt);
            }

            if (GetInt64(root, "timestamp") is { } stamp)
            {
                DateTime when = FromUnixMilliseconds(stamp);
                session.FirstActivity = session.FirstActivity is { } first && first < when ? first : when;
                if (when > session.LastActivity)
                {
                    session.LastActivity = when;
                }
            }
        }
    }

    // ── Pass 2: the transcripts ───────────────────────────────────────

    /// <summary>
    /// Streams each transcript once to pull out the fields only it knows — Claude's own title for
    /// the session, the message count, and the git branch — reporting each session back as it
    /// finishes so the table fills in progressively. Results are cached by file size and
    /// timestamp, so an unchanged transcript is never read twice.
    /// </summary>
    /// <param name="sessions">Rows from <see cref="ScanFast"/>, richest-first is not required.</param>
    /// <param name="onEnriched">Invoked (off the UI thread) after each session is filled in.</param>
    /// <param name="cancellation">Cancelled when the window closes.</param>
    public static async Task EnrichAsync(
        IReadOnlyList<SessionInfo> sessions,
        Action<SessionInfo> onEnriched,
        CancellationToken cancellation)
    {
        IndexCache cache = IndexCache.Load();
        bool cacheChanged = false;

        // Newest first: the session you lost to the reboot is almost always the most recent one,
        // so it should fill in before we spend a second reading a 150 MB archive.
        foreach (SessionInfo session in sessions
            .Where(s => s.TranscriptPath is not null)
            .OrderByDescending(s => s.LastActivity))
        {
            cancellation.ThrowIfCancellationRequested();

            string path = session.TranscriptPath!;
            TranscriptFacts facts;

            if (cache.TryGet(path, session.SizeBytes, out TranscriptFacts cached))
            {
                facts = cached;
            }
            else
            {
                facts = await Task.Run(() => ReadTranscript(path, cancellation), cancellation);
                cache.Put(path, session.SizeBytes, facts);
                cacheChanged = true;
            }

            Apply(session, facts);
            onEnriched(session);
        }

        if (cacheChanged)
        {
            cache.Save();
        }
    }

    private static void Apply(SessionInfo session, TranscriptFacts facts)
    {
        if (facts.Title.Length > 0)
        {
            session.Title = facts.Title;
        }

        if (facts.LastPrompt.Length > 0 && session.LastPrompt.Length == 0)
        {
            session.LastPrompt = facts.LastPrompt;
        }

        if (facts.GitBranch.Length > 0)
        {
            session.GitBranch = facts.GitBranch;
        }

        if (facts.Version.Length > 0 && session.Version.Length == 0)
        {
            session.Version = facts.Version;
        }

        if (facts.Cwd.Length > 0 && session.ProjectPath.Length == 0)
        {
            session.ProjectPath = facts.Cwd;
        }

        if (facts.FirstTimestampTicks > 0)
        {
            DateTime first = new DateTime(facts.FirstTimestampTicks, DateTimeKind.Utc).ToLocalTime();
            session.FirstActivity = session.FirstActivity is { } known && known < first ? known : first;
        }

        session.MessageCount = facts.MessageCount;
        session.Enriched = true;
    }

    /// <summary>
    /// One streaming pass over a transcript. Deliberately does not parse every line as JSON — at
    /// 150 MB that dominates the cost, and all but a handful of lines are tool output we do not
    /// care about. Instead each line is tested with ordinal substring checks and only the few that
    /// match are parsed.
    /// </summary>
    private static TranscriptFacts ReadTranscript(string path, CancellationToken cancellation)
    {
        var facts = new TranscriptFacts();
        int lineNumber = 0;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1 << 20);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                // Cancellation is checked periodically rather than per line — the check is cheap
                // but not free, and a 150 MB file is millions of lines.
                if ((++lineNumber & 0xFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal) ||
                    line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
                {
                    facts.MessageCount++;
                    CaptureTimestamp(line, ref facts);
                    continue;
                }

                // Claude appends a fresh title record whenever it renames the session, so the last
                // one in the file is the current one.
                if (line.Contains("\"type\":\"ai-title\"", StringComparison.Ordinal))
                {
                    using JsonDocument? doc = TryParse(line);
                    if (doc is not null && GetString(doc.RootElement, "aiTitle") is { Length: > 0 } title)
                    {
                        facts.Title = Flatten(title);
                    }
                    continue;
                }

                if (line.Contains("\"type\":\"last-prompt\"", StringComparison.Ordinal))
                {
                    using JsonDocument? doc = TryParse(line);
                    if (doc is not null && GetString(doc.RootElement, "lastPrompt") is { Length: > 0 } prompt)
                    {
                        facts.LastPrompt = Flatten(prompt);
                    }
                    continue;
                }

                // The metadata carried on every record; the header lines are enough, so we stop
                // looking once we have it.
                if (facts.GitBranch.Length == 0 && line.Contains("\"gitBranch\"", StringComparison.Ordinal))
                {
                    using JsonDocument? doc = TryParse(line);
                    if (doc is not null)
                    {
                        facts.GitBranch = GetString(doc.RootElement, "gitBranch") ?? "";
                        facts.Version = GetString(doc.RootElement, "version") ?? facts.Version;
                        facts.Cwd = GetString(doc.RootElement, "cwd") ?? facts.Cwd;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            // A transcript being written by a live session, or one that vanished mid-scan. What we
            // read up to that point is still worth showing.
        }

        return facts;
    }

    /// <summary>
    /// Pulls the ISO timestamp out of a line without parsing it. Only the first one is kept — the
    /// last-activity time already comes from the file's own timestamp, which is cheaper and just
    /// as accurate.
    /// </summary>
    private static void CaptureTimestamp(string line, ref TranscriptFacts facts)
    {
        if (facts.FirstTimestampTicks > 0)
        {
            return;
        }

        const string Key = "\"timestamp\":\"";
        int start = line.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        start += Key.Length;
        int end = line.IndexOf('"', start);
        if (end > start &&
            DateTime.TryParse(
                line.AsSpan(start, end - start),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime parsed))
        {
            facts.FirstTimestampTicks = parsed.Ticks;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static SessionInfo GetOrAdd(Dictionary<string, SessionInfo> byId, string id)
    {
        if (!byId.TryGetValue(id, out SessionInfo? session))
        {
            session = new SessionInfo { SessionId = id };
            byId[id] = session;
        }

        return session;
    }

    /// <summary>
    /// Turns a project folder name back into a path: <c>F--Code-CascadeUI</c> to
    /// <c>F:\Code\CascadeUI</c>. Lossy — hyphens in real folder names are indistinguishable from
    /// separators — so callers treat the result as a fallback only.
    /// </summary>
    private static string DecodeProjectSlug(string slug)
    {
        if (slug.Length > 3 && slug[1] == '-' && slug[2] == '-' && char.IsLetter(slug[0]))
        {
            return $"{slug[0]}:\\{slug[3..].Replace('-', '\\')}";
        }

        return slug.Replace('-', '\\');
    }

    /// <summary>
    /// Collapses newlines and runs of whitespace so a multi-line prompt fits a table cell. Long
    /// text is left intact; the column ellipsises it.
    /// </summary>
    private static string Flatten(string text)
    {
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        int length = 0;
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            bool isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (!lastWasSpace && length > 0)
                {
                    buffer[length++] = ' ';
                }
            }
            else
            {
                buffer[length++] = c;
            }

            lastWasSpace = isSpace;
        }

        while (length > 0 && buffer[length - 1] == ' ')
        {
            length--;
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// Confirms a process id really belongs to the session that wrote it. Pids get reused, so the
    /// record's stored start time (a Windows FILETIME) is checked against the live process — an
    /// unrelated program inheriting a dead session's pid must not show up as a running session.
    /// </summary>
    private static bool IsProcessAlive(int? pid, string? procStart)
    {
        if (pid is not { } id || id <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(id);

            if (long.TryParse(procStart, out long expectedFileTime))
            {
                long actualFileTime = process.StartTime.ToFileTimeUtc();
                // Allow a small slop: the recorded and queried start times come from different
                // APIs and can disagree in the low bits.
                return Math.Abs(actualFileTime - expectedFileTime) < TimeSpan.TicksPerSecond;
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (InvalidOperationException)
        {
            return false;   // exited between the lookup and the read
        }
    }

    private static IEnumerable<string> ReadLinesSafely(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1 << 16);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static DateTime FromUnixMilliseconds(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
}

/// <summary>What one pass over a transcript yields. Cached on disk between launches.</summary>
internal struct TranscriptFacts
{
    public string Title;
    public string LastPrompt;
    public string GitBranch;
    public string Version;
    public string Cwd;
    public int MessageCount;

    /// <summary>UTC ticks of the earliest message, or 0 when none was parsed.</summary>
    public long FirstTimestampTicks;

    public TranscriptFacts()
    {
        Title = "";
        LastPrompt = "";
        GitBranch = "";
        Version = "";
        Cwd = "";
        MessageCount = 0;
        FirstTimestampTicks = 0;
    }
}
