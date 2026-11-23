using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Defs used by the group photo, resolved once (a missing def is logged once and keeps the spot's button disabled instead of throwing).</summary>
    internal static class MFDefs
    {
        private static bool resolved;
        private static DutyDef poseDuty;
        private static JobDef pose;

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            poseDuty = DefDatabase<DutyDef>.GetNamedSilentFail("MF_PoseForPhoto");
            pose = DefDatabase<JobDef>.GetNamedSilentFail("MF_Pose");
            if (poseDuty == null || pose == null) Log.Error("[MomentFrames] group photo defs missing (MF_PoseForPhoto / MF_Pose): the photo spot stays disabled.");
        }

        public static DutyDef PoseDuty { get { Resolve(); return poseDuty; } }
        public static JobDef Pose { get { Resolve(); return pose; } }
        public static bool Ready { get { Resolve(); return poseDuty != null && pose != null; } }
    }

    /// <summary>Where a group lines up: rows north of (behind) the photo spot, facing south, six abreast for a small group and up to ten for a
    /// big one (so the picture stays near 3:2 instead of growing tall), rows balanced, the front row nearest the spot. Cells that cannot be
    /// stood on (walls, furniture, doors, fog) or that are in another room than the spot (behind a wall, across a door) are skipped and the
    /// people move to the next row.</summary>
    public static class GroupFormation
    {
        public const int MaxRows = 10;
        public const int PreviewRefreshTicks = 60;   // the placement preview recomputes this often (a wall may have gone up)

        /// <summary>People per row: six up to twelve people, eight up to twenty-four, ten beyond.</summary>
        public static int PerRow(int count) => count <= 12 ? 6 : count <= 24 ? 8 : 10;

        public static List<IntVec3> Cells(IntVec3 spot, int count, Map map) => Cells(spot, count, map, Rot4.North);

        public static List<IntVec3> Cells(IntVec3 spot, int count, Map map, Rot4 direction)
        {
            var studio = Building_PhotoStudio.At(map,spot);
            if (studio != null) return Building_PhotoStudio.Formation(map,spot,studio.Rotation,count);
            var cells = new List<IntVec3>(Math.Max(0, count));
            if (count <= 0 || map == null) return cells;
            int maxPerRow = PerRow(count);
            int rows = (count + maxPerRow - 1) / maxPerRow;
            int perRow = (count + rows - 1) / rows;
            Room room = spot.InBounds(map) ? spot.GetRoom(map) : null;
            for (int r = 0; r < MaxRows && cells.Count < count; r++)
            {
                int n = Math.Min(perRow, count - cells.Count);
                int z = spot.z + 1 + r, x0 = spot.x - (n - 1) / 2;
                for (int i = 0; i < n; i++)
                {
                    int dx = x0 + i - spot.x, dz = z - spot.z;
                    var c = direction == Rot4.East ? spot + new IntVec3(dz, 0, -dx)
                        : direction == Rot4.South ? spot + new IntVec3(-dx, 0, -dz)
                        : direction == Rot4.West ? spot + new IntVec3(-dz, 0, dx) : new IntVec3(x0 + i, 0, z);
                    if (Usable(c, room, map)) cells.Add(c);
                }
            }
            return cells;
        }

        /// <summary>Standable, no door, not fogged, and in the spot's room — so a wall right behind the spot does not put people in the room beyond it.</summary>
        public static bool Usable(IntVec3 c, Room spotRoom, Map map) => c.InBounds(map) && c.Standable(map) && c.GetDoor(map) == null && !c.Fogged(map) && c.GetRoom(map) == spotRoom;

        private static Map cacheMap;
        private static IntVec3 cacheSpot;
        private static int cacheCount, cacheDirection, cacheTick = -1;
        private static readonly List<IntVec3> cached = new List<IntVec3>();

        /// <summary>Cells for the placement/selection preview, which asks every frame: recomputed only when the spot, the head count or the map
        /// changes, or every PreviewRefreshTicks. The returned list is shared — read only.</summary>
        public static List<IntVec3> CachedCells(IntVec3 spot, int count, Map map)
            => CachedCells(spot,count,map,Rot4.North);
        public static List<IntVec3> CachedCells(IntVec3 spot,int count,Map map,Rot4 direction)
        {
            int tick = Find.TickManager.TicksGame;
            if (map != cacheMap || spot != cacheSpot || count != cacheCount || direction.AsInt!=cacheDirection || tick - cacheTick >= PreviewRefreshTicks || tick < cacheTick)
            {
                cached.Clear();
                cached.AddRange(Cells(spot, count, map,direction));
                cacheMap = map;
                cacheSpot = spot;
                cacheCount = count;
                cacheDirection=direction.AsInt;
                cacheTick = tick;
            }
            return cached;
        }

        public static void ClearCache()
        {
            cached.Clear();
            cacheMap = null;
            cacheTick = -1;
        }
    }

    /// <summary>The group photo: who may join, starting the gathering, and the lord that runs it.</summary>
    public static class GroupPhoto
    {
        public const string Kind = "group";
        public const int WarnTicks = 5000;             // two in-game hours without everyone in place: one message naming who is still missing
        public const int GiveUpTicks = 15000;          // six hours: the line-up disbands without a photo (someone never came, or the map was never viewed)
        public const int HoldTicks = 60;               // everyone on their cell for a second before the shutter (arrivals settle, faces turn)
        public const int Padding = 3;                  // cells of margin around the group in the picture
        public const float LeastSpan = 10f;            // even a pair is shot with this much of the room around them
        public const int TickEvery = 15;               // the lord's state machine runs every this many ticks
        public const int UnfitEvery = 60;              // members are re-checked for fitness every this many ticks
        public const int CallBackEvery = 240;          // a free drop-out is asked to reconsider their job this often
        public const int RejoinCooldownTicks = 300;    // someone who dropped out may come back after this (no flapping in and out)
        public const int MessageCooldownTicks = 2500;  // at most one message per person per this many ticks
        public const int WatchdogTicks = 900;          // a capture whose callback never came is given up after this
        public const int StartRetries = 10;            // RequestNow refusals on the current map before giving up (should not happen)
        public const float EligibleCacheSeconds = 0.5f;

        /// <summary>Can this person be called to line up? A healthy sleeper may be woken by the explicit call. They must be on their feet,
        /// not drafted, not in a mental state, not bound to any other lord
        /// (a party, a ritual, a caravan forming), fit by the vanilla party rules for both starting and staying (bleeding, starving, exhausted,
        /// needing treatment, prisoners, slaves, wild men), and not doing something a photo call must not interrupt (Busy). Babies are carried,
        /// so they cannot pose.</summary>
        public static bool CanJoin(Pawn p) => CanJoin(p, null);
        internal static bool CanJoin(Pawn p, LordJob_MFGroupPhoto editing) => JoinProblemKey(p, editing, true) == null;
        internal static bool CanJoin(Pawn p, LordJob gathering, bool allowMedicalRest, bool allowSleeping = true)
            => JoinProblemKey(p, gathering, allowSleeping, allowMedicalRest) == null;

        /// <summary>Why this colonist cannot be called to pose right now, in one phrase. Ready pawns return an empty string.</summary>
        public static string WhyNot(Pawn p) => WhyNot(p, null);
        internal static string WhyNot(Pawn p, LordJob_MFGroupPhoto editing)
        {
            string key = JoinProblemKey(p, editing, true);
            return key == null ? "" : key == "MF_Why_OtherGathering" ? PhotoGatheringAccess.Blocked(p) : key.Translate().ToString();
        }
        internal static string WhyNot(Pawn p, LordJob gathering, bool allowMedicalRest, bool allowSleeping = true)
        {
            string key = JoinProblemKey(p, gathering, allowSleeping, allowMedicalRest);
            return key == null ? "" : key == "MF_Why_OtherGathering" ? PhotoGatheringAccess.Blocked(p) : key.Translate().ToString();
        }
        internal static bool MedicalRest(Pawn p) => p != null && !p.Dead && !p.Downed
            && (HealthAIUtility.ShouldSeekMedicalRest(p) || p.health.hediffSet.HasTendableNonInjuryNonMissingPartHediff());
        internal static string MedicalRestWarning(Pawn p) => MedicalRest(p) ? "MF_PreparationMedicalWarning".Translate().ToString() : "";

        // Keep selection, displayed reasons and attendance on the same rules. Only an explicit call may wake a sleeper;
        // automatic rejoining and readiness require them to be awake already.
        internal static string AttendanceProblem(Pawn p, LordJob_MFGroupPhoto gathering)
        {
            string key = JoinProblemKey(p, gathering, false, gathering?.MedicalRestAllowed(p) == true);
            return key == null ? "" : key.Translate().ToString();
        }

        private static string JoinProblemKey(Pawn p, LordJob gathering, bool allowSleeping, bool allowMedicalRest = false)
        {
            if (p == null || p.Dead) return "MF_Why_Dead";
            if (!p.Spawned) return "MF_Why_OtherMap";
            if (!p.RaceProps.Humanlike || !p.IsFreeColonist) return "MF_Reunion_NotOurs";
            if (p.DevelopmentalStage.Baby()) return "MF_Why_Baby";
            if (p.Downed) return "MF_Why_Downed";
            if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) return "MF_Why_Downed";
            if (p.Drafted) return "MF_Why_Drafted";
            if (p.InMentalState) return "MF_Why_Mental";
            if (p.GetLord() != null && p.GetLord().LordJob != gathering && !PhotoGatheringAccess.CanLeave(p)) return "MF_Why_OtherGathering";
            string busy = BusyProblemKey(p, allowMedicalRest);
            if (busy != null) return busy;
            if (p.needs.food?.Starving == true) return "MF_Why_Starving";
            if (p.health.hediffSet.BleedRateTotal > 0f) return "MF_Why_Bleeding";
            if (p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss)?.Severity > .2f) return "MF_Why_BloodLoss";
            if (p.needs.rest != null && p.needs.rest.CurCategory == RestCategory.Exhausted) return "MF_Why_Exhausted";
            if (!allowMedicalRest && p.health.hediffSet.HasTendableNonInjuryNonMissingPartHediff()) return "MF_Why_Treatment";
            if (p.health.hediffSet.InLabor()) return "MF_Why_Labor";
            if (!allowSleeping && !p.Awake()) return "MF_Why_Asleep";
            if (allowMedicalRest && MedicalRest(p)) return null;
            if (!GatheringsUtility.PawnCanStartOrContinueGathering(p)
                || (p.Awake() && !GatheringsUtility.ShouldGuestKeepAttendingGathering(p))) return "MF_Why_Unavailable";
            return null;
        }

        /// <summary>Work an explicit photo call must not interrupt. The pose duty still yields to emergencies that arise after the call.</summary>
        public static bool Busy(Pawn p) => p != null && BusyProblemKey(p) != null;

        private static string BusyProblemKey(Pawn p, bool allowMedicalRest = false)
        {
            var job = p.CurJob;
            if (job != null && job.playerForced) return "MF_Why_Busy";
            if (job != null && job.def == JobDefOf.DoBill && job.bill?.recipe != null && job.bill.recipe.IsSurgery) return "MF_Why_Surgery";
            if (p.InBed() && p.BillStack != null && p.BillStack.AnyShouldDoNow) return "MF_Why_Surgery";
            if (!allowMedicalRest && p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)) return "MF_Why_MedicalRest";
            if (job != null && (job.def == JobDefOf.BeatFire || job.def == JobDefOf.ExtinguishFiresNearby
                || job.def == JobDefOf.Rescue || job.def == JobDefOf.TendPatient || job.def == JobDefOf.FeedPatient
                || job.def == JobDefOf.CarryToMomAfterBirth || job.def == JobDefOf.Flee
                || job.def == JobDefOf.FleeAndCower || job.def == JobDefOf.FleeAndCowerShort)) return "MF_Why_Emergency";
            return null;
        }

        private static Map eligibleMap;
        private static IntVec3 eligibleSpot;
        private static float eligibleAt = -1f;
        private static List<Pawn> eligible = new List<Pawn>();

        /// <summary>Colonists on this map who could line up now, youngest first (children in the front row). Cached for half a second — the spot's
        /// gizmo, inspect string and preview all ask every frame. The list is shared: read only; Start re-checks every pawn anyway.</summary>
        public static IReadOnlyList<Pawn> Eligible(Map map, IntVec3 spot)
        {
            if (map == null) return new List<Pawn>();
            if (map == eligibleMap && spot == eligibleSpot && Time.realtimeSinceStartup - eligibleAt < EligibleCacheSeconds) return eligible;
            var list = new List<Pawn>();
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
                if (CanJoin(p) && p.CanReach(spot, PathEndMode.Touch, Danger.Some)) list.Add(p);
            list.Sort((a, b) => a.ageTracker.AgeBiologicalYears.CompareTo(b.ageTracker.AgeBiologicalYears));
            eligibleMap = map;
            eligibleSpot = spot;
            eligibleAt = Time.realtimeSinceStartup;
            eligible = list;
            return list;
        }

        public static void ForgetEligible() => eligibleAt = -1f;

        /// <summary>New game or load (PhotoStore constructor): drop every reference to the previous game's map and pawns.</summary>
        public static void Reset()
        {
            eligibleMap = null;
            eligible = new List<Pawn>();
            eligibleAt = -1f;
            GroupFormation.ClearCache();
        }

        public static LordJob_MFGroupPhoto ActiveJob(Map map, IntVec3 spot)
        {
            if (map == null) return null;
            var lords = map.lordManager.lords;
            for (int i = 0; i < lords.Count; i++)
                if (lords[i].LordJob is LordJob_MFGroupPhoto j && j.spot == spot && !j.Finished) return j;
            return null;
        }

        public static AcceptanceReport CanStart(Map map, IntVec3 spot, out IReadOnlyList<Pawn> pawns)
        {
            pawns = Eligible(map, spot);
            if (!MFDefs.Ready) return "MF_GroupDefsMissing".Translate();
            if (ActiveJob(map, spot) != null) return "MF_GroupBusy".Translate();
            if (map.dangerWatcher.DangerRating == StoryDanger.High) return "MF_GroupDanger".Translate();
            if (pawns.Count == 0) return "MF_GroupNoOne".Translate();
            return true;
        }

        /// <summary>Lines the pawns up behind the spot. Everyone is re-checked (the list may be half a second old); their current jobs end the way
        /// a ritual start ends them, and the pose duty is picked up on the next tick.</summary>
        public static Lord Start(Map map, IntVec3 spot, IReadOnlyList<Pawn> pawns)
        {
            if (map == null || pawns == null || !MFDefs.Ready) return null;
            var fresh = pawns.Where(CanJoin).ToList();
            if (fresh.Count == 0) return null;
            var cells = GroupFormation.Cells(spot, fresh.Count, map);
            if (cells.Count == 0)
            {
                Messages.Message("MF_GroupNoRoom".Translate(), new TargetInfo(spot, map), MessageTypeDefOf.RejectInput, false);
                return null;
            }
            var members = fresh.Take(cells.Count).ToList();
            if (members.Count < fresh.Count) Messages.Message("MF_GroupLessRoom".Translate(members.Count, fresh.Count), new TargetInfo(spot, map), MessageTypeDefOf.CautionInput, false);
            foreach (var p in members)
                PhotoGatheringAccess.Leave(p);
            foreach (var p in members)
                if (p.jobs?.curJob != null) p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            var job = new LordJob_MFGroupPhoto(spot, members, cells.Take(members.Count).ToList());
            var lord = LordMaker.MakeNewLord(Faction.OfPlayer, job, map, members);
            ForgetEligible();
            Messages.Message("MF_GroupStarted".Translate(members.Count), new TargetInfo(spot, map), MessageTypeDefOf.NeutralEvent, false);
            return lord;
        }
        internal static Lord StartPrepared(Map map,IntVec3 spot,IList<Pawn> selected,Rot4 direction,LordJob_MFGroupPhoto editing=null,
            IDictionary<Pawn,Pawn> babyGuardians=null, int reactionMode = -1, bool peopleFocus = false, IDictionary<string,int> reactions=null, bool useSavedFrame=true, IDictionary<string,int> faces=null, IDictionary<string,IntVec3> positions=null)
        {
            direction = Building_PhotoStudio.At(map,spot)?.Rotation ?? direction;
            var plan=PhotoGatheringPlan.Create(map,spot,selected,direction,editing,babyGuardians,positions);
            if(!plan.CanStart) { Messages.Message(plan.Reason,MessageTypeDefOf.RejectInput,false);return null; }
            if(editing!=null)
            {
                if(!editing.ReplacePhotoRoster(plan.Moving,plan.Cells,direction,plan.BabyGuardians,spot)) return null;
                editing.SetPreparationOptions(plan.Moving,reactionMode,peopleFocus,reactions,useSavedFrame,faces); return editing.lord;
            }
            foreach(var p in plan.Moving) PhotoGatheringAccess.Leave(p);
            foreach(var p in plan.Moving) if(p.jobs?.curJob!=null) p.jobs.EndCurrentJob(JobCondition.InterruptForced,false);
            var job=new LordJob_MFGroupPhoto(spot,plan.Moving,plan.Cells,direction);
            job.MakeBabyGuests(plan.BabyGuardians);
            job.SetPreparationOptions(plan.Moving,reactionMode,peopleFocus,reactions,useSavedFrame,faces);
            var lord=LordMaker.MakeNewLord(Faction.OfPlayer,job,map,plan.Moving);
            ForgetEligible();Messages.Message("MF_GroupStarted".Translate(plan.Attending.Count),new TargetInfo(spot,map),MessageTypeDefOf.NeutralEvent,false);
            return lord;
        }
    }

    /// <summary>The photo spot (Misc tab, placed like a marriage spot: no cost, no work). Select it to start a group photo, to take it right now
    /// with whoever stands in place, to take another once it is taken, or to let everyone go; the line-up is previewed while it is selected or
    /// being placed. The player may drag the framing on the map ("set the framing"): every group photo here is then framed exactly so.</summary>
    [StaticConstructorOnStartup]
    public class Building_PhotoSpot : Building
    {
        internal static int HiddenInCaptures;
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (!PhotoCapture.HideGuides || this is Building_PhotoStudio) base.DrawAt(drawLoc, flip);
            else HiddenInCaptures++;
        }
        private static Texture2D cancelIcon;
        internal static Texture2D CancelIcon => cancelIcon ?? (cancelIcon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", false) ?? BaseContent.BadTex);
        public static readonly Color FrameColor = new Color(1f, 0.78f, 0.3f);

        // The player's own framing (dragged on the map). None: the picture fits whoever stands in the line-up.
        private bool framed;
        private Vector3 frameCenter;
        private float frameSpan, frameAspect;
        private List<IntVec3> frameCells;   // drawing cache, not saved
        private int preparationDirection;
        internal Rot4 PreparationDirection { get=>this is Building_PhotoStudio?Rotation:new Rot4(Mathf.Clamp(preparationDirection,0,3));set=>preparationDirection=value.AsInt; }

        public bool Framed => framed;

        public bool TryGetFrame(out Vector3 center, out float span, out float aspect)
        {
            center = frameCenter;
            span = frameSpan;
            aspect = frameAspect;
            return framed;
        }

        public void SetFrame(Vector3 center, float span, float aspect)
        {
            framed = true;
            frameCenter = center;
            frameSpan = Mathf.Clamp(span, PhotoCapture.MinSpan, PhotoCapture.MaxSpan);
            frameAspect = PhotoCapture.ClampAspect(aspect);
            frameCells = null;
        }

        public void ClearFrame()
        {
            framed = false;
            frameCells = null;
        }

        /// <summary>The map cells the chosen framing covers.</summary>
        public CellRect FrameRect => PhotoCapture.CoveredRect(frameCenter, frameSpan, Map, frameAspect);

        /// <summary>The same cells as a list for drawing (computed once per framing), or null when the framing is automatic.</summary>
        public List<IntVec3> FrameCells => framed && Spawned ? frameCells ?? (frameCells = FrameRect.Cells.ToList()) : null;

        /// <summary>Where the line-up stands (or would stand now): the cells of the gathering in progress, else the preview for everyone free.</summary>
        public List<IntVec3> LineUpCells()
        {
            var job = GroupPhoto.ActiveJob(Map, Position);
            var draft=Dialog_GroupPhotoSetup.PreviewCellsFor(this);
            return draft ?? (job != null ? job.CellList : GroupFormation.CachedCells(Position,Math.Max(1,GroupPhoto.Eligible(Map,Position).Count),Map,PreparationDirection));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref framed, "mfFramed", false);
            Scribe_Values.Look(ref frameCenter, "mfFrameCenter");
            Scribe_Values.Look(ref frameSpan, "mfFrameSpan", 0f);
            Scribe_Values.Look(ref frameAspect, "mfFrameAspect", 0f);
            Scribe_Values.Look(ref preparationDirection,"mfPreparationDirection",0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && framed && (frameSpan <= 0f || frameAspect <= 0f)) framed = false;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            if (Faction != Faction.OfPlayer || !Spawned) yield break;
            var locations=Map.GetComponent<PhotoLocationMapComponent>();
            yield return new Command_Toggle { defaultLabel="MF_BirthdayDefaultSpot".Translate(), defaultDesc="MF_BirthdayDefaultSpotHelp".Translate(), icon=MFIcons.Camera,
                isActive=()=>locations.IsDefault(this), toggleAction=()=>locations.Select(locations.IsDefault(this)?null:this) };
            var job = GroupPhoto.ActiveJob(Map, Position);
            if (job == null)
            {
                var report = GroupPhoto.CanStart(Map, Position, out var pawns);
                var start = new Command_Action
                {
                    defaultLabel = "MF_GroupSetupTitle".Translate(),
                    defaultDesc = "MF_GroupSetupHelp".Translate()+"\n\n"+"MF_GroupSetupReady".Translate(),
                    icon = MFIcons.Camera,
                    action = () => Find.WindowStack.Add(new Dialog_GroupPhotoSetup(this))
                };
                if (!report.Accepted) start.Disable(report.Reason);
                yield return start;
                var pets = new Command_Action
                {
                    defaultLabel = "MF_PetPairGizmo".Translate(), defaultDesc = "MF_PetPairGizmoDesc".Translate(), icon = MFIcons.Camera,
                    action = () => Find.WindowStack.Add(new Dialog_PetPhotoSetup(this))
                };
                if (!PetPhotos.Animals(Map).Any()) pets.Disable("MF_Pet_NoAnimals".Translate());
                yield return pets;
            }
            else
            {
                // Before the first picture: take it now / cancel. After it the line holds: take another / dismiss.
                if (job.IsBirthday) yield return new Command_Action
                {
                    defaultLabel = "MF_BirthdayEditGuests".Translate(), defaultDesc = "MF_BirthdaySetupHelp".Translate(), icon = MFIcons.Camera,
                    action = () => Birthday.OpenSetup(job.BirthdayPawn, job.BirthdayAge, job)
                };
                else if(!job.IsPetPhoto && job.ReunionOf.NullOrEmpty() && job.Takes==0 && !job.Shooting) yield return new Command_Action
                {
                    defaultLabel="MF_GroupSetupEdit".Translate(),defaultDesc="MF_GroupSetupHelp".Translate(),icon=MFIcons.Camera,
                    action=()=>Find.WindowStack.Add(new Dialog_GroupPhotoSetup(this,job))
                };
                bool taken = job.Takes > 0;
                int inPlace = job.InPlace;
                var shoot = new Command_Action
                {
                    defaultLabel = (job.IsBirthday ? "MF_BirthdayShoot" : taken ? "MF_GroupRetake" : "MF_GroupShootNow").Translate(inPlace + (job.FixedBirthdaySubject ? 1 : 0) + job.CarriedPhotoBabies.Count(b=>b!=job.BirthdayPawn && job.BabyReady(b))),
                    defaultDesc = (job.IsBirthday ? "MF_BirthdayShootDesc" : taken ? "MF_GroupRetakeDesc" : "MF_GroupShootNowDesc").Translate(),
                    icon = MFIcons.Camera,
                    action = job.ShootNow
                };
                if (inPlace == 0) shoot.Disable("MF_GroupNoOneInPlace".Translate());
                else if ((job.IsBirthday || job.CarriedPhotoBabies.Any()) && (!job.Complete || inPlace != job.lord.ownedPawns.Count)) shoot.Disable("MF_BirthdayWaitAll".Translate());
                else if (job.Shooting) shoot.Disable("MF_GroupBusy".Translate());
                yield return shoot;
                yield return new Command_Action
                {
                    defaultLabel = (taken ? "MF_GroupDismiss" : "MF_GroupCancel").Translate(),
                    defaultDesc = (taken ? "MF_GroupDismissDesc" : "MF_GroupCancelDesc").Translate(),
                    icon = CancelIcon,
                    action = job.Cancel
                };
            }
            if (job?.IsBirthday == true) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MF_SpotFrame".Translate(),
                defaultDesc = "MF_SpotFrameDesc".Translate(),
                icon = MFIcons.SpotFrame,
                action = () => Find.DesignatorManager.Select(new Designator_MFSpotFrame(this))
            };
            if (framed)
                yield return new Command_Action
                {
                    defaultLabel = "MF_SpotFrameClear".Translate(),
                    defaultDesc = "MF_SpotFrameClearDesc".Translate(),
                    icon = MFIcons.SpotFrame,
                    iconDrawScale = 0.8f,
                    action = ClearFrame
                };
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            if (!Spawned || Faction != Faction.OfPlayer) return sb.ToString();
            if (sb.Length > 0) sb.AppendLine();
            var job = GroupPhoto.ActiveJob(Map, Position);
            if (job != null)
            {
                sb.Append(PhotoGatheringProgress.Summary(job));
                string details=PhotoGatheringProgress.Details(job);
                if(!details.NullOrEmpty()) sb.AppendLine().Append(details);
            }
            else sb.Append("MF_GroupIdle".Translate(GroupPhoto.Eligible(Map, Position).Count));
            if (framed)
            {
                var rect = FrameRect;
                sb.AppendLine().Append("MF_SpotFramed".Translate(rect.Width, rect.Height));
                int outside = 0;
                var cells = LineUpCells();
                for (int i = 0; i < cells.Count; i++) if (!rect.Contains(cells[i])) outside++;
                if (outside > 0) sb.AppendLine().Append("MF_SpotFrameMisses".Translate(outside));
            }
            else sb.AppendLine().Append("MF_SpotAuto".Translate());
            return sb.ToString();
        }
    }

    /// <summary>The spot's "set the framing" tool: drag a rectangle on the map (a click gives the default size) and every group photo at that spot
    /// is framed exactly so. While it is active the line-up (white) and the current framing (amber) are drawn, so the player sees who will be in
    /// the picture. One drag and it hands the selection back to the spot.</summary>
    public class Designator_MFSpotFrame : Designator
    {
        private readonly Building_PhotoSpot spot;
        private bool done;

        public Designator_MFSpotFrame(Building_PhotoSpot spot)
        {
            this.spot = spot;
            defaultLabel = "MF_SpotFrame".Translate();
            defaultDesc = "MF_SpotFrameDesc".Translate();
            icon = MFIcons.SpotFrame;
            useMouseIcon = true;
            soundDragSustain = SoundDefOf.Designate_DragStandard;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            soundSucceeded = SoundDefOf.Designate_ZoneAdd;
        }

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

        public override void DrawMouseAttachments()
        {
            base.DrawMouseAttachments();
            if(PhotoCapture.HideGuides) return;
            SharedPhotoUI.DrawDragHint(Find.DesignatorManager.Dragger?.Dragging==true);
        }

        private bool SpotOk => spot != null && spot.Spawned && spot.Map == Map;

        public override AcceptanceReport CanDesignateCell(IntVec3 loc) => SpotOk && loc.InBounds(Map);

        public override void DesignateSingleCell(IntVec3 c)
        {
            if (!SpotOk) return;
            spot.SetFrame(c.ToVector3Shifted(), MomentFramesMod.Settings.spanCells, PhotoCapture.DefaultAspect);
            done = true;
        }

        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            var list = cells.Where(c => c.InBounds(Map)).ToList();
            if (list.Count == 0 || !SpotOk) return;
            if (list.Count == 1) DesignateSingleCell(list[0]);
            else
            {
                var rect = CellRect.FromLimits(list.Min(c => c.x), list.Min(c => c.z), list.Max(c => c.x), list.Max(c => c.z));
                Designator_TakePhoto.FitRect(rect, out Vector3 center, out float span, out float aspect);
                spot.SetFrame(center, span, aspect);
                done = true;
            }
            Finalize(true);
        }

        public override void SelectedUpdate()
        {
            // Hand the selection back a frame later (not from inside the drag's own end), so the spot shows the new framing and what it misses.
            if (done || !SpotOk)
            {
                Find.DesignatorManager.Deselect();
                if (done && SpotOk) Find.Selector.Select(spot, false);
                return;
            }
            GenUI.RenderMouseoverBracket();
            var lineUp = spot.LineUpCells();
            if (lineUp.Count > 0) GenDraw.DrawFieldEdges(lineUp, Color.white);
            CellRect rect;
            var dragger = Find.DesignatorManager.Dragger;
            if (dragger != null && dragger.Dragging && dragger.DragCells.Count > 0)
            {
                var cells = dragger.DragCells;
                rect = CellRect.FromLimits(cells.Min(c => c.x), cells.Min(c => c.z), cells.Max(c => c.x), cells.Max(c => c.z));
                Designator_TakePhoto.FitRect(rect, out Vector3 center, out float span, out float aspect);
                rect = PhotoCapture.CoveredRect(center, span, Map, aspect);
            }
            else if (spot.Framed) rect = spot.FrameRect;
            else
            {
                IntVec3 c = UI.MouseCell();
                if (!c.InBounds(Map)) return;
                rect = PhotoCapture.CoveredRect(c.ToVector3Shifted(), MomentFramesMod.Settings.spanCells, Map);
            }
            GenDraw.DrawFieldEdges(rect.Cells.ToList(), Building_PhotoSpot.FrameColor);
        }
    }

    /// <summary>Draws the line-up cells while the spot is placed or selected (the def sets drawPlaceWorkersWhileSelected). While a group photo is
    /// gathering, the cells actually assigned are shown; otherwise the cells everyone on the map would take. No allocation per frame.</summary>
    public class PlaceWorker_MFPhotoSpot : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = thing != null && thing.Spawned ? thing.Map : Find.CurrentMap;
            if (map == null) return;
            List<IntVec3> cells;
            var job = thing != null ? GroupPhoto.ActiveJob(map, center) : null;
            if (job != null) cells = job.CellList;
            else if (thing is Building_PhotoSpot selectedSpot) cells = selectedSpot.LineUpCells();
            else
            {
                int n = thing != null ? GroupPhoto.Eligible(map, center).Count : map.mapPawns.FreeColonistsSpawnedCount;
                cells = typeof(Building_PhotoStudio).IsAssignableFrom(def.thingClass)
                    ? Building_PhotoStudio.Formation(map,center,rot,Math.Max(1,n)) : GroupFormation.CachedCells(center, Math.Max(1, n), map);
            }
            if (cells.Count > 0) GenDraw.DrawFieldEdges(cells, Color.white);
            var frame = (thing as Building_PhotoSpot)?.FrameCells;
            if (frame != null) GenDraw.DrawFieldEdges(frame, Building_PhotoSpot.FrameColor);
        }
    }

    /// <summary>The gathering: everyone called walks to their cell and faces south; when every one of them has stood still for a second the photo
    /// is taken. Then the line holds — the player takes another (same line-up, same framing) or dismisses it; six hours after the last picture it
    /// breaks up by itself. Nobody is photographed before the whole group is there: someone who dropped out (drafted, downed, broke
    /// down, became unfit, was given a job by hand) is announced with the reason, comes back by themselves once they can again, and until then
    /// the line-up holds — the player takes the picture with whoever stands there ("take it now" on the spot), or cancels. After two hours one
    /// message names who is still missing; after six hours the line-up disbands without a photo. The photo is only taken while the player is
    /// looking at this map (the line-up holds until they do, within the same six hours). Threats, the spot's removal or a death call it off.
    /// Everything runs on the lord's own tick — nothing exists when no photo is gathering.
    /// Derives from LordJob_VoluntarilyJoinable because drafting only evicts a pawn from that kind of lord (Pawn_DraftController), and because
    /// the vanilla join node then brings a dropped member back: the join priority is positive for current members and for called members who
    /// can rejoin, zero for everyone else.</summary>
    public partial class LordJob_MFGroupPhoto : LordJob_VoluntarilyJoinable
    {
        /// <summary>Saved as int: keep the order (Deferred is no longer produced but stays for saves from the first build).</summary>
        public enum Outcome { None, Shot, Deferred, Cancelled, Danger, NoOne, Killed, SpotGone, NotViewed, Failed, TimedOut, Dismissed }

        public IntVec3 spot;
        private List<Pawn> members = new List<Pawn>();   // everyone called, formation order, index-aligned with cells; a drop-out stays here so they can come back
        private List<IntVec3> cells = new List<IntVec3>();
        private int startTick;
        private int readySince = -1;
        private int shotTick = -1;
        private int startFailures;
        private int state;        // 0 gathering, 1 capture in flight, 2 done, 3 taken: holding the line for another picture
        private int takes;        // pictures taken by this line-up
        private int holdSince = -1;
        private int outcome;
        private string photoId;
        private int shotCount;
        private string lostName;
        private bool warned;      // the two-hour "still missing" message went out
        private bool shootNow;    // the player asked for the picture with whoever stands in place
        // A reunion: the same people back in the same place, framed exactly like the photo they are re-making.
        private string reunionOf;
        private Vector3 reunionCenter;
        private float reunionSpan, reunionAspect;
        private string reunionTitle;
        // A birthday: the picture belongs to that person (Birthday.cs).
        private string birthdayOf;
        private int birthdayAge;
        private string birthdayTitle;
        private Pawn birthdayPawn;
        private Pawn birthdayGuardian;
        private List<Pawn> photoBabies = new List<Pawn>();
        private List<Pawn> photoBabyCarriers = new List<Pawn>();
        private bool fixedBirthdaySubject;
        private List<Pawn> medicalRestGuests = new List<Pawn>();
        private int captureReactionMode = -1;
        private List<string> reactionPawnIds = new List<string>();
        private List<int> reactionIcons = new List<int>();
        private List<string> facePawnIds = new List<string>();
        private List<int> faceChoices = new List<int>();
        internal Dictionary<string,int> FaceAssignments => PhotoFaces.Read(facePawnIds,faceChoices);
        private bool peopleFocus;
        private bool useSavedFrame = true;
        internal bool UseSavedFrame => useSavedFrame;
        internal int CaptureReactionMode => captureReactionMode;
        internal bool PeopleFocus => peopleFocus;
        internal Dictionary<string,int> ReactionAssignments => ReactionChoices.Read(reactionPawnIds,reactionIcons);
        internal bool MedicalRestAllowed(Pawn p) => medicalRestGuests.Contains(p);
        internal void SetPreparationOptions(IEnumerable<Pawn> explicitGuests, int reactionMode, bool focus, IDictionary<string,int> reactions=null, bool useSavedFrame=true, IDictionary<string,int> faces=null)
        {
            if(faces!=null)
            { facePawnIds=faces.Keys.Where(k=>!string.IsNullOrEmpty(k)).OrderBy(k=>k).ToList();faceChoices=facePawnIds.Select(id=>PhotoFaces.Choice(faces,id)).ToList(); }
            medicalRestGuests = explicitGuests.Where(p=>p!=null).Distinct().ToList();
            captureReactionMode = reactionMode < 0 ? -1 : Mathf.Clamp(reactionMode,0,2);
            peopleFocus = focus;
            this.useSavedFrame = useSavedFrame;
            if(reactions!=null)
            {
                reactionPawnIds=reactions.Where(p=>ReactionChoices.Valid(p.Value)&&!string.IsNullOrEmpty(p.Key)).Select(p=>p.Key).OrderBy(id=>id).ToList();
                reactionIcons=reactionPawnIds.Select(id=>reactions[id]).ToList();
            }
        }
        private string birthdayRetakeOf;
        private Rot4 formationDirection = Rot4.North;
        internal bool EditingBirthday;
        internal bool EditingPhotoSetup;
        private int lastLordTick = -1; // transient: editing windows are closed on load; saved start/hold ticks already exclude their elapsed time
        private Pawn petPawn, petOwner;
        internal bool IsPetPhoto => petPawn != null || petOwner != null;
        internal Pawn PetPawn => petPawn;
        internal Pawn PetOwner => petOwner;
        internal void MakePetPhoto(Pawn owner, Pawn animal) { petOwner = owner; petPawn = animal; }

        public void MakeBirthday(Pawn p, int age, bool fixedSubject = false, Rot4? direction = null, string retakeOf = null, Pawn guardian = null)
        {
            if (p == null) return;
            birthdayOf = p.GetUniqueLoadID();
            birthdayAge = age;
            birthdayTitle = Birthday.Title(p, age);
            birthdayPawn = p;
            birthdayGuardian = guardian;
            fixedBirthdaySubject = fixedSubject;
            formationDirection = direction ?? Rot4.North;
            birthdayRetakeOf = retakeOf;
        }

        internal Pawn BirthdayPawn => birthdayPawn;
        internal Pawn BirthdayGuardian => birthdayGuardian;
        internal int BirthdayAge => birthdayAge;
        internal Rot4 FormationDirection => formationDirection;
        internal bool FixedBirthdaySubject => fixedBirthdaySubject;
        internal bool IsBirthday => !birthdayOf.NullOrEmpty();
        internal Rot4 PoseFacing => formationDirection.Opposite;
        internal Map PhotoMap => lord?.Map;
        internal IEnumerable<Pawn> CarriedPhotoBabies
        {
            get
            {
                if(birthdayGuardian!=null && birthdayPawn!=null) yield return birthdayPawn;
                foreach(var baby in photoBabies) if(baby!=null && baby!=birthdayPawn) yield return baby;
            }
        }
        internal Pawn BabyForCarrier(Pawn carrier)
        {
            if(carrier!=null && carrier==birthdayGuardian) return birthdayPawn;
            int i=photoBabyCarriers.IndexOf(carrier);
            return i>=0 && i<photoBabies.Count?photoBabies[i]:null;
        }
        internal Pawn CarrierForBaby(Pawn baby)
        {
            if(baby!=null && baby==birthdayPawn) return birthdayGuardian;
            int i=photoBabies.IndexOf(baby);
            return i>=0 && i<photoBabyCarriers.Count?photoBabyCarriers[i]:null;
        }
        internal bool BabyReady(Pawn baby)
        {
            var carrier=CarrierForBaby(baby);
            return carrier!=null && BabyForCarrier(carrier)==baby && InPlaceNow(carrier);
        }
        internal void MakeBabyGuests(IDictionary<Pawn,Pawn> guardians)
        {
            photoBabies.Clear();photoBabyCarriers.Clear();
            if(guardians!=null) foreach(var pair in guardians) {photoBabies.Add(pair.Key);photoBabyCarriers.Add(pair.Value);}
        }
        internal bool ReplacePhotoRoster(List<Pawn> next,List<IntVec3> positions,Rot4 direction,IDictionary<Pawn,Pawn> babyGuardians=null,IntVec3? newSpot=null)
        {
            if(IsBirthday || IsPetPhoto || !reunionOf.NullOrEmpty() || Shooting || Finished || Takes>0 || next.Count!=positions.Count) return false;
            var nextBabies=babyGuardians ?? photoBabies.Where(b=>next.Contains(CarrierForBaby(b))).ToDictionary(b=>b,CarrierForBaby);
            if(nextBabies.Any(pair=>BirthdayCarry.Holder(pair.Key)!=null && BirthdayCarry.Holder(pair.Key)!=pair.Value)) return false;
            var leaving=lord.ownedPawns.Where(p=>!next.Contains(p)).Select(p=>new { Pawn=p,Cell=CellFor(p) }).ToList();
            MakeBabyGuests(nextBabies);
            foreach(var old in leaving)
            { members.Remove(old.Pawn);Drop(old.Pawn,PawnLostCondition.LeftVoluntarily,"MF_Why_Left".Translate(),old.Cell); }
            StopStudioLights();
            members=next;cells=positions;formationDirection=direction;spot=newSpot??spot;
            foreach(var p in next)
            {
                if(!lord.ownedPawns.Contains(p)) { PhotoGatheringAccess.Leave(p); lord.AddPawn(p); }
                if(!(p.jobs?.curDriver is JobDriver_MFCarryBaby)) p.jobs?.EndCurrentJob(JobCondition.InterruptForced,false);
            }
            droppedAt.Clear();dropReason.Clear();readySince=-1;warned=false;shootNow=false;startTick=Find.TickManager.TicksGame;
            lord.CurLordToil.UpdateAllDuties();GroupPhoto.ForgetEligible();return true;
        }

        internal bool ReplaceBirthdayRoster(IntVec3 place, List<Pawn> next, List<IntVec3> positions, Rot4 direction, Pawn guardian = null)
        {
            if (!IsBirthday || Shooting || Finished || next.Count != positions.Count) return false;
            if (BirthdayCarry.Holder(birthdayPawn) != null && guardian != birthdayGuardian) return false;
            foreach (var old in lord.ownedPawns.Where(p => !next.Contains(p)).ToList())
            {
                members.Remove(old);
                Drop(old, PawnLostCondition.LeftVoluntarily, "MF_Why_Left".Translate());
            }
            StopStudioLights();
            members = next; cells = positions; spot = place; formationDirection = direction;
            birthdayGuardian = guardian;
            fixedBirthdaySubject = Birthday.FixedSubject(birthdayPawn);
            foreach (var p in next)
            {
                if (!lord.ownedPawns.Contains(p)) { PhotoGatheringAccess.Leave(p); lord.AddPawn(p); }
                if (!(p == birthdayGuardian && p.jobs?.curDriver is JobDriver_MFCarryBaby))
                    p.jobs?.EndCurrentJob(JobCondition.InterruptForced, false);
            }
            droppedAt.Clear(); dropReason.Clear();
            readySince = -1; warned = false; shootNow = false;
            startTick = Find.TickManager.TicksGame;
            lord.CurLordToil.UpdateAllDuties();
            GroupPhoto.ForgetEligible();
            return true;
        }

        public string BirthdayOf => birthdayOf;

        /// <summary>No photo spot where they stand (a reunion, a birthday at a place picked on the map): the line-up's own buttons go on its members.</summary>
        public bool Spotless => Spot == null;
        // Not saved: a load starts them fresh (a drop-out simply may rejoin at once and the messages start over).
        private readonly Dictionary<Pawn, int> droppedAt = new Dictionary<Pawn, int>();
        private readonly Dictionary<Pawn, string> dropReason = new Dictionary<Pawn, string>();
        private readonly Dictionary<Pawn, int> lastMessage = new Dictionary<Pawn, int>();

        /// <summary>Turns this line-up into a re-shoot of an older photo: same centre, same span, same shape, and the album links the two.</summary>
        public void MakeReunion(PhotoRecord original)
        {
            if (original == null) return;
            reunionOf = original.id;
            reunionCenter = original.center;
            reunionSpan = original.spanCells;
            reunionAspect = original.Aspect;
            reunionTitle = original.title.NullOrEmpty() ? original.dateLabel : original.title;
            captureReactionMode = original.captureReactionMode;
            reactionPawnIds = new List<string>(original.reactionPawnIds ?? new List<string>());
            reactionIcons = new List<int>(original.reactionIcons ?? new List<int>());
            facePawnIds = new List<string>(original.facePawnIds ?? new List<string>());
            faceChoices = new List<int>(original.faceChoices ?? new List<int>());
        }

        public string ReunionOf => reunionOf;

        public bool Finished => state == 2;
        public bool Shooting => state == 1;
        public bool Holding => state == 3;
        public int Takes => takes;
        public Outcome Result => (Outcome)outcome;
        public string PhotoId => photoId;
        public int ShotCount => shotCount;
        public IReadOnlyList<Pawn> Members => members;
        public IReadOnlyList<IntVec3> Cells => cells;
        internal List<IntVec3> CellList => cells;   // for the preview: no copy
        public override bool AllowStartNewGatherings => false;

        public LordJob_MFGroupPhoto() { }

        public LordJob_MFGroupPhoto(IntVec3 spot, List<Pawn> members, List<IntVec3> cells, Rot4? direction=null)
        {
            this.spot = spot;
            this.members = members;
            this.cells = cells;
            formationDirection=direction??Rot4.North;
            startTick = Find.TickManager.TicksGame;
            lastLordTick = startTick;
        }

        public IntVec3 CellFor(Pawn p)
        {
            var carrier=CarrierForBaby(p);
            if(carrier!=null) return CellFor(carrier);
            int i = members.IndexOf(p);
            return i >= 0 && i < cells.Count ? cells[i] : spot;
        }

        /// <summary>Called members still alive: the photo waits for all of them unless the player takes it early.</summary>
        public int Roster
        {
            get
            {
                int n = 0;
                for (int i = 0; i < members.Count; i++) if (members[i] != null && !members[i].Dead) n++;
                return n;
            }
        }

        /// <summary>Everyone called is currently a member (nobody dropped out or everyone came back).</summary>
        public bool Complete => lord != null && lord.ownedPawns.Count >= Roster;

        public IEnumerable<Pawn> Dropped => droppedAt.Keys;
        public string DropReason(Pawn p) => dropReason.TryGetValue(p, out var r) ? r : "";

        private bool CanRejoin(Pawn p)
        {
            if (IsPetPhoto || !members.Contains(p) || p.Map != Map || !GroupPhoto.AttendanceProblem(p, this).NullOrEmpty()) return false;
            return !droppedAt.TryGetValue(p, out int t) || Find.TickManager.TicksGame - t >= GroupPhoto.RejoinCooldownTicks;
        }

        /// <summary>Current members stay (positive); a called member who dropped out is pulled back in by the vanilla join node once they can
        /// (positive); everyone else is never pulled in (zero).</summary>
        public override float VoluntaryJoinPriorityFor(Pawn p) => state != 2 && lord != null && (lord.ownedPawns.Contains(p) || CanRejoin(p)) ? 1f : 0f;

        /// <summary>The vanilla join node only runs when a pawn looks for a new job, so a drop-out who went back to a long one (studying an entity,
        /// r102) kept the whole line waiting. A drop-out who may come back is asked to reconsider: the join node sits above ordinary work in the
        /// think tree, so they return to the line; urgent needs and emergency work sit above it and carry on (the same job is a continuation, not
        /// restarted), and a job the player ordered is left alone.</summary>
        private void CallBackDropOuts()
        {
            foreach (var p in droppedAt.Keys.ToList())
            {
                if (lord.ownedPawns.Contains(p) || !p.Spawned || p.Drafted || !CanRejoin(p)) continue;
                if (p.CurJob != null && p.CurJob.playerForced) continue;
                p.jobs?.CheckForJobOverride();
            }
        }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var gather = new LordToil_MFGather(this);
            graph.AddToil(gather);
            var end = new LordToil_End();
            graph.AddToil(end);
            var done = new Transition(gather, end);
            done.AddTrigger(new Trigger_TickCondition(() => state == 2));
            graph.AddTransition(done);
            return graph;
        }

        private bool InPlaceNow(Pawn p) => p.Spawned && p.Map == Map && p.Position == CellFor(p) && !p.pather.Moving
            && (IsPetPhoto ? !p.Downed && p.Awake() : GroupPhoto.AttendanceProblem(p, this).NullOrEmpty())
            && (!(p.jobs?.curDriver is JobDriver_MFCarryBaby) || BabyForCarrier(p)!=null)
            && (BabyForCarrier(p)==null || p.carryTracker.CarriedThing==BabyForCarrier(p));

        public int InPlace
        {
            get
            {
                int n = 0;
                var owned = lord?.ownedPawns;
                if (owned == null) return 0;
                for (int i = 0; i < owned.Count; i++) if (InPlaceNow(owned[i])) n++;
                return n;
            }
        }

        public IEnumerable<Pawn> NotInPlace
        {
            get
            {
                var owned = lord?.ownedPawns;
                if (owned == null) yield break;
                for (int i = 0; i < owned.Count; i++) if (!InPlaceNow(owned[i])) yield return owned[i];
            }
        }

        public override void LordJobTick()
        {
            if (state == 2) return;
            StartStudioLights();
            int now = Find.TickManager.TicksGame;
            int elapsed = lastLordTick < 0 ? 1 : Math.Max(0, now - lastLordTick);
            lastLordTick = now;
            if (state == 1)
            {
                // A real capture may still be encoding while game time advances at high speed.
                // PhotoCapture has its own real-time bound; this watchdog handles requests that never started.
                if (now - shotTick > GroupPhoto.WatchdogTicks && !PhotoCapture.InProgress(photoId)) Finish(Outcome.Failed);
                return;
            }
            // A preparation window does not pause the game. Threats and a lost birthday subject still cancel the gathering,
            // while time spent choosing guests does not consume their line-up/holding deadline.
            if (Map == null) { Finish(Outcome.Cancelled); return; }
            if (Map.dangerWatcher.DangerRating == StoryDanger.High) { Finish(Outcome.Danger); return; }
            if (IsBirthday && (birthdayPawn == null || birthdayPawn.Dead || !birthdayPawn.IsFreeColonist
                || !BirthdayCarry.Available(birthdayPawn) || birthdayPawn.MapHeld != Map))
            {
                if (birthdayPawn?.Dead == true) { lostName = birthdayPawn.LabelShort; Finish(Outcome.Killed); }
                else Finish(Outcome.Cancelled);
                return;
            }
            foreach(var baby in photoBabies)
            {
                if(baby==null || baby.Dead || !baby.IsFreeColonist || !BirthdayCarry.Available(baby) || baby.MapHeld!=Map)
                { if(baby?.Dead==true) {lostName=baby.LabelShort;Finish(Outcome.Killed);} else Finish(Outcome.Cancelled);return; }
            }
            if (EditingBirthday || EditingPhotoSetup)
            {
                startTick += elapsed;
                if (holdSince >= 0) holdSince += elapsed;
                readySince = -1;
                return;
            }
            if (now % GroupPhoto.TickEvery != 0) return;
            if (IsPetPhoto && (!PetPhotos.CanContinue(this) || now - startTick >= PetPhotos.GiveUpTicks))
            { Finish(Outcome.Cancelled); return; }
            // A reunion stands where the old photo was taken: no spot there to lose.
            if (reunionOf.NullOrEmpty() && birthdayOf.NullOrEmpty() && Spot == null) { Finish(Outcome.SpotGone); return; }
            if (!IsPetPhoto && now % GroupPhoto.UnfitEvery == 0) DropUnfit();
            if (state == 2) return;
            if (now % GroupPhoto.CallBackEvery == 0 && droppedAt.Count > 0) CallBackDropOuts();
            int total = lord.ownedPawns.Count;
            if (total == 0 && Roster == 0 && !fixedBirthdaySubject) { Finish(Outcome.NoOne); return; }
            int inPlace = InPlace;
            if (state == 3)
            {
                // Taken: the line holds until the player takes another or dismisses it (six hours at most).
                if (now - holdSince >= GroupPhoto.GiveUpTicks) { Finish(Outcome.TimedOut); return; }
                if (shootNow) ShootRequested(now, inPlace);
                return;
            }
            if ((total > 0 || fixedBirthdaySubject) && inPlace == total) { if (readySince < 0) readySince = now; }
            else readySince = -1;
            bool held = readySince >= 0 && now - readySince >= GroupPhoto.HoldTicks;
            bool complete = Complete;
            if (now - startTick >= GroupPhoto.GiveUpTicks)
            {
                Finish(inPlace == 0 ? Outcome.NoOne : (complete && held && Map != Find.CurrentMap ? Outcome.NotViewed : Outcome.TimedOut));
                return;
            }
            if (shootNow)
            {
                ShootRequested(now, inPlace);
                return;
            }
            if (!warned && now - startTick >= GroupPhoto.WarnTicks && !(complete && held))
            {
                warned = true;
                var names = NotInPlace.Select(p => p.LabelShort).Concat(droppedAt.Keys.Where(p => !lord.ownedPawns.Contains(p)).Select(p => p.LabelShort)).Distinct().ToList();
                Say("MF_GroupWaiting".Translate(names.Count > 0 ? string.Join(", ", names) : "?"));
            }
            if (!held || !complete) return;
            // Only while the player is looking at this map: otherwise the line-up holds (within GiveUpTicks) instead of a later picture of an empty spot.
            if (Map != Find.CurrentMap) return;
            TryShoot(now);
        }

        private Building_PhotoSpot Spot => spot.GetFirstThing<Building_PhotoSpot>(Map);

        /// <summary>The player pressed "take it now" / "take another": with whoever stands in place, while this map is on screen. The request stays
        /// set until the capture actually starts (another photo may be rendering this frame).</summary>
        private void ShootRequested(int now, int inPlace)
        {
            if (Map != Find.CurrentMap) { shootNow = false; Say("MF_GroupNotViewing".Translate()); return; }
            if (IsPetPhoto && (!Complete || inPlace != 2)) { shootNow = false; Say("MF_Pet_WaitAll".Translate()); return; }
            if ((IsBirthday || CarriedPhotoBabies.Any()) && (!Complete || inPlace != lord.ownedPawns.Count)) { shootNow = false; Say("MF_BirthdayWaitAll".Translate()); return; }
            if (inPlace == 0 && !fixedBirthdaySubject) { shootNow = false; Say("MF_GroupNoOneInPlace".Translate()); return; }
            TryShoot(now);
        }

        private void Say(string text, MessageTypeDef type = null)
        {
            if (Current.ProgramState == ProgramState.Playing) Messages.Message(CaptionText.Clean(text), new TargetInfo(spot, Map), type ?? MessageTypeDefOf.NeutralEvent, false);
        }

        private void Announce(Pawn p, string text)
        {
            int now = Find.TickManager.TicksGame;
            if (lastMessage.TryGetValue(p, out int last) && now - last < GroupPhoto.MessageCooldownTicks) return;
            lastMessage[p] = now;
            if (Current.ProgramState == ProgramState.Playing) Messages.Message(CaptionText.Clean(text), p, MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Members who can no longer stand in a line leave with the same reason used by preparation. A dropped sleeper is never
        /// automatically woken; another explicit call or waking naturally makes them eligible again.</summary>
        private void DropUnfit()
        {
            for (int i = lord.ownedPawns.Count - 1; i >= 0; i--)
            {
                var p = lord.ownedPawns[i];
                string why = p.Map != Map ? "MF_Why_OtherMap".Translate().ToString() : GroupPhoto.AttendanceProblem(p, this);
                if (!why.NullOrEmpty()) Drop(p, PawnLostCondition.LeftVoluntarily, why);
            }
        }

        /// <summary>Removes a member (they stay called and may come back) and, if they were walking to or standing on their cell for the photo,
        /// ends that job right away.</summary>
        private void Drop(Pawn p, PawnLostCondition cond, string why, IntVec3? formerCell=null)
        {
            bool posing = p.Spawned && p.CurJob != null && (p.CurJobDef == MFDefs.Pose || (p.CurJobDef == JobDefOf.Goto && p.CurJob.targetA.Cell == (formerCell??CellFor(p))));
            dropReason[p] = why;
            lord.Notify_PawnLost(p, cond);
            if (posing && p.Spawned && !p.Drafted && p.jobs != null) p.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        private string ReasonFor(Pawn p, PawnLostCondition cond)
        {
            if (dropReason.TryGetValue(p, out var why) && !why.NullOrEmpty()) return why;
            switch (cond)
            {
                case PawnLostCondition.Drafted: return "MF_Why_Drafted".Translate();
                case PawnLostCondition.Incapped: return "MF_Why_Downed".Translate();
                case PawnLostCondition.InMentalState: return "MF_Why_Mental".Translate();
                case PawnLostCondition.ExitedMap: return "MF_Why_OtherMap".Translate();
                default: return p.Drafted ? "MF_Why_Drafted".Translate() : "MF_Why_Left".Translate();
            }
        }

        /// <summary>The player wants a picture now with whoever stands in place (before the first one: without waiting for the rest; after it:
        /// another one). Everyone stays in line either way.</summary>
        public void ShootNow()
        {
            if (EditingBirthday || EditingPhotoSetup || (state != 0 && state != 3)) return;
            shootNow = true;
        }

        private void TryShoot(int now)
        {
            var store = PhotoStore.Instance;
            if (store == null) { Finish(Outcome.Failed); return; }
            if (PhotoCapture.Busy) return;   // another photo renders this frame: try again next time
            if (IsPetPhoto && (!PetPhotos.CanContinue(this) || InPlace != 2)) { shootNow = false; return; }
            var people = new List<Pawn>();
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                var p = lord.ownedPawns[i];
                if (!InPlaceNow(p)) continue;
                people.Add(p);
                var c = p.Position;
                if (c.x < minX) minX = c.x; if (c.z < minZ) minZ = c.z; if (c.x > maxX) maxX = c.x; if (c.z > maxZ) maxZ = c.z;
            }
            if (IsBirthday)
            {
                birthdayPawn = birthdayPawn ?? members.FirstOrDefault(x => x?.GetUniqueLoadID() == birthdayOf);
                if (birthdayPawn == null || !PhotoCapture.VisualPosition(birthdayPawn, Map, out Vector3 birthdayPos) || birthdayPawn.Dead
                    || (birthdayGuardian != null && birthdayGuardian.carryTracker.CarriedThing != birthdayPawn)
                    || (!fixedBirthdaySubject && !people.Contains(birthdayPawn)))
                { shootNow = false; Say("MF_BirthdaySubjectMissing".Translate()); return; }
                if (!people.Contains(birthdayPawn)) people.Insert(0, birthdayPawn);
                else { people.Remove(birthdayPawn); people.Insert(0, birthdayPawn); }
                minX = Math.Min(minX, Mathf.FloorToInt(birthdayPos.x)); maxX = Math.Max(maxX, Mathf.CeilToInt(birthdayPos.x));
                minZ = Math.Min(minZ, Mathf.FloorToInt(birthdayPos.z)); maxZ = Math.Max(maxZ, Mathf.CeilToInt(birthdayPos.z));
            }
            foreach(var baby in photoBabies)
            {
                if(!BabyReady(baby) || !people.Contains(CarrierForBaby(baby)) || !PhotoCapture.VisualPosition(baby,Map,out var babyPos))
                { shootNow=false;return; }
                people.Add(baby);
                minX=Math.Min(minX,Mathf.FloorToInt(babyPos.x));maxX=Math.Max(maxX,Mathf.CeilToInt(babyPos.x));
                minZ=Math.Min(minZ,Mathf.FloorToInt(babyPos.z));maxZ=Math.Max(maxZ,Mathf.CeilToInt(babyPos.z));
            }
            if (people.Count == 0) return;
            var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy(peopleFocus ? 1 : GroupPhoto.Padding).ClipInsideMap(Map);
            Designator_TakePhoto.FitRect(rect, out Vector3 center, out float span, out float aspect);
            if (span < (peopleFocus ? 5f : GroupPhoto.LeastSpan)) span = peopleFocus ? 5f : GroupPhoto.LeastSpan;
            string kind = GroupPhoto.Kind;
            string title = "MF_GroupPhoto".Translate(people.Count);
            if (!reunionOf.NullOrEmpty())
            {
                center = reunionCenter; span = reunionSpan; aspect = reunionAspect;   // the old frame, whatever the line-up looks like
                kind = Kinds.Reshoot;
                title = "MF_ReunionTitle".Translate(reunionTitle);
            }
            else if (useSavedFrame && Spot != null && Spot.TryGetFrame(out Vector3 fc, out float fs, out float fa))
            {
                center = fc; span = fs; aspect = fa;   // the framing the player dragged for this spot
            }
            else if (Spot is Building_PhotoStudio studio)
                studio.FitPeopleAndBackdrop(CellRect.FromLimits(minX,minZ,maxX,maxZ),out center,out span,out aspect);
            string desc = null;
            if (IsPetPhoto)
            {
                kind = PetPhotos.Kind; title = "MF_PetPairTitle".Translate(petOwner.LabelShort, petPawn.LabelShort);
                desc = PetPhotos.Caption(PetPhotos.Kind, people, Map, petPawn, lord.GetUniqueLoadID());
                float large = Mathf.Sqrt(Mathf.Max(1f, petPawn.BodySize));
                span = Mathf.Max(span, 7f + large * 2f); aspect = PhotoCapture.DefaultAspect;
                center = (petOwner.DrawPos + petPawn.DrawPos) * .5f;
            }
            if (!birthdayOf.NullOrEmpty())
            {
                kind = Kinds.Birthday;
                title = birthdayTitle;
                var bp = people.FirstOrDefault(x => x.GetUniqueLoadID() == birthdayOf);
                if (bp == null) return;
            }
            // Each picture of the line-up is its own record (a retake does not replace the first one).
            string key = (!birthdayOf.NullOrEmpty() ? "birthday:" + birthdayOf + ":" + birthdayAge : (reunionOf.NullOrEmpty() ? "group:" : "reunion:") + lord.GetUniqueLoadID())
                + (!birthdayRetakeOf.NullOrEmpty() ? ":retake:" + lord.GetUniqueLoadID() : takes > 0 ? ":" + (takes + 1) : "");
            int before = state;
            state = 1;   // before the request: its callback may come at once
            shotTick = now;
            var rec = store.RequestNow(Map, center, kind, key, title, desc, people, span, aspect, Taken,
                studioLamps.Any(l => l.Spawned), prepared =>
            {
                prepared.captureReactionMode = captureReactionMode;
                if (!IsBirthday && !IsPetPhoto && photoBabies.Count == 0)
                    prepared.groupSession = "group:" + Map.uniqueID + ":" + lord.GetUniqueLoadID();
                prepared.reactionPawnIds = new List<string>(reactionPawnIds);
                prepared.reactionIcons = new List<int>(reactionIcons);
                prepared.facePawnIds = new List<string>(facePawnIds ?? new List<string>());
                prepared.faceChoices = new List<int>(faceChoices ?? new List<int>());
                if (!reunionOf.NullOrEmpty()) prepared.reshootOf = reunionOf;
                if (IsBirthday) { prepared.requiredPerson = birthdayOf; prepared.eventAge = birthdayAge; prepared.reshootOf = birthdayRetakeOf; }
                if (IsPetPhoto)
                {
                    prepared.requiredSubjects = new List<string> { petOwner.GetUniqueLoadID(), petPawn.GetUniqueLoadID() };
                    // This new pair's sentence was generated here, not supplied by the player or an event letter.
                    // Capture finishes on a later frame, after these required subjects and provenance are set.
                    prepared.generatedDesc = true;
                }
                if (photoBabies.Count>0) prepared.requiredSubjects=photoBabies.Concat(photoBabyCarriers).Select(p=>p.GetUniqueLoadID()).Distinct().ToList();
            });
            if (rec != null)
            {
                photoId = rec.id;
                shotCount = people.Count;
                shootNow = false;
                return;
            }
            if (state == 1) state = before;
            if (++startFailures >= GroupPhoto.StartRetries) Finish(Outcome.Failed);   // the capture would not start on the current map (should not happen)
        }

        /// <summary>After a successful image birthdays and pet portraits dismiss themselves. Ordinary groups hold for another picture;
        /// a failed image returns to gathering without completing the birthday letter.</summary>
        private void Taken()
        {
            if (state != 1) return;
            var rec = PhotoStore.Instance?.Get(photoId);
            if (rec?.HasFile != true)
            {
                state = 0; readySince = -1; shootNow = false; photoId = null;
                Say("MF_BirthdayRetryCapture".Translate());
                return;
            }
            state = 3;
            takes++;
            if (IsBirthday) { Birthday.CompleteLetter(birthdayPawn, birthdayAge); Finish(Outcome.Shot); return; }
            if (IsPetPhoto) { Finish(Outcome.Shot); return; }
            if (photoBabies.Count>0) { Finish(Outcome.Shot); return; }
            holdSince = Find.TickManager.TicksGame;
            readySince = -1;
            if (takes == 1) Say("MF_GroupHolding".Translate());
        }

        private void Finish(Outcome o)
        {
            if (state == 2) return;
            state = 2;
            outcome = (int)o;
            StopStudioLights();
            PhotoStore.Instance?.CancelPending(photoId);
        }

        /// <summary>Cancel before the first picture; dismiss after it.</summary>
        public void Cancel() => Finish(takes > 0 ? Outcome.Dismissed : Outcome.Cancelled);

        /// <summary>Leaves for good (the pawn's own gizmo): no longer called, so no coming back.</summary>
        public void Leave(Pawn p)
        {
            if (p == null) return;
            int i = members.IndexOf(p);
            if (i >= 0)
            {
                members.RemoveAt(i);
                if (i < cells.Count) cells.RemoveAt(i);
            }
            droppedAt.Remove(p);
            if (lord != null && lord.ownedPawns.Contains(p)) Drop(p, PawnLostCondition.LeftVoluntarily, "MF_Why_Left".Translate());
        }

        public override void Notify_InMentalState(Pawn pawn, MentalStateDef stateDef)
        {
            if (lord != null && lord.ownedPawns.Contains(pawn)) Drop(pawn, PawnLostCondition.InMentalState, "MF_Why_Mental".Translate());
        }

        /// <summary>Someone left the line (our own drops and vanilla's: drafted, downed, killed): a death calls the photo off; anyone else is
        /// announced with the reason and remains called, so they come back once they can.</summary>
        public override void Notify_PawnLost(Pawn p, PawnLostCondition condition)
        {
            if (state == 2 || p == null) return;
            if (condition == PawnLostCondition.Killed)
            {
                lostName = p.LabelShort;
                Finish(Outcome.Killed);
                return;
            }
            string why = ReasonFor(p, condition);
            dropReason[p] = why;
            droppedAt[p] = Find.TickManager.TicksGame;
            readySince = -1;
            if (members.Contains(p)) Announce(p, "MF_GroupDropped".Translate(p.LabelShort, why));
        }

        /// <summary>A drop-out came back (the vanilla join node added them): their cell is theirs again through UpdateAllDuties.</summary>
        public override void Notify_PawnAdded(Pawn p)
        {
            if (p == null || !droppedAt.Remove(p)) return;
            dropReason.Remove(p);
            readySince = -1;
            Announce(p, "MF_GroupRejoined".Translate(p.LabelShort));
        }

        public override string GetReport(Pawn pawn) => "MF_GroupReport".Translate();

        public override IEnumerable<Gizmo> GetPawnGizmos(Pawn p)
        {
            if (state == 2 || p == null || !p.IsColonistPlayerControlled) yield break;
            if (IsBirthday && !Shooting) yield return new Command_Action
            {
                defaultLabel = "MF_BirthdayEditGuests".Translate(), defaultDesc = "MF_BirthdaySetupHelp".Translate(),
                icon = MFIcons.Camera, action = () => Birthday.OpenSetup(birthdayPawn, birthdayAge, this)
            };
            if (Spotless || IsBirthday)
            {
                // The spot's buttons, on the members: nowhere else to press "take it now" or "dismiss" for a line-up without a spot.
                bool taken = takes > 0;
                int inPlace = InPlace;
                var shoot = new Command_Action
                {
                    defaultLabel = (IsBirthday ? "MF_BirthdayShoot" : taken ? "MF_GroupRetake" : "MF_GroupShootNow").Translate(inPlace + (fixedBirthdaySubject ? 1 : 0) + photoBabies.Count(BabyReady)),
                    defaultDesc = (IsBirthday ? "MF_BirthdayShootDesc" : taken ? "MF_GroupRetakeDesc" : "MF_GroupShootNowDesc").Translate(),
                    icon = MFIcons.Camera,
                    action = ShootNow
                };
                if (inPlace == 0 && !fixedBirthdaySubject) shoot.Disable("MF_GroupNoOneInPlace".Translate());
                else if ((IsBirthday || CarriedPhotoBabies.Any()) && (!Complete || inPlace != lord.ownedPawns.Count)) shoot.Disable("MF_BirthdayWaitAll".Translate());
                else if (Shooting) shoot.Disable("MF_GroupBusy".Translate());
                yield return shoot;
                yield return new Command_Action
                {
                    defaultLabel = (taken ? "MF_GroupDismiss" : "MF_GroupCancel").Translate(),
                    defaultDesc = (taken ? "MF_GroupDismissDesc" : "MF_GroupCancelDesc").Translate(),
                    icon = Building_PhotoSpot.CancelIcon,
                    action = Cancel
                };
            }
            yield return new Command_Action
            {
                defaultLabel = "MF_GroupLeave".Translate(),
                defaultDesc = "MF_GroupLeaveDesc".Translate(),
                icon = MFIcons.Camera,
                action = () => Leave(p)
            };
        }

        /// <summary>Runs when the lord goes (photo taken, called off, or the last member left). One message per ending; a taken photo already
        /// announced itself through the store, and a capture that produced nothing announced its failure there too.</summary>
        public override bool EndPawnJobOnCleanup(Pawn p) => !(p.jobs?.curDriver is JobDriver_MFCarryBaby
            && p.carryTracker.CarriedThing is Pawn);

        public override void Cleanup()
        {
            StopStudioLights();
            if (state != 2)
            {
                state = 2;
                if (outcome == (int)Outcome.None) outcome = (int)Outcome.NoOne;
            }
            PhotoStore.Instance?.CancelPending(photoId);
            GroupPhoto.ForgetEligible();
            string msg = null;
            MessageTypeDef type = MessageTypeDefOf.NeutralEvent;
            var result = Result;
            // After a picture nothing is "called off": the line simply breaks up.
            if (takes > 0 && (result == Outcome.Cancelled || result == Outcome.NoOne)) result = Outcome.Dismissed;
            if (takes > 0 && result == Outcome.Danger) { msg = "MF_GroupHeldEnd_Danger".Translate(); type = MessageTypeDefOf.NegativeEvent; }
            else if (takes > 0 && result == Outcome.TimedOut) msg = "MF_GroupHeldEnd_TimedOut".Translate();
            else switch (result)
            {
                case Outcome.Dismissed: msg = "MF_GroupDismissed".Translate(); break;
                case Outcome.Cancelled: msg = "MF_GroupCancelled".Translate(); break;
                case Outcome.Danger: msg = "MF_GroupCalledOff_Danger".Translate(); type = MessageTypeDefOf.NegativeEvent; break;
                case Outcome.NoOne: msg = "MF_GroupCalledOff_NoOne".Translate(); type = MessageTypeDefOf.NegativeEvent; break;
                case Outcome.Killed: msg = "MF_GroupCalledOff_Killed".Translate(lostName ?? "?"); type = MessageTypeDefOf.NegativeEvent; break;
                case Outcome.SpotGone: msg = "MF_GroupCalledOff_SpotGone".Translate(); type = MessageTypeDefOf.NegativeEvent; break;
                case Outcome.NotViewed: msg = "MF_GroupNotViewed".Translate(); type = MessageTypeDefOf.NegativeEvent; break;
                case Outcome.TimedOut: msg = "MF_GroupTimedOut".Translate(); type = MessageTypeDefOf.NegativeEvent; break;
                case Outcome.Failed: msg = "MF_GroupFailed".Translate(); type = MessageTypeDefOf.NegativeEvent; break;
            }
            if (msg != null && Current.ProgramState == ProgramState.Playing) Messages.Message(msg, new TargetInfo(spot, Map), type, false);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref spot, "spot");
            Scribe_Collections.Look(ref members, "members", LookMode.Reference);
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            Scribe_Values.Look(ref startTick, "startTick", 0);
            Scribe_Values.Look(ref readySince, "readySince", -1);
            Scribe_Values.Look(ref shotTick, "shotTick", -1);
            Scribe_Values.Look(ref startFailures, "startFailures", 0);
            Scribe_Values.Look(ref state, "state", 0);
            Scribe_Values.Look(ref takes, "takes", 0);
            Scribe_Values.Look(ref holdSince, "holdSince", -1);
            Scribe_Values.Look(ref outcome, "outcome", 0);
            Scribe_Values.Look(ref photoId, "photoId");
            Scribe_Values.Look(ref shotCount, "shotCount", 0);
            Scribe_Values.Look(ref lostName, "lostName");
            Scribe_Values.Look(ref warned, "warned", false);
            Scribe_Values.Look(ref shootNow, "shootNow", false);
            Scribe_Values.Look(ref reunionOf, "reunionOf");
            Scribe_Values.Look(ref reunionCenter, "reunionCenter");
            Scribe_Values.Look(ref reunionSpan, "reunionSpan", 0f);
            Scribe_Values.Look(ref reunionAspect, "reunionAspect", 0f);
            Scribe_Values.Look(ref reunionTitle, "reunionTitle");
            Scribe_Values.Look(ref birthdayOf, "birthdayOf");
            Scribe_Values.Look(ref birthdayAge, "birthdayAge", 0);
            Scribe_Values.Look(ref birthdayTitle, "birthdayTitle");
            Scribe_References.Look(ref birthdayPawn, "birthdayPawn");
            Scribe_References.Look(ref birthdayGuardian, "birthdayGuardian");
            Scribe_Collections.Look(ref photoBabies, "photoBabies", LookMode.Reference);
            Scribe_Collections.Look(ref photoBabyCarriers, "photoBabyCarriers", LookMode.Reference);
            Scribe_Values.Look(ref fixedBirthdaySubject, "fixedBirthdaySubject", false);
            Scribe_Collections.Look(ref medicalRestGuests, "medicalRestGuests", LookMode.Reference);
            Scribe_Values.Look(ref captureReactionMode, "captureReactionMode", -1);
            Scribe_Collections.Look(ref reactionPawnIds,"reactionPawnIds",LookMode.Value);
            Scribe_Collections.Look(ref reactionIcons,"reactionIcons",LookMode.Value);
            Scribe_Collections.Look(ref facePawnIds,"facePawnIds",LookMode.Value);
            Scribe_Collections.Look(ref faceChoices,"faceChoices",LookMode.Value);
            Scribe_Values.Look(ref peopleFocus, "peopleFocus", false);
            Scribe_Values.Look(ref useSavedFrame, "useSavedFrame", true);
            Scribe_Values.Look(ref birthdayRetakeOf, "birthdayRetakeOf");
            Scribe_Values.Look(ref formationDirection, "formationDirection", Rot4.North);
            Scribe_References.Look(ref petPawn, "petPawn");
            Scribe_References.Look(ref petOwner, "petOwner");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (members == null) members = new List<Pawn>();
                if (medicalRestGuests == null) medicalRestGuests = new List<Pawn>();
                var reactions=ReactionChoices.Read(reactionPawnIds,reactionIcons);
                reactionPawnIds=reactions.Keys.OrderBy(id=>id).ToList();reactionIcons=reactionPawnIds.Select(id=>reactions[id]).ToList();
                medicalRestGuests.RemoveAll(p=>p==null);
                if (cells == null) cells = new List<IntVec3>();
                if(photoBabies==null) photoBabies=new List<Pawn>();
                if(photoBabyCarriers==null) photoBabyCarriers=new List<Pawn>();
                if(photoBabies.Count!=photoBabyCarriers.Count || photoBabies.Any(p=>p==null) || photoBabyCarriers.Any(p=>p==null))
                {
                    // A missing saved child/carrier cancels the photo instead of silently leaving that child out.
                    state=2;outcome=(int)Outcome.Cancelled;
                    for(int i=Math.Min(photoBabies.Count,photoBabyCarriers.Count)-1;i>=0;i--)
                        if(photoBabies[i]==null || photoBabyCarriers[i]==null) {photoBabies.RemoveAt(i);photoBabyCarriers.RemoveAt(i);}
                    int keep=Math.Min(photoBabies.Count,photoBabyCarriers.Count);
                    if(photoBabies.Count>keep) photoBabies.RemoveRange(keep,photoBabies.Count-keep);
                    if(photoBabyCarriers.Count>keep) photoBabyCarriers.RemoveRange(keep,photoBabyCarriers.Count-keep);
                }
                // A member whose reference did not resolve leaves together with their cell, so the index alignment survives.
                for (int i = Math.Min(members.Count, cells.Count) - 1; i >= 0; i--)
                    if (members[i] == null) { members.RemoveAt(i); cells.RemoveAt(i); }
                members.RemoveAll(p => p == null);
                if (cells.Count > members.Count) cells.RemoveRange(members.Count, cells.Count - members.Count);
                // A capture that was in flight at save time has no callback any more; the record itself was saved (with or without its file).
                // The line holds as if it had come back (a v2.4.0 save lands here too: its line-up now waits to be dismissed).
                if (IsBirthday && birthdayPawn == null) birthdayPawn = members.FirstOrDefault(p => p.GetUniqueLoadID() == birthdayOf);
                if (state == 1)
                {
                    state = 0;
                    readySince = -1;
                    shootNow = false;
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        var saved = PhotoStore.Instance?.Get(photoId);
                        if (saved?.HasFile == true) { state = 1; Taken(); }
                        else if (saved != null) PhotoStore.Instance.Remove(saved);
                    });
                }
            }
        }
    }

    /// <summary>Everyone gets the pose duty aimed at their own cell. Long needs are not served while lining up. The join hook is the duty's own
    /// (MediumPriority), so the vanilla join node considers this lord for a called member who dropped out and can come back.</summary>
    public class LordToil_MFGather : LordToil
    {
        private readonly LordJob_MFGroupPhoto job;

        public LordToil_MFGather(LordJob_MFGroupPhoto job) { this.job = job; }

        public override bool AllowSatisfyLongNeeds => false;

        public override ThinkTreeDutyHook VoluntaryJoinDutyHookFor(Pawn p) => MFDefs.PoseDuty?.hook ?? ThinkTreeDutyHook.MediumPriority;

        public override void UpdateAllDuties()
        {
            var duty = MFDefs.PoseDuty;
            if (duty == null) return;
            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                var p = lord.ownedPawns[i];
                if (p.mindState != null) p.mindState.duty = new PawnDuty(duty, job.CellFor(p)) { locomotion = LocomotionUrgency.Jog };
            }
        }
    }

    /// <summary>Duty think node after arrival: stand on the cell and face south.</summary>
    public class JobGiver_MFPose : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            var duty = pawn.mindState?.duty;
            if (duty == null || pawn.Downed || MFDefs.Pose == null) return null;
            return JobMaker.MakeJob(MFDefs.Pose, duty.focus.Cell);
        }
    }

    /// <summary>Stand still facing the camera (south). Ends after a few seconds and is handed out again while the duty lasts, so a pawn who
    /// wandered off (pushed, fled) walks back through the duty's goto node.</summary>
    public class JobDriver_MFPose : JobDriver
    {
        public const int PoseTicks = 250;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => pawn.Position != job.targetA.Cell);
            var pose = ToilMaker.MakeToil("MFPose");
            pose.initAction = () =>
            {
                pawn.pather.StopDead();
                pawn.Rotation = (pawn.GetLord()?.LordJob as LordJob_MFGroupPhoto)?.PoseFacing ?? Rot4.South;
            };
            pose.tickIntervalAction = delta => pawn.Rotation = (pawn.GetLord()?.LordJob as LordJob_MFGroupPhoto)?.PoseFacing ?? Rot4.South;
            pose.handlingFacing = true;
            pose.socialMode = RandomSocialMode.Off;
            pose.defaultCompleteMode = ToilCompleteMode.Delay;
            pose.defaultDuration = PoseTicks;
            yield return pose;
        }
    }
}
