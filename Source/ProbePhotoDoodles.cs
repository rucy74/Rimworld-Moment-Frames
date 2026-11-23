using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    // Only entered by Probe's guarded command-line mode in an explicitly marked disposable profile.
    public static partial class Probe
    {
        private static bool photoDoodlesMode, doodlesDone, doodlesFailCapture;
        private static readonly List<Pawn> doodlesPeople = new List<Pawn>();
        private static readonly List<string> doodlesPersonIds = new List<string>();
        private static readonly Dictionary<string, DoodlesBakeTrace> doodlesBakes = new Dictionary<string, DoodlesBakeTrace>();
        private static PhotoRecord doodlesRecord;
        private static string doodlesRecordId, doodlesOriginalHash, doodlesThumbHash, doodlesEditId, doodlesEditHash;
        private static string doodlesBirthdayId, doodlesWeddingId;
        private static int doodlesMode, doodlesEventDraws, doodlesStatusCalls;
        private static float doodlesShotWait = -1f;
        private static bool doodlesShotRetried;
        private static Dialog_AlbumStickers doodlesEditor;
        private static Dialog_GroupPhotoSetup doodlesGroup;
        private static Building_Bed doodlesBed;
        private static Pawn doodlesSleeper;

        private sealed class DoodlesBakeTrace
        {
            internal int Mode, Changed, Baked;
            internal string Label;
            internal readonly List<string> Subjects = new List<string>();
            internal readonly List<PhotoIcon> Icons = new List<PhotoIcon>();
            internal byte[] Before, After, Thumb;
            internal Color32[] Pixels;
        }

        private static void DoodlesCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("photo doodles: " + label);
            checks.Add("PASS: " + label);
        }
        private static string DoodlesHash(string path)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }
        private static void DoodlesBeforeBake(Texture2D image, PhotoReactions.Plan plan, out DoodlesBakeTrace __state)
        {
            var tex = image;
            __state = null;
            if (!photoDoodlesMode || !active || plan?.Record == null) return;
            __state = new DoodlesBakeTrace { Mode = plan.Mode, Label = DoodlesCaptureLabel(plan.Record, plan.Mode), Before = tex.EncodeToPNG(), Pixels = tex.GetPixels32() };
            __state.Subjects.AddRange(plan.Entries.Select(e => e.Pawn.GetUniqueLoadID()));
            __state.Icons.AddRange(plan.Entries.Select(e => e.Icon));
        }
        private static void DoodlesAfterBake(Texture2D image, PhotoReactions.Plan plan, int __result, DoodlesBakeTrace __state)
        {
            var tex = image;
            if (__state == null) return;
            var after = tex.GetPixels32();
            __state.Changed = after.Where((p, i) => p.r != __state.Pixels[i].r || p.g != __state.Pixels[i].g || p.b != __state.Pixels[i].b).Count();
            __state.Baked = __result; __state.After = tex.EncodeToPNG();
            tex.Apply(false, false);
            int width = Mathf.Max(1, Mathf.RoundToInt(PhotoCapture.ThumbHeight * tex.width / (float)tex.height));
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, PhotoCapture.ThumbHeight, 0, RenderTextureFormat.ARGB32);
            var thumb = new Texture2D(width, PhotoCapture.ThumbHeight, TextureFormat.RGB24, false);
            try
            {
                Graphics.Blit(tex, rt); RenderTexture.active = rt;
                thumb.ReadPixels(new Rect(0, 0, width, PhotoCapture.ThumbHeight), 0, 0, false); thumb.Apply(false, false);
                __state.Thumb = thumb.EncodeToPNG();
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.Destroy(thumb); }
            doodlesBakes[plan.Record.id] = __state;
            File.WriteAllBytes(Path.Combine(dir, __state.Label + "-before.png"), __state.Before);
            File.WriteAllBytes(Path.Combine(dir, __state.Label + "-after.png"), __state.After);
        }
        private static bool DoodlesMaybeFail(ref PhotoCapture.Result __result)
        {
            if (!photoDoodlesMode || !doodlesFailCapture) return true;
            doodlesFailCapture = false; __result = null; return false;
        }
        private static void DoodlesNoteEvent() { if (photoDoodlesMode && active) doodlesEventDraws++; }
        private static void DoodlesNoteStatus() { if (photoDoodlesMode && active) doodlesStatusCalls++; }

        private static bool DoodlesRequire(string file)
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            { doodlesShotWait = -1f; doodlesShotRetried = false; Require(file); return true; }
            if (doodlesShotWait < 0f) doodlesShotWait = Time.realtimeSinceStartup;
            float elapsed = Time.realtimeSinceStartup - doodlesShotWait;
            if (elapsed > 15f) throw new TimeoutException("native screenshot did not finish: " + file);
            if (elapsed > 1f && !doodlesShotRetried) { Shot(file); doodlesShotRetried = true; }
            return false;
        }
        private static void DoodlesAppearance(bool dark = false)
        {
            MomentFramesMod.Settings.albumTheme = dark ? "darkroom" : "graphite";
            MomentFramesMod.Settings.albumFont = dark ? "system" : "pencil";
            MomentFramesMod.Settings.albumTextSize = 130;
        }
        private static void DoodlesPose()
        {
            for (int i = 0; i < doodlesPeople.Count; i++)
            {
                var pawn = doodlesPeople[i]; FixReady(pawn);
                pawn.Position = center + new IntVec3(i * 2 - 6, 0, -2); pawn.Notify_Teleported(true, true);
                pawn.Drawer.tweener.ResetTweenedPosToRoot();
            }
            Find.Selector.ClearSelection(); Find.MainTabsRoot.EscapeCurrentTab(false);
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 10f);
        }
        private static PhotoRecord DoodlesFixture(string kind = "group")
        {
            var rec = new PhotoRecord { id = "doodles-fixture", kind = kind, mapId = map.uniqueID,
                center = center.ToVector3Shifted(), spanCells = 12f, aspect = 1.5f, ticksAbs = Find.TickManager.TicksAbs };
            rec.SetPeople(doodlesPeople); return rec;
        }
        private static void DoodlesPriorityChecks()
        {
            var subject = doodlesPeople[7];
            var birthday = DoodlesFixture(Kinds.Birthday); birthday.requiredPerson = subject.GetUniqueLoadID();
            bool birthdayPrioritized = true;
            for (int seed = 0; seed < 40; seed++)
            {
                birthday.id = "doodles-birthday-" + seed;
                var plan = PhotoReactions.CreatePlan(birthday, map, PhotoReactions.ModePhoto);
                birthdayPrioritized &= plan.Entries.Count <= 3 && plan.Entries.Count > 0
                    && plan.Entries[0].Pawn == subject && plan.Entries[0].Icon == PhotoIcon.Star;
            }
            DoodlesCheck(birthdayPrioritized, "eligible birthday subject always receives the first Star through 40 stable seeds");
            var originalName = subject.Name;
            try
            {
                subject.Name = new NameTriple("Renamed", "New birthday nickname", "Probe");
                var renamed = PhotoReactions.CreatePlan(birthday, map, PhotoReactions.ModePhoto);
                DoodlesCheck(renamed.Entries[0].Pawn == subject && renamed.Entries[0].Icon == PhotoIcon.Star,
                    "birthday priority uses stable subject ID after a nickname change");
            }
            finally { subject.Name = originalName; }
            subject.drafter.Drafted = true;
            DoodlesCheck(PhotoReactions.CreatePlan(birthday, map, PhotoReactions.ModePhoto).Entries.All(e => e.Pawn != subject),
                "birthday priority never overrides a drafted subject's exclusion");
            subject.drafter.Drafted = false; subject.health.AddHediff(HediffDefOf.Anesthetic);
            DoodlesCheck(subject.Downed && PhotoReactions.CreatePlan(birthday, map, PhotoReactions.ModePhoto).Entries.All(e => e.Pawn != subject),
                "birthday priority never overrides a downed subject's exclusion");
            FixReady(subject);

            var first = doodlesPeople[6]; var second = doodlesPeople[7];
            var wedding = DoodlesFixture("wedding"); wedding.eventSubjectIds = new List<string> { first.GetUniqueLoadID(), second.GetUniqueLoadID() };
            bool pairPriority = true;
            for (int seed = 0; seed < 40; seed++)
            {
                wedding.id = "doodles-wedding-" + seed; var plan = PhotoReactions.CreatePlan(wedding, map, PhotoReactions.ModePhoto);
                pairPriority &= plan.Entries.Count >= 2 && plan.Entries.Count <= 3
                    && new HashSet<Pawn>(plan.Entries.Take(2).Select(e => e.Pawn)).SetEquals(new[] { first, second })
                    && plan.Entries.Take(2).All(e => e.Icon == PhotoIcon.Heart)
                    && plan.Entries.Where(e => e.Pawn != first && e.Pawn != second).All(e => e.Icon != PhotoIcon.Heart);
            }
            DoodlesCheck(pairPriority, "both verified wedding subjects receive priority Hearts and guests receive no Heart through 40 seeds");
            wedding.SetPeople(new[] { first, second });
            var alone = PhotoReactions.CreatePlan(wedding, map, PhotoReactions.ModePhoto);
            DoodlesCheck(alone.Entries.Count == 2 && alone.Entries.All(e => e.Icon == PhotoIcon.Heart),
                "wedding photograph containing only the two verified subjects still gives both a Heart");
            wedding.SetPeople(doodlesPeople);
            originalName = first.Name;
            try
            {
                first.Name = new NameTriple("Renamed", "New wedding nickname", "Probe");
                var renamed = PhotoReactions.CreatePlan(wedding, map, PhotoReactions.ModePhoto);
                DoodlesCheck(renamed.Entries.Any(e => e.Pawn == first && e.Icon == PhotoIcon.Heart)
                    && renamed.Entries.Any(e => e.Pawn == second && e.Icon == PhotoIcon.Heart), "wedding pair identity survives nickname changes");
            }
            finally { first.Name = originalName; }
            first.drafter.Drafted = true;
            var one = PhotoReactions.CreatePlan(wedding, map, PhotoReactions.ModePhoto);
            DoodlesCheck(one.Entries[0].Pawn == second && one.Entries[0].Icon == PhotoIcon.Heart
                && one.Entries.Count(e => e.Icon == PhotoIcon.Heart) == 1 && one.Entries.All(e => e.Pawn != first),
                "one eligible wedding subject receives one Heart while the unavailable subject is omitted");
            second.drafter.Drafted = true;
            DoodlesCheck(PhotoReactions.CreatePlan(wedding, map, PhotoReactions.ModePhoto).Entries.All(e => e.Icon != PhotoIcon.Heart),
                "no eligible wedding subject means no guest inherits a Heart");
            first.drafter.Drafted = false; second.drafter.Drafted = false;
            wedding.eventSubjectIds.Clear();
            DoodlesCheck(PhotoReactions.CreatePlan(wedding, map, PhotoReactions.ModePhoto).Entries.All(e => e.Icon != PhotoIcon.Heart),
                "legacy wedding without verified subject IDs uses neutral reactions");
        }
        private static void DoodlesLogicChecks()
        {
            var rec = DoodlesFixture();
            var first = PhotoReactions.CreatePlan(rec, map, PhotoReactions.ModePhoto);
            var second = PhotoReactions.CreatePlan(rec, map, PhotoReactions.ModePhoto);
            DoodlesCheck(first.Entries.Count > 0 && first.Entries.Count <= 3 && first.Entries.Select(e => e.Pawn).Distinct().Count() == first.Entries.Count,
                "eight-person frame chooses a bounded subset of at most three unique reactions");
            DoodlesCheck(first.Entries.Select(e => e.Pawn.GetUniqueLoadID() + ":" + e.Icon + ":" + e.Delay).SequenceEqual(
                second.Entries.Select(e => e.Pawn.GetUniqueLoadID() + ":" + e.Icon + ":" + e.Delay)), "same moment produces stable reaction choices and timing");
            DoodlesCheck(first.Entries.Select(e => e.Delay).Distinct().Count() == first.Entries.Count, "selected people have staggered reaction starts");
            DoodlesCheck(first.Entries.All(e => e.Icon != PhotoIcon.Paw), "Paw remains a manual album sticker and is not emitted by ordinary photo reactions");
            var candidate = doodlesPeople[0]; DoodlesCheck(PhotoReactions.Eligible(candidate, rec, map), "awake visible recorded free colonist is eligible");
            candidate.drafter.Drafted = true; DoodlesCheck(!PhotoReactions.Eligible(candidate, rec, map), "drafted participant cannot emit a cheerful capture reaction");
            candidate.drafter.Drafted = false;
            candidate.health.AddHediff(HediffDefOf.Anesthetic);
            DoodlesCheck(candidate.Downed && !PhotoReactions.Eligible(candidate, rec, map), "actually downed participant is excluded from capture reactions");
            FixReady(candidate);
            var former = candidate.Position; candidate.Position = center + new IntVec3(22, 0, 0); candidate.Notify_Teleported(true, true);
            DoodlesCheck(!PhotoReactions.Eligible(candidate, rec, map), "recorded person who moved outside the frame is excluded");
            candidate.Position = former; candidate.Notify_Teleported(true, true); candidate.Drawer.tweener.ResetTweenedPosToRoot();
            rec.peopleIds.Remove(candidate.GetUniqueLoadID()); DoodlesCheck(!PhotoReactions.Eligible(candidate, rec, map), "unrecorded nearby colonist is excluded");
            rec.SetPeople(doodlesPeople);
            foreach (string kind in new[] { "battle", "death", "funeral", "disaster", "raid", "manhunter" })
                DoodlesCheck(PhotoReactions.CreatePlan(DoodlesFixture(kind), map, PhotoReactions.ModePhoto).Entries.Count == 0, "negative event has no cheerful reactions: " + kind);
            rec.ticksAbs -= 60000;
            DoodlesCheck(PhotoReactions.CreatePlan(rec, map, PhotoReactions.ModePhoto).Entries.Count == 0, "delayed old event does not produce new cheering");
            if (ModsConfig.BiotechActive)
            {
                var baby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f, allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
                GenSpawn.Spawn(baby, center, map); rec = DoodlesFixture("birthday"); rec.SetPeople(doodlesPeople.Concat(new[] { baby }));
                DoodlesCheck(!PhotoReactions.Eligible(baby, rec, map), "baby does not receive an adult capture reaction");
                rec.requiredPerson = baby.GetUniqueLoadID();
                DoodlesCheck(PhotoReactions.CreatePlan(rec, map, PhotoReactions.ModePhoto).Entries.All(e => e.Pawn != baby),
                    "birthday subject priority never overrides the baby exclusion");
                baby.Destroy(DestroyMode.Vanish);
            }
            else checks.Add("INFO: baby reaction exclusion requires Biotech; Core run does not claim it");
            foreach (PhotoIcon icon in Enum.GetValues(typeof(PhotoIcon)))
            {
                var texture = PhotoIconArt.Texture(icon); var pixels = texture.GetPixels32();
                DoodlesCheck(texture.width == 96 && texture.height == 96 && pixels.Any(p => p.a > 200) && pixels.Any(p => p.a == 0)
                    && ReferenceEquals(texture, PhotoIconArt.Texture(icon)), "cached readable transparent drawn icon: " + icon);
            }
            DoodlesCheck(PhotoEventIcons.ForKind("birthday") == PhotoIcon.Cake && PhotoEventIcons.ForKind("wedding") == PhotoIcon.Rings
                && PhotoEventIcons.ForKind("memorynight") == PhotoIcon.Projector && PhotoEventIcons.ForKind("party", "concert") == PhotoIcon.Music
                && PhotoEventIcons.ForKind("party") == PhotoIcon.Group && PhotoEventIcons.ForKind("death") == PhotoIcon.Camera,
                "event symbols use typed events; older generic party and negative events retain neutral truthful symbols");
            DoodlesCheck(PhotoEventIcons.ForStatus(PhotoParticipantStatus.Ready) == PhotoIcon.Check
                && PhotoEventIcons.ForStatus(PhotoParticipantStatus.Moving) == PhotoIcon.Clock
                && PhotoEventIcons.ForStatus(PhotoParticipantStatus.Sleeping) == PhotoIcon.Sleep
                && PhotoEventIcons.ForStatus(PhotoParticipantStatus.Unavailable) == PhotoIcon.Warning,
                "readiness, movement, sleep and unavailability have distinct code-drawn symbols");
        }
        private static void DoodlesSettingsRoundTrip()
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var path = Path.Combine(dir, "reaction-settings-" + mode + ".xml");
                MomentFramesSettings fixture = new MomentFramesSettings { captureReactionMode = mode };
                Scribe.saver.InitSaving(path, "ReactionSettings"); Scribe_Deep.Look(ref fixture, "settings"); Scribe.saver.FinalizeSaving();
                fixture = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref fixture, "settings"); Scribe.loader.FinalizeLoading();
                DoodlesCheck(fixture.captureReactionMode == mode, "actual Scribe persists reaction selection " + mode);
            }
            var doc = new XmlDocument(); doc.Load(Path.Combine(dir, "reaction-settings-2.xml"));
            var node = doc.SelectSingleNode("/ReactionSettings/settings/captureReactionMode");
            DoodlesCheck(node != null, "nondefault photo reaction mode is present in settings XML"); node.ParentNode.RemoveChild(node);
            var legacy = Path.Combine(dir, "reaction-settings-legacy.xml"); doc.Save(legacy);
            MomentFramesSettings old = null; Scribe.loader.InitLoading(legacy); Scribe_Deep.Look(ref old, "settings"); Scribe.loader.FinalizeLoading();
            DoodlesCheck(old.captureReactionMode == PhotoReactions.ModeAfter, "older settings safely default to live reactions after the shutter");
        }
        private static string DoodlesCaptureLabel(PhotoRecord record, int mode) => record?.kind == Kinds.Birthday
            ? "capture-priority-birthday" : record?.kind == "wedding" ? "capture-priority-wedding" : "capture-mode-" + mode;
        private static void DoodlesStartCapture(int mode, string kind = "group")
        {
            DoodlesPose(); PhotoReactions.Reset(); doodlesMode = mode; doodlesDone = false;
            MomentFramesMod.Settings.captureReactionMode = mode;
            string captureKey = kind == "group" ? "doodles:" + mode : "doodles:priority:" + kind;
            doodlesRecord = PhotoStore.Instance.RequestNow(map, center.ToVector3Shifted(), kind, captureKey,
                kind == Kinds.Birthday ? "생일 주인공의 별 · native priority" : kind == "wedding" ? "결혼 당사자의 하트 · native priority" : "함께 남긴 순간 · capture " + mode,
                "A native photograph of eight staged colonists, with capture reaction mode " + mode,
                doodlesPeople, 12f, 1.5f, () => {
                    doodlesDone = true;
                    DoodlesCheck(mode == PhotoReactions.ModeOff ? PhotoReactions.ActiveCount == 0 : PhotoReactions.ActiveCount > 0 && PhotoReactions.ActiveCount <= 3,
                        "successful mode " + mode + " capture starts only its chosen live effects");
                    if (mode != PhotoReactions.ModeOff && kind == Kinds.Birthday)
                        DoodlesCheck(PhotoReactions.HasActiveFor(doodlesPeople[7]), "actual birthday capture starts its subject's prioritized live reaction");
                    if (mode != PhotoReactions.ModeOff && kind == "wedding")
                        DoodlesCheck(PhotoReactions.HasActiveFor(doodlesPeople[6]) && PhotoReactions.HasActiveFor(doodlesPeople[7]),
                            "actual wedding capture starts live reactions for both verified subjects");
                });
            DoodlesCheck(doodlesRecord != null, "mode " + mode + " starts a real native store capture");
            if (kind == Kinds.Birthday) { doodlesRecord.requiredPerson = doodlesPeople[7].GetUniqueLoadID(); doodlesBirthdayId = doodlesRecord.id; }
            if (kind == "wedding")
            {
                doodlesRecord.eventSubjectIds = new List<string> { doodlesPeople[6].GetUniqueLoadID(), doodlesPeople[7].GetUniqueLoadID() };
                doodlesWeddingId = doodlesRecord.id;
            }
            // The mode belongs to this exposure even if the user changes preferences before its deferred render.
            MomentFramesMod.Settings.captureReactionMode = (mode + 1) % 3;
        }
        private static bool DoodlesSamePixels(byte[] first, byte[] second)
        {
            var a = new Texture2D(2, 2, TextureFormat.RGB24, false); var b = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!a.LoadImage(first) || !b.LoadImage(second) || a.width != b.width || a.height != b.height) return false;
                var x = a.GetPixels32(); var y = b.GetPixels32();
                // A JPG original (the default since 2.6.0) is lossy: the same picture within JPG 98's normal error (a wedding photo: per-pixel
                // largest channel difference mean 1.05, max 6). A missing or different reaction icon differs by far more than 48 where it is.
                bool lossy = PhotoJpeg.IsJpeg(first) || PhotoJpeg.IsJpeg(second);
                int worst = 0; long sum = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    int d = Math.Max(Math.Abs(x[i].r - y[i].r), Math.Max(Math.Abs(x[i].g - y[i].g), Math.Abs(x[i].b - y[i].b)));
                    if (d != 0 && !lossy) return false;
                    worst = Math.Max(worst, d); sum += d;
                }
                return !lossy || worst <= 48 && sum < 4L * x.Length;   // mean below 4
            }
            finally { UnityEngine.Object.Destroy(a); UnityEngine.Object.Destroy(b); }
        }
        private static void DoodlesCheckCapture()
        {
            var store = PhotoStore.Instance;
            DoodlesCheck(doodlesRecord.HasFile && File.Exists(store.OriginalPath(doodlesRecord)), "mode " + doodlesMode + " stored a native original PNG");
            DoodlesCheck(doodlesBakes.TryGetValue(doodlesRecord.id, out var trace) && trace.Mode == doodlesMode, "capture snapshots reaction preference before deferred rendering, mode " + doodlesMode);
            DoodlesCheck(DoodlesSamePixels(File.ReadAllBytes(store.OriginalPath(doodlesRecord)), trace.After), "stored original is the actual post-bake native image, mode " + doodlesMode);
            DoodlesCheck(File.ReadAllBytes(store.OriginalThumbPath(doodlesRecord)).SequenceEqual(trace.Thumb), "thumbnail derives from final baked image, mode " + doodlesMode);
            DoodlesCheck(doodlesMode == PhotoReactions.ModePhoto ? trace.Baked > 0 && trace.Changed > 20 : trace.Baked == 0 && trace.Changed == 0 && trace.Before.SequenceEqual(trace.After),
                "mode " + doodlesMode + " writes " + (doodlesMode == 2 ? "visible selected reaction pixels" : "an entirely clean photograph"));
            if (doodlesRecord.kind == Kinds.Birthday)
                DoodlesCheck(trace.Subjects[0] == doodlesRecord.requiredPerson && trace.Icons[0] == PhotoIcon.Star && trace.Baked > 0,
                    "actual birthday render includes its subject Star after stable identity was attached to the deferred exposure");
            if (doodlesRecord.kind == "wedding")
                DoodlesCheck(new HashSet<string>(trace.Subjects.Take(2)).SetEquals(doodlesRecord.eventSubjectIds)
                    && trace.Icons.Take(2).All(i => i == PhotoIcon.Heart) && trace.Icons.Skip(2).All(i => i != PhotoIcon.Heart) && trace.Baked >= 2,
                    "actual wedding render bakes both verified subject Hearts and never a guest Heart");
            File.Copy(store.OriginalPath(doodlesRecord), Path.Combine(dir, trace.Label + "-stored.png"), true);
            File.Copy(store.OriginalThumbPath(doodlesRecord), Path.Combine(dir, trace.Label + "-thumb.png"), true);
        }
        private static void DoodlesFillStickerDraft()
        {
            DoodlesCheck(AlbumStickers.Palette.Length == 7 && AlbumStickers.Allowed(PhotoIcon.Paw) && AlbumStickers.MaxCount == 6,
                "seven selectable manual sticker types include Paw while saved capacity remains six");
            foreach (var icon in new[] { PhotoIcon.Heart, PhotoIcon.Star, PhotoIcon.Smile, PhotoIcon.Music, PhotoIcon.Wave, PhotoIcon.Paw })
                DoodlesCheck(doodlesEditor.AddSticker(icon), "editor adds manual palette sticker " + icon);
        }
        private static void DoodlesShowEditor(bool dark = false, bool narrow = false)
        {
            Find.MainTabsRoot.EscapeCurrentTab(false); DoodlesAppearance(dark);
            doodlesEditor = new Dialog_AlbumStickers(doodlesRecord); Find.WindowStack.Add(doodlesEditor);
            if (narrow) doodlesEditor.windowRect = new Rect(370f, 12f, 520f, 750f);
        }
        private static void DoodlesCheckSafeMargins()
        {
            DoodlesCheck(doodlesEditor.DrawnFrame.width > 0 && doodlesEditor.DrawnImage.width > 0, "native sticker editor has drawn its paper and image");
            foreach (var sticker in doodlesEditor.Draft)
                DoodlesCheck(AlbumStickers.TryPlacedRect(sticker, doodlesEditor.DrawnFrame, doodlesEditor.DrawnImage, out var placement)
                    && AlbumStickers.WithinMargin(doodlesEditor.DrawnFrame, doodlesEditor.DrawnImage, placement), "sticker stays entirely on paper without covering the photograph");
            foreach (var dimensions in new[] { new Vector2(180f, 180f), new Vector2(500f, 300f), new Vector2(300f, 600f), new Vector2(1000f, 550f) })
            foreach (float aspect in new[] { PhotoCapture.MinAspect, PhotoCapture.DefaultAspect, PhotoCapture.MaxAspect })
            {
                Rect paper = new Rect(3, 5, dimensions.x, dimensions.y), image = AlbumStickers.ImageRect(paper, aspect);
                foreach (var icon in AlbumStickers.Palette)
                    DoodlesCheck(AlbumStickers.TryPlacedRect(new PhotoSticker(icon, .5f, .5f), paper, image, out var rect)
                        && AlbumStickers.WithinMargin(paper, image, rect), "center drag projects to safe paper at " + dimensions + " aspect " + aspect + " icon " + icon);
            }
        }
        private static void DoodlesCheckUnchanged()
        {
            var store = PhotoStore.Instance;
            DoodlesCheck(DoodlesHash(store.OriginalPath(doodlesRecord)) == doodlesOriginalHash
                && DoodlesHash(store.OriginalThumbPath(doodlesRecord)) == doodlesThumbHash, "sticker editing leaves original and thumbnail bytes unchanged");
        }
        private static void DoodlesRecordRoundTrip()
        {
            var path = Path.Combine(dir, "sticker-record.xml"); PhotoRecord fixture = doodlesRecord;
            Scribe.saver.InitSaving(path, "StickerRecord"); Scribe_Deep.Look(ref fixture, "record"); Scribe.saver.FinalizeSaving();
            fixture = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref fixture, "record"); Scribe.loader.FinalizeLoading();
            DoodlesCheck(fixture.stickers.Count == 6 && fixture.stickers.Any(s => s.icon == PhotoIcon.Paw)
                && fixture.stickers.Select((s, i) => s.icon == doodlesRecord.stickers[i].icon
                && Mathf.Approximately(s.x, doodlesRecord.stickers[i].x) && Mathf.Approximately(s.y, doodlesRecord.stickers[i].y)).All(v => v),
                "actual Scribe deep-load retains all six sticker positions and types");
            var xml = new XmlDocument(); xml.Load(path);
            foreach (string field in new[] { "stickers", "eventSubtype", "eventSubjectIds" })
            { var node = xml.SelectSingleNode("/StickerRecord/record/" + field); node?.ParentNode.RemoveChild(node); }
            var legacy = Path.Combine(dir, "sticker-record-legacy.xml"); xml.Save(legacy);
            fixture = null; Scribe.loader.InitLoading(legacy); Scribe_Deep.Look(ref fixture, "record"); Scribe.loader.FinalizeLoading();
            DoodlesCheck(fixture.stickers != null && fixture.stickers.Count == 0 && fixture.eventSubtype == null
                && fixture.eventSubjectIds != null && fixture.eventSubjectIds.Count == 0
                && fixture.id == doodlesRecord.id && fixture.file == doodlesRecord.file, "old photograph without new fields loads cleanly and preserves its identity and file");
            path = Path.Combine(dir, "wedding-subject-record.xml"); fixture = PhotoStore.Instance.Get(doodlesWeddingId);
            var subjects = fixture.eventSubjectIds.ToList();
            Scribe.saver.InitSaving(path, "WeddingSubjects"); Scribe_Deep.Look(ref fixture, "record"); Scribe.saver.FinalizeSaving();
            fixture = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref fixture, "record"); Scribe.loader.FinalizeLoading();
            DoodlesCheck(fixture.eventSubjectIds.SequenceEqual(subjects), "actual Scribe deep-load preserves the exact verified wedding subject IDs");
            xml = new XmlDocument(); xml.Load(path); var subjectNode = xml.SelectSingleNode("/WeddingSubjects/record/eventSubjectIds");
            DoodlesCheck(subjectNode != null, "verified subject IDs are written into wedding record XML"); subjectNode.ParentNode.RemoveChild(subjectNode);
            legacy = Path.Combine(dir, "wedding-subject-record-legacy.xml"); xml.Save(legacy);
            fixture = null; Scribe.loader.InitLoading(legacy); Scribe_Deep.Look(ref fixture, "record"); Scribe.loader.FinalizeLoading();
            DoodlesCheck(fixture.eventSubjectIds != null && fixture.eventSubjectIds.Count == 0
                && PhotoReactions.CreatePlan(fixture, map, PhotoReactions.ModePhoto).Entries.All(e => e.Icon != PhotoIcon.Heart),
                "legacy Scribe wedding without subject IDs loads safely with neutral reactions");
        }
        private static void DoodlesShowGallery(bool dark = false, bool narrow = false)
        {
            DoodlesAppearance(dark); MainTabWindow_Album.Open(doodlesRecord);
            var album = (MainTabWindow_Album)DefDatabase<MainButtonDef>.GetNamed("MF_Album").TabWindow;
            if (narrow) album.windowRect = new Rect(35f, 55f, 1000f, 600f);
        }
        private static void DoodlesShowGroup(bool dark = false, bool narrow = false)
        {
            Find.MainTabsRoot.EscapeCurrentTab(false); DoodlesAppearance(dark);
            doodlesGroup = new Dialog_GroupPhotoSetup(fixesSpot); Find.WindowStack.Add(doodlesGroup);
            if (narrow) doodlesGroup.windowRect = new Rect(370f, 12f, 520f, 750f);
        }
        private static void PhotoDoodlesStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); FixSetup(); map.Biome.constantOutdoorTemperature = 22f;
                    MomentFramesMod.Settings.msColonyStart = false; MomentFramesMod.Settings.birthdayMode = Birthday.ModeOff;
                    doodlesPeople.AddRange(map.mapPawns.FreeColonistsSpawned);
                    while (doodlesPeople.Count < 8)
                    {
                        var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                        GenSpawn.Spawn(pawn, center + new IntVec3(0, 0, -6), map); doodlesPeople.Add(pawn);
                    }
                    doodlesPeople.RemoveRange(8, doodlesPeople.Count - 8); DoodlesPose();
                    doodlesPersonIds.AddRange(doodlesPeople.Select(p => p.GetUniqueLoadID()));
                    fixesSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot")); fixesSpot.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(fixesSpot, center + new IntVec3(0, 0, -5), map);
                    var harmony = new Harmony("momentframes.probe.photo-doodles");
                    harmony.Patch(AccessTools.Method(typeof(PhotoReactions), "Bake"), new HarmonyMethod(typeof(Probe), nameof(DoodlesBeforeBake)), new HarmonyMethod(typeof(Probe), nameof(DoodlesAfterBake)));
                    harmony.Patch(AccessTools.Method(typeof(PhotoCapture), "Render"), new HarmonyMethod(typeof(Probe), nameof(DoodlesMaybeFail)));
                    harmony.Patch(AccessTools.Method(typeof(PhotoEventIcons), "DrawEvent"), postfix: new HarmonyMethod(typeof(Probe), nameof(DoodlesNoteEvent)));
                    harmony.Patch(AccessTools.Method(typeof(PhotoEventIcons), "ForParticipant"), postfix: new HarmonyMethod(typeof(Probe), nameof(DoodlesNoteStatus)));
                    DoodlesLogicChecks(); DoodlesPriorityChecks(); DoodlesSettingsRoundTrip(); Wait(1f); phase = 100; return;
                case 100:
                    DoodlesStartCapture(PhotoReactions.ModeOff); phase = 1; return;
                case 1:
                    if (!doodlesDone || PhotoCapture.Busy) return; DoodlesCheckCapture(); DoodlesStartCapture(PhotoReactions.ModeAfter); phase = 2; return;
                case 2:
                    if (!doodlesDone || PhotoCapture.Busy) return; DoodlesCheckCapture(); Wait(.25f); phase = 101; return;
                case 101:
                    CloseLog(); Shot("reaction-live-after-shutter.png"); phase = 3; return;
                case 3:
                    if (!DoodlesRequire("reaction-live-after-shutter.png")) return;
                    PhotoReactions.Prune(Time.realtimeSinceStartup + 5f); DoodlesCheck(PhotoReactions.ActiveCount == 0, "live reactions expire without advancing paused game ticks");
                    DoodlesStartCapture(PhotoReactions.ModePhoto); phase = 4; return;
                case 4:
                    if (!doodlesDone || PhotoCapture.Busy) return; DoodlesCheckCapture(); Wait(.25f); phase = 102; return;
                case 102:
                    CloseLog(); Shot("reaction-live-photo-mode.png"); phase = 5; return;
                case 5:
                    if (!DoodlesRequire("reaction-live-photo-mode.png")) return;
                    PhotoReactions.Reset(); doodlesRecordId = doodlesRecord.id;
                    DoodlesStartCapture(PhotoReactions.ModePhoto, Kinds.Birthday); phase = 50; return;
                case 50:
                    if (!doodlesDone || PhotoCapture.Busy) return; DoodlesCheckCapture(); Wait(.25f); phase = 51; return;
                case 51:
                    CloseLog(); Shot("reaction-priority-birthday-live.png"); phase = 52; return;
                case 52:
                    if (!DoodlesRequire("reaction-priority-birthday-live.png")) return;
                    DoodlesStartCapture(PhotoReactions.ModePhoto, "wedding"); phase = 53; return;
                case 53:
                    if (!doodlesDone || PhotoCapture.Busy) return; DoodlesCheckCapture(); Wait(.25f); phase = 54; return;
                case 54:
                    CloseLog(); Shot("reaction-priority-wedding-live.png"); phase = 55; return;
                case 55:
                    if (!DoodlesRequire("reaction-priority-wedding-live.png")) return;
                    PhotoReactions.Reset(); doodlesRecord = PhotoStore.Instance.Get(doodlesRecordId);
                    doodlesOriginalHash = DoodlesHash(PhotoStore.Instance.OriginalPath(doodlesRecord)); doodlesThumbHash = DoodlesHash(PhotoStore.Instance.OriginalThumbPath(doodlesRecord));
                    DoodlesShowEditor(); Wait(.5f); phase = 6; return;
                case 6:
                    DoodlesFillStickerDraft();
                    DoodlesCheck(!doodlesEditor.AddSticker(PhotoIcon.Heart) && doodlesRecord.stickers.Count == 0, "seventh sticker is refused and editing remains a detached draft");
                    DoodlesCheck(doodlesEditor.MoveSticker(0, doodlesEditor.DrawnImage.center), "dragging toward a face moves the draft to its nearest safe margin");
                    DoodlesCheckSafeMargins(); CloseLog(); Shot("stickers-editor-graphite-130.png"); phase = 7; return;
                case 7:
                    if (!DoodlesRequire("stickers-editor-graphite-130.png")) return;
                    doodlesEditor.CancelDraft(); DoodlesCheck(doodlesRecord.stickers.Count == 0, "cancel discards all unsaved stickers");
                    DoodlesCheckUnchanged(); DoodlesShowEditor(); DoodlesFillStickerDraft(); Wait(.4f); phase = 8; return;
                case 8:
                    int version = PhotoStore.Instance.Version;
                    DoodlesCheck(doodlesEditor.SaveDraft() && doodlesRecord.stickers.Count == 6 && PhotoStore.Instance.Version > version, "save commits exactly six metadata decorations and invalidates the album cache");
                    DoodlesCheck(!AlbumStickers.Commit(doodlesRecord, AlbumStickers.Copy(doodlesRecord.stickers)), "saving an unchanged sticker draft does not notify or churn the registry");
                    DoodlesCheckUnchanged(); DoodlesRecordRoundTrip();
                    var crop = PhotoStore.Instance.SaveEdit(doodlesRecord, new Rect(.1f, .1f, .6f, .8f), PhotoFilters.Sepia);
                    DoodlesCheck(crop != null && crop.aspect != doodlesRecord.Aspect, "native cropped alternate version has a different actual image aspect");
                    doodlesEditId = crop.id; doodlesEditHash = DoodlesHash(PhotoStore.Instance.EditPath(crop));
                    PhotoStore.Instance.SetShown(doodlesRecord, null);
                    doodlesEditor = new Dialog_AlbumStickers(doodlesRecord, doodlesEditId); Find.WindowStack.Add(doodlesEditor);
                    Wait(.5f); phase = 81; return;
                case 81:
                    DoodlesCheck(doodlesEditor.HeldPath == PhotoStore.Instance.VersionPath(doodlesRecord, doodlesEditId)
                        && Mathf.Abs(doodlesEditor.DrawnImage.width / doodlesEditor.DrawnImage.height - doodlesRecord.Edit(doodlesEditId).aspect) < .001f,
                        "editor honors explicitly viewed crop path and aspect while the album representative remains the original");
                    DoodlesCheckSafeMargins(); CloseLog(); Shot("stickers-editor-alternate-crop-130.png"); phase = 82; return;
                case 82:
                    if (!DoodlesRequire("stickers-editor-alternate-crop-130.png")) return;
                    doodlesEditor.CancelDraft(); DoodlesCheck(doodlesRecord.Shown == null
                        && DoodlesHash(PhotoStore.Instance.EditPath(doodlesRecord.Edit(doodlesEditId))) == doodlesEditHash,
                        "closing alternate-version decoration preserves the cropped PNG and the original representative choice");
                    DoodlesCheckUnchanged(); DoodlesShowEditor(false, true); Wait(.5f); phase = 9; return;
                case 9:
                    DoodlesCheckSafeMargins(); CloseLog(); Shot("stickers-editor-narrow-130.png"); phase = 10; return;
                case 10:
                    if (!DoodlesRequire("stickers-editor-narrow-130.png")) return; doodlesEditor.CancelDraft(); DoodlesShowEditor(true); Wait(.5f); phase = 11; return;
                case 11:
                    DoodlesCheckSafeMargins(); DoodlesCheck(doodlesEditor.SelectSticker(5) && doodlesEditor.RemoveSelected() && doodlesEditor.Draft.Count == 5
                        && doodlesRecord.stickers.Count == 6, "delete acts on selected draft and preserves saved decorations until save");
                    DoodlesCheck(doodlesEditor.AddSticker(PhotoIcon.Paw) && doodlesEditor.Draft.Any(s => s.icon == PhotoIcon.Paw),
                        "Paw is selectable again after removing a draft sticker"); CloseLog(); Shot("stickers-editor-darkroom-130.png"); phase = 12; return;
                case 12:
                    if (!DoodlesRequire("stickers-editor-darkroom-130.png")) return; doodlesEditor.CancelDraft(); DoodlesShowGallery(); Wait(.5f); phase = 13; return;
                case 13:
                    DoodlesCheck(doodlesEventDraws > 0, "native album invokes the typed event icon renderer");
                    new Dialog_PhotoEdit(doodlesRecord).OpenStickers();
                    var stickerWindow = Find.WindowStack.WindowOfType<Dialog_AlbumStickers>();
                    DoodlesCheck(stickerWindow != null, "the edit window opens the sticker editor");
                    stickerWindow.Close(false);
                    CloseLog(); Shot("stickers-gallery-graphite-130.png"); phase = 14; return;
                case 14:
                    if (!DoodlesRequire("stickers-gallery-graphite-130.png")) return; DoodlesShowGallery(true, true); Wait(.5f); phase = 15; return;
                case 15:
                    CloseLog(); Shot("stickers-gallery-darkroom-min-130.png"); phase = 16; return;
                case 16:
                    if (!DoodlesRequire("stickers-gallery-darkroom-min-130.png")) return;
                    Find.MainTabsRoot.EscapeCurrentTab(false); doodlesSleeper = doodlesPeople[7];
                    doodlesBed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot")); doodlesBed.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(doodlesBed, doodlesSleeper.Position, map); doodlesBed.CompAssignableToPawn.TryAssignPawn(doodlesSleeper);
                    doodlesSleeper.needs.rest.CurLevel = .05f; doodlesSleeper.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, doodlesBed), JobCondition.InterruptForced);
                    for (int i = 0; i < 90; i++) Find.TickManager.DoSingleTick(); SettleDraw(); Wait(.2f); phase = 17; return;
                case 17:
                    DoodlesCheck(!doodlesSleeper.Awake() && !PhotoReactions.Eligible(doodlesSleeper, DoodlesFixture(), map), "sleeping native bed occupant never emits a capture reaction");
                    var sleepingBirthday = DoodlesFixture(Kinds.Birthday); sleepingBirthday.requiredPerson = doodlesSleeper.GetUniqueLoadID();
                    DoodlesCheck(PhotoReactions.CreatePlan(sleepingBirthday, map, PhotoReactions.ModePhoto).Entries.All(e => e.Pawn != doodlesSleeper),
                        "birthday Star priority never forces a sleeping subject to react");
                    DoodlesCheck(PhotoEventIcons.ForParticipant(doodlesSleeper, false, false) == PhotoIcon.Sleep, "sleeping person retains sleep symbol even when unavailable");
                    doodlesPeople[6].drafter.Drafted = true; DoodlesShowGroup(); Wait(.5f); phase = 18; return;
                case 18:
                    DoodlesCheck(doodlesStatusCalls > 0, "native preparation roster invokes actual participant status icons");
                    CloseLog(); Shot("icons-roster-graphite-130.png"); phase = 19; return;
                case 19:
                    if (!DoodlesRequire("icons-roster-graphite-130.png")) return; Find.WindowStack.TryRemove(typeof(Dialog_GroupPhotoSetup), false); DoodlesShowGroup(true, true); Wait(.5f); phase = 20; return;
                case 20:
                    CloseLog(); Shot("icons-roster-darkroom-narrow-130.png"); phase = 21; return;
                case 21:
                    if (!DoodlesRequire("icons-roster-darkroom-narrow-130.png")) return;
                    Find.WindowStack.TryRemove(typeof(Dialog_GroupPhotoSetup), false); DoodlesPose();
                    PhotoReactions.NotifySaved(PhotoReactions.CreatePlan(DoodlesFixture(), map, PhotoReactions.ModeAfter));
                    DoodlesCheck(PhotoReactions.ActiveCount > 0, "ephemeral fixture can start visible live reactions");
                    GameDataSaveLoader.SaveGame("mf-photo-doodles"); GameDataSaveLoader.LoadGame("mf-photo-doodles"); Wait(1f); phase = 22; return;
                case 22:
                    map = Find.CurrentMap; doodlesRecord = PhotoStore.Instance.Get(doodlesRecordId);
                    doodlesPeople.Clear(); foreach (var id in doodlesPersonIds) doodlesPeople.Add(map.mapPawns.FreeColonistsSpawned.First(p => p.GetUniqueLoadID() == id));
                    DoodlesCheck(doodlesRecord?.stickers?.Count == 6 && doodlesRecord.stickers.Any(s => s.icon == PhotoIcon.Paw)
                        && PhotoReactions.ActiveCount == 0, "actual save/load restores manual Paw decoration and never replays transient reactions");
                    DoodlesCheck(PhotoStore.Instance.Get(doodlesBirthdayId)?.requiredPerson == doodlesPeople[7].GetUniqueLoadID()
                        && PhotoStore.Instance.Get(doodlesWeddingId)?.eventSubjectIds.SequenceEqual(new[] { doodlesPeople[6].GetUniqueLoadID(), doodlesPeople[7].GetUniqueLoadID() }) == true,
                        "actual game save/load retains birthday subject and exact wedding pair IDs");
                    DoodlesCheckUnchanged(); PhotoReactions.Update();
                    DoodlesCheck(PhotoReactions.ActiveCount == 0, "first observation after load does not synthesize reactions from stored photos");
                    DoodlesPose(); PhotoReactions.Reset(); doodlesFailCapture = true; doodlesDone = false; MomentFramesMod.Settings.captureReactionMode = PhotoReactions.ModeAfter;
                    PhotoStore.Instance.RequestNow(map, center.ToVector3Shifted(), "group", "doodles:fail", "Failed disposable capture", "", doodlesPeople, 12f, 1.5f, () => doodlesDone = true);
                    phase = 23; return;
                case 23:
                    if (!doodlesDone || PhotoCapture.Busy) return;
                    DoodlesCheck(!PhotoStore.Instance.HasKey("doodles:fail") && PhotoReactions.ActiveCount == 0, "failed native exposure stores no photograph and plays no live reactions");
                    PhotoReactions.NotifySaved(PhotoReactions.CreatePlan(DoodlesFixture(), map, PhotoReactions.ModeAfter));
                    DoodlesCheck(PhotoReactions.ActiveCount > 0, "map-exit fixture starts reactions before leaving");
                    Current.Game.CurrentMap = null; PhotoReactions.Update(); Current.Game.CurrentMap = map; PhotoReactions.Update();
                    DoodlesCheck(PhotoReactions.ActiveCount == 0, "leaving and returning to a map clears transient reactions without replay");
                    File.WriteAllText(Path.Combine(dir, "photo-doodles-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Marked disposable native fixtures, staged pawns and explicit settings/Scribe tests. Tagged birthday and wedding capture records validate rendering and subject IDs; natural event-hook firing is not asserted. Original user saves, natural player timing and full modpack compatibility remain untested. Glyphs are code-drawn; no AI or emotional inference is used.\"}");
                    active = false; Application.Quit(); return;
            }
        }
    }
}
