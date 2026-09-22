using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.Net.Sockets;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// The transport registered for one server session. TSPlayer.Client dynamically reads the
/// current native slot, so neither the player nor RemoteClient alone identifies a connection.
/// Reading this adapter performs no socket calls, authentication or phase mutation.
/// </summary>
public sealed class M10ConnectionPhaseAdapter(SessionKey session, TSPlayer player, RemoteClient client, ISocket socket)
{
    public SessionKey Session { get; } = session;

    public bool MatchesCurrent(SessionKey currentSession, TSPlayer? currentPlayer)
    {
        int slot = Session.Slot;
        var players = ServerTShock.Players;
        var clients = Netplay.Clients;
        return currentSession == Session && currentPlayer is not null &&
            ReferenceEquals(currentPlayer, player) && player.Index == slot && client.Id == slot &&
            players is not null && clients is not null && (uint)slot < players.Length && (uint)slot < clients.Length &&
            ReferenceEquals(players[slot], player) && ReferenceEquals(clients[slot], client) &&
            ReferenceEquals(client.Socket, socket) && ReferenceEquals(ServerTShock.Players, players) &&
            ReferenceEquals(Netplay.Clients, clients);
    }

    public M10AcceptedConnectionPhase? ReadAccepted(SessionKey currentSession, TSPlayer? currentPlayer)
    {
        if (!MatchesCurrent(currentSession, currentPlayer)) return null;
        int state = client.State;
        // These are target 1.4.5.8 server RemoteClient states, not client Connection states.
        ConnectionPhase? phase = state switch
        {
            -1 or 0 => ConnectionPhase.Handshake,
            1 or 2 => ConnectionPhase.Authenticating,
            3 => ConnectionPhase.WorldSync,
            10 => ConnectionPhase.Playing,
            _ => null
        };
        return phase.HasValue && MatchesCurrent(currentSession, currentPlayer)
            ? new(state, phase.Value) : null;
    }
}

/// <summary>Read-only native observation; it does not attest account login or SSC delivery.</summary>
public sealed record M10AcceptedConnectionPhase(int NativeState, ConnectionPhase Phase);
