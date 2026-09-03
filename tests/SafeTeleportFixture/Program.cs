using HeroesRedemption.SafeTeleport;

var baseline = args.Contains("--baseline", StringComparer.OrdinalIgnoreCase);
var checks = 0;

void Check(bool condition, string message)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException($"CHECK_FAILED[{checks}]={message}");
}

var settings = new TrackerSettings(
    ReliabilityDelaySeconds: 1.5f,
    CheckpointSpacing: 1.25f,
    MinimumTeleportDistance: 1f,
    MaximumAutoCheckpoints: 4,
    PreferManualCheckpoint: true);

if (baseline)
{
    var tracker = new SafeCheckpointTracker();
    var found = tracker.TrySelectDestination("map-a", 101, new Point2(8, 8), settings, out _);
    Check(!found, "baseline must have no recovery destination");
    Check(tracker.AutoCheckpointCount == 0, "baseline must not record checkpoints");
    Console.WriteLine("MODE=baseline");
    Console.WriteLine("INPUT=enabled=false scene=map-a player=101 current=(8,8) hotkey=F6");
    Console.WriteLine("OUTPUT=teleported=false destination=none autoCheckpoints=0");
    Console.WriteLine($"CHECKS={checks}");
    Console.WriteLine("STATUS=PASS");
    Console.WriteLine("EXIT_STATUS=0");
    return;
}

var t = new SafeCheckpointTracker();

Check(SpawnPointCatalog.TryGet("OldPrison", out var oldPrisonSpawn),
    "known scene must have a fallback spawn");
Check(oldPrisonSpawn == new Point2(-134.8125f, -105.625f),
    "OldPrison fallback must match validated serialized player position");
Check(!SpawnPointCatalog.TryGet("unknown-scene", out _),
    "unknown scene must not invent a fallback");

// Initial and moving positions must survive the delay before they become safe.
var first = t.Observe("map-a", 101, new Point2(0, 0), 100, 0f, true, settings);
Check(first.ScopeReset, "first scope should reset");
Check(first.Committed == 0, "initial sample must not commit immediately");
t.Observe("map-a", 101, new Point2(2, 0), 100, 0.5f, true, settings);
var early = t.Observe("map-a", 101, new Point2(2, 0), 100, 1.49f, true, settings);
Check(early.Committed == 0, "sample must wait full reliability delay");
var mature = t.Observe("map-a", 101, new Point2(3.5f, 0), 100, 1.5f, true, settings);
Check(mature.Committed == 1, "initial traversed point should mature");
Check(t.TrySelectDestination("map-a", 101, new Point2(3.5f, 0), settings, out var nearest),
    "recovery destination should exist");
Check(nearest.Position == new Point2(0, 0) && !nearest.IsManual,
    "nearest reliable automatic destination should be initial point");

// Damage, pause/selection, and death invalidate pending samples.
t.Observe("map-a", 101, new Point2(5, 0), 90, 2f, true, settings);
t.Observe("map-a", 101, new Point2(6.5f, 0), 90, 4f, false, settings);
var afterPause = t.Observe("map-a", 101, new Point2(8, 0), 90, 6f, true, settings);
Check(afterPause.Committed == 0, "paused/dead interval must not mature a sample");
Check(!t.TrySaveManual("map-a", 101, new Point2(8, 0), 6f, false, out _),
    "manual save must reject unplayable state");

var damageGuard = new SafeCheckpointTracker();
damageGuard.Observe("map-damage", 301, new Point2(0, 0), 100, 0f, true, settings);
damageGuard.Observe("map-damage", 301, new Point2(2, 0), 100, 0.5f, true, settings);
damageGuard.Observe("map-damage", 301, new Point2(4, 0), 90, 1f, true, settings);
var beforePostDamageMatures = damageGuard.Observe(
    "map-damage", 301, new Point2(4, 0), 90, 2.4f, true, settings);
Check(beforePostDamageMatures.Committed == 0 && damageGuard.AutoCheckpointCount == 0,
    "damage must discard all pre-hit pending positions");

var pauseGuard = new SafeCheckpointTracker();
pauseGuard.Observe("map-pause", 302, new Point2(0, 0), 100, 0f, true, settings);
pauseGuard.Observe("map-pause", 302, new Point2(2, 0), 100, 2f, false, settings);
Check(pauseGuard.AutoCheckpointCount == 0,
    "pause/selection must discard pending positions instead of confirming them");

var deathGuard = new SafeCheckpointTracker();
deathGuard.Observe("map-death", 303, new Point2(0, 0), 0, 0f, false, settings);
deathGuard.Observe("map-death", 303, new Point2(2, 0), 0, 3f, false, settings);
Check(deathGuard.AutoCheckpointCount == 0,
    "death state must never confirm a position");

// A deliberate manual checkpoint is preferred by default.
Check(t.TrySaveManual("map-a", 101, new Point2(7, 3), 6.1f, true, out var manual),
    "manual save should succeed during live play");
Check(manual.IsManual, "manual marker should be explicit");
Check(t.TrySelectDestination("map-a", 101, new Point2(50, 50), settings, out var selectedManual),
    "manual recovery destination should exist");
Check(selectedManual.IsManual && selectedManual.Position == new Point2(7, 3),
    "manual checkpoint should be preferred");

// Teleport blocks immediate re-recording, then the new location can mature normally.
t.NotifyTeleported(manual.Position, 6.1f, settings);
var blocked = t.Observe("map-a", 101, manual.Position, 90, 6.2f, true, settings);
Check(!blocked.SampleAccepted, "post-teleport recording must be temporarily blocked");

// Changing either scene or PlayerStats instance resets all recovery state.
var sceneReset = t.Observe("map-b", 101, new Point2(1, 1), 100, 10f, true, settings);
Check(sceneReset.ScopeReset, "new map must reset tracker");
Check(t.AutoCheckpointCount == 0 && t.ManualCheckpoint is null,
    "new map must clear manual and automatic checkpoints");
var playerReset = t.Observe("map-b", 202, new Point2(2, 2), 100, 11f, true, settings);
Check(playerReset.ScopeReset, "new player instance must reset tracker");
Check(t.PlayerInstanceId == 202, "tracker must bind new player instance");

// Bounded history plus nearest-by-distance selection when manual preference is off.
var nearestSettings = settings with { PreferManualCheckpoint = false };
for (var i = 0; i < 8; i++)
{
    var x = i * 2f;
    t.Observe("map-b", 202, new Point2(x, 2), 100, 20f + i * 2f, true, nearestSettings);
    t.Observe("map-b", 202, new Point2(x + 1.3f, 2), 100, 21.5f + i * 2f, true, nearestSettings);
}
Check(t.AutoCheckpointCount <= 4, "automatic history must obey configured bound");
Check(t.TrySelectDestination("map-b", 202, new Point2(13.9f, 2), nearestSettings, out var boundedNearest),
    "bounded history should still yield a destination");
Check(!boundedNearest.IsManual, "nearest test should select automatic history");

Console.WriteLine("MODE=modified");
Console.WriteLine("INPUT=F6/F7 policy; delay=1.5 spacing=1.25 minDistance=1 maxAuto=4");
Console.WriteLine($"OUTPUT=autoDestination=({nearest.Position.X:R},{nearest.Position.Y:R}) manualDestination=({selectedManual.Position.X:R},{selectedManual.Position.Y:R}) boundedCount={t.AutoCheckpointCount} nearestBounded=({boundedNearest.Position.X:R},{boundedNearest.Position.Y:R})");
Console.WriteLine($"SPAWN_FALLBACK=OldPrison({oldPrisonSpawn.X:R},{oldPrisonSpawn.Y:R})");
Console.WriteLine("GUARDS=damage:true pauseRecording:true deathRecording:true sceneReset:true playerReset:true postTeleportBlock:true");
Console.WriteLine($"CHECKS={checks}");
Console.WriteLine("STATUS=PASS");
Console.WriteLine("EXIT_STATUS=0");
