using System.Linq;
using NUnit.Framework;
using RWBR.Client.Greybox;
using RWBR.Shared;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    public class GreyboxWorldTests
    {
        [Test]
        public void World_has_one_collider_per_server_obstacle_with_matching_bounds()
        {
            var go = new GameObject("World");
            try
            {
                var world = go.AddComponent<GreyboxWorld>();
                world.Build();
                MapLayout layout = world.Layout;

                BoxCollider[] obstacles = go.GetComponentsInChildren<BoxCollider>()
                    .Where(c => c.name.StartsWith("Obstacle_"))
                    .OrderBy(c => c.name)
                    .ToArray();
                Assert.AreEqual(layout.Obstacles.Count, obstacles.Length);

                for (int i = 0; i < obstacles.Length; i++)
                {
                    Bounds b = obstacles[i].bounds;
                    Aabb a = layout.Obstacles[i];
                    Assert.That(Vector3.Distance(b.min, new Vector3(a.Min.X, a.Min.Y, a.Min.Z)), Is.LessThan(1e-3f), obstacles[i].name);
                    Assert.That(Vector3.Distance(b.max, new Vector3(a.Max.X, a.Max.Y, a.Max.Z)), Is.LessThan(1e-3f), obstacles[i].name);
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Shared_rules_compile_in_unity_and_cap_movement_speed()
        {
            var hack = new InputCommand { MoveZ = 1000, Buttons = InputButtons.Sprint };
            SVector3 p = Simulation.Move(SVector3.Zero, hack, 1f, new MapLayout("open", 100, new Aabb[0]));
            Assert.LessOrEqual(p.Length(), GameRules.SprintSpeed + 1e-3f);
        }
    }
}
