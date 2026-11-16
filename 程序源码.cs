using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

public sealed class LinkSettings
{
    public string WatchPath = "";
    public string TargetPath = "";
    public bool RequireWindow = true;

    public void Validate()
    {
        ValidateExe(WatchPath, "程序 A");
        ValidateExe(TargetPath, "程序 B");
        if (SamePath(WatchPath, TargetPath)) throw new Exception("程序 A 和程序 B 请选择不同的程序。");
        if (SamePath(WatchPath, Assembly.GetExecutingAssembly().Location) || SamePath(TargetPath, Assembly.GetExecutingAssembly().Location))
            throw new Exception("请选择其他程序，不能选择联动助手本身。");
    }
    private static void ValidateExe(string path, string label)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path) ||
            !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new Exception(label + "：请选择存在的 .exe 程序文件。");
    }
    internal static bool SamePath(string a, string b)
    {
        return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
    public static LinkSettings Load(string path)
    {
        if (!File.Exists(path)) return new LinkSettings();
        using (var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
            return (LinkSettings)new XmlSerializer(typeof(LinkSettings)).Deserialize(reader);
    }
    public void Save(string path)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new XmlSerializer(typeof(LinkSettings)).Serialize(stream, this);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class LaunchGate
{
    private bool completed;
    private DateTime nextAttempt = DateTime.MinValue;
    public bool Tick(bool running, DateTime now, Func<bool> launch)
    {
        if (!running) { completed = false; nextAttempt = DateTime.MinValue; return false; }
        if (completed || now < nextAttempt) return false;
        bool success = launch();
        if (success) completed = true;
        else nextAttempt = now.AddSeconds(30);
        return success;
    }
}

internal static class LinkRuntime
{
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out uint needed);

    internal static string DesktopName()
    {
        var value = new StringBuilder(256); uint needed;
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, value, 512, out needed) ? value.ToString() : "unknown";
    }
    internal static HashSet<uint> Find(string path)
    {
        var ids = new HashSet<uint>();
        if (String.IsNullOrWhiteSpace(path)) return ids;
        int session;
        using (Process current = Process.GetCurrentProcess()) session = current.SessionId;
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    bool matches;
                    // Protected processes may deny module access. In that case use the selected exe name in this session.
                    try { matches = LinkSettings.SamePath(process.MainModule.FileName, path); }
                    catch (System.ComponentModel.Win32Exception) { matches = true; }
                    if (matches && !process.HasExited) ids.Add((uint)process.Id);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return ids;
    }
    internal static bool Restore(HashSet<uint> ids)
    {
        IntPtr candidate = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
        {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (!ids.Contains(pid) || GetWindow(hwnd, 4) != IntPtr.Zero || GetWindowTextLength(hwnd) == 0) return true;
            if (candidate == IntPtr.Zero || IsWindowVisible(hwnd)) candidate = hwnd;
            return !IsWindowVisible(hwnd);
        }, IntPtr.Zero);
        if (candidate == IntPtr.Zero) return false;
        ShowWindowAsync(candidate, IsIconic(candidate) ? 9 : 5);
        SetForegroundWindow(candidate);
        Thread.Sleep(150);
        return IsWindowVisible(candidate) && !IsIconic(candidate);
    }
    internal static bool Launch(LinkSettings settings, Action<string> report)
    {
        try
        {
            if (!String.Equals(DesktopName(), "Default", StringComparison.OrdinalIgnoreCase))
                throw new Exception("请从 Windows 正常桌面运行助手，当前桌面：" + DesktopName());
            var existing = Find(settings.TargetPath);
            if (existing.Count > 0)
            {
                if (Restore(existing)) { report("已恢复程序 B，窗口可见。"); return true; }
                if (!settings.RequireWindow) { report("程序 B 已在后台运行，未重复启动。"); return true; }
                throw new Exception("程序 B 已在运行，但尚未找到可见主窗口；不会重复启动。");
            }
            if (!File.Exists(settings.TargetPath)) throw new FileNotFoundException("程序 B 已移动，请重新选择。");
            var info = new ProcessStartInfo(settings.TargetPath) {
                WorkingDirectory = Path.GetDirectoryName(settings.TargetPath), UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal
            };
            using (Process started = Process.Start(info))
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    var ids = Find(settings.TargetPath);
                    if (Restore(ids)) { report("已启动程序 B，并确认窗口可见。"); return true; }
                    if (!settings.RequireWindow && ids.Count > 0) { report("已启动程序 B，并确认后台进程运行。"); return true; }
                    Thread.Sleep(250);
                }
            }
            throw new Exception(settings.RequireWindow ? "尚未确认程序 B 的可见窗口。若它是后台工具，可取消窗口检查。" : "尚未找到程序 B 的进程，请确认选择的是实际运行文件。");
        }
        catch (Exception e) { report("打开失败（自动联动将在30秒后重试）：" + e.Message); return false; }
    }
}

internal sealed class MainWindow : Form
{
    private readonly TextBox watch = new TextBox();
    private readonly TextBox target = new TextBox();
    private readonly CheckBox requireWindow = new CheckBox();
    private readonly Label status = new Label();
    private readonly Label activeRule = new Label();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly EventWaitHandle settingsRequest;
    private readonly EventWaitHandle stopRequest;
    private LinkSettings settings;
    private LaunchGate gate = new LaunchGate();
    private bool lastRunning;
    private bool exiting;
    private bool configured;

    internal MainWindow(bool background)
    {
        Text = "程序联动助手";
        ClientSize = new Size(700, 440);
        MinimumSize = new Size(716, 479);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(245, 247, 244);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var heading = new Label { Text = "选择两个程序，自动联动", Location = new Point(28, 23), Size = new Size(640, 32), Font = new Font(Font.FontFamily, 17, FontStyle.Bold), ForeColor = Color.FromArgb(29, 65, 49) };
        Controls.Add(heading);
        Controls.Add(new Label { Text = "当程序 A 运行时，自动打开程序 B。每次 A 运行只触发一次。", Location = new Point(30, 66), Size = new Size(640, 25) });
        AddPicker("程序 A · 检测对象", watch, 110);
        AddPicker("程序 B · 自动打开", target, 190);
        requireWindow.Text = "确认程序 B 的窗口可见（后台工具可取消勾选）";
        requireWindow.SetBounds(30, 272, 620, 26);
        requireWindow.Checked = true;
        Controls.Add(requireWindow);
        var save = MakeButton("保存并开始检测", 30, 310, 176);
        save.BackColor = Color.FromArgb(29, 65, 49); save.ForeColor = Color.White;
        save.Click += delegate { SaveSettings(); };
        var test = MakeButton("立即打开 B", 220, 310, 135);
        test.Click += delegate {
            if (!configured) { MessageBox.Show(this, "请先选择 A、B 并保存。", Text); return; }
            LinkRuntime.Launch(settings, Report);
        };
        var hide = MakeButton("收起到托盘", 369, 310, 135);
        hide.Click += delegate { Hide(); };
        activeRule.SetBounds(30, 360, 640, 23);
        activeRule.AutoEllipsis = true;
        Controls.Add(activeRule);
        status.SetBounds(30, 390, 640, 35);
        status.AutoEllipsis = true;
        status.ForeColor = Color.FromArgb(70, 85, 75);
        Controls.Add(status);
        AcceptButton = save;

        settingsRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SettingsEvent);
        stopRequest = new EventWaitHandle(false, EventResetMode.ManualReset, Program.StopEvent);
        var menu = new ContextMenuStrip();
        menu.Items.Add("设置程序 A / B", null, delegate { Reveal(); });
        menu.Items.Add("立即打开程序 B", null, delegate { if (configured) LinkRuntime.Launch(settings, Report); else Reveal(); });
        menu.Items.Add("查看运行日志", null, delegate { Process.Start("notepad.exe", "\"" + Program.LogPath + "\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("启用登录后自动运行", null, delegate { ChangeStartup(true); });
        menu.Items.Add("取消登录后自动运行", null, delegate { ChangeStartup(false); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出助手", null, delegate { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "程序联动助手", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { Reveal(); };
        try
        {
            settings = LinkSettings.Load(Program.ConfigPath);
            if (File.Exists(Program.ConfigPath)) { settings.Validate(); configured = true; }
        }
        catch (Exception e) { settings = new LinkSettings(); Report("配置未加载，请重新选择：" + e.Message); }
        watch.Text = settings.WatchPath; target.Text = settings.TargetPath; requireWindow.Checked = settings.RequireWindow;
        UpdateRule();
        if (configured) Report("正在等待程序 A。关闭窗口后继续在托盘检测。");
        else Report("请选择程序 A 和程序 B，保存后开始检测。");
        timer.Interval = 2000;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Shown += delegate { if (background && configured) Hide(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        FormClosed += delegate {
            timer.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose(); settingsRequest.Dispose(); stopRequest.Dispose();
            Program.Log("助手已退出。");
        };
        Program.Log("程序联动助手 v1 启动。桌面：" + LinkRuntime.DesktopName());
    }
    private void AddPicker(string label, TextBox box, int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(30, y), Size = new Size(400, 24) });
        box.SetBounds(30, y + 29, 535, 30); box.ReadOnly = true; Controls.Add(box);
        var browse = MakeButton("选择…", 579, y + 27, 90);
        browse.Click += delegate {
            using (var dialog = new OpenFileDialog { Title = "选择 " + label + " 的实际 .exe 文件", Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true, DereferenceLinks = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
        };
    }
    private Button MakeButton(string text, int x, int y, int width)
    {
        var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 36), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
        Controls.Add(button); return button;
    }
    private void SaveSettings()
    {
        try
        {
            var next = new LinkSettings { WatchPath = watch.Text, TargetPath = target.Text, RequireWindow = requireWindow.Checked };
            next.Validate(); next.Save(Program.ConfigPath);
            settings = next; gate = new LaunchGate(); lastRunning = false; configured = true;
            UpdateRule(); Report("已保存，开始检测。A 当前正在运行时也会触发联动。");
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "未能保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void UpdateRule()
    {
        activeRule.Text = configured ? "当前联动：" + Path.GetFileName(settings.WatchPath) + "  →  " + Path.GetFileName(settings.TargetPath) : "尚未配置联动";
    }
    private void Tick()
    {
        if (stopRequest.WaitOne(0)) { exiting = true; Close(); return; }
        if (settingsRequest.WaitOne(0)) Reveal();
        if (!configured) return;
        timer.Stop();
        try
        {
            bool running = LinkRuntime.Find(settings.WatchPath).Count > 0;
            if (running != lastRunning) Report(running ? "检测到程序 A 运行。" : "程序 A 已退出，等待下次运行。");
            lastRunning = running;
            gate.Tick(running, DateTime.UtcNow, delegate { return LinkRuntime.Launch(settings, Report); });
        }
        catch (Exception e) { Report("检测失败：" + e.Message); }
        finally { if (!exiting) timer.Start(); }
    }
    private void Report(string message) { status.Text = message; Program.Log(message); }
    private void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ChangeStartup(bool enable)
    {
        try { Program.SetStartup(enable); Report(enable ? "已启用当前用户登录后自动运行。" : "已取消登录后自动运行，助手仍继续检测。"); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "开机启动设置失败"); }
    }
}

internal static class Program
{
    internal const string SettingsEvent = @"Local\UserProgramLink_Settings_v1";
    internal const string StopEvent = @"Local\UserProgramLink_Stop_v1";
    private const string MutexName = @"Local\UserProgramLink_Instance_v1";
    internal static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "联动设置.xml");
    internal static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "运行日志.txt");
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--self-test") { SelfTest(); return 0; }
            if (args.Length > 0 && args[0] == "--stop") { Signal(StopEvent); return 0; }
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) { Signal(SettingsEvent); return 0; }
                try
                {
                    if (!String.Equals(LinkRuntime.DesktopName(), "Default", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("请从 Windows 桌面双击运行程序联动助手。");
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainWindow(args.Length > 0 && args[0] == "--background"));
                }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception e) { Log("错误：" + e.Message); return 1; }
    }
    private static void Signal(string name)
    {
        try { using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }
    internal static void SetStartup(bool enable)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "程序联动助手.lnk");
        if (!enable) { if (File.Exists(path)) File.Delete(path); return; }
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType); object link = null;
        try
        {
            link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type type = link.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
            type.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { "--background" });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { AppDomain.CurrentDomain.BaseDirectory });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "按自定义规则检测程序 A 并启动程序 B" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally { if (link != null) Marshal.ReleaseComObject(link); Marshal.ReleaseComObject(shell); }
    }
    internal static void Log(string message)
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 262144) { File.Copy(LogPath, LogPath + ".old", true); File.WriteAllText(LogPath, ""); }
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }
    private static void Check(bool passed, string name, List<string> results)
    {
        if (!passed) throw new Exception("自测失败：" + name);
        results.Add("通过：" + name);
    }
    internal static void SelfTest()
    {
        var results = new List<string>();
        var gate = new LaunchGate(); var time = DateTime.UtcNow; int calls = 0;
        Func<bool> launch = delegate { calls++; return true; };
        gate.Tick(false, time, launch); Check(calls == 0, "A 未运行时不启动 B", results);
        gate.Tick(true, time, launch); Check(calls == 1, "A 首次运行触发 B", results);
        gate.Tick(true, time, launch); Check(calls == 1, "同一次运行不重复触发", results);
        gate.Tick(false, time, launch); gate.Tick(true, time, launch); Check(calls == 2, "A 退出后再次运行可触发", results);
        gate = new LaunchGate(); int tries = 0; Func<bool> retry = delegate { return ++tries > 1; };
        gate.Tick(true, time, retry); gate.Tick(true, time.AddSeconds(29), retry); Check(tries == 1, "失败等待30秒", results);
        gate.Tick(true, time.AddSeconds(30), retry); Check(tries == 2, "失败后重试成功", results);
        gate = new LaunchGate(); gate.Tick(true, time, launch); Check(calls == 3, "更换配置后可重新触发", results);
        string temporary = Path.Combine(Path.GetTempPath(), "ProgramLinkTest-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var config = new LinkSettings { WatchPath = @"C:\中文 空格\程序A.exe", TargetPath = @"D:\工具\程序B.exe", RequireWindow = false };
            config.Save(temporary); var loaded = LinkSettings.Load(temporary);
            Check(loaded.WatchPath == config.WatchPath && loaded.TargetPath == config.TargetPath && !loaded.RequireWindow, "中文及空格路径保存后正确读取", results);
            config.RequireWindow = true; config.Save(temporary); Check(LinkSettings.Load(temporary).RequireWindow, "已有配置安全替换", results);
            bool rejected = false; try { new LinkSettings().Validate(); } catch { rejected = true; }
            Check(rejected, "拒绝空配置", results);
            rejected = false; try { new LinkSettings { WatchPath = Application.ExecutablePath, TargetPath = Application.ExecutablePath }.Validate(); } catch { rejected = true; }
            Check(rejected, "拒绝 A、B 相同或助手自身", results);
            Check(LinkRuntime.Find(Application.ExecutablePath).Contains((uint)Process.GetCurrentProcess().Id), "按所选路径匹配当前会话进程", results);
            Check(!File.Exists(temporary + ".tmp"), "保存不残留临时配置", results);
        }
        finally { foreach (string suffix in new string[] { "", ".bak", ".tmp" }) if (File.Exists(temporary + suffix)) File.Delete(temporary + suffix); }
        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "自测结果.txt"), results.ToArray());
    }
}
