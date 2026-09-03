namespace HeroesRedemption.SafeTeleport;

public readonly record struct Point2(float X, float Y)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y);

    public float DistanceSquared(Point2 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}

public readonly record struct TrackerSettings(
    float ReliabilityDelaySeconds,
    float CheckpointSpacing,
    float MinimumTeleportDistance,
    int MaximumAutoCheckpoints,
    bool PreferManualCheckpoint)
{
    public TrackerSettings Normalized() => new(
        Math.Clamp(ReliabilityDelaySeconds, 0.25f, 10f),
        Math.Clamp(CheckpointSpacing, 0.25f, 20f),
        Math.Clamp(MinimumTeleportDistance, 0.10f, 20f),
        Math.Clamp(MaximumAutoCheckpoints, 4, 512),
        PreferManualCheckpoint);
}

public readonly record struct SafeCheckpoint(
    Point2 Position,
    float CapturedAt,
    bool IsManual);

public readonly record struct ObservationResult(
    bool ScopeReset,
    int Committed,
    int AutoCheckpointCount,
    bool SampleAccepted);

/// <summary>Validated serialized player start positions for the shipped scenes.</summary>
public static class SpawnPointCatalog
{
    private static readonly Dictionary<string, Point2> Points =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Hub"] = new Point2(43.4375f, -60.71875f),
            ["Cave"] = new Point2(-97f, -13f),
            ["Cemetery"] = new Point2(-123.5f, -1.125f),
            ["Winterland"] = new Point2(10.975439f, -16.765644f),
            ["Sewer"] = new Point2(-118.46875f, -91.40625f),
            ["OldPrison"] = new Point2(-134.8125f, -105.625f),
            ["Catacomb"] = new Point2(-174.968811f, -7.281252f)
        };

    public static bool TryGet(string sceneName, out Point2 point) =>
        Points.TryGetValue(sceneName, out point);
}

/// <summary>
/// Keeps a bounded history of positions which an alive, unpaused player reached and
/// then survived for a configurable delay. The policy is Unity-independent so its
/// state transitions can be tested without launching the game.
/// </summary>
public sealed class SafeCheckpointTracker
{
    private readonly List<SafeCheckpoint> _auto = new();
    private readonly Queue<PendingSample> _pending = new();
    private string? _sceneKey;
    private int _playerInstanceId;
    private Point2? _lastQueued;
    private float? _lastHp;
    private float _lastDamageAt = float.NegativeInfinity;
    private float _recordingBlockedUntil;
    private SafeCheckpoint? _manual;

    public int AutoCheckpointCount => _auto.Count;
    public SafeCheckpoint? ManualCheckpoint => _manual;
    public string? SceneKey => _sceneKey;
    public int PlayerInstanceId => _playerInstanceId;

    public ObservationResult Observe(
        string sceneKey,
        int playerInstanceId,
        Point2 position,
        float hp,
        float now,
        bool playable,
        TrackerSettings settings)
    {
        settings = settings.Normalized();
        var reset = EnsureScope(sceneKey, playerInstanceId);

        if (!position.IsFinite || !float.IsFinite(hp) || !float.IsFinite(now))
        {
            InvalidatePending();
            return new ObservationResult(reset, 0, _auto.Count, false);
        }

        if (_lastHp.HasValue && hp < _lastHp.Value - 0.001f)
        {
            _lastDamageAt = now;
            InvalidatePending();
        }
        _lastHp = hp;

        if (!playable || hp <= 0f || now < _recordingBlockedUntil)
        {
            InvalidatePending();
            return new ObservationResult(reset, 0, _auto.Count, false);
        }

        var spacingSq = settings.CheckpointSpacing * settings.CheckpointSpacing;
        if (!_lastQueued.HasValue || _lastQueued.Value.DistanceSquared(position) >= spacingSq)
        {
            _pending.Enqueue(new PendingSample(position, now));
            _lastQueued = position;
        }

        var committed = 0;
        while (_pending.Count > 0)
        {
            var candidate = _pending.Peek();
            if (now - candidate.ObservedAt < settings.ReliabilityDelaySeconds)
                break;

            _pending.Dequeue();
            if (_lastDamageAt > candidate.ObservedAt)
                continue;
            if (_auto.Count > 0 &&
                _auto[^1].Position.DistanceSquared(candidate.Position) < spacingSq)
                continue;

            _auto.Add(new SafeCheckpoint(candidate.Position, now, false));
            committed++;
            while (_auto.Count > settings.MaximumAutoCheckpoints)
                _auto.RemoveAt(0);
        }

        return new ObservationResult(reset, committed, _auto.Count, true);
    }

    public bool TrySaveManual(
        string sceneKey,
        int playerInstanceId,
        Point2 position,
        float now,
        bool playable,
        out SafeCheckpoint checkpoint)
    {
        EnsureScope(sceneKey, playerInstanceId);
        if (!playable || !position.IsFinite || !float.IsFinite(now))
        {
            checkpoint = default;
            return false;
        }

        checkpoint = new SafeCheckpoint(position, now, true);
        _manual = checkpoint;
        return true;
    }

    public bool TrySelectDestination(
        string sceneKey,
        int playerInstanceId,
        Point2 current,
        TrackerSettings settings,
        out SafeCheckpoint checkpoint)
    {
        settings = settings.Normalized();
        EnsureScope(sceneKey, playerInstanceId);
        if (!current.IsFinite)
        {
            checkpoint = default;
            return false;
        }

        if (settings.PreferManualCheckpoint && _manual.HasValue)
        {
            checkpoint = _manual.Value;
            return true;
        }

        var minimumSq = settings.MinimumTeleportDistance * settings.MinimumTeleportDistance;
        SafeCheckpoint? nearest = null;
        var nearestSq = float.PositiveInfinity;

        if (_manual.HasValue)
            Consider(_manual.Value);
        foreach (var candidate in _auto)
            Consider(candidate);

        if (!nearest.HasValue)
        {
            checkpoint = default;
            return false;
        }

        checkpoint = nearest.Value;
        return true;

        void Consider(SafeCheckpoint candidate)
        {
            var distanceSq = current.DistanceSquared(candidate.Position);
            if (distanceSq < minimumSq || distanceSq >= nearestSq)
                return;
            nearest = candidate;
            nearestSq = distanceSq;
        }
    }

    public void NotifyTeleported(Point2 destination, float now, TrackerSettings settings)
    {
        settings = settings.Normalized();
        _pending.Clear();
        _lastQueued = destination;
        _recordingBlockedUntil = now + settings.ReliabilityDelaySeconds;
    }

    public bool EnsureScope(string sceneKey, int playerInstanceId)
    {
        if (string.Equals(_sceneKey, sceneKey, StringComparison.Ordinal) &&
            _playerInstanceId == playerInstanceId)
            return false;

        _sceneKey = sceneKey;
        _playerInstanceId = playerInstanceId;
        _auto.Clear();
        _pending.Clear();
        _manual = null;
        _lastQueued = null;
        _lastHp = null;
        _lastDamageAt = float.NegativeInfinity;
        _recordingBlockedUntil = 0f;
        return true;
    }

    public void Reset()
    {
        _sceneKey = null;
        _playerInstanceId = 0;
        _auto.Clear();
        _pending.Clear();
        _manual = null;
        _lastQueued = null;
        _lastHp = null;
        _lastDamageAt = float.NegativeInfinity;
        _recordingBlockedUntil = 0f;
    }

    private void InvalidatePending()
    {
        _pending.Clear();
        _lastQueued = null;
    }

    private readonly record struct PendingSample(Point2 Position, float ObservedAt);
}
