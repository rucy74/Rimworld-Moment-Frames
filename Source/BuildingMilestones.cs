using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public sealed class MFBuildingFootprint : IExposable
    {
        public string key = "";
        public List<IntVec3> cells = new List<IntVec3>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key", ""); Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { if (cells == null) cells = new List<IntVec3>(); if (key == null) key = ""; }
        }
    }

    public sealed class MFBuildingCandidate : IExposable
    {
        public int minX, minZ, maxX, maxZ, lastConstruction, stableSince = -1, retryAfter;
        public List<IntVec3> structures = new List<IntVec3>();
        public string fingerprint = "", photoId = "", eventKey = "";
        public List<MFBuildingFootprint> photographed = new List<MFBuildingFootprint>();
        public CellRect Bounds => CellRect.FromLimits(minX, minZ, maxX, maxZ);
        public void ExposeData()
        {
            Scribe_Values.Look(ref minX, "minX"); Scribe_Values.Look(ref minZ, "minZ");
            Scribe_Values.Look(ref maxX, "maxX"); Scribe_Values.Look(ref maxZ, "maxZ");
            Scribe_Values.Look(ref lastConstruction, "lastConstruction"); Scribe_Values.Look(ref stableSince, "stableSince", -1);
            Scribe_Values.Look(ref retryAfter, "retryAfter");
            Scribe_Values.Look(ref fingerprint, "fingerprint", ""); Scribe_Values.Look(ref photoId, "photoId", "");
            Scribe_Values.Look(ref eventKey, "eventKey", "");
            Scribe_Collections.Look(ref structures, "structures", LookMode.Value);
            Scribe_Collections.Look(ref photographed, "photographed", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (structures == null) structures = new List<IntVec3>();
                if (photographed == null) photographed = new List<MFBuildingFootprint>();
                if (photoId == null) photoId = ""; if (fingerprint == null) fingerprint = "";
                if (eventKey.NullOrEmpty()) eventKey = "building:" + Guid.NewGuid().ToString("N");
            }
        }
    }

    /// <summary>Map-local construction history. Existing closed rooms are baselined once, using cells rather than transient Room.ID.
    /// Finished frames mark local candidates; a quiet, roofed, usable new space is photographed once its map is visible.</summary>
    public sealed class MapComponent_MFBuildingMilestones : MapComponent
    {
        internal const string Kind = "building";
        private bool initialized;
        private List<MFBuildingFootprint> known = new List<MFBuildingFootprint>();
        private List<MFBuildingCandidate> candidates = new List<MFBuildingCandidate>();
        private readonly HashSet<IntVec3> knownCells = new HashSet<IntVec3>();
        private readonly HashSet<string> knownKeys = new HashSet<string>();
        private int lastPoll = -1;
        private sealed class RoomSnapshot
        {
            internal Room Room;
            internal MFBuildingFootprint Footprint;
            internal bool Complete;
            internal bool UsableEvidence;
            internal int Unfinished;
            internal string State, Facility;
        }
        public MapComponent_MFBuildingMilestones(Map map) : base(map) { }
        public override void ExposeData()
        {
            Scribe_Values.Look(ref initialized, "mfBuildingsInitialized", false);
            Scribe_Collections.Look(ref known, "mfBuildingFootprints", LookMode.Deep);
            Scribe_Collections.Look(ref candidates, "mfBuildingCandidates", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (known == null) known = new List<MFBuildingFootprint>();
                if (candidates == null) candidates = new List<MFBuildingCandidate>();
                known.RemoveAll(k => k == null); candidates.RemoveAll(c => c == null);
                RebuildIndex(); lastPoll = -1;
            }
        }
        private void RebuildIndex()
        {
            knownCells.Clear(); knownKeys.Clear();
            foreach (var footprint in known)
            {
                knownKeys.Add(footprint.key);
                foreach (var cell in footprint.cells) knownCells.Add(cell);
            }
        }
        private static MFBuildingFootprint Footprint(Room room)
        {
            var cells = room.Cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (var c in cells) { hash = (hash ^ (uint)c.x) * 1099511628211UL; hash = (hash ^ (uint)c.z) * 1099511628211UL; } }
            return new MFBuildingFootprint { key = cells.Count + ":" + hash.ToString("X16"), cells = cells };
        }
        private static bool TrackableRoom(Room room) => room != null && !room.Dereferenced && room.ProperRoom
            && !room.IsDoorway && !room.TouchesMapEdge
            && room.CellCount >= BuildingMilestonePolicy.MinRoomCells && room.CellCount <= BuildingMilestonePolicy.MaxRoomCells;
        private static bool IndoorCandidate(Room room) => TrackableRoom(room) && !room.PsychologicallyOutdoors;
        internal void EnsureBaseline()
        {
            if (initialized) return;
            // Never infer that buildings already in a loaded colony were just completed by this mod's installation.
            // An empty room with active work can still be a first-load building project. Existing usable rooms remain known
            // during a furniture replacement. A finished-floor empty new room cannot be distinguished from an old empty one here.
            var baseline = new List<MFBuildingFootprint>();
            var pending = new List<MFBuildingCandidate>();
            foreach (var room in map.regionGrid.AllRooms.Where(IndoorCandidate).ToList())
            {
                var footprint = Footprint(room); var snapshot = Snapshot(room, footprint);
                if (BuildingMilestonePolicy.ExistingAtBaseline(snapshot.Unfinished, snapshot.UsableEvidence)) baseline.Add(footprint);
                else
                {
                    var bounds = room.ExtentsClose;
                    pending.Add(new MFBuildingCandidate { minX = bounds.minX, minZ = bounds.minZ, maxX = bounds.maxX, maxZ = bounds.maxZ,
                        lastConstruction = Find.TickManager.TicksGame, eventKey = "building:" + map.uniqueID + ":" + Guid.NewGuid().ToString("N") });
                }
            }
            // Only commit a complete baseline; a modded room failure must not leave half the old colony classified as new.
            known.AddRange(baseline); candidates.AddRange(pending); RebuildIndex(); initialized = true;
        }
        internal void ConstructionFinished(IntVec3 position, bool structural)
        {
            if (!Enabled) return;
            EnsureBaseline();
            int now = Find.TickManager.TicksGame;
            var close = candidates.Where(c => c.photoId.NullOrEmpty() && c.Bounds.ExpandedBy(5).Contains(position)
                && Math.Max(c.maxX, position.x) - Math.Min(c.minX, position.x) <= 90
                && Math.Max(c.maxZ, position.z) - Math.Min(c.minZ, position.z) <= 90).ToList();
            var candidate = close.FirstOrDefault();
            if (candidate == null)
            {
                candidate = new MFBuildingCandidate { minX = position.x, maxX = position.x, minZ = position.z, maxZ = position.z,
                    eventKey = "building:" + map.uniqueID + ":" + Guid.NewGuid().ToString("N") };
                candidates.Add(candidate);
            }
            foreach (var other in close.Skip(1))
            {
                candidate.minX = Math.Min(candidate.minX, other.minX); candidate.maxX = Math.Max(candidate.maxX, other.maxX);
                candidate.minZ = Math.Min(candidate.minZ, other.minZ); candidate.maxZ = Math.Max(candidate.maxZ, other.maxZ);
                foreach (var cell in other.structures) if (!candidate.structures.Contains(cell)) candidate.structures.Add(cell);
                candidates.Remove(other);
            }
            candidate.minX = Math.Min(candidate.minX, position.x); candidate.maxX = Math.Max(candidate.maxX, position.x);
            candidate.minZ = Math.Min(candidate.minZ, position.z); candidate.maxZ = Math.Max(candidate.maxZ, position.z);
            if (structural && !candidate.structures.Contains(position)) candidate.structures.Add(position);
            candidate.lastConstruction = now; candidate.stableSince = -1; candidate.fingerprint = "";
        }
        public override void MapComponentTick()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null || !map.IsPlayerHome) return;
            // Do not scan room cells, floors or furniture while automatic building photographs are disabled.
            if (!Enabled) return;
            int tick = Find.TickManager.TicksGame;
            if (lastPoll >= 0 && tick - lastPoll < 250) return;
            lastPoll = tick;
            try { Poll(tick); } catch (Exception e) { Warn(e); }
        }
        private void Poll(int now)
        {
            EnsureBaseline();
            var store = PhotoStore.Instance;
            if (store == null || candidates.Count == 0) return;
            var rooms = map.regionGrid.AllRooms.Where(TrackableRoom).ToList();
            var footprints = new Dictionary<Room, MFBuildingFootprint>();
            var roomSnapshots = new Dictionary<Room, RoomSnapshot>();
            var unfinishedThings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)).ToList();
            foreach (var candidate in candidates.ToList())
            {
                if (!candidate.photoId.NullOrEmpty())
                {
                    var captured = store.GetAny(candidate.photoId);
                    if (captured?.HasFile == true)
                    {
                        foreach (var room in candidate.photographed)
                            if (knownKeys.Add(room.key)) { known.Add(room); foreach (var cell in room.cells) knownCells.Add(cell); }
                        candidates.Remove(candidate); continue;
                    }
                    if (captured != null) continue; // queued or in flight; loaded pending requests are owned by PhotoStore
                    candidate.photoId = ""; candidate.retryAfter = now + BuildingMilestonePolicy.QuietTicks;
                }
                var snapshots = new List<RoomSnapshot>();
                foreach (var room in rooms)
                {
                    var a = room.ExtentsClose; var b = candidate.Bounds.ExpandedBy(2);
                    if (a.maxX < b.minX || b.maxX < a.minX || a.maxZ < b.minZ || b.maxZ < a.minZ) continue;
                    if (!footprints.TryGetValue(room, out var footprint)) footprints[room] = footprint = Footprint(room);
                    int newCells = footprint.cells.Count(c => !knownCells.Contains(c));
                    if (!BuildingMilestonePolicy.NewSpace(footprint.cells.Count, newCells, knownKeys.Contains(footprint.key), candidate.structures.Count)) continue;
                    if (!roomSnapshots.TryGetValue(room, out var roomSnapshot))
                        roomSnapshots[room] = roomSnapshot = Snapshot(room, footprint, unfinishedThings);
                    if (!roomSnapshot.Facility.NullOrEmpty()) snapshots.Add(roomSnapshot);
                }
                // An old room's replacement furniture is tracked briefly, but cannot create a new-building photograph.
                if (snapshots.Count == 0)
                {
                    if (now - candidate.lastConstruction > 60000) candidates.Remove(candidate);
                    continue;
                }
                string fingerprint = string.Join("|", snapshots.OrderBy(s => s.Footprint.key).Select(s => s.State));
                if (candidate.fingerprint != fingerprint)
                { candidate.fingerprint = fingerprint; candidate.stableSince = now; }
                if (snapshots.Any(s => !s.Complete) || !BuildingMilestonePolicy.Settled(now, candidate.lastConstruction, candidate.stableSince)
                    || now < candidate.retryAfter || map != Find.CurrentMap || PhotoCapture.Busy
                    || MomentFramesMod.Settings?.buildingPhotos != true || !SettingsCapturePolicy.Automatic(MomentFramesMod.Settings)) continue;
                Capture(store, candidate, snapshots, now);
            }
        }
        private static bool Enabled => MomentFramesMod.Settings?.buildingPhotos == true && SettingsCapturePolicy.Automatic(MomentFramesMod.Settings);
        private RoomSnapshot Snapshot(Room room, MFBuildingFootprint footprint, List<Thing> unfinishedThings = null)
        {
            var cells = new HashSet<IntVec3>(footprint.cells);
            var bounds = room.ExtentsClose.ExpandedBy(1);
            int unfinished = unfinishedThings != null ? unfinishedThings.Count(t => bounds.Contains(t.Position))
                : map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(t => bounds.Contains(t.Position))
                    + map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => bounds.Contains(t.Position));
            int floor = footprint.cells.Count(c => { var terrain = map.terrainGrid.TerrainAt(c); return terrain.IsFloor && !terrain.natural; });
            int storage = footprint.cells.Count(c => c.GetZone(map) is Zone_Stockpile);
            var furniture = room.ContainedAndAdjacentThings.Where(t => t.Faction == Faction.OfPlayer && t is Building
                && cells.Contains(t.Position) && !(t is Frame) && !(t is Blueprint)).ToList();
            bool usable = furniture.Any(t => t is Building_Bed || t is Building_WorkTable || t is Building_ResearchBench || t is Building_Storage);
            string role = room.Role?.defName ?? "None";
            string facility = BuildingMilestonePolicy.Facility(role, furniture.Any(t => t is Building_ResearchBench),
                furniture.Any(t => t is Building_Bookcase), furniture.Any(t => t is Building_WorkTable),
                furniture.OfType<Building_Bed>().Any(t => t.Medical),
                furniture.Any(t => !t.def.IsDoor && t.def.Fillage != FillCategory.Full && t.def.defName != "RitualSpot"));
            bool knownRole = !facility.NullOrEmpty();
            bool usableEvidence = knownRole || usable || storage > 0 || floor >= footprint.cells.Count * .8f;
            bool complete = BuildingMilestonePolicy.Complete(room.ProperRoom, room.OpenRoofCount, unfinished, knownRole, knownRole, floor, storage, footprint.cells.Count);
            string furnishings = string.Join(",", furniture.OrderBy(t => t.thingIDNumber).Select(t => t.def.defName + ":" + t.Position.x + ":" + t.Position.z));
            return new RoomSnapshot { Room = room, Footprint = footprint, Complete = complete, Unfinished = unfinished, UsableEvidence = usableEvidence, Facility = facility,
                State = footprint.key + ":" + room.OpenRoofCount + ":" + unfinished + ":" + role + ":" + floor + ":" + storage + ":" + furnishings };
        }
        private void Capture(PhotoStore store, MFBuildingCandidate candidate, List<RoomSnapshot> snapshots, int now)
        {
            var allCells = snapshots.SelectMany(s => s.Footprint.cells).ToList();
            int minX = allCells.Min(c => c.x), maxX = allCells.Max(c => c.x), minZ = allCells.Min(c => c.z), maxZ = allCells.Max(c => c.z);
            var area = CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy(2).ClipInsideMap(map);
            Vector3 center = new Vector3(area.minX + area.Width / 2f, 0f, area.minZ + area.Height / 2f);
            float height = area.Height, width = area.Width;
            float aspect = PhotoCapture.ClampAspect(Mathf.Max(PhotoCapture.DefaultAspect, width / Mathf.Max(1f, height)));
            float span = Mathf.Max(height, width / aspect);
            bool ordinary = BuildingMilestonePolicy.FitsCapture(span, PhotoCapture.MaxSpan);
            Vector3 fittedCenter = center; float fittedSpan = span, fittedAspect = aspect;
            PhotoCapture.KeepInside(map, ref fittedCenter, ref fittedSpan, ref fittedAspect);
            // Narrow maps can reduce the ordinary span too. Use the area path whenever any requested room edge would be lost.
            ordinary &= fittedCenter.x - fittedSpan * fittedAspect / 2f <= area.minX
                && fittedCenter.z - fittedSpan / 2f <= area.minZ
                && fittedCenter.x + fittedSpan * fittedAspect / 2f >= area.minX + area.Width
                && fittedCenter.z + fittedSpan / 2f >= area.minZ + area.Height;
            string role = snapshots.Count == 1 ? (snapshots[0].Facility == "Library" ? "MF_Records_Library".Translate().ToString()
                : DefDatabase<RoomRoleDef>.GetNamedSilentFail(snapshots[0].Facility)?.LabelCap.ToString() ?? snapshots[0].Room.GetRoomRoleLabel()) : "";
            string title = role.NullOrEmpty() ? "MF_BuildingNewSpaceTitle".Translate().ToString() : "MF_BuildingRoomTitle".Translate(role).ToString();
            // Describes only observed completion evidence. It does not claim first-of-kind, expansion or damage/reconstruction.
            string caption = "MF_BuildingCaption".Translate(allCells.Count).ToString();
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => allCells.Contains(p.Position)).ToList();
            candidate.photographed = snapshots.Select(s => s.Footprint).ToList();
            var record = ordinary ? store.RequestAutomatic(map, center, Kind, candidate.eventKey, title, caption, people, span, aspect)
                : store.RequestAutomaticArea(map, area, Kind, candidate.eventKey, title, caption, people);
            if (record != null) candidate.photoId = record.id;
            else candidate.retryAfter = now + BuildingMilestonePolicy.QuietTicks;
        }
        internal static void Warn(Exception error)
        {
            try { Log.WarningOnce("[MomentFrames] building photograph skipped: " + error, 0x4D464255); } catch { }
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    internal static class Patch_MFBuildingCompleted
    {
        internal sealed class ConstructionState { internal Frame Frame; internal Map Map; internal IntVec3 Position; internal BuildableDef BuiltDef; internal bool Structural; }
        private static void Prefix(Frame __instance, out ConstructionState __state)
        {
            __state = null;
            try
            {
                if (__instance.Map == null || __instance.Faction != Faction.OfPlayer || !__instance.Map.IsPlayerHome) return;
                if (MomentFramesMod.Settings?.buildingPhotos != true || !SettingsCapturePolicy.Automatic(MomentFramesMod.Settings)) return;
                var def = __instance.def.entityDefToBuild;
                var thing = def as ThingDef;
                __state = new ConstructionState { Frame = __instance, Map = __instance.Map, Position = __instance.Position, BuiltDef = def,
                    Structural = thing != null && (thing.IsDoor || thing.Fillage == FillCategory.Full) };
                __state.Map.GetComponent<MapComponent_MFBuildingMilestones>().EnsureBaseline();
            }
            catch (Exception e) { __state = null; MapComponent_MFBuildingMilestones.Warn(e); }
        }
        private static void Postfix(ConstructionState __state)
        {
            if (__state == null || __state.Frame == null || !__state.Frame.Destroyed) return;
            try
            {
                bool built = __state.BuiltDef is TerrainDef terrain ? __state.Map.terrainGrid.TerrainAt(__state.Position) == terrain
                    : __state.BuiltDef is ThingDef thing && __state.Position.GetThingList(__state.Map).Any(t => t.def == thing && t.Spawned);
                if (built) __state.Map.GetComponent<MapComponent_MFBuildingMilestones>().ConstructionFinished(__state.Position, __state.Structural);
            }
            catch (Exception e) { MapComponent_MFBuildingMilestones.Warn(e); }
        }
    }
}
