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
                if (string.IsNullOrWhiteSpace(body.Token))
                    return Results.BadRequest(new { error = "Token is required." });

                // Hold the session-change gate across the whole sign-in so a job can't start
                // against the old account mid-swap (R4).
                if (!jobs.TryBeginSessionChange())
                    return Results.Conflict(new { error = "A job is running." });
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
                finally
                {
                    jobs.EndSessionChange();
                }
            }
        );

        app.MapDelete(
            "/api/session",
            (DiscordSession session, JobManager jobs) =>
            {
                if (!jobs.TryBeginSessionChange())
                    return Results.Conflict(new { error = "A job is running." });
                try
                {
                    session.SignOut();
                    return Results.Ok();
                }
                finally
                {
                    jobs.EndSessionChange();
                }
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
                    guilds.Add(
                        new
                        {
                            id = guild.Id.ToString(),
                            name = guild.Name,
                            icon = guild.IconUrl,
                        }
                    );
                }
                return Results.Ok(guilds);
            }
        );

        app.MapGet(
            "/api/guilds/{guildId}/channels",
            async (string guildId, DiscordSession session, CancellationToken ct) =>
            {
                var state = session.Current;
                if (state is null)
                    return Results.Unauthorized();
                if (!Snowflake.TryParse(guildId, CultureInfo.InvariantCulture).HasValue)
                    return Results.BadRequest(new { error = "Invalid server ID." });

                var channels = new List<object>();
                await foreach (
                    var channel in state.Client.GetGuildChannelsAsync(Snowflake.Parse(guildId), ct)
                )
                {
                    // Categories are headers, not exportable; threads are pulled in automatically
                    if (channel.IsCategory)
                        continue;
                    channels.Add(
                        new
                        {
                            id = channel.Id.ToString(),
                            name = channel.Name,
                            category = channel.Parent?.Name,
                            isVoice = channel.IsVoice,
                            isForum = channel.IsThreadContainer,
                        }
                    );
                }
                return Results.Ok(channels);
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
                // DMs are out of scope for v1 (export and deletion). Reject the DM pseudo-guild
                // server-side so a direct API call can't create a run the UI never offers (R6).
                if (guildId == Guild.DirectMessages.Id)
                    return Results.BadRequest(
                        new { error = "Direct messages aren't supported — choose a server." }
                    );
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
                    },
                    () => ReferenceEquals(session.Current, state)
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
                            run?.GuildId,
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
                int? cursor,
                string? planId
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

                // When the reader is opened to review a specific deletion plan, tag each message
                // with the SAME decision that will run: it's deleted iff it's in the plan's frozen
                // candidate set AND isn't now protected or already gone (execution only ever shrinks
                // that set). Otherwise show why it's kept, via the same pure Evaluate the plan used.
                Func<MessageRecord, (bool WillDelete, string? KeptReason)>? decide = null;
                if (!string.IsNullOrEmpty(planId) && manifest.GetPlan(planId) is { } planRow)
                {
                    // Membership for just this page's messages — never the whole (possibly millions)
                    // candidate set — so review paging stays cheap on full-server archives.
                    var candidateSet = manifest.GetPlanItemsAmong(
                        planId,
                        items.Select(m => m.Id).ToArray()
                    );
                    var planCriteria = DeletionCriteria.FromCanonicalJson(planRow.CriteriaJson);
                    var channelsById = manifest
                        .GetChannels()
                        .ToDictionary(c => c.Id, StringComparer.Ordinal);
                    var planExcluded = planCriteria.ExcludedMessageIds.ToHashSet(
                        StringComparer.Ordinal
                    );
                    var planBefore = planCriteria.ResolveBefore();
                    var planAfter = planCriteria.ResolveAfter();

                    decide = m =>
                    {
                        // A message is actually deleted iff it's in the plan, isn't now protected,
                        // and its delete-status is still retryable — the exact predicate execution
                        // uses. A terminal status (deleted, gone, or not-deletable) is never run.
                        var retryable = DeletionJob.IsRetryableStatus(m.DeleteStatus);
                        var willDelete = candidateSet.Contains(m.Id) && !m.Keep && retryable;
                        if (willDelete)
                            return (true, null);
                        if (!retryable)
                            return (
                                false,
                                m.DeleteStatus
                                    is MessageDeleteStatus.Deleted
                                        or MessageDeleteStatus.Gone
                                    ? "already gone"
                                    : "can’t be deleted"
                            );
                        var reason = channelsById.TryGetValue(m.ChannelId, out var ch)
                            ? KeptReasonLabel(
                                DeletionPlanner.Evaluate(
                                    m,
                                    ch,
                                    planCriteria,
                                    planRow.UserId,
                                    planExcluded,
                                    planBefore,
                                    planAfter
                                )
                            )
                            : null;
                        return (false, reason);
                    };
                }

                return Results.Ok(
                    new
                    {
                        total,
                        nextCursor = next,
                        items = items.Select(m =>
                        {
                            var d = decide?.Invoke(m);
                            return new
                            {
                                m.Id,
                                m.ChannelId,
                                m.AuthorId,
                                m.AuthorName,
                                m.AuthorAvatarUrl,
                                m.Timestamp,
                                m.Content,
                                m.IsPinned,
                                m.AttachmentCount,
                                m.EmbedCount,
                                m.Keep,
                                deleteStatus = m.DeleteStatus.ToString(),
                                attachments = (m.Attachments ?? []).Select(a => new
                                {
                                    a.FileName,
                                    a.LocalName,
                                    a.Url,
                                    a.Kind,
                                    a.IsSpoiler,
                                }),
                                stickers = (m.Stickers ?? []).Select(s => new
                                {
                                    s.Name,
                                    s.Url,
                                    s.IsImage,
                                }),
                                forwarded = m.Forwarded is null
                                    ? null
                                    : new
                                    {
                                        m.Forwarded.Content,
                                        stickers = m.Forwarded.Stickers.Select(s => new
                                        {
                                            s.Name,
                                            s.Url,
                                            s.IsImage,
                                        }),
                                        attachments = m.Forwarded.Attachments.Select(a => new
                                        {
                                            a.FileName,
                                            a.LocalName,
                                            a.Url,
                                            a.Kind,
                                            a.IsSpoiler,
                                        }),
                                    },
                                willDelete = d?.WillDelete,
                                keptReason = d?.KeptReason,
                            };
                        }),
                    }
                );
            }
        );

        app.MapPut(
            "/api/exports/{runId}/messages/{messageId}/keep",
            async (
                string runId,
                string messageId,
                KeepRequest body,
                JobManager jobs,
                RunRegistry registry
            ) =>
            {
                // While a deletion for this run is live, a message may already be dispatched, and
                // Core's HTTP layer retries a 429'd DELETE without re-reading keep — so a protection
                // committed now could lose the race and the message be deleted anyway. Refuse
                // protection changes until the deletion finishes or is cancelled, which removes the
                // window entirely (protection set before the run is still honoured pre-dispatch).
                if (
                    jobs.Current
                        is { State: JobState.Running, Kind: JobKind.Deletion, RunId: var rid }
                    && rid == runId
                )
                {
                    return Results.Conflict(
                        new
                        {
                            error = "A deletion is running for this export. Cancel or let it finish before changing protection.",
                        }
                    );
                }

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
                    },
                    () => ReferenceEquals(session.Current, state)
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

                // Reject bad dates / references up front so a typo can never widen the deletion,
                // then resolve any pasted links to raw IDs server-side (R1/R2).
                var validationError = criteria.Validate();
                if (validationError is not null)
                    return Results.BadRequest(new { error = validationError });
                criteria = criteria.WithNormalizedReferences();

                var deletingUserId = state.User.Id.ToString();

                // Read channels, messages and revision from one consistent snapshot so the plan's
                // candidates and its stamped revision can never disagree (R3).
                var (revision, channels, messages) = manifest.ReadPlanningSnapshot();
                var plan = DeletionPlanner.BuildPlan(channels, messages, criteria, deletingUserId);

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

                // Samples live in the full message reader now: the UI opens it with this planId and
                // the messages endpoint tags every message with the same decision that will run, so
                // the preview itself only needs counts, the per-channel breakdown and the rule recap.
                var run = manifest.GetRun();
                var mismatch = run is not null && run.MeUserId != deletingUserId;
                var confirmText = mismatch
                    ? $"{run!.GuildName} / {state.User.Name}"
                    : run?.GuildName;

                return Results.Ok(
                    new
                    {
                        planId,
                        totalCandidates = plan.TotalCandidates,
                        totalKept = plan.TotalKept,
                        channels = plan.ChannelSummaries,
                        criteriaSummary = DescribeCriteria(criteria, state.User.Name),
                        deletingUser = Describe(state.User),
                        exportingUserId = run?.MeUserId,
                        accountMismatch = mismatch,
                        confirmText,
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
                // When the deleting account differs from the account that made the backup, the
                // typed confirmation must also name the deleting account, so the risk is explicit
                // (R4). Otherwise the server name alone confirms.
                var mismatch = run is not null && run.MeUserId != deletingUserId;
                var expectedConfirm = mismatch
                    ? $"{run!.GuildName} / {state.User.Name}"
                    : run?.GuildName ?? "";
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
                        new
                        {
                            error = mismatch
                                ? "Type the server name and your username exactly to confirm."
                                : "Type the server name exactly to confirm.",
                            expected = expectedConfirm,
                        }
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
                    },
                    () => ReferenceEquals(session.Current, state)
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

                using var subscription = job.Subscribe();
                try
                {
                    await foreach (var evt in subscription.Reader.ReadAllAsync(ctx.RequestAborted))
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(evt.Payload);
                        await ctx.Response.WriteAsync(
                            $"event: {evt.Type}\ndata: {json}\n\n",
                            ctx.RequestAborted
                        );
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected; the using-block unregisters the subscription.
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

    // The short, human label shown next to a kept sample message explaining why it survives. Only
    // the "could have been deleted but a rule/choice spared it" reasons get a label; structural
    // exclusions (wrong author, channel not exported, non-deletable kind) aren't shown as "kept".
    private static string? KeptReasonLabel(MessageSkipReason reason) =>
        reason switch
        {
            MessageSkipReason.ManuallyKept => "protected",
            MessageSkipReason.Pinned => "pinned",
            MessageSkipReason.HasAttachments => "has attachment",
            MessageSkipReason.ContainsKeepPhrase => "keep-word",
            MessageSkipReason.ExplicitlyExcluded => "excluded",
            MessageSkipReason.OutOfRange => "outside date range",
            _ => null,
        };

    // Plain-English recap of exactly the (normalized) criteria the server is about to act on, so the
    // confirm screen restates the rule set rather than making the user trust the form above.
    private static IReadOnlyList<string> DescribeCriteria(
        DeletionCriteria c,
        string deletingUserName
    )
    {
        var lines = new List<string>
        {
            c.Scope switch
            {
                DeletionScope.Mine => $"Only your own messages ({deletingUserName})",
                DeletionScope.Everyone => "Everyone's messages (needs Manage Messages)",
                DeletionScope.Authors => $"Messages by {c.AuthorIds.Count} selected author(s)",
                _ => "—",
            },
        };

        if (c.KeepPinned)
            lines.Add("Keeping pinned messages");
        if (c.KeepWithAttachments)
            lines.Add("Keeping messages that have attachments");
        if (c.KeepIfContains.Count > 0)
            lines.Add("Keeping messages containing: " + string.Join(", ", c.KeepIfContains));
        if (!string.IsNullOrWhiteSpace(c.OnlyAfter))
            lines.Add($"Only messages after {c.OnlyAfter}");
        if (!string.IsNullOrWhiteSpace(c.OnlyBefore))
            lines.Add($"Only messages before {c.OnlyBefore}");
        if (!string.IsNullOrWhiteSpace(c.OnlyContains))
            lines.Add($"Only messages containing \"{c.OnlyContains}\"");
        if (c.OnlyWithLink)
            lines.Add("Only messages that contain a link");
        if (c.OnlyWithAttachment)
            lines.Add("Only messages that have an attachment");
        if (c.ExcludedChannelIds.Count > 0)
            lines.Add($"Excluding {c.ExcludedChannelIds.Count} channel(s)");
        if (c.ExcludedMessageIds.Count > 0)
            lines.Add($"Excluding {c.ExcludedMessageIds.Count} specific message(s)");
        lines.Add(
            c.AllowIncompleteMedia
                ? "Including channels whose media backup is incomplete"
                : "Skipping channels with incomplete export or missing media"
        );
        return lines;
    }

    public record SignInRequest(string Token);

    public record StartExportRequest(string GuildId, ExportOptions? Options);

    public record KeepRequest(bool Keep);

    public record StartDeletionRequest(string PlanId, string? Confirm, bool DryRun);
}
