using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App.TopBar;

/// <summary>
/// Owns the top bar window for the app lifetime, mirroring the dock host. The bar is
/// opt-in; a failure while opening or applying it closes the bar (releasing any reserved
/// work area) without affecting the desktop widgets.
/// </summary>
internal sealed class TopBarHost : IDisposable
{
    private readonly TopBarState _state;
    private readonly DesktopStyleState _style;
    private readonly ITopBarEnvironment _environment;
    private readonly IPerformanceSamplingService _sampler;
    private readonly Action<string> _notify;
    private TopBarWindow? _window;

    internal TopBarHost(
        TopBarState state,
        DesktopStyleState style,
        ITopBarEnvironment environment,
        IPerformanceSamplingService sampler,
        Action<string> notify)
    {
        _state = state;
        _style = style;
        _environment = environment;
        _sampler = sampler;
        _notify = notify;
    }

    internal void Apply()
    {
        if (!_state.IsEnabled)
        {
            Close();
            return;
        }

        try
        {
            if (_window is null)
            {
                Open();
            }
            else
            {
                _window.ApplyState();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"顶端信息条打开失败：{exception}");
            Close();
            _notify("顶端信息条未能打开，已恢复屏幕工作区。");
        }
    }

    public void Dispose() => Close();

    private void Open()
    {
        var window = new TopBarWindow(_state, _style, _environment, _sampler);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        _window = window;
        window.Show();
    }

    private void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
