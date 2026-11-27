using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NaiwaPet
{
    /// <summary>
    /// 贴在鼠标指针上的奶蛙。分层窗口 + 逐像素透明 + 鼠标穿透。
    /// 指针精确落在它两只手交叠处，位置不做任何平滑——做成硬跟随，
    /// 否则甩动鼠标时会有"甩过头再荡回来"的橡皮筋感。
    ///
    /// 四种状态，全部来自素材包里的原始帧：
    ///   Idle     站姿 + 随机眨眼
    ///   Laugh    双击后播 422 帧大笑动画（30fps）
    ///   LaughEnd 笑倒躺平，停 2 秒自己爬起来
    ///   Sleep    鼠标 30 秒不动就躺下
    /// </summary>
    /// <summary>托盘设置持久化到 %APPDATA%\NaiwaPet\settings.json。</summary>
    internal sealed class Settings
    {
        public bool AutoStart { get; set; } = true;
        public int SizePreset { get; set; } = 1;
        public bool ClickToLaugh { get; set; } = true;
        public bool TopMost { get; set; } = true;

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "NaiwaPet", "settings.json");
            }
        }

        public static Settings Load()
        {
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null) { s.SizePreset = Math.Clamp(s.SizePreset, 0, 2); return s; }
            }
            catch { }
            var fresh = new Settings();
            fresh.Save();          // 首次运行把默认值落盘，方便查看/修改
            return fresh;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    internal sealed class PetForm : Form
    {
        // 源图 idle.png 是 288x368；指针放在头与肚子之间的缺口 (55,73)，
        // 落在空白处，不压脸也不压手
        const float AnchorX = 55f, AnchorY = 73f;          // 站姿/笑/躺平 用
        const float HandAnchorX = 99f, HandAnchorY = 73f;  // 单手姿势（画布左侧多留 44px 放手臂）
        const int SrcH = 368;
        const int LaughFrames = 422;
        const double LaughFps = 30.0;

        const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
                  WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080,
                  WS_EX_TOPMOST = 0x00000008;
        const uint ULW_ALPHA = 0x02;
        const byte AC_SRC_ALPHA = 0x01;
        const int VK_LBUTTON = 0x01;
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "NaiwaPet";

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }
        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        enum PetState { Idle, Hover, Laugh, LaughEnd, Sleep }

        sealed class Frame : IDisposable
        {
            public IntPtr HBitmap;
            public Size Size;
            public Point Anchor;
            public void Dispose()
            {
                if (HBitmap != IntPtr.Zero) DeleteObject(HBitmap);
                HBitmap = IntPtr.Zero;
            }
        }

        static readonly int[] SizePresets = { 92, 126, 168 };   // 96 DPI 下的画布高
        const float MaxCanvasHeight = 280f;                     // 高分屏上限，别糊满屏幕
        int _preset = 1;
        float _dpi = 96f;
        float PetScale
        {
            get
            {
                float h = SizePresets[_preset] * _dpi / 96f;   // 按当前显示器 DPI 放大
                if (h > MaxCanvasHeight) h = MaxCanvasHeight;
                return h / SrcH;
            }
        }

        readonly Timer _tick = new Timer { Interval = 15 };
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Random _rnd = new Random();

        IntPtr _memDC;
        readonly Dictionary<string, List<Frame>> _static = new Dictionary<string, List<Frame>>();
        readonly Dictionary<int, Frame> _laughCache = new Dictionary<int, Frame>();

        PetState _state = PetState.Idle;
        double _stateAt, _lastMoveAt, _laughAt, _blinkAt = -1, _nextBlinkAt = 2.5;
        Point _lastCursor;
        int _btnDown;
        double _lastPressAt = -99;
        double _ignoreClicksUntil;
        Point _anchorScaled, _handAnchorScaled;
        IntPtr _handCursor;
        bool _tickedLogged;

        NotifyIcon _tray;
        bool _clickToLaugh = true;
        Settings _settings;

        public PetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-4000, -4000, 10, 10);
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
                            | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* 内容交给 UpdateLayeredWindow */ }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Log("OnLoad start");
            try { LoadCore(); Log("LoadCore done"); }
            catch (Exception ex) { Log("LOAD ERROR: " + ex); }
        }

        /// <summary>写入/删除 HKCU\...\Run。路径用当前 exe，所以整个文件夹挪走也没问题。</summary>
        static void ApplyAutoStart(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (on)
                        key.SetValue(RunValueName, "\"" + Environment.ProcessPath + "\"", RegistryValueKind.String);
                    else
                        key.DeleteValue(RunValueName, false);
                }
            }
            catch { }   // 公司策略可能禁止写启动项，不让程序因此崩掉
        }

        void LoadCore()
        {
            _settings = Settings.Load();
            _preset = _settings.SizePreset;
            _clickToLaugh = _settings.ClickToLaugh;
            TopMost = _settings.TopMost;
            ApplyAutoStart(_settings.AutoStart);
            _memDC = CreateCompatibleDC(GetDC(IntPtr.Zero));
            _handCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32649);   // IDC_HAND
            POINT p0;
            if (GetCursorPos(out p0)) _lastCursor = new Point(p0.X, p0.Y);
            _lastMoveAt = _clock.Elapsed.TotalSeconds;
            _stateAt = _lastMoveAt;
            Rebuild();
            SetupTray();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--laugh") >= 0)
                EnterState(PetState.Laugh, _clock.Elapsed.TotalSeconds);
            _tick.Tick += OnTick;
            _tick.Start();
        }

        static readonly bool LogEnabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--log") >= 0;
        static void Log(string msg)
        {
            if (!LogEnabled) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "naiwa_pet.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n");
            }
            catch { }
        }

        // ---------------- 帧 ----------------
        /// <summary>素材全部编译进 exe，运行时从嵌入资源读，拷走一个文件就能用。</summary>
        static class Assets
        {
            static readonly Dictionary<string, string> Map = Build();

            static Dictionary<string, string> Build()
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in typeof(Assets).Assembly.GetManifestResourceNames())
                {
                    var key = name.StartsWith("NaiwaPet.") ? name.Substring("NaiwaPet.".Length) : name;
                    map[key] = name;
                }
                return map;
            }

            public static Stream Open(string relative)
            {
                var key = "assets." + relative.Replace('\\', '/').Replace('/', '.');
                string name;
                if (!Map.TryGetValue(key, out name))
                    throw new FileNotFoundException("缺少素材: " + relative + " (key=" + key + ")");
                return typeof(Assets).Assembly.GetManifestResourceStream(name);
            }
        }

        static Bitmap LoadPng(string relative)
        {
            using (var s = Assets.Open(relative))
            using (var tmp = new Bitmap(s))
                return new Bitmap(tmp);
        }

        Frame BuildFrame(Bitmap src, Point? anchor = null)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * PetScale));
            int h = Math.Max(1, (int)Math.Round(src.Height * PetScale));
            using (var scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                return new Frame
                {
                    HBitmap = ToHBitmap(scaled),
                    Size = scaled.Size,
                    Anchor = anchor ?? _anchorScaled
                };
            }
        }

        static IntPtr ToHBitmap(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = 40;
                bmi.bmiHeader.biWidth = bmp.Width;
                bmi.bmiHeader.biHeight = -bmp.Height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                IntPtr bits;
                IntPtr hbmp = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hbmp == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                int rowBytes = bmp.Width * 4;
                var row = new byte[rowBytes];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                    Marshal.Copy(row, 0, bits + y * rowBytes, rowBytes);
                }
                return hbmp;
            }
            finally { bmp.UnlockBits(data); }
        }

        void Rebuild()
        {
            foreach (var list in _static.Values) foreach (var f in list) f.Dispose();
            _static.Clear();
            foreach (var f in _laughCache.Values) f.Dispose();
            _laughCache.Clear();

            _anchorScaled = new Point((int)Math.Round(AnchorX * PetScale), (int)Math.Round(AnchorY * PetScale));

            _static["idle"] = new List<Frame> { BuildFrame(LoadPng("pet.png")) };
            var blink = new List<Frame>();
            for (int i = 0; i < 7; i++)
            {
                var bmp = LoadPng("blink" + i + ".png");
                blink.Add(BuildFrame(bmp));
                bmp.Dispose();
            }
            _static["blink"] = blink;

            _handAnchorScaled = new Point((int)Math.Round(HandAnchorX * PetScale),
                                          (int)Math.Round(HandAnchorY * PetScale));
            _static["hand"] = new List<Frame> { BuildFrame(LoadPng("pet_hand.png"), _handAnchorScaled) };

            var fallen = LoadPng(Path.Combine("laugh", "0421.png"));
            _static["fallen"] = new List<Frame> { BuildFrame(fallen) };
            fallen.Dispose();
        }

        Frame GetLaugh(int index)
        {
            index = Math.Max(0, Math.Min(LaughFrames - 1, index));
            Frame f;
            if (_laughCache.TryGetValue(index, out f)) return f;
            var bmp = LoadPng(Path.Combine("laugh", index.ToString("0000") + ".png"));
            f = BuildFrame(bmp);
            bmp.Dispose();
            _laughCache[index] = f;
            return f;
        }

        // ---------------- 主循环 ----------------
        int _tickErrors;
        void OnTick(object sender, EventArgs e)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                if (_tickErrors++ < 3) Log("TICK ERROR: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        void Tick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            POINT c;
            if (!GetCursorPos(out c)) return;
            var cur = new Point(c.X, c.Y);
            int moved = Math.Abs(cur.X - _lastCursor.X) + Math.Abs(cur.Y - _lastCursor.Y);
            _lastCursor = cur;

            PollClicks(now);

            if (moved > 0) _lastMoveAt = now;

            // 换到不同 DPI 的显示器就按新比例重建一次（换尺寸只要几毫秒）
            float dpi = DpiAt(cur);
            if (Math.Abs(dpi - _dpi) > 1f) { _dpi = dpi; Rebuild(); }

            // ---- 状态迁移 ----
            if (_state == PetState.Sleep && moved > 2) _state = PetState.Idle;

            // 指针变成手型（可点击）时换成单手伸出的姿势
            if (_state == PetState.Idle || _state == PetState.Hover)
            {
                bool wantHover = IsHandCursor();
                if (wantHover != (_state == PetState.Hover))
                    EnterState(wantHover ? PetState.Hover : PetState.Idle, now);
            }
            if (_state == PetState.Idle && now - _lastMoveAt > 30) EnterState(PetState.Sleep, now);
            if (_state == PetState.Laugh)
            {
                if (moved > 60) EnterState(PetState.Idle, now);                       // 甩一下鼠标就打断
                else if ((now - _laughAt) * LaughFps >= LaughFrames) EnterState(PetState.LaughEnd, now);
            }
            if (_state == PetState.LaughEnd && now - _stateAt > 2.0) EnterState(PetState.Idle, now);

            // ---- 眨眼（不笑/不睡时）----
            bool canBlink = _state == PetState.Idle;
            if (_blinkAt >= 0)
            {
                if (now - _blinkAt > 0.30) { _blinkAt = -1; _nextBlinkAt = now + 2.5 + _rnd.NextDouble() * 4.0; }
            }
            else if (canBlink && now > _nextBlinkAt && now - _lastMoveAt > 1.6)
            {
                _blinkAt = now;
            }

            // ---- 选帧 ----
            Frame frame;
            switch (_state)
            {
                case PetState.Laugh:
                    frame = GetLaugh((int)((now - _laughAt) * LaughFps));
                    break;
                case PetState.Hover:
                    frame = _static["hand"][0];
                    break;
                case PetState.LaughEnd:
                case PetState.Sleep:
                    frame = _static["fallen"][0];
                    break;
                default:
                    if (_blinkAt >= 0)
                    {
                        int i = (int)((now - _blinkAt) / 0.045);
                        var seq = _static["blink"];
                        frame = seq[Math.Min(i, seq.Count - 1)];
                    }
                    else frame = _static["idle"][0];
                    break;
            }

            Render(frame, cur.X - frame.Anchor.X, cur.Y - frame.Anchor.Y);
            if (!_tickedLogged) { _tickedLogged = true; Log($"first frame rendered at {cur.X},{cur.Y} state={_state} dpi={_dpi}"); }
        }

        /// <summary>光标所在显示器的有效 DPI（96/120/144/192…）。取不到就按 96 算。</summary>
        float DpiAt(Point p)
        {
            try
            {
                var mon = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2 /*NEAREST*/);
                if (mon == IntPtr.Zero) return 96f;
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0 /*EFFECTIVE*/, out dx, out dy) == 0 && dx > 0) return dx;
            }
            catch { }
            return 96f;
        }

        bool IsHandCursor()
        {
            if (_handCursor == IntPtr.Zero) return false;
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(ref ci)) return false;
            return ci.hCursor == _handCursor;
        }

        void EnterState(PetState s, double now)
        {
            _state = s;
            _stateAt = now;
            if (s == PetState.Laugh) _laughAt = now;
            if (s != PetState.Idle) _blinkAt = -1;
        }

        void PollClicks(double now)
        {
            if (!_clickToLaugh) { _btnDown = 0; return; }
            bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (down && _btnDown == 0 && now > _ignoreClicksUntil)
            {
                if (now - _lastPressAt < 0.38)
                {
                    _lastPressAt = -99;
                    if (_state != PetState.LaughEnd) EnterState(PetState.Laugh, now);
                }
                else _lastPressAt = now;
            }
            _btnDown = down ? 1 : 0;
        }

        void Render(Frame frame, int x, int y)
        {
            if (frame == null || frame.HBitmap == IntPtr.Zero) return;
            IntPtr old = SelectObject(_memDC, frame.HBitmap);
            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { cx = frame.Size.Width, cy = frame.Size.Height };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDC, ref src, 0, ref blend, ULW_ALPHA);
            SelectObject(_memDC, old);
        }

        // ---------------- 托盘 ----------------
        void SetupTray()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) => _ignoreClicksUntil = _clock.Elapsed.TotalSeconds + 1.0;
            menu.Closed += (s, e) => _ignoreClicksUntil = _clock.Elapsed.TotalSeconds + 0.4;

            var sizeMenu = new ToolStripMenuItem("大小");
            string[] names = { "小", "中", "大" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var item = new ToolStripMenuItem(names[i]) { Checked = idx == _settings.SizePreset };
                item.Click += (s, e) =>
                {
                    _preset = idx;
                    foreach (ToolStripMenuItem mi in sizeMenu.DropDownItems) mi.Checked = false;
                    item.Checked = true;
                    _settings.SizePreset = idx;
                    _settings.Save();
                    Rebuild();
                };
                sizeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(sizeMenu);

            var laughItem = new ToolStripMenuItem("双击时大笑") { Checked = _settings.ClickToLaugh, CheckOnClick = true };
            laughItem.CheckedChanged += (s, e) =>
            {
                _clickToLaugh = laughItem.Checked;
                _settings.ClickToLaugh = laughItem.Checked;
                _settings.Save();
            };
            menu.Items.Add(laughItem);

            var top = new ToolStripMenuItem("置顶") { Checked = _settings.TopMost, CheckOnClick = true };
            top.CheckedChanged += (s, e) =>
            {
                TopMost = top.Checked;
                _settings.TopMost = top.Checked;
                _settings.Save();
            };
            menu.Items.Add(top);

            var auto = new ToolStripMenuItem("开机启动") { Checked = _settings.AutoStart, CheckOnClick = true };
            auto.CheckedChanged += (s, e) =>
            {
                _settings.AutoStart = auto.Checked;
                ApplyAutoStart(auto.Checked);
                _settings.Save();
            };
            menu.Items.Add(auto);

            menu.Items.Add(new ToolStripSeparator());
            var quit = new ToolStripMenuItem("退出");
            quit.Click += (s, e) => Close();
            menu.Items.Add(quit);

            IntPtr hIcon;
            using (var src = LoadPng("pet.png"))
            using (var small = new Bitmap(src, new Size(32, 32)))
                hIcon = small.GetHicon();

            _tray = new NotifyIcon
            {
                Icon = Icon.FromHandle(hIcon),
                Text = "奶蛙 · 抱着鼠标",
                Visible = true,
                ContextMenuStrip = menu
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            foreach (var list in _static.Values) foreach (var f in list) f.Dispose();
            foreach (var f in _laughCache.Values) f.Dispose();
            if (_memDC != IntPtr.Zero) DeleteDC(_memDC);
            base.OnFormClosed(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "NaiwaPet.SingleInstance", out created))
            {
                if (!created) return;
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PetForm());
            }
        }
    }
}
