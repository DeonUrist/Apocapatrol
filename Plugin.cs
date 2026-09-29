using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Apocapatrol
{
    // Prototype: press a key, a complete car (frame + wheels + engine + radiator + steering wheel) is assembled in front of
    // the player with the game's own part-attach recipe, fuelled and started. Groundwork for AI-driven raider cars.
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocapatrol";
        public const string NAME = "Apocapatrol";
        public const string VERSION = "0.3.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Body, Wheel, Engine, Radiator, SteeringWheel, Seat, Driver;
        internal static ConfigEntry<bool> ReleaseHandbrake, RegisterDriver;
        internal static ConfigEntry<float> DriverOffsetX, DriverOffsetY, DriverOffsetZ;
        internal static ConfigEntry<string> DriverDisabledFsms;
        internal static ConfigEntry<float> DriveDelaySeconds, DriveThrottle, StuckPedalChance;
        internal static ConfigEntry<float> SpawnDistance;
        internal static ConfigEntry<bool> FillFuel, StartEngine, NwhStartFallback, RegisterWithGame;
        internal static ConfigEntry<float> DriveTestSeconds, DriveTestThrottle;
        internal static ConfigEntry<Key> SpawnKey;
        internal static ConfigEntry<bool> VerboseLog;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;

            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");

            Body = Config.Bind("Build", "Body", "PipeRat", "Vehicle frame prefab (e.g. PipeRat, Duke, TinyTyrant)");
            Wheel = Config.Bind("Build", "Wheel", "small_wheel_1",
                "Wheel item, prefab name or in-game name (e.g. small_wheel_1 or \"Small rubbish wheel\"); one per hinge_wheel_*");
            Engine = Config.Bind("Build", "Engine", "1.2L I4 59HP 87Nm Gasoline", "Engine item, prefab name or in-game name");
            Radiator = Config.Bind("Build", "Radiator", "radiator_small", "Radiator item, prefab name or in-game name (empty = none)");
            SteeringWheel = Config.Bind("Build", "SteeringWheel", "steeringwheel_7", "Steering wheel item, prefab name or in-game name (empty = none)");
            Seat = Config.Bind("Build", "Seat", "poloska_seat_front_homemade", "Driver seat item, prefab name or in-game name (empty = none)");
            ReleaseHandbrake = Config.Bind("Build", "ReleaseHandbrake", true, "Release the handbrake (handbrake lever FSM -> HandbrakeOff)");
            SpawnDistance = Config.Bind("Build", "SpawnDistance", 8f, new ConfigDescription(
                "How far in front of the player the car appears (m)", new AcceptableValueRange<float>(3f, 100f)));
            FillFuel = Config.Bind("Build", "FillFuel", true, "Fill the tank (Fuel/LiquidAmount Liquid = LiquidCapacity)");
            StartEngine = Config.Bind("Build", "StartEngine", true, "Start the engine through the frame's own START key FSM (Ignition -> Start)");
            NwhStartFallback = Config.Bind("Build", "NwhStartFallback", true,
                "If the engine is not running 3 s after the FSM start, call NWH powertrain.engine.StartEngine() directly");
            RegisterWithGame = Config.Bind("Build", "RegisterWithGame", true,
                "Name and register the car (ArrayList_Cars) and every part (ArrayList_Items) like vanilla spawns so they are saved");
            DriveTestSeconds = Config.Bind("Build", "DriveTestSeconds", 0f, new ConfigDescription(
                "Only with no [Driver]: after starting, push the throttle for this long with nobody inside. 0 = off",
                new AcceptableValueRange<float>(0f, 60f)));
            DriveTestThrottle = Config.Bind("Build", "DriveTestThrottle", 0.5f, new ConfigDescription(
                "Throttle used by the drive test (0..1)", new AcceptableValueRange<float>(0.05f, 1f)));

            Driver = Config.Bind("Driver", "Driver", "Scraffa",
                "Who sits at the wheel: an enemy prefab (Scraffa, Boltjaw, ...) = live driver without AI that drives the car; " +
                "a *_Dead prefab = ragdoll passenger only; empty = nobody (then the [Build] drive test applies)");
            DriverDisabledFsms = Config.Bind("Driver", "DisabledFsms", "Movement;Unstuck;Rotate;Detection;Attack;RangedAttackWait;Damage Ranged;Codex;Sound;Sound2;Sound3",
                "FSMs switched off on a live driver (the AI and the body movers); Health/Damage/Bodypart stay on so it can be killed");
            DriveDelaySeconds = Config.Bind("Driver", "DriveDelaySeconds", 3f, new ConfigDescription(
                "The driver sits still this long after spawning before driving off", new AcceptableValueRange<float>(0f, 600f)));
            DriveThrottle = Config.Bind("Driver", "DriveThrottle", 0.5f, new ConfigDescription(
                "Throttle the driver holds (0..1); steering straight for now", new AcceptableValueRange<float>(0.05f, 1f)));
            StuckPedalChance = Config.Bind("Driver", "StuckPedalChance", 5f, new ConfigDescription(
                "% chance that a killed driver's leg stays on the gas; otherwise the gas is released and the car rolls out on its own (handbrake once it rests)",
                new AcceptableValueRange<float>(0f, 100f)));
            DriverOffsetX = Config.Bind("Driver", "OffsetX", 0f, new ConfigDescription("Driver offset from the car's sitPos, right (m)", new AcceptableValueRange<float>(-2f, 2f)));
            DriverOffsetY = Config.Bind("Driver", "OffsetY", 0f, new ConfigDescription("Driver offset from the car's sitPos, up (m)", new AcceptableValueRange<float>(-2f, 2f)));
            DriverOffsetZ = Config.Bind("Driver", "OffsetZ", 0f, new ConfigDescription("Driver offset from the car's sitPos, forward (m)", new AcceptableValueRange<float>(-2f, 2f)));
            RegisterDriver = Config.Bind("Driver", "RegisterDriver", false, "Register the driver like a vanilla spawn (saved, but the seat pin is not - after a load it is loose)");

            SpawnKey = Config.Bind("Debug", "SpawnKey", Key.F7, "Assemble one car in front of the player. None = off");
            VerboseLog = Config.Bind("Debug", "VerboseLog", true, "Log every build step (prefab lookups, hinge states, engine state)");

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Patrol.ResetForScene(); };
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }

        private static void EnsureRunner()
        {
            // the game destroys the plugin's GameObject on scene load; the logic lives on a hidden object it can't find
            if (_runner != null) return;
            _runner = new GameObject("Apocapatrol.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner);
            _runner.AddComponent<Patrol>();
        }

        internal static void Verbose(string msg)
        {
            if (VerboseLog.Value) Log.LogInfo(msg);
        }

        internal static bool Pressed(Key key)
        {
            if (key == Key.None) return false;
            var kb = Keyboard.current;
            if (kb == null) return false;
            try { return kb[key].wasPressedThisFrame; }
            catch (Exception) { return false; }
        }
    }
}
