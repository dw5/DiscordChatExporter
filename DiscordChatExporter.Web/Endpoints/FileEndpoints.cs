using System;
using System.IO;
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
        app.MapGet(
            "/files/{runId}/{**path}",
            (string runId, string path, RunRegistry registry, HttpContext ctx) =>
            {
                var runDir = registry.FindRunDir(runId);
                if (runDir is null)
                    return Results.NotFound();

                var root = Path.GetFullPath(runDir);
                var requested = Path.GetFullPath(Path.Combine(root, path));

                // Prevent path traversal outside the run directory
                if (
                    !requested.StartsWith(
                        root + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                    && requested != root
                )
                {
                    return Results.NotFound();
                }

                if (!File.Exists(requested))
                    return Results.NotFound();

                // Block scripts in served content so a malicious exported message (DCE embeds
                // message text as HTML) can't call the API with the access cookie. We keep the
                // content same-origin (rather than a sandboxed opaque origin) so its own local
                // media subresources still load with the cookie attached.
                ctx.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self' data:; script-src 'none'; style-src 'self' 'unsafe-inline'; "
                    + "img-src 'self' data: blob:; media-src 'self' data: blob:; font-src 'self' data:; "
                    + "frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

                if (!ContentTypes.TryGetContentType(requested, out var contentType))
                    contentType = "application/octet-stream";

                return Results.File(requested, contentType);
            }
        );
    }
}
