using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Maps Bundle source identities to asset records without copying records.
    /// Current Endfield indexes store each Bundle's records contiguously, so the
    /// normal map is one start/count pair per Bundle.
    /// </summary>
    internal sealed class EndfieldAssetSourceIndex
    {
        private readonly IReadOnlyList<VirtualAssetRecord> records;
        private readonly Dictionary<string, (int Start, int Count)> ranges =
            new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<VirtualAssetRecord>> fragmentedRecords;

        internal EndfieldAssetSourceIndex(IReadOnlyList<VirtualAssetRecord> records)
        {
            this.records = records ?? throw new ArgumentNullException(nameof(records));
            BuildRanges();
        }

        internal IEnumerable<VirtualAssetRecord> ForSource(string source)
        {
            source = NormalizeSource(source);
            if (fragmentedRecords != null)
            {
                return fragmentedRecords.TryGetValue(source, out var sourceRecords)
                    ? sourceRecords
                    : Array.Empty<VirtualAssetRecord>();
            }

            return ranges.TryGetValue(source, out var range)
                ? EnumerateRange(range.Start, range.Count)
                : Array.Empty<VirtualAssetRecord>();
        }

        private IEnumerable<VirtualAssetRecord> EnumerateRange(int start, int count)
        {
            for (var index = start; index < start + count; index++)
                yield return records[index];
        }

        private void BuildRanges()
        {
            string currentSource = null;
            var currentStart = 0;
            var currentCount = 0;
            var closedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void FinishRange()
            {
                if (currentSource == null) return;
                ranges.Add(currentSource, (currentStart, currentCount));
                closedSources.Add(currentSource);
            }

            for (var index = 0; index < records.Count; index++)
            {
                var source = NormalizeSource(records[index].Source);
                if (source.Equals(currentSource, StringComparison.OrdinalIgnoreCase))
                {
                    currentCount++;
                    continue;
                }

                FinishRange();
                if (closedSources.Contains(source))
                {
                    BuildFragmentedRecords();
                    return;
                }

                currentSource = source;
                currentStart = index;
                currentCount = 1;
            }

            FinishRange();
        }

        private void BuildFragmentedRecords()
        {
            ranges.Clear();
            fragmentedRecords = new Dictionary<string, List<VirtualAssetRecord>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                var source = NormalizeSource(record.Source);
                if (!fragmentedRecords.TryGetValue(source, out var sourceRecords))
                {
                    sourceRecords = new List<VirtualAssetRecord>();
                    fragmentedRecords.Add(source, sourceRecords);
                }
                sourceRecords.Add(record);
            }
        }

        private static string NormalizeSource(string source)
        {
            var normalized = (source ?? string.Empty).Replace('\\', '/');
            var marker = normalized.IndexOf("Bundles/", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 ? normalized[marker..] : normalized;
        }
    }
}
