// Kinetik widget layer (beta). GPL-3.0-or-later. Compiled into every widget app and the Widget Hub.
// Each widget window publishes its card margin as a window property; other widgets find it by enumerating windows.
// Groups and stacks live in %LOCALAPPDATA%\Kinetik\widgets.txt:  group=KinetikClock,KinetikWeather   stack=10;KinetikCalendar,KinetikFeeds
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public sealed class WidgetInfo { public IntPtr Hwnd; public string Name; public Rectangle Window, Card; }
public sealed class StackDef { public int Seconds = 10; public List<string> Members = new List<string>(); }

public static class Widgets
{
    const string Prop = "KinetikWidgetMargin";
    public const int CmdMove = 1, CmdCommit = 2, CmdHide = 3, CmdShow = 4;
    static readonly uint Msg = RegisterWindowMessage("KinetikWidgetCmd");
    public static bool Hidden;

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string n, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr c);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr i, int id);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static Rectangle Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }
    static string Dir { get { var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kinetik"); Directory.CreateDirectory(d); return d; } }
    static string LayoutFile { get { return Path.Combine(Dir, "widgets.txt"); } }
    static readonly string Self = Process.GetCurrentProcess().ProcessName;

    /// <summary>Call from a widget's constructor. moveBy shifts the window and saves nothing; save persists settings.
    /// scale/setScale are optional: they enable the bottom-right corner resize grip (setScale applies without saving).</summary>
    public static void Attach(Form f, Action<int, int> moveBy, Action save, Func<bool> locked, Func<int> scale = null, Action<int> setScale = null)
    {
        Hook hook = null;
        f.HandleCreated += (s, e) => { hook = new Hook(f, moveBy, save, locked, scale, setScale); SetProp(f.Handle, Prop, (IntPtr)1); };
        f.HandleDestroyed += (s, e) => { if (hook != null) hook.ReleaseHandle(); };
    }

    /// <summary>Publishes the card margin (window edge to card edge) so neighbours snap to the card, not its shadow.</summary>
    public static void Publish(IntPtr h, int margin) { SetProp(h, Prop, (IntPtr)(margin + 1)); }

    public static List<WidgetInfo> Enumerate(bool visibleOnly = true)
    {
        var list = new List<WidgetInfo>();
        var names = new Dictionary<uint, string>();
        EnumWindows((h, l) =>
        {
            var m = GetProp(h, Prop);
            if (m == IntPtr.Zero || (visibleOnly && !IsWindowVisible(h))) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            string name;
            if (!names.TryGetValue(pid, out name)) { try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; } names[pid] = name; }
            var w = Rect(h); int mg = (int)m - 1;
            list.Add(new WidgetInfo { Hwnd = h, Name = name, Window = w, Card = new Rectangle(w.X + mg, w.Y + mg, Math.Max(1, w.Width - 2 * mg), Math.Max(1, w.Height - 2 * mg)) });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static void Send(IntPtr h, int cmd, int a = 0, int b = 0) { PostMessage(h, Msg, (IntPtr)cmd, (IntPtr)((b << 16) | (a & 0xFFFF))); }

    // ---- layout file
    public static void Load(out List<List<string>> groups, out List<StackDef> stacks)
    {
        groups = new List<List<string>>(); stacks = new List<StackDef>();
        string[] lines; try { lines = File.ReadAllLines(LayoutFile, Encoding.UTF8); } catch { return; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("group=")) groups.Add(line.Substring(6).Split(',').Select(x => x.Trim()).Where(x => x != "").ToList());
            else if (line.StartsWith("stack="))
            {
                var parts = line.Substring(6).Split(';'); int sec;
                if (parts.Length == 2 && int.TryParse(parts[0], out sec) && sec >= 2)
                    stacks.Add(new StackDef { Seconds = Math.Min(sec, 3600), Members = parts[1].Split(',').Select(x => x.Trim()).Where(x => x != "").ToList() });
            }
        }
    }

    public static void Save(List<List<string>> groups, List<StackDef> stacks)
    {
        var sb = new StringBuilder();
        foreach (var g in groups.Where(g => g.Count > 1)) sb.AppendLine("group=" + string.Join(",", g));
        foreach (var s in stacks.Where(s => s.Members.Count > 1)) sb.AppendLine("stack=" + s.Seconds + ";" + string.Join(",", s.Members));
        var tmp = LayoutFile + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(LayoutFile)) File.Replace(tmp, LayoutFile, null); else File.Move(tmp, LayoutFile);
    }

    static List<List<string>> cachedGroups = new List<List<string>>(); static DateTime cachedAt;
    static List<string> GroupMates()
    {
        if ((DateTime.UtcNow - cachedAt).TotalMilliseconds > 700)
        {
            List<StackDef> st; Load(out cachedGroups, out st); cachedAt = DateTime.UtcNow;
        }
        var g = cachedGroups.FirstOrDefault(x => x.Contains(Self));
        return g == null ? new List<string>() : g.Where(x => x != Self).ToList();
    }

    // ---- called from the widgets' drag code
    /// <summary>Snaps a dragged window position to neighbouring widgets' cards (edge to edge with a small gap, or flush).</summary>
    public static Point Snap(Form f, Point p)
    {
        var me = Rect(f.Handle); var mg = (int)GetProp(f.Handle, Prop) - 1; if (mg < 0) mg = 0;
        var card = new Rectangle(p.X + mg, p.Y + mg, Math.Max(1, me.Width - 2 * mg), Math.Max(1, me.Height - 2 * mg));
        const int gap = 8, reach = 14;
        int bx = reach, by = reach, ax = 0, ay = 0;
        var mates = GroupMates();
        foreach (var o in Enumerate())
        {
            if (o.Hwnd == f.Handle || mates.Contains(o.Name)) continue;
            var r = o.Card;
            bool near = card.Top < r.Bottom + reach && card.Bottom > r.Top - reach, nearV = card.Left < r.Right + reach && card.Right > r.Left - reach;
            if (near) foreach (var d in new[] { r.Right + gap - card.Left, r.Left - gap - card.Right, r.Left - card.Left, r.Right - card.Right }) if (Math.Abs(d) < Math.Abs(bx)) { bx = d; ax = d; }
            if (nearV) foreach (var d in new[] { r.Bottom + gap - card.Top, r.Top - gap - card.Bottom, r.Top - card.Top, r.Bottom - card.Bottom }) if (Math.Abs(d) < Math.Abs(by)) { by = d; ay = d; }
        }
        return new Point(p.X + ax, p.Y + ay);
    }

    /// <summary>Moves this widget's group mates by the same delta. Call before applying the new position.</summary>
    public static void Drag(Form f, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var mates = GroupMates(); if (mates.Count == 0) return;
        foreach (var o in Enumerate()) if (o.Hwnd != f.Handle && mates.Contains(o.Name)) Send(o.Hwnd, CmdMove, dx, dy);
    }

    public static void Commit(Form f)
    {
        var mates = GroupMates();
        foreach (var o in Enumerate()) if (o.Hwnd != f.Handle && mates.Contains(o.Name)) Send(o.Hwnd, CmdCommit);
    }

    /// <summary>One line of state for the Widget Hub to show (for example "21:53 London"). Skipped when unchanged.</summary>
    static string lastStatus;
    public static void Status(string text)
    {
        if (text == lastStatus) return;
        lastStatus = text;
        try
        {
            var d = Path.Combine(Dir, "Status"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, Self + ".txt"), text ?? "", new UTF8Encoding(false));
        }
        catch { }
    }

    sealed class Hook : NativeWindow
    {
        readonly Form form; readonly Action<int, int> moveBy; readonly Action save; readonly Func<bool> locked; readonly Func<int> scale; readonly Action<int> setScale;
        bool resizing; int startScale, startW; Point downAt;

        public Hook(Form f, Action<int, int> mv, Action sv, Func<bool> lk, Func<int> sc, Action<int> ss)
        { form = f; moveBy = mv; save = sv; locked = lk; scale = sc; setScale = ss; AssignHandle(f.Handle); }

        bool InGrip(Point screen)
        {
            if (setScale == null || locked()) return false;
            var r = Rect(Handle); int mg = Math.Max(0, (int)GetProp(Handle, Prop) - 1);
            return screen.X > r.Right - mg - 20 && screen.X < r.Right - mg && screen.Y > r.Bottom - mg - 20 && screen.Y < r.Bottom - mg;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Msg)
            {
                int cmd = (int)m.WParam, a = (short)((int)m.LParam & 0xFFFF), b = (short)(((int)m.LParam >> 16) & 0xFFFF);
                if (cmd == CmdMove) moveBy(a, b);
                else if (cmd == CmdCommit) save();
                else if (cmd == CmdHide) { Hidden = true; ShowWindow(Handle, 0); }
                else if (cmd == CmdShow) { Hidden = false; ShowWindow(Handle, 8); }
                return;
            }
            if (m.Msg == 0x201 && InGrip(Cursor.Position)) // WM_LBUTTONDOWN on the resize grip
            {
                resizing = true; startScale = scale(); downAt = Cursor.Position;
                startW = Math.Max(1, Rect(Handle).Width - 2 * Math.Max(0, (int)GetProp(Handle, Prop) - 1));
                SetCapture(Handle); return;
            }
            if (resizing && m.Msg == 0x200) // WM_MOUSEMOVE
            {
                int s = (int)Math.Round(startScale * (startW + Cursor.Position.X - downAt.X) / (double)startW);
                setScale(Math.Max(50, Math.Min(300, s))); return;
            }
            if (resizing && m.Msg == 0x202) { resizing = false; ReleaseCapture(); save(); return; }
            if (m.Msg == 0x20 && (resizing || InGrip(Cursor.Position))) { SetCursor(LoadCursor(IntPtr.Zero, 32642)); m.Result = (IntPtr)1; return; } // IDC_SIZENWSE
            base.WndProc(ref m);
        }
    }
}
