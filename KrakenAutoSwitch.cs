using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// Wireless headset auto audio switch (USB dongle stays plugged in).
// Detects headset power on/off through the dongle's HID interface and switches
// the Windows default output device:
//   ON  -> headset (playback + communications), previous defaults are remembered
//   OFF -> restore the remembered devices (or the configured fallback)
// Supported: Razer Kraken V4 (event driven), Logitech G325 LIGHTSPEED (polled).
static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new Mutex(true, "KrakenAutoSwitch_single", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
        }
    }
}

class Headset
{
    public string Key, Name, GameName, ChatName;
    public bool Present;      // dongle found
    public int State = -1;    // -1 unknown, 0 off, 1 on
    public DateTime OnSince;
    public ToolStripMenuItem StatusItem, SwitchItem;
}

class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "KrakenAutoSwitch";

    readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly SynchronizationContext ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    readonly Headset kraken = new Headset { Key = "kraken", Name = "Razer Kraken V4", GameName = "Razer Kraken V4 - Game", ChatName = "Razer Kraken V4 - Chat" };
    readonly Headset g325 = new Headset { Key = "g325", Name = "Logitech G325", GameName = "G325 LIGHTSPEED", ChatName = "G325 LIGHTSPEED" };
    readonly List<Headset> headsets = new List<Headset>();
    ToolStripMenuItem noneItem, autoStartItem;
    string fallbackName = "";
    string lang = "auto"; // auto | en | ko
    bool ko;
    string[] saved = new string[3]; // previous default device ids (console, multimedia, communications)

    // UI text: English by default, Korean when the OS UI language is Korean (or language=ko)
    string T(string en, string kr) { return ko ? kr : en; }

    public TrayApp()
    {
        headsets.Add(kraken); headsets.Add(g325);
        LoadConfig();
        LoadSaved();
        ko = lang == "ko" || (lang == "auto" && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko");

        var menu = new ContextMenuStrip();
        noneItem = new ToolStripMenuItem(T("No supported headset dongle found", "지원하는 헤드셋 동글이 없음")) { Enabled = false };
        menu.Items.Add(noneItem);
        foreach (var h in headsets)
        {
                        h.StatusItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
            menu.Items.Add(h.StatusItem);
        }
        menu.Items.Add(new ToolStripSeparator());
        foreach (var h in headsets)
        {
            var hh = h;
            h.SwitchItem = new ToolStripMenuItem(string.Format(T("Switch to {0} now", "{0}(으)로 지금 전환"), h.Name), null, (s, e) => SwitchToHeadset(hh)) { Visible = false };
            menu.Items.Add(h.SwitchItem);
        }
        menu.Items.Add(T("Restore previous device now", "이전 장치로 지금 복원"), null, (s, e) => Restore(true));
        menu.Items.Add(new ToolStripSeparator());
        autoStartItem = new ToolStripMenuItem(T("Start with Windows", "Windows 시작 시 자동 실행"));
        autoStartItem.Checked = IsAutoStart();
        autoStartItem.Click += (s, e) => { SetAutoStart(!IsAutoStart()); autoStartItem.Checked = IsAutoStart(); };
        menu.Items.Add(autoStartItem);
        menu.Items.Add(T("Open log", "로그 열기"), null, (s, e) => { try { System.Diagnostics.Process.Start(Path.Combine(dir, "KrakenAutoSwitch.log")); } catch { } });
        menu.Items.Add(T("Exit", "종료"), null, (s, e) => { tray.Visible = false; Application.Exit(); });
        tray.ContextMenuStrip = menu;
        UpdateTray();
        tray.Visible = true;

        new Thread(KrakenLoop) { IsBackground = true }.Start();
        new Thread(G325Loop) { IsBackground = true }.Start();
    }

    // ---------- 설정 / 상태 파일 ----------
    void LoadConfig()
    {
        string p = Path.Combine(dir, "KrakenAutoSwitch.ini");
        if (!File.Exists(p))
        {
            var sb = new StringBuilder();
            sb.Append("; Device names are substring matches (part of the name shown in Sound settings). / 장치 이름은 '포함' 일치입니다.\r\n");
            foreach (var h in headsets) sb.Append(h.Key + ".game=" + h.GameName + "\r\n" + h.Key + ".chat=" + h.ChatName + "\r\n");
            sb.Append("; Device to switch to on power-off if no previous device was remembered (empty = do nothing).\r\n; 헤드폰을 끌 때 저장된 직전 장치가 없으면 이 장치로 전환 (비우면 아무것도 안 함)\r\nfallback=\r\n; UI language: auto (follow Windows) | en | ko\r\nlanguage=auto\r\n");
            File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
        }
        foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
        {
            int i = line.IndexOf('=');
            if (line.StartsWith(";") || i < 0) continue;
            string k = line.Substring(0, i).Trim().ToLower(), v = line.Substring(i + 1).Trim();
            if (k == "game") kraken.GameName = v; else if (k == "chat") kraken.ChatName = v; // v1.x keys
            else if (k == "fallback") fallbackName = v; else if (k == "language") lang = v.ToLower();
            foreach (var h in headsets) { if (k == h.Key + ".game") h.GameName = v; else if (k == h.Key + ".chat") h.ChatName = v; }
        }
    }

    string SavedPath { get { return Path.Combine(dir, "KrakenAutoSwitch.state"); } }
    void LoadSaved() { try { var l = File.ReadAllLines(SavedPath); for (int i = 0; i < 3 && i < l.Length; i++) saved[i] = l[i].Length > 0 ? l[i] : null; } catch { } }
    void SaveSaved() { try { File.WriteAllLines(SavedPath, new[] { saved[0] ?? "", saved[1] ?? "", saved[2] ?? "" }); } catch { } }

    void Log(string s)
    {
        try
        {
            string p = Path.Combine(dir, "KrakenAutoSwitch.log");
            if (File.Exists(p) && new FileInfo(p).Length > 200000) File.Delete(p);
            File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    // ---------- 자동 실행 ----------
    static bool IsAutoStart() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
    static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\""); else k.DeleteValue(RunName, false);
        }
    }

    // ---------- 트레이 표시 ----------
    string StateText(Headset h)
    {
        return h.State == 1 ? T("on", "켜짐") : h.State == 0 ? T("off", "꺼짐") : T("unknown (until next on/off)", "알 수 없음 (다음 on/off 때 확정)");
    }

    void UpdateTray()
    {
        bool anyOn = false, anyUnknown = false, anyPresent = false;
        var tip = new List<string>();
        foreach (var h in headsets)
        {
            h.StatusItem.Visible = h.SwitchItem.Visible = h.Present;
            if (!h.Present) continue;
            anyPresent = true;
            anyOn |= h.State == 1; anyUnknown |= h.State == -1;
            h.StatusItem.Text = h.Name + ": " + StateText(h);
            tip.Add(h.Name + ": " + StateText(h));
        }
        noneItem.Visible = !anyPresent;
        Color c = anyOn ? Color.LimeGreen : anyUnknown || !anyPresent ? Color.Orange : Color.Gray;
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
                g.DrawEllipse(Pens.Black, 1, 1, 14, 14);
            }
            var old = tray.Icon;
            tray.Icon = Icon.FromHandle(bmp.GetHicon());
            if (old != null) old.Dispose();
        }
        string text = T("Headset Auto Switch", "헤드셋 자동 전환") + (tip.Count > 0 ? "\n" + string.Join("\n", tip) : "");
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; // NotifyIcon limit
    }

    // ---------- 헤드셋 감시 ----------
    void SetPresent(Headset h, bool present)
    {
        ui.Post(_ =>
        {
            if (h.Present == present) return;
            h.Present = present;
            if (!present) h.State = -1;
            Log(h.Name + " dongle " + (present ? "found" : "removed"));
            UpdateTray();
        }, null);
    }

    void PostState(Headset h, int s) { ui.Post(_ => Apply(h, s), null); }

    // Razer Kraken V4: the dongle sends an event on power change.
    //   02 0A 60 .. .. .. 05 00 80 80 20 02 01 <state>   (01 = on, 00 = off)
    void KrakenLoop()
    {
        while (true)
        {
            try
            {
                string path = Hid.FindPath(0x1532, 0x056C, 0xFF14);
                if (path != null)
                {
                    SetPresent(kraken, true);
                    Hid.ReadLoop(path, (b, n) =>
                    {
                        if (n < 14 || b[1] != 0x0A || b[10] != 0x20 || b[11] != 0x02 || b[12] != 0x01) return;
                        PostState(kraken, b[13] == 1 ? 1 : 0);
                    });
                }
                SetPresent(kraken, false);
            }
            catch (Exception ex) { Log("Kraken HID error: " + ex.Message); }
            Thread.Sleep(2000); // 동글 재연결 / 절전 복귀 대비
        }
    }

    // Logitech G325: the dongle does not announce power changes, so poll the headset
    // through the Centurion bridge (same protocol as Solaar's PRO X 2 / G522 support).
    void G325Loop()
    {
        while (true)
        {
            try
            {
                string path = Hid.FindPath(0x046D, 0x0B21, 0xFFA0);
                if (path != null)
                {
                    SetPresent(g325, true);
                    Centurion.Poll(path, s => PostState(g325, s), Log);
                }
                SetPresent(g325, false);
            }
            catch (Exception ex) { Log("G325 HID error: " + ex.Message); }
            Thread.Sleep(3000);
        }
    }

    void Apply(Headset h, int s)
    {
        if (s == h.State) return;
        h.State = s;
        if (s == 1) h.OnSince = DateTime.Now;
        Log(h.Name + " " + (s == 1 ? "ON" : "OFF"));
        UpdateTray();
        // 가장 최근에 켜진 헤드셋으로, 모두 꺼졌으면 이전 장치로
        Headset active = null;
        foreach (var x in headsets) if (x.State == 1 && (active == null || x.OnSince > active.OnSince)) active = x;
        if (active != null) SwitchToHeadset(active); else Restore(false);
    }

    // ---------- 오디오 전환 ----------
    static Audio.Dev Find(List<Audio.Dev> devs, string name)
    {
        return string.IsNullOrEmpty(name) ? null : devs.Find(d => d.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    bool IsHeadsetDevice(List<Audio.Dev> devs, string id)
    {
        if (id == null) return false;
        foreach (var h in headsets)
        {
            var g = Find(devs, h.GameName); var c = Find(devs, h.ChatName);
            if ((g != null && g.Id == id) || (c != null && c.Id == id)) return true;
        }
        return false;
    }

    void Notify(string msg) { tray.ShowBalloonTip(1500, T("Headset Auto Switch", "헤드셋 자동 전환"), msg, ToolTipIcon.None); }

    void SwitchToHeadset(Headset h)
    {
        try
        {
            var devs = Audio.ListRender();
            var game = Find(devs, h.GameName); var chat = Find(devs, h.ChatName) ?? game;
            if (game == null) { Log(h.Name + " playback device not found: " + h.GameName); return; }
            var cur = new[] { Audio.GetDefault(0), Audio.GetDefault(1), Audio.GetDefault(2) };
            if (cur[0] == game.Id && cur[2] == chat.Id) return;
            // 다른 헤드셋이 아닌 '원래 장치'일 때만 저장
            if (!IsHeadsetDevice(devs, cur[0])) { saved = cur; SaveSaved(); }
            Audio.SetDefault(game.Id, 0); Audio.SetDefault(game.Id, 1); Audio.SetDefault(chat.Id, 2);
            Log("-> " + game.Name + " / comms: " + chat.Name);
            Notify(string.Format(T("Output switched to {0}", "{0}(으)로 출력 전환"), h.Name));
        }
        catch (Exception ex) { Log("Switch error: " + ex); }
    }

    // force=false: only when a headset is currently the default (e.g. not on startup with speakers)
    void Restore(bool force)
    {
        try
        {
            var devs = Audio.ListRender();
            if (!force && !IsHeadsetDevice(devs, Audio.GetDefault(0))) return;
            var fb = Find(devs, fallbackName);
            for (int role = 0; role < 3; role++)
            {
                string id = saved[role];
                bool isHs = IsHeadsetDevice(devs, id);
                if (isHs && role == 2) { id = saved[0]; isHs = IsHeadsetDevice(devs, id); } // 꺼진 헤드폰이 통신 기본 장치로 남지 않게
                if (id == null || !devs.Exists(d => d.Id == id) || isHs) id = fb != null ? fb.Id : null;
                if (id == null) continue;
                Audio.SetDefault(id, role);
            }
            Log("Restored: " + string.Join(" | ", Array.ConvertAll(new[] { Audio.GetDefault(0), Audio.GetDefault(1), Audio.GetDefault(2) }, id => devs.Find(d => d.Id == id) != null ? devs.Find(d => d.Id == id).Name : "?")));
            Notify(T("Restored previous output device", "이전 출력 장치로 복원"));
        }
        catch (Exception ex) { Log("Switch error: " + ex); }
    }
}

// ================== Logitech Centurion (G325) ==================
// Frame (0x50 addressed variant): [0x50, device_addr, cpl_length, flags, feat_idx, func|swid, params..., pad to 64]
// cpl_length = len(feat_idx..params) + 1. See Solaar lib/logitech_receiver/base.py.
static class Centurion
{
    const byte ReportId = 0x50;

    static byte[] Frame(byte addr, params byte[] layer3)
    {
        var f = new byte[64];
        f[0] = ReportId; f[1] = addr; f[2] = (byte)(layer3.Length + 1);
        Array.Copy(layer3, 0, f, 4, layer3.Length);
        return f;
    }

    static byte[] Wait(BlockingCollection<byte[]> rx, int ms, Func<byte[], bool> match)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        byte[] f;
        while (true)
        {
            int left = (int)(until - DateTime.Now).TotalMilliseconds;
            if (left <= 0 || !rx.TryTake(out f, left)) return null;
            if (match(f)) return f;
        }
    }

    static void Drain(BlockingCollection<byte[]> rx) { byte[] x; while (rx.TryTake(out x)) { } }

    // Polls the headset every ~2 s; reports 1 (answered) / 0 (two missed polls). Returns when the dongle goes away.
    public static void Poll(string path, Action<int> onState, Action<string> log)
    {
        using (var rh = Hid.Open(path))
        using (var wh = Hid.Open(path))
        {
            if (rh.IsInvalid || wh.IsInvalid) return;
            var rx = new BlockingCollection<byte[]>();
            bool alive = true;
            var reader = new Thread(() =>
            {
                var b = new byte[64]; int n;
                while (Hid.Read(rh, b, out n)) rx.Add((byte[])b.Clone());
                alive = false;
            }) { IsBackground = true };
            reader.Start();

            // 1) device_addr: the dongle answers ROOT.GetProtocolVersion only on the right address (G325: 0x23)
            int addr = -1;
            for (int i = 0; i < 256 && addr < 0 && alive; i++)
            {
                byte a = (byte)((0x23 + i) & 0xFF);
                if (!Hid.Write(wh, Frame(a, 0x00, 0x1A, 0x00, 0x00, 0x00))) return;
                var r = Wait(rx, 20, f => f[0] == ReportId && f[4] == 0x00 && f[5] == 0x1A);
                if (r != null) addr = r[1];
            }
            if (addr < 0) { log("G325: no Centurion device address"); Thread.Sleep(10000); return; }

            // 2) ROOT.GetFeature(0x0003 CentPPBridge) on the dongle
            Drain(rx);
            Hid.Write(wh, Frame((byte)addr, 0x00, 0x0A, 0x00, 0x03));
            var fr = Wait(rx, 1000, f => f[0] == ReportId && f[4] == 0x00 && f[5] == 0x0A);
            if (fr == null || fr[6] == 0) { log("G325: bridge feature not found"); Thread.Sleep(10000); return; }
            byte bridge = fr[6];
            log(string.Format("G325: centurion addr 0x{0:X2}, bridge {1}", addr, bridge));

            // 3) bridge sendFragment -> headset ROOT.GetFeature(0x0001); a MessageEvent reply means the headset is on
            int misses = 0;
            while (alive)
            {
                Drain(rx);
                if (!Hid.Write(wh, Frame((byte)addr, bridge, 0x1B, 0x00, 0x05, 0x00, 0x00, 0x0B, 0x00, 0x01))) return;
                var ev = Wait(rx, 800, f => f[0] == ReportId && f[4] == bridge && f[5] == 0x10 && f[8] == 0x00 && f[9] == 0x00);
                if (ev != null) { misses = 0; onState(1); }
                else if (++misses >= 2) onState(0);
                Thread.Sleep(1500);
            }
        }
    }
}

// ================== HID ==================
static class Hid
{
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS { public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved; public ushort a, b, c, d, e, f, g, h, i, j; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS caps);
    [DllImport("setupapi.dll")] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);

    public static bool Write(SafeFileHandle h, byte[] report) { int w; return WriteFile(h, report, report.Length, out w, IntPtr.Zero); }
    public static bool Read(SafeFileHandle h, byte[] buf, out int n) { return ReadFile(h, buf, buf.Length, out n, IntPtr.Zero); }

    public static SafeFileHandle Open(string path)
    {
        var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return h;
    }

    // vid/pid 가 일치하고 최상위 Usage Page 가 usagePage 인 HID 경로
    public static string FindPath(int vid, int pid, int usagePage)
    {
        string tag = string.Format("vid_{0:x4}&pid_{1:x4}", vid, pid);
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                    string p = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                    if (!p.ToLower().Contains(tag)) continue;
                    using (var h = Open(p))
                    {
                        if (h.IsInvalid) continue;
                        IntPtr pp; HIDP_CAPS caps;
                        if (!HidD_GetPreparsedData(h, out pp)) continue;
                        HidP_GetCaps(pp, out caps); HidD_FreePreparsedData(pp);
                        if (caps.UsagePage == usagePage && caps.InputReportByteLength > 0) return p;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    public static void ReadLoop(string path, Action<byte[], int> onReport)
    {
        using (var h = Open(path))
        {
            if (h.IsInvalid) return;
            IntPtr pp; HIDP_CAPS caps;
            HidD_GetPreparsedData(h, out pp); HidP_GetCaps(pp, out caps); HidD_FreePreparsedData(pp);
            var b = new byte[caps.InputReportByteLength];
            int n;
            while (ReadFile(h, b, b.Length, out n, IntPtr.Zero)) onReport(b, n);
        }
    }
}

// ================== 오디오 (Core Audio / IPolicyConfig) ==================
static class Audio
{
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice dev);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice dev);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr c);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr c);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IMMDevice d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore ps);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int s);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int c);
        [PreserveSig] int GetAt(int i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr n, IntPtr f);
        [PreserveSig] int GetDeviceFormat(IntPtr n, int d, IntPtr f);
        [PreserveSig] int SetDeviceFormat(IntPtr n, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod(IntPtr n, int d, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod(IntPtr n, IntPtr p);
        [PreserveSig] int GetShareMode(IntPtr n, IntPtr m);
        [PreserveSig] int SetShareMode(IntPtr n, IntPtr m);
        [PreserveSig] int GetPropertyValue(IntPtr n, int b, IntPtr k, IntPtr v);
        [PreserveSig] int SetPropertyValue(IntPtr n, int b, IntPtr k, IntPtr v);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr n, int v);
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    public class Dev { public string Id, Name; }

    static IMMDeviceEnumerator Enumerator()
    {
        return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
    }

    public static List<Dev> ListRender()
    {
        var res = new List<Dev>();
        IMMDeviceCollection col;
        Marshal.ThrowExceptionForHR(Enumerator().EnumAudioEndpoints(0, 1 /*ACTIVE*/, out col));
        int n; col.GetCount(out n);
        var key = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
        for (int i = 0; i < n; i++)
        {
            IMMDevice d; col.Item(i, out d);
            string id; d.GetId(out id);
            IPropertyStore ps; d.OpenPropertyStore(0, out ps);
            PROPVARIANT v; ps.GetValue(ref key, out v);
            string name = Marshal.PtrToStringUni(v.p); PropVariantClear(ref v);
            res.Add(new Dev { Id = id, Name = name ?? "" });
        }
        return res;
    }

    // role: 0 console, 1 multimedia, 2 communications
    public static string GetDefault(int role)
    {
        IMMDevice d;
        if (Enumerator().GetDefaultAudioEndpoint(0, role, out d) != 0) return null;
        string id; d.GetId(out id); return id;
    }

    public static void SetDefault(string id, int role)
    {
        var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("294935CE-F637-4E7C-A41B-AB255460B862")));
        Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, role));
    }
}
