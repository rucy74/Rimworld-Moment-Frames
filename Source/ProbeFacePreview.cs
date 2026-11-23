using System;
using System.Collections;
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
        private static Pawn previewHoverPawn;
        private static Dialog_BirthdaySetup previewBirthday;
        private static float previewOldScale;
        private static int previewDrawsBeforeScale;
        private static void SavePreview(Texture texture,string name)
        {
            var previous=RenderTexture.active;Texture2D copy=null;
            try
            {
                RenderTexture.active=(RenderTexture)texture;
                copy=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false);
                copy.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0);copy.Apply();
                File.WriteAllBytes(Path.Combine(dir,name),copy.EncodeToPNG());
            }
            finally {RenderTexture.active=previous;if(copy!=null)UnityEngine.Object.Destroy(copy);}
        }
        private static void FacePreviewStep()
        {
            switch(phase)
            {
                case 700:
                    var pawn=studioFamily[0];pawn.Rotation=Rot4.South;
                    using(var preview=new PhotoFacePreview())
                    {
                        var normal=preview.Get(pawn,0);StudioCheck(normal!=null,"normal preview renders");SavePreview(normal,"face-0.png");
                        var comps=pawn.AllComps.Where(c=>c.GetType().Namespace=="FacialAnimation").ToList();
                        var shapes=comps.Select(c=>Tuple.Create(c,AccessTools.Field(c.GetType(),"Shape"))).Where(t=>t.Item2!=null).ToList();
                        var before=shapes.Select(t=>t.Item2.GetValue(t.Item1)).ToList();
                        int prior=PhotoFacePreview.GraphicOverrides;
                        for(int choice=1;choice<=4;choice++)
                        {
                            var image=preview.Get(pawn,choice);StudioCheck(image!=null,"preview choice "+choice+" renders");SavePreview(image,"face-"+choice+".png");
                            StudioCheck(PhotoFacePreview.RenderingPawn==null&&PhotoFacePreview.RenderingFrame==null,"preview scope released for "+choice);
                            StudioCheck(shapes.Select(t=>t.Item2.GetValue(t.Item1)).SequenceEqual(before),"actual pawn shapes preserved for "+choice);
                        }
                        StudioCheck(PhotoFaces.Available?PhotoFacePreview.GraphicOverrides>prior:PhotoFacePreview.GraphicOverrides==prior,"optional FA graphic override follows availability");
                        StudioCheck(PhotoFaces.Available?preview.PreviewLabel(pawn,4)==PhotoFaces.Label(4):preview.PreviewLabel(pawn,4).Contains("MF_FaceUnsupported".Translate()),"preview label describes a supported pose or its fallback");
                        SavePreview(preview.Get(pawn,0),"face-reset.png");
                        int builds=preview.Builds;var cached=preview.Get(pawn,4);
                        builds=preview.Builds;
                        bool reused=true;for(int i=0;i<60;i++)reused &= ReferenceEquals(cached,preview.Get(pawn,4));
                        StudioCheck(reused,"cached preview reused across sixty UI requests");
                        StudioCheck(preview.Builds==builds,"unchanged portrait does not render on every UI frame");
                        if(PhotoFaces.Available)
                        {
                            var record=new PhotoRecord{peopleIds=new List<string>{pawn.GetUniqueLoadID()},facePawnIds=new List<string>{pawn.GetUniqueLoadID()},faceChoices=new List<int>{1}};
                            using(var foreign=PhotoFaces.Begin(map,record))
                            {
                                StudioCheck(foreign.Owned.Count==1,"foreign expression fixture active");
                                var own=foreign.Owned[0];var old=own.Item3.Cast<object>().ToArray();prior=PhotoFacePreview.GraphicOverrides;
                                StudioCheck(preview.Get(pawn,2)!=null,"preview while another expression is active renders");
                                StudioCheck(old.SequenceEqual(own.Item3.Cast<object>())&&PhotoFacePreview.GraphicOverrides==prior,"preview preserves foreign expression without overriding it");
                            }
                        }
                        StudioCheck(!PhotoFaces.HasTemporary(pawn),"preview leaves no temporary animation behind");
                        Rect bounds=new Rect(0,0,400,330);var popup=preview.PopupRect(bounds,new Vector2(395,325));
                        StudioCheck(bounds.Contains(popup.min)&&bounds.Contains(popup.max),"enlarged portrait stays within a small window");
                        preview.Dispose();StudioCheck(preview.Count==0&&!((RenderTexture)cached).IsCreated(),"closing preview releases textures and pawn references");
                    }
                    previewHoverPawn=studioChild;Wait(.4f);phase=701;return;
                case 701:
                    Shot("preview-group-hover.png");Wait(.3f);phase=702;return;
                case 702:
                    var groupCache=Traverse.Create(studioDialog).Field("facePreview").GetValue<PhotoFacePreview>();
                    StudioCheck(groupCache.Builds>0,"group setup uses live portrait preview");
                    StudioCheck(groupCache.HoverDraws>0,"group hover popup really drawn");
                    studioDialog.Close();StudioCheck(groupCache.Count==0,"group setup close releases preview cache");
                    previewBirthday=new Dialog_BirthdaySetup(studioChild,6,null,studioBackdrop.Position);
                    var faces=Traverse.Create(previewBirthday).Field("faces").GetValue<Dictionary<string,int>>();
                    faces[studioChild.GetUniqueLoadID()]=2;
                    Find.WindowStack.Add(previewBirthday);Wait(.5f);phase=703;return;
                case 703:
                    Shot("preview-birthday-hover.png");Wait(.3f);phase=704;return;
                case 704:
                    var birthdayCache=Traverse.Create(previewBirthday).Field("facePreview").GetValue<PhotoFacePreview>();
                    StudioCheck(birthdayCache.Builds>0,"birthday setup uses live portrait preview");
                    StudioCheck(birthdayCache.HoverDraws>0,"birthday hover popup really drawn");
                    previewDrawsBeforeScale=birthdayCache.HoverDraws;
                    previewOldScale=Prefs.UIScale;Prefs.UIScale=1.25f;
                    previewBirthday.windowRect=new Rect(240,20,800,740);Wait(.5f);phase=705;return;
                case 705:
                    Shot("preview-birthday-scale125.png");Wait(.3f);phase=706;return;
                case 706:
                    var scaledCache=Traverse.Create(previewBirthday).Field("facePreview").GetValue<PhotoFacePreview>();
                    StudioCheck(scaledCache.HoverDraws>previewDrawsBeforeScale,"scaled birthday preview drawn");
                    Prefs.UIScale=previewOldScale;previewBirthday.Close();
                    StudioCheck(scaledCache.Count==0,"birthday setup close releases preview cache");
                    previewHoverPawn=null;
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"preview-result.json"),"{\"ok\":true,\"fa\":"+(PhotoFaces.Available?"true":"false")+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
            }
        }
        [HarmonyPatch(typeof(PhotoFacePreview),"Draw")]
        private static class Patch_ProbePreviewHover
        {
            private static void Prefix(Pawn pawn,Rect rect,out Tuple<Vector2,bool> __state)
            {
                bool inject=active&&pawn==previewHoverPawn;
                __state=Tuple.Create(Event.current.mousePosition,inject);
                if(inject){Event.current.mousePosition=rect.center;Widgets.mouseOverScrollViewStack.Push(true);}
            }
            private static void Postfix(Tuple<Vector2,bool> __state)
            {if(__state.Item2)Widgets.mouseOverScrollViewStack.Pop();Event.current.mousePosition=__state.Item1;}
        }
    }
}
