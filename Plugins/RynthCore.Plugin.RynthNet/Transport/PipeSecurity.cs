// ============================================================================
//  RynthNet - Transport/PipeSecurity.cs
//  Local-only, same-user-only named pipes.
//
//  - Pipe names:  RynthNet.1.<ns>.<pid>.<nonce>
//      ns    = hash of (Windows user SID, Windows session id): clients of one
//              user in one session find each other; nobody else's are listed.
//      pid   = the owning process, checked against the kernel after connecting.
//      nonce = random per start, so a name can't be guessed or pre-created.
//  - Server ACL:  owner = current user; allow current user; deny NETWORK (no
//    SMB access to \\machine\pipe\..., even with the same credentials). Other
//    users, services and remote machines can't open the pipe at all.
//  - Client side: before sending anything, the dialer checks that the pipe's
//    owner is the current user (so another user's squatting pipe gets nothing)
//    and that the server process id matches the pid in the name.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace RynthCore.Plugin.RynthNet.Transport;

internal static class PipeSecurityHelper
{
    public const string Prefix = "RynthNet.1.";
    private const string PipeRoot = @"\\.\pipe\";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll")]
    private static extern int ProcessIdToSessionId(uint processId, out uint sessionId);

    private static SecurityIdentifier? _user;

    public static SecurityIdentifier CurrentUser
    {
        get
        {
            if (_user != null) return _user;
            using var identity = WindowsIdentity.GetCurrent();
            _user = identity.User ?? throw new InvalidOperationException("no user SID for this process");
            return _user;
        }
    }

    /// <summary>The per-user, per-session namespace part of the pipe names.</summary>
    public static string DefaultNamespace()
    {
        uint session = 0;
        try { ProcessIdToSessionId((uint)Environment.ProcessId, out session); } catch { }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(CurrentUser.Value + "|" + session.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string PipeName(string ns, int pid, string nonce) => $"{Prefix}{ns}.{pid}.{nonce}";

    /// <summary>The node id is the pid and nonce: "pid.nonce".</summary>
    public static string NodeId(int pid, string nonce) => $"{pid}.{nonce}";

    public static bool TryParse(string pipeName, string ns, out int pid, out string nodeId)
    {
        pid = 0;
        nodeId = "";
        string head = Prefix + ns + ".";
        if (!pipeName.StartsWith(head, StringComparison.Ordinal)) return false;
        string rest = pipeName.Substring(head.Length);
        int dot = rest.IndexOf('.');
        if (dot <= 0 || dot == rest.Length - 1) return false;
        if (!int.TryParse(rest.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0) return false;
        string nonce = rest.Substring(dot + 1);
        if (nonce.Length != 16) return false;
        foreach (char c in nonce)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        nodeId = NodeId(pid, nonce);
        return true;
    }

    /// <summary>Names of the pipes in this namespace (live: a crashed client's pipe is gone with it).</summary>
    public static List<string> Discover(string ns)
    {
        var names = new List<string>();
        string head = Prefix + ns + ".";
        foreach (string path in Directory.EnumerateFiles(PipeRoot, head + "*"))
        {
            string name = path.StartsWith(PipeRoot, StringComparison.OrdinalIgnoreCase) ? path.Substring(PipeRoot.Length) : Path.GetFileName(path);
            if (name.StartsWith(head, StringComparison.Ordinal)) names.Add(name);
        }
        return names;
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        SecurityIdentifier me = CurrentUser;
        security.SetOwner(me);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>One listening instance. <paramref name="first"/> claims the name (fails if it exists).</summary>
    public static NamedPipeServerStream CreateServer(string pipeName, bool first)
    {
        PipeOptions options = PipeOptions.Asynchronous;
        if (first) options |= PipeOptions.FirstPipeInstance;
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, Wire.MaxFrame, Wire.MaxFrame, CreateSecurity());
    }

    /// <summary>After connecting: is this our user's pipe, served by the process its name says?</summary>
    public static bool VerifyServer(NamedPipeClientStream pipe, int expectedPid, out string why)
    {
        why = "";
        try
        {
            PipeSecurity security = pipe.GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != CurrentUser)
            {
                why = "pipe owned by another user";
                return false;
            }
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) == 0)
            {
                why = "server process unknown (error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            if (pid != (uint)expectedPid)
            {
                why = $"server pid {pid} is not {expectedPid}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>The process id of the client connected to a server instance, 0 when unknown.</summary>
    public static int ClientPid(NamedPipeServerStream pipe)
    {
        try
        {
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) != 0 ? (int)pid : 0;
        }
        catch
        {
            return 0;
        }
    }
}
