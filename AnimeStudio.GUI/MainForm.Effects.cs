using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnimeStudio;

namespace AnimeStudio.GUI;

partial class MainForm
{
    private async Task ExportCheckedEndfieldEffectsAsync()
    {
        if (endfieldVfsArchive == null || !VerifyEndfieldArchive()) return;
        var files = GetCheckedEndfieldFiles();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Check logical effect .prefab files first. Particle-only Prefabs are supported; a character Mesh is not required.", "EFF effect sources");
            return;
        }
        using var folder = new FolderBrowserDialog { Description = "Export independent EFF effect sources (.efffx)", SelectedPath = saveDirectoryBackup };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        saveDirectoryBackup = folder.SelectedPath;
        var archive = endfieldVfsArchive;
        var workspace = endfieldWorkspace;
        var unityVersion = Studio.assetsManager.SpecifyUnityVersion;
        await endfieldPreviewLock.WaitAsync();
        int completed = 0;
        var failed = new List<string>();
        try
        {
            StatusStripUpdate("Reading effect source dependencies...");
            var prepared = await Task.Run(() =>
            {
                if (!EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out var indexPath))
                    throw new InvalidDataException("Open the current game folder and update the workspace index first");
                return EndfieldIndexStore.LoadPrefabExportIndexes(indexPath, files.ConvertAll(f => f.Container));
            });
            foreach (var file in files)
            {
                StatusStripUpdate($"Effect sources {completed + failed.Count + 1}/{files.Count}: {file.Name}");
                try
                {
                    string output = EndfieldEffectSourceCommand.PackagePath(folder.SelectedPath, file.Container);
                    await Task.Run(() => EndfieldEffectSourceCommand.ExportOne(archive, workspace, file.Container, output, unityVersion, prepared[file.Container]));
                    completed++;
                }
                catch (Exception error)
                {
                    failed.Add(file.Container + ": " + error.Message);
                    Logger.Error("EFF effect source export: " + error);
                }
            }
        }
        catch (Exception error)
        {
            failed.Add(error.Message);
            Logger.Error("EFF effect source batch: " + error);
        }
        finally { endfieldPreviewLock.Release(); }
        StatusStripUpdate($"Effect sources: {completed}/{files.Count} exported, {failed.Count} failed.");
        if (failed.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, failed), "EFF effect source failures");
        if (completed > 0 && Properties.Settings.Default.openAfterExport) Studio.OpenFolderInExplorer(folder.SelectedPath);
    }
}
