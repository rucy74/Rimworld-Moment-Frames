using System;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Four same-density quadrant renders -> one overview. Map-cell boundaries, rather than half-image assumptions,
    /// keep odd and rectangular maps aligned. Original quadrant files are retained separately.</summary>
    internal static class SettlementImageComposition
    {
        internal static Task<PhotoCapture.Result> Begin(PhotoCapture.Result[] parts, CellRect[] areas, int mapWidth, int mapHeight)
        {
            SettlementLayout.ImageSize(mapWidth, mapHeight, SettlementResolution.Side(mapWidth, mapHeight, mapWidth, mapHeight), out int width, out int height);
            bool jpeg = PhotoImageFormat.SaveJpeg;   // settings are read here, never on the worker
            return Task.Run(() => {
                byte[] output = new byte[width * height * 3];
                for (int i = 0; i < 4; i++)
                {
                    var source = parts[i].pixels ?? PhotoImageFormat.DecodeOnWorker(parts[i].png)
                        ?? throw new System.IO.InvalidDataException("Survey quadrant could not be decoded"); var area = areas[i];
                    int x0 = area.minX * width / mapWidth, x1 = (area.minX + area.Width) * width / mapWidth;
                    int y0 = area.minZ * height / mapHeight, y1 = (area.minZ + area.Height) * height / mapHeight;
                    for (int y = y0; y < y1; y++)
                    {
                        int sy = Math.Min(source.Height - 1, (y-y0) * source.Height / Math.Max(1,y1-y0));
                        if (source.Width == x1-x0) { Buffer.BlockCopy(source.Rgb,sy*source.Width*3,output,(y*width+x0)*3,source.Width*3); continue; }
                        for (int x=x0;x<x1;x++)
                        {
                            int sx=Math.Min(source.Width-1,(x-x0)*source.Width/Math.Max(1,x1-x0));
                            Buffer.BlockCopy(source.Rgb,(sy*source.Width+sx)*3,output,(y*width+x)*3,3);
                        }
                    }
                }
                var pixels=new PhotoPixels(width,height,output);
                return new PhotoCapture.Result {png=PhotoImageFormat.Encode(pixels,jpeg),thumb=pixels.Resize(256).Encode(),preview=pixels.Resize(PhotoPreviews.Side).Encode()};
            });
        }
        internal static PhotoCapture.Result Combine(PhotoCapture.Result[] parts, CellRect[] areas, int mapWidth, int mapHeight)
        {
            if (parts?.Length != 4 || areas?.Length != 4) throw new ArgumentException("Four quadrants required");
            SettlementLayout.ImageSize(mapWidth, mapHeight, SettlementResolution.Side(mapWidth, mapHeight, mapWidth, mapHeight), out int width, out int height);
            var output = new Color32[width * height];
            Texture2D combined = null;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    Texture2D source = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    try
                    {
                        if (parts[i]?.png == null || !source.LoadImage(parts[i].png, false)) throw new InvalidOperationException("Missing quadrant");
                        Color32[] pixels = source.GetPixels32(); CellRect area = areas[i];
                        int x0 = area.minX * width / mapWidth, x1 = (area.minX + area.Width) * width / mapWidth;
                        int y0 = area.minZ * height / mapHeight, y1 = (area.minZ + area.Height) * height / mapHeight;
                        for (int y = y0; y < y1; y++)
                        {
                            int sy = Math.Min(source.height - 1, (y - y0) * source.height / Math.Max(1, y1 - y0));
                            if (source.width == x1 - x0)
                            {
                                Array.Copy(pixels, sy * source.width, output, y * width + x0, source.width);
                                continue;
                            }
                            for (int x = x0; x < x1; x++)
                            {
                                int sx = Math.Min(source.width - 1, (x - x0) * source.width / Math.Max(1, x1 - x0));
                                output[y * width + x] = pixels[sy * source.width + sx];
                            }
                        }
                    }
                    finally { UnityEngine.Object.Destroy(source); }
                }
                combined = new Texture2D(width, height, TextureFormat.RGB24, false);
                combined.SetPixels32(output); combined.Apply(false, false);
                byte[] png = combined.EncodeToPNG(), thumb = PhotoCapture.MakeThumb(combined);
                return new PhotoCapture.Result { png = png, thumb = thumb };
            }
            finally { if (combined != null) UnityEngine.Object.Destroy(combined); }
        }
    }
}
