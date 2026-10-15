using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
namespace Apocapatrol
{
    // One shared cache on the player car, rather than hierarchy walks for each patrol's sensing tick.
    internal sealed class PatrolVisibility : MonoBehaviour
    {
        private Transform _engine;
        private float _horsepower, _refresh;
        private Light[] _lights = new Light[0];
        private static PatrolVisibility Of(GameObject car)
        { return car == null ? null : car.GetComponent<PatrolVisibility>() ?? car.AddComponent<PatrolVisibility>(); }
        private void Refresh()
        {
            if (Time.unscaledTime < _refresh) return;
            _refresh = Time.unscaledTime + 5f; _horsepower = 0f; _engine = null;
            _lights = GetComponentsInChildren<Light>(true).Where(l => l.type == LightType.Spot || l.transform.GetComponentsInParent<PlayMakerFSM>(true).Any(f => f.FsmName == "Headlight")).ToArray();
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                int end=t.name.IndexOf("HP",StringComparison.OrdinalIgnoreCase); if(end<=0)continue;
                int start=end-1;while(start>=0&&(char.IsDigit(t.name[start])||t.name[start]=='.'))start--;
                if(float.TryParse(t.name.Substring(start+1,end-start-1),NumberStyles.Float,CultureInfo.InvariantCulture,out var hp)&&hp>0f)
                { _horsepower=hp;_engine=t;break; }
            }
        }
        internal static bool LightsOn(GameObject car)
        {var torch=Camera.main!=null?Camera.main.transform.Find("Flashlight"):null;if(torch!=null&&torch.gameObject.activeInHierarchy)return true;var cache=Of(car);if(cache==null)return false;cache.Refresh();return cache._lights.Any(l=>l!=null&&l.enabled&&l.gameObject.activeInHierarchy&&l.intensity>0f);}
        internal static float EngineDistance(GameObject car)
        {if(car==null||!Nwh.EngineRunning(car))return 0f;var cache=Of(car);cache.Refresh();return cache._engine!=null&&cache._engine.IsChildOf(car.transform)?TrafficRules.EngineRange(cache._horsepower):0f;}
    }
}
