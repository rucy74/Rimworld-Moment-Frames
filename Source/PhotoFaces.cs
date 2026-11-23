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
    /// <summary>Optional NL Facial Animation adapter. Own objects only; never calls the engine's clear-all temporary API.</summary>
    internal static class PhotoFaces
    {
        private static readonly Type controller=AccessTools.TypeByName("FacialAnimation.FacialAnimationControllerComp");
        private static readonly Type animation=AccessTools.TypeByName("FacialAnimation.FaceAnimation");
        private static readonly Type definition=AccessTools.TypeByName("FacialAnimation.FaceAnimationDef");
        private static readonly Type frame=AccessTools.TypeByName("FacialAnimation.FaceAnimationDef+AnimationFrame");
        private static readonly FieldInfo temporary=controller==null?null:AccessTools.Field(controller,"forcedTemporaryAnimationList");
        private static readonly FieldInfo frames=definition==null?null:AccessTools.Field(definition,"animationFrames");
        private static readonly MethodInfo update=controller==null?null:AccessTools.Method(controller,"UpdateAnimation");
        private static readonly MethodInfo status=controller==null?null:AccessTools.Method(controller,"UpdateStatus");
        internal static bool Available => temporary!=null && frames!=null && update!=null && status!=null && animation!=null && frame!=null;
        internal static readonly int[] Options={0,1,2,3,4};
        internal static string Label(int value)=>("MF_Face"+Mathf.Clamp(value,0,4)).Translate();
        internal static int Choice(IDictionary<string,int> choices,string id)=>choices!=null && choices.TryGetValue(id,out int value)?Mathf.Clamp(value,0,4):0;
        internal static Dictionary<string,int> Read(IList<string> ids,IList<int> values)
        {
            var result=new Dictionary<string,int>();
            for(int i=0;i<Math.Min(ids?.Count??0,values?.Count??0);i++)
                if(!string.IsNullOrEmpty(ids[i]) && values[i]>=0 && values[i]<=4)result[ids[i]]=values[i];
            return result;
        }
        private static object Controller(Pawn p)=>Available?p?.AllComps.FirstOrDefault(c=>controller.IsInstanceOfType(c)):null;
        private static object Shape(Pawn p,string part,string name,bool preview)
        {
            var component=p.AllComps.FirstOrDefault(c=>c.GetType().FullName=="FacialAnimation."+part+"ControllerComp");
            var type=component==null?null:AccessTools.Property(component.GetType(),"FaceType")?.GetValue(component,null);
            if(type==null)return null;
            var path=(string)AccessTools.Field(type.GetType(),"texPath")?.GetValue(type);
            bool unisex=(bool?)AccessTools.Field(type.GetType(),"enableUnisexTexPath")?.GetValue(type)??false;
            string facing=preview?"south":p.Rotation==Rot4.East?"east":p.Rotation==Rot4.West?"west":p.Rotation==Rot4.North?"north":"south";
            Type shape=AccessTools.TypeByName("FacialAnimation."+part+"ShapeDef");
            object def=shape==null?null:typeof(DefDatabase<>).MakeGenericType(shape).GetMethod("GetNamedSilentFail").Invoke(null,new object[]{name});
            if(def==null)return null;
            string prefix=path+"/"+(unisex?"Unisex":p.gender.ToString())+"/";
            bool Exists(string candidate,string side)=>ContentFinder<Texture2D>.Get(prefix+candidate+"_"+side,false)!=null
                || part=="Lid" && ContentFinder<Texture2D>.Get(prefix+candidate+"_cover_"+side,false)!=null;
            bool Has(string candidate)=>Exists(candidate,facing) || facing=="west" && Exists(candidate,"east");
            if(Has(name))return def;
            // FA deliberately maps, for example, surprise to open. Respect that declared
            // alternate, but do not advertise sad/smile when they only resolve to normal.
            string alternate=AccessTools.Property(shape,"GetAltShapeDef")?.GetValue(def,null) as string;
            return !string.IsNullOrEmpty(alternate) && alternate!="normal" && alternate!=name && Has(alternate)?def:null;
        }
        internal static object FrameFor(Pawn p,int choice,bool preview=false)
        {
            if(!Available || choice<=0 || choice>4 || p==null || p.Dead || !preview && p.Rotation==Rot4.North || Controller(p)==null)return null;
            object mouth=Shape(p,"Mouth",choice==1 || choice==2?"smile":choice==3?"surprise":"sad",preview);
            object lid=Shape(p,"Lid",choice==2?"close":"normal",preview);
            if(mouth==null || lid==null)return null;
            object value=Activator.CreateInstance(frame);
            frame.GetField("duration").SetValue(value,600);
            frame.GetField("mouthShapeDef").SetValue(value,mouth);
            frame.GetField("lidShapeDef").SetValue(value,lid);
            return value;
        }
        internal static bool Supports(Pawn pawn,int choice)
        {
            if(choice==0)return true;
            try { return FrameFor(pawn,choice,true)!=null; }
            catch(Exception e) { Warn(e);return false; }
        }
        internal static bool HasTemporary(Pawn pawn) => Controller(pawn) is object comp && temporary.GetValue(comp) is IList list && list.Count>0;
        private static bool warned;
        private static void Warn(Exception e)
        {
            if(warned)return;warned=true;
            Log.Warning("[MomentFrames] optional facial posing unavailable: "+e.GetBaseException().Message);
        }
        internal sealed class Lease : IDisposable
        {
            internal readonly List<Tuple<Pawn,object,IList,object>> Owned=new List<Tuple<Pawn,object,IList,object>>();
            public void Dispose()
            {
                foreach(var entry in Owned)
                    try
                    {
                        // Other mods may have cleared/replaced our animation. Never restore an old list over theirs.
                        bool present=entry.Item3.Contains(entry.Item4);
                        if(present)entry.Item3.Remove(entry.Item4);
                        if(present && !entry.Item1.Destroyed) {status.Invoke(entry.Item2,new object[]{Find.TickManager.TicksGame});update.Invoke(entry.Item2,null);PortraitsCache.SetDirty(entry.Item1);}
                    }
                    catch(Exception e){Warn(e);}
                Owned.Clear();
            }
        }
        internal static Lease Begin(Map map,PhotoRecord record)
        {
            var lease=new Lease();
            if(!Available || map==null || record==null || record.facePawnIds?.Count==0)return lease;
            try
            {
                var choices=Read(record.facePawnIds,record.faceChoices);
                foreach(var pawn in PhotoCapture.VisiblePawns(map).Where(p=>p!=null && !p.Dead && record.Has(p)).Distinct())
                {
                    int choice=Choice(choices,pawn.GetUniqueLoadID());if(choice==0)continue;
                    try
                    {
                        object comp=Controller(pawn);if(comp==null)continue;
                        var list=temporary.GetValue(comp) as IList;
                        if(list==null || list.Count>0)continue; // another effect owns the face
                        object pose=FrameFor(pawn,choice);if(pose==null)continue;
                        object def=Activator.CreateInstance(definition);
                        var poseFrames=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(frame));poseFrames.Add(pose);frames.SetValue(def,poseFrames);
                        object item;
                        Rand.PushState(pawn.thingIDNumber);
                        try{item=Activator.CreateInstance(animation,new[]{def,(object)Find.TickManager.TicksGame});}
                        finally{Rand.PopState();}
                        list.Add(item);lease.Owned.Add(Tuple.Create(pawn,comp,list,item));
                        status.Invoke(comp,new object[]{Find.TickManager.TicksGame});update.Invoke(comp,null);PortraitsCache.SetDirty(pawn);
                    }
                    catch(Exception e){Warn(e);}
                }
                return lease;
            }
            catch
            {
                // Enumeration can fail after some actors were posed, before the caller owns this lease.
                lease.Dispose();
                throw;
            }
        }
        internal static void Button(Rect rect,Pawn pawn,bool selected,IDictionary<string,int> choices)
        {
            if(!Available || choices==null)return;
            if(SharedPhotoUI.Button(rect,"MF_FaceChoice".Translate(Label(Choice(choices,pawn.GetUniqueLoadID()))),selected))
            {
                var options=new List<FloatMenuOption>();
                foreach(int choice in Options)
                {
                    int pick=choice;bool supported=Supports(pawn,pick);
                    options.Add(new FloatMenuOption(Label(pick)+(supported?"":" — "+"MF_FaceUnsupported".Translate()),
                        supported?(Action)(()=>choices[pawn.GetUniqueLoadID()]=pick):null));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(rect,"MF_FaceHelp".Translate());
        }
    }
}
