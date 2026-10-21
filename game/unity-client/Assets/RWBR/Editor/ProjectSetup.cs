using RWBR.Client.Greybox;
using RWBR.Client.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RWBR.EditorTools
{
    /// <summary>
    /// Regenerates the greybox scene and mobile player settings. Menu: RWBR > Create Greybox Scene.
    /// Batch: Unity -batchmode -quit -projectPath game/unity-client -executeMethod RWBR.EditorTools.ProjectSetup.CreateGreyboxScene
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/Scenes/Greybox.unity";

        [MenuItem("RWBR/Create Greybox Scene")]
        public static void CreateGreyboxScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);

            new GameObject("GreyboxWorld").AddComponent<GreyboxWorld>(); // builds itself on load

            // Root at the feet (matches the server's position convention); the capsule is visual only.
            var player = new GameObject("Player");
            player.transform.position = new Vector3(0, 0, -20); // facing the building's doorway
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(player.transform, false);
            body.transform.localPosition = new Vector3(0, 0.9f, 0);
            body.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f);
            GreyboxPlayer controller = player.AddComponent<GreyboxPlayer>();

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            cameraGo.GetComponent<Camera>().nearClipPlane = 0.1f;
            cameraGo.AddComponent<ThirdPersonCamera>().Target = controller;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new System.InvalidOperationException($"Could not save {ScenePath}");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            ConfigureMobilePlayerSettings();
            AssetDatabase.SaveAssets();
            Debug.Log($"RWBR: greybox scene written to {ScenePath}");
        }

        private static void ConfigureMobilePlayerSettings()
        {
            PlayerSettings.companyName = "RWBR";
            PlayerSettings.productName = "Real-World Battle Royale";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
        }
    }
}
