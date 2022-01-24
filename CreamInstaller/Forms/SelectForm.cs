﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Gameloop.Vdf.Linq;

using Microsoft.Win32;

namespace CreamInstaller
{
    internal partial class SelectForm : CustomForm
    {
        internal SelectForm(IWin32Window owner) : base(owner)
        {
            InitializeComponent();
            Text = Program.ApplicationName;
            Program.SelectForm = this;
        }

        private static async Task<List<string>> GameLibraryDirectories() => await Task.Run(() =>
        {
            List<string> gameDirectories = new();
            if (Program.Canceled) return gameDirectories;
            string steamInstallPath = Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Valve\\Steam", "InstallPath", null) as string;
            steamInstallPath ??= Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\Valve\\Steam", "InstallPath", null) as string;
            if (steamInstallPath != null && Directory.Exists(steamInstallPath))
            {
                string libraryFolder = steamInstallPath + @"\steamapps";
                if (Directory.Exists(libraryFolder))
                {
                    gameDirectories.Add(libraryFolder);
                    string libraryFolders = libraryFolder + @"\libraryfolders.vdf";
                    if (File.Exists(libraryFolders) && ValveDataFile.TryDeserialize(File.ReadAllText(libraryFolders, Encoding.UTF8), out VProperty _result))
                    {
                        dynamic result = _result;
                        foreach (dynamic property in result?.Value) if (int.TryParse(property.Key, out int _))
                            {
                                string path = property.Value?.path?.ToString();
                                if (string.IsNullOrWhiteSpace(path)) continue;
                                path += @"\steamapps";
                                if (Directory.Exists(path) && !gameDirectories.Contains(path)) gameDirectories.Add(path);
                            }
                    }
                }
            }
            return gameDirectories;
        });

        private static async Task<List<string>> GetDllDirectoriesFromGameDirectory(string gameDirectory) => await Task.Run(async () =>
        {
            List<string> dllDirectories = new();
            if (Program.Canceled || !Directory.Exists(gameDirectory)) return null;
            string api = gameDirectory + @"\steam_api.dll";
            string api64 = gameDirectory + @"\steam_api64.dll";
            if (File.Exists(api) || File.Exists(api64)) dllDirectories.Add(gameDirectory);
            string[] directories = Directory.GetDirectories(gameDirectory);
            foreach (string _directory in directories)
            {
                if (Program.Canceled) return null;
                try
                {
                    List<string> moreDllDirectories = await GetDllDirectoriesFromGameDirectory(_directory);
                    if (moreDllDirectories is not null) dllDirectories.AddRange(moreDllDirectories);
                }
                catch { }
            }
            if (!dllDirectories.Any()) return null;
            return dllDirectories;
        });

        private static async Task<List<Tuple<int, string, string, int, string>>> GetGamesFromLibraryDirectory(string libraryDirectory) => await Task.Run(() =>
        {
            List<Tuple<int, string, string, int, string>> games = new();
            if (Program.Canceled || !Directory.Exists(libraryDirectory)) return null;
            string[] files = Directory.GetFiles(libraryDirectory);
            foreach (string file in files)
            {
                if (Program.Canceled) return null;
                if (Path.GetExtension(file) == ".acf" && ValveDataFile.TryDeserialize(File.ReadAllText(file, Encoding.UTF8), out VProperty _result))
                {
                    dynamic result = _result;
                    string _appid = result.Value?.appid?.ToString();
                    string installdir = result.Value?.installdir?.ToString();
                    string name = result.Value?.name?.ToString();
                    string _buildid = result.Value?.buildid?.ToString();
                    if (string.IsNullOrWhiteSpace(_appid)
                        || string.IsNullOrWhiteSpace(installdir)
                        || string.IsNullOrWhiteSpace(name)
                        || string.IsNullOrWhiteSpace(_buildid))
                        continue;
                    string branch = result.Value?.UserConfig?.betakey?.ToString();
                    if (string.IsNullOrWhiteSpace(branch)) branch = "public";
                    string gameDirectory = libraryDirectory + @"\common\" + installdir;
                    if (!int.TryParse(_appid, out int appid)) continue;
                    if (!int.TryParse(_buildid, out int buildid)) continue;
                    games.Add(new(appid, name, branch, buildid, gameDirectory));
                }
            }
            if (!games.Any()) return null;
            return games;
        });

        internal List<TreeNode> TreeNodes => GatherTreeNodes(selectionTreeView.Nodes);
        private List<TreeNode> GatherTreeNodes(TreeNodeCollection nodeCollection)
        {
            List<TreeNode> treeNodes = new();
            foreach (TreeNode rootNode in nodeCollection)
            {
                treeNodes.Add(rootNode);
                treeNodes.AddRange(GatherTreeNodes(rootNode.Nodes));
            }
            return treeNodes;
        }

        internal List<Task> RunningTasks = new();

        private async Task GetCreamApiApplicablePrograms(IProgress<int> progress)
        {
            if (Program.Canceled) return;
            List<Tuple<int, string, string, int, string>> applicablePrograms = new();
            string launcherRootDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs\\Paradox Interactive";
            if (Directory.Exists(launcherRootDirectory))
                applicablePrograms.Add(new(0, "Paradox Launcher", "", 0, launcherRootDirectory));
            List<string> gameLibraryDirectories = await GameLibraryDirectories();
            foreach (string libraryDirectory in gameLibraryDirectories)
            {
                List<Tuple<int, string, string, int, string>> games = await GetGamesFromLibraryDirectory(libraryDirectory);
                if (games is not null)
                    foreach (Tuple<int, string, string, int, string> game in games)
                        applicablePrograms.Add(game);
            }

            int cur = 0;
            RunningTasks.Clear();
            foreach (Tuple<int, string, string, int, string> program in applicablePrograms)
            {
                int appId = program.Item1;
                string name = program.Item2;
                string branch = program.Item3;
                int buildId = program.Item4;
                string directory = program.Item5;
                ProgramSelection selection = ProgramSelection.FromAppId(appId);
                if (selection is not null) selection.Validate();
                if (Program.Canceled) return;
                if (Program.BlockProtectedGames && Program.IsGameBlocked(name, directory)) continue;
                RunningTasks.Add(Task.Run(async () =>
                {
                    if (Program.Canceled) return;
                    List<string> dllDirectories = await GetDllDirectoriesFromGameDirectory(directory);
                    if (dllDirectories is null) return;
                    VProperty appInfo = null;
                    if (appId > 0) appInfo = await SteamCMD.GetAppInfo(appId, branch, buildId);
                    if (appId > 0 && appInfo is null) return;
                    if (Program.Canceled) return;
                    ConcurrentDictionary<int, string> dlc = new();
                    List<Task> dlcTasks = new();
                    List<int> dlcIds = await SteamCMD.ParseDlcAppIds(appInfo);
                    if (dlcIds.Count > 0)
                    {
                        foreach (int id in dlcIds)
                        {
                            if (Program.Canceled) return;
                            Task task = Task.Run(async () =>
                            {
                                if (Program.Canceled) return;
                                string dlcName = null;
                                VProperty dlcAppInfo = await SteamCMD.GetAppInfo(id);
                                if (dlcAppInfo is not null) dlcName = dlcAppInfo.Value?.TryGet("common")?.TryGet("name")?.ToString();
                                if (Program.Canceled) return;
                                if (string.IsNullOrWhiteSpace(dlcName)) return; //dlcName = "Unknown DLC";
                                dlc[id] = /*$"[{id}] " +*/ dlcName;
                                progress.Report(++cur);
                            });
                            dlcTasks.Add(task);
                            RunningTasks.Add(task);
                            progress.Report(-RunningTasks.Count);
                            Thread.Sleep(10); // to reduce control & window freezing
                        }
                    }
                    else if (appId > 0) return;
                    if (Program.Canceled) return;
                    if (string.IsNullOrWhiteSpace(name)) return;

                    selection ??= new();
                    selection.Usable = true;
                    selection.SteamAppId = appId;
                    selection.Name = name;
                    selection.RootDirectory = directory;
                    selection.SteamApiDllDirectories = dllDirectories;
                    selection.AppInfo = appInfo;
                    if (selection.Icon is null)
                    {
                        if (appId == 0) selection.Icon = Program.GetFileIconImage(directory + @"\launcher\bootstrapper-v2.exe");
                        else
                        {
                            selection.IconStaticID = appInfo?.Value?.TryGet("common")?.TryGet("icon")?.ToString();
                            selection.ClientIconStaticID = appInfo?.Value?.TryGet("common")?.TryGet("clienticon")?.ToString();
                        }
                    }
                    if (allCheckBox.Checked) selection.Enabled = true;

                    foreach (Task task in dlcTasks)
                    {
                        if (Program.Canceled) return;
                        await task;
                    }
                    if (Program.Canceled) return;
                    selectionTreeView.Invoke((MethodInvoker)delegate
                    {
                        if (Program.Canceled) return;
                        TreeNode programNode = TreeNodes.Find(s => s.Name == "" + appId) ?? new();
                        programNode.Name = "" + appId;
                        programNode.Text = /*(appId > 0 ? $"[{appId}] " : "") +*/ name;
                        programNode.Checked = selection.Enabled;
                        programNode.Remove();
                        selectionTreeView.Nodes.Add(programNode);
                        if (appId == 0) // paradox launcher
                        {
                            // maybe add game and/or dlc choice here?
                        }
                        else
                        {
                            foreach (KeyValuePair<int, string> dlcApp in dlc)
                            {
                                if (Program.Canceled || programNode is null) return;
                                selection.AllSteamDlc[dlcApp.Key] = dlcApp.Value;
                                if (allCheckBox.Checked) selection.SelectedSteamDlc[dlcApp.Key] = dlcApp.Value;
                                TreeNode dlcNode = TreeNodes.Find(s => s.Name == "" + dlcApp.Key) ?? new();
                                dlcNode.Name = "" + dlcApp.Key;
                                dlcNode.Text = dlcApp.Value;
                                dlcNode.Checked = selection.SelectedSteamDlc.Contains(dlcApp);
                                dlcNode.Remove();
                                programNode.Nodes.Add(dlcNode);
                            }
                        }
                    });

                    progress.Report(++cur);
                }));
                progress.Report(-RunningTasks.Count);
            }
            foreach (Task task in RunningTasks.ToList())
            {
                if (Program.Canceled) return;
                await task;
            }
            progress.Report(RunningTasks.Count);
        }

        private async void OnLoad()
        {
        retry:
            try
            {
                Program.Canceled = false;
                blockedGamesCheckBox.Enabled = false;
                blockProtectedHelpButton.Enabled = false;
                cancelButton.Enabled = true;
                scanButton.Enabled = false;
                noneFoundLabel.Visible = false;
                allCheckBox.Enabled = false;
                installButton.Enabled = false;
                uninstallButton.Enabled = installButton.Enabled;
                selectionTreeView.Enabled = false;
                progressLabel.Visible = true;
                progressBar1.Visible = true;
                progressBar1.Value = 0;
                groupBox1.Size = new(groupBox1.Size.Width, groupBox1.Size.Height - 44);

                bool setup = true;
                int maxProgress = 0;
                int curProgress = 0;
                Progress<int> progress = new();
                IProgress<int> iProgress = progress;
                progress.ProgressChanged += (sender, _progress) =>
                {
                    if (Program.Canceled) return;
                    if (_progress < 0) maxProgress = -_progress;
                    else curProgress = _progress;
                    int p = Math.Max(Math.Min((int)((float)(curProgress / (float)maxProgress) * 100), 100), 0);
                    progressLabel.Text = setup ? $"Setting up SteamCMD . . . {p}% ({curProgress}/{maxProgress})"
                        : $"Gathering and caching your applicable games and their DLCs . . . {p}% ({curProgress}/{maxProgress})";
                    progressBar1.Value = p;
                };

                iProgress.Report(-1660); // not exact, number varies
                int cur = 0;
                iProgress.Report(cur);
                progressLabel.Text = "Setting up SteamCMD . . . ";
                if (!Directory.Exists(SteamCMD.DirectoryPath)) Directory.CreateDirectory(SteamCMD.DirectoryPath);

                FileSystemWatcher watcher = new(SteamCMD.DirectoryPath);
                watcher.Changed += (sender, e) => iProgress.Report(++cur);
                watcher.Filter = "*";
                watcher.IncludeSubdirectories = true;
                watcher.EnableRaisingEvents = true;
                await SteamCMD.Setup();
                watcher.Dispose();

                setup = false;
                progressLabel.Text = "Gathering and caching your applicable games and their DLCs . . . ";
                ProgramSelection.ValidateAll();
                TreeNodes.ForEach(node =>
                {
                    if (!int.TryParse(node.Name, out int appId) || node.Parent is null && ProgramSelection.FromAppId(appId) is null) node.Remove();
                });
                await GetCreamApiApplicablePrograms(iProgress);

                progressBar1.Value = 100;
                groupBox1.Size = new(groupBox1.Size.Width, groupBox1.Size.Height + 44);
                progressLabel.Visible = false;
                progressBar1.Visible = false;
                selectionTreeView.Enabled = ProgramSelection.All.Any();
                allCheckBox.Enabled = selectionTreeView.Enabled;
                noneFoundLabel.Visible = !selectionTreeView.Enabled;
                installButton.Enabled = ProgramSelection.AllUsableEnabled.Any();
                uninstallButton.Enabled = installButton.Enabled;
                cancelButton.Enabled = false;
                scanButton.Enabled = true;
                blockedGamesCheckBox.Enabled = true;
                blockProtectedHelpButton.Enabled = true;
            }
            catch (Exception e)
            {
                if (ExceptionHandler.OutputException(e)) goto retry;
                Close();
            }
        }

        private void OnTreeViewNodeCheckedChanged(object sender, TreeViewEventArgs e)
        {
            if (e.Action == TreeViewAction.Unknown) return;
            TreeNode node = e.Node;
            if (node is not null)
            {
                ProgramSelection selection = ProgramSelection.FromAppId(int.Parse(node.Name));
                if (selection is null)
                {
                    TreeNode parent = node.Parent;
                    if (parent is not null)
                    {
                        ProgramSelection.FromAppId(int.Parse(parent.Name)).ToggleDlc(int.Parse(node.Name), node.Checked);
                        parent.Checked = parent.Nodes.Cast<TreeNode>().ToList().Any(treeNode => treeNode.Checked);
                    }
                }
                else
                {
                    if (selection.AllSteamDlc.Any())
                    {
                        selection.ToggleAllDlc(node.Checked);
                        node.Nodes.Cast<TreeNode>().ToList().ForEach(treeNode => treeNode.Checked = node.Checked);
                    }
                    else selection.Enabled = node.Checked;
                    allCheckBox.CheckedChanged -= OnAllCheckBoxChanged;
                    allCheckBox.Checked = TreeNodes.TrueForAll(treeNode => treeNode.Checked);
                    allCheckBox.CheckedChanged += OnAllCheckBoxChanged;
                }
            }
            installButton.Enabled = ProgramSelection.AllUsableEnabled.Any();
            uninstallButton.Enabled = installButton.Enabled;
        }

        private class TreeNodeSorter : IComparer
        {
            public int Compare(object a, object b)
            {
                if (!int.TryParse((a as TreeNode).Name, out int A)) return 1;
                if (!int.TryParse((b as TreeNode).Name, out int B)) return 0;
                return A > B ? 1 : 0;
            }
        }

        private void OnLoad(object sender, EventArgs _)
        {
            selectionTreeView.TreeViewNodeSorter = new TreeNodeSorter();
            selectionTreeView.AfterCheck += OnTreeViewNodeCheckedChanged;
            Dictionary<string, Image> images = new();
            Task.Run(async () =>
            {
                images["File Explorer"] = Program.GetFileExplorerImage();
                images["SteamDB"] = await Program.GetImageFromUrl("https://steamdb.info/favicon.ico");
                images["Steam Store"] = await Program.GetImageFromUrl("https://store.steampowered.com/favicon.ico");
                images["Steam Community"] = await Program.GetImageFromUrl("https://steamcommunity.com/favicon.ico");
            });
            Image Image(string identifier) => images.GetValueOrDefault(identifier, null);
            selectionTreeView.NodeMouseClick += (sender, e) =>
            {
                TreeNode node = e.Node;
                TreeNode parentNode = node.Parent;
                if (!int.TryParse(node.Name, out int appId)) return;
                ProgramSelection selection = ProgramSelection.FromAppId(appId);
                if (e.Button == MouseButtons.Right && node.Bounds.Contains(e.Location))
                {
                    selectionTreeView.SelectedNode = node;
                    nodeContextMenu.Items.Clear();
                    if (selection is not null)
                    {
                        nodeContextMenu.Items.Add(new ToolStripMenuItem(selection.Name, selection.Icon));
                        nodeContextMenu.Items.Add(new ToolStripSeparator());
                        nodeContextMenu.Items.Add(new ToolStripMenuItem("Open Root Directory", Image("File Explorer"),
                            new EventHandler((sender, e) => Program.OpenDirectoryInFileExplorer(selection.RootDirectory))));
                        for (int i = 0; i < selection.SteamApiDllDirectories.Count; i++)
                        {
                            string directory = selection.SteamApiDllDirectories[i];
                            nodeContextMenu.Items.Add(new ToolStripMenuItem($"Open Steamworks Directory ({i + 1})", Image("File Explorer"),
                                new EventHandler((sender, e) => Program.OpenDirectoryInFileExplorer(directory))));
                        }
                    }
                    else
                    {
                        nodeContextMenu.Items.Add(new ToolStripMenuItem(node.Text));
                        nodeContextMenu.Items.Add(new ToolStripSeparator());
                    }
                    if (appId != 0)
                    {
                        nodeContextMenu.Items.Add(new ToolStripMenuItem("Open SteamDB", Image("SteamDB"),
                            new EventHandler((sender, e) => Program.OpenUrlInInternetBrowser("https://steamdb.info/app/" + appId))));
                        nodeContextMenu.Items.Add(new ToolStripMenuItem("Open Steam Store", Image("Steam Store"),
                            new EventHandler((sender, e) => Program.OpenUrlInInternetBrowser("https://store.steampowered.com/app/" + appId))));
                        if (selection is not null) nodeContextMenu.Items.Add(new ToolStripMenuItem("Open Steam Community", selection.ClientIcon ?? Image("Steam Community"),
                            new EventHandler((sender, e) => Program.OpenUrlInInternetBrowser("https://steamcommunity.com/app/" + appId))));
                    }
                    nodeContextMenu.Show(selectionTreeView, e.Location);
                }
            };
            OnLoad();
        }

        private static void PopulateParadoxLauncherDlc(ProgramSelection paradoxLauncher = null)
        {
            paradoxLauncher ??= ProgramSelection.FromAppId(0);
            if (paradoxLauncher is not null)
            {
                paradoxLauncher.ExtraSteamAppIdDlc.Clear();
                foreach (ProgramSelection selection in ProgramSelection.AllUsableEnabled)
                {
                    if (selection.Name == paradoxLauncher.Name) continue;
                    if (selection.AppInfo.Value?.TryGet("extended")?.TryGet("publisher")?.ToString() != "Paradox Interactive") continue;
                    paradoxLauncher.ExtraSteamAppIdDlc.Add(new(selection.SteamAppId, selection.Name, selection.SelectedSteamDlc));
                }
                if (!paradoxLauncher.ExtraSteamAppIdDlc.Any())
                    foreach (ProgramSelection selection in ProgramSelection.AllUsable)
                    {
                        if (selection.Name == paradoxLauncher.Name) continue;
                        if (selection.AppInfo.Value?.TryGet("extended")?.TryGet("publisher")?.ToString() != "Paradox Interactive") continue;
                        paradoxLauncher.ExtraSteamAppIdDlc.Add(new(selection.SteamAppId, selection.Name, selection.AllSteamDlc));
                    }
            }
        }

        private static bool ParadoxLauncherDlcDialog(Form form)
        {
            ProgramSelection paradoxLauncher = ProgramSelection.FromAppId(0);
            if (paradoxLauncher is not null && paradoxLauncher.Enabled)
            {
                PopulateParadoxLauncherDlc(paradoxLauncher);
                if (!paradoxLauncher.ExtraSteamAppIdDlc.Any())
                {
                    return new DialogForm(form).Show(Program.ApplicationName, SystemIcons.Warning,
                        $"WARNING: There are no installed games with DLC that can be added to the Paradox Launcher!" +
                        "\n\nInstalling CreamAPI for the Paradox Launcher is pointless, since no DLC will be added to the configuration!",
                        "Ignore", "Cancel") != DialogResult.OK;
                }
            }
            return false;
        }

        private void OnAccept(bool uninstall = false)
        {
            if (ProgramSelection.All.Any())
            {
                foreach (ProgramSelection selection in ProgramSelection.AllUsableEnabled)
                    if (!Program.IsProgramRunningDialog(this, selection)) return;
                if (ParadoxLauncherDlcDialog(this)) return;
                Hide();
                InstallForm installForm = new(this, uninstall);
                installForm.ShowDialog();
                if (installForm.Reselecting)
                {
                    this.InheritLocation(installForm);
                    Show();
                    OnLoad();
                }
                else Close();
            }
        }

        private void OnInstall(object sender, EventArgs e) => OnAccept(false);
        private void OnUninstall(object sender, EventArgs e) => OnAccept(true);
        private void OnScan(object sender, EventArgs e) => OnLoad();

        private void OnCancel(object sender, EventArgs e)
        {
            progressLabel.Text = "Cancelling . . . ";
            Program.Cleanup();
        }

        private void OnAllCheckBoxChanged(object sender, EventArgs e)
        {
            bool shouldCheck = false;
            TreeNodes.ForEach(node =>
            {
                if (node.Parent is null)
                {
                    if (!node.Checked) shouldCheck = true;
                    if (node.Checked != shouldCheck)
                    {
                        node.Checked = shouldCheck;
                        OnTreeViewNodeCheckedChanged(null, new(node, TreeViewAction.ByMouse));
                    }
                }
            });
            allCheckBox.Checked = shouldCheck;
        }

        private void OnBlockProtectedGamesCheckBoxChanged(object sender, EventArgs e)
        {
            Program.BlockProtectedGames = blockedGamesCheckBox.Checked;
            OnLoad();
        }

        private readonly string helpButtonListPrefix = "\n    •  ";
        private void OnBlockProtectedGamesHelpButtonClicked(object sender, EventArgs e)
        {
            string blockedGames = "";
            foreach (string name in Program.ProtectedGameNames)
                blockedGames += helpButtonListPrefix + name;
            string blockedDirectories = "";
            foreach (string path in Program.ProtectedGameDirectories)
                blockedDirectories += helpButtonListPrefix + path;
            string blockedDirectoryExceptions = "";
            foreach (string name in Program.ProtectedGameDirectoryExceptions)
                blockedDirectoryExceptions += helpButtonListPrefix + name;
            new DialogForm(this).Show(blockedGamesCheckBox.Text, SystemIcons.Information,
                "Blocks the program from caching and displaying games protected by DLL checks," +
                "\nanti-cheats, or that are confirmed not to be working with CreamAPI." +
                "\n\nBlocked game names:" + blockedGames +
                "\n\nBlocked game sub-directories:" + blockedDirectories +
                "\n\nBlocked game sub-directory exceptions (not blocked):" + blockedDirectoryExceptions,
                "OK");
        }
    }
}