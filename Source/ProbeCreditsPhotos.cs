using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>-momentFramesCreditsPhotos: the colony's photos beside the ending credits (CreditsPhotos). The choice of photos on made-up
    /// albums (birthdays and test shots never, favourites first, the room spread over the years, time order, the ending last); then real photo
    /// files in this game: the main menu's credits show none, the ending credits (GameVictoryUtility.ShowCredits, as every ending calls it)
    /// show a card in each margin at once, from the original files, beside the game's 800-wide column and clear of "Skip credits"; when the
    /// scroll stops, the column dims, the big photos come one at a time and the ending photo stays with "Thanks for playing"; scrolling back
    /// brings the margins back; the setting turns it off. Screenshots of both margins (twice), a big photo and the end.</summary>
    public static partial class Probe
    {
        private static bool creditsPhotosMode;
        private static List<PhotoRecord> cpExpected;
        private static Screen_Credits cpScreen;
        private static int cpWait, cpDrawn, cpShot;
        private static float cpDeadline, cpLastSince;
        private static readonly HashSet<int> cpSeen = new HashSet<int>();

        private static void CpCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("credits photos: " + label); checks.Add("PASS: " + label); }

        private static PhotoRecord CpFake(string id, string kind, int ticks, bool favorite = false)
            => new PhotoRecord { id = id, file = id + ".png", kind = kind, ticksAbs = ticks, favorite = favorite, title = id };

        private static void CpPickChecks()
        {
            var some = new List<PhotoRecord>
            {
                CpFake("ending", Historic.KindEnding, 900), CpFake("birthday", Kinds.Birthday, 100, true), CpFake("test", Kinds.Test, 110, true),
                CpFake("start", "colonystart", 10), CpFake("manualfav", Kinds.Manual, 50, true), CpFake("manual", Kinds.Manual, 60),
                CpFake("quarter", Kinds.SettlementQuarter, 70), CpFake("party", "party", 80), CpFake("wedding", "wedding", 30),
            };
            var pick = CreditsPhotos.Pick(some, r => true);
            CpCheck(string.Join(",", pick.Select(r => r.id)) == "start,wedding,manualfav,manual,quarter,party,ending",
                "choice: no birthday, no test shot; with room for all, every other photo in time order, the ending last (" + string.Join(",", pick.Select(r => r.id)) + ")");
            pick = CreditsPhotos.Pick(some, r => true, 5);
            CpCheck(string.Join(",", pick.Select(r => r.id)) == "start,wedding,manualfav,party,ending",
                "short of room: favourite, big moments, other moments before hand-taken and whole-settlement photos (" + string.Join(",", pick.Select(r => r.id)) + ")");
            var areas = new List<PhotoRecord> { CpFake("s1a", Kinds.SettlementQuarter, 100), CpFake("s1b", Kinds.SettlementQuarter, 101), CpFake("s2a", Kinds.SettlementQuarter, 200), CpFake("day", Kinds.SettlementOverview, 300), CpFake("day2", Kinds.SettlementOverview, 310) };
            areas[0].eventKey = "survey:one:0"; areas[1].eventKey = "survey:one:1"; areas[2].eventKey = "survey:two:0";
            pick = CreditsPhotos.Pick(areas, r => true);
            CpCheck(string.Join(",", pick.Select(r => r.id)) == "s1a,s2a,day", "one area photo per settlement survey, one per day without a survey key (" + string.Join(",", pick.Select(r => r.id)) + ")");
            CpCheck(CreditsPhotos.Pick(some, r => r.id != "wedding").All(r => r.id != "wedding"), "a photo whose file is gone is left out");

            var many = new List<PhotoRecord>();
            for (int i = 0; i < 40; i++) many.Add(CpFake("big" + i, "battle", 1000 + i * 10));
            for (int i = 0; i < 10; i++) many.Add(CpFake("fav" + i, Kinds.Manual, 1005 + i * 37, true));
            for (int i = 0; i < 12; i++) many.Add(CpFake("q" + i, Kinds.SettlementQuarter, 1001 + i * 30));
            many.Add(CpFake("end", Historic.KindEnding, 5000));
            pick = CreditsPhotos.Pick(many, r => true);
            CpCheck(pick.Count == CreditsPhotos.Max, "at most " + CreditsPhotos.Max + " photos (" + pick.Count + ")");
            CpCheck(Enumerable.Range(0, 10).All(i => pick.Any(r => r.id == "fav" + i)), "every favourite is in when there is room");
            CpCheck(pick.Count(r => r.id.StartsWith("big")) == 37 && pick.Any(r => r.id == "big0") && pick.Any(r => r.id == "big39"),
                "the big moments that fit (13) are spread from the first to the last");
            CpCheck(!pick.Any(r => r.id.StartsWith("q")), "quarterly photos only when room is left");
            CpCheck(pick.Last().id == "end" && pick.Take(pick.Count - 1).Select(r => r.ticksAbs).SequenceEqual(pick.Take(pick.Count - 1).Select(r => r.ticksAbs).OrderBy(x => x)),
                "time order, the ending last");
            CpCheck(CreditsPhotos.Pick(new PhotoRecord[0], r => true).Count == 0, "no photos, nothing to show");

            float span = 1f / 3.5f;   // five rolling photos: three on the left, two on the right
            CreditsPhotos.Slot(0f, 5, 0, out int i0, out float a0);
            CreditsPhotos.Slot(0f, 5, 1, out int r0, out float b0);
            CreditsPhotos.Slot(span / 2f, 5, 0, out int i1, out float a1);
            CreditsPhotos.Slot(span, 5, 1, out int r1, out float b1);
            CreditsPhotos.Slot(1f, 5, 0, out int i2, out float a2);
            CreditsPhotos.Slot(1f, 5, 1, out int r2, out float b2);
            CpCheck(i0 == 0 && a0 == 0f && r0 == -1 && b0 == 0f, "left card fades in from the start, the right one has not begun");
            CpCheck(i1 == 0 && Mathf.Approximately(a1, 1f) && r1 == 1 && Mathf.Approximately(b1, 1f), "half a span apart: the left full in its turn while the right is full in its own");
            CpCheck(i2 == 4 && Mathf.Approximately(a2, 1f) && r2 == 3 && Mathf.Approximately(b2, 1f), "the last card of each side stays until the credits stop");
            CreditsPhotos.Slot(0.5f, 1, 1, out int none, out _);
            CpCheck(none == -1, "one photo: the right margin stays empty");

            CreditsPhotos.Split(CreditsPhotos.Pick(some, r => true), out var roll, out var big);
            CpCheck(string.Join(",", roll.Select(r => r.id)) == "start,wedding,manualfav,manual,quarter,party", "the ending photo does not roll (" + string.Join(",", roll.Select(r => r.id)) + ")");
            CpCheck(string.Join(",", big.Select(r => r.id)) == "start,wedding,manualfav,ending", "big photos: favourites, then big moments, in time order, the ending last (" + string.Join(",", big.Select(r => r.id)) + ")");
            var plain = new List<PhotoRecord>();
            for (int k = 0; k < 5; k++) plain.Add(CpFake("p" + k, Kinds.Manual, 10 + k));
            CreditsPhotos.Split(plain, out roll, out big);
            CpCheck(roll.Count == 5 && string.Join(",", big.Select(r => r.id)) == "p0,p2,p4", "no favourite, no big moment, no ending: any three, spread (" + string.Join(",", big.Select(r => r.id)) + ")");
            CreditsPhotos.Split(new List<PhotoRecord> { CpFake("e", Historic.KindEnding, 5) }, out roll, out big);
            CpCheck(roll.Count == 0 && big.Count == 1 && big[0].id == "e", "only an ending photo: it is the whole show");

            CreditsPhotos.FinaleSlot(0.2f, 4, out int f0, out _);
            CreditsPhotos.FinaleSlot(2f, 4, out int f1, out float fa1);
            CreditsPhotos.FinaleSlot(3.9f, 4, out int f2, out float fa2);
            CreditsPhotos.FinaleSlot(100f, 4, out int f3, out float fa3);
            CpCheck(f0 == -1 && f1 == 0 && Mathf.Approximately(fa1, 1f) && f2 == 1 && Mathf.Abs(fa2 - 0.5f) < 0.01f && f3 == 3 && Mathf.Approximately(fa3, 1f),
                "big photos: one at a time, fading in, the last one stays (" + f0 + " " + f1 + " " + f2 + " " + fa2.ToString("0.00") + " " + f3 + ")");
        }

        private static PhotoRecord CpPhoto(string id, string kind, int offset, Color tint, bool favorite = false)
        {
            const int w = 600, h = 400;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float shade = 0.55f + 0.45f * ((x / 40 + y / 40) % 2);
                    int k = (y * w + x) * 3;
                    rgb[k] = (byte)(tint.r * 255 * shade); rgb[k + 1] = (byte)(tint.g * 255 * shade); rgb[k + 2] = (byte)(tint.b * 255 * shade);
                }
            var pixels = new PhotoPixels(w, h, rgb);
            var store = PhotoStore.Instance;
            var rec = UiRecord(store, "credits-" + id, kind, "credits-" + id + ".png", offset);
            rec.favorite = favorite;
            rec.title = "Credits " + id;
            rec.dateLabel = "5500 · " + id;
            File.WriteAllBytes(store.FilePath(rec), pixels.Encode());
            File.WriteAllBytes(store.ThumbPath(rec), pixels.Resize(FramePhotoTextures.SideFor(w, h, 256)).Encode());
            return rec;
        }

        private static void CpScroll(float t)
        {
            float max = (float)AccessTools.PropertyGetter(typeof(Screen_Credits), "MaxScrollPosition").Invoke(cpScreen, null);
            Traverse.Create(cpScreen).Field("scrollPosition").SetValue(t * max);
            Traverse.Create(cpScreen).Field("timeUntilAutoScroll").SetValue(999f);   // hold it there for the check
        }

        private static void CreditsPhotosStep()
        {
            var store = PhotoStore.Instance;
            if (phase == 0)
            {
                map = Find.CurrentMap; center = map.Center; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                MomentFramesMod.Settings.autoCaptureEnabled = false;
                Directory.CreateDirectory(store.FolderPath);
                CpPickChecks();
                CpCheck(new MomentFramesSettings().creditsPhotos && SettingsCatalog.Entries.Any(e => e.Id == "creditsPhotos" && e.Kind == SettingsCatalog.SettingKind.Toggle),
                    "setting on by default, a toggle in the settings window");
                CpPhoto("start", "colonystart", -6000, new Color(0.35f, 0.75f, 0.35f));
                CpPhoto("wedding", "wedding", -5000, new Color(0.95f, 0.55f, 0.65f), true);
                CpPhoto("manualfav", Kinds.Manual, -4000, new Color(0.40f, 0.60f, 0.95f), true);
                CpPhoto("birthday", Kinds.Birthday, -3500, new Color(0.95f, 0.85f, 0.25f), true);
                CpPhoto("quarter", Kinds.SettlementQuarter, -3000, new Color(0.60f, 0.45f, 0.30f));
                CpPhoto("battle", "battle", -2000, new Color(0.85f, 0.30f, 0.25f));
                CpPhoto("ending", Historic.KindEnding, -1000, new Color(0.95f, 0.70f, 0.30f));
                cpExpected = CreditsPhotos.Pick(store.Records, r => File.Exists(store.FilePath(r)));
                CpCheck(string.Join(",", cpExpected.Select(r => r.id)) == "credits-start,credits-wedding,credits-manualfav,credits-quarter,credits-battle,credits-ending",
                    "this game's photos: six of seven (no birthday), the ending last");
                // The main menu's credits: the game's own, nothing added.
                cpDrawn = CreditsPhotos.DrawnFrames;
                cpScreen = new Screen_Credits();
                Find.WindowStack.Add(cpScreen);
                cpWait = Time.frameCount + 20; phase = 1; return;
            }
            if (phase == 1)
            {
                if (Time.frameCount < cpWait) return;
                CpCheck(CreditsPhotos.Shown == null && CreditsPhotos.DrawnFrames == cpDrawn, "the main menu's credits show no photos");
                cpScreen.Close(false);
                // Off in the settings: the ending credits stay the game's own.
                MomentFramesMod.Settings.creditsPhotos = false;
                GameVictoryUtility.ShowCredits("Probe ending (setting off)", null);
                cpScreen = Find.WindowStack.WindowOfType<Screen_Credits>();
                cpWait = Time.frameCount + 20; phase = 2; return;
            }
            if (phase == 2)
            {
                if (Time.frameCount < cpWait) return;
                CpCheck(cpScreen != null && cpScreen.wonGame && CreditsPhotos.Shown == null && CreditsPhotos.DrawnFrames == cpDrawn, "setting off: no photos in the ending credits");
                cpScreen.Close(false);
                MomentFramesMod.Settings.creditsPhotos = true;
                GameVictoryUtility.ShowCredits("Probe ending: the colony's story would be told here.", null);
                cpScreen = Find.WindowStack.WindowOfType<Screen_Credits>();
                CpCheck(cpScreen != null && cpScreen.wonGame && CreditsPhotos.Shown != null && CreditsPhotos.Shown.Select(r => r.id).SequenceEqual(cpExpected.Select(r => r.id)),
                    "the ending credits take the chosen photos when they open");
                Wait(4f); phase = 3; cpShot = 0; return;   // the game's fade from black (3 s) and the thumbnails loading in the background
            }
            if (phase == 3)
            {
                CpScroll((cpShot + 0.75f) / (((CreditsPhotos.Rolling.Count + 1) / 2) + 0.5f));
                cpDeadline = Time.realtimeSinceStartup + 20f; phase = 4; return;
            }
            if (phase == 4)
            {
                string state = string.Join(" ", new[] { 0, 1 }.Select(s => CreditsPhotos.SideIndex[s] + "/" + CreditsPhotos.SideAlpha[s].ToString("0.00") + "/" + CreditsPhotos.SideFull[s]));
                bool ready = CreditsPhotos.SideIndex[0] == 2 * cpShot && CreditsPhotos.SideIndex[1] == 2 * cpShot + 1
                    && Mathf.Approximately(CreditsPhotos.SideAlpha[0], 1f) && Mathf.Approximately(CreditsPhotos.SideAlpha[1], 1f) && CreditsPhotos.SideFull[0] && CreditsPhotos.SideFull[1];
                if (!ready)
                {
                    if (Time.realtimeSinceStartup > cpDeadline) CpCheck(false, "both margins never showed the originals of cards " + 2 * cpShot + " and " + (2 * cpShot + 1) + " (" + state + ")");
                    return;
                }
                CpCheck(true, "both margins at once, each the original photo, not the thumbnail: step " + cpShot + " (" + state + ")");
                float margin = (UI.screenWidth - 800f) / 2f, width = Mathf.Min(margin - 56f, 520f);
                var l = CreditsPhotos.SideCard[0]; var r = CreditsPhotos.SideCard[1];
                CpCheck(l.xMax <= margin - 10f && l.x >= 10f && r.x >= UI.screenWidth - margin + 10f && r.xMax <= UI.screenWidth - 10f,
                    "cards in their margins, clear of the credits column (" + l + " " + r + ", screen " + UI.screenWidth + "x" + UI.screenHeight + ")");
                CpCheck(Mathf.Abs(l.width - width) < 1f && Mathf.Abs(r.width - width) < 1f, "cards fill the margin (" + l.width + " " + r.width + ", room " + width + ")");
                CpCheck(l.yMax <= UI.screenHeight - 110f && l.y >= 30f && r.yMax <= UI.screenHeight - 110f && r.y >= 30f, "cards clear of \"Skip credits\" and the top edge");
                CpCheck(Mathf.Abs(l.center.y - r.center.y) > 40f, "the two cards stand at different heights");
                CpCheck(CreditsPhotos.HeldCount <= 6, "only the photos on screen and the next ones are held (" + CreditsPhotos.HeldCount + ")");
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, cpShot == 0 ? "credits-both.png" : "credits-next.png"));
                cpWait = Time.frameCount + 4;
                phase = ++cpShot < 2 ? 5 : 6; return;
            }
            if (phase == 5)
            {
                if (Time.frameCount < cpWait) return;
                phase = 3; return;
            }
            if (phase == 6)
            {
                if (Time.frameCount < cpWait) return;
                CpCheck(string.Join(",", CreditsPhotos.Rolling.Select(r => r.id)) == "credits-start,credits-wedding,credits-manualfav,credits-quarter,credits-battle", "the five rolling photos");
                CpCheck(string.Join(",", CreditsPhotos.Finale.Select(r => r.id)) == "credits-wedding,credits-manualfav,credits-battle,credits-ending", "three big moments, then the ending (" + string.Join(",", CreditsPhotos.Finale.Select(r => r.id)) + ")");
                CpScroll(1f);
                cpSeen.Clear();
                cpDeadline = Time.realtimeSinceStartup + 25f; phase = 7; return;
            }
            if (phase == 7)
            {
                if (CreditsPhotos.FinaleIndex >= 0) cpSeen.Add(CreditsPhotos.FinaleIndex);
                if (!(CreditsPhotos.FinaleIndex == 0 && Mathf.Approximately(CreditsPhotos.FinaleAlpha, 1f) && CreditsPhotos.BigFull && Mathf.Approximately(CreditsPhotos.Dim, 1f)))
                {
                    if (Time.realtimeSinceStartup > cpDeadline) CpCheck(false, "the first big photo never came, full size (index " + CreditsPhotos.FinaleIndex + ", alpha " + CreditsPhotos.FinaleAlpha + ", full " + CreditsPhotos.BigFull + ", dim " + CreditsPhotos.Dim + ")");
                    return;
                }
                var card = CreditsPhotos.BigCard;
                CpCheck(Mathf.Abs(card.center.x - UI.screenWidth / 2f) < 1f && card.width >= UI.screenWidth * 0.45f, "a big photo in the middle of the screen, from the original (" + card + ")");
                CpCheck(card.height <= UI.screenHeight * 0.68f + 1f && card.yMax <= UI.screenHeight - 130f + 1f && card.y >= 39f, "it fits, clear of \"Skip credits\"");
                CpCheck(CreditsPhotos.SideIndex[0] == -1 && CreditsPhotos.SideIndex[1] == -1, "the margins have gone");
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "credits-big.png"));
                cpDeadline = Time.realtimeSinceStartup + 25f; phase = 8; return;
            }
            if (phase == 8)
            {
                int last = CreditsPhotos.Finale.Count - 1;
                if (CreditsPhotos.FinaleIndex >= 0) cpSeen.Add(CreditsPhotos.FinaleIndex);
                if (!(CreditsPhotos.FinaleIndex == last && Mathf.Approximately(CreditsPhotos.FinaleAlpha, 1f)))
                {
                    if (Time.realtimeSinceStartup > cpDeadline) CpCheck(false, "the ending photo never came (index " + CreditsPhotos.FinaleIndex + ")");
                    return;
                }
                CpCheck(Enumerable.Range(0, last + 1).All(cpSeen.Contains), "every big photo took its turn (" + string.Join(",", cpSeen.OrderBy(x => x)) + ")");
                var card = CreditsPhotos.BigCard; var thanks = CreditsPhotos.Thanks;
                CpCheck(CreditsPhotos.Finale[last].kind == Historic.KindEnding && CreditsPhotos.BigFull, "the ending photo last, full size");
                CpCheck(thanks.height > 0f && thanks.y >= card.yMax && thanks.yMax <= UI.screenHeight - 100f, "\"Thanks for playing\" under it, clear of \"Skip credits\" (" + thanks + ", card " + card + ")");
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "credits-end.png"));
                cpLastSince = Time.realtimeSinceStartup; phase = 9; return;
            }
            if (phase == 9)
            {
                if (Time.realtimeSinceStartup < cpLastSince + 4.5f) return;   // longer than one big photo's turn
                CpCheck(CreditsPhotos.FinaleIndex == CreditsPhotos.Finale.Count - 1 && Mathf.Approximately(CreditsPhotos.FinaleAlpha, 1f), "the ending photo stays");
                CpScroll((0.75f) / (((CreditsPhotos.Rolling.Count + 1) / 2) + 0.5f));
                cpWait = Time.frameCount + 8; phase = 10; return;
            }
            if (phase == 10)
            {
                if (Time.frameCount < cpWait) return;
                CpCheck(CreditsPhotos.FinaleIndex == -1 && CreditsPhotos.Dim == 0f && CreditsPhotos.SideIndex[0] == 0 && CreditsPhotos.SideIndex[1] == 1, "scrolling back up brings the margins back");
                cpScreen.Close(false);
                cpWait = Time.frameCount + 3; phase = 11; return;
            }
            if (phase == 11)
            {
                if (Time.frameCount < cpWait) return;
                CpCheck(CreditsPhotos.Shown == null && CreditsPhotos.HeldCount == 0, "closing the credits lets go of the photos");
                File.WriteAllText(Path.Combine(dir, "result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json)) + "],\"limitations\":\"Credits opened through GameVictoryUtility.ShowCredits without an ending song and scrolled by setting the window's scroll position; a real ending's song timing, the RimTalk/other-mod credits screens and very small screens (no margin) were not filmed.\"}");
                active = false; Application.Quit();
            }
        }
    }
}
