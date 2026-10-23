using RWBR.Client.Practice;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Combat
{
    /// <summary>A built weapon: its root (grip at the origin, barrel along +Z) and the muzzle point.</summary>
    public sealed class WeaponModel
    {
        public GameObject Root;
        public Transform Muzzle;
        /// <summary>Where spent cases leave the weapon.</summary>
        public Transform Ejector;
        /// <summary>Hand targets for IK on rigged characters: trigger hand on the grip, support hand forward.</summary>
        public Transform RightHandGrip;
        public Transform LeftHandGrip;
    }

    /// <summary>
    /// Original, low-poly weapon models built from primitives at runtime, one shape per weapon class. They are real 3D
    /// geometry with correct proportions, grip and muzzle points, and are owned outright; a licensed weapon pack can
    /// replace them later without touching gameplay (only <see cref="Build"/> changes). Dimensions are in metres.
    /// </summary>
    public static class WeaponModels
    {
        private static readonly Color Metal = new Color(0.16f, 0.17f, 0.18f);
        private static readonly Color Polymer = new Color(0.27f, 0.25f, 0.2f);
        private static readonly Color Glass = new Color(0.1f, 0.2f, 0.28f);

        public static WeaponModel Build(WeaponClass weaponClass, Transform parent)
        {
            var root = new GameObject($"Weapon_{weaponClass}");
            root.transform.SetParent(parent, false);
            Transform t = root.transform;
            float muzzle;
            float support; // how far forward the support hand holds the weapon (z)
            switch (weaponClass)
            {
                case WeaponClass.Pistol:
                    Box(t, "Slide", new Vector3(0, 0.05f, 0.06f), new Vector3(0.032f, 0.04f, 0.19f), Metal);
                    Box(t, "Frame", new Vector3(0, 0.02f, 0.05f), new Vector3(0.03f, 0.025f, 0.15f), Polymer);
                    Box(t, "Grip", new Vector3(0, -0.04f, -0.01f), new Vector3(0.03f, 0.11f, 0.045f), Polymer, new Vector3(-15, 0, 0));
                    muzzle = 0.16f;
                    support = 0f; // two-handed grip: the support hand wraps the trigger hand
                    break;
                case WeaponClass.Smg:
                    Box(t, "Receiver", new Vector3(0, 0.05f, 0.06f), new Vector3(0.05f, 0.07f, 0.3f), Metal);
                    Barrel(t, new Vector3(0, 0.055f, 0.21f), 0.012f, 0.08f, Metal);
                    Box(t, "Magazine", new Vector3(0, -0.05f, 0.12f), new Vector3(0.028f, 0.15f, 0.035f), Metal);
                    Box(t, "Grip", new Vector3(0, -0.03f, -0.02f), new Vector3(0.03f, 0.09f, 0.04f), Polymer, new Vector3(-12, 0, 0));
                    Box(t, "Stock", new Vector3(0, 0.04f, -0.17f), new Vector3(0.02f, 0.05f, 0.16f), Metal);
                    muzzle = 0.25f;
                    support = 0.12f;
                    break;
                case WeaponClass.Shotgun:
                    Box(t, "Receiver", new Vector3(0, 0.05f, 0.05f), new Vector3(0.055f, 0.075f, 0.3f), Metal);
                    Barrel(t, new Vector3(0, 0.07f, 0.45f), 0.016f, 0.5f, Metal);
                    Barrel(t, new Vector3(0, 0.035f, 0.38f), 0.014f, 0.36f, Metal); // magazine tube
                    Box(t, "Pump", new Vector3(0, 0.035f, 0.36f), new Vector3(0.05f, 0.05f, 0.16f), Polymer);
                    Box(t, "Grip", new Vector3(0, -0.03f, -0.04f), new Vector3(0.032f, 0.09f, 0.045f), Polymer, new Vector3(-20, 0, 0));
                    Box(t, "Stock", new Vector3(0, 0.02f, -0.25f), new Vector3(0.045f, 0.1f, 0.26f), Polymer, new Vector3(6, 0, 0));
                    muzzle = 0.7f;
                    support = 0.3f; // on the pump
                    break;
                case WeaponClass.Marksman:
                case WeaponClass.Sniper:
                    bool sniper = weaponClass == WeaponClass.Sniper;
                    float barrel = sniper ? 0.62f : 0.42f;
                    Box(t, "Receiver", new Vector3(0, 0.05f, 0.06f), new Vector3(0.055f, 0.075f, 0.42f), Metal);
                    Barrel(t, new Vector3(0, 0.06f, 0.27f + barrel / 2), 0.013f, barrel, Metal);
                    Box(t, "Handguard", new Vector3(0, 0.05f, 0.34f), new Vector3(0.06f, 0.06f, 0.2f), Polymer);
                    Box(t, "Magazine", new Vector3(0, -0.04f, 0.1f), new Vector3(0.03f, 0.1f, 0.07f), Metal);
                    Box(t, "Grip", new Vector3(0, -0.03f, -0.04f), new Vector3(0.032f, 0.09f, 0.045f), Polymer, new Vector3(-18, 0, 0));
                    Box(t, "Stock", new Vector3(0, 0.03f, -0.26f), new Vector3(0.045f, 0.11f, 0.28f), Polymer);
                    Barrel(t, new Vector3(0, 0.125f, 0.08f), sniper ? 0.024f : 0.019f, sniper ? 0.32f : 0.24f, Metal); // scope
                    Barrel(t, new Vector3(0, 0.125f, sniper ? 0.245f : 0.205f), sniper ? 0.026f : 0.021f, 0.01f, Glass);
                    muzzle = 0.27f + barrel;
                    support = 0.24f;
                    break;
                default: // assault rifle
                    Box(t, "Receiver", new Vector3(0, 0.05f, 0.04f), new Vector3(0.055f, 0.08f, 0.4f), Metal);
                    Box(t, "Handguard", new Vector3(0, 0.05f, 0.33f), new Vector3(0.06f, 0.065f, 0.22f), Polymer);
                    Barrel(t, new Vector3(0, 0.055f, 0.52f), 0.012f, 0.2f, Metal);
                    Box(t, "Magazine", new Vector3(0, -0.06f, 0.12f), new Vector3(0.03f, 0.16f, 0.06f), Metal, new Vector3(12, 0, 0));
                    Box(t, "Grip", new Vector3(0, -0.03f, -0.04f), new Vector3(0.032f, 0.09f, 0.045f), Polymer, new Vector3(-18, 0, 0));
                    Box(t, "Stock", new Vector3(0, 0.03f, -0.27f), new Vector3(0.045f, 0.1f, 0.24f), Polymer);
                    Box(t, "Sight", new Vector3(0, 0.105f, 0.08f), new Vector3(0.03f, 0.035f, 0.08f), Metal);
                    muzzle = 0.62f;
                    support = 0.2f;
                    break;
            }

            var muzzlePoint = new GameObject("Muzzle").transform;
            muzzlePoint.SetParent(t, false);
            muzzlePoint.localPosition = new Vector3(0, weaponClass == WeaponClass.Shotgun ? 0.07f : 0.055f, muzzle);
            var ejector = new GameObject("Ejector").transform;
            ejector.SetParent(t, false);
            ejector.localPosition = new Vector3(0.03f, 0.07f, 0.08f);
            Transform right = Point(t, "RightHandGrip", new Vector3(0, -0.02f, -0.02f), new Vector3(-15, 0, -90));
            Transform left = weaponClass == WeaponClass.Pistol
                ? Point(t, "LeftHandGrip", new Vector3(-0.025f, -0.035f, 0f), new Vector3(-15, 0, 90))
                : Point(t, "LeftHandGrip", new Vector3(0, 0.02f, support), new Vector3(0, 0, 90));
            return new WeaponModel { Root = root, Muzzle = muzzlePoint, Ejector = ejector, RightHandGrip = right, LeftHandGrip = left };
        }

        private static Transform Point(Transform parent, string name, Vector3 position, Vector3 euler)
        {
            var point = new GameObject(name).transform;
            point.SetParent(parent, false);
            point.localPosition = position;
            point.localRotation = Quaternion.Euler(euler);
            return point;
        }

        private static void Box(Transform parent, string name, Vector3 center, Vector3 size, Color color, Vector3 euler = default)
        {
            GameObject go = PracticeViews.CreatePrimitive(PrimitiveType.Cube, parent, name, color);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = size;
            go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>A cylinder along +Z centred on <paramref name="center"/>.</summary>
        private static void Barrel(Transform parent, Vector3 center, float radius, float length, Color color)
        {
            GameObject go = PracticeViews.CreatePrimitive(PrimitiveType.Cylinder, parent, "Barrel", color);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.Euler(90, 0, 0);
            go.transform.localScale = new Vector3(radius * 2, length / 2, radius * 2);
            go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
