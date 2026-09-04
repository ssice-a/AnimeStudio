
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using static AnimeStudio.GUI.Studio;
using OpenTK.Graphics;
using OpenTK.Mathematics;
using System.Text.RegularExpressions;
using OpenTK.Audio.OpenAL;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Drawing.Drawing2D;
using static AnimeStudio.AssetsManager;
using Microsoft.VisualBasic.Devices;

namespace AnimeStudio.GUI
{
    partial class MainForm : Form
    {
        private AssetItem lastSelectedItem;
        private AssetBrowser assetBrowser;
        private AboutForm aboutForm;
        private GameSelector gameSelector;
        private UnityCNEdit unityCNEdit;
        private DirectBitmap imageTexture;
        private string tempClipboard;

        private FMOD.System system;
        private FMOD.Sound sound;
        private FMOD.Channel channel;
        private FMOD.SoundGroup masterSoundGroup;
        private FMOD.MODE loopMode = FMOD.MODE.LOOP_OFF;
        private uint FMODlenms;
        private float FMODVolume = 0.8f;

        private bool themeIsFirstLaunch = true;

        #region TexControl
        private static char[] textureChannelNames = new[] { 'B', 'G', 'R', 'A' };
        private bool[] textureChannels = new[] { true, true, true, true };
        #endregion

        #region GLControl
        private bool glControlLoaded;
        private int mdx, mdy;
        private bool lmdown, rmdown;
        private int pgmID, pgmColorID, pgmBlackID;
        private int attributeVertexPosition;
        private int attributeNormalDirection;
        private int attributeVertexColor;
        private int uniformModelMatrix;
        private int uniformViewMatrix;
        private int uniformProjMatrix;
        private int vao;
        private OpenTK.Mathematics.Vector3[] vertexData;
        private OpenTK.Mathematics.Vector3[] normalData;
        private OpenTK.Mathematics.Vector3[] normal2Data;
        private OpenTK.Mathematics.Vector4[] colorData;
        private Matrix4 modelMatrixData;
        private Matrix4 viewMatrixData;
        private Matrix4 projMatrixData;
        private int[] indiceData;
        private int wireFrameMode;
        private int shadeMode;
        private int normalMode;
        #endregion

        //asset list sorting
        private int sortColumn = -1;
        private bool reverseSort;

        //tree search
        private int nextGObject;
        private List<TreeNode> treeSrcResults = new List<TreeNode>();

        private string openDirectoryBackup = string.Empty;
        private string saveDirectoryBackup = string.Empty;

        private GUILogger logger;

        public MainForm()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            InitializeComponent();
            ApplyTheme();
            Text = $"AnimeStudio v{System.Windows.Forms.Application.ProductVersion}";
            InitializeExportOptions();
            InitializeProgressBar();
            InitializeLogger();
            InitalizeOptions();
            FMODinit();
            InitializeEndfieldVirtualPathFilter();
            sceneTreeView.BeforeExpand += sceneTreeView_BeforeExpand;
            sceneTreeView.AfterSelect += sceneTreeView_AfterSelect;
        }

        private ToolStripMenuItem endfieldVirtualPathsToolStripMenuItem;
        private ToolStripMenuItem openEndfieldVfsToolStripMenuItem;
        private VirtualAssetPathIndex endfieldVirtualPathIndex;
        private EndfieldBundleDependencyIndex endfieldDependencyIndex;
        private EndfieldVfsArchive endfieldVfsArchive;
        private readonly List<VirtualAssetRecord> endfieldVirtualAssetRecords = new();
        private List<VirtualAssetRecord> endfieldVisibleAssetRecords = new();
        private readonly Dictionary<string, AssetItem> endfieldLoadedAssetLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AnimeStudio.Object> endfieldLoadedObjectLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim endfieldPreviewLock = new(1, 1);
        private CancellationTokenSource endfieldPreviewCancellation = new();
        private CancellationTokenSource endfieldIndexCancellation = new();
        private int endfieldListOperationGeneration;
        private bool endfieldVirtualAssetListMode;
        private string endfieldVirtualMapPath = string.Empty;
        private string endfieldWorkspace = string.Empty;
        private List<TreeNode> endfieldOriginalSceneNodes;
        private VirtualAssetFile endfieldSelectedVirtualFile;
        private GameObject endfieldSelectedPrefabRoot;

        private void InitializeEndfieldVirtualPathFilter()
        {
            openEndfieldVfsToolStripMenuItem = new ToolStripMenuItem
            {
                Name = "openEndfieldVfsToolStripMenuItem",
                Text = "Open Endfield VFS...",
                ToolTipText = "Index the game VFS and load individual bundles only when an asset is selected"
            };
            openEndfieldVfsToolStripMenuItem.Click += openEndfieldVfsToolStripMenuItem_Click;
            fileToolStripMenuItem.DropDownItems.Insert(2, openEndfieldVfsToolStripMenuItem);

            endfieldVirtualPathsToolStripMenuItem = new ToolStripMenuItem
            {
                Name = "endfieldVirtualPathsToolStripMenuItem",
                Text = "Unified VFS Paths (Endfield)",
                CheckOnClick = true,
                Tag = "virtual-path-filter",
                ToolTipText = "Merge Endfield AssetMap containers into the Scene Hierarchy view"
            };
            endfieldVirtualPathsToolStripMenuItem.Click += endfieldVirtualPathsToolStripMenuItem_Click;
            filterTypeToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator { Tag = "virtual-path-filter" });
            filterTypeToolStripMenuItem.DropDownItems.Add(endfieldVirtualPathsToolStripMenuItem);

            formatSpecificToolStripMenuItem.Enabled = true;
            var eiemMenu = new ToolStripMenuItem("EIEM");
            eiemMenu.DropDownItems.Add("All assets", null,
                (_, _) => ExportAssets(ExportFilter.All, ExportType.Eiem));
            eiemMenu.DropDownItems.Add("Selected assets", null,
                (_, _) => ExportAssets(ExportFilter.Selected, ExportType.Eiem));
            eiemMenu.DropDownItems.Add("Filtered assets", null,
                (_, _) => ExportAssets(ExportFilter.Filtered, ExportType.Eiem));
            eiemMenu.DropDownItems.Add(new ToolStripSeparator());
            eiemMenu.DropDownItems.Add("Checked Prefab structure...", null,
                async (_, _) => await ExportCheckedEndfieldPrefabsAsync(includeResources: false));
            eiemMenu.DropDownItems.Add("Checked Prefab as EIEM mod package...", null,
                async (_, _) => await ExportCheckedEndfieldPrefabsAsync(includeResources: true));
            formatSpecificToolStripMenuItem.DropDownItems.Add(eiemMenu);

            var importJson = new ToolStripMenuItem
            {
                Name = "importEiemJsonToolStripMenuItem",
                Text = "Export EIEM from JSON...",
                ToolTipText = "Use a runtime or EIEM JSON selection file to export matching assets"
            };
            importJson.Click += importEiemJsonToolStripMenuItem_Click;
            fileToolStripMenuItem.DropDownItems.Insert(3, importJson);
        }

        private sealed record EiemJsonSelector(string Name, string Type,
            string Source, string Container, long? PathId);
        private sealed record ResolvedEiemSelection(EiemJsonSelector Selector,
            VirtualAssetRecord Record, AssetItem Asset, int CandidateCount);

        private async void importEiemJsonToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "EIEM or runtime JSON|*.json|All files|*.*",
                Multiselect = false,
                Title = "Select an EIEM/runtime JSON selection file"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            List<EiemJsonSelector> selectors;
            try
            {
                selectors = ReadEiemJsonSelectors(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to read JSON: {ex.Message}", "EIEM JSON",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (selectors.Count == 0)
            {
                MessageBox.Show(this, "The JSON contains no asset selectors.", "EIEM JSON",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var folder = new OpenFolderDialog
            {
                InitialFolder = saveDirectoryBackup,
                Title = "Select EIEM export folder"
            };
            if (folder.ShowDialog(this) != DialogResult.OK)
                return;
            saveDirectoryBackup = folder.Folder;

            timer.Stop();
            StatusStripUpdate($"Resolving {selectors.Count:N0} JSON asset selector(s)...");
            var exported = 0;
            var skipped = 0;
            var resolved = ResolveEiemJsonSelections(selectors);
            var ambiguous = resolved.Count(x => x.CandidateCount > 1);

            // Load each source Bundle once. Runtime scene dumps often contain
            // many renderers from the same character Bundle.
            foreach (var batch in resolved.Where(x => x.Record != null)
                         .GroupBy(x => x.Record.Source, StringComparer.OrdinalIgnoreCase))
            {
                var first = batch.First();
                try
                {
                    if (endfieldVfsArchive == null)
                    {
                        skipped += batch.Count();
                        continue;
                    }
                    await PreviewEndfieldAssetAsync(first.Record);
                    foreach (var selection in batch)
                    {
                        var item = FindLoadedEndfieldAsset(selection.Record);
                        if (item == null || !Exporter.ExportEiemFile(item,
                                BuildEiemJsonExportPath(folder.Folder, selection.Record),
                                selection.Record.Source, selection.Record.Container))
                            skipped++;
                        else
                            exported++;
                    }
                }
                catch (Exception ex)
                {
                    skipped += batch.Count();
                    Logger.Error($"EIEM JSON export failed for {first.Selector.Name}: {ex.Message}");
                }
            }
            foreach (var selection in resolved.Where(x => x.Record == null))
            {
                try
                {
                    if (string.Equals(selection.Selector.Type, "Bundle", StringComparison.OrdinalIgnoreCase))
                    {
                        if (endfieldVfsArchive == null ||
                            !ExportEiemBundle(folder.Folder, selection.Selector.Source))
                            skipped++;
                        else
                            exported++;
                    }
                    else if (selection.Asset == null || !Exporter.ExportEiemFile(selection.Asset, folder.Folder))
                        skipped++;
                    else
                        exported++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    Logger.Error($"EIEM JSON export failed for {selection.Selector.Name}: {ex.Message}");
                }
            }
            StatusStripUpdate($"Finished EIEM JSON export: {exported} exported, {skipped} skipped" +
                (ambiguous > 0 ? $", {ambiguous} ambiguous (not guessed)." : "."));
            if (exported > 0 && Properties.Settings.Default.openAfterExport)
                Studio.OpenFolderInExplorer(folder.Folder);
        }

        private static List<EiemJsonSelector> ReadEiemJsonSelectors(string path)
        {
            var root = JToken.Parse(File.ReadAllText(path));
            var result = new List<EiemJsonSelector>();
            var candidates = root is JArray array ? array : null;
            if (candidates != null)
            {
                foreach (var token in candidates)
                    AddEiemJsonSelectorTree(result, token);
                return DeduplicateEiemJsonSelectors(result);
            }

            foreach (var key in new[] { "meshes", "assets", "records", "selectors" })
            {
                if (root[key] is JArray entries)
                    foreach (var token in entries)
                        AddEiemJsonSelectorTree(result, token);
            }
            // Runtime scene dumps include a diagnostic list of every Bundle
            // observed during the session. It is context for offline lookup,
            // not an instruction to export all of them. A JSON containing
            // only a bundles array is still treated as an explicit Bundle
            // selection file.
            if (result.Count == 0 && root["bundles"] is JArray bundles)
                foreach (var token in bundles)
                    AddEiemJsonSelector(result, token);
            if (result.Count == 0)
                AddEiemJsonSelectorTree(result, root);
            return DeduplicateEiemJsonSelectors(result);
        }

        private static List<EiemJsonSelector> DeduplicateEiemJsonSelectors(
            IEnumerable<EiemJsonSelector> selectors)
        {
            return selectors
                .GroupBy(x => $"{x.Name}|{x.Type}|{x.Source}|{x.Container}|{x.PathId}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
        }

        private static void AddEiemJsonSelectorTree(List<EiemJsonSelector> result, JToken token)
        {
            AddEiemJsonSelector(result, token);
            if (token is not JObject obj)
                return;

            if (obj["materials"] is JArray materials)
            {
                foreach (var material in materials.OfType<JObject>())
                {
                    AddEiemJsonSelector(result, material, "Material");
                    if (material["textures"] is JArray textures)
                        foreach (var texture in textures.OfType<JObject>())
                            AddEiemJsonSelector(result, texture, "Texture");
                }
            }
        }

        private static void AddEiemJsonSelector(List<EiemJsonSelector> result, JToken token,
            string defaultType = null)
        {
            if (token is not JObject obj)
                return;
            var name = (string)obj["name"] ?? (string)obj["asset"] ??
                       (string)obj["lookupName"] ?? string.Empty;
            var meshText = (string)obj["mesh"] ?? string.Empty;
            var bundlePath = (string)obj["path"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(bundlePath) &&
                bundlePath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new EiemJsonSelector(Path.GetFileName(bundlePath), "Bundle",
                    bundlePath.Replace('\\', '/'), string.Empty, null));
                return;
            }
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(meshText))
            {
                var match = Regex.Match(meshText, "name=\"(?<name>[^\"]+)\"",
                    RegexOptions.IgnoreCase);
                name = match.Success ? match.Groups["name"].Value : meshText;
            }
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (name.StartsWith("<", StringComparison.Ordinal) &&
                name.EndsWith(">", StringComparison.Ordinal))
                return;
            var type = (string)obj["type"] ?? (string)obj["lookupType"];
            if (string.IsNullOrWhiteSpace(type))
                type = defaultType ?? (!string.IsNullOrWhiteSpace(meshText)
                    ? "Mesh" : ((string)obj["rendererType"] ?? "Mesh"));
            var source = (string)obj["sourcePath"] ?? (string)obj["bundle"] ??
                         (string)obj["source"] ?? bundlePath ?? string.Empty;
            if (string.Equals(source, "runtime-observation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "offline-index-required", StringComparison.OrdinalIgnoreCase))
                source = string.Empty;
            var container = (string)obj["container"] ?? (string)obj["logicalPath"] ??
                            string.Empty;
            long? pathId = null;
            var pathToken = obj["pathId"] ?? obj["PathID"] ?? obj["nativePathId"];
            if (pathToken != null && long.TryParse(pathToken.ToString(), out var parsed))
                pathId = parsed;
            result.Add(new EiemJsonSelector(name, type, source, container, pathId));
        }

        private List<ResolvedEiemSelection> ResolveEiemJsonSelections(
            IReadOnlyList<EiemJsonSelector> selectors)
        {
            var records = new VirtualAssetRecord[selectors.Count];
            var candidateCounts = new int[selectors.Count];
            if (endfieldVirtualAssetRecords.Count > 0)
            {
                var pendingByName = selectors
                    .Select((selector, index) => (selector, index))
                    .Where(x => !string.Equals(x.selector.Type, "Bundle",
                        StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.selector.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.ToArray(),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var record in endfieldVirtualAssetRecords)
                {
                    if (!pendingByName.TryGetValue(record.Name, out var candidates))
                        continue;
                    foreach (var candidate in candidates)
                    {
                        if (!EiemRecordMatchesSelector(candidate.selector, record))
                            continue;
                        candidateCounts[candidate.index]++;
                        if (records[candidate.index] == null)
                            records[candidate.index] = record;
                    }
                }
            }

            for (var index = 0; index < records.Length; index++)
            {
                if (candidateCounts[index] <= 1)
                    continue;
                Logger.Warning($"EIEM selector is ambiguous and will not be guessed: " +
                    $"{selectors[index].Type}:{selectors[index].Name} " +
                    $"({candidateCounts[index]} index matches). Runtime path/container is required.");
                records[index] = null;
            }
            return selectors.Select((selector, index) => new ResolvedEiemSelection(
                selector, records[index], candidateCounts[index] > 1
                    ? null : FindNormalJsonAsset(selector),
                candidateCounts[index])).ToList();
        }

        private static bool EiemRecordMatchesSelector(EiemJsonSelector selector,
            VirtualAssetRecord record)
        {
            if (!EiemAssetTypeMatches(selector.Type, record.Type))
                return false;
            if (selector.PathId.HasValue && selector.PathId.Value != record.PathId)
                return false;
            if (!string.IsNullOrWhiteSpace(selector.Container) &&
                !EiemPathMatches(selector.Container, record.Container))
                return false;
            if (string.IsNullOrWhiteSpace(selector.Source))
                return true;
            var source = selector.Source.Replace('\\', '/');
            return record.Source.Contains(source, StringComparison.OrdinalIgnoreCase) ||
                   source.Contains(record.Source, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EiemPathMatches(string requested, string actual)
        {
            var left = (requested ?? string.Empty).Replace('\\', '/').Trim('/');
            var right = (actual ?? string.Empty).Replace('\\', '/').Trim('/');
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                   left.EndsWith("/" + right, StringComparison.OrdinalIgnoreCase) ||
                   right.EndsWith("/" + left, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EiemAssetTypeMatches(string requested, string actual)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return true;
            if (string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(requested, "Texture", StringComparison.OrdinalIgnoreCase) &&
                   (actual?.StartsWith("Texture", StringComparison.OrdinalIgnoreCase) == true ||
                    string.Equals(actual, "Cubemap", StringComparison.OrdinalIgnoreCase));
        }

        private static AssetItem FindNormalJsonAsset(EiemJsonSelector selector)
        {
            var type = selector.Type ?? string.Empty;
            if (string.Equals(type, "Bundle", StringComparison.OrdinalIgnoreCase))
                return null;
            return Studio.exportableAssets.FirstOrDefault(x =>
                string.Equals(x.Text, selector.Name, StringComparison.OrdinalIgnoreCase) &&
                EiemAssetTypeMatches(type, x.TypeString));
        }

        private static string BuildEiemJsonExportPath(string root, VirtualAssetRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Container))
                return root;
            var parts = record.Container.Replace('\\', '/').Split('/',
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
                return root;
            var relative = parts.Take(parts.Length - 1)
                .Select(Exporter.FixFileName)
                .ToArray();
            return Path.Combine(new[] { root }.Concat(relative).ToArray());
        }

        private bool ExportEiemBundle(string outputRoot, string logicalPath)
        {
            var normalized = EndfieldVfsArchive.NormalizeLogicalPath(logicalPath);
            if (string.IsNullOrWhiteSpace(normalized) || endfieldVfsArchive == null ||
                !endfieldVfsArchive.TryGet(normalized, out var entry))
                return false;

            var output = Path.Combine(outputRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            if (File.Exists(output) && new FileInfo(output).Length == entry.Length)
                return true;

            var temp = output + ".tmp";
            File.WriteAllBytes(temp, endfieldVfsArchive.ReadPayload(normalized));
            File.Move(temp, output, true);
            return true;
        }

        private async void openEndfieldVfsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var vfsDialog = new OpenFolderDialog
            {
                Title = "Select Endfield VFS folder (the folder containing .blc and .chk files)",
                InitialFolder = FindDefaultEndfieldVfsRoot()
            };
            if (vfsDialog.ShowDialog(this) != DialogResult.OK)
                return;

            var vfsRoot = ResolveEndfieldVfsRoot(vfsDialog.Folder);
            if (vfsRoot == null)
            {
                MessageBox.Show(this, "The selected folder does not contain Endfield .blc/.chk files.",
                    "Invalid Endfield VFS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var workspaceDialog = new OpenFolderDialog
            {
                Title = "Select workspace for the small on-demand cache (do not select drive C)",
                InitialFolder = string.IsNullOrWhiteSpace(endfieldWorkspace) ? Path.GetPathRoot(Environment.CurrentDirectory) : endfieldWorkspace
            };
            if (workspaceDialog.ShowDialog(this) != DialogResult.OK)
                return;
            var workspace = Path.GetFullPath(workspaceDialog.Folder);
            if (string.Equals(Path.GetPathRoot(workspace), @"C:\", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Choose a workspace outside drive C. VFS indexes and preview caches will not be written to C.",
                    "Workspace required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                openEndfieldVfsToolStripMenuItem.Enabled = false;
                StatusStripUpdate("Reading Endfield VFS metadata...");
                var archive = await Task.Run(() => EndfieldVfsArchive.Open(vfsRoot));
                var game = GameManager.GetGameByType(GameType.ArknightsEndfield);

                string mapPath;
                if (!EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out mapPath))
                {
                    endfieldIndexCancellation.Cancel();
                    endfieldIndexCancellation.Dispose();
                    endfieldIndexCancellation = new CancellationTokenSource();
                    var progress = new Progress<EndfieldIndexProgress>(UpdateEndfieldIndexProgress);
                    StatusStripUpdate("No current resource index. Building directly from VFS; File > Abort pauses safely.");
                    mapPath = await EndfieldVfsAssetIndexBuilder.BuildAsync(
                        archive, workspace, game, progress, endfieldIndexCancellation.Token);
                }

                StatusStripUpdate("Loading compact Endfield resource paths and dependencies...");
                var loadedIndex = await Task.Run(() => EndfieldIndexStore.Load(mapPath));
                if (!string.Equals(loadedIndex.VfsFingerprint, archive.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("The Endfield index belongs to a different VFS version.");
                var assetIndex = loadedIndex.Assets;
                var dependencyIndex = loadedIndex.Dependencies;

                ResetForm();
                endfieldVfsArchive = archive;
                endfieldDependencyIndex = dependencyIndex;
                endfieldWorkspace = workspace;
                ActivateEndfieldVirtualIndex(assetIndex, mapPath);
                Studio.Game = game;
                assetsManager.Game = Studio.Game;
                assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
                Text = $"AnimeStudio v{System.Windows.Forms.Application.ProductVersion} - Endfield VFS";
                StatusStripUpdate($"Ready: {endfieldVfsArchive.Entries.Count:N0} VFS files, {endfieldVirtualPathIndex.AssetCount:N0} assets, {endfieldDependencyIndex.CabCount:N0} CABs. Select an asset to preview it.");
            }
            catch (OperationCanceledException)
            {
                StatusStripUpdate("Endfield index build paused. Open the same VFS/workspace to resume from the last checkpoint.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to open Endfield VFS: {ex}");
                MessageBox.Show(this, ex.Message, "Endfield VFS error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                StatusStripUpdate("Failed to open Endfield VFS.");
            }
            finally
            {
                openEndfieldVfsToolStripMenuItem.Enabled = true;
            }
        }

        private void UpdateEndfieldIndexProgress(EndfieldIndexProgress value)
        {
            var percent = value.TotalBundles == 0 ? 0 : value.ProcessedBundles * 100 / value.TotalBundles;
            SetProgressBarValue(percent);
            var rate = value.Elapsed.TotalSeconds <= 0 ? 0 : value.ProcessedBundles / value.Elapsed.TotalSeconds;
            var remaining = rate <= 0 ? TimeSpan.Zero :
                TimeSpan.FromSeconds((value.TotalBundles - value.ProcessedBundles) / rate);
            StatusStripUpdate(
                $"Indexing VFS {value.ProcessedBundles:N0}/{value.TotalBundles:N0} ({percent}%) | " +
                $"assets {value.AssetCount:N0} | CABs {value.CabCount:N0} | failed {value.FailedBundles:N0} | ETA {remaining:hh\\:mm\\:ss}");
        }

        private static string FindDefaultEndfieldVfsRoot()
        {
            var known = @"D:\Hypergryph Launcher\games\Endfield Game\Endfield_Data\StreamingAssets\VFS";
            return Directory.Exists(known) ? known : Environment.CurrentDirectory;
        }

        private static string ResolveEndfieldVfsRoot(string selectedPath)
        {
            var candidates = new[]
            {
                selectedPath,
                Path.Combine(selectedPath, "Endfield_Data", "StreamingAssets", "VFS"),
                Path.Combine(selectedPath, "StreamingAssets", "VFS")
            };
            return candidates.FirstOrDefault(path => Directory.Exists(path) &&
                Directory.EnumerateFiles(path, "*.blc", SearchOption.AllDirectories).Any() &&
                Directory.EnumerateFiles(path, "*.chk", SearchOption.AllDirectories).Any());
        }

        private void endfieldVirtualPathsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!endfieldVirtualPathsToolStripMenuItem.Checked)
            {
                Interlocked.Increment(ref endfieldListOperationGeneration);
                sceneTreeView.Nodes.Clear();
                endfieldVirtualAssetListMode = false;
                endfieldVisibleAssetRecords.Clear();
                assetListView.VirtualListSize = visibleAssets.Count;
                assetListView.Refresh();
                if (endfieldOriginalSceneNodes != null)
                {
                    sceneTreeView.Nodes.AddRange(endfieldOriginalSceneNodes.ToArray());
                    endfieldOriginalSceneNodes = null;
                }
                StatusStripUpdate("Virtual Asset Paths disabled.");
                return;
            }

            if (endfieldVirtualPathIndex != null)
            {
                endfieldVirtualAssetListMode = true;
                endfieldVisibleAssetRecords = endfieldVirtualAssetRecords.ToList();
                assetListView.VirtualListSize = endfieldVisibleAssetRecords.Count;
                assetListView.Refresh();
                BuildVirtualAssetTree(endfieldVirtualPathIndex);
                StatusStripUpdate($"Loaded {endfieldVirtualPathIndex.AssetCount:N0} assets into {endfieldVirtualPathIndex.DirectoryCount:N0} virtual paths.");
                return;
            }

            endfieldVirtualPathsToolStripMenuItem.Checked = false;
            MessageBox.Show(this, "Use File > Open Endfield VFS first.", "Endfield VFS",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ActivateEndfieldVirtualIndex(VirtualAssetPathIndex index, string mapPath, bool activate = true)
        {
            Interlocked.Increment(ref endfieldListOperationGeneration);
            endfieldVirtualPathIndex = index;
            endfieldVirtualMapPath = mapPath;
            endfieldVirtualAssetRecords.Clear();
            endfieldVirtualAssetRecords.AddRange(index.Records);
            endfieldVisibleAssetRecords = endfieldVirtualAssetRecords.ToList();
            BuildEndfieldLoadedAssetLookup();
            RebuildEndfieldTypeFilters();
            endfieldVirtualAssetListMode = true;
            assetListView.VirtualListSize = endfieldVisibleAssetRecords.Count;
            assetListView.Refresh();
            if (activate)
            {
                endfieldVirtualPathsToolStripMenuItem.Checked = true;
                BuildVirtualAssetTree(index);
            }
            StatusStripUpdate($"Loaded {index.AssetCount:N0} assets into {index.DirectoryCount:N0} virtual paths.");
        }

        private void RebuildEndfieldTypeFilters()
        {
            for (var i = filterTypeToolStripMenuItem.DropDownItems.Count - 1; i >= 1; i--)
            {
                var item = filterTypeToolStripMenuItem.DropDownItems[i];
                if (item.Tag as string == "endfield-asset-type")
                    filterTypeToolStripMenuItem.DropDownItems.RemoveAt(i);
            }

            foreach (var type in endfieldVirtualAssetRecords.Select(x => x.Type)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ToolStripMenuItem
                {
                    CheckOnClick = true,
                    Name = $"endfieldType_{type}",
                    Text = type,
                    Tag = "endfield-asset-type"
                };
                item.Click += typeToolStripMenuItem_Click;
                filterTypeToolStripMenuItem.DropDownItems.Insert(
                    filterTypeToolStripMenuItem.DropDownItems.IndexOf(endfieldVirtualPathsToolStripMenuItem), item);
            }
            allToolStripMenuItem.Checked = true;
        }

        private void BuildVirtualAssetTree(VirtualAssetPathIndex index)
        {
            sceneTreeView.BeginUpdate();
            sceneTreeView.Nodes.Clear();
            var root = new TreeNode("Assets") { Tag = index.Root };
            AddVirtualAssetTreeChildren(root, index.Root);
            sceneTreeView.Nodes.Add(root);
            root.Expand();
            sceneTreeView.EndUpdate();
        }

        private void sceneTreeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (endfieldVirtualPathsToolStripMenuItem?.Checked != true ||
                e.Node.Tag is not VirtualAssetPathNode source ||
                e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag != null)
                return;

            e.Node.Nodes.Clear();
            AddVirtualAssetTreeChildren(e.Node, source);
        }

        private void AddVirtualAssetTreeChildren(TreeNode parent, VirtualAssetPathNode source)
        {
            foreach (var child in source.Children.Values)
            {
                var isFile = child.Children.Count == 0 && child.Assets.Count > 0 && !IsLargeUnscopedNode(child);
                // A file is a leaf. Keep the logical file distinct from the
                // serialized objects it owns; Prefab selection must resolve
                // its root GameObject instead of binding to an arbitrary bone.
                var node = isFile
                    ? new TreeNode(child.Name)
                    {
                        Tag = new VirtualAssetFile(child.Assets[0].Container, child.Assets.ToArray())
                    }
                    : new TreeNode(child.Name) { Tag = child };
                if (child.Children.Count > 0 || (!isFile && child.Assets.Count > 0 && !IsLargeUnscopedNode(child)))
                    node.Nodes.Add(new TreeNode { Tag = null });
                parent.Nodes.Add(node);
            }

            const int maxTreeAssets = 200;
            var visibleAssets = source.Name.Equals("[no container]", StringComparison.OrdinalIgnoreCase)
                ? 0
                : Math.Min(source.Assets.Count, maxTreeAssets);
            for (var i = 0; i < visibleAssets; i++)
            {
                var asset = source.Assets[i];
                var name = Path.GetFileName(asset.Container.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(name))
                    name = string.IsNullOrWhiteSpace(asset.Name) ? "[unnamed]" : asset.Name;
                if (!string.IsNullOrWhiteSpace(asset.Type))
                    name += $" [{asset.Type}]";
                parent.Nodes.Add(new TreeNode(name) { Tag = asset });
            }
            if (source.Assets.Count > visibleAssets)
                parent.Nodes.Add(new TreeNode($"... {source.Assets.Count - visibleAssets:N0} more assets; use Asset List") { Tag = null });
        }

        private static VirtualAssetRecord ChooseVirtualFilePreviewAsset(VirtualAssetPathNode file)
            => ChooseVirtualFilePreviewAsset(file.Assets);

        private static VirtualAssetRecord ChooseVirtualFilePreviewAsset(
            IReadOnlyList<VirtualAssetRecord> records)
        {
            // Prefer the object that gives the native preview the most useful
            // entry point for a file, without changing the complete object
            // records retained by Asset List.
            var priorities = new[]
            {
                "GameObject", "Mesh", "Texture2D", "Texture", "Material",
                "Sprite", "AnimationClip", "Animator", "TextAsset"
            };
            foreach (var type in priorities)
            {
                var match = records.FirstOrDefault(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
            return records.Count > 0 ? records[0] : null;
        }

        private static bool IsLargeUnscopedNode(VirtualAssetPathNode node)
        {
            return node.Name.Equals("[no container]", StringComparison.OrdinalIgnoreCase) && node.Assets.Count > 0;
        }

        private void ApplyTheme()
        {
#if NET9_0_OR_GREATER
#pragma warning disable WFO5001
            var currentTheme = Properties.Settings.Default.guiTheme;

            try
            {
                switch (currentTheme)
                {
                    case (int)GuiColorTheme.System:
                        if (IsSystemInDarkMode())
                        {
                            goto case (int)GuiColorTheme.Dark;
                        }
                        goto case (int)GuiColorTheme.Light;
                    case (int)GuiColorTheme.Dark:
                        System.Windows.Forms.Application.SetColorMode(SystemColorMode.Dark);
                        assetListView.GridLines = false;
                        assetInfoLabel.ForeColor = System.Drawing.SystemColors.ControlLightLight;
                        break;
                    case (int)GuiColorTheme.Light:
                        System.Windows.Forms.Application.SetColorMode(SystemColorMode.Classic);
                        assetListView.GridLines = true;
                        assetInfoLabel.ForeColor = System.Drawing.SystemColors.ControlText;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply theme : {ex}");
            }
#pragma warning restore WFO5001
#endif
        }

        private static bool IsSystemInDarkMode()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not read the system app theme, assuming light : {ex.Message}");
                return false;
            }
        }

        private void specifyTheme_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (themeIsFirstLaunch)
            {
                themeIsFirstLaunch = false;
                return;
            }

            if (Environment.Version.Major < 9)
            {
                MessageBox.Show(".NET 9 or higher is required for changing the app theme.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedTheme = specifyTheme.SelectedIndex;
            Properties.Settings.Default.guiTheme = selectedTheme;
            Properties.Settings.Default.Save();
            Logger.Info("Updated app theme !");

            var text = "Please keep in mind dark mode is still in beta, a better support is planned in future versions of .NET. The application needs to restart in order to apply the theme.";

            if (selectedTheme == 2)
            {
                text = "The application needs to restart in order to apply the theme.";
            }

            MessageBox.Show(text, "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);

            ApplyTheme();

        }

        private void InitializeExportOptions()
        {
            enableConsole.Checked = Properties.Settings.Default.enableConsole;
            enableFileLogging.Checked = Properties.Settings.Default.enableFileLogging;
            displayAll.Checked = Properties.Settings.Default.displayAll;
            displayInfo.Checked = Properties.Settings.Default.displayInfo;
            enablePreview.Checked = Properties.Settings.Default.enablePreview;
            enableModelPreview.Checked = Properties.Settings.Default.enableModelPreview;
            modelsOnly.Checked = Properties.Settings.Default.modelsOnly;
            enableResolveDependencies.Checked = Properties.Settings.Default.enableResolveDependencies;
            allowDuplicates.Checked = Properties.Settings.Default.allowDuplicates;
            useBundleContainerNameToolStripMenuItem.Checked = Properties.Settings.Default.useBundleContainerName;
            skipContainer.Checked = Properties.Settings.Default.skipContainer;
            assetsManager.ResolveDependencies = enableResolveDependencies.Checked;
            SkipContainer = Properties.Settings.Default.skipContainer;
            MiHoYoBinData.Encrypted = Properties.Settings.Default.encrypted;
            MiHoYoBinData.Key = Properties.Settings.Default.key;
            AssetsHelper.Minimal = Properties.Settings.Default.minimalAssetMap;
        }

        private void InitializeLogger()
        {
            logger = new GUILogger(StatusStripUpdate);
            ConsoleHelper.AllocConsole();
            ConsoleHelper.SetConsoleTitle("Debug Console");
            var handle = ConsoleHelper.GetConsoleWindow();
            if (enableConsole.Checked)
            {
                Logger.Default = new ConsoleLogger();
                ConsoleHelper.ShowWindow(handle, ConsoleHelper.SW_SHOW);
            }
            else
            {
                Logger.Default = logger;
                ConsoleHelper.ShowWindow(handle, ConsoleHelper.SW_HIDE);
            }
            var loggerEventType = (LoggerEvent)Properties.Settings.Default.loggerEventType;
            var loggerEventTypes = Enum.GetValues<LoggerEvent>().ToArray()[1..^1];
            foreach (var loggerEvent in loggerEventTypes)
            {
                var menuItem = new ToolStripMenuItem(loggerEvent.ToString())
                {
                    CheckOnClick = true,
                    Checked = loggerEventType.HasFlag(loggerEvent),
                    Tag = loggerEvent
                };

                menuItem.Click += LoggerEventMenuItem_Click;

                loggedEventsMenuItem.DropDownItems.Add(menuItem);
            }
            Logger.Flags = loggerEventType;
            Logger.FileLogging = enableFileLogging.Checked;
        }

        private void InitializeProgressBar()
        {
            Progress.Default = new Progress<int>(SetProgressBarValue);
            Studio.StatusStripUpdate = StatusStripUpdate;
        }

        private void InitalizeOptions()
        {
            var assetMapType = (ExportListType)Properties.Settings.Default.assetMapType;
            var assetMapTypes = Enum.GetValues<ExportListType>().ToArray()[1..];
            foreach (var mapType in assetMapTypes)
            {
                var menuItem = new ToolStripMenuItem(mapType.ToString()) { CheckOnClick = true, Checked = assetMapType.HasFlag(mapType), Tag = (int)mapType };
                assetMapTypeMenuItem.DropDownItems.Add(menuItem);
            }

            specifyTheme.SelectedIndex = Properties.Settings.Default.guiTheme;

            try
            {
                Studio.Game = GameManager.GetGame(Properties.Settings.Default.selectedGame);
            }
            catch
            {
                Logger.Info($"Invalid game index in settings, resetting to default");
                Properties.Settings.Default.selectedGame = 0;
                Properties.Settings.Default.Save();
                Studio.Game = GameManager.GetGame(Properties.Settings.Default.selectedGame);
            }

            try
            {
                TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Properties.Settings.Default.types));
            } catch (Newtonsoft.Json.JsonSerializationException)
            {
                // Fixes an issue where the application won't load if invalid settings from another version of Studio were previously saved.
                Properties.Settings.Default.Reset();
                TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Properties.Settings.Default.types));
            }
            
            Logger.Info($"Target Game is {Studio.Game.Type}");

            if (Studio.Game.IsUnityCN())
            {
                if (Studio.Game is UnityCNGame unityCNGameKey)
                {
                    if (Studio.Game.Type == GameType.UnityCNCustomKey)
                    {
                        UnityCNManager.SetKey(new("UnityCN Custom Key", Properties.Settings.Default.lastUnityCNKey));
                    }
                    else
                    {
                        UnityCNManager.SetKey(unityCNGameKey.Key);
                    }
                }
            }

            // See https://github.com/Eleiyas/Z3-Asset-Map 
            var paths = File.Exists("./Maps/Z3-AssetIndex-Eleiyas.json")
                ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText("./Maps/Z3-AssetIndex-Eleiyas.json"))
                : new Dictionary<ulong, string>();

            Studio.Paths = paths;
            AssetsHelper.Paths = paths;

            MapNameComboBox.SelectedIndexChanged += new EventHandler(specifyNameComboBox_SelectedIndexChanged);
            if (!string.IsNullOrEmpty(Properties.Settings.Default.selectedCABMapName))
            {
                if (!AssetsHelper.LoadCABMapInternal(Properties.Settings.Default.selectedCABMapName))
                {
                    Properties.Settings.Default.selectedCABMapName = "";
                    Properties.Settings.Default.Save();
                }
                else
                {
                    MapNameComboBox.Text = Properties.Settings.Default.selectedCABMapName;
                }
            }
        }
        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private async void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths.Length > 0)
            {
                LoadPaths(null, paths);
            }
        }

        string FormatBytes(double bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i;
            for (i = 0; i < suffixes.Length && bytes >= 1024; i++)
            {
                bytes /= 1024;
            }
            return $"{bytes:0.##} {suffixes[i]}";
        }

        long GetTotalSize(string[] paths)
        {
            long total = 0;
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    total += new FileInfo(path).Length;
                }
                else if (Directory.Exists(path))
                {
                    var dirInfo = new DirectoryInfo(path);
                    total += dirInfo.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                }
            }
            return total;
        }

        long GetFolderSize(string path)
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                            .Sum(file => new FileInfo(file).Length);
        }

        private bool SizeWarning(long totalSize)
        {
            long estimatedUsedRam = (long)(totalSize * 8.5); // number deduced by loading different sets of assets and averaging the sizes
            ulong ramLeft = new ComputerInfo().AvailablePhysicalMemory;
            if (estimatedUsedRam > (long)ramLeft)
            {
                var result = MessageBox.Show($"You are trying to load {FormatBytes(totalSize)} of data, which will make the tool use approximately {FormatBytes(estimatedUsedRam)} of ram, but you have {FormatBytes(ramLeft)} left, continue ?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                return result == DialogResult.Yes;
            }
            return true;
        }

        public async void LoadPaths(List<AssetFilterDataItem> filterData, params string[] paths)
        {
            long totalSize = GetTotalSize(paths);
            if (!SizeWarning(totalSize)) return;

            ResetForm();
            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            assetsManager.Game = Studio.Game;
            if (filterData != null)
            {
                assetsManager.FilterData = new AssetFilterData { Items = filterData };
            }
            if (paths.Length == 1 && Directory.Exists(paths[0]))
            {
                await Task.Run(() => assetsManager.LoadFolder(paths[0]));
            }
            else
            {
                await Task.Run(() => assetsManager.LoadFiles(paths));
            }
            BuildAssetStructures();
        }

        private async void loadFile_Click(object sender, EventArgs e)
        {
            openFileDialog1.InitialDirectory = openDirectoryBackup;
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                var paths = openFileDialog1.FileNames;
                ResetForm();
                openDirectoryBackup = Path.GetDirectoryName(paths[0]);
                assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
                assetsManager.Game = Studio.Game;
                if (paths.Length == 1 && File.Exists(paths[0]) && Path.GetExtension(paths[0]) == ".txt")
                {
                    paths = File.ReadAllLines(paths[0]);
                }
                await Task.Run(() => assetsManager.LoadFiles(paths));
                BuildAssetStructures();
            }
        }

        private async void loadFolder_Click(object sender, EventArgs e)
        {
            var openFolderDialog = new OpenFolderDialog();
            openFolderDialog.InitialFolder = openDirectoryBackup;
            if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                openDirectoryBackup = openFolderDialog.Folder;

                long totalSize = GetFolderSize(openDirectoryBackup);
                if (!SizeWarning(totalSize)) return;

                ResetForm();
                assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
                assetsManager.Game = Studio.Game;
                await Task.Run(() => assetsManager.LoadFolder(openFolderDialog.Folder));
                BuildAssetStructures();
            }
        }

        private async void extractFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.Title = "Select the save folder";
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    var fileNames = openFileDialog1.FileNames;
                    var savePath = saveFolderDialog.Folder;
                    var extractedCount = await Task.Run(() => ExtractFile(fileNames, savePath));
                    StatusStripUpdate($"Finished extracting {extractedCount} files.");
                }
            }
        }

        private async void extractFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var openFolderDialog = new OpenFolderDialog();
            if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.Title = "Select the save folder";
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    var path = openFolderDialog.Folder;
                    var savePath = saveFolderDialog.Folder;
                    var extractedCount = await Task.Run(() => ExtractFolder(path, savePath));
                    StatusStripUpdate($"Finished extracting {extractedCount} files.");
                }
            }
        }

        private async void BuildAssetStructures()
        {
            if (assetsManager.assetsFileList.Count == 0)
            {
                StatusStripUpdate("No Unity file can be loaded.");
                return;
            }

            (var productName, var treeNodeCollection) = await Task.Run(BuildAssetData);
            var typeMap = await Task.Run(BuildClassStructure);

            if (string.IsNullOrEmpty(productName))
            {
                if (!Studio.Game.Type.IsNormal())
                {
                    productName = Studio.Game.Name;
                }
                else if (Studio.Game.IsUnityCN())
                {
                    if (Studio.Game is UnityCNGame unityCN)
                    {
                        productName = unityCN.Name;
                    }
                }
                else
                {
                    productName = "no productName";
                }
            }

            Text = $"AnimeStudio v{System.Windows.Forms.Application.ProductVersion} - {productName} - {assetsManager.assetsFileList[0].unityVersion} - {assetsManager.assetsFileList[0].m_TargetPlatform}";

            if (endfieldVirtualPathIndex != null)
                BuildEndfieldLoadedAssetLookup();
            assetListView.VirtualListSize = visibleAssets.Count;

            sceneTreeView.BeginUpdate();
            sceneTreeView.Nodes.AddRange(treeNodeCollection.ToArray());
            sceneTreeView.EndUpdate();
            treeNodeCollection.Clear();

            classesListView.BeginUpdate();
            foreach (var version in typeMap)
            {
                var versionGroup = new ListViewGroup(version.Key);
                classesListView.Groups.Add(versionGroup);

                foreach (var uclass in version.Value)
                {
                    uclass.Value.Group = versionGroup;
                    classesListView.Items.Add(uclass.Value);
                }
            }
            typeMap.Clear();
            classesListView.EndUpdate();

            var types = exportableAssets.Select(x => x.Type).Distinct().OrderBy(x => x.ToString()).ToArray();
            foreach (var type in types)
            {
                var typeItem = new ToolStripMenuItem
                {
                    CheckOnClick = true,
                    Name = type.ToString(),
                    Size = new Size(180, 22),
                    Text = type.ToString()
                };
                typeItem.Click += typeToolStripMenuItem_Click;
                filterTypeToolStripMenuItem.DropDownItems.Add(typeItem);
            }
            allToolStripMenuItem.Checked = true;
            var log = $"Finished loading {assetsManager.assetsFileList.Count} files with {assetListView.Items.Count} exportable assets";
            var m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.Count);
            var objectsCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
            if (m_ObjectsCount != objectsCount)
            {
                log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
            }
            StatusStripUpdate(log);
        }

        private void typeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var typeItem = (ToolStripMenuItem)sender;
            if (typeItem != allToolStripMenuItem)
            {
                allToolStripMenuItem.Checked = false;
            }
            else if (allToolStripMenuItem.Checked)
            {
                for (var i = 1; i < filterTypeToolStripMenuItem.DropDownItems.Count; i++)
                {
                    if (filterTypeToolStripMenuItem.DropDownItems[i] is ToolStripMenuItem item &&
                        item != endfieldVirtualPathsToolStripMenuItem &&
                        item.Tag as string != "virtual-path-filter")
                        item.Checked = false;
                }
            }
            FilterAssetList();
        }

        private void AnimeStudioForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (glControl.Visible)
            {
                if (e.Control)
                {
                    switch (e.KeyCode)
                    {
                        case Keys.W:
                            //Toggle WireFrame
                            wireFrameMode = (wireFrameMode + 1) % 3;
                            glControl.Invalidate();
                            break;
                        case Keys.S:
                            //Toggle Shade
                            shadeMode = (shadeMode + 1) % 2;
                            glControl.Invalidate();
                            break;
                        case Keys.N:
                            //Normal mode
                            normalMode = (normalMode + 1) % 2;
                            CreateVAO();
                            glControl.Invalidate();
                            break;
                    }
                }
            }
            else if (previewPanel.Visible)
            {
                if (e.Control)
                {
                    var need = false;
                    switch (e.KeyCode)
                    {
                        case Keys.B:
                            textureChannels[0] = !textureChannels[0];
                            need = true;
                            break;
                        case Keys.G:
                            textureChannels[1] = !textureChannels[1];
                            need = true;
                            break;
                        case Keys.R:
                            textureChannels[2] = !textureChannels[2];
                            need = true;
                            break;
                        case Keys.A:
                            textureChannels[3] = !textureChannels[3];
                            need = true;
                            break;
                    }
                    if (need)
                    {
                        if (lastSelectedItem != null)
                        {
                            PreviewAsset(lastSelectedItem);
                            assetInfoLabel.Text = lastSelectedItem.InfoText;
                        }
                    }
                }
            }
        }

        private void exportClassStructuresMenuItem_Click(object sender, EventArgs e)
        {
            if (classesListView.Items.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    var savePath = saveFolderDialog.Folder;
                    var count = classesListView.Items.Count;
                    int i = 0;
                    Progress.Reset();
                    foreach (TypeTreeItem item in classesListView.Items)
                    {
                        var versionPath = Path.Combine(savePath, item.Group.Header);
                        Directory.CreateDirectory(versionPath);

                        var saveFile = $"{versionPath}{Path.DirectorySeparatorChar}{item.SubItems[1].Text} {item.Text}.txt";
                        File.WriteAllText(saveFile, item.ToString());

                        Progress.Report(++i, count);
                    }

                    StatusStripUpdate("Finished exporting class structures");
                }
            }
        }

        private void displayAll_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.displayAll = displayAll.Checked;
            Properties.Settings.Default.Save();
        }

        private void enablePreview_Check(object sender, EventArgs e)
        {
            if (lastSelectedItem != null)
            {
                switch (lastSelectedItem.Type)
                {
                    case ClassIDType.Texture2D:
                    case ClassIDType.Sprite:
                        {
                            if (enablePreview.Checked && imageTexture != null)
                            {
                                previewPanel.BackgroundImage = imageTexture.Bitmap;
                            }
                            else
                            {
                                previewPanel.BackgroundImage = Properties.Resources.preview;
                                previewPanel.BackgroundImageLayout = ImageLayout.Center;
                            }
                        }
                        break;
                    case ClassIDType.Shader:
                    case ClassIDType.TextAsset:
                    case ClassIDType.MonoBehaviour:
                    case ClassIDType.MiHoYoBinData:
                        textPreviewBox.Visible = !textPreviewBox.Visible;
                        break;
                    case ClassIDType.Font:
                        fontPreviewBox.Visible = !fontPreviewBox.Visible;
                        break;
                    case ClassIDType.AudioClip:
                        {
                            FMODpanel.Visible = !FMODpanel.Visible;

                            if (sound != null && channel != null)
                            {
                                var result = channel.isPlaying(out var playing);
                                if (result == FMOD.RESULT.OK && playing)
                                {
                                    channel.stop();
                                    FMODreset();
                                }
                            }
                            else if (FMODpanel.Visible)
                            {
                                PreviewAsset(lastSelectedItem);
                            }

                            break;
                        }

                }

            }
            else if (lastSelectedItem != null && enablePreview.Checked)
            {
                PreviewAsset(lastSelectedItem);
            }

            Properties.Settings.Default.enablePreview = enablePreview.Checked;
            Properties.Settings.Default.Save();
        }
        private void displayAssetInfo_Check(object sender, EventArgs e)
        {
            if (displayInfo.Checked && assetInfoLabel.Text != null)
            {
                assetInfoLabel.Visible = true;
            }
            else
            {
                assetInfoLabel.Visible = false;
            }

            Properties.Settings.Default.displayInfo = displayInfo.Checked;
            Properties.Settings.Default.Save();
        }

        private void showExpOpt_Click(object sender, EventArgs e)
        {
            var exportOpt = new ExportOptions();
            if (exportOpt.ShowDialog(this) == DialogResult.OK && exportOpt.Resetted)
            {
                InitializeExportOptions();
                InitializeLogger();
                InitalizeOptions();
            }
        }

        private void assetListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (endfieldVirtualAssetListMode)
            {
                if (e.ItemIndex < endfieldVisibleAssetRecords.Count)
                {
                    var asset = endfieldVisibleAssetRecords[e.ItemIndex];
                    var item = new ListViewItem(string.IsNullOrWhiteSpace(asset.Name) ? "[unnamed]" : asset.Name);
                    item.SubItems.Add(asset.Container);
                    item.SubItems.Add(asset.Type);
                    item.SubItems.Add(asset.PathId.ToString());
                    item.SubItems.Add(string.Empty);
                    item.SubItems.Add(Path.GetFileName(asset.Source));
                    e.Item = item;
                }
                return;
            }
            if (e.ItemIndex < visibleAssets.Count)
            {
                e.Item = visibleAssets[e.ItemIndex];
            }
        }

        private void tabPageSelected(object sender, TabControlEventArgs e)
        {
            switch (e.TabPageIndex)
            {
                case 0:
                    treeSearch.Select();
                    break;
                case 1:
                    listSearch.Select();
                    break;
            }
        }

        private void treeSearch_TextChanged(object sender, EventArgs e)
        {
            treeSrcResults.Clear();
            nextGObject = 0;
        }

        private void treeSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !string.IsNullOrEmpty(treeSearch.Text))
            {
                if (treeSrcResults.Count == 0)
                {
                    try
                    {
                        Regex.Match("", treeSearch.Text, RegexOptions.IgnoreCase);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Invalid Regex.\n" + ex.Message);
                        return;
                    }
                    var regex = new Regex(treeSearch.Text, RegexOptions.IgnoreCase);
                    foreach (TreeNode node in sceneTreeView.Nodes)
                    {
                        TreeNodeSearch(regex, node);
                    }
                }
                if (treeSrcResults.Count > 0)
                {
                    if (e.Shift)
                    {
                        foreach (var node in treeSrcResults)
                        {
                            var tempNode = node;
                            if (e.Alt)
                            {
                                while (tempNode.Parent != null)
                                {
                                    tempNode = tempNode.Parent;
                                }
                            }
                            tempNode.EnsureVisible();
                            tempNode.Checked = e.Control;
                        }
                        sceneTreeView.SelectedNode = treeSrcResults[0];
                    }
                    else
                    {
                        if (nextGObject >= treeSrcResults.Count)
                        {
                            nextGObject = 0;
                        }
                        var node = treeSrcResults[nextGObject];
                        if (e.Alt)
                        {
                            while (node.Parent != null)
                            {
                                node = node.Parent;
                            }
                        }

                        node.EnsureVisible();
                        node.Checked = e.Control;
                        sceneTreeView.SelectedNode = treeSrcResults[nextGObject];
                        nextGObject++;
                    }
                }
            }
        }

        private void TreeNodeSearch(Regex regex, TreeNode treeNode)
        {
            if (regex.IsMatch(treeNode.Text))
            {
                treeSrcResults.Add(treeNode);
            }

            foreach (TreeNode node in treeNode.Nodes)
            {
                TreeNodeSearch(regex, node);
            }
        }

        private void sceneTreeView_AfterCheck(object sender, TreeViewEventArgs e)
        {
            foreach (TreeNode childNode in e.Node.Nodes)
            {
                childNode.Checked = e.Node.Checked;
            }
        }

        private void sceneHierarchy_Click(object sender, EventArgs e)
        {
            var saveFileDialog = new SaveFileDialog() { FileName = "scene.json", Filter = "Scene Hierarchy dump | *.json" };
            if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                var path = saveFileDialog.FileName;
                var nodes = new Dictionary<string, object>();
                foreach (TreeNode node in sceneTreeView.Nodes)
                {
                    var value = GetNode(node);
                    nodes.Add(node.Text, value);
                }
                var json = JsonConvert.SerializeObject(nodes, Formatting.Indented);
                File.WriteAllText(path, json);
                Logger.Info("Scene Hierarchy dumped sucessfully !!");
            }
        }

        private object GetNode(TreeNode treeNode)
        {
            var nodes = new Dictionary<string, object>();
            foreach (TreeNode node in treeNode.Nodes)
            {
                if (HasGameObjectNode(node))
                {
                    nodes.TryAdd(node.Text, GetNode(node));
                }
            }
            return nodes.Count == 0 ? string.Empty : nodes;
        }

        private bool HasGameObjectNode(TreeNode treeNode)
        {
            if (treeNode is GameObjectTreeNode gameObjectNode && !(bool)gameObjectNode.gameObject.m_Transform?.m_Father.IsNull)
            {
                return gameObjectNode.gameObject.m_Animator != null;
            }
            else
            {
                foreach (TreeNode node in treeNode.Nodes)
                {
                    return HasGameObjectNode(node);
                }
                return false;
            }
        }

        private void listSearch_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                Invoke(new Action(FilterAssetList));
            }
        }

        private async void assetListView_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn != e.Column)
            {
                reverseSort = false;
            }
            else
            {
                reverseSort = !reverseSort;
            }
            sortColumn = e.Column;
            if (endfieldVirtualAssetListMode)
            {
                var generation = Interlocked.Increment(ref endfieldListOperationGeneration);
                var column = e.Column;
                var descending = reverseSort;
                var sorted = endfieldVisibleAssetRecords.ToList();
                StatusStripUpdate($"Sorting {sorted.Count:N0} assets...");
                await Task.Run(() => sorted.Sort((a, b) =>
                {
                    var result = column switch
                    {
                        0 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                        1 => string.Compare(a.Container, b.Container, StringComparison.OrdinalIgnoreCase),
                        2 => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
                        3 => a.PathId.CompareTo(b.PathId),
                        5 => string.Compare(a.Source, b.Source, StringComparison.OrdinalIgnoreCase),
                        _ => 0
                    };
                    return descending ? -result : result;
                }));
                if (generation != endfieldListOperationGeneration || !endfieldVirtualAssetListMode)
                    return;
                endfieldVisibleAssetRecords = sorted;
                assetListView.SelectedIndices.Clear();
                assetListView.Refresh();
                StatusStripUpdate($"Sorted {sorted.Count:N0} assets.");
                return;
            }
            assetListView.BeginUpdate();
            assetListView.SelectedIndices.Clear();
            if (sortColumn == 4) //FullSize
            {
                visibleAssets.Sort((a, b) =>
                {
                    var asf = a.FullSize;
                    var bsf = b.FullSize;
                    return reverseSort ? bsf.CompareTo(asf) : asf.CompareTo(bsf);
                });
            }
            else if (sortColumn == 3) // PathID
            {
                visibleAssets.Sort((x, y) =>
                {
                    long pathID_X = x.m_PathID;
                    long pathID_Y = y.m_PathID;
                    return reverseSort ? pathID_Y.CompareTo(pathID_X) : pathID_X.CompareTo(pathID_Y);
                });
            }
            else
            {
                visibleAssets.Sort((a, b) =>
                {
                    var at = a.SubItems[sortColumn].Text;
                    var bt = b.SubItems[sortColumn].Text;
                    return reverseSort ? bt.CompareTo(at) : at.CompareTo(bt);
                });
            }
            assetListView.EndUpdate();
        }

        private async void selectAsset(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            if (!e.IsSelected)
                return;

            previewPanel.BackgroundImage = Properties.Resources.preview;
            previewPanel.BackgroundImageLayout = ImageLayout.Center;
            previewPanel.ContextMenuStrip = null;
            classTextBox.Visible = false;
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            FMODpanel.Visible = false;
            glControl.Visible = false;
            StatusStripUpdate("");

            FMODreset();

            if (endfieldVirtualAssetListMode)
            {
                if (e.ItemIndex >= 0 && e.ItemIndex < endfieldVisibleAssetRecords.Count)
                    await PreviewEndfieldAssetAsync(endfieldVisibleAssetRecords[e.ItemIndex]);
                return;
            }

            lastSelectedItem = (AssetItem)e.Item;

            if (tabControl2.SelectedIndex == 1)
            {
                dumpTextBox.Text = DumpAsset(lastSelectedItem.Asset);
            }
            if (enablePreview.Checked)
            {
                PreviewAsset(lastSelectedItem);
                if (displayInfo.Checked && lastSelectedItem.InfoText != null)
                {
                    assetInfoLabel.Text = lastSelectedItem.InfoText;
                    assetInfoLabel.Visible = true;
                }
            }
        }

        private async void sceneTreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (endfieldVirtualPathsToolStripMenuItem?.Checked != true)
                return;

            if (e.Node.Tag is VirtualAssetFile file)
            {
                endfieldSelectedVirtualFile = file;
                endfieldSelectedPrefabRoot = null;
                if (file.IsPrefab)
                    await PreviewEndfieldPrefabAsync(file);
                else
                {
                    var previewRecord = ChooseVirtualFilePreviewAsset(file.Records);
                    if (previewRecord != null)
                        await PreviewEndfieldAssetAsync(previewRecord);
                }
            }
            else if (e.Node.Tag is VirtualAssetRecord asset)
            {
                endfieldSelectedVirtualFile = null;
                endfieldSelectedPrefabRoot = null;
                await PreviewEndfieldAssetAsync(asset);
            }
        }

        private async Task<GameObject> PreviewEndfieldPrefabAsync(VirtualAssetFile file)
        {
            if (file == null || !file.IsPrefab || file.Records.Count == 0)
                return null;
            if (endfieldVfsArchive == null || string.IsNullOrWhiteSpace(endfieldWorkspace))
            {
                StatusStripUpdate("Open Endfield VFS first to preview the Prefab structure.");
                return null;
            }

            var source = file.Records.Select(x => x.Source)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(source))
            {
                StatusStripUpdate("The selected Prefab has no source Bundle.");
                return null;
            }

            ResetEndfieldPreviewCancellation();
            var token = endfieldPreviewCancellation.Token;
            try
            {
                await endfieldPreviewLock.WaitAsync(token);
                try
                {
                    await LoadEndfieldBundleClosureAsync(source, token);
                    var sourceCabs = endfieldDependencyIndex?.GetCabNames(source)
                        ?? Array.Empty<string>();
                    var gameObjects = assetsManager.assetsFileList
                        .SelectMany(x => x.Objects).OfType<GameObject>();
                    var root = EndfieldPrefabDocument.FindRoot(gameObjects, file, sourceCabs);
                    if (root == null)
                        throw new InvalidDataException($"Prefab root '{file.Stem}' was not found in its dependency closure.");

                    endfieldSelectedPrefabRoot = root;
                    lastSelectedItem = new AssetItem(root) { Container = file.Container };
                    assetInfoLabel.Text = $"Prefab\n{file.Container}\nSource: {source}\n" +
                                          $"Root PathID: {root.m_PathID}";
                    assetInfoLabel.Visible = displayInfo.Checked;
                    ResetEndfieldPreviewSurface();
                    PreviewText(EndfieldPrefabDocument.Build(file, root));
                    if (tabControl2.SelectedIndex == 1)
                        dumpTextBox.Text = DumpAsset(root);
                    StatusStripUpdate($"Prefab structure: {file.Container}");
                    return root;
                }
                finally
                {
                    endfieldPreviewLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Endfield Prefab preview failed for {file.Container}: {ex}");
                StatusStripUpdate($"Prefab preview failed: {ex.Message}");
                return null;
            }
        }

        private async Task PreviewEndfieldAssetAsync(VirtualAssetRecord record)
        {
            assetInfoLabel.Text = $"{record.Type}\n{record.Container}\nSource: {record.Source}\nPathID: {record.PathId}";
            assetInfoLabel.Visible = displayInfo.Checked;

            if (!enablePreview.Checked)
                return;
            if (endfieldVfsArchive == null || string.IsNullOrWhiteSpace(endfieldWorkspace))
            {
                StatusStripUpdate("Open Endfield VFS first to preview indexed assets.");
                return;
            }

            var logicalPath = GetEndfieldLogicalBundlePath(record.Source);
            if (string.IsNullOrWhiteSpace(logicalPath) || !endfieldVfsArchive.TryGet(logicalPath, out _))
            {
                StatusStripUpdate($"Bundle is not present in the VFS index: {logicalPath ?? record.Source}");
                return;
            }

            ResetEndfieldPreviewCancellation();
            var token = endfieldPreviewCancellation.Token;

            try
            {
                await endfieldPreviewLock.WaitAsync(token);
                try
                {
                    await LoadEndfieldBundleClosureAsync(record.Source, token);
                    var loadedAsset = FindLoadedEndfieldAsset(record);
                    if (loadedAsset == null)
                        throw new InvalidDataException($"PathID {record.PathId} ({record.Type}) was not found after parsing the bundle.");

                    lastSelectedItem = loadedAsset;
                    ResetEndfieldPreviewSurface();
                    PreviewAsset(loadedAsset);
                    if (displayInfo.Checked && loadedAsset.InfoText != null)
                    {
                        assetInfoLabel.Text = loadedAsset.InfoText;
                        assetInfoLabel.Visible = true;
                    }
                    if (tabControl2.SelectedIndex == 1)
                        dumpTextBox.Text = DumpAsset(loadedAsset.Asset);
                    StatusStripUpdate($"Previewing {record.Type}: {record.Container}");
                }
                finally
                {
                    endfieldPreviewLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // A newer selection superseded this one.
            }
            catch (Exception ex)
            {
                Logger.Error($"Endfield on-demand preview failed for {record.Source}: {ex}");
                StatusStripUpdate($"Preview failed: {ex.Message}");
            }
        }

        private void ResetEndfieldPreviewCancellation()
        {
            endfieldPreviewCancellation.Cancel();
            endfieldPreviewCancellation.Dispose();
            endfieldPreviewCancellation = new CancellationTokenSource();
        }

        private void ResetEndfieldPreviewSurface()
        {
            previewPanel.BackgroundImage = Properties.Resources.preview;
            previewPanel.BackgroundImageLayout = ImageLayout.Center;
            previewPanel.ContextMenuStrip = null;
            classTextBox.Visible = false;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            FMODpanel.Visible = false;
            glControl.Visible = false;
            FMODreset();
        }

        private async Task LoadEndfieldBundleClosureAsync(string source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var logicalPath = GetEndfieldLogicalBundlePath(source);
            if (string.IsNullOrWhiteSpace(logicalPath) || !endfieldVfsArchive.TryGet(logicalPath, out _))
                throw new InvalidDataException($"Bundle is not present in the VFS index: {logicalPath ?? source}");

            var bundlePaths = endfieldDependencyIndex?.ResolveBundleClosure(logicalPath)
                ?? new[] { logicalPath };
            StatusStripUpdate($"Reading {bundlePaths.Count:N0} required Bundle(s) from VFS...");
            var cachePaths = await Task.Run(() => bundlePaths
                .Where(path => endfieldVfsArchive.TryGet(path, out _))
                .Select(path => endfieldVfsArchive.ExtractToCache(path, endfieldWorkspace))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(), token);
            token.ThrowIfCancellationRequested();
            if (cachePaths.Length == 0)
                throw new InvalidDataException("None of the required Bundles were present in the VFS index.");

            // Any AssetItem retained across Clear() points at a disposed
            // ObjectReader. Clear UI references before closing those streams.
            lastSelectedItem = null;
            endfieldSelectedPrefabRoot = null;
            dumpTextBox.Clear();
            assetsManager.Clear();
            exportableAssets.Clear();
            visibleAssets.Clear();
            endfieldLoadedAssetLookup.Clear();
            endfieldLoadedObjectLookup.Clear();
            assetsManager.Game = Studio.Game;
            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            assetsManager.ResolveDependencies = false;
            assetsManager.FilterData = new AssetFilterData { Items = new List<AssetFilterDataItem>() };

            StatusStripUpdate($"Parsing {cachePaths.Length:N0} required Bundle(s)...");
            await Task.Run(() => assetsManager.LoadFiles(cachePaths, mergeSplitAssets: false), token);
            token.ThrowIfCancellationRequested();
            if (assetsManager.assetsFileList.Count == 0)
                throw new InvalidDataException("The selected VFS entry did not contain a readable Unity bundle.");

            await Task.Run(BuildAssetData, token);
            token.ThrowIfCancellationRequested();
            BuildEndfieldLoadedAssetLookup();
        }

        private static string GetEndfieldLogicalBundlePath(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return string.Empty;
            var normalized = source.Replace('\\', '/');
            var marker = normalized.IndexOf("Bundles/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
                normalized = normalized[marker..];
            return EndfieldVfsArchive.NormalizeLogicalPath(normalized);
        }

        private void BuildEndfieldLoadedAssetLookup()
        {
            endfieldLoadedAssetLookup.Clear();
            endfieldLoadedObjectLookup.Clear();
            foreach (var asset in exportableAssets)
            {
                var source = asset.SourceFile?.fullName;
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                var key = BuildEndfieldAssetKey(source, asset.m_PathID, asset.TypeString);
                if (!endfieldLoadedAssetLookup.ContainsKey(key))
                    endfieldLoadedAssetLookup.Add(key, asset);

                var fileKey = BuildEndfieldAssetKey(Path.GetFileName(source), asset.m_PathID, asset.TypeString);
                if (!endfieldLoadedAssetLookup.ContainsKey(fileKey))
                    endfieldLoadedAssetLookup.Add(fileKey, asset);
            }

            // GameObjects are intentionally absent from exportableAssets and
            // live in the scene tree. Index every parsed object separately so
            // an indexed Prefab/GameObject can still be resolved by PathID.
            foreach (var obj in assetsManager.assetsFileList.SelectMany(x => x.Objects))
            {
                var source = obj.assetsFile?.fullName;
                if (string.IsNullOrWhiteSpace(source))
                    continue;
                var type = obj.type.ToString();
                var key = BuildEndfieldAssetKey(source, obj.m_PathID, type);
                if (!endfieldLoadedObjectLookup.ContainsKey(key))
                    endfieldLoadedObjectLookup.Add(key, obj);
                var fileKey = BuildEndfieldAssetKey(Path.GetFileName(source), obj.m_PathID, type);
                if (!endfieldLoadedObjectLookup.ContainsKey(fileKey))
                    endfieldLoadedObjectLookup.Add(fileKey, obj);
            }
        }

        private AssetItem FindLoadedEndfieldAsset(VirtualAssetRecord record)
        {
            var type = record.Type ?? string.Empty;
            var key = BuildEndfieldAssetKey(record.Source, record.PathId, type);
            if (endfieldLoadedAssetLookup.TryGetValue(key, out var asset))
                return asset;

            key = BuildEndfieldAssetKey(Path.GetFileName(record.Source), record.PathId, type);
            if (endfieldLoadedAssetLookup.TryGetValue(key, out asset))
                return asset;

            key = BuildEndfieldAssetKey(record.Source, record.PathId, type);
            if (endfieldLoadedObjectLookup.TryGetValue(key, out var loadedObject))
                return CreateEndfieldAssetItem(loadedObject, record.Container);

            key = BuildEndfieldAssetKey(Path.GetFileName(record.Source), record.PathId, type);
            if (endfieldLoadedObjectLookup.TryGetValue(key, out loadedObject))
                return CreateEndfieldAssetItem(loadedObject, record.Container);

            var sourceCabs = endfieldDependencyIndex?.GetCabNames(record.Source);
            if (sourceCabs?.Count > 0)
            {
                var cabNames = sourceCabs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                asset = exportableAssets.FirstOrDefault(x =>
                    x.m_PathID == record.PathId &&
                    string.Equals(x.TypeString, type, StringComparison.OrdinalIgnoreCase) &&
                    cabNames.Contains(Path.GetFileName(x.SourceFile?.fileName ?? string.Empty)));
                if (asset != null)
                    return asset;

                loadedObject = assetsManager.assetsFileList
                    .Where(x => cabNames.Contains(Path.GetFileName(x.fileName ?? string.Empty)))
                    .SelectMany(x => x.Objects)
                    .FirstOrDefault(x => x.m_PathID == record.PathId &&
                        string.Equals(x.type.ToString(), type, StringComparison.OrdinalIgnoreCase));
                if (loadedObject != null)
                    return CreateEndfieldAssetItem(loadedObject, record.Container);
            }

            asset = exportableAssets.FirstOrDefault(x =>
                x.m_PathID == record.PathId &&
                string.Equals(x.TypeString, type, StringComparison.OrdinalIgnoreCase));
            if (asset != null)
                return asset;

            asset = exportableAssets.FirstOrDefault(x =>
                x.m_PathID == record.PathId &&
                string.Equals(x.Text, record.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TypeString, type, StringComparison.OrdinalIgnoreCase));
            if (asset != null)
                return asset;

            loadedObject = assetsManager.assetsFileList.SelectMany(x => x.Objects)
                .FirstOrDefault(x => x.m_PathID == record.PathId &&
                    string.Equals(x.Name, record.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.type.ToString(), type, StringComparison.OrdinalIgnoreCase));
            return loadedObject == null ? null : CreateEndfieldAssetItem(loadedObject, record.Container);
        }

        private static AssetItem CreateEndfieldAssetItem(AnimeStudio.Object asset, string container)
        {
            var item = new AssetItem(asset) { Container = container ?? string.Empty };
            item.SetSubItems();
            return item;
        }

        private static string BuildEndfieldAssetKey(string source, long pathId, string type)
        {
            var normalized = source ?? string.Empty;
            try { normalized = Path.GetFullPath(normalized); } catch { }
            return $"{normalized.Replace('\\', '/')}|{pathId}|{type}";
        }

        private void classesListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            classTextBox.Visible = true;
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            FMODpanel.Visible = false;
            glControl.Visible = false;
            StatusStripUpdate("");
            if (e.IsSelected)
            {
                classTextBox.Text = ((TypeTreeItem)classesListView.SelectedItems[0]).ToString();
            }
        }

        private void preview_Resize(object sender, EventArgs e)
        {
            if (glControlLoaded && glControl.Visible)
            {
                ChangeGLSize(glControl.Size);
                glControl.Invalidate();
            }
        }

        private void PreviewAsset(AssetItem assetItem)
        {
            if (assetItem == null)
                return;
            try
            {
                switch (assetItem.Asset)
                {
                    case GameObject m_GameObject when Properties.Settings.Default.enableModelPreview:
                        PreviewGameObject(m_GameObject);
                        break;
                    case Texture2D m_Texture2D:
                        PreviewTexture2D(assetItem, m_Texture2D);
                        break;
                    case AudioClip m_AudioClip:
                        PreviewAudioClip(assetItem, m_AudioClip);
                        break;
                    case Shader m_Shader:
                        PreviewShader(m_Shader);
                        break;
                    case TextAsset m_TextAsset:
                        PreviewTextAsset(m_TextAsset);
                        break;
                    case MonoBehaviour m_MonoBehaviour:
                        PreviewMonoBehaviour(m_MonoBehaviour);
                        break;
                    case Font m_Font:
                        PreviewFont(m_Font);
                        break;
                    case Mesh m_Mesh:
                        PreviewMesh(m_Mesh);
                        break;
                    case VideoClip _:
                    case MovieTexture _:
                        StatusStripUpdate("Only supported export.");
                        break;
                    case Sprite m_Sprite:
                        PreviewSprite(assetItem, m_Sprite);
                        break;
                    case Animator m_Animator when Properties.Settings.Default.enableModelPreview:
                        //StatusStripUpdate("Can be exported to FBX file.");
                        PreviewAnimator(m_Animator);
                        break;
                    case AnimationClip m_AnimationClip:
                        PreviewAnimationClip(m_AnimationClip);
                        break;
                    case MiHoYoBinData m_MiHoYoBinData:
                        PreviewText(m_MiHoYoBinData.AsString);
                        StatusStripUpdate("Can be exported/previewed as JSON if data is a valid JSON (check XOR).");
                        break;
                    case NapAssetBundleIndexAsset m_NapAssetBundleIndexAsset:
                        PreviewText(DumpAsset(assetItem.Asset));
                        break;
                    default:
                        var str = assetItem.Asset.Dump();
                        if (str != null)
                        {
                            textPreviewBox.Text = str;
                            textPreviewBox.Visible = true;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Preview {assetItem.Type}:{assetItem.Text} error\r\n{e.Message}\r\n{e.StackTrace}");
            }
        }

        private void PreviewTexture2D(AssetItem assetItem, Texture2D m_Texture2D)
        {
            var image = m_Texture2D.ConvertToImage(true);
            if (image != null)
            {
                var bitmap = new DirectBitmap(image.ConvertToBytes(), m_Texture2D.m_Width, m_Texture2D.m_Height);
                image.Dispose();
                assetItem.InfoText = $"Width: {m_Texture2D.m_Width}\nHeight: {m_Texture2D.m_Height}\nFormat: {m_Texture2D.m_TextureFormat}";
                switch (m_Texture2D.m_TextureSettings.m_FilterMode)
                {
                    case 0: assetItem.InfoText += "\nFilter Mode: Point "; break;
                    case 1: assetItem.InfoText += "\nFilter Mode: Bilinear "; break;
                    case 2: assetItem.InfoText += "\nFilter Mode: Trilinear "; break;
                }
                assetItem.InfoText += $"\nAnisotropic level: {m_Texture2D.m_TextureSettings.m_Aniso}\nMip map bias: {m_Texture2D.m_TextureSettings.m_MipBias}";
                switch (m_Texture2D.m_TextureSettings.m_WrapMode)
                {
                    case 0: assetItem.InfoText += "\nWrap mode: Repeat"; break;
                    case 1: assetItem.InfoText += "\nWrap mode: Clamp"; break;
                }
                assetItem.InfoText += "\nChannels: ";
                int validChannel = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (textureChannels[i])
                    {
                        assetItem.InfoText += textureChannelNames[i];
                        validChannel++;
                    }
                }
                if (validChannel == 0)
                    assetItem.InfoText += "None";
                if (validChannel != 4)
                {
                    var bytes = bitmap.Bits;
                    for (int i = 0; i < bitmap.Height; i++)
                    {
                        int offset = Math.Abs(bitmap.Stride) * i;
                        for (int j = 0; j < bitmap.Width; j++)
                        {
                            bytes[offset] = textureChannels[0] ? bytes[offset] : validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue;
                            bytes[offset + 1] = textureChannels[1] ? bytes[offset + 1] : validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue;
                            bytes[offset + 2] = textureChannels[2] ? bytes[offset + 2] : validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue;
                            bytes[offset + 3] = textureChannels[3] ? bytes[offset + 3] : byte.MaxValue;
                            offset += 4;
                        }
                    }
                }
                PreviewTexture(bitmap);

                StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle");
            }
            else
            {
                StatusStripUpdate("Unsupported image for preview");
            }
        }

        private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
        {
            //Info
            assetItem.InfoText = "Compression format: ";
            if (m_AudioClip.version[0] < 5)
            {
                switch (m_AudioClip.m_Type)
                {
                    case FMODSoundType.ACC:
                        assetItem.InfoText += "Acc";
                        break;
                    case FMODSoundType.AIFF:
                        assetItem.InfoText += "AIFF";
                        break;
                    case FMODSoundType.IT:
                        assetItem.InfoText += "Impulse tracker";
                        break;
                    case FMODSoundType.MOD:
                        assetItem.InfoText += "Protracker / Fasttracker MOD";
                        break;
                    case FMODSoundType.MPEG:
                        assetItem.InfoText += "MP2/MP3 MPEG";
                        break;
                    case FMODSoundType.OGGVORBIS:
                        assetItem.InfoText += "Ogg vorbis";
                        break;
                    case FMODSoundType.S3M:
                        assetItem.InfoText += "ScreamTracker 3";
                        break;
                    case FMODSoundType.WAV:
                        assetItem.InfoText += "Microsoft WAV";
                        break;
                    case FMODSoundType.XM:
                        assetItem.InfoText += "FastTracker 2 XM";
                        break;
                    case FMODSoundType.XMA:
                        assetItem.InfoText += "Xbox360 XMA";
                        break;
                    case FMODSoundType.VAG:
                        assetItem.InfoText += "PlayStation Portable ADPCM";
                        break;
                    case FMODSoundType.AUDIOQUEUE:
                        assetItem.InfoText += "iPhone";
                        break;
                    default:
                        assetItem.InfoText += "Unknown";
                        break;
                }
            }
            else
            {
                switch (m_AudioClip.m_CompressionFormat)
                {
                    case AudioCompressionFormat.PCM:
                        assetItem.InfoText += "PCM";
                        break;
                    case AudioCompressionFormat.Vorbis:
                        assetItem.InfoText += "Vorbis";
                        break;
                    case AudioCompressionFormat.ADPCM:
                        assetItem.InfoText += "ADPCM";
                        break;
                    case AudioCompressionFormat.MP3:
                        assetItem.InfoText += "MP3";
                        break;
                    case AudioCompressionFormat.PSMVAG:
                        assetItem.InfoText += "PlayStation Portable ADPCM";
                        break;
                    case AudioCompressionFormat.HEVAG:
                        assetItem.InfoText += "PSVita ADPCM";
                        break;
                    case AudioCompressionFormat.XMA:
                        assetItem.InfoText += "Xbox360 XMA";
                        break;
                    case AudioCompressionFormat.AAC:
                        assetItem.InfoText += "AAC";
                        break;
                    case AudioCompressionFormat.GCADPCM:
                        assetItem.InfoText += "Nintendo 3DS/Wii DSP";
                        break;
                    case AudioCompressionFormat.ATRAC9:
                        assetItem.InfoText += "PSVita ATRAC9";
                        break;
                    default:
                        assetItem.InfoText += "Unknown";
                        break;
                }
            }

            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return;
            var exinfo = new FMOD.CREATESOUNDEXINFO();

            exinfo.cbsize = Marshal.SizeOf(exinfo);
            exinfo.length = (uint)m_AudioClip.m_Size;

            var result = system.createSound(m_AudioData, FMOD.MODE.OPENMEMORY | loopMode, ref exinfo, out sound);
            if (ERRCHECK(result)) return;

            sound.getNumSubSounds(out var numsubsounds);

            if (numsubsounds > 0)
            {
                result = sound.getSubSound(0, out var subsound);
                if (result == FMOD.RESULT.OK)
                {
                    sound = subsound;
                }
            }

            result = sound.getLength(out FMODlenms, FMOD.TIMEUNIT.MS);
            if (ERRCHECK(result)) return;

            result = system.playSound(sound, null, true, out channel);
            if (ERRCHECK(result)) return;

            FMODpanel.Visible = true;

            result = channel.getFrequency(out var frequency);
            if (ERRCHECK(result)) return;

            FMODinfoLabel.Text = frequency + " Hz";
            FMODtimerLabel.Text = $"0:0.0 / {FMODlenms / 1000 / 60}:{FMODlenms / 1000 % 60}.{FMODlenms / 10 % 100}";
        }

        private void PreviewShader(Shader m_Shader)
        {
            if (m_Shader.byteSize > 0xFFFFFFF)
            {
                PreviewText("Shader is too large to parse");
                return;
            }

            var str = m_Shader.Convert();
            PreviewText(str == null ? "Serialized Shader can't be read" : str.Replace("\n", "\r\n"));
        }

        private void PreviewTextAsset(TextAsset m_TextAsset)
        {
            var text = Encoding.UTF8.GetString(m_TextAsset.m_Script);
            text = text.Replace("\n", "\r\n").Replace("\0", "");
            PreviewText(text);
        }

        private void PreviewMonoBehaviour(MonoBehaviour m_MonoBehaviour)
        {
            var obj = m_MonoBehaviour.ToType();
            if (obj == null)
            {
                var type = MonoBehaviourToTypeTree(m_MonoBehaviour);
                obj = m_MonoBehaviour.ToType(type);
            }
            var str = JsonConvert.SerializeObject(obj, Formatting.Indented);
            PreviewText(str);
        }

        private void PreviewFont(Font m_Font)
        {
            if (m_Font.m_FontData != null)
            {
                var data = Marshal.AllocCoTaskMem(m_Font.m_FontData.Length);
                Marshal.Copy(m_Font.m_FontData, 0, data, m_Font.m_FontData.Length);

                uint cFonts = 0;
                var re = FontHelper.AddFontMemResourceEx(data, (uint)m_Font.m_FontData.Length, IntPtr.Zero, ref cFonts);
                if (re != IntPtr.Zero)
                {
                    using (var pfc = new PrivateFontCollection())
                    {
                        pfc.AddMemoryFont(data, m_Font.m_FontData.Length);
                        Marshal.FreeCoTaskMem(data);
                        if (pfc.Families.Length > 0)
                        {
                            fontPreviewBox.SelectionStart = 0;
                            fontPreviewBox.SelectionLength = 80;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 16, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 81;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 12, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 138;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 18, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 195;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 24, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 252;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 36, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 309;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 48, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 366;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 60, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 423;
                            fontPreviewBox.SelectionLength = 55;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 72, FontStyle.Regular);
                            fontPreviewBox.Visible = true;
                        }
                    }
                    return;
                }
            }
            StatusStripUpdate("Unsupported font for preview. Try to export.");
        }

        private void PreviewMesh(Mesh m_Mesh)
        {
            if (m_Mesh.m_VertexCount > 0)
            {
                viewMatrixData = Matrix4.CreateRotationY(-(float)Math.PI / 4) * Matrix4.CreateRotationX(-(float)Math.PI / 6);
                #region Vertices
                if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
                {
                    StatusStripUpdate("Mesh can't be previewed.");
                    return;
                }
                int count = 3;
                if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
                {
                    count = 4;
                }
                vertexData = new OpenTK.Mathematics.Vector3[m_Mesh.m_VertexCount];
                // Calculate Bounding
                float[] min = new float[3];
                float[] max = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    min[i] = m_Mesh.m_Vertices[i];
                    max[i] = m_Mesh.m_Vertices[i];
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        min[i] = Math.Min(min[i], m_Mesh.m_Vertices[v * count + i]);
                        max[i] = Math.Max(max[i], m_Mesh.m_Vertices[v * count + i]);
                    }
                    vertexData[v] = new OpenTK.Mathematics.Vector3(
                        m_Mesh.m_Vertices[v * count],
                        m_Mesh.m_Vertices[v * count + 1],
                        m_Mesh.m_Vertices[v * count + 2]);
                }

                // Calculate modelMatrix
                var dist = OpenTK.Mathematics.Vector3.One;
                var offset = OpenTK.Mathematics.Vector3.Zero;
                for (int i = 0; i < 3; i++)
                {
                    dist[i] = max[i] - min[i];
                    offset[i] = (max[i] + min[i]) / 2;
                }
                float d = Math.Max(1e-5f, dist.Length);
                modelMatrixData = Matrix4.CreateTranslation(-offset) * Matrix4.CreateScale(2f / d);
                #endregion
                #region Indicies
                indiceData = new int[m_Mesh.m_Indices.Count];
                for (int i = 0; i < m_Mesh.m_Indices.Count; i = i + 3)
                {
                    indiceData[i] = (int)m_Mesh.m_Indices[i];
                    indiceData[i + 1] = (int)m_Mesh.m_Indices[i + 1];
                    indiceData[i + 2] = (int)m_Mesh.m_Indices[i + 2];
                }
                #endregion
                #region Normals
                if (m_Mesh.m_Normals != null && m_Mesh.m_Normals.Length > 0)
                {
                    if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                        count = 3;
                    else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                        count = 4;
                    normalData = new OpenTK.Mathematics.Vector3[m_Mesh.m_VertexCount];
                    for (int n = 0; n < m_Mesh.m_VertexCount; n++)
                    {
                        normalData[n] = new OpenTK.Mathematics.Vector3(
                            m_Mesh.m_Normals[n * count],
                            m_Mesh.m_Normals[n * count + 1],
                            m_Mesh.m_Normals[n * count + 2]);
                    }
                }
                else
                    normalData = null;
                // calculate normal by ourself
                normal2Data = new OpenTK.Mathematics.Vector3[m_Mesh.m_VertexCount];
                int[] normalCalculatedCount = new int[m_Mesh.m_VertexCount];
                for (int i = 0; i < m_Mesh.m_VertexCount; i++)
                {
                    normal2Data[i] = OpenTK.Mathematics.Vector3.Zero;
                    normalCalculatedCount[i] = 0;
                }
                for (int i = 0; i < m_Mesh.m_Indices.Count; i = i + 3)
                {
                    var dir1 = vertexData[indiceData[i + 1]] - vertexData[indiceData[i]];
                    var dir2 = vertexData[indiceData[i + 2]] - vertexData[indiceData[i]];
                    var normal = OpenTK.Mathematics.Vector3.Cross(dir1, dir2);
                    normal.Normalize();
                    for (int j = 0; j < 3; j++)
                    {
                        normal2Data[indiceData[i + j]] += normal;
                        normalCalculatedCount[indiceData[i + j]]++;
                    }
                }
                for (int i = 0; i < m_Mesh.m_VertexCount; i++)
                {
                    if (normalCalculatedCount[i] == 0)
                        normal2Data[i] = new OpenTK.Mathematics.Vector3(0, 1, 0);
                    else
                        normal2Data[i] /= normalCalculatedCount[i];
                }
                #endregion
                #region Colors
                if (m_Mesh.m_Colors != null && m_Mesh.m_Colors.Length == m_Mesh.m_VertexCount * 3)
                {
                    colorData = new OpenTK.Mathematics.Vector4[m_Mesh.m_VertexCount];
                    for (int c = 0; c < m_Mesh.m_VertexCount; c++)
                    {
                        colorData[c] = new OpenTK.Mathematics.Vector4(
                            m_Mesh.m_Colors[c * 3],
                            m_Mesh.m_Colors[c * 3 + 1],
                            m_Mesh.m_Colors[c * 3 + 2],
                            1.0f);
                    }
                }
                else if (m_Mesh.m_Colors != null && m_Mesh.m_Colors.Length == m_Mesh.m_VertexCount * 4)
                {
                    colorData = new OpenTK.Mathematics.Vector4[m_Mesh.m_VertexCount];
                    for (int c = 0; c < m_Mesh.m_VertexCount; c++)
                    {
                        colorData[c] = new OpenTK.Mathematics.Vector4(
                        m_Mesh.m_Colors[c * 4],
                        m_Mesh.m_Colors[c * 4 + 1],
                        m_Mesh.m_Colors[c * 4 + 2],
                        m_Mesh.m_Colors[c * 4 + 3]);
                    }
                }
                else
                {
                    colorData = new OpenTK.Mathematics.Vector4[m_Mesh.m_VertexCount];
                    for (int c = 0; c < m_Mesh.m_VertexCount; c++)
                    {
                        colorData[c] = new OpenTK.Mathematics.Vector4(0.5f, 0.5f, 0.5f, 1.0f);
                    }
                }
                #endregion
                glControl.Visible = true;
                CreateVAO();
                StatusStripUpdate("Using OpenGL Version: " + GL.GetString(StringName.Version) + "\n"
                                  + "'Mouse Left'=Rotate | 'Mouse Right'=Move | 'Mouse Wheel'=Zoom \n"
                                  + "'Ctrl W'=Wireframe | 'Ctrl S'=Shade | 'Ctrl N'=ReNormal ");
            }
            else
            {
                StatusStripUpdate("Unable to preview this mesh");
            }
        }

        private void PreviewGameObject(GameObject m_GameObject)
        {
            var options = new ModelConverter.Options()
            {
                imageFormat = Properties.Settings.Default.convertType,
                game = Studio.Game,
                collectAnimations = Properties.Settings.Default.collectAnimations,
                exportMaterials = false,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Properties.Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Properties.Settings.Default.texs),
            };
            var model = new ModelConverter(m_GameObject, options, Array.Empty<AnimationClip>());
            PreviewModel(model);
        }
        private void PreviewAnimator(Animator m_Animator)
        {
            var options = new ModelConverter.Options()
            {
                imageFormat = Properties.Settings.Default.convertType,
                game = Studio.Game,
                collectAnimations = Properties.Settings.Default.collectAnimations,
                exportMaterials = false,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Properties.Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Properties.Settings.Default.texs),
            };
            var model = new ModelConverter(m_Animator, options, Array.Empty<AnimationClip>());
            PreviewModel(model);
        }

        private void PreviewAnimationClip(AnimationClip clip)
        {
            var str = clip.Convert();
            if (string.IsNullOrEmpty(str))
                str = "Legacy animation is not supported";
            PreviewText(str.Replace("\n", "\r\n"));
        }

        private void PreviewModel(ModelConverter model)
        {
            if (model.MeshList.Count > 0)
            {
                viewMatrixData = Matrix4.CreateRotationY(-(float)Math.PI / 4) * Matrix4.CreateRotationX(-(float)Math.PI / 6);
                #region Vertices
                vertexData = model.MeshList.SelectMany(x => x.VertexList).Select(x => new OpenTK.Mathematics.Vector3(x.Vertex.X, x.Vertex.Y, x.Vertex.Z)).ToArray();
                // Calculate Bounding
                var min = vertexData.Aggregate(OpenTK.Mathematics.Vector3.ComponentMin);
                var max = vertexData.Aggregate(OpenTK.Mathematics.Vector3.ComponentMax);

                // Calculate modelMatrix
                var dist = max - min;
                var offset = (max - min) / 2;
                float d = Math.Max(1e-5f, dist.Length);
                modelMatrixData = Matrix4.CreateTranslation(-offset) * Matrix4.CreateScale(2f / d);
                #endregion
                #region Indicies
                int meshOffset = 0;
                var indices = new List<int>();
                foreach (var mesh in model.MeshList)
                {
                    foreach (var submesh in mesh.SubmeshList)
                    {
                        foreach (var face in submesh.FaceList)
                        {
                            foreach (var index in face.VertexIndices)
                            {
                                indices.Add(submesh.BaseVertex + index + meshOffset);
                            }
                        }
                    }
                    meshOffset += mesh.VertexList.Count;
                }
                indiceData = indices.ToArray();
                #endregion
                #region Normals
                normalData = model.MeshList.SelectMany(x => x.VertexList).Select(x => new OpenTK.Mathematics.Vector3(x.Normal.X, x.Normal.Y, x.Normal.Z)).ToArray();
                // calculate normal by ourself
                normal2Data = new OpenTK.Mathematics.Vector3[vertexData.Length];
                int[] normalCalculatedCount = new int[vertexData.Length];
                Array.Fill(normal2Data, OpenTK.Mathematics.Vector3.Zero);
                Array.Fill(normalCalculatedCount, 0);
                for (int j = 0; j < indiceData.Length; j += 3)
                {
                    var dir1 = vertexData[indiceData[j + 1]] - vertexData[indiceData[j]];
                    var dir2 = vertexData[indiceData[j + 2]] - vertexData[indiceData[j]];
                    var normal = OpenTK.Mathematics.Vector3.Cross(dir1, dir2);
                    normal.Normalize();
                    for (int k = 0; k < 3; k++)
                    {
                        normal2Data[indiceData[j + k]] += normal;
                        normalCalculatedCount[indiceData[j + k]]++;
                    }
                }
                for (int j = 0; j < vertexData.Length; j++)
                {
                    if (normalCalculatedCount[j] == 0)
                        normal2Data[j] = new OpenTK.Mathematics.Vector3(0, 1, 0);
                    else
                        normal2Data[j] /= normalCalculatedCount[j];
                }
                #endregion
                #region Colors
                colorData = model.MeshList.SelectMany(x => x.VertexList).Select(x => new OpenTK.Mathematics.Vector4(x.Color.R, x.Color.G, x.Color.B, x.Color.A)).ToArray();
                #endregion
                glControl.Visible = true;
                CreateVAO();
                StatusStripUpdate("Using OpenGL Version: " + GL.GetString(StringName.Version) + "\n"
                                  + "'Mouse Left'=Rotate | 'Mouse Right'=Move | 'Mouse Wheel'=Zoom \n"
                                  + "'Ctrl W'=Wireframe | 'Ctrl S'=Shade | 'Ctrl N'=ReNormal ");
            }
            else
            {
                StatusStripUpdate("Unable to preview this model");
            }
        }

        private void PreviewSprite(AssetItem assetItem, Sprite m_Sprite)
        {
            var image = m_Sprite.GetImage();
            if (image != null)
            {
                var bitmap = new DirectBitmap(image.ConvertToBytes(), image.Width, image.Height);
                image.Dispose();
                assetItem.InfoText = $"Width: {bitmap.Width}\nHeight: {bitmap.Height}\n";
                PreviewTexture(bitmap);
            }
            else
            {
                StatusStripUpdate("Unsupported sprite for preview.");
            }
        }

        private void PreviewTexture(DirectBitmap bitmap)
        {
            imageTexture?.Dispose();
            imageTexture = bitmap;
            previewPanel.BackgroundImage = imageTexture.Bitmap;
            previewPanel.ContextMenuStrip = previewContextMenuStrip;
            if (imageTexture.Width > previewPanel.Width || imageTexture.Height > previewPanel.Height)
                previewPanel.BackgroundImageLayout = ImageLayout.Zoom;
            else
                previewPanel.BackgroundImageLayout = ImageLayout.Center;
        }

        private void PreviewText(string text)
        {
            textPreviewBox.Text = text;
            textPreviewBox.Visible = true;
        }

        private void SetProgressBarValue(int value)
        {
            if (value < 0) value = 0;
            if (value > 100) value = 100;

            if (InvokeRequired)
            {
                var result = BeginInvoke(new Action(() => { progressBar1.Value = value; }));
                result.AsyncWaitHandle.WaitOne();
            }
            else
            {
                progressBar1.Value = value;
            }
        }

        private void StatusStripUpdate(string statusText)
        {
            if (InvokeRequired)
            {
                var result = BeginInvoke(() => { toolStripStatusLabel1.Text = statusText; });
                result.AsyncWaitHandle.WaitOne();
            }
            else
            {
                toolStripStatusLabel1.Text = statusText;
            }
        }

        public void ResetForm()
        {
            Interlocked.Increment(ref endfieldListOperationGeneration);
            Text = $"AnimeStudio v{System.Windows.Forms.Application.ProductVersion}";
            endfieldVirtualPathsToolStripMenuItem.Checked = false;
            endfieldVirtualPathIndex = null;
            endfieldDependencyIndex = null;
            endfieldVfsArchive = null;
            endfieldVirtualAssetListMode = false;
            endfieldVirtualAssetRecords.Clear();
            endfieldVisibleAssetRecords.Clear();
            endfieldLoadedAssetLookup.Clear();
            endfieldLoadedObjectLookup.Clear();
            endfieldVirtualMapPath = string.Empty;
            endfieldWorkspace = string.Empty;
            endfieldOriginalSceneNodes = null;
            endfieldSelectedVirtualFile = null;
            endfieldSelectedPrefabRoot = null;
            endfieldPreviewCancellation.Cancel();
            endfieldPreviewCancellation.Dispose();
            endfieldPreviewCancellation = new CancellationTokenSource();
            endfieldIndexCancellation.Cancel();
            endfieldIndexCancellation.Dispose();
            endfieldIndexCancellation = new CancellationTokenSource();
            assetsManager.Clear();
            assemblyLoader.Clear();
            exportableAssets.Clear();
            visibleAssets.Clear();
            sceneTreeView.Nodes.Clear();
            assetListView.VirtualListSize = 0;
            assetListView.Items.Clear();
            classesListView.Items.Clear();
            classesListView.Groups.Clear();
            previewPanel.BackgroundImage = Properties.Resources.preview;
            previewPanel.ContextMenuStrip = null;
            imageTexture?.Dispose();
            imageTexture = null;
            previewPanel.BackgroundImageLayout = ImageLayout.Center;
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            glControl.Visible = false;
            lastSelectedItem = null;
            sortColumn = -1;
            reverseSort = false;
            listSearch.Text = string.Empty;

            var count = filterTypeToolStripMenuItem.DropDownItems.Count;
            for (var i = 1; i < count; i++)
            {
                var item = filterTypeToolStripMenuItem.DropDownItems[i];
                if (item != endfieldVirtualPathsToolStripMenuItem && item.Tag as string != "virtual-path-filter")
                {
                    filterTypeToolStripMenuItem.DropDownItems.RemoveAt(i);
                    i--;
                    count--;
                }
            }

            FMODreset();
            StatusStripUpdate("Reset successfully !!");
        }

        private void assetListView_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && assetListView.SelectedIndices.Count > 0)
            {
                if (endfieldVirtualAssetListMode)
                {
                    tempClipboard = assetListView.HitTest(new Point(e.X, e.Y)).SubItem?.Text ?? string.Empty;
                    copyToolStripMenuItem.Visible = true;
                    contextMenuStrip1.Show(assetListView, e.X, e.Y);
                    return;
                }
                goToSceneHierarchyToolStripMenuItem.Visible = false;
                showOriginalFileToolStripMenuItem.Visible = false;
                exportAnimatorwithselectedAnimationClipMenuItem.Visible = false;

                if (assetListView.SelectedIndices.Count == 1)
                {
                    goToSceneHierarchyToolStripMenuItem.Visible = true;
                    showOriginalFileToolStripMenuItem.Visible = true;
                }
                if (assetListView.SelectedIndices.Count >= 1)
                {
                    var selectedAssets = GetSelectedAssets();
                    if (selectedAssets.Any(x => x.Type == ClassIDType.Animator) && selectedAssets.Any(x => x.Type == ClassIDType.AnimationClip))
                    {
                        exportAnimatorwithselectedAnimationClipMenuItem.Visible = true;
                    }
                }

                tempClipboard = assetListView.HitTest(new Point(e.X, e.Y)).SubItem.Text;
                contextMenuStrip1.Show(assetListView, e.X, e.Y);
            }
        }

        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetDataObject(tempClipboard);
        }

        private void copyImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (imageTexture == null)
                return;

            var data = new DataObject();
            using (var ms = new MemoryStream())
            {
                imageTexture.Bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                data.SetData("PNG", false, ms);
                data.SetImage(imageTexture.Bitmap);
                Clipboard.SetDataObject(data, true);
            }
        }

        private void exportSelectedAssetsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Convert);
        }

        private void showOriginalFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectasset = (AssetItem)assetListView.Items[assetListView.SelectedIndices[0]];
            var args = $"/select, \"{selectasset.SourceFile.originalPath ?? selectasset.SourceFile.fullName}\"";
            var pfi = new ProcessStartInfo("explorer.exe", args);
            Process.Start(pfi);
        }

        private void exportAnimatorwithAnimationClipMenuItem_Click(object sender, EventArgs e)
        {
            AssetItem animator = null;
            List<AssetItem> animationList = new List<AssetItem>();
            var selectedAssets = GetSelectedAssets();
            foreach (var assetPreloadData in selectedAssets)
            {
                if (assetPreloadData.Type == ClassIDType.Animator)
                {
                    animator = assetPreloadData;
                }
                else if (assetPreloadData.Type == ClassIDType.AnimationClip)
                {
                    animationList.Add(assetPreloadData);
                }
            }

            if (animator != null)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    var exportPath = Path.Combine(saveFolderDialog.Folder, "Animator") + Path.DirectorySeparatorChar;
                    ExportAnimatorWithAnimationClip(animator, animationList, exportPath);
                }
            }
        }

        private void exportSelectedObjectsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportObjects(false);
        }

        private void exportObjectswithAnimationClipMenuItem_Click(object sender, EventArgs e)
        {
            ExportObjects(true);
        }

        private void ExportObjects(bool animation)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    var exportPath = Path.Combine(saveFolderDialog.Folder, "GameObject") + Path.DirectorySeparatorChar;
                    List<AssetItem> animationList = null;
                    if (animation)
                    {
                        animationList = GetSelectedAssets().Where(x => x.Type == ClassIDType.AnimationClip).ToList();
                        if (animationList.Count == 0)
                        {
                            animationList = null;
                        }
                    }
                    ExportObjectsWithAnimationClip(exportPath, sceneTreeView.Nodes, animationList);
                }
            }
            else
            {
                StatusStripUpdate("No Objects available for export");
            }
        }

        private void exportSelectedObjectsmergeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportMergeObjects(false);
        }

        private void exportSelectedObjectsmergeWithAnimationClipToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportMergeObjects(true);
        }

        private void ExportMergeObjects(bool animation)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var gameObjects = new List<GameObject>();
                GetSelectedParentNode(sceneTreeView.Nodes, gameObjects);
                if (gameObjects.Count > 0)
                {
                    var saveFileDialog = new SaveFileDialog();
                    saveFileDialog.FileName = gameObjects[0].m_Name + " (merge).fbx";
                    saveFileDialog.AddExtension = false;
                    saveFileDialog.Filter = "Fbx file (*.fbx)|*.fbx";
                    saveFileDialog.InitialDirectory = saveDirectoryBackup;
                    if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
                    {
                        saveDirectoryBackup = Path.GetDirectoryName(saveFileDialog.FileName);
                        var exportPath = saveFileDialog.FileName;
                        List<AssetItem> animationList = null;
                        if (animation)
                        {
                            animationList = GetSelectedAssets().Where(x => x.Type == ClassIDType.AnimationClip).ToList();
                            if (animationList.Count == 0)
                            {
                                animationList = null;
                            }
                        }
                        ExportObjectsMergeWithAnimationClip(exportPath, gameObjects, animationList);
                    }
                }
                else
                {
                    StatusStripUpdate("No Object selected for export.");
                }
            }
        }

        private void exportSelectedNodessplitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportNodes(false);
        }

        private void exportSelectedNodessplitSelectedAnimationClipsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportNodes(true);
        }

        private void ExportNodes(bool animation)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    var exportPath = Path.Combine(saveFolderDialog.Folder, "GameObject") + Path.DirectorySeparatorChar;
                    var roots = sceneTreeView.Nodes.Cast<TreeNode>().Where(x => x.Level == 0 && x.Checked).ToList();
                    if (roots.Count == 0)
                    {
                        Logger.Info("No root nodes found selected.");
                        return;
                    }
                    List<AssetItem> animationList = null;
                    if (animation)
                    {
                        animationList = GetSelectedAssets().Where(x => x.Type == ClassIDType.AnimationClip).ToList();
                        if (animationList.Count == 0)
                        {
                            animationList = null;
                        }
                    }
                    ExportNodesWithAnimationClip(exportPath, roots, animationList);
                }
            }
        }

        private void goToSceneHierarchyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectasset = (AssetItem)assetListView.Items[assetListView.SelectedIndices[0]];
            if (selectasset.TreeNode != null)
            {
                sceneTreeView.SelectedNode = selectasset.TreeNode;
                tabControl1.SelectedTab = tabPage1;
            }
            else
            {
                MessageBox.Show("Asset does not exist in hierarchy !", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void exportAllAssetsMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.Convert);
        }

        private void exportSelectedAssetsMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Convert);
        }

        private void exportFilteredAssetsMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.Convert);
        }

        private void toolStripMenuItem4_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.Raw);
        }

        private void toolStripMenuItem5_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Raw);
        }

        private void toolStripMenuItem6_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.Raw);
        }

        private void toolStripMenuItem7_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.Dump);
        }

        private void toolStripMenuItem8_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Dump);
        }

        private void toolStripMenuItem9_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.Dump);
        }
        private void toolStripMenuItem17_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.JSON);
        }

        private void toolStripMenuItem24_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.JSON);
        }

        private void toolStripMenuItem25_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.JSON);
        }

        private void toolStripMenuItem11_Click(object sender, EventArgs e)
        {
            ExportAssetsList(ExportFilter.All);
        }

        private void toolStripMenuItem12_Click(object sender, EventArgs e)
        {
            ExportAssetsList(ExportFilter.Selected);
        }

        private void toolStripMenuItem13_Click(object sender, EventArgs e)
        {
            ExportAssetsList(ExportFilter.Filtered);
        }

        private void exportAllObjectssplitToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    var savePath = saveFolderDialog.Folder + Path.DirectorySeparatorChar;
                    ExportSplitObjects(savePath, sceneTreeView.Nodes);
                }
            }
            else
            {
                StatusStripUpdate("No Objects available for export");
            }
        }

        private List<AssetItem> GetSelectedAssets()
        {
            if (endfieldVirtualAssetListMode)
            {
                var loaded = new List<AssetItem>();
                foreach (int index in assetListView.SelectedIndices)
                {
                    if (index < 0 || index >= endfieldVisibleAssetRecords.Count)
                        continue;
                    var asset = FindLoadedEndfieldAsset(endfieldVisibleAssetRecords[index]);
                    if (asset != null && !loaded.Contains(asset))
                        loaded.Add(asset);
                }
                return loaded;
            }
            var selectedAssets = new List<AssetItem>(assetListView.SelectedIndices.Count);
            foreach (int index in assetListView.SelectedIndices)
            {
                selectedAssets.Add((AssetItem)assetListView.Items[index]);
            }

            return selectedAssets;
        }

        private async void FilterAssetList()
        {
            if (endfieldVirtualAssetListMode)
            {
                var generation = Interlocked.Increment(ref endfieldListOperationGeneration);
                var query = listSearch.Text;
                var selectedTypes = filterTypeToolStripMenuItem.DropDownItems
                    .OfType<ToolStripMenuItem>()
                    .Where(x => x.Tag as string == "endfield-asset-type" && x.Checked)
                    .Select(x => x.Text)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var showAllTypes = allToolStripMenuItem.Checked;
                Regex regex = null;
                if (!string.IsNullOrWhiteSpace(query))
                {
                    try
                    {
                        regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Invalid Regex.\n" + ex.Message);
                        StatusStripUpdate($"Invalid search pattern: {ex.Message}");
                        return;
                    }
                }

                StatusStripUpdate($"Filtering {endfieldVirtualAssetRecords.Count:N0} assets...");
                var filtered = await Task.Run(() => endfieldVirtualAssetRecords.Where(x =>
                {
                    if (!showAllTypes && !selectedTypes.Contains(x.Type))
                        return false;
                    return regex == null || regex.IsMatch(x.Name) || regex.IsMatch(x.Container) ||
                        regex.IsMatch(x.Type) || regex.IsMatch(x.PathId.ToString()) || regex.IsMatch(x.Source);
                }).ToList());
                if (generation != endfieldListOperationGeneration || !endfieldVirtualAssetListMode)
                    return;

                assetListView.BeginUpdate();
                assetListView.SelectedIndices.Clear();
                endfieldVisibleAssetRecords = filtered;
                assetListView.VirtualListSize = endfieldVisibleAssetRecords.Count;
                assetListView.EndUpdate();
                StatusStripUpdate($"Showing {endfieldVisibleAssetRecords.Count:N0} of {endfieldVirtualAssetRecords.Count:N0} virtual assets.");
                return;
            }
            assetListView.BeginUpdate();
            assetListView.SelectedIndices.Clear();
            var show = new List<ClassIDType>();
            if (!allToolStripMenuItem.Checked)
            {
                for (var i = 1; i < filterTypeToolStripMenuItem.DropDownItems.Count; i++)
                {
                    if (filterTypeToolStripMenuItem.DropDownItems[i] is ToolStripMenuItem item && item != endfieldVirtualPathsToolStripMenuItem && item.Tag as string != "virtual-path-filter" && item.Checked)
                    {
                        show.Add((ClassIDType)Enum.Parse(typeof(ClassIDType), item.Text));
                    }
                }
                visibleAssets = exportableAssets.FindAll(x => show.Contains(x.Type));
            }
            else
            {
                visibleAssets = exportableAssets;
            }
            if (Properties.Settings.Default.modelsOnly)
            {
                var models = visibleAssets.FindAll(x => x.Type == ClassIDType.Animator || x.Type == ClassIDType.GameObject);
                foreach (var model in models)
                {
                    var hasModel = model.Asset switch
                    {
                        GameObject m_GameObject => m_GameObject.HasModel(),
                        Animator m_Animator => m_Animator.m_GameObject.TryGet(out var gameObject) && gameObject.HasModel(),
                        _ => throw new NotImplementedException()
                    };
                    if (!hasModel)
                    {
                        visibleAssets.Remove(model);
                    }
                }
            }
            if (!string.IsNullOrEmpty(listSearch.Text))
            {
                try
                {
                    Regex.Match("", listSearch.Text, RegexOptions.IgnoreCase);
                }
                catch (Exception ex)
                {
                    Logger.Error("Invalid Regex.\n" + ex.Message);
                    listSearch.Text = "";
                }
                var regex = new Regex(listSearch.Text, RegexOptions.IgnoreCase);
                visibleAssets = visibleAssets.FindAll(
                    x => regex.IsMatch(x.Text) ||
                    regex.IsMatch(x.SubItems[1].Text) ||
                    regex.IsMatch(x.SubItems[3].Text));
            }
            assetListView.VirtualListSize = visibleAssets.Count;
            assetListView.EndUpdate();
        }

        private async void ExportAssets(ExportFilter type, ExportType exportType)
        {
            if (endfieldVirtualAssetListMode && exportType == ExportType.Eiem)
            {
                var records = type switch
                {
                    ExportFilter.All => endfieldVirtualAssetRecords,
                    ExportFilter.Filtered => endfieldVisibleAssetRecords,
                    ExportFilter.Selected => assetListView.SelectedIndices.Cast<int>()
                        .Where(i => i >= 0 && i < endfieldVisibleAssetRecords.Count)
                        .Select(i => endfieldVisibleAssetRecords[i]).ToList(),
                    _ => new List<VirtualAssetRecord>()
                };
                if (records.Count == 0)
                {
                    StatusStripUpdate("No virtual assets selected for EIEM export");
                    return;
                }
                var virtualFolderDialog = new OpenFolderDialog
                {
                    InitialFolder = saveDirectoryBackup,
                    Title = "Select EIEM export folder"
                };
                if (virtualFolderDialog.ShowDialog(this) != DialogResult.OK)
                    return;
                timer.Stop();
                saveDirectoryBackup = virtualFolderDialog.Folder;
                await ExportEndfieldVirtualAssets(records, virtualFolderDialog.Folder);
                return;
            }
            if (exportableAssets.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    timer.Stop();
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    List<AssetItem> toExportAssets = null;
                    switch (type)
                    {
                        case ExportFilter.All:
                            toExportAssets = exportableAssets;
                            break;
                        case ExportFilter.Selected:
                            toExportAssets = GetSelectedAssets();
                            break;
                        case ExportFilter.Filtered:
                            toExportAssets = visibleAssets;
                            break;
                    }
                    await Studio.ExportAssets(saveFolderDialog.Folder, toExportAssets, exportType, Properties.Settings.Default.openAfterExport);
                }
            }
            else
            {
                StatusStripUpdate("No exportable assets loaded");
            }
        }

        private List<VirtualAssetFile> GetCheckedEndfieldPrefabFiles()
        {
            var result = new List<VirtualAssetFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(TreeNodeCollection nodes)
            {
                foreach (TreeNode node in nodes)
                {
                    if (node.Checked && node.Tag is VirtualAssetFile file && file.IsPrefab &&
                        seen.Add(file.Container))
                        result.Add(file);
                    if (node.Nodes.Count > 0)
                        Visit(node.Nodes);
                }
            }
            Visit(sceneTreeView.Nodes);
            return result;
        }

        private async Task ExportCheckedEndfieldPrefabsAsync(bool includeResources)
        {
            var files = GetCheckedEndfieldPrefabFiles();
            if (files.Count == 0)
            {
                const string message = "No .prefab file is checked. The EIEM package exporter only accepts logical .prefab files; character-data .asset files are not Prefabs.\n\nCheck chr_0028_wulfa_postmodel.prefab, then run this command again.";
                StatusStripUpdate(message);
                MessageBox.Show(this, message, "EIEM Prefab export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var folder = new OpenFolderDialog
            {
                InitialFolder = saveDirectoryBackup,
                Title = includeResources
                    ? "Export Prefab as EIEM mod package"
                    : "Export Prefab structure"
            };
            if (folder.ShowDialog(this) != DialogResult.OK)
                return;
            saveDirectoryBackup = folder.Folder;

            StatusStripUpdate(includeResources
                ? $"Exporting {files.Count:N0} checked Prefab(s) as EIEM mod packages..."
                : $"Exporting {files.Count:N0} checked Prefab(s)...");
            var exported = 0;
            foreach (var file in files)
            {
                var root = await PreviewEndfieldPrefabAsync(file);
                if (root != null && await Task.Run(() =>
                        Exporter.ExportEndfieldPrefab(file, root, folder.Folder, includeResources,
                            endfieldVirtualAssetRecords.ToArray(), endfieldDependencyIndex)))
                    exported++;
                StatusStripUpdate($"Prefab export: {exported}/{files.Count} completed");
            }
            StatusStripUpdate($"Finished Prefab export: {exported}/{files.Count} completed");
            if (exported > 0 && Properties.Settings.Default.openAfterExport)
                Studio.OpenFolderInExplorer(folder.Folder);
        }

        private async Task ExportEndfieldVirtualAssets(List<VirtualAssetRecord> records, string output)
        {
            var exported = 0;
            var skipped = 0;
            foreach (var batch in records.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    await PreviewEndfieldAssetAsync(batch.First());
                    foreach (var record in batch)
                    {
                        var item = FindLoadedEndfieldAsset(record);
                        if (item == null || !Exporter.ExportEiemFile(item,
                                BuildEiemJsonExportPath(output, record), record.Source, record.Container))
                            skipped++;
                        else
                            exported++;
                    }
                }
                catch (Exception ex)
                {
                    skipped += batch.Count();
                    Logger.Error($"EIEM virtual export failed for {batch.Key}: {ex.Message}");
                }
                StatusStripUpdate($"EIEM export: {exported} exported, {skipped} skipped.");
            }
            StatusStripUpdate($"Finished EIEM export: {exported} exported, {skipped} skipped.");
            if (exported > 0 && Properties.Settings.Default.openAfterExport)
                Studio.OpenFolderInExplorer(output);
        }

        private void ExportAssetsList(ExportFilter type)
        {
            // XXX: Only exporting as XML for now, but would JSON(/CSV/other) be useful too?

            if (exportableAssets.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    timer.Stop();
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    List<AssetItem> toExportAssets = null;
                    switch (type)
                    {
                        case ExportFilter.All:
                            toExportAssets = exportableAssets;
                            break;
                        case ExportFilter.Selected:
                            toExportAssets = GetSelectedAssets();
                            break;
                        case ExportFilter.Filtered:
                            toExportAssets = visibleAssets;
                            break;
                    }
                    Studio.ExportAssetsList(saveFolderDialog.Folder, toExportAssets, ExportListType.XML);
                }
            }
            else
            {
                StatusStripUpdate("No exportable assets loaded");
            }
        }

        private void toolStripMenuItem15_Click(object sender, EventArgs e)
        {
            logger.ShowErrorMessage = toolStripMenuItem15.Checked;
        }
        private async void toolStripMenuItem19_DropDownOpening(object sender, EventArgs e)
        {
            if (specifyAIVersion.Enabled && await AIVersionManager.FetchVersions())
            {
                UpdateVersionList();
            }
        }

        private void miscToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            if (miscToolStripMenuItem.Enabled)
            {
                MapNameComboBox.Items.Clear();
                MapNameComboBox.Items.AddRange(AssetsHelper.GetMaps());
            }
        }

        private async void toolStripComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (specifyAIVersion.SelectedIndex == 0)
            {
                return;
            }
            if (skipContainer.Checked)
            {
                Logger.Info("Skip container is enabled, aborting...");
                return;
            }
            optionsToolStripMenuItem.DropDown.Visible = false;
            var version = specifyAIVersion.SelectedItem.ToString();

            if (version.Contains(' '))
            {
                version = version.Split(' ')[0];
            }

            Logger.Info($"Loading AI v{version}");
            InvokeUpdate(specifyAIVersion, false);
            var path = await AIVersionManager.FetchAI(version);
            await Task.Run(() => ResourceIndex.FromFile(path));
            UpdateContainers();
            UpdateVersionList();
            InvokeUpdate(specifyAIVersion, true);
        }

        private void UpdateVersionList()
        {
            var selectedIndex = specifyAIVersion.SelectedIndex;
            specifyAIVersion.Items.Clear();
            specifyAIVersion.Items.Add("None");

            var versions = AIVersionManager.GetVersions();
            foreach (var version in versions)
            {
                specifyAIVersion.Items.Add(version.Item1 + (version.Item2 ? " (cached)" : ""));
            }

            specifyAIVersion.SelectedIndexChanged -= new EventHandler(toolStripComboBox1_SelectedIndexChanged);
            specifyAIVersion.SelectedIndex = selectedIndex;
            specifyAIVersion.SelectedIndexChanged += new EventHandler(toolStripComboBox1_SelectedIndexChanged);
        }

        private void UpdateContainers()
        {
            if (exportableAssets.Count > 0)
            {
                Logger.Info("Updating Containers...");
                assetListView.BeginUpdate();
                foreach (var asset in exportableAssets)
                {
                    if (int.TryParse(asset.Container, out var value))
                    {
                        var last = unchecked((uint)value);
                        var name = Path.GetFileNameWithoutExtension(asset.SourceFile.originalPath);
                        if (uint.TryParse(name, out var id))
                        {
                            var path = ResourceIndex.GetContainer(id, last);
                            if (!string.IsNullOrEmpty(path))
                            {
                                asset.Container = path;
                                asset.SubItems[1].Text = path;
                                if (asset.Type == ClassIDType.MiHoYoBinData)
                                {
                                    asset.Text = Path.GetFileNameWithoutExtension(path);
                                }
                            }
                        }
                    }
                }
                assetListView.EndUpdate();
                Logger.Info("Updated !!");
            }
        }

        private void InvokeUpdate(ToolStripItem item, bool value)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => { item.Enabled = value; }));
            }
            else
            {
                item.Enabled = value;
            }
        }

        private void tabControl2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl2.SelectedIndex == 1 && lastSelectedItem != null)
            {
                dumpTextBox.Text = DumpAsset(lastSelectedItem.Asset);
            }
        }
        private void enableResolveDependencies_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.enableResolveDependencies = enableResolveDependencies.Checked;
            Properties.Settings.Default.Save();

            assetsManager.ResolveDependencies = enableResolveDependencies.Checked;
        }
        private void allowDuplicates_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.allowDuplicates = allowDuplicates.Checked;
            Properties.Settings.Default.Save();
        }
        private void UseBundleContainerNameToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.useBundleContainerName = useBundleContainerNameToolStripMenuItem.Checked;
            Properties.Settings.Default.Save();
        }
        private void skipContainer_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.skipContainer = skipContainer.Checked;
            Properties.Settings.Default.Save();

            SkipContainer = skipContainer.Checked;
        }
        private void assetMapTypeMenuItem_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            var assetMapType = Properties.Settings.Default.assetMapType;
            if (e.ClickedItem is ToolStripMenuItem item)
            {
                if (item.Checked)
                {
                    assetMapType -= (int)item.Tag;
                }
                else
                {
                    assetMapType += (int)item.Tag;
                }

                Properties.Settings.Default.assetMapType = assetMapType;
                Properties.Settings.Default.Save();
            }

        }
        private void modelsOnly_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.modelsOnly = modelsOnly.Checked;
            Properties.Settings.Default.Save();

            if (visibleAssets.Count > 0)
            {
                FilterAssetList();
            }
        }
        private void enableModelPreview_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.enableModelPreview = enableModelPreview.Checked;
            Properties.Settings.Default.Save();
        }

        public void updateGame(Game game)
        {
            int index = GameManager.GetGameIndex(game);
            Properties.Settings.Default.selectedGame = index;
            Properties.Settings.Default.Save();
            ResetForm();
            Studio.Game = game;
            Logger.Info($"Target Game is {Studio.Game.Name}");
            if (Studio.Game.IsUnityCN() && Studio.Game is UnityCNGame unityCnGame)
            {
                UnityCNManager.SetKey(unityCnGame.Key);
            }
            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            assetsManager.Game = Studio.Game;
        }

        public void updateGame(GameType mapGame)
        {
            Game game = GameManager.GetGameByType(mapGame);
            int index = GameManager.GetGameIndex(game);

            Properties.Settings.Default.selectedGame = index;
            Properties.Settings.Default.Save();

            ResetForm();

            Studio.Game = game;
            Logger.Info($"Target Game is {Studio.Game.Name}");

            if (Studio.Game.IsUnityCN() && Studio.Game is UnityCNGame unityCnGame)
            {
                UnityCNManager.SetKey(unityCnGame.Key);
            }

            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            assetsManager.Game = Studio.Game;
        }

        private async void specifyNameComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            miscToolStripMenuItem.DropDown.Visible = false;
            InvokeUpdate(miscToolStripMenuItem, false);

            ResetForm();

            var name = MapNameComboBox.SelectedItem.ToString();
            await Task.Run(() =>
            {
                if (AssetsHelper.LoadCABMapInternal(name))
                {
                    Properties.Settings.Default.selectedCABMapName = name;
                    Properties.Settings.Default.Save();
                }
            });

            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            assetsManager.Game = Studio.Game;

            InvokeUpdate(miscToolStripMenuItem, true);
        }

        private async void buildMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            miscToolStripMenuItem.DropDown.Visible = false;
            InvokeUpdate(miscToolStripMenuItem, false);

            var input = MapNameComboBox.Text;
            var selectedText = MapNameComboBox.SelectedText;
            var name = "";

            if (!string.IsNullOrEmpty(selectedText))
            {
                name = selectedText;
            }
            else if (!string.IsNullOrEmpty(input))
            {
                if (input.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
                {
                    Logger.Warning("Name has invalid characters !!");
                    InvokeUpdate(miscToolStripMenuItem, true);
                    return;
                }

                name = input;
            }
            else
            {
                Logger.Error("Map name is empty, please enter any name in ComboBox above");
                InvokeUpdate(miscToolStripMenuItem, true);
                return;
            }

            if (File.Exists(Path.Combine(AssetsHelper.MapName, $"{name}.bin")))
            {
                var acceptOverride = MessageBox.Show("Map already exist, Do you want to override it ?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (acceptOverride != DialogResult.Yes)
                {
                    InvokeUpdate(miscToolStripMenuItem, true);
                    return;
                }
            }

            var version = specifyUnityVersion.Text;
            var openFolderDialog = new OpenFolderDialog();
            openFolderDialog.Title = "Select Game Folder";
            if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                Logger.Info("Scanning for files...");
                var files = Directory.GetFiles(openFolderDialog.Folder, "*.*", SearchOption.AllDirectories).ToArray();
                Logger.Info($"Found {files.Length} files");
                AssetsHelper.SetUnityVersion(version);
                await Task.Run(() => AssetsHelper.BuildCABMap(files, name, openFolderDialog.Folder, Studio.Game));
            }
            InvokeUpdate(miscToolStripMenuItem, true);
        }

        private async void buildBothToolStripMenuItem_Click(object sender, EventArgs e)
        {
            miscToolStripMenuItem.DropDown.Visible = false;
            InvokeUpdate(miscToolStripMenuItem, false);

            var input = toolStripTextBox1.Text;
            var selectedText = toolStripTextBox1.SelectedText;
            var exportListType = (ExportListType)assetMapTypeMenuItem.DropDownItems.Cast<ToolStripMenuItem>().Select(x => x.Checked ? (int)x.Tag : 0).Sum();
            var name = "";

            if (!string.IsNullOrEmpty(selectedText))
            {
                name = selectedText;
            }
            else if (!string.IsNullOrEmpty(input))
            {
                if (input.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
                {
                    Logger.Warning("Name has invalid characters !!");
                    InvokeUpdate(miscToolStripMenuItem, true);
                    return;
                }

                name = input;
            }
            else
            {
                Logger.Error("Map name is empty, please enter any name in ComboBox above");
                InvokeUpdate(miscToolStripMenuItem, true);
                return;
            }

            if (File.Exists(Path.Combine(AssetsHelper.MapName, $"{name}.bin")))
            {
                var acceptOverride = MessageBox.Show("Map already exist, Do you want to override it ?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (acceptOverride != DialogResult.Yes)
                {
                    InvokeUpdate(miscToolStripMenuItem, true);
                    return;
                }
            }

            var version = specifyUnityVersion.Text;
            var openFolderDialog = new OpenFolderDialog();
            openFolderDialog.Title = "Select Game Folder";
            if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                Logger.Info("Scanning for files...");
                var files = Directory.GetFiles(openFolderDialog.Folder, "*.*", SearchOption.AllDirectories).ToArray();
                Logger.Info($"Found {files.Length} files");

                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                saveFolderDialog.Title = "Select Output Folder";
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    AssetsHelper.SetUnityVersion(version);
                    await Task.Run(() => AssetsHelper.BuildBoth(files, name, openFolderDialog.Folder, Studio.Game, saveFolderDialog.Folder, exportListType));
                }
            }
            InvokeUpdate(miscToolStripMenuItem, true);
        }

        private void clearMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            miscToolStripMenuItem.DropDown.Visible = false;
            InvokeUpdate(miscToolStripMenuItem, false);

            var acceptDelete = MessageBox.Show("Map will be deleted, this can't be undone, continue ?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (acceptDelete != DialogResult.Yes)
            {
                InvokeUpdate(miscToolStripMenuItem, true);
                return;
            }

            var name = MapNameComboBox.Text.ToString();
            var path = Path.Combine(AssetsHelper.MapName, $"{name}.bin");
            if (File.Exists(path))
            {
                File.Delete(path);
                Logger.Info($"{name} deleted successfully !!");
                MapNameComboBox.SelectedIndexChanged -= new EventHandler(specifyNameComboBox_SelectedIndexChanged);
                MapNameComboBox.SelectedIndex = 0;
                MapNameComboBox.SelectedIndexChanged += new EventHandler(specifyNameComboBox_SelectedIndexChanged);
            }

            InvokeUpdate(miscToolStripMenuItem, true);
        }

        private void resetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ResetForm();
            AssetsHelper.Clear();
            assetBrowser?.Clear();
            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            assetsManager.Game = Studio.Game;
        }

        private void enableConsole_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.enableConsole = enableConsole.Checked;
            Properties.Settings.Default.Save();

            var handle = ConsoleHelper.GetConsoleWindow();
            if (enableConsole.Checked)
            {
                Logger.Default = new ConsoleLogger();
                ConsoleHelper.ShowWindow(handle, ConsoleHelper.SW_SHOW);
            }
            else
            {
                Logger.Default = logger;
                ConsoleHelper.ShowWindow(handle, ConsoleHelper.SW_HIDE);
            }
        }

        private void enableFileLogging_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.enableFileLogging = enableFileLogging.Checked;
            Properties.Settings.Default.Save();

            Logger.FileLogging = enableFileLogging.Checked;
        }

        private void LoggerEventMenuItem_Click(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem clickedItem) return;
            if (clickedItem.Tag is not LoggerEvent clickedEvent) return;

            var currentFlags = Logger.Flags;

            if (clickedItem.Checked)
                currentFlags |= clickedEvent;
            else
                currentFlags &= ~clickedEvent;

            Logger.Flags = currentFlags;

            Properties.Settings.Default.loggerEventType = (int)currentFlags;
            Properties.Settings.Default.Save();

            Logger.Info($"Logger events updated: {clickedItem.Tag} set to {clickedItem.Checked}");
        }

        private void loggedEventsMenuItem_DropDownClosing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        }

        private void loggedEventsMenuItem_DropDownClosed(object sender, EventArgs e)
        {
            Properties.Settings.Default.loggerEventType = loggedEventsMenuItem.DropDownItems.Cast<ToolStripMenuItem>().Select(x => x.Checked ? (int)x.Tag : 0).Sum();
            Properties.Settings.Default.Save();

            Logger.Flags = (LoggerEvent)Properties.Settings.Default.loggerEventType;
        }

        private void abortStripMenuItem_Click(object sender, EventArgs e)
        {
            Logger.Info("Aborting....");
            endfieldIndexCancellation.Cancel();
            assetsManager.tokenSource.Cancel();
            AssetsHelper.tokenSource.Cancel();
        }

        private async void loadAIToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (skipContainer.Checked)
            {
                Logger.Info("Skip container is enabled, aborting...");
                return;
            }
            miscToolStripMenuItem.DropDown.Visible = false;

            var openFileDialog = new OpenFileDialog() { Multiselect = false, Filter = "Asset Index JSON File|*.json" };
            if (openFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                var path = openFileDialog.FileName;
                Logger.Info($"Loading AI...");
                InvokeUpdate(loadAIToolStripMenuItem, false);
                await Task.Run(() => ResourceIndex.FromFile(path));
                UpdateContainers();
                InvokeUpdate(loadAIToolStripMenuItem, true);
            }
        }

        private async void loadCABMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            miscToolStripMenuItem.DropDown.Visible = false;

            var openFileDialog = new OpenFileDialog() { Multiselect = false, Filter = "CABMap File|*.bin" };
            if (openFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                var path = openFileDialog.FileName;
                InvokeUpdate(loadCABMapToolStripMenuItem, false);
                await Task.Run(() => AssetsHelper.LoadCABMap(path));
                InvokeUpdate(loadCABMapToolStripMenuItem, true);
            }
        }

        private void clearConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Console.Clear();
        }

        private async void buildAssetMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            miscToolStripMenuItem.DropDown.Visible = false;
            InvokeUpdate(miscToolStripMenuItem, false);

            var name = "assets_map";
            var saveDirectory = saveDirectoryBackup;

            var saveFileDialog = new SaveFileDialog()
            {
                Filter = "Map file (*.map)|*.map",
                DefaultExt = "map",
                Title = "Select Output File (format will auto adjust according to what you selected)",
                InitialDirectory = saveDirectory,
            };

            if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                saveDirectory = Path.GetDirectoryName(saveFileDialog.FileName);
                var input = Path.GetFileNameWithoutExtension(saveFileDialog.FileName);

                var exportListType = (ExportListType)assetMapTypeMenuItem.DropDownItems.Cast<ToolStripMenuItem>().Select(x => x.Checked ? (int)x.Tag : 0).Sum();

                if (!string.IsNullOrEmpty(input))
                {
                    if (input.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
                    {
                        Logger.Warning("Name has invalid characters !!");
                        InvokeUpdate(miscToolStripMenuItem, true);
                        return;
                    }

                    name = input;
                }

                var version = specifyUnityVersion.Text;
                var openFolderDialog = new OpenFolderDialog();
                openFolderDialog.Title = $"Select Game Folder";
                if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    Logger.Info("Scanning for files...");
                    var files = Directory.GetFiles(openFolderDialog.Folder, "*.*", SearchOption.AllDirectories).ToArray();
                    Logger.Info($"Found {files.Length} files");

                    AssetsHelper.SetUnityVersion(version);
                    await Task.Run(() => AssetsHelper.BuildAssetMap(files, name, Studio.Game, saveDirectory, exportListType));
                }
                InvokeUpdate(miscToolStripMenuItem, true);
            }
            InvokeUpdate(miscToolStripMenuItem, true);
        }

        private void loadAssetMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            assetBrowser = new AssetBrowser(this);
            assetBrowser.Show();
        }

        #region FMOD
        private void FMODinit()
        {
            FMODreset();

            var result = FMOD.Factory.System_Create(out system);
            if (ERRCHECK(result)) { return; }

            result = system.getVersion(out var version);
            ERRCHECK(result);
            if (version < FMOD.VERSION.number)
            {
                Logger.Error($"Error!  You are using an old version of FMOD {version:X}.  This program requires {FMOD.VERSION.number:X}.");
                System.Windows.Forms.Application.Exit();
            }

            result = system.init(2, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
            if (ERRCHECK(result)) { return; }

            result = system.getMasterSoundGroup(out masterSoundGroup);
            if (ERRCHECK(result)) { return; }

            result = masterSoundGroup.setVolume(FMODVolume);
            if (ERRCHECK(result)) { return; }
        }

        private void FMODreset()
        {
            timer.Stop();
            FMODprogressBar.Value = 0;
            FMODtimerLabel.Text = "0:00.0 / 0:00.0";
            FMODstatusLabel.Text = "Stopped";
            FMODinfoLabel.Text = "";

            if (sound != null && sound.isValid())
            {
                var result = sound.release();
                ERRCHECK(result);
                sound = null;
            }
        }

        private void FMODplayButton_Click(object sender, EventArgs e)
        {
            if (sound != null && channel != null)
            {
                timer.Start();
                var result = channel.isPlaying(out var playing);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    result = channel.stop();
                    if (ERRCHECK(result)) { return; }

                    result = system.playSound(sound, null, false, out channel);
                    if (ERRCHECK(result)) { return; }

                    FMODpauseButton.Text = "Pause";
                }
                else
                {
                    result = system.playSound(sound, null, false, out channel);
                    if (ERRCHECK(result)) { return; }
                    FMODstatusLabel.Text = "Playing";

                    if (FMODprogressBar.Value > 0)
                    {
                        uint newms = FMODlenms / 1000 * (uint)FMODprogressBar.Value;

                        result = channel.setPosition(newms, FMOD.TIMEUNIT.MS);
                        if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                        {
                            if (ERRCHECK(result)) { return; }
                        }

                    }
                }
            }
        }

        private void FMODpauseButton_Click(object sender, EventArgs e)
        {
            if (sound != null && channel != null)
            {
                var result = channel.isPlaying(out var playing);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    result = channel.getPaused(out var paused);
                    if (ERRCHECK(result)) { return; }
                    result = channel.setPaused(!paused);
                    if (ERRCHECK(result)) { return; }

                    if (paused)
                    {
                        FMODstatusLabel.Text = "Playing";
                        FMODpauseButton.Text = "Pause";
                        timer.Start();
                    }
                    else
                    {
                        FMODstatusLabel.Text = "Paused";
                        FMODpauseButton.Text = "Resume";
                        timer.Stop();
                    }
                }
            }
        }

        private void FMODstopButton_Click(object sender, EventArgs e)
        {
            if (channel != null)
            {
                var result = channel.isPlaying(out var playing);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    result = channel.stop();
                    if (ERRCHECK(result)) { return; }
                    //channel = null;
                    //don't FMODreset, it will nullify the sound
                    timer.Stop();
                    FMODprogressBar.Value = 0;
                    FMODtimerLabel.Text = "0:00.0 / 0:00.0";
                    FMODstatusLabel.Text = "Stopped";
                    FMODpauseButton.Text = "Pause";
                }
            }
        }

        private void FMODloopButton_CheckedChanged(object sender, EventArgs e)
        {
            FMOD.RESULT result;

            loopMode = FMODloopButton.Checked ? FMOD.MODE.LOOP_NORMAL : FMOD.MODE.LOOP_OFF;

            if (sound != null)
            {
                result = sound.setMode(loopMode);
                if (ERRCHECK(result)) { return; }
            }

            if (channel != null)
            {
                result = channel.isPlaying(out var playing);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }

                result = channel.getPaused(out var paused);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing || paused)
                {
                    result = channel.setMode(loopMode);
                    if (ERRCHECK(result)) { return; }
                }
            }
        }

        private void FMODvolumeBar_ValueChanged(object sender, EventArgs e)
        {
            FMODVolume = Convert.ToSingle(FMODvolumeBar.Value) / 10;

            var result = masterSoundGroup.setVolume(FMODVolume);
            if (ERRCHECK(result)) { return; }
        }

        private void FMODprogressBar_Scroll(object sender, EventArgs e)
        {
            if (channel != null)
            {
                uint newms = FMODlenms / 1000 * (uint)FMODprogressBar.Value;
                FMODtimerLabel.Text = $"{newms / 1000 / 60}:{newms / 1000 % 60}.{newms / 10 % 100}/{FMODlenms / 1000 / 60}:{FMODlenms / 1000 % 60}.{FMODlenms / 10 % 100}";
            }
        }

        private void FMODprogressBar_MouseDown(object sender, MouseEventArgs e)
        {
            timer.Stop();
        }

        private void FMODprogressBar_MouseUp(object sender, MouseEventArgs e)
        {
            if (channel != null)
            {
                uint newms = FMODlenms / 1000 * (uint)FMODprogressBar.Value;

                var result = channel.setPosition(newms, FMOD.TIMEUNIT.MS);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }


                result = channel.isPlaying(out var playing);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing) { timer.Start(); }
            }
        }

        private void timer_Tick(object sender, EventArgs e)
        {
            uint ms = 0;
            bool playing = false;
            bool paused = false;

            if (channel != null)
            {
                var result = channel.getPosition(out ms, FMOD.TIMEUNIT.MS);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    ERRCHECK(result);
                }

                result = channel.isPlaying(out playing);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    ERRCHECK(result);
                }

                result = channel.getPaused(out paused);
                if ((result != FMOD.RESULT.OK) && (result != FMOD.RESULT.ERR_INVALID_HANDLE))
                {
                    ERRCHECK(result);
                }
            }

            FMODtimerLabel.Text = $"{ms / 1000 / 60}:{ms / 1000 % 60}.{ms / 10 % 100} / {FMODlenms / 1000 / 60}:{FMODlenms / 1000 % 60}.{FMODlenms / 10 % 100}";
            FMODprogressBar.Value = (int)(ms * 1000 / FMODlenms);
            FMODstatusLabel.Text = paused ? "Paused " : playing ? "Playing" : "Stopped";

            if (system != null && channel != null)
            {
                system.update();
            }
        }

        private bool ERRCHECK(FMOD.RESULT result)
        {
            if (result != FMOD.RESULT.OK)
            {
                FMODreset();
                StatusStripUpdate($"FMOD error! {result} - {FMOD.Error.String(result)}");
                return true;
            }
            return false;
        }
        #endregion

        #region GLControl
        private void InitOpenTK()
        {
            ChangeGLSize(glControl.Size);
            GL.ClearColor(System.Drawing.Color.CadetBlue);
            pgmID = GL.CreateProgram();
            LoadShader("vs", ShaderType.VertexShader, pgmID, out int vsID);
            LoadShader("fs", ShaderType.FragmentShader, pgmID, out int fsID);
            GL.LinkProgram(pgmID);

            pgmColorID = GL.CreateProgram();
            LoadShader("vs", ShaderType.VertexShader, pgmColorID, out vsID);
            LoadShader("fsColor", ShaderType.FragmentShader, pgmColorID, out fsID);
            GL.LinkProgram(pgmColorID);

            pgmBlackID = GL.CreateProgram();
            LoadShader("vs", ShaderType.VertexShader, pgmBlackID, out vsID);
            LoadShader("fsBlack", ShaderType.FragmentShader, pgmBlackID, out fsID);
            GL.LinkProgram(pgmBlackID);

            attributeVertexPosition = GL.GetAttribLocation(pgmID, "vertexPosition");
            attributeNormalDirection = GL.GetAttribLocation(pgmID, "normalDirection");
            attributeVertexColor = GL.GetAttribLocation(pgmColorID, "vertexColor");
            uniformModelMatrix = GL.GetUniformLocation(pgmID, "modelMatrix");
            uniformViewMatrix = GL.GetUniformLocation(pgmID, "viewMatrix");
            uniformProjMatrix = GL.GetUniformLocation(pgmID, "projMatrix");
        }

        private static void LoadShader(string filename, ShaderType type, int program, out int address)
        {
            address = GL.CreateShader(type);
            var str = (string)Properties.Resources.ResourceManager.GetObject(filename);
            GL.ShaderSource(address, str);
            GL.CompileShader(address);
            GL.AttachShader(program, address);
            GL.DeleteShader(address);
        }

        private static void CreateVBO(out int vboAddress, OpenTK.Mathematics.Vector3[] data, int address)
        {
            GL.GenBuffers(1, out vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer,
                                    (IntPtr)(data.Length * OpenTK.Mathematics.Vector3.SizeInBytes),
                                    data,
                                    BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private static void CreateVBO(out int vboAddress, OpenTK.Mathematics.Vector4[] data, int address)
        {
            GL.GenBuffers(1, out vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer,
                                    (IntPtr)(data.Length * OpenTK.Mathematics.Vector4.SizeInBytes),
                                    data,
                                    BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 4, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private static void CreateVBO(out int vboAddress, Matrix4 data, int address)
        {
            GL.GenBuffers(1, out vboAddress);
            GL.UniformMatrix4(address, false, ref data);
        }

        private static void CreateEBO(out int address, int[] data)
        {
            GL.GenBuffers(1, out address);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, address);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                            (IntPtr)(data.Length * sizeof(int)),
                            data,
                            BufferUsageHint.StaticDraw);
        }

        private void CreateVAO()
        {
            GL.DeleteVertexArray(vao);
            GL.GenVertexArrays(1, out vao);
            GL.BindVertexArray(vao);
            CreateVBO(out var vboPositions, vertexData, attributeVertexPosition);
            if (normalMode == 0)
            {
                CreateVBO(out var vboNormals, normal2Data, attributeNormalDirection);
            }
            else
            {
                if (normalData != null)
                    CreateVBO(out var vboNormals, normalData, attributeNormalDirection);
            }
            CreateVBO(out var vboColors, colorData, attributeVertexColor);
            CreateVBO(out var vboModelMatrix, modelMatrixData, uniformModelMatrix);
            CreateVBO(out var vboViewMatrix, viewMatrixData, uniformViewMatrix);
            CreateVBO(out var vboProjMatrix, projMatrixData, uniformProjMatrix);
            CreateEBO(out var eboElements, indiceData);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        private void ChangeGLSize(Size size)
        {
            GL.Viewport(0, 0, size.Width, size.Height);

            if (size.Width <= size.Height)
            {
                float k = 1.0f * size.Width / size.Height;
                projMatrixData = Matrix4.CreateScale(1, k, 1);
            }
            else
            {
                float k = 1.0f * size.Height / size.Width;
                projMatrixData = Matrix4.CreateScale(k, 1, 1);
            }
        }

        private void glControl_Load(object sender, EventArgs e)
        {
            InitOpenTK();
            glControlLoaded = true;
        }

        private void glControl_Paint(object sender, PaintEventArgs e)
        {
            glControl.MakeCurrent();
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.BindVertexArray(vao);
            if (wireFrameMode == 0 || wireFrameMode == 2)
            {
                GL.UseProgram(shadeMode == 0 ? pgmID : pgmColorID);
                GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                GL.DrawElements(PrimitiveType.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
            }
            //Wireframe
            if (wireFrameMode == 1 || wireFrameMode == 2)
            {
                GL.Enable(EnableCap.PolygonOffsetLine);
                GL.PolygonOffset(-1, -1);
                GL.UseProgram(pgmBlackID);
                GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                GL.DrawElements(PrimitiveType.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
                GL.Disable(EnableCap.PolygonOffsetLine);
            }
            GL.BindVertexArray(0);
            GL.Flush();
            glControl.SwapBuffers();
        }

        private void glControl_MouseWheel(object sender, MouseEventArgs e)
        {
            if (glControl.Visible)
            {
                viewMatrixData *= Matrix4.CreateScale(1 + e.Delta / 1000f);
                glControl.Invalidate();
            }
        }

        private void glControl_MouseDown(object sender, MouseEventArgs e)
        {
            mdx = e.X;
            mdy = e.Y;
            if (e.Button == MouseButtons.Left)
            {
                lmdown = true;
            }
            if (e.Button == MouseButtons.Right)
            {
                rmdown = true;
            }
        }

        private void glControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (lmdown || rmdown)
            {
                float dx = mdx - e.X;
                float dy = mdy - e.Y;
                mdx = e.X;
                mdy = e.Y;
                if (lmdown)
                {
                    dx *= 0.01f;
                    dy *= 0.01f;
                    viewMatrixData *= Matrix4.CreateRotationX(dy);
                    viewMatrixData *= Matrix4.CreateRotationY(dx);
                }
                if (rmdown)
                {
                    dx *= 0.003f;
                    dy *= 0.003f;
                    viewMatrixData *= Matrix4.CreateTranslation(-dx, dy, 0);
                }
                glControl.Invalidate();
            }
        }

        private void glControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lmdown = false;
            }
            if (e.Button == MouseButtons.Right)
            {
                rmdown = false;
            }
        }
        #endregion

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void modelsObjectsExportAll_Click(object sender, EventArgs e)
        {
            exportAllObjectssplitToolStripMenuItem1_Click(sender, e);
        }

        private void modelsObjectsExportSelected_Click(object sender, EventArgs e)
        {
            bool includeAnimationClips = modelsIncludeAnimationClips.Checked;
            bool mergeObjects = modelsMerge.Checked;

            switch ((includeAnimationClips, mergeObjects))
            {
                case (false, false):
                    exportSelectedObjectsToolStripMenuItem_Click(sender, e);
                    break;
                case (false, true):
                    exportSelectedObjectsmergeToolStripMenuItem_Click(sender, e);
                    break;
                case (true, false):
                    exportObjectswithAnimationClipMenuItem_Click(sender, e);
                    break;
                case (true, true):
                    exportSelectedObjectsmergeWithAnimationClipToolStripMenuItem_Click(sender, e);
                    break;
            }
        }

        private void modelsNodesExportSelected_Click(object sender, EventArgs e)
        {
            bool includeAnimationClips = modelsIncludeAnimationClips.Checked;

            switch (includeAnimationClips)
            {
                case true:
                    exportSelectedNodessplitSelectedAnimationClipsToolStripMenuItem_Click(sender, e);
                    break;
                case false:
                    exportSelectedNodessplitToolStripMenuItem_Click(sender, e);
                    break;
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            aboutForm = new AboutForm();
            aboutForm.ShowDialog();
        }

        private void gameSelectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            gameSelector = new GameSelector(this);
            gameSelector.ShowDialog();
        }

        private void editUnityCNKeysToolStripMenuItem_Click(object sender, EventArgs e)
        {
            unityCNEdit = new UnityCNEdit();
            unityCNEdit.ShowDialog();
        }
    }
}
