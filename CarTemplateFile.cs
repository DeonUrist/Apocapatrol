using System;

namespace Apocapatrol
{
    // The car template file (JSON, read and written with Unity's JsonUtility). The same two classes live in Apocatemplater
    // (TemplateFile.cs, the dumper) - keep the field names in sync. Every field has a default, so a hand-written file may leave any out.
    [Serializable]
    public class TemplateFile
    {
        public int schema = 1;
        public string name = "";            // template name (spawner menu, logs); "" = the file name
        public string body = "";            // frame prefab (PipeRat, Poloska, TinyTyrant, Junker, Rustcargo ...)
        public string kind = "";            // small | junker | truck   ("" = guessed from the body like the built-in park)
        public string tier = "";            // basic | advanced          ("" = guessed from the name: "Advanced" in it = advanced)
        public bool spawns = true;          // false = only in the spawner menu, never picked by patrols / convoys
        public float weight = 1f;           // relative pick chance among the templates of the same kind + tier (built-ins are 1)
        public string rams = "";            // None | Pedestrians | Cars ("" = Cars for trucks, Pedestrians otherwise)
        public string driver = "";          // crew prefab (Scraffa, Spanna, Sprokka, Boltjaw, Flexa, Lugnut, Scrud); "" = nobody
        public string passenger = "";
        public string cargo = "";           // "" none | "Random" (rolled by the [Loot] chances) | a loot key (Food ...) | an item spec "a:2;b:1-3"
        public float lootScaleMin = 1f, lootScaleMax = 1f;
        public string[] bumpers = new string[0];   // a front bumper rolled per build ("" = none), only if no part sits on hinge_bumper_front
        public bool fillFuel = true, releaseHandbrake = true;
        public TemplatePart[] parts = new TemplatePart[0];
        public string source = "";          // who wrote it (Apocatemplater version, date, the car it came from) - information only
    }

    [Serializable]
    public class TemplatePart
    {
        public int parent = -1;             // index of the part this one is attached to; -1 = the frame
        public string hinge = "";           // path from the frame (or the parent part) to the hinge, e.g. "parts/hinge_wheel_FL"
        public string prefab = "";          // part prefab (asset root name)
        public float[] hingePos = new float[0];   // hinge local position / euler angles when moved off the prefab's (adjust tool); empty = untouched
        public float[] hingeRot = new float[0];
        public float[] pos = new float[0];        // the part's own local offset on the hinge; empty = zero (the game's attach resets it)
        public float[] rot = new float[0];
    }
}
