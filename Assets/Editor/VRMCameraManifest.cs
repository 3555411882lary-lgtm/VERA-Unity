#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
public sealed class VRMCameraManifest : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 2000;
    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifest = Path.Combine(path,"src/main/AndroidManifest.xml");
        var doc = new XmlDocument(); doc.Load(manifest);
        const string ns="http://schemas.android.com/apk/res/android";
        const string permission="horizonos.permission.HEADSET_CAMERA";
        var root = doc.DocumentElement;
        const string tools = "http://schemas.android.com/tools";
        foreach (XmlElement meta in root.SelectNodes("application/meta-data"))
        {
            if (meta.GetAttribute("name", ns) != "com.oculus.ossplash.background") continue;
            root.SetAttribute("xmlns:tools", tools);
            var replace = meta.GetAttribute("replace", tools);
            if (!System.Array.Exists(replace.Split(','), item => item.Trim() == "android:value"))
                meta.SetAttribute("replace", tools, string.IsNullOrEmpty(replace) ? "android:value" : replace + ",android:value");
        }
        foreach(XmlElement e in doc.GetElementsByTagName("uses-permission"))
            if(e.GetAttribute("name",ns)==permission) { doc.Save(manifest); return; }
        var entry=doc.CreateElement("uses-permission");
        entry.SetAttribute("name",ns,permission);
        doc.DocumentElement.PrependChild(entry); doc.Save(manifest);
    }
}
#endif
