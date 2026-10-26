using System;
using System.Drawing;
using System.Windows.Forms;

namespace NetworkRescue
{
#if SOFT_DIALOG_TESTS
    internal static class NativeUiText { internal static string LocalText(string key) { return key; } }
#endif

    internal static class SoftDialogTests
    {
        private static int assertions;
        private static void Assert(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        private static double Contrast(Color first, Color second)
        {
            Func<byte, double> linear = delegate(byte value) { double channel = value / 255.0; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); };
            Func<Color, double> light = delegate(Color color) { return 0.2126 * linear(color.R) + 0.7152 * linear(color.G) + 0.0722 * linear(color.B); };
            double a = light(first), b = light(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        public static int Main()
        {
            Assert(SoftDialog.CancelIsDefault(MessageBoxButtons.OKCancel, MessageBoxDefaultButton.Button2), "Button2 selects cancel");
            Assert(!SoftDialog.CancelIsDefault(MessageBoxButtons.OKCancel, MessageBoxDefaultButton.Button1), "Button1 selects confirm");
            Assert(!SoftDialog.CancelIsDefault(MessageBoxButtons.OK, MessageBoxDefaultButton.Button2), "OK-only keeps its only accept action");
            Assert(SoftDialog.SafeResult(DialogResult.OK, false) == DialogResult.Cancel, "unconfirmed OK cannot authorize");
            Assert(SoftDialog.SafeResult(DialogResult.Cancel, false) == DialogResult.Cancel, "cancel remains cancel");
            Assert(SoftDialog.SafeResult(DialogResult.None, false) == DialogResult.Cancel, "window close remains cancel");
            Assert(SoftDialog.SafeResult(DialogResult.OK, true) == DialogResult.OK, "explicit confirm returns OK");
            Assert(SoftDialog.SafeResult(DialogResult.Cancel, true) == DialogResult.Cancel, "non-OK result cannot authorize");
            bool rejected = false;
            try { SoftDialog.ValidateButtons(MessageBoxButtons.YesNo); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "unsupported buttons rejected");
            foreach (string theme in new[] { "light", "dark" })
            {
                SoftDialog.SetTheme(theme);
                var binding = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                Color surface = (Color)typeof(SoftDialog).GetProperty("Surface", binding).GetValue(null, null);
                Color ink = (Color)typeof(SoftDialog).GetProperty("Ink", binding).GetValue(null, null);
                Color accent = (Color)typeof(SoftDialog).GetProperty("Accent", binding).GetValue(null, null);
                Color primaryText = theme == "dark" ? Color.FromArgb(21, 25, 34) : Color.White;
                Assert(Contrast(surface, ink) >= 4.5, "dialog body text remains readable in " + theme);
                Assert(Contrast(accent, primaryText) >= 4.5, "dialog primary button remains readable in " + theme);
            }
            bool invalidTheme = false;
            try { SoftDialog.SetTheme("auto"); } catch (ArgumentException) { invalidTheme = true; }
            Assert(invalidTheme, "unknown dialog themes are rejected");
            SoftDialog.SetTheme("light");
            Rectangle owner = new Rectangle(400, 200, 800, 600);
            Rectangle owned = SoftDialog.FitBounds(new Rectangle(0, 0, 1920, 1040), owner, 1F, 100);
            Assert(owned.Left + owned.Width / 2 == owner.Left + owner.Width / 2 && owned.Top + owned.Height / 2 == owner.Top + owner.Height / 2, "owned dialog centers on owner when it fits");
            foreach (Rectangle area in new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(-1920, 0, 1920, 1040), new Rectangle(100, 200, 800, 560), new Rectangle(0, 0, 320, 240) })
            foreach (float dpi in new[] { 96F, 144F, 192F, 384F })
            foreach (int textHeight in new[] { 0, 100, 100000 })
            {
                float scale = SoftDialog.LayoutScale(dpi, area);
                Rectangle bounds = SoftDialog.FitBounds(area, new Rectangle(area.Left - 600, area.Top - 600, 100, 100), scale, textHeight);
                Assert(area.Contains(bounds), "dialog fits working area at DPI " + dpi);
                Assert(bounds.Width > 0 && bounds.Height > 0, "positive dialog dimensions");
                Rectangle centered = SoftDialog.FitBounds(area, Rectangle.Empty, scale, textHeight);
                Assert(Math.Abs(centered.Left + centered.Width / 2 - (area.Left + area.Width / 2)) <= 1, "screen center when no owner");
            }
            Console.WriteLine("SoftDialog: " + assertions + " pure policy/geometry assertions passed. No windows shown.");
            return 0;
        }
    }
}
