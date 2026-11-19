using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;

// Drives only UI press/scroll. No avatar bone manipulation or environment raycasting.
[DefaultExecutionOrder(-100)]
public sealed class QuestDualUIInput : MonoBehaviour
{
    public Transform trackingSpace;
    public Camera headCamera;
    public XRRayInteractor leftRay, rightRay;
    public bool enableHandPinch = true;
    [Min(0.005f)] public float pinchClose = 0.025f;
    [Min(0.01f)] public float pinchOpen = 0.04f;
    readonly List<XRHandSubsystem> hands = new List<XRHandSubsystem>();
    bool leftPinch, rightPinch;
    int leftSource, rightSource;
    bool leftArmed, rightArmed;
    Material lineMaterial;
    InputAction leftAimPosition, leftAimRotation, rightAimPosition, rightAimRotation;
    InputAction leftHandPosition, leftHandRotation, leftHandTracked, leftHandPinch;
    InputAction rightHandPosition, rightHandRotation, rightHandTracked, rightHandPinch;
    readonly List<InputAction> ownedActions = new List<InputAction>();
    float nextDiagnostic;
    GameObject leftHovered, rightHovered;

    InputAction Action(string path, string type)
    {
        var action = new InputAction(type: InputActionType.Value, binding: path, expectedControlType: type);
        ownedActions.Add(action);
        return action;
    }

    void OnEnable()
    {
        foreach (var action in ownedActions) action.Enable();
    }


    void Awake()
    {
        Configure(leftRay); Configure(rightRay);
        var shader = Resources.Load<Shader>("QuestUIRay");
        if (shader != null && shader.isSupported) lineMaterial = new Material(shader);
        else Debug.LogError("[QuestUI v2] Missing/unsupported Resources/QuestUIRay.shader; ray rendering disabled.", this);
        leftAimPosition = Action("<XRController>{LeftHand}/pointerPosition", "Vector3");
        leftAimRotation = Action("<XRController>{LeftHand}/pointerRotation", "Quaternion");
        rightAimPosition = Action("<XRController>{RightHand}/pointerPosition", "Vector3");
        rightAimRotation = Action("<XRController>{RightHand}/pointerRotation", "Quaternion");
        leftHandPosition = Action("<MetaAimHand>{LeftHand}/devicePosition", "Vector3");
        leftHandRotation = Action("<MetaAimHand>{LeftHand}/deviceRotation", "Quaternion");
        leftHandTracked = Action("<MetaAimHand>{LeftHand}/isTracked", "Button");
        leftHandPinch = Action("<MetaAimHand>{LeftHand}/indexPressed", "Button");
        rightHandPosition = Action("<MetaAimHand>{RightHand}/devicePosition", "Vector3");
        rightHandRotation = Action("<MetaAimHand>{RightHand}/deviceRotation", "Quaternion");
        rightHandTracked = Action("<MetaAimHand>{RightHand}/isTracked", "Button");
        rightHandPinch = Action("<MetaAimHand>{RightHand}/indexPressed", "Button");
        SetupLine(leftRay); SetupLine(rightRay);
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include))
        {
            if (canvas.renderMode != RenderMode.WorldSpace || canvas.gameObject.scene != gameObject.scene) continue;
            var receiver = canvas.GetComponent<TrackedDeviceGraphicRaycaster>();
            if (receiver == null) receiver = canvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            receiver.enabled = true;
            receiver.checkFor3DOcclusion = false;
            receiver.checkFor2DOcclusion = false;
            // Permit ray input on either face; does not flip or move the canvas.
            receiver.ignoreReversedGraphics = false;
            if (headCamera != null) canvas.worldCamera = headCamera;
        }
    }
    static void Configure(XRRayInteractor ray)
    {
        if (ray == null) return;
        ray.raycastMask = ~0; // XRI uses this mask for UI as well: zero blocks every Canvas.
        ray.blockUIOnInteractableSelection = false;
        ray.rayOriginTransform = ray.transform;
        ray.enableUIInteraction = true;
        ray.enableARRaycasting = false;
        ray.maxRaycastDistance = 5f;
        ray.uiPressInput.inputSourceMode = XRInputButtonReader.InputSourceMode.ManualValue;
        ray.uiScrollInput.inputSourceMode = XRInputValueReader.InputSourceMode.ManualValue;
    }
    void SetupLine(XRRayInteractor ray)
    {
        if (ray == null) return;
        var line = ray.GetComponent<LineRenderer>();
        if (line == null) line = ray.gameObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true; line.positionCount = 2;
        line.startWidth = 0.003f; line.endWidth = 0.001f;
        if (lineMaterial != null) line.sharedMaterial = lineMaterial;
        line.enabled = false;
    }
    void Update()
    {
        if (trackingSpace == null || headCamera == null)
        {
            Release(leftRay); Release(rightRay);
            if (Time.unscaledTime >= nextDiagnostic)
            { nextDiagnostic = Time.unscaledTime + 10f; Debug.LogError("[QuestUI v2] Assign Tracking Space=Camera Offset and Head Camera=Main Camera.", this); }
            return;
        }
        hands.Clear(); SubsystemManager.GetSubsystems(hands);
        if (Time.unscaledTime >= nextDiagnostic)
        {
            nextDiagnostic = Time.unscaledTime + 10f;
            Debug.Log($"[QuestUI v2] handSubsystems={hands.Count}, controllerAimControls=L{leftAimPosition.controls.Count}/R{rightAimPosition.controls.Count}, handAimControls=L{leftHandTracked.controls.Count}/R{rightHandTracked.controls.Count}", this);
            foreach (var h in hands)
            {
                bool li = h.leftHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out _);
                bool ri = h.rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out _);
                Debug.Log($"[QuestUI v2] hands running={h.running}, leftTracked={h.leftHand.isTracked}, rightTracked={h.rightHand.isTracked}, indexPose=L{li}/R{ri}", this);
            }
        }
        Drive(leftRay, XRNode.LeftHand, true, ref leftPinch, ref leftSource, ref leftArmed);
        Drive(rightRay, XRNode.RightHand, false, ref rightPinch, ref rightSource, ref rightArmed);
    }
    void Drive(XRRayInteractor ray, XRNode node, bool left, ref bool pinch, ref int previousSource, ref bool armed)
    {
        if (ray == null) return;
        var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
        bool trackedController = device.isValid && (device.characteristics & InputDeviceCharacteristics.Controller) != 0
            && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && tracked;
        bool pressed = false;
        Vector2 scroll = Vector2.zero;
        int source = 0;
        var aimP = left ? leftAimPosition : rightAimPosition;
        var aimQ = left ? leftAimRotation : rightAimRotation;
        Quaternion aimRotation = aimQ.ReadValue<Quaternion>();
        if (trackedController && aimP.controls.Count > 0 && aimQ.controls.Count > 0
            && Quaternion.Dot(aimRotation, aimRotation) > 0.5f)
        {
            Vector3 aimPosition = aimP.ReadValue<Vector3>();
            ray.transform.SetPositionAndRotation(trackingSpace.TransformPoint(aimPosition), trackingSpace.rotation * aimRotation);
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out pressed);
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out scroll);
            pinch = false; source = 1;
        }
        else if (enableHandPinch)
        {
            var hp = left ? leftHandPosition : rightHandPosition;
            var hq = left ? leftHandRotation : rightHandRotation;
            var ht = left ? leftHandTracked : rightHandTracked;
            var click = left ? leftHandPinch : rightHandPinch;
            Quaternion handRotation = hq.ReadValue<Quaternion>();
            if (ht.ReadValue<float>() > 0.5f && hp.controls.Count > 0 && hq.controls.Count > 0
                && Quaternion.Dot(handRotation, handRotation) > 0.5f)
            {
                ray.transform.SetPositionAndRotation(trackingSpace.TransformPoint(hp.ReadValue<Vector3>()), trackingSpace.rotation * handRotation);
                pressed = click.ReadValue<float>() > 0.5f;
                pinch = pressed;
                source = 3;
            }
            foreach (var subsystem in hands)
            {
                if (source == 3) break;
                if (!subsystem.running) continue;
                var hand = left ? subsystem.leftHand : subsystem.rightHand;
                if (!hand.isTracked || !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose index)
                    || !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumb)) continue;
                float gap = Vector3.Distance(index.position, thumb.position);
                pinch = pinch ? gap < Mathf.Max(pinchOpen, pinchClose + 0.005f) : gap < pinchClose;
                Vector3 world = trackingSpace.TransformPoint((index.position + thumb.position) * 0.5f);
                // Shoulder-to-pinch ray: stable baseline, not Meta's native aim pose.
                Vector3 shoulder = headCamera.transform.position + headCamera.transform.right * (left ? -0.16f : 0.16f) - Vector3.up * 0.18f;
                Vector3 direction = world - shoulder;
                if (direction.sqrMagnitude < 0.0025f) continue;
                ray.transform.SetPositionAndRotation(world, Quaternion.LookRotation(direction.normalized, Vector3.up));
                pressed = pinch; source = 2; break;
            }
        }
        bool changed = source != previousSource;
        if (changed)
        {
            Debug.Log($"[QuestUI] {node}: source={source} (0=none,1=controllerAim,2=jointHand,3=MetaHandAim)", this);
            previousSource = source;
            armed = false;
        }
        // On source loss/replacement release before accepting the new press, avoiding stuck UI drags.
        if (!pressed) armed = true;
        bool uiPressed = armed && !changed && source != 0 && pressed;
        ray.uiPressInput.QueueManualState(uiPressed, uiPressed ? 1f : 0f);
        ray.uiScrollInput.manualValue = source == 1 ? scroll : Vector2.zero;
        ray.enableUIInteraction = source != 0;
        var line = ray.GetComponent<LineRenderer>();
        if (line != null) line.enabled = source != 0 && lineMaterial != null;
        if (source == 0) pinch = false;
    }
    void LateUpdate()
    {
        Draw(leftRay); Draw(rightRay);
        LogHover(leftRay, "Left", ref leftHovered);
        LogHover(rightRay, "Right", ref rightHovered);
    }
    void LogHover(XRRayInteractor ray, string hand, ref GameObject previous)
    {
        GameObject current = null;
        if (ray != null && ray.enableUIInteraction && ray.TryGetCurrentUIRaycastResult(out var hit)) current = hit.gameObject;
        if (current == previous) return;
        previous = current;
        Debug.Log($"[QuestUI v3] {hand} UI hover={(current == null ? "none" : current.name)}", this);
    }
    static void Draw(XRRayInteractor ray)
    {
        if (ray == null) return;
        var line = ray.GetComponent<LineRenderer>();
        if (line == null || !line.enabled) return;
        Vector3 end = ray.transform.position + ray.transform.forward * ray.maxRaycastDistance;
        if (ray.TryGetCurrentUIRaycastResult(out var hit)) end = hit.worldPosition;
        line.SetPosition(0, ray.transform.position); line.SetPosition(1, end);
    }
    void OnDisable()
    {
        Release(leftRay); Release(rightRay);
        foreach (var action in ownedActions) action.Disable();
        leftSource = rightSource = 0; leftPinch = rightPinch = false;
    }
    static void Release(XRRayInteractor ray)
    {
        if (ray == null) return;
        ray.uiPressInput.QueueManualState(false, 0f);
        ray.uiScrollInput.manualValue = Vector2.zero;
        ray.enableUIInteraction = false;
        var line = ray.GetComponent<LineRenderer>();
        if (line != null) line.enabled = false;
    }
    void OnDestroy()
    {
        foreach (var action in ownedActions) action.Dispose();
        if (lineMaterial != null) Destroy(lineMaterial);
    }
}
