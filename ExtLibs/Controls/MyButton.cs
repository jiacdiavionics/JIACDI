using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MissionPlanner.Controls
{
    public class MyButton : Button
    {
        private bool mouseOver;
        private bool mouseDown;
        private bool inOnPaint;
        private float interactionLevel;
        private Timer animationTimer;

        internal Color _BGGradTop;
        internal Color _BGGradBot;
        internal Color _TextColor;
        internal Color _TextColorNotEnabled;
        internal Color _Outline;
        internal Color _ColorNotEnabled;
        internal Color _ColorMouseOver;
        internal Color _ColorMouseDown;

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "0x94, 0xc1, 0x1f")]
        public Color BGGradTop { get { return _BGGradTop; } set { _BGGradTop = value; Invalidate(); } }

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "0xcd, 0xe2, 0x96")]
        public Color BGGradBot { get { return _BGGradBot; } set { _BGGradBot = value; Invalidate(); } }

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "73, 0x2b, 0x3a, 0x03")]
        public Color ColorNotEnabled { get { return _ColorNotEnabled; } set { _ColorNotEnabled = value; Invalidate(); } }

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "73, 0x2b, 0x3a, 0x03")]
        public Color ColorMouseOver { get { return _ColorMouseOver; } set { _ColorMouseOver = value; Invalidate(); } }

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "150, 0x2b, 0x3a, 0x03")]
        public Color ColorMouseDown { get { return _ColorMouseDown; } set { _ColorMouseDown = value; Invalidate(); } }

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "0x40, 0x57, 0x04")]
        public Color TextColor { get { return _TextColor; } set { _TextColor = value; Invalidate(); } }

        [Browsable(true), Category("Colors")]
        public Color TextColorNotEnabled
        {
            get { return _TextColorNotEnabled.IsEmpty ? _TextColor : _TextColorNotEnabled; }
            set { _TextColorNotEnabled = value; Invalidate(); }
        }

        [Browsable(true), Category("Colors")]
        [DefaultValue(typeof(Color), "0x79, 0x94, 0x29")]
        public Color Outline { get { return _Outline; } set { _Outline = value; Invalidate(); } }

        protected override Size DefaultSize => base.DefaultSize;

        public MyButton()
        {
            _BGGradTop = Color.FromArgb(31, 38, 42);
            _BGGradBot = _BGGradTop;
            _TextColor = Color.FromArgb(232, 237, 240);
            _TextColorNotEnabled = Color.FromArgb(101, 114, 120);
            _Outline = Color.FromArgb(53, 65, 71);
            _ColorNotEnabled = Color.FromArgb(17, 21, 23);
            _ColorMouseOver = Color.FromArgb(42, 51, 56);
            _ColorMouseDown = Color.FromArgb(17, 118, 157);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (inOnPaint || Width <= 0 || Height <= 0)
            {
                return;
            }

            inOnPaint = true;
            try
            {
                Graphics graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                Color parentColor = Parent == null ? BackColor : Parent.BackColor;
                graphics.Clear(parentColor);

                Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                int radius = Math.Min(6, Math.Max(2, Height / 4));
                Color fill = ResolveFill();

                using (GraphicsPath path = RoundedRectangle(bounds, radius))
                using (SolidBrush brush = new SolidBrush(fill))
                using (Pen border = new Pen(Enabled ? Outline : Blend(Outline, parentColor, 0.55f)))
                {
                    graphics.FillPath(brush, path);
                    graphics.DrawPath(border, path);
                }

                int pressOffset = mouseDown && Enabled ? 1 : 0;
                DrawContent(graphics, new Rectangle(5, 3 + pressOffset, Math.Max(1, Width - 10), Math.Max(1, Height - 6)));

                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = Rectangle.Inflate(bounds, -4, -4);
                    ControlPaint.DrawFocusRectangle(graphics, focus, TextColor, fill);
                }
            }
            finally
            {
                inOnPaint = false;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            mouseOver = true;
            StartAnimation();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouseOver = false;
            mouseDown = false;
            StartAnimation();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            mouseDown = true;
            StartAnimation();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            mouseDown = false;
            StartAnimation();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            StartAnimation();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && animationTimer != null)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
                animationTimer = null;
            }

            base.Dispose(disposing);
        }

        private Color ResolveFill()
        {
            Color baseColor = BGGradTop.IsEmpty ? BackColor : BGGradTop;
            if (!Enabled)
            {
                return Blend(baseColor, ColorNotEnabled, 0.72f);
            }

            if (mouseDown)
            {
                return Blend(baseColor, ColorMouseDown, 0.88f);
            }

            return Blend(baseColor, ColorMouseOver, interactionLevel);
        }

        private void DrawContent(Graphics graphics, Rectangle bounds)
        {
            Color color = Enabled ? TextColor : TextColorNotEnabled;
            Image image = Image;
            string text = Text ?? string.Empty;
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter;
            if (!ShowKeyboardCues)
            {
                flags |= TextFormatFlags.HidePrefix;
            }

            if (image == null)
            {
                TextRenderer.DrawText(graphics, text, Font, bounds, color, flags);
                return;
            }

            int imageSide = Math.Min(Math.Min(18, bounds.Height), Math.Min(image.Width, image.Height));
            if (string.IsNullOrWhiteSpace(text))
            {
                Rectangle imageBounds = new Rectangle(
                    bounds.Left + ((bounds.Width - imageSide) / 2),
                    bounds.Top + ((bounds.Height - imageSide) / 2),
                    imageSide,
                    imageSide);
                graphics.DrawImage(image, imageBounds);
                return;
            }

            Size textSize = TextRenderer.MeasureText(graphics, text, Font, bounds.Size,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int gap = 6;
            int totalWidth = Math.Min(bounds.Width, imageSide + gap + textSize.Width);
            int startX = bounds.Left + Math.Max(0, (bounds.Width - totalWidth) / 2);
            Rectangle iconBounds = new Rectangle(startX, bounds.Top + ((bounds.Height - imageSide) / 2), imageSide, imageSide);
            Rectangle textBounds = new Rectangle(iconBounds.Right + gap, bounds.Top,
                Math.Max(1, bounds.Right - iconBounds.Right - gap), bounds.Height);
            graphics.DrawImage(image, iconBounds);
            TextRenderer.DrawText(graphics, text, Font, textBounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
        }

        private void StartAnimation()
        {
            float target = mouseOver && Enabled ? 1f : 0f;
            if (SystemInformation.HighContrast || SystemInformation.TerminalServerSession)
            {
                interactionLevel = target;
                Invalidate();
                return;
            }

            if (animationTimer == null)
            {
                animationTimer = new Timer { Interval = 16 };
                animationTimer.Tick += AnimationTick;
            }

            if (!animationTimer.Enabled)
            {
                animationTimer.Start();
            }
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            float target = mouseOver && Enabled ? 1f : 0f;
            interactionLevel += (target - interactionLevel) * 0.28f;
            if (Math.Abs(target - interactionLevel) < 0.025f)
            {
                interactionLevel = target;
                animationTimer.Stop();
            }

            Invalidate();
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            float alpha = (to.A / 255f) * amount;
            return Color.FromArgb(
                (int)(from.A + ((255 - from.A) * alpha)),
                (int)(from.R + ((to.R - from.R) * alpha)),
                (int)(from.G + ((to.G - from.G) * alpha)),
                (int)(from.B + ((to.B - from.B) * alpha)));
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
