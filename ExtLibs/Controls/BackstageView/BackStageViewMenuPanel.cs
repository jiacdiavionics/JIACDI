using System.Drawing;
using System.Windows.Forms;

namespace MissionPlanner.Controls.BackstageView
{
    public class BackStageViewMenuPanel : Panel
    {
        internal Color GradColor = Color.White;
        internal Color PencilBorderColor = Color.White;

        public BackStageViewMenuPanel()
        {
            this.SetStyle(ControlStyles.UserPaint, true);

            HorizontalScroll.Enabled = false;
            HorizontalScroll.Visible = false;
            HorizontalScroll.Maximum = 0;
            HScroll = false;
            AutoScroll = true;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            base.OnPaintBackground(pevent);

            using (Pen border = new Pen(PencilBorderColor))
            {
                pevent.Graphics.DrawLine(border, Width - 1, 0, Width - 1, Height);
            }
        }

        protected override void OnResize(System.EventArgs eventargs)
        {
            base.OnResize(eventargs);
            this.Invalidate();
        }

        public void PaintBackground(PaintEventArgs pevent)
        {
            OnPaintBackground(pevent);
        }
    }
}
