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
    public sealed class Defs
    {
        [JsonProperty("items")] public Dictionary<string, ItemDef> Items { get; set; }
        [JsonProperty("entities")] public Dictionary<string, EntityDef> Entities { get; set; }
        [JsonProperty("npcs")] public Dictionary<string, NpcDef> Npcs { get; set; }

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

        /// <summary>True when the item declares an equipment slot at all.</summary>
        public bool IsEquippable(string id) => !string.IsNullOrEmpty(SlotOf(id));

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
