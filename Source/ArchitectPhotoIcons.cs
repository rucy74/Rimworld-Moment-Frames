using UnityEngine;

namespace MomentFrames
{
    /// <summary>One-time CPU-drawn architect icons; no texture readback or font dependency.</summary>
    [Verse.StaticConstructorOnStartup]
    internal static class ArchitectPhotoIcons
    {
        private static Texture2D takePhoto,photoSpot;
        internal static Texture2D TakePhoto=>takePhoto!=null?takePhoto:takePhoto=Create(false);
        internal static Texture2D PhotoSpot=>photoSpot!=null?photoSpot:photoSpot=Create(true);
        private static Texture2D visitLine;
        /// <summary>The world map's first-visit order: places joined by a dashed line, the newest one warm.</summary>
        internal static Texture2D VisitLine=>visitLine!=null?visitLine:visitLine=CreateVisitLine();
        private static readonly Color ink=new Color(.10f,.14f,.16f),body=new Color(.89f,.87f,.79f),
            metal=new Color(.39f,.44f,.43f),lens=new Color(.24f,.57f,.61f),accent=new Color(.90f,.66f,.31f);

        private static Texture2D Create(bool spot)
        {
            var p=new Paint();
            if(spot)
            {
                // The grounded tripod reads as a place to assemble, distinct from the shutter action.
                p.Ellipse(32,56,23,5,2f,new Color(.77f,.65f,.40f,.8f));
                p.Line(32,35,32,56,5f,ink);
                p.Line(31,38,19,55,5f,ink);p.Line(33,38,45,55,5f,ink);
                p.Line(32,35,32,54,2f,metal);
                p.Line(31,39,20,54,2f,metal);p.Line(33,39,44,54,2f,metal);
                Camera(p,13,14,38,24);
            }
            else
            {
                Camera(p,7,24,49,30);
                // Warm shutter sparkle remains legible at the small architect-button scale.
                p.Line(52,7,52,18,3.2f,ink);p.Line(47,12.5f,57,12.5f,3.2f,ink);
                p.Line(52,7.5f,52,17.5f,1.6f,accent);p.Line(47.5f,12.5f,56.5f,12.5f,1.6f,accent);
            }
            var texture=new Texture2D(Paint.Size,Paint.Size,TextureFormat.RGBA32,false)
            {
                name=spot?"MF_ArchitectPhotoSpot":"MF_ArchitectTakePhoto",filterMode=FilterMode.Bilinear,
                wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave
            };
            texture.SetPixels32(p.Pixels());texture.Apply(false,false);return texture;
        }
        private static Texture2D CreateVisitLine()
        {
            var p=new Paint();
            var points=new[]{new Vector2(11f,51f),new Vector2(30f,31f),new Vector2(52f,14f)};
            for(int i=0;i+1<points.Length;i++)
            {
                Vector2 a=points[i],b=points[i+1];
                int pieces=Mathf.Max(3,Mathf.RoundToInt(Vector2.Distance(a,b)/5f));
                for(int d=0;d<pieces;d+=2)
                {
                    Vector2 s=Vector2.Lerp(a,b,d/(float)pieces),e=Vector2.Lerp(a,b,Mathf.Min(1f,(d+1)/(float)pieces));
                    p.Line(s.x,s.y,e.x,e.y,5f,ink);p.Line(s.x,s.y,e.x,e.y,2.4f,body);
                }
            }
            for(int i=0;i<points.Length-1;i++){p.Disk(points[i].x,points[i].y,7.5f,ink);p.Disk(points[i].x,points[i].y,5.2f,body);}
            Vector2 last=points[points.Length-1];
            p.Disk(last.x,last.y,9f,ink);p.Disk(last.x,last.y,6.6f,accent);p.Disk(last.x-1.8f,last.y-1.8f,2f,new Color(1f,.9f,.7f));
            var texture=new Texture2D(Paint.Size,Paint.Size,TextureFormat.RGBA32,false)
            {
                name="MF_WorldVisitLine",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave
            };
            texture.SetPixels32(p.Pixels());texture.Apply(false,false);return texture;
        }
        private static void Camera(Paint p,float x,float y,float width,float height)
        {
            float scale=width/49f,cx=x+width*.5f,cy=y+height*.52f;
            p.Rect(x+1.6f,y+2f,width,height,4f,new Color(0,0,0,.22f));
            p.Rect(x+width*.23f,y-7f*scale,width*.39f,11f*scale,2.5f,ink);
            p.Rect(x+width*.23f+2f,y-5f*scale,width*.39f-4f,8f*scale,1.2f,metal);
            p.Rect(x,y,width,height,3.5f,ink);
            p.Rect(x+2f,y+2f,width-4f,height-4f,2f,body);
            p.Rect(x+width-10f*scale,y+2f,8f*scale,height-4f,1.8f,metal);
            p.Rect(x+5f*scale,y-2.5f*scale,7f*scale,3.2f*scale,1f,ink);
            p.Disk(cx,cy,12f*scale,ink);p.Disk(cx,cy,9.6f*scale,metal);
            p.Disk(cx,cy,8f*scale,ink);p.Disk(cx,cy,6.6f*scale,lens);
            p.Disk(cx+1.4f*scale,cy+1.5f*scale,3.6f*scale,new Color(.10f,.25f,.30f));
            p.Disk(cx-2.5f*scale,cy-2.9f*scale,1.8f*scale,new Color(.86f,.96f,.94f));
            p.Disk(x+6f*scale,y+7f*scale,1.6f*scale,accent);
        }

        private sealed class Paint
        {
            internal const int Size=128;
            private readonly Color[] pixels=new Color[Size*Size];
            private const float Scale=Size/64f,Edge=.4f;
            private void Shape(float left,float top,float right,float bottom,System.Func<float,float,float> distance,Color color)
            {
                int x0=Mathf.Clamp(Mathf.FloorToInt((left-1f)*Scale),0,Size-1),x1=Mathf.Clamp(Mathf.CeilToInt((right+1f)*Scale),0,Size-1);
                int y0=Mathf.Clamp(Mathf.FloorToInt((top-1f)*Scale),0,Size-1),y1=Mathf.Clamp(Mathf.CeilToInt((bottom+1f)*Scale),0,Size-1);
                for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
                {
                    float a=Mathf.Clamp01(.5f-distance((x+.5f)/Scale,(y+.5f)/Scale)/(2f*Edge))*color.a;
                    if(a<=0f)continue;
                    int at=(Size-1-y)*Size+x;Color previous=pixels[at];float alpha=a+previous.a*(1f-a);
                    pixels[at]=new Color((color.r*a+previous.r*previous.a*(1f-a))/alpha,
                        (color.g*a+previous.g*previous.a*(1f-a))/alpha,(color.b*a+previous.b*previous.a*(1f-a))/alpha,alpha);
                }
            }
            internal void Rect(float x,float y,float w,float h,float radius,Color color)
            {
                radius=Mathf.Min(radius,Mathf.Min(w,h)/2f);
                Shape(x,y,x+w,y+h,(px,py)=>
                {
                    float dx=Mathf.Abs(px-x-w/2f)-(w/2f-radius),dy=Mathf.Abs(py-y-h/2f)-(h/2f-radius);
                    return Mathf.Sqrt(Mathf.Max(dx,0)*Mathf.Max(dx,0)+Mathf.Max(dy,0)*Mathf.Max(dy,0))+Mathf.Min(Mathf.Max(dx,dy),0)-radius;
                },color);
            }
            internal void Disk(float x,float y,float radius,Color color)
                =>Shape(x-radius,y-radius,x+radius,y+radius,(px,py)=>Mathf.Sqrt((px-x)*(px-x)+(py-y)*(py-y))-radius,color);
            internal void Line(float x0,float y0,float x1,float y1,float width,Color color)
            {
                float dx=x1-x0,dy=y1-y0,length=dx*dx+dy*dy;
                Shape(Mathf.Min(x0,x1)-width,Mathf.Min(y0,y1)-width,Mathf.Max(x0,x1)+width,Mathf.Max(y0,y1)+width,(px,py)=>
                {
                    float t=length>.00001f?Mathf.Clamp01(((px-x0)*dx+(py-y0)*dy)/length):0f;
                    float xx=px-x0-t*dx,yy=py-y0-t*dy;return Mathf.Sqrt(xx*xx+yy*yy)-width*.5f;
                },color);
            }
            internal void Ellipse(float x,float y,float rx,float ry,float width,Color color)
            {
                for(int i=0;i<64;i++)
                {
                    float a=i*Mathf.PI/32f,b=(i+1)*Mathf.PI/32f;
                    Line(x+Mathf.Cos(a)*rx,y+Mathf.Sin(a)*ry,x+Mathf.Cos(b)*rx,y+Mathf.Sin(b)*ry,width,color);
                }
            }
            internal Color32[] Pixels()
            {var result=new Color32[pixels.Length];for(int i=0;i<pixels.Length;i++)result[i]=pixels[i];return result;}
        }
    }
}
