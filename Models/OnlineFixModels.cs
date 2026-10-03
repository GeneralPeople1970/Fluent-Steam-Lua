namespace SteamLuaManager.Models;

// OnlineFix 补丁搜索条目：online-fix.me 搜索页解析产物
public sealed record OnlineFixSearchResult(
    string Title,
    string Url,
    string? VersionText,
    string? Tablet,
    string? UpdatedText);

// OnlineFix 补丁文件条目：uploads 目录索引解析产物
public sealed record OnlineFixFileEntry(
    string Name,
    string Url,
    string? SizeText,
    string? DateText);

// OnlineFix 补丁详情：文章页解析产物；Files 只收 Fix Repair 目录（补丁小文件）
public sealed record OnlineFixDetail(
    string Title,
    string ArticleUrl,
    string GameDirName,
    string UploadsBaseUrl,
    string? VersionText,
    string? Tablet,
    string? UpdatedText,
    string Instructions,
    IReadOnlyList<OnlineFixFileEntry> Files);
