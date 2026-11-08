using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Web.Services.Manifest;
using Gress;

namespace DiscordChatExporter.Web.Services.Export;

public sealed class GuildExportJob(
    DiscordClient discord,
    TokenKind tokenKind,
    User currentUser,
    Snowflake guildId,
    ExportOptions options,
    RunRegistry registry
)
{
    public async Task<string> RunAsync(Job job, CancellationToken cancellationToken)
    {
        job.Log("Resolving server...");
        var guild = await discord.GetGuildAsync(guildId, cancellationToken);

        var (runId, runDir) = registry.CreateRunDirectory(guild.Name, guild.Id.ToString());
        job.Log($"Export directory: {runDir}");
        var manifest = registry.OpenManifest(runDir);

        await manifest.InitRunAsync(
            new RunRecord(
                runId,
                guild.Id.ToString(),
                guild.Name,
                currentUser.Id.ToString(),
                tokenKind.ToString(),
                DateTimeOffset.UtcNow,
                null,
                string.Join(",", options.Formats),
                0
            )
        );

        job.EmitData("run", new { runId, guild = guild.Name });

        // 1. Channels
        job.Log("Fetching channels...");
        var channels = new List<Channel>();
        await foreach (var channel in discord.GetGuildChannelsAsync(guild.Id, cancellationToken))
        {
            if (channel.IsCategory)
                continue;
            if (!options.IncludeVoiceChannels && channel.IsVoice)
                continue;
            channels.Add(channel);
        }

        // Restrict to the chosen channels, if any (keep thread containers so their threads come too)
        if (options.ChannelIds.Count > 0)
        {
            var wanted = options.ChannelIds.ToHashSet();
            channels = channels.Where(c => wanted.Contains(c.Id.ToString())).ToList();
        }
        job.Log($"Found {channels.Count} channel(s).");

        // 2. Threads (strict, so a failure is loud rather than a silently short list)
        if (options.Threads is "active" or "all")
        {
            job.Log("Fetching threads (this can take a while)...");
            var threads = new List<Channel>();
            await foreach (
                var thread in discord.GetChannelThreadsStrictAsync(
                    channels,
                    includeArchived: options.Threads == "all",
                    cancellationToken: cancellationToken
                )
            )
            {
                threads.Add(thread);
                if (threads.Count % 25 == 0)
                    job.Log($"Fetched {threads.Count} thread(s)...");
            }
            channels.AddRange(threads);
            job.Log($"Fetched {threads.Count} thread(s).");
        }

        // Forums/media channels are containers; their threads were fetched above
        var exportable = channels.Where(c => !c.IsThreadContainer).ToArray();

        foreach (var channel in exportable)
        {
            await manifest.UpsertChannelAsync(
                new ChannelRecord(
                    channel.Id.ToString(),
                    channel.Parent?.Id.ToString(),
                    channel.Name,
                    channel.GetHierarchicalName(),
                    "",
                    (int)channel.Kind,
                    channel.IsThread,
                    channel.IsArchived,
                    ChannelExportStatus.Pending,
                    0,
                    null,
                    0
                )
            );
        }

        // 3. Export each channel into all requested formats in one pass
        var parallelLimit = options.ParallelLimit ?? (tokenKind == TokenKind.Bot ? 3 : 1);
        var exporter = new ChannelExporter(discord);

        // "Only my messages" narrows the export (and therefore what can be deleted) to the
        // signed-in account's own messages, matched by author ID.
        var messageFilter = options.OnlyMyMessages
            ? DiscordChatExporter.Core.Exporting.Filtering.MessageFilter.Parse(
                $"from:{currentUser.Id}"
            )
            : DiscordChatExporter.Core.Exporting.Filtering.MessageFilter.Null;
        var completed = 0;
        var total = exportable.Length;

        // ExportRequest treats its output/assets paths as templates (%g, %c, …). Our run directory
        // is a literal path that can contain a '%' from the guild name, so escape it to '%%' or the
        // export would write into a different folder than the manifest and /files use (R5).
        var outputBase = runDir.Replace("%", "%%") + Path.DirectorySeparatorChar;
        var mediaBase =
            Path.Combine(runDir, "media").Replace("%", "%%") + Path.DirectorySeparatorChar;

        // Every failed asset URL (attachment or not), collected across the parallel channel exports
        // for the portable summary.json written at the end.
        var failedAssets = new System.Collections.Concurrent.ConcurrentBag<(
            string ChannelId,
            string Url,
            bool IsAttachment
        )>();

        await Parallel.ForEachAsync(
            exportable,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelLimit),
                CancellationToken = cancellationToken,
            },
            async (channel, ct) =>
            {
                var channelId = channel.Id.ToString();
                // Maps each attachment URL to the message it belongs to, so that if its download
                // fails we can later re-fetch that message for a fresh (re-signed) URL.
                var attachmentMessageByUrl = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    var requests = options
                        .Formats.Select(format => new ExportRequest(
                            guild,
                            channel,
                            outputBase,
                            mediaBase,
                            format,
                            null,
                            null,
                            DiscordChatExporter.Core.Exporting.Partitioning.PartitionLimit.Null,
                            messageFilter,
                            false,
                            options.FormatMarkdown,
                            options.DownloadMedia,
                            true,
                            null,
                            false
                        )
                        {
                            LinkFailedAttachmentsLocally = options.DownloadMedia,
                        })
                        .ToArray();

                    var messageCount = 0L;
                    var result = await exporter.ExportChannelAsync(
                        requests,
                        null,
                        async (message, innerCt) =>
                        {
                            if (WriteMessage(manifest, channel, message))
                            {
                                foreach (var attachment in message.Attachments)
                                    attachmentMessageByUrl[attachment.Url] = message.Id.ToString();
                                messageCount++;
                            }
                            await Task.CompletedTask;
                        },
                        ct
                    );

                    foreach (var url in result.FailedAssetUrls)
                    {
                        var isAttachment = ExportAssetDownloader.IsDiscordAttachmentUrl(url);
                        // Record every failed asset for summary.json, not just attachments, so
                        // avatar/emoji/embed-thumbnail failures leave a diagnostic trail (R8).
                        failedAssets.Add((channelId, url, isAttachment));
                        if (isAttachment)
                        {
                            await manifest.RecordFailedAssetAsync(
                                attachmentMessageByUrl.GetValueOrDefault(url, ""),
                                channelId,
                                url
                            );
                        }
                    }

                    var failedAttachments = result.FailedAssetUrls.Count(
                        ExportAssetDownloader.IsDiscordAttachmentUrl
                    );

                    var status =
                        failedAttachments > 0
                            ? ChannelExportStatus.CompleteMediaErrors
                            : ChannelExportStatus.Complete;

                    await manifest.SetChannelStatusAsync(
                        channelId,
                        status,
                        messageCount,
                        null,
                        failedAttachments
                    );
                }
                catch (ChannelEmptyException)
                {
                    await manifest.SetChannelStatusAsync(
                        channelId,
                        ChannelExportStatus.Empty,
                        0,
                        null,
                        0
                    );
                }
                catch (OperationCanceledException)
                {
                    await manifest.SetChannelStatusAsync(
                        channelId,
                        ChannelExportStatus.Cancelled,
                        0,
                        null,
                        0
                    );
                    throw;
                }
                catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                {
                    await manifest.SetChannelStatusAsync(
                        channelId,
                        ChannelExportStatus.Failed,
                        0,
                        ex.Message,
                        0
                    );
                }

                var done = Interlocked.Increment(ref completed);
                job.ReportProgress((double)done / Math.Max(1, total), $"{done}/{total} channels");
            }
        );

        await manifest.SetFinishedAsync(DateTimeOffset.UtcNow);
        await manifest.FlushAsync();
        await WriteSummaryAsync(runId, runDir, guild, manifest, failedAssets, job);
        job.Log("Export complete.");
        return runId;
    }

    // Writes a portable per-channel status + failed-asset snapshot next to the export, so the run is
    // diagnosable without the SQLite manifest. Best-effort: a write failure never fails the export.
    private static async Task WriteSummaryAsync(
        string runId,
        string runDir,
        Guild guild,
        ManifestStore manifest,
        IEnumerable<(string ChannelId, string Url, bool IsAttachment)> failedAssets,
        Job job
    )
    {
        try
        {
            var channels = manifest.GetChannels();
            var summary = new
            {
                runId,
                guild = new { id = guild.Id.ToString(), name = guild.Name },
                generatedAt = DateTimeOffset.UtcNow,
                channels = channels
                    .Select(c => new
                    {
                        id = c.Id,
                        name = c.HierarchicalName,
                        status = c.Status.ToString(),
                        messageCount = c.MessageCount,
                        failedAttachmentCount = c.FailedAttachmentCount,
                        error = c.Error,
                    })
                    .ToArray(),
                failedAssets = failedAssets
                    .GroupBy(f => f.Url, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        url = g.Key,
                        channelId = g.First().ChannelId,
                        isAttachment = g.First().IsAttachment,
                    })
                    .ToArray(),
            };

            var json = System.Text.Json.JsonSerializer.Serialize(
                summary,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
            );
            await File.WriteAllTextAsync(Path.Combine(runDir, "summary.json"), json);
        }
        catch (Exception ex)
        {
            job.Log("Could not write summary.json: " + ex.Message);
        }
    }

    // Returns false if the message was skipped (not recorded).
    private static bool WriteMessage(ManifestStore manifest, Channel channel, Message message)
    {
        // A regular thread's starter message has the same ID as the thread and is also exported by
        // the parent channel, so skip it here to avoid double-counting. But a forum/media post's
        // starter has that same ID while its container is NOT exported, so it must be kept.
        if (
            channel.IsThread
            && channel.Parent is { IsThreadContainer: false }
            && message.Id == channel.Id
        )
        {
            return false;
        }

        var attachments = message.Attachments.Select(ToAttachmentRecord).ToList();

        var stickers =
            message.Stickers.Count == 0 ? null : message.Stickers.Select(ToStickerRecord).ToList();

        var forwarded = message.ForwardedMessage is { } fwd
            ? new ForwardedRecord(
                fwd.Content,
                fwd.Stickers.Select(ToStickerRecord).ToList(),
                fwd.Attachments.Select(ToAttachmentRecord).ToList()
            )
            : null;

        manifest.EnqueueMessage(
            new MessageRecord(
                message.Id.ToString(),
                channel.Id.ToString(),
                message.Author.Id.ToString(),
                message.Author.FullName,
                message.Timestamp,
                (int)message.Kind,
                message.IsPinned,
                message.Content,
                message.Attachments.Count,
                message.Embeds.Count,
                false,
                MessageDeleteStatus.None,
                attachments,
                message.Author.AvatarUrl,
                stickers,
                forwarded
            )
        );

        return true;
    }

    private static AttachmentRecord ToAttachmentRecord(Attachment a) =>
        new(
            a.FileName,
            // The deterministic name the exporter writes into media/, so the UI can load it.
            ExportAssetDownloader.GetTargetFileName(a.Url),
            a.Url,
            a.IsImage ? "image"
                : a.IsVideo ? "video"
                : a.IsAudio ? "audio"
                : "file",
            a.IsSpoiler
        );

    private static StickerRecord ToStickerRecord(Sticker s) => new(s.Name, s.SourceUrl, s.IsImage);
}
