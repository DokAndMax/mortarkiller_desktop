using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace mortarkiller
{
    public partial class Form1 : Form
    {
        int c1x = 0;
        int c2x = 0;
        int c1y = 0;
        int c2y = 0;
        int hndr = 0;
        int sx = 0;
        int sy = 0;
        int tx = 0;
        int ty = 0;
        int pixels = 0;
        Double mdistance = 0;
        public Form1()
        {
            InitializeComponent();
            this.KeyPreview = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            
            if (keyData == (Keys.Control | Keys.Q))
            {
                c1x = System.Windows.Forms.Control.MousePosition.X;
                c1y = System.Windows.Forms.Control.MousePosition.Y;
                return true;
            }
            if (keyData == (Keys.Control | Keys.W))
            {
                c2x = System.Windows.Forms.Control.MousePosition.X;
                c2y = System.Windows.Forms.Control.MousePosition.Y;
                if (c1x != 0 && c1y != 0)
                {
                    hndr = Math.Max(Math.Abs(c2y - c1y), Math.Abs(c2x - c1x));
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.A))
            {
                sx = System.Windows.Forms.Control.MousePosition.X;
                sy = System.Windows.Forms.Control.MousePosition.Y;
                mdistance = Math.Round(Math.Sqrt(Convert.ToDouble(((tx - sx) * (tx - sx)) + ((ty - sy) * (ty - sy)))) / hndr * 100, 2);
                label4.Text = mdistance.ToString();
                return true;
            }
            if (keyData == (Keys.Control | Keys.S))
            {
                tx = System.Windows.Forms.Control.MousePosition.X;
                ty = System.Windows.Forms.Control.MousePosition.Y;
                mdistance = Math.Sqrt(Convert.ToDouble(((tx - sx) * (tx - sx)) + ((ty - sy) * (ty - sy)))) / hndr * 100;
                label4.Text = mdistance.ToString();
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                listView1.Items.Clear();
                double g = 32;
                double tune = -16;
                double v0 = 151;




                int width1 = Screen.PrimaryScreen.Bounds.Width;
                int height1 = Screen.PrimaryScreen.Bounds.Height;
                double ratio1 = 16;
                double ratio2 = height1 / (width1 / ratio1);
                pixels = (height1 / 2) - System.Windows.Forms.Control.MousePosition.Y;
                double fov = trackBar1.Value;
                double vfov = Math.Atan(Math.Tan((fov / 2.0) / 180.0 * 3.14) * ratio2 / ratio1);
                double angle = Math.Atan(Math.Tan(vfov / 2.0) / (height1 / 2.0) * pixels);
                double elevation = Math.Tan(angle) * mdistance;
                elevation = elevation * -2.23404255;
                label6.Text = elevation.ToString();
                var angles = new Dictionary<int, string>()
                {
                    { 855, "121"},
                    { 850, "133"},
                    { 845, "145"},
                    { 840, "157"},
                    { 835, "169"},
                    { 830, "181"},
                    { 825, "193"},
                    { 820, "204"},
                    { 815, "216"},
                    { 810, "228"},
                    { 805, "239"},
                    { 800, "250"},
                    { 795, "262"},
                    { 790, "273"},
                    { 785, "284"},
                    { 780, "295"},
                    { 775, "307"},
                    { 770, "317"},
                    { 765, "328"},
                    { 760, "339"},
                    { 755, "350"},
                    { 750, "360"},
                    { 745, "371"},
                    { 740, "381"},
                    { 735, "391"},
                    { 730, "401"},
                    { 725, "411"},
                    { 720, "421"},
                    { 715, "431"},
                    { 710, "440"},
                    { 705, "450"},
                    { 700, "459"},
                    { 695, "468"},
                    { 690, "477"},
                    { 685, "486"},
                    { 680, "495"},
                    { 675, "503"},
                    { 670, "512"},
                    { 665, "520"},
                    { 660, "528"},
                    { 655, "536"},
                    { 650, "544"},
                    { 645, "551"},
                    { 640, "559"},
                    { 635, "566"},
                    { 630, "573"},
                    { 625, "580"},
                    { 620, "587"},
                    { 615, "593"},
                    { 610, "600"},
                    { 605, "606"},
                    { 600, "612"},
                    { 595, "618"},
                    { 590, "624"},
                    { 585, "629"},
                    { 580, "634"},
                    { 575, "639"},
                    { 570, "644"},
                    { 565, "649"},
                    { 560, "653"},
                    { 555, "658"},
                    { 550, "662"},
                    { 545, "666"},
                    { 540, "669"},
                    { 535, "673"},
                    { 530, "676"},
                    { 525, "679"},
                    { 520, "682"},
                    { 515, "685"},
                    { 510, "687"},
                    { 505, "689"},
                    { 500, "691"},
                    { 495, "693"},
                    { 490, "695"},
                    { 485, "696"},
                    { 480, "697"},
                    { 475, "698"},
                    { 470, "699"},
                    { 465, "699"},
                    { 460, "700"},
                    { 455, "700"},

                };
                for (double i = 85.5; i >= 45.5; i-=0.5)
                {
                    double v0x = v0 * (Math.Cos(i / 180.0 * 3.14));
                    double hmax = ((Math.Pow(v0, 2) * Math.Pow((Math.Sin(i / 180.0 * 3.14)), 2)) / (2.0 * g));
                    hmax += tune;
                    hmax += elevation;
                    double x = Convert.ToInt32(angles[Convert.ToInt32(i*10)]) / 2.0;
                    double t = 0.0;
                    while (hmax >= 0)
                    {
                        double vy = g * t;
                        t += 0.01;
                        x += (v0x * 0.01);
                        hmax -= (vy * 0.01);
                    }
                    if (Math.Abs(x - mdistance) < 10)
                    {
                        listView1.Items.Add("Hit " + x.ToString() + "  Aim: " + angles[Convert.ToInt32(i * 10)]);
                    }
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            label1.Text = trackBar1.Value.ToString();
        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://youtu.be/8PT1eohjcSA");
        }
    }
}
