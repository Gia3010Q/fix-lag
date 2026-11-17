using FastColoredTextBoxNS;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Forms
{
    public partial class ThemeEditor : Form
    {
        private const string MizuThemeSection = "mizuauto";
        public static Color AccountBackground = SystemColors.Control;
        public static Color AccountForeground = SystemColors.ControlText;

        public static Color ButtonsBackground = SystemColors.Control;
        public static Color ButtonsForeground = SystemColors.ControlText;
        public static Color ButtonsBorder = SystemColors.Control;
        public static FlatStyle ButtonStyle = FlatStyle.Standard;

        public static Color FormsBackground = SystemColors.Control;
        public static Color FormsForeground = SystemColors.ControlText;
        public static bool UseDarkTopBar = true;
        public static bool ShowHeaders = true;

        public static Color TextBoxesBackground = SystemColors.Control;
        public static Color TextBoxesForeground = SystemColors.ControlText;
        public static Color TextBoxesBorder = Color.FromArgb(0x7A7A7A);

        public static Color LabelBackground = SystemColors.Control;
        public static Color LabelForeground = SystemColors.ControlText;
        public static bool LabelTransparent = true;
        
        public static bool LightImages = false;
        public static string BackgroundPath = "";
        public static string BackgroundMode = "Fill";
        public static int BackgroundScale = 100;
        public static int BackgroundTint = 55;
        private PictureBox backgroundPreview;
        private ComboBox backgroundModeBox;
        private NumericUpDown backgroundScaleBox;
        private TrackBar backgroundTintBar;
        private bool backgroundControlsReady;
        // public static bool UseNormalTabControls = false;

        public static string ToHexString(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static IniFile ThemeIni;
        private static IniSection Theme;

        public ThemeEditor()
        {
            AccountManager.SetDarkBar(Handle);

            InitializeComponent();
            BuildBackgroundControls();
            this.Rescale();
        }

        public void ApplyTheme()
        {
            BackColor = FormsBackground;
            ForeColor = FormsForeground;

            foreach (Control control in this.Controls)
            {
                if (control is Button || control is CheckBox)
                {
                    if (control is Button)
                    {
                        Button b = control as Button;
                        b.FlatStyle = ButtonStyle;
                        b.FlatAppearance.BorderColor = ButtonsBorder;
                    }

                    if (!(control is CheckBox)) control.BackColor = ButtonsBackground;
                    control.ForeColor = ButtonsForeground;
                }
                else if (control is TextBox || control is RichTextBox)
                {
                    if (control is Classes.BorderedTextBox)
                    {
                        Classes.BorderedTextBox b = control as Classes.BorderedTextBox;
                        b.BorderColor = TextBoxesBorder;
                    }

                    if (control is Classes.BorderedRichTextBox)
                    {
                        Classes.BorderedRichTextBox b = control as Classes.BorderedRichTextBox;
                        b.BorderColor = TextBoxesBorder;
                    }

                    control.BackColor = TextBoxesBackground;
                    control.ForeColor = TextBoxesForeground;
                }
                else if (control is Label)
                {
                    control.BackColor = LabelTransparent ? Color.Transparent : LabelBackground;
                    control.ForeColor = LabelForeground;
                }
                else if (control is ListBox)
                {
                    control.BackColor = ButtonsBackground;
                    control.ForeColor = ButtonsForeground;
                }
                else if (control is ComboBox || control is NumericUpDown)
                {
                    control.BackColor = TextBoxesBackground;
                    control.ForeColor = TextBoxesForeground;
                }
            }
            GlassBackground.Apply(this);
            RefreshBackgroundPreview();
        }

        private void BuildBackgroundControls()
        {
            Text = "Mizuauto · Edit Theme";
            ClientSize = new Size(548, 328);
            MinimumSize = Size;
            MaximumSize = Size;
            Selection.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Label imageTitle = new Label { Text = "NỀN", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(264, 12), AutoSize = true };
            backgroundPreview = new PictureBox { Location = new Point(264, 35), Size = new Size(270, 133), BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.StretchImage };
            Button choose = new Button { Text = "Chọn tệp", Location = new Point(264, 176), Size = new Size(130, 29) };
            Button clear = new Button { Text = "Xóa nền", Location = new Point(404, 176), Size = new Size(130, 29) };
            choose.Click += (sender, args) => ChooseBackground();
            clear.Click += (sender, args) =>
            {
                BackgroundPath = "";
                SaveBackgroundOptions();
            };

            Label modeLabel = new Label { Text = "Hiển thị", Location = new Point(264, 217), AutoSize = true };
            backgroundModeBox = new ComboBox { Location = new Point(358, 213), Size = new Size(176, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            backgroundModeBox.Items.AddRange(new object[] { "Fill", "Fit", "Stretch", "Center", "Tile" });
            backgroundModeBox.SelectedItem = backgroundModeBox.Items.Contains(BackgroundMode) ? BackgroundMode : "Fill";

            Label scaleLabel = new Label { Text = "Kích thước", Location = new Point(264, 251), AutoSize = true };
            backgroundScaleBox = new NumericUpDown { Location = new Point(358, 247), Size = new Size(76, 25), Minimum = 25, Maximum = 300, Increment = 5, Value = BackgroundScale };
            Label percentLabel = new Label { Text = "%", Location = new Point(440, 251), AutoSize = true };

            Label tintLabel = new Label { Text = "Độ tối", Location = new Point(264, 286), AutoSize = true };
            backgroundTintBar = new TrackBar { Location = new Point(350, 280), Size = new Size(184, 35), Minimum = 0, Maximum = 90, TickFrequency = 15, Value = BackgroundTint };

            Controls.AddRange(new Control[] { imageTitle, backgroundPreview, choose, clear, modeLabel, backgroundModeBox, scaleLabel, backgroundScaleBox, percentLabel, tintLabel, backgroundTintBar });
            backgroundModeBox.SelectedIndexChanged += (sender, args) => SaveBackgroundOptions();
            backgroundScaleBox.ValueChanged += (sender, args) => SaveBackgroundOptions();
            backgroundTintBar.ValueChanged += (sender, args) => SaveBackgroundOptions();
            backgroundControlsReady = true;
            RefreshBackgroundPreview();
        }

        private void ChooseBackground()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Ảnh, GIF, video|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.wmv;*.avi;*.m4v;*.mov|Ảnh và GIF|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Video|*.mp4;*.wmv;*.avi;*.m4v;*.mov", Title = "Chọn nền Mizuauto" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    bool dynamicMedia = ThemeMedia.IsVideo(dialog.FileName) || ThemeMedia.IsGif(dialog.FileName);
                    string target = dialog.FileName;
                    if (ThemeMedia.IsVideo(dialog.FileName))
                    {
                        if (new FileInfo(dialog.FileName).Length == 0) throw new ArgumentException("Video trống");
                    }
                    else
                    {
                        using (Image image = Image.FromFile(dialog.FileName))
                            if (image.Width < 1 || image.Height < 1) throw new ArgumentException("Ảnh không hợp lệ");
                    }
                    if (!dynamicMedia)
                    {
                        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mizuauto");
                        Directory.CreateDirectory(folder);
                        target = Path.Combine(folder, "theme-background" + Path.GetExtension(dialog.FileName).ToLowerInvariant());
                        if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                            File.Copy(dialog.FileName, target, true);
                    }
                    GlassBackground.Invalidate();
                    BackgroundPath = target;
                    if (ThemeMedia.IsVideo(target) && (string)backgroundModeBox.SelectedItem == "Tile")
                        backgroundModeBox.SelectedItem = "Fill";
                    SaveBackgroundOptions();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OutOfMemoryException)
                {
                    MessageBox.Show(this, "Không thể đọc media: " + ex.Message, "Mizuauto", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void SaveBackgroundOptions()
        {
            if (!backgroundControlsReady) return;
            BackgroundMode = backgroundModeBox.SelectedItem as string ?? "Fill";
            BackgroundScale = (int)backgroundScaleBox.Value;
            BackgroundTint = backgroundTintBar.Value;
            SaveTheme();
            AccountManager.Instance.ApplyTheme();
        }

        private void RefreshBackgroundPreview()
        {
            if (backgroundPreview == null) return;
            Image previous = backgroundPreview.Image;
            Bitmap preview = GlassBackground.Render(backgroundPreview.ClientSize);
            if (preview != null && ThemeMedia.IsVideo(BackgroundPath))
            {
                using (Graphics graphics = Graphics.FromImage(preview))
                using (Brush shade = new SolidBrush(Color.FromArgb(180, 18, 16, 30)))
                using (Brush ink = new SolidBrush(Color.White))
                using (Font symbol = new Font("Segoe UI", 25, FontStyle.Bold))
                using (Font name = new Font("Segoe UI", 8, FontStyle.Regular))
                {
                    graphics.FillRectangle(shade, new Rectangle(Point.Empty, preview.Size));
                    graphics.DrawString("▶", symbol, ink, new PointF(112, 18));
                    string fileName = Path.GetFileName(BackgroundPath);
                    graphics.DrawString(fileName, name, ink, new RectangleF(12, 90, preview.Width - 24, 30));
                }
            }
            backgroundPreview.Image = preview;
            previous?.Dispose();
        }

        public static void LoadTheme()
        {
            ThemeIni ??= File.Exists(Path.Combine(Environment.CurrentDirectory, "RAMTheme.ini")) ? new IniFile("RAMTheme.ini") : new IniFile();

            Theme = ThemeIni.Section(MizuThemeSection);
            if (!Theme.Exists("PaletteVersion"))
            {
                AccountBackground = Color.FromArgb(36, 29, 50);
                AccountForeground = Color.FromArgb(255, 242, 250);
                ButtonsBackground = Color.FromArgb(77, 60, 99);
                ButtonsForeground = Color.White;
                ButtonsBorder = Color.FromArgb(174, 141, 198);
                ButtonStyle = FlatStyle.Flat;
                FormsBackground = Color.FromArgb(23, 20, 33);
                FormsForeground = Color.FromArgb(255, 244, 250);
                TextBoxesBackground = Color.FromArgb(43, 36, 58);
                TextBoxesForeground = Color.White;
                TextBoxesBorder = Color.FromArgb(115, 90, 145);
                LabelBackground = FormsBackground;
                LabelForeground = FormsForeground;
                LabelTransparent = true;
                LightImages = true;
                SaveTheme();
            }

            // bool.TryParse(Theme.Get("DisableCustomTabs"), out UseNormalTabControls);

            // if (!Theme.Exists("DisableCustomTabs")) { Theme.Set("DisableCustomTabs", "false"); ThemeIni.Save("RAMTheme.ini"); }

            if (Theme.Exists("AccountsBG")) AccountBackground = ColorTranslator.FromHtml(Theme.Get("AccountsBG"));
            if (Theme.Exists("AccountsFG")) AccountForeground = ColorTranslator.FromHtml(Theme.Get("AccountsFG"));

            if (Theme.Exists("ButtonsBG")) ButtonsBackground = ColorTranslator.FromHtml(Theme.Get("ButtonsBG"));
            if (Theme.Exists("ButtonsFG")) ButtonsForeground = ColorTranslator.FromHtml(Theme.Get("ButtonsFG"));
            if (Theme.Exists("ButtonsBC")) ButtonsBorder = ColorTranslator.FromHtml(Theme.Get("ButtonsBC"));
            if (Theme.Exists("ButtonStyle") && Enum.TryParse(Theme.Get("ButtonStyle"), out FlatStyle BS)) ButtonStyle = BS;

            if (Theme.Exists("FormsBG")) FormsBackground = ColorTranslator.FromHtml(Theme.Get("FormsBG"));
            if (Theme.Exists("FormsFG")) FormsForeground = ColorTranslator.FromHtml(Theme.Get("FormsFG"));
            if (Theme.Exists("DarkTopBar") && bool.TryParse(Theme.Get("DarkTopBar"), out bool DarkTopBar)) UseDarkTopBar = DarkTopBar;
            if (Theme.Exists("ShowHeaders") && bool.TryParse(Theme.Get("ShowHeaders"), out bool bShowHeaders)) ShowHeaders = bShowHeaders;

            if (Theme.Exists("TextBoxesBG")) TextBoxesBackground = ColorTranslator.FromHtml(Theme.Get("TextBoxesBG"));
            if (Theme.Exists("TextBoxesFG")) TextBoxesForeground = ColorTranslator.FromHtml(Theme.Get("TextBoxesFG"));
            if (Theme.Exists("TextBoxesBC")) TextBoxesBorder = ColorTranslator.FromHtml(Theme.Get("TextBoxesBC"));

            if (Theme.Exists("TextBoxesBG") && !Theme.Exists("LabelsTransparent")) LabelTransparent = false; // support old themes
            if (Theme.Exists("LabelsBC")) LabelBackground = ColorTranslator.FromHtml(Theme.Get("LabelsBC")); else LabelBackground = TextBoxesBackground;
            if (Theme.Exists("LabelsFC")) LabelForeground = ColorTranslator.FromHtml(Theme.Get("LabelsFC")); else LabelForeground = TextBoxesForeground;
            if (Theme.Exists("LabelsTransparent") && bool.TryParse(Theme.Get("LabelsTransparent"), out bool bLabelTransparent)) LabelTransparent = bLabelTransparent;

            if (!Theme.Exists("LightImages")) Theme.Set("LightImages", FormsBackground.GetBrightness() < 0.5 ? "true" : "false");
            if (bool.TryParse(Theme.Get("LightImages"), out bool bLightImages)) LightImages = bLightImages;
            BackgroundPath = Theme.Exists("BackgroundPath") ? Theme.Get("BackgroundPath") : "";
            BackgroundMode = Theme.Exists("BackgroundMode") ? Theme.Get("BackgroundMode") : "Fill";
            if (Theme.Exists("BackgroundScale") && int.TryParse(Theme.Get("BackgroundScale"), out int scale)) BackgroundScale = Math.Max(25, Math.Min(300, scale));
            if (Theme.Exists("BackgroundTint") && int.TryParse(Theme.Get("BackgroundTint"), out int tint)) BackgroundTint = Math.Max(0, Math.Min(90, tint));
        }

        public static void SaveTheme()
        {
            ThemeIni ??= File.Exists(Path.Combine(Environment.CurrentDirectory, "RAMTheme.ini")) ? new IniFile("RAMTheme.ini") : new IniFile();
            Theme ??= ThemeIni.Section(MizuThemeSection);

            Theme.Set("AccountsBG", ToHexString(AccountBackground));
            Theme.Set("AccountsFG", ToHexString(AccountForeground));

            Theme.Set("ButtonsBG", ToHexString(ButtonsBackground));
            Theme.Set("ButtonsFG", ToHexString(ButtonsForeground));
            Theme.Set("ButtonsBC", ToHexString(ButtonsBorder));
            Theme.Set("ButtonStyle", ButtonStyle.ToString());

            Theme.Set("FormsBG", ToHexString(FormsBackground));
            Theme.Set("FormsFG", ToHexString(FormsForeground));
            Theme.Set("DarkTopBar", UseDarkTopBar.ToString());
            Theme.Set("ShowHeaders", ShowHeaders.ToString());

            Theme.Set("TextBoxesBG", ToHexString(TextBoxesBackground));
            Theme.Set("TextBoxesFG", ToHexString(TextBoxesForeground));
            Theme.Set("TextBoxesBC", ToHexString(TextBoxesBorder));

            Theme.Set("LabelsBC", ToHexString(LabelBackground));
            Theme.Set("LabelsFC", ToHexString(LabelForeground));
            Theme.Set("LabelsTransparent", LabelTransparent.ToString());

            Theme.Set("LightImages", LightImages.ToString());
            Theme.Set("PaletteVersion", "2");
            Theme.Set("BackgroundPath", BackgroundPath);
            Theme.Set("BackgroundMode", BackgroundMode);
            Theme.Set("BackgroundScale", BackgroundScale.ToString());
            Theme.Set("BackgroundTint", BackgroundTint.ToString());

            ThemeIni.Save("RAMTheme.ini");
        }

        private void SetBG_Click(object sender, EventArgs e)
        {
            string Selected = Selection.SelectedItem as string;

            if (string.IsNullOrEmpty(Selected)) return;

            if (SelectColor.ShowDialog() == DialogResult.OK)
            {
                switch (Selected)
                {
                    case "Accounts":
                        AccountBackground = SelectColor.Color;
                        break;

                    case "Buttons":
                        ButtonsBackground = SelectColor.Color;
                        break;

                    case "Forms":
                        FormsBackground = SelectColor.Color;
                        LightImages = FormsBackground.GetBrightness() < 0.5;
                        break;

                    case "Text Boxes":
                        TextBoxesBackground = SelectColor.Color;
                        break;

                    case "Labels":
                        LabelBackground = SelectColor.Color;
                        break;
                }

                AccountManager.Instance.ApplyTheme();
                SaveTheme();
            }
        }

        private void SetFG_Click(object sender, EventArgs e)
        {
            string Selected = Selection.SelectedItem as string;

            if (string.IsNullOrEmpty(Selected)) return;

            if (SelectColor.ShowDialog() == DialogResult.OK)
            {
                switch (Selected)
                {
                    case "Accounts":
                        AccountForeground = SelectColor.Color;
                        break;

                    case "Buttons":
                        ButtonsForeground = SelectColor.Color;
                        break;

                    case "Forms":
                        FormsForeground = SelectColor.Color;
                        break;

                    case "Text Boxes":
                        TextBoxesForeground = SelectColor.Color;
                        break;

                    case "Labels":
                        LabelForeground = SelectColor.Color;
                        break;
                }

                AccountManager.Instance.ApplyTheme();
                SaveTheme();
            }
        }

        private void ThemeEditor_FormClosing(object sender, FormClosingEventArgs e)
        {
            Hide();
            e.Cancel = true;
        }

        private void ShowControls(params Control[] controls)
        {
            SetBorder.Visible = false;
            ChangeStyle.Visible = false;
            HideHeaders.Visible = false;
            ToggleDarkTopBar.Visible = false;
            ToggleTransparentBG.Visible = false;

            foreach (Control control in controls)
                control.Visible = true;
        }

        private void Selection_SelectedIndexChanged(object sender, EventArgs e)
        {
            string Selected = Selection.SelectedItem as string;

            if (string.IsNullOrEmpty(Selected)) return;

            if (Selected == "Buttons")
                ShowControls(SetBorder, ChangeStyle);
            else if (Selected == "Text Boxes")
                ShowControls(SetBorder);
            else if (Selected == "Accounts")
                ShowControls(HideHeaders);
            else if (Selected == "Forms")
                ShowControls(ToggleDarkTopBar);
            else if (Selected == "Labels")
                ShowControls(ToggleTransparentBG);
            else
                ShowControls();
        }

        private void SetBorder_Click(object sender, EventArgs e)
        {
            string Selected = Selection.SelectedItem as string;

            if (string.IsNullOrEmpty(Selected)) return;

            if (SelectColor.ShowDialog() == DialogResult.OK)
            {
                switch (Selected)
                {
                    case "Buttons":
                        ButtonsBorder = SelectColor.Color;
                        break;

                    case "Text Boxes":
                        TextBoxesBorder = SelectColor.Color;
                        break;
                }

                AccountManager.Instance.ApplyTheme();
                SaveTheme();
            }
        }

        private void ChangeStyle_Click(object sender, EventArgs e)
        {
            ButtonStyle = ButtonStyle.Next();
            AccountManager.Instance.ApplyTheme();
            SaveTheme();
        }

        private void HideHeaders_Click(object sender, EventArgs e)
        {
            ShowHeaders = !ShowHeaders;
            AccountManager.Instance.ApplyTheme();
            SaveTheme();
        }

        private void ToggleTransparentBG_Click(object sender, EventArgs e)
        {
            LabelTransparent = !LabelTransparent;
            AccountManager.Instance.ApplyTheme();
            SaveTheme();
        }

        private void ToggleDarkTopBar_Click(object sender, EventArgs e)
        {
            UseDarkTopBar = !UseDarkTopBar;
            SaveTheme();
            MessageBox.Show("This option requires RAM to be restarted.\nThis may not work on older versions of windows.\nEnabled: " + (UseDarkTopBar ? "True" : "false"), "Roblox Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
