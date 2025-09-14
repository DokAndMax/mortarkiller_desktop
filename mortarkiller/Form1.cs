using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;


namespace mortarkiller;

public partial class Form1 : Form
{
    private const string MotdUrl = "file_url_lol";
    private static readonly HttpClient client = new HttpClient();
    private Color _currentMarkerColor = ColorTranslator.FromHtml("#d0cb28"); // жовтий
    private int _currentMarkerNumber = 1;
    private bool _readAltNumHotkeys;
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

    private DetectorParams? _gridParams;
    private ParameterSet? _pinParams;
    private TemplateLibrary? _pinTemplate;
    private PinDetector? _pinDetector;
    private PlayerParams? _playersParams;
    private PlayerDetector? _playerDetector;
    private LiveMode? _playerLive;

    private AutoMortarRunner? _auto;

    // TTS для ручного режиму
    private readonly SpeechSynthesizer _tts = new();

    public Form1()
    {
        //the hotkey to open the program and also to calculate elevation
        InitializeComponent();
        LoadMotdAsync();
        this.KeyPreview = true;
        RegisterHotKey(this.Handle, 5, (int)KeyModifier.Alt, Keys.F.GetHashCode());
    }

    //become the active window
    private void pop()
    {
        // просто підняти вікно (логіка активації така сама, як була)
        this.WindowState = FormWindowState.Minimized;
        this.WindowState = FormWindowState.Normal;
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

        // За замовчуванням вибираємо "1" (жовтий)
        if (rbMarker1 != null) rbMarker1.Checked = true;

        InitDetectors();

        if (_pinDetector != null && _pinParams != null && _gridParams != null && _playerLive != null && _playersParams != null)
        {
            _auto = new AutoMortarRunner(
                processName: "TslGame",
                pinDetector: _pinDetector,
                pinParams: _pinParams,
                gridParams: _gridParams,
                playerLive: _playerLive,
                playersParams: _playersParams,
                computeSolutions: ComputeSolutionsAndUpdateUI,
                intervalMs: 200,
                cropTopPercent: 0.08,
                cropSidePercent: 0.47,
                enableDebug: true); // <-- увімкнути дамп

            // Підписка на події — оновлення UI
            _auto.Status += s => BeginInvoke((MethodInvoker)(() => listView1.Items.Add(s)));
            _auto.DistanceReady += m => BeginInvoke((MethodInvoker)(() => label4.Text = m.ToString("#.##")));
            _auto.PxPer100Ready += v => BeginInvoke((MethodInvoker)(() => { /* можна показати десь */ }));
            _auto.PairFound += (pin, marker) => { /* опційно: намалювати/лог */ };
        }
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
        pixels = (height1 / 2) - Control.MousePosition.Y;
        double fov = trackBar1.Value;
        double vfov = Math.Atan(Math.Tan((fov / 2.0) / 180.0 * 3.14) * ratio2 / ratio1) * 2.0;
        double angle = Math.Atan(Math.Tan(vfov / 2.0) / (height1 / 2.0) * pixels);
        double elevation = Math.Tan(angle) * dist;
        elevation = elevation * -1;
        return elevation;
    }

    // Використати конкретний Y піксель на екрані (а не курсор)
    double getElevationByScreenY(double dist, int screenY)
    {
        pixels = (height1 / 2) - screenY;
        double fov = trackBar1.Value;
        double vfov = Math.Atan(Math.Tan((fov / 2.0) / 180.0 * 3.14) * ratio2 / ratio1) * 2.0;
        double angle = Math.Atan(Math.Tan(vfov / 2.0) / (height1 / 2.0) * pixels);
        double elevation = Math.Tan(angle) * dist;
        return -elevation;
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
                c1x = Control.MousePosition.X;
                c1y = Control.MousePosition.Y;
                RegisterHotKey(this.Handle, 2, (int)KeyModifier.Alt, Keys.W.GetHashCode());
                //unlocks the other key
                setq = true;
            }
            if (id == 2)
            {
                c2x = Control.MousePosition.X;
                c2y = Control.MousePosition.Y;
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
                sx = Control.MousePosition.X;
                sy = Control.MousePosition.Y;
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
                tx = Control.MousePosition.X;
                ty = Control.MousePosition.Y;
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
                // якщо гра у фулскріні і не у фокусі “Search” — сфокусувати “Search” і вийти
                var searchWnd = GetSearchWindowHandle();
                var fg = NativeMethods.GetForegroundWindow();

                bool isGameFullScreen = ScreenshotHelper.DetectWindowMode("TslGame") == WindowMode.FullScreen;
                bool notSearchForeground = fg != searchWnd;
                bool needActivateSelf = !IsAppWindowFocused();
                if (notSearchForeground && needActivateSelf)
                {
                    // 1) Якщо гра у фулскріні і не “Search” у фокусі — фокусимо “Search” і виходимо
                    if (isGameFullScreen)
                    {
                        FocusSearchWindow();
                        return;
                    } 
                    else
                    {
                        pop();
                        return;
                    }
                }

                real.Clear();
                solutions.Clear();
                listView1.Items.Clear();

                if (seta && sets && setw)
                {
                    //calculation of firing solution begins
                    double elevation = getElevation(mdistance);
                    if (!notSearchForeground)
                    {
                        // фокус гри (як і було)
                        var tslHandle = Process.GetProcessesByName("TslGame").LastOrDefault()?.MainWindowHandle ?? IntPtr.Zero;
                        if (tslHandle != IntPtr.Zero)
                            NativeMethods.SetForegroundWindow(tslHandle);
                    }
                    Debug.WriteLine($"[MANUAL] mdistance={mdistance}, elevation={elevation}");
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
                            Debug.WriteLine($"[MANUAL] angle={i * 10}, label={angles[Convert.ToInt32(i * 10)]}, x={x:F2}, error={Math.Abs(x - mdistance):F2}");
                            //time between click and impact (time elapsed * 2 + length of the anim)
                            label8.Text = ((t + 2.150 + (mdistance / (2 * v0x))).ToString("#.###"));
                            //very accurate

                            //this part has to do with the fact that pubg has two different angles labeled as 699m
                            //and 700m same thing
                            if (i * 10 == 455)
                            {
                                real.Add("MAXIMUM 700", x);
                                solutions.Add(Math.Abs(x - mdistance), "MAXIMUM 700");
                            }
                            else if (i * 10 == 460)
                            {
                                real.Add("smaller 700", x);
                                solutions.Add(Math.Abs(x - mdistance), "smaller 700");
                            }
                            else if (i * 10 == 465)
                            {
                                real.Add("BIGGER 699", x);
                                solutions.Add(Math.Abs(x - mdistance), "BIGGER 699");
                            }
                            else if (i * 10 == 470)
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

                    if (listView1.Items.Count > 0)
                        Debug.WriteLine($"[MANUAL] Best={listView1.Items[0].Text}");

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

                        var topText = listView1.Items[0].Text;
                        var aimNumber = ExtractAimNumber(topText);
                        if (aimNumber.HasValue)
                            _ = SpeakAsync(aimNumber.Value.ToString());
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

            if (_readAltNumHotkeys)
            {
                switch (id)
                {
                    case 6:
                    case 7:
                    case 8:
                    case 9:
                    case 10:
                    case 11:
                    case 12:
                    case 13:
                        var pinColor = MapHotkeyIdToPinColor(id);
                        var markerColor = MapUiMarkerNumberToColorName(_currentMarkerNumber);
                        _auto?.Start(pinColor, markerColor);
                        break;
                }
            }
        }
    }
    private void Form1_Closing(object sender, FormClosingEventArgs e)
    {
        UnregisterHotKey(this.Handle, 1);
        //UnregisterHotKey(this.Handle, 2);
        UnregisterHotKey(this.Handle, 3);
        UnregisterHotKey(this.Handle, 4);
        UnregisterHotKey(this.Handle, 5);
        // Верхній ряд
        UnregisterHotKey(this.Handle, 6);
        UnregisterHotKey(this.Handle, 7);
        UnregisterHotKey(this.Handle, 8);
        UnregisterHotKey(this.Handle, 9);

        // NumPad
        UnregisterHotKey(this.Handle, 10);
        UnregisterHotKey(this.Handle, 11);
        UnregisterHotKey(this.Handle, 12);
        UnregisterHotKey(this.Handle, 13);

        _auto?.Dispose();
    }

    //hotkeys on/off
    private void checkBox1_CheckedChanged(object sender, EventArgs e)
    {
        if (checkBox1.Checked)
        {
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

    private void markerColor_CheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not RadioButton rb || !rb.Checked)
            return;

        if (!int.TryParse(rb.Text, out var number))
            return;

        _currentMarkerNumber = number;

        // Мапінг: 1-жовтий, 2-помаранчевий, 3-синій, 4-зелений
        _currentMarkerColor = number switch
        {
            1 => ColorTranslator.FromHtml("#d0cb28"), // жовта мітка
            2 => ColorTranslator.FromHtml("#dd7e44"), // помаранчева мітка
            3 => ColorTranslator.FromHtml("#60a6c2"), // синя мітка
            4 => ColorTranslator.FromHtml("#52a44f"), // зелена мітка
            _ => _currentMarkerColor
        };

        markerPreview.Invalidate();
    }

    // Малюємо прев’ю мітки
    private void markerPreview_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

        var bounds = Rectangle.Inflate(markerPreview.ClientRectangle, -1, -1); // 1px всередину
        DrawMarker(e.Graphics, bounds, _currentMarkerColor, _currentMarkerNumber);
    }

    // Універсальний малювальник мітки-кружечка з білим числом і чорним контуром
    private static void DrawMarker(Graphics g, Rectangle bounds, Color fill, int number)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        int size = Math.Min(bounds.Width, bounds.Height);
        var circleRect = new RectangleF(
            bounds.X + (bounds.Width - size) / 2f,
            bounds.Y + (bounds.Height - size) / 2f,
            size, size);

        using (var brush = new SolidBrush(fill))
            g.FillEllipse(brush, circleRect);

        // Підбираємо розмір шрифту ~60% від діаметра кружечка
        float em = circleRect.Height * 0.60f;
        using var path = new GraphicsPath();
        using var ff = new FontFamily("Segoe UI");
        var sf = StringFormat.GenericTypographic;

        // Малюємо текст у (0,0), далі відцентруємо шляхом трансформації
        path.AddString(number.ToString(), ff, (int)FontStyle.Bold, em, new Point(0, 0), sf);

        // Центрування тексту в колі
        var textBounds = path.GetBounds();
        float tx = circleRect.X + (circleRect.Width - textBounds.Width) / 2f - textBounds.X;
        float ty = circleRect.Y + (circleRect.Height - textBounds.Height) / 2f - textBounds.Y;
        using (var m = new Matrix())
        {
            m.Translate(tx, ty);
            path.Transform(m);
        }

        // Біле заповнення + тонкий чорний контур
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, path);

        float penWidth = Math.Max(1f, circleRect.Height / 22f); // тоненький контур
        using (var pen = new Pen(Color.Black, penWidth) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, path);
    }

    private void PaintBorderlessGroupBox(object sender, PaintEventArgs p)
    {
        GroupBox box = (GroupBox)sender;
        p.Graphics.Clear(SystemColors.Control);
        p.Graphics.DrawString(box.Text, box.Font, Brushes.Black, 0, 0);
    }

    private void checkBoxHotkeysAlt1234_CheckedChanged(object? sender, EventArgs e)
    {
        if (checkBoxHotkeysAlt1234.Checked)
        {
            _readAltNumHotkeys = true;

            // Верхній ряд цифр 1–4
            RegisterHotKey(this.Handle, 6, (int)KeyModifier.Alt, Keys.D1.GetHashCode());
            RegisterHotKey(this.Handle, 7, (int)KeyModifier.Alt, Keys.D2.GetHashCode());
            RegisterHotKey(this.Handle, 8, (int)KeyModifier.Alt, Keys.D3.GetHashCode());
            RegisterHotKey(this.Handle, 9, (int)KeyModifier.Alt, Keys.D4.GetHashCode());

            // NumPad 1–4
            RegisterHotKey(this.Handle, 10, (int)KeyModifier.Alt, Keys.NumPad1.GetHashCode());
            RegisterHotKey(this.Handle, 11, (int)KeyModifier.Alt, Keys.NumPad2.GetHashCode());
            RegisterHotKey(this.Handle, 12, (int)KeyModifier.Alt, Keys.NumPad3.GetHashCode());
            RegisterHotKey(this.Handle, 13, (int)KeyModifier.Alt, Keys.NumPad4.GetHashCode());
        }
        else
        {
            _readAltNumHotkeys = false;

            // Верхній ряд
            UnregisterHotKey(this.Handle, 6);
            UnregisterHotKey(this.Handle, 7);
            UnregisterHotKey(this.Handle, 8);
            UnregisterHotKey(this.Handle, 9);

            // NumPad
            UnregisterHotKey(this.Handle, 10);
            UnregisterHotKey(this.Handle, 11);
            UnregisterHotKey(this.Handle, 12);
            UnregisterHotKey(this.Handle, 13);
        }
    }

    private void InitDetectors()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string gridParamsPath = Path.Combine(baseDir, "grid_params.json");
        string pinParamsPath = Path.Combine(baseDir, "pin_best_params.json");
        string playersPath = Path.Combine(baseDir, "players_params.json");

        _gridParams = JsonSerializer.Deserialize<DetectorParams>(
            File.ReadAllText(gridParamsPath),
            JsonOptions());

        _pinParams = ParamsIO.LoadFromBestParamsJson(pinParamsPath);

        string dirOfPinParams = Path.GetDirectoryName(Path.GetFullPath(pinParamsPath)) ?? ".";
        string templateDir = Path.Combine(dirOfPinParams, "best_pin_masks");
        _pinTemplate = Directory.Exists(templateDir) ? TemplateLibrary.LoadFromDir(templateDir) : null;
        _pinDetector = new PinDetector(templates: _pinTemplate);

        _playersParams =JsonSerializer.Deserialize<PlayerParams>(
            File.ReadAllText(playersPath),
            JsonOptions());
        _playerDetector = new PlayerDetector(_playersParams!.CalibratedColors);
        _playerLive = new LiveMode(_playerDetector);
    }

    private PinColor MapHotkeyIdToPinColor(int id) => id switch
    {
        6 or 10 => PinColor.Yellow,
        7 or 11 => PinColor.Orange,
        8 or 12 => PinColor.Blue,
        9 or 13 => PinColor.Green,
        _ => PinColor.Yellow
    };

    private ColorName MapUiMarkerNumberToColorName(int n) => n switch
    {
        1 => ColorName.Yellow,
        2 => ColorName.Orange,
        3 => ColorName.Blue,
        4 => ColorName.Green,
        _ => ColorName.Yellow
    };

    private (bool hasShort, bool hasOver, string bestItemText, string secondItemText, double impactTime)
        ComputeSolutionsAndUpdateUI(double distanceMeters, int? pinScreenY)
    {
        if (InvokeRequired)
            return (ValueTuple<bool, bool, string, string, double>)Invoke(
                new Func<double, int?, (bool, bool, string, string, double)>(ComputeSolutionsAndUpdateUI),
                distanceMeters, pinScreenY);

        // працюємо як у ручному — через mdistance
        mdistance = distanceMeters;

        listView1.Items.Clear();

        // якщо автодетектор передав Y піна — використовуємо його; інакше fallback на курсор
        double elevation = pinScreenY.HasValue
            ? getElevationByScreenY(mdistance, pinScreenY.Value)
            : getElevation(mdistance);
        Debug.WriteLine($"[AUTO] mdistance={mdistance}, elevation={elevation}");

        label6.Text = elevation.ToString("#.##");

        // Далі — без змін, як у твоєму ручному коді:
        var real = new Dictionary<string, double>();
        var solutions = new Dictionary<double, string>();
        double impactTime = 0;

        int ctr = 0;
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
                label8.Text = ((t + 2.150 + (mdistance / (2 * v0x))).ToString("#.###"));
                impactTime = (t + 2.150 + (mdistance / (2 * v0x)));

                string label;
                if (i * 10 == 455) label = "MAXIMUM 700";
                else if (i * 10 == 460) label = "smaller 700";
                else if (i * 10 == 465) label = "BIGGER 699";
                else if (i * 10 == 470) label = "smaller 699";
                else label = angles[Convert.ToInt32(i * 10)];

                Debug.WriteLine($"[AUTO] angle={i * 10}, label={label}, x={x:F2}, error={Math.Abs(x - mdistance):F2}");

                real.Add(label, x);
                solutions.Add(Math.Abs(x - mdistance), label);
                solutions = solutions.OrderBy(obj => obj.Key).ToDictionary(obj => obj.Key, obj => obj.Value);
                ctr++;
            }

            if (listView1.Items.Count > 0)
                Debug.WriteLine($"[AUTO] Best={listView1.Items[0].Text}");
        }

        foreach (var item in solutions)
        {
            real[item.Value] = Math.Round(real[item.Value], 2);
            mdistance = Math.Round(mdistance, 2);
            if (real[item.Value] > mdistance)
                listView1.Items.Add(Math.Round(Math.Abs(real[item.Value] - mdistance), 2).ToString() + "m Overshoot.  Aim: " + item.Value);
            else if (real[item.Value] < mdistance)
                listView1.Items.Add(Math.Round(Math.Abs(real[item.Value] - mdistance), 2).ToString() + "m Short.  Aim: " + item.Value);
            else
                listView1.Items.Add("Precise Hit. Aim: " + item.Value);
        }

        if (listView1.Items.Count != 0)
            listView1.Items[0] = new ListViewItem(listView1.Items[0].Text) { ForeColor = Color.Green };
        else
            listView1.Items.Add("NO FIRING SOLUTION! CANT HIT");

        bool hasShort = false, hasOver = false;
        foreach (ListViewItem it in listView1.Items)
        {
            if (it.Text.Contains("Short", StringComparison.OrdinalIgnoreCase)) hasShort = true;
            if (it.Text.Contains("Overshoot", StringComparison.OrdinalIgnoreCase)) hasOver = true;
        }

        string best = listView1.Items.Count > 0 ? listView1.Items[0].Text : "No solution";
        string second = listView1.Items.Count > 1 ? listView1.Items[1].Text : "";

        return (hasShort, hasOver, best, second, impactTime);
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false) => new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true
    };

    // Повертає вікно Windows Search як в оригінальному коді
    private static IntPtr GetSearchWindowHandle()
    {
        return NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Windows.UI.Core.CoreWindow", "Search");
    }

    private static bool IsAppWindowFocused() => Form.ActiveForm != null;

    // Логіка залишена ідентичною: біп і фокус на "Search"
    private static void FocusSearchWindow()
    {
        var search = GetSearchWindowHandle();
        Console.Beep(); // як було — не змінюю логіку
        if (search != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(search);
    }

    private static int? ExtractAimNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Matches(text, @"\d+");
        if (m.Count == 0) return null;
        return int.TryParse(m[^1].Value, out var v) ? v : (int?)null;
    }

    private Task SpeakAsync(string text)
    {
        var tcs = new TaskCompletionSource<object?>();
        void handler(object? s, SpeakCompletedEventArgs e)
        {
            _tts.SpeakCompleted -= handler;
            tcs.TrySetResult(null);
        }
        _tts.SpeakCompleted += handler;
        _tts.SpeakAsync(text);
        return tcs.Task;
    }
}