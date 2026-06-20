using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Text;
using System.Windows.Forms;
using UniverseGen.Generation;
using UniverseGen.Imaging;
using UniverseGen.Models;
using UniverseGen.Rendering;
using RegionF = UniverseGen.Models.RegionF;

namespace UniverseGen.Demo;

public class MainForm : Form
{
    private readonly GalaxyGenerator _generator = new();
    private readonly GalaxyRenderer  _renderer  = new();
    private Galaxy?  _galaxy;
    private Bitmap?  _sideViewBitmap;

    // ── galaxy tab controls ───────────────────────────────────────────────────
    private Panel    _preview        = null!;
    private Bitmap?  _galaxyBitmap;
    private TrackBar _tbArms         = null!, _tbTightness    = null!, _tbArmWidth     = null!;
    private TrackBar _tbBulge        = null!, _tbDissipation  = null!;
    private TrackBar _tbBarStrength  = null!, _tbBarLength    = null!;
    private TrackBar _tbFeatureDensity = null!;
    private Label    _lblArms        = null!, _lblTightness   = null!, _lblArmWidth    = null!;
    private Label    _lblBulge       = null!, _lblDissipation = null!;
    private Label    _lblBarStrength = null!, _lblBarLength   = null!;
    private Label    _lblFeatureDensity = null!;
    private ComboBox _cbAge          = null!, _cbType         = null!;
    private TextBox  _txSeed         = null!;
    private TextBox  _txGalaxyCode   = null!;
    private string?  _galaxyCode;
    private Button   _btnGenerate    = null!, _btnExport = null!, _btnImport = null!;

    // ── explore tab controls ──────────────────────────────────────────────────
    private Panel    _explorePanel    = null!;
    private Label    _lblZoomLevel    = null!;
    private Label    _lblGridSizeVal  = null!;
    private TrackBar _tbExploreGrid   = null!;
    private Button   _btnZoomOut      = null!, _btnZoomReset = null!;
    private Label    _lblZoomPath     = null!;

    // Density-field generation is physically meaningful up to ~level 3 (≈1,500 ly
    // per cell at N=4).  Beyond that, discrete particle simulation takes over.
    private const int MaxZoomLevel = 3;

    private readonly List<(int gx, int gy)>    _zoomPath    = [];
    private readonly Dictionary<string, Bitmap> _exploreCache = new();
    private Point    _hoveredCell  = new(-1, -1);
    private bool     _hoverInMinimap;

    // ── stars tab controls ────────────────────────────────────────────────────
    private BufferedPanel      _starPanel      = null!;
    private Label              _lblStarPos     = null!;
    private Label              _lblStarCount   = null!;
    private Label              _lblStarChunks  = null!;
    private Label              _lblGcDist      = null!;
    private Label              _lblCacheStats  = null!;
    private DateTime           _nextCacheStatsRefresh;
    private Camera3D           _camera         = new();
    private StarChunkCache?    _starCache;
    private StarStreamService? _starStream;
    private readonly StarFieldRenderer _starRenderer = new();
    private Bitmap?            _starBitmap;
    private Bitmap?            _starBitmapBack;  // back buffer reused by the render thread
    private volatile bool      _starRenderPending;
    private Task?              _starRenderTask;
    private Point              _starMouseLast;
    private bool               _starDragging;
    private readonly HashSet<Keys>              _heldKeys  = [];
    private System.Windows.Forms.Timer          _moveTimer = null!;
    // Speed ladder: 10 discrete levels, each doubling the last.
    // Values are in normalised galaxy units per keypress (1 unit = 50,000 ly).
    //   Level 1 ≈  0.05 ly/press     Level 6 ≈  1.5 ly/press
    //   Level 2 ≈  0.1  ly/press     Level 7 ≈  5   ly/press
    //   Level 3 ≈  0.25 ly/press     Level 8 ≈  15  ly/press
    //   Level 4 ≈  0.5  ly/press     Level 9 ≈  50  ly/press
    //   Level 5 ≈  1    ly/press     Level 10 ≈ 150  ly/press
    private static readonly float[] SpeedLevels =
    [
        0.000001f, 0.000002f, 0.000005f, 0.00001f, 0.00002f,
        0.00003f,  0.0001f,   0.0003f,   0.001f,   0.003f,
    ];
    private int   _starSpeedIndex = 1;                          // default = level 2
    private float _starMoveSpeed  => SpeedLevels[_starSpeedIndex];
    private ProjectedStar[]    _projectedStars = [];
    private ProjectedStar?     _hoveredStar;
    private StarSystem?        _hoveredSystem;
    private readonly StarSystemGenerator _systemGenerator = new();
    private const float        MinHitPixels  =  4f;      // min hit zone for any on-screen star

    // ── system tab controls ───────────────────────────────────────────────────
    private TabControl      _tabs            = null!;
    private BufferedPanel   _systemPanel     = null!;
    private Label           _lblSysName      = null!;
    private Label           _lblSysInfo      = null!;
    private Label           _lblSysZones     = null!;
    private Label           _lblSysBodies    = null!;
    private ListBox         _sysBodyList     = null!;
    private Label           _lblBodyStats    = null!;
    private StarSystem?     _selectedSystem;
    private StarSystemBody? _selectedBody;
    private Moon[]?         _selectedBodyMoons;
    private readonly MoonGenerator _moonGenerator = new();
    private bool            _starWasDragging;
    private bool            _sysBodyListUpdating;
    private readonly List<(RectangleF Rect, StarSystemBody Body)>   _sysBodyHitRects      = [];
    private readonly List<(RectangleF Rect, Moon Moon)>             _sysMoonHitRects      = [];
    private readonly List<(RectangleF Rect, CompanionStar Comp)>    _sysCompanionHitRects = [];
    private StarSystemBody? _sysHoveredBody;
    private Moon?           _sysHoveredMoon;
    private CompanionStar?  _sysHoveredCompanion;
    private const float        MaxHoverDepth =  0.001f;  // 50 ly — hover only on nearby stars
    private const float        LyPerUnit     = 50000f;   // galaxy normalised → light-years

    // ── chain strip ───────────────────────────────────────────────────────────
    private Label _chainLabel = null!;

    public MainForm()
    {
        Text        = "Universal Generator";
        Size        = new Size(1100, 860);
        MinimumSize = new Size(900, 840);
        BackColor   = Color.FromArgb(28, 28, 28);
        ForeColor   = Color.White;
        KeyPreview  = true;   // route WM_KEYUP through the form so OnKeyUp always fires
        BuildUI();
    }

    // ── top-level layout ─────────────────────────────────────────────────────

    private void BuildUI()
    {
        var chainPanel = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 34,
            BackColor = Color.FromArgb(16, 16, 16),
            Padding   = new Padding(12, 0, 12, 0),
        };
        _chainLabel = new Label
        {
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            Font      = new Font("Consolas", 9f),
            AutoSize  = false,
            Text      = ChainText(),
        };
        chainPanel.Controls.Add(_chainLabel);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9f),
        };
        var tabs = _tabs;
        tabs.TabPages.Add(BuildGalaxyTab());
        tabs.TabPages.Add(BuildExploreTab());
        tabs.TabPages.Add(BuildStarsTab());
        tabs.TabPages.Add(BuildSystemTab());
        tabs.TabPages.Add(BuildStubTab("Planet",  "Planet generation — coming soon"));
        tabs.TabPages.Add(BuildStubTab("Surface", "Surface generation — coming soon"));

        tabs.SelectedIndexChanged += (_, _) =>
        {
            _explorePanel?.Invalidate();
            if (tabs.SelectedTab?.Text == "Stars")  OnStarsTabActivated();
            if (tabs.SelectedTab?.Text == "System") _systemPanel?.Invalidate();
        };

        // Continuous movement timer — fires at ~60 fps, applies held WASD/QE
        // keys without any OS key-repeat delay.
        _moveTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _moveTimer.Tick += OnMoveTick;
        _moveTimer.Start();

        Controls.Add(tabs);
        Controls.Add(chainPanel);
    }

    // ── galaxy tab ───────────────────────────────────────────────────────────

    private TabPage BuildGalaxyTab()
    {
        var tab = new TabPage("Galaxy")
        {
            BackColor = Color.FromArgb(28, 28, 28),
            Padding   = new Padding(0),
        };

        var sidePanel = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 270,
            BackColor = Color.FromArgb(38, 38, 38),
            Padding   = new Padding(12),
        };

        _preview = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _preview.Paint += OnPreviewPaint;

        tab.Controls.Add(_preview);
        tab.Controls.Add(sidePanel);

        int y = 12;

        void Slider(string label, ref TrackBar tb, ref Label lbl, int min, int max, int val)
        {
            lbl = new Label
            {
                Text = $"{label}: {val}", Left = 12, Top = y, Width = 246, Height = 20,
                ForeColor = Color.Silver, AutoSize = false,
            };
            sidePanel.Controls.Add(lbl);
            y += 24;
            tb = new TrackBar
            {
                Left = 8, Top = y, Width = 250, Height = 32,
                Minimum = min, Maximum = max, Value = val,
                BackColor = Color.FromArgb(38, 38, 38),
            };
            var capLbl = lbl; var capLabel = label; var capTb = tb;
            tb.ValueChanged += (_, _) => capLbl.Text = $"{capLabel}: {capTb.Value}";
            sidePanel.Controls.Add(tb);
            y += 46;
        }

        void Combo(string label, ref ComboBox cb, string[] items, int sel)
        {
            sidePanel.Controls.Add(new Label
            {
                Text = label, Left = 12, Top = y, Height = 20,
                ForeColor = Color.Silver, AutoSize = false,
            });
            y += 24;
            cb = new ComboBox
            {
                Left = 12, Top = y, Width = 246,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
            };
            cb.Items.AddRange(items);
            cb.SelectedIndex = sel;
            sidePanel.Controls.Add(cb);
            y += 42;
        }

        Slider("Arms",         ref _tbArms,        ref _lblArms,         2,   8,  4);
        Slider("Tightness",    ref _tbTightness,   ref _lblTightness,   10, 100, 50);
        Slider("Arm Width",    ref _tbArmWidth,    ref _lblArmWidth,     5,  80, 25);
        Slider("Bulge",        ref _tbBulge,       ref _lblBulge,        5,  60, 20);
        Slider("Dissipation",  ref _tbDissipation, ref _lblDissipation,  0,  60, 15);
        Slider("Bar Strength", ref _tbBarStrength, ref _lblBarStrength,  0, 100,  0);
        Slider("Bar Length",   ref _tbBarLength,   ref _lblBarLength,   10,  60, 35);

        Divider(sidePanel, y); y += 16;

        Slider("Feature Density %", ref _tbFeatureDensity, ref _lblFeatureDensity, 25, 200, 100);

        Combo("Type:", ref _cbType, ["Spiral", "Elliptical", "Lenticular", "Irregular"], 0);
        _cbType.SelectedIndexChanged += (_, _) => UpdateControlsForType();
        Combo("Age:",  ref _cbAge,  ["Young", "Mature", "Old"], 1);

        sidePanel.Controls.Add(new Label
        {
            Text = "Seed:", Left = 12, Top = y, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        });
        y += 24;
        _txSeed = new TextBox
        {
            Left = 12, Top = y, Width = 246, Text = "12345",
            BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
        };
        sidePanel.Controls.Add(_txSeed);
        y += 42;

        _btnGenerate = MakeButton("Generate",   Color.FromArgb(0, 110, 200),  12,       y, 118);
        _btnExport   = MakeButton("Export PNG", Color.FromArgb(0, 140,  90), 142,       y, 118);
        _btnImport   = MakeButton("Import PNG", Color.FromArgb(90,  60,  0),  12, y + 42, 248);

        _btnExport.Enabled  = false;
        _btnGenerate.Click += OnGenerate;
        _btnExport.Click   += OnExport;
        _btnImport.Click   += OnImport;
        sidePanel.Controls.AddRange([_btnGenerate, _btnExport, _btnImport]);

        y += 90;
        Divider(sidePanel, y); y += 16;

        sidePanel.Controls.Add(new Label
        {
            Text = "Galaxy Code:", Left = 12, Top = y, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        });
        y += 24;

        _txGalaxyCode = new TextBox
        {
            Left = 12, Top = y, Width = 246,
            BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.FromArgb(140, 140, 140),
            Font      = new Font("Consolas", 8f),
            Text      = "(generate a galaxy first)",
        };
        sidePanel.Controls.Add(_txGalaxyCode);
        y += 30;

        var btnCopy = MakeButton("Copy",      Color.FromArgb(50, 50, 80),  12, y, 118);
        var btnLoad = MakeButton("Load Code", Color.FromArgb(0, 80, 140), 142, y, 118);

        btnCopy.Click += (_, _) =>
        {
            if (_galaxyCode != null) Clipboard.SetText(_galaxyCode);
        };
        btnLoad.Click += (_, _) =>
        {
            try
            {
                var p = GalaxyCode.Decode(_txGalaxyCode.Text.Trim());
                ApplyParams(p);
                Regenerate(p);
            }
            catch (Exception ex) { Error(ex); }
        };
        sidePanel.Controls.AddRange([btnCopy, btnLoad]);

        UpdateControlsForType();
        return tab;
    }

    // ── explore tab ───────────────────────────────────────────────────────────

    private TabPage BuildExploreTab()
    {
        var tab = new TabPage("Explore")
        {
            BackColor = Color.FromArgb(28, 28, 28),
            Padding   = new Padding(0),
        };

        var sidePanel = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 270,
            BackColor = Color.FromArgb(38, 38, 38),
            Padding   = new Padding(12),
        };

        _explorePanel = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _explorePanel.Paint      += OnExplorePaint;
        _explorePanel.MouseMove  += OnExploreMouseMove;
        _explorePanel.MouseLeave += OnExploreMouseLeave;
        _explorePanel.MouseClick += OnExploreClick;
        _explorePanel.Resize     += (_, _) => _explorePanel.Invalidate();

        tab.Controls.Add(_explorePanel);
        tab.Controls.Add(sidePanel);

        int y = 12;

        // grid size slider
        _lblGridSizeVal = new Label
        {
            Text = "Grid Size: 4 × 4", Left = 12, Top = y, Width = 246, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        };
        sidePanel.Controls.Add(_lblGridSizeVal);
        y += 24;

        _tbExploreGrid = new TrackBar
        {
            Left = 8, Top = y, Width = 250, Height = 32,
            Minimum = 2, Maximum = 8, Value = 4,
            BackColor = Color.FromArgb(38, 38, 38),
        };
        _tbExploreGrid.ValueChanged += (_, _) =>
        {
            int n = _tbExploreGrid.Value;
            _lblGridSizeVal.Text = $"Grid Size: {n} × {n}";
            _zoomPath.Clear();
            ClearExploreCache();
            UpdateExploreUI();
            _explorePanel.Invalidate();
        };
        sidePanel.Controls.Add(_tbExploreGrid);
        y += 46;

        Divider(sidePanel, y); y += 16;

        // zoom level
        _lblZoomLevel = new Label
        {
            Text = "Zoom Level: 0 / 10", Left = 12, Top = y, Width = 246, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        };
        sidePanel.Controls.Add(_lblZoomLevel);
        y += 32;

        _btnZoomOut   = MakeButton("◄ Zoom Out", Color.FromArgb(50, 70, 110),  12, y, 118);
        _btnZoomReset = MakeButton("↺ Reset",     Color.FromArgb(90, 40,  40), 142, y, 118);
        _btnZoomOut.Enabled  = false;
        _btnZoomOut.Click   += (_, _) => ExploreZoomOut();
        _btnZoomReset.Click += (_, _) => ExploreReset();
        sidePanel.Controls.AddRange([_btnZoomOut, _btnZoomReset]);
        y += 46;

        Divider(sidePanel, y); y += 16;

        sidePanel.Controls.Add(new Label
        {
            Text = "Location:", Left = 12, Top = y, Width = 246, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        });
        y += 24;

        _lblZoomPath = new Label
        {
            Left = 12, Top = y, Width = 246, Height = 300,
            ForeColor = Color.White,
            Font      = new Font("Consolas", 8f),
            AutoSize  = false,
        };
        sidePanel.Controls.Add(_lblZoomPath);

        UpdateExploreUI();
        return tab;
    }

    private static void Divider(Panel panel, int y) =>
        panel.Controls.Add(new Label
        {
            Left = 12, Top = y, Width = 246, Height = 1,
            BackColor = Color.FromArgb(55, 55, 55), AutoSize = false,
        });

    // ── explore paint ─────────────────────────────────────────────────────────

    private void OnExplorePaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_galaxyBitmap == null || _galaxy == null)
        {
            using var brush = new SolidBrush(Color.FromArgb(55, 55, 55));
            var fmt = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("Generate a galaxy on the Galaxy tab first",
                         new Font("Segoe UI", 13f), brush,
                         new RectangleF(0, 0, _explorePanel.Width, _explorePanel.Height), fmt);
            return;
        }

        Bitmap bmp;
        if (_zoomPath.Count == 0)
        {
            bmp = _galaxyBitmap;
        }
        else
        {
            string key = CacheKey();
            if (!_exploreCache.TryGetValue(key, out bmp!))
            {
                var r      = CurrentRegion();
                var zoomed = _generator.GenerateZoomedView(_galaxy.Params,
                                 new RegionF(r.X, r.Y, r.Width, r.Height),
                                 zoomLevel: _zoomPath.Count);
                bmp = _renderer.Render(zoomed);
                _exploreCache[key] = bmp;
            }
        }

        var dest = ZoomRect(_explorePanel.ClientSize, bmp.Size);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode   = PixelOffsetMode.Half;
        g.DrawImage(bmp, dest);

        DrawGrid(g, dest);
        DrawMinimap(g);
        DrawZoomBadge(g, dest);
    }

    private string CacheKey()
    {
        var sb = new StringBuilder();
        sb.Append(_tbExploreGrid.Value);
        foreach (var (gx, gy) in _zoomPath)
        {
            sb.Append('/'); sb.Append(gx); sb.Append(','); sb.Append(gy);
        }
        return sb.ToString();
    }

    private void ClearExploreCache()
    {
        foreach (var bmp in _exploreCache.Values) bmp.Dispose();
        _exploreCache.Clear();
    }

    private void DrawGrid(Graphics g, Rectangle dest)
    {
        int n = _tbExploreGrid.Value;
        float cw = (float)dest.Width  / n;
        float ch = (float)dest.Height / n;

        bool atMax = _zoomPath.Count >= MaxZoomLevel;

        using var linePen   = new Pen(Color.FromArgb(55, 255, 255, 255));
        using var hoverFill = new SolidBrush(Color.FromArgb(45, 255, 255, 255));
        using var hoverPen  = new Pen(atMax
                                          ? Color.FromArgb(160, 255, 80, 80)
                                          : Color.FromArgb(200, 255, 255, 180), 2f);

        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
            {
                float cx = dest.X + col * cw;
                float cy = dest.Y + row * ch;

                if (!atMax && _hoveredCell.X == col && _hoveredCell.Y == row)
                {
                    g.FillRectangle(hoverFill, cx, cy, cw, ch);
                    g.DrawRectangle(hoverPen, cx, cy, cw, ch);
                }
                else
                {
                    g.DrawRectangle(linePen, cx, cy, cw, ch);
                }
            }
        }
    }

    private void DrawMinimap(Graphics g)
    {
        if (_galaxyBitmap == null) return;

        const int mm = 160, margin = 12;
        var mmRect = new Rectangle(
            _explorePanel.Width  - mm - margin,
            _explorePanel.Height - mm - margin,
            mm, mm);

        // Background + galaxy thumbnail
        g.FillRectangle(Brushes.Black, mmRect);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(_galaxyBitmap, mmRect);

        // Nested zoom rectangles — outermost first (drawn under inner ones)
        float nx = 0, ny = 0, ns = 1f;
        int   gn = _tbExploreGrid.Value;

        for (int i = 0; i < _zoomPath.Count; i++)
        {
            float cs = ns / gn;
            float rx = nx + _zoomPath[i].gx * cs;
            float ry = ny + _zoomPath[i].gy * cs;

            float brightness = 0.35f + 0.65f * (i + 1f) / _zoomPath.Count;
            int   alpha      = (int)(brightness * 220);
            using var rp = new Pen(Color.FromArgb(alpha, 255, 220, 80), i == _zoomPath.Count - 1 ? 2f : 1f);
            g.DrawRectangle(rp,
                mmRect.X + rx * mm, mmRect.Y + ry * mm, cs * mm, cs * mm);

            // Dim fill for the current (innermost) region
            if (i == _zoomPath.Count - 1)
            {
                using var fill = new SolidBrush(Color.FromArgb(30, 255, 220, 80));
                g.FillRectangle(fill, mmRect.X + rx * mm, mmRect.Y + ry * mm, cs * mm, cs * mm);
            }

            nx = rx; ny = ry; ns = cs;
        }

        // Outer border
        g.DrawRectangle(_hoverInMinimap
                            ? new Pen(Color.FromArgb(180, 255, 255, 200))
                            : Pens.DimGray,
                        mmRect);

        // Label
        using var fnt  = new Font("Segoe UI", 7f);
        using var lblB = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        using var lblF = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
        var lblR = new RectangleF(mmRect.X, mmRect.Y, mm, 14);
        g.FillRectangle(lblB, lblR);
        g.DrawString("Click to navigate", fnt, lblF, lblR.X + 3, lblR.Y + 2);
    }

    private void DrawZoomBadge(Graphics g, Rectangle dest)
    {
        int level = _zoomPath.Count;
        bool atMax = level >= MaxZoomLevel;

        string text = atMax
            ? "Max zoom — transition to Star Cluster view"
            : $"Level {level} / {MaxZoomLevel}  —  click a cell to zoom in";

        using var font = new Font("Segoe UI", 8.5f);
        var sz   = g.MeasureString(text, font);
        var rect = new RectangleF(dest.X + 6, dest.Y + 6, sz.Width + 12, sz.Height + 6);

        using var bg  = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        using var fg  = new SolidBrush(atMax ? Color.FromArgb(255, 200, 80) : Color.FromArgb(200, 200, 200));
        g.FillRectangle(bg, rect);
        g.DrawString(text, font, fg, rect.X + 6, rect.Y + 3);
    }

    // ── explore interaction ───────────────────────────────────────────────────

    private void OnExploreMouseMove(object? sender, MouseEventArgs e)
    {
        if (_galaxyBitmap == null) return;

        var mmRect = MinimapRect();
        bool inMm  = mmRect.Contains(e.Location);

        if (inMm != _hoverInMinimap)
        {
            _hoverInMinimap = inMm;
            _explorePanel.Invalidate();
        }

        if (inMm)
        {
            _hoveredCell             = new(-1, -1);
            _explorePanel.Cursor = Cursors.Hand;
            return;
        }

        // Compute which grid cell the mouse is over (all explore bitmaps are square 512px)
        var dest = ZoomRect(_explorePanel.ClientSize, new Size(512, 512));

        if (!dest.Contains(e.Location))
        {
            _hoveredCell         = new(-1, -1);
            _explorePanel.Cursor = Cursors.Default;
            _explorePanel.Invalidate();
            return;
        }

        int n    = _tbExploreGrid.Value;
        int col  = Math.Clamp((int)((e.X - dest.X) / ((float)dest.Width  / n)), 0, n - 1);
        int row  = Math.Clamp((int)((e.Y - dest.Y) / ((float)dest.Height / n)), 0, n - 1);
        var cell = new Point(col, row);

        _explorePanel.Cursor = _zoomPath.Count < MaxZoomLevel ? Cursors.Hand : Cursors.Default;

        if (cell != _hoveredCell)
        {
            _hoveredCell = cell;
            _explorePanel.Invalidate();
        }
    }

    private void OnExploreMouseLeave(object? sender, EventArgs e)
    {
        _hoveredCell    = new(-1, -1);
        _hoverInMinimap = false;
        _explorePanel.Cursor = Cursors.Default;
        _explorePanel.Invalidate();
    }

    private void OnExploreClick(object? sender, MouseEventArgs e)
    {
        if (_galaxyBitmap == null) return;

        // Minimap click — navigate the zoom hierarchy
        if (MinimapRect().Contains(e.Location))
        {
            HandleMinimapClick(e.Location);
            return;
        }

        // Grid click — zoom in
        if (_hoveredCell.X < 0 || _zoomPath.Count >= MaxZoomLevel) return;

        _zoomPath.Add((_hoveredCell.X, _hoveredCell.Y));
        UpdateExploreUI();
        _explorePanel.Invalidate();
    }

    private void HandleMinimapClick(Point loc)
    {
        var mmRect = MinimapRect();
        const float mm = 160f;

        // Build the list of zoom-level rectangles (level 0 = full minimap, level i = region after i steps)
        var rects = new List<RectangleF>
        {
            new(mmRect.X, mmRect.Y, mm, mm)   // level 0
        };

        float nx = 0, ny = 0, ns = 1f;
        int   gn = _tbExploreGrid.Value;
        foreach (var (gx, gy) in _zoomPath)
        {
            float cs = ns / gn;
            float rx = nx + gx * cs;
            float ry = ny + gy * cs;
            rects.Add(new RectangleF(mmRect.X + rx * mm, mmRect.Y + ry * mm, cs * mm, cs * mm));
            nx = rx; ny = ry; ns = cs;
        }

        // Deepest rectangle that contains the click = target zoom level
        int target = 0;
        for (int i = rects.Count - 1; i >= 0; i--)
        {
            if (rects[i].Contains(loc)) { target = i; break; }
        }

        if (target < _zoomPath.Count)
        {
            _zoomPath.RemoveRange(target, _zoomPath.Count - target);
            UpdateExploreUI();
            _explorePanel.Invalidate();
        }
    }

    // ── explore helpers ───────────────────────────────────────────────────────

    private RectangleF CurrentRegion()
    {
        float x = 0, y = 0, size = 1f;
        int   n = _tbExploreGrid.Value;
        foreach (var (gx, gy) in _zoomPath)
        {
            float cs = size / n;
            x    += gx * cs;
            y    += gy * cs;
            size  = cs;
        }
        return new RectangleF(x, y, size, size);
    }

    private Rectangle MinimapRect()
    {
        const int mm = 160, margin = 12;
        return new Rectangle(
            _explorePanel.Width  - mm - margin,
            _explorePanel.Height - mm - margin,
            mm, mm);
    }

    private void ExploreZoomOut()
    {
        if (_zoomPath.Count == 0) return;
        _zoomPath.RemoveAt(_zoomPath.Count - 1);
        UpdateExploreUI();
        _explorePanel.Invalidate();
    }

    private void ExploreReset()
    {
        _zoomPath.Clear();
        UpdateExploreUI();
        _explorePanel.Invalidate();
    }

    private void UpdateExploreUI()
    {
        int level = _zoomPath.Count;
        _lblZoomLevel.Text  = $"Zoom Level: {level} / {MaxZoomLevel}";
        _btnZoomOut.Enabled = level > 0;

        var sb  = new StringBuilder();
        float nx = 0, ny = 0, ns = 1f;
        int   gn = _tbExploreGrid.Value;
        sb.AppendLine("◉ Galaxy");
        foreach (var (gx, gy) in _zoomPath)
        {
            float cs = ns / gn;
            nx += gx * cs; ny += gy * cs; ns = cs;
            sb.AppendLine($"  › Cell ({gx},{gy})  [{nx:F3}, {ny:F3}]");
        }
        if (level >= MaxZoomLevel) sb.AppendLine("  ★ → Star Cluster view");
        _lblZoomPath.Text = sb.ToString();

        UpdateChain();
    }

    // ── stars tab ─────────────────────────────────────────────────────────────

    private TabPage BuildStarsTab()
    {
        var tab = new TabPage("Stars")
        {
            BackColor = Color.FromArgb(28, 28, 28),
            Padding   = new Padding(0),
        };

        var side = new Panel
        {
            Dock      = DockStyle.Right,
            Width     = 220,
            BackColor = Color.FromArgb(38, 38, 38),
            Padding   = new Padding(12),
        };

        _starPanel = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _starPanel.Paint      += OnStarPaint;
        _starPanel.MouseDown  += OnStarMouseDown;
        _starPanel.MouseMove  += OnStarMouseMove;
        _starPanel.MouseUp    += OnStarMouseUp;
        _starPanel.MouseWheel += OnStarMouseWheel;
        _starPanel.Resize     += (_, _) => RequestStarRender();
        _starPanel.LostFocus  += (_, _) => _heldKeys.Clear(); // release all keys on focus loss

        tab.Controls.Add(_starPanel);
        tab.Controls.Add(side);

        int y = 12;
        void AddLabel(ref Label fld, string text, bool mono = false)
        {
            fld = new Label
            {
                Text      = text,
                Left      = 12, Top = y, Width = 196, Height = 32,
                ForeColor = Color.Silver, AutoSize  = false,
                Font      = mono ? new Font("Consolas", 7.5f) : new Font("Segoe UI", 8.5f),
            };
            side.Controls.Add(fld);
            y += 36;
        }

        Label dummy = null!;
        AddLabel(ref dummy, "STAR NAVIGATOR", false);
        dummy.ForeColor = Color.White;
        dummy.Font      = new Font("Segoe UI", 9f, FontStyle.Bold);

        Divider(side, y); y += 16;

        AddLabel(ref _lblStarPos,    "Position: —",     mono: true);
        AddLabel(ref _lblStarCount,  "Stars visible: —", mono: false);
        AddLabel(ref _lblStarChunks, "Chunks loaded: —", mono: false);
        AddLabel(ref _lblGcDist,     "GC: —",            mono: false);
        AddLabel(ref _lblCacheStats, "Cache: —",         mono: true);
        _lblCacheStats.Height = 48; // two lines

        Divider(side, y); y += 16;

        // Speed controls
        var lblSpeed = new Label
        {
            Text = SpeedLabel(), Left = 12, Top = y, Width = 196, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        };
        side.Controls.Add(lblSpeed);
        y += 26;

        var btnFaster = MakeButton("▲ Faster", Color.FromArgb(50, 70, 50),  12, y, 94);
        var btnSlower = MakeButton("▼ Slower", Color.FromArgb(70, 50, 50), 114, y, 94);
        btnFaster.Click += (_, _) =>
        {
            _starSpeedIndex = Math.Min(_starSpeedIndex + 1, SpeedLevels.Length - 1);
            lblSpeed.Text   = SpeedLabel();
        };
        btnSlower.Click += (_, _) =>
        {
            _starSpeedIndex = Math.Max(_starSpeedIndex - 1, 0);
            lblSpeed.Text   = SpeedLabel();
        };
        side.Controls.AddRange([btnFaster, btnSlower]);
        y += 42;

        Divider(side, y); y += 16;

        var btnReset = MakeButton("↺ Reset Camera", Color.FromArgb(50, 50, 90), 12, y, 196);
        btnReset.Click += (_, _) => { _camera = new Camera3D(); RequestStarRender(); };
        side.Controls.Add(btnReset);
        y += 46;

        Divider(side, y); y += 16;

        side.Controls.Add(new Label
        {
            Text      = "Drag  →  look\nScroll  →  fly\nW/S  →  forward/back\nA/D  →  strafe\nQ/E  →  up/down",
            Left      = 12, Top = y, Width = 196, Height = 80,
            ForeColor = Color.FromArgb(100, 100, 100),
            Font      = new Font("Segoe UI", 8f),
            AutoSize  = false,
        });

        return tab;
    }

    private void OnStarsTabActivated()
    {
        if (_galaxy == null || _starPanel == null) return;

        if (_starCache == null)
        {
            var gen    = new StarGenerator();
            _starCache = new StarChunkCache(_galaxy.Params, gen);
            _starStream = new StarStreamService(_starCache);
            // ChunkReady fires from the stream background thread; marshal back to
            // the UI thread before calling RequestStarRender so that only one
            // thread ever touches _starRenderTask / _starBitmapBack, eliminating
            // the "Object is currently in use elsewhere" race at LockBits.
            _starStream.ChunkReady += _ => _starPanel.BeginInvoke(RequestStarRender);
        }

        _starPanel.Focus();
        RequestStarRender();
        _starStream?.NotifyCameraPosition(_camera.Position);
    }

    // ── star rendering ────────────────────────────────────────────────────────

    private void RequestStarRender()
    {
        _starRenderPending = true;
        if (_starRenderTask is { IsCompleted: false }) return;

        _starRenderTask = Task.Run(() =>
        {
            while (_starRenderPending)
            {
                _starRenderPending = false;
                DoStarRender();
            }
        });
    }

    private void DoStarRender()
    {
        if (_starCache == null || _starPanel == null) return;

        int w = _starPanel.Width, h = _starPanel.Height;
        if (w <= 0 || h <= 0) return;

        // Render radius: 6 chunks × 10 ly = 60 ly, matching MaxDepth = 0.0012
        const int renderRadiusXZ = 6;
        const int renderRadiusY  = 2;

        // Always ensure every chunk in the render volume is cached before drawing.
        // GetOrGenerate is a no-op for already-cached chunks, so this is cheap on
        // subsequent frames.  On a cache miss the C++ generator runs in < 0.1 ms,
        // so 845 chunks (13×13×5) adds at most ~80 ms on a cold first frame.
        if (_starCache != null)
        {
            var center = _starCache.WorldToChunk(_camera.Position);
            for (int dx = -renderRadiusXZ; dx <= renderRadiusXZ; dx++)
            for (int dy = -renderRadiusY;  dy <= renderRadiusY;  dy++)
            for (int dz = -renderRadiusXZ; dz <= renderRadiusXZ; dz++)
                _starCache.GetOrGenerate(new ChunkCoord(center.X + dx, center.Y + dy, center.Z + dz));
        }

        var chunks = _starStream?.GetVisibleChunks(_camera.Position, renderRadiusXZ, renderRadiusY)
                                  .ToList() ?? [];

        int totalStars = chunks.Sum(c => c.Stars.Length);

        // Pass the back buffer for reuse (eliminates ~8 MB LOH alloc per frame).
        // The render thread is the only writer of _starBitmapBack; the UI thread
        // will swap it out under Invoke() before the next render begins.
        var (bmp, projected) = _starRenderer.Render(
            chunks, _camera, w, h,
            reuseBitmap:   _starBitmapBack,
            maxHoverDepth: MaxHoverDepth);

        if (_starPanel.IsHandleCreated)
        {
            // Position in light-years for display
            float px = _camera.Position.X * LyPerUnit;
            float py = _camera.Position.Y * LyPerUnit;
            float pz = _camera.Position.Z * LyPerUnit;
            string FmtLy(float v) => $"{(v >= 0 ? "+" : "")}{v:N0} ly";

            // Distance to galactic centre for side-panel display
            float gcNorm  = _camera.Position.Length();
            float gcLy    = gcNorm * LyPerUnit;
            string gcStr  = gcLy < 1000f ? $"GC: {gcLy:F0} ly"
                                         : $"GC: {gcLy / 1000f:F1} kly";

            // Capture oldBack before entering Invoke so we can detect a resize.
            // The renderer returns a brand-new Bitmap when dimensions changed; in
            // that case the previous back buffer was not reused and must be disposed
            // here (it won't be recycled into _starBitmapBack).
            var oldBack = _starBitmapBack;

            _starPanel.Invoke(() =>
            {
                // Double-buffer swap:
                //   new front  = bmp  (just rendered)
                //   new back   = old front (will be written next frame)
                // If the renderer couldn't reuse oldBack (resize), dispose it now
                // so we don't leak the bitmap.
                if (bmp != oldBack)
                    oldBack?.Dispose();

                _starBitmapBack = _starBitmap;   // old front → back buffer
                _starBitmap     = bmp;           // freshly rendered → front
                _projectedStars = projected;
                _hoveredStar    = null;
                _lblStarPos.Text    = $"{FmtLy(px)}\n{FmtLy(py)}\n{FmtLy(pz)}";
                _lblStarCount.Text  = $"Stars visible: {totalStars:N0}";
                _lblStarChunks.Text = $"Chunks: {chunks.Count}";
                _lblGcDist.Text     = gcStr;

                // Refresh cache stats at most once per second — GetStats() walks
                // the dictionary to count stars, so we keep it off the hot path.
                if (_starCache != null && DateTime.UtcNow >= _nextCacheStatsRefresh)
                {
                    _nextCacheStatsRefresh = DateTime.UtcNow.AddSeconds(1);
                    var cs = _starCache.GetStats();
                    float hitPct = float.IsNaN(cs.HitRate) ? 0f : cs.HitRate * 100f;
                    string genMs = float.IsNaN(cs.AvgGenerationMs) ? "—" : $"{cs.AvgGenerationMs:F1}ms";
                    _lblCacheStats.Text =
                        $"{cs.Count:N0}/{cs.Capacity:N0} ({cs.FillPct:F0}%)\n" +
                        $"Hit {hitPct:F1}%  Ev {cs.Evictions:N0}  Gen {genMs}";
                }
                _starPanel.Invalidate();
            });
        }
        else
        {
            // Handle not yet created (e.g. form still initialising).
            // Only dispose bmp if it is a freshly allocated bitmap — if it is
            // the same object as _starBitmapBack (reuse case) we must not dispose
            // it, because the render thread will pass it in again next frame.
            if (bmp != _starBitmapBack)
                bmp.Dispose();
        }
    }

    private void OnStarPaint(object? sender, PaintEventArgs e)
    {
        if (_galaxy == null)
        {
            var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            };
            using var b = new SolidBrush(Color.FromArgb(55, 55, 55));
            e.Graphics.DrawString("Generate a galaxy on the Galaxy tab first",
                new Font("Segoe UI", 13f), b,
                new RectangleF(0, 0, _starPanel.Width, _starPanel.Height), fmt);
            return;
        }

        if (_starBitmap != null)
            e.Graphics.DrawImageUnscaled(_starBitmap, 0, 0);
        else
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var b = new SolidBrush(Color.FromArgb(40, 40, 40));
            e.Graphics.DrawString("Loading…", new Font("Segoe UI", 13f), b,
                new RectangleF(0, 0, _starPanel.Width, _starPanel.Height), fmt);
        }

        DrawSmbhInStarView(e.Graphics, _starPanel.Width, _starPanel.Height);
        DrawStarMinimap(e.Graphics);

        // Hover overlay
        if (_hoveredStar is { } hs)
            DrawStarTooltip(e.Graphics, hs);
    }

    // ── star camera input ─────────────────────────────────────────────────────

    private void OnStarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _starDragging    = true;
            _starWasDragging = false;    // reset on each press
            _starMouseLast   = e.Location;
            _starPanel.Focus();
        }
    }

    private void OnStarMouseUp(object? sender, MouseEventArgs e)
    {
        _starDragging = false;
        // Click (not a drag) on a hovered star → open System tab
        if (!_starWasDragging && e.Button == MouseButtons.Left && _hoveredSystem != null)
        {
            _selectedSystem   = _hoveredSystem;
            _selectedBody     = null;
            _selectedBodyMoons = null;
            UpdateSystemSidebar();
            _tabs.SelectedTab = _tabs.TabPages.OfType<TabPage>()
                                     .FirstOrDefault(t => t.Text == "System");
        }
    }

    private void OnStarMouseMove(object? sender, MouseEventArgs e)
    {
        if (_starDragging)
        {
            float dx = e.X - _starMouseLast.X;
            float dy = e.Y - _starMouseLast.Y;
            if (dx != 0 || dy != 0) _starWasDragging = true;   // real motion = drag
            _starMouseLast = e.Location;
            _camera.Look(-dx * 0.003f, dy * 0.003f);
            RequestStarRender();
            _starStream?.NotifyCameraPosition(_camera.Position);
            return;
        }

        // Hover hit-test:
        //   • Stars beyond MaxHoverDepth are invisible to hover.
        //   • Hit zone = the star's rendered disc (pixelRadius), min MinHitPixels.
        //   • When discs overlap the frontmost star (smallest depth) wins —
        //     so a large foreground disc always beats anything behind it.
        var stars = _projectedStars;
        ProjectedStar? best = null;
        float bestDepth = float.MaxValue;
        float mx = e.X, my = e.Y;

        foreach (ref readonly var ps in stars.AsSpan())
        {
            if (ps.Depth > MaxHoverDepth) continue;

            float ddx = ps.ScreenX - mx, ddy = ps.ScreenY - my;
            float d2  = ddx * ddx + ddy * ddy;
            float hit = MathF.Max(ps.PixelRadius, MinHitPixels);
            if (d2 <= hit * hit && ps.Depth < bestDepth)
            {
                bestDepth = ps.Depth;
                best = ps;
            }
        }

        bool changed = best?.Star != _hoveredStar?.Star;
        _hoveredStar = best;
        if (changed)
        {
            _hoveredSystem = (best.HasValue && _galaxy != null)
                ? _systemGenerator.Generate(best.Value.Star, _galaxy.Params.Age,
                                            _galaxy.Params.Seed, StarDesignation(best.Value.Star))
                : null;
            _starPanel.Invalidate();
        }
    }

    private void OnStarMouseWheel(object? sender, MouseEventArgs e)
    {
        _camera.MoveForward(e.Delta > 0 ? _starMoveSpeed : -_starMoveSpeed);
        RequestStarRender();
        _starStream?.NotifyCameraPosition(_camera.Position);
    }

    // ── SMBH overlay ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the supermassive black hole as a screen overlay.
    ///
    /// Two distance bands:
    ///   Far  (≤ 500 ly / 0.01 normalised) — faint amber beacon glow.
    ///   Near (≤ render radius = MaxDepth) — full accretion disk, event horizon
    ///         shadow, relativistic jets, and an info label.
    /// </summary>
    private void DrawSmbhInStarView(Graphics g, int w, int h)
    {
        if (_galaxy?.BlackHole == null) return;

        var bh = _galaxy.BlackHole;
        var (sx, sy, depth) = _camera.Project(GalacticBlackHole.Position, w, h);
        if (float.IsNaN(sx) || depth <= 0f) return;

        var sm = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        float act = bh.ActivityLevel;

        // ── far beacon glow (visible up to ~500 ly) ───────────────────────────
        const float FarGlowDepth = 0.010f;   // 500 ly

        if (depth <= FarGlowDepth)
        {
            float t = 1f - depth / FarGlowDepth;               // 0..1 (brightest when nearest)
            float br = 18f + t * 10f;                           // beacon radius px

            int a3 = (int)((t * 55 + act * 30));
            int a2 = (int)((t * 80 + act * 50));
            int a1 = (int)((t * 130 + act * 70));

            using var og = new SolidBrush(Color.FromArgb(Math.Min(a3, 255), 160, 70, 10));
            using var mg = new SolidBrush(Color.FromArgb(Math.Min(a2, 255), 220, 120, 30));
            using var ig = new SolidBrush(Color.FromArgb(Math.Min(a1, 255), 255, 190, 60));

            g.FillEllipse(og, sx - br * 2, sy - br * 2, br * 4, br * 4);
            g.FillEllipse(mg, sx - br,     sy - br,     br * 2, br * 2);
            g.FillEllipse(ig, sx - br * 0.4f, sy - br * 0.4f, br * 0.8f, br * 0.8f);
        }

        // ── close-range full render (within star-field render radius) ─────────
        if (depth <= _starRenderer.MaxDepth)
        {
            // Shadow disc: sized like a very large star (BaseStarSize × 2 / depth)
            float shadowR = Math.Min((_starRenderer.BaseStarSize * 2f) / depth, 110f);
            float glowR1  = shadowR * 2.5f;
            float glowR2  = shadowR * 4.5f;
            float glowR3  = shadowR * 8.0f;

            // Outer corona
            using var c3 = new SolidBrush(Color.FromArgb((int)(18 + act * 22), 150, 65, 10));
            g.FillEllipse(c3, sx - glowR3, sy - glowR3, glowR3 * 2, glowR3 * 2);

            // Mid glow
            using var c2 = new SolidBrush(Color.FromArgb((int)(40 + act * 55), 225, 130, 28));
            g.FillEllipse(c2, sx - glowR2, sy - glowR2, glowR2 * 2, glowR2 * 2);

            // Inner bright ring
            using var c1 = new SolidBrush(Color.FromArgb((int)(80 + act * 110), 255, 215, 90));
            g.FillEllipse(c1, sx - glowR1, sy - glowR1, glowR1 * 2, glowR1 * 2);

            // Event horizon shadow (opaque black — blocks everything behind it)
            using (var shadow = new SolidBrush(Color.Black))
                g.FillEllipse(shadow, sx - shadowR, sy - shadowR, shadowR * 2, shadowR * 2);

            // Relativistic jets — screen-space vertical lines, blue-white, fading
            if (act > 0.08f)
            {
                float jetLen = shadowR * 7f;
                float jetW   = Math.Max(1.5f, shadowR * 0.12f);
                int   jetA   = (int)(act * 110);

                using var jetPen = new Pen(Color.FromArgb(jetA, 160, 215, 255), jetW);
                jetPen.StartCap = System.Drawing.Drawing2D.LineCap.Flat;
                jetPen.EndCap   = System.Drawing.Drawing2D.LineCap.Flat;

                // Upper jet
                using var fadeUp = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new PointF(sx, sy), new PointF(sx, sy - jetLen),
                    Color.FromArgb(jetA, 160, 215, 255), Color.FromArgb(0, 160, 215, 255));
                g.FillRectangle(fadeUp,
                    sx - jetW / 2f, sy - jetLen, jetW, jetLen);

                // Lower jet (counter-jet, slightly dimmer)
                using var fadeDn = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new PointF(sx, sy), new PointF(sx, sy + jetLen),
                    Color.FromArgb((int)(jetA * 0.7f), 160, 215, 255), Color.FromArgb(0, 160, 215, 255));
                g.FillRectangle(fadeDn,
                    sx - jetW / 2f, sy, jetW, jetLen);
            }

            // ── info label ────────────────────────────────────────────────────
            float distLy = depth * LyPerUnit;
            string distStr = distLy < 10f ? $"{distLy:F2} ly" : $"{distLy:F0} ly";
            string line1   = bh.Designation;
            string line2   = $"SMBH  ·  {bh.MassDisplay}  ·  {distStr}";

            using var font1 = new Font("Consolas", 9f);
            using var font2 = new Font("Consolas", 7.5f);
            float lineH1 = font1.Height + 2f;
            float lineH2 = font2.Height + 2f;
            float boxW   = Math.Max(g.MeasureString(line1, font1).Width,
                                    g.MeasureString(line2, font2).Width) + 16f;
            float boxH   = lineH1 + lineH2 + 10f;

            float lx = sx + shadowR + 8f;
            float ly = sy - boxH / 2f;
            if (lx + boxW > w - 4) lx = sx - shadowR - boxW - 8f;
            if (ly < 4)            ly = 4f;
            if (ly + boxH > h - 4) ly = h - boxH - 4f;

            using var bg     = new SolidBrush(Color.FromArgb(200, 6, 4, 14));
            using var border = new Pen(Color.FromArgb(160, 255, 165, 40));
            g.FillRectangle(bg, lx, ly, boxW, boxH);
            g.DrawRectangle(border, lx, ly, boxW, boxH);

            using var fgTitle = new SolidBrush(Color.FromArgb(255, 255, 185, 60));
            using var fgBody  = new SolidBrush(Color.FromArgb(210, 200, 155, 90));
            g.DrawString(line1, font1, fgTitle, lx + 8f, ly + 5f);
            g.DrawString(line2, font2, fgBody,  lx + 8f, ly + 5f + lineH1);
        }

        g.SmoothingMode = sm;
    }

    // ── star minimap ──────────────────────────────────────────────────────────

    private void DrawStarMinimap(Graphics g)
    {
        if (_galaxyBitmap == null) return;

        const int mm     = 160;
        const int margin = 12;
        var mmRect = new Rectangle(margin, _starPanel.Height - mm - margin, mm, mm);

        // Galaxy image
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.FillRectangle(Brushes.Black, mmRect);
        g.DrawImage(_galaxyBitmap, mmRect);

        var sm = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Camera position in galaxy normalised space:
        // The face-on bitmap maps X→left/right and Z→up/down in the image.
        // Galaxy spans roughly [-1,1], so normalise to [0,1] then scale to mm.
        float nx = Math.Clamp((_camera.Position.X + 1f) / 2f, 0f, 1f);
        float nz = Math.Clamp((_camera.Position.Z + 1f) / 2f, 0f, 1f);
        float dotX = mmRect.X + nx * mm;
        float dotY = mmRect.Y + nz * mm;

        // Direction indicator — project forward vector onto XZ plane
        var fwd = _camera.Forward;
        float fwdLen = 14f;
        float arrowX = dotX + fwd.X * fwdLen;
        float arrowY = dotY + fwd.Z * fwdLen;

        using var dirPen = new Pen(Color.FromArgb(210, 255, 220, 80), 1.5f);
        g.DrawLine(dirPen, dotX, dotY, arrowX, arrowY);

        // Arrowhead
        float angle = MathF.Atan2(fwd.Z, fwd.X);
        float aw = 5f;
        g.DrawLine(dirPen,
            arrowX, arrowY,
            arrowX - aw * MathF.Cos(angle - 0.5f),
            arrowY - aw * MathF.Sin(angle - 0.5f));
        g.DrawLine(dirPen,
            arrowX, arrowY,
            arrowX - aw * MathF.Cos(angle + 0.5f),
            arrowY - aw * MathF.Sin(angle + 0.5f));

        // Glow halo then solid dot for camera position
        using var glowBrush = new SolidBrush(Color.FromArgb(70, 255, 220, 80));
        using var dotBrush  = new SolidBrush(Color.FromArgb(255, 255, 220, 80));
        g.FillEllipse(glowBrush, dotX - 7f, dotY - 7f, 14f, 14f);
        g.FillEllipse(dotBrush,  dotX - 3f, dotY - 3f,  6f,  6f);

        g.SmoothingMode = sm;

        // Border + label
        g.DrawRectangle(Pens.DimGray, mmRect);
        using var labelFont  = new Font("Segoe UI", 7f);
        using var labelBrush = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
        using var labelBg    = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
        var labelRect = new RectangleF(mmRect.X, mmRect.Y, mm, 15f);
        g.FillRectangle(labelBg, labelRect);
        g.DrawString("Galaxy Overview", labelFont, labelBrush, mmRect.X + 4, mmRect.Y + 2);
    }

    // ── star hover overlay ────────────────────────────────────────────────────

    private void DrawStarTooltip(Graphics g, ProjectedStar ps)
    {
        var star = ps.Star;

        string designation = StarDesignation(star);
        string classDesc   = StarClassDescription(star);
        float  distLy      = ps.Depth * LyPerUnit;
        string distStr     = distLy < 10f   ? $"{distLy:F2} ly"
                           : distLy < 1000f ? $"{distLy:F0} ly"
                                            : $"{distLy / 1000f:F2} kly";
        string flags  = star.IsSupergiant ? "  ★ Supergiant"
                      : star.IsGiant      ? "  ★ Giant"      : "";
        string binary = star.IsBinary     ? "  ⚭ Binary"     : "";

        // Region name — look up the chunk that contains this star
        string regionLine = "";
        if (_hoveredStar.HasValue)
        {
            var chunk = _starCache?.WorldToChunk(new System.Numerics.Vector3(star.X, star.Y, star.Z));
            if (chunk.HasValue && _starCache != null)
            {
                // Find the StarChunk to get its RegionType
                if (_starCache.TryGet(chunk.Value, out var sc) && sc != null)
                {
                    string sci    = RegionNameGenerator.ScientificName(
                                        chunk.Value.X, chunk.Value.Y, chunk.Value.Z,
                                        sc.Region, _galaxy!.Params.Seed);
                    string poetic = RegionNameGenerator.PoeticName(
                                        chunk.Value.X, chunk.Value.Y, chunk.Value.Z,
                                        sc.Region, _galaxy!.Params.Seed);
                    string rtype  = sc.Region switch
                    {
                        RegionType.Nursery   => "Star-forming Region",
                        RegionType.Halo      => "Stellar Halo",
                        RegionType.Bulge     => "Galactic Bulge",
                        RegionType.Void      => "Deep Void",
                        RegionType.DiskInter => "Inter-arm Disk",
                        _                    => "Spiral Arm",
                    };
                    regionLine = $"{poetic}  [{sci}]  · {rtype}";
                }
            }
        }

        string clusterLine = "";
        if (_galaxy?.ClusterMap != null)
        {
            var starPos = new Vector3(star.X, star.Y, star.Z);
            var nearest = _galaxy.ClusterMap.FindNearest(starPos);
            if (nearest != null)
            {
                float clusterDistLy = Vector3.Distance(nearest.Position, starPos) * LyPerUnit;
                string ctype = nearest.Type switch
                {
                    ClusterType.Nebula          => "Nebula",
                    ClusterType.OpenCluster     => "Open Cluster",
                    ClusterType.GlobularCluster => "Globular Cluster",
                    _                           => "Cluster",
                };
                clusterLine = $"⊕ {nearest.PoeticName}  [{nearest.ScientificName}]  · {ctype}  ({clusterDistLy:F0} ly)";
            }
        }

        string systemLine = _hoveredSystem != null ? BuildSystemSummaryLine(_hoveredSystem) : "";

        var lines = new[]
        {
            designation,
            classDesc + flags + binary,
            $"Distance: {distStr}",
            $"Luminosity: {star.Luminosity / 255f:P0}",
            clusterLine,
            regionLine,
            systemLine,
        }.Where(l => l.Length > 0).ToArray();

        using var font = new Font("Consolas", 9f);
        float lineH = font.Height + 3f;

        const float OrreryH    = 56f;
        const float OrreryMinW = 300f;
        const float HintH      = 14f;    // "click to open" line
        bool  showOrrery = _hoveredSystem != null;
        float textW = lines.Max(l => g.MeasureString(l, font).Width) + 20f;
        float boxW  = showOrrery ? Math.Max(textW, OrreryMinW + 20f) : textW;
        float boxH  = lines.Length * lineH + 16f + (showOrrery ? OrreryH + HintH + 10f : 0f);

        // ── position info box (stays inside panel) ────────────────────────────
        float ringR = MathF.Max(ps.PixelRadius + 5f, 11f);
        float gap   = ringR + 8f;

        float tx = ps.ScreenX + gap;
        float ty = ps.ScreenY - boxH / 2f;
        if (tx + boxW > _starPanel.Width  - 4) tx = ps.ScreenX - gap - boxW;
        if (ty < 4)                            ty = 4f;
        if (ty + boxH > _starPanel.Height - 4) ty = _starPanel.Height - boxH - 4f;

        var sm = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // ── highlight ring around star ────────────────────────────────────────
        // Outer soft glow
        using var glowPen = new Pen(Color.FromArgb(55, 255, 240, 120), 6f);
        g.DrawEllipse(glowPen,
            ps.ScreenX - ringR, ps.ScreenY - ringR, ringR * 2, ringR * 2);
        // Crisp inner ring
        using var ringPen = new Pen(Color.FromArgb(200, 255, 240, 120), 1.5f);
        g.DrawEllipse(ringPen,
            ps.ScreenX - ringR, ps.ScreenY - ringR, ringR * 2, ringR * 2);

        // ── connector line from ring edge to info box ─────────────────────────
        float boxCX = tx + boxW / 2f;
        float boxCY = ty + boxH / 2f;
        float angle  = MathF.Atan2(boxCY - ps.ScreenY, boxCX - ps.ScreenX);
        float lineX0 = ps.ScreenX + ringR * MathF.Cos(angle);
        float lineY0 = ps.ScreenY + ringR * MathF.Sin(angle);
        // Attach to the near edge of the box
        float lineX1 = tx + boxW / 2f < ps.ScreenX ? tx + boxW : tx;
        float lineY1 = ty + boxH / 2f;

        using var linePen = new Pen(Color.FromArgb(110, 255, 240, 120), 1f);
        linePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
        g.DrawLine(linePen, lineX0, lineY0, lineX1, lineY1);

        g.SmoothingMode = sm;

        // ── info box ──────────────────────────────────────────────────────────
        using var bg     = new SolidBrush(Color.FromArgb(215, 8, 10, 22));
        using var border = new Pen(Color.FromArgb(150, 180, 220, 255));
        g.FillRectangle(bg, tx, ty, boxW, boxH);
        g.DrawRectangle(border, tx, ty, boxW, boxH);

        using var headerBrush = new SolidBrush(Color.White);
        using var bodyBrush   = new SolidBrush(Color.FromArgb(170, 200, 220));
        float ly = ty + 8f;
        for (int i = 0; i < lines.Length; i++)
        {
            g.DrawString(lines[i], font, i == 0 ? headerBrush : bodyBrush, tx + 10f, ly);
            ly += lineH;
        }

        // Orrery strip + click hint
        if (showOrrery && _hoveredSystem != null)
        {
            float sepY = ly + 1f;
            using var sepPen = new Pen(Color.FromArgb(55, 150, 150, 150));
            g.DrawLine(sepPen, tx + 8f, sepY, tx + boxW - 8f, sepY);
            DrawOrrery(g, _hoveredSystem, tx + 8f, sepY + 4f, boxW - 16f, OrreryH);

            // "click to open" hint
            using var hintFnt = new Font("Segoe UI", 6.5f);
            using var hintBrs = new SolidBrush(Color.FromArgb(70, 160, 200, 160));
            float hintY = ty + boxH - hintFnt.Height - 3f;
            g.DrawString("▶ click to open System view", hintFnt, hintBrs,
                tx + boxW - g.MeasureString("▶ click to open System view", hintFnt).Width - 6f,
                hintY);
        }
    }

    // ── system summary + orrery ───────────────────────────────────────────────

    // Delegate — logic lives in BodyStats.
    private static string BuildSystemSummaryLine(StarSystem sys) =>
        BodyStats.BuildSystemSummary(sys);

    /// <summary>
    /// Draws a mini log-scale orrery strip inside the star tooltip.
    /// Orbital range auto-fits to the system's extent.
    /// </summary>
    private static void DrawOrrery(
        Graphics g, StarSystem sys, float ox, float oy, float ow, float oh)
    {
        // Layout math lives in OrreryLayout — this method is GDI+ rendering only.
        var layout = new OrreryLayout(sys);

        const float StarX     = 13f;
        const float ScaleLeft = 26f;
        const float Pad       = 4f;

        float NX(float norm) => ox + ScaleLeft + norm * (ow - ScaleLeft - Pad);
        float cy = oy + oh * 0.46f;

        // ── background ────────────────────────────────────────────────────
        using var bg = new SolidBrush(Color.FromArgb(16, 255, 255, 255));
        g.FillRectangle(bg, ox, oy, ow, oh);

        // ── HZ band ───────────────────────────────────────────────────────
        if (!float.IsNaN(layout.HzInnerNorm))
        {
            float x1 = NX(layout.HzInnerNorm), x2 = NX(layout.HzOuterNorm);
            using var hzFill = new SolidBrush(Color.FromArgb(22, 55, 200, 55));
            g.FillRectangle(hzFill, x1, oy + 2, x2 - x1, oh - 4);
            using var hzEdge = new Pen(Color.FromArgb(32, 70, 200, 70), 0.5f);
            g.DrawLine(hzEdge, x1, oy + 2, x1, oy + oh - 2);
            g.DrawLine(hzEdge, x2, oy + 2, x2, oy + oh - 2);
        }

        // ── snow line ─────────────────────────────────────────────────────
        if (!float.IsNaN(layout.SnowLineNorm))
        {
            float sx = NX(layout.SnowLineNorm);
            using var snowPen = new Pen(Color.FromArgb(50, 130, 200, 255), 0.8f);
            snowPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawLine(snowPen, sx, oy + 2, sx, oy + oh - 2);
        }

        // ── orbit axis ────────────────────────────────────────────────────
        using var axisPen = new Pen(Color.FromArgb(45, 180, 180, 180), 0.5f);
        g.DrawLine(axisPen, ox + ScaleLeft, cy, ox + ow - 2, cy);

        // ── host star ─────────────────────────────────────────────────────
        const float StarR = 6.5f;
        var (sr, sg, sb) = layout.StarColor;
        var sc = Color.FromArgb(sr, sg, sb);
        using var starGlow  = new SolidBrush(Color.FromArgb(28, sc.R, sc.G, sc.B));
        using var starBrush = new SolidBrush(sc);
        g.FillEllipse(starGlow,  ox + StarX - StarR * 1.9f, cy - StarR * 1.9f, StarR * 3.8f, StarR * 3.8f);
        g.FillEllipse(starBrush, ox + StarX - StarR, cy - StarR, StarR * 2f, StarR * 2f);

        // ── companion hint (far right, small disc only — no hover in tooltip) ──
        foreach (var ci in layout.Companions)
        {
            var (cr, cg, cb) = ci.Color;
            float cx2 = ox + ow - 7f - ci.CompanionIndex * 12f;
            using var compBrush = new SolidBrush(Color.FromArgb(150, cr, cg, cb));
            g.FillEllipse(compBrush, cx2 - 3.5f, cy - 3.5f, 7f, 7f);
            using var lbF   = new SolidBrush(Color.FromArgb(90, 200, 200, 200));
            using var lbFnt = new Font("Segoe UI", 6f);
            g.DrawString(ci.Label, lbFnt, lbF, cx2 - 3f, cy - 14f);
        }

        // ── orbital bodies ────────────────────────────────────────────────
        foreach (var item in layout.Bodies)
        {
            float bx = NX(item.NormX);
            if (bx < ox + ScaleLeft || bx > ox + ow) continue;

            if (item.IsBelt)
            {
                using var beltPen = new Pen(Color.FromArgb(75, 140, 120, 85), 1.2f);
                beltPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                g.DrawLine(beltPen, bx, oy + 6, bx, oy + oh - 6);
                continue;
            }

            float br = item.Radius;   // tooltip uses 1× scale
            var (br2, bg2, bb2) = item.Color;
            var col = Color.FromArgb(br2, bg2, bb2);

            if (item.Body.HasRings)
            {
                using var rp = new Pen(Color.FromArgb(125, col.R, col.G, col.B), 0.8f);
                g.DrawEllipse(rp, bx - br * 2.3f, cy - br * 0.35f, br * 4.6f, br * 0.7f);
            }

            using var bb = new SolidBrush(col);
            g.FillEllipse(bb, bx - br, cy - br, br * 2, br * 2);

            if (item.Body.InHabitableZone)
            {
                using var hzDot = new SolidBrush(Color.FromArgb(185, 55, 215, 55));
                g.FillEllipse(hzDot, bx - 2.5f, cy + br + 3f, 5f, 5f);
            }
        }
    }

    // Thin WinForms wrappers — the data lives in SystemVisuals (framework-agnostic).
    private static Color OrrerySpectralColor(SpectralClass cls)
    { var (r,g,b) = SystemVisuals.SpectralRgb(cls); return Color.FromArgb(r, g, b); }

    private static float OrreryBodyRadius(BodyType type) =>
        SystemVisuals.BodyRadius(type);

    private static Color OrreryBodyColor(BodyType type)
    { var (r,g,b) = SystemVisuals.BodyRgb(type); return Color.FromArgb(r, g, b); }

    // Delegates — logic lives in SystemVisuals.
    private static string StarDesignation(Star star)       => SystemVisuals.StarDesignation(star);
    private static string StarClassDescription(Star star)  => SystemVisuals.SpectralDescription(star.Class);

    private string SpeedLabel()
    {
        int level = _starSpeedIndex + 1;                          // 1-based for display
        float ly  = _starMoveSpeed * LyPerUnit;
        string lyStr = ly < 1f ? $"{ly:F2} ly/step"
                     : ly < 10f ? $"{ly:F1} ly/step"
                     : $"{ly:F0} ly/step";
        return $"Speed: {level} / {SpeedLevels.Length}  ({lyStr})";
    }

    // Track key-down so the movement timer can act on it immediately,
    // with no OS key-repeat delay.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_starPanel != null && _starPanel.Focused && _starCache != null)
        {
            switch (keyData)
            {
                case Keys.W: case Keys.S:
                case Keys.A: case Keys.D:
                case Keys.Q: case Keys.E:
                    _heldKeys.Add(keyData);
                    return true; // suppress — movement is applied in OnMoveTick
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _heldKeys.Remove(e.KeyCode);
        base.OnKeyUp(e);
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        if (_starPanel == null || !_starPanel.Focused || _heldKeys.Count == 0) return;

        bool moved = false;
        if (_heldKeys.Contains(Keys.W)) { _camera.MoveForward( _starMoveSpeed); moved = true; }
        if (_heldKeys.Contains(Keys.S)) { _camera.MoveForward(-_starMoveSpeed); moved = true; }
        if (_heldKeys.Contains(Keys.A)) { _camera.MoveRight(  -_starMoveSpeed); moved = true; }
        if (_heldKeys.Contains(Keys.D)) { _camera.MoveRight(   _starMoveSpeed); moved = true; }
        if (_heldKeys.Contains(Keys.Q)) { _camera.MoveUp(      _starMoveSpeed); moved = true; }
        if (_heldKeys.Contains(Keys.E)) { _camera.MoveUp(     -_starMoveSpeed); moved = true; }

        if (moved)
        {
            RequestStarRender();
            _starStream?.NotifyCameraPosition(_camera.Position);
        }
    }

    // ── system tab ────────────────────────────────────────────────────────────

    private TabPage BuildSystemTab()
    {
        var tab = new TabPage("System")
        {
            BackColor = Color.FromArgb(28, 28, 28),
            Padding   = new Padding(0),
        };

        var side = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 280,
            BackColor = Color.FromArgb(38, 38, 38),
            Padding   = new Padding(0),
        };

        _systemPanel = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _systemPanel.Paint      += OnSystemPaint;
        _systemPanel.MouseMove  += OnSystemMouseMove;
        _systemPanel.MouseClick += OnSystemMouseClick;
        _systemPanel.MouseLeave += (_, _) =>
        {
            _sysHoveredBody = null; _sysHoveredMoon = null;
            _systemPanel.Cursor = Cursors.Default;
            _systemPanel.Invalidate();
        };

        tab.Controls.Add(_systemPanel);
        tab.Controls.Add(side);

        // ── sidebar ───────────────────────────────────────────────────────────
        int y = 14;
        void Add(Control c) { side.Controls.Add(c); }

        // Title
        Add(new Label
        {
            Text = "SYSTEM VIEW", Left = 14, Top = y, Width = 252, Height = 22,
            ForeColor = Color.White, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = false,
        });
        y += 26; Divider(side, y); y += 14;

        // System name (designation)
        _lblSysName = new Label
        {
            Text = "—", Left = 14, Top = y, Width = 252, Height = 22,
            ForeColor = Color.White, Font = new Font("Consolas", 10f, FontStyle.Bold),
            AutoSize = false,
        };
        Add(_lblSysName); y += 26;

        // Star class + luminosity (2 lines)
        _lblSysInfo = new Label
        {
            Text = "—", Left = 14, Top = y, Width = 252, Height = 46,
            ForeColor = Color.Silver, Font = new Font("Segoe UI", 9f),
            AutoSize = false,
        };
        Add(_lblSysInfo); y += 50;

        // HZ + snow line (2 lines, green)
        _lblSysZones = new Label
        {
            Text = "—", Left = 14, Top = y, Width = 252, Height = 46,
            ForeColor = Color.FromArgb(90, 210, 100), Font = new Font("Segoe UI", 9f),
            AutoSize = false,
        };
        Add(_lblSysZones); y += 50;

        // Summary (planets, giants, belt…)
        _lblSysBodies = new Label
        {
            Text = "—", Left = 14, Top = y, Width = 252, Height = 22,
            ForeColor = Color.FromArgb(180, 180, 180), Font = new Font("Segoe UI", 9f),
            AutoSize = false,
        };
        Add(_lblSysBodies); y += 30;

        Divider(side, y); y += 14;

        // "Bodies" section header
        Add(new Label
        {
            Text = "BODIES", Left = 14, Top = y, Width = 252, Height = 18,
            ForeColor = Color.FromArgb(130, 130, 130), Font = new Font("Segoe UI", 8f),
            AutoSize = false,
        });
        y += 22;

        // Clickable body list
        _sysBodyList = new ListBox
        {
            Left = 14, Top = y, Width = 252, Height = 155,
            BackColor   = Color.FromArgb(22, 22, 22),
            ForeColor   = Color.Silver,
            DrawMode    = DrawMode.OwnerDrawFixed,
            ItemHeight  = 22,
            BorderStyle = BorderStyle.FixedSingle,
            Font        = new Font("Segoe UI", 9f),
        };
        _sysBodyList.DrawItem             += OnSysBodyListDrawItem;
        _sysBodyList.SelectedIndexChanged += OnSysBodyListSelectionChanged;
        Add(_sysBodyList); y += _sysBodyList.Height + 10;

        Divider(side, y); y += 14;

        // Selected body stats
        Add(new Label
        {
            Text = "SELECTED BODY", Left = 14, Top = y, Width = 252, Height = 18,
            ForeColor = Color.FromArgb(130, 130, 130), Font = new Font("Segoe UI", 8f),
            AutoSize = false,
        });
        y += 22;

        _lblBodyStats = new Label
        {
            Text      = "Click a body above to\ninspect it.",
            Left      = 14, Top = y, Width = 252, Height = 300,
            ForeColor = Color.Silver, Font = new Font("Consolas", 8.5f),
            AutoSize  = false,
        };
        Add(_lblBodyStats);

        // Back button anchored to bottom
        var btnBack = new Button
        {
            Text      = "◄ Back to Stars",
            Left      = 14, Width = 252, Height = 32,
            BackColor = Color.FromArgb(40, 60, 90), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        btnBack.Click += (_, _) =>
        {
            var starsTab = _tabs.TabPages.OfType<TabPage>().FirstOrDefault(t => t.Text == "Stars");
            if (starsTab != null) _tabs.SelectedTab = starsTab;
        };
        side.SizeChanged += (_, _) => btnBack.Top = side.Height - btnBack.Height - 14;
        Add(btnBack);

        return tab;
    }

    private void OnSysBodyListDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _sysBodyList.Items.Count) return;
        var body     = (StarSystemBody)_sysBodyList.Items[e.Index];
        bool sel     = (e.State & DrawItemState.Selected) != 0;
        bool hab     = body.IsHabitable;

        var bgColor  = sel ? Color.FromArgb(45, 75, 125) : Color.FromArgb(22, 22, 22);
        e.Graphics.FillRectangle(new SolidBrush(bgColor), e.Bounds);

        // Colour dot
        var dotCol = OrreryBodyColor(body.Type);
        e.Graphics.FillEllipse(new SolidBrush(dotCol),
            e.Bounds.X + 5, e.Bounds.Y + 7, 9, 9);

        // Text: name suffix + type + orbit
        string suffix = body.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? body.Name;
        string type   = body.Type == BodyType.AsteroidBelt ? "Asteroid Belt" : body.Type.ToString();
        string orb    = $"{body.OrbitAu:F2} AU";
        string text   = $"{suffix}  {type}  ({orb})";
        if (hab) text += "  ★";

        var fgColor = hab ? Color.FromArgb(105, 220, 105) : Color.FromArgb(210, 210, 210);
        using var fnt = new Font("Segoe UI", 9f);
        e.Graphics.DrawString(text, fnt, new SolidBrush(fgColor), e.Bounds.X + 20, e.Bounds.Y + 3);
    }

    private void OnSysBodyListSelectionChanged(object? sender, EventArgs e)
    {
        if (_sysBodyListUpdating) return;
        if (_sysBodyList.SelectedIndex < 0) return;

        var body = (StarSystemBody)_sysBodyList.Items[_sysBodyList.SelectedIndex];
        _selectedBody = body;
        _selectedBodyMoons = body.Type != BodyType.AsteroidBelt
            ? _moonGenerator.Generate(body, body.Name)
            : null;
        UpdateSystemSidebar();
        _systemPanel.Invalidate();
    }

    // ── system tab paint ──────────────────────────────────────────────────────

    private void OnSystemPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _systemPanel.Width, h = _systemPanel.Height;

        if (_selectedSystem == null)
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var b = new SolidBrush(Color.FromArgb(45, 45, 45));
            g.DrawString("Click a star in the Stars view to inspect its system",
                new Font("Segoe UI", 13f), b,
                new RectangleF(0, 0, w, h), fmt);
            return;
        }

        var sys = _selectedSystem;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        const float OrrH   = 195f;
        bool   hasMoons     = _selectedBody != null && (_selectedBodyMoons?.Length ?? 0) > 0;
        float  moonH        = hasMoons ? 110f : 0f;
        float  detailTop    = OrrH + moonH;
        float  detailH      = h - detailTop;

        DrawSystemOrrery(g, sys, 0, 0, w, OrrH);

        if (hasMoons)
            DrawMoonZone(g, _selectedBody!, _selectedBodyMoons!, 0, OrrH, w, moonH);

        if (_selectedBody != null)
            DrawBodyDetail(g, _selectedBody, 0, detailTop, w, detailH);
        else
        {
            using var fnt = new Font("Segoe UI", 9.5f);
            using var brs = new SolidBrush(Color.FromArgb(60, 60, 60));
            var fmt = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("Click a body in the orrery or the list to inspect it", fnt, brs,
                new RectangleF(0, detailTop + 10f, w, 30f), fmt);
        }

        // Moon hover popup
        if (_sysHoveredMoon is { } hm)
            DrawMoonTooltip(g, hm, _systemPanel.PointToClient(Cursor.Position), w);

        // Companion star hover popup
        if (_sysHoveredCompanion is { } hc)
            DrawCompanionTooltip(g, hc, _systemPanel.PointToClient(Cursor.Position), w);
    }

    private static void DrawMoonTooltip(Graphics g, Moon moon, Point cursor, int panelW)
    {
        string line1 = moon.Name;
        string line2 = $"{moon.Type}  ·  r {moon.RadiusKm:F0} km";
        string line3 = $"Orbit: {moon.OrbitParentRadii:F1} R♁";

        using var fnt1 = new Font("Consolas", 9f, FontStyle.Bold);
        using var fnt2 = new Font("Segoe UI",  8.5f);
        float w1 = g.MeasureString(line1, fnt1).Width;
        float w2 = Math.Max(g.MeasureString(line2, fnt2).Width,
                             g.MeasureString(line3, fnt2).Width);
        float boxW = Math.Max(w1, w2) + 18f;
        float boxH = fnt1.Height + fnt2.Height * 2f + 18f;

        float tx = cursor.X + 14f;
        float ty = cursor.Y - boxH - 8f;
        if (tx + boxW > panelW - 4) tx = cursor.X - boxW - 14f;
        if (ty < 2) ty = cursor.Y + 18f;

        using var bg     = new SolidBrush(Color.FromArgb(220, 10, 12, 26));
        using var border = new Pen(Color.FromArgb(140, 150, 180, 220));
        g.FillRectangle(bg,     tx, ty, boxW, boxH);
        g.DrawRectangle(border, tx, ty, boxW, boxH);

        using var h1Brs = new SolidBrush(Color.White);
        using var h2Brs = new SolidBrush(Color.FromArgb(180, 200, 220));
        g.DrawString(line1, fnt1, h1Brs, tx + 9f, ty + 6f);
        g.DrawString(line2, fnt2, h2Brs, tx + 9f, ty + 6f + fnt1.Height + 2f);
        g.DrawString(line3, fnt2, h2Brs, tx + 9f, ty + 6f + fnt1.Height + 2f + fnt2.Height + 2f);
    }

    private static void DrawCompanionTooltip(
        Graphics g, CompanionStar comp, Point cursor, int panelW)
    {
        string line1 = comp.Designation;
        string line2 = $"{comp.Class}-type star  ·  {comp.MassSolar:F2} M☉";
        string line3 = $"Wide-binary separation: ~{comp.OrbitAu:F0} AU";

        using var fnt1 = new Font("Consolas",  9f, FontStyle.Bold);
        using var fnt2 = new Font("Segoe UI",  8.5f);
        float w1 = g.MeasureString(line1, fnt1).Width;
        float w2 = Math.Max(g.MeasureString(line2, fnt2).Width,
                             g.MeasureString(line3, fnt2).Width);
        float boxW = Math.Max(w1, w2) + 18f;
        float boxH = fnt1.Height + fnt2.Height * 2f + 18f;

        float tx = cursor.X + 14f;
        float ty = cursor.Y - boxH - 8f;
        if (tx + boxW > panelW - 4) tx = cursor.X - boxW - 14f;
        if (ty < 2) ty = cursor.Y + 18f;

        using var bg     = new SolidBrush(Color.FromArgb(220, 10, 12, 26));
        using var border = new Pen(Color.FromArgb(140, 200, 170, 100));   // gold tint for stars
        g.FillRectangle(bg,     tx, ty, boxW, boxH);
        g.DrawRectangle(border, tx, ty, boxW, boxH);

        using var h1Brs = new SolidBrush(Color.White);
        using var h2Brs = new SolidBrush(Color.FromArgb(200, 210, 190, 140));
        g.DrawString(line1, fnt1, h1Brs, tx + 9f, ty + 6f);
        g.DrawString(line2, fnt2, h2Brs, tx + 9f, ty + 6f + fnt1.Height + 2f);
        g.DrawString(line3, fnt2, h2Brs, tx + 9f, ty + 6f + fnt1.Height + 2f + fnt2.Height + 2f);
    }

    private void OnSystemMouseMove(object? sender, MouseEventArgs e)
    {
        // Check body hits first (orrery zone)
        StarSystemBody? bodyHit = null;
        foreach (var (rect, body) in _sysBodyHitRects)
            if (rect.Contains(e.Location)) { bodyHit = body; break; }

        // Check companion star hits (orrery zone, right margin)
        CompanionStar? compHit = null;
        foreach (var (rect, comp) in _sysCompanionHitRects)
            if (rect.Contains(e.Location)) { compHit = comp; break; }

        // Check moon hits (moon zone below orrery)
        Moon? moonHit = null;
        foreach (var (rect, moon) in _sysMoonHitRects)
            if (rect.Contains(e.Location)) { moonHit = moon; break; }

        bool changed = bodyHit != _sysHoveredBody
                    || moonHit != _sysHoveredMoon
                    || compHit != _sysHoveredCompanion;
        _sysHoveredBody      = bodyHit;
        _sysHoveredMoon      = moonHit;
        _sysHoveredCompanion = compHit;

        if (changed)
        {
            _systemPanel.Cursor = (bodyHit != null || moonHit != null || compHit != null)
                                  ? Cursors.Hand : Cursors.Default;
            _systemPanel.Invalidate();
        }
    }

    private void OnSystemMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // Body click in orrery
        foreach (var (rect, body) in _sysBodyHitRects)
        {
            if (!rect.Contains(e.Location)) continue;
            SelectSystemBody(body);
            return;
        }
    }

    private void SelectSystemBody(StarSystemBody body)
    {
        _selectedBody      = body;
        _selectedBodyMoons = body.Type != BodyType.AsteroidBelt
            ? _moonGenerator.Generate(body, body.Name) : null;

        // Sync list selection without triggering the handler again
        _sysBodyListUpdating = true;
        int idx = _selectedSystem != null
            ? Array.IndexOf(_selectedSystem.Bodies, body) : -1;
        _sysBodyList.SelectedIndex = idx >= 0 ? idx : -1;
        _sysBodyListUpdating = false;

        UpdateSystemSidebar();
        _systemPanel.Invalidate();
    }

    // ── system tab rendering ──────────────────────────────────────────────────

    /// <summary>
    /// Full interactive orrery strip (larger version of the tooltip mini-orrery).
    /// Populates <see cref="_sysBodyHitRects"/> for hit-testing.
    /// </summary>
    private void DrawSystemOrrery(
        Graphics g, StarSystem sys, float ox, float oy, float ow, float oh)
    {
        _sysBodyHitRects.Clear();

        // ── log scale extent ──────────────────────────────────────────────────
        // Layout math is in OrreryLayout — this method is GDI+ rendering only.
        var layout = new OrreryLayout(sys);

        const float StarX     = 18f;
        const float ScaleLeft = 34f;
        const float Pad       = 8f;

        // Convert a normalised [0,1] layout position to a pixel X coordinate.
        float NX(float norm) => ox + ScaleLeft + norm * (ow - ScaleLeft - Pad);

        float cy = oy + oh * 0.48f;

        // ── background ────────────────────────────────────────────────────────
        using var bg = new SolidBrush(Color.FromArgb(12, 255, 255, 255));
        g.FillRectangle(bg, ox, oy, ow, oh);

        // Separator line
        using var sep = new Pen(Color.FromArgb(35, 180, 180, 180));
        g.DrawLine(sep, ox, oy + oh - 1, ox + ow, oy + oh - 1);

        // ── AU tick marks ──────────────────────────────────────────────────────
        using var tickFnt = new Font("Segoe UI", 8f);
        using var tickBrs = new SolidBrush(Color.FromArgb(80, 175, 175, 175));
        using var tickPen = new Pen(Color.FromArgb(40, 160, 160, 160));

        foreach (var (normX, lbl) in layout.Ticks)
        {
            float tx = NX(normX);
            g.DrawLine(tickPen, tx, oy + oh - 18f, tx, oy + oh - 4f);
            g.DrawString(lbl, tickFnt, tickBrs, tx - 10f, oy + oh - 18f);
        }

        // ── HZ band ───────────────────────────────────────────────────────────
        if (!float.IsNaN(layout.HzInnerNorm))
        {
            float x1 = NX(layout.HzInnerNorm), x2 = NX(layout.HzOuterNorm);
            using var hzFill = new SolidBrush(Color.FromArgb(20, 40, 185, 40));
            using var hzEdge = new Pen(Color.FromArgb(28, 55, 185, 55), 0.8f);
            g.FillRectangle(hzFill, x1, oy + 4, x2 - x1, oh - 24);
            g.DrawLine(hzEdge, x1, oy + 4, x1, oy + oh - 22);
            g.DrawLine(hzEdge, x2, oy + 4, x2, oy + oh - 22);
            using var hzLblBrs = new SolidBrush(Color.FromArgb(55, 100, 210, 100));
            using var hzLblFnt = new Font("Segoe UI", 8f);
            g.DrawString("HZ", hzLblFnt, hzLblBrs, (x1 + x2) / 2f - 8f, oy + 5f);
        }

        // ── snow line ─────────────────────────────────────────────────────────
        if (!float.IsNaN(layout.SnowLineNorm))
        {
            float sx = NX(layout.SnowLineNorm);
            using var snowPen = new Pen(Color.FromArgb(45, 120, 195, 255), 0.9f);
            snowPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawLine(snowPen, sx, oy + 4, sx, oy + oh - 22);
            using var snowLblBrs = new SolidBrush(Color.FromArgb(65, 130, 195, 255));
            using var snowLblFnt = new Font("Segoe UI", 8.5f);
            g.DrawString("❄", snowLblFnt, snowLblBrs, sx - 6f, oy + 4f);
        }

        // ── orbit axis ────────────────────────────────────────────────────────
        using var axisPen = new Pen(Color.FromArgb(35, 180, 180, 180), 0.6f);
        g.DrawLine(axisPen, ox + ScaleLeft, cy, ox + ow - Pad, cy);

        // ── host star ─────────────────────────────────────────────────────────
        float starR = 11f;
        var  (sr, sg, sb2) = layout.StarColor;
        var   sc    = Color.FromArgb(sr, sg, sb2);
        using var starGlow  = new SolidBrush(Color.FromArgb(35, sc.R, sc.G, sc.B));
        using var starBrush = new SolidBrush(sc);
        g.FillEllipse(starGlow,  ox + StarX - starR * 2.0f, cy - starR * 2.0f, starR * 4f, starR * 4f);
        g.FillEllipse(starBrush, ox + StarX - starR,         cy - starR,         starR * 2f, starR * 2f);

        using var starLblFnt = new Font("Segoe UI", 8f);
        using var starLblBrs = new SolidBrush(Color.FromArgb(160, 210, 210, 210));
        g.DrawString(sys.StarClass.ToString()[..1], starLblFnt, starLblBrs, ox + StarX - 4f, cy + starR + 3f);

        // ── companion star(s) ─────────────────────────────────────────────────
        // Companions orbit at wide separations (30–800 AU) — far beyond the planet
        // zone — so they are drawn as a star-like disc at the right margin with a
        // "~N AU →" note, rather than at their true log-scale position which would
        // compress all the planets into a tiny sliver.  They are fully hoverable.
        _sysCompanionHitRects.Clear();
        foreach (var ci in layout.Companions)
        {
            var comp  = ci.Companion;
            var (ccr, ccg, ccb) = ci.Color;
            var cc    = Color.FromArgb(ccr, ccg, ccb);
            float cR  = 6f;
            // Stack multiple companions from right: B at ow-18, C at ow-46 …
            float cx2 = ox + ow - Pad - cR - ci.CompanionIndex * (cR * 2f + 14f);

            // Dashed separator from last planet area to companion
            using var gapPen = new Pen(Color.FromArgb(22, 170, 170, 170), 0.7f);
            gapPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawLine(gapPen, ox + ow - 52f - ci.CompanionIndex * (cR * 2f + 14f), cy, cx2 - cR, cy);

            // Wide-binary distance note (above the dashed line)
            using var noteFnt = new Font("Segoe UI", 7.5f);
            using var noteBrs = new SolidBrush(Color.FromArgb(70, 175, 175, 175));
            string orbitNote = comp.OrbitAu >= 100f
                ? $"~{(int)comp.OrbitAu} AU →"
                : $"~{comp.OrbitAu:F0} AU →";
            g.DrawString(orbitNote, noteFnt, noteBrs, cx2 - 42f, cy - cR - 26f);

            bool isHovered = comp == _sysHoveredCompanion;

            // Hover ring
            if (isHovered)
            {
                using var hovPen = new Pen(Color.FromArgb(140, 220, 220, 220), 1.2f);
                g.DrawEllipse(hovPen, cx2 - cR - 4f, cy - cR - 4f, (cR + 4f) * 2f, (cR + 4f) * 2f);
            }

            // Glow
            using var compGlow = new SolidBrush(Color.FromArgb(isHovered ? 50 : 30, cc.R, cc.G, cc.B));
            g.FillEllipse(compGlow, cx2 - cR * 2.2f, cy - cR * 2.2f, cR * 4.4f, cR * 4.4f);

            // Disc
            using var compBrush = new SolidBrush(Color.FromArgb(195, cc.R, cc.G, cc.B));
            g.FillEllipse(compBrush, cx2 - cR, cy - cR, cR * 2f, cR * 2f);

            // Designation label above ("B" or "C")
            string compLabel = ci.Label;
            using var compLblFnt = new Font("Segoe UI", 9f, isHovered ? FontStyle.Bold : FontStyle.Regular);
            using var compLblBrs = new SolidBrush(Color.FromArgb(isHovered ? 210 : 155, 210, 210, 210));
            g.DrawString(compLabel, compLblFnt, compLblBrs, cx2 - 5f, cy - cR - 17f);

            // Spectral class below disc
            using var classFnt = new Font("Segoe UI", 7.5f);
            using var classBrs = new SolidBrush(Color.FromArgb(110, 185, 185, 185));
            g.DrawString(comp.Class.ToString()[..1], classFnt, classBrs, cx2 - 4f, cy + cR + 3f);

            // Hit rect
            float hitR = Math.Max(cR + 6f, 10f);
            _sysCompanionHitRects.Add((new RectangleF(cx2 - hitR, cy - hitR, hitR * 2f, hitR * 2f), comp));
        }

        // ── orbital bodies ────────────────────────────────────────────────────
        foreach (var item in layout.Bodies)
        {
            var   body = item.Body;
            float bx   = NX(item.NormX);
            if (bx < ox + ScaleLeft || bx > ox + ow) continue;

            if (item.IsBelt)
            {
                using var beltPen = new Pen(Color.FromArgb(80, 145, 125, 85), 1.5f);
                beltPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                g.DrawLine(beltPen, bx, oy + 8f, bx, oy + oh - 22f);
                _sysBodyHitRects.Add((new RectangleF(bx - 6f, oy + 4f, 12f, oh - 26f), body));
                using var beltLblBrs = new SolidBrush(Color.FromArgb(90, 170, 160, 120));
                using var beltLblFnt = new Font("Segoe UI", 8.5f);
                g.DrawString("belt", beltLblFnt, beltLblBrs, bx - 10f, oy + 8f);
                continue;
            }

            float br  = item.Radius * 1.6f;   // full orrery uses 1.6× scale
            var (br2, bg2, bb2) = item.Color;
            var col = Color.FromArgb(br2, bg2, bb2);

            bool isSelected = body == _selectedBody;
            bool isHovered  = body == _sysHoveredBody && body != _selectedBody;

            if (isSelected)
            {
                using var selGlow = new SolidBrush(Color.FromArgb(55, 255, 240, 130));
                g.FillEllipse(selGlow, bx - br * 2.5f, cy - br * 2.5f, br * 5f, br * 5f);
                using var selRing = new Pen(Color.FromArgb(200, 255, 230, 80), 1.8f);
                g.DrawEllipse(selRing, bx - br - 4f, cy - br - 4f, (br + 4f) * 2, (br + 4f) * 2);
            }
            else if (isHovered)
            {
                using var hovRing = new Pen(Color.FromArgb(130, 200, 200, 200), 1.3f);
                g.DrawEllipse(hovRing, bx - br - 3f, cy - br - 3f, (br + 3f) * 2, (br + 3f) * 2);
            }

            if (body.HasRings)
            {
                using var rp = new Pen(Color.FromArgb(130, col.R, col.G, col.B), 1.2f);
                g.DrawEllipse(rp, bx - br * 2.5f, cy - br * 0.4f, br * 5f, br * 0.8f);
            }

            using var bb = new SolidBrush(col);
            g.FillEllipse(bb, bx - br, cy - br, br * 2f, br * 2f);

            if (body.InHabitableZone)
            {
                using var hzDot = new SolidBrush(Color.FromArgb(195, 55, 215, 55));
                g.FillEllipse(hzDot, bx - 3f, cy + br + 3f, 6f, 6f);
            }

            using var bodyLblFnt = new Font("Segoe UI", 9f);
            using var bodyLblBrs = new SolidBrush(
                isSelected ? Color.FromArgb(235, 255, 242, 100)
                           : Color.FromArgb(140, 200, 200, 200));
            g.DrawString(item.Label, bodyLblFnt, bodyLblBrs, bx - 5f, cy - br - 17f);

            float hitR = Math.Max(br + 6f, 10f);
            _sysBodyHitRects.Add((new RectangleF(bx - hitR, cy - hitR, hitR * 2f, hitR * 2f), body));
        }

        // ── system title in top-left ──────────────────────────────────────────
        using var titFnt = new Font("Segoe UI", 9f);
        using var titBrs = new SolidBrush(Color.FromArgb(130, 195, 195, 195));
        g.DrawString($"{sys.Designation}  ·  {sys.PlanetCount} bodies", titFnt, titBrs, ox + 4f, oy + 4f);
    }

    /// <summary>
    /// Draws the moon system of <paramref name="planet"/> as a horizontal log-scale strip.
    /// Only the first <see cref="MaxDisplayedMoons"/> moons (the significant named ones)
    /// are drawn; any remainder are summarised as "+N uncatalogued" at the right edge.
    /// Also populates <see cref="_sysMoonHitRects"/> for hover hit-testing.
    /// </summary>
    private void DrawMoonZone(
        Graphics g, StarSystemBody planet, Moon[] moons,
        float ox, float oy, float ow, float oh)
    {
        _sysMoonHitRects.Clear();

        // Layout math lives in MoonLayout — this method is GDI+ rendering only.
        var moonLayout = new MoonLayout(moons);
        int hidden     = moonLayout.HiddenCount;

        // Background + separator
        using var bg  = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
        using var sep = new Pen(Color.FromArgb(35, 180, 180, 180));
        g.FillRectangle(bg, ox, oy, ow, oh);
        g.DrawLine(sep, ox, oy + oh - 1, ox + ow, oy + oh - 1);

        // Title — total count, not just displayed
        using var titFnt = new Font("Segoe UI", 9f);
        using var titBrs = new SolidBrush(Color.FromArgb(130, 180, 180, 180));
        int total = moons.Length;
        g.DrawString($"Moons of {planet.Name}  ({total} satellite{(total == 1 ? "" : "s")})",
            titFnt, titBrs, ox + 10f, oy + 5f);

        if (total == 0 || moonLayout.Items.Count == 0) return;

        float cy = oy + oh * 0.55f;

        // Planet icon
        const float PlanetX = 42f;
        float pR  = Math.Min(SystemVisuals.BodyRadius(planet.Type) * 2.2f, 18f);
        var (pr2, pg2, pb2) = SystemVisuals.BodyRgb(planet.Type);
        var   col = Color.FromArgb(pr2, pg2, pb2);
        using var pb = new SolidBrush(col);
        g.FillEllipse(pb, ox + PlanetX - pR, cy - pR, pR * 2, pR * 2);
        if (planet.HasRings)
        {
            using var rp = new Pen(Color.FromArgb(120, col.R, col.G, col.B), 1.2f);
            g.DrawEllipse(rp, ox + PlanetX - pR * 2.3f, cy - pR * 0.35f, pR * 4.6f, pR * 0.7f);
        }

        // Scale: normalised [0,1] positions map to the strip between planet edge and right margin
        float reserveRight = hidden > 0 ? 90f : 12f;
        float scaleW       = ow - PlanetX - pR - reserveRight;
        float MX(float norm) => ox + PlanetX + pR + norm * scaleW;

        // Axis line
        using var axPen = new Pen(Color.FromArgb(28, 160, 160, 160), 0.5f);
        g.DrawLine(axPen, ox + PlanetX + pR, cy, ox + PlanetX + pR + scaleW, cy);

        using var mnFnt  = new Font("Segoe UI", 8.5f);
        using var radFnt = new Font("Segoe UI", 8f);

        foreach (var item in moonLayout.Items)
        {
            float mx = MX(item.NormX);
            float mr = item.Radius;

            bool isHovered = item.Moon == _sysHoveredMoon;
            var (mr2, mg2, mb2) = item.Color;
            var mc = Color.FromArgb(mr2, mg2, mb2);

            if (isHovered)
            {
                using var hov = new Pen(Color.FromArgb(160, 200, 200, 200), 1.2f);
                g.DrawEllipse(hov, mx - mr - 3f, cy - mr - 3f, (mr + 3f) * 2, (mr + 3f) * 2);
            }

            using var mb = new SolidBrush(mc);
            g.FillEllipse(mb, mx - mr, cy - mr, mr * 2, mr * 2);

            using var mnBrs = new SolidBrush(Color.FromArgb(isHovered ? 210 : 150, 185, 185, 185));
            g.DrawString(item.Label, mnFnt, mnBrs, mx - 6f, cy + mr + 3f);

            using var radBrs = new SolidBrush(Color.FromArgb(isHovered ? 180 : 100, 170, 170, 170));
            g.DrawString(item.RadiusLabel, radFnt, radBrs, mx - 12f, cy - mr - 16f);

            float hitR = Math.Max(mr + 5f, 10f);
            _sysMoonHitRects.Add((new RectangleF(mx - hitR, cy - hitR, hitR * 2f, hitR * 2f), item.Moon));
        }

        // "+N more" overflow note
        if (hidden > 0)
        {
            float nx = ox + ow - reserveRight + 4f;
            using var moreFnt = new Font("Segoe UI", 8.5f);
            using var moreBrs = new SolidBrush(Color.FromArgb(110, 180, 180, 180));
            g.DrawString($"+{hidden}\nmore", moreFnt, moreBrs, nx, cy - moreFnt.Height);
        }
    }

    /// <summary>
    /// Draws detailed stats for the selected body: large icon on the left, stat grid on the right.
    /// </summary>
    private static void DrawBodyDetail(
        Graphics g, StarSystemBody body, float ox, float oy, float ow, float oh)
    {
        if (oh < 20f) return;

        using var bg = new SolidBrush(Color.FromArgb(9, 255, 255, 255));
        g.FillRectangle(bg, ox, oy, ow, oh);

        var col = OrreryBodyColor(body.Type);

        // ── planet icon ───────────────────────────────────────────────────────
        float iconR = Math.Min(oh * 0.28f, 44f);
        float iconX = ox + 20f + iconR;
        float iconY = oy + oh * 0.42f;

        using var glow = new SolidBrush(Color.FromArgb(28, col.R, col.G, col.B));
        g.FillEllipse(glow, iconX - iconR * 2f, iconY - iconR * 2f, iconR * 4f, iconR * 4f);
        using var fill = new SolidBrush(col);
        g.FillEllipse(fill, iconX - iconR, iconY - iconR, iconR * 2f, iconR * 2f);

        if (body.HasRings)
        {
            using var rp = new Pen(Color.FromArgb(160, col.R, col.G, col.B), 2.5f);
            g.DrawEllipse(rp,
                iconX - iconR * 2.4f, iconY - iconR * 0.4f,
                iconR * 4.8f, iconR * 0.8f);
        }

        // HZ badge
        if (body.IsHabitable)
        {
            using var hzBadge = new SolidBrush(Color.FromArgb(185, 50, 215, 50));
            g.FillEllipse(hzBadge, iconX - 6f, iconY + iconR + 5f, 12f, 12f);
            using var hzFnt = new Font("Segoe UI", 6.5f);
            using var hzBrs = new SolidBrush(Color.White);
            g.DrawString("✓", hzFnt, hzBrs, iconX - 5f, iconY + iconR + 5f);
        }

        // ── stat grid ─────────────────────────────────────────────────────────
        float gx = ox + iconX + iconR + 20f;
        float gy = oy + 12f;

        using var nameFnt = new Font("Consolas", 11f, FontStyle.Bold);
        using var typeFnt = new Font("Segoe UI",  9.5f);
        using var keyFnt  = new Font("Consolas",  9f);

        using var nameBrs = new SolidBrush(Color.White);
        using var typeBrs = new SolidBrush(Color.FromArgb(150, 210, 150));
        using var keyBrs  = new SolidBrush(Color.FromArgb(130, 160, 185));
        using var valBrs  = new SolidBrush(Color.FromArgb(230, 230, 230));
        using var habBrs  = new SolidBrush(Color.FromArgb(100, 220, 100));

        float lh = keyFnt.Height + 3f;

        g.DrawString(body.Name, nameFnt, nameBrs, gx, gy); gy += nameFnt.Height + 3f;
        g.DrawString(BodyStats.BodyTypeLabel(body.Type), typeFnt, typeBrs, gx, gy);
        gy += typeFnt.Height + 8f;

        const float keyW = 100f;
        foreach (var (key, val) in BodyStats.GetStatRows(body))
        {
            // "Hab. zone" gets colour-coded; all others use the default value brush.
            using var vBrs = new SolidBrush(
                key == "Hab. zone"
                    ? (body.InHabitableZone ? Color.FromArgb(100, 215, 100) : Color.FromArgb(140, 140, 140))
                    : Color.FromArgb(230, 230, 230));
            g.DrawString(key, keyFnt, keyBrs, gx,          gy);
            g.DrawString(val, keyFnt, vBrs,   gx + keyW,   gy);
            gy += lh;
        }

        if (body.IsHabitable)
        {
            gy += 3f;
            g.DrawString("★ Potentially habitable", typeFnt, habBrs, gx, gy);
        }
    }

    // ── system sidebar update ─────────────────────────────────────────────────

    private void UpdateSystemSidebar()
    {
        var sys = _selectedSystem;
        if (sys == null)
        {
            _lblSysName.Text   = "—";
            _lblSysInfo.Text   = "—";
            _lblSysZones.Text  = "—";
            _lblSysBodies.Text = "—";
            _lblBodyStats.Text = "Click a body above\nto inspect it.";
            _sysBodyList.Items.Clear();
            return;
        }

        _lblSysName.Text = sys.Designation;

        string cls     = SystemVisuals.SpectralLabel(sys.StarClass);
        string lumStr  = BodyStats.FormatLuminosity(sys.LuminositySol);
        string compStr = sys.Companions.Length switch { 0 => "", 1 => "  ·  binary", _ => "  ·  trinary" };

        _lblSysInfo.Text  = $"{cls}\nLuminosity: {lumStr}{compStr}";
        _lblSysZones.Text = sys.HzInnerAu > 0f
            ? $"HZ: {sys.HzInnerAu:F2}–{sys.HzOuterAu:F2} AU\nSnow line: {sys.SnowLineAu:F2} AU"
            : "No habitable zone";
        _lblSysBodies.Text = BuildSystemSummaryLine(sys)
            + (sys.HasOortCloud ? "  ·  Oort cloud" : "");

        // Populate clickable body list (suppress selection event while doing so)
        _sysBodyListUpdating = true;
        _sysBodyList.Items.Clear();
        foreach (var body in sys.Bodies)
            _sysBodyList.Items.Add(body);
        // Re-select the currently chosen body if any
        if (_selectedBody != null)
        {
            int idx = Array.IndexOf(sys.Bodies, _selectedBody);
            _sysBodyList.SelectedIndex = idx >= 0 ? idx : -1;
        }
        _sysBodyListUpdating = false;

        // Selected body stats for the label
        var b = _selectedBody;
        if (b == null)
        {
            _lblBodyStats.Text = "Click a body above\nto inspect it.";
            return;
        }

        _lblBodyStats.Text = BodyStats.BuildBodySidebarText(b);
    }

    // ── stub tabs ─────────────────────────────────────────────────────────────

    private TabPage BuildStubTab(string title, string description)
    {
        var tab = new TabPage(title)
        {
            BackColor = Color.FromArgb(28, 28, 28),
            Padding   = new Padding(0),
        };

        var sidePanel = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 270,
            BackColor = Color.FromArgb(38, 38, 38),
            Padding   = new Padding(12),
        };

        int y = 12;
        sidePanel.Controls.Add(new Label
        {
            Text = "Seed chain:", Left = 12, Top = y, Width = 246, Height = 20,
            ForeColor = Color.Silver, AutoSize = false,
        });
        y += 24;
        sidePanel.Controls.Add(new TextBox
        {
            Left = 12, Top = y, Width = 246,
            Text      = ChainText(),
            ReadOnly  = true,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.White,
            Font      = new Font("Consolas", 8f),
        });
        y += 32;
        Divider(sidePanel, y); y += 16;
        sidePanel.Controls.Add(new Label
        {
            Text      = $"{title} controls will\nappear here.",
            Left = 12, Top = y, Width = 246, Height = 40,
            ForeColor = Color.Silver, AutoSize = false,
        });

        var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 10, 10) };
        previewPanel.Controls.Add(new Label
        {
            Text      = description,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(50, 50, 50),
            Font      = new Font("Segoe UI", 14f),
            AutoSize  = false,
        });

        tab.Controls.Add(previewPanel);
        tab.Controls.Add(sidePanel);
        return tab;
    }

    // ── chain strip ───────────────────────────────────────────────────────────

    private string ChainText()
    {
        string g = _galaxyCode != null ? $"G:{_galaxyCode}" : "G:—";
        return $"{g}   ›   S:—   ›   P:—   ›   Su:—";
    }

    private void UpdateChain() => _chainLabel.Text = ChainText();

    // ── galaxy type controls ──────────────────────────────────────────────────

    private void UpdateControlsForType()
    {
        var  type     = (GalaxyType)_cbType.SelectedIndex;
        bool hasArms  = type == GalaxyType.Spiral;
        bool hasBar   = type == GalaxyType.Spiral;
        bool hasBulge = type != GalaxyType.Irregular;

        SetSliderEnabled(_tbArms,        _lblArms,        hasArms);
        SetSliderEnabled(_tbTightness,   _lblTightness,   hasArms);
        SetSliderEnabled(_tbArmWidth,    _lblArmWidth,    hasArms);
        SetSliderEnabled(_tbBulge,       _lblBulge,       hasBulge);
        SetSliderEnabled(_tbBarStrength, _lblBarStrength, hasBar);
        SetSliderEnabled(_tbBarLength,   _lblBarLength,   hasBar);
    }

    private static void SetSliderEnabled(TrackBar tb, Label lbl, bool enabled)
    {
        tb.Enabled    = enabled;
        lbl.ForeColor = enabled ? Color.Silver : Color.FromArgb(65, 65, 65);
    }

    private static Button MakeButton(string text, Color back, int x, int y, int w) =>
        new()
        {
            Text = text, Left = x, Top = y, Width = w, Height = 34,
            BackColor = back, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        };

    // ── galaxy params ─────────────────────────────────────────────────────────

    private GalaxyParams BuildParams()
    {
        ulong.TryParse(_txSeed.Text.Trim(), out ulong seed);
        return new GalaxyParams
        {
            Arms          = _tbArms.Value,
            ArmTightness  = _tbTightness.Value   / 100f,
            ArmWidth      = _tbArmWidth.Value     / 100f,
            BulgeFactor   = _tbBulge.Value        / 100f,
            BulgeStrength = 1.0f,
            Dissipation   = _tbDissipation.Value  / 100f,
            CoreDensity   = 0.90f,
            ArmDensity    = 0.75f,
            Age           = (GalaxyAge) _cbAge.SelectedIndex,
            Type          = (GalaxyType)_cbType.SelectedIndex,
            Seed          = seed,
            BarStrength     = _tbBarStrength.Value / 100f,
            BarLength       = _tbBarLength.Value   / 100f,
            FeatureDensity  = _tbFeatureDensity.Value,
        };
    }

    private void ApplyParams(GalaxyParams p)
    {
        _tbArms.Value        = Math.Clamp(p.Arms,                       _tbArms.Minimum,        _tbArms.Maximum);
        _tbTightness.Value   = Math.Clamp((int)(p.ArmTightness  * 100), _tbTightness.Minimum,   _tbTightness.Maximum);
        _tbArmWidth.Value    = Math.Clamp((int)(p.ArmWidth      * 100), _tbArmWidth.Minimum,    _tbArmWidth.Maximum);
        _tbBulge.Value       = Math.Clamp((int)(p.BulgeFactor   * 100), _tbBulge.Minimum,       _tbBulge.Maximum);
        _tbDissipation.Value = Math.Clamp((int)(p.Dissipation   * 100), _tbDissipation.Minimum, _tbDissipation.Maximum);
        _tbBarStrength.Value     = Math.Clamp((int)(p.BarStrength   * 100), _tbBarStrength.Minimum,     _tbBarStrength.Maximum);
        _tbBarLength.Value       = Math.Clamp((int)(p.BarLength     * 100), _tbBarLength.Minimum,       _tbBarLength.Maximum);
        _tbFeatureDensity.Value  = Math.Clamp(p.FeatureDensity,            _tbFeatureDensity.Minimum,  _tbFeatureDensity.Maximum);
        _cbType.SelectedIndex = (int)p.Type;
        _cbAge.SelectedIndex  = (int)p.Age;
        _txSeed.Text          = p.Seed.ToString();
    }

    // ── event handlers ────────────────────────────────────────────────────────

    private void Regenerate(GalaxyParams parms)
    {
        _galaxy = _generator.Generate(parms, 512);

        var sideGalaxy = _generator.GenerateSideView(parms, 512, 128);
        _sideViewBitmap?.Dispose();
        _sideViewBitmap = _renderer.Render(sideGalaxy);

        _galaxyBitmap?.Dispose();
        _galaxyBitmap = _renderer.Render(_galaxy, _galaxy.ClusterMap);

        _galaxyCode = GalaxyCode.Encode(parms);
        _txGalaxyCode.Text      = _galaxyCode;
        _txGalaxyCode.ForeColor = Color.White;

        ClearExploreCache();
        _zoomPath.Clear();
        UpdateExploreUI();

        // Reset star cache so the new galaxy is used when the Stars tab is next shown
        _starStream?.Dispose();
        _starStream = null;
        _starCache  = null;

        _btnExport.Enabled = true;
        _preview.Invalidate();
        _explorePanel.Invalidate();
        UpdateChain();
    }

    private void OnPreviewPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode   = PixelOffsetMode.Half;

        if (_galaxyBitmap != null)
            g.DrawImage(_galaxyBitmap, ZoomRect(_preview.ClientSize, _galaxyBitmap.Size));

        if (_sideViewBitmap != null)
        {
            const int margin = 10;
            int insetW = _preview.Width  / 3;
            int insetH = insetW          / 4;
            var inset  = new Rectangle(_preview.Width  - insetW - margin,
                                       _preview.Height - insetH - margin,
                                       insetW, insetH);
            g.DrawImage(_sideViewBitmap, inset);
            g.DrawRectangle(Pens.DimGray, inset);
        }
    }

    private static Rectangle ZoomRect(Size canvas, Size image)
    {
        float scale = Math.Min((float)canvas.Width / image.Width,
                               (float)canvas.Height / image.Height);
        int w = (int)(image.Width  * scale);
        int h = (int)(image.Height * scale);
        return new Rectangle((canvas.Width - w) / 2, (canvas.Height - h) / 2, w, h);
    }

    private void OnGenerate(object? sender, EventArgs e)
    {
        _btnGenerate.Enabled = false;
        _btnGenerate.Text    = "Generating…";
        Application.DoEvents();
        try   { Regenerate(BuildParams()); }
        catch (Exception ex) { Error(ex); }
        finally { _btnGenerate.Enabled = true; _btnGenerate.Text = "Generate"; }
    }

    private void OnExport(object? sender, EventArgs e)
    {
        if (_galaxy == null || _galaxyBitmap is not Bitmap bmp) return;
        using var dlg = new SaveFileDialog { Filter = "PNG Image|*.png", Title = "Export Galaxy Seed" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try   { PngSeed.Save(bmp, _galaxy.Params, dlg.FileName); }
        catch (Exception ex) { Error(ex); }
    }

    private void OnImport(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Filter = "PNG Image|*.png", Title = "Import Galaxy Seed" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var (img, parms) = PngSeed.Load(dlg.FileName);
            img.Dispose();
            ApplyParams(parms);
            Regenerate(parms);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void Error(Exception ex) =>
        MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => DoubleBuffered = true;
    }
}
