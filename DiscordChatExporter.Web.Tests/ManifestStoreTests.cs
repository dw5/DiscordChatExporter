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
    public async Task Plan_membership_query_returns_only_the_requested_ids_in_the_plan()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        await store.CreatePlanAsync("plan1", "{}", "me", "User", 1, ["1", "2", "3"]);
        await store.FlushAsync();

        // Only "2" is both asked-for and in the plan; "4" isn't in the plan, "1"/"3" weren't asked.
        store.GetPlanItemsAmong("plan1", ["2", "4"]).Should().BeEquivalentTo(new[] { "2" });
        store.GetPlanItemsAmong("plan1", []).Should().BeEmpty();
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

    [Fact]
    public async Task Message_attachments_round_trip_through_query()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        store.EnqueueMessage(
            Msg("1") with
            {
                AttachmentCount = 2,
                Attachments =
                [
                    new AttachmentRecord(
                        "pic.png",
                        "pic-abcde.png",
                        "https://cdn/pic.png",
                        "image",
                        false
                    ),
                    new AttachmentRecord(
                        "clip.mp4",
                        "clip-12345.mp4",
                        "https://cdn/clip.mp4",
                        "video",
                        true
                    ),
                ],
            }
        );
        await store.FlushAsync();

        var (items, _, _) = store.QueryMessages(null, null, null, null, 0, 50);
        var msg = items.Single(m => m.Id == "1");
        msg.Attachments.Should().NotBeNull();
        msg.Attachments!.Should().HaveCount(2);
        msg.Attachments![0].LocalName.Should().Be("pic-abcde.png");
        msg.Attachments![0].Kind.Should().Be("image");
        msg.Attachments![1].IsSpoiler.Should().BeTrue();
    }

    [Fact]
    public async Task Stickers_and_forwarded_message_round_trip_through_query()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        store.EnqueueMessage(
            Msg("1") with
            {
                Stickers = [new StickerRecord("wave", "https://cdn/stickers/1.png", true)],
                Forwarded = new ForwardedRecord(
                    "forwarded text",
                    [new StickerRecord("hi", "https://cdn/stickers/2.png", false)],
                    [
                        new AttachmentRecord(
                            "f.png",
                            "f-abc.png",
                            "https://cdn/f.png",
                            "image",
                            false
                        ),
                    ]
                ),
            }
        );
        await store.FlushAsync();

        var (items, _, _) = store.QueryMessages(null, null, null, null, 0, 50);
        var msg = items.Single(m => m.Id == "1");
        msg.Stickers.Should().ContainSingle(s => s.Name == "wave" && s.IsImage);
        msg.Forwarded.Should().NotBeNull();
        msg.Forwarded!.Content.Should().Be("forwarded text");
        msg.Forwarded.Stickers.Should().ContainSingle(s => s.Name == "hi" && !s.IsImage);
        msg.Forwarded.Attachments.Should().ContainSingle(a => a.LocalName == "f-abc.png");
    }

    [Fact]
    public async Task Reexport_replaces_attachment_rows()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        store.EnqueueMessage(
            Msg("1") with
            {
                Attachments = [new AttachmentRecord("a.png", "a.png", "u", "image", false)],
            }
        );
        await store.FlushAsync();
        store.EnqueueMessage(
            Msg("1") with
            {
                Attachments = [new AttachmentRecord("b.png", "b.png", "u2", "image", false)],
            }
        );
        await store.FlushAsync();

        var (items, _, _) = store.QueryMessages(null, null, null, null, 0, 50);
        items
            .Single(m => m.Id == "1")
            .Attachments!.Should()
            .ContainSingle(a => a.FileName == "b.png");
    }

    [Fact]
    public async Task Failed_assets_are_tracked_per_channel()
    {
        // The same URL failing in two channels must keep both rows, or media retry could promote
        // one channel to Complete while the other's backup is still missing (R4).
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        await store.RecordFailedAssetAsync("", "chanA", "https://cdn/x.png");
        await store.RecordFailedAssetAsync("", "chanB", "https://cdn/x.png");
        await store.FlushAsync();

        var failed = store.GetFailedAssets(onlyUnrecovered: true);
        failed.Select(f => f.ChannelId).Should().BeEquivalentTo("chanA", "chanB");
    }

    [Fact]
    public async Task Planning_snapshot_returns_revision_channels_and_messages_together()
    {
        await using var store = Open();
        await store.InitRunAsync(
            new RunRecord("r", "g", "Guild", "me", "User", DateTimeOffset.UtcNow, null, "Json", 0)
        );
        await store.UpsertChannelAsync(
            new ChannelRecord(
                "c1",
                null,
                "general",
                "Guild / general",
                "general",
                0,
                false,
                false,
                ChannelExportStatus.Complete,
                2,
                null,
                0
            )
        );
        store.EnqueueMessage(Msg("1"));
        store.EnqueueMessage(Msg("2"));
        await store.FlushAsync();

        await store.SetKeepAsync("1", true);
        await store.FlushAsync();
        var expectedRevision = store.GetManifestRevision();

        var (revision, channels, messages) = store.ReadPlanningSnapshot();

        revision.Should().Be(expectedRevision);
        channels.Should().ContainSingle(c => c.Id == "c1");
        messages.Select(m => m.Id).Should().BeEquivalentTo("1", "2");
        messages.Single(m => m.Id == "1").Keep.Should().BeTrue();
    }
}
