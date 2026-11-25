using System.Text.Json;
using System.Xml.Linq;

internal static class MemoryConfigTests
{
    internal static void TestGcConserveMemoryRuntimeConfig()
    {
        var repoRoot = FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "src", "BingLan.App", "BingLan.App.csproj");
        if (!File.Exists(csprojPath))
        {
            throw new InvalidOperationException($"未找到应用项目文件：{csprojPath}");
        }

        var doc = XDocument.Load(csprojPath);
        if (doc.Descendants("GCConserveMemory").Any())
        {
            throw new InvalidOperationException(
                "BingLan.App.csproj 不应使用无效的 <GCConserveMemory> 属性，应使用 RuntimeHostConfigurationOption 写入 runtimeconfig.json");
        }

        var option = doc.Descendants("RuntimeHostConfigurationOption")
            .FirstOrDefault(element => string.Equals(
                (string?)element.Attribute("Include"),
                "System.GC.ConserveMemory",
                StringComparison.Ordinal));
        var configuredValue = (string?)option?.Attribute("Value");
        if (!int.TryParse(configuredValue, out var csprojLevel) || csprojLevel is < 1 or > 9)
        {
            throw new InvalidOperationException(
                $"BingLan.App.csproj 必须通过 RuntimeHostConfigurationOption 配置有效 System.GC.ConserveMemory（1-9），实际：{configuredValue ?? "<missing>"}");
        }

        var concurrentGc = (string?)doc.Descendants("ConcurrentGarbageCollection").FirstOrDefault()?.Value;
        if (string.Equals(concurrentGc, "false", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "BingLan.App.csproj 应保持 <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection> 以避免 Gen2 阻塞交互帧");
        }

        // Verify the emitted BingLan.runtimeconfig.json actually carries System.GC.ConserveMemory
        // and does not disable concurrent GC.
        var runtimeConfigPath = FindAppRuntimeConfigPath(repoRoot);
        if (runtimeConfigPath is not null)
        {
            using var stream = File.OpenRead(runtimeConfigPath);
            using var json = JsonDocument.Parse(stream);
            if (!json.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
                || !runtimeOptions.TryGetProperty("configProperties", out var configProperties)
                || !configProperties.TryGetProperty("System.GC.ConserveMemory", out var actualElement))
            {
                throw new InvalidOperationException(
                    $"生成的 runtimeconfig.json 缺少 System.GC.ConserveMemory：{runtimeConfigPath}");
            }

            if (configProperties.TryGetProperty("System.GC.Concurrent", out var concurrentElement)
                && concurrentElement.ValueKind == JsonValueKind.False)
            {
                throw new InvalidOperationException(
                    $"BingLan.runtimeconfig.json 不应将 System.GC.Concurrent 设为 false：{runtimeConfigPath}");
            }

            var actualLevel = actualElement.ValueKind switch
            {
                JsonValueKind.Number => actualElement.GetInt32(),
                JsonValueKind.String when int.TryParse(actualElement.GetString(), out var parsed) => parsed,
                _ => -1
            };
            if (actualLevel != csprojLevel)
            {
                throw new InvalidOperationException(
                    $"BingLan.runtimeconfig.json 的 System.GC.ConserveMemory 应为 {csprojLevel}，实际 {actualElement}");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BingLan.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException($"无法从 {AppContext.BaseDirectory} 向上定位仓库根目录");
    }

    private static string? FindAppRuntimeConfigPath(string repoRoot)
    {
        var isRelease = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        var preferredConfig = isRelease ? "Release" : "Debug";
        var fallbackConfig = isRelease ? "Debug" : "Release";

        foreach (var config in new[] { preferredConfig, fallbackConfig })
        {
            var candidate = Path.Combine(
                repoRoot, "src", "BingLan.App", "bin", config, "net10.0-windows", "BingLan.runtimeconfig.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
