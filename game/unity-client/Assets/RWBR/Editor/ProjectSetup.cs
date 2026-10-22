using System;
using RWBR.Client.Diagnostics;
using RWBR.Client.Greybox;
using RWBR.Client.Menu;
using RWBR.Client.Online;
using RWBR.Client.Player;
using RWBR.Client.Practice;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RWBR.EditorTools
{
    /// <summary>
    /// Regenerates the practice scene and mobile player settings, and builds Android.
    /// Menu: RWBR > ... Batch: see game/unity-client/unity-batch.sh.
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/Scenes/Greybox.unity";
        public const string OnlineScenePath = "Assets/Scenes/Online.unity";
        public const string MenuScenePath = "Assets/Scenes/MainMenu.unity";
        public const string ApplicationId = "com.rwbr.battleroyale";
        public const string AndroidApkPath = "Builds/Android/rwbr-practice.apk";

        [MenuItem("RWBR/Create Practice + Online Scenes")]
        public static void CreateGreyboxScene()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            CreateScene(ScenePath, game => game.AddComponent<PracticeGame>(), game => game.AddComponent<PracticeHud>(), "Practice");
            CreateScene(OnlineScenePath, game => game.AddComponent<OnlineGame>(), game => game.AddComponent<OnlineHud>(), "Online");
            CreateMenuScene();
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MenuScenePath, true), // first scene = what the app opens with
                new EditorBuildSettingsScene(ScenePath, true),
                new EditorBuildSettingsScene(OnlineScenePath, true),
            };
            ConfigurePlayerSettings();
            AssetDatabase.SaveAssets();
            Debug.Log($"RWBR: scenes written to {ScenePath} and {OnlineScenePath}");
        }

        private static void CreateMenuScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(40, -50, 0);
            var world = new GameObject("GreyboxWorld").AddComponent<GreyboxWorld>();
            world.UseMap(RWBR.Shared.CityMap.Version); // the city as the menu backdrop
            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            cameraGo.GetComponent<Camera>().farClipPlane = 800f;
            new GameObject("MainMenu").AddComponent<MainMenu>();
            if (!EditorSceneManager.SaveScene(scene, MenuScenePath))
                throw new InvalidOperationException($"Could not save {MenuScenePath}");
        }

        private static void CreateScene(string path, Func<GameObject, MonoBehaviour> addGame, Action<GameObject> addHud, string name)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);

            new GameObject("GreyboxWorld").AddComponent<GreyboxWorld>(); // builds itself on load

            var gameGo = new GameObject(name);
            MonoBehaviour game = addGame(gameGo);
            addHud(gameGo);

            new GameObject("PerformanceMonitor").AddComponent<PerformanceMonitor>();

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            Camera cam = cameraGo.GetComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 400f;
            cam.fieldOfView = 60f;
            cameraGo.AddComponent<ThirdPersonCamera>().Target = (ICameraTarget)game;

            if (!EditorSceneManager.SaveScene(scene, path))
                throw new InvalidOperationException($"Could not save {path}");
        }

        private static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "RWBR";
            PlayerSettings.productName = "Real-World Battle Royale";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, ApplicationId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.forceInternetPermission = true; // UDP to the match server
        }

        [MenuItem("RWBR/Build Android (development APK)")]
        public static void BuildAndroid()
        {
            ConfigurePlayerSettings();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { MenuScenePath, ScenePath, OnlineScenePath },
                locationPathName = AndroidApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development,
            };
            EditorUserBuildSettings.buildAppBundle = false;
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log($"RWBR: Android build {s.result}: {s.outputPath} {s.totalSize / 1048576f:0.0} MB in {s.totalTime.TotalSeconds:0}s, {s.totalErrors} errors");
            if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
