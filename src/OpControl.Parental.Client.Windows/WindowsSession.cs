using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Client;

public static class WindowsSession
{
    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;
    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    public static int GetMonitoredSessionId(string account)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var target = account.Contains('\\') ? account : $"{Environment.MachineName}\\{account}";
        var active = 0;
        var disconnected = 0;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            return 0;

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(buffer, i * size));
                var user = QueryString(info.SessionId, WTSUserName);
                var domain = QueryString(info.SessionId, WTSDomainName);
                var full = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
                if (!string.Equals(full, target, StringComparison.OrdinalIgnoreCase)) continue;
                if (info.State == 0 && active == 0) active = info.SessionId;
                if (info.State == 4 && disconnected == 0) disconnected = info.SessionId;
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return active != 0 ? active : disconnected;
    }

    public static bool IsHelperRunning(int sessionId)
    {
        try
        {
            return Process.GetProcessesByName("OpControl.Parental.Helper").Any(p =>
            {
                try { return p.SessionId == sessionId; }
                catch { return false; }
            });
        }
        catch { return false; }
    }

    public static bool LaunchHelper(int sessionId, string helperPath, string monitoredAccount, ILogger logger)
    {
        if (!File.Exists(helperPath))
        {
            logger.LogError("Helper not found at {Path}", helperPath);
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            logger.LogWarning("WTSQueryUserToken failed for session {Session}: {Error}", sessionId, Marshal.GetLastWin32Error());
            return false;
        }

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                logger.LogWarning("DuplicateTokenEx failed: {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            CreateEnvironmentBlock(out environment, primaryToken, false);
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };
            var command = $"\"{helperPath}\" --monitored \"{monitoredAccount}\"";
            var ok = CreateProcessAsUser(primaryToken, null, command, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT, environment, Path.GetDirectoryName(helperPath), ref si, out var pi);
            if (!ok)
            {
                logger.LogWarning("CreateProcessAsUser failed: {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            logger.LogInformation("Launched helper in session {Session} as monitored account {Account}", sessionId, monitoredAccount);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    public static int GetPipeClientSessionId(SafePipeHandle pipe) => GetPipeClientProcessId(pipe) is var pid && pid > 0
        ? GetProcessSessionId(pid)
        : -1;

    public static int GetPipeClientProcessId(SafePipeHandle pipe)
    {
        try
        {
            return GetNamedPipeClientProcessId(pipe, out var pid) ? (int)pid : -1;
        }
        catch { return -1; }
    }

    public static int GetProcessSessionId(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.SessionId;
        }
        catch { return -1; }
    }

    public static string? GetProcessImagePath(int pid)
    {
        if (pid <= 0) return null;
        var handle = OpenProcess(0x1000, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(2048);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string QueryString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes))
            return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }
}

public sealed class HelperSupervisor : BackgroundService
{
    private readonly ClientSettings _settings;
    private readonly DataSyncService _sync;
    private readonly ILogger<HelperSupervisor> _logger;
    private DateTime _lastLog = DateTime.MinValue;
    private bool _wasRunning;
    private DateTime _lastKilledReport = DateTime.MinValue;
    private readonly Queue<DateTime> _recentKills = new();

    public HelperSupervisor(ClientSettings settings, DataSyncService sync, ILogger<HelperSupervisor> logger)
    {
        _settings = settings;
        _sync = sync;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var options = _settings.Value;
                var session = WindowsSession.GetMonitoredSessionId(options.MonitoredAccount);
                if (session == 0)
                {
                    if (DateTime.UtcNow - _lastLog > TimeSpan.FromMinutes(5))
                    {
                        _logger.LogInformation("No session for monitored account {Account}", options.MonitoredAccount);
                        _lastLog = DateTime.UtcNow;
                    }
                    continue;
                }

                var running = WindowsSession.IsHelperRunning(session);
                if (_wasRunning && !running)
                {
                    var now = DateTime.UtcNow;
                    _recentKills.Enqueue(now);
                    while (_recentKills.Count > 0 && now - _recentKills.Peek() > TimeSpan.FromMinutes(10))
                        _recentKills.Dequeue();
                    if (now - _lastKilledReport > TimeSpan.FromMinutes(1))
                    {
                        _lastKilledReport = now;
                        _sync.QueueTamper(new TamperEventDto("helper-killed",
                            _recentKills.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            now));
                        _logger.LogWarning("Helper stopped unexpectedly (count {Count} in 10 min)", _recentKills.Count);
                    }
                }
                _wasRunning = running;

                if (!running)
                {
                    var path = string.IsNullOrWhiteSpace(options.HelperPath)
                        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helper", "OpControl.Parental.Helper.exe"))
                        : options.HelperPath!;
                    WindowsSession.LaunchHelper(session, path, options.MonitoredAccount, _logger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Helper supervisor failed");
            }
        }
    }
}
