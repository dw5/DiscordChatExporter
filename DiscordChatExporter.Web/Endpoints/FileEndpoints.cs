using System;
using System.IO;
using System.Linq;
using DiscordChatExporter.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace DiscordChatExporter.Web.Endpoints;

public static class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // Serves the backed-up export files for in-browser viewing. The sandbox CSP gives the exported
    // HTML an opaque origin so it can never call the API with the access cookie.
    public static void MapExportFiles(this IEndpointRouteBuilder app)
    {
        // Both the bare run id and any path under it are served by the same handler, so the run
        // root lists just like any subdirectory (a redirect route would shadow the trailing-slash
        // form of the catch-all).
        app.MapGet(
            "/files/{runId}",
            (string runId, RunRegistry registry, HttpContext ctx) => Serve(runId, "", registry, ctx)
        );
        app.MapGet(
            "/files/{runId}/{**path}",
            (string runId, string path, RunRegistry registry, HttpContext ctx) =>
                Serve(runId, path, registry, ctx)
        );
    }

    private static IResult Serve(string runId, string path, RunRegistry registry, HttpContext ctx)
    {
        var runDir = registry.FindRunDir(runId);
        if (runDir is null)
            return Results.NotFound();

        var root = Path.GetFullPath(runDir);
        var requested = Path.GetFullPath(Path.Combine(root, path));

        // Prevent path traversal outside the run directory
        if (
            !requested.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && requested != root
        )
        {
            return Results.NotFound();
        }

        // Block scripts in served content so a malicious exported message (DCE embeds
        // message text as HTML) can't call the API with the access cookie. We keep the
        // content same-origin (rather than a sandboxed opaque origin) so its own local
        // media subresources still load with the cookie attached.
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self' data:; script-src 'none'; style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data: blob:; media-src 'self' data: blob:; font-src 'self' data:; "
            + "frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // A directory (including the run root) renders a browsable listing, so "open the
        // export folder" works from the UI (R5).
        if (Directory.Exists(requested))
            return Results.Content(
                RenderDirectory(runId, root, requested),
                "text/html; charset=utf-8"
            );

        if (!File.Exists(requested))
            return Results.NotFound();

        if (!ContentTypes.TryGetContentType(requested, out var contentType))
            contentType = "application/octet-stream";

        return Results.File(requested, contentType);
    }

    // Renders a minimal, script-free listing of a directory inside an export run. Links are
    // relative so they resolve under /files/{runId}/… and stay inside the sandbox.
    private static string RenderDirectory(string runId, string root, string dir)
    {
        var rel = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
        if (rel == ".")
            rel = "";

        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Export files</title>");
        sb.Append(
            "<style>body{font-family:system-ui,sans-serif;max-width:60rem;margin:2rem auto;"
                + "padding:0 1rem;background:#1e1f22;color:#dbdee1}a{color:#00a8fc;text-decoration:none}"
                + "a:hover{text-decoration:underline}li{margin:.25rem 0;list-style:none}"
                + "ul{padding:0}h1{font-size:1.1rem;color:#f2f3f5}.dir{font-weight:600}"
                + ".sz{color:#949ba4;font-size:.85rem;margin-left:.5rem}</style></head><body>"
        );
        sb.Append("<h1>").Append(Encode("/" + rel)).Append("</h1><ul>");

        if (!string.IsNullOrEmpty(rel))
            sb.Append("<li class=\"dir\"><a href=\"../\">../</a></li>");

        foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sub);
            sb.Append("<li class=\"dir\"><a href=\"")
                .Append(EncodeHref(name))
                .Append("/\">")
                .Append(Encode(name))
                .Append("/</a></li>");
        }

        foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            sb.Append("<li><a href=\"")
                .Append(EncodeHref(name))
                .Append("\">")
                .Append(Encode(name))
                .Append("</a><span class=\"sz\">")
                .Append(FormatSize(size))
                .Append("</span></li>");
        }

        sb.Append("</ul></body></html>");
        return sb.ToString();
    }

    private static string Encode(string s) => System.Net.WebUtility.HtmlEncode(s);

    // A single path segment safe inside an href: URL-escape it (so '#', '?', spaces, etc. stay part
    // of the path), then HTML-encode the result for the attribute (R5).
    private static string EncodeHref(string segment) => Encode(Uri.EscapeDataString(segment));

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}
