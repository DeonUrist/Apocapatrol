using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocapatrol
{
    [Serializable] internal sealed class BusPassengerPreset
    {
        // Forward bend, sideways swing, axial twist. Knees bend opposite to thighs.
        public Vector3[] left={new Vector3(85f,-5f,0f),new Vector3(-90f,0f,0f),new Vector3(25f,-5f,0f),new Vector3(25f,0f,0f),Vector3.zero};
        public Vector3[] right={new Vector3(85f,5f,0f),new Vector3(-90f,0f,0f),new Vector3(25f,5f,0f),new Vector3(25f,0f,0f),Vector3.zero};
        public Vector3 seatOffset;
    }

    internal static class BusPassengerPoseStore
    {
        private static BusPassengerPreset _preset;
        internal static BusPassengerPreset Current { get { if(_preset==null)Load();return _preset; } }
        private static string FilePath=>Path.Combine(EditorStore.Root,"BusPassengerPose.json");
        private static void Load()
        {
            _preset=new BusPassengerPreset();
            try
            {
                if(!File.Exists(FilePath))return;
                var saved=JsonUtility.FromJson<BusPassengerPreset>(File.ReadAllText(FilePath));
                if(saved==null||saved.left==null||saved.right==null||saved.left.Length!=5||saved.right.Length!=5)throw new InvalidDataException("Expected five joints per side");
                for(int i=0;i<5;i++){saved.left[i]=Safe(saved.left[i],180f);saved.right[i]=Safe(saved.right[i],180f);}
                saved.seatOffset=Safe(saved.seatOffset,1f);_preset=saved;
            }
            catch(Exception e){Plugin.Log.LogWarning("Bus passenger pose: "+e.Message);}
        }
        private static Vector3 Safe(Vector3 value,float limit)
        {
            if(float.IsNaN(value.x)||float.IsInfinity(value.x)||float.IsNaN(value.y)||float.IsInfinity(value.y)||float.IsNaN(value.z)||float.IsInfinity(value.z))throw new InvalidDataException("Invalid joint value");
            return new Vector3(Mathf.Clamp(value.x,-limit,limit),Mathf.Clamp(value.y,-limit,limit),Mathf.Clamp(value.z,-limit,limit));
        }
        internal static void Reset(){_preset=new BusPassengerPreset();}
        internal static string Save()
        {
            try
            {
                Directory.CreateDirectory(EditorStore.Root);string temp=FilePath+".tmp";
                File.WriteAllText(temp,JsonUtility.ToJson(Current,true));
                if(File.Exists(FilePath))File.Replace(temp,FilePath,FilePath+".bak");else File.Move(temp,FilePath);
                return "Saved BusPassengerPose.json (all bus passengers)";
            }
            catch(Exception e){Plugin.Log.LogWarning("Bus passenger pose save: "+e.Message);return "Save failed: "+e.Message;}
        }
    }

    // Keyboard overlay: no cursor capture, scene scans, or extra work while disabled.
    internal sealed class BusPassengerPoseEditor : MonoBehaviour
    {
        internal static Pose Target;
        internal static bool Active=>Plugin.BusPassengerPoseEditorEnabled!=null&&Plugin.BusPassengerPoseEditorEnabled.Value;
        internal static bool Editing(Pose pose)=>Active&&Target==pose;
        private static readonly string[] Joints={"Thigh","Knee","Upper arm","Elbow","Wrist","Seat position"};
        private int _joint,_side;private float _nextFind;private string _status="";
        private void Update()
        {
            if(Plugin.Pressed(Plugin.BusPassengerPoseEditorKey.Value))Plugin.BusPassengerPoseEditorEnabled.Value=!Active;
            if(!Active){Target=null;return;}
            if(Time.unscaledTime>=_nextFind)
            {
                _nextFind=Time.unscaledTime+.5f;Target=null;
                var player=PlayerRef.Player;var camera=Camera.main;
                if(camera==null&&player==null)return;
                var origin=camera!=null?camera.transform.position:player.position;float best=80f*80f;
                foreach(var marker in PatrolMarker.All)
                {
                    if(marker==null||marker.Passenger==null)continue;
                    var pose=marker.Passenger.GetComponent<Pose>();if(pose==null||!pose.IsBusPassenger)continue;
                    float distance=(pose.transform.position-origin).sqrMagnitude;
                    if(distance<best){best=distance;Target=pose;}
                }
            }
            if(Target==null)return;
            if(Plugin.Pressed(Key.Numpad0))_joint=(_joint+1)%Joints.Length;
            if(Plugin.Pressed(Key.NumpadDivide))_side=(_side+1)%3;
            if(Plugin.Pressed(Key.Numpad5))_status=BusPassengerPoseStore.Save();
            if(Plugin.Pressed(Key.NumpadPeriod)){BusPassengerPoseStore.Reset();_status="Defaults restored; press Num5 to save";}
            var kb=Keyboard.current;bool shift=kb!=null&&(kb.leftShiftKey.isPressed||kb.rightShiftKey.isPressed);
            float step=_joint==5?.01f:1f;if(shift)step*=5f;
            var delta=Vector3.zero;
            if(Plugin.Pressed(Key.Numpad8))delta.x+=step;if(Plugin.Pressed(Key.Numpad2))delta.x-=step;
            if(Plugin.Pressed(Key.Numpad6))delta.y+=step;if(Plugin.Pressed(Key.Numpad4))delta.y-=step;
            if(Plugin.Pressed(Key.Numpad9))delta.z+=step;if(Plugin.Pressed(Key.Numpad7))delta.z-=step;
            if(delta==Vector3.zero)return;
            var preset=BusPassengerPoseStore.Current;
            if(_joint==5)preset.seatOffset=Clamp(preset.seatOffset+new Vector3(delta.y,delta.z,delta.x),1f);
            else
            {
                if(_side!=2)preset.left[_joint]=Clamp(preset.left[_joint]+delta,180f);
                if(_side!=1)preset.right[_joint]=Clamp(preset.right[_joint]+(_side==0?new Vector3(delta.x,-delta.y,-delta.z):delta),180f);
            }
            _status="Unsaved live changes";
        }
        private static Vector3 Clamp(Vector3 value,float limit)=>new Vector3(Mathf.Clamp(value.x,-limit,limit),Mathf.Clamp(value.y,-limit,limit),Mathf.Clamp(value.z,-limit,limit));
        private void OnGUI()
        {
            if(!Active||Event.current.type!=EventType.Repaint)return;
            if(Target==null){GUI.Box(new Rect(12,12,560,65),"Bus passenger pose editor: no seated bus passenger within 80 m\nSpawn a bus template with a passenger. Toggle: "+Plugin.BusPassengerPoseEditorKey.Value);return;}
            var preset=BusPassengerPoseStore.Current;
            string value=_joint==5?preset.seatOffset.ToString("F2"):"L "+preset.left[_joint].ToString("F0")+"  R "+preset.right[_joint].ToString("F0");
            GUI.Box(new Rect(12,12,670,145),"Bus passenger pose: "+Target.name+"\n"+Joints[_joint]+" / "+(_side==0?"Both (mirrored)":_side==1?"Left":"Right")+"  "+value
                +"\nNum0 next joint   Num/ both/left/right   Shift faster"
                +"\nNum8/2 bend   Num4/6 sideways   Num7/9 twist"
                +"\nSeat: Num8/2 forward/back   Num4/6 left/right   Num7/9 down/up"
                +"\nNum5 save   Num. reset   "+Plugin.BusPassengerPoseEditorKey.Value+" close\n"+_status);
        }
        private void OnDestroy(){Target=null;}
    }
}
