using System;
using System.Collections.Generic;
using UnityEngine;

namespace MomentFrames
{
    /// <summary>The approved tree-e-v1 hanging-photo layout, in the texture's own coordinates. The 1254 px source is drawn
    /// as a square; the concept normalized it to 1280 px. Positions, hook endpoints and card sizes retain that exact layout.</summary>
    public static class MemoryTreeLayout
    {
        public const int Count = 35;
        public const float PreviewSize = 1280f;
        public const float StructureCells = 7f;
        public const float PixelCells = StructureCells / PreviewSize;
        public const float StringWidth = 3f * PixelCells;
        public const float SideBorder = 6f * PixelCells;
        public const float TopBorder = 6f * PixelCells;
        public const float BottomBorder = 16f * PixelCells;

        public sealed class Slot
        {
            public readonly int id;
            public readonly string branch;
            public readonly Vector2 anchor, hook, photoCenter, photoSize;
            public readonly float rotationDeg;

            internal Slot(int id, string branch, float ax, float ay, float hx, float hy, float cx, float cy, float w, float h, float angle)
            {
                this.id = id;
                this.branch = branch;
                anchor = new Vector2(ax / PreviewSize, ay / PreviewSize);
                hook = new Vector2(hx / PreviewSize, hy / PreviewSize);
                photoCenter = new Vector2(cx / PreviewSize, cy / PreviewSize);
                photoSize = new Vector2(w / PreviewSize, h / PreviewSize);
                rotationDeg = angle;
            }

            public Vector2 CellSize => photoSize * StructureCells;
            public Vector2 ImageSize => CellSize - new Vector2(SideBorder * 2f, TopBorder + BottomBorder);
            public Quaternion Rotation => Quaternion.AngleAxis(rotationDeg, Vector3.up);
            public Vector3 AnchorAt(Vector3 drawLoc) => World(anchor, drawLoc);
            public Vector3 HookAt(Vector3 drawLoc) => World(hook, drawLoc);
            public Vector3 CenterAt(Vector3 drawLoc) => World(photoCenter, drawLoc);
            public Vector3 ImageCenterAt(Vector3 drawLoc) => CenterAt(drawLoc) + Rotation * new Vector3(0f, 0f, (BottomBorder - TopBorder) / 2f);

            /// <summary>The four actual rotated card corners, with no enlarged rectangle used for clicking.</summary>
            public Vector3[] CornersAt(Vector3 drawLoc)
            {
                Vector3 c = CenterAt(drawLoc);
                Vector2 size = CellSize;
                Quaternion q = Rotation;
                return new[]
                {
                    c + q * new Vector3(-size.x / 2f, 0f, -size.y / 2f),
                    c + q * new Vector3(-size.x / 2f, 0f, size.y / 2f),
                    c + q * new Vector3(size.x / 2f, 0f, size.y / 2f),
                    c + q * new Vector3(size.x / 2f, 0f, -size.y / 2f)
                };
            }
        }

        // Values from docs/concept-hanging-photos/tree-e-v1/anchors.json, revision vertical-hanging-r2.
        private static readonly Slot[] placements =
        {
            new Slot(1, "upper-left", 243.96f, 413.40f, 265.95f, 472.37f, 269.00f, 516.00f, 108.00f, 87.48f, -4f),
            new Slot(2, "crown", 452.19f, 258.25f, 446.95f, 310.81f, 443.00f, 356.00f, 112.00f, 90.72f, 5f),
            new Slot(3, "middle-right", 1017.67f, 479.74f, 1031.50f, 518.10f, 1033.00f, 561.00f, 106.00f, 85.86f, -2f),
            new Slot(4, "lower-left", 243.96f, 801.28f, 235.66f, 853.69f, 231.00f, 898.00f, 110.00f, 89.10f, 6f),
            new Slot(5, "lower-right", 924.78f, 822.71f, 928.33f, 881.04f, 932.00f, 923.00f, 104.00f, 84.24f, -5f),
            new Slot(6, "upper-left", 179.65f, 395.02f, 152.29f, 472.32f, 150.00f, 516.00f, 108.00f, 87.48f, 3f),
            new Slot(7, "upper-left", 367.46f, 430.75f, 397.26f, 514.89f, 402.00f, 560.00f, 112.00f, 90.72f, -6f),
            new Slot(8, "crown", 555.28f, 333.78f, 546.50f, 408.10f, 545.00f, 451.00f, 106.00f, 85.86f, 2f),
            new Slot(9, "crown", 729.82f, 275.60f, 721.67f, 403.51f, 724.00f, 448.00f, 110.00f, 89.10f, -3f),
            new Slot(10, "crown", 774.74f, 239.87f, 809.13f, 314.19f, 804.00f, 356.00f, 104.00f, 84.24f, 7f),
            new Slot(11, "middle-right", 959.49f, 355.22f, 912.95f, 486.37f, 916.00f, 530.00f, 108.00f, 87.48f, -4f),
            new Slot(12, "middle-left", 236.81f, 548.13f, 208.16f, 619.75f, 205.00f, 665.00f, 112.00f, 90.72f, 4f),
            new Slot(13, "middle-left", 288.87f, 539.97f, 325.50f, 632.10f, 327.00f, 675.00f, 106.00f, 85.86f, -2f),
            new Slot(14, "lower-left", 115.34f, 732.89f, 115.66f, 779.69f, 111.00f, 824.00f, 110.00f, 89.10f, 6f),
            new Slot(15, "lower-right", 1053.40f, 843.13f, 1057.33f, 892.04f, 1061.00f, 934.00f, 104.00f, 84.24f, -5f),
            new Slot(16, "crown", 701.24f, 168.42f, 691.29f, 208.32f, 689.00f, 252.00f, 108.00f, 87.48f, 3f),
            new Slot(17, "middle-right", 815.57f, 564.47f, 801.47f, 606.98f, 807.00f, 652.00f, 112.00f, 90.72f, -7f),
            new Slot(18, "middle-right", 911.52f, 574.67f, 925.50f, 607.10f, 924.00f, 650.00f, 106.00f, 85.86f, 2f),
            new Slot(19, "middle-right", 1043.19f, 593.05f, 1046.89f, 629.56f, 1050.00f, 674.00f, 110.00f, 89.10f, -4f),
            new Slot(20, "middle-right", 1189.15f, 451.16f, 1211.67f, 508.04f, 1208.00f, 550.00f, 104.00f, 84.24f, 5f),
            new Slot(21, "middle-right", 1068.71f, 346.03f, 1091.71f, 419.32f, 1094.00f, 463.00f, 108.00f, 87.48f, -3f),
            new Slot(22, "middle-right", 1189.15f, 451.16f, 1222.74f, 616.89f, 1218.00f, 662.00f, 112.00f, 90.72f, 6f),
            new Slot(23, "middle-left", 434.83f, 533.84f, 441.26f, 606.23f, 445.00f, 649.00f, 106.00f, 85.86f, -5f),
            new Slot(24, "middle-left", 493.01f, 566.51f, 521.78f, 694.46f, 521.00f, 739.00f, 110.00f, 89.10f, 1f),
            new Slot(25, "lower-left", 338.88f, 740.03f, 347.94f, 776.98f, 345.00f, 819.00f, 104.00f, 84.24f, 4f),
            new Slot(26, "lower-left", 488.93f, 851.29f, 490.43f, 906.50f, 495.00f, 950.00f, 108.00f, 87.48f, -6f),
            new Slot(27, "lower-left", 357.26f, 817.61f, 361.37f, 880.70f, 359.00f, 926.00f, 112.00f, 90.72f, 3f),
            new Slot(28, "middle-right", 833.94f, 589.98f, 813.50f, 704.10f, 815.00f, 747.00f, 106.00f, 85.86f, -2f),
            new Slot(29, "lower-right", 1053.40f, 621.63f, 1058.43f, 773.78f, 1053.00f, 818.00f, 110.00f, 89.10f, 7f),
            new Slot(30, "lower-right", 1172.82f, 707.37f, 1178.06f, 746.98f, 1181.00f, 789.00f, 104.00f, 84.24f, -4f),
            new Slot(31, "lower-right", 1143.22f, 767.59f, 1179.53f, 870.29f, 1178.00f, 914.00f, 108.00f, 87.48f, 2f),
            new Slot(32, "lower-right", 794.13f, 863.54f, 798.05f, 950.81f, 802.00f, 996.00f, 112.00f, 90.72f, -5f),
            new Slot(33, "crown", 661.44f, 378.69f, 661.99f, 537.17f, 659.00f, 580.00f, 106.00f, 85.86f, 4f),
            new Slot(34, "crown", 400.13f, 369.51f, 407.67f, 416.51f, 410.00f, 461.00f, 110.00f, 89.10f, -3f),
            new Slot(35, "middle-right", 995.22f, 348.07f, 982.40f, 390.11f, 978.00f, 432.00f, 104.00f, 84.24f, 6f)
        };

        public static IReadOnlyList<Slot> Slots => placements;
        public static Slot At(int index) => index >= 0 && index < Count ? placements[index] : null;

        public static Vector3 World(Vector2 normalized, Vector3 drawLoc) =>
            drawLoc + new Vector3((normalized.x - 0.5f) * StructureCells, 0f, (0.5f - normalized.y) * StructureCells);

        /// <summary>Hit-test the actual projected card, rather than an overlapping minimum-size click target.</summary>
        public static bool Contains(Vector2 point, IList<Vector2> corners)
        {
            if (corners == null || corners.Count != 4) return false;
            bool positive = false, negative = false;
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = corners[i], b = corners[(i + 1) % 4];
                float cross = (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);
                if (cross > 0.001f) positive = true;
                if (cross < -0.001f) negative = true;
                if (positive && negative) return false;
            }
            // A collapsed quad is not a click target (camera/GUI transform unavailable).
            return positive || negative;
        }
    }
}
