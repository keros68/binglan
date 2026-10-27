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
    private static readonly TimeSpan PreviousInstanceTimeout = TimeSpan.FromSeconds(15);

    private WidgetCoordinator? _coordinator;
    private SingleInstanceGate? _singleInstanceGate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UseSoftwareRendering();
        AccessibilityThemeManager.EnsureInitialized();
        // Every window shows the app icon in the taskbar and Alt+Tab instead of a blank one.
        var appIcon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/BingLan;component/Assets/Brand/BingLan.ico", UriKind.Absolute));
        appIcon.Freeze();
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
        if (!TryAcquireInstance(
                instanceName,
                e.Args.Contains(WaitForPreviousInstanceSwitch, StringComparer.OrdinalIgnoreCase)))
        {
            Shutdown();
            return;
        }
        _coordinator = new WidgetCoordinator(interactiveQa, dataDirectory, showOnboarding: true);
        _coordinator.Start();
        // Once running, one failed action (a shell call, a damaged file) is logged and
        // reported instead of closing every card; startup failures still end the process.
        DispatcherUnhandledException += (_, args) =>
        {
            Services.StartupLog.Write($"未处理异常：{args.Exception}");
            args.Handled = true;
            _coordinator?.ReportFailedAction();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.StartupLog.Write($"未观察的后台异常：{args.Exception}");
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

    protected override void OnExit(ExitEventArgs e)
    {
        Services.UiDiagnostics.Stop();
        _coordinator?.Dispose();
        _singleInstanceGate?.Dispose();
        base.OnExit(e);
    }
}
