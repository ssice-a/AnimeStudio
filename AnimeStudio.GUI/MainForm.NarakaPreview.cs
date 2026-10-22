using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTK.Mathematics;

namespace AnimeStudio.GUI;

partial class MainForm
{
    private NarakaPreviewRenderer narakaPreview;
    private FlowLayoutPanel narakaPreviewOptions;
    private CheckBox narakaShowAll;

    private void InitializeNarakaPreviewOptions()
    {
        narakaPreviewOptions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 28, Visible = false, AutoSize = false };
        narakaShowAll = new CheckBox { Text = "显示全部网格（含低精度 LOD / 未启用项）", AutoSize = true };
        narakaShowAll.CheckedChanged += (_, _) =>
        {
            if (narakaPreview == null) return;
            narakaPreview.ShowAll = narakaShowAll.Checked;
            narakaPreview.Selection = -1;
            UpdateNarakaPreviewInfo(); glControl.Invalidate();
        };
        narakaPreviewOptions.Controls.Add(narakaShowAll);
        previewPanel.Controls.Add(narakaPreviewOptions);
        FormClosing += (_, _) => ClearNarakaPreview();
    }

    private void ClearNarakaPreview()
    {
        if (narakaPreview != null)
        {
            glControl.MakeCurrent(); narakaPreview.Dispose(); narakaPreview = null;
        }
        if (narakaPreviewOptions != null) narakaPreviewOptions.Visible = false;
    }

    private void UpdateNarakaPreviewInfo()
    {
        if (narakaPreview == null) return;
        var data = narakaPreview.Data;
        var selected = narakaPreview.Selection >= 0 ? data.Parts[narakaPreview.Selection] : null;
        assetInfoLabel.Text = selected == null
            ? $"Prefab: {data.Prefab}\nMeshes: {narakaPreview.VisibleCount} / {data.Parts.Count} · 基础色预览（展开 Prefab 可逐个选择 Mesh）"
            : $"Mesh: {selected.Name} · {selected.MeshName}\nSource: {selected.MeshSource}\nMaterial: {string.Join(", ", selected.Materials.Select(m => m.Name).Distinct())}";
        assetInfoLabel.Visible = displayInfo.Checked;
        StatusStripUpdate("基础色预览 | 左键旋转 · 右键平移 · 滚轮缩放 | Ctrl+S 切换照明 · Ctrl+W 线框");
    }

    private async Task PreviewNarakaPrefabAsync(VirtualAssetFile file, TreeNode node = null, bool expand = false, int selection = -1)
    {
        var record = file.Records.FirstOrDefault();
        if (record == null || !File.Exists(record.Source)) return;
        endfieldSelectedVirtualFile = file;
        // Returning to the currently displayed Prefab also supersedes any
        // other Prefab still loading; an old completion must not replace it.
        ResetEndfieldPreviewCancellation();
        var token = endfieldPreviewCancellation.Token;
        if (narakaPreview?.Data.Prefab == file.Container)
        {
            narakaPreview.Selection = selection;
            if (expand) node?.Expand();
            UpdateNarakaPreviewInfo(); glControl.Invalidate(); return;
        }
        try
        {
            await endfieldPreviewLock.WaitAsync(token);
            try
            {
                StatusStripUpdate("Loading Prefab meshes and base color textures...");
                // Use the exporter graph loader: only this Prefab's references
                // are decoded, even when its Bundle contains many costumes.
                var data = await Task.Run(() => NarakaPrefabPreview.Load(record, token), token);
                token.ThrowIfCancellationRequested();
                if (IsDisposed || Disposing) return;
                ResetEndfieldPreviewSurface();
                lastSelectedItem = null; endfieldSelectedPrefabRoot = null;
                if (data.Parts.Count == 0) { assetInfoLabel.Visible = false; PreviewText("This Prefab contains no MeshRenderer / SkinnedMeshRenderer: " + file.Container); return; }
                glControl.Visible = true;
                glControl.MakeCurrent();
                narakaPreview = new NarakaPreviewRenderer(data) { Selection = selection };
                var extent = Math.Max(1e-5f, (data.Max - data.Min).Length);
                modelMatrixData = Matrix4.CreateTranslation(-(data.Min + data.Max) * 0.5f) * Matrix4.CreateScale(1.75f / extent);
                viewMatrixData = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(15)) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(5));
                wireFrameMode = 0; shadeMode = 0;
                narakaShowAll.Checked = false;
                narakaPreviewOptions.Visible = true; narakaPreviewOptions.BringToFront(); assetInfoLabel.BringToFront();
                if (node != null && node.TreeView == sceneTreeView)
                {
                    node.Nodes.Clear();
                    for (var i = 0; i < data.Parts.Count; i++)
                    {
                        var part = data.Parts[i];
                        var state = part.LowerLod ? " · LOD" : !part.Visible ? " · inactive" : "";
                        node.Nodes.Add(new TreeNode(part.Name + " [Mesh" + state + "]") { Tag = new NarakaPreviewSelection(file.Container, i) });
                    }
                    if (expand) node.Expand();
                }
                dumpTextBox.Text = Newtonsoft.Json.JsonConvert.SerializeObject(new { data.Prefab, Meshes = data.Parts.Select(p => new
                    { p.Name, p.RendererSource, p.MeshSource, p.MeshName, p.Visible, p.LowerLod,
                      Materials = p.Materials.Select(m => new { m.Name, m.Source, BaseColorTexture = m.Texture != null,
                          m.Cull, m.Cutout, m.Cutoff, m.Blend, m.SilkCoverage, m.AlphaResolved, m.UseBaseAlpha, m.AlphaReason, m.ZWrite, m.ZTest,
                          m.Queue, m.SrcBlend, m.DstBlend, m.DepthOnly }) }), data.Warnings }, Newtonsoft.Json.Formatting.Indented);
                UpdateNarakaPreviewInfo(); glControl.Invalidate();
            }
            finally { endfieldPreviewLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ClearNarakaPreview();
            Logger.Error($"Naraka Prefab preview failed for {file.Container}: {error}");
            ResetEndfieldPreviewSurface();
            assetInfoLabel.Visible = false;
            dumpTextBox.Clear();
            PreviewText("Prefab preview failed: " + error.Message);
            StatusStripUpdate("Prefab preview failed: " + error.Message);
        }
    }
}
