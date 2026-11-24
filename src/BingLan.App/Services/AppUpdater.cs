using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App.Services;

/// <summary>
/// Looks for a new release once a day (when allowed) or on request, and installs one only
/// when the user asks. Runs on the UI thread; the network work itself is asynchronous.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    // A failed automatic check is tried again on the next tick, so a network that was not
    // ready at logon does not postpone the check by a whole day.
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly UpdateState _state;
    private readonly UpdateService _service;
    private readonly Action _saveState;
    private readonly Action _exitApp;
    private readonly bool _canAutoCheck;
    private readonly DispatcherTimer _timer = new() { Interval = TickInterval };
    private Version? _notifiedVersion;

    public AppUpdater(
        UpdateState state,
        Version currentVersion,
        Action saveState,
        Action exitApp,
        bool canAutoCheck,
        System.Net.Http.HttpMessageHandler? handler = null)
    {
        _state = state;
        _service = new UpdateService(currentVersion, handler);
        _saveState = saveState;
        _exitApp = exitApp;
        _canAutoCheck = canAutoCheck;
        _timer.Tick += (_, _) => _ = CheckIfDueAsync();
    }

    /// <summary>Raised whenever the status, the found release or the busy state changes.</summary>
    public event Action? Changed;

    /// <summary>Raised once per release when a check first finds it.</summary>
    public event Action<UpdateRelease>? ReleaseFound;

    public Version CurrentVersion => _service.CurrentVersion;
    public UpdateRelease? Available { get; private set; }
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = string.Empty;

    public bool AutoCheck
    {
        get => _state.AutoCheck;
        set
        {
            if (_state.AutoCheck == value)
            {
                return;
            }
            _state.AutoCheck = value;
            _saveState();
            if (value)
            {
                _ = CheckIfDueAsync();
            }
        }
    }

    public void Start()
    {
        if (!_canAutoCheck)
        {
            return;
        }
        _timer.Start();
        _ = CheckIfDueAsync();
    }

    public Task CheckNowAsync() => CheckAsync(manual: true);

    private Task CheckIfDueAsync() =>
        _canAutoCheck && _state.AutoCheck && UpdateService.IsAutoCheckDue(_state.LastCheckedAt, DateTimeOffset.Now)
            ? CheckAsync(manual: false)
            : Task.CompletedTask;

    private async Task CheckAsync(bool manual)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        if (manual)
        {
            Status = "正在检查更新…";
        }
        OnChanged();

        var result = await _service.CheckAsync();
        IsBusy = false;
        if (result.Status == UpdateCheckStatus.Failed)
        {
            // An automatic check fails quietly and is tried again later.
            if (manual)
            {
                Status = $"检查更新失败：{result.ErrorMessage}";
            }
            OnChanged();
            return;
        }

        _state.LastCheckedAt = DateTimeOffset.Now;
        _saveState();
        Available = result.Release;
        var checkedAt = _state.LastCheckedAt.Value.LocalDateTime.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture);
        Status = Available is { } release
            ? $"发现新版本 {release.Version.ToString(3)}"
            : $"已是最新版本（检查于 {checkedAt}）";
        OnChanged();
        if (Available is { } found && found.Version != _notifiedVersion)
        {
            _notifiedVersion = found.Version;
            ReleaseFound?.Invoke(found);
        }
    }

    /// <summary>
    /// Downloads and verifies the installer, starts it and exits the app; the installer
    /// stops any copy still running, puts the taskbar back and opens the new version.
    /// </summary>
    public async Task DownloadAndInstallAsync()
    {
        if (IsBusy || Available?.Installer is not { } installer)
        {
            return;
        }
        IsBusy = true;
        Status = "正在下载新版本…";
        OnChanged();

        var progress = new Progress<double>(fraction =>
        {
            Status = $"正在下载新版本… {fraction * 100:0}%";
            OnChanged();
        });
        string path;
        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout);
            path = await _service.DownloadAsync(
                installer,
                Path.Combine(Path.GetTempPath(), "BingLanUpdate"),
                progress,
                timeout.Token);
        }
        catch (UpdateDownloadException exception)
        {
            IsBusy = false;
            Status = $"下载失败：{exception.Message}";
            OnChanged();
            return;
        }

        try
        {
            var logDirectory = StartupLog.DataDirectory;
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "update-install.log");
            Process.Start(new ProcessStartInfo(
                path,
                $"/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /UPDATE=1 /LOG=\"{logPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            IsBusy = false;
            Status = $"无法启动安装程序：{exception.Message}";
            OnChanged();
            return;
        }

        StartupLog.Write($"开始安装新版本 {Available.Version.ToString(3)}");
        _exitApp();
    }

    public void OpenReleasePage()
    {
        if (Available is not { } release)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(release.PageUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            Status = $"无法打开发布页：{exception.Message}";
            OnChanged();
        }
    }

    public void Dispose() => _timer.Stop();

    private void OnChanged() => Changed?.Invoke();
}
