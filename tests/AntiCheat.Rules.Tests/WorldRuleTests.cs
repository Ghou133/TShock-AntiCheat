using System.Collections.Immutable;
using System.Security.Cryptography;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class WorldRuleTests
{
    private static readonly SessionKey Key = new(Guid.Parse("70BFF52D-C8B4-4960-BE1A-2E960242D30C"), 1, 4, 1);
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    [TestCase(WorldActionKind.TileEdit)]
    [TestCase(WorldActionKind.LiquidSet)]
    [TestCase(WorldActionKind.SignWrite)]
    [TestCase(WorldActionKind.ContainerOpen)]
    [TestCase(WorldActionKind.ObjectPlacement)]
    [TestCase(WorldActionKind.DisplayEntityInteraction)]
    public void AuthorizedNativeWorldRequestsPass(WorldActionKind kind)
        => Assert.That(WorldRules.Evaluate(Valid(kind)).Action, Is.EqualTo(ControlAction.Pass));

    [TestCase(-1, 10)]
    [TestCase(100, 10)]
    [TestCase(10, -1)]
    [TestCase(10, 100)]
    public void CoordinatesAreCheckedBeforeAnyTileLookup(int x, int y)
    {
        var result = WorldRules.Evaluate(Valid(WorldActionKind.TileEdit) with { X = x, Y = y });
        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(result.PrerequisitesComplete, Is.False);
        });
    }

    [TestCase(1, 800)]
    [TestCase(21, 800)]
    [TestCase(3, 400)]
    [TestCase(22, 400)]
    [TestCase(24, 0)]
    [TestCase(1, -1)]
    public void TileActionAndRuntimeTypeBoundsAreSafetyOnly(int action, int data)
    {
        var result = WorldRules.Evaluate(Valid(WorldActionKind.TileEdit) with { EditAction = action, EditData = data });
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
    }

    [TestCase(20, 0)]
    [TestCase(23, 4)]
    [TestCase(21, 799)]
    [TestCase(22, 399)]
    public void CurrentTileActionsAndLastValidTypesRemainAccepted(int action, int data)
        => Assert.That(WorldRules.Evaluate(Valid(WorldActionKind.TileEdit) with { EditAction = action, EditData = data }).Action, Is.EqualTo(ControlAction.Pass));

    [TestCase(255, 3)]
    [TestCase(0, 0)]
    [TestCase(0, 255)]
    public void ShimmerAndEmptyLiquidRemovalAreLegal(int amount, int type)
        => Assert.That(WorldRules.Evaluate(Valid(WorldActionKind.LiquidSet) with { LiquidAmount = amount, LiquidType = type }).Action, Is.EqualTo(ControlAction.Pass));

    [TestCase(256, 0)]
    [TestCase(-1, 0)]
    [TestCase(1, 4)]
    [TestCase(1, 255)]
    public void InvalidLiquidDomainNeverBecomesAccountCheating(int amount, int type)
    {
        var result = WorldRules.Evaluate(Valid(WorldActionKind.LiquidSet) with { LiquidAmount = amount, LiquidType = type });
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.PrerequisitesComplete, Is.False);
    }

    [Test]
    public void CompletePermissionDenialBlocksWithoutInferringCheating()
    {
        var observation = Valid(WorldActionKind.TileEdit);
        var result = WorldRules.Evaluate(observation with { Authorization = observation.Authorization! with { Allowed = false } });
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.PredicateSatisfied, Is.False);
    }

    [Test]
    public void MissingPermissionAndUnmatchedEventOrFootprintRemainUnknown()
    {
        var observation = Valid(WorldActionKind.ObjectPlacement);
        var authority = observation.Authorization!;
        foreach (var invalid in new[]
        {
            authority with { EventId = Guid.NewGuid() }, authority with { Session = Key with { Generation = 2 } },
            authority with { X = 11 }, authority with { TargetId = 9 }, authority with { Complete = false },
            authority with { FootprintComplete = false }, authority with { Allowed = null }
        }) Assert.That(WorldRules.Evaluate(observation with { Authorization = invalid }).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void OldWorldAndRuntimeUnknownCannotBorrowGeometry()
    {
        var observation = Valid(WorldActionKind.TileEdit) with { X = -100 };
        foreach (var geometry in new[]
        {
            observation.Geometry! with { WorldEpoch = 2 }, observation.Geometry! with { RuntimeFingerprint = "other" },
            observation.Geometry! with { Complete = false }
        }) Assert.That(WorldRules.Evaluate(observation with { Geometry = geometry }).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void UnrelatedMissingTablesDoNotDisableIndependentWorldSafety()
    {
        var tile = Valid(WorldActionKind.TileEdit);
        tile = tile with { Geometry = tile.Geometry! with { SignCapacity = 0, WallTypeCount = 0 } };
        Assert.That(WorldRules.Evaluate(tile).Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(WorldRules.Evaluate(tile with { X = -1 }).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        var liquid = Valid(WorldActionKind.LiquidSet);
        liquid = liquid with { Geometry = liquid.Geometry! with { TileTypeCount = 0, WallTypeCount = 0, SignCapacity = 0 } };
        Assert.That(WorldRules.Evaluate(liquid).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void SignBoundsTargetBindingAndChestRapidSwitchAreDistinct()
    {
        var sign = Valid(WorldActionKind.SignWrite);
        Assert.That(WorldRules.Evaluate(sign with { TargetId = 1000 }).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(WorldRules.Evaluate(sign with { TargetBindingValid = false }).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(WorldRules.Evaluate(sign with { TargetBindingValid = null }).Action, Is.EqualTo(ControlAction.Unknown));
        var first = Valid(WorldActionKind.ContainerOpen);
        var second = first with { EventId = Guid.NewGuid(), TargetId = 2, X = 12 };
        second = second with { Authorization = first.Authorization! with { EventId = second.EventId, X = second.X, TargetId = second.TargetId } };
        Assert.That(WorldRules.Evaluate(first).Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(WorldRules.Evaluate(second).Action, Is.EqualTo(ControlAction.Pass), "No invented previous-chest cooldown or forced close step.");
    }

    [Test]
    public void ResourceBudgetAndCoreCancellationDoNotProduceCheatingEvidence()
    {
        var result = WorldRules.Evaluate(Valid(WorldActionKind.ObjectPlacement) with { BudgetAllowed = false });
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        var cancelled = WorldRules.Evaluate(Valid(WorldActionKind.TileEdit) with { AlreadyCancelled = true });
        Assert.That(cancelled.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(cancelled.Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void EnvironmentalChangeIsNotAttributedToTheNearestPlayer()
        => Assert.That(WorldRules.Evaluate(Valid(WorldActionKind.EnvironmentalChange)).Action, Is.EqualTo(ControlAction.Unknown));

    [Test]
    public void CausalSimpleTileCanBePlannedButAnyLaterRevisionPreventsRestoration()
    {
        var (evidence, current) = Rollback();
        var valid = LimitedTileRollback.Plan(evidence, current, Key, Now);
        Assert.Multiple(() =>
        {
            Assert.That(valid.CanRestore, Is.True);
            Assert.That(valid.Plan!.RestoreState, Is.EqualTo(evidence.BeforeState));
            Assert.That(valid.Plan.ExpectedCurrent, Is.EqualTo(current));
        });
        Assert.That(LimitedTileRollback.Plan(evidence, current with { Revision = 3 }, Key, Now).CanRestore, Is.False);
        Assert.That(LimitedTileRollback.Plan(evidence, current with { StateHash = Hash([9]) }, Key, Now).CanRestore, Is.False);
        Assert.That(LimitedTileRollback.Plan(evidence, current with { WorldEpoch = 2 }, Key, Now).CanRestore, Is.False);
    }

    [Test]
    public void RollbackRejectsItemContainersEntityChangesAmbiguousCauseAndExpiredEvidence()
    {
        var (evidence, current) = Rollback();
        foreach (var invalid in new[]
        {
            evidence with { CauseExclusive = false }, evidence with { SimpleTileOnly = false },
            evidence with { NoItemOrEntitySideEffects = false }, evidence with { CauseSession = Key with { Generation = 2 } },
            evidence with { ChangedUtc = Now - TimeSpan.FromSeconds(3) }, evidence with { BeforeState = [9, 8, 7] },
            evidence with { BeforeState = Enumerable.Repeat((byte)0, 513).ToImmutableArray() },
            evidence with { BeforeRevision = 0 }, evidence with { IncidentId = Guid.Empty }
        }) Assert.That(LimitedTileRollback.Plan(invalid, current, Key, Now).CanRestore, Is.False);
        var overflow = evidence with { BeforeRevision = long.MaxValue, AfterRevision = long.MinValue };
        Assert.That(LimitedTileRollback.Plan(overflow, current with { Revision = long.MinValue }, Key, Now).CanRestore, Is.False);
    }

    private static WorldActionObservation Valid(WorldActionKind kind)
    {
        var eventId = Guid.NewGuid();
        return new(Key, kind, 10, 10)
        {
            CurrentSession = Key, EventId = eventId, RuntimeFingerprint = "fixture/1.4.5.8", ParseComplete = true,
            ClientOrigin = true, BeforeSideEffects = true, EditAction = 1, EditData = 1, LiquidAmount = 255,
            LiquidType = 0, TargetId = 1, TargetBindingValid = true,
            Geometry = new(1, "fixture/1.4.5.8", "geometry-v1", 100, 100, 800, 400, 1000, true),
            Authorization = new(Key, eventId, kind, 10, 10, 1, 0, true, true, true, true)
        };
    }

    private static (TileRollbackEvidence, CurrentTileVersion) Rollback()
    {
        ImmutableArray<byte> before = [1, 2, 3];
        var afterHash = Hash([4, 5, 6]);
        return (new(Guid.NewGuid(), Key, 10, 10, 1, 2, Hash(before.AsSpan()), afterHash, before, Now,
            true, true, true, "fixture/1.4.5.8"), new(1, 10, 10, 2, afterHash, "fixture/1.4.5.8"));
    }
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
}
