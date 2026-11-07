using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using DiscordChatExporter.Web.Services.Manifest;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class ManifestStoreTests : IAsyncLifetime
{
    private string _dir = "";

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dce-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // best effort
        }
        return Task.CompletedTask;
    }

    private ManifestStore Open() => ManifestStore.Create(Path.Combine(_dir, "m.db"));

    private static MessageRecord Msg(string id, string channel = "c1") =>
        new(
            id,
            channel,
            "author",
            "Author",
            DateTimeOffset.UnixEpoch,
            0,
            false,
            "content " + id,
            0,
            0,
            false,
            MessageDeleteStatus.None
        );

    [Fact]
    public async Task Batched_writes_from_parallel_producers_all_land()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );

        Parallel.For(0, 1000, i => store.EnqueueMessage(Msg((1_000_000 + i).ToString())));

        await store.FlushAsync();

        var (_, _, total) = store.QueryMessages(null, null, null, null, 0, 10);
        total.Should().Be(1000);
    }

    [Fact]
    public async Task Keep_toggle_bumps_revision()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        store.EnqueueMessage(Msg("1"));
        await store.FlushAsync();

        var before = store.GetManifestRevision();
        await store.SetKeepAsync("1", true);
        await store.FlushAsync();

        store.GetManifestRevision().Should().BeGreaterThan(before);
        store.GetMessage("1")!.Keep.Should().BeTrue();
    }

    [Fact]
    public async Task Plan_round_trips_with_items()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );

        await store.CreatePlanAsync("plan1", "{}", "me", "User", 5, ["1", "2", "3"]);
        await store.FlushAsync();

        var plan = store.GetPlan("plan1");
        plan.Should().NotBeNull();
        plan!.ManifestRevision.Should().Be(5);
        store.GetPlanItemIds("plan1").Should().BeEquivalentTo("1", "2", "3");
    }

    [Fact]
    public async Task Delete_status_is_persisted()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        store.EnqueueMessage(Msg("1"));
        await store.FlushAsync();

        await store.SetMessageDeleteStatusAsync("1", MessageDeleteStatus.Deleted, null);
        await store.FlushAsync();

        store.GetMessage("1")!.DeleteStatus.Should().Be(MessageDeleteStatus.Deleted);
    }

    [Fact]
    public async Task Reexport_preserves_keep_flag()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        store.EnqueueMessage(Msg("1"));
        await store.FlushAsync();
        await store.SetKeepAsync("1", true);
        await store.FlushAsync();

        // Re-enqueue the same message id (as a re-export would)
        store.EnqueueMessage(Msg("1"));
        await store.FlushAsync();

        store.GetMessage("1")!.Keep.Should().BeTrue();
    }
}
