using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static Pawn playtestFaceFixturePawn;
        private static bool PlaytestFaceFreeze(object __instance)=>((ThingComp)__instance).parent!=playtestFaceFixturePawn;
        private static void PlaytestFaceRequire(bool value,string label)
        {
            checks.Add((value?"PASS: ":"FAIL: ")+label);
            if(!value)throw new InvalidOperationException("playtest faces: "+label);
        }
        private static object PlaytestFaceDef(string type,string name)
        {
            Type t=AccessTools.TypeByName("FacialAnimation."+type);
            return t==null?null:typeof(DefDatabase<>).MakeGenericType(t).GetMethod("GetNamedSilentFail").Invoke(null,new object[]{name});
        }
        private static byte[] PlaytestFacePixels(RenderTexture source,string name)
        {
            RenderTexture previous=RenderTexture.active;Texture2D pixels=null;
            try
            {
                RenderTexture.active=source;pixels=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);
                pixels.ReadPixels(new Rect(0,0,source.width,source.height),0,0);pixels.Apply();
                File.WriteAllBytes(Path.Combine(dir,name),pixels.EncodeToPNG());
                return pixels.GetRawTextureData();
            }
            finally {RenderTexture.active=previous;if(pixels!=null)UnityEngine.Object.Destroy(pixels);}
        }

        internal static void PlaytestIconChecks()
        {
            Texture2D outline=PhotoIconArt.Texture(PhotoIcon.Heart),filled=PhotoIconArt.Texture(PhotoIcon.HeartFilled);
            PlaytestFaceRequire(outline.GetPixel(outline.width/2,outline.height/2).a<.01f,"unselected heart center stays transparent");
            PlaytestFaceRequire(filled.GetPixel(filled.width/2,filled.height/2).a>.99f,"favorite heart center is filled");
            foreach(var texture in new[]{outline,filled,ArchitectPhotoIcons.TakePhoto,ArchitectPhotoIcons.PhotoSpot})
            {
                Color32[] pixels=texture.GetPixels32();
                PlaytestFaceRequire(pixels.Any(c=>c.a>0 && c.a<255),texture.name+" has an antialiased contour");
                bool clearBorder=true;
                for(int x=0;x<texture.width;x++)clearBorder&=pixels[x].a==0&&pixels[(texture.height-1)*texture.width+x].a==0;
                for(int y=0;y<texture.height;y++)clearBorder&=pixels[y*texture.width].a==0&&pixels[(y+1)*texture.width-1].a==0;
                PlaytestFaceRequire(clearBorder,texture.name+" keeps transparent bounds without clipping");
                File.WriteAllBytes(Path.Combine(dir,texture.name+".png"),texture.EncodeToPNG());
            }
            Color32[] action=ArchitectPhotoIcons.TakePhoto.GetPixels32(),spot=ArchitectPhotoIcons.PhotoSpot.GetPixels32();
            PlaytestFaceRequire(action.Where((p,i)=>!p.Equals(spot[i])).Count()>action.Length/5,"camera action and tripod place icons are visibly different");
        }

        // Run against a disposable spawned human with FA, Experimentals and Retexture.
        // The old hook fails the visible-vs-blink pixel equality: it replaces lids but
        // leaves the hidden eye graphic behind. Removing alternate support fails surprise.
        internal static void PlaytestFaceChecks(Pawn pawn)
        {
            PlaytestFaceRequire(PhotoFaces.Available,"FA adapter loaded for blink and declared-alternate regression");
            var comps=pawn.AllComps.Where(c=>c.GetType().Namespace=="FacialAnimation").ToList();
            var mouth=comps.First(c=>c.GetType().Name=="MouthControllerComp");
            var eye=comps.First(c=>c.GetType().Name=="EyeballControllerComp");
            var lid=comps.First(c=>c.GetType().Name=="LidControllerComp");
            var animator=comps.First(c=>c.GetType().Name=="FacialAnimationControllerComp");
            var mouthType=AccessTools.Property(mouth.GetType(),"FaceType");
            object previousMouth=mouthType.GetValue(mouth,null);
            var shapes=comps.Select(c=>Tuple.Create(c,AccessTools.Field(c.GetType(),"Shape"))).Where(t=>t.Item2!=null).ToList();
            var previousShapes=shapes.Select(t=>t.Item2.GetValue(t.Item1)).ToArray();
            var visibility=(IDictionary)AccessTools.Field(eye.GetType(),"visibleDict").GetValue(eye);
            var previousVisibility=visibility.Keys.Cast<object>().Select(k=>new DictionaryEntry(k,visibility[k])).ToArray();
            Rot4 previousRotation=pawn.Rotation;
            var target=AccessTools.Method(animator.GetType(),"UpdateAnimation");
            var patch=AccessTools.Method(typeof(Probe),nameof(PlaytestFaceFreeze));
            var harmony=new Harmony("MomentFrames.Probe.PlaytestFaces");
            try
            {
                object smiling=PlaytestFaceDef("MouthTypeDef","MouthSmiling");
                PlaytestFaceRequire(smiling!=null,"actual user Retexture MouthSmiling fixture is loaded");
                mouthType.SetValue(mouth,smiling,null);pawn.Rotation=Rot4.South;
                AccessTools.Method(mouth.GetType(),"ReloadIfNeed").Invoke(mouth,null);
                string path=(string)AccessTools.Field(smiling.GetType(),"texPath").GetValue(smiling)+"/"+pawn.gender+"/";
                PlaytestFaceRequire(ContentFinder<Texture2D>.Get(path+"surprise_south",false)==null && ContentFinder<Texture2D>.Get(path+"open_south",false)!=null,
                    "Retexture fixture has open texture but no explicit surprise texture");
                PlaytestFaceRequire(PhotoFaces.Supports(pawn,3),"FA-declared surprise to open alternate is offered");
                PlaytestFaceRequire(!PhotoFaces.Supports(pawn,4),"sad resolving only to normal is not falsely offered");
                PlaytestFaceRequire(PhotoFaces.Supports(pawn,1)&&PhotoFaces.Supports(pawn,2),"smile and closed-eye fixtures supported");
                PlaytestFaceRequire(!PhotoFaces.HasTemporary(pawn),"blink fixture does not begin with another temporary expression");
                playtestFaceFixturePawn=pawn;harmony.Patch(target,prefix:new HarmonyMethod(patch));
                var lidShape=AccessTools.Field(lid.GetType(),"Shape");
                var eyeShape=AccessTools.Field(eye.GetType(),"Shape");
                eyeShape.SetValue(eye,PlaytestFaceDef("EyeballShapeDef","normal"));
                byte[][] smilePixels=new byte[2][];byte[][] closedPixels=new byte[2][];
                for(int blink=0;blink<2;blink++)
                {
                    lidShape.SetValue(lid,PlaytestFaceDef("LidShapeDef",blink==1?"close":"normal"));
                    foreach(var entry in previousVisibility)visibility[entry.Key]=blink==0;
                    var beforeShapes=shapes.Select(t=>t.Item2.GetValue(t.Item1)).ToArray();
                    var beforeVisibility=visibility.Keys.Cast<object>().Select(k=>new DictionaryEntry(k,visibility[k])).ToArray();
                    using(var preview=new PhotoFacePreview())
                    {
                        smilePixels[blink]=PlaytestFacePixels((RenderTexture)preview.Get(pawn,1),"face-feedback-smile-blink"+blink+".png");
                        closedPixels[blink]=PlaytestFacePixels((RenderTexture)preview.Get(pawn,2),"face-feedback-closed-blink"+blink+".png");
                    }
                    PlaytestFaceRequire(beforeShapes.SequenceEqual(shapes.Select(t=>t.Item2.GetValue(t.Item1))),"preview preserves live shapes in blink fixture "+blink);
                    PlaytestFaceRequire(beforeVisibility.All(e=>Equals(e.Value,visibility[e.Key])),"preview preserves live eye visibility in blink fixture "+blink);
                }
                PlaytestFaceRequire(smilePixels[0].SequenceEqual(smilePixels[1]),"smile portrait retains identical pupils during live blink");
                PlaytestFaceRequire(closedPixels[0].SequenceEqual(closedPixels[1]),"closed-eye portrait is independent of live blink");
                PlaytestFaceRequire(!smilePixels[0].SequenceEqual(closedPixels[0]),"eye-close selection actually changes rendered portrait pixels");
                PlaytestFaceRequire(PhotoFacePreview.RenderingPawn==null && PhotoFacePreview.RenderingFrame==null,"portrait override scope released");
                PlaytestFaceRequire(!PhotoFaces.HasTemporary(pawn),"portrait checks create no temporary animations");
            }
            finally
            {
                harmony.Unpatch(target,patch);playtestFaceFixturePawn=null;
                mouthType.SetValue(mouth,previousMouth,null);
                AccessTools.Method(mouth.GetType(),"ReloadIfNeed").Invoke(mouth,null);
                for(int i=0;i<shapes.Count;i++)shapes[i].Item2.SetValue(shapes[i].Item1,previousShapes[i]);
                foreach(var entry in previousVisibility)visibility[entry.Key]=entry.Value;
                pawn.Rotation=previousRotation;pawn.Drawer.renderer.SetAllGraphicsDirty();
            }
        }
    }
}
