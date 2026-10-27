using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AnimeStudio.GUI;

partial class MainForm
{
    private void SetEndfieldResourceRoot()
    {
        var current = EndfieldSharedResources.ResourceRoot;
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择编译器和资源工具包的存储位置。AnimeStudio 索引保留在单独选择的工作区。",
            SelectedPath = Directory.Exists(current) ? current : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        EndfieldSharedResources.SetResourceRoot(dialog.SelectedPath);
        MessageBox.Show(this, "EFF 工具将保存在：\n" + Path.Combine(dialog.SelectedPath, "tools"),
            "EFF 工具存储位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task PrepareEndfieldSharedToolsAsync()
    {
        var root = EndfieldSharedResources.ResourceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            SetEndfieldResourceRoot();
            root = EndfieldSharedResources.ResourceRoot;
            if (string.IsNullOrWhiteSpace(root)) return;
        }
        try
        {
            var progress = new Progress<string>(StatusStripUpdate);
            var compiler = await Task.Run(() => EndfieldSharedNativeTools.EnsureInstalledAsync(
                root, progress, endfieldToolsCancellation.Token), endfieldToolsCancellation.Token);
            StatusStripUpdate("EFF tools ready: " + compiler);
        }
        catch (OperationCanceledException) when (endfieldToolsCancellation.IsCancellationRequested)
        { StatusStripUpdate("EFF tool preparation canceled."); }
        catch (Exception error)
        {
            StatusStripUpdate("EFF tools: " + error.Message);
            MessageBox.Show(this, error.Message, "EFF shared tools", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task<string> SelectEndfieldWorkspace(EndfieldVfsArchive archive)
    {
        var token = endfieldIndexCancellation.Token;
        var shared = await Task.Run(() => EndfieldSharedIndexes.FindCurrent(archive, token,
            EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive))), token);
        token.ThrowIfCancellationRequested();
        var dialog = new OpenFolderDialog
        {
            Title = "选择资源工作区（已有工作区可直接复用）",
            InitialFolder = shared ?? SuggestEndfieldWorkspace(EndfieldSharedIndexes.Game(archive))
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? Path.GetFullPath(dialog.Folder) : null;
    }

    private string SuggestEndfieldWorkspace(string game)
    {
        try
        {
            var selected = EndfieldSharedIndexes.WorkspacePath(EndfieldSharedResources.IndexWorkspace);
            var workspace = Directory.Exists(selected) ? selected : EndfieldSharedIndexes.Workspaces(game).FirstOrDefault(Directory.Exists);
            if (workspace == null && endfieldVfsArchive != null &&
                string.Equals(EndfieldSharedIndexes.Game(endfieldVfsArchive), game, StringComparison.OrdinalIgnoreCase))
                workspace = endfieldWorkspace;
            if (workspace == null)
            {
                var suggested = EndfieldSharedIndexes.DefaultWorkspace(game);
                if (!string.IsNullOrWhiteSpace(suggested))
                {
                    Directory.CreateDirectory(suggested);
                    workspace = suggested;
                }
            }
            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
                workspace = Directory.Exists(game) ? game : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return workspace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Environment.CurrentDirectory; }
    }

    private async Task<string> PrepareEndfieldIndex(EndfieldVfsArchive archive, string workspace, Game game)
    {
        var token = endfieldIndexCancellation.Token;
        StatusStripUpdate("正在检查已有索引，File > Abort 可取消…");
        if (await Task.Run(() => EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out _, token), token))
        {
            EndfieldSharedIndexes.TryRemember(archive, workspace, token);
            return EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);
        }

        string source = EndfieldIndexUpdates.DefaultSource;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var dialog = new EndfieldIndexSourceDialog(source);
            if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
            source = dialog.Source;
            try
            {
                if (dialog.Choice == EndfieldIndexSource.Build)
                {
                    StatusStripUpdate("正在本地构建资源索引，File > Abort 可安全取消；再次打开同一工作区可继续。");
                    return await EndfieldVfsAssetIndexBuilder.BuildAsync(archive, workspace, game,
                        new Progress<EndfieldIndexProgress>(UpdateEndfieldIndexProgress), token);
                }
                string package;
                if (dialog.Choice == EndfieldIndexSource.Import)
                {
                    using var file = new OpenFileDialog { Title = "导入完整资源索引", Filter = "EFF 完整索引|*.effindex.gz|所有文件|*.*" };
                    if (file.ShowDialog(this) != DialogResult.OK) continue;
                    package = file.FileName;
                }
                else
                {
                    StatusStripUpdate("正在查找匹配当前游戏资源的已发布索引，File > Abort 可取消…");
                    var catalog = await EndfieldIndexUpdates.Check(source, token);
                    var content = await Task.Run(() => EndfieldPortableIndex.ContentIdentity(archive), token);
                    var item = catalog?.Indexes.LastOrDefault(i => i.Release.GameContent == content);
                    if (item == null) throw new IOException("发布源中没有匹配当前游戏资源的完整索引。可以选择本地构建，或导入其他已下载的索引包。");
                    package = EndfieldIndexUpdates.CachedPackage(item, workspace);
                    await EndfieldIndexUpdates.Download(item, package, new Progress<string>(StatusStripUpdate), token);
                }
                StatusStripUpdate("正在校验并导入索引，File > Abort 可取消…");
                return await Task.Run(() => EndfieldPortableIndex.Import(archive, workspace, package, token), token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { MessageBox.Show(this, "索引下载检查超时，请重试或选择本地构建／导入。", "资源索引", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception error) when (error is not OperationCanceledException)
            { MessageBox.Show(this, error.Message, "资源索引未准备完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}

internal enum EndfieldIndexSource { Download, Build, Import }

internal sealed class EndfieldIndexSourceDialog : Form
{
    private readonly TextBox source;
    internal string Source => source.Text.Trim();
    internal EndfieldIndexSource Choice { get; private set; }
    internal EndfieldIndexSourceDialog(string catalog)
    {
        Text = "准备终末地资源索引";StartPosition = FormStartPosition.CenterParent;ClientSize = new Size(600, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;MaximizeBox = false;MinimizeBox = false;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
        layout.Controls.Add(new Label { Text = "当前工作区需要资源索引，请选择准备方式。", AutoSize = true });
        layout.Controls.Add(new Label { Text = "下载可省去全游戏索引构建；模型、贴图和特效仍从本机游戏按需提取。", AutoSize = true });
        layout.Controls.Add(new Label { Text = "索引发布源（GitHub Release API 或索引目录地址）", AutoSize = true });
        source = new TextBox { Text = catalog, Dock = DockStyle.Fill };layout.Controls.Add(source);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        foreach (var option in new[] { ("下载预建索引", EndfieldIndexSource.Download), ("本地构建", EndfieldIndexSource.Build), ("导入索引包…", EndfieldIndexSource.Import) })
        {
            var button = new Button { Text = option.Item1, AutoSize = true, Height = 34 };
            button.Click += (_, _) => { Choice = option.Item2;DialogResult = DialogResult.OK;Close(); };buttons.Controls.Add(button);
        }
        layout.Controls.Add(buttons);
        layout.Controls.Add(new Label { Text = "已有可用索引会直接复用。下载、导入或构建时可在 File > Abort 中取消。", AutoSize = true });
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = cancel;layout.Controls.Add(cancel);Controls.Add(layout);
    }
}
