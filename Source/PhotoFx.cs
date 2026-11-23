using System;
using System.IO;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MomentFrames
{
    /// <summary>"Snapshot" effect the moment a photo has been saved: white flash, shutter click, the photo shrinking from the centre of the
    /// screen into a tilted polaroid card in the corner, and an optional half-second freeze. Every part has its own setting.</summary>
    public static class PhotoFx
    {
        public const float FlashSeconds = 0.3f;
        public const float FreezeSeconds = 0.5f;
        public const float MinCameraSize = 12f, MaxCameraSize = 60f;   // the game's own zoom range
        private static float flashStart = -10f;

        public static void Play(PhotoRecord rec)
        {
            var s = MomentFramesMod.Settings;
            if (s == null || rec == null || Current.ProgramState != ProgramState.Playing) return;
            try
            {
                if (s.fxShutter)
                {
                    var snd = DefDatabase<SoundDef>.GetNamedSilentFail("MF_CameraShutter");
                    if (snd != null) snd.PlayOneShotOnCamera();
                }
                if (s.fxCamera) LookAt(rec);
                if (s.fxFlash) flashStart = Time.realtimeSinceStartup;
                bool freeze = s.fxFreeze && !Busy();
                if (s.fxPopup || freeze)
                {
                    Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
                    Find.WindowStack.Add(new Window_PhotoPop(rec, s.fxPopup, freeze));
                }
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] capture effect failed: " + e);
            }
        }

        /// <summary>A raid or a fight is on: the game is holding normal speed because shots were just fired (TimeSlower), or a map has a
        /// hostile threat by the game's own danger rating (the storyteller and the music read the same value). The freeze frame is skipped
        /// then (2026-11-17, user decision): 2.3 shipped it off by default for exactly this reason, a death photo in the middle of a raid,
        /// and the 2026-09-20 build turned it on for everyone. A failed check counts as busy, so it never holds the game by mistake.</summary>
        internal static bool Busy()
        {
            try
            {
                var slower = Find.TickManager?.slower;
                if (slower != null && slower.ForcedNormalSpeed) return true;
                foreach (var m in Find.Maps)
                    if (m?.dangerWatcher != null && m.dangerWatcher.DangerRating != StoryDanger.None) return true;
                return false;
            }
            catch (Exception e)
            {
                Hooks.Fail("fx busy", e);
                return true;
            }
        }

        /// <summary>Moves the player's view to what was just photographed, framed like the picture (the capture itself renders off-camera, so
        /// without this the moment happens somewhere the player is not looking). Only for event photos — a shot the player set up by hand is
        /// already on screen. RootSize is half the visible height in cells, so the photographed span plus a fifth fits exactly.</summary>
        private static void LookAt(PhotoRecord rec)
        {
            var map = Find.CurrentMap;
            var driver = Find.CameraDriver;
            if (map == null || driver == null || !rec.HasCenter || rec.mapId != map.uniqueID) return;
            if (!Kinds.IsMilestone(rec.kind)) return;
            float size = Mathf.Clamp(rec.spanCells * 0.6f, MinCameraSize, MaxCameraSize);
            driver.SetRootPosAndSize(new Vector3(rec.center.x, 0f, rec.center.z), size);
        }

        /// <summary>White overlay decaying over FlashSeconds. Drawn after the whole in-game UI (Harmony postfix on UIRoot_Play.UIRootOnGUI) —
        /// deliberately not Verse.ScreenFader, which takes control away from the player while it fades and is shared with the ship-launch sequence.</summary>
        internal static void DrawFlash()
        {
            float t = Time.realtimeSinceStartup - flashStart;
            if (t < 0f || t > FlashSeconds || Event.current.type != EventType.Repaint) return;
            float a = 1f - t / FlashSeconds;
            a *= a;   // instant attack, quick decay
            Widgets.DrawBoxSolid(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight), new Color(1f, 1f, 1f, 0.85f * a));
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    internal static class Patch_FlashOverlay
    {
        private static void Postfix()
        {
            try { PhotoFx.DrawFlash(); }
            catch (Exception e) { Hooks.Fail("flash", e); }
        }
    }

    /// <summary>The polaroid card. Never pauses (except the optional freeze), never blocks the camera or clicks outside the card itself.
    /// The window rect follows the animation, so only the card area takes input.</summary>
    public class Window_PhotoPop : Window
    {
        private const float ShrinkStart = 0.10f, ShrinkEnd = 0.70f, Hold = 3.2f, FadeEnd = 3.7f;
        private const float CardPhotoWidth = 200f, Border = 10f, Caption = 34f, Pad = 16f, Tilt = -4f;

        private readonly PhotoRecord rec;
        private readonly bool showCard;
        private readonly bool freeze;
        private float opened;
        private Texture2D tex;
        private string acquired;

        protected override float Margin => 0f;

        public Window_PhotoPop(PhotoRecord rec, bool showCard, bool freeze)
        {
            this.rec = rec;
            this.showCard = showCard;
            this.freeze = freeze;
            opened = Time.realtimeSinceStartup;
            layer = WindowLayer.GameUI;
            forcePause = freeze;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            focusWhenOpened = false;
            doWindowBackground = false;
            drawShadow = false;
            doCloseX = false;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnCancel = false;
            closeOnClickedOutside = false;
            draggable = false;
            resizeable = false;
            soundAppear = null;
            soundClose = null;
            soundAmbient = null;
        }

        private float Age => Time.realtimeSinceStartup - opened;
        private float Aspect => tex != null && tex.height > 0 ? (float)tex.width / tex.height : rec.Aspect;

        public override void PreOpen()
        {
            base.PreOpen();
            var store = PhotoStore.Instance;
            string path = store?.FilePath(rec);
            if (!path.NullOrEmpty())
            {
                // The card is at most half the screen, and small after a moment. Decoding the full-size original here held the game for 0.1~0.3 s on every photo.
                string preview = PhotoPreviews.PathFor(path);
                if (File.Exists(preview))
                {
                    tex = PhotoTextureCache.AcquireTexture(preview);
                    if (tex != null) acquired = preview;
                }
                if (tex == null)
                {
                    tex = PhotoTextureCache.AcquireTexture(path);
                    if (tex != null) acquired = path;
                }
            }
            if (tex == null) tex = PhotoTextureCache.Thumb(store?.ThumbPath(rec), path);
        }

        public override void PostClose()
        {
            if (acquired != null) PhotoTextureCache.Release(acquired);
            acquired = null;
            tex = null;
            base.PostClose();
        }

        /// <summary>0 = big photo in the centre, 1 = settled card in the corner (eased).</summary>
        private float Progress
        {
            get
            {
                float t = Mathf.Clamp01((Age - ShrinkStart) / (ShrinkEnd - ShrinkStart));
                return 1f - (1f - t) * (1f - t) * (1f - t);
            }
        }

        /// <summary>Photo rectangle in screen (UI) coordinates for the current moment, plus the border thickness and card alpha.</summary>
        private void Geometry(out Rect photo, out float border, out float alpha)
        {
            float p = Progress;
            float aspect = Aspect;
            float w1 = CardPhotoWidth, h1 = w1 / aspect;
            float w0 = Mathf.Min(UI.screenWidth * 0.45f, (UI.screenHeight * 0.55f) * aspect), h0 = w0 / aspect;
            float w = Mathf.Lerp(w0, w1, p), h = Mathf.Lerp(h0, h1, p);
            border = Mathf.Lerp(0f, Border, p);
            float cardW1 = w1 + 2f * Border, cardH1 = h1 + Border + Caption;
            float endX = UI.screenWidth - 240f - cardW1 - 8f;             // left of the speed/date/weather controls
            float endY = UI.screenHeight - 35f - cardH1 - 12f;            // above the bottom bar
            float cx0 = UI.screenWidth / 2f, cy0 = UI.screenHeight / 2f - 20f;
            float cx1 = endX + Border + w1 / 2f, cy1 = endY + Border + h1 / 2f;
            float cx = Mathf.Lerp(cx0, cx1, p), cy = Mathf.Lerp(cy0, cy1, p);
            photo = new Rect(cx - w / 2f, cy - h / 2f, w, h);
            float age = Age;
            alpha = age > Hold ? Mathf.Clamp01(1f - (age - Hold) / (FadeEnd - Hold)) : 1f;
        }

        private Rect CardRect(Rect photo, float border) => new Rect(photo.x - border, photo.y - border, photo.width + 2f * border, photo.height + border + Caption * Progress);

        private Rect CurrentWindowRect()
        {
            if (!showCard) return new Rect(0f, 0f, 1f, 1f);
            Geometry(out Rect photo, out float border, out _);
            Rect card = CardRect(photo, border);
            return new Rect(card.x - Pad, card.y - Pad, card.width + 2f * Pad, card.height + 2f * Pad);
        }

        protected override void SetInitialSizeAndPosition() => windowRect = CurrentWindowRect();

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            forcePause = freeze && Age < PhotoFx.FreezeSeconds;
            windowRect = CurrentWindowRect();
            if (Age > (showCard ? FadeEnd : PhotoFx.FreezeSeconds)) Close(false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!showCard) return;
            Geometry(out Rect photoScreen, out float border, out float alpha);
            Rect cardScreen = CardRect(photoScreen, border);
            // Window-local coordinates.
            Vector2 origin = new Vector2(windowRect.x, windowRect.y);
            Rect photo = new Rect(photoScreen.x - origin.x, photoScreen.y - origin.y, photoScreen.width, photoScreen.height);
            Rect card = new Rect(cardScreen.x - origin.x, cardScreen.y - origin.y, cardScreen.width, cardScreen.height);
            // Hovering keeps the card up.
            if (Mouse.IsOver(card) && Age > Hold) opened = Time.realtimeSinceStartup - Hold;

            Matrix4x4 matrix = GUI.matrix;
            Color color = GUI.color;
            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;
            try
            {
                float angle = Tilt * Progress;
                if (angle != 0f) UI.RotateAroundPivot(angle, card.center);
                if (border > 0.5f) Widgets.DrawBoxSolid(card, new Color(0.97f, 0.96f, 0.92f, alpha));
                GUI.color = new Color(1f, 1f, 1f, alpha);
                if (tex != null) GUI.DrawTexture(photo, tex, ScaleMode.StretchToFill);
                else Widgets.DrawBoxSolid(photo, new Color(0.3f, 0.3f, 0.3f, alpha));
                if (Progress > 0.6f)
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = new Color(0.25f, 0.22f, 0.18f, alpha * Mathf.Clamp01((Progress - 0.6f) / 0.4f));
                    Widgets.Label(new Rect(card.x + 4f, photo.yMax, card.width - 8f, Caption * Progress), rec.title);
                }
            }
            finally
            {
                GUI.matrix = matrix;
                GUI.color = color;
                Text.Font = font;
                Text.Anchor = anchor;
            }
            if (Widgets.ButtonInvisible(card))
            {
                MainTabWindow_Album.Open(rec);
                Close(false);
            }
        }
    }
}
