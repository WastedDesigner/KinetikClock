using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// Compile alongside the app with /main:ClockFeatureTests (KinetikClock.cs, SuiteSupport.cs, Widgets.cs). Nothing is read from or saved to this PC.
// With a folder as the first argument it also draws the demo clock and the Meeting and Alarms pages there as PNGs.
static class ClockFeatureTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    [STAThread]
    static void Main(string[] args)
    {
        // alarms
        Check(Alarms.ParseTime("7:30") == new TimeSpan(7, 30, 0) && Alarms.ParseTime("07:30") == new TimeSpan(7, 30, 0) && Alarms.ParseTime("6:45pm") == new TimeSpan(18, 45, 0)
            && Alarms.ParseTime("6.45 PM") == new TimeSpan(18, 45, 0) && Alarms.ParseTime("7 am") == new TimeSpan(7, 0, 0) && Alarms.ParseTime("12am") == TimeSpan.Zero && Alarms.ParseTime("12pm") == new TimeSpan(12, 0, 0)
            && Alarms.ParseTime("1930") == new TimeSpan(19, 30, 0), "Times are read in the usual ways");
        Check(Alarms.ParseTime("25:00") == null && Alarms.ParseTime("7:75") == null && Alarms.ParseTime("13pm") == null && Alarms.ParseTime("soon") == null && Alarms.ParseTime("") == null, "Things that aren't times are refused");
        var list = new List<Alarm> { new Alarm { Hour = 7, Minute = 30, Label = "Wake | up", Days = Alarms.Weekdays }, new Alarm { Hour = 9, Minute = 0, On = false, Days = 0 } };
        var back = Alarms.Parse(Alarms.Join(list));
        Check(back.Count == 2 && back[0].Label == "Wake   up", "Alarms round trip, a | in a label is replaced");
        Check(back[0].Hour == 7 && back[0].Days == 62 && back[0].On && !back[1].On && back[1].Days == 0 && Alarms.Parse("garbage\n99:99|1||127").Count == 0, "Alarm fields survive, bad lines are dropped");
        var monday = new DateTime(2026, 11, 16, 7, 30, 20); // a Monday
        var fired = new HashSet<string>();
        var alarmList = new List<Alarm> { new Alarm { Hour = 7, Minute = 30, Days = Alarms.Weekdays }, new Alarm { Hour = 7, Minute = 30, Days = Alarms.Weekends, Label = "weekend" }, new Alarm { Hour = 7, Minute = 30, Days = 0, Label = "once" }, new Alarm { Hour = 7, Minute = 30, On = false, Label = "off" } };
        var due = Alarms.Due(alarmList, monday, fired);
        Check(due.Count == 2 && due.Any(a => a.Label == "") && due.Any(a => a.Label == "once"), "Only alarms for today that are on ring");
        Check(!alarmList[2].On, "A one-off alarm switches itself off");
        Check(Alarms.Due(alarmList, monday.AddSeconds(20), fired).Count == 0, "An alarm rings once in its minute");
        Check(Alarms.Due(alarmList, monday.AddDays(1), fired).Count == 1, "A repeating alarm rings again the next day");
        Check(Alarms.NextRepeat(127) == 62 && Alarms.NextRepeat(62) == 65 && Alarms.NextRepeat(65) == 0 && Alarms.NextRepeat(0) == 127, "Repeat cycles through the choices");

        // timers and the stopwatch
        Check(Dur.Span(TimeSpan.FromSeconds(272)) == "04:32" && Dur.Span(TimeSpan.FromSeconds(3725)) == "1:02:05" && Dur.Span(TimeSpan.FromSeconds(-5)) == "00:00" && Dur.Span(TimeSpan.FromMilliseconds(100)) == "00:01", "Countdown text");
        Check(Dur.ParseDuration("90s") == TimeSpan.FromSeconds(90) && Dur.ParseDuration("12m") == TimeSpan.FromMinutes(12) && Dur.ParseDuration("1h30m") == TimeSpan.FromMinutes(90) && Dur.ParseDuration("12:30") == new TimeSpan(0, 12, 30)
            && Dur.ParseDuration("10") == TimeSpan.FromMinutes(10) && Dur.ParseDuration("1h") == TimeSpan.FromHours(1) && Dur.ParseDuration("2m 30s") == new TimeSpan(0, 2, 30), "Lengths are read in the usual ways");
        Check(Dur.ParseDuration("") == null && Dur.ParseDuration("abc") == null && Dur.ParseDuration("0") == null && Dur.ParseDuration("m") == null, "Things that aren't lengths are refused");
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var w = new Watch();
        w.Start(t0); w.Lap(t0.AddSeconds(10)); w.Lap(t0.AddSeconds(25)); w.Stop(t0.AddSeconds(30)); w.Start(t0.AddSeconds(100));
        Check(w.Elapsed(t0.AddSeconds(105)) == TimeSpan.FromSeconds(35) && w.Split(0) == TimeSpan.FromSeconds(10) && w.Split(1) == TimeSpan.FromSeconds(15), "Stopwatch keeps its time across a stop, with lap splits");
        Check(Watch.Text(new TimeSpan(0, 0, 1, 2, 340)) == "01:02.34" && Watch.Text(new TimeSpan(1, 2, 3)) == "1:02:03.00", "Stopwatch text");
        w.Reset();
        Check(w.Laps.Count == 0 && w.Elapsed(t0) == TimeSpan.Zero, "Stopwatch resets");

        // focus timer
        var f = new Pomodoro(); int ended;
        f.Start(t0, 25, 5, 15);
        Check(f.Running && f.Phase == 0 && f.Remaining(t0) == TimeSpan.FromMinutes(25), "Focus starts with a focus session");
        Check(!f.Tick(t0.AddMinutes(24), 25, 5, 15, out ended), "Nothing happens before the session ends");
        Check(f.Tick(t0.AddMinutes(25), 25, 5, 15, out ended) && ended == 0 && f.Phase == 1 && f.Done == 1 && f.Running && f.Remaining(t0.AddMinutes(25)) == TimeSpan.FromMinutes(5), "A focus session is followed by a short break");
        var now = t0.AddMinutes(25);
        for (int i = 0; i < 3; i++)
        {
            now = now.AddMinutes(5); Check(f.Tick(now, 25, 5, 15, out ended) && ended == 1 && f.Phase == 0, "A break is followed by focus");
            now = now.AddMinutes(25); f.Tick(now, 25, 5, 15, out ended);
        }
        Check(f.Done == 4 && f.Phase == 2 && f.Remaining(now) == TimeSpan.FromMinutes(15), "Every fourth focus session earns a long break");
        f.Pause(now.AddMinutes(5));
        Check(!f.Running && f.Remaining(now.AddMinutes(99)) == TimeSpan.FromMinutes(10) && f.Active, "Pausing keeps what is left");
        f.Start(now.AddMinutes(100), 25, 5, 15);
        Check(f.Running && f.Remaining(now.AddMinutes(100)) == TimeSpan.FromMinutes(10), "Resuming carries on from there");
        f.Reset();
        Check(!f.Active && f.Phase == 0 && f.Done == 0, "Focus resets");

        // meeting planner
        TimeZoneInfo london, paris, melbourne;
        try { london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); paris = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); melbourne = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time"); }
        catch (TimeZoneNotFoundException) { Console.WriteLine("skipped the meeting planner: time zone data missing"); london = paris = melbourne = null; }
        if (london != null)
        {
            var winter = new DateTime(2026, 1, 15);
            var both = Meeting.Overlap(london, paris, winter, 9, 17);
            Check(both.SequenceEqual(Enumerable.Range(9, 7)), "London and Paris share seven working hours (London 9am to 4pm)");
            Check(Meeting.Overlap(london, melbourne, winter, 9, 17).Count == 0, "London and Melbourne never share a working hour");
            Check(Meeting.Overlap(london, london, winter, 9, 17).Count == 8, "A place always overlaps itself");
            Check(string.Join("|", Meeting.Ranges(both, false)) == "9am to 4pm" && string.Join("|", Meeting.Ranges(new List<int> { 9, 10, 14, 15 }, true)) == "9:00 to 11:00|14:00 to 16:00" && Meeting.Ranges(new List<int>(), false).Count == 0, "Shared hours are written as ranges");
        }

        // the next calendar event
        var start = DateTime.UtcNow.AddMinutes(25); var end = start.AddHours(1);
        var item = NextUp.Parse(start.Ticks + "\t" + end.Ticks + "\tDesign review\tStudio 2\tWork");
        Check(item != null && item.Title == "Design review" && item.Location == "Studio 2" && Math.Abs((item.Start - DateTime.Now).TotalMinutes - 25) < 1, "The next event is read from Calendar's file");
        Check(NextUp.Text(item, DateTime.Now, false) == "Design review in 25 min" || NextUp.Text(item, DateTime.Now, false) == "Design review in 26 min", "A near event reads as minutes away");
        var later = NextUp.Parse(DateTime.UtcNow.AddMinutes(180).Ticks + "\t" + DateTime.UtcNow.AddMinutes(240).Ticks + "\tLunch\t\tWork");
        var laterText = NextUp.Text(later, DateTime.Now, true);
        Check(laterText.StartsWith("Lunch ") && !laterText.Contains(" in "), "A later event reads as a time");
        Check(NextUp.Text(item, DateTime.Now.AddHours(2), false) == null && NextUp.Text(null, DateTime.Now, false) == null, "A started event is not shown");
        Check(NextUp.Parse("") == null && NextUp.Parse("x\ty\tz") == null && NextUp.Parse("0\t0\tTitle") == null, "Bad lines are ignored");

        Console.WriteLine("PASS: alarms, timers, stopwatch, focus timer, meeting planner and next event.");
        if (args.Length == 0) return;

        // the clock and two of the pages, drawn
        Directory.CreateDirectory(args[0]);
        Program.Demo = true; Program.DemoNext = true;
        Environment.SetEnvironmentVariable("KINETIK_SHOT", Path.Combine(args[0], "clock-next.png"));
        var clock = new ClockForm();
        clock.Show();
        Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
        foreach (var page in new[] { "Meeting", "Alarms", "Focus" })
        {
            clock.ShowTools(page);
            Application.DoEvents(); Thread.Sleep(400); Application.DoEvents();
            var win = Application.OpenForms.OfType<KinetikSuite.SettingsWindow>().First();
            win.StartPosition = FormStartPosition.Manual; win.Location = new Point(-3000, -3000);
            Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
            using (var bmp = new Bitmap(win.Width, win.Height)) { win.DrawToBitmap(bmp, new Rectangle(0, 0, win.Width, win.Height)); bmp.Save(Path.Combine(args[0], "clock-" + page + ".png")); }
            win.Close(); Application.DoEvents();
        }
        Environment.Exit(0);
    }
}
