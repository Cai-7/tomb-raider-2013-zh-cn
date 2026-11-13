using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading;
using TrRebootTools.Shared;
using TrRebootTools.Shared.Cdc;
using TrRebootTools.ModManager.Mod;

// 古墓丽影9（Tomb Raider 2013）文本工具
// 依赖 TrRebootModTools：https://github.com/arcusmaximus/TrRebootModTools
//
//   list    <游戏目录>                       列出各语言的 locals.bin
//   dump    <游戏目录> <输出目录> [语言]      导出文本为 JSON（默认全部语言）
//   install <游戏目录> <mod目录>             安装 mod 并合并进 patch2.000.tiger
//   uninstall <游戏目录>                     删除本工具安装的 mod 档案
//   inspect <tiger 文件>                     直接检查某个档案里的文本（用于校验）
//   find    <游戏目录> <内部路径>            查询某个文件在哪个档案里

const string DefaultGameFolder = @"E:\SteamLibrary\steamapps\common\Tomb Raider";

if (args.Length < 2)
{
    Console.WriteLine(Usage());
    return 1;
}

string command = args[0];
string gameFolder = args.Length > 1 ? args[1] : DefaultGameFolder;

try
{
    switch (command)
    {
        case "list":
            ListLocales(gameFolder);
            break;
        case "dump":
            Dump(gameFolder, args[2], args.Length > 3 ? args[3] : null);
            break;
        case "install":
            Install(gameFolder, args[2]);
            break;
        case "uninstall":
            Uninstall(gameFolder);
            break;
        case "inspect":
            Inspect(gameFolder);
            break;
        case "find":
            Find(gameFolder, args[2]);
            break;
        default:
            Console.WriteLine(Usage());
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败：{ex.Message}");
    return 1;
}

static string Usage() =>
    """
    用法：
      TrTextTool list    <游戏目录>
      TrTextTool dump    <游戏目录> <输出目录> [语言]
      TrTextTool install <游戏目录> <mod目录>
      TrTextTool uninstall <游戏目录>
      TrTextTool inspect <tiger文件>
      TrTextTool find    <游戏目录> <内部路径>
    """;

static IReadOnlyList<(ArchiveFileDescriptor File, string Locale)> FindLocalsFiles(ArchiveSet archives)
{
    CdcGameInfo info = CdcGameInfo.Get(CdcGame.Tr2013);
    return archives.Files
                   .Where(f => CdcHash.Lookup(f.NameHash, CdcGame.Tr2013, true) is { } path
                               && Path.GetFileName(path) == "locals.bin")
                   .OrderBy(f => info.LocaleToLanguageCode(f.Locale))
                   .Select(f => (f, info.LocaleToLanguageCode(f.Locale)))
                   .ToList();
}

static void ListLocales(string gameFolder)
{
    using ArchiveSet archives = ArchiveSet.Open(gameFolder, true, false, CdcGame.Tr2013);
    foreach ((ArchiveFileDescriptor file, string locale) in FindLocalsFiles(archives))
        Console.WriteLine($"locals.bin  locale={locale,-8} {file.Length,8} bytes");
}

static void Dump(string gameFolder, string outputFolder, string? onlyLocale)
{
    using ArchiveSet archives = ArchiveSet.Open(gameFolder, true, false, CdcGame.Tr2013);
    foreach ((ArchiveFileDescriptor file, string locale) in FindLocalsFiles(archives))
    {
        if (onlyLocale != null && !string.Equals(locale, onlyLocale, StringComparison.OrdinalIgnoreCase))
            continue;

        using Stream stream = archives.OpenFile(file);
        LocalsBin locals = LocalsBin.Open(stream, CdcGame.Tr2013);

        string folder = Path.Combine(outputFolder, locale, "pc-w", "local");
        Directory.CreateDirectory(folder);
        string jsonPath = Path.Combine(folder, "locals.json");

        using (FileStream fs = File.Create(jsonPath))
        using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        }))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in locals.Strings.OrderBy(p => p.Key, Comparer<string>.Create(CompareTextKeys)))
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        Console.WriteLine($"{locale,-8} -> {jsonPath} ({locals.Strings.Count} 条)");
    }
}

static void Install(string gameFolder, string modFolder)
{
    // TR2013 的 mod 全部扁平化进 patch2.000.tiger，而扁平化是以「当前的 patch2」为底稿的。
    // 如果之前装过别的 mod，底稿里会残留旧内容，重装/换版本就会叠加。
    // 先把 patch2 还原成官方原件，保证每次安装的起点一致。
    RestoreOfficialPatch2(gameFolder, quiet: true);

    using ArchiveSet archives = ArchiveSet.Open(gameFolder, true, false, CdcGame.Tr2013);
    var cache = new ResourceUsageCache();
    cache.AddArchiveSet(archives, null, CancellationToken.None);

    var installer = new ModInstaller(archives, cache);
    InstalledMod? mod = installer.InstallFromFolderAsync(modFolder, new ConsoleProgress(), CancellationToken.None)
                                 .GetAwaiter().GetResult();
    if (mod == null)
        throw new InvalidOperationException("安装失败");

    Console.WriteLine($"已安装 mod：{mod.Name}");
    installer.UpdateFlatModArchive(new ConsoleProgress(), CancellationToken.None);
    Console.WriteLine("已合并进 patch2.000.tiger（官方原件备份为 bigfile._orig.patch2.000.tiger）");
}

/// <summary>用官方原件 bigfile._orig.patch2.000.tiger 还原 patch2.000.tiger。</summary>
static bool RestoreOfficialPatch2(string gameFolder, bool quiet)
{
    string backup = Path.Combine(gameFolder, "bigfile._orig.patch2.000.tiger");
    string patch2 = Path.Combine(gameFolder, "patch2.000.tiger");
    if (!File.Exists(backup))
    {
        if (!quiet)
            Console.WriteLine("警告：找不到 bigfile._orig.patch2.000.tiger，无法自动还原 patch2；"
                            + "请用 Steam 验证游戏文件完整性。");
        return false;
    }

    File.Copy(backup, patch2, true);
    if (!quiet)
        Console.WriteLine("已用官方原件还原 patch2.000.tiger");
    return true;
}

static void Uninstall(string gameFolder)
{
    // 一个 mod 可能有多个档案（不同 subId / 语言），但它们共用同一个 archive id。
    // 按 id 逐个调用 Delete 会导致第二次删除时找不到 (id, 0) 而抛异常，
    // 因此这里先收集去重后的 id，一次性删除。
    using (ArchiveSet archives = ArchiveSet.Open(gameFolder, true, true, CdcGame.Tr2013))
    {
        var cache = new ResourceUsageCache();
        cache.AddArchiveSet(archives, null, CancellationToken.None);

        List<int> modIds = archives.Archives
                                   .Where(a => a.ModName != null)
                                   .Select(a => a.Id)
                                   .Distinct()
                                   .ToList();

        if (modIds.Count == 0)
        {
            Console.WriteLine("没有检测到已安装的 mod");
        }
        else
        {
            foreach (int id in modIds)
                Console.WriteLine($"移除 mod 档案 id={id}");
            archives.Delete(modIds, cache, new ConsoleProgress(), CancellationToken.None);
        }
    }
    // 退出上面的 using，确保所有档案句柄已释放，再动 patch2 文件

    foreach (string file in Directory.EnumerateFiles(gameFolder, "bigfile._mod.*"))
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"警告：无法删除 {Path.GetFileName(file)}：{ex.Message}");
        }
    }

    if (!RestoreOfficialPatch2(gameFolder, quiet: false))
        Console.WriteLine("请用 Steam 验证游戏文件完整性来还原。");
}

static void Inspect(string archivePath)
{
    using Archive archive = Archive.Open(archivePath, null, CdcGame.Tr2013);
    CdcGameInfo info = CdcGameInfo.Get(CdcGame.Tr2013);
    Console.WriteLine($"archive id={archive.Id} sub={archive.SubId} files={archive.Files.Count}");

    foreach (ArchiveFileDescriptor file in archive.Files)
    {
        if (CdcHash.Lookup(file.NameHash, CdcGame.Tr2013, true) is not { } path
            || Path.GetFileName(path) != "locals.bin")
            continue;

        using Stream stream = archive.OpenFile(file);
        LocalsBin locals = LocalsBin.Open(stream, CdcGame.Tr2013);
        string locale = info.LocaleToLanguageCode(file.Locale);
        int empty = locals.Strings.Count(p => string.IsNullOrEmpty(p.Value));
        Console.WriteLine($"{locale,-8} {locals.Strings.Count,5} 条  空值 {empty}  {file.Length} bytes");
    }
}

static void Find(string gameFolder, string internalPath)
{
    using ArchiveSet archives = ArchiveSet.Open(gameFolder, true, true, CdcGame.Tr2013);
    ulong hash = CdcHash.Calculate(internalPath, CdcGame.Tr2013, true);
    foreach (Archive archive in archives.Archives)
        foreach (ArchiveFileDescriptor file in archive.Files.Where(f => f.NameHash == hash))
            Console.WriteLine($"{Path.GetFileName(archive.BaseFilePath)}  id={archive.Id}  {file.Length} bytes");
}

static int CompareTextKeys(string a, string b) =>
    int.TryParse(a, out int ai) && int.TryParse(b, out int bi)
        ? ai.CompareTo(bi)
        : string.CompareOrdinal(a, b);

sealed class ConsoleProgress : ITaskProgress
{
    public void Begin(string statusText) => Console.WriteLine($"  {statusText}");
    public void Report(float progress) { }
    public void End() { }
}
