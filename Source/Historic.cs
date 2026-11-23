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
    /// <summary>"Historic moments" that are not single game events: colony anniversaries, population milestones, "years ago today" letters.
    /// The decisions are pure functions of ticks/counts (so the probe can test them with synthetic values); DailyCheck is the thin hook,
    /// run once per in-game day from PhotoStore.GameComponentUpdate. Marks make every milestone fire once per game.</summary>
    public static class Historic
    {
        public const string KindAnniversary = "anniversary", KindPopulation = "population", KindLanding = "landing";
        public const string KindColonyStart = "colonystart", KindMechlink = "mechlink", KindRelicFound = "relicfound";
        public const string KindSurgeryFail = "surgeryfail", KindXenogerm = "xenogerm", KindSanguophage = "sanguophage";
        public const string KindPersona = "persona", KindTitle = "title", KindIdeoReform = "ideoreform";
        public const string KindRareSeen = "rareseen", KindRareTamed = "raretamed";
        public const string KindAnomaly = "anomaly", KindContained = "contained", KindEnding = "ending", KindSpecialTree = "specialtree";
        /// <summary>Places that only exist on the way to an ending: standing on the map with one of these is the moment.</summary>
        public static readonly string[] EndingThings = { "ArchonexusCore", "VoidNode" };
        /// <summary>Anomaly creatures worth one photograph the first time the colony meets one (defName of the PawnKindDef).</summary>
        public static readonly string[] AnomalyPawns = { "Revenant", "Nociosphere", "Ghoul", "Dreadmeld", "Devourer", "Chimera", "Sightstealer", "Metalhorror" };
        /// <summary>Trees that are an event in themselves the first time one stands on the colony's map.</summary>
        public static readonly string[] SpecialTrees = { "Plant_TreeGauranlen" };   // not the anima tree: most maps start with one, so it is not an event
        /// <summary>Anomaly things that simply appear on the map (defName of the ThingDef).</summary>
        public static readonly string[] AnomalyThings = { "FleshmassHeart", "PitGate", "GoldenCube", "UnnaturalCorpse" };
        /// <summary>Animals rare enough that meeting one is a story. Kinds, not species, so a modded giant is added by name.</summary>
        public static readonly string[] RareAnimals = { "AlphaThrumbo", "HiveQueen" };
        public const int PollTicks = 60;             // how often the short poll below runs (1 second of game time)
        public const int StartEarliestTick = 240;    // never before this: the drop pods are still coming down
        public const int StartLatestTick = 30000;    // a loaded old save is long past this, so only a fresh game is photographed
        public const int FreshImplantTicks = 900;    // an implant older than this was fitted before (a recruited mechanitor, a loaded save)
        public static readonly int[] PopulationThresholds = { 10, 20, 30, 50, 100 };
        public const float WideSpan = 61f;   // colony-wide shots: anniversaries, population milestones, the ship countdown
        public const int GravshipPad = 6;            // cells of sky around the ship when it lands or leaves
        public const float GravshipLeastSpan = 31f;  // a small ship is still shot with the ground it is leaving

        /// <summary>N when nowAbs is within the first day of the N-th anniversary of startAbs (N >= 1), else 0.</summary>
        public static int AnniversaryYears(int nowAbs, int startAbs)
        {
            int d = nowAbs - startAbs;
            if (d < GenDate.TicksPerYear) return 0;
            return d % GenDate.TicksPerYear < GenDate.TicksPerDay ? d / GenDate.TicksPerYear : 0;
        }

        /// <summary>Highest threshold reached by count, or 0.</summary>
        public static int PopulationMilestone(int count)
        {
            int best = 0;
            foreach (int t in PopulationThresholds) if (count >= t) best = t;
            return best;
        }

        /// <summary>Same day of the year, at least one whole year apart (for "N years ago today").</summary>
        public static int YearsAgoToday(int nowAbs, int thenAbs, float longitude)
        {
            if (nowAbs - thenAbs < GenDate.TicksPerYear) return 0;
            if (GenDate.DayOfYear(nowAbs, longitude) != GenDate.DayOfYear(thenAbs, longitude)) return 0;
            return GenDate.Year(nowAbs, longitude) - GenDate.Year(thenAbs, longitude);
        }

        /// <summary>Centre of the colony's buildings (map centre when there are none). Computed only when a colony-wide photo is taken.</summary>
        public static Vector3 ColonyCenter(Map map)
        {
            var buildings = map.listerBuildings.allBuildingsColonist;
            if (buildings == null || buildings.Count == 0) return map.Center.ToVector3Shifted();
            double x = 0, z = 0;
            foreach (var b in buildings) { x += b.Position.x; z += b.Position.z; }
            return new Vector3((float)(x / buildings.Count) + 0.5f, 0f, (float)(z / buildings.Count) + 0.5f);
        }

        private static Map HomeMap => Find.AnyPlayerHomeMap ?? Find.CurrentMap;

        private static int lastStartCount = -1;   // colonists seen on the previous poll, to tell "everyone is out of the pods" from "still arriving"
        private static int stableStartSince = -1;
        private static readonly EquipmentObservation equipment = new EquipmentObservation();

        /// <summary>New game / load: the previous game's count must not count as the first poll of this one.</summary>
        internal static void Reset() { lastStartCount = -1; stableStartSince = -1; equipment.Clear(); }

        internal static bool ArrivalPending(Map map)
        {
            if (map == null) return true;
            if (PhotoStore.Instance?.InitialColonists?.Any(p => p != null && !p.Dead && !p.Destroyed && (!p.Spawned || p.Map != map)) == true) return true;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!(thing is Skyfaller) && !(thing is ActiveTransporter)) continue;
                if (!(thing is IThingHolder holder)) continue;
                var todo = new Queue<IThingHolder>(); todo.Enqueue(holder);
                var seen = new HashSet<IThingHolder>();
                while (todo.Count > 0)
                {
                    var current = todo.Dequeue();
                    if (!seen.Add(current)) continue;
                    var held = current.GetDirectlyHeldThings();
                    if (held != null && held.Any(t => t is Pawn p && p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike)) return true;
                    var children = new List<IThingHolder>(); current.GetChildHolders(children);
                    foreach (var child in children) todo.Enqueue(child);
                }
            }
            return false;
        }

        /// <summary>Short poll (PollTicks) for moments that have no event to hook: the colony's first minute, a fresh mechlink, a relic reaching
        /// the colony. Each one is marked so it happens once per game.</summary>
        public static void FrequentCheck(PhotoStore store, int ticksGame)
        {
            var s = MomentFramesMod.Settings;
            if (store == null || s == null || Current.ProgramState != ProgramState.Playing) return;
            try
            {
                JoinCapture.Poll(store, ticksGame);
                // Reuse one census for these checks; their original observation/request order is preserved.
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                ObservePersonaWeapons(store, SettingsCapturePolicy.Automatic(s) && s.msPersonal, colonists);
                if (SettingsCapturePolicy.Automatic(s))
                {
                    if (s.msColonyStart) CheckColonyStart(store, ticksGame);
                    if (s.msMechlink) CheckMechlink(store, colonists);
                    if (s.msRelicFound && ticksGame > StartEarliestTick) CheckRelicFound(store);
                    if (s.msPersonal) CheckPersonal(store, colonists);
                    if (s.msRareAnimal && ticksGame > StartEarliestTick) CheckRareAnimals(store);
                    if (s.msPersonal && ticksGame > StartEarliestTick) CheckSpecialTrees(store);
                    if (s.msAnomaly && ticksGame > StartEarliestTick) CheckAnomalies(store);
                    if (s.msEnding && ticksGame > StartEarliestTick) CheckEndings(store);
                    if (s.msPersonal && ticksGame > StartEarliestTick) CheckIdeoReform(store);
                }
                Journey.Check(store, ticksGame);   // the places are remembered even with the photos off (the route on the world map)
            }
            catch (Exception e) { Log.Warning("[MomentFrames] poll failed: " + e); }
        }

        /// <summary>The colony's first photograph. Scenarios differ — crashlanded colonists ride down in pods and only appear when they open —
        /// so the shot waits until the number of colonists on the map has stopped changing for one poll.</summary>
        private static void CheckColonyStart(PhotoStore store, int ticksGame)
        {
            if (ticksGame < StartEarliestTick || ticksGame > StartLatestTick || store.HasMark("colonystart")) return;
            Map map = HomeMap;
            if (map == null || map != Find.CurrentMap) return;
            var people = map.mapPawns.FreeColonistsSpawned;
            if (ArrivalPending(map)) { stableStartSince = -1; lastStartCount = -1; return; }
            int count = people != null ? people.Count : 0;
            if (count == 0) { lastStartCount = 0; return; }
            if (count != lastStartCount) { lastStartCount = count; stableStartSince = ticksGame; return; }
            if (stableStartSince < 0) stableStartSince = ticksGame;
            if (ticksGame - stableStartSince < 300) return;
            if (!store.AddMark("colonystart")) return;
            var list = people.ToList();
            var rect = CellRect.FromLimits(list.Min(p => p.Position.x), list.Min(p => p.Position.z), list.Max(p => p.Position.x), list.Max(p => p.Position.z)).ExpandedBy(4).ClipInsideMap(map);
            Designator_TakePhoto.FitRect(rect, out Vector3 c, out float span, out float aspect);
            store.RequestAutomatic(map, c, KindColonyStart, "colonystart", "MF_ColonyStart".Translate(Faction.OfPlayer?.Name ?? ""), "", list, span, aspect);
        }

        /// <summary>A colonist has just been fitted with a mechlink (Biotech): photographed once per pawn, with whoever is standing over them.</summary>
        private static void CheckMechlink(PhotoStore store, IReadOnlyList<Pawn> colonists)
        {
            for (int i = 0; i < colonists.Count; i++)
            {
                var p = colonists[i];
                if (p?.health?.hediffSet?.hediffs == null || p.Map == null) continue;
                Hediff fresh = null;
                foreach (var h in p.health.hediffSet.hediffs)
                    if (h?.def?.defName == "MechlinkImplant" && h.ageTicks <= FreshImplantTicks) { fresh = h; break; }
                if (fresh == null || !store.AddMark("mechlink:" + p.ThingID)) continue;
                var people = new List<Pawn> { p };
                foreach (var other in p.Map.mapPawns.FreeColonistsSpawned)
                    if (other != p && other.Position.InHorDistOf(p.Position, 4f)) people.Add(other);
                Hooks.AutoFrame(p.Map, p.Position, people, 9f, 15f, out Vector3 c, out float span, out float aspect);
                store.RequestAutomatic(p.Map, c, KindMechlink, "mechlink:" + p.ThingID, "MF_Mechlink".Translate(p.LabelShort), "", people, span, aspect);
            }
        }

        /// <summary>An ideoligion relic has reached the colony (quest reward, dug up, traded for). The reliquary hook covers installing it later.</summary>
        private static void CheckRelicFound(PhotoStore store)
        {
            if (!ModsConfig.IdeologyActive) return;
            var ideos = Faction.OfPlayer?.ideos?.AllIdeos;
            if (ideos == null) return;
            foreach (var ideo in ideos)
            {
                if (ideo?.PreceptsListForReading == null) continue;
                foreach (var precept in ideo.PreceptsListForReading)
                {
                    var relicPrecept = precept as Precept_Relic;
                    if (relicPrecept == null || !relicPrecept.RelicInPlayerPossession) continue;
                    Thing relic = relicPrecept.GeneratedRelic;
                    Map map = relic?.MapHeld;
                    if (relic == null || map == null || map != Find.CurrentMap) continue;
                    if (!store.AddMark("relicfound:" + relicPrecept.Id)) continue;
                    IntVec3 pos = relic.PositionHeld;
                    var people = map.mapPawns.FreeColonistsSpawned.Where(x => x.Position.InHorDistOf(pos, 6f)).ToList();
                    Hooks.AutoFrame(map, pos, people, 9f, 17f, out Vector3 c, out float span, out float aspect);
                    store.RequestAutomatic(map, c, KindRelicFound, "relicfound:" + relicPrecept.Id, "MF_RelicFound".Translate(relic.LabelCap), "", people, span, aspect);
                }
            }
        }

        /// <summary>Things that happen to one colonist and leave a lasting mark: a botched operation is its own hook, these are the states you
        /// can simply look for. Each is photographed once per pawn.</summary>
        private static void CheckPersonal(PhotoStore store, IReadOnlyList<Pawn> colonists)
        {
            for (int i = 0; i < colonists.Count; i++)
            {
                var p = colonists[i];
                if (p?.Map == null) continue;
                // a sanguophage among the colonists (joined, converted or created)
                if (p.genes?.Xenotype?.defName == "Sanguophage" && store.AddMark("sanguophage:" + p.ThingID))
                    Portrait(store, p, KindSanguophage, "sanguophage:" + p.ThingID, "MF_Sanguophage".Translate(p.LabelShort));
                // a xenogerm has just been implanted (the coma is the tell-tale, and it is fresh)
                if (p.health?.hediffSet != null)
                    foreach (var h in p.health.hediffSet.hediffs)
                        if (h?.def?.defName == "XenogerminationComa" && h.ageTicks <= FreshImplantTicks && store.AddMark("xenogerm:" + p.ThingID))
                        {
                            Portrait(store, p, KindXenogerm, "xenogerm:" + p.ThingID, "MF_Xenogerm".Translate(p.LabelShort));
                            break;
                        }
                // a royal title of Count or higher (the empire's own ranking decides "or higher")
                var title = p.royalty?.MainTitle();
                if (title != null && RoyalTitleDefOf.Count != null && title.seniority >= RoyalTitleDefOf.Count.seniority &&
                    store.AddMark("title:" + p.ThingID + ":" + title.defName))
                    Portrait(store, p, KindTitle, "title:" + p.ThingID + ":" + title.defName, "MF_TitleGained".Translate(p.LabelShort, title.GetLabelCapFor(p)));
            }
        }

        private static void ObservePersonaWeapons(PhotoStore store, bool capture, IReadOnlyList<Pawn> colonists)
        {
            for (int i = 0; i < colonists.Count; i++)
            {
                var pawn = colonists[i];
                var weapon = pawn?.equipment?.Primary;
                bool persona = weapon?.TryGetComp<CompBladelinkWeapon>() != null;
                if (pawn == null || !equipment.ChangedToPersona(pawn.GetUniqueLoadID(), weapon?.GetUniqueLoadID(), persona) || !capture) continue;
                string key = "persona:" + pawn.ThingID + ":" + weapon.def.defName;
                if (store.AddMark(key)) Portrait(store, pawn, KindPersona, key, "MF_Persona".Translate(pawn.LabelShort, weapon.LabelShortCap));
            }
        }

        /// <summary>A gauranlen (or anima) tree now stands on the colony's map — transplanted from a pod, or simply grown there. The connection
        /// ritual is a separate photo; this one is the tree arriving (user, 2026-09-20: "영혼나무 옮겨심을 때나 자동으로 나올 때").</summary>
        private static void CheckSpecialTrees(PhotoStore store)
        {
            Map map = Find.CurrentMap;
            if (map == null || !map.IsPlayerHome) return;
            for (int i = 0; i < SpecialTrees.Length; i++)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(SpecialTrees[i]);
                if (def == null) continue;
                var list = map.listerThings.ThingsOfDef(def);
                if (list == null || list.Count == 0 || !store.AddMark("tree:" + def.defName)) continue;
                Sighting(store, map, list[0].Position, KindSpecialTree, "tree:" + def.defName, "MF_SpecialTree".Translate(list[0].LabelCap));
            }
        }

        /// <summary>The colony's ideoligion has been reformed: photographed once per reformation, on the colonists who believe it.</summary>
        private static void CheckIdeoReform(PhotoStore store)
        {
            if (!ModsConfig.IdeologyActive) return;
            var ideo = Faction.OfPlayer?.ideos?.PrimaryIdeo;
            int count = ideo?.development?.reformCount ?? 0;
            if (ideo == null || count <= 0 || !store.AddMark("ideoreform:" + count)) return;
            Map home = HomeMap;
            if (home == null) return;
            var people = home.mapPawns.FreeColonistsSpawned.ToList();
            Vector3 c = people.Count > 0 ? people[0].DrawPos : ColonyCenter(home);
            Hooks.AutoFrame(home, c.ToIntVec3(), people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 fc, out float span, out float aspect);
            store.RequestAutomatic(home, fc, KindIdeoReform, "ideoreform:" + count, "MF_IdeoReform".Translate(ideo.name), "", people, span, aspect);
        }

        /// <summary>A creature rare enough to be worth a photograph: once per species for meeting one, once more if the colony tames it.</summary>
        private static void CheckRareAnimals(PhotoStore store)
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                string kind = pawn?.kindDef?.defName;
                if (kind == null || pawn.Dead) continue;
                bool rare = false;
                for (int i = 0; i < RareAnimals.Length; i++) if (RareAnimals[i] == kind) { rare = true; break; }
                if (!rare) continue;
                bool tamed = pawn.Faction == Faction.OfPlayer;
                string key = (tamed ? "raretamed:" : "rareseen:") + kind;
                if (!store.AddMark(key)) continue;
                var people = map.mapPawns.FreeColonistsSpawned.Where(x => x.Position.InHorDistOf(pawn.Position, 12f)).ToList();
                Hooks.AutoFrame(map, pawn.Position, people, 11f, 21f, out Vector3 c, out float span, out float aspect);
                store.RequestAutomatic(map, c, tamed ? KindRareTamed : KindRareSeen, key,
                    (tamed ? "MF_RareTamed" : "MF_RareSeen").Translate(pawn.KindLabel.CapitalizeFirst()), "", people, span, aspect);
            }
        }

        /// <summary>The strange things Anomaly brings: each creature or object is photographed the first time it shows up, and the first entity
        /// the colony manages to hold on a platform gets its own photo. Colonists nearby are framed with it; when nobody is close, the thing
        /// alone is shot a little wider, since these moments are about what appeared, not who watched.</summary>
        private static void CheckAnomalies(PhotoStore store)
        {
            Map map = Find.CurrentMap;
            if (map == null || !ModsConfig.AnomalyActive) return;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                string kind = pawn?.kindDef?.defName;
                if (kind == null || pawn.Dead) continue;
                bool listed = false;
                for (int i = 0; i < AnomalyPawns.Length; i++) if (AnomalyPawns[i] == kind) { listed = true; break; }
                if (!listed || !store.AddMark("anomaly:" + kind)) continue;
                Sighting(store, map, pawn.Position, KindAnomaly, "anomaly:" + kind, "MF_Anomaly".Translate(pawn.KindLabel.CapitalizeFirst()));
            }
            for (int i = 0; i < AnomalyThings.Length; i++)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(AnomalyThings[i]);
                if (def == null) continue;
                var list = map.listerThings.ThingsOfDef(def);
                if (list == null || list.Count == 0 || !store.AddMark("anomaly:" + def.defName)) continue;
                Sighting(store, map, list[0].Position, KindAnomaly, "anomaly:" + def.defName, "MF_Anomaly".Translate(list[0].LabelCap));
            }
            // the first entity the colony has strapped down and kept
            var platformDef = DefDatabase<ThingDef>.GetNamedSilentFail("HoldingPlatform");
            if (platformDef != null && !store.HasMark("contained:first"))
                foreach (var platform in map.listerThings.ThingsOfDef(platformDef))
                {
                    var held = (platform as Building_HoldingPlatform)?.HeldPawn;
                    if (held == null || !store.AddMark("contained:first")) continue;
                    Sighting(store, map, platform.Position, KindContained, "contained:first", "MF_Contained".Translate(held.KindLabel.CapitalizeFirst()));
                    break;
                }
        }

        /// <summary>The last stretch of a game: the ship's countdown running, the archonexus countdown (the ascension), standing in front of
        /// the structures the endings are built around, and reaching the mech hive. Each is photographed once. The steps that live only inside
        /// quest state (the royal ascent, its shuttle, the archonexus moves, the hive's core switched off) are hooked in Endings.</summary>
        private static readonly System.Reflection.FieldInfo ShipRoot = AccessTools.Field(typeof(ShipCountdown), "shipRoot");
        private static readonly System.Reflection.FieldInfo AscensionRoot = AccessTools.Field(typeof(ArchonexusCountdown), "archonexusCoreRoot");

        private static Map CountdownMap(System.Reflection.FieldInfo field)
        {
            var origin = (field?.GetValue(null) as Thing)?.MapHeld;
            return origin != null && Find.Maps.Contains(origin) ? origin : null;
        }

        private static void CheckEndings(PhotoStore store)
        {
            Map shipMap = ShipCountdown.CountingDown ? CountdownMap(ShipRoot) : null;
            if (shipMap != null && store.AddMark("ending:shipcountdown"))
            {
                var people = shipMap.mapPawns.FreeColonistsSpawned.ToList();
                store.RequestAutomatic(shipMap, ColonyCenter(shipMap), KindEnding, "ending:shipcountdown", "MF_ShipCountdown".Translate(), "", people, WideSpan);
            }
            Map ascensionMap = ModsConfig.IdeologyActive && ArchonexusCountdown.CountdownActivated ? CountdownMap(AscensionRoot) : null;
            if (ascensionMap != null && store.AddMark("ending:ascension"))
            {
                var people = ascensionMap.mapPawns.FreeColonistsSpawned.ToList();
                store.RequestAutomatic(ascensionMap, ColonyCenter(ascensionMap), KindEnding, "ending:ascension", "MF_EndingAscension".Translate(), "", people, WideSpan);
            }
            Map map = Find.CurrentMap;
            if (map == null) return;
            if (ModsConfig.BiotechActive && map.generatorDef == MapGeneratorDefOf.Mechhive && !store.HasMark("ending:mechhive"))
            {
                var first = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                if (first != null && store.AddMark("ending:mechhive"))
                    Sighting(store, map, first.Position, KindEnding, "ending:mechhive", "MF_EndingMechhive".Translate());
            }
            for (int i = 0; i < EndingThings.Length; i++)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(EndingThings[i]);
                if (def == null) continue;
                var list = map.listerThings.ThingsOfDef(def);
                if (list == null || list.Count == 0 || !store.AddMark("ending:" + def.defName)) continue;
                Sighting(store, map, list[0].Position, KindEnding, "ending:" + def.defName, "MF_EndingPlace".Translate(list[0].LabelCap));
            }
        }

        /// <summary>Something on the map worth a photograph, with whoever is standing near it.</summary>
        internal static void Sighting(PhotoStore store, Map map, IntVec3 pos, string kind, string key, string title)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(x => x.Position.InHorDistOf(pos, 12f)).ToList();
            float min = people.Count > 0 ? 11f : 15f;   // nobody there: take in more of the place instead
            Hooks.AutoFrame(map, pos, people, min, 21f, out Vector3 c, out float span, out float aspect);
            store.RequestAutomatic(map, c, kind, key, title, "", people, span, aspect);
        }

        /// <summary>One colonist and whoever is right next to them (a surgeon, a bonded partner).</summary>
        internal static void Portrait(PhotoStore store, Pawn p, string kind, string key, string title)
        {
            if (store == null || p?.Map == null) return;
            var people = new List<Pawn> { p };
            foreach (var other in p.Map.mapPawns.FreeColonistsSpawned)
                if (other != p && other.Position.InHorDistOf(p.Position, 4f)) people.Add(other);
            Hooks.AutoFrame(p.Map, p.Position, people, 9f, 15f, out Vector3 c, out float span, out float aspect);
            store.RequestAutomatic(p.Map, c, kind, key, title, "", people, span, aspect);
        }

        /// <summary>Thin hook: nowAbs is passed in so the probe can run "a year later" without waiting.</summary>
        public static void DailyCheck(PhotoStore store, int nowAbs)
        {
            var s = MomentFramesMod.Settings;
            if (store == null || s == null || Current.ProgramState != ProgramState.Playing) return;
            Map home = HomeMap;
            if (home == null) return;
            try { YearInPhotos.Check(store, GenLocalDate.Year(home)); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] year letter failed: " + e, 0x4D465931); }
            if (SettingsCapturePolicy.Automatic(s) && s.msHistoric)
            {
                int years = AnniversaryYears(nowAbs, Find.TickManager.gameStartAbsTick);
                if (years > 0 && store.AddMark("anniv:" + years))
                    store.RequestAutomatic(home, ColonyCenter(home), KindAnniversary, "anniv:" + years, "MF_Anniversary".Translate(years), "", home.mapPawns.FreeColonistsSpawned, WideSpan);
                int pop = PopulationMilestone(PawnsFinder.AllMaps_FreeColonists.Count);
                if (pop > 0 && store.AddMark("pop:" + pop))
                    store.RequestAutomatic(home, ColonyCenter(home), KindPopulation, "pop:" + pop, "MF_Population".Translate(pop), "", home.mapPawns.FreeColonistsSpawned, WideSpan);
            }
            if (s.msAnniversaryLetter) SendYearsAgoLetter(store, nowAbs, home);
        }

        /// <summary>Only on the first anniversary, at most one letter per day. A RimWorld year has 60 days, so "every year" would soon
        /// mean a letter every single day; once per photo keeps the letter count equal to last year's milestone count.
        /// The milestone taken exactly one year ago today (the earliest one if several happened that day).</summary>
        private static void SendYearsAgoLetter(PhotoStore store, int nowAbs, Map home)
        {
            float lon = Find.WorldGrid.LongLatOf(home.Tile).x;
            string dayKey = RecordEventPolicy.AnniversaryDayKey(GenDate.Year(nowAbs, lon), GenDate.DayOfYear(nowAbs, lon));
            if (store.HasMark(dayKey)) return;
            PhotoRecord best = null;
            foreach (var rec in store.Records)
            {
                if (!Kinds.IsMilestone(rec.kind)) continue;
                if (YearsAgoToday(nowAbs, rec.ticksAbs, lon) != 1) continue;
                // Upgrade old saves too: a photo's existing anniversary mark proves this calendar day already sent a letter.
                if (store.HasMark("letter:" + rec.id + ":1")) { store.AddMark(dayKey); return; }
                if (!rec.HasFile || !System.IO.File.Exists(store.FilePath(rec))) continue;
                if (best == null || rec.ticksAbs < best.ticksAbs) best = rec;
            }
            if (best == null) return;
            var def = DefDatabase<LetterDef>.GetNamedSilentFail("MF_PhotoLetter");
            if (def == null) return;
            var letter = (Letter_MFPhoto)LetterMaker.MakeLetter(def);
            letter.photoId = best.id;
            letter.Label = "MF_LetterYearsAgo".Translate(1, best.title);
            letter.title = letter.Label;
            letter.Text = "MF_LetterText".Translate(1, best.title, best.dateLabel, best.people.Count > 0 ? string.Join(", ", best.people) : "-");
            store.AddMark(dayKey); store.AddMark("letter:" + best.id + ":1");
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    /// <summary>"N years ago today" letter: opens the photo. Only the photo id is saved; if the record is gone the letter just closes.</summary>
    public class Letter_MFPhoto : ChoiceLetter
    {
        public string photoId;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                var rec = PhotoStore.Instance?.Get(photoId);
                if (rec != null && rec.HasFile)
                {
                    yield return new DiaOption("MF_ViewPhoto".Translate())
                    {
                        action = () => MainTabWindow_Album.Open(rec),
                        resolveTree = true
                    };
                }
                yield return Option_Close;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref photoId, "photoId");
        }
    }

    /// <summary>First gravship landing (Odyssey): photographed once, right after the landing cutscene, with the whole ship in frame.</summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    internal static class Patch_GravshipLanding
    {
        private static void Postfix()
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msHistoric) return;
                var store = PhotoStore.Instance;
                Map map = Find.CurrentMap;
                if (store == null || map == null) return;
                var engine = map.listerBuildings.AllBuildingsColonistOfClass<Building_GravEngine>().FirstOrDefault();
                if (engine == null) return;
                string key = "gravship:landing:" + Find.TickManager.TicksGame;   // every landing, not just the first
                CellRect rect = Gravships.Footprint(engine, map).ExpandedBy(Historic.GravshipPad).ClipInsideMap(map);
                Designator_TakePhoto.FitRect(rect, out Vector3 center, out float span, out float aspect);
                if (span < Historic.GravshipLeastSpan) span = Historic.GravshipLeastSpan;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => rect.Contains(p.Position)).ToList();
                store.RequestAutomatic(map, center, Historic.KindLanding, key, "MF_FirstLanding".Translate(engine.RenamableLabel), "", people, span, aspect);
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] landing hook failed: " + e);
            }
        }
    }
}
