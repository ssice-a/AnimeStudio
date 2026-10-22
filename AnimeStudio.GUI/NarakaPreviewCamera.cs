using System;
using OpenTK.Mathematics;

namespace AnimeStudio.GUI;

internal static class NarakaPreviewCamera
{
    // Converted geometry uses GL CCW front faces and a camera looking down -Z.
    // Camera-space +Z is nearer; GL depth must decrease toward that surface.
    // A positive Z scale makes the opposite/inside surface win the depth test.
    internal static Matrix4 Projection(int width, int height) => Matrix4.CreateScale(
        Math.Min(1, (float)height / Math.Max(1, width)),
        Math.Min(1, (float)width / Math.Max(1, height)), -1);
}
