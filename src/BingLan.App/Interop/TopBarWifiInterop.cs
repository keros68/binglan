using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BingLan.Core.TopBar;

namespace BingLan.App.Interop;

/// <summary>
/// Documented Native Wi-Fi (wlanapi.dll) and NetworkInterface bridge for the TopBar Network
/// flyout: reads active connection info, lists saved/available Wi-Fi networks, connects to
/// existing Windows Wi-Fi profiles and disconnects cleanly without touching credentials.
/// </summary>
internal static class TopBarWifiInterop
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint WlanAvailableNetworkConnected = 0x1;
    private const uint WlanAvailableNetworkHasProfile = 0x2;
    private const int WlanConnectionModeProfile = 0;
    private const int Dot11BssTypeInfrastructure = 1;

    internal static TopBarNetworkSnapshot ReadNetworkSnapshot()
    {
        string? currentSsid = null;
        string? currentProfile = null;
        int? signalQuality = null;
        var interfaceGuid = Guid.Empty;
        var hasWifiInterface = false;
        var wifiConnected = false;
        var accessDenied = false;
        IReadOnlyList<TopBarWifiNetwork> networks = [];

        nint clientHandle = 0;
        nint interfaceListPtr = 0;
        try
        {
            if (TopBarNativeMethods.WlanOpenHandle(
                    TopBarNativeMethods.WlanClientVersionVista,
                    0,
                    out _,
                    out clientHandle) == ErrorSuccess
                && clientHandle != 0
                && TopBarNativeMethods.WlanEnumInterfaces(clientHandle, 0, out interfaceListPtr) == ErrorSuccess
                && interfaceListPtr != 0)
            {
                var count = Marshal.ReadInt32(interfaceListPtr);
                var itemSize = Marshal.SizeOf<TopBarNativeMethods.WlanInterfaceInfo>();
                var firstItemPtr = interfaceListPtr + 8;

                TopBarNativeMethods.WlanInterfaceInfo? selected = null;
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<TopBarNativeMethods.WlanInterfaceInfo>(
                        firstItemPtr + i * itemSize);
                    hasWifiInterface = true;
                    if (selected is null || info.State == TopBarNativeMethods.WlanInterfaceStateConnected)
                    {
                        selected = info;
                        if (info.State == TopBarNativeMethods.WlanInterfaceStateConnected)
                        {
                            break;
                        }
                    }
                }

                if (selected is { } iface)
                {
                    interfaceGuid = iface.InterfaceGuid;
                    if (iface.State == TopBarNativeMethods.WlanInterfaceStateConnected)
                    {
                        wifiConnected = true;
                        (currentSsid, currentProfile, signalQuality) = ReadCurrentConnection(clientHandle, interfaceGuid);
                    }

                    (networks, accessDenied) = ReadAvailableNetworks(clientHandle, interfaceGuid, currentSsid);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
            // Systems without the WLAN AutoConfig service still show Ethernet / offline details.
        }
        finally
        {
            if (interfaceListPtr != 0)
            {
                TopBarNativeMethods.WlanFreeMemory(interfaceListPtr);
            }
            if (clientHandle != 0)
            {
                _ = TopBarNativeMethods.WlanCloseHandle(clientHandle, 0);
            }
        }

        var activeAdapter = FindActiveNetworkAdapter(wifiConnected ? interfaceGuid : Guid.Empty);
        var isConnected = activeAdapter is not null || wifiConnected;
        var isWireless = activeAdapter is not null ? activeAdapter.IsWireless : wifiConnected;
        var linkSpeed = activeAdapter?.LinkSpeed;
        var ipv4 = activeAdapter?.Ipv4Address;
        var adapterName = activeAdapter?.Name;

        var title = !isConnected
            ? "未连接"
            : isWireless
                ? (!string.IsNullOrWhiteSpace(currentSsid) ? currentSsid! : "WLAN")
                : "以太网";

        return new TopBarNetworkSnapshot(
            IsConnected: isConnected,
            IsWireless: isWireless,
            ConnectionTitle: title,
            CurrentSsid: currentSsid,
            CurrentProfileName: currentProfile,
            SignalQuality: signalQuality,
            LinkSpeedBitsPerSecond: linkSpeed,
            Ipv4Address: ipv4,
            AdapterName: adapterName,
            InterfaceGuid: interfaceGuid,
            HasWifiInterface: hasWifiInterface,
            WifiListAccessDenied: accessDenied,
            AvailableNetworks: networks);
    }

    internal static bool ConnectSavedProfile(Guid interfaceGuid, string profileName)
    {
        if (interfaceGuid == Guid.Empty || string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        nint clientHandle = 0;
        try
        {
            if (TopBarNativeMethods.WlanOpenHandle(
                    TopBarNativeMethods.WlanClientVersionVista,
                    0,
                    out _,
                    out clientHandle) != ErrorSuccess
                || clientHandle == 0)
            {
                return false;
            }

            var parameters = new WlanConnectionParameters
            {
                WlanConnectionMode = WlanConnectionModeProfile,
                Profile = profileName,
                Dot11Ssid = 0,
                DesiredBssidList = 0,
                Dot11BssType = Dot11BssTypeInfrastructure,
                Flags = 0
            };
            var guid = interfaceGuid;
            return WlanConnect(clientHandle, ref guid, ref parameters, 0) == ErrorSuccess;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (clientHandle != 0)
            {
                _ = TopBarNativeMethods.WlanCloseHandle(clientHandle, 0);
            }
        }
    }

    internal static bool Disconnect(Guid interfaceGuid)
    {
        if (interfaceGuid == Guid.Empty)
        {
            return false;
        }

        nint clientHandle = 0;
        try
        {
            if (TopBarNativeMethods.WlanOpenHandle(
                    TopBarNativeMethods.WlanClientVersionVista,
                    0,
                    out _,
                    out clientHandle) != ErrorSuccess
                || clientHandle == 0)
            {
                return false;
            }

            var guid = interfaceGuid;
            return WlanDisconnect(clientHandle, ref guid, 0) == ErrorSuccess;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (clientHandle != 0)
            {
                _ = TopBarNativeMethods.WlanCloseHandle(clientHandle, 0);
            }
        }
    }

    internal static bool TriggerScan(Guid interfaceGuid)
    {
        if (interfaceGuid == Guid.Empty)
        {
            return false;
        }

        nint clientHandle = 0;
        try
        {
            if (TopBarNativeMethods.WlanOpenHandle(
                    TopBarNativeMethods.WlanClientVersionVista,
                    0,
                    out _,
                    out clientHandle) != ErrorSuccess
                || clientHandle == 0)
            {
                return false;
            }

            var guid = interfaceGuid;
            return WlanScan(clientHandle, ref guid, 0, 0, 0) == ErrorSuccess;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (clientHandle != 0)
            {
                _ = TopBarNativeMethods.WlanCloseHandle(clientHandle, 0);
            }
        }
    }

    private static (string? Ssid, string? ProfileName, int? SignalQuality) ReadCurrentConnection(
        nint clientHandle,
        Guid interfaceGuid)
    {
        nint connPtr = 0;
        try
        {
            var guid = interfaceGuid;
            if (TopBarNativeMethods.WlanQueryInterface(
                    clientHandle,
                    ref guid,
                    TopBarNativeMethods.WlanIntfOpcodeCurrentConnection,
                    0,
                    out var dataSize,
                    out connPtr,
                    0) != ErrorSuccess
                || connPtr == 0
                || dataSize < Marshal.SizeOf<TopBarNativeMethods.WlanConnectionAttributes>())
            {
                return (null, null, null);
            }

            var conn = Marshal.PtrToStructure<TopBarNativeMethods.WlanConnectionAttributes>(connPtr);
            var assoc = conn.AssociationAttributes;
            var ssidLen = (int)Math.Min(assoc.Dot11Ssid.SsidLength, 32u);
            var ssid = ssidLen > 0 && assoc.Dot11Ssid.Ssid is { Length: >= 32 }
                ? TopBarModuleRules.DecodeSsidBytes(
                    assoc.Dot11Ssid.Ssid.AsSpan(0, ssidLen),
                    TopBarNativeMethods.DecodeSystemAnsi)
                : null;
            var profile = string.IsNullOrWhiteSpace(conn.ProfileName) ? null : conn.ProfileName.Trim();
            var quality = (int)Math.Clamp(assoc.WlanSignalQuality, 0u, 100u);
            return (ssid, profile, quality);
        }
        finally
        {
            if (connPtr != 0)
            {
                TopBarNativeMethods.WlanFreeMemory(connPtr);
            }
        }
    }

    private static (IReadOnlyList<TopBarWifiNetwork> Networks, bool AccessDenied) ReadAvailableNetworks(
        nint clientHandle,
        Guid interfaceGuid,
        string? currentSsid)
    {
        nint listPtr = 0;
        try
        {
            var guid = interfaceGuid;
            var rc = WlanGetAvailableNetworkList(clientHandle, ref guid, 0, 0, out listPtr);
            if (rc == ErrorAccessDenied)
            {
                return ([], true);
            }
            if (rc != ErrorSuccess || listPtr == 0)
            {
                return ([], false);
            }

            var count = Marshal.ReadInt32(listPtr);
            if (count <= 0)
            {
                return ([], false);
            }

            var itemSize = Marshal.SizeOf<WlanAvailableNetwork>();
            var firstItemPtr = listPtr + 8;
            var raw = new List<TopBarWifiNetwork>(count);
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<WlanAvailableNetwork>(firstItemPtr + i * itemSize);
                var ssidLen = (int)Math.Min(item.Dot11Ssid.SsidLength, 32u);
                if (ssidLen <= 0 || item.Dot11Ssid.Ssid is not { Length: >= 32 })
                {
                    continue;
                }
                var ssid = TopBarModuleRules.DecodeSsidBytes(
                    item.Dot11Ssid.Ssid.AsSpan(0, ssidLen),
                    TopBarNativeMethods.DecodeSystemAnsi);
                if (string.IsNullOrWhiteSpace(ssid))
                {
                    continue;
                }
                // Preserve Windows' exact native profile association; never substitute another
                // profile merely because its name happens to match the SSID.
                var profile = string.IsNullOrWhiteSpace(item.ProfileName) ? null : item.ProfileName;
                var isSaved = (item.Flags & WlanAvailableNetworkHasProfile) != 0 && profile is not null;
                var isConnected = (item.Flags & WlanAvailableNetworkConnected) != 0
                    || (!string.IsNullOrWhiteSpace(currentSsid) && string.Equals(currentSsid, ssid, StringComparison.Ordinal));
                raw.Add(new TopBarWifiNetwork(
                    Ssid: ssid,
                    ProfileName: profile,
                    SignalQuality: (int)Math.Clamp(item.WlanSignalQuality, 0u, 100u),
                    IsConnected: isConnected,
                    IsSaved: isSaved,
                    IsSecured: item.SecurityEnabled != 0));
            }

            return (TopBarControlRules.DeduplicateAndSortWifiNetworks(raw), false);
        }
        finally
        {
            if (listPtr != 0)
            {
                TopBarNativeMethods.WlanFreeMemory(listPtr);
            }
        }
    }

    private sealed record ActiveAdapterInfo(bool IsWireless, long? LinkSpeed, string? Ipv4Address, string? Name);

    private static ActiveAdapterInfo? FindActiveNetworkAdapter(Guid connectedWlanGuid)
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            // Prefer a physical Ethernet adapter with a real default gateway over virtual switches
            // (Hyper-V / WSL / VMware / VPN adapters) so an active Wi-Fi link is not mislabeled.
            var physicalWired = candidates.FirstOrDefault(adapter =>
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Wman)
                && !IsLikelyVirtualAdapter(adapter)
                && HasDefaultGateway(adapter));
            NetworkInterface? connectedWlanAdapter = null;
            if (connectedWlanGuid != Guid.Empty)
            {
                connectedWlanAdapter = candidates.FirstOrDefault(adapter =>
                    Guid.TryParse(adapter.Id, out var id) && id == connectedWlanGuid);
            }
            var anyWireless = connectedWlanAdapter
                ?? candidates.FirstOrDefault(adapter =>
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Wman);
            var picked = physicalWired
                ?? anyWireless
                ?? candidates.FirstOrDefault(adapter => !IsLikelyVirtualAdapter(adapter))
                ?? candidates[0];

            var isWireless = picked.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Wman
                || (connectedWlanGuid != Guid.Empty && ReferenceEquals(picked, connectedWlanAdapter));
            string? ipv4 = null;
            try
            {
                ipv4 = picked.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)?
                    .Address.ToString();
            }
            catch (NetworkInformationException)
            {
                // Keep snapshot usable even if IP properties cannot be queried.
            }

            return new ActiveAdapterInfo(
                IsWireless: isWireless,
                LinkSpeed: picked.Speed > 0 ? picked.Speed : null,
                Ipv4Address: ipv4,
                Name: string.IsNullOrWhiteSpace(picked.Description) ? picked.Name : picked.Description);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool HasDefaultGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Count > 0;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static bool IsLikelyVirtualAdapter(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        return text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wintun", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanAvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ProfileName;

        internal TopBarNativeMethods.Dot11Ssid Dot11Ssid;
        internal int Dot11BssType;
        internal uint NumberOfBssids;
        internal int NetworkConnectable;
        internal uint NotConnectableReason;
        internal uint NumberOfPhyTypes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        internal int[] Dot11PhyTypes;

        internal int MorePhyTypes;
        internal uint WlanSignalQuality;
        internal int SecurityEnabled;
        internal int DefaultAuthAlgorithm;
        internal int DefaultCipherAlgorithm;
        internal uint Flags;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        internal int WlanConnectionMode;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string Profile;

        internal nint Dot11Ssid;
        internal nint DesiredBssidList;
        internal int Dot11BssType;
        internal uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(
        nint clientHandle,
        ref Guid interfaceGuid,
        uint flags,
        nint reserved,
        out nint networkList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanConnect(
        nint clientHandle,
        ref Guid interfaceGuid,
        ref WlanConnectionParameters connectionParameters,
        nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(
        nint clientHandle,
        ref Guid interfaceGuid,
        nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(
        nint clientHandle,
        ref Guid interfaceGuid,
        nint dot11Ssid,
        nint ieData,
        nint reserved);
}
