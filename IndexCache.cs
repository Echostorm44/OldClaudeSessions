using System.Text.Json;
using System.Text.Json.Serialization;

namespace OldClaudeSessions;

/// <summary>
/// Remembers what the last run learned from each transcript, so reading a 150 MB archive is a
/// once-ever cost rather than a once-per-launch one. An entry is valid only while the transcript's
/// size still matches: transcripts are append-only, so a changed size means new content and the
/// file is read again.
/// </summary>
internal sealed class IndexCache
{
    private readonly Dictionary<string, CacheEntry> entries;

    private IndexCache(Dictionary<string, CacheEntry> entries) => this.entries = entries;

    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OldClaudeSessions",
        "index.json");

    /// <summary>Loads the cache, returning an empty one if it is missing or unreadable.</summary>
    public static IndexCache Load()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                using FileStream stream = File.OpenRead(CacheFile);
                var loaded = JsonSerializer.Deserialize(stream, CacheJson.Default.CacheFileModel);
                if (loaded?.Entries is { } loadedEntries)
                {
                    return new IndexCache(new Dictionary<string, CacheEntry>(
                        loadedEntries, StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable cache is not worth reporting — we just do the work again.
        }

        return new IndexCache(new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Returns cached facts for a transcript whose size is unchanged since we read it.</summary>
    public bool TryGet(string transcriptPath, long sizeBytes, out TranscriptFacts facts)
    {
        if (entries.TryGetValue(transcriptPath, out CacheEntry? entry) && entry.SizeBytes == sizeBytes)
        {
            facts = new TranscriptFacts
            {
                Title = entry.Title,
                LastPrompt = entry.LastPrompt,
                GitBranch = entry.GitBranch,
                Version = entry.Version,
                Cwd = entry.Cwd,
                MessageCount = entry.MessageCount,
                FirstTimestampTicks = entry.FirstTimestampTicks,
            };
            return true;
        }

        facts = default;
        return false;
    }

    public void Put(string transcriptPath, long sizeBytes, TranscriptFacts facts) =>
        entries[transcriptPath] = new CacheEntry
        {
            SizeBytes = sizeBytes,
            Title = facts.Title,
            LastPrompt = facts.LastPrompt,
            GitBranch = facts.GitBranch,
            Version = facts.Version,
            Cwd = facts.Cwd,
            MessageCount = facts.MessageCount,
            FirstTimestampTicks = facts.FirstTimestampTicks,
        };

    /// <summary>
    /// Writes the cache back, dropping entries for transcripts that no longer exist so the file
    /// does not accumulate pruned sessions forever.
    /// </summary>
    public void Save()
    {
        try
        {
            foreach (string path in entries.Keys.Where(p => !File.Exists(p)).ToList())
            {
                entries.Remove(path);
            }

            string? directory = Path.GetDirectoryName(CacheFile);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream stream = File.Create(CacheFile);
            JsonSerializer.Serialize(
                stream, new CacheFileModel { Entries = entries }, CacheJson.Default.CacheFileModel);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the cache costs a slower next launch, nothing more.
        }
    }
}

/// <summary>The cache file's shape. Keyed by full transcript path.</summary>
internal sealed class CacheFileModel
{
    public Dictionary<string, CacheEntry> Entries { get; set; } = [];
}

internal sealed class CacheEntry
{
    public long SizeBytes { get; set; }
    public string Title { get; set; } = "";
    public string LastPrompt { get; set; } = "";
    public string GitBranch { get; set; } = "";
    public string Version { get; set; } = "";
    public string Cwd { get; set; } = "";
    public int MessageCount { get; set; }
    public long FirstTimestampTicks { get; set; }
}

/// <summary>
/// Source-generated serialization for the cache — the app publishes NativeAOT, where the
/// reflection-based serializer is unavailable.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CacheFileModel))]
internal sealed partial class CacheJson : JsonSerializerContext;
