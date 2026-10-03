using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class LuaFileManager : ILuaFileManager, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private FileSystemWatcher? _watcher;
    private bool _isWatching;
    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();

    private static readonly Regex AddAppIdRegex = new(@"addappid\((\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddDepotRegex = new(@"addappid\((\d+),\s*(\d+),\s*""([^""]+)""\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddTokenRegex = new(@"addtoken\((\d+),\s*""([^""]+)""\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestPinRegex = new(@"^\s*setManifestid\((\d+),\s*""(\d+)""(?:\s*,\s*(\d+))?\)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ManifestPinCommentedRegex = new(@"^\s*--\s*setManifestid\((\d+),\s*""(\d+)""(?:\s*,\s*(\d+))?\)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public event EventHandler? FilesChanged;

    // 解析模板缓存：未变文件跳过读盘+正则；模板只存解析产物，深拷贝后装配新实例，
    // 与现扫结果逐字段一致（Token/Depot/BareAppIds/IsManifestPinned）
    private sealed class CachedLuaParse
    {
        public DateTime Mtime;
        public long Length;
        public string Token = string.Empty;
        public bool IsManifestPinned;
        public List<DepotInfo> Depots = new();
        public List<int> BareAppIds = new();
    }

    private readonly Dictionary<string, CachedLuaParse> _parseCache = new(StringComparer.OrdinalIgnoreCase);

    private static DepotInfo CopyDepot(DepotInfo d) => new()
    {
        DepotId = d.DepotId, Key = d.Key, ManifestId = d.ManifestId, IsPinned = d.IsPinned
    };

    private GameInfo GetOrParseLuaGame(string file, int appId, bool isDisabled, HashSet<string> seen)
    {
        seen.Add(file);
        var fi = new FileInfo(file);
        var mtime = fi.Exists ? fi.LastWriteTime : DateTime.MinValue;
        var length = fi.Exists ? fi.Length : -1;
        CachedLuaParse? template = null;
        lock (_parseCache)
        {
            if (fi.Exists && _parseCache.TryGetValue(file, out var cached)
                && cached.Mtime == mtime && cached.Length == length)
                template = cached;
        }
        var game = new GameInfo
        {
            AppId = appId,
            LuaFilePath = file,
            LuaFileTime = mtime,
            IsDisabled = isDisabled
        };
        if (template != null)
        {
            game.Token = template.Token;
            game.IsManifestPinned = template.IsManifestPinned;
            game.Depots = new ObservableCollection<DepotInfo>(template.Depots.Select(CopyDepot));
            game.BareAppIds = new List<int>(template.BareAppIds);
            return game;
        }
        ParseLuaContent(game);
        lock (_parseCache)
        {
            _parseCache[file] = new CachedLuaParse
            {
                Mtime = mtime,
                Length = length,
                Token = game.Token,
                IsManifestPinned = game.IsManifestPinned,
                Depots = game.Depots.Select(CopyDepot).ToList(),
                BareAppIds = new List<int>(game.BareAppIds)
            };
        }
        return game;
    }

    public LuaFileManager(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
    }

    public async Task<List<GameInfo>> ScanLuaFilesAsync()
    {
        return await Task.Run(() =>
        {
            var result = new List<GameInfo>();
            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
                return result;

            var luaFiles = Directory.GetFiles(luaFolder, "*.lua");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in luaFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (int.TryParse(fileName, out var appId))
                    result.Add(GetOrParseLuaGame(file, appId, false, seen));
            }

            var disableFolder = Path.Combine(luaFolder, "Disable");
            if (Directory.Exists(disableFolder))
            {
                var disabledFiles = Directory.GetFiles(disableFolder, "*.lua");
                var seenAppIds = new HashSet<int>();
                foreach (var file in disabledFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (int.TryParse(fileName, out var appId))
                    {
                        if (seenAppIds.Add(appId) == false) continue;

                        result.Add(GetOrParseLuaGame(file, appId, true, seen));
                    }
                }
            }

            lock (_parseCache)
            {
                foreach (var dead in _parseCache.Keys.Where(k => !seen.Contains(k)).ToList())
                    _parseCache.Remove(dead);
            }

            return result;
        });
    }

    public async Task<GameInfo?> ParseLuaFileAsync(int appId)
    {
        return await Task.Run(() =>
        {
            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            var filePath = Path.Combine(luaFolder, $"{appId}.lua");
            if (!File.Exists(filePath)) return null;

            var game = new GameInfo
            {
                AppId = appId,
                LuaFilePath = filePath,
                LuaFileTime = File.GetLastWriteTime(filePath)
            };
            ParseLuaContent(game);
            return game;
        });
    }

    private static void ParseLuaContent(GameInfo game)
    {
        if (!File.Exists(game.LuaFilePath)) return;

        var content = File.ReadAllText(game.LuaFilePath);
        game.Depots.Clear();

        // Parse addtoken
        var tokenMatch = AddTokenRegex.Match(content);
        if (tokenMatch.Success)
            game.Token = tokenMatch.Groups[2].Value;

        // Parse depots from addappid(depotId, flag, "key")
        var depotMatches = AddDepotRegex.Matches(content);
        foreach (Match match in depotMatches)
        {
            // 恶意或损坏文件的超长数字会让 Parse 抛异常拖垮整批扫描，坏条目只跳过自己
            if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var depotId))
                continue;
            var key = match.Groups[3].Value;
            game.Depots.Add(new DepotInfo { DepotId = depotId, Key = key });
        }

        // Parse active manifest pins
        var activePins = new Dictionary<int, string>();
        var activeMatches = ManifestPinRegex.Matches(content);
        foreach (Match match in activeMatches)
        {
            if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var depotId))
                continue;
            activePins[depotId] = match.Groups[2].Value;
        }

        // Parse commented manifest pins
        var commentedPins = new Dictionary<int, string>();
        var commentedMatches = ManifestPinCommentedRegex.Matches(content);
        foreach (Match match in commentedMatches)
        {
            if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var depotId))
                continue;
            commentedPins[depotId] = match.Groups[2].Value;
        }

        // Merge into depots
        var allDepotIds = game.Depots.Select(d => d.DepotId)
            .Union(activePins.Keys)
            .Union(commentedPins.Keys)
            .Distinct()
            .ToList();

        foreach (var depotId in allDepotIds)
        {
            var existing = game.Depots.FirstOrDefault(d => d.DepotId == depotId);
            if (existing != null)
            {
                if (activePins.TryGetValue(depotId, out var activeId))
                {
                    existing.ManifestId = activeId;
                    existing.IsPinned = true;
                }
                else if (commentedPins.TryGetValue(depotId, out var commentedId))
                {
                    existing.ManifestId = commentedId;
                    existing.IsPinned = false;
                }
            }
            else
            {
                string manifestId = "";
                bool isPinned = false;
                if (activePins.TryGetValue(depotId, out var aid)) { manifestId = aid; isPinned = true; }
                else if (commentedPins.TryGetValue(depotId, out var cid)) { manifestId = cid; }
                game.Depots.Add(new DepotInfo { DepotId = depotId, Key = "", ManifestId = manifestId, IsPinned = isPinned });
            }
        }

        // Parse bare addappid(id) lines (pure entitlement DLC etc.), excluding keyed ones
        game.BareAppIds.Clear();
        var keyedIds = new HashSet<int>(game.Depots.Select(d => d.DepotId));
        foreach (Match match in AddAppIdRegex.Matches(content))
        {
            if (int.TryParse(match.Groups[1].Value, out var bareId)
                && !keyedIds.Contains(bareId)
                && !game.BareAppIds.Contains(bareId))
                game.BareAppIds.Add(bareId);
        }

        game.IsManifestPinned = activePins.Count > 0;
    }

    public async Task SetManifestPinAsync(int appId, bool pin, Dictionary<int, string>? manifestIds = null)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var filePath = Path.Combine(luaFolder, $"{appId}.lua");
        if (!File.Exists(filePath)) return;

        var content = await File.ReadAllTextAsync(filePath);
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        if (pin && manifestIds != null)
        {
            foreach (var kvp in manifestIds)
            {
                var depotId = kvp.Key;
                var newManifestId = kvp.Value;
                var targetLine = $"setManifestid({depotId},\"{newManifestId}\",0)";
                UpdateManifestLine(lines, depotId, targetLine);
            }
        }
        else if (!pin)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var match = ManifestPinRegex.Match(lines[i]);
                if (match.Success && !lines[i].TrimStart().StartsWith("--"))
                {
                    lines[i] = "--" + lines[i].TrimStart();
                }
            }
        }

        await File.WriteAllTextAsync(filePath, string.Join("\n", lines));
    }

    private static void UpdateManifestLine(List<string> lines, int depotId, string newLine)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var active = ManifestPinRegex.Match(lines[i]);
            var commented = ManifestPinCommentedRegex.Match(lines[i]);

            if (active.Success && int.TryParse(active.Groups[1].Value, CultureInfo.InvariantCulture, out var activeId) && activeId == depotId)
            {
                lines[i] = newLine;
                return;
            }
            if (commented.Success && int.TryParse(commented.Groups[1].Value, CultureInfo.InvariantCulture, out var commentedId) && commentedId == depotId)
            {
                lines[i] = newLine;
                return;
            }
        }

        // Not found - append after last addappid or addtoken line
        var insertAt = lines.Count - 1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (AddDepotRegex.IsMatch(lines[i]) || AddAppIdRegex.IsMatch(lines[i]) || AddTokenRegex.IsMatch(lines[i]))
            {
                insertAt = i + 1;
                break;
            }
        }
        lines.Insert(insertAt, newLine);
    }

    public async Task AddLuaFileAsync(string sourceFilePath)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(luaFolder, fileName);

        await Task.Run(() => File.Copy(sourceFilePath, destPath, true));
    }

    public async Task AddBinFileAsync(string sourceFilePath)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return;

        var statsDir = Path.Combine(steamPath, "appcache", "stats");
        Directory.CreateDirectory(statsDir);
        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(statsDir, fileName);

        await Task.Run(() => File.Copy(sourceFilePath, destPath, true));
    }

    public async Task<string?> AddManifestFileAsync(string sourceFilePath)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return null;

        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        Directory.CreateDirectory(depotCacheDir);
        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(depotCacheDir, fileName);

        await Task.Run(() => File.Copy(sourceFilePath, destPath, true));
        return destPath;
    }

    /// <summary>删除 lua 中指定 id 的 addappid 行（裸行/带密钥行）及其 setManifestid 行（含注释掉的）。</summary>
    public async Task RemoveAppIdsFromLuaAsync(int appId, IEnumerable<int> depotIds)
    {
        var ids = new HashSet<int>(depotIds);
        if (ids.Count == 0) return;

        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var filePath = Path.Combine(luaFolder, $"{appId}.lua");
        if (!File.Exists(filePath)) return;

        var content = await File.ReadAllTextAsync(filePath);
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var kept = new List<string>(lines.Count);

        foreach (var line in lines)
        {
            var depotMatch = AddDepotRegex.Match(line);
            var appMatch = depotMatch.Success ? null : AddAppIdRegex.Match(line);
            int? lineId = null;
            if (depotMatch.Success && int.TryParse(depotMatch.Groups[1].Value, out var did)) lineId = did;
            else if (appMatch != null && appMatch.Success && int.TryParse(appMatch.Groups[1].Value, out var aid)) lineId = aid;
            if (lineId is int x && ids.Contains(x)) continue;

            var pinMatch = ManifestPinRegex.Match(line);
            var commentedMatch = pinMatch.Success ? null : ManifestPinCommentedRegex.Match(line);
            var pinIdStr = pinMatch.Success ? pinMatch.Groups[1].Value
                : commentedMatch != null && commentedMatch.Success ? commentedMatch.Groups[1].Value : null;
            if (pinIdStr != null && int.TryParse(pinIdStr, out var pid) && ids.Contains(pid)) continue;

            kept.Add(line);
        }

        await File.WriteAllTextAsync(filePath, string.Join("\n", kept));
    }

    public async Task DeleteLuaFileAsync(int appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var filePath = Path.Combine(luaFolder, $"{appId}.lua");
        if (File.Exists(filePath))
        {
            await Task.Run(() => File.Delete(filePath));
        }
    }

    public async Task DisableGameAsync(int appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var srcPath = Path.Combine(luaFolder, $"{appId}.lua");
        if (!File.Exists(srcPath)) return;

        var disableFolder = Path.Combine(luaFolder, "Disable");
        Directory.CreateDirectory(disableFolder);
        var destPath = Path.Combine(disableFolder, $"{appId}.lua");

        await Task.Run(() =>
        {
            // 同盘一步覆盖移动：先删后移中间崩溃会丢档
            File.Move(srcPath, destPath, true);
        });
    }

    public async Task EnableGameAsync(int appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var disableFolder = Path.Combine(luaFolder, "Disable");
        var srcPath = Path.Combine(disableFolder, $"{appId}.lua");
        if (!File.Exists(srcPath)) return;

        var destPath = Path.Combine(luaFolder, $"{appId}.lua");

        await Task.Run(() =>
        {
            // 同盘一步覆盖移动：先删后移中间崩溃会丢档
            File.Move(srcPath, destPath, true);
        });
    }

    public void StartWatching()
    {
        if (_isWatching) return;

        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder)) return;

        _watcher = new FileSystemWatcher(luaFolder, "*.lua")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += OnFilesChanged;
        _watcher.Changed += OnFilesChanged;
        _watcher.Deleted += OnFilesChanged;
        _watcher.Renamed += OnFilesChanged;
        _watcher.EnableRaisingEvents = true;
        _isWatching = true;
    }

    public void StopWatching()
    {
        if (!_isWatching || _watcher == null) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
        _isWatching = false;
    }

    private void OnFilesChanged(object sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            // 文件监听回调跑在池线程，burst 导入时并发进这里；
            // 旧 CTS 只 Cancel 不 Dispose：已释放 CTS 的 Token 会抛 ODE，而下面只抓 OCE
            try { _debounceCts?.Cancel(); } catch { }
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                    FilesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void Dispose()
    {
        StopWatching();
        lock (_debounceLock)
        {
            try { _debounceCts?.Cancel(); } catch { }
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        GC.SuppressFinalize(this);
    }
}
