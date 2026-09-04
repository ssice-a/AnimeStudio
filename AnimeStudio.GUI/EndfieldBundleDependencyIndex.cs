using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Maps Unity CAB names to logical Endfield Bundle paths. This is the bridge between
    /// external PPtr references in a Prefab and the encrypted VFS entry containing them.
    /// </summary>
    internal sealed class EndfieldBundleDependencyIndex
    {
        private sealed record CabRecord(string Source, string[] Dependencies);

        private readonly Dictionary<string, CabRecord> byCab =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> cabsBySource =
            new(StringComparer.OrdinalIgnoreCase);

        public int CabCount => byCab.Count;

        internal void AddCab(string name, string source, IEnumerable<string> dependencies)
        {
            source = NormalizeSource(source);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
                return;
            var normalizedDependencies = dependencies
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!byCab.ContainsKey(name))
                byCab.Add(name, new CabRecord(source, normalizedDependencies));
            if (!cabsBySource.TryGetValue(source, out var sourceCabs))
            {
                sourceCabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                cabsBySource.Add(source, sourceCabs);
            }
            sourceCabs.Add(name);
        }

        public static EndfieldBundleDependencyIndex LoadXml(string path)
        {
            var index = new EndfieldBundleDependencyIndex();
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit,
            });

            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cab")
                {
                    reader.Read();
                    continue;
                }

                var element = (XElement)XElement.ReadFrom(reader);
                var name = element.Attribute("Name")?.Value;
                var source = NormalizeSource(element.Attribute("Source")?.Value);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
                    continue;

                var dependencies = element.Elements("Dependency")
                    .Select(x => Path.GetFileName(x.Value))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                index.AddCab(name, source, dependencies);
            }
            return index;
        }

        public IReadOnlyCollection<string> GetCabNames(string source)
        {
            source = NormalizeSource(source);
            return cabsBySource.TryGetValue(source, out var names)
                ? names
                : Array.Empty<string>();
        }

        public IReadOnlyList<string> ResolveBundleClosure(string source)
        {
            source = NormalizeSource(source);
            var result = new List<string>();
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queuedCabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();

            void AddSource(string value)
            {
                if (!seenSources.Add(value))
                    return;
                result.Add(value);
                if (cabsBySource.TryGetValue(value, out var names))
                    foreach (var name in names)
                        if (queuedCabs.Add(name))
                            queue.Enqueue(name);
            }

            AddSource(source);
            while (queue.Count > 0)
            {
                var cab = queue.Dequeue();
                if (!byCab.TryGetValue(cab, out var record))
                    continue;
                AddSource(record.Source);
                foreach (var dependency in record.Dependencies)
                {
                    if (!queuedCabs.Add(dependency))
                        continue;
                    queue.Enqueue(dependency);
                    if (byCab.TryGetValue(dependency, out var dependencyRecord))
                        AddSource(dependencyRecord.Source);
                }
            }
            return result;
        }

        private static string NormalizeSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return string.Empty;
            var normalized = source.Replace('\\', '/');
            var marker = normalized.IndexOf("Bundles/", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 ? normalized[marker..] : normalized;
        }
    }
}
