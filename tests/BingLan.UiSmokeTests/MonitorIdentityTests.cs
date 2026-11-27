using BingLan.App.Dock;
using BingLan.Core.Dock;

internal static class MonitorIdentityTests
{
    internal static void Run()
    {
        const string pathA = @"\\?\DISPLAY#DELA07A#INSTANCE-A#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string pathB = @"\\?\DISPLAY#DELA07A#INSTANCE-B#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string pathC = @"\\?\DISPLAY#DELA07A#INSTANCE-C#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        var hardware = new Dictionary<string, MonitorHardwareInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new(pathA, "Same model", 1),
            ["B"] = new(pathB, "Same model", 2),
            ["C"] = new(pathC, "Same model", 3)
        };
        var before = MonitorIdentityResolver.BuildResolvedMonitors(
            [Snapshot("A", true, 0), Snapshot("B", false, -1920), Snapshot("C", false, 1920)], hardware);
        var after = MonitorIdentityResolver.BuildResolvedMonitors(
            [Snapshot("A", false, 1920), Snapshot("B", true, 0), Snapshot("C", false, -1920)], hardware);
        Check(before.Select(m => m.MonitorKey).Distinct().Count() == 3, "Three physical displays need unique identities.");
        foreach (var display in before)
        {
            var moved = after.Single(m => m.DeviceName == display.DeviceName);
            Check(display.IsPersistentIdentity && moved.MonitorKey == display.MonitorKey,
                "Changing primary role and negative geometry must preserve each physical identity.");
        }
        Check(before.Single(m => m.IsPrimary).MonitorKey != "primary", "The Windows primary needs a physical key.");
        Check(before.Single(m => m.IsPrimary).ScreenKey is null, "Legacy primary ScreenKey remains null.");
        Check(before.Single(m => m.DeviceName == "A").LegacyDeviceKey == "DISPLAY#DELA07A#INSTANCE-A",
            "Exact path migration alias must exist for the primary too.");

        var replugged = MonitorIdentityResolver.BuildResolvedMonitors([Snapshot("NEW", true, 0)],
            new Dictionary<string, MonitorHardwareInfo> { ["NEW"] = new(pathC, "Changed name", 50) });
        Check(replugged[0].MonitorKey != before.Single(m => m.DeviceName == "A").MonitorKey,
            "A different exact device instance must not inherit a same-model profile.");

        var sole = MonitorIdentityResolver.BuildResolvedMonitors([Snapshot("A", true, 0)], hardware);
        Check(sole[0].MonitorKey == before.Single(m => m.DeviceName == "A").MonitorKey,
            "Removing other monitors must not change the remaining exact device identity.");

        var duplicatePath = new Dictionary<string, MonitorHardwareInfo>
        {
            ["A"] = new(pathA, "Same model", 1),
            ["B"] = new(pathA, "Same model", 2)
        };
        var duplicated = MonitorIdentityResolver.BuildResolvedMonitors(
            [Snapshot("A", true, 0), Snapshot("B", false, 1920)], duplicatePath);
        Check(duplicated.All(m => !m.IsPersistentIdentity) && duplicated[0].MonitorKey != duplicated[1].MonitorKey,
            "A duplicated exact path must not claim reliable identity, including primary collisions.");

        var ambiguous = MonitorIdentityResolver.BuildResolvedMonitors([Snapshot("A", true, 0)],
            new Dictionary<string, MonitorHardwareInfo> { ["A"] = hardware["A"] with { IsAmbiguous = true } });
        var virtualDisplay = MonitorIdentityResolver.BuildResolvedMonitors([Snapshot("A", true, 0)],
            new Dictionary<string, MonitorHardwareInfo> { ["A"] = hardware["A"] with { IsVirtual = true } });
        var modelOnly = MonitorIdentityResolver.BuildResolvedMonitors([Snapshot("A", true, 0)],
            new Dictionary<string, MonitorHardwareInfo> { ["A"] = new("DISPLAY#DELA07A", "Same model") });
        Check(!ambiguous[0].IsPersistentIdentity && !virtualDisplay[0].IsPersistentIdentity
            && !modelOnly[0].IsPersistentIdentity, "Mirrored, virtual, and model-only sources must remain unreliable.");

        Check(MonitorIdentityResolver.FindByPoint(before, -1800, 10)?.DeviceName == "B",
            "Identity must not disturb negative-coordinate hit testing.");
    }

    private static MonitorSnapshot Snapshot(string device, bool primary, int left) =>
        new(0, device, new PixelRect(left, 0, left + 1920, 1080), new PixelRect(left, 0, left + 1920, 1040), primary, 96);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
