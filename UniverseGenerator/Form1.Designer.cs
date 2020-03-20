namespace UniverseGenerator
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.label1 = new System.Windows.Forms.Label();
            this.CoreSeedtxtbox = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.DensityUpDown = new System.Windows.Forms.NumericUpDown();
            this.label3 = new System.Windows.Forms.Label();
            this.ScaleUpDown = new System.Windows.Forms.NumericUpDown();
            this.XCoordstxtbox = new System.Windows.Forms.TextBox();
            this.ControlGroupBox = new System.Windows.Forms.GroupBox();
            this.YCoordstxtbox = new System.Windows.Forms.TextBox();
            this.label5 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.DensityUpDown)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.ScaleUpDown)).BeginInit();
            this.ControlGroupBox.SuspendLayout();
            this.SuspendLayout();
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.BackColor = System.Drawing.SystemColors.MenuText;
            this.label1.Location = new System.Drawing.Point(15, 22);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(32, 13);
            this.label1.TabIndex = 0;
            this.label1.Text = "Seed";
            // 
            // CoreSeedtxtbox
            // 
            this.CoreSeedtxtbox.BackColor = System.Drawing.SystemColors.MenuText;
            this.CoreSeedtxtbox.ForeColor = System.Drawing.Color.Cornsilk;
            this.CoreSeedtxtbox.Location = new System.Drawing.Point(116, 19);
            this.CoreSeedtxtbox.MaxLength = 9;
            this.CoreSeedtxtbox.Name = "CoreSeedtxtbox";
            this.CoreSeedtxtbox.Size = new System.Drawing.Size(60, 20);
            this.CoreSeedtxtbox.TabIndex = 1;
            this.CoreSeedtxtbox.Text = "123456789";
            this.CoreSeedtxtbox.TextChanged += new System.EventHandler(this.textBox1_TextChanged);
            this.CoreSeedtxtbox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.CoreSeedtxtbox_KeyPress);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.BackColor = System.Drawing.SystemColors.MenuText;
            this.label2.Location = new System.Drawing.Point(15, 42);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(42, 13);
            this.label2.TabIndex = 2;
            this.label2.Text = "Density";
            // 
            // DensityUpDown
            // 
            this.DensityUpDown.BackColor = System.Drawing.SystemColors.MenuText;
            this.DensityUpDown.ForeColor = System.Drawing.Color.Cornsilk;
            this.DensityUpDown.Location = new System.Drawing.Point(132, 40);
            this.DensityUpDown.Maximum = new decimal(new int[] {
            50,
            0,
            0,
            0});
            this.DensityUpDown.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.DensityUpDown.Name = "DensityUpDown";
            this.DensityUpDown.Size = new System.Drawing.Size(44, 20);
            this.DensityUpDown.TabIndex = 3;
            this.DensityUpDown.Value = new decimal(new int[] {
            30,
            0,
            0,
            0});
            this.DensityUpDown.ValueChanged += new System.EventHandler(this.DensityUpDown_ValueChanged);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.BackColor = System.Drawing.SystemColors.MenuText;
            this.label3.Location = new System.Drawing.Point(15, 64);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(34, 13);
            this.label3.TabIndex = 4;
            this.label3.Text = "Scale";
            // 
            // ScaleUpDown
            // 
            this.ScaleUpDown.BackColor = System.Drawing.SystemColors.MenuText;
            this.ScaleUpDown.ForeColor = System.Drawing.Color.Cornsilk;
            this.ScaleUpDown.Location = new System.Drawing.Point(132, 62);
            this.ScaleUpDown.Minimum = new decimal(new int[] {
            8,
            0,
            0,
            0});
            this.ScaleUpDown.Name = "ScaleUpDown";
            this.ScaleUpDown.Size = new System.Drawing.Size(44, 20);
            this.ScaleUpDown.TabIndex = 5;
            this.ScaleUpDown.Value = new decimal(new int[] {
            16,
            0,
            0,
            0});
            this.ScaleUpDown.ValueChanged += new System.EventHandler(this.ScaleUpDown_ValueChanged);
            // 
            // XCoordstxtbox
            // 
            this.XCoordstxtbox.BackColor = System.Drawing.SystemColors.MenuText;
            this.XCoordstxtbox.ForeColor = System.Drawing.Color.Cornsilk;
            this.XCoordstxtbox.Location = new System.Drawing.Point(55, 84);
            this.XCoordstxtbox.MaxLength = 9;
            this.XCoordstxtbox.Name = "XCoordstxtbox";
            this.XCoordstxtbox.Size = new System.Drawing.Size(121, 20);
            this.XCoordstxtbox.TabIndex = 6;
            this.XCoordstxtbox.TextChanged += new System.EventHandler(this.XCoordstxtbox_TextChanged);
            this.XCoordstxtbox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.XCoordstxtbox_KeyPress);
            // 
            // ControlGroupBox
            // 
            this.ControlGroupBox.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.ControlGroupBox.BackColor = System.Drawing.Color.Brown;
            this.ControlGroupBox.Controls.Add(this.YCoordstxtbox);
            this.ControlGroupBox.Controls.Add(this.label5);
            this.ControlGroupBox.Controls.Add(this.label4);
            this.ControlGroupBox.Controls.Add(this.CoreSeedtxtbox);
            this.ControlGroupBox.Controls.Add(this.XCoordstxtbox);
            this.ControlGroupBox.Controls.Add(this.label1);
            this.ControlGroupBox.Controls.Add(this.ScaleUpDown);
            this.ControlGroupBox.Controls.Add(this.label2);
            this.ControlGroupBox.Controls.Add(this.label3);
            this.ControlGroupBox.Controls.Add(this.DensityUpDown);
            this.ControlGroupBox.ForeColor = System.Drawing.Color.Cornsilk;
            this.ControlGroupBox.Location = new System.Drawing.Point(600, 12);
            this.ControlGroupBox.Name = "ControlGroupBox";
            this.ControlGroupBox.Size = new System.Drawing.Size(188, 140);
            this.ControlGroupBox.TabIndex = 7;
            this.ControlGroupBox.TabStop = false;
            this.ControlGroupBox.Text = "Controls";
            // 
            // YCoordstxtbox
            // 
            this.YCoordstxtbox.BackColor = System.Drawing.SystemColors.MenuText;
            this.YCoordstxtbox.ForeColor = System.Drawing.Color.Cornsilk;
            this.YCoordstxtbox.Location = new System.Drawing.Point(55, 105);
            this.YCoordstxtbox.MaxLength = 9;
            this.YCoordstxtbox.Name = "YCoordstxtbox";
            this.YCoordstxtbox.Size = new System.Drawing.Size(121, 20);
            this.YCoordstxtbox.TabIndex = 10;
            this.YCoordstxtbox.TextChanged += new System.EventHandler(this.YCoordstxtbox_TextChanged);
            this.YCoordstxtbox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.YCoordstxtbox_KeyPress);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.BackColor = System.Drawing.SystemColors.MenuText;
            this.label5.Location = new System.Drawing.Point(15, 108);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(14, 13);
            this.label5.TabIndex = 9;
            this.label5.Text = "Y";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.BackColor = System.Drawing.SystemColors.MenuText;
            this.label4.Location = new System.Drawing.Point(15, 87);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(14, 13);
            this.label4.TabIndex = 8;
            this.label4.Text = "X";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.Black;
            this.ClientSize = new System.Drawing.Size(800, 800);
            this.Controls.Add(this.ControlGroupBox);
            this.KeyPreview = true;
            this.Name = "Form1";
            this.Text = "Display At Startup";
            this.ResizeEnd += new System.EventHandler(this.Form1_ResizeEnd);
            this.Click += new System.EventHandler(this.Form1_Click);
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.Form1_KeyDown);
            this.MouseMove += new System.Windows.Forms.MouseEventHandler(this.Form1_MouseMove);
            ((System.ComponentModel.ISupportInitialize)(this.DensityUpDown)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.ScaleUpDown)).EndInit();
            this.ControlGroupBox.ResumeLayout(false);
            this.ControlGroupBox.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.TextBox CoreSeedtxtbox;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.NumericUpDown DensityUpDown;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.NumericUpDown ScaleUpDown;
        private System.Windows.Forms.TextBox XCoordstxtbox;
        private System.Windows.Forms.GroupBox ControlGroupBox;
        private System.Windows.Forms.TextBox YCoordstxtbox;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label4;
    }
}

