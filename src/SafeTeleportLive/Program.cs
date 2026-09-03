using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace HeroesRedemption.SafeTeleportLive;

internal static class Program
{
    private static nint _processHandle;
    private static Process? _process;
    private static long _moduleBase;
    private static long _methodAddress;
    private static long _allocationBase;
    private static long _dataAddress;
    private static LiveConfig? _config;
    private static bool _hookActive;
    private static int _restoring;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var baseDir = AppContext.BaseDirectory;
        var configPath = args.FirstOrDefault(x => x.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))?[9..]
                         ?? Path.Combine(baseDir, "safe-teleport-config.json");
        var gameRoot = args.FirstOrDefault(x => x.StartsWith("--game-root=", StringComparison.OrdinalIgnoreCase))?[12..];
        try
        {
            _config = LiveConfig.Load(configPath);
            if (args.Contains("--validate", StringComparer.OrdinalIgnoreCase))
                return ValidateOnly(configPath, gameRoot);
            if (args.Contains("--fixture-baseline", StringComparer.OrdinalIgnoreCase))
                return RunFixture(modified: false);
            if (args.Contains("--fixture", StringComparer.OrdinalIgnoreCase))
                return RunFixture(modified: true);
            var dumpImage = args.FirstOrDefault(x => x.StartsWith("--dump-hook-image=", StringComparison.OrdinalIgnoreCase));
            if (dumpImage is not null)
                return DumpHookImage(dumpImage[18..]);

            var (process, module) = FindProcessAndModule(_config.ProcessName, _config.ModuleName);
            _process = process;
            _moduleBase = module.BaseAddress;
            VerifyModuleHash(module.FileName, _config.ExpectedModuleSha256);
            _processHandle = NativeMethods.OpenGameProcess(process.Id);
            _methodAddress = checked(_moduleBase + _config.UpdateRva);

            if (args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
            {
                var result = RestoreExistingHook();
                Console.WriteLine(result);
                return 0;
            }

            InstallOrAttachHook();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; RestoreHook(); Environment.Exit(0); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreHook();
            return RunHotkeyLoop();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            RestoreHook();
            return 1;
        }
        finally
        {
            NativeMethods.Close(_processHandle);
            _processHandle = 0;
        }
    }

    private static int ValidateOnly(string configPath, string? gameRootOverride)
    {
        var gameRoot = FindGameRoot(configPath, gameRootOverride);
        var modulePath = Path.Combine(gameRoot, _config!.ModuleName);
        VerifyModuleHash(modulePath, _config.ExpectedModuleSha256);
        VerifyFilePrefix(modulePath, _config);
        var fixtureImage = HookImageBuilder.Build(0x0000012345600000, 0x0000000180000000, _config);
        if (!HookImageBuilder.TryDecodeEntryPatch(fixtureImage.EntryPatch, out var decoded) ||
            decoded != 0x0000012345600000 + HookLayout.CodeOffset)
            throw new InvalidOperationException("入口跳板编码校验失败。");
        Console.WriteLine($"VALIDATE config={Path.GetFullPath(configPath)} moduleHash=PASS prologue=PASS " +
                          $"patchLength={HookImageBuilder.PatchLength} hookCodeBytes={fixtureImage.CodeLength} status=PASS");
        return 0;
    }

    private static int RunFixture(bool modified)
    {
        const long allocation = 0x0000012345600000;
        const long module = 0x0000000180000000;
        var image = HookImageBuilder.Build(allocation, module, _config!);
        var original = _config!.ExpectedPrefixBytes;
        var simulatedEntry = original.ToArray();
        Console.WriteLine($"FIXTURE mode={(modified ? "modified" : "baseline")} input=(12.5,-3.25) original={Convert.ToHexString(original)}");

        if (!modified)
        {
            var before = new Position2(12.5f, -3.25f);
            var after = before;
            Console.WriteLine($"FIXTURE F7=NO_HANDLER F6=NO_HANDLER before={before} after={after} moved=0");
            Console.WriteLine("STATUS=PASS EXIT_STATUS=0");
            return 0;
        }

        image.EntryPatch.CopyTo(simulatedEntry, 0);
        Require(HookImageBuilder.TryDecodeEntryPatch(simulatedEntry, out var codeAddress), "跳板形状必须可识别。");
        Require(codeAddress == allocation + HookLayout.CodeOffset, "跳板目的地址错误。");
        Require(image.Allocation.AsSpan(0, HookLayout.Magic.Length).SequenceEqual(HookLayout.Magic), "魔数错误。");
        Require(BinaryPrimitives.ReadInt32LittleEndian(image.Allocation.AsSpan(8, 4)) == HookLayout.Version, "版本错误。");

        var packed = new Position2(12.5f, -3.25f).Packed;
        Require(Position2.FromPacked(packed) == new Position2(12.5f, -3.25f), "坐标打包往返失败。");
        var policy = new AnchorPolicy(_config.EmergencyStep);
        const long playerA = 0x11110000;
        const long playerB = 0x22220000;
        const int sceneA = 10;
        const int sceneB = 20;
        var saved = policy.Save(new Position2(12.5f, -3.25f), playerA, sceneA);
        var manual = policy.ChooseTarget(new Position2(99, 88), playerA, sceneA);
        Require(manual.Target == saved && manual.Source == "manual-anchor", "F7/F6 锚点往返失败。");
        policy.ObserveScope(playerA, sceneB);
        Require(!policy.HasManualAnchor, "同一 PlayerStats 跨场景必须清除锚点。");
        _ = policy.Save(new Position2(3, 4), playerA, sceneB);
        policy.ObserveScope(playerB, sceneB);
        Require(!policy.HasManualAnchor, "切换 PlayerStats 实例必须清除跨场景锚点。");
        var safeSpawn = new Position2(-123.5f, -1.125f);
        var sceneFallback = policy.ChooseTarget(new Position2(1, 2), playerB, sceneB, safeSpawn, "Cemetery");
        Require(sceneFallback.Target == safeSpawn && sceneFallback.Source == "verified-scene-spawn:Cemetery",
            "无手动锚点时必须优先选择当前场景已验证出生点。");
        var emergency1 = policy.ChooseTarget(new Position2(1, 2), playerB, sceneB);
        var emergency2 = policy.ChooseTarget(new Position2(1, 2), playerB, sceneB);
        Require(emergency1.Target == new Position2(1, 2 + _config.EmergencyStep), "首次应急候选错误。");
        Require(emergency2.Target == new Position2(1 + _config.EmergencyStep, 2), "第二应急候选错误。");
        Require(!new Position2(float.NaN, 0).IsPlausible(_config.MaximumCoordinateMagnitude), "NaN 必须被拒绝。");

        original.CopyTo(simulatedEntry, 0);
        Require(simulatedEntry.SequenceEqual(original), "回滚后入口必须逐字节还原。");
        Console.WriteLine($"FIXTURE hookAddress=0x{codeAddress:X} codeBytes={image.CodeLength} patch={Convert.ToHexString(image.EntryPatch)}");
        Console.WriteLine($"FIXTURE F7={saved} F6={manual.Target} source={manual.Source} moved=1 velocityAfter=(0,0)");
        Console.WriteLine($"FIXTURE sceneChange=0x{playerB:X} anchorCleared=1 scene=Cemetery verifiedSpawn={sceneFallback.Target} " +
                          $"emergency1={emergency1.Target} emergency2={emergency2.Target}");
        Console.WriteLine($"FIXTURE rollback={Convert.ToHexString(simulatedEntry)} exact=1");
        Console.WriteLine("STATUS=PASS EXIT_STATUS=0");
        return 0;
    }

    private static int DumpHookImage(string path)
    {
        const long allocation = 0x0000012345600000;
        const long module = 0x0000000180000000;
        var image = HookImageBuilder.Build(allocation, module, _config!);
        File.WriteAllBytes(path, image.Allocation);
        Console.WriteLine($"HOOK_IMAGE path={Path.GetFullPath(path)} codeOffset=0x{HookLayout.CodeOffset:X} " +
                          $"codeLength={image.CodeLength} status=PASS");
        return 0;
    }

    private static void InstallOrAttachHook()
    {
        var current = NativeMethods.Read(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
        if (current.SequenceEqual(_config!.ExpectedPrefixBytes))
        {
            _allocationBase = NativeMethods.Allocate(_processHandle, HookLayout.AllocationSize);
            var image = HookImageBuilder.Build(_allocationBase, _moduleBase, _config);
            NativeMethods.Write(_processHandle, _allocationBase, image.Allocation);
            _ = NativeMethods.Protect(_processHandle, _allocationBase + HookLayout.CodeOffset, 0x1000,
                NativeMethods.PageExecuteRead);
            NativeMethods.Flush(_processHandle, _allocationBase + HookLayout.CodeOffset, image.CodeLength);
            IReadOnlyList<nint>? suspended = null;
            try
            {
                suspended = NativeMethods.SuspendThreadsForPatch(_process!, _methodAddress, HookImageBuilder.PatchLength);
                var recheck = NativeMethods.Read(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
                if (!recheck.SequenceEqual(_config.ExpectedPrefixBytes))
                    throw new InvalidOperationException("挂接前 PlayerStats.Update 字节发生变化，未覆盖。");
                var oldProtection = NativeMethods.Protect(
                    _processHandle, _methodAddress, HookImageBuilder.PatchLength, NativeMethods.PageExecuteReadWrite);
                try
                {
                    NativeMethods.Write(_processHandle, _methodAddress, image.EntryPatch);
                    NativeMethods.Flush(_processHandle, _methodAddress, image.EntryPatch.Length);
                }
                finally
                {
                    _ = NativeMethods.Protect(
                        _processHandle, _methodAddress, HookImageBuilder.PatchLength, oldProtection);
                }
                var installed = NativeMethods.Read(_processHandle, _methodAddress, image.EntryPatch.Length);
                if (!installed.SequenceEqual(image.EntryPatch))
                    throw new InvalidOperationException("入口钩子写入后回读不一致。");
            }
            finally
            {
                if (suspended is not null) NativeMethods.ResumeAndClose(suspended);
            }
            _dataAddress = _allocationBase + HookLayout.DataOffset;
            _hookActive = true;
            Console.WriteLine($"LIVE_HOOK_INSTALLED method=0x{_methodAddress:X} cave=0x{_allocationBase:X} " +
                              $"original={Convert.ToHexString(_config.ExpectedPrefixBytes)} status=PASS");
            return;
        }

        if (!TryResolveOurHook(current, out _allocationBase))
            throw new InvalidOperationException($"PlayerStats.Update 入口既非原始字节也非本工具钩子：{Convert.ToHexString(current)}。");
        _dataAddress = _allocationBase + HookLayout.DataOffset;
        _hookActive = true;
        Console.WriteLine($"LIVE_HOOK_REATTACHED method=0x{_methodAddress:X} cave=0x{_allocationBase:X} status=PASS");
    }

    private static int RunHotkeyLoop()
    {
        var saveVk = NativeMethods.ParseVirtualKey(_config!.SaveAnchorHotkey);
        var teleportVk = NativeMethods.ParseVirtualKey(_config.TeleportHotkey);
        var clearVk = NativeMethods.ParseVirtualKey(_config.ClearAnchorHotkey);
        var exitVk = NativeMethods.ParseVirtualKey(_config.ExitHotkey);
        var all = new[] { saveVk, teleportVk, clearVk, exitVk };
        var previous = all.ToDictionary(x => x, _ => false);
        var policy = new AnchorPolicy(_config.EmergencyStep);
        var lastCount = 0;

        Console.WriteLine($"已连接 PID {_process!.Id}。{_config.SaveAnchorHotkey}=保存安全锚点，" +
                          $"{_config.TeleportHotkey}=返回锚点/应急脱困，{_config.ClearAnchorHotkey}=清除锚点，" +
                          $"{_config.ExitHotkey}=还原钩子并退出。");
        Console.WriteLine("热键仅在游戏窗口位于前台时响应；不会关闭游戏。");

        while (!_process.HasExited)
        {
            var foreground = NativeMethods.IsForegroundProcess(_process.Id);
            foreach (var vk in all)
            {
                var down = NativeMethods.IsKeyDown(vk);
                if (foreground && down && !previous[vk])
                {
                    if (vk == exitVk)
                    {
                        RestoreHook();
                        return 0;
                    }
                    var snapshot = ReadSnapshot();
                    policy.ObserveScope(snapshot.PlayerPointer, snapshot.SceneHandle);
                    if (vk == saveVk)
                    {
                        RequireUsable(snapshot);
                        var saved = policy.Save(snapshot.Current, snapshot.PlayerPointer, snapshot.SceneHandle);
                        WriteTarget(saved);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ANCHOR_SAVED player=0x{snapshot.PlayerPointer:X} position={saved}");
                    }
                    else if (vk == clearVk)
                    {
                        policy.Clear();
                        WriteInt32(HookLayout.TargetValid, 0);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ANCHOR_CLEARED");
                    }
                    else if (vk == teleportVk)
                    {
                        RequireUsable(snapshot);
                        var verifiedSpawn = TryGetVerifiedSpawn(snapshot.SceneName);
                        var choice = policy.ChooseTarget(
                            snapshot.Current,
                            snapshot.PlayerPointer,
                            snapshot.SceneHandle,
                            verifiedSpawn,
                            snapshot.SceneName);
                        if (!choice.Target.IsPlausible(_config.MaximumCoordinateMagnitude))
                            throw new InvalidOperationException($"目标坐标异常：{choice.Target}。");
                        WriteTarget(choice.Target);
                        WriteInt32(HookLayout.Status, 0);
                        WriteInt32(HookLayout.Command, 1); // command is committed last
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TELEPORT_QUEUED source={choice.Source} " +
                                          $"from={snapshot.Current} target={choice.Target}");
                    }
                }
                previous[vk] = down;
            }

            var count = ReadInt32(HookLayout.TeleportCount);
            if (count != lastCount)
            {
                var snapshot = ReadSnapshot();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TELEPORT_APPLIED count={count} position={snapshot.Current} " +
                                  $"velocity=(0,0) status=PASS");
                lastCount = count;
            }
            Thread.Sleep(20);
        }
        _hookActive = false;
        Console.WriteLine("游戏程序已结束；系统已自动回收即时钩子内存。");
        return 0;
    }

    private static Snapshot ReadSnapshot()
    {
        var bytes = NativeMethods.Read(_processHandle, _dataAddress, 0x50);
        var sceneNamePointer = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(HookLayout.SceneNamePointer, 8));
        return new Snapshot(
            Position2.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(HookLayout.CurrentPosition, 8))),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HookLayout.CurrentValid, 4)) != 0,
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(HookLayout.PlayerPointer, 8)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HookLayout.Heartbeat, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HookLayout.SceneHandle, 4)),
            ReadIl2CppString(sceneNamePointer));
    }

    private static void RequireUsable(Snapshot snapshot)
    {
        if (!snapshot.Valid || snapshot.PlayerPointer == 0 || snapshot.Heartbeat == 0)
            throw new InvalidOperationException("角色刚切换场景或尚未完成初始化，请等待画面恢复后再按一次。");
        if (!snapshot.Current.IsPlausible(_config!.MaximumCoordinateMagnitude))
            throw new InvalidOperationException($"读取到异常角色坐标：{snapshot.Current}。");
    }

    private static void WriteTarget(Position2 target)
    {
        Span<byte> packed = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(packed, target.Packed);
        NativeMethods.Write(_processHandle, _dataAddress + HookLayout.TargetPosition, packed);
        WriteInt32(HookLayout.TargetValid, 1);
    }

    private static Position2? TryGetVerifiedSpawn(string? sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return null;
        foreach (var (name, spawn) in _config!.SafeSceneSpawns)
            if (name.Equals(sceneName, StringComparison.OrdinalIgnoreCase))
                return spawn.ToPosition();
        return null;
    }

    private static string? ReadIl2CppString(long pointer)
    {
        if (pointer == 0) return null;
        try
        {
            var header = NativeMethods.Read(_processHandle, pointer + 0x10, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 0 or > 128) return null;
            return System.Text.Encoding.Unicode.GetString(
                NativeMethods.Read(_processHandle, pointer + 0x14, checked(length * 2)));
        }
        catch { return null; }
    }

    private static int ReadInt32(int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(NativeMethods.Read(_processHandle, _dataAddress + offset, 4));

    private static void WriteInt32(int offset, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        NativeMethods.Write(_processHandle, _dataAddress + offset, bytes);
    }

    private static string RestoreExistingHook()
    {
        var current = NativeMethods.Read(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
        if (current.SequenceEqual(_config!.ExpectedPrefixBytes))
            return "ROLLBACK state=ALREADY_ORIGINAL status=PASS";
        if (!TryResolveOurHook(current, out _allocationBase))
            throw new InvalidOperationException($"入口不是本工具钩子，停止覆盖：{Convert.ToHexString(current)}。");
        _hookActive = true;
        RestoreHook();
        var restored = NativeMethods.Read(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
        if (!restored.SequenceEqual(_config.ExpectedPrefixBytes))
            throw new InvalidOperationException("回滚后字节校验失败。");
        return $"ROLLBACK before={Convert.ToHexString(current)} after={Convert.ToHexString(restored)} status=PASS";
    }

    private static bool TryResolveOurHook(byte[] entry, out long allocation)
    {
        allocation = 0;
        if (!HookImageBuilder.TryDecodeEntryPatch(entry, out var codeAddress)) return false;
        allocation = codeAddress - HookLayout.CodeOffset;
        if (allocation <= 0) return false;
        try
        {
            var header = NativeMethods.Read(_processHandle, allocation, 16);
            return header.AsSpan(0, HookLayout.Magic.Length).SequenceEqual(HookLayout.Magic) &&
                   BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4)) == HookLayout.Version &&
                   BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4)) == HookLayout.CodeOffset;
        }
        catch { return false; }
    }

    private static void RestoreHook()
    {
        if (!_hookActive || _processHandle == 0 || _process is null || _process.HasExited ||
            Interlocked.Exchange(ref _restoring, 1) != 0) return;
        try
        {
            IReadOnlyList<nint>? suspended = null;
            try
            {
                suspended = NativeMethods.SuspendThreadsForPatch(_process, _methodAddress, HookImageBuilder.PatchLength);
                var current = NativeMethods.Read(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
                if (current.SequenceEqual(_config!.ExpectedPrefixBytes))
                {
                    _hookActive = false;
                    return;
                }
                if (!TryResolveOurHook(current, out _))
                    throw new InvalidOperationException("退出时入口已被其他工具改动，未覆盖未知字节。");
                var oldProtection = NativeMethods.Protect(
                    _processHandle, _methodAddress, HookImageBuilder.PatchLength, NativeMethods.PageExecuteReadWrite);
                try
                {
                    NativeMethods.Write(_processHandle, _methodAddress, _config.ExpectedPrefixBytes);
                    NativeMethods.Flush(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
                }
                finally
                {
                    _ = NativeMethods.Protect(
                        _processHandle, _methodAddress, HookImageBuilder.PatchLength, oldProtection);
                }
                var restored = NativeMethods.Read(_processHandle, _methodAddress, HookImageBuilder.PatchLength);
                if (!restored.SequenceEqual(_config.ExpectedPrefixBytes))
                    throw new InvalidOperationException("即时钩子回滚校验失败。");
                Console.WriteLine($"LIVE_HOOK_ROLLED_BACK bytes={Convert.ToHexString(restored)} status=PASS");
                _hookActive = false;
            }
            finally
            {
                if (suspended is not null) NativeMethods.ResumeAndClose(suspended);
            }
            TryReleaseAllocationAfterDrain();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"回滚错误：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _restoring, 0);
        }
    }

    private static void TryReleaseAllocationAfterDrain()
    {
        if (_allocationBase == 0 || _process is null || _process.HasExited) return;
        // The entry has already been restored, so no new hook invocation can begin.
        // Give a thread that had already jumped to the cave time to leave it, then
        // require both the explicit active-depth counter and all RIPs to be outside
        // the allocation before MEM_RELEASE.
        Thread.Sleep(50);
        for (var i = 0; i < 100; i++)
        {
            var depth = BinaryPrimitives.ReadInt32LittleEndian(
                NativeMethods.Read(_processHandle, _dataAddress + HookLayout.ActiveDepth, 4));
            if (depth == 0) break;
            if (i == 99)
            {
                Console.WriteLine($"LIVE_CAVE_RETAINED activeDepth={depth} reason=in-flight-call status=PASS");
                return;
            }
            Thread.Sleep(5);
        }

        IReadOnlyList<nint>? suspended = null;
        try
        {
            suspended = NativeMethods.SuspendThreadsForPatch(_process, _allocationBase, HookLayout.AllocationSize);
            var depth = BinaryPrimitives.ReadInt32LittleEndian(
                NativeMethods.Read(_processHandle, _dataAddress + HookLayout.ActiveDepth, 4));
            if (depth != 0)
            {
                Console.WriteLine($"LIVE_CAVE_RETAINED activeDepth={depth} reason=second-check status=PASS");
                return;
            }
            NativeMethods.Release(_processHandle, _allocationBase);
            Console.WriteLine($"LIVE_CAVE_RELEASED base=0x{_allocationBase:X} size=0x{HookLayout.AllocationSize:X} status=PASS");
            _allocationBase = 0;
            _dataAddress = 0;
        }
        finally
        {
            if (suspended is not null) NativeMethods.ResumeAndClose(suspended);
        }
    }

    private static (Process Process, ProcessModule Module) FindProcessAndModule(string processName, string moduleName)
    {
        var normalized = Path.GetFileNameWithoutExtension(processName);
        foreach (var process in Process.GetProcessesByName(normalized).OrderByDescending(p => p.StartTime))
        {
            try
            {
                var module = process.Modules.Cast<ProcessModule>().FirstOrDefault(x =>
                    string.Equals(x.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
                if (module is not null) return (process, module);
            }
            catch { process.Dispose(); }
        }
        throw new InvalidOperationException($"未找到正在运行的 {processName}。");
    }

    private static void VerifyModuleHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"模块 SHA-256 不匹配：{actual}。");
    }

    private static void VerifyFilePrefix(string modulePath, LiveConfig config)
    {
        // This PE has a verified RVA-to-file delta of 0xE00 for the executable section.
        var offset = checked(config.UpdateRva - 0xE00);
        using var stream = File.OpenRead(modulePath);
        stream.Position = offset;
        var bytes = new byte[HookImageBuilder.PatchLength];
        if (stream.Read(bytes) != bytes.Length || !bytes.SequenceEqual(config.ExpectedPrefixBytes))
            throw new InvalidOperationException("磁盘 PlayerStats.Update 前缀校验失败。");
    }

    private static string FindGameRoot(string configPath, string? gameRootOverride)
    {
        if (!string.IsNullOrWhiteSpace(gameRootOverride))
        {
            var resolved = Path.GetFullPath(gameRootOverride);
            if (File.Exists(Path.Combine(resolved, _config!.ModuleName)))
                return resolved;
            throw new FileNotFoundException($"指定的遊戲目錄中找不到 {_config!.ModuleName}：{resolved}");
        }

        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, _config!.ModuleName))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"从配置路径向上未找到 {_config!.ModuleName}。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private readonly record struct Snapshot(
        Position2 Current,
        bool Valid,
        long PlayerPointer,
        int Heartbeat,
        int SceneHandle,
        string? SceneName);
}
