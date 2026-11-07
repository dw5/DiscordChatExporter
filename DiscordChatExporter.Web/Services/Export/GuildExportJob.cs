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
        var completed = 0;
        var total = exportable.Length;

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
                            runDir + Path.DirectorySeparatorChar,
                            Path.Combine(runDir, "media") + Path.DirectorySeparatorChar,
                            format,
                            null,
                            null,
                            DiscordChatExporter.Core.Exporting.Partitioning.PartitionLimit.Null,
                            DiscordChatExporter.Core.Exporting.Filtering.MessageFilter.Null,
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
                        if (ExportAssetDownloader.IsDiscordAttachmentUrl(url))
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
        job.Log("Export complete.");
        return runId;
    }

    // Returns false if the message was skipped (not recorded).
    private static bool WriteMessage(ManifestStore manifest, Channel channel, Message message)
    {
        // The thread starter message is rendered by the parent channel's export with the same ID;
        // skip it here so it isn't double-counted or deleted through the thread.
        if (channel.IsThread && !channel.IsThreadContainer && message.Id == channel.Id)
            return false;

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
                MessageDeleteStatus.None
            )
        );

        return true;
    }
}
