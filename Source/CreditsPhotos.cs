using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The colony's photos in the ending credits (user 2026-11-08, from a Reddit comment "It would be really cool if the pictures
    /// also appear during the games credits", then "2+4로 해?" / "그래 진행해 줘" after the first cut's photos were small and blurry). Only the
    /// credits after a won game (every ending goes through GameVictoryUtility.ShowCredits, which sets wonGame); the main menu's credits stay as
    /// they are. The game draws its credits in an 800-wide column in the middle of a black screen. While they roll, a card stands in each
    /// margin, left and right, and each changes in turn, oldest first, keeping pace with the credits' own scroll (which the game times to the
    /// ending song). When the scroll stops ("Thanks for playing" in the middle, about ten seconds of the song left), the column dims and a
    /// few big moments come back one at a time in the middle of the screen, full size, then the ending photo stays with "Thanks for playing"
    /// under it. Cards show the original photo, read and decoded in the background (the thumbnail stands in until it is ready): the first
    /// cut drew the 256-pixel album thumbnail stretched to a 280-pixel card.</summary>
    public static class CreditsPhotos
    {
        public const int Max = 48;
        internal const int FinaleMax = 3;
        private const float Column = 800f, Pad = 28f, MaxWidth = 520f, MinWidth = 170f, Border = 12f, CaptionH = 48f, Fade = 0.15f;
        private const float BigBorder = 18f, BigCaptionH = 70f, DimSeconds = 1f, FinaleLead = 0.4f, FinaleSpan = 3.2f, FinaleFade = 0.6f;
        private static readonly Color Paper = new Color(0.95f, 0.93f, 0.88f), Ink = new Color(0.22f, 0.19f, 0.16f);

        /// <summary>The big moments (the HTML album's large cards, with births and recruits).</summary>
        internal static readonly HashSet<string> BigKinds = new HashSet<string>
        {
            "colonystart", "wedding", "anniversary", "population", "mechboss", "battle", "disaster", "memorynight", "ship", "gravship",
            Historic.KindEnding, "landing", "baby", "recruit",
        };

        private sealed class Loaded { internal PhotoPixels pixels; internal byte[] png; }

        private static Screen_Credits screen;
        private static List<PhotoRecord> shown, rolling, finale;
        private static float finaleClock = -1f, scrollT;
        private static int tickFrame = -1;
        private static readonly Dictionary<string, Material> held = new Dictionary<string, Material>();
        private static readonly Dictionary<string, Task<Loaded>> loading = new Dictionary<string, Task<Loaded>>();
        private static readonly HashSet<string> failed = new HashSet<string>();

        // What the last frame drew (the probe reads these). Side 0 = left margin, 1 = right.
        internal static int DrawnFrames, FinaleIndex = -1;
        internal static readonly int[] SideIndex = { -1, -1 };
        internal static readonly float[] SideAlpha = new float[2];
        internal static readonly Rect[] SideCard = new Rect[2];
        internal static readonly bool[] SideFull = new bool[2];
        internal static float FinaleAlpha, Dim;
        internal static Rect BigCard, Thanks;
        internal static bool BigFull;
        internal static IReadOnlyList<PhotoRecord> Shown => shown;
        internal static IReadOnlyList<PhotoRecord> Rolling => rolling;
        internal static IReadOnlyList<PhotoRecord> Finale => finale;
        internal static int HeldCount => held.Count;

        internal static bool Enabled => MomentFramesMod.Settings != null && MomentFramesMod.Settings.creditsPhotos;

        /// <summary>Favourites first, then the big moments, then the other moments (group photos, ceremonies, first visits ...), then photos taken
        /// by hand, then the colony's whole-settlement photos: at most <paramref name="max"/>, spread evenly over the years when a group has more
        /// than the room left, in time order, the latest ending photo last. Never a birthday (one person's photo), a test shot, a re-shoot or a
        /// photo from another save. A whole-settlement survey is saved as several area photos: one of them stands for it (the author's own game,
        /// 2026-11-08: one day's survey was 20 of 46 photos, and without the "other moments" step it would have filled the credits).</summary>
        internal static List<PhotoRecord> Pick(IEnumerable<PhotoRecord> records, Func<PhotoRecord, bool> usable, int max = Max)
        {
            var surveys = new HashSet<string>();
            var pool = records.Where(r => r != null && r.HasFile && !Kinds.PersonalOnly(r.kind) && r.kind != Kinds.Test && usable(r))
                .OrderBy(r => r.ticksAbs).Where(r => !Kinds.IsSurvey(r.kind) || surveys.Add(SurveyOf(r))).ToList();
            var ending = pool.LastOrDefault(r => r.kind == Historic.KindEnding);
            int room = max - (ending != null ? 1 : 0);
            var chosen = new List<PhotoRecord>();
            foreach (Func<PhotoRecord, bool> group in new Func<PhotoRecord, bool>[] { r => r.favorite, r => BigKinds.Contains(r.kind), r => Kinds.IsMilestone(r.kind), r => r.kind == Kinds.Manual, r => Kinds.IsSurvey(r.kind) })
            {
                int left = room - chosen.Count;
                if (left <= 0) break;
                chosen.AddRange(Spread(pool.Where(r => r != ending && !chosen.Contains(r) && group(r)).ToList(), left));
            }
            chosen = chosen.OrderBy(r => r.ticksAbs).ToList();
            if (ending != null) chosen.Add(ending);
            return chosen;
        }

        /// <summary>The photos beside the rolling credits (all but the ending photo) and the big ones once they stop: up to
        /// <see cref="FinaleMax"/> of the rolling ones (favourites, then big moments, spread over the years; any when there are none), in time
        /// order, then the ending photo.</summary>
        internal static void Split(List<PhotoRecord> chosen, out List<PhotoRecord> roll, out List<PhotoRecord> big)
        {
            var ending = chosen.Count > 0 && chosen[chosen.Count - 1].kind == Historic.KindEnding ? chosen[chosen.Count - 1] : null;
            roll = ending == null ? chosen.ToList() : chosen.Take(chosen.Count - 1).ToList();
            var pool = roll;
            var picked = new List<PhotoRecord>();
            foreach (Func<PhotoRecord, bool> group in new Func<PhotoRecord, bool>[] { r => r.favorite, r => BigKinds.Contains(r.kind) })
            {
                int left = FinaleMax - picked.Count;
                if (left <= 0) break;
                picked.AddRange(Spread(pool.Where(r => !picked.Contains(r) && group(r)).ToList(), left));
            }
            if (picked.Count == 0) picked.AddRange(Spread(pool, FinaleMax));
            big = picked.OrderBy(r => r.ticksAbs).ToList();
            if (ending != null) big.Add(ending);
        }

        /// <summary>The survey an area photo belongs to ("survey:&lt;id&gt;:&lt;area&gt;"); without that key, its day.</summary>
        private static string SurveyOf(PhotoRecord r)
        {
            string key = r.eventKey;
            int last = key != null && key.StartsWith("survey:", StringComparison.Ordinal) ? key.LastIndexOf(':') : -1;
            return last > "survey:".Length ? key.Substring(0, last) : "day:" + r.ticksAbs / GenDate.TicksPerDay;
        }

        /// <summary>n of the list, evenly from first to last (the list is in time order).</summary>
        private static IEnumerable<PhotoRecord> Spread(List<PhotoRecord> list, int n)
        {
            if (list.Count <= n) return list;
            if (n == 1) return new[] { list[list.Count - 1] };
            return Enumerable.Range(0, n).Select(i => list[(int)Math.Round(i * (list.Count - 1) / (double)(n - 1))]);
        }

        internal static void Open(Screen_Credits credits)
        {
            Close();
            if (credits == null || !credits.wonGame || !Enabled) return;
            var store = PhotoStore.Instance;
            if (store == null) return;
            var list = Pick(store.Records, r => File.Exists(store.FilePath(r)));
            if (list.Count == 0) return;
            screen = credits;
            shown = list;
            Split(list, out rolling, out finale);
            foreach (var r in list) PhotoTextureCache.ThumbDeferred(store.ThumbPath(r), store.FilePath(r));
        }

        internal static void Close()
        {
            foreach (var path in held.Keys.ToList()) PhotoTextureCache.Release(path);
            held.Clear();
            loading.Clear();
            failed.Clear();
            screen = null;
            shown = rolling = finale = null;
            finaleClock = -1f;
            tickFrame = -1;
            SideIndex[0] = SideIndex[1] = -1;
            FinaleIndex = -1;
            Dim = 0f;
        }

        /// <summary>Which card a margin shows, how visible, at this point of the credits (t = scroll / its end). The left margin takes the
        /// rolling photos 0, 2, 4 ..., the right one 1, 3, 5 ..., each for the same span, the right half a span later, so one card is always
        /// steady while the other changes; the last card of each side stays until the credits stop.</summary>
        internal static void Slot(float t, int m, int side, out int index, out float alpha)
        {
            index = -1; alpha = 0f;
            int count = side == 0 ? (m + 1) / 2 : m / 2;
            if (count <= 0) return;
            float span = m <= 1 ? 1f : 1f / ((m + 1) / 2 + 0.5f);
            float pos = (Mathf.Clamp01(t) - (side == 0 ? 0f : span / 2f)) / span;
            if (pos < 0f) return;
            int k = Mathf.Min(count - 1, (int)pos);
            float u = pos - k;
            index = side + 2 * k;
            float fadeIn = Mathf.Clamp01(u / Fade);
            alpha = k == count - 1 ? fadeIn : Mathf.Min(fadeIn, Mathf.Clamp01((1f - u) / Fade));
        }

        /// <summary>Which big photo, how visible, this long after the credits stopped (seconds); the last one stays.</summary>
        internal static void FinaleSlot(float clock, int n, out int index, out float alpha)
        {
            index = -1; alpha = 0f;
            if (n <= 0 || clock < FinaleLead) return;
            index = Mathf.Min(n - 1, (int)((clock - FinaleLead) / FinaleSpan));
            float start = FinaleLead + index * FinaleSpan;
            float fadeIn = Mathf.Clamp01((clock - start) / FinaleFade);
            alpha = index == n - 1 ? fadeIn : Mathf.Min(fadeIn, Mathf.Clamp01((start + FinaleSpan - clock) / FinaleFade));
        }

        internal static void Draw(Screen_Credits credits, float scroll, float maxScroll)
        {
            if (!ReferenceEquals(credits, screen) || shown == null || shown.Count == 0) return;
            var store = PhotoStore.Instance;
            if (store == null) return;
            if (Time.frameCount != tickFrame) Tick(store, scroll, maxScroll);
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            var matrix = GUI.matrix;
            var color = GUI.color;
            var font = Text.Font;
            var anchor = Text.Anchor;
            try
            {
                float margin = (UI.screenWidth - Column) / 2f;
                float w = Mathf.Min(margin - 2f * Pad, MaxWidth);
                for (int side = 0; side < 2; side++)
                {
                    SideIndex[side] = -1; SideAlpha[side] = 0f;
                    if (w < MinWidth || rolling.Count == 0) continue;   // too narrow a screen: the credits as the game has them
                    Slot(scrollT, rolling.Count, side, out int i, out float alpha);
                    alpha *= 1f - Dim;
                    if (i < 0 || alpha <= 0f) continue;
                    var tex = TextureFor(store, rolling[i], out bool full);
                    if (tex == null) continue;
                    float cx = side == 0 ? margin / 2f : UI.screenWidth - margin / 2f;
                    float cy = UI.screenHeight * (side == 0 ? 0.40f : 0.52f);
                    SideCard[side] = DrawCard(rolling[i], tex, new Vector2(cx, cy), w, UI.screenHeight * 0.5f + Border + CaptionH, Border, CaptionH,
                        GameFont.Small, GameFont.Tiny, side == 0 ? -2f : 2f, alpha, false);
                    SideIndex[side] = i; SideAlpha[side] = alpha; SideFull[side] = full;
                }

                FinaleIndex = -1; FinaleAlpha = 0f; Thanks = default;
                if (Dim > 0f)
                {
                    // Over the game's column only: the margins are already black, and "Skip credits" stays where the game put it.
                    var column = new Rect(UI.screenWidth / 2f - Column / 2f, 0f, Column, UI.screenHeight);
                    if (column.xMax > UI.screenWidth - 200f) column.yMax = UI.screenHeight - 110f;
                    GUI.color = new Color(0f, 0f, 0f, Dim);
                    GUI.DrawTexture(column, BaseContent.WhiteTex);
                    FinaleSlot(finaleClock, finale.Count, out int j, out float big);
                    if (j >= 0 && big > 0f)
                    {
                        var tex = TextureFor(store, finale[j], out bool full);
                        if (tex != null)
                        {
                            bool last = j == finale.Count - 1;
                            float maxW = Mathf.Min(UI.screenWidth * 0.62f, UI.screenWidth - 160f);
                            float maxH = UI.screenHeight * 0.68f;
                            BigCard = DrawCard(finale[j], tex, new Vector2(UI.screenWidth / 2f, UI.screenHeight * 0.44f), maxW, maxH, BigBorder, BigCaptionH,
                                GameFont.Medium, GameFont.Small, j % 2 == 0 ? -1f : 1f, big, last);
                            FinaleIndex = j; FinaleAlpha = big; BigFull = full;
                            if (last)
                            {
                                GUI.matrix = matrix;
                                GUI.color = new Color(1f, 1f, 1f, big);
                                Text.Font = GameFont.Medium;
                                Text.Anchor = TextAnchor.UpperCenter;
                                Thanks = new Rect(UI.screenWidth / 2f - 400f, BigCard.yMax + 18f, 800f, 40f);
                                Widgets.Label(Thanks, "ThanksForPlaying".Translate());
                            }
                        }
                    }
                }
            }
            finally
            {
                GUI.matrix = matrix;
                GUI.color = color;
                Text.Font = font;
                Text.Anchor = anchor;
            }
            DrawnFrames++;
        }

        /// <summary>Once a frame: where the credits are, how long they have been stopped, and which originals to have ready.</summary>
        private static void Tick(PhotoStore store, float scroll, float maxScroll)
        {
            tickFrame = Time.frameCount;
            scrollT = maxScroll > 0f ? Mathf.Clamp01(scroll / maxScroll) : 1f;
            bool stopped = maxScroll <= 0f || scroll >= maxScroll - 0.5f;
            finaleClock = !stopped ? -1f : finaleClock < 0f ? 0f : finaleClock + Time.deltaTime;   // the window's own clock (Time.deltaTime)
            Dim = finaleClock < 0f ? 0f : Mathf.Clamp01(finaleClock / DimSeconds);

            var want = new List<PhotoRecord>();
            for (int side = 0; side < 2 && Dim < 1f; side++)
            {
                Slot(scrollT, rolling.Count, side, out int i, out _);
                if (i < 0) continue;
                want.Add(rolling[i]);
                if (i + 2 < rolling.Count) want.Add(rolling[i + 2]);
            }
            if (scrollT >= 0.9f || finaleClock >= 0f)
            {
                FinaleSlot(Mathf.Max(0f, finaleClock), finale.Count, out int j, out _);
                j = Mathf.Max(0, j);
                for (int k = j; k < Mathf.Min(finale.Count, j + 2); k++) want.Add(finale[k]);
            }
            Keep(store, want);
        }

        /// <summary>Holds the originals of <paramref name="want"/> in the shared full-size cache and lets go of the rest; reads and decodes them
        /// in the background, uploading at most one a frame.</summary>
        private static void Keep(PhotoStore store, List<PhotoRecord> want)
        {
            var paths = new HashSet<string>(want.Select(r => store.FilePath(r)).Where(p => !p.NullOrEmpty()));
            foreach (var path in held.Keys.Where(p => !paths.Contains(p)).ToList()) { PhotoTextureCache.Release(path); held.Remove(path); }
            foreach (var path in loading.Keys.Where(p => !paths.Contains(p)).ToList()) loading.Remove(path);
            foreach (var pair in loading.ToList())
            {
                if (!pair.Value.IsCompleted) continue;
                loading.Remove(pair.Key);
                try
                {
                    var got = pair.Value.GetAwaiter().GetResult();
                    var mat = PhotoTextureCache.AcquireDecoded(pair.Key, got.pixels, got.png);
                    if (mat != null) held[pair.Key] = mat; else failed.Add(pair.Key);
                }
                catch (Exception) { failed.Add(pair.Key); }   // the thumbnail stays on the card
                break;
            }
            foreach (var path in paths)
            {
                if (held.ContainsKey(path) || loading.ContainsKey(path) || failed.Contains(path)) continue;
                if (PhotoTextureCache.IsCached(path))
                {
                    var mat = PhotoTextureCache.Acquire(path);
                    if (mat != null) held[path] = mat; else failed.Add(path);
                    continue;
                }
                string source = path;
                var task = Task.Run(() => Read(source));
                task.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                loading[path] = task;
            }
        }

        private static Loaded Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 67108864) throw new IOException("Photo unavailable or too large");
            byte[] bytes = File.ReadAllBytes(path);
            // As the original viewer: RGB PNGs and sequential JPEGs decode here; other images keep the engine's decoder.
            var pixels = PhotoImageFormat.DecodeOnWorker(bytes);
            return pixels != null ? new Loaded { pixels = pixels } : new Loaded { png = bytes };
        }

        private static Texture TextureFor(PhotoStore store, PhotoRecord rec, out bool full)
        {
            full = held.TryGetValue(store.FilePath(rec), out var mat) && mat != null && mat.mainTexture != null;
            return full ? mat.mainTexture : PhotoTextureCache.ThumbDeferred(store.ThumbPath(rec), store.FilePath(rec));
        }

        /// <summary>A cream card around the photo with its title and date, centred on <paramref name="center"/> within the size given, kept clear
        /// of the top edge and of "Skip credits".</summary>
        private static Rect DrawCard(PhotoRecord rec, Texture tex, Vector2 center, float maxW, float maxH, float border, float captionH,
            GameFont titleFont, GameFont dateFont, float angle, float alpha, bool roomBelow)
        {
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1.5f;
            float pw = maxW - 2f * border, ph = pw / aspect;
            if (ph > maxH - border - captionH) { ph = maxH - border - captionH; pw = ph * aspect; }
            float w = pw + 2f * border, h = border + ph + captionH;
            float bottom = UI.screenHeight - 130f - (roomBelow ? 58f : 0f);
            float y = Mathf.Clamp(center.y - h / 2f, 40f, Mathf.Max(40f, bottom - h));
            var card = new Rect(center.x - w / 2f, y, w, h);
            var turned = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, card.center);
            Widgets.DrawBoxSolid(card, new Color(Paper.r, Paper.g, Paper.b, alpha));
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(new Rect(card.x + border, card.y + border, pw, ph), tex, ScaleMode.ScaleToFit);
            GUI.color = new Color(Ink.r, Ink.g, Ink.b, alpha);
            Text.Anchor = TextAnchor.UpperCenter;
            bool big = titleFont == GameFont.Medium;
            Text.Font = titleFont;
            Widgets.Label(new Rect(card.x + border, card.y + border + ph + (big ? 8f : 5f), pw, big ? 32f : 24f), (rec.DisplayTitle ?? "").Truncate(pw));
            Text.Font = dateFont;
            Widgets.Label(new Rect(card.x + border, card.y + border + ph + (big ? 42f : 27f), pw, big ? 24f : 18f), rec.dateLabel ?? "");
            GUI.matrix = turned;
            return card;
        }
    }

    [HarmonyPatch(typeof(Screen_Credits), nameof(Screen_Credits.PreOpen))]
    internal static class Patch_CreditsPhotosOpen
    {
        private static void Postfix(Screen_Credits __instance)
        {
            try { CreditsPhotos.Open(__instance); }
            catch (Exception e) { Hooks.Fail("credits photos", e); }
        }
    }

    [HarmonyPatch(typeof(Screen_Credits), nameof(Screen_Credits.DoWindowContents))]
    internal static class Patch_CreditsPhotosDraw
    {
        private static Func<Screen_Credits, float> maxScroll;

        private static void Postfix(Screen_Credits __instance, float ___scrollPosition)
        {
            try
            {
                if (maxScroll == null) maxScroll = AccessTools.MethodDelegate<Func<Screen_Credits, float>>(AccessTools.PropertyGetter(typeof(Screen_Credits), "MaxScrollPosition"));
                CreditsPhotos.Draw(__instance, ___scrollPosition, maxScroll(__instance));
            }
            catch (Exception e) { Hooks.Fail("credits photos", e); }
        }
    }

    [HarmonyPatch(typeof(Screen_Credits), nameof(Screen_Credits.PostClose))]
    internal static class Patch_CreditsPhotosClose
    {
        private static void Postfix()
        {
            try { CreditsPhotos.Close(); }
            catch (Exception e) { Hooks.Fail("credits photos", e); }
        }
    }
}
