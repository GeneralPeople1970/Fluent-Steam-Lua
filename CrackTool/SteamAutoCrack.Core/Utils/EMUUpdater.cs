using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SteamAutoCrack.Core.Utils
{
    public class EMUUpdater
    {
        private const string GoldbergReleaseUrl = "https://api.github.com/repos/Detanup01/gbe_fork/releases";

        public static bool Downloading;
        private readonly ILogger _log;

        /// <summary>
        ///     GitHub API Token for authentication.
        /// </summary>
        public string? GitHubToken { get; set; }

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        static EMUUpdater()
        {
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/57.0.2987.133 Safari/537.36");
            }
        }

        private bool _bInited;
        private string _currentVersion = string.Empty;
        private string _latestVersion = string.Empty;
        private string _downloadUrl = string.Empty;
        private long _expectedSize = 0;
        private string _expectedSha256 = string.Empty;

        public EMUUpdater(string? gitHubToken = null)
        {
            _log = Log.ForContext<EMUUpdater>();
            GitHubToken = gitHubToken ?? Config.Config.GitHubToken;
        }

        // 下载源候选：调用方按偏好给多个 URL（直连/镜像），逐个试到成功为止；
        // 为空则保持上游原行为（只用直连）。由宿主 App 的接口设置生成。
        public Func<string, IEnumerable<string>>? UrlCandidates { get; set; }

        private IEnumerable<string> Candidates(string url)
            => UrlCandidates != null ? UrlCandidates(url) : new[] { url };

        // 日志里写清走直连还是哪个镜像
        private static string SourceLabel(string url)
        {
            try
            {
                var host = new Uri(url).Host;
                if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                    return "直连";
                return host;
            }
            catch { return url; }
        }

        public async Task Init()
        {
            _currentVersion = GetCurrentGoldbergVersion();
            _bInited = await FetchLatestReleaseInfo().ConfigureAwait(false);
        }

        public async Task<bool> Download(bool force = false)
        {
            if (Downloading)
            {
                _log.Information("下载任务已在进行中，跳过重复请求");
                return false;
            }

            if (!_bInited)
            {
                _log.Error("更新器未初始化，或从 GitHub 获取版本信息失败");
                return false;
            }

            Downloading = true;
            try
            {
                _log.Information("模拟器版本：当前 {Current}，最新 {Latest}",
                    string.IsNullOrEmpty(_currentVersion) ? "None" : _currentVersion, _latestVersion);

                if (!force && _currentVersion.Equals(_latestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Information("模拟器已是最新版");
                    return true;
                }

                if (!Directory.Exists(Config.Config.TempPath))
                    Directory.CreateDirectory(Config.Config.TempPath);

                var tempArchiveFile = Path.Combine(Config.Config.TempPath, "Goldberg.7z");

                _log.Information("开始下载模拟器…");
                var downloaded = false;
                foreach (var dlUrl in Candidates(_downloadUrl))
                {
                    try
                    {
                        _log.Information("尝试下载源：{Source}…", SourceLabel(dlUrl));
                        await DownloadFileAsync(dlUrl, tempArchiveFile, _expectedSize).ConfigureAwait(false);
                        downloaded = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("该源下载失败，换下一个源：{Msg}", ex.Message);
                        try { if (File.Exists(tempArchiveFile)) File.Delete(tempArchiveFile); } catch { }
                    }
                }
                if (!downloaded)
                    throw new InvalidDataException("所有下载源均失败，请检查网络或更换镜像源后重试");

                if (!string.IsNullOrEmpty(_expectedSha256))
                {
                    _log.Debug("Verifying downloaded file SHA256...");
                    var actualSha256 = ComputeSha256(tempArchiveFile);
                    _log.Debug("Downloaded SHA256: {Actual}, Expected: {Expected}", actualSha256, _expectedSha256);

                    if (!actualSha256.Equals(_expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(tempArchiveFile)) File.Delete(tempArchiveFile);
                        throw new InvalidDataException($"下载文件校验不对。应为：{_expectedSha256}，实际：{actualSha256}");
                    }
                    _log.Debug("SHA256 verification passed.");
                }

                _log.Information("正在解压模拟器…");
                await SafeExtractAndDeploy(tempArchiveFile).ConfigureAwait(false);

                var versionFile = Path.Combine(Config.Config.GoldbergPath, "version");
                await File.WriteAllTextAsync(versionFile, _latestVersion).ConfigureAwait(false);
                var oldCommitFile = Path.Combine(Config.Config.GoldbergPath, "commit_id");
                if (File.Exists(oldCommitFile))
                {
                    try { File.Delete(oldCommitFile); } catch { /* ignore */ }
                }

                _log.Information("模拟器已更新到 {Version}", _latestVersion);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "更新模拟器时出错");
                return false;
            }
            finally
            {
                Downloading = false;
            }
        }

        private async Task<bool> FetchLatestReleaseInfo()
        {
            foreach (var apiUrl in Candidates(GoldbergReleaseUrl))
            {
                try
                {
                    _log.Information("正在查询最新版本（源：{Source}）…", SourceLabel(apiUrl));
                    using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

                var token = GitHubToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    token = Config.Config.GitHubToken;
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    token = token.Trim();
                    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        token = token["Bearer ".Length..].Trim();
                    }
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    _log.Debug("Using GitHub API token for authentication.");
                }

                using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _log.Warning("该源查询失败，状态码：{StatusCode}，换下一个源", response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                JsonElement latestRelease;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    if (doc.RootElement.GetArrayLength() == 0)
                    {
                        _log.Error("没有找到可用版本");
                        return false;
                    }
                    latestRelease = doc.RootElement[0];
                }
                else
                {
                    latestRelease = doc.RootElement;
                }

                _latestVersion = latestRelease.GetProperty("tag_name").GetString() ?? string.Empty;

                if (latestRelease.TryGetProperty("assets", out var assets))
                {
                    string? bestName = null;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString();
                        // 只要 release 包：debug 包解出来是 debug/ 目录，根本装不上；
                        // vs 工具集后缀会变（vs22/vs26…），按名字取最新，不写死
                        if (name != null && name.StartsWith("emu-win-release", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                            && (bestName == null || string.Compare(name, bestName, StringComparison.Ordinal) > 0))
                        {
                            bestName = name;
                            _downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                            _expectedSize = asset.GetProperty("size").GetInt64();
                            _expectedSha256 = string.Empty;

                            if (asset.TryGetProperty("digest", out var digestProp))
                            {
                                var digest = digestProp.GetString() ?? string.Empty;
                                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                {
                                    _expectedSha256 = digest["sha256:".Length..].Trim();
                                }
                                else
                                {
                                    _expectedSha256 = digest.Trim();
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(_downloadUrl))
                {
                    _log.Warning("该源版本包中没有目标文件，换下一个源");
                    continue;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "该源查询异常，换下一个源");
                continue;
            }
            }
            _log.Error("所有版本查询源均失败，请检查网络或更换镜像源后重试");
            return false;
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, long expectedSize)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = File.Create(destinationPath))
            {
                await using var downloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await downloadStream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            var fileInfo = new FileInfo(destinationPath);
            if (expectedSize > 0 && fileInfo.Length != expectedSize)
            {
                if (fileInfo.Exists) fileInfo.Delete();
                throw new InvalidDataException($"下载文件大小不对。应为：{expectedSize} 字节，实际：{fileInfo.Length} 字节");
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private async Task SafeExtractAndDeploy(string archivePath)
        {
            await Task.Run(() =>
            {
                var stagingDir = Path.Combine(Config.Config.TempPath, "Goldberg_Staging_" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, true);
                    Directory.CreateDirectory(stagingDir);

                    using (var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath))
                    {
                        archive.WriteToDirectory(stagingDir, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }

                    var sourceDir = Directory.Exists(Path.Combine(stagingDir, "release"))
                        ? Path.Combine(stagingDir, "release")
                        : stagingDir;

                    var requiredFiles = new[]
                    {
                        Path.Combine(sourceDir, "regular", "x64", "steam_api64.dll"),
                        Path.Combine(sourceDir, "regular", "x86", "steam_api.dll")
                    };

                    foreach (var file in requiredFiles)
                    {
                        if (!File.Exists(file))
                        {
                            // 解压没报错但文件缺失：多半是包结构变了，或杀软把刚落盘的
                            // steam_api*.dll 直接隔离了；把现场列出来，方便定位是哪一种
                            var landed = Directory.Exists(stagingDir)
                                ? string.Join("；", Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                                    .Select(f => Path.GetRelativePath(stagingDir, f)).Take(20))
                                : "（暂存目录已不存在）";
                            throw new FileNotFoundException($"Verification failed: essential emulator file '{Path.GetFileName(file)}' not found in extracted archive. 已解压出：{landed}。若列表里有该文件，检查杀毒软件隔离区（Defender 常把模拟器 DLL 当威胁直接吃掉）。");
                        }
                    }

                    if (!Directory.Exists(Config.Config.GoldbergPath))
                        Directory.CreateDirectory(Config.Config.GoldbergPath);

                    CopyDirectory(new DirectoryInfo(sourceDir), new DirectoryInfo(Config.Config.GoldbergPath));
                }
                finally
                {
                    if (Directory.Exists(stagingDir))
                    {
                        try { Directory.Delete(stagingDir, true); } catch { /* ignore */ }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
        {
            Directory.CreateDirectory(target.FullName);

            foreach (var fi in source.GetFiles())
            {
                fi.CopyTo(Path.Combine(target.FullName, fi.Name), true);
            }

            foreach (var diSourceSubDir in source.GetDirectories())
            {
                if (diSourceSubDir.Name.Equals("release", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
                CopyDirectory(diSourceSubDir, nextTargetSubDir);
            }
        }

        private string GetCurrentGoldbergVersion()
        {
            try
            {
                var path = Path.Combine(Config.Config.GoldbergPath, "version");
                if (!File.Exists(path))
                {
                    path = Path.Combine(Config.Config.GoldbergPath, "commit_id");
                }
                return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() ?? string.Empty : string.Empty;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "读取本地模拟器版本失败");
                return string.Empty;
            }
        }
    }
}
