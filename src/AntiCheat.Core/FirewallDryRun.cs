using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AntiCheat.Core;

public enum FirewallRecommendation { RecordOnly, ProposeTemporaryBlock }
public enum NetworkAbuseReason { ConnectionBudget, HandshakeTimeout, WeightedPacketCost, BroadcastAmplification }
public sealed record NetworkWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc, long Connections, long Bytes, long Cost);
public sealed record FirewallDryRunEvent(Guid EventId, DateTimeOffset ObservedUtc, SessionKey? Session,
    long? AccountId, string SocketPeer, string? VerifiedClientAddress, string AddressProvenance,
    bool ProxyPeer, bool ExclusiveSourceVerified, NetworkWindow Window, NetworkAbuseReason Reason,
    string RuleVersion, int SuggestedTtlSeconds, string? EvidenceReference, FirewallRecommendation Recommendation,
    string? ProposedTarget, bool DryRun = true, bool Execute = false);

/// <summary>Stateless structured output only. Contains no IPC, process launch, firewall access or shell formatting.</summary>
public sealed class FirewallDryRun(TimeProvider clock)
{
    public FirewallDryRunEvent Create(IPAddress socketPeer, NetworkWindow window, NetworkAbuseReason reason,
        TimeSpan suggestedTtl, SessionKey? session = null, long? accountId = null, bool proxyPeer = false,
        bool exclusiveSourceVerified = false, IPAddress? verifiedClientAddress = null, string? evidenceReference = null)
    {
        ArgumentNullException.ThrowIfNull(socketPeer);
        ArgumentNullException.ThrowIfNull(window);
        if (window.EndUtc < window.StartUtc || window.Connections < 0 || window.Bytes < 0 || window.Cost < 0 ||
            suggestedTtl <= TimeSpan.Zero || accountId is <= 0 || evidenceReference?.Length > 256 ||
            !Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(window));
        var peer = Normalize(socketPeer);
        // NAT/shared-address safety is the default. A proxy socket is never a target, even if an
        // upstream real-address claim exists; execution at an audited ingress is a future adapter.
        var eligible = exclusiveSourceVerified && !proxyPeer && IsUnicast(peer);
        return new(Guid.NewGuid(), clock.GetUtcNow(), session, accountId, peer.ToString(),
            verifiedClientAddress is null ? null : Normalize(verifiedClientAddress).ToString(),
            "server-socket-peer", proxyPeer, exclusiveSourceVerified, window, reason, "network-dry-run/1.0.0",
            (int)Math.Clamp(suggestedTtl.TotalSeconds, 1, 900), evidenceReference,
            eligible ? FirewallRecommendation.ProposeTemporaryBlock : FirewallRecommendation.RecordOnly,
            eligible ? peer.ToString() : null);
    }
    public static string Serialize(FirewallDryRunEvent value) => JsonSerializer.Serialize(value);
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private static bool IsUnicast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily != AddressFamily.InterNetwork ||
            bytes[0] is > 0 and < 224 && bytes[0] != 10 &&
            !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 169 && bytes[1] == 254);
    }
}
