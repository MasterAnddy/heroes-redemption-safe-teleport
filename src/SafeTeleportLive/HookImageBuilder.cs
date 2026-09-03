using System.Buffers.Binary;
using System.Text;

namespace HeroesRedemption.SafeTeleportLive;

internal static class HookLayout
{
    internal const int AllocationSize = 0x2000;
    internal const int CodeOffset = 0x1000;
    internal const int DataOffset = 0x40;
    internal const int Command = 0x00;
    internal const int Status = 0x04;
    internal const int CurrentPosition = 0x08;
    internal const int CurrentValid = 0x10;
    internal const int TargetPosition = 0x18;
    internal const int TargetValid = 0x20;
    internal const int Heartbeat = 0x28;
    internal const int TeleportCount = 0x2C;
    internal const int PlayerPointer = 0x30;
    internal const int RigidbodyPointer = 0x38;
    internal const int SceneNamePointer = 0x40;
    internal const int SceneHandle = 0x48;
    internal const int ActiveDepth = 0x4C;
    internal static ReadOnlySpan<byte> Magic => "HRTPSAFE"u8;
    internal const int Version = 1;
}

internal sealed record HookImage(byte[] Allocation, byte[] EntryPatch, int CodeLength);

internal static class HookImageBuilder
{
    internal const int PatchLength = 13;

    internal static HookImage Build(
        long allocationBase,
        long moduleBase,
        LiveConfig config)
    {
        var image = new byte[HookLayout.AllocationSize];
        HookLayout.Magic.CopyTo(image);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(8, 4), HookLayout.Version);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(12, 4), HookLayout.CodeOffset);
        var dataAddress = checked(allocationBase + HookLayout.DataOffset);
        var returnAddress = checked(moduleBase + config.UpdateRva + PatchLength);
        var code = BuildCode(
            dataAddress,
            checked(moduleBase + config.GetPositionRva),
            checked(moduleBase + config.SetPositionRva),
            checked(moduleBase + config.SetVelocityRva),
            checked(moduleBase + config.GetActiveSceneRva),
            checked(moduleBase + config.GetSceneNameRva),
            returnAddress);
        if (code.Length > HookLayout.AllocationSize - HookLayout.CodeOffset)
            throw new InvalidOperationException("The hook code exceeds the code page.");
        code.CopyTo(image, HookLayout.CodeOffset);

        var codeAddress = checked(allocationBase + HookLayout.CodeOffset);
        var patch = new byte[PatchLength];
        patch[0] = 0x48;
        patch[1] = 0xB8; // mov rax, imm64
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(2, 8), codeAddress);
        patch[10] = 0xFF;
        patch[11] = 0xE0; // jmp rax
        patch[12] = 0x90;
        return new HookImage(image, patch, code.Length);
    }

    internal static bool TryDecodeEntryPatch(ReadOnlySpan<byte> bytes, out long codeAddress)
    {
        codeAddress = 0;
        if (bytes.Length < PatchLength || bytes[0] != 0x48 || bytes[1] != 0xB8 ||
            bytes[10] != 0xFF || bytes[11] != 0xE0 || bytes[12] != 0x90)
            return false;
        codeAddress = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(2, 8));
        return codeAddress > 0;
    }

    private static byte[] BuildCode(
        long data,
        long getPosition,
        long setPosition,
        long setVelocity,
        long getActiveScene,
        long getSceneName,
        long returnAddress)
    {
        var a = new MiniAssembler();

        // Re-run the overwritten, instruction-aligned PlayerStats.Update prologue.
        a.Emit(0x40, 0x53);                         // push rbx
        a.Emit(0x48, 0x83, 0xEC, 0x30);             // sub rsp,30h
        a.Emit(0x48, 0x8B, 0xD9);                   // mov rbx,rcx

        a.MovRax(data);
        a.Emit(0x48, 0x89, 0x44, 0x24, 0x28);       // local data
        a.Emit(0xFF, 0x40, 0x4C);                   // activeDepth++
        a.Emit(0x48, 0x39, 0x58, 0x30);             // cmp [rax+30h],rbx
        a.Je("check-scene");
        a.Emit(0x48, 0x89, 0x58, 0x30);             // mov [rax+30h],rbx
        a.Emit(0xC7, 0x40, 0x20, 0, 0, 0, 0);       // targetValid=0
        a.Emit(0xC7, 0x00, 0, 0, 0, 0);             // command=0
        a.Emit(0xC7, 0x40, 0x48, 0, 0, 0, 0);       // force scene refresh
        a.Label("check-scene");
        a.Emit(0x31, 0xC9);                         // static MethodInfo*=null
        a.MovR11(getActiveScene);
        a.Emit(0x41, 0xFF, 0xD3);                   // eax=active scene handle
        a.Emit(0x4C, 0x8B, 0x54, 0x24, 0x28);       // r10=data
        a.Emit(0x41, 0x39, 0x42, 0x48);             // same active scene?
        a.Je("same-scene");
        a.Emit(0x41, 0x89, 0x42, 0x48);             // sceneHandle=eax
        a.Emit(0x41, 0xC7, 0x42, 0x20, 0, 0, 0, 0); // targetValid=0
        a.Emit(0x41, 0xC7, 0x02, 0, 0, 0, 0);       // command=0
        a.Emit(0x8B, 0xC8);                         // ecx=eax
        a.Emit(0x31, 0xD2);                         // MethodInfo*=null
        a.MovR11(getSceneName);
        a.Emit(0x41, 0xFF, 0xD3);                   // rax=Il2CppString*
        a.Emit(0x4C, 0x8B, 0x54, 0x24, 0x28);
        a.Emit(0x49, 0x89, 0x42, 0x40);             // sceneNamePointer=rax
        a.Label("same-scene");

        a.Emit(0x48, 0x8B, 0x44, 0x24, 0x28);       // rax=data
        a.Emit(0x48, 0x8B, 0x4B, 0x28);             // rcx=PlayerStats.playerControls
        a.Emit(0x48, 0x85, 0xC9);
        a.Je("resume-original");
        a.Emit(0x48, 0x8B, 0x49, 0x38);             // rcx=PlayerControls.rb
        a.Emit(0x48, 0x85, 0xC9);
        a.Je("resume-original");
        a.Emit(0x48, 0x89, 0x4C, 0x24, 0x20);       // local rb
        a.Emit(0x48, 0x89, 0x48, 0x38);             // data.rb=rcx
        a.Emit(0x31, 0xD2);                         // methodInfo=null
        a.MovR11(getPosition);
        a.Emit(0x41, 0xFF, 0xD3);                   // call r11
        a.Emit(0x4C, 0x8B, 0x54, 0x24, 0x28);       // r10=data
        a.Emit(0x49, 0x89, 0x42, 0x08);             // currentPosition=rax
        a.Emit(0x41, 0xC7, 0x42, 0x10, 1, 0, 0, 0); // currentValid=1
        a.Emit(0x41, 0xFF, 0x42, 0x28);             // heartbeat++
        a.Emit(0x41, 0x83, 0x3A, 0x01);             // command==1?
        a.Jne("resume-original");
        a.Emit(0x41, 0x83, 0x7A, 0x20, 0x01);       // targetValid==1?
        a.Jne("missing-target");

        a.Emit(0x49, 0x8B, 0x52, 0x18);             // rdx=packed target
        a.Emit(0x48, 0x8B, 0x4C, 0x24, 0x20);       // rcx=rb
        a.Emit(0x45, 0x31, 0xC0);                   // methodInfo=null
        a.MovR11(setPosition);
        a.Emit(0x41, 0xFF, 0xD3);

        a.Emit(0x48, 0x8B, 0x4C, 0x24, 0x20);       // rcx=rb
        a.Emit(0x31, 0xD2);                         // velocity=(0,0)
        a.Emit(0x45, 0x31, 0xC0);
        a.MovR11(setVelocity);
        a.Emit(0x41, 0xFF, 0xD3);
        a.Emit(0x4C, 0x8B, 0x54, 0x24, 0x28);
        a.Emit(0x41, 0xC7, 0x02, 0, 0, 0, 0);       // command=0
        a.Emit(0x41, 0xC7, 0x42, 0x04, 1, 0, 0, 0); // status=success
        a.Emit(0x41, 0xFF, 0x42, 0x2C);             // teleportCount++
        a.Jmp("resume-original");

        a.Label("missing-target");
        a.Emit(0x41, 0xC7, 0x02, 0, 0, 0, 0);       // command=0
        a.Emit(0x41, 0xC7, 0x42, 0x04, 2, 0, 0, 0); // status=missing target

        a.Label("resume-original");
        a.Emit(0x48, 0x8B, 0x44, 0x24, 0x28);       // rax=data
        a.Emit(0xFF, 0x48, 0x4C);                   // activeDepth--
        a.Emit(0x48, 0x8B, 0xCB);                   // restore this in rcx
        a.Emit(0x80, 0x7B, 0x60, 0x00);             // original cmp [rcx+60],0 flags
        a.MovRax(returnAddress);
        a.Emit(0xFF, 0xE0);                         // jump PlayerStats.Update+13

        return a.Finish();
    }

    private sealed class MiniAssembler
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int DisplacementOffset, string Label)> _fixups = [];

        internal void Emit(params byte[] bytes) => _bytes.AddRange(bytes);

        internal void MovRax(long value)
        {
            Emit(0x48, 0xB8);
            EmitInt64(value);
        }

        internal void MovR11(long value)
        {
            Emit(0x49, 0xBB);
            EmitInt64(value);
        }

        internal void Label(string name) => _labels.Add(name, _bytes.Count);
        internal void Je(string label) => Branch([0x0F, 0x84], label);
        internal void Jne(string label) => Branch([0x0F, 0x85], label);
        internal void Jmp(string label) => Branch([0xE9], label);

        private void Branch(byte[] opcode, string label)
        {
            Emit(opcode);
            _fixups.Add((_bytes.Count, label));
            Emit(0, 0, 0, 0);
        }

        private void EmitInt64(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        internal byte[] Finish()
        {
            var result = _bytes.ToArray();
            foreach (var (offset, label) in _fixups)
            {
                if (!_labels.TryGetValue(label, out var target))
                    throw new InvalidOperationException($"Undefined label: {label}.");
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), target - (offset + 4));
            }
            return result;
        }
    }
}
