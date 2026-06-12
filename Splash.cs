using System;
using System.Reflection;
using System.Windows.Forms;

namespace MissionPlanner
{
    public partial class Splash : Form
    {
        public Splash()
        {
            InitializeComponent();

            string strVersion = typeof(Splash).GetType().Assembly.GetName().Version.ToString();

            TXT_version.Text = "Version: " + Application.ProductVersion;

            Console.WriteLine(strVersion);

            // JIAC&DI branding - load Jordan flag and logo
            if (pictureBox1 != null)
            {
                pictureBox1.BackgroundImage = MissionPlanner.Properties.Resources.jordan_flag;
                pictureBox1.Image = MissionPlanner.Properties.Resources.jiacdi_splash_logo;
                pictureBox1.Visible = true;
            }

            Console.WriteLine("Splash .ctor - JIAC&DI branding loaded");
        }
    }
}