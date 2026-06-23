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
            Console.WriteLine("Splash .ctor - using jiacdi_splashbg background only");
        }
    }
}