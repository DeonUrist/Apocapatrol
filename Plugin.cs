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
    // AI-driven raider cars: templates assembled from the game's own frames and parts, live crews with a driving AI, ranged
    // passengers, ram damage, loot trucks, a convoy spawner driven by the game's Distance Travelled, and a save sidecar.
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.denis.apocalypter.motorcyclemod", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocapatrol";
        public const string NAME = "Apocapatrol";
        public const string VERSION = "2.4.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<Key> MenuKey;
        internal static ConfigEntry<bool> AllowDebugSpawns;
        internal static ConfigEntry<float> PatrolSpawnChancePercent;
        internal static ConfigEntry<float> MinPartHealth, MaxPartHealth, MinPartsFill, MaxPartsFill;

        // Condition of a spawned part: min..max, weighted toward two thirds of the way up (triangular distribution)
        internal static float RollPartHealth() { return Triangular(MinPartHealth.Value, MaxPartHealth.Value); }
        internal static float RollPartFill() { return Triangular(MinPartsFill.Value, MaxPartsFill.Value); }

        // random between a and b, weighted toward two thirds of the way up
        internal static float Triangular(float a, float b)
        {
            float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            if (hi - lo < 0.01f) return lo;
            float mode = lo + (hi - lo) * 2f / 3f;
            float u = UnityEngine.Random.value, f = (mode - lo) / (hi - lo);
            return u < f ? lo + Mathf.Sqrt(u * (hi - lo) * (mode - lo)) : hi - Mathf.Sqrt((1f - u) * (hi - lo) * (hi - mode));
        }

        // [Combat]
        internal static ConfigEntry<bool> RangedCombat;
        internal static ConfigEntry<int> AudioVoices;
        internal static ConfigEntry<float> FireArc, MaxAimPitch, AimTurnSpeed, FireBurstSeconds, FireIntervalMin, FireIntervalMax, ShootDistance;
        internal static ConfigEntry<bool> RamDamage, RamDamageInCar;
        internal static ConfigEntry<float> RamDamageMultiplier, RamFullSpeedKmh, RamInCarFactor, RamPushStrength;
        internal static ConfigEntry<string> RamDamageByBody;
        // [Driving]
        internal static ConfigEntry<float> StuckPedalChance, StuckPedalTakeoverSeconds, BailChance, StuckBailChance;
        // [Self-destruct]
        internal static ConfigEntry<bool> SelfDestruct;
        internal static ConfigEntry<float> ExplodedLootPercent;
        // [AI]
        internal static ConfigEntry<bool> AiInvertSteering, AiOverlay, CustomPaintjobs;
        internal static ConfigEntry<bool> SpawnWarning, RaiderMusic, RiderLances, HideFsmWarnings, TurretSpotEditor, FuryRoad, SuspensionLiftCenterOfMass, StandstillDamping;
        internal static ConfigEntry<float> RiderRange, RiderIntervalMin, RiderIntervalMax, RiderLanceSpeed, RiderMaxThrowAngle, RiderJumpRange, WitnessMeChance, RiderLeapSpeed, RiderBlastDamage, RiderBlastRadius, TurretThigh, TurretKnee, TurretHipHeight, RiderLeapReachPercent, RiderBailRange;
        internal static ConfigEntry<string> RiderLanceOffset, RiderLanceRotation;
        internal static ConfigEntry<float> AiDriveByOffset;
        internal static ConfigEntry<float> AiThrottle, AiLeadTime, AiCommitSeconds, AiSteerRate, AiSteerAngle, AiMaxSteerAtSpeed, AiTurnSafeSpeed,
            AiRamDistance, AiPassWidth, AiRunOutMeters, AiRunOutMaxSeconds, AiReverseSeconds, AiReverseThrottle, AiStuckSeconds,
            AiRecoverWindow, AiMaxRecovers, AiWaitSeconds, AiFeelerRange, AiFeelerSpeedFactor, AiFrontOffset, AiMaxSlopeDeg, AiAvoidGain,
            AiIgnoreMassBelow, AiGiveUpDistance;
        // [Debug]
        internal static ConfigEntry<bool> VerboseLog;
        internal static ConfigEntry<float> ExitSpeedKmh;
        // [Cleanup]
        internal static ConfigEntry<bool> CleanupEnabled;
        internal static ConfigEntry<float> CleanupMinutes, CleanupDistance;
        internal static ConfigEntry<int> CleanupMaxCars;
        // [Convoy spawner]
        internal static ConfigEntry<bool> ConvoyEnabled;
        internal static ConfigEntry<float> MaxHeat, HeatIntervalKm, ConvoySpawnDistance, MinConvoyCooldown, MaxConvoyCooldown;
        // hidden pose settings (PoseConfiguration = true exposes them)
        internal static PoseFloat DriverOffsetX, DriverOffsetY, DriverOffsetZ;
        internal static PoseBool PoseEnabled;
        internal static PoseFloat PoseThigh, PoseKnee, PoseArm, PoseElbow, PoseLeftLegCloser, PoseRightLegCloser, PoseLeftArmCloser, PoseRightArmCloser, PoseBikeArm, PoseBikeArmSpread;

        // fixed values that used to be settings
        internal const float SpawnDistance = 8f;          // m in front of the player (template spawner)
        internal const float TakeoverSeconds = 2f;        // a promoted passenger drives off this long after climbing over
        internal const float EjectDistance = 0.3f;        // the carcass must be this far from the seat before the passenger climbs over
        internal const float EjectSpeed = 4f;             // m/s sideways (+ half of it up) given to the thrown carcass
        internal const float BailDistance = 0.5f;         // an occupant getting out appears this far beside its seat

        internal static readonly Dictionary<string, ArmPoseProfile> HumanArmPoses = new Dictionary<string, ArmPoseProfile>(StringComparer.OrdinalIgnoreCase);
        internal static readonly string[] HumanTypes = { "Boltjaw", "Flexa", "Lugnut", "Scrud", "Sprokka", "Scraffa", "Spanna" };

        private static GameObject _runner;

        // true = every setting in the .cfg and in Apocasetter again (see the list in Awake); false = only [Scaling], [Combat], [Debug]
        private const bool ExposeAllSettings = false;

        private static ConfigFile HiddenConfig()
        {
            // never written: SaveOnConfigSet off and no Save() call - the entries only carry their default values
            return new ConfigFile(Path.Combine(Paths.CachePath, "Apocapatrol.hidden.cfg"), false) { SaveOnConfigSet = false };
        }

        // 1.8.0 moved four settings into [Scaling]: carry a player's old values over (from the orphaned lines of the old sections)
        // before PurgeStaleEntries drops those lines. Only when the new line is not in the file yet.
        private void MigrateMovedSettings()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var orphans = prop != null ? prop.GetValue(Config, null) as Dictionary<ConfigDefinition, string> : null;
                if (orphans == null || orphans.Count == 0) return;
                var moves = new List<KeyValuePair<ConfigDefinition, ConfigEntryBase>>
                {
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("General", "AudioVoices"), AudioVoices),
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("Self-destruct", "SelfDestructingCars"), SelfDestruct),
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("Scaling", "SelfDestruct"), SelfDestruct),   // 2.2.3: -> [General]
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("Convoy spawner", "Enabled"), ConvoyEnabled),
                };
                int n = 0;
                foreach (var mv in moves)
                {
                    string old;
                    if (mv.Value == null || !orphans.TryGetValue(mv.Key, out old)) continue;
                    if (!Equals(mv.Value.BoxedValue, mv.Value.DefaultValue)) continue;   // the player already set the new one
                    mv.Value.SetSerializedValue(old);
                    n++;
                }
                if (n > 0) Log.LogInfo("Config: carried " + n + " moved setting(s) over");
            }
            catch (Exception e) { Log.LogWarning("Config migration: " + e.Message); }
        }

        internal static ConfigEntry<Key> BindEditorConfigKey(ConfigFile config)
        {
            var definition = new ConfigDefinition("Debug", "ApocaPatrol Config Key");
            bool alreadyPresent = false;
            foreach (var key in config.Keys) if (key.Equals(definition)) alreadyPresent = true;
            var property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var orphaned = property != null ? property.GetValue(config, null) as Dictionary<ConfigDefinition, string> : null;
            alreadyPresent |= orphaned != null && orphaned.ContainsKey(definition);
            bool save = config.SaveOnConfigSet; config.SaveOnConfigSet = false;
            try
            {
                var oldDefinition = new ConfigDefinition("Debug", "TemplateSpawnerKey");
                Key old = config.Bind(oldDefinition, Key.None).Value;
                config.Remove(oldDefinition);
                var entry = config.Bind(definition, Key.None, new ConfigDescription("Open the ApocaPatrol Patrols, Convoys and Loot configuration window. None = unbound. Existing editor bindings are preserved"));
                if (!alreadyPresent) entry.Value = old;
                return entry;
            }
            finally { config.SaveOnConfigSet = save; }
        }

        private void Awake()
        {
            Log = Logger;
            try { QuietFsm.Install(); } catch (Exception e) { Log.LogWarning("QuietFsm: " + e.Message); }
            try { Suspension.Install(); } catch (Exception e) { Log.LogWarning("Suspension: " + e.Message); }
            try { MotorcycleIntegration.Install(); }
            catch (Exception e) { Log.LogError("Motorcycle save hook not installed (saved motorcycles may not load): " + e); }
            Paint.Init(Info.Location);
            PatrolSkin.Init(Info.Location);
            var fixedSettings = HiddenConfig();
            EditorStore.Load(Path.GetDirectoryName(Info.Location));
            CarTemplates.Init(Info.Location);
            bool exposePose = PoseConfigurationEnabled(Config.ConfigFilePath);

            // ---- [General], top to bottom as the Mods window lists them (2.2.3): on/off, Fury Road, self-destruct, then the rest
            ConvoyEnabled = Config.Bind("General", "Enabled", true,
                "Raider patrols and convoys spawn on their own while you play. Off = no automatic spawns at all (the editor's test spawns still work)");
            FuryRoad = Config.Bind("General", "Fury Road", false,
                "Every patrol and convoy group can spawn from the start: their minimum distance travelled and minimum boss kills are ignored. " +
                "Off = groups unlock with distance and bosses as set in the editor");
            SelfDestruct = Config.Bind("General", "SelfDestruct", true,
                "A raider car that is fully vacated (crew dead, bailed out, or the last passenger got out beside a dead driver) explodes: the frame " +
                "goes black (trucks stay a lootable wreck), every part pops off with 0 condition and the dead chassis cannot be entered, " +
                "fuelled or fitted with parts any more; it is removed once you are 1000 m away. Off = vacated cars stay as they are now. " +
                "A car you have sat in never explodes");
            Config.Bind("General", "Apocasetter", true,
                "Show this mod in the Apocasetter Mods menu.\n" +
                "To expose seat offsets, shared pose settings and all per-human controls, uncomment the next line and restart:\n" +
                "PoseConfiguration = true");
            SpawnWarning = Config.Bind("General", "SpawnWarning", true,
                "When raiders spawn (an automatic patrol or convoy, or a test spawn from the editor) a red warning appears top left for a few " +
                "seconds: \"You hear a sound of distant engines\". Template spawns do not show it. Off = no warning");
            RaiderMusic = Config.Bind("General", "Raider music", true,
                "A raider car built from a template that has a cassette in its radio plays it at full volume from the moment it spawns, " +
                "until you switch the radio off. Off = the cassette sits in the radio, silent, as in a parked car");

            // ---- the settings players see (Denis, 1.8.0): [Scaling], [Combat], [Debug]. Everything else is fixed at the defaults below.
            AudioVoices = Config.Bind("Scaling", "AudioVoices", 64, new ConfigDescription(
                "How many sounds the game can play at once (Unity's real voices; the game ships with 32). A firefight with several raider cars needs " +
                "more: every shot and every hit is a sound, and beyond the limit sounds - the player's own shots too - cut out. 64 is a good value; " +
                "0 leaves the game's own setting. Applied at game start (restart the game after a change)",
                new AcceptableValueList<int>(0, 32, 48, 64, 96, 128)));
            MinConvoyCooldown = Config.Bind("Scaling", "MinConvoyCooldown", 5f, new ConfigDescription(
                "Shortest time between two raider spawns (minutes; 0 = can follow immediately). Each wait is rolled between this and " +
                "MaxConvoyCooldown, a little shorter at high heat. A change applies from the next roll",
                new AcceptableValueRange<float>(0f, 120f)));
            MaxConvoyCooldown = Config.Bind("Scaling", "MaxConvoyCooldown", 60f, new ConfigDescription(
                "Longest time between two raider spawns (minutes; 0 = automatic spawning off)", new AcceptableValueRange<float>(0f, 240f)));

            RangedCombat = Config.Bind("Combat", "RangedCombat", true,
                "Ranged humans (Boltjaw/Flexa/Lugnut/Scrud/Sprokka) in a car use their vanilla targeting and ranged attack: the passenger whenever a target " +
                "is in the fire arc, the driver in bursts. Off = everybody just rides along");
            ShootDistance = Config.Bind("Combat", "ShootDistance", 40f, new ConfigDescription(
                "Occupants only shoot at a target closer than this, m", new AcceptableValueRange<float>(1f, 200f)));
            RamDamage = Config.Bind("Combat", "RamDamage", true,
                "An AI car that hits you (on foot or in your car) hurts you. The game's own bumper damage only works against creatures, not the player");
            RiderLances = Config.Bind("Combat", "HumanTurrets", true,
                "A template's human turrets fight: a Warboy crouching on the roof throws blast lances (and may leap at you: \"Witness me!\"), a gunner " +
                "shoots all around with its own gun. Off = they just ride along");
            WitnessMeChance = Config.Bind("Debug", "WitnessMeChance", 10f, new ConfigDescription(
                "TEST SETTING (will be removed): % chance that an unhurt blast-lance rider screams \"Witness me!\" and leaps at your car when you drive close " +
                "(a wounded rider always does). 100 = every time", new AcceptableValueRange<float>(0f, 100f)));
            TurretSpotEditor = Config.Bind("Debug", "TurretSpotEditor", false,
                "Glowing markers on the human turret spots of the nearest raider car and numpad keys to move them: 8/2 forward/back, 4/6 left/right, " +
                "7/9 down/up (Shift = x5), 0 = next turret, 5 = save the spot into the car's template file, . = markers on/off");
            RamDamageInCar = Config.Bind("Combat", "RamDamageInCar", false,
                "Ram damage also while you sit in your own car (the game's own CrashDamage still applies by your speed). Off = only on foot");

            MenuKey = BindEditorConfigKey(Config);
            AllowDebugSpawns = Config.Bind("Debug", "AllowDebugSpawns", false, "Show Spawn Template, S group buttons and SPAWN in the editor");
            TemplateExporter.Configure(Config);
            PatrolSpawnChancePercent = Config.Bind("Scaling", "PatrolSpawnChancePercent", 80f, new ConfigDescription("Global patrol/convoy distribution: 80 = 80 % patrols and 20 % convoys when both have eligible types", new AcceptableValueRange<float>(0f, 100f)));
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Log what the mod does: spawns, builds, crews, the AI's state changes, ram hits. Off = only the load line and warnings, nothing that gives a spawn away");
            AiOverlay = Config.Bind("Debug", "AiOverlay", false, "On-screen line per AI car: state, speed, target angle, steering, feeler distances. With no raider car driving: the time until the next spawn roll");
            CustomPaintjobs = Config.Bind("Debug", "CustomPaintjobs", true,
                "Raider cars wear the paint jobs from the mod's Textures folder (body textures, truck container sides and inside). " +
                "Off = they keep the game's own textures (cars spawned from then on; the textures are not loaded at game start)");

            // ---- hidden settings: bound to an in-memory ConfigFile that is never saved, so they keep their default values and do not
            // appear in the .cfg or the Apocasetter menu. To bring ALL of them back (with their old sections: Combat, Driving,
            // Self-destruct, AI, Loot, Convoy spawner, Cleanup, Debug), set ExposeAllSettings = true and rebuild. Hidden in 1.8.0:
            //   [Combat] FireArcHalfAngle 100, MaxAimPitch 35, AimTurnSpeed 180, FireBurstSeconds 3, FireIntervalMin 8, FireIntervalMax 20,
            //            RamDamageMultiplier 1, RamDamageByBody "Junker=50, Rust*=70, Scrapwagon=70, PigPen=50", RamFullSpeedKmh 30,
            //            RamPushStrength 1, RamInCarFactor 1
            //   [Driving] StuckPedalChance 5, StuckPedalTakeoverSeconds 4, BailChance 25, StuckBailChance 100 (was 50 before 1.8.4)
            //   [Self-destruct] CarPartsLootFromExplodedCars 12
            //   [AI] Throttle 1, DriveByOffset 5, LeadTime 1, CommitSeconds 0.5, SteerRate 1.5, SteerAngle 30, MaxSteerAtSpeed 0.35,
            //        TurnSafeSpeed 18, RamDistance 15, PassWidth 12, RunOutMeters 30, RunOutMaxSeconds 4, ReverseSeconds 2,
            //        ReverseThrottle 0.6, StuckSeconds 2, RecoverWindow 30 (was 12), MaxRecovers 2 (was 4), WaitSeconds 4, FeelerRange 10,
            //        FeelerSpeedFactor 0.6, FrontOffset 2, MaxSlopeDeg 35, AvoidGain 1.2, IgnoreMassBelow 40, GiveUpDistance 700,
            //        InvertSteering false
            //   [Cleanup] Enabled true, RemoveAfterMinutes 10 (was 40), MaxCars 30, MinDistance 800
            //   [Debug] ExitSpeedKmh 30
            var H = ExposeAllSettings ? Config : HiddenConfig();
                        
            
                                    FireArc = H.Bind("Combat", "FireArcHalfAngle", 100f, new ConfigDescription(
                "Occupants may fire this many degrees left or right of the car's forward direction", new AcceptableValueRange<float>(0f, 180f)));
            MaxAimPitch = H.Bind("Combat", "MaxAimPitch", 35f, new ConfigDescription(
                "Maximum upper-body aim angle up or down (degrees)", new AcceptableValueRange<float>(0f, 80f)));
            AimTurnSpeed = H.Bind("Combat", "AimTurnSpeed", 180f, new ConfigDescription(
                "How quickly an occupant turns its upper body toward or away from a target (degrees/second)", new AcceptableValueRange<float>(1f, 720f)));
            FireBurstSeconds = H.Bind("Combat", "FireBurstSeconds", 3f, new ConfigDescription(
                "How long one of the driver's bursts lasts (shooting pose, vanilla Attack inside the fire arc), s", new AcceptableValueRange<float>(0.5f, 30f)));
            FireIntervalMin = H.Bind("Combat", "FireIntervalMin", 8f, new ConfigDescription(
                "Shortest pause between two bursts of the driver, s", new AcceptableValueRange<float>(0f, 120f)));
            FireIntervalMax = H.Bind("Combat", "FireIntervalMax", 20f, new ConfigDescription(
                "Longest pause between two bursts of the driver, s", new AcceptableValueRange<float>(0f, 300f)));
                        RamDamageMultiplier = H.Bind("Combat", "RamDamageMultiplier", 1f, new ConfigDescription(
                "Ram damage scale: at 1 a full-speed hit takes 30 health with a small car, 50 with a Junker, 70 with a truck (RamDamageByBody)",
                new AcceptableValueRange<float>(0f, 3f)));
            RamDamageByBody = H.Bind("Combat", "RamDamageByBody", "Junker=50, Rust*=70, Scrapwagon=70, PigPen=50",
                "Full-speed damage per car body, 'Body=damage' pairs; * = prefix. Bodies not listed take " + Ram.DefaultDamage + ". Applied before the multiplier");
            RamFullSpeedKmh = H.Bind("Combat", "RamFullSpeedKmh", 30f, new ConfigDescription(
                "Impact speed (km/h, relative) for the full damage. At half that speed the hit does half damage; below half it does nothing",
                new AcceptableValueRange<float>(5f, 200f)));
            RamPushStrength = H.Bind("Combat", "RamPushStrength", 1f, new ConfigDescription(
                "A damaging hit also shoves you (on foot) in the car's direction: 1 = about the impact speed plus a hop; 0 = off",
                new AcceptableValueRange<float>(0f, 4f)));
                        RamInCarFactor = H.Bind("Combat", "RamInCarFactor", 1f, new ConfigDescription(
                "With RamDamageInCar: damage factor while you sit in your own car (1 = same as on foot)", new AcceptableValueRange<float>(0f, 1f)));

            RiderRange = H.Bind("Combat", "RiderRange", 35f, new ConfigDescription("A blast-lance rider throws at a target closer than this, m (and only when a flat throw reaches it)", new AcceptableValueRange<float>(5f, 150f)));
            RiderMaxThrowAngle = H.Bind("Combat", "RiderMaxThrowAngle", 20f, new ConfigDescription("The rider only throws when the lance reaches the predicted spot with at most this many degrees up (a throw, not a lob)", new AcceptableValueRange<float>(0f, 45f)));
            RiderJumpRange = H.Bind("Combat", "RiderJumpRange", 15f, new ConfigDescription("\"Witness me!\": the rider leaps when your car is closer than this, m", new AcceptableValueRange<float>(3f, 30f)));
            RiderIntervalMin = H.Bind("Combat", "RiderIntervalMin", 5f, new ConfigDescription("Shortest pause between two of a rider's throws, s", new AcceptableValueRange<float>(1f, 60f)));
            RiderIntervalMax = H.Bind("Combat", "RiderIntervalMax", 9f, new ConfigDescription("Longest pause between two of a rider's throws, s", new AcceptableValueRange<float>(1f, 120f)));
            RiderLanceOffset = H.Bind("Combat", "RiderLanceOffset", "0, 0, 0", "Warboy's lance prop: position in the throwing hand, hand-local metres x, y, z");
            RiderLanceRotation = H.Bind("Combat", "RiderLanceRotation", "0, 0, 0", "Warboy's lance prop: rotation in the throwing hand, degrees x, y, z");
            RiderLeapReachPercent = H.Bind("Combat", "RiderLeapReachPercent", 80f, new ConfigDescription("\"Witness me!\": the Warboy only leaps at a car within this % of his longest jump (RiderLeapSpeed x 1.2 s, the speeds of both cars counted)", new AcceptableValueRange<float>(10f, 100f)));
            RiderBailRange = H.Bind("Combat", "RiderBailRange", 6f, new ConfigDescription("A Warboy on a motorcycle who cannot turn to you jumps off when you are closer than this, m", new AcceptableValueRange<float>(1f, 30f)));
            SuspensionLiftCenterOfMass = H.Bind("Combat", "SuspensionLiftCenterOfMass", true, "A template's lifted suspension lowers the car's centre of mass by the lift (Part Adjustment uses the same rule)");
            StandstillDamping = H.Bind("Combat", "StandstillDamping", true, "A parked car (under 0.3 m/s, no engine torque) is held still, so a lifted chassis on heavy wheels does not shake in place (same rule as Part Adjustment)");
            RiderLeapSpeed = H.Bind("Combat", "RiderLeapSpeed", 11f, new ConfigDescription("\"Witness me!\": the leap's ground speed relative to the rider's car, m/s - a long, flat jump, no lob", new AcceptableValueRange<float>(5f, 40f)));
            RiderBlastDamage = H.Bind("Combat", "RiderBlastDamage", 60f, new ConfigDescription("\"Witness me!\": damage of the rider's blast at its centre (in your car too); falls to a third at the edge", new AcceptableValueRange<float>(0f, 300f)));
            RiderBlastRadius = H.Bind("Combat", "RiderBlastRadius", 7f, new ConfigDescription("\"Witness me!\": reach of the rider's blast, m (damage and the shove of your car)", new AcceptableValueRange<float>(1f, 20f)));
            TurretThigh = H.Bind("Combat", "TurretThigh", 80f, new ConfigDescription("Crouched human turret gunner: thigh angle forward, degrees", new AcceptableValueRange<float>(0f, 150f)));
            TurretKnee = H.Bind("Combat", "TurretKnee", 130f, new ConfigDescription("Crouched human turret gunner: knee bend, degrees", new AcceptableValueRange<float>(0f, 170f)));
            TurretHipHeight = H.Bind("Combat", "TurretHipHeight", 0.4f, new ConfigDescription("Crouched human turret gunner: hips above the spot (feet), m", new AcceptableValueRange<float>(0f, 1.2f)));
            RiderLanceSpeed = H.Bind("Combat", "RiderLanceSpeed", 30f, new ConfigDescription("Throw speed of a rider's lance, m/s (the player's is 30)", new AcceptableValueRange<float>(5f, 80f)));
            StuckPedalChance = H.Bind("Driving", "StuckPedalChance", 5f, new ConfigDescription(
                "% chance that a killed driver's gas pedal stays stuck; otherwise the gas is released and the car rolls to a stop",
                new AcceptableValueRange<float>(0f, 100f)));
            StuckPedalTakeoverSeconds = H.Bind("Driving", "StuckPedalTakeoverSeconds", 4f, new ConfigDescription(
                "With a dead driver's foot stuck on the gas, a live passenger kicks it off after this long so the car can stop, s",
                new AcceptableValueRange<float>(0f, 120f)));
            BailChance = H.Bind("Driving", "BailChance", 25f, new ConfigDescription(
                "% chance that a passenger who outlives the driver gets out and fights on foot once the car stands still; otherwise it takes the wheel. " +
                "If the player took the car first it always gets out", new AcceptableValueRange<float>(0f, 100f)));
            StuckBailChance = H.Bind("Driving", "StuckBailChance", 100f, new ConfigDescription(
                "% chance that, when the driving AI gives up on a stuck car (after [AI] MaxRecovers), the whole crew gets out and fights on foot " +
                "instead of the car waiting WaitSeconds and trying again", new AcceptableValueRange<float>(0f, 100f)));

                        ExplodedLootPercent = H.Bind("Self-destruct", "CarPartsLootFromExplodedCars", 12f, new ConfigDescription(
                "% of the popped-off parts that keep their condition instead of dropping to 0. Each part rolls 0-100 weighted toward the low " +
                "numbers and keeps its condition when the roll is at or below this value (12 = about one part in five, 50 = about 70 %, 100 = all)",
                new AcceptableValueRange<float>(0f, 100f)));

            AiThrottle = H.Bind("AI", "Throttle", 1f, new ConfigDescription(
                "Maximum throttle the driving AI uses (0..1); also the throttle of a stuck pedal", new AcceptableValueRange<float>(0.05f, 1f)));
            AiDriveByOffset = H.Bind("AI", "DriveByOffset", 5f, new ConfigDescription(
                "When the target may not be rammed (the template's ramsTargets), the car aims this far beside it and drives past instead, m",
                new AcceptableValueRange<float>(1f, 20f)));
            AiLeadTime = H.Bind("AI", "LeadTime", 1f, new ConfigDescription(
                "Aim this many seconds ahead of the target's movement (intercept), s", new AcceptableValueRange<float>(0f, 4f)));
            AiCommitSeconds = H.Bind("AI", "CommitSeconds", 0.5f, new ConfigDescription(
                "The aim point is re-taken only this often, so the car commits to a heading instead of twitching after the target, s",
                new AcceptableValueRange<float>(0.05f, 3f)));
            AiSteerRate = H.Bind("AI", "SteerRate", 1.5f, new ConfigDescription(
                "How fast the wheel turns: full-lock units per second (1.5 = straight to full lock in 0.67 s). Lower = lazier, wider turns",
                new AcceptableValueRange<float>(0.2f, 10f)));
            AiSteerAngle = H.Bind("AI", "SteerAngle", 30f, new ConfigDescription(
                "Angle to the aim point at which the driver asks for full lock, degrees (smaller = sharper corrections)",
                new AcceptableValueRange<float>(5f, 90f)));
            AiMaxSteerAtSpeed = H.Bind("AI", "MaxSteerAtSpeed", 0.35f, new ConfigDescription(
                "Steering limit at 72 km/h and above (0..1); full lock is allowed below 18 km/h, blended in between. Keeps the car on its wheels",
                new AcceptableValueRange<float>(0.05f, 1f)));
            AiTurnSafeSpeed = H.Bind("AI", "TurnSafeSpeed", 18f, new ConfigDescription(
                "Above this speed (m/s) the driver lifts off and brakes lightly when the target is more than 40 degrees off the nose",
                new AcceptableValueRange<float>(3f, 40f)));
            AiRamDistance = H.Bind("AI", "RamDistance", 15f, new ConfigDescription(
                "Within this distance of the target (m), obstacle avoidance is switched off: go straight for the ram",
                new AcceptableValueRange<float>(0f, 50f)));
            AiPassWidth = H.Bind("AI", "PassWidth", 12f, new ConfigDescription(
                "The target counts as passed when it is behind the car and within this many metres of the car's track",
                new AcceptableValueRange<float>(2f, 40f)));
            AiRunOutMeters = H.Bind("AI", "RunOutMeters", 30f, new ConfigDescription(
                "After passing or ramming the target the car keeps going this far before turning around, m",
                new AcceptableValueRange<float>(0f, 200f)));
            AiRunOutMaxSeconds = H.Bind("AI", "RunOutMaxSeconds", 4f, new ConfigDescription(
                "... or at most this long, s", new AcceptableValueRange<float>(0.5f, 20f)));
            AiReverseSeconds = H.Bind("AI", "ReverseSeconds", 2f, new ConfigDescription(
                "How long the car reverses after hitting an obstacle or getting stuck, s (grows with repeated attempts)",
                new AcceptableValueRange<float>(0.5f, 10f)));
            AiReverseThrottle = H.Bind("AI", "ReverseThrottle", 0.6f, new ConfigDescription(
                "Throttle used while reversing", new AcceptableValueRange<float>(0.1f, 1f)));
            AiStuckSeconds = H.Bind("AI", "StuckSeconds", 2f, new ConfigDescription(
                "Not moving for this long while trying to drive forward = stuck, reverse out", new AcceptableValueRange<float>(0.5f, 10f)));
            AiRecoverWindow = H.Bind("AI", "RecoverWindow", 30f, new ConfigDescription(
                "Recoveries closer together than this count as repeated attempts, s", new AcceptableValueRange<float>(1f, 60f)));
            AiMaxRecovers = H.Bind("AI", "MaxRecovers", 2f, new ConfigDescription(
                "After this many repeated recoveries the car gives up: the crew bails ([Driving] StuckBailChance) or the car waits WaitSeconds",
                new AcceptableValueRange<float>(1f, 20f)));
            AiWaitSeconds = H.Bind("AI", "WaitSeconds", 4f, new ConfigDescription(
                "How long a car that gave up sits still before trying again, s", new AcceptableValueRange<float>(1f, 60f)));
            AiFeelerRange = H.Bind("AI", "FeelerRange", 10f, new ConfigDescription(
                "Base length of the centre obstacle feeler ray, m (side rays are shorter)", new AcceptableValueRange<float>(2f, 40f)));
            AiFeelerSpeedFactor = H.Bind("AI", "FeelerSpeedFactor", 0.6f, new ConfigDescription(
                "Extra feeler length per m/s of speed", new AcceptableValueRange<float>(0f, 2f)));
            AiFrontOffset = H.Bind("AI", "FrontOffset", 2f, new ConfigDescription(
                "Feelers start this far in front of the car's centre of mass, m (should be just outside the bumper)",
                new AcceptableValueRange<float>(0f, 5f)));
            AiMaxSlopeDeg = H.Bind("AI", "MaxSlopeDeg", 35f, new ConfigDescription(
                "Surfaces flatter than this are driven over, steeper ones are obstacles (rocks, walls), degrees",
                new AcceptableValueRange<float>(10f, 80f)));
            AiAvoidGain = H.Bind("AI", "AvoidGain", 1.2f, new ConfigDescription(
                "How hard the feelers steer away from obstacles", new AcceptableValueRange<float>(0f, 4f)));
            AiIgnoreMassBelow = H.Bind("AI", "IgnoreMassBelow", 40f, new ConfigDescription(
                "Loose physics objects lighter than this (kg) are not obstacles; the car drives through them",
                new AcceptableValueRange<float>(0f, 1000f)));
            AiGiveUpDistance = H.Bind("AI", "GiveUpDistance", 700f, new ConfigDescription(
                "Beyond this distance from the player the car stops chasing and coasts until the player comes closer, m",
                new AcceptableValueRange<float>(20f, 2000f)));
            AiInvertSteering = H.Bind("AI", "InvertSteering", false,
                "Flip the steering sign if the car turns away from the target instead of toward it");

            MinPartHealth = H.Bind("Loot", "MinPartHealth", 2f, new ConfigDescription(
                "Lowest condition (%) of a spawned car's parts that have one (engine, radiator, wheels)", new AcceptableValueRange<float>(0f, 100f)));
            MaxPartHealth = H.Bind("Loot", "MaxPartHealth", 35f, new ConfigDescription(
                "Highest condition (%) of a spawned car's parts; the roll is weighted toward two thirds of the way from Min to Max",
                new AcceptableValueRange<float>(0f, 100f)));
            MinPartsFill = H.Bind("Loot", "MinPartsFill", 15f, new ConfigDescription(
                "Lowest fill (% of capacity) of a spawned car's tank (gas/diesel), engine oil and radiator water", new AcceptableValueRange<float>(0f, 100f)));
            MaxPartsFill = H.Bind("Loot", "MaxPartsFill", 60f, new ConfigDescription(
                "Highest fill (%) of tank, oil and water; each rolled separately, weighted toward two thirds of the way from Min to Max",
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
            PoseBikeArm = PoseFloat.Create(Config, "Pose", "BikeArmAngle", 55f, "Motorcycle riders (Motorcycle, Halfbreed): arm angle down to the handlebar", bodyRange, exposePose);
            PoseBikeArmSpread = PoseFloat.Create(Config, "Pose", "BikeArmSpread", 22f, "Motorcycle riders: each arm this far outward, hands on the handlebar grips", closerRange, exposePose);
            PoseElbow = PoseFloat.Create(Config, "Pose", "ElbowAngle", 25f, "Fallback elbow angle", bodyRange, exposePose);

            foreach (var human in HumanTypes)
                HumanArmPoses[human] = ArmPoseProfile.Create(Config, human,
                    Array.IndexOf(PassengerGuard.RangedHumans, human) >= 0, exposePose);
            ArmPoseProfile.RemoveHardcodedSettings(Config);
            if (!exposePose) ArmPoseProfile.RemoveStoredConfiguration(Config);

            const string CS = "Convoy spawner";
            HideFsmWarnings = H.Bind("General", "Hide missing-FSM warnings", true, "The game's \"Could not find FSM: ID on GameObject: ...\" warnings are not printed (QuietFsm)");
            MaxHeat = H.Bind(CS, "MaxHeat", 3f, new ConfigDescription(
                "Upper limit of the heat (3 = 300 %). Below 100 % the heat is how complete a group is; above 100 % it only raises the chances of " +
                "shortens the cooldown a little; group definitions, cargo and exact rosters are configured in the editor", new AcceptableValueRange<float>(0f, 5f)));
            HeatIntervalKm = H.Bind(CS, "HeatIntervalKm", 10f, new ConfigDescription(
                "Every this many km of the game's Distance Travelled add 25 % heat (linear: 10 = 100 % at 40 km)", new AcceptableValueRange<float>(1f, 200f)));
            ConvoySpawnDistance = H.Bind(CS, "SpawnDistance", 350f, new ConfigDescription(
                "How far away a spawn appears (m): ahead of your car, up to 45 degrees left or right; behind you when on foot", new AcceptableValueRange<float>(50f, 1000f)));
            CleanupEnabled = H.Bind("Cleanup", "Enabled", true,
                "Remove raider cars you left behind. A car you have ever sat in is never removed (the game itself only deletes cars beyond 5 km)");
            CleanupMinutes = H.Bind("Cleanup", "RemoveAfterMinutes", 10f, new ConfigDescription(
                "A raider car farther than MinDistance for this long is removed with its crew and cargo (0 = never by time)", new AcceptableValueRange<float>(0f, 600f)));
            CleanupMaxCars = H.Bind("Cleanup", "MaxCars", 30, new ConfigDescription(
                "With more raider cars than this in the world, the farthest ones beyond MinDistance are removed first (0 = no limit)", new AcceptableValueRange<int>(0, 200)));
            CleanupDistance = H.Bind("Cleanup", "MinDistance", 800f, new ConfigDescription(
                "Cars closer than this are never removed (m)", new AcceptableValueRange<float>(100f, 5000f)));

                                    ExitSpeedKmh = H.Bind("Debug", "ExitSpeedKmh", 30f, new ConfigDescription(
                "You can leave your car below this speed (km/h). The game's own limit is 6 m/s = 21.6 km/h, too low to get out of a car a truck keeps shoving",
                new AcceptableValueRange<float>(5f, 200f)));
            
            MigrateMovedSettings();
            ApplyAudioVoices();   // after the migration: an old [General] AudioVoices counts
            Paint.Preload();      // all Textures decoded now, not in the middle of a spawn
            CarTemplates.Load();  // JSON car templates: embedded in the DLL + plugins\Apocapatrol\CarTemplates\*.json
            PurgeStaleEntries();

            SceneManager.sceneLoaded += (s, m) => { EditorSession.CancelAll(); PickupCatalog.Invalidate(); EnsureRunner(); Patrol.ResetForScene(); PlayerRef.Reset(); PatrolPersistence.ResetForScene(); Convoy.ResetForScene(); };
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded (" + CarTemplates.Summary + ")");
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
            _runner.AddComponent<TemplateExporter>();
            _runner.AddComponent<TurretSpotter>();
            _runner.AddComponent<Convoy>();
            _runner.AddComponent<Cleanup>();
        }

        // Unity mixes at most numRealVoices sounds; the rest are virtual (silent until a slot frees up). Reset() restarts the audio system,
        // so this runs once at plugin load, before the game plays anything.
        private static void ApplyAudioVoices()
        {
            try
            {
                int want = AudioVoices.Value;
                var cfg = AudioSettings.GetConfiguration();
                if (want <= 0 || cfg.numRealVoices == want) { Log.LogInfo("Audio: " + cfg.numRealVoices + " real voices (unchanged)"); return; }
                int before = cfg.numRealVoices;
                cfg.numRealVoices = want;
                if (cfg.numVirtualVoices < want) cfg.numVirtualVoices = want * 8;
                bool ok = AudioSettings.Reset(cfg);
                Log.LogInfo("Audio: real voices " + before + " -> " + AudioSettings.GetConfiguration().numRealVoices + (ok ? "" : " (Reset failed)"));
            }
            catch (Exception e) { Log.LogWarning("Audio: could not set the voice count: " + e.Message); }
        }

        internal static void Verbose(string msg)
        {
            if (VerboseLog != null && VerboseLog.Value && Log != null) Log.LogInfo(msg);   // null while Awake is still binding the config
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
