using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        private ToolStripButton btnOfflineMaps;
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
        private bool mapUpdateInProgress;
        private bool embedded;
        private Control embeddedHost;
        private bool disposingMap;
        private bool webRecoveryInProgress;
        private Task initializationTask;

        internal const string MapResourceHost = "dimp3d.local";
        internal const string PreferredSourceSetting = "map3d_preferred_source";
        internal const string GoogleApiKeySetting = "map3d_google_api_key";
        private const string LastLatitudeSetting = "map3d_last_latitude";
        private const string LastLongitudeSetting = "map3d_last_longitude";
        private const string GoogleCesiumBaseUrl =
            "https://ajax.googleapis.com/ajax/libs/cesiumjs/1.105/Build/Cesium/";
        internal static readonly byte[] TransparentPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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
            initializationTask = InitializeWebViewAsync();
        }

        private void InitializeComponent()
        {
            Text = "DIMP - 3D Map";
            Size = new Size(1280, 800);
            MinimumSize = new Size(900, 600);
            BackColor = ModernUi.Canvas;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;

            toolbar = new ToolStrip();
            btnCenter = new ToolStripButton();
            btnFollow = new ToolStripButton();
            btnReset = new ToolStripButton();
            btnClearTrack = new ToolStripButton();
            btnOfflineMaps = new ToolStripButton();
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

            toolbar.BackColor = ModernUi.Surface;
            toolbar.ForeColor = ModernUi.TextPrimary;
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Dock = DockStyle.Top;
            toolbar.Renderer = new MissionPlanner.Controls.DIMPRenderer();

            ConfigureToolButton(btnCenter, "Center", "Center terrain view on vehicle");
            btnCenter.Image = ModernUi.CreateIcon("\uE707");
            btnCenter.Click += (sender, args) => ExecuteMapCommand("centerOnVehicle");

            ConfigureToolButton(btnFollow, "Follow: On", "Toggle vehicle follow");
            btnFollow.Image = ModernUi.CreateIcon("\uE72E");
            btnFollow.Click += (sender, args) =>
            {
                followVehicle = !followVehicle;
                btnFollow.Text = followVehicle ? "Follow: On" : "Follow: Off";
                ExecuteMapCommand(followVehicle ? "enableFollow" : "disableFollow");
            };

            ConfigureToolButton(btnReset, "Reset View", "Reset camera view");
            btnReset.Image = ModernUi.CreateIcon("\uE72C");
            btnReset.Click += (sender, args) => ExecuteMapCommand("resetView");

            ConfigureToolButton(btnClearTrack, "Clear Track", "Clear vehicle track");
            btnClearTrack.Image = ModernUi.CreateIcon("\uE74D");
            btnClearTrack.Click += (sender, args) => ExecuteMapCommand("clearTrack");

            ConfigureToolButton(btnOfflineMaps, "Offline Maps", "Import and manage offline 3D map data");
            btnOfflineMaps.Image = ModernUi.CreateIcon("\uE8B7");
            btnOfflineMaps.Click += (sender, args) => ShowOfflineMapManager();

            toolbar.Items.AddRange(new ToolStripItem[]
            {
                btnCenter,
                btnFollow,
                btnReset,
                btnClearTrack,
                new ToolStripSeparator(),
                btnOfflineMaps
            });

            webView.Dock = DockStyle.Fill;
            webView.BackColor = Color.Black;
            webView.DefaultBackgroundColor = Color.Black;

            statusStrip.BackColor = ModernUi.Surface;
            statusStrip.ForeColor = ModernUi.TextPrimary;
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
            lblStatus.ForeColor = ModernUi.Warning;
            lblLat.Text = "Lat: --";
            lblLng.Text = "Lng: --";
            lblAlt.Text = "Alt: --";
            lblSpeed.Text = "Spd: --";
            lblHeading.Text = "HDG: --";

            refreshTimer.Interval = 50;
            refreshTimer.Tick += RefreshTimer_Tick;

            Controls.Add(webView);

            FormClosing += Map3D_FormClosing;
            Shown += (sender, args) => ResizeMapToWindow();
            Resize += (sender, args) => ResizeMapToWindow();
            VisibleChanged += Map3D_VisibleChanged;

            ModernUi.Apply(this);

            ResumeLayout(false);
            PerformLayout();
        }

        private static void ConfigureToolButton(ToolStripButton button, string text, string tooltip)
        {
            button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            button.Text = text;
            button.ToolTipText = tooltip;
            button.ForeColor = ModernUi.TextPrimary;
            button.Margin = new Padding(3, 2, 3, 2);
            button.Padding = new Padding(8, 3, 8, 3);
        }

        private void ShowOfflineMapManager()
        {
            LogDebug("Opening offline 3D map package manager");
            try
            {
                using (var manager = new Map3DOfflinePackageManagerForm())
                {
                    Form owner = embeddedHost?.FindForm();
                    LogDebug("Offline package manager constructed");
                    if (owner != null && !owner.IsDisposed)
                    {
                        manager.ShowDialog(owner);
                    }
                    else
                    {
                        manager.ShowDialog(this);
                    }
                }

                LogDebug("Offline package manager closed; reloading map content");
                ReloadMapContent();
            }
            catch (Exception ex)
            {
                LogDebug("Offline package manager error: " + ex);
                CustomMessageBox.Show(
                    "Unable to open the offline map manager:\n\n" + ex.Message,
                    "Offline 3D Maps");
            }
        }

        internal static bool ShouldUseGoogle3D(string preferredSource, string apiKey)
        {
            return string.Equals(preferredSource, "google", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(apiKey);
        }

        private static string GetGoogleApiKey()
        {
            string key = Settings.Instance.GetString(GoogleApiKeySetting, string.Empty);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = Environment.GetEnvironmentVariable("DIMP_GOOGLE_MAPS_API_KEY") ?? string.Empty;
            }

            return key.Trim();
        }

        private static string GetCachedMapProviderName()
        {
            string cacheRoot = Path.Combine(
                Settings.GetDataDirectory(),
                "gmapcache",
                "TileDBv3",
                "en");
            string activeProvider = null;

            try
            {
                activeProvider = FlightData.mymap?.MapProvider?.Name;
            }
            catch
            {
            }

            string[] candidates =
            {
                activeProvider,
                Settings.Instance.GetString("MapType", string.Empty),
                "GoogleSatelliteMap",
                "BingSatelliteMap"
            };
            foreach (string candidate in candidates)
            {
                if (IsSafePathSegment(candidate) && Directory.Exists(Path.Combine(cacheRoot, candidate)))
                {
                    return candidate;
                }
            }

            try
            {
                if (Directory.Exists(cacheRoot))
                {
                    foreach (string directory in Directory.GetDirectories(cacheRoot))
                    {
                        string name = Path.GetFileName(directory);
                        if (IsSafePathSegment(name))
                        {
                            return name;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool IsSafePathSegment(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value != "." && value != ".." &&
                   value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                webReady = false;
                UpdateRefreshState();
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
                if (IsMapUnavailable())
                {
                    return;
                }

                LogDebug("WebView2 environment created");
                
                await webView.EnsureCoreWebView2Async(environment);
                if (IsMapUnavailable())
                {
                    return;
                }

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
                webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
                ConfigureLocalMapResources();

                // Handle navigation events
                webView.NavigationStarting += WebView_NavigationStarting;
                webView.NavigationCompleted += WebView_NavigationCompleted;
                
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
                if (IsMapUnavailable())
                {
                    return;
                }

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

        private bool IsMapUnavailable()
        {
            return disposingMap || IsDisposed || Disposing || webView == null || webView.IsDisposed;
        }

        private void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            LogDebug("Navigation starting: " + e.Uri);
            if (!disposingMap)
            {
                lblStatus.Text = "Loading HTML...";
            }
        }

        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            LogDebug("Navigation completed. Success: " + e.IsSuccess + ", Error: " + e.WebErrorStatus);
            if (!e.IsSuccess)
            {
                LogDebug("Navigation failed with error: " + e.WebErrorStatus);
                return;
            }

            await Task.Delay(100);
            await ExecuteScriptSafeAsync(@"
                if (window.cesiumViewer) {
                    window.cesiumViewer.resize();
                    window.cesiumViewer.scene.requestRender();
                }
            ", "navigation resize");
        }

        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            if (disposingMap)
            {
                return;
            }

            LogDebug("WebView2 process failed: " + e.ProcessFailedKind);
            webReady = false;
            mapUpdateInProgress = false;
            lblStatus.Text = "3D map renderer restarting...";
            lblStatus.ForeColor = ModernUi.Warning;
            UpdateRefreshState();
            _ = RecoverWebViewAsync();
        }

        private async Task RecoverWebViewAsync()
        {
            if (webRecoveryInProgress || disposingMap)
            {
                return;
            }

            webRecoveryInProgress = true;
            try
            {
                await Task.Delay(750);
                if (IsMapUnavailable())
                {
                    return;
                }

                Control parent = webView.Parent;
                DetachWebViewEvents();
                parent?.Controls.Remove(webView);
                webView.Dispose();

                webView = new WebView2
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    DefaultBackgroundColor = Color.Black
                };

                Control target = embedded && embeddedHost != null && !embeddedHost.IsDisposed
                    ? embeddedHost
                    : (parent != null && !parent.IsDisposed ? parent : this);
                target.Controls.Add(webView);
                webView.BringToFront();

                initializationTask = InitializeWebViewAsync();
                await initializationTask;
            }
            catch (Exception ex)
            {
                if (!disposingMap)
                {
                    LogDebug("WebView2 recovery failed: " + ex);
                    lblStatus.Text = "3D map renderer failed";
                    lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                }
            }
            finally
            {
                webRecoveryInProgress = false;
            }
        }

        private void DetachWebViewEvents()
        {
            if (webView == null)
            {
                return;
            }

            webView.NavigationStarting -= WebView_NavigationStarting;
            webView.NavigationCompleted -= WebView_NavigationCompleted;

            try
            {
                CoreWebView2 core = webView.CoreWebView2;
                if (core != null)
                {
                    core.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    core.WebResourceRequested -= CoreWebView2_WebResourceRequested;
                    core.ProcessFailed -= CoreWebView2_ProcessFailed;
                }
            }
            catch (Exception ex)
            {
                LogDebug("WebView2 event cleanup error: " + ex.Message);
            }
        }

        private async Task ExecuteScriptSafeAsync(string script, string operation)
        {
            if (!webReady || IsMapUnavailable() || webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                if (!disposingMap)
                {
                    LogDebug("JavaScript " + operation + " error: " + ex.Message);
                }
            }
        }

        internal static bool HasVisibleInstance
        {
            get { return _instance != null && !_instance.IsDisposed && _instance.Visible; }
        }

        private void ConfigureLocalMapResources()
        {
            try
            {
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    "https://" + MapResourceHost + "/*",
                    CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
                LogDebug("Local 3D map resource host configured: " + MapResourceHost);
            }
            catch (Exception ex)
            {
                LogDebug("Local 3D map resource host error: " + ex.Message);
            }
        }

        internal sealed class DynamicMapResource
        {
            internal byte[] Contents { get; set; }
            internal string ContentType { get; set; }
            internal bool IsImagery { get; set; }
        }

        internal static bool IsDynamicMapResourcePath(string absolutePath)
        {
            string path = absolutePath ?? string.Empty;
            return path.StartsWith("/raster/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/dem/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/xyz/", StringComparison.OrdinalIgnoreCase);
        }

        internal static DynamicMapResource GetDynamicMapResource(string absolutePath)
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(absolutePath ?? string.Empty)
                    .Replace('\\', '/')
                    .Trim('/');
            }
            catch (UriFormatException)
            {
                return null;
            }

            string[] segments = decoded.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 5 || segments.Any(segment => segment == "." || segment == ".."))
            {
                return null;
            }

            string route = segments[0].ToLowerInvariant();
            string packageId = segments[1];
            int level;
            int x;
            int y;
            string yText = Path.GetFileNameWithoutExtension(segments[4]);
            if (!int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out level) ||
                !int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out x) ||
                !int.TryParse(yText, NumberStyles.None, CultureInfo.InvariantCulture, out y))
            {
                return null;
            }

            Map3DOfflinePackage package = Map3DOfflinePackageCatalog.GetPackage(packageId);
            if (package == null)
            {
                return null;
            }

            if (route == "raster" && package.Kind == Map3DPackageKinds.RasterImagery)
            {
                return new DynamicMapResource
                {
                    Contents = Map3DRasterTileService.GetImageryTile(package, level, x, y),
                    ContentType = "image/png",
                    IsImagery = true
                };
            }
            if (route == "dem" && package.Kind == Map3DPackageKinds.RasterTerrain)
            {
                return new DynamicMapResource
                {
                    Contents = Map3DRasterTileService.GetTerrainTile(package, level, x, y),
                    ContentType = "application/octet-stream"
                };
            }
            if (route == "xyz" && package.Kind == Map3DPackageKinds.XyzImagery)
            {
                string tilePath = Map3DOfflinePackageCatalog.ResolveXyzTile(package, level, x, y);
                return new DynamicMapResource
                {
                    Contents = string.IsNullOrEmpty(tilePath) ? null : File.ReadAllBytes(tilePath),
                    ContentType = string.IsNullOrEmpty(tilePath) ? "image/png" : GetContentType(tilePath),
                    IsImagery = true
                };
            }

            return null;
        }

        private async void HandleDynamicMapResourceAsync(
            CoreWebView2WebResourceRequestedEventArgs e,
            CoreWebView2Deferral deferral)
        {
            try
            {
                Uri uri = new Uri(e.Request.Uri);
                DynamicMapResource resource = await Task.Run(() => GetDynamicMapResource(uri.AbsolutePath));
                if (IsMapUnavailable() || webView.CoreWebView2 == null)
                {
                    return;
                }

                if (resource != null && resource.Contents != null && resource.Contents.Length > 0)
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(resource.Contents, false),
                        200,
                        "OK",
                        "Content-Type: " + resource.ContentType +
                        "\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400");
                }
                else if (resource != null && resource.IsImagery)
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(TransparentPng, false),
                        200,
                        "OK",
                        "Content-Type: image/png\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=300");
                }
                else
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(new byte[0]),
                        404,
                        "Not Found",
                        "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
                }
            }
            catch (Exception ex)
            {
                LogDebug("Dynamic offline map resource error: " + ex.Message);
                if (!IsMapUnavailable() && webView.CoreWebView2 != null)
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(new byte[0]),
                        500,
                        "Error",
                        "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void CoreWebView2_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                Uri uri = new Uri(e.Request.Uri);

                if (!string.Equals(uri.Host, MapResourceHost, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (IsDynamicMapResourcePath(uri.AbsolutePath))
                {
                    CoreWebView2Deferral deferral = e.GetDeferral();
                    HandleDynamicMapResourceAsync(e, deferral);
                    return;
                }

                string filePath = ResolveLocalMapResource(uri.AbsolutePath);

                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    string headers = "Content-Type: " + GetContentType(filePath) + "\r\nAccess-Control-Allow-Origin: *";
                    Stream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
                    return;
                }

                if (uri.AbsolutePath.StartsWith("/gmap/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(TransparentPng, false),
                        200,
                        "OK",
                        "Content-Type: image/png\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=300");
                    return;
                }

                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(new byte[0]),
                    404,
                    "Not Found",
                    "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
            }
            catch (Exception ex)
            {
                LogDebug("Local 3D map resource request error: " + ex.Message);
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(new byte[0]),
                    500,
                    "Error",
                    "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *");
            }
        }

        internal static string ResolveLocalMapResource(string absolutePath)
        {
            string path;
            try
            {
                path = Uri.UnescapeDataString(absolutePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
                string secondPass = Uri.UnescapeDataString(path);
                if (!string.Equals(path, secondPass, StringComparison.Ordinal))
                {
                    path = secondPass.Replace('\\', '/');
                }
            }
            catch (UriFormatException)
            {
                return null;
            }

            int separator = path.IndexOf('/');

            if (separator <= 0)
            {
                return null;
            }

            string bucket = path.Substring(0, separator);
            string relativePath = path.Substring(separator + 1);

            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
                relativePath.IndexOf(':') >= 0)
            {
                return null;
            }

            string[] segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            foreach (string segment in segments)
            {
                if (segment == "." || segment == "..")
                {
                    return null;
                }
            }

            relativePath = string.Join("/", segments);

            string runningDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string sourceDirectory = Path.GetFullPath(Path.Combine(runningDirectory, "..", "..", ".."));

            if (bucket.Equals("cesium", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveExistingResource(relativePath,
                    Path.Combine(runningDirectory, "Cesium"),
                    Path.Combine(sourceDirectory, "ExtLibs", "wasm", "wwwroot", "Cesium"));
            }

            if (bucket.Equals("srtm", StringComparison.OrdinalIgnoreCase))
            {
                string terrain = ResolveExistingResource(relativePath,
                    Path.Combine(Settings.GetDataDirectory(), "srtm"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mission Planner", "srtm"));
                return File.Exists(terrain)
                    ? terrain
                    : Map3DOfflinePackageCatalog.ResolveHgtFile(relativePath);
            }

            if (bucket.Equals("buildings", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveExistingResource(relativePath,
                    Path.Combine(Settings.GetDataDirectory(), "map3d", "buildings"),
                    Path.Combine(sourceDirectory, "map3d", "buildings"));
            }

            if (bucket.Equals("buildings3d", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveExistingResource(relativePath,
                    Path.Combine(Settings.GetDataDirectory(), "map3d", "buildings3d-v2-textured"),
                    Path.Combine(sourceDirectory, "map3d", "buildings3d"),
                    Path.Combine(Settings.GetDataDirectory(), "map3d", "buildings3d"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Mission Planner", "map3d", "buildings3d"));
            }

            if (bucket.Equals("vehicles", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveExistingResource(relativePath,
                    Path.Combine(runningDirectory, "Map3D", "vehicles"),
                    Path.Combine(sourceDirectory, "map3d", "vehicles"));
            }

            if (bucket.Equals("gmap", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveExistingResource(relativePath,
                    Path.Combine(Settings.GetDataDirectory(), "gmapcache", "TileDBv3", "en"));
            }

            if (bucket.Equals("imports", StringComparison.OrdinalIgnoreCase))
            {
                int packageSeparator = relativePath.IndexOf('/');
                if (packageSeparator <= 0 || packageSeparator >= relativePath.Length - 1)
                {
                    return null;
                }
                string packageId = relativePath.Substring(0, packageSeparator);
                string packageRelativePath = relativePath.Substring(packageSeparator + 1);
                return Map3DOfflinePackageCatalog.ResolvePackageResource(
                    packageId,
                    packageRelativePath);
            }

            return null;
        }

        private static string ResolveExistingResource(string relativePath, params string[] roots)
        {
            foreach (string root in roots)
            {
                string path = ResolveRootedPath(root, relativePath);

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }

            return ResolveRootedPath(roots.Length > 0 ? roots[0] : string.Empty, relativePath);
        }

        private static string ResolveRootedPath(string root, string relativePath)
        {
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            string normalizedRoot = Path.GetFullPath(root);
            string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }

        internal static string GetContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".css":
                    return "text/css";
                case ".js":
                    return "application/javascript";
                case ".json":
                    return "application/json";
                case ".wasm":
                    return "application/wasm";
                case ".png":
                    return "image/png";
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".webp":
                    return "image/webp";
                case ".svg":
                    return "image/svg+xml";
                case ".kml":
                    return "application/vnd.google-earth.kml+xml";
                case ".kmz":
                    return "application/vnd.google-earth.kmz";
                case ".hgt":
                case ".b3dm":
                case ".pnts":
                case ".i3dm":
                case ".cmpt":
                case ".terrain":
                case ".ktx2":
                case ".f32":
                    return "application/octet-stream";
                case ".glb":
                    return "model/gltf-binary";
                default:
                    return "application/octet-stream";
            }
        }

        internal static void ConfigureWebView2LoaderPath()
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
            string message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (Exception ex)
            {
                LogDebug("Unable to read WebView2 message: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            LogDebug("WebMessage received: " + message);

            if (message == "ready")
            {
                webReady = true;
                lblStatus.Text = "3D map ready";
                lblStatus.ForeColor = Color.FromArgb(0, 200, 100);
                LogDebug("Stage 8: 3D Map ready");
                ExecuteMapCommand(followVehicle ? "enableFollow" : "disableFollow");
                UpdateRefreshState();
                _ = UpdateMapFromTelemetryAsync();
            }
            else if (message.StartsWith("action:source:", StringComparison.OrdinalIgnoreCase))
            {
                HandleSourceRequest(message.Substring("action:source:".Length));
            }
            else if (message.Equals("action:configure-google", StringComparison.OrdinalIgnoreCase))
            {
                if (ShowGoogleApiKeyDialog())
                {
                    Settings.Instance[PreferredSourceSetting] = "google";
                    SaveSettingsQuietly();
                    ReloadMapContent();
                }
            }
            else if (message.Equals("action:manage-offline-maps", StringComparison.OrdinalIgnoreCase))
            {
                ShowOfflineMapManager();
            }
            else if (message.StartsWith("action:prepare-offline:", StringComparison.OrdinalIgnoreCase))
            {
                double latitude;
                double longitude;
                if (TryParseCoordinates(message.Substring("action:prepare-offline:".Length), out latitude, out longitude))
                {
                    _ = PrepareOfflineAreaAsync(latitude, longitude);
                }
            }
            else if (message.StartsWith("visited:", StringComparison.OrdinalIgnoreCase))
            {
                double latitude;
                double longitude;
                if (TryParseCoordinates(message.Substring("visited:".Length), out latitude, out longitude))
                {
                    RememberVisitedLocation(latitude, longitude);
                }
            }
            else if (message.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
            {
                string source = message.Substring("source:".Length);
                LogDebug("Active map source: " + source);
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

        private void HandleSourceRequest(string source)
        {
            source = (source ?? string.Empty).Trim().ToLowerInvariant();
            if (source == "google")
            {
                if (string.IsNullOrWhiteSpace(GetGoogleApiKey()) && !ShowGoogleApiKeyDialog())
                {
                    return;
                }

                Settings.Instance[PreferredSourceSetting] = "google";
            }
            else if (source == "offline")
            {
                Settings.Instance[PreferredSourceSetting] = "offline";
            }
            else
            {
                return;
            }

            SaveSettingsQuietly();
            ReloadMapContent();
        }

        private void ReloadMapContent()
        {
            if (IsMapUnavailable() || webView.CoreWebView2 == null)
            {
                return;
            }

            webReady = false;
            mapUpdateInProgress = false;
            lblStatus.Text = "Changing 3D map source...";
            lblStatus.ForeColor = Color.FromArgb(255, 210, 120);
            UpdateRefreshState();
            webView.NavigateToString(GetCesiumHtml());
        }

        private bool ShowGoogleApiKeyDialog()
        {
            using (Form dialog = new Form())
            using (TextBox apiKey = new TextBox())
            using (CheckBox showKey = new CheckBox())
            using (Button save = new Button())
            using (Button cancel = new Button())
            {
                dialog.Text = "Google 3D Map";
                dialog.ClientSize = new Size(480, 174);
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.BackColor = Color.FromArgb(28, 28, 40);
                dialog.ForeColor = Color.White;

                Label instructions = new Label
                {
                    AutoSize = false,
                    Location = new Point(18, 16),
                    Size = new Size(444, 38),
                    Text = "Enter a Google Maps Platform key restricted to the Map Tiles API.",
                    ForeColor = Color.FromArgb(225, 225, 235)
                };
                apiKey.Location = new Point(18, 58);
                apiKey.Size = new Size(444, 24);
                apiKey.Text = GetGoogleApiKey();
                apiKey.UseSystemPasswordChar = true;
                showKey.Location = new Point(18, 91);
                showKey.Size = new Size(100, 24);
                showKey.Text = "Show key";
                showKey.CheckedChanged += (sender, args) =>
                    apiKey.UseSystemPasswordChar = !showKey.Checked;

                save.Location = new Point(278, 126);
                save.Size = new Size(88, 30);
                save.Text = "Save";
                save.DialogResult = DialogResult.OK;
                cancel.Location = new Point(374, 126);
                cancel.Size = new Size(88, 30);
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                dialog.AcceptButton = save;
                dialog.CancelButton = cancel;
                dialog.Controls.AddRange(new Control[] { instructions, apiKey, showKey, save, cancel });

                Form owner = embeddedHost?.FindForm();
                DialogResult result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog(this);
                if (result != DialogResult.OK || string.IsNullOrWhiteSpace(apiKey.Text))
                {
                    return false;
                }

                Settings.Instance[GoogleApiKeySetting] = apiKey.Text.Trim();
                SaveSettingsQuietly();
                return true;
            }
        }

        private async Task PrepareOfflineAreaAsync(double latitude, double longitude)
        {
            string radiusText = "5";
            if (InputBox.Show(
                    "Offline Area",
                    "Radius in kilometers (0.5 to 100):",
                    ref radiusText) != DialogResult.OK)
            {
                return;
            }

            double radius;
            if (!double.TryParse(radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out radius) &&
                !double.TryParse(radiusText, out radius))
            {
                CustomMessageBox.Show("Enter a valid radius.", "Offline Area");
                return;
            }

            radius = Math.Max(0.5, Math.Min(100, radius));
            await ExecuteScriptSafeAsync(
                "window.dimpMap && window.dimpMap.setOfflineStatus('Preparing offline area...');",
                "offline preparation status");

            try
            {
                string provider = GetCachedMapProviderName();
                Map3DOfflinePreparationResult result = await Task.Run(() =>
                    Map3DOfflineRegionCache.PrepareArea(latitude, longitude, radius, provider));
                string status = result.ToStatusText();
                await ExecuteScriptSafeAsync(
                    "window.dimpMap && window.dimpMap.setOfflineStatus(" +
                    JsonConvert.SerializeObject(status) + ");",
                    "offline preparation result");
                lblStatus.Text = status;
                lblStatus.ForeColor = Color.FromArgb(0, 200, 100);
            }
            catch (Exception ex)
            {
                LogDebug("Offline area preparation error: " + ex);
                await ExecuteScriptSafeAsync(
                    "window.dimpMap && window.dimpMap.setOfflineStatus('Offline preparation failed');",
                    "offline preparation error");
                CustomMessageBox.Show("Unable to prepare the offline area:\n\n" + ex.Message, "Offline Area");
            }
        }

        private static bool TryParseCoordinates(string value, out double latitude, out double longitude)
        {
            latitude = 0;
            longitude = 0;
            string[] parts = (value ?? string.Empty).Split(':');
            return parts.Length >= 2 &&
                   double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude) &&
                   latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
        }

        private static void RememberVisitedLocation(double latitude, double longitude)
        {
            Settings.Instance[LastLatitudeSetting] = latitude.ToString("R", CultureInfo.InvariantCulture);
            Settings.Instance[LastLongitudeSetting] = longitude.ToString("R", CultureInfo.InvariantCulture);
            _ = Task.Run(() =>
            {
                try
                {
                    Map3DOfflineRegionCache.RememberVisitedLocation(latitude, longitude);
                }
                catch (Exception ex)
                {
                    LogDebug("Unable to remember offline terrain location: " + ex.Message);
                }
            });
        }

        private static void SaveSettingsQuietly()
        {
            try
            {
                Settings.Instance.Save();
            }
            catch (Exception ex)
            {
                LogDebug("Unable to save 3D map settings: " + ex.Message);
            }
        }

        private void PositionOnSecondaryMonitor()
        {
            try
            {
                Screen[] screens = Screen.AllScreens;

                Screen target = Screen.PrimaryScreen;

                if (screens.Length > 1)
                {
                    target = screens[1];
                }

                StartPosition = FormStartPosition.Manual;
                Bounds = target.WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Map3D PositionOnSecondaryMonitor Error: " + ex.Message);
                StartPosition = FormStartPosition.CenterScreen;
                WindowState = FormWindowState.Maximized;
            }
        }

        private static void LogDebug(string message)
        {
            try
            {
                string logDirectory = Settings.GetDataDirectory();
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(logDirectory, "map3d_debug.log");
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [Map3D] " + message + Environment.NewLine);
            }
            catch
            {
                // Ignore logging errors
            }
            Console.WriteLine("[Map3D] " + message);
        }

        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            await UpdateMapFromTelemetryAsync();
        }

        private async Task UpdateMapFromTelemetryAsync()
        {
            try
            {
                if (MainV2.comPort != null &&
                    MainV2.comPort.MAV != null &&
                    MainV2.comPort.MAV.cs != null &&
                    VehicleTelemetryValidation.HasUsablePosition(MainV2.comPort.MAV.cs))
                {
                    double lat = MainV2.comPort.MAV.cs.lat;
                    double lng = MainV2.comPort.MAV.cs.lng;
                    double relativeAlt = MainV2.comPort.MAV.cs.alt;
                    double absoluteAlt = MainV2.comPort.MAV.cs.altasl;
                    float yaw = MainV2.comPort.MAV.cs.yaw;
                    float pitch = MainV2.comPort.MAV.cs.pitch;
                    float roll = MainV2.comPort.MAV.cs.roll;
                    float groundCourse = MainV2.comPort.MAV.cs.groundcourse;
                    float groundSpeed =
                        VehicleTelemetryValidation.GetVisualGroundSpeed(MainV2.comPort.MAV.cs);
                    float climbRate =
                        VehicleTelemetryValidation.GetVisualClimbRate(MainV2.comPort.MAV.cs);
                    float heading = float.IsNaN(yaw) || float.IsInfinity(yaw) ? groundCourse : yaw;
                    string vehicleModel = GetVehicleModelKind(
                        MainV2.comPort.MAV.aptype,
                        MainV2.comPort.MAV.cs.firmware);

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
                    lblHeading.Text = "HDG: " + heading.ToString("F0", CultureInfo.InvariantCulture);

                    if (webReady && !mapUpdateInProgress && webView.CoreWebView2 != null)
                    {
                        string script = string.Format(
                            CultureInfo.InvariantCulture,
                            "window.dimpMap && window.dimpMap.setVehicle({0},{1},{2},{3},{4},{5},{6},{7},'{8}',{9},{10});",
                            lat,
                            lng,
                            absoluteAlt,
                            heading,
                            groundSpeed,
                            relativeAlt,
                            pitch,
                            roll,
                            vehicleModel,
                            groundCourse,
                            climbRate);

                        mapUpdateInProgress = true;
                        try
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync(script);
                        }
                        finally
                        {
                            mapUpdateInProgress = false;
                        }
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
            if (!webReady || IsMapUnavailable() || webView.CoreWebView2 == null)
            {
                return;
            }

            if (command == "centerOnVehicle" && !telemetryValid)
            {
                return;
            }

            _ = ExecuteScriptSafeAsync(
                "window.dimpMap && window.dimpMap." + command + "();",
                command);
        }

        private void ResizeMapToWindow()
        {
            if (webView == null || webView.IsDisposed)
            {
                return;
            }

            webView.Dock = DockStyle.Fill;

            if (webReady && webView.CoreWebView2 != null)
            {
                _ = ExecuteScriptSafeAsync(
                    "window.dimpMap && window.dimpMap.resize && window.dimpMap.resize();",
                    "resize");
            }
        }

        internal static bool ShouldRunRefresh(
            bool ready,
            bool isEmbedded,
            bool standaloneVisible,
            bool embeddedVisible)
        {
            return ready && (isEmbedded ? embeddedVisible : standaloneVisible);
        }

        private void UpdateRefreshState()
        {
            if (refreshTimer == null || disposingMap)
            {
                return;
            }

            bool embeddedVisible = embedded && embeddedHost != null && !embeddedHost.IsDisposed &&
                                   embeddedHost.Visible &&
                                   (embeddedHost.FindForm() == null || embeddedHost.FindForm().Visible);
            bool shouldRun = ShouldRunRefresh(webReady, embedded, Visible, embeddedVisible);

            if (shouldRun && !refreshTimer.Enabled)
            {
                refreshTimer.Start();
            }
            else if (!shouldRun && refreshTimer.Enabled)
            {
                refreshTimer.Stop();
            }
        }

        private void Map3D_VisibleChanged(object sender, EventArgs e)
        {
            UpdateRefreshState();
        }

        private void EmbeddedHost_VisibleChanged(object sender, EventArgs e)
        {
            UpdateRefreshState();
        }

        internal static string GetVehicleModelKind(
            MAVLink.MAV_TYPE vehicleType,
            ArduPilot.Firmwares firmware)
        {
            switch (vehicleType)
            {
                case MAVLink.MAV_TYPE.FIXED_WING:
                case MAVLink.MAV_TYPE.VTOL_DUOROTOR:
                case MAVLink.MAV_TYPE.VTOL_QUADROTOR:
                case MAVLink.MAV_TYPE.VTOL_TILTROTOR:
                case MAVLink.MAV_TYPE.VTOL_RESERVED2:
                case MAVLink.MAV_TYPE.VTOL_RESERVED3:
                case MAVLink.MAV_TYPE.VTOL_RESERVED4:
                case MAVLink.MAV_TYPE.VTOL_RESERVED5:
                    return "fixedwing";

                case MAVLink.MAV_TYPE.HEXAROTOR:
                    return "hexacopter";

                case MAVLink.MAV_TYPE.HELICOPTER:
                case MAVLink.MAV_TYPE.COAXIAL:
                    return "helicopter";

                case MAVLink.MAV_TYPE.QUADROTOR:
                case MAVLink.MAV_TYPE.OCTOROTOR:
                case MAVLink.MAV_TYPE.TRICOPTER:
                    return "quadcopter";
            }

            return firmware == ArduPilot.Firmwares.ArduPlane ? "fixedwing" : "quadcopter";
        }

        internal void AttachEmbedded(Control host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (webView == null || webView.IsDisposed)
            {
                return;
            }

            if (Visible)
            {
                Hide();
            }

            if (embeddedHost != null)
            {
                embeddedHost.VisibleChanged -= EmbeddedHost_VisibleChanged;
            }

            embedded = true;
            embeddedHost = host;
            embeddedHost.VisibleChanged += EmbeddedHost_VisibleChanged;
            host.Controls.Add(webView);
            webView.Dock = DockStyle.Fill;
            webView.Visible = true;
            webView.BringToFront();

            UpdateRefreshState();
            ResizeMapToWindow();
        }

        internal void DetachEmbedded()
        {
            if (!embedded || webView == null || webView.IsDisposed)
            {
                if (embeddedHost != null)
                {
                    embeddedHost.VisibleChanged -= EmbeddedHost_VisibleChanged;
                }

                embedded = false;
                embeddedHost = null;
                UpdateRefreshState();
                return;
            }

            Control previousHost = embeddedHost;
            if (previousHost != null)
            {
                previousHost.VisibleChanged -= EmbeddedHost_VisibleChanged;
            }

            embedded = false;
            embeddedHost = null;
            Controls.Add(webView);
            webView.Dock = DockStyle.Fill;
            webView.Visible = true;
            UpdateRefreshState();
            ResizeMapToWindow();
        }

        internal void ResizeEmbedded()
        {
            if (embedded)
            {
                ResizeMapToWindow();
            }
        }

        private void Map3D_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing)
            {
                return;
            }

            e.Cancel = true;
            Hide();
            UpdateRefreshState();
        }

        public new void Activate()
        {
            if (embedded)
            {
                Form hostForm = embeddedHost == null ? null : embeddedHost.FindForm();
                hostForm?.BringToFront();
                hostForm?.Focus();
                ResizeMapToWindow();
                UpdateRefreshState();
                return;
            }

            if (!Visible)
            {
                Show();
            }

            BringToFront();
            Focus();
            ResizeMapToWindow();

            UpdateRefreshState();
        }

        public new void Deactivate()
        {
            Hide();
            UpdateRefreshState();
        }

        public static void ShowMap()
        {
            Instance.Activate();
        }

        public static void HideMap()
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Deactivate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposingMap = true;
                VisibleChanged -= Map3D_VisibleChanged;
                if (embeddedHost != null)
                {
                    embeddedHost.VisibleChanged -= EmbeddedHost_VisibleChanged;
                }

                if (refreshTimer != null)
                {
                    refreshTimer.Stop();
                    refreshTimer.Tick -= RefreshTimer_Tick;
                    refreshTimer.Dispose();
                }

                if (webView != null)
                {
                    DetachWebViewEvents();
                    webView.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        internal static string GetCesiumHtml()
        {
            string apiKey = GetGoogleApiKey();
            string preferredSource = Settings.Instance.GetString(PreferredSourceSetting, "offline");
            bool useGoogle = ShouldUseGoogle3D(preferredSource, apiKey);
            double defaultLatitude = GetCoordinateSetting(LastLatitudeSetting, 31.9539);
            double defaultLongitude = GetCoordinateSetting(LastLongitudeSetting, 35.9106);

            try
            {
                if (!Settings.Instance.ContainsKey(LastLatitudeSetting) && FlightData.mymap != null)
                {
                    defaultLatitude = FlightData.mymap.Position.Lat;
                    defaultLongitude = FlightData.mymap.Position.Lng;
                }
            }
            catch
            {
            }

            return BuildCesiumHtml(
                useGoogle,
                useGoogle ? apiKey : string.Empty,
                !string.IsNullOrWhiteSpace(apiKey),
                defaultLatitude,
                defaultLongitude,
                GetCachedMapProviderName(),
                Map3DOfflinePackageCatalog.GetPackages(true));
        }

        internal static string BuildCesiumHtml(
            bool useGoogle,
            string googleApiKey,
            bool googleApiConfigured,
            double defaultLatitude,
            double defaultLongitude,
            string offlineMapProvider,
            IReadOnlyList<Map3DOfflinePackage> importedPackages = null)
        {
            string runtimeBase = useGoogle
                ? GoogleCesiumBaseUrl
                : "https://" + MapResourceHost + "/cesium/";
            string mapConfiguration = JsonConvert.SerializeObject(new
            {
                preferredSource = useGoogle ? "google" : "offline",
                googleApiKey = googleApiKey ?? string.Empty,
                googleApiConfigured,
                defaultLatitude,
                defaultLongitude,
                offlineMapProvider = IsSafePathSegment(offlineMapProvider) ? offlineMapProvider : string.Empty,
                importedMaps = BuildImportedMapConfiguration(importedPackages)
            });

            string html = @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta http-equiv=""X-UA-Compatible"" content=""IE=edge"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
    <title>DIMP 3D Map</title>
    <link href=""__CESIUM_WIDGETS_URL__"" rel=""stylesheet"">

    <style>
        * { margin: 0 !important; padding: 0 !important; box-sizing: border-box; }
        html, body {
            width: 100vw !important;
            height: 100vh !important;
            overflow: hidden !important;
            background: #000000 !important;
        }
        #cesiumContainer {
            width: 100vw !important;
            height: 100vh !important;
            position: fixed !important;
            top: 0 !important;
            left: 0 !important;
            right: 0 !important;
            bottom: 0 !important;
        }

        .cesium-viewer,
        .cesium-viewer-cesiumWidgetContainer,
        .cesium-widget,
        .cesium-widget canvas {
            position: absolute !important;
            top: 0 !important;
            left: 0 !important;
            right: 0 !important;
            bottom: 0 !important;
            width: 100% !important;
            height: 100% !important;
            min-width: 100% !important;
            min-height: 100% !important;
            display: block !important;
            overflow: hidden !important;
        }

        .cesium-viewer-bottom,
        .cesium-credit-logoContainer,
        .cesium-credit-textContainer {
            display: block !important;
        }

        .cesium-viewer-bottom {
            z-index: 10001 !important;
        }

        #loading {
            position: fixed;
            left: 0; top: 0;
            right: 0; bottom: 0;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            display: flex;
            flex-direction: column;
            justify-content: center;
            align-items: center;
            z-index: 99999;
            color: #fff;
            font-family: 'Segoe UI', Arial, sans-serif;
        }
        #loading.hidden { display: none !important; }

        #loading h2 { margin-bottom: 20px; color: #4a90d9; }
        #loading p { color: #aaa; font-size: 14px; margin: 5px 0; }
        #loading .error { color: #ff6b6b; display: none; }
        #loading .spinner {
            width: 40px; height: 40px;
            border: 3px solid rgba(255,255,255,0.2);
            border-top: 3px solid #4a90d9;
            border-radius: 50%;
            animation: spin 1s linear infinite;
            margin-bottom: 20px;
        }

        #mapControls {
            position: fixed;
            top: 14px;
            left: 14px;
            z-index: 10000;
            display: flex;
            gap: 8px;
            flex-wrap: wrap;
            max-width: calc(100vw - 28px);
            font-family: 'Segoe UI', Arial, sans-serif;
            pointer-events: none;
        }

        #lockButton,
        #sourceButton,
        #importButton,
        #offlineButton,
        #settingsButton {
            min-width: 104px;
            height: 38px;
            border: 1px solid rgba(255,255,255,0.32);
            border-radius: 6px;
            background: rgba(18, 22, 36, 0.88);
            color: #ffffff;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            font-size: 13px;
            font-weight: 700;
            letter-spacing: 0;
            cursor: pointer;
            box-shadow: 0 8px 24px rgba(0,0,0,0.38);
            pointer-events: auto;
        }

        #lockButton {
            min-width: 0;
            width: 48px;
            height: 48px;
            flex: 0 0 48px;
            padding: 0;
            font-family: 'Segoe MDL2 Assets', 'Segoe UI Symbol', sans-serif;
            font-size: 23px;
            font-weight: 400;
            line-height: 1;
            transition: filter 140ms ease, transform 140ms ease,
                        background-color 160ms ease, border-color 160ms ease;
        }

        #lockButton::before {
            display: block;
            line-height: 1;
        }

        #lockButton.locked::before {
            content: '\E72E';
        }

        #lockButton.unlocked::before {
            content: '\E785';
        }

        #lockButton:focus-visible {
            outline: 2px solid #ffffff;
            outline-offset: 2px;
        }

        #sourceButton {
            background: rgba(20, 82, 112, 0.94);
            border-color: rgba(116, 213, 255, 0.74);
        }

        #offlineButton {
            background: rgba(34, 92, 62, 0.94);
            border-color: rgba(113, 224, 162, 0.72);
        }

        #importButton {
            background: rgba(76, 57, 118, 0.94);
            border-color: rgba(186, 157, 255, 0.74);
        }

        #settingsButton {
            min-width: 76px;
            background: rgba(48, 50, 62, 0.94);
        }

        #sourceBadge {
            min-width: 118px;
            height: 38px;
            padding: 0 12px !important;
            border: 1px solid rgba(255,255,255,0.22);
            border-radius: 6px;
            background: rgba(14, 16, 25, 0.82);
            color: #e9edf6;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            font-size: 12px;
            font-weight: 600;
            pointer-events: none;
        }

        #lockButton.locked {
            background: rgba(18, 118, 214, 0.94);
            border-color: rgba(146, 206, 255, 0.9);
        }

        #lockButton.unlocked {
            background: rgba(36, 38, 48, 0.9);
            border-color: rgba(255,255,255,0.26);
        }

        #lockButton:hover,
        #sourceButton:hover,
        #importButton:hover,
        #offlineButton:hover,
        #settingsButton:hover {
            filter: brightness(1.13);
        }

        #lockButton:active,
        #sourceButton:active,
        #importButton:active,
        #offlineButton:active,
        #settingsButton:active {
            transform: translateY(1px);
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
    <div id=""mapControls"">
        <button id=""lockButton"" type=""button"" class=""locked"" aria-pressed=""true""
                aria-label=""Drone lock on"" title=""Drone locked. Orbit with mouse, arrows, or WASD""></button>
        <button id=""sourceButton"" type=""button"" title=""Switch 3D map source"">OFFLINE</button>
        <button id=""importButton"" type=""button"" title=""Import and manage offline 3D map packages"">IMPORT MAP</button>
        <button id=""offlineButton"" type=""button"" title=""Prepare this location for offline use"">SAVE OFFLINE</button>
        <button id=""settingsButton"" type=""button"" title=""Configure Google Map Tiles API key"">API KEY</button>
        <span id=""sourceBadge"">OFFLINE READY</span>
    </div>
    <div id=""cesiumContainer""></div>

    <script>
        window.DIMP_MAP_CONFIG = __MAP_CONFIG__;
        window.CESIUM_BASE_URL = '__CESIUM_BASE_URL__';
    </script>
    <script src=""__CESIUM_SCRIPT_URL__""></script>
    <script>
        if (typeof Cesium === 'undefined' && window.DIMP_MAP_CONFIG.preferredSource === 'google') {
            window.DIMP_CESIUM_FALLBACK = true;
            window.DIMP_MAP_CONFIG.preferredSource = 'offline';
            window.DIMP_MAP_CONFIG.googleApiKey = '';
            window.CESIUM_BASE_URL = 'https://dimp3d.local/cesium/';
            var localWidgets = document.createElement('link');
            localWidgets.rel = 'stylesheet';
            localWidgets.href = 'https://dimp3d.local/cesium/Widgets/widgets.css';
            document.head.appendChild(localWidgets);
            document.write('<script src=""https://dimp3d.local/cesium/Cesium.js""><\/script>');
        }
    </script>

    <script>
    (function() {
        'use strict';

        var MAP_CONFIG = window.DIMP_MAP_CONFIG || {};
        var IMPORTED_MAPS = Array.isArray(MAP_CONFIG.importedMaps)
            ? MAP_CONFIG.importedMaps
            : [];
        var configuredLatitude = Number(MAP_CONFIG.defaultLatitude);
        var configuredLongitude = Number(MAP_CONFIG.defaultLongitude);
        var DEFAULT_LNG = Number.isFinite(configuredLongitude) ? configuredLongitude : 35.9106;
        var DEFAULT_LAT = Number.isFinite(configuredLatitude) ? configuredLatitude : 31.9539;
        var DEFAULT_ALT = 3000;
        var PITCH = -60;
        var vehicleEntity = null;
        var vehicleModelType = null;
        var vehicleTrackEntity = null;
        var vehicleTrackPositions = [];
        var lastVehicle = null;
        var displayedVehicle = null;
        var fpvTarget = null;
        var displayedFpv = null;
        var animationFrameHandle = null;
        var lastAnimationTime = 0;
        var lastTrackTime = 0;
        var followVehicle = true;
        var followCameraInitialized = false;
        var fpvMode = false;
        var MAP_RESOURCE_BASE = 'https://dimp3d.local/';
        var SRTM_BASE_URL = MAP_RESOURCE_BASE + 'srtm/';
        var BUILDINGS_BASE_URL = MAP_RESOURCE_BASE + 'buildings/';
        var BUILDINGS_3D_BASE_URL = MAP_RESOURCE_BASE + 'buildings3d/';
        var VEHICLES_BASE_URL = MAP_RESOURCE_BASE + 'vehicles/';
        var TERRAIN_TILE_SIZE = 65;
        var MAX_SRTM_CACHE_TILES = 8;
        var SRTM_MEMORY_SAMPLES = 1801;
        var MIN_SRTM_TERRAIN_LEVEL = 7;
        var MAX_SRTM_TERRAIN_LEVEL = 13;
        var MAX_SRTM_SOURCE_TILES_PER_REQUEST = 4;
        var BUILDING_TILE_SCALE = 10;
        var BUILDING_LOAD_RADIUS_SUBTILES = 0;
        var MAX_BUILDINGS_PER_TILE = 400;
        var MAX_LOADED_BUILDING_TILES = 1;
        var BUILDING_BATCH_SIZE = 25;
        var BUILDING_MAX_CAMERA_HEIGHT = 6000;
        var EARTH_RADIUS_METERS = 6378137;
        var MAP_PRESENTATION_FRAME_MS = 1000 / 30;
        var FPV_PRESENTATION_FRAME_MS = 1000 / 60;
        var srtmTileCache = {};
        var srtmTileOrder = [];
        var srtmMissingTiles = {};
        var terrainHeightCache = {};
        var buildingTileState = {};
        var buildingTileQueue = [];
        var buildingTileOrder = [];
        var buildingLoadActive = false;
        var buildingRefreshTimer = null;
        var buildingTileset = null;
        var buildingTilesetAttempted = false;
        var useLegacyBuildingTiles = false;
        var offlineImageryLayer = null;
        var onlineSatelliteLayer = null;
        var googleTileset = null;
        var googleFailureCount = 0;
        var googleFailureWindowStarted = 0;
        var activeMapSource = MAP_CONFIG.preferredSource === 'google' && MAP_CONFIG.googleApiKey
            ? 'google'
            : 'offline';
        var visitedLocationTimer = null;
        var importedImageryLayers = [];
        var importedSceneLayers = [];
        var importedTerrainMaps = IMPORTED_MAPS.filter(function(map) {
            return map && map.kind === 'raster-terrain' && map.resourceUrl;
        });

        function post(msg) {
            console.log('[Map3D]', msg);
            try {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(msg);
                }
            } catch(e) {}
        }

        function observePromise(promise, onFulfilled, onRejected) {
            var chained = promise.then(onFulfilled);
            if (chained && typeof chained.otherwise === 'function') {
                chained.otherwise(onRejected);
            } else if (chained && typeof chained.catch === 'function') {
                chained.catch(onRejected);
            }
            return chained;
        }

        function logSizes() {
            post('debug:window.innerWidth: ' + window.innerWidth);
            post('debug:window.innerHeight: ' + window.innerHeight);
            var container = document.getElementById('cesiumContainer');
            if (container) {
                post('debug:cesiumContainer.clientWidth: ' + container.clientWidth);
                post('debug:cesiumContainer.clientHeight: ' + container.clientHeight);
                post('debug:cesiumContainer.offsetWidth: ' + container.offsetWidth);
                post('debug:cesiumContainer.offsetHeight: ' + container.offsetHeight);
            }
            var canvas = document.querySelector('#cesiumContainer canvas');
            if (canvas) {
                post('debug:canvas.width: ' + canvas.width);
                post('debug:canvas.height: ' + canvas.height);
                post('debug:canvas.clientWidth: ' + canvas.clientWidth);
                post('debug:canvas.clientHeight: ' + canvas.clientHeight);
            }
        }

        function setStatus(s) {
            var el = document.getElementById('status');
            if (el) el.textContent = s;
            post('status:' + s);
        }

        function showError(msg) {
            var el = document.getElementById('error');
            if (el) { el.style.display = 'block'; el.textContent = 'Error: ' + msg; }
            post('error:' + msg);
        }

        function hideLoading() {
            var el = document.getElementById('loading');
            if (el) el.classList.add('hidden');
        }

        function updateLockButton() {
            var button = document.getElementById('lockButton');
            if (!button) return;

            button.classList.toggle('locked', followVehicle);
            button.classList.toggle('unlocked', !followVehicle);
            button.setAttribute('aria-pressed', followVehicle ? 'true' : 'false');
            button.setAttribute('aria-label', followVehicle ? 'Drone lock on' : 'Drone lock off');
            button.title = followVehicle
                ? 'Drone locked. Drag or use arrows/WASD to orbit; wheel or +/- to zoom'
                : 'Lock camera target on drone';
        }

        function updateSourceControls(statusText) {
            var sourceButton = document.getElementById('sourceButton');
            var badge = document.getElementById('sourceBadge');
            if (sourceButton) {
                sourceButton.textContent = activeMapSource === 'google' ? 'GOOGLE 3D' : 'OFFLINE';
                sourceButton.title = activeMapSource === 'google'
                    ? 'Switch to the offline map'
                    : 'Switch to Google Photorealistic 3D Tiles';
            }
            if (badge) {
                badge.textContent = statusText ||
                    (activeMapSource === 'google' ? 'GOOGLE ONLINE' : 'OFFLINE READY');
            }
        }

        function setOfflineStatus(statusText) {
            updateSourceControls(String(statusText || 'OFFLINE READY').toUpperCase());
        }

        function getCameraLocation() {
            if (!window.cesiumViewer || !window.cesiumViewer.camera) return null;
            var cartographic = window.cesiumViewer.camera.positionCartographic;
            if (!cartographic) return null;
            var latitude = Cesium.Math.toDegrees(cartographic.latitude);
            var longitude = Cesium.Math.toDegrees(cartographic.longitude);
            return Number.isFinite(latitude) && Number.isFinite(longitude)
                ? { latitude: latitude, longitude: longitude }
                : null;
        }

        function requestOfflinePreparation() {
            var location = getCameraLocation();
            if (!location) return;
            updateSourceControls('PREPARING OFFLINE');
            post('action:prepare-offline:' + location.latitude.toFixed(7) + ':' +
                location.longitude.toFixed(7));
        }

        function scheduleVisitedLocation() {
            if (fpvMode) return;
            if (visitedLocationTimer) clearTimeout(visitedLocationTimer);
            visitedLocationTimer = setTimeout(function() {
                visitedLocationTimer = null;
                var location = getCameraLocation();
                if (location) {
                    post('visited:' + location.latitude.toFixed(7) + ':' +
                        location.longitude.toFixed(7));
                }
            }, 800);
        }

        function setFollowVehicle(enabled) {
            followVehicle = !!enabled;
            updateLockButton();

            if (followVehicle && lastVehicle) {
                moveCameraToVehicle(lastVehicle, true);
                if (window.cesiumViewer) {
                    window.cesiumViewer.scene.requestRender();
                }
            } else if (!followVehicle && window.cesiumViewer) {
                window.cesiumViewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY);
                followCameraInitialized = false;
            }

            setStatus(followVehicle
                ? 'Drone locked - orbit view remains enabled'
                : 'Drone lock disabled');
        }

        function toggleVehicleLock() {
            setFollowVehicle(!followVehicle);
        }

        function handleLockedCameraKeyDown(event) {
            if (!followVehicle || !followCameraInitialized || fpvMode || !window.cesiumViewer) return;
            var target = event.target;
            var tagName = target && target.tagName ? target.tagName.toLowerCase() : '';
            if (tagName === 'input' || tagName === 'textarea' || tagName === 'select' ||
                tagName === 'button') return;

            var camera = window.cesiumViewer.camera;
            var orbitStep = Cesium.Math.toRadians(event.shiftKey ? 8 : 3);
            var zoomStep = Math.max(10, Cesium.Cartesian3.magnitude(camera.position) * 0.08);
            var handled = true;
            switch (event.key) {
                case 'ArrowLeft':
                case 'a':
                case 'A':
                    camera.rotateLeft(orbitStep);
                    break;
                case 'ArrowRight':
                case 'd':
                case 'D':
                    camera.rotateRight(orbitStep);
                    break;
                case 'ArrowUp':
                case 'w':
                case 'W':
                    camera.rotateUp(orbitStep);
                    break;
                case 'ArrowDown':
                case 's':
                case 'S':
                    camera.rotateDown(orbitStep);
                    break;
                case '+':
                case '=':
                    camera.zoomIn(zoomStep);
                    break;
                case '-':
                case '_':
                    camera.zoomOut(zoomStep);
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled) {
                event.preventDefault();
                event.stopPropagation();
                window.cesiumViewer.scene.requestRender();
            }
        }

        function initMapControls() {
            var lockButton = document.getElementById('lockButton');
            var sourceButton = document.getElementById('sourceButton');
            var importButton = document.getElementById('importButton');
            var offlineButton = document.getElementById('offlineButton');
            var settingsButton = document.getElementById('settingsButton');

            if (lockButton) {
                lockButton.addEventListener('click', function(event) {
                    event.preventDefault();
                    event.stopPropagation();
                    toggleVehicleLock();
                });
            }
            if (sourceButton) {
                sourceButton.addEventListener('click', function(event) {
                    event.preventDefault();
                    event.stopPropagation();
                    post('action:source:' + (activeMapSource === 'google' ? 'offline' : 'google'));
                });
            }
            if (importButton) {
                importButton.addEventListener('click', function(event) {
                    event.preventDefault();
                    event.stopPropagation();
                    post('action:manage-offline-maps');
                });
            }
            if (offlineButton) {
                offlineButton.addEventListener('click', function(event) {
                    event.preventDefault();
                    event.stopPropagation();
                    requestOfflinePreparation();
                });
            }
            if (settingsButton) {
                settingsButton.addEventListener('click', function(event) {
                    event.preventDefault();
                    event.stopPropagation();
                    post('action:configure-google');
                });
            }

            [lockButton, sourceButton, importButton, offlineButton, settingsButton].forEach(function(button) {
                if (button) {
                    button.addEventListener('pointerdown', function(event) {
                        event.stopPropagation();
                    });
                }
            });
            document.addEventListener('keydown', handleLockedCameraKeyDown);
            updateLockButton();
            updateSourceControls(window.DIMP_CESIUM_FALLBACK ? 'OFFLINE - NO NETWORK' : null);
        }

        function normalizeVehicleType(vehicleType) {
            vehicleType = String(vehicleType || '').toLowerCase();
            if (vehicleType === 'fixedwing' || vehicleType === 'hexacopter' ||
                vehicleType === 'helicopter' || vehicleType === 'quadcopter') {
                return vehicleType;
            }
            return 'quadcopter';
        }

        function ensureVehicleEntities(vehicleType) {
            if (!window.cesiumViewer) return;

            var viewer = window.cesiumViewer;
            var startAir = Cesium.Cartesian3.fromDegrees(DEFAULT_LNG, DEFAULT_LAT, 25);
            vehicleType = normalizeVehicleType(vehicleType);

            if (!vehicleTrackEntity) {
                vehicleTrackEntity = viewer.entities.add({
                    name: 'UAV Track',
                    polyline: {
                        positions: new Cesium.CallbackProperty(function() {
                            return vehicleTrackPositions;
                        }, false),
                        width: 4,
                        material: Cesium.Color.CYAN.withAlpha(0.85),
                        clampToGround: false
                    }
                });
            }

            if (vehicleEntity && vehicleModelType === vehicleType) {
                return;
            }

            if (vehicleEntity) {
                viewer.entities.remove(vehicleEntity);
                vehicleEntity = null;
            }

            var modelScale = vehicleType === 'fixedwing' ? 3.0 :
                vehicleType === 'helicopter' ? 2.8 : 3.2;
            vehicleEntity = viewer.entities.add({
                name: 'DIMP ' + vehicleType,
                position: startAir,
                orientation: Cesium.Transforms.headingPitchRollQuaternion(
                    startAir,
                    new Cesium.HeadingPitchRoll(0, 0, 0)),
                model: {
                    uri: VEHICLES_BASE_URL + vehicleType + '.glb',
                    scale: modelScale,
                    minimumPixelSize: 34,
                    maximumScale: 240,
                    runAnimations: false,
                    incrementallyLoadTextures: false,
                    silhouetteColor: Cesium.Color.fromCssColorString('#071722'),
                    silhouetteSize: 1.25
                }
            });
            vehicleModelType = vehicleType;
            post('debug:Using ' + vehicleType + ' UAV model');
        }

        function finiteOr(value, fallback) {
            value = Number(value);
            return Number.isFinite(value) ? value : fallback;
        }

        function positiveModulo(value, modulus) {
            return ((value % modulus) + modulus) % modulus;
        }

        function cloneVehicleState(vehicle) {
            if (!vehicle) return null;
            return {
                lat: vehicle.lat,
                lng: vehicle.lng,
                absoluteAlt: vehicle.absoluteAlt,
                displayAlt: vehicle.displayAlt,
                relativeAlt: vehicle.relativeAlt,
                heading: vehicle.heading,
                pitch: vehicle.pitch,
                roll: vehicle.roll,
                speed: vehicle.speed,
                groundCourse: vehicle.groundCourse,
                climbRate: vehicle.climbRate,
                terrainHeight: vehicle.terrainHeight,
                positionUpdatedAt: vehicle.positionUpdatedAt,
                vehicleType: vehicle.vehicleType
            };
        }

        function shortestAngleDelta(from, to) {
            return positiveModulo(to - from + 180, 360) - 180;
        }

        function interpolateAngle(from, to, alpha) {
            return from + shortestAngleDelta(from, to) * alpha;
        }

        function predictVehicleState(vehicle, now) {
            var predicted = cloneVehicleState(vehicle);
            if (!predicted) return null;

            var ageSeconds = Math.max(0, Math.min(0.75,
                (now - finiteOr(vehicle.positionUpdatedAt, now)) / 1000));
            var speed = Math.max(0, finiteOr(vehicle.speed, 0));
            var course = Cesium.Math.toRadians(
                finiteOr(vehicle.groundCourse, vehicle.heading));
            var distance = speed * ageSeconds;

            if (distance > 0.01) {
                var north = Math.cos(course) * distance;
                var east = Math.sin(course) * distance;
                var latitudeRadians = Cesium.Math.toRadians(vehicle.lat);
                var longitudeScale = Math.max(0.1, Math.abs(Math.cos(latitudeRadians)));

                predicted.lat += Cesium.Math.toDegrees(north / EARTH_RADIUS_METERS);
                predicted.lng += Cesium.Math.toDegrees(
                    east / (EARTH_RADIUS_METERS * longitudeScale));
            }

            var altitudeDelta = finiteOr(vehicle.climbRate, 0) * ageSeconds;
            predicted.relativeAlt += altitudeDelta;
            predicted.absoluteAlt += altitudeDelta;
            return predicted;
        }

        function smoothVehicleState(current, target, elapsedSeconds) {
            if (!current) return cloneVehicleState(target);

            var positionAlpha = 1 - Math.exp(-Math.max(0, elapsedSeconds) * 12);
            var attitudeAlpha = 1 - Math.exp(-Math.max(0, elapsedSeconds) * 16);
            current.lat += (target.lat - current.lat) * positionAlpha;
            current.lng += (target.lng - current.lng) * positionAlpha;
            current.absoluteAlt += (target.absoluteAlt - current.absoluteAlt) * positionAlpha;
            current.relativeAlt += (target.relativeAlt - current.relativeAlt) * positionAlpha;
            current.heading = interpolateAngle(current.heading, target.heading, attitudeAlpha);
            current.pitch = interpolateAngle(current.pitch, target.pitch, attitudeAlpha);
            current.roll = interpolateAngle(current.roll, target.roll, attitudeAlpha);
            current.speed = target.speed;
            current.groundCourse = target.groundCourse;
            current.climbRate = target.climbRate;
            current.terrainHeight = target.terrainHeight;
            current.positionUpdatedAt = target.positionUpdatedAt;
            current.vehicleType = target.vehicleType;
            return current;
        }

        function padNumber(value, digits) {
            var text = Math.abs(value).toString();
            while (text.length < digits) {
                text = '0' + text;
            }
            return text;
        }

        function tileNameFromDegrees(latDegree, lngDegree, extension) {
            var ns = latDegree >= 0 ? 'N' : 'S';
            var ew = lngDegree >= 0 ? 'E' : 'W';
            return ns + padNumber(latDegree, 2) + ew + padNumber(lngDegree, 3) + extension;
        }

        function srtmTileInfo(lat, lng) {
            var latDegree = Math.floor(lat);
            var lngDegree = Math.floor(lng);
            return {
                latDegree: latDegree,
                lngDegree: lngDegree,
                name: tileNameFromDegrees(latDegree, lngDegree, '.hgt')
            };
        }

        function rememberSrtmTile(name) {
            var existingIndex = srtmTileOrder.indexOf(name);
            if (existingIndex >= 0) {
                srtmTileOrder.splice(existingIndex, 1);
            }

            srtmTileOrder.push(name);

            while (srtmTileOrder.length > MAX_SRTM_CACHE_TILES) {
                var evict = srtmTileOrder.shift();
                if (evict && srtmTileCache[evict] && srtmTileCache[evict].ready) {
                    delete srtmTileCache[evict];
                }
            }
        }

        function decodeSrtmBuffer(buffer, sourceSamples) {
            var targetSamples = Math.min(sourceSamples, SRTM_MEMORY_SAMPLES);
            var sourceView = new DataView(buffer);
            var targetData = new Int16Array(targetSamples * targetSamples);
            var sourceStep = (sourceSamples - 1) / (targetSamples - 1);
            var targetIndex = 0;

            for (var row = 0; row < targetSamples; row++) {
                var sourceRow = Math.round(row * sourceStep);

                for (var col = 0; col < targetSamples; col++) {
                    var sourceCol = Math.round(col * sourceStep);
                    targetData[targetIndex++] = sourceView.getInt16(
                        ((sourceRow * sourceSamples) + sourceCol) * 2,
                        false);
                }
            }

            return {
                samples: targetSamples,
                data: targetData
            };
        }

        function loadSrtmTileByInfo(info) {
            if (srtmMissingTiles[info.name]) {
                return Promise.resolve(null);
            }

            var existing = srtmTileCache[info.name];
            if (existing) {
                rememberSrtmTile(info.name);
                return existing.promise;
            }

            var entry = {
                ready: false,
                promise: fetch(SRTM_BASE_URL + info.name)
                    .then(function(response) {
                        if (!response.ok) {
                            throw new Error('Missing SRTM tile ' + info.name);
                        }
                        return response.arrayBuffer();
                    })
                    .then(function(buffer) {
                        var samples = Math.sqrt(buffer.byteLength / 2);
                        if (samples !== 1201 && samples !== 3601) {
                            throw new Error('Invalid SRTM tile size for ' + info.name);
                        }

                        var decoded = decodeSrtmBuffer(buffer, samples);
                        entry.ready = true;
                        entry.tile = {
                            name: info.name,
                            latDegree: info.latDegree,
                            lngDegree: info.lngDegree,
                            samples: decoded.samples,
                            data: decoded.data
                        };
                        rememberSrtmTile(info.name);
                        post('debug:Loaded SRTM tile ' + info.name +
                            ' (source ' + samples + ', cached ' + decoded.samples + ')');
                        return entry.tile;
                    })
                    .catch(function(error) {
                        entry.ready = true;
                        entry.missing = true;
                        srtmMissingTiles[info.name] = true;
                        delete srtmTileCache[info.name];
                        post('debug:' + String(error.message || error));
                        return null;
                    })
            };

            srtmTileCache[info.name] = entry;
            rememberSrtmTile(info.name);
            return entry.promise;
        }

        function loadSrtmTile(lat, lng) {
            return loadSrtmTileByInfo(srtmTileInfo(lat, lng));
        }

        function readSrtmSample(tile, row, col) {
            row = Math.max(0, Math.min(tile.samples - 1, row));
            col = Math.max(0, Math.min(tile.samples - 1, col));
            var value = tile.data[(row * tile.samples) + col];
            return value <= -32768 ? NaN : value;
        }

        function blendValid(a, b, fraction) {
            var aValid = Number.isFinite(a);
            var bValid = Number.isFinite(b);

            if (aValid && bValid) {
                return a + (b - a) * fraction;
            }

            if (aValid) return a;
            if (bValid) return b;
            return NaN;
        }

        function sampleLoadedSrtmTile(tile, lat, lng) {
            if (!tile) return NaN;

            var localLat = lat - tile.latDegree;
            var localLng = lng - tile.lngDegree;

            if (localLat < -0.000001 || localLat > 1.000001 || localLng < -0.000001 || localLng > 1.000001) {
                return NaN;
            }

            localLat = Math.max(0, Math.min(1, localLat));
            localLng = Math.max(0, Math.min(1, localLng));

            var colFloat = localLng * (tile.samples - 1);
            var rowFloat = (1 - localLat) * (tile.samples - 1);
            var col = Math.floor(colFloat);
            var row = Math.floor(rowFloat);
            var colFraction = colFloat - col;
            var rowFraction = rowFloat - row;

            var h00 = readSrtmSample(tile, row, col);
            var h10 = readSrtmSample(tile, row, col + 1);
            var h01 = readSrtmSample(tile, row + 1, col);
            var h11 = readSrtmSample(tile, row + 1, col + 1);
            var h0 = blendValid(h00, h10, colFraction);
            var h1 = blendValid(h01, h11, colFraction);
            var height = blendValid(h0, h1, rowFraction);

            return Number.isFinite(height) ? height : 0;
        }

        function terrainCacheKey(lat, lng) {
            return lat.toFixed(5) + ',' + lng.toFixed(5);
        }

        function getCachedTerrainHeight(lat, lng) {
            var key = terrainCacheKey(lat, lng);
            if (Object.prototype.hasOwnProperty.call(terrainHeightCache, key)) {
                return terrainHeightCache[key];
            }

            if (window.cesiumViewer && window.cesiumViewer.scene && window.cesiumViewer.scene.globe) {
                var cartographic = Cesium.Cartographic.fromDegrees(lng, lat);
                var globeHeight = window.cesiumViewer.scene.globe.getHeight(cartographic);
                if (Number.isFinite(globeHeight)) {
                    terrainHeightCache[key] = globeHeight;
                    return globeHeight;
                }
            }

            return 0;
        }

        function sampleSrtmHeight(lat, lng) {
            return loadSrtmTile(lat, lng).then(function(tile) {
                var height = sampleLoadedSrtmTile(tile, lat, lng);
                if (!Number.isFinite(height)) {
                    height = 0;
                }
                terrainHeightCache[terrainCacheKey(lat, lng)] = height;
                return height;
            });
        }

        function sampleActiveTerrainHeight(lat, lng) {
            var hasImportedCesiumTerrain = IMPORTED_MAPS.some(function(map) {
                return map && map.kind === 'cesium-terrain';
            });
            if (window.cesiumViewer &&
                (importedTerrainMaps.length > 0 || hasImportedCesiumTerrain)) {
                var position = Cesium.Cartographic.fromDegrees(lng, lat);
                return Cesium.sampleTerrain(window.cesiumViewer.terrainProvider, 13, [position])
                    .then(function(results) {
                        var height = results && results[0] ? Number(results[0].height) : NaN;
                        if (!Number.isFinite(height)) {
                            return sampleSrtmHeight(lat, lng);
                        }
                        terrainHeightCache[terrainCacheKey(lat, lng)] = height;
                        return height;
                    })
                    .catch(function() {
                        return sampleSrtmHeight(lat, lng);
                    });
            }
            return sampleSrtmHeight(lat, lng);
        }

        function refreshVehicleTerrainHeight(vehicle) {
            if (!vehicle) return;

            sampleActiveTerrainHeight(vehicle.lat, vehicle.lng).then(function(height) {
                if (lastVehicle &&
                    Math.abs(lastVehicle.lat - vehicle.lat) <= 0.000001 &&
                    Math.abs(lastVehicle.lng - vehicle.lng) <= 0.000001) {
                    lastVehicle.terrainHeight = height;
                }

                if (fpvTarget &&
                    Math.abs(fpvTarget.lat - vehicle.lat) <= 0.000001 &&
                    Math.abs(fpvTarget.lng - vehicle.lng) <= 0.000001) {
                    fpvTarget.terrainHeight = height;
                }
            });
        }

        function makeFlatTerrainData(level) {
            return new Cesium.HeightmapTerrainData({
                buffer: new Float32Array(TERRAIN_TILE_SIZE * TERRAIN_TILE_SIZE),
                width: TERRAIN_TILE_SIZE,
                height: TERRAIN_TILE_SIZE,
                childTileMask: level < MAX_SRTM_TERRAIN_LEVEL ? 15 : 0
            });
        }

        function makeFlatTerrainBuffer() {
            return new Float32Array(TERRAIN_TILE_SIZE * TERRAIN_TILE_SIZE);
        }

        function loadImportedTerrainBuffers(x, y, level) {
            if (importedTerrainMaps.length === 0 || level < 5 || level > 18) {
                return Promise.resolve([]);
            }

            var xTiles = 2 << level;
            var yTiles = 1 << level;
            var tileWest = -180 + (360 * x / xTiles);
            var tileEast = -180 + (360 * (x + 1) / xTiles);
            var tileNorth = 90 - (180 * y / yTiles);
            var tileSouth = 90 - (180 * (y + 1) / yTiles);
            var applicableMaps = importedTerrainMaps.filter(function(map) {
                var minZoom = Number(map.minZoom);
                var maxZoom = Number(map.maxZoom);
                if (Number.isFinite(minZoom) && level < minZoom) return false;
                if (Number.isFinite(maxZoom) && level > maxZoom) return false;
                if (!Number.isFinite(Number(map.west)) || !Number.isFinite(Number(map.south)) ||
                    !Number.isFinite(Number(map.east)) || !Number.isFinite(Number(map.north))) {
                    return true;
                }

                var mapWest = Number(map.west);
                var mapEast = Number(map.east);
                var latitudeOverlap = Number(map.north) > tileSouth &&
                    Number(map.south) < tileNorth;
                var longitudeOverlap = mapWest <= mapEast
                    ? mapEast > tileWest && mapWest < tileEast
                    : mapWest < tileEast || mapEast > tileWest;
                return latitudeOverlap && longitudeOverlap;
            });
            if (applicableMaps.length === 0) {
                return Promise.resolve([]);
            }

            return Promise.all(applicableMaps.map(function(map) {
                var url = String(map.resourceUrl)
                    .replace('{z}', String(level))
                    .replace('{x}', String(x))
                    .replace('{y}', String(y));
                return fetch(url)
                    .then(function(response) {
                        if (!response.ok) return null;
                        return response.arrayBuffer();
                    })
                    .then(function(buffer) {
                        if (!buffer || buffer.byteLength !==
                            TERRAIN_TILE_SIZE * TERRAIN_TILE_SIZE * 4) return null;
                        return new Float32Array(buffer);
                    })
                    .catch(function() { return null; });
            })).then(function(buffers) {
                return buffers.filter(function(buffer) { return !!buffer; });
            });
        }

        function mergeTerrainBuffers(baseBuffer, importedBuffers) {
            for (var bufferIndex = importedBuffers.length - 1; bufferIndex >= 0; bufferIndex--) {
                var imported = importedBuffers[bufferIndex];
                for (var sampleIndex = 0; sampleIndex < baseBuffer.length; sampleIndex++) {
                    if (Number.isFinite(imported[sampleIndex])) {
                        baseBuffer[sampleIndex] = imported[sampleIndex];
                    }
                }
            }
            return baseBuffer;
        }

        function createSrtmTerrainProvider() {
            function SrtmTerrainProvider() {
                this._tilingScheme = new Cesium.GeographicTilingScheme({
                    ellipsoid: Cesium.Ellipsoid.WGS84
                });
                this._errorEvent = new Cesium.Event();
                this._ready = true;
                this._readyPromise = Promise.resolve(true);
                this._credit = new Cesium.Credit('SRTM terrain');
                this._levelZeroMaximumGeometricError =
                    Cesium.TerrainProvider.getEstimatedLevelZeroGeometricErrorForAHeightmap(
                        this._tilingScheme.ellipsoid,
                        TERRAIN_TILE_SIZE,
                        this._tilingScheme.getNumberOfXTilesAtLevel(0));
            }

            Object.defineProperties(SrtmTerrainProvider.prototype, {
                errorEvent: { get: function() { return this._errorEvent; } },
                credit: { get: function() { return this._credit; } },
                tilingScheme: { get: function() { return this._tilingScheme; } },
                ready: { get: function() { return this._ready; } },
                readyPromise: { get: function() { return this._readyPromise; } },
                hasWaterMask: { get: function() { return false; } },
                hasVertexNormals: { get: function() { return false; } },
                hasMetadata: { get: function() { return false; } },
                availability: { get: function() { return undefined; } }
            });

            SrtmTerrainProvider.prototype.getLevelMaximumGeometricError = function(level) {
                return this._levelZeroMaximumGeometricError / (1 << level);
            };

            function requestSrtmTerrainBuffer(provider, x, y, level) {
                var rectangle = provider._tilingScheme.tileXYToRectangle(x, y, level);
                var west = Cesium.Math.toDegrees(rectangle.west);
                var east = Cesium.Math.toDegrees(rectangle.east);
                var south = Cesium.Math.toDegrees(rectangle.south);
                var north = Cesium.Math.toDegrees(rectangle.north);
                var latStart = Math.max(-90, Math.floor(south));
                var latEnd = Math.min(89, Math.floor(north - 0.0000001));
                var lngStart = Math.floor(west);
                var lngEnd = Math.floor(east - 0.0000001);
                var sourceTileCount = Math.max(0, latEnd - latStart + 1) *
                    Math.max(0, lngEnd - lngStart + 1);

                // Coarse Cesium tiles span huge areas. Loading every one-degree HGT
                // file for those tiles blocks WebView2 and consumes excessive memory.
                if (level < MIN_SRTM_TERRAIN_LEVEL ||
                    level > MAX_SRTM_TERRAIN_LEVEL ||
                    sourceTileCount > MAX_SRTM_SOURCE_TILES_PER_REQUEST) {
                    return Promise.resolve(makeFlatTerrainBuffer());
                }

                var neededTiles = {};
                var promises = [];
                var latIndex;
                var lngIndex;

                for (latIndex = latStart; latIndex <= latEnd; latIndex++) {
                    if (latIndex < -90 || latIndex > 89) continue;
                    for (lngIndex = lngStart; lngIndex <= lngEnd; lngIndex++) {
                        var normalizedLng = lngIndex;
                        while (normalizedLng < -180) normalizedLng += 360;
                        while (normalizedLng > 179) normalizedLng -= 360;
                        var info = srtmTileInfo(latIndex + 0.00001, normalizedLng + 0.00001);
                        if (!neededTiles[info.name]) {
                            neededTiles[info.name] = true;
                            promises.push(loadSrtmTileByInfo(info));
                        }
                    }
                }

                if (promises.length === 0) {
                    return Promise.resolve(makeFlatTerrainBuffer());
                }

                return Promise.all(promises).then(function(loadedTiles) {
                    var loadedByName = {};
                    for (var loadedIndex = 0; loadedIndex < loadedTiles.length; loadedIndex++) {
                        var loadedTile = loadedTiles[loadedIndex];
                        if (loadedTile) {
                            loadedByName[loadedTile.name] = loadedTile;
                        }
                    }

                    var buffer = new Float32Array(TERRAIN_TILE_SIZE * TERRAIN_TILE_SIZE);
                    var index = 0;

                    for (var row = 0; row < TERRAIN_TILE_SIZE; row++) {
                        var lat = north - ((north - south) * row / (TERRAIN_TILE_SIZE - 1));

                        for (var col = 0; col < TERRAIN_TILE_SIZE; col++) {
                            var lng = west + ((east - west) * col / (TERRAIN_TILE_SIZE - 1));
                            if (lng > 180) lng -= 360;
                            if (lng < -180) lng += 360;

                            var sourceTile = loadedByName[srtmTileInfo(lat, lng).name];
                            var height = sourceTile ? sampleLoadedSrtmTile(sourceTile, lat, lng) : 0;
                            buffer[index++] = Number.isFinite(height) ? height : 0;
                        }
                    }

                    return buffer;
                }).catch(function(error) {
                    post('debug:SRTM terrain tile fallback: ' + String(error.message || error));
                    return makeFlatTerrainBuffer();
                });
            }

            SrtmTerrainProvider.prototype.requestTileGeometry = function(x, y, level) {
                var provider = this;
                return Promise.all([
                    requestSrtmTerrainBuffer(provider, x, y, level),
                    loadImportedTerrainBuffers(x, y, level)
                ]).then(function(results) {
                    return new Cesium.HeightmapTerrainData({
                        buffer: mergeTerrainBuffers(results[0], results[1]),
                        width: TERRAIN_TILE_SIZE,
                        height: TERRAIN_TILE_SIZE,
                        childTileMask: level < Math.max(MAX_SRTM_TERRAIN_LEVEL, 18) ? 15 : 0
                    });
                }).catch(function(error) {
                    post('debug:Terrain tile fallback: ' + String(error.message || error));
                    return makeFlatTerrainData(level);
                });
            };

            SrtmTerrainProvider.prototype.getTileDataAvailable = function(x, y, level) {
                return level <= (importedTerrainMaps.length > 0 ? 18 : MAX_SRTM_TERRAIN_LEVEL);
            };

            SrtmTerrainProvider.prototype.loadTileDataAvailability = function() {
                return undefined;
            };

            return new SrtmTerrainProvider();
        }

        function createOfflineImageryProvider() {
            return new Cesium.TileMapServiceImageryProvider({
                url: Cesium.buildModuleUrl('Assets/Textures/NaturalEarthII')
            });
        }

        function addOfflineCachedImageryLayer() {
            if (!window.cesiumViewer || offlineImageryLayer || !MAP_CONFIG.offlineMapProvider) return;

            try {
                var providerName = encodeURIComponent(String(MAP_CONFIG.offlineMapProvider));
                var provider = new Cesium.UrlTemplateImageryProvider({
                    url: MAP_RESOURCE_BASE + 'gmap/' + providerName + '/{z}/{y}/{x}.jpg',
                    minimumLevel: 0,
                    maximumLevel: 22,
                    enablePickFeatures: false,
                    credit: new Cesium.Credit('Previously viewed map tiles')
                });
                offlineImageryLayer = window.cesiumViewer.imageryLayers.addImageryProvider(provider);
                post('debug:Previously viewed ' + MAP_CONFIG.offlineMapProvider + ' tiles enabled');
            } catch (error) {
                post('debug:Unable to enable viewed-tile cache: ' + String(error));
            }
        }

        function importedMapRectangle(map) {
            if (!map || !Number.isFinite(Number(map.west)) || !Number.isFinite(Number(map.south)) ||
                !Number.isFinite(Number(map.east)) || !Number.isFinite(Number(map.north))) {
                return undefined;
            }
            return Cesium.Rectangle.fromDegrees(
                Number(map.west), Number(map.south), Number(map.east), Number(map.north));
        }

        function addImportedImageryLayers() {
            if (!window.cesiumViewer || importedImageryLayers.length > 0) return;

            IMPORTED_MAPS.forEach(function(map) {
                if (!map || (map.kind !== 'raster-imagery' && map.kind !== 'xyz-imagery') ||
                    !map.resourceUrl) return;
                try {
                    var options = {
                        url: map.resourceUrl,
                        minimumLevel: Number.isFinite(Number(map.minZoom)) ? Number(map.minZoom) : 0,
                        maximumLevel: Number.isFinite(Number(map.maxZoom)) ? Number(map.maxZoom) : 22,
                        enablePickFeatures: false,
                        credit: new Cesium.Credit('Offline map: ' + String(map.name || map.id))
                    };
                    var rectangle = importedMapRectangle(map);
                    if (rectangle) options.rectangle = rectangle;
                    var provider = new Cesium.UrlTemplateImageryProvider(options);
                    provider.errorEvent.addEventListener(function(error) {
                        post('debug:Imported imagery tile unavailable: ' +
                            String(error && error.message ? error.message : error));
                    });
                    importedImageryLayers.push(
                        window.cesiumViewer.imageryLayers.addImageryProvider(provider));
                    post('debug:Imported imagery enabled: ' + String(map.name || map.id));
                } catch (error) {
                    post('debug:Unable to enable imported imagery: ' + String(error));
                }
            });
        }

        function initializeImportedTerrainProvider() {
            if (!window.cesiumViewer) return;
            var terrainPackage = IMPORTED_MAPS.find(function(map) {
                return map && map.kind === 'cesium-terrain' && map.resourceUrl;
            });
            if (!terrainPackage) return;

            try {
                var provider = new Cesium.CesiumTerrainProvider({
                    url: terrainPackage.resourceUrl,
                    requestVertexNormals: true,
                    requestWaterMask: false
                });
                observePromise(provider.readyPromise, function() {
                    if (!window.cesiumViewer) return;
                    window.cesiumViewer.terrainProvider = provider;
                    post('debug:Imported Cesium terrain enabled: ' +
                        String(terrainPackage.name || terrainPackage.id));
                    window.cesiumViewer.scene.requestRender();
                }, function(error) {
                    post('debug:Imported Cesium terrain unavailable; using HGT terrain: ' +
                        String(error && error.message ? error.message : error));
                });
            } catch (error) {
                post('debug:Unable to initialize imported Cesium terrain: ' + String(error));
            }
        }

        function initializeImportedSceneLayers() {
            if (!window.cesiumViewer || importedSceneLayers.length > 0) return;

            IMPORTED_MAPS.forEach(function(map) {
                if (!map || !map.resourceUrl) return;
                if (map.kind === '3d-tiles') {
                    try {
                        var tileset = window.cesiumViewer.scene.primitives.add(
                            new Cesium.Cesium3DTileset({
                                url: map.resourceUrl,
                                maximumScreenSpaceError: fpvMode ? 12 : 8,
                                maximumMemoryUsage: fpvMode ? 384 : 512,
                                dynamicScreenSpaceError: true,
                                cullRequestsWhileMoving: true,
                                preloadWhenHidden: false
                            }));
                        importedSceneLayers.push(tileset);
                        observePromise(tileset.readyPromise, function() {
                            post('debug:Imported 3D Tiles ready: ' + String(map.name || map.id));
                            window.cesiumViewer.scene.requestRender();
                        }, function(error) {
                            post('debug:Imported 3D Tiles unavailable: ' + String(error));
                        });
                    } catch (error) {
                        post('debug:Unable to initialize imported 3D Tiles: ' + String(error));
                    }
                } else if (map.kind === 'geojson-buildings') {
                    fetch(map.resourceUrl)
                        .then(function(response) {
                            if (!response.ok) throw new Error('HTTP ' + response.status);
                            return response.json();
                        })
                        .then(function(geojson) {
                            var importedTile = { entities: [] };
                            var features = geojson && Array.isArray(geojson.features)
                                ? geojson.features
                                : [];
                            var index = 0;
                            function addBatch() {
                                var end = Math.min(index + BUILDING_BATCH_SIZE, features.length);
                                for (; index < end; index++) {
                                    addBuildingFeature(features[index], importedTile);
                                }
                                window.cesiumViewer.scene.requestRender();
                                if (index < features.length) {
                                    setTimeout(addBatch, 16);
                                } else {
                                    importedSceneLayers.push(importedTile);
                                    post('debug:Imported GeoJSON ready: ' +
                                        String(map.name || map.id) + ' (' +
                                        importedTile.entities.length + ' polygons)');
                                }
                            }
                            addBatch();
                        })
                        .catch(function(error) {
                            post('debug:Imported GeoJSON unavailable: ' + String(error));
                        });
                } else if (map.kind === 'kml') {
                    observePromise(Cesium.KmlDataSource.load(map.resourceUrl, {
                        camera: window.cesiumViewer.scene.camera,
                        canvas: window.cesiumViewer.scene.canvas,
                        clampToGround: false
                    }), function(source) {
                        importedSceneLayers.push(source);
                        window.cesiumViewer.dataSources.add(source);
                        window.cesiumViewer.scene.requestRender();
                        post('debug:Imported KML ready: ' + String(map.name || map.id));
                    }, function(error) {
                        post('debug:Imported KML unavailable: ' + String(error));
                    });
                }
            });
        }

        function addOnlineSatelliteLayer() {
            if (!window.cesiumViewer || onlineSatelliteLayer || activeMapSource === 'google') return;

            try {
                var provider = new Cesium.ArcGisMapServerImageryProvider({
                    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer',
                    enablePickFeatures: false
                });

                provider.errorEvent.addEventListener(function(error) {
                    post('debug:Satellite tile unavailable; keeping offline fallback: ' +
                        String(error && error.message ? error.message : error));
                });

                onlineSatelliteLayer = window.cesiumViewer.imageryLayers.addImageryProvider(provider);
                observePromise(provider.readyPromise, function() {
                    post('debug:Online satellite imagery ready with missing-tile filtering');
                }, function(error) {
                    post('debug:Online satellite imagery unavailable; keeping offline fallback: ' + String(error));
                });
            } catch (e) {
                post('debug:Online satellite imagery unavailable: ' + String(e));
            }
        }

        function setLocalBuildingsVisible(visible) {
            if (buildingTileset) {
                buildingTileset.show = !!visible;
            }

            Object.keys(buildingTileState).forEach(function(tileName) {
                var tile = buildingTileState[tileName];
                if (!tile || !tile.entities) return;
                for (var i = 0; i < tile.entities.length; i++) {
                    tile.entities[i].show = !!visible;
                }
            });
        }

        function activateOfflineMode(reason) {
            activeMapSource = 'offline';
            if (window.cesiumViewer) {
                if (googleTileset) {
                    try {
                        window.cesiumViewer.scene.primitives.remove(googleTileset);
                    } catch (removeError) {
                        post('debug:Google tileset cleanup failed: ' + String(removeError));
                    }
                    googleTileset = null;
                }

                window.cesiumViewer.scene.globe.show = true;
                setLocalBuildingsVisible(true);
                addOnlineSatelliteLayer();
                if (!buildingTilesetAttempted) {
                    setTimeout(function() {
                        if (activeMapSource === 'offline' && !buildingTilesetAttempted) {
                            initializeBuildingLayer();
                        }
                    }, 900);
                }
                window.cesiumViewer.scene.requestRender();
            }

            updateSourceControls(reason ? 'OFFLINE FALLBACK' : 'OFFLINE READY');
            post('source:offline' + (reason ? ':' + reason : ''));
        }

        function registerGoogleTileFailure(error) {
            var now = Date.now();
            if (!googleFailureWindowStarted || now - googleFailureWindowStarted > 12000) {
                googleFailureWindowStarted = now;
                googleFailureCount = 0;
            }
            googleFailureCount++;
            post('debug:Google 3D tile failure: ' +
                String(error && error.message ? error.message : error));
            if (googleFailureCount >= 6) {
                activateOfflineMode('Google tiles unavailable');
            }
        }

        function activateGoogle3D() {
            if (!window.cesiumViewer || activeMapSource !== 'google') return;
            if (!MAP_CONFIG.googleApiKey) {
                activateOfflineMode('API key required');
                return;
            }

            updateSourceControls('GOOGLE LOADING');
            try {
                googleTileset = window.cesiumViewer.scene.primitives.add(
                    new Cesium.Cesium3DTileset({
                        url: 'https://tile.googleapis.com/v1/3dtiles/root.json?key=' +
                            encodeURIComponent(MAP_CONFIG.googleApiKey),
                        showCreditsOnScreen: true,
                        maximumScreenSpaceError: fpvMode ? 10 : 7,
                        maximumMemoryUsage: fpvMode ? 640 : 896,
                        dynamicScreenSpaceError: true,
                        cullRequestsWhileMoving: true,
                        preloadWhenHidden: false
                    }));

                if (googleTileset.tileFailed) {
                    googleTileset.tileFailed.addEventListener(registerGoogleTileFailure);
                }
                var googleReady = googleTileset.readyPromise.then(function() {
                    if (!googleTileset || !window.cesiumViewer) return;
                    googleFailureCount = 0;
                    window.cesiumViewer.scene.globe.show = false;
                    setLocalBuildingsVisible(false);
                    updateSourceControls('GOOGLE ONLINE');
                    setStatus('Google 3D Map ready');
                    post('source:google');
                    window.cesiumViewer.scene.requestRender();
                });
                var googleFailed = function(error) {
                    activateOfflineMode(String(error && error.message ? error.message : error));
                };
                if (googleReady && typeof googleReady.otherwise === 'function') {
                    googleReady.otherwise(googleFailed);
                } else if (googleReady && typeof googleReady.catch === 'function') {
                    googleReady.catch(googleFailed);
                }
            } catch (error) {
                activateOfflineMode(String(error && error.message ? error.message : error));
            }
        }

        function enableLegacyBuildingLayer(reason) {
            useLegacyBuildingTiles = true;
            buildingTileset = null;
            post('debug:Using legacy GeoJSON buildings' + (reason ? ': ' + reason : ''));
            refreshBuildingsAround(
                lastVehicle ? lastVehicle.lat : DEFAULT_LAT,
                lastVehicle ? lastVehicle.lng : DEFAULT_LNG);
            startBuildingRefreshLoop();
        }

        function initializeBuildingLayer() {
            if (!window.cesiumViewer || buildingTilesetAttempted || activeMapSource === 'google') return;
            buildingTilesetAttempted = true;

            try {
                var tileset = new Cesium.Cesium3DTileset({
                    url: BUILDINGS_3D_BASE_URL + 'tileset.json',
                    maximumScreenSpaceError: fpvMode ? 12 : 10,
                    maximumMemoryUsage: fpvMode ? 384 : 512,
                    dynamicScreenSpaceError: true,
                    dynamicScreenSpaceErrorDensity: 0.00278,
                    dynamicScreenSpaceErrorFactor: 4,
                    cullWithChildrenBounds: true,
                    cullRequestsWhileMoving: true,
                    preloadWhenHidden: false,
                    preferLeaves: true,
                    credit: new Cesium.Credit(
                        'Buildings &copy; <a href=""https://www.openstreetmap.org/copyright"" target=""_blank"">OpenStreetMap contributors</a>')
                });
                buildingTileset = window.cesiumViewer.scene.primitives.add(tileset);
                buildingTileset.show = activeMapSource !== 'google';

                if (tileset.tileFailed) {
                    tileset.tileFailed.addEventListener(function(error) {
                        post('debug:3D building tile unavailable: ' +
                            String(error && error.message ? error.message : error));
                    });
                }

                observePromise(tileset.readyPromise, function() {
                    useLegacyBuildingTiles = false;
                    post('debug:Offline terrain-seated 3D Tiles buildings ready');
                    window.cesiumViewer.scene.requestRender();
                }, function(error) {
                    if (window.cesiumViewer && buildingTileset) {
                        window.cesiumViewer.scene.primitives.remove(buildingTileset);
                    }
                    enableLegacyBuildingLayer(String(error && error.message ? error.message : error));
                });
            } catch (error) {
                enableLegacyBuildingLayer(String(error && error.message ? error.message : error));
            }
        }

        function buildingTileNameFromIndex(latIndex, lngIndex) {
            var latDegree = Math.floor(latIndex / BUILDING_TILE_SCALE);
            var lngDegree = Math.floor(lngIndex / BUILDING_TILE_SCALE);
            var latSub = latIndex - (latDegree * BUILDING_TILE_SCALE);
            var lngSub = lngIndex - (lngDegree * BUILDING_TILE_SCALE);
            return tileNameFromDegrees(latDegree, lngDegree, '') + '_' + latSub + '_' + lngSub + '.json';
        }

        function buildingTileNameFromDegrees(lat, lng) {
            return buildingTileNameFromIndex(
                Math.floor(lat * BUILDING_TILE_SCALE),
                Math.floor(lng * BUILDING_TILE_SCALE));
        }

        function clampBuildingHeight(height) {
            height = finiteOr(height, 8);
            if (height < 2) height = 2;
            if (height > 300) height = 300;
            return height;
        }

        function parseBuildingDistance(value) {
            if (typeof value === 'number') return value;
            var text = String(value == null ? '' : value).trim().toLowerCase();
            var match = text.match(/^(-?\d+(?:\.\d+)?)\s*(m|meter|meters|ft|feet|foot)?/);
            if (!match) return NaN;
            var distance = Number(match[1]);
            if (match[2] === 'ft' || match[2] === 'feet' || match[2] === 'foot') {
                distance *= 0.3048;
            }
            return distance;
        }

        function buildingHeightFromProperties(properties) {
            properties = properties || {};
            var height = parseBuildingDistance(
                properties.height != null ? properties.height : properties['building:height']);
            if (Number.isFinite(height)) return clampBuildingHeight(height);

            var levels = Number(
                properties['building:levels'] != null
                    ? properties['building:levels']
                    : properties.levels);
            return clampBuildingHeight(Number.isFinite(levels) ? levels * 3 : 8);
        }

        function positionsFromRing(ring) {
            var flat = [];

            for (var i = 0; i < ring.length; i++) {
                if (ring[i].length < 2) continue;
                flat.push(Number(ring[i][0]));
                flat.push(Number(ring[i][1]));
            }

            if (flat.length < 6) {
                return null;
            }

            return Cesium.Cartesian3.fromDegreesArray(flat);
        }

        function hierarchyFromPolygonCoordinates(coordinates) {
            if (!coordinates || coordinates.length === 0) {
                return null;
            }

            var outer = positionsFromRing(coordinates[0]);
            if (!outer) {
                return null;
            }

            var holes = [];
            for (var i = 1; i < coordinates.length; i++) {
                var hole = positionsFromRing(coordinates[i]);
                if (hole) {
                    holes.push(new Cesium.PolygonHierarchy(hole));
                }
            }

            return new Cesium.PolygonHierarchy(outer, holes);
        }

        function addBuildingPolygon(coordinates, height, name) {
            var hierarchy = hierarchyFromPolygonCoordinates(coordinates);
            if (!hierarchy || !window.cesiumViewer) return null;

            return window.cesiumViewer.entities.add({
                name: name || 'Building',
                polygon: {
                    hierarchy: hierarchy,
                    height: 0,
                    extrudedHeight: clampBuildingHeight(height),
                    heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
                    extrudedHeightReference: Cesium.HeightReference.RELATIVE_TO_GROUND,
                    material: Cesium.Color.fromCssColorString('#d7dbe2').withAlpha(0.58),
                    outline: true,
                    outlineColor: Cesium.Color.fromCssColorString('#4d5560')
                }
            });
        }

        function addBuildingFeature(feature, tile) {
            if (!feature || !feature.geometry) return;

            var height = buildingHeightFromProperties(feature.properties);
            var name = feature.properties ? feature.properties.name : 'Building';
            var geometry = feature.geometry;

            if (geometry.type === 'Polygon') {
                var entity = addBuildingPolygon(geometry.coordinates, height, name);
                if (entity) tile.entities.push(entity);
            } else if (geometry.type === 'MultiPolygon') {
                for (var i = 0; i < geometry.coordinates.length; i++) {
                    var multiEntity = addBuildingPolygon(geometry.coordinates[i], height, name);
                    if (multiEntity) tile.entities.push(multiEntity);
                }
            }
        }

        function selectBuildingFeatures(features) {
            if (features.length <= MAX_BUILDINGS_PER_TILE) {
                return features;
            }

            var selected = [];
            var step = features.length / MAX_BUILDINGS_PER_TILE;
            for (var i = 0; i < MAX_BUILDINGS_PER_TILE; i++) {
                selected.push(features[Math.floor(i * step)]);
            }
            return selected;
        }

        function rememberBuildingTile(tileName) {
            var existingIndex = buildingTileOrder.indexOf(tileName);
            if (existingIndex >= 0) {
                buildingTileOrder.splice(existingIndex, 1);
            }
            buildingTileOrder.push(tileName);

            while (buildingTileOrder.length > MAX_LOADED_BUILDING_TILES) {
                var evictName = buildingTileOrder.shift();
                var evictTile = buildingTileState[evictName];
                if (!evictTile || !window.cesiumViewer) continue;

                window.cesiumViewer.entities.suspendEvents();
                try {
                    for (var i = 0; i < evictTile.entities.length; i++) {
                        window.cesiumViewer.entities.remove(evictTile.entities[i]);
                    }
                } finally {
                    window.cesiumViewer.entities.resumeEvents();
                }

                delete buildingTileState[evictName];
                post('debug:Unloaded building tile ' + evictName);
            }
        }

        function loadBuildingTile(tileName) {
            if (!window.cesiumViewer) {
                return;
            }

            if (buildingTileState[tileName]) {
                if (buildingTileState[tileName].loaded) {
                    rememberBuildingTile(tileName);
                }
                return;
            }

            var tile = {
                loading: false,
                loaded: false,
                missing: false,
                entities: [],
                features: null,
                featureIndex: 0
            };

            buildingTileState[tileName] = tile;
            buildingTileQueue.push({ name: tileName, tile: tile });
            processNextBuildingTile();
        }

        function processNextBuildingTile() {
            if (buildingLoadActive || buildingTileQueue.length === 0 || !window.cesiumViewer) {
                return;
            }

            var queued = buildingTileQueue.shift();
            var tileName = queued.name;
            var tile = queued.tile;
            buildingLoadActive = true;
            tile.loading = true;

            fetch(BUILDINGS_BASE_URL + tileName)
                .then(function(response) {
                    if (!response.ok) {
                        throw new Error('Missing building tile ' + tileName);
                    }
                    return response.json();
                })
                .then(function(geojson) {
                    var features = geojson && geojson.features ? geojson.features : [];
                    tile.features = selectBuildingFeatures(features);
                    processBuildingBatch(tileName, tile);
                })
                .catch(function(error) {
                    tile.loading = false;
                    tile.missing = true;
                    buildingLoadActive = false;
                    post('debug:' + String(error.message || error));
                    setTimeout(processNextBuildingTile, 0);
                });
        }

        function processBuildingBatch(tileName, tile) {
            if (!window.cesiumViewer || !tile.features) {
                tile.loading = false;
                buildingLoadActive = false;
                setTimeout(processNextBuildingTile, 0);
                return;
            }

            var end = Math.min(tile.featureIndex + BUILDING_BATCH_SIZE, tile.features.length);
            for (; tile.featureIndex < end; tile.featureIndex++) {
                addBuildingFeature(tile.features[tile.featureIndex], tile);
            }

            window.cesiumViewer.scene.requestRender();

            if (tile.featureIndex < tile.features.length) {
                setTimeout(function() {
                    processBuildingBatch(tileName, tile);
                }, 16);
                return;
            }

            tile.features = null;
            tile.loading = false;
            tile.loaded = true;
            buildingLoadActive = false;
            rememberBuildingTile(tileName);
            post('debug:Loaded building tile ' + tileName + ' (' + tile.entities.length + ' buildings)');
            setTimeout(processNextBuildingTile, 0);
        }

        function refreshBuildingsAround(lat, lng) {
            if (!useLegacyBuildingTiles || !window.cesiumViewer ||
                !Number.isFinite(lat) || !Number.isFinite(lng)) {
                return;
            }

            var cameraHeight = window.cesiumViewer.camera.positionCartographic.height;
            if (!Number.isFinite(cameraHeight) || cameraHeight > BUILDING_MAX_CAMERA_HEIGHT) {
                return;
            }

            var centerLat = Math.floor(lat * BUILDING_TILE_SCALE);
            var centerLng = Math.floor(lng * BUILDING_TILE_SCALE);

            for (var dy = -BUILDING_LOAD_RADIUS_SUBTILES; dy <= BUILDING_LOAD_RADIUS_SUBTILES; dy++) {
                for (var dx = -BUILDING_LOAD_RADIUS_SUBTILES; dx <= BUILDING_LOAD_RADIUS_SUBTILES; dx++) {
                    loadBuildingTile(buildingTileNameFromIndex(centerLat + dy, centerLng + dx));
                }
            }
        }

        function getCameraCartographic() {
            if (!window.cesiumViewer) return null;

            var scene = window.cesiumViewer.scene;
            var canvas = scene.canvas;
            var center = new Cesium.Cartesian2(canvas.clientWidth / 2, canvas.clientHeight / 2);
            var picked = window.cesiumViewer.camera.pickEllipsoid(center, scene.globe.ellipsoid);

            if (!picked) {
                return null;
            }

            return Cesium.Cartographic.fromCartesian(picked);
        }

        function startBuildingRefreshLoop() {
            if (buildingRefreshTimer) {
                clearInterval(buildingRefreshTimer);
            }

            buildingRefreshTimer = setInterval(function() {
                if (lastVehicle) {
                    refreshBuildingsAround(lastVehicle.lat, lastVehicle.lng);
                    return;
                }

                var cartographic = getCameraCartographic();
                if (cartographic) {
                    refreshBuildingsAround(
                        Cesium.Math.toDegrees(cartographic.latitude),
                        Cesium.Math.toDegrees(cartographic.longitude));
                }
            }, 2500);
        }

        function getVehicleOrientation(vehicle) {
            var center = Cesium.Cartesian3.fromDegrees(vehicle.lng, vehicle.lat, vehicle.displayAlt);
            // The bundled glTF vehicle meshes use a right-facing local model axis.
            // Align every airframe's nose with MAVLink heading in the ENU scene.
            var modelHeading = vehicle.heading - 90;
            return Cesium.Transforms.headingPitchRollQuaternion(
                center,
                new Cesium.HeadingPitchRoll(
                    Cesium.Math.toRadians(modelHeading),
                    Cesium.Math.toRadians(vehicle.pitch),
                    Cesium.Math.toRadians(vehicle.roll)));
        }

        function updateVehicleModel(vehicle, centerPosition) {
            if (!vehicleEntity) return;
            vehicleEntity.position = centerPosition;
            vehicleEntity.orientation = getVehicleOrientation(vehicle);
            vehicleEntity.show = !fpvMode;
        }

        function getDisplayAltitude(lat, lng, absoluteAlt, relativeAlt, sampledTerrainHeight) {
            var relAlt = finiteOr(relativeAlt, absoluteAlt);
            var terrainHeight = finiteOr(
                sampledTerrainHeight,
                getCachedTerrainHeight(lat, lng));
            var displayAlt = finiteOr(relAlt, 0);

            // Keep the 3D model just above the surface while the aircraft is on the ground.
            if (!Number.isFinite(displayAlt) || displayAlt < 4) {
                displayAlt = 4;
            }

            return terrainHeight + displayAlt;
        }

        function addTrackPoint(position) {
            var last = vehicleTrackPositions.length > 0
                ? vehicleTrackPositions[vehicleTrackPositions.length - 1]
                : null;

            if (!last || Cesium.Cartesian3.distance(last, position) > 0.5) {
                vehicleTrackPositions.push(position);
            }

            if (vehicleTrackPositions.length > 2500) {
                vehicleTrackPositions.splice(0, vehicleTrackPositions.length - 2500);
            }
        }

        function moveCameraToVehicle(vehicle, resetOrbit) {
            if (!window.cesiumViewer || !vehicle) return;

            var target = Cesium.Cartesian3.fromDegrees(vehicle.lng, vehicle.lat, vehicle.displayAlt);
            var camera = window.cesiumViewer.camera;
            var targetFrame = Cesium.Transforms.eastNorthUpToFixedFrame(target);
            var currentOffset = followCameraInitialized && !resetOrbit
                ? Cesium.Cartesian3.clone(camera.position)
                : null;
            var currentRange = currentOffset ? Cesium.Cartesian3.magnitude(currentOffset) : NaN;

            if (!currentOffset || !Number.isFinite(currentRange) || currentRange < 5) {
                var relativeAlt = Math.abs(finiteOr(vehicle.relativeAlt, 0));
                var range = Math.max(450, Math.min(2500, relativeAlt + 650));
                currentOffset = new Cesium.HeadingPitchRange(
                    Cesium.Math.toRadians(vehicle.heading),
                    Cesium.Math.toRadians(PITCH),
                    range);
            }

            // Keep the camera in the aircraft's moving ENU target frame. Cesium's
            // controller can then orbit and zoom normally without telemetry updates
            // resetting the user's chosen side, pitch, or range.
            camera.lookAtTransform(targetFrame, currentOffset);
            followCameraInitialized = true;
        }

        function placeVehicle(vehicle, addToTrack) {
            if (!window.cesiumViewer || !vehicle) return;

            vehicle.displayAlt = getDisplayAltitude(
                vehicle.lat,
                vehicle.lng,
                vehicle.absoluteAlt,
                vehicle.relativeAlt,
                vehicle.terrainHeight);

            var airPosition = Cesium.Cartesian3.fromDegrees(vehicle.lng, vehicle.lat, vehicle.displayAlt);

            updateVehicleModel(vehicle, airPosition);

            if (addToTrack) {
                addTrackPoint(airPosition);
            }

            if (followVehicle) {
                moveCameraToVehicle(vehicle, false);
            }

            window.cesiumViewer.scene.requestRender();
        }

        function updateVehicle(lat, lng, absoluteAlt, heading, speed, relativeAlt, pitch, roll,
            vehicleType, groundCourse, climbRate) {
            if (!window.cesiumViewer) return;

            lat = finiteOr(lat, NaN);
            lng = finiteOr(lng, NaN);
            if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;

            heading = finiteOr(heading, 0);
            speed = finiteOr(speed, 0);
            relativeAlt = finiteOr(relativeAlt, absoluteAlt);
            pitch = Math.max(-90, Math.min(90, finiteOr(pitch, 0)));
            roll = Math.max(-180, Math.min(180, finiteOr(roll, 0)));
            groundCourse = finiteOr(groundCourse, heading);
            climbRate = finiteOr(climbRate, 0);
            vehicleType = normalizeVehicleType(vehicleType);
            ensureVehicleEntities(vehicleType);

            var now = performance.now();
            var previous = lastVehicle;
            var positionChanged = !previous ||
                Math.abs(previous.lat - lat) > 0.00000001 ||
                Math.abs(previous.lng - lng) > 0.00000001;
            var terrainHeight = previous && Number.isFinite(previous.terrainHeight)
                ? previous.terrainHeight
                : getCachedTerrainHeight(lat, lng);

            lastVehicle = {
                lat: lat,
                lng: lng,
                absoluteAlt: finiteOr(absoluteAlt, relativeAlt),
                displayAlt: 0,
                relativeAlt: relativeAlt,
                heading: heading,
                pitch: pitch,
                roll: roll,
                speed: speed,
                groundCourse: groundCourse,
                climbRate: climbRate,
                terrainHeight: terrainHeight,
                positionUpdatedAt: positionChanged
                    ? now
                    : finiteOr(previous.positionUpdatedAt, now),
                vehicleType: vehicleType
            };

            if (!displayedVehicle || displayedVehicle.vehicleType !== vehicleType) {
                displayedVehicle = cloneVehicleState(lastVehicle);
                placeVehicle(displayedVehicle, true);
            }

            if (positionChanged) {
                refreshVehicleTerrainHeight(lastVehicle);
            }
        }

        function animateVehicleFrame(timestamp) {
            animationFrameHandle = window.requestAnimationFrame(animateVehicleFrame);

            var presentationFrameMs = fpvMode
                ? FPV_PRESENTATION_FRAME_MS
                : MAP_PRESENTATION_FRAME_MS;

            if (!lastAnimationTime) {
                lastAnimationTime = timestamp - presentationFrameMs;
            }

            var elapsedMilliseconds = timestamp - lastAnimationTime;
            if (elapsedMilliseconds + 0.25 < presentationFrameMs) {
                return;
            }

            lastAnimationTime = timestamp - (elapsedMilliseconds % presentationFrameMs);
            var elapsedSeconds = Math.min(0.1, elapsedMilliseconds / 1000);

            if (fpvMode && fpvTarget) {
                displayedFpv = smoothVehicleState(
                    displayedFpv,
                    predictVehicleState(fpvTarget, timestamp),
                    elapsedSeconds);
                applyFpvCamera(displayedFpv);
                return;
            }

            if (!fpvMode && lastVehicle && vehicleEntity) {
                displayedVehicle = smoothVehicleState(
                    displayedVehicle,
                    predictVehicleState(lastVehicle, timestamp),
                    elapsedSeconds);
                var addToTrack = timestamp - lastTrackTime >= 100;
                if (addToTrack) {
                    lastTrackTime = timestamp;
                }
                placeVehicle(displayedVehicle, addToTrack);
            }
        }

        function startVehicleAnimationLoop() {
            if (!animationFrameHandle) {
                animationFrameHandle = window.requestAnimationFrame(animateVehicleFrame);
            }
        }

        function setFpvMode(enabled) {
            fpvMode = !!enabled;
            lastAnimationTime = 0;
            MAX_SRTM_CACHE_TILES = fpvMode ? 5 : 8;
            MAX_BUILDINGS_PER_TILE = fpvMode ? 250 : 400;

            var controls = document.getElementById('mapControls');
            if (controls) {
                controls.style.display = fpvMode ? 'none' : 'flex';
            }

            if (vehicleTrackEntity) {
                vehicleTrackEntity.show = !fpvMode;
            }

            if (vehicleEntity) {
                vehicleEntity.show = !fpvMode;
            }

            if (buildingTileset) {
                buildingTileset.maximumScreenSpaceError = fpvMode ? 12 : 10;
                buildingTileset.maximumMemoryUsage = fpvMode ? 384 : 512;
            }

            if (googleTileset) {
                googleTileset.maximumScreenSpaceError = fpvMode ? 10 : 7;
                googleTileset.maximumMemoryUsage = fpvMode ? 640 : 896;
            }

            if (window.cesiumViewer) {
                var scene = window.cesiumViewer.scene;
                scene.screenSpaceCameraController.enableInputs = !fpvMode;
                scene.globe.maximumScreenSpaceError = fpvMode ? 3 : 2;
                scene.globe.tileCacheSize = fpvMode ? 64 : 96;
                window.cesiumViewer.targetFrameRate = fpvMode ? 60 : 45;
                if (scene.skyBox) scene.skyBox.show = true;
                if (scene.skyAtmosphere) scene.skyAtmosphere.show = true;
                forceResize();
                scene.requestRender();
            }
        }

        function getFpvAltitude(lat, lng, absoluteAlt, relativeAlt, sampledTerrainHeight) {
            var terrainHeight = finiteOr(
                sampledTerrainHeight,
                getCachedTerrainHeight(lat, lng));
            var relAlt = Math.max(2.5, finiteOr(relativeAlt, 2.5));
            var terrainBasedAltitude = terrainHeight + relAlt;
            var aslAltitude = finiteOr(absoluteAlt, terrainBasedAltitude);

            // Prefer valid altitude-above-sea-level telemetry, but never put the
            // camera below the local SRTM surface.
            if (Math.abs(aslAltitude) < 0.01 || Math.abs(aslAltitude) > 100000) {
                aslAltitude = terrainBasedAltitude;
            }

            return Math.max(terrainHeight + 2.5, terrainBasedAltitude, aslAltitude);
        }

        function applyFpvCamera(vehicle) {
            if (!window.cesiumViewer || !vehicle) return;

            var displayAlt = getFpvAltitude(
                vehicle.lat,
                vehicle.lng,
                vehicle.absoluteAlt,
                vehicle.relativeAlt,
                vehicle.terrainHeight);
            vehicle.displayAlt = displayAlt;

            var camera = window.cesiumViewer.camera;
            camera.setView({
                destination: Cesium.Cartesian3.fromDegrees(vehicle.lng, vehicle.lat, displayAlt),
                orientation: {
                    heading: Cesium.Math.toRadians(vehicle.heading),
                    pitch: Cesium.Math.toRadians(vehicle.pitch),
                    roll: Cesium.Math.toRadians(vehicle.roll)
                }
            });

            if (camera.frustum && camera.frustum.fov !== undefined) {
                camera.frustum.fov = Cesium.Math.toRadians(74);
                camera.frustum.near = 0.5;
            }

            window.cesiumViewer.scene.requestRender();
        }

        function setFpvCamera(lat, lng, absoluteAlt, heading, pitch, roll, relativeAlt,
            speed, groundCourse, climbRate) {
            if (!window.cesiumViewer) return;

            lat = finiteOr(lat, NaN);
            lng = finiteOr(lng, NaN);
            if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;

            heading = finiteOr(heading, 0);
            pitch = Math.max(-89, Math.min(89, finiteOr(pitch, 0)));
            roll = Math.max(-180, Math.min(180, finiteOr(roll, 0)));
            relativeAlt = finiteOr(relativeAlt, 2.5);
            speed = Math.max(0, finiteOr(speed, 0));
            groundCourse = finiteOr(groundCourse, heading);
            climbRate = finiteOr(climbRate, 0);

            var now = performance.now();
            var previous = fpvTarget;
            var positionChanged = !previous ||
                Math.abs(previous.lat - lat) > 0.00000001 ||
                Math.abs(previous.lng - lng) > 0.00000001;
            var terrainHeight = previous && Number.isFinite(previous.terrainHeight)
                ? previous.terrainHeight
                : getCachedTerrainHeight(lat, lng);

            fpvTarget = {
                lat: lat,
                lng: lng,
                absoluteAlt: finiteOr(absoluteAlt, relativeAlt),
                displayAlt: 0,
                relativeAlt: relativeAlt,
                heading: heading,
                pitch: pitch,
                roll: roll,
                speed: speed,
                groundCourse: groundCourse,
                climbRate: climbRate,
                terrainHeight: terrainHeight,
                positionUpdatedAt: positionChanged
                    ? now
                    : finiteOr(previous.positionUpdatedAt, now)
            };
            lastVehicle = fpvTarget;

            if (!displayedFpv) {
                displayedFpv = cloneVehicleState(fpvTarget);
                applyFpvCamera(displayedFpv);
            }

            if (positionChanged) {
                refreshVehicleTerrainHeight(fpvTarget);
            }
        }

        function resetView() {
            if (!window.cesiumViewer || !window.cesiumViewer.camera) {
                post('debug:resetView called but viewer not ready');
                return;
            }

            try {
                window.cesiumViewer.camera.setView({
                    destination: Cesium.Cartesian3.fromDegrees(DEFAULT_LNG, DEFAULT_LAT, DEFAULT_ALT),
                    orientation: {
                        heading: Cesium.Math.toRadians(0),
                        pitch: Cesium.Math.toRadians(PITCH),
                        roll: 0
                    }
                });

                // Force render
                window.cesiumViewer.resize();
                window.cesiumViewer.scene.requestRender();

                post('debug:resetView complete');
                logSizes();
            } catch(e) {
                post('error:resetView failed: ' + String(e));
            }
        }

        function forceResize() {
            if (!window.cesiumViewer) return;

            // Force the container to full size
            var container = document.getElementById('cesiumContainer');
            var width = Math.max(
                document.documentElement.clientWidth || 0,
                document.body.clientWidth || 0,
                window.innerWidth || 0);
            var height = Math.max(
                document.documentElement.clientHeight || 0,
                document.body.clientHeight || 0,
                window.innerHeight || 0);
            // Preserve native detail on typical 1080p/1440p displays while keeping
            // the hidden FPV renderer sharp enough for the HUD composition.
            var maxRenderWidth = fpvMode ? 1080 : 2560;
            var pixelRatio = Math.min(1, maxRenderWidth / Math.max(1, width));

            if (container) {
                container.style.position = 'fixed';
                container.style.left = '0';
                container.style.top = '0';
                container.style.right = '0';
                container.style.bottom = '0';
                container.style.width = width + 'px';
                container.style.height = height + 'px';
            }

            var fillTargets = document.querySelectorAll(
                '#cesiumContainer, .cesium-viewer, .cesium-viewer-cesiumWidgetContainer, .cesium-widget');
            for (var i = 0; i < fillTargets.length; i++) {
                fillTargets[i].style.position = 'absolute';
                fillTargets[i].style.left = '0';
                fillTargets[i].style.top = '0';
                fillTargets[i].style.right = '0';
                fillTargets[i].style.bottom = '0';
                fillTargets[i].style.width = width + 'px';
                fillTargets[i].style.height = height + 'px';
                fillTargets[i].style.overflow = 'hidden';
            }

            var canvas = document.querySelector('#cesiumContainer canvas');
            if (canvas) {
                canvas.style.position = 'absolute';
                canvas.style.left = '0';
                canvas.style.top = '0';
                canvas.style.width = width + 'px';
                canvas.style.height = height + 'px';
                canvas.width = Math.round(width * pixelRatio);
                canvas.height = Math.round(height * pixelRatio);
            }

            // Call viewer resize
            window.cesiumViewer.resolutionScale = pixelRatio;
            window.cesiumViewer.resize();
            window.cesiumViewer.scene.requestRender();

            logSizes();
        }

        function initViewer() {
            setStatus('Stage 1: HTML loaded');
            post('debug:HTML loaded');
            initMapControls();
            logSizes();

            if (typeof Cesium === 'undefined') {
                showError('Cesium.js failed to load. Check internet connection.');
                setStatus('Stage 1 FAILED: Cesium not loaded');
                return;
            }

            setStatus('Stage 2: Cesium loaded');
            post('debug:Cesium library loaded');

            try {
                setStatus('Stage 3: Creating viewer...');
                post('debug:Creating Cesium viewer...');

                window.cesiumViewer = new Cesium.Viewer('cesiumContainer', {
                    imageryProvider: createOfflineImageryProvider(),
                    terrainProvider: createSrtmTerrainProvider(),
                    baseLayerPicker: false,
                    geocoder: false,
                    homeButton: false,
                    sceneModePicker: false,
                    timeline: false,
                    animation: false,
                    fullscreenButton: false,
                    navigationHelpButton: false,
                    shouldAnimate: false,
                    requestRenderMode: true,
                    maximumRenderTimeChange: Infinity,
                    infoBox: false,
                    selectionIndicator: false
                });

                setStatus('Stage 4: Viewer created');
                post('debug:Viewer created successfully');
                addOfflineCachedImageryLayer();
                if (activeMapSource !== 'google') {
                    addOnlineSatelliteLayer();
                }
                addImportedImageryLayers();
                initializeImportedTerrainProvider();
                initializeImportedSceneLayers();
                
                // Force initial resize
                forceResize();
                window.cesiumViewer.resize();
                
                // Check globe is visible
                var scene = window.cesiumViewer.scene;
                post('debug:Globe show: ' + scene.globe.show);
                
                // Configure scene
                scene.globe.enableLighting = true;
                scene.globe.showGroundAtmosphere = true;
                scene.globe.depthTestAgainstTerrain = true;
                scene.globe.maximumScreenSpaceError = 2;
                scene.globe.tileCacheSize = 96;
                scene.globe.preloadSiblings = false;
                var cameraController = scene.screenSpaceCameraController;
                cameraController.enableInputs = true;
                cameraController.enableRotate = true;
                cameraController.enableTilt = true;
                cameraController.enableZoom = true;
                cameraController.enableLook = true;
                cameraController.minimumZoomDistance = 5;
                cameraController.inertiaSpin = 0.82;
                cameraController.inertiaZoom = 0.72;
                window.cesiumViewer.targetFrameRate = 45;
                window.cesiumViewer.camera.moveEnd.addEventListener(scheduleVisitedLocation);
                
                // Keep a natural horizon visible in both map and FPV views.
                if (scene.skyBox) scene.skyBox.show = true;
                if (scene.skyAtmosphere) scene.skyAtmosphere.show = true;
                if (scene.sun) scene.sun.show = true;
                
                setStatus('Stage 5: Scene configured');
                post('debug:Scene configured');

                if (activeMapSource === 'google') {
                    activateGoogle3D();
                } else {
                    activateOfflineMode(window.DIMP_CESIUM_FALLBACK ? 'No network' : null);
                }
                
                // Initial resize and render
                window.cesiumViewer.resize();
                scene.requestRender();
                
                // Center camera
                setStatus('Stage 6: Centering camera...');
                resetView();
                startVehicleAnimationLoop();

                // Let the first globe frame render before the streamed building layer starts.
                setTimeout(function() {
                    initializeBuildingLayer();
                }, 1200);
                
                setStatus(activeMapSource === 'google' ? 'Loading Google 3D Map' : 'Stage 7: 3D Map ready');
                post('debug:3D Map ready');
                
                // Hide loading overlay
                hideLoading();
                
            } catch(viewerError) {
                showError('Viewer creation failed: ' + (viewerError.message || String(viewerError)));
                setStatus('Viewer creation failed');
                post('error:Viewer creation failed: ' + String(viewerError));
                return;
            }
            
            // Window resize handler
            window.addEventListener('resize', function() {
                post('debug:Window resize event');
                forceResize();
            });
            window.addEventListener('offline', function() {
                if (activeMapSource === 'google') {
                    activateOfflineMode('Network unavailable');
                }
            });
            window.addEventListener('online', function() {
                if (activeMapSource !== 'google' && MAP_CONFIG.preferredSource === 'google' &&
                    MAP_CONFIG.googleApiKey && !window.DIMP_CESIUM_FALLBACK) {
                    activeMapSource = 'google';
                    activateGoogle3D();
                }
            });
            
            // Multiple delayed resize calls
            setTimeout(function() {
                post('debug:Delayed resize 250ms');
                forceResize();
                resetView();
            }, 250);
            
            setTimeout(function() {
                post('debug:Delayed resize 500ms');
                forceResize();
                logSizes();
            }, 500);
            
            setTimeout(function() {
                post('debug:Delayed resize 1000ms');
                forceResize();
                logSizes();
            }, 1000);
            
            setTimeout(function() {
                post('debug:Delayed resize 2000ms');
                forceResize();
                logSizes();
            }, 2000);
            
            // Expose API
            window.dimpMap = {
                setVehicle: function(lat, lng, alt, heading, speed, relativeAlt, pitch, roll,
                    vehicleType, groundCourse, climbRate) {
                    if (!window.cesiumViewer) return;
                    if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;
                    updateVehicle(lat, lng, alt, heading, speed, relativeAlt, pitch, roll,
                        vehicleType, groundCourse, climbRate);
                },
                centerOnVehicle: function() {
                    if (lastVehicle) {
                        moveCameraToVehicle(lastVehicle, true);
                        window.cesiumViewer.scene.requestRender();
                        setStatus('Centering on vehicle');
                    } else {
                        resetView();
                        setStatus('Waiting for vehicle position');
                    }
                },
                enableFollow: function() {
                    setFollowVehicle(true);
                },
                disableFollow: function() {
                    setFollowVehicle(false);
                },
                toggleFollow: function() {
                    toggleVehicleLock();
                },
                resetView: function() {
                    forceResize();
                    if (lastVehicle) {
                        moveCameraToVehicle(lastVehicle, true);
                    } else {
                        resetView();
                    }
                    setStatus('3D Map ready');
                },
                clearTrack: function() {
                    vehicleTrackPositions.length = 0;
                    if (lastVehicle) {
                        addTrackPoint(Cesium.Cartesian3.fromDegrees(
                            lastVehicle.lng,
                            lastVehicle.lat,
                            lastVehicle.displayAlt));
                    }
                    if (window.cesiumViewer) {
                        window.cesiumViewer.scene.requestRender();
                    }
                    setStatus('Track cleared');
                },
                setFpvMode: function(enabled) {
                    setFpvMode(enabled);
                },
                setFpvCamera: function(lat, lng, absoluteAlt, heading, pitch, roll, relativeAlt,
                    speed, groundCourse, climbRate) {
                    setFpvCamera(lat, lng, absoluteAlt, heading, pitch, roll, relativeAlt,
                        speed, groundCourse, climbRate);
                },
                resize: function() {
                    forceResize();
                    logSizes();
                },
                setOfflineStatus: function(statusText) {
                    setOfflineStatus(statusText);
                }
            };
            
            setTimeout(function() {
                post('ready');
                setStatus(activeMapSource === 'google' ? 'Google 3D Map ready' : '3D Map ready');
                logSizes();
            }, 1500);
        }
        
        // Global error handler
        window.onerror = function(msg, url, line) {
            setStatus('JS Error: ' + msg);
            showError(msg + ' (line ' + line + ')');
            post('error:JS Error at line ' + line + ': ' + msg);
            return true;
        };
        
        window.onunhandledrejection = function(e) {
            setStatus('Promise error');
            showError(String(e.reason));
            post('error:Promise rejection: ' + String(e.reason));
        };
        
        // Start initialization when DOM is ready
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', initViewer);
        } else {
            initViewer();
        }
    })();
    </script>
</body>
</html>";
            return html
                .Replace("__MAP_CONFIG__", mapConfiguration)
                .Replace("__CESIUM_BASE_URL__", runtimeBase)
                .Replace("__CESIUM_SCRIPT_URL__", runtimeBase + "Cesium.js")
                .Replace("__CESIUM_WIDGETS_URL__", runtimeBase + "Widgets/widgets.css");
        }

        private static object[] BuildImportedMapConfiguration(
            IReadOnlyList<Map3DOfflinePackage> importedPackages)
        {
            if (importedPackages == null || importedPackages.Count == 0)
            {
                return Array.Empty<object>();
            }

            return importedPackages
                .Where(package => package != null && package.Enabled &&
                                  !string.IsNullOrWhiteSpace(package.Id))
                .Select(package => new
                {
                    id = package.Id,
                    name = package.Name ?? package.Id,
                    kind = package.Kind,
                    resourceUrl = GetImportedResourceUrl(package),
                    west = package.West,
                    south = package.South,
                    east = package.East,
                    north = package.North,
                    minZoom = package.MinZoom,
                    maxZoom = package.MaxZoom
                })
                .Cast<object>()
                .ToArray();
        }

        private static string GetImportedResourceUrl(Map3DOfflinePackage package)
        {
            string baseUrl = "https://" + MapResourceHost + "/";
            string packageId = Uri.EscapeDataString(package.Id);
            switch (package.Kind)
            {
                case Map3DPackageKinds.RasterImagery:
                    return baseUrl + "raster/" + packageId + "/{z}/{x}/{y}.png";
                case Map3DPackageKinds.RasterTerrain:
                    return baseUrl + "dem/" + packageId + "/{z}/{x}/{y}.f32";
                case Map3DPackageKinds.XyzImagery:
                    return baseUrl + "xyz/" + packageId + "/{z}/{x}/{y}";
                case Map3DPackageKinds.CesiumTerrain:
                    string terrainDirectory = (Path.GetDirectoryName(package.RelativePath) ?? string.Empty)
                        .Replace('\\', '/')
                        .Trim('/');
                    return baseUrl + "imports/" + packageId + "/" +
                           EncodeResourcePath(terrainDirectory) + "/";
                default:
                    return baseUrl + "imports/" + packageId + "/" +
                           EncodeResourcePath(package.RelativePath);
            }
        }

        private static string EncodeResourcePath(string relativePath)
        {
            return string.Join("/", (relativePath ?? string.Empty)
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        }

        private static double GetCoordinateSetting(string key, double fallback)
        {
            double value;
            return double.TryParse(
                Settings.Instance.GetString(key, string.Empty),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : fallback;
        }
    }
}
