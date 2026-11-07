using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AwesomeAssertions;
using DiscordChatExporter.Core.Exporting;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class AssetDownloaderTests
{
    [Fact]
    public void Target_file_name_is_stable_across_signed_url_variants()
    {
        var a = "https://cdn.discordapp.com/attachments/1/2/photo.png?ex=aaa&is=bbb&hm=ccc";
        var b = "https://cdn.discordapp.com/attachments/1/2/photo.png?ex=zzz&is=yyy&hm=xxx";

        ExportAssetDownloader
            .GetTargetFileName(a)
            .Should()
            .Be(ExportAssetDownloader.GetTargetFileName(b));
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/file.png", true)]
    [InlineData("https://media.discordapp.net/attachments/1/2/file.png", true)]
    [InlineData("https://cdn.discordapp.com/avatars/1/abc.png", false)]
    [InlineData("https://example.com/attachments/1/2/file.png", false)]
    public void Recognizes_discord_attachment_urls(string url, bool expected) =>
        ExportAssetDownloader.IsDiscordAttachmentUrl(url).Should().Be(expected);

    [Fact]
    public async Task Aborted_download_leaves_no_file_at_final_path()
    {
        // A local server that sends a Content-Length larger than the body it actually writes,
        // then drops the connection, so the copy throws mid-stream.
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.ContentLength64 = 10_000;
            var buffer = new byte[100];
            await ctx.Response.OutputStream.WriteAsync(buffer);
            // Abruptly end without sending the promised bytes
            ctx.Response.Abort();
        });

        var dir = Path.Combine(Path.GetTempPath(), "dce-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var downloader = new ExportAssetDownloader(dir + Path.DirectorySeparatorChar, true);
            var url = $"http://127.0.0.1:{port}/attachments/1/2/file.bin";

            var act = async () => await downloader.DownloadAsync(url);
            await act.Should().ThrowAsync<Exception>();

            // No final file and no leftover temp file
            Directory.GetFiles(dir).Should().BeEmpty();
        }
        finally
        {
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch
            {
                // ignore
            }
            Directory.Delete(dir, true);
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
