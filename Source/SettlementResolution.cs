using System;
using UnityEngine;

namespace MomentFrames
{
    internal static class SettlementResolution
    {
        internal const int DefaultPixelsPerCell = 16;
        internal const int MaximumSide = ExternalPhotoFiles.MaximumSide;
        internal static int Normalize(int value) => value == 8 || value == 12 || value == 16 || value == 24 ? value : DefaultPixelsPerCell;

        internal static int Side(int mapWidth, int mapHeight, int areaWidth, int areaHeight, int pixelsPerCell, int hardwareLimit)
        {
            if (mapWidth < 1 || mapHeight < 1 || areaWidth < 1 || areaHeight < 1) throw new ArgumentOutOfRangeException();
            int cap = Math.Max(64, Math.Min(MaximumSide, hardwareLimit));
            double density = Math.Min(Normalize(pixelsPerCell), cap / (double)Math.Max(mapWidth, mapHeight));
            return Math.Max(64, (int)Math.Round(Math.Max(areaWidth, areaHeight) * density));
        }

        internal static int Side(int mapWidth, int mapHeight, int areaWidth, int areaHeight)
            => Side(mapWidth, mapHeight, areaWidth, areaHeight,
                MomentFramesMod.Settings?.surveyPxPerCell ?? DefaultPixelsPerCell,
                SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 4096);
    }
}
