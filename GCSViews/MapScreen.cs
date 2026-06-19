using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Standalone map window that can be moved to a second monitor.
    /// This is a separate window from the main DIMP GUI.
    /// </summary>
    public class MapScreen : Form
    {
        private GMapMarker droneMarker;
        private GMapOverlay markersOverlay;
        private System.Windows.Forms.Timer refreshTimer;
        
        // UI Components
        private myGMAP mapControl;
        private Panel statusPanel;
        private Label lblLat;
        private Label lblLng;
        private Label lblAlt;
        private Label lblSpeed;
        private Label lblHeading;
        private Label lblStatus;
        private Panel mapPanel;
        private ToolStrip toolbar;
        private ToolStripButton btnCenter;
        private ToolStripButton btnZoomIn;
        private ToolStripButton btnZoomOut;
        private ToolStripLabel lblZoom;
        private ToolStripButton btnFullscreen;
        
        // Static instance for singleton pattern
        private static MapScreen _instance = null;
        private static readonly object _lock = new object();

        public static MapScreen Instance
        {
            get
            {
                if (_instance == null || _instance.IsDisposed)
                {
                    lock (_lock)
                    {
                        if (_instance == null || _instance.IsDisposed)
                        {
                            _instance = new MapScreen();
                        }
                    }
                }
                return _instance;
            }
        }

        private MapScreen()
        {
            InitializeComponent();
            InitializeMap();
            
            // Position window on secondary monitor if available
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
                    this.WindowState = FormWindowState.Maximized;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MapScreen PositionOnSecondaryMonitor Error: " + ex.Message);
                this.StartPosition = FormStartPosition.CenterScreen;
            }
        }

        private void InitializeComponent()
        {
            this.Text = "DIMP - Map View (Move to second monitor)";
            this.Size = new Size(1280, 800);
            this.MinimumSize = new Size(800, 600);
            this.BackColor = Color.FromArgb(26, 26, 46);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = false;

            this.mapPanel = new Panel();
            this.statusPanel = new Panel();
            this.lblLat = new Label();
            this.lblLng = new Label();
            this.lblAlt = new Label();
            this.lblSpeed = new Label();
            this.lblHeading = new Label();
            this.lblStatus = new Label();
            this.refreshTimer = new System.Windows.Forms.Timer();
            this.toolbar = new ToolStrip();
            this.btnCenter = new ToolStripButton();
            this.btnZoomIn = new ToolStripButton();
            this.btnZoomOut = new ToolStripButton();
            this.lblZoom = new ToolStripLabel("Zoom: 15");
            this.btnFullscreen = new ToolStripButton();
            this.mapControl = new myGMAP();
            
            this.SuspendLayout();
            
            // mapPanel
            this.mapPanel.BackColor = Color.FromArgb(26, 26, 46);
            this.mapPanel.Dock = DockStyle.Fill;
            this.mapPanel.Location = new Point(0, 25);
            this.mapPanel.Name = "mapPanel";
            this.mapPanel.Size = new Size(1272, 717);
            this.mapPanel.TabIndex = 0;
            
            // mapControl
            this.mapControl.BackColor = Color.FromArgb(26, 26, 46);
            this.mapControl.Dock = DockStyle.Fill;
            this.mapControl.Location = new Point(0, 0);
            this.mapControl.Name = "mapControl";
            this.mapControl.Size = new Size(1272, 717);
            this.mapControl.TabIndex = 0;
            this.mapControl.DragButton = MouseButtons.Left;
            
            // toolbar
            this.toolbar.BackColor = Color.FromArgb(30, 30, 46);
            this.toolbar.GripStyle = ToolStripGripStyle.Hidden;
            this.toolbar.ImageScalingSize = new Size(24, 24);
            this.toolbar.Name = "toolbar";
            this.toolbar.Size = new Size(1272, 25);
            this.toolbar.TabIndex = 1;
            
            // Zoom In button
            this.btnZoomIn.Name = "btnZoomIn";
            this.btnZoomIn.Size = new Size(40, 22);
            this.btnZoomIn.Text = "+";
            this.btnZoomIn.ToolTipText = "Zoom In";
            this.btnZoomIn.Click += new EventHandler(this.btnZoomIn_Click);
            
            // Zoom Out button
            this.btnZoomOut.Name = "btnZoomOut";
            this.btnZoomOut.Size = new Size(40, 22);
            this.btnZoomOut.Text = "-";
            this.btnZoomOut.ToolTipText = "Zoom Out";
            this.btnZoomOut.Click += new EventHandler(this.btnZoomOut_Click);
            
            // Zoom label
            this.lblZoom.Name = "lblZoom";
            this.lblZoom.Size = new Size(55, 19);
            this.lblZoom.Text = "Zoom: 15";
            
            // Center button
            this.btnCenter.Name = "btnCenter";
            this.btnCenter.Size = new Size(60, 22);
            this.btnCenter.Text = "Center";
            this.btnCenter.ToolTipText = "Center on Drone";
            this.btnCenter.Click += new EventHandler(this.btnCenter_Click);
            
            // Fullscreen button
            this.btnFullscreen.Name = "btnFullscreen";
            this.btnFullscreen.Size = new Size(75, 22);
            this.btnFullscreen.Text = "Fullscreen";
            this.btnFullscreen.ToolTipText = "Toggle Fullscreen (F11)";
            this.btnFullscreen.Click += new EventHandler(this.btnFullscreen_Click);
            
            // Add items to toolbar
            this.toolbar.Items.AddRange(new ToolStripItem[] {
                this.btnZoomIn,
                this.btnZoomOut,
                new ToolStripSeparator(),
                this.lblZoom,
                new ToolStripSeparator(),
                this.btnCenter,
                this.btnFullscreen
            });
            
            // statusPanel
            this.statusPanel.BackColor = Color.FromArgb(20, 20, 36);
            this.statusPanel.Dock = DockStyle.Bottom;
            this.statusPanel.Location = new Point(0, 742);
            this.statusPanel.Name = "statusPanel";
            this.statusPanel.Size = new Size(1272, 40);
            this.statusPanel.TabIndex = 2;
            
            // Labels
            this.lblLat.AutoSize = true;
            this.lblLat.Font = new Font("Segoe UI", 10F);
            this.lblLat.ForeColor = Color.FromArgb(0, 200, 100);
            this.lblLat.Location = new Point(10, 12);
            this.lblLat.Name = "lblLat";
            this.lblLat.Size = new Size(40, 17);
            this.lblLat.Text = "Lat: -";
            
            this.lblLng.AutoSize = true;
            this.lblLng.Font = new Font("Segoe UI", 10F);
            this.lblLng.ForeColor = Color.FromArgb(0, 200, 100);
            this.lblLng.Location = new Point(170, 12);
            this.lblLng.Name = "lblLng";
            this.lblLng.Size = new Size(41, 17);
            this.lblLng.Text = "Lng: -";
            
            this.lblAlt.AutoSize = true;
            this.lblAlt.Font = new Font("Segoe UI", 10F);
            this.lblAlt.ForeColor = Color.FromArgb(220, 220, 230);
            this.lblAlt.Location = new Point(330, 12);
            this.lblAlt.Name = "lblAlt";
            this.lblAlt.Size = new Size(40, 17);
            this.lblAlt.Text = "Alt: -";
            
            this.lblSpeed.AutoSize = true;
            this.lblSpeed.Font = new Font("Segoe UI", 10F);
            this.lblSpeed.ForeColor = Color.FromArgb(220, 220, 230);
            this.lblSpeed.Location = new Point(490, 12);
            this.lblSpeed.Name = "lblSpeed";
            this.lblSpeed.Size = new Size(40, 17);
            this.lblSpeed.Text = "Spd: -";
            
            this.lblHeading.AutoSize = true;
            this.lblHeading.Font = new Font("Segoe UI", 10F);
            this.lblHeading.ForeColor = Color.FromArgb(220, 220, 230);
            this.lblHeading.Location = new Point(650, 12);
            this.lblHeading.Name = "lblHeading";
            this.lblHeading.Size = new Size(40, 17);
            this.lblHeading.Text = "HDG: -";
            
            this.lblStatus.AutoSize = true;
            this.lblStatus.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            this.lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
            this.lblStatus.Location = new Point(900, 12);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new Size(200, 17);
            this.lblStatus.Text = "Disconnected - Connect to vehicle";
            
            // refreshTimer
            this.refreshTimer.Interval = 500;
            this.refreshTimer.Tick += new EventHandler(this.refreshTimer_Tick);
            
            // Add controls
            this.mapPanel.Controls.Add(this.mapControl);
            this.statusPanel.Controls.AddRange(new Control[] {
                this.lblLat, this.lblLng, this.lblAlt, this.lblSpeed, this.lblHeading, this.lblStatus
            });
            
            // Main form controls
            this.Controls.Add(this.mapPanel);
            this.Controls.Add(this.statusPanel);
            this.Controls.Add(this.toolbar);
            
            // Key preview for F11
            this.KeyPreview = true;
            this.KeyDown += new KeyEventHandler(this.MapScreen_KeyDown);
            
            // Handle form closing
            this.FormClosing += new FormClosingEventHandler(this.MapScreen_FormClosing);
            
            this.ResumeLayout(false);
            this.PerformLayout();
            
            // Start the timer
            refreshTimer.Start();
        }

        private void InitializeMap()
        {
            try
            {
                // Set map provider
                string mapType = Settings.Instance.GetString("mapprovider", "Bing");
                switch (mapType.ToLower())
                {
                    case "google":
                        mapControl.MapProvider = GMap.NET.MapProviders.GMapProviders.GoogleMap;
                        break;
                    case "openstreetmap":
                        mapControl.MapProvider = GMap.NET.MapProviders.GMapProviders.OpenStreetMap;
                        break;
                    default:
                        mapControl.MapProvider = GMap.NET.MapProviders.GMapProviders.BingMap;
                        break;
                }

                GMaps.Instance.Mode = AccessMode.ServerAndCache;

                double lat = Settings.Instance.GetDouble("map_lat", 0);
                double lng = Settings.Instance.GetDouble("map_lng", 0);

                if (lat == 0 && lng == 0)
                {
                    mapControl.Position = new PointLatLng(37.7749, -122.4194);
                }
                else
                {
                    mapControl.Position = new PointLatLng(lat, lng);
                }

                mapControl.MinZoom = 2;
                mapControl.MaxZoom = 18;
                mapControl.Zoom = 15;

                markersOverlay = new GMapOverlay("markers");
                mapControl.Overlays.Add(markersOverlay);

                droneMarker = new GMarkerGoogle(mapControl.Position, GMarkerGoogleType.arrow);
                droneMarker.Size = new Size(40, 40);
                markersOverlay.Markers.Add(droneMarker);
            }
            catch (Exception ex)
            {
                Console.WriteLine("MapScreen InitializeMap Error: " + ex.Message);
            }
        }

        private void btnZoomIn_Click(object sender, EventArgs e)
        {
            if (mapControl.Zoom < mapControl.MaxZoom)
            {
                mapControl.Zoom++;
                UpdateZoomLabel();
            }
        }

        private void btnZoomOut_Click(object sender, EventArgs e)
        {
            if (mapControl.Zoom > mapControl.MinZoom)
            {
                mapControl.Zoom--;
                UpdateZoomLabel();
            }
        }

        private void UpdateZoomLabel()
        {
            lblZoom.Text = "Zoom: " + (int)mapControl.Zoom;
        }

        private void btnCenter_Click(object sender, EventArgs e)
        {
            CenterOnDrone();
        }

        private void btnFullscreen_Click(object sender, EventArgs e)
        {
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (this.WindowState == FormWindowState.Maximized && this.FormBorderStyle == FormBorderStyle.None)
            {
                this.WindowState = FormWindowState.Normal;
                this.FormBorderStyle = FormBorderStyle.Sizable;
            }
            else
            {
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Maximized;
            }
        }

        private void MapScreen_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && this.WindowState == FormWindowState.Maximized)
            {
                this.WindowState = FormWindowState.Normal;
                this.FormBorderStyle = FormBorderStyle.Sizable;
                e.Handled = true;
            }
        }

        private void MapScreen_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                Settings.Instance["map_lat"] = mapControl.Position.Lat.ToString();
                Settings.Instance["map_lng"] = mapControl.Position.Lng.ToString();
            }
            catch { }
            
            refreshTimer.Stop();
            
            lock (_lock)
            {
                _instance = null;
            }
            
            this.Hide();
            e.Cancel = true;
        }

        private void refreshTimer_Tick(object sender, EventArgs e)
        {
            UpdateMapFromTelemetry();
        }

        private void UpdateMapFromTelemetry()
        {
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.MAV.cs.lat != 0)
                {
                    double lat = MainV2.comPort.MAV.cs.lat;
                    double lng = MainV2.comPort.MAV.cs.lng;
                    double alt = MainV2.comPort.MAV.cs.alt;
                    float yaw = MainV2.comPort.MAV.cs.yaw;
                    float groundspeed = MainV2.comPort.MAV.cs.groundspeed;
                    
                    PointLatLng newPos = new PointLatLng(lat, lng);
                    droneMarker.Position = newPos;
                    
                    lblLat.Text = "Lat: " + lat.ToString("F6") + "°";
                    lblLng.Text = "Lng: " + lng.ToString("F6") + "°";
                    lblAlt.Text = "Alt: " + alt.ToString("F1") + "m";
                    lblSpeed.Text = "Spd: " + groundspeed.ToString("F1") + "m/s";
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
                Console.WriteLine("MapScreen UpdateMapFromTelemetry Error: " + ex.Message);
            }
        }

        private void CenterOnDrone()
        {
            if (MainV2.comPort != null && MainV2.comPort.MAV.cs.lat != 0)
            {
                mapControl.Position = new PointLatLng(
                    MainV2.comPort.MAV.cs.lat,
                    MainV2.comPort.MAV.cs.lng);
            }
        }

        public static void ShowMap()
        {
            Instance.Show();
            Instance.BringToFront();
        }

        public static void HideMap()
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Hide();
            }
        }
    }
}
