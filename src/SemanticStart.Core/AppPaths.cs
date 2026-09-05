namespace SemanticStart.Core;

/// <summary>
/// Well-known on-disk locations. Everything lives under LOCALAPPDATA so the product stays
/// user-level and never needs administrator rights.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SemanticStart");

    /// <summary>SQLite database holding entities, documents, profiles, FTS5 index, and usage stats.</summary>
    public static string IndexDatabase => Path.Combine(Root, "index.sqlite");

    /// <summary>Raw float32 embedding matrix, memory-mapped at query time.</summary>
    public static string VectorFile => Path.Combine(Root, "vectors.bin");

    /// <summary>Downloaded ONNX models and tokenizer vocabularies.</summary>
    public static string ModelsDirectory => Path.Combine(Root, "models");

    /// <summary>Cached online enrichment payloads, so a reindex does not re-fetch the network.</summary>
    public static string EnrichmentCacheDirectory => Path.Combine(Root, "cache", "enrichment");

    /// <summary>Extracted icons, keyed by entity id hash.</summary>
    public static string IconCacheDirectory => Path.Combine(Root, "cache", "icons");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Creates every directory the app writes to. Safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        foreach (var dir in new[]
                 {
                     Root, ModelsDirectory, EnrichmentCacheDirectory,
                     IconCacheDirectory, LogDirectory,
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
