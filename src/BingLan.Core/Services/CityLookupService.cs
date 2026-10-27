using BingLan.Core.Models;

namespace BingLan.Core.Services;

// 城市查找入口：先查内置离线城市库，无匹配再转向 Nominatim 在线搜索。
// 绝大多数中文地名（省、市、区县）在本地命中，不依赖境外网络可达性；
// 在线路径只服务本地库没有的冷门境外地名。
public sealed class CityLookupService
{
    private readonly CitySearchService _online;
    // The built-in library is parsed on the first search, not when the app starts.
    private readonly Lazy<CityLibrary> _library;

    public CityLookupService()
        : this(new CitySearchService())
    {
    }

    public CityLookupService(CitySearchService online, CityLibrary? library = null)
    {
        _online = online;
        _library = library is null ? new(CityLibrary.LoadDefault) : new(() => library);
    }

    public int LocalEntryCount => _library.Value.Count;

    public async Task<CitySearchOutcome> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var local = _library.Value.Search(query ?? "");
        if (local.Count > 0)
        {
            return new CitySearchOutcome(true, local, $"找到 {local.Count} 个候选地点");
        }
        return await _online.SearchAsync(query, cancellationToken).ConfigureAwait(false);
    }
}
