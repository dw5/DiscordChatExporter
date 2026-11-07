using System.Collections.Generic;
using DiscordChatExporter.Core.Exporting;

namespace DiscordChatExporter.Web.Services.Export;

public record ExportOptions
{
    // "active", "all", or "none"
    public string Threads { get; init; } = "all";

    public bool IncludeVoiceChannels { get; init; } = true;

    public bool DownloadMedia { get; init; } = true;

    public bool FormatMarkdown { get; init; } = true;

    public int? ParallelLimit { get; init; }

    // When non-empty, only these channel IDs are exported (plus their threads). Empty = all.
    public IReadOnlyList<string> ChannelIds { get; init; } = [];

    // When true, only messages sent by the signed-in account are exported (and thus can be deleted).
    // This is the "back up and remove just my own messages" mode, rather than archiving everything.
    public bool OnlyMyMessages { get; init; }

    public IReadOnlyList<ExportFormat> Formats { get; init; } =
    [ExportFormat.HtmlDark, ExportFormat.Json];
}
