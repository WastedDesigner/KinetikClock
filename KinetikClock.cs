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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kinetik Clock")]
[assembly: AssemblyProduct("Kinetik Clock")]
[assembly: AssemblyCopyright("Copyright © 2026 Akila Sella Hennedige")]
[assembly: AssemblyVersion("2.2.0.0")]

static class Program
{
    public const string Version = "2.2.0";
    // --demo: made-up places, nothing read from or saved to this PC. "--theme Name" picks its theme (for screenshots).
    public static bool Demo;
    public static string DemoTheme; public static int DemoStyle; public static bool DemoFull, DemoNext; // --next: a made-up next calendar event
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        Demo = args.Contains("--demo"); DemoFull = args.Contains("--full"); DemoNext = args.Contains("--next"); // --full: seconds, offsets, scale and places like a real setup
        int ti = Array.IndexOf(args, "--theme");
        if (ti >= 0 && ti + 1 < args.Length) DemoTheme = args[ti + 1];
        int si = Array.IndexOf(args, "--style");
        if (si >= 0 && si + 1 < args.Length) int.TryParse(args[si + 1], out DemoStyle);
        bool created;
        using (new Mutex(true, Demo ? @"Local\KinetikClockDemo" : @"Local\KinetikClock", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = KinetikSuite.Look.Current.MenuRenderer();
            Backup.KeyName = "KinetikClock"; Backup.AppTitle = "Kinetik Clock";
            if (!Demo) Backup.OfferRestore();
            var app = new ClockForm();
            // --settings opens the settings window once the app has started.
            if (args.Contains("--settings")) { EventHandler once = null; once = (s2, e2) => { Application.Idle -= once; app.ShowSettings(); }; Application.Idle += once; }
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
    public int X = int.MinValue, Y = int.MinValue, Width = 300, Scale = 100, Opacity = CommonMenu.DefaultOpacity, Layer = 0, Style = 0; // Style: 0 dials, 1 text only, 2 daylight bar, 3 orbit ring, 4 big type, 5 galaxy
    public int FocusMin = 25, ShortMin = 5, LongMin = 15, WorkStart = 9, WorkEnd = 17; // the focus timer's lengths; the meeting planner's working hours
    public bool Locked, Snap = true, Seconds, ShowOffset = true, ClickThrough, ShowNext = true;
    public bool Use24h = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
    public string ThemeName = "Midnight", FontName = ""; // "" = Segoe UI Variable Text
    public string Alarms = "", MeetA = "", MeetB = ""; // Alarms: see Alarms.Parse; MeetA/B: the meeting planner's time zone ids ("" = this PC)
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
                Layer = 2, Style = Program.DemoStyle, ThemeName = Program.DemoTheme ?? "Midnight", X = 240, Y = 160, ShowOffset = Program.DemoFull, Seconds = Program.DemoFull, Scale = Program.DemoFull ? 106 : 100, Opacity = Program.DemoFull ? 95 : CommonMenu.DefaultOpacity,
                Places = (Program.DemoFull ? "Local=" + (char)10 + "" : "") + "London=GMT Standard Time\nNew York=Eastern Standard Time\nTokyo=Tokyo Standard Time\nSydney=AUS Eastern Standard Time\nSan Francisco=Pacific Standard Time"
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

// ======================================================================= Alarms, timers, stopwatch, focus, meeting planner, next event
// The logic is here, apart from the windows, so it can be tested. Alarms are saved in Settings.Alarms; timers, the stopwatch and
// the focus timer live only while the app runs.
class Alarm
{
    public int Hour, Minute, Days = 127; // Days: one bit per weekday (bit 0 Sunday); 0 = once
    public bool On = true;
    public string Label = "";
}

static class Alarms
{
    public const int EveryDay = 127, Weekdays = 62, Weekends = 65;

    // "07:30|1|Label|127" per line.
    public static List<Alarm> Parse(string s)
    {
        var list = new List<Alarm>();
        foreach (var line in (s ?? "").Split('\n'))
        {
            var p = line.Split('|'); int h, m, d;
            if (p.Length < 4 || !int.TryParse(p[0].Split(':')[0], out h) || p[0].Split(':').Length != 2 || !int.TryParse(p[0].Split(':')[1], out m) || !int.TryParse(p[3], out d)) continue;
            if (h < 0 || h > 23 || m < 0 || m > 59) continue;
            list.Add(new Alarm { Hour = h, Minute = m, On = p[1] == "1", Label = p[2], Days = d & 127 });
        }
        return list;
    }

    public static string Join(IEnumerable<Alarm> alarms)
    {
        return string.Join("\n", alarms.Select(a => a.Hour.ToString("00") + ":" + a.Minute.ToString("00") + "|" + (a.On ? 1 : 0) + "|" + (a.Label ?? "").Replace("|", " ").Replace("\n", " ") + "|" + a.Days));
    }

    // "7:30", "07:30", "6:45pm", "6.45 PM", "7 am", "1930": null when it isn't a time.
    public static TimeSpan? ParseTime(string text)
    {
        var m = Regex.Match((text ?? "").Trim().ToLowerInvariant(), @"^(\d{1,2})(?:[:.]?(\d{2}))?\s*(am|pm|a|p)?$");
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (m.Groups[3].Success)
        {
            if (h < 1 || h > 12) return null;
            h = h % 12 + (m.Groups[3].Value[0] == 'p' ? 12 : 0);
        }
        else if (!m.Groups[2].Success && m.Groups[1].Value.Length > 2) return null;
        return h > 23 || min > 59 ? (TimeSpan?)null : new TimeSpan(h, min, 0);
    }

    public static string DaysText(int days)
    {
        return days == 0 ? "Once" : days == EveryDay ? "Every day" : days == Weekdays ? "Weekdays" : days == Weekends ? "Weekends" : "Some days";
    }

    // Every day, then weekdays, then weekends, then once.
    public static int NextRepeat(int days) { return days == EveryDay ? Weekdays : days == Weekdays ? Weekends : days == Weekends ? 0 : EveryDay; }

    // The alarms that go off in this minute, each only once (`fired` remembers them for a day); a one-off alarm is switched off.
    public static List<Alarm> Due(List<Alarm> alarms, DateTime now, HashSet<string> fired)
    {
        var due = new List<Alarm>();
        if (fired.Count > 200) fired.Clear();
        foreach (var a in alarms)
        {
            if (!a.On || a.Hour != now.Hour || a.Minute != now.Minute) continue;
            if (a.Days != 0 && (a.Days >> (int)now.DayOfWeek & 1) == 0) continue;
            if (!fired.Add(now.ToString("yyyyMMddHHmm") + "|" + a.Hour + ":" + a.Minute + a.Label)) continue;
            due.Add(a);
            if (a.Days == 0) a.On = false;
        }
        return due;
    }
}

class RunningTimer { public string Name = ""; public DateTime End; }

static class Dur
{
    // 04:32, or 1:04:32 from an hour up.
    public static string Span(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int s = (int)Math.Ceiling(t.TotalSeconds);
        return s >= 3600 ? s / 3600 + ":" + (s / 60 % 60).ToString("00") + ":" + (s % 60).ToString("00") : (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    // "90", "90s", "5m", "1h", "12:30" (minutes:seconds), "1h30m": null when it isn't a duration.
    public static TimeSpan? ParseDuration(string text)
    {
        text = (text ?? "").Trim().ToLowerInvariant();
        var colon = Regex.Match(text, @"^(\d{1,3}):(\d{2})$");
        if (colon.Success) return new TimeSpan(0, int.Parse(colon.Groups[1].Value), int.Parse(colon.Groups[2].Value));
        var parts = Regex.Match(text, @"^(?:(\d+)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?\s*(?:(\d+)\s*s)?$");
        if (parts.Success && text != "" && text.Any(char.IsLetter))
            return new TimeSpan(parts.Groups[1].Success ? int.Parse(parts.Groups[1].Value) : 0, parts.Groups[2].Success ? int.Parse(parts.Groups[2].Value) : 0, parts.Groups[3].Success ? int.Parse(parts.Groups[3].Value) : 0);
        int n;
        return int.TryParse(text, out n) && n > 0 ? TimeSpan.FromMinutes(n) : (TimeSpan?)null; // a bare number is minutes
    }
}

class Watch
{
    public bool Running; public DateTime Since; public TimeSpan Before;
    public readonly List<TimeSpan> Laps = new List<TimeSpan>(); // elapsed time at each lap

    public TimeSpan Elapsed(DateTime now) { return Before + (Running ? now - Since : TimeSpan.Zero); }
    public void Start(DateTime now) { if (!Running) { Since = now; Running = true; } }
    public void Stop(DateTime now) { if (Running) { Before += now - Since; Running = false; } }
    public void Lap(DateTime now) { if (Running) Laps.Add(Elapsed(now)); }
    public void Reset() { Running = false; Before = TimeSpan.Zero; Laps.Clear(); }
    // How long lap i took.
    public TimeSpan Split(int i) { return Laps[i] - (i == 0 ? TimeSpan.Zero : Laps[i - 1]); }

    public static string Text(TimeSpan t)
    {
        return (t.TotalHours >= 1 ? (int)t.TotalHours + ":" + t.Minutes.ToString("00") : t.Minutes.ToString("00")) + ":" + t.Seconds.ToString("00") + "." + (t.Milliseconds / 10).ToString("00");
    }
}

// Pomodoro: focus, a short break, focus, a short break... and a long break after every fourth focus.
class Pomodoro
{
    public int Phase;        // 0 focus, 1 short break, 2 long break
    public int Done;         // focus sessions finished in this round
    public bool Running; public DateTime End; public TimeSpan Left;
    public string Name { get { return Phase == 0 ? "Focus" : Phase == 1 ? "Break" : "Long break"; } }
    public bool Active { get { return Running || Left > TimeSpan.Zero; } }

    public static TimeSpan Length(int phase, int focus, int brk, int longBrk) { return TimeSpan.FromMinutes(phase == 0 ? focus : phase == 1 ? brk : longBrk); }
    public TimeSpan Remaining(DateTime now) { return Running ? End - now : Left; }

    public void Start(DateTime now, int focus, int brk, int longBrk)
    {
        if (Running) return;
        if (Left <= TimeSpan.Zero) Left = Length(Phase, focus, brk, longBrk);
        End = now + Left; Running = true;
    }
    public void Pause(DateTime now) { if (Running) { Left = End - now; Running = false; } }
    public void Reset() { Phase = 0; Done = 0; Running = false; Left = TimeSpan.Zero; }

    // Moves on to the next phase and starts it. Returns the phase that just ended.
    public int Advance(DateTime now, int focus, int brk, int longBrk)
    {
        int ended = Phase;
        if (Phase == 0) { Done++; Phase = Done % 4 == 0 ? 2 : 1; }
        else Phase = 0;
        Left = TimeSpan.Zero; Running = false;
        Start(now, focus, brk, longBrk);
        return ended;
    }

    // True once when the running phase has run out (and the next one has begun).
    public bool Tick(DateTime now, int focus, int brk, int longBrk, out int ended)
    {
        ended = -1;
        if (!Running || now < End) return false;
        ended = Advance(now, focus, brk, longBrk);
        return true;
    }
}

// Two places' working hours, side by side.
static class Meeting
{
    // The hours of the day in `a` (0-23, on `day`) that are inside working hours in both places.
    public static List<int> Overlap(TimeZoneInfo a, TimeZoneInfo b, DateTime day, int from, int to)
    {
        var hours = new List<int>();
        for (int h = 0; h < 24; h++)
        {
            var inA = DateTime.SpecifyKind(day.Date.AddHours(h), DateTimeKind.Unspecified);
            if (a.IsInvalidTime(inA)) continue;
            var inB = TimeZoneInfo.ConvertTime(inA, a, b);
            if (h >= from && h < to && inB.Hour >= from && inB.Hour < to) hours.Add(h);
        }
        return hours;
    }

    // "9:00 to 11:00": the runs of hours, as clock times in the place they're counted in.
    public static List<string> Ranges(List<int> hours, bool use24h)
    {
        var res = new List<string>();
        for (int i = 0; i < hours.Count; )
        {
            int j = i; while (j + 1 < hours.Count && hours[j + 1] == hours[j] + 1) j++;
            Func<int, string> t = h => use24h ? h + ":00" : (h % 12 == 0 ? 12 : h % 12) + (h % 24 < 12 ? "am" : "pm");
            res.Add(t(hours[i]) + " to " + t(hours[j] + 1));
            i = j + 1;
        }
        return res;
    }
}

// The next calendar event, as Kinetik Calendar saves it: %LOCALAPPDATA%\KinetikCalendar\nextevent.tsv, one line of
// UTC start ticks, UTC end ticks, title, location and calendar, tab-separated (empty when nothing is coming up).
static class NextUp
{
    public class Item { public DateTime Start, End; public string Title, Location; }
    static DateTime stamp, looked; static Item cached;

    public static Item Parse(string line)
    {
        var p = (line ?? "").Split('\t'); long s, e;
        if (p.Length < 3 || !long.TryParse(p[0], out s) || !long.TryParse(p[1], out e) || s <= 0 || s > DateTime.MaxValue.Ticks || e > DateTime.MaxValue.Ticks) return null;
        return new Item { Start = new DateTime(s, DateTimeKind.Utc).ToLocalTime(), End = new DateTime(Math.Max(s, e), DateTimeKind.Utc).ToLocalTime(), Title = p[2], Location = p.Length > 3 && p[3] != "" ? p[3] : null };
    }

    public static Item Read()
    {
        if (Program.Demo) return Program.DemoNext ? new Item { Start = DateTime.Now.AddMinutes(25), End = DateTime.Now.AddMinutes(85), Title = "Design review" } : null;
        if ((DateTime.UtcNow - looked).TotalSeconds < 15) return cached; // the file is looked at every 15 seconds
        looked = DateTime.UtcNow;
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikCalendar", "nextevent.tsv");
            var info = new FileInfo(file);
            if (!info.Exists) return cached = null;
            if (info.LastWriteTimeUtc != stamp) { stamp = info.LastWriteTimeUtc; cached = Parse(File.ReadAllText(file).Split('\n')[0].Trim()); }
        }
        catch { cached = null; }
        return cached;
    }

    // "Design review in 25 min", "Design review at 3:00pm", "Design review Tue 9:00am". Null once it has started.
    public static string Text(Item e, DateTime now, bool use24h)
    {
        if (e == null || e.Start <= now) return null;
        var d = e.Start - now;
        string when = d.TotalMinutes < 1 ? "now" : d.TotalMinutes < 60 ? "in " + (int)Math.Ceiling(d.TotalMinutes) + " min"
            : e.Start.Date == now.Date ? "at " + Clk(e.Start, use24h) : (e.Start.Date == now.Date.AddDays(1) ? "tomorrow " : e.Start.ToString("ddd ")) + Clk(e.Start, use24h);
        return e.Title + " " + when;
    }

    static string Clk(DateTime t, bool use24h) { return use24h ? t.ToString("H:mm") : t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant().Replace(" ", ""); }
}

// A bar of the day in two places, with working hours shaded and the hours both share lit up.
class MeetingStrip : Control
{
    public TimeZoneInfo A, B; public string NameA = "", NameB = ""; public int From = 9, To = 17; public DateTime Day = DateTime.Today;

    public MeetingStrip() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var look = KinetikSuite.Look.Current; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(look.Card);
        if (A == null || B == null) return;
        float k = DeviceDpi / 96f, left = 74 * k, cw = (Width - left - 10 * k) / 24f, rh = 26 * k, top = 8 * k;
        var overlap = Meeting.Overlap(A, B, Day, From, To);
        using (var f = new Font("Segoe UI", 8f))
        using (var ink = new SolidBrush(look.Text)) using (var soft = new SolidBrush(look.Sub))
        {
            for (int row = 0; row < 2; row++)
            {
                var tz = row == 0 ? A : B; float y = top + row * (rh + 6 * k);
                g.DrawString(row == 0 ? NameA : NameB, f, ink, new RectangleF(10 * k, y, left - 14 * k, rh), new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                for (int h = 0; h < 24; h++)
                {
                    var inA = DateTime.SpecifyKind(Day.Date.AddHours(h), DateTimeKind.Unspecified);
                    int local = row == 0 ? h : (A.IsInvalidTime(inA) ? h : TimeZoneInfo.ConvertTime(inA, A, B).Hour);
                    bool work = local >= From && local < To, both = overlap.Contains(h);
                    var cell = new RectangleF(left + h * cw + 0.5f * k, y, cw - 1 * k, rh);
                    using (var b = new SolidBrush(both ? Color.FromArgb(230, look.Accent) : work ? Color.FromArgb(70, look.Accent) : Color.FromArgb(40, look.Sub))) g.FillRectangle(b, cell);
                    if (h % 3 == 0 || row == 1)
                        g.DrawString(local.ToString(), f, both ? ink : soft, cell, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
            }
        }
    }
}

// The window an alarm opens: the time and label, a sound that repeats until you answer, Snooze (5 minutes) and Dismiss.
class AlarmForm : Form
{
    readonly System.Windows.Forms.Timer beep = new System.Windows.Forms.Timer { Interval = 2500 };

    public AlarmForm(string time, string label, Action snooze)
    {
        var look = KinetikSuite.Look.Current;
        float k = DeviceDpi / 96f;
        Text = "Alarm";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        BackColor = look.Bg; ForeColor = look.Text; Font = new Font("Segoe UI", 10f);
        ClientSize = new Size((int)(340 * k), (int)(190 * k));
        var big = new Label { Text = time, Font = new Font("Segoe UI", 30f, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = (int)(78 * k), ForeColor = look.Text };
        var text = new Label { Text = label == "" ? "Alarm" : label, AutoSize = false, TextAlign = ContentAlignment.TopCenter, Dock = DockStyle.Top, Height = (int)(40 * k), ForeColor = look.Sub };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = look.Bg, Padding = new Padding((int)(16 * k), (int)(6 * k), (int)(16 * k), (int)(16 * k)) };
        var dismiss = new KinetikSuite.FlatButton("Dismiss", true, k) { Font = Font }; dismiss.Click += (s, e) => Close();
        var snz = new KinetikSuite.FlatButton("Snooze 5 min", false, k) { Font = Font }; snz.Click += (s, e) => { snooze(); Close(); };
        bar.Controls.Add(dismiss); bar.Controls.Add(snz);
        Controls.Add(text); Controls.Add(big); Controls.Add(bar);
        beep.Tick += (s, e) => System.Media.SystemSounds.Exclamation.Play();
        Shown += (s, e) => { Activate(); System.Media.SystemSounds.Exclamation.Play(); beep.Start(); };
        FormClosed += (s, e) => beep.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
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

    public Color Line { get { return Term ? Color.FromArgb(110, Sub) : Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    // The terminal themes (the same green, amber and yellow as Kinetik's dashboard and Kinetik Screen) draw in a
    // monospace font unless a font is picked, and their divider lines glow in the theme's colour.
    public bool Term { get { return IsTerm(Name); } }
    public static bool IsTerm(string name) { return name == "Terminal" || name == "Amber terminal" || name == "Yellow terminal"; }
    static string mono;
    public static string Mono
    {
        get
        {
            if (mono == null)
            {
                mono = "Consolas";
                try { using (var f = new Font("Cascadia Mono", 10f)) if (f.Name == "Cascadia Mono") mono = "Cascadia Mono"; } catch { }
            }
            return mono;
        }
    }

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
        new Theme("OLED black", C(0, 0, 0), C(242, 242, 245), C(136, 136, 146), C(76, 194, 255), false),
        new Theme("Mocha", C(30, 30, 46), C(205, 214, 244), C(147, 153, 178), C(203, 166, 247), false),
        new Theme("Terminal", C(3, 10, 5), C(90, 255, 120), C(46, 170, 80), C(205, 255, 215), false),
        new Theme("Amber terminal", C(12, 7, 0), C(255, 182, 40), C(185, 118, 22), C(255, 232, 180), false),
        new Theme("Yellow terminal", C(0, 0, 0), C(255, 226, 0), C(200, 178, 0), C(255, 255, 190), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        // "App" follows the theme and accent colour every Kinetik app shares.
        if (name == "App") { var l = KinetikSuite.Look.Current; return new Theme("App", l.Card, l.Text, l.Sub, l.Accent, l.Light); }
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
    static Color Bg { get { return KinetikSuite.Look.Current.Card; } }
    static Color Hi { get { return KinetikSuite.Look.Current.Hover; } }
    static Color Edge { get { return KinetikSuite.Look.Current.Border; } }
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
    public override Color CheckBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckSelectedBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckPressedBackground { get { return KinetikSuite.Look.Current.Hover; } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? KinetikSuite.Look.Current.Text : KinetikSuite.Look.Current.Sub;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = KinetikSuite.Look.Current.Sub;
        base.OnRenderArrow(e);
    }
}

// ======================================================================= The widget
class ClockForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string n);
    bool HubHid { get { return GetProp(Handle, "KinetikHubHidden") != IntPtr.Zero; } } // set by Kinetik Hub's "Hide all widgets"
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    readonly Settings c = Settings.Load();
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    int layer, winW, winH, margin;
    float u = 1;
    bool dragging, moved, resizing;
    int startScale, startW;
    Point grab, downAt;
    string lastKey;
    ThemePicker picker;
    readonly List<RunningTimer> timers = new List<RunningTimer>();
    readonly Pomodoro focus = new Pomodoro();
    readonly Watch watch = new Watch();
    readonly HashSet<string> firedAlarms = new HashSet<string>();
    readonly List<Action> live = new List<Action>(); // the labels in the open tools window that count along
    readonly System.Windows.Forms.Timer liveTimer = new System.Windows.Forms.Timer { Interval = 100 };
    KinetikSuite.SettingsWindow tools;
    string footer = ""; // the line under the places: running timers and the next calendar event

    public ClockForm()
    {
        Widgets.Attach(this, (dx, dy) => { c.X += dx; c.Y += dy; SetWindowPos(Handle, IntPtr.Zero, c.X, c.Y, 0, 0, 0x1 | 0x4 | 0x10); }, () => c.Save(), () => c.Locked);
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
        tick.Tick += (s, e) => { Tick(); CheckTools(); Render(false); };
        liveTimer.Tick += (s, e) => { foreach (var u in live.ToList()) try { u(); } catch { } };
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
        if (disposing) { tick.Dispose(); liveTimer.Dispose(); tray.Dispose(); menu.Dispose(); DisposeFonts(); shadow.Dispose(); if (textLayer != null) textLayer.Dispose(); }
        base.Dispose(disposing);
    }

    // Wakes up just after the next second (or minute) turns over, so the display never lags behind the system clock.
    void Tick()
    {
        var now = DateTime.Now;
        int ms = Fast ? 1000 - now.Millisecond : (60 - now.Second) * 1000 - now.Millisecond;
        tick.Interval = Math.Max(50, ms + 20);
        tick.Start();
    }

    // ---- mouse: drag to move, right-click for the menu
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            downAt = Cursor.Position; moved = false;
            if (!c.Locked && OnGrip(e.Location)) { resizing = true; startScale = c.Scale; startW = winW - 2 * margin; }
            else if (!c.Locked) { dragging = true; grab = new Point(Cursor.Position.X - c.X, Cursor.Position.Y - c.Y); }
        }
        base.OnMouseDown(e);
    }

    // Bottom-right corner of the card: drag to resize (changes Size %).
    bool OnGrip(Point p) { int g = (int)(20 * u); return p.X > winW - margin - g && p.Y > winH - margin - g && p.X < winW - margin && p.Y < winH - margin; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (resizing)
        {
            int s = (int)Math.Round(startScale * (startW + Cursor.Position.X - downAt.X) / (double)startW);
            c.Scale = Math.Max(50, Math.Min(300, s)); moved = true;
            Render(false);
            return;
        }
        if (!dragging) Cursor = !c.Locked && OnGrip(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
        if (dragging && (moved || Math.Abs(Cursor.Position.X - downAt.X) + Math.Abs(Cursor.Position.Y - downAt.Y) > 4))
        {
            moved = true;
            var p = Snap(new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y));
            Widgets.Drag(this, p.X - c.X, p.Y - c.Y);
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
            if ((dragging || resizing) && moved) c.Save(); Widgets.Commit(this);
            dragging = resizing = false;
            Cursor = Cursors.Default;
        }
        else if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    // Pulls the card flush to a screen edge (with a small gap) when it's dragged close to one.
    Point Snap(Point p)
    {
        if (!c.Snap) return p;
        p = Widgets.Snap(this, p);
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
    // "Today · same time · Sun", shortened to what fits: drop "Today · ", then the weekday, then the day word entirely.
    string OffsetLine(Graphics g, DateTime local, DateTime there, float width)
    {
        string full = OffsetText(local, there) + " · " + there.ToString("ddd");
        foreach (var t in new[] { full, full.Replace("Today · ", ""), OffsetText(local, there).Replace("Today · ", ""), OffsetText(local, there).Split((char)183).Last().Trim() })
            if (g.MeasureString(t, subF, 1000, fmtL).Width <= width) return t;
        return full;
    }

    // 0 and 1 are the original styles. 2 to 5 keep the same multi-city card with a different row.
    static readonly string[] StyleNames = { "Dials", "Compact", "Daylight", "Orbit", "Big type", "Galaxy" };
    bool Dial { get { return c.Style == 0 || c.Style == 3 || c.Style == 5; } }
    bool TwoLine { get { return Dial || c.Style == 2 || c.Style == 4; } }
    float RowH { get { return (c.Style == 1 ? 40 : c.Style == 4 ? 60 : c.Style == 2 ? 58 : 54) * u; } }

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
        if (!Program.Demo && places.Count > 0) Widgets.Status(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, places[0].Tz).ToString(c.Use24h ? "HH:mm" : "h:mm tt") + " " + places[0].Name);
        footer = FooterText(nowUtc);
        string key = c.Places + "|" + footer + "|" + c.Style + c.ThemeName + c.Use24h + c.Seconds + c.ShowOffset + c.Width + c.Scale + c.Opacity
            + "|" + nowUtc.ToString(Fast ? "HHmmss" : "HHmm") + Theme.Get(c.ThemeName).Name;
        if (key == lastKey && !force) return;
        lastKey = key;

        var screen = c.X == int.MinValue ? Screen.PrimaryScreen : Screen.FromPoint(new Point(c.X + 40, c.Y + 40));
        u = DeviceDpi / 96f * c.Scale / 100f;
        margin = (int)(14 * u);
        float pad = 14 * u;
        int cardW = (int)(c.Width * u), cardH = (int)(pad * 2 + Math.Max(1, places.Count) * RowH + (footer != "" ? 26 * u : 0));
        winW = cardW + margin * 2; winH = cardH + margin * 2;

        bool wantThrough = c.Locked && c.ClickThrough;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, wantThrough ? ex | 0x20 : ex & ~0x20); // TRANSPARENT: clicks fall through

        EnsurePosition();
        var bmp = surface.Canvas(winW, winH);
        using (var g = Graphics.FromImage(bmp)) PaintCard(g, places, nowUtc, cardW, cardH, pad);
        var shot = Program.Demo ? Environment.GetEnvironmentVariable("KINETIK_SHOT") : null; // demo only: save the rendered card for screenshots
        if (shot != null) try { bmp.Save(shot); } catch { }
        surface.Push(Handle, c.X, c.Y);
        Widgets.Publish(Handle, margin); ShowWindow(Handle, Widgets.Hidden || HubHid ? 0 : 8); // SW_SHOWNA
        if (layer == 0) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);       // bottom of the pile
        else if (layer == 2) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // stay on top
    }

    Bitmap textLayer;
    readonly ShadowCache shadow = new ShadowCache();
    string Fam { get { return c.FontName != "" && Draw.HasFont(c.FontName) ? c.FontName : Theme.IsTerm(c.ThemeName) ? Theme.Mono : Draw.Family; } }

    // The card's fonts, made again only when the font, size or style changes rather than every second.
    Font nameF, subF, timeF, ampmF;
    string fontKey;
    static readonly StringFormat fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat fmtL = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

    void EnsureFonts()
    {
        float dpiFix = DeviceDpi / 96f;
        var key = Fam + "|" + u + "|" + dpiFix + "|" + c.Style + c.Seconds;
        if (key == fontKey) return;
        DisposeFonts();
        var fam = Fam;
        nameF = new Font(fam, 11 * u / dpiFix, FontStyle.Bold, GraphicsUnit.Point);
        subF = new Font(fam, 8.5f * u / dpiFix, GraphicsUnit.Point);
        timeF = new Font(fam, (c.Style == 4 ? (c.Seconds ? 20 : 25) : Dial ? 19 : 16) * u / dpiFix, c.Style == 4 ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
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
            if (c.Style == 5)
            {
                // galaxy: a diagonal accent tint and a gradient edge from the accent through blue to the line colour
                using (var b = new LinearGradientBrush(card, Color.FromArgb(alpha * 55 / 255, th.Accent), Color.FromArgb(0, th.Accent), 45f) { InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(alpha * 55 / 255, th.Accent), Color.FromArgb(0, th.Accent), Color.FromArgb(0, th.Accent) }, Positions = new[] { 0f, 0.45f, 1f } } }) g.FillPath(b, p);
                using (var eb = new LinearGradientBrush(card, th.Accent, th.Line, 45f) { InterpolationColors = new ColorBlend { Colors = new[] { th.Accent, Draw.Mix(th.Accent, Color.FromArgb(29, 123, 219), 0.6f), th.Line }, Positions = new[] { 0f, 0.4f, 1f } } })
                using (var pen = new Pen(eb, 1.6f * u)) g.DrawPath(pen, p);
            }
            else using (var pen = new Pen(th.Line, 1)) g.DrawPath(pen, p);
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
                if (Dial)
                {
                    float d = (c.Style == 3 ? 44 : 36) * u;
                    var dr = new RectangleF(x, y + (rh - d) / 2, d, d);
                    if (c.Style == 3) { DrawOrbit(g, dr, there, th); dr.Inflate(-6 * u, -6 * u); }
                    if (c.Style == 5) // galaxy: a soft accent halo behind each dial
                        foreach (var k in new[] { 6f, 3f }) using (var hb = new SolidBrush(Color.FromArgb(k > 4 ? 16 : 30, th.Accent))) g.FillEllipse(hb, RectangleF.Inflate(dr, k * u, k * u));
                    DrawDial(g, dr, there, th);
                    x += d + 12 * u;
                }
                else if (TwoLine) { }
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
                if (TwoLine && c.Style == 2)
                {
                    // daylight: the city's 24 hours as a bar, daytime lit, with a glowing marker for now
                    float bh = 3.5f * u, by = y + rh - 11 * u, bx = x, bw = card.Right - pad - x;
                    g.DrawString(name, nameF, text, new RectangleF(x, y + rh * 0.02f, right - x, rh * 0.5f), fmtL);
                    g.DrawString(OffsetLine(g, local, there, right - x), subF, sub, new RectangleF(x, y + rh * 0.34f, right - x, rh * 0.4f), fmtL);
                    using (var pb = new SolidBrush(Color.FromArgb(70, th.Sub))) g.FillRectangle(pb, bx, by, bw, bh);
                    using (var pb = new SolidBrush(Color.FromArgb(200, 255, 196, 64))) g.FillRectangle(pb, bx + bw * 0.25f, by, bw * 0.5f, bh);
                    float mx = bx + bw * (float)((there.Hour + there.Minute / 60.0) / 24.0);
                    var mc = there.Hour >= 6 && there.Hour < 18 ? Color.FromArgb(255, 196, 64) : th.Accent; // amber by day, accent by night
                    using (var pb = new SolidBrush(Color.FromArgb(70, mc))) g.FillEllipse(pb, mx - 7 * u, by + bh / 2 - 7 * u, 14 * u, 14 * u);
                    using (var pb = new SolidBrush(th.Text)) g.FillEllipse(pb, mx - 3 * u, by + bh / 2 - 3 * u, 6 * u, 6 * u);
                }
                else if (c.Style == 4)
                {
                    // big type: the name small above the time, with a thin line that fills through the hour
                    g.DrawString(name.ToUpperInvariant(), subF, sub, new RectangleF(x, y + rh * 0.04f, right - x, rh * 0.3f), fmtL);
                    g.DrawString(OffsetText(local, there).Split('·').Last().Trim() + " · " + there.ToString("ddd"), subF, sub, new RectangleF(x, y + rh * 0.62f, right - x, rh * 0.3f), fmtL);
                    float ly = y + rh - 6 * u, lw = card.Right - pad - x;
                    using (var pb = new SolidBrush(Color.FromArgb(50, th.Sub))) g.FillRectangle(pb, x, ly, lw, 2 * u);
                    using (var pb = new SolidBrush(th.Accent)) g.FillRectangle(pb, x, ly, lw * (there.Minute * 60 + there.Second) / 3600f, 2 * u);
                }
                else if (c.ShowOffset && TwoLine)
                {
                    g.DrawString(name, nameF, text, new RectangleF(x, y + rh * 0.08f, right - x, rh / 2), fmtL);
                    g.DrawString(OffsetLine(g, local, there, right - x), subF, sub, new RectangleF(x, y + rh * 0.45f, right - x, rh / 2), fmtL);
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
            if (footer != "")
            {
                using (var pen = new Pen(th.Line, 1)) g.DrawLine(pen, card.X + pad, y, card.Right - pad, y);
                g.DrawString(footer, subF, sub, new RectangleF(card.X + pad, y, card.Width - pad * 2, 26 * u), fmtL);
            }
        }
    }

    // A small analog face: light by day, dark by night, with the theme's accent as the second hand.
    // A ring of the city's 24 hours, noon at the top: the daytime half lit, with a glowing dot where it is now.
    void DrawOrbit(Graphics g, RectangleF r, DateTime t, Theme th)
    {
        var ring = r; ring.Inflate(-2 * u, -2 * u);
        using (var pen = new Pen(Color.FromArgb(90, th.Sub), 2.2f * u)) g.DrawEllipse(pen, ring);
        using (var pen = new Pen(Color.FromArgb(210, 255, 196, 64), 2.2f * u)) g.DrawArc(pen, ring, 180, 180);
        double a = ((t.Hour + t.Minute / 60.0) / 24.0 * 360 + 90) * Math.PI / 180;
        float cx = ring.X + ring.Width / 2 + (float)Math.Cos(a) * ring.Width / 2, cy = ring.Y + ring.Height / 2 + (float)Math.Sin(a) * ring.Height / 2;
        using (var b = new SolidBrush(Color.FromArgb(70, th.Accent))) g.FillEllipse(b, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u);
        using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, cx - 2.6f * u, cy - 2.6f * u, 5.2f * u, 5.2f * u);
    }

    void DrawDial(Graphics g, RectangleF r, DateTime t, Theme th)
    {
        float h = t.Hour + t.Minute / 60f;
        // day 7–18, dusk/dawn blend over an hour on each side
        float dayness = h < 6 ? 0 : h < 7 ? h - 6 : h < 18 ? 1 : h < 19 ? 19 - h : 0;
        Color dayFace = th.Light ? Color.White : Color.FromArgb(236, 238, 244), nightFace = th.Light ? Color.FromArgb(40, 44, 56) : Color.FromArgb(52, 56, 70);
        Color face = Draw.Mix(nightFace, dayFace, dayness);
        Color ink = Draw.Mix(Color.FromArgb(236, 238, 244), Color.FromArgb(28, 30, 38), dayness);
        // terminal themes: a phosphor outline, lit a little by day
        if (th.Term) { face = Draw.Mix(th.Bg, th.Text, 0.06f + 0.12f * dayness); ink = th.Text; }

        using (var b = new SolidBrush(face)) g.FillEllipse(b, r);
        using (var pen = new Pen(th.Term ? th.Sub : th.Line, th.Term ? 1.2f * u : 1)) g.DrawEllipse(pen, r);

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
        if (c.Seconds && c.Style == 5) hand(t.Second / 60.0, 0.8f, 3.4f * u, Color.FromArgb(55, th.Accent)); // soft glow under the seconds hand
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

    // ---- alarms, timers, stopwatch, focus, meeting planner
    string FooterText(DateTime nowUtc)
    {
        var parts = new List<string>();
        var now = DateTime.Now;
        if (focus.Active) parts.Add(focus.Name + " " + Dur.Span(focus.Remaining(now)) + (focus.Running ? "" : " (paused)"));
        var next = timers.OrderBy(t => t.End).FirstOrDefault();
        if (next != null) parts.Add("Timer " + Dur.Span(next.End - now) + (timers.Count > 1 ? " +" + (timers.Count - 1) : ""));
        if (watch.Running) parts.Add("Stopwatch " + Dur.Span(watch.Elapsed(now)));
        if (c.ShowNext) { var n = NextUp.Text(NextUp.Read(), now, c.Use24h); if (n != null) parts.Add("Next: " + n); }
        return string.Join("  ·  ", parts);
    }

    bool Fast { get { return c.Seconds || timers.Count > 0 || focus.Running || watch.Running; } }

    void Notify(string title, string text)
    {
        try { tray.ShowBalloonTip(8000, title, text, ToolTipIcon.None); } catch { }
        System.Media.SystemSounds.Asterisk.Play();
    }

    // Alarms due this minute, timers that have run out and focus phases that have ended.
    void CheckTools()
    {
        if (Program.Demo) return;
        var now = DateTime.Now;
        var list = Alarms.Parse(c.Alarms);
        var due = Alarms.Due(list, now, firedAlarms);
        if (due.Count > 0) { c.Alarms = Alarms.Join(list); c.Save(); foreach (var a in due) RingAlarm(a); RefreshTools("Alarms"); }
        foreach (var t in timers.Where(t => t.End <= now).ToList())
        {
            timers.Remove(t);
            Notify("Timer finished", t.Name != "" ? t.Name : "Your timer is done.");
            RefreshTools("Timer");
        }
        int ended;
        if (focus.Tick(now, c.FocusMin, c.ShortMin, c.LongMin, out ended))
        {
            Notify(ended == 0 ? "Focus session done" : "Break over", ended == 0 ? (focus.Phase == 2 ? "Time for a long break: " + c.LongMin + " minutes." : "Time for a break: " + c.ShortMin + " minutes.") : "Back to focus: " + c.FocusMin + " minutes.");
            RefreshTools("Focus");
        }
    }

    void RingAlarm(Alarm a)
    {
        string time = DateTime.Today.AddHours(a.Hour).AddMinutes(a.Minute).ToString(c.Use24h ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        var f = new AlarmForm(time, a.Label, () =>
        {
            var soon = DateTime.Now.AddMinutes(5); // snooze: a one-off alarm five minutes on
            var l = Alarms.Parse(c.Alarms);
            l.Add(new Alarm { Hour = soon.Hour, Minute = soon.Minute, Days = 0, Label = a.Label == "" ? "Snoozed" : a.Label });
            c.Alarms = Alarms.Join(l); c.Save(); RefreshTools("Alarms");
        });
        f.Show();
    }

    string toolsPage;
    void RefreshTools(string page) { if (tools != null && !tools.IsDisposed && toolsPage == page) tools.ShowPage(page); }
    void Started() { Tick(); Render(true); }

    // Alarms and timers…: its own window, in the same layout as Settings.
    public void ShowTools(string page = null)
    {
        if (tools != null && !tools.IsDisposed) { tools.Activate(); if (page != null) tools.ShowPage(page); return; }
        var pages = new List<KinetikSuite.SettingsWindow.Page>
        {
            new KinetikSuite.SettingsWindow.Page { Name = "Alarms", Glyph = "clock", Sub = "Ring at a time you set, on the days you choose.", Build = BuildAlarms },
            new KinetikSuite.SettingsWindow.Page { Name = "Timer", Glyph = "timer", Sub = "Count down from any length of time.", Build = BuildTimer },
            new KinetikSuite.SettingsWindow.Page { Name = "Stopwatch", Glyph = "timer", Sub = "Time something, with laps.", Build = BuildStopwatch },
            new KinetikSuite.SettingsWindow.Page { Name = "Focus", Glyph = "clock", Sub = "Work in sessions with breaks between (Pomodoro).", Build = BuildFocus },
            new KinetikSuite.SettingsWindow.Page { Name = "Meeting", Glyph = "places", Sub = "When two places are both at work.", Build = BuildMeeting },
        };
        tools = new KinetikSuite.SettingsWindow("Kinetik Clock", Program.Version, pages, page);
        tools.Text = "Kinetik Clock: alarms and timers";
        liveTimer.Start();
        tools.FormClosed += (s, e) => { liveTimer.Stop(); live.Clear(); tools = null; toolsPage = null; };
        tools.Show();
    }

    Label LiveLabel(KinetikSuite.SettingsWindow.Builder b, float pt, Func<string> text)
    {
        var l = new Label { Font = new Font("Segoe UI", pt, FontStyle.Bold), ForeColor = KinetikSuite.Look.Current.Text, BackColor = KinetikSuite.Look.Current.Card, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Size = new Size((int)(150 * b.K), (int)(32 * b.K)), Text = text() };
        live.Add(() => { var t = text(); if (l.Text != t) l.Text = t; });
        return l;
    }

    void BuildAlarms(KinetikSuite.SettingsWindow.Builder b)
    {
        toolsPage = "Alarms"; live.Clear();
        b.Section("Alarms");
        var list = Alarms.Parse(c.Alarms);
        if (list.Count == 0) b.Hint("No alarms yet.", 1);
        for (int i = 0; i < list.Count; i++)
        {
            int idx = i; var a = list[i];
            Action<Action<List<Alarm>>> edit = f => { var l = Alarms.Parse(c.Alarms); if (idx < l.Count) f(l); c.Alarms = Alarms.Join(l); c.Save(); b.Refresh(); };
            string time = DateTime.Today.AddHours(a.Hour).AddMinutes(a.Minute).ToString(c.Use24h ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
            b.Buttons(time + (a.Label != "" ? "  ·  " + a.Label : ""), Alarms.DaysText(a.Days) + (a.On ? "" : "  ·  off"),
                b.Button(a.On ? "On" : "Off", () => edit(l => l[idx].On = !l[idx].On), a.On),
                b.Button("Repeat", () => edit(l => l[idx].Days = Alarms.NextRepeat(l[idx].Days))),
                b.Button("Remove", () => edit(l => l.RemoveAt(idx))));
        }
        b.Buttons("New alarm", "A time such as 7:30 or 6:45pm", b.Button("Add…", () =>
        {
            var t = Prompt("New alarm", "Time (for example 7:30 or 6:45pm):", "7:00");
            if (t == null) return;
            var span = Alarms.ParseTime(t);
            if (span == null) { MessageBox.Show("That isn't a time. Try 7:30 or 6:45pm.", "Kinetik Clock"); return; }
            var label = Prompt("New alarm", "Label (optional):", "") ?? "";
            var l = Alarms.Parse(c.Alarms);
            l.Add(new Alarm { Hour = span.Value.Hours, Minute = span.Value.Minutes, Label = label.Trim() });
            c.Alarms = Alarms.Join(l.OrderBy(x => x.Hour * 60 + x.Minute)); c.Save(); b.Refresh();
        }, true));
        b.Hint("An alarm opens a window you can snooze for five minutes or dismiss, and plays a sound until you do. Alarms ring while Kinetik Clock is running.", 2);
    }

    void BuildTimer(KinetikSuite.SettingsWindow.Builder b)
    {
        toolsPage = "Timer"; live.Clear();
        Action<TimeSpan> start = len => { timers.Add(new RunningTimer { End = DateTime.Now + len, Name = Dur.Span(len) + " timer" }); Started(); b.Refresh(); };
        b.Section("Start a timer");
        b.Buttons("Minutes", "Tap one to start", new[] { 1, 5, 10, 15, 30, 60 }.Select(m => { int mm = m; return b.Small(m.ToString(), () => start(TimeSpan.FromMinutes(mm))); }).ToArray());
        b.Buttons("Another length", "90s, 12m, 1h30m or 12:30", b.Button("Start…", () =>
        {
            var t = Prompt("Timer", "How long? (for example 90s, 12m, 1h30m or 12:30)", "10m");
            if (t == null) return;
            var len = Dur.ParseDuration(t);
            if (len == null || len.Value.TotalSeconds < 1 || len.Value.TotalHours > 24) { MessageBox.Show("That isn't a length of time. Try 90s, 12m or 1h30m.", "Kinetik Clock"); return; }
            start(len.Value);
        }, true));
        b.Section("Running");
        if (timers.Count == 0) b.Hint("Nothing running. Timers count down on the clock, too.", 1);
        foreach (var t in timers.OrderBy(x => x.End).ToList())
        {
            var tt = t;
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = KinetikSuite.Look.Current.Card, Margin = Padding.Empty };
            flow.Controls.Add(LiveLabel(b, 13f, () => Dur.Span(tt.End - DateTime.Now)));
            var cancel = b.Small("✕", () => { timers.Remove(tt); Started(); b.Refresh(); });
            cancel.Margin = new Padding((int)(6 * b.K), 0, 0, 0);
            flow.Controls.Add(cancel);
            flow.Size = flow.PreferredSize;
            b.Row(tt.Name, "Ends " + tt.End.ToString(c.Use24h ? "HH:mm:ss" : "h:mm:ss tt", CultureInfo.InvariantCulture), flow);
        }
    }

    void BuildStopwatch(KinetikSuite.SettingsWindow.Builder b)
    {
        toolsPage = "Stopwatch"; live.Clear();
        b.Section("Stopwatch");
        b.Row("Elapsed", null, LiveLabel(b, 18f, () => Watch.Text(watch.Elapsed(DateTime.Now))));
        b.Buttons("Controls", null,
            b.Button(watch.Running ? "Stop" : "Start", () => { if (watch.Running) watch.Stop(DateTime.Now); else watch.Start(DateTime.Now); Started(); b.Refresh(); }, true),
            b.Button("Lap", () => { watch.Lap(DateTime.Now); b.Refresh(); }),
            b.Button("Reset", () => { watch.Reset(); Started(); b.Refresh(); }));
        if (watch.Laps.Count > 0) b.Section("Laps");
        for (int i = watch.Laps.Count - 1; i >= 0; i--)
        {
            var total = new Label { Text = Watch.Text(watch.Laps[i]), Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = KinetikSuite.Look.Current.Text, BackColor = KinetikSuite.Look.Current.Card, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Size = new Size((int)(150 * b.K), (int)(32 * b.K)) };
            b.Row("Lap " + (i + 1), "+" + Watch.Text(watch.Split(i)), total);
        }
    }

    void BuildFocus(KinetikSuite.SettingsWindow.Builder b)
    {
        toolsPage = "Focus"; live.Clear();
        b.Section("Focus timer");
        b.Row(focus.Name, "Session " + (focus.Phase == 0 ? focus.Done % 4 + 1 : Math.Max(1, focus.Done % 4 == 0 ? 4 : focus.Done % 4)) + " of 4", LiveLabel(b, 18f, () => focus.Active ? Dur.Span(focus.Remaining(DateTime.Now)) : Dur.Span(Pomodoro.Length(focus.Phase, c.FocusMin, c.ShortMin, c.LongMin))));
        b.Buttons("Controls", focus.Running ? "Running. It carries on to the next phase by itself." : null,
            b.Button(focus.Running ? "Pause" : focus.Active ? "Resume" : "Start", () =>
            {
                if (focus.Running) focus.Pause(DateTime.Now); else focus.Start(DateTime.Now, c.FocusMin, c.ShortMin, c.LongMin);
                Started(); b.Refresh();
            }, true),
            b.Button("Skip", () => { focus.Advance(DateTime.Now, c.FocusMin, c.ShortMin, c.LongMin); Started(); b.Refresh(); }),
            b.Button("Reset", () => { focus.Reset(); Started(); b.Refresh(); }));
        b.Section("Lengths");
        var focusMins = new[] { 15, 20, 25, 30, 45, 50 }; var shortMins = new[] { 3, 5, 10 }; var longMins = new[] { 10, 15, 20, 30 };
        b.Choice("Focus", null, focusMins.Select(m => m + " minutes").ToArray(), Math.Max(0, Array.IndexOf(focusMins, c.FocusMin)), i => { c.FocusMin = focusMins[i]; c.Save(); }, 140);
        b.Choice("Short break", null, shortMins.Select(m => m + " minutes").ToArray(), Math.Max(0, Array.IndexOf(shortMins, c.ShortMin)), i => { c.ShortMin = shortMins[i]; c.Save(); }, 140);
        b.Choice("Long break", "After every fourth focus session", longMins.Select(m => m + " minutes").ToArray(), Math.Max(0, Array.IndexOf(longMins, c.LongMin)), i => { c.LongMin = longMins[i]; c.Save(); }, 140);
        b.Hint("Changes apply from the next phase.", 1);
    }

    void BuildMeeting(KinetikSuite.SettingsWindow.Builder b)
    {
        toolsPage = "Meeting"; live.Clear();
        var zones = TimeZoneInfo.GetSystemTimeZones().ToList();
        var ids = new[] { "" }.Concat(zones.Select(z => z.Id)).ToList();
        var names = new[] { "This PC" }.Concat(zones.Select(z => z.DisplayName)).ToArray();
        string idA = c.MeetA, idB = c.MeetB;
        if (idA == "" && idB == "") idB = c.List.Select(p => p.Zone).FirstOrDefault(z => z != "") ?? "";
        Func<string, TimeZoneInfo> tz = id => { try { return id == "" ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Local; } };
        Func<string, string> nameOf = id => id == "" ? "You" : (c.List.FirstOrDefault(p => p.Zone == id) ?? new Place { Name = tz(id).Id }).Name;
        b.Section("Places");
        b.Choice("You", "Where you are", names, Math.Max(0, ids.IndexOf(idA)), i => { c.MeetA = ids[i]; c.Save(); b.Refresh(); }, 320);
        b.Choice("Them", "Where they are", names, Math.Max(0, ids.IndexOf(idB)), i => { c.MeetB = ids[i]; c.Save(); b.Refresh(); }, 320);
        var starts = new[] { 6, 7, 8, 9, 10 }; var ends = new[] { 15, 16, 17, 18, 19, 20 };
        Func<int, string> hr = h => c.Use24h ? h + ":00" : (h % 12 == 0 ? 12 : h % 12) + (h < 12 ? "am" : "pm");
        b.Choice("Work starts", null, starts.Select(hr).ToArray(), Math.Max(0, Array.IndexOf(starts, c.WorkStart)), i => { c.WorkStart = starts[i]; c.Save(); b.Refresh(); }, 140);
        b.Choice("Work ends", null, ends.Select(hr).ToArray(), Math.Max(0, Array.IndexOf(ends, c.WorkEnd)), i => { c.WorkEnd = ends[i]; c.Save(); b.Refresh(); }, 140);
        b.Section("Today");
        TimeZoneInfo za = tz(idA), zb = tz(idB);
        var day = TimeZoneInfo.ConvertTime(DateTime.UtcNow, za).Date;
        b.Add(new MeetingStrip { A = za, B = zb, NameA = nameOf(idA), NameB = nameOf(idB), From = c.WorkStart, To = c.WorkEnd, Day = day, Size = new Size(b.Width, (int)(76 * b.K)), Margin = new Padding((int)(4 * b.K), 0, 0, (int)(8 * b.K)) });
        var overlap = Meeting.Overlap(za, zb, day, c.WorkStart, c.WorkEnd);
        var theirs = overlap.Select(h => TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(day.AddHours(h), DateTimeKind.Unspecified), za, zb).Hour).OrderBy(h => h).ToList();
        b.Hint(overlap.Count == 0 ? "No shared working hours with these settings. Wider working hours may help."
            : "Both at work: " + string.Join(", ", Meeting.Ranges(overlap, c.Use24h)) + " for you, which is " + string.Join(", ", Meeting.Ranges(theirs, c.Use24h)) + " for them.", 3);
    }

    void Changed() { c.Save(); Tick(); Render(true); }

    KinetikSuite.SettingsWindow settings;

    // Settings…: this app's own pages, then the Look, Placement, General and About pages every Kinetik widget shares.
    public void ShowSettings(string page = null)
    {
        if (settings != null && !settings.IsDisposed) { settings.Activate(); if (page != null) settings.ShowPage(page); return; }
        var pages = new List<KinetikSuite.SettingsWindow.Page>
        {
            new KinetikSuite.SettingsWindow.Page { Name = "Clocks", Glyph = "clock", Sub = "The places shown, and how their times look.", Build = b =>
            {
                b.Section("Places");
                var places = c.List;
                for (int i = 0; i < places.Count; i++)
                {
                    int idx = i; var p = places[i];
                    Action<List<Place>> apply = l => { c.List = l; Changed(); b.Refresh(); };
                    var buttons = new List<KinetikSuite.FlatButton>
                    {
                        b.Button("Rename…", () => { var n = Prompt("Rename", "Name shown on the clock:", p.Name); if (n == null) return; var l = c.List; l[idx].Name = n.Trim(); apply(l); }),
                    };
                    if (idx > 0) buttons.Add(b.Small("↑", () => { var l = c.List; var x = l[idx]; l.RemoveAt(idx); l.Insert(idx - 1, x); apply(l); }));
                    if (idx < places.Count - 1) buttons.Add(b.Small("↓", () => { var l = c.List; var x = l[idx]; l.RemoveAt(idx); l.Insert(idx + 1, x); apply(l); }));
                    buttons.Add(b.Button("Remove", () => { var l = c.List; l.RemoveAt(idx); apply(l); }));
                    b.Buttons(p.Name == "" ? "Local" : p.Name, p.Zone == "" ? "This PC's time zone" : p.Tz.DisplayName, buttons.ToArray());
                }
                b.Buttons("Add a place", "Any city or time zone", b.Button("Add…", () => { AddPlace(); b.Refresh(); }, true));
                b.Section("Style");
                b.Choice("Style", null, StyleNames, c.Style, v => { c.Style = v; Changed(); }, 140);
                b.Switch("24-hour time", null, c.Use24h, v => { c.Use24h = v; Changed(); });
                b.Switch("Show seconds", null, c.Seconds, v => { c.Seconds = v; Changed(); });
                b.Switch("Show time difference", "How far ahead or behind each place is", c.ShowOffset, v => { c.ShowOffset = v; Changed(); });
                b.Switch("Next calendar event", "A line under the clocks, from Kinetik Calendar", c.ShowNext, v => { c.ShowNext = v; Changed(); });
                b.Buttons("Windows date and time", "Your own time zone and clock sync", b.Button("Open", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:dateandtime") { UseShellExecute = true }); } catch { } }));
            } },
        };
        pages.AddRange(Common().ForSettings().Pages());
        settings = new KinetikSuite.SettingsWindow("Kinetik Clock", Program.Version, pages, page);
        settings.Show();
    }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Clock", Version = Program.Version,
            OpenSettings = () => ShowSettings(),
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
        menu.Items.Add(Item("Alarms and timers…", false, () => ShowTools(null)));

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
        for (int i = 0; i < StyleNames.Length; i++) { int v = i; style.DropDownItems.Add(Item(StyleNames[i], c.Style == v, () => { c.Style = v; Changed(); })); }
        style.DropDownItems.Add(new ToolStripSeparator());
        style.DropDownItems.Add(Item("24-hour time", c.Use24h, () => { c.Use24h = !c.Use24h; Changed(); }));
        style.DropDownItems.Add(Item("Show seconds", c.Seconds, () => { c.Seconds = !c.Seconds; Changed(); }));
        style.DropDownItems.Add(Item("Show time difference", c.ShowOffset, () => { c.ShowOffset = !c.ShowOffset; Changed(); }));
        menu.Items.Add(style);

        menu.Items.Add(Item("Windows date and time settings", false, () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:dateandtime") { UseShellExecute = true }); } catch { } }));
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
    public static Color Page { get { return KinetikSuite.Look.Current.Bg; } }
    public static Color Card { get { return KinetikSuite.Look.Current.Card; } }
    public static Color CardHi { get { return KinetikSuite.Look.Current.Hover; } }
    public static Color Accent { get { return KinetikSuite.Look.Current.Accent; } }
    public static Color TextCol { get { return KinetikSuite.Look.Current.Text; } }
    public static Color SubCol { get { return KinetikSuite.Look.Current.Sub; } }

    public ThemePicker(string current, Action<string> chosen)
    {

        HandleCreated += (s, e) => KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
        Text = "Kinetik Clock theme";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Page;
        float k = DeviceDpi / 96f;
        var flow = new FlowLayoutPanel { Left = (int)(16 * k), Top = (int)(16 * k), Width = (int)(3 * 118 * k), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size((int)(3 * 118 * k), 0), WrapContents = true, BackColor = Page };
        foreach (var name in new[] { "Auto", "App" }.Concat(Theme.All.Select(t => t.Name)))
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
            TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName == "App" ? "Match app theme" : ThemeName, f, new Rectangle(0, Height - I(26), Width, I(20)), selected ? ThemePicker.TextCol : ThemePicker.SubCol,
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

        HandleCreated += (s, e) => KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
        Text = "Add a place";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        ClientSize = new Size(460, 440);
        BackColor = KinetikSuite.Look.Current.Card; ForeColor = KinetikSuite.Look.Current.Text;
        Font = new Font("Segoe UI", 9.5f);
        Color field = KinetikSuite.Look.Current.Hover;

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
    public Action LookChanged;                                      // optional: after the App theme changes
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
        if (OpenTheme != null) items.Add(Item("Widget theme…", false, OpenTheme));
        items.Add(KinetikSuite.Look.MenuItem(LookChanged)); // the theme of every Kinetik app's windows and menus

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

    // The same options as the shared pages of a settings window (Look, Placement, General, About).
    public KinetikSuite.SettingsWindow.Common ForSettings()
    {
        return new KinetikSuite.SettingsWindow.Common
        {
            Title = Title, Version = Version, OpenTheme = OpenTheme,
            Scale = Scale, SetScale = SetScale, Scales = Scales, Widths = Widths, CardWidth = CardWidth, SetCardWidth = SetCardWidth,
            FontName = FontName, SetFontName = SetFontName, Fonts = Fonts.Where(HasFont).ToArray(),
            Opacity = Opacity, SetOpacity = SetOpacity, Opacities = Opacities,
            Layer = Layer, SetLayer = SetLayer, Locked = Locked, SetLocked = SetLocked, LockText = LockText,
            ClickThrough = ClickThrough, SetClickThrough = SetClickThrough, Snap = Snap, SetSnap = SetSnap,
            RunAtStartup = RunAtStartup, SetRunAtStartup = SetRunAtStartup, BackUp = BackUp, Restore = Restore, LastBackup = LastBackup,
        };
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
