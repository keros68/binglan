using System.IO;
using System.Windows;

namespace BingLan.App;

public partial class App : System.Windows.Application
{
    public const string SuppressCoordinatorStartupSwitch =
        "BingLan.SuppressCoordinatorStartupForTests";
    private const string ProductionInstanceName = "Local\\BingLan.Desktop.Production";
    private const string InteractiveQaInstanceName = "Local\\BingLan.Desktop.InteractiveQa";
    public const string WaitForPreviousInstanceSwitch = "--wait-for-previous";
    /// <summary>
    /// Asks the running instance to save and exit the same way as the tray's Exit, then
    /// starts in its place: switching builds without quitting by hand or ending a process.
    /// </summary>
    public const string ReplaceInstanceSwitch = "--replace";
    private static readonly TimeSpan PreviousInstanceTimeout = TimeSpan.FromSeconds(15);

    private WidgetCoordinator? _coordinator;
    private SingleInstanceGate? _singleInstanceGate;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private bool _startupCompleted;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();
        try
        {
            base.OnStartup(e);
            UseSoftwareRendering();
            AccessibilityThemeManager.EnsureInitialized();
            // Use BitmapFrame.Create so IconBitmapDecoder keeps all ICO frames (16–256 px)
            // and WPF sets both ICON_BIG (taskbar/Alt+Tab) and ICON_SMALL (title bar) cleanly.
            var appIcon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/BingLan;component/Assets/Brand/BingLan.ico", UriKind.Absolute));
            if (appIcon.CanFreeze)
            {
                appIcon.Freeze();
            }
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is Window { Icon: null } window)
                    {
                        window.Icon = appIcon;
                    }
                }));
            if (AppContext.TryGetSwitch(SuppressCoordinatorStartupSwitch, out var suppress) && suppress)
            {
                return;
            }
            var interactiveQa = e.Args.Contains(
                "--interactive-qa",
                StringComparer.OrdinalIgnoreCase);
            var dataDirectory = interactiveQa
                ? Path.Combine(Path.GetTempPath(), "BingLanWidgets-InteractiveQa")
                : null;
            if (e.Args.Contains("--restore-taskbar", StringComparer.OrdinalIgnoreCase))
            {
                // Used by an uninstaller or by hand: undo taskbar and desktop icon changes
                // left by a crash.
                var directory = new Core.Services.LocalStateStore(dataDirectory).DataDirectory;
                var taskbarRestored = Taskbar.TaskbarAdapter.RecoverFromCheckpoint(
                    WidgetCoordinator.TaskbarCheckpointPath(directory));
                var iconsRestored = Services.CleanDesktopAdapter.RecoverNow(
                    WidgetCoordinator.CleanDesktopCheckpointPath(directory));
                Shutdown(taskbarRestored && iconsRestored ? 0 : 1);
                return;
            }
            var instanceName = interactiveQa
                ? InteractiveQaInstanceName
                : ProductionInstanceName;
            var replace = e.Args.Contains(ReplaceInstanceSwitch, StringComparer.OrdinalIgnoreCase);
            if (replace)
            {
                SignalRunningInstanceToExit(instanceName);
            }
            if (!TryAcquireInstance(
                    instanceName,
                    replace || e.Args.Contains(WaitForPreviousInstanceSwitch, StringComparer.OrdinalIgnoreCase)))
            {
                Shutdown();
                return;
            }
            _coordinator = new WidgetCoordinator(interactiveQa, dataDirectory, showOnboarding: true);
            _coordinator.Start();
            _startupCompleted = true;
            if (interactiveQa && e.Args.Contains("--open-launcher", StringComparer.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(_coordinator.ToggleLauncherPanel,
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            SessionEnding += (_, _) => _coordinator?.OnQueryEndSession();
            ListenForExitSignal(instanceName);
        }
        catch (Exception ex)
        {
            Services.StartupLog.WriteCrash("Startup", ex);
            Shutdown(1);
        }
    }

    private void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Services.StartupLog.WriteCrash(
                args.IsTerminating ? "AppDomain.Fatal" : "AppDomain",
                args.ExceptionObject);
        // Once running, one failed action (a shell call, a damaged file) is logged and
        // reported instead of closing every card; startup failures are logged and exit cleanly.
        DispatcherUnhandledException += (_, args) =>
        {
            Services.StartupLog.WriteCrash("Dispatcher", args.Exception);
            if (_startupCompleted)
            {
                args.Handled = true;
                _coordinator?.ReportFailedAction();
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.StartupLog.WriteCrash("TaskScheduler", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// The cards are small and mostly still, so software rendering draws them without
    /// loading the graphics driver into the process. Measured on a 1440p desktop with an
    /// NVIDIA GPU: private memory 160 MB -> 89 MB and idle CPU 4.9% -> 2.6% of a core.
    /// </summary>
    public static void UseSoftwareRendering() =>
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

    // After a restore the new instance starts while the old one is still saving and
    // exiting, so it waits briefly for the single-instance gate instead of quitting.
    private bool TryAcquireInstance(string instanceName, bool waitForPrevious)
    {
        var deadline = DateTime.UtcNow + (waitForPrevious ? PreviousInstanceTimeout : TimeSpan.Zero);
        while (true)
        {
            if (SingleInstanceGate.TryAcquire(instanceName, out _singleInstanceGate))
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Thread.Sleep(200);
        }
    }

    private static string ExitSignalName(string instanceName) => instanceName + ".Exit";

    private static void SignalRunningInstanceToExit(string instanceName)
    {
        if (EventWaitHandle.TryOpenExisting(ExitSignalName(instanceName), out var signal))
        {
            using (signal)
            {
                signal.Set();
            }
        }
    }

    // The running instance owns the exit signal; a later "--replace" start sets it.
    private void ListenForExitSignal(string instanceName)
    {
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName(instanceName));
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _coordinator?.RequestExit()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        Services.UiDiagnostics.Stop();
        _coordinator?.Dispose();
        _singleInstanceGate?.Dispose();
        base.OnExit(e);
    }
}
