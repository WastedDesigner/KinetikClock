// Kinetik Clock: a world clock desktop widget in the style of Kinetik's widget and Kinetik Calendar.
// Shows the time in any number of places, each with a small analog dial that turns dark at night.
// Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kinetik Clock")]
[assembly: AssemblyProduct("Kinetik Clock")]
[assembly: AssemblyCopyright("Copyright © 2026 Akila Sella Hennedige")]
[assembly: AssemblyVersion("1.0.5.0")]

static class Program
{
    public const string Version = "1.0.5";
    // --demo: made-up places, nothing read from or saved to this PC. "--theme Name" picks its theme (for screenshots).
    public static bool Demo;
    public static string DemoTheme;
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        Demo = args.Contains("--demo");
        int ti = Array.IndexOf(args, "--theme");
        if (ti >= 0 && ti + 1 < args.Length) DemoTheme = args[ti + 1];
        bool created;
        using (new Mutex(true, Demo ? @"Local\KinetikClockDemo" : @"Local\KinetikClock", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = new DarkMenu();
            Backup.KeyName = "KinetikClock"; Backup.AppTitle = "Kinetik Clock";
            if (!Demo) Backup.OfferRestore();
            var app = new ClockForm();
            using (new KinetikSuite.Shutdown("KinetikClock" + (Demo ? "Demo" : ""), () => { app.Close(); Application.Exit(); }))
                Application.Run(app);
        }
        if (!Demo) Backup.AfterExit();
    }
}

// ======================================================================= Settings
class Place
{
    public string Name, Zone; // Zone is a Windows time zone id; "" means this PC's own
    public TimeZoneInfo Tz
    {
        get
        {
            if (string.IsNullOrEmpty(Zone)) return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(Zone); } catch { return TimeZoneInfo.Local; }
        }
    }
}

class Settings
{
    const string KeyPath = @"Software\KinetikClock";
    public int X = int.MinValue, Y = int.MinValue, Width = 300, Scale = 100, Opacity = CommonMenu.DefaultOpacity, Layer = 0, Style = 0; // Style: 0 dials, 1 text only
    public bool Locked, Snap = true, Seconds, ShowOffset = true, ClickThrough;
    public bool Use24h = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
    public string ThemeName = "Midnight", FontName = ""; // "" = Segoe UI Variable Text
    public string Places = "Local=\nLondon=GMT Standard Time\nNew York=Eastern Standard Time\nTokyo=Tokyo Standard Time";

    public List<Place> List
    {
        get
        {
            return Places.Split('\n').Where(l => l.Contains('=')).Select(l =>
            {
                int i = l.IndexOf('=');
                return new Place { Name = l.Substring(0, i), Zone = l.Substring(i + 1) };
            }).ToList();
        }
        set { Places = string.Join("\n", value.Select(p => p.Name.Replace("=", "").Replace("\n", "") + "=" + p.Zone)); }
    }

    static IEnumerable<FieldInfo> Fields
    {
        get
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType == typeof(string));
        }
    }

    // Set when the saved settings exist but couldn't be read. The clock then runs on defaults, and Save does nothing,
    // so those defaults can never overwrite the real settings. Restarting the app tries again.
    public static bool Unreadable;

    // Reads the settings, retrying a few times: the key can be briefly unreadable, e.g. while a backup is being
    // restored into it or another program has it busy.
    public static Settings Load()
    {
        if (Program.Demo)
            return new Settings
            {
                Layer = 2, ThemeName = Program.DemoTheme ?? "Midnight", X = 240, Y = 160, ShowOffset = false,
                Places = "London=GMT Standard Time\nNew York=Eastern Standard Time\nTokyo=Tokyo Standard Time\nSydney=AUS Eastern Standard Time\nSan Francisco=Pacific Standard Time"
            };
        Settings s = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(500);
            bool ok;
            s = TryLoad(out ok);
            if (ok) { Unreadable = false; s.Opacity = CommonMenu.Migrate(s.Opacity); return s; }
        }
        Unreadable = true;
        MessageBox.Show("Kinetik Clock couldn't read its saved settings, so it's showing the default places for now.\n\n" +
            "Your settings haven't been changed, and nothing will be saved over them while this is showing. " +
            "Restart Kinetik Clock to try again" + (Backup.Exists ? ", or use right-click → Backup → Restore settings…" : "."),
            "Kinetik Clock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return s;
    }

    // ok is false when the settings are there but couldn't be read (a missing key is fine: first run).
    static Settings TryLoad(out bool ok)
    {
        ok = true;
        var s = new Settings();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                if (k != null)
                    foreach (var f in Fields)
                    {
                        var v = k.GetValue(f.Name);
                        if (v == null) continue;
                        try
                        {
                            if (f.FieldType == typeof(string)) f.SetValue(s, v.ToString());
                            else if (f.FieldType == typeof(int)) f.SetValue(s, Convert.ToInt32(v));
                            else f.SetValue(s, Convert.ToInt32(v) != 0);
                        }
                        catch { ok = false; }
                    }
        }
        catch { ok = false; }
        KinetikSuite.SettingsValidation.Sanitize(s); return s;
    }

    public void Save()
    {
        if (Program.Demo || Unreadable) return;
        KinetikSuite.SettingsValidation.Sanitize(this);
        if (KinetikSuite.RegistryFiles.CannotSave) return; // never save defaults over settings that couldn't be read
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                foreach (var f in Fields)
                {
                    var v = f.GetValue(this);
                    if (f.FieldType == typeof(bool)) k.SetValue(f.Name, (bool)v ? 1 : 0);
                    else if (f.FieldType == typeof(int)) k.SetValue(f.Name, (int)v);
                    else k.SetValue(f.Name, (string)v ?? "");
                }
        }
        catch { }
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Kinetik Clock";

    public static bool IsOn
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
        }
        catch { }
    }
}

// ======================================================================= Themes (same set as Kinetik Calendar)
class Theme
{
    public readonly string Name;
    public readonly Color Bg, Text, Sub, Accent;
    public readonly bool Light;

    public Theme(string name, Color bg, Color text, Color sub, Color accent, bool light)
    {
        Name = name; Bg = bg; Text = text; Sub = sub; Accent = accent; Light = light;
    }

    public Color Line { get { return Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    public static readonly Theme[] All =
    {
        new Theme("Midnight", C(22, 22, 26), C(242, 242, 246), C(168, 168, 180), C(240, 88, 88), false),
        new Theme("Navy", C(22, 27, 40), C(236, 240, 248), C(138, 150, 176), C(77, 166, 255), false),
        new Theme("Graphite", C(40, 40, 44), C(236, 236, 236), C(160, 160, 166), C(255, 159, 10), false),
        new Theme("Nord", C(46, 52, 64), C(236, 239, 244), C(152, 162, 182), C(136, 192, 208), false),
        new Theme("Dracula", C(40, 42, 54), C(248, 248, 242), C(150, 156, 192), C(255, 121, 198), false),
        new Theme("Solarized", C(0, 43, 54), C(238, 232, 213), C(131, 148, 150), C(203, 75, 22), false),
        new Theme("Forest", C(22, 33, 27), C(228, 240, 231), C(138, 166, 148), C(108, 201, 124), false),
        new Theme("Ocean", C(12, 29, 45), C(228, 242, 250), C(128, 160, 186), C(56, 198, 232), false),
        new Theme("Sunset", C(40, 23, 35), C(250, 236, 240), C(192, 150, 172), C(255, 138, 92), false),
        new Theme("Mono", C(10, 10, 10), C(245, 245, 245), C(140, 140, 140), C(245, 245, 245), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        if (name == "Auto")
        {
            bool light = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
            }
            catch { }
            return Get(light ? "Light" : "Midnight");
        }
        foreach (var t in All) if (t.Name == name) return t;
        return All[0];
    }
}

// ======================================================================= Drawing helpers
static class Draw
{
    public static string Family = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static readonly string[] FontChoices = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    public static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// A premultiplied-alpha bitmap backed by a DIB, pushed to a layered window with UpdateLayeredWindow (as in Kinetik).
sealed class DibSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND b, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    IntPtr memDC, dib, oldObj;
    Bitmap bmp;
    int w, h;

    public Bitmap Canvas(int width, int height)
    {
        if (dib != IntPtr.Zero && width == w && height == h) return bmp;
        Free();
        memDC = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr bits;
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        oldObj = SelectObject(memDC, dib);
        bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        w = width; h = height;
        return bmp;
    }

    public void Push(IntPtr hwnd, int x, int y)
    {
        var size = new SIZE { CX = w, CY = h };
        var src = new POINT();
        var pos = new POINT { X = x, Y = y };
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
    }

    void Free()
    {
        if (bmp != null) { bmp.Dispose(); bmp = null; }
        if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    public void Dispose() { Free(); }
}

// A card's soft shadow: stacked, slightly larger translucent shapes. Those anti-aliased fills over the whole card cost
// more than the rest of a frame put together, so they're drawn once into a bitmap and copied while nothing about them changes.
sealed class ShadowCache : IDisposable
{
    Bitmap bmp; string key;

    // Lays the shadow into the (cleared) w × h target. look sums up everything draw depends on.
    public void Paint(Graphics target, int w, int h, string look, Action<Graphics> draw)
    {
        var k = w + "x" + h + "|" + look + "|" + (int)target.SmoothingMode + (int)target.PixelOffsetMode;
        if (k != key || bmp == null)
        {
            if (bmp == null || bmp.Width != w || bmp.Height != h) { if (bmp != null) bmp.Dispose(); bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = target.SmoothingMode; g.PixelOffsetMode = target.PixelOffsetMode;
                draw(g);
            }
            key = k;
        }
        bmp.SetResolution(target.DpiX, target.DpiY); // so it's copied 1:1
        var mode = target.CompositingMode;
        target.CompositingMode = CompositingMode.SourceCopy;
        target.DrawImageUnscaled(bmp, 0, 0);
        target.CompositingMode = mode;
    }

    public void Dispose() { if (bmp != null) bmp.Dispose(); bmp = null; key = null; }
}

class DarkColors : ProfessionalColorTable
{
    static readonly Color Bg = Color.FromArgb(27, 31, 44), Hi = Color.FromArgb(40, 47, 66), Edge = Color.FromArgb(52, 60, 82);
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
    public override Color MenuItemPressedGradientBegin { get { return Hi; } }
    public override Color MenuItemPressedGradientEnd { get { return Hi; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return Edge; } }
    public override Color CheckBackground { get { return Color.FromArgb(52, 96, 160); } }
    public override Color CheckSelectedBackground { get { return Color.FromArgb(62, 110, 180); } }
    public override Color CheckPressedBackground { get { return Color.FromArgb(62, 110, 180); } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 236, 244) : Color.FromArgb(110, 118, 140);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Color.FromArgb(180, 188, 205);
        base.OnRenderArrow(e);
    }
}

// ======================================================================= The widget
class ClockForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    readonly Settings c = Settings.Load();
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    int layer, winW, winH, margin;
    float u = 1;
    bool dragging, moved;
    Point grab, downAt;
    string lastKey;
    ThemePicker picker;

    public ClockForm()
    {
        layer = c.Layer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Kinetik Clock";
        menu.Opening += (s, e) => BuildMenu();
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
        tray.Text = Program.Demo ? "Kinetik Clock (demo)" : "Kinetik Clock";
        Backup.BeforeExit = () => tray.Visible = false;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tick.Tick += (s, e) => { Tick(); Render(false); };
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.SessionSwitch += OnSession;
    }

    void OnTimeChanged(object sender, EventArgs e) { KinetikSuite.Ui.Post(this, () => { TimeZoneInfo.ClearCachedData(); Tick(); Render(true); }); }

    // Nothing redraws while the PC is locked (with seconds on, that's once a second); it catches up on unlock.
    void OnSession(object s, SessionSwitchEventArgs e)
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock) tick.Stop();
                else if (e.Reason == SessionSwitchReason.SessionUnlock) { Tick(); Render(true); }
            }));
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            if (layer == 2) cp.ExStyle |= 0x8;          // TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        if (m.Msg == 0x46 && layer == 0) // WM_WINDOWPOSCHANGING: "on the desktop" keeps it underneath every other window
        {
            var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = new IntPtr(1); Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Tick();
        Render(true);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false); // shown by Render() with SW_SHOWNA, so it never steals focus
        // the first Render ran while the handle was created, and the line above hid it again: show it now,
        // not when the next minute turns over
        if (value) BeginInvoke((Action)(() => Render(true)));
    }

    protected override void Dispose(bool disposing)
    {
        surface.Dispose();
        if (disposing) { SystemEvents.SessionSwitch -= OnSession; SystemEvents.TimeChanged -= OnTimeChanged; }
        if (disposing) { tick.Dispose(); tray.Dispose(); menu.Dispose(); DisposeFonts(); shadow.Dispose(); if (textLayer != null) textLayer.Dispose(); }
        base.Dispose(disposing);
    }

    // Wakes up just after the next second (or minute) turns over, so the display never lags behind the system clock.
    void Tick()
    {
        var now = DateTime.Now;
        int ms = c.Seconds ? 1000 - now.Millisecond : (60 - now.Second) * 1000 - now.Millisecond;
        tick.Interval = Math.Max(50, ms + 20);
        tick.Start();
    }

    // ---- mouse: drag to move, right-click for the menu
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            downAt = Cursor.Position; moved = false;
            if (!c.Locked) { dragging = true; grab = new Point(Cursor.Position.X - c.X, Cursor.Position.Y - c.Y); }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging && (moved || Math.Abs(Cursor.Position.X - downAt.X) + Math.Abs(Cursor.Position.Y - downAt.Y) > 4))
        {
            moved = true;
            var p = Snap(new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y));
            c.X = p.X; c.Y = p.Y;
            SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE | NOZORDER | NOACTIVATE
            Cursor = Cursors.SizeAll;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (dragging && moved) c.Save();
            dragging = false;
            Cursor = Cursors.Default;
        }
        else if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    // Pulls the card flush to a screen edge (with a small gap) when it's dragged close to one.
    Point Snap(Point p)
    {
        if (!c.Snap) return p;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int gap = (int)(12 * u), reach = (int)(18 * u);
        int left = p.X + margin, top = p.Y + margin, right = p.X + winW - margin, bottom = p.Y + winH - margin;
        if (Math.Abs(left - (wa.Left + gap)) < reach) p.X = wa.Left + gap - margin;
        else if (Math.Abs(right - (wa.Right - gap)) < reach) p.X = wa.Right - gap - winW + margin;
        if (Math.Abs(top - (wa.Top + gap)) < reach) p.Y = wa.Top + gap - margin;
        else if (Math.Abs(bottom - (wa.Bottom - gap)) < reach) p.Y = wa.Bottom - gap - winH + margin;
        return p;
    }

    void EnsurePosition()
    {
        if (c.X == int.MinValue || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(c.X, c.Y, winW, winH))))
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(12 * u);
            c.X = wa.Right - gap - winW + margin;
            c.Y = wa.Top + gap - margin;
        }
    }

    // ---- painting
    float RowH { get { return (c.Style == 0 ? 54 : 40) * u; } }

    string TimeText(DateTime t, out string suffix)
    {
        suffix = "";
        string s = c.Use24h ? t.ToString(c.Seconds ? "HH:mm:ss" : "HH:mm") : t.ToString(c.Seconds ? "h:mm:ss" : "h:mm");
        if (!c.Use24h) suffix = t.Hour < 12 ? "AM" : "PM";
        return s;
    }

    // "Tomorrow · +9 hrs", "Today · −2½ hrs", "Same time"
    static string OffsetText(DateTime local, DateTime there)
    {
        double hrs = Math.Round((there - local).TotalMinutes) / 60.0;
        string day = there.Date > local.Date ? "Tomorrow" : there.Date < local.Date ? "Yesterday" : "Today";
        if (Math.Abs(hrs) < 0.01) return day + " · same time";
        double a = Math.Abs(hrs);
        string n = a % 1 == 0 ? a.ToString("0") : a % 1 == 0.5 ? Math.Floor(a) + "½" : a % 1 == 0.25 ? Math.Floor(a) + "¼" : a % 1 == 0.75 ? Math.Floor(a) + "¾" : a.ToString("0.##");
        if (n.StartsWith("0") && n.Length > 1) n = n.Substring(1);
        return day + " · " + (hrs > 0 ? "+" : "−") + n + (a == 1 ? " hr" : " hrs");
    }

    public void Render(bool force)
    {
        if (IsDisposed || !IsHandleCreated) return;
        var places = c.List;
        var nowUtc = DateTime.UtcNow;
        string key = c.Places + "|" + c.Style + c.ThemeName + c.Use24h + c.Seconds + c.ShowOffset + c.Width + c.Scale + c.Opacity
            + "|" + nowUtc.ToString(c.Seconds ? "HHmmss" : "HHmm") + Theme.Get(c.ThemeName).Name;
        if (key == lastKey && !force) return;
        lastKey = key;

        var screen = c.X == int.MinValue ? Screen.PrimaryScreen : Screen.FromPoint(new Point(c.X + 40, c.Y + 40));
        u = DeviceDpi / 96f * c.Scale / 100f;
        margin = (int)(14 * u);
        float pad = 14 * u;
        int cardW = (int)(c.Width * u), cardH = (int)(pad * 2 + Math.Max(1, places.Count) * RowH);
        winW = cardW + margin * 2; winH = cardH + margin * 2;

        bool wantThrough = c.Locked && c.ClickThrough;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, wantThrough ? ex | 0x20 : ex & ~0x20); // TRANSPARENT: clicks fall through

        EnsurePosition();
        var bmp = surface.Canvas(winW, winH);
        using (var g = Graphics.FromImage(bmp)) PaintCard(g, places, nowUtc, cardW, cardH, pad);
        surface.Push(Handle, c.X, c.Y);
        ShowWindow(Handle, 8); // SW_SHOWNA
        if (layer == 0) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);       // bottom of the pile
        else if (layer == 2) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // stay on top
    }

    Bitmap textLayer;
    readonly ShadowCache shadow = new ShadowCache();
    string Fam { get { return c.FontName != "" && Draw.HasFont(c.FontName) ? c.FontName : Draw.Family; } }

    // The card's fonts, made again only when the font, size or style changes rather than every second.
    Font nameF, subF, timeF, ampmF;
    string fontKey;
    static readonly StringFormat fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtL = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

    void EnsureFonts()
    {
        float dpiFix = DeviceDpi / 96f;
        var key = c.FontName + "|" + u + "|" + dpiFix + "|" + c.Style;
        if (key == fontKey) return;
        DisposeFonts();
        var fam = Fam;
        nameF = new Font(fam, 11 * u / dpiFix, FontStyle.Bold, GraphicsUnit.Point);
        subF = new Font(fam, 8.5f * u / dpiFix, GraphicsUnit.Point);
        timeF = new Font(fam, (c.Style == 0 ? 19 : 16) * u / dpiFix, GraphicsUnit.Point);
        ampmF = new Font(fam, 8.5f * u / dpiFix, FontStyle.Bold, GraphicsUnit.Point);
        fontKey = key;
    }

    void DisposeFonts()
    {
        foreach (var f in new[] { nameF, subF, timeF, ampmF }) if (f != null) f.Dispose();
        nameF = subF = timeF = ampmF = null; fontKey = null;
    }

    void PaintCard(Graphics target, List<Place> places, DateTime nowUtc, int cardW, int cardH, float pad)
    {
        var th = Theme.Get(c.ThemeName);
        target.Clear(Color.Transparent);
        target.SmoothingMode = SmoothingMode.AntiAlias;
        target.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var card = new RectangleF(margin, margin, cardW, cardH);
        float rad = 16 * u;
        // soft shadow
        shadow.Paint(target, winW, winH, card + "|" + u + "|" + c.Opacity, sg =>
        {
            for (int i = 1; i <= 6; i++)
                using (var p = Draw.Round(RectangleF.Inflate(card, i * 1.6f * u, i * 1.6f * u), rad + i * 1.6f * u))
                using (var b = new SolidBrush(Color.FromArgb((int)(14 * c.Opacity / 100f / i), 0, 0, 0))) sg.FillPath(b, p);
        });

        // ClearType needs solid pixels behind the text. So for a mostly opaque card, the card is drawn solid with ClearType text
        // on a layer, and the whole layer is then blended in at the card's opacity. A very see-through card keeps grayscale text.
        int alpha = 255 * c.Opacity / 100;
        bool crisp = alpha >= 150;
        var g = target;
        if (crisp)
        {
            if (textLayer == null || textLayer.Width != winW || textLayer.Height != winH) { if (textLayer != null) textLayer.Dispose(); textLayer = new Bitmap(winW, winH, PixelFormat.Format32bppPArgb); }
            g = Graphics.FromImage(textLayer);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TextContrast = 2;
        }
        else g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        try { PaintContent(g, th, card, rad, crisp ? 255 : alpha, places, nowUtc, pad); }
        finally
        {
            if (crisp)
            {
                g.Dispose();
                if (alpha >= 255) target.DrawImageUnscaled(textLayer, 0, 0);
                else
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
                        target.DrawImage(textLayer, new Rectangle(0, 0, textLayer.Width, textLayer.Height), 0, 0, textLayer.Width, textLayer.Height, GraphicsUnit.Pixel, ia);
                    }
            }
        }
    }

    void PaintContent(Graphics g, Theme th, RectangleF card, float rad, int alpha, List<Place> places, DateTime nowUtc, float pad)
    {
        using (var p = Draw.Round(card, rad))
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, th.Bg))) g.FillPath(b, p);
            using (var pen = new Pen(th.Line, 1)) g.DrawPath(pen, p);
        }

        var local = DateTime.Now;
        if (places.Count == 0)
        {
            using (var f = new Font(Fam, 10 * u / (DeviceDpi / 96f), GraphicsUnit.Point))
            using (var b = new SolidBrush(th.Sub))
                g.DrawString("Right-click to add a place", f, b, card.X + pad, card.Y + pad);
            return;
        }

        EnsureFonts();
        using (var text = new SolidBrush(th.Text))
        using (var sub = new SolidBrush(th.Sub))
        {
            float y = card.Y + pad, rh = RowH;
            for (int i = 0; i < places.Count; i++, y += rh)
            {
                var pl = places[i];
                var there = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, pl.Tz);
                if (i > 0) using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, card.X + pad, y, card.Right - pad, y);

                float x = card.X + pad;
                if (c.Style == 0)
                {
                    float d = 36 * u;
                    DrawDial(g, new RectangleF(x, y + (rh - d) / 2, d, d), there, th);
                    x += d + 12 * u;
                }
                else
                {
                    // a small sun or moon dot
                    bool day = there.Hour >= 6 && there.Hour < 18;
                    float d = 8 * u;
                    using (var b = new SolidBrush(day ? Color.FromArgb(255, 196, 64) : Draw.Mix(th.Sub, th.Bg, 0.3f)))
                        g.FillEllipse(b, x, y + (rh - d) / 2, d, d);
                    x += d + 10 * u;
                }

                // time on the right
                string suffix;
                string t = TimeText(there, out suffix);
                float right = card.Right - pad;
                if (suffix != "")
                {
                    var sw = g.MeasureString(suffix, ampmF, 100, fmtR).Width;
                    g.DrawString(suffix, ampmF, sub, new RectangleF(right - sw, y + rh * 0.08f, sw, rh), fmtR);
                    right -= sw + 1 * u;
                }
                var tw = g.MeasureString(t, timeF, 400, fmtR).Width;
                g.DrawString(t, timeF, text, new RectangleF(right - tw, y, tw, rh), fmtR);
                right -= tw + 8 * u;

                // name and offset on the left
                string name = pl.Name == "" ? "Local" : pl.Name;
                if (c.ShowOffset && c.Style == 0)
                {
                    g.DrawString(name, nameF, text, new RectangleF(x, y + rh * 0.08f, right - x, rh / 2), fmtL);
                    g.DrawString(OffsetText(local, there) + " · " + there.ToString("ddd"), subF, sub, new RectangleF(x, y + rh * 0.45f, right - x, rh / 2), fmtL);
                }
                else
                {
                    string extra = c.ShowOffset ? "  " + OffsetText(local, there).Split('·').Last().Trim() : "";
                    g.DrawString(name, nameF, text, new RectangleF(x, y, right - x, rh), fmtL);
                    if (extra != "")
                    {
                        var nw = g.MeasureString(name, nameF).Width;
                        g.DrawString(extra, subF, sub, new RectangleF(x + nw, y + 1 * u, Math.Max(0, right - x - nw), rh), fmtL);
                    }
                }
            }
        }
    }

    // A small analog face: light by day, dark by night, with the theme's accent as the second hand.
    void DrawDial(Graphics g, RectangleF r, DateTime t, Theme th)
    {
        float h = t.Hour + t.Minute / 60f;
        // day 7–18, dusk/dawn blend over an hour on each side
        float dayness = h < 6 ? 0 : h < 7 ? h - 6 : h < 18 ? 1 : h < 19 ? 19 - h : 0;
        Color dayFace = th.Light ? Color.White : Color.FromArgb(236, 238, 244), nightFace = th.Light ? Color.FromArgb(40, 44, 56) : Color.FromArgb(52, 56, 70);
        Color face = Draw.Mix(nightFace, dayFace, dayness);
        Color ink = Draw.Mix(Color.FromArgb(236, 238, 244), Color.FromArgb(28, 30, 38), dayness);

        using (var b = new SolidBrush(face)) g.FillEllipse(b, r);
        using (var pen = new Pen(th.Line, 1)) g.DrawEllipse(pen, r);

        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = r.Width / 2;
        using (var pen = new Pen(Color.FromArgb(110, ink), 1.2f * u))
            for (int i = 0; i < 12; i += 3)
            {
                double a = i * Math.PI / 6;
                g.DrawLine(pen, cx + (float)Math.Sin(a) * rad * 0.78f, cy - (float)Math.Cos(a) * rad * 0.78f,
                                cx + (float)Math.Sin(a) * rad * 0.9f, cy - (float)Math.Cos(a) * rad * 0.9f);
            }

        Action<double, float, float, Color> hand = (turns, len, w, col) =>
        {
            double a = turns * 2 * Math.PI;
            using (var pen = new Pen(col, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx, cy, cx + (float)Math.Sin(a) * rad * len, cy - (float)Math.Cos(a) * rad * len);
        };
        hand((t.Hour % 12 + t.Minute / 60.0) / 12, 0.5f, 2.4f * u, ink);
        hand((t.Minute + t.Second / 60.0) / 60, 0.74f, 1.8f * u, ink);
        if (c.Seconds) hand(t.Second / 60.0, 0.8f, 1 * u, th.Accent);
        using (var b = new SolidBrush(c.Seconds ? th.Accent : ink)) g.FillEllipse(b, cx - 2 * u, cy - 2 * u, 4 * u, 4 * u);
    }

    // ---- menu
    ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    void Changed() { c.Save(); Tick(); Render(true); }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Clock", Version = Program.Version,
            OpenTheme = () =>
            {
                if (picker != null && !picker.IsDisposed) { picker.Activate(); return; }
                picker = new ThemePicker(c.ThemeName, n => { c.ThemeName = n; Changed(); });
                picker.Show();
            },
            Scale = () => c.Scale, SetScale = v => { c.Scale = v; Changed(); },
            Widths = new[] { 240, 300, 360 }, CardWidth = () => c.Width, SetCardWidth = v => { c.Width = v; Changed(); },
            FontName = () => c.FontName, SetFontName = v => { c.FontName = v; Changed(); },
            Opacity = () => c.Opacity, SetOpacity = v => { c.Opacity = v; Changed(); },
            Layer = () => c.Layer, SetLayer = SetLayer,
            Locked = () => c.Locked, SetLocked = v => { c.Locked = v; Changed(); },
            ClickThrough = () => c.ClickThrough, SetClickThrough = v => { c.ClickThrough = v; Changed(); },
            Snap = () => c.Snap, SetSnap = v => { c.Snap = v; Changed(); },
            RunAtStartup = () => Startup.IsOn, SetRunAtStartup = Startup.Set,
            BackUp = () => { c.Save(); Backup.SaveNow(); }, Restore = Backup.RestoreNow, LastBackup = () => Backup.SavedText,
            Exit = () => { tray.Visible = false; Application.Exit(); },
        };
    }

    void BuildMenu()
    {
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var it in old) it.Dispose(); // a cleared submenu that was shown keeps its window otherwise
        menu.Items.Add(Item("Add a place…", false, AddPlace));

        var places = c.List;
        if (places.Count > 0)
        {
            var edit = new ToolStripMenuItem("Places");
            for (int i = 0; i < places.Count; i++)
            {
                int idx = i;
                var p = places[i];
                var it = new ToolStripMenuItem((p.Name == "" ? "Local" : p.Name) + "   " + (p.Zone == "" ? "(this PC)" : p.Tz.DisplayName));
                it.DropDownItems.Add(Item("Rename…", false, () =>
                {
                    var n = Prompt("Rename", "Name shown on the clock:", p.Name);
                    if (n == null) return;
                    var l = c.List; l[idx].Name = n.Trim(); c.List = l; Changed();
                }));
                if (idx > 0) it.DropDownItems.Add(Item("Move up", false, () => { var l = c.List; var x = l[idx]; l.RemoveAt(idx); l.Insert(idx - 1, x); c.List = l; Changed(); }));
                if (idx < places.Count - 1) it.DropDownItems.Add(Item("Move down", false, () => { var l = c.List; var x = l[idx]; l.RemoveAt(idx); l.Insert(idx + 1, x); c.List = l; Changed(); }));
                it.DropDownItems.Add(Item("Remove", false, () => { var l = c.List; l.RemoveAt(idx); c.List = l; Changed(); }));
                edit.DropDownItems.Add(it);
            }
            menu.Items.Add(edit);
        }
        menu.Items.Add(new ToolStripSeparator());

        var style = new ToolStripMenuItem("Style");
        style.DropDownItems.Add(Item("Dials", c.Style == 0, () => { c.Style = 0; Changed(); }));
        style.DropDownItems.Add(Item("Compact", c.Style == 1, () => { c.Style = 1; Changed(); }));
        style.DropDownItems.Add(new ToolStripSeparator());
        style.DropDownItems.Add(Item("24-hour time", c.Use24h, () => { c.Use24h = !c.Use24h; Changed(); }));
        style.DropDownItems.Add(Item("Show seconds", c.Seconds, () => { c.Seconds = !c.Seconds; Changed(); }));
        style.DropDownItems.Add(Item("Show time difference", c.ShowOffset, () => { c.ShowOffset = !c.ShowOffset; Changed(); }));
        menu.Items.Add(style);

        menu.Items.Add(Item("Windows date and time settings", false, () => { try { System.Diagnostics.Process.Start("ms-settings:dateandtime"); } catch { } }));
        menu.Items.Add(new ToolStripSeparator());
        Common().AddAll(menu.Items);
    }

    // Changing the window's layer needs a fresh window handle, since TOPMOST is a creation style.
    void SetLayer(int l)
    {
        c.Layer = layer = l; c.Save();
        RecreateHandle();
        lastKey = null;
        Render(true);
    }

    void AddPlace()
    {
        using (var dlg = new PlaceDialog())
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
            {
                var l = c.List; l.Add(dlg.Result); c.List = l; Changed();
            }
    }

    static string Prompt(string title, string label, string value)
    {
        using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(340, 110), BackColor = Color.FromArgb(27, 31, 44), ForeColor = Color.FromArgb(232, 236, 244), Font = new Font("Segoe UI", 9.5f), TopMost = true })
        {
            var lb = new Label { Text = label, Left = 12, Top = 12, AutoSize = true };
            var tb = new TextBox { Text = value, Left = 12, Top = 36, Width = 316, BackColor = Color.FromArgb(40, 47, 66), ForeColor = f.ForeColor, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 172, Top = 72, Width = 75, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 253, Top = 72, Width = 75, FlatStyle = FlatStyle.Flat };
            f.Controls.AddRange(new Control[] { lb, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
        }
    }
}

// A grid of theme tiles, the same picker as Kinetik Calendar and Kinetik's widget settings.
class ThemePicker : Form
{
    public static readonly Color Page = Color.FromArgb(20, 23, 33), Card = Color.FromArgb(27, 31, 44), CardHi = Color.FromArgb(36, 41, 58), Accent = Color.FromArgb(77, 166, 255);
    public static readonly Color TextCol = Color.FromArgb(232, 236, 244), SubCol = Color.FromArgb(150, 158, 180);

    public ThemePicker(string current, Action<string> chosen)
    {
        Text = "Kinetik Clock theme";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Page;
        float k = DeviceDpi / 96f;
        var flow = new FlowLayoutPanel { Left = (int)(16 * k), Top = (int)(16 * k), Width = (int)(3 * 118 * k), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size((int)(3 * 118 * k), 0), WrapContents = true, BackColor = Page };
        foreach (var name in new[] { "Auto" }.Concat(Theme.All.Select(t => t.Name)))
        {
            var tile = new ThemeTile(name, name == current, k);
            tile.Click += (s, e) =>
            {
                foreach (ThemeTile o in flow.Controls) o.SetSelected(o == tile);
                chosen(tile.ThemeName);
            };
            flow.Controls.Add(tile);
        }
        Controls.Add(flow);
        ClientSize = new Size(flow.Right + (int)(8 * k), flow.Bottom + (int)(8 * k));
    }
}

class ThemeTile : Control
{
    public readonly string ThemeName;
    bool selected, hot;
    float sel;
    readonly float k;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };
    static readonly Color[] Bars = { Color.FromArgb(77, 166, 255), Color.FromArgb(56, 198, 180), Color.FromArgb(240, 88, 88) };

    public ThemeTile(string name, bool isSelected, float scale)
    {
        ThemeName = name; selected = isSelected; sel = isSelected ? 1 : 0; k = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(I(110), I(84));
        Margin = new Padding(0, 0, I(8), I(8));
        Cursor = Cursors.Hand;
        BackColor = ThemePicker.Page;
        t.Tick += (s, e) =>
        {
            float target = selected ? 1 : 0;
            sel += (target - sel) * 0.3f;
            if (Math.Abs(target - sel) < 0.02f) { sel = target; t.Stop(); }
            Invalidate();
        };
    }

    int I(float v) { return (int)Math.Round(v * k); }
    public void SetSelected(bool v) { if (selected == v) return; selected = v; t.Start(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    static void Fill(Graphics g, RectangleF r, float rad, Color c) { using (var p = Draw.Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(1, 1, Width - 3, Height - 3);
        Fill(g, outer, I(10), hot ? ThemePicker.CardHi : ThemePicker.Card);
        if (sel > 0) using (var p = Draw.Round(outer, I(10))) using (var pen = new Pen(Color.FromArgb((int)(255 * sel), ThemePicker.Accent), 2)) g.DrawPath(pen, p);
        var inner = new RectangleF(I(8), I(8), Width - I(16), Height - I(34));
        if (ThemeName == "Auto")
        {
            Mini(g, inner, Theme.Get("Midnight"));
            var st = g.Save();
            g.SetClip(new RectangleF(inner.X + inner.Width / 2, inner.Y, inner.Width, inner.Height));
            Mini(g, inner, Theme.Get("Light"));
            g.Restore(st);
        }
        else Mini(g, inner, Theme.Get(ThemeName));
        using (var f = new Font(Draw.Family, 9f))
            TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName, f, new Rectangle(0, Height - I(26), Width, I(20)), selected ? ThemePicker.TextCol : ThemePicker.SubCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void Mini(Graphics g, RectangleF r, Theme th)
    {
        Fill(g, r, I(6), th.Bg);
        float x = r.X + I(7), w = r.Width - I(14), y = r.Y + I(7);
        using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, x, y, I(7), I(7));
        Fill(g, new RectangleF(x + I(11), y + I(1), w * 0.45f, I(4)), I(2), th.Text);
        for (int i = 0; i < 3; i++)
        {
            float yy = y + I(13) + i * I(9);
            Fill(g, new RectangleF(x, yy, I(3), I(6)), 1.5f * k, Bars[i]);
            Fill(g, new RectangleF(x + I(7), yy + I(1), w * (0.62f - i * 0.12f), I(4)), I(2), Color.FromArgb(150, th.Sub));
            Fill(g, new RectangleF(x + w - I(14), yy + I(1), I(14), I(4)), I(2), Color.FromArgb(200, th.Text));
        }
    }
}

// Pick a time zone (searchable by city or zone name) and give it a label.
class PlaceDialog : Form
{
    public Place Result;
    readonly TextBox search, name;
    readonly ListBox list;
    readonly List<TimeZoneInfo> zones = TimeZoneInfo.GetSystemTimeZones().ToList();

    public PlaceDialog()
    {
        Text = "Add a place";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        ClientSize = new Size(460, 440);
        BackColor = Color.FromArgb(27, 31, 44); ForeColor = Color.FromArgb(232, 236, 244);
        Font = new Font("Segoe UI", 9.5f);
        Color field = Color.FromArgb(40, 47, 66);

        Controls.Add(new Label { Text = "Search for a city or time zone:", Left = 12, Top = 12, AutoSize = true });
        search = new TextBox { Left = 12, Top = 34, Width = 436, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        list = new ListBox { Left = 12, Top = 64, Width = 436, Height = 270, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        Controls.Add(new Label { Text = "Name shown on the clock:", Left = 12, Top = 344, AutoSize = true });
        name = new TextBox { Left = 12, Top = 366, Width = 436, BackColor = field, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "Add", Left = 292, Top = 402, Width = 75, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 373, Top = 402, Width = 75, FlatStyle = FlatStyle.Flat };
        Controls.AddRange(new Control[] { search, list, name, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;

        search.TextChanged += (s, e) => Fill();
        search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = 0; e.Handled = true; } };
        list.SelectedIndexChanged += (s, e) =>
        {
            var z = list.SelectedItem as Zone;
            if (z != null) name.Text = GuessName(z.Tz, search.Text);
        };
        list.DoubleClick += (s, e) => ok.PerformClick();
        ok.Click += (s, e) =>
        {
            var z = list.SelectedItem as Zone;
            if (z == null) { search.Focus(); return; }
            Result = new Place { Name = string.IsNullOrWhiteSpace(name.Text) ? GuessName(z.Tz, "") : name.Text.Trim(), Zone = z.Tz.Id };
            DialogResult = DialogResult.OK;
        };
        Fill();
    }

    class Zone
    {
        public TimeZoneInfo Tz;
        public override string ToString()
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);
            return Tz.DisplayName + "   —   " + now.ToString("t");
        }
    }

    void Fill()
    {
        string q = search.Text.Trim().ToLowerInvariant();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var z in zones)
            if (q == "" || z.DisplayName.ToLowerInvariant().Contains(q) || z.Id.ToLowerInvariant().Contains(q) || z.StandardName.ToLowerInvariant().Contains(q))
                list.Items.Add(new Zone { Tz = z });
        list.EndUpdate();
        if (list.Items.Count == 1) list.SelectedIndex = 0;
    }

    // "(UTC+09:00) Osaka, Sapporo, Tokyo" → the city that was searched for, or else the last one listed.
    static string GuessName(TimeZoneInfo tz, string query)
    {
        string dn = tz.DisplayName;
        int i = dn.IndexOf(')');
        var cities = (i >= 0 ? dn.Substring(i + 1) : dn).Split(',').Select(s => s.Trim()).Where(s => s != "").ToList();
        if (cities.Count == 0) return tz.Id;
        var hit = cities.FirstOrDefault(s => query.Trim() != "" && s.ToLowerInvariant().Contains(query.Trim().ToLowerInvariant()));
        return hit ?? cities.Last();
    }
}

// ======================================================================= Settings backup
// Copies the app's settings (HKCU\Software\<KeyName>) to "<KeyName>-settings.reg" next to the exe, so they survive a
// Windows reinstall when the app folder is kept (on OneDrive, a USB stick…). It's a normal .reg export, so
// double-clicking the file restores the settings as well. A restore runs after the app has closed, so nothing the app
// saves on the way out can overwrite it, and then the app starts again with the restored settings.
// Same code in every Kinetik app.
static class Backup
{
    public static string KeyName, AppTitle;
    public static Action BeforeExit; // e.g. hide the tray icon, as the app's own Exit does
    static bool restoreAfterExit;

    public static string FilePath
    {
        get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), KeyName + "-settings.reg"); }
    }
    public static bool Exists { get { return System.IO.File.Exists(FilePath); } }
    public static string SavedText
    {
        get { return Exists ? System.IO.File.GetLastWriteTime(FilePath).ToString("d MMM yyyy, h:mm tt") : "never"; }
    }

    static bool HasSettings
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + KeyName)) return k != null; }
            catch { return true; }
        }
    }

    static bool Reg(string args) { return KinetikSuite.RegistryFiles.Run(args); }

    static bool FileIsOurs()
    {
        try
        {
            string own = "[HKEY_CURRENT_USER\\Software\\" + KeyName;
            bool any = false;
            foreach (var line in System.IO.File.ReadAllLines(FilePath))
            {
                var l = line.Trim();
                if (!l.StartsWith("[")) continue;
                if (!l.StartsWith(own + "]", StringComparison.OrdinalIgnoreCase) && !l.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static bool Import()
    {
        try { return FileIsOurs() && KinetikSuite.RegistryFiles.Replace(FilePath, new[] { KeyName }); }
        catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); return false; }
    }

    static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, AppTitle, MessageBoxButtons.OK, icon); }

    // At launch: no settings in the registry (a fresh Windows) but a backup beside the exe, so offer it.
    public static void OfferRestore()
    {
        if (HasSettings || !Exists) return;
        if (MessageBox.Show("There are saved " + AppTitle + " settings in this folder, from " + SavedText + ".\n\nRestore them?",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
    }

    public static void SaveNow()
    {
        if (Reg("export \"HKCU\\Software\\" + KeyName + "\" \"" + FilePath + "\" /y"))
            Say("Settings backed up to\n" + FilePath + "\n\nKeep this file with the app. After reinstalling Windows, " + AppTitle + " offers to restore it the first time it starts.", MessageBoxIcon.Information);
        else
            Say("Couldn't save the backup to\n" + FilePath + "\n\nCheck that the folder isn't read-only.", MessageBoxIcon.Warning);
    }

    public static void RestoreNow()
    {
        if (!Exists) { Say("There's no backup yet. Use \"Back up settings\" first.\n\nIt would be saved as\n" + FilePath, MessageBoxIcon.Information); return; }
        if (!FileIsOurs()) { Say("The backup file isn't a " + AppTitle + " settings file, so it wasn't used.", MessageBoxIcon.Warning); return; }
        if (MessageBox.Show("Replace the current settings with the backup from " + SavedText + "?\n\n" + AppTitle + " will restart.",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        restoreAfterExit = true;
        if (BeforeExit != null) try { BeforeExit(); } catch { }
        Application.Exit();
    }

    // Called from Main once the app has closed and released its single-instance lock.
    public static void AfterExit()
    {
        if (!restoreAfterExit) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
        try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
    }
}

// ======================================================================= The right-click menu every Kinetik widget shares
// The look, placement, startup and backup options, in the same order and the same words in every Kinetik app, so they
// feel like one set. Each app adds its own items above. Same code in every Kinetik app.
#pragma warning disable 0649 // the hooks an app has no use for stay null
class CommonMenu
{
    public static readonly int[] Scales = { 80, 90, 100, 115, 125, 150 };
    public static readonly int[] Opacities = { 100, 95, 80, 65, 50, 30, 0 };
    public const int DefaultOpacity = 95;
    public static readonly string[] Fonts = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    // Settings saved before the shared default changed from 92% to 95% move with it; any other choice is kept.
    public static int Migrate(int opacity) { return opacity == 92 ? DefaultOpacity : opacity; }

    public string Title, Version;
    public Action OpenTheme;                                        // opens the theme picker (or the page with it)
    public Func<int> Scale; public Action<int> SetScale;
    public int[] Widths; public Func<int> CardWidth; public Action<int> SetCardWidth; // optional: Narrow, Normal, Wide
    public Func<string> FontName; public Action<string> SetFontName;   // optional; "" = the default font
    public Func<int> Opacity; public Action<int> SetOpacity;
    public Func<int> Layer; public Action<int> SetLayer;
    public Func<bool> Locked; public Action<bool> SetLocked;
    public Func<bool> ClickThrough; public Action<bool> SetClickThrough; // optional
    public Func<bool> Snap; public Action<bool> SetSnap;
    public string LockText = "Lock position";
    public Action OpenSettings;                                     // optional: the app's Settings window
    public Func<bool> RunAtStartup; public Action<bool> SetRunAtStartup;   // optional
    public Action BackUp, Restore; public Func<string> LastBackup; // optional
    public Action Exit;                                         // optional

    static ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    // Appearance and placement: Theme, Size, Font, Background opacity, Placement.
    public void AddLook(ToolStripItemCollection items)
    {
        if (OpenTheme != null) items.Add(Item("Theme…", false, OpenTheme));

        var size = new ToolStripMenuItem("Size");
        foreach (var s in Scales) { int v = s; size.DropDownItems.Add(Item(v + "%", Scale() == v, () => SetScale(v))); }
        if (Widths != null)
        {
            size.DropDownItems.Add(new ToolStripSeparator());
            string[] names = { "Narrow", "Normal", "Wide" };
            for (int i = 0; i < Widths.Length && i < 3; i++) { int v = Widths[i]; size.DropDownItems.Add(Item(names[i], CardWidth() == v, () => SetCardWidth(v))); }
        }
        items.Add(size);

        if (FontName != null)
        {
            var font = new ToolStripMenuItem("Font");
            font.DropDownItems.Add(Item("Default", FontName() == "", () => SetFontName("")));
            foreach (var n in Fonts.Where(HasFont)) { string v = n; font.DropDownItems.Add(Item(v, FontName() == v, () => SetFontName(v))); }
            if (FontName() != "" && !Fonts.Contains(FontName())) font.DropDownItems.Add(Item(FontName(), true, () => { }));
            items.Add(font);
        }

        var opacity = new ToolStripMenuItem("Background opacity");
        foreach (var o in Opacities) { int v = o; opacity.DropDownItems.Add(Item(v + "%", Opacity() == v, () => SetOpacity(v))); }
        if (!Opacities.Contains(Opacity())) opacity.DropDownItems.Add(Item(Opacity() + "%", true, () => { }));
        items.Add(opacity);

        var place = new ToolStripMenuItem("Placement");
        string[] layers = { "On the desktop", "Normal window", "Always on top" };
        for (int i = 0; i < 3; i++) { int v = i; place.DropDownItems.Add(Item(layers[i], Layer() == v, () => SetLayer(v))); }
        place.DropDownItems.Add(new ToolStripSeparator());
        place.DropDownItems.Add(Item(LockText, Locked(), () => SetLocked(!Locked())));
        if (ClickThrough != null) place.DropDownItems.Add(Item("Click-through when locked", ClickThrough(), () => SetClickThrough(!ClickThrough())));
        place.DropDownItems.Add(Item("Snap to screen edges", Snap(), () => SetSnap(!Snap())));
        items.Add(place);
    }

    // The rest: Settings…, Run at startup, Backup, then the version and Exit.
    public void AddEnd(ToolStripItemCollection items)
    {
        items.Add(new ToolStripSeparator());
        if (OpenSettings != null) items.Add(Item("Settings…", false, OpenSettings));
        if (RunAtStartup != null) items.Add(Item("Run at startup", RunAtStartup(), () => SetRunAtStartup(!RunAtStartup())));
        if (BackUp != null)
        {
            var backup = new ToolStripMenuItem("Backup");
            backup.DropDownItems.Add(Item("Back up settings", false, BackUp));
            backup.DropDownItems.Add(Item("Restore settings…", false, Restore));
            backup.DropDownItems.Add(new ToolStripMenuItem("Last backup: " + LastBackup()) { Enabled = false });
            items.Add(backup);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Title + " " + Version) { Enabled = false });
        if (Exit != null) items.Add(Item("Exit", false, Exit));
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
