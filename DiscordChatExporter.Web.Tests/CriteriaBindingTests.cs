using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using DiscordChatExporter.Web.Services.Deletion;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

// Guards the bug where the browser sends an enum as its name ("Mine") but the server defaulted to
// numeric enums, producing an HTTP 400 on deletion preview.
public class CriteriaBindingTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Deserializes_scope_from_name()
    {
        var json = """{"scope":"Mine","keepPinned":true}""";
        var criteria = JsonSerializer.Deserialize<DeletionCriteria>(json, Options);
        criteria.Should().NotBeNull();
        criteria!.Scope.Should().Be(DeletionScope.Mine);
        criteria.KeepPinned.Should().BeTrue();
    }

    [Fact]
    public void Deserializes_everyone_scope_with_filters()
    {
        var json =
            """{"scope":"Everyone","onlyWithLink":true,"onlyContains":"x","excludedChannelIds":["1","2"]}""";
        var criteria = JsonSerializer.Deserialize<DeletionCriteria>(json, Options);
        criteria!.Scope.Should().Be(DeletionScope.Everyone);
        criteria.OnlyWithLink.Should().BeTrue();
        criteria.OnlyContains.Should().Be("x");
        criteria.ExcludedChannelIds.Should().BeEquivalentTo("1", "2");
    }
}
