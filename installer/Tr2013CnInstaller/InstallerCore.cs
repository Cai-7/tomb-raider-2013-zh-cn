using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TrRebootTools.Shared;
using TrRebootTools.Shared.Cdc;
using TrRebootTools.ModManager.Mod;

namespace Tr2013CnInstaller;

/// <summary>
/// 安装/卸载核心逻辑。GUI 与命令行模式共用。
/// 本程序只是个免费工具：它不包含、也不再分发任何游戏资源或汉化内容，
/// 汉化包始终从原作者的 GitHub Release 实时下载。
/// </summary>
public sealed class InstallerCore
{
    public const string AuthorRepo = "https://github.com/intergra/TR2013-Simplified-Chinese";
    public const string ModFileName = "TR2013-Simplified-Chinese.zip";
    public const string ModName = "TR2013-Simplified-Chinese";
    public const string DefaultVersion = "v8.0";

    /// <summary>重建字库（Noto Sans SC）的字体资源大小，安装后用它校验是否替换成功。</summary>
    public const long ExpectedFontResourceLength = 18_269_732;

    /// <summary>汉化包文本条数（v8.0），仅作信息性校验。</summary>
    public const int ExpectedLocalsCount = 3389;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    readonly Action<string> _log;

    public InstallerCore(Action<string> log) => _log = log;

    // ---------------------------------------------------------------- 游戏定位

    public static string? FindGame()
    {
        var roots = new List<string>();

        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steamPath)
            roots.Add(steamPath);

        foreach (char drive in new[] { 'C', 'D', 'E', 'F', 'G', 'H', 'I' })
        {
            string library = $@"{drive}:\SteamLibrary";
            if (Directory.Exists(library))
                roots.Add(library);
        }

        foreach (string root in roots.ToList())
        {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(root, "steamapps", "common", "Tomb Raider");
            if (File.Exists(Path.Combine(candidate, "TombRaider.exe")))
                return candidate;
        }
        return null;
    }

    public static bool IsGameRunning() =>
        Process.GetProcessesByName("TombRaider").Length > 0;

    static string ModUrl(string version) =>
        $"https://github.com/intergra/TR2013-Simplified-Chinese/releases/download/{version}/{ModFileName}";

    static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "TR2013CnInstaller");

    // ---------------------------------------------------------------- 磁盘空间

    public static (bool ok, string message) CheckDiskSpace(string gamePath)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(gamePath));
        if (root == null)
            return (true, "");
        DriveInfo drive = new(root);
        if (drive.AvailableFreeSpace < 8L * 1024 * 1024 * 1024)
            return (false, $"磁盘空间不足：{root} 需要约 8 GB 可用空间，当前只有 {drive.AvailableFreeSpace / 1f / 1024 / 1024 / 1024:F1} GB");
        return (true, "");
    }

    /// <summary>
    /// 哈希对照表必须和 exe 在同一目录（CdcHash 从 AppContext.BaseDirectory 读取）。
    /// 缺了它，安装会静默丢失字库等资源，必须提前拦下来。
    /// </summary>
    public static void CheckHashList()
    {
        string listPath = Path.Combine(AppContext.BaseDirectory, "TR2013_PC_Release.list");
        if (!File.Exists(listPath))
            throw new InvalidOperationException(
                "缺少哈希对照表 TR2013_PC_Release.list（应与安装器 exe 在同一目录）。" +
                "没有它安装会不完整，请重新下载完整的安装器压缩包。");
    }

    // ---------------------------------------------------------------- 下载

    /// <summary>下载汉化包（带断点提示与缓存复用），返回本地 zip 路径。</summary>
    public async Task<string> EnsureModZipAsync(string version, string? localZipOverride, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(localZipOverride))
        {
            if (!File.Exists(localZipOverride))
                throw new FileNotFoundException("指定的本地汉化包不存在", localZipOverride);
            _log($"使用本地汉化包：{localZipOverride}");
            return localZipOverride;
        }

        Directory.CreateDirectory(CacheDir);
        string zipPath = Path.Combine(CacheDir, $"{version}-{ModFileName}");

        if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 500L * 1024 * 1024)
        {
            _log($"发现已下载的汉化包缓存，直接复用：{zipPath}");
            return zipPath;
        }

        string url = ModUrl(version);
        _log($"开始下载汉化包（约 1.1 GB，来自原作者 Release）：");
        _log($"  {url}");

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAsync(url, zipPath, ct);
                break;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _log($"下载失败（第 {attempt} 次）：{ex.Message}，10 秒后重试 ...");
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }

        long length = new FileInfo(zipPath).Length;
        if (length < 500L * 1024 * 1024)
            throw new InvalidDataException($"下载的文件大小异常（{length:N0} 字节），可能不是完整的汉化包");

        _log($"下载完成：{zipPath}（{length / 1024 / 1024} MB）");
        return zipPath;
    }

    async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using HttpResponseMessage resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using FileStream fs = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using Stream src = await resp.Content.ReadAsStreamAsync(ct);

        byte[] buffer = new byte[1024 * 1024];
        long done = 0;
        int read;
        var sw = Stopwatch.StartNew();
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (sw.ElapsedMilliseconds > 500)
            {
                sw.Restart();
                if (total.HasValue)
                    _log($"下载进度：{done / 1024 / 1024} / {total.Value / 1024 / 1024} MB（{done * 100 / total.Value}%）");
                else
                    _log($"下载进度：{done / 1024 / 1024} MB");
            }
        }
    }

    // ---------------------------------------------------------------- 安装

    /// <summary>一键安装：下载（可选）→ 还原官方 patch2 → 安装 mod → 扁平化 → 校验。</summary>
    public async Task InstallAsync(string gamePath, string? localZipOverride, string version,
                                   IProgress<double>? percent, CancellationToken ct)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("游戏正在运行，请先完全退出再安装。");

        CheckHashList();

        (bool spaceOk, string spaceMsg) = CheckDiskSpace(gamePath);
        if (!spaceOk)
            throw new InvalidOperationException(spaceMsg);

        string zipPath = await EnsureModZipAsync(version, localZipOverride, ct);

        _log("正在还原官方 patch2.000.tiger（确保安装起点干净）...");
        if (!RestoreOfficialPatch2(gamePath))
            _log("警告：未找到官方原件备份，将在现有 patch2 基础上安装。");

        // 第一步：删除同名的旧 mod 档案。
        // 一个 mod 可能有多个档案（不同 subId）但共用 archive id，必须按去重后的 id 一次性删除。
        using (ArchiveSet archives = ArchiveSet.Open(gamePath, includeGame: true, includeMods: true, CdcGame.Tr2013))
        {
            var cache = new ResourceUsageCache();
            cache.AddArchiveSet(archives, null, CancellationToken.None);

            List<int> existingIds = archives.Archives
                .Where(a => a.ModName != null && string.Equals(a.ModName, ModName, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id)
                .Distinct()
                .ToList();
            if (existingIds.Count > 0)
            {
                _log("检测到已安装的同名 mod，先移除旧档案 ...");
                archives.Delete(existingIds, cache, new ProgressAdapter(_log), CancellationToken.None);
            }
        }
        // 释放句柄后再进入安装阶段

        // 第二步：把 zip 解压到临时目录（目录名 = mod 名，ModManager 用它作为 mod 标识）。
        string tempRoot = Path.Combine(Path.GetTempPath(), "TR2013CnInstaller", Guid.NewGuid().ToString("N"));
        string modFolder = Path.Combine(tempRoot, ModName);
        try
        {
            _log("正在解压汉化包（1.1 GB）...");
            Directory.CreateDirectory(modFolder);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, modFolder, overwriteFiles: true);

            // 第三步：安装阶段。档案集必须以 includeMods:false 打开——
            // 实测（TrTextTool 路径，已验证可用）这就是 ModManager 的既有工作方式；
            // 用 includeMods:true 打开会导致 mod 资源的外部引用解析越界。
            using (ArchiveSet archives = ArchiveSet.Open(gamePath, includeGame: true, includeMods: false, CdcGame.Tr2013))
            {
                var cache = new ResourceUsageCache();
                cache.AddArchiveSet(archives, null, CancellationToken.None);

                var installer = new ModInstaller(archives, cache);
                _log("正在安装汉化包（重打包需要几分钟，请耐心等待）...");
                InstalledMod? mod = await installer.InstallFromFolderAsync(modFolder, new ProgressAdapter(_log, percent), ct);
                if (mod == null)
                    throw new InvalidOperationException("安装失败：Mod Manager 未返回结果。");

                _log($"已安装 mod：{mod.Name}");
                _log("正在把 mod 合并进 patch2.000.tiger ...");
                installer.UpdateFlatModArchive(new ProgressAdapter(_log), CancellationToken.None);
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* 临时目录清理失败不影响结果 */ }
        }

        Verify(gamePath);
        _log("安装完成 ✓");
    }

    /// <summary>校验：字库资源必须已替换为重建版，文本条数应与汉化包一致。</summary>
    public void Verify(string gamePath)
    {
        using ArchiveSet archives = ArchiveSet.Open(gamePath, includeGame: true, includeMods: true, CdcGame.Tr2013);
        var info = CdcGameInfo.Get(CdcGame.Tr2013);

        bool fontOk = false;
        int? localsCount = null;

        foreach (Archive archive in archives.Archives)
        {
            foreach (ArchiveFileDescriptor file in archive.Files)
            {
                string? path = CdcHash.Lookup(file.NameHash, CdcGame.Tr2013, true);
                if (path == null)
                    continue;

                if (path.EndsWith("fontchinese.drm"))
                {
                    ResourceCollection? coll = archives.GetResourceCollection(file);
                    if (coll == null)
                        continue;
                    foreach (ResourceDescriptor r in coll.Resources.Where(r => r.Id == 0x60F8))
                    {
                        using Stream? s = archives.TryOpenResource(r);
                        if (s != null && s.Length == ExpectedFontResourceLength)
                        {
                            fontOk = true;
                            _log($"校验：重建字库已就位（{s.Length:N0} 字节，位于 {Path.GetFileName(archive.BaseFilePath)}）");
                        }
                    }
                }
                else if (Path.GetFileName(path) == "locals.bin"
                         && info.LocaleToLanguageCode(file.Locale) == "zhhant"
                         && file.Length > 100_000)
                {
                    using Stream s = archives.OpenFile(file);
                    LocalsBin locals = LocalsBin.Open(s, CdcGame.Tr2013);
                    localsCount = locals.Strings.Count;
                }
            }
        }

        if (!fontOk)
            throw new InvalidOperationException($"校验失败：未找到重建字库（应为 {ExpectedFontResourceLength:N0} 字节）。请重新安装。");

        if (localsCount is int count)
        {
            if (count == ExpectedLocalsCount)
                _log($"校验：简体文本 {count} 条，与汉化包一致 ✓");
            else
                _log($"提示：简体文本 {count} 条（v8.0 预期 {ExpectedLocalsCount} 条，可能是汉化包版本更新）");
        }
    }

    // ---------------------------------------------------------------- 卸载

    /// <summary>卸载全部已安装 mod，并把 patch2 还原成官方原件。</summary>
    public Task UninstallAsync(string gamePath, CancellationToken ct)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("游戏正在运行，请先完全退出再卸载。");

        // TR2013 的 mod 全部扁平化进 patch2.000.tiger，一个 mod 可能有多个档案（不同 subId）
        // 但共用同一个 archive id，必须按去重后的 id 一次性删除。
        using (ArchiveSet archives = ArchiveSet.Open(gamePath, includeGame: true, includeMods: true, CdcGame.Tr2013))
        {
            var cache = new ResourceUsageCache();
            cache.AddArchiveSet(archives, null, CancellationToken.None);

            List<int> modIds = archives.Archives
                                       .Where(a => a.ModName != null)
                                       .Select(a => a.Id)
                                       .Distinct()
                                       .ToList();

            if (modIds.Count == 0)
                _log("没有检测到已安装的 mod。");
            else
            {
                foreach (int id in modIds)
                    _log($"移除 mod 档案 id={id} ...");
                archives.Delete(modIds, cache, new ProgressAdapter(_log), ct);
            }
        }
        // 退出 using 释放所有档案句柄后再动 patch2 文件

        foreach (string file in Directory.EnumerateFiles(gamePath, "bigfile._mod.*"))
        {
            try { File.Delete(file); }
            catch (IOException ex) { _log($"警告：无法删除 {Path.GetFileName(file)}：{ex.Message}"); }
        }

        if (RestoreOfficialPatch2(gamePath))
            _log("已用官方原件还原 patch2.000.tiger ✓");
        else
            _log("警告：找不到官方原件备份，请用 Steam「验证游戏文件完整性」还原。");
        return Task.CompletedTask;
    }

    /// <summary>用官方原件 bigfile._orig.patch2.000.tiger 还原 patch2.000.tiger。</summary>
    public static bool RestoreOfficialPatch2(string gamePath)
    {
        string backup = Path.Combine(gamePath, "bigfile._orig.patch2.000.tiger");
        string patch2 = Path.Combine(gamePath, "patch2.000.tiger");
        if (!File.Exists(backup))
            return false;
        File.Copy(backup, patch2, true);
        return true;
    }
}

/// <summary>把 ModManager 的 ITaskProgress 回调转成日志行 / 进度百分比。</summary>
public sealed class ProgressAdapter : ITaskProgress
{
    readonly Action<string> _log;
    readonly IProgress<double>? _percent;

    public ProgressAdapter(Action<string> log, IProgress<double>? percent = null)
    {
        _log = log;
        _percent = percent;
    }

    public void Begin(string statusText) => _log("  " + statusText);
    public void Report(float progress) => _percent?.Report(progress);
    public void End() { }
}
