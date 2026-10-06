using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            name = NormalizeCab(name);
            source = NormalizeSource(source);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
                return;
            var normalizedDependencies = (dependencies ?? Array.Empty<string>())
                .Select(NormalizeCab)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (byCab.TryGetValue(name, out var previous))
            {
                if (!string.Equals(previous.Source, source, StringComparison.OrdinalIgnoreCase) ||
                    !previous.Dependencies.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(normalizedDependencies))
                    throw new InvalidDataException("Conflicting Bundle source or dependency set for CAB: " + name);
                return;
            }
            byCab.Add(name, new CabRecord(source, normalizedDependencies));
            if (!cabsBySource.TryGetValue(source, out var sourceCabs))
            {
                sourceCabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                cabsBySource.Add(source, sourceCabs);
            }
            sourceCabs.Add(name);
        }

        public IReadOnlyCollection<string> GetCabNames(string source)
        {
            source = NormalizeSource(source);
            return cabsBySource.TryGetValue(source, out var names)
                ? names
                : Array.Empty<string>();
        }

        public string GetBundleSource(string cab) =>
            byCab.TryGetValue(NormalizeCab(cab), out var record) ? record.Source : null;

        public IReadOnlyDictionary<string, string[]> DirectBundleDependencies(
            out IReadOnlyDictionary<string, string[]> unresolved)
        {
            var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var missing = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in cabsBySource)
            {
                var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missingCabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cab in source.Value)
                foreach (var dependency in byCab[cab].Dependencies)
                {
                    // Unity owns this intrinsic resource file; it is not a VFS Bundle.
                    if (string.Equals(dependency, "unity default resources", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!byCab.TryGetValue(dependency, out var owner))
                    {
                        missingCabs.Add(dependency);
                        continue;
                    }
                    if (!string.Equals(source.Key, owner.Source, StringComparison.OrdinalIgnoreCase))
                        dependencies.Add(owner.Source);
                }
                result.Add(source.Key.ToLowerInvariant(), dependencies.Select(x => x.ToLowerInvariant()).ToArray());
                if (missingCabs.Count != 0) missing.Add(source.Key.ToLowerInvariant(), missingCabs.ToArray());
            }
            unresolved = missing;
            return result;
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

        private static string NormalizeCab(string cab) =>
            Path.GetFileName((cab ?? string.Empty).Replace('\\', '/'));
    }
}
