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
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        enum KeyModifier
        {
            None = 0,
            Alt = 1,
            Control = 2,
            Shift = 4,
            WinKey = 8
        }
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
            RegisterHotKey(this.Handle, 5, (int)KeyModifier.Control, Keys.F.GetHashCode());
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
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312)
            {
                /* Note that the three lines below are not needed if you only want to register one hotkey.
                 * The below lines are useful in case you want to register multiple keys, which you can use a switch with the id as argument, or if you want to know which key/modifier was pressed for some particular reason. */

                Keys key = (Keys)(((int)m.LParam >> 16) & 0xFFFF);                  // The key of the hotkey that was pressed.
                KeyModifier modifier = (KeyModifier)((int)m.LParam & 0xFFFF);       // The modifier of the hotkey that was pressed.
                int id = m.WParam.ToInt32();                                        // The id of the hotkey that was pressed.
                if (id == 1)
                {
                    c1x = System.Windows.Forms.Control.MousePosition.X;
                    c1y = System.Windows.Forms.Control.MousePosition.Y;
                }
                if (id == 2)
                {
                    c2x = System.Windows.Forms.Control.MousePosition.X;
                    c2y = System.Windows.Forms.Control.MousePosition.Y;
                    if (c1x != 0 && c1y != 0)
                    {
                        hndr = Math.Max(Math.Abs(c2y - c1y), Math.Abs(c2x - c1x));
                    }
                }
                if (id == 3)
                {
                    sx = System.Windows.Forms.Control.MousePosition.X;
                    sy = System.Windows.Forms.Control.MousePosition.Y;
                    mdistance = Math.Round(Math.Sqrt(Convert.ToDouble(((tx - sx) * (tx - sx)) + ((ty - sy) * (ty - sy)))) / hndr * 100, 2);
                    label4.Text = mdistance.ToString("#.##");
                }
                if (id == 4)
                {
                    tx = System.Windows.Forms.Control.MousePosition.X;
                    ty = System.Windows.Forms.Control.MousePosition.Y;
                    mdistance = Math.Sqrt(Convert.ToDouble(((tx - sx) * (tx - sx)) + ((ty - sy) * (ty - sy)))) / hndr * 100;
                    label4.Text = mdistance.ToString("#.##");
                }
                if (id == 5)
                {

                    listView1.Items.Clear();
                    double g = 32;
                    double tune = -17;
                    double v0 = 151;




                    int width1 = Screen.PrimaryScreen.Bounds.Width;
                    int height1 = Screen.PrimaryScreen.Bounds.Height;
                    double ratio1 = 16;
                    double ratio2 = height1 / (width1 / ratio1);
                    pixels = (height1 / 2) - System.Windows.Forms.Control.MousePosition.Y;
                    double fov = trackBar1.Value;
                    double vfov = Math.Atan(Math.Tan((fov / 2.0) / 180.0 * 3.14) * ratio2 / ratio1) * 2.0;
                    double angle = Math.Atan(Math.Tan(vfov / 2.0) / (height1 / 2.0) * pixels);
                    double elevation = Math.Tan(angle) * mdistance;
                    elevation = elevation * -1;
                    label6.Text = elevation.ToString("#.##");
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
                    int ctr = 0;
                    var real = new Dictionary<string, double>()
                    {

                    };
                    var solutions = new Dictionary<double, string>();
                    real.Clear();
                    solutions.Clear();
                    for (double i = 85.5; i >= 45.5; i -= 0.5)
                    {
                        double v0x = v0 * (Math.Cos(i / 180.0 * 3.14));
                        double hmax = ((Math.Pow(v0, 2) * Math.Pow((Math.Sin(i / 180.0 * 3.14)), 2)) / (2.0 * g));
                        hmax += tune;
                        hmax += elevation;
                        double x = Convert.ToInt32(angles[Convert.ToInt32(i * 10)]) / 2.0;
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

                            //temp.Item1 = angles[Convert.ToInt32(i * 10)];
                            solutions.Add(Math.Abs(x - mdistance), angles[Convert.ToInt32(i * 10)]);
                            real.Add(angles[Convert.ToInt32(i * 10)], x);
                            solutions = solutions.OrderBy(obj => obj.Key).ToDictionary(obj => obj.Key, obj => obj.Value);
                            ctr++;
                            //listView1.Items.Add("Hit " + x.ToString() + "  Aim: " + angles[Convert.ToInt32(i * 10)]);
                        }
                    }
                    var windowInApplicationIsFocused = Form.ActiveForm != null;
                    if (!windowInApplicationIsFocused)
                    {
                        this.WindowState = FormWindowState.Minimized;
                        this.WindowState = FormWindowState.Normal;
                    }
                    foreach (var item in solutions)
                    {
                        real[item.Value] = Math.Round(real[item.Value], 2);
                        mdistance = Math.Round(mdistance, 2);
                        if (real[item.Value] > mdistance)
                        {
                            listView1.Items.Add(Math.Round(Math.Abs(real[item.Value] - mdistance), 2).ToString() + "m Overshoot.  Aim: " + item.Value);
                        }
                        else if (real[item.Value] < mdistance)
                        {
                            listView1.Items.Add(Math.Round(Math.Abs(real[item.Value] - mdistance), 2).ToString() + "m Short.  Aim: " + item.Value);
                        }
                        else
                        {
                            listView1.Items.Add("Precise Hit. Aim: " + item.Value);
                        }
                    }
                    if (listView1.Items.Count != 0)
                    {
                        listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                        {
                            ForeColor = Color.Green
                        };
                    }
                }
                // do something
            }
        }
        private void Form1_Closing(object sender, FormClosingEventArgs e)
        {
            UnregisterHotKey(this.Handle, 0);
            UnregisterHotKey(this.Handle, 1);
            UnregisterHotKey(this.Handle, 2);
            UnregisterHotKey(this.Handle, 3);
            UnregisterHotKey(this.Handle, 4);
            UnregisterHotKey(this.Handle, 5);
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox1.Checked){
                RegisterHotKey(this.Handle, 1, (int)KeyModifier.Control, Keys.Q.GetHashCode());
                RegisterHotKey(this.Handle, 2, (int)KeyModifier.Control, Keys.W.GetHashCode());
                RegisterHotKey(this.Handle, 3, (int)KeyModifier.Control, Keys.A.GetHashCode());
                RegisterHotKey(this.Handle, 4, (int)KeyModifier.Control, Keys.S.GetHashCode());
            }
            else
            {
                UnregisterHotKey(this.Handle, 0);
                UnregisterHotKey(this.Handle, 1);
                UnregisterHotKey(this.Handle, 2);
                UnregisterHotKey(this.Handle, 3);
                UnregisterHotKey(this.Handle, 4);
                //Application.Restart();
            }
        }
    }
}
