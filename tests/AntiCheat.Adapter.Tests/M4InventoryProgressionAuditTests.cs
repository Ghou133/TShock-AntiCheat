using System.Collections.Immutable;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Progression;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M4InventoryProgressionAuditTests
{
    [Test]
    public void TargetCarrotHasActualCollectorsEditionNativeUseAndCannotBeUnconditionallyUnobtainable()
    {
        Assert.That(ItemID.Carrot, Is.EqualTo(603));
        int oldLocal = Main.myPlayer, oldMode = Main.netMode;
        bool oldCollectors = Main.runningCollectorsEdition;
        try
        {
            Main.netMode = 0; Main.myPlayer = 12;
            var player = new Player { whoAmI = 12 };
            var carrot = new Item(); carrot.SetDefaults(ItemID.Carrot);
            Assert.That(carrot.buffType, Is.EqualTo(BuffID.PetBunny));
            Main.runningCollectorsEdition = false;
            player.ItemCheck_ApplyPetBuffs(carrot);
            Assert.That(player.FindBuffIndex(carrot.buffType), Is.EqualTo(-1));
            Main.runningCollectorsEdition = true;
            player.ItemCheck_ApplyPetBuffs(carrot);
            Assert.That(player.FindBuffIndex(carrot.buffType), Is.GreaterThanOrEqualTo(0));
            // The fixture toggles the actual in-process condition. It does not claim a DLC/registry entitlement
            // was created, nor that the server can attest the client's entitlement from a normal packet.
        }
        finally { Main.myPlayer = oldLocal; Main.netMode = oldMode; Main.runningCollectorsEdition = oldCollectors; }
    }

    [Test]
    public void TargetBoneBlockDefinitionDoesNotProveOriginOfPassiveOrImportedItem()
    {
        Assert.That(ItemID.BoneBlock, Is.EqualTo(766));
        var bone = new Item(); bone.SetDefaults(ItemID.BoneBlock);
        Assert.That(bone.createTile, Is.EqualTo(194));
        var rule = Catalog().Rules.Single(x => x.Id == "PG-NAT-001");
        Assert.That(rule.Items, Does.Contain(ItemID.BoneBlock));
        var observed = Observation(ItemID.BoneBlock);
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observed with { Action = ProgressionActionKind.PassiveReceipt }).Action,
            Is.EqualTo(ControlAction.Pass));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observed with { Action = ProgressionActionKind.InventorySync }).Action,
            Is.EqualTo(ControlAction.Pass));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observed).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase("Main.drunkWorld")]
    [TestCase("Main.getGoodWorld")]
    [TestCase("Main.zenithWorld")]
    [TestCase("Main.tenthAnniversaryWorld")]
    public void RedPotionSourcePredicateRetainsEachExactSeedException(string specialSeed)
    {
        Assert.That(ItemID.RedPotion, Is.EqualTo(678));
        var rule = Catalog().Rules.Single(x => x.Id == "PG-NAT-002");
        var observation = Observation(ItemID.RedPotion);
        var facts = observation.World!.Facts.SetItem(specialSeed, JsonSerializer.SerializeToElement(true));
        var result = ProgressionBusinessRules.Evaluate(rule, observation with { World = observation.World with { Facts = facts } });
        Assert.That(result.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(result.Reason, Is.EqualTo("progress-condition-false"));
    }

    [TestCase("exception.legacyOrImportedAsset")]
    [TestCase("exception.authorizedServerGrant")]
    [TestCase("exception.passiveReceipt")]
    [TestCase("exception.otherPlayerCausedState")]
    public void OrdinaryWorldDoesNotTurnPreviouslyLegalRedPotionIntoCurrentActorProof(string legalException)
    {
        var rule = Catalog().Rules.Single(x => x.Id == "PG-NAT-002");
        var observation = Observation(ItemID.RedPotion);
        var facts = observation.World!.Facts.SetItem(legalException, JsonSerializer.SerializeToElement(true));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observation with { World = observation.World with { Facts = facts } }).Action,
            Is.EqualTo(ControlAction.Pass));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observation).Verdict, Is.EqualTo(Verdict.Unknown),
            "Even closed world flags and manually false exception fields do not create missing acquisition evidence.");
    }

    private static ProgressionCatalog Catalog() => ProgressionCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "progression", "candidates.json"));

    private static ProgressionActionObservation Observation(int itemId)
    {
        var session = new SessionKey(Guid.NewGuid(), 2, 12, 1);
        var now = DateTimeOffset.UtcNow;
        var facts = new[] { "Main.drunkWorld", "Main.getGoodWorld", "Main.zenithWorld", "Main.tenthAnniversaryWorld",
            "exception.sourceWhitelistMatchesItem", "exception.authorizedServerGrant", "exception.legacyOrImportedAsset",
            "exception.passiveReceipt", "exception.otherPlayerCausedState" }
            .ToImmutableDictionary(x => x, _ => JsonSerializer.SerializeToElement(false));
        return new(session, itemId, ProgressionActionKind.UseItem)
        {
            CurrentSession = session, RuntimeFingerprint = "target326-candidate-audit", TerrariaVersion = "1.4.5.8",
            NowUtc = now, ParseComplete = true, Authenticated = true, ClientOrigin = true,
            ActiveActionAttributed = true, BeforeSideEffects = true,
            World = new(session.WorldEpoch, "target326-candidate-audit", "audit1", "ordinary-seed", now, now.AddSeconds(1), true, true, facts)
            // No Mechanism is fabricated. These are candidate-evaluator counterexamples, not live acquisition producers.
        };
    }
}
