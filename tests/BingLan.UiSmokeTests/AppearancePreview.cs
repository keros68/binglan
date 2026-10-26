using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BingLan.App.Themes;
using BingLan.App.Windows;
using BingLan.Core.Models;
using Button = System.Windows.Controls.Button;

/// <summary>Opt-in production picker with in-memory state only; no coordinator or system writes.</summary>
internal static class AppearancePreview
{
    internal static void Run(System.Windows.Application app)
    {
        var window = new AppearancePreviewWindow(new DesktopStyleState()) { ShowInTaskbar = true };
        if (Environment.GetCommandLineArgs().Contains("--appearance-capture"))
        {
            var output = Path.GetFullPath(Path.Combine("artifacts", "appearance-preview", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(output);
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var material = (ComboBox)window.FindName("MaterialSelector");
                    var density = (Slider)window.FindName("DensitySlider");
                    var ink = (ComboBox)window.FindName("TextInkSelector");
                    var background = (ComboBox)window.FindName("PreviewBackgroundSelector");
                    async Task Capture(string name)
                    {
                        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                        var dpi = VisualTreeHelper.GetDpi(window);
                        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                            PixelFormats.Pbgra32);
                        var backgroundVisual = new DrawingVisual();
                        using (var context = backgroundVisual.RenderOpen())
                            context.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                        image.Render(backgroundVisual);
                        image.Render(window);
                        Save(image, Path.Combine(output, name + ".png"));
                    }
                    await Capture("01-wide-light-dark-background");
                    background.SelectedIndex = 1;
                    await Capture("02-light-light-background");
                    material.SelectedIndex = (int)GlassMode.Dark;
                    density.Value = 0.65;
                    await Capture("03-dark");
                    material.SelectedIndex = (int)GlassMode.Clear;
                    background.SelectedIndex = 0;
                    await Capture("04-clear");
                    background.SelectedIndex = 1;
                    ink.SelectedIndex = (int)TextInk.Dark;
                    await Capture("05-clear-dark-text");
                    material.SelectedIndex = (int)GlassMode.Light;
                    window.Draft.CustomTextColor = "#A83F61";
                    ink.SelectedIndex = (int)TextInk.Custom;
                    density.Value = DesktopStyleRules.MinimumGlassDensity;
                    await Capture("06-custom-low-density");
                    ((Button)window.FindName("CompareButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Capture("07-original-comparison");
                    ((Button)window.FindName("CompareButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.Width = 560;
                    window.Height = 460;
                    await Capture("08-small-window");
                    ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(1.5, 1.5);
                    await Capture("09-small-window-scaled-content");
                    ((ScrollViewer)window.FindName("ParameterScroll")).ScrollToEnd();
                    await Capture("10-small-window-scaled-parameters-end");
                    foreach (var preset in Enum.GetValues<DesktopLayoutPreset>())
                    foreach (var (width, height) in new[] { (720d, 1280d), (864d, 1536d), (1080d, 1920d) })
                    {
                        var image = (BitmapSource)ThemePreviewRenderer.RenderPreset(preset,
                            DesktopExperienceRules.CreateDefault(), new Rect(-width, -120, width, height));
                        Save(image, Path.Combine(output, $"portrait-{preset}-{width:0}x{height:0}.png"));
                    }
                    Console.WriteLine($"APPEARANCE_CAPTURE {output}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception);
                    Environment.ExitCode = 1;
                }
                finally { window.Close(); }
            };
        }
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            window.ShowDialog();
            app.Shutdown();
        }));
        app.Run();
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
