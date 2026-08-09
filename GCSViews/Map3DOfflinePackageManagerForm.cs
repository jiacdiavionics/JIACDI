using MissionPlanner.Utilities;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    internal sealed class Map3DOfflinePackageManagerForm : Form
    {
        private readonly ListView packageList;
        private readonly Button importFileButton;
        private readonly Button importFolderButton;
        private readonly Button toggleButton;
        private readonly Button removeButton;
        private readonly Button closeButton;
        private readonly Label statusLabel;

        internal Map3DOfflinePackageManagerForm()
        {
            Text = "Offline 3D Map Packages";
            ClientSize = new Size(880, 510);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = ModernUi.Canvas;
            ForeColor = ModernUi.TextPrimary;

            packageList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = ModernUi.Surface,
                ForeColor = ModernUi.TextPrimary
            };
            packageList.Columns.Add("Package", 250);
            packageList.Columns.Add("Type", 170);
            packageList.Columns.Add("Coverage", 300);
            packageList.Columns.Add("Status", 90);
            packageList.SelectedIndexChanged += (sender, args) => UpdateButtons();
            packageList.DoubleClick += (sender, args) => ToggleSelected();

            importFileButton = CreateButton("Import File", "\uE8B7", ImportFileButton_Click);
            importFolderButton = CreateButton("Import Folder", "\uE8B7", ImportFolderButton_Click);
            toggleButton = CreateButton("Disable", "\uE73E", (sender, args) => ToggleSelected());
            removeButton = CreateButton("Remove", "\uE74D", (sender, args) => RemoveSelected());
            closeButton = CreateButton("Close", "\uE711", (sender, args) => Close());

            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Ready",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ModernUi.TextSecondary,
                AutoEllipsis = true
            };

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = ModernUi.SurfaceRaised,
                Padding = new Padding(18, 12, 18, 10)
            };
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 27,
                Text = "Offline 3D map library",
                Font = new Font(ModernUi.UiFontFamily, 12F, FontStyle.Bold),
                ForeColor = ModernUi.TextPrimary
            };
            var subtitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Terrain: HGT, GeoTIFF, DTED, Cesium layer.json. Imagery: MBTiles, GeoTIFF, XYZ. Scenes: GeoJSON, KML/KMZ, 3D Tiles.",
                ForeColor = ModernUi.TextSecondary,
                AutoEllipsis = true
            };
            header.Controls.Add(subtitle);
            header.Controls.Add(title);

            var commands = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(12, 9, 12, 8),
                BackColor = ModernUi.SurfaceRaised
            };
            commands.Controls.AddRange(new Control[]
            {
                importFileButton,
                importFolderButton,
                toggleButton,
                removeButton,
                statusLabel,
                closeButton
            });
            commands.SizeChanged += (sender, args) =>
            {
                int buttonsWidth = commands.Controls.Cast<Control>()
                    .Where(control => control != statusLabel)
                    .Sum(control => control.Width + control.Margin.Horizontal);
                statusLabel.Width = Math.Max(100,
                    commands.ClientSize.Width - buttonsWidth - commands.Padding.Horizontal - 16);
            };

            var content = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                BackColor = ModernUi.Canvas
            };
            content.Controls.Add(packageList);

            Controls.Add(content);
            Controls.Add(commands);
            Controls.Add(header);
            AcceptButton = closeButton;
            CancelButton = closeButton;

            Shown += (sender, args) => RefreshPackages();
            ModernUi.Apply(this);
        }

        private static Button CreateButton(string text, string glyph, EventHandler click)
        {
            var button = new Button
            {
                AutoSize = false,
                Size = new Size(text.Length > 10 ? 130 : 108, 38),
                Margin = new Padding(4, 0, 4, 0),
                Text = text,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleCenter,
                Image = ModernUi.CreateIcon(glyph, 16, ModernUi.TextPrimary)
            };
            button.Click += click;
            return button;
        }

        private async void ImportFileButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Import offline 3D map data",
                Filter =
                    "Supported map data|*.hgt;*.zip;*.tif;*.tiff;*.mbtiles;*.gpkg;*.dt0;*.dt1;*.dt2;*.dem;*.asc;*.bil;*.vrt;*.img;*.geojson;*.json;*.kml;*.kmz|" +
                    "Terrain and raster maps|*.hgt;*.zip;*.tif;*.tiff;*.mbtiles;*.gpkg;*.dt0;*.dt1;*.dt2;*.dem;*.asc;*.bil;*.vrt;*.img|" +
                    "3D scenes and buildings|*.geojson;*.json;*.kml;*.kmz|All files|*.*",
                CheckFileExists = true,
                Multiselect = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                Map3DRasterImportRole role = DetermineRasterRole(dialog.FileNames);
                if (role == (Map3DRasterImportRole)(-1))
                {
                    return;
                }

                await RunImportAsync(async () =>
                {
                    foreach (string file in dialog.FileNames)
                    {
                        await Task.Run(() => Map3DOfflinePackageCatalog.ImportFile(file, role));
                    }
                }, dialog.FileNames.Length == 1
                    ? "Importing " + Path.GetFileName(dialog.FileNames[0]) + "..."
                    : "Importing " + dialog.FileNames.Length + " map files...");
            }
        }

        private async void ImportFolderButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Select a Cesium terrain, 3D Tiles, HGT, or XYZ map folder",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                await RunImportAsync(
                    () => Task.Run(() => Map3DOfflinePackageCatalog.ImportFolder(dialog.SelectedPath)),
                    "Importing " + new DirectoryInfo(dialog.SelectedPath).Name + "...");
            }
        }

        private Map3DRasterImportRole DetermineRasterRole(string[] files)
        {
            string[] roleSensitive = files.Where(file =>
                    new[] { ".tif", ".tiff", ".gpkg", ".dt0", ".dt1", ".dt2", ".dem", ".asc", ".bil", ".vrt", ".img" }
                        .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (roleSensitive.Length == 0)
            {
                return Map3DRasterImportRole.Auto;
            }

            using (var dialog = new RasterRoleDialog())
            {
                DialogResult result = dialog.ShowDialog(this);
                return result == DialogResult.Yes
                    ? Map3DRasterImportRole.Terrain
                    : result == DialogResult.No
                        ? Map3DRasterImportRole.Imagery
                        : (Map3DRasterImportRole)(-1);
            }
        }

        private async Task RunImportAsync(Func<Task> import, string status)
        {
            SetBusy(true, status);
            try
            {
                await import();
                statusLabel.Text = "Import complete. The 3D map will reload when this window closes.";
                RefreshPackages();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Import failed";
                CustomMessageBox.Show("Unable to import the offline map data:\n\n" + ex.Message,
                    "Offline 3D Maps");
            }
            finally
            {
                SetBusy(false, statusLabel.Text);
            }
        }

        private void SetBusy(bool busy, string status)
        {
            UseWaitCursor = busy;
            importFileButton.Enabled = !busy;
            importFolderButton.Enabled = !busy;
            packageList.Enabled = !busy;
            closeButton.Enabled = !busy;
            statusLabel.Text = status;
            if (!busy)
            {
                UpdateButtons();
            }
            else
            {
                toggleButton.Enabled = false;
                removeButton.Enabled = false;
            }
        }

        private void RefreshPackages()
        {
            string selectedId = SelectedPackage?.Id;
            packageList.BeginUpdate();
            try
            {
                packageList.Items.Clear();
                foreach (Map3DOfflinePackage package in Map3DOfflinePackageCatalog.GetPackages())
                {
                    var item = new ListViewItem(package.Name ?? package.Id)
                    {
                        Tag = package
                    };
                    item.SubItems.Add(GetKindName(package.Kind));
                    item.SubItems.Add(GetCoverage(package));
                    item.SubItems.Add(package.Enabled ? "Enabled" : "Disabled");
                    packageList.Items.Add(item);
                    if (string.Equals(package.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                    }
                }
            }
            finally
            {
                packageList.EndUpdate();
            }
            UpdateButtons();
        }

        private Map3DOfflinePackage SelectedPackage => packageList.SelectedItems.Count == 1
            ? packageList.SelectedItems[0].Tag as Map3DOfflinePackage
            : null;

        private void UpdateButtons()
        {
            Map3DOfflinePackage package = SelectedPackage;
            toggleButton.Enabled = packageList.Enabled && package != null;
            removeButton.Enabled = packageList.Enabled && package != null;
            toggleButton.Text = package != null && package.Enabled ? "Disable" : "Enable";
        }

        private void ToggleSelected()
        {
            Map3DOfflinePackage package = SelectedPackage;
            if (package == null) return;
            Map3DOfflinePackageCatalog.SetEnabled(package.Id, !package.Enabled);
            RefreshPackages();
        }

        private void RemoveSelected()
        {
            Map3DOfflinePackage package = SelectedPackage;
            if (package == null) return;
            if (CustomMessageBox.Show(
                    "Remove '" + package.Name + "' and its imported local files?",
                    "Offline 3D Maps",
                    MessageBoxButtons.YesNo) != (int)DialogResult.Yes)
            {
                return;
            }
            Map3DOfflinePackageCatalog.Remove(package.Id);
            RefreshPackages();
            statusLabel.Text = "Package removed";
        }

        private static string GetKindName(string kind)
        {
            switch (kind)
            {
                case Map3DPackageKinds.RasterImagery: return "Raster imagery";
                case Map3DPackageKinds.RasterTerrain: return "Raster terrain";
                case Map3DPackageKinds.XyzImagery: return "XYZ imagery";
                case Map3DPackageKinds.SrtmHgt: return "SRTM terrain";
                case Map3DPackageKinds.CesiumTerrain: return "Cesium terrain";
                case Map3DPackageKinds.Cesium3DTiles: return "Cesium 3D Tiles";
                case Map3DPackageKinds.GeoJsonBuildings: return "GeoJSON buildings";
                case Map3DPackageKinds.Kml: return "KML/KMZ scene";
                default: return kind ?? "Unknown";
            }
        }

        private static string GetCoverage(Map3DOfflinePackage package)
        {
            if (!package.HasBounds)
            {
                return "Defined by package";
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F4}, {1:F4} to {2:F4}, {3:F4}",
                package.West, package.South, package.East, package.North);
        }

        private sealed class RasterRoleDialog : Form
        {
            internal RasterRoleDialog()
            {
                Text = "Raster Map Type";
                ClientSize = new Size(480, 162);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = ModernUi.Canvas;

                var message = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 88,
                    Padding = new Padding(18, 18, 18, 8),
                    Text = "How should DIMP use this georeferenced raster? Choose Terrain for elevation/DEM data, or Imagery for aerial and satellite maps.",
                    ForeColor = ModernUi.TextPrimary
                };
                var commands = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(12, 8, 12, 8)
                };
                var cancel = new Button { Text = "Cancel", Size = new Size(90, 34), DialogResult = DialogResult.Cancel };
                var imagery = new Button { Text = "Imagery", Size = new Size(100, 34), DialogResult = DialogResult.No };
                var terrain = new Button { Text = "Terrain", Size = new Size(100, 34), DialogResult = DialogResult.Yes };
                commands.Controls.AddRange(new Control[] { cancel, imagery, terrain });
                Controls.Add(commands);
                Controls.Add(message);
                AcceptButton = terrain;
                CancelButton = cancel;
                ModernUi.Apply(this);
            }
        }
    }
}
