// The economy screens and the prompt, in Scrapyard Comic. Views here are
// DUMB: they read state from Character and Interaction (which keep owning
// the data and the reply handling) and hand finished cmd bytes to a Send
// callback — the exact bytes the earlier panels built, so t14's wire trace
// does not move.

using System;
using Godot;

namespace SpaceAdventure.Game.UI
{
    /// <summary>
    /// One item card: rarity band on the left edge, name, qty, an optional
    /// action button. The looter identity in 40 lines.
    /// </summary>
    public static class ItemCard
    {
        public static Control Make(string name, int qty, string rarity,
            string note, string actionLabel, Action onAction)
        {
            var sb = new StyleBoxFlat { BgColor = Styles.Steel, BorderColor = Styles.Ink };
            sb.SetBorderWidthAll(2);
            var card = new PanelContainer { CustomMinimumSize = new Vector2(0, 34) };
            card.AddThemeStyleboxOverride("panel", sb);
            var row = Styles.Row(8);
            card.AddChild(row);

            row.AddChild(new ColorRect
            {
                Color = Styles.Rarity(rarity), CustomMinimumSize = new Vector2(5, 0),
                SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            row.AddChild(Styles.Grow(Styles.Display_(qty > 1 ? $"{name}  ×{qty}" : name, 14, Styles.Cream)));
            if (!string.IsNullOrEmpty(note)) row.AddChild(Styles.Display_(note, 12, Styles.Dust));
            if (actionLabel != null)
            {
                var btn = Styles.Button(actionLabel, false, onAction);
                btn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                row.AddChild(btn);
                row.AddChild(new Control { CustomMinimumSize = new Vector2(2, 0) });
            }
            return card;
        }
    }

    /// <summary>Bottom-center prompt / notice ("E · talk", refusals).</summary>
    public sealed class PromptView
    {
        private readonly PanelContainer _box;
        private readonly Label _label;

        public PromptView(Control root)
        {
            _box = Styles.Panel(Styles.SkewNone);
            _box.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.PinAt(_box, 0.5f, 0.66f, 0, Control.GrowDirection.Both, Control.GrowDirection.Begin);
            Styles.SetPadding(_box, 14, 4, 14, 6);
            _box.Visible = false;
            _label = Styles.Display_("", 14, Styles.Cream);
            Styles.Body(_box).AddChild(_label);
            root.AddChild(_box);
        }

        public void Set(string text)
        {
            _box.Visible = !string.IsNullOrEmpty(text);
            _label.Text = text ?? "";
        }
    }

    /// <summary>
    /// A centered modal panel with a header; the base for shop/bags/sheet.
    /// </summary>
    public abstract class ModalView
    {
        protected readonly PanelContainer Box;
        private readonly VBoxContainer _body;

        protected ModalView(Control root, string title, float width)
        {
            Box = Styles.Panel(Styles.SkewNone);
            Styles.PinAt(Box, 0.5f, 0.18f, width);
            Box.Visible = false;
            VBoxContainer stack = Styles.Body(Box);
            stack.AddChild(Styles.Header(title));
            _body = Styles.Column(2);
            stack.AddChild(_body);
            root.AddChild(Box);
        }

        public bool Open => Box.Visible;

        public void Show(bool on)
        {
            Box.Visible = on;
            if (on) Rebuild();
        }

        /// <summary>Clears and repopulates the body from current state.</summary>
        public void Rebuild()
        {
            foreach (Node child in _body.GetChildren())
            {
                _body.RemoveChild(child);
                child.QueueFree();
            }
            Fill(_body);
        }

        protected abstract void Fill(VBoxContainer body);

        protected Label Line(VBoxContainer body, string text, Color color, int size = 13)
        {
            var l = Styles.Display_(text, size, color);
            body.AddChild(l);
            return l;
        }

        /// <summary>A row with a growing label and trailing buttons.</summary>
        protected static HBoxContainer LabelRow(VBoxContainer body, string text, int size, Color color)
        {
            var row = Styles.Row(6);
            row.AddChild(Styles.Grow(Styles.Display_(text, size, color)));
            body.AddChild(row);
            return row;
        }
    }

    /// <summary>The shop: stock cards with prices, wallet, carrying list.</summary>
    public sealed class ShopView : ModalView
    {
        private readonly Character _character;
        private readonly Interaction _interact;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public ShopView(Control root, Character character, Interaction interact,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Quartermaster Vex", 380)
        {
            _character = character;
            _interact = interact;
            _nextSeq = nextSeq;
            _send = send;
        }

        protected override void Fill(VBoxContainer body)
        {
            long credits = _character.Credits;
            Line(body, credits < 0 ? "credits: —" : $"credits: {credits}", Styles.Amber, 14);
            body.AddChild(Styles.Gap(4));

            var stock = _interact.Stock;
            if (stock != null)
            {
                foreach (var e in stock)
                {
                    string item = e.item;
                    int price = e.price;
                    body.AddChild(ItemCard.Make(_character.Defs.ItemName(item), 1,
                        _character.Defs.ItemRarity(item), $"{price} cr",
                        credits >= 0 && credits < price ? null : "BUY",
                        () => { _send(_interact.BuyCmd(_nextSeq(), item, price)); Rebuild(); }));
                }
            }

            if (!string.IsNullOrEmpty(_interact.Status))
                Line(body, _interact.Status, Styles.Dust);

            body.AddChild(Styles.Gap(4));
            Line(body, $"carrying {_character.UsedSlots}/{Character.InventorySlots} slots  ·  B bags  ·  E closes",
                Styles.Dust, 12);
        }
    }

    /// <summary>Bags: the item-card list with equip actions.</summary>
    public sealed class BagsView : ModalView
    {
        private readonly Character _character;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public BagsView(Control root, Character character,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Bags", 400)
        {
            _character = character;
            _nextSeq = nextSeq;
            _send = send;
        }

        protected override void Fill(VBoxContainer body)
        {
            Line(body,
                $"{_character.UsedSlots} of {Character.InventorySlots} slots  ·  " +
                (_character.Credits < 0 ? "— cr" : $"{_character.Credits} cr"),
                Styles.Dust);
            body.AddChild(Styles.Gap(4));

            if (_character.UsedSlots == 0) Line(body, "empty", Styles.Dust);
            for (int i = 0; i < _character.UsedSlots; i++)
            {
                var it = _character.Inventory[i];
                string slot = _character.Defs.SlotOf(it.item);
                bool held = !string.IsNullOrEmpty(slot) && it.item == _character.Primary;
                string action = string.IsNullOrEmpty(slot) || held ? null : "EQUIP";
                string item = it.item;
                string slotName = slot;
                body.AddChild(ItemCard.Make(_character.Defs.ItemName(item), it.qty,
                    _character.Defs.ItemRarity(item),
                    held ? "equipped" : "", action,
                    () => { _send(_character.EquipCmd(_nextSeq(), slotName, item)); Rebuild(); }));
            }
            body.AddChild(Styles.Gap(4));
            Line(body, "B closes", Styles.Dust, 12);
        }
    }

    /// <summary>
    /// The F1 account panel: redeem a link code minted on the account site.
    /// The redeem request stays in Boot (it owns the network); this view
    /// only collects the code and shows status.
    /// </summary>
    public sealed class AccountView : ModalView
    {
        private readonly LineEdit _code;
        private readonly Label _status;

        public AccountView(Control root, Action<string> onLink, Action onClose)
            : base(root, "Account link", 360)
        {
            VBoxContainer stack = Styles.Body(Box);
            stack.AddChild(Styles.Display_("Mint a code on the account site, type it here.", 13, Styles.Dust));
            _code = new LineEdit { MaxLength = 8 };
            _code.AddThemeFontSizeOverride("font_size", 16);
            stack.AddChild(_code);
            stack.AddChild(Styles.Gap(4));
            var row = Styles.Row(6);
            row.AddChild(Styles.Button("LINK", false, () => onLink(_code.Text.ToUpperInvariant())));
            row.AddChild(Styles.Button("CLOSE", true, onClose));
            stack.AddChild(row);
            _status = Styles.Display_("", 13, Styles.Dust);
            stack.AddChild(Styles.Gap(4));
            stack.AddChild(_status);
        }

        public void SetStatus(string text) => _status.Text = text;

        protected override void Fill(VBoxContainer body) { }
    }

    /// <summary>The character sheet: read-only stats.</summary>
    public sealed class SheetView : ModalView
    {
        private readonly Character _character;

        public SheetView(Control root, Character character)
            : base(root, "Character", 340)
        {
            _character = character;
        }

        protected override void Fill(VBoxContainer body)
        {
            void Row(string k, string v)
            {
                var r = Styles.Row(6);
                r.AddChild(Styles.Grow(Styles.Display_(k, 13, Styles.Dust)));
                r.AddChild(Styles.Display_(v, 13, Styles.Cream));
                body.AddChild(r);
            }
            Row("health", _character.Health.ToString());
            Row("credits", _character.Credits < 0 ? "—" : _character.Credits.ToString());
            Row("primary", string.IsNullOrEmpty(_character.Primary) ? "—" : _character.Defs.ItemName(_character.Primary));
            Row("magazine", _character.Magazine < 0 ? "—" : $"{_character.Magazine} / {_character.Reserve}");
            body.AddChild(Styles.Gap(6));
            Line(body, "C closes", Styles.Dust, 12);
        }
    }
}
