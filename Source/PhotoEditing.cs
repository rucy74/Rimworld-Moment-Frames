using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The few looks a photo can be given. Pure functions on pixels, so the editor preview and the baked file are the same picture.</summary>
    public static class PhotoFilters
    {
        public const string None = "none", Mono = "mono", Sepia = "sepia", Warm = "warm", Cool = "cool", Faded = "faded";
        public static readonly string[] All = { None, Mono, Sepia, Warm, Cool, Faded };

        public static bool Known(string f) => Array.IndexOf(All, f) >= 0;

        private static byte B(float v) => (byte)(v <= 0f ? 0 : v >= 255f ? 255 : (int)(v + 0.5f));

        public static Color32 One(Color32 c, string filter)
        {
            float r = c.r, g = c.g, b = c.b;
            switch (filter)
            {
                case Mono:
                {
                    byte l = B(0.299f * r + 0.587f * g + 0.114f * b);
                    return new Color32(l, l, l, c.a);
                }
                case Sepia:
                    return new Color32(B(0.393f * r + 0.769f * g + 0.189f * b), B(0.349f * r + 0.686f * g + 0.168f * b),
                        B(0.272f * r + 0.534f * g + 0.131f * b), c.a);
                case Warm:
                    return new Color32(B(r * 1.08f + 8f), B(g * 1.02f + 2f), B(b * 0.86f), c.a);
                case Cool:
                    return new Color32(B(r * 0.88f), B(g * 1.0f + 2f), B(b * 1.1f + 10f), c.a);
                case Faded:
                {
                    // lifted blacks, softer contrast, a quarter of the colour gone, a faint yellow cast: an old print
                    float l = 0.299f * r + 0.587f * g + 0.114f * b;
                    r = l + (r - l) * 0.72f; g = l + (g - l) * 0.72f; b = l + (b - l) * 0.72f;
                    return new Color32(B(38f + r * 0.8f + 6f), B(36f + g * 0.78f + 3f), B(30f + b * 0.72f), c.a);
                }
                default:
                    return c;
            }
        }

        public static void Apply(Color32[] px, string filter)
        {
            if (px == null || filter.NullOrEmpty() || filter == None) return;
            for (int i = 0; i < px.Length; i++) px[i] = One(px[i], filter);
        }

        public static string Label(string filter) => ("MF_Filter_" + (filter ?? None)).Translate();
    }

    public static class PhotoEditing
    {
        public const float MinCrop = 0.06f;   // a crop is at least this fraction of the photo in each direction

        /// <summary>The crop kept inside the photo and not smaller than MinCrop. Fractions, origin top-left.</summary>
        public static Rect Clamp(Rect crop)
        {
            float w = Mathf.Clamp(crop.width, MinCrop, 1f), h = Mathf.Clamp(crop.height, MinCrop, 1f);
            float x = Mathf.Clamp(crop.x, 0f, 1f - w), y = Mathf.Clamp(crop.y, 0f, 1f - h);
            return new Rect(x, y, w, h);
        }

        /// <summary>Fits both dimensions together so a locked ratio survives the image and minimum-size limits.</summary>
        internal static Rect FitLocked(Rect crop, float ratio)
        {
            float w = Mathf.Clamp(crop.width, Mathf.Max(MinCrop, MinCrop * ratio), Mathf.Min(1f, ratio));
            float h = w / ratio;
            Vector2 center = crop.center;
            return Clamp(new Rect(center.x - w / 2f, center.y - h / 2f, w, h));
        }

        /// <summary>A locked corner keeps its opposite corner fixed; width leads, as in the editor's original drag behavior.</summary>
        internal static Rect ResizeLocked(Rect crop, int corner, float dx, float ratio)
        {
            bool left = corner == 2 || corner == 4, top = corner == 2 || corner == 3;
            float x = left ? crop.xMax : crop.x, y = top ? crop.yMax : crop.y;
            float availableW = left ? x : 1f - x, availableH = top ? y : 1f - y;
            float minW = Mathf.Max(MinCrop, MinCrop * ratio);
            float maxW = Mathf.Min(availableW, availableH * ratio);
            float w = Mathf.Clamp(crop.width + (left ? -dx : dx), minW, maxW), h = w / ratio;
            return new Rect(left ? x - w : x, top ? y - h : y, w, h);
        }

        /// <summary>Pixel rectangle of a crop in a texture of this size, in texture coordinates (rows counted from the bottom).</summary>
        public static RectInt Pixels(Rect crop, int width, int height)
        {
            crop = Clamp(crop);
            int x = Mathf.Clamp(Mathf.RoundToInt(crop.x * width), 0, width - 1);
            int top = Mathf.Clamp(Mathf.RoundToInt(crop.y * height), 0, height - 1);
            int w = Mathf.Clamp(Mathf.RoundToInt(crop.width * width), 1, width - x);
            int h = Mathf.Clamp(Mathf.RoundToInt(crop.height * height), 1, height - top);
            return new RectInt(x, height - top - h, w, h);
        }

        /// <summary>Crops and filters a photo (PNG or JPEG), returning the new image in the "Photo file format" setting and its width/height.
        /// Only ever reads the source bytes.</summary>
        public static byte[] Bake(byte[] source, Rect crop, string filter, out float aspect, List<PhotoImageIcon> icons = null)
        {
            aspect = PhotoCapture.DefaultAspect;
            var src = new Texture2D(2, 2, TextureFormat.RGB24, false);
            Texture2D dst = null;
            try
            {
                if (source == null || !src.LoadImage(source, false)) return null;
                RectInt r = Pixels(crop, src.width, src.height);
                Color32[] all = src.GetPixels32();
                var px = new Color32[r.width * r.height];
                for (int row = 0; row < r.height; row++)
                    Array.Copy(all, (r.y + row) * src.width + r.x, px, row * r.width, r.width);
                all = null;
                PhotoFilters.Apply(px, filter);
                PhotoImageIcons.Composite(px, r.width, r.height, r, src.width, src.height, icons);
                aspect = (float)r.width / r.height;
                // Color32 rows run bottom up, as PhotoPixels keeps them.
                var rgb = new byte[px.Length * 3];
                for (int i = 0, o = 0; i < px.Length; i++, o += 3) { rgb[o] = px[i].r; rgb[o + 1] = px[i].g; rgb[o + 2] = px[i].b; }
                return PhotoImageFormat.Encode(new PhotoPixels(r.width, r.height, rgb), PhotoImageFormat.SaveJpeg);
            }
            finally
            {
                UnityEngine.Object.Destroy(src);
                if (dst != null) UnityEngine.Object.Destroy(dst);
            }
        }
    }

    /// <summary>Editor for one photo: drag a crop on the picture, pick a look, save it as a new edited version under the original. Always starts
    /// from the original, so edits never stack and nothing degrades. The preview is a small copy filtered on the CPU with the same function the
    /// saved file goes through.</summary>
    public partial class Dialog_PhotoEdit : Window
    {
        private const float PanelW = 230f, MaxPreview = 1024f, Handle = 16f;
        private const float MinZoom = 0.25f, MaxZoom = 8f, ZoomStep = 1.25f;
        private enum Lock { Free, Original, Square, Wide, Tall }

        private readonly PhotoRecord rec;
        private readonly PhotoEdit editingBase;
        private Texture2D preview;
        private Color32[] basePx;
        private float srcAspect = PhotoCapture.DefaultAspect;
        private string filter = PhotoFilters.None;
        private Rect crop = new Rect(0f, 0f, 1f, 1f);   // fractions, origin top-left
        private Lock lockMode = Lock.Free;
        private int dragging;                              // 0 none, 1 move, 2 top-left, 3 top-right, 4 bottom-left, 5 bottom-right
        private Vector2 dragFrom;
        private Rect cropFrom;
        private float viewZoom = 1f;                       // relative to fitting the whole photo, independent of the saved crop
        private Vector2 viewPan;
        private bool panDragging, spaceHeld;
        private int panButton;
        private Vector2 viewDragFrom, panFrom;
        private bool failed;
        private List<PhotoImageIcon> imageIcons = new List<PhotoImageIcon>();
        private bool editIcons;
        private readonly bool startedWithIcons;
        private int selectedIcon = -1;
        private bool iconDragging;
        private Vector2 iconDragFrom, iconPositionFrom;
        private Vector2 panelScroll;

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1180f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 60f));

        public Dialog_PhotoEdit(PhotoRecord rec, string version = null, bool startIcons = false)
        {
            this.rec = rec;
            var edit = version == null ? rec.Shown : rec.Edit(version);
            editingBase = edit;
            if (edit != null)
            {
                crop = PhotoEditing.Clamp(new Rect(edit.cx, edit.cy, edit.cw, edit.ch));
                filter = PhotoFilters.Known(edit.filter) ? edit.filter : PhotoFilters.None;
                imageIcons = PhotoImageIcons.Copy(edit.imageIcons);
                startedWithIcons = imageIcons.Count > 0;
            }
            editIcons = startIcons;
            initialEdit=Snapshot();
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            draggable = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            try { LoadPreview(); }
            catch (Exception e)
            {
                failed = true;
                Log.Warning("[MomentFrames] could not open the photo for editing: " + e.Message);
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (preview != null) UnityEngine.Object.Destroy(preview);
            preview = null;
            basePx = null;
            panDragging = spaceHeld = false;
            dragging = 0;
        }

        /// <summary>A readable copy of the original, at most MaxPreview wide, scaled on the GPU.</summary>
        private void LoadPreview()
        {
            string path = PhotoStore.Instance?.OriginalPath(rec);
            if (path == null || !File.Exists(path)) { failed = true; return; }
            var full = new Texture2D(2, 2, TextureFormat.RGB24, false);
            RenderTexture rt = null;
            var previous = RenderTexture.active;
            try
            {
                if (!full.LoadImage(File.ReadAllBytes(path), false)) { failed = true; return; }
                srcAspect = (float)full.width / Mathf.Max(1, full.height);
                float scale = Mathf.Min(1f, MaxPreview / Mathf.Max(full.width, full.height));
                int w = Mathf.Max(8, Mathf.RoundToInt(full.width * scale)), h = Mathf.Max(8, Mathf.RoundToInt(full.height * scale));
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(full, rt);
                RenderTexture.active = rt;
                preview = new Texture2D(w, h, TextureFormat.RGB24, false);
                preview.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                preview.Apply(false, false);
                RenderTexture.active = previous;
                basePx = preview.GetPixels32();
                if (filter != PhotoFilters.None)
                {
                    var filtered = (Color32[])basePx.Clone();
                    PhotoFilters.Apply(filtered, filter);
                    preview.SetPixels32(filtered);
                    preview.Apply(false, false);
                }
            }
            finally
            {
                try { RenderTexture.active = previous; }
                finally
                {
                    try { if (rt != null) RenderTexture.ReleaseTemporary(rt); }
                    finally { UnityEngine.Object.Destroy(full); }
                }
            }
        }

        private void SetFilter(string f)
        {
            if (f == filter || preview == null || basePx == null) return;
            filter = f;
            var px = (Color32[])basePx.Clone();
            PhotoFilters.Apply(px, filter);
            preview.SetPixels32(px);
            preview.Apply(false, false);
        }

        /// <summary>Width/height of the crop in pixels for a lock, as a fraction ratio (cw/ch) of the original.</summary>
        private float LockRatio()
        {
            float pixelRatio;
            switch (lockMode)
            {
                case Lock.Original: pixelRatio = srcAspect; break;
                case Lock.Square: pixelRatio = 1f; break;
                case Lock.Wide: pixelRatio = 1.5f; break;
                case Lock.Tall: pixelRatio = 2f / 3f; break;
                default: return 0f;
            }
            return pixelRatio / srcAspect;
        }

        /// <summary>The largest crop of the locked shape around the current centre.</summary>
        private void FitLock()
        {
            float ratio = LockRatio();
            if (ratio <= 0f) return;
            crop = PhotoEditing.FitLocked(crop, ratio);
        }

        private void DrawEditor(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 40f, 34f), "MF_EditTitle".Translate(rec.title.NullOrEmpty() ? rec.dateLabel : rec.title));
            Text.Font = GameFont.Small;
            Rect canvas = new Rect(0f, 42f, inRect.width - PanelW - 16f, inRect.height - 42f);
            Rect panel = new Rect(canvas.xMax + 16f, 42f, PanelW, inRect.height - 42f);
            Widgets.DrawBoxSolid(canvas, new Color(0.08f, 0.07f, 0.06f));

            if (failed || preview == null)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(canvas, "MF_MissingFile".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonText(new Rect(panel.x, panel.yMax - 36f, panel.width, 32f), "CloseButton".Translate())) Close();
                return;
            }

            Rect viewport = Viewport(canvas);
            Rect area = new Rect(0f, 0f, viewport.width, viewport.height);
            DrawViewControls(canvas, area);
            GUI.BeginGroup(viewport);
            try
            {
                HandleViewportInput(area);
                Rect img = ViewImage(area);
                GUI.DrawTexture(img, preview);
                DrawImageIcons(img);
                DrawCrop(img);
                if (editIcons && selectedIcon >= 0 && selectedIcon < imageIcons.Count)
                    Widgets.DrawBox(PhotoImageIcons.OnImage(imageIcons[selectedIcon], img).ExpandedBy(3f), 2);
            }
            finally { GUI.EndGroup(); }
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            Rect hint = new Rect(canvas.x + 12f, viewport.yMax + 12f, canvas.width - 24f, canvas.yMax - viewport.yMax - 24f);
            Widgets.Label(hint, ViewGuide());
            TooltipHandler.TipRegion(hint, "MF_EditViewHelp".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            DrawPanel(panel);
        }

        private string ViewGuide() => "MF_EditViewHint".Translate() + "\n" + "MF_EditViewPanHint".Translate() + "\n"
            + (editIcons ? "MF_Phase2_IconMoveHint" : "MF_EditViewCropHint").Translate();

        private Rect Viewport(Rect canvas)
        {
            Text.Font = GameFont.Small;
            float guideHeight = Text.CalcHeight(ViewGuide(), Mathf.Max(1f, canvas.width - 24f));
            return new Rect(canvas.x, canvas.y + 42f, Mathf.Max(1f, canvas.width), Mathf.Max(1f, canvas.height - 42f - guideHeight - 24f));
        }

        private void DrawViewControls(Rect canvas, Rect area)
        {
            float x = canvas.x + 12f, y = canvas.y + 6f;
            if (Widgets.ButtonText(new Rect(x, y, 32f, 28f), "-")) ZoomView(area, 1f / ZoomStep, area.center);
            if (Widgets.ButtonText(new Rect(x + 36f, y, 32f, 28f), "+")) ZoomView(area, ZoomStep, area.center);
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect percent = new Rect(x + 72f, y, 68f, 28f);
            Widgets.Label(percent, Mathf.RoundToInt(viewZoom * 100f) + "%");
            TooltipHandler.TipRegion(percent, "MF_EditViewScaleHelp".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonText(new Rect(x + 146f, y, 106f, 28f), "MF_EditViewFit".Translate())) FitView();
        }

        /// <summary>The photo in viewport-local coordinates. Keep each enlarged axis over the viewport; smaller axes stay centred.</summary>
        private Rect ViewImage(Rect area)
        {
            float aspect = preview != null ? (float)preview.width / preview.height : srcAspect;
            Rect fit = Fit(area.ContractedBy(12f), aspect);
            Vector2 size = fit.size * viewZoom;
            float maxX = Mathf.Max(0f, (size.x - area.width) / 2f), maxY = Mathf.Max(0f, (size.y - area.height) / 2f);
            viewPan = new Vector2(Mathf.Clamp(viewPan.x, -maxX, maxX), Mathf.Clamp(viewPan.y, -maxY, maxY));
            return new Rect(area.center + viewPan - size / 2f, size);
        }

        private void ZoomView(Rect area, float factor, Vector2 anchor)
        {
            Rect before = ViewImage(area);
            float next = Mathf.Clamp(viewZoom * factor, MinZoom, MaxZoom);
            if (Mathf.Approximately(next, viewZoom)) return;
            Vector2 point = new Vector2((anchor.x - before.x) / before.width, (anchor.y - before.y) / before.height);
            viewZoom = next;
            Vector2 size = ViewImage(area).size;
            viewPan = anchor - area.center - Vector2.Scale(point - new Vector2(0.5f, 0.5f), size);
            ViewImage(area); // clamp after anchoring, including zooming out to a photo smaller than the viewport
        }

        private void FitView()
        {
            viewZoom = 1f;
            viewPan = Vector2.zero;
            panDragging = false;
            dragging = 0;
        }

        private void HandleViewportInput(Rect area)
        {
            Event ev = Event.current;
            HandleViewInput(area);
            if (ev.type != EventType.Used && (area.Contains(ev.mousePosition) || dragging != 0 || iconDragging))
            {
                if (editIcons) HandleIconMouse(ViewImage(area));
                else HandleMouse(ViewImage(area));
            }
            if (ev.rawType == EventType.MouseUp) { dragging = 0; iconDragging = false; }
        }

        private void HandleViewInput(Rect area)
        {
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Space)
            {
                spaceHeld = true;
                ev.Use();
            }
            else if (ev.type == EventType.KeyUp && ev.keyCode == KeyCode.Space)
            {
                spaceHeld = false;
                ev.Use();
            }
            else if (ev.type == EventType.MouseLeaveWindow)
            {
                spaceHeld = false;
            }
            else if (ev.rawType == EventType.MouseUp && panDragging && ev.button == panButton)
            {
                panDragging = false;
                ev.Use();
            }
            else if (ev.type == EventType.MouseDrag && panDragging && ev.button == panButton)
            {
                viewPan = panFrom + ev.mousePosition - viewDragFrom;
                ViewImage(area);
                ev.Use();
            }
            else if (area.Contains(ev.mousePosition))
            {
                if (ev.type == EventType.ScrollWheel)
                {
                    if (dragging == 0 && !panDragging) ZoomView(area, Mathf.Pow(ZoomStep, -ev.delta.y), ev.mousePosition);
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDown && (ev.button == 2 || (ev.button == 0 && spaceHeld)))
                {
                    dragging = 0;
                    iconDragging = false;
                    panDragging = true;
                    panButton = ev.button;
                    viewDragFrom = ev.mousePosition;
                    panFrom = viewPan;
                    ev.Use();
                }
            }
        }

        private static Rect Fit(Rect area, float aspect)
        {
            float w = area.width, h = w / aspect;
            if (h > area.height) { h = area.height; w = h * aspect; }
            return new Rect(area.x + (area.width - w) / 2f, area.y + (area.height - h) / 2f, w, h);
        }

        private Rect CropOnScreen(Rect img) =>
            new Rect(img.x + crop.x * img.width, img.y + crop.y * img.height, crop.width * img.width, crop.height * img.height);

        private void DrawCrop(Rect img)
        {
            Rect c = CropOnScreen(img);
            var shade = new Color(0f, 0f, 0f, 0.55f);
            Widgets.DrawBoxSolid(new Rect(img.x, img.y, img.width, c.y - img.y), shade);
            Widgets.DrawBoxSolid(new Rect(img.x, c.yMax, img.width, img.yMax - c.yMax), shade);
            Widgets.DrawBoxSolid(new Rect(img.x, c.y, c.x - img.x, c.height), shade);
            Widgets.DrawBoxSolid(new Rect(c.xMax, c.y, img.xMax - c.xMax, c.height), shade);
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            Widgets.DrawBox(c, 2);
            // thirds, to help composing
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            for (int i = 1; i <= 2; i++)
            {
                Widgets.DrawLineVertical(c.x + c.width * i / 3f, c.y, c.height);
                Widgets.DrawLineHorizontal(c.x, c.y + c.height * i / 3f, c.width);
            }
            GUI.color = Color.white;
            foreach (var h in Corners(c)) Widgets.DrawBoxSolid(h, Color.white);
        }

        private static Rect[] Corners(Rect c)
        {
            float s = Handle;
            return new[]
            {
                new Rect(c.x - s / 2f, c.y - s / 2f, s, s), new Rect(c.xMax - s / 2f, c.y - s / 2f, s, s),
                new Rect(c.x - s / 2f, c.yMax - s / 2f, s, s), new Rect(c.xMax - s / 2f, c.yMax - s / 2f, s, s)
            };
        }

        private void HandleMouse(Rect img)
        {
            Event ev = Event.current;
            Vector2 m = ev.mousePosition;
            Rect c = CropOnScreen(img);
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                var corners = Corners(c);
                int hit = 0;
                for (int i = 0; i < corners.Length; i++) if (corners[i].ExpandedBy(4f).Contains(m)) { hit = i + 2; break; }
                if (hit == 0 && c.Contains(m)) hit = 1;
                if (hit != 0)
                {
                    dragging = hit;
                    dragFrom = m;
                    cropFrom = crop;
                    ev.Use();
                }
            }
            else if (ev.type == EventType.MouseDrag && dragging != 0)
            {
                float dx = (m.x - dragFrom.x) / img.width, dy = (m.y - dragFrom.y) / img.height;
                Rect r = cropFrom;
                if (dragging == 1)
                {
                    r.x += dx; r.y += dy;
                    crop = PhotoEditing.Clamp(r);
                }
                else
                {
                    float ratio = LockRatio();
                    if (ratio > 0f)
                    {
                        crop = PhotoEditing.ResizeLocked(r, dragging, dx, ratio);
                        ev.Use();
                        return;
                    }
                    float x0 = r.x, y0 = r.y, x1 = r.xMax, y1 = r.yMax;
                    if (dragging == 2 || dragging == 4) x0 += dx; else x1 += dx;
                    if (dragging == 2 || dragging == 3) y0 += dy; else y1 += dy;
                    x0 = Mathf.Clamp01(x0); x1 = Mathf.Clamp01(x1); y0 = Mathf.Clamp01(y0); y1 = Mathf.Clamp01(y1);
                    float w = Mathf.Max(PhotoEditing.MinCrop, x1 - x0), h = Mathf.Max(PhotoEditing.MinCrop, y1 - y0);
                    // keep the corner opposite the dragged one where it was
                    float left = dragging == 2 || dragging == 4 ? r.xMax - w : r.x;
                    float top = dragging == 2 || dragging == 3 ? r.yMax - h : r.y;
                    crop = PhotoEditing.Clamp(new Rect(left, top, w, h));
                }
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp && dragging != 0)
            {
                dragging = 0;
                ev.Use();
            }
        }

        internal void OpenStickers() => Find.WindowStack.Add(new Dialog_AlbumStickers(rec, editingBase?.id ?? ""));

        private void DrawPanel(Rect panel)
        {
            float half = (panel.width - 6f) / 2f;
            if (Widgets.ButtonText(new Rect(panel.x, panel.y, half, 32f), "MF_Phase2_EditPhotoTab".Translate())) { editIcons = false; iconDragging = false; }
            if (Widgets.ButtonText(new Rect(panel.x + half + 6f, panel.y, half, 32f), "MF_Phase2_EditIconTab".Translate())) { editIcons = true; dragging = 0; }
            if (Widgets.ButtonText(new Rect(panel.x, panel.y + 38f, panel.width, 32f), "MF_Phase2_MarginStickers".Translate())) OpenStickers();
            Rect viewport = new Rect(panel.x, panel.y + 78f, panel.width, Mathf.Max(1f, panel.height - 232f));
            Rect view = new Rect(0f, 0f, panel.width - 16f, editIcons ? 400f : 490f);
            Widgets.BeginScrollView(viewport, ref panelScroll, view);
            if (editIcons) DrawIconPanel(view);
            else DrawPhotoOptions(view);
            Widgets.EndScrollView();
            DrawSaveControls(panel);
        }

        private void DrawPhotoOptions(Rect panel)
        {
            float y = panel.y;
            Widgets.Label(new Rect(panel.x, y, panel.width, 24f), "MF_EditFilter".Translate());
            y += 26f;
            foreach (var f in PhotoFilters.All)
            {
                if (Widgets.RadioButtonLabeled(new Rect(panel.x, y, panel.width, 26f), PhotoFilters.Label(f), filter == f)) SetFilter(f);
                y += 28f;
            }
            y += 10f;
            Widgets.Label(new Rect(panel.x, y, panel.width, 24f), "MF_EditCrop".Translate());
            y += 26f;
            foreach (Lock l in Enum.GetValues(typeof(Lock)))
            {
                if (Widgets.RadioButtonLabeled(new Rect(panel.x, y, panel.width, 26f), ("MF_EditLock_" + l).Translate(), lockMode == l))
                {
                    lockMode = l;
                    FitLock();
                }
                y += 28f;
            }
            y += 8f;
            if (Widgets.ButtonText(new Rect(panel.x, y, panel.width, 30f), "MF_UX_ResetCrop".Translate())) ResetCrop();
            if (Widgets.ButtonText(new Rect(panel.x,y+38f,panel.width,30f),"MF_UX_ResetFilter".Translate())) SetFilter(PhotoFilters.None);
        }

        private void DrawSaveControls(Rect panel)
        {
            var store = PhotoStore.Instance;
            GUI.enabled=CanUndoEdit;
            if(Widgets.ButtonText(new Rect(panel.x,panel.yMax-144f,panel.width,32f),"MF_UX_UndoEdit".Translate()))UndoEdit();
            GUI.enabled=true;
            RectInt px = PhotoEditing.Pixels(crop, Mathf.RoundToInt(srcAspect * 1000f), 1000);
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(panel.x, panel.yMax - 106f, panel.width, 34f),
                "MF_EditSize".Translate(Mathf.RoundToInt(crop.width * 100f), Mathf.RoundToInt(crop.height * 100f), ((float)px.width / px.height).ToString("0.00")));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            bool changed = HasUnsavedChanges;
            Rect save = new Rect(panel.x, panel.yMax - 70f, panel.width, 32f);
            if (!changed)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Widgets.ButtonText(save, "MF_EditSave".Translate(), true, false, false);
                GUI.color = Color.white;
            }
            else if (Widgets.ButtonText(save, "MF_EditSave".Translate()) && store != null)
            {
                var e = store.SaveEdit(rec, crop, filter, imageIcons);
                if (e != null)
                {
                    if (editingBase != null) { e.title = editingBase.title; e.note = editingBase.note; }
                    Messages.Message("MF_EditSaved".Translate(rec.edits.Count), MessageTypeDefOf.TaskCompletion, false);
                    allowClose=true;
                    MainTabWindow_Album.ShowVersion(rec, e.id);
                    Close();
                }
                else Messages.Message("MF_EditFailed".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            if (Widgets.ButtonText(new Rect(panel.x, panel.yMax - 34f, panel.width, 32f), "CancelButton".Translate())) Close();
        }

        private void DrawImageIcons(Rect image)
        {
            GUI.BeginGroup(image);
            try
            {
                Rect local = new Rect(0f, 0f, image.width, image.height);
                foreach (var item in imageIcons)
                    PhotoIconArt.Draw(PhotoImageIcons.OnImage(item, local), item.icon, PhotoImageIcons.ColorFor(item.icon));
            }
            finally { GUI.EndGroup(); }
        }

        private void HandleIconMouse(Rect image)
        {
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                selectedIcon = -1;
                for (int i = imageIcons.Count - 1; i >= 0; i--)
                    if (PhotoImageIcons.OnImage(imageIcons[i], image).ExpandedBy(4f).Contains(ev.mousePosition)) { selectedIcon = i; break; }
                if (selectedIcon < 0) return;
                iconDragging = true;
                iconDragFrom = ev.mousePosition;
                iconPositionFrom = new Vector2(imageIcons[selectedIcon].x, imageIcons[selectedIcon].y);
                ev.Use();
            }
            else if (ev.type == EventType.MouseDrag && iconDragging && selectedIcon >= 0 && selectedIcon < imageIcons.Count)
            {
                Vector2 delta = ev.mousePosition - iconDragFrom;
                imageIcons[selectedIcon].x = Mathf.Clamp01(iconPositionFrom.x + delta.x / image.width);
                imageIcons[selectedIcon].y = Mathf.Clamp01(iconPositionFrom.y + delta.y / image.height);
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp && iconDragging) { iconDragging = false; ev.Use(); }
            else if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Delete && selectedIcon >= 0 && selectedIcon < imageIcons.Count)
            { imageIcons.RemoveAt(selectedIcon); selectedIcon = -1; ev.Use(); }
        }

        private void DrawIconPanel(Rect panel)
        {
            Widgets.Label(new Rect(0f, 0f, panel.width, 58f), "MF_Phase2_IconHelp".Translate());
            float y = 66f;
            for (int i = 0; i < AlbumStickers.Palette.Length; i++)
            {
                PhotoIcon icon = AlbumStickers.Palette[i];
                Rect button = new Rect((i % 4) * 46f, y + (i / 4) * 46f, 40f, 40f);
                Widgets.DrawBoxSolid(button, new Color(.24f, .22f, .19f));
                PhotoIconArt.Draw(button.ContractedBy(5f), icon, PhotoImageIcons.ColorFor(icon));
                TooltipHandler.TipRegion(button, DescriptionCandidates.HasText("MF_Sticker_" + icon) ? ("MF_Sticker_" + icon).Translate() : icon.ToString());
                if (Widgets.ButtonInvisible(button) && imageIcons.Count < PhotoImageIcons.MaxCount)
                {
                    imageIcons.Add(new PhotoImageIcon(icon, crop.center.x, crop.center.y, Mathf.Clamp(Mathf.Min(.12f, crop.width * .3f), PhotoImageIcons.MinSize, PhotoImageIcons.MaxSize)));
                    selectedIcon = imageIcons.Count - 1;
                }
            }
            y += 102f;
            Widgets.Label(new Rect(0f, y, panel.width, 26f), "MF_Phase2_IconSize".Translate()); y += 30f;
            if (selectedIcon >= 0 && selectedIcon < imageIcons.Count)
            {
                var item = imageIcons[selectedIcon];
                item.size = Widgets.HorizontalSlider(new Rect(0f, y, panel.width, 28f), item.size, PhotoImageIcons.MinSize, PhotoImageIcons.MaxSize);
                y += 34f;
                if (Widgets.ButtonText(new Rect(0f, y, panel.width, 32f), "MF_Phase2_IconRemove".Translate())) { imageIcons.RemoveAt(selectedIcon); selectedIcon = -1; }
            }
            else Widgets.Label(new Rect(0f, y, panel.width, 60f), "MF_Phase2_IconSelect".Translate());
            if (Widgets.ButtonText(new Rect(0f, 300f, panel.width, 32f), "MF_Phase2_IconClear".Translate())) { imageIcons.Clear(); selectedIcon = -1; }
            Widgets.Label(new Rect(0f, 340f, panel.width, 52f), "MF_Phase2_IconOriginalSafe".Translate());
        }
    }
}
