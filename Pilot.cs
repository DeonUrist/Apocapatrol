using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    internal enum PilotState { Charge, Overshoot, Turnaround, Recover, Wait, Idle }

    // The driving AI. Lives on the car next to Crew, which decides WHETHER the car drives (driver alive, delay, death);
    // Pilot decides HOW: every physics step it writes throttle / steering / brakes into the NWH input.
    //
    // Hit and run: Charge at an intercept point ahead of the player (on foot or in a car, same thing), with the steering
    // rate-limited and reduced at speed so the turn is a wide arc, not a pivot. Once the player is passed (or rammed) the car
    // runs out straight for a bit (Overshoot), turns around (Turnaround) and charges again. A hit against something that is
    // not the player, or being stuck, reverses out (Recover) and charges again; repeated failures escalate to a short Wait.
    // Front feelers (raycasts) steer around obstacles ahead; the player, the player's car, NPCs and loose items are not
    // obstacles - the car is meant to ram them.
    internal class Pilot : MonoBehaviour
    {
        private GameObject _car;
        private Transform _tf;
        private Rigidbody _rb;
        private RamTargets _rams = RamTargets.Pedestrians;   // from the car's template (PatrolMarker)
        private bool _ramsTarget;                             // this step: may the current target (player / their car) be rammed?
        private float _passSide = 1f;                         // drive-by side when the target must not be rammed

        private PilotState _state = PilotState.Charge;
        private float _stateTime;
        private string _why = "start";

        private float _steer;                 // current steering command (-1..1), rate-limited
        private float _throttle, _brakes;
        private Vector3 _aim;                 // committed intercept point
        private float _nextAim;
        private float _stuckTime;
        private Vector3 _runStart;
        private float _turnSlow;

        private int _recoverCount;
        private float _lastRecover = -999f;
        private float _recoverDur, _recoverSteer;

        private bool _hitPending, _hitIsTarget, _rearHit, _abandoned;
        private float _hitSide;
        private float _pushTime, _pushStamp = -1f;   // slow continuous push against an obstacle ahead
        private string _pushName = "";
        private float _rearClear = -1f;               // nearest obstacle behind while reversing, -1 = clear

        private float _nextLog, _nextEngine, _flipLogged;
        private bool _hasTarget;
        private float _angle, _dist;          // last computed, for the overlay/log
        private readonly float[] _feelerAngle = { 0f, -22f, 22f, -48f, 48f };
        private readonly float[] _feelerLen = new float[5];
        private readonly float[] _feelerHit = new float[5];      // hit distance or -1
        private float _cliffAt = -1f;
        private struct Verdict { public bool Obstacle; public float Until; }
        private readonly Dictionary<Collider, Verdict> _obstacleCache = new Dictionary<Collider, Verdict>();   // per-collider, 5 s each
        private readonly List<Collider> _expired = new List<Collider>();
        private float _cachePrune;
        private static readonly List<Transform> _ownRoots = new List<Transform>();
        private static readonly RaycastHit[] _hits = new RaycastHit[24];   // non-allocating cast buffer (shared; casts never nest)
        private int _senseStep;                                            // feelers run every other physics step
        private float _senseSteer, _senseThrottle = 1f, _senseBrake;       // last feeler result, reused on the skipped step

        internal PilotState State { get { return _state; } }

        internal static Pilot Attach(GameObject car)
        {
            var p = car.GetComponent<Pilot>() ?? car.AddComponent<Pilot>();
            p._car = car; p._tf = car.transform; p._rb = car.GetComponent<Rigidbody>();
            var marker = car.GetComponent<PatrolMarker>();
            p._rams = marker != null ? marker.Rams : RamTargets.Pedestrians;
            p.Enter(PilotState.Charge, "start");
            if (!_ownRoots.Contains(car.transform)) _ownRoots.Add(car.transform);
            Plugin.Verbose("Pilot: driving AI on " + car.name + ", rams " + p._rams);
            return p;
        }

        internal void Detach()
        {
            _ownRoots.Remove(_tf);
            Destroy(this);
        }

        private void OnDestroy() { _ownRoots.Remove(_tf); }

        // ------------------------------------------------------------ main step (called by Crew from FixedUpdate)

        internal void Step()
        {
            if (_abandoned) return;
            if (_rb != null && _rb.isKinematic) return;     // the game's DistanceKinematic froze the car (far from the player after a load): hold, don't count as stuck
            float dt = Time.fixedDeltaTime;
            _stateTime += dt;

            Vector3 tpos, tvel; GameObject tcar;
            _hasTarget = PlayerRef.Target(out tpos, out tvel, out tcar);
            _ramsTarget = tcar != null ? _rams >= RamTargets.Cars : _rams >= RamTargets.Pedestrians;
            Vector3 fwd = Flat(_tf.forward);
            Vector3 vel = _rb != null ? _rb.velocity : Vector3.zero;
            float speed = vel.magnitude;
            float forwardSpeed = Vector3.Dot(vel, _tf.forward);

            if (_hasTarget)
            {
                var to = Flat(tpos - _tf.position);
                _dist = to.magnitude;
                _angle = Vector3.SignedAngle(fwd, to.sqrMagnitude > 1e-3f ? to.normalized : fwd, Vector3.up);
            }
            else { _dist = 9999f; _angle = 0f; }

            // flipped over: nothing to do but wait for the game's own flip-over handling
            if (_tf.up.y < 0.2f)
            {
                if (Time.time > _flipLogged + 10f) { _flipLogged = Time.time; Plugin.Verbose("Pilot: " + _car.name + " is on its side/roof, waiting"); }
                Apply(0f, 0f, 0.3f);
                return;
            }
            if (Time.time >= _nextEngine)
            {
                _nextEngine = Time.time + 1f;
                if (!Nwh.EngineRunning(_car)) { _nextEngine = Time.time + 5f; Nwh.StartEngine(_car); Plugin.Verbose("Pilot: engine off, StartEngine()"); }
            }

            // pushing slowly against something for a while counts as stuck (a car being shoved, a fence...)
            if (_pushStamp >= 0f && Time.fixedTime - _pushStamp > 0.25f) { _pushTime = 0f; _pushStamp = -1f; }
            if (_pushTime > 1f && (_state == PilotState.Charge || _state == PilotState.Overshoot || _state == PilotState.Turnaround))
            { _pushTime = 0f; _pushStamp = -1f; StartRecover("pushing against " + _pushName, _hitSide); }

            // collision that came in since the last step
            if (_hitPending)
            {
                _hitPending = false;
                if (_hitIsTarget)
                {
                    if (_state == PilotState.Charge || _state == PilotState.Turnaround) StartOvershoot("rammed the target");
                }
                else if (_state != PilotState.Recover && _state != PilotState.Wait)
                    StartRecover("hit an obstacle" + (_hitSide != 0f ? (_hitSide > 0f ? " on the right" : " on the left") : " head-on"), _hitSide);
            }

            if (!_hasTarget || _dist > Plugin.AiGiveUpDistance.Value)
            {
                if (_state != PilotState.Idle) Enter(PilotState.Idle, _hasTarget ? "target too far" : "no target");
            }
            else if (_state == PilotState.Idle) Enter(PilotState.Charge, "target in range");

            switch (_state)
            {
                case PilotState.Charge: StepCharge(tpos, tvel, speed, forwardSpeed, dt, false); break;
                case PilotState.Turnaround: StepCharge(tpos, tvel, speed, forwardSpeed, dt, true); break;
                case PilotState.Overshoot: StepOvershoot(speed, forwardSpeed, dt); break;
                case PilotState.Recover: StepRecover(forwardSpeed, dt); break;
                case PilotState.Wait:
                    Apply(0f, MoveSteer(0f, dt), 0.4f);
                    if (_stateTime >= Plugin.AiWaitSeconds.Value) { _recoverCount = 0; Enter(PilotState.Charge, "waited"); }
                    break;
                case PilotState.Idle:
                    Apply(0f, MoveSteer(0f, dt), speed > 1f ? 0.2f : 0f);
                    break;
            }

            if (Plugin.VerboseLog.Value && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Plugin.Verbose("Pilot: " + _state + " " + (speed * 3.6f).ToString("0") + " km/h (fwd " + (forwardSpeed * 3.6f).ToString("0") + ") gear " + Nwh.Gear(_car)
                    + " target " + _dist.ToString("0") + " m at " + _angle.ToString("0") + "° steer " + _steer.ToString("0.00")
                    + " thr " + _throttle.ToString("0.00") + " brk " + _brakes.ToString("0.00") + Feelers()
                    + " w=" + (_rb != null ? _rb.angularVelocity.magnitude.ToString("0.0") : "?") + " up.y=" + _tf.up.y.ToString("0.00"));
            }
        }

        // ------------------------------------------------------------ states

        private void StepCharge(Vector3 tpos, Vector3 tvel, float speed, float forwardSpeed, float dt, bool turning)
        {
            if (Nwh.GearIndex(_car) <= 0) Nwh.ShiftInto(_car, 1);

            // intercept point, re-sampled every CommitSeconds so the car commits to a heading instead of twitching
            if (Time.time >= _nextAim)
            {
                _nextAim = Time.time + Plugin.AiCommitSeconds.Value;
                float lead = Mathf.Min(Plugin.AiLeadTime.Value, _dist / Mathf.Max(speed, 3f));
                var leadVec = Flat(tvel) * lead;
                if (leadVec.magnitude > 25f) leadVec = leadVec.normalized * 25f;
                _aim = tpos + leadVec;
                if (!_ramsTarget)
                {
                    // drive-by: aim beside the target, on the side it already is relative to our nose (the smaller turn)
                    var toT = Flat(tpos - _tf.position);
                    var lateral = toT.sqrMagnitude > 1e-3f ? Vector3.Cross(Vector3.up, toT.normalized) : _tf.right;   // right of the line to it
                    _aim -= lateral * _passSide * Plugin.AiDriveByOffset.Value;
                }
            }
            var toAim = Flat(_aim - _tf.position);
            float aimAngle = toAim.sqrMagnitude > 1e-3f ? Vector3.SignedAngle(Flat(_tf.forward), toAim.normalized, Vector3.up) : 0f;
            float absAngle = Mathf.Abs(aimAngle);

            float desired = Mathf.Clamp(aimAngle / Plugin.AiSteerAngle.Value, -1f, 1f);
            if (turning && absAngle > 20f) desired = Mathf.Sign(aimAngle);           // full lock until we face them again

            // obstacle feelers; the target itself is never an obstacle, and close to it we go straight for the ram
            float avoidSteer, avoidThrottle, avoidBrake;
            Sense(speed, out avoidSteer, out avoidThrottle, out avoidBrake);
            // ...unless something (a parked car, a rock) sits between us and the target: the centre feeler never sees the target itself
            bool ramming = _ramsTarget && _dist < Plugin.AiRamDistance.Value && Mathf.Abs(_angle) < 35f && (_feelerHit[0] < 0f || _feelerHit[0] > _dist);
            if (!ramming) desired = Mathf.Clamp(desired + avoidSteer, -1f, 1f);

            float maxSteer = MaxSteerFor(speed);
            desired = Mathf.Clamp(desired, -maxSteer, maxSteer);
            float steer = MoveSteer(desired, dt);

            float max = Plugin.AiThrottle.Value;
            float taper = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01((absAngle - 20f) / 70f)) ;   // ease off when pointing well away...
            float throttle = max * Mathf.Lerp(1f, taper, Mathf.InverseLerp(4f, 10f, speed));   // ...but only once rolling (need speed to turn)
            float brakes = 0f;
            if (speed > Plugin.AiTurnSafeSpeed.Value && absAngle > 40f) { throttle = 0f; brakes = 0.5f; }
            if (turning && speed > 6f) throttle = Mathf.Min(throttle, max * 0.7f);
            if (!ramming) { throttle *= avoidThrottle; brakes = Mathf.Max(brakes, avoidBrake); }
            Apply(throttle, steer, brakes);

            // passed the target: it is behind us and close to our track
            if (!turning)
            {
                var to = Flat(tpos - _tf.position);
                float along = Vector3.Dot(to, Flat(_tf.forward).normalized);
                float lateral = Vector3.Cross(Flat(_tf.forward).normalized, to).magnitude;
                if (along < -1f && lateral < Plugin.AiPassWidth.Value && _dist < Plugin.AiPassWidth.Value * 2f && forwardSpeed > 2f)
                { StartOvershoot("passed the target"); return; }
            }
            else
            {
                if (absAngle < 25f) { Enter(PilotState.Charge, "facing the target again"); return; }
                // cannot make the turn (too slow, way off): three-point turn
                if (speed < 2.5f && absAngle > 90f) _turnSlow += dt; else _turnSlow = 0f;
                if (_turnSlow > 2f) { _turnSlow = 0f; StartRecover("three-point turn", 0f); return; }
            }

            Stuck(throttle, speed, dt);
        }

        private void StepOvershoot(float speed, float forwardSpeed, float dt)
        {
            if (Nwh.GearIndex(_car) <= 0) Nwh.ShiftInto(_car, 1);
            float avoidSteer, avoidThrottle, avoidBrake;
            Sense(speed, out avoidSteer, out avoidThrottle, out avoidBrake);
            float steer = MoveSteer(Mathf.Clamp(avoidSteer, -MaxSteerFor(speed), MaxSteerFor(speed)), dt);
            Apply(Plugin.AiThrottle.Value * avoidThrottle, steer, avoidBrake);
            float ran = Flat(_tf.position - _runStart).magnitude;
            if (ran >= Plugin.AiRunOutMeters.Value || _stateTime >= Plugin.AiRunOutMaxSeconds.Value)
            { Enter(PilotState.Turnaround, "ran out " + ran.ToString("0") + " m"); return; }
            Stuck(Plugin.AiThrottle.Value * avoidThrottle, speed, dt);
        }

        private void StepRecover(float forwardSpeed, float dt)
        {
            // something behind: stop backing up, go forward instead (the wheels are already turned toward the target)
            _rearClear = SenseRear(forwardSpeed);
            if (_rearHit || _rearClear >= 0f)
            {
                string why = _rearHit ? "hit something behind" : "obstacle " + _rearClear.ToString("0.0") + " m behind";
                _rearHit = false;
                if (Nwh.GearIndex(_car) < 0) Nwh.ShiftInto(_car, 1);
                _steer = -_steer;                         // nose was swinging one way in reverse; keep swinging it that way going forward
                Enter(PilotState.Charge, why);
                return;
            }
            // reverse = gear -1 + throttle (NWH's automatic takes the reverse gear from input.ShiftInto)
            if (Nwh.GearIndex(_car) >= 0) Nwh.ShiftInto(_car, -1);
            Apply(Plugin.AiReverseThrottle.Value, MoveSteer(_recoverSteer, dt), 0f);
            if (_stateTime >= _recoverDur)
            {
                if (Nwh.GearIndex(_car) < 0) Nwh.ShiftInto(_car, 1);
                Enter(PilotState.Charge, "reversed " + _recoverDur.ToString("0.0") + " s");
            }
        }

        // ------------------------------------------------------------ transitions

        private void StartOvershoot(string why)
        {
            _runStart = _tf.position;
            Enter(PilotState.Overshoot, why);
        }

        private void StartRecover(string why, float obstacleSide)
        {
            bool repeat = Time.time - _lastRecover < Plugin.AiRecoverWindow.Value;
            _recoverCount = repeat ? _recoverCount + 1 : 1;
            _lastRecover = Time.time;
            if (_recoverCount > Plugin.AiMaxRecovers.Value)
            {
                Apply(0f, _steer, 0.5f);
                var crew = _car.GetComponent<Crew>();
                if (crew != null && crew.OnStuck()) { _abandoned = true; return; }   // the crew got out; this component is being destroyed
                Enter(PilotState.Wait, why + " (" + _recoverCount + " recoveries)");
                return;
            }
            _recoverDur = Plugin.AiReverseSeconds.Value * (1f + 0.5f * (_recoverCount - 1));
            // reversing with the wheels turned to +s swings the nose to -s: away from an obstacle on side s, or toward the target
            float s = obstacleSide != 0f ? Mathf.Sign(obstacleSide) : -Mathf.Sign(_angle == 0f ? 1f : _angle);
            if (_recoverCount >= 3) s = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            _recoverSteer = s * 0.9f;
            _stuckTime = 0f;
            Enter(PilotState.Recover, why);
        }

        private void Enter(PilotState s, string why)
        {
            if (_state != s || _stateTime > 0f) Plugin.Verbose("Pilot: " + _state + " -> " + s + " (" + why + ")");
            if (s == PilotState.Charge) _passSide = _angle > 1f ? 1f : _angle < -1f ? -1f : (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            _state = s; _stateTime = 0f; _why = why; _turnSlow = 0f;
            if (s != PilotState.Recover) _stuckTime = 0f;
            _nextAim = 0f;
        }

        // Not moving in a forward state for StuckSeconds = stuck (whatever the pedals say: the feelers may be holding the car
        // in front of a wall with the throttle cut, and that is exactly when it has to back out).
        private void Stuck(float throttle, float speed, float dt)
        {
            if (speed < 0.5f) _stuckTime += dt; else _stuckTime = 0f;
            if (_stuckTime >= Plugin.AiStuckSeconds.Value) { _stuckTime = 0f; StartRecover("stuck " + Plugin.AiStuckSeconds.Value + " s", 0f); }
        }

        // ------------------------------------------------------------ steering / input helpers

        private float MaxSteerFor(float speed)
        {
            return Mathf.Lerp(1f, Plugin.AiMaxSteerAtSpeed.Value, Mathf.InverseLerp(5f, 20f, speed));
        }

        private float MoveSteer(float desired, float dt)
        {
            _steer = Mathf.MoveTowards(_steer, desired, Plugin.AiSteerRate.Value * dt);
            return _steer;
        }

        private void Apply(float throttle, float steer, float brakes)
        {
            _throttle = throttle; _brakes = brakes;
            Nwh.SetInput(_car, throttle, Plugin.AiInvertSteering.Value ? -steer : steer, brakes);
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        // ------------------------------------------------------------ obstacle feelers

        // Casts the front feelers and the cliff probe; returns a steering correction (+ = right), a throttle factor and a brake amount.
        private void Sense(float speed, out float steer, out float throttleFactor, out float brake)
        {
            // every other physics step (25 Hz is plenty for a car; halves the raycasts of a convoy); the skipped step reuses the last result
            if ((++_senseStep & 1) == 1) { steer = _senseSteer; throttleFactor = _senseThrottle; brake = _senseBrake; return; }
            SenseNow(speed, out steer, out throttleFactor, out brake);
            _senseSteer = steer; _senseThrottle = throttleFactor; _senseBrake = brake;
        }

        private void SenseNow(float speed, out float steer, out float throttleFactor, out float brake)
        {
            steer = 0f; throttleFactor = 1f; brake = 0f;
            float range = Plugin.AiFeelerRange.Value + speed * Plugin.AiFeelerSpeedFactor.Value;
            var origin = _tf.position + _tf.forward * Plugin.AiFrontOffset.Value + _tf.up * 0.8f;
            if (_rb != null) origin = _rb.worldCenterOfMass + _tf.forward * Plugin.AiFrontOffset.Value + _tf.up * 0.5f;
            float minSlopeNormalY = Mathf.Cos(Plugin.AiMaxSlopeDeg.Value * Mathf.Deg2Rad);

            if (Time.time > _cachePrune) PruneObstacleCache();

            float leftRoom = 0f, rightRoom = 0f;
            for (int i = 0; i < _feelerAngle.Length; i++)
            {
                float a = _feelerAngle[i];
                float len = range * (a == 0f ? 1f : Mathf.Abs(a) < 30f ? 0.75f : 0.5f);
                _feelerLen[i] = len;
                var dir = Quaternion.AngleAxis(a, _tf.up) * _tf.forward;
                _feelerHit[i] = Cast(origin, dir, len, a == 0f ? 0.4f : 0.2f, minSlopeNormalY);
                float d = _feelerHit[i] < 0f ? len : _feelerHit[i];
                if (a < 0f) leftRoom += d; else if (a > 0f) rightRoom += d;
            }

            // cliff / steep drop probe: a point ahead with no ground under it counts as a centre obstacle
            float probeAhead = 4f + speed * 0.8f;
            var probe = _tf.position + Flat(_tf.forward).normalized * probeAhead + Vector3.up * 3f;
            _cliffAt = -1f;
            if (!GroundBelow(probe, 15f)) { _cliffAt = probeAhead; if (_feelerHit[0] < 0f || _feelerHit[0] > probeAhead) _feelerHit[0] = probeAhead; }

            float gain = Plugin.AiAvoidGain.Value;
            for (int i = 1; i < _feelerAngle.Length; i++)
            {
                if (_feelerHit[i] < 0f) continue;
                float w = 1f - _feelerHit[i] / _feelerLen[i];
                steer += (_feelerAngle[i] < 0f ? 1f : -1f) * w * gain * (Mathf.Abs(_feelerAngle[i]) < 30f ? 1f : 0.6f);
            }
            if (_feelerHit[0] >= 0f)
            {
                float w = 1f - _feelerHit[0] / _feelerLen[0];
                steer += (rightRoom >= leftRoom ? 1f : -1f) * w * gain * 1.5f;
                throttleFactor = Mathf.Clamp(_feelerHit[0] / _feelerLen[0] + 0.2f, 0.2f, 1f);
                float brakeDist = 3f + speed * 0.6f;
                if (_feelerHit[0] < brakeDist && speed > 4f) { throttleFactor = 0f; brake = 0.6f; }
            }
            steer = Mathf.Clamp(steer, -1f, 1f);
        }

        // Three rays backwards from the tail while reversing; returns the nearest obstacle distance or -1.
        private float SenseRear(float forwardSpeed)
        {
            float len = 2.5f + Mathf.Abs(Mathf.Min(forwardSpeed, 0f)) * 0.8f;
            var origin = (_rb != null ? _rb.worldCenterOfMass : _tf.position) - _tf.forward * Plugin.AiFrontOffset.Value + _tf.up * 0.5f;
            float minSlopeNormalY = Mathf.Cos(Plugin.AiMaxSlopeDeg.Value * Mathf.Deg2Rad);
            float best = -1f;
            float[] angles = { 180f, 150f, 210f };
            for (int i = 0; i < angles.Length; i++)
            {
                var dir = Quaternion.AngleAxis(angles[i], _tf.up) * _tf.forward;
                float d = Cast(origin, dir, i == 0 ? len : len * 0.7f, i == 0 ? 0.4f : 0.2f, minSlopeNormalY);
                if (d >= 0f && (best < 0f || d < best)) best = d;
            }
            return best;
        }

        // first hit along the ray that counts as an obstacle, or -1
        private float Cast(Vector3 origin, Vector3 dir, float len, float radius, float minSlopeNormalY)
        {
            int n = radius > 0f
                ? Physics.SphereCastNonAlloc(origin, radius, dir, _hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(origin, dir, _hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = -1f;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (best >= 0f && h.distance >= best) continue;
                if (h.collider == null) continue;
                if (h.distance < 0.05f) continue;                      // overlap at the start of the cast (Unity reports distance 0, a sideways normal)
                if (h.normal.y >= minSlopeNormalY) continue;           // ground / gentle slope, drivable
                if (!IsObstacle(h.collider)) continue;
                best = h.distance;
            }
            return best;
        }

        private bool GroundBelow(Vector3 from, float depth)
        {
            int n = Physics.RaycastNonAlloc(from, Vector3.down, _hits, depth, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || c.transform.IsChildOf(_tf)) continue;
                return true;
            }
            return false;
        }

        // What the car should NOT steer around: itself, other patrol cars' occupants... no - the player, the player's car,
        // any creature/NPC (they have a Health FSM) and loose light items. Everything else (terrain walls, rocks, buildings,
        // wrecks, parked cars) is an obstacle.
        private bool IsObstacle(Collider c)
        {
            Verdict v;
            if (_obstacleCache.TryGetValue(c, out v) && Time.time < v.Until) return v.Obstacle;
            v.Obstacle = Classify(c);
            v.Until = Time.time + 5f;
            _obstacleCache[c] = v;
            return v.Obstacle;
        }

        // every 30 s: drop verdicts nobody asked for again (destroyed colliders, things passed long ago)
        private void PruneObstacleCache()
        {
            _cachePrune = Time.time + 30f;
            _expired.Clear();
            foreach (var kv in _obstacleCache) if (Time.time >= kv.Value.Until) _expired.Add(kv.Key);
            foreach (var c in _expired) _obstacleCache.Remove(c);
        }

        private bool Classify(Collider c)
        {
            var t = c.transform;
            if (t.IsChildOf(_tf)) return false;
            int kind = PlayerRef.Kind(t);
            if (kind == 1) return _rams < RamTargets.Pedestrians;          // the player on foot
            if (kind == 2) return _rams < RamTargets.Cars;                 // the player's car
            bool creature = false;
            for (var a = t; a != null; a = a.parent)
            {
                if (PlayerRef.HasVehicleController(a)) return _rams < RamTargets.Cars;   // another car (parked, wreck, patrol)
                foreach (var f in a.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "Health" || f.FsmName == "Detection") creature = true;
            }
            if (creature) return _rams < RamTargets.Pedestrians;         // creature / NPC: run it over, or avoid it
            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.mass < Plugin.AiIgnoreMassBelow.Value) return false;   // loose item
            return true;
        }

        private string Feelers()
        {
            var s = " feelers";
            for (int i = 0; i < _feelerAngle.Length; i++) s += " " + (_feelerHit[i] < 0f ? "-" : _feelerHit[i].ToString("0.0"));
            if (_cliffAt >= 0f) s += " CLIFF@" + _cliffAt.ToString("0");
            return s;
        }

        // ------------------------------------------------------------ collisions (the car's Rigidbody is on the root, so they arrive here)

        private void OnCollisionEnter(Collision col)
        {
            if (col.collider == null || col.contactCount == 0) return;
            float rel = col.relativeVelocity.magnitude;
            if (rel < 3f) return;
            var contact = col.GetContact(0);
            var local = _tf.InverseTransformPoint(contact.point);
            if (contact.normal.y >= Mathf.Cos(Plugin.AiMaxSlopeDeg.Value * Mathf.Deg2Rad)) return;   // ground / bump under the car, not a wall
            float along = Vector3.Dot(contact.normal, _tf.forward);
            bool frontal = local.z > 0.3f && along < -0.3f;
            bool rear = local.z < -0.3f && along > 0.3f;
            if (!frontal && !rear) return;                                // glancing / side, not something in the way
            int kind = PlayerRef.Kind(col.collider.transform);
            bool target = kind != 0 && !IsObstacle(col.collider);         // the player / their car, and we are allowed to ram it
            bool npc = !target && !IsObstacle(col.collider);
            if (npc) return;                                              // ran over a creature or a loose item: keep going
            if (rear)
            {
                if (_state == PilotState.Recover && !target) { _rearHit = true; Plugin.Verbose("Pilot: backed into " + col.collider.name + " at " + rel.ToString("0.0") + " m/s"); }
                return;
            }
            _hitPending = true;
            _hitIsTarget = target;
            _hitSide = Mathf.Abs(local.x) > 0.6f ? Mathf.Sign(local.x) : 0f;
            Plugin.Verbose("Pilot: " + (target ? "rammed " : "hit ") + col.collider.name + " at " + rel.ToString("0.0") + " m/s, local " + local.ToString("0.0"));
        }

        // A slow, sustained frontal contact with an obstacle (shoving a parked car, leaning on a wall) is tracked here and
        // turned into a recovery by Step() once it lasts a second; a single Enter at speed is handled above.
        private void OnCollisionStay(Collision col)
        {
            if (col.collider == null || col.contactCount == 0) return;
            if (_state != PilotState.Charge && _state != PilotState.Overshoot && _state != PilotState.Turnaround) return;
            if (_throttle < 0.3f) return;
            var contact = col.GetContact(0);
            var local = _tf.InverseTransformPoint(contact.point);
            if (local.z < 0.3f || Vector3.Dot(contact.normal, _tf.forward) > -0.3f) return;
            if (contact.normal.y >= Mathf.Cos(Plugin.AiMaxSlopeDeg.Value * Mathf.Deg2Rad)) return;
            if (_rb != null && Vector3.Dot(_rb.velocity, _tf.forward) > 2f) return;    // still making progress
            if (!IsObstacle(col.collider)) return;
            _pushTime += Time.fixedDeltaTime;
            _pushStamp = Time.fixedTime;
            _pushName = col.collider.name;
            _hitSide = Mathf.Abs(local.x) > 0.6f ? Mathf.Sign(local.x) : 0f;
        }

        // ------------------------------------------------------------ overlay

        private void OnGUI()
        {
            if (!Plugin.AiOverlay.Value || Time.timeScale <= 0f) return;
            int line = 0;
            foreach (var t in _ownRoots) { if (t == _tf) break; line++; }
            var vel = _rb != null ? _rb.velocity : Vector3.zero;
            string s = _car.name + ": " + _state + " (" + _why + ")  " + (vel.magnitude * 3.6f).ToString("0") + " km/h  gear " + Nwh.Gear(_car)
                + "  target " + _dist.ToString("0") + " m @ " + _angle.ToString("0") + "°  steer " + _steer.ToString("0.00")
                + "  thr " + _throttle.ToString("0.00") + "  brk " + _brakes.ToString("0.00") + Feelers()
                + "  recovers " + _recoverCount
                + (_state == PilotState.Recover ? "  rear " + (_rearClear < 0f ? "clear" : _rearClear.ToString("0.0") + " m") : "")
                + (_pushTime > 0f ? "  push " + _pushTime.ToString("0.0") + " s " + _pushName : "");
            GUI.Label(new Rect(10, 10 + 18 * line, 1400, 22), s);
        }
    }

    // Cached references to the player and, while driving, the player's car. Refreshed lazily; reset on scene load.
    internal static class PlayerRef
    {
        private static Transform _player;
        private static float _nextFind;
        private static GameObject _playerCar;
        private static PlayMakerFSM _playerCarDrive;
        private static PlayMakerFSM _inCar;             // Player [InCar]: state InCar / OnFoot, var Car = the car the player sits in
        private static HutongGames.PlayMaker.FsmGameObject _inCarVar;
        private static Transform _inCarOwner;
        private static Vector3 _lastPos, _vel;
        private static float _lastStamp = -1f;

        internal static void Reset() { _player = null; _playerCar = null; _playerCarDrive = null; _inCar = null; _inCarVar = null; _inCarOwner = null; _nextFind = 0f; _lastStamp = -1f; }

        internal static Transform Player
        {
            get
            {
                if (_player == null && Time.unscaledTime >= _nextFind)
                {
                    _nextFind = Time.unscaledTime + 2f;
                    var go = GameObject.Find("Player");
                    _player = go != null ? go.transform : null;
                }
                return _player;
            }
        }

        // The car the player is driving, or null. The Player's own InCar FSM says so (state InCar, variable Car); nothing is
        // scanned. Fallback while that FSM is not there yet: the Player object is parented under <car>/DriveTrigger/sitPos while driving.
        internal static GameObject PlayerCar
        {
            get
            {
                var p = Player;
                if (p == null) { _playerCar = null; _playerCarDrive = null; return null; }
                if (InCarFsm(p) && _inCar.Fsm.Initialized)
                {
                    if (_inCar.ActiveStateName != "InCar") { _playerCar = null; _playerCarDrive = null; return null; }
                    if (_inCarVar == null) _inCarVar = _inCar.FsmVariables.GetFsmGameObject("Car");
                    var car = _inCarVar != null ? _inCarVar.Value : null;
                    if (car != null)
                    {
                        if (car != _playerCar) { _playerCar = car; _playerCarDrive = Patrol.FindFsm(car, "DriveTrigger", "Drive"); }
                        return _playerCar;
                    }
                }
                if (_playerCar != null && _playerCarDrive != null && _playerCarDrive.Fsm.Initialized && _playerCarDrive.ActiveStateName == "inCar") return _playerCar;
                _playerCar = null; _playerCarDrive = null;
                for (var a = p.parent; a != null; a = a.parent)
                    if (HasVehicleController(a)) { _playerCar = a.gameObject; _playerCarDrive = Patrol.FindFsm(_playerCar, "DriveTrigger", "Drive"); return _playerCar; }
                return null;
            }
        }

        private static bool InCarFsm(Transform p)
        {
            if (_inCar != null && _inCarOwner == p) return true;
            _inCar = null; _inCarVar = null; _inCarOwner = null;
            foreach (var f in p.GetComponents<PlayMakerFSM>()) if (f.FsmName == "InCar") { _inCar = f; break; }
            if (_inCar == null) return false;
            _inCarOwner = p;
            return true;
        }

        internal static bool HasVehicleController(Transform t)
        {
            foreach (var c in t.GetComponents<Component>())
                if (c != null && c.GetType().Name == "VehicleController") return true;
            return false;
        }

        // Position and velocity of what an AI car should ram: the player's car while driving, the player on foot otherwise.
        internal static bool Target(out Vector3 pos, out Vector3 vel, out GameObject car)
        {
            car = PlayerCar;
            var p = Player;
            if (car != null)
            {
                var rb = car.GetComponent<Rigidbody>();
                pos = rb != null ? rb.worldCenterOfMass : car.transform.position;
                vel = rb != null ? rb.velocity : Vector3.zero;
                return true;
            }
            if (p == null) { pos = Vector3.zero; vel = Vector3.zero; return false; }
            pos = p.position;
            // on foot: velocity from the position delta, once per physics step (several pilots may ask)
            if (_lastStamp != Time.fixedTime)
            {
                float dt = _lastStamp < 0f ? 0f : Time.fixedTime - _lastStamp;
                if (dt > 0f && dt < 1f) { var v = (pos - _lastPos) / dt; _vel = Vector3.Lerp(_vel, v, 0.3f); }
                else _vel = Vector3.zero;
                _lastPos = pos; _lastStamp = Time.fixedTime;
            }
            vel = _vel;
            return true;
        }

        // 0 = neither, 1 = the player on foot, 2 = the car the player is driving
        internal static int Kind(Transform t)
        {
            if (t == null) return 0;
            var p = _player;                       // no lookup here; a null player means nothing to ram anyway
            var car = _playerCar;
            if (car != null && t.IsChildOf(car.transform)) return 2;
            // the player object may not be parented under its car: anything the player is inside of counts
            if (p != null && car == null)
            {
                for (var a = p.parent; a != null; a = a.parent)
                    if (t.IsChildOf(a) && HasVehicleController(a)) return 2;
            }
            if (p != null && (t == p || t.IsChildOf(p))) return car != null ? 2 : 1;
            return 0;
        }
    }
}
