using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A place the colonists have stood on: the world tile, what it was (biome, landmark), when they first got there, and its photo if
    /// one was taken.</summary>
    public class Visit : IExposable
    {
        public PlanetTile tile = PlanetTile.Invalid;
        public string biome;          // BiomeDef defName
        public string landmark;       // LandmarkDef defName (Odyssey), null when the tile has none
        public string landmarkName;   // the landmark's own name, else its label
        public int ticksAbs;
        public string photoId;
        public string locationName;   // map parent's actual name at the visit, not a guessed colony number

        public void ExposeData()
        {
            Scribe_Values.Look(ref tile, "tile");
            Scribe_Values.Look(ref biome, "biome");
            Scribe_Values.Look(ref landmark, "landmark");
            Scribe_Values.Look(ref landmarkName, "landmarkName");
            Scribe_Values.Look(ref ticksAbs, "ticksAbs", 0);
            Scribe_Values.Look(ref photoId, "photoId");
            Scribe_Values.Look(ref locationName, "locationName");
        }
    }

    /// <summary>The colony's journey. Every map the colonists set foot on is a place, however they got there — on foot with a caravan, by shuttle,
    /// by gravship. The first time they stand in a biome, or on a tile with a landmark (Odyssey), the place is photographed once they have been
    /// there a little while (a caravan walks in from the edge), with the new land shown on a quarter-map scale. A gravship's landing photograph
    /// remains a separate event; it does not replace this view of the new land. The world overlay shows first-discovery order, not a party's travel route.</summary>
    public static class Journey
    {
        public const string Kind = "journey";
        public const int SettleTicks = 600;       // on the map this long before the photo (walking in, the shuttle's ramp, the ship settling)
        public const int CheckEvery = 240;
        public const int LandingWindow = 2500;    // legacy public constant; new-place exploration is now independent of the landing photograph
        public const float MinSpan = 25f, MaxSpan = 61f;   // wide: the new ground is the point

        /// <summary>The colonist nearest the group's middle: the picture is centred on someone. The middle itself could be empty ground (r104: two
        /// arrivals far apart, the middle a lake shore, and both too far from it to be framed).</summary>
        public static IntVec3 Focus(List<Pawn> people, Map map)
        {
            if (people.Count == 0) return map.Center;
            float ax = (float)people.Average(p => p.Position.x), az = (float)people.Average(p => p.Position.z);
            return people.MinBy(p => (p.Position.x - ax) * (p.Position.x - ax) + (p.Position.z - az) * (p.Position.z - az)).Position;
        }

        private static readonly Dictionary<int, int> firstSeen = new Dictionary<int, int>();   // map uniqueID -> tick the colonists were first seen there; not saved (a load restarts the wait)
        private static int lastCheck = -99999;

        public static void Reset()
        {
            firstSeen.Clear();
            lastCheck = -99999;
            JourneyMap.Reset();
        }

        public static Landmark LandmarkAt(PlanetTile tile)
        {
            if (!ModsConfig.OdysseyActive || !tile.Valid) return null;
            try { return Find.World?.landmarks?[tile]; }
            catch { return null; }
        }

        public static string LandmarkLabel(Landmark lm) => lm == null ? null : !lm.name.NullOrEmpty() ? lm.name : lm.def?.LabelCap.ToString();

        /// <summary>Where the colonists are, as words: "Glowforest", "Oasis (Glowforest)".</summary>
        public static string PlaceLabel(BiomeDef biome, Landmark lm)
        {
            string b = biome?.LabelCap.ToString() ?? "?";
            return lm == null ? b : "MF_JourneyPlace".Translate(LandmarkLabel(lm), b).ToString();
        }

        public static void Check(PhotoStore store, int ticksGame)
        {
            if (ticksGame - lastCheck < CheckEvery) return;
            lastCheck = ticksGame;
            // A colony that is already somewhere (a new game, or a save from before the journey existed) starts its journey where it stands.
            if (store.AddMark("journey:start"))
            {
                foreach (var home in Find.Maps.Where(m => m.IsPlayerHome && !m.IsPocketMap))
                    if (!store.Visited(home.Tile)) store.AddVisit(NewVisit(home));
                return;
            }
            var maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                if (map.IsPocketMap || !map.Tile.Valid || store.Visited(map.Tile)) continue;
                if (map.mapPawns.FreeColonistsSpawnedCount == 0) { firstSeen.Remove(map.uniqueID); continue; }
                if (!firstSeen.TryGetValue(map.uniqueID, out int seen)) { firstSeen[map.uniqueID] = ticksGame; continue; }
                if (ticksGame - seen < SettleTicks) continue;
                firstSeen.Remove(map.uniqueID);
                Arrive(store, map);
            }
        }

        private static Visit NewVisit(Map map)
        {
            var lm = LandmarkAt(map.Tile);
            return new Visit
            {
                tile = map.Tile,
                biome = map.Biome?.defName,
                landmark = lm?.def?.defName,
                landmarkName = LandmarkLabel(lm),
                locationName = map.Parent?.Label,
                ticksAbs = Find.TickManager.TicksAbs
            };
        }

        /// <summary>The colonists have settled on a new place: remember it, and photograph it if it is a first biome or has a landmark.</summary>
        public static PhotoRecord Arrive(PhotoStore store, Map map)
        {
            var visit = NewVisit(map);
            bool newBiome = map.Biome != null && !store.Visits.Any(v => v.biome == map.Biome.defName);
            var lm = LandmarkAt(map.Tile);
            store.AddVisit(visit);
            if (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || !MomentFramesMod.Settings.msJourney || (!newBiome && lm == null)) return null;
            string place = PlaceLabel(map.Biome, lm);
            // The landing event stays untouched. This separate record consistently shows the first biome/landmark on a quarter-map scale.
            string biome = map.Biome?.LabelCap.ToString() ?? "?";
            string title = lm == null ? "MF_JourneyFirstBiome".Translate(biome).ToString()
                : newBiome ? "MF_JourneyFirstBiomeLandmark".Translate(biome, LandmarkLabel(lm)).ToString()
                : "MF_JourneyLandmark".Translate(LandmarkLabel(lm)).ToString();
            var people = map.mapPawns.FreeColonistsSpawned.ToList();
            CellRect area = JourneyFraming.ExplorationArea(map, people);
            var rec = store.RequestAutomaticArea(map, area, Kind, "journey:" + map.Tile, title, null, people);
            if (rec != null)
            {
                rec.captionPlace = place;
                visit.photoId = rec.id;
            }
            return rec;
        }
    }

    /// <summary>First-discovery order on the world map, across all parties. The UI explicitly distinguishes this from actual travel.
    /// Great-circle arcs stay above the planet, and a small print is pinned to every place that has a photo — click it to open the
    /// photo in the album. Only while the world is shown and the setting is on; the arcs are rebuilt only when the list of places changes.</summary>
    [StaticConstructorOnStartup]
    public static class JourneyMap
    {
        public const float PinW = 48f, PinH = 36f;
        private const float Lift = 1.003f;         // above the ground, below the world objects
        private const float StepDegrees = 1.5f;    // arc resolution

        private static Material lineMat;
        private static Material LineMat => lineMat ?? (lineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.WorldOverlayTransparent, new Color(1f, 0.72f, 0.2f, 1f), 3590));

        private static readonly List<Vector3> arcs = new List<Vector3>();   // segment endpoints, pairs
        internal static int Segments => arcs.Count / 2;   // probe diagnostics
        private static int builtFor = -1, builtCount = -1;

        /// <summary>New game / load: another game's route must not stay drawn because its version and visit count happen to match.</summary>
        internal static void Reset()
        {
            arcs.Clear();
            builtFor = builtCount = -1;
        }

        private static bool Showing => MomentFramesMod.Settings != null && WorldRendererUtility.WorldSelected
            && PhotoStore.Instance != null && PhotoStore.Instance.Visits.Count > 0;

        private static void Rebuild(PhotoStore store)
        {
            arcs.Clear();
            var visits = store.Visits;
            for (int i = 1; i < visits.Count; i++)
            {
                PlanetTile a = visits[i - 1].tile, b = visits[i].tile;
                if (!a.Valid || !b.Valid || a.Layer != b.Layer) continue;   // a trip to orbit is not a line on the surface
                Vector3 pa = Find.WorldGrid.GetTileCenter(a), pb = Find.WorldGrid.GetTileCenter(b);
                float angle = Vector3.Angle(pa, pb);
                int steps = Mathf.Max(1, Mathf.CeilToInt(angle / StepDegrees));
                Vector3 prev = pa * Lift;
                for (int k = 1; k <= steps; k++)
                {
                    Vector3 p = Vector3.Slerp(pa, pb, (float)k / steps) * Lift;
                    arcs.Add(prev);
                    arcs.Add(p);
                    prev = p;
                }
            }
            builtFor = store.Version;
            builtCount = visits.Count;
        }

        public static void DrawLines()
        {
            if (!Showing || !MomentFramesMod.Settings.journeyLines) return;
            var store = PhotoStore.Instance;
            if (builtFor != store.Version || builtCount != store.Visits.Count) Rebuild(store);
            for (int i = 0; i + 1 < arcs.Count; i += 2) GenDraw.DrawWorldLineBetween(arcs[i], arcs[i + 1], LineMat, 3f);
        }

        public static void DrawPins()
        {
            if (!Showing || !MomentFramesMod.Settings.journeyPhotos) return;
            var store = PhotoStore.Instance;
            foreach (var v in store.Visits)
            {
                if (v.photoId.NullOrEmpty() || !v.tile.Valid) continue;
                var rec = store.Get(v.photoId);
                if (rec == null || !rec.HasFile) continue;
                Vector3 pos = Find.WorldGrid.GetTileCenter(v.tile);
                if (WorldRendererUtility.HiddenBehindTerrainNow(pos)) continue;
                Vector2 ui = GenWorldUI.WorldToUIPosition(pos);
                var r = new Rect(ui.x - PinW / 2f, ui.y - PinH - 6f, PinW, PinH);
                Widgets.DrawBoxSolid(r, new Color(0.96f, 0.94f, 0.88f));
                var tex = PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
                if (tex != null) GUI.DrawTexture(r.ContractedBy(2f), tex, ScaleMode.ScaleAndCrop);
                Widgets.DrawBox(r, 1);
                TooltipHandler.TipRegion(r, rec.title + "\n" + rec.dateLabel);
                if (Widgets.ButtonInvisible(r)) MainTabWindow_Album.Open(rec);
            }
        }
    }

    [HarmonyPatch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceUpdate))]
    internal static class Patch_JourneyLines
    {
        private static void Postfix()
        {
            try { JourneyMap.DrawLines(); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] journey lines: " + e.Message, 0x4d464a31); }
        }
    }

    [HarmonyPatch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceOnGUI))]
    internal static class Patch_JourneyPins
    {
        private static void Postfix()
        {
            try { JourneyMap.DrawPins(); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] journey pins: " + e.Message, 0x4d464a32); }
        }
    }
}
