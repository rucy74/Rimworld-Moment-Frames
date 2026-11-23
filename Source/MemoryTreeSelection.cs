using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace MomentFrames
{
    /// <summary>A stable place on the memory tree. A pin protects the choice from automatic replacement.</summary>
    public sealed class MemoryTreeSlot : IExposable
    {
        public string photoId;
        public bool pinned;

        public void ExposeData()
        {
            Scribe_Values.Look(ref photoId, "photoId");
            Scribe_Values.Look(ref pinned, "pinned", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrWhiteSpace(photoId))
            {
                photoId = null;
                pinned = false;
            }
        }
    }

    /// <summary>Per-tree choices and automatic history. Sync never redraws old choices at random.</summary>
    public sealed class MemoryTreeSelection : IExposable
    {
        public const int Capacity = 35;
        public List<MemoryTreeSlot> slots = Enumerable.Range(0, Capacity).Select(_ => new MemoryTreeSlot()).ToList();
        public bool autoFill = true;
        public List<string> allowedKinds = new List<string>(); // empty means every kind
        private List<string> seen = new List<string>();
        private bool reevaluate = true;

        public bool Pick(int slot, string photoId, bool pin = true)
        {
            bool changed = Normalize();
            if (slot < 0 || slot >= Capacity || string.IsNullOrWhiteSpace(photoId)) return changed;
            for (int i = 0; i < Capacity; i++)
                if (i != slot && slots[i].photoId == photoId) changed |= Write(i, null, false);
            changed |= Write(slot, photoId, pin);
            if (!seen.Contains(photoId)) { seen.Add(photoId); changed = true; }
            return changed;
        }

        public bool SetPinned(int slot, bool pin)
        {
            bool changed = Normalize();
            if (slot < 0 || slot >= Capacity || slots[slot].photoId == null) return changed;
            if (slots[slot].pinned != pin) { slots[slot].pinned = pin; changed = true; }
            return changed;
        }

        public bool Clear(int slot)
        {
            bool changed = Normalize();
            if (slot < 0 || slot >= Capacity) return changed;
            // Keep the ID in seen: clearing a choice must not immediately select it again.
            return Write(slot, null, false) || changed;
        }

        public bool Configure(bool enabled, IEnumerable<string> kinds)
        {
            bool changed = Normalize();
            var next = CleanKinds(kinds);
            bool filterChanged = !allowedKinds.SequenceEqual(next);
            if (filterChanged || (enabled && !autoFill)) reevaluate = true;
            if (autoFill != enabled) { autoFill = enabled; changed = true; }
            if (filterChanged) { allowedKinds = next; changed = true; }
            return changed;
        }

        public bool Sync(IEnumerable<PhotoRecord> records, Func<PhotoRecord, bool> canDisplay)
        {
            bool changed = Normalize();
            var all = records == null ? new List<PhotoRecord>() : records
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.id))
                .GroupBy(r => r.id, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var byId = all.ToDictionary(r => r.id, StringComparer.Ordinal);
            var visited = new HashSet<string>(seen, StringComparer.Ordinal);
            if (visited.RemoveWhere(id => !byId.ContainsKey(id)) > 0) changed = true;
            var kinds = new HashSet<string>(allowedKinds, StringComparer.Ordinal);
            bool Allowed(PhotoRecord r) => kinds.Count == 0 || kinds.Contains(r.kind ?? "");

            for (int i = 0; i < Capacity; i++)
            {
                string id = slots[i].photoId;
                if (id == null) continue;
                if (!byId.TryGetValue(id, out var record) || (!slots[i].pinned && (record.Archived || !Allowed(record))))
                {
                    // Archived, it gives up an unpinned place; once restored it may earn one again. A pinned place waits for it.
                    if (record?.Archived == true) visited.Remove(id);
                    changed |= Write(i, null, false);
                }
                // The record still exists: missing PNGs keep their place and pin as a placeholder.
                else if (visited.Add(id)) changed = true;
            }

            if (autoFill)
            {
                var ready = all.Where(r => Allowed(r) && r.HasFile && (canDisplay == null || canDisplay(r)))
                    .OrderBy(r => r.ticksAbs).ThenBy(r => r.id, StringComparer.Ordinal).ToList();
                if (reevaluate)
                {
                    changed |= Reconcile(ready, byId, visited);
                    reevaluate = false;
                    changed = true;
                }
                else
                {
                    var displayed = new HashSet<string>(slots.Where(s => s.photoId != null).Select(s => s.photoId), StringComparer.Ordinal);
                    foreach (var record in ready)
                    {
                        if (visited.Contains(record.id)) continue;
                        if (displayed.Contains(record.id)) { visited.Add(record.id); changed = true; continue; }
                        int destination = slots.FindIndex(s => s.photoId == null);
                        if (destination < 0) destination = OldestUnpinned(byId);
                        // All places are pinned: leave the new ID unseen so it waits for a later unpin.
                        if (destination < 0) continue;
                        if (slots[destination].photoId != null) displayed.Remove(slots[destination].photoId);
                        changed |= Write(destination, record.id, false);
                        displayed.Add(record.id);
                        visited.Add(record.id);
                    }
                }
            }
            if (seen.Count != visited.Count || !visited.SetEquals(seen))
            {
                seen = visited.OrderBy(id => id, StringComparer.Ordinal).ToList();
                changed = true;
            }
            return changed;
        }

        private bool Reconcile(List<PhotoRecord> ready, Dictionary<string, PhotoRecord> byId, HashSet<string> visited)
        {
            bool changed = false;
            var fixedIds = new HashSet<string>(slots.Where(s => s.pinned).Select(s => s.photoId), StringComparer.Ordinal);
            int room = Capacity - fixedIds.Count;
            var candidates = ready.Where(r => !fixedIds.Contains(r.id)).ToList();
            // Keep already-desired photos in their places; fill with the latest eligible history.
            var desired = new HashSet<string>(candidates.AsEnumerable().Reverse().Take(room).Select(r => r.id), StringComparer.Ordinal);
            // If there are spare places, retain current unavailable images as placeholders.
            for (int i = 0; i < Capacity && desired.Count < room; i++)
                if (!slots[i].pinned && slots[i].photoId != null) desired.Add(slots[i].photoId);
            for (int i = 0; i < Capacity; i++)
                if (!slots[i].pinned && slots[i].photoId != null && !desired.Contains(slots[i].photoId))
                    changed |= Write(i, null, false);
            var displayed = new HashSet<string>(slots.Where(s => s.photoId != null).Select(s => s.photoId), StringComparer.Ordinal);
            foreach (var record in candidates.Where(r => desired.Contains(r.id)))
            {
                if (displayed.Contains(record.id)) continue;
                int destination = slots.FindIndex(s => s.photoId == null);
                if (destination < 0) break;
                changed |= Write(destination, record.id, false);
                displayed.Add(record.id);
            }
            if (room > 0)
            {
                // Old eligible photos outside the latest set are consumed, not rotated next tick.
                foreach (var record in ready) if (visited.Add(record.id)) changed = true;
            }
            // With all 35 places pinned, unselected candidates remain pending instead.
            return changed;
        }

        private int OldestUnpinned(Dictionary<string, PhotoRecord> byId)
        {
            int result = -1;
            for (int i = 0; i < Capacity; i++)
            {
                if (slots[i].pinned || slots[i].photoId == null) continue;
                if (result < 0) { result = i; continue; }
                var next = byId[slots[i].photoId];
                var prior = byId[slots[result].photoId];
                if (next.ticksAbs < prior.ticksAbs || (next.ticksAbs == prior.ticksAbs && string.CompareOrdinal(next.id, prior.id) < 0)) result = i;
            }
            return result;
        }

        private bool Write(int slot, string photoId, bool pin)
        {
            bool pinned = photoId != null && pin;
            if (slots[slot].photoId == photoId && slots[slot].pinned == pinned) return false;
            slots[slot].photoId = photoId;
            slots[slot].pinned = pinned;
            return true;
        }

        private static List<string> CleanKinds(IEnumerable<string> kinds) => kinds == null ? new List<string>() : kinds
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();

        private bool Normalize()
        {
            bool changed = false;
            if (slots == null) { slots = new List<MemoryTreeSlot>(); changed = true; }
            if (slots.Count > Capacity) { slots.RemoveRange(Capacity, slots.Count - Capacity); changed = true; }
            while (slots.Count < Capacity) { slots.Add(new MemoryTreeSlot()); changed = true; }
            var owners = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Capacity; i++)
            {
                if (slots[i] == null) { slots[i] = new MemoryTreeSlot(); changed = true; }
                if (string.IsNullOrWhiteSpace(slots[i].photoId)) { changed |= Write(i, null, false); continue; }
                string id = slots[i].photoId;
                if (!owners.TryGetValue(id, out int prior)) { owners.Add(id, i); continue; }
                // Repair malformed/legacy duplicates by keeping a pin in preference to an automatic choice.
                if (slots[i].pinned && !slots[prior].pinned) { changed |= Write(prior, null, false); owners[id] = i; }
                else changed |= Write(i, null, false);
            }
            var cleanKinds = CleanKinds(allowedKinds);
            if (allowedKinds == null || !allowedKinds.SequenceEqual(cleanKinds)) { allowedKinds = cleanKinds; changed = true; }
            var cleanSeen = seen == null ? new List<string>() : seen.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            if (seen == null || !seen.SequenceEqual(cleanSeen)) { seen = cleanSeen; changed = true; }
            return changed;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref slots, "slots", LookMode.Deep);
            Scribe_Values.Look(ref autoFill, "autoFill", true);
            Scribe_Collections.Look(ref allowedKinds, "allowedKinds", LookMode.Value);
            Scribe_Collections.Look(ref seen, "seen", LookMode.Value);
            Scribe_Values.Look(ref reevaluate, "reevaluate", true);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Normalize();
        }
    }
}
