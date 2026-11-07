using System.Net;
using System.Text.Json;

namespace DiscordChatExporter.Core.Discord;

public enum MessageDeletionOutcome
{
    Deleted,
    AlreadyGone,
    MissingPermissions,
    MissingAccess,
    ThreadArchived,
    NotDeletable,
    RateLimited,
    Unauthorized,
    Failed,
}

public record MessageDeletionResult(
    MessageDeletionOutcome Outcome,
    int? StatusCode = null,
    int? ErrorCode = null,
    string? ErrorMessage = null
)
{
    public bool IsSuccess =>
        Outcome is MessageDeletionOutcome.Deleted or MessageDeletionOutcome.AlreadyGone;

    // https://discord.com/developers/docs/topics/opcodes-and-status-codes#json-json-error-codes
    public static MessageDeletionResult FromResponse(HttpStatusCode statusCode, string? body)
    {
        var status = (int)statusCode;
        if (status is >= 200 and < 300)
            return new MessageDeletionResult(MessageDeletionOutcome.Deleted, status);

        var (errorCode, errorMessage) = TryParseError(body);

        MessageDeletionResult Make(MessageDeletionOutcome outcome) =>
            new(outcome, status, errorCode, errorMessage);

        if (statusCode == HttpStatusCode.Unauthorized)
            return Make(MessageDeletionOutcome.Unauthorized);

        if (statusCode == HttpStatusCode.TooManyRequests)
            return Make(MessageDeletionOutcome.RateLimited);

        return errorCode switch
        {
            // Unknown Message / Unknown Channel: nothing left to delete.
            // This also covers the case where a retried request already succeeded the first time.
            10008 or 10003 => Make(MessageDeletionOutcome.AlreadyGone),
            50013 => Make(MessageDeletionOutcome.MissingPermissions),
            50001 => Make(MessageDeletionOutcome.MissingAccess),
            50083 => Make(MessageDeletionOutcome.ThreadArchived),
            50021 => Make(MessageDeletionOutcome.NotDeletable),
            _ when statusCode == HttpStatusCode.Forbidden => Make(
                MessageDeletionOutcome.MissingPermissions
            ),
            _ => Make(MessageDeletionOutcome.Failed),
        };
    }

    private static (int? Code, string? Message) TryParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, body);

            int? code =
                root.TryGetProperty("code", out var codeJson)
                && codeJson.ValueKind == JsonValueKind.Number
                && codeJson.TryGetInt32(out var codeValue)
                    ? codeValue
                    : null;

            var message =
                root.TryGetProperty("message", out var messageJson)
                && messageJson.ValueKind == JsonValueKind.String
                    ? messageJson.GetString()
                    : null;

            return (code, message);
        }
        catch (JsonException)
        {
            return (null, body);
        }
    }
}
