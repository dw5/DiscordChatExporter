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

    // Subscribe for SSE. Returns a reader that first replays history, then streams live events.
    public ChannelReader<JobEvent> Subscribe()
    {
        var channel = Channel.CreateUnbounded<JobEvent>();
        lock (_lock)
        {
            foreach (var past in _history)
                channel.Writer.TryWrite(past);

            if (State != JobState.Running)
                channel.Writer.TryComplete();
            else
                _subscribers.Add(channel);
        }
        return channel.Reader;
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

// Runs at most one job at a time across the whole app.
public sealed class JobManager
{
    private readonly object _lock = new();
    private Job? _current;
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

    // Starts a job unless one is already running. Returns null if busy.
    public Job? TryStart(JobKind kind, string? runId, Func<Job, CancellationToken, Task> work)
    {
        Job job;
        lock (_lock)
        {
            if (_current is { State: JobState.Running })
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
