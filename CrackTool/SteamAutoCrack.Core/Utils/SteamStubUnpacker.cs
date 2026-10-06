using System.ComponentModel;
using System.Text.Json;
using Serilog;
using Steamless.API.Model;
using Steamless.API.PE32;
using Steamless.API.Services;
using Steamless.Unpacker.Variant10.x86;

namespace SteamAutoCrack.Core.Utils;

public class SteamStubUnpackerConfig
{
    public enum SteamAPICheckBypassDLLs
    {
        [Description("winmm.dll")] WINMM_DLL,
        [Description("version.dll")] VERSION_DLL,
        [Description("winhttp.dll")] WINHTTP_DLL
    }

    public enum SteamAPICheckBypassModes
    {
        [Description("Disabled")] Disabled,
        [Description("Enable All Time")] All,
        [Description("Enable Only Nth Time")] OnlyN,

        [Description("Enable Only Not Nth Time")]
        OnlyNotN
    }

    /// <summary>
    ///     Keeps the .bind section in the unpacked file.
    /// </summary>
    public bool KeepBind { get; set; } = true;

    /// <summary>
    ///     Keeps the DOS stub in the unpacked file.
    /// </summary>
    public bool KeepStub { get; set; } = false;

    /// <summary>
    ///     Realigns the unpacked file sections.
    /// </summary>
    public bool Realign { get; set; } = false;

    /// <summary>
    ///     Recalculates the unpacked file checksum.
    /// </summary>
    public bool ReCalcChecksum { get; set; } = false;

    /// <summary>
    ///     Use Experimental Features.
    /// </summary>
    public bool UseExperimentalFeatures { get; set; } = false;

    /// <summary>
    ///     Temp steam_settings folder path
    /// </summary>
    public string SteamsettingsPath { get; set; } = Path.Combine(Config.Config.TempPath, "steam_settings");

    /// <summary>
    ///     SteamAPICheckBypass Mode
    /// </summary>
    public SteamAPICheckBypassModes SteamAPICheckBypassMode { get; set; } = SteamAPICheckBypassModes.Disabled;

    /// <summary>
    ///     DLL hijacking name for SteamAPICheckBypass
    /// </summary>
    public SteamAPICheckBypassDLLs SteamAPICheckBypassDLL { get; set; } = SteamAPICheckBypassDLLs.WINMM_DLL;

    /// <summary>
    ///     SteamAPI Check Bypass Nth Time Setting
    /// </summary>
    public List<ulong> SteamAPICheckBypassNthTime { get; set; } = new() { 1 };

    public static class DefaultConfig
    {
        /// <summary>
        ///     Keeps the .bind section in the unpacked file.
        /// </summary>
        public static readonly bool KeepBind = true;

        /// <summary>
        ///     Keeps the DOS stub in the unpacked file.
        /// </summary>
        public static readonly bool KeepStub = false;

        /// <summary>
        ///     Realigns the unpacked file sections.
        /// </summary>
        public static readonly bool Realign = false;

        /// <summary>
        ///     Recalculates the unpacked file checksum.
        /// </summary>
        public static readonly bool ReCalcChecksum = false;

        /// <summary>
        ///     Use Experimental Features.
        /// </summary>
        public static readonly bool UseExperimentalFeatures = false;

        /// <summary>
        ///     Temp steam_settings folder path
        /// </summary>
        public static readonly string SteamsettingsPath = Path.Combine(Config.Config.TempPath, "steam_settings");

        /// <summary>
        ///     SteamAPICheckBypass Mode
        /// </summary>
        public static readonly SteamAPICheckBypassModes SteamAPICheckBypassMode = SteamAPICheckBypassModes.Disabled;

        /// <summary>
        ///     DLL hijacking name for SteamAPICheckBypass
        /// </summary>
        public static readonly SteamAPICheckBypassDLLs SteamAPICheckBypassDLL = SteamAPICheckBypassDLLs.WINMM_DLL;

        /// <summary>
        ///     SteamAPI Check Bypass Nth Time Setting
        /// </summary>
        public static readonly List<ulong> SteamAPICheckBypassNthTime = new() { 1 };
    }
}

public interface ISteamStubUnpacker
{
    public Task<bool> Unpack(string path);
}

public class SteamStubUnpacker : ISteamStubUnpacker
{
    private readonly ILogger _log;
    private readonly LoggingService steamlessLoggingService = new();
    private readonly SteamlessOptions steamlessOptions;
    private readonly List<SteamlessPlugin> steamlessPlugins = new();

    public SteamStubUnpacker(SteamStubUnpackerConfig SteamStubUnpackerConfig)
    {
        _log = Log.ForContext<SteamStubUnpacker>();
        steamlessOptions = new SteamlessOptions
        {
            KeepBindSection = SteamStubUnpackerConfig.KeepBind,
            ZeroDosStubData = !SteamStubUnpackerConfig.KeepStub,
            DontRealignSections = !SteamStubUnpackerConfig.Realign,
            RecalculateFileChecksum = SteamStubUnpackerConfig.ReCalcChecksum,
            UseExperimentalFeatures = SteamStubUnpackerConfig.UseExperimentalFeatures
        };
        _SteamAPICheckBypassMode = SteamStubUnpackerConfig.SteamAPICheckBypassMode;
        _SteamAPICheckBypassDLL = SteamStubUnpackerConfig.SteamAPICheckBypassDLL;
        _SteamAPICheckBypassNthTime = SteamStubUnpackerConfig.SteamAPICheckBypassNthTime;
        steamlessLoggingService.AddLogMessage += (sender, e) =>
        {
            try
            {
                Log.ForContext("SourceContext", sender?.GetType().Assembly.GetName().Name?.Replace(".", ""))
                    .Debug(e.Message);
            }
            catch
            {
            }
        };
        GetSteamlessPlugins();
    }

    private SteamStubUnpackerConfig.SteamAPICheckBypassModes _SteamAPICheckBypassMode { get; }
    private SteamStubUnpackerConfig.SteamAPICheckBypassDLLs _SteamAPICheckBypassDLL { get; }
    private List<ulong> _SteamAPICheckBypassNthTime { get; }

    public async Task<bool> Unpack(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            {
                _log.Error("路径无效");
                return false;
            }

            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                await UnpackFolder(path);
            else
                await UnpackFile(path);

            if (_SteamAPICheckBypassMode != SteamStubUnpackerConfig.SteamAPICheckBypassModes.Disabled)
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                    ApplySteamAPICheckBypass(path, true);
                else
                    ApplySteamAPICheckBypass(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "脱壳失败");
            return false;
        }
    }

    private void GetSteamlessPlugins()
    {
        try
        {
            var existsteamlessPlugins = new List<SteamlessPlugin>
            {
                new Main(),
                new Steamless.Unpacker.Variant20.x86.Main(),
                new Steamless.Unpacker.Variant21.x86.Main(),
                new Steamless.Unpacker.Variant30.x86.Main(),
                new Steamless.Unpacker.Variant30.x64.Main(),
                new Steamless.Unpacker.Variant31.x86.Main(),
                new Steamless.Unpacker.Variant31.x64.Main()
            };
            foreach (var plugin in existsteamlessPlugins)
            {
                if (!plugin.Initialize(steamlessLoggingService))
                {
                    _log.Error($"插件加载失败：插件初始化失败 ({plugin.Name})");
                    continue;
                }

                steamlessPlugins.Add(plugin);
            }
        }
        catch
        {
            _log.Error("插件加载失败");
        }
    }

    private async Task UnpackFolder(string path)
    {
        try
        {
            _log.Information("正在脱壳目录 \"{path}\" 下的所有文件…", path);
            foreach (var exepath in Directory.EnumerateFiles(path, "*.exe", SearchOption.AllDirectories))
                await UnpackFile(exepath);
            _log.Information("目录 \"{path}\" 下所有文件已处理", path);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "脱壳目录 \"{path}\" 失败", path);
        }
    }

    private async Task UnpackFile(string path)
    {
        try
        {
            var bSuccess = false;
            var bError = false;
            _log.Information("正在脱壳文件 \"{path}\"…", path);
            foreach (var p in steamlessPlugins)
                if (p.CanProcessFile(path))
                {
                    GC.Collect();
                    if (await Task.Run(() => p.ProcessFile(path, steamlessOptions)))
                    {
                        bSuccess = true;
                        bError = false;
                        _log.Information("文件 \"{path}\" 脱壳成功", path);
                        if (File.Exists(Path.ChangeExtension(path, ".exe.bak")))
                        {
                            _log.Debug("Backup file already exists, skipping backup process...");
                            File.Delete(path);
                        }
                        else
                        {
                            File.Move(path, Path.ChangeExtension(path, ".exe.bak"));
                        }

                        File.Move(Path.ChangeExtension(path, ".exe.unpacked.exe"), path);
                    }
                    else
                    {
                        bError = true;
                        _log.Warning("文件 \"{path}\" 无需脱壳（未加壳或其他保护）",
                            path);
                    }
                }

            if (!bSuccess && !bError)
                _log.Warning("文件 \"{path}\" 无需脱壳（未加壳或其他保护）", path);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "脱壳或备份文件 \"{path}\" 失败", path);
            throw new Exception($"Failed to unpack or backup File \"{path}\".");
        }
    }

    private void ApplySteamAPICheckBypass(string path, bool folder = false)
    {
        try
        {
            var dllPaths = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")?.ToString();

            var pathsList = new List<string>(dllPaths?.Split(';') ?? Array.Empty<string>());
            var dllPath = "";
            var dll = "";
            var targetDllName = _SteamAPICheckBypassDLL switch
            {
                SteamStubUnpackerConfig.SteamAPICheckBypassDLLs.WINMM_DLL => "winmm.dll",
                SteamStubUnpackerConfig.SteamAPICheckBypassDLLs.VERSION_DLL => "version.dll",
                SteamStubUnpackerConfig.SteamAPICheckBypassDLLs.WINHTTP_DLL => "winhttp.dll",
                _ => "winmm.dll"
            };
            var bypassDllNames = new List<string>
            {
                "winmm.dll",
                "version.dll",
                "winhttp.dll"
            };
            var steamsettingsFiles = new List<string>
            {
                Path.Combine("steam_settings", "achievements.json"),
                Path.Combine("steam_settings", "branches.json"),
                Path.Combine("steam_settings", "configs.app.ini"),
                Path.Combine("steam_settings", "configs.main.ini"),
                Path.Combine("steam_settings", "configs.overlay.ini"),
                Path.Combine("steam_settings", "configs.user.ini"),
                Path.Combine("steam_settings", "default_items.json"),
                Path.Combine("steam_settings", "items.json"),
                Path.Combine("steam_settings", "stats.txt"),
                Path.Combine("steam_settings", "steam_appid.txt"),
                Path.Combine("steam_settings", "supported_languages.txt"),
                Path.Combine("steam_settings", "achievement_images")
            };

            var mode = _SteamAPICheckBypassMode switch
            {
                SteamStubUnpackerConfig.SteamAPICheckBypassModes.OnlyN => "nth_time_only",
                SteamStubUnpackerConfig.SteamAPICheckBypassModes.OnlyNotN => "not_nth_time_only",
                SteamStubUnpackerConfig.SteamAPICheckBypassModes.All => "all",
                _ => throw new InvalidOperationException("绕过模式无效")
            };

            foreach (var dirPath in pathsList.AsReadOnly())
            {
                var fullPath = Path.Combine(dirPath, "SteamAPICheckBypass");
                if (Directory.Exists(fullPath))
                {
                    dllPath = fullPath;
                    break;
                }
            }

            var files = new List<string>();

            if (folder)
                files = Directory.GetFiles(path, "*.exe", SearchOption.AllDirectories).ToList<string>();
            else
                files.Add(path);

            foreach (var file in files)
            {
                var skipFile = false;
                foreach (var bypassDllName in bypassDllNames)
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, bypassDllName)))
                    {
                        _log.Information("绕过组件已存在，跳过");
                        skipFile = true;
                    }

                if (skipFile)
                    continue;

                var f = new Pe32File(file);
                f.Parse();
                if (!f.IsFile64Bit())
                    dll = Path.Combine(dllPath, "SteamAPICheckBypass_x32.dll");
                else
                    dll = Path.Combine(dllPath, "SteamAPICheckBypass.dll");
                File.Copy(dll, Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, targetDllName));
                var jsonContent = new Dictionary<string, object>();
                if (File.Exists(Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, "SteamAPICheckBypass.json")))
                {
                    var oldjsonString =
                        File.ReadAllText(Path.Combine(Path.GetDirectoryName(file) ?? string.Empty,
                            "SteamAPICheckBypass.json"));
                    jsonContent = JsonSerializer.Deserialize<Dictionary<string, object>>(oldjsonString);
                }

                jsonContent![Path.GetFileName(file)] = new
                {
                    mode = "file_redirect",
                    to = Path.GetFileName(file) + ".bak",
                    file_must_exist = true
                };

                string filepath;

                if (folder)
                    filepath = path;
                else
                    filepath = Path.GetDirectoryName(file) ?? string.Empty;

                var apidlls = Directory.GetFiles(filepath, "steam_api.dll",
                        SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(Path.GetDirectoryName(file) ?? string.Empty, p)).ToArray();
                apidlls = apidlls.Concat(Directory
                    .GetFiles(filepath, "steam_api64.dll", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(Path.GetDirectoryName(file) ?? string.Empty, p))).ToArray();
                var steamsettingsPaths = apidlls
                    .Select(p => Path.Combine(Path.GetDirectoryName(p) ?? string.Empty, "steam_settings"));
                var steamsettingsFilePaths = apidlls
                    .SelectMany(p =>
                        steamsettingsFiles.Select(f => Path.Combine(Path.GetDirectoryName(p) ?? string.Empty, f)))
                    .Distinct();
                foreach (var steamsettingsPath in steamsettingsPaths)
                    jsonContent[steamsettingsPath] = new
                    {
                        mode = "file_hide"
                    };

                foreach (var steamsettingsFilePath in steamsettingsFilePaths)
                    jsonContent[steamsettingsFilePath] = new
                    {
                        mode = "file_hide",
                        hook_times_mode = "not_nth_time_only",
                        hook_time_n =
                            "1" // This value is estimated the game checks the config file after the emulator read it
                    };

                foreach (var apiDllPath in apidlls)
                    if (_SteamAPICheckBypassMode == SteamStubUnpackerConfig.SteamAPICheckBypassModes.All)
                        jsonContent[apiDllPath] = new
                        {
                            mode = "file_redirect",
                            to = apiDllPath + ".bak",
                            file_must_exist = true
                        };
                    else
                        jsonContent[apiDllPath] = new
                        {
                            mode = "file_redirect",
                            to = apiDllPath + ".bak",
                            file_must_exist = true,
                            hook_times_mode = mode,
                            hook_time_n = _SteamAPICheckBypassNthTime
                        };

                var jsonString = JsonSerializer.Serialize(jsonContent,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, "SteamAPICheckBypass.json"),
                    jsonString);
            }

            _log.Information("绕过组件已应用");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "应用绕过组件失败");
            throw new Exception("应用绕过组件失败");
        }
    }
}