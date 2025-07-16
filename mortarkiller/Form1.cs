using System;
using System.Net.Http;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Design;


namespace WINWORD
{
    public partial class Form1 : Form
    {
        private const string MotdUrl = "http://5.61.47.45:9000/motd.txt";
        private static readonly HttpClient client = new HttpClient();
        //system wide hotkey code I stole
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
        //global important variables
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
        bool setq = false;
        bool setw = false;
        bool seta = false;
        bool sets = false;
        double g;
        double tune;
        double v0;
        int width1;
        int height1;
        double ratio1;
        double ratio2;
        //convert what you see on the pubg mortar into an angle (degrees * 10)
        public dynamic angles = new Dictionary<int, string>()
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
        public Form1()
        {
            //the hotkey to open the program and also to calculate elevation
            InitializeComponent();
            LoadMotdAsync();
            this.KeyPreview = true;
            RegisterHotKey(this.Handle, 5, (int)KeyModifier.Alt, Keys.F.GetHashCode());
        }

        //become the active window
        bool pop()
        {
            var windowInApplicationIsFocused = Form.ActiveForm != null;
            if (!windowInApplicationIsFocused)
            {
                this.WindowState = FormWindowState.Minimized;
                this.WindowState = FormWindowState.Normal;
                return true;
            }
            else
            {
                return false;
            }
        }
        private async Task LoadMotdAsync()
        {
            try
            {
                // Use await to get the actual string
                string motdContent = await client.GetStringAsync(MotdUrl);

                // Update UI safely
                if (motdTextBox.InvokeRequired)
                {
                    motdTextBox.Invoke((MethodInvoker)(() => motdTextBox.Text = motdContent));
                }
                else
                {
                    motdTextBox.Text = motdContent;
                }
            }
            catch (HttpRequestException ex)
            {
                string errorMessage = $"Server error: {ex.Message}";
                motdTextBox.Invoke((MethodInvoker)(() => motdTextBox.Text = errorMessage));
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error: {ex.Message}";
                motdTextBox.Invoke((MethodInvoker)(() => motdTextBox.Text = errorMessage));
            }
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            //listView is the output, contains both firing solutions and help cues for user
            listView1.Clear();
            listView1.Items.Add("SET MAP SCALE! Alt+Q, Alt+W");
            listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
            {
                ForeColor = Color.DarkRed
            };
            listView1.Items.Add("KEYBINDS NOT ENABLED!");
            //PUBG specific physics constants
            g = 32;
            //tune explained later
            tune = -17;
            //starting speed
            v0 = 151;
            //get screen res and ratio
            width1 = Screen.PrimaryScreen.Bounds.Width;
            height1 = Screen.PrimaryScreen.Bounds.Height;
            ratio1 = 16;
            ratio2 = height1 / (width1 / ratio1);
            //Message of the day
            //example path
        }
        

        //I dont think I actually use this function here,
        //but you can feed distance and elevation into it and get pubg mortar output
        public int smallcalc(double dist, double elev)
        {
            double minErr = 10;
            string aim = "000";
            for (double i = 85.5; i >= 45.5; i -= 0.5)
            {
                double v0x = v0 * (Math.Cos(i / 180.0 * 3.14));
                double hmax = ((Math.Pow(v0, 2) * Math.Pow((Math.Sin(i / 180.0 * 3.14)), 2)) / (2.0 * g));
                hmax += tune;
                hmax += elev;
                double x = Convert.ToInt32(angles[Convert.ToInt32(i * 10)]) / 2.0;
                double t = 0.0;
                while (hmax >= 0)
                {
                    double vy = g * t;
                    t += 0.01;
                    x += (v0x * 0.01);
                    hmax -= (vy * 0.01);
                }
                if (Math.Abs(x - dist) < minErr)
                {
                    minErr = Math.Abs(x - dist);
                    aim = angles[Convert.ToInt32(i * 10)];
                }
            }
            return Convert.ToInt32(aim);
        }
        
        //da fov slider
        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            label1.Text = trackBar1.Value.ToString();
        }
        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            //if I remove this it doesnt compile
            //lol
        }
        double getElevation(double dist)
        {
            //this gets elevation from distance and angle
            //angle is calculated based on camera fov and resolution
            //you know how many pixels per degree, so you know how many degrees you got
            //angle and distance gets you the other side of the triangle
            pixels = (height1 / 2) - System.Windows.Forms.Control.MousePosition.Y;
            double fov = trackBar1.Value;
            double vfov = Math.Atan(Math.Tan((fov / 2.0) / 180.0 * 3.14) * ratio2 / ratio1) * 2.0;
            double angle = Math.Atan(Math.Tan(vfov / 2.0) / (height1 / 2.0) * pixels);
            double elevation = Math.Tan(angle) * dist;
            elevation = elevation * -1;
            return elevation;
        }

        //OLD YOUTUBE TUTORIAL
        //NEEDS UPDATE
        //private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        //{
        //    System.Diagnostics.Process.Start("https://youtu.be/8PT1eohjcSA");
        //}

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312)
            {
                /* Note that the three lines below are not needed if you only want to register one hotkey.
                 * The below lines are useful in case you want to register multiple keys, which you can use a switch with the id as argument, or if you want to know which key/modifier was pressed for some particular reason. */

                Keys key = (Keys)(((int)m.LParam >> 16) & 0xFFFF);                  // The key of the hotkey that was pressed.
                KeyModifier modifier = (KeyModifier)((int)m.LParam & 0xFFFF);       // The modifier of the hotkey that was pressed.
                int id = m.WParam.ToInt32();
                if (id == 1)
                {
                    //first key to set scale (alt q)
                    c1x = System.Windows.Forms.Control.MousePosition.X;
                    c1y = System.Windows.Forms.Control.MousePosition.Y;
                    RegisterHotKey(this.Handle, 2, (int)KeyModifier.Alt, Keys.W.GetHashCode());
                    //unlocks the other key
                    setq = true;
                }
                if (id == 2)
                {
                    c2x = System.Windows.Forms.Control.MousePosition.X;
                    c2y = System.Windows.Forms.Control.MousePosition.Y;
                    if (setq)
                    {
                        //scale is not set!
                        setw = true;
                        mdistance = 0;
                        seta = false;
                        sets = false;
                        label4.Text = "";
                        listView1.Items.Clear();
                        UnregisterHotKey(this.Handle, 2);
                        listView1.Items.Add("Set distance now Alt+A, Alt+S");
                        setq = false;
                        if (c1x != 0 && c1y != 0)
                        {
                            hndr = Math.Max(Math.Abs(c2y - c1y), Math.Abs(c2x - c1x));
                            //how many pixels per 100m on screen
                        }
                    }
                }
                if (id == 3)
                {
                    //set mortar (player) position
                    sx = System.Windows.Forms.Control.MousePosition.X;
                    sy = System.Windows.Forms.Control.MousePosition.Y;
                    seta = true;
                    if (setw && sets)
                    {
                        listView1.Items.Clear();
                        listView1.Items.Add("Now use cursor and Alt+F to input angle");
                        mdistance = Math.Round(Math.Sqrt(Convert.ToDouble(((tx - sx) * (tx - sx)) + ((ty - sy) * (ty - sy)))) / hndr * 100, 2);
                        label4.Text = mdistance.ToString("#.##");
                    }
                }
                if (id == 4)
                {
                    //set target position
                    //if target updates, it is assumed mortar pos is the same as before. Vice versa too.
                    tx = System.Windows.Forms.Control.MousePosition.X;
                    ty = System.Windows.Forms.Control.MousePosition.Y;
                    sets = true;
                    if (setw && seta)
                    {
                        listView1.Items.Clear();
                        listView1.Items.Add("Now use cursor and Alt+F to input angle");
                        mdistance = Math.Sqrt(Convert.ToDouble(((tx - sx) * (tx - sx)) + ((ty - sy) * (ty - sy)))) / hndr * 100;
                        label4.Text = mdistance.ToString("#.##");
                    }
                }
                //this thing is for sorting the firing solutions by error. I forgot how it really works
                var real = new Dictionary<string, double>()
                {

                };
                var solutions = new Dictionary<double, string>();
                real.Clear();
                solutions.Clear();
                if (id == 5)
                {
                    //the altf hotkey
                    //if window not active, becomes active. If window active - gives you the elevation calculation for your cursor.
                    if (pop())
                    {
                        return;
                    }
                    real.Clear();
                    solutions.Clear();
                    listView1.Items.Clear();

                    if (seta && sets && setw)
                    {
                        //calculation of firing solution begins
                        double elevation = getElevation(mdistance);
                        //it only takes distance arg because it reads your cursor pos inside the function
                        label6.Text = elevation.ToString("#.##");
                        int ctr = 0;
                        for (double i = 85.5; i >= 45.5; i -= 0.5)
                        {
                            //iterate through EVERY possible firing angle in PUBG mortar
                            double v0x = v0 * (Math.Cos(i / 180.0 * 3.14));
                            double hmax = ((Math.Pow(v0, 2) * Math.Pow((Math.Sin(i / 180.0 * 3.14)), 2)) / (2.0 * g));
                            hmax += tune;
                            //so this part is weird. Before I did this it overshot when shooting close and overshot when shooting far, or vice versa, idk.
                            //and adjusting elevation is a way to affect both of those. Its pretty dead on now. My best guess is this has to do with the fact that
                            //mortar projectile does not spawn right in the tube, spawns a bit higher I guess
                            hmax += elevation;
                            //and the hmax is weird. Its called hmax because I start simulating the projectile from the peak height
                            //but it is really just the Y of the shell. 

                            //I take the distance pubg gives you, halve it. Calculate the hmax by formula and THAT is my starting point.
                            //I guess less simulation time is a way to accumulate less error. I think it works.
                            //DRAWBACK: Cant target anything before the peak of the parabola, cant use the mortar as direct fire cannon for attacking skyscraper
                            //not that bad

                            double x = Convert.ToInt32(angles[Convert.ToInt32(i * 10)]) / 2.0;
                            double t = 0.0;
                            //physics sim
                            while (hmax >= 0)
                            {
                                double vy = g * t;
                                t += 0.01;
                                x += (v0x * 0.01);
                                hmax -= (vy * 0.01);
                            }
                            //IF HIT IS SOMEWHAT CLOSE
                            if (Math.Abs(x - mdistance) < 10)
                            {
                                //time between click and impact (time elapsed * 2 + length of the anim)
                                label8.Text = ((t + 2.150 + (mdistance / (2 * v0x))).ToString("#.###"));
                                //very accurate

                                //this part has to do with the fact that pubg has two different angles labeled as 699m
                                //and 700m same thing
                                if (i*10 == 455)
                                {
                                    real.Add("MAXIMUM 700", x);
                                    solutions.Add(Math.Abs(x - mdistance), "MAXIMUM 700");
                                }
                                else if (i*10 == 460) {
                                    real.Add("smaller 700", x);
                                    solutions.Add(Math.Abs(x - mdistance), "smaller 700");
                                }
                                else if (i*10 == 465)
                                {
                                    real.Add("BIGGER 699", x);
                                    solutions.Add(Math.Abs(x - mdistance), "BIGGER 699");
                                }
                                else if (i*10 == 470)
                                {
                                    real.Add("smaller 699", x);
                                    solutions.Add(Math.Abs(x - mdistance), "smaller 699");
                                }
                                else
                                {
                                    //any other angle with unique pubg display distance
                                    real.Add(angles[Convert.ToInt32(i * 10)], x);
                                    solutions.Add(Math.Abs(x - mdistance), angles[Convert.ToInt32(i * 10)]);
                                }
                                //sort by how good the hit is
                                solutions = solutions.OrderBy(obj => obj.Key).ToDictionary(obj => obj.Key, obj => obj.Value);
                                ctr++;
                            }
                        }

                        foreach (var item in solutions)
                        {
                            //sort formatting
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
                                //if error is 0.00000
                                //never happens lol
                                listView1.Items.Add("Precise Hit. Aim: " + item.Value);
                            }
                        }
                        if (listView1.Items.Count != 0)
                        {
                            //make best firing solution listed as first GREEN and sexy
                            listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                            {
                                ForeColor = Color.Green
                            };
                        }
                        else
                        {
                            listView1.Items.Add("NO FIRING SOLUTION! CANT HIT");
                        }
                        if (!checkBox1.Checked)
                        {
                            //if user tries to do stuff with the keybinds turned off give a warn
                            listView1.Clear();
                            listView1.Items.Add("DISTANCE NOT SET!");
                            listView1.Items.Add("KEYBINDS NOT ENABLED!");
                            listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                            {
                                ForeColor = Color.DarkRed
                            };
                            listView1.Items[1] = new ListViewItem(listView1.Items[1].Text)
                            {
                                ForeColor = Color.DarkOrange
                            };
                        }
                    }
                    else
                    {
                        //dynamic tips at different aiming stages
                        if (!setw)
                        {
                            listView1.Clear();
                            listView1.Items.Add("SET MAP SCALE! LAlt+Q, LAlt+W");
                            listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                            {
                                ForeColor = Color.DarkRed
                            };
                        }
                        else
                        {
                            listView1.Clear();
                            listView1.Items.Add("DISTANCE NOT SET!");
                            listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                            {
                                ForeColor = Color.DarkRed
                            };
                        }
                        if (!checkBox1.Checked)
                        {
                            listView1.Items.Add("KEYBINDS NOT ENABLED!");
                        }
                    }
                    

                }
                // do something
            }
        }
        private void Form1_Closing(object sender, FormClosingEventArgs e)
        {
            UnregisterHotKey(this.Handle, 1);
            //UnregisterHotKey(this.Handle, 2);
            UnregisterHotKey(this.Handle, 3);
            UnregisterHotKey(this.Handle, 4);
            UnregisterHotKey(this.Handle, 5);
        }

        //hotkeys on/off
        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox1.Checked){
                RegisterHotKey(this.Handle, 1, (int)KeyModifier.Alt, Keys.Q.GetHashCode());
                //RegisterHotKey(this.Handle, 2, (int)KeyModifier.Control, Keys.W.GetHashCode());
                RegisterHotKey(this.Handle, 3, (int)KeyModifier.Alt, Keys.A.GetHashCode());
                RegisterHotKey(this.Handle, 4, (int)KeyModifier.Alt, Keys.S.GetHashCode());

                listView1.Items.RemoveAt(listView1.Items.Count - 1);
            }
            else
            {
                UnregisterHotKey(this.Handle, 1);
                //UnregisterHotKey(this.Handle, 2);
                UnregisterHotKey(this.Handle, 3);
                UnregisterHotKey(this.Handle, 4);
                listView1.Clear();
                mdistance = 0;
                sets = false;
                label4.Text = "";
                listView1.Items.Add("DISTANCE NOT SET!");
                listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                {
                    ForeColor = Color.DarkRed
                };
                listView1.Items.Add("KEYBINDS NOT ENABLED!");
                listView1.Items[0] = new ListViewItem(listView1.Items[0].Text)
                {
                    ForeColor = Color.DarkRed
                };
                //Application.Restart();
            }
        }

    }
}

