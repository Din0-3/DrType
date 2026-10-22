using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DrType
{
    // 取得目前文字游標的螢幕位置：先用 Win32 caret，不行再問 MSAA（Chrome、Edge、Word 適用），最後退回滑鼠位置。
    public static class CaretLocator
    {
        public static Rectangle Locate()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint pid;
            uint tid = NativeMethods.GetWindowThreadProcessId(fg, out pid);
            var gti = new NativeMethods.GUITHREADINFO();
            gti.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(gti);
            if (NativeMethods.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero)
            {
                var r = gti.rcCaret;
                var p = new NativeMethods.POINT { X = r.Left, Y = r.Top };
                if (NativeMethods.ClientToScreen(gti.hwndCaret, ref p) && (r.Right > r.Left || r.Bottom > r.Top))
                    return new Rectangle(p.X, p.Y, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
            }
            try
            {
                IntPtr target = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : fg;
                object obj;
                Guid iid = NativeMethods.IID_IAccessible;
                if (NativeMethods.AccessibleObjectFromWindow(target, NativeMethods.OBJID_CARET, ref iid, out obj) == 0 && obj != null)
                {
                    var acc = obj as Accessibility.IAccessible;
                    if (acc != null)
                    {
                        int x, y, w, h;
                        acc.accLocation(out x, out y, out w, out h, 0);
                        if (x != 0 || y != 0) return new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
                    }
                }
            }
            catch (Exception) { }
            var c = Cursor.Position;
            return new Rectangle(c.X, c.Y, 1, 20);
        }
    }

    // 不搶焦點的浮動視窗基底
    public class NoActivateForm : Form
    {
        public NoActivateForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        public void ShowNoActivate(int x, int y, int w, int h)
        {
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, x, y, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            if (!Visible) Visible = true;
        }
    }

    public sealed class SuggestionPopup : NoActivateForm
    {
        private static readonly Color Back = Color.FromArgb(255, 255, 255);
        private static readonly Color Border = Color.FromArgb(180, 190, 205);
        private static readonly Color SelBack = Color.FromArgb(0, 103, 192);
        private static readonly Color Fore = Color.FromArgb(30, 30, 30);
        private static readonly Color Dim = Color.FromArgb(130, 130, 130);
        private static readonly Color Hint = Color.FromArgb(245, 247, 250);

        private string[] items = new string[0];
        private int selected;
        private Font itemFont, numFont, hintFont;
        private int rowH, numW, hintH;
        private Rectangle anchor;
        private string hintText = "";

        public event Action<int> ItemClicked;
        public event Action<Rectangle> BoundsChangedOnScreen;

        public SuggestionPopup()
        {
            BackColor = Back;
            SetFontSize(11);
        }

        public void SetFontSize(int pt)
        {
            itemFont = new Font("Segoe UI", pt);
            numFont = new Font("Segoe UI", pt - 2);
            hintFont = new Font("Microsoft JhengHei UI", Math.Max(8, pt - 3));
            using (var g = CreateGraphics())
            {
                rowH = (int)Math.Ceiling(itemFont.GetHeight(g) * 1.35);
                numW = (int)Math.Ceiling(g.MeasureString("0", numFont).Width) + 14;
                hintH = (int)Math.Ceiling(hintFont.GetHeight(g) * 1.5);
            }
        }

        public void SetHint(string text)
        {
            hintText = text ?? "";
        }

        public void ShowItems(string[] newItems, int sel, bool reposition)
        {
            items = newItems;
            selected = sel;
            if (reposition || !Visible) anchor = CaretLocator.Locate();
            int w = 140;
            using (var g = CreateGraphics())
            {
                foreach (var s in items)
                    w = Math.Max(w, numW + TextRenderer.MeasureText(g, s, itemFont).Width + 20);
                if (hintText.Length > 0) w = Math.Max(w, TextRenderer.MeasureText(g, hintText, hintFont).Width + 16);
            }
            int h = items.Length * rowH + 2 + (hintText.Length > 0 ? hintH : 0);
            var screen = Screen.FromPoint(new Point(anchor.Left, anchor.Bottom)).WorkingArea;
            w = Math.Min(w, screen.Width / 2);
            int x = anchor.Left;
            int y = anchor.Bottom + 4;
            if (x + w > screen.Right) x = screen.Right - w;
            if (x < screen.Left) x = screen.Left;
            if (y + h > screen.Bottom) y = anchor.Top - h - 4; // 下方放不下就放到游標上方
            if (y < screen.Top) y = screen.Top;
            ShowNoActivate(x, y, w, h);
            Invalidate();
            if (BoundsChangedOnScreen != null) BoundsChangedOnScreen(new Rectangle(x, y, w, h));
        }

        public void Select(int sel)
        {
            selected = sel;
            Invalidate();
        }

        public void HidePopup()
        {
            if (Visible) Visible = false;
            if (BoundsChangedOnScreen != null) BoundsChangedOnScreen(Rectangle.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            for (int i = 0; i < items.Length; i++)
            {
                var row = new Rectangle(1, 1 + i * rowH, Width - 2, rowH);
                bool sel = i == selected;
                if (sel)
                {
                    using (var b = new SolidBrush(SelBack)) g.FillRectangle(b, row);
                }
                if (i < 10)
                {
                    var numRect = new Rectangle(row.Left + 4, row.Top, numW - 6, row.Height);
                    TextRenderer.DrawText(g, ((i + 1) % 10).ToString(), numFont, numRect, sel ? Color.FromArgb(200, 225, 255) : Dim,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                }
                var textRect = new Rectangle(row.Left + numW, row.Top, row.Width - numW - 4, row.Height);
                TextRenderer.DrawText(g, items[i], itemFont, textRect, sel ? Color.White : Fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (hintText.Length > 0)
            {
                var hr = new Rectangle(1, Height - 1 - hintH, Width - 2, hintH);
                using (var b = new SolidBrush(Hint)) g.FillRectangle(b, hr);
                TextRenderer.DrawText(g, hintText, hintFont, hr, Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = (e.Y - 1) / Math.Max(1, rowH);
            if (e.Button == MouseButtons.Left && i >= 0 && i < items.Length && ItemClicked != null) ItemClicked(i);
        }
    }

    // 切換開關時在滑鼠旁顯示一秒的小提示
    public sealed class Osd : NoActivateForm
    {
        private readonly Timer timer = new Timer();
        private string text = "";
        private Color accent;
        private readonly Font font = new Font("Microsoft JhengHei UI", 11, FontStyle.Bold);

        public Osd()
        {
            BackColor = Color.FromArgb(40, 40, 40);
            timer.Interval = 1200;
            timer.Tick += delegate
            {
                timer.Stop();
                Visible = false;
            };
        }

        public void Flash(string message, Color color)
        {
            text = message;
            accent = color;
            Size sz;
            using (var g = CreateGraphics()) sz = TextRenderer.MeasureText(g, text, font);
            int w = sz.Width + 40, h = sz.Height + 16;
            var p = Cursor.Position;
            var screen = Screen.FromPoint(p).WorkingArea;
            int x = Math.Min(p.X + 16, screen.Right - w), y = Math.Min(p.Y + 20, screen.Bottom - h);
            ShowNoActivate(x, y, w, h);
            Invalidate();
            timer.Stop();
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, 12, Height / 2 - 5, 10, 10);
            TextRenderer.DrawText(g, text, font, new Rectangle(28, 0, Width - 28, Height), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    public static class Icons
    {
        public static readonly Color OnColor = Color.FromArgb(0, 120, 212);
        public static readonly Color OffColor = Color.FromArgb(150, 150, 150);

        // 程式內直接畫出圖示，不需要另外的圖檔
        public static Icon Make(bool on, int size)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var rect = new RectangleF(0.5f, 0.5f, size - 1.5f, size - 1.5f);
                    using (var path = RoundRect(rect, size * 0.22f))
                    using (var b = new SolidBrush(on ? OnColor : OffColor))
                        g.FillPath(b, path);
                    using (var f = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("D", f, Brushes.White, new RectangleF(0, size * 0.02f, size, size), sf);
                    if (!on)
                    {
                        using (var pen = new Pen(Color.White, Math.Max(1.5f, size / 10f)))
                            g.DrawLine(pen, size * 0.2f, size * 0.8f, size * 0.8f, size * 0.2f);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
