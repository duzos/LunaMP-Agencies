using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LmpAgenciesUpdater;

namespace LmpAgenciesInstaller
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new InstallerWindow()); }
            catch (Exception error) { MessageBox.Show(error.Message, "LunaMultiplayer Agencies installer", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    internal sealed class InstallerWindow : Form
    {
        private readonly ComboBox paths = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly Button browse = new Button { Text = "Browse...", AutoSize = true };
        private readonly Button install = new Button { Text = "Install", AutoSize = true };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(590, 0), Text = "Detecting KSP installations..." };
        private readonly Label destination = new Label { AutoSize = true, MaximumSize = new Size(590, 0) };
        private readonly int build;
        private readonly string tag, hash;
        private bool busy;

        public InstallerWindow()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("installer.txt"))
            {
                if (stream == null) throw new InvalidOperationException("This development executable has no client payload. Download the packaged installer from the release.");
                using (var reader = new StreamReader(stream))
                { build = int.Parse(reader.ReadLine()); tag = reader.ReadLine(); hash = reader.ReadLine(); }
            }
            Text = "LunaMultiplayer Agencies — " + tag;
            ClientSize = new Size(650, 330); MinimumSize = new Size(650, 330); StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 7, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = "Install LunaMultiplayer Agencies " + tag, AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 2);
            var help = new Label { Text = "Choose your KSP installation. Close KSP before installing.\nYour settings, saves and other mods are kept.", AutoSize = true, Padding = new Padding(0, 10, 0, 10) };
            layout.Controls.Add(help, 0, 1); layout.SetColumnSpan(help, 2);
            layout.Controls.Add(paths, 0, 2); layout.Controls.Add(browse, 1, 2);
            layout.Controls.Add(destination, 0, 3); layout.SetColumnSpan(destination, 2);
            layout.Controls.Add(install, 1, 4);
            layout.Controls.Add(status, 0, 5); layout.SetColumnSpan(status, 2); Controls.Add(layout);
            paths.TextChanged += (_, __) => destination.Text = "Install into: " + paths.Text;
            browse.Click += (_, __) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Select the KSP folder containing GameData and KSP_x64.exe", ShowNewFolderButton = false, SelectedPath = paths.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK) paths.Text = dialog.SelectedPath;
            };
            install.Click += InstallClicked;
            FormClosing += (_, e) => { if (busy) e.Cancel = true; };
            Shown += async (_, __) =>
            {
                install.Enabled = false;
                try
                {
                    var detected = await Task.Run(() => KspDiscovery.DetectInstalled());
                    paths.Items.AddRange(detected);
                    if (detected.Length > 0) paths.SelectedIndex = 0;
                    status.Text = detected.Length == 0 ? "No KSP installation found. Use Browse to choose its folder." : "Found " + detected.Length + " KSP installation(s). Confirm the selected folder, then click Install.";
                }
                catch (Exception e) { status.Text = "Automatic detection failed: " + e.Message + " Use Browse."; }
                finally { install.Enabled = true; }
            };
        }

        private async void InstallClicked(object sender, EventArgs args)
        {
            var root = paths.Text.Trim();
            busy = true; install.Enabled = browse.Enabled = paths.Enabled = false; status.Text = "Installing " + tag + "...";
            try
            {
                var result = await Task.Run(() => Install(root));
                status.Text = result;
                MessageBox.Show(this, result, "Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e) { status.Text = e.Message; MessageBox.Show(this, e.Message, "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; install.Enabled = browse.Enabled = paths.Enabled = true; }
        }

        private string Install(string selectedRoot)
        {
            var root = Path.GetFullPath(selectedRoot);
            var executable = Assembly.GetExecutingAssembly().Location;
            InstallerSafety.ValidateTarget(root, executable); InstallerSafety.RequireGameClosed(root);
            Mutex mutex;
            if (!UpdateInstaller.TryAcquire(Path.Combine(root, "GameData"), out mutex)) throw new InvalidOperationException("Another installer or updater is already using this KSP folder.");
            try
            {
                var stage = Path.Combine(root, "LunaMultiplayer-install-" + build + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                var zip = Path.Combine(stage, "client.zip");
                using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("client.zip"))
                {
                    if (payload == null) throw new InvalidOperationException("The embedded client package is missing.");
                    using (var file = new FileStream(zip, FileMode.CreateNew, FileAccess.Write)) payload.CopyTo(file);
                }
                var options = new HelperOptions { Mode = HelperMode.Client, Build = build, Target = Path.Combine(root, "GameData"), Zip = zip, Sha256 = hash,
                    Extract = Path.Combine(stage, "extract"), Backup = Path.Combine(stage, "backup"), Result = Path.Combine(stage, "result.txt") };
                var result = UpdateInstaller.Run(options, (_, __) => true, clientInstaller: true, preSwap: () =>
                {
                    InstallerSafety.ValidateTarget(root, executable); InstallerSafety.CheckTree(stage); InstallerSafety.RequireGameClosed(root);
                });
                try { File.Delete(zip); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (!result.Success) throw new InvalidOperationException(result.Message + "\nRecovery files: " + stage);
                return "Installed " + tag + " into " + root + ".\nYou can now start KSP.\nBackup and result: " + stage;
            }
            finally { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
