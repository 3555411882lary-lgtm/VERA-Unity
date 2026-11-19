using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.Hands;
using TMPro;
//using UnityEngine.XR.OpenXR.Features.MetaQuestMotion;
//using Unity.Collections;

/// <summary>
/// 主场景控制器 - Quest 3 透传 + VRM Avatar + 手柄交互
/// 右手 A：语音录/停（不按 Grip 时）
/// 右手 B：切换文字输入框；左右 Trigger 专用于 UI
/// 手柄射线指向UI：点击交互
/// </summary>
public class VRMController : MonoBehaviour
{
    [Header("VRM模型")]
    public GameObject vrmAvatar;
    public Vector3 avatarOffset = new Vector3(0, 0, 1.5f);
    public float avatarRotationY = 180f;
    public MotionPlayer motionPlayer;

    [Header("XR")]
    public GameObject xrOrigin;

    [Header("UI引用")]
    public TMP_Text statusText;
    public TMP_InputField chatInput;
    public TMP_Text chatLog;


    // 状态
    private TouchScreenKeyboard chatKeyboard;
    private bool chatKeyboardWasVisible;
    private bool isRecording = false;
    private bool rightTriggerDown = false;
    private bool leftTriggerDown = false;
    private bool leftScreenshotTriggerDown = false;
    private bool leftPinchDown = false;
    private float lastScreenshotRequest = -10f;
    private AudioClip recordedClip;
    private string chatLogText = "";
    private int recordingStartSample;
    private const int MaxRecordingSeconds = 30;

    // XR设备
    private InputDevice rightController;
    private InputDevice leftController;


    void Start()
    {
        // 确保WebSocket连接
        if (WebSocketClient.Instance == null)
        {
            var go = new GameObject("WebSocketClient");
            go.AddComponent<WebSocketClient>();
        }

        var presence=GetComponent<VRMPresence>();
        if (!presence) presence=gameObject.AddComponent<VRMPresence>();
        presence.motion=motionPlayer;
        if(vrmAvatar) presence.avatar=vrmAvatar.GetComponentInChildren<Animator>();
        presence.walker=FindObjectOfType<QuestProceduralWalker>();
        if (!presence.viewCamera) presence.viewCamera=Camera.main;
        presence.session=WebSocketClient.Instance.PresenceId;
        presence.token=VRMConnectionMenu.Token;
        if (!GetComponent<VRMConnectionMenu>()) gameObject.AddComponent<VRMConnectionMenu>();
        presence.enabled=true;
        // 注册WS事件
        WebSocketClient.Instance.OnConnected += OnWSConnected;
        WebSocketClient.Instance.OnDisconnected += OnWSDisconnected;
        WebSocketClient.Instance.OnTextResponse += OnTextResponse;
        WebSocketClient.Instance.OnError += OnServerError;
        WebSocketClient.Instance.OnAudioReceived += OnAudioReceived;
        WebSocketClient.Instance.OnMotionResponse += OnMotionResponse;
        WebSocketClient.Instance.OnIdleReceived += OnIdleReceived;
        WebSocketClient.Instance.OnFrameReceived += OnFrameReceived;

        // 定位VRM
        if (vrmAvatar != null)
        {
            vrmAvatar.transform.position = avatarOffset;
            vrmAvatar.transform.rotation = Quaternion.Euler(0, avatarRotationY, 0);
        }

        UpdateStatus("连接中...");
    }

    void Update()
    {
        SyncChatKeyboard();
        ReadController(XRNode.RightHand, InputDeviceCharacteristics.Right, ref rightController);
        ReadController(XRNode.LeftHand, InputDeviceCharacteristics.Left, ref leftController);
        bool voiceKey = Button(rightController, CommonUsages.primaryButton) && !Button(rightController, CommonUsages.gripButton);
        voiceKey |= rightTriggerVoice && Trigger(rightController);
        bool chatKey = Button(rightController, CommonUsages.secondaryButton) || Button(rightController, CommonUsages.menuButton);
        bool yPressed = Button(leftController, CommonUsages.secondaryButton);
        if (yPressed && !leftYHeld)
        {
            if (debugPanel && !transform.IsChildOf(debugPanel.transform))
                debugPanel.SetActive(!debugPanel.activeSelf);
            else
                Debug.LogWarning("[VRMInput] Assign the debug UI panel in VRMController.", this);
        }
        leftYHeld = yPressed;
        bool shotKey = Trigger(leftController);
        if (!rightController.isValid || !leftController.isValid)
        {
            if (hands == null || !hands.running)
            {
                SubsystemManager.GetSubsystems(handSystems);
                hands = handSystems.Find(system => system.running);
            }
            if (hands != null && hands.running)
            {
                if (!rightController.isValid) voiceKey |= Pinched(hands.rightHand);
                if (!leftController.isValid) shotKey |= Pinched(hands.leftHand);
            }
        }
        if (voiceKey && !rightTriggerDown) ToggleVoice();
        if (shotKey && !leftScreenshotTriggerDown) TakeScreenshotAndSend();
        if (chatKey && !leftTriggerDown && chatInput)
        {
            chatInput.gameObject.SetActive(!chatInput.gameObject.activeSelf);
            if (chatInput.gameObject.activeSelf) chatInput.ActivateInputField();
        }
        rightTriggerDown = voiceKey;
        leftTriggerDown = chatKey;
        leftScreenshotTriggerDown = shotKey;
    }
    public GameObject debugPanel;
    private bool leftYHeld;
    public bool rightTriggerVoice = false;
    XRHandSubsystem hands;
    readonly System.Collections.Generic.List<XRHandSubsystem> handSystems = new System.Collections.Generic.List<XRHandSubsystem>();
    readonly System.Collections.Generic.List<InputDevice> controllerDevices = new System.Collections.Generic.List<InputDevice>();
    void ReadController(XRNode node, InputDeviceCharacteristics side, ref InputDevice device)
    {
        if (device.isValid) return;
        InputDevices.GetDevicesWithCharacteristics(side | InputDeviceCharacteristics.Controller, controllerDevices);
        device = controllerDevices.Count > 0 ? controllerDevices[0] : InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid && (device.characteristics & InputDeviceCharacteristics.Controller) == 0) device = default;
        if (device.isValid) Debug.Log($"[VRMInput] {side} {device.name}");
    }
    static bool Button(InputDevice device, InputFeatureUsage<bool> usage) => device.isValid && device.TryGetFeatureValue(usage,out bool value) && value;
    static bool Trigger(InputDevice device) => Button(device,CommonUsages.triggerButton) || (device.isValid && device.TryGetFeatureValue(CommonUsages.trigger,out float value) && value>.75f);
    static bool Pinched(XRHand hand) => hand.isTracked && hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose index) && hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumb) && Vector3.Distance(index.position,thumb.position)<.025f;

    // ===== 语音录制 =====
    void ToggleVoice()
    {
        if (isRecording)
            StopVoiceAndSend();
        else
            StartVoice();
    }

    void StartVoice()
    {
        GetComponent<VRMPresence>()?.Interrupt();
        if (WebSocketClient.Instance == null || Microphone.devices.Length == 0)
        {
            UpdateStatus("未检测到麦克风");
            return;
        }
        WebSocketClient.Instance.InvalidateTurn();
        isRecording = true;
        recordedClip = Microphone.Start(null, false, MaxRecordingSeconds, 48000);
        if (recordedClip == null)
        {
            isRecording = false;
            UpdateStatus("麦克风启动失败");
            return;
        }
        recordingStartSample = Microphone.GetPosition(null);
        Debug.Log("[Voice] === 开始录音 ===");
        UpdateStatus("录音中...");
    }

    void StopVoiceAndSend()
    {
        isRecording = false;
        int endSample = Microphone.GetPosition(null);
        if (endSample < 0) endSample = 0;
        Microphone.End(null);
        UpdateStatus("发送中...");

        if (recordedClip == null)
        {
            Debug.LogWarning("[Voice] 录音为空");
            return;
        }

        // 转WAV
        int capturedSamples = endSample - recordingStartSample;
        if (capturedSamples < 0) capturedSamples += recordedClip.samples;
        capturedSamples = Mathf.Clamp(capturedSamples, 0, recordedClip.samples);
        if (capturedSamples == 0)
        {
            UpdateStatus("录音太短");
            Destroy(recordedClip);
            recordedClip = null;
            return;
        }
        byte[] wavData = AudioClipToWav(recordedClip, capturedSamples, recordingStartSample);
        if (wavData != null && wavData.Length > 0)
        {
            WebSocketClient.Instance.SendVoiceInput(wavData, recordedClip.frequency);
            Debug.Log($"[Voice] 录音结束: {wavData.Length} bytes");
        }

        if (recordedClip != null) Destroy(recordedClip);
        recordedClip = null;
        UpdateStatus("已连接");
    }

    // AudioClip转WAV
    byte[] AudioClipToWav(AudioClip clip, int samples, int startSample)
    {
        if (clip == null || samples <= 0 || startSample < 0 || startSample >= clip.samples) return null;

        int channels = clip.channels;
        int frequency = clip.frequency;
        int total = samples * channels;
        float[] floatData = new float[total];
        int firstCount = Mathf.Min(samples, clip.samples - startSample) * channels;
        float[] first = new float[firstCount];
        clip.GetData(first, startSample);
        Array.Copy(first, 0, floatData, 0, first.Length);
        int remaining = total - first.Length;
        if (remaining > 0)
        {
            float[] tail = new float[remaining];
            clip.GetData(tail, 0);
            Array.Copy(tail, 0, floatData, first.Length, tail.Length);
        }

        // 转16bit PCM
        short[] intData = new short[floatData.Length];
        for (int i = 0; i < floatData.Length; i++)
        {
            float clamped = Mathf.Clamp(floatData[i], -1f, 1f);
            intData[i] = (short)(clamped * 32767);
        }

        byte[] byteData = new byte[intData.Length * 2];
        Buffer.BlockCopy(intData, 0, byteData, 0, byteData.Length);

        // WAV头
        using (var ms = new MemoryStream())
        {
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + byteData.Length);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)channels);
                bw.Write(frequency);
                bw.Write(frequency * channels * 2);
                bw.Write((short)(channels * 2));
                bw.Write((short)16);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                bw.Write(byteData.Length);
                bw.Write(byteData);
            }
            return ms.ToArray();
        }
    }

    // ===== 截图 =====
    void TakeScreenshotAndSend()
    {
        UpdateStatus("截图中...");

        StartCoroutine(CaptureScreenshot());
    }

    IEnumerator CaptureScreenshot()
    {
        yield return new WaitForEndOfFrame();

        Texture2D tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        tex.Apply();

        // 缩放
        int maxWidth = 640;
        if (tex.width > maxWidth)
        {
            int newHeight = (int)(maxWidth * (float)tex.height / tex.width);
            Texture2D scaled = new Texture2D(maxWidth, newHeight, TextureFormat.RGB24, false);
            // 简单缩放
            for (int y = 0; y < newHeight; y++)
            {
                for (int x = 0; x < maxWidth; x++)
                {
                    float u = (float)x / maxWidth;
                    float v = (float)y / newHeight;
                    scaled.SetPixel(x, y, tex.GetPixelBilinear(u, v));
                }
            }
            scaled.Apply();
            Destroy(tex);
            tex = scaled;
        }

        byte[] pngBytes = tex.EncodeToPNG();
        Destroy(tex);

        string b64 = Convert.ToBase64String(pngBytes);
        WebSocketClient.Instance.SendScreenshot(b64);
        Debug.Log($"[Screenshot] 发送: {pngBytes.Length} bytes");

        UpdateStatus("已连接");
    }

    // ===== WebSocket回调 =====
    void OnWSConnected()
    {
        UpdateStatus("已连接");
        AppendChatLog("[系统] 已连接服务器");
    }

    void OnWSDisconnected()
    {
        UpdateStatus("断开，重连中...");
        AppendChatLog("[系统] 断开连接");
    }

    void OnServerError(string message)
    {
        GetComponent<VRMPresence>()?.Interrupt();
        GetComponent<VRMPresence>()?.Record("error",message);
    }

    void OnTextResponse(string text)
    {
        GetComponent<VRMPresence>()?.Resume();
        if (!string.IsNullOrEmpty(text))
        {
            AppendChatLog($"VRM: {text}");
            // TODO: 驱动VRM口型同步
        }
    }

    void OnAudioReceived(byte[] audioData)
    {
        // TODO: 播放音频 + 口型同步
        Debug.Log($"[Audio] 收到音频: {audioData.Length} bytes");
        StartCoroutine(PlayWavAudio(audioData));
    }

    void OnFrameReceived(string bonesJson, float jaw, string faceJson)
    {
        if(GetComponent<VRMPresence>()?.Interrupted==true)return;
        if (motionPlayer != null)
        {
            motionPlayer.ApplyLiveFrame(bonesJson);
            motionPlayer.ApplyFaceFrame(jaw, faceJson);
        }
    }

    void OnIdleReceived(string idleJson)
    {
        if(GetComponent<VRMPresence>()?.Interrupted==true)return;
        Debug.Log($"[Idle] 收到idle动作: {idleJson?.Length ?? 0}chars");
        if (motionPlayer != null)
        {
            motionPlayer.SetIdleFrames(idleJson);
            motionPlayer.PlayIdleLoop(idleJson);
        }
    }

    void OnMotionResponse(string emotion, string motionJson, string durationStr, string motionMode)
    {
        GetComponent<VRMPresence>()?.Resume();
        GetComponent<VRMPresence>()?.SetEmotion(emotion);
        float duration = float.TryParse(durationStr, out var d) ? d : 3f;
        Debug.Log($"[Motion] emotion={emotion}, mode={motionMode}, frames={motionJson?.Length ?? 0}chars, player={(motionPlayer != null ? "bound" : "NULL")}");
        if (string.IsNullOrWhiteSpace(motionJson)) Debug.LogWarning("[Motion] Server returned empty motion; only locomotion can move the root.");
        if (motionPlayer != null)
        {
            if (motionMode == "hast_quaternion_v1")
                motionPlayer.PlayHastClip(emotion, motionJson, duration);
            else if (motionMode == "source_quaternion_v1")
                motionPlayer.PlayQuaternionSkill(emotion, motionJson, duration);
            else if (motionMode == "absolute")
                motionPlayer.PlaySkill(emotion, motionJson, duration);
            else
                motionPlayer.PlayMotion(emotion, motionJson, duration);
        }
    }

    IEnumerator PlayWavAudio(byte[] wavData)
    {
        GetComponent<VRMPresence>()?.Resume();
        // 解析WAV创建AudioClip
        AudioClip clip = WavToAudioClip(wavData);
        if (clip != null)
        {
            AudioSource source = gameObject.GetComponent<AudioSource>();
            if (source == null)
                source = gameObject.AddComponent<AudioSource>();
            // 释放上一个clip避免内存泄漏
            if (source.clip != null) Destroy(source.clip);
            source.clip = clip;
            GetComponent<VRMPresence>().voice=source;
            source.Play();
            yield return new WaitForSeconds(clip.length);
            if(source.clip==clip){source.clip=null;Destroy(clip);}
        }
    }

    AudioClip WavToAudioClip(byte[] wavData)
    {
        try
        {
            if (wavData == null || wavData.Length < 44 || wavData[0] != 'R' || wavData[1] != 'I' || wavData[2] != 'F' || wavData[3] != 'F')
            {
                Debug.LogWarning("[Audio] 不是合法WAV");
                return null;
            }

            int channels = 1;
            int frequency = 44100;
            int bitsPerSample = 16;
            int dataOffset = -1;
            int dataSize = 0;

            // 遍历chunk找 fmt 和 data
            int pos = 12;
            while (pos + 8 <= wavData.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(wavData, pos, 4);
                int chunkSize = BitConverter.ToInt32(wavData, pos + 4);

                if (chunkId == "fmt ")
                {
                    channels = BitConverter.ToInt16(wavData, pos + 10);
                    frequency = BitConverter.ToInt32(wavData, pos + 12);
                    bitsPerSample = BitConverter.ToInt16(wavData, pos + 22);
                }
                else if (chunkId == "data")
                {
                    dataOffset = pos + 8;
                    dataSize = chunkSize;
                    break;
                }
                pos += 8 + chunkSize;
                if (chunkSize % 2 == 1) pos++; // chunk对齐
            }

            if (dataOffset < 0 || dataSize <= 0)
            {
                Debug.LogWarning("[Audio] 找不到data chunk");
                return null;
            }

            int bytesPerSample = bitsPerSample / 8;
            int sampleCount = dataSize / bytesPerSample;
            int frameCount = sampleCount / channels;

            float[] floatData = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = dataOffset + i * bytesPerSample;
                if (idx + bytesPerSample > wavData.Length) break;

                if (bitsPerSample == 16)
                {
                    short sample = BitConverter.ToInt16(wavData, idx);
                    floatData[i] = sample / 32768f;
                }
                else if (bitsPerSample == 32)
                {
                    float sample = BitConverter.ToSingle(wavData, idx);
                    floatData[i] = sample;
                }
                else if (bitsPerSample == 8)
                {
                    floatData[i] = (wavData[idx] - 128) / 128f;
                }
            }

            Debug.Log($"[Audio] WAV: {channels}ch, {frequency}Hz, {bitsPerSample}bit, {frameCount}帧");

            AudioClip clip = AudioClip.Create("TTS", frameCount, channels, frequency, false);
            clip.SetData(floatData, 0);
            return clip;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Audio] WAV parse error: {e.Message}");
            return null;
        }
    }

    public void OnChatInputSelected()
    {
        Debug.Log("[QuestKeyboard] ChatInput selected; requesting keyboard.");
        if (chatInput == null || !chatInput.interactable || chatInput.readOnly) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (chatKeyboard != null && chatKeyboard.active) return;
        // Reuse TMP's keyboard if it already created one; do not open two competing keyboards.
        chatKeyboard = chatInput.touchScreenKeyboard;
        if (chatKeyboard == null || !chatKeyboard.active)
            chatKeyboard = TouchScreenKeyboard.Open(chatInput.text, TouchScreenKeyboardType.Default, false, false, false, false, "输入消息");
        chatKeyboardWasVisible = false;
        Debug.Log($"[QuestKeyboard] supported={TouchScreenKeyboard.isSupported}, handle={chatKeyboard != null}");
#endif
    }

    void SyncChatKeyboard()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (chatKeyboard == null || chatInput == null) return;
        if (chatKeyboard.status == TouchScreenKeyboard.Status.Visible || chatKeyboard.status == TouchScreenKeyboard.Status.Done)
        {
            if (chatInput.text != chatKeyboard.text) chatInput.text = chatKeyboard.text;
        }
        bool visible = TouchScreenKeyboard.visible;
        if (visible != chatKeyboardWasVisible)
        {
            chatKeyboardWasVisible = visible;
            Debug.Log($"[QuestKeyboard] visible={visible}, status={chatKeyboard.status}");
        }
        if (chatKeyboard.status == TouchScreenKeyboard.Status.Done || chatKeyboard.status == TouchScreenKeyboard.Status.Canceled || chatKeyboard.status == TouchScreenKeyboard.Status.LostFocus)
            chatKeyboard = null;
#endif
    }

    // ===== UI =====
    public void OnSendButton()
    {
        if (chatInput != null && !string.IsNullOrEmpty(chatInput.text))
        {
            string text = chatInput.text;
            AppendChatLog($"我: {text}");
            GetComponent<VRMPresence>()?.Interrupt();

            WebSocketClient.Instance.SendTextInput(text);
            if (chatKeyboard != null) { chatKeyboard.active = false; chatKeyboard = null; }
            chatInput.text = "";
            chatInput.DeactivateInputField();
        }
    }

    void UpdateStatus(string text)
    {
        if (statusText != null)
            statusText.text = text;
    }

    void AppendChatLog(string text)
    {
        chatLogText += text + "\n";
        if (chatLog != null)
            chatLog.text = chatLogText;
        Debug.Log($"[Chat] {text}");

        if (chatLogClearCoroutine != null)
            StopCoroutine(chatLogClearCoroutine);
        chatLogClearCoroutine = StartCoroutine(ClearChatLogAfterDelay());
    }

    private Coroutine chatLogClearCoroutine;

    [Header("ChatLog")]
    public float chatLogDisplayDuration = 8f;

    IEnumerator ClearChatLogAfterDelay()
    {
        yield return new WaitForSeconds(chatLogDisplayDuration);
        chatLogText = "";
        if (chatLog != null)
            chatLog.text = "";
        chatLogClearCoroutine = null;
    }

    void OnDestroy()
    {
        if (WebSocketClient.Instance != null)
        {
            WebSocketClient.Instance.OnConnected -= OnWSConnected;
            WebSocketClient.Instance.OnDisconnected -= OnWSDisconnected;
            WebSocketClient.Instance.OnTextResponse -= OnTextResponse;
            WebSocketClient.Instance.OnError -= OnServerError;
            WebSocketClient.Instance.OnAudioReceived -= OnAudioReceived;
            WebSocketClient.Instance.OnMotionResponse -= OnMotionResponse;
            WebSocketClient.Instance.OnIdleReceived -= OnIdleReceived;
            WebSocketClient.Instance.OnFrameReceived -= OnFrameReceived;
        }
    }
}
