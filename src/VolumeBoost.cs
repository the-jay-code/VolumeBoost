// Volume Boost - VLC-style volume boost (100% to 400%) for quiet laptop speakers.
// Drives Equalizer APO: writes its own VolumeBoost.txt and includes it from config.txt; lives in the system tray.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const string BoostFileName = "VolumeBoost.txt";
    const string IncludeLine = "Include: " + BoostFileName;
    static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeBoost", "state.txt");

    static string ConfigDir;
    static EventWaitHandle showSignal;

    [STAThread]
    static int Main(string[] args)
    {
        ConfigDir = FindConfigDir();

        // Elevated helper mode: give normal users write access to Equalizer APO's config folder
        if (args.Length > 0 && args[0] == "--grant-access")
            return GrantAccess() ? 0 : 1;

        bool isFirst;
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "VolumeBoost.Show", out isFirst);
        if (!isFirst) { showSignal.Set(); return 0; } // already running: just pop the window up

        Application.EnableVisualStyles();
        if (!EnsureSetup()) return 1;
        Application.Run(new BoostForm(showSignal));
        return 0;
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

    // Checks Equalizer APO is installed, we can write its config, and config.txt includes our file
    static bool EnsureSetup()
    {
        if (!Directory.Exists(ConfigDir))
        {
            if (MessageBox.Show("Volume Boost needs Equalizer APO (free) to amplify your speakers.\n\n" +
                    "Install it, tick your speakers in its Configurator, restart your PC, then run Volume Boost again.\n\n" +
                    "Open the Equalizer APO download page now?", "Volume Boost",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("https://sourceforge.net/projects/equalizerapo/");
            return false;
        }

        if (!CanWrite())
        {
            if (MessageBox.Show("Volume Boost needs a one-time permission to change Equalizer APO's settings.\n\n" +
                    "Windows will ask for admin approval. Continue?", "Volume Boost",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return false;
            try
            {
                var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--grant-access")
                    { Verb = "runas", UseShellExecute = true });
                p.WaitForExit();
            }
            catch { return false; } // user said no to the UAC prompt
            if (!CanWrite())
            {
                MessageBox.Show("Still can't write to:\n" + ConfigDir, "Volume Boost", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        try
        {
            string config = Path.Combine(ConfigDir, "config.txt");
            string text = File.Exists(config) ? File.ReadAllText(config) : "";
            if (text.IndexOf(IncludeLine, StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (text.Length > 0 && !text.EndsWith("\n")) text += "\r\n";
                File.WriteAllText(config, text + IncludeLine + "\r\n");
            }
        }
        catch (Exception e)
        {
            MessageBox.Show("Couldn't update Equalizer APO's config.txt:\n" + e.Message, "Volume Boost",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    static bool CanWrite()
    {
        try
        {
            string probe = Path.Combine(ConfigDir, ".volumeboost-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static bool GrantAccess()
    {
        try
        {
            // S-1-5-32-545 = BUILTIN\Users, by SID so it works on non-English Windows
            var p = Process.Start(new ProcessStartInfo("icacls",
                "\"" + ConfigDir + "\" /grant *S-1-5-32-545:(OI)(CI)M") { CreateNoWindow = true, UseShellExecute = false });
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void ApplyBoost(int percent, bool on, bool saveState = true)
    {
        double db = on ? Math.Round(20 * Math.Log10(percent / 100.0), 2) : 0;
        string text = "# Written by Volume Boost - changes here get overwritten\r\nPreamp: " +
            db.ToString("+0.##;-0.##;0", System.Globalization.CultureInfo.InvariantCulture) + " dB\r\n";
        string path = Path.Combine(ConfigDir, BoostFileName);
        for (int i = 0; i < 5; i++)
        {
            try { File.WriteAllText(path, text); break; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { MessageBox.Show("Can't write Equalizer APO's config file:\n" + path); return; }
        }
        if (!saveState) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, percent + " " + (on ? "on" : "off"));
        }
        catch { }
    }

    public static void LoadState(out int percent, out bool on)
    {
        percent = 200; on = true;
        try
        {
            string[] parts = File.ReadAllText(StatePath).Trim().Split(' ');
            percent = Math.Max(100, Math.Min(400, int.Parse(parts[0])));
            on = parts.Length < 2 || parts[1] != "off";
        }
        catch { }
    }
}

// Colours follow the Windows light/dark app setting
class Theme
{
    public Color Back, Text, Muted, Track, Accent, Warm;

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
        t.Accent = Color.FromArgb(59, 130, 246);
        t.Warm = Color.FromArgb(245, 158, 11);
        if (light)
        {
            t.Back = Color.FromArgb(250, 250, 250); t.Text = Color.FromArgb(24, 24, 27);
            t.Muted = Color.FromArgb(113, 113, 122); t.Track = Color.FromArgb(222, 222, 226);
        }
        else
        {
            t.Back = Color.FromArgb(28, 28, 30); t.Text = Color.FromArgb(244, 244, 245);
            t.Muted = Color.FromArgb(150, 150, 158); t.Track = Color.FromArgb(58, 58, 62);
        }
        t.IsDark = !light;
        return t;
    }
    public bool IsDark;
}

// Flat slider: thin rounded track, coloured fill, round thumb. Snaps to 5%.
class BoostSlider : Control
{
    public const int Min = 100, Max = 400;
    int value = 200;
    bool dragging;
    public Color Fill, Track, Thumb;
    public event EventHandler ValueChanged;

    public BoostSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public int Value
    {
        get { return value; }
        set
        {
            int v = Math.Max(Min, Math.Min(Max, (int)Math.Round(value / 5.0) * 5));
            if (v == this.value) return;
            this.value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    int Radius { get { return Height / 3; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int r = Radius, cy = Height / 2, left = r, right = Width - r;
        float th = Math.Max(4f, Height / 7f);
        float x = left + (right - left) * (value - Min) / (float)(Max - Min);
        using (var b = new SolidBrush(Track)) FillPill(g, b, left, cy - th / 2, right - left, th);
        using (var b = new SolidBrush(Fill)) FillPill(g, b, left, cy - th / 2, x - left, th);
        using (var b = new SolidBrush(Thumb)) g.FillEllipse(b, x - r, cy - r, 2 * r, 2 * r);
        using (var p = new Pen(Fill, Math.Max(2f, r / 3f))) g.DrawEllipse(p, x - r + 1, cy - r + 1, 2 * r - 2, 2 * r - 2);
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Color.FromArgb(90, Fill), 2)) g.DrawEllipse(p, x - r - 3, cy - r - 3, 2 * r + 6, 2 * r + 6);
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
        int r = Radius;
        Value = Min + (int)Math.Round((x - r) * (Max - Min) / (double)Math.Max(1, Width - 2 * r));
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); dragging = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragging) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 5 : -5; }
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
            case Keys.Left: case Keys.Down: Value -= 5; break;
            case Keys.Right: case Keys.Up: Value += 5; break;
            case Keys.PageDown: Value -= 25; break;
            case Keys.PageUp: Value += 25; break;
            case Keys.Home: Value = Min; break;
            case Keys.End: Value = Max; break;
        }
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
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public bool Checked
    {
        get { return on; }
        set
        {
            if (value == on) return;
            on = value;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float h = Height - 2, w = Width - 2, pad = h / 6f, k = h - 2 * pad;
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            path.AddArc(1, 1, h, h, 90, 180);
            path.AddArc(1 + w - h, 1, h, h, 270, 180);
            path.CloseFigure();
            using (var b = new SolidBrush(on ? OnColor : OffColor)) g.FillPath(b, path);
        }
        float kx = on ? 1 + w - h + pad : 1 + pad;
        using (var b = new SolidBrush(Knob)) g.FillEllipse(b, kx, 1 + pad, k, k);
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Color.FromArgb(120, OnColor), 2)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Checked = !Checked; }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
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
    bool exiting;

    public BoostForm(EventWaitHandle showSignal)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Volume Boost";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(380, 190);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = theme.Back;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 9F);

        big.Font = new Font("Segoe UI Light", 36F);
        big.AutoSize = true;
        big.Location = new Point(18, 14);
        big.ForeColor = theme.Text;
        sub.Font = new Font("Segoe UI", 9.75F);
        sub.AutoSize = true;
        sub.Location = new Point(25, 88);

        toggle.Size = new Size(46, 26);
        toggle.Location = new Point(310, 36);
        toggle.OffColor = theme.Track;
        toggle.Knob = Color.White;

        slider.Size = new Size(340, 30);
        slider.Location = new Point(20, 122);
        slider.Track = theme.Track;
        slider.Thumb = theme.Back;

        var min = MakeLabel("100%", new Font("Segoe UI", 8.25F), theme.Muted, 24, 156);
        var max = MakeLabel("400%", new Font("Segoe UI", 8.25F), theme.Muted, 0, 160);
        max.Location = new Point(356 - TextRenderer.MeasureText(max.Text, max.Font).Width + 4, 156);

        Controls.AddRange(new Control[] { big, sub, toggle, slider, min, max });

        int percent; bool on;
        Program.LoadState(out percent, out on);
        slider.Value = percent;
        toggle.Checked = on;
        Program.ApplyBoost(percent, on);
        RefreshView();

        // Debounce so dragging doesn't rewrite the file dozens of times a second
        debounce.Interval = 120;
        debounce.Tick += (s, e) => { debounce.Stop(); Program.ApplyBoost(slider.Value, toggle.Checked); };
        slider.ValueChanged += (s, e) => { if (!toggle.Checked) toggle.Checked = true; RefreshView(); debounce.Stop(); debounce.Start(); };
        toggle.CheckedChanged += (s, e) => { RefreshView(); Program.ApplyBoost(slider.Value, toggle.Checked); };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => ShowWindow());
        trayToggle.Click += (s, e) => toggle.Checked = !toggle.Checked;
        menu.Items.Add(trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (turns boost off)", null, (s, e) => { exiting = true; Close(); });
        tray.Icon = Icon;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        RefreshView();

        // Another launch of the app signals us to pop up
        ThreadPool.RegisterWaitForSingleObject(showSignal, (st, timedOut) =>
        {
            try { BeginInvoke((Action)ShowWindow); } catch { }
        }, null, -1, false);
    }

    Label MakeLabel(string text, Font font, Color color, int x, int y)
    {
        return new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Location = new Point(x, y) };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = theme.IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { } // dark title bar on Windows 10 20H1+ / 11
    }

    void RefreshView()
    {
        bool on = toggle.Checked;
        int v = slider.Value;
        Color accent = v > WarnAbove ? theme.Warm : theme.Accent;
        double db = 20 * Math.Log10(v / 100.0);

        big.Text = v + "%";
        big.ForeColor = on ? theme.Text : theme.Muted;
        if (!on) { sub.Text = "Off  \u00B7  normal volume"; sub.ForeColor = theme.Muted; }
        else if (v > WarnAbove) { sub.Text = "+" + db.ToString("0.0") + " dB  \u00B7  may crackle on loud sounds"; sub.ForeColor = theme.Warm; }
        else { sub.Text = "+" + db.ToString("0.0") + " dB louder"; sub.ForeColor = theme.Muted; }

        slider.Fill = on ? accent : theme.Muted;
        toggle.OnColor = accent;
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
                tray.ShowBalloonTip(3000, "Volume Boost is still on", "It keeps running here. Right-click to exit.", ToolTipIcon.None);
            }
            return;
        }
        Program.ApplyBoost(slider.Value, false, false); // remember the level for next launch
        tray.Visible = false;
        base.OnFormClosing(e);
    }
    bool toldAboutTray;
}
