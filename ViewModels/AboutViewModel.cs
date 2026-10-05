using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamLuaManager.Models;

namespace SteamLuaManager.ViewModels;

// 常见问题窗口的数据：问题列表 + 关键字过滤；VM 无外部依赖，由窗口直接持有
public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchText = string.Empty;

    public ObservableCollection<FaqItem> FilteredFaqs { get; } = new();

    private readonly List<FaqItem> _allFaqs = new()
    {
        new("游戏下载无互联网连接",
            "1.优先查看软件设置-内核设置-上游清单源是否为可用状态，切换为可用源后再重试下载(最好卸载之前下载失败的游戏缓存状态)\n2.如上述测试的所有清单源皆为不可用，则再使用清单监听作为备用方案"),
        new("D加密游戏报错",
            "使用Denuvo加密的游戏无法直接入库后游玩，需要使用有正版游戏的账号进行授权提取，然后你再拿授权文件进行授权后才能玩，或者直接找破解补丁也行\n注意:D加密授权后，不能进行游戏更新，系统更新，驱动更新等操作，会导致D加密授权失效(绑定系统特征码和游戏版本的）"),
        new("内核不兼容",
            "启动软件时提示内核冲突，请回忆你是否有用过同类工具，他可能会给你安装相似的内核\n你可以在软件右下角的工具内内净化环境进行常见内核清除，部分魔改内核无法识别，需自行手动定位删除\n如依旧出现入库游戏后steam不显示的情况，首先确定你是否重启steam，如依旧，则说明内核没清楚干净，可酌情重装steam"),
        new("入库游戏后，steam库里没有",
            "1.内核安装完成后需要重启steam，内核才会生效，否则即使你软件内显示入库成功，也会因为内核没有hook而不显示\n2.其次就是启动steam后，偶尔入库的游戏会消失，这是因为网络问题导致没连上或者超时连上的ost的上游签名验证，导致hook被推迟，所以会短暂不显示游戏\n3.还有就是你装过多种入库内核，导致冲突不生效，需要清理之前安装的内核（可见 内核不兼容 栏）"),
        new("云存档报错",
            "云存档报错为正常现象，目前清单入库的游戏无法使用steam官方云存档服务\n如想消除报错，两个方法\n1.如果仅想消除报错，不打算使用存档备份功能，可直接 右键游戏-属性-关闭云存档 即可\n2.使用软件内的云存档重定向功能，将云存档重定向到本地或云盘进行储存以修复报错"),
        new("云存档开启后没效果",
            "先确认电脑装了VC++运行库（云存档页顶部有提示）,再确认Steam已重启 (DLL只在启动时读配置）"),
        new("入库Wallpaper不显示",
            "Wallpaper为软件类，需要在steam的入库页面顶部筛选信息里勾选显示软件类，才会显示出wallpaper"),
        new("入库的游戏是否支持联机",
            "纯网游或全程联网的游戏不行，如果只是单和好友联机的那种双人和多人游戏的大厅匹配游戏，可以 右键游戏-通用-启动选项参数  内 填入  -onlinefix   来使用联机功能(需双方都是用同方法)\n或者使用联机补丁进行联机功能解锁\n具体可以使用软件内的联机功能区进行了解"),
        new("是否支持创意工坊",
            "支持，但是需要上游清单源可正常工作(也可以清单监听解决)，并且有创意工坊密钥，即可正常订阅下载"),
        new("提取已拥有游戏前进行登录时卡住",
            "账号登录为steam官方接口，国内裸连偶尔会很慢，建议开启代理或梯子后重试"),
        new("联机补丁下载慢或失败",
            "补丁源站在境外，刚需代理或梯子；压缩包解压密码是online-fix.me；注意核对补丁兼容的游戏版本"),
        new("游戏入库卡在下载/更新密钥缓存",
            "密钥缓存为一个接近20mb的json文件，如出现下载慢或卡住可考虑切换网络环境或开启梯子或代理后重启软件重试"),
        new("不想刮削游戏封面",
            "可在软件内的显示设置力关闭封面刮削即可"),
        new("开启修改器绑定游戏后修改器不自启动",
            "去修改器页安装后台服务，并保持对应绑定的启用开关打开"),
    };

    public AboutViewModel()
    {
        RefreshFilteredFaqs();
    }

    partial void OnSearchTextChanged(string value) => RefreshFilteredFaqs();

    private void RefreshFilteredFaqs()
    {
        FilteredFaqs.Clear();
        var q = SearchText?.Trim();
        foreach (var item in _allFaqs)
        {
            if (string.IsNullOrEmpty(q)
                || item.Question.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Answer.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredFaqs.Add(item);
        }
    }
}
