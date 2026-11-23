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
    public static partial class Probe
    {
        private static bool renderEffectsMode, renderOrderMode;
        private static bool effectsSortEnabled, effectsBlockEnabled;
        private static int effectsSortBefore;
        private static int effectsCase;
        private static bool effectsReady, effectsFailNext, effectsEligible;
        private static PhotoCapture.Result effectsResult;
        private static readonly string[] normalEffectsNames = { "near-on", "far-on", "far-off", "near-off", "far-bright", "far-after-bright", "failure" };
        private static readonly string[] orderEffectsNames = { "sort-off-block-off", "sort-on-block-off", "sort-off-block-on", "sort-on-block-on", "sort-on-block-off-repeat" };
        private static string[] effectsNames => renderOrderMode ? orderEffectsNames : normalEffectsNames;
        private static readonly List<string> effectsFacts = new List<string>();
        private static readonly List<Pawn> effectsPeople = new List<Pawn>();
        private static CameraState effectsState;

        private sealed class CameraState
        {
            internal readonly Vector3 pos=Find.Camera.transform.position;
            internal readonly float size=Find.Camera.orthographicSize, aspect=Find.Camera.aspect, far=Find.Camera.farClipPlane;
            internal readonly Vector3 root=(Vector3)AccessTools.Field(typeof(CameraDriver),"rootPos").GetValue(Find.CameraDriver);
            internal readonly float rootSize=(float)AccessTools.Field(typeof(CameraDriver),"rootSize").GetValue(Find.CameraDriver);
            internal readonly RenderTexture target=Find.Camera.targetTexture, activeTarget=RenderTexture.active;
            internal readonly bool enabled=Find.CameraDriver.enabled;
            internal readonly Color light=MatBases.LightOverlay.color;
            internal readonly float saturation=Find.CameraColor.saturation;
            internal readonly HighlightStyleMode style=Prefs.HighlightStyleMode;
            internal readonly DotHighlightDisplayMode display=Prefs.DotHighlightDisplayMode;
            internal bool Matches() => Vector3.Distance(pos,Find.Camera.transform.position)<.001f
                && Mathf.Abs(size-Find.Camera.orthographicSize)<.001f && Mathf.Abs(aspect-Find.Camera.aspect)<.001f
                && Mathf.Abs(far-Find.Camera.farClipPlane)<.001f && target==Find.Camera.targetTexture && activeTarget==RenderTexture.active
                && enabled==Find.CameraDriver.enabled && light==MatBases.LightOverlay.color && Mathf.Abs(saturation-Find.CameraColor.saturation)<.001f
                && Vector3.Distance(root,(Vector3)AccessTools.Field(typeof(CameraDriver),"rootPos").GetValue(Find.CameraDriver))<.001f
                && Mathf.Abs(rootSize-(float)AccessTools.Field(typeof(CameraDriver),"rootSize").GetValue(Find.CameraDriver))<.001f
                && style==Prefs.HighlightStyleMode && display==Prefs.DotHighlightDisplayMode;
        }

        private static void EffectsRequire(bool good,string message)
        {
            checks.Add((good?"PASS: ":"FAIL: ")+message);
            if(!good)throw new InvalidOperationException(message);
        }
        private static void EffectsThrowPrefix()
        {
            if(effectsFailNext){effectsFailNext=false;throw new InvalidOperationException("MF deliberate capture restoration probe");}
        }
        private static void EffectsCapturePrefix() { effectsState=new CameraState(); }
        // Probe-only interventions on our two patches; no game or other-mod patch is removed.
        private static bool EffectsSortGate() => effectsSortEnabled;
        private static bool EffectsBlockGate(ref bool __result)
        {
            if(effectsBlockEnabled)return true;
            __result=true;return false;
        }
        private static void EffectsCapturePostfix()
        {
            EffectsRequire(effectsState.Matches(),effectsNames[effectsCase]+" restores camera, light, saturation, targets and highlight preferences");
            EffectsRequire(!NativePhotoOrder.Active,effectsNames[effectsCase]+" releases native rendering scope");
        }
        private static void RenderEffectsStep()
        {
            switch(phase)
            {
                case 0:
                    deadline=Time.realtimeSinceStartup+240f;map=Find.CurrentMap;center=map.Center;
                    CloseLog();Find.TickManager.Pause();Find.Storyteller.storytellerComps.Clear();Find.Storyteller.incidentQueue.Clear();
                    FxSet(false);MomentFramesMod.Settings.autoCaptureEnabled=false;MomentFramesMod.Settings.brightPhotos=false;MomentFramesMod.Settings.captureReactionMode=PhotoReactions.ModeOff;
                    map.fogGrid.ClearAllFog();
                    foreach(var c in CellRect.CenteredOn(center,24))
                    {
                        foreach(var t in c.GetThingList(map).ToList())if(!(t is Pawn)&&t.def.destroyable)t.Destroy();
                        map.roofGrid.SetRoof(c,null);map.areaManager.NoRoof[c]=true;map.terrainGrid.SetTerrain(c,TerrainDef.Named("WoodPlankFloor"));
                    }
                    effectsPeople.AddRange(map.mapPawns.FreeColonistsSpawned.Take(3));
                    for(int i=0;i<effectsPeople.Count;i++)
                    {
                        var pawn=effectsPeople[i];pawn.jobs.StopAll();pawn.drafter.Drafted=true;pawn.Position=center+new IntVec3(-4+i*4,0,1);pawn.Rotation=Rot4.South;
                    }
                    var cat=PawnGenerator.GeneratePawn(PawnKindDef.Named("Cat"));GenSpawn.Spawn(cat,center+new IntVec3(5,0,-2),map);cat.Rotation=Rot4.East;
                    var fire=(Fire)ThingMaker.MakeThing(ThingDefOf.Fire);fire.fireSize=1.2f;GenSpawn.Spawn(fire,center+new IntVec3(-5,0,-3),map);
                    var lamp=ThingMaker.MakeThing(ThingDefOf.TorchLamp);GenSpawn.Spawn(lamp,center+new IntVec3(0,0,-3),map);
                    lamp.TryGetComp<CompRefuelable>()?.Refuel(50f);
                    FleckMaker.ThrowSmoke((center+new IntVec3(5,0,3)).ToVector3Shifted(),map,1.5f);
                    new Harmony("MomentFrames.Probe.RenderEffects").Patch(AccessTools.Method(typeof(PhotoCapture),"Render"),
                        prefix:new HarmonyMethod(typeof(Probe),nameof(EffectsCapturePrefix)),postfix:new HarmonyMethod(typeof(Probe),nameof(EffectsCapturePostfix)));
                    new Harmony("MomentFrames.Probe.RenderEffects").Patch(AccessTools.Method(typeof(NativePhotoOrder),"Render"),
                        prefix:new HarmonyMethod(typeof(Probe),nameof(EffectsThrowPrefix)));
                    if(renderOrderMode)
                    {
                        var h=new Harmony("MomentFrames.Probe.RenderOrder");
                        h.Patch(AccessTools.Method(typeof(Patch_NativePhotoOrder),"Prefix"),prefix:new HarmonyMethod(typeof(Probe),nameof(EffectsSortGate)));
                        h.Patch(AccessTools.Method(typeof(CaptureSilhouetteGuides),"Prefix"),prefix:new HarmonyMethod(typeof(Probe),nameof(EffectsBlockGate)));
                    }
                    effectsCase=0;phase=1;Wait(1f);return;
                case 1:
                    Prefs.HighlightStyleMode=HighlightStyleMode.Silhouettes;
                    effectsSortEnabled=effectsCase!=0&&effectsCase!=2;
                    effectsBlockEnabled=effectsCase==2||effectsCase==3;
                    Prefs.DotHighlightDisplayMode=!renderOrderMode&&(effectsCase==2||effectsCase==3)?DotHighlightDisplayMode.None:DotHighlightDisplayMode.HighlightAll;
                    float zoom=!renderOrderMode&&(effectsCase==0||effectsCase==3)?15f:Find.CameraDriver.config.sizeRange.max;
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(),zoom);
                    MomentFramesMod.Settings.brightPhotos=!renderOrderMode&&(effectsCase==4||effectsCase==6);
                    phase=2;Wait(.6f);return;
                case 2:
                    CloseLog();Shot(effectsNames[effectsCase]+"-map-before.png");phase=3;Wait(.4f);return;
                case 3:
                    effectsReady=false;effectsFailNext=!renderOrderMode&&effectsCase==6;
                    effectsSortBefore=NativePhotoOrder.SortCount;
                    effectsEligible=SilhouetteUtility.ShouldDrawSilhouette(effectsPeople[0]);
                    effectsFacts.Add("{\"case\":"+Json(effectsNames[effectsCase])+",\"zoom\":"+Find.CameraDriver.ZoomRootSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        +",\"silhouetteEligible\":"+(effectsEligible?"true":"false")
                        +",\"sortEnabled\":"+(effectsSortEnabled?"true":"false")+",\"blockEnabled\":"+(effectsBlockEnabled?"true":"false")+"}");
                    EffectsRequire(PhotoCapture.Capture(map,center.ToVector3Shifted(),14f,r=>{effectsResult=r;effectsReady=true;},1.5f),"capture accepted "+effectsNames[effectsCase]);
                    phase=4;return;
                case 4:
                    if(!effectsReady)return;
                    EffectsRequire(!PhotoCapture.Busy&&!PhotoCapture.HideGuides,"capture releases pending guides "+effectsNames[effectsCase]);
                    EffectsRequire(SilhouetteUtility.ShouldDrawSilhouette(effectsPeople[0])==effectsEligible,"normal map highlight behavior resumes "+effectsNames[effectsCase]);
                    if(renderOrderMode)
                    {
                        EffectsRequire(effectsEligible,"zoomed-out silhouettes eligible in every comparison");
                        EffectsRequire(effectsSortEnabled?NativePhotoOrder.SortCount>effectsSortBefore:NativePhotoOrder.SortCount==effectsSortBefore,"sort gate exercised "+effectsNames[effectsCase]);
                    }
                    if(!renderOrderMode&&effectsCase==6)EffectsRequire(effectsResult==null,"deliberate render failure returns no photograph");
                    else {EffectsRequire(effectsResult?.png!=null,"native PNG produced "+effectsNames[effectsCase]);File.WriteAllBytes(Path.Combine(dir,effectsNames[effectsCase]+".png"),effectsResult.png);}
                    phase=5;Wait(.4f);return;
                case 5:
                    CloseLog();Shot(effectsNames[effectsCase]+"-map-after.png");phase=6;Wait(.4f);return;
                case 6:
                    if(++effectsCase<effectsNames.Length){phase=1;return;}
                    File.WriteAllText(Path.Combine(dir,"render-effects-result.json"),"{\"ok\":true,\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"cases\":["+string.Join(",",effectsFacts)+"],\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    active=false;Application.Quit();return;
            }
        }
    }
}
