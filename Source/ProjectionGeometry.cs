using System;

namespace MomentFrames
{
    // Pure geometry shared by drawing, placement previews, map bounds and the numeric dialog.
    internal static class ProjectionGeometry
    {
        internal const float Minimum = .5f, Maximum = 40f;
        internal const int LegacyDepth = 0, BehindDepth = 1, FrontDepth = 2;
        internal static bool ValidDimension(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= Minimum && value <= Maximum;
        internal static bool Custom(float width, float height) => ValidDimension(width) && ValidDimension(height);
        internal static void Bounds(int preset, float customWidth, float customHeight, out float width, out float height)
        {
            if (Custom(customWidth, customHeight)) { width = customWidth; height = customHeight; return; }
            width = preset == 2 ? 9f : preset == 1 ? 6f : 2.4f;
            height = preset == 2 ? 6f : preset == 1 ? 4f : 1.6f;
        }
        internal static void Fit(float aspect, float width, float height, out float fittedWidth, out float fittedHeight)
        {
            if (float.IsNaN(aspect) || float.IsInfinity(aspect) || aspect <= 0f) aspect = 1.5f;
            fittedWidth = width; fittedHeight = width / aspect;
            if (fittedHeight > height) { fittedHeight = height; fittedWidth = height * aspect; }
        }
        internal static float Throw(bool horizontal, int preset, float width, float height, float boxWidth, float boxHeight, bool custom)
            // Anchor the actual photograph's near edge, not the unused area of its input box.
            => !custom && preset == 0 ? 1.8f : (horizontal ? width : height) / 2f + 1.2f;
    }
}
