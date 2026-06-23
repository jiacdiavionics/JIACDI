using MissionPlanner.Utilities;
using MissionPlanner.Controls;
using MissionPlanner.ArduPilot;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.MapProviders;
using MissionPlanner.Maps;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// 3D Map Window using GMapControl with satellite imagery.
    /// Provides a real satellite map view with drone position overlay.
    /// </summary>
    public class Map3D : Form, IActivate
    {
        private GMapControl mapControl;
        private GMapOverlay droneOverlay;
        private GMapMarkerDrone droneMarker;
        private System.Windows.Forms.Timer refreshTimer;

        // UI Components
        private Panel controlPanel;
        private Label lblTitle;
        private Label lblLat;
        private Label lblLng;
        private Label lblAlt;
        private Label lblHeading;
        private Label lblStatus;
        private Label lblProvider;
        private ComboBox cmbMapType;
        private Button btnCenterOnDrone;

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
            InitializeMap();
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
            this.Text = "DIMP - 3D Map View (Satellite)";
            this.Size = new Size(1280, 800);
            this.MinimumSize = new Size(800, 600);
            this.BackColor = Color.FromArgb(26, 26, 46);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = false;

            mapControl = new GMapControl();
            controlPanel = new Panel();
            lblTitle = new Label();
            lblLat = new Label();
            lblLng = new Label();
            lblAlt = new Label();
            lblHeading = new Label();
            lblStatus = new Label();
            lblProvider = new Label();
            cmbMapType = new ComboBox();
            btnCenterOnDrone = new Button();
            refreshTimer = new System.Windows.Forms.Timer();

            // Map Control
            mapControl.Dock = DockStyle.Fill;
            mapControl.BackColor = Color.FromArgb(20, 20, 36);
            mapControl.Bearing = 0;
            mapControl.CanDragMap = true;
            mapControl.GrayScaleMode = false;
            mapControl.MarkersEnabled = true;
            mapControl.MaxZoom = 19;
            mapControl.MinZoom = 3;
            mapControl.NegativeMode = false;
            mapControl.PolygonsEnabled = true;
            mapControl.RoutesEnabled = true;
            mapControl.ShowTileGridLines = false;
            mapControl.Zoom = 15;

            // Control Panel
            controlPanel.BackColor = Color.FromArgb(30, 30, 46);
            controlPanel.Dock = DockStyle.Bottom;
            controlPanel.Name = "controlPanel";
            controlPanel.Size = new Size(1272, 80);
            controlPanel.Padding = new Padding(10);

            // Title
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(0, 122, 204);
            lblTitle.Location = new Point(10, 5);
            lblTitle.Name = "lblTitle";
            lblTitle.Text = "3D Map View (Satellite)";

            // Status label
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Segoe UI", 10F);
            lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
            lblStatus.Location = new Point(10, 30);
            lblStatus.Name = "lblStatus";
            lblStatus.Text = "Disconnected";

            // Provider label
            lblProvider.AutoSize = true;
            lblProvider.Font = new Font("Segoe UI", 9F);
            lblProvider.ForeColor = Color.FromArgb(200, 200, 200);
            lblProvider.Location = new Point(10, 55);
            lblProvider.Name = "lblProvider";
            lblProvider.Text = "Provider: --";

            // Lat label
            lblLat.AutoSize = true;
            lblLat.Font = new Font("Segoe UI", 9F);
            lblLat.ForeColor = Color.FromArgb(0, 200, 100);
            lblLat.Location = new Point(200, 10);
            lblLat.Name = "lblLat";
            lblLat.Text = "Lat: --";

            // Lng label
            lblLng.AutoSize = true;
            lblLng.Font = new Font("Segoe UI", 9F);
            lblLng.ForeColor = Color.FromArgb(0, 200, 100);
            lblLng.Location = new Point(320, 10);
            lblLng.Name = "lblLng";
            lblLng.Text = "Lng: --";

            // Alt label
            lblAlt.AutoSize = true;
            lblAlt.Font = new Font("Segoe UI", 9F);
            lblAlt.ForeColor = Color.FromArgb(0, 200, 100);
            lblAlt.Location = new Point(440, 10);
            lblAlt.Name = "lblAlt";
            lblAlt.Text = "Alt: --";

            // Heading label
            lblHeading.AutoSize = true;
            lblHeading.Font = new Font("Segoe UI", 9F);
            lblHeading.ForeColor = Color.FromArgb(0, 200, 100);
            lblHeading.Location = new Point(540, 10);
            lblHeading.Name = "lblHeading";
            lblHeading.Text = "HDG: --";

            // Map Type ComboBox
            cmbMapType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMapType.Font = new Font("Segoe UI", 9F);
            cmbMapType.FormattingEnabled = true;
            cmbMapType.Items.AddRange(new object[] {
                "Bing Satellite",
                "Google Satellite",
                "Bing Hybrid",
                "Google Hybrid"
            });
            cmbMapType.Location = new Point(200, 35);
            cmbMapType.Name = "cmbMapType";
            cmbMapType.Size = new Size(150, 23);
            cmbMapType.SelectedIndex = 0;
            cmbMapType.SelectedIndexChanged += CmbMapType_SelectedIndexChanged;

            // Center on Drone button
            btnCenterOnDrone.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCenterOnDrone.BackColor = Color.FromArgb(0, 122, 204);
            btnCenterOnDrone.FlatStyle = FlatStyle.Flat;
            btnCenterOnDrone.Font = new Font("Segoe UI", 9F);
            btnCenterOnDrone.ForeColor = Color.White;
            btnCenterOnDrone.Location = new Point(1100, 10);
            btnCenterOnDrone.Name = "btnCenterOnDrone";
            btnCenterOnDrone.Size = new Size(130, 30);
            btnCenterOnDrone.TabIndex = 0;
            btnCenterOnDrone.Text = "Center on Drone";
            btnCenterOnDrone.UseVisualStyleBackColor = true;
            btnCenterOnDrone.Click += BtnCenterOnDrone_Click;

            // Add controls to control panel
            controlPanel.Controls.Add(btnCenterOnDrone);
            controlPanel.Controls.Add(cmbMapType);
            controlPanel.Controls.Add(lblProvider);
            controlPanel.Controls.Add(lblStatus);
            controlPanel.Controls.Add(lblHeading);
            controlPanel.Controls.Add(lblAlt);
            controlPanel.Controls.Add(lblLng);
            controlPanel.Controls.Add(lblLat);
            controlPanel.Controls.Add(lblTitle);

            // Add panels to form
            this.Controls.Add(mapControl);
            this.Controls.Add(controlPanel);

            // Timer
            refreshTimer.Interval = 500;
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            // Form events
            this.FormClosing += Map3D_FormClosing;

            this.SuspendLayout();
            this.ResumeLayout(false);
        }

        private void InitializeMap()
        {
            try
            {
                // Set initial provider to Bing Satellite
                mapControl.MapProvider = GMapProviders.BingSatelliteMap;
                lblProvider.Text = "Provider: Bing Satellite";

                // Initialize drone overlay
                droneOverlay = new GMapOverlay("drone");
                droneMarker = new GMapMarkerDrone(new PointLatLng(0, 0));
                droneOverlay.Markers.Add(droneMarker);
                mapControl.Overlays.Add(droneOverlay);

                // Set default position (world view)
                mapControl.Position = new PointLatLng(37.7749, -122.4194);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D InitializeMap Error: " + ex.Message);
            }
        }

        private void CmbMapType_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                switch (cmbMapType.SelectedIndex)
                {
                    case 0: // Bing Satellite
                        mapControl.MapProvider = GMapProviders.BingSatelliteMap;
                        lblProvider.Text = "Provider: Bing Satellite";
                        break;
                    case 1: // Google Satellite
                        mapControl.MapProvider = GMapProviders.GoogleSatelliteMap;
                        lblProvider.Text = "Provider: Google Satellite";
                        break;
                    case 2: // Bing Hybrid
                        mapControl.MapProvider = GMapProviders.BingHybridMap;
                        lblProvider.Text = "Provider: Bing Hybrid";
                        break;
                    case 3: // Google Hybrid
                        mapControl.MapProvider = GMapProviders.GoogleHybridMap;
                        lblProvider.Text = "Provider: Google Hybrid";
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D CmbMapType Error: " + ex.Message);
            }
        }

        private void BtnCenterOnDrone_Click(object sender, EventArgs e)
        {
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.MAV.cs.lat != 0)
                {
                    double lat = MainV2.comPort.MAV.cs.lat;
                    double lng = MainV2.comPort.MAV.cs.lng;
                    mapControl.Position = new PointLatLng(lat, lng);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D CenterOnDrone Error: " + ex.Message);
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

                    // Update marker position
                    droneMarker.Position = new PointLatLng(lat, lng);
                    droneMarker.Heading = yaw;

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

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            UpdateDronePosition();
        }

        private void Map3D_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
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
    }

    /// <summary>
    /// Custom GMap marker for drone visualization
    /// </summary>
    public class GMapMarkerDrone : GMapMarkerBase
    {
        private float heading = 0;
        public float Heading
        {
            get { return heading; }
            set { heading = value; }
        }

        public GMapMarkerDrone(PointLatLng p)
            : base(p)
        {
        }

        public override void OnRender(IGraphics g)
        {
            try
            {
                // Translate to marker position
                g.TranslateTransform(LocalPosition.X, LocalPosition.Y);

                // Rotate based on heading
                if (heading != 0)
                {
                    g.RotateTransform(heading);
                }

                // Draw drone body (quadcopter shape)
                using (var bodyPen = new Pen(Color.Cyan, 2))
                using (var bodyBrush = new SolidBrush(Color.FromArgb(150, 0, 200, 220)))
                {
                    // Main body circle
                    g.FillEllipse(bodyBrush, -8, -8, 16, 16);
                    g.DrawEllipse(bodyPen, -8, -8, 16, 16);

                    // Arms
                    g.DrawLine(bodyPen, -6, -6, -14, -14);
                    g.DrawLine(bodyPen, 6, -6, 14, -14);
                    g.DrawLine(bodyPen, -6, 6, -14, 14);
                    g.DrawLine(bodyPen, 6, 6, 14, 14);

                    // Propellers
                    using (var propBrush = new SolidBrush(Color.FromArgb(200, 50, 50, 50)))
                    {
                        g.FillEllipse(propBrush, -16, -16, 6, 6);
                        g.FillEllipse(propBrush, 10, -16, 6, 6);
                        g.FillEllipse(propBrush, -16, 10, 6, 6);
                        g.FillEllipse(propBrush, 10, 10, 6, 6);
                    }

                    // Direction indicator (nose)
                    using (var nosePen = new Pen(Color.Red, 3))
                    {
                        g.DrawLine(nosePen, 0, 0, 0, -18);
                    }
                }
            }
            catch
            {
                // Silently ignore render errors
            }
        }
    }
}
