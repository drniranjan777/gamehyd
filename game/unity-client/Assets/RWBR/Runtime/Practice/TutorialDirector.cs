using System;
using RWBR.Shared;
using UnityEngine;

namespace RWBR.Client.Practice
{
    /// <summary>
    /// First-run onboarding on the training ground (BRD 5 "onboarding and tutorial"): seven short steps checked
    /// against the simulation. Enemies hold fire until the last step. Completion is remembered on the device.
    /// </summary>
    public sealed class TutorialDirector
    {
        public const string DoneKey = "rwbr.tutorialDone";

        private readonly struct Step
        {
            public Step(string desktop, string touch, Func<TutorialDirector, PracticeSimulation, bool> done)
            {
                Desktop = desktop;
                Touch = touch;
                Done = done;
            }

            public string Desktop { get; }
            public string Touch { get; }
            public Func<TutorialDirector, PracticeSimulation, bool> Done { get; }
        }

        private static readonly Step[] Steps =
        {
            new Step("Look around with the mouse", "Drag on the right side of the screen to look around",
                (t, s) => t.LookedDegrees > 120f),
            new Step("Walk towards the building ahead (W A S D)", "Walk towards the building with the left stick",
                (t, s) => s.Player.Motor.Position.Z > -12f),
            new Step("Go inside through the doorway", "Go inside through the doorway",
                (t, s) => Inside(s.Player.Motor.Position)),
            new Step("Pick up the rifle (E)", "Tap PICK UP next to the rifle",
                (t, s) => s.Player.Combat.HasWeapon),
            new Step("Shoot an enemy (left mouse). They hold fire during training", "Hold FIRE and drag to aim. Enemies hold fire during training",
                (t, s) => t.EnemyHit),
            new Step("Climb the outside stairs to the roof (east side)", "Climb the outside stairs to the roof (east side)",
                (t, s) => s.Player.Motor.Position.Y > 3f),
            new Step("Training complete. Enemies fight back now: clear the area!", "Training complete. Enemies fight back now: clear the area!",
                (t, s) => false),
        };

        private float _lastYaw = float.NaN;

        public TutorialDirector(PracticeSimulation sim)
        {
            sim.EnemiesPassive = true;
        }

        public int Index { get; private set; }
        public int Count => Steps.Length;
        public bool Finished => Index >= Steps.Length - 1;
        public float LookedDegrees { get; private set; }
        public bool EnemyHit { get; set; }

        public string Text(bool touch) => touch ? Steps[Index].Touch : Steps[Index].Desktop;

        public static bool Done
        {
            get
            {
                try { return PlayerPrefs.GetInt(DoneKey, 0) == 1; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Advance when the current step's condition holds. Call every frame.</summary>
        public void Update(PracticeSimulation sim, float viewYawDegrees)
        {
            if (!float.IsNaN(_lastYaw)) LookedDegrees += Mathf.Abs(Mathf.DeltaAngle(_lastYaw, viewYawDegrees));
            _lastYaw = viewYawDegrees;
            if (Finished || !Steps[Index].Done(this, sim)) return;

            Index++;
            if (!Finished) return;
            sim.EnemiesPassive = false;
            try
            {
                PlayerPrefs.SetInt(DoneKey, 1);
                PlayerPrefs.Save();
            }
            catch (Exception)
            {
                // Storage unavailable: the tutorial will run again next time.
            }
        }

        private static bool Inside(System.Numerics.Vector3 p) => Math.Abs(p.X) < 7.7f && Math.Abs(p.Z) < 5.7f && p.Y < 1f;
    }
}
