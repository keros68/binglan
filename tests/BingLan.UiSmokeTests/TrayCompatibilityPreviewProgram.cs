using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

/// <summary>Explicit publish entry for the portable compatibility preview only.</summary>
internal static class TrayCompatibilityPreviewProgram
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            Directory.CreateDirectory("artifacts");
            var path = Path.Combine("artifacts", $"tray-preview-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            using var log = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
            try
            {
                Console.WriteLine($"VERSION {typeof(TrayCompatibilityPreviewProgram).Assembly.GetName().Version}");
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                AppContext.SetSwitch(BingLan.App.App.SuppressCoordinatorStartupSwitch, true);
                var app = new BingLan.App.App();
                app.InitializeComponent();
                BingLan.App.App.UseSoftwareRendering();
                ShellSurfacePreview.Run(app);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                throw;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法完成托盘验收：{exception.Message}\n请检查工具目录是否可写，并查看 artifacts 中的日志。",
                "冰蓝托盘验收", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
