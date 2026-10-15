using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class TrafficPlatform : MonoBehaviour { internal TrafficAgent Owner; }

    internal sealed class TrafficAgent : MonoBehaviour
    {
        internal string Journey;
        internal int Slot;
        internal bool Truck;
        internal float SeenUntil;
        internal Vector3 LastKnown;
        internal string Detection = "travelling";
        private float _nextSense;

        internal bool Goal(out Vector3 goal, out float cruise, out bool stop, out bool hunt)
        {
            goal = transform.position; cruise = 0f; stop = true; hunt = false;
            var battle=GetComponent<BattleCar>();if(battle!=null)return battle.Goal(out goal,out cruise,out stop,out hunt);
            var journey = WorldTraffic.Find(Journey);
            if (journey == null || !Plugin.WorldTrafficEnabled.Value) return false;
            if(journey.merchant)
            {goal=TrafficSpace.Scene(journey.arrived?journey.route[journey.route.Length-1]:RoadPath.Target(journey.route,journey.leg,TrafficSpace.World(transform.position),journey.speed));cruise=journey.speed;stop=journey.arrived||journey.waitSeconds>0f;return true;}
            Vector3 player, velocity; GameObject car;
            player=velocity=Vector3.zero;car=null;
            bool hasPlayer = TrafficFactions.PlayerHostile(journey.faction)&&PlayerRef.Target(out player, out velocity, out car) && !PlayerRef.GodMode;
            float distance = hasPlayer ? TrafficLocations.FlatDistance(journey.position, TrafficSpace.World(player)) : float.MaxValue;
            if (Time.time >= _nextSense)
            {
                _nextSense = Time.time + 0.5f;
                bool saw = false, heard = false;
                if (hasPlayer && !journey.convoy)
                {
                    var delta = player - transform.position; delta.y = 0f;
                    float sight = TrafficRules.VisibilityRange(DayLight.Dark, PatrolVisibility.LightsOn(car));
                    saw = delta.magnitude <= sight && Vector3.Angle(transform.forward, delta) <= 100f
                        && Sight(player, car);
                    heard = DayLight.Dark && delta.magnitude <= TrafficLocations.EngineRange(car);
                    if (saw || heard) { LastKnown = TrafficSpace.World(player); SeenUntil = Time.time + Plugin.PatrolMemorySeconds.Value; }
                }
                Detection = saw ? "sight" : heard ? "engine" : Time.time < SeenUntil ? "last known" : "travelling";
                _detected = saw || heard;
            }
            hunt = hasPlayer && TrafficRules.Pursue(journey.convoy, Truck, distance, Plugin.ConvoyGuardRange.Value,
                _detected, Time.time < SeenUntil);
            if (hunt)
            {
                goal = journey.convoy || _detected ? player : TrafficSpace.Scene(LastKnown);
                cruise = journey.speed; stop = false; return true;
            }
            var actual = TrafficSpace.World(transform.position);
            var rb = GetComponent<Rigidbody>(); float speed = rb != null ? rb.velocity.magnitude : journey.speed;
            goal = TrafficSpace.Scene(journey.arrived ? journey.route[journey.route.Length - 1] : RoadPath.Target(journey.route, journey.leg, actual, speed));
            if ((journey.convoy && !Truck) || (!journey.convoy && Slot > 0))
            {
                // Escorts return to the cargo vehicle after an engagement, keeping a staggered column.
                float behind = FormationRank(journey, Slot) * 18f;
                float leaderProgress = RoadPath.Progress(journey.route, journey.leg, journey.position);
                float ownProgress = RoadPath.Progress(journey.route, journey.leg, actual);
                if (journey.arrived) goal = TrafficSpace.Scene(RoadPath.Point(journey.route, leaderProgress - behind));
                cruise = Mathf.Clamp(journey.speed + (leaderProgress - behind - ownProgress) * 0.25f, 2f, journey.speed + 8f);
            }
            else cruise = journey.speed;
            cruise = RoadPath.Speed(journey.route, journey.leg, actual, cruise);
            stop = journey.arrived && (Truck || Slot == 0 || TrafficLocations.FlatDistance(transform.position, goal) < 12f);
            return true;
        }
        private bool _detected;

        internal static int FormationRank(TrafficJourney journey, int slot)
        {
            if (!journey.convoy) return slot;
            if (slot >= 0 && slot < journey.vehicles.Length && journey.vehicles[slot].truck) return 0;
            int rank = 1;
            for (int i = 0; i < slot && i < journey.vehicles.Length; i++) if (!journey.vehicles[i].truck) rank++;
            return rank;
        }

        private bool Sight(Vector3 target, GameObject playerCar)
        {
            var origin = transform.position + Vector3.up * 2f;
            var delta = target + Vector3.up - origin;
            int count=Physics.RaycastNonAlloc(origin,delta.normalized,SightHits,delta.magnitude,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
            if(count==SightHits.Length)return false;
            for(int i=0;i<count;i++)
            {
                var hit=SightHits[i];
                var t = hit.collider.transform;
                if (t.IsChildOf(transform) || (playerCar != null && t.IsChildOf(playerCar.transform))
                    || (PlayerRef.Player != null && t.IsChildOf(PlayerRef.Player))) continue;
                return false;
            }
            return true;
        }

        private static readonly RaycastHit[] SightHits=new RaycastHit[24];
        internal bool CanFight
        {
            get
            {
                var journey = WorldTraffic.Find(Journey);
                if(journey?.merchant==true)return TrafficFactions.ForCar(gameObject)!=null;
                if(GetComponent<BattleCar>()!=null)return TrafficFactions.ForCar(gameObject)!=null;
                var opposing=TrafficFactions.ForCar(gameObject);
                if(opposing!=null&&opposing.GetComponent<TrafficActor>()!=null)return true;
                if(journey!=null&&!TrafficFactions.PlayerHostile(journey.faction))return false;
                if (journey == null) return true;
                if (!journey.convoy) return !PlayerRef.GodMode&&(_detected||Time.time<SeenUntil);
                var player = PlayerRef.Player;
                return player != null && !PlayerRef.GodMode
                    && TrafficLocations.FlatDistance(journey.position, TrafficSpace.World(player.position)) <= Plugin.ConvoyGuardRange.Value;
            }
        }
    }

    internal sealed partial class WorldTraffic : MonoBehaviour
    {
        private sealed class Live
        {
            internal readonly Dictionary<int, GameObject> Cars = new Dictionary<int, GameObject>();
            internal bool Building;
            internal int Reserved;
            internal float RetryAt;
        }
        private sealed class Departure
        {
            internal RoadJob Job;
            internal TrafficBase Source, Destination;
            internal string Name;
            internal string Faction;
            internal TrafficVehicle[] Vehicles;
        }
        private static WorldTraffic _instance;
        private readonly List<TrafficJourney> _journeys = new List<TrafficJourney>();
        private readonly Dictionary<string, Live> _live = new Dictionary<string, Live>();
        private readonly Dictionary<bool, Departure> _departures = new Dictionary<bool, Departure>();
        private readonly Dictionary<string, RoadJob> _routeJobs = new Dictionary<string, RoadJob>();
        private readonly Dictionary<string, int> _routeAttempts = new Dictionary<string, int>();
        private Patrol _patrol;
        private float _elapsed, _nextPopulation;
        private int _generation;
        private float _nextRoadRefresh;
        private void Awake() { _instance = this; _generation = Patrol.SceneGen; }
        internal static TrafficJourney Find(string id)
        {if(_instance==null)return null;foreach(var journey in _instance._journeys)if(journey.id==id)return journey;return null;}
        internal static bool Owns(PatrolMarker marker)
        { return marker != null && marker.GetComponent<TrafficAgent>() != null && Plugin.WorldTrafficEnabled.Value; }
        internal static bool CombatAllowed(GameObject car)
        {if(car!=null&&car.GetComponent<PatrolMarker>()?.SetupOnly==true)return false;var a = AgentOf(car); return a == null || a.CanFight; }
        internal static TrafficAgent AgentOf(GameObject car)
        { return car == null ? null : car.GetComponent<TrafficAgent>() ?? car.GetComponent<TrafficPlatform>()?.Owner; }
        internal static string OverlayLine()
        {
            if (_instance == null) return "World traffic: initializing";
            return "World traffic: " + TrafficLocations.Known.Count + " camps, " + _instance._journeys.Count
                + " groups, " + _instance.RealCars + " cars, " + TrafficRoads.Status + (TrafficLocations.Known.Count < 2 ? " — discovering camps" : "");
        }
        internal static void ResetForScene()
        {
            TrafficLocations.Reset();
            TrafficCamps.Reset();
            EventBudget.Reset();
            RoadPlanner.Reset();
            TrafficRoads.Reset();
            TrafficSpace.Reset();
            if (_instance == null) return;
            _instance.StopAllCoroutines(); _instance._journeys.Clear(); _instance._live.Clear();
            _instance._departures.Clear(); _instance._routeJobs.Clear(); _instance._routeAttempts.Clear();
            _instance._roamingDeparture=null;
            _instance._nearDepartures.Clear();_instance._nextMerchant=0f;_instance._hornFsm=null;_instance._hornCar=null;
            _instance._battles.Clear();_instance._battleLive.Clear();_instance._nextBattleScan=0f;
            _instance._elapsed = _instance._nextPopulation = 0f; _instance._generation = Patrol.SceneGen;
            _instance._gameInkAt = 0f;
            _instance._nextRoadRefresh = 0f;
        }

        private int RealCars
        {get{int count=0;foreach(var live in _live.Values){count+=live.Reserved;foreach(var car in live.Cars.Values)if(car!=null)count++;}foreach(var battle in _battleLive.Values){count+=battle.Reserved;foreach(var car in battle.Cars.Values)if(car!=null)count++;}return count;}}

        private void Update()
        {
            if (_patrol == null) _patrol = GetComponent<Patrol>();
            if (_patrol == null || !_patrol.InGame() || PatrolPersistence.Restoring) return;
            MerchantSignals();
            if (!Plugin.WorldTrafficEnabled.Value)
            {
                // Toggle off: detach existing physical cars into ordinary patrol cleanup; no hidden representations respawn.
                foreach (var live in _live.Values) foreach (var car in live.Cars.Values)
                    if (car != null) Detach(car);
                foreach(var battle in _battleLive.Values){foreach(var car in battle.Cars.Values)if(car!=null){Destroy(car.GetComponent<BattleCar>());Detach(car);}foreach(var corpse in battle.Corpses)if(corpse!=null)Destroy(corpse);}
                StopAllCoroutines(); _live.Clear(); _journeys.Clear();_battleLive.Clear();_battles.Clear(); return;
            }
            TrafficLocations.Tick();
            TrafficCamps.Tick();
            if (Plugin.TrafficRoadRouting.Value) TrafficRoads.Tick();
            _elapsed += Time.deltaTime;
            if (_elapsed < 0.25f) return;
            float seconds = _elapsed; _elapsed = 0f;
            try { Tick(seconds); }
            catch (Exception e) { Plugin.Log.LogError("World traffic: " + e); }
        }

        private void Tick(float seconds)
        {
            var player = PlayerRef.Player; if (player == null) return;
            int nearby=TrafficLocations.NearbyCount(TrafficSpace.World(player.position),Plugin.CampTrafficRange.Value);
            BattleTick(seconds,player);
            ExcludeClearedBases();
            TrimPopulation(player);
            RefreshRoadRoutes();
            float load = Plugin.TrafficLoadRange.Value;
            float unload = Mathf.Max(load + 100f, Plugin.TrafficUnloadRange.Value);
            foreach (var journey in _journeys.ToArray())
            {
                if(journey.merchant)journey.waitSeconds=Mathf.Max(0f,journey.waitSeconds-seconds);
                if(!string.IsNullOrEmpty(journey.battle))continue;
                _live.TryGetValue(journey.id, out var live);
                if (live != null && !live.Building)
                {
                    foreach (var pair in live.Cars.ToArray())
                    {
                        var car = pair.Value;
                        var marker = car != null ? car.GetComponent<PatrolMarker>() : null;
                        var crew = car != null ? car.GetComponent<Crew>() : null;
                        if (marker == null || marker.PlayerEntered || PlayerRef.PlayerCar == car || marker.Exploded
                            || crew == null || !crew.CrewAlive || crew.Phase == CrewPhase.Released)
                        {
                            journey.vehicles[pair.Key].retired = true;
                            if (car != null) Detach(car);
                            live.Cars.Remove(pair.Key); // captured cars, wrecks and dismounts remain in the real world
                        }
                    }
                    var leader = live.Cars.OrderByDescending(p => journey.vehicles[p.Key].truck).Select(p => p.Value).FirstOrDefault(c => c != null);
                    if (leader != null)
                    {
                        float reach = journey.leg == journey.route.Length - 1 ? 12f : 22f;
                        journey.Follow(TrafficSpace.World(leader.transform.position), reach);
                        bool far = TrafficLocations.FlatDistance(journey.position, TrafficSpace.World(player.position)) > unload
                            && live.Cars.Values.All(c => c == null || Vector3.Distance(c.transform.position, player.position) > unload);
                        if (far) Unload(journey, live);
                    }
                    else if (journey.vehicles.All(v => v.retired)) { Remove(journey); continue; }
                }
                if (!_live.ContainsKey(journey.id)&&journey.waitSeconds<=0f) journey.Advance(seconds);
                bool nearDestination = TrafficLocations.FlatDistance(journey.position, TrafficSpace.World(player.position)) <= load;
                bool nearMember = live != null && live.Cars.Values.Any(c => c != null && Vector3.Distance(c.transform.position, player.position) <= load);
                if (journey.Expire(seconds, nearDestination || nearMember, Plugin.TrafficDestinationGrace.Value)) { Remove(journey); continue; }
                if ((nearby>0||journey.roaming||journey.merchant||journey.freeRoute) && !_live.ContainsKey(journey.id) && nearDestination && !Convoy.AutosaveSoon(30f)
                    && RealCars < Plugin.TrafficMaxRealCars.Value && EventBudget.Available(journey.id)>0)
                {
                    var next = new Live(); _live[journey.id] = next;
                    StartCoroutine(Materialize(journey, next));
                }
                else if (live != null && live.Cars.Count == 0 && !live.Building && Time.time >= live.RetryAt)
                { _live.Remove(journey.id); }
            }
            PumpNearbyRoutes();PopulateMerchants();
            if (!TrafficLocations.DiscoveryReady || !Plugin.ConvoyEnabled.Value || Time.time < _nextPopulation) return;
            _nextPopulation = Time.time + 1f;
            PopulateRoaming(TrafficSpace.World(player.position));
            Populate(false, Mathf.Min(Mathf.Max(0,Plugin.MaxPatrols.Value-RoamingCap),nearby)); Populate(true, Mathf.Min(Plugin.MaxConvoys.Value,nearby));
        }

        private static bool Cleared(string id)=>id!=null&&TrafficLocations.Known.TryGetValue(id,out var camp)&&camp.cleared;
        private void ExcludeClearedBases()
        {
            foreach(var journey in _journeys.ToArray())
            {
                if(!string.IsNullOrEmpty(journey.battle))continue;
                if(!Cleared(journey.source)&&!Cleared(journey.destination))continue;
                if(journey.roaming||journey.merchant||journey.freeRoute)continue;
                if(!_live.ContainsKey(journey.id)){Remove(journey);continue;}
                if(Cleared(journey.source))journey.source="";
                if(!Cleared(journey.destination))continue;
                var destination=TrafficLocations.Known.Values.Where(c=>!c.cleared).OrderBy(c=>TrafficLocations.FlatDistance(c.apron,journey.position)).FirstOrDefault();
                if(destination==null)
                {
                    // No valid destination: hold nearby physical traffic, then let normal unattended cleanup retire it.
                    journey.destination="";journey.route=new[]{journey.position,journey.position};journey.roadLegs=new bool[2];journey.leg=1;
                    journey.arrived=true;journey.unseenSeconds=journey.destinationSeconds=0f;_routeJobs.Remove(journey.id);_routeAttempts.Remove(journey.id);continue;
                }
                journey.destination=destination.id;journey.arrived=false;journey.unseenSeconds=journey.destinationSeconds=0f;
                journey.route=RoadPlanner.Build(journey.position,destination.apron,new Vector3[0][],TrafficLocations.Blocks(),0f,journey.id.GetHashCode(),out var roads);
                journey.roadLegs=roads;journey.leg=1;_routeJobs.Remove(journey.id);_routeAttempts.Remove(journey.id);
                _nextRoadRefresh=0f;
            }
        }

        private void TrimPopulation(Transform player)
        {
            if(!TrafficLocations.DiscoveryReady)return; // Preserve saved journeys while lazy camp discovery initializes after load.
            for(int role=0;role<3;role++)
            {
                bool convoy=role==1,roaming=role==2;int camps=TrafficLocations.NearbyCount(TrafficSpace.World(player.position),Plugin.CampTrafficRange.Value);
                int cap=roaming?RoamingCap:convoy?Mathf.Min(Plugin.MaxConvoys.Value,camps):Mathf.Min(Mathf.Max(0,Plugin.MaxPatrols.Value-RoamingCap),camps);
                int count=_journeys.Count(j=>!j.merchant&&j.convoy==convoy&&j.roaming==roaming);
                if(count<=cap)continue;
                foreach(var journey in _journeys.ToArray())
                {
                    if(count<=cap)break;
                    if(journey.merchant||journey.freeRoute||journey.convoy!=convoy||journey.roaming!=roaming||!string.IsNullOrEmpty(journey.battle)||_live.ContainsKey(journey.id)||(cap>0&&TrafficLocations.FlatDistance(journey.position,TrafficSpace.World(player.position))<=Plugin.TrafficLoadRange.Value))continue;
                    Remove(journey);count--;
                }
            }
        }

        private void Populate(bool convoy, int desired)
        {
            var bases = TrafficLocations.Known.Values.Where(b=>!b.cleared&&b.faction!="Coyotes").ToArray();
            if(bases.Length<2){_departures.Remove(convoy);return;}
            int existing = _journeys.Count(j => !j.merchant&&j.convoy == convoy&&!j.roaming)+_nearDepartures.Count(d=>d.Role==(convoy?1:0));
            if(existing>=desired||_journeys.Count>=Plugin.TrafficMaxGroups.Value){_departures.Remove(convoy);return;}
            if(_departures.TryGetValue(convoy,out var pending))
            {
                if(!pending.Job.Complete)return;
                _departures.Remove(convoy);
                if(pending.Job.Error!=null)Plugin.Log.LogWarning("Road planning: "+pending.Job.Error);
                var route=pending.Job.Route;
                if(route!=null&&!Cleared(pending.Source.id)&&!Cleared(pending.Destination.id)&&TrafficCamps.Loaded(pending.Source.id))
                {
                    _journeys.Add(new TrafficJourney{id=Guid.NewGuid().ToString("N"),name=pending.Name,source=pending.Source.id,destination=pending.Destination.id,
                        convoy=convoy,faction=pending.Faction,route=route,roadLegs=pending.Job.Roads,TerrainRoute=pending.Job.Elevation,position=route[0],vehicles=pending.Vehicles,
                        speed=(convoy?Plugin.TrafficConvoySpeed.Value:Plugin.TrafficPatrolSpeed.Value)/3.6f});
                    return;
                }
            }
            // At most one outstanding departure per role; failed pairs don't trigger a burst of graph searches.
            {
                var group = Convoy.TrafficGroup(convoy,convoy?null:"Scrapyard"); if (group == null) return;
                var roster = EditorStore.ComposeFull(group, convoy, () => UnityEngine.Random.value);
                if (roster.Count == 0 || roster.Any(t => !t.Available)) return;
                var player=PlayerRef.Player;
                var local=player!=null?TrafficLocations.NearbyBases(TrafficSpace.World(player.position),Plugin.CampTrafficRange.Value):new TrafficBase[0];
                if(local.Length==0)return;
                var source = local[UnityEngine.Random.Range(0, local.Length)];
                var choices = bases.Where(b => b != source && TrafficLocations.FlatDistance(source.apron, b.apron) >= 300f).ToArray();
                if (choices.Length == 0) return;
                var destination = choices[UnityEngine.Random.Range(0, choices.Length)];
                _departures[convoy]=new Departure{Job=RoadPlanner.RequestRoute(source,destination,convoy),Name=group.Name,Faction=group.Faction,Source=source,Destination=destination,
                    Vehicles=roster.Select(t=>Freeze(t)).ToArray()};
            }
        }

        private void RefreshRoadRoutes()
        {
            if (!Plugin.TrafficRoadRouting.Value) return;
            foreach(var pair in _routeJobs.ToArray())
            {
                if(!pair.Value.Complete)continue;
                _routeJobs.Remove(pair.Key);
                var journey=Find(pair.Key);var job=pair.Value;
                if(journey==null||journey.arrived||job.Route==null||job.Roads==null)continue;
                if(journey.route.Length>2&&journey.route.Length<=32&&!job.Roads.Any(b=>b)&&!job.Elevation)continue;
                // The record may have advanced while the worker searched. Start at its actual current position.
                if(TrafficLocations.FlatDistance(journey.position,job.Route[0])>60f){_routeAttempts.Remove(pair.Key);continue;}
                journey.route=job.Route;journey.roadLegs=job.Roads;journey.TerrainRoute=job.Elevation;journey.leg=1;
            }
            if(Time.time<_nextRoadRefresh)return;
            _nextRoadRefresh=Time.time+0.5f;
            foreach (var journey in _journeys)
            {
                if (journey.roaming || journey.merchant || journey.freeRoute || journey.arrived || (journey.TerrainRoute&&journey.route.Length<=32&&journey.roadLegs.Any(b => b)) || _routeJobs.ContainsKey(journey.id)
                    || (_routeAttempts.TryGetValue(journey.id,out int attempted)&&attempted==TrafficRoads.Revision)
                    || !TrafficLocations.Known.TryGetValue(journey.destination, out var destination)) continue;
                if(_live.TryGetValue(journey.id,out var live)&&!journey.convoy&&live.Cars.Values.Any(c=>c!=null&&c.GetComponent<TrafficAgent>()?.SeenUntil>Time.time))continue;
                if(live!=null&&live.Building)continue;
                var source = new TrafficBase { id = journey.source, apron = journey.position };
                _routeAttempts[journey.id]=TrafficRoads.Revision;
                _routeJobs[journey.id]=RoadPlanner.RequestRoute(source,destination,journey.convoy);
                break; // spread route requests across updates; physical groups can adopt roads too
            }
        }

        private void OnApplicationQuit() { TrafficRoads.Flush(); }

        private static TrafficVehicle Freeze(CarTemplate t)
        {
            var record = new TrafficVehicle { template = t.Name, truck = t.IsTruck, cargoKey = t.SpawnCargoKey,faction=t.Faction,strength=TrafficBattles.Strength(t) };
            record.merchantTypes=t.MerchantTypes;record.merchantGroup=t.MerchantGroup;
            // Freeze the exact roster, parts, bumper choice and cargo at departure, so each reload is the same encounter.
            if (t.Parts != null)
            {
                var file = new TemplateFile { schema = 3, name = t.Name, body = t.Body, kind = t.KindLabel, parts = t.Parts,
                    faction=t.Faction,
                    driver = t.Driver, passenger = t.Passenger, rams = t.Rams.ToString(), lootPreset = t.LootPreset,explodeChance=t.ExplodeChance,
                    fillFuel = t.FillFuel, releaseHandbrake = t.ReleaseHandbrake, fuelLitres = t.FuelLitres,
                    suspensionWidth = t.SuspensionWidth, suspensionHeight = t.SuspensionHeight,
                    bumpers = t.Bumpers == null ? null : new[] { t.RollBumper() } };
                for (int i = 0; i < 3; i++) { file.SetTurret(i, t.Turrets[i]); file.SetTurretPos(i, t.TurretPos[i]); file.SetTurretStanding(i,t.TurretStanding[i]); }
                if (t.Trailer != null)
                {
                    file.trailer = new TemplateFile { schema = 3, body = t.Trailer.Body, parts = t.Trailer.Parts };
                    for (int i = 0; i < 4; i++) { file.trailer.SetTurret(i, t.Trailer.Turrets[i]); file.trailer.SetTurretPos(i, t.Trailer.TurretPos[i]); file.trailer.SetTurretStanding(i,t.Trailer.TurretStanding[i]); }
                }
                record.definition = file.ToJson();
            }
            var loot = t.SpawnLoot;
            if (loot == null)
            {
                var profile = EditorStore.Loot(t.IsTruck, t.LootPreset) ?? (t.IsTruck ? EditorStore.RollTruckLoot(() => UnityEngine.Random.value, t.SpawnCargoOptions) : null);
                loot = profile != null ? profile.Roll(() => UnityEngine.Random.value) : new Dictionary<string, int>();
                record.cargoKey = EditorStore.CargoTexture(loot, profile != null ? profile.TextureKey : "");
            }
            record.lootNames = loot.Keys.ToArray(); record.lootCounts = record.lootNames.Select(k => loot[k]).ToArray();
            return record;
        }

        private static CarTemplate Thaw(TrafficVehicle record)
        {
            var t = string.IsNullOrEmpty(record.definition) ? CarTemplate.Find(record.template)?.CloneForSpawn()
                : CarTemplates.Parse(record.definition, record.template, "world traffic", record.template);
            if (t == null || !t.Available) return null;
            t.Faction=TrafficFactions.Normalize(record.faction);
            if(record.merchantTypes!=null&&record.merchantTypes.Length>0){t.MerchantTypes=MerchantCatalog.Selection(record.merchantTypes);t.MerchantGroup=record.merchantGroup;t.MerchantTravelling=true;t.MerchantSaveJson=record.crew?.merchantJson;t.ReleaseHandbrake=true;t.ConvoyMember=true;}
            t.SpawnLoot = new Dictionary<string, int>();
            for (int i = 0; i < record.lootNames.Length && i < record.lootCounts.Length; i++) t.SpawnLoot[record.lootNames[i]] = record.lootCounts[i];
            t.SpawnCargoKey = record.cargoKey;
            if (record.crew != null) { t.Driver = record.crew.driverPresent ? record.crew.driverPrefab : ""; t.Passenger = record.crew.passengerPresent ? record.crew.passengerPrefab : ""; }
            if (record.turrets != null)
                for (int i = 0; i < record.turrets.Length; i++)
                { if (i < 3) t.Turrets[i] = record.turrets[i]; else if (t.Trailer != null && i < 7) t.Trailer.Turrets[i - 3] = record.turrets[i]; }
            return t;
        }

        private IEnumerator Materialize(TrafficJourney journey, Live live)
        {
            live.Building = true; live.Reserved = journey.vehicles.Count(v => !v.retired);
            int generation = _generation, pending = 0;
            var capped=new HashSet<int>();
            var heading = journey.Heading;
            foreach(int slot in Enumerable.Range(0,journey.vehicles.Length).OrderByDescending(i=>journey.vehicles[i].truck))
            {
                if (journey.vehicles[slot].retired) continue;
                if(EventBudget.Available(journey.id)<=0||RealCars-live.Reserved>=Plugin.TrafficMaxRealCars.Value)
                {capped.Add(slot);live.Reserved--;continue;}
                var t = Thaw(journey.vehicles[slot]);
                // If an optional mod is unavailable, preserve the world record and retry instead of rerolling it.
                if (t == null) { live.Reserved--; continue; }
                float progress = RoadPath.Progress(journey.route, journey.leg, journey.position) - TrafficAgent.FormationRank(journey, slot) * 18f;
                var worldSpot = TrafficAgent.FormationRank(journey,slot)==0?journey.position:RoadPath.Point(journey.route,progress);
                var slotHeading = RoadPath.Point(journey.route, progress + 4f) - worldSpot; slotHeading.y = 0f;
                if (slotHeading.sqrMagnitude < 0.01f) slotHeading = heading;
                var spot = TrafficSpace.Scene(worldSpot);
                if (!Convoy.GroundAt(spot, out var ground)) { live.Reserved--; continue; }
                // Physical terrain/props must fit the chassis before construction, including a truck's wider lane.
                if (!TrafficLocations.VehicleClear(null, ground + Vector3.up * 2f, ground + Vector3.up * 2f + slotHeading.normalized * 10f, t.IsTruck ? 2f : 1.4f))
                { live.Reserved--; continue; }
                int index = slot; pending++;
                t.TrafficId = journey.id; t.TrafficSlot = index;
                t.ConvoyMember=journey.convoy||journey.merchant;
                _patrol.SpawnAt(t, ground + Vector3.up, Quaternion.LookRotation(slotHeading), true, car =>
                {
                    pending--; live.Reserved--;
                    if (generation != Patrol.SceneGen || !Plugin.WorldTrafficEnabled.Value || Find(journey.id) == null)
                    { if (car != null) Cleanup.Remove(car.GetComponent<PatrolMarker>(), "cancelled traffic build"); return; }
                    if (car == null) { if(EventBudget.Available(journey.id)<=0)capped.Add(index);return; }
                    live.Cars[index] = car;
                    Attach(car, journey, index);
                    ApplyState(car, journey.vehicles[index]);
                });
                yield return new WaitForSeconds(0.7f);
                while (!_patrol.InGame() && generation == Patrol.SceneGen) yield return null;
                if (generation != Patrol.SceneGen) yield break;
            }
            // Builder callbacks may arrive late: do not start a second build while one still owns a slot.
            while (pending > 0 && generation == Patrol.SceneGen) yield return null;
            live.Building = false; live.Reserved = 0; live.RetryAt = Time.time + 15f;
            foreach (var car in live.Cars.Values) if (car != null) car.GetComponent<Crew>()?.Release();
            if (live.Cars.Count > 0)
            {
                // A failed slot must not reappear at the next unload: keep only the constructed representation.
                for (int i = 0; i < journey.vehicles.Length; i++) if (!live.Cars.ContainsKey(i)&&!capped.Contains(i)) journey.vehicles[i].retired = true;
            }
        }

        private static void Attach(GameObject car, TrafficJourney journey, int slot)
        {
            var a = car.GetComponent<TrafficAgent>() ?? car.AddComponent<TrafficAgent>();
            a.Journey = journey.id; a.Slot = slot; a.Truck = journey.vehicles[slot].truck;
            EncounterCar.Attach(car,journey.id,journey.convoy||journey.merchant,car.GetComponent<EncounterCar>()?.Building==true);
            var marker = car.GetComponent<PatrolMarker>(); marker.TrafficId = journey.id; marker.TrafficSlot = slot;
            var trailer = TrailerSupport.Attached(car);
            if (trailer != null)
            {
                var platform = trailer.GetComponent<TrafficPlatform>() ?? trailer.AddComponent<TrafficPlatform>();
                platform.Owner = a;
            }
        }

        internal static void AttachPending(GameObject car, string id, int slot)
        {
            var journey = Find(id);
            if (_instance == null || journey == null || slot < 0 || slot >= journey.vehicles.Length) return;
            if (!_instance._live.TryGetValue(id, out var live)) return;
            live.Cars[slot] = car;
            Attach(car, journey, slot); // before native save registration, including manual saves during engine startup
        }

        private static void Detach(GameObject car)
        {
            var marker = car.GetComponent<PatrolMarker>();
            if (marker != null) { marker.TrafficId = ""; marker.TrafficSlot = -1; }
            var trailer = TrailerSupport.Attached(car);
            if (trailer != null) Destroy(trailer.GetComponent<TrafficPlatform>());
            Destroy(car.GetComponent<TrafficAgent>());
        }

        internal static void BindRestored(PatrolMarker marker)
        {
            var journey = Find(marker.TrafficId); if (journey == null || _instance == null) return;
            if (marker.TrafficSlot < 0 || marker.TrafficSlot >= journey.vehicles.Length) return;
            if (marker.PlayerEntered || marker.Exploded || journey.vehicles[marker.TrafficSlot].retired) { Detach(marker.gameObject); return; }
            if (!_instance._live.TryGetValue(journey.id, out var live)) _instance._live[journey.id] = live = new Live();
            live.Cars[marker.TrafficSlot] = marker.gameObject; Attach(marker.gameObject, journey, marker.TrafficSlot);
            var record = journey.vehicles[marker.TrafficSlot];
            var template = Thaw(record);
            if (template != null && record.turrets != null)
            {
                for (int i = 0; i < 7 && i < record.turrets.Length; i++)
                {
                    if (string.IsNullOrEmpty(record.turrets[i])) continue;
                    var vehicleTemplate = i < 3 ? template : template.Trailer;
                    if (vehicleTemplate == null) continue;
                    Rider.Seat(marker.gameObject, marker, i, record.turrets[i], vehicleTemplate.TurretPos[i < 3 ? i : i - 3],
                        i < 3 ? null : TrailerSupport.Attached(marker.gameObject),vehicleTemplate.TurretStanding[i<3?i:i-3]);
                }
                ApplyState(marker.gameObject, record);
            }
        }

        internal static void MissingRestored(PatrolCarData car)
        {
            var journey = Find(car.trafficId);
            if (journey != null && car.trafficSlot >= 0 && car.trafficSlot < journey.vehicles.Length)
                journey.vehicles[car.trafficSlot].retired = true; // late game load cannot create a second copy
        }

        private void Unload(TrafficJourney journey, Live live)
        {
            if(journey.merchant)journey.waitSeconds=0f;
            foreach (var pair in live.Cars)
            {
                if (pair.Value == null) continue;
                CaptureState(pair.Value, journey.vehicles[pair.Key]);
                if (!CanVirtualize(pair.Value, journey.vehicles[pair.Key]))
                {
                    journey.vehicles[pair.Key].retired = true;
                    Detach(pair.Value);
                    continue;
                }
                Cleanup.Remove(pair.Value.GetComponent<PatrolMarker>(), "world traffic continues virtually");
            }
            _live.Remove(journey.id);
            // Rejoin the current leg from the actual departure point, never teleport back to the pre-encounter point.
        }

        private void Remove(TrafficJourney journey)
        {
            if (_live.TryGetValue(journey.id, out var live))
            {
                if (live.Building) return;
                foreach (var car in live.Cars.Values)
                    if (car != null && !car.GetComponent<PatrolMarker>().PlayerEntered) Cleanup.Remove(car.GetComponent<PatrolMarker>(), "traffic destination unattended");
                _live.Remove(journey.id);
            }
            _journeys.Remove(journey);
        }

        internal static string SaveJson()
        {
            if (_instance == null) return "";
            foreach (var journey in _instance._journeys)
                if (_instance._live.TryGetValue(journey.id, out var live))
                    foreach (var pair in live.Cars) if (pair.Value != null) CaptureState(pair.Value, journey.vehicles[pair.Key]);
            foreach(var battle in _instance._battles)if(_instance._battleLive.TryGetValue(battle.id,out var live)&&!battle.resolved)
                foreach(var car in live.Cars.Values)if(car!=null){var tag=car.GetComponent<BattleCar>();if(tag!=null)CaptureState(car,(tag.Side==0?battle.sideA:battle.sideB)[tag.Slot]);}
            return TrafficCodec.Write(new TrafficSave { bases = TrafficLocations.Known.Values.ToArray(),pois=TrafficLocations.Points.Values.ToArray(), journeys = _instance._journeys.ToArray(),battles=_instance._battles.ToArray() });
        }

        internal static void RestoreJson(string json)
        {
            if (_instance == null || string.IsNullOrEmpty(json)) return;
            var saved = TrafficCodec.Read(json);
            if (saved == null || (saved.version != 1 && saved.version != 2) || saved.bases == null || saved.journeys == null
                || saved.bases.Length > 512 || saved.journeys.Length > 128) throw new System.IO.InvalidDataException("Invalid world traffic save");
            if (saved.version == 1) MigrateSceneCoordinates(saved);
            foreach (var b in saved.bases) if (b != null && !string.IsNullOrEmpty(b.id)) TrafficLocations.Known[b.id] = b;
            foreach(var p in (saved.pois??new TrafficPoi[0]).Take(1024))if(p!=null&&!string.IsNullOrEmpty(p.id))TrafficLocations.Points[p.id]=p;
            _instance._journeys.Clear(); _instance._live.Clear();
            _instance._battles.Clear();_instance._battleLive.Clear();
            if(saved.battles!=null)foreach(var battle in saved.battles.Take(64))if(TrafficBattleValid(battle))_instance._battles.Add(battle);
            foreach (var j in saved.journeys)
                if (j != null && !string.IsNullOrEmpty(j.id) && j.route != null && j.route.Length >= 2 && j.route.Length <= 20000
                    && j.vehicles != null && j.vehicles.Length <= 100 && j.leg >= 1 && j.leg < j.route.Length
                    && j.speed > 0f && j.speed <= 50f && j.vehicles.All(v => v != null))
                {
                    if (j.roadLegs == null || j.roadLegs.Length != j.route.Length) j.roadLegs = new bool[j.route.Length];
                    j.faction=TrafficFactions.Normalize(j.faction);if(!_instance._battles.Any(b=>b.id==j.battle))j.battle="";
                    foreach(var record in j.vehicles){record.faction=j.faction;var template=Thaw(record);if(template!=null)record.strength=TrafficBattles.Strength(template);}
                    _instance._journeys.Add(j);
                }
        }

        private static void MigrateSceneCoordinates(TrafficSave saved)
        {
            // The original prototype saved scene positions. Convert from the game's restored MapMagic offset once.
            var origin = TrafficSpace.Origin;
            var ids = new Dictionary<string, string>();
            foreach (var b in saved.bases)
            {
                if (b == null) continue;
                var previous = b.id;
                b.center -= origin; b.gate -= origin; b.apron -= origin;
                var parts = (previous ?? "").Split(':');
                if (parts.Length == 3 && int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int z))
                    b.id = parts[0] + ":" + Mathf.RoundToInt(x - origin.x) + ":" + Mathf.RoundToInt(z - origin.z);
                if (!string.IsNullOrEmpty(previous)) ids[previous] = b.id;
            }
            foreach (var j in saved.journeys)
            {
                if (j == null) continue;
                j.position -= origin;
                if (j.route != null) for (int i = 0; i < j.route.Length; i++) j.route[i] -= origin;
                if (j.source != null && ids.TryGetValue(j.source, out var source)) j.source = source;
                if (j.destination != null && ids.TryGetValue(j.destination, out var destination)) j.destination = destination;
            }
            saved.version = 2;
            Plugin.Verbose("Traffic: converted prototype scene-coordinate save to stable MapMagic world frame");
        }
    }
}
