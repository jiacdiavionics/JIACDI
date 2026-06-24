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

            // Add controls in correct order for proper docking
            // webView must be added FIRST (will be below toolbar due to docking order)
            // toolbar and statusStrip use Dock.Top, webView uses DockStyle.None (default)
            Controls.Add(webView);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

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

                // Center on primary screen by default
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(Math.Min(1280, Screen.PrimaryScreen.WorkingArea.Width),
                               Math.Min(800, Screen.PrimaryScreen.WorkingArea.Height - 100));

                if (screens.Length > 1)
                {
                    // Position on secondary monitor if available
                    Screen secondary = screens[1];
                    StartPosition = FormStartPosition.Manual;
                    Location = secondary.WorkingArea.Location;
                    Size = new Size(Math.Min(1280, secondary.WorkingArea.Width),
                                   Math.Min(800, secondary.WorkingArea.Height));
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
    * {
      margin: 0;
      padding: 0;
      box-sizing: border-box;
    }
    html, body, #cesiumContainer {
      width: 100%;
      height: 100%;
      margin: 0;
      padding: 0;
      overflow: hidden;
      background: #87CEEB;
      font-family: 'Segoe UI', Arial, sans-serif;
    }

    #loading-overlay {
      position: absolute;
      left: 0;
      top: 0;
      right: 0;
      bottom: 0;
      background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
      display: flex;
      flex-direction: column;
      justify-content: center;
      align-items: center;
      z-index: 9999;
      color: #fff;
    }

    #loading-overlay.hidden {
      display: none;
    }

    #loading-spinner {
      width: 50px;
      height: 50px;
      border: 4px solid rgba(255,255,255,0.2);
      border-top: 4px solid #4a90d9;
      border-radius: 50%;
      animation: spin 1s linear infinite;
      margin-bottom: 20px;
    }

    @keyframes spin {
      0% { transform: rotate(0deg); }
      100% { transform: rotate(360deg); }
    }

    #loading-text {
      font-size: 16px;
      color: #ccc;
    }

    #status-message {
      font-size: 12px;
      color: #888;
      margin-top: 10px;
      max-width: 300px;
      text-align: center;
    }

    #error-overlay {
      position: absolute;
      left: 0;
      top: 0;
      right: 0;
      bottom: 0;
      background: rgba(0,0,0,0.85);
      display: none;
      flex-direction: column;
      justify-content: center;
      align-items: center;
      z-index: 9998;
      color: #fff;
      padding: 20px;
    }

    #error-overlay.visible {
      display: flex;
    }

    #error-title {
      font-size: 20px;
      color: #ff6b6b;
      margin-bottom: 15px;
    }

    #error-details {
      font-size: 14px;
      color: #ccc;
      max-width: 500px;
      text-align: center;
      line-height: 1.6;
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
  </style>
</head>
<body>
  <div id=""loading-overlay"">
    <div id=""loading-spinner""></div>
    <div id=""loading-text"">Initializing 3D Map...</div>
    <div id=""status-message"">Loading Cesium engine</div>
  </div>

  <div id=""error-overlay"">
    <div id=""error-title"">3D Map Error</div>
    <div id=""error-details""></div>
  </div>

  <div id=""cesiumContainer""></div>

  <script>
    (function () {
      'use strict';

      // Status reporting to host
      const post = (message) => {
        console.log('[DIMP-3D]', message);
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(message);
        }
      };

      const setStatus = (text) => {
        document.getElementById('status-message').textContent = text;
        post('status:' + text);
      };

      const showError = (title, message) => {
        document.getElementById('error-title').textContent = title || 'Error';
        document.getElementById('error-details').textContent = message;
        document.getElementById('error-overlay').classList.add('visible');
        post('error:' + title + ': ' + message);
      };

      const hideLoading = () => {
        document.getElementById('loading-overlay').classList.add('hidden');
      };

      // Global error handlers
      window.onerror = function(msg, url, line, col, error) {
        setStatus('JavaScript error: ' + msg);
        showError('JavaScript Error', msg + ' (line ' + line + ')');
        return false;
      };

      window.onunhandledrejection = function(event) {
        setStatus('Unhandled promise error');
        showError('Promise Error', event.reason ? event.reason.toString() : 'Unknown error');
      };

      // Check Cesium loaded
      if (typeof Cesium === 'undefined' || !window.Cesium) {
        showError('Load Error', 'Cesium.js failed to load. Please check your internet connection and ensure WebView2 is installed.');
        document.getElementById('loading-text').textContent = 'Failed to load Cesium';
        return;
      }

      setStatus('Cesium loaded successfully');
      post('debug:Cesium initialized');

      // Default view position: Amman, Jordan
      const defaultView = {
        lng: 35.9106,
        lat: 31.9539,
        height: 2000,      // 2km altitude - close enough to see terrain
        heading: 0,         // North
        pitch: -45          // 45 degrees down - looking at terrain
      };

      // Create viewer with Bing imagery (includes terrain elevation)
      setStatus('Creating map viewer...');

      try {
        // Use ESRI World Imagery for clear satellite tiles
        const imageryProvider = new Cesium.UrlTemplateImageryProvider({
          url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
          maximumLevel: 19,
          credit: 'Esri World Imagery'
        });

        // Create viewer
        const viewer = new Cesium.Viewer('cesiumContainer', {
          animation: false,
          baseLayerPicker: false,
          fullscreenButton: false,
          geocoder: false,
          homeButton: false,
          infoBox: false,
          imageryProvider: imageryProvider,
          navigationHelpButton: false,
          sceneMode: Cesium.SceneMode.SCENE3D,
          sceneModePicker: false,
          selectionIndicator: false,
          timeline: false,
          // Start with ellipsoid (flat) terrain, then upgrade to 3D terrain
          terrainProvider: new Cesium.EllipsoidTerrainProvider(),
          skyBox: false,
          skyAtmosphere: false,
          requestRenderMode: false,
          maximumRenderTimeChange: Infinity
        });

        setStatus('Viewer created, configuring scene...');

        // Hide default imagery picker button if it appears
        viewer.baseLayerPicker = false;

        // Configure scene for TERRAIN view (not space/globe)
        const scene = viewer.scene;

        // Disable atmosphere effects that make it look like space
        scene.globe.enableLighting = false;
        scene.globe.showGroundAtmosphere = false;
        scene.skyAtmosphere.show = false;
        
        // Show sun during day
        scene.sun.show = true;

        // Set globe to show terrain imagery clearly
        scene.globe.depthTestAgainstTerrain = false;
        scene.globe.maximumScreenSpaceError = 2; // Higher = faster rendering, lower = sharper
        
        // Configure camera for terrain viewing
        scene.screenSpaceCameraController.minimumZoomDistance = 100;   // 100m minimum height
        scene.screenSpaceCameraController.maximumZoomDistance = 50000; // 50km maximum height
        
        // Reduce terrain exaggeration for cleaner look
        if ('verticalExaggeration' in scene) {
          scene.verticalExaggeration = 1.0; // No exaggeration
        }

        // Enable FXAA for smoother edges
        if (scene.fxaa) {
          scene.fxaa = true;
        }

        post('debug:Scene configured');

        // Load 3D terrain data
        setStatus('Loading terrain data...');

        const loadTerrain = async () => {
          // Try to load Cesium World Terrain (includes elevation)
          try {
            setStatus('Requesting 3D terrain...');
            
            // Try STK Terrain from AGI
            const terrainProvider = await Cesium.CesiumTerrainProvider.fromUrl(
              'https://assets.agi.com/stk-terrain/v1/tileset1.json',
              {
                requestVertexNormals: false, // Faster loading
                requestWaterMask: false      // Faster loading
              }
            );

            viewer.terrainProvider = terrainProvider;
            setStatus('3D terrain loaded');
            post('debug:Terrain loaded - STK Terrain');
            
            // Enable lighting now that terrain is loaded
            scene.globe.enableLighting = true;
            
          } catch (terrainError) {
            console.log('Terrain load failed:', terrainError);
            setStatus('Terrain unavailable - using flat map');
            post('debug:Terrain unavailable - flat globe mode');
            
            // The map will still show satellite imagery, just without elevation
          }
        };

        // Start terrain loading
        loadTerrain();

        // Set initial camera position to show TERRAIN immediately
        setStatus('Setting camera view...');

        // Use flyTo with a view that looks AT the terrain
        viewer.camera.flyTo({
          destination: Cesium.Cartesian3.fromDegrees(
            defaultView.lng,
            defaultView.lat,
            defaultView.height
          ),
          orientation: {
            heading: Cesium.Math.toRadians(defaultView.heading),
            pitch: Cesium.Math.toRadians(defaultView.pitch), // Looking DOWN at terrain
            roll: 0
          },
          duration: 0 // Instant - no animation delay
        });

        setStatus('Map ready');
        post('debug:Initial view set to terrain');
        hideLoading();

        // Vehicle tracking state
        let followVehicle = true;
        let lastPosition = null;
        let firstVehicleFix = true;
        let lastFollowCameraMove = 0;

        // Look at terrain from above
        const lookAtTerrain = (lat, lng, altitude, heading, duration) => {
          if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;

          // Calculate camera altitude: above terrain + offset
          const cameraHeight = Math.max(Number.isFinite(altitude) ? altitude : 0, 500) + 1500;
          
          viewer.camera.flyTo({
            destination: Cesium.Cartesian3.fromDegrees(
              lng,   // Note: Cesium uses lng, lat order
              lat,
              cameraHeight
            ),
            orientation: {
              heading: Cesium.Math.toRadians(Number.isFinite(heading) ? heading : 0),
              pitch: Cesium.Math.toRadians(-35), // Looking down at terrain
              roll: 0
            },
            duration: duration || 0.5
          });
        };

        // Expose API for C# to call
        window.dimpMap = {
          // Set vehicle position - this is called from C# with telemetry data
          setVehicle: function(lat, lng, alt, heading, speed) {
            if (!Number.isFinite(lat) || !Number.isFinite(lng)) {
              return;
            }

            lastPosition = { lat: lat, lng: lng, alt: alt };

            if (firstVehicleFix) {
              firstVehicleFix = false;
              setStatus('Using vehicle position');
              post('debug:Using vehicle position: ' + lat + ', ' + lng);
              lookAtTerrain(lat, lng, alt, heading, 1.0);
              lastFollowCameraMove = Date.now();
            } else if (followVehicle && Date.now() - lastFollowCameraMove > 3000) {
              lookAtTerrain(lat, lng, alt, heading, 0.3);
              lastFollowCameraMove = Date.now();
            }
          },

          // Center camera on vehicle
          centerOnVehicle: function() {
            if (lastPosition) {
              setStatus('Centering on vehicle');
              post('debug:Center on vehicle');
              lookAtTerrain(lastPosition.lat, lastPosition.lng, lastPosition.alt, 0, 0.5);
              lastFollowCameraMove = Date.now();
            } else {
              setStatus('No vehicle position available');
            }
          },

          // Enable vehicle following
          enableFollow: function() {
            followVehicle = true;
            setStatus('Vehicle follow enabled');
            post('debug:Follow enabled');
            if (lastPosition) {
              lookAtTerrain(lastPosition.lat, lastPosition.lng, lastPosition.alt, 0, 0.5);
              lastFollowCameraMove = Date.now();
            }
          },

          // Disable vehicle following
          disableFollow: function() {
            followVehicle = false;
            setStatus('Vehicle follow disabled');
            post('debug:Follow disabled');
          },

          // Reset to default view (Amman, Jordan terrain)
          resetView: function() {
            setStatus('Resetting to default view');
            post('debug:Reset view');
            
            // Fly to Amman, Jordan - clear terrain view
            viewer.camera.flyTo({
              destination: Cesium.Cartesian3.fromDegrees(
                defaultView.lng,
                defaultView.lat,
                defaultView.height
              ),
              orientation: {
                heading: Cesium.Math.toRadians(defaultView.heading),
                pitch: Cesium.Math.toRadians(defaultView.pitch),
                roll: 0
              },
              duration: 0.8
            });
            
            firstVehicleFix = true; // Allow next vehicle position to set view
          },

          // Clear track (placeholder for future implementation)
          clearTrack: function() {
            post('debug:Clear track');
          },

          // Debug: set view to specific location
          setView: function(lat, lng, height, heading, pitch) {
            viewer.camera.flyTo({
              destination: Cesium.Cartesian3.fromDegrees(
                lng || defaultView.lng,
                lat || defaultView.lat,
                height || defaultView.height
              ),
              orientation: {
                heading: Cesium.Math.toRadians(heading || 0),
                pitch: Cesium.Math.toRadians(pitch || -45),
                roll: 0
              },
              duration: 0.5
            });
          }
        };

        // Notify C# that 3D map is ready
        setTimeout(() => {
          post('ready');
          setStatus('3D Map initialized');
        }, 500);

      } catch (initError) {
        console.error('Initialization error:', initError);
        showError('Initialization Failed', initError.message || initError.toString());
        setStatus('Initialization failed');
      }

    })();
  </script>
</body>
</html>";
        }
    }
}
