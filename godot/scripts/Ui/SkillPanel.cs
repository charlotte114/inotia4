using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record SkillPanelContext(
    PartyMemberView Member,
    StorySkillProgression? HeroProgression,
    SkillCatalog Catalog,
    MenuPanelsCatalog Panels,
    ControlSkinCatalog ControlSkin,
    Func<string, Texture2D?> Texture,
    Bag Bag,
    IReadOnlyList<string> MemberIds,
    Func<string, string?> MemberIconOf,
    PartyRoster? Party = null,
    Action<string>? SelectMember = null,
    Action? Changed = null,
    IReadOnlyDictionary<string, string>? StoryAssets = null,
    Action<string>? ShowPage = null)
{
    public string? Asset(string key) => Panels.Asset(key);

    public string? Skin(string key) => ControlSkin.Asset(key);

    public bool HasParty => Party is not null;

    public string MemberId => Member.Id;

    public bool IsPlayer => Member.IsPlayer;

    public SkillConfiguration Skills => Member.IsPlayer
        ? HeroProgression!
        : Member.Skills!;

    public ShortcutBar Bar() => Member.IsPlayer
        ? HeroProgression!.Bar()
        : ShortcutBar.For(Bag, Member.Owner,
            Member.Skills?.ActiveSkills(Catalog, Member.ClassId) ?? []);
}

public partial class SkillPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color Gold = new("#f3deb0");

    private static readonly Color White = new("#ffffff");

    private static readonly string[] Sections = ["directory", "ai", "settings"];

    private SkillPanelContext? _context;

    private Control _canvas = null!;

    private Control _content = null!;

    private Control _overlay = null!;

    /// <summary>登记屏画在整块 1024×640 画布上，所以单独一层。</summary>
    private Control _screen = null!;

    private Control _tabs = null!;

    private string _section = "directory";

    private string? _skill;

    private int _gridScroll;

    private int _descriptionScroll;

    private ShortcutEdit? _shortcutEdit;

    private bool _skillResetConfirm;

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

        _overlay = new Control
        {
            Name = "Overlay",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_overlay);

        _screen = new Control
        {
            Name = "Screen",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_screen);

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

    public const string Page = "skills";

    public void Open(SkillPanelContext context)
    {
        _context = context;
        _section = "directory";
        _skill = null;
        _gridScroll = 0;
        _descriptionScroll = 0;
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void Close()
    {
        Visible = false;
        _skill = null;
        _shortcutEdit = null;
    }

    public void Render()
    {
        if (_context is not { } context) return;

        PanelGfx.Clear(_content);
        PanelGfx.Clear(_overlay);
        PanelGfx.Clear(_screen);
        PanelGfx.Clear(_tabs);

        if (_shortcutEdit is not null)
        {
            DrawShortcutScreen(context);
            return;
        }

        if (context.StoryAssets is { } assets) MenuTabBar.Draw(_tabs, assets, context.Texture, Page, context.ShowPage);

        DrawFrames(context);
        DrawPartyColumn(context);
        if (_section == "directory") DrawDirectory(context);
        else if (_section == "ai") DrawAi(context);
        else if (_section == "settings") DrawSettings(context);
        DrawTabs(context);
        DrawHeader(context);
        DrawAside(context);
        DrawSkillResetConfirm(context);
    }

    private void DrawSkillResetConfirm(SkillPanelContext context)
    {
        if (!_skillResetConfirm) return;

        var labels = context.Panels.SkillPanel.Labels;
        PanelGfx.NativeMessage(context.Texture, context.Asset,
            labels.ResetButton,
            () =>
            {
                _skillResetConfirm = false;
                try
                {
                    context.HeroProgression!.Reset();
                    context.Changed?.Invoke();
                }
                catch (Exception error)
                {
                    GD.PushWarning($"[skill] 初始化技能点失败：{error.Message}");
                }

                Render();
            },
            () =>
            {
                _skillResetConfirm = false;
                Render();
            },
            _overlay);
    }

    public bool Escape()
    {
        if (_skillResetConfirm)
        {
            _skillResetConfirm = false;
            Render();
            return true;
        }

        if (_shortcutEdit is null) return false;
        _shortcutEdit = null;
        Render();
        return true;
    }

    private void DrawFrames(SkillPanelContext context)
    {
        PanelGfx.Border(context.Texture, context.Asset,
            SkillLayouts.LeftFrame.X, SkillLayouts.LeftFrame.Y,
            SkillLayouts.LeftFrame.Width, SkillLayouts.LeftFrame.Height, _content);

        PanelGfx.SolidBorder(context.Texture, context.Asset,
            SkillLayouts.RightTitle.X, SkillLayouts.RightTitle.Y,
            SkillLayouts.RightTitle.Width, SkillLayouts.RightTitle.Height, _content);
        PanelGfx.Border(context.Texture, context.Asset,
            SkillLayouts.RightFrame.X, SkillLayouts.RightFrame.Y,
            SkillLayouts.RightFrame.Width, SkillLayouts.RightFrame.Height, _content);
    }

    private void DrawPartyColumn(SkillPanelContext context)
    {
        MenuMemberColumn.Draw(_content, context.Texture, context.Asset, context.MemberIds,
            context.MemberIconOf, context.MemberId, context.SelectMember);
    }

    private void DrawDirectory(SkillPanelContext context)
    {
        var configuration = context.Skills;
        var skillIds = context.Catalog.SkillsOf(context.Member.ClassId);
        var draft = configuration.Draft(context.Catalog, context.Member.ClassId);
        var positions = context.Panels.SkillPositions;
        var bleed = SkillLayouts.ScrollBleed;

        var scroll = new ScrollContainer
        {
            Name = "Skills",
            Position = new Vector2(SkillLayouts.ScrollX - bleed, SkillLayouts.ScrollY - bleed),
            Size = new Vector2(SkillLayouts.ScrollWidth + bleed * 2f, SkillLayouts.ScrollHeight + bleed * 2f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollBar(scroll);
        _content.AddChild(scroll);

        var height = SkillLayouts.ContentHeight(positions.Select(value => new SkillPosition(value.Column, value.Row)))
                     + bleed * 2f;
        var content = new Control
        {
            CustomMinimumSize = new Vector2(SkillLayouts.ScrollWidth + bleed * 2f, height),
            Size = new Vector2(SkillLayouts.ScrollWidth + bleed * 2f, height),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(content);

        for (var index = 0; index < context.Panels.SkillColumns.Length; index += 1)
        {
            var column = context.Panels.SkillColumns[index];
            PanelGfx.Image(context.Texture, column.Image,
                bleed + 1f + index * SkillLayouts.CellColumnStride, bleed,
                SkillLayouts.ColumnImageWidth, SkillLayouts.ColumnImageHeight, content);
        }

        for (var index = 0; index < skillIds.Count && index < positions.Length; index += 1)
        {
            var id = skillIds[index];
            if (!context.Catalog.Skills.TryGetValue(id, out var skill)) continue;

            var position = positions[index];
            var level = draft.LevelOf(id);
            var x = bleed + position.Column * SkillLayouts.CellColumnStride;
            var y = bleed + SkillLayouts.CellTop + position.Row * SkillLayouts.CellRowStride;

            PanelGfx.Image(context.Texture, context.Asset("skill-slot"),
                x + SkillLayouts.SlotRingInset, y + SkillLayouts.SlotRingInset,
                SkillLayouts.SlotRingSize, SkillLayouts.SlotRingSize, content);
            PanelGfx.Image(context.Texture, skill.Icon, x, y, SkillLayouts.CellSize, SkillLayouts.CellSize, content);

            if (level <= 0)
            {
                PanelGfx.Image(context.Texture, context.Asset("skill-locked"),
                    x, y, SkillLayouts.CellSize, SkillLayouts.CellSize, content);
            }
            else
            {
                PanelGfx.Image(context.Texture, context.Asset("skill-rank"),
                    x + SkillLayouts.RankPlateX, y + SkillLayouts.RankPlateY,
                    SkillLayouts.RankPlateWidth, SkillLayouts.RankPlateHeight, content);
                PanelGfx.Image(context.Texture, context.Asset($"skill-rank-{level}"),
                    x + SkillLayouts.RankDigitX, y + SkillLayouts.RankDigitY,
                    SkillLayouts.RankDigitWidth, SkillLayouts.RankDigitHeight, content);
            }

            if (id == SelectedSkill(context))
            {
                PanelGfx.Image(context.Texture, context.Asset(level > 0 ? "skill-selected" : "skill-selected-ai"),
                    x + SkillLayouts.SelectionInset, y + SkillLayouts.SelectionInset,
                    SkillLayouts.SelectionWidth, SkillLayouts.SelectionHeight, content);
            }

            var target = id;
            PanelGfx.Hit(x, y, SkillLayouts.CellSize, SkillLayouts.CellSize, () => Select(target), content);
        }

        scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _gridScroll);
        scroll.GetVScrollBar().ValueChanged += value => _gridScroll = (int)value;
    }

    private void DrawAi(SkillPanelContext context)
    {
        var party = context.Party;
        if (party is null) return;

        var configuration = context.Skills;
        var skillIds = context.Catalog.SkillsOf(context.Member.ClassId);
        var draft = configuration.Draft(context.Catalog, context.Member.ClassId);
        var positions = context.Panels.SkillPositions;
        var bleed = SkillLayouts.ScrollBleed;

        var scroll = new ScrollContainer
        {
            Name = "Skills",
            Position = new Vector2(SkillLayouts.ScrollX - bleed, SkillLayouts.ScrollY - bleed),
            Size = new Vector2(SkillLayouts.ScrollWidth + bleed * 2f, SkillLayouts.ScrollHeight + bleed * 2f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollBar(scroll);
        _content.AddChild(scroll);

        var height = Math.Max(SkillLayouts.CellTop + 5f * SkillLayouts.CellRowStride + SkillLayouts.CellSize,
            SkillLayouts.CellTop + 2f * 178f + SkillLayouts.CellSize) + bleed * 2f;
        var content = new Control
        {
            CustomMinimumSize = new Vector2(SkillLayouts.ScrollWidth + bleed * 2f, height),
            Size = new Vector2(SkillLayouts.ScrollWidth + bleed * 2f, height),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(content);

        for (var index = 0; index < Math.Min(2, context.Panels.SkillColumns.Length); index += 1)
        {
            var column = context.Panels.SkillColumns[index];
            PanelGfx.Image(context.Texture, column.Image,
                bleed + 1f + index * SkillLayouts.AiCellColumnStride, bleed,
                SkillLayouts.ColumnImageWidth, SkillLayouts.ColumnImageHeight, content);
        }

        PanelGfx.Fill(SkillLayouts.AiBandLeft.X + bleed, SkillLayouts.AiBandLeft.Y + bleed,
            SkillLayouts.AiBandLeft.Width, SkillLayouts.AiBandLeft.Height,
            new Color(111f / 255f, 111f / 255f, 111f / 255f, 30f / 255f), content);
        PanelGfx.Fill(SkillLayouts.AiBandRight.X + bleed, SkillLayouts.AiBandRight.Y + bleed,
            SkillLayouts.AiBandRight.Width, SkillLayouts.AiBandRight.Height,
            new Color(111f / 255f, 111f / 255f, 111f / 255f, 30f / 255f), content);

        var frequencies = party.AiFrequency(context.MemberId);
        for (var index = 0; index < skillIds.Count && index < positions.Length; index += 1)
        {
            var id = skillIds[index];
            if (!context.Catalog.Skills.TryGetValue(id, out var skill)) continue;

            var position = positions[index];
            var level = draft.LevelOf(id);
            var x = bleed + position.Column * SkillLayouts.AiCellColumnStride;
            var y = bleed + SkillLayouts.CellTop + position.Row * SkillLayouts.CellRowStride;

            var cell = PanelGfx.Hit(x, y, SkillLayouts.CellSize, SkillLayouts.CellSize, () => Select(id), content);
            PanelGfx.Image(context.Texture, level > 0 ? skill.Icon : context.Asset("skill-locked"),
                0f, 0f, SkillLayouts.CellSize, SkillLayouts.CellSize, cell);
            PanelGfx.Image(context.Texture, context.Asset("skill-slot"),
                SkillLayouts.SlotRingInset, SkillLayouts.SlotRingInset,
                SkillLayouts.SlotRingSize, SkillLayouts.SlotRingSize, cell);
            if (id == SelectedSkill(context))
                PanelGfx.Image(context.Texture, context.Asset("skill-selected-ai"),
                    SkillLayouts.SelectionInset, SkillLayouts.SelectionInset,
                    SkillLayouts.SelectionWidth, SkillLayouts.SelectionHeight, cell);

            if (level <= 0) continue;

            var frequency = frequencies.GetValueOrDefault(id, 1);
            var target = id;
            var button = PanelGfx.Hit(x + SkillLayouts.AiFrequencyOffsetX, y + SkillLayouts.AiFrequencyOffsetY,
                SkillLayouts.AiFrequencyWidth, SkillLayouts.AiFrequencyHeight,
                () =>
                {
                    party.SetAiFrequency(context.MemberId, target, (frequency + 1) % 3);
                    context.Changed?.Invoke();
                    Render();
                }, content);
            PanelGfx.Image(context.Texture, context.Asset(SkillLevelAssets[frequency]),
                0f, 0f, SkillLayouts.AiFrequencyWidth, SkillLayouts.AiFrequencyHeight, button);
        }

        scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _gridScroll);
        scroll.GetVScrollBar().ValueChanged += value => _gridScroll = (int)value;
    }

    private void DrawSettings(SkillPanelContext context)
    {
        var party = context.Party;
        if (party is null) return;

        var labels = context.Panels.SkillPanel.Labels;
        PanelGfx.Centered(labels.CounterattackTitle, SkillLayouts.SettingsTitleX - 120f,
            SkillLayouts.SettingsTitleY, 240f, SkillLayouts.SettingsTitleSize, Gold, _content);

        var mode = party.Counterattack(context.MemberId);
        for (var level = 0; level < 3; level += 1)
        {
            var target = level;
            var button = PanelGfx.Hit(SkillLayouts.SettingsButtonX + level * SkillLayouts.SettingsButtonStride,
                SkillLayouts.SettingsButtonTop, SkillLayouts.SettingsButtonWidth, SkillLayouts.SettingsButtonHeight,
                () =>
                {
                    party.SetCounterattack(context.MemberId, target);
                    context.Changed?.Invoke();
                    Render();
                }, _content);
            PanelGfx.Image(context.Texture, context.Asset(SkillLevelAssets[level]),
                0f, 0f, SkillLayouts.SettingsButtonWidth, SkillLayouts.SettingsButtonHeight, button);
            button.Modulate = new Color(1f, 1f, 1f, mode == level ? 1f : .55f);
        }

        if (!context.IsPlayer) return;   // 不是主角就没有「技能点初始化」这一项
        PanelGfx.Hit(SkillLayouts.ResetButtonX, SkillLayouts.ResetButtonY,
            SkillLayouts.ResetButtonWidth, SkillLayouts.ResetButtonHeight,
            () =>
            {
                _skillResetConfirm = true;
                Render();
            }, _content);
        PanelGfx.Text(labels.ResetButton, SkillLayouts.ResetButtonX + 24f, SkillLayouts.ResetButtonY + 18f,
            20, Gold, _content);
    }

    private static readonly string[] SkillLevelAssets = ["skill-level-none", "skill-level-normal", "skill-level-high"];

    private void DrawTabs(SkillPanelContext context)
    {
        for (var index = 0; index < Sections.Length; index += 1)
        {
            var section = Sections[index];
            var disabled = section != "directory" && !context.HasParty;
            var button = PanelGfx.Hit(SkillLayouts.TabX, SkillLayouts.TabTop + index * SkillLayouts.TabStride,
                SkillLayouts.TabWidth, SkillLayouts.TabHeight, () =>
                {
                    if (_section == section) return;
                    _section = section;
                    _descriptionScroll = 0;
                    Render();
                }, _content, disabled);
            PanelGfx.Image(context.Texture, context.Asset($"skill-tab-{section}"),
                0f, 0f, SkillLayouts.TabWidth, SkillLayouts.TabHeight, button);
            if (disabled) button.Modulate = new Color(1f, 1f, 1f, SkillLayouts.DisabledAlpha);
        }
    }

    private void DrawHeader(SkillPanelContext context)
    {
        var label = context.IsPlayer
            ? $"{context.Panels.Character.Labels.SkillPoints} {context.HeroProgression?.Points ?? 0}"
            : context.Member.Source == "story" ? "伙伴技能" : "佣兵技能";
        PanelGfx.Text(label, SkillLayouts.HeaderX, SkillLayouts.HeaderY, SkillLayouts.HeaderSize, Gold, _content);
    }

    private void DrawAside(SkillPanelContext context)
    {
        if (_section == "settings") return;

        var configuration = context.Skills;
        var skillIds = context.Catalog.SkillsOf(context.Member.ClassId);
        if (skillIds.Count == 0) return;

        var skill = context.Catalog.SkillForGrowth(SelectedSkill(context)!, !context.IsPlayer);
        var level = configuration.Draft(context.Catalog, context.Member.ClassId).LevelOf(skill.Id);
        var maximum = context.IsPlayer
            ? skill.NormalMaxLevel
            : context.Member.Companion?.Snapshot().SkillCaps.GetValueOrDefault(skill.Id) ?? 0;
        if (_section == "ai" && level == 0) return;
        var height = _section == "ai" ? SkillLayouts.DescriptionAiHeight : SkillLayouts.DescriptionHeight;

        var scroll = new ScrollContainer
        {
            Name = "Description",
            Position = new Vector2(SkillLayouts.DescriptionX, SkillLayouts.DescriptionY),
            Size = new Vector2(SkillLayouts.DescriptionWidth, height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollBar(scroll);
        _content.AddChild(scroll);

        var column = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(SkillLayouts.DescriptionWidth, 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        column.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(column);

        AppendRich(column, SkillDetailLines.DescriptionParts(skill, level,
            context.IsPlayer ? null : maximum));

        var previewHeight = 0;
        if (_section == "directory" && level < maximum && level < skill.Ranks.Count)
        {
            var separator = new Control
            {
                CustomMinimumSize = new Vector2(SkillLayouts.DescriptionWidth, SkillLayouts.SeparatorAdvance),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            column.AddChild(separator);
            DrawSeparator(separator);

            AppendRich(column, SkillDetailLines.LearningParts(skill.Name, level,
                context.IsPlayer ? context.HeroProgression?.LearningRequirements(skill.Id) : null,
                context.IsPlayer ? null
                    : level < skill.Ranks.Count ? skill.Ranks[level].RequiredCharacterLevel : null));

            var gap = new Control
            {
                CustomMinimumSize = new Vector2(SkillLayouts.DescriptionWidth, 12f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            column.AddChild(gap);

            var rows = SkillDetailLines.NextLevelPreview(skill.Ranks, level, skill.Kind, maximum, skill.Display);
            previewHeight = (int)(rows.Count * SkillLayouts.PreviewRowHeight);
            var previews = new Control
            {
                CustomMinimumSize = new Vector2(SkillLayouts.DescriptionWidth, previewHeight),
                Size = new Vector2(SkillLayouts.DescriptionWidth, previewHeight),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            column.AddChild(previews);
            DrawPreviews(context, previews, rows);
        }

        scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _descriptionScroll);
        scroll.GetVScrollBar().ValueChanged += value => _descriptionScroll = (int)value;

        if (_section == "directory") DrawMenu(context, skill, level);
    }

    private void DrawPreviews(SkillPanelContext context, Control parent, List<SkillPreviewRow> rows)
    {
        var center = SkillLayouts.DescriptionWidth / 2f;
        for (var index = 0; index < rows.Count; index += 1)
        {
            var row = rows[index];
            var y = index * SkillLayouts.PreviewRowHeight;
            var labelColor = new Color(row.IsMana ? SkillLayouts.ManaColor : SkillLayouts.TextColor);
            var valueColor = new Color(row.IsMana ? SkillLayouts.ManaColor : SkillLayouts.EffectValueColor);

            PanelGfx.CenteredAt(row.Label, center, y, SkillLayouts.DescriptionWidth,
                SkillLayouts.PreviewSize, labelColor, parent);

            var valueY = y + SkillLayouts.PreviewValueOffset;
            var from = PanelGfx.Text(row.From, center - 12f - 140f, valueY, SkillLayouts.PreviewSize, valueColor, parent);
            from.Size = new Vector2(140f, SkillLayouts.PreviewSize + 6f);
            from.HorizontalAlignment = HorizontalAlignment.Right;

            PanelGfx.Image(context.Texture, context.Asset("skill-compare-arrow"),
                center + SkillLayouts.PreviewArrowOffsetX, valueY + SkillLayouts.PreviewArrowOffsetY,
                SkillLayouts.PreviewArrowWidth, SkillLayouts.PreviewArrowHeight, parent);

            PanelGfx.Text(row.To, center + 12f, valueY, SkillLayouts.PreviewSize, valueColor, parent);
        }
    }

    private static void DrawSeparator(Control parent)
    {
        var color = new Color("#8caace");
        for (var offset = 0f; offset < SkillLayouts.DescriptionWidth; offset += SkillLayouts.SeparatorStride)
        {
            var dash = Math.Min(SkillLayouts.SeparatorDash, SkillLayouts.DescriptionWidth - offset);
            PanelGfx.Fill(offset, SkillLayouts.SeparatorGap, dash, 1f, color, parent);
        }
    }

    /// <summary>
    /// 说明区底部的菜单行：学习（立即扣点）、快捷键／取消快捷键。三张图里的字是印在图上的，
    /// 所以按钮不叠文字，标签只留给无障碍与调试。命中区 125×55，原图 149×75 落在 (−12,−9)。
    /// </summary>
    private void DrawMenu(SkillPanelContext context, SkillDefinition skill, int level)
    {
        var actions = new List<(string Image, bool Disabled, Action Run)>();

        if (context.IsPlayer && context.HeroProgression is { } progression)
        {
            var problem = progression.LearningProblem(skill.Id);
            actions.Add(("skill-learn", problem is not null, () =>
            {
                try
                {
                    progression.Learn(skill.Id);
                }
                catch (Exception error)
                {
                    GD.PushWarning($"技能学习失败：{skill.Id} — {error.Message}");
                    return;
                }

                context.Changed?.Invoke();
                Render();
            }));
        }

        if (skill.IsActive)
        {
            var bar = context.Bar();
            var assigned = level > 0 && bar.Assigned(ShortcutEntry.SkillKind, skill.Id);
            actions.Add((assigned ? "skill-shortcut-cancel" : "skill-shortcut", level == 0, () =>
            {
                if (assigned)
                {
                    bar.ClearObject(ShortcutEntry.SkillKind, skill.Id);
                    context.Changed?.Invoke();
                    Render();
                    return;
                }

                _shortcutEdit = new ShortcutEdit(ShortcutEntry.SkillKind, skill.Id, 0);
                Render();
            }));
        }

        var menu = SkillLayouts.Menu;
        var offset = menu.Offsets.GetValueOrDefault(Math.Min(actions.Count, 3));
        var rowX = SkillLayouts.RightTitle.X + menu.X + offset;
        var rowY = menu.Y;

        for (var index = 0; index < actions.Count; index += 1)
        {
            var (image, disabled, run) = actions[index];
            var x = rowX + index * menu.ButtonStride;
            var button = PanelGfx.Hit(x, rowY, menu.ButtonWidth, menu.ButtonHeight, run, _overlay, disabled);

            PanelGfx.Image(context.Texture, context.Asset(image),
                SkillLayouts.MenuImageOffsetX, SkillLayouts.MenuImageOffsetY,
                SkillLayouts.MenuImageWidth, SkillLayouts.MenuImageHeight, button);

            var pressed = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            button.AddChild(pressed);
            PanelGfx.Image(context.Texture, context.Asset("skill-button-pressed"),
                SkillLayouts.MenuImageOffsetX, SkillLayouts.MenuImageOffsetY,
                SkillLayouts.MenuImageWidth, SkillLayouts.MenuImageHeight, pressed);
            button.ButtonDown += () => pressed.Visible = true;
            button.ButtonUp += () => pressed.Visible = false;
            button.MouseExited += () => pressed.Visible = false;

            if (disabled) button.Modulate = new Color(1f, 1f, 1f, SkillLayouts.DisabledAlpha);
        }
    }

    /// <summary>
    /// 登记快捷键这一屏：
    /// 菜单整块让位，露出压暗的画面；顶部一行提示，右侧一排出与游戏 HUD 同位的六个圆槽
    /// 与换组钮，待登记的对象画在屏幕中间偏右的圆框里，左上角一枚圆形关闭钮。
    ///
    /// 画法与背包页那处入口共用 <see cref="ShortcutScreen"/>；槽位读 <see cref="ControlLayouts"/> 的默认键位，
    /// 因为 Godot 侧还没有操作区 HUD，这一排只在这一屏出现，位置与默认布局一致。
    /// </summary>
    private void DrawShortcutScreen(SkillPanelContext context)
    {
        if (_shortcutEdit is not { } edit) return;

        ShortcutScreen.Draw(_screen, _canvas.Scale.X, _canvas.Position, GetViewportRect().Size,
            ScreenContext(context), edit,
            AssignShortcut,
            () =>
            {
                _shortcutEdit = edit with { Page = (edit.Page + 1) % ShortcutLayout.PageCount };
                Render();
            },
            () =>
            {
                _shortcutEdit = null;
                Render();
            });
    }

    private static ShortcutScreenContext ScreenContext(SkillPanelContext context) =>
        new(context.Bar(), context.Catalog, context.Panels, context.ControlSkin, context.Texture);

    private void AssignShortcut(int slot)
    {
        if (_context is not { } context || _shortcutEdit is not { } edit) return;

        var bar = context.Bar();
        var current = bar.Page(edit.Page)[slot];
        var same = edit.Kind == ShortcutEntry.SkillKind && current is { Kind: ShortcutEntry.SkillKind } &&
                   current.Id == edit.Id;
        bar.Assign(edit.Page, slot, same ? null : new ShortcutEntry(edit.Kind, edit.Id));
        context.Changed?.Invoke();
        Render();
    }

    private string? SelectedSkill(SkillPanelContext context)
    {
        var ids = context.Catalog.SkillsOf(context.Member.ClassId);
        if (ids.Count == 0) return null;
        if (_skill is { } current && ids.Contains(current)) return current;
        _skill = ids[0];
        return _skill;
    }

    private void Select(string id)
    {
        if (_skill == id) return;
        _skill = id;
        _descriptionScroll = 0;
        Render();
    }

    private static void AppendRich(Container parent, List<DescriptionTextPart> parts)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            FitContent = true,
            CustomMinimumSize = new Vector2(SkillLayouts.DescriptionWidth, 0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = Bbcode(parts),
        };
        label.AddThemeFontSizeOverride("normal_font_size", SkillLayouts.DescriptionSize);
        label.AddThemeColorOverride("default_color", White);
        label.AddThemeConstantOverride("line_separation", SkillLayouts.DescriptionLineSeparation);
        parent.AddChild(label);
    }

    private static string Bbcode(IEnumerable<DescriptionTextPart> parts) =>
        string.Concat(parts.Select(part => part.Color is null
            ? Escape(part.Text)
            : $"[color={part.Color}]{Escape(part.Text)}[/color]"));

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private static void HideScrollBar(ScrollContainer scroll)
    {
        var bar = scroll.GetVScrollBar();
        foreach (var state in new[] { "scroll", "grabber", "grabber_highlight", "grabber_pressed" })
            bar.AddThemeStyleboxOverride(state, PanelGfx.EmptyStyle());
    }

    public bool SelectById(string id)
    {
        if (_context?.Catalog.Skills.ContainsKey(id) != true) return false;
        _skill = id;
        _descriptionScroll = 0;
        Render();
        return true;
    }

    public void OpenShortcutScreen(string skillId)
    {
        _shortcutEdit = new ShortcutEdit(ShortcutEntry.SkillKind, skillId, 0);
        Render();
    }

    public bool DebugAssign(int slot)
    {
        if (_shortcutEdit is not { } edit || _context is not { } context) return false;
        AssignShortcut(slot);
        var row = context.Bar().Page(edit.Page);
        GD.Print($"[skill] 第 {slot + 1} 格 → {(row[slot] is { } entry ? $"{entry.Kind}:{entry.Id}" : "空")}");
        return true;
    }

    public string? SelectedSkillId => _context is { } context ? SelectedSkill(context) : null;

    public void ScrollDescription(int offset)
    {
        _descriptionScroll = Math.Max(0, offset);
        Render();
    }

    public bool LearnSelected()
    {
        if (_context is not { } context || SelectedSkill(context) is not { } id) return false;
        try
        {
            context.HeroProgression!.Learn(id);
        }
        catch (Exception error)
        {
            GD.Print($"[skill] 学习被拒：{error.Message}");
            return false;
        }

        context.Changed?.Invoke();
        Render();
        return true;
    }
}
