using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class OnlineFixViewModel : ObservableObject, IDisposable
{
    private readonly IOnlineFixService _onlineFixService;
    private readonly IDialogService _dialogService;
    private bool _disposed;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _queryCts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // Pill 视图切换：默认 480 联机
    [ObservableProperty]
    private bool _isShowing480 = true;

    [ObservableProperty]
    private bool _isShowingPatch;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isSearchEmptyVisible;

    [ObservableProperty]
    private bool _isLoadingDetail;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    private Timer? _statusMessageTimer;

    // 通知 3 秒自动消失，跟主页逻辑一致
    partial void OnStatusMessageChanged(string value)
    {
        _statusMessageTimer?.Dispose();
        _statusMessageTimer = null;
        if (!string.IsNullOrEmpty(value))
        {
            _statusMessageTimer = new Timer(_ => Application.Current.Dispatcher.Invoke(() => StatusMessage = string.Empty),
                null, 3000, Timeout.Infinite);
        }
    }

    public ObservableCollection<OnlineFixSearchResult> SearchResults { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private OnlineFixDetail? _detail;
    public OnlineFixDetail? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(IsDetailVisible));
                OnPropertyChanged(nameof(HasFiles));
                RefreshSearchEmptyVisible();
            }
        }
    }

    public bool IsDetailVisible => Detail != null;
    public bool HasFiles => Detail != null && Detail.Files.Count > 0;

    public OnlineFixViewModel(IOnlineFixService onlineFixService, IDialogService dialogService)
    {
        _onlineFixService = onlineFixService;
        _dialogService = dialogService;
        SearchResults.CollectionChanged += (_, _) => RefreshSearchEmptyVisible();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelQuery();
        CancelDownload();
        try { _downloadCts?.Dispose(); } catch { }
        _downloadCts = null;
    }

    // 同步命令 CanExecute 恒 true：下载中取消按钮必须可点，不能复用异步的 DownloadCommand
    //（AsyncRelayCommand 执行期间 CanExecute 自动为 false，会把取消按钮一并禁用）
    [RelayCommand]
    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    private void CancelQuery()
    {
        try { _queryCts?.Cancel(); } catch { }
        try { _queryCts?.Dispose(); } catch { }
        _queryCts = null;
    }

    private void RefreshSearchEmptyVisible()
        => IsSearchEmptyVisible = HasSearched && SearchResults.Count == 0 && !IsSearching && Detail == null;

    // 切页清空由 MainWindow 调用
    public void ResetState()
    {
        CancelQuery();
        CancelDownload();
        HasSearched = false;
        Detail = null;
        RefreshSearchEmptyVisible();
    }

    private void AddLog(string message)
    {
        LogService.Info("联机", message);
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private void Show480View()
    {
        IsShowing480 = true;
        IsShowingPatch = false;
    }

    [RelayCommand]
    private void ShowPatchView()
    {
        IsShowing480 = false;
        IsShowingPatch = true;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusMessage = "请输入游戏名";
            return;
        }
        if (IsSearching) return;
        CancelQuery();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        IsSearching = true;
        SearchResults.Clear();
        LogLines.Clear();
        Detail = null;
        StatusMessage = "";
        AddLog($"搜索联机补丁：{query}");
        try
        {
            var results = await _onlineFixService.SearchAsync(query, ct);
            foreach (var r in results)
                SearchResults.Add(r);
            StatusMessage = results.Count == 0 ? "未找到匹配的游戏" : $"找到 {results.Count} 个结果";
            AddLog(results.Count == 0 ? "未找到匹配的游戏" : $"找到 {results.Count} 个结果");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = "已取消搜索";
        }
        catch (OperationCanceledException)
        {
            // HttpClient 超时也是 OCE，但此时并未取消——源站境外，基本都是裸连太慢
            StatusMessage = "搜索超时，请开启代理或梯子后重试";
            AddLog("搜索超时：源站为境外俄罗斯站点，国内裸连很慢，请开启代理或梯子后重试");
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败：{ex.Message}";
            AddLog($"搜索失败：{ex.Message}");
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
            RefreshSearchEmptyVisible();
        }
    }

    [RelayCommand]
    private async Task LoadDetailAsync(OnlineFixSearchResult? entry)
    {
        if (entry == null || IsLoadingDetail) return;
        CancelQuery();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        IsLoadingDetail = true;
        StatusMessage = "正在读取补丁详情…";
        AddLog($"读取补丁详情：{entry.Title}");
        try
        {
            Detail = await _onlineFixService.GetDetailAsync(entry.Url, ct);
            if (Detail == null)
            {
                StatusMessage = "详情解析失败，该游戏可能暂无直链补丁";
                AddLog("详情解析失败（无官方直链目录）");
            }
            else
            {
                // 进详情即清结果列表，只看当前游戏
                SearchResults.Clear();
                StatusMessage = Detail.Files.Count > 0
                    ? $"已加载 {Detail.Files.Count} 个补丁文件"
                    : "详情已加载，该游戏暂无 Fix Repair 补丁文件";
                AddLog($"详情已加载：{Detail.Title}，补丁文件 {Detail.Files.Count} 个");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = "已取消";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "读取超时，请开启代理或梯子后重试";
            AddLog("读取详情超时：源站为境外俄罗斯站点，国内裸连很慢，请开启代理或梯子后重试");
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取详情失败：{ex.Message}";
            AddLog($"读取详情失败：{ex.Message}");
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync(OnlineFixFileEntry? file)
    {
        // 下载中点取消（取消按钮传 null）；平时按文件下载
        if (IsDownloading)
        {
            CancelDownload();
            return;
        }
        if (file == null || Detail == null) return;
        // 下载全程只认快照：中途再搜索会把 Detail 置空，直接读属性必 NRE
        var detail = Detail;
        CancelDownload();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var ct = cts.Token;

        var dir = Path.Combine(AppContext.BaseDirectory, "cache", "onlinefix", detail.GameDirName);
        var dest = Path.Combine(dir, file.Name);
        if (File.Exists(dest))
        {
            var overwrite = await _dialogService.ShowConfirmAsync(
                "覆盖确认", $"已存在 {file.Name}，重新下载会覆盖，继续吗？", "覆盖", "取消");
            if (!overwrite) return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusMessage = $"正在下载 {file.Name}…";
        AddLog($"开始下载补丁：{file.Name}");
        try
        {
            Directory.CreateDirectory(dir);
            var progress = new Progress<double>(p => DownloadProgress = p);
            await _onlineFixService.DownloadFileAsync(file.Url, detail.UploadsBaseUrl + "Fix%20Repair/", dest, progress, ct);
            await File.WriteAllTextAsync(BuildInstructionPath(dir), BuildInstructionText(detail), ct);
            StatusMessage = $"下载完成：{file.Name}";
            AddLog($"补丁已保存：{dest}");
            var open = await _dialogService.ShowConfirmAsync(
                "下载完成", $"补丁已保存到：\n{dir}\n\n使用说明 (必看).txt 已一并放入，是否打开目录？",
                "打开目录", "关闭");
            if (open)
            {
                try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { StatusMessage = $"打开目录失败：{ex.Message}"; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = "下载已取消";
            AddLog("下载已取消");
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "下载超时，请开启代理或梯子后重试";
            AddLog("下载超时：源站为境外俄罗斯站点，国内裸连很慢，请开启代理或梯子后重试");
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }
        catch (Exception ex)
        {
            StatusMessage = $"下载失败：{ex.Message}";
            AddLog($"下载失败：{ex.Message}");
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
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

    private static string BuildInstructionPath(string dir)
        => Path.Combine(dir, "使用说明 (必看).txt");

    // 使用说明取自详情页，俄文原文照搬；顶部追加中文提示让用户自行翻译
    private static string BuildInstructionText(OnlineFixDetail detail)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("以下为 OnlineFix 官方俄文使用说明，请自行翻译查看：");
        sb.AppendLine();
        sb.AppendLine($"游戏：{detail.Title}");
        if (!string.IsNullOrEmpty(detail.VersionText)) sb.AppendLine($"版本：{detail.VersionText}");
        if (!string.IsNullOrEmpty(detail.Tablet)) sb.AppendLine($"来源：{detail.Tablet}");
        sb.AppendLine($"原文地址：{detail.ArticleUrl}");
        sb.AppendLine($"补丁压缩包解压密码：online-fix.me");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(detail.Instructions) ? "（本补丁未附带使用说明）" : detail.Instructions);
        return sb.ToString();
    }
}
