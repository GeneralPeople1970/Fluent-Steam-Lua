namespace SteamLuaManager.Models;

public class CdnEndpoint
{
    public string Name { get; init; }
    public string UrlTemplate { get; init; }
    public bool IsImageEndpoint { get; init; } = true;
    /// <summary>接口型源：地址需按 appid 查接口才能拿到（如 Heybox），不参与 URL 模板拼接与直链测速。</summary>
    public bool IsApiLookup { get; init; }

    public CdnEndpoint(string name, string urlTemplate)
    {
        Name = name;
        UrlTemplate = urlTemplate;
    }

    public static List<CdnEndpoint> Defaults { get; } = new()
    {
        new("Store API", "https://store.steampowered.com/api/appdetails?appids={0}&l=schinese&filters=basic")
        { IsImageEndpoint = false },
        new("Akamai 主节点", "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/header.jpg"),
        new("Akamai 备用", "https://cdn.akamai.steamstatic.com/steam/apps/{0}/header.jpg"),
        new("Cloudflare CDN", "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg"),
        new("Akamai 大图", "https://cdn.akamai.steamstatic.com/steam/apps/{0}/library_600x900.jpg"),
        new("Heybox 国内源 (默认)", "heybox://{0}") { IsApiLookup = true },
    };

    public override string ToString() => Name;
}
