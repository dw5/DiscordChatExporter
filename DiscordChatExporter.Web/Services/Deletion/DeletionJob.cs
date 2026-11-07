using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Web.Services;
using DiscordChatExporter.Web.Services.Manifest;

namespace DiscordChatExporter.Web.Services.Deletion;

public sealed class DeletionJob(
    DiscordClient discord,
    TokenKind tokenKind,
    ManifestStore manifest,
    string runDir,
    string planId,
    bool dryRun
)
{
    // Discord only allows bulk-deleting messages younger than 14 days; stay clear of the edge.
    private static readonly TimeSpan BulkDeleteMaxAge = TimeSpan.FromDays(13.5);
    private const int MaxConsecutivePermissionFailures = 20;

    public async Task RunAsync(Job job, CancellationToken cancellationToken)
    {
        var planItemIds = manifest.GetPlanItemIds(planId).ToHashSet(StringComparer.Ordinal);
        if (planItemIds.Count == 0)
        {
            job.Log("Nothing to delete.");
            return;
        }

        // Re-read the live rows for the planned IDs; the set can only shrink from here (a row may
        // have been kept or already deleted since the preview), never grow.
        var messages = manifest
            .GetAllMessages()
            .Where(m => planItemIds.Contains(m.Id))
            .Where(m => !m.Keep)
            .Where(m => m.DeleteStatus is MessageDeleteStatus.None or MessageDeleteStatus.Failed)
            .ToArray();

        var byChannel = messages.GroupBy(m => m.ChannelId, StringComparer.Ordinal).ToArray();

        var pacer =
            tokenKind == TokenKind.Bot ? DeletionPacer.ForBotToken() : DeletionPacer.ForUserToken();

        var logPath = Path.Combine(runDir, "deletion-log.jsonl");
        var total = messages.Length;
        var processed = 0;
        var deleted = 0;
        var skipped = 0;

        job.Log(
            dryRun
                ? $"DRY RUN: would attempt to delete {total} message(s)."
                : $"Deleting {total} message(s) across {byChannel.Length} channel(s)..."
        );

        foreach (var group in byChannel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channelId = group.Key;

            // Newest first, so if interrupted the oldest history is preserved longest
            var ordered = group
                .OrderByDescending(m =>
                    Snowflake.TryParse(m.Id, CultureInfo.InvariantCulture)?.Value ?? 0UL
                )
                .ToArray();

            var consecutivePermissionFailures = 0;

            // Bot tokens can bulk-delete recent messages
            var remaining = new List<MessageRecord>(ordered);
            if (tokenKind == TokenKind.Bot)
            {
                var now = DateTimeOffset.UtcNow;
                var bulkEligible = remaining
                    .Where(m => now - m.Timestamp < BulkDeleteMaxAge)
                    .ToList();

                foreach (var chunk in Chunk(bulkEligible, 100).Where(c => c.Count >= 2))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed += chunk.Count;

                    if (dryRun)
                    {
                        foreach (var m in chunk)
                            await LogAttemptAsync(logPath, m, "bulk", "dry-run");
                        deleted += chunk.Count;
                    }
                    else
                    {
                        try
                        {
                            var result = await discord.BulkDeleteMessagesAsync(
                                Snowflake.Parse(channelId),
                                chunk.Select(m => Snowflake.Parse(m.Id)).ToArray(),
                                cancellationToken
                            );

                            if (result.IsSuccess)
                            {
                                foreach (var m in chunk)
                                {
                                    await manifest.SetMessageDeleteStatusAsync(
                                        m.Id,
                                        MessageDeleteStatus.Deleted,
                                        null
                                    );
                                    await LogAttemptAsync(logPath, m, "bulk", "deleted");
                                }
                                deleted += chunk.Count;
                                foreach (var m in chunk)
                                    remaining.Remove(m);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Fall back to single deletes for this chunk
                            job.Log($"Bulk delete failed ({ex.Message}); falling back.");
                        }
                    }

                    job.ReportProgress((double)processed / Math.Max(1, total));
                }
            }

            foreach (var message in remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (consecutivePermissionFailures >= MaxConsecutivePermissionFailures)
                {
                    job.Log($"Skipping rest of channel {channelId}: repeated permission failures.");
                    foreach (var m in remaining.SkipWhile(x => x.Id != message.Id))
                    {
                        await manifest.SetMessageDeleteStatusAsync(
                            m.Id,
                            MessageDeleteStatus.SkippedNoPermission,
                            "Repeated permission failures"
                        );
                        skipped++;
                    }
                    break;
                }

                processed++;

                if (dryRun)
                {
                    await LogAttemptAsync(logPath, message, "single", "dry-run");
                    deleted++;
                    job.ReportProgress((double)processed / Math.Max(1, total));
                    continue;
                }

                var stopwatch = Stopwatch.StartNew();
                MessageDeletionResult result;
                try
                {
                    result = await discord.DeleteMessageAsync(
                        Snowflake.Parse(channelId),
                        Snowflake.Parse(message.Id),
                        cancellationToken
                    );
                }
                catch (DiscordChatExporterException ex) when (ex.IsFatal)
                {
                    // e.g. invalid token
                    job.Log("Fatal error: " + ex.Message);
                    throw;
                }
                stopwatch.Stop();

                var (status, outcomeLabel) = MapOutcome(result.Outcome);
                if (
                    result.Outcome
                    is MessageDeletionOutcome.MissingPermissions
                        or MessageDeletionOutcome.MissingAccess
                )
                    consecutivePermissionFailures++;
                else
                    consecutivePermissionFailures = 0;

                if (status is MessageDeleteStatus.Deleted or MessageDeleteStatus.Gone)
                    deleted++;
                else
                    skipped++;

                await manifest.SetMessageDeleteStatusAsync(message.Id, status, result.ErrorMessage);
                await LogAttemptAsync(logPath, message, "single", outcomeLabel);

                job.ReportProgress(
                    (double)processed / Math.Max(1, total),
                    $"{deleted} deleted, {skipped} skipped"
                );

                var delay = pacer.NextDelay(stopwatch.Elapsed);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
        }

        await manifest.FlushAsync();
        job.EmitData(
            "summary",
            new
            {
                dryRun,
                total,
                deleted,
                skipped,
            }
        );
        job.Log(
            dryRun
                ? $"DRY RUN complete: {deleted} would be deleted, {skipped} skipped."
                : $"Done. {deleted} deleted, {skipped} skipped."
        );
    }

    private static (MessageDeleteStatus, string) MapOutcome(MessageDeletionOutcome outcome) =>
        outcome switch
        {
            MessageDeletionOutcome.Deleted => (MessageDeleteStatus.Deleted, "deleted"),
            MessageDeletionOutcome.AlreadyGone => (MessageDeleteStatus.Gone, "already-gone"),
            MessageDeletionOutcome.MissingPermissions => (
                MessageDeleteStatus.SkippedNoPermission,
                "missing-permissions"
            ),
            MessageDeletionOutcome.MissingAccess => (
                MessageDeleteStatus.SkippedNoPermission,
                "missing-access"
            ),
            MessageDeletionOutcome.ThreadArchived => (
                MessageDeleteStatus.SkippedArchivedThread,
                "archived-thread"
            ),
            MessageDeletionOutcome.NotDeletable => (
                MessageDeleteStatus.SkippedNotDeletable,
                "not-deletable"
            ),
            _ => (MessageDeleteStatus.Failed, "failed"),
        };

    private static async Task LogAttemptAsync(
        string logPath,
        MessageRecord message,
        string method,
        string outcome
    )
    {
        var line = JsonSerializer.Serialize(
            new
            {
                at = DateTimeOffset.UtcNow,
                channelId = message.ChannelId,
                messageId = message.Id,
                author = message.AuthorName,
                method,
                outcome,
            }
        );
        await File.AppendAllTextAsync(logPath, line + Environment.NewLine);
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }
}
