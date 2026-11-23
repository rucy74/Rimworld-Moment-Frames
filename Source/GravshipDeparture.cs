using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Observe the completed native ship render, while the ship still exists on its origin map.
    /// The native callback schedules removal after a pan and 0.5s delay. MF neither postpones nor re-invokes any game call.</summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.OnGravshipCaptureComplete))]
    internal static class Patch_GravshipLaunch
    {
        private static readonly Dictionary<int, int> lastShotTick = new Dictionary<int, int>();
        internal static void Reset() => lastShotTick.Clear();

        private static void Postfix(Capture capture)
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msShip || capture?.IsGravshipCapture != true) return;
                Building_GravEngine engine = capture.engine;
                Map map = engine?.Map;
                var store = PhotoStore.Instance;
                if (engine?.Spawned != true || map == null || store == null) return;
                int now = Find.TickManager.TicksGame;
                if (lastShotTick.TryGetValue(engine.thingIDNumber, out int last) && now == last) return;
                lastShotTick[engine.thingIDNumber] = now;
                CellRect rect = Gravships.Footprint(engine, map).ExpandedBy(Historic.GravshipPad).ClipInsideMap(map);
                Designator_TakePhoto.FitRect(rect, out Vector3 center, out float span, out float aspect);
                if (span < Historic.GravshipLeastSpan) span = Historic.GravshipLeastSpan;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => rect.Contains(p.Position)).ToList();
                string key = "gravship:" + engine.thingIDNumber + ":" + now;
                string title = "MF_GravshipLaunch".Translate(engine.RenamableLabel);
                var rec = store.RequestAutomaticNow(map, center, "gravship", key, title, "", people, span, aspect, null);
                if (rec != null) rec.requiredThings.Add(engine.ThingID);
                else
                {
                    var record = store.RecordEvent(map, "gravship", key, title, "MF_DepartureRecordOnly".Translate());
                    if (record != null)
                    {
                        record.eventPeopleNames = people.Select(p => p.LabelShort).ToList();
                        record.eventPeopleIds = people.Select(p => p.GetUniqueLoadID()).ToList();
                    }
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[MomentFrames] departure observation failed: " + e); }
                catch { } // MF diagnostics must also leave the native callback's successful return alone.
            }
        }
    }
}
