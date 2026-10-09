// The station panel (Phase 12's workbench, Phase 22's forge): the recipes
// made at this station's kind, through the shared CraftList. DUMB like the
// other panels: it reads Character and SkillSheet, and hands finished
// `craft` cmd bytes to Send. Boot owns the station's entity id, its kind and
// the refusal line, and sets them before Show.

using System;
using Godot;

namespace SpaceAdventure.Game.UI
{
    /// <summary>A station: its recipe cards with skill, level, seconds, have/need, QTY and CRAFT.</summary>
    public sealed class BenchView : ModalView
    {
        /// <summary>The station NPC's entity id; Boot sets it before Show.</summary>
        public uint Bench;
        /// <summary>"bench" or "forge": which recipes this station makes.</summary>
        public string Station = "bench";
        /// <summary>A refusal or notice line from Boot; shown at the bottom when non-empty.</summary>
        public string Status = "";

        public readonly CraftList List;

        public BenchView(Control root, Character character, SkillSheet skills, Icons icons,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Workbench", 520)
        {
            List = new CraftList(character, skills, icons, nextSeq, send);
            // Phase 12 kept the panel open on CRAFT; so does this — the bar
            // runs under it and the counts redraw as units land.
            List.Crafted = (r, qty) => { Status = "working…"; Rebuild(); };
        }

        /// <summary>The rig's press: CRAFT on a recipe's row.</summary>
        public bool Press(string recipe, out string why) => List.Press(Bench, recipe, out why);

        protected override void Fill(VBoxContainer body)
        {
            SetTitle(Station == "forge" ? "Forge" : "Workbench");
            List.Fill(body, Station, Bench, Rebuild);
            if (!string.IsNullOrEmpty(Status))
                Line(body, Status, Styles.Dust);
            body.AddChild(Styles.Gap(4));
            Line(body, "F closes  ·  moving or a hit stops the work  ·  materials spill on death", Styles.Dust, 11);
        }
    }
}
