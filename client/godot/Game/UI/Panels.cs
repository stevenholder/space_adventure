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
    /// <summary>
    /// A grip over a panel's header: press to grab, the panel follows the
    /// pointer until release, clamped to the screen. Follows via _Process,
    /// not GuiInput, so a fast drag that leaves the header keeps hold.
    /// </summary>
    public partial class DragHandle : Control
    {
        public Control Target;
        public Control Bounds;
        public System.Action OnDropped;
        private bool _dragging;
        private Vector2 _grab;

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = CursorShape.Move;
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _dragging = true;
                    _grab = GetGlobalMousePosition() - Target.GlobalPosition;
                    AcceptEvent();
                }
                else if (_dragging) Release();
            }
        }

        public override void _Process(double delta)
        {
            if (!_dragging) return;
            if (!Input.IsMouseButtonPressed(MouseButton.Left)) { Release(); return; }
            Target.GlobalPosition = GetGlobalMousePosition() - _grab;
            Clamp(Target, Bounds);
        }

        private void Release()
        {
            _dragging = false;
            OnDropped?.Invoke();
        }

        /// <summary>Keeps at least the header on screen.</summary>
        public static void Clamp(Control target, Control bounds)
        {
            if (target == null || bounds == null) return;
            Vector2 size = target.Size;
            Vector2 max = bounds.Size - new Vector2(Mathf.Min(size.X, 120f), 40f);
            Vector2 p = target.GlobalPosition;
            target.GlobalPosition = new Vector2(Mathf.Clamp(p.X, 0f, Mathf.Max(0f, max.X)), Mathf.Clamp(p.Y, 0f, Mathf.Max(0f, max.Y)));
        }
    }

    /// <summary>
    /// Where a panel was left: user://sa.cfg [panels] key = position. Shared
    /// by the modals and the map, which is not a modal.
    /// </summary>
    public static class PanelMemory
    {
        private const string ConfigPath = "user://sa.cfg";

        /// <summary>Puts the title label's grip on a panel: drag + save.</summary>
        public static void Grip(PanelContainer box, Control root, string key, VBoxContainer header)
        {
            var grip = new DragHandle { Target = box, Bounds = root, OnDropped = () => Save(box, key) };
            header.GetChild<Control>(0).AddChild(grip);
            grip.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        }

        public static void Restore(Control box, Control root, string key, ref bool restored)
        {
            if (restored) { DragHandle.Clamp(box, root); return; }
            restored = true;
            var cf = new ConfigFile();
            if (cf.Load(ConfigPath) != Error.Ok) return;
            if (!cf.HasSectionKey("panels", key)) return;
            Vector2 at = cf.GetValue("panels", key).AsVector2();
            // Deferred: the panel's size is known only after a layout pass.
            box.CallDeferred(Control.MethodName.SetPosition, at);
            Callable.From(() => DragHandle.Clamp(box, root)).CallDeferred();
        }

        public static void Save(Control box, string key)
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath);
            cf.SetValue("panels", key, box.Position);
            cf.Save(ConfigPath);
        }
    }

    public abstract class ModalView
    {
        protected readonly PanelContainer Box;
        private readonly VBoxContainer _body;

        private readonly Control _root;
        private readonly string _key;

        protected ModalView(Control root, string title, float width, float top = 0.18f, float left = 0.5f)
        {
            _root = root;
            _key = title.ToLowerInvariant().Replace(' ', '_');
            Box = Styles.Panel(Styles.SkewNone);
            Styles.PinAt(Box, left, top, width);
            Box.Visible = false;
            VBoxContainer stack = Styles.Body(Box);
            VBoxContainer header = Styles.Header(title);
            stack.AddChild(header);
            // The header is the grip: drag the panel anywhere, and it stays
            // there across sessions (user://sa.cfg [panels]).
            // Parented to the title LABEL, not the header container: a
            // container lays its children out, and the grip would become a
            // zero-height row that nothing can press.
            PanelMemory.Grip(Box, root, _key, header);
            _body = Styles.Column(2);
            stack.AddChild(_body);
            root.AddChild(Box);
        }

        public bool Open => Box.Visible;

        public void Show(bool on)
        {
            Box.Visible = on;
            if (on)
            {
                Rebuild();
                RestorePosition();
            }
        }

        private void RestorePosition() => PanelMemory.Restore(Box, _root, _key, ref _restored);
        private bool _restored;
        private void SavePosition() => PanelMemory.Save(Box, _key);

        /// <summary>Screen position, for the rig's drag proof.</summary>
        public Vector2 Position => Box.Position;
        public Vector2 HeaderCentre => Box.GlobalPosition + new Vector2(Box.Size.X * 0.5f, 18f);

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
        private readonly Icons _icons;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public ShopView(Control root, Character character, Interaction interact, Icons icons,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Quartermaster Vex", 440)
        {
            _character = character;
            _interact = interact;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
        }

        /// <summary>
        /// Where the stock list is scrolled to, kept across rebuilds. A buy
        /// rebuilds the panel twice (the click, then the server's reply), so
        /// this is fed by the scrollbar's own value changes -- never read
        /// back from a fresh box, which starts at 0 -- and restored once the
        /// new bar has a range to restore into.
        /// </summary>
        private int _scrollAt;
        private ScrollContainer _scrollBox;

        /// <summary>The rig's scroll proof: where the list is, and a way to move it.</summary>
        public int ScrollOffset => _scrollBox != null && GodotObject.IsInstanceValid(_scrollBox) ? _scrollBox.ScrollVertical : -1;
        public void ScrollTo(int px) { if (_scrollBox != null && GodotObject.IsInstanceValid(_scrollBox)) _scrollBox.ScrollVertical = px; }

        /// <summary>Phase 13: STOCK or BUYBACK, WoW's two tabs.</summary>
        private bool _buybackTab;
        /// <summary>The rig's tab switch.</summary>
        public void ShowBuyback(bool on) { _buybackTab = on; if (Open) Rebuild(); }

        protected override void Fill(VBoxContainer body)
        {
            long credits = _character.Credits;
            var head = Styles.Row(8);
            head.AddChild(Styles.Button("STOCK", !_buybackTab, () => { _buybackTab = false; Rebuild(); }));
            head.AddChild(Styles.Button("BUYBACK", _buybackTab, () => { _buybackTab = true; Rebuild(); }));
            head.AddChild(Styles.Grow(new Control()));
            head.AddChild(Styles.Display_(credits < 0 ? "— cr" : $"{credits} cr", 14, Styles.Amber));
            body.AddChild(head);
            body.AddChild(Styles.Gap(4));

            if (_buybackTab) { FillBuyback(body); return; }
            var stock = _interact.Stock;
            if (stock != null)
            {
                // Ten items of stock at 76 px each outrun a 720p frame: the
                // list scrolls inside a fixed height, the wallet stays put.
                var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
                _scrollBox = scroll;
                VScrollBar bar = scroll.GetVScrollBar();
                int want = _scrollAt;
                bool restored = want == 0;
                void Restore()
                {
                    if (restored || bar.MaxValue <= bar.Page) return;
                    restored = true;
                    scroll.ScrollVertical = want;
                }
                bar.Changed += Restore;
                bar.ValueChanged += v => { if (restored) _scrollAt = (int)v; };
                var list = Styles.Column(4);
                list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                scroll.AddChild(list);
                foreach (var e in stock)
                {
                    string item = e.item;
                    int price = e.price;
                    SpaceAdventure.Net.Defs defs = _character.Defs;
                    var slot = new ItemSlot { Defs = defs, Icons = _icons, Static = true, Item = item, Qty = 1 };
                    bool owned = _character.SlotHolding(item) != "";
                    bool canAfford = credits < 0 || credits >= price;
                    Control buy = canAfford
                        ? Styles.Button("BUY", false, () => { _send(_interact.BuyCmd(_nextSeq(), item, price)); Rebuild(); })
                        : Styles.Display_("—", 12, Styles.Dust);
                    var priceLab = Styles.Display_($"{price} cr", 13, canAfford ? Styles.Amber : Styles.Dust);
                    priceLab.CustomMinimumSize = new Vector2(60, 0);
                    priceLab.HorizontalAlignment = HorizontalAlignment.Right;
                    list.AddChild(Styles.Card(Styles.Rarity(defs.ItemRarity(item)), slot,
                        defs.ItemName(item) + (owned ? "  ·  worn" : ""), Styles.Rarity(defs.ItemRarity(item)),
                        defs.Item(item)?.Desc ?? "", priceLab, buy));
                }
                body.AddChild(scroll);
            }
            if (!string.IsNullOrEmpty(_interact.Status))
                Line(body, _interact.Status, Styles.Dust);
            body.AddChild(Styles.Gap(4));
            Line(body, "hover for details  ·  right-click a bag item to sell it  ·  F closes", Styles.Dust, 11);
        }

        /// <summary>What was sold this session, newest first, each at what the shop paid.</summary>
        private void FillBuyback(VBoxContainer body)
        {
            var entries = _interact.Buyback;
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = Styles.Column(4);
            list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(list);
            if (entries == null || entries.Length == 0)
                list.AddChild(Styles.Display_("nothing sold yet", 12, Styles.Dust));
            else
                for (int i = entries.Length - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    string item = e.item;
                    SpaceAdventure.Net.Defs defs = _character.Defs;
                    var slot = new ItemSlot { Defs = defs, Icons = _icons, Static = true, Item = item, Qty = e.qty };
                    bool canAfford = _character.Credits < 0 || _character.Credits >= e.price;
                    Control back = canAfford
                        ? Styles.Button("BUY BACK", false, () => { _send(_interact.BuybackCmd(_nextSeq(), item)); Rebuild(); })
                        : Styles.Display_("—", 12, Styles.Dust);
                    var priceLab = Styles.Display_($"{e.price} cr", 13, canAfford ? Styles.Amber : Styles.Dust);
                    priceLab.CustomMinimumSize = new Vector2(60, 0);
                    priceLab.HorizontalAlignment = HorizontalAlignment.Right;
                    list.AddChild(Styles.Card(Styles.Rarity(defs.ItemRarity(item)), slot,
                        defs.ItemName(item) + (e.qty > 1 ? $"  ×{e.qty}" : ""), Styles.Rarity(defs.ItemRarity(item)),
                        "sold this session  ·  the shop's price, no markup", priceLab, back));
                }
            body.AddChild(scroll);
            if (!string.IsNullOrEmpty(_interact.Status)) Line(body, _interact.Status, Styles.Dust);
            body.AddChild(Styles.Gap(4));
            Line(body, "twelve most recent sales  ·  gone when you log out  ·  F closes", Styles.Dust, 11);
        }

        /// <summary>
        /// Phase 12 task 15: the SELL side. One compact row per carried
        /// stack the shop will take (SellPrice > 0) that is not on the
        /// body; click sells one, shift-click the whole stack. The price
        /// shown is the BASE price -- the server may pay more (the
        /// Scavenging→Commerce synergy), so the header says so.
        /// </summary>
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

    /// <summary>
    /// Escape with nothing open: the game menu (WoW's). Return, the account
    /// link, quit. Escape again returns.
    /// </summary>
    public sealed class GameMenuView : ModalView
    {
        private readonly System.Action _onAccount;
        private readonly System.Action _onQuit;

        public GameMenuView(Control root, System.Action onAccount, System.Action onQuit)
            : base(root, "Menu", 260, 0.30f)
        {
            _onAccount = onAccount;
            _onQuit = onQuit;
        }

        protected override void Fill(VBoxContainer body)
        {
            body.AddChild(Styles.Gap(2));
            body.AddChild(Styles.Button("RETURN TO GAME", false, () => Show(false)));
            body.AddChild(Styles.Gap(4));
            body.AddChild(Styles.Button("ACCOUNT", false, () => { Show(false); _onAccount(); }));
            body.AddChild(Styles.Gap(4));
            body.AddChild(Styles.Button("QUIT GAME", true, _onQuit));
            body.AddChild(Styles.Gap(6));
            Line(body, "Esc returns", Styles.Dust, 11);
        }
    }
}
