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

    public Snowflake? ResolveBefore() => ParseBoundary(OnlyBefore);

    public Snowflake? ResolveAfter() => ParseBoundary(OnlyAfter);

    private static Snowflake? ParseBoundary(string? value) =>
        Snowflake.TryParse(value, CultureInfo.InvariantCulture);

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
            AllowIncompleteMedia
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
        bool AllowIncompleteMedia
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
