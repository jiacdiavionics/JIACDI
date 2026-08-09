using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TimeSpan = System.TimeSpan;

namespace MissionPlanner.Controls
{
    public partial class Status : UserControl
    {
        private System.Threading.Timer _hidetimer;
        private double _percent = 50;
        public double Percent
        {
            get { return _percent;}
            set
            {
                if (value < 0 || value > 100)
                    return;

                _percent = value;
                this.BeginInvoke((Action) delegate { this.Visible = true; });
                _hidetimer.Change(TimeSpan.FromSeconds(10), TimeSpan.Zero);
                this.Invalidate();
            }
        }
        public Status()
        {
            InitializeComponent();

            CreateHandle();

            _hidetimer = new System.Threading.Timer(state =>
            {
                this.BeginInvoke((Action) delegate { this.Visible = false; });
            }, null, 1, -1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_percent > 100)
                _percent = 50;

            try
            {
                using (Brush bgBrush = new SolidBrush(Utilities.ModernUi.SurfaceRaised))
                {
                    e.Graphics.FillRectangle(bgBrush, 0, 0, Width, Height);
                }
                
                using (Brush progressBrush = new SolidBrush(Utilities.ModernUi.AccentBright))
                {
                    e.Graphics.FillRectangle(progressBrush, 0, 0, (float)(Width * (_percent / 100.0)), Height);
                }
                
                using (Pen borderPen = new Pen(Utilities.ModernUi.Border, 1))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
                }
            }
            catch (OverflowException)
            {

            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (Brush bgBrush = new SolidBrush(Utilities.ModernUi.Surface))
            {
                e.Graphics.FillRectangle(bgBrush, e.ClipRectangle);
            }
        }
    }
}
