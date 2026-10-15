using System;
using UnityEngine;

namespace Apocapatrol
{
    // One texture draw per map. Reuse pixel storage; route segments never rotate the GUI matrix or draw
    // thousands of individual textures. Sparse route arrays retain bends in both map projections.
    internal sealed class TrafficMapInk
    {
        private Texture2D _texture;
        private Color32[] _pixels;
        private int _width,_height;
        internal Texture2D Texture => _texture;
        internal Color32[] Pixels => _pixels;
        internal void Begin(int width,int height)
        {
            width=Mathf.Clamp(width,32,512);height=Mathf.Clamp(height,32,512);
            if(_texture==null||width!=_width||height!=_height)
            {
                if(_texture!=null)UnityEngine.Object.Destroy(_texture);
                _width=width;_height=height;_pixels=new Color32[width*height];
                _texture=new Texture2D(width,height,TextureFormat.RGBA32,false){filterMode=FilterMode.Bilinear};
            }
            else Array.Clear(_pixels,0,_pixels.Length);
        }
        internal void Segment(Vector2 a,Vector2 b,Color32 color)
        {
            if(!Clip(ref a,ref b,new Rect(0,0,_width-1,_height-1)))return;
            int x=Mathf.RoundToInt(a.x),y=Mathf.RoundToInt(a.y),endX=Mathf.RoundToInt(b.x),endY=Mathf.RoundToInt(b.y);
            int dx=Math.Abs(endX-x),dy=-Math.Abs(endY-y),sx=x<endX?1:-1,sy=y<endY?1:-1,error=dx+dy;
            while(true)
            {
                Pixel(x,y,color);Pixel(x+1,y,color);Pixel(x,y+1,color);
                if(x==endX&&y==endY)break;
                int twice=error*2;if(twice>=dy){error+=dy;x+=sx;}if(twice<=dx){error+=dx;y+=sy;}
            }
        }
        private void Pixel(int x,int y,Color32 color)
        {if(x>=0&&y>=0&&x<_width&&y<_height)_pixels[(_height-1-y)*_width+x]=color;}
        internal void End(){_texture.SetPixels32(_pixels);_texture.Apply(false,false);}
        internal void Dispose(){if(_texture!=null)UnityEngine.Object.Destroy(_texture);_texture=null;_pixels=null;}
        internal static bool Clip(ref Vector2 a,ref Vector2 b,Rect rect)
        {
            float lo=0f,hi=1f;var d=b-a;
            if(!Bound(a.x,d.x,rect.xMin,rect.xMax,ref lo,ref hi)||!Bound(a.y,d.y,rect.yMin,rect.yMax,ref lo,ref hi))return false;
            var start=a;a=start+d*lo;b=start+d*hi;return true;
        }
        private static bool Bound(float a,float d,float min,float max,ref float lo,ref float hi)
        {if(Mathf.Abs(d)<0.00001f)return a>=min&&a<=max;float x=(min-a)/d,y=(max-a)/d;lo=Mathf.Max(lo,Mathf.Min(x,y));hi=Mathf.Min(hi,Mathf.Max(x,y));return lo<=hi;}
    }
}
