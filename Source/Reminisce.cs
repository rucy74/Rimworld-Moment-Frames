using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    /// <summary>Reminiscing: colonists look at displayed photos for recreation, and two people who are in the same displayed photo sometimes
    /// talk about it ("remember when?"). Only photos on a display (frame, collage board, memorial stand; the projector for recreation only) count.
    /// Per map, a small index (pawn id -> fixed displays showing that pawn) is rebuilt only when a display spawns/despawns/changes its photos
    /// (Dirty) or the record list changes (PhotoStore.Version). Nothing here runs per tick: the joy giver is asked when a pawn seeks
    /// recreation, the interaction weight when a pawn starts a chat. Every entry point is guarded so a fault here can only cost that one
    /// recreation/chat attempt (one warning), never the pawn's job or the game tick.</summary>
    public static class Reminisce
    {
        public const string ThoughtViewed = "MF_ViewedOwnPhoto", ThoughtTogether = "MF_RememberedTogether";
        /// <summary>Ticks of looking at a display before the "looked at a photo of myself" memory is granted (~10 s).</summary>
        public const int ViewTicksForMemory = 600;   // ~10 s at 1x; earlier if recreation fills up first (the job ends then)

        private class MapIndex
        {
            public int version = -1, gen = -1;
            public readonly List<Building> displays = new List<Building>();
            public readonly Dictionary<string, List<Building>> byPawn = new Dictionary<string, List<Building>>();
        }

        private static readonly Dictionary<int, MapIndex> maps = new Dictionary<int, MapIndex>();
        private static readonly List<Building> empty = new List<Building>();
        private static readonly List<Building> moving = new List<Building>();
        private static List<ThingDef> displayDefs;
        private static int gen;

        /// <summary>How many times any map index was rebuilt (probe: must not grow per query).</summary>
        public static int Rebuilds { get; private set; }

        public static bool Enabled => MomentFramesMod.Settings != null && MomentFramesMod.Settings.reminisce;

        /// <summary>A display spawned, despawned or changed its photos.</summary>
        public static void Dirty() => gen++;

        /// <summary>New game: forget every map.</summary>
        public static void Clear()
        {
            maps.Clear();
            gen++;
        }

        /// <summary>Every def whose thing class shows photos (frames, collage, memorial, projector); enumerated with listerThings.ThingsOfDef.</summary>
        private static List<ThingDef> DisplayDefs =>
            displayDefs ?? (displayDefs = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.thingClass != null && typeof(IPhotoDisplay).IsAssignableFrom(d.thingClass)).ToList());

        private static MapIndex Get(Map map)
        {
            var store = PhotoStore.Instance;
            if (map == null || store == null) return null;
            if (!maps.TryGetValue(map.uniqueID, out var idx))
            {
                if (maps.Count > 16)
                    foreach (int dead in maps.Keys.Where(k => Find.Maps.All(m => m.uniqueID != k)).ToList()) maps.Remove(dead);
                idx = new MapIndex();
                maps[map.uniqueID] = idx;
            }
            if (idx.version != store.Version || idx.gen != gen) Rebuild(idx, map, store);
            return idx;
        }

        private static void Rebuild(MapIndex idx, Map map, PhotoStore store)
        {
            Rebuilds++;
            idx.version = store.Version;
            idx.gen = gen;
            idx.displays.Clear();
            idx.byPawn.Clear();
            foreach (var def in DisplayDefs)
            {
                var things = map.listerThings.ThingsOfDef(def);   // O(1) list per def; no scan of all buildings
                for (int i = 0; i < things.Count; i++)
                {
                    if (!(things[i] is Building b) || !(b is IPhotoDisplay d) || !d.FixedDisplay || !b.Spawned) continue;
                    bool any = false;
                    foreach (var rec in d.Displayed)
                    {
                        any = true;
                        if (rec.peopleIds.Count > 0) foreach (var id in rec.peopleIds) Add(idx, id, b);
                        else foreach (var name in rec.people) Add(idx, "name:" + name, b);   // records from before v2.4 have names only
                    }
                    if (any) idx.displays.Add(b);
                }
            }
        }

        private static void Add(MapIndex idx, string key, Building b)
        {
            if (!idx.byPawn.TryGetValue(key, out var list)) idx.byPawn[key] = list = new List<Building>();
            if (!list.Contains(b)) list.Add(b);
        }

        /// <summary>Fixed displays on this map that currently show at least one photo.</summary>
        public static List<Building> DisplayedFrames(Map map) => Get(map)?.displays ?? empty;

        /// <summary>Displays whose photo changes by itself (projectors) and currently show one — scanned per query, never indexed.
        /// The returned list is reused: consume it before the next call.</summary>
        public static List<Building> MovingDisplays(Map map)
        {
            moving.Clear();
            if (map == null) return moving;
            foreach (var def in DisplayDefs)
            {
                var things = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < things.Count; i++)
                    if (things[i] is Building b && b.Spawned && b is IPhotoDisplay d && !d.FixedDisplay && d.Displayed.Any()) moving.Add(b);
            }
            return moving;
        }

        private static List<Building> DisplaysWith(MapIndex idx, Pawn p)
        {
            if (idx.byPawn.TryGetValue(p.GetUniqueLoadID(), out var list)) return list;
            return idx.byPawn.TryGetValue("name:" + p.LabelShort, out list) ? list : null;
        }

        /// <summary>A fixed display on a's map showing a photo that both pawns are in (one at random when there are several), or null.</summary>
        public static Building SharedPhoto(Pawn a, Pawn b, out PhotoRecord photo)
        {
            photo = null;
            if (a == null || b == null || a == b || a.Map == null || a.Map != b.Map) return null;
            var idx = Get(a.Map);
            if (idx == null) return null;
            var candidates = DisplaysWith(idx, a);
            if (candidates == null) return null;
            Building pick = null;
            int n = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                foreach (var rec in ((IPhotoDisplay)candidates[i]).Displayed)
                {
                    if (!rec.Has(a) || !rec.Has(b)) continue;
                    n++;
                    if (Rand.Range(0, n) == 0) { pick = candidates[i]; photo = rec; }
                }
            }
            return pick;
        }

        public static Building SharedFrame(Pawn a, Pawn b) => SharedPhoto(a, b, out _);

        /// <summary>The first photo on the display that the viewer is in, or null.</summary>
        public static PhotoRecord PhotoWith(IPhotoDisplay d, Pawn viewer)
        {
            if (d == null || viewer == null) return null;
            foreach (var rec in d.Displayed) if (rec.HasFile && rec.Has(viewer)) return rec;
            return null;
        }

        /// <summary>Looking at a photo gives the mood memory only to a viewer who is in it.</summary>
        public static bool GrantsMemory(Pawn viewer, PhotoRecord rec) => viewer != null && rec != null && rec.HasFile && rec.Has(viewer);

        public static void GainViewed(Pawn viewer, IPhotoDisplay d)
        {
            var rec = PhotoWith(d, viewer);
            if (rec != null) Gain(viewer, ThoughtViewed, rec, null);
        }

        public static void GainTogether(Pawn pawn, Pawn other, PhotoRecord rec)
        {
            if (pawn != null && other != null && rec != null) Gain(pawn, ThoughtTogether, rec, other);
        }

        private static void Gain(Pawn pawn, string thoughtDef, PhotoRecord rec, Pawn other)
        {
            var memories = pawn.needs?.mood?.thoughts?.memories;
            if (memories == null) return;
            var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(thoughtDef);
            if (def == null) return;
            if (!(ThoughtMaker.MakeThought(def) is Thought_MFPhoto t)) return;
            t.title = rec.title;
            memories.TryGainMemory(t, other);
        }

        internal static void Warn(string where, Exception e) => Log.WarningOnce("[MomentFrames] " + where + " failed and was skipped (the pawn does something else): " + e, where.GetHashCode());
    }

    /// <summary>Memory that remembers which photo it is about; the caption is shown after the label.</summary>
    public class Thought_MFPhoto : Thought_Memory
    {
        public string title;

        public override string LabelCap => title.NullOrEmpty() ? base.LabelCap : base.LabelCap + ": " + title;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref title, "title");
        }
    }

    /// <summary>Recreation: go and look at a displayed photo. Photos the pawn is in are preferred; the projector counts too.</summary>
    public class JoyGiver_ViewPhoto : JoyGiver
    {
        private static readonly List<Building> candidates = new List<Building>();

        public override Job TryGiveJob(Pawn pawn)
        {
            try
            {
                if (!Reminisce.Enabled || pawn.Map == null) return null;
                bool outsideOk = JoyUtility.EnjoyableOutsideNow(pawn);
                candidates.Clear();
                Collect(pawn, Reminisce.DisplayedFrames(pawn.Map), outsideOk);
                Collect(pawn, Reminisce.MovingDisplays(pawn.Map), outsideOk);
                if (!candidates.TryRandomElementByWeight(f => Weight(pawn, f), out var pick)) return null;
                return JobMaker.MakeJob(def.jobDef, pick);
            }
            catch (Exception e)
            {
                Reminisce.Warn("photo recreation", e);
                return null;
            }
            finally
            {
                candidates.Clear();
            }
        }

        private static void Collect(Pawn pawn, List<Building> displays, bool outsideOk)
        {
            for (int i = 0; i < displays.Count; i++)
            {
                var f = displays[i];
                if (!f.Spawned || f.Faction != Faction.OfPlayer || f.IsForbidden(pawn) || f.Fogged()) continue;
                if (!outsideOk && !f.Position.Roofed(f.Map)) continue;
                if (!f.IsSociallyProper(pawn) || !f.IsPoliticallyProper(pawn) || f.VacuumConcernTo(pawn)) continue;
                if (!pawn.CanReserveAndReach(f, PathEndMode.Touch, Danger.None)) continue;
                candidates.Add(f);
            }
        }

        public static float Weight(Pawn pawn, Building f)
        {
            var d = f as IPhotoDisplay;
            float w = d == null ? 1f : (!d.FixedDisplay ? 2f : (Reminisce.PhotoWith(d, pawn) != null ? 4f : 1f));
            return w + Mathf.Max(f.GetStatValue(StatDefOf.Beauty), 0f) * 0.05f;
        }
    }

    /// <summary>Stand at the display and look at it (same shape as viewing art). After a while, a viewer who is in a shown photo gets the memory.</summary>
    public class JobDriver_ViewPhoto : JobDriver_VisitJoyThing
    {
        private int viewed;
        private bool granted;

        private Building Display => job.GetTarget(TargetIndex.A).Thing as Building;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref viewed, "viewed", 0);
            Scribe_Values.Look(ref granted, "granted", false);
        }

        protected override void WaitTickAction(int delta)
        {
            var f = Display;
            if (f == null || !(f is IPhotoDisplay d))
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            pawn.rotationTracker.FaceTarget(f);
            viewed += delta;
            float baseBeauty = f.def.GetStatValueAbstract(StatDefOf.Beauty);
            float factor = baseBeauty > 0f ? Mathf.Max(f.GetStatValue(StatDefOf.Beauty) / baseBeauty, 0f) : 1f;
            // The memory comes after ~10 s of looking — or right when this batch's joy gain is about to fill recreation, since JoyTickCheckEnd
            // then ends the job: a legendary frame (beauty x8, quality-boosted JoyGainFactor) fills it from 0.9 in three 15-tick batches, and the
            // pawn still came here to look. rawGain mirrors JoyTickCheckEnd's gain before the tolerance factor (<= 1), so it errs one batch early.
            var joy = pawn.needs.joy;
            float rawGain = factor * job.def.joyGainRate * 0.36f / 2500f * delta * f.GetStatValue(StatDefOf.JoyGainFactor);
            bool aboutToFill = joy != null && joy.CurLevel + rawGain > 0.9999f;
            if (!granted && (viewed >= Reminisce.ViewTicksForMemory || aboutToFill))
            {
                granted = true;
                try { Reminisce.GainViewed(pawn, d); }
                catch (Exception e) { Reminisce.Warn("photo memory", e); }
            }
            pawn.GainComfortFromCellIfPossible(delta);
            JoyUtility.JoyTickCheckEnd(pawn, delta, JoyTickFullJoyAction.EndJob, factor, f);
        }
    }

    /// <summary>"Remember when?" — only between two pawns who are both in a photo displayed somewhere on their map.
    /// Cheap gate first (CountFor is cached per store version), then the index lookup.</summary>
    public class InteractionWorker_RememberWhen : InteractionWorker
    {
        public const float SelectionWeight = 0.12f;   // chitchat is ~1, deep talk ~0.075

        public override float RandomSelectionWeight(Pawn initiator, Pawn recipient)
        {
            try
            {
                if (!Reminisce.Enabled) return 0f;
                var store = PhotoStore.Instance;
                if (store == null || store.CountFor(initiator) == 0 || store.CountFor(recipient) == 0) return 0f;
                return Reminisce.SharedFrame(initiator, recipient) != null ? SelectionWeight : 0f;
            }
            catch (Exception e)
            {
                Reminisce.Warn("remember-when weight", e);
                return 0f;
            }
        }

        public override void Interacted(Pawn initiator, Pawn recipient, List<RulePackDef> extraSentencePacks, out string letterText, out string letterLabel, out LetterDef letterDef, out LookTargets lookTargets)
        {
            base.Interacted(initiator, recipient, extraSentencePacks, out letterText, out letterLabel, out letterDef, out lookTargets);
            try
            {
                if (Reminisce.SharedPhoto(initiator, recipient, out var rec) == null || rec == null) return;
                Reminisce.GainTogether(initiator, recipient, rec);
                Reminisce.GainTogether(recipient, initiator, rec);
            }
            catch (Exception e)
            {
                Reminisce.Warn("remember-when memory", e);
            }
        }
    }
}
