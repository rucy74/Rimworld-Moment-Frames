using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>How a photo frame looks. Chosen per frame with a button; the default follows the quality: frames below excellent in the
    /// colour of their material, excellent and better gilded. Saved as its number, so the order must not change.</summary>
    public enum FrameStyle { Material = 0, BlackMat = 1, Gilded = 2, Polaroid = 3, Canvas = 4 }

    /// <summary>Drawing pieces shared by the frames and the collage board: a soft shadow, a moulding with a lit top edge and a shaded bottom edge,
    /// feet for things that stand on the floor, and a quad whose texture is centre-cropped to fill its box. Everything is a flat quad at the
    /// thing's altitude, each layer a hair above the last (8 layers stay far below the next altitude layer, 0.37 apart).</summary>
    [StaticConstructorOnStartup]
    public static class FrameLook
    {
        public const float Layer = 0.003f;
        public const int StyleCount = 5;

        /// <summary>The border of the plain style; a def's photo window is the photo area of that style, the other styles fit inside the same
        /// outer size.</summary>
        public const float MaterialBorder = 0.09f;

        public static readonly Color Gold = new Color(0.80f, 0.64f, 0.22f), GoldLight = new Color(0.98f, 0.86f, 0.46f), GoldDark = new Color(0.48f, 0.35f, 0.10f);
        private static readonly Color Black = new Color(0.07f, 0.07f, 0.08f), MatWhite = new Color(0.93f, 0.91f, 0.86f), CutWhite = new Color(0.99f, 0.98f, 0.95f);
        private static readonly Color Card = new Color(0.95f, 0.94f, 0.90f), Tape = new Color(0.90f, 0.84f, 0.62f, 0.75f), CanvasEdge = new Color(0.22f, 0.18f, 0.14f);

        /// <summary>Border widths in cells: each side, top, bottom.</summary>
        public static void Borders(FrameStyle s, out float side, out float top, out float bottom)
        {
            switch (s)
            {
                case FrameStyle.BlackMat: side = top = bottom = 0.12f; break;
                case FrameStyle.Gilded: side = top = bottom = 0.11f; break;
                case FrameStyle.Polaroid: side = top = 0.05f; bottom = 0.15f; break;
                case FrameStyle.Canvas: side = top = bottom = 0.03f; break;
                default: side = top = bottom = MaterialBorder; break;
            }
        }

        public static string Label(FrameStyle s) => ("MF_Style_" + s).Translate();

        // ---------------- materials ----------------

        private static readonly Dictionary<Color, Material> solids = new Dictionary<Color, Material>();
        private static readonly Dictionary<Color, Material> clears = new Dictionary<Color, Material>();
        private static Texture2D softTex;

        public static Material Solid(Color c)
        {
            if (!solids.TryGetValue(c, out var m)) solids[c] = m = SolidColorMaterials.SimpleSolidColorMaterial(c);
            return m;
        }

        /// <summary>See-through colour (tape).</summary>
        public static Material Clear(Color c)
        {
            if (!clears.TryGetValue(c, out var m)) clears[c] = m = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Transparent, c);
            return m;
        }

        /// <summary>A white square whose alpha falls off towards the edges; tinted black it is a soft shadow of any size.</summary>
        private static Texture2D SoftTex
        {
            get
            {
                if (softTex != null) return softTex;
                const int n = 32;
                softTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "MF_SoftShadow" };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float ex = Mathf.Min(x + 0.5f, n - x - 0.5f) / (n * 0.3f), ey = Mathf.Min(y + 0.5f, n - y - 0.5f) / (n * 0.3f);
                        float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ex)) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ey));
                        px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                softTex.SetPixels32(px);
                softTex.Apply(false, true);
                return softTex;
            }
        }

        private static readonly Dictionary<int, Material> shadowMats = new Dictionary<int, Material>();

        private static Material ShadowMat(float alpha)
        {
            int k = Mathf.RoundToInt(alpha * 100f);
            if (!shadowMats.TryGetValue(k, out var m)) shadowMats[k] = m = MaterialPool.MatFrom(SoftTex, ShaderDatabase.Transparent, new Color(0f, 0f, 0f, k / 100f));
            return m;
        }

        // ---------------- pieces ----------------

        public static void Quad(Vector3 c, float w, float h, Material m, float angle = 0f)
        {
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(c, angle == 0f ? Quaternion.identity : Quaternion.AngleAxis(angle, Vector3.up), new Vector3(w, 1f, h)), m, 0);
        }

        /// <summary>A soft shadow the size of (w, h) plus its blur, moved (dx, dz) from c.</summary>
        public static void Shadow(Vector3 c, float w, float h, float dx, float dz, float alpha, float angle = 0f)
        {
            const float blur = 0.16f;
            Quad(new Vector3(c.x + dx, c.y, c.z + dz), w + blur * 2f, h + blur * 2f, ShadowMat(alpha), angle);
        }

        public static Color Shade(Color c, float f) => new Color(Mathf.Clamp01(c.r * f), Mathf.Clamp01(c.g * f), Mathf.Clamp01(c.b * f), 1f);

        /// <summary>A moulding of width b around a (w, h) opening centred on c: the flat colour, a lit top and left edge, a shaded bottom and right
        /// edge, a dark lip where it meets the picture. Drawn two layers up from c.y; the caller draws the picture above that.</summary>
        public static void Moulding(Vector3 c, float w, float h, float b, Color col, Color light, Color dark)
        {
            float ow = w + 2f * b, oh = h + 2f * b, e = Mathf.Max(0.022f, b * 0.28f);
            Quad(c, ow, oh, Solid(col));
            Vector3 l = c;
            l.y += Layer;
            Quad(new Vector3(c.x, l.y, c.z + oh / 2f - e / 2f), ow, e, Solid(light));
            Quad(new Vector3(c.x - ow / 2f + e / 2f, l.y, c.z), e, oh, Solid(Color.Lerp(light, col, 0.35f)));
            Quad(new Vector3(c.x, l.y + Layer * 0.5f, c.z - oh / 2f + e / 2f), ow, e, Solid(dark));
            Quad(new Vector3(c.x + ow / 2f - e / 2f, l.y + Layer * 0.5f, c.z), e, oh, Solid(Color.Lerp(dark, col, 0.25f)));
            Quad(new Vector3(c.x, l.y + Layer, c.z), w + 0.035f, h + 0.035f, Solid(Shade(col, 0.42f)));
        }

        public static void Moulding(Vector3 c, float w, float h, float b, Color col) => Moulding(c, w, h, b, col, Shade(col, 1.3f), Shade(col, 0.6f));

        /// <summary>Two posts and two feet under a standing board whose bottom edge is at bottomZ (world), with a soft blot on the floor.</summary>
        public static void Feet(Vector3 c, float boardW, float bottomZ, Color col, float scale = 1f)
        {
            float post = 0.07f * scale, postH = 0.16f * scale, foot = 0.24f * scale, footH = 0.05f * scale;
            Shadow(new Vector3(c.x, c.y, bottomZ - postH), boardW * 0.8f, 0.1f * scale, 0f, 0f, 0.35f);
            Color wood = Shade(col, 0.62f), footCol = Shade(col, 0.48f);
            foreach (float sx in new[] { -1f, 1f })
            {
                float x = c.x + sx * boardW * 0.32f;
                Quad(new Vector3(x, c.y + Layer, bottomZ - postH / 2f), post, postH, Solid(wood));
                Quad(new Vector3(x, c.y + Layer * 1.5f, bottomZ - postH + footH / 2f), foot, footH, Solid(footCol));
            }
        }

        // ---------------- a framed photo in any style ----------------

        /// <summary>Tilt of a polaroid, fixed per thing (-3.5..3.5 degrees, never flat).</summary>
        public static float Tilt(int id)
        {
            float t = (int)((uint)(id * 7919) % 8u) - 3.5f;   // unsigned: past id ~271000 the product wraps negative and tilted a frame to -10.5 degrees
            return Mathf.Abs(t) < 1f ? -2f : t;
        }

        /// <summary>Draws the frame and the photo (w, h) centred on photoCentre. photoMat null = an empty frame (blank mat). Returns nothing;
        /// the outer size is <see cref="Borders"/> around (w, h).</summary>
        public static void DrawFramed(FrameStyle s, Vector3 photoCentre, float w, float h, Material photoMat, Color tint, Color stuff, int id, bool onWall)
        {
            Borders(s, out float side, out float top, out float bottom);
            float ow = w + 2f * side, oh = h + top + bottom;
            // the outer box centre: the photo sits higher in it when the bottom border is wider (polaroid)
            Vector3 box = new Vector3(photoCentre.x, photoCentre.y, photoCentre.z - (bottom - top) / 2f);
            float angle = s == FrameStyle.Polaroid ? Tilt(id) : 0f;
            float sdx = onWall ? 0.05f : 0.04f, sdz = onWall ? -0.09f : -0.06f;
            // on a wall the frame stands off it (a firm shadow); a standing board throws a light one on the floor behind it
            Shadow(box, ow, oh, sdx, sdz, onWall ? (s == FrameStyle.Canvas ? 0.55f : 0.45f) : 0.25f, angle);
            Vector3 b = box;
            b.y += Layer;
            Vector3 pc = photoCentre;
            pc.y += Layer * 5f;
            switch (s)
            {
                case FrameStyle.BlackMat:
                {
                    Quad(b, ow, oh, Solid(Black));
                    b.y += Layer;
                    const float edge = 0.035f;
                    Quad(b, ow - 2f * edge, oh - 2f * edge, Solid(MatWhite));
                    b.y += Layer;
                    Quad(new Vector3(b.x, b.y, b.z + (oh - 2f * edge) / 2f - 0.012f), ow - 2f * edge, 0.024f, Solid(Shade(MatWhite, 0.72f)));   // the frame's lip shading the mat
                    Quad(new Vector3(pc.x, b.y, pc.z), w + 0.03f, h + 0.03f, Solid(CutWhite));                                                  // the mat's bevelled cut
                    break;
                }
                case FrameStyle.Gilded:
                {
                    Moulding(b, w, h, side, Gold, GoldLight, GoldDark);
                    b.y += Layer * 3f;
                    float inset = side * 0.45f;
                    // a dark groove inside the moulding and a diamond at each corner
                    Material groove = Solid(Shade(GoldDark, 0.85f));
                    Quad(new Vector3(b.x, b.y, b.z + h / 2f + inset), w + inset * 2f, 0.018f, groove);
                    Quad(new Vector3(b.x, b.y, b.z - h / 2f - inset), w + inset * 2f, 0.018f, groove);
                    Quad(new Vector3(b.x - w / 2f - inset, b.y, b.z), 0.018f, h + inset * 2f, groove);
                    Quad(new Vector3(b.x + w / 2f + inset, b.y, b.z), 0.018f, h + inset * 2f, groove);
                    float d = side * 0.9f;
                    foreach (float sx in new[] { -1f, 1f })
                        foreach (float sz in new[] { -1f, 1f })
                        {
                            var corner = new Vector3(b.x + sx * (ow / 2f - side * 0.5f), b.y + Layer * 0.5f, b.z + sz * (oh / 2f - side * 0.5f));
                            Quad(corner, d, d, Solid(GoldLight), 45f);
                            corner.y += Layer * 0.25f;
                            Quad(corner, d * 0.5f, d * 0.5f, Solid(GoldDark), 45f);
                        }
                    break;
                }
                case FrameStyle.Polaroid:
                {
                    Quad(b, ow, oh, Solid(Card), angle);
                    // the photo turns with the card around the card's centre
                    Vector3 rel = Quaternion.AngleAxis(angle, Vector3.up) * (photoCentre - box);
                    pc = box + rel;
                    pc.y = photoCentre.y + Layer * 5f;
                    Vector3 tape = box + Quaternion.AngleAxis(angle, Vector3.up) * new Vector3(0f, 0f, oh / 2f - 0.01f);
                    tape.y = photoCentre.y + Layer * 6f;
                    Quad(tape, ow * 0.34f, 0.08f, Clear(Tape), angle);
                    break;
                }
                case FrameStyle.Canvas:
                {
                    // no frame: the canvas's own edge, seen on the right and at the bottom
                    Quad(new Vector3(pc.x + 0.03f, b.y, pc.z - 0.03f), w, h, Solid(CanvasEdge));
                    break;
                }
                default:
                    Moulding(b, w, h, side, stuff);
                    break;
            }
            var m = Matrix4x4.TRS(pc, angle == 0f ? Quaternion.identity : Quaternion.AngleAxis(angle, Vector3.up), new Vector3(w, 1f, h));
            if (photoMat != null) Fading.DrawPhoto(m, photoMat, tint);
            else Graphics.DrawMesh(MeshPool.plane10, m, Solid(new Color(0.84f, 0.80f, 0.72f)), 0);
        }

        // ---------------- centre-cropped photos ----------------

        /// <summary>Which part of a trimmed photo shows: the middle, or the start (top of a tall photo, left of a wide one), or the end. Saved.</summary>
        public const int CropMiddle = 0, CropStart = 1, CropEnd = 2;

        /// <summary>The part of a photo (aspect w/h) that fills a box of another aspect: full height or full width, at the given position.
        /// UV rows run bottom-up, so the top of a photo is y = 1.</summary>
        public static Rect CoverUV(float photoAspect, float boxAspect, int pos = CropMiddle)
        {
            if (photoAspect <= 0f || boxAspect <= 0f) return new Rect(0f, 0f, 1f, 1f);
            if (photoAspect > boxAspect)
            {
                float u = boxAspect / photoAspect;
                return new Rect(pos == CropStart ? 0f : pos == CropEnd ? 1f - u : (1f - u) / 2f, 0f, u, 1f);
            }
            float v = photoAspect / boxAspect;
            return new Rect(0f, pos == CropStart ? 1f - v : pos == CropEnd ? 0f : (1f - v) / 2f, 1f, v);
        }

        /// <summary>The share of a photo a box of another aspect shows (1 = nothing trimmed).</summary>
        public static float Visible(float photoAspect, float boxAspect) => photoAspect <= 0f || boxAspect <= 0f ? 1f : Mathf.Min(photoAspect / boxAspect, boxAspect / photoAspect);

        private static readonly Dictionary<long, Mesh> coverMeshes = new Dictionary<long, Mesh>();

        /// <summary>A unit quad (like MeshPool.plane10) showing only that part of the texture; cached per rounded crop (a handful per game).</summary>
        public static Mesh CoverMesh(float photoAspect, float boxAspect, int pos = CropMiddle)
        {
            Rect uv = CoverUV(photoAspect, boxAspect, pos);
            long key = (long)Mathf.RoundToInt(uv.x * 1000f) | ((long)Mathf.RoundToInt(uv.y * 1000f) << 12) | ((long)Mathf.RoundToInt(uv.width * 1000f) << 24) | ((long)Mathf.RoundToInt(uv.height * 1000f) << 36);
            if (coverMeshes.TryGetValue(key, out var mesh)) return mesh;
            mesh = new Mesh { name = "MF_Cover" };
            mesh.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) };
            mesh.uv = new[] { new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMax, uv.yMin) };
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            coverMeshes[key] = mesh;
            return mesh;
        }
    }
}
