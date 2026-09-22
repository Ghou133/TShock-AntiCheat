using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AntiCheat.Rules;

public interface IWindowsFirewallComFactory
{
    object CreatePolicy();
    object CreateRule();
}

/// <summary>
/// INetFwPolicy2/INetFwRules/INetFwRule COM adapter. No shell, service installation, elevation,
/// blanket allow/block, policy reset, or arbitrary remotely supplied rule attributes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsComFirewallBackend : IRestrictedFirewallBackend
{
    private readonly string applicationPath, scope, prefix;
    private readonly int localPort;
    private readonly IWindowsFirewallComFactory factory;
    private const string DescriptionPrefix = "TShockAntiCheat/v1 expires=";

    public WindowsComFirewallBackend(RestrictedFirewallOptions options, bool enableOsOperations)
        : this(options, enableOsOperations, new NativeComFactory()) { }

    /// <summary>The injected factory supports contract tests without activating any Windows COM object.</summary>
    public WindowsComFirewallBackend(RestrictedFirewallOptions options, bool enableOsOperations, IWindowsFirewallComFactory factory)
    {
        if (!enableOsOperations) throw new InvalidOperationException("OS firewall operations require an explicit external-host opt-in.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows firewall COM requires Windows.");
        WindowsFirewallExecutor.ValidateOptions(options);
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        applicationPath = Path.GetFullPath(options.ApplicationPath); localPort = options.LocalTcpPort;
        scope = WindowsFirewallExecutor.ScopeFor(applicationPath, localPort);
        prefix = WindowsFirewallExecutor.RulePrefix + scope + "-";
        // No COM activation or OS read/write takes place in this constructor.
    }

    public IReadOnlyList<RestrictedFirewallRule> ReadOwnedRules()
    {
        object? policy = null, collection = null;
        try
        {
            policy = factory.CreatePolicy(); collection = ((dynamic)policy).Rules;
            var found = new List<RestrictedFirewallRule>(); int inspected = 0;
            foreach (object entry in (IEnumerable)collection)
            {
                try
                {
                    if (++inspected > 10000) throw new InvalidDataException("System firewall inventory exceeds bounded inspection budget.");
                    dynamic rule = entry;
                    string name = rule.Name;
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    found.Add(ReadOwned(rule));
                    if (found.Count > 1024) throw new InvalidDataException("Owned firewall inventory exceeds capacity.");
                }
                finally { Release(entry); }
            }
            return found;
        }
        finally { Release(collection); Release(policy); }
    }

    public void EnsureRule(RestrictedFirewallRule desired)
    {
        ValidateDesired(desired);
        object? policy = null, collection = null, item = null, existing = null;
        try
        {
            policy = factory.CreatePolicy(); collection = ((dynamic)policy).Rules;
            // Add overwrites an existing identifier. Check its ownership before using that API.
            existing = FindRule(collection, desired.Name);
            if (existing is not null)
            {
                var prior = ReadOwned((dynamic)existing);
                if (prior == desired) return;
            }
            item = factory.CreateRule();
            dynamic rule = item;
            rule.Name = desired.Name;
            rule.Description = DescriptionPrefix + desired.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture);
            rule.Grouping = WindowsFirewallExecutor.RuleGroup;
            rule.ApplicationName = applicationPath;
            rule.Protocol = 6; // NET_FW_IP_PROTOCOL_TCP; must precede LocalPorts.
            rule.LocalPorts = localPort.ToString(CultureInfo.InvariantCulture);
            rule.RemotePorts = "*";
            rule.LocalAddresses = "*";
            rule.InterfaceTypes = "All";
            rule.RemoteAddresses = desired.Address; // Only one normalized IP, never CIDR/range/*/hostname.
            rule.Direction = 1; // NET_FW_RULE_DIR_IN
            rule.Action = 0; // NET_FW_ACTION_BLOCK
            rule.Profiles = 0x7fffffff; // NET_FW_PROFILE2_ALL
            rule.EdgeTraversal = false;
            rule.Enabled = true;
            ((dynamic)collection).Add(rule);
            Release(existing); existing = FindRule(collection, desired.Name);
            if (existing is null || ReadOwned((dynamic)existing) != desired)
                throw new InvalidDataException("Configured firewall rule did not read back with the exact owned contract.");
        }
        finally { Release(existing); Release(item); Release(collection); Release(policy); }
    }

    public void RemoveRule(string ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName) || !ruleName.StartsWith(prefix, StringComparison.Ordinal) || ruleName.Length != prefix.Length + 64)
            throw new ArgumentException("Only this helper's exact rule namespace can be removed.");
        object? policy = null, collection = null, item = null;
        try
        {
            policy = factory.CreatePolicy(); collection = ((dynamic)policy).Rules;
            item = FindRule(collection, ruleName);
            if (item is null) return;
            _ = ReadOwned((dynamic)item); // Refuse to remove a colliding unmanaged/mutated rule.
            ((dynamic)collection).Remove(ruleName);
        }
        finally { Release(item); Release(collection); Release(policy); }
    }

    private RestrictedFirewallRule ReadOwned(dynamic rule)
    {
        string name = rule.Name, address = rule.RemoteAddresses, app = rule.ApplicationName, ports = rule.LocalPorts;
        string group = rule.Grouping, description = rule.Description;
        // Windows may render one host with a full-width prefix/mask. No subnet or range is accepted.
        address = NormalizeSingleHost(address);
        if (group != WindowsFirewallExecutor.RuleGroup || app != applicationPath || ports != localPort.ToString(CultureInfo.InvariantCulture) ||
            name != WindowsFirewallExecutor.RuleName(scope, address) || (int)rule.Protocol != 6 || (int)rule.Direction != 1 ||
            (int)rule.Action != 0 || !(bool)rule.Enabled || (int)rule.Profiles != 0x7fffffff || (bool)rule.EdgeTraversal ||
            (string)rule.LocalAddresses != "*" || (string)rule.RemotePorts != "*" || (string)rule.InterfaceTypes != "All" ||
            !description.StartsWith(DescriptionPrefix, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParseExact(description[DescriptionPrefix.Length..], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset expires))
            throw new InvalidDataException("OS firewall rule conflicts with the exact helper-owned contract.");
        return new(name, address, app, localPort, expires);
    }

    private static string NormalizeSingleHost(string address)
    {
        string[] parts = address.Split('/');
        if (parts.Length is < 1 or > 2 || !NetworkControls.TryNormalizeAddress(parts[0], out var ip) ||
            (parts.Length == 2 && (ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? parts[1] is not ("32" or "255.255.255.255") : parts[1] != "128")))
            throw new InvalidDataException("OS rule is not a single-host address.");
        string canonical = ip!.ToString();
        if (!WindowsFirewallExecutor.IsEligiblePublicAddress(canonical)) throw new InvalidDataException("OS rule targets a protected address class.");
        return canonical;
    }

    private void ValidateDesired(RestrictedFirewallRule desired)
    {
        if (desired is null || desired.ApplicationPath != applicationPath || desired.LocalTcpPort != localPort ||
            !WindowsFirewallExecutor.IsEligiblePublicAddress(desired.Address) || desired.Name != WindowsFirewallExecutor.RuleName(scope, desired.Address))
            throw new ArgumentException("Only this helper's exact executable, port and single-host rules can be configured.");
    }

    private sealed class NativeComFactory : IWindowsFirewallComFactory
    {
        public object CreatePolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        public object CreateRule() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
    }
    private static object? FindRule(object collection, string name)
    {
        try { return ((dynamic)collection).Item(name); }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070002)) { return null; }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
