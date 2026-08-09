using GMap.NET;
using GMap.NET.WindowsForms;
using MissionPlanner.Controls;
using MissionPlanner.Maps;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using log4net;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.GCSViews
{
    public partial class SITL : MyUserControl, IActivate
    {
        internal static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        Uri sitlmasterurl = new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/");
        Uri sitlbetaurl = new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/Beta/");

        Uri sitlcopterstableurl = new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/CopterStable/");
        Uri sitlplanestableurl = new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/PlaneStable/");
        Uri sitlroverstableurl = new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/RoverStable/");

        string sitldirectory = Settings.GetUserDataDirectory() + "sitl" +
                               Path.DirectorySeparatorChar;

        public static string BundledPath = "";

        private const string VehicleInfoManifestName = "vehicleinfo.json";
        private const string SitlStateMarkerName = ".dimp-sitl-state";
        private const string SitlStateSchema = "dimp-sitl-state-v2";
        private static readonly string[] FramePrefixFallbacks =
        {
            "octa", "tri", "y6", "firefly", "heli", "gazebo", "last_letter", "jsbsim",
            "quadplane", "plane-elevon", "plane-vtail", "plane", "airsim"
        };

        private static readonly string[] SitlDependencyFiles =
        {
            "cygatomic-1.dll",
            "cyggcc_s-1.dll",
            "cyggcc_s-seh-1.dll",
            "cyggomp-1.dll",
            "cygiconv-2.dll",
            "cygintl-8.dll",
            "cygquadmath-0.dll",
            "cygssp-0.dll",
            "cygstdc++-6.dll",
            "cygwin1.dll"
        };

        internal static readonly string[] BundledVehicleImages =
        {
            "ArduCopter.exe",
            "ArduHeli.exe",
            "ArduPlane.exe",
            "ArduRover.exe"
        };

        internal static IReadOnlyList<string> RequiredDependencyFiles => SitlDependencyFiles;

        GMapOverlay markeroverlay;

        GMapMarkerWP homemarker = new GMapMarkerWP(new PointLatLng(-34.98106, 117.85201), "H");
        bool onmarker = false;
        bool mousedown = false;
        private PointLatLng MouseDownStart;

        internal static UdpClient SITLSEND;

        internal static List<System.Diagnostics.Process> simulator = new List<Process>();

        private ComboBox cmbPhysicsProfile;
        private NumericUpDown numWindSpeed;
        private NumericUpDown numWindTurbulence;
        private Label lblPhysicsProfile;
        private Label lblWindSpeed;
        private Label lblWindTurbulence;
        private string customAerodynamicModelPath;

        // Match the native ArduPilot SITL loop-rate default used by current
        // desktop builds. Lowering this value reduces physics integration fidelity.
        internal const int HighFidelitySimulationRate = 1200;

        private sealed class PhysicsProfile
        {
            internal string Name { get; }
            internal bool UsesAerodynamicJson { get; }
            internal bool SelectsCustomModel { get; }

            internal PhysicsProfile(string name, bool usesAerodynamicJson = false,
                bool selectsCustomModel = false)
            {
                Name = name;
                UsesAerodynamicJson = usesAerodynamicJson;
                SelectsCustomModel = selectsCustomModel;
            }

            public override string ToString()
            {
                return Name;
            }
        }

        /*
    { "quadplane",          QuadPlane::create },
    { "xplane",             XPlane::create },
    { "firefly",            QuadPlane::create },
    { "+",                  MultiCopter::create },
    { "quad",               MultiCopter::create },
    { "copter",             MultiCopter::create },
    { "x",                  MultiCopter::create },
    { "hexa",               MultiCopter::create },
    { "octa",               MultiCopter::create },
    { "tri",                MultiCopter::create },
    { "y6",                 MultiCopter::create },
    { "heli",               Helicopter::create },
    { "heli-dual",          Helicopter::create },
    { "heli-compound",      Helicopter::create },
    { "singlecopter",       SingleCopter::create },
    { "coaxcopter",         SingleCopter::create },
    { "rover",              SimRover::create },
    { "crrcsim",            CRRCSim::create },
    { "jsbsim",             JSBSim::create },
    { "flightaxis",         FlightAxis::create },
    { "gazebo",             Gazebo::create },
    { "last_letter",        last_letter::create },
    { "tracker",            Tracker::create },
    { "balloon",            Balloon::create },
    { "plane",              Plane::create },
    { "calibration",        Calibration::create },
             */

        ///tmp/.build/ArduCopter.elf -M+ -O-34.98106,117.85201,40,0
        ///tmp/.build/APMrover2.elf -Mrover -O-34.98106,117.85201,40,0
        ///tmp/.build/ArduPlane.elf -Mjsbsim -O-34.98106,117.85201,40,0 --autotest-dir ./
        ///tmp/.build/ArduCopter.elf -Mheli -O-34.98106,117.85201,40,0
        ~SITL()
        {
            try
            {
                simulator.ForEach(a=>
                {
                    try
                    {
                        a.Kill();
                    }catch { }
                });
            }
            catch
            {
            }
        }

        public SITL()
        {
            InitializeComponent();
            InitializePhysicsControls();
            ApplyModernLayout();

            if (!Directory.Exists(sitldirectory))
                Directory.CreateDirectory(sitldirectory);

            // Populate the version selection box
            var versionSelect = new Dictionary<string, APFirmware.RELEASE_TYPES?>()
            {
                { "Latest (Dev)", APFirmware.RELEASE_TYPES.DEV },
                { "Beta", APFirmware.RELEASE_TYPES.BETA },
                { "Stable", APFirmware.RELEASE_TYPES.OFFICIAL },
                { "Skip Download", null }
            };
            cmb_version.DataSource = new BindingSource(versionSelect, null);
            cmb_version.DisplayMember = "Key";
            cmb_version.ValueMember = "Value";

            var selectedVersion = Settings.Instance.ContainsKey("sitl_download_version")
                ? Settings.Instance.GetInt32("sitl_download_version")
                : (!string.IsNullOrEmpty(TryGetLocalSITLImage("ArduCopter.elf")) ? 3 : 0);

            cmb_version.SelectedIndex = Math.Max(0, Math.Min(versionSelect.Count - 1, selectedVersion));
            PopulateBundledModelFrames();
        }

        private void InitializePhysicsControls()
        {
            cmbPhysicsProfile = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 6, 10, 6)
            };
            cmbPhysicsProfile.Items.Add(new PhysicsProfile("High fidelity (recommended)"));
            cmbPhysicsProfile.Items.Add(new PhysicsProfile(
                "Skywalker aerodynamic model", usesAerodynamicJson: true));
            cmbPhysicsProfile.Items.Add(new PhysicsProfile(
                "Custom aerodynamic JSON...", usesAerodynamicJson: true, selectsCustomModel: true));
            cmbPhysicsProfile.Items.Add(new PhysicsProfile("ArduPilot native default"));
            cmbPhysicsProfile.SelectedIndex = Math.Max(0, Math.Min(
                cmbPhysicsProfile.Items.Count - 1,
                Settings.Instance.GetInt32("sitl_physics_profile", 0)));
            cmbPhysicsProfile.SelectedIndexChanged += CmbPhysicsProfile_SelectedIndexChanged;

            numWindSpeed = new NumericUpDown
            {
                DecimalPlaces = 1,
                Increment = 0.5M,
                Minimum = 0,
                Maximum = 60,
                Value = ClampDecimal(Settings.Instance.GetDouble("sitl_wind_speed", 0), 0, 60),
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 6, 10, 6)
            };
            numWindTurbulence = new NumericUpDown
            {
                DecimalPlaces = 1,
                Increment = 0.1M,
                Minimum = 0,
                Maximum = 10,
                Value = ClampDecimal(Settings.Instance.GetDouble("sitl_wind_turbulence", 0), 0, 10),
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 6, 0, 6)
            };

            lblPhysicsProfile = CreateInlineLabel("Physics");
            lblWindSpeed = CreateInlineLabel("Wind m/s");
            lblWindTurbulence = CreateInlineLabel("Turbulence");
        }

        private static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return minimum;
            }

            return Math.Max(minimum, Math.Min(maximum, (decimal)value));
        }

        private static Label CreateInlineLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ModernUi.TextSecondary
            };
        }

        private void CmbPhysicsProfile_SelectedIndexChanged(object sender, EventArgs e)
        {
            PhysicsProfile profile = cmbPhysicsProfile.SelectedItem as PhysicsProfile;
            if (profile == null)
            {
                return;
            }

            if (profile.SelectsCustomModel)
            {
                using (var dialog = new OpenFileDialog
                {
                    Title = "Import ArduPilot aerodynamic model",
                    Filter = "Aerodynamic model JSON (*.json)|*.json|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                })
                {
                    if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                    {
                        cmbPhysicsProfile.SelectedIndex = 0;
                        return;
                    }

                    string validationError;
                    if (!ValidateAerodynamicModelJson(dialog.FileName, out validationError))
                    {
                        CustomMessageBox.Show(
                            "This is not a valid ArduPilot plane aerodynamic model.\n\n" + validationError,
                            Strings.ERROR);
                        cmbPhysicsProfile.SelectedIndex = 0;
                        return;
                    }

                    string customDirectory = Path.Combine(sitldirectory, "models", "custom");
                    Directory.CreateDirectory(customDirectory);
                    string destination = Path.Combine(customDirectory,
                        MakeSafeFileName(Path.GetFileName(dialog.FileName)));
                    if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destination),
                            PathComparison))
                    {
                        File.Copy(dialog.FileName, destination, true);
                    }

                    customAerodynamicModelPath = destination;
                }
            }

            Settings.Instance["sitl_physics_profile"] = cmbPhysicsProfile.SelectedIndex.ToString(
                CultureInfo.InvariantCulture);
        }

        internal static bool ValidateAerodynamicModelJson(string path, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    error = "The model file does not exist.";
                    return false;
                }

                JObject model = JObject.Parse(File.ReadAllText(path));
                string[] required = { "s", "b", "c", "c_lift_a", "c_drag_p", "c_m_a" };
                foreach (string name in required)
                {
                    JToken token = model.GetValue(name, StringComparison.OrdinalIgnoreCase);
                    double value;
                    if (token == null || !double.TryParse(token.ToString(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value) || double.IsNaN(value) ||
                        double.IsInfinity(value))
                    {
                        error = "Missing or invalid numeric coefficient: " + name;
                        return false;
                    }
                }

                double wingArea = model.GetValue("s", StringComparison.OrdinalIgnoreCase).Value<double>();
                double wingSpan = model.GetValue("b", StringComparison.OrdinalIgnoreCase).Value<double>();
                double chord = model.GetValue("c", StringComparison.OrdinalIgnoreCase).Value<double>();
                if (wingArea <= 0 || wingArea > 100 || wingSpan <= 0 || wingSpan > 50 ||
                    chord <= 0 || chord > 10)
                {
                    error = "Wing area, span, or chord is outside a safe simulation range.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string MakeSafeFileName(string value)
        {
            string fileName = Path.GetFileName(value ?? string.Empty);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(fileName) ? "airframe.json" : fileName;
        }

        private string FindSitlManifest()
        {
            string runningDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string sourceDirectory = Path.GetFullPath(Path.Combine(runningDirectory, "..", "..", ".."));
            string[] candidates =
            {
                Path.Combine(sitldirectory, VehicleInfoManifestName),
                string.IsNullOrWhiteSpace(BundledPath)
                    ? string.Empty
                    : Path.Combine(BundledPath, VehicleInfoManifestName),
                Path.Combine(runningDirectory, "sitl", VehicleInfoManifestName),
                Path.Combine(sourceDirectory, "sitl", VehicleInfoManifestName)
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private void PopulateBundledModelFrames()
        {
            string selected = cmb_model.Text;
            IReadOnlyList<string> frames = GetBundledFrameNames(FindSitlManifest());
            if (frames.Count == 0)
            {
                return;
            }

            cmb_model.BeginUpdate();
            try
            {
                cmb_model.Items.Clear();
                cmb_model.Items.Add(string.Empty);
                foreach (string frame in frames)
                {
                    cmb_model.Items.Add(frame);
                }

                int selectedIndex = cmb_model.FindStringExact(selected);
                cmb_model.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            }
            finally
            {
                cmb_model.EndUpdate();
            }
        }

        internal static IReadOnlyList<string> GetBundledFrameNames(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                return manifest.Properties()
                    .Select(property => GetPropertyValue(property.Value as JObject, "frames") as JObject)
                    .Where(frames => frames != null)
                    .SelectMany(frames => frames.Properties())
                    .Where(frame =>
                    {
                        JObject definition = frame.Value as JObject;
                        JToken external = GetPropertyValue(definition, "external");
                        return external == null || external.Type != JTokenType.Boolean || !external.Value<bool>();
                    })
                    .Select(frame => frame.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(frame => frame, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                log.Warn("Unable to populate bundled SITL frame list", ex);
                return Array.Empty<string>();
            }
        }

        private void ApplyModernLayout()
        {
            SuspendLayout();
            try
            {
                Controls.Clear();
                BackColor = ModernUi.Canvas;
                Padding = new Padding(14);

                var workspace = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1,
                    BackColor = ModernUi.Canvas,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty
                };
                workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
                workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
                workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

                myGMAP1.Dock = DockStyle.Fill;
                Control mapSection = CreateSimulationSection(groupBox1.Text, myGMAP1);
                mapSection.Margin = new Padding(0, 0, 12, 0);
                workspace.Controls.Add(mapSection, 0, 0);

                var controlsColumn = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 3,
                    BackColor = ModernUi.Canvas,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty
                };
                controlsColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                controlsColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
                controlsColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 16F));
                controlsColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));

                Control vehicles = CreateVehicleSelector();
                Control options = CreateSimulationOptions();
                Control advanced = CreateAdvancedSimulationOptions();
                controlsColumn.Controls.Add(CreateSimulationSection(groupBox2.Text, vehicles), 0, 0);
                controlsColumn.Controls.Add(CreateSimulationSection(groupBox3.Text, options), 0, 1);
                controlsColumn.Controls.Add(CreateSimulationSection(groupBox4.Text, advanced), 0, 2);
                controlsColumn.GetControlFromPosition(0, 0).Margin = new Padding(0, 0, 0, 10);
                controlsColumn.GetControlFromPosition(0, 1).Margin = new Padding(0, 0, 0, 10);

                workspace.Controls.Add(controlsColumn, 1, 0);
                Controls.Add(workspace);
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private Control CreateVehicleSelector()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = ModernUi.Surface,
                Margin = Padding.Empty,
                Padding = new Padding(8)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            AddVehicleTile(grid, pictureBoxplane, label6, 0, 0);
            AddVehicleTile(grid, pictureBoxrover, label5, 1, 0);
            AddVehicleTile(grid, pictureBoxquad, label4, 0, 1);
            AddVehicleTile(grid, pictureBoxheli, label3, 1, 1);
            return grid;
        }

        private static void AddVehicleTile(TableLayoutPanel grid, PictureBox picture, Label label, int column, int row)
        {
            var tile = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ModernUi.SurfaceRaised,
                Margin = new Padding(5),
                Padding = new Padding(10, 8, 10, 6)
            };
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.Cursor = Cursors.Hand;
            picture.BackColor = Color.Transparent;
            label.Dock = DockStyle.Bottom;
            label.Height = 28;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.ForeColor = ModernUi.TextPrimary;
            label.Font = new Font(ModernUi.UiFontFamily, 9F, FontStyle.Bold);
            tile.Controls.Add(picture);
            tile.Controls.Add(label);
            grid.Controls.Add(tile, column, row);
        }

        private Control CreateSimulationOptions()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = ModernUi.Surface,
                Padding = new Padding(12, 10, 12, 8),
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            label1.Dock = DockStyle.Fill;
            label1.TextAlign = ContentAlignment.MiddleLeft;
            NUM_heading.Dock = DockStyle.Fill;
            NUM_heading.Margin = new Padding(4, 8, 12, 8);
            cmb_version.Dock = DockStyle.Fill;
            cmb_version.Margin = new Padding(4, 8, 0, 8);
            var firmwareLabel = new Label
            {
                Text = "Firmware",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ModernUi.TextSecondary
            };

            layout.Controls.Add(label1, 0, 0);
            layout.Controls.Add(NUM_heading, 1, 0);
            layout.Controls.Add(firmwareLabel, 2, 0);
            layout.Controls.Add(cmb_version, 3, 0);
            return layout;
        }

        private Control CreateAdvancedSimulationOptions()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = ModernUi.Surface,
                Padding = new Padding(12, 9, 12, 10),
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var primaryOptions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            primaryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
            primaryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17F));
            primaryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
            primaryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36F));
            primaryOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));

            PrepareInlineLabel(label2);
            PrepareInlineLabel(label7);
            num_simspeed.Dock = DockStyle.Fill;
            num_simspeed.Margin = new Padding(4, 6, 10, 6);
            cmb_model.Dock = DockStyle.Fill;
            cmb_model.Margin = new Padding(4, 6, 8, 6);
            chk_wipe.Dock = DockStyle.Fill;
            chk_wipe.TextAlign = ContentAlignment.MiddleLeft;
            primaryOptions.Controls.Add(label2, 0, 0);
            primaryOptions.Controls.Add(num_simspeed, 1, 0);
            primaryOptions.Controls.Add(label7, 2, 0);
            primaryOptions.Controls.Add(cmb_model, 3, 0);
            primaryOptions.Controls.Add(chk_wipe, 4, 0);

            var commandLine = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            commandLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
            commandLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72F));
            PrepareInlineLabel(label8);
            txt_cmdline.Dock = DockStyle.Fill;
            txt_cmdline.Margin = new Padding(4, 6, 0, 6);
            commandLine.Controls.Add(label8, 0, 0);
            commandLine.Controls.Add(txt_cmdline, 1, 0);

            var physicsOptions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            physicsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13F));
            physicsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
            physicsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
            physicsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13F));
            physicsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
            physicsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 11F));
            physicsOptions.Controls.Add(lblPhysicsProfile, 0, 0);
            physicsOptions.Controls.Add(cmbPhysicsProfile, 1, 0);
            physicsOptions.Controls.Add(lblWindSpeed, 2, 0);
            physicsOptions.Controls.Add(numWindSpeed, 3, 0);
            physicsOptions.Controls.Add(lblWindTurbulence, 4, 0);
            physicsOptions.Controls.Add(numWindTurbulence, 5, 0);

            var swarmButtons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0, 7, 0, 0),
                Padding = Padding.Empty
            };
            swarmButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            swarmButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            swarmButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            swarmButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            AddSwarmButton(swarmButtons, but_swarmlink, 0, 0);
            AddSwarmButton(swarmButtons, but_swarmseq, 1, 0);
            AddSwarmButton(swarmButtons, but_swarmplane, 0, 1);
            AddSwarmButton(swarmButtons, but_swarmrover, 1, 1);

            layout.Controls.Add(primaryOptions, 0, 0);
            layout.Controls.Add(commandLine, 0, 1);
            layout.Controls.Add(physicsOptions, 0, 2);
            layout.Controls.Add(swarmButtons, 0, 3);
            return layout;
        }

        private static void PrepareInlineLabel(Label label)
        {
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = ModernUi.TextSecondary;
        }

        private static void AddSwarmButton(TableLayoutPanel layout, MyButton button, int column, int row)
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(4);
            layout.Controls.Add(button, column, row);
        }

        private static Control CreateSimulationSection(string title, Control content)
        {
            var section = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ModernUi.Surface,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            var heading = new Label
            {
                Dock = DockStyle.Top,
                Height = 38,
                Text = title,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                BackColor = ModernUi.SurfaceRaised,
                ForeColor = ModernUi.TextPrimary,
                Font = new Font(ModernUi.UiFontFamily, 9.5F, FontStyle.Bold)
            };
            content.Dock = DockStyle.Fill;
            section.Controls.Add(content);
            section.Controls.Add(heading);
            return section;
        }

        public void Activate()
        {
            if(MainV2.comPort.MAV.cs.PlannedHomeLocation.Lat == 0 && MainV2.comPort.MAV.cs.PlannedHomeLocation.Lng == 0)
                homemarker.Position = new PointLatLng(-35.3633515, 149.1652412);
            else
                homemarker.Position = MainV2.comPort.MAV.cs.PlannedHomeLocation;

            myGMAP1.Position = homemarker.Position;

            myGMAP1.MapProvider = GCSViews.FlightData.mymap.MapProvider;
            myGMAP1.MaxZoom = 22;
            myGMAP1.Zoom = 16;
            myGMAP1.DisableFocusOnMouseEnter = true;

            markeroverlay = new GMapOverlay("markers");
            myGMAP1.Overlays.Add(markeroverlay);

            markeroverlay.Markers.Add(homemarker);

            myGMAP1.Invalidate();

            Utilities.ThemeManager.ApplyThemeTo(this);

            MissionPlanner.Utilities.Tracking.AddPage(this.GetType().ToString(), this.Text);
        }

        private async void pictureBoxplane_Click(object sender, EventArgs e)
        {

            var exepath = CheckandGetSITLImage("ArduPlane.elf");

            if (markeroverlay.Markers.Count == 0)
            {
                CustomMessageBox.Show(Strings.Invalid_home_location);
                return;
            }

            try
            {
                await StartSITL(await exepath, "plane",
                    BuildHomeLocation(markeroverlay.Markers[0].Position, (int) NUM_heading.Value), "",
                    (int) num_simspeed.Value);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Failed to download and start sitl\n" + ex.ToString());
            }
        }

        private async void pictureBoxrover_Click(object sender, EventArgs e)
        {
            if (markeroverlay.Markers.Count == 0)
            {
                CustomMessageBox.Show(Strings.Invalid_home_location);
                return;
            }

            var exepath = CheckandGetSITLImage("ArduRover.elf");
            try
            {
                await StartSITL(await exepath, "rover",
                    BuildHomeLocation(markeroverlay.Markers[0].Position, (int) NUM_heading.Value), "",
                    (int) num_simspeed.Value);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Failed to download and start sitl\n" + ex.ToString());
            }
        }

        private async void pictureBoxquad_Click(object sender, EventArgs e)
        {
            if (markeroverlay.Markers.Count == 0)
            {
                CustomMessageBox.Show(Strings.Invalid_home_location);
                return;
            }

            var exepath = CheckandGetSITLImage("ArduCopter.elf");
            try
            {
                await StartSITL(await exepath, "+",
                    BuildHomeLocation(markeroverlay.Markers[0].Position, (int) NUM_heading.Value), "",
                    (int) num_simspeed.Value);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Failed to download and start sitl\n" + ex.ToString());
            }
        }

        private async void pictureBoxheli_Click(object sender, EventArgs e)
        {
            if (markeroverlay.Markers.Count == 0)
            {
                CustomMessageBox.Show(Strings.Invalid_home_location);
                return;
            }

            var exepath = CheckandGetSITLImage("ArduHeli.elf");
            try
            {
                await StartSITL(await exepath, "heli",
                    BuildHomeLocation(markeroverlay.Markers[0].Position, (int) NUM_heading.Value), "",
                    (int) num_simspeed.Value);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Failed to download and start sitl\n" + ex.ToString());
            }
        }

        string BuildHomeLocation(PointLatLng homelocation, int heading = 0)
        {
            return String.Format("{0},{1},{2},{3}", homelocation.Lat.ToString(CultureInfo.InvariantCulture), homelocation.Lng.ToString(CultureInfo.InvariantCulture),
                srtm.getAltitude(homelocation.Lat, homelocation.Lng).alt.ToString(CultureInfo.InvariantCulture), heading.ToString(CultureInfo.InvariantCulture));
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string pathname, int mode);

        // user permissions
        const int S_IRUSR = 0x100;
        const int S_IWUSR = 0x80;
        const int S_IXUSR = 0x40;

        // group permission
        const int S_IRGRP = 0x20;
        const int S_IWGRP = 0x10;
        const int S_IXGRP = 0x8;

        // other permissions
        const int S_IROTH = 0x4;
        const int S_IWOTH = 0x2;
        const int S_IXOTH = 0x1;

        private string TryGetLocalSITLImage(string filename)
        {
            var imageName = Path.GetFileNameWithoutExtension(filename);
            var checks = new[]
            {
                "{0}.exe",
                "{0}",
                "{0}.elf"
            };

            foreach (var template in checks)
            {
                var localPath = Path.Combine(sitldirectory, string.Format(template, imageName));
                if (File.Exists(localPath) && HasSITLDependencies(Path.GetDirectoryName(localPath)))
                {
                    return localPath;
                }

                localPath = Path.Combine(sitldirectory, string.Format(template, imageName).ToLowerInvariant());
                if (File.Exists(localPath) && HasSITLDependencies(Path.GetDirectoryName(localPath)))
                {
                    return localPath;
                }
            }

            return "";
        }

        internal static bool HasSITLDependencies(string directory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return true;
            }

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            return SitlDependencyFiles.All(file => File.Exists(Path.Combine(directory, file)));
        }

        /// <summary>
        /// Try BundlePath first, then arm manifest, then cygwin on server
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        private async Task<string> CheckandGetSITLImage(string filename)
        {
            // Save the selected version for next time
            Settings.Instance["sitl_download_version"] = cmb_version.SelectedIndex.ToString();
            var release_type = cmb_version.SelectedValue as APFirmware.RELEASE_TYPES?;
            if (BundledPath != "")
            {
                filename = filename.Replace(".elf", "");
                var file = filename;
                if (!File.Exists(BundledPath + System.IO.Path.DirectorySeparatorChar + file))
                {
                    string[] checks = new string[] { "{0}", "{0}.exe", "lib{0}.so", "{0}.so", "{0}.elf" };

                    foreach (var template in checks)
                    {
                        file = String.Format(template, filename);
                        log.Info("try path " + BundledPath + System.IO.Path.DirectorySeparatorChar + file);
                        if (File.Exists(BundledPath + System.IO.Path.DirectorySeparatorChar + file))
                        {
                            return BundledPath + System.IO.Path.DirectorySeparatorChar + file;
                        }
                        file = file.ToLower();
                        log.Info("try path " + BundledPath + System.IO.Path.DirectorySeparatorChar + file);
                        if (File.Exists(BundledPath + System.IO.Path.DirectorySeparatorChar + file))
                        {
                            return BundledPath + System.IO.Path.DirectorySeparatorChar + file;
                        }
                    }
                }

                return "";
            }

            var localImage = TryGetLocalSITLImage(filename);
            if (!string.IsNullOrEmpty(localImage))
            {
                log.Info("Using local SITL image " + localImage);
                return localImage;
            }

            if ((RuntimeInformation.OSArchitecture == Architecture.X64 ||
              RuntimeInformation.OSArchitecture == Architecture.X86) && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var type = APFirmware.MAV_TYPE.Copter;
                if (filename.ToLower().Contains("copter"))
                    type = APFirmware.MAV_TYPE.Copter;
                if (filename.ToLower().Contains("plane"))
                    type = APFirmware.MAV_TYPE.FIXED_WING;
                if (filename.ToLower().Contains("rover"))
                    type = APFirmware.MAV_TYPE.GROUND_ROVER;
                if (filename.ToLower().Contains("heli"))
                    type = APFirmware.MAV_TYPE.HELICOPTER;

                var fw = APFirmware.GetOptions(new DeviceInfo() { board = "", hardwareid = "" }, release_type, type);
                fw = fw.Where(a => a.Platform == "SITL_x86_64_linux_gnu").ToList();
                if (fw.Count > 0)
                {
                    var path = sitldirectory + Path.GetFileNameWithoutExtension(filename);
                    if (release_type.HasValue)
                    {
                        Download.getFilefromNet(fw.First().Url.AbsoluteUri, path);
                        try
                        {
                            int _0755 = S_IRUSR | S_IXUSR | S_IWUSR
                                | S_IRGRP | S_IXGRP
                                | S_IROTH | S_IXOTH;

                            chmod(path, _0755);
                        }
                        catch (Exception ex)
                        {
                            log.Error(ex);
                        }
                    }
                    return path;
                }
            }

            if (RuntimeInformation.OSArchitecture == Architecture.Arm ||
               RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                var type = APFirmware.MAV_TYPE.Copter;
                if (filename.ToLower().Contains("copter"))
                    type = APFirmware.MAV_TYPE.Copter;
                if (filename.ToLower().Contains("plane"))
                    type = APFirmware.MAV_TYPE.FIXED_WING;
                if (filename.ToLower().Contains("rover"))
                    type = APFirmware.MAV_TYPE.GROUND_ROVER;
                if (filename.ToLower().Contains("heli"))
                    type = APFirmware.MAV_TYPE.HELICOPTER;

                var fw = APFirmware.GetOptions(new DeviceInfo() { board = "", hardwareid="" }, release_type, type);
                fw = fw.Where(a => a.Platform == "SITL_arm_linux_gnueabihf").ToList();
                if (fw.Count > 0)
                {
                    var path = sitldirectory + Path.GetFileNameWithoutExtension(filename);
                    if (release_type.HasValue)
                    {
                        Download.getFilefromNet(fw.First().Url.AbsoluteUri, path);
                        try {
                            int _0755 =            S_IRUSR | S_IXUSR | S_IWUSR
                                | S_IRGRP | S_IXGRP
                                | S_IROTH | S_IXOTH;

                            chmod(path, _0755);
                        }
                        catch (Exception ex)
                        {
                            log.Error(ex);
                        }
                    }
                    return path;
                }
            }

            if (release_type.HasValue)
            {
                // kill old session - so we can overwrite if needed
                try
                {
                    simulator.ForEach(a =>
                    {
                        try
                        {
                            a.Kill();
                        }
                        catch { }
                    });
                }
                catch
                {
                }

                var url = sitlmasterurl;

                if (release_type == APFirmware.RELEASE_TYPES.DEV)
                {
                    // master by default
                }
                else if (release_type == APFirmware.RELEASE_TYPES.BETA)
                {
                    url = sitlbetaurl;
                }
                else if (release_type == APFirmware.RELEASE_TYPES.OFFICIAL)
                {
                    if (filename.ToLower().Contains("copter"))
                        url = sitlcopterstableurl;
                    if (filename.ToLower().Contains("rover"))
                        url = sitlroverstableurl;
                    if (filename.ToLower().Contains("plane"))
                        url = sitlplanestableurl;
                    if (filename.ToLower().Contains("heli"))
                        url = sitlcopterstableurl;
                } else
                {
                    return null;
                }

                Uri fullurl = new Uri(url, filename);

                var load = Common.LoadingBox("Downloading", "Downloading sitl software");

                var t1 = Download.getFilefromNetAsync(fullurl.ToString(),
                    sitldirectory + Path.GetFileNameWithoutExtension(filename) + ".exe");

                load.Refresh();

                // dependancys

                Parallel.ForEach(SitlDependencyFiles, new ParallelOptions() { MaxDegreeOfParallelism = 2 }, (a, b) =>
                {
                    var depurl = new Uri(url, a);
                    var t2 = Download.getFilefromNet(depurl.ToString(), sitldirectory + depurl.Segments[depurl.Segments.Length - 1]);
                });

                await t1;

                load.Close();
            }

            return sitldirectory + Path.GetFileNameWithoutExtension(filename) + ".exe";
        }

        private async Task<string> GetDefaultConfig(string model, string executablePath)
        {
            string manifestPath = Path.Combine(sitldirectory, VehicleInfoManifestName);
            IReadOnlyList<string> configuredFiles = ResolveDefaultParameterFiles(
                manifestPath,
                sitldirectory,
                model,
                executablePath);
            var availableFiles = new List<string>();

            foreach (string configPath in configuredFiles)
            {
                if (!File.Exists(configPath))
                {
                    string relativePath = GetSafeRelativePath(sitldirectory, configPath);
                    if (!string.IsNullOrEmpty(relativePath))
                    {
                        try
                        {
                            string directory = Path.GetDirectoryName(configPath);
                            if (!string.IsNullOrEmpty(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }

                            string source =
                                "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/autotest/" +
                                relativePath.Replace(Path.DirectorySeparatorChar, '/');
                            await Download.getFilefromNetAsync(source, configPath);
                        }
                        catch (Exception ex)
                        {
                            log.Warn("Unable to download SITL defaults " + relativePath, ex);
                        }
                    }
                }

                if (File.Exists(configPath))
                {
                    availableFiles.Add(configPath);
                }
            }

            if (availableFiles.Count == 0)
            {
                log.WarnFormat("No SITL default parameter file is available for model {0} ({1})", model,
                    executablePath);
            }

            return string.Join(",", availableFiles);
        }

        internal static IReadOnlyList<string> ResolveDefaultParameterFiles(
            string manifestPath,
            string sitlRoot,
            string model,
            string executablePath)
        {
            var relativePaths = new List<string>();

            if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
            {
                try
                {
                    JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                    JObject frame = FindFrameConfiguration(manifest, model, executablePath);
                    JToken defaults = frame == null ? null : GetPropertyValue(frame, "default_params_filename");

                    if (defaults != null && defaults.Type == JTokenType.String)
                    {
                        relativePaths.Add(defaults.Value<string>());
                    }
                    else if (defaults is JArray defaultsArray)
                    {
                        relativePaths.AddRange(defaultsArray
                            .Where(item => item.Type == JTokenType.String)
                            .Select(item => item.Value<string>()));
                    }
                }
                catch (Exception ex)
                {
                    log.Warn("Unable to read the bundled SITL vehicle manifest", ex);
                }
            }

            if (relativePaths.Count == 0)
            {
                relativePaths.Add(GetFallbackDefaultParameterPath(model, executablePath));
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(sitlRoot ?? string.Empty)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
            }
            catch
            {
                return Array.Empty<string>();
            }

            var resolved = new List<string>();
            foreach (string relativePath in relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    string normalizedRelative = relativePath
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    string[] pathSegments = normalizedRelative.Split(
                        new[] { Path.DirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries);
                    if (Path.IsPathRooted(normalizedRelative) ||
                        pathSegments.Any(segment => segment == ".."))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelative));
                    if (!fullPath.StartsWith(normalizedRoot, PathComparison))
                    {
                        continue;
                    }

                    if (!resolved.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    {
                        resolved.Add(fullPath);
                    }
                }
                catch
                {
                    // Ignore malformed or escaping paths from an external manifest.
                }
            }

            return resolved;
        }

        private static StringComparison PathComparison =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static JObject FindFrameConfiguration(JObject manifest, string model, string executablePath)
        {
            string preferredSection = GetPreferredVehicleSection(model, executablePath);
            var sections = new List<JObject>();
            JObject preferred = GetPropertyValue(manifest, preferredSection) as JObject;
            if (preferred != null)
            {
                sections.Add(preferred);
            }

            sections.AddRange(manifest.Properties()
                .Where(property => !string.Equals(property.Name, preferredSection, StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Value as JObject)
                .Where(section => section != null));

            foreach (JObject section in sections)
            {
                JObject frames = GetPropertyValue(section, "frames") as JObject;
                if (frames == null)
                {
                    continue;
                }

                string requestedModel = model;
                if (string.IsNullOrWhiteSpace(requestedModel))
                {
                    requestedModel = GetPropertyValue(section, "default_frame")?.Value<string>();
                }

                JObject exact = GetPropertyValue(frames, requestedModel) as JObject;
                if (exact != null)
                {
                    return exact;
                }

                string prefix = FramePrefixFallbacks.FirstOrDefault(candidate =>
                    !string.IsNullOrEmpty(requestedModel) &&
                    requestedModel.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(prefix))
                {
                    JObject prefixMatch = GetPropertyValue(frames, prefix) as JObject;
                    if (prefixMatch != null)
                    {
                        return prefixMatch;
                    }
                }

                if (!string.IsNullOrEmpty(requestedModel) &&
                    requestedModel.EndsWith("-heli", StringComparison.OrdinalIgnoreCase))
                {
                    JObject helicopter = GetPropertyValue(frames, "heli") as JObject;
                    if (helicopter != null)
                    {
                        return helicopter;
                    }
                }
            }

            return null;
        }

        private static JToken GetPropertyValue(JObject value, string propertyName)
        {
            if (value == null || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            return value.Properties()
                .FirstOrDefault(property =>
                    string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        private static string GetPreferredVehicleSection(string model, string executablePath)
        {
            string executable = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty) ?? string.Empty;
            if (executable.IndexOf("plane", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ArduPlane";
            }

            if (executable.IndexOf("rover", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Rover";
            }

            if (executable.IndexOf("heli", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Helicopter";
            }

            if (executable.IndexOf("copter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ArduCopter";
            }

            if (!string.IsNullOrEmpty(model) && model.IndexOf("rover", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Rover";
            }

            if (!string.IsNullOrEmpty(model) && model.StartsWith("heli", StringComparison.OrdinalIgnoreCase))
            {
                return "Helicopter";
            }

            if (!string.IsNullOrEmpty(model) &&
                (model.StartsWith("plane", StringComparison.OrdinalIgnoreCase) ||
                 model.StartsWith("quadplane", StringComparison.OrdinalIgnoreCase)))
            {
                return "ArduPlane";
            }

            return "ArduCopter";
        }

        private static string GetFallbackDefaultParameterPath(string model, string executablePath)
        {
            switch (GetPreferredVehicleSection(model, executablePath))
            {
                case "ArduPlane":
                    return "models/plane.parm";
                case "Rover":
                    return "default_params/rover.parm";
                case "Helicopter":
                    return "default_params/copter-heli.parm";
                default:
                    return "default_params/copter.parm";
            }
        }

        private static string GetSafeRelativePath(string root, string fullPath)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                string normalizedPath = Path.GetFullPath(fullPath);
                return normalizedPath.StartsWith(normalizedRoot, PathComparison)
                    ? normalizedPath.Substring(normalizedRoot.Length)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        internal static string BuildSITLStateFingerprint(string executablePath, string defaultConfig)
        {
            var identity = new StringBuilder(SitlStateSchema);
            AppendFileIdentity(identity, executablePath);
            foreach (string configPath in (defaultConfig ?? string.Empty)
                         .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AppendFileIdentity(identity, configPath.Trim());
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString()));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void AppendFileIdentity(StringBuilder identity, string path)
        {
            identity.Append('|').Append(path ?? string.Empty);
            try
            {
                var file = new FileInfo(path ?? string.Empty);
                identity.Append('|').Append(file.Exists ? file.Length : -1)
                    .Append('|').Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
            }
            catch
            {
                identity.Append("|-1|0");
            }
        }

        internal static bool ShouldResetSITLState(string simulationDirectory, string fingerprint)
        {
            try
            {
                string markerPath = Path.Combine(simulationDirectory, SitlStateMarkerName);
                return !File.Exists(markerPath) ||
                       !string.Equals(File.ReadAllText(markerPath).Trim(), fingerprint,
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        internal static void WriteSITLStateFingerprint(string simulationDirectory, string fingerprint)
        {
            Directory.CreateDirectory(simulationDirectory);
            File.WriteAllText(Path.Combine(simulationDirectory, SitlStateMarkerName), fingerprint + Environment.NewLine);
        }

        private static async Task<TcpClient> ConnectToSITLAsync(Process process, int port, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            Exception lastError = null;

            while (DateTime.UtcNow < deadline)
            {
                if (process == null || HasProcessExited(process))
                {
                    throw new InvalidOperationException("The SITL process exited before opening its MAVLink port.");
                }

                var tcpClient = new TcpClient();
                try
                {
                    await tcpClient.ConnectAsync("127.0.0.1", port);
                    return tcpClient;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    tcpClient.Close();
                    await Task.Delay(250);
                }
            }

            throw new TimeoutException("SITL did not open TCP port " + port + " before the startup timeout.",
                lastError);
        }

        private static async Task<bool> WaitForSITLReadyAsync(CurrentState state, Process process, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                if (process == null || HasProcessExited(process))
                {
                    return false;
                }

                if (IsSITLReady(state))
                {
                    return true;
                }

                await Task.Delay(250);
            }

            return false;
        }

        internal static bool IsSITLReady(CurrentState state)
        {
            return VehicleTelemetryValidation.HasUsablePosition(state) &&
                   state.sensors_health.gyro &&
                   state.sensors_health.accelerometer &&
                   state.sensors_health.ahrs;
        }

        private static bool HasProcessExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        internal static bool IsTransientSITLStartupMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("Unhealthy AHRS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("AHRS: waiting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("EKF", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   message.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsFrameCompatibleWithExecutable(
            string manifestPath,
            string model,
            string executablePath)
        {
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(manifestPath) ||
                !File.Exists(manifestPath))
            {
                return true;
            }

            try
            {
                string baseModel = model.Split(':')[0];
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JObject section = GetPropertyValue(
                    manifest,
                    GetPreferredVehicleSection(baseModel, executablePath)) as JObject;
                JObject frames = GetPropertyValue(section, "frames") as JObject;
                if (frames == null)
                {
                    return false;
                }

                if (GetPropertyValue(frames, baseModel) != null)
                {
                    return true;
                }

                return frames.Properties().Any(frame =>
                    baseModel.StartsWith(frame.Name + "-", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        internal static string GetSimulationDirectoryName(string model, string executablePath)
        {
            string executable = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty);
            string identity = (executable + "-" + (model ?? string.Empty)).Trim('-');
            var safe = new StringBuilder(identity.Length);
            foreach (char value in identity)
            {
                safe.Append(char.IsLetterOrDigit(value) || value == '-' || value == '_'
                    ? value
                    : '_');
            }

            string result = safe.ToString().Trim('_');
            if (result.Length > 64)
            {
                using (SHA256 sha = SHA256.Create())
                {
                    string suffix = BitConverter.ToString(
                            sha.ComputeHash(Encoding.UTF8.GetBytes(identity)))
                        .Replace("-", string.Empty)
                        .Substring(0, 12)
                        .ToLowerInvariant();
                    result = result.Substring(0, 48).TrimEnd('_', '-') + "-" + suffix;
                }
            }

            return string.IsNullOrWhiteSpace(result) ? "simulation" : result;
        }

        internal static string QuoteCommandLineValue(string value)
        {
            value = value ?? string.Empty;
            if (value.Length > 0 && value.All(character =>
                    !char.IsWhiteSpace(character) && character != '"'))
            {
                return value;
            }

            // Follow the Windows CommandLineToArgvW escaping rules. Backslashes in
            // normal paths are preserved; only runs before a quote or the closing
            // delimiter need doubling.
            var quoted = new StringBuilder(value.Length + 2);
            quoted.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(character);
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        internal static string BuildSITLArguments(
            string model,
            string homeLocation,
            int speedup,
            int simulationRate,
            string extraArguments)
        {
            var arguments = new StringBuilder();
            arguments.Append("-M").Append(QuoteCommandLineValue(model));
            arguments.Append(" -O").Append(QuoteCommandLineValue(homeLocation));
            arguments.Append(" -s").Append(Math.Max(1, speedup).ToString(CultureInfo.InvariantCulture));
            if (simulationRate > 0)
            {
                arguments.Append(" --rate ")
                    .Append(simulationRate.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Append(" --serial0 tcp:0");
            if (!string.IsNullOrWhiteSpace(extraArguments))
            {
                arguments.Append(' ').Append(extraArguments.Trim());
            }

            return arguments.ToString();
        }

        private PhysicsProfile SelectedPhysicsProfile =>
            cmbPhysicsProfile?.SelectedItem as PhysicsProfile;

        private string CreatePhysicsDefaults(string simulationDirectory, PhysicsProfile profile)
        {
            bool highFidelity = profile != null &&
                                !string.Equals(profile.Name, "ArduPilot native default",
                                    StringComparison.Ordinal);
            double windSpeed = (double)numWindSpeed.Value;
            double windTurbulence = (double)numWindTurbulence.Value;
            if (!highFidelity && windSpeed <= 0 && windTurbulence <= 0)
            {
                return null;
            }

            var contents = new StringBuilder();
            contents.AppendLine("# DIMP generated SITL physics profile");
            if (highFidelity)
            {
                contents.AppendLine("SIM_SERVO_SPEED 0.14");
                contents.AppendLine("SIM_SERVO_DELAY 0.015");
                contents.AppendLine("SIM_SERVO_FILTER 12");
            }

            contents.Append("SIM_WIND_SPD ")
                .AppendLine(windSpeed.ToString("0.0", CultureInfo.InvariantCulture));
            contents.Append("SIM_WIND_TURB ")
                .AppendLine(windTurbulence.ToString("0.0", CultureInfo.InvariantCulture));

            string profilePath = Path.Combine(simulationDirectory, "dimp-physics.parm");
            string newContents = contents.ToString();
            if (!File.Exists(profilePath) ||
                !string.Equals(File.ReadAllText(profilePath), newContents, StringComparison.Ordinal))
            {
                File.WriteAllText(profilePath, newContents);
            }

            return profilePath;
        }

        private string PrepareAerodynamicModel(
            string simulationDirectory,
            string model,
            string executablePath,
            PhysicsProfile profile)
        {
            if (profile == null || !profile.UsesAerodynamicJson ||
                GetPreferredVehicleSection(model, executablePath) != "ArduPlane")
            {
                return model;
            }

            string source = profile.SelectsCustomModel
                ? customAerodynamicModelPath
                : Path.Combine(sitldirectory, "models", "skywalker_2013.json");
            string validationError;
            if (!ValidateAerodynamicModelJson(source, out validationError))
            {
                throw new InvalidOperationException(
                    "The selected aerodynamic model is unavailable or invalid: " + validationError);
            }

            string destination = Path.Combine(simulationDirectory, "dimp-airframe.json");
            byte[] sourceBytes = File.ReadAllBytes(source);
            if (!File.Exists(destination) || !File.ReadAllBytes(destination).SequenceEqual(sourceBytes))
            {
                File.WriteAllBytes(destination, sourceBytes);
            }

            return model.Split(':')[0] + ":dimp-airframe.json";
        }

        private async Task StartSITL(string exepath, string model, string homelocation, string extraargs = "", int speedup = 1)
        {
            // A null image means the version selection was cancelled.
            if (exepath == null)
                return;

            if (String.IsNullOrEmpty(homelocation))
            {
                CustomMessageBox.Show(Strings.Invalid_home_location, Strings.ERROR);
                return;
            }

            if (!File.Exists(exepath))
            {
                CustomMessageBox.Show(Strings.Failed_to_download_the_SITL_image, Strings.ERROR);
                return;
            }

            // kill old session
            try
            {
                simulator.ForEach(a =>
                {
                    try
                    {
                        a.Kill();
                    }
                    catch { }
                });
                simulator.Clear();
            }
            catch
            {
            }

            try
            {
                SITLSEND?.Close();
                SITLSEND = null;
            }
            catch
            {
            }

            // override default model
            if (cmb_model.Text != "")
                model = cmb_model.Text;

            string manifestPath = FindSitlManifest();
            if (!IsFrameCompatibleWithExecutable(manifestPath, model, exepath))
            {
                CustomMessageBox.Show(
                    "The selected frame '" + model + "' does not belong to " +
                    Path.GetFileNameWithoutExtension(exepath) +
                    ". Select a matching frame or leave the frame field empty.",
                    Strings.ERROR);
                return;
            }

            var config = await GetDefaultConfig(model, exepath);

            if (string.IsNullOrEmpty(config))
            {
                CustomMessageBox.Show(
                    "SITL cannot start because its default parameter file is missing. Reinstall DIMP or restore the sitl defaults folder.",
                    Strings.ERROR);
                return;
            }

            string simdir = Path.Combine(
                sitldirectory,
                GetSimulationDirectoryName(model, exepath)) + Path.DirectorySeparatorChar;

            Directory.CreateDirectory(simdir);

            PhysicsProfile physicsProfile = SelectedPhysicsProfile;
            string launchModel;
            string physicsConfig;
            try
            {
                launchModel = PrepareAerodynamicModel(simdir, model, exepath, physicsProfile);
                physicsConfig = CreatePhysicsDefaults(simdir, physicsProfile);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(ex.Message, Strings.ERROR);
                return;
            }

            string combinedConfig = string.IsNullOrWhiteSpace(physicsConfig)
                ? config
                : config + "," + physicsConfig;
            extraargs += " --defaults " + QuoteCommandLineValue(combinedConfig);
            extraargs += " " + txt_cmdline.Text + " ";

            Settings.Instance["sitl_wind_speed"] = numWindSpeed.Value.ToString(
                CultureInfo.InvariantCulture);
            Settings.Instance["sitl_wind_turbulence"] = numWindTurbulence.Value.ToString(
                CultureInfo.InvariantCulture);

            string stateFingerprint = BuildSITLStateFingerprint(exepath, combinedConfig);
            bool automaticWipe = ShouldResetSITLState(simdir, stateFingerprint);
            if ((chk_wipe.Checked || automaticWipe) &&
                extraargs.IndexOf("--wipe", StringComparison.OrdinalIgnoreCase) < 0)
            {
                extraargs += " --wipe ";
            }

            if (automaticWipe)
            {
                log.Info("Resetting stale SITL state for " + model + " after a simulator/defaults update");
            }

            string path = Environment.GetEnvironmentVariable("PATH");

            Environment.SetEnvironmentVariable("PATH", sitldirectory + ";" + simdir + ";" + path, EnvironmentVariableTarget.Process);

            Environment.SetEnvironmentVariable("HOME", simdir, EnvironmentVariableTarget.Process);

            ProcessStartInfo exestart = new ProcessStartInfo();
            exestart.FileName = exepath;
            int simulationRate = physicsProfile != null &&
                                 !string.Equals(physicsProfile.Name, "ArduPilot native default",
                                     StringComparison.Ordinal)
                ? HighFidelitySimulationRate
                : 0;
            exestart.Arguments = BuildSITLArguments(
                launchModel,
                homelocation,
                speedup,
                simulationRate,
                extraargs);
            exestart.WorkingDirectory = simdir;
            exestart.WindowStyle = ProcessWindowStyle.Minimized;
            Console.WriteLine("sitl: {0} {1} {2}", exestart.WorkingDirectory, exestart.FileName,
                exestart.Arguments);
            Process sitlProcess = null;
            var client = new Comms.TcpSerial();
            Form startupBox = Common.LoadingBox(
                "Simulation",
                automaticWipe
                    ? "Preparing a clean simulator state and initializing AHRS/GPS..."
                    : "Starting the simulator and initializing AHRS/GPS...");

            try
            {
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                exestart.UseShellExecute = isWindows;
                if (!isWindows)
                {
                    exestart.RedirectStandardOutput = true;
                    exestart.RedirectStandardError = true;
                }

                sitlProcess = Process.Start(exestart);
                if (sitlProcess == null)
                {
                    throw new InvalidOperationException("The operating system did not create the SITL process.");
                }

                simulator.Add(sitlProcess);
                if (!isWindows)
                {
                    sitlProcess.EnableRaisingEvents = true;
                    sitlProcess.ErrorDataReceived +=
                        (sender, args) => { Console.WriteLine("SITL ERR: " + args.Data); };
                    sitlProcess.OutputDataReceived +=
                        (sender, args) => { Console.WriteLine("SITL: " + args.Data); };
                    sitlProcess.Exited += (sender, args) => { Console.WriteLine("SITL EXIT!"); };
                    sitlProcess.BeginOutputReadLine();
                    sitlProcess.BeginErrorReadLine();
                }

                WriteSITLStateFingerprint(simdir, stateFingerprint);

                client.client = await ConnectToSITLAsync(sitlProcess, 5760, TimeSpan.FromSeconds(12));

                MainV2.comPort.BaseStream = client;

                SITLSEND = new UdpClient("127.0.0.1", 5501);

                await Task.Delay(200);

                MainV2.instance.doConnect(MainV2.comPort, "preset", "5760");
                if (!MainV2.comPort.BaseStream.IsOpen)
                {
                    throw new InvalidOperationException("The MAVLink connection closed during SITL startup.");
                }

                bool ready = await WaitForSITLReadyAsync(
                    MainV2.comPort.MAV.cs,
                    sitlProcess,
                    TimeSpan.FromSeconds(55));

                if (ready && IsTransientSITLStartupMessage(MainV2.comPort.MAV.cs.messageHigh))
                {
                    MainV2.comPort.MAV.cs.messageHigh = string.Empty;
                }

                startupBox.Close();
                startupBox.Dispose();
                startupBox = null;

                MainV2.View.ShowScreen(MainV2.View.screens[0].Name);

                if (!ready)
                {
                    string status = MainV2.comPort.MAV.cs.messageHigh;
                    CustomMessageBox.Show(
                        "SITL connected, but AHRS/GPS did not become ready within 55 seconds." +
                        (string.IsNullOrWhiteSpace(status) ? string.Empty : "\n\nStatus: " + status),
                        Strings.ERROR);
                }
            }
            catch (Exception ex)
            {
                log.Error("Failed to start or connect to SITL", ex);
                try
                {
                    client.client?.Close();
                    if (sitlProcess != null && !HasProcessExited(sitlProcess))
                    {
                        sitlProcess.Kill();
                    }
                }
                catch
                {
                }

                startupBox?.Close();
                startupBox?.Dispose();
                startupBox = null;
                CustomMessageBox.Show(
                    Strings.Failed_to_connect_to_SITL_instance + "\n\n" + ex.Message,
                    Strings.ERROR);
            }
            finally
            {
                startupBox?.Close();
                startupBox?.Dispose();
            }
        }

        static internal void rcinput()
        {
            try
            {
                byte[] rcreceiver = new byte[2 * 8];
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech1), 0, rcreceiver, 0, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech2), 0, rcreceiver, 2, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech3), 0, rcreceiver, 4, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech4), 0, rcreceiver, 6, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech5), 0, rcreceiver, 8, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech6), 0, rcreceiver, 10, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech7), 0, rcreceiver, 12, 2);
                Array.ConstrainedCopy(BitConverter.GetBytes((ushort)MainV2.comPort.MAV.cs.rcoverridech8), 0, rcreceiver, 14, 2);

                SITLSEND.Send(rcreceiver, rcreceiver.Length);
            }
            catch
            {
            }
        }

        private void myGMAP1_OnMarkerEnter(GMapMarker item)
        {
            if (!mousedown)
                onmarker = true;
        }

        private void myGMAP1_OnMarkerLeave(GMapMarker item)
        {
            if (!mousedown)
                onmarker = false;
        }

        private void myGMAP1_MouseMove(object sender, MouseEventArgs e)
        {
            if (onmarker)
            {
                if (e.Button == MouseButtons.Left)
                {
                    homemarker.Position = myGMAP1.FromLocalToLatLng(e.X, e.Y);
                }
            }
            else if (mousedown)
            {
                PointLatLng point = myGMAP1.FromLocalToLatLng(e.X, e.Y);

                double latdif = MouseDownStart.Lat - point.Lat;
                double lngdif = MouseDownStart.Lng - point.Lng;

                try
                {
                    myGMAP1.Position = new PointLatLng(myGMAP1.Position.Lat + latdif, myGMAP1.Position.Lng + lngdif);
                }
                catch
                {
                }
            }
        }

        private void myGMAP1_MouseUp(object sender, MouseEventArgs e)
        {
            mousedown = false;
            onmarker = false;
        }

        private void myGMAP1_MouseDown(object sender, MouseEventArgs e)
        {
            mousedown = true;
            MouseDownStart = myGMAP1.FromLocalToLatLng(e.X, e.Y);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                StartSwarmChain();
                return true;
            }

            if (keyData == (Keys.Control | Keys.D))
            {
                _ = StartSwarmSeperate(Firmwares.ArduCopter2);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        public async Task StartSwarmSeperate(Firmwares firmware)
        {
            var max = 10;

            if (InputBox.Show("how many?", "how many?", ref max) != DialogResult.OK)
                return;

            // kill old session
            try
            {
                simulator.ForEach(a =>
                {
                    try
                    {
                        a.Kill();
                    }
                    catch { }
                });
            }
            catch
            {
            }
            Task<string> exepath;
            string model = "";
            if (firmware == Firmwares.ArduPlane)
            {
                exepath = CheckandGetSITLImage("ArduPlane.elf");
                model = "plane";
            } else
            if (firmware == Firmwares.ArduRover)
            {
                exepath = CheckandGetSITLImage("ArduRover.elf");
                model = "rover";
            }
            else // (firmware == Firmwares.ArduCopter2)
            {
                exepath = CheckandGetSITLImage("ArduCopter.elf");
                model = "+";
            }

            string executablePath = await exepath;
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                CustomMessageBox.Show(Strings.Failed_to_download_the_SITL_image, Strings.ERROR);
                return;
            }

            var config = await GetDefaultConfig(model, executablePath);
            if (string.IsNullOrEmpty(config))
            {
                CustomMessageBox.Show("SITL default parameter files are missing.", Strings.ERROR);
                return;
            }

            string stateFingerprint = BuildSITLStateFingerprint(executablePath, config);

            max--;

            for (int a = (int)max; a >= 0; a--)
            {
                var extra = " ";

                extra += @" --defaults """ + config + @",identity.parm"" ";

                var home = new PointLatLngAlt(markeroverlay.Markers[0].Position).newpos((double)NUM_heading.Value, a * 4);

                if (max == a)
                {
                    extra += String.Format(
			" -M{4} -s1 --home {3} --instance {0} --serial0 tcp:0 {1} ",
                        a, "", a + 1, BuildHomeLocation(home, (int)NUM_heading.Value), model);
                }
                else
                {
                    extra += String.Format(
			" -M{4} -s1 --home {3} --instance {0} --serial0 tcp:0 {1} ",
			a, "" /*"--serial2 tcpclient:127.0.0.1:" + (5770 + 10 * a)*/, a + 1,
                        BuildHomeLocation(home, (int)NUM_heading.Value), model);
                }

                string simdir = sitldirectory + model + (a + 1) + Path.DirectorySeparatorChar;

                Directory.CreateDirectory(simdir);

                bool resetState = ShouldResetSITLState(simdir, stateFingerprint);
                if (resetState)
                {
                    extra += " --wipe ";
                }

                File.WriteAllText(simdir + "identity.parm", String.Format(@"SERIAL0_PROTOCOL=2
SERIAL1_PROTOCOL=2
SYSID_THISMAV={0}
MAV_SYSID={0}
SIM_TERRAIN=0
TERRAIN_ENABLE=0
SCHED_LOOP_RATE=50
SIM_RATE_HZ=400
SIM_DRIFT_SPEED=0
SIM_DRIFT_TIME=0
", a + 1));

                string path = Environment.GetEnvironmentVariable("PATH");

                Environment.SetEnvironmentVariable("PATH", sitldirectory + ";" + simdir + ";" + path,
                    EnvironmentVariableTarget.Process);

                Environment.SetEnvironmentVariable("HOME", simdir, EnvironmentVariableTarget.Process);

                ProcessStartInfo exestart = new ProcessStartInfo();
                exestart.FileName = executablePath;
                exestart.Arguments = extra;
                exestart.WorkingDirectory = simdir;
                exestart.WindowStyle = ProcessWindowStyle.Minimized;
                exestart.UseShellExecute = true;

                log.InfoFormat("sitl: {0} {1} {2}", exestart.WorkingDirectory, exestart.FileName,
                                       exestart.Arguments);

                Process process = Process.Start(exestart);
                if (process == null)
                {
                    throw new InvalidOperationException("The operating system did not create a swarm SITL process.");
                }

                simulator.Add(process);
                WriteSITLStateFingerprint(simdir, stateFingerprint);

                await Task.Delay(100);
            }

            await Task.Delay(2000);

            MainV2.View.ShowScreen(MainV2.View.screens[0].Name);

            try
            {
                Parallel.For(0, max + 1, (a) =>
                //for (int a = (int)max; a >= 0; a--)
                {
                    var mav = new MAVLinkInterface();

                    var client = new Comms.TcpSerial();
                    try
                    {

                        client.client = new TcpClient("127.0.0.1", 5760 + (10 * (a)));
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    mav.BaseStream = client;

                    //SITLSEND = new UdpClient("127.0.0.1", 5501);

                    Thread.Sleep(200);

                    this.BeginInvokeIfRequired(() =>
                    {
                        MainV2.instance.doConnect(mav, "preset", "5760", false);

                        lock (this)
                            MainV2.Comports.Add(mav);

                        try
                        {
                            _ = mav.getParamListMavftpAsync((byte)mav.sysidcurrent, (byte)mav.compidcurrent);
                        }
                        catch
                        {
                        }
                    });
                }
                );

                return;
            }
            catch (Exception ex)
            {
                log.Error(ex);
                CustomMessageBox.Show(Strings.Failed_to_connect_to_SITL_instance +
                                      ex.InnerException?.Message, Strings.ERROR);
                return;
            }
        }

        public async void StartSwarmChain()
        {
            var max = 10;

            if (InputBox.Show("how many?", "how many?", ref max) != DialogResult.OK)
                return;

            // kill old session
            try
            {
                simulator.ForEach(a =>
                {
                    try
                    {
                        a.Kill();
                    }
                    catch { }
                });
            }
            catch
            {
            }

            var exepath = CheckandGetSITLImage("ArduCopter.elf");
            var model = "+";

            string executablePath = await exepath;
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                CustomMessageBox.Show(Strings.Failed_to_download_the_SITL_image, Strings.ERROR);
                return;
            }

            var config = await GetDefaultConfig(model, executablePath);
            if (string.IsNullOrEmpty(config))
            {
                CustomMessageBox.Show("SITL default parameter files are missing.", Strings.ERROR);
                return;
            }

            string stateFingerprint = BuildSITLStateFingerprint(executablePath, config);
            max--;

            for (int a = (int)max; a >= 0; a--)
            {
                var extra = " ";

                extra += @" --defaults """ + config + @",identity.parm"" ";

                var home = new PointLatLngAlt(markeroverlay.Markers[0].Position).newpos((double)NUM_heading.Value, a * 4);

                if (max == a)
                {
                    extra += String.Format(
			" -M{4} -s1 --home {3} --instance {0} --serial0 tcp:0 {1} ",
                        a, "", a + 1, BuildHomeLocation(home, (int)NUM_heading.Value), model);
                }
                else
                {
                    extra += String.Format(
			" -M{4} -s1 --home {3} --instance {0} --serial0 tcp:0 {1} ",
			a, "--serial2 tcpclient:127.0.0.1:" + (5772 + 10 * a), a + 1,
                        BuildHomeLocation(home, (int)NUM_heading.Value), model);
                }

                string simdir = sitldirectory + model + (a + 1) + Path.DirectorySeparatorChar;

                Directory.CreateDirectory(simdir);

                bool resetState = ShouldResetSITLState(simdir, stateFingerprint);
                if (resetState)
                {
                    extra += " --wipe ";
                }

                File.WriteAllText(simdir + "identity.parm", String.Format(@"SERIAL0_PROTOCOL=2
SERIAL1_PROTOCOL=2
SYSID_THISMAV={0}
MAV_SYSID={0}
SIM_TERRAIN=0
TERRAIN_ENABLE=0
SCHED_LOOP_RATE=50
SIM_RATE_HZ=400
SIM_DRIFT_SPEED=0
SIM_DRIFT_TIME=0
", a + 1));

                string path = Environment.GetEnvironmentVariable("PATH");

                Environment.SetEnvironmentVariable("PATH", sitldirectory + ";" + simdir + ";" + path,
                    EnvironmentVariableTarget.Process);

                Environment.SetEnvironmentVariable("HOME", simdir, EnvironmentVariableTarget.Process);

                ProcessStartInfo exestart = new ProcessStartInfo();
                exestart.FileName = executablePath;
                exestart.Arguments = extra;
                exestart.WorkingDirectory = simdir;
                exestart.WindowStyle = ProcessWindowStyle.Minimized;
                exestart.UseShellExecute = true;

                File.AppendAllText(Settings.GetUserDataDirectory() + "sitl.bat",
                    "mkdir " + (a + 1) + "\ncd " + (a + 1) + "\n" + @"""" + executablePath + @"""" + " " + extra + " &\n");

                File.AppendAllText(Settings.GetUserDataDirectory() + "sitl1.sh",
                    "mkdir " + (a + 1) + "\ncd " + (a + 1) + "\n" + @"""../" +
                    Path.GetFileName(executablePath).Replace("C:", "/mnt/c").Replace("\\", "/").Replace(".exe", ".elf") + @"""" + " " +
                    extra.Replace("C:", "/mnt/c").Replace("\\", "/") + " &\nsleep .3\ncd ..\n");

                log.InfoFormat("sitl: {0} {1} {2}", exestart.WorkingDirectory, exestart.FileName, exestart.Arguments);

                Process process = Process.Start(exestart);
                if (process == null)
                {
                    throw new InvalidOperationException("The operating system did not create a swarm SITL process.");
                }

                simulator.Add(process);
                WriteSITLStateFingerprint(simdir, stateFingerprint);
            }

            System.Threading.Thread.Sleep(2000);

            MainV2.View.ShowScreen(MainV2.View.screens[0].Name);

            try
            {
                var client = new Comms.TcpSerial();

                client.client = new TcpClient("127.0.0.1", 5760);

                MainV2.comPort.BaseStream = client;

                SITLSEND = new UdpClient("127.0.0.1", 5501);

                Thread.Sleep(200);

                this.BeginInvokeIfRequired(() =>
                {
                    MainV2.instance.doConnect(MainV2.comPort, "preset", "5760", false);
                    try
                    {
                        _ = MainV2.comPort.getParamListMavftpAsync((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent);
                    }
                    catch
                    {
                    }
                });

                return;
            }
            catch
            {
                CustomMessageBox.Show(Strings.Failed_to_connect_to_SITL_instance, Strings.ERROR);
                return;
            }
        }

        private void but_swarmseq_Click(object sender, EventArgs e)
        {
             StartSwarmChain();
        }

        private void but_swarmlink_Click(object sender, EventArgs e)
        {
            _ = StartSwarmSeperate(Firmwares.ArduCopter2);
        }

        private void but_swarmplane_Click(object sender, EventArgs e)
        {
            _ = StartSwarmSeperate(Firmwares.ArduPlane);
        }

        private void but_swarmrover_Click(object sender, EventArgs e)
        {
            _ = StartSwarmSeperate(Firmwares.ArduRover);
        }
    }
}
