using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Called only from Probe's marked disposable review-fixes mode.
    public static partial class Probe
    {
        private static bool reviewEditingUiDone;
        private static PhotoRecord reviewEditingRailA, reviewEditingRailB;

        private static void ReviewEditingCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("review editing fixes: " + label);
            checks.Add("PASS: " + label);
        }

        private static string ReviewEditingHash(string path)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }

        private static Vector2 ReviewEditingImageSize(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path), false)) throw new InvalidDataException("cannot decode " + path);
                return new Vector2(texture.width, texture.height);
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        private static void ReviewEditingChecks(PhotoRecord original)
        {
            var store = PhotoStore.Instance;
            string originalPath = store.OriginalPath(original), originalHash = ReviewEditingHash(originalPath);
            Vector2 sourceSize = ReviewEditingImageSize(originalPath);
            float sourceAspect = sourceSize.x / sourceSize.y;
            ReviewEditingCheck(sourceSize.x >= 128 && sourceSize.y >= 128 && Mathf.Abs(sourceAspect - 1.5f) < .01f,
                "review edit fixtures begin from an actual native 3:2 photograph");
            var crops = new[] {
                new Rect(0f, .375f, 1f, .25f), new Rect(.4166667f, 0f, 1f / 6f, 1f),
                new Rect(.2777778f, 0f, 4f / 9f, 1f), new Rect(0f, 0f, 1f, 1f) };
            var labels = new[] { "panorama-6-1", "portrait-1-4", "portrait-2-3", "ordinary-3-2" };
            var ratios = new[] { 6f, .25f, 2f / 3f, 1.5f };
            var fixtures = new List<PhotoRecord>();
            for (int i = 0; i < crops.Length; i++)
            {
                var record = new PhotoRecord { id = "review-edit-" + Guid.NewGuid().ToString("N"), kind = Kinds.Manual,
                    file = original.file, title = "Original " + labels[i], desc = "Original note " + labels[i],
                    dateLabel = original.dateLabel, dateSlug = original.dateSlug, aspect = original.Aspect,
                    spanCells = original.spanCells, center = original.center, mapId = original.mapId, ticksAbs = original.ticksAbs };
                var edit = store.SaveEdit(record, crops[i], PhotoFilters.Sepia);
                ReviewEditingCheck(edit != null && record.Shown == edit && record.file == original.file,
                    labels[i] + " SaveEdit writes a separate shown edit and preserves the original reference");
                Vector2 full = ReviewEditingImageSize(store.EditPath(edit)), thumb = ReviewEditingImageSize(store.EditThumbPath(edit));
                float aspect = full.x / full.y;
                ReviewEditingCheck(Mathf.Abs(aspect - ratios[i]) < .035f && Mathf.Abs(edit.aspect - aspect) < .00001f,
                    labels[i] + " actual edited PNG and saved metadata retain the requested aspect");
                ReviewEditingCheck(thumb.x <= PhotoCapture.ThumbHeight * PhotoCapture.MaxAspect && thumb.y <= PhotoCapture.ThumbHeight
                    && Mathf.Abs(thumb.x * full.y - thumb.y * full.x) <= (full.x + full.y) * .51f,
                    labels[i] + " decoded thumbnail preserves the PNG aspect within pixel rounding and fits its size limits");
                File.Copy(store.EditPath(edit), Path.Combine(dir, "review-edit-" + labels[i] + ".png"));
                File.Copy(store.EditThumbPath(edit), Path.Combine(dir, "review-edit-" + labels[i] + ".thumb.png"));
                fixtures.Add(record);
            }
            ReviewEditingCheck(ReviewEditingHash(originalPath) == originalHash,
                "four real crop/filter saves leave the original native photograph byte-identical");
            var records = (List<PhotoRecord>)AccessTools.Field(typeof(PhotoStore), "records").GetValue(store);
            records.AddRange(fixtures); store.NotifyChanged();
            reviewEditingRailA = fixtures[0]; reviewEditingRailB = fixtures[1];
            reviewEditingRailB.Shown.title = "Shown portrait title"; reviewEditingRailB.Shown.note = "Shown portrait note";
            MainTabWindow_Album.ShowVersion(reviewEditingRailA, "");
            var album = (MainTabWindow_Album)DefDatabase<MainButtonDef>.GetNamed("MF_Album").TabWindow;
            ReviewEditingCheck(album.GalleryCurrent == reviewEditingRailA && album.GalleryVersion == "",
                "gallery fixture explicitly starts on the first photograph's original version");
            ReviewEditingCheck(album.SelectGalleryPhoto(reviewEditingRailB, false) && album.GalleryCurrent == reviewEditingRailB
                && album.GalleryVersion == null && store.VersionPath(reviewEditingRailB, album.GalleryVersion) == store.EditPath(reviewEditingRailB.Shown)
                && reviewEditingRailB.TitleOf(album.GalleryVersion ?? reviewEditingRailB.Shown?.id) == "Shown portrait title"
                && reviewEditingRailB.DescOf(album.GalleryVersion ?? reviewEditingRailB.Shown?.id) == "Shown portrait note",
                "the actual rail selection path changes photographs and resets original selection to the next photo's shown image/title/note");
            var go = AccessTools.Method(typeof(MainTabWindow_Album), "Go");
            MainTabWindow_Album.ShowVersion(reviewEditingRailB, "");
            go.Invoke(album, new object[] { fixtures, 0 });
            ReviewEditingCheck(album.GalleryCurrent == reviewEditingRailA && album.GalleryVersion == null,
                "arrow navigation also selects the next photograph's shown version through the shared path");
            MainTabWindow_Album.ShowVersion(reviewEditingRailB, reviewEditingRailB.Shown.id);
            ReviewEditingCheck(!album.SelectGalleryPhoto(reviewEditingRailB, false) && album.GalleryVersion == reviewEditingRailB.Shown.id,
                "selecting the current photograph preserves its explicitly viewed edit");
            Find.MainTabsRoot.EscapeCurrentTab(false);
            ReviewEditingFadeChecks(original);
        }

        private static void ReviewEditingFadeChecks(PhotoRecord original)
        {
            var years = new[] { 0, 5, 10, 15, 20, 30 };
            var plain = new[] { 0f, .5f, 1f, 1f, 1f, 1f };
            var protectedValues = new[] { 0f, .25f, .5f, .75f, 1f, 1f };
            for (int i = 0; i < years.Length; i++)
            {
                int age = years[i] * GenDate.TicksPerYear;
                ReviewEditingCheck(Mathf.Abs(Fading.Fade01(age, false, false, 10) - plain[i]) < .00001f
                    && Mathf.Abs(Fading.Fade01(age, true, false, 10) - protectedValues[i]) < .00001f,
                    "ordinary and protected frame fade at " + years[i] + " years follow full and half speed, then saturate");
                ReviewEditingCheck(Mathf.Abs(Fading.Fade01(age, false, true, 10) - (.5f + .5f * plain[i])) < .00001f
                    && Mathf.Abs(Fading.Fade01(age, true, true, 10) - (.5f + .5f * protectedValues[i])) < .00001f,
                    "relic fade at " + years[i] + " years keeps the initial half fade and protection speed");
            }
            ReviewEditingCheck(Fading.Fade01(-GenDate.TicksPerYear, true, false, 10) == 0f
                && Fading.Fade01(-GenDate.TicksPerYear, true, true, 10) == .5f,
                "future-dated original/relic photographs do not fade below their initial state");
            bool previousFade = MomentFramesMod.Settings.fade;
            try
            {
                MomentFramesMod.Settings.fade = false;
                ReviewEditingCheck(Fading.TintFor(original, true) == Color.white,
                    "disabled aging leaves the displayed photograph untinted");
            }
            finally { MomentFramesMod.Settings.fade = previousFade; }
        }

        private static void ReviewEditingStartUi(PhotoRecord original)
        {
            reviewEditingUiDone = false;
            Find.WindowStack.Add(new Window_ReviewEditingProbe(original));
        }

        private sealed class Window_ReviewEditingProbe : Window
        {
            private readonly PhotoRecord original;
            private bool inputChecksDone;
            internal Window_ReviewEditingProbe(PhotoRecord original)
            {
                this.original = original; doWindowBackground = false; closeOnAccept = false; closeOnCancel = false;
                layer = WindowLayer.GameUI; absorbInputAroundWindow = false;
            }
            public override Vector2 InitialSize => new Vector2(1f, 1f);
            protected override void SetInitialSizeAndPosition() { windowRect = new Rect(0f, 0f, 1f, 1f); }
            public override void DoWindowContents(Rect rect) { }
            public override void ExtraOnGUI()
            {
                if (reviewEditingUiDone || !active || Event.current.type != EventType.Repaint) return;
                try
                {
                    if (!inputChecksDone)
                    {
                        ReviewEditingMouseChecks(original);
                        ReviewEditorViewChecks(original);
                        inputChecksDone = true;
                    }
                    if (!ReviewEditorViewScreensStep(original)) return;
                    reviewEditingUiDone = true;
                    Close(false);
                }
                catch (Exception e) { Fail(e); }
            }
        }

        private static bool ReviewEditingCropValid(Rect crop, float ratio)
        {
            const float epsilon = .00001f;
            return crop.x >= -epsilon && crop.y >= -epsilon && crop.xMax <= 1f + epsilon && crop.yMax <= 1f + epsilon
                && crop.width >= PhotoEditing.MinCrop - epsilon && crop.height >= PhotoEditing.MinCrop - epsilon
                && Mathf.Abs(crop.width / crop.height - ratio) < epsilon;
        }

        private static void ReviewEditingMouseChecks(PhotoRecord original)
        {
            var editor = new Dialog_PhotoEdit(original);
            var cropField = AccessTools.Field(typeof(Dialog_PhotoEdit), "crop");
            var modeField = AccessTools.Field(typeof(Dialog_PhotoEdit), "lockMode");
            var aspectField = AccessTools.Field(typeof(Dialog_PhotoEdit), "srcAspect");
            MethodInfo fit = AccessTools.Method(typeof(Dialog_PhotoEdit), "FitLock"), handle = AccessTools.Method(typeof(Dialog_PhotoEdit), "HandleMouse");
            var sources = new[] { 2f / 3f, 1f, 1.5f, 3f };
            var modes = new[] { "Original", "Square", "Wide", "Tall" };
            var starts = new[] { new Rect(.25f, .25f, .45f, .4f), new Rect(.91f, .91f, .06f, .06f), new Rect(0f, 0f, .06f, .06f) };
            Event previous = Event.current;
            try
            {
                foreach (float source in sources)
                    foreach (string mode in modes)
                    {
                        float pixelRatio = mode == "Original" ? source : mode == "Square" ? 1f : mode == "Wide" ? 1.5f : 2f / 3f;
                        float ratio = pixelRatio / source;
                        aspectField.SetValue(editor, source); modeField.SetValue(editor, Enum.Parse(modeField.FieldType, mode));
                        foreach (Rect start in starts)
                        {
                            cropField.SetValue(editor, start); fit.Invoke(editor, null);
                            Rect fitted = (Rect)cropField.GetValue(editor);
                            if (!ReviewEditingCropValid(fitted, ratio)) throw new InvalidOperationException("FitLock " + source + "/" + mode + " " + fitted);
                            foreach (int corner in new[] { 2, 3, 4, 5 })
                                foreach (float dx in new[] { -2f, -.03f, .03f, 2f })
                                {
                                    cropField.SetValue(editor, fitted);
                                    bool left = corner == 2 || corner == 4, top = corner == 2 || corner == 3;
                                    Rect image = new Rect(100f, 100f, 1000f, 1000f / source);
                                    Vector2 point = new Vector2(image.x + (left ? fitted.x : fitted.xMax) * image.width,
                                        image.y + (top ? fitted.y : fitted.yMax) * image.height);
                                    Event.current = new Event { type = EventType.MouseDown, button = 0, mousePosition = point }; handle.Invoke(editor, new object[] { image });
                                    Event.current = new Event { type = EventType.MouseDrag, button = 0, mousePosition = point + new Vector2(dx * image.width, .17f * image.height) };
                                    handle.Invoke(editor, new object[] { image });
                                    Rect result = (Rect)cropField.GetValue(editor);
                                    Event.current = new Event { type = EventType.MouseUp, button = 0, mousePosition = point }; handle.Invoke(editor, new object[] { image });
                                    float anchorX = left ? fitted.xMax : fitted.x, anchorY = top ? fitted.yMax : fitted.y;
                                    bool grows = left ? dx < 0f : dx > 0f;
                                    bool reachesLimit = grows ? (left ? result.x < .00001f : result.xMax > .99999f)
                                        || (top ? result.y < .00001f : result.yMax > .99999f)
                                        : result.width <= PhotoEditing.MinCrop + .00001f || result.height <= PhotoEditing.MinCrop + .00001f;
                                    if (!ReviewEditingCropValid(result, ratio)
                                        || Mathf.Abs((left ? result.xMax : result.x) - anchorX) > .00001f
                                        || Mathf.Abs((top ? result.yMax : result.y) - anchorY) > .00001f
                                        || (Mathf.Abs(dx) > 1f && !reachesLimit))
                                        throw new InvalidOperationException("HandleMouse source=" + source + " lock=" + mode + " corner=" + corner + " dx=" + dx + " result=" + result);
                                }
                        }
                        ReviewEditingCheck(true, "actual editor FitLock and four-corner mouse drags preserve " + mode + " on source aspect " + source.ToString("0.###")
                            + " at image edges and minimum size");
                    }
                modeField.SetValue(editor, Enum.Parse(modeField.FieldType, "Free"));
                Rect free = new Rect(.2f, .2f, .4f, .4f); cropField.SetValue(editor, free);
                Rect freeImage = new Rect(100f, 100f, 1000f, 1000f);
                Vector2 freePoint = new Vector2(700f, 700f);
                Event.current = new Event { type = EventType.MouseDown, button = 0, mousePosition = freePoint }; handle.Invoke(editor, new object[] { freeImage });
                Event.current = new Event { type = EventType.MouseDrag, button = 0, mousePosition = freePoint + new Vector2(100f, -100f) }; handle.Invoke(editor, new object[] { freeImage });
                Rect freeResult = (Rect)cropField.GetValue(editor);
                Event.current = new Event { type = EventType.MouseUp, button = 0, mousePosition = freePoint }; handle.Invoke(editor, new object[] { freeImage });
                ReviewEditingCheck(Mathf.Abs(freeResult.width - .5f) < .00001f && Mathf.Abs(freeResult.height - .3f) < .00001f,
                    "free-crop mouse drag still changes width and height independently");
            }
            finally { Event.current = previous; }
        }
    }
}
