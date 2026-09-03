using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HeroesRedemption.SafeTeleportLive;

internal static class NativeMethods
{
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ThreadSuspendResume = 0x0002;
    internal const uint ThreadGetContext = 0x0008;
    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint MemRelease = 0x8000;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageExecuteRead = 0x20;
    internal const uint PageExecuteReadWrite = 0x40;
    private const uint ContextAmd64 = 0x00100000;
    private const uint ContextControl = ContextAmd64 | 0x1;
    private const int ContextSize = 0x4D0;
    private const int ContextFlagsOffset = 0x30;
    private const int RipOffset = 0xF8;

    internal static int ParseVirtualKey(string text)
    {
        if (text.Length >= 2 && text[0] is 'F' or 'f' && int.TryParse(text[1..], out var n) && n is >= 1 and <= 24)
            return 0x70 + n - 1;
        throw new InvalidOperationException($"Unsupported hotkey: {text}.");
    }

    internal static nint OpenGameProcess(int pid)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite,
            false, pid);
        if (handle == 0) ThrowLast("OpenProcess");
        return handle;
    }

    internal static byte[] Read(nint process, long address, int count)
    {
        var bytes = new byte[count];
        if (!ReadProcessMemory(process, (nint)address, bytes, count, out var read) || read != count)
            ThrowLast("ReadProcessMemory");
        return bytes;
    }

    internal static void Write(nint process, long address, ReadOnlySpan<byte> value)
    {
        var bytes = value.ToArray();
        if (!WriteProcessMemory(process, (nint)address, bytes, bytes.Length, out var written) || written != bytes.Length)
            ThrowLast("WriteProcessMemory");
    }

    internal static long Allocate(nint process, int count)
    {
        var address = VirtualAllocEx(process, 0, (nuint)count, MemReserve | MemCommit, PageReadWrite);
        if (address == 0) ThrowLast("VirtualAllocEx");
        return address;
    }

    internal static uint Protect(nint process, long address, int count, uint protection)
    {
        if (!VirtualProtectEx(process, (nint)address, (nuint)count, protection, out var old))
            ThrowLast("VirtualProtectEx");
        return old;
    }

    internal static void Flush(nint process, long address, int count)
    {
        if (!FlushInstructionCache(process, (nint)address, (nuint)count)) ThrowLast("FlushInstructionCache");
    }

    internal static void Release(nint process, long address)
    {
        if (!VirtualFreeEx(process, (nint)address, 0, MemRelease)) ThrowLast("VirtualFreeEx");
    }

    internal static bool IsForegroundProcess(int pid)
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;
        _ = GetWindowThreadProcessId(window, out var foregroundPid);
        return foregroundPid == (uint)pid;
    }

    internal static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    internal static IReadOnlyList<nint> SuspendThreadsForPatch(
        System.Diagnostics.Process process,
        long patchStart,
        int patchLength)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suspended = new List<nint>();
            try
            {
                foreach (System.Diagnostics.ProcessThread thread in process.Threads)
                {
                    var handle = OpenThread(ThreadSuspendResume | ThreadGetContext, false, (uint)thread.Id);
                    if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenThread({thread.Id})");
                    if (SuspendThread(handle) == uint.MaxValue)
                    {
                        CloseHandle(handle);
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"SuspendThread({thread.Id})");
                    }
                    suspended.Add(handle);
                }

                var occupied = suspended.Any(handle =>
                {
                    var rip = GetInstructionPointer(handle);
                    return rip >= patchStart && rip < patchStart + patchLength;
                });
                if (!occupied) return suspended;
            }
            catch
            {
                ResumeAndClose(suspended);
                throw;
            }

            ResumeAndClose(suspended);
            Thread.Sleep(2);
        }
        throw new InvalidOperationException("PlayerStats.Update remained in use; the patch was not written.");
    }

    internal static void ResumeAndClose(IEnumerable<nint> handles)
    {
        foreach (var handle in handles.Reverse())
        {
            _ = ResumeThread(handle);
            _ = CloseHandle(handle);
        }
    }

    private static long GetInstructionPointer(nint thread)
    {
        var buffer = Marshal.AllocHGlobal(ContextSize);
        try
        {
            Span<byte> zero = stackalloc byte[256];
            for (var offset = 0; offset < ContextSize; offset += zero.Length)
            {
                var count = Math.Min(zero.Length, ContextSize - offset);
                Marshal.Copy(zero[..count].ToArray(), 0, buffer + offset, count);
            }
            Marshal.WriteInt32(buffer, ContextFlagsOffset, unchecked((int)ContextControl));
            if (!GetThreadContext(thread, buffer)) ThrowLast("GetThreadContext");
            return Marshal.ReadInt64(buffer, RipOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void Close(nint handle)
    {
        if (handle != 0) _ = CloseHandle(handle);
    }

    private static void ThrowLast(string operation) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenThread(uint access, bool inheritHandle, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(nint thread, nint context);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, int size, out int read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, int size, out int written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(nint process, nint address, nuint size, uint newProtection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
