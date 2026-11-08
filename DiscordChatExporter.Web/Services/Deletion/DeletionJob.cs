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
            .Where(m => IsRetryableStatus(m.DeleteStatus))
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
            var remaining = new List<MessageRecord>(ordered);

            // Bot tokens can bulk-delete recent messages (real runs only). Protected/already-gone
            // rows are re-checked live just before the request, so a message protected in the
            // browser mid-run is never deleted.
            if (tokenKind == TokenKind.Bot && !dryRun)
            {
                var now = DateTimeOffset.UtcNow;
                var bulkEligible = remaining
                    .Where(m => now - m.Timestamp < BulkDeleteMaxAge)
                    .ToList();

                foreach (var rawChunk in Chunk(bulkEligible, 100))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunk = rawChunk.Where(IsStillDeletable).ToList();
                    if (chunk.Count < 2)
                        continue; // too small to bulk; the singles pass will handle these

                    var stopwatch = Stopwatch.StartNew();
                    MessageDeletionResult result;
                    try
                    {
                        result = await discord.BulkDeleteMessagesAsync(
                            Snowflake.Parse(channelId),
                            chunk.Select(m => Snowflake.Parse(m.Id)).ToArray(),
                            cancellationToken
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (DiscordChatExporterException ex) when (ex.IsFatal)
                    {
                        job.Log("Fatal error: " + ex.Message);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Recoverable: record and let the singles pass retry this chunk
                        job.Log($"Bulk delete failed ({ex.Message}); falling back to singles.");
                        foreach (var m in chunk)
                            await LogAttemptAsync(logPath, m, "bulk", "failed");
                        continue;
                    }
                    stopwatch.Stop();

                    if (result.IsSuccess)
                    {
                        consecutivePermissionFailures = 0;
                        foreach (var m in chunk)
                        {
                            await manifest.SetMessageDeleteStatusAsync(
                                m.Id,
                                MessageDeleteStatus.Deleted,
                                null
                            );
                            await LogAttemptAsync(logPath, m, "bulk", "deleted");
                            deleted++;
                            processed++;
                            remaining.Remove(m);
                        }
                    }
                    else
                    {
                        foreach (var m in chunk)
                            await LogAttemptAsync(logPath, m, "bulk", "failed");
                        // leave in remaining for the singles pass

                        // A permission failure won't fix itself for the rest of the channel, so stop
                        // firing bulk requests and let the singles pass hit the channel threshold.
                        if (
                            result.Outcome
                            is MessageDeletionOutcome.MissingPermissions
                                or MessageDeletionOutcome.MissingAccess
                        )
                        {
                            consecutivePermissionFailures++;
                            break;
                        }
                    }

                    job.ReportProgress(
                        (double)processed / Math.Max(1, total),
                        $"{deleted} deleted, {skipped} skipped"
                    );

                    // Pace bulk requests the same way as singles: back off after a slow/rate-limited
                    // call so a zero-baseline bot token still slows down under pressure.
                    var bulkDelay = pacer.NextDelay(stopwatch.Elapsed);
                    if (bulkDelay > TimeSpan.Zero)
                        await Task.Delay(bulkDelay, cancellationToken);
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
                        processed++;
                    }
                    break;
                }

                // Re-check live state so a message protected (or already deleted) since the plan
                // was built is never touched (R2).
                if (!IsStillDeletable(message))
                {
                    skipped++;
                    processed++;
                    continue;
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

    // Re-reads a message's live row so protections or prior deletions made after planning win.
    private bool IsStillDeletable(MessageRecord message)
    {
        var live = manifest.GetMessage(message.Id);
        return live is not null && !live.Keep && IsRetryableStatus(live.DeleteStatus);
    }

    // The statuses the planner also counts as candidates, so preview and execution never disagree:
    // never-tried, a prior soft failure, or a transient skip that can succeed once the cause (a
    // missing permission or an archived thread) is fixed. Deleted/Gone/NotDeletable are terminal.
    private static bool IsRetryableStatus(MessageDeleteStatus status) =>
        status
            is MessageDeleteStatus.None
                or MessageDeleteStatus.Failed
                or MessageDeleteStatus.SkippedNoPermission
                or MessageDeleteStatus.SkippedArchivedThread;

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
