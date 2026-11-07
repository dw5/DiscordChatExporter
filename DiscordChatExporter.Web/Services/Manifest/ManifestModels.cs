using System;

namespace DiscordChatExporter.Web.Services.Manifest;

public enum ChannelExportStatus
{
    Pending,
    Complete,
    CompleteMediaErrors,
    Empty,
    Failed,
    Cancelled,
}

public enum MessageDeleteStatus
{
    None,
    Deleted,
    Gone,
    SkippedNoPermission,
    SkippedArchivedThread,
    SkippedNotDeletable,
    Failed,
}

public record RunRecord(
    string RunId,
    string GuildId,
    string GuildName,
    string MeUserId,
    string TokenKind,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Formats,
    long ManifestRevision
);

public record ChannelRecord(
    string Id,
    string? ParentId,
    string Name,
    string HierarchicalName,
    string Path,
    int Kind,
    bool IsThread,
    bool IsArchived,
    ChannelExportStatus Status,
    long MessageCount,
    string? Error,
    int FailedAttachmentCount
);

public record MessageRecord(
    string Id,
    string ChannelId,
    string AuthorId,
    string AuthorName,
    DateTimeOffset Timestamp,
    int Kind,
    bool IsPinned,
    string Content,
    int AttachmentCount,
    int EmbedCount,
    bool Keep,
    MessageDeleteStatus DeleteStatus
);
