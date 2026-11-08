using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DiscordChatExporter.Web.Services;

public enum JobKind
{
    Export,
    MediaRetry,
    Deletion,
}

public enum JobState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

// A single running job's live view. Progress, a rolling log, and an event feed for SSE.
public sealed class Job
{
    private readonly List<Channel<JobEvent>> _subscribers = [];
    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private readonly List<JobEvent> _history = [];

    internal Job(string id, JobKind kind, string? runId)
    {
        Id = id;
        Kind = kind;
        RunId = runId;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public JobKind Kind { get; }
    public string? RunId { get; }
    public DateTimeOffset StartedAt { get; }
    public JobState State { get; private set; } = JobState.Running;
    public string? Error { get; private set; }
    public double Progress { get; private set; }
    public string? StatusText { get; private set; }

    internal CancellationTokenSource Cancellation { get; } = new();

    public void Cancel() => Cancellation.Cancel();

    public void ReportProgress(double fraction, string? statusText = null)
    {
        Progress = Math.Clamp(fraction, 0, 1);
        if (statusText is not null)
            StatusText = statusText;
        Emit(new JobEvent("progress", new { progress = Progress, status = StatusText }));
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            _log.Add(message);
            if (_log.Count > 500)
                _log.RemoveAt(0);
        }
        Emit(new JobEvent("log", new { message }));
    }

    public void EmitData(string type, object payload) => Emit(new JobEvent(type, payload));

    internal void Finish(JobState state, string? error)
    {
        State = state;
        Error = error;
        Emit(new JobEvent("done", new { state = state.ToString(), error }));
        lock (_lock)
        {
            foreach (var sub in _subscribers)
                sub.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
            return _log.ToArray();
    }

    // Subscribe for SSE. Returns a disposable that first replays history, then streams live events;
    // disposing it (when the client disconnects) unregisters the subscription so it can't leak and
    // keep receiving events for the rest of the job (R6). The queue is bounded and drops its oldest
    // events for a slow consumer rather than growing without limit.
    public JobSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<JobEvent>(
            new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            }
        );
        lock (_lock)
        {
            foreach (var past in _history)
                channel.Writer.TryWrite(past);

            if (State != JobState.Running)
                channel.Writer.TryComplete();
            else
                _subscribers.Add(channel);
        }
        return new JobSubscription(
            channel.Reader,
            () =>
            {
                lock (_lock)
                    _subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        );
    }

    private void Emit(JobEvent evt)
    {
        lock (_lock)
        {
            _history.Add(evt);
            if (_history.Count > 1000)
                _history.RemoveAt(0);
            foreach (var sub in _subscribers)
                sub.Writer.TryWrite(evt);
        }
    }
}

public record JobEvent(string Type, object Payload);

// A live SSE subscription. Dispose unregisters it from the job so a disconnected client's queue
// stops accumulating events.
public sealed class JobSubscription(ChannelReader<JobEvent> reader, Action onDispose) : IDisposable
{
    private int _disposed;

    public ChannelReader<JobEvent> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            onDispose();
    }
}

// Runs at most one job at a time across the whole app.
public sealed class JobManager
{
    private readonly object _lock = new();
    private Job? _current;

    // True while a session sign-in/out is in progress. Held across the (async) sign-in so a job
    // cannot start mid-change and a change cannot start mid-job: together with the running-job
    // check this keeps every job bound to one account for its whole lifetime.
    private bool _sessionChanging;

    // Count of in-flight protection (keep) mutations per run. Held across the async SetKeep/flush so
    // that a deletion cannot be admitted while a protection for the same run is committing, and a
    // protection cannot begin once that run's deletion is running. This makes "protected is never
    // deleted" hold even against Core's 429 DELETE-retry, which never re-reads keep.
    private readonly Dictionary<string, int> _pendingKeepByRun = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public Job? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public bool IsBusy => Current is { State: JobState.Running };

    public Job? Get(string id) => _jobs.GetValueOrDefault(id);

    // Reserves the right to change the session. Fails if a job is running or another change is in
    // flight. The caller must pair a true result with EndSessionChange() in a finally.
    public bool TryBeginSessionChange()
    {
        lock (_lock)
        {
            if (_sessionChanging || _current is { State: JobState.Running })
                return false;
            _sessionChanging = true;
            return true;
        }
    }

    public void EndSessionChange()
    {
        lock (_lock)
            _sessionChanging = false;
    }

    // Reserves the right to change protection on a run. Fails if that run's deletion is running, so a
    // protection can never commit after its message may have been dispatched. The caller pairs a true
    // result with EndKeepChange() in a finally. While the reservation is held, TryStart refuses to
    // admit a deletion for the same run, so the two can never interleave.
    public bool TryBeginKeepChange(string runId)
    {
        lock (_lock)
        {
            if (
                _current is { State: JobState.Running, Kind: JobKind.Deletion } d
                && d.RunId == runId
            )
                return false;
            _pendingKeepByRun[runId] = _pendingKeepByRun.GetValueOrDefault(runId) + 1;
            return true;
        }
    }

    public void EndKeepChange(string runId)
    {
        lock (_lock)
        {
            var n = _pendingKeepByRun.GetValueOrDefault(runId) - 1;
            if (n <= 0)
                _pendingKeepByRun.Remove(runId);
            else
                _pendingKeepByRun[runId] = n;
        }
    }

    // Starts a job unless one is already running or the session is being changed. `canStart`, when
    // given, is evaluated under the admission lock so the caller can confirm the account it captured
    // is still the current one — closing the window between reading the session and admitting the
    // job (R3). Returns null if busy, changing, or the guard fails.
    public Job? TryStart(
        JobKind kind,
        string? runId,
        Func<Job, CancellationToken, Task> work,
        Func<bool>? canStart = null
    )
    {
        Job job;
        lock (_lock)
        {
            if (_sessionChanging || _current is { State: JobState.Running })
                return null;
            // A deletion must not be admitted while a protection for the same run is still
            // committing, or it could dispatch a DELETE for a message that is about to be protected
            // (its keep write hasn't landed yet). The caller's canStart runs below, still under this
            // lock, so a deletion caller re-validates the plan revision here — atomically with this
            // pending-keep check — and once admitted no keep change for the run can have slipped in.
            if (
                kind == JobKind.Deletion
                && runId is not null
                && _pendingKeepByRun.GetValueOrDefault(runId) > 0
            )
                return null;
            if (canStart is not null && !canStart())
                return null;

            job = new Job(Guid.NewGuid().ToString("N"), kind, runId);
            _current = job;
        }

        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                await work(job, job.Cancellation.Token);
                job.Finish(JobState.Completed, null);
            }
            catch (OperationCanceledException)
            {
                job.Finish(JobState.Cancelled, null);
            }
            catch (Exception ex)
            {
                job.Log("Error: " + ex.Message);
                job.Finish(JobState.Failed, ex.Message);
            }
        });

        return job;
    }
}
