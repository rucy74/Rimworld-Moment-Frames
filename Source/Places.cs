using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>"Places of memory": on the colony map, a small photo pin wherever a photo was taken (its capture centre), so the base shows its
    /// own history. Pins close together on screen stack into one with a count. A pin's menu opens a photo, shoots the oldest photo there again
    /// with the same framing, and shows then and now side by side. Toggled from the play-settings row; the switch is saved with the colony.</summary>
    [StaticConstructorOnStartup]
    public static class Places
    {
        public const float PinW = 44f, PinH = 30f, StackReach = 34f;
        public const int MenuPhotos = 8, MenuThenNow = 3, TipPhotos = 6;

        public static bool Show
        {
            get => PhotoStore.Instance?.showPlaces ?? false;
            set { if (PhotoStore.Instance != null) PhotoStore.Instance.showPlaces = value; }
        }

        public class Pin
        {
            public Vector2 at;
            public readonly List<PhotoRecord> photos = new List<PhotoRecord>();   // oldest first
            public PhotoRecord Top => photos[photos.Count - 1];
        }

        private static List<PhotoRecord> onMap = new List<PhotoRecord>();
        private static int onMapVersion = -1, onMapId = -1;
        private static PhotoStore onMapStore;

        /// <summary>New game / load: drop the previous game's list (it held that game's records and store).</summary>
        internal static void Reset()
        {
            onMap = new List<PhotoRecord>();
            onMapStore = null;
            onMapVersion = onMapId = -1;
        }

        /// <summary>The photos taken on this map that can be pinned: a file, a known centre, not a relic from another colony. Oldest first.</summary>
        public static List<PhotoRecord> On(Map map)
        {
            var store = PhotoStore.Instance;
            if (store == null || map == null) return new List<PhotoRecord>();
            if (!ReferenceEquals(store, onMapStore) || store.Version != onMapVersion || map.uniqueID != onMapId)
            {
                onMapStore = store;
                onMapVersion = store.Version;
                onMapId = map.uniqueID;
                onMap = store.Records.Where(r => r.mapId == map.uniqueID && r.HasFile && r.HasCenter && r.kind != Kinds.Relic
                    && !SettlementChronicle.IsSurvey(r)).OrderBy(r => r.ticksAbs).ToList();
            }
            return onMap;
        }

        /// <summary>Greedy stacking by screen distance, in the given order: a photo joins the first pin within reach, else starts its own.</summary>
        public static List<Pin> Stack(IEnumerable<(Vector2 at, PhotoRecord rec)> points, float reach = StackReach)
        {
            var pins = new List<Pin>();
            foreach (var (at, rec) in points)
            {
                Pin near = null;
                for (int i = 0; i < pins.Count; i++)
                    if ((pins[i].at - at).sqrMagnitude <= reach * reach) { near = pins[i]; break; }
                if (near == null)
                {
                    near = new Pin { at = at };
                    pins.Add(near);
                }
                near.photos.Add(rec);
            }
            return pins;
        }

        private static List<Pin> framePins = new List<Pin>();
        private static int framePinsFrame = -1;

        /// <summary>This frame's pins on screen (computed once per frame; OnGUI runs several times a frame).</summary>
        public static List<Pin> PinsOnScreen(Map map)
        {
            if (framePinsFrame == Time.frameCount) return framePins;
            framePinsFrame = Time.frameCount;
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            var pts = new List<(Vector2, PhotoRecord)>();
            foreach (var r in On(map))
                if (view.Contains(r.center.ToIntVec3())) pts.Add((r.center.MapToUIPosition(), r));
            framePins = Stack(pts);
            return framePins;
        }

        public static void Draw(Map map)
        {
            if (!Show || map == null || map != Find.CurrentMap || Find.UIRoot.screenshotMode.Active) return;
            var store = PhotoStore.Instance;
            var pins = PinsOnScreen(map);
            for (int n = 0; n < pins.Count; n++)
            {
                var pin = pins[n];
                var r = new Rect(pin.at.x - PinW / 2f, pin.at.y - PinH - 8f, PinW, PinH);
                Widgets.DrawBoxSolid(new Rect(pin.at.x - 1f, r.yMax, 2f, 8f), new Color(0.15f, 0.12f, 0.1f, 0.85f));   // the stem to the spot
                for (int k = Mathf.Min(2, pin.photos.Count - 1); k >= 1; k--)
                {
                    var b = new Rect(r.x + 3f * k, r.y - 3f * k, r.width, r.height);   // the photos under it
                    Widgets.DrawBoxSolid(b, new Color(0.84f, 0.82f, 0.77f));
                    Widgets.DrawBox(b, 1);
                }
                Widgets.DrawBoxSolid(r, new Color(0.96f, 0.94f, 0.88f));
                var tex = PhotoTextureCache.Thumb(store.ThumbPath(pin.Top), store.FilePath(pin.Top));
                if (tex != null) GUI.DrawTexture(r.ContractedBy(2f), tex, ScaleMode.ScaleAndCrop);
                Widgets.DrawBox(r, 1);
                if (pin.photos.Count > 1)
                {
                    var badge = new Rect(r.xMax - 9f, r.y - 9f, 20f, 16f);
                    Widgets.DrawBoxSolid(badge, new Color(0.72f, 0.2f, 0.15f));
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(badge, pin.photos.Count.ToString());
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                }
                Widgets.DrawHighlightIfMouseover(r);
                TooltipHandler.TipRegion(r, Tip(pin));
                if (Widgets.ButtonInvisible(r)) Find.WindowStack.Add(new FloatMenu(Options(pin)));
            }
        }

        private static string Tip(Pin pin)
        {
            var lines = pin.photos.AsEnumerable().Reverse().Take(TipPhotos).Select(r => r.DisplayTitle + " — " + r.dateLabel).ToList();
            if (pin.photos.Count > TipPhotos) lines.Add("MF_PlacesMore".Translate(pin.photos.Count - TipPhotos));
            return string.Join("\n", lines);
        }

        /// <summary>The photo "then" of a place: its oldest original (not a re-shoot).</summary>
        public static PhotoRecord Oldest(Pin pin) => pin.photos.FirstOrDefault(r => r.reshootOf.NullOrEmpty()) ?? pin.photos.FirstOrDefault();

        public static List<FloatMenuOption> Options(Pin pin)
        {
            var store = PhotoStore.Instance;
            var opts = new List<FloatMenuOption>();
            foreach (var rec in pin.photos.AsEnumerable().Reverse().Take(MenuPhotos))
            {
                var r = rec;
                opts.Add(new FloatMenuOption(r.DisplayTitle + " — " + r.dateLabel, () => MainTabWindow_Album.Open(r)));
            }
            if (pin.photos.Count > MenuPhotos)
            {
                var ids = pin.photos.Select(r => r.id).ToArray();
                opts.Add(new FloatMenuOption("MF_DesignNav_PlaceViewAll".Translate(pin.photos.Count), () =>
                    Find.WindowStack.Add(new Dialog_ChoosePhoto("MF_DesignNav_PlacePhotos".Translate(pin.photos.Count), null, id =>
                    {
                        var rec = PhotoStore.Instance?.Get(id);
                        if (rec != null) MainTabWindow_Album.Open(rec);
                    }, ids, true))));
            }
            int thenNow = 0;
            foreach (var orig in pin.photos.Where(r => r.reshootOf.NullOrEmpty()))
            {
                var later = store?.LatestReshootOf(orig);
                if (later == null || !later.HasFile || thenNow >= MenuThenNow) continue;
                var o = orig;
                opts.Add(new FloatMenuOption("MF_PlacesThenNow".Translate(o.title), () => Find.WindowStack.Add(new Dialog_ThenNow(o, later))));
                thenNow++;
            }
            var oldest = Oldest(pin);
            if (oldest != null && store != null)
                opts.Add(new FloatMenuOption("MF_PlacesReshoot".Translate(oldest.title), () =>
                {
                    if (store.Reshoot(oldest) != null) Messages.Message("MF_ReshootQueued".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }));
            return opts;
        }
    }

    /// <summary>Draws the pins over the map (MapComponentOnGUI runs before the colonist bar and the selector's click handling, so a pin takes
    /// its click and nothing under it gets selected).</summary>
    public class MapComponent_MFPlaces : MapComponent
    {
        public MapComponent_MFPlaces(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            try { Places.Draw(map); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] places of memory: " + e.Message, 0x4d465031); }
        }
    }

    /// <summary>The switch in the play-settings row (bottom right).</summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    internal static class Patch_PlacesToggle
    {
        private static void Postfix(WidgetRow row, bool worldView)
        {
            try
            {
                if (row == null || PhotoStore.Instance == null) return;
                if (worldView)
                {
                    // Two icons like the game's own (2026-11-07): a word button among the icons read as out of place.
                    var settings = MomentFramesMod.Settings;
                    if (settings == null) return;
                    bool photos = settings.journeyPhotos, lines = settings.journeyLines;
                    row.ToggleableIcon(ref photos, MFIcons.Camera, "MF_Phase2_WorldPhotos".Translate() + "\n\n" + "MF_Phase2_WorldPhotosHelp".Translate(), SoundDefOf.Mouseover_ButtonToggle);
                    row.ToggleableIcon(ref lines, ArchitectPhotoIcons.VisitLine, "MF_Phase2_WorldLines".Translate() + "\n\n" + "MF_Phase2_WorldLinesHelp".Translate(), SoundDefOf.Mouseover_ButtonToggle);
                    if (photos != settings.journeyPhotos || lines != settings.journeyLines)
                    {
                        settings.journeyPhotos = photos; settings.journeyLines = lines;
                        MomentFramesMod.Instance?.WriteSettings();
                    }
                    return;
                }
                bool show = Places.Show;
                row.ToggleableIcon(ref show, MFIcons.Camera, "MF_PlacesToggle".Translate(), SoundDefOf.Mouseover_ButtonToggle);
                Places.Show = show;
            }
            catch (Exception e) { Hooks.Fail("places toggle", e); }
        }
    }

    /// <summary>Then and now: a photo and its latest re-shoot (same place, same framing) side by side, in full size.</summary>
    public class Dialog_ThenNow : Window
    {
        private readonly PhotoRecord then, now;
        private readonly string thenPath, nowPath;
        private readonly Material thenMat, nowMat;

        public Dialog_ThenNow(PhotoRecord then, PhotoRecord now)
        {
            this.then = then;
            this.now = now;
            var store = PhotoStore.Instance;
            thenPath = store?.FilePath(then);
            nowPath = store?.FilePath(now);
            thenMat = thenPath.NullOrEmpty() ? null : PhotoTextureCache.Acquire(thenPath);
            nowMat = nowPath.NullOrEmpty() ? null : PhotoTextureCache.Acquire(nowPath);
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(1000f, 480f);

        public override void PostClose()
        {
            base.PostClose();
            if (thenMat != null) PhotoTextureCache.Release(thenPath);
            if (nowMat != null) PhotoTextureCache.Release(nowPath);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "MF_ThenNowTitle".Translate());
            Text.Font = GameFont.Small;
            const float gap = 20f;
            float w = (inRect.width - gap) / 2f;
            Panel(new Rect(0f, 42f, w, inRect.height - 42f), then, thenMat, "MF_Then".Translate());
            Panel(new Rect(w + gap, 42f, w, inRect.height - 42f), now, nowMat, "MF_Now".Translate());
        }

        private static void Panel(Rect r, PhotoRecord rec, Material mat, string label)
        {
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), label + " — " + rec.dateLabel);
            var box = new Rect(r.x, r.y + 28f, r.width, r.height - 28f - 28f);
            Texture tex = mat?.mainTexture;
            if (tex != null && tex.height > 0)
            {
                float a = (float)tex.width / tex.height;
                float fw = Mathf.Min(box.width, box.height * a), fh = fw / a;
                var fit = new Rect(box.center.x - fw / 2f, box.center.y - fh / 2f, fw, fh);
                Widgets.DrawBoxSolid(fit.ExpandedBy(4f), new Color(0.96f, 0.94f, 0.88f));
                GUI.DrawTexture(fit, tex);
                if (Widgets.ButtonInvisible(fit)) MainTabWindow_Album.Open(rec);
            }
            Text.Anchor = TextAnchor.UpperCenter;
            Widgets.Label(new Rect(r.x, r.yMax - 26f, r.width, 26f), rec.title);
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
