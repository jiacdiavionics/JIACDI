using MissionPlanner.Controls;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Shared presentation layer for DIMP. This class deliberately changes appearance only;
    /// controls, names, bindings and event handlers remain owned by their existing views.
    /// </summary>
    public static class ModernUi
    {
        public static readonly Color Canvas = Color.FromArgb(17, 21, 23);
        public static readonly Color Surface = Color.FromArgb(23, 28, 31);
        public static readonly Color SurfaceRaised = Color.FromArgb(31, 38, 42);
        public static readonly Color SurfaceHover = Color.FromArgb(42, 51, 56);
        public static readonly Color Border = Color.FromArgb(53, 65, 71);
        public static readonly Color BorderStrong = Color.FromArgb(72, 87, 94);
        public static readonly Color Accent = Color.FromArgb(27, 158, 207);
        public static readonly Color AccentBright = Color.FromArgb(62, 190, 235);
        public static readonly Color AccentPressed = Color.FromArgb(17, 118, 157);
        public static readonly Color TextPrimary = Color.FromArgb(232, 237, 240);
        public static readonly Color TextSecondary = Color.FromArgb(159, 172, 179);
        public static readonly Color TextDisabled = Color.FromArgb(101, 114, 120);
        public static readonly Color Success = Color.FromArgb(54, 199, 139);
        public static readonly Color Warning = Color.FromArgb(242, 184, 75);
        public static readonly Color Danger = Color.FromArgb(239, 91, 103);

        private static readonly ConditionalWeakTable<Control, ControlHook> Hooks =
            new ConditionalWeakTable<Control, ControlHook>();
        private static readonly ConditionalWeakTable<TabControl, TabHook> TabHooks =
            new ConditionalWeakTable<TabControl, TabHook>();
        private static readonly Dictionary<string, string> NavigationGlyphs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "MenuFlightData", "\uE9D2" },
                { "MenuFlightPlanner", "\uE707" },
                { "MenuInitConfig", "\uE713" },
                { "MenuConfigTune", "\uE9E9" },
                { "MenuSimulation", "\uE768" },
                { "MenuMap3D", "\uE774" },
                { "MenuMap", "\uE81D" },
                { "MenuTabletMirror", "\uE70A" },
                { "MenuThreeScreens", "\uE772" },
                { "MenuHelp", "\uE897" },
                { "MenuConnect", "\uE703" },
                { "menu_AdvanceLock", "\uE72E" }
            };

        private static string uiFontFamily;
        private static string iconFontFamily;

        public static string UiFontFamily
        {
            get
            {
                if (uiFontFamily == null)
                {
                    uiFontFamily = FindInstalledFont("Segoe UI Variable Text", "Segoe UI");
                }

                return uiFontFamily;
            }
        }

        public static bool MotionEnabled =>
            !SystemInformation.HighContrast && !SystemInformation.TerminalServerSession;

        public static void Apply(Control root)
        {
            if (root == null || root.IsDisposed)
            {
                return;
            }

            ApplyControl(root);
            HookChildren(root);

            foreach (Control child in root.Controls)
            {
                Apply(child);
            }
        }

        public static void ApplyMainShell(MainV2 main)
        {
            if (main == null || main.IsDisposed || main.MainMenu == null)
            {
                return;
            }

            Apply(main);

            MenuStrip menu = main.MainMenu;
            menu.SuspendLayout();
            try
            {
                menu.AutoSize = false;
                menu.Height = Scale(menu, 54);
                menu.Padding = new Padding(Scale(menu, 12), Scale(menu, 7), Scale(menu, 12), Scale(menu, 6));
                menu.BackColor = Surface;
                menu.ForeColor = TextPrimary;
                menu.BackgroundImage = null;
                menu.ImageScalingSize = new Size(Scale(menu, 18), Scale(menu, 18));
                menu.Renderer = new DIMPRenderer();
                menu.ShowItemToolTips = true;

                foreach (ToolStripItem item in menu.Items)
                {
                    ConfigureNavigationItem(item, menu);
                }
            }
            finally
            {
                menu.ResumeLayout(true);
            }

            main.panel1.BackColor = Surface;
            main.panel1.Padding = new Padding(0, 0, 0, 1);
            main.Invalidate(true);
        }

        public static Bitmap CreateIcon(string glyph, int pixelSize = 18, Color? color = null)
        {
            int size = Math.Max(12, pixelSize);
            Bitmap bitmap = new Bitmap(size, size);
            bitmap.SetResolution(96, 96);

            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = new Font(GetIconFontFamily(), size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                TextRenderer.DrawText(
                    graphics,
                    glyph,
                    font,
                    new Rectangle(0, 0, size, size),
                    color ?? TextPrimary,
                    Color.Transparent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            return bitmap;
        }

        public static Bitmap CreateNamedIcon(string name, int pixelSize = 18, Color? color = null)
        {
            string glyph;
            if (!NavigationGlyphs.TryGetValue(name ?? string.Empty, out glyph))
            {
                glyph = "\uE10C";
            }

            return CreateIcon(glyph, pixelSize, color);
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)(from.A + ((to.A - from.A) * amount)),
                (int)(from.R + ((to.R - from.R) * amount)),
                (int)(from.G + ((to.G - from.G) * amount)),
                (int)(from.B + ((to.B - from.B) * amount)));
        }

        private static void ConfigureNavigationItem(ToolStripItem item, ToolStrip owner)
        {
            item.ForeColor = TextPrimary;
            item.Font = CreateFont(9.25f,
                item.Name == "MenuConnect" ? FontStyle.Bold : FontStyle.Regular);

            ToolStripButton button = item as ToolStripButton;
            if (button == null)
            {
                return;
            }

            if (button.Name == "MenuArduPilot")
            {
                button.DisplayStyle = ToolStripItemDisplayStyle.Image;
                button.Margin = new Padding(5, 1, 3, 1);
                button.Padding = new Padding(3);
                return;
            }

            string glyph;
            if (NavigationGlyphs.TryGetValue(button.Name, out glyph))
            {
                button.Image = CreateIcon(glyph, Scale(owner, 18));
                button.ImageScaling = ToolStripItemImageScaling.None;
            }

            if (button.Name == "menu_AdvanceLock")
            {
                button.DisplayStyle = ToolStripItemDisplayStyle.Image;
                button.Margin = new Padding(2, 2, 2, 2);
                button.Padding = new Padding(7, 5, 7, 5);
                return;
            }

            button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
            button.Margin = button.Name == "MenuConnect"
                ? new Padding(6, 2, 2, 2)
                : new Padding(1, 2, 1, 2);
            button.Padding = button.Name == "MenuConnect"
                ? new Padding(13, 5, 13, 5)
                : new Padding(9, 5, 9, 5);
        }

        private static void ApplyControl(Control control)
        {
            ReplaceLegacyFont(control);

            ToolStrip toolStrip = control as ToolStrip;
            if (toolStrip != null)
            {
                StyleToolStrip(toolStrip);
                return;
            }

            ConnectionControl connection = control as ConnectionControl;
            if (connection != null)
            {
                connection.BackgroundImage = null;
                connection.BackColor = Surface;
                connection.ForeColor = TextPrimary;
            }

            Button button = control as Button;
            if (button != null && !(button is MyButton))
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = SurfaceHover;
                button.FlatAppearance.MouseDownBackColor = AccentPressed;
                button.UseVisualStyleBackColor = false;
                button.BackColor = SurfaceRaised;
                button.ForeColor = TextPrimary;
            }
            if (button != null)
            {
                ApplyCommandIcon(button);
            }

            TextBox textBox = control as TextBox;
            if (textBox != null)
            {
                textBox.BackColor = SurfaceRaised;
                textBox.ForeColor = TextPrimary;
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }

            RichTextBox richTextBox = control as RichTextBox;
            if (richTextBox != null && richTextBox.Name != "TXT_terminal")
            {
                richTextBox.BackColor = SurfaceRaised;
                richTextBox.ForeColor = TextPrimary;
                richTextBox.BorderStyle = BorderStyle.FixedSingle;
            }

            ComboBox comboBox = control as ComboBox;
            if (comboBox != null)
            {
                comboBox.BackColor = SurfaceRaised;
                comboBox.ForeColor = TextPrimary;
                comboBox.FlatStyle = FlatStyle.Flat;
            }

            UpDownBase upDown = control as UpDownBase;
            if (upDown != null)
            {
                upDown.BackColor = SurfaceRaised;
                upDown.ForeColor = TextPrimary;
                upDown.BorderStyle = BorderStyle.FixedSingle;
            }

            ListBox listBox = control as ListBox;
            if (listBox != null)
            {
                listBox.BackColor = Surface;
                listBox.ForeColor = TextPrimary;
                listBox.BorderStyle = BorderStyle.FixedSingle;
            }

            TreeView treeView = control as TreeView;
            if (treeView != null)
            {
                treeView.BackColor = Surface;
                treeView.ForeColor = TextPrimary;
                treeView.LineColor = BorderStrong;
                treeView.BorderStyle = BorderStyle.None;
            }

            ListView listView = control as ListView;
            if (listView != null)
            {
                listView.BackColor = Surface;
                listView.ForeColor = TextPrimary;
                listView.BorderStyle = BorderStyle.None;
            }

            DataGridView grid = control as DataGridView;
            if (grid != null)
            {
                StyleGrid(grid);
            }

            TabControl tabs = control as TabControl;
            if (tabs != null)
            {
                StyleTabs(tabs);
            }

            TabPage tabPage = control as TabPage;
            if (tabPage != null)
            {
                tabPage.BackColor = Canvas;
                tabPage.ForeColor = TextPrimary;
                tabPage.UseVisualStyleBackColor = false;
            }

            LinkLabel link = control as LinkLabel;
            if (link != null)
            {
                link.LinkColor = AccentBright;
                link.ActiveLinkColor = TextPrimary;
                link.VisitedLinkColor = Accent;
            }

            GroupBox group = control as GroupBox;
            if (group != null)
            {
                group.ForeColor = TextPrimary;
                group.Font = CreateFont(Math.Max(8.5f, group.Font.SizeInPoints), FontStyle.Regular);
            }

            Form form = control as Form;
            if (form != null)
            {
                form.BackColor = Canvas;
                form.ForeColor = TextPrimary;
            }
        }

        private static void StyleToolStrip(ToolStrip strip)
        {
            strip.BackColor = Surface;
            strip.ForeColor = TextPrimary;
            strip.Font = CreateFont(9f, FontStyle.Regular);
            strip.Renderer = strip.Renderer is DIMPRenderer ? strip.Renderer : new DIMPRenderer();
            strip.ImageScalingSize = new Size(18, 18);

            ContextMenuStrip context = strip as ContextMenuStrip;
            if (context != null)
            {
                context.ShowImageMargin = true;
                context.Padding = new Padding(4);
            }

            foreach (ToolStripItem item in strip.Items)
            {
                item.ForeColor = TextPrimary;
                item.Font = CreateFont(9f, FontStyle.Regular);

                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.HasDropDownItems)
                {
                    menuItem.DropDown.BackColor = SurfaceRaised;
                    menuItem.DropDown.ForeColor = TextPrimary;
                    menuItem.DropDown.Renderer = new DIMPRenderer();
                }
            }
        }

        private static void StyleGrid(DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = Canvas;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Border;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceRaised;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceRaised;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.Font = CreateFont(8.75f, FontStyle.Bold);
            grid.ColumnHeadersHeight = Math.Max(grid.ColumnHeadersHeight, 30);
            grid.RowsDefaultCellStyle.BackColor = Surface;
            grid.RowsDefaultCellStyle.ForeColor = TextPrimary;
            grid.RowsDefaultCellStyle.SelectionBackColor = AccentPressed;
            grid.RowsDefaultCellStyle.SelectionForeColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Canvas;
            grid.AlternatingRowsDefaultCellStyle.ForeColor = TextPrimary;
            grid.RowHeadersDefaultCellStyle.BackColor = SurfaceRaised;
            grid.RowHeadersDefaultCellStyle.ForeColor = TextSecondary;
        }

        private static void ApplyCommandIcon(Button button)
        {
            if (button.Image != null || button.Width < 70 || button.Height < 22 ||
                string.IsNullOrWhiteSpace(button.Text))
            {
                return;
            }

            string command = ((button.Name ?? string.Empty) + " " + button.Text).ToLowerInvariant();
            string glyph = null;
            if (ContainsAny(command, "refresh", "reload", "rescan"))
                glyph = "\uE72C";
            else if (ContainsAny(command, "delete", "remove", "clear"))
                glyph = "\uE74D";
            else if (ContainsAny(command, "save", "write", "apply"))
                glyph = "\uE74E";
            else if (ContainsAny(command, "load", "open", "browse", "read"))
                glyph = "\uE8E5";
            else if (ContainsAny(command, "download"))
                glyph = "\uE896";
            else if (ContainsAny(command, "upload", "send"))
                glyph = "\uE898";
            else if (ContainsAny(command, "add", "create", "new"))
                glyph = "\uE710";
            else if (ContainsAny(command, "stop", "disconnect"))
                glyph = "\uE71A";
            else if (ContainsAny(command, "start", "run"))
                glyph = "\uE768";
            else if (ContainsAny(command, "connect"))
                glyph = "\uE703";
            else if (ContainsAny(command, "reset", "restore"))
                glyph = "\uE777";

            if (glyph == null)
            {
                return;
            }

            button.Image = CreateIcon(glyph, 15, button.Enabled ? TextPrimary : TextDisabled);
            button.ImageAlign = ContentAlignment.MiddleLeft;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (value.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void StyleTabs(TabControl tabs)
        {
            tabs.BackColor = Canvas;
            tabs.ForeColor = TextPrimary;

            if (tabs.DrawMode != TabDrawMode.Normal || tabs.Appearance != TabAppearance.Normal ||
                (tabs.Alignment != TabAlignment.Top && tabs.Alignment != TabAlignment.Bottom) ||
                tabs.ItemSize.Height <= 4)
            {
                return;
            }

            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.Padding = new Point(12, 4);

            TabHook hook;
            if (!TabHooks.TryGetValue(tabs, out hook))
            {
                hook = new TabHook();
                tabs.DrawItem += DrawTab;
                TabHooks.Add(tabs, hook);
            }
        }

        private static void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabControl tabs = sender as TabControl;
            if (tabs == null || e.Index < 0 || e.Index >= tabs.TabPages.Count)
            {
                return;
            }

            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle bounds = e.Bounds;
            using (SolidBrush background = new SolidBrush(selected ? SurfaceRaised : Surface))
            {
                e.Graphics.FillRectangle(background, bounds);
            }

            if (selected)
            {
                int y = tabs.Alignment == TabAlignment.Bottom ? bounds.Top : bounds.Bottom - 2;
                using (Pen accent = new Pen(AccentBright, 2))
                {
                    e.Graphics.DrawLine(accent, bounds.Left + 7, y, bounds.Right - 7, y);
                }
            }

            using (Font font = CreateFont(8.75f, selected ? FontStyle.Bold : FontStyle.Regular))
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    tabs.TabPages[e.Index].Text,
                    font,
                    bounds,
                    selected ? TextPrimary : TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        private static void ReplaceLegacyFont(Control control)
        {
            if (control.Font == null || IsCanvasControl(control))
            {
                return;
            }

            string family = control.Font.FontFamily.Name;
            if (family.IndexOf("Microsoft Sans Serif", StringComparison.OrdinalIgnoreCase) >= 0 ||
                family.IndexOf("MS Sans Serif", StringComparison.OrdinalIgnoreCase) >= 0 ||
                family.Equals("Arial", StringComparison.OrdinalIgnoreCase) || control is Form)
            {
                float size = Math.Max(8.25f, control.Font.SizeInPoints);
                control.Font = CreateFont(size, control.Font.Style);
            }
        }

        private static bool IsCanvasControl(Control control)
        {
            string name = control.GetType().FullName ?? string.Empty;
            return name.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("GMapControl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("ZedGraph", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("AGauge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Font CreateFont(float size, FontStyle style)
        {
            try
            {
                return new Font(UiFontFamily, size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Segoe UI", size, style, GraphicsUnit.Point);
            }
        }

        private static string GetIconFontFamily()
        {
            if (iconFontFamily == null)
            {
                iconFontFamily = FindInstalledFont("Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");
            }

            return iconFontFamily;
        }

        private static string FindInstalledFont(params string[] preferred)
        {
            try
            {
                foreach (string candidate in preferred)
                {
                    foreach (FontFamily family in FontFamily.Families)
                    {
                        if (family.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            return family.Name;
                        }
                    }
                }
            }
            catch
            {
            }

            return "Segoe UI";
        }

        private static int Scale(Control control, int pixels)
        {
            try
            {
                using (Graphics graphics = control.CreateGraphics())
                {
                    return Math.Max(1, (int)Math.Round(pixels * graphics.DpiX / 96f));
                }
            }
            catch
            {
                return pixels;
            }
        }

        private static void HookChildren(Control control)
        {
            ControlHook hook;
            if (Hooks.TryGetValue(control, out hook))
            {
                return;
            }

            hook = new ControlHook();
            control.ControlAdded += ControlAdded;
            Hooks.Add(control, hook);
        }

        private static void ControlAdded(object sender, ControlEventArgs e)
        {
            Apply(e.Control);
        }

        private sealed class ControlHook
        {
        }

        private sealed class TabHook
        {
        }
    }
}
