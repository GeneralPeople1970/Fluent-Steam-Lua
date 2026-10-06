namespace SteamAutoCrack.Core.Utils;

// 我移除了 SQLite 全量游戏列表库：DLC 名解析走商店接口直查，不再需要本地建库查库；
// 保留同名空实现只是让其它生成器照常编译，实际调用方只有 DLC 名回退（等价于以前查不到时的行为）。
public class SteamApp
{
    public uint? AppId { get; set; }

    public string? Name { get; set; }

    public override string ToString()
    {
        return $"{AppId}={Name}";
    }
}

public class SteamAppList
{
    public static Task WaitForReady()
    {
        return Task.CompletedTask;
    }

    public static Task<SteamApp> GetAppById(uint appid)
    {
        return Task.FromResult(new SteamApp
        {
            AppId = appid,
            Name = null
        });
    }
}
