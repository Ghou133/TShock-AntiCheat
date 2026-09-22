using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AntiCheat.DevMcp;

public sealed record OwnedProcessMember(int Pid, DateTimeOffset StartedUtc, string ExecutablePath);
public sealed record OwnedLogSnapshot(string Path, long ObservedBytes, long WrittenBytes, long DroppedBytes,
    bool Completed, bool ReachedEof, string? Error);
public sealed record OwnedProcessCleanup(bool TerminationRequested, string? Reason, bool RootExited,
    int? ActiveJobProcesses, bool LogsDrained, bool TimedOut, string? Error);
public sealed record OwnedJobInspection(bool Exists, int? ActiveProcessCount, int? ErrorCode);

/// <summary>
/// Owns one non-breakaway Windows JobObject. The process is assigned atomically by
/// PROC_THREAD_ATTRIBUTE_JOB_LIST, created suspended, verified, then resumed. Even a parent
/// crash between CreateProcess and Resume cannot leave an unowned suspended process.
/// </summary>
public sealed class OwnedProcess : IDisposable
{
    public const int MaximumLogBytes = 4 * 1024 * 1024;
    public const int MaximumReportedMembers = 128;
    private const string JobPrefix = "Local\\AntiCheat.DevMcp.";
    private const string RequestedJobPrefix = "Local\\terraria-dev-";
    private static readonly TimeSpan MaximumRunTime = TimeSpan.FromHours(2);
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly Native.KernelHandle job;
    private readonly Native.KernelHandle process;
    private readonly WindowsPathPins[] pins;
    private readonly SafeFileHandle executablePin;
    private readonly PipeCapture stdout;
    private readonly PipeCapture stderr;
    private int disposeStarted;
    private bool disposed;
    private bool terminationRequested;
    private string? terminationReason;
    private string? terminationError;
    private int? exitCode;
    private Task? cleanupTask;
    private OwnedProcessCleanup cleanup = new(false, null, false, null, false, false, null);

    public int Pid { get; }
    public DateTimeOffset StartedUtc { get; }
    public string ExecutablePath { get; }
    public string JobName { get; }
    public OwnedLogSnapshot Stdout => stdout.Snapshot();
    public OwnedLogSnapshot Stderr => stderr.Snapshot();
    public OwnedProcessCleanup Cleanup { get { lock (gate) return cleanup; } }
    public bool MemberSnapshotIncomplete { get; private set; }
    public int? ActiveProcessCount
    {
        get
        {
            lock (gate)
            {
                if (disposed) return cleanup.ActiveJobProcesses == 0 ? 0 : null;
                return ReadActiveProcessCount(job);
            }
        }
    }
    public bool AllExited => ActiveProcessCount == 0;

    private OwnedProcess(Native.KernelHandle job, Native.KernelHandle process, int pid,
        DateTimeOffset startedUtc, string executablePath, string jobName, WindowsPathPins[] pins,
        SafeFileHandle executablePin, PipeCapture stdout, PipeCapture stderr)
    {
        this.job = job; this.process = process; Pid = pid; StartedUtc = startedUtc;
        ExecutablePath = executablePath; JobName = jobName; this.pins = pins;
        this.executablePin = executablePin; this.stdout = stdout; this.stderr = stderr;
    }

    public static OwnedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> envOverrides, string stdoutLogPath, string stderrLogPath, string? requestedJobName = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Atomic JobObject process creation requires Windows 10 or newer.");
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(envOverrides);
        if (requestedJobName is not null && !IsRequestedJobName(requestedJobName))
            throw new ArgumentException("Requested job names must be Local\\terraria-dev- followed by 32 hexadecimal characters.", nameof(requestedJobName));
        executable = WindowsPathPins.Normalize(executable);
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The executable must be an explicit .exe path; no shell lookup is performed.", nameof(executable));
        workingDirectory = WindowsPathPins.Normalize(workingDirectory);
        stdoutLogPath = WindowsPathPins.Normalize(stdoutLogPath);
        stderrLogPath = WindowsPathPins.Normalize(stderrLogPath);
        if (string.Equals(stdoutLogPath, stderrLogPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("stdout and stderr require separate new files.");
        var commandLine = BuildCommandLine(executable, arguments);
        var environment = BuildEnvironment(envOverrides);

        var pathPins = new List<WindowsPathPins>(4);
        SafeFileHandle? exeHandle = null, outRead = null, outWrite = null, errRead = null, errWrite = null,
            inRead = null, inWrite = null;
        FileStream? outLog = null, errLog = null;
        Native.KernelHandle? jobHandle = null, processHandle = null, threadHandle = null;
        OwnedProcess? owned = null;
        nint attributeList = nint.Zero, inheritedHandles = nint.Zero, jobList = nint.Zero, environmentBlock = nint.Zero;
        var initializedAttributes = false;
        var ownershipTransferred = false;
        var jobWasCreated = false;
        try
        {
            var executablePaths = WindowsPathPins.ForFile(executable); pathPins.Add(executablePaths);
            exeHandle = executablePaths.OpenFile(WindowsPathPins.Native.GenericRead,
                WindowsPathPins.Native.ShareRead, WindowsPathPins.Native.OpenExisting);
            pathPins.Add(WindowsPathPins.ForDirectory(workingDirectory));
            var outPaths = WindowsPathPins.ForFile(stdoutLogPath); pathPins.Add(outPaths);
            var errPaths = WindowsPathPins.ForFile(stderrLogPath); pathPins.Add(errPaths);
            outLog = outPaths.CreateNew(asynchronous: true);
            errLog = errPaths.CreateNew(asynchronous: true);

            var jobName = requestedJobName ?? JobPrefix + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
            jobHandle = Native.CreateJobObjectW(nint.Zero, jobName);
            var createJobError = Marshal.GetLastPInvokeError();
            if (jobHandle.IsInvalid) throw new Win32Exception(createJobError, "CreateJobObject failed.");
            if (createJobError == 183) throw new IOException("The freshly generated job name already exists.");
            jobWasCreated = true;
            var limits = new Native.ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway flags.
            if (!Native.SetInformationJobObject(jobHandle, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot enable kill-on-job-close.");

            MakePipe(out outRead, out outWrite);
            MakePipe(out errRead, out errWrite);
            MakePipe(out inRead, out inWrite);
            if (!Native.SetHandleInformation(outRead, 1, 0) || !Native.SetHandleInformation(errRead, 1, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot restrict pipe inheritance.");
            // The child reads EOF. No API exposes an arbitrary console-command writer.
            inWrite.Dispose(); inWrite = null;

            nuint attributeSize = 0;
            _ = Native.InitializeProcThreadAttributeList(nint.Zero, 2, 0, ref attributeSize);
            if (attributeSize == 0 || attributeSize > 1024 * 1024)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot size process attributes.");
            attributeList = Marshal.AllocHGlobal(checked((int)attributeSize));
            if (!Native.InitializeProcThreadAttributeList(attributeList, 2, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot initialize process attributes.");
            initializedAttributes = true;
            inheritedHandles = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(inheritedHandles, 0, inRead.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, nint.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, 2 * nint.Size, errWrite.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributeList, 0, 0x20002, inheritedHandles,
                    (nuint)(3 * nint.Size), nint.Zero, nint.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot restrict inherited handles.");
            jobList = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(jobList, jobHandle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributeList, 0, 0x2000D, jobList, (nuint)nint.Size, nint.Zero, nint.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot assign the job atomically at process creation.");
            environmentBlock = Marshal.StringToHGlobalUni(environment);
            var startup = new Native.StartupInfoEx
            {
                StartupInfo = new Native.StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x101, ShowWindow = 0,
                    StdInput = inRead.DangerousGetHandle(), StdOutput = outWrite.DangerousGetHandle(), StdError = errWrite.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            const uint flags = 0x00000004 | 0x00000400 | 0x00080000 | 0x08000000;
            if (!Native.CreateProcessW(executable, commandLine, nint.Zero, nint.Zero, true, flags,
                    environmentBlock, workingDirectory, ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessW failed before execution.");
            processHandle = new Native.KernelHandle(information.Process);
            threadHandle = new Native.KernelHandle(information.Thread);
            if (!Native.IsProcessInJob(processHandle, jobHandle, out var assigned) || !assigned)
                throw new IOException("The suspended process was not assigned to its owned job.");
            var identity = ReadIdentity(processHandle, checked((int)information.ProcessId));
            if (!string.Equals(identity.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The created process image differs from the pinned executable.");

            outWrite.Dispose(); outWrite = null;
            errWrite.Dispose(); errWrite = null;
            inRead.Dispose(); inRead = null;
            var outCapture = new PipeCapture(outRead, outLog, stdoutLogPath);
            var errCapture = new PipeCapture(errRead, errLog, stderrLogPath);
            owned = new OwnedProcess(jobHandle, processHandle, identity.Pid, identity.StartedUtc, executable,
                jobName, pathPins.ToArray(), exeHandle, outCapture, errCapture);
            ownershipTransferred = true;
            outCapture.Start(); errCapture.Start();
            if (Native.ResumeThread(threadHandle) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot resume the owned process.");
            return owned;
        }
        catch
        {
            if (ownershipTransferred) owned!.Dispose();
            else if (jobWasCreated && jobHandle is { IsInvalid: false })
            {
                // This handle was created here. Never look up a PID/name to terminate startup failures.
                _ = Native.TerminateJobObject(jobHandle, Native.CanceledExitCode);
                jobHandle.Dispose();
                if (processHandle is { IsInvalid: false }) _ = Native.WaitForSingleObject(processHandle, 5000);
            }
            throw;
        }
        finally
        {
            if (initializedAttributes) Native.DeleteProcThreadAttributeList(attributeList);
            if (attributeList != nint.Zero) Marshal.FreeHGlobal(attributeList);
            if (inheritedHandles != nint.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (jobList != nint.Zero) Marshal.FreeHGlobal(jobList);
            if (environmentBlock != nint.Zero) Marshal.FreeHGlobal(environmentBlock);
            threadHandle?.Dispose(); outWrite?.Dispose(); errWrite?.Dispose(); inRead?.Dispose(); inWrite?.Dispose();
            if (!ownershipTransferred)
            {
                outRead?.Dispose(); errRead?.Dispose(); outLog?.Dispose(); errLog?.Dispose();
                processHandle?.Dispose(); jobHandle?.Dispose(); exeHandle?.Dispose();
                for (var i = pathPins.Count - 1; i >= 0; --i) pathPins[i].Dispose();
            }
        }
    }

    /// <summary>Cancellation terminates this owned job and performs bounded cleanup before throwing.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeStarted) != 0, this);
        using var limit = new CancellationTokenSource(MaximumRunTime);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                int? code;
                lock (gate) code = ReadExitCode();
                if (code is not null)
                {
                    if (ActiveProcessCount != 0) TerminateOwnedTree("root-exited-descendant-cleanup");
                    await GetCleanupTask().ConfigureAwait(false);
                    return code.Value;
                }
                await Task.Delay(25, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            try { TerminateOwnedTree(cancellationToken.IsCancellationRequested ? "wait-canceled" : "maximum-runtime-exceeded"); }
            catch (Win32Exception) { /* The error remains visible in Cleanup; closing our job is the fallback. */ }
            await GetCleanupTask().ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("Owned process exceeded its two-hour absolute runtime limit.");
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void TerminateOwnedTree(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 256) throw new ArgumentException("Termination reason is too long.", nameof(reason));
        lock (gate)
        {
            if (disposed || terminationRequested) return;
            terminationRequested = true; terminationReason = reason;
            if (!Native.TerminateJobObject(job, Native.CanceledExitCode))
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot terminate the owned JobObject.");
                terminationError = error.Message;
                throw error;
            }
        }
    }

    /// <summary>Members are read only from this job, then their handle membership/identity is verified.</summary>
    public IReadOnlyList<OwnedProcessMember> GetMembers()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var bytes = 8 + MaximumReportedMembers * nint.Size;
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.WriteInt64(buffer, 0);
                var ok = Native.QueryInformationJobObjectBuffer(job, 3, buffer, (uint)bytes, out _);
                var error = Marshal.GetLastPInvokeError();
                var assigned = unchecked((uint)Marshal.ReadInt32(buffer));
                var count = unchecked((uint)Marshal.ReadInt32(buffer, 4));
                MemberSnapshotIncomplete = !ok || assigned > MaximumReportedMembers || count > MaximumReportedMembers;
                if (!ok && error != 234) return [];
                var members = new List<OwnedProcessMember>(Math.Min(checked((int)Math.Min(count, MaximumReportedMembers)), MaximumReportedMembers));
                for (var i = 0; i < Math.Min(count, MaximumReportedMembers); ++i)
                {
                    var pid = Marshal.ReadIntPtr(buffer, 8 + i * nint.Size).ToInt64();
                    if (pid is <= 0 or > int.MaxValue) { MemberSnapshotIncomplete = true; continue; }
                    using var member = Native.OpenProcess(Native.QueryLimitedInformation | Native.Synchronize, false, (uint)pid);
                    if (member.IsInvalid) { MemberSnapshotIncomplete = true; continue; }
                    if (!Native.IsProcessInJob(member, job, out var included)) { MemberSnapshotIncomplete = true; continue; }
                    if (!included) continue;
                    try { members.Add(ReadIdentity(member, (int)pid)); }
                    catch (Win32Exception) { MemberSnapshotIncomplete = true; }
                }
                return members;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    /// <summary>false means ended/different identity; null means the identity could not be verified.</summary>
    public static bool? IsSameProcessAlive(OwnedProcessMember identity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Pid <= 0) return false;
        using var handle = Native.OpenProcess(Native.QueryLimitedInformation | Native.Synchronize, false, (uint)identity.Pid);
        if (handle.IsInvalid) return Marshal.GetLastPInvokeError() == 87 ? false : null;
        var status = Native.WaitForSingleObject(handle, 0);
        if (status == 0) return false;
        if (status != 258) return null;
        try
        {
            var actual = ReadIdentity(handle, identity.Pid);
            return actual.StartedUtc == identity.StartedUtc &&
                string.Equals(actual.ExecutablePath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception) { return null; }
    }

    public static OwnedJobInspection InspectJob(string jobName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!IsRequestedJobName(jobName) && (string.IsNullOrEmpty(jobName) || !jobName.StartsWith(JobPrefix, StringComparison.Ordinal) || jobName.Length > 256 ||
            jobName[JobPrefix.Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-')))
            throw new ArgumentException("Only recorded development-tool job names can be inspected.", nameof(jobName));
        using var handle = Native.OpenJobObjectW(4, false, jobName); // JOB_OBJECT_QUERY only.
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return new(false, null, error == 2 ? null : error);
        }
        var count = ReadActiveProcessCount(handle);
        return new(true, count, count is null ? Marshal.GetLastPInvokeError() : null);
    }

    private static bool IsRequestedJobName(string? name) => name is not null &&
        name.StartsWith(RequestedJobPrefix, StringComparison.Ordinal) && name.Length == RequestedJobPrefix.Length + 32 &&
        name[RequestedJobPrefix.Length..].All(char.IsAsciiHexDigit);

    private Task GetCleanupTask()
    {
        lock (gate) return cleanupTask ??= CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        var clock = Stopwatch.StartNew();
        int? active = null;
        var rootExited = false;
        while (clock.Elapsed < TerminationWait)
        {
            lock (gate)
            {
                rootExited = ReadExitCode() is not null;
                active = disposed ? null : ReadActiveProcessCount(job);
            }
            if (rootExited && active == 0) break;
            await Task.Delay(25).ConfigureAwait(false);
        }
        var drains = Task.WhenAll(stdout.Completion, stderr.Completion);
        var completed = await Task.WhenAny(drains, Task.Delay(DrainWait)).ConfigureAwait(false) == drains;
        if (!completed) { stdout.Stop(); stderr.Stop(); }
        var drained = completed && stdout.Snapshot().ReachedEof && stderr.Snapshot().ReachedEof;
        lock (gate)
            cleanup = new(terminationRequested, terminationReason, rootExited, active, drained,
                !rootExited || active != 0 || !drained, terminationError);
    }

    private int? ReadExitCode()
    {
        if (exitCode is not null || disposed) return exitCode;
        var result = Native.WaitForSingleObject(process, 0);
        if (result == 258) return null;
        if (result != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot wait on the owned process handle.");
        if (!Native.GetExitCodeProcess(process, out var code)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return exitCode = unchecked((int)code);
    }

    private static int? ReadActiveProcessCount(Native.KernelHandle handle)
    {
        if (!Native.QueryInformationJobObjectAccounting(handle, 1, out var accounting,
                (uint)Marshal.SizeOf<Native.BasicAccountingInformation>(), out _)) return null;
        return accounting.ActiveProcesses <= int.MaxValue ? (int)accounting.ActiveProcesses : null;
    }

    private static OwnedProcessMember ReadIdentity(Native.KernelHandle handle, int pid)
    {
        if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the process creation time.");
        var image = new StringBuilder(32768);
        uint size = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageNameW(handle, 0, image, ref size))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the process executable identity.");
        var fileTime = ((long)created.High << 32) | created.Low;
        return new(pid, DateTime.FromFileTimeUtc(fileTime), image.ToString());
    }

    internal static StringBuilder BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 256) throw new ArgumentException("Too many process arguments.", nameof(arguments));
        var command = new StringBuilder();
        AppendQuoted(command, executable);
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > 32766 || argument.Contains('\0'))
                throw new ArgumentException("Process arguments are too long or contain nulls.", nameof(arguments));
            command.Append(' '); AppendQuoted(command, argument);
            if (command.Length >= 32767) throw new ArgumentException("The Windows command line is too long.", nameof(arguments));
        }
        if (command.Length >= 32767) throw new ArgumentException("The Windows command line is too long.", nameof(arguments));
        return command;
    }

    private static void AppendQuoted(StringBuilder command, string argument)
    {
        command.Append('"');
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { ++slashes; continue; }
            command.Append('\\', c == '"' ? 2 * slashes + 1 : slashes);
            command.Append(c); slashes = 0;
        }
        command.Append('\\', 2 * slashes).Append('"');
    }

    private static string BuildEnvironment(IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count > 128) throw new ArgumentException("Too many environment overrides.", nameof(overrides));
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            entries[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        foreach (var (name, value) in overrides)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 32766 || name.Contains('=') || name.Contains('\0') ||
                value is null || value.Length > 32766 || value.Contains('\0'))
                throw new ArgumentException("Invalid environment override.", nameof(overrides));
            entries[name] = value;
        }
        // StringToHGlobalUni appends the second NUL. No encoding or pacing settings are changed here.
        var block = string.Join('\0', entries.Select(x => x.Key + "=" + x.Value)) + '\0';
        if (block.Length >= 131072) throw new ArgumentException("The environment block is too large.", nameof(overrides));
        return block;
    }

    private static void MakePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
        if (!Native.CreatePipe(out read, out write, ref attributes, 16384))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create owned process output pipes.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        try
        {
            try { if (ActiveProcessCount != 0) TerminateOwnedTree("owner-disposed"); }
            catch (Win32Exception) { }
            try { GetCleanupTask().GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                lock (gate) cleanup = cleanup with { TimedOut = true, Error = error.GetType().Name + ": " + error.Message };
            }
        }
        finally
        {
            // Closing our non-inherited handle is the kernel-enforced crash/disposal fallback.
            lock (gate) { disposed = true; job.Dispose(); process.Dispose(); }
            stdout.Stop(); stderr.Stop();
            executablePin.Dispose();
            for (var i = pins.Length - 1; i >= 0; --i) pins[i].Dispose();
        }
    }

    private sealed class PipeCapture(SafeFileHandle pipe, FileStream log, string path)
    {
        private readonly CancellationTokenSource stop = new();
        private long observed, written;
        private int completed, eof;
        private string? error;
        public Task Completion { get; private set; } = Task.CompletedTask;
        public void Start() => Completion = RunAsync();
        public void Stop() { stop.Cancel(); }
        public OwnedLogSnapshot Snapshot()
        {
            var kept = Interlocked.Read(ref written);
            var seen = Interlocked.Read(ref observed);
            return new(path, seen, kept, Math.Max(0, seen - kept), Volatile.Read(ref completed) != 0,
                Volatile.Read(ref eof) != 0, Volatile.Read(ref error));
        }

        private async Task RunAsync()
        {
            var buffer = new byte[16384];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    // An anonymous pipe is synchronous. Peek plus a single reader only reads
                    // currently available bytes, avoiding an indefinitely blocked ReadFile thread.
                    if (!Native.PeekNamedPipe(pipe, nint.Zero, 0, nint.Zero, out var available, nint.Zero))
                    {
                        var code = Marshal.GetLastPInvokeError();
                        if (code is 109 or 233) { Volatile.Write(ref eof, 1); break; }
                        throw new Win32Exception(code, "Cannot inspect child output pipe.");
                    }
                    if (available == 0) { await Task.Delay(10, stop.Token).ConfigureAwait(false); continue; }
                    if (!Native.ReadFile(pipe, buffer, Math.Min((uint)buffer.Length, available), out var count, nint.Zero))
                    {
                        var code = Marshal.GetLastPInvokeError();
                        if (code is 109 or 233) { Volatile.Write(ref eof, 1); break; }
                        throw new Win32Exception(code, "Cannot drain child output pipe.");
                    }
                    if (count == 0) continue;
                    Interlocked.Add(ref observed, count);
                    var keep = (int)Math.Min(count, Math.Max(0, MaximumLogBytes - Interlocked.Read(ref written)));
                    if (keep == 0 || error is not null) continue; // Keep draining after cap or disk failure.
                    try
                    {
                        await log.WriteAsync(buffer.AsMemory(0, keep), stop.Token).ConfigureAwait(false);
                        Interlocked.Add(ref written, keep);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                    { error = failure.GetType().Name + ": output file write failed; subsequent bytes drained without persistence."; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception failure) when (failure is IOException or Win32Exception or ObjectDisposedException)
            { error ??= failure.GetType().Name + ": output pipe drain failed."; }
            finally
            {
                try { pipe.Dispose(); log.Dispose(); }
                catch (IOException) { error ??= "IOException: output file cleanup failed."; }
                finally { Volatile.Write(ref completed, 1); }
            }
        }
    }

    private static class Native
    {
        internal const uint CanceledExitCode = 0xC000013A, QueryLimitedInformation = 0x1000, Synchronize = 0x00100000;
        internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public KernelHandle() : base(true) { }
            internal KernelHandle(nint value) : base(true) => SetHandle(value);
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes { internal int Length; internal nint Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal uint Size;
            internal nint Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, Reserved2Size;
            internal nint Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx { internal StartupInfo StartupInfo; internal nint AttributeList; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation { internal nint Process, Thread; internal uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime { internal uint Low, High; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimitInformation
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtendedLimitInformation
        {
            internal BasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicAccountingInformation
        {
            internal long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern KernelHandle CreateJobObjectW(nint attributes, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern KernelHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(KernelHandle job, int informationClass, ref ExtendedLimitInformation info, uint length);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObjectAccounting(KernelHandle job, int informationClass,
            out BasicAccountingInformation info, uint length, out uint returnedLength);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObjectBuffer(KernelHandle job, int informationClass, nint info, uint length, out uint returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(KernelHandle process, KernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool assigned);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern KernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, nint processAttributes,
            nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment,
            string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value,
            nuint size, nint previousValue, nint returnSize);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(KernelHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(KernelHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(KernelHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(KernelHandle process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint size, nint bytesRead, out uint available, nint bytesLeft);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint count, out uint read, nint overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
