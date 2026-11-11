using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VkWorkspaceSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--check-profile") return ProfileEnvironment.Check(args[1]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                ProfileLauncher launcher = new ProfileLauncher(Path.Combine(local, "VKWorkspaceSwitcher"),
                    Path.Combine(local, "Programs", "VK WorkSpace", "vkworkspace.exe"));
                launcher.Initialize();
                ProfileRuntime runtime = new ProfileRuntime(launcher);
                if (args.Length == 2 && (args[0] == "--launch" || args[0] == "--close"))
                {
                    Profile profile = launcher.State.Profiles.FirstOrDefault(p => p.Id == args[1]);
                    if (profile == null) throw new InvalidOperationException("Профиль не найден.");
                    if (args[0] == "--close") runtime.Close(profile);
                    else runtime.Open(profile);
                    return 0;
                }
                if (args.Length != 0) throw new InvalidOperationException("Неизвестные параметры запуска.");
                Application.Run(new MainWindow(launcher, runtime));
                return 0;
            }
            catch (Exception error)
            {
                Theme.ShowError(null, error.Message);
                return 1;
            }
        }
    }

    internal sealed class MainWindow : Form
    {
        private readonly ProfileLauncher launcher;
        private readonly ProfileRuntime runtime;
        private readonly AccountList accounts = new AccountList();
        private readonly DarkButton open = new DarkButton { Primary = true };
        private readonly DarkButton close = new DarkButton { Text = "Закрыть", Visible = false };
        private readonly DarkButton add = new DarkButton();
        private readonly DarkButton more = new DarkButton();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem rename = new ToolStripMenuItem("Переименовать");
        private readonly ToolStripMenuItem headerColor = new ToolStripMenuItem("Цвет шапки…");
        private readonly ToolStripMenuItem folder = new ToolStripMenuItem("Открыть папку данных");
        private readonly ToolStripMenuItem settings = new ToolStripMenuItem("Путь к приложению…");
        private readonly ToolTip tooltips = new ToolTip();
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        private readonly Image headerIcon;
        private bool busy;
        private bool closing;
        private bool updatingTitles;
        private bool titlesPending;

        public MainWindow(ProfileLauncher launcher, ProfileRuntime runtime)
        {
            this.launcher = launcher;
            this.runtime = runtime;
            Theme.Apply(this);
            Text = "VK WorkSpace — аккаунты";
            ClientSize = new Size(600, 420);
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            Controls.Add(layout);

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            headerIcon = Theme.AppIcon.ToBitmap();
            header.Controls.Add(new PictureBox { Image = headerIcon, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(38, 38), Margin = new Padding(0, 2, 12, 0) }, 0, 0);
            header.Controls.Add(new Label { Text = "Аккаунты", Font = new Font("Segoe UI", 19F, FontStyle.Bold), Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 0) }, 1, 0);
            add.Text = "+ Добавить";
            add.Size = new Size(124, 42);
            add.Margin = Padding.Empty;
            header.Controls.Add(add, 2, 0);
            layout.Controls.Add(header, 0, 0);

            accounts.Dock = DockStyle.Fill;
            accounts.Margin = Padding.Empty;
            accounts.IntegralHeight = false;
            accounts.SelectedIndexChanged += delegate { UpdateActions(); };
            accounts.MouseDoubleClick += async delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && accounts.IndexFromPoint(e.Location) != ListBox.NoMatches)
                    await OpenSelected();
            };
            accounts.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    await OpenSelected();
                }
                else if (e.KeyCode == Keys.F2)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    RenameSelected();
                }
            };
            accounts.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                int index = accounts.IndexFromPoint(e.Location);
                if (index != ListBox.NoMatches) accounts.SelectedIndex = index;
            };
            layout.Controls.Add(accounts, 0, 1);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 22, 0, 0), ColumnCount = 5, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            open.Text = "Открыть";
            open.Dock = DockStyle.Fill;
            open.Margin = Padding.Empty;
            close.Dock = DockStyle.Fill;
            close.Margin = Padding.Empty;
            more.Text = "⋯";
            more.Font = new Font("Segoe UI", 17F, FontStyle.Bold);
            more.Dock = DockStyle.Fill;
            more.Margin = Padding.Empty;
            footer.Controls.Add(open, 0, 0);
            footer.Controls.Add(close, 2, 0);
            footer.Controls.Add(more, 4, 0);
            layout.Controls.Add(footer, 0, 2);

            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
            menu.BackColor = Theme.Surface;
            menu.ForeColor = Theme.Text;
            menu.Font = Font;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Padding = new Padding(4);
            menu.Items.AddRange(new ToolStripItem[] { rename, headerColor, folder, new ToolStripSeparator(), settings });
            foreach (ToolStripItem item in menu.Items) item.Padding = new Padding(9, 5, 9, 5);
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (busy) { e.Cancel = true; return; }
                UpdateActions();
            };
            accounts.ContextMenuStrip = menu;
            tooltips.SetToolTip(more, "Действия");
            more.AccessibleName = "Действия с аккаунтом";
            more.Click += delegate { menu.Show(more, new Point(more.Width, -6), ToolStripDropDownDirection.AboveLeft); };
            rename.Click += delegate { RenameSelected(); };
            headerColor.Click += async delegate { await ChangeHeaderColor(); };
            folder.Click += delegate { OpenFolder(); };
            settings.Click += delegate { SelectExecutable(); };
            open.Click += async delegate { await OpenSelected(); };
            close.Click += async delegate { await CloseSelected(); };
            add.Click += async delegate { await AddProfile(); };

            Reload("default");
            refresh.Interval = 3000;
            refresh.Tick += delegate { if (!busy) RefreshStatuses(); };
            refresh.Start();
            Shown += delegate { accounts.Focus(); RefreshWindowTitles(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }

        private AccountRow Selected { get { return accounts.SelectedItem as AccountRow; } }

        private void Reload(string selectedId)
        {
            accounts.BeginUpdate();
            try
            {
                accounts.Items.Clear();
                foreach (Profile profile in launcher.State.Profiles)
                {
                    AccountRow row = new AccountRow { Profile = profile, Running = runtime.IsRunning(profile) };
                    int index = accounts.Items.Add(row);
                    if (profile.Id == selectedId) accounts.SelectedIndex = index;
                }
            }
            finally { accounts.EndUpdate(); }
            UpdateActions();
            RefreshWindowTitles();
        }

        private void RefreshStatuses()
        {
            for (int index = 0; index < accounts.Items.Count; index++)
            {
                AccountRow row = (AccountRow)accounts.Items[index];
                bool running = runtime.IsRunning(row.Profile);
                if (row.Running == running) continue;
                row.Running = running;
                accounts.Invalidate(accounts.GetItemRectangle(index));
            }
            UpdateActions();
            RefreshWindowTitles();
        }

        private async void RefreshWindowTitles()
        {
            if (!IsHandleCreated || IsDisposed || Disposing) return;
            if (updatingTitles) { titlesPending = true; return; }
            updatingTitles = true;
            try
            {
                do
                {
                    titlesPending = false;
                    Profile[] running = accounts.Items.Cast<AccountRow>().Where(row => row.Running).Select(row => row.Profile).ToArray();
                    await Task.Run(delegate
                    {
                        foreach (Profile profile in running) runtime.UpdateWindowTitle(profile);
                    });
                } while (titlesPending && !IsDisposed && !Disposing);
            }
            catch (Exception error)
            {
                // Caption changes are best effort; they must not interrupt the account list.
                Debug.WriteLine("Window title update failed: " + error.Message);
            }
            finally { updatingTitles = false; }
        }

        private void UpdateActions()
        {
            AccountRow row = Selected;
            open.Enabled = rename.Enabled = headerColor.Enabled = folder.Enabled = row != null && !busy;
            add.Enabled = more.Enabled = accounts.Enabled = settings.Enabled = !busy;
            close.Visible = row != null && row.Running;
            close.Enabled = close.Visible && !busy;
            close.Text = closing ? "Закрываю…" : "Закрыть";
            open.Text = busy && !closing ? "Открываю…" : row != null && row.Running ? "Показать" : "Открыть";
        }

        private async Task CloseSelected()
        {
            AccountRow row = Selected;
            if (row == null || !row.Running || busy) return;
            busy = closing = true;
            UpdateActions();
            try { await Task.Run(delegate { return runtime.Close(row.Profile); }); }
            catch (Exception error) { ShowError(error); }
            finally
            {
                busy = closing = false;
                if (!IsDisposed) RefreshStatuses();
            }
        }

        private async Task OpenSelected()
        {
            AccountRow row = Selected;
            if (row == null || busy) return;
            busy = true;
            UpdateActions();
            try { await Task.Run(delegate { return runtime.Open(row.Profile); }); }
            catch (Exception error) { ShowError(error); }
            finally
            {
                busy = false;
                if (!IsDisposed) RefreshStatuses();
            }
        }

        private async Task AddProfile()
        {
            if (busy) return;
            using (NameDialog dialog = new NameDialog("Новый аккаунт", "Аккаунт " + (launcher.State.Profiles.Count + 1), "Создать и открыть"))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Profile profile = launcher.AddProfile(dialog.ProfileName);
                    Reload(profile.Id);
                    await OpenSelected();
                }
                catch (Exception error) { ShowError(error); }
            }
        }

        private void RenameSelected()
        {
            AccountRow row = Selected;
            if (row == null || busy) return;
            using (NameDialog dialog = new NameDialog("Переименовать", row.Profile.Name, "Сохранить"))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { launcher.RenameProfile(row.Profile.Id, dialog.ProfileName); Reload(row.Profile.Id); }
                catch (Exception error) { ShowError(error); }
            }
        }

        private async Task ChangeHeaderColor()
        {
            AccountRow row = Selected;
            if (row == null || busy) return;
            using (HeaderColorDialog dialog = new HeaderColorDialog(row.Profile))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    launcher.SetHeaderColor(row.Profile.Id, dialog.SelectedColor);
                    Reload(row.Profile.Id);
                    if (runtime.IsRunning(row.Profile))
                    {
                        busy = true;
                        UpdateActions();
                        bool applied = await Task.Run(delegate { return runtime.UpdateHeader(row.Profile); });
                        if (!applied) throw new InvalidOperationException("Цвет сохранён, но шапку пока не удалось изменить. Дождитесь полной загрузки клиента. Если это повторяется после обновления VK WorkSpace, потребуется обновить лаунчер.");
                    }
                }
                catch (Exception error) { ShowError(error); }
                finally { busy = false; if (!IsDisposed) RefreshStatuses(); }
            }
        }

        private void OpenFolder()
        {
            AccountRow row = Selected;
            if (row == null || busy) return;
            try
            {
                string path = launcher.DataPath(row.Profile);
                if (!Directory.Exists(path)) throw new InvalidOperationException("Сначала откройте этот аккаунт, чтобы VK WorkSpace создал папку данных.");
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception error) { ShowError(error); }
        }

        private void SelectExecutable()
        {
            if (busy) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "Выберите vkworkspace.exe", Filter = "VK WorkSpace (vkworkspace.exe)|vkworkspace.exe", FileName = launcher.State.ExecutablePath })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { launcher.SetExecutable(dialog.FileName); }
                catch (Exception error) { ShowError(error); }
            }
        }

        private void ShowError(Exception error)
        {
            if (!IsDisposed) Theme.ShowError(this, error.Message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refresh.Stop();
            refresh.Dispose();
            menu.Dispose();
            tooltips.Dispose();
            headerIcon.Dispose();
            base.OnFormClosed(e);
        }
    }

    internal sealed class NameDialog : Form
    {
        private readonly TextBox name = new TextBox();
        public string ProfileName { get { return name.Text.Trim(); } }

        public NameDialog(string title, string value, string action)
        {
            Theme.Apply(this);
            Text = title;
            ClientSize = new Size(396, 146);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            Panel fieldBorder = new Panel { Location = new Point(24, 23), Size = new Size(348, 40), BackColor = Theme.Border, Padding = new Padding(1) };
            Panel field = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(11, 9, 11, 7) };
            name.BorderStyle = BorderStyle.None;
            name.BackColor = Theme.Surface;
            name.ForeColor = Theme.Text;
            name.Dock = DockStyle.Fill;
            name.MaxLength = 80;
            name.Text = value;
            name.AccessibleName = "Название аккаунта";
            field.Controls.Add(name);
            fieldBorder.Controls.Add(field);
            Controls.Add(fieldBorder);

            DarkButton ok = new DarkButton { Text = action, Primary = true, Location = new Point(112, 84), Size = new Size(164, 38) };
            DarkButton cancel = new DarkButton { Text = "Отмена", Location = new Point(284, 84), Size = new Size(88, 38), DialogResult = DialogResult.Cancel };
            ok.Click += delegate
            {
                if (ProfileName.Length == 0) { name.Focus(); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            name.TextChanged += delegate { ok.Enabled = ProfileName.Length > 0; };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { name.Focus(); name.SelectAll(); };
        }
    }
}
