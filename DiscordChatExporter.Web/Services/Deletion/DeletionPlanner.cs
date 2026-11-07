using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Web.Services.Manifest;

namespace DiscordChatExporter.Web.Services.Deletion;

public enum MessageSkipReason
{
    None,
    AlreadyGone,
    ChannelNotExported,
    ChannelExcluded,
    NotDeletableKind,
    ManuallyKept,
    Pinned,
    HasAttachments,
    ContainsKeepPhrase,
    OutOfRange,
    ExplicitlyExcluded,
    NotInScope,
    DoesNotMatchFilter,
}

public record ChannelPlanSummary(
    string ChannelId,
    string ChannelName,
    int CandidateCount,
    int KeptCount,
    int IneligibleCount
);

public record DeletionPlan(
    IReadOnlyList<string> CandidateMessageIds,
    IReadOnlyList<ChannelPlanSummary> ChannelSummaries,
    int TotalCandidates,
    int TotalKept
);

// Pure: decides which exported messages are deletion candidates. It never crawls Discord;
// it only ever narrows the set of messages that were actually backed up in the manifest.
public static class DeletionPlanner
{
    // Message kinds that Discord does not allow deleting, or that are rendered from another
    // message (thread starter) and must not be deleted here.
    // https://discord.com/developers/docs/resources/channel#message-object-message-types
    private static readonly HashSet<int> NonDeletableKinds = [1, 2, 3, 4, 5, 14, 15, 16, 17, 21];

    public static bool IsChannelEligible(ChannelRecord channel, DeletionCriteria criteria)
    {
        if (criteria.ExcludedChannelIds.Contains(channel.Id, StringComparer.Ordinal))
            return false;

        return channel.Status switch
        {
            ChannelExportStatus.Complete => true,
            ChannelExportStatus.CompleteMediaErrors => criteria.AllowIncompleteMedia,
            _ => false,
        };
    }

    public static MessageSkipReason Evaluate(
        MessageRecord message,
        ChannelRecord channel,
        DeletionCriteria criteria,
        string deletingUserId,
        IReadOnlySet<string> excludedMessageIds,
        Snowflake? before,
        Snowflake? after
    )
    {
        if (message.DeleteStatus is MessageDeleteStatus.Deleted or MessageDeleteStatus.Gone)
            return MessageSkipReason.AlreadyGone;

        if (criteria.ExcludedChannelIds.Contains(channel.Id, StringComparer.Ordinal))
            return MessageSkipReason.ChannelExcluded;

        if (!IsChannelEligible(channel, criteria))
            return MessageSkipReason.ChannelNotExported;

        if (NonDeletableKinds.Contains(message.Kind))
            return MessageSkipReason.NotDeletableKind;

        // Scope is checked before the "keep" rules so that counts reflect only messages the
        // user could actually act on.
        var inScope = criteria.Scope switch
        {
            DeletionScope.Mine => string.Equals(
                message.AuthorId,
                deletingUserId,
                StringComparison.Ordinal
            ),
            DeletionScope.Everyone => true,
            DeletionScope.Authors => criteria.AuthorIds.Contains(
                message.AuthorId,
                StringComparer.Ordinal
            ),
            _ => false,
        };

        if (!inScope)
            return MessageSkipReason.NotInScope;

        if (message.Keep)
            return MessageSkipReason.ManuallyKept;

        if (criteria.KeepPinned && message.IsPinned)
            return MessageSkipReason.Pinned;

        if (criteria.KeepWithAttachments && message.AttachmentCount > 0)
            return MessageSkipReason.HasAttachments;

        if (
            criteria.KeepIfContains.Any(phrase =>
                !string.IsNullOrEmpty(phrase)
                && message.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return MessageSkipReason.ContainsKeepPhrase;
        }

        if (excludedMessageIds.Contains(message.Id))
            return MessageSkipReason.ExplicitlyExcluded;

        var messageSnowflake = Snowflake.TryParse(message.Id, CultureInfo.InvariantCulture);
        if (messageSnowflake is not null)
        {
            // "before" means: only delete messages older than this point
            if (before is not null && messageSnowflake.Value.Value >= before.Value.Value)
                return MessageSkipReason.OutOfRange;

            // "after" means: only delete messages newer than this point
            if (after is not null && messageSnowflake.Value.Value <= after.Value.Value)
                return MessageSkipReason.OutOfRange;
        }

        // Narrowing filters: when set, the message must also match to be a candidate
        if (
            !string.IsNullOrEmpty(criteria.OnlyContains)
            && !message.Content.Contains(criteria.OnlyContains, StringComparison.OrdinalIgnoreCase)
        )
        {
            return MessageSkipReason.DoesNotMatchFilter;
        }

        if (criteria.OnlyWithLink && !DeletionCriteria.ContainsLink(message.Content))
            return MessageSkipReason.DoesNotMatchFilter;

        if (criteria.OnlyWithAttachment && message.AttachmentCount == 0)
            return MessageSkipReason.DoesNotMatchFilter;

        return MessageSkipReason.None;
    }

    public static DeletionPlan BuildPlan(
        IReadOnlyList<ChannelRecord> channels,
        IReadOnlyList<MessageRecord> messages,
        DeletionCriteria criteria,
        string deletingUserId
    )
    {
        var channelsById = channels.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var excludedMessageIds = criteria.ExcludedMessageIds.ToHashSet(StringComparer.Ordinal);
        var before = criteria.ResolveBefore();
        var after = criteria.ResolveAfter();

        var candidateIds = new List<string>();
        var perChannelCandidates = new Dictionary<string, int>(StringComparer.Ordinal);
        var perChannelKept = new Dictionary<string, int>(StringComparer.Ordinal);
        var perChannelIneligible = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (!channelsById.TryGetValue(message.ChannelId, out var channel))
                continue;

            var reason = Evaluate(
                message,
                channel,
                criteria,
                deletingUserId,
                excludedMessageIds,
                before,
                after
            );

            if (reason == MessageSkipReason.None)
            {
                candidateIds.Add(message.Id);
                Increment(perChannelCandidates, message.ChannelId);
            }
            else if (
                reason
                is MessageSkipReason.ManuallyKept
                    or MessageSkipReason.Pinned
                    or MessageSkipReason.HasAttachments
                    or MessageSkipReason.ContainsKeepPhrase
                    or MessageSkipReason.ExplicitlyExcluded
                    or MessageSkipReason.OutOfRange
            )
            {
                // Messages the user could have deleted but chose to keep (by a rule or manually)
                Increment(perChannelKept, message.ChannelId);
            }
            else
            {
                Increment(perChannelIneligible, message.ChannelId);
            }
        }

        var summaries = channels
            .Select(c => new ChannelPlanSummary(
                c.Id,
                c.HierarchicalName,
                perChannelCandidates.GetValueOrDefault(c.Id),
                perChannelKept.GetValueOrDefault(c.Id),
                perChannelIneligible.GetValueOrDefault(c.Id)
            ))
            .Where(s => s.CandidateCount > 0 || s.KeptCount > 0)
            .OrderByDescending(s => s.CandidateCount)
            .ToArray();

        return new DeletionPlan(
            candidateIds,
            summaries,
            candidateIds.Count,
            perChannelKept.Values.Sum()
        );
    }

    private static void Increment(Dictionary<string, int> map, string key) =>
        map[key] = map.GetValueOrDefault(key) + 1;
}
