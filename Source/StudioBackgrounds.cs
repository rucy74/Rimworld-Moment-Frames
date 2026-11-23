using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class StudioBackgrounds
    {
        // Interior of the upright cloth. The floor, rolled top and support frame remain visible.
        internal static readonly Rect Panel=new Rect(.185f,.41f,.63f,.42f);
        internal const float Aspect=8f*.63f/(5f*.42f);
        internal static string Folder=>Path.Combine(PhotoStore.Instance.FolderPath,"studio-backgrounds");
        internal static string PathFor(string file)=>!file.NullOrEmpty() && file==Path.GetFileName(file) && file.EndsWith(".png",StringComparison.OrdinalIgnoreCase) ? Path.Combine(Folder,file) : null;
        internal static Rect Crop(int w,int h,float zoom,float x,float y)
        {
            zoom=Mathf.Clamp(zoom,1,4);float aspect=w/(float)h;
            float cw=Mathf.Min(1,Aspect/aspect)/zoom,ch=Mathf.Min(1,aspect/Aspect)/zoom;
            return new Rect(Mathf.Clamp01(x)*(1-cw),Mathf.Clamp01(y)*(1-ch),cw,ch);
        }
        internal static bool Dimensions(byte[] bytes,out int width,out int height)
        {
            width=height=0;
            if(bytes.Length>=24 && bytes[0]==137 && bytes[1]==80 && bytes[2]==78 && bytes[3]==71)
            {width=Big(bytes,16);height=Big(bytes,20);}
            else if(bytes.Length>4 && bytes[0]==255 && bytes[1]==216)
            {
                int p=2;
                while(p+4<=bytes.Length)
                {
                    if(bytes[p++]!=255)return false;
                    while(p<bytes.Length&&bytes[p]==255)p++;
                    if(p>=bytes.Length)break;int marker=bytes[p++];
                    if(marker==0xd9||marker==0xda)break;
                    if(marker==0x01 || marker>=0xd0&&marker<=0xd7)continue;
                    if(p+2>bytes.Length)return false;int length=(bytes[p]<<8)|bytes[p+1];
                    if(length<2||p+length>bytes.Length)return false;
                    if((marker>=0xc0&&marker<=0xc3 || marker>=0xc5&&marker<=0xc7 || marker>=0xc9&&marker<=0xcb || marker>=0xcd&&marker<=0xcf)&&length>=8)
                    {height=(bytes[p+3]<<8)|bytes[p+4];width=(bytes[p+5]<<8)|bytes[p+6];break;}
                    p+=length;
                }
            }
            return width>0&&height>0&&width<=16384&&height<=16384&&(long)width*height<=40000000;
        }
        private static int Big(byte[] b,int p)=>(b[p]<<24)|(b[p+1]<<16)|(b[p+2]<<8)|b[p+3];
        internal static Texture2D Load(string path)
        {
            if(path.NullOrEmpty()||!File.Exists(path))throw new IOException("MF_BackgroundMissing".Translate());
            if(new FileInfo(path).Length>32*1024*1024)throw new IOException("MF_BackgroundTooLarge".Translate());
            byte[] bytes=File.ReadAllBytes(path);
            if(!Dimensions(bytes,out int w,out int h))throw new IOException("MF_BackgroundFormat".Translate());
            Texture2D full=null,small=null;RenderTexture rt=null;var previous=RenderTexture.active;
            try
            {
                full=new Texture2D(2,2,TextureFormat.RGBA32,false);
                if(!full.LoadImage(bytes,false))throw new IOException("MF_BackgroundFormat".Translate());
                if(Mathf.Max(w,h)<=2048){var result=full;full=null;return result;}
                float scale=2048f/Mathf.Max(w,h);w=Mathf.Max(1,Mathf.RoundToInt(w*scale));h=Mathf.Max(1,Mathf.RoundToInt(h*scale));
                rt=RenderTexture.GetTemporary(w,h,0,RenderTextureFormat.ARGB32);Graphics.Blit(full,rt);RenderTexture.active=rt;
                small=new Texture2D(w,h,TextureFormat.RGBA32,false);small.ReadPixels(new Rect(0,0,w,h),0,0);small.Apply(false,false);
                var resized=small;small=null;return resized;
            }
            finally {RenderTexture.active=previous;if(rt!=null)RenderTexture.ReleaseTemporary(rt);if(full!=null)UnityEngine.Object.Destroy(full);if(small!=null)UnityEngine.Object.Destroy(small);}
        }
        internal static string Import(Texture2D image)
        {
            byte[] bytes=image.EncodeToPNG();string name;
            using(var sha=SHA256.Create())name=string.Concat(sha.ComputeHash(bytes).Select(b=>b.ToString("x2")))+".png";
            Directory.CreateDirectory(Folder);string path=PathFor(name);
            if(!File.Exists(path))
            {
                string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try {File.WriteAllBytes(temp,bytes);if(!File.Exists(path))File.Move(temp,path);}
                finally {if(File.Exists(temp))File.Delete(temp);}
            }
            return name;
        }
    }

    public partial class Building_PhotoStudio
    {
        private string backgroundFile;
        private float backgroundZoom=1,backgroundX=.5f,backgroundY=.5f;
        private Texture2D backgroundTexture;private Material backgroundMaterial;
        private bool backgroundAttempted;
        internal string BackgroundFile=>backgroundFile;
        internal float BackgroundZoom=>backgroundZoom;
        internal float BackgroundX=>backgroundX;
        internal float BackgroundY=>backgroundY;
        internal void SetPhotoBackground(string file,float zoom,float x,float y)
        {
            if(!HasCanvas)return;
            ClearBackgroundCache();backgroundFile=file;backgroundZoom=Mathf.Clamp(zoom,1,4);backgroundX=Mathf.Clamp01(x);backgroundY=Mathf.Clamp01(y);
        }
        private void ExposeBackground()
        {
            Scribe_Values.Look(ref backgroundFile,"studioBackgroundFile");Scribe_Values.Look(ref backgroundZoom,"studioBackgroundZoom",1);
            Scribe_Values.Look(ref backgroundX,"studioBackgroundX",.5f);Scribe_Values.Look(ref backgroundY,"studioBackgroundY",.5f);
            if(Scribe.mode==LoadSaveMode.PostLoadInit){ClearBackgroundCache();backgroundZoom=Mathf.Clamp(backgroundZoom,1,4);backgroundX=Mathf.Clamp01(backgroundX);backgroundY=Mathf.Clamp01(backgroundY);}
        }
        private void ClearBackgroundCache()
        {
            if(backgroundMaterial!=null)UnityEngine.Object.Destroy(backgroundMaterial);
            if(backgroundTexture!=null)UnityEngine.Object.Destroy(backgroundTexture);
            backgroundMaterial=null;backgroundTexture=null;backgroundAttempted=false;
        }
        private Material BackgroundMaterial()
        {
            if(backgroundAttempted)return backgroundMaterial;backgroundAttempted=true;
            try
            {
                backgroundTexture=StudioBackgrounds.Load(StudioBackgrounds.PathFor(backgroundFile));
                backgroundTexture.wrapMode=TextureWrapMode.Clamp;
                backgroundMaterial=new Material(ShaderDatabase.Cutout){mainTexture=backgroundTexture};
                Rect crop=StudioBackgrounds.Crop(backgroundTexture.width,backgroundTexture.height,backgroundZoom,backgroundX,backgroundY);
                backgroundMaterial.mainTextureScale=crop.size;backgroundMaterial.mainTextureOffset=crop.position;
            }
            catch(Exception e){Log.Warning("[MomentFrames] Studio background unavailable; using the cloth: "+e.Message);}
            return backgroundMaterial;
        }
        protected override void DrawAt(Vector3 drawLoc,bool flip=false)
        {
            base.DrawAt(drawLoc,flip);
            if(!HasCanvas||backgroundFile.NullOrEmpty())return;
            var mat=BackgroundMaterial();if(mat==null)return;
            var panel=StudioBackgrounds.Panel;var size=def.graphicData.drawSize;
            var rotation=Quaternion.AngleAxis(Rotation.AsAngle,Vector3.up);
            var center=drawLoc+rotation*new Vector3((panel.center.x-.5f)*size.x,0,(panel.center.y-.5f)*size.y);
            center.y+=.001f;
            Graphics.DrawMesh(MeshPool.plane10,Matrix4x4.TRS(center,rotation,new Vector3(panel.width*size.x,1,panel.height*size.y)),mat,0);
        }
        public override void DeSpawn(DestroyMode mode=DestroyMode.Vanish){ClearBackgroundCache();base.DeSpawn(mode);}
    }
}
