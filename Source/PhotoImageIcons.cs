using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Manual decoration in original-image coordinates, not a claim about a pawn's feelings.</summary>
    public sealed class PhotoImageIcon : IExposable
    {
        public PhotoIcon icon = PhotoIcon.Heart;
        public float x = .5f, y = .5f, size = .12f;
        public PhotoImageIcon() { }
        public PhotoImageIcon(PhotoIcon icon, float x, float y, float size) { this.icon = icon; this.x = x; this.y = y; this.size = size; }
        public void ExposeData()
        {
            Scribe_Values.Look(ref icon, "icon", PhotoIcon.Heart);
            Scribe_Values.Look(ref x, "x", .5f);
            Scribe_Values.Look(ref y, "y", .5f);
            Scribe_Values.Look(ref size, "size", .12f);
        }
    }

    internal static class PhotoImageIcons
    {
        internal const int MaxCount = 24;
        internal const float MinSize = .025f, MaxSize = .5f;
        internal static bool Valid(PhotoImageIcon item) => item != null && AlbumStickers.Allowed(item.icon)
            && AlbumStickers.Finite(item.x) && AlbumStickers.Finite(item.y) && AlbumStickers.Finite(item.size);
        internal static List<PhotoImageIcon> Copy(IEnumerable<PhotoImageIcon> source) => (source ?? Enumerable.Empty<PhotoImageIcon>())
            .Where(Valid).Take(MaxCount).Select(s => new PhotoImageIcon(s.icon, Mathf.Clamp01(s.x), Mathf.Clamp01(s.y), Mathf.Clamp(s.size, MinSize, MaxSize))).ToList();

        internal static Rect OnImage(PhotoImageIcon item, Rect image) => new Rect(image.x + item.x * image.width - item.size * image.width / 2f,
            image.y + item.y * image.height - item.size * image.width / 2f, item.size * image.width, item.size * image.width);

        /// <summary>Source and output pixels are bottom-up; normalized y is top-down like the crop. Filter precedes decorations.</summary>
        internal static void Composite(Color32[] pixels, int width, int height, RectInt crop, int originalWidth, int originalHeight,
            IEnumerable<PhotoImageIcon> source)
        {
            if (pixels == null || pixels.Length != width * height || width <= 0 || height <= 0) return;
            foreach (var item in Copy(source))
            {
                int side = Mathf.Max(1, Mathf.RoundToInt(item.size * originalWidth));
                float left = item.x * originalWidth - side / 2f - crop.x;
                float bottom = (1f - item.y) * originalHeight - side / 2f - crop.y;
                Color32[] mask = PhotoIconArt.Texture(item.icon).GetPixels32();
                int maskSize = PhotoIconArt.TextureSize;
                Color ink = ColorFor(item.icon);
                int x0 = Mathf.Max(0, Mathf.FloorToInt(left)), y0 = Mathf.Max(0, Mathf.FloorToInt(bottom));
                int x1 = Mathf.Min(width, Mathf.CeilToInt(left + side)), y1 = Mathf.Min(height, Mathf.CeilToInt(bottom + side));
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                {
                    int mx = Mathf.Clamp(Mathf.FloorToInt((x + .5f - left) * maskSize / side), 0, maskSize - 1);
                    int my = Mathf.Clamp(Mathf.FloorToInt((y + .5f - bottom) * maskSize / side), 0, maskSize - 1);
                    float alpha = mask[my * maskSize + mx].a / 255f;
                    if (alpha <= 0f) continue;
                    Color old = pixels[y * width + x];
                    pixels[y * width + x] = (Color32)new Color(Mathf.Lerp(old.r, ink.r, alpha), Mathf.Lerp(old.g, ink.g, alpha), Mathf.Lerp(old.b, ink.b, alpha), old.a);
                }
            }
        }

        internal static Color ColorFor(PhotoIcon icon)
        {
            if (icon == PhotoIcon.Heart) return new Color(1f, .70f, .66f);
            if (icon == PhotoIcon.Star) return new Color(1f, .88f, .49f);
            if (icon == PhotoIcon.Music || icon == PhotoIcon.Wave || icon == PhotoIcon.Paw) return new Color(.74f, .91f, .77f);
            return new Color(.98f, .94f, .85f);
        }
    }
}
