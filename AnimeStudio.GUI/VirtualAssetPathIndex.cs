using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AnimeStudio.GUI
{
    internal sealed record VirtualAssetRecord(string Name, string Container,
                                               string Type, long PathId, string Source,
                                               long Offset = -1,
                                               bool ContainerOnly = false);

    /// <summary>
    /// One logical Unity asset file from AssetBundle.m_Container.  Records are
    /// the serialized objects associated with the file; they are deliberately
    /// kept behind the file node instead of being presented as sibling files.
    /// </summary>
    internal sealed class VirtualAssetFile
    {
        public string Container { get; }
        public IReadOnlyList<VirtualAssetRecord> Records { get; }
        public string Name => Path.GetFileName(Container.Replace('\\', '/'));
        public string Stem => Path.GetFileNameWithoutExtension(Name);
        public bool IsPrefab => Name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        public VirtualAssetFile(string container, IReadOnlyList<VirtualAssetRecord> records)
        {
            Container = container ?? string.Empty;
            Records = records ?? Array.Empty<VirtualAssetRecord>();
        }
    }

    internal sealed class VirtualAssetPathNode
    {
        public string Name { get; }
        public SortedDictionary<string, VirtualAssetPathNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<VirtualAssetRecord> Assets { get; } = new();
        public long AssetCount { get; set; }

        public VirtualAssetPathNode(string name) => Name = name;
    }

    internal sealed class VirtualAssetPathIndex
    {
        private readonly Dictionary<string, string> sourcePool =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> containerPool =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> typePool =
            new(StringComparer.Ordinal);

        public VirtualAssetPathNode Root { get; }
        public long AssetCount { get; private set; }
        public int DirectoryCount { get; private set; }
        public List<VirtualAssetRecord> Records { get; } = new();

        internal VirtualAssetPathIndex(string name) => Root = new VirtualAssetPathNode(name);

        public static VirtualAssetPathIndex LoadXml(string path)
        {
            var index = new VirtualAssetPathIndex("Assets");
            var sourcePool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var typePool = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit,
            });

            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Asset")
                {
                    reader.Read();
                    continue;
                }
                var element = (XElement)XElement.ReadFrom(reader);
                var name = element.Element("Name")?.Value ?? string.Empty;
                var container = element.Element("Container")?.Value ?? string.Empty;
                var type = Pool(typePool, element.Element("Type")?.Value ?? string.Empty);
                var pathIdText = element.Element("PathID")?.Value ?? "0";
                var source = Pool(sourcePool, NormalizeSource(element.Element("Source")?.Value ?? string.Empty));
                long.TryParse(pathIdText, out var pathId);
                index.Add(new VirtualAssetRecord(name, container, type, pathId, source));
            }

            index.Records.Sort((a, b) =>
            {
                var result = string.Compare(a.Container, b.Container, StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
                result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
                return string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
            });
            index.DirectoryCount = CountDirectories(index.Root);
            return index;
        }

        private static string NormalizeSource(string source)
        {
            var normalized = source.Replace('\\', '/');
            var marker = normalized.IndexOf("Bundles/", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 ? normalized[marker..] : normalized;
        }

        private static string Pool(Dictionary<string, string> pool, string value)
        {
            if (pool.TryGetValue(value, out var existing))
                return existing;
            pool.Add(value, value);
            return value;
        }

        internal void Add(VirtualAssetRecord asset)
        {
            asset = asset with
            {
                Container = Pool(containerPool, asset.Container ?? string.Empty),
                Type = Pool(typePool, asset.Type ?? string.Empty),
                Source = Pool(sourcePool, asset.Source ?? string.Empty)
            };
            AssetCount++;
            Records.Add(asset);
            var container = string.IsNullOrWhiteSpace(asset.Container)
                ? "[no container]"
                : asset.Container.Replace('\\', '/').Trim('/');
            var parts = container.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // The virtual tree mirrors the extracted file system.  Keep the
            // final file name as a leaf node; Unity objects belonging to that
            // file remain in the Asset List/Preview instead of becoming fake
            // sibling files in Scene Hierarchy.
            // Game catalogs already provide one record per logical file. Keep only
            // their directory nodes resident; file leaves are created by the TreeView
            // when that directory is expanded. This saves hundreds of thousands of
            // dictionaries/lists for catalogs such as Naraka's AppRes.info.
            var directoryPartCount = asset.ContainerOnly
                ? Math.Max(0, parts.Length - 1)
                : parts.Length;
            var node = Root;
            node.AssetCount++;
            for (var i = 0; i < directoryPartCount; i++)
            {
                var part = parts[i];
                if (!node.Children.TryGetValue(part, out var child))
                {
                    child = new VirtualAssetPathNode(part);
                    node.Children.Add(part, child);
                }
                node = child;
                node.AssetCount++;
            }
            node.Assets.Add(asset);
        }

        internal void Add(AssetEntry asset)
        {
            Add(new VirtualAssetRecord(
                asset.Name ?? string.Empty,
                Pool(containerPool, asset.Container ?? string.Empty),
                Pool(typePool, asset.Type.ToString()),
                asset.PathID,
                Pool(sourcePool, asset.Source ?? string.Empty),
                asset.Offset));
        }

        internal void FinishLoading(bool sortRecords)
        {
            if (sortRecords)
            {
                Records.Sort((a, b) =>
                {
                    var result = string.Compare(a.Container, b.Container, StringComparison.OrdinalIgnoreCase);
                    if (result != 0) return result;
                    result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    if (result != 0) return result;
                    return string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
                });
            }
            DirectoryCount = CountDirectories(Root);
            sourcePool.Clear();
            containerPool.Clear();
            typePool.Clear();
        }

        private static int CountDirectories(VirtualAssetPathNode node)
        {
            var count = node.Children.Count;
            foreach (var child in node.Children.Values)
                count += CountDirectories(child);
            return count;
        }
    }
}
