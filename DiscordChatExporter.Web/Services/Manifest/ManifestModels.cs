using System;
using System.Collections.Generic;

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
    MessageDeleteStatus DeleteStatus,
    IReadOnlyList<AttachmentRecord>? Attachments = null,
    string? AuthorAvatarUrl = null,
    IReadOnlyList<StickerRecord>? Stickers = null,
    ForwardedRecord? Forwarded = null
);

// One attachment on a message. LocalName is the deterministic file name the exporter writes into
// the run's media/ folder (ExportAssetDownloader.GetTargetFileName), so the UI can load it from
// /files/{runId}/media/{LocalName}. Url is the original (expiring) Discord CDN link, kept as a
// fallback when media wasn't downloaded.
public record AttachmentRecord(
    string FileName,
    string LocalName,
    string Url,
    string Kind, // image | video | audio | file
    bool IsSpoiler
);

// A sticker on a message. Url is Discord's public (unsigned, non-expiring) sticker CDN link, so the
// reader can render it directly. IsImage is false only for Lottie (JSON-animated) stickers, which
// have no static image to show. Display-only: stickers never affect deletion.
public record StickerRecord(string Name, string Url, bool IsImage);

// A forwarded message's own content, carried inline (Discord message snapshot). It has its own text,
// stickers and attachments but no author/identity of its own. Display-only.
public record ForwardedRecord(
    string Content,
    IReadOnlyList<StickerRecord> Stickers,
    IReadOnlyList<AttachmentRecord> Attachments
);
