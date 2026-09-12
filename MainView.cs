using Cascade.UI;
using Updater = Cascade.UI.Installer.Update.Updater;

namespace OldClaudeSessions;

/// <summary>
/// The single window: a table of every Claude Code session this machine remembers, newest first,
/// and a footer that hands you the command to bring the selected one back.
/// </summary>
internal sealed class MainView : Component
{
    // ── Palette (Apple dark) ──────────────────────────────────────────
    private static readonly ColorValue Background = new("#000000");
    private static readonly ColorValue Surface = new("#1C1C1E");
    private static readonly ColorValue SurfaceRaised = new("#2C2C2E");
    private static readonly ColorValue Hairline = new("#3A3A3C");
    private static readonly ColorValue Accent = new("#0A84FF");
    private static readonly ColorValue TextPrimary = new("#FFFFFF");
    private static readonly ColorValue TextSecondary = new("#98989E");
    private static readonly ColorValue TextDim = new("#6C6C70");
    private static readonly ColorValue Live = new("#30D158");
    private static readonly ColorValue Warn = new("#FF9F0A");

    // ── State ─────────────────────────────────────────────────────────
    private List<SessionInfo> sessions = [];
    private SessionInfo? selected;
    private string search = "";
    private bool showPruned;
    private bool scanning;
    private string copied = "";
    private CancellationTokenSource? enrichment;

    // ── Update state ──────────────────────────────────────────────────
    private string? updateVersion;    // non-null when a newer version is available
    private string updateStatus = ""; // shown while downloading/applying
    private bool updateBusy;          // downloading or applying — disables the buttons

    // The row list handed to the table, kept alive between renders. DataTable's cell-text cache is
    // only carried across a re-render when the list is reference-equal, so rebuilding it on every
    // Invalidate would re-format every cell for nothing.
    private List<SessionInfo> visibleRows = [];

    public MainView()
    {
        Reload();

        if (Updater.IsConfigured)
        {
            // This launch reached a healthy state — defuse the updater's crash-rollback for the
            // (possibly just-applied) version.
            Updater.MarkHealthy();

            // Check in the background; if there is one, we ASK before doing anything.
            _ = CheckForUpdateAsync();
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var result = await Task.Run(() => Updater.CheckNowAsync());
            if (result.IsAvailable && result.Version is { Length: > 0 })
            {
                Dispatcher.Post(() => { updateVersion = result.Version; Invalidate(); });
            }
        }
        catch
        {
            // A failed update check is invisible to the user.
        }
    }

    private async Task DownloadAndApplyAsync()
    {
        updateBusy = true;
        updateStatus = "Downloading update…";
        Invalidate();
        try
        {
            await Task.Run(() => Updater.DownloadAsync());
            updateStatus = "Restarting to apply…";
            Invalidate();
            // Hands off to the cascade-update shim: it waits for this process to exit, swaps in the
            // new version, and relaunches. So this call ends the app.
            Updater.ApplyAndRestart();
        }
        catch
        {
            updateBusy = false;
            updateStatus = "Update failed — please try again later.";
            Invalidate();
        }
    }

    // ── Loading ───────────────────────────────────────────────────────

    /// <summary>
    /// Fills the table from the small index files (fast enough to do inline), then kicks off the
    /// transcript pass in the background.
    /// </summary>
    private void Reload()
    {
        enrichment?.Cancel();
        enrichment = new CancellationTokenSource();

        sessions = SessionScanner.ScanFast();
        RefreshRows();

        // Pre-select the newest session: the one you almost certainly opened this tool to get back.
        // The table draws it from the Selected binding (alpha.32), so the footer and the highlight
        // agree from the first frame.
        selected = visibleRows.FirstOrDefault();

        scanning = true;
        _ = EnrichAsync(enrichment.Token);
    }

    private async Task EnrichAsync(CancellationToken cancellation)
    {
        try
        {
            await SessionScanner.EnrichAsync(
                sessions,
                // Called off the UI thread once per session — hop back before touching the tree.
                // Rows are mutated in place, so the table's cached cell text is now stale. A fresh
                // list instance is what tells the reconciler to drop that cache.
                _ => Dispatcher.Post(() =>
                {
                    RefreshRows();
                    Invalidate();
                }),
                cancellation);
        }
        catch (OperationCanceledException)
        {
            return;   // a refresh or a closing window superseded this pass
        }
        catch
        {
            // Enrichment is decoration. The table is already usable without it.
        }

        Dispatcher.Post(() =>
        {
            scanning = false;
            Invalidate();
        });
    }

    /// <summary>
    /// Rebuilds the row list from the current filters. Called from the handlers that can change it
    /// rather than from Render(), which the framework requires to be pure — and which also means
    /// the list stays reference-equal between renders, so the table keeps its cell-text cache.
    /// </summary>
    private void RefreshRows()
    {
        IEnumerable<SessionInfo> query = sessions;

        if (!showPruned)
        {
            query = query.Where(s => s.IsResumable);
        }

        if (search.Trim() is { Length: > 0 } needle)
        {
            query = query.Where(s => s.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        visibleRows = query.ToList();
    }

    // ── Render ────────────────────────────────────────────────────────

    protected override Node Render()
    {
        var children = new List<Node>(5) { Header() };

        if (updateVersion is not null)
        {
            children.Add(UpdateBanner());
        }

        children.Add(Toolbar());
        children.Add(SessionTable().Expand());
        children.Add(Footer());

        return new Column(spacing: 16, children: [.. children])
            .Padding(EdgeInsets.All(20))
            .Background(Background);
    }

    // Lucide "arrow-down-to-line" — the update affordance.
    private static readonly Icon UpdateIcon = new(
        ["M12 17V3", "m6 11 6 6 6-6", "M19 21H5"], new Size(24, 24), 20f, "Update");

    /// <summary>An in-app banner shown when a newer version is available — asks before doing anything.</summary>
    private Node UpdateBanner()
    {
        Node actions = updateBusy
            ? new Label(updateStatus).FontSize(13).Color(TextPrimary)
            : new Row(
                spacing: 10,
                crossAxisAlignment: CrossAxisAlignment.Center,
                children:
                [
                    new Button("Later", () => { updateVersion = null; Invalidate(); }).Variant("ghost").Height(32),
                    new Button("Update now", () => _ = DownloadAndApplyAsync()).Height(32),
                ]);

        return new Row(
            spacing: 12,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                new IconView(UpdateIcon, size: 20).Color(TextPrimary),
                new Label($"Version {updateVersion} is available")
                    .FontSize(14).Color(TextPrimary)
                    .MaxLines(1).Overflow(TextOverflow.Ellipsis).Expand(),
                actions,
            ])
            .Padding(EdgeInsets.Symmetric(horizontal: 16, vertical: 12))
            .Background(Accent)
            .CornerRadius(10);
    }

    private Node Header()
    {
        int resumable = sessions.Count(s => s.IsResumable);
        string subtitle = scanning
            ? $"{resumable} resumable · reading transcripts…"
            : $"{resumable} resumable · {sessions.Count - resumable} pruned";

        return new Row(
            spacing: 12,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                new Column(
                    spacing: 2,
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children:
                    [
                        new Label("Claude Sessions").FontSize(20).Color(TextPrimary),
                        new Label(subtitle).FontSize(12).Color(TextSecondary),
                    ]).Expand(),
                new Button("Refresh", Reload).Variant("ghost").Height(32),
            ]);
    }

    // Lucide "search".
    private static readonly Icon SearchIcon = new(
        ["m21 21-4.34-4.34", "M11 17a6 6 0 1 0 0-12 6 6 0 0 0 0 12z"], new Size(24, 24), 16f, "Search");

    private Node Toolbar() =>
        new Row(
            spacing: 12,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                new TextInput(
                    Bind(search, v => { search = v; RefreshRows(); Invalidate(); }),
                    placeholder: "Search title, prompt, folder, branch or session id",
                    icon: SearchIcon).Expand(),
                new Checkbox(
                    Bind(showPruned, v => { showPruned = v; RefreshRows(); Invalidate(); }),
                    "Show pruned"),
            ]);

    private Node SessionTable()
    {
        var table = new DataTable<SessionInfo>(visibleRows,
        [
            // Auto: as wide as the longest project name actually present, no more.
            DataColumn<SessionInfo>.Text("When", s => Format.Relative(s.LastActivity), width: 88)
                .Resizable(false),
            DataColumn<SessionInfo>.Text("Project", s => s.ProjectName)
                .Width(DataColumnWidth.Auto).MinWidth(110f).MaxWidth(220f)
                .Tooltip(s => s.ProjectPath),
            DataColumn<SessionInfo>.Custom("Title", TitleCell).Width(230).MinWidth(140f)
                .Tooltip(s => s.Title.Length > 0 ? s.Title : s.LastPrompt),
            // Fill: takes whatever the sized columns leave, so the prompt gets the benefit of a
            // wider window. Drag any border to rebalance.
            DataColumn<SessionInfo>.Text("Last prompt", s => s.LastPrompt)
                .Width(DataColumnWidth.Fill).MinWidth(180f)
                .Tooltip(s => s.LastPrompt),
            DataColumn<SessionInfo>.Text("Msgs", s => s.MessageCount >= 0 ? $"{s.MessageCount:N0}" : s.IsResumable ? "…" : "—", width: 76)
                .Align(ColumnAlignment.Right),
            DataColumn<SessionInfo>.Text("Size", s => Format.Size(s.SizeBytes), width: 88)
                .Align(ColumnAlignment.Right),
            // "Span" not "Length": a session resumed over several weeks legitimately spans them,
            // and calling that its length would read as a bug.
            DataColumn<SessionInfo>.Text("Span", s => Format.Duration(s.Duration), width: 84)
                .Align(ColumnAlignment.Right),
            DataColumn<SessionInfo>.Custom("Status", StatusCell).Width(84),
            DataColumn<SessionInfo>.Text("Branch", s => s.GitBranch, width: 96),
        ])
            .Sortable(true)
            .SelectionMode(SelectionMode.Single)
            .Selected(Bind(selected, v => selected = v))
            .OnSelect(session =>
            {
                selected = session;
                copied = "";
                Invalidate();
            })
            .RowContextMenu(RowMenu)
            .HoverHighlight(true)
            .RowHeight(38)
            .EmptyState(EmptyState());

        return new Column(children: [table.Expand()])
            .Background(Surface)
            .CornerRadius(12)
            .Border(Hairline, 1, 12);
    }

    /// <summary>A small accent bar on the selected row.</summary>
    /// <summary>
    /// Claude's own title when it has one, otherwise a dimmed placeholder — an untitled row is
    /// usually a session that ended before Claude got around to naming it.
    /// </summary>
    private static Node TitleCell(SessionInfo session) =>
        new Label(session.Title.Length > 0 ? session.Title : "untitled")
            .FontSize(13)
            .Color(session.Title.Length > 0 ? TextPrimary : TextDim)
            .MaxLines(1)
            .Overflow(TextOverflow.Ellipsis);

    private static Node StatusCell(SessionInfo session)
    {
        (string text, ColorValue color) = session switch
        {
            { Liveness: Liveness.Busy } => ("running", Live),
            { Liveness: Liveness.Idle } => ("open", Live),
            { IsResumable: false } => ("pruned", Warn),
            _ => ("ended", TextSecondary),
        };

        return new Label(text).FontSize(12).Color(color);
    }

    private IReadOnlyList<ContextMenuItem> RowMenu(SessionInfo session) =>
    [
        ContextMenuItem.Action(
            "Copy resume command",
            () => Copy(session.ResumeCommand, "Resume command copied"),
            disabled: !session.IsResumable),
        ContextMenuItem.Action(
            "Copy with cd to project folder",
            () => Copy(session.ResumeCommandWithCd, "Command with cd copied"),
            disabled: !session.IsResumable || session.ProjectPath.Length == 0),
        ContextMenuItem.Separator(),
        ContextMenuItem.Action("Copy session id", () => Copy(session.SessionId, "Session id copied")),
        ContextMenuItem.Action(
            "Copy project path",
            () => Copy(session.ProjectPath, "Project path copied"),
            disabled: session.ProjectPath.Length == 0),
    ];

    // Lucide "inbox" — the empty state.
    private static readonly Icon InboxIcon = new(
        [
            "M22 12h-6l-2 3h-4l-2-3H2",
            "M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z",
        ],
        new Size(24, 24), 24f, "Empty");

    private Node EmptyState() =>
        new Center(
            new Column(
                spacing: 10,
                crossAxisAlignment: CrossAxisAlignment.Center,
                children:
                [
                    new IconView(InboxIcon, size: 40).Color(TextDim),
                    new Label(search.Length > 0 ? "No sessions match your search" : "No sessions found")
                        .FontSize(15).Color(TextPrimary),
                    new Label(
                        search.Length > 0
                            ? "Try a different term, or turn on Show pruned."
                            : $"Looked in {SessionScanner.ClaudeHome}")
                        .FontSize(12).Color(TextSecondary),
                ]));

    // ── Footer: the thing you actually came here for ──────────────────

    // Lucide "copy".
    private static readonly Icon CopyIcon = new(
        [
            "M20 9h-9a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-9a2 2 0 0 0-2-2z",
            "M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1",
        ],
        new Size(24, 24), 18f, "Copy");

    private Node Footer()
    {
        if (selected is not { } session)
        {
            return new Label("Select a session to get its resume command")
                .FontSize(13).Color(TextSecondary)
                .Padding(EdgeInsets.Symmetric(horizontal: 4, vertical: 8));
        }

        return new Column(
            spacing: 10,
            children:
            [
                new Row(
                    spacing: 10,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new Label(session.DisplayTitle)
                            .FontSize(15).Color(TextPrimary)
                            .MaxLines(1).Overflow(TextOverflow.Ellipsis).Expand(),
                        new Label(Detail(session)).FontSize(12).Color(TextSecondary),
                    ]),
                new Row(
                    spacing: 12,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        // The command in full, monospaced — copying is the fast path, but after a
                        // reboot you sometimes just want to read the guid off the screen.
                        new Label(session.IsResumable
                                ? session.ResumeCommand
                                : $"{session.SessionId} — transcript pruned, cannot be resumed")
                            .FontSize(13)
                            .FontFamily(FontFamily.ForCategory(SystemFont.Monospace))
                            .Color(session.IsResumable ? TextPrimary : Warn)
                            .MaxLines(1).Overflow(TextOverflow.Ellipsis)
                            .Padding(EdgeInsets.Symmetric(horizontal: 12, vertical: 10))
                            .Background(SurfaceRaised)
                            .CornerRadius(8)
                            .Expand(),
                        new Button("Copy", () => Copy(session.ResumeCommand, "Copied — paste it in the folder below"), CopyIcon)
                            .Disabled(!session.IsResumable)
                            .Height(40),
                    ]),
                new Row(
                    spacing: 8,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new Label(session.ProjectPath.Length > 0 ? session.ProjectPath : "unknown folder")
                            .FontSize(12).Color(TextSecondary)
                            .MaxLines(1).Overflow(TextOverflow.Ellipsis).Expand(),
                        new Label(copied).FontSize(12).Color(Live),
                    ]),
            ])
            .Padding(EdgeInsets.All(14))
            .Background(Surface)
            .CornerRadius(12)
            .Border(Hairline, 1, 12);
    }

    /// <summary>The right-hand metadata line in the footer — the bits too narrow for a column.</summary>
    private static string Detail(SessionInfo session)
    {
        var parts = new List<string>(4) { session.LastActivity.ToString("ddd d MMM, HH:mm") };

        if (session.Version.Length > 0)
        {
            parts.Add($"cli {session.Version}");
        }

        if (session.GitBranch.Length > 0)
        {
            parts.Add(session.GitBranch);
        }

        if (session.PromptCount > 0)
        {
            parts.Add($"{session.PromptCount:N0} prompts");
        }

        return string.Join("  ·  ", parts);
    }

    private void Copy(string text, string confirmation)
    {
        if (text.Length == 0)
        {
            return;
        }

        copied = confirmation;
        Invalidate();
        _ = Clipboard.WriteTextAsync(text);
    }
}
