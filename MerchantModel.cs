using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
namespace Apocapatrol
{
    internal static class MerchantCatalog
    {
        internal static readonly string[] Types={"general","mechanic","organic"};
        internal static bool Known(string id)=>Types.Contains(id);
        internal static string Selected(IEnumerable<string> types)=>types?.FirstOrDefault(Known)??"general";
        internal static string[] Selection(IEnumerable<string> types)=>new[]{Selected(types)};
        internal static string Label(string id)=>id=="mechanic"?"Mechanic":id=="organic"?"Organic mechanic":"General merchant";
        internal static string Station(string id)=>id=="mechanic"?"Sell_Items_mechanic":id=="organic"?"Sell_Items_organic_mechanic":"Sell_Items";
        internal static SpawnGroup DefaultGroup()=>new SpawnGroup{Id="coyote-merchant",Name="Coyote merchant",Faction="Coyotes",Chance=10,Templates=new List<string>{"Coyote_Trader"},MerchantTypes=new List<string>{"general"}};
        internal static List<SpawnGroup> DefaultGroups()=>Types.Select(type=>{var group=DefaultGroup();group.MerchantTypes=new List<string>{type};if(type!="general"){group.Id+="-"+type;group.Name="Coyote "+Label(type).ToLowerInvariant();}return group;}).ToList();
        internal static GameObject[] Goods(string type)
        {
            var station=Prefabs.FindAny(Station(type));
            var proxy=station!=null?station.GetComponent<PlayMakerArrayListProxy>():null;
            if(proxy==null){var list=GameObject.Find("ItemBuySellList");if(list!=null)proxy=list.GetComponent<PlayMakerArrayListProxy>();}
            var goods=proxy!=null?proxy.preFillGameObjectList.Where(g=>g!=null).ToArray():new GameObject[0];
            if(goods.Length==0&&proxy?.arrayList!=null)goods=proxy.arrayList.OfType<GameObject>().Where(g=>g!=null).ToArray();
            if(goods.Length==0)return new[]{Prefabs.FindAny("Repairbox_Small"),Prefabs.FindAny("Toolbox"),Prefabs.FindAny("first_aid_1")}.Where(g=>g!=null).ToArray();
            return goods.Where(g=>g.GetComponents<PlayMakerFSM>().Any(f=>f.FsmName=="ForBuy")).ToArray();
        }
    }
    [Serializable] public sealed class MerchantPose
    {public string id;public Vector3 position,rotation;}
    [Serializable] public sealed class MerchantLayout
    {public string template;public MerchantPose[] objects=new MerchantPose[0];}
    [Serializable] public sealed class MerchantLayoutFile
    {public int version=2;public MerchantLayout[] layouts=new MerchantLayout[0];}
    [Serializable] public sealed class MerchantDemand
    {public string type;public string[] names=new string[0];public int[] prices=new int[0];}
    [Serializable] public sealed class MerchantStock
    {public string type,prefab,nativeName="";public int slot,buyPrice=-1;public bool purchased;}
    [Serializable] public sealed class MerchantTurret
    {public int slot;public string prefab;public float health,maxHealth;public Vector3 position;public bool standing;}
    [Serializable] public sealed class MerchantSave
    {public int version=1;public string template,group;public string[] types=new string[0];public MerchantStock[] stock=new MerchantStock[0];public MerchantDemand[] demands=new MerchantDemand[0];public MerchantTurret[] turrets;public bool travelling;}
    public sealed class MerchantObject
    {public string Id,Label,Kind;public Transform Transform;}
    public static class MerchantApi
    {
        public static GameObject[] Cars()=>MerchantHost.All.Where(h=>h!=null).Select(h=>h.gameObject).ToArray();
        public static MerchantObject[] Objects(GameObject car)=>car!=null?car.GetComponent<MerchantHost>()?.Objects.ToArray()??new MerchantObject[0]:new MerchantObject[0];
        public static string Save(GameObject car)=>MerchantLayouts.Save(car);
        public static void Freeze(GameObject car){var host=car!=null?car.GetComponent<MerchantHost>():null;if(host==null)return;var journey=WorldTraffic.Find(car.GetComponent<PatrolMarker>()?.TrafficId);if(journey?.merchant==true)journey.waitSeconds=300f;else host.Park();}
    }
    internal static class MerchantLayouts
    {
        private static MerchantLayoutFile _data;
        private static string PathName=>Path.Combine(EditorStore.Root,"MerchantLayouts.json");
        private static MerchantLayoutFile Defaults()
        {using(var stream=typeof(Plugin).Assembly.GetManifestResourceStream("Apocapatrol.MerchantLayouts.defaults.json"))using(var reader=new StreamReader(stream))return TrafficCodec.ReadRecord<MerchantLayoutFile>(reader.ReadToEnd());}
        private static MerchantLayoutFile Data
        {
            get
            {
                if(_data!=null)return _data;
                bool save=false;
                try{_data=File.Exists(PathName)?TrafficCodec.ReadRecord<MerchantLayoutFile>(File.ReadAllText(PathName)):Defaults();save=!File.Exists(PathName);}
                catch(Exception e){Plugin.Log.LogWarning("Merchant layout: "+e.Message);_data=Defaults();}
                if(_data?.layouts==null)_data=Defaults();
                if(_data.version<2)
                {
                    foreach(var layout in _data.layouts.Where(l=>l!=null))layout.objects=(layout.objects??new MerchantPose[0]).Where(p=>p!=null&&!string.IsNullOrEmpty(p.id)).OrderBy(p=>p.id.StartsWith("general/")?0:1).GroupBy(p=>CommonId(p.id)).Select(g=>new MerchantPose{id=g.Key,position=g.First().position,rotation=g.First().rotation}).ToArray();
                    _data.version=2;save=true;
                }
                foreach(var baseline in Defaults().layouts)
                {
                    var layout=_data.layouts.FirstOrDefault(l=>l?.template==baseline.template);
                    if(layout==null){_data.layouts=_data.layouts.Concat(new[]{baseline}).ToArray();save=true;continue;}
                    var missing=baseline.objects.Where(p=>!(layout.objects??new MerchantPose[0]).Any(o=>o?.id==p.id)).ToArray();
                    if(missing.Length>0){layout.objects=(layout.objects??new MerchantPose[0]).Concat(missing).ToArray();save=true;}
                }
                if(save)EditorStore.AtomicWrite(PathName,TrafficCodec.WriteRecord(_data));
                return _data;
            }
        }
        internal static string CommonId(string id){var slash=(id??"").IndexOf('/');return slash>0&&MerchantCatalog.Known(id.Substring(0,slash))?id.Substring(slash+1):id;}
        internal static void Apply(MerchantHost host)
        {
            var layout=Data.layouts.FirstOrDefault(l=>l!=null&&l.template==host.Data.template);if(layout?.objects==null)return;
            foreach(var pose in layout.objects.OrderBy(p=>p.id=="board"?0:1)){var obj=host.Objects.FirstOrDefault(o=>CommonId(o.Id)==pose.id);if(obj?.Transform==null)continue;obj.Transform.position=host.transform.TransformPoint(pose.position);obj.Transform.rotation=host.transform.rotation*Quaternion.Euler(pose.rotation);}
        }
        internal static string Save(GameObject car)
        {
            var host=car!=null?car.GetComponent<MerchantHost>():null;if(host==null)return "No merchant bus selected";
            var layout=new MerchantLayout{template=host.Data.template,objects=host.Objects.Where(o=>o.Transform!=null).Select(o=>new MerchantPose{id=CommonId(o.Id),position=car.transform.InverseTransformPoint(o.Transform.position),rotation=(Quaternion.Inverse(car.transform.rotation)*o.Transform.rotation).eulerAngles}).ToArray()};
            var old=Data.layouts.FirstOrDefault(l=>l.template==layout.template);
            if(old?.objects!=null)layout.objects=old.objects.Where(p=>!layout.objects.Any(n=>n.id==p.id)).Concat(layout.objects).ToArray();
            var list=Data.layouts.Where(l=>l.template!=layout.template).ToList();list.Add(layout);Data.layouts=list.ToArray();EditorStore.AtomicWrite(PathName,TrafficCodec.WriteRecord(Data));foreach(var merchant in MerchantHost.All.Where(h=>h!=null&&h.Data?.template==layout.template))Apply(merchant);return "Saved shared placement for all merchant types: "+layout.template;
        }
    }
}
