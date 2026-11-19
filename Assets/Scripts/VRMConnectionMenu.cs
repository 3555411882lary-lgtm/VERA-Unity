using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class VRMConnectionMenu : MonoBehaviour
{
    const string EndpointKey = "vrm.connection.endpoint";
    const string CredentialKey = "vrm.connection.credential";
    static string editorToken;
    public static string Endpoint => PlayerPrefs.GetString(EndpointKey, "");
    public static string PresenceUrl {
        get {
            if (!Uri.TryCreate(Endpoint,UriKind.Absolute,out var ws)) return "";
            var url=new UriBuilder(ws){ Scheme=ws.Scheme=="wss"?"https":"http", Port=ws.Port };
            url.Path=ws.AbsolutePath.Substring(0,ws.AbsolutePath.LastIndexOf('/')+1)+"presence";
            return url.Uri.AbsoluteUri.TrimEnd('/');
        }
    }
    static string Crypt(string method,string value) {
#if UNITY_ANDROID && !UNITY_EDITOR
        using(var api=new AndroidJavaClass("com.vrm.settings.VRMDeviceSecret")) return api.CallStatic<string>(method,value);
#else
        return value;
#endif
    }
    public static string Token {
        get {
#if UNITY_ANDROID && !UNITY_EDITOR
            string stored=PlayerPrefs.GetString(CredentialKey, "");
            try { return stored.Length==0?"":Crypt("open",stored); }
            catch(Exception){ Debug.LogWarning("[Settings] Re-enter device token"); return ""; }
#else
            return editorToken??"";
#endif
        }
    }
    TMP_Text caption;
    bool editing;
    void Start() {
        var controller=GetComponent<VRMController>();
        var canvas=controller.chatInput ? controller.chatInput.GetComponentInParent<Canvas>(true) : null;
        if(!canvas && controller.statusText) canvas=controller.statusText.GetComponentInParent<Canvas>(true);
        if(!canvas){ Debug.LogError("[Settings] Assign a UI canvas through chatInput or statusText"); return; }
        var button=new GameObject("Connection Settings",typeof(RectTransform),typeof(Image),typeof(Button));
        var rect=(RectTransform)button.transform;
        rect.SetParent(canvas.transform,false); rect.anchorMin=rect.anchorMax=rect.pivot=Vector2.one;
        rect.anchoredPosition=new Vector2(-8,-8); rect.sizeDelta=new Vector2(180,46);
        button.GetComponent<Image>().color=new Color(.15f,.17f,.19f,1);
        var label=new GameObject("Label",typeof(RectTransform),typeof(TextMeshProUGUI));
        var textRect=(RectTransform)label.transform; textRect.SetParent(rect,false);
        textRect.anchorMin=Vector2.zero; textRect.anchorMax=Vector2.one; textRect.offsetMin=textRect.offsetMax=Vector2.zero;
        caption=label.GetComponent<TextMeshProUGUI>(); caption.raycastTarget=false; caption.fontSize=20; caption.alignment=TextAlignmentOptions.Center;
        caption.text="Settings";
        button.GetComponent<Button>().onClick.AddListener(()=>{if(!editing)StartCoroutine(Edit());});
    }
    IEnumerator Edit() {
        editing=true;
        try {
            caption.text="Server URL";
            var keyboard=TouchScreenKeyboard.Open(Endpoint,TouchScreenKeyboardType.URL,false,false,false,false,"Middleware WebSocket URL");
            if(keyboard==null) yield break;
            while(keyboard.status==TouchScreenKeyboard.Status.Visible) yield return null;
            if(keyboard.status!=TouchScreenKeyboard.Status.Done) yield break;
            if(!Uri.TryCreate(keyboard.text.Trim(),UriKind.Absolute,out var ws) || (ws.Scheme!="ws" && ws.Scheme!="wss") || ws.UserInfo!="" || ws.Query!="" || ws.Fragment!="" || ws.Host=="") {
                caption.text="Invalid URL"; yield return new WaitForSecondsRealtime(2); yield break;
            }
            if(ws.AbsolutePath=="/") ws=new Uri(ws,"/ws");
            yield return null;
            caption.text="Device token";
            keyboard=TouchScreenKeyboard.Open("",TouchScreenKeyboardType.Default,false,false,true,false,"VRM_TOKEN (blank to clear)");
            if(keyboard==null) yield break;
            while(keyboard.status==TouchScreenKeyboard.Status.Visible) yield return null;
            if(keyboard.status!=TouchScreenKeyboard.Status.Done) yield break;
            string token=keyboard.text.Trim();
            try {
#if UNITY_ANDROID && !UNITY_EDITOR
                string sealedToken=token.Length==0?"":Crypt("seal",token);
                PlayerPrefs.SetString(CredentialKey,sealedToken);
#else
                editorToken=token;
#endif
                PlayerPrefs.SetString(EndpointKey,ws.AbsoluteUri); PlayerPrefs.Save(); caption.text="Saved. Restart app";
            } catch(Exception){caption.text="Save failed";}
            yield return new WaitForSecondsRealtime(4);
        } finally { editing=false; caption.text="Settings"; }
    }
}
