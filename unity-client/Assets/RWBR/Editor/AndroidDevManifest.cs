using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

namespace RWBR.EditorTools
{
    /// <summary>
    /// Development APKs talk to a platform API on the developer's PC over plain HTTP (http://&lt;PC LAN IP&gt;:3001).
    /// Unity's "Allow downloads over HTTP" setting only relaxes UnityWebRequest; Android 9+ also blocks cleartext
    /// traffic at the OS level unless the manifest allows it. This adds android:usesCleartextTraffic="true" to the
    /// generated manifest for development builds only, so release builds stay HTTPS-only.
    /// </summary>
    public sealed class AndroidDevManifest : IPostGenerateGradleAndroidProject
    {
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 0;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            if (!ProjectSetup.BuildingDevelopment) return;
            string manifest = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
            var doc = new XmlDocument();
            doc.Load(manifest);
            var application = doc.SelectSingleNode("/manifest/application") as XmlElement;
            if (application == null)
            {
                application = doc.CreateElement("application");
                doc.DocumentElement.AppendChild(application);
            }
            application.SetAttribute("usesCleartextTraffic", AndroidNs, "true");
            doc.Save(manifest);
            Debug.Log("RWBR: development build allows cleartext HTTP (usesCleartextTraffic)");
        }
    }
}
