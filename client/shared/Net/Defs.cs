// The `defs` message, parsed.
//
// The server owns the item, entity and NPC tables (server/data/items.json,
// server/data/npcs.json) and ships the client-visible slice of them once, on
// join, as a JSON blob. Its shape is server/internal/defs/defs.go `payload`:
//
//     { "items":    { "<item id>":   { name, kind, slot, asset, ... } },
//       "entities": { "<type name>": { type, asset, max_health, hitbox } },
//       "npcs":     { "<npc id>":    { name, asset, verb } },
//       "constants": { interact_dist, interact_cone } }
//
// Every one of those is a JSON MAP, which is exactly what Unity's JsonUtility
// cannot represent -- it has no dictionary support at all. That is why this
// used to be read by scanning the raw blob for a quoted key and walking
// braces to the matching close: a narrow reader over three fields, living in
// Interact.cs. It worked, and it could only ever answer the questions it had
// been cut for.
//
// The `asset` field is what forced the issue. It is the id of the model in
// art/manifest.json -- "char.player", "npc.grunt", "weapon.pulse" -- and it
// has been crossing the wire since Phase 2 with nothing on this end reading
// it, because reading a fourth field meant another bespoke scan. Parsing the
// blob properly once is smaller than scanning it four ways.
//
// Newtonsoft is the parser because Unity ships it as a first-party package
// (com.unity.nuget.newtonsoft-json) and it is on NuGet for the headless
// build, so the same source compiles under both. It references no engine
// type, which is what lets this file live in `Net` at all -- CONVENTIONS.md
// rule 2: Net may not see UnityEngine.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace SpaceAdventure.Net
{
    /// <summary>One row of the item table, as the client sees it.</summary>
    public sealed class ItemDef
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("kind")] public string Kind { get; set; } = "";
        [JsonProperty("slot")] public string Slot { get; set; } = "";
        [JsonProperty("asset")] public string Asset { get; set; } = "";

        /// <summary>Phase 8: absent reads as "" and renders common (C64).</summary>
        [JsonProperty("rarity")] public string Rarity { get; set; } = "";

        // Phase 11.7: what the character panel and tooltips read.
        [JsonProperty("desc")] public string Desc { get; set; } = "";
        [JsonProperty("armor")] public ArmorDef Armor { get; set; }
        [JsonProperty("weapon")] public WeaponDef Weapon { get; set; }
        [JsonProperty("stack_max")] public int StackMax { get; set; } = 1;

        // Phase 12: what a shop pays before sell_rate (0 = unsellable), and
        // the lesser tool this one stands in for.
        [JsonProperty("value")] public long Value { get; set; }
        [JsonProperty("supersedes")] public string Supersedes { get; set; } = "";

        // Phase 13: what `use` does with it, and a mod's deltas.
        [JsonProperty("consumable")] public ConsumableDef Consumable { get; set; }
        [JsonProperty("ability")] public AbilityDef Ability { get; set; }
        [JsonProperty("mod")] public ModDef Mod { get; set; }

        /// <summary>Phase 13: something `use` accepts — a consumable or worn gear with an ability.</summary>
        public bool Usable => Consumable != null || Ability != null;
    }

    public sealed class ConsumableDef
    {
        [JsonProperty("heal")] public int Heal { get; set; }
        [JsonProperty("cooldown")] public double Cooldown { get; set; }
    }

    public sealed class AbilityDef
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("range")] public double Range { get; set; }
        [JsonProperty("cooldown")] public double Cooldown { get; set; }
    }

    /// <summary>Additive deltas onto the primary's weapon table (server sim.ApplyMod).</summary>
    public sealed class ModDef
    {
        [JsonProperty("damage")] public int Damage { get; set; }
        [JsonProperty("magazine")] public int Magazine { get; set; }
        [JsonProperty("max_range")] public double MaxRange { get; set; }
        [JsonProperty("falloff_start")] public double FalloffStart { get; set; }
        [JsonProperty("falloff_end")] public double FalloffEnd { get; set; }
    }

    /// <summary>One resource node def (server/data/nodes.json, Phase 12).</summary>
    public sealed class NodeDef
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("asset")] public string Asset { get; set; } = "";
        [JsonProperty("skill")] public string Skill { get; set; } = "";
        [JsonProperty("level")] public int Level { get; set; }
        [JsonProperty("tool")] public string Tool { get; set; } = "";
        [JsonProperty("channel")] public double Channel { get; set; }
        [JsonProperty("yields")] public int Yields { get; set; }
    }

    /// <summary>An item and a count, as recipes name them.</summary>
    public sealed class ItemQtyDef
    {
        [JsonProperty("item")] public string Item { get; set; } = "";
        [JsonProperty("qty")] public int Qty { get; set; }
    }

    /// <summary>One workbench recipe (server/data/recipes.json, Phase 12).</summary>
    public sealed class RecipeDef
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("level")] public int Level { get; set; }
        [JsonProperty("inputs")] public List<ItemQtyDef> Inputs { get; set; } = new List<ItemQtyDef>();
        [JsonProperty("output")] public ItemQtyDef Output { get; set; } = new ItemQtyDef();
        [JsonProperty("xp")] public long XP { get; set; }
    }

    /// <summary>The GDD constants the client mirrors for display.</summary>
    public sealed class ConstantsDef
    {
        [JsonProperty("sell_rate")] public double SellRate { get; set; }
    }

    public sealed class ArmorDef
    {
        [JsonProperty("value")] public int Value { get; set; }
    }

    public sealed class WeaponDef
    {
        [JsonProperty("damage")] public int Damage { get; set; }
        [JsonProperty("fire_interval")] public double FireInterval { get; set; }
        [JsonProperty("magazine")] public int Magazine { get; set; }
        [JsonProperty("max_range")] public double MaxRange { get; set; }
        [JsonProperty("ammo_item")] public string AmmoItem { get; set; } = "";
    }

    /// <summary>One entity type's render and hitbox def.</summary>
    public sealed class EntityDef
    {
        [JsonProperty("type")] public string Type { get; set; } = "";
        [JsonProperty("asset")] public string Asset { get; set; } = "";
        [JsonProperty("max_health")] public int MaxHealth { get; set; }
    }

    /// <summary>An NPC archetype's display name, model and interaction verb.</summary>
    public sealed class NpcDef
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("asset")] public string Asset { get; set; } = "";
        [JsonProperty("verb")] public string Verb { get; set; } = "";
    }

    /// <summary>
    /// The whole `defs` payload. Every lookup fails soft: an id the server
    /// never sent gives back the id itself (for a name) or an empty string
    /// (for a slot or an asset), never an exception and never a throw on a
    /// null table. A client that has not yet received `defs` is the normal
    /// state for the first few frames, not an error.
    /// </summary>
    /// <summary>One skill roster row (server/data/skills.json, Phase 11).</summary>
    public sealed class SkillDef
    {
        public sealed class EfficacyDef
        {
            [JsonProperty("kind")] public string Kind { get; set; } = "";
            [JsonProperty("per_level")] public double PerLevel { get; set; }
        }
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("reserved")] public bool Reserved { get; set; }
        [JsonProperty("efficacy")] public EfficacyDef Efficacy { get; set; }
    }

    /// <summary>One declared cross-skill bonus (the panel draws these).</summary>
    public sealed class SynergyDef
    {
        [JsonProperty("source")] public string Source { get; set; } = "";
        [JsonProperty("target")] public string Target { get; set; } = "";
        [JsonProperty("what")] public string What { get; set; } = "";
        [JsonProperty("per_level")] public double PerLevel { get; set; }
        [JsonProperty("where")] public string Where { get; set; } = "";
    }

    public sealed class Defs
    {
        [JsonProperty("items")] public Dictionary<string, ItemDef> Items { get; set; }
        /// <summary>Phase 11.7: the ordered equipment slot set; empty means primary only.</summary>
        [JsonProperty("equip_slots")] public List<string> EquipSlots { get; set; } = new List<string>();
        [JsonProperty("inv_slots")] public int InvSlots { get; set; }
        [JsonProperty("skills")] public List<SkillDef> Skills { get; set; }
        [JsonProperty("synergies")] public List<SynergyDef> Synergies { get; set; }
        [JsonProperty("entities")] public Dictionary<string, EntityDef> Entities { get; set; }
        [JsonProperty("npcs")] public Dictionary<string, NpcDef> Npcs { get; set; }
        // Phase 12: nodes and recipes in file order, and the constants.
        [JsonProperty("nodes")] public List<NodeDef> Nodes { get; set; } = new List<NodeDef>();
        [JsonProperty("recipes")] public List<RecipeDef> Recipes { get; set; } = new List<RecipeDef>();
        [JsonProperty("constants")] public ConstantsDef Constants { get; set; } = new ConstantsDef();

        /// <summary>
        /// The payload exactly as it arrived.
        ///
        /// Kept because the tables above are a LOSSY read of it: they take the
        /// handful of fields this client uses and drop the rest. Anything that
        /// needs the bytes -- the C41 codec-parity harness compares them
        /// against Go's -- has to have them, and re-serialising the parsed
        /// object would compare this client's idea of the payload with itself.
        /// </summary>
        public string Raw { get; private set; } = "";

        /// <summary>An empty table, so callers never hold a null.</summary>
        public static readonly Defs Empty = new Defs();

        /// <summary>
        /// Parses the payload, failing SOFT.
        ///
        /// Every lookup on this class already declines to throw -- an unknown
        /// id gives back the id, or an empty string. The parse itself did not,
        /// and it is called straight from the message loop: one payload whose
        /// shape this client does not expect (`items` as an array rather than
        /// a map, say) took the whole connection down with a Newtonsoft
        /// exception. A client that cannot read `defs` should show items by
        /// their ids, not disconnect.
        /// </summary>
        public static Defs Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return Empty;

            Defs defs;
            try
            {
                defs = JsonConvert.DeserializeObject<Defs>(json);
            }
            catch (JsonException)
            {
                defs = null;
            }

            defs ??= new Defs();
            defs.Raw = json;
            return defs;
        }

        /// <summary>Display name for an item, falling back to its id.</summary>
        public string ItemName(string id) =>
            TryItem(id, out ItemDef it) && !string.IsNullOrEmpty(it.Name) ? it.Name : id;

        /// <summary>The equipment slot an item declares, or "" if it declares none.</summary>
        public string SlotOf(string id) => TryItem(id, out ItemDef it) ? it.Slot ?? "" : "";

        /// <summary>Phase 8: the item's rarity tier, "" (=common) when unknown.</summary>
        public string ItemRarity(string id) => TryItem(id, out ItemDef it) ? it.Rarity ?? "" : "";

        /// <summary>True when the item declares an equipment slot at all.</summary>
        public bool IsEquippable(string id) => !string.IsNullOrEmpty(SlotOf(id));

        /// <summary>The item def, or null.</summary>
        public ItemDef Item(string id) => TryItem(id, out ItemDef it) ? it : null;

        /// <summary>Phase 12: a node def by id, or null.</summary>
        public NodeDef Node(string id)
        {
            if (Nodes == null || string.IsNullOrEmpty(id)) return null;
            foreach (NodeDef n in Nodes) if (n.Id == id) return n;
            return null;
        }

        /// <summary>Phase 12: a node's model id, or "" (boxes).</summary>
        public string NodeAsset(string id) => Node(id)?.Asset ?? "";

        /// <summary>
        /// Phase 12: what one unit of an item sells for at the shop, before
        /// the synergy the server applies; 0 = unsellable.
        /// </summary>
        public long SellPrice(string id)
        {
            ItemDef it = Item(id);
            if (it == null || it.Value <= 0) return 0;
            double rate = Constants?.SellRate ?? 0;
            return (long)System.Math.Floor(it.Value * rate);
        }

        /// <summary>Phase 12: whether the worn tool is `want` or supersedes it.</summary>
        public bool ToolSatisfies(string worn, string want)
        {
            for (int i = 0; !string.IsNullOrEmpty(worn) && i < 8; i++)
            {
                if (worn == want) return true;
                worn = Item(worn)?.Supersedes ?? "";
            }
            return false;
        }

        /// <summary>
        /// Mirrors the server's SlotAccepts: exact match, except an
        /// "accessory" item fits any accessoryN slot.
        /// </summary>
        public static bool SlotAccepts(string itemSlot, string slot) =>
            itemSlot == slot || (itemSlot == "accessory" && slot.StartsWith("accessory"));

        /// <summary>The art/manifest.json model id for an item, or "".</summary>
        public string ItemAsset(string id) => TryItem(id, out ItemDef it) ? it.Asset ?? "" : "";

        /// <summary>
        /// The model id for an entity type name ("player", "npc", "target").
        /// </summary>
        public string EntityAsset(string type) =>
            Entities != null && type != null && Entities.TryGetValue(type, out EntityDef d)
                ? d.Asset ?? "" : "";

        /// <summary>
        /// The model id for an NPC archetype ("npc.grunt"). Distinct from
        /// <see cref="EntityAsset"/> because one entity def serves every NPC
        /// on the wire — the archetype is the only thing that tells a
        /// shopkeeper from a grunt.
        /// </summary>
        public string NpcAsset(string archetype) =>
            Npcs != null && archetype != null && Npcs.TryGetValue(archetype, out NpcDef d)
                ? d.Asset ?? "" : "";

        /// <summary>Display name for an NPC archetype, or "".</summary>
        public string NpcName(string archetype) =>
            Npcs != null && archetype != null && Npcs.TryGetValue(archetype, out NpcDef d)
                ? d.Name ?? "" : "";

        private bool TryItem(string id, out ItemDef item)
        {
            item = null;
            return Items != null && id != null && Items.TryGetValue(id, out item);
        }
    }
}
