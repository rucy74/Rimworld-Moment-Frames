using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Profiling;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool framePhotoQualityMode;
        private static PhotoRecord fqPhoto, fqSmall, fqAlpha;
        private static readonly FramePhotoBinding fqBinding = new FramePhotoBinding(), fqPeer = new FramePhotoBinding();
        private static FramePhotoTextures.Entry fqEntry;
        private static int fqChoice, fqRepeat, fqUploads, fqWait;
        private static double fqBindMs;
        private static string fqOriginalHash;
        private static readonly int[] fqHeights = { 256, 512, 1024 };
        private static readonly System.Collections.Generic.List<string> fqMeasures = new System.Collections.Generic.List<string>();
        private static void FqCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("frame quality: " + label); checks.Add("PASS: " + label); }
        private static string FqHash(string path)
        { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))); }
        private static PhotoRecord FqRecord(string name, int w, int h, bool alpha = false)
        {
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)((i / 61 + i % 37) % 256);
            var pixels = new PhotoPixels(w, h, rgb);
            var store = PhotoStore.Instance;
            var record = UiRecord(store, "frame-quality-" + name, Kinds.Test, "frame-quality-" + name + ".png", 0);
            byte[] bytes = pixels.Encode();
            if (alpha)
            {
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var colors = new Color32[w * h];
                for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(80, 140, 220, (byte)(i % 256));
                t.SetPixels32(colors); t.Apply(); bytes = t.EncodeToPNG(); UnityEngine.Object.Destroy(t);
            }
            File.WriteAllBytes(store.FilePath(record), bytes);
            File.WriteAllBytes(store.ThumbPath(record), pixels.Resize(FramePhotoTextures.SideFor(w, h, 256)).Encode());
            return record;
        }
        private static void FqSettings()
        {
            var item = SettingsCatalog.Entries.Single(e => e.Id == "framePhotoHeight");
            FqCheck(item.Kind == SettingsCatalog.SettingKind.Select && item.InlineOptions && item.Options.Select(o => (int)o.Value).SequenceEqual(fqHeights), "exactly three inline options, no slider");
            FqCheck(new MomentFramesSettings().framePhotoHeight == 512 && MomentFramesMod.Settings.framePhotoHeight == 512, "new and missing user settings default to 512");
            foreach (int h in fqHeights)
            {
                var settings = new MomentFramesSettings(); item.Set(settings, h);
                string path = Path.Combine(dir, "settings-" + h + ".xml");
                Scribe.saver.InitSaving(path, "FrameQuality"); Scribe_Deep.Look(ref settings, "settings"); Scribe.saver.FinalizeSaving();
                settings = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref settings, "settings"); Scribe.loader.FinalizeLoading();
                FqCheck(settings.framePhotoHeight == h, "native Scribe save/load " + h);
            }
            var legacy = new XmlDocument(); legacy.Load(Path.Combine(dir, "settings-1024.xml"));
            var field = legacy.SelectSingleNode("/FrameQuality/settings/framePhotoHeight"); field.ParentNode.RemoveChild(field);
            string old = Path.Combine(dir, "settings-legacy.xml"); legacy.Save(old);
            MomentFramesSettings previous = null; Scribe.loader.InitLoading(old); Scribe_Deep.Look(ref previous, "settings"); Scribe.loader.FinalizeLoading();
            FqCheck(previous.framePhotoHeight == 512 && FramePhotoTextures.Normalize(999) == 512, "legacy and invalid quality safely use 512");
            // Positive control: this detector must reject a deliberate wrong dimension.
            var wrong = new Texture2D(5, 7);
            FqCheck(!FqSize(wrong, 6, 7), "dimension mismatch positive control"); UnityEngine.Object.Destroy(wrong);
        }
        private static bool FqSize(Texture texture, int w, int h) => texture != null && texture.width == w && texture.height == h;
        private static void FramePhotoQualityStep()
        {
            if (phase == 0)
            {
                map = Find.CurrentMap; center = map.Center; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                MomentFramesMod.Settings.autoCaptureEnabled = false;
                Directory.CreateDirectory(PhotoStore.Instance.FolderPath); FqSettings();
                fqPhoto = FqRecord("rgb", 3072, 2048); fqSmall = FqRecord("small", 300, 200); fqAlpha = FqRecord("alpha", 900, 600, true);
                fqOriginalHash = FqHash(PhotoStore.Instance.FilePath(fqPhoto));
                fqMeasures.Add("height\trepeat\twidth\tactualHeight\tnativeTextureBytes\tbudgetBytes\tbindColdMs\tworkerReadDecodeResizeMs\tmainUploadMs\twarmBindingUs");
                phase = 1; return;
            }
            if (phase == 1)
            {
                fqBinding.Unbind(); fqPeer.Unbind(); FramePhotoTextures.Release(fqEntry); fqEntry = null; PhotoTextureCache.Clear();
                int h = fqHeights[fqChoice]; MomentFramesMod.Settings.framePhotoHeight = h;
                var clock = Stopwatch.StartNew(); fqBinding.Bind(fqPhoto); fqBindMs = clock.Elapsed.TotalMilliseconds;
                if (h > 256) fqEntry = FramePhotoTextures.Acquire(PhotoStore.Instance.FilePath(fqPhoto), h);
                phase = 2; return;
            }
            if (phase == 2)
            {
                fqBinding.Bind(fqPhoto); if (fqEntry != null && !fqEntry.Finished) return;
                int h = fqHeights[fqChoice]; var texture = fqBinding.Mat?.mainTexture;
                FqCheck(FqSize(texture, h * 3 / 2, h), "native displayed dimensions " + h + " repeat " + fqRepeat);
                int uploads = FramePhotoTextures.Uploads;
                fqPeer.Bind(fqPhoto); FqCheck(ReferenceEquals(fqBinding.Mat, fqPeer.Mat), "same photograph shares material/texture " + h);
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 10000; i++) fqBinding.Bind(fqPhoto);
                double micros = clock.Elapsed.TotalMilliseconds * 1000 / 10000;
                FqCheck(uploads == FramePhotoTextures.Uploads, "10000 warm binds perform no image upload " + h);
                fqMeasures.Add(string.Join("\t", new object[] { h, fqRepeat, texture.width, texture.height, Profiler.GetRuntimeMemorySizeLong(texture), (long)texture.width * texture.height * 6,
                    fqBindMs.ToString("F3", CultureInfo.InvariantCulture), (fqEntry?.DecodeMs ?? 0).ToString("F3", CultureInfo.InvariantCulture),
                    (fqEntry?.UploadMs ?? 0).ToString("F3", CultureInfo.InvariantCulture), micros.ToString("F3", CultureInfo.InvariantCulture) }));
                if (++fqRepeat < 3) { phase = 1; return; }
                fqRepeat = 0; if (++fqChoice < 3) { phase = 1; return; }
                File.WriteAllLines(Path.Combine(dir, "frame-quality-cost.tsv"), fqMeasures);
                fqBinding.Unbind(); fqPeer.Unbind(); FramePhotoTextures.Release(fqEntry); fqEntry = null;
                fqBinding.Bind(fqSmall); phase = 3; return;
            }
            if (phase == 3)
            {
                fqBinding.Bind(fqSmall); if (fqBinding.BoundPath != PhotoStore.Instance.FilePath(fqSmall)) return;
                FqCheck(FqSize(fqBinding.Mat.mainTexture, 300, 200), "1024 never enlarges smaller original");
                fqBinding.Bind(fqAlpha); phase = 4; return;
            }
            if (phase == 4)
            {
                fqBinding.Bind(fqAlpha); if (fqBinding.BoundPath != PhotoStore.Instance.FilePath(fqAlpha)) return;
                FqCheck(FqSize(fqBinding.Mat.mainTexture, 900, 600) && ((Texture2D)fqBinding.Mat.mainTexture).format == TextureFormat.RGBA32, "imported RGBA preserves size and alpha format");
                fqBinding.Bind(fqPhoto); phase = 5; return;
            }
            if (phase == 5)
            {
                fqBinding.Bind(fqPhoto); if (fqBinding.BoundPath != PhotoStore.Instance.FilePath(fqPhoto)) return;
                var old = fqBinding.Mat; PhotoTextureCache.Forget(PhotoStore.Instance.FilePath(fqPhoto)); fqBinding.Bind(fqPhoto);
                FqCheck(!ReferenceEquals(old, fqBinding.Mat), "explicit file invalidation releases old display");
                PhotoTextureCache.Clear(); fqBinding.Unbind(); FramePhotoTextures.Update();
                FqCheck(FramePhotoTextures.Count == 0 && FramePhotoTextures.Bytes == 0, "reset invalidates in-flight cache and clears resident entries");
                MomentFramesMod.Settings.framePhotoHeight = 512; fqBinding.Bind(fqPhoto); phase = 6; return;
            }
            if (phase == 6)
            {
                fqBinding.Bind(fqPhoto); if (fqBinding.BoundPath != PhotoStore.Instance.FilePath(fqPhoto)) return;
                FqCheck(FqSize(fqBinding.Mat.mainTexture, 768, 512), "pending old 1024 load cannot replace new 512 after reset");
                frame = (Building_PhotoFrame)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MF_PhotoFrame"), ThingDefOf.WoodLog);
                GenSpawn.Spawn(frame, center, map); frame.SetPhoto(fqPhoto.id); frame.RefreshBinding();
                FqCheck(FqSize(frame.BoundTexture, 768, 512), "actual frame building binds chosen quality");
                MomentFramesMod.Settings.framePhotoHeight = 256; frame.RefreshBinding();
                FqCheck(FqSize(frame.BoundTexture, 384, 256) && FramePhotoTextures.Count == 0, "live 512 to 256 switch frees old-resolution cache");
                MomentFramesMod.Settings.framePhotoHeight = 512; frame.RefreshBinding(); fqBinding.Bind(fqPhoto);
                phase = 9; return;
            }
            if (phase == 9)
            {
                frame.RefreshBinding(); fqBinding.Bind(fqPhoto); if (!FqSize(frame.BoundTexture, 768, 512)) return;
                FqCheck(ReferenceEquals(frame.BoundTexture, fqBinding.Mat.mainTexture), "live 256 to 512 switch reuses shared high-quality texture");
                var collage = (Building_PhotoCollage)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MF_PhotoCollage"), ThingDefOf.WoodLog);
                GenSpawn.Spawn(collage, center + new IntVec3(4, 0, 0), map);
                collage.SetSlot(0, fqPhoto.id); collage.SetSlot(1, fqPhoto.id); collage.RefreshBinding();
                FqCheck(ReferenceEquals(collage.SlotTexture(0), frame.BoundTexture) && ReferenceEquals(collage.SlotTexture(1), frame.BoundTexture), "collage slots share chosen-resolution frame texture"); collage.DeSpawn();
                var memorial = (Building_PhotoFrame)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MF_MemorialFrame"), ThingDefOf.WoodLog);
                GenSpawn.Spawn(memorial, center + new IntVec3(-4, 0, 0), map); memorial.SetPhoto(fqPhoto.id); memorial.RefreshBinding();
                FqCheck(ReferenceEquals(memorial.BoundTexture, frame.BoundTexture), "memorial display shares chosen-resolution texture"); memorial.DeSpawn();
                var tree = new PhotoBinding(); tree.Bind(fqPhoto, true);
                FqCheck(FqSize(tree.Mat.mainTexture, 384, 256), "thumbnail-only display remains 256"); tree.Unbind();
                FqCheck(FqHash(PhotoStore.Instance.FilePath(fqPhoto)) == fqOriginalHash, "original photo bytes unchanged");
                fqUploads = FramePhotoTextures.Uploads;
                Find.WindowStack.Add(new FrameQualityProbeWindow()); fqWait = Time.frameCount + 15; phase = 7; return;
            }
            if (phase == 7)
            {
                if (Time.frameCount < fqWait) return;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "frame-quality-settings.png"));
                fqWait = Time.frameCount + 5; phase = 8; return;
            }
            if (phase == 8)
            {
                if (Time.frameCount < fqWait) return;
                FqCheck(FramePhotoTextures.Uploads == fqUploads, "idle frames do not reload chosen texture");
                File.WriteAllText(Path.Combine(dir, "result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json)) + "],\"limitations\":\"Synthetic RGB 3072x2048; three cold texture-cache repetitions with OS file cache warm. Native texture allocation and CPU load/binding timings; not an FPS/TPS benchmark or full mod-pack test.\"}");
                fqBinding.Unbind(); frame.DeSpawn(); PhotoTextureCache.Clear(); active = false; Application.Quit();
            }
        }
    }
    internal sealed class FrameQualityProbeWindow : Window
    {
        private readonly SettingsWindow renderer = new SettingsWindow();
        public override Vector2 InitialSize => new Vector2(940, 680);
        internal FrameQualityProbeWindow() { renderer.SetSearch("MF_Settings_Label_framePhotoHeight".Translate()); doCloseX = true; }
        public override void DoWindowContents(Rect rect) => renderer.Draw(rect, MomentFramesMod.Settings);
    }
}
