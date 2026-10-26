using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetworkRescue
{
    internal static class SoftDialog
    {
        private static bool darkTheme;
        private static Color Surface { get { return Tone(Color.FromArgb(244, 238, 232), Color.FromArgb(32, 38, 53)); } }
        private static Color Canvas { get { return Tone(Color.FromArgb(238, 232, 225), Color.FromArgb(21, 25, 34)); } }
        private static Color Ink { get { return Tone(Color.FromArgb(37, 38, 44), Color.FromArgb(232, 236, 244)); } }
        private static Color Accent { get { return Tone(Color.FromArgb(59, 70, 185), Color.FromArgb(135, 146, 255)); } }
        private static Color Tone(Color day, Color night) { return darkTheme ? night : day; }
        internal static void SetTheme(string theme)
        {
            if (theme != "light" && theme != "dark") throw new ArgumentException("Unknown theme.", "theme");
            darkTheme = theme == "dark";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBounds { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr handle, out NativeBounds bounds);

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxIcon icon)
        {
            return Show(owner, text, caption, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            ValidateButtons(buttons);
            using (DialogWindow dialog = new DialogWindow(owner, text ?? "", caption ?? "", buttons, icon, defaultButton))
            {
                DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                return SafeResult(result, dialog.Confirmed);
            }
        }

        internal static void ValidateButtons(MessageBoxButtons buttons)
        {
            if (buttons != MessageBoxButtons.OK && buttons != MessageBoxButtons.OKCancel)
                throw new ArgumentException("Only OK and OKCancel dialogs are supported.", "buttons");
        }

        internal static bool CancelIsDefault(MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
        {
            return buttons == MessageBoxButtons.OKCancel && defaultButton == MessageBoxDefaultButton.Button2;
        }

        internal static DialogResult SafeResult(DialogResult result, bool confirmed)
        {
            return confirmed && result == DialogResult.OK ? DialogResult.OK : DialogResult.Cancel;
        }

        internal static float LayoutScale(float dpi, Rectangle workingArea)
        {
            float requested = Math.Max(.5F, dpi / 96F);
            return Math.Min(requested, Math.Max(.25F, Math.Min(workingArea.Width / 520F, workingArea.Height / 340F)));
        }

        internal static Rectangle FitBounds(Rectangle workingArea, Rectangle ownerBounds, float scale, int preferredTextHeight)
        {
            int margin = Math.Min(Px(12, scale), Math.Max(0, Math.Min(workingArea.Width, workingArea.Height) / 20));
            int width = Math.Min(Px(660, scale), Math.Max(1, workingArea.Width - margin * 2));
            int height = Math.Min(Math.Max(Px(310, scale), Math.Min(Px(650, scale), preferredTextHeight + Px(192, scale))), Math.Max(1, workingArea.Height - margin * 2));
            Rectangle center = ownerBounds.IsEmpty ? workingArea : ownerBounds;
            int x = center.Left + (center.Width - width) / 2;
            int y = center.Top + (center.Height - height) / 2;
            return new Rectangle(Math.Max(workingArea.Left + margin, Math.Min(x, workingArea.Right - margin - width)), Math.Max(workingArea.Top + margin, Math.Min(y, workingArea.Bottom - margin - height)), width, height);
        }

        private static int Px(float value, float scale) { return Math.Max(1, (int)Math.Round(value * scale)); }

        private static string Label(string key, string fallback)
        {
            string value = NativeUiText.LocalText(key);
            return String.IsNullOrEmpty(value) || value == key ? fallback : value;
        }

        private static GraphicsPath Rounded(RectangleF rectangle, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void FillRounded(Graphics graphics, RectangleF rectangle, float radius, Color color)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            using (GraphicsPath path = Rounded(rectangle, radius))
            using (SolidBrush brush = new SolidBrush(color)) graphics.FillPath(brush, path);
        }

        private sealed class DialogWindow : Form
        {
            private readonly float scale;
            private readonly MessageBoxIcon mark;
            private readonly Font bodyFont, captionFont;
            private readonly RichTextBox body;
            private readonly Button confirm, cancel;
            private Rectangle textPanel, markBounds;
            internal bool Confirmed { get; private set; }

            internal DialogWindow(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
            {
                IntPtr handle = owner == null ? IntPtr.Zero : owner.Handle;
                Rectangle workingArea = Screen.FromHandle(handle).WorkingArea;
                Control ownerControl = handle == IntPtr.Zero ? null : Control.FromHandle(handle);
                Form ownerForm = ownerControl == null ? null : ownerControl.FindForm();
                Rectangle ownerBounds = ownerForm == null ? Rectangle.Empty : ownerForm.Bounds;
                NativeBounds native;
                if (ownerBounds.IsEmpty && handle != IntPtr.Zero && GetWindowRect(handle, out native)) ownerBounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
                float dpi;
                using (Graphics graphics = Graphics.FromHwnd(handle)) dpi = graphics.DpiX;
                scale = LayoutScale(dpi, workingArea);
                float fontScale = scale / Math.Max(.5F, dpi / 96F);
                bodyFont = new Font("Segoe UI", 10.5F * fontScale, FontStyle.Regular, GraphicsUnit.Point);
                captionFont = new Font("Segoe UI", 13.5F * fontScale, FontStyle.Bold, GraphicsUnit.Point);
                mark = icon;
                Text = caption;
                AccessibleName = caption;
                AccessibleRole = AccessibleRole.Dialog;
                AutoScaleMode = AutoScaleMode.None;
                Font = bodyFont;
                BackColor = Canvas;
                ForeColor = Ink;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
                ControlBox = false;
                TopMost = false;
                StartPosition = FormStartPosition.Manual;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

                // Sample only for sizing; the read-only control receives the complete original text.
                string sample = text.Length > 8192 ? text.Substring(0, 8192) : text;
                int desiredWidth = Math.Min(Px(660, scale), Math.Max(1, workingArea.Width - Px(24, scale)));
                int preferredHeight;
                using (Graphics graphics = Graphics.FromHwnd(handle))
                    preferredHeight = TextRenderer.MeasureText(graphics, sample, bodyFont, new Size(Math.Max(1, desiredWidth - Px(82, scale)), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height;
                Bounds = FitBounds(workingArea, ownerBounds, scale, preferredHeight);
                int width = ClientSize.Width, height = ClientSize.Height;
                bool hasMark = icon != MessageBoxIcon.None;
                markBounds = new Rectangle(Px(27, scale), Px(25, scale), Px(33, scale), Px(33, scale));
                Label title = new Label { Text = caption, AccessibleName = caption, AutoEllipsis = true, UseMnemonic = false, Font = captionFont, ForeColor = Ink, BackColor = Surface, TextAlign = ContentAlignment.MiddleLeft, TabStop = false };
                title.Bounds = new Rectangle(Px(hasMark ? 73 : 29, scale), Px(25, scale), Math.Max(1, width - Px(hasMark ? 139 : 95, scale)), Px(35, scale));
                Controls.Add(title);
                SoftButton close = new SoftButton("", false, true, scale) { AccessibleName = Label("dialogClose", "Close"), TabIndex = 3 };
                close.Bounds = new Rectangle(width - Px(58, scale), Px(24, scale), Px(32, scale), Px(32, scale));
                close.Click += delegate { Complete(false); };
                Controls.Add(close);
                textPanel = new Rectangle(Px(24, scale), Px(78, scale), Math.Max(1, width - Px(48, scale)), Math.Max(1, height - Px(164, scale)));
                body = new RichTextBox { MaxLength = Int32.MaxValue, Text = text, ReadOnly = true, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Ink, Font = bodyFont, HideSelection = false, TabStop = true, TabIndex = 0, AccessibleName = caption, ShortcutsEnabled = true };
                body.Bounds = new Rectangle(textPanel.Left + Px(13, scale), textPanel.Top + Px(12, scale), Math.Max(1, textPanel.Width - Px(26, scale)), Math.Max(1, textPanel.Height - Px(24, scale)));
                Controls.Add(body);
                string confirmLabel = buttons == MessageBoxButtons.OKCancel ? Label("dialogConfirm", "Confirm") : Label("dialogAcknowledge", "OK");
                string cancelLabel = Label("dialogCancel", "Cancel");
                int buttonHeight = Px(40, scale), buttonGap = Px(12, scale), right = width - Px(25, scale), y = height - Px(65, scale);
                int confirmWidth = ButtonWidth(confirmLabel, scale, width, buttons == MessageBoxButtons.OKCancel);
                int cancelWidth = ButtonWidth(cancelLabel, scale, width, true);
                confirm = new SoftButton(confirmLabel, true, false, scale) { TabIndex = 1, AccessibleName = confirmLabel };
                confirm.Bounds = new Rectangle(right - confirmWidth, y, confirmWidth, buttonHeight);
                confirm.Click += delegate { Complete(true); };
                Controls.Add(confirm);
                if (buttons == MessageBoxButtons.OKCancel)
                {
                    cancel = new SoftButton(cancelLabel, false, false, scale) { TabIndex = 2, AccessibleName = cancelLabel };
                    cancel.Bounds = new Rectangle(confirm.Left - buttonGap - cancelWidth, y, cancelWidth, buttonHeight);
                    cancel.Click += delegate { Complete(false); };
                    Controls.Add(cancel);
                    CancelButton = cancel;
                }
                else CancelButton = close;
                AcceptButton = CancelIsDefault(buttons, defaultButton) ? cancel : confirm;
                ActiveControl = (Control)AcceptButton;
                Shown += delegate
                {
                    Rectangle area = Screen.FromControl(this).WorkingArea;
                    Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
                    ActiveControl = (Control)AcceptButton;
                    ((Control)AcceptButton).Focus();
                };
            }

            private int ButtonWidth(string text, float factor, int width, bool twoButtons)
            {
                int measured;
                using (Graphics graphics = CreateGraphics()) measured = TextRenderer.MeasureText(graphics, text, bodyFont, Size.Empty, TextFormatFlags.NoPrefix).Width;
                return Math.Min(Math.Max(Px(96, factor), measured + Px(30, factor)), Math.Max(1, (width - Px(62, factor)) / (twoButtons ? 2 : 1)));
            }

            private void Complete(bool confirmed)
            {
                Confirmed = confirmed;
                DialogResult = confirmed ? DialogResult.OK : DialogResult.Cancel;
                Close();
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if ((keyData & Keys.KeyCode) == Keys.Escape) { Complete(false); return true; }
                return base.ProcessDialogKey(keyData);
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!Confirmed) DialogResult = DialogResult.Cancel;
                base.OnFormClosing(e);
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Width <= 1 || Height <= 1) return;
                using (GraphicsPath path = Rounded(new RectangleF(0, 0, Width, Height), Math.Max(12, 22 * scale)))
                {
                    Region previous = Region;
                    Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF panel = new RectangleF(7 * scale, 7 * scale, Width - 14 * scale, Height - 14 * scale);
                for (int i = 7; i >= 1; i--)
                {
                    RectangleF shadow = panel; shadow.Offset(i * scale / 2, i * scale / 2);
                    FillRounded(graphics, shadow, 19 * scale, Tone(Color.FromArgb(10, 163, 148, 135), Color.FromArgb(18, 0, 0, 0)));
                }
                RectangleF light = panel; light.Offset(-2 * scale, -2 * scale);
                FillRounded(graphics, light, 19 * scale, Tone(Color.FromArgb(252, 248, 243), Color.FromArgb(48, 58, 77)));
                FillRounded(graphics, panel, 19 * scale, Surface);
                if (!textPanel.IsEmpty)
                {
                    FillRounded(graphics, textPanel, 12 * scale, Tone(Color.FromArgb(229, 221, 213), Color.FromArgb(48, 58, 77)));
                    RectangleF well = textPanel; well.Inflate(-2 * scale, -2 * scale); well.Offset(scale, scale);
                    FillRounded(graphics, well, 10 * scale, Surface);
                }
                if (mark != MessageBoxIcon.None) DrawMark(graphics);
            }

            private void DrawMark(Graphics graphics)
            {
                bool warning = mark == MessageBoxIcon.Warning, error = mark == MessageBoxIcon.Error;
                Color color = warning ? Tone(Color.FromArgb(134, 90, 24), Color.FromArgb(228, 189, 118)) : error ? Tone(Color.FromArgb(156, 57, 69), Color.FromArgb(242, 160, 171)) : Accent;
                using (SolidBrush background = new SolidBrush(warning ? Tone(Color.FromArgb(243, 232, 207), Color.FromArgb(66, 55, 32)) : error ? Tone(Color.FromArgb(243, 223, 224), Color.FromArgb(68, 40, 53)) : Tone(Color.FromArgb(227, 227, 242), Color.FromArgb(45, 53, 80))))
                using (Pen pen = new Pen(color, Math.Max(1.4F, 1.7F * scale)))
                {
                    RectangleF r = markBounds;
                    graphics.FillEllipse(background, r);
                    r.Inflate(-7 * scale, -7 * scale);
                    float cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                    if (error)
                    {
                        graphics.DrawEllipse(pen, r);
                        graphics.DrawLine(pen, cx - 4 * scale, cy - 4 * scale, cx + 4 * scale, cy + 4 * scale);
                        graphics.DrawLine(pen, cx + 4 * scale, cy - 4 * scale, cx - 4 * scale, cy + 4 * scale);
                    }
                    else if (warning)
                    {
                        graphics.DrawPolygon(pen, new[] { new PointF(cx, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) });
                        graphics.DrawLine(pen, cx, cy - 2 * scale, cx, cy + 3 * scale);
                        graphics.DrawLine(pen, cx, cy + 5 * scale, cx, cy + 6 * scale);
                    }
                    else
                    {
                        graphics.DrawEllipse(pen, r);
                        graphics.DrawLine(pen, cx, cy - 5 * scale, cx, cy - 4 * scale);
                        graphics.DrawLine(pen, cx, cy - scale, cx, cy + 6 * scale);
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) { if (bodyFont != null) bodyFont.Dispose(); if (captionFont != null) captionFont.Dispose(); }
            }
        }

        private sealed class SoftButton : Button
        {
            private readonly bool primary, close;
            private readonly float scale;
            private bool pressed;

            internal SoftButton(string text, bool primary, bool close, float scale)
            {
                this.primary = primary; this.close = close; this.scale = scale;
                Text = text; UseMnemonic = false; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                BackColor = Surface; UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }

            protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnMouseLeave(EventArgs e) { pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
            protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics; graphics.Clear(Surface); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF r = new RectangleF(3 * scale, 3 * scale, Math.Max(1, Width - 7 * scale), Math.Max(1, Height - 7 * scale));
                RectangleF shadow = r; shadow.Offset(2 * scale, 2 * scale);
                FillRounded(graphics, shadow, 9 * scale, Tone(Color.FromArgb(209, 197, 186), Color.FromArgb(16, 19, 27)));
                RectangleF light = r; light.Offset(-scale, -scale); FillRounded(graphics, light, 9 * scale, Tone(Color.FromArgb(255, 252, 247), Color.FromArgb(48, 58, 77)));
                FillRounded(graphics, r, 9 * scale, primary ? pressed ? Tone(Color.FromArgb(47, 57, 153), Color.FromArgb(114, 125, 223)) : Accent : pressed ? Tone(Color.FromArgb(228, 220, 212), Color.FromArgb(44, 52, 70)) : Surface);
                if (close)
                {
                    float x = Width / 2F, y = Height / 2F, size = 4 * scale;
                    using (Pen pen = new Pen(Ink, Math.Max(1.4F, 1.6F * scale))) { graphics.DrawLine(pen, x - size, y - size, x + size, y + size); graphics.DrawLine(pen, x + size, y - size, x - size, y + size); }
                }
                else TextRenderer.DrawText(graphics, Text, Font, Rectangle.Round(r), primary ? Tone(Color.White, Color.FromArgb(21, 25, 34)) : Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                if (Focused && ShowFocusCues)
                    using (GraphicsPath path = Rounded(new RectangleF(scale, scale, Width - 3 * scale, Height - 3 * scale), 10 * scale))
                    using (Pen pen = new Pen(Accent, Math.Max(1, 1.5F * scale))) graphics.DrawPath(pen, path);
            }
        }
    }
}
