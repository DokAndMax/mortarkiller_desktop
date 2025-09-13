
using System.Windows.Forms;

namespace mortarkiller
{
    partial class Form1
    {
        /// <summary>
        /// Обязательная переменная конструктора.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Освободить все используемые ресурсы.
        /// </summary>
        /// <param name="disposing">истинно, если управляемый ресурс должен быть удален; иначе ложно.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Код, автоматически созданный конструктором форм Windows

        /// <summary>
        /// Требуемый метод для поддержки конструктора — не изменяйте 
        /// содержимое этого метода с помощью редактора кода.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            groupBoxMarkerColor = new GroupBox();
            rbMarker1 = new RadioButton();
            rbMarker2 = new RadioButton();
            rbMarker3 = new RadioButton();
            rbMarker4 = new RadioButton();
            markerPreview = new PictureBox();
            checkBoxHotkeysAlt1234 = new CheckBox();
            label3 = new Label();
            trackBar1 = new TrackBar();
            label1 = new Label();
            label2 = new Label();
            label4 = new Label();
            label5 = new Label();
            label6 = new Label();
            listView1 = new ListView();
            checkBox1 = new CheckBox();
            label7 = new Label();
            label8 = new Label();
            label9 = new Label();
            label10 = new Label();
            label11 = new Label();
            motdTextBox = new RichTextBox();
            groupBoxMarkerColor.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)markerPreview).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackBar1).BeginInit();
            SuspendLayout();
            // 
            // groupBoxMarkerColor
            // 
            groupBoxMarkerColor.Controls.Add(rbMarker1);
            groupBoxMarkerColor.Controls.Add(rbMarker2);
            groupBoxMarkerColor.Controls.Add(rbMarker3);
            groupBoxMarkerColor.Controls.Add(rbMarker4);
            groupBoxMarkerColor.Controls.Add(markerPreview);
            groupBoxMarkerColor.Location = new System.Drawing.Point(3, 30);
            groupBoxMarkerColor.Name = "groupBoxMarkerColor";
            groupBoxMarkerColor.Size = new System.Drawing.Size(220, 37);
            groupBoxMarkerColor.TabIndex = 21;
            groupBoxMarkerColor.TabStop = false;
            groupBoxMarkerColor.Text = "Player marker";
            groupBoxMarkerColor.Paint += PaintBorderlessGroupBox;
            // 
            // rbMarker1
            // 
            rbMarker1.AutoSize = true;
            rbMarker1.Location = new System.Drawing.Point(10, 14);
            rbMarker1.Name = "rbMarker1";
            rbMarker1.Size = new System.Drawing.Size(31, 19);
            rbMarker1.TabIndex = 0;
            rbMarker1.TabStop = true;
            rbMarker1.Text = "1";
            rbMarker1.UseVisualStyleBackColor = true;
            rbMarker1.CheckedChanged += markerColor_CheckedChanged;
            // 
            // rbMarker2
            // 
            rbMarker2.AutoSize = true;
            rbMarker2.Location = new System.Drawing.Point(55, 14);
            rbMarker2.Name = "rbMarker2";
            rbMarker2.Size = new System.Drawing.Size(31, 19);
            rbMarker2.TabIndex = 1;
            rbMarker2.TabStop = true;
            rbMarker2.Text = "2";
            rbMarker2.UseVisualStyleBackColor = true;
            rbMarker2.CheckedChanged += markerColor_CheckedChanged;
            // 
            // rbMarker3
            // 
            rbMarker3.AutoSize = true;
            rbMarker3.Location = new System.Drawing.Point(100, 14);
            rbMarker3.Name = "rbMarker3";
            rbMarker3.Size = new System.Drawing.Size(31, 19);
            rbMarker3.TabIndex = 2;
            rbMarker3.TabStop = true;
            rbMarker3.Text = "3";
            rbMarker3.UseVisualStyleBackColor = true;
            rbMarker3.CheckedChanged += markerColor_CheckedChanged;
            // 
            // rbMarker4
            // 
            rbMarker4.AutoSize = true;
            rbMarker4.Location = new System.Drawing.Point(145, 14);
            rbMarker4.Name = "rbMarker4";
            rbMarker4.Size = new System.Drawing.Size(31, 19);
            rbMarker4.TabIndex = 3;
            rbMarker4.TabStop = true;
            rbMarker4.Text = "4";
            rbMarker4.UseVisualStyleBackColor = true;
            rbMarker4.CheckedChanged += markerColor_CheckedChanged;
            // 
            // markerPreview
            // 
            markerPreview.Location = new System.Drawing.Point(186, 10);
            markerPreview.Name = "markerPreview";
            markerPreview.Size = new System.Drawing.Size(28, 28);
            markerPreview.SizeMode = PictureBoxSizeMode.StretchImage;
            markerPreview.TabIndex = 4;
            markerPreview.TabStop = false;
            markerPreview.Paint += markerPreview_Paint;
            // 
            // checkBoxHotkeysAlt1234
            // 
            checkBoxHotkeysAlt1234.AutoSize = true;
            checkBoxHotkeysAlt1234.Location = new System.Drawing.Point(13, 69);
            checkBoxHotkeysAlt1234.Name = "checkBoxHotkeysAlt1234";
            checkBoxHotkeysAlt1234.Size = new System.Drawing.Size(163, 19);
            checkBoxHotkeysAlt1234.TabIndex = 15;
            checkBoxHotkeysAlt1234.Text = "Read hotkeys (Alt+1,2,3,4)";
            checkBoxHotkeysAlt1234.UseVisualStyleBackColor = true;
            checkBoxHotkeysAlt1234.CheckedChanged += checkBoxHotkeysAlt1234_CheckedChanged;
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new System.Drawing.Point(9, 120);
            label3.Margin = new Padding(2, 0, 2, 0);
            label3.Name = "label3";
            label3.Size = new System.Drawing.Size(78, 15);
            label3.TabIndex = 9;
            label3.Text = "In-Game FOV";
            // 
            // trackBar1
            // 
            trackBar1.Location = new System.Drawing.Point(12, 149);
            trackBar1.Margin = new Padding(2);
            trackBar1.Maximum = 103;
            trackBar1.Minimum = 80;
            trackBar1.Name = "trackBar1";
            trackBar1.Size = new System.Drawing.Size(177, 45);
            trackBar1.TabIndex = 10;
            trackBar1.Value = 103;
            trackBar1.Scroll += trackBar1_Scroll;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new System.Drawing.Point(77, 185);
            label1.Margin = new Padding(2, 0, 2, 0);
            label1.Name = "label1";
            label1.Size = new System.Drawing.Size(25, 15);
            label1.TabIndex = 11;
            label1.Text = "103";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new System.Drawing.Point(9, 239);
            label2.Margin = new Padding(2, 0, 2, 0);
            label2.Name = "label2";
            label2.Size = new System.Drawing.Size(99, 15);
            label2.TabIndex = 12;
            label2.Text = "Distance on map:";
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new System.Drawing.Point(117, 239);
            label4.Margin = new Padding(2, 0, 2, 0);
            label4.Name = "label4";
            label4.Size = new System.Drawing.Size(0, 15);
            label4.TabIndex = 13;
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new System.Drawing.Point(9, 212);
            label5.Margin = new Padding(2, 0, 2, 0);
            label5.Name = "label5";
            label5.Size = new System.Drawing.Size(58, 15);
            label5.TabIndex = 12;
            label5.Text = "Elevation:";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new System.Drawing.Point(117, 212);
            label6.Margin = new Padding(2, 0, 2, 0);
            label6.Name = "label6";
            label6.Size = new System.Drawing.Size(0, 15);
            label6.TabIndex = 12;
            // 
            // listView1
            // 
            listView1.Location = new System.Drawing.Point(13, 273);
            listView1.Margin = new Padding(2);
            listView1.Name = "listView1";
            listView1.Size = new System.Drawing.Size(220, 166);
            listView1.TabIndex = 14;
            listView1.UseCompatibleStateImageBehavior = false;
            listView1.View = View.List;
            listView1.SelectedIndexChanged += listView1_SelectedIndexChanged;
            // 
            // checkBox1
            // 
            checkBox1.AutoSize = true;
            checkBox1.Location = new System.Drawing.Point(13, 93);
            checkBox1.Margin = new Padding(2);
            checkBox1.Name = "checkBox1";
            checkBox1.Size = new System.Drawing.Size(173, 19);
            checkBox1.TabIndex = 16;
            checkBox1.Text = "Read hotkeys (Alt+Q,W,A,S)";
            checkBox1.UseVisualStyleBackColor = true;
            checkBox1.CheckedChanged += checkBox1_CheckedChanged;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Location = new System.Drawing.Point(14, 442);
            label7.Margin = new Padding(4, 0, 4, 0);
            label7.Name = "label7";
            label7.Size = new System.Drawing.Size(165, 15);
            label7.TabIndex = 17;
            label7.Text = "⏲️ Time from click to impact: ";
            // 
            // label8
            // 
            label8.AutoSize = true;
            label8.Location = new System.Drawing.Point(183, 442);
            label8.Margin = new Padding(4, 0, 4, 0);
            label8.Name = "label8";
            label8.Size = new System.Drawing.Size(0, 15);
            label8.TabIndex = 18;
            // 
            // label9
            // 
            label9.AutoSize = true;
            label9.Location = new System.Drawing.Point(16, 468);
            label9.Margin = new Padding(4, 0, 4, 0);
            label9.Name = "label9";
            label9.Size = new System.Drawing.Size(146, 15);
            label9.TabIndex = 19;
            label9.Text = "Panzerfaust as mortar tips:";
            // 
            // label10
            // 
            label10.AutoSize = true;
            label10.Location = new System.Drawing.Point(16, 483);
            label10.Margin = new Padding(4, 0, 4, 0);
            label10.Name = "label10";
            label10.Size = new System.Drawing.Size(130, 15);
            label10.TabIndex = 19;
            label10.Text = "68, 48, 42.5, 17.5 meters";
            // 
            // label11
            // 
            label11.AutoSize = true;
            label11.Location = new System.Drawing.Point(16, 498);
            label11.Margin = new Padding(4, 0, 4, 0);
            label11.Name = "label11";
            label11.Size = new System.Drawing.Size(184, 15);
            label11.TabIndex = 19;
            label11.Text = "for zeroing 0 (hipfire), 60, 100, 150";
            // 
            // motdTextBox
            // 
            motdTextBox.Location = new System.Drawing.Point(-1, -1);
            motdTextBox.Margin = new Padding(4, 3, 4, 3);
            motdTextBox.Name = "motdTextBox";
            motdTextBox.Size = new System.Drawing.Size(245, 28);
            motdTextBox.TabIndex = 20;
            motdTextBox.Text = "";
            // 
            // Form1
            // 
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(243, 519);
            Controls.Add(groupBoxMarkerColor);
            Controls.Add(checkBoxHotkeysAlt1234);
            Controls.Add(motdTextBox);
            Controls.Add(label10);
            Controls.Add(label11);
            Controls.Add(label9);
            Controls.Add(label8);
            Controls.Add(label7);
            Controls.Add(checkBox1);
            Controls.Add(listView1);
            Controls.Add(label4);
            Controls.Add(label6);
            Controls.Add(label5);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(trackBar1);
            Controls.Add(label3);
            Icon = (System.Drawing.Icon)resources.GetObject("$this.Icon");
            Margin = new Padding(2);
            Name = "Form1";
            Text = "mortarkiller";
            Load += Form1_Load;
            groupBoxMarkerColor.ResumeLayout(false);
            groupBoxMarkerColor.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)markerPreview).EndInit();
            ((System.ComponentModel.ISupportInitialize)trackBar1).EndInit();
            ResumeLayout(false);
            PerformLayout();

        }

        #endregion
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TrackBar trackBar1;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.ListView listView1;
        private System.Windows.Forms.CheckBox checkBox1;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.Label label11;
        private System.Windows.Forms.RichTextBox motdTextBox;
        private System.Windows.Forms.GroupBox groupBoxMarkerColor;
        private System.Windows.Forms.RadioButton rbMarker1;
        private System.Windows.Forms.RadioButton rbMarker2;
        private System.Windows.Forms.RadioButton rbMarker3;
        private System.Windows.Forms.RadioButton rbMarker4;
        private System.Windows.Forms.PictureBox markerPreview;
        private System.Windows.Forms.CheckBox checkBoxHotkeysAlt1234;
    }
}
