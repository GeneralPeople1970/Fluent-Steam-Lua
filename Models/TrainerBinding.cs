using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class TrainerBinding : ObservableObject
{
    public string GameName { get; set; } = string.Empty;
    public string GameExePath { get; set; } = string.Empty;
    public string TrainerFilePath { get; set; } = string.Empty;
    public string TrainerDisplayName { get; set; } = string.Empty;
    [ObservableProperty]
    private bool _isEnabled = true;

    [JsonIgnore]
    public string TrainerFileName => Path.GetFileName(TrainerFilePath);

    [JsonIgnore]
    public bool HasAutoKeys => AutoKeys.Count > 0;

    [JsonIgnore]
    public string AutoKeysSummary
    {
        get
        {
            if (AutoKeys.Count == 0) return string.Empty;
            var descs = AutoKeys.Select(k =>
            {
                var idx = k.LastIndexOf(" - ", StringComparison.Ordinal);
                return idx > 0 ? k[(idx + 3)..] : k;
            });
            return "已自动激活的功能：" + string.Join("、", descs);
        }
    }

    public List<string> AutoKeys { get; set; } = new();
}
