using System;

namespace DiscordChatExporter.Web.Services.Deletion;

// Adaptive delay between individual delete calls. User tokens start from a positive base to avoid
// self-bot rate limiting; bot tokens start at zero and rely on the rate-limit headers, but still
// back off if Discord actually slows us down.
public sealed class DeletionPacer
{
    private readonly TimeSpan _base;
    private readonly TimeSpan _max;
    private readonly TimeSpan _floorOnBackoff;
    private readonly int _decayAfterCleanCalls;
    private TimeSpan _current;
    private int _cleanStreak;

    public DeletionPacer(
        TimeSpan baseDelay,
        TimeSpan? max = null,
        TimeSpan? floorOnBackoff = null,
        int decayAfterCleanCalls = 50
    )
    {
        _base = baseDelay;
        _max = max ?? TimeSpan.FromSeconds(15);
        _floorOnBackoff = floorOnBackoff ?? TimeSpan.FromMilliseconds(250);
        _decayAfterCleanCalls = decayAfterCleanCalls;
        _current = baseDelay;
    }

    public static DeletionPacer ForUserToken() => new(TimeSpan.FromMilliseconds(1000));

    public static DeletionPacer ForBotToken() => new(TimeSpan.Zero);

    public TimeSpan CurrentDelay => _current;

    // Returns the delay to wait before the next call, given how long the last call took.
    // A call that took noticeably longer than the base delay means we waited out an advisory or
    // hard rate limit, so we slow down. Repeated clean calls let us speed back up toward the base.
    public TimeSpan NextDelay(TimeSpan lastCallDuration)
    {
        var wasThrottled = lastCallDuration > _base + TimeSpan.FromSeconds(2);

        if (wasThrottled)
        {
            _cleanStreak = 0;

            // max(current * 1.5, floor) so that a zero baseline (bots) still starts backing off
            var scaled = TimeSpan.FromTicks((long)(_current.Ticks * 1.5));
            _current = Max(scaled, _floorOnBackoff);
            _current = Min(_current, _max);
        }
        else
        {
            _cleanStreak++;
            if (_cleanStreak >= _decayAfterCleanCalls && _current > _base)
            {
                _cleanStreak = 0;
                var decayed = TimeSpan.FromTicks((long)(_current.Ticks * 0.9));
                _current = Max(decayed, _base);
            }
        }

        // Jitter so repeated runs don't line up on the same cadence
        var jitterMs = _current > TimeSpan.Zero ? Random.Shared.Next(0, 250) : 0;
        return _current + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
