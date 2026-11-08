using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Web.Services.Deletion;

public enum DeletionScope
{
    // Only messages authored by the account performing the deletion
    Mine,

    // Every message (requires 'Manage Messages')
    Everyone,

    // Messages authored by the specified users
    Authors,
}

public record DeletionCriteria
{
    public DeletionScope Scope { get; init; } = DeletionScope.Mine;

    public IReadOnlyList<string> AuthorIds { get; init; } = [];

    public IReadOnlyList<string> ExcludedChannelIds { get; init; } = [];

    public bool KeepPinned { get; init; } = true;

    public bool KeepWithAttachments { get; init; }

    public IReadOnlyList<string> KeepIfContains { get; init; } = [];

    // Message IDs explicitly excluded, e.g. pasted or parsed from message links
    public IReadOnlyList<string> ExcludedMessageIds { get; init; } = [];

    // Only delete messages before / after these points (dates or snowflake IDs)
    public string? OnlyBefore { get; init; }

    public string? OnlyAfter { get; init; }

    // Channels whose export finished with missing attachment backups are excluded by default.
    // The user must opt in explicitly, acknowledging the missing-file count.
    public bool AllowIncompleteMedia { get; init; }

    // Undiscord-style narrowing filters: when set, a message must ALSO match to be deleted.
    public string? OnlyContains { get; init; }

    public bool OnlyWithLink { get; init; }

    public bool OnlyWithAttachment { get; init; }

    public Snowflake? ResolveBefore() => ParseBoundary(OnlyBefore);

    public Snowflake? ResolveAfter() => ParseBoundary(OnlyAfter);

    private const long DiscordEpochMs = 1420070400000L;
    private const ulong MaxTimestampMs = (1UL << 42) - 1;

    // Parses a boundary as either a raw snowflake ID or a date. Dates are converted to a snowflake
    // without the unchecked underflow/overflow in Snowflake.FromDate: a date before Discord existed
    // clamps to the smallest snowflake (so "before <pre-2015>" matches nothing) and an absurd future
    // date clamps to the largest, instead of wrapping and silently widening the deletion.
    private static Snowflake? ParseBoundary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var raw))
            return new Snowflake(raw);

        if (
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var instant
            )
        )
        {
            var delta = instant.ToUnixTimeMilliseconds() - DiscordEpochMs;
            if (delta <= 0)
                return Snowflake.Zero;
            var ts = (ulong)delta;
            return new Snowflake(ts > MaxTimestampMs ? ulong.MaxValue : ts << 22);
        }

        return null;
    }

    // Rejects criteria that can't be acted on safely before a plan is ever built. In particular a
    // mistyped date must never silently become "no boundary" and widen what gets deleted.
    public string? Validate()
    {
        if (!string.IsNullOrWhiteSpace(OnlyBefore) && ParseBoundary(OnlyBefore) is null)
            return $"Couldn't read the 'before' value \"{OnlyBefore}\". Use YYYY-MM-DD or a message ID.";

        if (!string.IsNullOrWhiteSpace(OnlyAfter) && ParseBoundary(OnlyAfter) is null)
            return $"Couldn't read the 'after' value \"{OnlyAfter}\". Use YYYY-MM-DD or a message ID.";

        var before = ResolveBefore();
        var after = ResolveAfter();
        if (before is not null && after is not null && after.Value.Value >= before.Value.Value)
            return "The 'after' point must be earlier than the 'before' point.";

        foreach (var reference in ExcludedMessageIds)
            if (ParseMessageReference(reference) is null)
                return $"Couldn't read the protected-message reference \"{reference}\". Use a message ID or link.";

        if (Scope == DeletionScope.Authors && AuthorIds.Count == 0)
            return "Choose at least one author for the 'specific authors' scope.";

        return null;
    }

    // Resolves pasted message links to raw IDs so the server — not just the browser — protects the
    // right messages. Assumes Validate() already passed, so every reference parses.
    public DeletionCriteria WithNormalizedReferences() =>
        this with
        {
            ExcludedMessageIds = ExcludedMessageIds
                .Select(ParseMessageReference)
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };

    // Stable serialization used as part of the plan identity hash
    public string ToCanonicalJson()
    {
        var normalized = new CanonicalForm(
            Scope.ToString(),
            AuthorIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ExcludedChannelIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            KeepPinned,
            KeepWithAttachments,
            KeepIfContains.ToArray(),
            ExcludedMessageIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            OnlyBefore,
            OnlyAfter,
            AllowIncompleteMedia,
            OnlyContains,
            OnlyWithLink,
            OnlyWithAttachment
        );

        return JsonSerializer.Serialize(normalized);
    }

    private record CanonicalForm(
        string Scope,
        string[] AuthorIds,
        string[] ExcludedChannelIds,
        bool KeepPinned,
        bool KeepWithAttachments,
        string[] KeepIfContains,
        string[] ExcludedMessageIds,
        string? OnlyBefore,
        string? OnlyAfter,
        bool AllowIncompleteMedia,
        string? OnlyContains,
        bool OnlyWithLink,
        bool OnlyWithAttachment
    );

    // Lightweight link detection for the "only messages with a link" filter.
    public static bool ContainsLink(string content) =>
        !string.IsNullOrEmpty(content)
        && (
            content.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || content.Contains("https://", StringComparison.OrdinalIgnoreCase)
        );

    // Parses either a raw message ID or a Discord message link, returning the message ID.
    // https://discord.com/channels/<guild>/<channel>/<message>
    public static string? ParseMessageReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        if (trimmed.Contains("discord.com/channels/", StringComparison.OrdinalIgnoreCase))
        {
            var lastSegment = trimmed
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();

            return Snowflake.TryParse(lastSegment, CultureInfo.InvariantCulture) is not null
                ? lastSegment
                : null;
        }

        return Snowflake.TryParse(trimmed, CultureInfo.InvariantCulture) is not null
            ? trimmed
            : null;
    }
}
