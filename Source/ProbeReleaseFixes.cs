using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    // Explicit disposable-profile mode, never active during ordinary play.
    public static partial class Probe
    {
        private static bool releaseFixesMode, releaseCaptured;
        private static PhotoCapture.Result releaseResult;
        private static Window releaseWindow;
        private static Building_PhotoProjector releaseProjector;
        private static Dialog_ExternalPhotos releaseScan;
        private static PhotoRecord releaseLarge;
        private static string releaseLargeHash;
        private static void ReleaseCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("release fix: " + label); checks.Add("PASS: " + label); }
        private static void ReleaseRemoveLords()
        {
            foreach (var lord in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(lord);
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) { pawn.jobs?.StopAll(); FixReady(pawn); }
            foreach (var window in Find.WindowStack.Windows.ToList())
                if (window is Dialog_MemoryNightProgress) window.Close();
        }
        private static void ReleasePreset(int size)
        {
            MomentFramesMod.Settings.albumTextSize = size;
            var window = new Dialog_MFReactionPresetName(Current.Game.GetComponent<ReactionPresetsGameComponent>(), new Dictionary<string,int>(), new List<string>());
            Traverse.Create(window).Field("presetName").SetValue("가족사진 Family");
            releaseWindow = window; Find.WindowStack.Add(window);
        }
        private static void ReleaseFixesStep()
        {
            CloseLog();
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 180f; map = Find.CurrentMap; center = map.Center;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    ClearHostiles(map); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    var settings = MomentFramesMod.Settings;
                    settings.autoCaptureEnabled = settings.memoryNight = settings.yearLetter = settings.buildingPhotos = false;
                    settings.settlementPeriod = 0; settings.pxPerCell = 64; settings.albumTextSize = 100;
                    Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
                    ReleaseRemoveLords();
                    var people = map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
                    ReleaseCheck(people.Count >= 2, "fresh minimal game has colonists");
                    foreach (var cell in CellRect.CenteredOn(center,14)) { map.fogGrid.Unfog(cell); map.roofGrid.SetRoof(cell,null); }
                    foreach (var pawn in people) { FixReady(pawn); pawn.Position=center+IntVec3.East*people.IndexOf(pawn); pawn.Notify_Teleported(); }
                    releaseProjector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.WoodLog);
                    releaseProjector.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(releaseProjector,center+IntVec3.South*4,map,Rot4.North);
                    Directory.CreateDirectory(store.FolderPath);
                    var tex = new Texture2D(96,64,TextureFormat.RGB24,false);
                    tex.SetPixels32(Enumerable.Repeat(new Color32(40,100,180,255),96*64).ToArray()); tex.Apply(false);
                    byte[] png=tex.EncodeToPNG(); UnityEngine.Object.Destroy(tex);
                    for(int i=0;i<3;i++)
                    {
                        string file="release-fixture-"+i+".png"; File.WriteAllBytes(Path.Combine(store.FolderPath,file),png);
                        var rec=new PhotoRecord { id="release-fix-"+i,file=file,title="검증 사진 "+i,kind="manual",aspect=1.5f,
                            year=5500,ticksAbs=Find.TickManager.TicksAbs+i,capturedTicksAbs=Find.TickManager.TicksAbs+i,mapId=map.uniqueID,dateLabel="5500년 1분기 1일",dateSlug="5500-1-01" };
                        Traverse.Create(store).Field("records").GetValue<List<PhotoRecord>>().Add(rec);
                    }
                    store.NotifyChanged(); releaseProjector.SetFilm("","","");
                    ReleaseCheck(MemoryNight.StartPrepared(releaseProjector,new[]{people[0]},MemoryNight.DefaultWatchTicks),"night starts with three-photo film (reel " + releaseProjector.ReelCount + ", refusal " + MemoryNight.CannotStart(releaseProjector) + ", person: " + GroupPhoto.WhyNot(people[0]) + ")");
                    var night=MemoryNight.Active(releaseProjector);
                    Traverse.Create(night).Field("clock").GetValue<MemoryNightTiming>().BeginWatching();
                    var callOff=AccessTools.Method(typeof(LordJob_MFMemoryNight),"ShouldBeCalledOff");
                    var musicAlive=AccessTools.Method(typeof(MemoryNightMusic),"Alive");
                    ReleaseCheck(!(bool)callOff.Invoke(night,null) && (bool)musicAlive.Invoke(null,new object[]{night}),"current night and music are active");
                    releaseProjector.SetFilm("release-no-photos","","");
                    ReleaseCheck(night.FilmIds.Count==3 && night.HasFilmPhotos && !(bool)callOff.Invoke(night,null)
                        && (bool)musicAlive.Invoke(null,new object[]{night}),"empty NEXT film preserves current snapshot, night and music");
                    ReleaseRemoveLords(); releaseProjector.SetFilm("","","");
                    var party=new LordJob_Joinable_Party(center,people[0],GatheringDefOf.Party);
                    var old=LordMaker.MakeNewLord(Faction.OfPlayer,party,map,new[]{people[0],people[1]});
                    ReleaseCheck(GroupPhoto.CanJoin(people[1],null,true),"ordinary party guest is eligible");
                    ReleaseCheck(MemoryNight.StartPrepared(releaseProjector,new[]{people[1]},MemoryNight.DefaultWatchTicks),"night accepts ordinary party guest");
                    var next=MemoryNight.Active(releaseProjector);
                    ReleaseCheck(next.lord.ownedPawns.Contains(people[1]) && !old.ownedPawns.Contains(people[1]) && people[1].GetLord()==next.lord,"guest leaves old party and belongs to exactly the new night");
                    ReleaseRemoveLords();
                    // B1 (Claude final review): an evening's own night, which no progress window decides, must not wait forever.
                    LordJob_MFMemoryNight Natural()
                    {
                        var job=new LordJob_MFMemoryNight(MemoryNight.GatherCell(releaseProjector),people[0],MemoryNight.Def,releaseProjector);
                        LordMaker.MakeNewLord(Faction.OfPlayer,job,map,new[]{people[0]});
                        Traverse.Create(job).Field("clock").GetValue<MemoryNightTiming>().GatherElapsed=MemoryNightTiming.GatherWaitTicks-1;
                        return job;
                    }
                    var empty=Natural();
                    ReleaseCheck(Traverse.Create(empty).Field("expected").GetValue<List<Pawn>>().Count>1 && empty.ArrivedCount==0,"natural night waits for every colonist and nobody has arrived");
                    empty.TickFlow();
                    ReleaseCheck(empty.Cancelled && !empty.Watching && (bool)callOff.Invoke(empty,null),"natural night with nobody present is called off when the wait runs out");
                    ReleaseRemoveLords();
                    var partial=Natural();
                    people[0].jobs.StartJob(JobMaker.MakeJob(JobDefOf.SpectateCeremony,people[0].Position,MemoryNight.ScreenRect(releaseProjector).CenterCell),Verse.AI.JobCondition.InterruptForced);
                    ReleaseCheck(partial.ArrivedCount==1,"one colonist settled at an audience spot");
                    partial.TickFlow();
                    var partialExpected=Traverse.Create(partial).Field("expected").GetValue<List<Pawn>>();
                    ReleaseCheck(partial.Watching && !partial.Cancelled && partialExpected.Count==1 && partialExpected[0]==people[0],"natural night starts with whoever came when the wait runs out");
                    ReleaseRemoveLords();
                    ReleaseCheck(MemoryNight.StartPrepared(releaseProjector,new[]{people[0],people[1]},MemoryNight.DefaultWatchTicks),"hand-picked night starts");
                    var picked=MemoryNight.Active(releaseProjector);
                    Traverse.Create(picked).Field("clock").GetValue<MemoryNightTiming>().GatherElapsed=MemoryNightTiming.GatherWaitTicks-1;
                    picked.TickFlow();
                    ReleaseCheck(picked.WaitingForDecision && !picked.Watching && !picked.Cancelled,"hand-picked night still waits for the player's decision");
                    ReleaseRemoveLords();
                    var editor=new Dialog_PhotoEdit(store.Get("release-fix-0")); var previousTarget=RenderTexture.active;
                    editor.PreOpen(); ReleaseCheck(RenderTexture.active==previousTarget,"real editor preview restores active render target"); editor.PostClose();
                    Find.Selector.ClearSelection(); Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(),12f);
                    ReleaseCheck(PhotoCapture.Capture(map,center,8f,r=>{releaseResult=r;releaseCaptured=true;}),"real native capture accepted");
                    phase=1; Wait(.1f); return;
                case 1:
                    if(!releaseCaptured) { Wait(.1f); return; }
                    ReleaseCheck(releaseResult?.png?.Length>100 && !PhotoCapture.Busy,"native capture completes and releases lease");
                    File.WriteAllBytes(Path.Combine(dir,"native-capture.png"),releaseResult.png);
                    releaseScan=new Dialog_ExternalPhotos(false); Find.WindowStack.Add(releaseScan);
                    ReleaseCheck(!releaseScan.forcePause && releaseScan.Busy,"folder scan starts asynchronously without pausing");
                    phase=2; Wait(.1f); return;
                case 2:
                    if(releaseScan.Busy) { Wait(.1f); return; }
                    ReleaseCheck(Traverse.Create(releaseScan).Field("status").GetValue<string>()=="","folder scan completes without error");
                    releaseScan.Close();
                    var cancelled=new Dialog_ExternalPhotos(false); Find.WindowStack.Add(cancelled); cancelled.Close(); cancelled.WindowUpdate();
                    ReleaseCheck(Traverse.Create(cancelled).Field("candidates").GetValue<List<ExternalPhotoCandidate>>().Count==0,"closed scan cannot apply late results");
                    ReleasePreset(100); phase=3; Wait(.5f); return;
                case 3:
                    Shot("preset-100.png"); phase=30; Wait(.3f); return;
                case 30:
                    releaseWindow.Close(); ReleasePreset(150); phase=4; Wait(.5f); return;
                case 4:
                    Shot("preset-150.png"); phase=40; Wait(.3f); return;
                case 40:
                    releaseWindow.Close(); MomentFramesMod.Settings.albumTextSize=100;
                    GramOnline(false); ReleaseCheck(OptionalPhotoBridges.Contracts().HasRimGram,"real RimGram local contract available");
                    releaseLarge=new PhotoRecord {id="release-large",kind=Kinds.Manual,file="release-large.png",title="대형 사진 공유 시험",dateLabel="5500년 1분기 1일",year=5500,aspect=1f};
                    File.Copy(Path.Combine(dir,"6000.png"),store.FilePath(releaseLarge));
                    ReleaseCheck(store.CommitRecord(releaseLarge),"6000x6000 original registered");
                    releaseLargeHash=PhotoBridgeSelection.Hash(File.ReadAllBytes(store.VersionPath(releaseLarge,"")));
                    bridgeCount=GramPosts().Count; bridgeDraft=new Dialog_MFPhotoBridge(store,releaseLarge,"",false); Find.WindowStack.Add(bridgeDraft);
                    phase=5; Wait(.1f); return;
                case 5:
                    bridgeDraft.WindowUpdate();
                    if(Traverse.Create(bridgeDraft).Field("preview").GetValue<Texture2D>()==null) { Wait(.1f); return; }
                    var preview=Traverse.Create(bridgeDraft).Field("preview").GetValue<Texture2D>();
                    ReleaseCheck(Math.Max(preview.width,preview.height)<=PhotoPreviews.Side,"large original uses bounded native preview");
                    string path=Traverse.Create(bridgeDraft).Field("previewPath").GetValue<string>();
                    byte[] bytes=File.ReadAllBytes(path); File.WriteAllBytes(path,new byte[]{1,2,3}); SubmitBridge();
                    ReleaseCheck(!Traverse.Create(bridgeDraft).Field("submission").GetValue<PhotoBridgeSubmission>().Finished
                        && GramPosts().Count==bridgeCount,"local preparation failure keeps action retryable and creates no post");
                    File.WriteAllBytes(path,bytes); SubmitBridge();
                    ReleaseCheck(GramPosts().Count==bridgeCount+1,"retry posts large photograph locally once");
                    bridgePost=GramPosts().Cast<object>().Single(p=>PostField<string>(p,"photoJpg")!=null && PostField<string>(p,"text").Contains("대형 사진"));
                    var jpg=Convert.FromBase64String(PostField<string>(bridgePost,"photoJpg")); var image=new Texture2D(2,2);
                    try { ReleaseCheck(image.LoadImage(jpg) && Math.Max(image.width,image.height)<=1600,"published JPG decodes at bounded size"); }
                    finally { UnityEngine.Object.Destroy(image); }
                    File.WriteAllBytes(Path.Combine(dir,"published-large.jpg"),jpg);
                    ReleaseCheck(!PostField<bool>(bridgePost,"outgoing") && !PostField<bool>(bridgePost,"sent") && GramOutbox().Count==0,"local publication does not queue online transmission");
                    ReleaseCheck(PhotoBridgeSelection.Hash(File.ReadAllBytes(store.VersionPath(releaseLarge,"")))==releaseLargeHash,"sharing preserves full original bytes");
                    CloseBridgeWindows(); Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.Selector.ClearSelection(); Find.Selector.Select(releaseProjector,false);
                    Find.CameraDriver.SetRootPosAndSize(releaseProjector.Position.ToVector3Shifted(),12f);
                    phase=6; Wait(.6f); return;
                case 6:
                    Shot("projector-controls.png"); phase=60; Wait(.3f); return;
                case 60:
                    Find.Selector.ClearSelection(); GameDataSaveLoader.SaveGame("mf-release-fixes"); phase=7; Wait(.4f); return;
                case 7:
                    GameDataSaveLoader.LoadGame("mf-release-fixes"); phase=8; Wait(.6f); return;
                case 8:
                    ReleaseCheck(PhotoStore.Instance.Records.Count(r=>r.id.StartsWith("release-fix-"))==3,"records survive real save/load");
                    ReleaseCheck(PhotoStore.Instance.Records.Where(r=>r.id.StartsWith("release-")).All(r=>r.HasFile),"all saved originals remain available");
                    File.WriteAllText(Path.Combine(dir,"fix-result.json"),"{\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    File.WriteAllText(Path.Combine(dir,"fix-runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    active=false; Application.Quit(); return;
            }
        }
    }
}
