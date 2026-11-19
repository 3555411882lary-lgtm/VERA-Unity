using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UniVRM10;

/// <summary>
/// 骨骼动作播放器 - 配合 middleware.py 的 motion 协议
/// 播放格式: [{"t": 秒, "bones": {"rightUpperArm": {"x": -80}}}, ...]
/// 角度单位: 度
/// </summary>
[DefaultExecutionOrder(10000)] // 在VRM的LookAt/SpringBone之后执行
public class MotionPlayer : MonoBehaviour
{
    [Header("VRM")]
    public GameObject vrm;
    public Animator animator;
    [HideInInspector] public bool locomotionOwnsHips;

    // boneName(middleware) → HumanBodyBones
    private static readonly Dictionary<string, HumanBodyBones> BoneMap = new Dictionary<string, HumanBodyBones>
    {
        {"hips", HumanBodyBones.Hips},
        {"spine", HumanBodyBones.Spine},
        {"chest", HumanBodyBones.Chest},
        {"upperChest", HumanBodyBones.UpperChest},
        {"neck", HumanBodyBones.Neck},
        {"head", HumanBodyBones.Head},
        {"leftShoulder", HumanBodyBones.LeftShoulder},
        {"rightShoulder", HumanBodyBones.RightShoulder},
        {"leftUpperArm", HumanBodyBones.LeftUpperArm},
        {"rightUpperArm", HumanBodyBones.RightUpperArm},
        {"leftLowerArm", HumanBodyBones.LeftLowerArm},
        {"rightLowerArm", HumanBodyBones.RightLowerArm},
        {"leftHand", HumanBodyBones.LeftHand},
        {"rightHand", HumanBodyBones.RightHand},
        {"leftUpperLeg", HumanBodyBones.LeftUpperLeg},
        {"rightUpperLeg", HumanBodyBones.RightUpperLeg},
        {"leftLowerLeg", HumanBodyBones.LeftLowerLeg},
        {"rightLowerLeg", HumanBodyBones.RightLowerLeg},
        {"leftFoot", HumanBodyBones.LeftFoot},
        {"rightFoot", HumanBodyBones.RightFoot},
        {"leftToes", HumanBodyBones.LeftToes},
        {"leftThumbMetacarpal", HumanBodyBones.LeftThumbProximal},
        {"leftThumbProximal", HumanBodyBones.LeftThumbIntermediate},
        {"leftThumbDistal", HumanBodyBones.LeftThumbDistal},
        {"leftIndexProximal", HumanBodyBones.LeftIndexProximal},
        {"leftIndexIntermediate", HumanBodyBones.LeftIndexIntermediate},
        {"leftIndexDistal", HumanBodyBones.LeftIndexDistal},
        {"leftMiddleProximal", HumanBodyBones.LeftMiddleProximal},
        {"leftMiddleIntermediate", HumanBodyBones.LeftMiddleIntermediate},
        {"leftMiddleDistal", HumanBodyBones.LeftMiddleDistal},
        {"leftRingProximal", HumanBodyBones.LeftRingProximal},
        {"leftRingIntermediate", HumanBodyBones.LeftRingIntermediate},
        {"leftRingDistal", HumanBodyBones.LeftRingDistal},
        {"leftLittleProximal", HumanBodyBones.LeftLittleProximal},
        {"leftLittleIntermediate", HumanBodyBones.LeftLittleIntermediate},
        {"leftLittleDistal", HumanBodyBones.LeftLittleDistal},
        {"rightThumbMetacarpal", HumanBodyBones.RightThumbProximal},
        {"rightThumbProximal", HumanBodyBones.RightThumbIntermediate},
        {"rightThumbDistal", HumanBodyBones.RightThumbDistal},
        {"rightIndexProximal", HumanBodyBones.RightIndexProximal},
        {"rightIndexIntermediate", HumanBodyBones.RightIndexIntermediate},
        {"rightIndexDistal", HumanBodyBones.RightIndexDistal},
        {"rightMiddleProximal", HumanBodyBones.RightMiddleProximal},
        {"rightMiddleIntermediate", HumanBodyBones.RightMiddleIntermediate},
        {"rightMiddleDistal", HumanBodyBones.RightMiddleDistal},
        {"rightRingProximal", HumanBodyBones.RightRingProximal},
        {"rightRingIntermediate", HumanBodyBones.RightRingIntermediate},
        {"rightRingDistal", HumanBodyBones.RightRingDistal},
        {"rightLittleProximal", HumanBodyBones.RightLittleProximal},
        {"rightLittleIntermediate", HumanBodyBones.RightLittleIntermediate},
        {"rightLittleDistal", HumanBodyBones.RightLittleDistal},
        {"rightToes", HumanBodyBones.RightToes},
    };

    // 部分骨骼的旋转轴方向需要取反（VRM骨骼朝向导致）
    private static readonly HashSet<string> FlipX = new HashSet<string> { "rightUpperLeg", "leftUpperLeg" };

    Vector3 FixEuler(string boneName, Vector3 euler)
    {
        if (FlipX.Contains(boneName)) euler.x = -euler.x;
        return euler;
    }
    private static readonly Dictionary<string, string> EmotionMap = new Dictionary<string, string>
    {
        {"joy", "happy"},
        {"angry", "angry"},
        {"sorrow", "sad"},
        {"surprised", "surprised"},
        {"fun", "happy"},
        {"relaxed", "relaxed"},
        {"neutral", "neutral"},
    };

    // motion_engine.py face顺序 → VRM表情预设
    private static readonly string[] FaceOrder = {
        "aa","ih","ou","ee","oh","blinkLeft","blinkRight",
        "happy","angry","sad","relaxed",
        "browUpLeft","browUpRight","browDownLeft","browDownRight",
        "eyeWideLeft","eyeWideRight","cheekPuff","mouthSmile"
    };
    private static readonly Dictionary<string, UniVRM10.ExpressionPreset> FacePresetMap =
        new Dictionary<string, UniVRM10.ExpressionPreset>
    {
        {"aa", UniVRM10.ExpressionPreset.aa},
        {"ih", UniVRM10.ExpressionPreset.ih},
        {"ou", UniVRM10.ExpressionPreset.ou},
        {"ee", UniVRM10.ExpressionPreset.ee},
        {"oh", UniVRM10.ExpressionPreset.oh},
        {"blinkLeft", UniVRM10.ExpressionPreset.blinkLeft},
        {"blinkRight", UniVRM10.ExpressionPreset.blinkRight},
        {"happy", UniVRM10.ExpressionPreset.happy},
        {"angry", UniVRM10.ExpressionPreset.angry},
        {"sad", UniVRM10.ExpressionPreset.sad},
        {"relaxed", UniVRM10.ExpressionPreset.relaxed},
    };

    private Dictionary<HumanBodyBones, Quaternion> initialRot = new Dictionary<HumanBodyBones, Quaternion>();
    // 绝对旋转模式：技能动作使用（相对T-pose，不叠加Animator姿势）
    private bool absoluteMode = false;
    private bool sourceQuaternionClip;
    public enum SkillCoordinateBasis { MirrorZ, AsExported }
    [Header("Quaternion Reply Protocol")]
    [Tooltip("Source coordinate convention is not documented. MirrorZ is an explicit hypothesis; AsExported allows comparison.")]
    public SkillCoordinateBasis skillCoordinateBasis = SkillCoordinateBasis.MirrorZ;
    [Tooltip("Retain VRM's starting pose and apply source motion changes. This does not preserve the source first pose.")]
    public bool rebaseQuaternionSkills = true;
    private bool hastClip;
    public bool HASTPlaying => hastClip && isPlaying;
    [Header("HAST Leg Coordinates")]
    [Tooltip("Leg-only basis correction. Disable for comparison with the previous export mapping.")]
    public bool correctHastLegForward = true;
    public bool ReplyMotionActive => (isPlaying && !idleLoopRunning) || blendFromPose != null;
    private readonly Dictionary<HumanBodyBones, Quaternion> restParentModel = new Dictionary<HumanBodyBones, Quaternion>();
    private bool clipRebase;
    private SkillCoordinateBasis clipBasis;
    private readonly Dictionary<string, Quaternion> sourceFirstPose = new Dictionary<string, Quaternion>();
    [Header("HAST Pelvis Stabilization")]
    [Tooltip("Limit reply pelvis tilt relative to its first frame; preserve yaw. Does not change source motion selection.")]
    public bool stabilizeHastPelvis = true;
    [Range(0f, 30f)] public float maxHastPelvisTilt = 5f;
    private Quaternion hastFirstHips = Quaternion.identity;
    private bool hasHastFirstHips;
    private readonly Dictionary<string, Quaternion> hastFirstPose = new Dictionary<string, Quaternion>();
    private Coroutine motionRoutine;
    [Header("Reply Transition")]
    [Range(0.05f, 3f)] public float returnBlendSeconds = 0.8f;
    private Dictionary<HumanBodyBones, Quaternion> blendFromPose;
    private float blendElapsed = -1f;
    private Dictionary<HumanBodyBones, Quaternion> lastRenderedPose;
    private bool returnIdlePrepared;
    private bool isPlaying;
    private bool idleLoopRunning;
    private float playStartTime;
    private float playDuration;
    private List<MotionFrame> currentFrames;
    private Dictionary<HumanBodyBones, Quaternion> startPose;
    private readonly HashSet<string> clipBones = new HashSet<string>();
    private Vrm10Instance vrm10;
    private string pendingLiveBones;
    private string latestLiveBones;
    private float lastLiveFrameTime = -100f;
    private const float LiveFrameTimeout = 0.5f;
    private bool loggedFirstFrame;
    private bool loggedHead;
    // 响应动作播放期间锁定，live帧不覆盖
    private float motionLockUntil = 0f;
    // jaw/face 表情数据
    private float jawValue;
    private float[] faceValues;
    [Header("Live Pose Tuning")]
    [Range(0f, 1f)] public float liveHeadWeight = 0.059f;
    [Range(0f, 1f)] public float liveNeckWeight = 0.088f;
    [Range(0f, 1f)] public float liveArmWeight = 0.08f;
    [Range(0f, 1f)] public float liveBodyWeight = 0.214f;

    void Start()
    {
        if (animator == null && vrm != null)
            animator = vrm.GetComponent<Animator>();
        if (vrm != null)
            vrm10 = vrm.GetComponent<Vrm10Instance>();
        if (GetComponent<FootAnchorIK>() == null) gameObject.AddComponent<FootAnchorIK>();
        StartCoroutine(CacheInitialRotationsDelayed());
        StartCoroutine(DebugHeadRot());
    }

    IEnumerator CacheInitialRotationsDelayed()
    {
        yield return null;
        yield return null;
        CacheInitialRotations();
        foreach (var kv in BoneMap)
        {
            var t = animator.GetBoneTransform(kv.Value);
            if (t != null) Debug.Log($"[RestPose] {kv.Key} ({t.name}): {t.localRotation.eulerAngles}");
        }
    }

    IEnumerator DebugHeadRot()
    {
        yield return new WaitForSeconds(1f);
        if (animator == null) { Debug.LogWarning("[MotionPlayer] animator null"); yield break; }
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head != null)
            Debug.Log($"[MotionPlayer] idle Head rot after 1s: {head.localRotation.eulerAngles}, boneName={head.name}, parent={head.parent?.name}");
        else
            Debug.LogWarning("[MotionPlayer] Head bone not found!");
    }

    void CacheInitialRotations()
    {
        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
        }
        initialRot.Clear();
        if (animator == null) return;
        foreach (var kv in BoneMap)
        {
            var t = animator.GetBoneTransform(kv.Value);
            if (t != null)
            {
                initialRot[kv.Value] = t.localRotation;
                restParentModel[kv.Value] = Quaternion.Inverse(animator.transform.rotation) * t.parent.rotation;
                Debug.Log($"[MotionPlayer] initialRot {kv.Key}: {t.localRotation.eulerAngles} parentWorld={(t.parent != null ? t.parent.rotation.eulerAngles : Vector3.zero)}");
            }
        }
    }

    /// <summary>播放动作队列（json字符串）</summary>
    // Keep only the latest response motion for each mode; never dump the continuous live stream.
    void SaveReplyMotion(string mode, string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
#if UNITY_EDITOR
            string directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "MotionDiagnostics"));
#else
            string directory = System.IO.Path.Combine(Application.persistentDataPath, "MotionDiagnostics");
#endif
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "last_reply_" + mode + ".json");
            System.IO.File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            Debug.Log("[MotionDump] Full reply motion saved: " + path);
        }
        catch (Exception error)
        {
            Debug.LogWarning("[MotionDump] Could not save motion: " + error.Message);
        }
    }

    public void PlayMotion(string emotion, string motionJson, float duration)
    {
        hastClip = false;
        SaveReplyMotion("relative", motionJson);
        Debug.Log($"[MotionPlayer] PlayMotion called: emotion={emotion}, jsonLen={motionJson?.Length ?? 0}, duration={duration}");
        // 表情
        SetEmotion(emotion);

        // 动作
        if (!string.IsNullOrEmpty(motionJson))
        {
            try
            {
                var frames = ParseFrames(motionJson);
                Debug.Log($"[MotionPlayer] parsed {frames?.Count ?? 0} frames");
                if (frames != null && frames.Count > 0)
                {
                    if (motionRoutine != null) StopCoroutine(motionRoutine);
                    blendFromPose = null; blendElapsed = -1f;
                    // 确保Animator开启（可能被ApplyLiveFrame或上一轮绝对模式关掉）
                    if (animator != null && !animator.enabled) animator.enabled = true;
                    pendingLiveBones = null;
                    idleLoopRunning = false;
                    absoluteMode = false;
                    motionLockUntil = Time.time + duration;
                    motionRoutine = StartCoroutine(PlayFrames(frames, duration));
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Motion] 解析失败: {e.Message}");
            }
        }
    }

    /// <summary>播放技能动作（绝对旋转，相对T-pose）</summary>
    public void PlaySkill(string emotion, string motionJson, float duration)
    {
        SaveReplyMotion("absolute", motionJson);
        Debug.Log($"[MotionPlayer] PlaySkill called: emotion={emotion}, jsonLen={motionJson?.Length ?? 0}, duration={duration}");
        SetEmotion(emotion);
        if (string.IsNullOrEmpty(motionJson)) return;
        try
        {
            var frames = ParseFrames(motionJson);
            if (frames != null && frames.Count > 0)
            {
                if (motionRoutine != null) StopCoroutine(motionRoutine);
                    blendFromPose = null; blendElapsed = -1f;
                pendingLiveBones = null;
                idleLoopRunning = false;
                motionLockUntil = Time.time + duration;
                motionRoutine = StartCoroutine(PlayFramesAbsolute(frames, duration));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Motion] 技能解析失败: {e.Message}");
        }
    }

    public void PlayQuaternionSkill(string emotion, string motionJson, float duration)
    {
        hastClip = false;
        PlaySkill(emotion, motionJson, duration);
    }

    public void PlayHastClip(string emotion, string motionJson, float duration)
    {
        hastClip = true;
        PlaySkill(emotion, motionJson, duration);
    }

    /// <summary>实时帧直接应用（绝对旋转，不插值）</summary>
    public void ApplyLiveFrame(string bonesJson)
    {
        if (animator == null || string.IsNullOrEmpty(bonesJson)) return;
        // 响应动作播放期间保留最新流帧，但不覆盖回复动作。
        latestLiveBones = bonesJson;
        lastLiveFrameTime = Time.unscaledTime;
        if (isPlaying && !idleLoopRunning || blendFromPose != null) return;
        if (idleLoopRunning && motionRoutine != null) StopCoroutine(motionRoutine);
        idleLoopRunning = false; motionRoutine = null;
        absoluteMode = true;
        isPlaying = false;
        currentFrames = null;
        pendingLiveBones = bonesJson;
        if (!loggedFirstFrame) {
            loggedFirstFrame = true;
            foreach (var kv in BoneMap) {
                var t = animator.GetBoneTransform(kv.Value);
                Debug.Log($"[BoneMap] {kv.Key} -> {kv.Value} -> {(t != null ? t.name : "NULL")}");
            }
        }
    }

    /// <summary>循环播放idle动作（绝对旋转）</summary>
    public void PlayIdleLoop(string motionJson)
    {
        if (string.IsNullOrEmpty(motionJson)) return;
        try
        {
            var frames = ParseFrames(motionJson);
            if (frames != null && frames.Count > 0)
            {
                idleFrames = frames;
                if (Time.unscaledTime - lastLiveFrameTime > LiveFrameTimeout && !idleLoopRunning && !isPlaying && blendFromPose == null)
                    motionRoutine = StartCoroutine(IdleLoopRoutine(frames));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Motion] idle解析失败: {e.Message}");
        }
    }

    IEnumerator IdleLoopRoutine(List<MotionFrame> frames)
    {
        idleLoopRunning = true;
        absoluteMode = true;
        isPlaying = true;
        currentFrames = frames;
        sourceQuaternionClip = false;
        PrepareEulerClip(frames);
        float dur = frames[frames.Count - 1].t;
        if (dur <= 0.1f) dur = 1.9f;

        while (true)
        {
            if (Time.unscaledTime - lastLiveFrameTime <= LiveFrameTimeout)
            {
                idleLoopRunning = false;
                isPlaying = false;
                currentFrames = null;
                motionRoutine = null;
                yield break;
            }
            playStartTime = Time.time;
            playDuration = dur;
            yield return new WaitForSeconds(dur);
        }
    }

    IEnumerator PlayFramesAbsolute(List<MotionFrame> frames, float totalDuration)
    {
        absoluteMode = true;
        // 不再关闭Animator：帧应用已移至LateUpdate，会在Animator之后覆盖骨骼
        isPlaying = true;
        playStartTime = Time.time;
        playDuration = totalDuration;
        currentFrames = frames;
        sourceQuaternionClip = frames.Count > 0 && frames[0].rotations.Count > 0;
        if (!sourceQuaternionClip)
            Debug.LogWarning("[MotionV1] Legacy absolute Euler clip received. Update server to source_quaternion_v1; old skill exports do not use Unity Euler order.");
        PrepareEulerClip(frames);

        // Motion plays on its own timeline; TTS duration does not hold the body frozen.
        float clipDuration = frames.Count > 0 ? frames[frames.Count - 1].t : totalDuration;
        yield return new WaitForSeconds(clipDuration);

        BeginReturnBlend();
    }

    private List<MotionFrame> idleFrames;

    /// <summary>设置idle帧（不立即播）</summary>
    public void SetIdleFrames(string motionJson)
    {
        if (string.IsNullOrEmpty(motionJson)) return;
        try { idleFrames = ParseFrames(motionJson); }
        catch (Exception e) { Debug.LogWarning($"[Motion] idle设置失败: {e.Message}"); }
    }

    // ===== 表情 =====
    void SetEmotion(string emotion)
    {
        if (vrm10 == null || vrm10.Runtime == null) return;

        var expr = vrm10.Runtime.Expression;
        if (expr == null) return;

        // 先全部归零
        foreach (var kv in EmotionMap)
        {
            var key = ExpressionKey.CreateFromPreset(GetPreset(kv.Value));
            expr.SetWeight(key, 0f);
        }

        if (EmotionMap.TryGetValue(emotion, out string presetName))
        {
            var key = ExpressionKey.CreateFromPreset(GetPreset(presetName));
            expr.SetWeight(key, 1f);
        }
    }

    static UniVRM10.ExpressionPreset GetPreset(string name)
    {
        switch (name)
        {
            case "happy": return UniVRM10.ExpressionPreset.happy;
            case "angry": return UniVRM10.ExpressionPreset.angry;
            case "sad": return UniVRM10.ExpressionPreset.sad;
            case "surprised": return UniVRM10.ExpressionPreset.surprised;
            case "relaxed": return UniVRM10.ExpressionPreset.relaxed;
            default: return UniVRM10.ExpressionPreset.neutral;
        }
    }

    // ===== 骨骼动作 =====
    List<MotionFrame> ParseFrames(string json)
    {
        // [{"t":0,"bones":{"rightUpperArm":{"x":-80}}},...]
        var frames = new List<MotionFrame>();

        // 分段提取每个帧对象
        int i = 0;
        while (i < json.Length)
        {
            int start = json.IndexOf('{', i);
            if (start < 0) break;
            int depth = 0;
            int end = -1;
            bool inString = false;
            for (int j = start; j < json.Length; j++)
            {
                char c = json[j];
                if (inString)
                {
                    if (c == '\\') j++;
                    else if (c == '\"') inString = false;
                    continue;
                }
                if (c == '\"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { end = j; break; } }
            }
            if (end < 0) break;

            string frameJson = json.Substring(start, end - start + 1);
            var frame = ParseFrame(frameJson);
            if (frame != null) frames.Add(frame);
            i = end + 1;
        }
        frames.Sort((a, b) => a.t.CompareTo(b.t));
        for (int index = 1; index < frames.Count; index++)
        {
            if (frames[index].t <= frames[index - 1].t)
                frames[index].t = frames[index - 1].t + 0.001f;
        }
        return frames;
    }

    MotionFrame ParseFrame(string json)
    {
        var frame = new MotionFrame();
        string tStr = ExtractValue(json, "t");
        frame.t = float.TryParse(tStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? Mathf.Max(0f, t) : 0f;
        frame.bones = new Dictionary<string, Vector3>();

        // 提取bones对象
        int bonesIdx = json.IndexOf("\"bones\"");
        if (bonesIdx < 0) return frame;
        int braceStart = json.IndexOf('{', bonesIdx);
        int depth = 0, braceEnd = -1;
        bool inString = false;
        for (int j = braceStart; j < json.Length; j++)
        {
            char c = json[j];
            if (inString)
            {
                if (c == '\\') j++;
                else if (c == '\"') inString = false;
                continue;
            }
            if (c == '\"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) { braceEnd = j; break; } }
        }
        if (braceEnd < 0) return frame;
        string bonesJson = json.Substring(braceStart + 1, braceEnd - braceStart - 1);

        // 提取每个骨骼 {"name":{"x":..,"y":..,"z":..}}
        int p = 0;
        while (p < bonesJson.Length)
        {
            int qStart = bonesJson.IndexOf('"', p);
            if (qStart < 0) break;
            int qEnd = bonesJson.IndexOf('"', qStart + 1);
            if (qEnd < 0) break;
            string boneName = bonesJson.Substring(qStart + 1, qEnd - qStart - 1);

            int rotStart = bonesJson.IndexOf('{', qEnd);
            if (rotStart < 0) break;
            depth = 0;
            int rotEnd = -1;
            bool inObjectString = false;
            for (int j = rotStart; j < bonesJson.Length; j++)
            {
                char c = bonesJson[j];
                if (inObjectString)
                {
                    if (c == '\\') j++;
                    else if (c == '"') inObjectString = false;
                    continue;
                }
                if (c == '"') inObjectString = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { rotEnd = j; break; } }
            }
            if (rotEnd < 0) break;
            string rotJson = bonesJson.Substring(rotStart, rotEnd - rotStart + 1);

            if (rotJson.Contains("\"qw\""))
            {
                Quaternion q = new Quaternion(GetAxis(rotJson, "qx"), GetAxis(rotJson, "qy"), GetAxis(rotJson, "qz"), GetAxis(rotJson, "qw"));
                float length = Mathf.Sqrt(Quaternion.Dot(q, q));
                if (float.IsNaN(length) || float.IsInfinity(length) || length < 0.00001f)
                    throw new FormatException("Invalid reply quaternion: " + boneName);
                frame.rotations[boneName] = new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
                p = rotEnd + 1;
                continue;
            }
            Vector3 euler = new Vector3(
                GetAxis(rotJson, "x"),
                GetAxis(rotJson, "y"),
                GetAxis(rotJson, "z")
            );
            frame.bones[boneName] = euler;
            p = rotEnd + 1;
        }
        return frame;
    }

    float GetAxis(string rotJson, string axis)
    {
        return float.TryParse(ExtractValue(rotJson, axis), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && !float.IsNaN(v) && !float.IsInfinity(v) ? v : 0f;
    }

    string ExtractValue(string json, string key)
    {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) return null;
        idx = json.IndexOf(':', idx + pattern.Length);
        if (idx < 0) return null;
        idx++;
        while (idx < json.Length && (json[idx] == ' ')) idx++;
        int end = idx;
        while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
        return json.Substring(idx, end - idx).Trim();
    }

    IEnumerator PlayFrames(List<MotionFrame> frames, float totalDuration)
    {
        isPlaying = true;
        playStartTime = Time.time;
        playDuration = totalDuration;
        currentFrames = frames;
        sourceQuaternionClip = false;
        PrepareEulerClip(frames);

        float clipDuration = frames.Count > 0 ? frames[frames.Count - 1].t : totalDuration;
        yield return new WaitForSeconds(clipDuration);

        BeginReturnBlend();
    }

    private bool needCapturePose;

    /// <summary>设置jaw和face表情数据（来自连续帧推送）</summary>
    public void ApplyFaceFrame(float jaw, string faceJson)
    {
        jawValue = Mathf.Clamp01(jaw);
        if (!string.IsNullOrEmpty(faceJson) && faceJson.Length >= 2 && faceJson[0] == '[')
        {
            string inner = faceJson.Substring(1, faceJson.Length - 2);
            var parts = inner.Split(',');
            var arr = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out arr[i]);
            faceValues = arr;
        }
    }

    /// <summary>把jaw/face应用到VRM表情（每帧调用）</summary>
    void ApplyFacial()
    {
        if (vrm10 == null || vrm10.Runtime == null || vrm10.Runtime.Expression == null) return;
        // 响应动作播放期间由SetEmotion控制表情，不覆盖
        if (Time.time < motionLockUntil) return;

        var expr = vrm10.Runtime.Expression;

        // 先清零映射的表情
        foreach (var kv in FacePresetMap)
            expr.SetWeight(ExpressionKey.CreateFromPreset(kv.Value), 0f);

        // 应用face数据
        if (faceValues != null)
        {
            int n = Mathf.Min(faceValues.Length, FaceOrder.Length);
            for (int i = 0; i < n; i++)
            {
                if (FacePresetMap.TryGetValue(FaceOrder[i], out var preset))
                    expr.SetWeight(ExpressionKey.CreateFromPreset(preset), Mathf.Clamp01(faceValues[i]));
            }
        }

        // jaw → aa表情，与face里的aa取大值
        var aaKey = ExpressionKey.CreateFromPreset(UniVRM10.ExpressionPreset.aa);
        float aaWeight = Mathf.Clamp01(jawValue);
        if (faceValues != null && faceValues.Length > 0)
            aaWeight = Mathf.Max(aaWeight, Mathf.Clamp01(faceValues[0]));
        expr.SetWeight(aaKey, aaWeight);
    }

    public void InterruptPresenceMotion()
    {
        if(motionRoutine!=null)StopCoroutine(motionRoutine);
        motionRoutine=null;isPlaying=false;idleLoopRunning=false;
        currentFrames=null;hastClip=false;blendFromPose=null;
        latestLiveBones=null;pendingLiveBones=null;lastLiveFrameTime=-1000f;
        jawValue=0;faceValues=null;
    }

    void LateUpdate()
    {
        if (animator == null) return;
        if (blendFromPose != null)
        {
            pendingLiveBones = null;
            blendElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(blendElapsed / Mathf.Max(0.05f, returnBlendSeconds));
            ApplyBlendTransition(t * t * (3f - 2f * t));
            if (t >= 1f)
            {
                blendFromPose = null; blendElapsed = -1f;
                if (returnIdlePrepared && !(latestLiveBones != null && Time.unscaledTime-lastLiveFrameTime <= LiveFrameTimeout))
                    motionRoutine = StartCoroutine(IdleLoopRoutine(idleFrames));
            }
        }
        else if (isPlaying && currentFrames != null)
            ApplyFrameAt(currentFrames, Time.time - playStartTime);
        else if (latestLiveBones != null && Time.unscaledTime-lastLiveFrameTime <= LiveFrameTimeout)
        {
            ApplyLiveBonesNow(latestLiveBones);
            pendingLiveBones = null;
        }
        else if (idleFrames != null && idleFrames.Count > 0 && !idleLoopRunning)
            motionRoutine = StartCoroutine(IdleLoopRoutine(idleFrames));
        lastRenderedPose = CaptureCurrentPose();
        ApplyFacial();
    }

    void ApplyLiveBonesNow(string bonesJson)
    {
        int p = 0;
        while (p < bonesJson.Length)
        {
            int qStart = bonesJson.IndexOf('"', p);
            if (qStart < 0) break;
            int qEnd = bonesJson.IndexOf('"', qStart + 1);
            if (qEnd < 0) break;
            string boneName = bonesJson.Substring(qStart + 1, qEnd - qStart - 1);
            int rotStart = bonesJson.IndexOf('{', qEnd);
            if (rotStart < 0) break;
            int depth = 0, rotEnd = -1;
            for (int j = rotStart; j < bonesJson.Length; j++)
            {
                if (bonesJson[j] == '{') depth++;
                else if (bonesJson[j] == '}') { depth--; if (depth == 0) { rotEnd = j; break; } }
            }
            if (rotEnd < 0) break;
            string rotJson = bonesJson.Substring(rotStart, rotEnd - rotStart + 1);
            if (!(locomotionOwnsHips && boneName == "hips") && BoneMap.TryGetValue(boneName, out var hb))
            {
                float qx = GetAxis(rotJson, "qx");
                float qy = GetAxis(rotJson, "qy");
                float qz = GetAxis(rotJson, "qz");
                float qw = GetAxis(rotJson, "qw");
                float px = GetAxis(rotJson, "px");
                float py = GetAxis(rotJson, "py");
                float pz = GetAxis(rotJson, "pz");
                float pw = GetAxis(rotJson, "pw");
                Transform bone = animator.GetBoneTransform(hb);
                if (bone != null)
                {
                    // HAST output is right-handed; mirror Y to Unity's handedness and apply as a delta from VRM's rest pose.
                    Quaternion deltaUnity = new Quaternion(-qx, qy, -qz, qw);
                    float qMagnitude = Mathf.Sqrt(deltaUnity.x * deltaUnity.x + deltaUnity.y * deltaUnity.y + deltaUnity.z * deltaUnity.z + deltaUnity.w * deltaUnity.w);
                    if (qMagnitude < 1e-4f) deltaUnity = Quaternion.identity;
                    else
                    {
                        float invMagnitude = 1f / qMagnitude;
                        deltaUnity = new Quaternion(deltaUnity.x * invMagnitude, deltaUnity.y * invMagnitude, deltaUnity.z * invMagnitude, deltaUnity.w * invMagnitude);
                    }
                    Quaternion restUnity = initialRot.TryGetValue(hb, out var rr) ? rr : Quaternion.identity;
                    float weight = GetLiveWeight(boneName);
                    Quaternion target = restUnity * deltaUnity;
                    bone.localRotation = Quaternion.Slerp(restUnity, target, weight);
                }
            }
            p = rotEnd + 1;
        }
    }

    Quaternion ConvertSkillBasis(Quaternion q)
    {
        return clipBasis == SkillCoordinateBasis.MirrorZ ? new Quaternion(-q.x, -q.y, q.z, q.w) : q;
    }

    Quaternion ConvertHastRotation(string name, Quaternion raw)
    {
        bool leg = name.EndsWith("UpperLeg") || name.EndsWith("LowerLeg") || name.EndsWith("Foot") || name.EndsWith("Toes");
        // Basis change, not quaternion inversion: preserve a valid rotation.
        // Keep upper body mapping unchanged. Apply the same leg basis to the reference frame.
        if (correctHastLegForward && leg)
            return new Quaternion(raw.x, -raw.y, -raw.z, raw.w);
        return new Quaternion(-raw.x, -raw.y, raw.z, raw.w);
    }

    Quaternion StabilizeHastHips(Quaternion current)
    {
        if (!stabilizeHastPelvis || !hasHastFirstHips) return current;
        Quaternion delta = Quaternion.Inverse(hastFirstHips) * current;
        // Swing/twist decomposition around normalized model up; keep yaw intact.
        float length = Mathf.Sqrt(delta.y * delta.y + delta.w * delta.w);
        Quaternion twist = length > 0.00001f
            ? new Quaternion(0f, delta.y / length, 0f, delta.w / length)
            : Quaternion.identity;
        Quaternion swing = delta * Quaternion.Inverse(twist);
        float angle = Quaternion.Angle(Quaternion.identity, swing);
        float weight = angle > 0.00001f ? Mathf.Min(1f, maxHastPelvisTilt / angle) : 1f;
        return hastFirstHips * Quaternion.Slerp(Quaternion.identity, swing, weight) * twist;
    }

    Quaternion QuaternionTarget(MotionFrame frame, string name, HumanBodyBones bone)
    {
        Quaternion baseline = startPose.TryGetValue(bone, out var captured) ? captured : Quaternion.identity;
        if (!frame.rotations.TryGetValue(name, out var raw)) return baseline;
        if (hastClip)
        {
            // HAST emits normalized local rotations around its own standard pose.
            // Rebase the whole clip onto the pose captured when the reply started,
            // so the first HAST frame cannot snap the avatar to T-pose.
            Quaternion q = ConvertHastRotation(name, raw);
            if (name == "hips") q = StabilizeHastHips(q);
            Quaternion parent = restParentModel.TryGetValue(bone, out var p) ? p : Quaternion.identity;
            Quaternion rest = initialRot.TryGetValue(bone, out var r) ? r : Quaternion.identity;
            Quaternion hastLocal = Quaternion.Inverse(parent) * q * parent * rest;
            if (!hastFirstPose.TryGetValue(name, out var firstHastLocal))
                return baseline;
            return baseline * (Quaternion.Inverse(firstHastLocal) * hastLocal);
        }
        Quaternion value = ConvertSkillBasis(raw);
        if (!clipRebase) return value;
        if (!sourceFirstPose.TryGetValue(name, out var reference)) return baseline;
        return baseline * (Quaternion.Inverse(reference) * value);
    }

    void ApplyQuaternionBlend(MotionFrame a, MotionFrame b, float alpha)
    {
        if (animator == null) return;
        foreach (string name in clipBones)
        {
            if (locomotionOwnsHips && name == "hips") continue;
            var bone = BoneMap[name];
            var t = animator.GetBoneTransform(bone);
            if (t != null) t.localRotation = Quaternion.Slerp(QuaternionTarget(a, name, bone), QuaternionTarget(b, name, bone), Mathf.Clamp01(alpha));
        }
    }

    float GetLiveWeight(string boneName)
    {
        if (boneName == "head") return liveHeadWeight;
        if (boneName == "neck") return liveNeckWeight;
        if (boneName.Contains("UpperArm") || boneName.Contains("LowerArm") || boneName.Contains("Shoulder")) return liveArmWeight;
        return liveBodyWeight;
    }

    void OnAnimatorIK(int layerIndex)
    {
        // 帧应用已移至LateUpdate，这里不再处理
        // （Animator disabled时OnAnimatorIK不会被调用）
    }

    void RestorePose(Dictionary<HumanBodyBones, Quaternion> pose)
    {
        foreach (var kv in pose)
        {
            var t = animator.GetBoneTransform(kv.Key);
            if (t != null) t.localRotation = kv.Value;
        }
    }

    Dictionary<HumanBodyBones, Quaternion> CaptureCurrentPose()
    {
        var pose = new Dictionary<HumanBodyBones, Quaternion>();
        if (animator == null) return pose;
        // 捕获所有HumanBodyBones的姿势，不只是BoneMap里的
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone == HumanBodyBones.LastBone) continue;
            var t = animator.GetBoneTransform(bone);
            if (t != null) pose[bone] = t.localRotation;
        }
        return pose;
    }

    void BlendFromStartPose(Dictionary<HumanBodyBones, Quaternion> startPose, float w)
    {
        foreach (var kv in BoneMap)
        {
            var t = animator.GetBoneTransform(kv.Value);
            if (t == null) continue;
            if (!startPose.TryGetValue(kv.Value, out var startRot)) continue;
            t.localRotation = Quaternion.Slerp(startRot, t.localRotation, w);
        }
    }

    void BeginReturnBlend()
    {
        // Coroutines resume before Animator evaluation: use the last pose actually rendered.
        blendFromPose = lastRenderedPose != null
            ? new Dictionary<HumanBodyBones, Quaternion>(lastRenderedPose) : CaptureCurrentPose();
        blendElapsed = 0f;
        isPlaying = false; idleLoopRunning = false; currentFrames = null;
        pendingLiveBones = null; motionRoutine = null; motionLockUntil = 0f;
        returnIdlePrepared = false;
        Debug.Log($"[MotionBlend] Clip finished; returnSeconds={returnBlendSeconds:F2}. Body no longer waits for audio.");
    }

    void ApplyBlendTransition(float t)
    {
        // Always build a clean destination, never use the previous blended pose as the destination.
        foreach (var kv in initialRot)
        {
            if (locomotionOwnsHips && kv.Key == HumanBodyBones.Hips) continue;
            var bone = animator.GetBoneTransform(kv.Key);
            if (bone != null) bone.localRotation = kv.Value;
        }
        if (latestLiveBones != null && Time.unscaledTime-lastLiveFrameTime <= LiveFrameTimeout)
            ApplyLiveBonesNow(latestLiveBones);
        else if (idleFrames != null && idleFrames.Count > 0)
        {
            if (!returnIdlePrepared)
            {
                absoluteMode = true; sourceQuaternionClip = false;
                PrepareEulerClip(idleFrames); returnIdlePrepared = true;
            }
            // Blend to the idle first pose; idle starts from that exact pose after transition.
            ApplyFrameAt(idleFrames, 0f);
        }
        foreach (var kv in BoneMap)
        {
            if (locomotionOwnsHips && kv.Value == HumanBodyBones.Hips) continue;
            var bone = animator.GetBoneTransform(kv.Value);
            if (bone != null && blendFromPose.TryGetValue(kv.Value, out var from))
                bone.localRotation = Quaternion.Slerp(from, bone.localRotation, t);
        }
    }

    void ApplyFrameAt(List<MotionFrame> frames, float elapsed)
    {
        // 找当前帧和后一帧插值
        MotionFrame prev = null, next = null;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].t <= elapsed)
                prev = frames[i];
            else
            {
                next = frames[i];
                break;
            }
        }
        if (prev == null) prev = frames[0];
        if (next == null) { if (sourceQuaternionClip) ApplyQuaternionBlend(prev, prev, 0f); else ApplyPose(prev.bones, 1f); return; }

        float span = Mathf.Max(0.001f, next.t - prev.t);
        float alpha = Mathf.Clamp01((elapsed - prev.t) / span);
        // 缓动：ease in-out，让动作更自然
        alpha = alpha * alpha * (3f - 2f * alpha);
        if (sourceQuaternionClip) ApplyQuaternionBlend(prev, next, alpha);
        else ApplyPoseBlend(prev.bones, next.bones, alpha);
    }

    // Capture once, not from the previous rendered frame: relative rotations must not accumulate.
    void PrepareEulerClip(List<MotionFrame> frames)
    {
        startPose = CaptureCurrentPose();
        clipBones.Clear();
        sourceFirstPose.Clear();
        hastFirstPose.Clear();
        hasHastFirstHips = false;
        clipRebase = rebaseQuaternionSkills;
        clipBasis = skillCoordinateBasis;
        if (sourceQuaternionClip)
        {
            foreach (var entry in frames[0].rotations)
            {
                Quaternion raw = entry.Value;
                Quaternion q = ConvertHastRotation(entry.Key, raw);
                if (entry.Key == "hips") { hastFirstHips = q; hasHastFirstHips = true; }
                if (BoneMap.TryGetValue(entry.Key, out var firstBone))
                {
                    Quaternion parent = restParentModel.TryGetValue(firstBone, out var p) ? p : Quaternion.identity;
                    Quaternion rest = initialRot.TryGetValue(firstBone, out var r) ? r : Quaternion.identity;
                    hastFirstPose[entry.Key] = Quaternion.Inverse(parent) * q * parent * rest;
                }
                sourceFirstPose[entry.Key] = ConvertSkillBasis(entry.Value);
            }
            foreach (var frame in frames)
                foreach (var bone in frame.rotations.Keys)
                    if (BoneMap.ContainsKey(bone)) clipBones.Add(bone);
            Debug.Log($"[MotionV1] sourceQuaternion=true, rebase={clipRebase}, basis={clipBasis}, frames={frames.Count}, bones={clipBones.Count}. No Euler conversion, hinge filter or segment gain.");
            return;
        }
        foreach (var frame in frames)
        {
            if (frame.bones == null) continue;
            foreach (var bone in frame.bones.Keys)
                if (BoneMap.ContainsKey(bone)) clipBones.Add(bone);
        }
        Debug.Log($"[MotionFix] Euler clip: absolute={absoluteMode}, animatedBones={clipBones.Count}, frames={frames.Count}; fixed baseline, unmentioned bones untouched.");
    }

    Quaternion ClipTarget(string name, HumanBodyBones bone, Dictionary<string, Vector3> pose)
    {
        Quaternion baseline = startPose != null && startPose.TryGetValue(bone, out var captured)
            ? captured : (initialRot.TryGetValue(bone, out var initial) ? initial : Quaternion.identity);
        // Missing key means no command: use captured pose for absolute clips, zero offset for relative clips.
        if (pose == null || !pose.TryGetValue(name, out var angles)) return baseline;
        Quaternion value = Quaternion.Euler(FixEuler(name, angles));
        return absoluteMode ? value : baseline * value;
    }

    void ApplyPoseBlend(Dictionary<string, Vector3> a, Dictionary<string, Vector3> b, float alpha)
    {
        if (animator == null) return;
        foreach (var name in clipBones)
        {
            if (locomotionOwnsHips && name == "hips") continue;
            var bone = BoneMap[name];
            var target = animator.GetBoneTransform(bone);
            if (target == null) continue;
            target.localRotation = Quaternion.Slerp(ClipTarget(name, bone, a), ClipTarget(name, bone, b), Mathf.Clamp01(alpha));
        }
    }

    void ApplyPose(Dictionary<string, Vector3> pose, float weight)
    {
        if (animator == null) return;
        foreach (var name in clipBones)
        {
            if (locomotionOwnsHips && name == "hips") continue;
            var bone = BoneMap[name];
            var target = animator.GetBoneTransform(bone);
            if (target == null) continue;
            Quaternion baseline = startPose != null && startPose.TryGetValue(bone, out var captured) ? captured : target.localRotation;
            target.localRotation = Quaternion.Slerp(baseline, ClipTarget(name, bone, pose), Mathf.Clamp01(weight));
        }
    }

    void ResetBones()
    {
        foreach (var kv in BoneMap)
        {
            var t = animator.GetBoneTransform(kv.Value);
            if (t != null && initialRot.ContainsKey(kv.Value))
                t.localRotation = initialRot[kv.Value];
        }
    }

    public class MotionFrame
    {
        public readonly Dictionary<string, Quaternion> rotations = new Dictionary<string, Quaternion>();
        public float t;
        public Dictionary<string, Vector3> bones;
    }
}