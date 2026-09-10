using Cascade.UI;

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

    // The row list handed to the table, kept alive between renders. DataTable's cell-text cache is
    // only carried across a re-render when the list is reference-equal, so rebuilding it on every
    // Invalidate would re-format every cell for nothing.
    private List<SessionInfo> visibleRows = [];

    public MainView() => Reload();

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

        // Keep the selection across a refresh if that session still exists.
        string? previous = selected?.SessionId;
        selected = previous is null
            ? sessions.FirstOrDefault(s => s.IsResumable)
            : sessions.FirstOrDefault(s => s.SessionId == previous) ?? sessions.FirstOrDefault();

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

    protected override Node Render() =>
        new Column(
            spacing: 16,
            children:
            [
                Header(),
                Toolbar(),
                SessionTable().Expand(),
                Footer(),
            ])
            .Padding(EdgeInsets.All(20))
            .Background(Background);

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
                // The label is passed separately rather than to the Checkbox: the control sizes
                // its own label to a width that clips anything past a single short word.
                new Checkbox(Bind(showPruned, v => { showPruned = v; RefreshRows(); Invalidate(); })),
                new Label("Show pruned").FontSize(13).Color(TextSecondary),
            ]);

    private Node SessionTable()
    {
        var table = new DataTable<SessionInfo>(visibleRows,
        [
            // The table's own selection highlight does not survive a re-render — the reconciler
            // carries a table's cell cache across but not its selected row — and this view
            // re-renders on every keystroke and every background enrichment. So the marker is
            // drawn from our own `selected` field, which does survive.
            DataColumn<SessionInfo>.Custom("", SelectionMarker).Width(16),
            DataColumn<SessionInfo>.Text("When", s => Format.Relative(s.LastActivity), width: 88),
            DataColumn<SessionInfo>.Text("Project", s => s.ProjectName, width: 150)
                .Tooltip(s => s.ProjectPath),
            DataColumn<SessionInfo>.Custom("Title", TitleCell).Width(230)
                .Tooltip(s => s.Title.Length > 0 ? s.Title : s.LastPrompt),
            // Explicit pixels, not Fill: DataTable records Width(DataColumnWidth.Fill)/MinWidth but
            // never reads them, so an unsized column silently gets availableWidth / columnCount.
            DataColumn<SessionInfo>.Text("Last prompt", s => s.LastPrompt, width: 320)
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
    private Node SelectionMarker(SessionInfo session) =>
        // A glyph rather than a background-filled box: inside a table cell a childless Column
        // measures to nothing and paints nothing, however it is sized.
        new Label(ReferenceEquals(session, selected) ? "▌" : " ")
            .FontSize(15)
            .Color(Accent);

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
