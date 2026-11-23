using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Reached only by the marked, disposable review-fixes probe, on a real OnGUI repaint.
    public static partial class Probe
    {
        private static Dialog_PhotoEdit reviewEditorViewScreen;
        private static int reviewEditorViewScreenStage, reviewEditorViewScreenFrame;
        private static float reviewEditorViewScreenAt;

        private static T ReviewEditorGet<T>(Dialog_PhotoEdit editor, string name) =>
            (T)AccessTools.Field(typeof(Dialog_PhotoEdit), name).GetValue(editor);

        private static void ReviewEditorSet(Dialog_PhotoEdit editor, string name, object value) =>
            AccessTools.Field(typeof(Dialog_PhotoEdit), name).SetValue(editor, value);

        private static Rect ReviewEditorImage(Dialog_PhotoEdit editor, Rect area) =>
            (Rect)AccessTools.Method(typeof(Dialog_PhotoEdit), "ViewImage").Invoke(editor, new object[] { area });

        private static void ReviewEditorZoom(Dialog_PhotoEdit editor, Rect area, float factor, Vector2 anchor) =>
            AccessTools.Method(typeof(Dialog_PhotoEdit), "ZoomView").Invoke(editor, new object[] { area, factor, anchor });

        private static void ReviewEditorFit(Dialog_PhotoEdit editor) =>
            AccessTools.Method(typeof(Dialog_PhotoEdit), "FitView").Invoke(editor, null);

        private static EventType ReviewEditorInput(Dialog_PhotoEdit editor, Rect area, Event input)
        {
            Event previous = Event.current;
            try
            {
                Event.current = input;
                AccessTools.Method(typeof(Dialog_PhotoEdit), "HandleViewportInput").Invoke(editor, new object[] { area });
                return Event.current.type;
            }
            finally { Event.current = previous; }
        }

        private static Event ReviewEditorMouse(EventType type, Vector2 point, int button = 0, Vector2 delta = default(Vector2)) =>
            ReviewEditorRaw(new Event { type = type, mousePosition = point, button = button, delta = delta }, type);
        private static Event ReviewEditorRaw(Event e, EventType type) { var set = AccessTools.PropertySetter(typeof(Event), "rawType"); if (set != null) set.Invoke(e, new object[] { type }); return e; }

        private static Vector2 ReviewEditorPoint(Rect image, Vector2 fractions) =>
            new Vector2(image.x + fractions.x * image.width, image.y + fractions.y * image.height);

        private static Vector2 ReviewEditorFractions(Rect image, Vector2 point) =>
            new Vector2((point.x - image.x) / image.width, (point.y - image.y) / image.height);

        private static bool ReviewEditorRectNear(Rect a, Rect b) =>
            (a.position - b.position).sqrMagnitude < .000000001f && (a.size - b.size).sqrMagnitude < .000000001f;

        private static bool ReviewEditorViewInside(Rect image, Rect area)
        {
            const float epsilon = .001f;
            bool x = image.width > area.width + epsilon ? image.x <= area.x + epsilon && image.xMax >= area.xMax - epsilon
                : Mathf.Abs(image.center.x - area.center.x) < epsilon;
            bool y = image.height > area.height + epsilon ? image.y <= area.y + epsilon && image.yMax >= area.yMax - epsilon
                : Mathf.Abs(image.center.y - area.center.y) < epsilon;
            return x && y;
        }

        private static void ReviewEditorViewChecks(PhotoRecord original)
        {
            var editor = new Dialog_PhotoEdit(original);
            Rect area = new Rect(0f, 0f, 900f, 600f), crop = new Rect(.2f, .2f, .4f, .4f);
            ReviewEditorSet(editor, "srcAspect", 1.5f); ReviewEditorSet(editor, "crop", crop);
            Vector2 anchor = new Vector2(480f, 330f), outside = new Vector2(area.xMax + 60f, area.center.y);
            Rect before = ReviewEditorImage(editor, area);
            Vector2 anchorBefore = ReviewEditorFractions(before, anchor);
            EventType wheel = ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.ScrollWheel, anchor, delta: new Vector2(0f, -2f)));
            Rect zoomed = ReviewEditorImage(editor, area);
            ReviewEditingCheck(wheel == EventType.Used && ReviewEditorGet<float>(editor, "viewZoom") > 1f &&
                (ReviewEditorFractions(zoomed, anchor) - anchorBefore).sqrMagnitude < .000000001f &&
                ReviewEditorRectNear(crop, ReviewEditorGet<Rect>(editor, "crop")),
                "wheel zoom keeps the image point under the cursor and leaves the stored crop unchanged");
            float previousZoom = ReviewEditorGet<float>(editor, "viewZoom"); Vector2 previousPan = ReviewEditorGet<Vector2>(editor, "viewPan");
            EventType external = ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.ScrollWheel, outside, delta: new Vector2(0f, -2f)));
            ReviewEditingCheck(external == EventType.ScrollWheel && ReviewEditorGet<float>(editor, "viewZoom") == previousZoom &&
                ReviewEditorGet<Vector2>(editor, "viewPan") == previousPan,
                "wheel input outside the photo viewport does not zoom or consume another control's event");

            ReviewEditorFit(editor); ReviewEditorZoom(editor, area, 2f, area.center);
            Vector2 middle = area.center;
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, middle, 2));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, middle + new Vector2(120f, 80f), 2));
            previousZoom = ReviewEditorGet<float>(editor, "viewZoom"); previousPan = ReviewEditorGet<Vector2>(editor, "viewPan");
            wheel = ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.ScrollWheel, middle, delta: new Vector2(0f, -2f)));
            ReviewEditingCheck(ReviewEditorGet<bool>(editor, "panDragging") && previousPan.sqrMagnitude > 1f &&
                wheel == EventType.Used && ReviewEditorGet<float>(editor, "viewZoom") == previousZoom &&
                ReviewEditorGet<Vector2>(editor, "viewPan") == previousPan && ReviewEditorRectNear(crop, ReviewEditorGet<Rect>(editor, "crop")),
                "middle-button drag pans the preview without changing crop, and suppresses zoom during that drag");
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, outside, 2));
            ReviewEditingCheck(!ReviewEditorGet<bool>(editor, "panDragging"), "middle-button release outside the viewport ends panning");
            previousPan = ReviewEditorGet<Vector2>(editor, "viewPan");
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, outside, 2));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, middle, 2));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, middle, 2));
            ReviewEditingCheck(!ReviewEditorGet<bool>(editor, "panDragging") && ReviewEditorGet<Vector2>(editor, "viewPan") == previousPan,
                "a pan gesture beginning outside the viewport cannot capture a side-panel drag");

            ReviewEditorFit(editor); ReviewEditorZoom(editor, area, 2f, area.center);
            ReviewEditorInput(editor, area, new Event { type = EventType.KeyDown, keyCode = KeyCode.Space, mousePosition = middle });
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, middle));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, middle + new Vector2(-90f, -60f)));
            ReviewEditingCheck(ReviewEditorGet<bool>(editor, "panDragging") && ReviewEditorGet<Vector2>(editor, "viewPan").sqrMagnitude > 1f &&
                ReviewEditorGet<int>(editor, "dragging") == 0 && ReviewEditorRectNear(crop, ReviewEditorGet<Rect>(editor, "crop")),
                "Space plus left drag moves the preview without starting or changing a crop drag");
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, outside));
            ReviewEditorInput(editor, area, new Event { type = EventType.KeyUp, keyCode = KeyCode.Space, mousePosition = outside });
            ReviewEditingCheck(!ReviewEditorGet<bool>(editor, "spaceHeld") && !ReviewEditorGet<bool>(editor, "panDragging"),
                "Space and mouse release outside the viewport leave no stuck pan modifier");

            ReviewEditorFit(editor); ReviewEditorZoom(editor, area, 2f, area.center);
            Rect image = ReviewEditorImage(editor, area);
            Vector2 cropCenter = ReviewEditorPoint(image, crop.center), movement = new Vector2(36f, 24f);
            previousPan = ReviewEditorGet<Vector2>(editor, "viewPan"); previousZoom = ReviewEditorGet<float>(editor, "viewZoom");
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, cropCenter));
            wheel = ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.ScrollWheel, cropCenter, delta: new Vector2(0f, -2f)));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, cropCenter + movement));
            Rect moved = ReviewEditorGet<Rect>(editor, "crop");
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, outside));
            ReviewEditingCheck(wheel == EventType.Used && ReviewEditorGet<float>(editor, "viewZoom") == previousZoom &&
                ReviewEditorGet<Vector2>(editor, "viewPan") == previousPan && !ReviewEditorGet<bool>(editor, "panDragging") &&
                ReviewEditorGet<int>(editor, "dragging") == 0 && Mathf.Abs(moved.x - crop.x - movement.x / image.width) < .00001f &&
                Mathf.Abs(moved.y - crop.y - movement.y / image.height) < .00001f && moved.size == crop.size,
                "ordinary left drag still moves crop in zoomed image coordinates and ignores wheel zoom until release");

            var mode = AccessTools.Field(typeof(Dialog_PhotoEdit), "lockMode");
            mode.SetValue(editor, Enum.Parse(mode.FieldType, "Square"));
            ReviewEditorSet(editor, "crop", new Rect(.35f, .35f, .3f, .3f));
            AccessTools.Method(typeof(Dialog_PhotoEdit), "FitLock").Invoke(editor, null);
            Rect locked = ReviewEditorGet<Rect>(editor, "crop"); image = ReviewEditorImage(editor, area);
            Vector2 corner = ReviewEditorPoint(image, locked.position);
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, corner));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, corner + new Vector2(-40f, -17f)));
            Rect resized = ReviewEditorGet<Rect>(editor, "crop");
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, outside));
            ReviewEditingCheck(ReviewEditingCropValid(resized, 1f / 1.5f) && Mathf.Abs(resized.xMax - locked.xMax) < .00001f &&
                Mathf.Abs(resized.yMax - locked.yMax) < .00001f && Mathf.Abs(resized.width - locked.width - 40f / image.width) < .00001f,
                "a locked crop corner at enlarged preview retains its aspect, fixed opposite corner, and source-space delta");
            ReviewEditorSet(editor, "crop", new Rect(0f, 0f, 1f, 1f));
            Vector2 hiddenImagePoint = new Vector2(-20f, area.center.y);
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, hiddenImagePoint));
            ReviewEditingCheck(ReviewEditorGet<int>(editor, "dragging") == 0 && !ReviewEditorGet<bool>(editor, "panDragging"),
                "off-viewport parts of an enlarged crop cannot steal clicks from adjacent controls");

            foreach (float source in new[] { 2f / 3f, 1f, 1.5f, 3f })
            {
                ReviewEditorSet(editor, "srcAspect", source); ReviewEditorFit(editor);
                ReviewEditorZoom(editor, area, .00001f, area.center);
                ReviewEditingCheck(ReviewEditorGet<float>(editor, "viewZoom") == .25f && ReviewEditorViewInside(ReviewEditorImage(editor, area), area),
                    "preview reaches its quarter-size lower bound and recenters on source aspect " + source.ToString("0.###"));
                ReviewEditorZoom(editor, area, 100000f, area.center);
                ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, middle, 2));
                ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, middle + new Vector2(100000f, -100000f), 2));
                ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, outside, 2));
                ReviewEditingCheck(ReviewEditorGet<float>(editor, "viewZoom") == 8f && ReviewEditorViewInside(ReviewEditorImage(editor, area), area),
                    "preview reaches its eight-times upper bound and clamps a large pan on source aspect " + source.ToString("0.###"));
            }
            ReviewEditorSet(editor, "crop", crop); ReviewEditorSet(editor, "filter", PhotoFilters.Sepia);
            ReviewEditorSet(editor, "dragging", 1); ReviewEditorSet(editor, "panDragging", true);
            ReviewEditorFit(editor);
            ReviewEditingCheck(ReviewEditorGet<float>(editor, "viewZoom") == 1f && ReviewEditorGet<Vector2>(editor, "viewPan") == Vector2.zero &&
                !ReviewEditorGet<bool>(editor, "panDragging") && ReviewEditorGet<int>(editor, "dragging") == 0 &&
                ReviewEditorRectNear(crop, ReviewEditorGet<Rect>(editor, "crop")) && ReviewEditorGet<string>(editor, "filter") == PhotoFilters.Sepia,
                "fit-view resets only preview position, scale, and active drags while retaining the chosen crop and filter");
            ReviewEditorSaveIndependence(original, editor, area);
        }

        private static void ReviewEditorSaveIndependence(PhotoRecord original, Dialog_PhotoEdit editor, Rect area)
        {
            var store = PhotoStore.Instance; string originalPath = store.OriginalPath(original), originalHash = ReviewEditingHash(originalPath);
            string shown = original.shownEdit; int editCount = original.edits.Count;
            var fixture = new PhotoRecord { id = "review-editor-view-" + Guid.NewGuid().ToString("N"), kind = Kinds.Manual,
                file = original.file, title = "View-independent edit", dateLabel = original.dateLabel, dateSlug = original.dateSlug,
                aspect = original.Aspect, spanCells = original.spanCells, center = original.center, mapId = original.mapId, ticksAbs = original.ticksAbs };
            ReviewEditorSet(editor, "srcAspect", original.Aspect);
            Rect crop = ReviewEditorGet<Rect>(editor, "crop"); string filter = ReviewEditorGet<string>(editor, "filter");
            var fit = store.SaveEdit(fixture, crop, filter);
            ReviewEditorZoom(editor, area, 4f, area.center);
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDown, area.center, 2));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseDrag, area.center + new Vector2(140f, -90f), 2));
            ReviewEditorInput(editor, area, ReviewEditorMouse(EventType.MouseUp, area.center, 2));
            var enlarged = store.SaveEdit(fixture, ReviewEditorGet<Rect>(editor, "crop"), ReviewEditorGet<string>(editor, "filter"));
            ReviewEditingCheck(fit != null && enlarged != null && fit.file != enlarged.file &&
                ReviewEditingImageSize(store.EditPath(fit)) == ReviewEditingImageSize(store.EditPath(enlarged)) &&
                ReviewEditorSamePixels(store.EditPath(fit), store.EditPath(enlarged)) &&
                fit.cx == enlarged.cx && fit.cy == enlarged.cy && fit.cw == enlarged.cw && fit.ch == enlarged.ch && fit.filter == enlarged.filter,
                "actual SaveEdit produces identical decoded PNG pixels, dimensions, and crop metadata at fit view and after zoom plus pan");
            ReviewEditingCheck(ReviewEditingHash(originalPath) == originalHash && original.file == fixture.file &&
                original.edits.Count == editCount && original.shownEdit == shown,
                "preview gestures and both real edited PNG saves preserve the original bytes and its version selection");
            File.Copy(store.EditPath(fit), Path.Combine(dir, "review-editor-view-fit-edit.png"), true);
            File.Copy(store.EditPath(enlarged), Path.Combine(dir, "review-editor-view-zoom-edit.png"), true);
            File.WriteAllText(Path.Combine(dir, "review-editor-view-save.json"), "{\"sourceSha256\":" + Json(originalHash) +
                ",\"fitSha256\":" + Json(ReviewEditingHash(store.EditPath(fit))) + ",\"zoomSha256\":" +
                Json(ReviewEditingHash(store.EditPath(enlarged))) + ",\"decodedPixelsEqual\":true,\"originalUnchanged\":true}");
        }

        private static bool ReviewEditorSamePixels(string first, string second)
        {
            var a = new Texture2D(2, 2, TextureFormat.RGB24, false); var b = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                return a.LoadImage(File.ReadAllBytes(first), false) && b.LoadImage(File.ReadAllBytes(second), false) &&
                    a.width == b.width && a.height == b.height && a.GetPixels32().SequenceEqual(b.GetPixels32());
            }
            finally { UnityEngine.Object.Destroy(a); UnityEngine.Object.Destroy(b); }
        }

        private static bool ReviewEditorViewScreensStep(PhotoRecord original)
        {
            switch (reviewEditorViewScreenStage)
            {
                case 0:
                    Messages.Clear();
                    reviewEditorViewScreen = new Dialog_PhotoEdit(original);
                    Find.WindowStack.Add(reviewEditorViewScreen);
                    ReviewEditorSet(reviewEditorViewScreen, "crop", new Rect(.25f, .25f, .5f, .5f));
                    ReviewEditingCheck(ReviewEditorGet<Texture2D>(reviewEditorViewScreen, "preview") != null,
                        "native editor window loads the actual source PNG for fit and enlarged screenshots");
                    reviewEditorViewScreenAt = Time.realtimeSinceStartup; reviewEditorViewScreenFrame = Time.frameCount;
                    reviewEditorViewScreenStage = 1; return false;
                case 1:
                    if (Time.realtimeSinceStartup - reviewEditorViewScreenAt < .4f || Time.frameCount - reviewEditorViewScreenFrame < 2) return false;
                    ReviewEditingCheck(Find.WindowStack.Windows.Contains(reviewEditorViewScreen) &&
                        reviewEditorViewScreen.windowRect.xMin >= 0f && reviewEditorViewScreen.windowRect.yMin >= 0f &&
                        reviewEditorViewScreen.windowRect.xMax <= UI.screenWidth && reviewEditorViewScreen.windowRect.yMax <= UI.screenHeight,
                        "actual fit-view editor including its close button remains inside the native screen");
                    CloseLog(); Shot("review-editor-view-fit.png"); reviewEditorViewScreenAt = Time.realtimeSinceStartup;
                    reviewEditorViewScreenStage = 2; return false;
                case 2:
                    if (!ReviewEditorScreenshotReady("review-editor-view-fit.png")) return false;
                    // Use the live viewport layout, including the localized guide's wrapped height.
                    Rect canvas = new Rect(0f, 42f, reviewEditorViewScreen.windowRect.width - 36f - 230f - 16f,
                        reviewEditorViewScreen.windowRect.height - 36f - 42f);
                    Rect viewport = (Rect)AccessTools.Method(typeof(Dialog_PhotoEdit), "Viewport").Invoke(reviewEditorViewScreen, new object[] { canvas });
                    Rect area = new Rect(Vector2.zero, viewport.size);
                    ReviewEditorZoom(reviewEditorViewScreen, area, 2f, area.center);
                    ReviewEditorInput(reviewEditorViewScreen, area, ReviewEditorMouse(EventType.MouseDown, area.center, 2));
                    ReviewEditorInput(reviewEditorViewScreen, area, ReviewEditorMouse(EventType.MouseDrag, area.center + new Vector2(40f, 25f), 2));
                    ReviewEditorInput(reviewEditorViewScreen, area, ReviewEditorMouse(EventType.MouseUp, area.center, 2));
                    reviewEditorViewScreenAt = Time.realtimeSinceStartup; reviewEditorViewScreenFrame = Time.frameCount;
                    reviewEditorViewScreenStage = 3; return false;
                case 3:
                    if (Time.realtimeSinceStartup - reviewEditorViewScreenAt < .4f || Time.frameCount - reviewEditorViewScreenFrame < 2) return false;
                    ReviewEditingCheck(ReviewEditorGet<float>(reviewEditorViewScreen, "viewZoom") == 2f &&
                        ReviewEditorRectNear(ReviewEditorGet<Rect>(reviewEditorViewScreen, "crop"), new Rect(.25f, .25f, .5f, .5f)),
                        "actual enlarged editor window renders at twice-fit scale while retaining its crop");
                    CloseLog(); Shot("review-editor-view-zoom.png"); reviewEditorViewScreenAt = Time.realtimeSinceStartup;
                    reviewEditorViewScreenStage = 4; return false;
                case 4:
                    if (!ReviewEditorScreenshotReady("review-editor-view-zoom.png")) return false;
                    ReviewEditingCheck(ReviewEditingImageSize(Path.Combine(dir, "review-editor-view-fit.png")) == new Vector2(Screen.width, Screen.height) &&
                        ReviewEditingImageSize(Path.Combine(dir, "review-editor-view-zoom.png")) == new Vector2(Screen.width, Screen.height) &&
                        ReviewEditingHash(Path.Combine(dir, "review-editor-view-fit.png")) != ReviewEditingHash(Path.Combine(dir, "review-editor-view-zoom.png")),
                        "fit and enlarged views produce separate full-size screenshots from the real native editor");
                    reviewEditorViewScreen.Close(false); reviewEditorViewScreen = null;
                    checks.Add("INFO: editor screenshots are native renders; human readability and physical mouse feel still require visual and player review.");
                    reviewEditorViewScreenStage = 5; return true;
                default: return true;
            }
        }

        private static bool ReviewEditorScreenshotReady(string name)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length > 1000) return true;
            if (Time.realtimeSinceStartup - reviewEditorViewScreenAt > 10f) throw new TimeoutException("editor screenshot missing: " + name);
            return false;
        }
    }
}
