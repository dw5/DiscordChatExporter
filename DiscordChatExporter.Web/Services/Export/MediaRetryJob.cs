using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Web.Services.Manifest;

namespace DiscordChatExporter.Web.Services.Export;

// Recovers attachment backups that failed during export, without re-exporting or rewriting any
// HTML/JSON. It downloads only into media/, at the exact deterministic path the export already
// links to, so a recovered file simply makes the existing link work offline.
public sealed class MediaRetryJob(DiscordClient discord, ManifestStore manifest, string runDir)
{
    public async Task RunAsync(Job job, CancellationToken cancellationToken)
    {
        var mediaDir = Path.Combine(runDir, "media");
        Directory.CreateDirectory(mediaDir);
        var downloader = new ExportAssetDownloader(mediaDir + Path.DirectorySeparatorChar, true);

        var failed = manifest.GetFailedAssets(onlyUnrecovered: true);
        var total = failed.Count;
        if (total == 0)
        {
            job.Log("No missing attachments to recover.");
            return;
        }

        job.Log($"Attempting to recover {total} attachment(s)...");
        var recovered = 0;
        var stillMissing = 0;
        var processed = 0;

        foreach (var asset in failed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            // The file name the export links to (stable across re-signed URL variants)
            var expectedName = ExportAssetDownloader.GetTargetFileName(asset.Url);
            var freshUrl = await TryResolveFreshUrlAsync(asset, expectedName, cancellationToken);

            if (freshUrl is null)
            {
                stillMissing++;
                job.ReportProgress((double)processed / total);
                continue;
            }

            try
            {
                var downloadedPath = await downloader.DownloadAsync(freshUrl, cancellationToken);

                // Guard: only claim recovery if the file landed exactly where the export links.
                if (
                    !string.Equals(
                        Path.GetFileName(downloadedPath),
                        expectedName,
                        StringComparison.Ordinal
                    )
                )
                {
                    stillMissing++;
                }
                else
                {
                    await manifest.SetFailedAssetRecoveredAsync(asset.Url, downloadedPath);
                    recovered++;
                }
            }
            catch (Exception ex)
            {
                job.Log($"Failed to recover one attachment: {ex.Message}");
                stillMissing++;
            }

            job.ReportProgress((double)processed / total, $"{recovered} recovered");
        }

        // Flip channels whose attachments are now all recovered to a clean 'complete' status.
        await manifest.FlushAsync();
        foreach (var channel in manifest.GetChannels())
        {
            if (channel.Status != ChannelExportStatus.CompleteMediaErrors)
                continue;

            var remaining = manifest
                .GetFailedAssets(onlyUnrecovered: true)
                .Count(a => a.ChannelId == channel.Id);

            if (remaining == 0)
            {
                await manifest.SetChannelStatusAsync(
                    channel.Id,
                    ChannelExportStatus.Complete,
                    channel.MessageCount,
                    null,
                    0
                );
                job.Log($"Channel '{channel.HierarchicalName}' is now fully backed up.");
            }
        }

        await manifest.FlushAsync();
        job.EmitData(
            "summary",
            new
            {
                total,
                recovered,
                stillMissing,
            }
        );
        job.Log($"Recovery complete: {recovered} recovered, {stillMissing} still missing.");
    }

    private async Task<string?> TryResolveFreshUrlAsync(
        FailedAssetRecord asset,
        string expectedName,
        CancellationToken cancellationToken
    )
    {
        // Without a message id we can only retry the stored URL (may still be valid for a while).
        if (string.IsNullOrEmpty(asset.MessageId))
            return asset.Url;

        try
        {
            var message = await discord.TryGetMessageAsync(
                Snowflake.Parse(asset.ChannelId),
                Snowflake.Parse(asset.MessageId),
                cancellationToken
            );

            if (message is null)
                return asset.Url;

            // Find the attachment whose (normalized) target file name matches the one we need
            var match = message.Attachments.FirstOrDefault(a =>
                string.Equals(
                    ExportAssetDownloader.GetTargetFileName(a.Url),
                    expectedName,
                    StringComparison.Ordinal
                )
            );

            return match?.Url ?? asset.Url;
        }
        catch
        {
            return asset.Url;
        }
    }
}
