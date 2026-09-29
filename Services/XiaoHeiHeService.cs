using System.Net.Http;
using System.Text.Json;

namespace SteamLuaManager.Services;

// 小黑盒国内搜索源：入库前置的名字搜索与 appid 精确补位优先走这里，
// 搜不到再降级到现有国外链路。未公开接口，解析按缺字段即跳过处理。
// 来源标注只写 app.log（LogService），不进窗口日志。
public sealed record HeiHeGame(int AppId, string Name, string CoverUrl, List<string> CoverCandidates);

public static class XiaoHeiHeService
{
    private const string SearchUrl = "https://api.xiaoheihe.cn/game/search/?q={0}&limit=10&offset=0";
    private const string DetailUrl = "https://api.xiaoheihe.cn/game/web/get_game_detail/?appid={0}";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // 名字搜索：只收 Steam 平台有效条目，按名含关键词/type/关注数加权排序，取前 10；
    // 关键词归一化（去标点）后匹配，无一匹配判 miss 走替补链，避免乱码返回一堆无关结果
    public static async Task<List<HeiHeGame>> SearchByNameAsync(string keyword, CancellationToken ct = default)
    {
        var result = new List<HeiHeGame>();
        var normKeyword = Norm(keyword);
        if (string.IsNullOrEmpty(normKeyword))
            return result;
        try
        {
            using var resp = await _http.GetAsync(
                string.Format(SearchUrl, Uri.EscapeDataString(keyword)), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn("入库", $"小黑盒搜索失败（HTTP {(int)resp.StatusCode}）");
                return result;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var r) ||
                !r.TryGetProperty("games", out var games) ||
                games.ValueKind != JsonValueKind.Array)
            {
                LogService.Info("入库", "小黑盒搜索无有效结果，转备用源");
                return result;
            }
            var scored = new List<(HeiHeGame Game, bool NameHit, bool IsGame, int Follow)>();
            foreach (var g in games.EnumerateArray())
            {
                if (!TryGetSteamAppId(g, out var appId)) continue;
                var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var type = g.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                // 封面候选：Steam 直链优先，heybox 备用（未发售游戏 Steam 侧可能无图，如 4209920）；
                // 竖图裁切难看但聊胜于无，老模板垫底（新游戏已 404）
                var head = g.TryGetProperty("head_image_616_353", out var h) ? h.GetString() ?? "" : "";
                var img = g.TryGetProperty("image", out var im) ? im.GetString() ?? "" : "";
                var vertical = g.TryGetProperty("vertical_head_image", out var v) ? v.GetString() ?? "" : "";
                var covers = new List<string>();
                foreach (var u in new[] { head, img, vertical,
                             $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg" })
                {
                    if (!string.IsNullOrEmpty(u) && !covers.Contains(u))
                        covers.Add(u);
                }
                var follow = g.TryGetProperty("follow_num", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var fn) ? fn : 0;
                scored.Add((new HeiHeGame(appId, name, covers[0], covers),
                    Norm(name).Contains(normKeyword, StringComparison.Ordinal),
                    type == "game", follow));
            }
            var ordered = scored
                .OrderByDescending(x => x.NameHit)
                .ThenByDescending(x => x.IsGame)
                .ThenByDescending(x => x.Follow)
                .ToList();
            // 门控：名中至少一个，或头名是高关注游戏（英文搜中文名场景，如 ass 搜出刺客信条）；
            // 否则判 miss 走替补，避免乱码展示一堆无关结果
            var top = ordered.FirstOrDefault();
            if (!ordered.Any(x => x.NameHit) && (top.Game == null || !top.IsGame || top.Follow < 10000))
            {
                LogService.Info("入库", $"小黑盒搜索无有效结果，转备用源：{keyword}");
                return result;
            }
            result = ordered.Take(10).Select(x => x.Game).ToList();
            LogService.Info("入库", $"小黑盒搜索命中 {result.Count} 个：{keyword}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn("入库", $"小黑盒搜索异常，转备用源：{ex.Message}");
        }
        return result;
    }

    // appid 精确补位：只补中文名与封面；result 为空对象判 miss
    public static async Task<(string? Name, string? CoverUrl)> GetGameDetailAsync(int appId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(string.Format(DetailUrl, appId), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn("入库", $"小黑盒详情失败 AppID {appId}（HTTP {(int)resp.StatusCode}）");
                return (null, null);
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var r) ||
                r.ValueKind != JsonValueKind.Object ||
                !r.TryGetProperty("name", out var n))
            {
                LogService.Info("入库", $"小黑盒详情无数据 AppID {appId}，转备用源");
                return (null, null);
            }
            var name = n.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                LogService.Info("入库", $"小黑盒详情无数据 AppID {appId}，转备用源");
                return (null, null);
            }
            var cover = r.TryGetProperty("image", out var img) ? img.GetString() : null;
            LogService.Info("入库", $"小黑盒详情补到名称 AppID {appId}：{name}");
            return (name, string.IsNullOrWhiteSpace(cover) ? null : cover);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn("入库", $"小黑盒详情异常 AppID {appId}，转备用源：{ex.Message}");
            return (null, null);
        }
    }

    // 归一化：去标点空白后比对，中英文名含冒号空格等不影响命中
    private static string Norm(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Steam 有效条目：platforms 含 steam，且 steam_appid 与 appid 一致（过滤 epic/xbox/手游的假 id）
    private static bool TryGetSteamAppId(JsonElement g, out int appId)
    {
        appId = 0;
        if (!g.TryGetProperty("steam_appid", out var sid) || sid.ValueKind != JsonValueKind.Number || !sid.TryGetInt32(out var steamId) || steamId <= 0)
            return false;
        if (!g.TryGetProperty("appid", out var aid) || aid.ValueKind != JsonValueKind.Number || !aid.TryGetInt32(out var id) || id != steamId)
            return false;
        if (!g.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var p in platforms.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.String &&
                string.Equals(p.GetString(), "steam", StringComparison.OrdinalIgnoreCase))
            {
                appId = steamId;
                return true;
            }
        }
        return false;
    }
}
