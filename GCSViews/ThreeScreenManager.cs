using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    internal sealed class ThreeScreenManager : IDisposable
    {
        private readonly MainV2 owner;
        private readonly FlightData flightData;

        private DashboardWindow dataWindow;
        private DashboardWindow hudWindow;
        private DashboardWindow mapWindow;
        private TableLayoutPanel dataLayout;
        private SplitContainer dataSplit;
        private Panel mapHost;
        private TabControl quickHost;
        private TabControl gaugesHost;
        private TabControl actionsHost;
        private ToolStripButton map2DButton;
        private ToolStripButton map3DButton;

        private HostedControlState hudState;
        private HostedControlState mapState;
        private HostedTabState quickState;
        private HostedTabState gaugesState;
        private HostedTabState actionsState;
        private ActionPanelLayoutState actionsLayoutState;

        private bool active;
        private bool stopping;
        private bool map3DActive;
        private bool map3DWasVisible;

        internal ThreeScreenManager(MainV2 owner, FlightData flightData)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.flightData = flightData ?? throw new ArgumentNullException(nameof(flightData));
        }

        internal bool IsActive => active;

        internal event EventHandler Closed;

        internal void Start()
        {
            if (active)
            {
                BringToFront();
                return;
            }

            if (flightData.IsDisposed)
            {
                throw new InvalidOperationException("Flight Data is not available.");
            }

            active = true;
            map3DWasVisible = Map3D.HasVisibleInstance;

            try
            {
                if (map3DWasVisible)
                {
                    Map3D.HideMap();
                }

                CaptureLayout();
                CreateWindows();
                HostFlightDataControls();
                flightData.SetThreeScreenModeActive(true);
                ArrangeWindows();
                ShowWindows();

                if (Settings.Instance.GetBoolean("three_screen_map3d", false))
                {
                    Show3DMap();
                }
                else
                {
                    Show2DMap();
                }
            }
            catch
            {
                StopCore(false);
                throw;
            }
        }

        internal void Stop()
        {
            StopCore(true);
        }

        private void CaptureLayout()
        {
            hudState = HostedControlState.Capture(flightData.ThreeScreenHudControl);
            mapState = HostedControlState.Capture(flightData.ThreeScreenMapControl);
            quickState = HostedTabState.Capture(flightData.ThreeScreenQuickTab);
            gaugesState = HostedTabState.Capture(flightData.ThreeScreenGaugesTab);
            actionsState = HostedTabState.Capture(flightData.ThreeScreenActionsTab);
            actionsLayoutState = ActionPanelLayoutState.Capture(flightData.ThreeScreenActionsTab);
        }

        private void CreateWindows()
        {
            dataWindow = new DashboardWindow("DIMP - UAV Data");
            hudWindow = new DashboardWindow("DIMP - HUD");
            mapWindow = new DashboardWindow("DIMP - Map / 3D Map");

            dataWindow.ExitRequested += ExitRequested;
            hudWindow.ExitRequested += ExitRequested;
            mapWindow.ExitRequested += ExitRequested;
            dataWindow.UserClosing += WindowUserClosing;
            hudWindow.UserClosing += WindowUserClosing;
            mapWindow.UserClosing += WindowUserClosing;

            dataLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = ModernUi.Canvas,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            dataWindow.Content.Controls.Add(dataLayout);

            dataSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BorderStyle = BorderStyle.None,
                SplitterWidth = 5,
                BackColor = ModernUi.Border
            };
            dataSplit.Panel1.BackColor = ModernUi.Canvas;
            dataSplit.Panel2.BackColor = ModernUi.Canvas;
            dataLayout.Controls.Add(dataSplit, 0, 0);
            dataWindow.Content.Resize += (sender, args) => UpdateDataSplitDistance();

            quickHost = CreateTabHost();
            gaugesHost = CreateTabHost();
            actionsHost = CreateTabHost();
            dataSplit.Panel1.Controls.Add(CreateDataSection("QUICK DATA", quickHost));
            dataSplit.Panel2.Controls.Add(CreateDataSection("UAV GAUGES", gaugesHost));
            dataLayout.Controls.Add(CreateDataSection("ACTION PANEL", actionsHost), 0, 1);

            mapHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            mapWindow.Content.Controls.Add(mapHost);
            mapWindow.Content.Resize += (sender, args) =>
            {
                if (map3DActive)
                {
                    Map3D.Instance.ResizeEmbedded();
                }
            };

            map2DButton = CreateModeButton("2D Map", "Show the live 2D map");
            map3DButton = CreateModeButton("3D Map", "Show the terrain and buildings map");
            map2DButton.Image = ModernUi.CreateIcon("\uE81D");
            map3DButton.Image = ModernUi.CreateIcon("\uE774");
            map2DButton.Click += (sender, args) => Show2DMap();
            map3DButton.Click += (sender, args) => Show3DMap();
            mapWindow.Toolbar.Items.Insert(1, new ToolStripSeparator());
            mapWindow.Toolbar.Items.Insert(2, map2DButton);
            mapWindow.Toolbar.Items.Insert(3, map3DButton);

            ModernUi.Apply(dataWindow);
            ModernUi.Apply(hudWindow);
            ModernUi.Apply(mapWindow);
        }

        private void HostFlightDataControls()
        {
            quickState.HostIn(quickHost);
            gaugesState.HostIn(gaugesHost);
            actionsState.HostIn(actionsHost);
            actionsLayoutState?.ApplyThreeScreenLayout();

            HUD hud = flightData.ThreeScreenHudControl;
            hudWindow.Content.Controls.Add(hud);
            hud.Dock = DockStyle.Fill;
            hud.Visible = true;
            hud.Enabled = true;
            hud.doResize();

            Control map = flightData.ThreeScreenMapControl;
            mapHost.Controls.Add(map);
            map.Dock = DockStyle.Fill;
            map.Visible = true;
            map.BringToFront();
        }

        private static TabControl CreateTabHost()
        {
            return new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.FlatButtons,
                ItemSize = new Size(0, 1),
                SizeMode = TabSizeMode.Fixed,
                Multiline = true,
                Padding = Point.Empty,
                BackColor = ModernUi.Surface
            };
        }

        private static Control CreateDataSection(string title, TabControl tabHost)
        {
            Panel section = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ModernUi.Canvas
            };
            Label heading = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = title,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                BackColor = ModernUi.Surface,
                ForeColor = ModernUi.TextSecondary,
                Font = new Font(ModernUi.UiFontFamily, 9F, FontStyle.Bold)
            };

            section.Controls.Add(tabHost);
            section.Controls.Add(heading);
            return section;
        }

        private static ToolStripButton CreateModeButton(string text, string tooltip)
        {
            return new ToolStripButton
            {
                Text = text,
                ToolTipText = tooltip,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                CheckOnClick = false,
                ForeColor = ModernUi.TextPrimary,
                Margin = new Padding(3, 2, 3, 2),
                Padding = new Padding(10, 3, 10, 3)
            };
        }

        private void Show2DMap()
        {
            if (!active || mapHost == null || mapHost.IsDisposed)
            {
                return;
            }

            if (map3DActive)
            {
                Map3D.Instance.DetachEmbedded();
                map3DActive = false;
            }

            Control map = flightData.ThreeScreenMapControl;
            map.Visible = true;
            map.BringToFront();
            map2DButton.Checked = true;
            map3DButton.Checked = false;
            Settings.Instance["three_screen_map3d"] = false.ToString();
        }

        private void Show3DMap()
        {
            if (!active || mapHost == null || mapHost.IsDisposed || map3DActive)
            {
                return;
            }

            flightData.ThreeScreenMapControl.Visible = false;
            Map3D.Instance.AttachEmbedded(mapHost);
            map3DActive = true;
            map2DButton.Checked = false;
            map3DButton.Checked = true;
            Settings.Instance["three_screen_map3d"] = true.ToString();
        }

        private void ArrangeWindows()
        {
            Screen[] screens = Screen.AllScreens
                .OrderByDescending(screen => screen.Primary)
                .ThenBy(screen => screen.Bounds.Left)
                .ThenBy(screen => screen.Bounds.Top)
                .ToArray();

            if (screens.Length >= 3)
            {
                SetWindowBounds(dataWindow, screens[0].WorkingArea);
                SetWindowBounds(hudWindow, screens[1].WorkingArea);
                SetWindowBounds(mapWindow, screens[2].WorkingArea);
                return;
            }

            if (screens.Length == 2)
            {
                Rectangle first = screens[0].WorkingArea;
                Rectangle second = screens[1].WorkingArea;
                int leftWidth = second.Width / 2;

                SetWindowBounds(dataWindow, first);
                SetWindowBounds(hudWindow, new Rectangle(second.Left, second.Top, leftWidth, second.Height));
                SetWindowBounds(mapWindow,
                    new Rectangle(second.Left + leftWidth, second.Top, second.Width - leftWidth, second.Height));
                return;
            }

            Rectangle area = screens.Length == 0 ? Screen.PrimaryScreen.WorkingArea : screens[0].WorkingArea;
            int columnWidth = Math.Max(1, area.Width / 3);
            SetWindowBounds(dataWindow, new Rectangle(area.Left, area.Top, columnWidth, area.Height));
            SetWindowBounds(hudWindow, new Rectangle(area.Left + columnWidth, area.Top, columnWidth, area.Height));
            SetWindowBounds(mapWindow,
                new Rectangle(area.Left + (columnWidth * 2), area.Top, area.Width - (columnWidth * 2), area.Height));
        }

        private static void SetWindowBounds(Form window, Rectangle bounds)
        {
            window.StartPosition = FormStartPosition.Manual;
            window.WindowState = FormWindowState.Normal;
            window.Bounds = bounds;
        }

        private void ShowWindows()
        {
            dataWindow.Show(owner);
            hudWindow.Show(owner);
            mapWindow.Show(owner);
            UpdateDataSplitDistance();
            BringToFront();
        }

        private void BringToFront()
        {
            dataWindow?.BringToFront();
            hudWindow?.BringToFront();
            mapWindow?.BringToFront();
        }

        private void UpdateDataSplitDistance()
        {
            if (dataSplit == null || dataSplit.IsDisposed)
            {
                return;
            }

            int minimum = dataSplit.Panel1MinSize;
            int maximum = dataSplit.Width - dataSplit.Panel2MinSize - dataSplit.SplitterWidth;
            if (maximum < minimum)
            {
                return;
            }

            int desired = (int)(dataSplit.Width * 0.56);
            dataSplit.SplitterDistance = Math.Max(minimum, Math.Min(desired, maximum));
        }

        private void ExitRequested(object sender, EventArgs e)
        {
            Stop();
        }

        private void WindowUserClosing(object sender, FormClosingEventArgs e)
        {
            if (!stopping)
            {
                e.Cancel = true;
                Stop();
            }
        }

        private void StopCore(bool raiseClosed)
        {
            if (!active || stopping)
            {
                return;
            }

            stopping = true;

            try
            {
                if (map3DActive)
                {
                    Map3D.Instance.DetachEmbedded();
                    map3DActive = false;
                }

                flightData.SetThreeScreenModeActive(false);
                actionsLayoutState?.Restore();
                RestoreTabs();
                mapState?.Restore();
                hudState?.Restore();
                flightData.ThreeScreenHudControl.doResize();

                CloseWindow(mapWindow);
                CloseWindow(hudWindow);
                CloseWindow(dataWindow);

                if (map3DWasVisible)
                {
                    Map3D.Instance.Activate();
                }
            }
            finally
            {
                active = false;
                stopping = false;

                if (raiseClosed)
                {
                    Closed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void RestoreTabs()
        {
            quickState?.Restore();
            actionsState?.Restore();
            gaugesState?.Restore();
            quickState?.RestoreSelection();
            actionsState?.RestoreSelection();
            gaugesState?.RestoreSelection();
        }

        private static void CloseWindow(Form window)
        {
            if (window == null || window.IsDisposed)
            {
                return;
            }

            window.Close();
            window.Dispose();
        }

        public void Dispose()
        {
            StopCore(false);
        }

        internal sealed class HostedControlState
        {
            private readonly Control control;
            private readonly Control parent;
            private readonly int childIndex;
            private readonly DockStyle dock;
            private readonly AnchorStyles anchor;
            private readonly Rectangle bounds;
            private readonly bool visible;
            private readonly bool enabled;

            private HostedControlState(Control control)
            {
                this.control = control;
                parent = control.Parent;
                childIndex = parent == null ? 0 : parent.Controls.GetChildIndex(control);
                dock = control.Dock;
                anchor = control.Anchor;
                bounds = control.Bounds;
                visible = control.Visible;
                enabled = control.Enabled;
            }

            internal static HostedControlState Capture(Control control)
            {
                if (control == null)
                {
                    throw new InvalidOperationException("A required Flight Data control is missing.");
                }

                return new HostedControlState(control);
            }

            internal void Restore()
            {
                if (control.IsDisposed)
                {
                    return;
                }

                Control currentParent = control.Parent;
                currentParent?.Controls.Remove(control);

                control.Dock = DockStyle.None;
                control.Anchor = anchor;
                control.Bounds = bounds;
                control.Dock = dock;
                control.Visible = visible;
                control.Enabled = enabled;

                if (parent == null || parent.IsDisposed)
                {
                    return;
                }

                parent.Controls.Add(control);
                parent.Controls.SetChildIndex(control, Math.Min(childIndex, parent.Controls.Count - 1));
            }
        }

        private sealed class ActionPanelLayoutState
        {
            private readonly TableLayoutPanel table;
            private readonly int columnCount;
            private readonly int rowCount;
            private readonly LayoutStyleState[] columnStyles;
            private readonly LayoutStyleState[] rowStyles;
            private readonly ControlPlacement[] placements;
            private readonly DockStyle dock;
            private readonly AnchorStyles anchor;
            private readonly Rectangle bounds;
            private readonly Padding margin;
            private readonly Padding padding;
            private readonly bool autoScroll;
            private readonly bool autoSize;
            private readonly AutoSizeMode autoSizeMode;

            private ActionPanelLayoutState(TableLayoutPanel table)
            {
                this.table = table;
                columnCount = table.ColumnCount;
                rowCount = table.RowCount;
                columnStyles = table.ColumnStyles.Cast<ColumnStyle>()
                    .Select(style => new LayoutStyleState(style.SizeType, style.Width)).ToArray();
                rowStyles = table.RowStyles.Cast<RowStyle>()
                    .Select(style => new LayoutStyleState(style.SizeType, style.Height)).ToArray();
                placements = table.Controls.Cast<Control>()
                    .Select(control => new ControlPlacement(table, control)).ToArray();
                dock = table.Dock;
                anchor = table.Anchor;
                bounds = table.Bounds;
                margin = table.Margin;
                padding = table.Padding;
                autoScroll = table.AutoScroll;
                autoSize = table.AutoSize;
                autoSizeMode = table.AutoSizeMode;
            }

            internal static ActionPanelLayoutState Capture(TabPage page)
            {
                TableLayoutPanel table = page?.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
                return table == null ? null : new ActionPanelLayoutState(table);
            }

            internal void ApplyThreeScreenLayout()
            {
                if (table.IsDisposed)
                {
                    return;
                }

                Cell[] cells =
                {
                    new Cell("CMB_action", 0, 0),
                    new Cell("BUTactiondo", 1, 0),
                    new Cell("BUT_quickauto", 2, 0),
                    new Cell("modifyandSetSpeed", 3, 0),
                    new Cell("CMB_setwp", 0, 1),
                    new Cell("BUT_setwp", 1, 1),
                    new Cell("BUT_quickmanual", 2, 1),
                    new Cell("modifyandSetAlt", 3, 1),
                    new Cell("CMB_modes", 0, 2),
                    new Cell("BUT_setmode", 1, 2),
                    new Cell("BUT_quickrtl", 2, 2),
                    new Cell("modifyandSetLoiterRad", 3, 2),
                    new Cell("CMB_mountmode", 0, 3),
                    new Cell("BUT_mountmode", 1, 3),
                    new Cell("BUT_joystick", 2, 3),
                    new Cell("BUT_ARM", 3, 3),
                    new Cell("BUT_RAWSensor", 0, 4),
                    new Cell("BUTrestartmission", 1, 4),
                    new Cell("BUT_Homealt", 2, 4),
                    new Cell("BUT_clear_track", 3, 4),
                    new Cell("BUT_SendMSG", 0, 5),
                    new Cell("BUT_resumemis", 1, 5),
                    new Cell("BUT_abortland", 2, 5)
                };

                table.SuspendLayout();
                try
                {
                    table.AutoScroll = false;
                    table.AutoSize = false;
                    table.Anchor = AnchorStyles.None;
                    table.Dock = DockStyle.Fill;
                    table.Margin = Padding.Empty;
                    table.Padding = new Padding(4);
                    table.RowCount = 6;

                    foreach (Cell cell in cells)
                    {
                        Control control = table.Controls[cell.Name];
                        if (control == null)
                        {
                            continue;
                        }

                        table.SetColumn(control, cell.Column);
                        table.SetRow(control, cell.Row);
                        table.SetColumnSpan(control, 1);
                        table.SetRowSpan(control, 1);
                        control.Dock = DockStyle.Fill;
                        control.Margin = new Padding(3);
                    }

                    table.ColumnCount = 4;
                    table.ColumnStyles.Clear();
                    table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
                    table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
                    table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
                    table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));

                    table.RowStyles.Clear();
                    for (int index = 0; index < 6; index++)
                    {
                        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 6F));
                    }
                }
                finally
                {
                    table.ResumeLayout(true);
                    table.Parent?.PerformLayout();
                }
            }

            internal void Restore()
            {
                if (table.IsDisposed)
                {
                    return;
                }

                table.SuspendLayout();
                try
                {
                    table.ColumnCount = Math.Max(columnCount, table.ColumnCount);
                    table.RowCount = Math.Max(rowCount, table.RowCount);

                    foreach (ControlPlacement placement in placements)
                    {
                        placement.Restore(table);
                    }

                    table.ColumnStyles.Clear();
                    table.ColumnCount = columnCount;
                    foreach (LayoutStyleState style in columnStyles)
                    {
                        table.ColumnStyles.Add(new ColumnStyle(style.SizeType, style.Size));
                    }

                    table.RowStyles.Clear();
                    table.RowCount = rowCount;
                    foreach (LayoutStyleState style in rowStyles)
                    {
                        table.RowStyles.Add(new RowStyle(style.SizeType, style.Size));
                    }

                    table.Dock = DockStyle.None;
                    table.Anchor = anchor;
                    table.Bounds = bounds;
                    table.Dock = dock;
                    table.Margin = margin;
                    table.Padding = padding;
                    table.AutoScroll = autoScroll;
                    table.AutoSize = autoSize;
                    table.AutoSizeMode = autoSizeMode;
                }
                finally
                {
                    table.ResumeLayout(true);
                    table.Parent?.PerformLayout();
                }
            }

            private sealed class ControlPlacement
            {
                private readonly Control control;
                private readonly int column;
                private readonly int row;
                private readonly int columnSpan;
                private readonly int rowSpan;
                private readonly DockStyle dock;
                private readonly AnchorStyles anchor;
                private readonly Padding margin;

                internal ControlPlacement(TableLayoutPanel table, Control control)
                {
                    this.control = control;
                    column = table.GetColumn(control);
                    row = table.GetRow(control);
                    columnSpan = table.GetColumnSpan(control);
                    rowSpan = table.GetRowSpan(control);
                    dock = control.Dock;
                    anchor = control.Anchor;
                    margin = control.Margin;
                }

                internal void Restore(TableLayoutPanel table)
                {
                    if (control.IsDisposed)
                    {
                        return;
                    }

                    table.SetColumn(control, column);
                    table.SetRow(control, row);
                    table.SetColumnSpan(control, columnSpan);
                    table.SetRowSpan(control, rowSpan);
                    control.Dock = dock;
                    control.Anchor = anchor;
                    control.Margin = margin;
                }
            }

            private struct LayoutStyleState
            {
                internal LayoutStyleState(SizeType sizeType, float size)
                {
                    SizeType = sizeType;
                    Size = size;
                }

                internal SizeType SizeType { get; }
                internal float Size { get; }
            }

            private struct Cell
            {
                internal Cell(string name, int column, int row)
                {
                    Name = name;
                    Column = column;
                    Row = row;
                }

                internal string Name { get; }
                internal int Column { get; }
                internal int Row { get; }
            }
        }

        internal sealed class HostedTabState
        {
            private readonly TabPage page;
            private readonly Control parent;
            private readonly int tabIndex;
            private readonly TabPage selectedTab;

            private HostedTabState(TabPage page)
            {
                this.page = page;
                parent = page.Parent;

                TabControl tabControl = parent as TabControl;
                tabIndex = tabControl == null ? 0 : tabControl.TabPages.IndexOf(page);
                selectedTab = tabControl?.SelectedTab;
            }

            internal static HostedTabState Capture(TabPage page)
            {
                if (page == null)
                {
                    throw new InvalidOperationException("A required Flight Data tab is missing.");
                }

                return new HostedTabState(page);
            }

            internal void HostIn(TabControl host)
            {
                if (host == null)
                {
                    throw new ArgumentNullException(nameof(host));
                }

                if (page.IsDisposed)
                {
                    throw new ObjectDisposedException(page.Name);
                }

                TabControl currentTabControl = page.Parent as TabControl;
                if (currentTabControl != null)
                {
                    currentTabControl.TabPages.Remove(page);
                }
                else
                {
                    page.Parent?.Controls.Remove(page);
                }

                host.TabPages.Add(page);
            }

            internal void Restore()
            {
                if (page.IsDisposed)
                {
                    return;
                }

                TabControl currentTabControl = page.Parent as TabControl;
                if (currentTabControl != null)
                {
                    currentTabControl.TabPages.Remove(page);
                }
                else
                {
                    page.Parent?.Controls.Remove(page);
                }

                if (parent == null || parent.IsDisposed)
                {
                    return;
                }

                TabControl tabControl = parent as TabControl;
                if (tabControl != null)
                {
                    int index = Math.Max(0, Math.Min(tabIndex, tabControl.TabPages.Count));
                    tabControl.TabPages.Insert(index, page);
                }
                else
                {
                    parent.Controls.Add(page);
                }
            }

            internal void RestoreSelection()
            {
                TabControl tabControl = parent as TabControl;
                if (tabControl != null && selectedTab != null && tabControl.TabPages.Contains(selectedTab))
                {
                    tabControl.SelectedTab = selectedTab;
                }
            }
        }

        private sealed class DashboardWindow : Form
        {
            internal DashboardWindow(string title)
            {
                Text = title;
                BackColor = ModernUi.Canvas;
                ForeColor = ModernUi.TextPrimary;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = true;
                KeyPreview = true;
                MinimumSize = new Size(320, 300);

                Toolbar = new ToolStrip
                {
                    Dock = DockStyle.Top,
                    GripStyle = ToolStripGripStyle.Hidden,
                    BackColor = ModernUi.Surface,
                    ForeColor = ModernUi.TextPrimary,
                    Renderer = new DIMPRenderer(),
                    Padding = new Padding(6, 3, 6, 3)
                };

                ToolStripLabel titleLabel = new ToolStripLabel(title)
                {
                    ForeColor = ModernUi.TextPrimary,
                    Font = new Font(ModernUi.UiFontFamily, 10F, FontStyle.Bold),
                    Margin = new Padding(5, 2, 8, 2)
                };
                ToolStripButton exitButton = new ToolStripButton
                {
                    Text = "Exit 3 Screens",
                    ToolTipText = "Return to the normal DIMP layout",
                    Alignment = ToolStripItemAlignment.Right,
                    DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                    Image = ModernUi.CreateIcon("\uE711", 18, ModernUi.Warning),
                    ForeColor = ModernUi.Warning,
                    Margin = new Padding(3, 2, 3, 2),
                    Padding = new Padding(10, 3, 10, 3)
                };
                exitButton.Click += (sender, args) => ExitRequested?.Invoke(this, EventArgs.Empty);

                Toolbar.Items.Add(titleLabel);
                Toolbar.Items.Add(exitButton);

                Content = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = ModernUi.Canvas
                };

                Controls.Add(Content);
                Controls.Add(Toolbar);

                KeyDown += (sender, args) =>
                {
                    if (args.KeyCode == Keys.Escape)
                    {
                        args.Handled = true;
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                    }
                };
                FormClosing += (sender, args) => UserClosing?.Invoke(this, args);
            }

            internal ToolStrip Toolbar { get; }
            internal Panel Content { get; }
            internal event EventHandler ExitRequested;
            internal event FormClosingEventHandler UserClosing;
        }
    }
}
