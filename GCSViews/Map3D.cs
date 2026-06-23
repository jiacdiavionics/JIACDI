using MissionPlanner.Utilities;
using MissionPlanner.Controls;
using MissionPlanner.ArduPilot;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// 3D Map Window using OpenTK for rendering terrain and drone visualization.
    /// This provides a 3D perspective of the terrain with drone position overlay.
    /// </summary>
    public class Map3D : Form, IActivate
    {
        private GameWindow gameWindow;
        private double cameraDistance = 50.0;
        private double cameraPitch = 0.5;
        private double cameraYaw = 0.0;
        private OpenTK.Vector3 dronePosition = new OpenTK.Vector3(0, 0, 0);
        private float droneHeading = 0;
        private bool isDragging = false;
        private int lastMouseX, lastMouseY;
        private bool terrainGenerated = false;
        private const int GRID_SIZE = 100;
        private const float CELL_SIZE = 1.0f;
        private float[,] terrainHeights;
        private float verticalScale = 1.0f;

        private System.Windows.Forms.Timer refreshTimer;
        private Panel glPanel;

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
            InitializeOpenTK();
            GenerateTerrain();
            PositionOnSecondaryMonitor();
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

            // GL Panel (OpenGL rendering surface)
            glPanel.Dock = DockStyle.Fill;
            glPanel.BackColor = Color.FromArgb(20, 20, 36);
            glPanel.Name = "glPanel";

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
            this.Controls.Add(glPanel);
            this.Controls.Add(controlPanel);

            // Timer
            refreshTimer.Interval = 50;
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            // Form events
            this.FormClosing += Map3D_FormClosing;
            this.Resize += Map3D_Resize;

            this.SuspendLayout();
            this.ResumeLayout(false);
        }

        private void InitializeOpenTK()
        {
            try
            {
                // Create GameWindow using the panel's Handle
                gameWindow = new GameWindow(800, 600, GraphicsMode.Default);
                gameWindow.Title = "DIMP - 3D Map View";
                
                // Set up callbacks
                gameWindow.Load += GameWindow_Load;
                gameWindow.RenderFrame += GameWindow_RenderFrame;
                gameWindow.UpdateFrame += GameWindow_UpdateFrame;
                
                // Hook into panel events for input
                glPanel.MouseDown += GlPanel_MouseDown;
                glPanel.MouseUp += GlPanel_MouseUp;
                glPanel.MouseMove += GlPanel_MouseMove;
                glPanel.MouseWheel += GlPanel_MouseWheel;
                
                // Handle resize
                this.Resize += (s, e) => ResizeGameWindow();
                ResizeGameWindow();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D InitializeOpenTK Error: " + ex.Message);
            }
        }

        private void ResizeGameWindow()
        {
            if (gameWindow != null && !gameWindow.IsDisposed)
            {
                try
                {
                    // Recreate the window with new size
                    gameWindow.Close();
                    gameWindow.Dispose();
                }
                catch { }
                
                try
                {
                    gameWindow = new GameWindow(glPanel.Width, glPanel.Height, GraphicsMode.Default);
                    gameWindow.Load += GameWindow_Load;
                    gameWindow.RenderFrame += GameWindow_RenderFrame;
                    gameWindow.UpdateFrame += GameWindow_UpdateFrame;
                    
                    // Set panel as parent
                    SetParent(gameWindow.WindowInfo.Handle, glPanel.Handle);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Map3D ResizeGameWindow Error: " + ex.Message);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        private void GenerateTerrain()
        {
            Random random = new Random();
            terrainHeights = new float[GRID_SIZE, GRID_SIZE];

            for (int x = 0; x < GRID_SIZE; x++)
            {
                for (int z = 0; z < GRID_SIZE; z++)
                {
                    // Generate varying terrain height with some water areas
                    float height = (float)(Math.Sin(x * 0.1) * Math.Cos(z * 0.1) * 5 + 
                                            Math.Sin(x * 0.05 + z * 0.05) * 3);
                    terrainHeights[x, z] = height;
                }
            }

            terrainGenerated = true;
        }

        private void GameWindow_Load(object sender, EventArgs e)
        {
            GL.ClearColor(0.08f, 0.08f, 0.14f, 1.0f); // Dark blue background
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ColorMaterial);
            GL.Enable(EnableCap.Lighting);
            GL.Enable(EnableCap.Light0);
            GL.ShadeModel(ShadingModel.Smooth);

            float[] lightPos = { 1.0f, 1.0f, 1.0f, 0.0f };
            float[] lightColor = { 0.8f, 0.8f, 0.8f, 1.0f };
            GL.Light(LightName.Light0, LightParameter.Position, lightPos);
            GL.Light(LightName.Light0, LightParameter.Diffuse, lightColor);
        }

        private void GameWindow_UpdateFrame(object sender, FrameEventArgs e)
        {
            UpdateDronePosition();
        }

        private void GameWindow_RenderFrame(object sender, FrameEventArgs e)
        {
            if (gameWindow == null || gameWindow.IsDisposed) return;

            GL.Viewport(0, 0, gameWindow.Width, gameWindow.Height);
            
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Set up projection matrix
            Matrix4 projection = Matrix4.Perspectivepective(45.0f, (float)gameWindow.Width / gameWindow.Height, 0.1f, 1000.0f);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref projection);

            // Set up modelview matrix
            Matrix4 modelview = Matrix4.LookAt(
                (float)(cameraDistance * Math.Sin(cameraYaw) * Math.Cos(cameraPitch)),
                (float)(cameraDistance * Math.Sin(cameraPitch) + dronePosition.Y),
                (float)(cameraDistance * Math.Cos(cameraYaw) * Math.Cos(cameraPitch)),
                (float)dronePosition.X, (float)dronePosition.Y, (float)dronePosition.Z,
                0, 1, 0);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref modelview);

            // Draw terrain
            DrawTerrain();

            // Draw grid
            DrawGrid();

            // Draw drone
            DrawDrone();

            gameWindow.SwapBuffers();
        }

        private void DrawTerrain()
        {
            if (!terrainGenerated) return;

            GL.Begin(PrimitiveType.Triangles);

            for (int x = 0; x < GRID_SIZE - 1; x++)
            {
                for (int z = 0; z < GRID_SIZE - 1; z++)
                {
                    float h1 = terrainHeights[x, z] * verticalScale;
                    float h2 = terrainHeights[x + 1, z] * verticalScale;
                    float h3 = terrainHeights[x, z + 1] * verticalScale;
                    float h4 = terrainHeights[x + 1, z + 1] * verticalScale;

                    float avgHeight = (h1 + h2 + h3 + h4) / 4;
                    Color terrainCol = GetTerrainColor(avgHeight);

                    // Triangle 1
                    GL.Color3(terrainCol);
                    GL.Vertex3(x * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h1, z * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2);
                    GL.Vertex3((x + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h2, z * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2);
                    GL.Vertex3(x * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h3, (z + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2);

                    // Triangle 2
                    GL.Color3(terrainCol);
                    GL.Vertex3((x + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h2, z * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2);
                    GL.Vertex3((x + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h4, (z + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2);
                    GL.Vertex3(x * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2, h3, (z + 1) * CELL_SIZE - GRID_SIZE * CELL_SIZE / 2);
                }
            }

            GL.End();
        }

        private Color GetTerrainColor(float height)
        {
            if (height < -2)
            {
                return Color.FromArgb(20, 60, 120); // Deep water
            }
            else if (height < 0)
            {
                return Color.FromArgb(40, 80, 150); // Shallow water
            }
            else if (height < 2)
            {
                return Color.FromArgb(194, 178, 128); // Beach/sand
            }
            else if (height < 5)
            {
                return Color.FromArgb(76, 120, 50); // Grass
            }
            else
            {
                return Color.FromArgb(100, 100, 100); // Mountain/rock
            }
        }

        private void DrawGrid()
        {
            GL.Color3(0.2f, 0.2f, 0.3f);
            GL.Begin(PrimitiveType.Lines);

            float gridExtent = GRID_SIZE * CELL_SIZE;
            float start = -gridExtent / 2;

            for (int i = 0; i <= GRID_SIZE; i += 5)
            {
                float pos = start + i * CELL_SIZE;
                GL.Vertex3(pos, -20, start);
                GL.Vertex3(pos, -20, start + gridExtent);
                GL.Vertex3(start, -20, pos);
                GL.Vertex3(start + gridExtent, -20, pos);
            }

            GL.End();
        }

        private void DrawDrone()
        {
            GL.PushMatrix();
            GL.Translate(dronePosition.X, dronePosition.Y + 2, dronePosition.Z);
            GL.Rotate(droneHeading, 0, 1, 0);

            // Drone body - cyan color
            GL.Color3(0, 0.8f, 1);

            // Main body (box)
            GL.Begin(PrimitiveType.Quads);
            // Top
            GL.Vertex3(-0.5, 0.3, -0.3);
            GL.Vertex3(0.5, 0.3, -0.3);
            GL.Vertex3(0.5, 0.3, 0.3);
            GL.Vertex3(-0.5, 0.3, 0.3);
            // Bottom
            GL.Vertex3(-0.5, -0.3, -0.3);
            GL.Vertex3(0.5, -0.3, -0.3);
            GL.Vertex3(0.5, -0.3, 0.3);
            GL.Vertex3(-0.5, -0.3, 0.3);
            // Front
            GL.Vertex3(-0.5, -0.3, 0.3);
            GL.Vertex3(0.5, -0.3, 0.3);
            GL.Vertex3(0.5, 0.3, 0.3);
            GL.Vertex3(-0.5, 0.3, 0.3);
            // Back
            GL.Vertex3(-0.5, -0.3, -0.3);
            GL.Vertex3(0.5, -0.3, -0.3);
            GL.Vertex3(0.5, 0.3, -0.3);
            GL.Vertex3(-0.5, 0.3, -0.3);
            // Left
            GL.Vertex3(-0.5, -0.3, -0.3);
            GL.Vertex3(-0.5, 0.3, -0.3);
            GL.Vertex3(-0.5, 0.3, 0.3);
            GL.Vertex3(-0.5, -0.3, 0.3);
            // Right
            GL.Vertex3(0.5, -0.3, -0.3);
            GL.Vertex3(0.5, 0.3, -0.3);
            GL.Vertex3(0.5, 0.3, 0.3);
            GL.Vertex3(0.5, -0.3, 0.3);
            GL.End();

            // Arms
            GL.Color3(0.3f, 0.3f, 0.4f);
            GL.Begin(PrimitiveType.Lines);
            GL.Vertex3(0.3, 0, 0.3); GL.Vertex3(2, 0, 2);
            GL.Vertex3(-0.3, 0, 0.3); GL.Vertex3(-2, 0, 2);
            GL.Vertex3(0.3, 0, -0.3); GL.Vertex3(2, 0, -2);
            GL.Vertex3(-0.3, 0, -0.3); GL.Vertex3(-2, 0, -2);
            GL.End();

            // Propellers
            GL.Color3(0.5f, 0.5f, 0.5f);
            DrawCircle(2, 0, 2, 0.3f);
            DrawCircle(-2, 0, 2, 0.3f);
            DrawCircle(2, 0, -2, 0.3f);
            DrawCircle(-2, 0, -2, 0.3f);

            // Direction indicator (nose)
            GL.Color3(1, 0, 0);
            GL.Begin(PrimitiveType.Triangles);
            GL.Vertex3(0, 0, 0.5);
            GL.Vertex3(-0.2, 0, 0.8);
            GL.Vertex3(0.2, 0, 0.8);
            GL.End();

            GL.PopMatrix();
        }

        private void DrawCircle(float x, float y, float z, float radius)
        {
            GL.Begin(PrimitiveType.LineLoop);
            for (int i = 0; i < 16; i++)
            {
                double angle = 2 * Math.PI * i / 16;
                GL.Vertex3(x + (float)(radius * Math.Cos(angle)), y, z + (float)(radius * Math.Sin(angle)));
            }
            GL.End();
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

                    dronePosition = new OpenTK.Vector3((float)(lng * 100), (float)(alt / 10.0), (float)(lat * 100));
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

                cameraYaw -= deltaX * 0.005;
                cameraPitch += deltaY * 0.005;

                if (cameraPitch < 0.1) cameraPitch = 0.1;
                if (cameraPitch > Math.PI / 2 - 0.1) cameraPitch = Math.PI / 2 - 0.1;

                lastMouseX = e.X;
                lastMouseY = e.Y;
            }
        }

        private void GlPanel_MouseWheel(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            cameraDistance -= e.Delta * 0.05;
            if (cameraDistance < 5) cameraDistance = 5;
            if (cameraDistance > 200) cameraDistance = 200;
        }

        private void BtnResetView_Click(object sender, EventArgs e)
        {
            ResetView();
        }

        private void ResetView()
        {
            cameraDistance = 50.0;
            cameraPitch = 0.5;
            cameraYaw = 0.0;
        }

        private void VerticalScaleBar_Scroll(object sender, EventArgs e)
        {
            verticalScale = verticalScaleBar.Value / 10.0f;
            lblVerticalScale.Text = "Vertical Scale: " + verticalScale.ToString("F1") + "x";
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            if (gameWindow != null && !gameWindow.IsDisposed)
            {
                try
                {
                    gameWindow.ProcessEvents();
                }
                catch { }
            }
        }

        private void Map3D_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        private void Map3D_Resize(object sender, EventArgs e)
        {
            if (glPanel != null && gameWindow != null && !gameWindow.IsDisposed)
            {
                try
                {
                    gameWindow.Width = glPanel.Width;
                    gameWindow.Height = glPanel.Height;
                }
                catch { }
            }
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
                if (gameWindow != null)
                {
                    gameWindow.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
