using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static UniverseGenerator.SolarObjects;

namespace UniverseGenerator
{
    public partial class Form1 : Form
    {
        SolidBrush WhiteBrush = new SolidBrush(Color.White);
        Pen WhitePen = new Pen(Color.White, 1);
        Pen BlackPen = new Pen(Color.Black, 1);
        Pen BluePen = new Pen(Color.Blue, 1);
        Pen RedPen = new Pen(Color.Red, 1);
        Pen OrangePen = new Pen(Color.Orange, 1);
        Pen YellowPen = new Pen(Color.Yellow, 1);

        Font DebugFont = new System.Drawing.Font("Arial", 10);
        SolidBrush BlackFontBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
        SolidBrush WhiteFontBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);

        public Pos LastGenLocation { get; set; }
        public Pos SectorLocation { get; set; }
        public Pos GalaxyLocation { get; set; }

        //The current list of stars that might need to be rendered
        public List<SolarObjects.Star> CurStarList { get; set; }
        //The total X/Y/Z size of the galaxy level
        public int GalaxySize { get; set; }
        //The total X/Y/Z size of the sector level
        public int SectorSize { get; set; }

        public bool ShowStarData { get; set; }
        public SolarObjects.Star HighlightedStar { get; set; }

        public int CoreSeed { get; set; }
        public int CoreDensity { get; set; }

        public int DisplayScale { get; set; }
        public const int MinScale = 8;
        public const int MaxScale = 100;

        public Form1()
        {
            InitializeComponent();
            SectorLocation = new Pos(0, 0, 0);
            GalaxyLocation = new Pos(0, 0, 0);
            GalaxySize = 500;
            SectorSize = 500;
            CoreDensity = 20;
            CoreSeed = 2324234;
            DisplayScale = 16;
            CurStarList = new List<Star>();

            this.DoubleBuffered = true;
            this.MouseWheel += Form1_MouseWheel;

            Graphics dc = this.CreateGraphics();

            this.Show();
        }

        public List<SolarObjects.Star> generateStars(int seed, int density)
        {
            if (LastGenLocation != null && LastGenLocation.X == SectorLocation.X && LastGenLocation.Y == SectorLocation.Y)
                return CurStarList;

            int sectorsX = this.Width / DisplayScale;
            int sectorsY = this.Height / DisplayScale;
            int sectorsZ = this.Depth / DisplayScale;
            var returnList = new List<SolarObjects.Star>();
            SolarObjects.Pos screen_sector = new SolarObjects.Pos(0, 0, 0);

            for (screen_sector.X = 0; screen_sector.X < sectorsX; screen_sector.X++)
            {
                for (screen_sector.Y = 0; screen_sector.Y < sectorsY; screen_sector.Y++)
                {
                    for (screen_sector.Z = 0; screen_sector.Z < sectorsZ; screen_sector.Z++)
                    {
                        var starSeed = seed + ((SectorLocation.X + (screen_sector.X + 1)) & 0xFFFF) << 16 | ((SectorLocation.Y + (screen_sector.Y + 1)) & 0xFFFF);
                        if (Generators.RndBoolSeeded(starSeed, Math.Max(density, 1)))
                        {
                            returnList.Add(new SolarObjects.Star(screen_sector.X, screen_sector.Y, starSeed));
                        }
                    }
                }
            }

            LastGenLocation = new Pos(SectorLocation.X, SectorLocation.Y, SectorLocation.Z);

            return returnList;
        }

        private void RefreshGalaxy()
        {
            if (LastGenLocation != null && LastGenLocation.X == SectorLocation.X && LastGenLocation.Y == SectorLocation.Y && LastGenLocation.Z == SectorLocation.Z)
                return;
            CurStarList = generateStars(CoreSeed, CoreDensity);
            this.Invalidate();
        }

        private void Form1_ResizeEnd(object sender, EventArgs e)
        {
            RefreshGalaxy();
        }


        #region FormElementEvents
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            if (int.TryParse(CoreSeedtxtbox.Text, out int newSeed))
            {
                CoreSeed = newSeed;
                RefreshGalaxy();
            }
            else
            {
                MessageBox.Show("Galaxy Seed should be an integer");
            }
        }

        private void DensityUpDown_ValueChanged(object sender, EventArgs e)
        {
            CoreDensity = 50 - (int)DensityUpDown.Value;
            RefreshGalaxy();
        }

        private void CoreSeedtxtbox_KeyPress(object sender, KeyPressEventArgs e)
        {
            var keyPressed = e.KeyChar.ToString().ToUpper()[0];
            if (keyPressed == (char)Keys.W || keyPressed == (char)Keys.A || keyPressed == (char)Keys.A || keyPressed == (char)Keys.D)
            {
                e.Handled = true;
                this.ActiveControl = label1;
            }
        }

        private void ScaleUpDown_ValueChanged(object sender, EventArgs e)
        {
            DisplayScale = (int)ScaleUpDown.Value;
            RefreshGalaxy();
        }

        private void XCoordstxtbox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!int.TryParse(e.KeyChar.ToString(), out int intCheck) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true;
                this.ActiveControl = label1;
                return;
            }
        }

        private void YCoordstxtbox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!int.TryParse(e.KeyChar.ToString(), out int intCheck) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true;
                this.ActiveControl = label1;
                return;
            }
        }

        private void XCoordstxtbox_TextChanged(object sender, EventArgs e)
        {
            if (SectorLocation.X != int.Parse(XCoordstxtbox.Text))
            {
                SectorLocation.X = int.Parse(XCoordstxtbox.Text);
                RefreshGalaxy();
            }
        }

        private void YCoordstxtbox_TextChanged(object sender, EventArgs e)
        {
            if (SectorLocation.Y != int.Parse(YCoordstxtbox.Text))
            {
                SectorLocation.Y = int.Parse(YCoordstxtbox.Text);
                RefreshGalaxy();
            }
        }

        #endregion

        #region InputManagement
        private void Form1_MouseWheel(object sender, MouseEventArgs e)
        {
            var wheelMovement = e.Delta / 120;
            DisplayScale += wheelMovement;
            if (DisplayScale < MinScale)
                DisplayScale = MinScale;
            else if (DisplayScale > MaxScale)
                DisplayScale = MaxScale;

            ScaleUpDown.Value = DisplayScale;
            RefreshGalaxy();
        }

        private void Form1_Click(object sender, EventArgs e)
        {
            this.ActiveControl = label1;
            RefreshGalaxy();
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.W)
            {
                SectorLocation.Y = SectorLocation.Y - 1;
                YCoordstxtbox.Text = SectorLocation.Y.ToString();
            }
            else if (e.KeyCode == Keys.S)
            {
                SectorLocation.Y = SectorLocation.Y + 1;
                YCoordstxtbox.Text = SectorLocation.Y.ToString();
            }
            else if (e.KeyCode == Keys.A)
            {
                SectorLocation.X = SectorLocation.X - 1;
                XCoordstxtbox.Text = SectorLocation.X.ToString();
            }
            else if (e.KeyCode == Keys.D)
            {
                SectorLocation.X = SectorLocation.X + 1;
                XCoordstxtbox.Text = SectorLocation.X.ToString();
            }

            RefreshGalaxy();
        }

        private void Form1_MouseMove(object sender, MouseEventArgs e)
        {
            //Removed until we get the generation working again
            //if (CurStarList.Where(T => T.Location.X == Math.Floor((Decimal)(e.X / DisplayScale)) && T.Location.Y == Math.Floor((Decimal)(e.Y / DisplayScale))).Count() == 1)
            //{
            //    if (HighlightedStar != CurStarList.Single(T => T.Location.X == Math.Floor((Decimal)(e.X / DisplayScale)) && T.Location.Y == Math.Floor((Decimal)(e.Y / DisplayScale))))
            //    {
            //        ShowStarData = true;
            //        HighlightedStar = CurStarList.Single(T => T.Location.X == Math.Floor((Decimal)(e.X / DisplayScale)) && T.Location.Y == Math.Floor((Decimal)(e.Y / DisplayScale)));
            //        this.Invalidate();
            //    }
            //}
            //else if (ShowStarData == true)
            //{
            //    ShowStarData = false;
            //    HighlightedStar = null;
            //    this.Invalidate();
            //}
        }
        #endregion

        #region Drawing
        public void DrawCircle(Graphics dc, SolidBrush drawPen, Pos location, int width, int height)
        {
            dc.FillEllipse(drawPen, location.X, location.Y, width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics dc = e.Graphics;
            foreach (var star in CurStarList)
            {
                DrawCircle(dc, new SolidBrush(star.StarType), star.SectorLocation.Scaled(DisplayScale), star.Size, star.Size);
            }

            if (ShowStarData)
            {
                var mousePosition = this.PointToClient(Cursor.Position);
                var textRow = 0;
                var windowHeight = (25 + (15 * (HighlightedStar.Planets.Count() + HighlightedStar.Planets.Sum(T => T.Moons.Count()))));
                dc.FillRectangle(WhiteBrush, mousePosition.X, mousePosition.Y, 300, windowHeight);
                dc.DrawString("Name : " + HighlightedStar.Name, DebugFont, BlackFontBrush, mousePosition.X + 5, mousePosition.Y + 5);
                if (HighlightedStar.Planets.Count() > 0)
                {
                    foreach (var planet in HighlightedStar.Planets.OrderBy(T => T.Distance))
                    {
                        textRow++;
                        dc.DrawString("Planet  : " + planet.Name + " - Size(" + planet.Size + ")" + " - Distance(" + planet.Distance + ")", DebugFont, BlackFontBrush, mousePosition.X + 5, mousePosition.Y + 5 + (textRow * 15));
                        if (planet.Moons.Count() > 0)
                        {
                            foreach (var moon in planet.Moons.OrderBy(T => T.Distance))
                            {
                                textRow++;
                                dc.DrawString("-Moon : " + moon.Name + " - Size(" + moon.Size + ")" + " - Distance(" + moon.Distance + ")", DebugFont, BlackFontBrush, mousePosition.X + 5, mousePosition.Y + 5 + (textRow * 15));
                            }
                        }
                    }
                }

            }

            dc.DrawString("Sector(" + SectorLocation.X + "," + SectorLocation.Y + ")", DebugFont, WhiteFontBrush, 0, 0);
            base.OnPaint(e);
        }
        #endregion

    }
}