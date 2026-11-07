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

    public IReadOnlyList<ExportFormat> Formats { get; init; } =
    [ExportFormat.HtmlDark, ExportFormat.Json];
}
