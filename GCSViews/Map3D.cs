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
                LogDebug("Stage 1: WebView2 initialization starting");
                lblStatus.Text = "Initializing WebView2...";
                
                ConfigureWebView2LoaderPath();

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DIMP",
                    "WebView2",
                    "Map3D");

                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                LogDebug("WebView2 environment created");
                
                await webView.EnsureCoreWebView2Async(environment);
                LogDebug("CoreWebView2 initialized");
                
                lblStatus.Text = "Configuring WebView2...";

                // Configure WebView2 settings
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                
                // Enable console logging for debugging
                try
                {
                    webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                    webView.CoreWebView2.Settings.IsScriptEnabled = true;
                    webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                }
                catch { }

                // Add event handlers for diagnostics
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                
                // Handle navigation events
                webView.NavigationStarting += (s, e) => {
                    LogDebug("Navigation starting: " + e.Uri);
                    lblStatus.Text = "Loading HTML...";
                };
                
                webView.NavigationCompleted += (s, e) => {
                    LogDebug("Navigation completed. Success: " + e.IsSuccess + ", Error: " + e.WebErrorStatus);
                    if (!e.IsSuccess)
                    {
                        LogDebug("Navigation failed with error: " + e.WebErrorStatus);
                    }
                };
                
                // Handle console messages from JavaScript - removed AddHostObjectToScriptWithOrigins call
                // (not available in all WebView2 versions)

                LogDebug("Stage 2: Loading HTML content");
                lblStatus.Text = "Loading 3D map HTML...";
                
                // Navigate to the Cesium HTML content
                webView.NavigateToString(GetCesiumHtml());
                
                LogDebug("HTML content loaded into WebView2");
            }
            catch (Exception ex)
            {
                string errorMsg = ex.Message;
                LogDebug("CRITICAL ERROR in InitializeWebView: " + errorMsg);
                LogDebug("Stack: " + ex.StackTrace);
                
                lblStatus.Text = "WebView2 Error: " + errorMsg;
                lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                
                CustomMessageBox.Show(
                    "Failed to initialize 3D Map WebView2:\n\n" + errorMsg +
                    "\n\nPlease ensure Microsoft Edge WebView2 Runtime is installed.\n" +
                    "Download from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                    "3D Map - WebView2 Error");
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
            LogDebug("WebMessage received: " + message);

            if (message == "ready")
            {
                webReady = true;
                lblStatus.Text = "3D map ready";
                lblStatus.ForeColor = Color.FromArgb(0, 200, 100);
                LogDebug("Stage 8: 3D Map ready");
                ExecuteMapCommand(followVehicle ? "enableFollow" : "disableFollow");
                refreshTimer.Start();
                UpdateMapFromTelemetry();
            }
            else if (message.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                string errorDetails = message.Substring(6); // Remove "error:" prefix
                lblStatus.Text = "JS Error: " + errorDetails;
                lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                LogDebug("JavaScript Error: " + errorDetails);
            }
            else if (message.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            {
                string statusText = message.Substring(7); // Remove "status:" prefix
                lblStatus.Text = statusText;
                LogDebug("Status: " + statusText);
            }
            else if (message.StartsWith("debug:", StringComparison.OrdinalIgnoreCase))
            {
                string debugText = message.Substring(6); // Remove "debug:" prefix
                LogDebug("JS Debug: " + debugText);
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

        private static void LogDebug(string message)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map3d_debug.log");
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [Map3D] " + message + Environment.NewLine);
            }
            catch
            {
                // Ignore logging errors
            }
            Console.WriteLine("[Map3D] " + message);
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
            return @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta http-equiv=""X-UA-Compatible"" content=""IE=edge"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>DIMP 3D Map</title>
    
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body, #cesiumContainer {
            width: 100%; height: 100%; margin: 0; padding: 0; overflow: hidden;
            background: #87CEEB;
        }
        
        #loading {
            position: absolute; left: 0; top: 0; right: 0; bottom: 0;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            display: flex; flex-direction: column; justify-content: center; align-items: center;
            z-index: 9999; color: #fff; font-family: 'Segoe UI', Arial, sans-serif;
        }
        #loading.hidden { display: none; }
        
        #loading h2 { margin-bottom: 20px; color: #4a90d9; }
        #loading p { color: #aaa; font-size: 14px; margin: 5px 0; }
        #loading .error { color: #ff6b6b; display: none; }
        #loading .spinner {
            width: 40px; height: 40px; border: 3px solid rgba(255,255,255,0.2);
            border-top: 3px solid #4a90d9; border-radius: 50%;
            animation: spin 1s linear infinite; margin-bottom: 20px;
        }
        @keyframes spin { 0% { transform: rotate(0deg); } 100% { transform: rotate(360deg); } }
    </style>
</head>
<body>
    <div id=""loading"">
        <div class=""spinner""></div>
        <h2>DIMP 3D Map</h2>
        <p id=""status"">Loading...</p>
        <p class=""error"" id=""error""></p>
    </div>
    <div id=""cesiumContainer""></div>
    
    <!-- Load Cesium JS -->
    <script src=""https://cesium.com/downloads/cesiumjs/releases/1.104/Build/Cesium/Cesium.js""></script>
    
    <script>
    (function() {
        'use strict';
        
        function post(msg) {
            console.log('[Map3D]', msg);
            try {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(msg);
                }
            } catch(e) {}
        }
        
        function setStatus(s) {
            document.getElementById('status').textContent = s;
            post('status:' + s);
        }
        
        function showError(msg) {
            document.getElementById('error').style.display = 'block';
            document.getElementById('error').textContent = 'Error: ' + msg;
            post('error:' + msg);
        }
        
        function hideLoading() {
            document.getElementById('loading').classList.add('hidden');
        }
        
        // Default view: Amman, Jordan
        var DEFAULT_LNG = 35.9106;
        var DEFAULT_LAT = 31.9539;
        var DEFAULT_ALT = 3000; // meters above ground
        var PITCH = -60; // looking down at terrain
        
        // Camera position function
        function centerCamera(lat, lng, alt, heading, pitch, duration) {
            if (!window.cesiumViewer) return;
            
            var cameraAlt = (typeof alt === 'number' && isFinite(alt)) ? alt : DEFAULT_ALT;
            var cameraPitch = pitch !== undefined ? pitch : PITCH;
            var cameraHeading = heading !== undefined ? heading : 0;
            
            window.cesiumViewer.camera.setView({
                destination: Cesium.Cartesian3.fromDegrees(
                    (typeof lng === 'number' && isFinite(lng)) ? lng : DEFAULT_LNG,
                    (typeof lat === 'number' && isFinite(lat)) ? lat : DEFAULT_LAT,
                    cameraAlt
                ),
                orientation: {
                    heading: Cesium.Math.toRadians(cameraHeading),
                    pitch: Cesium.Math.toRadians(cameraPitch),
                    roll: 0
                }
            });
        }
        
        // Catch all errors
        window.onerror = function(msg, url, line) {
            setStatus('JS Error: ' + msg);
            showError(msg + ' (line ' + line + ')');
            return true;
        };
        
        window.onunhandledrejection = function(e) {
            setStatus('Promise error');
            showError(String(e.reason));
        };
        
        setStatus('Stage 1: Checking Cesium...');
        
        // Check if Cesium loaded
        if (typeof Cesium === 'undefined') {
            showError('Cesium.js failed to load. Check internet connection.');
            setStatus('Stage 1 FAILED: Cesium not loaded');
            return;
        }
        
        setStatus('Stage 2: Cesium loaded - creating imagery...');
        post('debug:Cesium library loaded');
        
        try {
            // Use UrlTemplateImageryProvider with OpenStreetMap
            var imageryProvider = new Cesium.UrlTemplateImageryProvider({
                url: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
                credit: '© OpenStreetMap contributors'
            });
            
            setStatus('Stage 3: Imagery provider created');
            post('debug:Imagery provider created');
            
            setStatus('Stage 4: Creating Cesium viewer...');
            
            // Create viewer
            window.cesiumViewer = new Cesium.Viewer('cesiumContainer', {
                imageryProvider: imageryProvider,
                terrainProvider: new Cesium.EllipsoidTerrainProvider(),
                baseLayerPicker: false,
                geocoder: false,
                homeButton: false,
                sceneModePicker: false,
                timeline: false,
                animation: false,
                fullscreenButton: false,
                navigationHelpButton: false,
                shouldAnimate: false
            });
            
            setStatus('Stage 5: Viewer created - configuring...');
            post('debug:Viewer created');
            
            // Configure scene for terrain view (no dark space effect)
            var scene = window.cesiumViewer.scene;
            
            // Disable dark lighting effects
            scene.globe.enableLighting = false;
            scene.globe.showGroundAtmosphere = false;
            scene.globe.depthTestAgainstTerrain = false;
            
            // Disable sky/atmosphere for daytime terrain look
            if (scene.skyBox) {
                scene.skyBox.show = false;
            }
            if (scene.skyAtmosphere) {
                scene.skyAtmosphere.show = false;
            }
            
            // Show sun
            if (scene.sun) {
                scene.sun.show = true;
            }
            
            setStatus('Stage 6: Centering camera...');
            post('debug:Scene configured');
            
            // Center camera on default location immediately
            centerCamera(DEFAULT_LAT, DEFAULT_LNG, DEFAULT_ALT, 0, PITCH, 0);
            
            setStatus('Stage 7: 3D Map ready');
            post('debug:Camera centered');
            hideLoading();
            
            // Expose API
            window.dimpMap = {
                setVehicle: function(lat, lng, alt, heading, speed) {
                    if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;
                    
                    var cameraAlt = Math.max(Number.isFinite(alt) ? alt : 0, 100) + 500;
                    centerCamera(lat, lng, cameraAlt, heading, PITCH, 0.5);
                    setStatus('Using vehicle position');
                },
                centerOnVehicle: function() {
                    // Will be called with vehicle coords when telemetry is available
                    setStatus('Centering on vehicle');
                },
                enableFollow: function() {
                    setStatus('Follow enabled');
                },
                disableFollow: function() {
                    setStatus('Follow disabled');
                },
                resetView: function() {
                    setStatus('Resetting view...');
                    centerCamera(DEFAULT_LAT, DEFAULT_LNG, DEFAULT_ALT, 0, PITCH, 0.5);
                    setStatus('3D Map ready');
                },
                clearTrack: function() {
                    setStatus('Track cleared');
                }
            };
            
            setTimeout(function() {
                post('ready');
            }, 1000);
            
        } catch(e) {
            showError(e.message || String(e));
            setStatus('Failed: ' + (e.message || String(e)));
            post('error:Viewer creation failed: ' + String(e));
        }
        
    })();
    </script>
</body>
</html>";
        }
    }
}
