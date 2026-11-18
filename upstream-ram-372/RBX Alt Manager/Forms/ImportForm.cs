using RBX_Alt_Manager.Forms;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public partial class ImportForm : Form
    {
        public ImportForm()
        {
            AccountManager.SetDarkBar(Handle);

            InitializeComponent();
            this.Rescale();
            Text = "Roblox Manager · Nhập cookie";
        }

        public void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            foreach (Control control in this.Controls)
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
            }
        }

        private void ImportForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private async void ImportButton_Click(object sender, EventArgs e)
        {
            string[] lines = Accounts.Text.Split('\n');
            Accounts.Clear();
            Accounts.ReadOnly = true;
            ImportButton.Enabled = false;
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    string input = lines[i];
                    string error = null;
                    Account account = null;
                    try { account = await Task.Run(() => AccountManager.AddAccount(input, out error)); }
                    catch { error = "Không thêm được tài khoản. Kiểm tra mạng rồi thử lại."; }
                    // Never echo the session token into the result or a diagnostic log.
                    Accounts.AppendText(account != null ? "Đã thêm " + account.Username + "\r\n" :
                        "Dòng " + (i + 1) + ": " + error + "\r\n");
                }
            }
            finally
            {
                Accounts.ReadOnly = false;
                ImportButton.Enabled = true;
            }
        }
    }
}