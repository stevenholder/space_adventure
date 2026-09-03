// Phase 8 tasks 8 and 9 (part) — the economy screens and the prompt, in
// Scrapyard Comic. Views here are DUMB: they read state from Character and
// Interaction (which keep owning the data and the reply handling) and hand
// finished cmd bytes to a Send callback — the exact bytes the IMGUI panels
// built, so t14's wire trace does not move.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game.UI
{
    /// <summary>
    /// One item card: rarity band on the left edge, name, qty, an optional
    /// action button. The looter identity in 60 lines.
    /// </summary>
    public static class ItemCard
    {
        public static VisualElement Make(string name, int qty, string rarity,
            string note, string actionLabel, Action onAction)
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = Styles.Steel;
            Styles.SetBorder(card, Styles.Ink, 2);
            card.style.marginBottom = 4;
            card.style.height = 34;

            var band = new VisualElement();
            band.style.width = 5;
            band.style.height = Length.Percent(100);
            band.style.backgroundColor = Styles.Rarity(rarity);
            card.Add(band);

            var label = Styles.Display_(qty > 1 ? $"{name}  ×{qty}" : name, 14, Styles.Cream);
            label.style.marginLeft = 8;
            label.style.flexGrow = 1;
            card.Add(label);

            if (!string.IsNullOrEmpty(note))
            {
                var n = Styles.Display_(note, 12, Styles.Dust);
                n.style.marginRight = 8;
                card.Add(n);
            }

            if (actionLabel != null)
            {
                var btn = new Button(onAction) { text = actionLabel };
                StyleButton(btn, false);
                card.Add(btn);
            }
            return card;
        }

        public static void StyleButton(Button b, bool danger)
        {
            b.style.backgroundColor = danger ? Styles.Danger : Styles.Amber;
            b.style.color = Styles.Ink;
            b.style.fontSize = 12;
            if (Styles.Display != null)
                b.style.unityFontDefinition = FontDefinition.FromSDFFont(Styles.Display);
            Styles.SetBorder(b, Styles.Ink, 2);
            b.style.marginRight = 6;
            b.style.paddingLeft = 8;
            b.style.paddingRight = 8;
        }
    }

    /// <summary>Bottom-center prompt / notice ("E · talk", refusals).</summary>
    public sealed class PromptView
    {
        private readonly VisualElement _box;
        private readonly Label _label;

        public PromptView(VisualElement root)
        {
            _box = Styles.Panel(Styles.SkewNone);
            _box.style.position = Position.Absolute;
            _box.style.bottom = Length.Percent(34);
            _box.style.left = Length.Percent(50);
            _box.style.translate = new Translate(Length.Percent(-50), 0);
            _box.style.paddingTop = 4;
            _box.style.paddingBottom = 6;
            _box.style.display = DisplayStyle.None;
            _label = Styles.Display_("", 14, Styles.Cream);
            _box.Add(_label);
            root.Add(_box);
        }

        public void Set(string text)
        {
            _box.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            _label.text = text;
        }
    }

    /// <summary>
    /// A centered modal panel with a header; the base for shop/bags/sheet.
    /// </summary>
    public abstract class ModalView
    {
        protected readonly VisualElement Box;
        private readonly VisualElement _body;

        protected ModalView(VisualElement root, string title, float width)
        {
            Box = Styles.Panel(Styles.SkewNone);
            Box.style.position = Position.Absolute;
            Box.style.top = Length.Percent(18);
            Box.style.left = Length.Percent(50);
            Box.style.translate = new Translate(Length.Percent(-50), 0);
            Box.style.width = width;
            Box.style.display = DisplayStyle.None;
            Box.Add(Styles.Header(title));
            _body = new VisualElement();
            Box.Add(_body);
            root.Add(Box);
        }

        public bool Open => Box.style.display == DisplayStyle.Flex;

        public void Show(bool on)
        {
            Box.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (on) Rebuild();
        }

        /// <summary>Clears and repopulates the body from current state.</summary>
        public void Rebuild()
        {
            _body.Clear();
            Fill(_body);
        }

        protected abstract void Fill(VisualElement body);

        protected Label Line(VisualElement body, string text, Color color, int size = 13)
        {
            var l = Styles.Display_(text, size, color);
            body.Add(l);
            return l;
        }
    }

    /// <summary>The shop: stock cards with prices, wallet, carrying list.</summary>
    public sealed class ShopView : ModalView
    {
        private readonly Character _character;
        private readonly Interaction _interact;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public ShopView(VisualElement root, Character character, Interaction interact,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Quartermaster Vex", 380)
        {
            _character = character;
            _interact = interact;
            _nextSeq = nextSeq;
            _send = send;
        }

        protected override void Fill(VisualElement body)
        {
            long credits = _character.Credits;
            Line(body, credits < 0 ? "credits: —" : $"credits: {credits}", Styles.Amber, 14)
                .style.marginBottom = 6;

            var stock = _interact.Stock;
            if (stock != null)
            {
                foreach (var e in stock)
                {
                    string item = e.item;
                    int price = e.price;
                    var card = ItemCard.Make(_character.Defs.ItemName(item), 1,
                        _character.Defs.ItemRarity(item), $"{price} cr",
                        credits >= 0 && credits < price ? null : "BUY",
                        () => { _send(_interact.BuyCmd(_nextSeq(), item, price)); Rebuild(); });
                    body.Add(card);
                }
            }

            if (!string.IsNullOrEmpty(_interact.Status))
                Line(body, _interact.Status, Styles.Dust);

            Line(body, $"carrying {_character.UsedSlots}/{Character.InventorySlots} slots  ·  B bags  ·  E closes",
                Styles.Dust, 12).style.marginTop = 6;
        }
    }

    /// <summary>Bags: the item-card grid with equip actions.</summary>
    public sealed class BagsView : ModalView
    {
        private readonly Character _character;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public BagsView(VisualElement root, Character character,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Bags", 400)
        {
            _character = character;
            _nextSeq = nextSeq;
            _send = send;
        }

        protected override void Fill(VisualElement body)
        {
            Line(body,
                $"{_character.UsedSlots} of {Character.InventorySlots} slots  ·  " +
                (_character.Credits < 0 ? "— cr" : $"{_character.Credits} cr"),
                Styles.Dust).style.marginBottom = 6;

            if (_character.UsedSlots == 0)
            {
                Line(body, "empty", Styles.Dust);
            }
            for (int i = 0; i < _character.UsedSlots; i++)
            {
                var it = _character.Inventory[i];
                string slot = _character.Defs.SlotOf(it.item);
                bool held = !string.IsNullOrEmpty(slot) && it.item == _character.Primary;
                string action = string.IsNullOrEmpty(slot) || held ? null : $"EQUIP";
                string item = it.item;
                string slotName = slot;
                body.Add(ItemCard.Make(_character.Defs.ItemName(item), it.qty,
                    _character.Defs.ItemRarity(item),
                    held ? "equipped" : "", action,
                    () => { _send(_character.EquipCmd(_nextSeq(), slotName, item)); Rebuild(); }));
            }
            Line(body, "B closes", Styles.Dust, 12).style.marginTop = 6;
        }
    }

    /// <summary>The character sheet: read-only stats.</summary>
    public sealed class SheetView : ModalView
    {
        private readonly Character _character;

        public SheetView(VisualElement root, Character character)
            : base(root, "Character", 340)
        {
            _character = character;
        }

        protected override void Fill(VisualElement body)
        {
            void Row(string k, string v)
            {
                var r = new VisualElement();
                r.style.flexDirection = FlexDirection.Row;
                r.style.justifyContent = Justify.SpaceBetween;
                var key = Styles.Display_(k, 13, Styles.Dust);
                var val = Styles.Display_(v, 13, Styles.Cream);
                r.Add(key);
                r.Add(val);
                body.Add(r);
            }
            Row("health", _character.Health.ToString());
            Row("credits", _character.Credits < 0 ? "—" : _character.Credits.ToString());
            Row("primary", string.IsNullOrEmpty(_character.Primary) ? "—" : _character.Defs.ItemName(_character.Primary));
            Row("magazine", _character.Magazine < 0 ? "—" : $"{_character.Magazine} / {_character.Reserve}");
            Line(body, "C closes", Styles.Dust, 12).style.marginTop = 8;
        }
    }
}
