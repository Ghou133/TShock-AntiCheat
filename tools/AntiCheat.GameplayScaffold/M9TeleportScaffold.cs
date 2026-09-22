using System.Reflection;
using System.Text.Json;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Mono.Cecil.Cil;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private sealed class TeleportWitness(TSPlayer player)
    {
        public TSPlayer Player { get; } = player;
        public long NativeEntries, Send65, Send96;
        public object? LastRelay;
        public long RawRequests, RawWitnessFaults;
        public TeleportRawPending? PendingRaw;
        public TeleportRawResult? LastRaw;
        public string? OriginalRodGroup;
        public string? AddedRodGroup;
    }
    private sealed record TeleportRawState(float X, float Y, float VelocityX, float VelocityY, int PortalColor);
    private sealed record TeleportRawPending(GetDataEventArgs Arguments, Player Player, long Sequence,
        int Thread, bool Handled, TeleportRawState State);
    private sealed record TeleportRawResult(long Sequence, int Packet, bool SamePlayer, bool SameThread,
        bool HandledBefore, bool HandledAfter, TeleportRawState Before, TeleportRawState After);
    private readonly Dictionary<int, TeleportWitness> teleportActors = new(4);
    private ILHook? teleportBodyWitness;

    private void PrepareTeleportWitness(string[] names)
    {
        Require(names.Length is > 0 and <= 4, "Use qa_m9_teleport <one to four ordinary actor names>.");
        Require(teleportActors.Count == 0, "Teleport witness is prepared once per owned run.");
        foreach (string name in names)
        {
            var actor = ResolvePlayer(name);
            Require(actor.IsLoggedIn && !actor.HasPermission("anticheat.bypass") &&
                !actor.HasPermission("compatibility.qa.console"), "Ordinary authenticated actor required.");
            teleportActors.Add(actor.Index, new(actor));
        }
        teleportBodyWitness = new(typeof(Player).GetMethod(nameof(Player.Teleport))!, il =>
        {
            var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<Player>>(player =>
            {
                if (teleportActors.TryGetValue(player.whoAmI, out var state) && ReferenceEquals(state.Player.TPlayer, player))
                    state.NativeEntries = TeleportIncrement(state.NativeEntries);
            });
        });
        HookEvents.Terraria.NetMessage.SendData += ObserveTeleportRelay;
        ServerApi.Hooks.NetGetData.Register(this, ObserveTeleportRawBefore, 2001);
        ServerApi.Hooks.NetGetData.Register(this, ObserveTeleportRawAfter, -1001);
        WriteTeleportState();
    }

    private static long TeleportIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private void SetTeleportRodPermission(string[] arguments)
    {
        Require(arguments.Length == 2 && arguments[1] is "allow" or "restore", "Use qa_m10_rod <witness actor> allow|restore.");
        var actor = ResolvePlayer(arguments[0]);
        Require(teleportActors.TryGetValue(actor.Index, out var witness) && ReferenceEquals(witness.Player, actor) &&
            actor.IsLoggedIn && actor.Account is not null && actor.tempGroup is null &&
            !actor.HasPermission("anticheat.bypass") && !actor.HasPermission("compatibility.qa.console") &&
            !actor.HasPermission(Permissions.bypassssc), "Current ordinary SSC witness actor required.");
        if (arguments[1] == "allow")
        {
            Require(witness!.OriginalRodGroup is null && !actor.HasPermission(Permissions.rod), "Actor must start without rod permission.");
            witness.OriginalRodGroup = actor.Group.Name;
            witness.AddedRodGroup = "qa_m10_rod_" + actor.Index + "_" + session;
            TShock.Groups.AddGroup(witness.AddedRodGroup, witness.OriginalRodGroup, Permissions.rod, "255,255,255");
            TShock.UserAccounts.SetUserGroup(actor.Account!, witness.AddedRodGroup);
            Require(actor.Group.Permissions == Permissions.rod && actor.Group.ParentName == witness.OriginalRodGroup &&
                actor.HasPermission(Permissions.rod) && !actor.HasPermission("anticheat.bypass") &&
                !actor.HasPermission(Permissions.bypassssc), "Only the inherited ordinary group plus the core rod permission is allowed.");
        }
        else
        {
            Require(witness!.OriginalRodGroup is not null, "No rod group change to restore.");
            TShock.UserAccounts.SetUserGroup(actor.Account!, witness.OriginalRodGroup!);
            Require(!actor.HasPermission(Permissions.rod), "Original group restoration failed.");
            witness.OriginalRodGroup = null;
        }
        WriteTeleportState();
    }

    private static TeleportRawState CaptureTeleportRawState(Player player) => new(player.position.X,
        player.position.Y, player.velocity.X, player.velocity.Y, player.lastPortalColorIndex);

    private void ObserveTeleportRawBefore(GetDataEventArgs args)
    {
        if ((int)args.MsgID is not (65 or 96) || args.Msg is null ||
            !teleportActors.TryGetValue(args.Msg.whoAmI, out var state) ||
            !ReferenceEquals(TShock.Players[args.Msg.whoAmI], state.Player)) return;
        if (state.PendingRaw is not null) state.RawWitnessFaults = TeleportIncrement(state.RawWitnessFaults);
        state.RawRequests = TeleportIncrement(state.RawRequests);
        var player = state.Player.TPlayer;
        state.PendingRaw = new(args, player, state.RawRequests, Environment.CurrentManagedThreadId,
            args.Handled, CaptureTeleportRawState(player));
    }

    private void ObserveTeleportRawAfter(GetDataEventArgs args)
    {
        if ((int)args.MsgID is not (65 or 96) || args.Msg is null ||
            !teleportActors.TryGetValue(args.Msg.whoAmI, out var state)) return;
        var before = state.PendingRaw;
        state.PendingRaw = null;
        if (before is null || !ReferenceEquals(before.Arguments, args))
        { state.RawWitnessFaults = TeleportIncrement(state.RawWitnessFaults); return; }
        state.LastRaw = new(before.Sequence, (int)args.MsgID,
            ReferenceEquals(TShock.Players[args.Msg.whoAmI], state.Player) && ReferenceEquals(state.Player.TPlayer, before.Player),
            before.Thread == Environment.CurrentManagedThreadId, before.Handled, args.Handled,
            before.State, CaptureTeleportRawState(before.Player));
        // Both snapshots bracket this exact synchronous raw dispatch. Normal GameUpdate
        // movement between later console snapshots is not attributed to a rejected packet.
        // The after hook still precedes the native body; native entry and real peer counters
        // independently establish that a handled packet has no native/relay continuation.
    }

    private void ObserveTeleportRelay(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.msgType is not (65 or 96) || !args.ContinueExecution) return;
        int slot = args.msgType == 65 ? (int)args.number2 : args.number;
        if (!teleportActors.TryGetValue(slot, out var state) || !ReferenceEquals(TShock.Players[slot], state.Player)) return;
        if (args.msgType == 65) state.Send65 = TeleportIncrement(state.Send65);
        else state.Send96 = TeleportIncrement(state.Send96);
        var player = state.Player.TPlayer;
        state.LastRelay = new { packet = args.msgType, args.remoteClient, args.ignoreClient,
            x = player.position.X, y = player.position.Y, velocityX = player.velocity.X, velocityY = player.velocity.Y,
            player.lastPortalColorIndex, nativeBodyEntries = state.NativeEntries,
            source = "actual native post-write SendData boundary; separate peer receipt required" };
    }

    private void WriteTeleportState()
    {
        var plugin = M5Plugin();
        var payload = new { utc = DateTimeOffset.UtcNow, witnessInstalled = teleportBodyWitness is not null,
            guardPresent = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M9PlayerTeleportGuard") is not null,
            actors = teleportActors.Values.Select(x => new { x.Player.Name, slot = x.Player.Index,
                account = x.Player.Account.ID, group = x.Player.Group.Name,
                groupParent = x.Player.Group.ParentName, directPermissions = x.Player.Group.Permissions,
                temporaryGroup = x.Player.tempGroup?.Name, sscBypass = x.Player.HasPermission(Permissions.bypassssc),
                x.OriginalRodGroup, x.AddedRodGroup,
                bypass = x.Player.HasPermission("anticheat.bypass"), rodPermission = x.Player.HasPermission(Permissions.rod),
                samePlayer = ReferenceEquals(TShock.Players[x.Player.Index], x.Player), x.NativeEntries, x.Send65, x.Send96,
                x = x.Player.TPlayer.position.X, y = x.Player.TPlayer.position.Y,
                velocityX = x.Player.TPlayer.velocity.X, velocityY = x.Player.TPlayer.velocity.Y,
                x.Player.TPlayer.lastPortalColorIndex, x.LastRelay, x.RawRequests, x.RawWitnessFaults, x.LastRaw }).ToArray(),
            source = "bounded passive actual Player.Teleport body entry and native send-boundary state; no gameplay state changed" };
        File.WriteAllText(Path.Combine(output!, "m9-teleport-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void DisposeTeleportWitness()
    {
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveTeleportRawBefore);
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveTeleportRawAfter);
        HookEvents.Terraria.NetMessage.SendData -= ObserveTeleportRelay;
        teleportBodyWitness?.Dispose(); teleportBodyWitness = null; teleportActors.Clear();
    }
}
