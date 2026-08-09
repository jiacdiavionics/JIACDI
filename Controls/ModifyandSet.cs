using System;
using System.Windows.Forms;

namespace MissionPlanner.Controls
{
    public partial class ModifyandSet : UserControl
    {
        private bool updatingLayout;

        [System.ComponentModel.Browsable(false)]
        public NumericUpDown NumericUpDown
        {
            get { return numericUpDown1; }
        }

        [System.ComponentModel.Browsable(false)]
        public MyButton Button
        {
            get { return myButton1; }
        }

        [System.ComponentModel.Browsable(true)]
        public String ButtonText
        {
            get { return Button.Text; }
            set { Button.Text = value; }
        }

        [System.ComponentModel.Browsable(true)]
        public Decimal Increment
        {
            get { return NumericUpDown.Increment; }
            set { NumericUpDown.Increment = value; }
        }

        [System.ComponentModel.Browsable(true)]
        public int DecimalPlaces
        {
            get { return NumericUpDown.DecimalPlaces; }
            set { NumericUpDown.DecimalPlaces = value; }
        }

        [System.ComponentModel.Browsable(true)]
        public Decimal Value
        {
            get { return NumericUpDown.Value; }
            set { NumericUpDown.Value = value; }
        }

        [System.ComponentModel.Browsable(true)]
        public Decimal Minimum
        {
            get { return NumericUpDown.Minimum; }
            set { NumericUpDown.Minimum = value; }
        }

        [System.ComponentModel.Browsable(true)]
        public Decimal Maximum
        {
            get { return NumericUpDown.Maximum; }
            set { NumericUpDown.Maximum = value; }
        }

        public new event EventHandler Click;
        public event EventHandler ValueChanged;

        public ModifyandSet()
        {
            InitializeComponent();
            flowLayoutPanel1.AutoSize = false;
            flowLayoutPanel1.WrapContents = false;
            numericUpDown1.Margin = Padding.Empty;
            myButton1.AutoSize = false;
            myButton1.Margin = Padding.Empty;
            flowLayoutPanel1.Resize += (sender, args) => UpdateResponsiveLayout();
            UpdateResponsiveLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            UpdateResponsiveLayout();
        }

        private void UpdateResponsiveLayout()
        {
            if (updatingLayout || flowLayoutPanel1.ClientSize.Width <= 0)
            {
                return;
            }

            updatingLayout = true;
            try
            {
                int preferredNumericWidth = Math.Max(44, Math.Min(58, flowLayoutPanel1.ClientSize.Width / 3));
                int numericWidth = Math.Min(preferredNumericWidth,
                    Math.Max(0, flowLayoutPanel1.ClientSize.Width - 32));
                numericUpDown1.Width = numericWidth;
                myButton1.Width = Math.Max(0, flowLayoutPanel1.ClientSize.Width - numericWidth);
                myButton1.Height = Math.Max(23, flowLayoutPanel1.ClientSize.Height);
            }
            finally
            {
                updatingLayout = false;
            }
        }

        private void myButton1_Click(object sender, EventArgs e)
        {
            if (Click != null)
                Click(sender, e);
        }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            if (ValueChanged != null)
                ValueChanged(sender, e);
        }
    }
}
