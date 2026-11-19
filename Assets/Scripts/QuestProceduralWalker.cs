using System.Collections;
using UnityEngine;

// Prototype: slow step-turn + short walk, two-bone IK in LateUpdate after MotionPlayer.
// Planted feet keep WORLD rotation and position; lifted foot alone adopts the new root heading.
[DefaultExecutionOrder(12000)]
public sealed class QuestProceduralWalker : MonoBehaviour
{
    [Header("Collider Terrain / Stairs")]
    public bool useColliderTerrain = true;
    [Tooltip("Only floor and stair colliders; exclude avatar and walls.")]
    public LayerMask terrainMask;
    [Range(0.05f, 0.35f)] public float stairStepHeight = 0.22f;
    [Range(0f, 50f)] public float terrainSlope = 35f;
    float standingRootAboveGround;
    float strideLift;
    public Animator avatarAnimator;
    public MotionPlayer motionPlayer;
    public QuestLiveFloorAnchor ground;
    public Transform facingReference;
    [Range(0.05f, 0.4f)] public float stepDistance = 0.24f;
    [Range(0.3f, 1.5f)] public float stepSeconds = 0.5f;
    [Range(0.01f, 0.15f)] public float footLift = 0.045f;
    [Tooltip("Extra clearance above a detected stair riser.")]
    [Range(0f, 0.12f)] public float stairClearance = 0.035f;
    [Header("Footprint Edge Check")]
    [Range(0.04f, 0.18f)] public float footProbeHalfLength = 0.11f;
    [Range(0.025f, 0.10f)] public float footProbeHalfWidth = 0.055f;
    [Range(0.005f, 0.04f)] public float footprintHeightTolerance = 0.018f;
    [Range(0f, 20f)] public float toeRollDegrees = 8f;
    [Range(0.05f, 0.5f)] public float weightShiftSeconds = 0.18f;
    [Range(3f, 35f)] public float turnPerStep = 20f;
    [Range(0.01f, 0.15f)] public float maxStepHeight = 0.08f;
    [Min(0.1f)] public float maxCommandDistance = 1f;
    public float ankleAboveGround = 0.06f;
    [Header("Runtime Leg Reach")]
    [Range(0f, 0.12f)] public float pelvisDropFraction = 0.015f;
    [Tooltip("Walking pelvis drop cap in meters; keeps the avatar from looking crouched.")]
    [Range(0f, 0.05f)] public float maxPelvisDropMeters = 0.02f;
    [Range(0f, 0.03f)] public float reachReserveFraction = 0.002f;
    [Tooltip("Numerical reach tolerance; the IK solver still clamps the final distance.")]
    [Range(0f, 0.03f)] public float reachToleranceMeters = 0.015f;
    [Range(0.005f, 0.05f)] public float minimumRootAdvance = 0.01f;
    [Range(8, 64)] public int reachSamples = 24;
    [Tooltip("Unity collider obstacle checks only; this is NOT full real-world obstacle avoidance.")]
    public LayerMask obstacleMask;
    public bool allowModelCommands = true;
    [Tooltip("收到AI回复动作时，让MotionPlayer驱动腿部；仅无回复动作时启用脚底IK。")]
    public bool replyMotionOwnsLegs = true;

    [Header("Procedural Gait")]
    [Range(0f, 0.025f)] public float supportSideShift = 0.012f;
    [Range(0f, 0.015f)] public float pelvisBob = 0.005f;
    Vector3 gaitPelvisOffset;
    bool firstStride, lastStride;

    float strideSupportSide;
    float detectedStepLift;
    float detectedStepTopY;
    Vector3 detectedStepPoint;
    Vector3 detectedStepDirection;
    float pendingStepTopY = float.NaN;
    int pendingStepFeet;

    [ContextMenu("Apply Moderate Gait Preset")]
    public void ApplyModerateGaitPreset()
    {
        stepDistance = 0.24f; stepSeconds = 0.5f; footLift = 0.045f; turnPerStep = 20f;
        supportSideShift = 0.012f; pelvisBob = 0.005f;
        Debug.Log("[WalkGait] Preset: nominal step=0.24m, body advance up to0.12m/step, turn=20deg, duration=0.5s.", this);
    }

    sealed class Leg
    {
        public Transform upper, lower, foot;
        public Vector3 planted, restLocal, hipOffsetRoot;
        public float thighLength, shinLength;
        public Quaternion plantedRotation, restRotation;
    }
    Leg left, right;
    Transform hips;
    Vector3 hipLocal;
    Quaternion hipRotation;
    Coroutine movement;
    WebSocketClient subscribedClient;
    public bool IsWalking => movement != null;
    public bool IsReady => ready;
    bool ready, cancel, swingLeft = true;
    bool savedRootMotion;
    bool ownsPose;
    bool warnedReach;
    Vector3 standingHipLocal;
    Vector3 calibrationScale;
    Vector3 kneeForwardLocal;

    IEnumerator Start()
    {
        if (avatarAnimator == null) avatarAnimator = GetComponentInChildren<Animator>();
        if (ground == null) ground = GetComponent<QuestLiveFloorAnchor>();
        if (motionPlayer == null) motionPlayer = GetComponent<MotionPlayer>();
        if (avatarAnimator == null || !avatarAnimator.isHuman || ground == null)
        { Debug.LogError("[Walk] Humanoid Animator and ground anchor required.", this); yield break; }
        float nextWaitLog = 0f;
        while (!useColliderTerrain && !ground.GroundCalibrated)
        {
            if (Time.realtimeSinceStartup >= nextWaitLog)
            {
                nextWaitLog = Time.realtimeSinceStartup + 5f;
                Debug.Log("[Walk] Waiting for ground: " + ground.GroundDiagnostic, this);
            }
            yield return null;
        }
        yield return new WaitForSecondsRealtime(1f);
        left = MakeLeg(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        right = MakeLeg(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
        hips = avatarAnimator.GetBoneTransform(HumanBodyBones.Hips);
        if (left == null || right == null || hips == null) yield break;
        if (useColliderTerrain && terrainMask.value == 0)
        { Debug.LogError("[Walk] Assign Terrain Mask to ground/stair colliders.", this); yield break; }
        if (useColliderTerrain)
        {
            if (!TryTerrain(transform.position, transform.position.y, out var initialGround))
            { Debug.LogError("[Walk] No terrain under avatar.", this); yield break; }
            standingRootAboveGround = transform.position.y - initialGround.y;
        }
        standingHipLocal = hips.localPosition;
        hipRotation = hips.localRotation;
        savedRootMotion = avatarAnimator.applyRootMotion;
        calibrationScale = transform.lossyScale;
        float legLength = Mathf.Min(left.thighLength + left.shinLength, right.thighLength + right.shinLength);
        float drop = Mathf.Min(maxPelvisDropMeters, legLength * Mathf.Clamp(pelvisDropFraction, 0f, 0.12f));
        // Move the pelvis, NOT the root or the world-space foot contacts.
        Vector3 desiredHipWorld = hips.position - Vector3.up * drop;
        hipLocal = hips.parent != null ? hips.parent.InverseTransformPoint(desiredHipWorld) : desiredHipWorld;
        hips.localPosition = hipLocal;
        left.hipOffsetRoot = Quaternion.Inverse(transform.rotation) * (left.upper.position - transform.position);
        right.hipOffsetRoot = Quaternion.Inverse(transform.rotation) * (right.upper.position - transform.position);
        Vector3 facing = Vector3.ProjectOnPlane(facingReference != null ? facingReference.forward : transform.forward, Vector3.up);
        if (facing.sqrMagnitude < 0.001f) facing = transform.forward;
        kneeForwardLocal = Quaternion.Inverse(transform.rotation) * facing.normalized;
        ownsPose = true;
        avatarAnimator.applyRootMotion = false;
        Debug.Log($"[WalkReach] L={left.thighLength:F3}+{left.shinLength:F3}m, R={right.thighLength:F3}+{right.shinLength:F3}m, pelvisDrop={drop:F3}m, scale={calibrationScale}", this);
        hips.localPosition = standingHipLocal;
        ready = true;
        BindClient();
        Debug.Log("[Walk] Ready: procedural feet locked. No walk animation used.", this);
    }
    void Update() { BindClient(); }
    void BindClient()
    {
        var client = WebSocketClient.Instance;
        if (client == subscribedClient) return;
        if (subscribedClient != null) subscribedClient.OnLocomotion -= OnMoveCommand;
        subscribedClient = client;
        if (client != null)
        {
            client.OnLocomotion += OnMoveCommand;
            Debug.Log("[WalkProtocol] Walker subscribed.", this);
        }
    }

    Leg MakeLeg(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones foot)
    {
        var a = avatarAnimator.GetBoneTransform(upper); var b = avatarAnimator.GetBoneTransform(lower); var c = avatarAnimator.GetBoneTransform(foot);
        if (a == null || b == null || c == null) return null;
        return new Leg { upper=a, lower=b, foot=c, planted=c.position, plantedRotation=c.rotation,
            thighLength=Vector3.Distance(a.position,b.position), shinLength=Vector3.Distance(b.position,c.position),
            restLocal=transform.InverseTransformPoint(c.position), restRotation=Quaternion.Inverse(transform.rotation)*c.rotation };
    }
    Vector3 failedDirection;
    float retryAfter;
    void Report(string kind,string detail){var presence=FindObjectOfType<VRMPresence>(); if(presence)presence.Record(kind,detail);}
    public void MoveRelative(float forwardMeters, float rightMeters, float turnDegrees)
    {
        if (!ready || !allowModelCommands || movement != null || (transform.lossyScale-calibrationScale).sqrMagnitude > 0.000001f) { Debug.LogWarning("[Walk] Not ready, disabled, or busy; command ignored.", this); return; }
        Vector3 forward = Vector3.ProjectOnPlane(facingReference != null ? facingReference.forward : transform.forward, Vector3.up).normalized;
        Vector3 rightward = Vector3.Cross(Vector3.up, forward);
        Vector3 delta = Vector3.ClampMagnitude(forward * forwardMeters + rightward * rightMeters, maxCommandDistance);
        if (Time.unscaledTime<retryAfter && Vector3.Dot(delta.normalized,failedDirection)>.95f && Mathf.Abs(turnDegrees)<1f) {
            Report("walk_failed","同方向刚被阻挡，已暂停重试。询问用户方向。"); return;
        }
        Debug.Log($"[WalkProtocol] Accepted displacement={delta}, turn={turnDegrees:F1}", this);
        left.planted = left.foot.position; left.plantedRotation = left.foot.rotation;
        right.planted = right.foot.position; right.plantedRotation = right.foot.rotation;
        ground.locomotionOwnsRoot = true;
        // Walker owns the pelvis/legs during locomotion; MotionPlayer still drives arms/head.
        if (motionPlayer != null) motionPlayer.locomotionOwnsHips = true;
        cancel = false;
        movement = StartCoroutine(Walk(delta, Mathf.Clamp(turnDegrees, -90f, 90f), forward));
    }
    void OnMoveCommand(string json)
    {
        Debug.Log($"[WalkProtocol] Walker command={json}, ready={ready}, busy={movement != null}, allowed={allowModelCommands}", this);
        var command = JsonUtility.FromJson<WebSocketClient.MoveCommand>(json);
        if (command == null) return;
        if (command.stop) { StopWalking(); return; }
        if (float.IsNaN(command.forward) || float.IsInfinity(command.forward)
            || float.IsNaN(command.right) || float.IsInfinity(command.right)
            || float.IsNaN(command.turn) || float.IsInfinity(command.turn)) return;
        MoveRelative(command.forward, command.right, command.turn);
    }
    public void StopWalking() { cancel = true; }
    IEnumerator Walk(Vector3 delta, float turn, Vector3 forward)
    {
        yield return null;
        Vector3 destination = transform.position + delta;
        Quaternion heading = transform.rotation;
        if (delta.sqrMagnitude > 0.0001f)
            heading = Quaternion.AngleAxis(Vector3.SignedAngle(forward, delta.normalized, Vector3.up), Vector3.up) * heading;
        heading = Quaternion.AngleAxis(turn, Vector3.up) * heading;
        int budget = 80;
        int completedSteps = 0;
        while (!cancel && budget-- > 0)
        {
            float angle = Quaternion.Angle(transform.rotation, heading);
            Vector3 remaining = destination - transform.position; remaining.y = 0f;
            if (angle < 0.5f && remaining.magnitude < 0.005f) break;
            Leg moving = swingLeft ? left : right;
            Leg support = swingLeft ? right : left;
            Vector3 nextPosition = transform.position;
            Quaternion nextRotation = transform.rotation;
            Vector3 ankle = moving.planted;
            bool accepted = false;
            string rejection = "No candidate";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                float fraction = Mathf.Pow(0.5f, attempt);
                nextRotation = Quaternion.RotateTowards(transform.rotation, heading, Mathf.Max(1f,turnPerStep)*fraction);
                nextPosition = transform.position;
                if (angle < 0.5f)
                {
                    float advance = Mathf.Min(remaining.magnitude, Mathf.Max(0.005f,stepDistance)*0.5f*fraction);
                    if (advance < minimumRootAdvance && remaining.magnitude > minimumRootAdvance) break;
                    nextPosition += remaining.normalized * advance;
                }
                if (obstacleMask.value != 0 && Physics.CheckCapsule(nextPosition + Vector3.up*0.25f, nextPosition + Vector3.up*1.3f, 0.18f, obstacleMask, QueryTriggerInteraction.Ignore))
                { rejection = "Collider obstacle"; break; }
                ankle = nextPosition + nextRotation * Vector3.Scale(moving.restLocal, calibrationScale);
                // On the first stair phase the root stays below the riser, while the
                // swing foot reaches the detected tread center instead of stopping at the root projection.
                if (!float.IsNaN(detectedStepTopY) && pendingStepFeet == 0 && detectedStepPoint != Vector3.zero)
                {
                    Vector3 footTarget = detectedStepPoint + detectedStepDirection * (footProbeHalfLength + 0.03f);
                    ankle.x = footTarget.x; ankle.z = footTarget.z;
                    ankle.y = detectedStepTopY + ankleAboveGround;
                }
                if (!TryTerrain(ankle, moving.planted.y - ankleAboveGround, out Vector3 hit))
                { rejection = "Next foot target rejected: " + ground.GroundDiagnostic; break; }
                float oldSoleY = moving.planted.y - ankleAboveGround;
                if (Mathf.Abs(hit.y-oldSoleY) > (useColliderTerrain ? stairStepHeight : maxStepHeight))
                { rejection = "Ground height change too large"; break; }
                ankle.y = hit.y + ankleAboveGround;
                if (!ProbeStepRiser(transform.position, nextPosition, ankle, oldSoleY, out rejection)) break;
                if (useColliderTerrain)
                {
                    if (!TryTerrain(nextPosition, transform.position.y-standingRootAboveGround, out var rootGround))
                    { rejection = "Root has no ground"; continue; }
                    nextPosition.y = rootGround.y + standingRootAboveGround;
                    if (!float.IsNaN(detectedStepTopY))
                    {
                        bool footAlreadyOnTread = pendingStepFeet > 0 && Mathf.Abs(pendingStepTopY-detectedStepTopY) < 0.04f;
                        bool thisFootIsFirstStep = Mathf.Abs(ankle.y-detectedStepTopY-ankleAboveGround) < 0.05f;
                        if (footAlreadyOnTread) nextPosition.y = Mathf.Max(nextPosition.y, detectedStepTopY + standingRootAboveGround);
                        else if (thisFootIsFirstStep) nextPosition.y = rootGround.y + standingRootAboveGround;
                    }
                }
                strideLift = Mathf.Max(footLift + Mathf.Abs(ankle.y-moving.planted.y), detectedStepLift);
                if (!SwingClear(moving.planted, ankle))
                { rejection = "Swing foot intersects terrain"; continue; }
                strideSupportSide = Mathf.Sign(Vector3.Dot(support.planted-transform.position, transform.right));
                firstStride = completedSteps == 0 || angle >= 0.5f;
                lastStride = angle >= 0.5f || Vector3.ProjectOnPlane(nextPosition-destination, Vector3.up).magnitude < 0.005f;
                if (TrajectoryReachable(moving,support,nextPosition,nextRotation,ankle,out rejection))
                {
                    accepted = true;
                    if (attempt > 0) Debug.Log($"[WalkReach] Step reduced to {fraction:F3} of requested size.",this);
                    break;
                }
            }
            if (!accepted) { failedDirection=delta.normalized; retryAfter=Time.unscaledTime+10f; Report("walk_failed",rejection); Debug.LogWarning("[WalkReach] Step refused: " + rejection,this); break; }
            Vector3 startPosition = transform.position; Quaternion startRotation = transform.rotation;
            Vector3 oldFoot = moving.planted; Quaternion oldFootRotation = moving.plantedRotation;
            float stepTopY = detectedStepTopY;
            float elapsed = 0f;
            float duration = Mathf.Max(0.1f,stepSeconds);
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float swing = SwingPhase(t);
                float footT = FootProgress(swing);
                float body = BodyProgress(t);
                transform.SetPositionAndRotation(Vector3.Lerp(startPosition,nextPosition,body), Quaternion.Slerp(startRotation,nextRotation,body));
                float footArc = 4f * footT * (1f - footT);
                moving.planted = Vector3.Lerp(oldFoot,ankle,footT) + Vector3.up*(footArc*strideLift);
                // Pitch only while airborne; support foot rotation stays fixed.
                Quaternion desired = Quaternion.Slerp(oldFootRotation,nextRotation*moving.restRotation,footT);
                Vector3 pitchAxis = Vector3.Cross(Vector3.up, transform.rotation*kneeForwardLocal).normalized;
                moving.plantedRotation = Quaternion.AngleAxis(-toeRollDegrees*LiftProfile(swing),pitchAxis)*desired;
                gaitPelvisOffset = PelvisOffset(t,transform.position,transform.rotation,support);
                yield return null;
            }
            moving.planted = ankle;
            moving.plantedRotation = nextRotation*moving.restRotation;
            if (!float.IsNaN(stepTopY) && Mathf.Abs(ankle.y - stepTopY - ankleAboveGround) < 0.06f)
            {
                if (pendingStepFeet == 0) { pendingStepTopY = stepTopY; pendingStepFeet = 1; }
                else if (Mathf.Abs(pendingStepTopY-stepTopY) < 0.06f) { pendingStepFeet = 2; }
            }
            else if (pendingStepFeet > 0 && moving.planted.y < pendingStepTopY + ankleAboveGround - 0.06f)
            { pendingStepFeet = 0; pendingStepTopY = float.NaN; }
            gaitPelvisOffset = Vector3.zero;
            completedSteps++;
            swingLeft = !swingLeft;
        }
        gaitPelvisOffset = Vector3.zero;
        hips.localPosition = standingHipLocal;
        ground.locomotionOwnsRoot = false;
        if (motionPlayer != null) motionPlayer.locomotionOwnsHips = false;
        movement = null;
        if (!cancel && Vector3.ProjectOnPlane(destination-transform.position,Vector3.up).magnitude<0.005f) Report("walk_finished",$"完成 {completedSteps} 步，位置 {transform.position}");
        Debug.Log($"[WalkGait] Finished {completedSteps} steps; root={transform.position}. Planted feet retained.", this);
    }
    bool TryTerrain(Vector3 target, float referenceY, out Vector3 point)
    {
        if (!useColliderTerrain) return ground.TryWalkingGround(target, out point);
        Vector3 origin = new Vector3(target.x, referenceY + stairStepHeight + 0.05f, target.z);
        if (Physics.Raycast(origin, Vector3.down, out var hit, 2f*stairStepHeight+0.1f, terrainMask, QueryTriggerInteraction.Ignore)
            && Vector3.Angle(hit.normal, Vector3.up) <= terrainSlope)
        { point = hit.point; return true; }
        point = default; return false;
    }

    bool ProbeStepRiser(Vector3 root, Vector3 candidateRoot, Vector3 landing, float oldGroundY, out string reason)
    {
        detectedStepLift = 0f;
        detectedStepTopY = float.NaN;
        detectedStepPoint = Vector3.zero;
        detectedStepDirection = Vector3.zero;
        reason = "OK";
        if (!useColliderTerrain) return true;
        Vector3 travel = candidateRoot - root;
        travel.y = 0f;
        if (travel.sqrMagnitude < 0.000001f) return true;
        Vector3 direction = travel.normalized;
        float distance = travel.magnitude + 0.16f;
        Vector3 low = root + Vector3.up * (ankleAboveGround + 0.025f);
        if (!Physics.Raycast(low, direction, out RaycastHit riser, distance, terrainMask, QueryTriggerInteraction.Ignore))
        {
            Debug.LogWarning($"[WalkStep] no riser hit: mask={terrainMask.value}, origin={low}, dir={direction}, distance={distance:F2}", this);
            return true;
        }
        Debug.Log($"[WalkStep] riser hit layer={riser.collider.gameObject.layer} name={riser.collider.name} point={riser.point} normal={riser.normal}", this);
        if (Vector3.Angle(riser.normal, Vector3.up) < 55f) return true;

        // Sample forward of the riser and choose the highest walkable tread found.
        float maxRise = stairStepHeight + 0.01f;
        bool foundTread = false;
        RaycastHit bestTop = default;
        float bestRise = float.NegativeInfinity;
        for (int i = 1; i <= 8; i++)
        {
            float forward = 0.035f * i;
            Vector3 sample = riser.point + direction * forward;
            Vector3 highOrigin = new Vector3(sample.x, oldGroundY + maxRise + 0.12f, sample.z);
            if (!Physics.Raycast(highOrigin, Vector3.down, out RaycastHit top, maxRise + 0.16f, terrainMask, QueryTriggerInteraction.Ignore)) continue;
            if (Vector3.Angle(top.normal, Vector3.up) > terrainSlope) continue;
            float rise = top.point.y - oldGroundY;
            if (rise < -0.03f) continue;
            foundTread = true;
            if (rise > maxRise)
            { reason = $"Step too high ({rise:F3}m > {stairStepHeight:F3}m)"; return false; }
            if (rise > bestRise) { bestRise = rise; bestTop = top; }
        }
        if (!foundTread)
        {
            Debug.LogWarning($"[WalkStep] riser has no tread: riser={riser.point}, normal={riser.normal}, oldY={oldGroundY:F3}, maxRise={maxRise:F3}, mask={terrainMask.value}", this);
            reason = "Riser detected but no walkable top within step height"; return false;
        }

        // Validate the whole planted footprint only after its center crosses the riser.
        float landingPastRiser = Vector3.Dot(landing-riser.point,direction);
        detectedStepTopY = bestTop.point.y;
        detectedStepPoint = bestTop.point;
        detectedStepDirection = direction;
        if (landingPastRiser > footProbeHalfLength && !FootprintOnTread(landing, direction, detectedStepTopY, out reason)) return false;
        detectedStepLift = Mathf.Max(0f, bestRise) + stairClearance;
        return true;
    }
    bool FootprintOnTread(Vector3 ankle, Vector3 travel, float expectedY, out string reason)
    {
        reason = "OK";
        Vector3 forward = travel.sqrMagnitude > 0.0001f ? travel.normalized : transform.forward;
        Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
        Vector3[] offsets = {
            Vector3.zero,
            forward*footProbeHalfLength, -forward*footProbeHalfLength,
            side*footProbeHalfWidth, -side*footProbeHalfWidth,
            forward*footProbeHalfLength+side*footProbeHalfWidth,
            forward*footProbeHalfLength-side*footProbeHalfWidth,
            -forward*footProbeHalfLength+side*footProbeHalfWidth,
            -forward*footProbeHalfLength-side*footProbeHalfWidth
        };
        for (int i=0;i<offsets.Length;i++)
        {
            Vector3 origin=ankle+offsets[i]+Vector3.up*(stairStepHeight+0.12f);
            if (!Physics.Raycast(origin,Vector3.down,out RaycastHit hit,stairStepHeight+0.24f,terrainMask,QueryTriggerInteraction.Ignore))
            { reason=$"Footprint sample {i} has no ground"; return false; }
            if (Vector3.Angle(hit.normal,Vector3.up)>terrainSlope || Mathf.Abs(hit.point.y-expectedY)>footprintHeightTolerance)
            { reason=$"Footprint sample {i} is off tread: y={hit.point.y:F3}, expected={expectedY:F3}"; return false; }
        }
        return true;
    }
    bool SwingClear(Vector3 from, Vector3 to)
    {
        if (!useColliderTerrain) return true;
        // Sweep a small ankle proxy along the entire foot trajectory, including stair risers.
        Vector3 previous = from;
        for (int i=1; i<=24; i++)
        {
            float t = i/24f;
            float progress=FootProgress(t);
            Vector3 next=Vector3.Lerp(from,to,progress);
            // Clearance arc: rise before the riser, stay high over it, then descend.
            float arc=4f*progress*(1f-progress);
            next += Vector3.up*(arc*strideLift);
            Vector3 delta = next-previous;
            if (Physics.CheckSphere(next, 0.015f, terrainMask, QueryTriggerInteraction.Ignore)
                || Physics.SphereCast(previous,0.015f,delta.normalized,out var hit,delta.magnitude,terrainMask,QueryTriggerInteraction.Ignore)) return false;
            previous = next;
        }
        return true;
    }

    float ShiftFraction() { return Mathf.Clamp(weightShiftSeconds/Mathf.Max(0.1f,stepSeconds),0.05f,0.25f); }
    float SwingPhase(float t) { float f=ShiftFraction(); return Mathf.Clamp01((t-f)/(1f-2f*f)); }
    static float SmoothPhase(float t) { t=Mathf.Clamp01(t); return t*t*(3f-2f*t); }
    static float FootProgress(float t) { return t*t*t*(t*(t*6f-15f)+10f); }
    static float LiftProfile(float t) { float v=Mathf.Sin(Mathf.PI*t); return v*v; }
    float BodyProgress(float t)
    {
        if (firstStride && lastStride) return t*t*(3f-2f*t);
        if (firstStride) return 2f*t*t-t*t*t;
        if (lastStride) return t+t*t-t*t*t;
        return t;
    }
    Vector3 PelvisOffset(float t, Vector3 root, Quaternion rotation, Leg support)
    {
        Vector3 right = rotation * Vector3.right;
        float side = strideSupportSide;
        float f = ShiftFraction();
        float weight = SmoothPhase(t/f) * (1f-SmoothPhase((t-(1f-f))/f));
        return right*(side*supportSideShift*weight) - Vector3.up*(pelvisBob*weight);
    }

    bool TrajectoryReachable(Leg moving, Leg support, Vector3 endPosition, Quaternion endRotation, Vector3 endFoot, out string reason)
    {
        int count = Mathf.Clamp(reachSamples,8,64);
        for (int i=0;i<=count;i++)
        {
            float t = (float)i/count; float w=BodyProgress(t);
            Vector3 root=Vector3.Lerp(transform.position,endPosition,w);
            Quaternion rotation=Quaternion.Slerp(transform.rotation,endRotation,w);
            float footT=FootProgress(SwingPhase(t));
            float footArc=4f*footT*(1f-footT);
            Vector3 lifted=Vector3.Lerp(moving.planted,endFoot,footT)+Vector3.up*(footArc*strideLift);
            Vector3 pelvis = PelvisOffset(t, root, rotation, support);
            if (!Reachable(moving,root+rotation*moving.hipOffsetRoot+pelvis,lifted,out reason)) return false;
            if (!Reachable(support,root+rotation*support.hipOffsetRoot+pelvis,support.planted,out reason)) return false;
        }
        reason="OK"; return true;
    }
    bool Reachable(Leg leg,Vector3 hip,Vector3 target,out string reason)
    {
        float total=leg.thighLength+leg.shinLength;
        float reserve=total*Mathf.Clamp(reachReserveFraction,0.001f,0.03f);
        float distance=Vector3.Distance(hip,target);
        float min=Mathf.Abs(leg.thighLength-leg.shinLength)+reserve;
        float max=total-reserve;
        float tolerance=Mathf.Clamp(reachToleranceMeters,0f,0.03f);
        reason=$"{leg.foot.name}: distance={distance:F3}m, reachable=[{min:F3},{max:F3}]m, tolerance={tolerance:F3}m";
        return distance>=min-tolerance && distance<=max+tolerance;
    }

    void LateUpdate()
    {
        if (!ready || !IsWalking) return;
        if ((transform.lossyScale-calibrationScale).sqrMagnitude > 0.000001f)
        {
            cancel = true;
            if (!warnedReach) { warnedReach = true; Debug.LogWarning("[WalkReach] Scale changed after calibration; restart calibration before walking.",this); }
        }
        hips.localPosition = hipLocal; hips.localRotation = hipRotation;
        hips.position += gaitPelvisOffset;
        // Locomotion owns both legs while the root is moving. Reply motion remains available
        // for the upper body; otherwise the root advances while the feet stay planted.
        Solve(left); Solve(right);
    }
    void Solve(Leg leg)
    {
        Vector3 hip = leg.upper.position;
        float a = leg.thighLength, b = leg.shinLength;
        Vector3 vector = leg.planted - hip;
        if (a < 0.001f || b < 0.001f || vector.sqrMagnitude < 0.000001f) return;
        float distance = Mathf.Clamp(vector.magnitude, Mathf.Abs(a-b)+0.0001f,a+b-0.0001f);
        Vector3 direction = vector.normalized;
        Vector3 facing = transform.rotation * kneeForwardLocal;
        Vector3 pole = Vector3.ProjectOnPlane(facing,direction).normalized;
        if (pole.sqrMagnitude < 0.001f) pole = Vector3.ProjectOnPlane(transform.right,direction).normalized;
        float along = (a*a-b*b+distance*distance)/(2f*distance);
        Vector3 knee = hip + direction*along + pole*Mathf.Sqrt(Mathf.Max(0f,a*a-along*along));
        leg.upper.rotation = Quaternion.FromToRotation(leg.lower.position-hip,knee-hip) * leg.upper.rotation;
        leg.lower.rotation = Quaternion.FromToRotation(leg.foot.position-leg.lower.position,leg.planted-leg.lower.position) * leg.lower.rotation;
        leg.foot.rotation = leg.plantedRotation;
    }
    void OnDisable()
    {
        if (subscribedClient != null) subscribedClient.OnLocomotion -= OnMoveCommand;
        subscribedClient = null;
        StopAllCoroutines(); movement = null; ready = false;
        if (ownsPose)
        {
            if (avatarAnimator != null) avatarAnimator.applyRootMotion = savedRootMotion;
            if (hips != null) hips.localPosition = standingHipLocal;
            ownsPose = false;
        }
        if (ground != null) ground.locomotionOwnsRoot = false;
        if (motionPlayer != null) motionPlayer.locomotionOwnsHips = false;
    }
}
