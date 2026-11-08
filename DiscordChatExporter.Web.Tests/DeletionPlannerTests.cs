using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using DiscordChatExporter.Web.Services.Deletion;
using DiscordChatExporter.Web.Services.Manifest;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class DeletionPlannerTests
{
    private const string Me = "1000";
    private const string Other = "2000";

    private static ChannelRecord Channel(
        string id,
        ChannelExportStatus status = ChannelExportStatus.Complete
    ) => new(id, null, "chan", "chan", "", 0, false, false, status, 0, null, 0);

    private static MessageRecord Message(
        string id,
        string channelId = "c1",
        string authorId = Me,
        int kind = 0,
        bool pinned = false,
        bool keep = false,
        int attachments = 0,
        string content = "hello",
        MessageDeleteStatus delete = MessageDeleteStatus.None
    ) =>
        new(
            id,
            channelId,
            authorId,
            "author",
            DateTimeOffset.UnixEpoch,
            kind,
            pinned,
            content,
            attachments,
            0,
            keep,
            delete
        );

    private static DeletionPlan Plan(
        DeletionCriteria criteria,
        IReadOnlyList<ChannelRecord> channels,
        IReadOnlyList<MessageRecord> messages
    ) => DeletionPlanner.BuildPlan(channels, messages, criteria, Me);

    [Fact]
    public void Mine_scope_only_selects_own_messages()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Mine },
            [Channel("c1")],
            [Message("1", authorId: Me), Message("2", authorId: Other)]
        );

        plan.CandidateMessageIds.Should().ContainSingle().Which.Should().Be("1");
    }

    [Fact]
    public void Pre_epoch_before_boundary_matches_nothing_not_everything()
    {
        // Regression for the snowflake underflow (R2): "before 2014" (pre-Discord) must delete
        // nothing, not wrap into a huge future snowflake that selects every message.
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, OnlyBefore = "2014-01-01" },
            [Channel("c1")],
            [Message("100"), Message("200")]
        );

        plan.CandidateMessageIds.Should().BeEmpty();
    }

    [Fact]
    public void Non_deletable_skipped_messages_are_not_candidates()
    {
        // Preview must not promise messages Discord already said can't be deleted (R3).
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [
                Message("1", delete: MessageDeleteStatus.SkippedNotDeletable),
                Message("2", delete: MessageDeleteStatus.None),
            ]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Transiently_skipped_messages_remain_candidates()
    {
        // No-permission / archived-thread skips are retryable and stay candidates, matching what
        // the deletion job will re-attempt (R3).
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [
                Message("1", delete: MessageDeleteStatus.SkippedNoPermission),
                Message("2", delete: MessageDeleteStatus.SkippedArchivedThread),
                Message("3", delete: MessageDeleteStatus.Failed),
            ]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("1", "2", "3");
    }

    [Fact]
    public void Everyone_scope_selects_all_deletable()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [Message("1", authorId: Me), Message("2", authorId: Other)]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("1", "2");
    }

    [Fact]
    public void Authors_scope_selects_listed_authors()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Authors, AuthorIds = [Other] },
            [Channel("c1")],
            [Message("1", authorId: Me), Message("2", authorId: Other)]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Pinned_are_kept_by_default()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [Message("1", pinned: true), Message("2")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Pinned_are_deleted_when_keepPinned_false()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, KeepPinned = false },
            [Channel("c1")],
            [Message("1", pinned: true)]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("1");
    }

    [Fact]
    public void Manual_keep_is_respected()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [Message("1", keep: true), Message("2")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Keep_with_attachments_is_respected()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, KeepWithAttachments = true },
            [Channel("c1")],
            [Message("1", attachments: 2), Message("2")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Keep_if_contains_is_case_insensitive()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, KeepIfContains = ["IMPORTANT"] },
            [Channel("c1")],
            [Message("1", content: "this is important"), Message("2", content: "junk")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Excluded_channels_are_skipped()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, ExcludedChannelIds = ["c1"] },
            [Channel("c1"), Channel("c2")],
            [Message("1", channelId: "c1"), Message("2", channelId: "c2")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Excluded_message_ids_are_skipped()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, ExcludedMessageIds = ["1"] },
            [Channel("c1")],
            [Message("1"), Message("2")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Non_deletable_kinds_are_excluded()
    {
        // Kind 21 = ThreadStarterMessage, 1 = RecipientAdd
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [Message("1", kind: 21), Message("2", kind: 1), Message("3", kind: 0)]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("3");
    }

    [Fact]
    public void Already_deleted_rows_are_skipped()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [Channel("c1")],
            [
                Message("1", delete: MessageDeleteStatus.Deleted),
                Message("2", delete: MessageDeleteStatus.Gone),
                Message("3"),
            ]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("3");
    }

    [Fact]
    public void Incomplete_channels_are_excluded()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            [
                Channel("c1", ChannelExportStatus.Failed),
                Channel("c2", ChannelExportStatus.Complete),
            ],
            [Message("1", channelId: "c1"), Message("2", channelId: "c2")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("2");
    }

    [Fact]
    public void Media_error_channels_excluded_unless_overridden()
    {
        var channels = new[] { Channel("c1", ChannelExportStatus.CompleteMediaErrors) };
        var messages = new[] { Message("1", channelId: "c1") };

        var without = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone },
            channels,
            messages
        );
        without.CandidateMessageIds.Should().BeEmpty();

        var with = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, AllowIncompleteMedia = true },
            channels,
            messages
        );
        with.CandidateMessageIds.Should().BeEquivalentTo("1");
    }

    [Fact]
    public void Only_contains_filter_narrows_candidates()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, OnlyContains = "delete me" },
            [Channel("c1")],
            [Message("1", content: "please DELETE ME now"), Message("2", content: "keep this")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("1");
    }

    [Fact]
    public void Only_with_link_filter_narrows_candidates()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, OnlyWithLink = true },
            [Channel("c1")],
            [Message("1", content: "see https://example.com"), Message("2", content: "no link")]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("1");
    }

    [Fact]
    public void Only_with_attachment_filter_narrows_candidates()
    {
        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, OnlyWithAttachment = true },
            [Channel("c1")],
            [Message("1", attachments: 1), Message("2", attachments: 0)]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo("1");
    }

    [Fact]
    public void Before_boundary_only_deletes_older_messages()
    {
        // Snowflake for 2022-01-01 roughly; use IDs where larger = newer
        var older = "900000000000000000";
        var newer = "999000000000000000";
        var boundary = "950000000000000000";

        var plan = Plan(
            new DeletionCriteria { Scope = DeletionScope.Everyone, OnlyBefore = boundary },
            [Channel("c1")],
            [Message(older), Message(newer)]
        );

        plan.CandidateMessageIds.Should().BeEquivalentTo(older);
    }
}
