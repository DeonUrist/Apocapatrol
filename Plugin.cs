using System;
using System.Collections.Generic;
using System.IO;
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
        public const string VERSION = "0.8.7";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Body, Wheel, Engine, Radiator, SteeringWheel, Seat, PassengerSeat, Driver, Passenger;
        internal static ConfigEntry<bool> ReleaseHandbrake, RegisterDriver;
        internal static PoseFloat DriverOffsetX, DriverOffsetY, DriverOffsetZ;
        internal static ConfigEntry<bool> PassengerRangedCombat;
        internal static ConfigEntry<float> PassengerFireArc, PassengerMaxAimPitch, PassengerAimTurnSpeed;
        internal static PoseBool PoseEnabled;
        internal static PoseFloat PoseThigh, PoseKnee, PoseArm, PoseElbow, PoseLeftLegCloser, PoseRightLegCloser, PoseLeftArmCloser, PoseRightArmCloser;
        internal static ConfigEntry<float> DriveDelaySeconds, DriveThrottle, StuckPedalChance;
        internal static ConfigEntry<float> SpawnDistance;
        internal static ConfigEntry<bool> FillFuel, StartEngine, NwhStartFallback, RegisterWithGame;
        internal static ConfigEntry<float> DriveTestSeconds, DriveTestThrottle;
        internal static ConfigEntry<Key> SpawnKey;
        internal static ConfigEntry<bool> VerboseLog;
        internal static readonly Dictionary<string, ArmPoseProfile> HumanArmPoses = new Dictionary<string, ArmPoseProfile>(StringComparer.OrdinalIgnoreCase);
        internal static readonly string[] HumanTypes = { "Boltjaw", "Flexa", "Lugnut", "Scrud", "Sprokka", "Scraffa", "Spanna" };

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;
            bool exposePose = PoseConfigurationEnabled(Config.ConfigFilePath);

            Config.Bind("General", "Apocasetter", true,
                "Show this mod in the Apocasetter Mods menu.\n" +
                "To expose seat offsets, shared pose settings and all per-human controls, uncomment the next line and restart:\n" +
                "PoseConfiguration = true");

            Body = Config.Bind("Build", "Body", "PipeRat", "Vehicle frame prefab (e.g. PipeRat, Duke, TinyTyrant)");
            Wheel = Config.Bind("Build", "Wheel", "small_wheel_1",
                "Wheel item, prefab name or in-game name (e.g. small_wheel_1 or \"Small rubbish wheel\"); one per hinge_wheel_*");
            Engine = Config.Bind("Build", "Engine", "1.2L I4 59HP 87Nm Gasoline", "Engine item, prefab name or in-game name");
            Radiator = Config.Bind("Build", "Radiator", "radiator_small", "Radiator item, prefab name or in-game name (empty = none)");
            SteeringWheel = Config.Bind("Build", "SteeringWheel", "steeringwheel_7", "Steering wheel item, prefab name or in-game name (empty = none)");
            Seat = Config.Bind("Build", "Seat", "poloska_seat_front_homemade", "Driver seat item, prefab name or in-game name (empty = none)");
            PassengerSeat = Config.Bind("Build", "PassengerSeat", "poloska_seat_front_homemade", "Front passenger seat item, prefab name or in-game name (empty = none)");
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
                "empty = nobody (then the [Build] drive test applies)");
            DriveDelaySeconds = Config.Bind("Driver", "DriveDelaySeconds", 3f, new ConfigDescription(
                "The driver sits still this long after spawning before driving off (0 = drives as soon as the engine runs, -1 = never drives, just sits)",
                new AcceptableValueRange<float>(-1f, 600f)));
            DriveThrottle = Config.Bind("Driver", "DriveThrottle", 0.5f, new ConfigDescription(
                "Throttle the driver holds (0..1); steering straight for now", new AcceptableValueRange<float>(0.05f, 1f)));
            StuckPedalChance = Config.Bind("Driver", "StuckPedalChance", 5f, new ConfigDescription(
                "% chance that a killed driver's gas pedal stays stuck; otherwise the gas is released and the car slowly rolls to a stop",
                new AcceptableValueRange<float>(0f, 100f)));
            var offsetRange = new AcceptableValueRange<float>(-2f, 2f);
            DriverOffsetX = PoseFloat.Create(Config, "Driver", "OffsetX", 0f, "Where each occupant's hips go: offset from its seat anchor, right (m)", offsetRange, exposePose);
            DriverOffsetY = PoseFloat.Create(Config, "Driver", "OffsetY", -0.3f, "Where each occupant's hips go: offset from its seat anchor, up (m)", offsetRange, exposePose);
            DriverOffsetZ = PoseFloat.Create(Config, "Driver", "OffsetZ", 0f, "Where each occupant's hips go: offset from its seat anchor, forward (m)", offsetRange, exposePose);
            RegisterDriver = Config.Bind("Driver", "RegisterDriver", false,
                "Deprecated: occupants are persisted by Apocapatrol sidecars; vanilla item registration is no longer used");

            Passenger = Config.Bind("Passenger", "Passenger", "Flexa",
                "Who sits in the front passenger seat; Boltjaw/Flexa/Lugnut/Scrud/Sprokka shoot, Scraffa/Spanna remain passive; empty = nobody");
            PassengerRangedCombat = Config.Bind("Passenger", "RangedCombat", true,
                "Let ranged passengers use their vanilla targeting and attack logic while seated");
            PassengerFireArc = Config.Bind("Passenger", "FireArcHalfAngle", 90f, new ConfigDescription(
                "Passenger may fire this many degrees left or right of the car's forward direction",
                new AcceptableValueRange<float>(0f, 180f)));
            PassengerMaxAimPitch = Config.Bind("Passenger", "MaxAimPitch", 35f, new ConfigDescription(
                "Maximum upper-body aim angle up or down (degrees)", new AcceptableValueRange<float>(0f, 80f)));
            PassengerAimTurnSpeed = Config.Bind("Passenger", "AimTurnSpeed", 180f, new ConfigDescription(
                "How quickly the passenger turns its upper body toward or away from a target (degrees/second)",
                new AcceptableValueRange<float>(1f, 720f)));

            PoseEnabled = PoseBool.Create(Config, "Pose", "Enabled", true, "Seated pose for live occupants", exposePose);
            var bodyRange = new AcceptableValueRange<float>(0f, 130f);
            var closerRange = new AcceptableValueRange<float>(-60f, 60f);
            PoseThigh = PoseFloat.Create(Config, "Pose", "ThighAngle", 85f, "Thigh angle", bodyRange, exposePose);
            PoseKnee = PoseFloat.Create(Config, "Pose", "KneeAngle", 30f, "Knee angle", bodyRange, exposePose);
            PoseArm = PoseFloat.Create(Config, "Pose", "ArmAngle", 65f, "Fallback arm angle", bodyRange, exposePose);
            PoseLeftLegCloser = PoseFloat.Create(Config, "Pose", "LeftLegCloser", 10f, "Left leg inward angle", closerRange, exposePose);
            PoseRightLegCloser = PoseFloat.Create(Config, "Pose", "RightLegCloser", 10f, "Right leg inward angle", closerRange, exposePose);
            PoseLeftArmCloser = PoseFloat.Create(Config, "Pose", "LeftArmCloser", 10f, "Fallback left arm inward angle", closerRange, exposePose);
            PoseRightArmCloser = PoseFloat.Create(Config, "Pose", "RightArmCloser", 10f, "Fallback right arm inward angle", closerRange, exposePose);
            PoseElbow = PoseFloat.Create(Config, "Pose", "ElbowAngle", 25f, "Fallback elbow angle", bodyRange, exposePose);

            foreach (var human in HumanTypes)
                HumanArmPoses[human] = ArmPoseProfile.Create(Config, human,
                    Array.IndexOf(PassengerGuard.RangedHumans, human) >= 0, exposePose);
            ArmPoseProfile.RemoveHardcodedSettings(Config);
            if (!exposePose) ArmPoseProfile.RemoveStoredConfiguration(Config);

            SpawnKey = Config.Bind("Debug", "SpawnKey", Key.F7, "Assemble one car in front of the player. None = off");
            VerboseLog = Config.Bind("Debug", "VerboseLog", true, "Log every build step (prefab lookups, hinge states, engine state)");

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Patrol.ResetForScene(); PatrolPersistence.ResetForScene(); };
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }

        private static bool PoseConfigurationEnabled(string path)
        {
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("#") || line.StartsWith(";")) continue;
                    int separator = line.IndexOf('=');
                    if (separator < 0 || !string.Equals(line.Substring(0, separator).Trim(), "PoseConfiguration", StringComparison.OrdinalIgnoreCase)) continue;
                    bool enabled;
                    return bool.TryParse(line.Substring(separator + 1).Trim(), out enabled) && enabled;
                }
            }
            catch (Exception e) { Log.LogWarning("Could not read PoseConfiguration switch: " + e.Message); }
            return false;
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

    internal class BonePoseConfig
    {
        internal PoseFloat Vertical, Horizontal, Rotation;

        internal static BonePoseConfig Create(ConfigFile config, string section, string bone, float vertical, float horizontal, float twist, bool expose)
        {
            var range = new AcceptableValueRange<float>(-180f, 180f);
            return new BonePoseConfig
            {
                Vertical = PoseFloat.Create(config, section, bone + "Vertical", vertical, bone + " vertical angle: positive swings forward/up (degrees)", range, expose),
                Horizontal = PoseFloat.Create(config, section, bone + "Horizontal", horizontal, bone + " horizontal angle: positive turns toward the occupant's right (degrees)", range, expose),
                Rotation = PoseFloat.Create(config, section, bone + "Rotation", twist, bone + " axial rotation around the limb/joint (degrees)", range, expose)
            };
        }
    }

    internal class PoseFloat
    {
        private readonly float _defaultValue;
        private readonly ConfigEntry<float> _entry;
        internal float Value { get { return _entry == null ? _defaultValue : _entry.Value; } }

        private PoseFloat(float defaultValue, ConfigEntry<float> entry) { _defaultValue = defaultValue; _entry = entry; }

        internal static PoseFloat Create(ConfigFile config, string section, string key, float value, string description,
            AcceptableValueRange<float> range, bool expose)
        {
            return new PoseFloat(value, expose ? config.Bind(section, key, value, new ConfigDescription(description, range)) : null);
        }
    }

    internal class PoseBool
    {
        private readonly bool _defaultValue;
        private readonly ConfigEntry<bool> _entry;
        internal bool Value { get { return _entry == null ? _defaultValue : _entry.Value; } }

        private PoseBool(bool defaultValue, ConfigEntry<bool> entry) { _defaultValue = defaultValue; _entry = entry; }

        internal static PoseBool Create(ConfigFile config, string section, string key, bool value, string description, bool expose)
        {
            return new PoseBool(value, expose ? config.Bind(section, key, value, description) : null);
        }
    }

    internal class ArmPoseProfile
    {
        internal BonePoseConfig LeftArm, LeftElbow, LeftHand, RightArm, RightElbow, RightHand;
        internal WeaponPoseConfig Weapon;

        private static readonly Dictionary<string, float[]> Defaults = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Boltjaw", new[] { 80f,35f,-15.6f, 25f,0f,0f, 0f,0f,0f, -20f,50f,-20f, 125f,0f,25f, 0f,25f,-25f, 0f,0f,0f,-15f,20f,0f } },
            { "Flexa", new[] { 80f,35f,-15.652f, 25f,0f,0f, 0f,0f,0f, 5f,-10f,0f, 120f,0f,0f, 0f,25f,-45f, 0f,0f,0f,-15f,20f,0f } },
            { "Lugnut", new[] { 85f,34f,0f, 0f,0f,0f, 5f,15f,-10f, 80f,60f,20f, 20f,-140f,0f, -40f,50f,0f, 0f,-0.1f,0f,-15f,0f,0f } },
            { "Scrud", new[] { 78f,20f,30f, 25f,0f,0f, 0f,0f,0f, 75f,-20f,-30f, 25f,0f,0f, 0f,0f,0f, 0f,0f,0f,-25f,35f,0f } },
            { "Sprokka", new[] { 105f,10f,0f, 0f,0f,0f, 0f,0f,0f, 15f,-27f,-30f, 25f,0f,0f, 0f,0f,0f, 0f,0f,0.03f,0f,15f,0f } },
            { "Scraffa", new[] { 65f,10f,0f, 25f,0f,0f, 0f,0f,0f, 65f,-10f,0f, 25f,0f,0f, 0f,0f,0f } },
            { "Spanna", new[] { 65f,10f,0f, 25f,0f,0f, 0f,0f,0f, 65f,-10f,0f, 25f,0f,0f, 0f,0f,0f } }
        };

        internal static ArmPoseProfile Create(ConfigFile config, string human, bool ranged, bool expose)
        {
            string section = "Pose." + human;
            float[] d = Defaults[human];
            return new ArmPoseProfile
            {
                LeftArm = BonePoseConfig.Create(config, section, "LeftArm", d[0], d[1], d[2], expose),
                LeftElbow = BonePoseConfig.Create(config, section, "LeftElbow", d[3], d[4], d[5], expose),
                LeftHand = BonePoseConfig.Create(config, section, "LeftHand", d[6], d[7], d[8], expose),
                RightArm = BonePoseConfig.Create(config, section, "RightArm", d[9], d[10], d[11], expose),
                RightElbow = BonePoseConfig.Create(config, section, "RightElbow", d[12], d[13], d[14], expose),
                RightHand = BonePoseConfig.Create(config, section, "RightHand", d[15], d[16], d[17], expose),
                Weapon = ranged ? WeaponPoseConfig.Create(config, section, d, expose) : null
            };
        }

        internal static void RemoveStoredConfiguration(ConfigFile config)
        {
            bool save = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            foreach (var pair in Defaults)
            {
                string section = "Pose." + pair.Key;
                int count = pair.Value.Length;
                string[] bones = { "LeftArm", "LeftElbow", "LeftHand", "RightArm", "RightElbow", "RightHand" };
                foreach (var bone in bones)
                    foreach (var suffix in new[] { "Vertical", "Horizontal", "Rotation" })
                    {
                        var definition = new ConfigDefinition(section, bone + suffix);
                        config.Bind(definition, 0f).Value = 0f;
                        config.Remove(definition);
                    }
                if (count > 18)
                    foreach (var key in new[] { "WeaponPositionX", "WeaponPositionY", "WeaponPositionZ", "WeaponRotationX", "WeaponRotationY", "WeaponRotationZ" })
                    {
                        var definition = new ConfigDefinition(section, key);
                        config.Bind(definition, 0f).Value = 0f;
                        config.Remove(definition);
                    }
            }
            Remove<float>(config, "Driver", "OffsetX");
            Remove<float>(config, "Driver", "OffsetY");
            Remove<float>(config, "Driver", "OffsetZ");
            Remove<bool>(config, "Pose", "Enabled");
            foreach (var key in new[] { "ThighAngle", "KneeAngle", "ArmAngle", "LeftLegCloser", "RightLegCloser", "LeftArmCloser", "RightArmCloser", "ElbowAngle", "LegsCloser", "ArmsCloser" })
                Remove<float>(config, "Pose", key);
            Remove<bool>(config, "Pose", "FromRestPose");
            config.SaveOnConfigSet = save;
            config.Save();
        }

        internal static void RemoveHardcodedSettings(ConfigFile config)
        {
            bool save = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            Remove<string>(config, "Driver", "DisabledFsms");
            Remove<bool>(config, "Pose", "AngleConfiguration");
            config.SaveOnConfigSet = save;
            config.Save();
        }

        private static void Remove<T>(ConfigFile config, string section, string key)
        {
            var definition = new ConfigDefinition(section, key);
            config.Bind(definition, default(T));
            config.Remove(definition);
        }
    }

    internal class WeaponPoseConfig
    {
        internal PoseFloat PositionX, PositionY, PositionZ;
        internal PoseFloat RotationX, RotationY, RotationZ;

        internal static WeaponPoseConfig Create(ConfigFile config, string section, float[] d, bool expose)
        {
            var positionRange = new AcceptableValueRange<float>(-2f, 2f);
            var rotationRange = new AcceptableValueRange<float>(-180f, 180f);
            return new WeaponPoseConfig
            {
                PositionX = PoseFloat.Create(config, section, "WeaponPositionX", d[18], "Weapon local position along the hand's X axis (m)", positionRange, expose),
                PositionY = PoseFloat.Create(config, section, "WeaponPositionY", d[19], "Weapon local position along the hand's Y axis (m)", positionRange, expose),
                PositionZ = PoseFloat.Create(config, section, "WeaponPositionZ", d[20], "Weapon local position along the hand's Z axis (m)", positionRange, expose),
                RotationX = PoseFloat.Create(config, section, "WeaponRotationX", d[21], "Weapon local rotation around its X axis (degrees)", rotationRange, expose),
                RotationY = PoseFloat.Create(config, section, "WeaponRotationY", d[22], "Weapon local rotation around its Y axis (degrees)", rotationRange, expose),
                RotationZ = PoseFloat.Create(config, section, "WeaponRotationZ", d[23], "Weapon local rotation around its Z axis (degrees)", rotationRange, expose)
            };
        }
    }
}
