using System.Collections.Immutable;
using Terraria.ID;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// The P1-C candidate deliberately names a small, version-locked boss-bag set.
/// It is not a full ItemID ledger; non-bag advanced items stay outside this slice.
/// </summary>
public static class M18ImportantItemCatalog
{
    public static ImmutableDictionary<int, string> Items { get; } =
        new Dictionary<int, string>
        {
            [ItemID.KingSlimeBossBag] = nameof(ItemID.KingSlimeBossBag),
            [ItemID.EyeOfCthulhuBossBag] = nameof(ItemID.EyeOfCthulhuBossBag),
            [ItemID.EaterOfWorldsBossBag] = nameof(ItemID.EaterOfWorldsBossBag),
            [ItemID.BrainOfCthulhuBossBag] = nameof(ItemID.BrainOfCthulhuBossBag),
            [ItemID.QueenBeeBossBag] = nameof(ItemID.QueenBeeBossBag),
            [ItemID.SkeletronBossBag] = nameof(ItemID.SkeletronBossBag),
            [ItemID.WallOfFleshBossBag] = nameof(ItemID.WallOfFleshBossBag),
            [ItemID.DestroyerBossBag] = nameof(ItemID.DestroyerBossBag),
            [ItemID.TwinsBossBag] = nameof(ItemID.TwinsBossBag),
            [ItemID.SkeletronPrimeBossBag] = nameof(ItemID.SkeletronPrimeBossBag),
            [ItemID.PlanteraBossBag] = nameof(ItemID.PlanteraBossBag),
            [ItemID.GolemBossBag] = nameof(ItemID.GolemBossBag),
            [ItemID.FishronBossBag] = nameof(ItemID.FishronBossBag),
            [ItemID.CultistBossBag] = nameof(ItemID.CultistBossBag),
            [ItemID.MoonLordBossBag] = nameof(ItemID.MoonLordBossBag),
            [ItemID.FairyQueenBossBag] = nameof(ItemID.FairyQueenBossBag),
            [ItemID.QueenSlimeBossBag] = nameof(ItemID.QueenSlimeBossBag),
            [ItemID.DeerclopsBossBag] = nameof(ItemID.DeerclopsBossBag),
        }.ToImmutableDictionary();
}
