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
    public void Canonical_json_is_order_independent()
    {
        var a = new DeletionCriteria { ExcludedChannelIds = ["1", "2", "3"] };
        var b = new DeletionCriteria { ExcludedChannelIds = ["3", "1", "2"] };
        a.ToCanonicalJson().Should().Be(b.ToCanonicalJson());
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
