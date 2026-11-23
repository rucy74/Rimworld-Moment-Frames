using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Owned by the setup window, never by the map pawn or the game's portrait cache.
    internal sealed class PhotoFacePreview : IDisposable
    {
        private sealed class Entry
        {
            internal int choice;
            internal bool busy;
            internal bool supported;
            internal RenderTexture texture;
            internal int used;
        }
        private readonly Dictionary<Pawn,Entry> cache=new Dictionary<Pawn,Entry>();
        private Pawn hovered;
        private int hoverChoice;
        private Rect visible;
        private Vector2 scroll, hoverAnchor;
        internal int Builds { get; private set; }
        internal int HoverDraws { get; private set; }
        internal int Count => cache.Count;
        private static bool warned;
        [ThreadStatic] internal static Pawn RenderingPawn;
        [ThreadStatic] internal static object RenderingFrame;
        internal static int GraphicOverrides;

        internal void Begin(Rect visible,Vector2 scroll)
        {
            hovered=null;
            this.visible=visible;this.scroll=scroll;
        }
        internal Texture Get(Pawn pawn,int choice)
        {
            bool busy=PhotoFaces.Available && PhotoFaces.HasTemporary(pawn);
            if(cache.TryGetValue(pawn,out var entry) && entry.choice==choice && entry.busy==busy)
            {entry.used=Time.frameCount;return entry.texture;}
            if(entry!=null)Release(entry);
            if(cache.Count>=32 && !cache.ContainsKey(pawn))
            {
                var oldest=cache.OrderBy(k=>k.Value.used).First();Release(oldest.Value);cache.Remove(oldest.Key);
            }
            entry=new Entry{choice=choice,busy=busy,used=Time.frameCount};cache[pawn]=entry;
            var priorPawn=RenderingPawn;var priorFrame=RenderingFrame;
            try
            {
                // No temporary animation or actor state is changed. The optional hook
                // only supplies alternate graphics during this one synchronous render.
                RenderingPawn=pawn;RenderingFrame=busy?null:PhotoFaces.FrameFor(pawn,choice,true);
                entry.supported=choice==0 || RenderingFrame!=null;
                entry.texture=new RenderTexture(256,256,24){name="MF face preview",filterMode=FilterMode.Bilinear};
                entry.texture.Create();
                float headSize=Mathf.Max(.4f,pawn.ageTracker.CurLifeStage.headSizeFactor??1f);
                var offset=pawn.Drawer.renderer.BaseHeadOffsetAt(Rot4.South);
                Find.PawnCacheRenderer.RenderPawn(pawn,entry.texture,offset,2f/headSize,0f,Rot4.South,
                    pawn.health.hediffSet.HasHead,true,true,portrait:true);
                Builds++;
                return entry.texture;
            }
            catch(Exception e)
            {
                Release(entry);
                if(!warned){warned=true;Log.Warning("[MomentFrames] face preview unavailable: "+e.GetBaseException().Message);}
                return null;
            }
            finally {RenderingPawn=priorPawn;RenderingFrame=priorFrame;}
        }
        internal void Draw(Pawn pawn,Rect rect,int choice)
        {
            if(Event.current.type!=EventType.Repaint)return;
            Rect inWindow=new Rect(visible.position+rect.position-scroll,rect.size);
            if(!visible.Overlaps(inWindow))return;
            GUI.DrawTexture(rect,Get(pawn,choice)??Dialog_BirthdaySetup.PortraitFor(pawn));
            if(Mouse.IsOver(rect) && visible.Contains(visible.position+Event.current.mousePosition-scroll))
            {hovered=pawn;hoverChoice=choice;hoverAnchor=new Vector2(inWindow.xMax,inWindow.y);}
            TooltipHandler.TipRegion(rect,"MF_FacePreviewHelp".Translate());
        }
        internal Rect PopupRect(Rect bounds,Vector2 anchor)
        {
            float width=Mathf.Min(250f,bounds.width-16f),height=Mathf.Min(310f,bounds.height-16f);
            float x=anchor.x+12f;
            if(x+width>bounds.xMax-8f)x=anchor.x-76f-width;
            return new Rect(Mathf.Clamp(x,bounds.x+8f,bounds.xMax-width-8f),Mathf.Clamp(anchor.y,bounds.y+8f,bounds.yMax-height-8f),width,height);
        }
        internal string PreviewLabel(Pawn pawn,int choice)
        {
            if(cache.TryGetValue(pawn,out var entry) && entry.choice==choice)
            {
                if(entry.busy)return "MF_FacePreviewBusy".Translate();
                if(!entry.supported)return PhotoFaces.Label(0)+" ("+"MF_FaceUnsupported".Translate()+")";
            }
            return PhotoFaces.Label(choice);
        }
        internal void DrawHover(Rect bounds)
        {
            if(hovered==null || Event.current.type!=EventType.Repaint)return;
            HoverDraws++;
            Rect panel=PopupRect(bounds,hoverAnchor);
            Widgets.DrawBoxSolid(panel,AlbumStyle.Theme.Paper);Widgets.DrawBox(panel);
            float side=panel.width-20f;
            GUI.DrawTexture(new Rect(panel.x+10f,panel.y+8f,side,side),Get(hovered,hoverChoice)??Dialog_BirthdaySetup.PortraitFor(hovered));
            SharedPhotoUI.Label(new Rect(panel.x+12f,panel.y+side+10f,panel.width-24f,panel.height-side-16f),
                hovered.LabelShort+" · "+PreviewLabel(hovered,hoverChoice));
        }
        private static void Release(Entry entry)
        {if(entry.texture!=null){entry.texture.Release();UnityEngine.Object.Destroy(entry.texture);entry.texture=null;}}
        public void Dispose(){foreach(var entry in cache.Values)Release(entry);cache.Clear();hovered=null;}
    }

    // Patch one non-generic FA worker; no writes to Shape/animation lists or pawn caches.
    [HarmonyPatch]
    internal static class Patch_FacePreviewGraphic
    {
        private static readonly Type worker=AccessTools.TypeByName("FacialAnimation.NLFacialAnimationPartNodeWorker");
        private static readonly MethodInfo target=worker==null?null:AccessTools.DeclaredMethod(worker,"GetGraphic");
        private static bool Prepare()=>target!=null;
        private static MethodBase TargetMethod()=>target;
        private static void Postfix(PawnRenderNode node,PawnDrawParms parms,ref Graphic __result)
        {
            if(PhotoFacePreview.RenderingPawn!=parms.pawn || PhotoFacePreview.RenderingFrame==null || !parms.Portrait)return;
            object comp=AccessTools.Field(node.GetType(),"controller").GetValue(node);
            string name=comp.GetType().Name;
            var frame=PhotoFacePreview.RenderingFrame;
            object lid=AccessTools.Field(frame.GetType(),"lidShapeDef").GetValue(frame);
            bool eyeball=name=="EyeballControllerComp";
            if(eyeball && (bool)AccessTools.Field(lid.GetType(),"disableEyeball").GetValue(lid)) {__result=null;return;}
            string part=name=="MouthControllerComp"?"mouth":name=="LidControllerComp"?"lid":eyeball?"eyeball":null;
            if(part==null)return;
            object shape=AccessTools.Field(frame.GetType(),part+"ShapeDef").GetValue(frame);
            // A blink can leave the live eyeball layer invisible. Replacing only the lid
            // then shows white eyes for the whole cached preview. Read the real eye graphic
            // directly for an open-eye pose without changing the pawn's live visibility.
            if(eyeball && shape==null)shape=AccessTools.Field(comp.GetType(),"Shape").GetValue(comp);
            if(shape==null)return;
            var graphics=(IDictionary)AccessTools.Field(comp.GetType(),"graphicList").GetValue(comp);
            object layer=AccessTools.Field(node.GetType(),"layerType").GetValue(node);
            if(eyeball && (layer.ToString()=="L_Option" || layer.ToString()=="R_Option"))
            {
                object animator=parms.pawn.AllComps.FirstOrDefault(c=>c.GetType().FullName=="FacialAnimation.FacialAnimationControllerComp");
                object param=animator==null?null:AccessTools.Property(animator.GetType(),"AnimationParam")?.GetValue(animator,null);
                if(param!=null && AccessTools.Field(param.GetType(),"enableHighlight")?.GetValue(param) is bool highlight && !highlight)
                {__result=null;return;}
            }
            if(!graphics.Contains(layer))return;
            object collection=graphics[layer];if(collection==null)return;
            object rotation=AccessTools.Field(node.GetType(),"rotDrawMode").GetValue(node);
            __result=(Graphic)AccessTools.Method(collection.GetType(),"GetSubGraphic").Invoke(collection,new[]{parms.pawn,shape,rotation});
            PhotoFacePreview.GraphicOverrides++;
        }
    }
}
