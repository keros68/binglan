using Microsoft.Win32;

namespace BingLan.App.Services;

/// <summary>
/// The app's single per-user startup entry under the current user's Run key. No
/// administrator rights, scheduled task or second resident process are involved.
/// </summary>
internal sealed class StartupRegistration(string executablePath, bool canModify)
{
    internal const string ValueName = "BingLan";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private readonly string _command = $"\"{executablePath}\"";

    internal bool CanModify { get; } = canModify;

    internal bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(ValueName) is not string value
            || !string.Equals(value, _command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Task Manager / Windows Settings disable startup entries via StartupApproved\Run
        // (odd low bit such as 0x01/0x03/0x05) without deleting the Run value itself.
        using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath);
        return approvedKey?.GetValue(ValueName) is not byte[] { Length: > 0 } status
            || (status[0] & 1) == 0;
    }

    internal void SetEnabled(bool enabled)
    {
        if (!CanModify)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(ValueName, _command, RegistryValueKind.String);
            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
            approvedKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
