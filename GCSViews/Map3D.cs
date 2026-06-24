using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MissionPlanner.Controls;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Cesium-powered 3D terrain map window.
    /// </summary>
    public class Map3D : Form, IActivate
    {
        private WebView2 webView;
        private ToolStrip toolbar;
        private ToolStripButton btnCenter;
        private ToolStripButton btnFollow;
        private ToolStripButton btnReset;
        private ToolStripButton btnClearTrack;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        private ToolStripStatusLabel lblLat;
        private ToolStripStatusLabel lblLng;
        private ToolStripStatusLabel lblAlt;
        private ToolStripStatusLabel lblSpeed;
        private ToolStripStatusLabel lblHeading;
        private Timer refreshTimer;

        private bool webReady;
        private bool followVehicle = true;
        private bool telemetryValid;

        private static Map3D _instance;
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
            PositionOnSecondaryMonitor();
            InitializeWebView();
        }

        private void InitializeComponent()
        {
            Text = "DIMP - 3D Map";
            Size = new Size(1280, 800);
            MinimumSize = new Size(900, 600);
            BackColor = Color.FromArgb(18, 18, 28);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;

            toolbar = new ToolStrip();
            btnCenter = new ToolStripButton();
            btnFollow = new ToolStripButton();
            btnReset = new ToolStripButton();
            btnClearTrack = new ToolStripButton();
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            lblLat = new ToolStripStatusLabel();
            lblLng = new ToolStripStatusLabel();
            lblAlt = new ToolStripStatusLabel();
            lblSpeed = new ToolStripStatusLabel();
            lblHeading = new ToolStripStatusLabel();
            webView = new WebView2();
            refreshTimer = new Timer();

            SuspendLayout();

            toolbar.BackColor = Color.FromArgb(30, 30, 46);
            toolbar.ForeColor = Color.FromArgb(220, 220, 230);
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Dock = DockStyle.Top;
            toolbar.Renderer = new MissionPlanner.Controls.DIMPRenderer();

            ConfigureToolButton(btnCenter, "Center", "Center terrain view on vehicle");
            btnCenter.Click += (sender, args) => ExecuteMapCommand("centerOnVehicle");

            ConfigureToolButton(btnFollow, "Follow: On", "Toggle vehicle follow");
            btnFollow.Click += (sender, args) =>
            {
                followVehicle = !followVehicle;
                btnFollow.Text = followVehicle ? "Follow: On" : "Follow: Off";
                ExecuteMapCommand(followVehicle ? "enableFollow" : "disableFollow");
            };

            ConfigureToolButton(btnReset, "Reset View", "Reset camera view");
            btnReset.Click += (sender, args) => ExecuteMapCommand("resetView");

            ConfigureToolButton(btnClearTrack, "Clear Track", "Clear vehicle track");
            btnClearTrack.Click += (sender, args) => ExecuteMapCommand("clearTrack");

            toolbar.Items.AddRange(new ToolStripItem[]
            {
                btnCenter,
                btnFollow,
                btnReset,
                btnClearTrack
            });

            webView.Dock = DockStyle.Fill;
            webView.BackColor = Color.Black;
            webView.DefaultBackgroundColor = Color.Black;

            statusStrip.BackColor = Color.FromArgb(20, 20, 36);
            statusStrip.ForeColor = Color.FromArgb(220, 220, 230);
            statusStrip.SizingGrip = false;
            statusStrip.Items.AddRange(new ToolStripItem[]
            {
                lblStatus,
                new ToolStripStatusLabel { Spring = true },
                lblLat,
                lblLng,
                lblAlt,
                lblSpeed,
                lblHeading
            });

            lblStatus.Text = "Loading 3D map";
            lblStatus.ForeColor = Color.FromArgb(255, 210, 120);
            lblLat.Text = "Lat: --";
            lblLng.Text = "Lng: --";
            lblAlt.Text = "Alt: --";
            lblSpeed.Text = "Spd: --";
            lblHeading.Text = "HDG: --";

            refreshTimer.Interval = 500;
            refreshTimer.Tick += RefreshTimer_Tick;

            Controls.Add(webView);

            FormClosing += Map3D_FormClosing;

            ResumeLayout(false);
            PerformLayout();
        }

        private static void ConfigureToolButton(ToolStripButton button, string text, string tooltip)
        {
            button.DisplayStyle = ToolStripItemDisplayStyle.Text;
            button.Text = text;
            button.ToolTipText = tooltip;
            button.ForeColor = Color.FromArgb(220, 220, 230);
            button.Margin = new Padding(3, 2, 3, 2);
            button.Padding = new Padding(8, 3, 8, 3);
        }

        private async void InitializeWebView()
        {
            try
            {
                ConfigureWebView2LoaderPath();

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DIMP",
                    "WebView2",
                    "Map3D");

                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.NavigateToString(GetCesiumHtml());
            }
            catch (Exception ex)
            {
                lblStatus.Text = "3D map unavailable";
                lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                CustomMessageBox.Show(
                    "The 3D map requires Microsoft Edge WebView2 Runtime and internet access for map tiles." +
                    Environment.NewLine + Environment.NewLine + ex.Message,
                    "3D Map");
            }
        }

        private static void ConfigureWebView2LoaderPath()
        {
            try
            {
                string runtimeName = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                string loaderFolder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "runtimes",
                    runtimeName,
                    "native");

                if (Directory.Exists(loaderFolder))
                {
                    CoreWebView2Environment.SetLoaderDllFolderPath(loaderFolder);
                }
            }
            catch (InvalidOperationException)
            {
                // WebView2 has already loaded the native loader.
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D WebView2 loader path error: " + ex.Message);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.TryGetWebMessageAsString();

            if (message == "ready")
            {
                webReady = true;
                lblStatus.Text = "3D map ready";
                lblStatus.ForeColor = Color.FromArgb(0, 200, 100);
                ExecuteMapCommand(followVehicle ? "enableFollow" : "disableFollow");
                refreshTimer.Start();
                UpdateMapFromTelemetry();
            }
            else if (message.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                lblStatus.Text = "3D map error";
                lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                Console.WriteLine("Map3D WebView Error: " + message);
            }
        }

        private void PositionOnSecondaryMonitor()
        {
            try
            {
                Screen[] screens = Screen.AllScreens;

                if (screens.Length > 1)
                {
                    Screen secondary = screens[1];
                    StartPosition = FormStartPosition.Manual;
                    Location = secondary.WorkingArea.Location;
                    WindowState = FormWindowState.Maximized;
                }
                else
                {
                    StartPosition = FormStartPosition.CenterScreen;
                    WindowState = FormWindowState.Maximized;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D PositionOnSecondaryMonitor Error: " + ex.Message);
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            UpdateMapFromTelemetry();
        }

        private void UpdateMapFromTelemetry()
        {
            try
            {
                if (MainV2.comPort != null &&
                    MainV2.comPort.MAV != null &&
                    MainV2.comPort.MAV.cs != null &&
                    MainV2.comPort.MAV.cs.lat != 0 &&
                    MainV2.comPort.MAV.cs.lng != 0)
                {
                    double lat = MainV2.comPort.MAV.cs.lat;
                    double lng = MainV2.comPort.MAV.cs.lng;
                    double relativeAlt = MainV2.comPort.MAV.cs.alt;
                    double absoluteAlt = MainV2.comPort.MAV.cs.altasl;
                    float yaw = MainV2.comPort.MAV.cs.yaw;
                    float groundSpeed = MainV2.comPort.MAV.cs.groundspeed;

                    if (Math.Abs(absoluteAlt) < 0.01)
                    {
                        absoluteAlt = relativeAlt;
                    }

                    telemetryValid = true;
                    lblStatus.Text = "Connected";
                    lblStatus.ForeColor = Color.FromArgb(0, 200, 100);
                    lblLat.Text = "Lat: " + lat.ToString("F6", CultureInfo.InvariantCulture);
                    lblLng.Text = "Lng: " + lng.ToString("F6", CultureInfo.InvariantCulture);
                    lblAlt.Text = "Alt: " + relativeAlt.ToString("F1", CultureInfo.InvariantCulture) + " m";
                    lblSpeed.Text = "Spd: " + groundSpeed.ToString("F1", CultureInfo.InvariantCulture) + " m/s";
                    lblHeading.Text = "HDG: " + yaw.ToString("F0", CultureInfo.InvariantCulture);

                    if (webReady)
                    {
                        string script = string.Format(
                            CultureInfo.InvariantCulture,
                            "window.dimpMap && window.dimpMap.setVehicle({0},{1},{2},{3},{4});",
                            lat,
                            lng,
                            absoluteAlt,
                            yaw,
                            groundSpeed);

                        _ = webView.CoreWebView2.ExecuteScriptAsync(script);
                    }
                }
                else
                {
                    telemetryValid = false;
                    lblStatus.Text = webReady ? "Disconnected" : "Loading 3D map";
                    lblStatus.ForeColor = webReady ? Color.FromArgb(255, 100, 100) : Color.FromArgb(255, 210, 120);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D UpdateMapFromTelemetry Error: " + ex.Message);
            }
        }

        private void ExecuteMapCommand(string command)
        {
            if (!webReady || webView.CoreWebView2 == null)
            {
                return;
            }

            if (command == "centerOnVehicle" && !telemetryValid)
            {
                return;
            }

            _ = webView.CoreWebView2.ExecuteScriptAsync("window.dimpMap && window.dimpMap." + command + "();");
        }

        private void Map3D_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        public new void Activate()
        {
            if (!Visible)
            {
                Show();
            }

            BringToFront();
            Focus();

            if (webReady && !refreshTimer.Enabled)
            {
                refreshTimer.Start();
            }
        }

        public new void Deactivate()
        {
            Hide();
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

                if (webView != null)
                {
                    webView.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private static string GetCesiumHtml()
        {
            return @"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta http-equiv=""X-UA-Compatible"" content=""IE=edge"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>DIMP 3D Terrain Map</title>
  <script src=""https://cesium.com/downloads/cesiumjs/releases/1.120/Build/Cesium/Cesium.js""></script>
  <link href=""https://cesium.com/downloads/cesiumjs/releases/1.120/Build/Cesium/Widgets/widgets.css"" rel=""stylesheet"">
  <style>
    html, body, #cesiumContainer {
      width: 100%;
      height: 100%;
      margin: 0;
      padding: 0;
      overflow: hidden;
      background: #0a0e1a;
      font-family: Segoe UI, Arial, sans-serif;
    }

    .cesium-viewer-bottom,
    .cesium-credit-logoContainer {
      display: none !important;
    }

    .cesium-viewer-toolbar,
    .cesium-viewer-animationContainer,
    .cesium-viewer-timelineContainer,
    .cesium-viewer-fullscreenContainer,
    .cesium-viewer-geocoderContainer,
    .cesium-navigationHelpButton-wrapper,
    .cesium-sceneModePicker-wrapper,
    .cesium-home-button {
      display: none !important;
    }

    #message {
      position: absolute;
      right: 14px;
      top: 14px;
      z-index: 5;
      max-width: 360px;
      padding: 10px 12px;
      color: #fee2e2;
      background: rgba(69, 10, 10, 0.86);
      border: 1px solid rgba(248, 113, 113, 0.6);
      border-radius: 6px;
      display: none;
    }
    
    #loading {
      position: absolute;
      left: 50%;
      top: 50%;
      transform: translate(-50%, -50%);
      color: #4a90d9;
      font-size: 16px;
      z-index: 10;
    }
  </style>
</head>
<body>
  <div id=""loading"">Loading 3D Map...</div>
  <div id=""cesiumContainer""></div>
  <div id=""message""></div>
  <script>
    (function () {
      const post = (message) => {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(message);
        }
      };

      const showError = (message) => {
        const box = document.getElementById('message');
        box.textContent = message;
        box.style.display = 'block';
        post('error:' + message);
      };

      if (!window.Cesium) {
        showError('Cesium could not be loaded.');
        return;
      }

      // High-quality ESRI World Imagery satellite tiles
      const satelliteImagery = new Cesium.UrlTemplateImageryProvider({
        url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
        maximumLevel: 19,
        credit: 'Esri World Imagery'
      });

      const viewer = new Cesium.Viewer('cesiumContainer', {
        animation: false,
        baseLayerPicker: false,
        fullscreenButton: false,
        geocoder: false,
        homeButton: false,
        infoBox: false,
        imageryProvider: satelliteImagery,
        navigationHelpButton: false,
        sceneMode: Cesium.SceneMode.SCENE3D,
        sceneModePicker: false,
        selectionIndicator: false,
        timeline: false,
        terrainProvider: new Cesium.EllipsoidTerrainProvider(),
        skyAtmosphere: new Cesium.SkyAtmosphere()
      });

      // Hide loading indicator once terrain starts loading
      document.getElementById('loading').style.display = 'none';

      // Configure scene for best 3D terrain visualization
      viewer.scene.globe.enableLighting = true;
      viewer.scene.globe.showGroundAtmosphere = true;
      viewer.scene.globe.depthTestAgainstTerrain = false;
      viewer.scene.globe.maximumScreenSpaceError = 1;
      
      // Atmosphere settings for realistic sky
      if (viewer.scene.skyAtmosphere) {
        viewer.scene.skyAtmosphere.show = true;
        viewer.scene.skyAtmosphere.brightnessShift = 0.0;
      }
      
      // Sun lighting
      viewer.scene.sun.show = true;

      // Camera settings
      viewer.scene.screenSpaceCameraController.minimumZoomDistance = 50;
      viewer.scene.screenSpaceCameraController.maximumZoomDistance = 25000000;
      
      // Enable anti-aliasing
      viewer.scene.fxaa = true;

      // Vertical exaggeration for terrain
      if ('verticalExaggeration' in viewer.scene) {
        viewer.scene.verticalExaggeration = 2.0;
        viewer.scene.verticalExaggerationRelativeHeight = 0;
      }

      // Load terrain with fallback chain
      const loadTerrain = async () => {
        const terrainUrls = [
          // OpenTopoData terrain
          { url: 'https://api.open-topo-data.net/v2/terrain', name: 'OpenTopoData' },
          // STK Terrain from AGI
          { url: 'https://assets.agi.com/stk-terrain/v1/tileset1.json', name: 'STK Terrain' },
          // Cesium World Terrain via ion (public default)
          { url: 'https://assets.cesium.com/1/cesium-terrain-provider', name: 'Cesium Ion' }
        ];

        for (const terrainConfig of terrainUrls) {
          try {
            console.log('Trying terrain:', terrainConfig.name);
            const terrainProvider = await Cesium.CesiumTerrainProvider.fromUrl(
              terrainConfig.url,
              {
                requestVertexNormals: true,
                requestWaterMask: true
              }
            );
            viewer.terrainProvider = terrainProvider;
            console.log('Successfully loaded:', terrainConfig.name);
            post('terrain:' + terrainConfig.name);
            return;
          } catch (e) {
            console.log(terrainConfig.name + ' failed:', e.message);
          }
        }
        
        // If all terrain sources fail, use ellipsoid (no 3D terrain but map still works)
        console.log('All terrain sources failed, using flat globe');
        post('terrain:none');
      };

      // Start loading terrain
      loadTerrain();

      // Add labels overlay after a delay
      setTimeout(() => {
        try {
          const labelsImagery = new Cesium.UrlTemplateImageryProvider({
            url: 'https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}',
            maximumLevel: 19
          });
          viewer.imageryLayers.addImageryProvider(labelsImagery);
        } catch (e) {
          console.log('Could not add labels:', e.message);
        }
      }, 3000);

      const defaultView = {
        lng: 35.5860,
        lat: 31.4750,
        height: 5000,
        heading: 72,
        pitch: -25
      };

      viewer.camera.flyTo({
        destination: Cesium.Cartesian3.fromDegrees(
          defaultView.lng,
          defaultView.lat,
          defaultView.height),
        orientation: {
          heading: Cesium.Math.toRadians(defaultView.heading),
          pitch: Cesium.Math.toRadians(defaultView.pitch),
          roll: 0
        },
        duration: 1.0
      });

      let followVehicle = true;
      let lastPosition = null;
      let firstVehicleFix = true;
      let lastFollowCameraMove = 0;

      const lookAtVehicle = (position, heading, duration) => {
        const cartographic = Cesium.Cartographic.fromCartesian(position);
        const lng = Cesium.Math.toDegrees(cartographic.longitude);
        const lat = Cesium.Math.toDegrees(cartographic.latitude);
        const alt = Math.max(cartographic.height + 3000, 3000);

        viewer.camera.flyTo({
          destination: Cesium.Cartesian3.fromDegrees(lng, lat, alt),
          orientation: {
            heading: Cesium.Math.toRadians(heading),
            pitch: Cesium.Math.toRadians(-25),
            roll: 0
          },
          duration: duration
        });
      };

      window.dimpMap = {
        setVehicle: function (lat, lng, alt, heading, speed) {
          if (!Number.isFinite(lat) || !Number.isFinite(lng)) {
            return;
          }

          const mapAlt = Number.isFinite(alt) ? alt : 0;
          const position = Cesium.Cartesian3.fromDegrees(lng, lat, mapAlt);
          lastPosition = position;

          if (firstVehicleFix) {
            firstVehicleFix = false;
            lookAtVehicle(position, heading || 0, 0.8);
            lastFollowCameraMove = Date.now();
          } else if (followVehicle && Date.now() - lastFollowCameraMove > 4000) {
            lookAtVehicle(position, heading || 0, 0.4);
            lastFollowCameraMove = Date.now();
          }
        },
        centerOnVehicle: function () {
          if (lastPosition) {
            lookAtVehicle(lastPosition, 0, 0.5);
          }
        },
        enableFollow: function () {
          followVehicle = true;
          if (lastPosition) {
            lookAtVehicle(lastPosition, 0, 0.5);
            lastFollowCameraMove = Date.now();
          }
        },
        disableFollow: function () {
          followVehicle = false;
        },
        resetView: function () {
          viewer.camera.flyTo({
            destination: Cesium.Cartesian3.fromDegrees(
              defaultView.lng,
              defaultView.lat,
              defaultView.height),
            orientation: {
              heading: Cesium.Math.toRadians(defaultView.heading),
              pitch: Cesium.Math.toRadians(defaultView.pitch),
              roll: 0
            },
            duration: 0.8
          });
        },
        clearTrack: function () {
        }
      };

      post('ready');
    })();
  </script>
</body>
</html>";
        }
    }
}
