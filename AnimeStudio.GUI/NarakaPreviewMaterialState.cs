using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeStudio.GUI;

internal static class NarakaPreviewMaterialState
{
    internal static NarakaPreviewMaterial Apply(NarakaPreviewMaterial material,
        IReadOnlyDictionary<string, float> floats, string keywords, string shader, int queue)
    {
        // No image inspection: diffuse A may be unrelated packed data. These
        // exact profiles consume it only where the shader contract says so.
        shader = (shader ?? "").Replace('\\', '/').Split('/').Last().ToLowerInvariant();
        var tokens = (keywords ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var periodic = shader == "ch_cloth_silk_specular4_periodic2.shadergraph";
        var silk = shader == "lx22standardcharacterclothsilk.shader";
        var opaqueGraph = shader == "ch_cloth_silk_specular4_periodic2_opaque.shadergraph";
        var depthOnly = shader == "lx22transparentanimdepth.shader";
        var knownOpaque = shader is "lx22standardcharacterskin.shader" or "lx22standardcharacter.shader" or "lx22standardcharactercloth.shader";
        var clipSwitch = tokens.Contains("_USE_CUTOUT") || tokens.Contains("_ALPHATEST_ON") ||
            floats.GetValueOrDefault("_AlphaClip") == 1 || floats.GetValueOrDefault("_AlphaTest") == 1;
        var cutout = !depthOnly && !opaqueGraph && (knownOpaque ? tokens.Contains("_USE_CUTOUT") : clipSwitch);
        // _Surface is an explicit selector; it overrides stale blend factors.
        // _Mode is shader-specific and cannot be decoded as a global enum.
        var explicitBlend = floats.TryGetValue("_Surface", out var surface) ? surface == 1 :
            tokens.Contains("_ALPHABLEND_ON") ||
            (floats.GetValueOrDefault("_SrcBlend", -1) == 5 && floats.GetValueOrDefault("_DstBlend", -1) == 10);
        var blend = !depthOnly && !opaqueGraph && !knownOpaque && (periodic || silk || explicitBlend);
        // Render state and alpha source are separate facts. Even explicit
        // SrcAlpha blending does not prove the alpha comes from the diffuse.
        var resolved = depthOnly || opaqueGraph || periodic || silk || knownOpaque;
        var useBaseAlpha = resolved && !depthOnly && (cutout || blend);
        var reason = !resolved ? "unresolved-shader-alpha-unused" : depthOnly ? "dedicated-depth" : opaqueGraph ? "opaque-shader-profile" :
            periodic || silk ? "silk-transparent-pass" : cutout || blend ? "explicit-material-alpha-state" :
            "opaque-material-state";
        // The known silk pass uses a view-dependent coverage function. A
        // serialized threshold alone neither enables clipping nor this function.
        var zwrite = floats.GetValueOrDefault("_ZWrite", floats.GetValueOrDefault("_SilkZWrite", blend ? 0 : 1)) != 0;
        return material with
        {
            Cull = (int)floats.GetValueOrDefault("_Cull", floats.GetValueOrDefault("_Culling", 2)),
            Cutout = cutout, Blend = blend, SilkCoverage = periodic || silk,
            Cutoff = silk ? floats.GetValueOrDefault("_Cutoff", 0.5f) :
                floats.GetValueOrDefault("_AlphaClipThreshold", floats.GetValueOrDefault("_Cutoff", 0.5f)),
            ZWrite = zwrite, ZTest = (int)floats.GetValueOrDefault("_ZTest", 4),
            Queue = queue >= 0 ? queue : depthOnly ? 1990 : blend ? 3000 : cutout ? 2450 : 2000,
            SrcBlend = (int)floats.GetValueOrDefault("_SrcBlend", floats.GetValueOrDefault("_ClothMaterialSrcBlend", 5)),
            DstBlend = (int)floats.GetValueOrDefault("_DstBlend", 10), DepthOnly = depthOnly,
            AlphaResolved = resolved, UseBaseAlpha = useBaseAlpha, AlphaReason = reason,
        };
    }

    // DoubleTransparentPass: verified from Periodic2 blob 776 and legacy silk
    // blob 1216. Full multi-pass composition remains a preview approximation.
    internal static float SilkAlpha(float tintedAlpha, float threshold, float normalDotView)
    {
        var coverage = Math.Clamp(tintedAlpha / Math.Max(0.01f, threshold), 0, 1);
        return (1 - MathF.Pow(1 - coverage, 1 / Math.Max(5.96046448e-8f, Math.Abs(normalDotView)))) * Math.Min(1, 255 * coverage);
    }
}
