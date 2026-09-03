using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace HeroesRedemption.SafeTeleport;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.heroesredemption.safeteleport";
    public const string PluginName = "Heroes Redemption Safe Teleport";
    public const string PluginVersion = "1.0.0";

    internal const string ExpectedGameAssemblySha256 =
        "56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541";

    private SafeTeleportBehaviour? _behaviour;

    public override void Load()
    {
        ValidateGameBuild();

        var enabled = Config.Bind("General", "Enabled", true,
            "启用安全位置记录及瞬移热键。");
        var teleportKey = Config.Bind("Hotkeys", "TeleportKey", Key.F6,
            "瞬移至手动检查点；没有手动检查点时，瞬移至距离当前位置最近的可靠自动检查点。");
        var saveKey = Config.Bind("Hotkeys", "SaveKey", Key.F7,
            "把当前位置保存为本地图、本玩家实例的手动检查点。");
        var sampleInterval = Config.Bind("Safety", "SampleIntervalSeconds", 0.10f,
            "检查玩家位置的间隔（未缩放秒）。范围 0.02..1.00。");
        var reliabilityDelay = Config.Bind("Safety", "ReliabilityDelaySeconds", 1.50f,
            "玩家到达一个位置并继续存活多久后，才把它视为可靠位置。范围 0.25..10.00。");
        var spacing = Config.Bind("Safety", "CheckpointSpacing", 1.25f,
            "自动检查点之间的最小世界距离。范围 0.25..20.00。");
        var minimumTeleportDistance = Config.Bind("Safety", "MinimumTeleportDistance", 1.00f,
            "自动选择时跳过距离当前位置过近的检查点。范围 0.10..20.00。");
        var maximumCheckpoints = Config.Bind("Safety", "MaximumAutoCheckpoints", 96,
            "每个地图/玩家实例保存的自动检查点上限。范围 4..512。");
        var preferManual = Config.Bind("Safety", "PreferManualCheckpoint", true,
            "存在 F7 手动检查点时，F6 优先回到该位置；关闭后会在全部检查点中选择最近者。");
        var logAutoCaptures = Config.Bind("Diagnostics", "LogAutoCaptures", false,
            "是否记录每次自动检查点确认；默认关闭以避免刷日志。");

        SafeTeleportBehaviour.Configure(
            Log, enabled, teleportKey, saveKey, sampleInterval, reliabilityDelay,
            spacing, minimumTeleportDistance, maximumCheckpoints, preferManual,
            logAutoCaptures);
        ClassInjector.RegisterTypeInIl2Cpp<SafeTeleportBehaviour>();
        _behaviour = AddComponent<SafeTeleportBehaviour>();

        Log.LogInfo(
            $"SAFE_TELEPORT_READY teleportKey={teleportKey.Value} saveKey={saveKey.Value} " +
            $"delay={SafeTeleportBehaviour.ClampDelay(reliabilityDelay.Value):0.00} " +
            $"preferManual={preferManual.Value}");
    }

    public override bool Unload()
    {
        SafeTeleportBehaviour.Stop();
        if (_behaviour is not null)
            UnityEngine.Object.Destroy(_behaviour);
        _behaviour = null;
        Log.LogInfo("SAFE_TELEPORT_STOPPED");
        return true;
    }

    private static void ValidateGameBuild()
    {
        var path = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("GameAssembly.dll was not found.", path);

        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actual = Convert.ToHexString(sha256.ComputeHash(stream));
        if (!actual.Equals(ExpectedGameAssemblySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"GameAssembly SHA-256 mismatch. Expected {ExpectedGameAssemblySha256}; actual {actual}.");
    }
}

public sealed class SafeTeleportBehaviour : MonoBehaviour
{
    private static readonly SafeCheckpointTracker Tracker = new();
    private static ManualLogSource? _log;
    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<Key>? _teleportKey;
    private static ConfigEntry<Key>? _saveKey;
    private static ConfigEntry<float>? _sampleInterval;
    private static ConfigEntry<float>? _reliabilityDelay;
    private static ConfigEntry<float>? _spacing;
    private static ConfigEntry<float>? _minimumTeleportDistance;
    private static ConfigEntry<int>? _maximumCheckpoints;
    private static ConfigEntry<bool>? _preferManual;
    private static ConfigEntry<bool>? _logAutoCaptures;
    private static PlayerStats? _player;
    private static float _nextPlayerSearchAt;
    private static float _nextSampleAt;
    private static bool _running;

    public SafeTeleportBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    internal static float ClampDelay(float value) => Math.Clamp(value, 0.25f, 10f);

    internal static void Configure(
        ManualLogSource log,
        ConfigEntry<bool> enabled,
        ConfigEntry<Key> teleportKey,
        ConfigEntry<Key> saveKey,
        ConfigEntry<float> sampleInterval,
        ConfigEntry<float> reliabilityDelay,
        ConfigEntry<float> spacing,
        ConfigEntry<float> minimumTeleportDistance,
        ConfigEntry<int> maximumCheckpoints,
        ConfigEntry<bool> preferManual,
        ConfigEntry<bool> logAutoCaptures)
    {
        _log = log;
        _enabled = enabled;
        _teleportKey = teleportKey;
        _saveKey = saveKey;
        _sampleInterval = sampleInterval;
        _reliabilityDelay = reliabilityDelay;
        _spacing = spacing;
        _minimumTeleportDistance = minimumTeleportDistance;
        _maximumCheckpoints = maximumCheckpoints;
        _preferManual = preferManual;
        _logAutoCaptures = logAutoCaptures;
        _player = null;
        _nextPlayerSearchAt = 0f;
        _nextSampleAt = 0f;
        Tracker.Reset();
        _running = true;
    }

    internal static void Stop()
    {
        _running = false;
        _player = null;
        Tracker.Reset();
    }

    public void Update()
    {
        if (!_running || !(_enabled?.Value ?? true))
            return;

        var now = Time.unscaledTime;
        RefreshPlayer(now);
        var player = _player;
        if (player is null)
            return;

        var scene = SceneManager.GetActiveScene();
        var sceneKey = $"{scene.handle}:{scene.name}";
        var playerId = player.GetInstanceID();
        var position3 = player.transform.position;
        var position = new Point2(position3.x, position3.y);
        var recordable = IsRecordable(player);
        var recoverable = IsRecoverable(player);
        var settings = CurrentSettings();

        if (now >= _nextSampleAt)
        {
            _nextSampleAt = now + Math.Clamp(_sampleInterval?.Value ?? 0.10f, 0.02f, 1f);
            var result = Tracker.Observe(
                sceneKey, playerId, position, player.hp, now, recordable, settings);
            if (result.ScopeReset)
                _log?.LogInfo($"SAFE_TELEPORT_SCOPE_RESET scene={sceneKey} player={playerId}");
            if (result.Committed > 0 && (_logAutoCaptures?.Value ?? false))
                _log?.LogInfo(
                    $"SAFE_TELEPORT_AUTO_SAVED count={result.Committed} total={result.AutoCheckpointCount}");
        }

        var keyboard = Keyboard.current;
        if (keyboard is null)
            return;

        if (keyboard[_saveKey?.Value ?? Key.F7].wasPressedThisFrame)
        {
            if (Tracker.TrySaveManual(sceneKey, playerId, position, now, recordable, out var saved))
                _log?.LogInfo($"SAFE_TELEPORT_MANUAL_SAVED x={saved.Position.X:R} y={saved.Position.Y:R}");
            else
                _log?.LogWarning("SAFE_TELEPORT_SAVE_SKIPPED reason=player-not-playable");
        }

        if (!keyboard[_teleportKey?.Value ?? Key.F6].wasPressedThisFrame)
            return;

        if (!recoverable)
        {
            _log?.LogWarning("SAFE_TELEPORT_SKIPPED reason=player-not-playable");
            return;
        }

        SafeCheckpoint destination;
        string source;
        if (Tracker.TrySelectDestination(sceneKey, playerId, position, settings, out destination))
        {
            source = destination.IsManual ? "manual" : "auto";
        }
        else if (SpawnPointCatalog.TryGet(scene.name, out var spawn))
        {
            destination = new SafeCheckpoint(spawn, now, false);
            source = "scene-spawn";
        }
        else
        {
            _log?.LogWarning($"SAFE_TELEPORT_SKIPPED reason=no-reliable-checkpoint-or-spawn scene={scene.name}");
            return;
        }

        Teleport(player, position3.z, destination.Position);
        Tracker.NotifyTeleported(destination.Position, now, settings);
        _log?.LogInfo(
            $"SAFE_TELEPORT_APPLIED source={source} " +
            $"x={destination.Position.X:R} y={destination.Position.Y:R}");
    }

    private static void RefreshPlayer(float now)
    {
        // UnityEngine.Object can retain a managed wrapper after its native player was
        // destroyed. Use Unity's overloaded null comparison rather than pattern matching.
        if (_player != null)
            return;
        _player = null;
        if (now < _nextPlayerSearchAt)
            return;
        _nextPlayerSearchAt = now + 0.50f;
        _player = UnityEngine.Object.FindObjectOfType<PlayerStats>();
    }

    private static bool IsRecoverable(PlayerStats player)
    {
        if (!player.gameObject.activeInHierarchy || player.isDead || player.hp <= 0f)
            return false;
        return true;
    }

    private static bool IsRecordable(PlayerStats player)
    {
        if (!IsRecoverable(player))
            return false;
        if (PlayerControls.isPaused || Time.timeScale <= 0.001f)
            return false;
        return true;
    }

    private static TrackerSettings CurrentSettings() => new(
        _reliabilityDelay?.Value ?? 1.50f,
        _spacing?.Value ?? 1.25f,
        _minimumTeleportDistance?.Value ?? 1.00f,
        _maximumCheckpoints?.Value ?? 96,
        _preferManual?.Value ?? true);

    private static void Teleport(PlayerStats player, float z, Point2 destination)
    {
        var target3 = new Vector3(destination.X, destination.Y, z);
        var rigidbody = player.GetComponent<Rigidbody2D>();
        if (rigidbody is not null)
        {
            rigidbody.velocity = Vector2.zero;
            rigidbody.angularVelocity = 0f;
            rigidbody.position = new Vector2(destination.X, destination.Y);
        }
        player.transform.position = target3;
        Physics2D.SyncTransforms();
    }
}
