namespace HeroesRedemption.SafeTeleportLive;

internal readonly record struct Position2(float X, float Y)
{
    internal bool IsPlausible(float maximumMagnitude) =>
        float.IsFinite(X) && float.IsFinite(Y) &&
        MathF.Abs(X) <= maximumMagnitude && MathF.Abs(Y) <= maximumMagnitude;

    internal ulong Packed => (uint)BitConverter.SingleToInt32Bits(X) |
                             ((ulong)(uint)BitConverter.SingleToInt32Bits(Y) << 32);

    internal static Position2 FromPacked(ulong value) => new(
        BitConverter.Int32BitsToSingle(unchecked((int)(uint)value)),
        BitConverter.Int32BitsToSingle(unchecked((int)(uint)(value >> 32))));

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

internal sealed class AnchorPolicy
{
    private readonly float _step;
    private long _player;
    private int _sceneHandle;
    private Position2? _manualAnchor;
    private Position2? _emergencyOrigin;
    private int _emergencyIndex;

    internal AnchorPolicy(float step) => _step = step;

    internal void ObserveScope(long player, int sceneHandle)
    {
        if (player == 0 || player == _player && sceneHandle == _sceneHandle) return;
        _player = player;
        _sceneHandle = sceneHandle;
        _manualAnchor = null;
        _emergencyOrigin = null;
        _emergencyIndex = 0;
    }

    internal Position2 Save(Position2 current, long player, int sceneHandle)
    {
        ObserveScope(player, sceneHandle);
        _manualAnchor = current;
        _emergencyOrigin = current;
        _emergencyIndex = 0;
        return current;
    }

    internal (Position2 Target, string Source) ChooseTarget(
        Position2 current,
        long player,
        int sceneHandle,
        Position2? verifiedSceneSpawn = null,
        string? sceneName = null)
    {
        ObserveScope(player, sceneHandle);
        if (_manualAnchor is { } anchor)
            return (anchor, "manual-anchor");

        if (verifiedSceneSpawn is { } spawn)
            return (spawn, $"verified-scene-spawn:{sceneName ?? "unknown"}");

        _emergencyOrigin ??= current;
        // A bounded expanding cardinal search is used only when F7 has not yet
        // established a known-good anchor. Repeated F6 presses try distinct nearby
        // locations instead of accumulating unbounded coordinate drift.
        var ring = _emergencyIndex / 4 + 1;
        var distance = _step * ring;
        var direction = _emergencyIndex++ % 4;
        var origin = _emergencyOrigin.Value;
        return direction switch
        {
            0 => (new Position2(origin.X, origin.Y + distance), "emergency-up"),
            1 => (new Position2(origin.X + distance, origin.Y), "emergency-right"),
            2 => (new Position2(origin.X, origin.Y - distance), "emergency-down"),
            _ => (new Position2(origin.X - distance, origin.Y), "emergency-left")
        };
    }

    internal void Clear()
    {
        _manualAnchor = null;
        _emergencyOrigin = null;
        _emergencyIndex = 0;
    }

    internal bool HasManualAnchor => _manualAnchor.HasValue;
}
