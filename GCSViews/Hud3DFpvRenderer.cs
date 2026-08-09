using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Renders a Cesium nose-camera view into the existing HUD background.
    /// </summary>
    internal sealed class Hud3DFpvRenderer : IDisposable
    {
        internal const int TargetFramesPerSecond = 60;
        internal const int FrameIntervalMilliseconds = 1000 / TargetFramesPerSecond;
        internal const int TelemetryUpdatesPerSecond = 30;
        internal const int TelemetryUpdateIntervalMilliseconds = 1000 / TelemetryUpdatesPerSecond;
        internal const int MaximumConsecutiveCaptureFailures = 5;
        internal const int ScreencastJpegQuality = 82;
        internal const int ScreencastWatchdogMilliseconds = 1000;
        internal const int ScreencastRetryMilliseconds = 1500;

        private const int SchedulerIntervalMilliseconds = 8;
        private const int PerformanceLogIntervalMilliseconds = 5000;

        private const double DefaultLat = 31.9539;
        private const double DefaultLng = 35.9106;
        private const double DefaultAbsoluteAlt = 950;
        private const double DefaultRelativeAlt = 120;

        private readonly HUD hud;
        private readonly TaskCompletionSource<bool> mapReady =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream captureStream = new MemoryStream();

        private FpvRenderHost renderHost;
        private WebView2 webView;
        private CoreWebView2DevToolsProtocolEventReceiver screencastFrameReceiver;
        private System.Windows.Forms.Timer frameTimer;
        private Image displayedFrame;
        private Bitmap frameBufferA;
        private Bitmap frameBufferB;
        private bool active = true;
        private bool running;
        private bool captureInProgress;
        private bool usingScreencast;
        private bool screencastRecoveryInProgress;
        private bool disposed;
        private bool failed;
        private int screencastDecodeInProgress;
        private int consecutiveCaptureFailures;
        private long nextFrameDueTicks;
        private long screencastStartedTicks;
        private long lastScreencastFrameTicks;
        private long nextScreencastRetryTicks;
        private long performanceWindowStartedTicks;
        private long performanceCaptureTicks;
        private int performanceRenderedFrames;
        private int performanceDroppedFrames;

        internal event EventHandler Failed;

        internal string LastError { get; private set; }

        internal Hud3DFpvRenderer(HUD hud)
        {
            this.hud = hud ?? throw new ArgumentNullException(nameof(hud));
        }

        internal async Task StartAsync()
        {
            ThrowIfDisposed();
            CreateRenderHost();

            Map3D.ConfigureWebView2LoaderPath();

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DIMP",
                "WebView2",
                "Hud3DFpv");

            Directory.CreateDirectory(userDataFolder);

            CoreWebView2EnvironmentOptions environmentOptions =
                new CoreWebView2EnvironmentOptions(
                    "--disable-background-timer-throttling " +
                    "--disable-renderer-backgrounding " +
                    "--disable-backgrounding-occluded-windows " +
                    "--disable-features=CalculateNativeWinOcclusion");
            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(null, userDataFolder, environmentOptions);
            ThrowIfDisposed();

            await webView.EnsureCoreWebView2Async(environment);
            ThrowIfDisposed();

            ConfigureWebView();
            webView.NavigateToString(Map3D.GetCesiumHtml());

            Task completed = await Task.WhenAny(mapReady.Task, Task.Delay(20000));
            if (completed != mapReady.Task)
            {
                throw new TimeoutException("The 3D FPV map did not finish loading within 20 seconds.");
            }

            await mapReady.Task;
            ThrowIfDisposed();

            await webView.CoreWebView2.ExecuteScriptAsync(
                "window.dimpMap && window.dimpMap.setFpvMode(true);");

            running = active;
            if (running)
            {
                ResetFrameSchedule();
                usingScreencast = await TryStartScreencastAsync();
                if (!usingScreencast)
                {
                    ScheduleScreencastRetry();
                }
                frameTimer.Start();
                if (usingScreencast)
                {
                    await UpdateScreencastCameraAsync();
                }
                else
                {
                    await RenderFrameAsync();
                }
            }
        }

        internal void Pause()
        {
            active = false;
            running = false;
            frameTimer?.Stop();
        }

        internal void Resume()
        {
            if (disposed || failed)
            {
                return;
            }

            active = true;
            if (mapReady.Task.Status == TaskStatus.RanToCompletion)
            {
                running = true;
                long now = Stopwatch.GetTimestamp();
                screencastStartedTicks = now;
                lastScreencastFrameTicks = now;
                ResetFrameSchedule();
                frameTimer?.Start();
            }
        }

        private void CreateRenderHost()
        {
            Size renderSize = GetRenderSize();

            renderHost = new FpvRenderHost
            {
                BackColor = Color.Black,
                Bounds = new Rectangle(-20000, -20000, renderSize.Width, renderSize.Height),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Text = "DIMP HUD 3D FPV Renderer"
            };

            webView = new WebView2
            {
                BackColor = Color.Black,
                DefaultBackgroundColor = Color.Black,
                Dock = DockStyle.Fill
            };

            frameTimer = new System.Windows.Forms.Timer
            {
                // Cesium presents at 60 Hz. Telemetry is sent at 30 Hz and smoothly
                // interpolated in JavaScript, reducing cross-process work and jitter.
                Interval = SchedulerIntervalMilliseconds
            };
            frameTimer.Tick += FrameTimer_Tick;

            renderHost.Controls.Add(webView);

            Form owner = hud.FindForm();
            if (owner != null && !owner.IsDisposed)
            {
                renderHost.Show(owner);
            }
            else
            {
                renderHost.Show();
            }
        }

        private Size GetRenderSize()
        {
            int hudWidth = Math.Max(1, hud.ClientSize.Width);
            int hudHeight = Math.Max(1, hud.ClientSize.Height);
            double ratio = Math.Max(1.0, Math.Min(2.4, (double)hudWidth / hudHeight));
            int width = Math.Min(800, Math.Max(480, (int)Math.Round(hudWidth * 1.2)));
            int height = (int)Math.Round(width / ratio);

            if (height > 500)
            {
                height = 500;
                width = (int)Math.Round(height * ratio);
            }

            return new Size(Math.Max(420, width), Math.Max(280, height));
        }

        private void ConfigureWebView()
        {
            CoreWebView2 core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsScriptEnabled = true;
            core.Settings.IsWebMessageEnabled = true;

            core.AddWebResourceRequestedFilter(
                "https://" + Map3D.MapResourceHost + "/*",
                CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += CoreWebView2_WebResourceRequested;
            core.WebMessageReceived += CoreWebView2_WebMessageReceived;
            core.ProcessFailed += CoreWebView2_ProcessFailed;

            webView.NavigationCompleted += WebView_NavigationCompleted;
        }

        private void CoreWebView2_WebResourceRequested(
            object sender,
            CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                Uri uri = new Uri(e.Request.Uri);
                if (!string.Equals(uri.Host, Map3D.MapResourceHost, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (Map3D.IsDynamicMapResourcePath(uri.AbsolutePath))
                {
                    CoreWebView2Deferral deferral = e.GetDeferral();
                    HandleDynamicMapResourceAsync(e, deferral);
                    return;
                }

                string filePath = Map3D.ResolveLocalMapResource(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    string headers = "Content-Type: " + Map3D.GetContentType(filePath) +
                                     "\r\nAccess-Control-Allow-Origin: *";
                    Stream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        stream,
                        200,
                        "OK",
                        headers);
                    return;
                }

                if (uri.AbsolutePath.StartsWith("/gmap/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(Map3D.TransparentPng, false),
                        200,
                        "OK",
                        "Content-Type: image/png\r\nAccess-Control-Allow-Origin: *\r\n" +
                        "Cache-Control: public, max-age=300");
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
                Log("Local resource error: " + ex.Message);
            }
        }

        private async void HandleDynamicMapResourceAsync(
            CoreWebView2WebResourceRequestedEventArgs e,
            CoreWebView2Deferral deferral)
        {
            try
            {
                Uri uri = new Uri(e.Request.Uri);
                Map3D.DynamicMapResource resource = await Task.Run(
                    () => Map3D.GetDynamicMapResource(uri.AbsolutePath));
                if (disposed || webView?.CoreWebView2 == null)
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
                        "\r\nAccess-Control-Allow-Origin: *\r\n" +
                        "Cache-Control: public, max-age=86400");
                }
                else if (resource != null && resource.IsImagery)
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(Map3D.TransparentPng, false),
                        200,
                        "OK",
                        "Content-Type: image/png\r\nAccess-Control-Allow-Origin: *\r\n" +
                        "Cache-Control: public, max-age=300");
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
                Log("Dynamic offline map resource error: " + ex.Message);
                if (!disposed && webView?.CoreWebView2 != null)
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

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message;

            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch
            {
                return;
            }

            if (string.Equals(message, "ready", StringComparison.OrdinalIgnoreCase))
            {
                mapReady.TrySetResult(true);
            }
            else if (message != null && message.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                Log(message);
                mapReady.TrySetException(new InvalidOperationException(message.Substring(6)));
            }
        }

        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Fail("The 3D FPV renderer stopped because its WebView2 process failed (" +
                 e.ProcessFailedKind + "). Toggle 3D Map FPV to restart it.");
        }

        private void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                mapReady.TrySetException(new InvalidOperationException(
                    "3D FPV page failed to load: " + e.WebErrorStatus));
            }
        }

        private void FrameTimer_Tick(object sender, EventArgs e)
        {
            if (screencastRecoveryInProgress)
            {
                return;
            }

            if (usingScreencast && IsScreencastStalled())
            {
                _ = RecoverScreencastAsync(
                    "The Chromium compositor stream paused; restarting it.");
                return;
            }

            if (!usingScreencast && IsScreencastRetryDue())
            {
                _ = RecoverScreencastAsync(
                    "Retrying the Chromium compositor stream.");
                return;
            }

            if (TryClaimFrameSlot())
            {
                if (usingScreencast)
                {
                    _ = UpdateScreencastCameraAsync();
                }
                else
                {
                    _ = RenderFrameAsync();
                }
            }
        }

        private void ResetFrameSchedule()
        {
            long now = Stopwatch.GetTimestamp();
            nextFrameDueTicks = now + GetTelemetryUpdateIntervalTicks();
            performanceWindowStartedTicks = now;
            performanceCaptureTicks = 0;
            performanceRenderedFrames = 0;
            performanceDroppedFrames = 0;
        }

        private bool TryClaimFrameSlot()
        {
            if (!running || disposed || webView?.CoreWebView2 == null)
            {
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            long intervalTicks = GetTelemetryUpdateIntervalTicks();
            if (nextFrameDueTicks == 0)
            {
                nextFrameDueTicks = now;
            }
            // WM_TIMER can arrive a few milliseconds either side of its nominal
            // deadline. A small early tolerance avoids an accidental 30 Hz cadence
            // when a 16 ms timer tick lands just before a 16.67 ms frame boundary.
            long earlyToleranceTicks = Math.Max(1, Stopwatch.Frequency / 250);
            if (now + earlyToleranceTicks < nextFrameDueTicks)
            {
                return false;
            }

            long lateSlots = Math.Max(0, (now - nextFrameDueTicks) / intervalTicks);
            nextFrameDueTicks += (lateSlots + 1) * intervalTicks;
            if (captureInProgress)
            {
                performanceDroppedFrames += (int)Math.Min(int.MaxValue, lateSlots + 1);
                return false;
            }

            performanceDroppedFrames += (int)Math.Min(int.MaxValue, lateSlots);
            return true;
        }

        private static long GetFrameIntervalTicks()
        {
            return Math.Max(1, Stopwatch.Frequency / TargetFramesPerSecond);
        }

        private static long GetTelemetryUpdateIntervalTicks()
        {
            return Math.Max(1, Stopwatch.Frequency / TelemetryUpdatesPerSecond);
        }

        private async Task<bool> TryStartScreencastAsync()
        {
            try
            {
                CoreWebView2 core = webView?.CoreWebView2;
                if (core == null)
                {
                    return false;
                }

                Size renderSize = webView.ClientSize;
                await core.CallDevToolsProtocolMethodAsync("Page.bringToFront", "{}");
                screencastFrameReceiver = core.GetDevToolsProtocolEventReceiver(
                    "Page.screencastFrame");
                screencastFrameReceiver.DevToolsProtocolEventReceived +=
                    ScreencastFrameReceived;

                JObject options = new JObject
                {
                    ["format"] = "jpeg",
                    ["quality"] = ScreencastJpegQuality,
                    ["maxWidth"] = Math.Max(1, renderSize.Width),
                    ["maxHeight"] = Math.Max(1, renderSize.Height),
                    ["everyNthFrame"] = 1
                };

                await core.CallDevToolsProtocolMethodAsync(
                    "Page.startScreencast",
                    options.ToString(Formatting.None));

                screencastStartedTicks = Stopwatch.GetTimestamp();
                lastScreencastFrameTicks = 0;
                nextScreencastRetryTicks = 0;
                Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "Chromium compositor stream started at {0}x{1}, JPEG quality {2}.",
                    renderSize.Width,
                    renderSize.Height,
                    ScreencastJpegQuality));
                return true;
            }
            catch (Exception ex)
            {
                DetachScreencastReceiver();
                Log("Chromium compositor stream is unavailable; using preview capture: " +
                    ex.Message);
                return false;
            }
        }

        private async void ScreencastFrameReceived(
            object sender,
            CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            JObject payload;
            int sessionId;

            try
            {
                payload = JObject.Parse(e.ParameterObjectAsJson);
                sessionId = payload.Value<int>("sessionId");
            }
            catch (Exception ex)
            {
                Log("Unable to read compositor frame metadata: " + ex.Message);
                return;
            }

            try
            {
                CoreWebView2 core = webView?.CoreWebView2;
                if (core != null)
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Page.screencastFrameAck",
                        "{\"sessionId\":" + sessionId.ToString(CultureInfo.InvariantCulture) + "}");
                }
            }
            catch (Exception ex)
            {
                if (!disposed)
                {
                    Log("Unable to acknowledge compositor frame: " + ex.Message);
                }
            }

            lastScreencastFrameTicks = Stopwatch.GetTimestamp();
            if (!running || disposed || !usingScreencast)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref screencastDecodeInProgress, 1, 0) != 0)
            {
                performanceDroppedFrames++;
                return;
            }

            string encodedFrame = payload.Value<string>("data");
            if (string.IsNullOrEmpty(encodedFrame))
            {
                Interlocked.Exchange(ref screencastDecodeInProgress, 0);
                return;
            }

            long frameStartedTicks = Stopwatch.GetTimestamp();
            try
            {
                byte[] bytes = Convert.FromBase64String(encodedFrame);
                Bitmap frame = await Task.Run(() => DecodeScreencastFrame(bytes));

                if (!running || disposed || !usingScreencast || frame == null)
                {
                    return;
                }

                DisplayFrame(frame);
                consecutiveCaptureFailures = 0;
                RecordCompletedFrame(frameStartedTicks);
            }
            catch (Exception ex)
            {
                HandleFrameFailure(ex);
            }
            finally
            {
                Interlocked.Exchange(ref screencastDecodeInProgress, 0);
            }
        }

        private Bitmap DecodeScreencastFrame(byte[] bytes)
        {
            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (Image decoded = Image.FromStream(stream))
            {
                return CopyToReusableFrameBuffer(decoded);
            }
        }

        private bool IsScreencastStalled()
        {
            if (!usingScreencast || screencastRecoveryInProgress || !running)
            {
                return false;
            }

            long baseline = lastScreencastFrameTicks != 0
                ? lastScreencastFrameTicks
                : screencastStartedTicks;
            if (baseline == 0)
            {
                return false;
            }

            long elapsedMilliseconds =
                (Stopwatch.GetTimestamp() - baseline) * 1000 / Stopwatch.Frequency;
            return elapsedMilliseconds >= ScreencastWatchdogMilliseconds;
        }

        private bool IsScreencastRetryDue()
        {
            return running && !disposed && !screencastRecoveryInProgress &&
                   nextScreencastRetryTicks != 0 &&
                   Stopwatch.GetTimestamp() >= nextScreencastRetryTicks;
        }

        private void ScheduleScreencastRetry()
        {
            nextScreencastRetryTicks = Stopwatch.GetTimestamp() +
                Stopwatch.Frequency * ScreencastRetryMilliseconds / 1000;
        }

        private async Task RecoverScreencastAsync(string reason)
        {
            if (screencastRecoveryInProgress || disposed || !running)
            {
                return;
            }

            screencastRecoveryInProgress = true;
            bool streamWasActive = usingScreencast;
            usingScreencast = false;
            try
            {
                if (streamWasActive)
                {
                    await StopScreencastAsync();
                }

                if (disposed || !running)
                {
                    return;
                }

                await Task.Delay(75);
                usingScreencast = await TryStartScreencastAsync();
                ResetFrameSchedule();
                if (usingScreencast)
                {
                    Log(reason + " Compositor stream restored at 60 Hz.");
                    await UpdateScreencastCameraAsync();
                }
                else
                {
                    ScheduleScreencastRetry();
                    Log(reason + " Using preview capture temporarily; another compositor retry is scheduled.");
                }
            }
            finally
            {
                screencastRecoveryInProgress = false;
            }
        }

        private async Task StopScreencastAsync()
        {
            try
            {
                if (webView?.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Page.stopScreencast",
                        "{}");
                }
            }
            catch (Exception ex)
            {
                if (!disposed)
                {
                    Log("Unable to stop compositor stream cleanly: " + ex.Message);
                }
            }
            finally
            {
                DetachScreencastReceiver();
            }
        }

        private void DetachScreencastReceiver()
        {
            if (screencastFrameReceiver == null)
            {
                return;
            }

            screencastFrameReceiver.DevToolsProtocolEventReceived -= ScreencastFrameReceived;
            screencastFrameReceiver = null;
        }

        private async Task UpdateScreencastCameraAsync()
        {
            if (!running || captureInProgress || disposed || webView?.CoreWebView2 == null)
            {
                return;
            }

            captureInProgress = true;
            try
            {
                await UpdateCameraAsync();
                consecutiveCaptureFailures = 0;
            }
            catch (Exception ex)
            {
                HandleFrameFailure(ex);
            }
            finally
            {
                captureInProgress = false;
            }
        }

        private async Task RenderFrameAsync()
        {
            if (!running || captureInProgress || disposed || webView?.CoreWebView2 == null)
            {
                return;
            }

            captureInProgress = true;
            long captureStartedTicks = Stopwatch.GetTimestamp();

            try
            {
                await UpdateCameraAsync();

                captureStream.SetLength(0);
                captureStream.Position = 0;
                await webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Jpeg,
                    captureStream);

                if (!running || disposed || captureStream.Length == 0)
                {
                    return;
                }

                captureStream.Position = 0;
                Bitmap frame;
                using (Image decoded = Image.FromStream(captureStream))
                {
                    frame = CopyToReusableFrameBuffer(decoded);
                }

                DisplayFrame(frame);
                consecutiveCaptureFailures = 0;
                RecordCompletedFrame(captureStartedTicks);
            }
            catch (Exception ex)
            {
                HandleFrameFailure(ex);
            }
            finally
            {
                captureInProgress = false;
            }
        }

        private void HandleFrameFailure(Exception ex)
        {
            if (disposed)
            {
                return;
            }

            consecutiveCaptureFailures++;
            if (consecutiveCaptureFailures <= 3)
            {
                Log("Frame error: " + ex.Message);
            }

            if (consecutiveCaptureFailures >= MaximumConsecutiveCaptureFailures)
            {
                Fail("The 3D FPV renderer stopped after repeated frame failures: " +
                     ex.Message + ". Toggle 3D Map FPV to restart it.");
            }
        }

        private Bitmap CopyToReusableFrameBuffer(Image decoded)
        {
            Bitmap target = ReferenceEquals(displayedFrame, frameBufferA)
                ? frameBufferB
                : frameBufferA;

            if (target == null || target.Width != decoded.Width || target.Height != decoded.Height)
            {
                DisposeFrame(target);
                target = new Bitmap(decoded.Width, decoded.Height,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                if (ReferenceEquals(displayedFrame, frameBufferA))
                {
                    frameBufferB = target;
                }
                else
                {
                    frameBufferA = target;
                }
            }

            lock (target)
            {
                using (Graphics graphics = Graphics.FromImage(target))
                {
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(decoded, 0, 0);
                }
            }

            return target;
        }

        private void RecordCompletedFrame(long captureStartedTicks)
        {
            long now = Stopwatch.GetTimestamp();
            performanceCaptureTicks += Math.Max(0, now - captureStartedTicks);
            performanceRenderedFrames++;

            long windowTicks = now - performanceWindowStartedTicks;
            long logIntervalTicks = Stopwatch.Frequency * PerformanceLogIntervalMilliseconds / 1000;
            if (windowTicks < logIntervalTicks)
            {
                return;
            }

            double seconds = Math.Max(0.001, (double)windowTicks / Stopwatch.Frequency);
            double framesPerSecond = performanceRenderedFrames / seconds;
            double averageCaptureMilliseconds = performanceRenderedFrames == 0
                ? 0
                : performanceCaptureTicks * 1000.0 /
                  Stopwatch.Frequency / performanceRenderedFrames;
            Size renderSize = webView?.ClientSize ?? Size.Empty;
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "Performance ({0}): {1:0.0} fps, {2:0.0} ms average frame latency, {3} dropped, {4}x{5} render target",
                usingScreencast ? "compositor" : "preview",
                framesPerSecond,
                averageCaptureMilliseconds,
                performanceDroppedFrames,
                renderSize.Width,
                renderSize.Height));

            performanceWindowStartedTicks = now;
            performanceCaptureTicks = 0;
            performanceRenderedFrames = 0;
            performanceDroppedFrames = 0;
        }

        private Task<string> UpdateCameraAsync()
        {
            double lat = DefaultLat;
            double lng = DefaultLng;
            double absoluteAlt = DefaultAbsoluteAlt;
            double relativeAlt = DefaultRelativeAlt;
            double heading = 0;
            double pitch = -5;
            double roll = 0;
            double speed = 0;
            double groundCourse = 0;
            double climbRate = 0;

            if (MainV2.comPort?.MAV?.cs != null)
            {
                CurrentState state = MainV2.comPort.MAV.cs;
                if (VehicleTelemetryValidation.HasUsablePosition(state))
                {
                    double altitudeScale = Math.Abs(CurrentState.multiplieralt) > 0.0001
                        ? CurrentState.multiplieralt
                        : 1;

                    lat = state.lat;
                    lng = state.lng;
                    absoluteAlt = state.altasl / altitudeScale;
                    relativeAlt = state.alt / altitudeScale;
                    heading = state.yaw;
                    pitch = state.pitch;
                    roll = state.roll;
                    speed = VehicleTelemetryValidation.GetVisualGroundSpeed(state);
                    groundCourse = state.groundcourse;
                    climbRate = VehicleTelemetryValidation.GetVisualClimbRate(state);
                }
            }

            string script = string.Format(
                CultureInfo.InvariantCulture,
                "window.dimpMap && window.dimpMap.setFpvCamera({0},{1},{2},{3},{4},{5},{6},{7},{8},{9});",
                lat,
                lng,
                absoluteAlt,
                heading,
                pitch,
                roll,
                relativeAlt,
                speed,
                groundCourse,
                climbRate);

            return webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void DisplayFrame(Image frame)
        {
            if (disposed)
            {
                frame.Dispose();
                return;
            }

            Image oldFrame = displayedFrame;
            displayedFrame = frame;
            hud.bgimage = frame;

            if (!ReferenceEquals(oldFrame, frameBufferA) &&
                !ReferenceEquals(oldFrame, frameBufferB))
            {
                DisposeFrame(oldFrame);
            }
        }

        private void Fail(string message)
        {
            if (disposed || failed)
            {
                return;
            }

            failed = true;
            running = false;
            LastError = message;
            frameTimer?.Stop();
            Log(message);

            if (ReferenceEquals(hud.bgimage, displayedFrame))
            {
                hud.bgimage = null;
            }

            Image oldFrame = displayedFrame;
            displayedFrame = null;
            if (!ReferenceEquals(oldFrame, frameBufferA) &&
                !ReferenceEquals(oldFrame, frameBufferB))
            {
                DisposeFrame(oldFrame);
            }

            if (hud.IsHandleCreated && !hud.IsDisposed)
            {
                try
                {
                    hud.BeginInvoke(new Action(() => Failed?.Invoke(this, EventArgs.Empty)));
                    return;
                }
                catch (InvalidOperationException)
                {
                }
            }

            Failed?.Invoke(this, EventArgs.Empty);
        }

        private static void DisposeFrame(Image frame)
        {
            if (frame == null)
            {
                return;
            }

            lock (frame)
            {
                frame.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(Hud3DFpvRenderer));
            }
        }

        private static void Log(string message)
        {
            try
            {
                string logDirectory = Settings.GetDataDirectory();
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    Path.Combine(logDirectory, "map3d_fpv_debug.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                    " [Hud3DFpv] " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            running = false;
            usingScreencast = false;
            mapReady.TrySetCanceled();

            if (frameTimer != null)
            {
                frameTimer.Stop();
                frameTimer.Tick -= FrameTimer_Tick;
                frameTimer.Dispose();
                frameTimer = null;
            }

            if (ReferenceEquals(hud.bgimage, displayedFrame))
            {
                hud.bgimage = null;
            }

            displayedFrame = null;
            DisposeFrame(frameBufferA);
            DisposeFrame(frameBufferB);
            frameBufferA = null;
            frameBufferB = null;

            if (webView != null)
            {
                webView.NavigationCompleted -= WebView_NavigationCompleted;

                if (webView.CoreWebView2 != null)
                {
                    _ = webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Page.stopScreencast",
                        "{}");
                    webView.CoreWebView2.WebResourceRequested -= CoreWebView2_WebResourceRequested;
                    webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    webView.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;
                }

                DetachScreencastReceiver();
                webView.Dispose();
                webView = null;
            }

            if (renderHost != null)
            {
                renderHost.Close();
                renderHost.Dispose();
                renderHost = null;
            }

            captureStream.Dispose();
        }

        private sealed class FpvRenderHost : Form
        {
            private const int WsExToolWindow = 0x00000080;
            private const int WsExNoActivate = 0x08000000;

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                    return parameters;
                }
            }
        }
    }
}
