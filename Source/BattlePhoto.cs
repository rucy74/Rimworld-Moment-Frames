using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A first-exchange negative, outside the album until the fight qualifies.
    /// Saved negatives stay immutable so loading an earlier save cannot lose its photograph.</summary>
    internal static class BattlePhoto
    {
        private const string Folder = ".battle-candidates";

        internal static void Capture(PhotoStore store, Battles.Fight fight, Thing attacker, Thing target, IntVec3? targetCell = null)
        {
            if (store == null || fight.map != Find.CurrentMap || PhotoCapture.Busy) return;
            var rect = CellRect.FromLimits(attacker.Position, targetCell ?? target.Position).ExpandedBy(6).ClipInsideMap(fight.map);
            Designator_TakePhoto.FitRect(rect, out Vector3 center, out float span, out float aspect);
            var people = new[] { attacker as Pawn, target as Pawn }.Where(p => p != null).ToList();
            var rec = store.CreateBattleCandidate(fight.map, center, span, aspect,
                "battle:" + fight.startTick + ":" + fight.map.uniqueID, people);
            if (rec == null) return;
            fight.candidate = rec;
            PhotoCapture.Capture(fight.map, rec.center, rec.spanCells, result =>
            {
                if (PhotoStore.Instance != store || rec.captureCancelled || result?.png == null
                    || !SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings.battleMode == Battles.ModeOff) return;
                try
                {
                    rec.peopleIds = result.capturedPeopleIds;
                    rec.people = result.capturedPeopleNames;
                    // Freeze facts now; only an accepted album photo consumes caption history.
                    rec.captionScene = result.captionContext?.Prefix;
                    rec.captionValues = result.captionContext?.Values?.ToList();
                    PhotoCapture.ApplyPeopleBox(rec, result);
                    string relative = Path.Combine(Folder, rec.id + PhotoImageFormat.ExtensionOf(result.png));
                    Directory.CreateDirectory(Path.Combine(store.FolderPath, Folder));
                    PhotoFiles.WriteOriginal(Path.Combine(store.FolderPath, relative), result.png);
                    if (result.thumb != null) PhotoFiles.TryDerived(Path.Combine(store.FolderPath, Folder, rec.id + ".thumb.png"), () => result.thumb);
                    rec.file = relative;
                    rec.capturedTicksAbs = result.capturedAbs;
                }
                catch (Exception e) { Log.Warning("[MomentFrames] first-exchange photograph failed: " + e.Message); }
            }, rec.Aspect, reactionRecord: rec);
        }

        internal static PhotoRecord Publish(PhotoStore store, Battles.Fight fight, string title)
        {
            var rec = fight.candidate;
            string source = CandidatePath(store, rec);
            if (source == null || !File.Exists(source)) return null;
            string old = rec.file;
            string file = rec.dateSlug + "_battle_" + rec.id + Path.GetExtension(source);
            try
            {
                // Keep a saved negative for any earlier save branch. Never overwrite an album original.
                string destination = Path.Combine(store.FolderPath, file);
                if (!File.Exists(destination)) File.Copy(source, destination);
                string thumb = Path.ChangeExtension(source, ".thumb.png");
                string destinationThumb = Path.ChangeExtension(destination, ".thumb.png");
                if (File.Exists(thumb) && !File.Exists(destinationThumb))
                    PhotoFiles.TryDerived(destinationThumb, () => File.ReadAllBytes(thumb));
                rec.title = title; rec.eventEnded = true;
                rec.file = null;
                PhotoDesc.TryApplyFrozen(rec);
                rec.file = file;
                if (!store.CommitRecord(rec)) { rec.file = old; return null; }
                if (!fight.candidateSaved) DeleteNegative(source);
                try { store.AnnounceBattle(rec); }
                catch (Exception e) { Log.Warning("[MomentFrames] battle photo notification failed: " + e.Message); }
                return rec;
            }
            catch (Exception e)
            {
                rec.file = old;
                Log.Warning("[MomentFrames] could not publish the first-exchange photograph: " + e.Message);
                return null;
            }
        }

        internal static void Discard(PhotoStore store, Battles.Fight fight)
        {
            if (fight.candidate == null) return;
            fight.candidate.captureCancelled = true;
            if (fight.candidateSaved) return;
            var path = CandidatePath(store, fight.candidate);
            if (path != null) DeleteNegative(path);
        }

        private static string CandidatePath(PhotoStore store, PhotoRecord rec)
        {
            if (store == null || rec == null || rec.id == null || rec.id.Length != 32 || !rec.id.All(Uri.IsHexDigit)) return null;
            foreach (string extension in new[] { ".png", ".jpg" })
                if (rec.file == Path.Combine(Folder, rec.id + extension)) return Path.Combine(store.FolderPath, Folder, rec.id + extension);
            return null;
        }

        private static void DeleteNegative(string path)
        {
            try { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".thumb.png")); }
            catch (Exception e) { Log.Warning("[MomentFrames] could not remove unused battle negative: " + e.Message); }
        }
    }
}
