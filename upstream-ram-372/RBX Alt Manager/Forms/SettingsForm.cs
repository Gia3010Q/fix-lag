using Microsoft.Win32;
using System;
using System.Diagnostics;
using RBX_Alt_Manager.Classes;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    public partial class SettingsForm : Form
    {
        private bool SettingsLoaded = false;
        private RegistryKey StartupKey;
        private CheckBox RenameRobloxWindowsCB;
        private CheckBox ShowRobloxWindowStatusCB;
        private CheckBox EnableSmallRobloxWindowsCB;
        private CheckBox ShowRobloxWindowSizeInTitleCB;
        private CheckBox AutoSizeRobloxWindowsCB;
        private NumericUpDown AutoRobloxWidthBox;
        private NumericUpDown AutoRobloxHeightBox;
        private TextBox NotesPathBox;
        private bool editingNotesPath;
        private bool fittingSettingsTabs;

        public SettingsForm()
        {
            AccountManager.SetDarkBar(Handle);

            InitializeComponent();
            InitializeMizuTab();
            this.Rescale();
            GeneralTab.Text = "General";
            DeveloperTab.Text = "Developer";
            MiscellaneousTab.Text = "Miscellaneous";
            SettingsTC.TabPages[3].Text = "Roblox Manager";
            ClientSize = new Size(Math.Max(ClientSize.Width, (int)(380 * Program.Scale)), ClientSize.Height);
            SettingsTC.SizeMode = TabSizeMode.Normal;
            SettingsTC.Padding = new Point((int)(20 * Program.Scale), (int)(4 * Program.Scale));
            SettingsTC.ItemSize = new Size((int)(92 * Program.Scale), (int)(24 * Program.Scale));
            SettingsTC.Resize += (sender, args) => FitSettingsTabs();
            MinimumSize = new Size(Width, MinimumSize.Height);
        }

        private void FitSettingsTabs()
        {
            if (fittingSettingsTabs || !SettingsTC.IsHandleCreated || SettingsTC.TabCount != 4)
                return;
            fittingSettingsTabs = true;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int extra = SettingsTC.ClientSize.Width -
                        SettingsTC.GetTabRect(SettingsTC.TabCount - 1).Right;
                    if (Math.Abs(extra) <= 3) break;
                    int horizontal = Math.Max(8, SettingsTC.Padding.X +
                        (int)Math.Round(extra / (2.0 * SettingsTC.TabCount)));
                    if (horizontal == SettingsTC.Padding.X) break;
                    SettingsTC.Padding = new Point(horizontal, SettingsTC.Padding.Y);
                }
            }
            finally { fittingSettingsTabs = false; }
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            AutoUpdateCB.Checked = false;
            AutoUpdateCB.Enabled = false;
            AutoUpdateCB.Text = "Updates: Roblox Manager installer";
            AsyncJoinCB.Checked = AccountManager.General.Get<bool>("AsyncJoin");
            LaunchDelayNumber.Value = Math.Max(30, Math.Min(60, AccountManager.General.Get<decimal>("AccountJoinDelay")));
            SavePasswordCB.Checked = AccountManager.General.Get<bool>("SavePasswords");
            DisableAgingAlertCB.Checked = AccountManager.General.Get<bool>("DisableAgingAlert");
            HideMRobloxCB.Checked = AccountManager.General.Get<bool>("HideRbxAlert");
            DisableImagesCB.Checked = AccountManager.General.Get<bool>("DisableImages");
            ShuffleLowestServerCB.Checked = AccountManager.General.Get<bool>("ShuffleChoosesLowestServer");
            MultiRobloxCB.Checked = AccountManager.General.Get<bool>("EnableMultiRbx");
            RegionFormatTB.Text = AccountManager.General.Get<string>("ServerRegionFormat");
            MaxRecentGamesNumber.Value = AccountManager.General.Get<int>("MaxRecentGames");

            EnableDMCB.Checked = AccountManager.Developer.Get<bool>("DevMode");
            EnableWSCB.Checked = AccountManager.Developer.Get<bool>("EnableWebServer");
            ERRPCB.Checked = true;
            ERRPCB.Enabled = false;
            ERRPCB.Text = "Password required for account APIs";
            AllowGCCB.Checked = AccountManager.WebServer.Get<bool>("AllowGetCookie");
            AllowGACB.Checked = AccountManager.WebServer.Get<bool>("AllowGetAccounts");
            AllowLACB.Checked = AccountManager.WebServer.Get<bool>("AllowLaunchAccount");
            AllowAECB.Checked = AccountManager.WebServer.Get<bool>("AllowAccountEditing");
            AllowExternalConnectionsCB.Checked = AccountManager.WebServer.Get<bool>("AllowExternalConnections");
            PasswordTextBox.Text = AccountManager.WebServer.Get("Password");
            PortNumber.Value = AccountManager.WebServer.Get<decimal>("WebServerPort");

            PresenceCB.Checked = AccountManager.General.Get<bool>("ShowPresence");
            PresenceUpdateRateNum .Value = AccountManager.General.Get<int>("PresenceUpdateRate");
            UnlockFPSCB.Checked = AccountManager.General.Get<bool>("UnlockFPS");
            MaxFPSValue.Value = AccountManager.General.Get<int>("MaxFPSValue");
            RenameRobloxWindowsCB.Checked = AccountManager.General.Get<bool>("RenameRobloxWindows");
            ShowRobloxWindowStatusCB.Checked = AccountManager.General.Get<bool>("ShowRobloxWindowStatus");
            EnableSmallRobloxWindowsCB.Checked = AccountManager.General.Get<bool>("EnableSmallRobloxWindows");
            ShowRobloxWindowSizeInTitleCB.Checked = AccountManager.General.Get<bool>("ShowRobloxWindowSizeInTitle");
            AutoRobloxWidthBox.Value = Math.Max(160, Math.Min(4096, AccountManager.General.Get<int>("AutoRobloxWindowWidth")));
            AutoRobloxHeightBox.Value = Math.Max(120, Math.Min(4096, AccountManager.General.Get<int>("AutoRobloxWindowHeight")));
            AutoSizeRobloxWindowsCB.Checked = AccountManager.General.Get<bool>("AutoSizeRobloxWindows");
            AutoRobloxWidthBox.Enabled = AutoRobloxHeightBox.Enabled = AutoSizeRobloxWindowsCB.Checked;
            NotesPathBox.Text = AccountManager.General.Get("NotesExportDirectory");

            if (AccountManager.General.Exists("CustomClientSettings") && File.Exists(AccountManager.General.Get<string>("CustomClientSettings")))
            {
                OverrideWithCustomCB.Checked = true;
                UnlockFPSCB.Enabled = false;
            }

            try { StartupKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); } catch { }

            if (StartupKey != null && StartupKey.GetValue(AppIdentity.StartupName) is string ExistingPath)
            {
                if (ExistingPath.Trim('"') != Application.ExecutablePath) // fix the path if moved
                    StartupKey.SetValue(AppIdentity.StartupName, "\"" + Application.ExecutablePath + "\"");

                StartOnPCStartup.Checked = true;
            }

            SettingsLoaded = true;

            ApplyTheme();
            FitSettingsTabs();
        }

        private void SettingsForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!SaveNotesPath())
            {
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            Hide();
        }

        private void InitializeMizuTab()
        {
            var tab = new TabPage("Roblox Manager");
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(10)
            };
            tab.Controls.Add(flow);
            SettingsTC.Controls.Add(tab);

            var externalMulti = AddMizuCheckBox(flow, "Use Multi Roblox from RAM", AppIdentity.ExternalMultiSetting);
            externalMulti.Checked = AccountManager.General.Get<bool>(AppIdentity.ExternalMultiSetting);
            externalMulti.CheckedChanged += (sender, args) =>
            {
                if (SettingsLoaded) AccountManager.Instance.UpdateMultiRoblox();
            };
            flow.SetFlowBreak(externalMulti, true);
            var multiHelp = new Label { Text = "Bật Multi Roblox ở RAM gốc trước, rồi giữ RAM mở.", Width = 330, Height = 38 };
            flow.Controls.Add(multiHelp);
            flow.SetFlowBreak(multiHelp, true);

            RenameRobloxWindowsCB = AddMizuCheckBox(flow, "Show username", "RenameRobloxWindows");
            ShowRobloxWindowStatusCB = AddMizuCheckBox(flow, "Show status and button", "ShowRobloxWindowStatus");
            EnableSmallRobloxWindowsCB = AddMizuCheckBox(flow, "Resize Roblox from window edges", "EnableSmallRobloxWindows");
            ShowRobloxWindowSizeInTitleCB = AddMizuCheckBox(flow, "Show size", "ShowRobloxWindowSizeInTitle");

            AutoSizeRobloxWindowsCB = AddMizuCheckBox(flow, "Open roblox with size", "AutoSizeRobloxWindows");
            AutoSizeRobloxWindowsCB.CheckedChanged += (sender, args) =>
                AutoRobloxWidthBox.Enabled = AutoRobloxHeightBox.Enabled = AutoSizeRobloxWindowsCB.Checked;
            var sizeRow = new FlowLayoutPanel
            {
                Width = 330,
                Height = 35,
                WrapContents = false,
                Margin = new Padding(3, 1, 3, 6)
            };
            sizeRow.Controls.Add(new Label { Text = "Width", AutoSize = true, Margin = new Padding(0, 7, 5, 0) });
            AutoRobloxWidthBox = new NumericUpDown { Minimum = 160, Maximum = 4096, Value = 800, Width = 76 };
            sizeRow.Controls.Add(AutoRobloxWidthBox);
            sizeRow.Controls.Add(new Label { Text = "Height", AutoSize = true, Margin = new Padding(13, 7, 5, 0) });
            AutoRobloxHeightBox = new NumericUpDown { Minimum = 120, Maximum = 4096, Value = 500, Width = 76 };
            sizeRow.Controls.Add(AutoRobloxHeightBox);
            AutoRobloxWidthBox.ValueChanged += (sender, args) => SaveAutoRobloxSize();
            AutoRobloxHeightBox.ValueChanged += (sender, args) => SaveAutoRobloxSize();
            flow.Controls.Add(sizeRow);
            flow.SetFlowBreak(sizeRow, true);

            var arrangeTitle = new Label { Text = "Arrange open Roblox windows", Width = 330, Height = 22,
                Margin = new Padding(3, 4, 3, 2) };
            flow.Controls.Add(arrangeTitle);
            flow.SetFlowBreak(arrangeTitle, true);
            var tileButton = new Button { Text = "Tile windows", Size = new Size(145, 27), Margin = new Padding(3) };
            tileButton.Click += async (sender, args) =>
            {
                if (AccountManager.Instance != null) await AccountManager.Instance.ArrangeRobloxWindows(false);
            };
            flow.Controls.Add(tileButton);
            var cascadeButton = new Button { Text = "Cascade windows", Size = new Size(145, 27), Margin = new Padding(3) };
            cascadeButton.Click += async (sender, args) =>
            {
                if (AccountManager.Instance != null) await AccountManager.Instance.ArrangeRobloxWindows(true);
            };
            flow.Controls.Add(cascadeButton);
            flow.SetFlowBreak(cascadeButton, true);

            var resetSizeButton = new Button
            {
                Text = "Reset size all tab",
                Size = new Size(296, 27),
                Margin = new Padding(3, 6, 3, 3)
            };
            resetSizeButton.Click += async (sender, args) =>
            {
                resetSizeButton.Enabled = false;
                try
                {
                    if (AccountManager.Instance != null)
                        await AccountManager.Instance.ResetAllRobloxWindowsToNativeMinimum();
                }
                finally { resetSizeButton.Enabled = true; }
            };
            flow.Controls.Add(resetSizeButton);
            flow.SetFlowBreak(resetSizeButton, true);

            var title = new Label { Text = "Notes export folder", Width = 330, Height = 22, Margin = new Padding(3, 15, 3, 2) };
            flow.Controls.Add(title);
            flow.SetFlowBreak(title, true);
            NotesPathBox = new TextBox { Width = 245, Margin = new Padding(3, 5, 3, 3) };
            NotesPathBox.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                SaveNotesPath();
            };
            NotesPathBox.Leave += (sender, args) => SaveNotesPath();
            flow.Controls.Add(NotesPathBox);
            var choose = new Button { Text = "Browse...", Size = new Size(86, 25), Margin = new Padding(3, 3, 3, 3) };
            choose.Click += (sender, args) => ChooseNotesFolder();
            flow.Controls.Add(choose);
            flow.SetFlowBreak(choose, true);
        }

        private void SaveAutoRobloxSize()
        {
            if (!SettingsLoaded) return;
            AccountManager.General.Set("AutoRobloxWindowWidth", AutoRobloxWidthBox.Value.ToString());
            AccountManager.General.Set("AutoRobloxWindowHeight", AutoRobloxHeightBox.Value.ToString());
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private CheckBox AddMizuCheckBox(FlowLayoutPanel flow, string text, string setting)
        {
            var check = new CheckBox { Text = text, Width = 260, Height = 27, Margin = new Padding(3, 2, 3, 2) };
            check.CheckedChanged += (sender, args) =>
            {
                if (!SettingsLoaded) return;
                AccountManager.General.Set(setting, check.Checked ? "true" : "false");
                AccountManager.IniSettings.Save("RAMSettings.ini");
                AccountManager.Instance?.ApplyRobloxFeatureSettings();
            };
            flow.Controls.Add(check);
            flow.SetFlowBreak(check, true);
            return check;
        }

        private bool SaveNotesPath()
        {
            if (!SettingsLoaded || editingNotesPath) return true;
            string entered = NotesPathBox.Text.Trim().Trim('"');
            string saved = AccountManager.General.Get("NotesExportDirectory") ?? string.Empty;
            if (string.Equals(entered, saved, StringComparison.OrdinalIgnoreCase)) return true;
            editingNotesPath = true;
            try
            {
                string fullPath = string.IsNullOrEmpty(entered) ? string.Empty :
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(entered));
                if (!string.IsNullOrEmpty(fullPath)) AccountManager.ExportNotes(fullPath);
                AccountManager.General.Set("NotesExportDirectory", fullPath);
                AccountManager.IniSettings.Save("RAMSettings.ini");
                NotesPathBox.Text = fullPath;
                Helper.SetToolTip(NotesPathBox, string.IsNullOrEmpty(fullPath) ?
                    "Enter a folder to create mizuautonote\\datauser.js" :
                    AccountManager.GetNotesOutputPath(fullPath));
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show("Could not export notes: " + exception.Message,
                    "Roblox Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                NotesPathBox.Text = saved;
                return false;
            }
            finally { editingNotesPath = false; }
        }

        private void ChooseNotesFolder()
        {
            if (!SaveNotesPath()) return;
            try
            {
                string current = AccountManager.General.Get("NotesExportDirectory");
                if (!ModernFolderDialog.TryChoose(this, current, out string selected)) return;
                NotesPathBox.Text = selected;
                SaveNotesPath();
            }
            catch (Exception exception)
            {
                MessageBox.Show("Could not select folder: " + exception.Message,
                    "Roblox Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #region General

        private void AutoUpdateCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("CheckForUpdates", AutoUpdateCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void AsyncJoinCB_CheckedChanged(object sender, EventArgs e)
        {
            LaunchDelayNumber.Enabled = !AsyncJoinCB.Checked;

            if (!SettingsLoaded) return;

            AccountManager.General.Set("AsyncJoin", AsyncJoinCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void LaunchDelayNumber_ValueChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("AccountJoinDelay", LaunchDelayNumber.Value.ToString());
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void MaxRecentGamesNumber_ValueChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("MaxRecentGames", MaxRecentGamesNumber.Value.ToString());
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void SavePasswordCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("SavePasswords", SavePasswordCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void DisableAgingAlertCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("DisableAgingAlert", DisableAgingAlertCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void HideMRobloxCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("HideRbxAlert", HideMRobloxCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void RegionFormatTB_TextChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("ServerRegionFormat", RegionFormatTB.Text, "Visit http://ip-api.com/json to see available format options");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void DisableImagesCB_CheckedChanged(object sender, EventArgs e)
        {
            AccountManager.General.Set("DisableImages", DisableImagesCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void ShuffleLowestServerCB_CheckedChanged(object sender, EventArgs e)
        {
            AccountManager.General.Set("ShuffleChoosesLowestServer", ShuffleLowestServerCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void MultiRobloxCB_CheckedChanged(object sender, EventArgs e)
        {
            AccountManager.General.Set("EnableMultiRbx", MultiRobloxCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");

            if (!AccountManager.Instance.UpdateMultiRoblox())
                MessageBox.Show("Multi Roblox is waiting for older Roblox sessions to close. Roblox Manager will retry automatically.", "Roblox Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void AutoCookieRefreshCB_CheckedChanged(object sender, EventArgs e)
        {
            AccountManager.General.Set("AutoCookieRefresh", AutoCookieRefreshCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");

            if (AccountManager.Instance.AutoCookieRefresh != null)
                AccountManager.Instance.AutoCookieRefresh.Enabled = AutoCookieRefreshCB.Checked;
        }

        private void StartOnPCStartup_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            if (StartOnPCStartup.Checked)
                StartupKey?.SetValue(AppIdentity.StartupName, "\"" + Application.ExecutablePath + "\"");
            else
                StartupKey?.DeleteValue(AppIdentity.StartupName, false);
        }

        private void EncryptionSelectionButton_Click(object sender, EventArgs e)
        {
            if (Utilities.YesNoPrompt("Settings", "Change Encryption Method", "Are you sure you want to change how your data is encrypted?", false))
                AccountManager.Instance.ResetEncryption(true);
        }

        #endregion

        #region Developer

        private void EnableDMCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.Developer.Set("DevMode", EnableDMCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void EnableWSCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.Developer.Set("EnableWebServer", EnableWSCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");

            MessageBox.Show("Roblox Manager must be restarted to enable this setting", "Roblox Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ERRPCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("EveryRequestRequiresPassword", ERRPCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void AllowGCCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("AllowGetCookie", AllowGCCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void AllowGACB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("AllowGetAccounts", AllowGACB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void AllowLACB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("AllowLaunchAccount", AllowLACB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void AllowAECB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("AllowAccountEditing", AllowAECB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void AllowExternalConnectionsCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("AllowExternalConnections", AllowExternalConnectionsCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");

            MessageBox.Show("Roblox Manager must be restarted to enable this setting\n\nThis setting requires admin privileges", "Roblox Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void PortNumber_ValueChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.WebServer.Set("WebServerPort", PortNumber.Value.ToString());
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void PasswordTextBox_TextChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            PasswordTextBox.Text = Regex.Replace(PasswordTextBox.Text, "[^0-9a-zA-Z ]", "");

            AccountManager.WebServer.Set("Password", PasswordTextBox.Text);
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        #endregion

        #region Miscellaneous

        private void PresenceCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("ShowPresence", PresenceCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void PresenceUpdateRateNum_ValueChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("PresenceUpdateRate", PresenceUpdateRateNum.Value.ToString());
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void UnlockFPSCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("UnlockFPS", UnlockFPSCB.Checked ? "true" : "false");
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void MaxFPSValue_ValueChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            AccountManager.General.Set("MaxFPSValue", MaxFPSValue.Value.ToString());
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void OverrideWithCustomCB_CheckedChanged(object sender, EventArgs e)
        {
            if (!SettingsLoaded) return;

            UnlockFPSCB.Enabled = !OverrideWithCustomCB.Checked;

            void Remove()
            {
                AccountManager.General.RemoveProperty("CustomClientSettings");
                OverrideWithCustomCB.Checked = false;
            }

            if (OverrideWithCustomCB.Checked)
            {
                if (CustomClientSettingsDialog.ShowDialog() == DialogResult.OK)
                {
                    if (File.Exists(CustomClientSettingsDialog.FileName) && File.ReadAllText(CustomClientSettingsDialog.FileName).TryParseJson<object>(out _))
                    {
                        string FileName = Path.Combine(Environment.CurrentDirectory, "CustomClientAppSettings.json");

                        File.Copy(CustomClientSettingsDialog.FileName, FileName);
                        AccountManager.General.Set("CustomClientSettings", FileName);
                    }
                    else
                        MessageBox.Show("Invalid file selected, make sure it contains valid JSON", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                    Remove();
            }
            else
                Remove();
            
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        private void ForceUpdateButton_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Cập nhật bằng bộ cài Roblox Manager riêng. Bộ cập nhật RAM gốc đã được tắt để giữ hai ứng dụng độc lập.", "Roblox Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion

        #region Themes

        public void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            ApplyTheme(Controls);
        }

        public void ApplyTheme(Control.ControlCollection _Controls)
        {
            foreach (Control control in _Controls)
            {
                if (control is Button || control is CheckBox)
                {
                    if (control is Button)
                    {
                        Button b = control as Button;
                        b.FlatStyle = ThemeEditor.ButtonStyle;
                        b.FlatAppearance.BorderColor = ThemeEditor.ButtonsBorder;
                    }

                    if (!(control is CheckBox)) control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
                else if (control is TextBox || control is RichTextBox)
                {
                    if (control is Classes.BorderedTextBox)
                    {
                        Classes.BorderedTextBox b = control as Classes.BorderedTextBox;
                        b.BorderColor = ThemeEditor.TextBoxesBorder;
                    }

                    if (control is Classes.BorderedRichTextBox)
                    {
                        Classes.BorderedRichTextBox b = control as Classes.BorderedRichTextBox;
                        b.BorderColor = ThemeEditor.TextBoxesBorder;
                    }

                    control.BackColor = ThemeEditor.TextBoxesBackground;
                    control.ForeColor = ThemeEditor.TextBoxesForeground;
                }
                else if (control is Label)
                {
                    control.BackColor = ThemeEditor.LabelTransparent ? Color.Transparent : ThemeEditor.LabelBackground;
                    control.ForeColor = ThemeEditor.LabelForeground;
                }
                else if (control is ListBox)
                {
                    control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
                else if (control is TabPage)
                {
                    ApplyTheme(control.Controls);

                    control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
                else if (control is FastColoredTextBoxNS.FastColoredTextBox)
                    control.ForeColor = Color.Black;
                else if (control is FlowLayoutPanel || control is Panel || control is TabControl)
                    ApplyTheme(control.Controls);
            }
        }

        #endregion
    }
}
