using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Web.Services.Manifest;

namespace DiscordChatExporter.Web.Services;

// Owns the data directory and the per-run export folders, and keeps one ManifestStore open per run.
public sealed class RunRegistry : IAsyncDisposable
{
    // Lazy values so the store for a given database is constructed exactly once even under
    // concurrent opens — ConcurrentDictionary.GetOrAdd can otherwise run its factory more than once,
    // which here would start multiple background writers on the same file (R1).
    private readonly ConcurrentDictionary<string, Lazy<ManifestStore>> _stores = new(
        StringComparer.Ordinal
    );

    public RunRegistry(string? dataDirOverride)
    {
        DataDir =
            dataDirOverride
            ?? Environment.GetEnvironmentVariable("DCE_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiscordChatExporter.Web"
            );

        ExportsDir = Path.Combine(DataDir, "exports");
        Directory.CreateDirectory(ExportsDir);
    }

    public string DataDir { get; }
    public string ExportsDir { get; }

    // Creates a new, exclusive run directory. The run id embeds a timestamp for display plus random
    // bytes so that two exports in the same second can never collide into one directory.
    public (string RunId, string Dir) CreateRunDirectory(string guildName, string guildId)
    {
        var guildFolder = Path.Combine(ExportsDir, Sanitize($"{guildName} [{guildId}]"));
        Directory.CreateDirectory(guildFolder);

        // Try a bounded number of fresh ids. We reject ANY pre-existing path (even an empty dir) so
        // a new export can never land on an old backup, and we let real I/O errors (disk full,
        // permissions) propagate instead of spinning forever (R5).
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var runId =
                DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)
                + "_"
                + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().AsSpan(0, 4));

            var dir = Path.Combine(guildFolder, runId);
            if (Directory.Exists(dir) || File.Exists(dir))
                continue; // id collision — try another

            Directory.CreateDirectory(dir);
            return (runId, dir);
        }

        throw new IOException("Could not allocate a unique run directory after many attempts.");
    }

    public ManifestStore OpenManifest(string runDir)
    {
        var dbPath = Path.Combine(runDir, "manifest.db");
        return _stores
            .GetOrAdd(dbPath, p => new Lazy<ManifestStore>(() => ManifestStore.Create(p)))
            .Value;
    }

    // Finds a run directory by its id across all guild folders.
    public string? FindRunDir(string runId)
    {
        if (!Directory.Exists(ExportsDir))
            return null;

        foreach (var guildFolder in Directory.EnumerateDirectories(ExportsDir))
        {
            var candidate = Path.Combine(guildFolder, runId);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public ManifestStore? OpenManifestForRun(string runId)
    {
        var dir = FindRunDir(runId);
        if (dir is null)
            return null;
        if (!File.Exists(Path.Combine(dir, "manifest.db")))
            return null;
        return OpenManifest(dir);
    }

    public IReadOnlyList<RunSummary> ListRuns()
    {
        var runs = new List<RunSummary>();
        if (!Directory.Exists(ExportsDir))
            return runs;

        foreach (var guildFolder in Directory.EnumerateDirectories(ExportsDir))
        foreach (var runDir in Directory.EnumerateDirectories(guildFolder))
        {
            var dbPath = Path.Combine(runDir, "manifest.db");
            if (!File.Exists(dbPath))
                continue;

            var runId = Path.GetFileName(runDir);
            runs.Add(new RunSummary(runId, runDir, Path.GetFileName(guildFolder)));
        }

        return runs.OrderByDescending(r => r.RunId, StringComparer.Ordinal).ToArray();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _stores.Values)
            if (lazy.IsValueCreated)
                await lazy.Value.DisposeAsync();
        _stores.Clear();
    }
}

public record RunSummary(string RunId, string Dir, string GuildFolder);
