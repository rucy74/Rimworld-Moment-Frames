using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
#if !BRIDGE_POLICY_TEST
using RimWorld;
using UnityEngine;
using Verse;
#endif

namespace MomentFrames
{
    internal sealed class PhotoBridgeSubmission
    {
        internal bool Finished { get; private set; }
        internal bool Uncertain { get; private set; }
        internal bool? Submit(Func<bool> call)
        {
            if (Finished) return null;
            Finished = true;
            try { bool accepted = call(); if (!accepted) Finished = false; return accepted; }
            catch { Uncertain = true; throw; } // A late failure might follow acceptance. Manual re-entry is blocked too.
        }
    }
    /// <summary>Optional mods are called through exact public contracts. No patches, private members, or capture replacement.</summary>
    internal sealed class PhotoBridgeContracts
    {
        private readonly MethodInfo publish, generate;
        private readonly ConstructorInfo request;
        private readonly PropertyInfo hoursLeft;
        private readonly FieldInfo monologue;
        private readonly object free, user;
        internal bool HasRimGram => publish != null;
        internal bool HasRimTalk => generate != null && request != null;
        internal object PublishedPost { get; private set; }
        internal PhotoBridgeContracts(Func<string, Type> resolve, Type pawn, Type faction)
        {
            var colony = resolve("RimGram.ColonyPage"); var tone = resolve("RimGram.ColonyTone");
            if (colony?.IsVisible == true && tone?.IsVisible == true && tone.IsEnum && Enum.IsDefined(tone, "Free"))
            {
                var method = colony.GetMethod("Publish", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { tone, faction, typeof(string), typeof(bool), typeof(string) }, null);
                var post = resolve("RimGram.Post");
                var parameters = method?.GetParameters();
                // Match the local-post contract, not a particular mod version or DLL build.
                // typed=false opts out of RimGram's online queue; photoJpg supplies our selected image.
                if (post?.IsVisible == true && method?.ReturnType == post
                    && parameters[3].Name == "typed" && parameters[4].Name == "photoJpg")
                {
                    publish = method; free = Enum.Parse(tone, "Free");
                    var prop = colony.GetProperty("HoursLeft", BindingFlags.Public | BindingFlags.Static);
                    if (prop?.PropertyType == typeof(float) && prop.GetGetMethod(false) != null) hoursLeft = prop;
                }
            }
            var talk = resolve("RimTalk.Service.TalkService"); var req = resolve("RimTalk.Data.TalkRequest");
            var kind = resolve("RimTalk.Source.Data.TalkType");
            if (talk?.IsVisible != true || req?.IsVisible != true || kind?.IsVisible != true || !kind.IsEnum || !Enum.IsDefined(kind, "User")) return;
            var ctor = req.GetConstructor(new[] { typeof(string), pawn, pawn, kind });
            var call = talk.GetMethod("GenerateTalk", BindingFlags.Public | BindingFlags.Static, null, new[] { req }, null);
            var field = req.GetField("IsMonologue", BindingFlags.Public | BindingFlags.Instance);
            if (ctor == null || call?.ReturnType != typeof(bool) || field?.FieldType != typeof(bool) || field.IsInitOnly) return;
            request = ctor; generate = call; monologue = field; user = Enum.Parse(kind, "User");
        }
        internal float HoursLeft => hoursLeft == null ? 0f : Math.Max(0f, (float)hoursLeft.GetValue(null, null));
        internal bool PublishLocal(string text, string jpgBase64)
        {
            if (!HasRimGram || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(jpgBase64)) return false;
            // typed=false is deliberate: current RimGram Online.OfferOwn enqueues only typed posts.
            // The JPG is copied by value into its post; neither mod owns the other mod's source file.
            PublishedPost = publish.Invoke(null, new[] { free, null, (object)text.Trim(), false, jpgBase64 });
            return PublishedPost != null;
        }
        internal bool RequestComment(object pawn, string prompt)
        {
            if (!HasRimTalk || pawn == null || string.IsNullOrWhiteSpace(prompt)) return false;
            var value = request.Invoke(new[] { (object)prompt.Trim(), pawn, null, user });
            monologue.SetValue(value, true);
            // RimTalk decides whether its current provider/settings can accept the request.
            // No photo data, image API call, background queue, cancellation of its current request, or automatic retries.
            return (bool)generate.Invoke(null, new[] { value });
        }
    }

    /// <summary>An immutable selection of one displayed version. Export writes new files outside the album, never the source.</summary>
    internal sealed class PhotoBridgeSelection
    {
        internal string Folder, Path, Id, Version, Title, Date, Description, ExpectedSha256;
        internal string[] People;
        internal bool ValidPath
        {
            get
            {
                if (string.IsNullOrEmpty(Folder) || string.IsNullOrEmpty(Path)) return false;
                try
                {
                    string root = System.IO.Path.GetFullPath(Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                        + System.IO.Path.DirectorySeparatorChar;
                    string full = System.IO.Path.GetFullPath(Path);
                    string extension = System.IO.Path.GetExtension(full);
                    return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase))
                        && File.Exists(full) && new FileInfo(full).Length > 0 && new FileInfo(full).Length <= 64 * 1024 * 1024;
                }
                catch { return false; }
            }
        }
        internal string Draft => string.Join("\n", new[] { Title, Date, Description }.Where(x => !string.IsNullOrWhiteSpace(x)));
        internal string Export(string destinationRoot, string caption = null)
        {
            if (!ValidPath) throw new IOException("The selected photo is unavailable.");
            string root = System.IO.Path.GetFullPath(destinationRoot);
            string sourceRoot = System.IO.Path.GetFullPath(Folder).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            // Even an accidentally supplied album root cannot turn an export into an original/edit write.
            if (root.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) || root.StartsWith(sourceRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Exports must be outside the album folder.");
            string directory = System.IO.Path.Combine(root, "photo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                byte[] bytes = File.ReadAllBytes(Path);
                string hash = Hash(bytes);
                if (!string.IsNullOrEmpty(ExpectedSha256) && !hash.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The selected image changed after review.");
                string image = "photo" + PhotoImageFormat.ExtensionOf(bytes);
                WriteNew(System.IO.Path.Combine(directory, image), bytes);
                string json = "{\n  \"format\": \"MomentFrames-selected-photo-v1\",\n  \"photoId\": " + Json(Id)
                    + ",\n  \"versionId\": " + Json(Version ?? "") + ",\n  \"title\": " + Json(Title) + ",\n  \"date\": " + Json(Date)
                    + ",\n  \"description\": " + Json(Description) + ",\n  \"people\": [" + string.Join(",", (People ?? Array.Empty<string>()).Select(Json))
                    + "],\n  \"shareText\": " + Json(caption ?? Draft) + ",\n  \"image\": " + Json(image) + ",\n  \"sha256\": " + Json(hash) + "\n}\n";
                WriteNew(System.IO.Path.Combine(directory, "manifest.json"), new UTF8Encoding(false).GetBytes(json));
                WriteNew(System.IO.Path.Combine(directory, "caption.txt"), new UTF8Encoding(false).GetBytes(caption ?? Draft));
                return directory;
            }
            catch
            {
                // Only this freshly created GUID directory is owned here. A partial export never touches the album.
                try { Directory.Delete(directory, true); } catch { }
                throw;
            }
        }
        private static void WriteNew(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
        }
        internal static string Json(string value)
        {
            if (value == null) return "null";
            var b = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '\\' || c == '"') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
        /// <summary>A PNG or JPEG within the size bounds (the name is from when every original was a PNG).</summary>
        internal static bool DecodablePng(byte[] bytes, int maximumSide = 8192)
        {
            // Bound image dimensions before Unity allocates the decoded texture.
            if (bytes == null || bytes.Length < 24 || bytes.Length > 64 * 1024 * 1024) return false;
            if (PhotoJpeg.Size(bytes, out int jpegWidth, out int jpegHeight))
                return jpegWidth <= maximumSide && jpegHeight <= maximumSide && jpegWidth <= 8192 && jpegHeight <= 8192;
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) return false;
            if (bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82) return false;
            long width = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
            long height = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0 && width <= maximumSide && height <= maximumSide && width <= 8192 && height <= 8192;
        }
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }

#if !BRIDGE_POLICY_TEST
    internal static class OptionalPhotoBridges
    {
        private static readonly Dictionary<string, Type> loadedTypes = new Dictionary<string, Type>();
        internal static Type LoadedType(string name)
        {
            // This UI is first used after mod loading has finished. Avoid assembly scans on every gallery repaint.
            if (loadedTypes.TryGetValue(name, out var known)) return known;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var type = assembly.GetType(name, false); if (type != null) { loadedTypes[name] = type; return type; } }
                catch { } // An optional assembly with unresolved dependencies does not disable the album.
            }
            loadedTypes[name] = null; return null;
        }
        internal static PhotoBridgeContracts Contracts()
        {
            try
            {
                return new PhotoBridgeContracts(LoadedType, typeof(Pawn), typeof(Faction));
            }
            catch (Exception e) { Log.Warning("[MomentFrames] optional public photo API unavailable: " + e.Message); return new PhotoBridgeContracts(_ => null, typeof(Pawn), typeof(Faction)); }
        }
        internal static bool RimGramInstalled => LoadedType("RimGram.ColonyPage") != null;
        internal static bool RimTalkInstalled => LoadedType("RimTalk.Service.TalkService") != null;
        internal static void AddMenu(List<FloatMenuOption> options, PhotoStore store, PhotoRecord photo, string version = null)
        {
            if (options == null || store == null || photo?.HasFile != true) return;
            if (RimGramInstalled) options.Add(new FloatMenuOption("MF_Bridge_ShareMenu".Translate(), () => Find.WindowStack.Add(new Dialog_MFPhotoBridge(store, photo, version, false))));
            if (LoadedType("RimTalk.Service.TalkService") != null)
                options.Add(new FloatMenuOption("MF_Bridge_TalkMenu".Translate(), () => Find.WindowStack.Add(new Dialog_MFPhotoBridge(store, photo, version, true))));
        }
        internal static bool OpenRimGram()
        {
            return OptionalRimGramWindow.Open();
        }
    }

    /// <summary>Every invocation is a reviewed, explicit button press. Closing/loading never publishes or starts AI.</summary>
    internal sealed class Dialog_MFPhotoBridge : Window
    {
        private readonly PhotoStore owner;
        private readonly PhotoRecord photo;
        private readonly PhotoBridgeSelection selection;
        private readonly bool talk;
        private readonly bool exportOnly;
        private readonly PhotoBridgeContracts contracts;
        private Texture2D preview;
        private string previewPath, previewSha256, imageProblem;
        private Pawn speaker;
        private string draft, status;
        private readonly PhotoBridgeSubmission submission = new PhotoBridgeSubmission();
        private bool finished => submission.Finished;
        private Vector2 scroll;
        private bool exportExpanded;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(760f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 40f));
        internal Dialog_MFPhotoBridge(PhotoStore owner, PhotoRecord photo, string version, bool talk) : this(owner, photo, version, talk, false) { }
        internal Dialog_MFPhotoBridge(PhotoStore owner, PhotoRecord photo, string version, bool talk, bool exportOnly)
        {
            this.owner = owner; this.photo = photo; this.talk = talk; this.exportOnly = exportOnly; exportExpanded = exportOnly;
            contracts = OptionalPhotoBridges.Contracts();
            string chosen = version ?? photo.Shown?.id ?? "";
            selection = new PhotoBridgeSelection { Folder = owner.FolderPath, Path = owner.VersionPath(photo, chosen), Id = photo.id,
                Version = chosen, Title = photo.TitleOf(chosen), Description = photo.DescOf(chosen), Date = photo.dateLabel, People = photo.people?.ToArray() };
            draft = talk ? "MF_Bridge_TalkPrompt".Translate(selection.Title, selection.Date, selection.Description).ToString() : selection.Draft;
            speaker = Speakers().FirstOrDefault(p => photo.Has(p)) ?? Speakers().FirstOrDefault();
            doCloseX = false; doWindowBackground = false; forcePause = false; absorbInputAroundWindow = true; closeOnAccept = false;
            try
            {
                if (selection.ValidPath)
                {
                    byte[] bytes = File.ReadAllBytes(selection.Path);
                    selection.ExpectedSha256 = PhotoBridgeSelection.Hash(bytes);
                    if (!PhotoBridgeSelection.DecodablePng(bytes)) imageProblem = "MF_Bridge_ImageInvalid".Translate();
                    else UpdatePreview();
                }
            }
            catch (Exception e) { imageProblem = "MF_Bridge_Stale".Translate(); Log.Warning("[MomentFrames] selected photo preview: " + e.Message); }
        }
        private void UpdatePreview()
        {
            if (preview != null || imageProblem != null || !SelectionCurrent) return;
            try
            {
                string path = PhotoPreviews.DisplayPath(selection.Path, null);
                if (string.IsNullOrEmpty(path)) return;
                byte[] bytes = File.ReadAllBytes(path);
                if (!PhotoBridgeSelection.DecodablePng(bytes, PhotoPreviews.Side)) throw new IOException("Invalid bounded sharing preview.");
                preview = PhotoTextureCache.AcquireTexture(path);
                if (preview == null) throw new IOException("Cannot load sharing preview.");
                previewPath = path;
                previewSha256 = PhotoBridgeSelection.Hash(bytes);
            }
            catch (Exception e) { imageProblem = "MF_Bridge_PrepareFailed".Translate(); Log.Warning("[MomentFrames] sharing preview: " + e.Message); }
        }
        public override void WindowUpdate() { base.WindowUpdate(); UpdatePreview(); }
        public override void PostClose()
        {
            if (preview != null && ReferenceEquals(PhotoTextureCache.Peek(previewPath), preview)) PhotoTextureCache.Release(previewPath);
            preview = null; base.PostClose();
        }
        private IEnumerable<Pawn> Speakers()
        {
            return Find.CurrentMap?.mapPawns.FreeColonistsSpawned.Where(p => p != null && !p.Dead && !p.Downed && p.DevelopmentalStage != DevelopmentalStage.Baby
                ).OrderByDescending(p => photo.Has(p)).ThenBy(p => p.LabelShort) ?? Enumerable.Empty<Pawn>();
        }
        private bool SelectionCurrent => PhotoStore.Instance == owner && owner.Get(photo.id) == photo && selection.ValidPath
            && string.Equals(selection.Path, owner.VersionPath(photo, selection.Version), StringComparison.OrdinalIgnoreCase);
        public override void DoWindowContents(Rect rect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect); var page = rect.ContractedBy(24f);
                string action = (exportOnly ? "CloseButton" : talk ? "MF_Bridge_RequestOnce" : "MF_Bridge_PublishLocal").Translate();
                float control = Mathf.Max(46f, SharedPhotoUI.ControlHeight, AlbumStyle.TextHeight(action, page.width - 12f) + 14f);
                string heading = (exportOnly ? "MF_Feedback_ExportTitle" : talk ? "MF_Bridge_TalkTitle" : "MF_Bridge_ShareTitle").Translate();
                float headingHeight = Mathf.Max(44f, AlbumStyle.TextHeight(heading, page.width - 54f, GameFont.Medium) + 8f);
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - 54f, headingHeight), heading);
                if (SharedPhotoUI.Button(new Rect(page.xMax - 44f, page.y, 44f, 44f), "×")) Close();
                bool capable = exportOnly || (talk ? contracts.HasRimTalk : contracts.HasRimGram);
                string problem = !SelectionCurrent ? "MF_Bridge_Stale".Translate().ToString() : !capable ? (talk ? "MF_Bridge_NoRimTalk" : "MF_Bridge_NoRimGram").Translate().ToString() : null;
                if (talk && (speaker == null || !Speakers().Contains(speaker))) problem = "MF_Bridge_NoSpeaker".Translate();
                if (!talk && !exportOnly && capable && !finished)
                {
                    try { if (contracts.HoursLeft > 0f) problem = "MF_Bridge_Cooldown".Translate(contracts.HoursLeft.ToString("0.0")); }
                    catch { problem = "MF_Bridge_NoRimGram".Translate(); }
                    if (problem == null && (imageProblem != null || preview == null))
                        problem = imageProblem ?? "MF_Bridge_Preparing".Translate().ToString();
                }
                string footer = problem ?? status;
                float footerHeight = string.IsNullOrEmpty(footer) ? 0f : Mathf.Min(page.height * .3f, AlbumStyle.TextHeight(footer, page.width) + 10f);
                var viewport = new Rect(page.x, page.y + headingHeight + 8f, page.width, page.height - headingHeight - control - footerHeight - 30f);
                float w = viewport.width - 18f, imageHeight = Mathf.Min(200f, page.height * .33f);
                string meta = selection.Title + " · " + selection.Date, hint = (exportOnly ? "MF_Feedback_ExportHint" : talk ? "MF_Bridge_TalkHint" : "MF_Feedback_LocalHint").Translate();
                string requestLabel = (exportOnly ? "MF_Feedback_ExportCaption" : talk ? "MF_Bridge_RequestLabel" : "MF_Bridge_CaptionLabel").Translate();
                float metaHeight = Mathf.Max(32f, AlbumStyle.TextHeight(meta, w) + 8f), hintHeight = Mathf.Max(50f, AlbumStyle.TextHeight(hint, w) + 8f);
                float labelHeight = Mathf.Max(32f, AlbumStyle.TextHeight(requestLabel, w) + 8f);
                float textY = imageHeight + metaHeight + hintHeight + 30f + (talk ? control + 10f : 0f);
                var view = new Rect(0f, 0f, w, textY + labelHeight + 168f + (talk ? 0f : control * ((exportExpanded ? 3f : 2f) - (exportOnly ? 1f : 0f)) + 26f));
                Widgets.BeginScrollView(viewport, ref scroll, view);
                var image = new Rect(0f, 0f, w, imageHeight);
                if (preview != null) GUI.DrawTexture(image, preview, ScaleMode.ScaleToFit);
                SharedPhotoUI.Label(new Rect(0f, imageHeight + 10f, w, metaHeight), meta);
                SharedPhotoUI.Label(new Rect(0f, imageHeight + metaHeight + 20f, w, hintHeight), hint, true);
                float y = imageHeight + metaHeight + hintHeight + 30f;
                if (talk)
                {
                    string label = speaker == null ? "MF_Bridge_NoSpeaker".Translate().ToString() : "MF_Bridge_Speaker".Translate(speaker.LabelShortCap).ToString();
                    if (speaker != null) SharedPhotoUI.Portrait(speaker, new Rect(0f, y, control, control));
                    if (SharedPhotoUI.Button(new Rect(control + 8f, y, w - control - 8f, control), label))
                    {
                        var entries = Speakers().Select(p => new FloatMenuOption(p.LabelShortCap, () => speaker = p)).ToList();
                        if (entries.Count > 0) Find.WindowStack.Add(new FloatMenu(entries));
                    }
                    y += control + 10f;
                }
                SharedPhotoUI.Label(new Rect(0f, y, w, labelHeight), requestLabel);
                draft = AlbumBridgeUI.TextArea(new Rect(0f, y + labelHeight + 6f, w, 150f), draft, talk ? "MF-TalkDraft" : "MF-GramDraft");
                y += labelHeight + 168f;
                if (!talk)
                {
                    if (!exportOnly)
                    {
                        if (SharedPhotoUI.Button(new Rect(0f, y, w, control), "MF_Bridge_OpenRimGram".Translate()))
                        { if (!OptionalRimGramWindow.OpenFrom(null, this)) status = "MF_Feedback_RimGramWindowUnavailable".Translate(); }
                        y += control + 8f;
                    }
                    if (SharedPhotoUI.Button(new Rect(0f, y, w, control), (exportExpanded ? "MF_Feedback_ExportClose" : "MF_Feedback_ExportOpen").Translate())) exportExpanded = !exportExpanded;
                    y += control + 8f;
                    if (exportExpanded)
                    {
                        float half = (w - 10f) / 2f;
                        if (SharedPhotoUI.Button(new Rect(0f, y, half, control), "MF_Bridge_CopyPath".Translate(), SelectionCurrent))
                        { GUIUtility.systemCopyBuffer = selection.Path; status = "MF_Bridge_PathCopied".Translate(); }
                        if (SharedPhotoUI.Button(new Rect(half + 10f, y, half, control), "MF_Bridge_Export".Translate(), SelectionCurrent)) Export();
                        TooltipHandler.TipRegion(new Rect(0f, y, w, control), "MF_Feedback_ExportHint".Translate());
                    }
                }
                Widgets.EndScrollView();
                if (!string.IsNullOrEmpty(footer)) SharedPhotoUI.Label(new Rect(page.x, page.yMax - control - footerHeight - 10f, page.width, footerHeight), footer, true);
                if (!string.IsNullOrEmpty(footer)) TooltipHandler.TipRegion(viewport, footer);
                var button = new Rect(page.x, page.yMax - control, page.width, control);
                if (SharedPhotoUI.Button(button, finished ? "MF_Bridge_Done".Translate().ToString() : action,
                    exportOnly || !finished && problem == null && !string.IsNullOrWhiteSpace(draft)))
                { if (exportOnly) Close(); else Submit(); }
                if (!string.IsNullOrEmpty(footer)) TooltipHandler.TipRegion(button, footer);
            }
        }
        private void Export()
        {
            try
            {
                string directory = selection.Export(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "MomentFramesExports"), draft);
                GUIUtility.systemCopyBuffer = directory;
                status = "MF_Bridge_Exported".Translate(); Messages.Message(status, MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e) { status = "MF_Bridge_ExportFailed".Translate(); Log.Warning("[MomentFrames] selected photo export: " + e.Message); }
        }
        private void Submit()
        {
            if (exportOnly || finished || !SelectionCurrent || string.IsNullOrWhiteSpace(draft)) return;
            if (talk && (speaker == null || !Speakers().Contains(speaker))) return;
            string sharedJpg = null;
            if (!talk)
            {
                if (preview == null || imageProblem != null) return;
                // Local file preparation cannot have accepted a post, so failures here remain retryable.
                try { sharedJpg = PrepareJpg(); }
                catch (Exception e)
                {
                    status = "MF_Bridge_PrepareFailed".Translate();
                    Messages.Message(status, MessageTypeDefOf.RejectInput, false);
                    Log.Warning("[MomentFrames] sharing image preparation: " + e.Message);
                    return;
                }
            }
            try
            {
                bool accepted = submission.Submit(() =>
                {
                    if (talk) return contracts.RequestComment(speaker, draft);
                    if (contracts.HoursLeft > 0f) return false;
                    return contracts.PublishLocal(draft, sharedJpg);
                }) == true;
                if (accepted) status = (talk ? "MF_Bridge_TalkAccepted" : "MF_Bridge_Posted").Translate();
                else status = (talk ? "MF_Bridge_TalkRejected" : "MF_Bridge_PostRejected").Translate();
                Messages.Message(status, accepted ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, false);
                if (accepted && !talk && !OptionalRimGramWindow.OpenFrom(contracts.PublishedPost, this))
                    status = "MF_Feedback_PostedWindowUnavailable".Translate();
            }
            catch (Exception e)
            {
                status = "MF_Bridge_CallFailed".Translate();
                Messages.Message(status, MessageTypeDefOf.RejectInput, false);
                Log.Warning("[MomentFrames] optional photo bridge: " + (e.InnerException ?? e).Message);
            }
        }
        private string PrepareJpg()
        {
            byte[] original = File.ReadAllBytes(selection.Path);
            if (!PhotoBridgeSelection.DecodablePng(original) || string.IsNullOrEmpty(selection.ExpectedSha256)
                || !PhotoBridgeSelection.Hash(original).Equals(selection.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected image changed after review. Reopen it from the album.");
            byte[] small = File.ReadAllBytes(previewPath);
            if (!PhotoBridgeSelection.DecodablePng(small, PhotoPreviews.Side) || PhotoBridgeSelection.Hash(small) != previewSha256)
                throw new IOException("The sharing preview changed after review.");
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!texture.LoadImage(small)) throw new IOException("Cannot decode the selected photo preview.");
                byte[] jpg = texture.EncodeToJPG(85);
                if (jpg == null || jpg.Length == 0 || jpg.Length > 8 * 1024 * 1024) throw new IOException("The shared JPG exceeds 8 MiB.");
                return Convert.ToBase64String(jpg);
            }
            finally { if (texture != null) UnityEngine.Object.Destroy(texture); }
        }
    }
#endif
}
