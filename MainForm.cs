// GZ Mod Manager - WinForms UI
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GZModManager
{
    public class MainForm : Form
    {
        public const string AppVersion = "1.1.0";
        readonly ModManager mm;

        ToolStripComboBox profileBox;
        ToolStripButton ue4ssButton;
        ListView modList;
        ListView fileList;
        TextBox infoBox;
        TextBox logBox;
        ToolStripStatusLabel statusGame, statusDeploy;
        bool suppressEvents;
        Dictionary<string, Conflict> conflicts = new Dictionary<string, Conflict>();

        public MainForm(ModManager manager)
        {
            mm = manager;
            Text = "GZ Mod Manager " + AppVersion + " - " + GameInfo.Name;
            Width = 1150; Height = 720;
            MinimumSize = new Size(800, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => InstallPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            BuildUi();
            Shown += (s, e) => FirstRun();
            FormClosing += (s, e) => { mm.SaveModList(); mm.SaveSettings(); };
        }

        // ------------------------------------------------------------------ layout
        void BuildUi()
        {
            // Menu
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("Set game folder...", null, (s, e) => PickGameFolder());
            file.DropDownItems.Add("Open game folder", null, (s, e) => OpenFolder(mm.GamePath));
            file.DropDownItems.Add("Open mods (staging) folder", null, (s, e) => OpenFolder(mm.ModsDir));
            file.DropDownItems.Add("Open UE4SS log", null, (s, e) => OpenFile(P.Os(mm.GamePath, GameInfo.Ue4ssDir + "/UE4SS.log")));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Exit", null, (s, e) => Close());
            var mods = new ToolStripMenuItem("&Mods");
            mods.DropDownItems.Add("Install mod from file...", null, (s, e) => InstallDialog());
            mods.DropDownItems.Add("Install mod from folder...", null, (s, e) => InstallFolderDialog());
            mods.DropDownItems.Add("Import hand-installed UE4SS mods...", null, (s, e) => ImportUnmanaged(true));
            mods.DropDownItems.Add(new ToolStripSeparator());
            mods.DropDownItems.Add("Enable all", null, (s, e) => SetAll(true));
            mods.DropDownItems.Add("Disable all", null, (s, e) => SetAll(false));
            var tools = new ToolStripMenuItem("&UE4SS");
            tools.DropDownItems.Add("Download UE4SS (GitHub releases page)", null, (s, e) => Open("https://github.com/UE4SS-RE/RE-UE4SS/releases/tag/experimental-latest"));
            tools.DropDownItems.Add("Open UE4SS settings file", null, (s, e) => OpenFile(P.Os(mm.GamePath, GameInfo.Ue4ssDir + "/UE4SS-settings.ini")));
            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("How it works", null, (s, e) => ShowHelp());
            menu.Items.AddRange(new ToolStripItem[] { file, mods, tools, help });

            // Toolbar
            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), ImageScalingSize = new Size(16, 16) };
            tb.Items.Add(new ToolStripLabel("Profile:"));
            profileBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            profileBox.SelectedIndexChanged += (s, e) =>
            {
                if (suppressEvents || profileBox.SelectedItem == null) return;
                string p = (string)profileBox.SelectedItem;
                if (!p.Equals(mm.Profile, StringComparison.OrdinalIgnoreCase)) Try(() => { mm.SwitchProfile(p); RefreshAll(); });
            };
            tb.Items.Add(profileBox);
            var profMenu = new ToolStripDropDownButton("Profiles");
            profMenu.DropDownItems.Add("New empty profile...", null, (s, e) => NewProfile(false));
            profMenu.DropDownItems.Add("Copy current profile...", null, (s, e) => NewProfile(true));
            profMenu.DropDownItems.Add("Delete current profile", null, (s, e) => DeleteProfile());
            tb.Items.Add(profMenu);
            tb.Items.Add(new ToolStripSeparator());
            tb.Items.Add(Btn("Install Mod...", "Install a mod from a .zip, folder, or .pak (or drag files onto the window)", (s, e) => InstallDialog()));
            tb.Items.Add(new ToolStripSeparator());
            tb.Items.Add(Btn("Deploy", "Copy enabled mods into the game folder", (s, e) => DoDeploy()));
            tb.Items.Add(Btn("Purge", "Remove all deployed mod files from the game folder and restore originals", (s, e) => DoPurge()));
            tb.Items.Add(new ToolStripSeparator());
            var launch = Btn("\u25B6  Deploy && Launch", "Deploy changes, then start the game through Steam", (s, e) => DeployAndLaunch());
            launch.Font = new Font(Font, FontStyle.Bold);
            tb.Items.Add(launch);
            ue4ssButton = new ToolStripButton("UE4SS: ?") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Click to turn UE4SS on/off (needed for Lua mods)" };
            ue4ssButton.Click += (s, e) => ToggleUe4ss();
            tb.Items.Add(ue4ssButton);

            // Mod list
            modList = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
                HideSelection = false, MultiSelect = false, AllowDrop = true, GridLines = false
            };
            modList.Columns.Add("#", 40);
            modList.Columns.Add("Mod", 230);
            modList.Columns.Add("Type", 120);
            modList.Columns.Add("Version", 70);
            modList.Columns.Add("Conflicts", 90);
            modList.ItemChecked += (s, e) =>
            {
                if (suppressEvents) return;
                var m = e.Item.Tag as ModEntry;
                if (m != null) { mm.SetEnabled(m, e.Item.Checked); RefreshAll(); }
            };
            modList.SelectedIndexChanged += (s, e) => ShowDetails();
            modList.KeyDown += ModListKeyDown;
            modList.ItemDrag += (s, e) => modList.DoDragDrop(e.Item, DragDropEffects.Move);
            modList.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(typeof(ListViewItem))) e.Effect = DragDropEffects.Move;
                else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            modList.DragOver += (s, e) => { if (e.Data.GetDataPresent(typeof(ListViewItem))) e.Effect = DragDropEffects.Move; };
            modList.DragDrop += ModListDragDrop;
            modList.ContextMenuStrip = BuildModMenu();

            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 3, 6, 3), ForeColor = SystemColors.GrayText,
                Text = "Lower = higher priority (wins conflicts). Drag or Ctrl+Up/Down to reorder.\r\nDrop .zip files here to install."
            };
            var orderBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(2) };
            orderBar.Controls.Add(SmallBtn("Move up", (s, e) => MoveSelected(-1)));
            orderBar.Controls.Add(SmallBtn("Move down", (s, e) => MoveSelected(1)));
            orderBar.Controls.Add(SmallBtn("Rename", (s, e) => RenameSelected()));
            orderBar.Controls.Add(SmallBtn("Remove", (s, e) => RemoveSelected()));
            var leftPanel = new Panel { Dock = DockStyle.Fill };
            leftPanel.Controls.Add(modList);
            leftPanel.Controls.Add(orderBar);
            leftPanel.Controls.Add(hint);

            // Right side: tabs
            fileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            fileList.Columns.Add("File (relative to game folder)", 380);
            fileList.Columns.Add("Status", 220);
            infoBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Font = new Font("Consolas", 9f) };
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f) };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var t1 = new TabPage("Files && Conflicts"); t1.Controls.Add(fileList);
            var t2 = new TabPage("Info / Readme"); t2.Controls.Add(infoBox);
            var t3 = new TabPage("Log"); t3.Controls.Add(logBox);
            tabs.TabPages.AddRange(new[] { t1, t2, t3 });

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560, FixedPanel = FixedPanel.None };
            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(tabs);

            var status = new StatusStrip();
            statusGame = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusDeploy = new ToolStripStatusLabel();
            status.Items.AddRange(new ToolStripItem[] { statusGame, statusDeploy });

            Controls.Add(split);
            Controls.Add(tb);
            Controls.Add(menu);
            Controls.Add(status);
            MainMenuStrip = menu;
            Load += (s, e) => { try { split.SplitterDistance = (int)(ClientSize.Width * 0.52); } catch { } };
        }

        ToolStripButton Btn(string text, string tip, EventHandler click)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.Click += click;
            return b;
        }
        Button SmallBtn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 26 };
            b.Click += click;
            return b;
        }

        ContextMenuStrip BuildModMenu()
        {
            var cm = new ContextMenuStrip();
            cm.Items.Add("Enable / disable", null, (s, e) => { var it = Sel(); if (it != null) it.Checked = !it.Checked; });
            cm.Items.Add("Move up", null, (s, e) => MoveSelected(-1));
            cm.Items.Add("Move down", null, (s, e) => MoveSelected(1));
            cm.Items.Add("Move to top", null, (s, e) => MoveSelected(-10000));
            cm.Items.Add("Move to bottom", null, (s, e) => MoveSelected(10000));
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Rename...", null, (s, e) => RenameSelected());
            cm.Items.Add("Open mod folder", null, (s, e) => { var m = SelMod(); if (m != null) OpenFolder(Path.Combine(mm.ModsDir, m.Name)); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Remove mod", null, (s, e) => RemoveSelected());
            return cm;
        }

        // ------------------------------------------------------------------ refresh
        void RefreshAll()
        {
            suppressEvents = true;
            try
            {
                profileBox.Items.Clear();
                foreach (var p in mm.Profiles()) profileBox.Items.Add(p);
                profileBox.SelectedItem = mm.Profiles().FirstOrDefault(p => p.Equals(mm.Profile, StringComparison.OrdinalIgnoreCase));

                conflicts = mm.Conflicts();
                string selName = SelMod() != null ? SelMod().Name : null;
                modList.BeginUpdate();
                modList.Items.Clear();
                for (int i = 0; i < mm.Mods.Count; i++)
                {
                    var m = mm.Mods[i];
                    int wins = 0, loses = 0;
                    if (m.Enabled)
                        foreach (var c in conflicts.Values)
                        {
                            if (c.Winner == m.Name) wins++;
                            else if (c.Losers.Contains(m.Name)) loses++;
                        }
                    string cText = (wins > 0 ? "+" + wins : "") + (wins > 0 && loses > 0 ? " / " : "") + (loses > 0 ? "-" + loses : "");
                    var it = new ListViewItem(new[] { (i + 1).ToString(), m.Name, m.Type, m.Version, cText }) { Tag = m, Checked = m.Enabled };
                    if (!m.Enabled) it.ForeColor = SystemColors.GrayText;
                    else if (loses > 0 && wins == 0) it.BackColor = Color.FromArgb(255, 236, 236);
                    else if (wins > 0 && loses == 0) it.BackColor = Color.FromArgb(234, 248, 234);
                    else if (wins > 0) it.BackColor = Color.FromArgb(255, 248, 225);
                    it.ToolTipText = cText.Length == 0 ? "" : "+N: wins N file conflicts   -N: loses N file conflicts";
                    modList.Items.Add(it);
                    if (m.Name == selName) { it.Selected = true; it.Focused = true; }
                }
                modList.ShowItemToolTips = true;
                modList.EndUpdate();
            }
            finally { suppressEvents = false; }

            statusGame.Text = mm.GamePathValid ? "Game: " + mm.GamePath : "Game folder not set - use File > Set game folder";
            string st = mm.Ue4ssState();
            ue4ssButton.Text = "UE4SS: " + (st == "enabled" ? "ON" : st == "disabled" ? "OFF" : st == "missing" ? "not installed" : "?");
            ue4ssButton.ForeColor = st == "enabled" ? Color.DarkGreen : st == "disabled" ? Color.DarkOrange : Color.Firebrick;
            UpdateDeployStatus();
            ShowDetails();
        }

        void UpdateDeployStatus()
        {
            if (mm.DeployedStateDirty)
            {
                statusDeploy.Text = mm.IsDeployed ? "Changes not deployed yet" : "Not deployed";
                statusDeploy.ForeColor = Color.DarkOrange;
            }
            else
            {
                statusDeploy.Text = "Deployed (profile " + mm.Profile + ")";
                statusDeploy.ForeColor = Color.DarkGreen;
            }
        }

        void ShowDetails()
        {
            fileList.BeginUpdate();
            fileList.Items.Clear();
            var m = SelMod();
            if (m == null) { fileList.EndUpdate(); infoBox.Text = "Select a mod to see its files and info."; return; }
            foreach (var f in m.Files)
            {
                string status = m.Enabled ? "OK" : "(disabled)";
                Color bg = Color.Empty;
                Conflict c;
                if (m.Enabled && conflicts.TryGetValue(f, out c))
                {
                    if (c.Winner == m.Name) { status = "Wins over: " + string.Join(", ", c.Losers); bg = Color.FromArgb(234, 248, 234); }
                    else { status = "Overwritten by: " + c.Winner; bg = Color.FromArgb(255, 236, 236); }
                }
                var it = new ListViewItem(new[] { f.Replace('/', '\\'), status });
                if (bg != Color.Empty) it.BackColor = bg;
                fileList.Items.Add(it);
            }
            fileList.EndUpdate();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Name:       " + m.Name);
            sb.AppendLine("Type:       " + m.Type);
            sb.AppendLine("Version:    " + (m.Version.Length > 0 ? m.Version : "-"));
            sb.AppendLine("Source:     " + m.Source);
            sb.AppendLine("Installed:  " + m.Installed);
            sb.AppendLine("Files:      " + m.Files.Count);
            sb.AppendLine();
            string docs = Path.Combine(mm.ModsDir, m.Name, "docs");
            var readmes = new List<string>();
            if (Directory.Exists(docs)) readmes.AddRange(Directory.GetFiles(docs, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)));
            string root = Path.Combine(mm.ModsDir, m.Name, "root");
            readmes.AddRange(m.Files.Where(f => Path.GetFileName(f).StartsWith("readme", StringComparison.OrdinalIgnoreCase)).Select(f => P.Os(root, f)));
            foreach (var r in readmes.Take(3))
            {
                sb.AppendLine("----- " + Path.GetFileName(r) + " -----");
                try { sb.AppendLine(File.ReadAllText(r)); } catch { }
            }
            infoBox.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        void Log(IEnumerable<string> lines)
        {
            foreach (var l in lines) logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + l + "\r\n");
        }
        void Log(string line) { Log(new[] { line }); }

        // ------------------------------------------------------------------ helpers
        ListViewItem Sel() { return modList.SelectedItems.Count > 0 ? modList.SelectedItems[0] : null; }
        ModEntry SelMod() { var it = Sel(); return it == null ? null : it.Tag as ModEntry; }

        void Try(Action a)
        {
            try { a(); }
            catch (UnauthorizedAccessException ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message + "\n\nWindows blocked access to a file. Close the game if it's running, or run GZ Mod Manager as administrator.", "Access denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "GZ Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } }
        void OpenFolder(string p) { if (Directory.Exists(p)) Open(p); else MessageBox.Show(this, "Folder not found:\n" + p); }
        void OpenFile(string p) { if (File.Exists(p)) Open(p); else MessageBox.Show(this, "File not found:\n" + p); }

        static string Prompt(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new Form { Text = title, Width = 420, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
            {
                var l = new Label { Text = label, Left = 12, Top = 12, Width = 380 };
                var t = new TextBox { Text = value, Left = 12, Top = 34, Width = 380 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 236, Top = 70, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 317, Top = 70, Width = 75 };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK && t.Text.Trim().Length > 0 ? t.Text.Trim() : null;
            }
        }

        // ------------------------------------------------------------------ actions
        void FirstRun()
        {
            if (!mm.GamePathValid)
            {
                string found = ModManager.DetectGamePath(SteamRoots());
                if (found.Length > 0) { mm.GamePath = found; mm.SaveSettings(); Log("Found game: " + found); }
                else PickGameFolder();
            }
            if (File.Exists(mm.LastDeployPath) && mm.IsDeployed)
            {
                // Treat the previous deploy as current if the same profile was deployed.
                string last = File.ReadAllText(mm.LastDeployPath);
                if (last.StartsWith(mm.Profile + "|")) mm.DeployedStateDirty = false;
            }
            RefreshAll();
            ImportUnmanaged(false);
        }

        static IEnumerable<string> SteamRoots()
        {
            var list = new List<string>();
            try { list.Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", "") as string); } catch { }
            try { list.Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", "") as string); } catch { }
            list.Add(@"C:\Program Files (x86)\Steam");
            list.Add(@"C:\Program Files\Steam");
            return list.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.Replace('/', '\\'));
        }

        void PickGameFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Select the game folder (the one containing GZPro and Engine)." })
            {
                if (mm.GamePath.Length > 0) d.SelectedPath = mm.GamePath;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string p = d.SelectedPath;
                // Accept picking a subfolder like GZPro\Binaries\Win64.
                for (string cur = p; cur != null; cur = Path.GetDirectoryName(cur))
                    if (File.Exists(P.Os(cur, GameInfo.Exe))) { p = cur; break; }
                if (!File.Exists(P.Os(p, GameInfo.Exe)))
                {
                    MessageBox.Show(this, "That doesn't look like the game folder (couldn't find GZPro\\Binaries\\Win64\\GZProSteam-Win64-Shipping.exe).", "Wrong folder");
                    return;
                }
                if (mm.IsDeployed && !p.Equals(mm.GamePath, StringComparison.OrdinalIgnoreCase))
                    Try(() => Log(mm.Purge()));
                mm.GamePath = p;
                mm.SaveSettings();
                RefreshAll();
            }
        }

        void InstallDialog()
        {
            using (var d = new OpenFileDialog { Filter = "Mod archives and paks (*.zip;*.pak;*.utoc;*.ucas)|*.zip;*.pak;*.utoc;*.ucas|All files (*.*)|*.*", Multiselect = true, Title = "Install mod" })
                if (d.ShowDialog(this) == DialogResult.OK) InstallPaths(d.FileNames);
        }
        void InstallFolderDialog()
        {
            using (var d = new FolderBrowserDialog { Description = "Select an extracted mod folder." })
                if (d.ShowDialog(this) == DialogResult.OK) InstallPaths(new[] { d.SelectedPath });
        }

        void InstallPaths(string[] paths)
        {
            // A .pak dropped together with its .utoc/.ucas counts as one mod.
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ModManager.PakExts.Contains(ext))
                {
                    string key = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
                    if (!done.Add(key)) continue;
                }
                string guess = Directory.Exists(path) ? P.SafeName(Path.GetFileName(path.TrimEnd('\\'))) : ModManager.GuessName(path);
                string name = Prompt(this, "Install mod", "Name for this mod:\n", guess);
                if (name == null) continue;
                try
                {
                    var m = mm.Install(path, name, false);
                    Log("Installed '" + m.Name + "' (" + m.Type + ", " + m.Files.Count + " files)");
                }
                catch (UnknownLayoutException ex)
                {
                    var r = MessageBox.Show(this,
                        "GZ Mod Manager couldn't tell where this mod's files go:\n\n  " + string.Join("\n  ", ex.Plan.Unknown.Take(12)) +
                        (ex.Plan.Unknown.Count > 12 ? "\n  ..." : "") +
                        "\n\nInstall it anyway, treating the archive as if it were the game folder itself?",
                        "Unknown mod layout", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes) Try(() => { var m = mm.Install(path, name, true); Log("Installed '" + m.Name + "' as game-folder files"); });
                }
                catch (Exception ex)
                {
                    Log("ERROR installing " + Path.GetFileName(path) + ": " + ex.Message);
                    MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            RefreshAll();
        }

        void ImportUnmanaged(bool explicitRequest)
        {
            var found = mm.FindUnmanagedUe4ssMods();
            if (found.Count == 0)
            {
                if (explicitRequest) MessageBox.Show(this, "No hand-installed UE4SS mods found.", "Import");
                return;
            }
            var r = MessageBox.Show(this,
                "These UE4SS mods were installed by hand, outside GZ Mod Manager:\n\n  " + string.Join("\n  ", found) +
                "\n\nMove them into GZ Mod Manager so you can turn them on/off and order them here?\n" +
                "(They'll be removed from the game folder and put back when you Deploy.)",
                "Import existing mods", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            foreach (var n in found)
                Try(() => { var m = mm.ImportUnmanaged(n); Log("Imported '" + m.Name + "'"); });
            RefreshAll();
            if (mm.Mods.Any(m => m.Enabled)) DoDeploy();
        }

        void DoDeploy()
        {
            Try(() =>
            {
                Cursor = Cursors.WaitCursor;
                try { Log(mm.Deploy()); }
                finally { Cursor = Cursors.Default; }
            });
            RefreshAll();
        }
        void DoPurge()
        {
            Try(() => Log(mm.Purge()));
            RefreshAll();
        }

        void DeployAndLaunch()
        {
            if (!mm.GamePathValid) { PickGameFolder(); if (!mm.GamePathValid) return; }
            if (mm.Mods.Any(m => m.Enabled && m.Type.StartsWith("UE4SS")) && mm.Ue4ssState() != "enabled")
            {
                var r = MessageBox.Show(this, "You have UE4SS mods enabled, but UE4SS is " + mm.Ue4ssState() + ". They won't load.\n\nLaunch anyway?", "UE4SS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            if (mm.DeployedStateDirty || !mm.IsDeployed) DoDeploy();
            if (mm.DeployedStateDirty) return; // deploy failed
            Log("Launching game through Steam...");
            Open("steam://rungameid/" + GameInfo.SteamAppId);
        }

        void ToggleUe4ss()
        {
            string st = mm.Ue4ssState();
            if (st == "missing")
            {
                var r = MessageBox.Show(this, "UE4SS isn't installed. It's required for Lua mods (like GrabBlockFix).\n\nOpen the download page? Get the zDEV-UE4SS zip and extract it into:\n" +
                    P.Os(mm.GamePath, GameInfo.Win64), "UE4SS", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) Open("https://github.com/UE4SS-RE/RE-UE4SS/releases/tag/experimental-latest");
                return;
            }
            Try(() => { mm.SetUe4ss(st != "enabled"); Log("UE4SS turned " + (st != "enabled" ? "ON" : "OFF")); });
            RefreshAll();
        }

        void SetAll(bool on)
        {
            foreach (var m in mm.Mods) m.Enabled = on;
            mm.SaveModList();
            mm.DeployedStateDirty = true;
            RefreshAll();
        }

        void MoveSelected(int delta)
        {
            var it = Sel();
            if (it == null) return;
            int i = it.Index;
            mm.Move(i, i + delta);
            RefreshAll();
        }

        void RenameSelected()
        {
            var m = SelMod();
            if (m == null) return;
            string n = Prompt(this, "Rename mod", "New name:", m.Name);
            if (n != null) Try(() => mm.RenameMod(m, n));
            RefreshAll();
        }

        void RemoveSelected()
        {
            var m = SelMod();
            if (m == null) return;
            if (MessageBox.Show(this, "Remove '" + m.Name + "' from GZ Mod Manager?\nIts files will be deleted from the mods folder." +
                (mm.IsDeployed ? "\n\nDeployed files stay in the game until you Deploy or Purge." : ""),
                "Remove mod", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Try(() => { mm.RemoveMod(m); Log("Removed '" + m.Name + "'"); });
            RefreshAll();
        }

        void NewProfile(bool copy)
        {
            string n = Prompt(this, copy ? "Copy profile" : "New profile", "Profile name:", copy ? mm.Profile + " copy" : "New profile");
            if (n != null) Try(() => mm.CreateProfile(n, copy));
            RefreshAll();
        }
        void DeleteProfile()
        {
            if (MessageBox.Show(this, "Delete profile '" + mm.Profile + "'? (Mods themselves are kept.)", "Delete profile", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Try(() => mm.DeleteProfile(mm.Profile));
            RefreshAll();
        }

        void ModListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Down) { MoveSelected(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
        }

        void ModListDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { InstallPaths((string[])e.Data.GetData(DataFormats.FileDrop)); return; }
            var dragged = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
            if (dragged == null) return;
            var pt = modList.PointToClient(new Point(e.X, e.Y));
            var target = modList.GetItemAt(pt.X, pt.Y);
            int to = target == null ? modList.Items.Count - 1 : target.Index;
            mm.Move(dragged.Index, to);
            RefreshAll();
        }

        void ShowHelp()
        {
            MessageBox.Show(this,
                "GZ Mod Manager keeps mods in its own 'mods' folder (next to the exe), not in the game.\n\n" +
                "- Tick a mod to enable it. Lower in the list = higher priority: when two mods have the same file, the lower one wins.\n" +
                "- Deploy copies the enabled mods into the game folder. Original game files that get replaced are backed up and restored on Purge.\n" +
                "- Purge removes everything GZ Mod Manager put in the game folder.\n" +
                "- Profiles are separate mod lists (which mods are on, and their order).\n" +
                "- Deploy & Launch deploys any changes, then starts the game through Steam.\n\n" +
                "Supported mod types:\n" +
                "- UE4SS Lua / C++ mods (folders with Scripts\\main.lua or dlls\\main.dll) -> ue4ss\\Mods\n" +
                "- Pak mods (.pak/.utoc/.ucas) -> Content\\Paks\\~mods (numbered by priority)\n" +
                "- Blueprint mods in a LogicMods folder -> Content\\Paks\\LogicMods\n" +
                "- Archives laid out like the game folder (containing GZPro\\...)\n\n" +
                "UE4SS mods are for offline / local play. Turn UE4SS OFF before playing online.",
                "How GZ Mod Manager works");
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            ModManager mm;
            try { mm = new ModManager(root); }
            catch (Exception ex)
            {
                MessageBox.Show("GZ Mod Manager couldn't start in:\n" + root + "\n\n" + ex.Message +
                    "\n\nPut GZ Mod Manager in a folder you can write to (e.g. Documents or Desktop), not Program Files.", "GZ Mod Manager");
                return;
            }
            Application.Run(new MainForm(mm));
        }
    }
}
