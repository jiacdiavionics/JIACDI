using System;
using System.Drawing;
using System.Windows.Forms;

namespace MissionPlanner.Controls.BackstageView
{
    public class BackstageViewButton : Control
    {
        private bool _isSelected;

        internal Color ContentPageColor = Color.Gray;
        internal Color PencilBorderColor = Color.White;
        internal Color SelectedTextColor = Color.White;
        internal Color UnSelectedTextColor = Color.Gray;
        internal Color HighlightColor1 = SystemColors.Highlight;
        internal Color HighlightColor2 = SystemColors.MenuHighlight;
        private bool _isMouseOver;

        //internal Color HighlightColor1 = Color.FromArgb(0x94, 0xc1, 0x1f);
        //internal Color HighlightColor2 = Color.FromArgb(0xcd, 0xe2, 0x96);

        public BackstageViewButton()
        {
            this.SuspendLayout();

            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);

            this.Width = 150;
            this.Height = 36;
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            
            this.ResumeLayout(false);
        }

        /// <summary>
        /// Whether this button should show the selected style
        /// </summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;

                    this.Invalidate();
                }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            base.OnPaintBackground(pevent);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            if (this.Parent != null)
            {
                ((BackStageViewMenuPanel)this.Parent).PaintBackground(pevent);
            }

            Graphics graphics = pevent.Graphics;
            Rectangle bounds = new Rectangle(0, 1, Math.Max(1, Width - 1), Math.Max(1, Height - 2));

            if (_isSelected)
            {
                using (SolidBrush selected = new SolidBrush(Color.FromArgb(48, HighlightColor1)))
                using (SolidBrush accent = new SolidBrush(HighlightColor1))
                {
                    graphics.FillRectangle(selected, bounds);
                    graphics.FillRectangle(accent, 0, 4, 3, Math.Max(1, Height - 8));
                }
            }
            else if (_isMouseOver)
            {
                using (SolidBrush hover = new SolidBrush(Color.FromArgb(32, Color.White)))
                {
                    graphics.FillRectangle(hover, bounds);
                }
            }

            using (Font textFont = new Font("Segoe UI", 9F,
                       _isSelected ? FontStyle.Bold : FontStyle.Regular))
            {
                TextRenderer.DrawText(
                    graphics,
                    Text,
                    textFont,
                    new Rectangle(14, 0, Math.Max(1, Width - 22), Height),
                    _isSelected ? SelectedTextColor : UnSelectedTextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }


        protected override void OnMouseEnter(EventArgs e)
        {
            _isMouseOver = true;
            base.OnMouseEnter(e);
            this.Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isMouseOver = false;
            base.OnMouseLeave(e);
            this.Invalidate();

        }

        /*
        // This IS necessary for transparency - windows only..... remove it
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }
         */
    }
}
