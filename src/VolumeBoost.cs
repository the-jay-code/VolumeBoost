// Volume Boost - VLC-style volume boost (100% to 400%) for quiet laptop speakers.
// Drives Equalizer APO: writes its own VolumeBoost.txt and includes it from config.txt; lives in the system tray.
// Runs as administrator (see app.manifest) so Equalizer APO's config folder never has to be opened up to other users.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const string BoostFileName = "VolumeBoost.txt";
    const string IncludeLine = "Include: " + BoostFileName;
    const string TaskName = "VolumeBoost";
    static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeBoost", "state.txt");
    static readonly string Schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public enum ApplyResult { Ok, Busy, Denied, Error }

    // Set when SetStartup refuses; the UI shows it instead of a generic message
    public static string StartupBlockReason;

    static string ConfigDir;
    static EventWaitHandle showSignal;

    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetDefaultDllDirectories(uint directoryFlags);
    const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

    [STAThread]
    static int Main(string[] args)
    {
        Harden();
        ConfigDir = FindConfigDir();
        string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        // For scripts: VolumeBoost.exe --startup on|off  (anything else is rejected, not silently treated as off)
        if (arg == "--startup")
        {
            string v = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (v != "on" && v != "off") return 2; // usage error, so a typo like "--startup yes" can't turn it off
            return SetStartup(v == "on") ? 0 : 1;
        }

        bool isFirst;
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName(), out isFirst);
        if (!isFirst) { showSignal.Set(); return 0; } // already running: just pop the window up

        Application.EnableVisualStyles();
        if (!EnsureSetup()) return 1;
        Application.Run(new BoostForm(showSignal, arg == "--tray"));
        return 0;
    }

    // Defense in depth for an elevated process: load system DLLs only from System32 (no DLL planting next to the
    // exe), and clear CLR profiler / AppDomainManager environment hooks so they can't be inherited by child processes.
    static void Harden()
    {
        try { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); } catch { }
        string[] vars = { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COR_PROFILER_PATH_32",
            "COR_PROFILER_PATH_64", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
            "APPDOMAIN_MANAGER_ASM", "APPDOMAIN_MANAGER_TYPE" };
        foreach (string v in vars)
            try { Environment.SetEnvironmentVariable(v, null); } catch { }
    }

    // The single-instance event is per-user, so another user's process can't block or hijack our startup
    static string ShowEventName()
    {
        try { return "VolumeBoost.Show." + WindowsIdentity.GetCurrent().User.Value; }
        catch { return "VolumeBoost.Show"; }
    }

    static string FindConfigDir()
    {
        string install = null;
        try
        {
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                        .OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                if (key != null) install = key.GetValue("InstallPath") as string;
        }
        catch { }
        if (string.IsNullOrEmpty(install))
            install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
        return Path.Combine(install, "config");
    }

    // Checks Equalizer APO is installed and config.txt includes our file
    static bool EnsureSetup()
    {
        if (!Directory.Exists(ConfigDir))
        {
            if (MessageBox.Show("Volume Boost needs Equalizer APO (free) to amplify your speakers.\n\n" +
                    "Install it, tick your speakers in its Configurator, restart your PC, then run Volume Boost again.\n\n" +
                    "Open the Equalizer APO download page now?", "Volume Boost",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenLink("https://sourceforge.net/projects/equalizerapo/");
            return false;
        }

        try
        {
            string config = Path.Combine(ConfigDir, "config.txt");
            // Read with the file's own encoding so non-English text in the user's config is never re-encoded
            Encoding enc = new UTF8Encoding(false);
            string text = "";
            if (File.Exists(config))
                using (var r = new StreamReader(config, Encoding.Default, true)) { text = r.ReadToEnd(); enc = r.CurrentEncoding; }

            bool included = false;
            foreach (string line in text.Split('\n'))
                if (Regex.IsMatch(line.Trim(), @"^Include\s*:\s*VolumeBoost\.txt$", RegexOptions.IgnoreCase))
                { included = true; break; } // tolerate extra spaces so we never add a second include (double boost)

            if (!included)
            {
                // Append only - never rewrite the whole file - so the user's existing settings and encoding stay intact.
                // "Device: all" makes the boost apply to every device even if the file ends inside a device section.
                string add = (text.Length > 0 && !text.EndsWith("\n")) ? "\r\n" : "";
                add += "Device: all\r\n" + IncludeLine + "\r\n";
                File.AppendAllText(config, add, enc);
            }
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Volume Boost couldn't update Equalizer APO's config.txt because Windows denied access.\n\n" +
                "Make sure you ran Volume Boost as administrator (click Yes on the \"allow changes\" prompt).",
                "Volume Boost", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        catch (Exception)
        {
            MessageBox.Show("Volume Boost couldn't update Equalizer APO's config.txt.\n\n" +
                "Check that Equalizer APO is installed correctly and try again.", "Volume Boost",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    // Returns how the write went so the UI can say exactly what's wrong
    public static ApplyResult ApplyBoost(int percent, bool on, bool saveState = true)
    {
        double db = on ? Math.Round(20 * Math.Log10(percent / 100.0), 2) : 0;
        string text = "# Written by Volume Boost - changes here get overwritten\r\nPreamp: " +
            db.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " dB\r\n";
        string path = Path.Combine(ConfigDir, BoostFileName);

        ApplyResult result = ApplyResult.Error;
        for (int i = 0; i < 5; i++)
        {
            try { AtomicWrite(path, text); result = ApplyResult.Ok; break; }
            catch (UnauthorizedAccessException) { result = ApplyResult.Denied; break; }
            catch (IOException) { result = ApplyResult.Busy; Thread.Sleep(50); }
        }
        if (result != ApplyResult.Ok) return result;

        if (saveState)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, percent + " " + (on ? "on" : "off"));
            }
            catch { }
        }
        return ApplyResult.Ok;
    }

    // Write to a temp file in the same folder, then swap it in, so a crash or power loss can't leave an empty config.
    // File.Replace keeps the destination's permissions (it stays owned by Administrators).
    static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static void LoadState(out int percent, out bool on)
    {
        percent = 200; on = true; // first run: a gentle 2x boost
        try
        {
            if (!File.Exists(StatePath)) return;
            string[] parts = File.ReadAllText(StatePath).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int p;
            if (parts.Length > 0 && int.TryParse(parts[0], out p)) percent = Math.Max(100, Math.Min(400, p));
            on = parts.Length < 2 || !parts[1].Equals("off", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
    }

    public static bool StartupEnabled()
    {
        return RunHidden(Schtasks, "/query /tn " + TaskName) == 0;
    }

    // Only folders under Program Files / Windows are writable by admins alone. Running the elevated startup task
    // from anywhere else (e.g. Downloads) would let any non-admin program swap the exe and run as admin at sign-in.
    static bool IsExeLocationSafe()
    {
        try
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string[] safe =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            foreach (string root in safe)
                if (!string.IsNullOrEmpty(root) && IsUnder(dir, root)) return true;
        }
        catch { }
        return false;
    }

    static bool IsUnder(string child, string parent)
    {
        try
        {
            string c = Path.GetFullPath(child).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // A logon task with highest privileges starts the app in the tray without a UAC prompt each sign-in
    public static bool SetStartup(bool enable)
    {
        StartupBlockReason = null;
        if (!enable) return RunHidden(Schtasks, "/delete /tn " + TaskName + " /f") == 0 || !StartupEnabled();

        if (!IsExeLocationSafe())
        {
            StartupBlockReason =
                "To start with Windows safely, keep Volume Boost in a protected folder.\n\n" +
                "Make a folder at C:\\Program Files\\VolumeBoost, move VolumeBoost.exe there, run it from there, " +
                "then turn on Start with Windows.\n\n" +
                "(Starting an admin app from Downloads or the Desktop would let other programs tamper with it.)";
            return false;
        }

        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>Starts Volume Boost in the tray when you sign in.</Description></RegistrationInfo>\r\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType>" +
            "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) +
            "</Command><Arguments>--tray</Arguments></Exec></Actions>\r\n" +
            "</Task>\r\n";

        // Register through the Task Scheduler COM API, so there's no temp XML file for another process to swap out.
        return RegisterLogonTask(xml);
    }

    const int TASK_CREATE_OR_UPDATE = 6;
    const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    static bool RegisterLogonTask(string xml)
    {
        object service = null, folder = null;
        try
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return false;
            service = Activator.CreateInstance(t);
            Invoke(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            folder = Invoke(service, "GetFolder", "\\");
            Invoke(folder, "RegisterTask", TaskName, xml, TASK_CREATE_OR_UPDATE, null, null,
                TASK_LOGON_INTERACTIVE_TOKEN, null);
            return StartupEnabled();
        }
        catch { return false; }
        finally
        {
            if (folder != null) try { Marshal.ReleaseComObject(folder); } catch { }
            if (service != null) try { Marshal.ReleaseComObject(service); } catch { }
        }
    }

    static object Invoke(object com, string method, params object[] args)
    {
        return com.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, com, args);
    }

    static int RunHidden(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false });
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return -1; }
    }

    // Open a web link through the normal shell so the browser doesn't inherit our admin rights
    public static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true }); } catch { }
    }
}

// Colours follow the Windows light/dark app setting
class Theme
{
    public Color Back, Text, Muted, Track, ToggleOff, Accent, Warm, WarmText;
    public bool IsDark;

    public static Theme Current()
    {
        bool light = true;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                if (key != null && key.GetValue("AppsUseLightTheme") is int) light = (int)key.GetValue("AppsUseLightTheme") != 0;
        }
        catch { }
        var t = new Theme();
        t.IsDark = !light;
        t.Accent = Color.FromArgb(59, 130, 246);
        t.Warm = Color.FromArgb(245, 158, 11);
        if (light)
        {
            t.Back = Color.FromArgb(250, 250, 250); t.Text = Color.FromArgb(24, 24, 27);
            t.Muted = Color.FromArgb(90, 90, 98); t.Track = Color.FromArgb(200, 200, 206);
            t.ToggleOff = Color.FromArgb(158, 158, 166); t.WarmText = Color.FromArgb(180, 83, 9);
        }
        else
        {
            t.Back = Color.FromArgb(28, 28, 30); t.Text = Color.FromArgb(244, 244, 245);
            t.Muted = Color.FromArgb(160, 160, 168); t.Track = Color.FromArgb(58, 58, 62);
            t.ToggleOff = Color.FromArgb(92, 92, 100); t.WarmText = t.Warm;
        }
        return t;
    }
}

// Flat slider: thin rounded track, coloured fill, round thumb. Snaps to 5%.
class BoostSlider : Control
{
    public const int Min = 100, Max = 400;
    const int Pad = 4; // room for the focus ring at both ends
    int value = 200;
    bool dragging;
    public Color Fill, Track, Thumb;
    public event EventHandler ValueChanged;
    public event EventHandler UserInput; // any user action, even one that doesn't change the value

    public BoostSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = Text = "Boost level " + value + " percent";
    }

    public int Value
    {
        get { return value; }
        set
        {
            int v = Math.Max(Min, Math.Min(Max, (int)Math.Round(value / 5.0) * 5));
            if (v == this.value) return;
            this.value = v;
            AccessibleName = Text = "Boost level " + v + " percent"; // Text is what UI Automation reads
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    void RaiseInput() { if (UserInput != null) UserInput(this, EventArgs.Empty); }

    protected override AccessibleObject CreateAccessibilityInstance() { return new Acc(this); }
    class Acc : Control.ControlAccessibleObject
    {
        BoostSlider s;
        public Acc(BoostSlider o) : base(o) { s = o; }
        public override AccessibleRole Role { get { return AccessibleRole.Slider; } }
        public override string Value { get { return s.Value + " percent"; } set { } }
    }

    int Radius { get { return Height / 3; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int r = Radius, cy = Height / 2, left = r + Pad, right = Width - r - Pad;
        float th = Math.Max(4f, Height / 7f);
        float x = left + (right - left) * (value - Min) / (float)(Max - Min);
        using (var b = new SolidBrush(Track)) FillPill(g, b, left, cy - th / 2, right - left, th);
        using (var b = new SolidBrush(Fill)) FillPill(g, b, left, cy - th / 2, x - left, th);
        using (var b = new SolidBrush(Thumb)) g.FillEllipse(b, x - r, cy - r, 2 * r, 2 * r);
        using (var p = new Pen(Fill, Math.Max(2f, r / 3f))) g.DrawEllipse(p, x - r + 1, cy - r + 1, 2 * r - 2, 2 * r - 2);
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Color.FromArgb(220, Fill), 2)) g.DrawEllipse(p, x - r - 3, cy - r - 3, 2 * r + 6, 2 * r + 6);
    }

    static void FillPill(Graphics g, Brush b, float x, float y, float w, float h)
    {
        if (w <= 0) return;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            float d = Math.Min(h, w);
            path.AddArc(x, y, d, h, 90, 180);
            path.AddArc(x + w - d, y, d, h, 270, 180);
            path.CloseFigure();
            g.FillPath(b, path);
        }
    }

    void SetFromX(int x)
    {
        int r = Radius + Pad;
        Value = Min + (int)Math.Round((x - r) * (Max - Min) / (double)Math.Max(1, Width - 2 * r));
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); dragging = true; SetFromX(e.X); RaiseInput(); }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragging) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 5 : -5; RaiseInput(); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k)
    {
        return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value -= 5; RaiseInput(); break;
            case Keys.Right: case Keys.Up: Value += 5; RaiseInput(); break;
            case Keys.PageDown: Value -= 25; RaiseInput(); break;
            case Keys.PageUp: Value += 25; RaiseInput(); break;
            case Keys.Home: Value = Min; RaiseInput(); break;
            case Keys.End: Value = Max; RaiseInput(); break;
        }
        base.OnKeyDown(e);
    }
}

// Pill-shaped on/off switch
class Toggle : Control
{
    bool on;
    public Color OnColor, OffColor, Knob;
    public event EventHandler CheckedChanged;

    public Toggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.StandardDoubleClick, false); // fast clicks each count as a click
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleName = Text = "Boost off";
    }

    public bool Checked
    {
        get { return on; }
        set
        {
            if (value == on) return;
            on = value;
            AccessibleName = Text = on ? "Boost on" : "Boost off";
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    // Report the checked state to screen readers (Narrator says "on"/"off"), not just the name
    protected override AccessibleObject CreateAccessibilityInstance() { return new Acc(this); }
    class Acc : Control.ControlAccessibleObject
    {
        Toggle t;
        public Acc(Toggle o) : base(o) { t = o; }
        public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
        public override AccessibleStates State
        {
            get { var s = base.State; if (t.Checked) s |= AccessibleStates.Checked; return s; }
        }
        public override string DefaultAction { get { return t.Checked ? "Turn off" : "Turn on"; } }
        public override void DoDefaultAction() { t.Checked = !t.Checked; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float inset = 3, h = Height - 2 * inset, w = Width - 2 * inset, pad = h / 6f, k = h - 2 * pad;
        using (var path = Pill(inset, inset, w, h))
        using (var b = new SolidBrush(on ? OnColor : OffColor))
            g.FillPath(b, path);
        float kx = on ? inset + w - h + pad : inset + pad;
        using (var b = new SolidBrush(Knob)) g.FillEllipse(b, kx, inset + pad, k, k);
        if (Focused && ShowFocusCues)
            using (var path = Pill(1, 1, Width - 2, Height - 2))
            using (var p = new Pen(Color.FromArgb(220, OnColor), 2))
                g.DrawPath(p, path);
    }

    static System.Drawing.Drawing2D.GraphicsPath Pill(float x, float y, float w, float h)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, h, h, 90, 180);
        path.AddArc(x + w - h, y, h, h, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Checked = !Checked;
        else if (e.KeyCode == Keys.Right) Checked = true;
        else if (e.KeyCode == Keys.Left) Checked = false;
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

// Dark colours for the tray right-click menu so it matches the app and Windows in dark mode
class DarkMenuColors : ProfessionalColorTable
{
    readonly Color back, hover, border;
    public DarkMenuColors(Theme t) { back = t.Back; hover = Color.FromArgb(58, 58, 62); border = Color.FromArgb(70, 70, 74); }
    public override Color MenuItemSelected { get { return hover; } }
    public override Color MenuItemSelectedGradientBegin { get { return hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return hover; } }
    public override Color MenuItemBorder { get { return hover; } }
    public override Color MenuBorder { get { return border; } }
    public override Color ToolStripDropDownBackground { get { return back; } }
    public override Color ImageMarginGradientBegin { get { return back; } }
    public override Color ImageMarginGradientMiddle { get { return back; } }
    public override Color ImageMarginGradientEnd { get { return back; } }
    public override Color SeparatorDark { get { return border; } }
    public override Color SeparatorLight { get { return border; } }
    public override Color CheckBackground { get { return hover; } }
    public override Color CheckSelectedBackground { get { return hover; } }
}

class BoostForm : Form
{
    const int WarnAbove = 300; // past this, loud sounds start to clip on most speakers

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly Theme theme = Theme.Current();
    readonly BoostSlider slider = new BoostSlider();
    readonly Toggle toggle = new Toggle();
    readonly Label big = new Label(), sub = new Label();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
    readonly ToolStripMenuItem trayToggle = new ToolStripMenuItem();
    readonly ToolStripMenuItem trayStartup = new ToolStripMenuItem("Start with Windows");
    bool exiting, startHidden, toldAboutTray;
    Program.ApplyResult lastApply = Program.ApplyResult.Ok;

    public BoostForm(EventWaitHandle showSignal, bool startInTray)
    {
        startHidden = startInTray;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Volume Boost";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(380, 190);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false; // with both off, Windows hides the buttons instead of showing a greyed-out maximize
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = theme.Back;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9F);

        big.Font = new Font("Segoe UI Light", 36F);
        big.AutoSize = true;
        big.Location = new Point(18, 14);
        sub.Font = new Font("Segoe UI", 9.75F);
        sub.AutoSize = true;
        sub.Location = new Point(25, 88);

        toggle.Size = new Size(52, 32);
        toggle.Location = new Point(305, 33);
        toggle.OffColor = theme.ToggleOff;
        toggle.Knob = Color.White;

        slider.Size = new Size(348, 30);
        slider.Location = new Point(16, 122);
        slider.Track = theme.Track;
        slider.Thumb = theme.Back;

        var min = new Label { Text = "100%", Font = new Font("Segoe UI", 8.25F), ForeColor = theme.Muted,
                              AutoSize = true, Location = new Point(24, 156) };
        var max = new Label { Text = "400%", Font = new Font("Segoe UI", 8.25F), ForeColor = theme.Muted,
                              AutoSize = false, TextAlign = ContentAlignment.TopRight, Size = new Size(80, 20),
                              Location = new Point(280, 156) };

        Controls.AddRange(new Control[] { big, sub, toggle, slider, min, max });

        int percent; bool on;
        Program.LoadState(out percent, out on);
        slider.Value = percent;
        toggle.Checked = on;
        Apply();

        // Debounce so dragging doesn't rewrite the file dozens of times a second
        debounce.Interval = 120;
        debounce.Tick += (s, e) => { debounce.Stop(); Apply(); };
        slider.ValueChanged += (s, e) => { if (!toggle.Checked) toggle.Checked = true; RefreshView(); debounce.Stop(); debounce.Start(); };
        // Touching the slider while off turns the boost on, even at 400% where the value can't go higher
        slider.UserInput += (s, e) => { if (!toggle.Checked) toggle.Checked = true; };
        toggle.CheckedChanged += (s, e) => { debounce.Stop(); Apply(); };

        var menu = new ContextMenuStrip();
        if (theme.IsDark)
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors(theme));
            menu.BackColor = theme.Back;
            menu.ForeColor = theme.Text;
        }
        menu.Items.Add("Open", null, (s, e) => ShowWindow());
        trayToggle.Click += (s, e) => toggle.Checked = !toggle.Checked;
        menu.Items.Add(trayToggle);
        trayStartup.Click += (s, e) =>
        {
            bool want = !trayStartup.Checked;
            if (!Program.SetStartup(want))
                MessageBox.Show(Program.StartupBlockReason ??
                    ("Couldn't " + (want ? "turn on" : "turn off") + " Start with Windows. Please try again."),
                    "Volume Boost", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            trayStartup.Checked = Program.StartupEnabled();
        };
        menu.Items.Add(trayStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (turns boost off)", null, (s, e) => { exiting = true; Close(); });
        menu.Opening += (s, e) => trayStartup.Checked = Program.StartupEnabled();
        tray.Icon = Icon;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        RefreshView();
        ResumeLayout(false);

        // Another launch of the app signals us to pop up
        ThreadPool.RegisterWaitForSingleObject(showSignal, (st, timedOut) =>
        {
            try { BeginInvoke((Action)ShowWindow); } catch { }
        }, null, -1, false);
    }

    void Apply()
    {
        lastApply = Program.ApplyBoost(slider.Value, toggle.Checked);
        RefreshView();
    }

    // --tray: create the window but keep it hidden until opened from the tray
    protected override void SetVisibleCore(bool value)
    {
        if (startHidden)
        {
            startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = theme.IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { } // dark title bar on Windows 10 20H1+ / 11
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } // goes to the tray, like the X
        base.OnKeyDown(e);
    }

    void RefreshView()
    {
        bool on = toggle.Checked;
        int v = slider.Value;
        bool warm = on && v > WarnAbove;
        double db = 20 * Math.Log10(v / 100.0);

        big.Text = on ? v + "%" : "Off";
        big.ForeColor = on ? theme.Text : theme.Muted;
        sub.ForeColor = theme.Muted;
        if (lastApply == Program.ApplyResult.Denied)
        { sub.Text = "Couldn't apply \u00B7 run Volume Boost as administrator"; sub.ForeColor = theme.WarmText; }
        else if (lastApply == Program.ApplyResult.Busy)
        { sub.Text = "Couldn't apply \u00B7 Equalizer APO's file is busy, try again"; sub.ForeColor = theme.WarmText; }
        else if (lastApply == Program.ApplyResult.Error)
        { sub.Text = "Couldn't apply \u00B7 couldn't write Equalizer APO's file"; sub.ForeColor = theme.WarmText; }
        else if (!on) sub.Text = "Normal volume \u00B7 switch on to boost to " + v + "%";
        else if (v == 100) sub.Text = "Normal volume \u00B7 drag right to boost";
        else if (warm) { sub.Text = "+" + db.ToString("0.0") + " dB \u00B7 may crackle on loud sounds"; sub.ForeColor = theme.WarmText; }
        else sub.Text = "+" + db.ToString("0.0") + " dB louder";

        slider.Fill = on ? (warm ? theme.Warm : theme.Accent) : theme.ToggleOff;
        toggle.OnColor = warm ? theme.Warm : theme.Accent;
        slider.Invalidate();
        toggle.Invalidate();

        trayToggle.Text = on ? "Turn boost off" : "Turn boost on";
        tray.Text = "Volume Boost - " + (on ? v + "%" : "off");
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!toldAboutTray)
            {
                toldAboutTray = true;
                tray.ShowBalloonTip(3000, "Volume Boost is still on", "Find it in the tray (click ^ next to the clock). Right-click it to exit.", ToolTipIcon.None);
            }
            return;
        }
        // Exit from the menu remembers "off"; a Windows shutdown keeps the boost for next sign-in
        Program.ApplyBoost(slider.Value, false, exiting);
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}
