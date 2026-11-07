using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DiscordChatExporter.Web.Endpoints;
using DiscordChatExporter.Web.Security;
using DiscordChatExporter.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var dataDir = GetArg("--data-dir");
var shouldOpen = args.Contains("--open");

var builder = WebApplication.CreateBuilder(args);

// Local-only by default. Override with --urls or ASPNETCORE_URLS.
if (
    string.IsNullOrEmpty(builder.Configuration["urls"])
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}

// DNS-rebinding guard: only answer to loopback host names unless explicitly extended.
var allowedHosts =
    Environment.GetEnvironmentVariable("DCE_ALLOWED_HOSTS") ?? "localhost;127.0.0.1;[::1]";
builder.Configuration["AllowedHosts"] = allowedHosts;

var accessGate = AccessGate.CreateOrFromEnvironment();
builder.Services.AddSingleton(accessGate);
builder.Services.AddSingleton<DiscordSession>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton(new RunRegistry(dataDir));

// Accept the same token env var as the CLI for a zero-click start
var startupToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

var app = builder.Build();

app.UseHostFiltering();
app.UseMiddleware<AccessGateMiddleware>();

app.MapApi();
app.MapExportFiles();

// The browser UI is embedded in the assembly so a single-file build is self-sufficient.
var assembly = Assembly.GetExecutingAssembly();
app.MapGet("/", () => ServeEmbedded(assembly, "index.html", "text/html; charset=utf-8"));
app.MapGet("/app.js", () => ServeEmbedded(assembly, "app.js", "text/javascript; charset=utf-8"));
app.MapGet("/app.css", () => ServeEmbedded(assembly, "app.css", "text/css; charset=utf-8"));
app.MapGet("/favicon.png", () => ServeEmbedded(assembly, "favicon.png", "image/png"));

if (!string.IsNullOrWhiteSpace(startupToken))
{
    var session = app.Services.GetRequiredService<DiscordSession>();
    try
    {
        await session.SignInAsync(startupToken, default);
        app.Logger.LogInformation("Signed in using DISCORD_TOKEN.");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("DISCORD_TOKEN was set but sign-in failed: {Message}", ex.Message);
    }
}

// Print the access URL (and optionally open the browser) once the server is listening.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address =
        app.Urls.FirstOrDefault()?.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1")
        ?? "http://127.0.0.1:5080";
    var url = $"{address}/?key={accessGate.Key}";

    Console.WriteLine();
    Console.WriteLine("  DiscordChatExporter — Web UI");
    Console.WriteLine("  Open this URL in your browser:");
    Console.WriteLine();
    Console.WriteLine($"      {url}");
    Console.WriteLine();

    if (shouldOpen)
        TryOpenBrowser(url);
});

app.Run();

static IResult ServeEmbedded(Assembly assembly, string suffix, string contentType)
{
    var name = assembly
        .GetManifestResourceNames()
        .FirstOrDefault(n =>
            n.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase) || n == suffix
        );

    if (name is null)
        return Results.NotFound();

    var stream = assembly.GetManifestResourceStream(name);
    return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
}

string? GetArg(string key)
{
    var index = Array.IndexOf(args, key);
    if (index >= 0 && index + 1 < args.Length)
        return args[index + 1];
    return null;
}

static void TryOpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
    }
    catch
    {
        // Best effort; the URL is printed above regardless
    }
}

// Exposed for integration tests
public partial class Program;
