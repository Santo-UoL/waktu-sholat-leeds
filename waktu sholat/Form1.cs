using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace waktu_sholat
{
    public partial class Form1 : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // DWM: warnai title bar agar serasi tema Islami (Windows 11).
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private static int Bgr(Color c) => c.R | (c.G << 8) | (c.B << 16);

        // Jarak overlay dari kiri taskbar — nilai awal/fallback; saat runtime
        // dihitung adaptif mengikuti tepi kanan widget cuaca (lebarnya berubah).
        private const int OverlayLeftOffset = 175;
        private volatile int _adaptiveOffset = OverlayLeftOffset;
        private volatile int _lastOverlayWidth = 130;
        private System.Threading.Timer? _offsetTimer;

        // Warna taskbar (di-sampel saat runtime agar overlay menyatu).
        private Color _taskbarColor = Color.FromArgb(243, 243, 243);
        private int _colorSampleCounter;

        // ---- Konfigurasi ----
        private const string CsvPath = @"C:\Users\Santo\Downloads\LGM_Prayer_Calendar_2026.csv";
        private const int WarningMinutes = 15;   // berkedip merah X menit sebelum sholat

        // Nama 5 waktu sholat + indeks kolom adhan & iqama di CSV
        private static readonly (string Name, int AdhanCol, int IqamaCol)[] Prayers =
        {
            ("Subuh",   3,  4),
            ("Zuhur",   6,  7),
            ("Asar",    8,  9),
            ("Maghrib", 10, 11),
            ("Isya",    12, 13),
        };
        // Nama Arab sejajar dgn Prayers
        private static readonly string[] PrayerArabic =
            { "الفجر", "الظهر", "العصر", "المغرب", "العشاء" };
        // Nama Inggris sejajar dgn Prayers
        private static readonly string[] PrayerEnglish =
            { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };

        private bool _english;               // false = Bahasa Indonesia
        private Rectangle _langRect;         // area klik toggle bahasa
        private Rectangle _updateRect;       // tombol cek pembaruan
        private Rectangle _uninstallRect;    // tombol uninstall
        private Rectangle _contactRect;      // tautan kontak email

        private const string ContactEmail = "elsu@leeds.ac.uk";

        // Repo GitHub tempat rilis dipublikasikan (untuk fitur Cek Update).
        private const string GitHubRepo = "Santo-UoL/waktu-sholat-leeds";

        private string L(string id, string en) => _english ? en : id;
        private string PName(int i) => _english ? PrayerEnglish[i] : Prayers[i].Name;

        // ---- Palet Islami (hijau zamrud & emas) ----
        private readonly Color _cBgTop = Color.FromArgb(15, 64, 50);
        private readonly Color _cBgBot = Color.FromArgb(5, 28, 21);
        private readonly Color _cGold = Color.FromArgb(214, 178, 96);
        private readonly Color _cGoldLite = Color.FromArgb(243, 214, 140);
        private readonly Color _cCream = Color.FromArgb(240, 233, 214);
        private readonly Color _cRowNext = Color.FromArgb(36, 104, 82);
        private readonly Color _cRed = Color.FromArgb(224, 86, 72);

        // Data CSV: tanggal -> baris kolom
        private readonly Dictionary<DateOnly, string[]> _rows = new();

        // State tampilan (digambar di OnPaint)
        private string _clockText = "--:--:--";
        private string _dateText = "";
        private string _hijriText = "";
        private string _countdownText = "";
        private string _statusText = "";
        private bool _warning;
        private int _nextIdx = -1;
        private readonly string[] _rowTime = { "--:--", "--:--", "--:--", "--:--", "--:--" };
        private readonly string[] _rowIqama = { "", "", "", "", "" };
        private System.Windows.Forms.Timer _timer = null!;

        // System tray + overlay taskbar
        private NotifyIcon _tray = null!;
        private TaskbarOverlay _overlay = null!;
        private IntPtr _lastIconHandle = IntPtr.Zero;
        private bool _allowVisible;       // form mulai tersembunyi (hanya tray)
        private bool _exiting;            // true saat benar-benar keluar
        private bool _balloonShown;       // info "masih di tray" cukup sekali

        private bool _blinkOn;
        private string? _loadError;

        // Font UI
        private Font _fClock = null!, _fArTitle = null!, _fArName = null!, _fName = null!,
                     _fTime = null!, _fIqama = null!, _fDate = null!, _fHijri = null!,
                     _fSub = null!, _fCount = null!, _fStatus = null!, _fBtn = null!;

        public Form1()
        {
            InitializeComponent();
            BuildUi();
            LoadStartupSetting();
            BuildTray();
            ApplyStartup();          // default: ikut start saat Windows booting
            BuildOverlay();
            LoadCsv();
            StartTimer();
            ScheduleStartupUpdateCheck();
        }

        // Cek update otomatis sekali saat app dibuka (senyap; hanya bertanya
        // bila ada versi baru). Ditunda 8 detik agar jaringan siap dulu,
        // terutama saat app ikut start bersama Windows.
        private void ScheduleStartupUpdateCheck()
        {
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                t.Dispose();
                CheckUpdate(silent: true);
            };
            t.Start();
        }

        private void BuildUi()
        {
            ClientSize = new Size(460, 694);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = _cBgBot;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(icoPath)) Icon = new Icon(icoPath);
            }
            catch { /* abaikan */ }

            LoadLang();

            _fClock = new Font("Segoe UI Semibold", 33f);
            _fArTitle = new Font("Traditional Arabic", 30f, FontStyle.Bold);
            _fArName = new Font("Traditional Arabic", 26f, FontStyle.Bold);
            _fName = new Font("Segoe UI Semibold", 15f);
            _fTime = new Font("Segoe UI", 19f, FontStyle.Bold);
            _fIqama = new Font("Segoe UI", 8.5f);
            _fDate = new Font("Segoe UI", 10.5f);
            _fHijri = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            _fSub = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _fCount = new Font("Segoe UI", 13.5f, FontStyle.Bold);
            _fStatus = new Font("Segoe UI", 8.5f);
            _fBtn = new Font("Segoe UI", 9f, FontStyle.Bold);
        }

        // ------------------------------------------------------------------
        // Gambar tampilan (tema Islami)
        // ------------------------------------------------------------------
        private Bitmap? _bgCache;   // latar statis di-cache agar repaint ringan

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int W = ClientSize.Width, H = ClientSize.Height;

            if (_bgCache == null) BuildBgCache();
            if (_bgCache != null) g.DrawImageUnscaled(_bgCache, 0, 0);

            // subjudul latin (ikut bahasa)
            DrawCenter(g, L("WAKTU SHOLAT  ·  LEEDS", "PRAYER TIMES  ·  LEEDS"),
                _fSub, Color.FromArgb(175, _cCream), W / 2f, 100f);

            // toggle bahasa (pojok kanan atas)
            DrawLangToggle(g, W);

            // jam besar dengan bayangan halus
            var csz = g.MeasureString(_clockText, _fClock);
            using (var sh = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                g.DrawString(_clockText, _fClock, sh, W / 2f - csz.Width / 2f + 2f, 118f);
            DrawCenter(g, _clockText, _fClock, _cCream, W / 2f, 116f);

            // tanggal + hijriah
            DrawCenter(g, _dateText, _fDate, Color.FromArgb(210, _cCream), W / 2f, 162f);
            if (_hijriText.Length > 0)
                DrawCenter(g, _hijriText, _fHijri, _cGoldLite, W / 2f, 182f);

            // baris sholat
            int top = 224, rowH = 56, gap = 6, x = 26, w = W - 52;
            for (int i = 0; i < 5; i++)
                DrawPrayerRow(g, i, x, top + i * (rowH + gap), w, rowH);

            int after = top + 5 * (rowH + gap) + 6;
            DrawCountdown(g, W / 2f, after);
            DrawCenter(g, _statusText, _fStatus, Color.FromArgb(150, _cCream), W / 2f, after + 46);

            DrawFooterButtons(g, W, after + 74);

            // kontak email (bisa diklik)
            DrawContact(g, W, after + 108);
        }

        private void DrawContact(Graphics g, int W, float y)
        {
            // Tautan gaya hyperlink: hanya label, alamat email terbuka saat diklik.
            string txt = "✉  " + L("Hubungi kami", "Contact us");
            var sz = g.MeasureString(txt, _fStatus);
            float x = W / 2f - sz.Width / 2f;
            _contactRect = Rectangle.Round(new RectangleF(x - 4, y - 3, sz.Width + 8, sz.Height + 6));

            bool hover = _contactRect.Contains(PointToClient(MousePosition));
            Color link = hover ? Color.FromArgb(170, 215, 255) : Color.FromArgb(130, 185, 235);
            using var b = new SolidBrush(link);
            g.DrawString(txt, _fStatus, b, x, y);
            using var p = new Pen(link, 1f);
            g.DrawLine(p, x + 18, y + sz.Height - 2, x + sz.Width, y + sz.Height - 2);
        }

        // Latar statis: gradien, cahaya, pola girih, siluet masjid, mihrab,
        // bingkai, bulan sabit, judul arab, pembatas. Digambar sekali ke cache.
        private void BuildBgCache()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            if (W <= 0 || H <= 0) return;
            _bgCache?.Dispose();
            var bmp = new Bitmap(W, H);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // gradien zamrud dalam
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H), _cBgTop, _cBgBot, 90f))
                g.FillRectangle(bg, 0, 0, W, H);

            // cahaya lembut dari atas (seakan lampu mihrab)
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(W / 2f - 220, -150, 440, 330);
                using var pgb = new PathGradientBrush(glow)
                {
                    CenterColor = Color.FromArgb(36, 255, 235, 180),
                    SurroundColors = new[] { Color.FromArgb(0, 255, 235, 180) },
                };
                g.FillPath(pgb, glow);
            }

            DrawGirihPattern(g, W, H);      // tekstur geometri Islami
            DrawMosqueSilhouette(g, W, H);  // siluet masjid di kaki halaman

            // lengkung mihrab di belakang header
            DrawMihrabArch(g, W / 2f, 26f, 128f, 206f);

            // watermark khatam di belakang header
            using (var wp = new Pen(Color.FromArgb(20, _cGold), 1.4f))
            {
                DrawKhatam(g, W / 2f, 96f, 122f, wp);
                DrawKhatam(g, W / 2f, 96f, 92f, wp);
            }

            // bingkai ganda emas + ornamen sudut
            using (var fp = new Pen(_cGold, 2f))
                g.DrawRectangle(fp, 7, 7, W - 15, H - 15);
            using (var fp2 = new Pen(Color.FromArgb(90, _cGold), 1f))
                g.DrawRectangle(fp2, 12, 12, W - 25, H - 25);
            DrawCornerOrnaments(g, 7, 7, W - 15, H - 15);

            // bulan sabit + bintang
            DrawCrescent(g, W / 2f - 14, 44f, 16f, _cGoldLite);
            DrawStar5(g, W / 2f + 18, 36f, 8f, _cGoldLite);

            // judul arab (statis, tidak ikut bahasa)
            DrawCenter(g, "أوقات الصلاة", _fArTitle, _cGold, W / 2f, 56f);

            // pembatas berhias
            DrawDivider(g, 46, W - 46, 210f);

            _bgCache = bmp;
        }

        // Pola bintang-8 (khatam) tersusun selang-seling sebagai tekstur latar.
        private void DrawGirihPattern(Graphics g, int W, int H)
        {
            const int cell = 58;
            using var pen = new Pen(Color.FromArgb(9, 240, 218, 150), 1f);
            for (int row = -1; row * cell < H + cell; row++)
            {
                float offY = row * cell;
                float offX = (row % 2 == 0) ? 0 : cell / 2f;
                for (int col = -1; col * cell < W + cell; col++)
                {
                    float cx = col * cell + offX, cy = offY;
                    DrawKhatam(g, cx, cy, cell * 0.42f, pen);
                    g.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
                }
            }
        }

        // Lengkung mihrab (busur runcing) ganda menghiasi area header.
        private void DrawMihrabArch(Graphics g, float cx, float apexY, float halfW, float baseY)
        {
            for (int i = 0; i < 2; i++)
            {
                float inset = i * 7f;
                using var path = new GraphicsPath();
                path.AddBezier(cx - halfW + inset, baseY,
                               cx - halfW + inset, apexY + 60f,
                               cx - 58f, apexY + 8f + inset,
                               cx, apexY + inset);
                path.AddBezier(cx, apexY + inset,
                               cx + 58f, apexY + 8f + inset,
                               cx + halfW - inset, apexY + 60f,
                               cx + halfW - inset, baseY);
                using var pen = new Pen(Color.FromArgb(i == 0 ? 60 : 28, _cGold), i == 0 ? 1.6f : 1f);
                g.DrawPath(pen, path);
            }
            // permata kecil di puncak lengkung
            using var b = new SolidBrush(_cGold);
            g.FillPolygon(b, new[] {
                new PointF(cx, apexY - 7), new PointF(cx + 4.5f, apexY),
                new PointF(cx, apexY + 7), new PointF(cx - 4.5f, apexY) });
        }

        // Siluet masjid (kubah bawang & menara) sangat halus di bagian bawah.
        private void DrawMosqueSilhouette(Graphics g, int W, int H)
        {
            float cx = W / 2f, baseY = H - 16f;
            using var b = new SolidBrush(Color.FromArgb(22, 226, 196, 120));

            // dinding rendah
            g.FillRectangle(b, cx - 105, baseY - 18, 210, 18);

            // kubah utama (bentuk bawang)
            using (var dome = new GraphicsPath())
            {
                dome.AddBezier(cx - 38, baseY - 18, cx - 38, baseY - 58, cx - 10, baseY - 62, cx, baseY - 72);
                dome.AddBezier(cx, baseY - 72, cx + 10, baseY - 62, cx + 38, baseY - 58, cx + 38, baseY - 18);
                dome.CloseFigure();
                g.FillPath(b, dome);
            }
            g.FillEllipse(b, cx - 2f, baseY - 79, 4, 4);          // finial
            g.FillRectangle(b, cx - 0.8f, baseY - 84, 1.6f, 6);

            // kubah samping
            foreach (float sx in new[] { cx - 72, cx + 72 })
            {
                using var d2 = new GraphicsPath();
                d2.AddArc(sx - 18, baseY - 36, 36, 36, 180, 180);
                d2.CloseFigure();
                g.FillPath(b, d2);
            }

            // menara kiri & kanan
            foreach (float mx in new[] { cx - 128, cx + 128 })
            {
                g.FillRectangle(b, mx - 4, baseY - 62, 8, 62);
                g.FillRectangle(b, mx - 6, baseY - 40, 12, 4);    // balkon
                using var md = new GraphicsPath();
                md.AddArc(mx - 6, baseY - 72, 12, 12, 180, 180);
                md.CloseFigure();
                g.FillPath(b, md);
                g.FillRectangle(b, mx - 0.7f, baseY - 80, 1.4f, 8);
            }
        }

        private void DrawFooterButtons(Graphics g, int W, float y)
        {
            string up = L("Cek Update", "Check Update");
            string un = "Uninstall";
            float h = 26, padX = 16, gap = 14;
            float uw = g.MeasureString(up, _fBtn).Width + 22 + padX;   // +ikon ⟳
            float nw = g.MeasureString(un, _fBtn).Width + padX;
            float total = uw + gap + nw;
            float sx = (W - total) / 2f;

            var rU = new RectangleF(sx, y, uw, h);
            var rN = new RectangleF(sx + uw + gap, y, nw, h);
            _updateRect = Rectangle.Round(rU);
            _uninstallRect = Rectangle.Round(rN);

            DrawPillButton(g, rU, "⟳  " + up, _cGold, _cGoldLite);
            DrawPillButton(g, rN, un, _cRed, Color.FromArgb(240, 150, 140));
        }

        private void DrawPillButton(Graphics g, RectangleF r, string text, Color border, Color textColor)
        {
            using (var path = Round(r, r.Height / 2f))
            {
                using var f = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
                g.FillPath(f, path);
                using var p = new Pen(border, 1.4f);
                g.DrawPath(p, path);
            }
            var sz = g.MeasureString(text, _fBtn);
            using var tb = new SolidBrush(textColor);
            g.DrawString(text, _fBtn, tb, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
        }

        private void DrawPrayerRow(Graphics g, int i, int x, int y, int w, int h)
        {
            bool isNext = i == _nextIdx;
            var rect = new RectangleF(x, y, w, h);

            if (isNext)
            {
                // pendar emas lembut di sekeliling baris aktif
                int[] alphas = { 36, 20, 9 };
                for (int k = 0; k < 3; k++)
                {
                    float inf = 2f + k * 2.6f;
                    using var gp = Round(RectangleF.Inflate(rect, inf, inf), 14f + inf);
                    using var pen = new Pen(Color.FromArgb(alphas[k], _cGoldLite), 2.2f);
                    g.DrawPath(pen, gp);
                }
            }

            using (var path = Round(rect, 14f))
            {
                if (isNext)
                {
                    using var f = new LinearGradientBrush(rect,
                        Color.FromArgb(255, 46, 124, 98), Color.FromArgb(255, 26, 84, 66), 90f);
                    g.FillPath(f, path);
                    using var p = new Pen(_cGold, 1.6f);
                    g.DrawPath(p, path);
                }
                else
                {
                    using var f = new LinearGradientBrush(rect,
                        Color.FromArgb(28, 255, 255, 255), Color.FromArgb(9, 255, 255, 255), 90f);
                    g.FillPath(f, path);
                    using var p = new Pen(Color.FromArgb(45, _cGold), 1f);
                    g.DrawPath(p, path);
                }
            }

            if (isNext)
            {
                using var ab = new SolidBrush(_cGold);
                using var bar = Round(new RectangleF(x + 7, y + 12, 4, h - 24), 2f);
                g.FillPath(ab, bar);
            }

            Color nameCol = isNext ? _cGoldLite : _cCream;
            Color arCol = isNext ? _cGoldLite : _cGold;

            bool hasIq = _rowIqama[i].Length > 0;
            using (var nb = new SolidBrush(nameCol))
            {
                if (hasIq)
                {
                    g.DrawString(PName(i), _fName, nb, x + 22, y + 7);
                    using var ib = new SolidBrush(Color.FromArgb(180, _cCream));
                    g.DrawString(_rowIqama[i], _fIqama, ib, x + 23, y + h - 22);
                }
                else
                {
                    var ns = g.MeasureString(PName(i), _fName);
                    g.DrawString(PName(i), _fName, nb, x + 22, y + (h - ns.Height) / 2f);
                }
            }

            using (var tb = new SolidBrush(nameCol))
            {
                var ts = g.MeasureString(_rowTime[i], _fTime);
                g.DrawString(_rowTime[i], _fTime, tb, x + w - 20 - ts.Width, y + (h - ts.Height) / 2f);
            }

            using (var arb = new SolidBrush(arCol))
            {
                var asz = g.MeasureString(PrayerArabic[i], _fArName);
                g.DrawString(PrayerArabic[i], _fArName, arb, x + w - 106 - asz.Width, y + (h - asz.Height) / 2f);
            }
        }

        private void DrawCountdown(Graphics g, float cx, float topY)
        {
            var sz = g.MeasureString(_countdownText, _fCount);
            float pw = sz.Width + 58, ph = 40f;
            var rect = new RectangleF(cx - pw / 2f, topY, pw, ph);
            bool alert = _warning && _blinkOn;

            // pendar di belakang pil
            int[] alphas = { 30, 16, 7 };
            for (int k = 0; k < 3; k++)
            {
                float inf = 2f + k * 2.6f;
                using var gp = Round(RectangleF.Inflate(rect, inf, inf), (ph + inf * 2) / 2f);
                using var pen = new Pen(Color.FromArgb(alphas[k], alert ? _cRed : _cGold), 2f);
                g.DrawPath(pen, gp);
            }

            using (var path = Round(rect, ph / 2f))
            {
                if (alert)
                {
                    using var f = new SolidBrush(_cRed);
                    g.FillPath(f, path);
                }
                else
                {
                    using var f = new LinearGradientBrush(rect,
                        Color.FromArgb(46, _cGold), Color.FromArgb(16, _cGold), 90f);
                    g.FillPath(f, path);
                }
                using var p = new Pen(alert ? _cGoldLite : _cGold, 1.6f);
                g.DrawPath(p, path);
            }

            // bintang kecil di kiri-kanan teks
            DrawStar5(g, rect.Left + 17, topY + ph / 2f, 4.5f, alert ? Color.White : Color.FromArgb(200, _cGold));
            DrawStar5(g, rect.Right - 17, topY + ph / 2f, 4.5f, alert ? Color.White : Color.FromArgb(200, _cGold));

            Color tc = alert ? Color.White : _cGoldLite;
            using var tb = new SolidBrush(tc);
            g.DrawString(_countdownText, _fCount, tb, cx - sz.Width / 2f, topY + (ph - sz.Height) / 2f);
        }

        private void DrawLangToggle(Graphics g, int W)
        {
            int lw = 54, lh = 21, lx = W - 16 - lw, ly = 15;
            _langRect = new Rectangle(lx, ly, lw, lh);

            using (var path = Round(new RectangleF(lx, ly, lw, lh), lh / 2f))
            {
                using var bb = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
                g.FillPath(bb, path);
                using var bp = new Pen(_cGold, 1.2f);
                g.DrawPath(bp, path);
            }

            float half = lw / 2f;
            var chip = _english
                ? new RectangleF(lx + half + 1, ly + 2, half - 3, lh - 4)
                : new RectangleF(lx + 2, ly + 2, half - 3, lh - 4);
            using (var hp = Round(chip, (lh - 4) / 2f))
            using (var hb = new SolidBrush(_cGold))
                g.FillPath(hb, hp);

            using var idb = new SolidBrush(_english ? Color.FromArgb(150, _cCream) : Color.FromArgb(11, 50, 39));
            using var enb = new SolidBrush(_english ? Color.FromArgb(11, 50, 39) : Color.FromArgb(150, _cCream));
            DrawCenteredIn(g, "ID", _fSub, idb, new RectangleF(lx, ly, half, lh));
            DrawCenteredIn(g, "EN", _fSub, enb, new RectangleF(lx + half, ly, half, lh));
        }

        private void DrawCenteredIn(Graphics g, string text, Font font, Brush b, RectangleF r)
        {
            var sz = g.MeasureString(text, font);
            g.DrawString(text, font, b, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
        }

        // ---- Ornamen Islami ----
        private void DrawCenter(Graphics g, string text, Font font, Color color, float cx, float topY)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sz = g.MeasureString(text, font);
            using var br = new SolidBrush(color);
            g.DrawString(text, font, br, cx - sz.Width / 2f, topY);
        }

        private void DrawDivider(Graphics g, float x1, float x2, float y)
        {
            float cx = (x1 + x2) / 2f;
            using var p = new Pen(Color.FromArgb(120, _cGold), 1f);
            g.DrawLine(p, x1, y, cx - 18, y);
            g.DrawLine(p, cx + 18, y, x2, y);

            // khatam kecil di tengah dengan inti emas
            using (var kp = new Pen(Color.FromArgb(210, _cGold), 1.2f))
                DrawKhatam(g, cx, y, 9f, kp);
            using var b = new SolidBrush(_cGoldLite);
            g.FillEllipse(b, cx - 2.4f, y - 2.4f, 4.8f, 4.8f);
            g.FillEllipse(b, x1 - 2, y - 2, 4, 4);
            g.FillEllipse(b, x2 - 2, y - 2, 4, 4);

            // titik aksen bertingkat di kiri-kanan khatam
            using var b2 = new SolidBrush(Color.FromArgb(160, _cGold));
            g.FillEllipse(b2, cx - 30 - 1.6f, y - 1.6f, 3.2f, 3.2f);
            g.FillEllipse(b2, cx + 30 - 1.6f, y - 1.6f, 3.2f, 3.2f);
        }

        private void DrawCrescent(Graphics g, float cx, float cy, float r, Color color)
        {
            using var path = new GraphicsPath { FillMode = FillMode.Alternate };
            path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
            path.AddEllipse(cx - r + r * 0.62f, cy - r * 0.92f, 2 * r * 0.92f, 2 * r * 0.92f);
            using var b = new SolidBrush(color);
            g.FillPath(b, path);
        }

        private void DrawStar5(Graphics g, float cx, float cy, float r, Color color)
        {
            var pts = new PointF[10];
            for (int k = 0; k < 10; k++)
            {
                double ang = -Math.PI / 2 + k * Math.PI / 5;
                float rad = (k % 2 == 0) ? r : r * 0.42f;
                pts[k] = new PointF(cx + (float)(Math.Cos(ang) * rad), cy + (float)(Math.Sin(ang) * rad));
            }
            using var b = new SolidBrush(color);
            g.FillPolygon(b, pts);
        }

        private static void DrawKhatam(Graphics g, float cx, float cy, float r, Pen pen)
        {
            var s1 = new PointF[4];
            var s2 = new PointF[4];
            for (int k = 0; k < 4; k++)
            {
                double a1 = Math.PI / 4 + k * Math.PI / 2;
                double a2 = k * Math.PI / 2;
                s1[k] = new PointF(cx + (float)(Math.Cos(a1) * r), cy + (float)(Math.Sin(a1) * r));
                s2[k] = new PointF(cx + (float)(Math.Cos(a2) * r), cy + (float)(Math.Sin(a2) * r));
            }
            g.DrawPolygon(pen, s1);
            g.DrawPolygon(pen, s2);
        }

        // Ornamen sudut: berlian emas + lengan garis dengan titik di ujungnya.
        private void DrawCornerOrnaments(Graphics g, int x, int y, int w, int h)
        {
            using var b = new SolidBrush(_cGold);
            using var pen = new Pen(Color.FromArgb(170, _cGold), 1.2f);
            void Corner(float ccx, float ccy, int sx, int sy)
            {
                g.FillPolygon(b, new[] {
                    new PointF(ccx, ccy - 6), new PointF(ccx + 6, ccy),
                    new PointF(ccx, ccy + 6), new PointF(ccx - 6, ccy) });
                g.DrawLine(pen, ccx + sx * 9, ccy, ccx + sx * 30, ccy);
                g.DrawLine(pen, ccx, ccy + sy * 9, ccx, ccy + sy * 30);
                g.FillEllipse(b, ccx + sx * 32 - 2, ccy - 2, 4, 4);
                g.FillEllipse(b, ccx - 2, ccy + sy * 32 - 2, 4, 4);
            }
            Corner(x, y, 1, 1); Corner(x + w, y, -1, 1);
            Corner(x, y + h, 1, -1); Corner(x + w, y + h, -1, -1);
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            float d = rad * 2f;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------------
        // Klik (toggle bahasa + tombol footer)
        // ------------------------------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_langRect.Contains(e.Location))
            {
                bool en = e.X > _langRect.X + _langRect.Width / 2; // kanan = EN, kiri = ID
                if (en != _english)
                {
                    _english = en;
                    SaveLang();
                    Tick(); // segarkan + repaint
                }
                return;
            }
            if (_updateRect.Contains(e.Location)) { CheckUpdate(); return; }
            if (_uninstallRect.Contains(e.Location)) { Uninstall(); return; }
            if (_contactRect.Contains(e.Location))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("mailto:" + ContactEmail)
                    { UseShellExecute = true });
                }
                catch { /* tidak ada mail client — abaikan */ }
                return;
            }
        }

        // Kursor tangan di atas area yang bisa diklik (tautan & tombol).
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool clickable = _contactRect.Contains(e.Location)
                || _updateRect.Contains(e.Location)
                || _uninstallRect.Contains(e.Location)
                || _langRect.Contains(e.Location);
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
        }

        // ------------------------------------------------------------------
        // System tray
        // ------------------------------------------------------------------
        private void BuildTray()
        {
            ShowInTaskbar = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Buka", null, (_, _) => ShowForm());
            menu.Items.Add(new ToolStripSeparator());

            var startupItem = new ToolStripMenuItem("Mulai saat Windows dinyalakan")
            {
                Checked = _startupEnabled,
                CheckOnClick = true,
            };
            startupItem.CheckedChanged += (_, _) =>
            {
                _startupEnabled = startupItem.Checked;
                SaveStartupSetting();
                ApplyStartup();
            };
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Cek Pembaruan", null, (_, _) => CheckUpdate());
            menu.Items.Add("Uninstall", null, (_, _) => Uninstall());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Keluar", null, (_, _) => ExitApp());

            _tray = new NotifyIcon
            {
                Text = "Waktu Sholat Leeds",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = SystemIcons.Application,   // sementara, ditimpa Tick()
            };
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowForm();
            };
            _tray.DoubleClick += (_, _) => ShowForm();
        }

        private void ShowForm()
        {
            _allowVisible = true;
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.BalloonTipTitle = "Waktu Sholat Leeds";
                _tray.BalloonTipText = "Aplikasi tetap berjalan di tray. Klik ikon untuk membuka.";
                _tray.ShowBalloonTip(2000);
            }
        }

        private void ExitApp()
        {
            _exiting = true;
            _tray.Visible = false;
            if (_lastIconHandle != IntPtr.Zero) DestroyIcon(_lastIconHandle);
            _offsetTimer?.Dispose();
            _overlay?.Close();
            Application.Exit();
        }

        // ------------------------------------------------------------------
        // Overlay taskbar (dekat widget cuaca)
        // ------------------------------------------------------------------
        private void BuildOverlay()
        {
            _overlay = new TaskbarOverlay();
            _overlay.Clicked += ShowForm;
            _overlay.Show();

            // Hitung jarak adaptif di thread latar tiap 2 detik (UIA lambat,
            // jangan di UI thread). Mengikuti tepi kanan widget cuaca.
            _offsetTimer = new System.Threading.Timer(_ => ComputeAdaptiveOffset(),
                null, dueTime: 300, period: 2000);
        }

        private static System.Windows.Rect? FindTaskbarRect(
            System.Windows.Automation.AutomationElement taskbar, string automationId)
        {
            var cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.AutomationIdProperty, automationId);
            var el = taskbar.FindFirst(System.Windows.Automation.TreeScope.Descendants, cond);
            if (el == null) return null;
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty || double.IsInfinity(r.X)) return null;
            return r;
        }

        private void ComputeAdaptiveOffset()
        {
            try
            {
                var h = FindWindow("Shell_TrayWnd", null);
                if (h == IntPtr.Zero || !GetWindowRect(h, out var tr)) return;
                int tbLeft = tr.Left;

                var tb = System.Windows.Automation.AutomationElement.FromHandle(h);
                if (tb == null) return;

                var widget = FindTaskbarRect(tb, "WidgetsButton");
                if (widget == null) return;

                int desired = (int)widget.Value.Right - tbLeft + 16;

                var start = FindTaskbarRect(tb, "StartButton");
                if (start != null)
                {
                    int maxOffset = (int)start.Value.Left - tbLeft - 10 - _lastOverlayWidth;
                    if (maxOffset > 4 && desired > maxOffset) desired = maxOffset;
                }

                _adaptiveOffset = Math.Max(4, desired);
            }
            catch { /* UIA kadang gagal sesaat; pakai nilai terakhir */ }
        }

        private void PositionOverlay()
        {
            Rectangle tb;
            var h = FindWindow("Shell_TrayWnd", null);
            if (h != IntPtr.Zero && GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top)
            {
                tb = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
            else
            {
                var b = Screen.PrimaryScreen!.Bounds;
                var wa = Screen.PrimaryScreen.WorkingArea;
                int top = wa.Bottom < b.Bottom ? wa.Bottom : b.Bottom - 40;
                tb = new Rectangle(b.Left, top, b.Width, b.Bottom - top);
            }
            _lastOverlayWidth = _overlay.Width;
            _overlay.Reposition(tb, _adaptiveOffset);
            _overlay.EnsureTopmost();

            if (_colorSampleCounter-- <= 0)
            {
                _colorSampleCounter = 10; // 10 x 500ms = 5 detik
                try
                {
                    int sx = tb.Right - 2;
                    int sy = tb.Top + tb.Height / 2;
                    using var bmp = new Bitmap(1, 1);
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(sx, sy, 0, 0, new Size(1, 1));
                    _taskbarColor = bmp.GetPixel(0, 0);
                }
                catch { /* abaikan, pakai nilai terakhir */ }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                int caption = Bgr(Color.FromArgb(11, 50, 39));   // hijau gelap caption
                DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, 4);
                int text = Bgr(_cGoldLite);                       // teks judul emas
                DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref text, 4);
                int border = Bgr(_cGold);                         // garis tepi emas
                DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, 4);
            }
            catch { /* DWM tidak tersedia (Windows lama) — abaikan */ }
        }

        // ------------------------------------------------------------------
        // Cek pembaruan online
        // ------------------------------------------------------------------
        private bool _checkingUpdate;

        // silent=true: dipakai saat startup — hanya bertanya bila ADA versi baru;
        // tidak menampilkan pesan "sudah terbaru" atau error jaringan.
        private async void CheckUpdate(bool silent = false)
        {
            if (_checkingUpdate) return;   // cegah klik ganda
            _checkingUpdate = true;
            UseWaitCursor = !silent;
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WaktuSholatLeeds-Updater");

                // Ambil rilis terbaru dari GitHub
                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                var latest = ParseVersion(tag);
                if (latest == null)
                {
                    if (!silent)
                        MessageBox.Show(L("Tidak bisa membaca versi rilis dari GitHub.",
                                          "Could not read release version from GitHub."),
                            "Waktu Sholat Leeds", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Cari asset .msi pada rilis
                string? msiUrl = null, msiName = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var n = a.GetProperty("name").GetString() ?? "";
                        if (n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            msiUrl = a.GetProperty("browser_download_url").GetString();
                            msiName = n;
                            break;
                        }
                    }
                }
                string releasePage = root.TryGetProperty("html_url", out var hu)
                    ? hu.GetString() ?? $"https://github.com/{GitHubRepo}/releases"
                    : $"https://github.com/{GitHubRepo}/releases";

                if (Norm(latest) <= Norm(current))
                {
                    if (!silent)
                        MessageBox.Show(
                            L($"Sudah versi terbaru ({current.ToString(3)}).",
                              $"You're up to date ({current.ToString(3)})."),
                            "Waktu Sholat Leeds", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Ada versi baru -> tanya dulu
                var ask = MessageBox.Show(
                    L($"Versi baru tersedia di GitHub!\n\nVersi sekarang : {current.ToString(3)}\nVersi terbaru  : {latest.ToString(3)}\n\nUpdate sekarang? Installer akan diunduh, lalu aplikasi ini menutup diri dan installer berjalan.",
                      $"A new version is available on GitHub!\n\nCurrent version : {current.ToString(3)}\nLatest version  : {latest.ToString(3)}\n\nUpdate now? The installer will be downloaded, then this app will close and the installer will run."),
                    "Waktu Sholat Leeds", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask != DialogResult.Yes) return;

                if (msiUrl == null)
                {
                    // Rilis tanpa MSI -> buka halaman rilis di browser
                    Process.Start(new ProcessStartInfo(releasePage) { UseShellExecute = true });
                    return;
                }

                // Unduh MSI ke folder temp (tampilkan balon agar user tahu)
                _tray.BalloonTipTitle = "Waktu Sholat Leeds";
                _tray.BalloonTipText = L("Mengunduh pembaruan… app akan menutup sendiri saat siap.",
                                         "Downloading update… the app will close itself when ready.");
                _tray.ShowBalloonTip(3000);

                string dest = Path.Combine(Path.GetTempPath(), msiName!);
                var bytes = await http.GetByteArrayAsync(msiUrl);
                await File.WriteAllBytesAsync(dest, bytes);

                // Jalankan installer lalu tutup app agar file bisa diganti
                Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{dest}\"",
                    UseShellExecute = true,
                });
                ExitApp();
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
            {
                if (!silent)
                    MessageBox.Show(L("Belum ada rilis di GitHub.", "No releases found on GitHub."),
                        "Waktu Sholat Leeds", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show(
                        L("Gagal mengecek pembaruan:\n", "Failed to check for updates:\n") + ex.Message,
                        "Waktu Sholat Leeds", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                UseWaitCursor = false;
                _checkingUpdate = false;
            }
        }

        // Samakan jumlah komponen versi agar 1.0.0 == 1.0.0.0 saat dibandingkan.
        private static Version Norm(Version v) =>
            new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        private static Version? ParseVersion(string s)
        {
            s = s.Trim().TrimStart('v', 'V').Trim();
            var tok = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) return null;
            return Version.TryParse(tok[0], out var v) ? v : null;
        }

        // ------------------------------------------------------------------
        // Uninstall — hapus app sepenuhnya
        // ------------------------------------------------------------------
        private void Uninstall()
        {
            var confirm = MessageBox.Show(
                L("Hapus Waktu Sholat Leeds sepenuhnya?\n\nApp akan dihentikan, dihapus dari startup Windows, dan seluruh folder programnya dihapus. Tindakan ini permanen.",
                  "Completely remove Waktu Sholat Leeds?\n\nThe app will stop, be removed from Windows startup, and its entire program folder will be deleted. This is permanent."),
                "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                run?.DeleteValue(RunKeyName, throwOnMissingValue: false);
            }
            catch { /* abaikan */ }

            // Hapus folder pengaturan di %APPDATA%
            try { if (Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, true); }
            catch { /* abaikan */ }

            try
            {
                string dir = AppContext.BaseDirectory.TrimEnd('\\');
                string bat = Path.Combine(Path.GetTempPath(), "uninstall_wsl.bat");
                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    "timeout /t 3 /nobreak >nul\r\n" +
                    $"rmdir /s /q \"{dir}\"\r\n" +
                    "del \"%~f0\"\r\n");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{bat}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("Gagal menjalankan uninstall:\n", "Failed to run uninstall:\n") + ex.Message,
                    "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ExitApp();
        }

        // ------------------------------------------------------------------
        // Pengaturan (disimpan di %APPDATA%\WaktuSholatLeeds agar bisa ditulis
        // tanpa admin walau app ter-install di Program Files)
        // ------------------------------------------------------------------
        private static string SettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WaktuSholatLeeds");
        private static string LangPath => Path.Combine(SettingsDir, "lang.txt");
        private static string StartupSettingPath => Path.Combine(SettingsDir, "startup.txt");

        private void LoadLang()
        {
            try
            {
                if (File.Exists(LangPath))
                    _english = File.ReadAllText(LangPath).Trim() == "en";
                else
                {
                    // migrasi dari lokasi lama (folder app)
                    var old = Path.Combine(AppContext.BaseDirectory, "lang.txt");
                    if (File.Exists(old)) _english = File.ReadAllText(old).Trim() == "en";
                }
            }
            catch { /* abaikan */ }
        }
        private void SaveLang()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(LangPath, _english ? "en" : "id");
            }
            catch { /* abaikan */ }
        }

        // ---- Mulai bersama Windows (default: aktif) ----
        private const string RunKeyName = "WaktuSholatLeeds";
        private bool _startupEnabled = true;

        private void LoadStartupSetting()
        {
            try
            {
                if (File.Exists(StartupSettingPath))
                    _startupEnabled = File.ReadAllText(StartupSettingPath).Trim() != "0";
            }
            catch { /* abaikan */ }
        }
        private void SaveStartupSetting()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(StartupSettingPath, _startupEnabled ? "1" : "0");
            }
            catch { /* abaikan */ }
        }

        // Tulis/hapus entri autostart di registry sesuai pengaturan.
        // Path selalu diperbarui ke exe yang sedang berjalan.
        private void ApplyStartup()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (run == null) return;
                if (_startupEnabled)
                    run.SetValue(RunKeyName, $"\"{Application.ExecutablePath}\"");
                else
                    run.DeleteValue(RunKeyName, throwOnMissingValue: false);
            }
            catch { /* abaikan */ }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        // Gambar ikon tray dinamis: lingkaran berisi sisa waktu menuju sholat.
        private void UpdateTrayIcon(string text, bool warning)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);
                var back = warning ? Color.FromArgb(220, 40, 40) : Color.FromArgb(33, 96, 76);
                using (var b = new SolidBrush(back))
                    g.FillEllipse(b, 0, 0, 31, 31);
                using (var pen = new Pen(Color.FromArgb(235, 214, 178, 96), 1.6f))
                    g.DrawEllipse(pen, 1, 1, 29, 29);

                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                float px = text.Length >= 3 ? 13f : 18f;
                using var f = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString(text, f, Brushes.White, new RectangleF(0, 0, 32, 33), sf);
            }

            IntPtr hIcon = bmp.GetHicon();
            var newIcon = (Icon)Icon.FromHandle(hIcon).Clone();
            var oldIcon = _tray.Icon;
            _tray.Icon = newIcon;

            if (oldIcon != null && oldIcon != SystemIcons.Application) oldIcon.Dispose();
            if (_lastIconHandle != IntPtr.Zero) DestroyIcon(_lastIconHandle);
            _lastIconHandle = hIcon;
        }

        // ------------------------------------------------------------------
        // Pemuatan CSV
        // ------------------------------------------------------------------
        private void LoadCsv()
        {
            string path = File.Exists(CsvPath)
                ? CsvPath
                : Path.Combine(AppContext.BaseDirectory, "LGM_Prayer_Calendar_2026.csv");

            try
            {
                if (!File.Exists(path))
                {
                    _loadError = $"File CSV tidak ditemukan:\n{CsvPath}";
                    return;
                }

                var lines = File.ReadAllLines(path);
                for (int i = 1; i < lines.Length; i++) // lewati header
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    var cols = line.Split(',');
                    if (cols.Length < 14) continue;
                    if (DateOnly.TryParseExact(cols[0], "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        _rows[d] = cols;
                    }
                }

                if (_rows.Count == 0)
                    _loadError = "CSV terbaca tapi tidak ada data tanggal yang valid.";
            }
            catch (Exception ex)
            {
                _loadError = "Gagal membaca CSV:\n" + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        // Timer
        // ------------------------------------------------------------------
        private void StartTimer()
        {
            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();
        }

        private void Tick()
        {
            var now = DateTime.Now;
            _clockText = now.ToString("HH:mm:ss");
            _dateText = now.ToString("dddd, dd MMMM yyyy", new CultureInfo(_english ? "en-GB" : "id-ID"));

            if (_loadError != null)
            {
                _nextIdx = -1;
                _hijriText = "";
                _countdownText = "CSV error";
                _statusText = _loadError.Replace("\n", "  ");
                _warning = true;
                _blinkOn = !_blinkOn;
                SetTray("Waktu Sholat Leeds — CSV error", "!", true);
                _overlay.SetContent("Sholat: CSV error", true, _taskbarColor);
                PositionOverlay();
                Invalidate();
                return;
            }

            var today = DateOnly.FromDateTime(now);
            if (!_rows.TryGetValue(today, out var row))
            {
                _nextIdx = -1;
                _hijriText = "";
                for (int i = 0; i < 5; i++) { _rowTime[i] = "--:--"; _rowIqama[i] = ""; }
                _countdownText = L("Tidak ada jadwal hari ini", "No schedule for today");
                _statusText = L($"Tanggal {today:yyyy-MM-dd} tidak ada di CSV", $"Date {today:yyyy-MM-dd} not in CSV");
                _warning = false;
                _blinkOn = false;
                SetTray("Waktu Sholat Leeds — " + L("tidak ada jadwal hari ini", "no schedule today"), "–", false);
                _overlay.SetContent(L("Tidak ada jadwal", "No schedule"), false, _taskbarColor);
                PositionOverlay();
                Invalidate();
                return;
            }

            _hijriText = row[2] + L(" H", " AH");

            var adhan = new DateTime?[5];
            for (int i = 0; i < 5; i++)
            {
                var p = Prayers[i];
                if (TryParseTime(today, row[p.AdhanCol], out var a))
                {
                    adhan[i] = a;
                    _rowTime[i] = a.ToString("HH:mm");
                    string iq = p.IqamaCol < row.Length ? row[p.IqamaCol].Trim() : "";
                    bool validIq = TimeOnly.TryParseExact(iq, "HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                    // Jam -> "Iqamah HH:mm"; teks lain (mis. "COMBINED WITH MAGHRIB")
                    // ditampilkan apa adanya; kosong -> tidak ada keterangan.
                    _rowIqama[i] = validIq ? L("Iqamah ", "Iqama ") + iq : iq;
                }
                else { adhan[i] = null; _rowTime[i] = "--:--"; _rowIqama[i] = ""; }
            }

            int nextIdx = -1;
            for (int i = 0; i < 5; i++)
                if (adhan[i].HasValue && adhan[i]!.Value > now) { nextIdx = i; break; }
            _nextIdx = nextIdx;

            DateTime nextAdhan;
            string nextName;
            if (nextIdx >= 0)
            {
                nextAdhan = adhan[nextIdx]!.Value;
                nextName = PName(nextIdx);
            }
            else
            {
                var tomorrow = today.AddDays(1);
                nextName = PName(0);
                if (_rows.TryGetValue(tomorrow, out var tRow) &&
                    TryParseTime(tomorrow, tRow[Prayers[0].AdhanCol], out var fajr))
                    nextAdhan = fajr;
                else
                    nextAdhan = now.Date.AddDays(1);
            }

            var remaining = nextAdhan - now;
            _warning = remaining.TotalMinutes <= WarningMinutes && remaining.TotalSeconds > 0;

            string rem = remaining.TotalSeconds > 0
                ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : "00:00:00";
            _countdownText = L($"Menuju {nextName}   {rem}", $"Next {nextName}   {rem}");
            _statusText = _warning
                ? L($"  {WarningMinutes} menit menjelang waktu sholat  ", $"  {WarningMinutes} minutes to prayer time  ")
                : L("Klik ikon taskbar untuk buka · minimize untuk sembunyi",
                    "Click taskbar icon to open · minimize to hide");

            _blinkOn = _warning ? !_blinkOn : false;

            int totalMin = Math.Max(0, (int)remaining.TotalMinutes);
            string iconText = totalMin < 60 ? totalMin.ToString() : (totalMin / 60) + L("j", "h");
            SetTray($"{nextName} {nextAdhan:HH:mm} · {HumanRemaining(remaining)} {L("lagi", "left")}", iconText, _warning);

            string overlayRem = totalMin < 60 ? $"{totalMin}m" : $"{totalMin / 60}{L("j", "h")}{(totalMin % 60):00}";
            _overlay.SetContent($"{nextName} {overlayRem}", _warning, _taskbarColor);
            PositionOverlay();
            Invalidate();
        }

        private string _lastIconKey = "";

        private void SetTray(string tooltip, string iconText, bool warning)
        {
            if (tooltip.Length > 63) tooltip = tooltip[..63];
            _tray.Text = tooltip;

            string key = iconText + "|" + warning;
            if (key == _lastIconKey) return;
            _lastIconKey = key;
            UpdateTrayIcon(iconText, warning);
        }

        private string HumanRemaining(TimeSpan t)
        {
            if (t.TotalSeconds <= 0) return L("sekarang", "now");
            int h = (int)t.TotalHours, m = t.Minutes;
            if (_english) return h > 0 ? $"{h}h {m}m" : $"{m}m";
            return h > 0 ? $"{h} jam {m} mnt" : $"{m} mnt";
        }

        private static bool TryParseTime(DateOnly date, string hhmm, out DateTime result)
        {
            result = default;
            if (TimeOnly.TryParseExact(hhmm.Trim(), "HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                result = date.ToDateTime(t);
                return true;
            }
            return false;
        }
    }
}
