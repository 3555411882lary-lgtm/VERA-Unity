using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Events;
using UnityEngine.XR;
using UniVRM10;

// Runs after MotionPlayer. Adds only small offsets and removes its last offset before Update.
[DefaultExecutionOrder(13000)]
public sealed class VRMPresence : MonoBehaviour
{
    public Animator avatar;
    public MotionPlayer motion;
    public QuestProceduralWalker walker;
    public Transform attentionTarget;
    public Camera viewCamera;
    [Tooltip("Optional transform whose blue axis points out of the avatar's face.")]
    public Transform bodyForward;
    VRMQuestCamera questCamera;
    public AudioSource voice;
    public string serviceUrl => VRMConnectionMenu.PresenceUrl;
    [System.NonSerialized] public string token;
    public string session="vrm";
    public bool observationEnabled=false;
    public UnityEvent<string> onObservation = new UnityEvent<string>();
    [Range(0,1)] public float attentionWeight=.5f;
    [Range(0,1)] public float breathingDegrees=.25f;
    public string LastObservation { get; private set; }
    public bool IsSpeaking => voice && voice.isPlaying;
    public bool Interrupted { get; private set; }
    Vrm10Instance vrm;
    Transform head,chest,leftEye,rightEye;
    Quaternion headOffset=Quaternion.identity,chestOffset=Quaternion.identity,leftOffset=Quaternion.identity,rightOffset=Quaternion.identity;
    Quaternion gaze=Quaternion.identity,eyeGaze=Quaternion.identity;
    float nextGlance,glanceUntil,blinkAt,blinkStart=-10,mouth;
    bool facialOverride;
    float overrideUntil,overrideHappy;
    Vector2 glance;
    float[] samples=new float[256];
    Coroutine capture;
    public bool manualScreenshotEnabled=true;
    public float pinchDistance=.025f;
    bool manualBusy;
    float lastManual=-10f;

    [Serializable] class Picture { public string session,image; }
    [Serializable] class Observation { public string text; }
    [Serializable] class MemoryEvent { public string session,kind,text; }
    float attentionUntil;
    public void Attend(Transform speaker){ attentionTarget=speaker; attentionUntil=Time.unscaledTime+6f; }
    void OnEnable(){StartCoroutine(Initialize());}
    IEnumerator Initialize()
    {
        while(!avatar){if(motion)avatar=motion.animator;else avatar=GetComponentInChildren<Animator>();yield return null;}
        if(!motion)motion=GetComponent<MotionPlayer>();
        if(!walker)walker=GetComponent<QuestProceduralWalker>();
        vrm=avatar.GetComponentInParent<Vrm10Instance>();
        head=avatar.GetBoneTransform(HumanBodyBones.Head);chest=avatar.GetBoneTransform(HumanBodyBones.Chest);
        leftEye=avatar.GetBoneTransform(HumanBodyBones.LeftEye);rightEye=avatar.GetBoneTransform(HumanBodyBones.RightEye);
        blinkAt=Time.unscaledTime+UnityEngine.Random.Range(3f,6f);nextGlance=Time.unscaledTime+6f;
        capture=StartCoroutine(ObserveLoop());
    }
    void Update()
    {
        RemoveOffset(head,ref headOffset);RemoveOffset(chest,ref chestOffset);
        RemoveOffset(leftEye,ref leftOffset);RemoveOffset(rightEye,ref rightOffset);
    }
    public void SetLeftPinch(bool pressed)
    {
        if(pressed&&!externalPinchHeld)RequestManualScreenshot();externalPinchHeld=pressed;
    }
    bool externalPinchHeld;
    public void RequestManualScreenshot()
    {
        if(!isActiveAndEnabled || manualBusy || !viewCamera || Time.unscaledTime-lastManual<1f)return;
        lastManual=Time.unscaledTime;StartCoroutine(ManualScreenshot());
    }
    IEnumerator ManualScreenshot()
    {
        manualBusy=true;yield return new WaitForEndOfFrame();
        try
        {
            var client=WebSocketClient.Instance;
            if(client){client.SendScreenshot(Convert.ToBase64String(Capture(true)),"看看我现在看到的画面");Record("manual_screenshot","Left trigger or pinch screenshot sent.");}
        }
        finally{manualBusy=false;}
    }
    static void RemoveOffset(Transform bone,ref Quaternion offset)
    {if(bone)bone.localRotation=bone.localRotation*Quaternion.Inverse(offset);offset=Quaternion.identity;}
    void LateUpdate()
    {
        if(!avatar)return;
        float now=Time.unscaledTime;
        if(!voice)voice=GetComponent<AudioSource>();
        if(now>=nextGlance){glance=new Vector2(UnityEngine.Random.Range(-6f,6f),UnityEngine.Random.Range(-3f,3f));glanceUntil=now+.5f;nextGlance=now+UnityEngine.Random.Range(5f,10f);}
        bool busy=(motion && motion.ReplyMotionActive) || Interrupted;
        Vector2 desired=Vector2.zero;
        if(attentionTarget && head)
        {
            Vector3 d=avatar.transform.InverseTransformDirection(attentionTarget.position-head.position).normalized;
            desired=new Vector2(Mathf.Clamp(Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg,-35,35),Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(d.y,-1,1))*Mathf.Rad2Deg,-18,18));
        }
        if(now<glanceUntil)desired+=glance;
        float weight=(motion && motion.ReplyMotionActive)?0f:attentionWeight*(now<attentionUntil?1f:.22f);
        eyeGaze=Quaternion.Slerp(eyeGaze,Quaternion.Euler(desired.y*.5f,desired.x*.5f,0),1-Mathf.Exp(-Time.unscaledDeltaTime*14));
        gaze=Quaternion.Slerp(gaze,Quaternion.Euler(desired.y*.5f,desired.x*.5f,0),1-Mathf.Exp(-Time.unscaledDeltaTime*4));
        headOffset=Quaternion.Slerp(Quaternion.identity,gaze,weight);leftOffset=rightOffset=Quaternion.Slerp(Quaternion.identity,eyeGaze,weight);
        if(head)head.localRotation*=headOffset;
        if(leftEye)leftEye.localRotation*=leftOffset;if(rightEye)rightEye.localRotation*=rightOffset;
        bool walking=walker && walker.IsWalking;
        chestOffset=Quaternion.Euler(!busy&&!walking?Mathf.Sin(now*1.5f)*breathingDegrees:0,0,0);
        if(chest)chest.localRotation*=chestOffset;
        if(vrm && vrm.Runtime!=null)
        {
            if(now>=blinkAt){blinkStart=now;blinkAt=now+UnityEngine.Random.Range(3f,6f);}
            float blink=Mathf.Clamp01(1-Mathf.Abs((now-blinkStart)/.085f-1));
            var expression=vrm.Runtime.Expression;
            if(!busy){expression.SetWeight(ExpressionKey.CreateFromPreset(ExpressionPreset.blinkLeft),blink);expression.SetWeight(ExpressionKey.CreateFromPreset(ExpressionPreset.blinkRight),blink);}
            if(facialOverride && now>overrideUntil)facialOverride=false;
            // MotionPlayer owns the emotion presets.
            float level=0;
            if(IsSpeaking){voice.GetOutputData(samples,0);foreach(float v in samples)level+=v*v;level=Mathf.Clamp01(Mathf.Sqrt(level/samples.Length)*8f);}
            mouth=Mathf.Lerp(mouth,level,1-Mathf.Exp(-Time.unscaledDeltaTime*18));
            expression.SetWeight(ExpressionKey.CreateFromPreset(ExpressionPreset.aa),mouth);
        }
    }
    public void SetEmotion(string emotion,float seconds=3f)
    {
        facialOverride=true;overrideUntil=Time.unscaledTime+Mathf.Clamp(seconds,.1f,15f);
        overrideHappy=(emotion=="joy"||emotion=="fun"||emotion=="happy")?.25f:0f;
    }
    public void Interrupt()
    {
        Interrupted=true;
        Attend(viewCamera ? viewCamera.transform : null);
        if(walker)walker.StopWalking();
        mouth=0f;
        if(vrm && vrm.Runtime!=null)vrm.Runtime.Expression.SetWeight(ExpressionKey.CreateFromPreset(ExpressionPreset.aa),0f);
        if(voice)voice.Stop();if(motion)motion.InterruptPresenceMotion();
        Record("interrupted","User interrupted; voice and motion stopped.");
    }
    public void Resume(){Interrupted=false;}
    public void Record(string kind,string text){if(isActiveAndEnabled && !string.IsNullOrEmpty(token))StartCoroutine(PostEvent(kind,text));}
    IEnumerator PostEvent(string kind,string text)
    {
        using(var request=JsonPost("/event",JsonUtility.ToJson(new MemoryEvent{session=session,kind=kind,text=text})))yield return request.SendWebRequest();
    }
    UnityWebRequest JsonPost(string path,string json)
    {
        var r=new UnityWebRequest(serviceUrl.TrimEnd('/')+path,"POST");r.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));r.downloadHandler=new DownloadHandlerBuffer();r.SetRequestHeader("Content-Type","application/json");if(!string.IsNullOrEmpty(token))r.SetRequestHeader("X-VRM-Token",token);r.timeout=50;return r;
    }
    IEnumerator ObserveLoop()
    {
        while(enabled)
        {
            float started=Time.realtimeSinceStartup;
            if(observationEnabled && head && !string.IsNullOrEmpty(token))
            {
                yield return new WaitForEndOfFrame();
                if (!questCamera) questCamera=GetComponent<VRMQuestCamera>() ?? gameObject.AddComponent<VRMQuestCamera>();
                byte[] jpeg=questCamera.CaptureForward((bodyForward ? bodyForward : avatar.transform).forward);
                if (jpeg == null) { yield return new WaitForSecondsRealtime(2f); continue; }
                using(var r=JsonPost("/observe",JsonUtility.ToJson(new Picture{session=session,image=Convert.ToBase64String(jpeg)})))
                {
                    yield return r.SendWebRequest();
                    if(r.result==UnityWebRequest.Result.Success){var result=JsonUtility.FromJson<Observation>(r.downloadHandler.text);LastObservation=result.text;onObservation.Invoke(result.text);}
                    else Debug.LogWarning("[VRMVision] "+r.error+"; configure vision service before enabling observation.",this);
                }
            }
            else if (questCamera) questCamera.Release();
            // A single coroutine never queues pictures while a previous one is processing.
            yield return new WaitForSecondsRealtime(Mathf.Max(.05f,2f-(Time.realtimeSinceStartup-started)));
        }
    }
    byte[] Capture(bool png=false, Camera source=null)
    {
        if (!source) source = viewCamera;
        int width=512,height=Mathf.Max(1,Mathf.RoundToInt(width/Mathf.Max(.1f,source.aspect)));
        RenderTexture target=RenderTexture.GetTemporary(width,height,24);var old=source.targetTexture;var active=RenderTexture.active;
        Texture2D texture=null;
        try{source.targetTexture=target;source.Render();RenderTexture.active=target;texture=new Texture2D(width,height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,width,height),0,0);texture.Apply();return png?texture.EncodeToPNG():texture.EncodeToJPG(65);}
        finally{source.targetTexture=old;RenderTexture.active=active;RenderTexture.ReleaseTemporary(target);if(texture)Destroy(texture);}
    }
    void OnDisable()
    {
        if (questCamera) questCamera.Release();
        StopAllCoroutines();RemoveOffset(head,ref headOffset);RemoveOffset(chest,ref chestOffset);RemoveOffset(leftEye,ref leftOffset);RemoveOffset(rightEye,ref rightOffset);
    }
}
