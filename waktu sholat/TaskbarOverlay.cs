using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace waktu_sholat
{
    // Jendela kecil melayang di atas taskbar (dekat widget cuaca) yang
    // menampilkan sholat berikutnya + sisa waktu. Diklik -> buka form utama.
    //
    // Memakai layered window (UpdateLayeredWindow) + alpha per-piksel agar
    // sudut membulat halus / anti-alias dan menyatu mulus dengan taskbar.
    public sealed class TaskbarOverlay : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
            int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }
        private const int ULW_ALPHA = 0x02;
        private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;

        public event Action? Clicked;

        private string _text = "";
        private bool _warning;
        private Color _taskbarBg = Color.FromArgb(243, 243, 243);
        private readonly Font _font = new("Segoe UI", 9.5f, FontStyle.Bold);

        public TaskbarOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;
            Height = 30;
            Width = 130;
            Location = new Point(-3000, -3000); // sembunyi sampai diposisikan
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                return cp;
            }
        }

        public void SetContent(string text, bool warning, Color taskbarBg)
        {
            if (text == _text && warning == _warning && taskbarBg == _taskbarBg) return;
            _text = text;
            _warning = warning;
            _taskbarBg = taskbarBg;

            using (var g = CreateGraphics())
            {
                var sz = g.MeasureString(text, _font);
                Width = Math.Max(70, (int)sz.Width + 36); // ruang dot kiri + padding
            }
            Render();
        }

        // Gambar pil ke bitmap ARGB anti-alias, premultiply, lalu dorong ke layered window.
        private void Render()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                // Normal: latar nyaris transparan (alpha 3) -> taskbar asli tembus
                // sehingga menyatu sempurna, tapi tetap bisa diklik (alpha > 0).
                // Warning: pil merah penuh sebagai alert.
                Color bg = _warning ? Color.FromArgb(210, 45, 45) : Color.FromArgb(3, 0, 0, 0);
                using (var path = RoundedRect(0, 0, Width, Height, Height / 2f))
                using (var b = new SolidBrush(bg))
                    g.FillPath(b, path);

                // Warna teks kontras dengan latar.
                bool lightBg = !_warning && Luminance(_taskbarBg) > 150;
                Color textColor = lightBg ? Color.FromArgb(28, 28, 30) : Color.White;
                Color dotColor = _warning
                    ? Color.FromArgb(255, 210, 210)
                    : (lightBg ? Color.FromArgb(45, 110, 180) : Color.FromArgb(150, 210, 255));

                using (var db = new SolidBrush(dotColor))
                    g.FillEllipse(db, 13f, Height / 2f - 4f, 8f, 8f);

                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                using var tb = new SolidBrush(textColor);
                g.DrawString(_text, _font, tb, new RectangleF(27, 0, Width - 31, Height), sf);
            }

            Premultiply(bmp);
            PushLayered(bmp);
        }

        private static double Luminance(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2f);
            float d = r * 2f;
            var p = new GraphicsPath();
            p.AddArc(x, y, d, d, 90, 180);
            p.AddArc(x + w - d, y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        private static void Premultiply(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int n = data.Stride * data.Height;
            var buf = new byte[n];
            Marshal.Copy(data.Scan0, buf, 0, n);
            for (int i = 0; i < n; i += 4) // B,G,R,A
            {
                byte a = buf[i + 3];
                if (a == 255) continue;
                buf[i] = (byte)(buf[i] * a / 255);
                buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                buf[i + 2] = (byte)(buf[i + 2] * a / 255);
            }
            Marshal.Copy(buf, 0, data.Scan0, n);
            bmp.UnlockBits(data);
        }

        private void PushLayered(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBmp);
            try
            {
                var size = new SIZE { CX = Width, CY = Height };
                var src = new POINT { X = 0, Y = 0 };
                var dst = new POINT { X = Left, Y = Top };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e) => Clicked?.Invoke();

        // Posisikan di dalam taskbar pada offset dari kiri (dekat widget cuaca).
        public void Reposition(Rectangle taskbar, int leftOffset)
        {
            int y = taskbar.Top + (taskbar.Height - Height) / 2;
            int x = taskbar.Left + leftOffset;
            if (Location.X != x || Location.Y != y)
            {
                Location = new Point(x, y);
                Render(); // layered window perlu di-push ulang pada posisi baru
            }
        }

        // Pastikan tetap di atas taskbar (yang juga topmost).
        public void EnsureTopmost()
        {
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
