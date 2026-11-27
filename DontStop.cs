// Don't Stop! - tray toggle for "ignore lid close" and "block idle sleep".
// Build: build.cmd (uses the C# compiler that ships with Windows / .NET Framework 4.x)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Cfg
{
    public static readonly Color Pink = Color.FromArgb(0xFF, 0x00, 0x86);
    public static readonly Color Grey = Color.FromArgb(150, 150, 150);
    public const string Font = "Impact";

    // Heat cutoff (only while lid-ignore is on AND the lid is closed)
    public const string BoardZone = @"\_TZ.TZS0";   public const float BoardLimit = 70f;
    public const string ChassisZone = @"\_TZ.TZS1"; public const float ChassisLimit = 50f;
    public const int CheckMs = 30000;
    public const int StrikesNeeded = 2;              // consecutive over-limit readings before hibernating

    public const string RegPath = @"Software\DontStop";
}

static class Native
{
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr h);

    [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint val);
    [DllImport("powrprof.dll")] public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint val);
    [DllImport("powrprof.dll")] public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint val);
    [DllImport("powrprof.dll")] public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint val);
    [DllImport("powrprof.dll")] public static extern bool IsPwrHibernateAllowed();

    [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    public const uint MF_CHECKED = 0x8, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr m, uint flags, IntPtr id, string text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr h, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static Guid SUB_BUTTONS = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
    public static Guid LIDACTION = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
    public static Guid GUID_ACTIVE_POWERSCHEME = new Guid("31f9f286-5084-42fe-b720-2b0264993763");
    public static Guid GUID_LIDSWITCH_STATE_CHANGE = new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");

    public static Guid ActiveScheme()
    {
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) throw new Exception("PowerGetActiveScheme failed");
        Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
        LocalFree(p);
        return g;
    }
}

class App : NativeWindow
{
    const int WM_POWERBROADCAST = 0x218, WM_ENDSESSION = 0x16, WM_CLOSE = 0x10, WM_SETTINGCHANGE = 0x1A;
    const int PBT_POWERSETTINGCHANGE = 0x8013, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;

    NotifyIcon tray = new NotifyIcon();
    System.Windows.Forms.Timer heatTimer = new System.Windows.Forms.Timer();
    IntPtr curIcon = IntPtr.Zero;
    RegistryKey reg = Registry.CurrentUser.CreateSubKey(Cfg.RegPath);

    bool lidOn, idleOn, lidClosed, cleanedUp;
    int strikes;
    string pendingNote;
    PerformanceCounter boardCtr, chassisCtr;

    public App()
    {
        CreateHandle(new CreateParams { Caption = "DontStop" }); // hidden; title lets scripts post WM_CLOSE

        lidOn = GetInt("LidOn") == 1;
        idleOn = GetInt("IdleOn") == 1;

        // Crash recovery: last run didn't exit cleanly while the lid setting was changed.
        if (GetInt("Applied") == 1)
        {
            bool crashed = GetInt("Running") == 1;
            RestoreLid();
            if (crashed)
            {
                lidOn = false;
                reg.SetValue("LidOn", 0);
                pendingNote = "Restored your lid setting after an unexpected exit.";
            }
        }
        reg.SetValue("Running", 1);

        tray.MouseClick += delegate (object s, MouseEventArgs e) {
            if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
            if (e.Button != MouseButtons.Left) return;
            bool any = lidOn || idleOn;
            SetState(!any, !any, true);
        };

        heatTimer.Interval = Cfg.CheckMs;
        heatTimer.Tick += delegate { CheckHeat(); };

        Guid g1 = Native.GUID_ACTIVE_POWERSCHEME, g2 = Native.GUID_LIDSWITCH_STATE_CHANGE;
        Native.RegisterPowerSettingNotification(Handle, ref g1, 0);
        Native.RegisterPowerSettingNotification(Handle, ref g2, 0);

        SetState(lidOn, idleOn, false);
        tray.Visible = true;
        if (pendingNote != null) { tray.ShowBalloonTip(5000, "Don't Stop!", pendingNote, ToolTipIcon.Info); pendingNote = null; }
    }

    // Plain Win32 popup menu (native Windows 11 light menu).
    void ShowMenu()
    {
        IntPtr m = Native.CreatePopupMenu();
        Native.AppendMenu(m, lidOn ? Native.MF_CHECKED : 0, (IntPtr)1, "Ignore lid close");
        Native.AppendMenu(m, idleOn ? Native.MF_CHECKED : 0, (IntPtr)2, "Block idle sleep");
        Native.AppendMenu(m, Native.MF_SEPARATOR, IntPtr.Zero, null);
        Native.AppendMenu(m, 0, (IntPtr)3, "Exit");
        Point pt = Cursor.Position;
        Native.SetForegroundWindow(Handle); // required so the menu closes when you click elsewhere
        int cmd = Native.TrackPopupMenuEx(m, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, pt.X, pt.Y, Handle, IntPtr.Zero);
        Native.PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
        Native.DestroyMenu(m);
        if (cmd == 1) SetState(!lidOn, idleOn, true);
        else if (cmd == 2) SetState(lidOn, !idleOn, true);
        else if (cmd == 3) Quit();
    }

    int GetInt(string name) { object v = reg.GetValue(name); return v is int ? (int)v : 0; }

    void SetState(bool lid, bool idle, bool remember)
    {
        if (lid && GetInt("Applied") == 0)
        {
            try { ApplyLid(Native.ActiveScheme()); }
            catch (Exception ex)
            {
                lid = false;
                tray.ShowBalloonTip(5000, "Don't Stop!", "Couldn't change the lid setting: " + ex.Message, ToolTipIcon.Error);
            }
        }
        else if (!lid) RestoreLid();

        Native.SetThreadExecutionState(idle ? Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED : Native.ES_CONTINUOUS);

        lidOn = lid; idleOn = idle;
        if (remember) { reg.SetValue("LidOn", lid ? 1 : 0); reg.SetValue("IdleOn", idle ? 1 : 0); }

        tray.Text = lid && idle ? "Lid Ignored & Idle Blocked" : lid ? "Lid Ignored" : idle ? "Idle Blocked" : "Off";
        SetIcon(lid || idle);
        strikes = 0;
        heatTimer.Enabled = lid;
    }

    // Save the original values first, then set "Do nothing" on AC and battery.
    void ApplyLid(Guid scheme)
    {
        uint ac, dc;
        Check(Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Native.SUB_BUTTONS, ref Native.LIDACTION, out ac), "read AC");
        Check(Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Native.SUB_BUTTONS, ref Native.LIDACTION, out dc), "read DC");
        reg.SetValue("Scheme", scheme.ToString());
        reg.SetValue("OrigAC", (int)ac);
        reg.SetValue("OrigDC", (int)dc);
        reg.SetValue("Applied", 1);
        Check(Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Native.SUB_BUTTONS, ref Native.LIDACTION, 0), "write AC");
        Check(Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Native.SUB_BUTTONS, ref Native.LIDACTION, 0), "write DC");
        Native.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
    }

    void RestoreLid()
    {
        if (GetInt("Applied") == 0) return;
        try
        {
            Guid scheme = new Guid((string)reg.GetValue("Scheme"));
            Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Native.SUB_BUTTONS, ref Native.LIDACTION, (uint)GetInt("OrigAC"));
            Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Native.SUB_BUTTONS, ref Native.LIDACTION, (uint)GetInt("OrigDC"));
            if (scheme == Native.ActiveScheme()) Native.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }
        catch { }
        reg.SetValue("Applied", 0);
    }

    static void Check(uint rc, string what) { if (rc != 0) throw new Exception(what + " failed (error " + rc + ")"); }

    void CheckHeat()
    {
        if (!lidOn || !lidClosed) { strikes = 0; return; }
        float board = ReadZone(ref boardCtr, Cfg.BoardZone);
        float chassis = ReadZone(ref chassisCtr, Cfg.ChassisZone);
        string why = null;
        if (board >= Cfg.BoardLimit) why = string.Format("{0:0} °C (board)", board);
        else if (chassis >= Cfg.ChassisLimit) why = string.Format("{0:0} °C (chassis)", chassis);
        if (why == null) { strikes = 0; return; }
        if (++strikes < Cfg.StrikesNeeded) return;

        SetState(false, false, true);
        bool hib = Native.IsPwrHibernateAllowed();
        pendingNote = (hib ? "Hibernated at " : "Shut down at ") + why;
        reg.SetValue("LastHeatEvent", DateTime.Now.ToString("s") + " " + pendingNote);
        Process.Start(new ProcessStartInfo("shutdown.exe", hib ? "/h" : "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
    }

    static float ReadZone(ref PerformanceCounter ctr, string zone)
    {
        try
        {
            if (ctr == null) ctr = new PerformanceCounter("Thermal Zone Information", "Temperature", zone, true);
            return ctr.NextValue() - 273.15f;
        }
        catch { return float.NaN; } // NaN never trips a limit
    }

    void SetIcon(bool on)
    {
        int size = Native.GetSystemMetrics(49); // SM_CXSMICON
        if (size <= 0) size = 16;
        using (Bitmap bmp = new Bitmap(size, size))
        using (Graphics g = Graphics.FromImage(bmp))
        using (SolidBrush brush = new SolidBrush(on ? Cfg.Pink : Cfg.Grey))
        using (FontFamily fam = new FontFamily(Cfg.Font))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string[] lines = { "Don't", "Stop!" };
            float half = size / 2f, pad = 0.5f;
            for (int i = 0; i < 2; i++)
            {
                using (GraphicsPath p = new GraphicsPath())
                using (Matrix m = new Matrix())
                {
                    p.AddString(lines[i], fam, 0, 100, PointF.Empty, StringFormat.GenericTypographic);
                    RectangleF b = p.GetBounds();
                    m.Translate(pad, i * half + pad);
                    m.Scale((size - 2 * pad) / b.Width, (half - 2 * pad) / b.Height);
                    m.Translate(-b.X, -b.Y);
                    p.Transform(m);
                    g.FillPath(brush, p);
                }
            }
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (curIcon != IntPtr.Zero) Native.DestroyIcon(curIcon);
            curIcon = h;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST)
        {
            int ev = m.WParam.ToInt32();
            if (ev == PBT_POWERSETTINGCHANGE)
            {
                Guid which = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                IntPtr data = new IntPtr(m.LParam.ToInt64() + 20);
                if (which == Native.GUID_LIDSWITCH_STATE_CHANGE)
                {
                    lidClosed = Marshal.ReadInt32(data) == 0;
                    strikes = 0;
                }
                else if (which == Native.GUID_ACTIVE_POWERSCHEME && lidOn && GetInt("Applied") == 1)
                {
                    // Plan switched: put the old plan back, apply to the new one.
                    Guid now = (Guid)Marshal.PtrToStructure(data, typeof(Guid));
                    if (now.ToString() != (string)reg.GetValue("Scheme"))
                    {
                        RestoreLid();
                        try { ApplyLid(now); } catch { SetState(false, idleOn, true); }
                    }
                }
            }
            else if ((ev == PBT_APMRESUMEAUTOMATIC || ev == PBT_APMRESUMESUSPEND) && pendingNote != null)
            {
                tray.ShowBalloonTip(8000, "Don't Stop!", pendingNote, ToolTipIcon.Warning);
                pendingNote = null;
            }
        }
        else if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero)
        {
            Cleanup(); // Windows is shutting down / logging off
        }
        else if (m.Msg == WM_CLOSE)
        {
            Quit(); // clean exit when something posts WM_CLOSE to the "DontStop" window
            return;
        }
        base.WndProc(ref m);
    }

    // Undo everything but keep the remembered toggles for next launch.
    public void Cleanup()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        RestoreLid();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        reg.SetValue("Running", 0);
        tray.Visible = false;
    }

    void Quit() { Cleanup(); Application.Exit(); }

    [STAThread]
    static void Main()
    {
        bool first;
        using (Mutex mx = new Mutex(true, @"Local\DontStop.SingleInstance", out first))
        {
            if (!first) return;
            Native.SetProcessDPIAware();
            App app = null;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate { if (app != null) app.Cleanup(); Environment.Exit(1); };
            app = new App();
            Application.Run();
            app.Cleanup();
        }
    }
}
