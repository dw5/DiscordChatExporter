using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

// When a cache source is provided, the context shares its member, channel, and role caches
// (and its asset downloader, if the asset settings match). This is used when the same channel
// is exported into multiple formats at once, to avoid fetching the same data more than once.
internal class ExportContext(
    DiscordClient discord,
    ExportRequest request,
    ExportContext? cacheSource = null
)
{
    private readonly Dictionary<Snowflake, Member?> _membersById =
        cacheSource?._membersById ?? new();

    private readonly Dictionary<Snowflake, Channel?> _channelsById =
        cacheSource?._channelsById ?? new();

    private readonly Dictionary<Snowflake, Role> _rolesById = cacheSource?._rolesById ?? new();

    private readonly ExportAssetDownloader _assetDownloader =
        cacheSource is not null
        && string.Equals(
            cacheSource.Request.AssetsDirPath,
            request.AssetsDirPath,
            StringComparison.Ordinal
        )
        && cacheSource.Request.ShouldReuseAssets == request.ShouldReuseAssets
            ? cacheSource._assetDownloader
            : new ExportAssetDownloader(request.AssetsDirPath, request.ShouldReuseAssets);

    private readonly List<string> _failedAssetUrls = [];
    private readonly Lock _failedAssetUrlsLock = new();

    // URLs of assets that could not be downloaded, in which case the export references
    // the original URL (or, see ExportRequest.LinkFailedAttachmentsLocally, the expected file path).
    public IReadOnlyList<string> GetFailedAssetUrls()
    {
        lock (_failedAssetUrlsLock)
            return _failedAssetUrls.ToArray();
    }

    public DiscordClient Discord { get; } = discord;

    public ExportRequest Request { get; } = request;

    public DateTimeOffset NormalizeDate(DateTimeOffset instant) =>
        Request.IsUtcNormalizationEnabled ? instant.ToUniversalTime() : instant.ToLocalTime();

    public string FormatDate(DateTimeOffset instant, string format = "g") =>
        NormalizeDate(instant).ToString(format, Request.CultureInfo);

    public async ValueTask PopulateChannelsAndRolesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var channel in Discord.GetGuildChannelsAsync(Request.Guild.Id, cancellationToken)
        )
        {
            _channelsById[channel.Id] = channel;
        }

        await foreach (var role in Discord.GetGuildRolesAsync(Request.Guild.Id, cancellationToken))
        {
            _rolesById[role.Id] = role;
        }
    }

    // Threads are not preloaded, so we resolve them on demand
    public async ValueTask PopulateChannelAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    )
    {
        if (_channelsById.ContainsKey(id))
            return;

        var channel = await Discord.TryGetChannelAsync(id, cancellationToken);

        // Store the result even if it's null, to avoid re-fetching non-existing channels
        _channelsById[id] = channel;
    }

    // Because members cannot be pulled in bulk, we need to populate them on demand
    private async ValueTask PopulateMemberAsync(
        Snowflake id,
        User? fallbackUser,
        CancellationToken cancellationToken = default
    )
    {
        if (_membersById.ContainsKey(id))
            return;

        var member = await Discord.TryGetGuildMemberAsync(Request.Guild.Id, id, cancellationToken);

        // User may have left the guild since they were mentioned.
        // Create a dummy member object based on the user info.
        if (member is null)
        {
            var user = fallbackUser ?? await Discord.TryGetUserAsync(id, cancellationToken);

            // User may have been deleted since they were mentioned
            if (user is not null)
                member = Member.CreateFallback(user);
        }

        // Store the result even if it's null, to avoid re-fetching non-existing members
        _membersById[id] = member;
    }

    public async ValueTask PopulateMemberAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(id, null, cancellationToken);

    public async ValueTask PopulateMemberAsync(
        User user,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(user.Id, user, cancellationToken);

    public Member? TryGetMember(Snowflake id) => _membersById.GetValueOrDefault(id);

    public Channel? TryGetChannel(Snowflake id) => _channelsById.GetValueOrDefault(id);

    public Role? TryGetRole(Snowflake id) => _rolesById.GetValueOrDefault(id);

    public IReadOnlyList<Role> GetUserRoles(Snowflake id) =>
        TryGetMember(id)
            ?.RoleIds.Select(TryGetRole)
            .WhereNotNull()
            .OrderByDescending(r => r.Position)
            .ToArray()
        ?? [];

    public Color? TryGetUserColor(Snowflake id) =>
        GetUserRoles(id).Where(r => r.Color is not null).Select(r => r.Color).FirstOrDefault();

    public async ValueTask<string> ResolveAssetUrlAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (!Request.ShouldDownloadAssets)
            return url;

        try
        {
            var filePath = await _assetDownloader.DownloadAsync(url, cancellationToken);
            return FormatLocalAssetPath(filePath);
        }
        // Try to catch only exceptions related to failed HTTP requests
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/332
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/372
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            lock (_failedAssetUrlsLock)
                _failedAssetUrls.Add(url);

            // Point at the location where the file is expected to be, so that the link starts working
            // as soon as the file gets downloaded there later (e.g., by retrying the failed download).
            if (
                Request.LinkFailedAttachmentsLocally
                && ExportAssetDownloader.IsDiscordAttachmentUrl(url)
            )
            {
                return FormatLocalAssetPath(
                    Path.Combine(
                        Request.AssetsDirPath,
                        ExportAssetDownloader.GetTargetFileName(url)
                    )
                );
            }

            // We don't want this to crash the exporting process in case of failure.
            // TODO: add logging so we can be more liberal with catching exceptions.
            return url;
        }
    }

    private string FormatLocalAssetPath(string filePath)
    {
        var relativeFilePath = Path.GetRelativePath(Request.OutputDirPath, filePath);

        // Prefer the relative path so that the export package can be copied around without breaking references.
        // However, if the assets directory lies outside the export directory, use the absolute path instead.
        var shouldUseAbsoluteFilePath =
            relativeFilePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
            || relativeFilePath.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal
            );

        var optimalFilePath = shouldUseAbsoluteFilePath ? filePath : relativeFilePath;

        // For HTML, the path needs to be properly formatted
        if (Request.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight)
            return Url.EncodeFilePath(optimalFilePath);

        return optimalFilePath;
    }
}
