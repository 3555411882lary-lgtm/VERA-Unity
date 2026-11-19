using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
using UnityEngine.XR.OpenXR.Features.Meta;
#endif

// Place on VRM root; disable all older floor anchors. No room model or ARPlaneManager required.
[DisallowMultipleComponent]
[DefaultExecutionOrder(11000)]
public sealed class QuestLiveFloorAnchor : MonoBehaviour
{
    public XROrigin xrOrigin;
    public ARRaycastManager raycastManager;
    public Camera headCamera;
    public Animator avatarAnimator;
    public Transform leftSole, rightSole;
    public Transform controllerRay;
    [Tooltip("Optional Grip+Trigger floor confirmation and Grip+A tip calibration. Off by default to avoid UI conflicts.")]
    public bool enableFloorShortcuts = false;
    [Min(0f)] public float footBoneToSole = 0.06f;
    public float heightAdjustment;
    [Tooltip("Continue sampling after automatic floor lock. Updates height only.")]
    public bool continuousGroundUpdate = true;
    [Min(0.1f)] public float heightFollowSpeed = 5f;
    [Min(0.01f)] public float maxAutomaticHeightStep = 0.25f;
    [Range(0f, 45f)] public float maxSlopeDegrees = 20f;
    [Min(0.01f)] public float maxFloorDifference = 0.25f;
    [Min(0.01f)] public float stableTolerance = 0.03f;
    [Min(2)] public int stableSamples = 8;
    [Min(0.1f)] public float expectedHeadHeight = 1.65f;
    [HideInInspector] public float groundRayDownAngle = 35f; // Legacy serialized field; avatar-local targeting supersedes fixed angle.
    [Min(0.1f)] public float rayLength = 6f;
    [Min(0f)] public float manualControllerTipOffset = 0.04f;
    [Min(0.1f)] public float settleSeconds = 1.5f;

    readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
    readonly List<InputDevice> devices = new List<InputDevice>();
    float nextProbe, nextLog, started, rootAboveSole, sampleY;
    int samples;
    bool calibrated, locked, ownsManager, wasTrigger, wasPrimary;
    bool manualHeightLocked;
    Vector3 floorSession;
    string hitDetails = "";
    string rayCoordinates = "no hit recorded";
    string state = "Waiting for scene permission";
    [Header("Multi-point Ground Sampling")]
    [Range(1f, 8f)] public float sampleSpreadDegrees = 3f;
    [Min(0.005f)] public float planeResidualMeters = 0.025f;
    [Min(0.05f)] public float maxPatchRadiusMeters = 0.5f;
    [Min(0.02f)] public float walkingHitToleranceMeters = 0.15f;
    [Header("Local Ground Stability")]
    [Min(0.02f)] public float avatarHitRadius = 0.20f;
    [Min(0.005f)] public float maxDropFromConfirmedGround = 0.03f;
    [Min(0.005f)] public float maxVerticalSpeed = 0.05f;
    [Min(0.02f)] public float sampleRegionRadius = 0.12f;
    Vector3 confirmedFloorSession;
    Vector3 candidateSession;
    float candidateStartedAt;
    bool hasConfirmedFloor;
    const string PermissionName = "com.oculus.permission.USE_SCENE";

    void Start()
    {
        started = Time.realtimeSinceStartup;
        if (headCamera == null) headCamera = Camera.main;
        if (avatarAnimator == null) avatarAnimator = GetComponentInChildren<Animator>();
        if (xrOrigin == null || raycastManager == null || raycastManager.gameObject != xrOrigin.gameObject)
        {
            Debug.LogError("[LiveFloor] Assign XROrigin and AR Raycast Manager on the SAME XRRig root. No movement.", this);
            enabled = false;
        }
    }

    void Update()
    {
        if (enableFloorShortcuts) ReadButtons();
        if (!calibrated && Time.realtimeSinceStartup - started >= settleSeconds) CalibrateStandingSoles();
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(PermissionName)) { Report(); return; }
        if (!raycastManager.enabled) { raycastManager.enabled = true; ownsManager = true; }
        if ((!locked || (continuousGroundUpdate && !manualHeightLocked)) && calibrated && Time.realtimeSinceStartup >= nextProbe)
        {
            nextProbe = Time.realtimeSinceStartup + 0.25f;
            if (headCamera == null) { state = "No head camera"; Report(); return; }
            // Probe the avatar's local patch, not an arbitrary surface in front of the viewer.
            float referenceY = headCamera.transform.position.y - expectedHeadHeight;
            inputs.Clear(); SubsystemManager.GetSubsystems(inputs);
            foreach (var input in inputs)
                if (input.running && (input.GetTrackingOriginMode() & TrackingOriginModeFlags.Floor) != 0)
                { referenceY = xrOrigin.transform.position.y; break; }
            Vector3 target = transform.position;
            target.y = locked ? xrOrigin.TrackablesParent.TransformPoint(floorSession).y : referenceY;
            Vector3 rayDirection = target - headCamera.transform.position;
            if (rayDirection.y >= -0.02f || rayDirection.sqrMagnitude < 0.01f)
            { samples = 0; state = "Avatar ground is not below headset"; Report(); return; }
            if (TrySurface(new Ray(headCamera.transform.position, rayDirection.normalized), out Vector3 point))
            {
                Vector2 separation = new Vector2(point.x-transform.position.x, point.z-transform.position.z);
                if (separation.magnitude > avatarHitRadius)
                { samples = 0; state = "Hit is outside avatar foot region; holding height"; Report(); return; }
                Vector3 pointSession = xrOrigin.TrackablesParent.InverseTransformPoint(point);
                if (samples > 0 && Vector2.Distance(new Vector2(pointSession.x, pointSession.z), new Vector2(candidateSession.x,candidateSession.z)) > sampleRegionRadius)
                    samples = 0;
                if (Mathf.Abs(point.y - referenceY) > maxFloorDifference)
                { samples = 0; state = $"Height rejected: hitY={point.y:F3}, referenceY={referenceY:F3}, delta={point.y-referenceY:F3}m; use confirmation only on known ground"; }
                else if (samples == 0 || Mathf.Abs(point.y - sampleY) > stableTolerance)
                { sampleY = point.y; samples = 1; candidateSession = pointSession; candidateStartedAt = Time.unscaledTime; state = "Stabilizing avatar-local ground"; }
                else
                {
                    sampleY += (point.y - sampleY) / (++samples);
                    state = "Stabilizing " + samples + "/" + stableSamples;
                    if (samples >= Mathf.Max(2, stableSamples) && Time.unscaledTime-candidateStartedAt >= 1f)
                    {
                        point.y = sampleY;
                        float previousY = locked ? xrOrigin.TrackablesParent.TransformPoint(floorSession).y : point.y;
                        float confirmedY = hasConfirmedFloor ? xrOrigin.TrackablesParent.TransformPoint(confirmedFloorSession).y : point.y;
                        if (hasConfirmedFloor && point.y < confirmedY-maxDropFromConfirmedGround)
                            state = "Ground drop exceeds confirmed baseline; holding height. Confirm manually or ReacquireFloor.";
                        else if (locked && Mathf.Abs(point.y - previousY) > maxAutomaticHeightStep)
                            state = "Large height change rejected; call ReacquireFloor or confirm manually";
                        else
                            LockFloor(point, "automatic raycast");
                        samples = 0;
                    }
                }
            }
            else samples = 0;
        }
#endif
        Report();
    }

    // Right trigger: confirm a visible surface pointed at by controllerRay.
    // Hold right grip and press A: place controller tip on ground, then confirm its height.
    void ReadButtons()
    {
        devices.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, devices);
        if (devices.Count == 0) { wasTrigger = wasPrimary = false; return; }
        var device = devices[0];
        if (!device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked)
        { wasTrigger = wasPrimary = false; return; }
        device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary);
        device.TryGetFeatureValue(CommonUsages.gripButton, out bool grip);
        if (trigger && !wasTrigger && grip) ConfirmControllerRay();
        if (primary && !wasPrimary && grip) ConfirmControllerTipHeight();
        wasTrigger = trigger; wasPrimary = primary;
    }

    [ContextMenu("Calibrate Standing Soles")]
    public void CalibrateStandingSoles()
    {
        Transform l = leftSole, r = rightSole;
        bool markers = l != null && r != null;
        if (!markers)
        {
            if (avatarAnimator == null || !avatarAnimator.isHuman) return;
            l = avatarAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
            r = avatarAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
        }
        if (l == null || r == null) return;
        rootAboveSole = transform.position.y - Mathf.Min(l.position.y, r.position.y) + (markers ? 0f : footBoneToSole);
        calibrated = true;
        Debug.Log($"[LiveFloor] Sole calibration ready: rootAboveSole={rootAboveSole:F3}m.", this);
    }

    // Use five visible positions, not the provider's pose rotation, to estimate a surface normal.
    bool TrySurface(Ray ray, out Vector3 point)
    {
        point = default;
        hitDetails = "";
        Vector3 d = ray.direction.normalized;
        Vector3 right = Vector3.Cross(d, Vector3.up);
        if (right.sqrMagnitude < 0.001f) right = Vector3.Cross(d, Vector3.forward);
        right.Normalize();
        Vector3 up = Vector3.Cross(right, d).normalized;
        float spread = Mathf.Tan(Mathf.Clamp(sampleSpreadDegrees, 1f, 8f) * Mathf.Deg2Rad);
        Vector3 c, l, r, u, v;
        if (!TryVisiblePoint(ray, out c)
            || !TryVisiblePoint(new Ray(ray.origin, (d-right*spread).normalized), out l)
            || !TryVisiblePoint(new Ray(ray.origin, (d+right*spread).normalized), out r)
            || !TryVisiblePoint(new Ray(ray.origin, (d+up*spread).normalized), out u)
            || !TryVisiblePoint(new Ray(ray.origin, (d-up*spread).normalized), out v)) return false;
        Vector3 horizontalSpan = r-l, verticalSpan = u-v;
        Vector3 cross = Vector3.Cross(horizontalSpan, verticalSpan);
        float spans = horizontalSpan.magnitude * verticalSpan.magnitude;
        if (horizontalSpan.magnitude < 0.02f || verticalSpan.magnitude < 0.02f
            || spans < 0.0004f || cross.magnitude / spans < 0.15f)
        { state = "Ground patch degenerate"; return false; }
        Vector3 normal = cross.normalized;
        if (normal.y < 0f) normal = -normal;
        Vector3 centroid = (c+l+r+u+v)/5f;
        float residual = 0f, radius = 0f;
        foreach (Vector3 sample in new Vector3[] { c,l,r,u,v })
        {
            residual = Mathf.Max(residual, Mathf.Abs(Vector3.Dot(sample-centroid,normal)));
            radius = Mathf.Max(radius, Vector3.Distance(sample,c));
        }
        float slope = Mathf.Acos(Mathf.Clamp01(normal.y))*Mathf.Rad2Deg;
        hitDetails = $"patchCenter={c}, fittedSlope={slope:F1}deg, residual={residual:F3}m, radius={radius:F3}m";
        if (residual > planeResidualMeters || radius > maxPatchRadiusMeters)
        { state = "Mixed surfaces/depth discontinuity rejected"; return false; }
        if (slope > maxSlopeDegrees)
        { state = "Fitted ground slope rejected"; return false; }
        // Project the centre measurement onto the fitted plane at its own X/Z.
        point = c;
        point.y = centroid.y - (normal.x*(c.x-centroid.x)+normal.z*(c.z-centroid.z))/normal.y;
        return true;
    }

    bool TryVisiblePoint(Ray worldRay, out Vector3 worldPoint)
    {
        worldPoint = default;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(PermissionName)) { state = "Scene permission missing"; return false; }
        if (raycastManager == null || !(raycastManager.subsystem is MetaOpenXRRaycastSubsystem meta) || !meta.running)
        { state = "Meta raycast subsystem not running"; return false; }
        Transform space = xrOrigin.TrackablesParent;
        Vector3 direction = space.InverseTransformVector(worldRay.direction.normalized);
        float scale = direction.magnitude;
        if (scale < 0.00001f) return false;
        var result = meta.RaycastEnvironment(new Ray(space.InverseTransformPoint(worldRay.origin), direction/scale),rayLength*scale);
        if (!result.status.IsSuccess()) { state = "Raycaster not ready/error: " + result.status; return false; }
        var hit = result.value;
        // InvalidOrientation still has a valid position according to Meta 2.5.1; orientation is unused.
        if (hit.hitStatus != EnvironmentRaycastHitStatus.Hit && hit.hitStatus != EnvironmentRaycastHitStatus.HitInvalidOrientation)
        { state = "Ray status="+hit.hitStatus+"; look at the ground patch"; return false; }
        worldPoint = space.TransformPoint(hit.hit.pose.position);
        Vector3 originSession = space.InverseTransformPoint(worldRay.origin);
        Vector3 directionSession = direction / scale;
        Vector3 deltaSession = hit.hit.pose.position - originSession;
        float along = Vector3.Dot(deltaSession, directionSession);
        float offRay = (deltaSession - directionSession * along).magnitude;
        rayCoordinates = $"originWorld={worldRay.origin:F3}, directionWorld={worldRay.direction:F3}, originSession={originSession:F3}, hitSession={hit.hit.pose.position:F3}, hitWorld={worldPoint:F3}, providerDistance={hit.hit.distance:F3}, sessionAlong={along:F3}, sessionOffRay={offRay:F3}";
        if (!Finite(worldPoint.x) || !Finite(worldPoint.y) || !Finite(worldPoint.z)) return false;
        Vector3 offset = worldPoint-worldRay.origin;
        if (offset.magnitude > rayLength + 0.05f || Vector3.Dot(offset,worldRay.direction) <= 0f) return false;
        return true;
#else
        state = "Environment raycast requires Quest Android";
        return false;
#endif
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

    public void ConfirmControllerRay()
    {
        if (!calibrated || !TryControllerPose(out Vector3 origin, out Quaternion rotation))
        { Debug.LogWarning("[LiveFloor] Wait for sole calibration and a tracked right controller.", this); return; }
        if (TrySurface(new Ray(origin, rotation * Vector3.forward), out Vector3 point))
            LockFloor(point, "user-confirmed controller ray");
        else Debug.LogWarning("[LiveFloor] " + state, this);
    }

    public void ConfirmControllerTipHeight()
    {
        if (!calibrated || !TryControllerPose(out Vector3 origin, out _)) return;
        // Intentional manual fallback: no depth required, not an automatic floor measurement.
        Vector3 point = origin - Vector3.up * manualControllerTipOffset;
        LockFloor(point, "manual controller-tip height");
    }

    bool TryControllerPose(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        if (controllerRay != null)
        { position = controllerRay.position; rotation = controllerRay.rotation; return true; }
        // devicePosition/deviceRotation are tracking-space poses. The head's parent is the
        // tracking-space transform in this project's direct-child TrackedPoseDriver setup.
        if (headCamera == null || headCamera.transform.parent == null) return false;
        var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!device.isValid || !device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked
            || !device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 localPosition)
            || !device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion localRotation)) return false;
        Transform trackingSpace = headCamera.transform.parent;
        position = trackingSpace.TransformPoint(localPosition);
        rotation = trackingSpace.rotation * localRotation;
        return true;
    }

    [ContextMenu("Reacquire Floor")]
    public void ReacquireFloor()
    {
        // Leave current transform unchanged while searching; release the step limit on explicit reposition.
        locked = false; hasConfirmedFloor = false; manualHeightLocked = false; samples = 0; nextProbe = 0f; state = "Reacquiring";
    }

    void LockFloor(Vector3 worldPoint, string source)
    {
        bool first = !locked;
        bool manual = source != "automatic raycast";
        floorSession = xrOrigin.TrackablesParent.InverseTransformPoint(worldPoint);
        if (!hasConfirmedFloor || manual)
        {
            confirmedFloorSession = floorSession;
            hasConfirmedFloor = true;
        }
        locked = true;
        manualHeightLocked = manual;
        state = manual ? "Manual height held; ReacquireFloor resumes automatic updates" : "Ground tracking active";
        if (first || manual)
            Debug.Log($"[LiveFloor] Accepted via {source}; floorWorldY={worldPoint.y:F3}m; continuous={continuousGroundUpdate && !manual}.", this);
    }

    public bool GroundCalibrated => locked && calibrated && (locomotionOwnsRoot ||
        Mathf.Abs(transform.position.y - (xrOrigin.TrackablesParent.TransformPoint(floorSession).y + rootAboveSole + heightAdjustment)) < 0.01f);
    public bool locomotionOwnsRoot;
    // Only actual visible environment hits; never substitute the cached flat height for a new step.
    public string GroundDiagnostic => state + "; " + hitDetails;
    public bool TryWalkingGround(Vector3 worldXZ, out Vector3 point)
    {
        point = default;
        if (!GroundCalibrated || headCamera == null) return false;
        Vector3 target = worldXZ;
        target.y = xrOrigin.TrackablesParent.TransformPoint(floorSession).y;
        Vector3 direction = target-headCamera.transform.position;
        if (direction.sqrMagnitude < 0.01f || direction.y >= 0f) return false;
        if (!TrySurface(new Ray(headCamera.transform.position,direction.normalized),out point)) return false;
        Vector2 difference = new Vector2(point.x-worldXZ.x, point.z-worldXZ.z);
        if (difference.magnitude > walkingHitToleranceMeters)
        { state = "Walking ray hit too far from requested foot X/Z"; return false; }
        return true;
    }

    void LateUpdate()
    {
        if (locomotionOwnsRoot || !locked || !calibrated) return;
        Vector3 position = transform.position;
        float targetY = xrOrigin.TrackablesParent.TransformPoint(floorSession).y + rootAboveSole + heightAdjustment;
        float t = 1f - Mathf.Exp(-Mathf.Max(0.1f, heightFollowSpeed) * Time.unscaledDeltaTime);
        float smoothed = Mathf.Lerp(position.y, targetY, t);
        position.y = Mathf.MoveTowards(position.y, smoothed, Mathf.Max(0.005f,maxVerticalSpeed)*Time.unscaledDeltaTime);
        transform.position = position;
    }
    void Report()
    {
        if (Time.realtimeSinceStartup < nextLog) return;
        nextLog = Time.realtimeSinceStartup + 10f;
        LogCoordinateState();
        Debug.Log("[LiveFloor] " + state + "; calibrated=" + calibrated + ", locked=" + locked + "; " + hitDetails, this);
    }
    void LogCoordinateState()
    {
        if (xrOrigin == null || headCamera == null) return;
        Transform space = xrOrigin.TrackablesParent;
        Transform parent = headCamera.transform.parent;
        inputs.Clear(); SubsystemManager.GetSubsystems(inputs);
        string modes = "";
        foreach (var input in inputs)
            modes += $"running={input.running}, mode={input.GetTrackingOriginMode()}; ";
        Vector3 parentPosition = parent != null ? parent.position : Vector3.zero;
        Vector3 parentLocal = parent != null ? parent.localPosition : Vector3.zero;
        Debug.Log($"[FloorCoords] {modes} headWorld={headCamera.transform.position:F3}, headLocal={headCamera.transform.localPosition:F3}, originWorld={xrOrigin.transform.position:F3}, originScale={xrOrigin.transform.lossyScale:F3}, headParentWorld={parentPosition:F3}, headParentLocal={parentLocal:F3}, spaceWorld={space.position:F3}, spaceLocal={space.localPosition:F3}, spaceScale={space.lossyScale:F3}, spaceUp={space.up:F3}", this);
        Debug.Log("[FloorRayCoords] " + rayCoordinates, this);
    }

    void OnDestroy() { if (ownsManager && raycastManager != null) raycastManager.enabled = false; }
}
