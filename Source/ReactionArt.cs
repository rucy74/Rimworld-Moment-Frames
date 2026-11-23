using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Authored speech-bubble expressions, used visually without performing an interaction.</summary>
    [StaticConstructorOnStartup]
    internal static class ReactionArt
    {
        private const int Size=128;
        private const float SymbolScale=.64f; // MoteBubble.DrawAt uses the same proportion.
        private static readonly Dictionary<int,Texture2D> bubbles=new Dictionary<int,Texture2D>();
        private static readonly Dictionary<string,Texture2D> native=new Dictionary<string,Texture2D>();
        static ReactionArt()
        {
            // Native flecks can render on workers. Read Unity textures on startup's main thread.
            Texture(ReactionChoices.Heart); Texture(ReactionChoices.HandWave);
            Texture(102); Texture(103); Texture(104); Texture(105);
            for(int icon=0;icon<=(int)PhotoIcon.Paw;icon++) Texture(icon);
        }
        internal static Texture2D Texture(int icon)
        {
            // A failed icon is cached too: render workers must not retry GPU work every frame.
            if(bubbles.TryGetValue(icon,out var texture)) return texture;
            try { texture=BuildTexture(icon); }
            catch(Exception e)
            {
                Warn("expression "+icon,e);
                try { texture=PhotoIconArt.Texture(FallbackIcon(icon)); }
                catch(Exception fallback) { Warn("fallback expression "+icon,fallback);texture=null; }
            }
            bubbles[icon]=texture;return texture;
        }
        private static Texture2D BuildTexture(int icon)
        {
            Texture2D texture=null;
            string authoredPath=AuthoredPath(icon);
            if(authoredPath!=null)
            {
                texture=Readable(authoredPath);
                if(texture!=null) return texture;
            }
            Texture2D shell=Readable("Things/Mote/Speech");
            // Keep saved legacy PhotoIcon values readable, including a fallback if an authored asset is missing.
            PhotoIcon fallback=FallbackIcon(icon);
            Texture2D symbol=icon==ReactionChoices.Heart?Readable("Things/Mote/SpeechSymbols/Romance")
                :icon==ReactionChoices.HandWave?Readable("Things/Mote/SpeechSymbols/Chitchat")
                :icon==(int)PhotoIcon.Smile?Readable("Things/Mote/SpeechSymbols/KindWords")
                :PhotoIconArt.Texture(fallback);
            if(symbol==null) symbol=PhotoIconArt.Texture(fallback);
            bool sourceColor=icon==ReactionChoices.Heart || icon==ReactionChoices.HandWave || icon==(int)PhotoIcon.Smile;
            Color tint=sourceColor?Color.white:AlbumStickers.ColorFor(fallback);
            var pixels=new Color32[Size*Size];
            if(shell!=null) Overlay(pixels,Size,shell.GetPixels32(),shell.width,shell.height,0,0,Size,Color.white,true);
            else FallbackShell(pixels);
            int symbolSize=Mathf.RoundToInt(Size*SymbolScale),inset=(Size-symbolSize)/2;
            Overlay(pixels,Size,symbol.GetPixels32(),symbol.width,symbol.height,inset,inset,symbolSize,tint,sourceColor);
            try
            {
                texture=new Texture2D(Size,Size,TextureFormat.RGBA32,false);
                texture.name="MF_SpeechExpression_"+icon;texture.filterMode=FilterMode.Bilinear;
                texture.wrapMode=TextureWrapMode.Clamp;texture.hideFlags=HideFlags.HideAndDontSave;
                texture.SetPixels32(pixels);texture.Apply(false,false);return texture;
            }
            catch { if(texture!=null)Cleanup(()=>UnityEngine.Object.Destroy(texture));throw; }
        }
        private static string AuthoredPath(int icon)
        {
            string name;
            switch(icon)
            {
                case 100:name="Heart";break;
                case 101:name="HandWave";break;
                case (int)PhotoIcon.Smile:name="Smile";break;
                case (int)PhotoIcon.Star:name="Star";break;
                case (int)PhotoIcon.Music:name="Music";break;
                case 102:name="Peace";break;
                case 103:name="Clap";break;
                case 104:name="Shy";break;
                case 105:name="Surprise";break;
                default:return null;
            }
            return "UI/MomentFrames/Expressions/"+name;
        }
        private static PhotoIcon FallbackIcon(int icon)
        {
            switch(icon)
            {
                case 100:return PhotoIcon.Heart;
                case 101:case 102:case 103:return PhotoIcon.Wave;
                case 104:return PhotoIcon.Smile;
                case 105:return PhotoIcon.Exclamation;
                default:return (PhotoIcon)icon;
            }
        }
        private static Texture2D Readable(string path)
        {
            if(native.TryGetValue(path,out var copy)) return copy;
            RenderTexture previous=null,rt=null;
            bool havePrevious=false,complete=false;
            try
            {
                Texture2D source=ContentFinder<Texture2D>.Get(path,false);
                if(source==null) { native[path]=null;return null; }
                previous=RenderTexture.active;havePrevious=true;
                rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32);
                Graphics.Blit(source,rt);RenderTexture.active=rt;
                copy=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);
                copy.name="MF_Readable_"+path;copy.filterMode=FilterMode.Bilinear;
                copy.wrapMode=TextureWrapMode.Clamp;copy.hideFlags=HideFlags.HideAndDontSave;
                copy.ReadPixels(new Rect(0,0,source.width,source.height),0,0);copy.Apply(false,false);
                native[path]=copy;complete=true;return copy;
            }
            catch(Exception e) { native[path]=null;Warn("texture "+path,e);return null; }
            finally
            {
                if(havePrevious)Cleanup(()=>RenderTexture.active=previous);
                if(rt!=null)Cleanup(()=>RenderTexture.ReleaseTemporary(rt));
                if(!complete && copy!=null)Cleanup(()=>UnityEngine.Object.Destroy(copy));
            }
        }
        private static void Warn(string what,Exception e)
        { try { Log.WarningOnce("[MomentFrames] unavailable "+what+": "+e.GetBaseException().Message,("MF.ReactionArt."+what).GetHashCode()); } catch { } }
        private static void Cleanup(Action action)
        { try { action(); } catch(Exception e) { Warn("reaction texture cleanup",e); } }
        // Straight-alpha composition preserves black symbol outlines and the white shell in every consumer.
        internal static void Overlay(Color32[] target,int targetSize,Color32[] source,int sw,int sh,
            int x,int y,int size,Color tint,bool sourceColor)
        {
            for(int dy=0;dy<size;dy++) for(int dx=0;dx<size;dx++)
            {
                int tx=x+dx,ty=y+dy;if(tx<0||ty<0||tx>=targetSize||ty>=targetSize) continue;
                var sample=source[Mathf.Min(sh-1,dy*sh/size)*sw+Mathf.Min(sw-1,dx*sw/size)];
                float alpha=sample.a/255f*tint.a;if(alpha<=0f) continue;
                int index=ty*targetSize+tx;Color old=target[index];Color paint=sourceColor?(Color)sample:tint;
                float combined=alpha+old.a*(1f-alpha);
                target[index]=new Color((paint.r*alpha+old.r*old.a*(1f-alpha))/combined,
                    (paint.g*alpha+old.g*old.a*(1f-alpha))/combined,
                    (paint.b*alpha+old.b*old.a*(1f-alpha))/combined,combined);
            }
        }
        private static void FallbackShell(Color32[] pixels)
        {
            for(int y=0;y<Size;y++) for(int x=0;x<Size;x++)
            {
                float dx=(x-65f)/47f,dy=(y-68f)/43f;
                bool inside=dx*dx+dy*dy<=1f || x>=24&&x<=40&&y>=19&&y<=39&&x+y>=48;
                if(!inside) continue;
                float innerX=(x-65f)/44f,innerY=(y-68f)/40f;
                pixels[y*Size+x]=innerX*innerX+innerY*innerY<1f?new Color32(255,255,255,255):new Color32(25,25,25,255);
            }
        }
        internal static Color ColorFor(int icon) => Color.white;
        internal static void Draw(Rect rect,int icon,Color color)
        {
            Texture2D texture=Texture(icon);if(texture==null)return;
            var prior=GUI.color;try {GUI.color=color;GUI.DrawTexture(rect,texture,ScaleMode.ScaleToFit,true);}finally{GUI.color=prior;}
        }
    }
}
