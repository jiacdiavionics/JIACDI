using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MissionPlanner.Controls
{
    /// <summary>
    /// Flat, high-clarity renderer shared by the application shell and operational toolbars.
    /// Hover transitions are paint-only and never enter the telemetry or command paths.
    /// </summary>
    public class DIMPRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Canvas = Color.FromArgb(17, 21, 23);
        private static readonly Color Surface = Color.FromArgb(23, 28, 31);
        private static readonly Color SurfaceRaised = Color.FromArgb(31, 38, 42);
        private static readonly Color SurfaceHover = Color.FromArgb(42, 51, 56);
        private static readonly Color Border = Color.FromArgb(53, 65, 71);
        private static readonly Color Accent = Color.FromArgb(27, 158, 207);
        private static readonly Color AccentBright = Color.FromArgb(62, 190, 235);
        private static readonly Color AccentPressed = Color.FromArgb(17, 118, 157);
        private static readonly Color TextPrimary = Color.FromArgb(232, 237, 240);
        private static readonly Color TextSecondary = Color.FromArgb(159, 172, 179);
        private static readonly Color TextDisabled = Color.FromArgb(101, 114, 120);

        private readonly Dictionary<ToolStripItem, float> hoverLevels =
            new Dictionary<ToolStripItem, float>();
        private Timer animationTimer;

        public DIMPRenderer()
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(e.ToolStrip is ToolStripDropDown ? SurfaceRaised : Surface))
            {
                e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            Rectangle margin = e.AffectedBounds;
            using (SolidBrush brush = new SolidBrush(Surface))
            {
                e.Graphics.FillRectangle(brush, margin);
            }

            using (Pen border = new Pen(Border))
            {
                e.Graphics.DrawLine(border, margin.Right - 1, margin.Top + 4, margin.Right - 1, margin.Bottom - 4);
            }
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripButton button = e.Item as ToolStripButton;
            if (button == null)
            {
                base.OnRenderButtonBackground(e);
                return;
            }

            Rectangle bounds = new Rectangle(1, 1, Math.Max(1, button.Width - 2), Math.Max(1, button.Height - 2));
            bool connect = button.Name == "MenuConnect";
            bool persistentSelection = button.Checked || IsPersistentNavigationSelection(button);
            float hover = GetHoverLevel(button);
            Color fill = Color.Transparent;

            if (connect)
            {
                fill = Blend(Accent, AccentBright, hover * 0.35f);
            }
            else if (button.Pressed)
            {
                fill = AccentPressed;
            }
            else if (persistentSelection)
            {
                fill = Blend(SurfaceRaised, Accent, 0.25f + (hover * 0.08f));
            }
            else if (hover > 0.01f)
            {
                fill = Blend(Surface, SurfaceHover, hover);
            }

            if (!button.Enabled && fill != Color.Transparent)
            {
                fill = Blend(fill, Canvas, 0.62f);
            }

            if (fill != Color.Transparent)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedRectangle(bounds, connect ? 6 : 5))
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    e.Graphics.FillPath(brush, path);
                }
            }

            if (persistentSelection && !connect)
            {
                int y = bounds.Bottom - 1;
                using (Pen accent = new Pen(AccentBright, 2))
                {
                    e.Graphics.DrawLine(accent, bounds.Left + 7, y, bounds.Right - 7, y);
                }
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle bounds = new Rectangle(2, 1, Math.Max(1, e.Item.Width - 4), Math.Max(1, e.Item.Height - 2));
            bool highlighted = e.Item.Selected || e.Item.Pressed;
            bool checkedItem = e.Item is ToolStripMenuItem && ((ToolStripMenuItem)e.Item).Checked;

            if (!highlighted && !checkedItem)
            {
                return;
            }

            Color fill = highlighted ? SurfaceHover : Blend(SurfaceRaised, Accent, 0.18f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRectangle(bounds, 4))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                Rectangle bounds = new Rectangle(1, 1, Math.Max(1, e.Item.Width - 2), Math.Max(1, e.Item.Height - 2));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedRectangle(bounds, 5))
                using (SolidBrush brush = new SolidBrush(e.Item.Pressed ? AccentPressed : SurfaceHover))
                {
                    e.Graphics.FillPath(brush, path);
                }
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            ToolStripMenuItem menuItem = e.Item as ToolStripMenuItem;
            if (menuItem == null || !menuItem.Checked)
            {
                return;
            }

            Rectangle bounds = e.ImageRectangle;
            bounds.Inflate(-1, -1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRectangle(bounds, 4))
            using (SolidBrush brush = new SolidBrush(Accent))
            {
                e.Graphics.FillPath(brush, path);
            }

            using (Pen pen = new Pen(Color.White, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                e.Graphics.DrawLines(pen, new[]
                {
                    new Point(bounds.Left + 3, bounds.Top + bounds.Height / 2),
                    new Point(bounds.Left + bounds.Width / 2 - 1, bounds.Bottom - 4),
                    new Point(bounds.Right - 3, bounds.Top + 3)
                });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen pen = new Pen(Border))
            {
                if (e.Vertical)
                {
                    e.Graphics.DrawLine(pen, e.Item.Width / 2, 6, e.Item.Width / 2, e.Item.Height - 6);
                }
                else
                {
                    e.Graphics.DrawLine(pen, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
                }
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                using (Pen pen = new Pen(Border))
                {
                    Rectangle bounds = e.ToolStrip.ClientRectangle;
                    bounds.Width--;
                    bounds.Height--;
                    e.Graphics.DrawRectangle(pen, bounds);
                }
            }
            else
            {
                using (Pen pen = new Pen(Border))
                {
                    e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
                }
            }
        }

        protected override void OnRenderToolStripPanelBackground(ToolStripPanelRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(Surface))
            {
                e.Graphics.FillRectangle(brush, e.ToolStripPanel.ClientRectangle);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? TextSecondary : TextDisabled;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool connect = e.Item.Name == "MenuConnect";
            Color color = !e.Item.Enabled
                ? TextDisabled
                : connect || e.Item.Selected || e.Item.Pressed ? Color.White : TextPrimary;

            TextRenderer.DrawText(
                e.Graphics,
                e.Item.Text,
                e.Item.Font,
                e.TextRectangle,
                color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private float GetHoverLevel(ToolStripItem item)
        {
            float target = item.Selected || item.Pressed ? 1f : 0f;
            if (!MotionEnabled())
            {
                return target;
            }

            float current;
            if (!hoverLevels.TryGetValue(item, out current))
            {
                current = 0f;
                hoverLevels[item] = current;
            }

            if (Math.Abs(current - target) > 0.01f)
            {
                EnsureAnimationTimer();
            }

            return current;
        }

        private void EnsureAnimationTimer()
        {
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
            bool animating = false;
            List<ToolStripItem> items = new List<ToolStripItem>(hoverLevels.Keys);
            foreach (ToolStripItem item in items)
            {
                if (item.Owner == null)
                {
                    hoverLevels.Remove(item);
                    continue;
                }

                float target = item.Selected || item.Pressed ? 1f : 0f;
                float current = hoverLevels[item];
                float next = current + ((target - current) * 0.28f);
                if (Math.Abs(next - target) < 0.025f)
                {
                    next = target;
                }
                else
                {
                    animating = true;
                }

                hoverLevels[item] = next;
                item.Owner.Invalidate(item.Bounds);
            }

            if (!animating)
            {
                animationTimer.Stop();
            }
        }

        private static bool IsPersistentNavigationSelection(ToolStripButton button)
        {
            return button.Name != "MenuConnect" &&
                   button.Name.StartsWith("Menu", StringComparison.Ordinal) &&
                   button.BackColor != Color.Transparent && button.BackColor.A > 0;
        }

        private static bool MotionEnabled()
        {
            return !SystemInformation.HighContrast && !SystemInformation.TerminalServerSession;
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)(from.A + ((to.A - from.A) * amount)),
                (int)(from.R + ((to.R - from.R) * amount)),
                (int)(from.G + ((to.G - from.G) * amount)),
                (int)(from.B + ((to.B - from.B) * amount)));
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
