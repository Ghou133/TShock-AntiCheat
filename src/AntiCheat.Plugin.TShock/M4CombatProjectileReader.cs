using System.Buffers.Binary;

namespace AntiCheat.Plugin.TShock;

/// <summary>Exact protocol 326 optional fields, independent of the mutable game entity.</summary>
public sealed record M4CombatProjectile(uint Key, int Type, float X, float Y, float VelocityX, float VelocityY,
    float Ai0, float Ai1, float Ai2, int Banner, int Damage, float Knockback, int OriginalDamage)
{
    public bool AllNumbersFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(VelocityX) &&
        float.IsFinite(VelocityY) && float.IsFinite(Ai0) && float.IsFinite(Ai1) && float.IsFinite(Ai2) && float.IsFinite(Knockback);
}

public static class M4CombatProjectileReader
{
    public static bool TryRead(M2Packet packet, out M4CombatProjectile? observation)
    {
        observation = null;
        if (packet.Kind != M2PacketKind.ProjectileNew || packet.Payload.Length < 23) return false;
        var data = packet.Payload;
        byte first = data[22];
        int cursor = 23;
        byte second = 0;
        if ((first & 4) != 0)
        {
            if (cursor >= data.Length) return false;
            second = data[cursor++];
        }
        int expected = cursor + ((first & 1) != 0 ? 4 : 0) + ((first & 2) != 0 ? 4 : 0) +
            ((first & 8) != 0 ? 2 : 0) + ((first & 16) != 0 ? 2 : 0) + ((first & 32) != 0 ? 4 : 0) +
            ((first & 64) != 0 ? 2 : 0) + ((second & 1) != 0 ? 4 : 0);
        if (data.Length != expected) return false;
        float ReadFloat() { float value = M2PacketReader.Single(data, cursor); cursor += 4; return value; }
        int ReadShort() { int value = M2PacketReader.Int16(data, cursor); cursor += 2; return value; }
        float ai0 = (first & 1) != 0 ? ReadFloat() : 0;
        float ai1 = (first & 2) != 0 ? ReadFloat() : 0;
        int banner = (first & 8) != 0 ? (ushort)ReadShort() : 0;
        int damage = (first & 16) != 0 ? ReadShort() : 0;
        float knockback = (first & 32) != 0 ? ReadFloat() : 0;
        int originalDamage = (first & 64) != 0 ? ReadShort() : 0;
        float ai2 = (second & 1) != 0 ? ReadFloat() : 0;
        observation = new(BinaryPrimitives.ReadUInt32LittleEndian(data), M2PacketReader.Int16(data, 20),
            M2PacketReader.Single(data, 4), M2PacketReader.Single(data, 8), M2PacketReader.Single(data, 12),
            M2PacketReader.Single(data, 16), ai0, ai1, ai2, banner, damage, knockback, originalDamage);
        return true;
    }
}
