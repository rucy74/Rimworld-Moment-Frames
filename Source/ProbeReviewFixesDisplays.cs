using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Entered only from the review-fixes probe's marked disposable profile.
    public static partial class Probe
    {
        private static int reviewDisplayPhase, reviewDisplayFrame, reviewScreenWidth, reviewScreenHeight;
        private static float reviewDisplayAt;
        private static string reviewSavedTheme, reviewSavedFont;
        private static int reviewSavedTextSize;
        private static readonly List<PhotoRecord> reviewRecoveryRecords = new List<PhotoRecord>();
        private static readonly List<PhotoBinding> reviewRecoveryBindings = new List<PhotoBinding>();
        private static readonly List<PhotoRecord> reviewAnnualRecords = new List<PhotoRecord>();
        private static PhotoBinding reviewHealthy, reviewHealthyPeer;
        private static Material reviewHealthyMaterial;
        private static string reviewHealthyPath;
        private static Dialog_MFYearPhotos reviewAnnual;
        private static Rect reviewAnnualFooter;

        private static void ReviewDisplayCheck(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("review display fixes: " + message);
            checks.Add("PASS: review display fixes — " + message);
        }

        private static PhotoRecord ReviewDisplayRecord(PhotoRecord original, string label) => new PhotoRecord
        {
            id = Guid.NewGuid().ToString("N"), file = "review-" + label + "-" + Guid.NewGuid().ToString("N") + ".png",
            kind = Kinds.Test, title = label, desc = "Disposable native display fixture", year = original.year,
            ticksAbs = original.ticksAbs, dateLabel = original.dateLabel, mapId = original.mapId,
            center = original.center, spanCells = original.spanCells, aspect = original.Aspect
        };

        private static void ReviewChronicleStoreIdentity(PhotoRecord original)
        {
            var game = Current.Game; var source = PhotoStore.Instance;
            var person = map.mapPawns.FreeColonistsSpawned.First();
            var firstPhoto = ReviewDisplayRecord(original, "chronicle-store-one"); firstPhoto.SetPeople(new[] { person });
            var secondPhoto = ReviewDisplayRecord(original, "chronicle-store-two"); secondPhoto.SetPeople(new[] { person });
            var firstLife = new LifeEvent { kind = LifeEvents.Joined, pawnIds = new List<string> { person.GetUniqueLoadID() },
                names = new List<string> { person.LabelShort }, year = original.year, ticksAbs = original.ticksAbs - 2 };
            var secondLife = new LifeEvent { kind = LifeEvents.Bond, pawnIds = new List<string> { person.GetUniqueLoadID() },
                names = new List<string> { person.LabelShort }, year = original.year, ticksAbs = original.ticksAbs - 1 };
            var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            var first = (PhotoStore)clone.Invoke(source, null); var second = (PhotoStore)clone.Invoke(source, null);
            AccessTools.Field(typeof(PhotoStore), "records").SetValue(first, new List<PhotoRecord> { firstPhoto });
            AccessTools.Field(typeof(PhotoStore), "records").SetValue(second, new List<PhotoRecord> { secondPhoto });
            AccessTools.Field(typeof(PhotoStore), "lifeEvents").SetValue(first, new List<LifeEvent> { firstLife });
            AccessTools.Field(typeof(PhotoStore), "lifeEvents").SetValue(second, new List<LifeEvent> { secondLife });
            var tab = (ITab_Pawn_Chronicle)InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_Chronicle));
            var rows = AccessTools.Method(typeof(ITab_Pawn_Chronicle), "RowsOf");
            int index = game.components.IndexOf(source);
            try
            {
                ReviewDisplayCheck(index >= 0 && first.Version == second.Version && !ReferenceEquals(first, second),
                    "chronicle fixture has two distinct stores with identical Version and the same real pawn ID");
                game.components[index] = first;
                var firstRows = ((IEnumerable)rows.Invoke(tab, new object[] { person, false })).Cast<object>().ToList();
                ReviewDisplayCheck(firstRows.Count == 2 && firstRows.Any(row => ReferenceEquals(AccessTools.Field(row.GetType(), "rec").GetValue(row), firstPhoto))
                    && firstRows.Any(row => ReferenceEquals(AccessTools.Field(row.GetType(), "life").GetValue(row), firstLife)),
                    "shared native chronicle tab first contains only the first store photo and life event");
                game.components[index] = second;
                var secondRows = ((IEnumerable)rows.Invoke(tab, new object[] { person, false })).Cast<object>().ToList();
                ReviewDisplayCheck(secondRows.Count == 2 && secondRows.Any(row => ReferenceEquals(AccessTools.Field(row.GetType(), "rec").GetValue(row), secondPhoto))
                    && secondRows.Any(row => ReferenceEquals(AccessTools.Field(row.GetType(), "life").GetValue(row), secondLife))
                    && !secondRows.Any(row => ReferenceEquals(AccessTools.Field(row.GetType(), "rec").GetValue(row), firstPhoto)
                        || ReferenceEquals(AccessTools.Field(row.GetType(), "life").GetValue(row), firstLife)),
                    "identical Version and pawn ID rebuild chronicle rows after store identity changes");
            }
            finally { if (index >= 0) game.components[index] = source; }
            checks.Add("INFO: chronicle test temporarily swaps controlled cloned native stores; it does not claim a complete game save/load test.");
        }

        private static void ReviewBeginRecovery(PhotoRecord original)
        {
            var store = PhotoStore.Instance;
            byte[] full = File.ReadAllBytes(store.FilePath(original));
            byte[] thumb = File.ReadAllBytes(store.ThumbPath(original));
            foreach (string label in new[] { "restore-original-only", "restore-thumb-only", "restore-both" })
            {
                var record = ReviewDisplayRecord(original, label); var binding = new PhotoBinding();
                ReviewDisplayCheck(!File.Exists(store.FilePath(record)) && !File.Exists(store.ThumbPath(record))
                    && PhotoTextureCache.Thumb(store.ThumbPath(record), store.FilePath(record)) == null,
                    label + " begins with both missing paths observed by the real thumbnail cache");
                binding.Bind(record, true);
                ReviewDisplayCheck(binding.Mat == null, label + " binds a missing file without a material");
                reviewRecoveryRecords.Add(record); reviewRecoveryBindings.Add(binding);
            }
            // Each fixture has a fresh filename. No original or existing thumbnail is moved, deleted, or overwritten.
            File.WriteAllBytes(store.FilePath(reviewRecoveryRecords[0]), full);
            File.WriteAllBytes(store.ThumbPath(reviewRecoveryRecords[1]), thumb);
            File.WriteAllBytes(store.FilePath(reviewRecoveryRecords[2]), full);
            File.WriteAllBytes(store.ThumbPath(reviewRecoveryRecords[2]), thumb);
            var healthy = ReviewDisplayRecord(original, "healthy-shared-holder");
            File.WriteAllBytes(store.FilePath(healthy), full); File.WriteAllBytes(store.ThumbPath(healthy), thumb);
            reviewHealthy = new PhotoBinding(); reviewHealthyPeer = new PhotoBinding();
            reviewHealthy.Bind(healthy); reviewHealthyPeer.Bind(healthy);
            reviewHealthyMaterial = reviewHealthy.Mat; reviewHealthyPath = reviewHealthy.BoundPath;
            ReviewDisplayCheck(reviewHealthyMaterial != null && ReferenceEquals(reviewHealthyMaterial, reviewHealthyPeer.Mat),
                "independent healthy holders share a material before missing-path retries");
            reviewDisplayAt = Time.realtimeSinceStartup; reviewDisplayFrame = Time.frameCount;
        }

        private static void ReviewShowAnnual(PhotoRecord original)
        {
            var store = PhotoStore.Instance;
            for (int i = 0; i < 3; i++)
            {
                var rec = ReviewDisplayRecord(original, "annual-print-" + i);
                rec.file = original.file; rec.kind = "review-print-" + i;
                rec.title = "정착지에서 함께 기억하는 한 해의 순간 " + (i + 1);
                ((List<PhotoRecord>)store.Records).Add(rec); reviewAnnualRecords.Add(rec);
            }
            store.NotifyChanged();
            Find.MainTabsRoot.EscapeCurrentTab(false);
            var letter = DesignLetter(original.year); letter.year = original.year;
            Find.LetterStack.ReceiveLetter(letter);
            reviewAnnual = new Dialog_MFYearPhotos(letter); Find.WindowStack.Add(reviewAnnual);
            reviewDisplayAt = Time.realtimeSinceStartup;
        }

        private static bool ReviewScreenshotReady(string name)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return true;
            if (Time.realtimeSinceStartup - reviewDisplayAt > 15f) throw new TimeoutException("review display screenshot " + name);
            return false;
        }

        private static bool ReviewDisplaysStep(PhotoRecord original)
        {
            switch (reviewDisplayPhase)
            {
                case 0:
                    ReviewChronicleStoreIdentity(original); ReviewBeginRecovery(original); reviewDisplayPhase = 1; return false;
                case 1:
                    for (int i = 0; i < reviewRecoveryBindings.Count; i++) reviewRecoveryBindings[i].Bind(reviewRecoveryRecords[i], true);
                    if (reviewRecoveryBindings.Any(b => b.Mat == null) || Time.realtimeSinceStartup - reviewDisplayAt < 2.2f || Time.frameCount - reviewDisplayFrame < 125)
                    {
                        if (Time.realtimeSinceStartup - reviewDisplayAt > 15f) throw new TimeoutException("restored images did not recover through native binding retries");
                        return false;
                    }
                    var store = PhotoStore.Instance;
                    for (int i = 0; i < reviewRecoveryBindings.Count; i++)
                        ReviewDisplayCheck(reviewRecoveryBindings[i].Mat != null && reviewRecoveryBindings[i].BoundPath == store.ThumbPath(reviewRecoveryRecords[i]),
                            reviewRecoveryRecords[i].title + " becomes a real bound thumbnail after the missing TTL and frame retry");
                    ReviewDisplayCheck(File.Exists(store.ThumbPath(reviewRecoveryRecords[0])) && !File.Exists(store.FilePath(reviewRecoveryRecords[1])),
                        "original-only recovery makes its thumbnail and thumbnail-only recovery does not require the original");
                    reviewHealthyPeer.Unbind();
                    ReviewDisplayCheck(ReferenceEquals(reviewHealthy.Mat, reviewHealthyMaterial) && PhotoTextureCache.Owns(reviewHealthyPath, reviewHealthyMaterial),
                        "missing-path expiration leaves another holder's live material and remaining reference intact");
                    reviewHealthy.Unbind();
                    ReviewDisplayCheck(!PhotoTextureCache.IsCached(reviewHealthyPath), "last healthy holder release removes its material normally after retries");
                    foreach (var binding in reviewRecoveryBindings) binding.Unbind();
                    reviewScreenWidth = Screen.width; reviewScreenHeight = Screen.height;
                    reviewSavedTheme = MomentFramesMod.Settings.albumTheme; reviewSavedFont = MomentFramesMod.Settings.albumFont;
                    reviewSavedTextSize = MomentFramesMod.Settings.albumTextSize;
                    MomentFramesMod.Settings.albumTheme = "graphite"; MomentFramesMod.Settings.albumFont = "pencil"; MomentFramesMod.Settings.albumTextSize = 130;
                    Screen.SetResolution(1000, 600, false); reviewDisplayAt = Time.realtimeSinceStartup; reviewDisplayPhase = 2; return false;
                case 2:
                    if (UI.screenWidth > 1000 || UI.screenHeight > 600 || Time.realtimeSinceStartup - reviewDisplayAt < .3f)
                    {
                        if (Time.realtimeSinceStartup - reviewDisplayAt > 10f) throw new TimeoutException("native screen did not resize to a small annual-review viewport");
                        return false;
                    }
                    foreach (var screen in new[] { new Vector2(1000, 600), new Vector2(800, 500), new Vector2(1280, 800) })
                    {
                        var size = Dialog_MFYearPhotos.SizeFor(screen, 1.3f);
                        ReviewDisplayCheck(size.x <= screen.x - 40f && size.y <= screen.y - 40f, "annual review size stays inside " + screen + " at 130 percent");
                    }
                    ReviewShowAnnual(original); reviewDisplayPhase = 3; return false;
                case 3:
                    if (!reviewAnnual.Rendered) return false;
                    var display = reviewAnnual.LastDisplayLayout; var page = display.Page;
                    Rect area = new Rect(0f, 0f, reviewAnnual.windowRect.width - 36f, reviewAnnual.windowRect.height - 36f);
                    ReviewDisplayCheck(ContainsRect(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight), reviewAnnual.windowRect),
                        "actual native annual window including its header and close X is inside the 1000x600 screen");
                    ReviewDisplayCheck(ContainsRect(area, page.AlbumButton) && ContainsRect(area, page.NightButton) && ContainsRect(area, page.CloseButton)
                        && page.AlbumButton.xMax < page.NightButton.xMin && page.NightButton.xMax < page.CloseButton.xMin,
                        "rendered annual album, memory-night and close controls remain separate and within the small content area");
                    ReviewDisplayCheck(display.Scrolls && page.Cards.Length == 3 && page.Cards.All(card => Dialog_MFYearPhotos.PrintImageFor(card, page).height > 100f),
                        "130-percent annual body scrolls while three native prints retain a usable image area");
                    reviewAnnualFooter = page.CloseButton;
                    CloseLog(); Shot("review-year-1000x600-130-top.png"); reviewDisplayAt = Time.realtimeSinceStartup; reviewDisplayPhase = 4; return false;
                case 4:
                    if (!ReviewScreenshotReady("review-year-1000x600-130-top.png")) return false;
                    var bottom = reviewAnnual.LastDisplayLayout;
                    AccessTools.Field(typeof(Dialog_MFYearPhotos), "scroll").SetValue(reviewAnnual,
                        new Vector2(0f, bottom.BodyHeight - bottom.BodyViewport.height));
                    reviewAnnual.Rendered = false; reviewDisplayPhase = 5; return false;
                case 5:
                    if (!reviewAnnual.Rendered) return false;
                    ReviewDisplayCheck(reviewAnnual.LastDisplayLayout.Page.CloseButton == reviewAnnualFooter,
                        "annual footer stays fixed when the real native scroll view moves to the end of the body");
                    CloseLog(); Shot("review-year-1000x600-130-bottom.png"); reviewDisplayAt = Time.realtimeSinceStartup; reviewDisplayPhase = 6; return false;
                case 6:
                    if (!ReviewScreenshotReady("review-year-1000x600-130-bottom.png")) return false;
                    reviewAnnual.OpenAlbum();
                    ReviewDisplayCheck(!Find.WindowStack.Windows.Contains(reviewAnnual) && Find.WindowStack.Windows.OfType<MainTabWindow_Album>().Any(),
                        "fixed annual album control's normal action closes the annual window and opens the album");
                    Find.WindowStack.TryRemove(typeof(MainTabWindow_Album), false);
                    var closeLetter = DesignLetter(original.year); closeLetter.year = original.year; Find.LetterStack.ReceiveLetter(closeLetter);
                    var closeWindow = new Dialog_MFYearPhotos(closeLetter); Find.WindowStack.Add(closeWindow); closeWindow.Close();
                    ReviewDisplayCheck(!Find.WindowStack.Windows.Contains(closeWindow) && !Find.LetterStack.LettersListForReading.Contains(closeLetter),
                        "annual close action removes both its window and notification at small-screen settings");
                    foreach (var record in reviewAnnualRecords) PhotoStore.Instance.Remove(record);
                    MomentFramesMod.Settings.albumTheme = reviewSavedTheme; MomentFramesMod.Settings.albumFont = reviewSavedFont;
                    MomentFramesMod.Settings.albumTextSize = reviewSavedTextSize;
                    Screen.SetResolution(reviewScreenWidth, reviewScreenHeight, false);
                    checks.Add("INFO: annual native screenshots use three explicitly cloned registry prints sharing the original captured PNG; button actions use the same public methods as the rendered controls.");
                    reviewDisplayAt = Time.realtimeSinceStartup; reviewDisplayPhase = 7; return false;
                case 7:
                    if (Screen.width != reviewScreenWidth || Screen.height != reviewScreenHeight)
                    {
                        if (Time.realtimeSinceStartup - reviewDisplayAt > 10f) throw new TimeoutException("native screen did not restore after annual-review fixture");
                        return false;
                    }
                    reviewDisplayPhase = 8; return true;
                default: return true;
            }
        }
    }
}
