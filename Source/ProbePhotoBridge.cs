using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool photoBridgeMode;
        private static Dialog_MFPhotoBridge bridgeDraft;
        private static object bridgePost;
        private static string bridgePhotoId, bridgePhotoHash, bridgeJpg;
        private static int bridgePostId, bridgeCount, bridgeTalkCalls;
        private static bool bridgeTalkAccept, bridgeTalkValid;
        private static Harmony bridgeHarness;
        private static Type BridgeType(string name) => OptionalPhotoBridges.LoadedType("RimGram." + name);
        private static object GramComponent() => AccessTools.Property(BridgeType("RimGramGameComp"), "Instance").GetValue(null, null);
        private static IList GramPosts() => (IList)AccessTools.Field(BridgeType("RimGramGameComp"), "posts").GetValue(GramComponent());
        private static IList GramOutbox() => (IList)AccessTools.Field(BridgeType("Online"), "outbox").GetValue(null);
        private static void GramOnline(bool enabled)
        {
            object settings = AccessTools.Field(BridgeType("RimGramMod"), "Settings").GetValue(null);
            AccessTools.Field(settings.GetType(), "online").SetValue(settings, enabled);
        }
        private static T PostField<T>(object post, string name) => (T)AccessTools.Field(post.GetType(), name).GetValue(post);
        private static void BridgeCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("photo bridge: " + label); checks.Add("PASS: " + label); }
        private static void SubmitBridge() => AccessTools.Method(typeof(Dialog_MFPhotoBridge), "Submit").Invoke(bridgeDraft, null);
        private static void CloseBridgeWindows()
        {
            foreach (var window in Find.WindowStack.Windows.Where(w => w is Dialog_MFPhotoBridge || w is Dialog_MFRimGramWindow || w.GetType() == BridgeType("MainTabWindow_RimGram")).ToList()) window.Close();
        }
        // Test only: observe the real public call boundary without contacting an AI provider.
        private static bool ObserveBridgeTalk(object __0, ref bool __result)
        {
            bridgeTalkCalls++;
            var type = __0.GetType();
            bridgeTalkValid = (bool)AccessTools.Field(type, "IsMonologue").GetValue(__0)
                && AccessTools.Property(type, "TalkType").GetValue(__0, null).ToString() == "User"
                && ((string)AccessTools.Property(type, "Prompt").GetValue(__0, null)).Contains("사진")
                && string.IsNullOrEmpty((string)AccessTools.Property(type, "ImageBase64").GetValue(__0, null));
            __result = bridgeTalkAccept; return false;
        }
        private static void PhotoBridgeStep()
        {
            CloseLog();
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    map = Find.CurrentMap; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    deadline = Time.realtimeSinceStartup + 180;
                    MomentFramesMod.Settings.autoCaptureEnabled = false;
                    MomentFramesMod.Settings.albumTextSize = 100; Prefs.DevMode = false;
                    var api = OptionalPhotoBridges.Contracts();
                    BridgeCheck(api.HasRimGram, "current installed RimGram binds without a DLL hash allowlist");
                    BridgeCheck(api.HasRimTalk, "current installed RimTalk public request contract binds");
                    BridgeCheck(RimTalkBridge.Registered, "RimTalk automatic photo context is registered");
                    GramOnline(false);
                    Directory.CreateDirectory(store.FolderPath);
                    var photo = new PhotoRecord { id = "bridge-photo", kind = Kinds.Manual, title = "사진관에서 함께 찍은 사진", desc = "사진관에서 함께 남긴 가족사진이다.", file = "bridge-photo.png", dateLabel = "5500년 1분기 1일", year = 5500, aspect = 1.5f };
                    CopyAsPng("family", store.FilePath(photo));
                    BridgeCheck(store.CommitRecord(photo), "real captured photo fixture registered");
                    bridgePhotoId = photo.id; bridgePhotoHash = PhotoBridgeSelection.Hash(File.ReadAllBytes(store.FilePath(photo)));
                    bridgeCount = GramPosts().Count;
                    MainTabWindow_Album.Open(photo);
                    bridgeDraft = new Dialog_MFPhotoBridge(store, photo, "", false); Find.WindowStack.Add(bridgeDraft);
                    BridgeCheck(GramPosts().Count == bridgeCount && GramOutbox().Count == 0, "opening the share dialog does not publish or queue a post");
                    phase = 1; Wait(.5f); return;
                case 1: Shot("01-local-share.png"); phase = 2; Wait(.3f); return;
                case 2:
                    // There is no yield/tick between enabling online, the synchronous call, and restoration.
                    // This exercises OfferOwn's typed=false branch while preventing any online update loop.
                    GramOnline(true);
                    try
                    {
                        BridgeCheck((bool)AccessTools.Property(BridgeType("Online"), "Enabled").GetValue(null, null), "online setting is enabled during synchronous local-post check");
                        SubmitBridge();
                        BridgeCheck(GramPosts().Count == bridgeCount + 1, "share action creates one real RimGram post");
                        bridgePost = GramPosts().Cast<object>().Single(p => PostField<string>(p, "photoJpg") != null && PostField<string>(p, "text").Contains("사진관"));
                        bridgePostId = PostField<int>(bridgePost, "id"); bridgeJpg = PostField<string>(bridgePost, "photoJpg");
                        BridgeCheck(!PostField<bool>(bridgePost, "outgoing") && !PostField<bool>(bridgePost, "sent") && GramOutbox().Count == 0, "local post remains out of online queue even when online is enabled");
                    }
                    finally { GramOnline(false); }
                    var jpg = Convert.FromBase64String(bridgeJpg); var texture = new Texture2D(2, 2);
                    try { BridgeCheck(texture.LoadImage(jpg) && texture.width > 100 && texture.height > 100, "published photo contains a decodable full image"); }
                    finally { UnityEngine.Object.Destroy(texture); }
                    File.WriteAllBytes(Path.Combine(dir, "published-photo.jpg"), jpg);
                    BridgeCheck(PhotoBridgeSelection.Hash(File.ReadAllBytes(store.FilePath(store.Get(bridgePhotoId)))) == bridgePhotoHash, "publication preserves original PNG bytes");
                    SubmitBridge(); BridgeCheck(GramPosts().Count == bridgeCount + 1, "repeat submit is blocked after success");
                    var host = Find.WindowStack.Windows.OfType<Dialog_MFRimGramWindow>().SingleOrDefault();
                    BridgeCheck(host != null, "publication opens hosted RimGram without closing album");
                    BridgeCheck(Find.MainTabsRoot.OpenTab == DefDatabase<MainButtonDef>.GetNamed("MF_Album"), "album main tab remains open");
                    var content = AccessTools.Field(typeof(Dialog_MFRimGramWindow), "content").GetValue(host);
                    BridgeCheck((int)AccessTools.Field(content.GetType(), "singlePostId").GetValue(content) == bridgePostId, "host navigates to the published post");
                    phase = 3; Wait(.6f); return;
                case 3: Shot("02-posted-in-rimgram.png"); phase = 4; Wait(.3f); return;
                case 4:
                    var hostBefore = Find.WindowStack.Windows.OfType<Dialog_MFRimGramWindow>().Single();
                    BridgeCheck(OptionalRimGramWindow.OpenFrom(bridgePost, bridgeDraft) && Find.WindowStack.Windows.OfType<Dialog_MFRimGramWindow>().Single() == hostBefore, "opening again reuses the same RimGram window");
                    CloseBridgeWindows();
                    var cooldown = new Dialog_MFPhotoBridge(store, store.Get(bridgePhotoId), "", false); bridgeDraft = cooldown;
                    BridgeCheck(OptionalPhotoBridges.Contracts().HoursLeft > 0, "RimGram publication cooldown remains active");
                    SubmitBridge(); BridgeCheck(GramPosts().Count == bridgeCount + 1, "another draft respects RimGram cooldown"); cooldown.Close();
                    var gramButton = DefDatabase<MainButtonDef>.AllDefsListForReading.Single(d => d.TabWindow?.GetType() == BridgeType("MainTabWindow_RimGram"));
                    Find.MainTabsRoot.SetCurrentTab(gramButton); var actualWindow = gramButton.TabWindow;
                    bridgeDraft = new Dialog_MFPhotoBridge(store, store.Get(bridgePhotoId), "", false); Find.WindowStack.Add(bridgeDraft);
                    BridgeCheck(OptionalRimGramWindow.OpenFrom(bridgePost, bridgeDraft), "existing native RimGram window can be focused");
                    BridgeCheck(Find.WindowStack.Windows.Count(w => w.GetType() == actualWindow.GetType()) == 1 && !Find.WindowStack.Windows.OfType<Dialog_MFRimGramWindow>().Any(), "existing native window is reused without another feed");
                    Find.MainTabsRoot.SetCurrentTab(null);
                    CloseBridgeWindows();
                    bridgeDraft = new Dialog_MFPhotoBridge(store, store.Get(bridgePhotoId), "", true); Find.WindowStack.Add(bridgeDraft);
                    bridgeHarness = new Harmony("momentframes.probe.photo-bridge");
                    var talkMethod = AccessTools.Method(OptionalPhotoBridges.LoadedType("RimTalk.Service.TalkService"), "GenerateTalk");
                    bridgeHarness.Patch(talkMethod, prefix: new HarmonyMethod(typeof(Probe), nameof(ObserveBridgeTalk)));
                    BridgeCheck(bridgeTalkCalls == 0, "opening talk dialog sends no AI request");
                    phase = 5; Wait(.5f); return;
                case 5: Shot("03-rimtalk-request.png"); phase = 6; Wait(.3f); return;
                case 6:
                    bridgeTalkAccept = false; SubmitBridge();
                    BridgeCheck(bridgeTalkCalls == 1 && bridgeTalkValid, "real RimTalk request has User monologue, text context and no image; rejection returns");
                    bridgeTalkAccept = true; SubmitBridge(); SubmitBridge();
                    BridgeCheck(bridgeTalkCalls == 2, "explicit retry after rejection succeeds; duplicate accepted request is blocked");
                    bridgeHarness.UnpatchAll(bridgeHarness.Id); bridgeHarness = null; CloseBridgeWindows();
                    Find.MainTabsRoot.SetCurrentTab(null);
                    GameDataSaveLoader.SaveGame("mf-photo-bridge"); phase = 7;
                    GameDataSaveLoader.LoadGame("mf-photo-bridge"); Wait(.5f); return;
                case 7:
                    var restored = GramPosts().Cast<object>().Single(p => PostField<int>(p, "id") == bridgePostId);
                    BridgeCheck(PostField<string>(restored, "photoJpg") == bridgeJpg && !PostField<bool>(restored, "outgoing"), "real saved RimGram photo persists after save/load as local");
                    BridgeCheck(GramOutbox().Count == 0, "load does not enqueue the local post");
                    BridgeCheck(PhotoBridgeSelection.Hash(File.ReadAllBytes(store.FilePath(store.Get(bridgePhotoId)))) == bridgePhotoHash, "album original survives save/load unchanged");
                    File.WriteAllText(Path.Combine(dir, "bridge-checks.txt"), string.Join("\n", checks)); Finish(); return;
            }
        }
    }
}
