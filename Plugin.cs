using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        public const string VERSION = "0.23.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<Key> MenuKey;
        internal static readonly Dictionary<string, ConfigEntry<float>> LootChances = new Dictionary<string, ConfigEntry<float>>(StringComparer.OrdinalIgnoreCase);
        internal static ConfigEntry<float> MinPartHealth, MaxPartHealth, LootMultiplier;

        internal static float LootChance(string key)
        {
            ConfigEntry<float> e;
            return key != null && LootChances.TryGetValue(key, out e) ? e.Value : 0f;
        }

        // Condition of a spawned part: min..max, weighted toward two thirds of the way up (triangular distribution)
        internal static float RollPartHealth()
        {
            float lo = Mathf.Min(MinPartHealth.Value, MaxPartHealth.Value), hi = Mathf.Max(MinPartHealth.Value, MaxPartHealth.Value);
            if (hi - lo < 0.01f) return lo;
            float mode = lo + (hi - lo) * 2f / 3f;
            float u = UnityEngine.Random.value, f = (mode - lo) / (hi - lo);
            return u < f ? lo + Mathf.Sqrt(u * (hi - lo) * (mode - lo)) : hi - Mathf.Sqrt((1f - u) * (hi - lo) * (hi - mode));
        }

        // [Combat]
        internal static ConfigEntry<bool> RangedCombat;
        internal static ConfigEntry<float> FireArc, MaxAimPitch, AimTurnSpeed, FireBurstSeconds, FireIntervalMin, FireIntervalMax, ShootDistance;
        internal static ConfigEntry<bool> RamDamage, RamDamageInCar;
        internal static ConfigEntry<float> RamDamageMultiplier, RamFullSpeedKmh, RamInCarFactor, RamPushStrength;
        internal static ConfigEntry<string> RamDamageByBody;
        // [Driving]
        internal static ConfigEntry<float> StuckPedalChance, StuckPedalTakeoverSeconds, BailChance, StuckBailChance;
        // [AI]
        internal static ConfigEntry<bool> AiInvertSteering, AiOverlay;
        internal static ConfigEntry<float> AiDriveByOffset;
        internal static ConfigEntry<float> AiThrottle, AiLeadTime, AiCommitSeconds, AiSteerRate, AiSteerAngle, AiMaxSteerAtSpeed, AiTurnSafeSpeed,
            AiRamDistance, AiPassWidth, AiRunOutMeters, AiRunOutMaxSeconds, AiReverseSeconds, AiReverseThrottle, AiStuckSeconds,
            AiRecoverWindow, AiMaxRecovers, AiWaitSeconds, AiFeelerRange, AiFeelerSpeedFactor, AiFrontOffset, AiMaxSlopeDeg, AiAvoidGain,
            AiIgnoreMassBelow, AiGiveUpDistance;
        // [Debug]
        internal static ConfigEntry<bool> VerboseLog;
        // [Convoy spawner]
        internal static ConfigEntry<bool> ConvoyEnabled;
        internal static ConfigEntry<float> MaxHeat, HeatIntervalKm, ConvoySpawnDistance, JustCarsToConvoyRatio, MinConvoyCooldown, MaxConvoyCooldown;
        internal static ConfigEntry<float> BasicCarsChance, AdvancedCarsChance, SuperCarsChance, BasicConvoyChance, AdvancedConvoyChance;
        internal static ConfigEntry<float> BasicCarsKm, AdvancedCarsKm, SuperCarsKm, BasicConvoyKm, AdvancedConvoyKm;
        internal static ConfigEntry<int> BasicCarsBosses, AdvancedCarsBosses, SuperCarsBosses, BasicConvoyBosses, AdvancedConvoyBosses;
        // hidden pose settings (PoseConfiguration = true exposes them)
        internal static PoseFloat DriverOffsetX, DriverOffsetY, DriverOffsetZ;
        internal static PoseBool PoseEnabled;
        internal static PoseFloat PoseThigh, PoseKnee, PoseArm, PoseElbow, PoseLeftLegCloser, PoseRightLegCloser, PoseLeftArmCloser, PoseRightArmCloser;

        // fixed values that used to be settings
        internal const float SpawnDistance = 8f;          // m in front of the player (template spawner)
        internal const float TakeoverSeconds = 2f;        // a promoted passenger drives off this long after climbing over
        internal const float EjectDistance = 0.3f;        // the carcass must be this far from the seat before the passenger climbs over
        internal const float EjectSpeed = 4f;             // m/s sideways (+ half of it up) given to the thrown carcass
        internal const float BailDistance = 0.5f;         // an occupant getting out appears this far beside its seat

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

            RangedCombat = Config.Bind("Combat", "RangedCombat", true,
                "Ranged humans (Boltjaw/Flexa/Lugnut/Scrud/Sprokka) in a car use their vanilla targeting and ranged attack: the passenger whenever a target " +
                "is in the fire arc, the driver in bursts. Off = everybody just rides along");
            ShootDistance = Config.Bind("Combat", "ShootDistance", 40f, new ConfigDescription(
                "Occupants only shoot at a target closer than this, m", new AcceptableValueRange<float>(1f, 200f)));
            FireArc = Config.Bind("Combat", "FireArcHalfAngle", 100f, new ConfigDescription(
                "Occupants may fire this many degrees left or right of the car's forward direction", new AcceptableValueRange<float>(0f, 180f)));
            MaxAimPitch = Config.Bind("Combat", "MaxAimPitch", 35f, new ConfigDescription(
                "Maximum upper-body aim angle up or down (degrees)", new AcceptableValueRange<float>(0f, 80f)));
            AimTurnSpeed = Config.Bind("Combat", "AimTurnSpeed", 180f, new ConfigDescription(
                "How quickly an occupant turns its upper body toward or away from a target (degrees/second)", new AcceptableValueRange<float>(1f, 720f)));
            FireBurstSeconds = Config.Bind("Combat", "FireBurstSeconds", 3f, new ConfigDescription(
                "How long one of the driver's bursts lasts (shooting pose, vanilla Attack inside the fire arc), s", new AcceptableValueRange<float>(0.5f, 30f)));
            FireIntervalMin = Config.Bind("Combat", "FireIntervalMin", 8f, new ConfigDescription(
                "Shortest pause between two bursts of the driver, s", new AcceptableValueRange<float>(0f, 120f)));
            FireIntervalMax = Config.Bind("Combat", "FireIntervalMax", 20f, new ConfigDescription(
                "Longest pause between two bursts of the driver, s", new AcceptableValueRange<float>(0f, 300f)));
            RamDamage = Config.Bind("Combat", "RamDamage", true,
                "An AI car that hits you (on foot or in your car) hurts you. The game's own bumper damage only works against creatures, not the player");
            RamDamageMultiplier = Config.Bind("Combat", "RamDamageMultiplier", 1f, new ConfigDescription(
                "Ram damage scale: at 1 a full-speed hit takes 30 health with a small car, 50 with a Junker, 70 with a truck (RamDamageByBody)",
                new AcceptableValueRange<float>(0f, 3f)));
            RamDamageByBody = Config.Bind("Combat", "RamDamageByBody", "Junker=50, Rust*=70, Scrapwagon=70, PigPen=50",
                "Full-speed damage per car body, 'Body=damage' pairs; * = prefix. Bodies not listed take " + Ram.DefaultDamage + ". Applied before the multiplier");
            RamFullSpeedKmh = Config.Bind("Combat", "RamFullSpeedKmh", 30f, new ConfigDescription(
                "Impact speed (km/h, relative) for the full damage. At half that speed the hit does half damage; below half it does nothing",
                new AcceptableValueRange<float>(5f, 200f)));
            RamPushStrength = Config.Bind("Combat", "RamPushStrength", 1f, new ConfigDescription(
                "A damaging hit also shoves you (on foot) in the car's direction: 1 = about the impact speed plus a hop; 0 = off",
                new AcceptableValueRange<float>(0f, 4f)));
            RamDamageInCar = Config.Bind("Combat", "RamDamageInCar", false,
                "Ram damage also while you sit in your own car (the game's own CrashDamage still applies by your speed). Off = only on foot");
            RamInCarFactor = Config.Bind("Combat", "RamInCarFactor", 1f, new ConfigDescription(
                "With RamDamageInCar: damage factor while you sit in your own car (1 = same as on foot)", new AcceptableValueRange<float>(0f, 1f)));

            StuckPedalChance = Config.Bind("Driving", "StuckPedalChance", 5f, new ConfigDescription(
                "% chance that a killed driver's gas pedal stays stuck; otherwise the gas is released and the car rolls to a stop",
                new AcceptableValueRange<float>(0f, 100f)));
            StuckPedalTakeoverSeconds = Config.Bind("Driving", "StuckPedalTakeoverSeconds", 4f, new ConfigDescription(
                "With a dead driver's foot stuck on the gas, a live passenger kicks it off after this long so the car can stop, s",
                new AcceptableValueRange<float>(0f, 120f)));
            BailChance = Config.Bind("Driving", "BailChance", 25f, new ConfigDescription(
                "% chance that a passenger who outlives the driver gets out and fights on foot once the car stands still; otherwise it takes the wheel. " +
                "If the player took the car first it always gets out", new AcceptableValueRange<float>(0f, 100f)));
            StuckBailChance = Config.Bind("Driving", "StuckBailChance", 50f, new ConfigDescription(
                "% chance that, when the driving AI gives up on a stuck car (after [AI] MaxRecovers), the whole crew gets out and fights on foot " +
                "instead of the car waiting WaitSeconds and trying again", new AcceptableValueRange<float>(0f, 100f)));

            AiThrottle = Config.Bind("AI", "Throttle", 1f, new ConfigDescription(
                "Maximum throttle the driving AI uses (0..1); also the throttle of a stuck pedal", new AcceptableValueRange<float>(0.05f, 1f)));
            AiDriveByOffset = Config.Bind("AI", "DriveByOffset", 5f, new ConfigDescription(
                "When the target may not be rammed (the template's ramsTargets), the car aims this far beside it and drives past instead, m",
                new AcceptableValueRange<float>(1f, 20f)));
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
                "Throttle used while reversing", new AcceptableValueRange<float>(0.1f, 1f)));
            AiStuckSeconds = Config.Bind("AI", "StuckSeconds", 2f, new ConfigDescription(
                "Not moving for this long while trying to drive forward = stuck, reverse out", new AcceptableValueRange<float>(0.5f, 10f)));
            AiRecoverWindow = Config.Bind("AI", "RecoverWindow", 12f, new ConfigDescription(
                "Recoveries closer together than this count as repeated attempts, s", new AcceptableValueRange<float>(1f, 60f)));
            AiMaxRecovers = Config.Bind("AI", "MaxRecovers", 4f, new ConfigDescription(
                "After this many repeated recoveries the car gives up: the crew bails ([Driving] StuckBailChance) or the car waits WaitSeconds",
                new AcceptableValueRange<float>(1f, 20f)));
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
            AiGiveUpDistance = Config.Bind("AI", "GiveUpDistance", 500f, new ConfigDescription(
                "Beyond this distance from the player the car stops chasing and coasts until the player comes closer, m",
                new AcceptableValueRange<float>(20f, 2000f)));
            AiInvertSteering = Config.Bind("AI", "InvertSteering", false,
                "Flip the steering sign if the car turns away from the target instead of toward it");

            LootMultiplier = Config.Bind("Loot", "Multiplier", 1f, new ConfigDescription(
                "Scales the amount of loot in a truck: 0 = nothing, 1 = the built-in amounts (Food dogfood x6; Water / Gasoline / Diesel cans x4, " +
                "50 % a barrel too; Medicine bandages x4 + first aid x2; Weapons 0-3 guns + 3-8 ammo boxes; Drugs alcohol x2 + weed x3 + weed plant x1; " +
                "Mechanic 3 repair boxes + 1 big oil can; Corpses 3-5 dead Scraffa; Rats 6-8 dead rats), 3 = 300 %. The 50 % barrels are not scaled",
                new AcceptableValueRange<float>(0f, 3f)));
            foreach (var d in CarTemplate.LootDefaults)
                LootChances[d[0]] = Config.Bind("Loot", d[0] + "Chance", float.Parse(d[2]), new ConfigDescription(
                    "% chance that a loot truck carries " + d[0] + " (all XChance values should add up to 100)", new AcceptableValueRange<float>(0f, 100f)));
            MinPartHealth = Config.Bind("Loot", "MinPartHealth", 2f, new ConfigDescription(
                "Lowest condition (%) of a spawned car's parts that have one (engine, radiator, wheels)", new AcceptableValueRange<float>(0f, 100f)));
            MaxPartHealth = Config.Bind("Loot", "MaxPartHealth", 35f, new ConfigDescription(
                "Highest condition (%) of a spawned car's parts; the roll is weighted toward two thirds of the way from Min to Max",
                new AcceptableValueRange<float>(0f, 100f)));

            var offsetRange = new AcceptableValueRange<float>(-2f, 2f);
            DriverOffsetX = PoseFloat.Create(Config, "Pose", "OffsetX", 0f, "Where each occupant's hips go: offset from its seat anchor, right (m)", offsetRange, exposePose);
            DriverOffsetY = PoseFloat.Create(Config, "Pose", "OffsetY", -0.3f, "Where each occupant's hips go: offset from its seat anchor, up (m)", offsetRange, exposePose);
            DriverOffsetZ = PoseFloat.Create(Config, "Pose", "OffsetZ", 0f, "Where each occupant's hips go: offset from its seat anchor, forward (m)", offsetRange, exposePose);
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

            const string CS = "Convoy spawner";
            ConvoyEnabled = Config.Bind(CS, "Enabled", true, "Enemy cars and convoys spawn on their own while you play (the Debug menu buttons work regardless)");
            MaxHeat = Config.Bind(CS, "MaxHeat", 3f, new ConfigDescription(
                "Upper limit of the heat (3 = 300 %). Heat scales the number of cars in every spawn and the chances of the tougher spawn types, " +
                "and shortens the cooldown a little", new AcceptableValueRange<float>(0f, 5f)));
            HeatIntervalKm = Config.Bind(CS, "HeatIntervalKm", 10f, new ConfigDescription(
                "Every this many km of the game's Distance Travelled add 25 % heat (linear: 10 = 100 % at 40 km)", new AcceptableValueRange<float>(1f, 200f)));
            ConvoySpawnDistance = Config.Bind(CS, "SpawnDistance", 300f, new ConfigDescription(
                "How far away a spawn appears (m): ahead of your car, up to 45 degrees left or right; behind you when on foot", new AcceptableValueRange<float>(50f, 1000f)));
            JustCarsToConvoyRatio = Config.Bind(CS, "JustCarsToConvoyRatio", 0.8f, new ConfigDescription(
                "When a convoy is allowed, how likely plain enemy cars spawn instead of it (0 = always the convoy, 1 = never)", new AcceptableValueRange<float>(0f, 1f)));
            MinConvoyCooldown = Config.Bind(CS, "MinConvoyCooldown", 5f, new ConfigDescription(
                "Shortest time between two spawns (minutes; 0 = can follow immediately)", new AcceptableValueRange<float>(0f, 120f)));
            MaxConvoyCooldown = Config.Bind(CS, "MaxConvoyCooldown", 60f, new ConfigDescription(
                "Longest time between two spawns (minutes; 0 = automatic spawning off)", new AcceptableValueRange<float>(0f, 240f)));

            BasicCarsKm = Config.Bind(CS, "BasicCarsDistanceKm", 0f, new ConfigDescription("Basic enemy cars (3 small cars at 100 % heat) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            BasicCarsBosses = Config.Bind(CS, "BasicCarsBossesKilled", 0, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            BasicCarsChance = Config.Bind(CS, "BasicCarsChance", 45f, new ConfigDescription("Weight of basic enemy cars among the allowed car spawns (%)", new AcceptableValueRange<float>(0f, 100f)));
            AdvancedCarsKm = Config.Bind(CS, "AdvancedCarsDistanceKm", 30f, new ConfigDescription("Advanced enemy cars (3 cars, at least 1 advanced, maybe a junker) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            AdvancedCarsBosses = Config.Bind(CS, "AdvancedCarsBossesKilled", 1, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            AdvancedCarsChance = Config.Bind(CS, "AdvancedCarsChance", 35f, new ConfigDescription("Weight of advanced enemy cars (%, multiplied by the heat)", new AcceptableValueRange<float>(0f, 100f)));
            SuperCarsKm = Config.Bind(CS, "SuperAdvancedCarsDistanceKm", 50f, new ConfigDescription("Super advanced enemy cars (5 cars, half junkers, at least 2 advanced) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            SuperCarsBosses = Config.Bind(CS, "SuperAdvancedCarsBossesKilled", 3, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            SuperCarsChance = Config.Bind(CS, "SuperAdvancedCarsChance", 20f, new ConfigDescription("Weight of super advanced enemy cars (%, multiplied by the heat)", new AcceptableValueRange<float>(0f, 100f)));
            BasicConvoyKm = Config.Bind(CS, "BasicConvoyDistanceKm", 5f, new ConfigDescription("Basic convoy (a basic truck + 2 junkers + 3-5 small cars) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            BasicConvoyBosses = Config.Bind(CS, "BasicConvoyBossesKilled", 0, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            BasicConvoyChance = Config.Bind(CS, "BasicConvoyChance", 70f, new ConfigDescription("Weight of the basic convoy among the allowed convoys (%)", new AcceptableValueRange<float>(0f, 100f)));
            AdvancedConvoyKm = Config.Bind(CS, "AdvancedConvoyDistanceKm", 20f, new ConfigDescription("Advanced convoy (an advanced truck + the same escort, at least 2 advanced) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            AdvancedConvoyBosses = Config.Bind(CS, "AdvancedConvoyBossesKilled", 3, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            AdvancedConvoyChance = Config.Bind(CS, "AdvancedConvoyChance", 30f, new ConfigDescription("Weight of the advanced convoy (%, multiplied by the heat)", new AcceptableValueRange<float>(0f, 100f)));

            MenuKey = Config.Bind("Debug", "TemplateSpawnerKey", Key.F8, "Open the template spawner: a list of the park, click a car to build it in front of you. None = off");
            VerboseLog = Config.Bind("Debug", "VerboseLog", true, "Log every build step (prefab lookups, hinge states, engine state) and the AI's state changes");
            AiOverlay = Config.Bind("Debug", "AiOverlay", false, "On-screen line per AI car: state, speed, target angle, steering, feeler distances");

            PurgeStaleEntries();

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Patrol.ResetForScene(); PlayerRef.Reset(); PatrolPersistence.ResetForScene(); Convoy.ResetForScene(); };
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }

        // Settings from earlier versions ([Build], [Driver], [Passenger], old keys) stay in the file as orphaned lines and would
        // clutter the Apocasetter menu: drop every orphan except the PoseConfiguration switch, then save.
        private void PurgeStaleEntries()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var orphans = prop != null ? prop.GetValue(Config, null) as Dictionary<ConfigDefinition, string> : null;
                if (orphans == null) return;
                var stale = new List<ConfigDefinition>();
                foreach (var def in orphans.Keys)
                    if (!string.Equals(def.Key, "PoseConfiguration", StringComparison.OrdinalIgnoreCase)) stale.Add(def);
                foreach (var def in stale) orphans.Remove(def);
                if (stale.Count > 0) { Config.Save(); Log.LogInfo("Config: removed " + stale.Count + " stale setting(s) from earlier versions"); }
            }
            catch (Exception e) { Log.LogWarning("Config cleanup: " + e.Message); }
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
            _runner.AddComponent<TemplateMenu>();
            _runner.AddComponent<Convoy>();
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
