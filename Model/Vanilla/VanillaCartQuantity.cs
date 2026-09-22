using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaQuantityObservation
    {
        internal Rectangle Dialog, Field;
        internal uint Amount;
        internal VanillaVisualInputProof Proof;
    }

    internal static class VanillaCartQuantity
    {
        internal static uint ConservativeAmount(uint carriedWeight, uint offeredAmount, uint cartWeight, uint cartMaximum)
        {
            if (carriedWeight == 0 || offeredAmount == 0 || cartMaximum == 0 || cartWeight > cartMaximum) return 0;
            uint remaining = cartMaximum - cartWeight;
            if (remaining >= carriedWeight) return offeredAmount;
            // The unedited prompt offers N items already included in total carried W.
            // Their unit weight cannot exceed ceil(W/N), independent of item/category.
            // This is deliberately conservative; never call a tab an item identity.
            ulong upperUnitWeight = Math.Max(1UL, ((ulong)carriedWeight + offeredAmount - 1) / offeredAmount);
            return (uint)Math.Min((ulong)offeredAmount, (ulong)remaining / upperUnitWeight);
        }

        internal static bool TryObserve(Bitmap image, out VanillaQuantityObservation observation)
        {
            observation = null;
            if (image == null || image.Width > 4096 || image.Height > 4096) return false;
            var pixels = new QuantityPixels(image);
            Rectangle[] forms = pixels.Components(false);
            Rectangle[] fields = pixels.Components(true);
            if (fields.Length > 16) return false;
            foreach (Rectangle field in fields)
            {
                Rectangle[] parents = forms.Where(box => box.Contains(field) && field.Top >= box.Top + box.Height / 4).ToArray();
                if (parents.Length != 1) continue;
                Rectangle dialog = parents[0];
                VanillaTextLine[] lines; string evidence;
                // OCR expects dark glyphs on a light surface. The selected number is
                // white on blue; grayscale of the whole selection creates a dark box
                // and can erase or merge digits. Remove only the observed selection
                // background, preserving glyph intensities without substituting text.
                using (Bitmap textImage = pixels.SelectedText(field))
                {
                    if (textImage == null || !VanillaTextRecognition.TryReadDigitsPixelPreserving(textImage,
                        new Rectangle(Point.Empty, textImage.Size), out lines, out evidence)) continue;
                }
                if (lines.Length != 1) continue;
                string text = lines[0].Text.Trim(); uint value;
                if (lines[0].Confidence < 70 || lines[0].Words.Any(w => w.Confidence < 65)
                    || text.Length == 0 || text.Length > 7 || text.Any(c => c < '0' || c > '9')
                    || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value == 0 || value > 2000000) continue;
                if (observation != null) { observation = null; return false; }
                observation = new VanillaQuantityObservation { Dialog = dialog, Field = Rectangle.Inflate(field, 2, 2), Amount = value };
            }
            return observation != null;
        }

        // Count and geometry must agree before using the untouched offered quantity.
        internal static VanillaQuantityObservation ReadStable(VanillaForegroundInput input)
        {
            VanillaQuantityObservation previous = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Check(input);
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    VanillaQuantityObservation current;
                    if (TryObserve(image, out current))
                    {
                        current.Proof = input.LastCaptureProof;
                        if (previous != null && previous.Amount == current.Amount && previous.Field == current.Field
                            && previous.Proof.Window == current.Proof.Window && previous.Proof.ClientOrigin == current.Proof.ClientOrigin
                            && previous.Proof.ClientSize == current.Proof.ClientSize) return current;
                        previous = current;
                    }
                    else previous = null;
                }
                Pause(input, 120);
            }
            throw new InvalidOperationException("Quantity field and offered count were not independently verified; no quantity submitted.");
        }

        internal static void Submit(VanillaForegroundInput input, VanillaQuantityObservation offered, uint amount)
        {
            if (offered == null || amount == 0 || amount > offered.Amount) throw new ArgumentException("Invalid observed quantity.");
            input.ClickFromProof(offered.Field, offered.Proof);
            // Native caret evidence or two visual blink transitions inside the actual edit
            // establish field focus. A modal rectangle alone is not keyboard focus.
            ConfirmFocus(input, offered.Field);
            using (Bitmap fresh = input.CaptureClientBitmap())
            {
                var proof = input.LastCaptureProof;
                input.ReplaceFocusedTextFromProof(amount.ToString(CultureInfo.InvariantCulture), proof,
                    () => { Check(input); if (VanillaCredentialFocus.Observe(proof.Window, offered.Field) == VanillaFieldFocus.Contradicted)
                        throw new InvalidOperationException("Quantity keyboard focus changed."); });
            }
            // Select the entered number so the insertion caret cannot be read as a digit.
            input.Chord(true, false, false, Keys.A);
            VanillaQuantityObservation confirmed = ReadStable(input);
            if (confirmed.Dialog != offered.Dialog || Math.Abs(confirmed.Field.Left - offered.Field.Left) > 3
                || Math.Abs(confirmed.Field.Top - offered.Field.Top) > 3 || confirmed.Amount != amount)
                throw new InvalidOperationException("Typed quantity readback did not match; Enter withheld.");
            if (VanillaCredentialFocus.Observe(confirmed.Proof.Window, confirmed.Field) == VanillaFieldFocus.Contradicted)
                throw new InvalidOperationException("Quantity keyboard focus was lost; Enter withheld.");
            input.PressFromProof(Keys.Enter, confirmed.Proof);
        }

        internal static bool CancelKnownPrompt(VanillaForegroundInput input)
        {
            using (Bitmap image = input.CaptureClientBitmap())
            {
                VanillaQuantityObservation current;
                if (!TryObserve(image, out current)) return !VanillaInventoryVision.HasQuantityPrompt(image);
                input.PressFromProof(Keys.Escape, input.LastCaptureProof);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                Pause(input, 120);
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    VanillaQuantityObservation current;
                    if (TryObserve(image, out current) || VanillaInventoryVision.HasQuantityPrompt(image)) return false;
                }
            }
            return true;
        }

        private static void ConfirmFocus(VanillaForegroundInput input, Rectangle field)
        {
            Bitmap previous = null;
            int native = 0, blinks = 0;
            Rectangle firstCaret = Rectangle.Empty;
            try
            {
                for (int pass = 0; pass < 20; pass++)
                {
                    Check(input);
                    Bitmap current = input.CaptureClientBitmap();
                    try
                    {
                        VanillaFieldFocus state = VanillaCredentialFocus.Observe(input.LastCaptureProof.Window, field);
                        if (state == VanillaFieldFocus.Contradicted) throw new InvalidOperationException("Another control owns quantity input.");
                        if (state == VanillaFieldFocus.Confirmed) { if (++native >= 2) return; }
                        else
                        {
                            native = 0; Rectangle caret;
                            if (previous != null && VanillaCredentialPattern.TryDetectCaretBlink(previous, current, field, out caret))
                            {
                                if (blinks == 0 || firstCaret == caret) { firstCaret = caret; if (++blinks >= 2) return; }
                                else { blinks = 1; firstCaret = caret; }
                            }
                        }
                        previous?.Dispose(); previous = current; current = null;
                    }
                    finally { current?.Dispose(); }
                    Pause(input, 100);
                }
            }
            finally { previous?.Dispose(); }
            throw new InvalidOperationException("Quantity field focus could not be verified; no value typed.");
        }

        private sealed class QuantityPixels
        {
            private readonly byte[] rgb;
            private readonly int width, height;
            internal QuantityPixels(Bitmap image)
            {
                width = image.Width; height = image.Height; rgb = new byte[width * height * 3];
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bitmap)) g.DrawImageUnscaled(image, 0, 0);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try { for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), rgb, y * width * 3, width * 3); }
                    finally { bitmap.UnlockBits(data); }
                }
            }
            internal bool Blue(int x, int y)
            {
                int i = (y * width + x) * 3;
                return rgb[i] >= 180 && rgb[i] - rgb[i + 2] >= 60 && rgb[i] - rgb[i + 1] >= 30;
            }
            internal Bitmap SelectedText(Rectangle field)
            {
                var background = new Dictionary<int, int>();
                for (int y = field.Top; y < field.Bottom; y++)
                for (int x = field.Left; x < field.Right; x++)
                {
                    if (!Blue(x, y)) continue;
                    int i = (y * width + x) * 3;
                    int value = rgb[i] | rgb[i + 1] << 8 | rgb[i + 2] << 16;
                    int count; background.TryGetValue(value, out count); background[value] = count + 1;
                }
                if (background.Count == 0) return null;
                var mode = background.OrderByDescending(item => item.Value).First();
                if (mode.Value < field.Width * field.Height / 3) return null;
                int[] color = { mode.Key & 255, mode.Key >> 8 & 255, mode.Key >> 16 & 255 };
                const int padding = 4;
                var result = new Bitmap(field.Width + padding * 2, field.Height + padding * 2, PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(result)) graphics.Clear(Color.White);
                int minX = result.Width, minY = result.Height, maxX = -1, maxY = -1;
                for (int y = field.Top; y < field.Bottom; y++)
                for (int x = field.Left; x < field.Right; x++)
                {
                    int i = (y * width + x) * 3, channels = 0;
                    double alpha = 0;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        if (color[channel] >= 240) continue;
                        alpha += Math.Max(0, Math.Min(1, (rgb[i + channel] - color[channel]) / (255.0 - color[channel])));
                        channels++;
                    }
                    int gray = (int)Math.Round(255 * (1 - alpha / Math.Max(1, channels)));
                    int px = x - field.Left + padding, py = y - field.Top + padding;
                    result.SetPixel(px, py, Color.FromArgb(gray, gray, gray));
                    if (gray < 245)
                    {
                        minX = Math.Min(minX, px); minY = Math.Min(minY, py);
                        maxX = Math.Max(maxX, px); maxY = Math.Max(maxY, py);
                    }
                }
                if (maxX < minX || maxY < minY) { result.Dispose(); return null; }
                const int inkPadding = 4;
                Rectangle crop = Rectangle.FromLTRB(Math.Max(0, minX - inkPadding), Math.Max(0, minY - inkPadding),
                    Math.Min(result.Width, maxX + inkPadding + 1), Math.Min(result.Height, maxY + inkPadding + 1));
                Bitmap cropped = result.Clone(crop, PixelFormat.Format24bppRgb);
                result.Dispose();
                return cropped;
            }

            internal Rectangle[] Components(bool selected)
            {
                var remaining = new bool[width * height];
                for (int p = 0; p < remaining.Length; p++)
                {
                    int i = p * 3;
                    remaining[p] = selected ? Blue(p % width, p / width)
                        : Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2])) >= 205;
                }
                var queue = new int[remaining.Length];
                var forms = new List<Rectangle>();
                for (int seed = 0; seed < remaining.Length; seed++)
                {
                    if (!remaining[seed]) continue;
                    queue[0] = seed; remaining[seed] = false;
                    int count = 1, next = 0, left = width, right = 0, top = height, bottom = 0;
                    while (next < count)
                    {
                        int point = queue[next++], x = point % width, y = point / width;
                        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            int q = ny * width + nx;
                            if (remaining[q]) { remaining[q] = false; queue[count++] = q; }
                        }
                    }
                    int w = right - left + 1, h = bottom - top + 1;
                    double aspect = w / (double)h;
                    if (selected ? (w >= 5 && w <= 350 && h >= 7 && h <= 65 && aspect >= .35 && aspect <= 25 && count >= w * h * .4)
                        : (w >= 70 && w <= 1100 && h >= 24 && h <= 250 && aspect >= 2.4 && aspect <= 7 && count >= w * h * .45))
                        forms.Add(Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
                    if (forms.Count > 32) return new Rectangle[0];
                }
                return forms.ToArray();
            }
        }
        private static void Check(VanillaForegroundInput input)
        { if (input.CancellationRequested != null && input.CancellationRequested()) throw new OperationCanceledException("Cart quantity operation cancelled."); }
        private static void Pause(VanillaForegroundInput input, int milliseconds)
        { for (int n = 0; n < milliseconds; n += 40) { Check(input); Thread.Sleep(Math.Min(40, milliseconds - n)); } Check(input); }
    }
}
