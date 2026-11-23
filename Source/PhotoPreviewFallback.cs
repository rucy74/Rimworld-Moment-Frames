using System;
using System.IO;
using UnityEngine;

namespace MomentFrames
{
    // Unity's PNG decoder handles palette, grayscale, 16-bit and interlaced imports.
    // Called only on the game thread, for one queued preview at a time. Originals stay unchanged.
    internal static class PhotoPreviewFallback
    {
        internal static byte[] Make(byte[] png)
        {
            if (!ExternalPhotoFiles.Png(png, out _)) throw new InvalidDataException("Invalid preview source image or image size");
            Texture2D source = null, small = null;
            try
            {
                source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!source.LoadImage(png, false)) throw new InvalidDataException("PNG could not be decoded");
                if (source.width <= PhotoPreviews.Side && source.height <= PhotoPreviews.Side) return source.EncodeToPNG();
                float scale = Mathf.Min(1f, PhotoPreviews.Side / (float)Math.Max(source.width, source.height));
                int w = Math.Max(1, Mathf.RoundToInt(source.width * scale)), h = Math.Max(1, Mathf.RoundToInt(source.height * scale));
                var pixels = new Color[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    pixels[y * w + x] = source.GetPixelBilinear((x + .5f) / w, (y + .5f) / h);
                small = new Texture2D(w, h, TextureFormat.RGBA32, false);
                small.SetPixels(pixels); small.Apply(false, false);
                return small.EncodeToPNG();
            }
            finally
            {
                if (small != null) UnityEngine.Object.Destroy(small);
                if (source != null) UnityEngine.Object.Destroy(source);
            }
        }
    }
}
