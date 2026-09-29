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
            var grip = new DragHandle { Target = Box, Bounds = root, OnDropped = SavePosition };
            header.GetChild<Control>(0).AddChild(grip);
            grip.SetAnchorsPreset(Control.LayoutPreset.FullRect);
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

        // ---- position memory --------------------------------------------

        private const string ConfigPath = "user://sa.cfg";
        private bool _restored;

        private void RestorePosition()
        {
            if (_restored) { DragHandle.Clamp(Box, _root); return; }
            _restored = true;
            var cf = new ConfigFile();
            if (cf.Load(ConfigPath) != Error.Ok) return;
            if (!cf.HasSectionKey("panels", _key)) return;
            Vector2 at = cf.GetValue("panels", _key).AsVector2();
            // Deferred: the panel's size is known only after a layout pass.
            Box.CallDeferred(Control.MethodName.SetPosition, at);
            Callable.From(() => DragHandle.Clamp(Box, _root)).CallDeferred();
        }

        private void SavePosition()
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath);
            cf.SetValue("panels", _key, Box.Position);
            cf.Save(ConfigPath);
        }

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
