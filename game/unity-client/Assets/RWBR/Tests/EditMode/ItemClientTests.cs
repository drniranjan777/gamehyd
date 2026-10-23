using System.Numerics;
using NUnit.Framework;
using RWBR.Client.Player;
using RWBR.Client.Practice;
using RWBR.Shared;
using RWBR.Shared.Protocol;

namespace RWBR.Tests
{
    /// <summary>Phase C part 2 on the client: the inventory mirror, grenade loot in practice, controls.</summary>
    public class ItemClientTests
    {
        [Test]
        public void The_client_rebuilds_its_inventory_from_a_snapshot_and_asks_the_same_questions_as_the_server()
        {
            var s = new SnapshotPacket { SelfActiveSlot = 0, SelfVest = 2, SelfHelmet = 1, SelfHelmetPoints = 30, SelfBackpack = 1 };
            s.SelfSlotWeapon[0] = WeaponCatalog.Rifle;
            s.SelfSlotAmmo[0] = 30;
            s.SelfSlotAttach[0] = (byte)Attachments.Scope4x;
            s.SelfItems.Add(new ItemCount { Kind = ItemKind.AmmoMedium, Count = 120 });
            s.SelfItems.Add(new ItemCount { Kind = ItemKind.Frag, Count = 2 });
            var self = new SnapshotEntry { Health = 80, Armor = 50 };

            Combatant c = Combatant.FromSnapshot(s, self);
            Assert.AreEqual(WeaponCatalog.Rifle, c.ActiveWeaponIndex);
            Assert.AreEqual(4f, c.ActiveWeapon.AimZoom, "the scope's zoom");
            Assert.AreEqual(120, c.ReserveAmmo);
            Assert.AreEqual(2, c.Count(ItemKind.Frag));
            Assert.AreEqual(120 * 0.5f + 12, c.Weight, 0.01f);
            Assert.AreEqual(Items.Capacity[1], c.Capacity);
            Assert.AreEqual(0, c.CanTake(ItemKind.Vest1, Items.ArmorPoints[1]), "worse vest");
            Assert.AreEqual(1, c.CanTake(ItemKind.Helmet2, Items.ArmorPoints[2]));
            Assert.AreEqual(1, c.CanTake(ItemKind.Scope4x, 1), "a second scope goes in the backpack");
        }

        [Test]
        public void Practice_does_not_offer_grenades_it_cannot_throw()
        {
            var frag = new LootSpawn(0, LootType.Ammo, Vector3.Zero, 30).WithItem(ItemKind.Frag, 1);
            var bandage = new LootSpawn(1, LootType.Medkit, Vector3.Zero, 1).WithItem(ItemKind.Bandage, 3);
            Assert.IsFalse(PracticeSimulation.Usable(frag));
            Assert.IsTrue(PracticeSimulation.Usable(bandage));
        }

        [Test]
        public void Touch_layout_has_a_throw_button_that_overlaps_nothing()
        {
            string saved = UnityEngine.PlayerPrefs.GetString("rwbr.touchLayout", null);
            try
            {
                TouchControls.ResetLayout();
                Assert.IsTrue(System.Array.Exists(TouchControls.Layout, b => b.Button == TouchButton.Throw));
                Assert.IsEmpty(TouchControls.Overlaps(16f / 9f));
                Assert.IsEmpty(TouchControls.Overlaps(19.5f / 9f));
            }
            finally
            {
                if (saved == null) UnityEngine.PlayerPrefs.DeleteKey("rwbr.touchLayout");
                else UnityEngine.PlayerPrefs.SetString("rwbr.touchLayout", saved);
                TouchControls.ReloadLayout();
            }
        }
    }
}
