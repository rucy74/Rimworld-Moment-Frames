using System;
using System.Collections.Generic;
using UnityEngine;

namespace MomentFrames
{
    /// <summary>Small drawn symbols shared by photo reactions, album information and margin stickers.</summary>
    public enum PhotoIcon
    {
        Camera, Cake, Rings, Group, Music, Projector, Check, Clock, Sleep, Warning,
        Heart, Star, Smile, Exclamation, Wave, Paw, HeartFilled
    }

    /// <summary>Code-drawn transparent white ink, tinted by each UI theme. No emoji or font glyph dependency.</summary>
    internal static class PhotoIconArt
    {
        internal const int TextureSize = 96;
        private const float Canvas = 64f;
        private static readonly Dictionary<PhotoIcon, Texture2D> textures = new Dictionary<PhotoIcon, Texture2D>();

        internal static Texture2D Texture(PhotoIcon icon)
        {
            if (textures.TryGetValue(icon, out var cached) && cached != null) return cached;
            var ink = new Ink();
            DrawSymbol(ink, icon);
            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            {
                name = "MF_PhotoIcon_" + icon,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(ink.Pixels());
            // Reactions may read the alpha mask when baking into a new photograph.
            texture.Apply(false, false);
            textures[icon] = texture;
            return texture;
        }

        internal static void Draw(Rect rect, PhotoIcon icon, Color color)
        {
            Color previous = GUI.color;
            try
            {
                GUI.color = color;
                GUI.DrawTexture(rect, Texture(icon), ScaleMode.ScaleToFit, true);
            }
            finally { GUI.color = previous; }
        }

        private static void DrawSymbol(Ink p, PhotoIcon icon)
        {
            switch (icon)
            {
                case PhotoIcon.Camera:
                    p.Path(9, 23, 18, 22, 22, 15, 39, 16, 43, 23, 55, 24, 54, 49, 10, 50, 9, 23);
                    p.Ellipse(33, 36, 11, 10.5f);
                    p.Ellipse(33, 36, 7, 7, 1.1f);
                    p.Line(45, 29, 49, 29, 1.7f);
                    break;
                case PhotoIcon.Cake:
                    p.Path(12, 32, 51, 32, 52, 49, 12, 50, 12, 32);
                    p.Curve(12, 37, 17, 46, 20, 33, 25, 39);
                    p.Curve(25, 39, 30, 46, 33, 33, 38, 39);
                    p.Curve(38, 39, 43, 46, 47, 33, 51, 38);
                    p.Line(9, 54, 55, 53);
                    p.Path(29, 31, 29, 23, 34, 23, 34, 31);
                    p.Curve(31, 20, 26, 16, 31, 10, 32, 9);
                    p.Curve(32, 9, 37, 15, 36, 19, 31, 20);
                    break;
                case PhotoIcon.Rings:
                    p.Ellipse(24, 37, 15, 15);
                    p.Ellipse(41, 35, 14, 14);
                    p.Path(36, 19, 39, 12, 47, 13, 49, 20, 36, 19);
                    p.Path(39, 12, 43, 19, 47, 13);
                    p.Line(30, 12, 27, 8, 1.2f);
                    p.Line(51, 11, 55, 8, 1.2f);
                    break;
                case PhotoIcon.Group:
                    // Overlapping photographs, deliberately avoiding the old positioning silhouettes.
                    p.Path(9, 22, 38, 17, 44, 46, 15, 51, 9, 22);
                    p.Path(22, 13, 54, 18, 50, 50, 19, 45, 22, 13);
                    p.Path(25, 19, 49, 23, 46, 40, 24, 37, 25, 19);
                    p.Path(25, 34, 32, 28, 37, 34, 41, 30, 47, 36);
                    p.Ellipse(42, 26, 2, 2, 1.1f);
                    break;
                case PhotoIcon.Music:
                    p.Path(25, 43, 25, 18, 49, 12, 48, 39);
                    p.Line(25, 25, 48, 19);
                    p.Ellipse(19, 45, 7, 5, 2.2f);
                    p.Ellipse(42, 41, 7, 5, 2.2f);
                    break;
                case PhotoIcon.Projector:
                    p.Ellipse(23, 17, 9, 9);
                    p.Ellipse(42, 18, 9, 9);
                    p.Ellipse(23, 17, 3, 3, 1.2f);
                    p.Ellipse(42, 18, 3, 3, 1.2f);
                    p.Path(12, 29, 43, 28, 43, 46, 13, 47, 12, 29);
                    p.Path(43, 33, 53, 30, 53, 44, 43, 41);
                    p.Path(29, 47, 29, 54, 19, 57, 41, 57);
                    p.Line(57, 28, 61, 26, 1.1f);
                    p.Line(57, 47, 61, 49, 1.1f);
                    break;
                case PhotoIcon.Check:
                    p.Path(13, 33, 25, 45, 51, 17, 2.5f);
                    break;
                case PhotoIcon.Clock:
                    p.Ellipse(32, 32, 22, 22);
                    p.Path(31, 17, 31, 33, 43, 39, 2f);
                    p.Line(32, 11, 32, 14, 1.1f);
                    p.Line(51, 32, 53, 32, 1.1f);
                    break;
                case PhotoIcon.Sleep:
                    p.Curve(42, 10, 8, 7, 3, 50, 36, 53);
                    p.Curve(36, 53, 43, 53, 49, 50, 51, 46);
                    p.Curve(51, 46, 27, 53, 18, 21, 42, 10);
                    p.Path(44, 19, 53, 19, 44, 29, 54, 29, 1.3f);
                    break;
                case PhotoIcon.Warning:
                    p.Path(31, 9, 57, 54, 7, 54, 31, 9, 1.8f);
                    p.Line(32, 25, 32, 38, 2.3f);
                    p.Dot(32, 46, 1.5f);
                    break;
                case PhotoIcon.HeartFilled:
                    p.FilledCurves(32,53, 26,47,9,35,9,23, 10,9,25,9,32,21,
                        40,8,55,12,55,24, 56,37,39,48,32,53);
                    goto case PhotoIcon.Heart;
                case PhotoIcon.Heart:
                    p.Curve(32, 53, 26, 47, 9, 35, 9, 23, 2f);
                    p.Curve(9, 23, 10, 9, 25, 9, 32, 21, 2f);
                    p.Curve(32, 21, 40, 8, 55, 12, 55, 24, 2f);
                    p.Curve(55, 24, 56, 37, 39, 48, 32, 53, 2f);
                    break;
                case PhotoIcon.Star:
                    p.Path(32, 8, 39, 24, 57, 26, 43, 38, 47, 56, 32, 46, 16, 56, 21, 38, 7, 26, 25, 24, 32, 8, 1.9f);
                    break;
                case PhotoIcon.Smile:
                    p.Ellipse(32, 32, 22, 22, 1.9f);
                    p.Dot(24, 26, 1.55f);
                    p.Dot(40, 26, 1.55f);
                    p.Curve(21, 36, 25, 47, 40, 48, 44, 35, 1.9f);
                    break;
                case PhotoIcon.Exclamation:
                    p.Path(30, 10, 32, 38, 35, 10, 30, 10, 1.5f);
                    p.Ellipse(32, 49, 2.6f, 2.6f, 1.5f);
                    break;
                case PhotoIcon.Wave:
                    p.Curve(9, 21, 21, 8, 39, 33, 55, 20, 2f);
                    p.Curve(9, 33, 21, 20, 39, 45, 55, 32, 2f);
                    p.Curve(9, 45, 21, 32, 39, 57, 55, 44, 2f);
                    break;
                case PhotoIcon.Paw:
                    p.Ellipse(17, 26, 4.8f, 6.2f, 1.9f);
                    p.Ellipse(27, 16, 4.8f, 6.5f, 1.9f);
                    p.Ellipse(39, 16, 4.8f, 6.5f, 1.9f);
                    p.Ellipse(49, 26, 4.8f, 6.2f, 1.9f);
                    p.Curve(20, 40, 22, 38, 24, 31, 32, 31, 1.9f);
                    p.Curve(32, 31, 40, 31, 42, 38, 44, 40, 1.9f);
                    p.Curve(44, 40, 52, 48, 46, 57, 39, 53, 1.9f);
                    p.Curve(39, 53, 34, 49, 30, 49, 25, 53, 1.9f);
                    p.Curve(25, 53, 18, 57, 12, 48, 20, 40, 1.9f);
                    break;
                default:
                    p.Ellipse(32, 32, 18, 18);
                    break;
            }
        }

        /// <summary>Distance-to-stroke rasterization with a soft subpixel edge. Coordinates use a 64-unit sheet.</summary>
        private sealed class Ink
        {
            private readonly float[] alpha = new float[TextureSize * TextureSize];
            private const float Edge = .55f;
            private static float Scale => TextureSize / Canvas;

            internal void Line(float x0, float y0, float x1, float y1, float width = 1.65f)
            {
                float radius = width * .5f;
                float margin = radius + Edge;
                int left = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(x0, x1) - margin) * Scale), 0, TextureSize - 1);
                int right = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(x0, x1) + margin) * Scale), 0, TextureSize - 1);
                int top = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(y0, y1) - margin) * Scale), 0, TextureSize - 1);
                int bottom = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(y0, y1) + margin) * Scale), 0, TextureSize - 1);
                float dx = x1 - x0, dy = y1 - y0, length = dx * dx + dy * dy;
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                    {
                        float px = (x + .5f) / Scale, py = (y + .5f) / Scale;
                        float along = length > .00001f ? Mathf.Clamp01(((px - x0) * dx + (py - y0) * dy) / length) : 0f;
                        float distance = Mathf.Sqrt((px - x0 - dx * along) * (px - x0 - dx * along)
                            + (py - y0 - dy * along) * (py - y0 - dy * along));
                        float coverage = Mathf.Clamp01((radius + Edge - distance) / (2f * Edge));
                        // Unity's pixel origin is bottom-left; drawing coordinates start at the top.
                        int at = (TextureSize - 1 - y) * TextureSize + x;
                        alpha[at] = Mathf.Max(alpha[at], coverage);
                    }
            }

            internal void Path(params float[] points)
            {
                float width = points.Length % 2 == 1 ? points[points.Length - 1] : 1.65f;
                int length = points.Length % 2 == 1 ? points.Length - 1 : points.Length;
                for (int i = 2; i < length; i += 2) Line(points[i - 2], points[i - 1], points[i], points[i + 1], width);
            }

            internal void Dot(float x, float y, float radius) => Line(x, y, x, y, radius * 2f);

            internal void Ellipse(float cx, float cy, float rx, float ry, float width = 1.65f)
            {
                const int steps = 56;
                for (int i = 0; i < steps; i++)
                {
                    float a = i * Mathf.PI * 2f / steps, b = (i + 1) * Mathf.PI * 2f / steps;
                    Line(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry, cx + Mathf.Cos(b) * rx, cy + Mathf.Sin(b) * ry, width);
                }
            }

            internal void Curve(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, float width = 1.65f)
            {
                const int steps = 28;
                float lastX = x0, lastY = y0;
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps, u = 1f - t;
                    float x = u * u * u * x0 + 3f * u * u * t * x1 + 3f * u * t * t * x2 + t * t * t * x3;
                    float y = u * u * u * y0 + 3f * u * u * t * y1 + 3f * u * t * t * y2 + t * t * t * y3;
                    Line(lastX, lastY, x, y, width);
                    lastX = x; lastY = y;
                }
            }

            internal void FilledCurves(params float[] points)
            {
                var outline=new List<Vector2>{new Vector2(points[0],points[1])};
                for(int at=2;at+5<points.Length;at+=6)
                {
                    Vector2 start=outline[outline.Count-1],a=new Vector2(points[at],points[at+1]),
                        b=new Vector2(points[at+2],points[at+3]),end=new Vector2(points[at+4],points[at+5]);
                    for(int i=1;i<=28;i++)
                    {
                        float t=i/28f,u=1f-t;
                        outline.Add(u*u*u*start+3f*u*u*t*a+3f*u*t*t*b+t*t*t*end);
                    }
                }
                for(int y=0;y<TextureSize;y++)for(int x=0;x<TextureSize;x++)
                {
                    Vector2 point=new Vector2((x+.5f)/Scale,(y+.5f)/Scale);
                    bool inside=false;float nearest=float.MaxValue;
                    for(int i=0,j=outline.Count-1;i<outline.Count;j=i++)
                    {
                        Vector2 a=outline[j],b=outline[i],line=b-a;
                        if((a.y>point.y)!=(b.y>point.y) && point.x<(b.x-a.x)*(point.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
                        float t=line.sqrMagnitude>.00001f?Mathf.Clamp01(Vector2.Dot(point-a,line)/line.sqrMagnitude):0f;
                        nearest=Mathf.Min(nearest,(point-a-t*line).magnitude);
                    }
                    float coverage=Mathf.Clamp01(.5f+(inside?nearest:-nearest)/(2f*Edge));
                    int at=(TextureSize-1-y)*TextureSize+x;
                    alpha[at]=Mathf.Max(alpha[at],coverage);
                }
            }

            internal Color32[] Pixels()
            {
                var pixels = new Color32[alpha.Length];
                for (int i = 0; i < alpha.Length; i++) pixels[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha[i] * 255f));
                return pixels;
            }
        }
    }
}
