using AutoTestAi.Application.Common;

namespace AutoTestAi.Application.Authorization;

/// <summary>
/// In-memory collapse window for authorization-denial audit events
/// (Phase 4 Slice 11). Repeated denials for the same (project, actor,
/// reason) within the window produce a single persisted row, so hostile or
/// buggy replay cannot grow <c>audit_events</c> without bound.
/// Strictly bounded: at most <see cref="MaxEntries"/> keys are retained;
/// under pressure expired keys are evicted first and new keys are then
/// suppressed (the authorization outcome itself is never affected).
/// Process-local by design: replicas deduplicate independently.
/// </summary>
public sealed class AuthorizationDenialDedup
{
    /// <summary>Collapse window for repeats (engineering constant, not policy).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    /// <summary>Hard memory bound on retained keys.</summary>
    public const int DefaultMaxEntries = 4096;

    private readonly TimeSpan _window;
    private readonly int _maxEntries;
    private readonly IDateTimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ProjectId, string? ActorKey, string Reason), DateTimeOffset> _emitted = new();

    public AuthorizationDenialDedup(IDateTimeProvider clock)
        : this(clock, DefaultWindow, DefaultMaxEntries)
    {
    }

    public AuthorizationDenialDedup(IDateTimeProvider clock, TimeSpan window, int maxEntries)
    {
        _clock = clock;
        _window = window;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>
    /// Returns true when the denial should be recorded (first sighting or
    /// window elapsed). Thread-safe; never throws for bookkeeping reasons.
    /// Suppression only skips the audit row — never the denial itself.
    /// </summary>
    public bool ShouldRecord(Guid projectId, string? actorKey, string reason)
    {
        var key = (projectId, actorKey, reason);
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_emitted.TryGetValue(key, out var last) && now - last < _window)
                return false;
            if (!_emitted.ContainsKey(key) && _emitted.Count >= _maxEntries)
            {
                EvictExpiredLocked(now);
                if (_emitted.Count >= _maxEntries)
                    return false;
            }
            _emitted[key] = now;
            return true;
        }
    }

    private void EvictExpiredLocked(DateTimeOffset now)
    {
        // Bounded: the dictionary never exceeds _maxEntries keys.
        foreach (var entry in _emitted.ToArray())
        {
            if (now - entry.Value >= _window)
                _emitted.Remove(entry.Key);
        }
    }
}
