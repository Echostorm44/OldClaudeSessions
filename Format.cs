namespace OldClaudeSessions;

/// <summary>Compact renderings for the table's numeric columns.</summary>
internal static class Format
{
    /// <summary>
    /// How long ago, in one or two characters plus a unit. After a reboot you are scanning for
    /// "this morning", not for a timestamp, so recency reads better than a date.
    /// </summary>
    public static string Relative(DateTime when)
    {
        if (when == default)
        {
            return "—";
        }

        TimeSpan ago = DateTime.Now - when;

        return ago switch
        {
            { TotalSeconds: < 0 } => "now",
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)ago.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)ago.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)ago.TotalDays}d ago",
            { TotalDays: < 365 } => when.ToString("d MMM"),
            _ => when.ToString("MMM yyyy"),
        };
    }

    /// <summary>Transcript size — a rough proxy for how much work is in there.</summary>
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Wall-clock span from the first message to the last.</summary>
    public static string Duration(TimeSpan? span) => span switch
    {
        null => "—",
        { TotalMinutes: < 1 } => "<1m",
        { TotalMinutes: < 60 } => $"{(int)span.Value.TotalMinutes}m",
        { TotalHours: < 24 } => $"{(int)span.Value.TotalHours}h {span.Value.Minutes}m",
        _ => $"{(int)span.Value.TotalDays}d {span.Value.Hours}h",
    };
}
