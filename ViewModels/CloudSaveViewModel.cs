using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace SteamLuaManager.ViewModels;

public partial class CloudSaveViewModel : ObservableObject
{
    private readonly ICloudRedirectService _cloudService;
    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly ISteamApiService _steamApiService;
    private readonly ILuaFileManager _luaFileManager;

    private bool _syncingCloudEnabled;
    private readonly Dictionary<int, string> _saveDirs = new();

    public ObservableCollection<GameInfo> RedirectedGames { get; } = new();

    public System.Collections.Generic.List<string> SortOptions { get; } =
        new() { "按存档时间降序", "按存档时间升序", "按AppID升序", "按AppID降序" };

    [ObservableProperty]
    private string _selectedSortOption = "按存档时间降序";

    [ObservableProperty]
    private bool _isCloudEnabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _syncPathText = "未配置";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _emptyHint = "暂无已重定向游戏：启用云存档后进游戏存一次档即会出现在列表";

    public CloudSaveViewModel(
        ICloudRedirectService cloudService,
        ISteamPathService steamPathService,
        ISettingsService settingsService,
        IDialogService dialogService,
        ISteamApiService steamApiService,
        ILuaFileManager luaFileManager)
    {
        _cloudService = cloudService;
        _steamPathService = steamPathService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _steamApiService = steamApiService;
        _luaFileManager = luaFileManager;
        CloudProviders = new System.Collections.Generic.List<CloudProviderOption>(_cloudService.ProviderOptions);
    }

    public void OnNavigatedTo() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        // 云端名单是 N+1 远端请求，设总超时兜底，避免无限挂起
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_refreshCts.Token, timeout.Token);
        try
        {
            IsBusy = true;
            await RefreshCoreAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // 被新切换中断静默让路；超时才提示
            if (timeout.IsCancellationRequested)
            {
                StatusMessage = "刷新超时，请检查网络后重试";
                LogService.Warn("云存档", "刷新云端名单超时");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 内部调用走这里：调用方已持有 IsBusy，不再经过入口守卫
    private async Task RefreshCoreAsync(CancellationToken ct = default)
    {
        try
        {
            StatusMessage = "正在刷新状态...";
            var st = await _cloudService.RefreshStatusAsync();
            _syncingCloudEnabled = true;
            try { IsCloudEnabled = st.CloudEnabled; }
            finally { _syncingCloudEnabled = false; }
            SyncPathText = string.IsNullOrEmpty(st.SyncPath) ? "未配置" : st.SyncPath;
            RefreshProviderBlock();
            EmptyHint = SelectedProvider == "folder"
                ? "暂无已重定向游戏：启用云存档后进游戏存一次档即会出现在列表"
                : "暂无云端存档：该源下尚未同步过，或登录/凭证未配置";
            RefreshProviderStatus();
            var baseStatus = StatusMessage;

            // 名单与封面沿用主页逻辑：先用 Lua 名单与本地缓存秒填，缺的再走网络补齐
            var progress = new Progress<string>(msg => StatusMessage = msg);
            List<RedirectedApp> scanned;
            try
            {
                scanned = await _cloudService.GetRedirectedAppsAsync(progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 名单失败不清旧源残留会误导，清空后报错；认证状态拼在后面保留
                _saveDirs.Clear();
                RedirectedGames.Clear();
                StatusMessage = string.IsNullOrEmpty(baseStatus)
                    ? $"刷新名单失败：{ex.Message}"
                    : $"刷新名单失败：{ex.Message}（{baseStatus}）";
                LogService.Warn("云存档", $"刷新名单失败: {ex.Message}");
                return;
            }
            var luaById = new Dictionary<int, GameInfo>();
            try
            {
                foreach (var g in await _luaFileManager.ScanLuaFilesAsync())
                    luaById[g.AppId] = g;
            }
            catch (Exception ex)
            {
                LogService.Warn("云存档", $"读取 Lua 游戏列表失败: {ex.Message}");
            }
            _saveDirs.Clear();
            var list = new List<GameInfo>();
            foreach (var s in scanned)
            {
                _saveDirs[s.AppId] = s.SaveDir;
                GameInfo g;
                if (luaById.TryGetValue(s.AppId, out var lg))
                    g = lg;
                else
                    g = new GameInfo { AppId = s.AppId, GameName = $"AppID: {s.AppId}" };
                g.LastSaveTime = s.LastSaveTime;
                list.Add(g);
            }
            _steamApiService.PopulateFromCache(list);
            RedirectedGames.Clear();
            foreach (var g in list)
                RedirectedGames.Add(g);
            ApplySorting();
            RefreshProviderStatus();
            _ = RefreshMissingInfoAsync(list);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败：{ex.Message}";
            LogService.Warn("云存档", $"刷新状态失败: {ex.Message}");
        }
    }

    partial void OnSelectedSortOptionChanged(string value) => ApplySorting();

    // 名单排序：默认存档时间倒序（无时间沉底），切换只重排不重拉
    private void ApplySorting()
    {
        if (RedirectedGames.Count == 0) return;
        var sorted = SelectedSortOption switch
        {
            "按存档时间升序" => RedirectedGames
                .OrderBy(g => g.LastSaveTime ?? DateTime.MaxValue)
                .ThenBy(g => g.AppId).ToList(),
            "按AppID升序" => RedirectedGames.OrderBy(g => g.AppId).ToList(),
            "按AppID降序" => RedirectedGames.OrderByDescending(g => g.AppId).ToList(),
            _ => RedirectedGames
                .OrderByDescending(g => g.LastSaveTime ?? DateTime.MinValue)
                .ThenBy(g => g.AppId).ToList(),
        };
        RedirectedGames.Clear();
        foreach (var g in sorted)
            RedirectedGames.Add(g);
    }

    // 后台补齐缺失的名称与封面，失败静默（本地已有内容不受影响）
    private async Task RefreshMissingInfoAsync(List<GameInfo> games)
    {
        try
        {
            await _steamApiService.RefreshGameInfoAsync(games);
            // 后台补到的名字可能改变按名排序，收尾重排一次
            ApplySorting();
        }
        catch (Exception ex)
        {
            LogService.Warn("云存档", $"补齐游戏信息失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenSaveFolder(int appId)
    {
        try
        {
            // 云端源无本地目录，打开对应网页控制台
            if (_cloudService.GetCloudProvider() != "folder")
            {
                var url = _cloudService.GetCloudConsoleUrl(appId);
                if (string.IsNullOrEmpty(url))
                {
                    StatusMessage = "无法定位云端目录，请先刷新名单";
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return;
            }
            if (!_saveDirs.TryGetValue(appId, out var dir) || !Directory.Exists(dir))
            {
                StatusMessage = "存档目录不存在";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "打开存档目录失败";
            LogService.Warn("云存档", $"打开存档目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteGameAsync(GameInfo? game)
    {
        if (IsBusy || game == null) return;
        if (SteamProcess.IsSteamRunning())
        {
            StatusMessage = "请先退出 Steam 再删除存档";
            await _dialogService.ShowAlertAsync("Steam 正在运行",
                "删除存档前请先完全退出 Steam，否则残留缓存可能把已删文件复活。");
            return;
        }

        // 提供商在 preview 前捕获：确认文案、删除语义、完成提示与本次 preview 口径一致
        var isCloud = _cloudService.GetCloudProvider() != "folder";
        DeletePreview preview;
        try
        {
            preview = await Task.Run(() => _cloudService.PreviewAppDeleteAsync(game.AppId));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"统计待删存档失败: {ex.Message}");
            return;
        }
        if (preview.Targets.Count == 0)
        {
            StatusMessage = "没有可删除的存档";
            return;
        }

        var displayName = string.IsNullOrEmpty(game.GameName) ? game.AppId.ToString() : game.GameName;
        var confirmNote = isCloud
            ? "注：云端源下将删除远端存档（本地 DLL 缓存与 Steam 用户数据一并清理），删除前自动下载备份到本地。"
            : null;
        if (!await _dialogService.ShowDeleteSavesConfirmAsync(displayName, game.AppId, preview.Targets, preview.BackupDir, confirmNote))
            return;

        try
        {
            IsBusy = true;
            var progress = new Progress<string>(msg => StatusMessage = msg);
            await _cloudService.DeleteAppSavesAsync(preview, progress);
            await RefreshCoreAsync();
            await _dialogService.ShowAlertAsync("删除完成",
                isCloud
                    ? $"已删除《{displayName}》的全部存档（含云端远端）。\n\n备份位于：\n{preview.BackupDir}\n恢复需手动上传回云端对应目录。"
                    : $"已删除《{displayName}》的全部存档。\n\n备份位于：\n{preview.BackupDir}\n恢复需手动拷回对应目录。");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"删除存档失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsCloudEnabledChanged(bool value)
    {
        if (_syncingCloudEnabled) return;
        _ = ApplyCloudEnabledAsync(value);
    }

    private void RevertToggle(bool value)
    {
        _syncingCloudEnabled = true;
        try { IsCloudEnabled = value; }
        finally { _syncingCloudEnabled = false; }
    }

    private async Task ApplyCloudEnabledAsync(bool value)
    {
        if (IsBusy)
        {
            RevertToggle(!value);
            return;
        }
        try
        {
            IsBusy = true;
            if (value)
            {
                var settings = _settingsService.Load();
                if (!settings.CloudBackupConfirmed)
                {
                    var confirmed = await _dialogService.ShowConfirmAsync(
                        "启用前确认",
                        "云存档会接管 Lua 游戏的存档读写，首次启用前请先备份重要存档。\n\n确认已备份并启用吗？",
                        "已备份，启用", "取消");
                    if (!confirmed)
                    {
                        RevertToggle(false);
                        return;
                    }
                    settings.CloudBackupConfirmed = true;
                    _settingsService.Save(settings);
                }

                var progress = new Progress<string>(msg => StatusMessage = msg);
                await _cloudService.EnableAsync(null, progress);
                await PromptRestartSteamAsync("云存档已启用");
            }
            else
            {
                await _cloudService.DisableAsync();
                await PromptRestartSteamAsync("云存档已关闭");
            }
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"切换开关失败: {ex.Message}");
            RevertToggle(!value);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 重启二选一：立即重启走共用逻辑，暂不重启保留现状
    private async Task PromptRestartSteamAsync(string doneMessage)
    {
        var restart = await _dialogService.ShowConfirmAsync(
            "需要重启 Steam",
            $"{doneMessage}，需重启 Steam 后生效。\n\n是否立即重启 Steam？",
            "立即重启", "暂不重启");
        if (!restart) return;
        var result = SteamProcess.RestartSteam(_steamPathService);
        if (!result.Ok)
        {
            StatusMessage = result.Message;
            LogService.Warn("云存档", $"重启 Steam 失败: {result.Message}");
        }
    }

    [RelayCommand]
    private async Task BrowseSyncPathAsync()
    {
        if (IsBusy) return;
        string? dir;
        try
        {
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            var owner = mainWindow == null
                ? IntPtr.Zero
                : new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
            dir = FolderPicker.PickFolder(
                SyncPathText == "未配置" ? null : SyncPathText, owner);
        }
        catch (Exception ex)
        {
            StatusMessage = "打开目录选择失败，请重试";
            LogService.Error("云存档", $"选择本地目录失败: {ex}");
            return;
        }
        if (string.IsNullOrEmpty(dir)) return;
        await ApplyNewSyncPathAsync(dir);
    }

    [RelayCommand]
    private async Task ResetSyncPathAsync()
    {
        var def = _cloudService.GetDefaultSyncPath();
        if (string.IsNullOrEmpty(def))
        {
            StatusMessage = "未检测到 Steam 路径";
            LogService.Warn("云存档", "重置目录失败：未检测到 Steam 路径");
            return;
        }
        await ApplyNewSyncPathAsync(def);
    }

    // ========== 云端提供商（与上游 companion 同契约，唯一真相在 config.json） ==========

    public System.Collections.Generic.List<CloudProviderOption> CloudProviders { get; }

    [ObservableProperty]
    private string _selectedProvider = "folder";

    [ObservableProperty]
    private bool _isCloudProvider;

    [ObservableProperty]
    private string _effectiveRootText = "";

    [ObservableProperty]
    private bool _isProviderSignedIn;

    [ObservableProperty]
    private bool _isSigningIn;

    public System.Collections.ObjectModel.ObservableCollection<string> AuthLogLines { get; } = new();

    // R2 表单
    [ObservableProperty] private string _r2AccountId = "";
    [ObservableProperty] private string _r2AccessKeyId = "";
    [ObservableProperty] private string _r2SecretKey = "";
    [ObservableProperty] private string _r2Bucket = "";
    [ObservableProperty] private string _r2KeyPrefix = "";
    [ObservableProperty] private string _r2Endpoint = "";

    // S3 表单
    [ObservableProperty] private string _s3AccessKeyId = "";
    [ObservableProperty] private string _s3SecretKey = "";
    [ObservableProperty] private string _s3Bucket = "";
    [ObservableProperty] private string _s3Endpoint = "";
    [ObservableProperty] private string _s3Region = "";
    [ObservableProperty] private string _s3KeyPrefix = "";
    [ObservableProperty] private string _s3CaCertPath = "";
    [ObservableProperty] private bool _s3SignPayload;
    [ObservableProperty] private bool _s3AllowInsecureHttp;
    [ObservableProperty] private bool _s3AllowInsecureTls;

    private bool _syncingProvider;
    private CancellationTokenSource? _signInCts;
    private CancellationTokenSource? _refreshCts;
    private int _authLogGeneration;

    partial void OnSelectedProviderChanged(string value)
    {
        if (_syncingProvider) return;
        IsCloudProvider = value != "folder";
        try
        {
            _cloudService.SetCloudProvider(value);
            LogService.Info("云存档", $"云端提供商已切换为{ProviderDisplayName(value)}，重启 Steam 后生效");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"切换提供商失败: {ex.Message}");
            RefreshProviderStatus();
            return;
        }
        RefreshProviderStatus();
        // 切源即切名单：中断进行中的刷新，以新源为准重拉
        _ = RefreshAfterProviderSwitchAsync();
        // DLL 启动时读配置，切源必须重启 Steam 才生效；连点切换只弹一次
        _ = PromptRestartAfterSwitchAsync("云端提供商已切换");
    }

    private bool _restartPromptShowing;

    private async Task PromptRestartAfterSwitchAsync(string doneMessage)
    {
        if (_restartPromptShowing) return;
        _restartPromptShowing = true;
        try { await PromptRestartSteamAsync(doneMessage); }
        finally { _restartPromptShowing = false; }
    }

    // 等待进行中的刷新让路，避免 IsBusy 守卫吞掉本次切换；5 秒等不到则放弃，用户可手动刷新
    private async Task RefreshAfterProviderSwitchAsync()
    {
        _refreshCts?.Cancel();
        for (var i = 0; i < 50 && IsBusy; i++)
            await Task.Delay(100);
        await RefreshAsync();
    }

    private string ProviderDisplayName(string id) =>
        CloudProviders.FirstOrDefault(p => p.Id == id)?.DisplayName ?? id;

    // 切源/刷新后重读：下拉回显 + 状态行 + 有效根目录 + R2/S3 表单预填（secret 为空则不覆盖界面输入）
    private void RefreshProviderBlock()
    {
        _syncingProvider = true;
        try { SelectedProvider = _cloudService.GetCloudProvider(); }
        finally { _syncingProvider = false; }
        IsCloudProvider = SelectedProvider != "folder";
        RefreshEffectiveRoot();
        PrefillCredentialForms();
    }

    // 有效远端根目录：两台机器核对前缀是否一致就看这里
    private void RefreshEffectiveRoot()
    {
        try
        {
            var root = _cloudService.GetEffectiveRemoteRoot();
            EffectiveRootText = string.IsNullOrEmpty(root) ? "" : $"远端根目录：{root}";
        }
        catch
        {
            EffectiveRootText = "";
        }
    }

    // 登录态单独刷新：登录/保存凭证后只翻按钮状态，不覆盖操作结果提示
    private void UpdateSignedInFlag()
    {
        try
        {
            IsProviderSignedIn = SelectedProvider switch
            {
                "gdrive" or "onedrive" => _cloudService.CheckOAuthToken(SelectedProvider).Ok,
                "r2" => _cloudService.LoadR2Credentials() != null,
                "s3" => _cloudService.LoadS3Credentials() != null,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            IsProviderSignedIn = false;
            LogService.Warn("云存档", $"读取登录态失败: {ex.Message}");
        }
    }

    // 提供商状态统一走顶部状态行；本地目录不显示，保持界面干净；
    // 登录态同步到 IsProviderSignedIn，供登录面板切换按钮
    private void RefreshProviderStatus()
    {
        try
        {
            UpdateSignedInFlag();
            switch (SelectedProvider)
            {
                case "gdrive" or "onedrive":
                    var (ok, msg) = _cloudService.CheckOAuthToken(SelectedProvider);
                    StatusMessage = ok ? $"已登录：{msg}" : $"未登录：{msg}";
                    break;
                case "r2":
                case "s3":
                    var (credOk, credMsg) = _cloudService.CheckStoredCredentials(SelectedProvider);
                    StatusMessage = credMsg;
                    break;
                default:
                    StatusMessage = string.Empty;
                    break;
            }
        }
        catch (Exception ex)
        {
            IsProviderSignedIn = false;
            StatusMessage = $"状态读取失败：{ex.Message}";
            LogService.Warn("云存档", $"读取提供商状态失败: {ex.Message}");
        }
    }

    private void PrefillCredentialForms()
    {
        try
        {
            var r2 = _cloudService.LoadR2Credentials();
            if (r2 != null)
            {
                R2AccountId = r2.AccountId;
                R2AccessKeyId = r2.AccessKeyId;
                R2Bucket = r2.Bucket;
                if (!string.IsNullOrEmpty(r2.KeyPrefix)) R2KeyPrefix = r2.KeyPrefix;
                if (!string.IsNullOrEmpty(r2.Endpoint)) R2Endpoint = r2.Endpoint;
                if (!string.IsNullOrEmpty(r2.SecretAccessKey)) R2SecretKey = r2.SecretAccessKey;
            }
            var s3 = _cloudService.LoadS3Credentials();
            if (s3 != null)
            {
                S3AccessKeyId = s3.AccessKeyId;
                S3Bucket = s3.Bucket;
                S3Endpoint = s3.Endpoint;
                S3Region = s3.Region;
                if (!string.IsNullOrEmpty(s3.KeyPrefix)) S3KeyPrefix = s3.KeyPrefix;
                if (!string.IsNullOrEmpty(s3.CaCertPath)) S3CaCertPath = s3.CaCertPath;
                S3SignPayload = s3.SignPayload;
                S3AllowInsecureHttp = s3.AllowInsecureHttp;
                S3AllowInsecureTls = s3.AllowInsecureTls;
                if (!string.IsNullOrEmpty(s3.SecretAccessKey)) S3SecretKey = s3.SecretAccessKey;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("云存档", $"预填凭证表单失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn) return;
        if (SelectedProvider is not ("gdrive" or "onedrive")) return;
        IsSigningIn = true;
        _authLogGeneration++;
        AuthLogLines.Clear();
        _signInCts = new CancellationTokenSource();
        void Log(string msg) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => AuthLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"));
        try
        {
            using var oauth = new CloudOAuthService();
            var tokenPath = _cloudService.GetTokenPath(SelectedProvider);
            var ok = await oauth.AuthorizeAsync(SelectedProvider, tokenPath, Log, _signInCts.Token);
            StatusMessage = ok ? "登录成功，重启 Steam 后生效" : "登录未完成";
            if (ok) ScheduleAuthLogAutoClear();
            UpdateSignedInFlag();
            LogService.Info("云存档", $"OAuth 登录{(ok ? "成功" : "未完成")}：{SelectedProvider}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"登录异常：{ex.Message}";
            LogService.Warn("云存档", $"OAuth 登录异常: {ex.Message}");
        }
        finally
        {
            IsSigningIn = false;
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    [RelayCommand]
    private void ClearAuthLog()
    {
        _authLogGeneration++;
        AuthLogLines.Clear();
    }

    // 登录成功后日志框 8 秒自动收起；期间重登/手动清空则取消本次自动清理
    private void ScheduleAuthLogAutoClear()
    {
        var gen = _authLogGeneration;
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
        {
            if (gen == _authLogGeneration)
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => AuthLogLines.Clear());
        });
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        try { _signInCts?.Cancel(); } catch { }
    }

    // 连接测试：只列举两级目录，不断 stats；0 数据不算错（提示核对前缀），鉴权/网络失败才报错
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsBusy || IsSigningIn) return;
        var provider = _cloudService.GetCloudProvider();
        if (provider == "folder")
        {
            StatusMessage = "本地目录模式无需连接测试";
            return;
        }
        var display = ProviderDisplayName(provider);
        var root = _cloudService.GetEffectiveRemoteRoot();
        try
        {
            IsBusy = true;
            StatusMessage = "正在测试远端连接…";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await _cloudService.TestCloudConnectionAsync(cts.Token);
            if (!string.IsNullOrEmpty(r.Error))
            {
                StatusMessage = $"连接测试失败：{r.Error}";
                LogService.Warn("云存档", $"连接测试失败：{provider} {r.Error}");
                await _dialogService.ShowAlertAsync("连接测试失败",
                    $"提供商：{display}\n远端根目录：{root}\n\n错误：{r.Error}");
                return;
            }
            var detail = r.AccountCount == 0
                ? "认证通过，但该前缀下无数据。请核对 key_prefix/目录名，或 DLL 尚未同步过。"
                : $"账号 {r.AccountCount} 个" +
                  (r.AppCount > 0 ? $"，首个账号下应用 {r.AppCount} 个（如 {r.SamplePath}）" : "，首个账号下暂无应用");
            StatusMessage = "连接测试通过";
            LogService.Info("云存档", $"连接测试通过：{provider} {detail}");
            await _dialogService.ShowAlertAsync("连接测试通过",
                $"提供商：{display}\n远端根目录：{root}\n\n{detail}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "连接测试超时，请检查网络后重试";
            LogService.Warn("云存档", "连接测试超时");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"连接测试异常: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 退出登录：删 token/凭证文件；DLL 共用同一文件，退出后该源同步即失效，需二次确认
    [RelayCommand]
    private async Task SignOutAsync(string? provider)
    {
        if (IsBusy || IsSigningIn) return;
        var target = string.IsNullOrEmpty(provider) ? SelectedProvider : provider;
        var display = ProviderDisplayName(target);
        var isOAuth = target is "gdrive" or "onedrive";
        var confirmed = await _dialogService.ShowConfirmAsync(
            isOAuth ? "退出登录" : "清除凭证",
            isOAuth
                ? $"退出{display}登录后，DLL 也无法再同步到该源（需重启 Steam 生效）。本地备份不受影响。\n\n确认退出吗？"
                : $"清除{display}凭证后，DLL 也无法再同步到该源（需重启 Steam 生效）。本地备份不受影响。\n\n确认清除吗？",
            isOAuth ? "退出登录" : "清除凭证", "取消");
        if (!confirmed) return;
        try
        {
            IsBusy = true;
            await Task.Run(() => _cloudService.SignOut(target));
            // 退出后清空界面 secret（文件已删，留着旧值误导人）；其他表单项保持原样
            if (target == "r2") R2SecretKey = "";
            if (target == "s3") S3SecretKey = "";
            StatusMessage = isOAuth ? "已退出登录" : "凭证已清除";
            LogService.Info("云存档", StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"退出登录失败: {ex.Message}");
            return;
        }
        finally
        {
            IsBusy = false;
        }
        RefreshProviderStatus();
        RefreshEffectiveRoot();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveR2Async()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            var path = await Task.Run(() => _cloudService.SaveR2Credentials(new R2Credentials(
                R2AccountId.Trim(), R2AccessKeyId.Trim(), R2SecretKey,
                R2Bucket.Trim(), R2KeyPrefix.Trim(), R2Endpoint.Trim())));
            StatusMessage = $"R2 凭证已保存，重启 Steam 后生效";
            LogService.Info("云存档", $"R2 凭证已保存：{path}");
            SyncProviderSelection("r2");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"保存 R2 凭证失败: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveS3Async()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            var path = await Task.Run(() => _cloudService.SaveS3Credentials(new S3Credentials(
                S3AccessKeyId.Trim(), S3SecretKey, S3Bucket.Trim(),
                S3Endpoint.Trim(), S3Region.Trim(), S3KeyPrefix.Trim(),
                S3SignPayload, S3AllowInsecureHttp, S3AllowInsecureTls, S3CaCertPath.Trim())));
            StatusMessage = $"S3 凭证已保存，重启 Steam 后生效";
            LogService.Info("云存档", $"S3 凭证已保存：{path}");
            SyncProviderSelection("s3");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"保存 S3 凭证失败: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void BrowseCaCert()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 CA 证书",
                Filter = "PEM 文件|*.pem|证书文件|*.crt;*.cer|所有文件|*.*",
                CheckFileExists = true,
            };
            if (!string.IsNullOrEmpty(S3CaCertPath) && System.IO.File.Exists(S3CaCertPath))
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(S3CaCertPath);
            if (dialog.ShowDialog() == true)
                S3CaCertPath = dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusMessage = "打开文件选择失败，请重试";
            LogService.Warn("云存档", $"选择 CA 证书失败: {ex.Message}");
        }
    }

    // 保存凭证后把下拉静默同步到对应源（服务侧已落盘，这里只做界面回显，不重触发变更提示）
    private void SyncProviderSelection(string id)
    {
        if (SelectedProvider == id) return;
        _syncingProvider = true;
        try { SelectedProvider = id; }
        finally { _syncingProvider = false; }
    }

    // 目录切换统一走这里：同目录直接跳过，否则先搬旧存档再切配置
    private async Task ApplyNewSyncPathAsync(string dir)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            var current = SyncPathText == "未配置" ? null : SyncPathText;
            if (!string.IsNullOrEmpty(current) && string.Equals(
                    current.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return;

            // 先搬旧存档再切配置，旧目录无存档则直接切换
            string? migrateFailure = null;
            if (!string.IsNullOrEmpty(current) && System.IO.Directory.Exists(current))
            {
                var migrateProgress = new Progress<string>(msg => StatusMessage = msg);
                var m = await _cloudService.MigrateSavesAsync(current, dir, migrateProgress);
                if (m.FailedFiles.Count > 0)
                {
                    LogService.Warn("云存档", $"迁移失败文件: {string.Join(", ", m.FailedFiles)}");
                    migrateFailure = $"已迁移 {m.MovedFiles} 个文件，{m.FailedFiles.Count} 个失败（可能被 Steam 占用），重启 Steam 后可手动复制剩余文件";
                    StatusMessage = migrateFailure;
                }
                else if (m.MovedFiles > 0)
                {
                    StatusMessage = $"已迁移 {m.MovedFiles} 个文件";
                }
            }
            await _cloudService.SetSyncPathAsync(dir);
            await PromptRestartSteamAsync("重定向目录已更新");
            await RefreshCoreAsync();
            // 刷新会清空状态行，失败摘要需要保留
            if (migrateFailure != null)
                StatusMessage = migrateFailure;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"切换目录失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
