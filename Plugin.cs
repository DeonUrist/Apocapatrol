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
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocapatrol";
        public const string NAME = "Apocapatrol";
        public const string VERSION = "1.10.0";

        internal static ManualLogSource Log;

        // [General] PatrolSizePercent: scales how many cars every spawn brings (25 / 50 / 100 / 125 / 150 %)
        internal static ConfigEntry<int> PatrolSizePercent;
        internal static float SizeFactor { get { return Mathf.Clamp(PatrolSizePercent != null ? PatrolSizePercent.Value : 100, 25, 150) / 100f; } }

        internal static ConfigEntry<Key> MenuKey;
        internal static readonly Dictionary<string, ConfigEntry<float>> LootChances = new Dictionary<string, ConfigEntry<float>>(StringComparer.OrdinalIgnoreCase);
        internal static ConfigEntry<float> MinPartHealth, MaxPartHealth, MinPartsFill, MaxPartsFill, LootMultiplier;

        internal static float LootChance(string key)
        {
            ConfigEntry<float> e;
            return key != null && LootChances.TryGetValue(key, out e) ? e.Value : 0f;
        }

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
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("General", "PatrolSizePercent"), PatrolSizePercent),
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("General", "AudioVoices"), AudioVoices),
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("Self-destruct", "SelfDestructingCars"), SelfDestruct),
                    new KeyValuePair<ConfigDefinition, ConfigEntryBase>(new ConfigDefinition("Loot", "Multiplier"), LootMultiplier),
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
                if (n > 0) Log.LogInfo("Config: carried " + n + " setting(s) over to [Scaling]");
            }
            catch (Exception e) { Log.LogWarning("Config migration: " + e.Message); }
        }

        private void Awake()
        {
            Log = Logger;
            Paint.Init(Info.Location);
            CarTemplates.Init(Info.Location);
            bool exposePose = PoseConfigurationEnabled(Config.ConfigFilePath);

            Config.Bind("General", "Apocasetter", true,
                "Show this mod in the Apocasetter Mods menu.\n" +
                "To expose seat offsets, shared pose settings and all per-human controls, uncomment the next line and restart:\n" +
                "PoseConfiguration = true");

            // ---- the settings players see (Denis, 1.8.0): [Scaling], [Combat], [Debug]. Everything else is fixed at the defaults below.
            PatrolSizePercent = Config.Bind("Scaling", "PatrolSizePercent", 100, new ConfigDescription(
                "Patrol size: how many cars every enemy spawn brings, in % of the full group (100 = as designed: 3 cars, a 5-car super group, " +
                "a convoy of a truck + 2 junkers + 3-5 small cars; groups are smaller than that only below 100 % heat, never bigger). " +
                "Lower it on a weak PC - fewer cars means fewer crews, physics bodies and AI drivers at once. A spawn always brings at least " +
                "one car; a convoy always brings its truck first",
                new AcceptableValueList<int>(25, 50, 100, 125, 150)));
            AudioVoices = Config.Bind("Scaling", "AudioVoices", 64, new ConfigDescription(
                "How many sounds the game can play at once (Unity's real voices; the game ships with 32). A firefight with several raider cars needs " +
                "more: every shot and every hit is a sound, and beyond the limit sounds - the player's own shots too - cut out. 64 is a good value; " +
                "0 leaves the game's own setting. Applied at game start (restart the game after a change)",
                new AcceptableValueList<int>(0, 32, 48, 64, 96, 128)));
            SelfDestruct = Config.Bind("Scaling", "SelfDestruct", true,
                "A raider car that is fully vacated (crew dead, bailed out, or the last passenger got out beside a dead driver) explodes: the frame " +
                "goes black (trucks stay a lootable wreck), every part pops off with 0 condition and the dead chassis cannot be entered, " +
                "fuelled or fitted with parts any more; it is removed once you are 1000 m away. Off = vacated cars stay as they are now. " +
                "A car you have sat in never explodes");
            LootMultiplier = Config.Bind("Scaling", "LootMultiplier", 1f, new ConfigDescription(
                "Scales the amount of loot in a truck: 0 = nothing, 1 = the built-in amounts (Food dogfood x6; Water / Gasoline / Diesel cans x4, " +
                "50 % a barrel too; Medicine bandages x4 + first aid x2; Weapons 0-3 guns + 3-8 ammo boxes; Drugs alcohol x2 + weed x3 + weed plant x1; " +
                "Mechanic 3 repair boxes + 1 big oil can; Corpses 3-5 dead Scraffa; Rats 6-8 dead rats), 3 = 300 %. The 50 % barrels are not scaled",
                new AcceptableValueRange<float>(0f, 3f)));
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
            RamDamageInCar = Config.Bind("Combat", "RamDamageInCar", false,
                "Ram damage also while you sit in your own car (the game's own CrashDamage still applies by your speed). Off = only on foot");

            MenuKey = Config.Bind("Debug", "TemplateSpawnerKey", Key.None, "Key for the template spawner (testing): a list of the park, click a car to build it in front of you. None = off (the default); F8 for example");
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
            //   [Loot] FoodChance 18, WaterChance 14, GasolineChance 11, DieselChance 11, MedicineChance 11, WeaponsChance 11,
            //          DrugsChance 7, MechanicChance 7, CorpsesChance 7, RatsChance 3, MinPartHealth 2, MaxPartHealth 35,
            //          MinPartsFill 15, MaxPartsFill 60
            //   [Convoy spawner] Enabled true, MaxHeat 3, HeatIntervalKm 10, SpawnDistance 350, JustCarsToConvoyRatio 0.8,
            //          (MinConvoyCooldown / MaxConvoyCooldown visible again in [Scaling] since 1.8.8) BasicCars 0 km / 0 bosses / 45, AdvancedCars 30 / 1 / 35,
            //          SuperAdvancedCars 50 / 3 / 20, BasicConvoy 5 / 0 / 70, AdvancedConvoy 20 / 3 / 30 (DistanceKm / BossesKilled / Chance)
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

                        foreach (var d in CarTemplate.LootDefaults)
                LootChances[d[0]] = H.Bind("Loot", d[0] + "Chance", float.Parse(d[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), new ConfigDescription(
                    "% chance that a loot truck carries " + d[0] + " (all XChance values should add up to 100)", new AcceptableValueRange<float>(0f, 100f)));
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
            PoseElbow = PoseFloat.Create(Config, "Pose", "ElbowAngle", 25f, "Fallback elbow angle", bodyRange, exposePose);

            foreach (var human in HumanTypes)
                HumanArmPoses[human] = ArmPoseProfile.Create(Config, human,
                    Array.IndexOf(PassengerGuard.RangedHumans, human) >= 0, exposePose);
            ArmPoseProfile.RemoveHardcodedSettings(Config);
            if (!exposePose) ArmPoseProfile.RemoveStoredConfiguration(Config);

            const string CS = "Convoy spawner";
            ConvoyEnabled = H.Bind(CS, "Enabled", true, "Enemy cars and convoys spawn on their own while you play (the Debug menu buttons work regardless)");
            MaxHeat = H.Bind(CS, "MaxHeat", 3f, new ConfigDescription(
                "Upper limit of the heat (3 = 300 %). Below 100 % the heat is how complete a group is; above 100 % it only raises the chances of " +
                "the tougher spawn types (advanced / super advanced cars, the advanced convoy) and shortens the cooldown a little - group sizes " +
                "stay at their 100 % values (x PatrolSizePercent)", new AcceptableValueRange<float>(0f, 5f)));
            HeatIntervalKm = H.Bind(CS, "HeatIntervalKm", 10f, new ConfigDescription(
                "Every this many km of the game's Distance Travelled add 25 % heat (linear: 10 = 100 % at 40 km)", new AcceptableValueRange<float>(1f, 200f)));
            ConvoySpawnDistance = H.Bind(CS, "SpawnDistance", 350f, new ConfigDescription(
                "How far away a spawn appears (m): ahead of your car, up to 45 degrees left or right; behind you when on foot", new AcceptableValueRange<float>(50f, 1000f)));
            JustCarsToConvoyRatio = H.Bind(CS, "JustCarsToConvoyRatio", 0.8f, new ConfigDescription(
                "When a convoy is allowed, how likely plain enemy cars spawn instead of it (0 = always the convoy, 1 = never)", new AcceptableValueRange<float>(0f, 1f)));

            BasicCarsKm = H.Bind(CS, "BasicCarsDistanceKm", 0f, new ConfigDescription("Basic enemy cars (3 small cars at 100 % heat) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            BasicCarsBosses = H.Bind(CS, "BasicCarsBossesKilled", 0, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            BasicCarsChance = H.Bind(CS, "BasicCarsChance", 45f, new ConfigDescription("Weight of basic enemy cars among the allowed car spawns (%)", new AcceptableValueRange<float>(0f, 100f)));
            AdvancedCarsKm = H.Bind(CS, "AdvancedCarsDistanceKm", 30f, new ConfigDescription("Advanced enemy cars (3 cars, at least 1 advanced, maybe a junker) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            AdvancedCarsBosses = H.Bind(CS, "AdvancedCarsBossesKilled", 1, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            AdvancedCarsChance = H.Bind(CS, "AdvancedCarsChance", 35f, new ConfigDescription("Weight of advanced enemy cars (%, multiplied by the heat)", new AcceptableValueRange<float>(0f, 100f)));
            SuperCarsKm = H.Bind(CS, "SuperAdvancedCarsDistanceKm", 50f, new ConfigDescription("Super advanced enemy cars (5 cars, half junkers, at least 2 advanced) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            SuperCarsBosses = H.Bind(CS, "SuperAdvancedCarsBossesKilled", 3, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            SuperCarsChance = H.Bind(CS, "SuperAdvancedCarsChance", 20f, new ConfigDescription("Weight of super advanced enemy cars (%, multiplied by the heat)", new AcceptableValueRange<float>(0f, 100f)));
            BasicConvoyKm = H.Bind(CS, "BasicConvoyDistanceKm", 5f, new ConfigDescription("Basic convoy (a basic truck + 2 junkers + 3-5 small cars) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            BasicConvoyBosses = H.Bind(CS, "BasicConvoyBossesKilled", 0, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            BasicConvoyChance = H.Bind(CS, "BasicConvoyChance", 70f, new ConfigDescription("Weight of the basic convoy among the allowed convoys (%)", new AcceptableValueRange<float>(0f, 100f)));
            AdvancedConvoyKm = H.Bind(CS, "AdvancedConvoyDistanceKm", 20f, new ConfigDescription("Advanced convoy (an advanced truck + the same escort, at least 2 advanced) from this Distance Travelled (km)", new AcceptableValueRange<float>(0f, 500f)));
            AdvancedConvoyBosses = H.Bind(CS, "AdvancedConvoyBossesKilled", 3, new ConfigDescription("... and this many bosses killed", new AcceptableValueRange<int>(0, 7)));
            AdvancedConvoyChance = H.Bind(CS, "AdvancedConvoyChance", 30f, new ConfigDescription("Weight of the advanced convoy (%, multiplied by the heat)", new AcceptableValueRange<float>(0f, 100f)));

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

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Patrol.ResetForScene(); PlayerRef.Reset(); PatrolPersistence.ResetForScene(); Convoy.ResetForScene(); };
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
