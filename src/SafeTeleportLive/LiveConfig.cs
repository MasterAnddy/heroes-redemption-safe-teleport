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
                    ?? throw new InvalidOperationException("配置为空。");
        value.Validate();
        return value;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProcessName) || string.IsNullOrWhiteSpace(ModuleName))
            throw new InvalidOperationException("进程名和模块名必须填写。");
        if (ExpectedModuleSha256.Replace(" ", "").Length != 64)
            throw new InvalidOperationException("模块 SHA-256 必须是 64 个十六进制字符。");
        _ = UpdateRva;
        _ = GetPositionRva;
        _ = SetPositionRva;
        _ = SetVelocityRva;
        _ = GetActiveSceneRva;
        _ = GetSceneNameRva;
        var prefix = ExpectedPrefixBytes;
        if (prefix.Length != HookImageBuilder.PatchLength)
            throw new InvalidOperationException($"PlayerStats.Update 前缀必须恰好覆盖 {HookImageBuilder.PatchLength} 字节。");
        _ = NativeMethods.ParseVirtualKey(SaveAnchorHotkey);
        _ = NativeMethods.ParseVirtualKey(TeleportHotkey);
        _ = NativeMethods.ParseVirtualKey(ClearAnchorHotkey);
        _ = NativeMethods.ParseVirtualKey(ExitHotkey);
        if (new[] { SaveAnchorHotkey, TeleportHotkey, ClearAnchorHotkey, ExitHotkey }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new InvalidOperationException("四个热键必须互不相同。");
        if (!float.IsFinite(EmergencyStep) || EmergencyStep is < 0.5f or > 50f)
            throw new InvalidOperationException("EmergencyStep 必须在 0.5..50 之间。");
        if (!float.IsFinite(MaximumCoordinateMagnitude) || MaximumCoordinateMagnitude is < 100f or > 10_000_000f)
            throw new InvalidOperationException("MaximumCoordinateMagnitude 超出范围。");
        if (SafeSceneSpawns is null || SafeSceneSpawns.Count == 0)
            throw new InvalidOperationException("必须提供至少一个已验证场景出生点。");
        foreach (var (scene, spawn) in SafeSceneSpawns)
            if (string.IsNullOrWhiteSpace(scene) || !spawn.ToPosition().IsPlausible(MaximumCoordinateMagnitude))
                throw new InvalidOperationException($"场景出生点异常：{scene}。");
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
