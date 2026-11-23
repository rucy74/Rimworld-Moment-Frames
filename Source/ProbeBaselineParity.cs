using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A photo comes out the same whichever thread protects it (2026-11-06, -momentFramesBaselineParity).
    /// Everything it calls existed before the protected copy moved off the game thread, so the same file runs on the old and the new build and their
    /// parity.txt can be compared line by line: ids, hashes and paths are written as placeholders. Times go to parity-timing.txt, which is expected to differ.</summary>
    public static partial class Probe
    {
        private static bool baselineParityMode;
        private static IEnumerator bpScript;
        private static long bpLastUpdate, bpSavedAt;
        private static int bpMainThread, bpPreserveMain, bpPreserveWorker;
        private static double bpRemember;
        private static readonly Dictionary<int, double> bpFrameMs = new Dictionary<int, double>();
        private static readonly Dictionary<int, long> bpFrameStamp = new Dictionary<int, long>();
        private static readonly List<string> bpLogged = new List<string>(), bpRows = new List<string>(), bpTimes = new List<string>();
        private static readonly List<double[]> bpSaves = new List<double[]>();
        private static readonly List<int> bpSaveFrames = new List<int>();
        private static double BpMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void BaselineParityStep()
        {
            long now = Stopwatch.GetTimestamp();
            if (bpLastUpdate != 0) bpFrameMs[Time.frameCount] = BpMs(now - bpLastUpdate);
            bpFrameStamp[Time.frameCount] = now;
            bpLastUpdate = now;
            if (bpScript == null)
            {
                deadline = Time.realtimeSinceStartup + 600f;
                map = Find.CurrentMap; center = map.Center;
                bpMainThread = Thread.CurrentThread.ManagedThreadId;
                BpInstrument();
                bpScript = BpRun();
            }
            if (bpScript.MoveNext()) return;
            File.WriteAllLines(Path.Combine(dir, "parity.txt"), bpRows);
            File.WriteAllLines(Path.Combine(dir, "parity-timing.txt"), bpTimes);
            File.WriteAllText(Path.Combine(dir, "runtime-log.txt"), string.Join("\n", Log.Messages.Select(m => m.text)));
            Finish();
        }

        private static void BpInstrument()
        {
            var harmony = new Harmony("momentframes.probe.baselineparity");
            harmony.Patch(AccessTools.Method(typeof(PhotoStore), "OnCaptured"), prefix: new HarmonyMethod(typeof(Probe), nameof(BpSaveBefore)), postfix: new HarmonyMethod(typeof(Probe), nameof(BpSaveAfter)));
            harmony.Patch(AccessTools.Method(typeof(ExternalPhotoImport), nameof(ExternalPhotoImport.RememberRecord)), prefix: new HarmonyMethod(typeof(Probe), nameof(BpRememberBefore)), postfix: new HarmonyMethod(typeof(Probe), nameof(BpRememberAfter)));
            harmony.Patch(AccessTools.Method(typeof(ExternalPhotoFiles), nameof(ExternalPhotoFiles.Preserve)), prefix: new HarmonyMethod(typeof(Probe), nameof(BpPreserveCall)));
            foreach (var part in new[] {
                AccessTools.Method(typeof(PhotoFiles), nameof(PhotoFiles.WriteOriginal)),
                AccessTools.Method(typeof(PhotoPreviews), nameof(PhotoPreviews.Remember)),
                AccessTools.Method(typeof(ExternalPhotoFiles), nameof(ExternalPhotoFiles.Append)),
                AccessTools.Method(typeof(PhotoStore), "Announce"),
                AccessTools.Method(typeof(Conditions), nameof(Conditions.NotifySaved)),
                AccessTools.Method(typeof(PhotoFx), nameof(PhotoFx.Play)),
                AccessTools.Method(typeof(PhotoReactions), nameof(PhotoReactions.NotifySaved)),
                AccessTools.Method(typeof(PhotoDesc), nameof(PhotoDesc.TryApplyCaptured)) })
                harmony.Patch(part, prefix: new HarmonyMethod(typeof(Probe), nameof(BpPartBefore)), postfix: new HarmonyMethod(typeof(Probe), nameof(BpPartAfter)));
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(Probe), nameof(BpLogCall)));
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(Probe), nameof(BpLogCall)));
        }
        private static void BpSaveBefore() { bpSavedAt = Stopwatch.GetTimestamp(); bpRemember = 0; bpParts.Clear(); bpInSave = true; }
        private static bool bpInSave;
        private static readonly Dictionary<string, double> bpParts = new Dictionary<string, double>();
        private static readonly List<string> bpSaveParts = new List<string>();
        private static void BpPartBefore(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void BpPartAfter(long __state, System.Reflection.MethodBase __originalMethod)
        {
            if (!bpInSave || Thread.CurrentThread.ManagedThreadId != bpMainThread) return;
            bpParts.TryGetValue(__originalMethod.Name, out double sum);
            bpParts[__originalMethod.Name] = sum + BpMs(Stopwatch.GetTimestamp() - __state);
        }
        private static void BpSaveAfter()
        {
            double total = BpMs(Stopwatch.GetTimestamp() - bpSavedAt);
            bpInSave = false;
            bpSaveParts.Add(string.Join(", ", bpParts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value.ToString("0.0"))));
            bpSaves.Add(new[] { total, bpRemember, PhotoCapture.LastRenderMilliseconds, PhotoCapture.LastEncodeMilliseconds, PhotoCapture.LastRenderFrame });
            bpSaveFrames.Add(Time.frameCount);
        }
        private static long bpRememberAt;
        private static void BpRememberBefore() => bpRememberAt = Stopwatch.GetTimestamp();
        private static void BpRememberAfter() => bpRemember += BpMs(Stopwatch.GetTimestamp() - bpRememberAt);
        private static void BpPreserveCall()
        {
            if (Thread.CurrentThread.ManagedThreadId == bpMainThread) Interlocked.Increment(ref bpPreserveMain);
            else Interlocked.Increment(ref bpPreserveWorker);
        }
        private static void BpLogCall(string text)
        { if (text != null && text.Contains("[MomentFrames]")) lock (bpLogged) bpLogged.Add(text); }

        private static List<string> BpFiles(string folder)
        {
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/'))
                .Where(f => f != ExternalPhotoFiles.LedgerFile).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        private static int BpLedger(string folder) => ExternalPhotoFiles.History(folder).Count;
        private static string BpPng(string path)
        {
            if (path == null || !File.Exists(path)) return "none";
            byte[] b = File.ReadAllBytes(path);
            if (!ExternalPhotoFiles.Png(b, out _)) return "bad";
            if (PhotoJpeg.Size(b, out int jw, out int jh)) return jw + "x" + jh + " jpg";
            long w = 0, h = 0;
            for (int i = 16; i < 20; i++) w = (w << 8) | b[i];
            for (int i = 20; i < 24; i++) h = (h << 8) | b[i];
            return w + "x" + h;
        }
        private static bool BpSameFile(string a, string b) => File.Exists(a) && File.Exists(b) && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        private static string BpWhich(string got, string raw, string baseline, string none = "null")
        {
            if (got == null) return none;
            string g = Path.GetFullPath(got);
            if (baseline != null && g == Path.GetFullPath(baseline)) return "baseline";
            if (raw != null && g == Path.GetFullPath(raw)) return "raw";
            return "other";
        }

        /// <summary>One line of everything a player or a later feature can see of this photo, with the parts that change on every run replaced.</summary>
        private static void BpRow(string name, PhotoRecord rec, List<string> before, int ledgerBefore, int logBefore)
        {
            var store = PhotoStore.Instance;
            string folder = store.FolderPath;
            string stem = rec?.file != null ? Path.GetFileNameWithoutExtension(rec.file) : null;
            string raw = stem != null ? Path.Combine(folder, rec.file) : null;
            string rawThumb = stem != null ? Path.Combine(folder, stem + ".thumb.png") : null;
            string rawHash = raw != null && File.Exists(raw) ? ExternalPhotoFiles.HashFile(raw) : null;
            string hash = rec?.sourceSha256;
            string baseline = rec?.sourceBaselineFile.NullOrEmpty() == false ? Path.Combine(folder, rec.sourceBaselineFile) : null;
            string baseThumb = baseline != null ? Path.ChangeExtension(baseline, ".thumb.png") : null;
            string Clean(string s)
            {
                if (s == null) return s;
                s = s.Replace(folder, "<folder>").Replace(folder.Replace('\\', '/'), "<folder>");
                if (stem != null) s = s.Replace(stem, "<raw>");
                if (!hash.NullOrEmpty()) s = s.Replace(hash, "<hash>");
                if (rec?.id != null) s = s.Replace(rec.id, "<id>");
                return s;
            }
            var created = BpFiles(folder).Except(before).Select(Clean).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var gone = before.Except(BpFiles(folder)).Select(Clean).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var ledger = ExternalPhotoFiles.History(folder);
            int ledgerDelta = ledger.Count - ledgerBefore;
            string ledgerRow = "none";
            if (ledgerDelta > 0 && rec != null)
            {
                var e = ledger.Last();
                var wrong = new List<string>();
                if (e.file != rec.file) wrong.Add("file");
                if (e.hash != rec.sourceSha256) wrong.Add("hash");
                if (e.recordId != rec.id) wrong.Add("record");
                if (e.kind != rec.kind) wrong.Add("kind");
                if (e.title != rec.title) wrong.Add("title");
                if (e.note != rec.desc) wrong.Add("note");
                if (e.date != rec.dateLabel) wrong.Add("date");
                if (e.dateSlug != rec.dateSlug) wrong.Add("slug");
                if (e.year != rec.year) wrong.Add("year");
                if (e.ticks != rec.ticksAbs) wrong.Add("ticks");
                if (Math.Abs(e.aspect - rec.Aspect) > 0.0001f) wrong.Add("aspect");
                ledgerRow = wrong.Count == 0 ? "matches" : "differs:" + string.Join(",", wrong);
            }
            string[] logged;
            lock (bpLogged) logged = bpLogged.Skip(logBefore).Select(Clean).ToArray();
            string row = name
                + " | record listed=" + (rec != null && store.AllRecords.Contains(rec)) + " file=" + (rec?.HasFile == true) + " eventOnly=" + (rec?.eventOnly == true)
                + " | hash=" + (hash.NullOrEmpty() ? "none" : hash == rawHash ? "of-raw" : "OTHER")
                + " baseline=" + (baseline == null ? "none" : rec.sourceBaselineFile == Path.Combine(ExternalPhotoFiles.BaselineFolder, hash + Path.GetExtension(rec.file)) ? "named-by-hash" : "OTHER:" + Clean(rec.sourceBaselineFile))
                + " copy=" + (baseline == null ? "none" : BpSameFile(raw, baseline) ? "same-as-raw" : File.Exists(baseline) ? "DIFFERENT" : "MISSING")
                + " copyThumb=" + (baseThumb == null ? "none" : BpSameFile(rawThumb, baseThumb) ? "same-as-thumb" : File.Exists(baseThumb) ? "DIFFERENT" : "MISSING")
                + " | shows=" + BpWhich(rec != null ? store.OriginalPath(rec) : null, raw, baseline)
                + " showsThumb=" + BpWhich(rec != null ? store.OriginalThumbPath(rec) : null, rawThumb, baseThumb)
                + " | image=" + BpPng(raw) + " thumb=" + BpPng(rawThumb) + " preview=" + BpPng(rec?.HasFile == true ? PhotoPreviews.PathFor(store.OriginalPath(rec)) : null)
                + " | ledger+" + ledgerDelta + " " + ledgerRow
                + " | created=[" + string.Join(",", created) + "] gone=[" + string.Join(",", gone) + "]"
                + " | log=[" + string.Join(" ; ", logged) + "]";
            bpRows.Add(row);
            checks.Add("INFO: " + row);
        }

        private static IEnumerable BpWaitDone(PhotoRecord rec, Action<int> during = null)
        {
            var store = PhotoStore.Instance;
            float until = Time.realtimeSinceStartup + 60f;
            int startFrame = PhotoCapture.LastRenderFrame;
            while (!(rec == null || rec.HasFile || rec.eventOnly || !store.AllRecords.Contains(rec)) || PhotoCapture.Busy)
            {
                if (Time.realtimeSinceStartup > until) throw new TimeoutException("the capture did not finish");
                during?.Invoke(startFrame);
                yield return null;
            }
            for (int i = 0; i < 3; i++) yield return null;
        }

        /// <summary>Takes one photo through the real pipeline and writes its row.</summary>
        private static IEnumerable BpTake(string name, Func<PhotoRecord> start, Action<PhotoRecord> afterStart = null, Action<PhotoRecord, int> during = null, float settle = 0f, Action<PhotoRecord> atEnd = null)
        {
            var store = PhotoStore.Instance;
            var before = BpFiles(store.FolderPath);
            int ledgerBefore = BpLedger(store.FolderPath), logBefore = bpLogged.Count, savesBefore = bpSaves.Count;
            var rec = start();
            if (rec == null) throw new InvalidOperationException(name + ": no record was made");
            afterStart?.Invoke(rec);
            foreach (var step in BpWaitDone(rec, during == null ? (Action<int>)null : f => during(rec, f))) yield return step;
            if (settle > 0f)
            {
                float until = Time.realtimeSinceStartup + settle;
                while (Time.realtimeSinceStartup < until) yield return null;
            }
            atEnd?.Invoke(rec);
            BpRow(name, rec, before, ledgerBefore, logBefore);
            for (int i = savesBefore; i < bpSaves.Count; i++) BpTime(name, bpSaves[i], bpSaveFrames[i], bpSaveParts[i]);
            bpLast = rec;
        }
        private static PhotoRecord bpLast;

        private static void BpTime(string name, double[] save, int frame, string parts)
        {
            int shutter = (int)save[4];
            double saveFrame = bpFrameMs.TryGetValue(frame + 1, out var a) ? a : -1, shutterFrame = bpFrameMs.TryGetValue(shutter + 1, out var b) ? b : -1;
            double wait = bpFrameStamp.TryGetValue(frame, out var saved) && bpFrameStamp.TryGetValue(shutter, out var shot) ? BpMs(saved - shot) : -1;
            bpTimes.Add(name + "\tsave call " + save[0].ToString("0.00") + " ms\tof it the old protect " + save[1].ToString("0.00") + " ms\tsave frame " + saveFrame.ToString("0.0") + " ms\tshutter frame " + shutterFrame.ToString("0.0")
                + " ms\trender " + save[2].ToString("0.0") + " ms\tencode " + save[3].ToString("0.0") + " ms\tshutter to saved " + wait.ToString("0") + " ms\tin the save call: " + parts);
        }

        /// <summary>The photo card the player sees, from the preview and from the full-size original, at the same moment of its animation in the same paused scene.</summary>
        private static IEnumerable BpCardCompare(PhotoRecord rec)
        {
            string path = PhotoStore.Instance.FilePath(rec), preview = PhotoPreviews.PathFor(path);
            Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
            Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false);
            LudeonTK.EditWindow_Log.wantsToOpen = false;
            yield return null;
            foreach (var s in BpCardShot(rec, "card-from-preview.png")) yield return s;
            byte[] kept = File.ReadAllBytes(preview);
            File.Delete(preview); PhotoTextureCache.Forget(preview);
            foreach (var s in BpCardShot(rec, "card-from-original.png")) yield return s;
            File.WriteAllBytes(preview, kept); PhotoTextureCache.Forget(preview);
            var a = new Texture2D(2, 2); var b = new Texture2D(2, 2);
            a.LoadImage(File.ReadAllBytes(Path.Combine(dir, "card-from-preview.png")));
            b.LoadImage(File.ReadAllBytes(Path.Combine(dir, "card-from-original.png")));
            var pa = a.GetPixels32(); var pb = b.GetPixels32();
            float aspect = rec.Aspect, w0 = Mathf.Min(UI.screenWidth * 0.45f, UI.screenHeight * 0.55f * aspect), h0 = w0 / aspect;
            int x0 = Mathf.RoundToInt(UI.screenWidth / 2f - w0 / 2f) + 2, x1 = Mathf.RoundToInt(UI.screenWidth / 2f + w0 / 2f) - 2;
            int top = Mathf.RoundToInt(UI.screenHeight / 2f - 20f - h0 / 2f) + 2, bottom = Mathf.RoundToInt(UI.screenHeight / 2f - 20f + h0 / 2f) - 2;
            long sum = 0, count = 0; int big = 0;
            for (int y = a.height - bottom; y < a.height - top; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * a.width + x;
                    int d = Math.Abs(pa[i].r - pb[i].r) + Math.Abs(pa[i].g - pb[i].g) + Math.Abs(pa[i].b - pb[i].b);
                    sum += d; count++;
                    if (d > 72) big++;
                }
            string row = "card, preview against original | card " + (x1 - x0) + "x" + (bottom - top) + " on a " + a.width + "x" + a.height + " screen, same size " + (pa.Length == pb.Length) + ", mean difference per channel " + (sum / 3.0 / count).ToString("0.00") + " of 255, pixels clearly different " + (100.0 * big / count).ToString("0.00") + "%";
            checks.Add("INFO: " + row);
            File.WriteAllText(Path.Combine(dir, "card-compare.txt"), row + "\n");
        }
        private static IEnumerable BpCardShot(PhotoRecord rec, string name)
        {
            var win = new Window_PhotoPop(rec, true, false);
            Find.WindowStack.Add(win);
            var opened = Traverse.Create(win).Field("opened");
            for (int i = 0; i < 4; i++) { opened.SetValue(Time.realtimeSinceStartup); yield return null; }
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name));
            float until = Time.realtimeSinceStartup + 10f;
            while (!File.Exists(Path.Combine(dir, name)) && Time.realtimeSinceStartup < until) { opened.SetValue(Time.realtimeSinceStartup); yield return null; }
            for (int i = 0; i < 4; i++) { opened.SetValue(Time.realtimeSinceStartup); yield return null; }
            Find.WindowStack.TryRemove(win, false);
            for (int i = 0; i < 4; i++) yield return null;
        }

        private static IEnumerator BpRun()
        {
            var store = PhotoStore.Instance;
            ClearHostiles(map); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
            foreach (var cell in map.AllCells) map.fogGrid.Unfog(cell);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 24f);
            MomentFramesMod.Settings.autoCaptureEnabled = true;
            MomentFramesMod.Settings.msColonyStart = false;   // the game's own first-day photo would land inside a case
            var people = map.mapPawns.FreeColonistsSpawned.ToList();
            Vector3 c = center.ToVector3Shifted();
            for (int i = 0; i < 60; i++) yield return null;
            int idleFrom = Time.frameCount;
            for (int i = 0; i < 180; i++) yield return null;
            var idle = bpFrameMs.Where(kv => kv.Key >= idleFrom).Select(kv => kv.Value).OrderBy(v => v).ToList();
            double idleMedian = idle[idle.Count / 2];
            int n = 0;
            string Key() => "bp:" + (++n);
            PhotoRecord Now(float span = 14f, float aspect = 1.5f, bool light = false) { int id = n + 1; return store.RequestNow(map, c, "wedding", Key(), "bp-" + id, "note " + id, people, span, aspect, null, light); }
            PhotoRecord Queued(float span, float aspect) { int id = n + 1; return store.Request(map, c, "wedding", Key(), "bp-" + id, "note " + id, people, span, aspect); }

            // Ordinary photos, by every way a photo can start.
            foreach (var s in BpTake("first photo of the colony, taken now", () => Now())) yield return s;
            var first = bpLast;
            foreach (var s in BpTake("taken now, keeping the light", () => Now(14f, 1.5f, true))) yield return s;
            foreach (var s in BpTake("queued", () => Queued(14f, PhotoCapture.DefaultAspect))) yield return s;
            foreach (var s in BpTake("queued, panorama", () => Queued(12f, 2.4f))) yield return s;
            foreach (var s in BpTake("queued, portrait", () => Queued(16f, 0.7f))) yield return s;
            foreach (var s in BpTake("automatic, taken now", () => { int id = n + 1; return store.RequestAutomaticNow(map, c, "wedding", Key(), "bp-" + id, "note " + id, people, 14f, 1.5f, null); })) yield return s;
            foreach (var s in BpTake("automatic, a building's area", () => { int id = n + 1; return store.RequestAutomaticArea(map, CellRect.CenteredOn(center, 20), "wedding", Key(), "bp-" + id, "note " + id, people); })) yield return s;
            foreach (var s in BpTake("taken again in the same frame", () => store.Reshoot(first))) yield return s;

            // Photos turned away after the worker has run: nothing of them may stay behind.
            foreach (var s in BpTake("turned away, the required person is not in it", () => Now(), r => r.requiredPerson = "Human_nobody")) yield return s;
            foreach (var s in BpTake("turned away, a subject left before the shutter", () => Now(), r => r.requiredSubjects = new List<string> { "Human_nobody" })) yield return s;
            foreach (var s in BpTake("turned away, automatic photos were switched off", () => { int id = n + 1; return store.RequestAutomaticNow(map, c, "wedding", Key(), "bp-" + id, "note " + id, people, 14f, 1.5f, null); },
                r => MomentFramesMod.Settings.autoCaptureEnabled = false, atEnd: r => MomentFramesMod.Settings.autoCaptureEnabled = true)) yield return s;
            foreach (var s in BpTake("removed while it is being written", () => Now(), during: (r, f) => { if (PhotoCapture.LastRenderFrame != f && store.Records.Contains(r)) store.Remove(r); }, settle: 4f)) yield return s;
            bool reset = false;
            foreach (var s in BpTake("the game is reset while it is being written", () => Now(), during: (r, f) => { if (!reset && PhotoCapture.LastRenderFrame != f) { reset = true; PhotoCapture.Reset(); store.Remove(r); } }, settle: 4f)) yield return s;
            if (!reset) checks.Add("FAIL: the reset case never reached the shutter");
            if (PhotoCapture.Busy) checks.Add("FAIL: the camera stayed busy after the reset");

            // A game save and the album's folder check in the moment a photo is being written: neither may see it half made.
            int newImages = 0; bool savedMid = false;
            foreach (var s in BpTake("the game is saved and the folder checked while it is being written", () => Now(),
                during: (r, f) =>
                {
                    if (PhotoCapture.LastRenderFrame == f) return;
                    newImages += ExternalPhotoImport.Scan(store).Count(x => x.kind != ExternalPhotoKind.ChangedImage && x.kind != ExternalPhotoKind.MissingImage);
                    if (!savedMid && !r.HasFile) { savedMid = true; GameDataSaveLoader.SaveGame("mfbp-mid"); }
                })) yield return s;
            bpRows.Add("while it is being written | saved=" + savedMid + " the folder check found new images=" + newImages);

            // The same picture twice shares one copy; turning a third away must not take the copy from the other two.
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (var s in BpTake("same picture, first", () => Now(14f, 1.5f))) yield return s;
            var twinA = bpLast;
            foreach (var s in BpTake("same picture, second", () => Now(14f, 1.5f))) yield return s;
            var twinB = bpLast;
            bool twins = twinA.sourceSha256 == twinB.sourceSha256;
            foreach (var s in BpTake("same picture, third turned away", () => Now(14f, 1.5f), r => r.requiredPerson = "Human_nobody")) yield return s;
            bpRows.Add("same picture: the two kept photos share a copy=" + twins + ", it is still there=" + File.Exists(Path.Combine(store.FolderPath, twinA.sourceBaselineFile)));

            // No room for the protected copy: the photo itself must survive, exactly as before, and the next save repairs it.
            string originals = Path.Combine(store.FolderPath, ExternalPhotoFiles.BaselineFolder), parked = originals + "-parked";
            Directory.Move(originals, parked);
            File.WriteAllText(originals, "blocks the folder");
            foreach (var s in BpTake("the protected folder cannot be written", () => Now())) yield return s;
            var unprotectedRec = bpLast;
            File.Delete(originals);
            Directory.Move(parked, originals);
            {
                var before = BpFiles(store.FolderPath); int ledger = BpLedger(store.FolderPath), logs = bpLogged.Count;
                ExternalPhotoImport.ObserveCurrent(store);
                BpRow("the next save repairs it", unprotectedRec, before, ledger, logs);
            }

            // Taken while the game runs, one after the other.
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            {
                var before = BpFiles(store.FolderPath); int ledger = BpLedger(store.FolderPath), logs = bpLogged.Count, saves = bpSaves.Count;
                var burst = new List<PhotoRecord>();
                for (int i = 0; i < 4; i++) burst.Add(Queued(10f + i, 1.5f));
                foreach (var r in burst) foreach (var s in BpWaitDone(r)) yield return s;
                for (int i = 0; i < burst.Count; i++)
                {
                    var r = burst[i];
                    string raw = Path.Combine(store.FolderPath, r.file), copy = r.sourceBaselineFile.NullOrEmpty() ? null : Path.Combine(store.FolderPath, r.sourceBaselineFile);
                    bpRows.Add("in a row, photo " + (i + 1) + " | file=" + r.HasFile + " hash=" + (r.sourceSha256 == ExternalPhotoFiles.HashFile(raw) ? "of-raw" : "OTHER") + " copy=" + (copy != null && BpSameFile(raw, copy) ? "same-as-raw" : "WRONG") + " image=" + BpPng(raw));
                }
                var entries = ExternalPhotoFiles.History(store.FolderPath).Skip(ledger).ToList();
                bpRows.Add("in a row, ledger | lines+" + entries.Count + " own record each=" + entries.All(e => burst.Any(r => r.id == e.recordId && r.file == e.file && r.sourceSha256 == e.hash)) + " in the order saved=" + entries.Select(e => e.recordId).SequenceEqual(burst.OrderBy(r => r.capturedTicksAbs).Select(r => r.id).Where(id => entries.Any(e => e.recordId == id))));
                for (int i = saves; i < bpSaves.Count; i++) BpTime("in a row", bpSaves[i], bpSaveFrames[i], bpSaveParts[i]);
                _ = before; _ = logs;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            foreach (var s in BpCardCompare(first)) yield return s;

            // A real save and load: the record's fields come back and nothing is written again.
            {
                var kept = store.AllRecords.Where(r => r.HasFile).Select(r => new[] { r.id, r.file, r.sourceSha256, r.sourceBaselineFile }).ToList();
                var before = BpFiles(store.FolderPath); int ledger = BpLedger(store.FolderPath);
                GameDataSaveLoader.SaveGame("mfbp");
                yield return null;
                bpRows.Add("saved | files added=" + BpFiles(store.FolderPath).Except(before).Count(f => !f.EndsWith(".json") && !f.EndsWith(".tsv")) + " ledger+" + (BpLedger(store.FolderPath) - ledger));
                GameDataSaveLoader.LoadGame("mfbp");
                float until = Time.realtimeSinceStartup + 120f;
                while (LongEventHandler.ShouldWaitForEvent || PhotoStore.Instance == store || Find.CurrentMap == null)
                {
                    if (Time.realtimeSinceStartup > until) throw new TimeoutException("the load did not finish");
                    yield return null;
                }
                map = Find.CurrentMap;
                store = PhotoStore.Instance;
                for (int i = 0; i < 30; i++) yield return null;
                bool same = kept.All(k => { var r = store.AllRecords.FirstOrDefault(x => x.id == k[0]); return r != null && r.file == k[1] && r.sourceSha256 == k[2] && r.sourceBaselineFile == k[3]; });
                var filesAfter = BpFiles(store.FolderPath);
                ExternalPhotoImport.ObserveCurrent(store);
                bpRows.Add("loaded | every record the same=" + same + " count same=" + (store.AllRecords.Count(r => r.HasFile) == kept.Count) + " observe wrote files=" + BpFiles(store.FolderPath).Except(filesAfter).Count() + " ledger+" + (BpLedger(store.FolderPath) - ledger));
            }

            // What the copy is for: a picture replaced from outside still shows the original, and the album notices.
            {
                var r = store.AllRecords.First(x => x.HasFile && !x.sourceBaselineFile.NullOrEmpty());
                string raw = Path.Combine(store.FolderPath, r.file), copy = Path.Combine(store.FolderPath, r.sourceBaselineFile);
                File.WriteAllBytes(raw, File.ReadAllBytes(Path.Combine(store.FolderPath, Path.GetFileNameWithoutExtension(r.file) + ".thumb.png")));
                ExternalPhotoImport.CompleteScan(new List<ExternalPhotoCandidate>());
                bpRows.Add("replaced from outside | shows=" + BpWhich(store.OriginalPath(r), raw, copy) + " album finds it changed=" + ExternalPhotoImport.Scan(store).Any(x => x.file == r.file && x.kind == ExternalPhotoKind.ChangedImage));
            }

            // Only the new build has these; on the old one the row says so, and the difference is expected.
            BpNewBuildOnly();

            // The picture of what the mod did to the frame. The two builds differ here on purpose.
            bpTimes.Insert(0, "idle frame " + idleMedian.ToString("0.0") + " ms (median of " + idle.Count + "), protected copies made on the game thread " + bpPreserveMain + ", on a worker " + bpPreserveWorker);
            foreach (string row in bpTimes) checks.Add("TIME: " + row);

            // Plain expectations, so a single run can also fail by itself.
            string Find1(string name) => bpRows.FirstOrDefault(r => r.StartsWith(name + " |")) ?? "";
            void Expect(bool ok, string what) => checks.Add((ok ? "PASS: " : "FAIL: ") + what);
            foreach (string name in new[] { "first photo of the colony, taken now", "taken now, keeping the light", "queued", "queued, panorama", "queued, portrait", "automatic, taken now", "automatic, a building's area", "taken again in the same frame" })
            {
                string row = Find1(name);
                Expect(row.Contains("hash=of-raw") && row.Contains("baseline=named-by-hash") && row.Contains("copy=same-as-raw") && row.Contains("copyThumb=same-as-thumb") && row.Contains("shows=baseline") && row.Contains("showsThumb=baseline") && row.Contains("ledger+1 matches") && !row.Contains("image=none") && !row.Contains("preview=none"), "a saved photo has its protected copy, shows it, and is in the ledger: " + name);
            }
            foreach (string name in new[] { "turned away, the required person is not in it", "turned away, a subject left before the shutter", "turned away, automatic photos were switched off", "removed while it is being written", "the game is reset while it is being written" })
            {
                string row = Find1(name);
                Expect(row.Contains("created=[]") && row.Contains("ledger+0") && row.Contains("file=False"), "nothing stays behind: " + name);
            }
            {
                string row = Find1("the protected folder cannot be written");
                Expect(row.Contains("file=True") && row.Contains("hash=none") && row.Contains("baseline=none") && row.Contains("ledger+0") && row.Contains("photo baseline/index unavailable"), "without room for the copy the photo survives, and the player's log says why");
                string repaired = Find1("the next save repairs it");
                Expect(repaired.Contains("hash=of-raw") && repaired.Contains("copy=same-as-raw") && repaired.Contains("ledger+0"), "the next save makes the missing copy");
            }
            if (bpRows.Any(r => r.Contains(".tmp"))) checks.Add("FAIL: a temporary file was left behind");
        }

        private static void BpNewBuildOnly()
        {
            var prepare = AccessTools.Method(typeof(ExternalPhotoFiles), "Prepare");
            if (prepare == null) { File.WriteAllText(Path.Combine(dir, "parity-new-only.txt"), "this build has no worker copy\n"); return; }
            string folder = Path.Combine(PhotoStore.Instance.FolderPath, "bp-unit");
            Directory.CreateDirectory(folder);
            var rows = new List<string>();
            byte[] png = { 1, 2, 3, 4 }, thumb = { 5, 6 }, preview = { 7, 8, 9 };
            object Ask() => prepare.Invoke(null, new object[] { folder, png, thumb, preview });
            string hash = ExternalPhotoFiles.Hash(png), image = Path.Combine(folder, ExternalPhotoFiles.BaselineFolder, hash + ".png"), small = Path.ChangeExtension(image, ".thumb.png"), big = PhotoPreviews.PathFor(image);
            int Temp() => Directory.GetFiles(folder, "*.tmp").Length;
            void Row(bool ok, string what) { rows.Add((ok ? "PASS: " : "FAIL: ") + what); checks.Add((ok ? "PASS: " : "FAIL: ") + what); }
            void Release(object p, bool keep = false) => AccessTools.Method(p.GetType(), "Release").Invoke(p, new object[] { keep });
            var first = Ask();
            Row(File.Exists(image) && File.Exists(small) && File.Exists(big) && Temp() == 2, "the worker writes the copy, its thumbnail and preview, and the picture and thumbnail under temporary names");
            Row(File.ReadAllBytes(image).SequenceEqual(png) && File.ReadAllBytes(small).SequenceEqual(thumb) && File.ReadAllBytes(big).SequenceEqual(preview), "what it writes is exactly what it was given");
            var second = Ask();
            Release(second);
            Row(File.Exists(image) && File.Exists(small) && File.Exists(big) && Temp() == 2, "a second request for the same picture lets go: the first one's files stay, its own temporaries go");
            Release(first);
            Row(!File.Exists(image) && !File.Exists(small) && !File.Exists(big) && Temp() == 0, "the first request lets go: everything is gone");
            Release(first);
            Row(Temp() == 0, "letting go twice does nothing");
            var kept = Ask();
            Release(kept, true);
            Row(File.Exists(image) && Temp() == 0, "a kept photo keeps its copy and loses only the temporaries");
            // The final name
            var write = AccessTools.Method(typeof(PhotoFiles), nameof(PhotoFiles.WriteOriginal));
            string temp = Path.Combine(folder, "a.tmp"), final = Path.Combine(folder, "a.png");
            File.WriteAllBytes(temp, png);
            write.Invoke(null, new object[] { final, thumb, temp });
            Row(File.ReadAllBytes(final).SequenceEqual(png) && !File.Exists(temp), "a finished file only changes its name");
            File.Delete(final);
            write.Invoke(null, new object[] { final, thumb, Path.Combine(folder, "gone.tmp") });
            Row(File.ReadAllBytes(final).SequenceEqual(thumb), "if the worker's file is gone the bytes are written the ordinary way");
            File.WriteAllBytes(temp, png);
            bool refused = false;
            try { write.Invoke(null, new object[] { final, png, temp }); }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is IOException) { refused = true; }
            Row(refused && File.ReadAllBytes(final).SequenceEqual(thumb), "an existing photo is never overwritten");
            // A worker that cannot write throws, and leaves nothing
            string blocked = Path.Combine(folder, "blocked");
            File.WriteAllText(blocked, "a file where the folder should be");
            bool threw = false;
            try { prepare.Invoke(null, new object[] { blocked, png, thumb, preview }); }
            catch (System.Reflection.TargetInvocationException) { threw = true; }
            Row(threw, "a worker that cannot write says so, so the game thread takes over");
            Directory.Delete(folder, true);
            File.WriteAllLines(Path.Combine(dir, "parity-new-only.txt"), rows);
        }
    }
}
