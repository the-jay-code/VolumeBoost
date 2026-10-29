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

class BoostForm : Form
{
    readonly TrackBar slider = new TrackBar();
    readonly Label label = new Label();
    readonly CheckBox onBox = new CheckBox();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
    readonly ToolStripMenuItem trayToggle = new ToolStripMenuItem();
    bool exiting;

    public BoostForm(EventWaitHandle showSignal)
    {
        Text = "Volume Boost";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(420, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        label.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        label.AutoSize = true;
        label.Location = new Point(15, 10);

        onBox.Text = "Boost on";
        onBox.Font = new Font("Segoe UI", 10);
        onBox.AutoSize = true;
        onBox.Location = new Point(310, 16);

        slider.Minimum = 100;
        slider.Maximum = 400;
        slider.TickFrequency = 50;
        slider.SmallChange = 5;
        slider.LargeChange = 25;
        slider.Width = 390;
        slider.Location = new Point(15, 55);

        var hint = new Label();
        hint.Text = "100%        150%        200%        250%        300%        350%        400%";
        hint.AutoSize = true;
        hint.ForeColor = Color.Gray;
        hint.Location = new Point(15, 100);

        var note = new Label();
        note.Text = "Closing this window keeps the boost running in the tray (by the clock).";
        note.AutoSize = true;
        note.ForeColor = Color.Gray;
        note.Location = new Point(15, 124);

        Controls.AddRange(new Control[] { label, onBox, slider, hint, note });

        int percent; bool on;
        Program.LoadState(out percent, out on);
        slider.Value = percent;
        onBox.Checked = on;
        Program.ApplyBoost(percent, on);
        RefreshText();

        // Debounce so dragging doesn't rewrite the file dozens of times a second
        debounce.Interval = 120;
        debounce.Tick += (s, e) => { debounce.Stop(); Program.ApplyBoost(slider.Value, onBox.Checked); };
        slider.ValueChanged += (s, e) => { if (!onBox.Checked) onBox.Checked = true; RefreshText(); debounce.Stop(); debounce.Start(); };
        onBox.CheckedChanged += (s, e) => { RefreshText(); Program.ApplyBoost(slider.Value, onBox.Checked); };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => ShowWindow());
        trayToggle.Click += (s, e) => onBox.Checked = !onBox.Checked;
        menu.Items.Add(trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (turns boost off)", null, (s, e) => { exiting = true; Close(); });
        tray.Icon = Icon;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        RefreshText();

        // Another launch of the app signals us to pop up
        ThreadPool.RegisterWaitForSingleObject(showSignal, (st, timedOut) =>
        {
            try { BeginInvoke((Action)ShowWindow); } catch { }
        }, null, -1, false);
    }

    void RefreshText()
    {
        label.Text = onBox.Checked ? "Boost: " + slider.Value + "%" : "Boost: off";
        trayToggle.Text = onBox.Checked ? "Turn boost off" : "Turn boost on";
        tray.Text = "Volume Boost - " + (onBox.Checked ? slider.Value + "%" : "off");
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
            return;
        }
        Program.ApplyBoost(slider.Value, false, false); // remember the level for next launch
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}
