using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MomentFrames
{
    /// <summary>One photo file in another game's folder, as far as it can be identified (from that game's index.tsv, else from the file name).</summary>
    public class RelicPhoto
    {
        public string file;        // file name inside the source folder
        public string thumbFile;   // its thumbnail, or null when that game was played before thumbnails existed
        public string title = "", desc = "", kind = "", dateSlug = "", dateLabel = "";
        public int year;
        public float aspect;       // 0 = unknown
    }

    /// <summary>Another game's photo folder (same root as this game's, different game id).</summary>
    public class RelicSource
    {
        internal readonly HashSet<string> archivedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string folder, gameId, name = "";
        public bool fromIndex;
        public List<RelicPhoto> photos = new List<RelicPhoto>();

        /// <summary>Dedup key stored in the imported record's eventKey, so a photo can be brought over only once.</summary>
        public string Key(RelicPhoto p) => "relic:" + gameId + ":" + p.file;
        public string FullPath(RelicPhoto p) => Path.Combine(folder, p.file);
        public string ThumbPath(RelicPhoto p) => Path.Combine(folder, p.thumbFile ?? Path.GetFileNameWithoutExtension(p.file) + ".thumb.png");
    }

    /// <summary>Photos of an earlier colony (another save) brought into this one. The records of that game live in its save file, so each game writes a
    /// small index.tsv beside its photos whenever it is saved; folders from before that are read from the file names. Imported photos are "relics":
    /// no people (pawn ids are local to a save — the same number here is somebody else), never a milestone, dated the day they arrive.</summary>
    public static class RelicImport
    {
        public const string IndexFile = "index.tsv";
        private const string ArchivedLine = "#archived";
        // 0007_5502-3-11_wedding_Title-words_1a2b3c4d.png
        private static readonly Regex NamePattern = new Regex(@"^(\d{4})_(\d{4}-\d-\d{2})_([^_]+)_(.*)_([0-9a-f]{8})\.(png|jpg)$", RegexOptions.IgnoreCase);

        /// <summary>Writes index.tsv for this game: colony name, then one line per photo (file, kind, date, title, caption, year, aspect). Called on save; never throws.</summary>
        public static void WriteIndex(PhotoStore store)
        {
            try
            {
                var recs = store.Records.Where(r => r.HasFile).ToList();
                var archived = store.ArchivedRecords.Where(r => r.HasFile).ToList();
                if (recs.Count == 0 && archived.Count == 0) return;
                Directory.CreateDirectory(store.FolderPath);
                var sb = new StringBuilder();
                sb.Append("#MomentFrames\t").Append(Clean(ColonyName())).Append('\t').Append(store.GameId).Append('\n');
                foreach (var r in recs)
                    sb.Append(Clean(r.file)).Append('\t').Append(Clean(r.kind)).Append('\t').Append(Clean(r.dateSlug)).Append('\t').Append(Clean(r.dateLabel)).Append('\t')
                      .Append(Clean(r.title)).Append('\t').Append(Clean(r.desc)).Append('\t').Append(r.year).Append('\t').Append(r.Aspect.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
                // The files stay in the folder: without this line another game would read them back by file name.
                foreach (var r in archived)
                    foreach (string file in new[] { r.file }.Concat(r.edits.Where(e => e.HasFile).Select(e => e.file)))
                        sb.Append(ArchivedLine).Append('\t').Append(Clean(file)).Append('\n');
                File.WriteAllText(Path.Combine(store.FolderPath, IndexFile), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] could not write the photo index: " + e.Message);
            }
        }

        /// <summary>The settlement's name (home map), else the player faction's name.</summary>
        public static string ColonyName()
        {
            try
            {
                string settlement = Find.AnyPlayerHomeMap?.Parent?.Label;
                if (!settlement.NullOrEmpty()) return settlement;
                return Faction.OfPlayer?.Name ?? "";
            }
            catch { return ""; }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        /// <summary>Every other game's folder beside this game's that holds at least one photo. Never the current game.</summary>
        public static List<RelicSource> Sources(PhotoStore store)
        {
            var list = new List<RelicSource>();
            string root = Path.GetDirectoryName(store.FolderPath);
            if (root.NullOrEmpty() || !Directory.Exists(root)) return list;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string id = Path.GetFileName(dir);
                if (string.Equals(id, store.GameId, StringComparison.OrdinalIgnoreCase)) continue;
                var src = Read(dir, id);
                if (src != null && src.photos.Count > 0) list.Add(src);
            }
            return list.OrderBy(s => s.name).ThenBy(s => s.gameId).ToList();
        }

        /// <summary>Reads one game folder: index.tsv when present, plus any photo the index does not know, identified from its file name.</summary>
        public static RelicSource Read(string folder, string gameId)
        {
            try
            {
                var src = new RelicSource { folder = folder, gameId = gameId };
                string index = Path.Combine(folder, IndexFile);
                if (File.Exists(index))
                {
                    ParseIndex(File.ReadAllLines(index), src);
                    src.fromIndex = true;
                }
                var known = new HashSet<string>(src.photos.Select(p => p.file).Concat(src.archivedFiles), StringComparer.OrdinalIgnoreCase);
                foreach (var path in PhotoImageFormat.ImageFiles(folder))
                {
                    string file = Path.GetFileName(path);
                    if (known.Contains(file)) continue;
                    var p = FromName(file);
                    if (p != null) { src.photos.Add(p); known.Add(file); }
                }
                src.photos.RemoveAll(p => p.file.NullOrEmpty() || !File.Exists(Path.Combine(folder, p.file)));
                foreach (var p in src.photos)
                {
                    string t = Path.GetFileNameWithoutExtension(p.file) + ".thumb.png";
                    p.thumbFile = File.Exists(Path.Combine(folder, t)) ? t : null;
                }
                src.photos = src.photos.OrderBy(p => p.dateSlug).ThenBy(p => p.file).ToList();
                if (src.name.NullOrEmpty()) src.name = "MF_RelicUnknownColony".Translate(gameId).ToString();
                return src;
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] could not read the photo folder " + folder + ": " + e.Message);
                return null;
            }
        }

        public static void ParseIndex(string[] lines, RelicSource src)
        {
            foreach (var line in lines)
            {
                if (line.NullOrEmpty()) continue;
                var f = line.Split('\t');
                if (line.StartsWith(ArchivedLine))
                {
                    if (f.Length > 1) src.archivedFiles.Add(f[1]);
                    continue;
                }
                if (line.StartsWith("#MomentFrames"))
                {
                    if (f.Length > 1) src.name = f[1].Trim();
                    continue;
                }
                if (f.Length < 6) continue;
                var p = new RelicPhoto { file = f[0], kind = f[1], dateSlug = f[2], dateLabel = f[3], title = f[4], desc = f[5] };
                if (f.Length > 6) int.TryParse(f[6], out p.year);
                if (f.Length > 7) float.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out p.aspect);
                src.photos.Add(p);
            }
        }

        /// <summary>Identify a photo from its file name alone (the title slug had its spaces turned into dashes).</summary>
        public static RelicPhoto FromName(string file)
        {
            var m = NamePattern.Match(file);
            if (!m.Success) return null;
            string title = m.Groups[4].Value.Replace('-', ' ').Trim();
            string kind = m.Groups[3].Value;
            return new RelicPhoto { file = file, dateSlug = m.Groups[2].Value, dateLabel = m.Groups[2].Value, kind = kind, title = title.Length == 0 || title == "x" ? kind : title };
        }
    }

    /// <summary>Album button "old colonies": the other games' folders on the left, their photos on the right; click a photo (or "import all") to copy
    /// it into this game as a relic record.</summary>
    public class Dialog_ImportRelics : Window
    {
        private List<RelicSource> sources = new List<RelicSource>();
        private int selected;
        private Vector2 scrollLeft, scrollRight;
        private HashSet<string> imported = new HashSet<string>();
        private int importedVersion = -1;
        private const float CellW = 208f, CellH = 176f, Pad = 8f, LeftW = 270f, RowH = 54f;

        public override Vector2 InitialSize => new Vector2(1080f, 700f);

        public Dialog_ImportRelics()
        {
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            var store = PhotoStore.Instance;
            sources = store == null ? new List<RelicSource>() : RelicImport.Sources(store);
            selected = 0;
            importedVersion = -1;
        }

        public int SourceCount => sources.Count;

        private HashSet<string> Imported(PhotoStore store)
        {
            if (importedVersion != store.Version)
            {
                importedVersion = store.Version;
                imported = new HashSet<string>(store.AllRecords.Where(r => r.kind == Kinds.Relic && !r.eventKey.NullOrEmpty()).Select(r => r.eventKey));
            }
            return imported;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 34f), "MF_RelicTitle".Translate());
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width - 40f, 46f), "MF_RelicsDesc".Translate());
            GUI.color = Color.white;
            Rect body = new Rect(inRect.x, inRect.y + 84f, inRect.width, inRect.height - 84f - 50f);
            var store = PhotoStore.Instance;
            if (store == null || sources.Count == 0)
            {
                string root = store == null ? "" : Path.GetDirectoryName(store.FolderPath);
                Widgets.Label(body, "MF_RelicNoSources".Translate(root));
                return;
            }
            var done = Imported(store);
            // Left: the other colonies.
            Rect left = new Rect(body.x, body.y, LeftW, body.height);
            Rect lview = new Rect(0f, 0f, LeftW - 16f, sources.Count * RowH);
            Widgets.BeginScrollView(left, ref scrollLeft, lview);
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                Rect row = new Rect(0f, i * RowH, lview.width, RowH - 4f);
                Widgets.DrawBoxSolid(row, i == selected ? new Color(0.25f, 0.35f, 0.25f) : new Color(0.15f, 0.15f, 0.15f));
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                int have = src.photos.Count(p => done.Contains(src.Key(p)));
                Text.Font = GameFont.Tiny;
                Widgets.Label(row.ContractedBy(6f), "MF_RelicSource".Translate(src.name, src.photos.Count, have));
                Text.Font = GameFont.Small;
                TooltipHandler.TipRegion(row, src.folder);
                if (Widgets.ButtonInvisible(row)) { selected = i; scrollRight = Vector2.zero; }
            }
            Widgets.EndScrollView();
            // Right: that colony's photos.
            selected = Mathf.Clamp(selected, 0, sources.Count - 1);
            var cur = sources[selected];
            Rect right = new Rect(left.xMax + 12f, body.y, body.width - LeftW - 12f, body.height);
            int cols = Mathf.Max(1, (int)((right.width - 16f) / (CellW + Pad)));
            int rows = (cur.photos.Count + cols - 1) / cols;
            Rect view = new Rect(0f, 0f, right.width - 16f, rows * (CellH + Pad));
            Widgets.BeginScrollView(right, ref scrollRight, view);
            int first = Mathf.Max(0, (int)(scrollRight.y / (CellH + Pad))) * cols;   // only the rows in view: each cell loads a thumbnail
            int last = Mathf.Min(cur.photos.Count, ((int)((scrollRight.y + right.height) / (CellH + Pad)) + 1) * cols);
            for (int i = first; i < last; i++)
            {
                var p = cur.photos[i];
                bool has = done.Contains(cur.Key(p));
                int r = i / cols, c = i % cols;
                Rect cell = new Rect(c * (CellW + Pad), r * (CellH + Pad), CellW, CellH);
                Widgets.DrawBoxSolid(cell, has ? new Color(0.22f, 0.32f, 0.22f) : new Color(0.15f, 0.15f, 0.15f));
                if (!has && Mouse.IsOver(cell)) Widgets.DrawHighlight(cell);
                Rect img = new Rect(cell.x + 8f, cell.y + 8f, CellW - 16f, (CellW - 16f) / 1.5f);
                var tex = PhotoTextureCache.Thumb(cur.ThumbPath(p), cur.FullPath(p));
                if (tex != null) Widgets.DrawTextureFitted(img, tex, 1f);
                else Widgets.Label(img, "MF_MissingFile".Translate());
                Text.Font = GameFont.Tiny;
                string caption = p.title + "\n" + (p.dateLabel.NullOrEmpty() ? p.dateSlug : p.dateLabel) + (has ? " — " + "MF_RelicImported".Translate() : "");
                Widgets.Label(new Rect(cell.x + 8f, img.yMax + 4f, CellW - 16f, 40f), caption);
                Text.Font = GameFont.Small;
                if (Mouse.IsOver(cell)) TooltipHandler.TipRegion(cell, p.title + "\n" + MainTabWindow_Album.KindLabel(p.kind) + " · " + (p.dateLabel.NullOrEmpty() ? p.dateSlug : p.dateLabel) + (p.desc.NullOrEmpty() ? "" : "\n" + p.desc) + "\n" + (has ? "MF_RelicImported".Translate() : "MF_RelicClickToImport".Translate()));
                if (!has && Widgets.ButtonInvisible(cell)) Import(store, cur, p);
            }
            Widgets.EndScrollView();
            // Bottom: import everything of this colony.
            int left2 = cur.photos.Count(p => !done.Contains(cur.Key(p)));
            if (left2 > 0 && Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 35f, 260f, 32f), "MF_RelicImportAll".Translate(left2)))
            {
                int n = 0;
                foreach (var p in cur.photos.Where(p => !done.Contains(cur.Key(p))).ToList())
                    if (store.ImportRelic(cur, p) != null) n++;
                Report(n);
            }
        }

        private void Import(PhotoStore store, RelicSource src, RelicPhoto p)
        {
            var rec = store.ImportRelic(src, p);
            if (rec != null) SoundDefOf.Click.PlayOneShotOnCamera();
            Report(rec != null ? 1 : 0);
        }

        private static void Report(int n)
        {
            if (n > 0) Messages.Message("MF_RelicImportedMsg".Translate(n), MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
