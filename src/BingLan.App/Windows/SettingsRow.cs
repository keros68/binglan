using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BingLan.App.Windows;

/// <summary>
/// One line of the settings window: a title and a short description on the left, and the
/// control that changes the setting on the right. A control without its own screen
/// reader name is named after the row title. The settings search indexes rows by title.
/// </summary>
public sealed class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    static SettingsRow()
    {
        FocusableProperty.OverrideMetadata(typeof(SettingsRow), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsRow), new FrameworkPropertyMetadata(false));
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is FrameworkElement element
            && string.IsNullOrEmpty(AutomationProperties.GetName(element))
            && Header.Length > 0)
        {
            AutomationProperties.SetName(element, Header);
        }
    }
}

/// <summary>
/// The left edge of a row's padding as a margin, so the line above a row starts where
/// its title starts, for top-level and indented rows alike.
/// </summary>
public sealed class SettingsRowInsetConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is Thickness padding ? new Thickness(padding.Left, 0, 0, 0) : new Thickness();

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Several settings rows in one rounded block, separated by thin lines. The template
/// clips the line above the first row, so each row can always draw the line above itself.
/// </summary>
public sealed class SettingsGroup : ItemsControl
{
    static SettingsGroup()
    {
        FocusableProperty.OverrideMetadata(typeof(SettingsGroup), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsGroup), new FrameworkPropertyMetadata(false));
    }
}
