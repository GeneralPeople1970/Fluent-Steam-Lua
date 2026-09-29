using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISteamDepotService
{
    void UseDataSource(string source);
    Task<DepotQueryResult?> QueryAppAsync(int appId, CancellationToken ct = default);
    Task<string?> GenerateLuaAsync(int appId, CancellationToken ct = default, bool pinManifest = false);
    Task<string?> GenerateLuaWithDlcAsync(int appId, CancellationToken ct = default, bool pinManifest = false);
    Task<DlcFetchResult> FetchDlcAsync(string luaPath, int dlcAppId, bool hasOwnDepot, CancellationToken ct = default, string? dlcName = null);
    /// <summary>批量查各 DLC 是否有独立仓库；单项失败返回 null（未知），调用方回退旧启发式。</summary>
    Task<Dictionary<int, bool?>> GetDlcHasDepotsAsync(IEnumerable<int> dlcIds, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default);
    Task<bool> EnsureKeyFilesAsync(CancellationToken ct = default);
    Task<KeyFileUpdateResult> UpdateKeyFilesAsync(CancellationToken ct = default);
    Task EnsureAllSourcesAsync(CancellationToken ct = default);
    DateTime? GetLastUpdateTime(string source);

    /// <summary>EnsureAllSourcesAsync 完成后触发，通知 UI 刷新时间显示。</summary>
    event Action? AllSourcesUpdated;
}

public class DlcFetchResult
{
    public bool Success { get; set; }
    public bool NeedKey { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class KeyFileUpdateResult
{
    public bool Success { get; set; }
    public int DepotKeysOldCount { get; set; }
    public int DepotKeysNewCount { get; set; }
    public int TokenKeysOldCount { get; set; }
    public int TokenKeysNewCount { get; set; }
    public long DepotKeysSizeBytes { get; set; }
    public long TokenKeysSizeBytes { get; set; }
}
