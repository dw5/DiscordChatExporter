using AwesomeAssertions;
using DiscordChatExporter.Web.Services.Deletion;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class PlanAndCriteriaTests
{
    [Theory]
    [InlineData("123456789012345678", "123456789012345678")]
    [InlineData("https://discord.com/channels/111/222/123456789012345678", "123456789012345678")]
    [InlineData("  123456789012345678  ", "123456789012345678")]
    public void Parses_message_references(string input, string expected) =>
        DeletionCriteria.ParseMessageReference(input).Should().Be(expected);

    [Theory]
    [InlineData("not a snowflake")]
    [InlineData("")]
    [InlineData("https://discord.com/channels/111/222/notanid")]
    public void Rejects_invalid_message_references(string input) =>
        DeletionCriteria.ParseMessageReference(input).Should().BeNull();

    [Fact]
    public void Validate_rejects_an_unparseable_date_boundary()
    {
        // A typo must never silently become "no boundary" and widen the deletion (R1).
        new DeletionCriteria { OnlyBefore = "2026-99-01" }
            .Validate()
            .Should()
            .NotBeNull();
        new DeletionCriteria { OnlyAfter = "garbage" }
            .Validate()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void Validate_rejects_a_reversed_range()
    {
        new DeletionCriteria { OnlyAfter = "2026-02-01", OnlyBefore = "2026-01-01" }
            .Validate()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void Validate_accepts_empty_and_valid_boundaries()
    {
        new DeletionCriteria().Validate().Should().BeNull();
        new DeletionCriteria { OnlyAfter = "2026-01-01", OnlyBefore = "2026-02-01" }
            .Validate()
            .Should()
            .BeNull();
    }

    [Fact]
    public void Validate_rejects_an_unreadable_protected_reference()
    {
        new DeletionCriteria { ExcludedMessageIds = ["not-an-id"] }
            .Validate()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void Normalizes_excluded_message_links_to_ids()
    {
        var criteria = new DeletionCriteria
        {
            ExcludedMessageIds =
            [
                "https://discord.com/channels/111/222/123456789012345678",
                "987654321098765432",
            ],
        }.WithNormalizedReferences();

        criteria
            .ExcludedMessageIds.Should()
            .BeEquivalentTo("123456789012345678", "987654321098765432");
    }

    [Fact]
    public void Canonical_json_is_order_independent()
    {
        var a = new DeletionCriteria { ExcludedChannelIds = ["1", "2", "3"] };
        var b = new DeletionCriteria { ExcludedChannelIds = ["3", "1", "2"] };
        a.ToCanonicalJson().Should().Be(b.ToCanonicalJson());
    }

    [Theory]
    [InlineData(DeletionScope.Mine)]
    [InlineData(DeletionScope.Everyone)]
    [InlineData(DeletionScope.Authors)]
    public void Canonical_json_round_trips_through_FromCanonicalJson(DeletionScope scope)
    {
        // A persisted plan stores ToCanonicalJson(); the reader's review mode reads it back with
        // FromCanonicalJson. Scope is serialized as a string, so a plain enum deserialize would
        // throw — this guards that round-trip for every scope.
        var original = new DeletionCriteria
        {
            Scope = scope,
            AuthorIds = ["11", "22"],
            ExcludedChannelIds = ["3", "1"],
            KeepPinned = false,
            KeepWithAttachments = true,
            KeepIfContains = ["keep", "this"],
            ExcludedMessageIds = ["99"],
            OnlyBefore = "2025-01-01",
            OnlyAfter = "2024-01-01",
            AllowIncompleteMedia = true,
            OnlyContains = "needle",
            OnlyWithLink = true,
            OnlyWithAttachment = true,
        };

        var restored = DeletionCriteria.FromCanonicalJson(original.ToCanonicalJson());

        restored.Scope.Should().Be(scope);
        restored.KeepPinned.Should().BeFalse();
        restored.KeepWithAttachments.Should().BeTrue();
        restored.OnlyContains.Should().Be("needle");
        restored.OnlyWithLink.Should().BeTrue();
        restored.AllowIncompleteMedia.Should().BeTrue();
        restored.KeepIfContains.Should().BeEquivalentTo("keep", "this");
        // The restored criteria must re-serialize identically, proving nothing was dropped.
        restored.ToCanonicalJson().Should().Be(original.ToCanonicalJson());
    }

    [Fact]
    public void PlanId_changes_with_revision()
    {
        var id1 = PlanIdentity.Compute("run", "{}", "user", "User", 1);
        var id2 = PlanIdentity.Compute("run", "{}", "user", "User", 2);
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void PlanId_changes_with_user_and_token_kind()
    {
        var baseId = PlanIdentity.Compute("run", "{}", "userA", "User", 1);
        PlanIdentity.Compute("run", "{}", "userB", "User", 1).Should().NotBe(baseId);
        PlanIdentity.Compute("run", "{}", "userA", "Bot", 1).Should().NotBe(baseId);
    }

    [Fact]
    public void PlanId_is_stable_for_same_inputs()
    {
        PlanIdentity
            .Compute("run", "{}", "user", "User", 1)
            .Should()
            .Be(PlanIdentity.Compute("run", "{}", "user", "User", 1));
    }
}
