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
    private readonly ConcurrentDictionary<string, ManifestStore> _stores = new(
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

        while (true)
        {
            var runId =
                DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)
                + "_"
                + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().AsSpan(0, 4));

            var dir = Path.Combine(guildFolder, runId);
            try
            {
                // Fails if the directory already exists, guaranteeing we never reuse one
                Directory.CreateDirectory(dir);
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    continue;
                return (runId, dir);
            }
            catch (IOException)
            {
                // Collision (extremely unlikely); try another id
            }
        }
    }

    public ManifestStore OpenManifest(string runDir)
    {
        var dbPath = Path.Combine(runDir, "manifest.db");
        return _stores.GetOrAdd(dbPath, ManifestStore.Create);
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
        foreach (var store in _stores.Values)
            await store.DisposeAsync();
        _stores.Clear();
    }
}

public record RunSummary(string RunId, string Dir, string GuildFolder);
