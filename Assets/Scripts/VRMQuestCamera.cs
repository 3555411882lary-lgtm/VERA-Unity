using Meta.XR;
using UnityEngine;
using UnityEngine.Android;

public sealed class VRMQuestCamera : MonoBehaviour
{
    const string PermissionName = "horizonos.permission.HEADSET_CAMERA";
    PassthroughCameraAccess sensor;
    bool requested;
    string lastStatus;

    void Status(string message)
    {
        if (message == lastStatus) return;
        lastStatus = message;
        Debug.Log("[VRMQuestCamera] " + message, this);
    }

    public byte[] CaptureForward(Vector3 direction)
    {
        if (!Permission.HasUserAuthorizedPermission(PermissionName))
        {
            Status("Waiting for headset camera permission");
            if (!requested) { requested = true; Permission.RequestUserPermission(PermissionName); }
            return null;
        }
        if (!sensor) sensor = gameObject.AddComponent<PassthroughCameraAccess>();
        if (!sensor.IsPlaying) { Status("Waiting for camera stream"); return null; }
        Texture frame = sensor.GetTexture();
        if (!frame || frame.width < 16) return null;
        Quaternion inverse = Quaternion.Inverse(sensor.GetCameraPose().rotation);
        Vector3 target = inverse * direction.normalized;
        if (target.z <= 0f) return Outside();
        Vector2 lo = Slope(inverse, Vector2.zero);
        Vector2 hi = Slope(inverse, Vector2.one);
        Vector2 uv = new Vector2((target.x / target.z - lo.x) / (hi.x - lo.x),
                                 (target.y / target.z - lo.y) / (hi.y - lo.y));
        // Keep the entire crop inside both the sensor rectangle and a 25 degree cone.
        float halfWidth = .24f;
        float aspectScale = (float)frame.width / frame.height * 9f / 16f;
        while (halfWidth >= .08f)
        {
            Vector2 half = new Vector2(halfWidth, halfWidth * aspectScale);
            Vector2 min = uv - half, max = uv + half;
            if (min.x >= 0 && min.y >= 0 && max.x <= 1 && max.y <= 1 &&
                InCone(min, direction) && InCone(max, direction) &&
                InCone(new Vector2(min.x,max.y), direction) && InCone(new Vector2(max.x,min.y), direction))
            {
                Status("Sending physical camera crop");
                return Encode(frame, 2f * half, min);
            }
            halfWidth *= .8f;
        }
        return Outside();
    }
    bool InCone(Vector2 uv, Vector3 direction) => Vector3.Angle(sensor.ViewportPointToRay(uv).direction, direction) <= 25f;
    Vector2 Slope(Quaternion inverse, Vector2 uv)
    {
        Vector3 ray = inverse * sensor.ViewportPointToRay(uv).direction;
        return new Vector2(ray.x / ray.z, ray.y / ray.z);
    }
    byte[] Outside() { Status("VRM direction outside usable camera view; skipped"); return null; }
    static byte[] Encode(Texture frame, Vector2 scale, Vector2 offset)
    {
        var previous = RenderTexture.active;
        var target = RenderTexture.GetTemporary(512, 288, 0);
        Texture2D cpu = null;
        try {
            Graphics.Blit(frame,target,scale,offset);
            RenderTexture.active = target;
            cpu = new Texture2D(512,288,TextureFormat.RGB24,false);
            cpu.ReadPixels(new Rect(0,0,512,288),0,0); cpu.Apply();
            return cpu.EncodeToJPG(65);
        } finally {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (cpu) Destroy(cpu);
        }
    }
    public void Release() { if (sensor) { sensor.enabled=false; Destroy(sensor); sensor=null; } }
    void OnDisable() => Release();
}
