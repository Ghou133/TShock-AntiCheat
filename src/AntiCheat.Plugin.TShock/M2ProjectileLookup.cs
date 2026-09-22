using Terraria;
using Terraria.DataStructures;

namespace AntiCheat.Plugin.TShock;

/// <summary>Protocol 326 has 1001 key identities per spawner, independently of the active entity-slot domain.</summary>
public static class M2ProjectileLookup
{
    public const int KeyIdentityCount = 1001;

    public static bool TryGet(ProjectileKey key, out Projectile? projectile, out bool lookupComplete)
    {
        projectile = null;
        lookupComplete = false;
        var map = Projectile.keyToIndex;
        var entities = Main.projectile;
        if (key.Index >= KeyIdentityCount || map is null || map.GetLength(0) != 256 ||
            map.GetLength(1) != KeyIdentityCount || entities is null || Main.netMode != 2 || Main.maxTilesX <= 0) return false;
        int index = map[key.Spawner, key.Index];
        if ((uint)index >= entities.Length || entities[index] is not { } candidate) return false;
        lookupComplete = true;
        if (candidate.key.bits != key.bits) return false;
        projectile = candidate;
        return true;
    }
}
