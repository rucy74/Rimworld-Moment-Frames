using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool faultIsolationMode, faultBaseline;
        private static string isolationFault;
        private static int isolationHits, birthdayVisits, groupVisits;
        private static bool isolationReady;
        private static PhotoCapture.Result isolationPhoto;
        private static Pawn isolationFacePawn;
        private static IList isolationFaceList;
        private static bool isolationFaceWasAdded;
        private static IEnumerable<Pawn> IsolationBrokenRoster()
        {
            yield return isolationFacePawn;
            isolationFaceWasAdded=isolationFaceList.Count>0;
            throw new InvalidOperationException("MF deliberate roster failure after first pose");
        }
        private static bool IsolationRosterPrefix(ref IEnumerable<Pawn> __result)
        {__result=IsolationBrokenRoster();return false;}
        private static void IsolationPartialFaces(Harmony harmony)
        {
            IsolationRequire(PhotoFaces.Available,"Facial Animation present for partial pose cleanup test");
            isolationFacePawn=map.mapPawns.FreeColonistsSpawned.First(p=>PhotoFaces.Supports(p,1)&&!PhotoFaces.HasTemporary(p));
            isolationFacePawn.Rotation=Rot4.South;
            object controller=AccessTools.Method(typeof(PhotoFaces),"Controller").Invoke(null,new object[]{isolationFacePawn});
            isolationFaceList=(IList)AccessTools.Field(controller.GetType(),"forcedTemporaryAnimationList").GetValue(controller);
            var record=new PhotoRecord {peopleIds=new List<string>{isolationFacePawn.GetUniqueLoadID()},
                facePawnIds=new List<string>{isolationFacePawn.GetUniqueLoadID()},faceChoices=new List<int>{1}};
            var target=AccessTools.Method(typeof(PhotoCapture),"VisiblePawns");
            harmony.Patch(target,prefix:new HarmonyMethod(typeof(Probe),nameof(IsolationRosterPrefix)));
            bool escaped=false;
            try {PhotoFaces.Begin(map,record);}
            catch(InvalidOperationException e){escaped=e.Message.Contains("roster failure");if(!escaped)throw;}
            finally {harmony.Unpatch(target,AccessTools.Method(typeof(Probe),nameof(IsolationRosterPrefix)));}
            IsolationRequire(escaped&&isolationFaceWasAdded,"failure occurs after a real FA pose was added");
            IsolationRequire(isolationFaceList.Count==0,"partially prepared FA pose removed even without a returned lease");
        }
        private static bool IsolationThrow(MethodBase __originalMethod)
        {
            if(__originalMethod.DeclaringType==typeof(Dialog_BirthdaySetup))birthdayVisits++;
            if(__originalMethod.DeclaringType==typeof(Dialog_GroupPhotoSetup))groupVisits++;
            if(isolationFault==__originalMethod.DeclaringType.Name+"."+__originalMethod.Name)
            {isolationFault=null;isolationHits++;throw new InvalidOperationException("MF deliberate isolation fault: "+__originalMethod.Name);}
            return __originalMethod.Name!="DrawPreview";
        }
        private static void IsolationRequire(bool good,string text)
        { checks.Add((good?"PASS: ":"FAIL: ")+text);if(!good)throw new InvalidOperationException(text); }
        private static void IsolationPreparation(string method)
        {
            int hits=isolationHits;bool escaped=false,accepted=false,callback=false;
            isolationFault=method;
            try { accepted=PhotoCapture.Capture(map,center.ToVector3Shifted(),14f,r=>callback=true); }
            catch(InvalidOperationException e) { escaped=e.Message.StartsWith("MF deliberate isolation fault:");if(!escaped)throw; }
            IsolationRequire(isolationHits==hits+1,"injected preparation fault reached "+method);
            IsolationRequire(!accepted && !callback,"failed preparation does not accept or invoke callback "+method);
            IsolationRequire(faultBaseline?escaped&&PhotoCapture.Busy:!escaped&&!PhotoCapture.Busy&&!PhotoCapture.HideGuides,
                (faultBaseline?"baseline reproduces escaped exception and stuck Busy ":"preparation failure isolated and Busy released ")+method);
            if(faultBaseline)PhotoCapture.Reset(); // Only the deliberately broken control needs manual recovery.
        }
        private static void IsolationPreview(bool birthday)
        {
            var birthdayMap=AccessTools.Field(typeof(Dialog_BirthdaySetup),"previewMap");
            var groupDialog=AccessTools.Field(typeof(Dialog_GroupPhotoSetup),"previewDialog");
            birthdayMap.SetValue(null,map);
            AccessTools.Field(typeof(Dialog_BirthdaySetup),"previewUntil").SetValue(null,float.MaxValue);
            groupDialog.SetValue(null,FormatterServices.GetUninitializedObject(typeof(Dialog_GroupPhotoSetup)));
            birthdayVisits=groupVisits=0;
            isolationFault=(birthday?"Dialog_BirthdaySetup":"Dialog_GroupPhotoSetup")+".DrawPreview";
            bool escaped=false;
            try { AccessTools.Method(typeof(PhotoCapture.Patch_MapUpdate_Capture),"Postfix").Invoke(null,new object[]{map}); }
            catch(TargetInvocationException e) { escaped=e.InnerException is InvalidOperationException;if(!escaped)throw; }
            IsolationRequire(escaped==faultBaseline,(faultBaseline?"baseline preview escapes ":"preview failure isolated ")+(birthday?"birthday":"group"));
            if(!faultBaseline)
            {
                IsolationRequire(birthdayVisits==1&&groupVisits==1,"other preview still runs after a failure");
                IsolationRequire((birthday?birthdayMap.GetValue(null):groupDialog.GetValue(null))==null,"failed preview disabled until reopened");
            }
            birthdayMap.SetValue(null,null);groupDialog.SetValue(null,null);
            AccessTools.Field(typeof(Dialog_BirthdaySetup),"previewUntil").SetValue(null,0f);
        }
        private static void FaultIsolationStep()
        {
            switch(phase)
            {
                case 0:
                    deadline=Time.realtimeSinceStartup+240f;map=Find.CurrentMap;center=map.Center;
                    FxSet(false);MomentFramesMod.Settings.autoCaptureEnabled=false;MomentFramesMod.Settings.brightPhotos=false;
                    MomentFramesMod.Settings.captureReactionMode=PhotoReactions.ModeOff;
                    Find.TickManager.Pause();Find.Storyteller.storytellerComps.Clear();Find.Storyteller.incidentQueue.Clear();
                    map.fogGrid.ClearAllFog();CloseLog();
                    var h=new Harmony("MomentFrames.Probe.FaultIsolation");
                    foreach(var target in new[]{AccessTools.Method(typeof(PhotoReactions),"CreatePlan"),AccessTools.Method(typeof(PhotoFaces),"Begin"),
                        AccessTools.Method(typeof(Dialog_BirthdaySetup),"DrawPreview"),AccessTools.Method(typeof(Dialog_GroupPhotoSetup),"DrawPreview"),AccessTools.Method(typeof(Map),"MapUpdate")})
                        h.Patch(target,prefix:new HarmonyMethod(typeof(Probe),nameof(IsolationThrow)));
                    IsolationPreparation("PhotoReactions.CreatePlan");
                    IsolationPreparation("PhotoFaces.Begin");
                    IsolationPreview(true);IsolationPreview(false);
                    isolationFault="Map.MapUpdate";bool originalEscaped=false;
                    try {map.MapUpdate();}catch(InvalidOperationException e){originalEscaped=e.Message.StartsWith("MF deliberate isolation fault:");if(!originalEscaped)throw;}
                    IsolationRequire(originalEscaped,"exception in original map call remains visible, no swallowing finalizer");
                    h.Unpatch(AccessTools.Method(typeof(Map),"MapUpdate"),AccessTools.Method(typeof(Probe),nameof(IsolationThrow)));
                    // Startup poisoning and GPU cleanup are exercised by the independent source harness.
                    // Here exercise a failed individual builder in the real runtime plus a healthy icon.
                    if(!faultBaseline)
                    {
                        if(VersionControl.CurrentVersionString.StartsWith("1.6"))IsolationPartialFaces(h);
                        var cache=(IDictionary)AccessTools.Field(typeof(ReactionArt),"bubbles").GetValue(null);
                        cache.Remove(ReactionChoices.Heart);
                        var builder=AccessTools.Method(typeof(ReactionArt),"BuildTexture");
                        h.Patch(builder,prefix:new HarmonyMethod(typeof(Probe),nameof(IsolationThrow)));
                        isolationFault="ReactionArt.BuildTexture";int hits=isolationHits;
                        var texture=ReactionArt.Texture(ReactionChoices.Heart);
                        IsolationRequire(isolationHits==hits+1&&texture!=null,"one failed icon returns a fallback");
                        IsolationRequire(ReactionArt.Texture(ReactionChoices.HandWave)!=null,"healthy icon remains usable");
                        IsolationRequire(ReactionArt.Texture(ReactionChoices.Heart)==texture&&isolationHits==hits+1,"failed icon fallback is cached without repeated work");
                        File.WriteAllBytes(Path.Combine(dir,"fallback-heart.png"),texture.EncodeToPNG());
                        File.WriteAllBytes(Path.Combine(dir,"healthy-wave.png"),ReactionArt.Texture(ReactionChoices.HandWave).EncodeToPNG());
                        h.Unpatch(builder,AccessTools.Method(typeof(Probe),nameof(IsolationThrow)));
                    }
                    phase=1;Wait(.5f);return;
                case 1:
                    isolationReady=false;
                    IsolationRequire(PhotoCapture.Capture(map,center.ToVector3Shifted(),19f,r=>{isolationPhoto=r;isolationReady=true;}),"next ordinary capture accepted");
                    phase=2;return;
                case 2:
                    if(!isolationReady)return;
                    IsolationRequire(isolationPhoto?.png!=null&&!PhotoCapture.Busy&&!PhotoCapture.HideGuides,"next ordinary capture saves and clears state");
                    File.WriteAllBytes(Path.Combine(dir,"recovered-photo.png"),isolationPhoto.png);
                    File.WriteAllText(Path.Combine(dir,"fault-isolation-result.json"),"{\"ok\":true,\"baseline\":"+(faultBaseline?"true":"false")+",\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"injected\":"+isolationHits+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    active=false;Application.Quit();return;
            }
        }
    }
}
