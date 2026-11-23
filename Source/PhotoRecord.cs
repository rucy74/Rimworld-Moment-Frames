using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Photo kinds. One place decides what counts as a milestone (chronicle, anniversary letters).</summary>
    public static class Kinds
    {
        public const string Manual = "manual", Test = "test", Reshoot = "reshoot";
        /// <summary>A photo brought over from another save's folder: no people (their ids belong to that save), never a milestone here.</summary>
        public const string Relic = "relic";

        public const string Birthday = "birthday";
        public const string SettlementOverview = "settlement-overview", SettlementQuarter = "settlement-quarter";
        public static bool IsSurvey(string kind) => kind == SettlementOverview || kind == SettlementQuarter;
        public const string OtherDay = "otherday", External = "external";
        /// <summary>Moved out of the album, an event keeps a line in the person's own chronicle; a picture somebody chose to take does not.</summary>
        public static bool KeepsLineWhenArchived(string kind) => IsMilestone(kind) && kind != GroupPhoto.Kind && kind != PetPhotos.Kind;

        public static bool IsMilestone(string kind) => kind != Manual && kind != Test && kind != Reshoot && kind != Relic && kind != Birthday && kind != OtherDay && kind != External && !IsSurvey(kind);

        /// <summary>Photos that belong to one person (birthdays): their own chronicle shows them; the album only when it is filtered to that person
        /// or to this kind; never the colony's chronicle, the anniversary letters or the HTML album.</summary>
        public static bool PersonalOnly(string kind) => kind == Birthday;

        /// <summary>Photographed in the sky's own light even with "bright photos" on: the light is what these are of.</summary>
        public static bool RealLight(string kind) => kind == Conditions.KindDisaster || kind == Conditions.KindSky;
    }

    /// <summary>An edited version of a photo: a crop of the ORIGINAL (never of another edit, so nothing degrades) with one filter, baked to its
    /// own PNG next to the original. The original file is never touched.</summary>
    public class PhotoEdit : IExposable
    {
        public string id;
        public string file;                 // file name in the photo folder; its thumbnail is the same name + ".thumb.png"
        public string filter = PhotoFilters.None;
        public float cx, cy, cw = 1f, ch = 1f;   // crop, as fractions of the original, origin at the top-left
        public float aspect = PhotoCapture.DefaultAspect;   // width / height of the edited image
        public int madeAbs = -1;            // when it was made (in-game ticks)
        public string title, note;          // this version's own title and note; empty = the photo's (user, 2026-09-26)
        public List<PhotoImageIcon> imageIcons = new List<PhotoImageIcon>(); // manual image decorations baked into this edit only
        public string sourceSha256, sourceBaselineFile; // immutable baseline; external overwrites become explicit new edits
        public bool externalImage;

        public bool HasFile => !file.NullOrEmpty();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref file, "file");
            Scribe_Values.Look(ref filter, "filter", PhotoFilters.None);
            Scribe_Values.Look(ref cx, "cx", 0f);
            Scribe_Values.Look(ref cy, "cy", 0f);
            Scribe_Values.Look(ref cw, "cw", 1f);
            Scribe_Values.Look(ref ch, "ch", 1f);
            Scribe_Values.Look(ref aspect, "aspect", PhotoCapture.DefaultAspect);
            Scribe_Values.Look(ref madeAbs, "madeAbs", -1);
            Scribe_Values.Look(ref title, "title");
            Scribe_Values.Look(ref note, "note");
            Scribe_Collections.Look(ref imageIcons, "imageIcons", LookMode.Deep);
            Scribe_Values.Look(ref sourceSha256, "sourceSha256");
            Scribe_Values.Look(ref sourceBaselineFile, "sourceBaselineFile");
            Scribe_Values.Look(ref externalImage, "externalImage", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) imageIcons = PhotoImageIcons.Copy(imageIcons);
        }
    }

    public class PhotoRecord : IExposable
    {
        // Live shutter cancellation; cancelled unfinished records are removed, never serialized.
        internal bool captureCancelled;
        public bool favorite; // Album-wide choice; independent of tree pins and edited versions.
        public string id;
        public string reshootOf;   // id of the photo this one re-frames (null = an original)
        public string groupSession; // only repeated photographs of the same ordinary group gathering
        public bool groupRepresentative;
        public int archivedTicksAbs = -1; // when it was moved to the archive tab; -1 = it is in the album
        public bool Archived => archivedTicksAbs >= 0;
        public string file;        // file name (no directory); null until the image is written
        public string kind;        // wedding / party / ritual / baby / death / recruit / legendary / ship / mechboss / manual / test
        public string eventSubtype; // optional typed event detail (for example concert), older records remain generic
        public int eventAge = -1; // birthday age supplied by the event, never parsed from a translated title
        public bool eventEnded; // an internal after-outcome request; success/normal completion is not implied
        public string captionScene, captionTemplate; // selected once at a successful shutter, never re-evaluated on load
        public string captionPlace; // verified visited-place label, preserved for captions of new captures only
        public string sourceSha256, sourceBaselineFile; // known first image content, independent of later external file edits
        public string importedColonyId, importedColonyName; // provenance of relics; optional for older saves
        public string externalSourceHash, externalSourceFile; // explicit import provenance; never changes original event facts
        public List<string> captionValues = new List<string>(); // observed slot values, frozen along with the chosen sentence
        public string eventKey;    // dedup key, may be empty
        public string title;
        public string desc;
        public bool userDescription; // distinguishes an explicitly edited original description from generated legacy survey notes
        public string requiredPerson; // birthdays: refuse a later frame without its subject
        public List<string> eventSubjectIds = new List<string>(); // verified event actors, such as the marrying pair; never inferred from names
        public List<string> requiredSubjects = new List<string>(); // all living actors required for a pet photograph
        public int captureDeadlineTick = -1; // optional absolute game tick, preserved when a pending request is loaded
        public string requiredConditionDef; // a weather/sky photograph must still show this exact activation
        public int requiredConditionStartTick = -1;
        public bool automaticCapture; // request provenance; older pending photos stay unspecified rather than guessing from their kind
        public int captureReactionMode = -1; // -1 uses the mod default; a gathering can retain its own choice
        public List<string> reactionPawnIds = new List<string>();
        public List<int> reactionIcons = new List<int>(); // aligned per-pawn choices; independent of the photo inclusion mode
        public List<string> facePawnIds = new List<string>();
        public List<int> faceChoices = new List<int>();
        public bool generatedDesc;
        public bool eventOnly; // a verified event whose subject no longer exists for a photograph
        public bool deferredCapture; // requested while its map was not viewed; a later image is explicitly not the original moment
        public List<string> eventPeopleNames = new List<string>(), eventPeopleIds = new List<string>();
        public List<string> requiredThings = new List<string>();
        public string dateLabel;   // localized in-game date
        public string dateSlug;    // numeric yyyy-q-dd for file names
        public int ticksAbs;               // when the event happened
        public int capturedTicksAbs = -1;  // when the image was actually rendered (-1 = unknown, records from before v2.4)
        public int year;                   // colony calendar year of the event (album filters)
        public float spanCells;
        public float aspect = PhotoCapture.DefaultAspect;   // image width / height (records saved by v1/v2 have none = 3:2)
        public float Aspect => eventSubtype == "mf-survey-area"
            ? aspect > 0f && !float.IsInfinity(aspect) ? aspect : PhotoCapture.DefaultAspect
            : aspect > 0.01f ? aspect : PhotoCapture.DefaultAspect;
        public int mapId = -1;     // map uniqueID, so an uncaptured record can be retried after load
        public string locationName;
        public int locationTile = -1; // frozen known place, retained after a visited map is discarded
        public Vector3 center = new Vector3(-1f, 0f, -1f);   // world x/z of the capture centre
        public bool HasCenter => center.x >= 0f && center.z >= 0f;
        public List<string> people = new List<string>();       // display names at the time
        public List<string> peopleIds = new List<string>();    // pawn load ids (stable across renames); empty for records from before v2.4
        public List<PhotoEdit> edits = new List<PhotoEdit>();   // edited versions, oldest first
        public string shownEdit;   // the edit shown in the album, on frames and everywhere else; null = the original
        public List<PhotoSticker> stickers = new List<PhotoSticker>(); // album-margin decorations; never baked into the photo files

        /// <summary>The edited version standing in for this photo, or null when the original is shown.</summary>
        public PhotoEdit Shown
        {
            get
            {
                if (shownEdit.NullOrEmpty() || edits == null) return null;
                for (int i = 0; i < edits.Count; i++)
                    if (edits[i] != null && edits[i].id == shownEdit && edits[i].HasFile) return edits[i];
                return null;
            }
        }

        public PhotoEdit Edit(string editId)
        {
            if (editId.NullOrEmpty() || edits == null) return null;
            for (int i = 0; i < edits.Count; i++)
                if (edits[i] != null && edits[i].id == editId) return edits[i];
            return null;
        }

        /// <summary>Width / height of what is shown (the edit when there is one). The capture framing stays in Aspect, which re-shoots use.</summary>
        public float DisplayAspect => Shown?.aspect ?? Aspect;

        /// <summary>The title and sentence shown with the photo: the shown edit's own, when it has them, else the photo's.</summary>
        public string DisplayTitle => AlbumPresentation.Title(CaptionText.Clean(Shown != null && !Shown.title.NullOrEmpty() ? Shown.title : title));
        public string DisplayDesc => GeneratedCaptionDisplay.Text(this,
            Shown != null && !Shown.note.NullOrEmpty() ? Shown.note : desc, Shown != null && !Shown.note.NullOrEmpty());

        /// <summary>A version's title and sentence (null / "" = the original).</summary>
        public string TitleOf(string version) { var e = version.NullOrEmpty() ? null : Edit(version); return AlbumPresentation.Title(CaptionText.Clean(e != null && !e.title.NullOrEmpty() ? e.title : title)); }
        public string DescOf(string version) { var e = version.NullOrEmpty() ? null : Edit(version); return GeneratedCaptionDisplay.Text(this, e != null && !e.note.NullOrEmpty() ? e.note : desc, e != null && !e.note.NullOrEmpty()); }

        public bool HasFile => !file.NullOrEmpty();

        /// <summary>A deliberately supplied sentence must survive a pending capture's automatic context selection.</summary>
        public void SetDescription(string text)
        {
            desc = text ?? "";
            generatedDesc = false;
            userDescription = true;
        }

        /// <summary>Replaces the people list (a mid-ritual shot re-titled as the appointment it turned out to be).</summary>
        public void SetPeople(IEnumerable<Pawn> pawns)
        {
            var list = pawns == null ? new List<Pawn>() : pawns.Where(p => p != null).Distinct().ToList();
            people = list.Select(p => p.LabelShort).ToList();
            peopleIds = list.Select(p => p.GetUniqueLoadID()).ToList();
        }

        /// <summary>Taken well after the event (the map was not being viewed): the picture shows the venue later, not the moment.</summary>
        public bool Delayed => HasFile && (deferredCapture || capturedTicksAbs >= 0 && capturedTicksAbs - ticksAbs > 2500);

        /// <summary>Is this pawn in the photo? By id; records without ids fall back to the name.</summary>
        public bool Has(Pawn p)
        {
            if (p == null) return false;
            if (peopleIds.Count > 0) return peopleIds.Contains(p.GetUniqueLoadID());
            return people.Contains(p.LabelShort);
        }

        // Where its people stood in the picture: fractions of the ORIGINAL image, origin top-left (like an edit's crop). -1 = unknown
        // (photos taken before this was recorded, or nobody of the photo inside the frame).
        public float boxX0 = -1f, boxY0 = -1f, boxX1 = -1f, boxY1 = -1f;

        public bool HasPeopleBox => boxX0 >= 0f && boxX1 > boxX0 && boxY1 > boxY0;

        /// <summary>Measured when the picture is taken: the box around every spawned person of the photo inside the frame (a pawn is drawn about
        /// a cell wide and a little taller). Suggests which part to keep when a collage window trims the photo.</summary>
        public void MeasurePeople(Map map)
        {
            boxX0 = boxY0 = boxX1 = boxY1 = -1f;
            if (map == null || !HasCenter) return;
            float span = spanCells, w = span * Aspect, left = center.x - w / 2f, top = center.z + span / 2f;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
            bool any = false;
            foreach (var p in PhotoCapture.VisiblePawns(map))
            {
                if (!Has(p) || !PhotoCapture.VisualPosition(p, map, out Vector3 d)) continue;
                float a = (d.x - 0.45f - left) / w, b = (d.x + 0.45f - left) / w;
                float t = (top - (d.z + 0.75f)) / span, u = (top - (d.z - 0.45f)) / span;
                if (b <= 0f || a >= 1f || u <= 0f || t >= 1f) continue;
                any = true;
                x0 = Mathf.Min(x0, Mathf.Clamp01(a));
                x1 = Mathf.Max(x1, Mathf.Clamp01(b));
                y0 = Mathf.Min(y0, Mathf.Clamp01(t));
                y1 = Mathf.Max(y1, Mathf.Clamp01(u));
            }
            if (!any) return;
            boxX0 = x0;
            boxY0 = y0;
            boxX1 = x1;
            boxY1 = y1;
        }

        /// <summary>The people box in the image shown now (an edit crops the original), origin top-left; null when unknown or nobody is left inside.</summary>
        public Rect? ShownPeopleBox
        {
            get
            {
                if (!HasPeopleBox) return null;
                float x0 = boxX0, y0 = boxY0, x1 = boxX1, y1 = boxY1;
                var e = Shown;
                if (e != null && e.cw > 0f && e.ch > 0f)
                {
                    x0 = Mathf.Clamp01((x0 - e.cx) / e.cw);
                    x1 = Mathf.Clamp01((x1 - e.cx) / e.cw);
                    y0 = Mathf.Clamp01((y0 - e.cy) / e.ch);
                    y1 = Mathf.Clamp01((y1 - e.cy) / e.ch);
                    if (x1 <= x0 || y1 <= y0) return null;
                }
                return Rect.MinMaxRect(x0, y0, x1, y1);
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref reshootOf, "reshootOf");
            Scribe_Values.Look(ref archivedTicksAbs, "archivedTicksAbs", -1);
            Scribe_Values.Look(ref groupSession, "mfGroupSession");
            Scribe_Values.Look(ref groupRepresentative, "mfGroupRepresentative", false);
            Scribe_Values.Look(ref favorite, "mfFavorite", false);
            Scribe_Values.Look(ref file, "file");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref eventSubtype, "eventSubtype");
            Scribe_Values.Look(ref eventAge, "eventAge", -1);
            Scribe_Values.Look(ref eventEnded, "eventEnded", false);
            Scribe_Values.Look(ref captionScene, "captionScene");
            Scribe_Values.Look(ref captionTemplate, "captionTemplate");
            Scribe_Values.Look(ref captionPlace, "captionPlace");
            Scribe_Values.Look(ref sourceSha256, "sourceSha256");
            Scribe_Values.Look(ref sourceBaselineFile, "sourceBaselineFile");
            Scribe_Values.Look(ref importedColonyId, "importedColonyId");
            Scribe_Values.Look(ref importedColonyName, "importedColonyName");
            Scribe_Values.Look(ref externalSourceHash, "externalSourceHash");
            Scribe_Values.Look(ref externalSourceFile, "externalSourceFile");
            Scribe_Collections.Look(ref captionValues, "captionValues", LookMode.Value);
            Scribe_Values.Look(ref eventKey, "eventKey");
            Scribe_Values.Look(ref title, "title");
            Scribe_Values.Look(ref desc, "desc");
            Scribe_Values.Look(ref requiredPerson, "requiredPerson");
            Scribe_Collections.Look(ref eventSubjectIds, "eventSubjectIds", LookMode.Value);
            Scribe_Collections.Look(ref requiredSubjects, "requiredSubjects", LookMode.Value);
            Scribe_Values.Look(ref captureDeadlineTick, "captureDeadlineTick", -1);
            Scribe_Values.Look(ref requiredConditionDef, "requiredConditionDef");
            Scribe_Values.Look(ref requiredConditionStartTick, "requiredConditionStartTick", -1);
            Scribe_Values.Look(ref automaticCapture, "automaticCapture", false);
            Scribe_Values.Look(ref captureReactionMode, "captureReactionMode", -1);
            Scribe_Collections.Look(ref reactionPawnIds, "reactionPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref reactionIcons, "reactionIcons", LookMode.Value);
            Scribe_Collections.Look(ref facePawnIds,"facePawnIds",LookMode.Value);
            Scribe_Collections.Look(ref faceChoices,"faceChoices",LookMode.Value);
            Scribe_Values.Look(ref generatedDesc, "generatedDesc", false);
            Scribe_Values.Look(ref userDescription, "userDescription", false);
            Scribe_Values.Look(ref eventOnly, "eventOnly", false);
            Scribe_Values.Look(ref deferredCapture, "deferredCapture", false);
            Scribe_Collections.Look(ref eventPeopleNames, "eventPeopleNames", LookMode.Value);
            Scribe_Collections.Look(ref eventPeopleIds, "eventPeopleIds", LookMode.Value);
            Scribe_Collections.Look(ref requiredThings, "requiredThings", LookMode.Value);
            Scribe_Values.Look(ref dateLabel, "dateLabel");
            Scribe_Values.Look(ref dateSlug, "dateSlug");
            Scribe_Values.Look(ref ticksAbs, "ticksAbs", 0);
            Scribe_Values.Look(ref capturedTicksAbs, "capturedTicksAbs", -1);
            Scribe_Values.Look(ref year, "year", 0);
            Scribe_Collections.Look(ref peopleIds, "peopleIds", LookMode.Value);
            Scribe_Values.Look(ref spanCells, "spanCells", 13f);
            Scribe_Values.Look(ref aspect, "aspect", PhotoCapture.DefaultAspect);
            Scribe_Values.Look(ref mapId, "mapId", -1);
            Scribe_Values.Look(ref locationName, "locationName");
            Scribe_Values.Look(ref locationTile, "locationTile", -1);
            Scribe_Values.Look(ref center, "center", new Vector3(-1f, 0f, -1f));
            Scribe_Collections.Look(ref people, "people", LookMode.Value);
            Scribe_Collections.Look(ref edits, "edits", LookMode.Deep);
            Scribe_Values.Look(ref shownEdit, "shownEdit");
            Scribe_Collections.Look(ref stickers, "stickers", LookMode.Deep);
            Scribe_Values.Look(ref boxX0, "boxX0", -1f);
            Scribe_Values.Look(ref boxY0, "boxY0", -1f);
            Scribe_Values.Look(ref boxX1, "boxX1", -1f);
            Scribe_Values.Look(ref boxY1, "boxY1", -1f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (people == null) people = new List<string>();
                if (peopleIds == null) peopleIds = new List<string>();
                if (eventPeopleNames == null) eventPeopleNames = new List<string>();
                if (eventPeopleIds == null) eventPeopleIds = new List<string>();
                if (captionValues == null) captionValues = new List<string>();
                if (reactionPawnIds == null) reactionPawnIds = new List<string>();
                if (reactionIcons == null) reactionIcons = new List<int>();
                int reactionCount = System.Math.Min(reactionPawnIds.Count, reactionIcons.Count);
                if (reactionPawnIds.Count > reactionCount) reactionPawnIds.RemoveRange(reactionCount, reactionPawnIds.Count - reactionCount);
                if (reactionIcons.Count > reactionCount) reactionIcons.RemoveRange(reactionCount, reactionIcons.Count - reactionCount);
                if (eventSubjectIds == null) eventSubjectIds = new List<string>();
                if (requiredSubjects == null) requiredSubjects = new List<string>();
                if (edits == null) edits = new List<PhotoEdit>();
                edits.RemoveAll(e => e == null);
                if (stickers == null) stickers = new List<PhotoSticker>();
                AlbumStickers.Normalize(stickers);
            }
        }
    }
}
