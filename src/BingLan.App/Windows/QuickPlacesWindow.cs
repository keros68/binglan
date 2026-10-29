using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Models;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;

namespace BingLan.App.Windows;

/// <summary>
/// A slim row of line icons the user chooses: places such as this PC, Downloads or the
/// Recycle Bin, any folder, program or file. A click opens it; nothing on disk changes.
/// </summary>
public sealed class QuickPlacesWindow : WidgetWindowBase
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>The frosted bar kept behind the icons when cards have no backing.</summary>
    internal const double ClearBarOpacity = 0.18d;

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };

    public QuickPlacesWindow()
    {
        Title = "快捷入口";
        MinWidth = 180d;
        MinHeight = 44d;
        var body = new Grid { ClipToBounds = true };
        body.SetBinding(Panel.BackgroundProperty, new Binding(nameof(WidgetBackgroundBrush)) { Source = this });
        // The icons scale with the card, so resizing it makes them larger or smaller.
        body.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(10, 6, 10, 6),
            Child = _row
        });
        Content = body;
        SetItems(QuickPlaceRules.CreateDefaults());
    }

    private double _fontScale = 1d;

    /// <summary>Shows these icons, in order.</summary>
    internal void SetItems(IReadOnlyList<QuickPlaceState> items)
    {
        _row.Children.Clear();
        foreach (var item in items)
        {
            _row.Children.Add(BuildButton(QuickPlaceRules.GetIcon(item.Icon).Glyph, item.Name, item.Target));
        }
    }

    public DesktopComponentKind ComponentKind => DesktopComponentKind.QuickLaunch;

    /// <summary>Takes the component's colours, corner and icon size.</summary>
    internal void ApplyComponent(DesktopComponentState component)
    {
        SetWidgetBackgroundColor(component.Appearance.BackgroundColor);
        SetWidgetBackgroundOpacity(component.Appearance.BackgroundOpacity);
        SetWidgetTextColor(component.Appearance.TextColor);
        WidgetCornerRadius = component.CornerRadius;
        _fontScale = component.FontScale;
        foreach (var button in _row.Children.OfType<Button>())
        {
            button.FontSize = IconSize * _fontScale;
        }
    }

    private const double IconSize = 22d;

    private Button BuildButton(string glyph, string name, string target)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = new System.Windows.Media.FontFamily(IconFont),
            FontSize = IconSize * _fontScale,
            MinWidth = 46d,
            MinHeight = 40d,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = name,
            Tag = target,
            // A faint shadow keeps the thin strokes readable on light wallpaper.
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.35,
                Color = Colors.Black
            }
        };
        button.SetBinding(ForegroundProperty, new Binding(nameof(WidgetTextBrush)) { Source = this });
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Open(target);
        return button;
    }

    /// <summary>
    /// Opens a target: "known:" folders are looked up now; folders, "shell:" places,
    /// programs and files open with whatever Windows uses for them, so a replacement
    /// file manager opens folders directly. Starts off the UI thread; opening can fail
    /// (a folder that no longer exists) and the strip stays as it is.
    /// </summary>
    internal static async void Open(string target)
    {
        var place = Resolve(target);
        if (string.IsNullOrEmpty(place))
        {
            return;
        }
        if (await ShellLauncher.StartAsync(new ProcessStartInfo(place) { UseShellExecute = true }) is { } failure)
        {
            Debug.WriteLine($"无法打开 {place}：{failure.Message}");
        }
    }

    internal static string? Resolve(string target)
    {
        if (!target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Environment.ExpandEnvironmentVariables(target);
        }
        return target[QuickPlaceRules.KnownPrefix.Length..].ToLowerInvariant() switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" => KnownFolders.GetDownloadsPath(),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null
        };
    }
}
