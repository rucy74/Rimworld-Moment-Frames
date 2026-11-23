using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Read-only original-resolution viewer; zoom and pan never create or alter a photo version.</summary>
    internal sealed class Dialog_MFPhotoZoom : Window
    {
        private readonly PhotoStore owner;
        private readonly PhotoRecord record;
        private readonly string path;
        private Material material;
        private Texture2D texture;
        private sealed class LoadedPhoto { internal PhotoPixels pixels; internal byte[] png; }
        private Task<LoadedPhoto> loading;
        private bool loadFailed;
        private float zoom = 1f;
        private Vector2 pan, dragFrom, panFrom;
        private bool dragging, space;
        private int dragButton;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1280f, UI.screenWidth - 40f), Mathf.Min(960f, UI.screenHeight - 60f));
        internal Dialog_MFPhotoZoom(PhotoRecord record, string version = null)
        {
            this.record = record; owner = PhotoStore.Instance; path = owner?.VersionPath(record, version);
            doWindowBackground = false; doCloseX = false; forcePause = false; absorbInputAroundWindow = true; closeOnAccept = false;
        }
        public override void PreOpen()
        {
            base.PreOpen();
            if (path == null || owner != PhotoStore.Instance) { loadFailed = true; return; }
            if (PhotoTextureCache.IsCached(path))
            { material = PhotoTextureCache.Acquire(path); texture = material?.mainTexture as Texture2D; return; }
            string source = path;
            loading = Task.Run(() => {
                var info = new FileInfo(source);
                if (!info.Exists || info.Length > 67108864) throw new IOException("Photo unavailable or too large");
                byte[] bytes = File.ReadAllBytes(source);
                // RGB PNGs and sequential JPEGs decode here; external RGBA/palette/interlaced PNGs and progressive JPEGs keep the engine's
                // decoder (and their transparency).
                var pixels = PhotoImageFormat.DecodeOnWorker(bytes);
                return pixels != null ? new LoadedPhoto { pixels = pixels } : new LoadedPhoto { png = bytes };
            });
            loading.ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (loading == null || !loading.IsCompleted) return;
            var completed = loading; loading = null;
            if (owner != PhotoStore.Instance) { loadFailed = true; return; }
            try
            {
                var loaded = completed.GetAwaiter().GetResult();
                material = PhotoTextureCache.AcquireDecoded(path, loaded.pixels, loaded.png);
                texture = material?.mainTexture as Texture2D;
                loadFailed = texture == null;
            }
            catch (Exception error)
            { loadFailed = true; Log.Warning("[MomentFrames] original viewer could not load photo: " + error.Message); }
        }
        public override void PostClose()
        {
            if (PhotoTextureCache.Owns(path, material)) PhotoTextureCache.Release(path);
            loading = null; material = null; texture = null; base.PostClose();
        }
        private float FitScale(Rect viewport) => texture == null ? 1f : Mathf.Min(viewport.width / texture.width, viewport.height / texture.height);
        private Rect Image(Rect viewport)
        {
            float scale = FitScale(viewport) * zoom;
            float width = texture.width * scale, height = texture.height * scale;
            pan.x = Mathf.Clamp(pan.x, -Mathf.Max(0f, (width - viewport.width) / 2f), Mathf.Max(0f, (width - viewport.width) / 2f));
            pan.y = Mathf.Clamp(pan.y, -Mathf.Max(0f, (height - viewport.height) / 2f), Mathf.Max(0f, (height - viewport.height) / 2f));
            return new Rect(viewport.center.x + pan.x - width / 2f, viewport.center.y + pan.y - height / 2f, width, height);
        }
        private void Zoom(Rect viewport, float factor, Vector2 anchor)
        {
            float previous = zoom; Rect image = Image(viewport); zoom = Mathf.Clamp(zoom * factor, .25f, 32f);
            pan += (anchor - image.center) * (1f - zoom / previous);
        }
        public override void DoWindowContents(Rect rect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect); Rect page = rect.ContractedBy(20f);
                float control = Mathf.Max(40f, SharedPhotoUI.ControlHeight);
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - 52f, control), record.DisplayTitle);
                if (SharedPhotoUI.Button(new Rect(page.xMax - 44f, page.y, 44f, control), "×")) Close();
                Rect toolbar = new Rect(page.x, page.y + control + 8f, page.width, control);
                Rect viewport = new Rect(page.x, toolbar.yMax + 8f, page.width, Mathf.Max(1f, page.yMax - toolbar.yMax - control - 16f));
                if (SharedPhotoUI.Button(new Rect(toolbar.x, toolbar.y, 48f, control), "−", texture != null)) Zoom(viewport, .8f, viewport.center);
                if (SharedPhotoUI.Button(new Rect(toolbar.x + 56f, toolbar.y, 48f, control), "+", texture != null)) Zoom(viewport, 1.25f, viewport.center);
                if (SharedPhotoUI.Button(new Rect(toolbar.x + 112f, toolbar.y, 150f, control), "MF_Feedback_ZoomFit".Translate())) { zoom = 1f; pan = Vector2.zero; }
                if (SharedPhotoUI.Button(new Rect(toolbar.x + 270f, toolbar.y, 150f, control), "MF_Feedback_ZoomPixels".Translate(), texture != null)) { zoom = Mathf.Clamp(1f / FitScale(viewport), .25f, 32f); pan = Vector2.zero; }
                SharedPhotoUI.Label(new Rect(toolbar.x + 430f, toolbar.y, Mathf.Max(1f, toolbar.width - 430f), control), (zoom * 100f).ToString("0") + "%");
                Widgets.DrawBoxSolid(viewport, AlbumStyle.Theme.Mat);
                if (owner != PhotoStore.Instance || texture == null || !PhotoTextureCache.Owns(path, material))
                    SharedPhotoUI.Label(viewport.ContractedBy(20f), (loading != null && !loadFailed ? "MF_UX_LoadingOriginal" : "MF_MissingFile").Translate());
                else
                {
                    var input = Event.current;
                    if (input.type == EventType.KeyDown && input.keyCode == KeyCode.Space) { space = true; input.Use(); }
                    else if (input.type == EventType.KeyUp && input.keyCode == KeyCode.Space) { space = false; input.Use(); }
                    else if (input.type == EventType.ScrollWheel && Mouse.IsOver(viewport)) { Zoom(viewport, Mathf.Pow(1.25f, -input.delta.y), input.mousePosition); input.Use(); }
                    else if (input.type == EventType.MouseDown && Mouse.IsOver(viewport) && (input.button == 2 || input.button == 0 && space))
                    { dragging = true; dragButton = input.button; dragFrom = input.mousePosition; panFrom = pan; input.Use(); }
                    else if (input.type == EventType.MouseDrag && dragging) { pan = panFrom + input.mousePosition - dragFrom; input.Use(); }
                    else if (input.type == EventType.MouseUp && dragging && input.button == dragButton) { dragging = false; input.Use(); }
                    Rect image = Image(viewport); image.position -= viewport.position;
                    GUI.BeginGroup(viewport);
                    try { GUI.DrawTexture(image, texture, ScaleMode.StretchToFill); }
                    finally { GUI.EndGroup(); }
                }
                SharedPhotoUI.Label(new Rect(page.x, page.yMax - control, page.width, control), "MF_Feedback_ZoomHelp".Translate(), true);
            }
        }
    }
}
