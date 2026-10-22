using NUnit.Framework;
using RWBR.Client.Greybox;
using RWBR.Shared;
using UnityEngine;
using SVector3 = System.Numerics.Vector3;

namespace RWBR.Tests
{
    public class GreyboxWorldTests
    {
        private static GreyboxWorld Build(string version)
        {
            var go = new GameObject("World");
            var world = go.AddComponent<GreyboxWorld>();
            world.UseMap(version);
            world.Build();
            return world;
        }

        /// <summary>Every server box is in the rendered mesh, and the physics colliders sit exactly on the box tops.</summary>
        [TestCase("greybox-block-100-v2")]
        [TestCase("hyd-city-v1")]
        public void Rendered_world_matches_the_server_layout(string version)
        {
            GreyboxWorld world = Build(version);
            try
            {
                MapLayout layout = world.Layout;
                Assert.AreEqual(version, layout.Version);
                Assert.AreEqual(layout.Obstacles.Count * 24, world.VertexCount, "one 24-vertex box per server obstacle");

                // Probe box tops from above: the collider surface must be at the server's height (water renders flat).
                var rng = new System.Random(3);
                int probed = 0;
                for (int k = 0; k < 400 && probed < 60; k++)
                {
                    int i = rng.Next(layout.Obstacles.Count);
                    Aabb b = layout.Obstacles[i];
                    if (layout.KindOf(i) == BoxKind.Water || b.Max.X - b.Min.X < 0.5f || b.Max.Z - b.Min.Z < 0.5f) continue;
                    float x = (b.Min.X + b.Max.X) / 2, z = (b.Min.Z + b.Max.Z) / 2;
                    // Only test tops that nothing else covers at this point.
                    float expected = 0;
                    for (int j = 0; j < layout.Obstacles.Count; j++)
                    {
                        Aabb o = layout.Obstacles[j];
                        if (layout.KindOf(j) == BoxKind.Water) continue;
                        if (x > o.Min.X && x < o.Max.X && z > o.Min.Z && z < o.Max.Z) expected = Mathf.Max(expected, o.Max.Y);
                    }
                    if (expected > b.Max.Y + 1e-3f) continue;
                    Assert.IsTrue(Physics.Raycast(new Vector3(x, 500, z), Vector3.down, out RaycastHit hit, 1000f), $"no collider over box {i}");
                    Assert.AreEqual(b.Max.Y, hit.point.y, 0.01f, $"box {i} ({layout.KindOf(i)})");
                    probed++;
                }
                Assert.Greater(probed, 20);
                if (version == CityMap.Version) Assert.Greater(world.ChunkCount, 50);
            }
            finally
            {
                Object.DestroyImmediate(world.gameObject);
            }
        }

        [Test]
        public void Shared_rules_compile_in_unity_and_cap_movement_speed()
        {
            var hack = new InputCommand { MoveZ = 1000, Buttons = InputButtons.Sprint };
            SVector3 p = Simulation.Move(SVector3.Zero, hack, 1f, new MapLayout("open", 100, new Aabb[0]));
            Assert.LessOrEqual(p.Length(), GameRules.SprintSpeed + 1e-3f);
        }

        /// <summary>Regression: player builds strip shaders only found by name; the world shader must ship.</summary>
        [Test]
        public void World_shader_is_in_resources_so_player_builds_include_it()
        {
            Shader shader = Resources.Load<Shader>("Shaders/VertexColorLit");
            Assert.IsNotNull(shader, "VertexColorLit must stay under a Resources folder");
            Assert.AreEqual("RWBR/VertexColorLit", shader.name);
            Assert.AreEqual("RWBR/VertexColorLit", RWBR.Client.Greybox.GreyboxWorld.WorldShader().name);
        }
    }
}
