using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AntiCheat.Rules;

/// <summary>Explicit local account ACL for a privileged helper and an unprivileged game account; HMAC remains mandatory.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallPipeFactory
{
    private readonly SecurityIdentifier client;
    public WindowsFirewallPipeFactory(string clientUserSid)
    {
        if (string.IsNullOrWhiteSpace(clientUserSid) || clientUserSid.Length > 184)
            throw new ArgumentException("An explicit Windows account SID is required.");
        client = new(clientUserSid);
        if (!client.IsAccountSid() || client.Value != clientUserSid)
            throw new ArgumentException("The pipe client must be one canonical account SID, not a group or wildcard.");
        byte[] sid = new byte[client.BinaryLength]; client.GetBinaryForm(sid, 0);
        uint nameLength = 0, domainLength = 0;
        _ = LookupAccountSid(IntPtr.Zero, sid, null, ref nameLength, null, ref domainLength, out _);
        if (Marshal.GetLastWin32Error() != 122 || nameLength is < 1 or > 2048 || domainLength > 2048)
            throw new ArgumentException("Configured pipe account SID cannot be resolved locally.");
        var name = new StringBuilder((int)nameLength); var domain = new StringBuilder((int)Math.Max(1, domainLength));
        if (!LookupAccountSid(IntPtr.Zero, sid, name, ref nameLength, domain, ref domainLength, out int use) || use != 1)
            throw new ArgumentException("Configured pipe SID must resolve to a user, never a group.");
    }

    public PipeSecurity BuildSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var server = identity.User ?? throw new InvalidOperationException("Helper identity has no user SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(server);
        // A valid local account credential is not permission to use this endpoint over SMB/network logon.
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(server, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(client, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    public NamedPipeServerStream Create(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 96 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Only one bounded local pipe name is accepted.");
        // Deliberately omit CurrentUserOnly: it would override this ACL and reject differing elevation.
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, BuildSecurity(), HandleInheritability.None);
    }

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(IntPtr systemName, byte[] sid, StringBuilder? name, ref uint nameLength,
        StringBuilder? domain, ref uint domainLength, out int sidNameUse);
}
