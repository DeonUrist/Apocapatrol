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
        public const string VERSION = "0.12.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Body, Wheel, Engine, Radiator, SteeringWheel, Seat, PassengerSeat, Driver, Passenger;
        internal static ConfigEntry<bool> ReleaseHandbrake, RegisterDriver;
        internal static PoseFloat DriverOffsetX, DriverOffsetY, DriverOffsetZ;
        internal static ConfigEntry<bool> PassengerRangedCombat, DriverRangedCombat;
        internal static ConfigEntry<float> DriverFireIntervalMin, DriverFireIntervalMax, DriverFireBurstSeconds;
        internal static ConfigEntry<float> PassengerFireArc, PassengerMaxAimPitch, PassengerAimTurnSpeed;
        internal static ConfigEntry<float> PassengerBailChance, PassengerTakeoverSeconds, StuckPedalTakeoverSeconds, EjectDistance, EjectSpeed, BailDistance, StuckBailChance;
        internal static PoseBool PoseEnabled;
        internal static PoseFloat PoseThigh, PoseKnee, PoseArm, PoseElbow, PoseLeftLegCloser, PoseRightLegCloser, PoseLeftArmCloser, PoseRightArmCloser;
        internal static ConfigEntry<float> DriveDelaySeconds, DriveThrottle, StuckPedalChance;
        internal static ConfigEntry<bool> AiEnabled, AiInvertSteering, AiOverlay;
        internal static ConfigEntry<float> AiThrottle, AiLeadTime, AiCommitSeconds, AiSteerRate, AiSteerAngle, AiMaxSteerAtSpeed, AiTurnSafeSpeed,
            AiRamDistance, AiPassWidth, AiRunOutMeters, AiRunOutMaxSeconds, AiReverseSeconds, AiReverseThrottle, AiStuckSeconds,
            AiRecoverWindow, AiMaxRecovers, AiWaitSeconds, AiFeelerRange, AiFeelerSpeedFactor, AiFrontOffset, AiMaxSlopeDeg, AiAvoidGain,
            AiIgnoreMassBelow, AiGiveUpDistance;
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
                "Throttle with the driving AI off (straight ahead) and of a stuck pedal (0..1); the AI has its own [AI] Throttle", new AcceptableValueRange<float>(0.05f, 1f)));
            StuckPedalChance = Config.Bind("Driver", "StuckPedalChance", 5f, new ConfigDescription(
                "% chance that a killed driver's gas pedal stays stuck; otherwise the gas is released and the car slowly rolls to a stop",
                new AcceptableValueRange<float>(0f, 100f)));
            DriverRangedCombat = Config.Bind("Driver", "RangedCombat", true,
                "A ranged human at the wheel (Boltjaw/Flexa/Lugnut/Scrud/Sprokka) shoots like a passenger, but only in bursts at random intervals; " +
                "between bursts it sits in the driver pose with its hands on the wheel");
            DriverFireIntervalMin = Config.Bind("Driver", "FireIntervalMin", 8f, new ConfigDescription(
                "Shortest pause between two bursts of the driver, s", new AcceptableValueRange<float>(0f, 120f)));
            DriverFireIntervalMax = Config.Bind("Driver", "FireIntervalMax", 20f, new ConfigDescription(
                "Longest pause between two bursts of the driver, s", new AcceptableValueRange<float>(0f, 300f)));
            DriverFireBurstSeconds = Config.Bind("Driver", "FireBurstSeconds", 3f, new ConfigDescription(
                "How long a burst lasts (the driver is in its shooting pose and its vanilla Attack runs inside the fire arc), s",
                new AcceptableValueRange<float>(0.5f, 30f)));
            StuckBailChance = Config.Bind("Driver", "StuckBailChance", 50f, new ConfigDescription(
                "% chance that, when the driving AI gives up on a stuck car (after [AI] MaxRecovers), the driver and the passenger get out and " +
                "fight on foot instead of the car waiting [AI] WaitSeconds and trying again; the car is left standing",
                new AcceptableValueRange<float>(0f, 100f)));
            var offsetRange = new AcceptableValueRange<float>(-2f, 2f);
            DriverOffsetX = PoseFloat.Create(Config, "Driver", "OffsetX", 0f, "Where each occupant's hips go: offset from its seat anchor, right (m)", offsetRange, exposePose);
            DriverOffsetY = PoseFloat.Create(Config, "Driver", "OffsetY", -0.3f, "Where each occupant's hips go: offset from its seat anchor, up (m)", offsetRange, exposePose);
            DriverOffsetZ = PoseFloat.Create(Config, "Driver", "OffsetZ", 0f, "Where each occupant's hips go: offset from its seat anchor, forward (m)", offsetRange, exposePose);
            RegisterDriver = Config.Bind("Driver", "RegisterDriver", false,
                "Deprecated: occupants are persisted by Apocapatrol sidecars; vanilla item registration is no longer used");

            AiEnabled = Config.Bind("AI", "Enabled", true,
                "Driving AI: chase and ram the player (on foot or in a car) hit-and-run style, reverse out of obstacles and steer around them. " +
                "false = the driver just drives straight ahead like before");
            AiThrottle = Config.Bind("AI", "Throttle", 1f, new ConfigDescription(
                "Maximum throttle the driving AI uses (0..1); [Driver] DriveThrottle is only for the AI-off drive and the stuck pedal",
                new AcceptableValueRange<float>(0.05f, 1f)));
            AiLeadTime = Config.Bind("AI", "LeadTime", 1f, new ConfigDescription(
                "Aim this many seconds ahead of the target's movement (intercept), s", new AcceptableValueRange<float>(0f, 4f)));
            AiCommitSeconds = Config.Bind("AI", "CommitSeconds", 0.5f, new ConfigDescription(
                "The aim point is re-taken only this often, so the car commits to a heading instead of twitching after the target, s",
                new AcceptableValueRange<float>(0.05f, 3f)));
            AiSteerRate = Config.Bind("AI", "SteerRate", 1.5f, new ConfigDescription(
                "How fast the wheel turns: full-lock units per second (1.5 = straight to full lock in 0.67 s). Lower = lazier, wider turns",
                new AcceptableValueRange<float>(0.2f, 10f)));
            AiSteerAngle = Config.Bind("AI", "SteerAngle", 30f, new ConfigDescription(
                "Angle to the aim point at which the driver asks for full lock, degrees (smaller = sharper corrections)",
                new AcceptableValueRange<float>(5f, 90f)));
            AiMaxSteerAtSpeed = Config.Bind("AI", "MaxSteerAtSpeed", 0.35f, new ConfigDescription(
                "Steering limit at 72 km/h and above (0..1); full lock is allowed below 18 km/h, blended in between. Keeps the car on its wheels",
                new AcceptableValueRange<float>(0.05f, 1f)));
            AiTurnSafeSpeed = Config.Bind("AI", "TurnSafeSpeed", 18f, new ConfigDescription(
                "Above this speed (m/s) the driver lifts off and brakes lightly when the target is more than 40 degrees off the nose",
                new AcceptableValueRange<float>(3f, 40f)));
            AiRamDistance = Config.Bind("AI", "RamDistance", 15f, new ConfigDescription(
                "Within this distance of the target (m), obstacle avoidance is switched off: go straight for the ram",
                new AcceptableValueRange<float>(0f, 50f)));
            AiPassWidth = Config.Bind("AI", "PassWidth", 12f, new ConfigDescription(
                "The target counts as passed when it is behind the car and within this many metres of the car's track",
                new AcceptableValueRange<float>(2f, 40f)));
            AiRunOutMeters = Config.Bind("AI", "RunOutMeters", 30f, new ConfigDescription(
                "After passing or ramming the target the car keeps going this far before turning around, m",
                new AcceptableValueRange<float>(0f, 200f)));
            AiRunOutMaxSeconds = Config.Bind("AI", "RunOutMaxSeconds", 4f, new ConfigDescription(
                "... or at most this long, s", new AcceptableValueRange<float>(0.5f, 20f)));
            AiReverseSeconds = Config.Bind("AI", "ReverseSeconds", 2f, new ConfigDescription(
                "How long the car reverses after hitting an obstacle or getting stuck, s (grows with repeated attempts)",
                new AcceptableValueRange<float>(0.5f, 10f)));
            AiReverseThrottle = Config.Bind("AI", "ReverseThrottle", 0.6f, new ConfigDescription(
                "Throttle (or brake, depending on how NWH reverses) used while reversing", new AcceptableValueRange<float>(0.1f, 1f)));
            AiStuckSeconds = Config.Bind("AI", "StuckSeconds", 2f, new ConfigDescription(
                "Not moving for this long while trying to drive forward = stuck, reverse out", new AcceptableValueRange<float>(0.5f, 10f)));
            AiRecoverWindow = Config.Bind("AI", "RecoverWindow", 12f, new ConfigDescription(
                "Recoveries closer together than this count as repeated attempts, s", new AcceptableValueRange<float>(1f, 60f)));
            AiMaxRecovers = Config.Bind("AI", "MaxRecovers", 4f, new ConfigDescription(
                "After this many repeated recoveries the car gives up for WaitSeconds", new AcceptableValueRange<float>(1f, 20f)));
            AiWaitSeconds = Config.Bind("AI", "WaitSeconds", 4f, new ConfigDescription(
                "How long a car that gave up sits still before trying again, s", new AcceptableValueRange<float>(1f, 60f)));
            AiFeelerRange = Config.Bind("AI", "FeelerRange", 10f, new ConfigDescription(
                "Base length of the centre obstacle feeler ray, m (side rays are shorter)", new AcceptableValueRange<float>(2f, 40f)));
            AiFeelerSpeedFactor = Config.Bind("AI", "FeelerSpeedFactor", 0.6f, new ConfigDescription(
                "Extra feeler length per m/s of speed", new AcceptableValueRange<float>(0f, 2f)));
            AiFrontOffset = Config.Bind("AI", "FrontOffset", 2f, new ConfigDescription(
                "Feelers start this far in front of the car's centre of mass, m (should be just outside the bumper)",
                new AcceptableValueRange<float>(0f, 5f)));
            AiMaxSlopeDeg = Config.Bind("AI", "MaxSlopeDeg", 35f, new ConfigDescription(
                "Surfaces flatter than this are driven over, steeper ones are obstacles (rocks, walls), degrees",
                new AcceptableValueRange<float>(10f, 80f)));
            AiAvoidGain = Config.Bind("AI", "AvoidGain", 1.2f, new ConfigDescription(
                "How hard the feelers steer away from obstacles", new AcceptableValueRange<float>(0f, 4f)));
            AiIgnoreMassBelow = Config.Bind("AI", "IgnoreMassBelow", 40f, new ConfigDescription(
                "Loose physics objects lighter than this (kg) are not obstacles; the car drives through them",
                new AcceptableValueRange<float>(0f, 1000f)));
            AiGiveUpDistance = Config.Bind("AI", "GiveUpDistance", 250f, new ConfigDescription(
                "Beyond this distance from the player the car stops chasing and coasts until the player comes closer, m",
                new AcceptableValueRange<float>(20f, 2000f)));
            AiInvertSteering = Config.Bind("AI", "InvertSteering", false,
                "Flip the steering sign if the car turns away from the target instead of toward it");

            Passenger = Config.Bind("Passenger", "Passenger", "Flexa",
                "Who sits in the front passenger seat; Boltjaw/Flexa/Lugnut/Scrud/Sprokka shoot, Scraffa/Spanna remain passive; empty = nobody");
            PassengerBailChance = Config.Bind("Passenger", "BailChance", 25f, new ConfigDescription(
                "% chance that a passenger who outlives the driver gets out of the car (and fights on foot with its remaining health) " +
                "once the car stands still; otherwise it climbs over and takes the wheel. If the player took the car first it always gets out",
                new AcceptableValueRange<float>(0f, 100f)));
            PassengerTakeoverSeconds = Config.Bind("Passenger", "TakeoverSeconds", 2f, new ConfigDescription(
                "A passenger that takes the wheel drives off this long after climbing over, s", new AcceptableValueRange<float>(0f, 60f)));
            EjectDistance = Config.Bind("Passenger", "EjectDistance", 1.8f, new ConfigDescription(
                "Before a passenger takes the wheel, the dead driver's carcass is thrown out to the left; the passenger climbs over once it is this far " +
                "from the seat (if it is not after 3 s, it is put down there), m", new AcceptableValueRange<float>(0.1f, 15f)));
            EjectSpeed = Config.Bind("Passenger", "EjectSpeed", 4f, new ConfigDescription(
                "How hard the carcass is thrown out, m/s sideways (plus half of that upwards)", new AcceptableValueRange<float>(0.5f, 20f)));
            BailDistance = Config.Bind("Passenger", "BailDistance", 1.8f, new ConfigDescription(
                "An occupant that gets out of the car appears this far beside its seat (passenger on its side, driver on the left), m",
                new AcceptableValueRange<float>(0.1f, 15f)));
            Config.Remove(new ConfigDefinition("Passenger", "ExitDistance"));
            StuckPedalTakeoverSeconds = Config.Bind("Passenger", "StuckPedalTakeoverSeconds", 6f, new ConfigDescription(
                "With a dead driver's foot stuck on the gas, a live passenger kicks it off after this long so the car can stop, s",
                new AcceptableValueRange<float>(0f, 120f)));
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
            AiOverlay = Config.Bind("Debug", "AiOverlay", false, "On-screen line per AI car: state, speed, target angle, steering, feeler distances");

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Patrol.ResetForScene(); PlayerRef.Reset(); PatrolPersistence.ResetForScene(); };
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
