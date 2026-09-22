using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// PhantasmArrow631 AI_122 casts ai[0] to int then directly indexes Main.npc.
/// Reject only unsafe target-array access before packet27 creates/updates and publishes an entity.
/// Target absence, inactive NPCs, parent provenance, damage and cleanup are not account proofs.
/// </summary>
public static class M14ResumePhantasmTargetSafety
{
    public const string ContractVersion = "terraria1.4.5.8-326-phantasm-target-index-safety-v1";

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (int)args.MsgID != 27) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        // Ordinary27 parsing retains ownership of unrelated/incomplete fixed headers.
        if (buffer is null || length < 23 || args.Index < 0 || args.Index > buffer.Length - length) return null;
        if (BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(args.Index + 20, 2)) != ProjectileID.PhantasmArrow) return null;
        var parsed = M2PacketReader.Read(args, true);
        if (parsed.Packet is null || !M4CombatProjectileReader.TryRead(parsed.Packet, out var shot))
            return Denied("phantasm-target-frame-incomplete");
        var targets = Main.npc;
        // A missing/extended host array is a model boundary, not a cheating claim. Both the
        // playable200 slots and native optional spare slot are safe wherever physically present.
        if (targets is null || targets.Length is < 200 or > 201) return null;
        float value = shot!.Ai0;
        // Check finiteness/range before the conversion: overflowing conv.i4 is platform-sensitive.
        // -1.99 truncates to the native -1 sentinel; -0.99 truncates to slot0. Preserve both.
        if (!float.IsFinite(value) || value <= -2f || value >= targets.Length)
            return Denied("phantasm-target-projection-outside-native-array");
        return new(NetworkRequestKind.EntitySync, 0, false, "phantasm-target-native-index-or-minus-one");
    }

    private static M5WorldWorkCost Denied(string reason) => new(NetworkRequestKind.EntitySync, 0, true, reason);
}
