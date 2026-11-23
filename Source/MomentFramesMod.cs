using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public class MomentFramesSettings : ModSettings
    {
        public const int DefaultSpan = 19;
        public int spanCells = DefaultSpan;      // default span of a click shot and of event photos that have no framing of their own
        public int pxPerCell = PhotoCapture.DefaultPxPerCell;   // Saved image density; independent of camera framing.
        public int photoFormat = PhotoImageFormat.Jpg;          // file type of new originals (PhotoImageFormat); photos already taken keep theirs
        public int framePhotoHeight = 512;      // Fixed map-frame display quality; never changes with camera zoom.
        public bool autoCaptureEnabled = true;   // master switch for event photos; manual requests and chronicles stay available
        public bool captureWeddings = true;
        public bool captureParties = true;
        public bool captureRituals = true;
        public bool captureFunerals = true;
        public string ritualExclude = "";     // extra ritual pattern/behavior defNames never photographed (comma separated)
        public bool msRole = true;
        public bool msBestow = true;
        public bool msBaby = true;
        public bool msDeath = true;
        public bool msRecruit = true;
        public bool msLegendary = true;
        public bool msShip = true;
        public bool msMechBoss = true;
        public bool msColonyStart = true;        // the colony's first photograph, once everyone is out of the pods
        public bool msMechlink = true;           // Biotech: a colonist fitted with a mechlink (once per pawn)
        public bool msRelicFound = true;         // Ideology: a relic reached the colony (once per relic)
        public bool msPersonal = true;           // a botched operation, a sanguophage, a xenogerm, a persona weapon, a high title, an ideo reform
        public bool msRareAnimal = true;         // a rare beast met or tamed (once per species)
        public bool capturePetArrivals = true;   // a newly owned pet, once per animal; existing animals are only baselined
        public bool msAnomaly = true;            // Anomaly: each strange creature or object on sight (once each), first entity contained
        public bool msJourney = true;            // a new place: the first time in a biome, or a landmark (Odyssey), however the colonists got there
        public bool journeyRoute = true;         // the journey drawn on the world map, with the places' photos pinned
        public bool journeyPhotos = true;
        public bool journeyLines = true;
        public bool chronicleTab = true;         // the Chronicle tab on a selected person
        private bool journeyDisplayMigrated;
        public int settlementPeriod = 1;         // 0 off, 1 quarterly, 2 yearly; ordinary anniversary photos remain independent
        public int surveyPxPerCell = SettlementResolution.DefaultPixelsPerCell;
        public bool buildingPhotos = true;
        public int birthdayMode = 1;             // Birthday.ModeOff / ModeAsk (a letter: take it? where?) / ModeAuto (at once, where they are)
        public int birthdayEvery = 1;            // every Nth birthday (the one that makes them an adult always counts)
        public bool birthdayFriends = true;      // the people they are close to come along when they are called to a place
        public bool lifeEvents = true;           // moments without a photo (joining, lovers, a lost limb ...) as lines in a colonist's own chronicle
        public int battleMode = Battles.ModeHard;   // fights the colony won: off / first of each type / hard ones, one a season / every fight
        public bool msDisaster = true;           // a disaster ended (toxic fallout, a cold snap ...): the colony that came through it
        public bool msSky = true;                // an aurora, an eclipse, glowing spores: the colonists out under it
        public bool creditsPhotos = true;        // the colony's photos beside the ending credits (CreditsPhotos)
        public bool memoryNight = true;          // colonists now and then hold a memory night at a projector in the evening (the button works regardless)
        public bool yearLetter = true;           // when the year turns, a letter about the year's photos
        public bool msEnding = true;             // the ship countdown and the structures the endings are built around
        public bool msResurrect = true;          // a colonist resurrected (once per pawn)
        public bool msRelic = true;              // Ideology: a relic placed in a reliquary (once per relic)
        public bool msMonolith = true;           // Anomaly: the void monolith advanced a level
        public bool msHistoric = true;           // colony anniversaries, population milestones, first gravship landing
        public bool msAnniversaryLetter = true;  // "a year ago today" letter (first anniversary only, max one per day)
        public bool reminisce = true;            // colonists look at hung photos for recreation and reminisce with people in the same photo
        public bool albumTilt = true;            // album cards are pinned at a slight angle
        public int projectorSeconds = 15;        // real seconds per photo on the projector (unpaused time)
        public bool fade = false;                // photos on frames yellow with age (map draw only; files, album and viewer untouched)
        public int fadeYears = Fading.DefaultYears;   // years until a photo in a plain frame is fully faded
        public string folderOverride = "";
        public bool brightPhotos = true;
        public bool fxFlash = true;       // white flash when a photo is taken
        public bool fxShutter = true;     // camera shutter sound
        public bool fxPopup = true;       // the photo shrinks from the centre of the screen into a polaroid card
        public bool fxFreeze = true;      // hold the game for half a second (freeze frame)
        public bool fxCamera = true;      // the view moves to the photographed spot, framed like the picture
        public int captureReactionMode = PhotoReactions.ModeAfter;
        public int preparationReactionMode = -1;   // what the preparation windows remember; -1 until a player asks them to remember
        public float eventZoom = 1f;      // how close automatically framed photos are taken (bigger = closer)
        // The album window: where it was left and how see-through its leather and paper are (photos and text stay solid). -1 = never moved.
        public float albumX = -1f, albumY = -1f, albumW = -1f, albumH = -1f, albumOpacity = 1f;
        public string albumTheme = "graphite", albumFont = "original";
        public int albumTextSize = 100;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref spanCells, "spanCells", 19);
            Scribe_Values.Look(ref pxPerCell, "pxPerCell", PhotoCapture.DefaultPxPerCell);
            pxPerCell = Mathf.Clamp(pxPerCell, PhotoCapture.MinPxPerCell, PhotoCapture.MaxPxPerCell);
            Scribe_Values.Look(ref photoFormat, "photoFormat", PhotoImageFormat.Jpg);
            if (photoFormat != PhotoImageFormat.Png) photoFormat = PhotoImageFormat.Jpg;
            Scribe_Values.Look(ref framePhotoHeight, "framePhotoHeight", 512);
            framePhotoHeight = FramePhotoTextures.Normalize(framePhotoHeight);
            Scribe_Values.Look(ref brightPhotos, "brightPhotos", true);
            Scribe_Values.Look(ref fxFlash, "fxFlash", true);
            Scribe_Values.Look(ref fxShutter, "fxShutter", true);
            Scribe_Values.Look(ref fxPopup, "fxPopup", true);
            Scribe_Values.Look(ref fxFreeze, "fxFreeze", true);
            Scribe_Values.Look(ref fxCamera, "fxCamera", true);
            Scribe_Values.Look(ref captureReactionMode, "captureReactionMode", PhotoReactions.ModeAfter);
            captureReactionMode = Mathf.Clamp(captureReactionMode, PhotoReactions.ModeOff, PhotoReactions.ModePhoto);
            Scribe_Values.Look(ref preparationReactionMode, "preparationReactionMode", -1);
            Scribe_Values.Look(ref eventZoom, "eventZoom", 1f);
            Scribe_Values.Look(ref autoCaptureEnabled, "autoCaptureEnabled", true);
            Scribe_Values.Look(ref captureWeddings, "captureWeddings", true);
            Scribe_Values.Look(ref captureParties, "captureParties", true);
            Scribe_Values.Look(ref captureRituals, "captureRituals", true);
            Scribe_Values.Look(ref captureFunerals, "captureFunerals", true);
            Scribe_Values.Look(ref ritualExclude, "ritualExclude", "");
            Scribe_Values.Look(ref msRole, "msRole", true);
            Scribe_Values.Look(ref msBestow, "msBestow", true);
            Scribe_Values.Look(ref msBaby, "msBaby", true);
            Scribe_Values.Look(ref msDeath, "msDeath", true);
            Scribe_Values.Look(ref msRecruit, "msRecruit", true);
            Scribe_Values.Look(ref msLegendary, "msLegendary", true);
            Scribe_Values.Look(ref msShip, "msShip", true);
            Scribe_Values.Look(ref msMechBoss, "msMechBoss", true);
            Scribe_Values.Look(ref msColonyStart, "msColonyStart", true);
            Scribe_Values.Look(ref msMechlink, "msMechlink", true);
            Scribe_Values.Look(ref msRelicFound, "msRelicFound", true);
            Scribe_Values.Look(ref msPersonal, "msPersonal", true);
            Scribe_Values.Look(ref msRareAnimal, "msRareAnimal", true);
            Scribe_Values.Look(ref capturePetArrivals, "capturePetArrivals", true);
            Scribe_Values.Look(ref msAnomaly, "msAnomaly", true);
            Scribe_Values.Look(ref msEnding, "msEnding", true);
            Scribe_Values.Look(ref lifeEvents, "lifeEvents", true);
            Scribe_Values.Look(ref battleMode, "battleMode", Battles.ModeHard);
            Scribe_Values.Look(ref msDisaster, "msDisaster", true);
            Scribe_Values.Look(ref msSky, "msSky", true);
            Scribe_Values.Look(ref creditsPhotos, "creditsPhotos", true);
            Scribe_Values.Look(ref memoryNight, "memoryNight", true);
            Scribe_Values.Look(ref yearLetter, "yearLetter", true);
            Scribe_Values.Look(ref msJourney, "msJourney", true);
            Scribe_Values.Look(ref journeyRoute, "journeyRoute", true);
            Scribe_Values.Look(ref journeyPhotos, "journeyPhotos", true);
            Scribe_Values.Look(ref journeyLines, "journeyLines", true);
            Scribe_Values.Look(ref chronicleTab, "chronicleTab", true);
            if (Scribe.mode == LoadSaveMode.Saving) journeyDisplayMigrated = true;
            Scribe_Values.Look(ref journeyDisplayMigrated, "journeyDisplayMigrated", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && !journeyDisplayMigrated)
            {
                journeyPhotos = journeyLines = journeyRoute;
                journeyDisplayMigrated = true;
            }
            Scribe_Values.Look(ref settlementPeriod, "settlementPeriod", 1);
            settlementPeriod = Mathf.Clamp(settlementPeriod, 0, 2);
            Scribe_Values.Look(ref surveyPxPerCell, "surveyPxPerCell", SettlementResolution.DefaultPixelsPerCell);
            surveyPxPerCell = SettlementResolution.Normalize(surveyPxPerCell);
            Scribe_Values.Look(ref buildingPhotos, "buildingPhotos", true);
            Scribe_Values.Look(ref birthdayMode, "birthdayMode", 1);
            Scribe_Values.Look(ref birthdayEvery, "birthdayEvery", 1);
            Scribe_Values.Look(ref birthdayFriends, "birthdayFriends", true);
            Scribe_Values.Look(ref msResurrect, "msResurrect", true);
            Scribe_Values.Look(ref msRelic, "msRelic", true);
            Scribe_Values.Look(ref msMonolith, "msMonolith", true);
            Scribe_Values.Look(ref msHistoric, "msHistoric", true);
            Scribe_Values.Look(ref msAnniversaryLetter, "msAnniversaryLetter", true);
            Scribe_Values.Look(ref reminisce, "reminisce", true);
            Scribe_Values.Look(ref albumTilt, "albumTilt", true);
            Scribe_Values.Look(ref projectorSeconds, "projectorSeconds", 15);
            if (projectorSeconds < 3) projectorSeconds = 3;
            if (projectorSeconds > 120) projectorSeconds = 120;
            Scribe_Values.Look(ref fade, "fade", false);
            Scribe_Values.Look(ref fadeYears, "fadeYears", Fading.DefaultYears);
            fadeYears = Mathf.Clamp(fadeYears, Fading.MinYears, Fading.MaxYears);
            Scribe_Values.Look(ref folderOverride, "folderOverride", "");
            Scribe_Values.Look(ref albumX, "albumX", -1f);
            Scribe_Values.Look(ref albumY, "albumY", -1f);
            Scribe_Values.Look(ref albumW, "albumW", -1f);
            Scribe_Values.Look(ref albumH, "albumH", -1f);
            Scribe_Values.Look(ref albumOpacity, "albumOpacity", 1f);
            Scribe_Values.Look(ref albumTheme, "albumTheme", "graphite");
            Scribe_Values.Look(ref albumFont, "albumFont", "original");
            Scribe_Values.Look(ref albumTextSize, "albumTextSize", 100);
            AlbumStyle.Normalize(this);
            albumOpacity = Mathf.Clamp(albumOpacity, MainTabWindow_Album.MinOpacity, 1f);
            if (spanCells < 7) spanCells = 7;
            if (spanCells > 31) spanCells = 31;
        }
    }

    public class MomentFramesMod : Mod
    {
        public static MomentFramesSettings Settings;
        public static MomentFramesMod Instance;

        public MomentFramesMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<MomentFramesSettings>();
            ApplyPatches(new Harmony("rucy74.momentframes"));
        }

        /// <summary>Patch classes that could not be applied, and how many were (probe diagnostics).</summary>
        public static readonly List<string> FailedPatches = new List<string>();
        public static int AppliedPatches;

        /// <summary>Each patch class on its own. A game update that renames one patched method then turns off that one moment, with an error in
        /// the log, instead of every patch after it: PatchAll stops at the first class it cannot apply, and its exception left this
        /// constructor, which also lost the settings window.</summary>
        private static void ApplyPatches(Harmony harmony)
        {
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(MomentFramesMod).Assembly))
            {
                // Only patch classes, as PatchAll does: Harmony also runs a class's methods named Prepare / Cleanup, and lord jobs, lord toils
                // and job drivers have a Cleanup of their own (probe r121: four errors at startup when every class was handed over).
                if (t.GetCustomAttributes(typeof(HarmonyAttribute), true).Length == 0) continue;
                try
                {
                    var patched = harmony.CreateClassProcessor(t).Patch();
                    if (patched != null && patched.Count > 0) AppliedPatches++;
                }
                catch (Exception e)
                {
                    FailedPatches.Add(t.Name);
                    Log.Error("[MomentFrames] could not apply " + t.Name + "; that part of the mod is off until it is updated for this game version. " + e.Message);
                }
            }
        }

        public override string SettingsCategory() => "Moment Frames";

        private readonly SettingsWindow settingsWindow = new SettingsWindow();

        public override void DoSettingsWindowContents(Rect inRect) => settingsWindow.Draw(inRect, Settings);

        internal void ReleaseSettingsPreview() => settingsWindow.ReleasePreview();
        internal bool TryCancelSettings() => settingsWindow.TryCancel();

    }

    [StaticConstructorOnStartup]
    public static class MFIcons
    {
        public static readonly Texture2D Camera = ArchitectPhotoIcons.TakePhoto;
        static MFIcons()
        {
            var spot = DefDatabase<ThingDef>.GetNamedSilentFail("MF_PhotoSpot");
            if (spot != null) spot.uiIcon = ArchitectPhotoIcons.PhotoSpot;
        }
        public static readonly Texture2D Frame = ContentFinder<Texture2D>.Get("UI/MomentFrames/Frame");
        public static readonly Texture2D AlbumLeather = ContentFinder<Texture2D>.Get("UI/MomentFrames/AlbumLeather");
        public static readonly Texture2D AlbumPaper = ContentFinder<Texture2D>.Get("UI/MomentFrames/AlbumPaper");
        public static readonly Texture2D Tape = ContentFinder<Texture2D>.Get("UI/MomentFrames/Tape");
        public static readonly Texture2D SpotFrame = ContentFinder<Texture2D>.Get("UI/MomentFrames/SpotFrame");
    }
}
