using System.Text.Json;
using Microsoft.Data.Sqlite;

// This is an extension of the existing isolated NetworkLab runner. It sends only
// protocol-326 packet28 frames over loopback and observes the native NPC state and
// the existing server-to-client relay; it is not a second test platform.
internal static class M18NpcStrikeScenario
{
    private const int ExtremeDamage = 9999;
    private const int OrdinaryButcherDamage = 1000;
    private const int LowLifeFixture = 45;
    private const int LowDamageLimit = 64;
    private const int LowDamageSessionLimit = 192;
    private const int NativeTargetType = 50; // King Slime: ordinary hostile native AI with enough life for the bounded prefix.
    private const string ButcherNormalSource = "TerraAngel Tools/Butcher.cs: ButcherNPC -> NetMessage.SendData(28)";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-a");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "", failurePhase = "", failureStack = "";
        bool permanentSanctionCandidate = Environment.GetEnvironmentVariable("ANTICHEAT_M18_PERMANENT_SANCTION_CANDIDATE") == "1";
        string phase = "start";
        var observations = new List<object>();
        var evidenceFailures = new List<string>();
        Exception? primaryError = null;
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            Check(Scalar(Path.Combine(run, "tshock", "tshock.sqlite"), "SELECT COUNT(*) FROM PlayerBans") == 0,
                "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0AObserver");
            await observer.Join();
            await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0AActor");
            await actor.Join();
            await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            // Reuse the existing one-time QA room/NPC preparation and its own
            // ownership records. The combat targets below are created by the
            // existing native TShock /spawnmob path over the authenticated TCP
            // client, not by this scenario or a second fixture platform.
            await host.ConsoleCommand("qa_prepare " + actor.Name);
            var preparedRoom = await host.FixtureSnapshot();
            await host.ConsoleCommand("qa_npcs " + actor.Name);
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            const string spawnGroup = "M18SpawnOnly";
            Check(Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", spawnGroup) == 0,
                "fresh-native-spawn-permission-group");
            bool spawnGroupAssignmentAttempted = false;
            try
            {
                await host.ConsoleCommand("group add " + spawnGroup + " tshock.npc.spawnmob");
                await Until(() => Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", spawnGroup) == 1,
                    "native-spawn-group-created");
                await host.ConsoleCommand("group parent " + spawnGroup + " default");
                await Until(() => Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default' AND Commands='tshock.npc.spawnmob'", spawnGroup) == 1,
                    "native-spawn-group-parented");
                await host.ConsoleCommand("user group " + actor.Name + " " + spawnGroup);
                spawnGroupAssignmentAttempted = true;
                await Until(() => Scalar(database, "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup=$group", actor.Name, spawnGroup) == 1,
                    "actor-temporarily-has-only-native-spawn-permission");
                await actor.Chat("/spawnmob " + NativeTargetType + " 9");
                await actor.PingAsync();
            }
            finally
            {
                if (spawnGroupAssignmentAttempted)
                {
                    await host.ConsoleCommand("user group " + actor.Name + " default");
                    await Until(() => Scalar(database, "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                        "actor-native-spawn-permission-restored");
                }
            }
            var initial = await WaitFixtureSnapshot(snapshot => snapshot.GetProperty("npcs").EnumerateArray()
                .Count(x => x.GetProperty("active").GetBoolean() && x.GetProperty("life").GetInt32() > 0 &&
                x.GetProperty("generation").GetInt32() > 0 && x.GetProperty("type").GetInt32() == NativeTargetType) >= 9,
                "nine-native-king-slime-spawnmob-targets");
            var allTargets = initial.GetProperty("npcs").EnumerateArray()
                .Where(x => x.GetProperty("active").GetBoolean() &&
                    x.GetProperty("life").GetInt32() > 0 &&
                    x.GetProperty("generation").GetInt32() > 0 &&
                    x.GetProperty("type").GetInt32() == NativeTargetType)
                .Select(x => new NpcTarget(
                    x.GetProperty("index").GetInt32(),
                    x.GetProperty("generation").GetInt32(),
                    x.GetProperty("type").GetInt32(),
                    x.GetProperty("life").GetInt32(),
                    x.GetProperty("position").GetProperty("x").GetSingle(),
                    x.GetProperty("position").GetProperty("y").GetSingle(),
                    x.GetProperty("defense").GetInt32(),
                    x.GetProperty("lifeMax").GetInt32()))
                .Take(9)
                .ToArray();
            // Use the first freshly spawned native target for the one-time low-life
            // preparation. The next four are reserved for the bounded resource
            // prefix, while the final two are reserved for the ordinary Butcher
            // sequence. Four additional native targets are reserved as Butcher
            // candidates; the live snapshot immediately before the source
            // sequence selects two that still have enough life for the first
            // ordinary 1000-damage hit to leave a positive target state. Keeping
            // those populations disjoint prevents the expected first
            // Butcher-prefix loss from poisoning the later resource-boundary
            // proof.
            var lowLifeTarget = allTargets.ElementAtOrDefault(0);
            var targets = allTargets.Skip(1).Take(4).ToArray();
            var butcherTargets = allTargets.Skip(5).Take(4).ToArray();
            Check(targets.Length == 4, "four-live-native-qa-npc-targets");
            Check(butcherTargets.Length == 4, "four-live-native-butcher-qa-npc-candidates");
            Check(lowLifeTarget is not null, "fifth-live-native-low-life-qa-npc-target");
            var lowLife = lowLifeTarget!;
            Check(targets.All(x => x.Index is >= 0 and < 200 && x.Generation is > 0 and <= byte.MaxValue),
                "target-slot-and-generation-bounds");
            Check(targets.All(x => x.Type == NativeTargetType), "targets-are-native-king-slimes-not-town-npc-fixtures");
            Check(lowLife.Index is >= 0 and < 200 && lowLife.Generation is > 0 and <= byte.MaxValue &&
                lowLife.Type == NativeTargetType, "low-life-target-is-native-king-slime");

            // The locked Butcher source scans the whole live Main.npc array with
            // an exact active/non-friendly/non-TargetDummy predicate. Capture that
            // server state separately; this is an attribution/selection witness,
            // not a client GUI invocation and not an NPC strike producer.
            string enumerationMarker = Path.Combine(run, "qa-m18-butcher-enumeration-" + actor.Slot + ".json");
            await host.ConsoleCommand("qa_m18_butcher_enum " + actor.Name);
            await Until(() => File.Exists(enumerationMarker) && host.ConsoleLines().Any(x =>
                x.Contains("M18 Butcher hostile enumeration recorded", StringComparison.Ordinal)),
                "butcher-hostile-enumeration-recorded");
            using var enumerationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(enumerationMarker));
            var enumeration = enumerationDocument.RootElement.Clone();
            Check(enumeration.GetProperty("fixtureArtificial").GetBoolean() == false,
                "butcher-enumeration-is-live-server-observation");
            Check(enumeration.GetProperty("scannedSlots").GetInt32() >= 200,
                "butcher-enumeration-scans-full-live-npc-array");
            Check(enumeration.GetProperty("sourcePredicate").GetString() ==
                "Main.npc[i].active && !Main.npc[i].friendly && Main.npc[i].type != NPCID.TargetDummy",
                "butcher-enumeration-retains-exact-source-predicate");
            var selectedTypes = enumeration.GetProperty("selected").EnumerateArray()
                .Select(x => x.GetProperty("type").GetInt32()).ToArray();
            Check(selectedTypes.Count(type => type == NativeTargetType) >= 9,
                "butcher-enumeration-includes-all-nine-live-hostile-king-slimes");
            Check(enumeration.GetProperty("mutation").GetString()!.StartsWith("none;", StringComparison.Ordinal),
                "butcher-enumeration-has-no-server-mutation");
            observations.Add(new
            {
                kind = "butcher-all-hostile-enumeration-observed",
                sourceContract = "TerraAngel Tools/Butcher.cs: ButcherAllHostileNPCs",
                enumeration,
                boundary = "server-side read-only predicate witness; no local Butcher GUI call, packet producer or complete creator proof"
            });
            observations.Add(new
            {
                kind = "native-hostile-npc-preparation",
                command = "/spawnmob " + NativeTargetType + " 9",
                permission = "tshock.npc.spawnmob",
                permissionGroup = spawnGroup,
                restoredBeforeStrike = Scalar(database, "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                targets,
                lowLife,
                butcherTargets
            });
            await host.ConsoleCommand("qa_capture 28");
            await Until(() => host.ConsoleLines().Any(x => x.Contains("QA raw packet capture: 28", StringComparison.Ordinal)),
                "existing-passive-raw-capture-enabled");
            await observer.Drain(TimeSpan.FromMilliseconds(250));

            // The ordinary TerraAngel Butcher path uses its default damage of
            // 1000, sends one packet28 for a low-life target, and surrounds it
            // with packet13 position updates. Prepare a fifth native target at
            // positive life=45 exactly once. There is no keep-alive or native
            // strike in the fixture, so an accepted request may really kill it.
            string lowLifeMarker = Path.Combine(run, "qa-m18-low-life-" + actor.Slot + "-" + lowLife.Index + ".json");
            await host.ConsoleCommand("qa_m18_low_life " + actor.Name + " " + lowLife.Index + " " + LowLifeFixture);
            await Until(() => File.Exists(lowLifeMarker) && host.ConsoleLines().Any(x =>
                x.Contains("M18 low-life native target prepared", StringComparison.Ordinal)),
                "low-life-native-target-prepared");
            using var lowLifeDocument = JsonDocument.Parse(await File.ReadAllTextAsync(lowLifeMarker));
            var lowLifeSetup = lowLifeDocument.RootElement.Clone();
            float lowLifeX = lowLifeSetup.GetProperty("after").GetProperty("position").GetProperty("x").GetSingle();
            float lowLifeY = lowLifeSetup.GetProperty("after").GetProperty("position").GetProperty("y").GetSingle();
            var lowLifeBefore = await WaitFixtureSnapshot(snapshot =>
            {
                var state = ReadNpc(snapshot, lowLife.Index);
                // With the two additional native Butcher targets present, the
                // ordinary NPC AI may apply a small amount of ambient contact
                // damage while the fixture marker is being observed. Keep the
                // low-life boundary positive and bounded; do not wait for an
                // exact mutable life value or turn this fixture into a keep-alive.
                return state.Active && state.Generation == lowLife.Generation &&
                    state.Life > 0 && state.Life <= LowLifeFixture;
            }, "low-life-native-target-visible-before-butcher");
            var lowLifeBeforeTarget = ReadNpc(lowLifeBefore, lowLife.Index);
            int lowLifeRawBefore = CountRawFrames(host.ReportDirectory, 28, actor.Slot);
            int lowLifeRelayBefore = CountRelays(actor, lowLife, OrdinaryButcherDamage);
            int lowLifeNativeBefore = NativeClientStrikeCount(lowLifeBefore, lowLife.Index,
                lowLife.Generation, actor.Slot);
            int lowLifeLogStart = host.ConsoleLines().Length;
            // Source-equivalent order: declare the target position, send the
            // ordinary packet28 hit, then restore the actor's saved local
            // position. The old fixture sent the target position twice and
            // therefore could not witness the actual before/after semantics.
            await actor.MoveTo(lowLifeX, lowLifeY);
            await actor.Send(28, writer => WriteStrike(writer, lowLife, OrdinaryButcherDamage));
            await actor.MoveTo(actor.InitialSpawnPosition.X, actor.InitialSpawnPosition.Y);
            await actor.PingAsync();
            await Task.Delay(350);
            await actor.Drain(TimeSpan.FromMilliseconds(150));
            var lowLifeAfter = await host.FixtureSnapshot();
            var lowLifeAfterTarget = ReadNpc(lowLifeAfter, lowLife.Index);
            var lowLifeConsole = host.ConsoleLines().Skip(lowLifeLogStart).ToArray();
            int lowLifeRawAfter = CountRawFrames(host.ReportDirectory, 28, actor.Slot);
            int lowLifeRelayAfter = CountRelays(actor, lowLife, OrdinaryButcherDamage);
            int lowLifeNativeAfter = NativeClientStrikeCount(lowLifeAfter, lowLife.Index,
                lowLife.Generation, actor.Slot);
            string? lowLifeRule = lowLifeConsole.FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F06.NpcStrikeDensityBudget", StringComparison.Ordinal) &&
                x.Contains("packet=28", StringComparison.Ordinal));
            bool lowLifeInputReached = lowLifeRawAfter > lowLifeRawBefore;
            bool lowLifeNativeEntry = lowLifeNativeAfter > lowLifeNativeBefore;
            bool lowLifeRelayObserved = lowLifeRelayAfter > lowLifeRelayBefore;
            bool lowLifeChanged = !lowLifeAfterTarget.Active || lowLifeAfterTarget.Life < lowLifeBeforeTarget.Life;
            bool lowLifeBlockedBeforeDamage = lowLifeInputReached && lowLifeRule is not null &&
                lowLifeRule.Contains("action=Block", StringComparison.Ordinal) &&
                lowLifeRule.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                !lowLifeNativeEntry && !lowLifeChanged;
            Check(lowLifeInputReached, "ordinary-butcher-low-life-packet28-reaches-raw-hook");
            Check(lowLifeRule is not null, "ordinary-butcher-low-life-has-final-f06-reason");
            Check(lowLifeBlockedBeforeDamage || lowLifeNativeEntry || lowLifeRelayObserved || lowLifeChanged,
                "ordinary-butcher-low-life-outcome-is-observable-before-classification");
            observations.Add(new
            {
                kind = "butcher-normal-packet28-low-life-real-target",
                sourceContract = ButcherNormalSource,
                sourceParameters = new
                {
                    defaultDamage = OrdinaryButcherDamage,
                    lowLife = LowLifeFixture,
                    sourceFormula = "ceil((npc.life + ceil(npc.defense/2)) / damage) -> one packet28 when the result is one",
                    packetSequence = new[] { 13, 28, 13 },
                    guiExecuted = false,
                    executionTier = "authenticated loopback TCP source-equivalent; TerraAngel GUI unavailable in this run"
                },
                setup = lowLifeSetup,
                target = lowLife,
                before = lowLifeBeforeTarget,
                after = lowLifeAfterTarget,
                rawPacket28Delta = lowLifeRawAfter - lowLifeRawBefore,
                finalRule = lowLifeRule,
                nativeStrikeEntryDelta = lowLifeNativeAfter - lowLifeNativeBefore,
                relayDelta = lowLifeRelayAfter - lowLifeRelayBefore,
                nativeLifeChanged = lowLifeChanged,
                effectiveInterceptionBeforeDamage = lowLifeBlockedBeforeDamage,
                interpretation = lowLifeBlockedBeforeDamage
                    ? "F06 canceled the ordinary packet28 before native StrikeNPC and no target loss was observed."
                    : "The bounded F06 path did not prove a pre-damage stop for ordinary 1000 damage; native/relay/life outcome is retained and must not be reported as an AntiCheat block."
            });

            // The actual ordinary Butcher loop is a finite source sequence:
            // packet13(target position), the source-computed repeated packet28
            // prefix, then packet13(Main.LocalPlayer.position). Use the other
            // authenticated observer so the earlier low-life baseline and its
            // consumed actor budgets remain independent evidence.
            observer.PauseHeartbeat = true;
            await observer.MoveTo(observer.InitialSpawnPosition.X, observer.InitialSpawnPosition.Y);
            await observer.PingAsync();
            var butcherBefore = await host.FixtureSnapshot();
            var butcherCandidates = butcherTargets
                .Select(candidate => ReadLiveNpcTarget(butcherBefore, candidate.Index))
                .Where(candidate => candidate.Type == NativeTargetType && candidate.Generation > 0 &&
                    candidate.InitialLife > OrdinaryButcherDamage + 990)
                .Take(2)
                .ToArray();
            Check(butcherCandidates.Length == 2,
                "ordinary-butcher-has-two-positive-life-candidates-after-native-ai");
            var butcherFirst = butcherCandidates[0];
            var butcherSecond = butcherCandidates[1];
            Check(butcherFirst.Type == NativeTargetType && butcherFirst.Generation > 0 &&
                butcherFirst.InitialLife > OrdinaryButcherDamage,
                "ordinary-butcher-first-target-has-positive-life-for-source-computed-prefix");
            Check(butcherSecond.Type == NativeTargetType && butcherSecond.Generation > 0 &&
                butcherSecond.InitialLife > OrdinaryButcherDamage,
                "ordinary-butcher-second-target-has-positive-life-for-source-computed-prefix");

            int butcherFirstHitCount = SourceHitCount(butcherFirst, OrdinaryButcherDamage);
            int butcherSecondHitCount = SourceHitCount(butcherSecond, OrdinaryButcherDamage);
            Check(butcherFirstHitCount is >= 2 and <= 8,
                "ordinary-butcher-first-prefix-is-finite-and-repeated");
            Check(butcherSecondHitCount is >= 1 and <= 8,
                "ordinary-butcher-second-prefix-is-finite");
            float butcherReturnX = observer.InitialSpawnPosition.X;
            float butcherReturnY = observer.InitialSpawnPosition.Y;
            int butcherFirstRawBefore = CountRawFrames(host.ReportDirectory, 28, observer.Slot);
            int butcherFirstRelayBefore = CountRelays(actor, butcherFirst, OrdinaryButcherDamage);
            int butcherFirstNativeBefore = NativeClientStrikeCount(butcherBefore, butcherFirst.Index,
                butcherFirst.Generation, observer.Slot);
            int butcherFirstLogStart = host.ConsoleLines().Length;
            var butcherFirstFrames = new List<byte[]> { PlayerControlsFrame(observer.Slot, butcherFirst.X, butcherFirst.Y) };
            butcherFirstFrames.AddRange(Enumerable.Range(0, butcherFirstHitCount)
                .Select(_ => LabClient.Packet(28, writer => WriteStrike(writer, butcherFirst, OrdinaryButcherDamage))));
            butcherFirstFrames.Add(PlayerControlsFrame(observer.Slot, butcherReturnX, butcherReturnY));
            await observer.SendBatch(butcherFirstFrames.ToArray());
            await observer.PingAsync();
            await Task.Delay(350);
            await actor.Drain(TimeSpan.FromMilliseconds(150));
            var butcherFirstAfter = await host.FixtureSnapshot();
            var butcherFirstAfterTarget = ReadNpc(butcherFirstAfter, butcherFirst.Index);
            var butcherFirstConsole = host.ConsoleLines().Skip(butcherFirstLogStart).ToArray();
            string? butcherFirstRule = butcherFirstConsole.FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F06.NpcStrikeDensityBudget", StringComparison.Ordinal) &&
                x.Contains("reason=npc-strike-ordinary-butcher-sequence-preforward-stop", StringComparison.Ordinal));
            int butcherFirstRawAfter = CountRawFrames(host.ReportDirectory, 28, observer.Slot);
            int butcherFirstRelayAfter = CountRelays(actor, butcherFirst, OrdinaryButcherDamage);
            int butcherFirstNativeAfter = NativeClientStrikeCount(butcherFirstAfter, butcherFirst.Index,
                butcherFirst.Generation, observer.Slot);
            int butcherFirstRelayDelta = butcherFirstRelayAfter - butcherFirstRelayBefore;
            int butcherFirstNativeDelta = butcherFirstNativeAfter - butcherFirstNativeBefore;
            bool butcherFirstLoss = !butcherFirstAfterTarget.Active ||
                butcherFirstAfterTarget.Life < butcherFirst.InitialLife;
            Check(butcherFirstRawAfter - butcherFirstRawBefore == butcherFirstHitCount,
                "ordinary-butcher-first-source-prefix-reaches-raw-packet28-hook");
            Check(butcherFirstRule is not null &&
                butcherFirstRule.Contains("action=Block", StringComparison.Ordinal) &&
                butcherFirstRule.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                butcherFirstRule.Contains("alreadyCanceled=False", StringComparison.OrdinalIgnoreCase),
                "ordinary-butcher-first-repeated-hit-is-stopped-before-native");
            Check(butcherFirstRelayDelta == 1 && butcherFirstNativeDelta == 1 && butcherFirstLoss,
                "ordinary-butcher-first-rejection-records-one-prefix-hit-loss-before-stop");

            var butcherSecondBefore = await host.FixtureSnapshot();
            var butcherSecondCurrent = ReadLiveNpcTarget(butcherSecondBefore, butcherTargets[1].Index);
            int butcherSecondRawBefore = CountRawFrames(host.ReportDirectory, 28, observer.Slot);
            int butcherSecondRelayBefore = CountRelays(actor, butcherSecondCurrent, OrdinaryButcherDamage);
            int butcherSecondNativeBefore = NativeClientStrikeCount(butcherSecondBefore, butcherSecondCurrent.Index,
                butcherSecondCurrent.Generation, observer.Slot);
            int butcherSecondLogStart = host.ConsoleLines().Length;
            var butcherSecondFrames = new List<byte[]> { PlayerControlsFrame(observer.Slot,
                butcherSecondCurrent.X, butcherSecondCurrent.Y) };
            butcherSecondFrames.AddRange(Enumerable.Range(0, SourceHitCount(butcherSecondCurrent, OrdinaryButcherDamage))
                .Select(_ => LabClient.Packet(28, writer => WriteStrike(writer, butcherSecondCurrent, OrdinaryButcherDamage))));
            butcherSecondFrames.Add(PlayerControlsFrame(observer.Slot, butcherReturnX, butcherReturnY));
            await observer.SendBatch(butcherSecondFrames.ToArray());
            await observer.PingAsync();
            await Task.Delay(350);
            await actor.Drain(TimeSpan.FromMilliseconds(150));
            // Binding.TryReportBusiness deliberately de-duplicates the same
            // rule/verdict/reason after the first occurrence. Use the bounded
            // structured journal for this second-target witness instead of
            // mistaking a missing duplicate log line for a missing block.
            await Until(() => HasButcherJournalSample(host.RunDirectory, butcherSecondCurrent.Index,
                butcherSecondCurrent.Generation, crossTarget: true),
                "ordinary-butcher-cross-target-journal-record");
            var butcherSecondAfter = await host.FixtureSnapshot();
            var butcherSecondAfterTarget = ReadNpc(butcherSecondAfter, butcherSecondCurrent.Index);
            var butcherSecondConsole = host.ConsoleLines().Skip(butcherSecondLogStart).ToArray();
            string? butcherSecondRule = butcherSecondConsole.FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F06.NpcStrikeDensityBudget", StringComparison.Ordinal) &&
                x.Contains("reason=npc-strike-ordinary-butcher-sequence-preforward-stop", StringComparison.Ordinal));
            int butcherSecondRawAfter = CountRawFrames(host.ReportDirectory, 28, observer.Slot);
            int butcherSecondRelayAfter = CountRelays(actor, butcherSecondCurrent, OrdinaryButcherDamage);
            int butcherSecondNativeAfter = NativeClientStrikeCount(butcherSecondAfter, butcherSecondCurrent.Index,
                butcherSecondCurrent.Generation, observer.Slot);
            Check(butcherSecondRawAfter - butcherSecondRawBefore == SourceHitCount(butcherSecondCurrent, OrdinaryButcherDamage),
                "ordinary-butcher-cross-target-prefix-reaches-raw-packet28-hook");
            var butcherSecondJournal = ReadButcherJournalSample(host.RunDirectory, butcherSecondCurrent.Index,
                butcherSecondCurrent.Generation, crossTarget: true);
            Check(butcherSecondJournal is { } journalSample &&
                journalSample.GetProperty("Action").GetString() == "Block" &&
                journalSample.GetProperty("Reason").GetString() ==
                    "npc-strike-ordinary-butcher-sequence-preforward-stop" &&
                journalSample.GetProperty("ButcherCrossTargetContinuation").GetBoolean() &&
                journalSample.GetProperty("InterceptionBoundary").GetString() == "packet28-before-native-receiver",
                "ordinary-butcher-cross-target-first-hit-is-stopped-before-native");
            Check(butcherSecondRelayAfter - butcherSecondRelayBefore == 0 &&
                butcherSecondNativeAfter - butcherSecondNativeBefore == 0 &&
                butcherSecondAfterTarget.Active && butcherSecondAfterTarget.Life == butcherSecondCurrent.InitialLife,
                "ordinary-butcher-cross-target-rejection-prevents-new-target-loss");
            Check(!butcherSecondConsole.Any(x => x.Contains("ANTICHEAT_INCIDENT", StringComparison.Ordinal)) &&
                Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "ordinary-butcher-sequence-stop-has-no-permanent-sanction");
            observations.Add(new
            {
                kind = "butcher-normal-source-sequence-protection",
                sourceContract = ButcherNormalSource,
                sourceFormula = "ceil((npc.life + ceil(npc.defense/2)) / damage)",
                packetSequence = "13(target) -> computed packet28 prefix -> 13(saved Main.LocalPlayer.position)",
                actor = observer.Name,
                target = new
                {
                    first = butcherFirst,
                    second = butcherSecondCurrent,
                    firstHitCount = butcherFirstHitCount,
                    secondHitCount = SourceHitCount(butcherSecondCurrent, OrdinaryButcherDamage)
                },
                first = new
                {
                    before = butcherFirst,
                    after = butcherFirstAfterTarget,
                    rawPacket28Delta = butcherFirstRawAfter - butcherFirstRawBefore,
                    nativeStrikeEntryDelta = butcherFirstNativeDelta,
                    relayDelta = butcherFirstRelayDelta,
                    firstRejectionAfterPrefixLoss = butcherFirstRule is not null && butcherFirstNativeDelta == 1,
                    lossBeforeFirstRejection = butcherFirstLoss,
                    rule = butcherFirstRule,
                    console = butcherFirstConsole.Where(IsStrikeLine).Take(8).ToArray()
                },
                second = new
                {
                    before = butcherSecondCurrent,
                    after = butcherSecondAfterTarget,
                    rawPacket28Delta = butcherSecondRawAfter - butcherSecondRawBefore,
                    nativeStrikeEntryDelta = butcherSecondNativeAfter - butcherSecondNativeBefore,
                    relayDelta = butcherSecondRelayAfter - butcherSecondRelayBefore,
                    rule = butcherSecondRule,
                    journal = butcherSecondJournal,
                    console = butcherSecondConsole.Where(IsStrikeLine).Take(8).ToArray()
                },
                finiteContext = new
                {
                    targetPositionControl = true,
                    savedReturnControl = true,
                    repeatedAttackSignature = true,
                    crossTargetContinuation = true,
                    summonContextIsAuxiliary = true,
                    fixed64BudgetUsedAsButcherProof = false
                },
                serverAftermath = "one first-target packet28 reached native state; repeated tail and the next target prefix were canceled before native entry; targets remained connected and no ban was written",
                sanction = "none"
            });

            // A normal low packet is allowed and must reach the native receiver.
            var lowBefore = await host.FixtureSnapshot();
            int lowRelayBefore = CountRelays(observer, targets[0], 1);
            await actor.Send(28, writer => WriteStrike(writer, targets[0], 1));
            await observer.WaitUntil(() => CountRelays(observer, targets[0], 1) > lowRelayBefore,
                TimeSpan.FromSeconds(5));
            var lowAfter = await host.FixtureSnapshot();
            var lowBeforeTarget = ReadNpc(lowBefore, targets[0].Index);
            var lowAfterTarget = ReadNpc(lowAfter, targets[0].Index);
            Check(lowAfterTarget.Active && lowAfterTarget.Life < lowBeforeTarget.Life,
                "ordinary-low-strike-reaches-native-life-path");
            Check(observer.NpcStrikes.Skip(lowRelayBefore).Any(x => x.Target == targets[0].Index && x.Damage == 1),
                "ordinary-low-strike-reaches-existing-relay");
            observations.Add(new
            {
                kind = "butcher-normal-packet28-low-allowed",
                sourceContract = ButcherNormalSource,
                target = targets[0],
                before = lowBeforeTarget,
                after = lowAfterTarget,
                relayCount = CountRelays(observer, targets[0], 1) - lowRelayBefore
            });

            // Keep the same authenticated actor/session while sending the three
            // per-target prefixes in two bounded writes. Waiting after every
            // prefix can outlive the queue's 60-tick window and would test
            // expiry rather than cross-NPC aggregation.
            int lowStart = CountRelays(observer, targets[0], 1);
            int allowedAdditional = LowDamageLimit - 2; // one already accepted, one final is blocked
            await actor.SendBatch(Enumerable.Range(0, allowedAdditional)
                .Select(_ => LabClient.Packet(28, writer => WriteStrike(writer, targets[0], 1)))
                .ToArray());
            await observer.WaitUntil(() => CountRelays(observer, targets[0], 1) >=
                lowStart + allowedAdditional, TimeSpan.FromSeconds(8));
            var beforeLowBlock = await host.FixtureSnapshot();
            var beforeLowBlockTarget = ReadNpc(beforeLowBlock, targets[0].Index);
            int relayAtLowBlock = CountRelays(observer, targets[0], 1);
            int logAtLowBlock = host.ConsoleLines().Length;

            var secondBefore = await host.FixtureSnapshot();
            var secondTargetBefore = ReadNpc(secondBefore, targets[1].Index);
            var thirdTargetBefore = ReadNpc(secondBefore, targets[2].Index);
            var fourthTargetBefore = ReadNpc(secondBefore, targets[3].Index);
            int secondRelayStart = CountRelays(observer, targets[1], 1);
            int thirdRelayStart = CountRelays(observer, targets[2], 1);
            int fourthRelayStart = CountRelays(observer, targets[3], 1);

            var prefixTail = new[]
                {
                    LabClient.Packet(28, writer => WriteStrike(writer, targets[0], 1))
                }
                .Concat(Enumerable.Range(0, LowDamageLimit - 1)
                    .Select(_ => LabClient.Packet(28, writer => WriteStrike(writer, targets[1], 1))))
                .Concat(Enumerable.Range(0, LowDamageLimit - 1)
                    .Select(_ => LabClient.Packet(28, writer => WriteStrike(writer, targets[2], 1))))
                .ToArray();
            await actor.SendBatch(prefixTail);
            await actor.PingAsync();
            await observer.WaitUntil(() => CountRelays(observer, targets[1], 1) >=
                    secondRelayStart + LowDamageLimit - 1 &&
                CountRelays(observer, targets[2], 1) >=
                    thirdRelayStart + LowDamageLimit - 1, TimeSpan.FromSeconds(8));
            var afterPrefix = await host.FixtureSnapshot();
            int secondRelayAtPrefix = CountRelays(observer, targets[1], 1);
            int thirdRelayAtPrefix = CountRelays(observer, targets[2], 1);
            var afterLowBlockTarget = ReadNpc(afterPrefix, targets[0].Index);
            var lowBlockConsole = host.ConsoleLines().Skip(logAtLowBlock).ToArray();
            int clientStrikeAtLowBlock = NativeClientStrikeCount(beforeLowBlock, targets[0].Index, targets[0].Generation, actor.Slot);
            int clientStrikeAfterPrefix = NativeClientStrikeCount(afterPrefix, targets[0].Index, targets[0].Generation, actor.Slot);
            Check(relayAtLowBlock == lowStart + allowedAdditional,
                "64th-low-strike-has-no-native-relay");
            Check(clientStrikeAfterPrefix == clientStrikeAtLowBlock && afterLowBlockTarget.Active && afterLowBlockTarget.Life > 0,
                "64th-low-strike-has-no-client-native-entry-or-death-side-effect");
            Check(lowBlockConsole.Any(x => x.Contains("rule=F06.NpcStrikeDensityBudget") &&
                x.Contains("reason=npc-strike-low-damage-window-budget-exhausted") &&
                x.Contains("action=Block") && x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase)),
                "64th-low-strike-writes-bounded-resource-block-before-native");
            Check(Scalar(Path.Combine(run, "tshock", "tshock.sqlite"), "SELECT COUNT(*) FROM PlayerBans") == 0,
                "low-density-stop-loss-does-not-ban");
            observations.Add(new
            {
                kind = "butcher-normal-packet28-low-density-target-budget",
                sourceContract = ButcherNormalSource,
                target = targets[0],
                acceptedRelays = relayAtLowBlock - lowStart + 1,
                blockedRequest = LowDamageLimit,
                before = beforeLowBlockTarget,
                after = afterLowBlockTarget,
                console = lowBlockConsole.Where(IsStrikeLine).Take(8).ToArray()
            });

            // The second and third final requests are sent together with the
            // fourth target's first request. The fourth target has target=1,
            // so session=193 is a distinct session-budget proof.
            var secondBeforeBlockTarget = ReadNpc(afterPrefix, targets[1].Index);
            var thirdBeforeBlockTarget = ReadNpc(afterPrefix, targets[2].Index);
            int finalLogStart = host.ConsoleLines().Length;
            await actor.SendBatch(
                LabClient.Packet(28, writer => WriteStrike(writer, targets[1], 1)),
                LabClient.Packet(28, writer => WriteStrike(writer, targets[2], 1)),
                LabClient.Packet(28, writer => WriteStrike(writer, targets[3], 1)));
            await actor.PingAsync();
            await Task.Delay(100);
            var afterFinal = await host.FixtureSnapshot();
            var secondAfterTarget = ReadNpc(afterFinal, targets[1].Index);
            var thirdAfterBlockTarget = ReadNpc(afterFinal, targets[2].Index);
            var fourthTargetAfter = ReadNpc(afterFinal, targets[3].Index);
            var finalConsole = host.ConsoleLines().Skip(finalLogStart).ToArray();
            int secondClientAfterFinal = NativeClientStrikeCount(afterFinal, targets[1].Index, targets[1].Generation, actor.Slot);
            int thirdClientAfterFinal = NativeClientStrikeCount(afterFinal, targets[2].Index, targets[2].Generation, actor.Slot);
            int fourthClientAfterFinal = NativeClientStrikeCount(afterFinal, targets[3].Index, targets[3].Generation, actor.Slot);
            int firstTargetRelayAfterCrossNpc = CountRelays(observer, targets[0], 1);
            Check(firstTargetRelayAfterCrossNpc == relayAtLowBlock,
                "first-npc-low-budget-remains-blocked-after-cross-npc-prefix");
            Check(CountRelays(observer, targets[1], 1) == secondRelayAtPrefix,
                "second-npc-64th-low-strike-has-no-relay");
            Check(CountRelays(observer, targets[2], 1) == thirdRelayAtPrefix,
                "third-npc-64th-low-strike-has-no-relay");
            Check(CountRelays(observer, targets[3], 1) == fourthRelayStart,
                "fourth-npc-first-low-strike-has-no-relay-after-session-budget");
            Check(secondClientAfterFinal == NativeClientStrikeCount(afterPrefix, targets[1].Index, targets[1].Generation, actor.Slot) &&
                secondAfterTarget.Active && secondAfterTarget.Life > 0,
                "second-npc-low-stop-has-no-client-native-entry-or-death-side-effect");
            Check(secondAfterTarget.Life < secondTargetBefore.Life,
                "second-npc-low-prefix-really-reached-native-life-path");
            Check(thirdClientAfterFinal == NativeClientStrikeCount(afterPrefix, targets[2].Index, targets[2].Generation, actor.Slot) &&
                thirdAfterBlockTarget.Active && thirdAfterBlockTarget.Life > 0,
                "third-npc-low-stop-has-no-client-native-entry-or-death-side-effect");
            Check(fourthClientAfterFinal == NativeClientStrikeCount(afterPrefix, targets[3].Index, targets[3].Generation, actor.Slot) &&
                fourthTargetAfter.Active && fourthTargetAfter.Life > 0,
                "fourth-npc-session-stop-has-no-client-native-entry-or-death-side-effect");
            Check(finalConsole.Any(x => x.Contains("rule=F06.NpcStrikeDensityBudget") &&
                x.Contains("reason=npc-strike-low-damage-session-budget-exhausted") &&
                x.Contains("target=1;session=193") && x.Contains("action=Block") &&
                x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase)),
                "fourth-npc-first-low-strike-is-blocked-by-session-budget");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "cross-npc-session-stop-loss-does-not-ban");
            observations.Add(new
            {
                kind = "butcher-normal-packet28-cross-npc-low-density-target-budget",
                sourceContract = ButcherNormalSource,
                target = targets[1],
                acceptedRelays = secondRelayAtPrefix - secondRelayStart,
                blockedRequest = LowDamageLimit,
                before = secondBeforeBlockTarget,
                after = secondAfterTarget
            });
            observations.Add(new
            {
                kind = "butcher-normal-packet28-third-npc-session-budget-prefix",
                sourceContract = ButcherNormalSource,
                target = targets[2],
                acceptedRelays = thirdRelayAtPrefix - thirdRelayStart,
                blockedRequest = LowDamageLimit,
                before = thirdTargetBefore,
                after = thirdAfterBlockTarget
            });
            observations.Add(new
            {
                kind = "butcher-normal-packet28-cross-npc-session-budget-stop",
                sourceContract = ButcherNormalSource,
                target = targets[3],
                acceptedRelays = 0,
                blockedRequest = 1,
                before = fourthTargetBefore,
                after = fourthTargetAfter,
                console = finalConsole.Where(IsStrikeLine).Take(8).ToArray(),
                sessionBudget = LowDamageSessionLimit,
                targetCountAtBlock = 1,
                sessionCountAtBlock = LowDamageSessionLimit + 1
            });

            // 9999 is a deliberately explicit candidate stop-loss. The default
            // ProductionCandidate path is record/block-only. A separate, explicit
            // TestLab candidate may exercise the complete first-sanction chain.
            var extremeBefore = await host.FixtureSnapshot();
            var extremeBeforeTarget = ReadNpc(extremeBefore, targets[0].Index);
            int extremeRelayStart = CountRelays(observer, targets[0], ExtremeDamage);
            int extremeLogStart = host.ConsoleLines().Length;
            await actor.Send(28, writer => WriteStrike(writer, targets[0], ExtremeDamage));
            await Task.Delay(350);
            await observer.Drain(TimeSpan.FromMilliseconds(150));
            var extremeAfter = await host.FixtureSnapshot();
            var extremeAfterTarget = ReadNpc(extremeAfter, targets[0].Index);
            var extremeConsole = host.ConsoleLines().Skip(extremeLogStart).ToArray();
            int extremeClientStrikeBefore = NativeClientStrikeCount(extremeBefore, targets[0].Index, targets[0].Generation, actor.Slot);
            int extremeClientStrikeAfter = NativeClientStrikeCount(extremeAfter, targets[0].Index, targets[0].Generation, actor.Slot);
            Check(CountRelays(observer, targets[0], ExtremeDamage) == extremeRelayStart,
                "9999-extreme-strike-has-no-native-relay");
            Check(extremeClientStrikeAfter == extremeClientStrikeBefore && extremeAfterTarget.Active && extremeAfterTarget.Life > 0,
                "9999-extreme-strike-has-no-client-native-entry-death-or-drop-trigger");
            long actorAccount = Scalar(Path.Combine(run, "tshock", "tshock.sqlite"),
                "SELECT ID FROM Users WHERE Username=$name", actor.Name);
            if (!permanentSanctionCandidate)
            {
                Check(extremeConsole.Any(x => x.Contains("rule=F06.NpcStrikeDensityBudget") &&
                    x.Contains("reason=npc-strike-extreme-damage-preforward-stop") &&
                    x.Contains("verdict=UnsafeInput") && x.Contains("action=Block") &&
                    x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase)),
                    "9999-extreme-strike-is-written-and-blocked-before-native");
                Check(!extremeConsole.Any(x => x.Contains("ANTICHEAT_INCIDENT") && x.Contains("accountId=" + actorAccount)),
                    "9999-extreme-strike-is-not-permanent-ban-proof");
                Check(Scalar(Path.Combine(run, "tshock", "tshock.sqlite"), "SELECT COUNT(*) FROM PlayerBans") == 0,
                    "9999-extreme-stop-loss-does-not-ban");
            }
            else
            {
                Check(extremeConsole.Any(x => x.Contains("rule=F06.NpcStrikeDensityBudget") &&
                    x.Contains("reason=npc-strike-extreme-damage-first-sanction-candidate") &&
                    x.Contains("verdict=ProvenCheat") && x.Contains("action=Block") &&
                    x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase)),
                    "9999-extreme-sanction-candidate-is-written-and-blocked-before-native");
                Check(extremeConsole.Count(x => x.Contains("ANTICHEAT_INCIDENT") &&
                    x.Contains("rule=F06.NpcStrikeDensityBudget") &&
                    x.Contains("accountId=" + actorAccount + " ") &&
                    x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                    x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1,
                    "9999-extreme-sanction-candidate-revokes-one-attributed-session");
                await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed,
                    TimeSpan.FromSeconds(8));
                Check(actor.IsBanRejection == false && actor.DisconnectReason == "AntiCheat proven violation.",
                    "9999-extreme-sanction-candidate-disconnects-the-first-session");
                await Until(() => Scalar(database,
                    "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999",
                    "acc:" + actor.Name) == 1, "9999-extreme-sanction-candidate-persists-one-account-ban");
                string journal = Path.Combine(run, "tshock", "anticheat", "enforcement");
                phase = "waiting-for-applied-journal";
                await Until(() => AppliedIntentCount(journal, actorAccount) == 1,
                    "9999-extreme-sanction-candidate-journal-intent-applied");
                phase = "recording-proven-account";
                Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 1,
                    "9999-extreme-sanction-candidate-has-no-duplicate-or-innocent-ban");
                observations.Add(new
                {
                    kind = "butcher-normal-packet28-extreme-damage-first-sanction-candidate",
                    sourceContract = ButcherNormalSource,
                    target = targets[0],
                    wireDamage = ExtremeDamage,
                    receiverDamage = ExtremeDamage,
                    before = extremeBeforeTarget,
                    after = extremeAfterTarget,
                    actorAccount,
                    console = extremeConsole.Where(IsStrikeLine).Take(12).ToArray(),
                    contract = "TestLabCandidate only; stage/target/identity/legal-exception gates complete; first account sanction is candidate evidence, not formal qualification"
                });
                host.RecordProvenAccount(actor.Name, actorAccount, "F06.NpcStrikeDensityBudget");
                phase = "disposing-first-banned-client";
                await actor.DisposeAsync();
                await Task.Delay(200);

                var connectWithIdentity = host.ConnectWithIdentity ??
                    throw new InvalidOperationException("M18 sanction validation requires identity-preserving lab reconnect.");
                phase = "immediate-reconnect";
                var immediate = await connectWithIdentity(actor.Name, actor.Uuid);
                await ExpectBanRejection(immediate);
                Check(immediate.IsBanRejection && immediate.LoginAttempts <= 1,
                    "9999-extreme-sanction-candidate-immediate-same-identity-reconnect-rejected");
                phase = "disposing-immediate-reconnect";
                await immediate.DisposeAsync();
                phase = "disposing-innocent-before-restart";
                await observer.DisposeAsync();
                var restart = host.RestartServer ??
                    throw new InvalidOperationException("M18 sanction validation requires the existing lab restart lifecycle.");
                phase = "clean-restart";
                await restart();

                phase = "restored-innocent-reconnect";
                var restoredObserver = await connectWithIdentity(observer.Name, observer.Uuid);
                await restoredObserver.Join(); await restoredObserver.Login();
                Check(restoredObserver.Authenticated && !restoredObserver.Closed && restoredObserver.SscSlots.Count >= 350,
                    "9999-extreme-sanction-candidate-innocent-observer-restored-after-restart");
                phase = "post-restart-banned-reconnect";
                var restartedActor = await connectWithIdentity(actor.Name, actor.Uuid);
                await ExpectBanRejection(restartedActor);
                Check(restartedActor.IsBanRejection && restartedActor.LoginAttempts <= 1,
                    "9999-extreme-sanction-candidate-same-identity-reconnect-rejected-after-restart");
                await restartedActor.DisposeAsync();
                Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999",
                    "acc:" + actor.Name) == 1 && Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 1,
                    "9999-extreme-sanction-candidate-persistent-ban-remains-single-after-restart");
                observations.Add(new
                {
                    kind = "butcher-normal-packet28-extreme-damage-sanction-reconnect-restart",
                    account = actor.Name,
                    actorAccount,
                    immediateReconnect = new { rejected = immediate.IsBanRejection, loginAttempts = immediate.LoginAttempts },
                    restartReconnect = new { rejected = restartedActor.IsBanRejection, loginAttempts = restartedActor.LoginAttempts },
                    innocentObserver = new { restoredObserver.Authenticated, restoredObserver.SscSlots.Count },
                    enforcement = "first rejection, session revocation, durable journal/account ban, immediate rejection, clean restart, post-restart rejection"
                });
                await restoredObserver.DisposeAsync();
                await EnsureEvidence(run, report, preparedRoom, initial, observations, failure,
                    permanentSanctionCandidate);
                status = "passed";
                return;
            }
            observations.Add(new
            {
                kind = "butcher-normal-packet28-extreme-damage-preforward-stop",
                sourceContract = ButcherNormalSource,
                target = targets[0],
                wireDamage = ExtremeDamage,
                receiverDamage = ExtremeDamage,
                before = extremeBeforeTarget,
                after = extremeAfterTarget,
                console = extremeConsole.Where(IsStrikeLine).Take(8).ToArray(),
                sanction = "none; weapon/effect provenance and legal damage formula are not complete"
            });

            // Below the explicit line remains a bounded record path. The actor
            // session has intentionally consumed its positive-damage budget by
            // this point, so use the independently authenticated observer for
            // this proof. That keeps the session-budget stop-loss evidence and
            // the high-but-non-extreme record-only path independent without
            // resetting or weakening either budget. The peer captures the
            // server relay because the observer is the packet sender. If the
            // native receiver rejects the packet, the evidence must retain
            // that as rejection rather than infer a successful attack.
            var recordBefore = await host.FixtureSnapshot();
            var recordTargetBefore = ReadNpc(recordBefore, targets[1].Index);
            int recordLogStart = host.ConsoleLines().Length;
            const int highRecordDamage = 100;
            int recordRelayStart = CountRelays(actor, targets[1], highRecordDamage);
            await observer.Send(28, writer => WriteStrike(writer, targets[1], highRecordDamage));
            await actor.WaitUntil(() => CountRelays(actor, targets[1], highRecordDamage) > recordRelayStart,
                TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            await actor.Drain(TimeSpan.FromMilliseconds(150));
            var recordAfter = await host.FixtureSnapshot();
            var recordTargetAfter = ReadNpc(recordAfter, targets[1].Index);
            var recordConsole = host.ConsoleLines().Skip(recordLogStart).ToArray();
            var recordRelays = actor.NpcStrikes
                .Where(x => x.Target == targets[1].Index && x.Generation == targets[1].Generation &&
                    x.Damage == highRecordDamage).ToArray();
            await Until(() => ReadStrikeJournalSample(host.RunDirectory, targets[1].Index,
                targets[1].Generation, "npc-strike-queue-record-only") is not null,
                "100-below-line-journal-record");
            var recordJournal = ReadStrikeJournalSample(host.RunDirectory, targets[1].Index,
                targets[1].Generation, "npc-strike-queue-record-only");
            bool recordRelayObserved = recordRelays.Length > 0;
            bool recordLifeChanged = !recordTargetAfter.Active || recordTargetAfter.Life < recordTargetBefore.Life;
            Check(recordRelayObserved && recordLifeChanged,
                "100-below-line-reaches-native-life-and-peer-relay");
            Check(recordJournal is { } recordSample &&
                recordSample.GetProperty("Action").GetString() == "Unknown" &&
                recordSample.GetProperty("Verdict").GetString() == "Unknown" &&
                recordSample.GetProperty("Reason").GetString() == "npc-strike-queue-record-only",
                "100-below-line-is-recorded-without-hard-proof");
            observations.Add(new
            {
                kind = "butcher-normal-packet28-high-record-native-acceptance-not-observed",
                sourceContract = ButcherNormalSource,
                target = targets[1],
                wireDamage = highRecordDamage,
                before = recordTargetBefore,
                after = recordTargetAfter,
                sender = observer.Name,
                senderAccount = Scalar(database, "SELECT ID FROM Users WHERE Username=$name", observer.Name),
                relayObserved = recordRelayObserved,
                relayDamages = recordRelays.Select(x => x.Damage).ToArray(),
                nativeLifeChanged = recordLifeChanged,
                journal = recordJournal,
                console = recordConsole.Where(IsStrikeLine).Take(8).ToArray(),
                interpretation = "F06 record-only admission was observed and the authenticated peer observed the native relay/life side effect; this remains a bounded record path, not permanent-sanction proof."
            });

            await EnsureEvidence(run, report, preparedRoom, initial, observations, failure,
                permanentSanctionCandidate);
            status = "passed";
        }
        catch (Exception error)
        {
            primaryError = error;
            failurePhase = phase;
            failureStack = error.ToString();
            failure = error.GetType().Name + ": " + error.Message + " phase=" + phase;
            string? evidenceError = await WriteEvidence(run, report, null, null, observations, failure,
                permanentSanctionCandidate, failurePhase, failureStack);
            if (evidenceError is not null) RecordEvidenceFailure("scenario-evidence-after-failure", evidenceError);
            throw;
        }
        finally
        {
            try { await CopyRelevantQaEvents(run, report); }
            catch (Exception error) { RecordEvidenceFailure("qa-event-copy", error.ToString()); }
            try
            {
                await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-a-run-summary.json"),
                    JsonSerializer.Serialize(new { status, failure, failurePhase, failureStack, evidenceFailures, observations }, Json));
            }
            catch (Exception error) { RecordEvidenceFailure("run-summary-write", error.ToString()); }

            // A finally block runs after an early return as well as after a
            // normal completion. If no business exception is already in
            // flight, a required evidence-output failure must escape this
            // scenario so Program.cs cannot convert it to a false pass.
            M18EvidenceFailurePropagation.ThrowIfRequiredEvidenceFailed(primaryError, evidenceFailures);
        }

        void RecordEvidenceFailure(string stage, string detail)
        {
            status = "failed";
            if (evidenceFailures.Count < 16)
                evidenceFailures.Add(stage + ": " + (detail.Length <= 2048 ? detail : detail[..2048]));
            if (string.IsNullOrEmpty(failure))
            {
                failurePhase = stage;
                failure = "evidence-output-failure phase=" + stage;
                failureStack = detail;
            }
        }

        async Task EnsureEvidence(string evidenceRun, string evidenceReport, JsonElement? evidenceRoom,
            JsonElement? evidenceInitial, IReadOnlyList<object> evidenceObservations, string evidenceFailure,
            bool evidenceSanctionCandidate, string evidenceFailurePhase = "", string evidenceFailureStack = "")
        {
            string? error = await WriteEvidence(evidenceRun, evidenceReport, evidenceRoom, evidenceInitial,
                evidenceObservations, evidenceFailure, evidenceSanctionCandidate, evidenceFailurePhase, evidenceFailureStack);
            if (error is null) return;
            RecordEvidenceFailure("scenario-evidence", error);
            throw new IOException("M18 evidence output failed: " + error);
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-a:" + name);

        async Task<JsonElement> WaitFixtureSnapshot(Func<JsonElement, bool> predicate, string label)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var snapshot = await host.FixtureSnapshot();
                if (predicate(snapshot)) return snapshot;
                await Task.Delay(100);
            }
            throw new TimeoutException("M18P0A fixture condition timed out: " + label);
        }

        async Task ExpectBanRejection(LabClient client)
        {
            try { await client.Join(); }
            catch (IOException) when (client.DisconnectReason is not null || client.Closed) { }
            if (!client.Closed && client.DisconnectReason is null)
                await client.LoginAndExpectRejected();
            await client.WaitUntil(() => client.DisconnectReason is not null || client.Closed,
                TimeSpan.FromSeconds(8));
        }
    }

    private static bool IsStrikeLine(string line) => line.Contains("F06.NpcStrikeDensityBudget", StringComparison.Ordinal) ||
        line.Contains("ANTICHEAT_INCIDENT", StringComparison.Ordinal);

    private static int CountRelays(LabClient client, NpcTarget target, int damage)
        => client.NpcStrikes.Count(x => x.Target == target.Index && x.Generation == target.Generation &&
            x.Damage == damage);

    private static async Task<string?> WriteEvidence(string run, string report, JsonElement? preparedRoom,
        JsonElement? initial, IReadOnlyList<object> observations, string failure = "",
        bool permanentSanctionCandidate = false, string failurePhase = "", string failureStack = "")
    {
        string path = Path.Combine(report, "m18-p0-a-evidence.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                status = string.IsNullOrEmpty(failure) ? "passed" : "failed",
                failure,
                failurePhase,
                failureStack,
                runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / C2S packet28",
                source = ButcherNormalSource,
                qaPreparation = "existing GameplayScaffold qa_prepare + qa_npcs; disposable world only",
                preparedRoom,
                initial,
                observations,
                sanctionBoundary = permanentSanctionCandidate
                    ? "TestLab-only first-sanction candidate; stage/target/identity/legal-exception gates were exercised, while formal qualification and production enablement remain pending"
                    : "low density and 9999 extreme stop are Block-only; no permanent account sanction because packet28 weapon/effect provenance is incomplete"
            }, Json));
            return null;
        }
        catch (Exception evidenceError)
        {
            // Never replace the original experiment failure with a serializer
            // failure. Nullable snapshots ensure the normal path is safe; this
            // final bounded record preserves the original stack if another
            // observation object is unexpectedly non-serializable.
            try
            {
                string fallbackPath = path + ".failure.json";
                await File.WriteAllTextAsync(fallbackPath, JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    status = "failed",
                    failure,
                    failurePhase,
                    failureStack,
                    runDirectory = run,
                    evidenceSerializationError = evidenceError.ToString(),
                    observationCount = observations.Count,
                }, Json));
            }
            catch (Exception fallbackError)
            {
                return evidenceError + " | fallback-output=" + fallbackError;
            }
            return evidenceError.ToString();
        }
    }

    private static async Task CopyRelevantQaEvents(string run, string report)
    {
        var searchRoots = new[]
        {
            Path.GetFullPath(run),
            Path.GetFullPath(Path.GetDirectoryName(report) ?? report)
        };
        string? source = searchRoots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "gameplay-events-*.jsonl"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (source is null)
            throw new FileNotFoundException("Required gameplay event journal was not found.",
                string.Join("; ", searchRoots));
        var lines = File.ReadLines(source)
            .Where(line => line.Contains("packetId", StringComparison.Ordinal) &&
                line.Contains("28", StringComparison.Ordinal))
            .Take(256)
            .ToArray();
        if (lines.Length == 0)
            throw new InvalidDataException("Required packet28 QA evidence was not present in the gameplay journal.");
        await File.WriteAllLinesAsync(Path.Combine(report, "qa-packet28-events.jsonl"), lines);
    }

    private static NpcState ReadNpc(JsonElement snapshot, int index)
    {
        var npc = snapshot.GetProperty("npcs").EnumerateArray()
            .Single(x => x.GetProperty("index").GetInt32() == index);
        return new NpcState(index, npc.GetProperty("active").GetBoolean(),
            npc.GetProperty("life").GetInt32(), npc.GetProperty("generation").GetInt32(),
            npc.GetProperty("type").GetInt32());
    }

    private static NpcTarget ReadLiveNpcTarget(JsonElement snapshot, int index)
    {
        var npc = snapshot.GetProperty("npcs").EnumerateArray()
            .Single(x => x.GetProperty("index").GetInt32() == index);
        return new NpcTarget(index,
            npc.GetProperty("generation").GetInt32(),
            npc.GetProperty("type").GetInt32(),
            npc.GetProperty("life").GetInt32(),
            npc.GetProperty("position").GetProperty("x").GetSingle(),
            npc.GetProperty("position").GetProperty("y").GetSingle(),
            npc.GetProperty("defense").GetInt32(),
            npc.GetProperty("lifeMax").GetInt32());
    }

    private static int SourceHitCount(NpcTarget target, int damage)
        => Math.Max(1, (int)Math.Ceiling((target.InitialLife +
            (int)Math.Ceiling(target.Defense / 2f)) / (float)damage));

    private static int NativeClientStrikeCount(JsonElement snapshot, int target, int generation, int owner)
    {
        var observer = snapshot.GetProperty("m18NativeStrikeObserver");
        return observer.GetProperty("events").EnumerateArray().Count(x =>
            x.GetProperty("target").GetInt32() == target &&
            x.GetProperty("generation").GetInt32() == generation &&
            x.GetProperty("fromNet").GetBoolean() &&
            x.GetProperty("owner").GetInt32() == owner);
    }

    private static int CountRawFrames(string report, byte packetId, byte playerIndex)
    {
        int count = 0;
        foreach (string line in Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines))
        {
            if (!line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            var payload = document.RootElement.GetProperty("payload");
            if (payload.GetProperty("packetId").GetByte() == packetId &&
                payload.GetProperty("playerIndex").GetByte() == playerIndex) count++;
        }
        return count;
    }

    private static bool HasButcherJournalSample(string run, int targetSlot, int targetGeneration,
        bool crossTarget)
        => ReadButcherJournalSample(run, targetSlot, targetGeneration, crossTarget) is not null;

    private static JsonElement? ReadStrikeJournalSample(string run, int targetSlot, int targetGeneration,
        string reason)
    {
        string path = Path.Combine(run, "tshock", "anticheat", "m18-observations.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1_048_576) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("StrikeBuckets", out var buckets)) return null;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("Samples", out var samples)) continue;
                foreach (var sample in samples.EnumerateArray())
                {
                    if (sample.GetProperty("TargetSlot").GetInt32() == targetSlot &&
                        sample.GetProperty("TargetGeneration").GetInt32() == targetGeneration &&
                        sample.GetProperty("Reason").GetString() == reason)
                        return sample.Clone();
                }
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        return null;
    }

    private static JsonElement? ReadButcherJournalSample(string run, int targetSlot, int targetGeneration,
        bool crossTarget)
    {
        string path = Path.Combine(run, "tshock", "anticheat", "m18-observations.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1_048_576) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("StrikeBuckets", out var buckets)) return null;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("Samples", out var samples)) continue;
                foreach (var sample in samples.EnumerateArray())
                {
                    if (sample.GetProperty("TargetSlot").GetInt32() == targetSlot &&
                        sample.GetProperty("TargetGeneration").GetInt32() == targetGeneration &&
                        sample.GetProperty("Action").GetString() == "Block" &&
                        sample.GetProperty("Reason").GetString() ==
                            "npc-strike-ordinary-butcher-sequence-preforward-stop" &&
                        sample.GetProperty("ButcherCrossTargetContinuation").GetBoolean() == crossTarget)
                        return sample.Clone();
                }
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        return null;
    }

    private static void WriteStrike(BinaryWriter writer, NpcTarget target, int damage)
    {
        writer.Write(checked((byte)target.Index));
        writer.Write(checked((byte)target.Generation));
        writer.Write(checked((short)damage));
        writer.Write(0f);
        writer.Write((byte)1); // encoded direction: native direction 0
        writer.Write((byte)1); // critical flag
    }

    private static byte[] PlayerControlsFrame(byte playerSlot, float x, float y)
        => LabClient.Packet(13, writer =>
        {
            writer.Write(playerSlot);
            writer.Write((byte)0); // control flags
            writer.Write((byte)0); // movement flags
            writer.Write((byte)0); // miscellaneous flags
            writer.Write((byte)0); // extra flags
            writer.Write((byte)0); // selected item
            writer.Write(x);
            writer.Write(y);
        });

    private static long Scalar(string database, string sql, params string?[] values)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$name", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$name", values.ElementAtOrDefault(0));
        if (sql.Contains("$group", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$group", values.ElementAtOrDefault(1));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task Until(Func<bool> predicate, string label)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }

    private static int AppliedIntentCount(string journal, long account)
    {
        var paths = Directory.EnumerateFiles(journal, "*.json").Take(3).ToArray();
        if (paths.Length > 1) throw new InvalidDataException("Unexpected M18 sanction intent count.");
        int found = 0;
        foreach (string path in paths)
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Oversized M18 sanction intent.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var record = JsonDocument.Parse(stream);
            if (record.RootElement.GetProperty("Intent").GetProperty("AccountId").GetInt64() == account &&
                record.RootElement.GetProperty("Intent").GetProperty("Evidence").GetProperty("RuleId").GetString() ==
                    "F06.NpcStrikeDensityBudget" && record.RootElement.GetProperty("Applied").GetBoolean())
                found++;
        }
        return found;
    }

    private sealed record NpcTarget(int Index, int Generation, int Type, int InitialLife,
        float X, float Y, int Defense, int LifeMax);
    private sealed record NpcState(int Index, bool Active, int Life, int Generation, int Type);
}
