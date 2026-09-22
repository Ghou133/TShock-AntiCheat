using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria;
using Terraria.GameContent;
using Terraria.Net;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>Allocation-free validation before native DeserializeRequest allocates client-sized lists.</summary>
public static class M6CraftingPacketSafety
{
    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (byte)args.MsgID != 82) return null;
        var malformed = new M5WorldWorkCost(NetworkRequestKind.Ordinary, 0, true, "crafting-module-frame-malformed");
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 2 || length > ushort.MaxValue - 3 ||
            args.Index < 0 || args.Index > buffer.Length - length) return malformed;
        var manager = NetManager.Instance;
        ushort id = manager.GetId<CraftingRequests.NetCraftingRequestsModule>();
        var module = manager.GetModule<CraftingRequests.NetCraftingRequestsModule>();
        // Generic storage defaults to id zero before registration. Never reinterpret another module.
        if (module is null || !manager._modules.TryGetValue(id, out var registered) ||
            !ReferenceEquals(module, registered)) return null;
        var body = buffer.AsSpan(args.Index, length);
        if (BinaryPrimitives.ReadUInt16LittleEndian(body) != id) return null;
        if (RejectPayload(body, id, Main.chest.Length, out int work)) return malformed;
        return new(NetworkRequestKind.WorldMutation, work, false, "crafting-target-authorization-admission");
    }

    public static bool RejectPayload(ReadOnlySpan<byte> body, ushort craftingModuleId, int chestCapacity) =>
        RejectPayload(body, craftingModuleId, chestCapacity, out _);

    public static bool RejectPayload(ReadOnlySpan<byte> body, ushort craftingModuleId, int chestCapacity,
        out int authorizationWorkUnits)
    {
        authorizationWorkUnits = 0;
        if (body.Length < 2) return true;
        if (BinaryPrimitives.ReadUInt16LittleEndian(body) != craftingModuleId) return false;
        int offset = 2;
        if (!Read7(body, ref offset, out int requirements) || requirements < 0 ||
            requirements > M6CraftingContainerSafety.MaximumRequirements ||
            requirements > (body.Length - offset - 1) / 5) return true;
        for (int i = 0; i < requirements; i++)
        {
            if (body.Length - offset < 4) return true;
            offset += 4; // Item/group semantics remain in the transactional handler.
            if (!Read7(body, ref offset, out int stack) || stack <= 0) return true;
        }
        if (!Read7(body, ref offset, out int targets) || targets < 0 ||
            targets > M6CraftingContainerSafety.MaximumTargets || targets > body.Length - offset) return true;
        for (int i = 0; i < targets; i++)
            // Negative bank/local references are an explicit native null-target sentinel.
            if (!Read7(body, ref offset, out int target) || target >= chestCapacity) return true;
        if (offset != body.Length) return true;
        // Cost is charged before per-target region/range/occupancy hooks. This is an admission
        // weight (one budget token per supplied target), not a claim to measure their CPU time.
        // The later feasibility callback separately caps native slot visits and simulation.
        authorizationWorkUnits = targets * 16;
        return false;
    }

    private static bool Read7(ReadOnlySpan<byte> body, ref int offset, out int value)
    {
        uint bits = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (offset >= body.Length) { value = 0; return false; }
            byte next = body[offset++];
            if (shift == 28 && (next & 0xf0) != 0) { value = 0; return false; }
            bits |= (uint)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) { value = unchecked((int)bits); return true; }
        }
        value = 0; return false;
    }
}
