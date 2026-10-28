using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int AlreadyRunning = 0;
    public const int Mutex = 1;
    public const int Config = 2;
    public const int ServiceTimeout = 3;
    public const int Unstable = 4;
    public const int RetryLimit = 5;
    public const int LaunchError = 6;
}

internal sealed class GuardConfig
{
    public string service_name { get; set; }
    public string nss_process_name { get; set; }
    public string package_app_user_model_id { get; set; }
    public string nss_arguments { get; set; }
    public double startup_timeout_sec { get; set; }
    public double recovery_timeout_sec { get; set; }
    public double poll_interval_sec { get; set; }
    public double prelaunch_stable_sec { get; set; }
    public double observation_sec { get; set; }
    public double nss_start_grace_sec { get; set; }
    public double retry_backoff_sec { get; set; }
    public int max_launch_attempts { get; set; }
    public int launcher_output_limit { get; set; }
    public int log_max_bytes { get; set; }
    public int log_backup_count { get; set; }

    public static GuardConfig Load(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> values = serializer.Deserialize<Dictionary<string, object>>(json);
        HashSet<string> allowed = new HashSet<string>(typeof(GuardConfig).GetProperties().Select(p => p.Name));
        string[] unknown = values.Keys.Where(k => !allowed.Contains(k)).OrderBy(k => k).ToArray();
        if (unknown.Length > 0)
            throw new InvalidDataException("Unknown config keys: " + string.Join(", ", unknown));
        GuardConfig config = serializer.Deserialize<GuardConfig>(json);
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(service_name) || string.IsNullOrWhiteSpace(nss_process_name) || string.IsNullOrWhiteSpace(package_app_user_model_id))
            throw new InvalidDataException("Service name, NSS process name and package application ID are required");
        if (startup_timeout_sec <= 0 || recovery_timeout_sec <= 0 || poll_interval_sec <= 0 || observation_sec <= 0 || nss_start_grace_sec <= 0)
            throw new InvalidDataException("Timeouts, poll interval and observation windows must be greater than zero");
        if (prelaunch_stable_sec < 0 || retry_backoff_sec < 0)
            throw new InvalidDataException("Stable and retry delays cannot be negative");
        if (nss_start_grace_sec >= observation_sec)
            throw new InvalidDataException("nss_start_grace_sec must be less than observation_sec");
        if (max_launch_attempts < 1 || max_launch_attempts > 5)
            throw new InvalidDataException("max_launch_attempts must be between 1 and 5");
        if (launcher_output_limit < 1000)
            throw new InvalidDataException("launcher_output_limit must be at least 1000");
        if (log_max_bytes <= 0 || log_backup_count < 0 || log_backup_count > 10)
            throw new InvalidDataException("Invalid log rotation settings");
    }
}

internal interface IClock
{
    double Now { get; }
    void Sleep(double seconds);
}

internal sealed class SystemClock : IClock
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    public double Now { get { return watch.Elapsed.TotalSeconds; } }
    public void Sleep(double seconds) { Thread.Sleep(TimeSpan.FromSeconds(seconds)); }
}

internal interface IGuardLogger
{
    void Info(string message, params object[] args);
    void Warn(string message, params object[] args);
    void Error(string message, params object[] args);
}

internal sealed class NullLogger : IGuardLogger
{
    public void Info(string message, params object[] args) { }
    public void Warn(string message, params object[] args) { }
    public void Error(string message, params object[] args) { }
}

internal sealed class GuardLogger : IGuardLogger
{
    private readonly object sync = new object();
    private readonly string path;
    private readonly int maxBytes;
    private readonly int backupCount;

    public GuardLogger(string directory, GuardConfig config)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "nss_startup_guard.log");
        maxBytes = config.log_max_bytes;
        backupCount = config.log_backup_count;
    }

    public void Info(string message, params object[] args) { Write("INFO", message, args); }
    public void Warn(string message, params object[] args) { Write("WARN", message, args); }
    public void Error(string message, params object[] args) { Write("ERROR", message, args); }

    private void Write(string level, string message, object[] args)
    {
        string text = string.Format(message, args);
        string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1} {2}", DateTime.Now, level, text);
        lock (sync)
        {
            RotateIfNeeded();
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        Console.WriteLine(line);
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(path) || new FileInfo(path).Length < maxBytes)
            return;
        if (backupCount == 0)
        {
            File.Delete(path);
            return;
        }
        string oldest = path + "." + backupCount;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = backupCount - 1; i >= 1; i--)
        {
            string source = path + "." + i;
            if (File.Exists(source)) File.Move(source, path + "." + (i + 1));
        }
        File.Move(path, path + ".1");
    }
}

internal sealed class LaunchSnapshot
{
    public bool Exited { get; set; }
    public int? ExitCode { get; set; }
    public string Stdout { get; set; }
    public string Stderr { get; set; }
}

internal interface ILaunchSession : IDisposable
{
    LaunchSnapshot Snapshot();
}

internal sealed class BoundedCapture
{
    private readonly object sync = new object();
    private readonly StringBuilder text = new StringBuilder();
    private readonly int limit;
    private bool truncated;

    public BoundedCapture(int limit) { this.limit = limit; }

    public void Add(string value)
    {
        if (value == null) return;
        lock (sync)
        {
            int remaining = limit - text.Length;
            if (remaining > 0)
            {
                string part = value.Length <= remaining ? value : value.Substring(0, remaining);
                text.AppendLine(part);
            }
            if (value.Length + Environment.NewLine.Length > remaining) truncated = true;
        }
    }

    public string Text()
    {
        lock (sync)
            return text.ToString() + (truncated ? "[output truncated]" : "");
    }
}

internal sealed class WindowsLaunchSession : ILaunchSession
{
    private readonly Process process;
    private readonly BoundedCapture stdout;
    private readonly BoundedCapture stderr;

    public WindowsLaunchSession(Process process, int outputLimit)
    {
        this.process = process;
        stdout = new BoundedCapture(outputLimit);
        stderr = new BoundedCapture(outputLimit);
        if (process.StartInfo.RedirectStandardOutput && process.StartInfo.RedirectStandardError)
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { stdout.Add(e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { stderr.Add(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
    }

    public LaunchSnapshot Snapshot()
    {
        bool exited = process.HasExited;
        return new LaunchSnapshot
        {
            Exited = exited,
            ExitCode = exited ? (int?)process.ExitCode : null,
            Stdout = stdout.Text(),
            Stderr = stderr.Text()
        };
    }

    public void Dispose() { process.Dispose(); }
}

internal sealed class PackageLaunchSession : ILaunchSession
{
    private readonly uint processId;
    public PackageLaunchSession(uint processId) { this.processId = processId; }
    public LaunchSnapshot Snapshot()
    {
        return new LaunchSnapshot { Exited = true, ExitCode = 0, Stdout = "", Stderr = "" };
    }
    public void Dispose() { }
}

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments,
        uint options,
        out uint processId);
}

[ComImport, Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
internal class ApplicationActivationManager { }

internal interface ISystemAdapter
{
    int? ServicePid();
    bool? NssRunning();
    ILaunchSession LaunchNss();
    LaunchSnapshot LaunchSnapshot(ILaunchSession session);
}

internal sealed class WindowsSystemAdapter : ISystemAdapter
{
    private readonly GuardConfig config;
    public WindowsSystemAdapter(GuardConfig config) { this.config = config; }

    private string Run(string fileName, string arguments)
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(fileName, arguments);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000)) return null;
                return process.ExitCode == 0 ? output : null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public int? ServicePid()
    {
        string output = Run("sc.exe", "queryex " + config.service_name);
        if (output == null) return null;
        Match state = Regex.Match(output, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        Match pid = Regex.Match(output, @"PID\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        if (!state.Success || !pid.Success || int.Parse(state.Groups[1].Value) != 4) return null;
        int value = int.Parse(pid.Groups[1].Value);
        return value > 0 ? (int?)value : null;
    }

    public bool? NssRunning()
    {
        string output = Run("tasklist.exe", "/FI \"IMAGENAME eq " + config.nss_process_name + "\" /FO CSV /NH");
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = Regex.Match(line, "^\"([^\"]+)\"");
            if (match.Success && string.Equals(match.Groups[1].Value, config.nss_process_name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public ILaunchSession LaunchNss()
    {
        IApplicationActivationManager manager = (IApplicationActivationManager)new ApplicationActivationManager();
        uint processId;
        int result = manager.ActivateApplication(config.package_app_user_model_id, config.nss_arguments, 0, out processId);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return new PackageLaunchSession(processId);
    }

    public LaunchSnapshot LaunchSnapshot(ILaunchSession session) { return session.Snapshot(); }
}

internal sealed class StartupGuard
{
    private readonly GuardConfig config;
    private readonly ISystemAdapter adapter;
    private readonly IGuardLogger logger;
    private readonly IClock clock;

    public StartupGuard(GuardConfig config, ISystemAdapter adapter, IGuardLogger logger, IClock clock)
    {
        this.config = config;
        this.adapter = adapter;
        this.logger = logger;
        this.clock = clock;
    }

    private int? WaitStableService(double timeout, string context)
    {
        double deadline = clock.Now + timeout;
        double stableSince = -1;
        int? currentPid = null;
        logger.Info("Waiting for stable Uniwill Service ({0})", context);
        while (clock.Now < deadline)
        {
            int? pid = adapter.ServicePid();
            if (!pid.HasValue)
            {
                if (currentPid.HasValue) logger.Warn("Service stopped or became unavailable while waiting");
                currentPid = null;
                stableSince = -1;
            }
            else if (!currentPid.HasValue || currentPid.Value != pid.Value)
            {
                logger.Info("Service running with PID {0}", pid.Value);
                currentPid = pid;
                stableSince = clock.Now;
            }
            else if (stableSince >= 0 && clock.Now - stableSince >= config.prelaunch_stable_sec)
            {
                logger.Info("Service PID {0} remained stable", pid.Value);
                return pid;
            }
            clock.Sleep(config.poll_interval_sec);
        }
        logger.Error("Timed out waiting for stable service ({0})", context);
        return null;
    }

    private string ObserveAfterLaunch(int servicePid, ILaunchSession session)
    {
        double deadline = clock.Now + config.observation_sec;
        bool nssSeen = false;
        bool launcherReported = session == null;
        logger.Info("Observing NSS and service PID {0}", servicePid);
        while (clock.Now < deadline)
        {
            int? pid = adapter.ServicePid();
            if (!pid.HasValue || pid.Value != servicePid)
            {
                logger.Warn("Service stopped or restarted during NSS observation");
                return "service_failed";
            }
            if (session != null && !launcherReported)
            {
                LaunchSnapshot snapshot = adapter.LaunchSnapshot(session);
                if (snapshot.Exited)
                {
                    logger.Info("Launcher exited with code {0}; stdout={1}; stderr={2}", snapshot.ExitCode, snapshot.Stdout, snapshot.Stderr);
                    launcherReported = true;
                }
            }
            bool? alive = adapter.NssRunning();
            if (!alive.HasValue)
            {
                logger.Error("NSS process state is unknown; refusing further launch decisions");
                return "state_unknown";
            }
            nssSeen = nssSeen || alive.Value;
            if (nssSeen && !alive.Value)
            {
                logger.Warn("NSS exited during observation");
                return "nss_exited";
            }
            clock.Sleep(config.poll_interval_sec);
        }
        if (!nssSeen)
        {
            if (session != null && !adapter.LaunchSnapshot(session).Exited)
            {
                logger.Error("Launcher is still running and NSS has not appeared; retry is unsafe");
                return "launcher_running";
            }
            logger.Warn("NSS did not appear during the handoff observation window");
            return "nss_missing";
        }
        logger.Info("Observation completed without a service restart");
        return "stable";
    }

    public int Run()
    {
        List<ILaunchSession> sessions = new List<ILaunchSession>();
        try
        {
            bool? initial = adapter.NssRunning();
            if (!initial.HasValue) return ExitCodes.Unstable;
            if (initial.Value) return ExitCodes.AlreadyRunning;
            int attempts = 0;
            while (attempts < config.max_launch_attempts)
            {
                int? servicePid = WaitStableService(config.startup_timeout_sec, "initial");
                if (!servicePid.HasValue) return ExitCodes.ServiceTimeout;
                bool? current = adapter.NssRunning();
                if (!current.HasValue) return ExitCodes.Unstable;
                if (current.Value) return ExitCodes.AlreadyRunning;

                attempts++;
                logger.Info("Launching NSS, attempt {0} of {1}", attempts, config.max_launch_attempts);
                ILaunchSession session;
                try { session = adapter.LaunchNss(); }
                catch (Exception ex)
                {
                    logger.Error("Unable to launch NSS: {0}", ex);
                    return ExitCodes.LaunchError;
                }
                sessions.Add(session);
                string outcome = ObserveAfterLaunch(servicePid.Value, session);
                if (outcome == "stable") return ExitCodes.Ok;
                if (outcome == "state_unknown" || outcome == "launcher_running") return ExitCodes.Unstable;

                if (outcome == "service_failed")
                {
                    bool? alive = adapter.NssRunning();
                    if (!alive.HasValue) return ExitCodes.Unstable;
                    logger.Info("Waiting for service recovery; NSS alive={0}", alive.Value);
                    int? recovered = WaitStableService(config.recovery_timeout_sec, "recovery");
                    if (!recovered.HasValue) return ExitCodes.ServiceTimeout;
                    alive = adapter.NssRunning();
                    if (!alive.HasValue) return ExitCodes.Unstable;
                    if (alive.Value)
                    {
                        string recoveryOutcome = ObserveAfterLaunch(recovered.Value, null);
                        if (recoveryOutcome == "stable" && adapter.NssRunning() == true) return ExitCodes.Ok;
                        return ExitCodes.Unstable;
                    }
                }

                if (attempts >= config.max_launch_attempts) break;
                bool? retryState = adapter.NssRunning();
                if (!retryState.HasValue) return ExitCodes.Unstable;
                if (retryState.Value) return ExitCodes.Ok;
                if (!adapter.LaunchSnapshot(session).Exited) return ExitCodes.Unstable;
                clock.Sleep(config.retry_backoff_sec);
            }
            return ExitCodes.RetryLimit;
        }
        finally
        {
            foreach (ILaunchSession session in sessions) session.Dispose();
        }
    }
}

internal static class ConsoleWindow
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public static bool Open()
    {
        if (GetConsoleWindow() != IntPtr.Zero) return false;
        if (AttachConsole(AttachParentProcess))
        {
            ResetStandardStreams();
            return false;
        }
        if (!AllocConsole()) return false;
        ResetStandardStreams();
        return true;
    }

    private static void ResetStandardStreams()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }
}

internal static class FailureConsole
{
    public static void Show(string baseDirectory, int exitCode, string detail)
    {
        bool ownsConsole = ConsoleWindow.Open();
        Console.WriteLine("NssStartupGuard failed with exit code {0}.", exitCode);
        if (!string.IsNullOrWhiteSpace(detail)) Console.WriteLine(detail);
        string logPath = Path.Combine(baseDirectory, "logs", "nss_startup_guard.log");
        Console.WriteLine();
        if (File.Exists(logPath))
        {
            Console.WriteLine("Log tail: " + logPath);
            Console.WriteLine(ReadTail(logPath, 32768).TrimEnd());
        }
        else
        {
            Console.WriteLine("No log file was created at: " + logPath);
        }
        if (ownsConsole)
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close this window.");
            try { Console.ReadLine(); }
            catch (IOException) { }
        }
    }

    private static string ReadTail(string path, int maximumBytes)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long offset = Math.Max(0, stream.Length - maximumBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string text = reader.ReadToEnd();
                if (offset > 0)
                {
                    int firstNewLine = text.IndexOf('\n');
                    if (firstNewLine >= 0 && firstNewLine + 1 < text.Length) text = text.Substring(firstNewLine + 1);
                }
                return text;
            }
        }
    }
}

internal static class Program
{
    private static string failureDetail;

    public static int Main(string[] args)
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        bool diagnostic = args.Any(arg => arg == "--check-config" || arg == "--self-test" || arg == "--activate-only");
        bool ownsDiagnosticConsole = diagnostic && ConsoleWindow.Open();
        int exitCode;
        try
        {
            exitCode = Run(args, baseDirectory);
        }
        catch (Exception ex)
        {
            failureDetail = "Unexpected error: " + ex;
            exitCode = ExitCodes.Unstable;
        }
        if (exitCode != ExitCodes.Ok)
        {
            if (!diagnostic) FailureConsole.Show(baseDirectory, exitCode, failureDetail);
            else if (ownsDiagnosticConsole)
            {
                Console.WriteLine("Press Enter to close this window.");
                try { Console.ReadLine(); }
                catch (IOException) { }
            }
        }
        return exitCode;
    }

    private static int Run(string[] args, string baseDirectory)
    {
        string configPath = Path.Combine(baseDirectory, "nss_startup_guard.config.json");
        bool checkOnly = false;
        bool selfTest = false;
        bool activateOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] == "--check-config") checkOnly = true;
            else if (args[i] == "--self-test") selfTest = true;
            else if (args[i] == "--activate-only") activateOnly = true;
            else
            {
                failureDetail = "Usage: NssStartupGuard.exe [--config path] [--check-config] [--self-test] [--activate-only]";
                Console.Error.WriteLine(failureDetail);
                return ExitCodes.Config;
            }
        }
        if (selfTest) return SelfTests.RunAll();
        GuardConfig config;
        try { config = GuardConfig.Load(configPath); }
        catch (Exception ex)
        {
            failureDetail = "Configuration error: " + ex.Message;
            Console.Error.WriteLine(failureDetail);
            return ExitCodes.Config;
        }
        if (checkOnly)
        {
            Console.WriteLine("Configuration is valid; no NUC component was queried or launched");
            return ExitCodes.Ok;
        }
        if (activateOnly)
        {
            try
            {
                using (ILaunchSession session = new WindowsSystemAdapter(config).LaunchNss())
                {
                    Console.WriteLine("Package activation succeeded");
                    return ExitCodes.Ok;
                }
            }
            catch (Exception ex)
            {
                failureDetail = "Package activation failed: " + ex;
                Console.Error.WriteLine(failureDetail);
                return ExitCodes.LaunchError;
            }
        }
        GuardLogger logger = new GuardLogger(Path.Combine(baseDirectory, "logs"), config);
        bool createdNew;
        using (Mutex mutex = new Mutex(false, @"Local\NssStartupGuard-8F2C77C5", out createdNew))
        {
            if (!createdNew)
            {
                logger.Warn("Another startup guard instance is already active");
                logger.Error("Startup guard failed with exit code {0}", ExitCodes.Mutex);
                return ExitCodes.Mutex;
            }
            StartupGuard guard = new StartupGuard(config, new WindowsSystemAdapter(config), logger, new SystemClock());
            int exitCode = guard.Run();
            if (exitCode == ExitCodes.Ok) logger.Info("Startup guard completed successfully");
            else logger.Error("Startup guard failed with exit code {0}", exitCode);
            return exitCode;
        }
    }
}

internal sealed class FakeClock : IClock
{
    private double now;
    public double Now { get { return now; } }
    public void Sleep(double seconds) { now += seconds; }
    public void Set(double value) { now = value; }
}

internal sealed class FakeEvent
{
    public double At;
    public int? ServicePid;
    public bool ServiceSpecified;
    public bool? Nss;
    public bool NssSpecified;
}

internal sealed class FakeLaunchSession : ILaunchSession
{
    private readonly FakeAdapter adapter;
    public FakeLaunchSession(FakeAdapter adapter) { this.adapter = adapter; }
    public LaunchSnapshot Snapshot()
    {
        return new LaunchSnapshot { Exited = adapter.LauncherExited, ExitCode = adapter.LauncherExited ? (int?)0 : null, Stdout = "mock stdout", Stderr = "mock stderr" };
    }
    public void Dispose() { }
}

internal sealed class FakeAdapter : ISystemAdapter
{
    private readonly FakeClock clock;
    private readonly List<FakeEvent> events;
    private int? servicePid;
    private bool? nss;
    public int LaunchCount;
    public readonly List<double> LaunchTimes = new List<double>();
    public bool AutoStartNss = true;
    public bool LauncherExited = true;
    public bool ThrowOnLaunch;

    public FakeAdapter(FakeClock clock, int? servicePid, bool? nss, params FakeEvent[] events)
    {
        this.clock = clock;
        this.servicePid = servicePid;
        this.nss = nss;
        this.events = events.OrderBy(e => e.At).ToList();
    }

    private void ApplyEvents()
    {
        foreach (FakeEvent item in events.Where(e => e.At <= clock.Now))
        {
            if (item.ServiceSpecified) servicePid = item.ServicePid;
            if (item.NssSpecified) nss = item.Nss;
        }
        events.RemoveAll(e => e.At <= clock.Now);
    }

    public int? ServicePid() { ApplyEvents(); return servicePid; }
    public bool? NssRunning() { ApplyEvents(); return nss; }
    public ILaunchSession LaunchNss()
    {
        LaunchCount++;
        LaunchTimes.Add(clock.Now);
        if (ThrowOnLaunch) throw new IOException("mock launch failure");
        if (AutoStartNss) nss = true;
        return new FakeLaunchSession(this);
    }
    public LaunchSnapshot LaunchSnapshot(ILaunchSession session) { return session.Snapshot(); }
}

internal static class SelfTests
{
    private static int failures;
    private static GuardConfig Config()
    {
        return new GuardConfig
        {
            service_name = "UniwillService", nss_process_name = "NucSoftwareStudio.exe", package_app_user_model_id = "Mock.Package!App", nss_arguments = "tray",
            startup_timeout_sec = 15, recovery_timeout_sec = 15, poll_interval_sec = 1, prelaunch_stable_sec = 2,
            observation_sec = 6, nss_start_grace_sec = 2, retry_backoff_sec = 1, max_launch_attempts = 2,
            launcher_output_limit = 16000, log_max_bytes = 1000000, log_backup_count = 3
        };
    }

    private static StartupGuard Guard(FakeAdapter adapter, FakeClock clock, GuardConfig config)
    {
        return new StartupGuard(config, adapter, new NullLogger(), clock);
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(string.Format("{0}: expected {1}, actual {2}", name, expected, actual));
    }

    private static void True(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    public static int RunAll()
    {
        failures = 0;
        Run("normal start", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false); Equal(0, Guard(a, c, Config()).Run(), "exit"); Equal(1, a.LaunchCount, "launches"); Equal(2.0, a.LaunchTimes[0], "launch time"); });
        Run("existing NSS", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, true); Equal(0, Guard(a, c, Config()).Run(), "exit"); Equal(0, a.LaunchCount, "launches"); });
        Run("delayed service", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, null, false, new FakeEvent { At = 4, ServiceSpecified = true, ServicePid = 200 }); Equal(0, Guard(a, c, Config()).Run(), "exit"); Equal(6.0, a.LaunchTimes[0], "launch time"); });
        Run("prelaunch crash", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false, new FakeEvent { At = 1, ServiceSpecified = true, ServicePid = null }, new FakeEvent { At = 3, ServiceSpecified = true, ServicePid = 200 }); Equal(0, Guard(a, c, Config()).Run(), "exit"); Equal(5.0, a.LaunchTimes[0], "launch time"); });
        Run("service crash live NSS", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false, new FakeEvent { At = 4, ServiceSpecified = true, ServicePid = null }, new FakeEvent { At = 6, ServiceSpecified = true, ServicePid = 200 }); Equal(0, Guard(a, c, Config()).Run(), "exit"); Equal(1, a.LaunchCount, "launches"); });
        Run("service crash exited NSS", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false, new FakeEvent { At = 4, ServiceSpecified = true, ServicePid = null, NssSpecified = true, Nss = false }, new FakeEvent { At = 6, ServiceSpecified = true, ServicePid = 200 }); Equal(0, Guard(a, c, Config()).Run(), "exit"); Equal(2, a.LaunchCount, "launches"); Equal(11.0, a.LaunchTimes[1], "second launch"); });
        Run("missing NSS retry limit", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false); a.AutoStartNss = false; Equal(5, Guard(a, c, Config()).Run(), "exit"); Equal(2, a.LaunchCount, "launches"); });
        Run("service timeout", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, null, false); Equal(3, Guard(a, c, Config()).Run(), "exit"); Equal(0, a.LaunchCount, "launches"); });
        Run("failed recovery live NSS", delegate { GuardConfig cfg = Config(); cfg.recovery_timeout_sec = 3; FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false, new FakeEvent { At = 4, ServiceSpecified = true, ServicePid = null }); Equal(3, Guard(a, c, cfg).Run(), "exit"); Equal(1, a.LaunchCount, "launches"); });
        Run("second service crash", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false, new FakeEvent { At = 4, ServiceSpecified = true, ServicePid = null }, new FakeEvent { At = 6, ServiceSpecified = true, ServicePid = 200 }, new FakeEvent { At = 10, ServiceSpecified = true, ServicePid = null }); Equal(4, Guard(a, c, Config()).Run(), "exit"); Equal(1, a.LaunchCount, "launches"); });
        Run("launch error", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false); a.ThrowOnLaunch = true; Equal(6, Guard(a, c, Config()).Run(), "exit"); Equal(1, a.LaunchCount, "launches"); });
        Run("invalid retry limit", delegate { GuardConfig cfg = Config(); cfg.max_launch_attempts = 20; bool threw = false; try { cfg.Validate(); } catch (InvalidDataException) { threw = true; } True(threw, "validation"); });
        Run("invalid grace window", delegate { GuardConfig cfg = Config(); cfg.nss_start_grace_sec = 6; bool threw = false; try { cfg.Validate(); } catch (InvalidDataException) { threw = true; } True(threw, "validation"); });
        Run("unknown NSS state", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, null); Equal(4, Guard(a, c, Config()).Run(), "exit"); Equal(0, a.LaunchCount, "launches"); });
        Run("running launcher", delegate { FakeClock c = new FakeClock(); FakeAdapter a = new FakeAdapter(c, 100, false); a.AutoStartNss = false; a.LauncherExited = false; Equal(4, Guard(a, c, Config()).Run(), "exit"); Equal(1, a.LaunchCount, "launches"); });
        Run("service output parsing", delegate { Match state = Regex.Match(" STATE : 4 RUNNING\n PID : 4321", @"STATE\s*:\s*(\d+)"); Match pid = Regex.Match(" STATE : 4 RUNNING\n PID : 4321", @"PID\s*:\s*(\d+)"); Equal("4", state.Groups[1].Value, "state"); Equal("4321", pid.Groups[1].Value, "pid"); });
        Run("NSS output parsing", delegate { Match match = Regex.Match("\"NucSoftwareStudio.exe\",\"1234\",\"Console\"", "^\"([^\"]+)\""); Equal("NucSoftwareStudio.exe", match.Groups[1].Value, "process"); });
        Run("mutex", delegate { bool firstCreated; bool secondCreated; using (Mutex first = new Mutex(false, @"Local\NssStartupGuard-Test-" + Guid.NewGuid(), out firstCreated)) { string name = "Local\\NssStartupGuard-Test-Shared-" + Guid.NewGuid(); using (Mutex one = new Mutex(false, name, out firstCreated)) { using (Mutex two = new Mutex(false, name, out secondCreated)) { True(firstCreated && !secondCreated, "mutex state"); } } } });
        Run("bounded output capture", delegate { BoundedCapture capture = new BoundedCapture(10); capture.Add("123456789012345"); True(capture.Text().Contains("[output truncated]"), "truncation"); });
        Console.WriteLine(failures == 0 ? "All 19 tests passed; no NUC component was queried or launched" : failures + " test(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
