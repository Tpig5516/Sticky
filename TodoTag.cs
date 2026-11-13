// ============================================================================
// 狗皮膏药（TodoTag）—— 轻量级 Windows 桌面待办清单
//
// 功能：单窗口待办清单；勾选完成（文字淡化 + 删除线，可再次点击取消）；
//       按下拖动调整顺序；悬停删除；清除已完成；置顶/最小化/关闭；
//       开机自启动开关；拖动窗口边缘/角落可缩放窗口；
//       自动保存内容、顺序、窗口位置/尺寸和置顶状态。
//
// 技术：.NET Framework 4.x + WinForms，单文件、无第三方依赖；
//       无边框窗口，界面全部自绘（GDI+），并按屏幕 DPI 整体缩放。
//
// 数据：%APPDATA%\TodoTag\data.txt，具体格式见 Store 类的注释。
//
// 结构：
//   Program     程序入口（DPI 感知、滚轮消息过滤器）
//   Ui          DPI 缩放换算
//   C / Gfx     配色与绘图工具
//   Todo        待办数据模型
//   Store       本地持久化
//   Native      Win32 API 封装（圆角、输入框占位提示）
//   WheelFilter 滚轮消息路由
//   MiniButton  标题栏自绘小按钮
//   InputHost   输入框圆角底板
//   TodoList    自绘待办列表（勾选/删除/拖动排序/滚动）
//   MainForm    无边框主窗口（标题栏、统计、输入框、窗口拖动）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TodoTag
{
    /// <summary>程序入口：声明 DPI 感知、计算全局缩放，然后启动主窗口。</summary>
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            // 必须在创建任何窗口前开启 DPI 感知，否则高分屏下整个窗口会被系统位图拉伸而变糊。
            try { SetProcessDPIAware(); } catch { }

            // 以屏幕 DPI 计算全局缩放（96 DPI = 1.0），界面尺寸统一按 DIP 设计、绘制时换算成像素。
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    Ui.Scale = g.DpiX / 96f;
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();
            // 滚轮消息默认发给焦点控件（可能是输入框），用全局过滤器转发给光标下的列表。
            Application.AddMessageFilter(new WheelFilter(form));
            Application.Run(form);
        }
    }

    /// <summary>
    /// 全局 DPI 缩放。界面尺寸按 96 DPI 下的 DIP 设计，
    /// 布局/绘制时通过 Px（整数像素）或 Pxf（浮点像素）换算，保证高分屏下清晰且比例一致。
    /// </summary>
    static class Ui
    {
        public static float Scale = 1f;
        public static int Px(float v) { return (int)Math.Round(v * Scale); }
        public static float Pxf(float v) { return v * Scale; }
    }

    /// <summary>界面配色（浅色主题）。</summary>
    static class C
    {
        public static readonly Color Bg = Color.FromArgb(251, 251, 252);        // 窗口背景
        public static readonly Color Surface = Color.FromArgb(243, 243, 246);   // 输入框底色
        public static readonly Color Ink = Color.FromArgb(44, 44, 50);          // 主文字
        public static readonly Color Muted = Color.FromArgb(146, 146, 156);     // 次要文字
        public static readonly Color Faint = Color.FromArgb(190, 190, 199);     // 空列表提示
        public static readonly Color Accent = Color.FromArgb(76, 134, 249);     // 强调色（勾选/置顶激活/链接悬停）
        public static readonly Color Hover = Color.FromArgb(242, 242, 246);     // 标题栏按钮悬停底色
        public static readonly Color ItemHover = Color.FromArgb(242, 242, 246); // 列表行悬停底色
        public static readonly Color DoneText = Color.FromArgb(178, 178, 187);  // 已完成文字（淡出目标色）
        public static readonly Color BoxBorder = Color.FromArgb(205, 205, 213); // 未勾选方框描边
        public static readonly Color Line = Color.FromArgb(232, 232, 236);      // 输入框细边框
        public static readonly Color Danger = Color.FromArgb(214, 86, 86);      // 删除/关闭按钮悬停
    }

    /// <summary>一条待办。</summary>
    class Todo
    {
        /// <summary>待办内容（单行，最长 160 字符）</summary>
        public string Text;
        /// <summary>是否已完成（数据层面的状态，切换后立即持久化）</summary>
        public bool Done;
        /// <summary>完成动画进度：0=未完成外观，1=已完成外观；用于颜色和删除线平滑过渡</summary>
        public float Anim;
        /// <summary>删除线宽度缓存（避免每次重绘都调用 MeasureText）</summary>
        public int StrikeWidth;
        /// <summary>该缓存对应的可用文本宽度；窗口缩放导致宽度变化时重新测量</summary>
        public int StrikeWidthFor = -1;
    }

    /// <summary>
    /// 本地持久化。
    /// 文件：%APPDATA%\TodoTag\data.txt（UTF-8），每行一条记录：
    ///   pos=x,y        窗口左上角坐标
    ///   size=w,h       窗口尺寸（旧文件可能没有该行，此时使用默认尺寸）
    ///   pin=0|1        是否置顶
    ///   0|1[TAB]Base64 每条待办：完成状态 + Base64(UTF-8 文本)，按列表当前顺序排列
    /// 文本使用 Base64 编码，避免内容中的换行、制表符等破坏行格式；
    /// 读取时逐行容错，遇到无法解析的行直接跳过，不影响其余数据。
    /// </summary>
    static class Store
    {
        static string DirPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoTag"); }
        }

        static string DataFile { get { return Path.Combine(DirPath, "data.txt"); } }

        /// <summary>读取数据文件；文件不存在或损坏时返回空列表（程序会自动重建）。</summary>
        public static void Load(out List<Todo> items, out Point pos, out bool hasPos, out bool pinned,
                                out Size size, out bool hasSize)
        {
            items = new List<Todo>();
            pos = Point.Empty;
            hasPos = false;
            pinned = false;
            size = Size.Empty;
            hasSize = false;

            try
            {
                if (!File.Exists(DataFile)) return;
                string[] lines = File.ReadAllLines(DataFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) continue;

                    // 窗口位置
                    if (line.StartsWith("pos="))
                    {
                        string[] xy = line.Substring(4).Split(',');
                        int x, y;
                        if (xy.Length == 2 && int.TryParse(xy[0], out x) && int.TryParse(xy[1], out y))
                        {
                            pos = new Point(x, y);
                            hasPos = true;
                        }
                        continue;
                    }
                    // 窗口尺寸
                    if (line.StartsWith("size="))
                    {
                        string[] wh = line.Substring(5).Split(',');
                        int w, h;
                        if (wh.Length == 2 && int.TryParse(wh[0], out w) && int.TryParse(wh[1], out h))
                        {
                            size = new Size(w, h);
                            hasSize = true;
                        }
                        continue;
                    }
                    // 置顶状态
                    if (line.StartsWith("pin="))
                    {
                        pinned = line.Substring(4) == "1";
                        continue;
                    }
                    // 待办行：格式为 “0/1 + TAB + Base64”
                    if (line.Length < 3 || line[1] != '\t') continue;
                    if (line[0] != '0' && line[0] != '1') continue;

                    try
                    {
                        Todo t = new Todo();
                        t.Text = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(2)));
                        t.Done = line[0] == '1';
                        t.Anim = t.Done ? 1f : 0f;  // 启动时已完成项直接呈现完成外观，不做动画
                        items.Add(t);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>保存全部数据；写入失败静默忽略（不打断用户操作）。</summary>
        public static void Save(List<Todo> items, Point pos, Size size, bool pinned)
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("pos=" + pos.X + "," + pos.Y);
                sb.AppendLine("size=" + size.Width + "," + size.Height);
                sb.AppendLine("pin=" + (pinned ? "1" : "0"));
                for (int i = 0; i < items.Count; i++)
                {
                    Todo t = items[i];
                    sb.Append(t.Done ? "1\t" : "0\t");
                    sb.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(t.Text)));
                }
                File.WriteAllText(DataFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>
    /// 开机自启动：写入当前用户的注册表 Run 项，登录 Windows 时自动运行本程序。
    /// 位置：HKCU\Software\Microsoft\Windows\CurrentVersion\Run，值名“狗皮膏药”，
    /// 值为带引号的 exe 完整路径（避免路径含空格时解析错误）。
    /// 只写 HKCU，不需要管理员权限；关闭时删除该值，可随时撤销。
    /// </summary>
    static class Startup
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "狗皮膏药";

        /// <summary>Run 项的值是命令行，路径含空格时必须加引号。</summary>
        static string Command(string exePath)
        {
            return "\"" + exePath + "\"";
        }

        /// <summary>
        /// 当前 exe 是否已登记开机启动。
        /// 只有登记的命令行与当前 exe 路径完全一致才算启用；
        /// 如果程序被移动/改名，旧路径会被视为未启用，再次点击即可覆盖为新路径。
        /// </summary>
        public static bool IsEnabled(string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(ValueName) as string;
                    if (value == null) return false;
                    return string.Equals(value.Trim(), Command(exePath), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        /// <summary>开启/关闭开机自启动；返回操作后实际是否处于启用状态（写入失败时保持原状）。</summary>
        public static bool SetEnabled(string exePath, bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    if (enable) key.SetValue(ValueName, Command(exePath));
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch { }
            return IsEnabled(exePath);
        }
    }

    /// <summary>绘图工具：圆角路径、颜色插值、中文字体回退。</summary>
    static class Gfx
    {
        /// <summary>构造圆角矩形路径（四段 90° 圆弧拼接），半径自动限制在宽高一半以内。</summary>
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0.1f) { p.AddRectangle(r); return p; }
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        /// <summary>颜色线性插值，t 会被限制在 [0,1]；用于勾选/完成的淡入淡出。</summary>
        public static Color Lerp(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>按优先级获取中文字体：微软雅黑 UI → 微软雅黑 → 系统通用字体。</summary>
        public static Font YaHei(float pt, FontStyle style)
        {
            try { return new Font("Microsoft YaHei UI", pt, style, GraphicsUnit.Point); }
            catch
            {
                try { return new Font("Microsoft YaHei", pt, style, GraphicsUnit.Point); }
                catch { return new Font(FontFamily.GenericSansSerif, pt, style, GraphicsUnit.Point); }
            }
        }
    }

    /// <summary>用到的 Win32 API 封装。</summary>
    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minSize, IntPtr maxSize);

        /// <summary>
        /// 回收当前进程的工作集：把暂时不用的内存页交还系统（任务管理器里内存占用会明显下降）。
        /// 这只是降低驻留内存，页面仍在系统的备用列表里，下次用到时会按需调回，不影响数据。
        /// </summary>
        public static void TrimWorkingSet()
        {
            try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }

        /// <summary>请求 Windows 11 给无边框窗口加圆角（属性 33，值 2=圆角；Win10 上调用无效但不报错）。</summary>
        public static void SetRounded(IntPtr hwnd)
        {
            try
            {
                int pref = 2;
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch { }
        }

        /// <summary>设置输入框占位提示文字（EM_SETCUEBANNER），WinForms 的 TextBox 本身没有该属性。</summary>
        public static void SetCue(IntPtr hwnd, string text)
        {
            try { SendMessage(hwnd, 0x1501, (IntPtr)1, text); } catch { }
        }
    }

    /// <summary>
    /// 全局滚轮过滤器：拦截 WM_MOUSEWHEEL，若光标位于列表区域则直接滚动列表。
    /// 这样即使焦点在输入框（输入框不处理滚轮），鼠标悬停在列表上也能正常滚动。
    /// </summary>
    class WheelFilter : IMessageFilter
    {
        readonly MainForm form;

        public WheelFilter(MainForm f) { form = f; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x20A)
            {
                // wParam 高 16 位是有符号的滚轮增量（每格 ±120）
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (form.HandleWheel(Cursor.Position, delta)) return true;  // 已处理，不再向下传递
            }
            return false;
        }
    }

    /// <summary>
    /// 标题栏小按钮（开机自启/置顶/最小化/关闭），完全自绘：悬停/按下底色 + 矢量图标，
    /// 不依赖系统图标字体；置顶或开机自启处于开启状态时图标显示为强调色。
    /// </summary>
    class MiniButton : Control
    {
        public enum Kind { Minimize, Close, Pin, Startup }

        readonly Kind kind;
        bool hot;     // 鼠标悬停
        bool down;    // 鼠标按下
        bool active;  // 置顶按钮的激活状态

        // 缓存绘制资源：按钮悬停会频繁重绘，避免每次都 new Pen/Brush/Path
        GraphicsPath hoverPath;
        int hoverPathW = -1;
        int hoverPathH = -1;
        readonly SolidBrush brushHover;
        readonly SolidBrush brushDown;
        readonly Pen penNormal;
        readonly Pen penInk;
        readonly Pen penAccent;
        readonly Pen penDanger;

        public bool Active
        {
            get { return active; }
            set { if (active != value) { active = value; Invalidate(); } }
        }

        public MiniButton(Kind k)
        {
            kind = k;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            // 关闭基类“松开鼠标自动触发 Click”的默认行为，改由 OnMouseUp 按“是否仍在按钮内”手动触发。
            // 否则自动 Click 会与手动 OnClick 叠加成两次（曾导致置顶按钮按一次切换两次）。
            SetStyle(ControlStyles.StandardClick, false);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;

            brushHover = new SolidBrush(C.Hover);
            brushDown = new SolidBrush(Color.FromArgb(226, 226, 233));
            penNormal = MakePen(Color.FromArgb(120, 120, 130));
            penInk = MakePen(C.Ink);
            penAccent = MakePen(C.Accent);
            penDanger = MakePen(C.Danger);
        }

        static Pen MakePen(Color color)
        {
            Pen pen = new Pen(color, Ui.Pxf(1.5f));
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            return pen;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = false;
            down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                down = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            // 只有按下后还在按钮范围内松开才算一次点击
            bool fire = down && ClientRectangle.Contains(e.Location);
            down = false;
            Invalidate();
            if (fire) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 悬停/按下时的圆角底色（路径按需缓存，仅尺寸变化时重建）
            if (down || hot)
            {
                if (hoverPath == null || hoverPathW != Width || hoverPathH != Height)
                {
                    if (hoverPath != null) hoverPath.Dispose();
                    hoverPath = Gfx.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Pxf(7f));
                    hoverPathW = Width;
                    hoverPathH = Height;
                }
                g.FillPath(down ? brushDown : brushHover, hoverPath);
            }

            // 图标颜色：置顶/自启激活=强调色；关闭悬停=红色；其他悬停=主文字色
            Pen pen = active ? penAccent
                     : (hot && kind == Kind.Close) ? penDanger
                     : hot ? penInk
                     : penNormal;

            float cx = Width / 2f;
            float cy = Height / 2f;
            float u = Ui.Pxf(1f);  // 图标单位长度，随 DPI 缩放

            if (kind == Kind.Close)
            {
                // 叉号
                float d = Ui.Pxf(4.8f);
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }
            else if (kind == Kind.Minimize)
            {
                // 横线
                g.DrawLine(pen, cx - 5f * u, cy + 0.5f * u, cx + 5f * u, cy + 0.5f * u);
            }
            else if (kind == Kind.Pin)
            {
                // 图钉：顶部横帽 + 梯形钉身 + 下方针尖
                g.DrawLine(pen, cx - 4.6f * u, cy - 5.4f * u, cx + 4.6f * u, cy - 5.4f * u);
                g.DrawLine(pen, cx - 3.9f * u, cy - 4.6f * u, cx - 1.9f * u, cy + 0.6f * u);
                g.DrawLine(pen, cx + 3.9f * u, cy - 4.6f * u, cx + 1.9f * u, cy + 0.6f * u);
                g.DrawLine(pen, cx - 1.9f * u, cy + 0.6f * u, cx + 1.9f * u, cy + 0.6f * u);
                g.DrawLine(pen, cx, cy + 0.6f * u, cx, cy + 5.6f * u);
            }
            else
            {
                // 开机自启动：电源符号（顶部开口的圆弧 + 竖线）
                RectangleF arc = new RectangleF(cx - 5f * u, cy - 5f * u, 10f * u, 10f * u);
                g.DrawArc(pen, arc, -60f, 300f);
                g.DrawLine(pen, cx, cy - 6.3f * u, cx, cy - 1.3f * u);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (hoverPath != null) hoverPath.Dispose();
                brushHover.Dispose();
                brushDown.Dispose();
                penNormal.Dispose();
                penInk.Dispose();
                penAccent.Dispose();
                penDanger.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>输入框底板：自绘圆角矩形背景 + 细边框，TextBox 作为子控件叠放在上面。</summary>
    class InputHost : Panel
    {
        GraphicsPath bgPath;          // 圆角路径缓存，尺寸变化时重建
        int bgW = -1;
        int bgH = -1;
        readonly SolidBrush brushSurface;
        readonly Pen penLine;

        public InputHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = C.Bg;
            brushSurface = new SolidBrush(C.Surface);
            penLine = new Pen(C.Line, 1f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (bgPath == null || bgW != Width || bgH != Height)
            {
                if (bgPath != null) bgPath.Dispose();
                // 内缩 0.5px，避免 1px 边框被裁掉一半
                bgPath = Gfx.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Pxf(10f));
                bgW = Width;
                bgH = Height;
            }
            e.Graphics.FillPath(brushSurface, bgPath);
            e.Graphics.DrawPath(penLine, bgPath);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (bgPath != null) bgPath.Dispose();
                brushSurface.Dispose();
                penLine.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 自绘待办列表。
    ///
    /// 坐标：第 i 行的 Y = PadTopPx + i*行高 - scrollY，滚动通过 scrollY 平移绘制实现，
    /// 右侧另画一条细滚动条（不使用系统滚动条）。
    ///
    /// 交互：
    ///   - 点击整行/勾选框：切换完成状态（具体动作交给 MainForm 处理并持久化）；
    ///   - 悬停行：显示行底色，右侧出现删除 ×；
    ///   - 按下后移动超过 5 像素：立即进入拖动排序（无需长按）——拖动项浮起，
    ///     原位置显示虚线占位槽，经过其他行时实时交换顺序；靠近上下边缘自动滚动；
    ///   - 按下未移动就松开：按普通点击处理，不会误触发拖动。
    ///
    /// 拖动状态说明：
    ///   press      按下时的行索引（用于判断这是点击还是拖动）
    ///   dragIndex  拖动项当前所在的索引（实时排序后会跟着变化）
    ///   dragPos    光标在列表内的位置（决定浮起卡片画在哪）
    ///   grabOffset 按下点距条目顶部的距离（让浮起卡片“跟手”，不跳位）
    /// </summary>
    class TodoList : Control
    {
        // 以下尺寸都是 96 DPI 下的 DIP，使用时统一用 Ui.Px 换算
        const int ItemH = 46;
        const int CheckSize = 20;

        readonly MainForm owner;        // 用于回调持久化、删除、切换完成状态
        readonly List<Todo> todos;      // 与 MainForm 共享同一个 List（拖动排序直接改它）
        readonly Font itemFont;
        readonly Font hintFont;
        readonly Timer dragTimer;       // 拖动期间的 16ms 心跳：自动滚动 + 同步光标位置
        int scrollY;                    // 垂直滚动偏移（像素，>=0）
        int hover = -1;                 // 当前悬停行，-1 表示无
        bool hoverDelete;               // 光标是否位于悬停行的删除按钮上
        int press = -1;                 // 左键按下时的行索引
        bool pressDelete;               // 按下点是否在删除按钮上（此时不启动拖动）
        Point pressPos;                 // 按下位置，用于 5px 拖动阈值判断
        bool draggingItem;              // 是否正在拖动排序
        int dragIndex = -1;             // 拖动项当前所在索引
        Point dragPos;                  // 拖动时光标位置（列表坐标）
        int grabOffset;                 // 光标相对条目顶部的偏移

        // ---- 绘制资源缓存 ----
        // 列表会因悬停、动画、滚动、拖动频繁重绘；这里把固定颜色的 Brush/Pen 和
        // 圆角路径缓存起来，避免每帧产生大量短命 GDI+ 对象（减少 GC 与句柄抖动）。
        readonly SolidBrush brushWhite;
        readonly SolidBrush brushHover;
        readonly SolidBrush brushSoft;
        readonly SolidBrush brushShadow1;   // 拖动卡片阴影（由浅到深三层）
        readonly SolidBrush brushShadow2;
        readonly SolidBrush brushShadow3;
        readonly SolidBrush brushScroll;
        readonly SolidBrush brushAccent;
        readonly Pen penBoxBorder;
        readonly Pen penBoxAccent;
        readonly Pen penCheck;
        readonly Pen penStrikeDone;
        readonly Pen penPlaceholder;
        readonly Pen penDelete;
        readonly Pen penDeleteHot;
        readonly Pen penCardBorder;
        GraphicsPath rowPath;               // 行背景圆角路径（宽度变化时重建）
        GraphicsPath[] shadowPaths;         // 拖动卡片的三层阴影路径
        int rowPathW = -1;
        int rowPathH = -1;
        GraphicsPath checkPath;             // 勾选框圆角路径
        int checkPathSize = -1;
        GraphicsPath deletePath;            // 删除按钮圆角路径
        int deletePathSize = -1;
        Size emptySize1;                    // 空列表提示文本的测量结果缓存
        Size emptySize2;
        bool emptyMeasured;
        PointF[] checkPts;                  // 对勾折线坐标缓存（同一 DPI 下只计算一次）
        float checkPtsU = -1;

        public TodoList(MainForm owner, List<Todo> todos)
        {
            this.owner = owner;
            this.todos = todos;
            itemFont = Gfx.YaHei(10.5f, FontStyle.Regular);
            hintFont = Gfx.YaHei(9f, FontStyle.Regular);
            // 双缓冲：所有绘制都在内存位图完成后一次性上屏，避免闪烁
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = C.Bg;
            dragTimer = new Timer();
            dragTimer.Interval = 16;
            dragTimer.Tick += DragTick;

            brushWhite = new SolidBrush(Color.White);
            brushHover = new SolidBrush(C.ItemHover);
            brushSoft = new SolidBrush(Color.FromArgb(238, 238, 243));
            brushShadow1 = new SolidBrush(Color.FromArgb(21, 0, 0, 0));
            brushShadow2 = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
            brushShadow3 = new SolidBrush(Color.FromArgb(7, 0, 0, 0));
            brushScroll = new SolidBrush(Color.FromArgb(214, 214, 221));
            brushAccent = new SolidBrush(C.Accent);

            penBoxBorder = new Pen(C.BoxBorder, Ui.Pxf(1.4f));
            penBoxAccent = new Pen(C.Accent, Ui.Pxf(1.4f));
            penCheck = new Pen(Color.White, Ui.Pxf(1.9f));
            penCheck.StartCap = LineCap.Round;
            penCheck.EndCap = LineCap.Round;
            penCheck.LineJoin = LineJoin.Round;
            penStrikeDone = new Pen(C.DoneText, Ui.Pxf(1.3f));
            penPlaceholder = new Pen(Color.FromArgb(220, 220, 228), 1f);
            penPlaceholder.DashStyle = DashStyle.Dash;
            penDelete = new Pen(Color.FromArgb(170, 170, 180), Ui.Pxf(1.4f));
            penDelete.StartCap = LineCap.Round;
            penDelete.EndCap = LineCap.Round;
            penDeleteHot = new Pen(C.Danger, Ui.Pxf(1.4f));
            penDeleteHot.StartCap = LineCap.Round;
            penDeleteHot.EndCap = LineCap.Round;
            penCardBorder = new Pen(Color.FromArgb(228, 228, 234), 1f);
        }

        /// <summary>按需构建（或在宽度变化时重建）行背景与拖动阴影的圆角路径。</summary>
        void EnsureRowPaths()
        {
            int w = Width - Ui.Px(12);
            int h = ItemHeightPx - Ui.Px(6);
            if (rowPath != null && rowPathW == w && rowPathH == h) return;

            DisposeRowPaths();
            Rectangle row = new Rectangle(Ui.Px(5), Ui.Px(3), w, h);
            rowPath = Gfx.Round(row, Ui.Pxf(8f));
            shadowPaths = new GraphicsPath[3];
            for (int k = 1; k <= 3; k++)
            {
                Rectangle sh = row;
                sh.Y += Ui.Px(2f);
                sh.Inflate(Ui.Px(k * 0.7f), Ui.Px(k * 0.7f));
                shadowPaths[k - 1] = Gfx.Round(sh, Ui.Pxf(9f));
            }
            rowPathW = w;
            rowPathH = h;
        }

        void DisposeRowPaths()
        {
            if (rowPath != null) { rowPath.Dispose(); rowPath = null; }
            if (shadowPaths != null)
            {
                for (int i = 0; i < shadowPaths.Length; i++)
                    if (shadowPaths[i] != null) shadowPaths[i].Dispose();
                shadowPaths = null;
            }
        }

        /// <summary>勾选框的圆角路径（尺寸固定，只创建一次）。</summary>
        GraphicsPath EnsureCheckPath(int size)
        {
            if (checkPath == null || checkPathSize != size)
            {
                if (checkPath != null) checkPath.Dispose();
                checkPath = Gfx.Round(new RectangleF(0.5f, 0.5f, size - 1f, size - 1f), Ui.Pxf(5.5f));
                checkPathSize = size;
            }
            return checkPath;
        }

        /// <summary>删除按钮的圆角路径（尺寸固定，只创建一次）。</summary>
        GraphicsPath EnsureDeletePath(int size)
        {
            if (deletePath == null || deletePathSize != size)
            {
                if (deletePath != null) deletePath.Dispose();
                deletePath = Gfx.Round(new RectangleF(0, 0, size, size), Ui.Pxf(7f));
                deletePathSize = size;
            }
            return deletePath;
        }

        // 常用尺寸的像素值（按 DPI 换算）
        int ItemHeightPx { get { return Ui.Px(ItemH); } }
        int PadTopPx { get { return Ui.Px(6); } }
        int CheckSizePx { get { return Ui.Px(CheckSize); } }
        int CheckLeftPx { get { return Ui.Px(18); } }
        int TextLeftPx { get { return Ui.Px(52); } }

        // 内容总高度：上边距 + 所有行 + 底部留白
        int ContentHeight { get { return PadTopPx + todos.Count * ItemHeightPx + Ui.Px(10); } }
        // 最大可滚动距离；内容不足一屏时为 0
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        /// <summary>新增待办后回到顶部，保证新条目可见。</summary>
        public void ResetScroll()
        {
            scrollY = 0;
            Invalidate();
        }

        /// <summary>滚动指定增量（来自滚轮），并限制在有效范围内。</summary>
        public void ScrollBy(int delta)
        {
            scrollY -= (int)Math.Round(delta * 0.55);
            ClampScroll();
            Invalidate();
        }

        void ClampScroll()
        {
            if (scrollY < 0) scrollY = 0;
            if (scrollY > MaxScroll) scrollY = MaxScroll;
        }

        /// <summary>第 i 行当前绘制位置的 Y（已扣除滚动偏移）。</summary>
        int ItemTop(int i) { return PadTopPx + i * ItemHeightPx - scrollY; }

        /// <summary>第 i 行删除按钮的点击热区（行右侧 26×26）。</summary>
        Rectangle DeleteRect(int i)
        {
            int d = Ui.Px(26);
            return new Rectangle(Width - Ui.Px(42), ItemTop(i) + (ItemHeightPx - d) / 2, d, d);
        }

        /// <summary>根据列表内坐标反查行号；不在任何行上时返回 -1。</summary>
        int HitIndex(Point p)
        {
            if (todos.Count == 0) return -1;
            int y = p.Y + scrollY - PadTopPx;   // 还原到“内容坐标系”
            if (y < 0) return -1;
            int i = y / ItemHeightPx;
            if (i < 0 || i >= todos.Count) return -1;
            return i;
        }

        /// <summary>
        /// 推进勾选动画：每个条目按 0.1/帧（16ms）逼近目标值，约 160ms 完成。
        /// 返回是否还有未完成的动画，MainForm 据此决定是否停止定时器。
        /// </summary>
        public bool StepAnimations()
        {
            bool any = false;
            const float step = 0.1f;
            for (int i = 0; i < todos.Count; i++)
            {
                Todo t = todos[i];
                float target = t.Done ? 1f : 0f;
                if (t.Anim < target)
                {
                    t.Anim += step;
                    if (t.Anim > target) t.Anim = target;
                    any = true;
                }
                else if (t.Anim > target)
                {
                    t.Anim -= step;
                    if (t.Anim < target) t.Anim = target;
                    any = true;
                }
            }
            if (any) Invalidate();
            return any;
        }

        /// <summary>
        /// 拖动期间的 16ms 心跳：
        /// 1) 用真实光标位置刷新拖动卡片（也覆盖“按住不动但列表在自动滚动”的情况）；
        /// 2) 光标靠近上下边缘时持续滚动列表；
        /// 3) 根据光标所在行实时调整拖动项的位置。
        /// </summary>
        void DragTick(object sender, EventArgs e)
        {
            if (!draggingItem)
            {
                dragTimer.Stop();
                return;
            }
            Point cur = PointToClient(Cursor.Position);
            dragPos = cur;
            bool changed = false;
            int edge = Ui.Px(30);   // 触发自动滚动的边缘感应区
            if (cur.Y < edge && scrollY > 0)
            {
                scrollY = Math.Max(0, scrollY - Ui.Px(7));
                changed = true;
            }
            else if (cur.Y > Height - edge && scrollY < MaxScroll)
            {
                scrollY = Math.Min(MaxScroll, scrollY + Ui.Px(7));
                changed = true;
            }
            UpdateDragIndex(cur);
            if (changed) Invalidate();
        }

        /// <summary>进入拖动：记录拖动项、抓取偏移，切换为抓手光标并启动拖动心跳。</summary>
        void StartDrag(Point p)
        {
            if (press < 0 || press >= todos.Count) return;
            draggingItem = true;
            dragIndex = press;
            dragPos = p;
            // 光标在条目内的相对位置，之后画浮起卡片时用它保持“跟手”
            grabOffset = p.Y - ItemTop(dragIndex);
            if (grabOffset < 0) grabOffset = 0;
            if (grabOffset > ItemHeightPx) grabOffset = ItemHeightPx;
            hover = -1;         // 拖动时取消行悬停高亮
            hoverDelete = false;
            Cursor = Cursors.SizeAll;
            dragTimer.Start();
            Invalidate();
        }

        /// <summary>
        /// 根据光标行号实时移动拖动项（边拖边排序）。
        /// 目标索引限制在 [0, count-1]；列表长度在拖动过程中不变。
        /// </summary>
        void UpdateDragIndex(Point p)
        {
            if (dragIndex < 0 || dragIndex >= todos.Count) return;
            int target = (p.Y + scrollY - PadTopPx) / ItemHeightPx;
            if (target < 0) target = 0;
            if (target > todos.Count - 1) target = todos.Count - 1;
            if (target == dragIndex) return;
            Todo t = todos[dragIndex];
            todos.RemoveAt(dragIndex);
            todos.Insert(target, t);
            dragIndex = target;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(C.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            ClampScroll();

            if (todos.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            int h = ItemHeightPx;
            // 只绘制可视区域内的行（上下各留 2px 冗余，避免边缘裁切）
            for (int i = 0; i < todos.Count; i++)
            {
                int top = ItemTop(i);
                if (top + h < -2) continue;
                if (top > Height + 2) break;
                // 拖动项的原位置画成占位槽，浮起卡片最后单独绘制
                if (draggingItem && i == dragIndex)
                {
                    DrawPlaceholder(g, top);
                    continue;
                }
                DrawItem(g, i, top);
            }

            DrawScrollbar(g);

            if (draggingItem) DrawDragged(g);
        }

        /// <summary>拖动占位槽：灰色圆角块 + 虚线描边，表示松手后的落点。</summary>
        void DrawPlaceholder(Graphics g, int top)
        {
            EnsureRowPaths();
            GraphicsState state = g.Save();
            g.TranslateTransform(0, top);
            g.FillPath(brushSoft, rowPath);
            g.DrawPath(penPlaceholder, rowPath);
            g.Restore(state);
        }

        /// <summary>绘制浮起的拖动卡片：顶部 = 光标 Y - 抓取偏移，并限制在列表内不被完全拖出。</summary>
        void DrawDragged(Graphics g)
        {
            if (dragIndex < 0 || dragIndex >= todos.Count) return;
            int top = dragPos.Y - grabOffset;
            if (top < -Ui.Px(12)) top = -Ui.Px(12);
            if (top > Height - ItemHeightPx + Ui.Px(12)) top = Height - ItemHeightPx + Ui.Px(12);
            DrawItem(g, dragIndex, top, true);
        }

        /// <summary>空列表提示（垂直居中）；文本尺寸只测量一次。</summary>
        void DrawEmpty(Graphics g)
        {
            if (!emptyMeasured)
            {
                emptySize1 = TextRenderer.MeasureText(g, "还没有待办", itemFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                emptySize2 = TextRenderer.MeasureText(g, "在下方输入内容，按回车添加", hintFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                emptyMeasured = true;
            }
            int cy = (Height - (emptySize1.Height + Ui.Px(10) + emptySize2.Height)) / 2;
            TextRenderer.DrawText(g, "还没有待办", itemFont, new Point((Width - emptySize1.Width) / 2, cy),
                Color.FromArgb(170, 170, 181), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "在下方输入内容，按回车添加", hintFont, new Point((Width - emptySize2.Width) / 2, cy + emptySize1.Height + Ui.Px(10)),
                Color.FromArgb(197, 197, 206), TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 绘制一行：背景（悬停/拖动浮起）→ 勾选框（颜色随 Anim 过渡）→ 对勾 → 文本
        /// → 删除线（宽度有缓存）→ 删除按钮。floating=true 表示这是拖动中的浮起卡片。
        /// 固定颜色的画刷/画笔和圆角路径都来自缓存，只有动画过渡中的插值颜色才临时创建。
        /// </summary>
        void DrawItem(Graphics g, int index, int top, bool floating = false)
        {
            Todo t = todos[index];
            int h = ItemHeightPx;
            float a = t.Anim;
            EnsureRowPaths();

            // 背景：把缓存的路径平移到该行（GDI+ 变换只用于图形，文本仍用绝对坐标）
            GraphicsState bgState = g.Save();
            g.TranslateTransform(0, top);
            if (floating)
            {
                g.FillPath(brushShadow3, shadowPaths[2]);
                g.FillPath(brushShadow2, shadowPaths[1]);
                g.FillPath(brushShadow1, shadowPaths[0]);
                g.FillPath(brushWhite, rowPath);
                g.DrawPath(penCardBorder, rowPath);
            }
            else if (index == hover)
            {
                g.FillPath(brushHover, rowPath);
            }
            g.Restore(bgState);

            bool open = a <= 0.001f;
            bool done = a >= 0.999f;

            // 勾选框
            int size = CheckSizePx;
            Rectangle cb = new Rectangle(CheckLeftPx, top + (h - size) / 2, size, size);
            GraphicsPath cp = EnsureCheckPath(size);
            GraphicsState boxState = g.Save();
            g.TranslateTransform(cb.X, cb.Y);
            if (open)
            {
                g.FillPath(brushWhite, cp);
                g.DrawPath(penBoxBorder, cp);
            }
            else if (done)
            {
                g.FillPath(brushAccent, cp);
                g.DrawPath(penBoxAccent, cp);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Gfx.Lerp(Color.White, C.Accent, a)))
                    g.FillPath(b, cp);
                using (Pen pen = new Pen(Gfx.Lerp(C.BoxBorder, C.Accent, a), Ui.Pxf(1.4f)))
                    g.DrawPath(pen, cp);
            }

            // 对勾（坐标相对勾选框，按尺寸缩放；折线点也做了缓存）
            if (a > 0.02f)
            {
                EnsureCheckPoints(size / 20f);
                if (done)
                {
                    g.DrawLines(penCheck, checkPts);
                }
                else
                {
                    int alpha = (int)(255 * Math.Min(1f, a * 1.4f));
                    using (Pen pen = new Pen(Color.FromArgb(alpha, Color.White), Ui.Pxf(1.9f)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, checkPts);
                    }
                }
            }
            g.Restore(boxState);

            // 文本（GDI 绘制，颜色直接传参）
            int textLeft = TextLeftPx;
            int maxW = Width - textLeft - Ui.Px(56);   // 右侧为删除按钮留出空间
            if (maxW < Ui.Px(40)) maxW = Ui.Px(40);
            Rectangle textRect = new Rectangle(textLeft, top, maxW, h);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            Color textColor = open ? C.Ink : done ? C.DoneText : Gfx.Lerp(C.Ink, C.DoneText, a);
            TextRenderer.DrawText(g, t.Text, itemFont, textRect, textColor, flags);

            // 删除线：测量结果缓存在 Todo 上，只有可用宽度变化时才重新测量
            if (a > 0.02f)
            {
                if (t.StrikeWidthFor != maxW)
                {
                    Size ts = TextRenderer.MeasureText(g, t.Text, itemFont, new Size(maxW, int.MaxValue),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    int tw = ts.Width;
                    if (tw > maxW - Ui.Px(2)) tw = maxW - Ui.Px(2);
                    if (tw < 0) tw = 0;
                    t.StrikeWidth = tw;
                    t.StrikeWidthFor = maxW;
                }
                if (t.StrikeWidth > 0)
                {
                    float ly = top + h / 2f + Ui.Pxf(0.5f);   // 文字垂直居中位置
                    if (done)
                    {
                        g.DrawLine(penStrikeDone, textLeft, ly, textLeft + t.StrikeWidth, ly);
                    }
                    else
                    {
                        int alpha = (int)(255 * Math.Min(1f, a * 1.2f));
                        using (Pen pen = new Pen(Color.FromArgb(alpha, textColor), Ui.Pxf(1.3f)))
                            g.DrawLine(pen, textLeft, ly, textLeft + t.StrikeWidth, ly);
                    }
                }
            }

            // 删除按钮：仅悬停行显示；拖动中的浮起卡片不显示
            if (!floating && index == hover)
            {
                Rectangle dr = DeleteRect(index);
                GraphicsPath dp = EnsureDeletePath(dr.Width);
                GraphicsState delState = g.Save();
                g.TranslateTransform(dr.X, dr.Y);
                if (hoverDelete)
                    g.FillPath(brushSoft, dp);
                Pen iconPen = hoverDelete ? penDeleteHot : penDelete;
                float cx = dr.Width / 2f;
                float cy = dr.Height / 2f;
                float e2 = Ui.Pxf(4.2f);
                g.DrawLine(iconPen, cx - e2, cy - e2, cx + e2, cy + e2);
                g.DrawLine(iconPen, cx + e2, cy - e2, cx - e2, cy + e2);
                g.Restore(delState);
            }
        }

        /// <summary>按 DPI 尺寸计算并缓存对勾的折线点。</summary>
        void EnsureCheckPoints(float u)
        {
            if (checkPts != null && checkPtsU == u) return;
            checkPts = new PointF[] {
                new PointF(5.6f * u, 10.6f * u),
                new PointF(8.7f * u, 13.7f * u),
                new PointF(14.4f * u, 6.6f * u)
            };
            checkPtsU = u;
        }

        /// <summary>右侧细滚动条：滑块高度按“可视高度/内容高度”比例，最小 26px。</summary>
        void DrawScrollbar(Graphics g)
        {
            if (MaxScroll <= 0) return;
            int trackTop = Ui.Px(6);
            int trackH = Height - Ui.Px(12);
            int thumbH = (int)((long)trackH * Height / ContentHeight);
            if (thumbH < Ui.Px(26)) thumbH = Ui.Px(26);
            if (thumbH > trackH) thumbH = trackH;
            int thumbY = trackTop + (int)((long)(trackH - thumbH) * scrollY / MaxScroll);
            g.FillRectangle(brushScroll, new Rectangle(Width - Ui.Px(7), thumbY, Ui.Px(3), thumbH));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动中：更新卡片位置并实时排序
            if (draggingItem)
            {
                dragPos = e.Location;
                UpdateDragIndex(e.Location);
                Invalidate();
                return;
            }

            // 窗口边缘缩放：列表铺满窗口宽度，左右边缘的鼠标事件先转发给主窗口，
            // 命中边缘时显示缩放光标，避免被列表的行悬停逻辑覆盖。
            if (!draggingItem && press < 0)
            {
                Cursor edgeCursor = owner.EdgeCursorAt(PointToScreen(e.Location));
                if (edgeCursor != null)
                {
                    if (hover != -1 || hoverDelete)
                    {
                        hover = -1;
                        hoverDelete = false;
                        Invalidate();
                    }
                    Cursor = edgeCursor;
                    return;
                }
            }

            // 已按下但还没拖动：移动超过 5px 阈值才认定为拖动。
            // 这样普通点击（含轻微手抖）仍会触发勾选，单击不会误排序。
            if (press >= 0 && !pressDelete &&
                (Math.Abs(e.X - pressPos.X) > Ui.Px(5) || Math.Abs(e.Y - pressPos.Y) > Ui.Px(5)))
            {
                StartDrag(e.Location);
                return;
            }

            // 未按下：更新悬停行与删除按钮热区
            int idx = HitIndex(e.Location);
            bool del = idx >= 0 && DeleteRect(idx).Contains(e.Location);
            if (idx != hover || del != hoverDelete)
            {
                hover = idx;
                hoverDelete = del;
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // 拖动或按住期间鼠标可能暂时移出控件（已 Capture），不要清空状态以免闪烁
            if (draggingItem || press >= 0) return;
            if (hover != -1)
            {
                hover = -1;
                hoverDelete = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            // 按在窗口左右边缘时交给主窗口开始缩放（列表铺满宽度，会遮住边缘）
            if (owner.StartResizeAt(PointToScreen(e.Location))) return;

            press = HitIndex(e.Location);
            pressDelete = press >= 0 && DeleteRect(press).Contains(e.Location);

            if (press >= 0)
            {
                pressPos = e.Location;
                // 捕获鼠标：这样即使拖动途中移出控件/窗口，也能收到 MouseUp 结束状态
                Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            dragTimer.Stop();
            Capture = false;

            // 拖动结束：列表顺序已在拖动过程中实时调整，这里只需保存并恢复悬停态
            if (draggingItem)
            {
                draggingItem = false;
                dragIndex = -1;
                press = -1;
                pressDelete = false;
                grabOffset = 0;

                int hit = HitIndex(e.Location);
                hover = hit;
                hoverDelete = hit >= 0 && DeleteRect(hit).Contains(e.Location);
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                owner.PersistOrder();
                Invalidate();
                return;
            }

            // 非拖动：只有“按下/松开在同一行、同一区域（整行或删除按钮）”才执行动作，
            // 防止按下后滑到别处松开造成误勾选/误删除。
            int idx = HitIndex(e.Location);
            bool del = idx >= 0 && DeleteRect(idx).Contains(e.Location);
            int pressed = press;
            bool pressedDel = pressDelete;
            press = -1;
            pressDelete = false;

            if (idx >= 0 && idx == pressed && del == pressedDel)
            {
                Todo t = todos[idx];
                if (del) owner.DeleteTodo(t);
                else owner.ToggleTodo(t);
                hover = HitIndex(e.Location);
                hoverDelete = hover >= 0 && DeleteRect(hover).Contains(e.Location);
                Invalidate();
            }
        }

        /// <summary>滚轮：仅当光标位于列表区域时接管，返回 true 表示已处理。</summary>
        public bool HandleWheel(Point screenPoint, int delta)
        {
            Rectangle r = RectangleToScreen(ClientRectangle);
            if (!r.Contains(screenPoint)) return false;
            ScrollBy(delta);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                dragTimer.Stop();
                dragTimer.Dispose();
                itemFont.Dispose();
                hintFont.Dispose();

                DisposeRowPaths();
                if (checkPath != null) { checkPath.Dispose(); checkPath = null; }
                if (deletePath != null) { deletePath.Dispose(); deletePath = null; }
                brushWhite.Dispose();
                brushHover.Dispose();
                brushSoft.Dispose();
                brushShadow1.Dispose();
                brushShadow2.Dispose();
                brushShadow3.Dispose();
                brushScroll.Dispose();
                brushAccent.Dispose();
                penBoxBorder.Dispose();
                penBoxAccent.Dispose();
                penCheck.Dispose();
                penStrikeDone.Dispose();
                penPlaceholder.Dispose();
                penDelete.Dispose();
                penDeleteHot.Dispose();
                penCardBorder.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 无边框主窗口。布局（单位 DIP）：
    ///   ┌ 顶部标题栏 50px：logo + 标题 + 开机自启/置顶/最小化/关闭
    ///   ├ 待办列表：占据剩余空间（自绘，见 TodoList）
    ///   ├ 底部统计 30px：共 N 项 / 已完成 N + “清除已完成”链接
    ///   └ 输入框 46px：回车添加、Esc 清空
    /// 标题栏区域由窗口自身处理鼠标按下以拖动窗口；右侧小按钮是子控件，各自处理点击。
    /// 窗口四边与四角有 6px 的缩放感应区，拖动即可调整大小（最小 300×280 DIP）。
    /// 任何内容/顺序/窗口位置/尺寸/置顶状态变化都会立即写入 Store（崩溃或断电也不丢数据）。
    /// </summary>
    class MainForm : Form
    {
        // 窗口尺寸与布局参数（DIP）
        const int W = 360;
        const int H = 508;
        const int TopH = 50;
        const int InputH = 46;
        const int MinW = 300;   // 缩放时的最小宽度（保证标题和四个按钮不重叠）
        const int MinH = 280;   // 缩放时的最小高度（标题栏 + 约两行待办 + 统计 + 输入框）

        readonly List<Todo> todos;       // 与 TodoList 共享同一个列表实例（拖动排序会直接修改它）
        readonly TodoList list;
        readonly Panel inputHost;
        readonly TextBox input;
        readonly MiniButton btnPin;
        readonly MiniButton btnStartup;
        readonly MiniButton btnMin;
        readonly MiniButton btnClose;
        readonly Timer anim;             // 勾选动画定时器，动画结束后自动停止
        readonly Timer trimTimer;        // 启动后一次性回收托管堆并归还工作集
        readonly ToolTip tips;

        readonly Font titleFont;
        readonly Font footFont;

        // 顶部 logo / 底部链接的绘制资源缓存（尺寸固定，只创建一次）
        GraphicsPath logoPath;
        PointF[] logoCheckPts;
        SolidBrush logoBrush;
        Pen logoPen;
        Size linkTextSize;
        bool linkTextMeasured;
        int lastFooterCount = -1;
        int lastFooterDone = -1;
        string footerText = "暂无待办";

        Rectangle footerRect;            // 底部统计区域
        Rectangle linkRect;              // “清除已完成”热区，每次绘制时按文本宽度更新
        bool linkHot;                    // 链接是否悬停

        bool dragging;                   // 是否正在拖动窗口
        bool dragMoved;                  // 本次按下后是否真的移动过（用于区分点击和拖动）
        Point dragOffset;                // 按下点相对窗口左上角的偏移

        // 边缘缩放状态
        [Flags]
        enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

        bool resizing;                   // 是否正在拖拽窗口边缘缩放
        ResizeEdge resizeEdge;           // 当前拖拽的是哪条边/哪个角
        Point resizeStartScreen;         // 开始缩放时的屏幕光标位置
        Rectangle resizeStartBounds;     // 开始缩放时的窗口矩形
        ResizeEdge hoverEdge;            // 当前光标悬停的边缘（用于切换缩放光标）

        public MainForm()
        {
            Point pos;
            bool hasPos;
            bool pinned;
            Size savedSize;
            bool hasSize;
            // 读取上次的内容/位置/尺寸/置顶状态
            Store.Load(out todos, out pos, out hasPos, out pinned, out savedSize, out hasSize);

            Text = "狗皮膏药";

            // 显式设置窗口图标：WinForms 不设置时会使用系统默认占位图标，
            // 任务栏/Alt+Tab 就不会显示 exe 内嵌的 app.ico。优先读取编译时嵌入的资源。
            bool iconLoaded = false;
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("TodoTag.app.ico"))
                {
                    if (s != null)
                    {
                        Icon = new Icon(s);
                        iconLoaded = true;
                    }
                }
            }
            catch { }
            if (!iconLoaded)
            {
                // 兜底：直接从 exe 文件提取图标
                try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            }

            // 无边框；布局全部手动按 DPI 换算，因此关闭 WinForms 的自动缩放，避免二次缩放
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            // 恢复窗口尺寸：限制在“最小尺寸”和当前虚拟屏幕之间；旧数据没有 size 行时使用默认尺寸
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            int startW = hasSize ? Math.Max(Ui.Px(MinW), Math.Min(savedSize.Width, virtualScreen.Width)) : Ui.Px(W);
            int startH = hasSize ? Math.Max(Ui.Px(MinH), Math.Min(savedSize.Height, virtualScreen.Height)) : Ui.Px(H);
            ClientSize = new Size(startW, startH);
            MinimumSize = new Size(Ui.Px(MinW), Ui.Px(MinH));
            BackColor = C.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            Font = Gfx.YaHei(10f, FontStyle.Regular);

            titleFont = Gfx.YaHei(11.5f, FontStyle.Bold);
            footFont = Gfx.YaHei(9f, FontStyle.Regular);

            // 控件只创建，具体位置由 ApplyLayout() 按当前窗口尺寸计算（窗口可拖动边缘缩放）
            list = new TodoList(this, todos);
            Controls.Add(list);

            // 输入框：圆角底板 + 无边框 TextBox 叠放在上面
            inputHost = new InputHost();
            Controls.Add(inputHost);

            input = new TextBox();
            input.BorderStyle = BorderStyle.None;
            input.Font = Gfx.YaHei(10.5f, FontStyle.Regular);
            input.BackColor = C.Surface;
            input.ForeColor = C.Ink;
            input.KeyDown += InputKeyDown;
            inputHost.Controls.Add(input);

            // 标题栏右侧四个按钮，从右往左依次排列，均为 30×30（位置在 ApplyLayout 中计算）
            btnClose = new MiniButton(MiniButton.Kind.Close);
            btnClose.Click += delegate { Close(); };

            btnMin = new MiniButton(MiniButton.Kind.Minimize);
            btnMin.Click += delegate
            {
                WindowState = FormWindowState.Minimized;
                // 最小化后窗口不可见，把工作集交还系统，降低常驻内存
                Native.TrimWorkingSet();
            };

            btnPin = new MiniButton(MiniButton.Kind.Pin);
            btnPin.Click += delegate
            {
                TopMost = !TopMost;
                btnPin.Active = TopMost;
                Persist();
            };

            // 开机自启动开关：图标为电源符号，蓝色表示已开启
            btnStartup = new MiniButton(MiniButton.Kind.Startup);
            btnStartup.Click += delegate { ToggleStartup(); };

            Controls.Add(btnStartup);
            Controls.Add(btnPin);
            Controls.Add(btnMin);
            Controls.Add(btnClose);

            tips = new ToolTip();
            tips.SetToolTip(btnPin, "置顶");
            tips.SetToolTip(btnMin, "最小化");
            tips.SetToolTip(btnClose, "关闭");

            TopMost = pinned;
            btnPin.Active = pinned;

            // 从注册表读取当前自启动状态，初始化按钮高亮和悬停提示
            bool autoStart = Startup.IsEnabled(Application.ExecutablePath);
            btnStartup.Active = autoStart;
            UpdateStartupTip(autoStart);

            // 勾选动画：16ms 一帧，由列表推进；没有动画时自动停止，避免空转占用 CPU
            anim = new Timer();
            anim.Interval = 16;
            anim.Tick += delegate { if (!list.StepAnimations()) anim.Stop(); };

            // 启动 2.5 秒后（界面已稳定）强制整理一次托管堆，并把空闲页面归还系统。
            // 只做一次，避免周期 GC 带来卡顿；后续在最小化时再回收工作集。
            trimTimer = new Timer();
            trimTimer.Interval = 2500;
            trimTimer.Tick += delegate
            {
                trimTimer.Stop();
                GC.Collect(2, GCCollectionMode.Forced, true);
                Native.TrimWorkingSet();
            };
            trimTimer.Start();

            // 根据当前窗口尺寸摆放所有控件
            ApplyLayout();

            // 恢复上次窗口位置；若当前虚拟屏幕已不包含该位置（如换显示器），则改为居中
            if (hasPos)
            {
                Rectangle target = new Rectangle(pos.X, pos.Y, ClientSize.Width, ClientSize.Height);
                if (SystemInformation.VirtualScreen.Contains(target))
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = pos;
                }
                else
                {
                    StartPosition = FormStartPosition.CenterScreen;
                }
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        /// <summary>
        /// 按当前 ClientSize 重新布局：列表占中间剩余高度，输入框/统计贴底，标题按钮靠右。
        /// 窗口尺寸变化（拖拽边缘缩放）时由 OnSizeChanged 调用。
        /// </summary>
        void ApplyLayout()
        {
            if (list == null) return;   // 构造早期尺寸变化时控件还没创建

            int topH = Ui.Px(TopH);
            int inputH = Ui.Px(InputH);
            int inputY = ClientSize.Height - Ui.Px(16) - inputH;
            int footH = Ui.Px(30);
            int footY = inputY - Ui.Px(6) - footH;

            list.Bounds = new Rectangle(0, topH, ClientSize.Width, Math.Max(Ui.Px(40), footY - topH));
            inputHost.Bounds = new Rectangle(Ui.Px(14), inputY, Math.Max(Ui.Px(160), ClientSize.Width - Ui.Px(28)), inputH);
            input.Location = new Point(Ui.Px(14), (inputH - input.PreferredHeight) / 2);   // 垂直居中
            input.Width = Math.Max(Ui.Px(100), inputHost.Width - Ui.Px(28));
            footerRect = new Rectangle(Ui.Px(18), footY, Math.Max(Ui.Px(120), ClientSize.Width - Ui.Px(36)), footH);

            btnClose.Bounds = new Rectangle(ClientSize.Width - Ui.Px(42), Ui.Px(10), Ui.Px(30), Ui.Px(30));
            btnMin.Bounds = new Rectangle(btnClose.Left - Ui.Px(32), Ui.Px(10), Ui.Px(30), Ui.Px(30));
            btnPin.Bounds = new Rectangle(btnMin.Left - Ui.Px(32), Ui.Px(10), Ui.Px(30), Ui.Px(30));
            btnStartup.Bounds = new Rectangle(btnPin.Left - Ui.Px(32), Ui.Px(10), Ui.Px(30), Ui.Px(30));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (WindowState == FormWindowState.Minimized) return;
            ApplyLayout();
        }

        /// <summary>判断某个客户区坐标是否落在窗口边缘/角落的缩放感应区内（6 DIP）。</summary>
        ResizeEdge HitEdge(Point clientPoint)
        {
            int m = Ui.Px(6);
            ResizeEdge e = ResizeEdge.None;
            if (clientPoint.X <= m) e |= ResizeEdge.Left;
            else if (clientPoint.X >= ClientSize.Width - m) e |= ResizeEdge.Right;
            if (clientPoint.Y <= m) e |= ResizeEdge.Top;
            else if (clientPoint.Y >= ClientSize.Height - m) e |= ResizeEdge.Bottom;
            return e;
        }

        static Cursor ResizeCursor(ResizeEdge e)
        {
            bool l = (e & ResizeEdge.Left) != 0;
            bool r = (e & ResizeEdge.Right) != 0;
            bool t = (e & ResizeEdge.Top) != 0;
            bool b = (e & ResizeEdge.Bottom) != 0;
            if ((l && t) || (r && b)) return Cursors.SizeNWSE;
            if ((r && t) || (l && b)) return Cursors.SizeNESW;
            if (l || r) return Cursors.SizeWE;
            return Cursors.SizeNS;
        }

        /// <summary>供子控件查询：光标在窗口边缘时返回对应的缩放光标，否则 null。</summary>
        public Cursor EdgeCursorAt(Point screenPoint)
        {
            ResizeEdge e = HitEdge(PointToClient(screenPoint));
            return e == ResizeEdge.None ? null : ResizeCursor(e);
        }

        /// <summary>供子控件在鼠标按下时调用：按在边缘则开始缩放并返回 true。</summary>
        public bool StartResizeAt(Point screenPoint)
        {
            ResizeEdge e = HitEdge(PointToClient(screenPoint));
            if (e == ResizeEdge.None) return false;
            StartResize(e);
            return true;
        }

        /// <summary>开始边缘缩放：记录起始光标和窗口矩形，捕获鼠标以便移出窗口后继续缩放。</summary>
        void StartResize(ResizeEdge edge)
        {
            resizing = true;
            resizeEdge = edge;
            dragMoved = true;   // 缩放结束后不要误触发点击（例如底部链接）
            resizeStartScreen = Cursor.Position;
            resizeStartBounds = Bounds;
            Cursor = ResizeCursor(edge);
            Capture = true;
        }

        /// <summary>
        /// 根据光标位移计算新窗口矩形并应用。
        /// 拖左边/上边时保持右/下边界不动并限制最小尺寸，避免窗口“跳位”。
        /// </summary>
        void DoResize()
        {
            Point cur = Cursor.Position;
            int dx = cur.X - resizeStartScreen.X;
            int dy = cur.Y - resizeStartScreen.Y;
            Rectangle b = resizeStartBounds;
            int minW = Ui.Px(MinW);
            int minH = Ui.Px(MinH);

            if ((resizeEdge & ResizeEdge.Left) != 0)
            {
                int right = b.Right;
                b.X = Math.Min(b.Left + dx, right - minW);
                b.Width = right - b.X;
            }
            else if ((resizeEdge & ResizeEdge.Right) != 0)
            {
                b.Width = Math.Max(minW, b.Width + dx);
            }
            if ((resizeEdge & ResizeEdge.Top) != 0)
            {
                int bottom = b.Bottom;
                b.Y = Math.Min(b.Top + dy, bottom - minH);
                b.Height = bottom - b.Y;
            }
            else if ((resizeEdge & ResizeEdge.Bottom) != 0)
            {
                b.Height = Math.Max(minH, b.Height + dy);
            }
            Bounds = b;
        }

        // 无边框窗口加投影（CS_DROPSHADOW）；Win11 下圆角由 Native.SetRounded 处理
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetRounded(Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // WinForms 的 TextBox 没有 PlaceholderText，这里用 EM_SETCUEBANNER 设置占位提示
            Native.SetCue(input.Handle, "添加待办，按回车保存");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            input.Focus();   // 启动后可直接输入
        }

        /// <summary>输入框键盘：回车添加、Esc 清空；SuppressKeyPress 防止系统提示音。</summary>
        void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddFromInput();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                input.Clear();
            }
        }

        /// <summary>把输入框内容添加为待办：去空白、限长 160，插入列表顶部并回到顶部。</summary>
        void AddFromInput()
        {
            string txt = input.Text.Trim();
            if (txt.Length == 0) return;
            if (txt.Length > 160) txt = txt.Substring(0, 160);

            Todo t = new Todo();
            t.Text = txt;
            todos.Insert(0, t);

            input.Clear();
            list.ResetScroll();
            Persist();
            UpdateFooter();
            list.Invalidate();
        }

        /// <summary>切换完成状态：启动淡出/删除线动画，并立即持久化。</summary>
        public void ToggleTodo(Todo t)
        {
            t.Done = !t.Done;
            if (!anim.Enabled) anim.Start();
            Persist();
            UpdateFooter();
            list.Invalidate();
        }

        /// <summary>删除单条待办（悬停 × 触发）。</summary>
        public void DeleteTodo(Todo t)
        {
            todos.Remove(t);
            Persist();
            UpdateFooter();
            list.Invalidate();
        }

        /// <summary>清除所有已完成项。</summary>
        void ClearCompleted()
        {
            todos.RemoveAll(delegate(Todo t) { return t.Done; });
            Persist();
            UpdateFooter();
            list.Invalidate();
        }

        /// <summary>只重绘底部统计区域，避免整窗刷新。</summary>
        void UpdateFooter()
        {
            Invalidate(new Rectangle(0, footerRect.Y - Ui.Px(2), Width, footerRect.Height + Ui.Px(4)));
        }

        /// <summary>
        /// 保存全部数据。窗口最小化时 Location 不可靠，用 RestoreBounds 记录“还原后”的位置。
        /// 句柄尚未创建时（构造阶段）跳过，避免保存错误坐标。
        /// </summary>
        void Persist()
        {
            if (!IsHandleCreated) return;
            Point p = WindowState == FormWindowState.Normal ? Location : RestoreBounds.Location;
            Store.Save(todos, p, Size, TopMost);
        }

        /// <summary>拖动排序结束后由列表回调，保存新的顺序。</summary>
        public void PersistOrder()
        {
            Persist();
        }

        /// <summary>切换开机自启动（写/删当前用户注册表 Run 项），并同步按钮高亮与悬停提示。</summary>
        void ToggleStartup()
        {
            string exe = Application.ExecutablePath;
            bool enable = !Startup.IsEnabled(exe);
            bool now = Startup.SetEnabled(exe, enable);
            btnStartup.Active = now;
            UpdateStartupTip(now);
        }

        /// <summary>悬停提示同时说明按钮功能与当前状态。</summary>
        void UpdateStartupTip(bool enabled)
        {
            tips.SetToolTip(btnStartup, enabled ? "开机自启动：已开启（点击关闭）" : "开机自启动：未开启（点击开启）");
        }

        /// <summary>把滚轮事件转发给列表（由 WheelFilter 调用）。</summary>
        public bool HandleWheel(Point screenPoint, int delta)
        {
            return list.HandleWheel(screenPoint, delta);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 左上角 logo：蓝色圆角方块 + 白色对勾（与程序图标一致）。
            // 位置和大小固定，路径/画刷/画笔只在首次绘制时创建一次。
            int topH = Ui.Px(TopH);
            float logoSize = Ui.Pxf(18f);
            RectangleF logo = new RectangleF(Ui.Pxf(18f), (topH - logoSize) / 2f, logoSize, logoSize);
            if (logoPath == null)
            {
                logoPath = Gfx.Round(logo, Ui.Pxf(5.5f));
                logoBrush = new SolidBrush(C.Accent);
                logoPen = new Pen(Color.White, Ui.Pxf(1.7f));
                logoPen.StartCap = LineCap.Round;
                logoPen.EndCap = LineCap.Round;
                logoPen.LineJoin = LineJoin.Round;
                float lu = logoSize / 18f;   // 对勾按 18×18 设计，按实际尺寸缩放
                logoCheckPts = new PointF[] {
                    new PointF(logo.X + 4.8f * lu, logo.Y + 9.4f * lu),
                    new PointF(logo.X + 7.8f * lu, logo.Y + 12.4f * lu),
                    new PointF(logo.X + 13.6f * lu, logo.Y + 5.6f * lu)
                };
            }
            g.FillPath(logoBrush, logoPath);
            g.DrawLines(logoPen, logoCheckPts);

            Rectangle titleRect = new Rectangle(Ui.Px(45), 0, Width - Ui.Px(186), topH);
            TextRenderer.DrawText(g, "狗皮膏药", titleFont, titleRect, C.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 底部统计：总数 + 已完成数（字符串只在数量变化时重建）
            int done = 0;
            for (int i = 0; i < todos.Count; i++)
                if (todos[i].Done) done++;

            if (done != lastFooterDone || todos.Count != lastFooterCount)
            {
                string left = todos.Count == 0 ? "暂无待办" : "共 " + todos.Count + " 项";
                if (done > 0) left += "  ·  已完成 " + done;
                footerText = left;
                lastFooterDone = done;
                lastFooterCount = todos.Count;
            }
            Rectangle leftRect = new Rectangle(footerRect.X, footerRect.Y, footerRect.Width, footerRect.Height);
            TextRenderer.DrawText(g, footerText, footFont, leftRect, C.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // “清除已完成”链接：只有存在已完成项时显示；文本尺寸只测量一次
            linkRect = Rectangle.Empty;
            if (done > 0)
            {
                if (!linkTextMeasured)
                {
                    linkTextSize = TextRenderer.MeasureText(g, "清除已完成", footFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    linkTextMeasured = true;
                }
                linkRect = new Rectangle(footerRect.Right - linkTextSize.Width, footerRect.Y, linkTextSize.Width, footerRect.Height);
                Color lc = linkHot ? C.Accent : Color.FromArgb(150, 150, 160);
                TextRenderer.DrawText(g, "清除已完成", footFont, linkRect, lc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // 鼠标按下：
        // 1) 在窗口边缘 6px 内 → 开始拖拽缩放（四个边和四个角都支持）；
        // 2) 在标题栏其余区域 → 拖动窗口（按钮是子控件，会自己接收事件）。
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            dragMoved = false;

            ResizeEdge edge = HitEdge(e.Location);
            if (edge != ResizeEdge.None)
            {
                StartResize(edge);
                return;
            }

            if (e.Y < Ui.Px(TopH))
            {
                dragging = true;
                dragOffset = e.Location;
                Capture = true;   // 捕获鼠标，移出窗口后仍可继续拖动
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 缩放中：不需要关心 e.Location（可能已移出窗口），直接用真实光标位置计算
            if (resizing)
            {
                DoResize();
                return;
            }

            // 拖动窗口：按“光标屏幕坐标 - 按下时偏移”设置窗口位置
            if (dragging)
            {
                Point sp = PointToScreen(e.Location);
                Point np = new Point(sp.X - dragOffset.X, sp.Y - dragOffset.Y);
                // 位移超过 3px 才算真正拖动过，用于区分“点击链接”和“拖完窗口松手”
                if (!dragMoved && (Math.Abs(np.X - Location.X) > Ui.Px(3) || Math.Abs(np.Y - Location.Y) > Ui.Px(3)))
                    dragMoved = true;
                Location = np;
                return;
            }

            // 边缘悬停：显示双向缩放光标（顶部 6px 用于缩放，标题栏拖动从 6px 往下开始）
            ResizeEdge edge = HitEdge(e.Location);
            if (edge != ResizeEdge.None)
            {
                if (hoverEdge != edge)
                {
                    hoverEdge = edge;
                    Cursor = ResizeCursor(edge);
                }
                return;
            }
            hoverEdge = ResizeEdge.None;

            // 链接悬停：手型光标 + 重绘底部区域
            bool hot = linkRect.Contains(e.Location);
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            if (hot != linkHot)
            {
                linkHot = hot;
                Invalidate(new Rectangle(0, footerRect.Y - Ui.Px(2), Width, footerRect.Height + Ui.Px(4)));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (resizing)
            {
                resizing = false;
                hoverEdge = ResizeEdge.None;
                Capture = false;
                Cursor = Cursors.Default;
                Persist();   // 保存新的窗口尺寸（位置也可能因拖左边/上边而变化）
                return;
            }

            dragging = false;
            Capture = false;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (!linkRect.Contains(e.Location)) return;
            if (dragMoved) { dragMoved = false; return; }   // 拖过窗口/缩放过窗口不算点击
            ClearCompleted();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (linkHot)
            {
                linkHot = false;
                Cursor = Cursors.Default;
                Invalidate(new Rectangle(0, footerRect.Y - Ui.Px(2), Width, footerRect.Height + Ui.Px(4)));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Persist();   // 关闭前再保存一次，确保窗口位置/顺序是最新的
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                footFont.Dispose();
                tips.Dispose();
                anim.Dispose();
                trimTimer.Dispose();
                if (logoPath != null) logoPath.Dispose();
                if (logoBrush != null) logoBrush.Dispose();
                if (logoPen != null) logoPen.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
