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
        private RamTargets _ramsBase = RamTargets.Pedestrians;   // from the car's template (PatrolMarker)
        private PatrolMarker _marker; private float _nextWarboyCheck; private bool _warboy;
        // 2.2.5: what it rams now - the template's setting, but Cars while at least one Warboy rides on it (a Warboy is only useful
        // close to the player's car); back to the template's when the last one has leapt, jumped off or died. Re-checked twice a second.
        private RamTargets _rams
        {
            get
            {
                if (Time.time >= _nextWarboyCheck)
                {
                    _nextWarboyCheck = Time.time + 0.5f;
                    bool w = false;
                    if (_marker == null && _car != null) _marker = _car.GetComponent<PatrolMarker>();
                    if (_marker != null) foreach (var r in _marker.Riders) { var g = r != null ? r.GetComponent<RiderGuard>() : null; if (g != null && g.IsWarboy) { w = true; break; } }
                    if (w != _warboy) { _warboy = w; Plugin.Verbose("Pilot: " + (_car != null ? _car.name : "?") + (w ? " rams cars while its Warboy rides" : " back to ramming " + _ramsBase)); }
                }
                return _warboy && _ramsBase < RamTargets.Cars ? RamTargets.Cars : _ramsBase;
            }
        }
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
        internal static readonly List<Pilot> All = new List<Pilot>();     // every live pilot (the overlay draws from here)
        private static readonly RaycastHit[] _hits = new RaycastHit[24];   // non-allocating cast buffer (shared; casts never nest)
        private static readonly List<PlayMakerFSM> _fsmBuffer = new List<PlayMakerFSM>();   // non-allocating GetComponents buffer
        private int _senseStep;                                            // feelers run every 2nd physics step (every 4th far from the target)
        private float _senseSteer, _senseThrottle = 1f, _senseBrake;       // last feeler result, reused on the skipped steps
        private int _gear; private float _gearNext;                        // NWH gear, read by reflection 5x a second instead of every step
        // rollover guard (2.0.16): the lateral acceleration this chassis can take before it tips = g x half track / COM height (x margin),
        // from the wheel hinges and a ground ray under the centre of mass; the steering at speed is capped so v^2 x curvature stays under it,
        // the car brakes for a turn it cannot take at its speed, and a chassis already leaning outward gets its steering and gas cut
        private float _rollLimit = 8f, _wheelbase = 2.5f, _tanSteer = 0.7f, _nextGeometry;
        private float _roll;                                               // current body roll, degrees (+ = right side higher = leaning left)

        internal PilotState State { get { return _state; } }
        internal float EscortUntil;                                        // 2.1.4: a "Witness me!" rider wants a ride alongside the player until then
        private float _nextEscortLog;

        internal static Pilot Attach(GameObject car)
        {
            var p = car.GetComponent<Pilot>() ?? car.AddComponent<Pilot>();
            p._car = car; p._tf = car.transform; p._rb = car.GetComponent<Rigidbody>();
            var marker = car.GetComponent<PatrolMarker>();
            p._ramsBase = marker != null ? marker.Rams : RamTargets.Pedestrians;
            p._marker = marker;
            p.Enter(PilotState.Charge, "start");
            p._nextGeometry = 0f;
            if (!All.Contains(p)) All.Add(p);
            Plugin.Verbose("Pilot: driving AI on " + car.name + ", rams " + p._rams);
            return p;
        }

        internal void Detach()
        {
            All.Remove(this);
            Destroy(this);
        }

        private void OnDestroy() { All.Remove(this); }

        // the transmission's gear index, refreshed 5x a second (a reflection read + boxing per step per car otherwise)
        private int Gear()
        {
            if (Time.fixedTime >= _gearNext) { _gearNext = Time.fixedTime + 0.2f; _gear = Nwh.GearIndex(_car); }
            return _gear;
        }

        private void Shift(int gear) { Nwh.ShiftInto(_car, gear); _gear = gear; _gearNext = Time.fixedTime + 0.2f; }   // assume it lands; re-read shortly

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
            if (Time.time >= _nextGeometry) { _nextGeometry = Time.time + 10f; Geometry(); }
            _roll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(_tf.right, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;

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

            bool escort = Time.time < EscortUntil && tcar != null && _hasTarget
                && (_state == PilotState.Charge || _state == PilotState.Turnaround || _state == PilotState.Overshoot);
            if (escort) StepEscort(tpos, tvel, speed, forwardSpeed, dt);
            else switch (_state)
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

        // 2.1.4 escort: the rider on the roof is about to leap ("Witness me!"), so the driver runs alongside the player's car - same heading,
        // same speed, a lane 5 m to the side it is already on - instead of ramming or passing. Player (nearly) stopped, or we face the other
        // way: the normal charge / turnaround until we are going the same way.
        private void StepEscort(Vector3 tpos, Vector3 tvel, float speed, float forwardSpeed, float dt)
        {
            var tv = Flat(tvel); float ts = tv.magnitude;
            var fwd = Flat(_tf.forward).normalized;
            if (ts < 3f) { StepCharge(tpos, tvel, speed, forwardSpeed, dt, false); return; }
            var dir = tv / ts;
            if (Vector3.Dot(fwd, dir) < -0.2f) { StepCharge(tpos, tvel, speed, forwardSpeed, dt, _state == PilotState.Turnaround); return; }
            if (Gear() <= 0) Shift(1);

            var right = Vector3.Cross(Vector3.up, dir);
            var rel = Flat(_tf.position - tpos);
            float side = Vector3.Dot(rel, right) >= 0f ? 1f : -1f;
            float along = Vector3.Dot(rel, dir);                                   // + = ahead of the player
            float ahead = Mathf.Max(along, -25f) + 8f + speed * 0.4f;              // a point on our lane a bit ahead of us
            var lanePoint = tpos + right * side * 5f + dir * ahead;
            var toAim = Flat(lanePoint - _tf.position);
            float aimAngle = toAim.sqrMagnitude > 1e-3f ? Vector3.SignedAngle(fwd, toAim.normalized, Vector3.up) : 0f;

            float desired = Mathf.Clamp(aimAngle / Plugin.AiSteerAngle.Value, -1f, 1f);
            float avoidSteer, avoidThrottle, avoidBrake;
            Sense(speed, out avoidSteer, out avoidThrottle, out avoidBrake);
            desired = Mathf.Clamp(desired + avoidSteer, -1f, 1f);
            float maxSteer = Mathf.Min(MaxSteerFor(speed), RollSafeSteer(speed));
            float wanted = desired;
            desired = Mathf.Clamp(desired, -maxSteer, maxSteer);
            float steer = MoveSteer(desired, dt);

            // speed: the player's, plus catching up from behind / dropping back when ahead (abreast = along 0)
            float want = ts + Mathf.Clamp(-along * 0.4f, -6f, 8f);
            float err = want - forwardSpeed;
            float max = Plugin.AiThrottle.Value;
            float throttle = err > 0f ? max * Mathf.Clamp(err * 0.3f, 0.15f, 1f) : 0f;
            float brakes = err < -2f ? Mathf.Clamp(-err * 0.08f, 0f, 0.6f) : 0f;
            if (Mathf.Abs(wanted) > 0.15f && speed > 4f && LateralFor(speed, Mathf.Abs(wanted)) > _rollLimit) { throttle = 0f; brakes = Mathf.Max(brakes, 0.35f); }
            throttle *= avoidThrottle; brakes = Mathf.Max(brakes, avoidBrake);
            LeanGuard(ref steer, ref throttle, ref brakes);
            Apply(throttle, steer, brakes);
            if (Plugin.VerboseLog.Value && Time.time >= _nextEscortLog)
            {
                _nextEscortLog = Time.time + 2f;
                Plugin.Verbose("Pilot: " + _car.name + " escorts the player for its rider: " + along.ToString("0") + " m " + (along >= 0f ? "ahead" : "behind")
                    + ", " + (side > 0f ? "right" : "left") + " side, " + (forwardSpeed * 3.6f).ToString("0") + "/" + (want * 3.6f).ToString("0") + " km/h");
            }
        }

        private void StepCharge(Vector3 tpos, Vector3 tvel, float speed, float forwardSpeed, float dt, bool turning)
        {
            if (Gear() <= 0) Shift(1);

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

            float maxSteer = Mathf.Min(MaxSteerFor(speed), RollSafeSteer(speed));
            float wanted = desired;                                                     // before the caps: how hard it needs to turn
            desired = Mathf.Clamp(desired, -maxSteer, maxSteer);
            float steer = MoveSteer(desired, dt);

            float max = Plugin.AiThrottle.Value;
            float taper = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01((absAngle - 20f) / 70f)) ;   // ease off when pointing well away...
            float throttle = max * Mathf.Lerp(1f, taper, Mathf.InverseLerp(4f, 10f, speed));   // ...but only once rolling (need speed to turn)
            float brakes = 0f;
            if (speed > Plugin.AiTurnSafeSpeed.Value && absAngle > 40f) { throttle = 0f; brakes = 0.5f; }
            // the turn it wants would tip it at this speed: off the gas and brake until the speed fits the turn (tall, lifted chassis)
            if (Mathf.Abs(wanted) > 0.15f && speed > 4f && LateralFor(speed, Mathf.Abs(wanted)) > _rollLimit) { throttle = 0f; brakes = Mathf.Max(brakes, 0.35f); }
            if (turning && speed > 6f) throttle = Mathf.Min(throttle, max * 0.7f);
            if (!ramming) { throttle *= avoidThrottle; brakes = Mathf.Max(brakes, avoidBrake); }
            LeanGuard(ref steer, ref throttle, ref brakes);
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
            if (Gear() <= 0) Shift(1);
            float avoidSteer, avoidThrottle, avoidBrake;
            Sense(speed, out avoidSteer, out avoidThrottle, out avoidBrake);
            float cap = Mathf.Min(MaxSteerFor(speed), RollSafeSteer(speed));
            float steer = MoveSteer(Mathf.Clamp(avoidSteer, -cap, cap), dt);
            float throttle = Plugin.AiThrottle.Value * avoidThrottle, brakes = avoidBrake;
            LeanGuard(ref steer, ref throttle, ref brakes);
            Apply(throttle, steer, brakes);
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
                if (Gear() < 0) Shift(1);
                _steer = -_steer;                         // nose was swinging one way in reverse; keep swinging it that way going forward
                Enter(PilotState.Charge, why);
                return;
            }
            // reverse = gear -1 + throttle (NWH's automatic takes the reverse gear from input.ShiftInto)
            if (Gear() >= 0) Shift(-1);
            Apply(Plugin.AiReverseThrottle.Value, MoveSteer(_recoverSteer, dt), 0f);
            if (_stateTime >= _recoverDur)
            {
                if (Gear() < 0) Shift(1);
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

        // ------------------------------------------------------------ rollover guard

        // Track width (hinge_wheel_FL <-> FR), wheelbase (FL <-> RL) and the centre of mass height over the ground (a ray down from the
        // COM past the car's own colliders) -> the lateral acceleration the chassis takes before the inner wheels lift: g x (track/2) / h,
        // with a 0.8 margin. A low PipeRat lands near 10 m/s^2 (above the tyres' grip: no change to how it drives); a lifted one on truck
        // wheels with plates on the roof around 6, which is what tipped it. Also NWH's maximum steer angle for the curvature of a steering input.
        private void Geometry()
        {
            try
            {
                var fl = Patrol.FindChild(_tf, "hinge_wheel_FL"); var fr = Patrol.FindChild(_tf, "hinge_wheel_FR"); var rl = Patrol.FindChild(_tf, "hinge_wheel_RL");
                float track = fl != null && fr != null ? Mathf.Abs(Vector3.Dot(fr.position - fl.position, _tf.right)) : 1.5f;
                if (fl != null && rl != null) _wheelbase = Mathf.Max(1.2f, Mathf.Abs(Vector3.Dot(rl.position - fl.position, _tf.forward)));
                var com = _rb != null ? _rb.worldCenterOfMass : _tf.position;
                float h = 0.7f;
                int n = Physics.RaycastNonAlloc(com + Vector3.up * 0.2f, Vector3.down, _hits, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                float best = -1f;
                for (int i = 0; i < n; i++) { var c = _hits[i].collider; if (c == null || c.transform.IsChildOf(_tf)) continue; if (best < 0f || _hits[i].distance < best) best = _hits[i].distance; }
                if (best >= 0f) h = Mathf.Clamp(best - 0.2f, 0.3f, 2.5f);
                _rollLimit = Mathf.Clamp(9.81f * (Mathf.Max(0.8f, track) * 0.5f) / h * 0.8f, 2.5f, 30f);
                float maxAngle = Nwh.MaxSteerAngle(_car);
                _tanSteer = Mathf.Tan(Mathf.Clamp(maxAngle > 1f ? maxAngle : 35f, 10f, 60f) * Mathf.Deg2Rad);
                if (Plugin.VerboseLog.Value) Plugin.Verbose("Pilot: " + _car.name + " track " + track.ToString("0.00") + " m, wheelbase " + _wheelbase.ToString("0.00") + " m, COM " + h.ToString("0.00") + " m up -> roll limit " + _rollLimit.ToString("0.0") + " m/s^2");
            }
            catch (Exception e) { Plugin.Verbose("Pilot: geometry: " + e.Message); }
        }

        // lateral acceleration (m/s^2) of this speed at this steering input (bicycle model: curvature = tan(steer x maxAngle) / wheelbase)
        private float LateralFor(float speed, float steer) { return speed * speed * (steer * _tanSteer) / _wheelbase; }

        // the steering input at which the lateral acceleration reaches the roll limit at this speed (1 = no cap)
        private float RollSafeSteer(float speed)
        {
            if (speed < 3f) return 1f;
            return Mathf.Clamp(_rollLimit * _wheelbase / (speed * speed * _tanSteer), 0.08f, 1f);
        }

        // The body leans outward in the turn (the inner wheels unload): from 5 degrees the steering and the gas are cut in proportion,
        // from 10 degrees the gas is off and it brakes, beyond 16 the wheel is centred - better a missed turn than a car on its roof.
        private void LeanGuard(ref float steer, ref float throttle, ref float brakes)
        {
            float lean = Mathf.Abs(_roll);
            if (lean < 5f) return;
            bool outward = Mathf.Sign(_roll) == Mathf.Sign(steer) && Mathf.Abs(steer) > 0.02f;   // right turn (+steer) lifts the right side (+roll)
            if (!outward && lean < 12f) return;                                                  // a slope, not the turn
            float cut = Mathf.Clamp01(1f - (lean - 5f) / 8f);
            steer *= cut; throttle *= cut;
            if (lean >= 10f) { throttle = 0f; brakes = Mathf.Max(brakes, 0.3f); }
            if (lean >= 16f) steer = 0f;
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
            // every other physics step (25 Hz is plenty for a car; halves the raycasts of a convoy), every 4th beyond 150 m from the
            // target (a far car has room; a convoy approaching from 350 m casts a quarter as much); the skipped steps reuse the last result
            int every = _dist > 150f ? 4 : 2;
            if ((++_senseStep % every) != 0) { steer = _senseSteer; throttleFactor = _senseThrottle; brake = _senseBrake; return; }
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

        private static readonly float[] RearAngles = { 180f, 150f, 210f };

        // Three rays backwards from the tail while reversing; returns the nearest obstacle distance or -1.
        private float SenseRear(float forwardSpeed)
        {
            float len = 2.5f + Mathf.Abs(Mathf.Min(forwardSpeed, 0f)) * 0.8f;
            var origin = (_rb != null ? _rb.worldCenterOfMass : _tf.position) - _tf.forward * Plugin.AiFrontOffset.Value + _tf.up * 0.5f;
            float minSlopeNormalY = Mathf.Cos(Plugin.AiMaxSlopeDeg.Value * Mathf.Deg2Rad);
            float best = -1f;
            var angles = RearAngles;
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
                _fsmBuffer.Clear();
                a.GetComponents(_fsmBuffer);
                for (int i = 0; i < _fsmBuffer.Count; i++)
                {
                    var f = _fsmBuffer[i];
                    if (f != null && (f.FsmName == "Health" || f.FsmName == "Detection")) creature = true;
                }
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

        // one line of the [Debug] AiOverlay (drawn by the runner's OnGUI for every pilot in All)
        internal string OverlayLine()
        {
            var vel = _rb != null ? _rb.velocity : Vector3.zero;
            return _car.name + ": " + _state + " (" + _why + ")  " + (vel.magnitude * 3.6f).ToString("0") + " km/h  gear " + Nwh.Gear(_car)
                + "  target " + _dist.ToString("0") + " m @ " + _angle.ToString("0") + "°  steer " + _steer.ToString("0.00")
                + "  thr " + _throttle.ToString("0.00") + "  brk " + _brakes.ToString("0.00") + Feelers()
                + "  roll " + _roll.ToString("0") + "°/" + _rollLimit.ToString("0")
                + "  recovers " + _recoverCount
                + (_state == PilotState.Recover ? "  rear " + (_rearClear < 0f ? "clear" : _rearClear.ToString("0.0") + " m") : "")
                + (_pushTime > 0f ? "  push " + _pushTime.ToString("0.0") + " s " + _pushName : "");
        }
    }

    // Cached references to the player and, while driving, the player's car. Refreshed lazily; reset on scene load.
    internal static class PlayerRef
    {
        private static Transform _player;
        private static float _nextFind;
        private static GameObject _playerCar;
        private static Rigidbody _playerCarRb;
        private static PlayMakerFSM _playerCarDrive;
        // Target() answers every pilot on the same physics step from one evaluation
        private static float _targetStamp = -1f;
        private static bool _targetOk;
        private static Vector3 _targetPos, _targetVel;
        private static GameObject _targetCar;
        private static readonly List<Component> _components = new List<Component>();   // non-allocating GetComponents buffer
        private static PlayMakerFSM _inCar;             // Player [InCar]: state InCar / OnFoot, var Car = the car the player sits in
        private static HutongGames.PlayMaker.FsmGameObject _inCarVar;
        private static Transform _inCarOwner;
        private static Vector3 _lastPos, _vel;
        private static float _lastStamp = -1f;

        internal static void Reset() { _player = null; _playerCar = null; _playerCarRb = null; _playerCarDrive = null; _inCar = null; _inCarVar = null; _inCarOwner = null; _nextFind = 0f; _lastStamp = -1f; _targetStamp = -1f; }

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
                if (p == null) return SetCar(null);
                if (InCarFsm(p) && _inCar.Fsm.Initialized)
                {
                    if (_inCar.ActiveStateName != "InCar") return SetCar(null);
                    if (_inCarVar == null) _inCarVar = _inCar.FsmVariables.GetFsmGameObject("Car");
                    var car = _inCarVar != null ? _inCarVar.Value : null;
                    if (car != null) return SetCar(car);
                }
                if (_playerCar != null && _playerCarDrive != null && _playerCarDrive.Fsm.Initialized && _playerCarDrive.ActiveStateName == "inCar") return _playerCar;
                for (var a = p.parent; a != null; a = a.parent)
                    if (HasVehicleController(a)) return SetCar(a.gameObject);
                return SetCar(null);
            }
        }

        // the car's Drive FSM and Rigidbody are looked up once per car change, not per caller
        private static GameObject SetCar(GameObject car)
        {
            if (car == _playerCar) return _playerCar;
            _playerCar = car;
            _playerCarDrive = car != null ? Patrol.FindFsm(car, "DriveTrigger", "Drive") : null;
            _playerCarRb = car != null ? car.GetComponent<Rigidbody>() : null;
            return _playerCar;
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
            _components.Clear();
            t.GetComponents(_components);
            for (int i = 0; i < _components.Count; i++)
            {
                var c = _components[i];
                if (c != null && c.GetType().Name == "VehicleController") return true;
            }
            return false;
        }

        // Position and velocity of what an AI car should ram: the player's car while driving, the player on foot otherwise.
        // Evaluated once per physics step however many pilots ask.
        internal static bool Target(out Vector3 pos, out Vector3 vel, out GameObject car)
        {
            if (_targetStamp != Time.fixedTime)
            {
                _targetStamp = Time.fixedTime;
                _targetOk = TargetNow(out _targetPos, out _targetVel, out _targetCar);
            }
            pos = _targetPos; vel = _targetVel; car = _targetCar;
            return _targetOk;
        }

        private static bool TargetNow(out Vector3 pos, out Vector3 vel, out GameObject car)
        {
            car = PlayerCar;
            var p = Player;
            if (car != null)
            {
                var rb = _playerCarRb;
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
