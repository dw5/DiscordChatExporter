using System;
using AwesomeAssertions;
using DiscordChatExporter.Web.Services.Deletion;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class DeletionPacerTests
{
    private static readonly TimeSpan Throttled = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Bot_baseline_is_zero_but_backs_off_when_throttled()
    {
        var pacer = DeletionPacer.ForBotToken();
        pacer.CurrentDelay.Should().Be(TimeSpan.Zero);

        pacer.NextDelay(Throttled);

        // From a zero baseline, a throttled call must still produce a positive floor
        pacer.CurrentDelay.Should().BeGreaterThan(TimeSpan.Zero);
        pacer.CurrentDelay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Repeated_throttling_increases_delay_up_to_cap()
    {
        var pacer = DeletionPacer.ForBotToken();
        for (var i = 0; i < 50; i++)
            pacer.NextDelay(Throttled);

        pacer.CurrentDelay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        pacer.CurrentDelay.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void User_baseline_is_positive()
    {
        var pacer = DeletionPacer.ForUserToken();
        pacer.CurrentDelay.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Clean_calls_decay_back_toward_base()
    {
        var pacer = DeletionPacer.ForUserToken();

        // Drive the delay up
        for (var i = 0; i < 5; i++)
            pacer.NextDelay(Throttled);
        var elevated = pacer.CurrentDelay;
        elevated.Should().BeGreaterThan(TimeSpan.FromSeconds(1));

        // Then many clean calls bring it down
        for (var i = 0; i < 200; i++)
            pacer.NextDelay(Fast);

        pacer.CurrentDelay.Should().BeLessThan(elevated);
        pacer.CurrentDelay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }
}
