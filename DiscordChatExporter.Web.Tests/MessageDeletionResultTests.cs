using System.Net;
using AwesomeAssertions;
using DiscordChatExporter.Core.Discord;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class MessageDeletionResultTests
{
    [Fact]
    public void NoContent_is_deleted()
    {
        var result = MessageDeletionResult.FromResponse(HttpStatusCode.NoContent, null);
        result.Outcome.Should().Be(MessageDeletionOutcome.Deleted);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Unknown_message_is_already_gone()
    {
        var result = MessageDeletionResult.FromResponse(
            HttpStatusCode.NotFound,
            """{"code":10008,"message":"Unknown Message"}"""
        );
        result.Outcome.Should().Be(MessageDeletionOutcome.AlreadyGone);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Missing_permissions_is_mapped()
    {
        var result = MessageDeletionResult.FromResponse(
            HttpStatusCode.Forbidden,
            """{"code":50013,"message":"Missing Permissions"}"""
        );
        result.Outcome.Should().Be(MessageDeletionOutcome.MissingPermissions);
    }

    [Fact]
    public void Missing_access_is_mapped()
    {
        var result = MessageDeletionResult.FromResponse(
            HttpStatusCode.Forbidden,
            """{"code":50001,"message":"Missing Access"}"""
        );
        result.Outcome.Should().Be(MessageDeletionOutcome.MissingAccess);
    }

    [Fact]
    public void Archived_thread_is_mapped()
    {
        var result = MessageDeletionResult.FromResponse(
            HttpStatusCode.BadRequest,
            """{"code":50083,"message":"Thread is archived"}"""
        );
        result.Outcome.Should().Be(MessageDeletionOutcome.ThreadArchived);
    }

    [Fact]
    public void Unauthorized_is_mapped()
    {
        var result = MessageDeletionResult.FromResponse(HttpStatusCode.Unauthorized, null);
        result.Outcome.Should().Be(MessageDeletionOutcome.Unauthorized);
    }

    [Fact]
    public void Forbidden_without_code_is_missing_permissions()
    {
        var result = MessageDeletionResult.FromResponse(HttpStatusCode.Forbidden, null);
        result.Outcome.Should().Be(MessageDeletionOutcome.MissingPermissions);
    }

    [Fact]
    public void Server_error_is_failed()
    {
        var result = MessageDeletionResult.FromResponse(HttpStatusCode.InternalServerError, null);
        result.Outcome.Should().Be(MessageDeletionOutcome.Failed);
        result.IsSuccess.Should().BeFalse();
    }
}
