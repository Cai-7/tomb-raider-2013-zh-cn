using System.Diagnostics;
using System.Reflection;

namespace Tr2013CnInstaller;

public sealed class MainForm : Form
{
    readonly TextBox _gamePath;
    readonly Button _btnInstall;
    readonly Button _btnUninstall;
    readonly ProgressBar _progress;
    readonly Label _status;
    readonly TextBox _log;
    readonly InstallerCore _core;

    public MainForm()
    {
        Text = "古墓丽影 9（2013）简体中文一键安装器";
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(720, 520);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var lblTitle = new Label
        {
            Text = "为 Steam 版《古墓丽影 9》安装简体中文（含重建字库，界面无方框）",
            Location = new Point(16, 14),
            AutoSize = true,
            ForeColor = Color.FromArgb(40, 40, 40),
        };

        var lblGame = new Label
        {
            Text = "游戏目录：",
            Location = new Point(16, 48),
            AutoSize = true,
        };
        _gamePath = new TextBox
        {
            Location = new Point(95, 44),
            Size = new Size(505, 27),
            ReadOnly = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var btnBrowse = new Button
        {
            Text = "浏览...",
            Location = new Point(610, 43),
            Size = new Size(90, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnBrowse.Click += (_, _) => BrowseGame();

        _btnInstall = new Button
        {
            Text = "一键安装",
            Location = new Point(16, 84),
            Size = new Size(200, 40),
        };
        _btnInstall.Click += (_, _) => RunOperation(install: true);

        _btnUninstall = new Button
        {
            Text = "卸载汉化",
            Location = new Point(232, 84),
            Size = new Size(140, 40),
        };
        _btnUninstall.Click += (_, _) => RunOperation(install: false);

        _progress = new ProgressBar
        {
            Location = new Point(16, 138),
            Size = new Size(684, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _status = new Label
        {
            Text = "就绪。",
            Location = new Point(16, 166),
            AutoSize = true,
            ForeColor = Color.DimGray,
        };
        _log = new TextBox
        {
            Location = new Point(16, 192),
            Size = new Size(684, 258),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 248, 248),
            Font = new Font("Consolas", 8.5f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        var linkAbout = new LinkLabel
        {
            Text = "关于 / 署名与许可",
            Location = new Point(16, 456),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        linkAbout.LinkClicked += (_, _) => ShowAbout();

        var linkProject = new LinkLabel
        {
            Text = "项目主页（GitHub）",
            Location = new Point(620, 456),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        linkProject.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo("https://github.com/Cai-7/tomb-raider-2013-zh-cn") { UseShellExecute = true });

        Controls.AddRange([lblTitle, lblGame, _gamePath, btnBrowse, _btnInstall, _btnUninstall,
                           _progress, _status, _log, linkAbout, linkProject]);

        _core = new InstallerCore(line => BeginInvoke(() => AppendLog(line)));

        // 自动定位游戏目录
        string? game = InstallerCore.FindGame();
        _gamePath.Text = game ?? "";
        if (game == null)
            AppendLog("未自动找到游戏目录，请点击「浏览...」手动选择包含 TombRaider.exe 的文件夹。");
    }

    void BrowseGame()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 TombRaider.exe 的游戏目录",
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (!File.Exists(Path.Combine(dialog.SelectedPath, "TombRaider.exe")))
        {
            MessageBox.Show(this, "所选目录下没有 TombRaider.exe，请选择游戏根目录。", "目录不正确",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _gamePath.Text = dialog.SelectedPath;
    }

    void RunOperation(bool install)
    {
        string gamePath = _gamePath.Text.Trim();
        if (gamePath.Length == 0 || !File.Exists(Path.Combine(gamePath, "TombRaider.exe")))
        {
            MessageBox.Show(this, "请先选择有效的游戏目录（含 TombRaider.exe）。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (InstallerCore.IsGameRunning())
        {
            MessageBox.Show(this, "游戏正在运行，请先完全退出再操作。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        _progress.Value = 0;
        _status.Text = install ? "正在安装 ..." : "正在卸载 ...";
        AppendLog(install ? "―――――― 开始安装 ――――――" : "―――――― 开始卸载 ――――――");

        var uiProgress = new Progress<double>(p =>
        {
            _progress.Value = Math.Clamp((int)(p * 100), 0, 100);
        });

        Task.Run(async () =>
        {
            try
            {
                if (install)
                    await _core.InstallAsync(gamePath, null, InstallerCore.DefaultVersion, uiProgress, CancellationToken.None);
                else
                    await _core.UninstallAsync(gamePath, CancellationToken.None);
                BeginInvoke(() =>
                {
                    _status.Text = install ? "安装完成 ✓ 请启动游戏，并在「语言」设置里选 Chinese / 中文" : "卸载完成 ✓";
                    MessageBox.Show(this,
                        install
                            ? "安装完成！\n\n1. 启动游戏\n2. 在「语言」设置里选择 Chinese / 中文\n\n提示：语音仍是英文，官方没有中文配音。"
                            : "卸载完成，游戏已还原为官方版本。",
                        "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    _status.Text = "操作失败 ✗";
                    AppendLog($"失败：{ex.Message}");
                    MessageBox.Show(this, $"操作失败：{ex.Message}", "错误",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
            finally
            {
                BeginInvoke(() => SetBusy(false));
            }
        });
    }

    void SetBusy(bool busy)
    {
        _btnInstall.Enabled = !busy;
        _btnUninstall.Enabled = !busy;
        UseWaitCursor = busy;
    }

    void AppendLog(string line)
    {
        if (_log.TextLength > 200_000)
            _log.Clear();
        _log.AppendText(line + Environment.NewLine);
    }

    void ShowAbout()
    {
        MessageBox.Show(this,
            "古墓丽影 9（2013）简体中文一键安装器 v"
            + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0") + "\n\n" +
            "本程序只是一个免费的安装工具，不包含、也不再分发任何游戏资源或汉化内容；\n" +
            "汉化包在安装时从原作者的官方发布页实时下载。\n\n" +
            "简体中文翻译与重建字库：TR2013-Simplified-Chinese\n" +
            "    作者：NoWindNoMoon（此情无关风月）\n" +
            "    " + InstallerCore.AuthorRepo + "\n\n" +
            ".tiger 档案解析与 mod 打包：TrRebootModTools（MIT，arcusmaximus）\n" +
            "繁简转换：OpenCC\n" +
            "字体：Noto Sans SC / KR（SIL OFL 1.1）\n\n" +
            "《Tomb Raider》(2013) 版权归 Square Enix / Crystal Dynamics 所有。\n" +
            "本工具仅供个人学习交流使用，禁止用于商业用途。",
            "关于与署名", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
