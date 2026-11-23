using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class Dialog_PhotoStorage : Window
    {
        private readonly PhotoStore store;
        private readonly string onlyRecord;
        private List<PhotoFileReference> references = new List<PhotoFileReference>();
        private List<PhotoFileStatus> rows = new List<PhotoFileStatus>();
        private Dictionary<string, PhotoFileMatch> matches = new Dictionary<string, PhotoFileMatch>();
        private CancellationTokenSource cancel;
        private Task<List<PhotoFileStatus>> scan;
        private Task<List<PhotoFileMatch>> search;
        private Task<string> restoring;
        private PhotoFileReference restoreReference;
        private string restoreHash, folder = "", status = "";
        private int scanned;
        private bool showAll, findFolder, closed, scanSucceeded;
        private Vector2 scroll;
        internal bool Busy => scan != null || search != null || restoring != null;
        internal IReadOnlyList<PhotoFileStatus> Results => rows;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1080f, UI.screenWidth - 40f), Mathf.Min(800f, UI.screenHeight - 40f));
        internal Dialog_PhotoStorage(string recordId = null)
        { store = PhotoStore.Instance; onlyRecord = recordId; doCloseX = true; absorbInputAroundWindow = true; forcePause = false; closeOnAccept = false; }
        public override void PreOpen() { base.PreOpen(); Refresh(); }
        public override void PostClose()
        {
            closed = true; cancel?.Cancel();
            var task = restoring; var reference = restoreReference;
            if (task != null) task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) Cleanup(reference, t.Result); });
            base.PostClose();
        }
        private static void Cleanup(PhotoFileReference r, string file)
        { try { PhotoStorageFiles.DiscardOwnedCopy(r, file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        internal void Refresh()
        {
            if (Busy || store == null || !ReferenceEquals(store, PhotoStore.Instance)) return;
            cancel?.Dispose(); cancel = new CancellationTokenSource(); var token = cancel.Token;
            references = PhotoStorage.References(store, onlyRecord); rows.Clear(); matches.Clear(); status = ""; scanned = 0; scanSucceeded = false;
            var snapshot = references.ToArray();
            scan = Task.Run(() => PhotoStorageFiles.Scan(snapshot, token, n => Interlocked.Exchange(ref scanned, n)), token);
        }
        internal void SearchFolder(string path)
        {
            if (Busy) return;
            var wanted = rows.Where(r => r.State == PhotoFileState.Missing || r.State == PhotoFileState.Unreadable).Select(r => r.Reference).ToList();
            string input = path.Trim().Trim('"'); var token = cancel.Token;
            search = Task.Run(() => PhotoStorageFiles.Find(input, wanted, token), token); status = "";
        }
        internal void Restore(PhotoFileStatus row)
        {
            if (Busy) return;
            if (!PhotoStorage.Current(store, row.Reference)) { status = "MF_StorageChanged".Translate(); return; }
            string source = row.AvailablePath, hash = row.VerifiedHash;
            if (matches.TryGetValue(row.Reference.Key, out var match)) { source = match.Path; hash = row.Reference.Hash; }
            if (source == null || !PhotoStorageFiles.HashKnown(hash)) return;
            restoreReference = row.Reference; restoreHash = hash; var token = cancel.Token;
            restoring = Task.Run(() => PhotoStorageFiles.Restore(row.Reference, source, hash, token), token);
        }
        private static string Failure(Task task) => task.IsCanceled ? "MF_StorageCancelled".Translate().ToString()
            : "MF_StorageFailed".Translate().ToString() + "\n" + task.Exception?.GetBaseException().Message;
        public override void WindowUpdate()
        {
            base.WindowUpdate(); if (closed) return;
            if (scan?.IsCompleted == true)
            {
                if (scan.Status == TaskStatus.RanToCompletion) { rows = scan.Result.OrderBy(r => r.State == PhotoFileState.Ready ? 1 : 0).ToList(); scanSucceeded = true; }
                else status = Failure(scan); scan = null;
            }
            if (search?.IsCompleted == true)
            {
                if (search.Status == TaskStatus.RanToCompletion)
                { matches = search.Result.ToDictionary(m => m.Reference.Key); status = "MF_StorageFound".Translate(matches.Count).ToString(); }
                else status = Failure(search); search = null;
            }
            if (restoring?.IsCompleted == true)
            {
                var task = restoring; restoring = null;
                if (task.Status != TaskStatus.RanToCompletion) status = Failure(task);
                else if (!PhotoStorage.Connect(store, restoreReference, task.Result, restoreHash))
                { Cleanup(restoreReference, task.Result); status = "MF_StorageChanged".Translate(); }
                else { Refresh(); status = "MF_StorageRestored".Translate(); }
            }
        }
        internal static void OpenFolder(PhotoStore store)
        {
            if (store == null) return;
            string path = store.FolderPath;
            while (!Directory.Exists(path) && !string.IsNullOrEmpty(Path.GetDirectoryName(path))) path = Path.GetDirectoryName(path);
            if (Directory.Exists(path)) Application.OpenURL(new Uri(Path.GetFullPath(path)).AbsoluteUri);
        }
        public override void DoWindowContents(Rect rect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect); Rect page = rect.ContractedBy(20f); float control = SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - 35f, control + 10f), "MF_StorageTitle".Translate());
                float y = page.y + control + 14f;
                int photos = references.Select(r => r.RecordId).Distinct().Count();
                int missing = rows.Where(r => r.State == PhotoFileState.Missing || r.State == PhotoFileState.Unreadable).Select(r => r.Reference.RecordId).Distinct().Count();
                string summary = restoring != null ? "MF_StorageRestoring".Translate().ToString()
                    : search != null ? "MF_StorageSearching".Translate().ToString()
                    : scan != null ? "MF_StorageProgress".Translate(Volatile.Read(ref scanned), references.Count).ToString()
                    : "MF_StorageSummary".Translate(photos, missing, rows.Count(r => r.State == PhotoFileState.ProtectedCopy)).ToString();
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, control), AlbumPresentation.Title(summary)); y += control + 4f;
                string help = "MF_StorageHint".Translate(); float hintH = AlbumStyle.TextHeight(help, page.width) + 8f;
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, hintH), help, true); y += hintH + 8f;
                float third = (page.width - 16f) / 3f;
                if (SharedPhotoUI.Button(new Rect(page.x, y, third, control), "MF_StorageRescan".Translate(), !Busy)) Refresh();
                if (SharedPhotoUI.Button(new Rect(page.x + third + 8f, y, third, control), "MF_OpenFolder".Translate())) OpenFolder(store);
                if (SharedPhotoUI.Button(new Rect(page.x + 2f * (third + 8f), y, third, control), "MF_StorageFind".Translate(), !Busy)) findFolder = !findFolder;
                y += control + 8f;
                if (findFolder)
                {
                    var field = new Rect(page.x, y, page.width - 155f, control);
                    folder = AlbumBridgeUI.TextField(field, folder, "MF_StorageFolderHint");
                    if (folder.NullOrEmpty() && GUI.GetNameOfFocusedControl() != "MF_StorageFolderHint")
                        SharedPhotoUI.Label(field.ContractedBy(8f), "MF_StorageFolderHint".Translate(), true);
                    if (SharedPhotoUI.Button(new Rect(page.xMax - 147f, y, 147f, control), "MF_UX_SearchFolder".Translate(), !Busy && !folder.NullOrEmpty())) SearchFolder(folder);
                    y += control + 5f;
                    string searchHelp = "MF_StorageSearchHint".Translate(); float sh = AlbumStyle.TextHeight(searchHelp, page.width) + 6f;
                    SharedPhotoUI.Label(new Rect(page.x, y, page.width, sh), searchHelp, true); y += sh;
                }
                if (SharedPhotoUI.QuietButton(new Rect(page.x, y, page.width, control), (showAll ? "MF_StorageProblems" : "MF_StorageAll").Translate())) showAll = !showAll;
                y += control + 6f;
                float statusH = status.NullOrEmpty() ? 0f : Mathf.Min(AlbumStyle.TextHeight(status, page.width) + 8f, control * 2.5f);
                Rect viewport = new Rect(page.x, y, page.width, Mathf.Max(20f, page.yMax - y - statusH));
                var visible = rows.Where(r => showAll || r.State != PhotoFileState.Ready).ToList();
                float width = viewport.width - 20f, actionW = Mathf.Min(210f, width * .29f);
                float rowH = Mathf.Max(110f, control * 2f + 22f);
                Widgets.BeginScrollView(viewport, ref scroll, new Rect(0f, 0f, width, Mathf.Max(viewport.height, visible.Count * (rowH + 8f))));
                try
                {
                    if (visible.Count == 0 && !Busy && scanSucceeded) SharedPhotoUI.Label(new Rect(8f, 8f, width - 16f, control * 2f), "MF_StorageNoProblems".Translate());
                    for (int i = 0; i < visible.Count; i++)
                    {
                        Rect row = new Rect(0f, i * (rowH + 8f), width, rowH);
                        if (row.yMax < scroll.y || row.y > scroll.y + viewport.height) continue;
                        var item = visible[i]; var r = item.Reference;
                        var tint = AlbumStyle.Theme.Accent; tint.a = .1f; Widgets.DrawBoxSolid(row, tint);
                        Rect title = new Rect(10f, row.y + 8f, width - actionW - 25f, control);
                        SharedPhotoUI.Label(title, AlbumPresentation.Title(r.Title)); TooltipHandler.TipRegion(title, r.Title + "\n" + r.Date + "\n" + r.File);
                        string version = (r.EditId == null ? "MF_StorageOriginal" : "MF_StorageEdit").Translate();
                        string label = version + " / " + (matches.ContainsKey(r.Key) ? "MF_StorageStateFound" : "MF_StorageState" + item.State).Translate();
                        SharedPhotoUI.Label(new Rect(10f, title.yMax + 4f, title.width, rowH - control - 18f), label, true);
                        if (item.Detail != null) TooltipHandler.TipRegion(row, item.Detail);
                        bool canRestore = item.AvailablePath != null && item.State == PhotoFileState.ProtectedCopy || matches.ContainsKey(r.Key);
                        if (canRestore && SharedPhotoUI.Button(new Rect(width - actionW - 8f, row.y + 12f, actionW, control), "MF_StorageRestore".Translate(), !Busy)) Restore(item);
                    }
                }
                finally { Widgets.EndScrollView(); }
                if (statusH > 0f) SharedPhotoUI.Label(new Rect(page.x, page.yMax - statusH, page.width, statusH), AlbumPresentation.Title(status), true);
            }
        }
    }
}
