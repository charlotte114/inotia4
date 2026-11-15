using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;

namespace Inotia4.Ui;

public sealed record PartyPanelContext(
    PartyRoster Party,
    string PlayerActorId,
    WorldIndex World,
    StoryClassCatalog Classes,
    SkillCatalog Catalog,
    SkillConfiguration PlayerSkills,
    MenuPanelsCatalog Panels,
    IReadOnlyDictionary<string, string> StoryAssets,
    Func<string, Texture2D?> Texture,
    Action<string, string>? Inspect = null,
    Action<string>? ShowPage = null)
{
    public string? Asset(string key) => Panels.Asset(key);
}

public partial class PartyPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color Gold = new("#f3deb0");

    private static readonly Color ErrorText = new("#e6a191");

    /// <summary>选中队员列那一格的高亮（原版取色 0xffda88）。</summary>
    private static readonly Color Selection = new("ffda88");

    /// <summary>详情栏确认弹层的底色（原版取色 0x15100b，不透明度 96%）。</summary>
    private static readonly Color ModalBack = new(0x15 / 255f, 0x10 / 255f, 0x0b / 255f, .96f);

    private static readonly string[] Tabs = ["info", "equipment", "skills"];

    private sealed record MemberView(
        int Level,
        CharacterPanel Panel,
        PlayerVitals Player,
        string ClassId,
        string Source,
        string ActorId,
        CompanionMember? Companion);

    private sealed class PartyModal
    {
        public string Kind = string.Empty;
        public string Id = string.Empty;
        public string Skill = string.Empty;
        public string? Group;
        public string Error = string.Empty;
    }

    private PartyPanelContext? _context;

    private Control _canvas = null!;
    private Control _content = null!;

    private Control _tabs = null!;

    private Control? _modal;

    private int _page;

    private string? _selected;

    private string _tab = "info";

    private PartyModal? _modalState;

    private string _error = string.Empty;

    private int _gridScroll;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;

        _canvas = new Control { Name = "Canvas", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_canvas);
        DesignStage.Center(_canvas);

        _tabs = new Control
        {
            Name = "Tabs",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_tabs);

        _content = new Control
        {
            Name = "Content",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_content);

        Resized += LayoutCanvas;
        LayoutCanvas();
    }

    private void LayoutCanvas()
    {
        var viewport = GetViewportRect().Size;
        if (viewport.X <= 0f || viewport.Y <= 0f) return;

        var scale = Math.Min(viewport.X / DesignWidth, viewport.Y / DesignHeight);
        _canvas.Scale = new Vector2(scale, scale);
        _canvas.Position = new Vector2(
            (viewport.X - DesignWidth * scale) / 2f,
            (viewport.Y - DesignHeight * scale) / 2f);
    }

    public bool IsOpen => Visible;

    public const string Page = "party";

    public void Open(PartyPanelContext context)
    {
        _context = context;
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void Close()
    {
        Visible = false;
        _modalState = null;
    }

    public bool Escape()
    {
        if (_modalState is null) return false;
        _modalState = null;
        Render();
        return true;
    }

    public void Select(string id)
    {
        if (_context is null) return;
        var slots = _context.Party.Snapshot().RosterSlots;
        var slot = slots.IndexOf(id);
        if (slot >= 0) _page = slot / 9;
        _selected = id;
        _error = string.Empty;
        _gridScroll = 0;
        _modalState = null;
        Render();
    }

    public void Render()
    {
        if (_context is not { } context) return;

        ReleaseModal();
        PanelGfx.Clear(_content);
        PanelGfx.Clear(_tabs);

        MenuTabBar.Draw(_tabs, context.StoryAssets, context.Texture, Page, context.ShowPage);

        var snapshot = context.Party.Snapshot();

        var story = new List<string> { "player" };
        foreach (var member in snapshot.Members)
            if (member.Source == "story") story.Add(member.Id);

        if (_selected is not null && MemberOf(_selected) is null)
        {
            _selected = null;
            _modalState = null;
        }
        _selected ??= story.Count > 0 ? story[0] : null;

        DrawPageButton(context);
        DrawStoryColumn(context, story);
        DrawRoster(context, snapshot);
        DrawDetail(context, snapshot);

        if (_error.Length > 0)
            PanelGfx.Text(_error, 70f, 434f, 18, ErrorText, _content);

        if (_modalState is not null) DrawModal(context);
    }

    private void DrawPageButton(PartyPanelContext context)
    {
        var button = PanelGfx.Hit(9f, -4f, 56f, 56f, () =>
        {
            _page = 1 - _page;
            _selected = null;
            _modalState = null;
            _error = string.Empty;
            _gridScroll = 0;
            Render();
        }, _content);
        PanelGfx.Image(context.Texture, context.Asset($"party-page-{_page + 1}"),
            0f, 0f, 56f, 56f, button);
    }

    private void DrawStoryColumn(PartyPanelContext context, List<string> story)
    {
        for (var index = 0; index < story.Count; index += 1)
        {
            var id = story[index];
            var member = MemberOf(id)!;
            var button = PanelGfx.Hit(6f, 80f + index * 91f, 52f, 65f, () => Select(id), _content);
            var portrait = PanelGfx.Image(context.Texture, PortraitOf(id),
                0f, 0f, 52f, 58f, button);
            if (portrait is not null)
                portrait.Modulate = new Color(1f, 1f, 1f, member.Player.Health > 0 ? 1f : .45f);

            PanelGfx.Centered(id == "player" ? "主角" : "伙伴", 26f - 60f, 63f, 120f, 16, Gold, button);

            if (_selected == id)
            {
                PanelGfx.Fill(0f, 0f, 52f, 58f, new Color(Selection, .12f), button);
                DrawStroke(button, 52f, 58f, 2f, Selection);
            }
        }
    }

    private static void DrawStroke(Control parent, float width, float height, float thick, Color color)
    {
        PanelGfx.Fill(0f, 0f, width, thick, color, parent);
        PanelGfx.Fill(0f, height - thick, width, thick, color, parent);
        PanelGfx.Fill(0f, thick, thick, height - thick * 2f, color, parent);
        PanelGfx.Fill(width - thick, thick, thick, height - thick * 2f, color, parent);
    }

    private void DrawRoster(PartyPanelContext context, PartySnapshot snapshot)
    {
        for (var index = 0; index < 9; index += 1)
        {
            var id = index < snapshot.RosterSlots.Count ? snapshot.RosterSlots[_page * 9 + index] : null;
            var x = 70f + index % 3 * 136f;
            var y = 5f + index / 3 * 123f;

            var slotId = id;
            var button = PanelGfx.Hit(x, y, 127f, 114f,
                () => { if (slotId is not null) Select(slotId); }, _content);

            PanelGfx.Image(context.Texture, context.Asset("party-slot"), 0f, 0f, 127f, 114f, button);
            var member = id is null ? null : MemberOf(id);
            if (member is null)
            {
                PanelGfx.Image(context.Texture, context.Asset("party-empty"), 0f, 0f, 127f, 114f, button);
            }
            else
            {
                var portrait = PanelGfx.Image(context.Texture, PortraitOf(id!), 0f, 0f, 127f, 114f, button);
                var fallen = member.Player.Health <= 0;
                if (portrait is not null && fallen) portrait.Modulate = new Color(1f, 1f, 1f, .45f);
                if (fallen)
                    PanelGfx.Centered("阵亡", 63f - 60f, 88f, 120f, 18, ErrorText, button);
            }

            PanelGfx.Image(context.Texture, context.Asset("party-frame"), -7f, -7f, 141f, 128f, button);
            if (id is not null && id == _selected)
                PanelGfx.Image(context.Texture, context.Asset("party-selected"), -7f, -7f, 141f, 128f, button);
            if (id is not null && snapshot.Active.Contains(id))
                PanelGfx.Image(context.Texture, context.Asset("party-deployed"), 68f, 54f, 78f, 64f, button);
        }
    }

    private void DrawDetail(PartyPanelContext context, PartySnapshot snapshot)
    {
        var id = _selected;
        var member = id is null ? null : MemberOf(id);

        // 佣兵页只画边框不铺底（UI_DrawBorderRect）：面板里露出的是遮罩下的世界画面。
        PanelGfx.Border(context.Texture, context.Asset, 482f, 0f, 409f, 431f, _content);
        PanelGfx.RoundBand(context.Texture, context.Asset, 516f, 136f, 341f, 101f, 50, _content);

        for (var index = 0; index < Tabs.Length; index += 1)
        {
            var tab = Tabs[index];
            var button = PanelGfx.Hit(882f, 33f + index * 117f, 50f, 130f, () =>
            {
                _tab = tab;
                _gridScroll = 0;
                Render();
            }, _content, disabled: member is null);
            PanelGfx.Image(context.Texture, context.Asset($"party-tab-{tab}"),
                0f, 0f, 50f, 130f, button);
            button.Modulate = member is null
                ? new Color(1f, 1f, 1f, .4f)
                : new Color(1f, 1f, 1f, _tab == tab ? 1f : .55f);
        }

        if (id is null || member is null) return;

        switch (_tab)
        {
            case "info":
                DrawInfo(context, id, member);
                break;
            case "equipment":
                DrawEquipment(context, id, member);
                break;
            default:
                DrawSkills(context, id, member);
                break;
        }

        if (id != "player" && member.Source == "mercenary")
        {
            var deployed = snapshot.Active.Contains(id);
            var fallen = member.Player.Health <= 0;
            MenuButton(context, deployed ? "party-reserve" : "party-join", 70f,
                () =>
                {
                    _error = string.Empty;
                    try
                    {
                        if (deployed) context.Party.Reserve(id);
                        else context.Party.DeployFirstAvailable(id);
                    }
                    catch (Exception error)
                    {
                        _error = error.Message;
                    }
                    Render();
                }, disabled: deployed ? fallen : snapshot.Active.Count >= 3 || fallen);
            MenuButton(context, "party-dismiss", 272f,
                () =>
                {
                    _modalState = new PartyModal { Kind = "dismiss", Id = id };
                    Render();
                }, disabled: deployed);
        }
    }

    /// <summary>
    /// 「加入」「踢出」「放逐」三枚按钮：底图 225×78 压在 197×52 的命中区上。
    /// 底图本身已经把字印在图上，所以这里只贴图、不再叠标签——叠上去会和图上原有的字压成两层。
    /// </summary>
    private void MenuButton(PartyPanelContext context, string image, float x,
        Action action, bool disabled)
    {
        var button = PanelGfx.Hit(x, 374f, 197f, 52f, action, _content, disabled);
        PanelGfx.Image(context.Texture, context.Asset(image), -14f, -13f, 225f, 78f, button);
        if (disabled) button.Modulate = new Color(1f, 1f, 1f, .4f);
    }

    private void DrawInfo(PartyPanelContext context, string id, MemberView member)
    {
        var character = context.Classes.Class(member.ClassId);

        PanelGfx.Centered($"Lv. {member.Level}", 686f - 175f, 21f, 350f, 21, Gold, _content);
        PanelGfx.Centered(character?.Name ?? member.ClassId, 686f - 175f, 49f, 350f, 21, Gold, _content);
        PanelGfx.Centered(NameOf(id), 686f - 175f, 78f, 350f, 25, Gold, _content);

        var rows = character?.Attributes ?? [];
        for (var index = 0; index < PrimaryStats.All.Length; index += 1)
        {
            var x = 536f + index % 2 * 162f;
            var y = 147f + index / 2 * 28f;
            var name = index < rows.Length ? rows[index].Name : PrimaryStats.All[index];
            PanelGfx.Text(name, x, y, 18, Gold, _content);
            PanelGfx.Right(PrimaryAttributeMath.Get(member.Panel.Attributes, PrimaryStats.All[index]).ToString(),
                x + 138f, y, 18, Colors.White, _content);
        }

        DrawGauges(context, member.Player);
    }

    private void DrawGauges(PartyPanelContext context, PlayerVitals resources)
    {
        var labels = context.Panels.Character.Labels;

        (string Key, string Label, int Value, int Max)[] rows =
        {
            ("health", labels.Health, resources.Health, resources.MaxHealth),
            ("mana", labels.Mana, resources.Mana, resources.MaxMana),
            ("experience", labels.Experience, resources.Experience, resources.NextLevelExperience),
        };

        for (var index = 0; index < rows.Length; index += 1)
        {
            var (key, label, value, max) = rows[index];
            var y = 273f + index * 50f;
            var ratio = max > 0
                ? Math.Clamp(value / (float)max, 0f, 1f)
                : key == "experience" ? 1f : 0f;

            PanelGfx.Right(label, 551f, y + 6f, 18, Gold, _content);
            PanelGfx.Image(context.Texture, context.Asset("character-gauge"), 565f, y, 243f, 37f, _content);
            PanelGfx.Centered(max > 0 ? $"{value} / {max}" : "已满级", 687f - 116f, y - 18f, 232f, 17, Gold, _content);

            var width = 223f * ratio;
            if (width > 0f)
            {
                var clip = new Control
                {
                    Position = new Vector2(575f, y + 12f),
                    Size = new Vector2(width, 13f),
                    ClipContents = true,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                _content.AddChild(clip);
                PanelGfx.Image(context.Texture, context.Asset($"character-{key}-fill"),
                    0f, 0f, 223f, 13f, clip);
            }

            PanelGfx.Right($"{(int)Math.Floor(ratio * 100)}%", 863f, y + 8f, 16, Gold, _content, 60f);
        }
    }

    private void DrawEquipment(PartyPanelContext context, string id, MemberView member)
    {
        var bag = context.Party.Bag;
        var equipment = bag.EquipmentFor(id);

        PanelGfx.Image(context.Texture, PortraitOf(id), 623f, 124f, 127f, 114f, _content);

        foreach (var slot in context.Panels.EquipmentSlots)
        {
            var instanceId = equipment.GetValueOrDefault(slot.Id);
            var entry = instanceId is null ? null : bag.Get(instanceId);
            var item = entry is null ? null : bag.Catalog.Items.GetValueOrDefault(entry.ItemId);

            var x = 482f + slot.X;
            var button = PanelGfx.Hit(x, slot.Y, 74f, 74f,
                () => context.Inspect?.Invoke(id, "bag"), _content);
            PanelGfx.Image(context.Texture, context.Asset("inventory-slot"), 0f, 0f, 74f, 74f, button);
            PanelGfx.Image(context.Texture, item?.Icon ?? slot.EmptyIcon,
                item is null ? 13f : 5f, item is null ? 13f : 5f,
                item is null ? 48f : 64f, item is null ? 48f : 64f, button);
        }

        TextButton(context, PanelGfx.Hit(568f, 374f, 230f, 44f,
            () => context.Inspect?.Invoke(id, "bag"), _content), "更换装备", 230f, 44f);

        PanelGfx.Centered($"Lv. {member.Level}", 686f - 175f, 331f, 350f, 20, Gold, _content);
    }

    private void DrawSkills(PartyPanelContext context, string id, MemberView member)
    {
        var character = context.Catalog.Class(member.ClassId);
        if (character is null) return;

        var configuration = id == "player" ? context.PlayerSkills : member.Companion!.Skills;
        var loadout = configuration.Draft(context.Catalog, member.ClassId);
        var companion = id == "player" ? null : member.Companion!.Snapshot();

        var scroll = NewScroll("Skills", 503f, 18f, 374f, 342f, _content);

        var content = new Control
        {
            CustomMinimumSize = new Vector2(374f, 554f),
            Size = new Vector2(374f, 554f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(content);

        for (var index = 0; index < context.Panels.SkillColumns.Length; index += 1)
        {
            var column = context.Panels.SkillColumns[index];
            PanelGfx.Image(context.Texture, column.Image,
                index * 91f, 0f, 72f, 17f, content);
        }

        var skillIds = character.SkillIds;
        for (var index = 0; index < skillIds.Length && index < context.Panels.SkillPositions.Length; index += 1)
        {
            var skillId = skillIds[index];
            if (!context.Catalog.Skills.TryGetValue(skillId, out var skill)) continue;

            var position = context.Panels.SkillPositions[index];
            var level = loadout.LevelOf(skillId);
            var available = companion is null || companion.SkillCaps.ContainsKey(skillId);
            var x = position.Column * 91f;
            var y = 20f + position.Row * 89f;

            var target = skillId;
            var button = PanelGfx.Hit(x, y, 75f, 75f,
                () =>
                {
                    _modalState = new PartyModal { Kind = "skill", Id = id, Skill = target };
                    Render();
                }, content);
            PanelGfx.Image(context.Texture,
                available ? skill.Icon : context.Asset("party-skill-unavailable"),
                0f, 0f, 75f, 75f, button);
            PanelGfx.Image(context.Texture, context.Asset("skill-slot"), -9f, -9f, 93f, 93f, button);

            if (available && level > 0)
            {
                PanelGfx.Image(context.Texture, context.Asset("skill-rank"), 57f, 51f, 24f, 25f, button);
                PanelGfx.Centered(level.ToString(), 69f - 40f, 54f, 80f, 18, Gold, button);
            }
            else if (available)
            {
                PanelGfx.Image(context.Texture, context.Asset("skill-locked"), 0f, 0f, 75f, 75f, button);
            }
        }

        if (companion is { Source: "mercenary" })
        {
            PanelGfx.Centered("佣兵团", 309f - 80f, 0f, 160f, 17, Gold, content);
            for (var index = 0; index < 2; index += 1)
            {
                var group = index < companion.GroupSkills.Count ? companion.GroupSkills[index] : null;
                var skill = group is not null &&
                            context.Party.Catalog.GroupSkills.TryGetValue(group, out var entry)
                    ? entry
                    : null;

                var target = group;
                var button = PanelGfx.Hit(273f, 20f + index * 178f, 75f, 75f,
                    () =>
                    {
                        if (target is null) return;
                        _modalState = new PartyModal { Kind = "group", Id = id, Group = target };
                        Render();
                    }, content, disabled: skill is null);
                PanelGfx.Image(context.Texture,
                    skill?.Icon ?? context.Asset("party-skill-unavailable"),
                    0f, 0f, 75f, 75f, button);
                PanelGfx.Image(context.Texture, context.Asset("skill-slot"), -9f, -9f, 93f, 93f, button);
            }
        }

        scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _gridScroll);
        scroll.GetVScrollBar().ValueChanged += value => _gridScroll = (int)value;

        TextButton(context, PanelGfx.Hit(537f, 374f, 305f, 44f,
            () => context.Inspect?.Invoke(id, "skills"), _content), "技能与自动战斗设置", 305f, 44f);
    }

    private void DrawModal(PartyPanelContext context)
    {
        var modal = _modalState!;
        if (MemberOf(modal.Id) is null)
        {
            _modalState = null;
            return;
        }

        var host = NewModal(718f, 415f);

        // 弹层开着时整页按钮不该还能点。
        var shield = new Control
        {
            Name = "Shield",
            Position = -new Vector2((DesignWidth - 718f) / 2f, (DesignHeight - 415f) / 2f),
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        host.AddChild(shield);

        PanelGfx.Fill(0f, 0f, 718f, 415f, ModalBack, host);
        PanelGfx.Border(context.Texture, context.Asset, 0f, 0f, 718f, 415f, host);

        switch (modal.Kind)
        {
            case "dismiss":
                DrawDismissModal(context, modal, host);
                break;
            case "group":
                DrawGroupModal(context, modal, host);
                break;
            default:
                DrawSkillModal(context, modal, host);
                break;
        }
    }

    private void DrawDismissModal(PartyPanelContext context, PartyModal modal, Control host)
    {
        PanelGfx.Image(context.Texture, PortraitOf(modal.Id), 38f, 66f, 127f, 114f, host);
        PanelGfx.Text($"确定放逐 {NameOf(modal.Id)}？", 196f, 70f, 25, Gold, host);
        PanelGfx.Text("放逐后，该佣兵和其穿戴的装备将一起离开。", 196f, 132f, 23, Gold, host);
        if (modal.Error.Length > 0)
            PanelGfx.Text(modal.Error, 35f, 279f, 22, ErrorText, host);

        TextButton(context, PanelGfx.Hit(270f, 350f, 170f, 48f, CancelModal, host), "取消", 170f, 48f);
        TextButton(context, PanelGfx.Hit(480f, 350f, 190f, 48f, () =>
        {
            _modalState = null;
            try
            {
                context.Party.Dismiss(modal.Id);
                _selected = null;
            }
            catch (Exception error)
            {
                modal.Error = error.Message;
                _modalState = modal;
            }
            Render();
        }, host), "确定", 190f, 48f);
    }

    private void DrawGroupModal(PartyPanelContext context, PartyModal modal, Control host)
    {
        var skill = context.Party.Catalog.GroupSkills.GetValueOrDefault(modal.Group ?? string.Empty);
        if (skill is null)
        {
            CancelModal();
            return;
        }

        PanelGfx.Image(context.Texture, skill.Icon, 35f, 25f, 75f, 75f, host);
        WrappedText(skill.Name, 135f, 30f, 550f, 26, Gold, host);
        WrappedText("佣兵团技能", 135f, 70f, 550f, 21, Gold, host);

        var scroll = NewScroll("GroupSkill", 35f, 124f, 648f, 197f, host);

        var content = new Control { MouseFilter = MouseFilterEnum.Ignore };
        scroll.AddChild(content);
        var description = WrappedText(skill.Description, 0f, 0f, 632f, 22, Gold, content);
        var effect = WrappedText("该佣兵留在名册中即可生效。相同的佣兵团技能只生效一次。",
            0f, 0f, 632f, 21, Gold, content);
        effect.Position = new Vector2(0f, description.GetMinimumSize().Y + 18f);
        content.CustomMinimumSize = new Vector2(648f, effect.Position.Y + effect.GetMinimumSize().Y);

        TextButton(context, PanelGfx.Hit(480f, 350f, 190f, 48f, CancelModal, host), "关闭", 190f, 48f);
    }

    private void DrawSkillModal(PartyPanelContext context, PartyModal modal, Control host)
    {
        var member = MemberOf(modal.Id)!;
        var skill = context.Catalog.SkillForGrowth(modal.Skill, modal.Id != "player");
        var configuration = modal.Id == "player" ? context.PlayerSkills : member.Companion!.Skills;
        var level = configuration.Draft(context.Catalog, member.ClassId).LevelOf(skill.Id);
        var cap = modal.Id == "player"
            ? skill.NormalMaxLevel
            : member.Companion!.Snapshot().SkillCaps.GetValueOrDefault(skill.Id);

        PanelGfx.Image(context.Texture, skill.Icon, 35f, 25f, 75f, 75f, host);
        WrappedText(skill.Name, 135f, 30f, 550f, 26, Gold, host);
        var status = level > 0
            ? $"等级 {level} / {cap}"
            : cap > 0 ? "尚未习得" : "该佣兵未开放此技能";
        WrappedText(status, 135f, 70f, 550f, 21, Gold, host);

        var scroll = NewScroll("SkillDetail", 35f, 124f, 648f, 197f, host);

        var rows = new List<(string Text, int Size, int Gap)> { (skill.Description, 22, 15) };
        if (level > 0 && level <= skill.Ranks.Count)
            foreach (var entry in SkillDisplay.Entries(skill, skill.Ranks[level - 1]))
                rows.Add(($"{entry.Label}：{entry.Text}", 20, 10));

        var content = new Control
        {
            CustomMinimumSize = new Vector2(648f, 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(content);

        var y = 0f;
        foreach (var (text, size, gap) in rows)
        {
            var label = WrappedText(text, 0f, y, 632f, size, Gold, content);
            y += label.GetMinimumSize().Y + gap;
        }
        content.CustomMinimumSize = new Vector2(648f, y);

        TextButton(context, PanelGfx.Hit(480f, 350f, 190f, 48f, CancelModal, host), "关闭", 190f, 48f);
    }

    private void CancelModal()
    {
        _modalState = null;
        Render();
    }

    private static ScrollContainer NewScroll(string name, float x, float y, float width, float height,
        Control parent)
    {
        var scroll = new ScrollContainer
        {
            Name = name,
            Position = new Vector2(x, y),
            Size = new Vector2(width, height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollBar(scroll);
        parent.AddChild(scroll);
        return scroll;
    }

    private static void HideScrollBar(ScrollContainer scroll)
    {
        var bar = scroll.GetVScrollBar();
        bar.AddThemeStyleboxOverride("scroll", PanelGfx.EmptyStyle());
        bar.AddThemeStyleboxOverride("grabber", PanelGfx.EmptyStyle());
        bar.AddThemeStyleboxOverride("grabber_highlight", PanelGfx.EmptyStyle());
        bar.AddThemeStyleboxOverride("grabber_pressed", PanelGfx.EmptyStyle());
    }

    private static Label WrappedText(string value, float x, float y, float width, int size,
        Color color, Control parent)
    {
        var label = new Label
        {
            Text = value,
            Position = new Vector2(x, y),
            CustomMinimumSize = new Vector2(width, 0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    private MemberView? MemberOf(string id)
    {
        if (_context is not { } context) return null;
        if (id == "player")
        {
            var hero = context.Party.Hero;
            return new MemberView(hero.Level, hero.Panel, hero.Player, hero.ClassId,
                "player", context.PlayerActorId, null);
        }

        var member = context.Party.Get(id);
        return member is null
            ? null
            : new MemberView(member.Level, member.Panel, member.Player, member.ClassId,
                member.Source, member.Definition.Actor, member);
    }

    private string NameOf(string id)
    {
        if (_context is not { } context) return id;
        if (id == "player")
            return context.World.Actors.GetValueOrDefault(context.PlayerActorId)?.Name ?? id;
        return context.Party.Get(id)?.Definition.Name ?? id;
    }

    private string? PortraitOf(string id)
    {
        if (_context is not { } context) return null;
        var member = MemberOf(id);
        if (member is null) return context.Asset("party-empty");
        if (id != "player" && member.Source == "mercenary")
            return context.Asset($"party-class-{member.ClassId}");
        var portrait = context.World.Actors.GetValueOrDefault(member.ActorId)?.Portrait ?? "kiyan";
        return context.Panels.Character.Portraits.GetValueOrDefault(portrait)?.PartyIcon
               ?? context.Asset($"party-class-{member.ClassId}");
    }

    private Control NewModal(float width, float height)
    {
        var modal = new Control
        {
            Name = "Modal",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2((DesignWidth - width) / 2f, (DesignHeight - height) / 2f),
        };
        _canvas.AddChild(modal);
        _modal = modal;
        return modal;
    }

    private void ReleaseModal()
    {
        if (_modal is null) return;
        if (GodotObject.IsInstanceValid(_modal))
        {
            _canvas.RemoveChild(_modal);
            _modal.QueueFree();
        }

        _modal = null;
    }

    private static void TextButton(PartyPanelContext context, Button button, string label,
        float width, float height)
    {
        PanelGfx.ButtonSkin(context.Texture, width, height, button);
        PanelGfx.CenteredAt(label, width / 2f, height / 2f, width - 12f,
            (int)Math.Min(22f, height * .42f), Gold, button);
    }

    public bool DebugTab(string tab)
    {
        if (_context is null || Array.IndexOf(Tabs, tab) < 0) return false;
        _tab = tab;
        _gridScroll = 0;
        Render();
        return _tab == tab;
    }

    public bool DebugDismiss()
    {
        if (_context is null || _selected is null || MemberOf(_selected) is null) return false;
        _modalState = new PartyModal { Kind = "dismiss", Id = _selected };
        Render();
        return _modalState is not null;
    }
}
