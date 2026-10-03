using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ScriptDownloadViewModel : ObservableObject, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamDepotService _depotService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamApiService _steamApiService;
    private readonly IDialogService _dialogService;
    private string _currentDownloadMode = "DepotKey";
    private bool _disposed;
    private CancellationTokenSource? _downloadCts;

    // 商店搜索的地区/语言组合（按优先级）：schinese 索引含中文本地化名称，english 兜底英文/外区
    private static readonly (string Cc, string Lang)[] StoreSearchLocales =
    {
        ("cn", "schinese"),
        ("us", "english")
    };

    [ObservableProperty]
    private string _gameId = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isSearchEmptyVisible;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _includeDlc = true;

    [ObservableProperty]
    private bool _pinManifest;

    [ObservableProperty]
    private bool _fetchNameComments;

    public bool IsLocalCacheMode => _currentDownloadMode == "DepotKey";
    public string CurrentDataSourceLabel => _currentDownloadMode switch
    {
        "DepotKey" => "本地缓存仓库",
        _ => "远程清单仓库"
    };

    /// <summary>上次更新本地缓存的时间；仅本地缓存仓库模式显示，远程仓库为空。</summary>
    public string LastUpdateTimeText
    {
        get
        {
            if (!IsLocalCacheMode) return "";
            var time = _depotService.GetLastUpdateTime(_currentDownloadMode);
            return time == null ? " · 缓存尚未更新" : $" · 上次更新 {time.Value:MM-dd HH:mm}";
        }
    }

    public ObservableCollection<FoundGame> SearchResults { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public ScriptDownloadViewModel(ISteamPathService steamPathService, ISteamDepotService depotService, ISettingsService settingsService, IHttpClientProvider httpClientProvider, ISteamApiService steamApiService, IDialogService dialogService)
    {
        _steamPathService = steamPathService;
        _depotService = depotService;
        _settingsService = settingsService;
        _httpClientProvider = httpClientProvider;
        _steamApiService = steamApiService;
        _dialogService = dialogService;
        _currentDownloadMode = _settingsService.Load().DownloadMode;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _depotService.AllSourcesUpdated += OnAllSourcesUpdated;
        SearchResults.CollectionChanged += (_, _) => RefreshSearchEmptyVisible();
        OnPropertyChanged(nameof(LastUpdateTimeText));
    }

    // 搜过但无结果才显示空提示；切页清空由 MainWindow 调 ResetSearchState 复位
    public void ResetSearchState()
    {
        HasSearched = false;
        RefreshSearchEmptyVisible();
    }

    private void RefreshSearchEmptyVisible()
        => IsSearchEmptyVisible = HasSearched && SearchResults.Count == 0 && !IsSearching;

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_currentDownloadMode == settings.DownloadMode) return;
        _currentDownloadMode = settings.DownloadMode;
        OnPropertyChanged(nameof(IsLocalCacheMode));
        OnPropertyChanged(nameof(CurrentDataSourceLabel));
        OnPropertyChanged(nameof(LastUpdateTimeText));
    }

    private void OnAllSourcesUpdated()
    {
        Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(LastUpdateTimeText)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _depotService.AllSourcesUpdated -= OnAllSourcesUpdated;
        try { _downloadCts?.Cancel(); } catch { }
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    // 切页离开时由 MainWindow 调用：在飞的入库任务取消，UI 已清空不再有幽灵进度
    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    public record FoundGame(int AppId, string Name, string CoverUrl, List<string>? CoverCandidates = null, string ReleaseDate = "")
    {
        // 副标题：有发售日期拼后面，无则只显示 AppID
        public string DisplaySubtitle => string.IsNullOrEmpty(ReleaseDate)
            ? $"AppID: {AppId}"
            : $"AppID: {AppId} · 发售：{ReleaseDate}";
    }

    // 封面候选去重：首选失败时 UI 按序切换；老模板垫底（新游戏已 404，失败即停）
    private static List<string> CoverCandidates(int appId, params string?[] urls)
    {
        var list = new List<string>();
        foreach (var u in urls)
        {
            if (!string.IsNullOrWhiteSpace(u) && !list.Contains(u))
                list.Add(u);
        }
        var fallback = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
        if (!list.Contains(fallback))
            list.Add(fallback);
        return list;
    }

    private static void ConfigureSteamStoreHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;

        var query = GameId?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            StatusMessage = "请输入游戏ID或名称";
            return;
        }

        IsSearching = true;
        SearchResults.Clear();
        LogLines.Clear();
        AddLog($"搜索：{query}");

        // 回退链（中→英→Spy→社区）走完要几秒，总限时从 7 秒放宽到 15 秒
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            // 全链 AppId 都是 int，超 int.MaxValue 的输入无法入库，直接明示而非当游戏名搜
            if (uint.TryParse(query, out var bigId) && bigId > int.MaxValue)
            {
                AddLog($"AppID {query} 超出支持范围（最大 {int.MaxValue}）");
                StatusMessage = $"AppID 超出支持范围（最大 {int.MaxValue}）";
                return;
            }
            if (int.TryParse(query, out var appId) && appId <= 0)
            {
                AddLog($"AppID 无效：{query}（必须为正整数）");
                StatusMessage = "AppID 必须为正整数";
                return;
            }
            if (appId > 0)
            {
                // 小黑盒国内源优先（正常 0.3 秒返回）；miss 才进 appdetails，避免 Store 超时挡路
                var (name, headerImage, releaseDate) = await XiaoHeiHeService.GetGameDetailAsync(appId, cts.Token);
                if (name == null)
                    (name, headerImage, releaseDate) = await GetAppNameAsync(appId, cts.Token);
                // 商店下架的游戏 appdetails 无数据，用备用源再捞一次名字
                if (name == null)
                {
                    name = await _steamApiService.GetFallbackGameNameAsync(appId, cts.Token);
                    if (name != null)
                        AddLog($"商店无数据，备用源解析到名称：{name}");
                }
                if (name != null)
                {
                    // header_image 为哈希 CDN 完整 URL；缺失时回退老模板（对早期游戏仍有效）
                    var coverUrl = !string.IsNullOrWhiteSpace(headerImage)
                        ? headerImage
                        : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                    SearchResults.Add(new FoundGame(appId, name, coverUrl,
                        CoverCandidates(appId, coverUrl), releaseDate));
                    AddLog($"找到：{name} (ID: {appId})");
                    StatusMessage = $"找到：{name}";
                }
                else
                {
                    // 所有名源都查不到（如已下架且社区页也被清）：名字只是展示用，
                    // 仓库密钥走独立接口，用 AppID 占位放行，不挡入库
                    AddLog($"未查到 AppID {appId} 的名称（可能已下架），以 AppID 占位继续，入库不受影响");
                    SearchResults.Add(new FoundGame(
                        appId,
                        $"AppID: {appId}",
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                        CoverCandidates(appId)));
                    StatusMessage = "未查到名称，已用 AppID 占位";
                }
            }
            else
            {
                await SearchByNameAsync(query, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("搜索超时（15秒），请检查网络连接");
            AddLog("建议：尝试开启VPN或代理后重试");
            StatusMessage = "搜索超时，请检查网络";
        }
        catch (Exception ex)
        {
            AddLog($"搜索异常：{ex.Message}");
            StatusMessage = $"搜索异常：{ex.Message}";
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
            RefreshSearchEmptyVisible();
        }
    }

    private async Task<(string? Name, string? HeaderImage, string ReleaseDate)> GetAppNameAsync(int appId, CancellationToken ct = default)
    {
        // 中文优先、查不到转英文；Steam 会对合服/改 ID 的游戏返回重定向后的 AppID 做 key，
        // 精确 key 命中失败时取首个 success 项（如 3669870 返回的 key 是 4760190）
        foreach (var lang in new[] { "schinese", "english" })
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}";
                var json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "script-steam-store",
                    TimeSpan.FromSeconds(10),
                    client => client.GetStringAsync(url, ct),
                    ConfigureSteamStoreHeaders);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // 精确 key 优先；精确 key 缺席时才采纳其他 success 项（纯重定向场景），
                    // 避免精确 key 存在但无数据时串到别的游戏
                    var candidates = new List<JsonElement>();
                    if (doc.RootElement.TryGetProperty(appId.ToString(), out var exact))
                    {
                        candidates.Add(exact);
                    }
                    else
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Object &&
                                prop.Value.TryGetProperty("success", out var s) && s.GetBoolean())
                                candidates.Add(prop.Value);
                        }
                    }
                    foreach (var root in candidates)
                    {
                        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("name", out var name))
                            continue;
                        var headerImage = data.TryGetProperty("header_image", out var img)
                            ? img.GetString()
                            : null;
                        var releaseDate = data.TryGetProperty("release_date", out var rd) &&
                            rd.ValueKind == JsonValueKind.Object && rd.TryGetProperty("date", out var dt) &&
                            dt.ValueKind == JsonValueKind.String
                            ? dt.GetString() ?? ""
                            : "";
                        var gameName = name.GetString();
                        if (!string.IsNullOrWhiteSpace(gameName))
                            return (gameName, headerImage, releaseDate.Trim());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Warn("入库", $"GetAppNameAsync 失败 AppID {appId} ({lang}): {ex.Message}");
            }
        }
        return (null, null, "");
    }

    private async Task SearchByNameAsync(string name, CancellationToken ct)
    {
        // 国内源优先：小黑盒中英文直搜；有效结果直接返回，无结果/异常静默走现有链路
        var heiHe = await XiaoHeiHeService.SearchByNameAsync(name, ct);
        if (heiHe.Count > 0)
        {
            var heiCount = Math.Min(heiHe.Count, 10);
            for (var i = 0; i < heiCount; i++)
                SearchResults.Add(new FoundGame(heiHe[i].AppId, heiHe[i].Name, heiHe[i].CoverUrl, heiHe[i].CoverCandidates, heiHe[i].ReleaseDate));
            AddLog($"找到 {heiCount} 个匹配结果");
            StatusMessage = $"找到 {heiCount} 个匹配结果";
            return;
        }

        // 多组地区/语言降级重试：cc=cn&l=schinese 的索引含中文本地化名称（中文搜索必需），
        // cc=us&l=english 兜底英文/外区匹配；命中即停
        foreach (var (cc, lang) in StoreSearchLocales)
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&cc={cc}&l={lang}";
            string json;
            try
            {
                json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "script-steam-store",
                    TimeSpan.FromSeconds(10),
                    client => client.GetStringAsync(url, ct),
                    ConfigureSteamStoreHeaders);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单组请求失败则尝试下一组参数
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");

            if (items.GetArrayLength() == 0)
                continue;

            int count = Math.Min(items.GetArrayLength(), 10);
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                var appId = item.GetProperty("id").GetInt32();
                var gameName = item.GetProperty("name").GetString() ?? name;

                string coverUrl;
                // tiny_image 为哈希 CDN 完整 URL（storesearch 实际返回的封面字段，新游戏必需）
                if (item.TryGetProperty("tiny_image", out var tinyImg) && !string.IsNullOrEmpty(tinyImg.GetString()))
                {
                    coverUrl = tinyImg.GetString()!;
                }
                else if (item.TryGetProperty("large_image", out var largeImg) && !string.IsNullOrEmpty(largeImg.GetString()))
                {
                    coverUrl = largeImg.GetString()!;
                }
                else if (item.TryGetProperty("small_image", out var smallImg) && !string.IsNullOrEmpty(smallImg.GetString()))
                {
                    coverUrl = smallImg.GetString()!;
                }
                else
                {
                    coverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                }

                SearchResults.Add(new FoundGame(appId, gameName, coverUrl,
                    CoverCandidates(appId, coverUrl)));
            }

            AddLog($"找到 {count} 个匹配结果");
            StatusMessage = $"找到 {count} 个匹配结果";
            return;
        }

        AddLog("未找到匹配的游戏");
        StatusMessage = "未找到匹配的游戏";
    }

    [RelayCommand]
    private async Task DownloadGameAsync(FoundGame game)
    {
        if (game == null || IsDownloading) return;
        await ExecuteDownloadAsync(game.AppId.ToString());
    }

    private Task<bool> ShowModernConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
        => _dialogService.ShowConfirmAsync(title, message, primaryText, closeText);

    private async Task ExecuteDownloadAsync(string gameId)
    {
        // 新下载先取消旧的（IsDownloading 门控下旧任务理论上已结束，这里防幽灵任务）
        CancelDownload();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var ct = cts.Token;
        IsDownloading = true;
        LogLines.Clear();
        AddLog($"开始处理 ID：{gameId}");
        AddLog($"当前入库接口：{CurrentDataSourceLabel}");
        AddLog("=".PadRight(50, '='));

        try
        {
            if (uint.TryParse(gameId, out var bigId) && bigId > int.MaxValue)
            {
                AddLog($"游戏 ID {gameId} 超出支持范围（最大 {int.MaxValue}）");
                StatusMessage = $"游戏 ID 超出支持范围（最大 {int.MaxValue}）";
                return;
            }
            if (!int.TryParse(gameId, out int appId) || appId <= 0)
            {
                AddLog("无效的游戏 ID（必须为正整数）");
                StatusMessage = "无效的游戏 ID（必须为正整数）";
                return;
            }

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder))
            {
                AddLog("未配置 Steam 路径，请先在基本设置中设置路径");
                StatusMessage = "未配置 Steam 路径";
                return;
            }

            if (!Directory.Exists(luaFolder))
            {
                Directory.CreateDirectory(luaFolder);
                AddLog($"已创建目录：{luaFolder}");
            }
            else
            {
                AddLog($"目标目录：{luaFolder}");
            }

            if (IsLocalCacheMode)
            {
                // 本地生成目标固定：已存在则先确认，避免耗时查询后直接覆盖旧档
                var targetLua = Path.Combine(luaFolder, $"{appId}.lua");
                if (File.Exists(targetLua))
                {
                    var overwrite = await ShowModernConfirmAsync(
                        "覆盖确认",
                        $"已存在 {appId}.lua，继续会覆盖旧文件，确定重新入库吗？",
                        "覆盖");
                    if (!overwrite)
                    {
                        AddLog("已取消入库（保留现有 Lua 文件）");
                        StatusMessage = "已取消入库";
                        return;
                    }
                }
                await ExecuteDepotKeyDownloadAsync(appId, ct);
            }
            else
            {
                await ExecuteRemoteDownloadAsync(gameId, luaFolder, ct);
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("入库已取消");
            StatusMessage = "已取消入库";
        }
        catch (Exception ex)
        {
            AddLog($"任务异常：{ex.Message}");
            StatusMessage = $"异常：{ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    // 主仓库为空时的成因区分提示：未发售 / 暂无数据 / 查询失败（查询失败返回 null）
    private async Task<string> GetEmptyDepotsReasonAsync(int appId)
    {
        return await _steamApiService.IsComingSoonAsync(appId) switch
        {
            true => "该游戏尚未发售，暂无法生成入库文件",
            false => "未能获取该游戏的仓库信息（可能为新上架游戏），请稍后重试",
            _ => "该游戏仓库信息查询失败，请检查网络后重试"
        };
    }

    private async Task ExecuteDepotKeyDownloadAsync(int appId, CancellationToken ct)
    {
        _depotService.UseDataSource(_currentDownloadMode);

        AddLog("查询游戏仓库信息...");
        DepotQueryResult? queryResult = null;
        try
        {
            queryResult = await _depotService.QueryAppAsync(appId, ct);
        }
        catch (Exception ex)
        {
            // 内部默认已 30s/3 次，外层只补 1 次并明示，避免弱网一次抖动就判死
            AddLog($"首次查询异常，正在重试（最后 1 次）：{ex.InnerException?.Message ?? ex.Message}");
            try
            {
                queryResult = await _depotService.QueryAppAsync(appId, ct);
            }
            catch (Exception ex2)
            {
                AddLog($"查询异常：{ex2.InnerException?.Message ?? ex2.Message}");
                StatusMessage = "查询失败";
                return;
            }
        }

        if (queryResult == null)
        {
            var reason = await GetEmptyDepotsReasonAsync(appId);
            AddLog($"查询失败：{reason}");
            StatusMessage = "查询失败";
            return;
        }
        AddLog($"查询完成：{queryResult.AppName}");
        AddLog($"主游戏仓库: {queryResult.GameDepots.Count} 个");
        AddLog($"总DLC数量: {queryResult.DlcAppIds.Count} 个");

        // SteamKit2 兜底后主仓库仍为空：token 保护已在兜底中处理，
        // 剩余成因为未发售或数据暂缺，继续生成只会得到无密钥的空清单，直接终止
        if (queryResult.GameDepots.Count == 0)
        {
            var reason = await GetEmptyDepotsReasonAsync(appId);
            AddLog($"生成终止：{reason}");
            StatusMessage = "生成失败";
            return;
        }

        AddLog("下载密钥文件...");
        var keyReady = await _depotService.EnsureKeyFilesAsync(ct);
        if (!keyReady)
        {
            AddLog("下载密钥文件失败");
            StatusMessage = "下载密钥文件失败";
            return;
        }
        AddLog("密钥文件已就绪");

        AddLog("正在生成 Lua 配置文件...");
        string? luaPath;
        int includedDlc = -1;
        try
        {
            if (IncludeDlc && queryResult.DlcAppIds.Count > 0)
            {
                // DLC 信息获取进度（每 5 个一报，收尾必报），长等待不再零日志
                var dlcProgress = new Progress<(int Done, int Total)>(p =>
                {
                    if (p.Done == p.Total || p.Done % 5 == 0)
                        AddLog($"正在获取 DLC 信息 ({p.Done}/{p.Total})...");
                });
                luaPath = await _depotService.GenerateLuaWithDlcAsync(appId, ct, pinManifest: PinManifest, dlcProgress: dlcProgress, fetchNames: FetchNameComments);
                if (!string.IsNullOrEmpty(luaPath) && File.Exists(luaPath))
                {
                    var content = await File.ReadAllTextAsync(luaPath);
                    var included = queryResult.DlcAppIds.Count(id =>
                        Regex.IsMatch(content, $@"\badd(?:app|token)id\(\s*{id}\s*[,\)]", RegexOptions.IgnoreCase));
                    includedDlc = included;
                    AddLog($"包含 DLC 共 {included}/{queryResult.DlcAppIds.Count} 个");
                    if (included < queryResult.DlcAppIds.Count)
                        AddLog($"其中 {queryResult.DlcAppIds.Count - included} 个 DLC 因本地密钥仓库无对应 depots 密钥暂未收录");
                }
                else
                {
                    AddLog($"包含 DLC 共 {queryResult.DlcAppIds.Count} 个");
                }
            }
            else
            {
                if (!IncludeDlc && queryResult.DlcAppIds.Count > 0)
                    AddLog($"已跳过 {queryResult.DlcAppIds.Count} 个 DLC（未勾选 DLC入库）");
                luaPath = await _depotService.GenerateLuaAsync(appId, ct, pinManifest: PinManifest, fetchNames: FetchNameComments);
            }
        }
        catch (InvalidOperationException ex)
        {
            AddLog(ex.Message);
            StatusMessage = "入库失败：密钥未收录，请更新缓存或更换接口";
            return;
        }

        if (string.IsNullOrEmpty(luaPath))
        {
            AddLog("生成 Lua 文件失败");
            StatusMessage = "生成失败";
            return;
        }

        AddLog($"Lua 配置文件已保存：{luaPath}");
        AddLog($"入库成功！Lua 文件：{Path.GetFileName(luaPath)}");
        StatusMessage = includedDlc >= 0 && includedDlc < queryResult.DlcAppIds.Count
            ? $"入库成功：{queryResult.AppName}（DLC {includedDlc}/{queryResult.DlcAppIds.Count}，部分缺密钥）"
            : $"入库成功：{queryResult.AppName}";
    }

    private async Task ExecuteRemoteDownloadAsync(string gameId, string luaFolder, CancellationToken ct)
    {
        AddLog("获取下载地址...");
        var shortCode = await GetShortCodeAsync(gameId, ct);
        if (string.IsNullOrEmpty(shortCode))
        {
            AddLog("获取短码失败");
            StatusMessage = "获取短码失败";
            return;
        }
        AddLog($"获取短码：{shortCode}");

        AddLog("开始下载文件...");
        // 固定名 zip 会被并发/残留互覆盖，Guid 隔离；finally 必删
        var zipPath = Path.Combine(Path.GetTempPath(), $"steam_lua_{gameId}_{Guid.NewGuid():N}.zip");
        var luaCount = 0;
        try
        {
            var success = await DownloadFileAsync(shortCode, zipPath, ct);
            if (!success)
            {
                AddLog("下载失败");
                StatusMessage = "下载失败";
                return;
            }
            AddLog("文件下载完成");

            AddLog("正在解压...");
            luaCount = ExtractLuaFiles(zipPath, luaFolder);
            AddLog("清理临时压缩包");
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        if (luaCount > 0)
        {
            AddLog($"入库完成！共导入 {luaCount} 个 Lua 脚本");
            StatusMessage = $"成功入库 {luaCount} 个 Lua 脚本";
        }
        else
        {
            AddLog("未找到任何 Lua 文件");
            StatusMessage = "未找到 Lua 文件";
        }
    }

    private async Task<string?> GetShortCodeAsync(string gameId, CancellationToken ct)
    {
        var targetUrl = $"https://steamgames554.s3.us-east-1.amazonaws.com/{gameId}.zip";
        var payload = new Dictionary<string, string> { { "url", targetUrl } };

        return await RetryAsync(async () =>
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-remote-download",
                TimeSpan.FromSeconds(30),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://short.walftech.com/api_create_link.php")
                    {
                        Content = new FormUrlEncodedContent(payload)
                    };
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                    request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"148\", \"Microsoft Edge\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
                    request.Headers.TryAddWithoutValidation("accept", "*/*");
                    request.Headers.TryAddWithoutValidation("origin", "https://remlua.com");
                    request.Headers.TryAddWithoutValidation("referer", "https://remlua.com/");
                    return await client.SendAsync(request);
                });
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("short_code").GetString();
            }
            catch (Exception ex)
            {
                AddLog($"短码 API 响应解析失败：{ex.GetType().Name}: {ex.Message}");
                AddLog($"响应内容前 500 字符：{json[..Math.Min(json.Length, 500)]}");
                return null;
            }
        }, "获取短码", ct);
    }

    private async Task<bool> DownloadFileAsync(string shortCode, string savePath, CancellationToken ct)
    {
        var proxyUrl = $"https://short.walftech.com/proxy.php?short={shortCode}";
        return await RetryAsync(async () =>
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-remote-download",
                TimeSpan.FromSeconds(60),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
                    request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"148\", \"Microsoft Edge\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                    request.Headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");
                    request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
                    request.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                    request.Headers.TryAddWithoutValidation("sec-fetch-user", "?1");
                    request.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
                    request.Headers.TryAddWithoutValidation("referer", $"https://short.walftech.com/?id={shortCode}");
                    request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9");
                    request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0");
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                });
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            await contentStream.CopyToAsync(fileStream);
            fileStream.Flush(true);
            return true;
        }, "下载文件", ct);
    }

    // 只管退避等待与 attempt 间检查：在飞请求仍靠原有 60s/15s 超时，不动代理 helper；
    // 取消异常必须直接透出，否则取消会变成重试
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, string stepName, CancellationToken ct, int maxRetries = 3)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 只有用户取消才透出；在飞请求自身超时（TaskCanceledException）走下面重试
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= maxRetries)
                    break;

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                AddLog($"{stepName}失败（第{attempt}次）：{ex.GetType().Name}: {ex.Message}，{delay.TotalSeconds}s后重试...");
                await Task.Delay(delay, ct);
            }
        }
        AddLog($"{stepName}失败，已重试{maxRetries}次");
        throw new HttpRequestException($"{stepName}失败，请检查网络后重试", lastException);
    }

    private int ExtractLuaFiles(string zipPath, string targetDir)
    {
        int count = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    var destPath = Path.Combine(targetDir, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(destPath, overwrite: true);
                    count++;
                    AddLog($"导入：{Path.GetFileName(entry.FullName)}");
                }
            }
        }
        File.Delete(zipPath);
        return count;
    }

    private const int MaxLogLines = 500;

    private void AddLog(string message)
    {
        LogService.Info("入库", message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        // 异步投递不阻塞工作线程；顺序与调用序一致
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        });
    }

    [RelayCommand]
    private async Task RefreshKeyCacheAsync()
    {
        if (IsDownloading) return;
        try
        {
            IsDownloading = true;
            _depotService.UseDataSource(_currentDownloadMode);
            AddLog($"正在更新密钥缓存（{CurrentDataSourceLabel}）...");
            var updateResult = await _depotService.UpdateKeyFilesAsync();

            if (updateResult.Success)
            {
                var depotDelta = updateResult.DepotKeysNewCount - updateResult.DepotKeysOldCount;
                var tokenDelta = updateResult.TokenKeysNewCount - updateResult.TokenKeysOldCount;

                AddLog($"{CurrentDataSourceLabel}状态：");
                AddLog($"depotkeys.json 已更新：{updateResult.DepotKeysOldCount} → {updateResult.DepotKeysNewCount} 条 ({(depotDelta >= 0 ? "+" : "")}{depotDelta})");
                AddLog($"appaccesstokens.json 已更新：{updateResult.TokenKeysOldCount} → {updateResult.TokenKeysNewCount} 条 ({(tokenDelta >= 0 ? "+" : "")}{tokenDelta})");
                StatusMessage = $"{CurrentDataSourceLabel}密钥缓存已更新";
                OnPropertyChanged(nameof(LastUpdateTimeText));
            }
            else
            {
                AddLog("更新失败，请检查网络");
                StatusMessage = "更新失败";
            }
        }
        catch (Exception ex)
        {
            AddLog($"更新失败：{ex.Message}");
            StatusMessage = "更新失败";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        SearchResults.Clear();
        StatusMessage = "日志已清除";
    }
}
