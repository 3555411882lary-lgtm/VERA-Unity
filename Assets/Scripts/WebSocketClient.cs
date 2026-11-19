using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// WebSocket客户端 - 负责与AstrBot/LLM服务器通信
/// 自动重连、发送文本/语音/截图、接收响应
/// </summary>
public class WebSocketClient : MonoBehaviour
{
    public static WebSocketClient Instance { get; private set; }

    [Header("服务器设置")]
    public string serverUrl => VRMConnectionMenu.Endpoint;

    // 事件
    public event Action<string> OnLocomotion;
    public event Action OnConnected;
    public event Action OnDisconnected;
    public event Action<string> OnTextResponse;   // 文字回复
    public event Action<byte[]> OnAudioReceived;   // 音频回复
    public event Action<string, string, string, string> OnMotionResponse; // 情绪, 动作队列JSON, 时长, 模式(absolute/relative)
    public event Action<string> OnIdleReceived; // idle动作JSON
    public event Action<string, float, string> OnFrameReceived; // bonesJson, jaw, faceArrayJson
    public event Action<string> OnError;

    [Serializable] public class MoveCommand { public float forward, right, turn; public bool stop; }
    [Serializable] class MoveEnvelope { public MoveCommand locomotion; }

    private ClientWebSocket ws;
    private CancellationTokenSource cts;
    private bool isConnecting = false;
    private float reconnectTimer = 0f;
    private const float ReconnectInterval = 3f;

    // 线程安全的发送队列
    private ConcurrentQueue<string> sendQueue = new ConcurrentQueue<string>();

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        Connect();
    }

    public async void Connect()
    {
        if (isConnecting || string.IsNullOrEmpty(serverUrl)) return;
        isConnecting = true;

        try { cts?.Cancel(); } catch { }
        cts = new CancellationTokenSource();
        try { ws?.Dispose(); } catch { }
        ws = new ClientWebSocket();

        Debug.Log($"[WS] Connecting to {serverUrl}...");

        try
        {
            await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
            Debug.Log("[WS] Connected!");
            isConnecting = false;
            OnConnected?.Invoke();

            _ = ReceiveLoop();
            _ = ProcessSendQueue();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WS] Connect failed: {e.Message}");
            isConnecting = false;
            reconnectTimer = ReconnectInterval;
        }
    }

    public void Disconnect()
    {
        try
        {
            cts?.Cancel();
            if (ws != null && ws.State == WebSocketState.Open)
                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        isConnecting = false;
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1024 * 1024]; // 1MB per receive
        var sb = new StringBuilder();

        while (ws != null && ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
        {
            try
            {
                WebSocketReceiveResult result;
                sb.Clear();

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.Log("[WS] Server closed connection");
                        OnDisconnected?.Invoke();
                        reconnectTimer = ReconnectInterval;
                        return;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string text = sb.ToString();
                HandleMessage(text);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (!cts.IsCancellationRequested)
                {
                    Debug.LogWarning($"[WS] Receive error: {e.Message}");
                    OnDisconnected?.Invoke();
                    reconnectTimer = ReconnectInterval;
                }
                return;
            }
        }
    }

    private async Task ProcessSendQueue()
    {
        while (ws != null && ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
        {
            while (sendQueue.TryDequeue(out string msg))
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                    Debug.Log($"[WS] Sent: {(msg.Length > 200 ? msg.Substring(0, 200) : msg)}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[WS] Send error: {e.Message}");
                }
            }
            await Task.Delay(16); // ~60fps
        }
    }

    private void HandleMessage(string text)
    {
        Debug.Log($"[WS] Received: {(text.Length > 300 ? text.Substring(0, 300) : text)}");

        try
        {
            // 简单解析JSON
            var data = SimpleJson.Parse(text);
            string type = data.GetString("type");

            // 连续动作帧（独立消息类型）
            if (type == "frame")
            {
                string bonesJson = data.GetRawArray("bones");
                if (bonesJson != null)
                {
                    float jaw = 0f;
                    string jawStr = data.GetString("jaw");
                    if (jawStr != null) float.TryParse(jawStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out jaw);
                    string faceJson = data.GetRawArray("face");
                    OnFrameReceived?.Invoke(bonesJson, jaw, faceJson);
                }
                return;
            }
            if (type == "idle")
            {
                string idleJson = data.GetRawArray("motion");
                if (idleJson != null) OnIdleReceived?.Invoke(idleJson);
                return;
            }

            switch (type)
            {
                case "response":
                    if (!string.IsNullOrEmpty(turnId) && data.GetString("turn_id")!=turnId) return;
                    var moveEnvelope = JsonUtility.FromJson<MoveEnvelope>(text);
                    if (moveEnvelope != null && moveEnvelope.locomotion != null && text.Contains("\"locomotion\""))
                        OnLocomotion?.Invoke(JsonUtility.ToJson(moveEnvelope.locomotion));
                    string responseText = data.GetString("text");
                    if (string.IsNullOrEmpty(responseText))
                        responseText = data.GetString("message");

                    string emotion = data.GetString("emotion") ?? "neutral";
                    string motionJson = data.GetRawArray("motion");
                    string durationStr = data.GetString("duration");
                    string motionMode = data.GetString("motion_mode") ?? "relative";

                    if (motionJson != null)
                        OnMotionResponse?.Invoke(emotion, motionJson, durationStr, motionMode);

                    // 解析音频（response里可能带audio）
                    string respAudioB64 = data.GetString("audio");
                    if (!string.IsNullOrEmpty(respAudioB64))
                    {
                        try
                        {
                            byte[] respAudioBytes = Convert.FromBase64String(respAudioB64);
                            OnAudioReceived?.Invoke(respAudioBytes);
                        }
                        catch (Exception ae)
                        {
                            Debug.LogWarning($"[WS] audio decode error: {ae.Message}");
                        }
                    }

                    OnTextResponse?.Invoke(responseText);
                    break;

                case "audio":
                    string audioB64 = data.GetString("audio");
                    if (!string.IsNullOrEmpty(audioB64))
                    {
                        byte[] audioBytes = Convert.FromBase64String(audioB64);
                        OnAudioReceived?.Invoke(audioBytes);
                    }
                    // 也可能有文字
                    string audioText = data.GetString("text");
                    if (!string.IsNullOrEmpty(audioText))
                        OnTextResponse?.Invoke(audioText);
                    break;

                case "tts":
                    string ttsB64 = data.GetString("audio");
                    if (!string.IsNullOrEmpty(ttsB64))
                    {
                        byte[] ttsBytes = Convert.FromBase64String(ttsB64);
                        OnAudioReceived?.Invoke(ttsBytes);
                    }
                    string ttsText = data.GetString("text");
                    if (!string.IsNullOrEmpty(ttsText))
                        OnTextResponse?.Invoke(ttsText);
                    break;

                case "error":
                    string errMsg = data.GetString("message");
                    Debug.LogWarning($"[WS] Server error: {errMsg}");
                    OnError?.Invoke(errMsg);
                    break;

                default:
                    Debug.Log($"[WS] Unknown type: {type}");
                    // 尝试提取文字
                    string anyText = data.GetString("text");
                    if (!string.IsNullOrEmpty(anyText))
                        OnTextResponse?.Invoke(anyText);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WS] Parse error: {e.Message}");
        }
    }

    // ===== 发送方法 =====

    public string PresenceId {
        get {
            string key=PlayerPrefs.GetString("vrm_presence_id", "");
            if (key.Length==32) return key;
            key=Guid.NewGuid().ToString("N"); PlayerPrefs.SetString("vrm_presence_id",key); PlayerPrefs.Save(); return key;
        }
    }
    string turnId="";
    public void InvalidateTurn(){turnId=Guid.NewGuid().ToString("N");}
    public void SendTextInput(string text)
    {
        var msg = SimpleJson.Build();
        msg.Add("type", "input");
        msg.Add("text", text);
        InvalidateTurn(); msg.Add("turn_id",turnId); msg.Add("presence_id",PresenceId);
        msg.Add("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        EnqueueSend(msg.ToString());
    }

    public void SendVoiceInput(byte[] audioData, int sampleRate = 16000)
    {
        var msg = SimpleJson.Build();
        msg.Add("type", "voice_input");
        msg.Add("audio", Convert.ToBase64String(audioData));
        msg.Add("sample_rate", sampleRate);
        InvalidateTurn(); msg.Add("turn_id",turnId); msg.Add("presence_id",PresenceId);
        msg.Add("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        EnqueueSend(msg.ToString());
    }

    public void SendScreenshot(string imageBase64, string text = "", string audioBase64 = "")
    {
        var msg = SimpleJson.Build();
        msg.Add("type", "screenshot");
        msg.Add("image", imageBase64);
        msg.Add("image_format", "png");
        msg.Add("text", text);
        InvalidateTurn(); msg.Add("turn_id",turnId); msg.Add("presence_id",PresenceId);
        msg.Add("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (!string.IsNullOrEmpty(audioBase64))
        {
            msg.Add("audio", audioBase64);
            msg.Add("sample_rate", 48000);
        }
        EnqueueSend(msg.ToString());
    }

    private void EnqueueSend(string json)
    {
        sendQueue.Enqueue(json);
    }

    void Update()
    {
        if (reconnectTimer > 0)
        {
            reconnectTimer -= Time.deltaTime;
            if (reconnectTimer <= 0)
            {
                if (isConnecting)
                {
                    // 仍在连接中，1秒后重试
                    reconnectTimer = 1f;
                }
                else
                {
                    Connect();
                }
            }
        }
    }

    void OnDestroy()
    {
        Disconnect();
    }
}

/// <summary>
/// 轻量JSON构建器 - 不依赖Newtonsoft
/// </summary>
public class SimpleJson
{
    private System.Text.StringBuilder sb = new System.Text.StringBuilder();

    public static SimpleJson Build()
    {
        var j = new SimpleJson();
        j.sb.Append("{");
        return j;
    }

    public SimpleJson Add(string key, string value)
    {
        if (sb.Length > 1) sb.Append(",");
        sb.Append("\"").Append(key).Append("\":");
        sb.Append("\"").Append(Escape(value)).Append("\"");
        return this;
    }

    public SimpleJson Add(string key, long value)
    {
        if (sb.Length > 1) sb.Append(",");
        sb.Append("\"").Append(key).Append("\":").Append(value);
        return this;
    }

    public SimpleJson Add(string key, int value)
    {
        if (sb.Length > 1) sb.Append(",");
        sb.Append("\"").Append(key).Append("\":").Append(value);
        return this;
    }

    public SimpleJson Add(string key, bool value)
    {
        if (sb.Length > 1) sb.Append(",");
        sb.Append("\"").Append(key).Append("\":").Append(value ? "true" : "false");
        return this;
    }

    public override string ToString()
    {
        sb.Append("}");
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    // ===== 解析 =====
    public static JsonData Parse(string json)
    {
        return new JsonData(json);
    }
}

/// <summary>
/// 轻量JSON解析器 - 从字符串中提取值
/// </summary>
public class JsonData
{
    private string json;

    public JsonData(string json)
    {
        this.json = json;
    }

    public string GetString(string key)
    {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) return null;

        idx = json.IndexOf(":", idx + pattern.Length);
        if (idx < 0) return null;

        // 跳过空白
        idx++;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t')) idx++;

        if (idx >= json.Length) return null;

        if (json[idx] == '"')
        {
            // 字符串值
            int start = idx + 1;
            int end = start;
            while (end < json.Length)
            {
                if (json[end] == '\\' && end + 1 < json.Length) { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            if (end >= json.Length) return null;
            return json.Substring(start, end - start).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
        }
        else
        {
            // 非字符串值
            int start = idx;
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
            return json.Substring(start, end - start).Trim();
        }
    }

    public long GetLong(string key)
    {
        string s = GetString(key);
        if (long.TryParse(s, out long v)) return v;
        return 0;
    }

    public int GetInt(string key)
    {
        string s = GetString(key);
        if (int.TryParse(s, out int v)) return v;
        return 0;
    }

    /// <summary>提取完整的数组/嵌套对象原始JSON（括号配对）</summary>
    public string GetRawArray(string key)
    {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) return null;
        idx = json.IndexOf(':', idx + pattern.Length);
        if (idx < 0) return null;
        idx++;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '	')) idx++;
        if (idx >= json.Length) return null;

        char open = json[idx];
        if (open != '[' && open != '{') return null;
        char close = open == '[' ? ']' : '}';

        int depth = 0;
        bool inString = false;
        for (int i = idx; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                    return json.Substring(idx, i - idx + 1);
            }
        }
        return null;
    }
}
