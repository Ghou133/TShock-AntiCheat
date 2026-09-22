using System.Collections.Immutable;
using AntiCheat.Progression;
using Terraria;
using Terraria.GameContent;

namespace AntiCheat.Plugin.TShock;

public sealed record M6RazorRecipeSnapshot(bool RecipeTableVerified, bool NativeWorldPermitsRecipe,
    int RequiredTile, ImmutableArray<(int ItemId, int Stack)> Ingredients,
    Truth SourceWorldProhibition, Truth SourceHardModeProhibition)
{
    // Recipe availability cannot attest SSC migration history, passive transfers, server grants or
    // the origin of its ingredients. It must never become a blanket item-possession proof.
    public bool AcquisitionPathsComplete => false;
}

/// <summary>
/// The selected 5334 item group gets actual target recipe/world context independently of its
/// already implemented summon-action contract. Reads one bounded recipe table on replacement,
/// then checks only the selected template. It grants no inventory authority or ban qualification.
/// </summary>
public sealed class M6RazorRecipeContexts(string fingerprint)
{
    private Recipe[]? table;
    private int count = -1;
    private Recipe? razor;
    private bool complete;

    public void Refresh()
    {
        if (ReferenceEquals(table, Main.recipe) && count == Recipe.numRecipes) return;
        table = Main.recipe; count = Recipe.numRecipes; razor = null; complete = false;
        if (fingerprint != TargetRuntime.Fingerprint || table is null || table.Length > 4096 ||
            count <= 0 || count > table.Length) return;
        int matches = 0;
        for (int i = 0; i < count; i++)
        {
            if (table[i] is not { } recipe) return;
            if (recipe.createItem?.type == 5334) { razor = recipe; matches++; }
        }
        complete = matches == 1 && TemplateMatches(razor);
    }

    public M6RazorRecipeSnapshot Capture(bool nativePluginContractComplete)
    {
        bool valid = nativePluginContractComplete && complete && ReferenceEquals(table, Main.recipe) &&
            count == Recipe.numRecipes && TemplateMatches(razor);
        return new(valid, SpecialSeedFeatures.Mechdusa, valid ? razor!.requiredTile : -1,
            valid ? razor!.requiredItem.Take(3).Select(x => (x.type, x.stack)).ToImmutableArray() : [],
            Main.zenithWorld ? Truth.False : Truth.True,
            // No raw inventory/summon packet carries the authoritative currentbossdefeated event.
            // A true hardMode flag disproves OP-081; a false flag cannot invent BossDType.NA.
            Main.hardMode ? Truth.False : Truth.Unknown);
    }

    private static bool TemplateMatches(Recipe? recipe) => recipe is not null &&
        recipe.createItem is { type: 5334, stack: 1 } && recipe.requiredTile == 134 && recipe.needMechdusa &&
        !recipe.needWater && !recipe.needHoney && !recipe.needLava && !recipe.needSnowBiome &&
        !recipe.needGraveyardBiome && !recipe.needTorchGodsFavor &&
        recipe.requiredItem is { Length: 15 } items &&
        items[0] is { type: 544, stack: 1 } && items[1] is { type: 557, stack: 1 } && items[2] is { type: 556, stack: 1 } &&
        items.Skip(3).All(x => x is not null && x.IsAir) &&
        recipe.acceptedGroups is { Length: 15 } groups && groups.All(x => x < 0);
}
