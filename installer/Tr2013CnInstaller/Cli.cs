using System.Reflection;
using System.Text;

namespace Tr2013CnInstaller;

/// <summary>命令行模式，供自动化测试与进阶用户使用：
///   TR2013CnInstaller --install "&lt;游戏目录&gt;" [--zip "&lt;本地汉化包&gt;"] [--version v8.0] [--log "&lt;文件&gt;"]
///   TR2013CnInstaller --uninstall "&lt;游戏目录&gt;" [--log "&lt;文件&gt;"]
/// </summary>
static class Cli
{
    public static int Run(string[] args)
    {
        TryAttachConsole();

        string? action = null, gamePath = null, zip = null, version = InstallerCore.DefaultVersion, logFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--install":   action = "install";   break;
                case "--uninstall": action = "uninstall"; break;
                case "--zip":       zip = Value(args, ref i); break;
                case "--version":   version = Value(args, ref i); break;
                case "--log":       logFile = Value(args, ref i); break;
                default:
                    if (gamePath == null && !args[i].StartsWith("--"))
                        gamePath = args[i];
                    break;
            }
        }

        if (action == null || gamePath == null)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  TR2013CnInstaller --install \"<游戏目录>\" [--zip \"<本地汉化包>\"] [--version v8.0] [--log \"<文件>\"]");
            Console.WriteLine("  TR2013CnInstaller --uninstall \"<游戏目录>\" [--log \"<文件>\"]");
            return 2;
        }

        gamePath = Path.GetFullPath(gamePath);
        if (!File.Exists(Path.Combine(gamePath, "TombRaider.exe")))
        {
            Console.Error.WriteLine($"错误：{gamePath} 下没有 TombRaider.exe");
            return 2;
        }

        using var logWriter = logFile != null
            ? new StreamWriter(File.Create(logFile), Encoding.UTF8) { AutoFlush = true }
            : null;

        void Log(string line)
        {
            Console.WriteLine(line);
            logWriter?.WriteLine(line);
        }

        var core = new InstallerCore(Log);
        try
        {
            if (action == "install")
                core.InstallAsync(gamePath, zip, version, null, CancellationToken.None).GetAwaiter().GetResult();
            else
                core.UninstallAsync(gamePath, CancellationToken.None).GetAwaiter().GetResult();
            Log("命令行操作成功");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"失败：{ex.Message}");
            Log(ex.ToString());
            return 1;
        }
    }

    static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"参数 {args[i - 1]} 缺少值");

    static void TryAttachConsole()
    {
        try { _ = AttachConsole(ATTACH_PARENT_PROCESS); } catch { /* 没有父控制台就忽略 */ }
    }

    const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);
}
