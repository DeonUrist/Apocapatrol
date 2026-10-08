using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using UnityEngine;
using Ap = Apocaplayer.ModAPI;

namespace Apocapatrol
{
    // (2.6.1) Human turrets animated by Apocaplayer's ModAPI (Apocaplayer 2.2.0+): the player's own crouch set on the roof - a Warboy crouches
    // (CrouchIdle) and throws his lance over the crouched legs (ThrowRight on the upper body), stands up before a motorcycle leap, jumps with the
    // player's Jump; a gunner crouches with its gun in the right hand at the player's weapon poses, aiming (RifleCrouchAim / the pistol's) and
    // pitching its spine at the target, the whole body turned by Pose. The body never walks: it rides, its velocity is held at zero.
    //
    // Every call into Apocaplayer is in this class, in NoInlining methods that run only once Available said the ModAPI is there; a body is kept
    // as a plain object. Without it (or an older Apocaplayer) the 2.6.0 animation stays: the bundle clips by name (Rider) and the bone squat (Pose).
    // A character some other mod already animates (NPCAI's gunmen, by the same Animator) is left to that mod: Attach returns null.
    internal static class PlayerAnims
    {
        internal const string ApocaplayerGuid = "com.denis.apocalypter.apocaplayer";
        private static bool _tried, _ok;

        internal static bool Available { get { Init(); return _ok; } }

        private static void Init()
        {
            if (_tried) return;
            if (!Chainloader.PluginInfos.ContainsKey(ApocaplayerGuid)) { _tried = true; Plugin.Log.LogInfo("Human turrets: Apocaplayer not installed - no player animations for them"); return; }
            try { _ok = Probe(); }
            catch (Exception e) { _tried = true; _ok = false; Plugin.Log.LogWarning("Human turrets: this Apocaplayer has no ModAPI (2.2.0 or newer needed) - the old crouch (" + e.GetType().Name + ")"); return; }
            if (!_ok && Time.unscaledTime < 20f) return;      // the bundle may still be loading: asked again at the next spawn
            _tried = true;
            Plugin.Log.LogInfo("Human turrets: " + (_ok ? "animated by Apocaplayer's ModAPI" : "Apocaplayer's animation bundle is missing - the old crouch"));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Probe() { return Ap.ApiVersion >= 1 && Ap.Ready; }

        // ---------------------------------------------------------------- per body (only when Available)
        // a riding character: never walks (zero velocity), no turn-in-place / walk-to-stop clips (the body is turned by our code)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object Attach(Animator a)
        {
            if (a == null || !a.isHuman) return null;
            if (Ap.Find(a) != null) { Plugin.Verbose("Human turrets: " + a.gameObject.name + " is already animated by another mod - left to it"); return null; }
            var c = Ap.Attach(a);
            if (c == null) return null;
            c.ManualVelocity = true; c.Velocity = Vector3.zero;
            c.TurnInPlace = false; c.WalkToStop = false;
            return c;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Dispose(object body) { var c = body as Ap.Character; if (c != null) c.Dispose(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Alive(object body) { var c = body as Ap.Character; return c != null && c.Valid && !c.Suspended; }
        // this frame's situation (call every frame)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Set(object body, bool crouched, bool aiming, bool airborne, float aimPitch)
        {
            var c = body as Ap.Character; if (c == null) return;
            c.ManualVelocity = true; c.Velocity = Vector3.zero;
            c.Crouched = crouched; c.Aiming = aiming; c.Firing = false; c.Airborne = airborne; c.AimPitch = aimPitch;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Throw(object body) { var c = body as Ap.Character; if (c != null) c.Throw(true); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Jump(object body) { var c = body as Ap.Character; if (c != null) { c.Airborne = true; c.Jump(); } }
        // the gun model in the right hand (null = none: bare hands)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetWeapon(object body, Transform model, string key) { var c = body as Ap.Character; if (c != null) c.SetWeapon(model, model != null ? key : null); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string WeaponKey(string objectName) { return Ap.WeaponKey(objectName); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool HasWeaponPoses(string key) { return Ap.HasWeaponPoses(key); }
        // how long the right-hand throw takes on the hands (CharPlan: the clip's length, 0.5..2 s)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ThrowSeconds()
        {
            var clip = Ap.GetClip("ThrowRight") ?? Ap.GetClip("Throw");
            return clip != null ? Mathf.Clamp(clip.length, 0.5f, 2f) : 0.9f;
        }
    }
}
