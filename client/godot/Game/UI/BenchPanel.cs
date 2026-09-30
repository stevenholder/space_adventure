// The workbench (Phase 12 task 14): one card per recipe, the bag's material
// counts along the top. DUMB like the other panels: it reads Character and
// SkillSheet, and hands finished `craft` cmd bytes to Send. Boot owns the
// bench entity id and the refusal line, and sets them before Show.

using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    /// <summary>The bench: recipe cards with inputs, level gate, CRAFT.</summary>
    public sealed class BenchView : ModalView
    {
        private readonly Character _character;
        private readonly SkillSheet _skills;
        private readonly Icons _icons;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        /// <summary>The bench NPC's entity id; Boot sets it before Show.</summary>
        public uint Bench;
        /// <summary>A refusal or notice line from Boot; shown at the bottom when non-empty.</summary>
        public string Status = "";

        private static readonly Color Short = new Color(0.9f, 0.3f, 0.3f);
        private static readonly (string id, string name)[] Materials =
        {
            ("mat.ore.iron", "iron"),
            ("mat.scrap", "scrap"),
            ("mat.ore.copper", "copper"),
        };

        public BenchView(Control root, Character character, SkillSheet skills, Icons icons,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Workbench", 460)
        {
            _character = character;
            _skills = skills;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
        }

        /// <summary>How many of an item the bag holds, across stacks.</summary>
        private int Held(string item)
        {
            int n = 0;
            ItemStack[] inv = _character.Inventory;
            if (inv == null) return 0;
            foreach (ItemStack s in inv)
                if (s != null && s.item == item) n += s.qty;
            return n;
        }

        protected override void Fill(VBoxContainer body)
        {
            Defs defs = _character.Defs;
            var head = Styles.Row(8);
            head.AddChild(Styles.Grow(Styles.Display_("recipes", 12, Styles.Dust)));
            foreach (var (id, name) in Materials)
            {
                int qty = Held(id);
                head.AddChild(Styles.Display_($"{qty} {name}", 12, qty > 0 ? Styles.Amber : Styles.Dust));
            }
            body.AddChild(head);
            body.AddChild(Styles.Gap(4));

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 360), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = Styles.Column(4);
            list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(list);
            int eng = _skills.Level("engineering");
            foreach (RecipeDef r in defs.Recipes)
            {
                string id = r.Id;
                string output = r.Output?.Item ?? "";
                bool locked = eng < r.Level;
                bool missing = false;
                var sb = new StringBuilder();
                foreach (ItemQtyDef input in r.Inputs)
                {
                    bool isShort = Held(input.Item) < input.Qty;
                    missing |= isShort;
                    if (sb.Length > 0) sb.Append(" · ");
                    // have/need reads at a glance; a short input shows what is held.
                    if (isShort) sb.Append(Held(input.Item)).Append('/');
                    sb.Append(input.Qty).Append("× ").Append(defs.ItemName(input.Item));
                }
                var slot = new ItemSlot { Defs = defs, Icons = _icons, Static = true, Item = output, Qty = r.Output?.Qty ?? 0 };
                var level = Styles.Display_($"ENG {r.Level}", 12, locked ? Styles.Dust : Styles.Amber);
                level.CustomMinimumSize = new Vector2(48, 0);
                level.HorizontalAlignment = HorizontalAlignment.Right;
                Control craft = locked || missing
                    ? Styles.Display_("—", 12, Styles.Dust)
                    : Styles.Button("CRAFT", false, () =>
                    {
                        _send(Encode.Cmd(_nextSeq(), Op.Craft, $"{{\"npc\":{Bench},\"recipe\":\"{id}\",\"qty\":1}}"));
                        Rebuild();
                    });
                Color rarity = Styles.Rarity(defs.ItemRarity(output));
                list.AddChild(Styles.Card(rarity, slot, r.Name, rarity, sb.ToString(), level, craft));
            }
            body.AddChild(scroll);

            if (!string.IsNullOrEmpty(Status))
                Line(body, Status, Styles.Dust);
            body.AddChild(Styles.Gap(4));
            Line(body, "E closes  ·  materials spill on death", Styles.Dust, 11);
        }
    }
}
