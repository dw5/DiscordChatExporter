using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

public record ChannelExportResult(long MessagesExported, IReadOnlyList<string> FailedAssetUrls);

public class ChannelExporter(DiscordClient discord)
{
    public async ValueTask ExportChannelAsync(
        ExportRequest request,
        IProgress<Percentage>? progress = null,
        CancellationToken cancellationToken = default
    ) => await ExportChannelAsync([request], progress, null, cancellationToken);

    // Exports the same channel into multiple outputs (e.g., different formats) while fetching
    // the messages only once. All requests must target the same channel with the same range,
    // filter, and order. The callback is invoked for every message after it has been passed
    // to all outputs.
    public async ValueTask<ChannelExportResult> ExportChannelAsync(
        IReadOnlyList<ExportRequest> requests,
        IProgress<Percentage>? progress,
        Func<Message, CancellationToken, ValueTask>? onMessageExported,
        CancellationToken cancellationToken = default
    )
    {
        if (requests.Count <= 0)
            throw new ArgumentException(
                "At least one export request is required.",
                nameof(requests)
            );

        var request = requests[0];

        foreach (var other in requests.Skip(1))
        {
            if (
                other.Guild.Id != request.Guild.Id
                || other.Channel.Id != request.Channel.Id
                || other.After != request.After
                || other.Before != request.Before
                || other.IsReverseMessageOrder != request.IsReverseMessageOrder
                || !ReferenceEquals(other.MessageFilter, request.MessageFilter)
            )
            {
                throw new ArgumentException(
                    "All export requests must target the same channel with the same range, filter, and order.",
                    nameof(requests)
                );
            }
        }

        // Forum channels don't have messages, they are just a list of threads
        if (request.Channel.IsThreadContainer)
        {
            throw new DiscordChatExporterException(
                $"Channel '{request.Channel.Name}' "
                    + $"of guild '{request.Guild.Name}' "
                    + $"is a forum and cannot be exported directly. "
                    + "You need to pull its threads and export them individually."
            );
        }

        // Build contexts. Secondary contexts share the caches of the primary one.
        var context = new ExportContext(discord, request);
        await context.PopulateChannelsAndRolesAsync(cancellationToken);

        var contexts = new List<ExportContext> { context };
        contexts.AddRange(requests.Skip(1).Select(r => new ExportContext(discord, r, context)));

        // Initialize the exporters before further checks to ensure the files are created even if
        // an exception is thrown after this point.
        var messageExporters = new List<MessageExporter>();
        try
        {
            foreach (var c in contexts)
                messageExporters.Add(new MessageExporter(c));

            // Check if the channel is empty
            if (request.Channel.IsEmpty)
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages; an empty file will be created."
                );
            }

            // Check if the 'before' and 'after' boundaries are valid
            if (
                (
                    request.Before is not null
                    && !request.Channel.MayHaveMessagesBefore(request.Before.Value)
                )
                || (
                    request.After is not null
                    && !request.Channel.MayHaveMessagesAfter(request.After.Value)
                )
            )
            {
                throw new ChannelEmptyException(
                    $"Channel '{request.Channel.Name}' "
                        + $"of guild '{request.Guild.Name}' "
                        + $"does not contain any messages within the specified period; an empty file will be created."
                );
            }

            var messages = !request.IsReverseMessageOrder
                ? discord.GetMessagesAsync(
                    request.Channel.Id,
                    request.After,
                    request.Before,
                    progress,
                    request.StrictMessageDiscovery,
                    cancellationToken
                )
                : discord.GetMessagesInReverseAsync(
                    request.Channel.Id,
                    request.After,
                    request.Before,
                    progress,
                    request.StrictMessageDiscovery,
                    cancellationToken
                );

            await foreach (var message in messages)
            {
                try
                {
                    // Resolve members for referenced users (the cache is shared by all contexts)
                    foreach (var user in message.GetReferencedUsers())
                        await context.PopulateMemberAsync(user, cancellationToken);

                    // Export the message
                    if (request.MessageFilter.IsMatch(message))
                    {
                        foreach (var messageExporter in messageExporters)
                            await messageExporter.ExportMessageAsync(message, cancellationToken);

                        if (onMessageExported is not null)
                            await onMessageExported(message, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    // Provide more context to the exception, to simplify debugging based on error messages
                    throw new DiscordChatExporterException(
                        $"Failed to export message #{message.Id} "
                            + $"in channel '{request.Channel.Name}' (#{request.Channel.Id}) "
                            + $"of guild '{request.Guild.Name} (#{request.Guild.Id})'.",
                        ex is not DiscordChatExporterException dex || dex.IsFatal,
                        ex
                    );
                }
            }
        }
        finally
        {
            // All exporters must be disposed (which finalizes their files), even if one of them fails
            var disposeExceptions = new List<Exception>();
            foreach (var messageExporter in messageExporters)
            {
                try
                {
                    await messageExporter.DisposeAsync();
                }
                catch (Exception ex)
                {
                    disposeExceptions.Add(ex);
                }
            }

            if (disposeExceptions.Count == 1)
                throw disposeExceptions[0];

            if (disposeExceptions.Count > 1)
                throw new AggregateException(disposeExceptions);
        }

        return new ChannelExportResult(
            messageExporters[0].MessagesExported,
            contexts
                .SelectMany(c => c.GetFailedAssetUrls())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        );
    }
}
