using System.Text.Json;
using Microsoft.Data.Sqlite;

// Narrow live slice for M18 P0-D. It checks the actual SSC inventory-sync and
// client-to-server new-world-item path at the target runtime. Quantity validity
// is separated from acquisition/creator attribution; no account sanction is
// inferred from a legal maxStack value. The packet21 pickup and the later
// packet5 SSC state write are recorded as separate protocol stages.
internal static class M18ItemDropScenario
{
    private const byte PlayerSlotPacket = 5;
    private const byte PlayerControlsPacket = 13;
    private const byte ItemDropPacket = 21;
    private const byte ItemOwnerPacket = 22;
    private const byte UpdateItemDropPacket = 90;
    private const short DirtBlock = 2;
    private const short ServerFixtureStone = 3; // Target ItemID.StoneBlock.
    private const short MaxStack = 9999;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-d");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string status = "failed", failure = "";
        bool itemDropSanctionCandidate = Environment.GetEnvironmentVariable("ANTICHEAT_M18_ITEM_DROP_SANCTION_CANDIDATE") == "1";
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0DObserver");
            await observer.Join(); await observer.RegisterAndLogin();
            observer.PauseHeartbeat = true;
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0DActor");
            await actor.Join(); await actor.RegisterAndLogin();
            actor.PauseHeartbeat = true;
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            await host.ConsoleCommand("qa_prepare " + actor.Name);
            await host.ConsoleCommand("qa_capture 5 13 21 22 90");
            var prepared = await host.FixtureSnapshot();
            int maxWorldItems = prepared.GetProperty("maxWorldItems").GetInt32();
            Check(maxWorldItems is > 0 and <= 4096, "locked-world-item-array-capacity-is-bounded");

            // The fixture is server-authorized and one-time. It creates a known
            // ordinary item slot without treating that setup as client acquisition.
            await host.ConsoleCommand("qa_m18_materials " + actor.Name);
            string markerPath = Path.Combine(run, "qa-m18-materials-issued-" + actor.Slot + ".json");
            await Until(() => File.Exists(markerPath), "m18-material-fixture-marker");
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
            int inventorySlot = marker.RootElement.GetProperty("slot").GetInt32();
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Slot == inventorySlot && x.Item == DirtBlock && x.Stack == 100), TimeSpan.FromSeconds(8));
            Check(actor.InventoryUpdates.Any(x => x.Player == actor.Slot && x.Slot == inventorySlot &&
                x.Item == DirtBlock && x.Stack == 100), "authorized-fixture-inventory-baseline-received");

            int inventoryEventBefore = CountQaEvents(host.ReportDirectory, "player-slot-packet");
            int inventoryPeerBefore = observer.InventoryUpdates.Count;
            int inventoryLogStart = host.ConsoleLines().Length;
            byte[] inventoryFrame = InventoryFrame(actor.Slot, inventorySlot, MaxStack, DirtBlock);
            await actor.SendBatch(inventoryFrame);
            await actor.PingAsync();
            await observer.WaitUntil(() => observer.InventoryUpdates.Skip(inventoryPeerBefore).Any(x =>
                x.Player == actor.Slot && x.Slot == inventorySlot && x.Item == DirtBlock && x.Stack == MaxStack),
                TimeSpan.FromSeconds(8));
            var inventoryAfter = await host.FixtureSnapshot();
            var actorAfterInventory = Player(inventoryAfter, actor.Name);
            var inventoryItem = actorAfterInventory.GetProperty("inventory").EnumerateArray()
                .Single(x => x.GetProperty("slot").GetInt32() == inventorySlot);
            int inventoryEventAfter = CountQaEvents(host.ReportDirectory, "player-slot-packet");
            string? inventoryRule = host.ConsoleLines().Skip(inventoryLogStart).FirstOrDefault(x =>
                x.Contains("rule=B1.ItemStructure", StringComparison.Ordinal) &&
                x.Contains("packet=5", StringComparison.Ordinal));
            Check(inventoryItem.GetProperty("type").GetInt32() == DirtBlock &&
                inventoryItem.GetProperty("stack").GetInt32() == MaxStack,
                "9999-normal-inventory-stack-reaches-ssc-state");
            Check(inventoryEventAfter > inventoryEventBefore,
                "9999-normal-inventory-write-reaches-player-slot-hook");
            Check(inventoryRule is not null, "9999-inventory-structure-rule-is-recorded");
            Check(Healthy(actor, database), "9999-normal-inventory-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "inventory-sync-max-stack",
                packet = PlayerSlotPacket,
                frameHex = Convert.ToHexString(inventoryFrame),
                slot = inventorySlot,
                item = DirtBlock,
                stack = MaxStack,
                after = inventoryItem,
                qaPlayerSlotEvents = inventoryEventAfter - inventoryEventBefore,
                peerInventoryBroadcasts = observer.InventoryUpdates.Count - inventoryPeerBefore,
                rule = inventoryRule,
                acquisitionAttribution = "not established by a legal maxStack inventory sync",
                sanction = "none"
            });

            // Capture an authorized server-owned control first. The scaffold
            // uses the audited native Item.NewItem/reservation/packet21/22
            // sequence; it is separate from the client-created packet21
            // candidate below and cannot establish client creator proof.
            await actor.Drain(TimeSpan.FromMilliseconds(150));
            await observer.Drain(TimeSpan.FromMilliseconds(150));
            int serverItemActorBefore = actor.WorldItemUpdates.Count;
            int serverItemObserverBefore = observer.WorldItemUpdates.Count;
            int serverOwnerActorBefore = actor.ItemOwnerUpdates.Count;
            int serverOwnerObserverBefore = observer.ItemOwnerUpdates.Count;
            await host.ConsoleCommand("qa_m18_server_item " + actor.Name);
            string serverMarkerPath = Path.Combine(run, "qa-m18-server-item-" + actor.Slot + ".json");
            await Until(() => File.Exists(serverMarkerPath), "m18-server-item-control-marker");
            using var serverMarker = JsonDocument.Parse(await File.ReadAllTextAsync(serverMarkerPath));
            int serverIndex = serverMarker.RootElement.GetProperty("itemIndex").GetInt32();
            await observer.WaitUntil(() => observer.WorldItemUpdates.Skip(serverItemObserverBefore).Any(x =>
                x.Packet == ItemDropPacket && x.Item == serverIndex && x.Type == ServerFixtureStone && x.Stack == MaxStack),
                TimeSpan.FromSeconds(8));
            var serverControlAfter = await WaitSnapshot("server-owned-world-item", snapshot =>
                TryFindWorldItem(snapshot, serverIndex, ServerFixtureStone, MaxStack, out _));
            JsonElement serverControlItem;
            Check(TryFindWorldItem(serverControlAfter, serverIndex, ServerFixtureStone, MaxStack, out serverControlItem),
                "server-owned-control-creates-live-reserved-item");
            Check(serverControlItem.GetProperty("playerIndexTheItemIsReservedFor").GetInt32() == actor.Slot,
                "server-owned-control-retains-explicit-reservation");
            var serverActorOwnerPackets = actor.ItemOwnerUpdates.Skip(serverOwnerActorBefore)
                .Where(x => x.Item == serverIndex).Select(x => new { item = x.Item, owner = x.Owner }).ToArray();
            var serverObserverOwnerPackets = observer.ItemOwnerUpdates.Skip(serverOwnerObserverBefore)
                .Where(x => x.Item == serverIndex).Select(x => new { item = x.Item, owner = x.Owner }).ToArray();
            observations.Add(new
            {
                kind = "server-owned-world-item-control",
                source = "existing target TShock Bouncer Item.NewItem/noBroadcast + playerIndexTheItemIsReservedFor + NetMessage packet21/22",
                itemIndex = serverIndex,
                itemType = ServerFixtureStone,
                stack = MaxStack,
                reservation = actor.Slot,
                after = serverControlItem,
                actorPacket21 = actor.WorldItemUpdates.Skip(serverItemActorBefore)
                    .Where(x => x.Packet == ItemDropPacket && x.Item == serverIndex)
                    .Select(x => new { packet = x.Packet, item = x.Item, stack = x.Stack, type = x.Type }).ToArray(),
                observerPacket21 = observer.WorldItemUpdates.Skip(serverItemObserverBefore)
                    .Where(x => x.Packet == ItemDropPacket && x.Item == serverIndex)
                    .Select(x => new { packet = x.Packet, item = x.Item, stack = x.Stack, type = x.Type }).ToArray(),
                actorPacket22 = serverActorOwnerPackets,
                observerPacket22 = serverObserverOwnerPackets,
                attributionBoundary = "closed server-fixture reservation control; not creator proof for the later client packet21 request",
                sanction = "none"
            });

            // TerraAngel's WipeGroundItems shape reuses packet21 with type=0 and
            // stacks=0, but sends an arbitrary/remote coordinate for every live
            // item. Exercise that finite clear request against the still-live
            // server-owned item. This is a pre-native connection stop-loss only:
            // no account sanction, no type-zero blanket block, and no stack
            // validity shortcut.
            int clearRawBefore = CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot);
            int clearEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            int clearPeerWorldBefore = observer.WorldItemUpdates.Count;
            int clearLogStart = host.ConsoleLines().Length;
            float clearRequestX = serverControlItem.GetProperty("x").GetSingle() + 5000f;
            float clearRequestY = serverControlItem.GetProperty("y").GetSingle() + 5000f;
            byte[] remoteClearFrame = WorldItemFrame(ItemDropPacket, (short)serverIndex,
                clearRequestX, clearRequestY, 0f, 0f, 0, 0);
            await observer.SendBatch(remoteClearFrame);
            await observer.PingAsync();
            await Until(() => host.ConsoleLines().Skip(clearLogStart).Any(x =>
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal)),
                "ground-item-clear-remote-target-guard-recorded");
            await Task.Delay(150);
            var clearAfter = await host.FixtureSnapshot();
            JsonElement retainedServerItem;
            Check(TryFindWorldItem(clearAfter, serverIndex, ServerFixtureStone, MaxStack, out retainedServerItem),
                "remote-ground-clear-does-not-remove-the-live-server-item");
            int clearRawAfter = CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot);
            int clearEventAfter = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            var clearConsole = host.ConsoleLines().Skip(clearLogStart).ToArray();
            string? clearRule = clearConsole.FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal));
            Check(clearRawAfter > clearRawBefore,
                "remote-ground-clear-reaches-the-real-raw-input-hook");
            Check(clearEventAfter == clearEventBefore,
                "remote-ground-clear-is-canceled-before-native-item-drop-event");
            Check(clearRule is not null &&
                clearRule.Contains("verdict=ResourceAbuse", StringComparison.Ordinal) &&
                clearRule.Contains("reason=ground-item-clear-target-mismatch-stop-loss", StringComparison.Ordinal) &&
                clearRule.Contains("action=Block", StringComparison.Ordinal) &&
                clearRule.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                clearRule.Contains("alreadyCanceled=False", StringComparison.OrdinalIgnoreCase),
                "remote-ground-clear-is-the-f08-pre-native-stop-loss");
            Check(!observer.Closed && observer.DisconnectReason is null &&
                Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "remote-ground-clear-does-not-disconnect-or-permanently-sanction");
            Check(!observer.WorldItemUpdates.Skip(clearPeerWorldBefore).Any(x =>
                x.Packet == ItemDropPacket && x.Item == serverIndex && x.Stack == 0 && x.Type == 0),
                "remote-ground-clear-has-no-type-zero-native-removal-broadcast");
            observations.Add(new
            {
                kind = "ground-item-clear-remote-target-stop-loss",
                packet = ItemDropPacket,
                frameHex = Convert.ToHexString(remoteClearFrame),
                target = new
                {
                    index = serverIndex,
                    type = ServerFixtureStone,
                    stack = MaxStack,
                    before = serverControlItem,
                    after = retainedServerItem
                },
                request = new { x = clearRequestX, y = clearRequestY, type = 0, stacks = 0 },
                rawPacket21Delta = clearRawAfter - clearRawBefore,
                qaItemDropEventDelta = clearEventAfter - clearEventBefore,
                rule = clearRule,
                peerTypeZeroRemovalBroadcast = false,
                serverAftermath = "target remained active with the original type/stack",
                connectionAftermath = new { observer.Authenticated, observer.Closed, observer.DisconnectReason },
                permanentSanctions = Scalar(database, "SELECT COUNT(*) FROM PlayerBans"),
                sanction = "none; F08 is a bounded connection stop-loss only"
            });

            var worldBefore = await host.FixtureSnapshot();
            await WriteSnapshot(report, "world-before", worldBefore);
            int worldItemEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            int worldInventoryBefore = observer.InventoryUpdates.Count;
            int actorOwnerBefore = actor.ItemOwnerUpdates.Count;
            int observerOwnerBefore = observer.ItemOwnerUpdates.Count;
            int worldLogStart = host.ConsoleLines().Length;
            var spawn = actor.InitialSpawnPosition;
            byte[] newWorldItem = WorldItemFrame(ItemDropPacket, (short)maxWorldItems, spawn.X + 24f, spawn.Y,
                0f, 0f, MaxStack, DirtBlock);
            await actor.SendBatch(newWorldItem);
            await actor.PingAsync();
            await observer.PingAsync();
            var worldAfter = await WaitSnapshot("world-after-new-item", snapshot =>
                TryFindWorldItem(snapshot, DirtBlock, MaxStack, out _));
            JsonElement created;
            Check(TryFindWorldItem(worldAfter, DirtBlock, MaxStack, out created),
                "9999-world-drop-sentinel-creates-live-item");
            int createdIndex = created.GetProperty("index").GetInt32();
            int worldItemEventAfter = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            string? worldRule = host.ConsoleLines().Skip(worldLogStart).FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=B1.ItemStructure", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal));
            bool worldClearBlocked = host.ConsoleLines().Skip(worldLogStart).Any(x =>
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("action=Block", StringComparison.Ordinal) &&
                x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase));
            Check(worldItemEventAfter > worldItemEventBefore,
                "9999-world-drop-reaches-tshock-item-drop-hook");
            Check(!worldClearBlocked,
                "9999-world-drop-does-not-write-a-block-decision");
            Check(Healthy(actor, database), "9999-world-drop-keeps-actor-healthy");
            var actorOwnerPackets = actor.ItemOwnerUpdates.Skip(actorOwnerBefore)
                .Where(x => x.Item == createdIndex).Select(x => new { item = x.Item, owner = x.Owner }).ToArray();
            var observerOwnerPackets = observer.ItemOwnerUpdates.Skip(observerOwnerBefore)
                .Where(x => x.Item == createdIndex).Select(x => new { item = x.Item, owner = x.Owner }).ToArray();
            observations.Add(new
            {
                kind = "world-drop-new-item",
                packet = ItemDropPacket,
                frameHex = Convert.ToHexString(newWorldItem),
                sentinel = maxWorldItems,
                created,
                qaItemDropEvents = worldItemEventAfter - worldItemEventBefore,
                peerInventoryBroadcastDelta = observer.InventoryUpdates.Count - worldInventoryBefore,
                itemOwnerPackets = new
                {
                    actorPacketCount = actor.ItemOwnerUpdates.Count - actorOwnerBefore,
                    observerPacketCount = observer.ItemOwnerUpdates.Count - observerOwnerBefore,
                    actor = actorOwnerPackets,
                    observer = observerOwnerPackets,
                    meaning = "bounded server-to-client packet22 observation; presence is not creator proof"
                },
                rule = worldRule,
                f08BlockObserved = worldClearBlocked,
                worldBefore = WorldItemCount(worldBefore),
                worldAfter = WorldItemCount(worldAfter),
                acquisitionAttribution = "new-item request reaches Bouncer/item-drop path; creator proof not established",
                sanction = "none"
            });

            // The explicit TestLab candidate is intentionally a separate terminal branch. It
            // exercises only a new packet21 allocation with a valid item type and a stack above
            // that type's audited maximum. The legal 9999 item remains live as the control; the
            // existing packet90/update and packet21 type-zero pickup paths are not reclassified.
            if (itemDropSanctionCandidate)
            {
                var candidateBefore = await host.FixtureSnapshot();
                int candidateEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
                int candidateRawBefore = CountRawFrames(host.ReportDirectory, ItemDropPacket, actor.Slot);
                int candidatePeerWorldBefore = observer.WorldItemUpdates.Count;
                int candidateLogStart = host.ConsoleLines().Length;
                byte[] impossibleFrame = WorldItemFrame(ItemDropPacket, (short)maxWorldItems, spawn.X + 40f, spawn.Y,
                    0f, 0f, checked((short)(MaxStack + 1)), DirtBlock);
                await actor.SendBatch(impossibleFrame);
                await Task.Delay(350);
                await observer.Drain(TimeSpan.FromMilliseconds(150));
                var candidateAfter = await host.FixtureSnapshot();
                var candidateConsole = host.ConsoleLines().Skip(candidateLogStart).ToArray();
                string? candidateRule = candidateConsole.FirstOrDefault(x =>
                    x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                    x.Contains("rule=B1.ItemStructure", StringComparison.Ordinal) &&
                    x.Contains("packet=21", StringComparison.Ordinal));
                long actorAccount = Scalar(database, "SELECT ID FROM Users WHERE Username=$name", actor.Name);
                Check(CountRawFrames(host.ReportDirectory, ItemDropPacket, actor.Slot) > candidateRawBefore,
                    "over-max-world-drop-reaches-real-raw-input-hook");
                Check(CountQaEvents(host.ReportDirectory, "item-drop-packet") == candidateEventBefore,
                    "over-max-world-drop-is-canceled-before-native-item-drop-event");
                Check(candidateRule is not null &&
                    candidateRule.Contains("verdict=ProvenCheat", StringComparison.Ordinal) &&
                    candidateRule.Contains("reason=world-drop-stack-impossible-first-sanction-candidate", StringComparison.Ordinal) &&
                    candidateRule.Contains("action=Block", StringComparison.Ordinal) &&
                    candidateRule.Contains("canceled=True", StringComparison.OrdinalIgnoreCase),
                    "over-max-world-drop-is-written-as-complete-first-sanction-candidate");
                Check(candidateConsole.Count(x => x.Contains("ANTICHEAT_INCIDENT", StringComparison.Ordinal) &&
                    x.Contains("rule=B1.ItemStructure", StringComparison.Ordinal) &&
                    x.Contains("accountId=" + actorAccount + " ") &&
                    x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                    x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1,
                    "over-max-world-drop-revokes-one-attributed-session");
                Check(!TryFindWorldItem(candidateAfter, DirtBlock, MaxStack + 1, out _),
                    "over-max-world-drop-does-not-create-a-live-native-item");
                Check(TryFindWorldItem(candidateAfter, createdIndex, DirtBlock, MaxStack, out _),
                    "over-max-world-drop-leaves-the-legal-9999-control-item-untouched");
                Check(observer.WorldItemUpdates.Skip(candidatePeerWorldBefore).All(x =>
                    x.Type != DirtBlock || x.Stack != MaxStack + 1),
                    "over-max-world-drop-has-no-peer-item-broadcast");
                Check(Healthy(observer, database), "over-max-world-drop-does-not-sanction-the-innocent-observer");
                await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed,
                    TimeSpan.FromSeconds(8));
                Check(actor.IsBanRejection == false && actor.DisconnectReason == "AntiCheat proven violation.",
                    "over-max-world-drop-disconnects-the-first-session");
                await Until(() => Scalar(database,
                    "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999",
                    "acc:" + actor.Name) == 1, "over-max-world-drop-persists-one-account-ban");
                string journal = Path.Combine(run, "tshock", "anticheat", "enforcement");
                await Until(() => AppliedIntentCount(journal, actorAccount, "B1.ItemStructure") == 1,
                    "over-max-world-drop-journal-intent-applied");
                Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 1,
                    "over-max-world-drop-has-no-duplicate-or-innocent-ban");
                observations.Add(new
                {
                    kind = "world-drop-stack-first-sanction-candidate",
                    packet = ItemDropPacket,
                    frameHex = Convert.ToHexString(impossibleFrame),
                    sentinel = maxWorldItems,
                    item = DirtBlock,
                    stack = MaxStack + 1,
                    maximumStack = MaxStack,
                    actorAccount,
                    before = new { worldItems = WorldItemCount(candidateBefore), legalItemPresent = TryFindWorldItem(candidateBefore, createdIndex, DirtBlock, MaxStack, out _) },
                    after = new { worldItems = WorldItemCount(candidateAfter), impossibleItemPresent = TryFindWorldItem(candidateAfter, DirtBlock, MaxStack + 1, out _) },
                    rule = candidateRule,
                    console = candidateConsole.Where(x => x.Contains("B1.ItemStructure", StringComparison.Ordinal) ||
                        x.Contains("ANTICHEAT_INCIDENT", StringComparison.Ordinal)).Take(12).ToArray(),
                    proof = "authenticated current actor, packet21 new-item sentinel, valid item definition, stack greater than target maximum, initial/authorized paths excluded",
                    enforcement = "first session rejection, native pre-write cancellation, durable journal/account ban; external formal qualification remains pending"
                });
                host.RecordProvenAccount(actor.Name, actorAccount, "B1.ItemStructure");
                await actor.DisposeAsync();
                await Task.Delay(200);

                var connectWithIdentity = host.ConnectWithIdentity ??
                    throw new InvalidOperationException("M18 item-drop sanction validation requires identity-preserving lab reconnect.");
                var immediate = await connectWithIdentity(actor.Name, actor.Uuid);
                await ExpectBanRejection(immediate);
                Check(immediate.IsBanRejection && immediate.LoginAttempts <= 1,
                    "over-max-world-drop-immediate-same-identity-reconnect-rejected");
                await immediate.DisposeAsync();
                await observer.DisposeAsync();
                var restart = host.RestartServer ??
                    throw new InvalidOperationException("M18 item-drop sanction validation requires the existing lab restart lifecycle.");
                await restart();
                var restoredObserver = await connectWithIdentity(observer.Name, observer.Uuid);
                await restoredObserver.Join(); await restoredObserver.Login();
                Check(restoredObserver.Authenticated && !restoredObserver.Closed && restoredObserver.SscSlots.Count >= 350,
                    "over-max-world-drop-innocent-observer-restored-after-restart");
                var restartedActor = await connectWithIdentity(actor.Name, actor.Uuid);
                await ExpectBanRejection(restartedActor);
                Check(restartedActor.IsBanRejection && restartedActor.LoginAttempts <= 1,
                    "over-max-world-drop-same-identity-reconnect-rejected-after-restart");
                await restartedActor.DisposeAsync();
                Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999",
                    "acc:" + actor.Name) == 1 && Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 1,
                    "over-max-world-drop-persistent-ban-remains-single-after-restart");
                observations.Add(new
                {
                    kind = "world-drop-stack-sanction-reconnect-restart",
                    account = actor.Name,
                    actorAccount,
                    immediateReconnect = new { rejected = immediate.IsBanRejection, loginAttempts = immediate.LoginAttempts },
                    restartReconnect = new { rejected = restartedActor.IsBanRejection, loginAttempts = restartedActor.LoginAttempts },
                    innocentObserver = new { restoredObserver.Authenticated, restoredObserver.SscSlots.Count },
                    enforcement = "first rejection, session revocation, durable journal/account ban, immediate rejection, clean restart, post-restart rejection"
                });
                await restoredObserver.DisposeAsync();
                await WriteEvidence(report, run, observations, "passed", "", itemDropSanctionCandidate);
                status = "passed";
                return;
            }

            // Update the same native world-item slot through the target's
            // UpdateItemDrop (90) request. The observer must see the native
            // update broadcast; the sender is intentionally not used as the
            // pickup witness in the next step.
            float updateX = spawn.X + 8f, updateY = spawn.Y;
            int updateEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            int updatePeerBefore = observer.WorldItemUpdates.Count;
            int updateLogStart = host.ConsoleLines().Length;
            byte[] updateFrame = WorldItemFrame(UpdateItemDropPacket, (short)createdIndex, updateX, updateY,
                0f, 0f, MaxStack, DirtBlock);
            await actor.SendBatch(updateFrame);
            await actor.PingAsync(); await observer.PingAsync();
            var worldUpdated = await WaitSnapshot("world-after-update-item", snapshot =>
                TryFindWorldItem(snapshot, createdIndex, DirtBlock, MaxStack, out var item) &&
                Math.Abs(item.GetProperty("x").GetSingle() - updateX) < 128f &&
                Math.Abs(item.GetProperty("y").GetSingle() - updateY) < 128f);
            JsonElement updated;
            Check(TryFindWorldItem(worldUpdated, createdIndex, DirtBlock, MaxStack, out updated),
                "9999-world-drop-update-keeps-live-item");
            int updateEventAfter = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            string? updateRule = host.ConsoleLines().Skip(updateLogStart).FirstOrDefault(x =>
                x.Contains("ANTICHEAT_M18_P0D_ITEM_RULE", StringComparison.Ordinal) &&
                x.Contains("packet=90", StringComparison.Ordinal));
            Check(updateEventAfter > updateEventBefore, "packet90-update-reaches-tshock-item-drop-hook");
            bool peerSawUpdate = observer.WorldItemUpdates.Skip(updatePeerBefore).Any(x =>
                x.Packet == UpdateItemDropPacket && x.Item == createdIndex &&
                x.Stack == MaxStack && x.Type == DirtBlock);
            Check(updateRule is not null && updateRule.Contains("rule=B1.ItemStructure", StringComparison.Ordinal) &&
                updateRule.Contains("verdict=Pass", StringComparison.Ordinal) &&
                updateRule.Contains("resultAction=Pass", StringComparison.Ordinal),
                "packet90-legal-max-stack-update-is-not-blocked");
            Check(Healthy(actor, database), "packet90-update-keeps-creator-healthy");
            observations.Add(new
            {
                kind = "world-drop-update",
                packet = UpdateItemDropPacket,
                frameHex = Convert.ToHexString(updateFrame),
                index = createdIndex,
                requested = new { x = updateX, y = updateY, stack = MaxStack, type = DirtBlock },
                updated,
                qaItemDropEvents = updateEventAfter - updateEventBefore,
                peerUpdateBroadcastObserved = peerSawUpdate,
                peerWorldItemUpdates = observer.WorldItemUpdates.Skip(updatePeerBefore)
                    .Select(x => new { packet = x.Packet, item = x.Item, stack = x.Stack, type = x.Type }).ToArray(),
                rule = updateRule,
                relayBoundary = "This target run emitted no packet90 to the independent peer; state-update success is not upgraded to a peer-relay claim.",
                sanction = "none"
            });

            // Use the other authenticated player as the pickup witness. This
            // keeps the passive/other-player pickup boundary explicit: the
            // creator remains a separate, already-full account. Movement is
            // recorded first, then the real client-to-server ItemDrop pickup
            // transaction is sent. The local M5 regression identifies this
            // transaction as packet21 with Type=0 and Stacks=0. Vanilla's
            // local pickup mutates the client's inventory before this request;
            // the adapter therefore sends a second, explicit packet5 frame to
            // persist that client state in SSC. These two stages must not be
            // conflated into a claim that packet21 alone returned the item.
            Check(!HasAnyPlayerItem(worldUpdated, observer.Name, DirtBlock, MaxStack),
                "other-player-pickup-slot-starts-empty");
            int pickupInventoryBefore = observer.InventoryUpdates.Count;
            int pickupOwnerBefore = observer.PacketCount(ItemOwnerPacket);
            int pickupOwnerUpdatesBefore = observer.ItemOwnerUpdates.Count;
            int pickupEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            int pickupControlFrameBefore = CountRawFrames(host.ReportDirectory, PlayerControlsPacket, observer.Slot);
            int pickupControlPacketBefore = observer.PacketCount(PlayerControlsPacket);
            int pickupControlBefore = observer.SentPacketCount;
            int pickupLogStart = host.ConsoleLines().Length;
            float pickupX = updated.GetProperty("x").GetSingle();
            float pickupY = updated.GetProperty("y").GetSingle() - 20f;
            await observer.MoveTo(pickupX, pickupY);
            await observer.PingAsync();
            byte[] pickupRequestFrame = WorldItemFrame(ItemDropPacket, (short)createdIndex,
                updated.GetProperty("x").GetSingle(), updated.GetProperty("y").GetSingle(),
                0f, 0f, 0, 0);
            await observer.SendBatch(pickupRequestFrame);
            await observer.PingAsync();
            var worldAfterPickup = await WaitSnapshot("world-after-other-player-pickup", snapshot =>
                !TryFindWorldItem(snapshot, createdIndex, DirtBlock, MaxStack, out _));
            Check(!TryFindWorldItem(worldAfterPickup, createdIndex, DirtBlock, MaxStack, out _),
                "other-player-pickup-removes-world-item");
            Check(!HasAnyPlayerItem(worldAfterPickup, observer.Name, DirtBlock, MaxStack),
                "packet21-world-removal-has-no-implicit-ssc-inventory-state");
            int pickupControlFrameAfter = CountRawFrames(host.ReportDirectory, PlayerControlsPacket, observer.Slot);
            Check(observer.SentPacketCount - pickupControlBefore >= 2 &&
                pickupControlFrameAfter > pickupControlFrameBefore,
                "other-player-pickup-uses-real-player-controls");
            int pickupEventAfter = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            string? pickupRule = host.ConsoleLines().Skip(pickupLogStart).FirstOrDefault(x =>
                x.Contains("ANTICHEAT_M18_P0D_ITEM_RULE", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal));
            string? pickupClearRule = host.ConsoleLines().Skip(pickupLogStart).FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal));
            Check(pickupEventAfter > pickupEventBefore,
                "other-player-pickup-reaches-tshock-item-drop-hook");
            Check(pickupClearRule is not null &&
                pickupClearRule.Contains("verdict=Pass", StringComparison.Ordinal) &&
                pickupClearRule.Contains("reason=normal-ground-item-pickup-observed", StringComparison.Ordinal) &&
                pickupClearRule.Contains("action=Pass", StringComparison.Ordinal) &&
                pickupClearRule.Contains("canceled=False", StringComparison.OrdinalIgnoreCase),
                "normal-ground-pickup-is-not-blanket-blocked-by-f08");

            int pickupInventorySlot = inventorySlot;
            Check(!HasPlayerItem(worldAfterPickup, observer.Name, pickupInventorySlot, DirtBlock, MaxStack),
                "other-player-pickup-ssc-slot-is-empty-before-explicit-sync");
            int pickupSlotEventBefore = CountQaEvents(host.ReportDirectory, "player-slot-packet");
            int pickupSlotPeerBefore = actor.InventoryUpdates.Count;
            int pickupSlotLogStart = host.ConsoleLines().Length;
            byte[] pickupInventoryFrame = InventoryFrame(observer.Slot, pickupInventorySlot, MaxStack, DirtBlock);
            await observer.SendBatch(pickupInventoryFrame);
            await observer.PingAsync();
            var worldAfterPickupSsc = await WaitSnapshot("world-after-other-player-pickup-ssc-sync", snapshot =>
                HasPlayerItem(snapshot, observer.Name, pickupInventorySlot, DirtBlock, MaxStack, out _) &&
                !TryFindWorldItem(snapshot, createdIndex, DirtBlock, MaxStack, out _));
            JsonElement pickupItem;
            Check(HasPlayerItem(worldAfterPickupSsc, observer.Name, pickupInventorySlot, DirtBlock, MaxStack,
                out pickupItem), "other-player-pickup-ssc-state-after-explicit-slot-sync");
            int pickupSlotEventAfter = CountQaEvents(host.ReportDirectory, "player-slot-packet");
            string? pickupSlotRule = host.ConsoleLines().Skip(pickupSlotLogStart).FirstOrDefault(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=B1.ItemStructure", StringComparison.Ordinal) &&
                x.Contains("packet=5", StringComparison.Ordinal));
            Check(pickupSlotEventAfter > pickupSlotEventBefore,
                "other-player-pickup-ssc-sync-reaches-player-slot-hook");
            Check(pickupSlotRule is not null && pickupSlotRule.Contains("verdict=Pass", StringComparison.Ordinal) &&
                pickupSlotRule.Contains("action=Pass", StringComparison.Ordinal) &&
                pickupSlotRule.Contains("canceled=False", StringComparison.Ordinal),
                "other-player-pickup-ssc-sync-is-not-blocked");
            Check(Healthy(observer, database) && Healthy(actor, database),
                "other-player-pickup-keeps-both-accounts-healthy");
            observations.Add(new
            {
                kind = "other-player-pickup-return",
                movementPacket = PlayerControlsPacket,
                packet = ItemDropPacket,
                frameHex = Convert.ToHexString(pickupRequestFrame),
                itemIndex = createdIndex,
                fixtureInventorySlot = inventorySlot,
                pickupTarget = new { x = pickupX, y = pickupY },
                worldAfterPacket21 = new
                {
                    worldItemRemoved = !TryFindWorldItem(worldAfterPickup, createdIndex, DirtBlock, MaxStack, out _),
                    observerHasItem = HasAnyPlayerItem(worldAfterPickup, observer.Name, DirtBlock, MaxStack),
                    serverState = "packet21-processed; no implicit SSC inventory item"
                },
                pickupItem,
                sentPacketDelta = observer.SentPacketCount - pickupControlBefore,
                qaPlayerControlFrames = pickupControlFrameAfter - pickupControlFrameBefore,
                receivedPlayerControlPackets = observer.PacketCount(PlayerControlsPacket) - pickupControlPacketBefore,
                qaItemDropEvents = pickupEventAfter - pickupEventBefore,
                pickupInventoryUpdates = observer.InventoryUpdates.Skip(pickupInventoryBefore)
                    .Select(x => new { player = x.Player, slot = x.Slot, item = x.Item, stack = x.Stack, prefix = x.Prefix }).ToArray(),
                itemOwnerPacketDelta = observer.PacketCount(ItemOwnerPacket) - pickupOwnerBefore,
                itemOwnerPackets = observer.ItemOwnerUpdates.Skip(pickupOwnerUpdatesBefore)
                    .Where(x => x.Item == createdIndex)
                    .Select(x => new { item = x.Item, owner = x.Owner }).ToArray(),
                 rule = pickupRule,
                 groundClearRule = pickupClearRule,
                pickupTransaction = new { id = createdIndex, type = 0, stacks = 0 },
                sscSync = new
                {
                    packet = PlayerSlotPacket,
                    frameHex = Convert.ToHexString(pickupInventoryFrame),
                    slot = pickupInventorySlot,
                    item = DirtBlock,
                    stack = MaxStack,
                    after = pickupItem,
                    qaPlayerSlotEvents = pickupSlotEventAfter - pickupSlotEventBefore,
                    peerInventoryBroadcasts = actor.InventoryUpdates.Count - pickupSlotPeerBefore,
                    rule = pickupSlotRule,
                    boundary = "explicit client-state packet5 persistence after packet21; not GUI evidence"
                },
                attributionBoundary = "other-player pickup is observed as a return path, not creator proof or a sanction cause",
                sanction = "none"
            });

            // Create a real server-owned batch so the next two operations can
            // be separated: two concentrated, coordinate-aligned normal
            // pickups, followed by the complete WipeGroundItems request shape
            // over every remaining live item. The batch is not itself proof;
            // it only gives both clients the same native world-item state.
            await observer.MoveTo(pickupX, pickupY);
            await observer.PingAsync();
            await host.ConsoleCommand("qa_m18_wipe_items " + observer.Name);
            string wipeMarkerPath = Path.Combine(run, "qa-m18-wipe-items-" + observer.Slot + ".json");
            await Until(() => File.Exists(wipeMarkerPath), "m18-wipe-items-control-marker");
            using var wipeMarkerDocument = JsonDocument.Parse(await File.ReadAllTextAsync(wipeMarkerPath));
            var wipeFixtureItems = wipeMarkerDocument.RootElement.GetProperty("created")
                .EnumerateArray().Select(x => x.Clone()).ToArray();
            Check(wipeFixtureItems.Length >= 5, "wipe-ground-items-native-batch-has-five-live-control-items");
            await observer.WaitUntil(() => wipeFixtureItems.All(item => observer.WorldItemUpdates.Any(update =>
                update.Packet == ItemDropPacket && update.Item == item.GetProperty("index").GetInt32() &&
                update.Type == item.GetProperty("type").GetInt32() &&
                update.Stack == item.GetProperty("stack").GetInt32())), TimeSpan.FromSeconds(8));
            var wipePrepared = await WaitSnapshot("wipe-items-prepared", snapshot =>
                wipeFixtureItems.All(item => TryFindWorldItem(snapshot,
                    item.GetProperty("index").GetInt32(), item.GetProperty("type").GetInt32(),
                    item.GetProperty("stack").GetInt32(), out _)));

            // A player is near, but not at, the item position (the native
            // pickup uses the player's actual position). This keeps the
            // normal path distinct from WipeGroundItems' exact target control.
            var concentratedItems = wipeFixtureItems.Take(2).ToArray();
            int concentratedEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            int concentratedRawBefore = CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot);
            int concentratedLogStart = host.ConsoleLines().Length;
            var concentratedFrames = new List<string>();
            foreach (var item in concentratedItems)
            {
                float itemX = item.GetProperty("position").GetProperty("x").GetSingle();
                float itemY = item.GetProperty("position").GetProperty("y").GetSingle();
                await observer.MoveTo(itemX, itemY - 20f);
                byte[] pickupFrame = WorldItemFrame(ItemDropPacket,
                    (short)item.GetProperty("index").GetInt32(), itemX, itemY,
                    item.GetProperty("velocity").GetProperty("x").GetSingle(),
                    item.GetProperty("velocity").GetProperty("y").GetSingle(), 0, 0);
                concentratedFrames.Add(Convert.ToHexString(pickupFrame));
                await observer.SendBatch(pickupFrame);
                await observer.PingAsync();
                await WaitSnapshot("concentrated-normal-pickup-" + item.GetProperty("index").GetInt32(), snapshot =>
                    !TryFindWorldItem(snapshot, item.GetProperty("index").GetInt32(),
                        item.GetProperty("type").GetInt32(), item.GetProperty("stack").GetInt32(), out _));
            }
            var concentratedAfter = await host.FixtureSnapshot();
            var concentratedConsole = host.ConsoleLines().Skip(concentratedLogStart).ToArray();
            var concentratedRules = concentratedConsole.Where(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal)).ToArray();
            Check(CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot) - concentratedRawBefore == concentratedItems.Length,
                "concentrated-normal-pickups-reach-the-real-packet21-hook");
            Check(CountQaEvents(host.ReportDirectory, "item-drop-packet") - concentratedEventBefore == concentratedItems.Length,
                "concentrated-normal-pickups-reach-native-item-drop-events");
            Check(concentratedRules.Length >= concentratedItems.Length &&
                concentratedRules.All(x => x.Contains("verdict=Pass", StringComparison.Ordinal) &&
                    x.Contains("reason=normal-ground-item-pickup-observed", StringComparison.Ordinal) &&
                    x.Contains("action=Pass", StringComparison.Ordinal) &&
                    x.Contains("canceled=False", StringComparison.OrdinalIgnoreCase)),
                "concentrated-normal-pickups-are-not-blanket-blocked-by-f08");
            Check(concentratedItems.All(item => !TryFindWorldItem(concentratedAfter,
                item.GetProperty("index").GetInt32(), item.GetProperty("type").GetInt32(),
                item.GetProperty("stack").GetInt32(), out _)),
                "concentrated-normal-pickups-remove-only-their-own-world-items");
            observations.Add(new
            {
                kind = "concentrated-normal-pickup-control",
                packetSequence = "13(player-near-item) -> 21(type=0, accurate item coordinates)",
                itemCount = concentratedItems.Length,
                items = concentratedItems,
                frameHex = concentratedFrames,
                rules = concentratedRules,
                rawPacket21Delta = CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot) - concentratedRawBefore,
                qaItemDropEventDelta = CountQaEvents(host.ReportDirectory, "item-drop-packet") - concentratedEventBefore,
                serverAftermath = "only the two concentrated pickup items were removed; remaining live items were retained for WipeGroundItems",
                sanction = "none"
            });

            var wipeBefore = await host.FixtureSnapshot();
            var wipeItems = wipeBefore.GetProperty("worldItems").EnumerateArray()
                .Where(item => item.GetProperty("index").GetInt32() < 400 &&
                    item.GetProperty("active").GetBoolean() && item.GetProperty("type").GetInt32() > 0 &&
                    item.GetProperty("stack").GetInt32() > 0 &&
                    !item.GetProperty("beingGrabbed").GetBoolean())
                .Select(item => item.Clone()).ToArray();
            Check(wipeItems.Length >= 4, "wipe-ground-items-sees-the-original-and-remaining-live-batch");
            Check(wipeItems.All(item => item.GetProperty("index").GetInt32() < 400),
                "wipe-ground-items-candidate-items-are-within-the-locked-400-slot-scan");
            float wipeReturnX = concentratedItems[^1].GetProperty("position").GetProperty("x").GetSingle();
            float wipeReturnY = concentratedItems[^1].GetProperty("position").GetProperty("y").GetSingle() - 20f;
            await observer.MoveTo(wipeReturnX, wipeReturnY);
            await observer.PingAsync();
            int wipeRawBefore = CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot);
            int wipeEventBefore = CountQaEvents(host.ReportDirectory, "item-drop-packet");
            int wipePeerBefore = actor.WorldItemUpdates.Count;
            int wipeLogStart = host.ConsoleLines().Length;
            var wipeFrames = new List<byte[]>(wipeItems.Length * 3);
            var wipePayloads = new List<object>(wipeItems.Length);
            foreach (var item in wipeItems)
            {
                int index = item.GetProperty("index").GetInt32();
                float itemX = item.GetProperty("x").GetSingle();
                float itemY = item.GetProperty("y").GetSingle();
                float velocityX = item.GetProperty("vx").GetSingle();
                float velocityY = item.GetProperty("vy").GetSingle();
                wipeFrames.Add(PlayerControlsFrame(observer.Slot, itemX, itemY));
                // WorldItem.TurnToAir is performed before NetMessage.SendData
                // in the locked source. The target serializer therefore sees
                // the original position/velocity but stack=0, prefix=0 and
                // inactive netID/type=0.
                byte[] finalPayload = WorldItemFrame(ItemDropPacket, (short)index, itemX, itemY,
                    velocityX, velocityY, 0, 0);
                wipeFrames.Add(finalPayload);
                wipeFrames.Add(PlayerControlsFrame(observer.Slot, wipeReturnX, wipeReturnY));
                wipePayloads.Add(new
                {
                    index,
                    preTurnToAir = new
                    {
                        active = item.GetProperty("active").GetBoolean(),
                        type = item.GetProperty("type").GetInt32(),
                        stack = item.GetProperty("stack").GetInt32(),
                        x = itemX,
                        y = itemY,
                        vx = velocityX,
                        vy = velocityY
                    },
                    finalPacket21 = new
                    {
                        type = 0,
                        stack = 0,
                        prefix = 0,
                        x = itemX,
                        y = itemY,
                        vx = velocityX,
                        vy = velocityY,
                        frameHex = Convert.ToHexString(finalPayload)
                    },
                    controlPositions = new[]
                    {
                        new { x = itemX, y = itemY, role = "target" },
                        new { x = wipeReturnX, y = wipeReturnY, role = "restore" }
                    }
                });
            }
            await observer.SendBatch(wipeFrames.ToArray());
            await observer.PingAsync();
            await Until(() => host.ConsoleLines().Skip(wipeLogStart).Count(x =>
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal)) >= wipeItems.Length,
                "wipe-ground-items-all-final-packet21-decisions-recorded");
            await Task.Delay(250);
            var wipeAfter = await host.FixtureSnapshot();
            var wipeConsole = host.ConsoleLines().Skip(wipeLogStart).ToArray();
            var wipeRules = wipeConsole.Where(x =>
                x.Contains("ANTICHEAT_RULE_INPUT", StringComparison.Ordinal) &&
                x.Contains("rule=F08.GroundItemClearBoundedGuard", StringComparison.Ordinal) &&
                x.Contains("packet=21", StringComparison.Ordinal)).ToArray();
            string? firstWipeRule = wipeRules.FirstOrDefault();
            Check(CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot) - wipeRawBefore == wipeItems.Length,
                "wipe-ground-items-sends-one-final-packet21-per-live-item");
            Check(CountQaEvents(host.ReportDirectory, "item-drop-packet") == wipeEventBefore,
                "wipe-ground-items-is-canceled-before-native-item-drop-events");
            Check(wipeRules.Length >= wipeItems.Length && wipeRules.All(x =>
                x.Contains("verdict=ResourceAbuse", StringComparison.Ordinal) &&
                x.Contains("reason=ground-item-clear-wipe-sequence-stop-loss", StringComparison.Ordinal) &&
                x.Contains("action=Block", StringComparison.Ordinal) &&
                x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase)),
                "wipe-ground-items-final-payload-is-stopped-by-the-f08-sequence-guard");
            Check(wipeItems.All(item => TryFindWorldItem(wipeAfter,
                item.GetProperty("index").GetInt32(), item.GetProperty("type").GetInt32(),
                item.GetProperty("stack").GetInt32(), out _)),
                "wipe-ground-items-does-not-remove-server-world-items");
            Check(!actor.WorldItemUpdates.Skip(wipePeerBefore).Any(x =>
                x.Packet == ItemDropPacket && x.Stack == 0 && x.Type == 0),
                "wipe-ground-items-has-no-peer-type-zero-removal-broadcast");
            Check(firstWipeRule is not null && firstWipeRule.Contains("alreadyCanceled=False", StringComparison.OrdinalIgnoreCase) &&
                !observer.Closed && observer.DisconnectReason is null &&
                Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "wipe-ground-items-first-rejection-has-zero-server-loss-and-no-sanction");
            Check(Healthy(observer, database) && Healthy(actor, database),
                "wipe-ground-items-keeps-both-accounts-healthy");
            observations.Add(new
            {
                kind = "wipe-ground-items-source-sequence-protection",
                sourceContract = "TerraAngel Tools/Developer/WipeGroundItems.cs",
                scanLimit = 400,
                packetSequence = "for each live item: TurnToAir -> 13(item.position) -> 21(post-TurnToAir payload) -> 13(Main.LocalPlayer.position)",
                itemCount = wipeItems.Length,
                items = wipePayloads,
                frameHex = wipeFrames.Select(Convert.ToHexString).ToArray(),
                before = new { worldItems = WorldItemCount(wipeBefore), liveCandidates = wipeItems.Length },
                after = new { worldItems = WorldItemCount(wipeAfter), retainedCandidates = wipeItems.Count(item =>
                    TryFindWorldItem(wipeAfter, item.GetProperty("index").GetInt32(),
                        item.GetProperty("type").GetInt32(), item.GetProperty("stack").GetInt32(), out _)) },
                rawPacket21Delta = CountRawFrames(host.ReportDirectory, ItemDropPacket, observer.Slot) - wipeRawBefore,
                qaItemDropEventDelta = CountQaEvents(host.ReportDirectory, "item-drop-packet") - wipeEventBefore,
                firstRejection = new
                {
                    rule = firstWipeRule,
                    serverLossBeforeRejection = 0,
                    nativeEventBeforeRejection = wipeEventBefore,
                    nativeEventAfter = CountQaEvents(host.ReportDirectory, "item-drop-packet"),
                    peerTypeZeroRemovalBroadcast = false
                },
                connectionAftermath = new { observer.Authenticated, observer.Closed, observer.DisconnectReason },
                permanentSanctions = Scalar(database, "SELECT COUNT(*) FROM PlayerBans"),
                interceptedBy = "F08.GroundItemClearBoundedGuard / M18GroundItemClearSequenceTracker",
                penalty = "connection stop-loss only; no account sanction"
            });

            await WriteEvidence(report, run, observations, "passed", "", itemDropSanctionCandidate);
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteEvidence(report, run, observations, "failed", failure, itemDropSanctionCandidate);
            throw;
        }
        finally
        {
            await CopyRelevantEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-d-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, itemDropSanctionCandidate, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-d:" + name);

        async Task<JsonElement> WaitSnapshot(string label, Func<JsonElement, bool> predicate)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            JsonElement last = default;
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var snapshot = await host.FixtureSnapshot();
                last = snapshot;
                if (predicate(snapshot))
                {
                    await WriteSnapshot(report, label, snapshot);
                    return snapshot;
                }
                await Task.Delay(100);
            }
            if (last.ValueKind != JsonValueKind.Undefined)
                await WriteSnapshot(report, label + "-timeout", last);
            throw new TimeoutException("M18 item snapshot timed out: " + label);
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

    private static byte[] InventoryFrame(byte owner, int slot, short stack, short item)
        => LabClient.Packet(PlayerSlotPacket, writer =>
        {
            writer.Write(owner); writer.Write((short)slot); writer.Write(stack);
            writer.Write((byte)0); writer.Write(item); writer.Write((byte)0);
        });

    private static byte[] WorldItemFrame(byte packet, short id, float x, float y, float velocityX,
        float velocityY, short stack, short type, byte flags = 0)
        => LabClient.Packet(packet, writer =>
        {
            writer.Write(id); writer.Write(x); writer.Write(y);
            writer.Write(velocityX); writer.Write(velocityY); writer.Write(stack);
            writer.Write((byte)0); writer.Write(flags); writer.Write(type);
        });

    private static byte[] PlayerControlsFrame(byte playerSlot, float x, float y)
        => LabClient.Packet(PlayerControlsPacket, writer =>
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

    private static JsonElement Player(JsonElement snapshot, string name)
        => snapshot.GetProperty("players").EnumerateArray()
            .Single(x => x.GetProperty("Name").GetString() == name);

    private static bool TryFindWorldItem(JsonElement snapshot, int type, int stack, out JsonElement item)
        => TryFindWorldItem(snapshot, -1, type, stack, out item);

    private static bool TryFindWorldItem(JsonElement snapshot, int index, int type, int stack, out JsonElement item)
    {
        foreach (var candidate in snapshot.GetProperty("worldItems").EnumerateArray())
        {
            if ((index < 0 || candidate.GetProperty("index").GetInt32() == index) &&
                candidate.GetProperty("active").GetBoolean() &&
                candidate.GetProperty("type").GetInt32() == type &&
                candidate.GetProperty("stack").GetInt32() == stack)
            {
                item = candidate.Clone();
                return true;
            }
        }
        item = default;
        return false;
    }

    private static bool HasPlayerItem(JsonElement snapshot, string name, int slot, int type, int stack)
        => HasPlayerItem(snapshot, name, slot, type, stack, out _);

    private static bool HasPlayerItem(JsonElement snapshot, string name, int slot, int type, int stack,
        out JsonElement item)
    {
        foreach (var candidate in Player(snapshot, name).GetProperty("inventory").EnumerateArray())
        {
            if (candidate.GetProperty("slot").GetInt32() == slot &&
                candidate.GetProperty("type").GetInt32() == type &&
                candidate.GetProperty("stack").GetInt32() == stack)
            {
                item = candidate.Clone();
                return true;
            }
        }
        item = default;
        return false;
    }

    private static bool HasAnyPlayerItem(JsonElement snapshot, string name, int type, int stack)
        => HasAnyPlayerItem(snapshot, name, type, stack, out _);

    private static bool HasAnyPlayerItem(JsonElement snapshot, string name, int type, int stack,
        out JsonElement item)
    {
        foreach (var candidate in Player(snapshot, name).GetProperty("inventory").EnumerateArray())
        {
            if (candidate.GetProperty("type").GetInt32() == type &&
                candidate.GetProperty("stack").GetInt32() == stack)
            {
                item = candidate.Clone();
                return true;
            }
        }
        item = default;
        return false;
    }

    private static int WorldItemCount(JsonElement snapshot)
        => snapshot.GetProperty("worldItems").GetArrayLength();

    private static bool Healthy(LabClient actor, string database)
        => actor.Authenticated && !actor.Closed && actor.DisconnectReason is null &&
            Scalar(database, "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0;

    private static int CountQaEvents(string report, string kind)
    {
        string needle = "\"kind\":\"" + kind + "\"";
        return Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(ReadSharedLines)
            .Count(line => line.Contains(needle, StringComparison.Ordinal));
    }

    private static int CountRawFrames(string report, byte packetId, byte playerIndex)
    {
        int count = 0;
        foreach (string line in Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(ReadSharedLines))
        {
            if (!line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var payload = document.RootElement.GetProperty("payload");
                if (payload.GetProperty("packetId").GetByte() == packetId &&
                    payload.GetProperty("playerIndex").GetByte() == playerIndex) count++;
            }
            catch (JsonException)
            {
                // The server may be appending the current final line. A
                // partial line is not evidence of a received frame and is
                // ignored until the next bounded read.
            }
        }
        return count;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static async Task WriteSnapshot(string report, string label, JsonElement snapshot)
        => await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), snapshot.GetRawText());

    private static async Task WriteEvidence(string report, string run, IReadOnlyList<object> observations,
        string status, string failure = "", bool itemDropSanctionCandidate = false)
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-d-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / packet5 SyncEquipment, packet13 PlayerControls, packet21 ItemDrop and packet90 UpdateItemDrop",
                source = "TerraAngel ItemSpawner.SpawnItemInMouse/SpawnItemInWorld -> SyncEquipment/SyncItem; target SSC, Bouncer and native pickup paths",
                observations,
                boundary = itemDropSanctionCandidate
                    ? "TestLab-only first-sanction candidate for authenticated packet21 new-item allocation with stack above the target definition; formal qualification and production enablement remain pending"
                    : "9999 is checked against the target item definition; legal maxStack does not establish creator proof; no permanent sanction",
                excluded = itemDropSanctionCandidate
                    ? "GUI/client-tool success, complete external creator provenance, formal qualification and production enablement are not claimed"
                    : "GUI/client-tool success, complete creator provenance, permanent attribution sanction and packet22 SSC acknowledgement are not claimed"
            }, Json));

    private static async Task CopyRelevantEvents(string report, string destination)
    {
        string[] lines = Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("\"kind\":\"player-slot-packet\"", StringComparison.Ordinal) ||
                line.Contains("\"kind\":\"item-drop-packet\"", StringComparison.Ordinal) ||
                line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal))
            .Take(2048)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(destination, "qa-item-events.jsonl"), lines);
    }

    private static long Scalar(string database, string sql, params string[] values)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$name", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$name", values.ElementAtOrDefault(0));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static int AppliedIntentCount(string journal, long account, string ruleId)
    {
        if (!Directory.Exists(journal)) return 0;
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
                record.RootElement.GetProperty("Intent").GetProperty("Evidence").GetProperty("RuleId").GetString() == ruleId &&
                record.RootElement.GetProperty("Applied").GetBoolean())
                found++;
        }
        return found;
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
}
