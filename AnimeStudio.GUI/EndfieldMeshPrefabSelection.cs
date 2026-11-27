using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeStudio.GUI;

internal sealed class EndfieldMeshPrefabSelection
{
    private readonly List<string> selectedPrefabs = new();

    internal string Select(IReadOnlyList<string> matches, Func<IReadOnlyList<string>, string> choose)
    {
        var candidates = matches.Select(path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = selectedPrefabs.FirstOrDefault(path =>
            candidates.Contains(path, StringComparer.OrdinalIgnoreCase));
        if (selected != null || candidates.Length == 0)
            return selected;

        selected = candidates.Length == 1 ? candidates[0] : choose(candidates);
        if (selected != null)
            selectedPrefabs.Add(selected);
        return selected;
    }
}
