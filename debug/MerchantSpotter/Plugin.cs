using System;
using System.Linq;
using Apocapatrol;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
namespace ApocapatrolMerchantSpotter
{
    [BepInPlugin("com.denis.apocalypter.merchantspotter","Merchant Object Spotter","1.0.2")]
    [BepInDependency(Apocapatrol.Plugin.GUID)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static MerchantEditor _runner;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<Key> _toggle;
        private static ManualLogSource _log;
        private void Awake()
        {
            Config.Bind("General","Apocasetter",true,"Show the editor and its key binding in the MODS settings window.");
            _enabled=Config.Bind("Debug","Enabled",false,"Enable the in-car merchant object editor. Disable TurretSpotEditor while using it.");
            _toggle=Config.Bind("Debug","ToggleKey",Key.NumpadMultiply,"Rebind the merchant editor toggle in MODS settings. None disables the shortcut; Enabled also opens the editor.");
            _log=Logger;SceneManager.sceneLoaded+=OnSceneLoaded;EnsureRunner();
        }
        private static void OnSceneLoaded(Scene scene,LoadSceneMode mode)=>EnsureRunner();
        private static void EnsureRunner()
        {
            if(_runner!=null)return;
            var go=new GameObject("MerchantSpotter.Runner"){hideFlags=HideFlags.HideAndDontSave};DontDestroyOnLoad(go);
            _runner=go.AddComponent<MerchantEditor>();_runner.Configure(_enabled,_toggle,_log);
            _log.LogInfo("Persistent merchant editor ready; toggle key: "+_toggle.Value);
        }
    }
    public sealed class MerchantEditor : MonoBehaviour
    {
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<Key> _toggle;
        private ManualLogSource Logger;
        private GameObject _car,_marker,_forward;
        private int _slot;private string _kind="Box",_status="";private float _next,_step=.05f;
        private MerchantObject[] _objects=new MerchantObject[0];
        internal void Configure(ConfigEntry<bool> enabled,ConfigEntry<Key> toggle,ManualLogSource log){_enabled=enabled;_toggle=toggle;Logger=log;}
        private static bool Press(Key key)=>key!=Key.None&&Keyboard.current!=null&&Keyboard.current[key].wasPressedThisFrame;
        private static bool Held(Key key)=>Keyboard.current!=null&&Keyboard.current[key].isPressed;
        private MerchantObject Current=>_objects.Length==0?null:_objects[Mathf.Clamp(_slot,0,_objects.Length-1)];
        private void Update()
        {
            if(_enabled==null)return;
            if(Press(_toggle.Value)){_enabled.Value=!_enabled.Value;_next=0;Logger.LogInfo("Merchant editor "+(_enabled.Value?"enabled":"disabled"));}
            if(!_enabled.Value){Hide();return;}
            if(Time.unscaledTime>=_next)
            {
                _next=Time.unscaledTime+.5f;var cam=Camera.main;
                var nearest=cam!=null?MerchantApi.Cars().Where(c=>c!=null&&(c.transform.position-cam.transform.position).sqrMagnitude<6400f).OrderBy(c=>(c.transform.position-cam.transform.position).sqrMagnitude).FirstOrDefault():null;
                if(nearest!=_car){_car=nearest;_slot=0;_kind="Box";}
                if(_car!=null){var all=MerchantApi.Objects(_car);if(!all.Any(o=>o.Kind==_kind&&o.Transform!=null)){var first=all.FirstOrDefault(o=>o.Transform!=null);if(first!=null){_kind=first.Kind;_slot=0;}}}
                _objects=MerchantApi.Objects(_car).Where(o=>o.Kind==_kind&&o.Transform!=null).ToArray();
            }
            if(_car==null||Current==null){Hide();return;}MerchantApi.Freeze(_car);
            if(Press(Key.NumpadDivide)){var kinds=MerchantApi.Objects(_car).Select(o=>o.Kind).Distinct().ToArray();int i=Array.IndexOf(kinds,_kind);_kind=kinds[(i+1)%kinds.Length];_slot=0;_next=0;return;}
            if(Press(Key.Numpad0))_slot=(_slot+1)%_objects.Length;
            if(Press(Key.NumpadPlus))_step=Mathf.Min(1f,_step*2f);if(Press(Key.NumpadMinus))_step=Mathf.Max(.005f,_step/2f);
            var node=Current.Transform;float step=_step*((Held(Key.LeftShift)||Held(Key.RightShift))?5f:1f);bool rotate=Held(Key.LeftCtrl)||Held(Key.RightCtrl);
            var delta=Vector3.zero;
            if(Press(Key.Numpad8))delta.z+=step;if(Press(Key.Numpad2))delta.z-=step;
            if(Press(Key.Numpad6))delta.x+=step;if(Press(Key.Numpad4))delta.x-=step;
            if(Press(Key.Numpad9))delta.y+=step;if(Press(Key.Numpad7))delta.y-=step;
            if(rotate)node.Rotate(new Vector3(delta.y,delta.x,delta.z)*(2f/_step),Space.Self);
            else node.position+=_car.transform.TransformDirection(delta);
            if(Press(Key.Numpad5)){try{_status=MerchantApi.Save(_car);Logger.LogInfo(_status);}catch(Exception e){_status="Save failed: "+e.Message;Logger.LogError(e);}}
            Mark(ref _marker,node.position,new Color(1f,.8f,.1f),.13f);Mark(ref _forward,node.position+node.forward*.4f,Color.green,.08f);
        }
        private static void Mark(ref GameObject marker,Vector3 point,Color color,float size)
        {
            if(marker==null){marker=GameObject.CreatePrimitive(PrimitiveType.Sphere);marker.name="MerchantSpotter.Marker";marker.hideFlags=HideFlags.HideAndDontSave;Destroy(marker.GetComponent<Collider>());marker.GetComponent<Renderer>().material.color=color;}
            marker.SetActive(true);marker.transform.position=point;marker.transform.localScale=Vector3.one*size;
        }
        private void Hide(){if(_marker!=null)_marker.SetActive(false);if(_forward!=null)_forward.SetActive(false);}
        private void OnGUI()
        {
            if(_enabled==null||!_enabled.Value||Event.current.type!=EventType.Repaint)return;
            GUI.depth=-1100;
            string selected=Current!=null?Current.Label+"  "+_car.transform.InverseTransformPoint(Current.Transform.position).ToString("F3"):_car!=null?"Merchant bus found; waiting for shop objects":"No merchant bus found within 80 m. Spawn it from Merchants, not as an ordinary vehicle.";
            GUI.Box(new Rect(15,15,850,110),"Merchant object editor: "+selected+"\n/ = object type   0 = next object   5 = save   "+_toggle.Value+" = toggle\n8/2 forward/back   4/6 sideways   7/9 down/up   Shift = x5   +/- = step ("+_step.ToString("0.000")+" m)\nCtrl + 4/6 yaw   Ctrl + 7/9 pitch   Ctrl + 8/2 roll   "+_status);
        }
        private void OnDestroy(){foreach(var m in new[]{_marker,_forward})if(m!=null){Destroy(m.GetComponent<Renderer>().material);Destroy(m);}}
    }
}
