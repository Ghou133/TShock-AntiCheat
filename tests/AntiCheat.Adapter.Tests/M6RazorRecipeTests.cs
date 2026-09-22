using System.Collections;
using System.Reflection;
using System.Text.Json;
using AntiCheat.Plugin.TShock;
using AntiCheat.Progression;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M6RazorRecipeTests
{
    private readonly List<Action> restore = [];
    private Recipe razor = null!;
    private Recipe[] oldRecipes = null!;
    private bool oldRemix, oldWorthy, oldZenith, oldHard;
    private int oldNet, oldLocal;
    private Player oldPlayer = null!;

    [OneTimeSetUp]
    public void LoadTheActualLockedTargetsRecipeDefinitions()
    {
        oldRecipes = Main.recipe;
        // Native SetupRecipes affects recipe groups and material/shimmer lookup arrays. Preserve
        // their original state so these actual-method tests do not leak initialization into others.
        foreach (var type in new[] { typeof(Recipe), typeof(RecipeGroup), typeof(RecipeGroups), typeof(ItemID.Sets), typeof(ShimmerTransforms), typeof(Lang), typeof(ContentSamples) })
            PreserveStatics(type);
        Main.recipe = Enumerable.Range(0, Recipe.maxRecipes).Select(_ => new Recipe()).ToArray();
        Recipe.numRecipes = 0; Recipe.currentRecipe = new Recipe();
        Recipe.TileUsedInRecipes = new bool[TileID.Count]; Recipe.TileCountsAs = new List<int>[TileID.Count];
        RecipeGroup.recipeGroups.Clear(); RecipeGroup.nextRecipeGroupIndex = 0;
        Lang.InitializeLegacyLocalization();
        Recipe.SetupRecipeGroups();
        // SetDefaults5 for beard items reads Main.player[Main.myPlayer].hairColor while building
        // the native recipe table. This isolated non-server fixture has not created that slot yet.
        int initialSlot = Main.myPlayer;
        var initialPlayer = Main.player[initialSlot];
        Main.player[initialSlot] ??= new Player { whoAmI = initialSlot };
        try
        {
            // RecipeGroup.SortDecraftingEntries reads sample item values. Create its required
            // samples with the actual SetDefaults method; no recipe or group is fabricated.
            foreach (int type in RecipeGroup.recipeGroups.Values.SelectMany(x => x.Items).Distinct())
            {
                var item = new Item(); item.SetDefaults(type); ContentSamples.ItemsByType[type] = item;
            }
            Recipe.SetupRecipes();
        }
        finally { Main.player[initialSlot] = initialPlayer; }
        razor = Main.recipe.Take(Recipe.numRecipes).Single(x => x.createItem.type == 5334);
    }

    private void PreserveStatics(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (field.IsLiteral) continue;
            object? value = field.GetValue(null);
            if (value is Array array)
            {
                var contents = (Array)array.Clone();
                restore.Add(() => { Array.Copy(contents, array, array.Length); if (!field.IsInitOnly) field.SetValue(null, array); });
            }
            else if (value is IDictionary dictionary)
            {
                var contents = dictionary.Keys.Cast<object>().Select(key => new DictionaryEntry(key, dictionary[key])).ToArray();
                restore.Add(() => { dictionary.Clear(); foreach (var entry in contents) dictionary.Add(entry.Key, entry.Value); if (!field.IsInitOnly) field.SetValue(null, value); });
            }
            else if (!field.IsInitOnly) restore.Add(() => field.SetValue(null, value));
        }
    }

    [OneTimeTearDown]
    public void RestoreRecipeState()
    {
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        Main.recipe = oldRecipes;
    }

    [SetUp]
    public void SetupWorld()
    {
        oldRemix = Main.remixWorld; oldWorthy = Main.getGoodWorld; oldZenith = Main.zenithWorld; oldHard = Main.hardMode;
        oldNet = Main.netMode; oldLocal = Main.myPlayer; oldPlayer = Main.player[12];
        Main.remixWorld = Main.getGoodWorld = Main.zenithWorld = Main.hardMode = false;
        Main.netMode = 1; Main.myPlayer = 12;
        Main.player[12] = new Player { whoAmI = 12, active = true, chest = -1 };
        Main.player[12].adjTile[134] = true;
    }

    [TearDown]
    public void RestoreWorld()
    {
        Main.remixWorld = oldRemix; Main.getGoodWorld = oldWorthy; Main.zenithWorld = oldZenith; Main.hardMode = oldHard;
        Main.netMode = oldNet; Main.myPlayer = oldLocal; Main.player[12] = oldPlayer;
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, true)]
    public void ActualRecipeTableAndNativeEnvironmentGateUseTheCombinedFeature(bool remix, bool worthy, bool allowed)
    {
        Main.remixWorld = remix; Main.getGoodWorld = worthy;
        var producer = new M6RazorRecipeContexts(TargetRuntime.Fingerprint); producer.Refresh();
        var snapshot = producer.Capture(true);
        Assert.That(snapshot.RecipeTableVerified, Is.True);
        Assert.That(snapshot.Ingredients, Is.EqualTo(new[] { (544, 1), (557, 1), (556, 1) }));
        Assert.That(snapshot.RequiredTile, Is.EqualTo(134));
        Assert.That(snapshot.NativeWorldPermitsRecipe, Is.EqualTo(allowed));
        Assert.That(razor.PlayerMeetsEnvironmentConditions(Main.player[12]), Is.EqualTo(allowed));
        Assert.That(snapshot.SourceWorldProhibition, Is.EqualTo(Truth.True), "Preserved source !zenith differs from this target's additional legal combination.");
        Assert.That(snapshot.SourceHardModeProhibition, Is.EqualTo(Truth.Unknown), "No authoritative currentbossdefeated value was observed.");
        Assert.That(snapshot.AcquisitionPathsComplete, Is.False);
    }

    [Test]
    public void RealLocalCraftWithImportedMaterialsSucceedsBeforeHardModeInCombinedWorld()
    {
        Main.remixWorld = Main.getGoodWorld = true;
        var player = Main.player[12];
        for (int i = 0; i < 3; i++) player.inventory[i].SetDefaults(razor.requiredItem[i].type);
        Recipe._ownedItems.Clear(); Recipe.CollectItems(player.inventory, 58);
        Assert.That(razor.PlayerMeetsEnvironmentConditions(player), Is.True);
        Assert.That(Recipe.CollectedEnoughItemsToCraft(razor), Is.True);
        List<Item> results = [];
        void Sink(object? _, HookEvents.Terraria.Main.CraftItem_GrantItemEventArgs args)
        { results.Add(args.result); args.ContinueExecution = false; }
        HookEvents.Terraria.Main.CraftItem_GrantItem += Sink;
        try
        {
            List<Recipe.RequiredItemEntry> ingredients = []; razor.GetIngredientsForOneCraft(player, ingredients);
            CraftingRequests.CraftLocally(razor, false, [], ingredients);
            Assert.That(player.inventory.Take(3).All(x => x.IsAir), Is.True);
            Assert.That(results.Single().type, Is.EqualTo(5334)); Assert.That(results.Single().stack, Is.EqualTo(1));
            Assert.That(Main.hardMode, Is.False);
        }
        finally { HookEvents.Terraria.Main.CraftItem_GrantItem -= Sink; }
        // Grant sink is a method-test boundary. Imported ingredients are an explicit legal-history
        // fixture; this does not claim a GUI craft or a closed fresh-SSC acquisition universe.
    }

    [Test]
    public void ChangedRecipeUnknownPluginAndWorldChangesCannotAttestAcquisition()
    {
        var producer = new M6RazorRecipeContexts(TargetRuntime.Fingerprint); producer.Refresh();
        Assert.That(producer.Capture(false).RecipeTableVerified, Is.False);
        int old = razor.requiredItem[0].stack;
        try { razor.requiredItem[0].stack = 2; Assert.That(producer.Capture(true).RecipeTableVerified, Is.False); }
        finally { razor.requiredItem[0].stack = old; }
        Main.remixWorld = Main.getGoodWorld = true;
        Assert.That(producer.Capture(true).NativeWorldPermitsRecipe, Is.True, "Current world conditions are read at capture, not aged by two ticks.");
        Main.hardMode = true;
        Assert.That(producer.Capture(true).SourceHardModeProhibition, Is.EqualTo(Truth.False));
        Assert.That(producer.Capture(true).AcquisitionPathsComplete, Is.False);
    }

    [Test]
    public void SelectedItemDataKeepsNaturalAndPolicyConditionsAndSameEventExceptionSeparate()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "START_HERE.md"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null);
        var catalog = ProgressionCatalog.Load(Path.Combine(directory!.FullName, "data/progression/m6-razor-candidates.json"));
        Assert.That(catalog.Rules.Select(x => x.Classification), Is.EqualTo(new[] { "natural_impossibility_candidate", "server_policy" }));
        var facts = new Dictionary<string, JsonElement>
        {
            ["Main.zenithWorld"] = JsonSerializer.SerializeToElement(false),
            ["Main.hardMode"] = JsonSerializer.SerializeToElement(false),
            ["policy.mklpSubsetEnabled"] = JsonSerializer.SerializeToElement(true),
            ["currentbossdefeated"] = JsonSerializer.SerializeToElement("BossDType.WallOfFlesh"),
            ["exception.nativeMechdusaRecipeAvailable"] = JsonSerializer.SerializeToElement(true)
        };
        Assert.That(ProgressionEvaluator.Assess(catalog.Rules[0], 5334, facts).Decision, Is.EqualTo(CandidateDecision.Pass));
        Assert.That(ProgressionEvaluator.Evaluate(catalog.Rules[1].Condition, facts), Is.EqualTo(Truth.False));
        facts.Remove("currentbossdefeated");
        Assert.That(ProgressionEvaluator.Evaluate(catalog.Rules[1].Condition, facts), Is.EqualTo(Truth.Unknown));
        facts["policy.mklpSubsetEnabled"] = JsonSerializer.SerializeToElement(false);
        Assert.That(ProgressionEvaluator.Evaluate(catalog.Rules[1].Condition, facts), Is.EqualTo(Truth.False));
    }
}
