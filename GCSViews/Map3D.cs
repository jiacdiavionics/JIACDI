using MissionPlanner.Utilities;
using MissionPlanner.Controls;
using MissionPlanner.ArduPilot;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// 3D Map Window for terrain and drone visualization.
    /// This provides a 3D perspective of the terrain with drone position overlay.
    /// </summary>
    public class Map3D : Form, IActivate
    {
        private double cameraDistance = 50.0;
        private double cameraPitch = 0.5;
        private double cameraYaw = 0.0;
        private Point3D dronePosition = new Point3D(0, 0, 0);
        private float droneHeading = 0;
        private bool isDragging = false;
        private int lastMouseX, lastMouseY;
        private bool terrainGenerated = false;
        private const int GRID_SIZE = 50;
        private const float CELL_SIZE = 10.0f;
        private float[,] terrainHeights;
        private float verticalScale = 1.0f;

        private System.Windows.Forms.Timer refreshTimer;
        private Panel glPanel;
        private PictureBox renderBox;

        // UI Components
        private Panel controlPanel;
        private Label lblTitle;
        private Label lblLat;
        private Label lblLng;
        private Label lblAlt;
        private Label lblHeading;
        private Label lblStatus;
        private Button btnResetView;
        private TrackBar verticalScaleBar;
        private Label lblVerticalScale;

        private static Map3D _instance = null;
        private static readonly object _lock = new object();

        public static Map3D Instance
        {
            get
            {
                if (_instance == null || _instance.IsDisposed)
                {
                    lock (_lock)
                    {
                        if (_instance == null || _instance.IsDisposed)
                        {
                            _instance = new Map3D();
                        }
                    }
                }
                return _instance;
            }
        }

        private Map3D()
        {
            InitializeComponent();
            GenerateTerrain();
            PositionOnSecondaryMonitor();
            renderBox.Paint += RenderBox_Paint;
        }

        private void PositionOnSecondaryMonitor()
        {
            try
            {
                Screen[] screens = Screen.AllScreens;
                
                if (screens.Length > 1)
                {
                    Screen secondary = screens[1];
                    this.StartPosition = FormStartPosition.Manual;
                    this.Location = secondary.WorkingArea.Location;
                    this.WindowState = FormWindowState.Maximized;
                }
                else
                {
                    this.StartPosition = FormStartPosition.CenterScreen;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D PositionOnSecondaryMonitor Error: " + ex.Message);
                this.StartPosition = FormStartPosition.CenterScreen;
            }
        }

        private void InitializeComponent()
        {
            this.Text = "DIMP - 3D Map View";
            this.Size = new Size(1280, 800);
            this.MinimumSize = new Size(800, 600);
            this.BackColor = Color.FromArgb(26, 26, 46);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = false;

            glPanel = new Panel();
            renderBox = new PictureBox();
            controlPanel = new Panel();
            lblTitle = new Label();
            lblLat = new Label();
            lblLng = new Label();
            lblAlt = new Label();
            lblHeading = new Label();
            lblStatus = new Label();
            btnResetView = new Button();
            verticalScaleBar = new TrackBar();
            lblVerticalScale = new Label();
            refreshTimer = new System.Windows.Forms.Timer();

            // GL Panel (rendering surface)
            glPanel.Dock = DockStyle.Fill;
            glPanel.BackColor = Color.FromArgb(20, 20, 36);
            glPanel.Name = "glPanel";

            // Render Box
            renderBox.Dock = DockStyle.Fill;
            renderBox.BackColor = Color.FromArgb(20, 20, 36);
            renderBox.SizeMode = PictureBoxSizeMode.Normal;

            // Control Panel
            controlPanel.BackColor = Color.FromArgb(30, 30, 46);
            controlPanel.Dock = DockStyle.Bottom;
            controlPanel.Location = new Point(0, 680);
            controlPanel.Name = "controlPanel";
            controlPanel.Size = new Size(1272, 100);
            controlPanel.Padding = new Padding(10);

            // Title
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(0, 122, 204);
            lblTitle.Location = new Point(10, 5);
            lblTitle.Name = "lblTitle";
            lblTitle.Text = "3D Map View";

            // Status label
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Segoe UI", 10F);
            lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
            lblStatus.Location = new Point(10, 35);
            lblStatus.Name = "lblStatus";
            lblStatus.Text = "Disconnected";

            // Lat label
            lblLat.AutoSize = true;
            lblLat.Font = new Font("Segoe UI", 9F);
            lblLat.ForeColor = Color.FromArgb(0, 200, 100);
            lblLat.Location = new Point(10, 60);
            lblLat.Name = "lblLat";
            lblLat.Text = "Lat: --";

            // Lng label
            lblLng.AutoSize = true;
            lblLng.Font = new Font("Segoe UI", 9F);
            lblLng.ForeColor = Color.FromArgb(0, 200, 100);
            lblLng.Location = new Point(110, 60);
            lblLng.Name = "lblLng";
            lblLng.Text = "Lng: --";

            // Alt label
            lblAlt.AutoSize = true;
            lblAlt.Font = new Font("Segoe UI", 9F);
            lblAlt.ForeColor = Color.FromArgb(0, 200, 100);
            lblAlt.Location = new Point(220, 60);
            lblAlt.Name = "lblAlt";
            lblAlt.Text = "Alt: --";

            // Heading label
            lblHeading.AutoSize = true;
            lblHeading.Font = new Font("Segoe UI", 9F);
            lblHeading.ForeColor = Color.FromArgb(0, 200, 100);
            lblHeading.Location = new Point(320, 60);
            lblHeading.Name = "lblHeading";
            lblHeading.Text = "HDG: --";

            // Reset View button
            btnResetView.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnResetView.Location = new Point(1100, 10);
            btnResetView.Name = "btnResetView";
            btnResetView.Size = new Size(130, 30);
            btnResetView.TabIndex = 0;
            btnResetView.Text = "Reset View";
            btnResetView.UseVisualStyleBackColor = true;
            btnResetView.BackColor = Color.FromArgb(0, 122, 204);
            btnResetView.ForeColor = Color.White;
            btnResetView.FlatStyle = FlatStyle.Flat;
            btnResetView.Click += BtnResetView_Click;

            // Vertical Scale label
            lblVerticalScale.AutoSize = true;
            lblVerticalScale.Font = new Font("Segoe UI", 9F);
            lblVerticalScale.ForeColor = Color.FromArgb(220, 220, 230);
            lblVerticalScale.Location = new Point(1100, 50);
            lblVerticalScale.Name = "lblVerticalScale";
            lblVerticalScale.Text = "Vertical Scale: 1x";

            // Vertical Scale bar
            verticalScaleBar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            verticalScaleBar.Location = new Point(1100, 70);
            verticalScaleBar.Maximum = 50;
            verticalScaleBar.Minimum = 1;
            verticalScaleBar.Name = "verticalScaleBar";
            verticalScaleBar.Size = new Size(130, 45);
            verticalScaleBar.TabIndex = 1;
            verticalScaleBar.Value = 10;
            verticalScaleBar.Scroll += VerticalScaleBar_Scroll;

            // Add controls to control panel
            controlPanel.Controls.Add(lblVerticalScale);
            controlPanel.Controls.Add(verticalScaleBar);
            controlPanel.Controls.Add(btnResetView);
            controlPanel.Controls.Add(lblHeading);
            controlPanel.Controls.Add(lblAlt);
            controlPanel.Controls.Add(lblLng);
            controlPanel.Controls.Add(lblLat);
            controlPanel.Controls.Add(lblStatus);
            controlPanel.Controls.Add(lblTitle);

            // Add panels to form
            glPanel.Controls.Add(renderBox);
            this.Controls.Add(glPanel);
            this.Controls.Add(controlPanel);

            // Mouse events for rendering panel
            renderBox.MouseDown += GlPanel_MouseDown;
            renderBox.MouseUp += GlPanel_MouseUp;
            renderBox.MouseMove += GlPanel_MouseMove;
            renderBox.MouseWheel += GlPanel_MouseWheel;

            // Timer
            refreshTimer.Interval = 100;
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            // Form events
            this.FormClosing += Map3D_FormClosing;
            this.Resize += Map3D_Resize;

            this.SuspendLayout();
            this.ResumeLayout(false);
        }

        private void GenerateTerrain()
        {
            Random random = new Random(42);
            terrainHeights = new float[GRID_SIZE, GRID_SIZE];

            for (int x = 0; x < GRID_SIZE; x++)
            {
                for (int z = 0; z < GRID_SIZE; z++)
                {
                    // Generate varying terrain height
                    float height = (float)(Math.Sin(x * 0.2) * Math.Cos(z * 0.2) * 8 + 
                                            Math.Sin(x * 0.1 + z * 0.1) * 4 +
                                            random.NextDouble() * 2);
                    terrainHeights[x, z] = height;
                }
            }

            terrainGenerated = true;
        }

        private void RenderBox_Paint(object sender, PaintEventArgs e)
        {
            if (renderBox.Width <= 0 || renderBox.Height <= 0) return;

            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(20, 30, 50));

            int centerX = renderBox.Width / 2;
            int centerY = renderBox.Height / 2;

            // Draw terrain grid
            DrawTerrain(g, centerX, centerY);

            // Draw grid on ground
            DrawGrid(g, centerX, centerY);

            // Draw drone
            DrawDrone(g, centerX, centerY);
        }

        private void DrawTerrain(Graphics g, int centerX, int centerY)
        {
            if (!terrainGenerated) return;

            // Sort triangles by depth (painter's algorithm)
            var triangles = new System.Collections.Generic.List<Triangle>();

            for (int x = 0; x < GRID_SIZE - 1; x++)
            {
                for (int z = 0; z < GRID_SIZE - 1; z++)
                {
                    float h1 = terrainHeights[x, z] * verticalScale;
                    float h2 = terrainHeights[x + 1, z] * verticalScale;
                    float h3 = terrainHeights[x, z + 1] * verticalScale;
                    float h4 = terrainHeights[x + 1, z + 1] * verticalScale;

                    // Transform to screen coordinates
                    var p1 = WorldToScreen(x * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h1, z * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, centerX, centerY);
                    var p2 = WorldToScreen((x + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h2, z * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, centerX, centerY);
                    var p3 = WorldToScreen(x * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h3, (z + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, centerX, centerY);
                    var p4 = WorldToScreen((x + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h4, (z + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, centerX, centerY);

                    float avgHeight = (h1 + h2 + h3 + h4) / 4;
                    Color terrainCol = GetTerrainColor(avgHeight);

                    // Calculate depth for sorting
                    float depth1 = (x + 0.5f) * CELL_SIZE;
                    float depth2 = ((x + 1) + 0.5f) * CELL_SIZE;
                    float depth3 = (z + 0.5f) * CELL_SIZE;
                    float avgDepth = (depth1 + depth2 + depth3) / 3;

                    // Triangle 1
                    triangles.Add(new Triangle(p1, p2, p3, terrainCol, avgDepth));
                    // Triangle 2
                    triangles.Add(new Triangle(p2, p4, p3, terrainCol, avgDepth));
                }
            }

            // Sort by depth (far to near)
            triangles.Sort((a, b) => b.Depth.CompareTo(a.Depth));

            // Draw triangles
            using (var brush = new SolidBrush(Color.Blue))
            {
                foreach (var tri in triangles)
                {
                    brush.Color = tri.Color;
                    var points = new Point[] { tri.P1, tri.P2, tri.P3 };
                    g.FillPolygon(brush, points);
                }
            }

            // Draw edges
            using (var pen = new Pen(Color.FromArgb(40, 40, 60), 0.5f))
            {
                foreach (var tri in triangles)
                {
                    g.DrawLine(pen, tri.P1, tri.P2);
                    g.DrawLine(pen, tri.P2, tri.P3);
                    g.DrawLine(pen, tri.P3, tri.P1);
                }
            }
        }

        private Point WorldToScreen(float worldX, float worldY, float worldZ, int centerX, int centerY)
        {
            // Rotate around Y axis (yaw)
            float cosYaw = (float)Math.Cos(-cameraYaw);
            float sinYaw = (float)Math.Sin(-cameraYaw);
            
            float rotatedX = worldX * cosYaw - worldZ * sinYaw;
            float rotatedZ = worldX * sinYaw + worldZ * cosYaw;

            // Rotate around X axis (pitch) - adjust Y based on pitch
            float cosPitch = (float)Math.Cos(-cameraPitch);
            float sinPitch = (float)Math.Sin(-cameraPitch);
            
            float adjustedY = worldY * cosPitch - rotatedZ * sinPitch;
            float adjustedZ = worldY * sinPitch + rotatedZ * cosPitch;

            // Perspective projection
            float scale = (float)(cameraDistance / (cameraDistance + adjustedZ + 100));
            if (scale < 0.01f) scale = 0.01f;

            int screenX = centerX + (int)(rotatedX * scale);
            int screenY = centerY - (int)(adjustedY * scale * 2);

            return new Point(screenX, screenY);
        }

        private Color GetTerrainColor(float height)
        {
            if (height < -4)
            {
                return Color.FromArgb(20, 60, 120); // Deep water
            }
            else if (height < 0)
            {
                return Color.FromArgb(40, 80, 150); // Shallow water
            }
            else if (height < 3)
            {
                return Color.FromArgb(194, 178, 128); // Beach/sand
            }
            else if (height < 8)
            {
                return Color.FromArgb(76, 120, 50); // Grass
            }
            else if (height < 12)
            {
                return Color.FromArgb(100, 100, 80); // Dirt
            }
            else
            {
                return Color.FromArgb(100, 100, 100); // Mountain/rock
            }
        }

        private void DrawGrid(Graphics g, int centerX, int centerY)
        {
            using (var pen = new Pen(Color.FromArgb(60, 60, 80), 1))
            {
                float gridExtent = GRID_SIZE * CELL_SIZE;
                float start = -gridExtent / 2;

                for (int i = 0; i <= GRID_SIZE; i += 5)
                {
                    float pos = start + i * CELL_SIZE;
                    
                    var p1 = WorldToScreen(pos, -20, start, centerX, centerY);
                    var p2 = WorldToScreen(pos, -20, start + gridExtent, centerX, centerY);
                    var p3 = WorldToScreen(start, -20, pos, centerX, centerY);
                    var p4 = WorldToScreen(start + gridExtent, -20, pos, centerX, centerY);

                    g.DrawLine(pen, p1, p2);
                    g.DrawLine(pen, p3, p4);
                }
            }
        }

        private void DrawDrone(Graphics g, int centerX, int centerY)
        {
            // Transform drone position
            var screenPos = WorldToScreen(
                (float)dronePosition.X, 
                (float)dronePosition.Y, 
                (float)dronePosition.Z, 
                centerX, centerY);

            // Draw drone body
            int size = 20;
            using (var pen = new Pen(Color.Cyan, 2))
            using (var brush = new SolidBrush(Color.FromArgb(100, 0, 180, 200)))
            {
                // Body (circle)
                g.FillEllipse(brush, screenPos.X - size/2, screenPos.Y - size/2, size, size);
                g.DrawEllipse(pen, screenPos.X - size/2, screenPos.Y - size/2, size, size);

                // Direction indicator
                float dirX = (float)Math.Sin(droneHeading * Math.PI / 180) * size;
                float dirY = -(float)Math.Cos(droneHeading * Math.PI / 180) * size;
                
                using (var dirPen = new Pen(Color.Red, 3))
                {
                    g.DrawLine(dirPen, screenPos.X, screenPos.Y, 
                        screenPos.X + (int)dirX, screenPos.Y + (int)dirY);
                }

                // Arms
                float[] armAngles = { 45, 135, 225, 315 };
                foreach (float angle in armAngles)
                {
                    double rad = (angle + droneHeading) * Math.PI / 180;
                    int endX = screenPos.X + (int)(Math.Sin(rad) * size);
                    int endY = screenPos.Y - (int)(Math.Cos(rad) * size);
                    g.DrawLine(pen, screenPos.X, screenPos.Y, endX, endY);
                }
            }
        }

        private void UpdateDronePosition()
        {
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.MAV.cs.lat != 0)
                {
                    double lat = MainV2.comPort.MAV.cs.lat;
                    double lng = MainV2.comPort.MAV.cs.lng;
                    double alt = MainV2.comPort.MAV.cs.alt;
                    float yaw = MainV2.comPort.MAV.cs.yaw;

                    // Convert lat/lng to local coordinates
                    dronePosition = new Point3D(lng * 1000, alt, lat * 1000);
                    droneHeading = yaw;

                    lblLat.Text = "Lat: " + lat.ToString("F6") + "°";
                    lblLng.Text = "Lng: " + lng.ToString("F6") + "°";
                    lblAlt.Text = "Alt: " + alt.ToString("F1") + "m";
                    lblHeading.Text = "HDG: " + yaw.ToString("F0") + "°";
                    lblStatus.Text = "Connected";
                    lblStatus.ForeColor = Color.FromArgb(0, 200, 100);
                }
                else
                {
                    lblStatus.Text = "Disconnected";
                    lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D UpdateDronePosition Error: " + ex.Message);
            }
        }

        private void GlPanel_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                isDragging = true;
                lastMouseX = e.X;
                lastMouseY = e.Y;
            }
        }

        private void GlPanel_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            isDragging = false;
        }

        private void GlPanel_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (isDragging)
            {
                int deltaX = e.X - lastMouseX;
                int deltaY = e.Y - lastMouseY;

                cameraYaw += deltaX * 0.01;
                cameraPitch -= deltaY * 0.01;

                // Clamp pitch
                if (cameraPitch < -Math.PI / 4) cameraPitch = (float)(-Math.PI / 4);
                if (cameraPitch > Math.PI / 3) cameraPitch = (float)(Math.PI / 3);

                lastMouseX = e.X;
                lastMouseY = e.Y;

                renderBox.Invalidate();
            }
        }

        private void GlPanel_MouseWheel(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            cameraDistance -= e.Delta * 0.1;
            if (cameraDistance < 10) cameraDistance = 10;
            if (cameraDistance > 200) cameraDistance = 200;
            renderBox.Invalidate();
        }

        private void BtnResetView_Click(object sender, EventArgs e)
        {
            ResetView();
        }

        private void ResetView()
        {
            cameraDistance = 80.0;
            cameraPitch = 0.3;
            cameraYaw = 0.0;
            renderBox.Invalidate();
        }

        private void VerticalScaleBar_Scroll(object sender, EventArgs e)
        {
            verticalScale = verticalScaleBar.Value / 10.0f;
            lblVerticalScale.Text = "Vertical Scale: " + verticalScale.ToString("F1") + "x";
            renderBox.Invalidate();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            UpdateDronePosition();
            renderBox.Invalidate();
        }

        private void Map3D_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        private void Map3D_Resize(object sender, EventArgs e)
        {
            renderBox.Invalidate();
        }

        public void Activate()
        {
            if (!this.Visible)
            {
                this.Show();
            }
            this.BringToFront();
            this.Focus();
        }

        public void Deactivate()
        {
            this.Hide();
        }

        public static void ShowMap()
        {
            Instance.Activate();
        }

        public static void HideMap()
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Hide();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (refreshTimer != null)
                {
                    refreshTimer.Stop();
                    refreshTimer.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        // Helper classes
        private class Point3D
        {
            public double X, Y, Z;
            public Point3D(double x, double y, double z)
            {
                X = x; Y = y; Z = z;
            }
        }

        private class Triangle
        {
            public Point P1, P2, P3;
            public Color Color;
            public float Depth;

            public Triangle(Point p1, Point p2, Point p3, Color color, float depth)
            {
                P1 = p1; P2 = p2; P3 = p3; Color = color; Depth = depth;
            }
        }
    }
}
