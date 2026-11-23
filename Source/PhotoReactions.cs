using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    /// <summary>Ephemeral reactions belong to a successful new shutter, never to loading or viewing an old photograph.</summary>
    internal static class PhotoReactions
    {
        internal const int ModeOff = 0, ModeAfter = 1, ModePhoto = 2;
        internal const float Lifetime = 2f;
        internal sealed class Entry
        {
            internal Pawn Pawn;
            internal PhotoIcon Icon;
            internal int Expression = -1;
            internal int ArtIcon => Expression >= 0 ? Expression : Icon==PhotoIcon.Heart?ReactionChoices.Heart
                :Icon==PhotoIcon.Wave?ReactionChoices.HandWave:(int)Icon;
            internal Color Color;
            internal float Delay;
        }
        internal sealed class Plan
        {
            internal int Mode;
            internal Map Map;
            internal PhotoRecord Record;
            internal List<Entry> Entries = new List<Entry>();
        }
        private sealed class Live { internal Plan Plan; internal Entry Entry; internal float Start; internal bool Native; }
        private static readonly List<Live> live = new List<Live>();
        internal static int ActiveCount => live.Count;
        internal static bool HasActiveFor(Pawn pawn) => live.Any(x => x.Entry.Pawn == pawn);
        private static readonly HashSet<string> happyKinds = new HashSet<string> {
            Kinds.Manual, Kinds.Reshoot, Kinds.Birthday, "group", "reunion", "wedding", "party"
        };

        internal static bool Eligible(Pawn pawn, PhotoRecord rec, Map map)
        {
            if (pawn == null || rec == null || map == null) return false;
            bool restingSubject = ReactionChoices.AllowsRestingBirthdaySubject(rec.kind, rec.requiredPerson,
                pawn.GetUniqueLoadID(), rec.reactionPawnIds, rec.reactionIcons);
            bool baby = pawn.DevelopmentalStage.Baby();
            if (!happyKinds.Contains(rec.kind ?? "")
                || pawn.MapHeld != map || pawn.Dead || !pawn.RaceProps.Humanlike
                || !pawn.IsFreeColonist || !baby && !restingSubject && pawn.Downed
                || pawn.Drafted || pawn.InMentalState || !baby && !restingSubject && !pawn.Awake() || !rec.Has(pawn)) return false;
            if (Find.TickManager != null && Math.Abs((long)Find.TickManager.TicksAbs - rec.ticksAbs) > 2500) return false;
            return PhotoCapture.InFrame(pawn, map, rec.center, rec.spanCells, rec.Aspect);
        }

        private static int StableHash(string value)
        {
            unchecked { int h = 23; foreach (char c in value ?? "") h = h * 31 + c; return h; }
        }

        internal static void RunVisualEffect(int seed, Action effect)
        {
            // Vanilla fleck Setup consumes Rand even when every visible parameter is supplied.
            Rand.PushState(seed);
            try { effect(); }
            finally { Rand.PopState(); }
        }

        /// <summary>What a preparation window starts with: saved in the photo, since nothing is drawn in until someone is given an emote. Automatic photos keep their own setting.</summary>
        internal static int PreparationMode
        {
            get
            {
                var s = MomentFramesMod.Settings;
                if (s == null) return ModePhoto;
                if (s.preparationReactionMode >= 0) return s.preparationReactionMode;
                return s.captureReactionMode == ModeOff ? ModeOff : ModePhoto;
            }
        }

        internal static Plan CreatePlan(PhotoRecord rec, Map map, int? mode = null)
        {
            var plan = new Plan { Record = rec, Map = map, Mode = PhotoPreparationChoices.ResolveReactionMode(rec?.captureReactionMode??-1,
                mode,MomentFramesMod.Settings?.captureReactionMode??ModeAfter) };
            if (rec == null || map == null || plan.Mode == ModeOff) return plan;
            // A local seed avoids changing the game's random stream. Sorting also makes it independent of map list ordering.
            var random = new System.Random(StableHash(rec.id));
            var candidates = PhotoCapture.VisiblePawns(map).Where(p => Eligible(p, rec, map)).Distinct().OrderBy(p => p.GetUniqueLoadID()).ToList();
            var assigned = ReactionChoices.Read(rec.reactionPawnIds,rec.reactionIcons);
            // Explicit choices apply to every selected, visible participant. The automatic subset is used only for unassigned people.
            foreach(var pawn in candidates.ToList())
            {
                int expression=ReactionChoices.Choice(assigned,pawn.GetUniqueLoadID());
                if(expression==ReactionChoices.Automatic) continue;
                candidates.Remove(pawn);
                if(expression==ReactionChoices.None) continue;
                plan.Entries.Add(new Entry { Pawn=pawn, Expression=expression,
                    Icon=expression==ReactionChoices.Heart?PhotoIcon.Heart:expression==ReactionChoices.HandWave?PhotoIcon.Wave:(PhotoIcon)expression,
                    Color=ReactionArt.ColorFor(expression),Delay=0f });
            }
            for (int i = candidates.Count - 1; i > 0; i--) { int j = random.Next(i + 1); var p = candidates[i]; candidates[i] = candidates[j]; candidates[j] = p; }
            int count = plan.Entries.Count + Math.Min(3, (candidates.Count + 1) / 2);
            // Event actors get the first available reactions. Eligibility still applies to the subject.
            var subjects = rec.kind == Kinds.Birthday
                ? new[] { rec.requiredPerson }
                : rec.kind == "wedding" ? (rec.eventSubjectIds ?? new List<string>()).Take(2).ToArray() : new string[0];
            foreach (string id in subjects.Where(s => !string.IsNullOrEmpty(s)).Distinct())
            {
                Pawn subject = candidates.FirstOrDefault(p => p.GetUniqueLoadID() == id);
                if (subject == null) continue;
                var icon = rec.kind == Kinds.Birthday ? PhotoIcon.Star : PhotoIcon.Heart;
                plan.Entries.Add(new Entry { Pawn = subject, Icon = icon, Color = ReactionArt.ColorFor((int)icon), Delay = .08f + plan.Entries.Count * .13f });
                candidates.Remove(subject);
            }
            count = Math.Max(count, plan.Entries.Count);
            // Wedding guests and legacy wedding records use neutral icons; only verified spouses get hearts.
            PhotoIcon[] choices = rec.kind == "reunion"
                ? new[] { PhotoIcon.Heart, PhotoIcon.Smile, PhotoIcon.Star }
                : rec.kind == Kinds.Birthday || rec.eventSubtype == "concert"
                    ? new[] { PhotoIcon.Star, PhotoIcon.Music, PhotoIcon.Smile }
                    : new[] { PhotoIcon.Smile, PhotoIcon.Star, PhotoIcon.Wave };
            for (int i = choices.Length - 1; i > 0; i--) { int j = random.Next(i + 1); var icon = choices[i]; choices[i] = choices[j]; choices[j] = icon; }
            int next = 0;
            foreach (var icon in choices)
            {
                if (plan.Entries.Count >= count || next >= candidates.Count) break;
                if (plan.Entries.Any(e => e.Icon == icon)) continue;
                plan.Entries.Add(new Entry { Pawn = candidates[next++], Icon = icon, Color = ReactionArt.ColorFor((int)icon), Delay = .08f + plan.Entries.Count * .13f });
            }
            return plan;
        }

        internal static int Bake(Texture2D image, Camera camera, Plan plan)
        {
            if (image == null || camera == null || plan?.Record == null || plan.Mode != ModePhoto) return 0;
            Color32[] pixels = image.GetPixels32();
            int size = Mathf.Clamp(Mathf.RoundToInt(image.height / Mathf.Max(1f, plan.Record.spanCells) * .85f), 24, 96), count = 0;
            var anchors = new List<Vector2>();
            var art = new List<Texture2D>();
            foreach (var entry in plan.Entries)
            {
                if (!Eligible(entry.Pawn, plan.Record, plan.Map)) continue;
                if(!PhotoCapture.VisualPosition(entry.Pawn,plan.Map,out var head)) continue;
                Vector3 v = camera.WorldToViewportPoint(head + new Vector3(0f, 0f, 1.12f));
                if (v.z <= 0f) continue;
                Texture2D ink = ReactionArt.Texture(entry.ArtIcon);
                if(ink==null)continue;
                anchors.Add(new Vector2(v.x * image.width, v.y * image.height));
                art.Add(ink);
            }
            var layout = BubbleLayout(anchors, size, image.width, image.height);
            for (int i = 0; i < layout.Count; i++)
            {
                var rect = layout[i];
                Texture2D ink = art[i];
                var mask = ink.GetPixels32();
                // The combined Core speech art already carries a white shell and dark outline.
                Stamp(pixels, image.width, image.height, mask, ink.width, ink.height,
                    Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), Mathf.RoundToInt(rect.width), Color.white,true);
                count++;
            }
            if (count > 0) { image.SetPixels32(pixels); image.Apply(false, false); }
            return count;
        }

        // Keep each bubble attached to its subject. Stacking collisions upward made
        // adjacent people appear to have reactions floating at unrelated heights.
        internal static List<Rect> BubbleLayout(IList<Vector2> anchors, int preferredSize, int width, int height)
        {
            var result = new List<Rect>();
            int maximum = Mathf.Min(preferredSize, Mathf.Min(width, height) - 4);
            if (maximum < 1) return result;
            var sizes = Enumerable.Repeat(maximum, anchors.Count).ToArray();
            for (int i = 0; i < anchors.Count; i++)
                for (int j = i + 1; j < anchors.Count; j++)
                {
                    Vector2 delta = anchors[i] - anchors[j];
                    int room = Mathf.FloorToInt(Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y))) - 4;
                    // Coincident subjects can still overlap; do not detach a bubble
                    // from its owner just to force every rectangle apart.
                    int side = Mathf.Clamp(room, Mathf.Min(12, maximum), maximum);
                    sizes[i] = Mathf.Min(sizes[i], side); sizes[j] = Mathf.Min(sizes[j], side);
                }
            for (int i = 0; i < anchors.Count; i++)
            {
                int side = sizes[i];
                float x = Mathf.Clamp(anchors[i].x - side / 2f, 2, width - side - 2);
                float y = Mathf.Clamp(anchors[i].y - side / 2f, 2, height - side - 2);
                result.Add(new Rect(x, y, side, side));
            }
            return result;
        }

        private static void Stamp(Color32[] target, int w, int h, Color32[] mask, int mw, int mh, int x, int y, int size, Color ink,bool sourceColor=false)
        {
            for (int dy = 0; dy < size; dy++) for (int dx = 0; dx < size; dx++)
            {
                int tx = x + dx, ty = y + dy;
                if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                var sample=mask[Mathf.Min(mh - 1, dy * mh / size) * mw + Mathf.Min(mw - 1, dx * mw / size)];
                float a = sample.a / 255f * ink.a;
                if (a <= 0f) continue;
                int index = ty * w + tx;
                Color old = target[index];
                Color paint=sourceColor?(Color)sample:ink;
                target[index] = new Color(old.r * (1f - a) + paint.r * a, old.g * (1f - a) + paint.g * a, old.b * (1f - a) + paint.b * a, 1f);
            }
        }

        internal static void NotifySaved(Plan plan)
        {
            if (plan == null || plan.Mode == ModeOff || plan.Map != Find.CurrentMap) return;
            float now = Time.realtimeSinceStartup;
            foreach (var entry in plan.Entries)
                if (Eligible(entry.Pawn, plan.Record, plan.Map))
                {
                    if(ReactionArt.Texture(entry.ArtIcon)==null)continue;
                    bool native=false;
                    var heartDef=entry.ArtIcon==ReactionChoices.Heart?DefDatabase<FleckDef>.GetNamedSilentFail("MF_PhotoHeart")
                        :entry.ArtIcon==ReactionChoices.HandWave?DefDatabase<FleckDef>.GetNamedSilentFail("MF_PhotoHand"):null;
                    if(heartDef!=null
                        && PhotoCapture.VisualPosition(entry.Pawn,plan.Map,out var head))
                    {
                        // Visual only: isolate native Setup's random draws without changing interaction, mood or relations.
                        RunVisualEffect(StableHash(plan.Record.id + ":" + entry.Pawn.GetUniqueLoadID()), () =>
                            plan.Map.flecks.CreateFleck(new FleckCreationData { def=heartDef,
                                spawnPosition=head+new Vector3(0f,0f,1.12f),scale=.85f,
                                velocityAngle=0f,velocitySpeed=.25f,solidTimeOverride=1.4f,ageTicksOverride=-1 }));
                        native=true;
                    }
                    live.Add(new Live { Plan=plan,Entry=entry,Start=now,Native=native });
                }
        }
        internal static void Reset() => live.Clear();
        internal static void Prune(float now) => live.RemoveAll(x => now - x.Start > x.Entry.Delay + Lifetime
            || x.Plan.Map != Find.CurrentMap || !WorldRendererUtility.DrawingMap || !Eligible(x.Entry.Pawn, x.Plan.Record, x.Plan.Map));
        internal static void Update() => Prune(Time.realtimeSinceStartup);
        internal static void Draw()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Camera == null || PhotoCapture.HideGuides || !WorldRendererUtility.DrawingMap) return;
            Prune(Time.realtimeSinceStartup);
            foreach (var item in live)
            {
                float age = Time.realtimeSinceStartup - item.Start - item.Entry.Delay;
                if (age < 0f || age > Lifetime) continue;
                if(item.Native || !PhotoCapture.VisualPosition(item.Entry.Pawn,item.Plan.Map,out var head))continue;
                Vector3 screen = Find.Camera.WorldToScreenPoint(head + new Vector3(0f, 0f, 1.12f));
                if (screen.z <= 0f) continue;
                Vector3 side=Find.Camera.WorldToScreenPoint(head+new Vector3(.85f,0f,1.12f));
                float size = Mathf.Clamp(Mathf.Abs(side.x-screen.x)/Prefs.UIScale,24f,96f), x = screen.x / Prefs.UIScale, y = UI.screenHeight - screen.y / Prefs.UIScale - age * 15f;
                var color = item.Entry.Color; color.a *= Mathf.Clamp01(age / .12f) * Mathf.Clamp01((Lifetime - age) / .35f);
                Rect rect = new Rect(x - size / 2f, y - size / 2f, size, size);
                ReactionArt.Draw(rect,item.Entry.ArtIcon,color);
            }
        }
    }
}
