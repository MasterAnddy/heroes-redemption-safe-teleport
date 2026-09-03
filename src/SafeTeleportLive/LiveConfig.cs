using System.Globalization;
using System.Text.Json;

namespace HeroesRedemption.SafeTeleportLive;

internal sealed record LiveConfig(
    string ProcessName,
    string ModuleName,
    string ExpectedModuleSha256,
    string PlayerStatsUpdateRva,
    string ExpectedUpdatePrefix,
    string RigidbodyGetPositionRva,
    string RigidbodySetPositionRva,
    string RigidbodySetVelocityRva,
    string SceneManagerGetActiveSceneRva,
    string SceneGetNameInternalRva,
    string SaveAnchorHotkey,
    string TeleportHotkey,
    string ClearAnchorHotkey,
    string ExitHotkey,
    Dictionary<string, SpawnPosition> SafeSceneSpawns,
    float EmergencyStep = 4f,
    float MaximumCoordinateMagnitude = 1_000_000f)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static LiveConfig Load(string path)
    {
        var value = JsonSerializer.Deserialize<LiveConfig>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidOperationException("The configuration is empty.");
        value.Validate();
        return value;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProcessName) || string.IsNullOrWhiteSpace(ModuleName))
            throw new InvalidOperationException("ProcessName and ModuleName are required.");
        if (ExpectedModuleSha256.Replace(" ", "").Length != 64)
            throw new InvalidOperationException("ExpectedModuleSha256 must contain exactly 64 hexadecimal characters.");
        _ = UpdateRva;
        _ = GetPositionRva;
        _ = SetPositionRva;
        _ = SetVelocityRva;
        _ = GetActiveSceneRva;
        _ = GetSceneNameRva;
        var prefix = ExpectedPrefixBytes;
        if (prefix.Length != HookImageBuilder.PatchLength)
            throw new InvalidOperationException($"ExpectedUpdatePrefix must cover exactly {HookImageBuilder.PatchLength} bytes of PlayerStats.Update.");
        _ = NativeMethods.ParseVirtualKey(SaveAnchorHotkey);
        _ = NativeMethods.ParseVirtualKey(TeleportHotkey);
        _ = NativeMethods.ParseVirtualKey(ClearAnchorHotkey);
        _ = NativeMethods.ParseVirtualKey(ExitHotkey);
        if (new[] { SaveAnchorHotkey, TeleportHotkey, ClearAnchorHotkey, ExitHotkey }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new InvalidOperationException("The four hotkeys must be unique.");
        if (!float.IsFinite(EmergencyStep) || EmergencyStep is < 0.5f or > 50f)
            throw new InvalidOperationException("EmergencyStep must be between 0.5 and 50.");
        if (!float.IsFinite(MaximumCoordinateMagnitude) || MaximumCoordinateMagnitude is < 100f or > 10_000_000f)
            throw new InvalidOperationException("MaximumCoordinateMagnitude is outside the supported range.");
        if (SafeSceneSpawns is null || SafeSceneSpawns.Count == 0)
            throw new InvalidOperationException("At least one verified scene spawn point is required.");
        foreach (var (scene, spawn) in SafeSceneSpawns)
            if (string.IsNullOrWhiteSpace(scene) || !spawn.ToPosition().IsPlausible(MaximumCoordinateMagnitude))
                throw new InvalidOperationException($"The spawn point for scene '{scene}' is invalid.");
    }

    internal long UpdateRva => ParseHex(PlayerStatsUpdateRva);
    internal long GetPositionRva => ParseHex(RigidbodyGetPositionRva);
    internal long SetPositionRva => ParseHex(RigidbodySetPositionRva);
    internal long SetVelocityRva => ParseHex(RigidbodySetVelocityRva);
    internal long GetActiveSceneRva => ParseHex(SceneManagerGetActiveSceneRva);
    internal long GetSceneNameRva => ParseHex(SceneGetNameInternalRva);
    internal byte[] ExpectedPrefixBytes => ParseBytes(ExpectedUpdatePrefix);

    private static long ParseHex(string text) => checked((long)ulong.Parse(
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture));

    private static byte[] ParseBytes(string text) => text
        .Split([' ', '\t', '\r', '\n', '-'], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => byte.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
        .ToArray();
}

internal sealed record SpawnPosition(float X, float Y)
{
    internal Position2 ToPosition() => new(X, Y);
}
