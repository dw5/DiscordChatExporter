using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Web.Services;
using DiscordChatExporter.Web.Services.Deletion;
using DiscordChatExporter.Web.Services.Export;
using DiscordChatExporter.Web.Services.Manifest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DiscordChatExporter.Web.Endpoints;

public static class ApiEndpoints
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        MapSession(app);
        MapGuilds(app);
        MapExports(app);
        MapDeletions(app);
        MapJobs(app);
    }

    private static void MapSession(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/session",
            (DiscordSession session) =>
            {
                var state = session.Current;
                return state is null
                    ? Results.Ok(new { authenticated = false })
                    : Results.Ok(
                        new
                        {
                            authenticated = true,
                            user = Describe(state.User),
                            tokenKind = state.TokenKind.ToString(),
                        }
                    );
            }
        );

        app.MapPost(
            "/api/session",
            async (
                SignInRequest body,
                DiscordSession session,
                JobManager jobs,
                CancellationToken ct
            ) =>
            {
                if (jobs.IsBusy)
                    return Results.Conflict(new { error = "A job is running." });
                if (string.IsNullOrWhiteSpace(body.Token))
                    return Results.BadRequest(new { error = "Token is required." });

                try
                {
                    var state = await session.SignInAsync(body.Token.Trim(), ct);
                    return Results.Ok(
                        new { user = Describe(state.User), tokenKind = state.TokenKind.ToString() }
                    );
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
        );

        app.MapDelete(
            "/api/session",
            (DiscordSession session, JobManager jobs) =>
            {
                if (jobs.IsBusy)
                    return Results.Conflict(new { error = "A job is running." });
                session.SignOut();
                return Results.Ok();
            }
        );
    }

    private static void MapGuilds(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/guilds",
            async (DiscordSession session, CancellationToken ct) =>
            {
                var state = session.Current;
                if (state is null)
                    return Results.Unauthorized();

                var guilds = new List<object>();
                await foreach (var guild in state.Client.GetUserGuildsAsync(ct))
                {
                    if (guild.Id == Guild.DirectMessages.Id)
                        continue;
                    guilds.Add(new { id = guild.Id.ToString(), name = guild.Name });
                }
                return Results.Ok(guilds);
            }
        );
    }

    private static void MapExports(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/exports",
            (
                StartExportRequest body,
                DiscordSession session,
                JobManager jobs,
                RunRegistry registry
            ) =>
            {
                var state = session.Current;
                if (state is null)
                    return Results.Unauthorized();
                if (!Snowflake.TryParse(body.GuildId, CultureInfo.InvariantCulture).HasValue)
                    return Results.BadRequest(new { error = "Invalid server ID." });

                var guildId = Snowflake.Parse(body.GuildId);
                var options = body.Options ?? new ExportOptions();

                var job = jobs.TryStart(
                    JobKind.Export,
                    null,
                    async (j, ct) =>
                    {
                        var exportJob = new GuildExportJob(
                            state.Client,
                            state.TokenKind,
                            state.User,
                            guildId,
                            options,
                            registry
                        );
                        await exportJob.RunAsync(j, ct);
                    }
                );

                return job is null
                    ? Results.Conflict(new { error = "A job is already running." })
                    : Results.Ok(new { jobId = job.Id });
            }
        );

        app.MapGet(
            "/api/exports",
            (RunRegistry registry) =>
            {
                var runs = registry
                    .ListRuns()
                    .Select(r =>
                    {
                        var manifest = registry.OpenManifest(r.Dir);
                        var run = manifest.GetRun();
                        return new
                        {
                            runId = r.RunId,
                            guild = run?.GuildName ?? r.GuildFolder,
                            startedAt = run?.StartedAt,
                            finishedAt = run?.FinishedAt,
                        };
                    })
                    .ToArray();
                return Results.Ok(runs);
            }
        );

        app.MapGet(
            "/api/exports/{runId}",
            (string runId, RunRegistry registry) =>
            {
                var manifest = registry.OpenManifestForRun(runId);
                if (manifest is null)
                    return Results.NotFound();

                var run = manifest.GetRun();
                var channels = manifest
                    .GetChannels()
                    .Select(c => new
                    {
                        id = c.Id,
                        name = c.HierarchicalName,
                        status = c.Status.ToString(),
                        messageCount = c.MessageCount,
                        failedAttachmentCount = c.FailedAttachmentCount,
                        error = c.Error,
                    })
                    .ToArray();

                return Results.Ok(
                    new
                    {
                        run = new
                        {
                            run?.RunId,
                            run?.GuildName,
                            run?.MeUserId,
                            run?.TokenKind,
                            run?.StartedAt,
                            run?.FinishedAt,
                            run?.ManifestRevision,
                        },
                        channels,
                    }
                );
            }
        );

        app.MapGet(
            "/api/exports/{runId}/messages",
            (
                string runId,
                RunRegistry registry,
                string? channel,
                string? author,
                string? q,
                bool? keep,
                int? cursor
            ) =>
            {
                var manifest = registry.OpenManifestForRun(runId);
                if (manifest is null)
                    return Results.NotFound();

                var (items, next, total) = manifest.QueryMessages(
                    channel,
                    author,
                    q,
                    keep,
                    cursor ?? 0,
                    100
                );

                return Results.Ok(
                    new
                    {
                        total,
                        nextCursor = next,
                        items = items.Select(m => new
                        {
                            m.Id,
                            m.ChannelId,
                            m.AuthorName,
                            m.Timestamp,
                            m.Content,
                            m.IsPinned,
                            m.AttachmentCount,
                            m.Keep,
                            deleteStatus = m.DeleteStatus.ToString(),
                        }),
                    }
                );
            }
        );

        app.MapPut(
            "/api/exports/{runId}/messages/{messageId}/keep",
            async (string runId, string messageId, KeepRequest body, RunRegistry registry) =>
            {
                var manifest = registry.OpenManifestForRun(runId);
                if (manifest is null)
                    return Results.NotFound();

                await manifest.SetKeepAsync(messageId, body.Keep);
                await manifest.FlushAsync();
                return Results.Ok();
            }
        );

        app.MapPost(
            "/api/exports/{runId}/retry-media",
            (string runId, DiscordSession session, JobManager jobs, RunRegistry registry) =>
            {
                var state = session.Current;
                if (state is null)
                    return Results.Unauthorized();
                var dir = registry.FindRunDir(runId);
                if (dir is null)
                    return Results.NotFound();

                var job = jobs.TryStart(
                    JobKind.MediaRetry,
                    runId,
                    async (j, ct) =>
                    {
                        var manifest = registry.OpenManifest(dir);
                        var retry = new MediaRetryJob(state.Client, manifest, dir);
                        await retry.RunAsync(j, ct);
                    }
                );

                return job is null
                    ? Results.Conflict(new { error = "A job is already running." })
                    : Results.Ok(new { jobId = job.Id });
            }
        );
    }

    private static void MapDeletions(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/exports/{runId}/deletions/preview",
            async (
                string runId,
                DeletionCriteria criteria,
                DiscordSession session,
                RunRegistry registry
            ) =>
            {
                var state = session.Current;
                if (state is null)
                    return Results.Unauthorized();
                var manifest = registry.OpenManifestForRun(runId);
                if (manifest is null)
                    return Results.NotFound();

                var deletingUserId = state.User.Id.ToString();
                var plan = DeletionPlanner.BuildPlan(
                    manifest.GetChannels(),
                    manifest.GetAllMessages(),
                    criteria,
                    deletingUserId
                );

                var revision = manifest.GetManifestRevision();
                var planId = PlanIdentity.Compute(
                    runId,
                    criteria.ToCanonicalJson(),
                    deletingUserId,
                    state.TokenKind.ToString(),
                    revision
                );

                await manifest.CreatePlanAsync(
                    planId,
                    criteria.ToCanonicalJson(),
                    deletingUserId,
                    state.TokenKind.ToString(),
                    revision,
                    plan.CandidateMessageIds
                );
                await manifest.FlushAsync();

                var run = manifest.GetRun();
                return Results.Ok(
                    new
                    {
                        planId,
                        totalCandidates = plan.TotalCandidates,
                        totalKept = plan.TotalKept,
                        channels = plan.ChannelSummaries,
                        deletingUser = Describe(state.User),
                        exportingUserId = run?.MeUserId,
                        accountMismatch = run?.MeUserId != deletingUserId,
                    }
                );
            }
        );

        app.MapPost(
            "/api/exports/{runId}/deletions",
            (
                string runId,
                StartDeletionRequest body,
                DiscordSession session,
                JobManager jobs,
                RunRegistry registry
            ) =>
            {
                var state = session.Current;
                if (state is null)
                    return Results.Unauthorized();
                var dir = registry.FindRunDir(runId);
                if (dir is null)
                    return Results.NotFound();
                var manifest = registry.OpenManifest(dir);

                var plan = manifest.GetPlan(body.PlanId);
                if (plan is null)
                    return Results.Conflict(new { error = "Unknown plan; please preview again." });

                var deletingUserId = state.User.Id.ToString();
                var currentRevision = manifest.GetManifestRevision();

                // The plan must still describe the same account and manifest state it was built for.
                if (
                    plan.UserId != deletingUserId
                    || plan.TokenKind != state.TokenKind.ToString()
                    || plan.ManifestRevision != currentRevision
                    || DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromHours(1)
                )
                {
                    return Results.Conflict(
                        new { error = "The export or options changed; please preview again." }
                    );
                }

                var run = manifest.GetRun();
                var expectedConfirm = run?.GuildName ?? "";
                if (
                    !body.DryRun
                    && !string.Equals(
                        body.Confirm?.Trim(),
                        expectedConfirm,
                        StringComparison.Ordinal
                    )
                )
                {
                    return Results.BadRequest(
                        new { error = "Type the server name exactly to confirm." }
                    );
                }

                var job = jobs.TryStart(
                    JobKind.Deletion,
                    runId,
                    async (j, ct) =>
                    {
                        var deletionJob = new DeletionJob(
                            state.Client,
                            state.TokenKind,
                            manifest,
                            dir,
                            body.PlanId,
                            body.DryRun
                        );
                        await deletionJob.RunAsync(j, ct);
                    }
                );

                return job is null
                    ? Results.Conflict(new { error = "A job is already running." })
                    : Results.Ok(new { jobId = job.Id });
            }
        );
    }

    private static void MapJobs(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/jobs/current",
            (JobManager jobs) =>
            {
                var job = jobs.Current;
                return job is null
                    ? Results.Ok(new { running = false })
                    : Results.Ok(
                        new
                        {
                            running = job.State == JobState.Running,
                            jobId = job.Id,
                            kind = job.Kind.ToString(),
                            state = job.State.ToString(),
                            runId = job.RunId,
                            progress = job.Progress,
                        }
                    );
            }
        );

        app.MapPost(
            "/api/jobs/{jobId}/cancel",
            (string jobId, JobManager jobs) =>
            {
                var job = jobs.Get(jobId);
                if (job is null)
                    return Results.NotFound();
                job.Cancel();
                return Results.Ok();
            }
        );

        app.MapGet(
            "/api/jobs/{jobId}/events",
            async (string jobId, JobManager jobs, HttpContext ctx) =>
            {
                var job = jobs.Get(jobId);
                if (job is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                var reader = job.Subscribe();
                await foreach (var evt in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(evt.Payload);
                    await ctx.Response.WriteAsync(
                        $"event: {evt.Type}\ndata: {json}\n\n",
                        ctx.RequestAborted
                    );
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
        );
    }

    private static object Describe(User user) =>
        new
        {
            id = user.Id.ToString(),
            name = user.FullName,
            displayName = user.DisplayName,
            isBot = user.IsBot,
        };

    public record SignInRequest(string Token);

    public record StartExportRequest(string GuildId, ExportOptions? Options);

    public record KeepRequest(bool Keep);

    public record StartDeletionRequest(string PlanId, string? Confirm, bool DryRun);
}
