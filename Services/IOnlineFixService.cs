using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface IOnlineFixService
{
    Task<IReadOnlyList<OnlineFixSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<OnlineFixDetail?> GetDetailAsync(string articleUrl, CancellationToken ct = default);
    Task<IReadOnlyList<OnlineFixFileEntry>> ListFilesAsync(string dirUrl, CancellationToken ct = default);
    Task DownloadFileAsync(string fileUrl, string refererDirUrl, string destPath,
        IProgress<double>? progress = null, CancellationToken ct = default);
}
