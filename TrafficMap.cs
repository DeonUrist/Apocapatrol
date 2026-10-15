using UnityEngine;
namespace Apocapatrol
{
    internal sealed partial class WorldTraffic
    {
        private Camera _gameMap;
        private RectTransform _gameMapImage;
        private float _nextMapScan,_gameInkAt;
        private readonly TrafficMapInk _gameInk=new TrafficMapInk();
        private readonly Vector3[] _mapCorners=new Vector3[4];
        private Texture2D _skull;
        private Vector3 OffscreenTrafficPoint(Vector3 center,Vector3 direction)
        {
            // Route creation only; include the inactive native camera when M is closed.
            if(_gameMap==null)foreach(var camera in Resources.FindObjectsOfTypeAll<Camera>())
                if(camera.name=="MapCheat_Camera"&&camera.gameObject.scene.IsValid()){_gameMap=camera;break;}
            direction.y=0f;direction.Normalize();
            float reach=Mathf.Max(Plugin.TrafficUnloadRange.Value,Plugin.TrafficLoadRange.Value)+250f;
            if(_gameMap==null)return center+direction*(reach+100f);
            var plane=new Plane(Vector3.up,TrafficSpace.Scene(center));
            var corners=new Vector2[4];
            for(int corner=0;corner<4;corner++)
            {
                var ray=_gameMap.ViewportPointToRay(new Vector3(corner==1||corner==2?1f:0f,corner>=2?1f:0f,0f));
                if(!plane.Raycast(ray,out float distance))return center+direction*(reach+100f);
                var world=TrafficSpace.World(ray.GetPoint(distance));corners[corner]=new Vector2(world.x,world.z);
            }
            reach=MapExitDistance(new Vector2(center.x,center.z),new Vector2(direction.x,direction.z),corners);
            return center+direction*(reach+100f);
        }
        internal static float MapExitDistance(Vector2 origin,Vector2 direction,Vector2[] corners)
        {
            float exit=0f;
            for(int i=0;i<corners.Length;i++)
            {
                var a=corners[i]-origin;var edge=corners[(i+1)%corners.Length]-corners[i];
                float cross=direction.x*edge.y-direction.y*edge.x;if(Mathf.Abs(cross)<.0001f)continue;
                float t=(a.x*edge.y-a.y*edge.x)/cross,u=(a.x*direction.y-a.y*direction.x)/cross;
                if(t>=0f&&u>=0f&&u<=1f)exit=Mathf.Max(exit,t);
            }
            return exit;
        }
        private void OnGUI()
        {
            if(!Plugin.WorldTrafficEnabled.Value||!Plugin.TrafficDebugMap.Value||Event.current.type!=EventType.Repaint)return;
            if((_gameMap==null||_gameMapImage==null)&&Time.unscaledTime>=_nextMapScan)
            {
                _nextMapScan=Time.unscaledTime+5f;
                var camera=GameObject.Find("MapCheat_Camera");_gameMap=camera!=null?camera.GetComponent<Camera>():null;
                var image=GameObject.Find("RawImage_Map");_gameMapImage=image!=null?image.GetComponent<RectTransform>():null;
            }
            if(_gameMap==null||_gameMapImage==null||!_gameMapImage.gameObject.activeInHierarchy)return;
            _gameMapImage.GetWorldCorners(_mapCorners);
            var rect=new Rect(_mapCorners[0].x,Screen.height-_mapCorners[1].y,_mapCorners[2].x-_mapCorners[0].x,_mapCorners[1].y-_mapCorners[0].y);
            if(rect.width<1f||rect.height<1f)return;
            var old=GUI.color;GUI.BeginGroup(rect);
            if(Time.unscaledTime>=_gameInkAt)
            {
                _gameInkAt=Time.unscaledTime+0.4f;
                var canvas=new Rect(0,0,Mathf.Min(512f,rect.width),Mathf.Min(512f,rect.height));
                _gameInk.Begin(Mathf.RoundToInt(canvas.width),Mathf.RoundToInt(canvas.height));
                foreach(var journey in _journeys)for(int i=1;i<journey.route.Length;i++)
                    _gameInk.Segment(MapPoint(journey.route[i-1],canvas),MapPoint(journey.route[i],canvas),RouteColor(journey));
                _gameInk.End();
            }
            GUI.color=Color.white;GUI.DrawTexture(new Rect(0,0,rect.width,rect.height),_gameInk.Texture);
            foreach(var journey in _journeys)
            {var p=MapPoint(journey.position,rect);GUI.color=RouteColor(journey);GUI.Label(new Rect(p.x,p.y,180f,22f),MapName(journey));}
            foreach(var camp in TrafficLocations.Known.Values)if(camp.cleared)
            {var p=MapPoint(camp.center,rect);GUI.color=Color.gray;GUI.Label(new Rect(p.x,p.y,180f,22f),camp.name+" [Cleared]");}
            foreach(var battle in _battles)
            {var p=MapPoint(battle.position,rect);GUI.color=Color.white;GUI.DrawTexture(new Rect(p.x-9f,p.y-9f,18f,18f),Skull());}
            foreach(var merchant in MerchantHost.All)if(merchant!=null&&!Owns(merchant.GetComponent<PatrolMarker>()))
            {var p=MapPoint(TrafficSpace.World(merchant.transform.position),rect);GUI.color=Plugin.HideRouteType.Value?Color.gray:Color.yellow;GUI.Label(new Rect(p.x,p.y,180f,22f),Plugin.HideRouteType.Value?"Distant traffic":Plugin.RealPatrolNames.Value?merchant.Data?.group??"Coyote merchant":"Merchant");}
            GUI.EndGroup();GUI.color=old;
        }
        private Vector2 MapPoint(Vector3 world,Rect rect)
        {var p=_gameMap.WorldToViewportPoint(TrafficSpace.Scene(world));return new Vector2(p.x*rect.width,(1f-p.y)*rect.height);}
        internal static string MapName(TrafficJourney journey)
        {return Plugin.HideRouteType.Value?"Distant traffic":Plugin.RealPatrolNames.Value&&!string.IsNullOrEmpty(journey.name)?journey.name:journey.merchant?"Merchant":journey.convoy?"Convoy":"Patrol";}
        internal static Color32 RouteColor(TrafficJourney journey)
        {
            if(Plugin.HideRouteType.Value)return new Color32(150,150,150,255);
            if(journey.merchant)return new Color32(255,220,50,255);
            if(TrafficFactions.Normalize(journey.faction)=="Coyotes")return journey.convoy?new Color32(110,210,255,255):new Color32(80,220,100,255);
            return journey.convoy?new Color32(255,150,40,255):new Color32(255,80,80,255);
        }
        private Texture2D Skull()
        {
            if(_skull!=null)return _skull;
            string[] rows={"00011111111000","00111111111100","01111111111110","11111111111111","11100111100111","11100111100111","11111100111111","01111100111110","00111111111100","00011011011000","00011011011000"};
            _skull=new Texture2D(14,11,TextureFormat.RGBA32,false);var pixels=new Color32[154];for(int y=0;y<11;y++)for(int x=0;x<14;x++)pixels[(10-y)*14+x]=rows[y][x]=='1'?new Color32(230,230,230,255):new Color32(0,0,0,0);
            _skull.SetPixels32(pixels);_skull.filterMode=FilterMode.Point;_skull.Apply();return _skull;
        }
        private void OnDestroy(){_gameInk.Dispose();if(_skull!=null)Destroy(_skull);}
    }
}
