using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Inotia4.Application;
using Inotia4.Domain;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;
using Inotia4.Domain.Story;
using Inotia4.Domain.Ui;
using Inotia4.Domain.World;
using Inotia4.Rendering;
using Inotia4.Ui;

namespace Inotia4.Scenes;

public partial class WorldScene : Node2D
{
    private const float PlayerSpeed = 8f * 1000f / 60f;

    private const float CameraZoom = 3f;

    private const float SaveIconSize = 75f;

    private const float HudIconGap = 14f;

    private const string SaveIconPath = "assets/ui/story/save.png";

    private const string SettingsIconPath = "assets/ui/story/tab-system.png";

    private const string StartMapId = "shadow-camp";

    private const string FallbackClassId = "dark-knight";

    private string _classId = FallbackClassId;

    private static readonly Vec2 StartPosition = new(120f, 312f);

    private const float FootRadius = 8f;

    private const float ExitLockRadius = 24f;

    private ContentLoader _loader = null!;
    private TextureCache _textures = null!;

    /// <summary>主角专用租约。主角会被 reparent，所以它的贴图不能挂在某张地图的租约上。</summary>
    private TextureLease _heroLease = null!;

    private TextureLease? _mapLease;

    private MapDefinition _map = null!;
    private CollisionGrid _grid = null!;
    private Node2D _mapRoot = null!;
    private ActorNode _player = null!;

    private OverheadStatus? _overhead;
    private Camera2D _camera = null!;

    private CameraPan? _cameraPan;

    private VirtualStick _stick = null!;
    private Label _hud = null!;
    private Label _questHud = null!;
    private Label _hintHud = null!;

    private Label _lootHud = null!;
    private ulong _lootHudUntilMs;
    private readonly Dictionary<string, int> _lootTally = new();
    private int _lootMoney;

    private Label _lootMissedHud = null!;
    private readonly Dictionary<string, int> _lootMissed = new();

    private readonly HashSet<string> _lootMissedSeen = new();
    private Inotia4.Rendering.GroundLootView _groundLootView = null!;
    private int _groundLootRevision = -1;

    private Inotia4.Rendering.PickupMessages _pickupMessages = null!;
    private ActionControls _actionControls = null!;
    private ControlLayoutPreferences? _controlLayout;

    private DisplaySettings _displaySettings = null!;

    private ControlSettings _controlSettings = null!;

    private GameAudio? _audio;

    private SceneWorldCommands WorldSink => _worldSink ??= new SceneWorldCommands(this);

    private SceneWorldCommands? _worldSink;

    private ControlLayoutEditor? _layoutEditor;
    private Label _hudNotice = null!;

    private long _hudNoticeRemainingMs;
    private BagPanel? _bagPanel;
    private ShopPanel? _shopPanel;
    private CraftingPanel? _craftPanel;
    private SkillPanel? _skillPanel;
    private StatusPanel? _statusPanel;

    private PartyPanel? _partyPanel;

    private WorldEnemyRoster? _mapRoster;

    private QuestEnemyWatch? _questEnemyWatch;

    private string _menuMemberId = "player";

    private StorySkillProgression? _skills;

    private BattleCharacter? _hero;

    private PartyRoster? _party;

    private readonly List<PartyAlly> _allies = [];

    private string _partySignature = string.Empty;

    private sealed record PartyAlly(PartyAllyNode Node, string MemberId, PartyController Ai, PartyWorldPort Port,
        OverheadStatus Overhead)
    {
        public bool Fallen;
    }

    private readonly HashSet<string> _fallen = [];

    private readonly Dictionary<string, Tween> _deathTweens = [];

    private readonly Dictionary<string, Action> _partyDefeatWork = [];

    private Vec2? _wipeoutPosition;

    private bool _defeated;

    private bool _defeatPending;

    private bool _deathVisualsPaused;

    private readonly SessionCheckpoint<WorldSnapshotState> _checkpoint = new();

    private bool _checkpointPending;

    private int _checkpointCaptures;

    private DefeatPanel? _defeatPanel;

    private StatusHud? _statusHud;

    private Control? _saveButton;

    private Control? _settingsButton;

    private PartyController? _heroAi;

    private Func<Vec2?>? _heroFollow;

    private string ControlledId => _party?.ControlledId ?? PlayerCombatId;

    private ActorNode ControlledNode() =>
        ControlledId == PlayerCombatId ? _player : FindAllyNode(ControlledId) ?? _player;

    public bool SwitchControl(string id)
    {
        if (_party is null || !_party.SwitchControl(id)) return false;
        _route = null;
        _combatApproach = null;
        _heroFollow = null;
        foreach (var ally in _allies)
        {
            ally.Ai.Cancel();
            ally.Node.HaltMotion();
        }

        _heroAi?.Cancel();
        _awaitingInputRelease = true;
        return true;
    }

    private void SyncAllies()
    {
        if (_party is null) return;
        var signature = string.Join('|', _party.ActiveIds);
        if (signature == _partySignature) return;
        _partySignature = signature;

        var active = new HashSet<string>(_party.ActiveIds);
        foreach (var ally in _allies.ToList())
        {
            if (active.Contains(ally.MemberId) && GodotObject.IsInstanceValid(ally.Node)) continue;
            if (GodotObject.IsInstanceValid(ally.Node)) ally.Node.QueueFree();
            ally.Ai.Dispose();
            CombatRuntime.RemoveActor(ally.MemberId);
            _allies.Remove(ally);
        }

        foreach (var memberId in _party.ActiveIds)
        {
            if (memberId == PlayerCombatId) continue;
            if (_allies.Any(ally => ally.MemberId == memberId)) continue;
            var companion = _party.Get(memberId);
            if (companion is null) continue;
            var baseActor = _loader.World.Actors.GetValueOrDefault(companion.Definition.Actor);
            if (baseActor?.Collision is null) continue;

            // 换装外观：底稿是伙伴的本体素材，
            // 职业只通过配方里的技能姿态覆盖同名片段——所以不合并职业的人物片段。
            var outfit = _outfits is null || _bag is null
                ? null
                : _outfits.Prepare(baseActor, _bag.EquipmentItems(_bag.EquipmentFor(memberId)),
                    companion.ClassId, baseActor.Id, _outfits.ChannelFor($"member:{memberId}"));
            var appearance = outfit?.Actor ?? baseActor;

            var node = new PartyAllyNode { Name = $"ally-{memberId}", MemberId = memberId };
            node.Setup(appearance, _textures);
            var overhead = new OverheadStatus { Name = $"overhead-{memberId}" }
                .Build(_loader.StatusHud, path => _heroLease.Texture(path))
                .Data(() => OverheadVitals(memberId));
            node.AddChild(overhead);
            _entities.AddChild(node);

            var landing = PartyLanding(appearance.Collision, node) ?? _position;
            node.TeleportTo(new Vector2(landing.X, landing.Y));

            var port = new PartyWorldPort(this, node, memberId);
            // 普攻按他自己的装备取：换武器就换动作与特效，
            // 双持、狂气也照这条走；装备组合解析不出来才退回职业绑定，绝不让伙伴不能普攻。
            var attack = ResolveMemberAttack(memberId, companion.ClassId, appearance);
            if (attack is not null) _memberAttacks[memberId] = attack;
            var ai = new PartyController(memberId, port, new CombatController(memberId, attack, port));
            port.Ai = ai;
            CombatRuntime.RegisterActor(memberId, ai.CombatController);
            _allies.Add(new PartyAlly(node, memberId, ai, port, overhead));
            if (!companion.Alive) MarkAllyFallen(node);
        }
    }

    private void MarkAllyFallen(PartyAllyNode node)
    {
        node.HaltMotion();
        node.Visible = false;
    }

    private const float AllyMinSpacing = 30f;

    private const float AllyNudgeStep = 2.5f;

    private void SeparatePartyAllies()
    {
        if (_allies.Count < 2) return;
        if (_storyPlaying || _defeated) return;

        for (var i = 0; i < _allies.Count; i++)
        {
            var first = _allies[i];
            if (!CanShuffleAlly(first)) continue;
            for (var j = i + 1; j < _allies.Count; j++)
            {
                var second = _allies[j];
                if (!CanShuffleAlly(second)) continue;

                var delta = second.Node.Position - first.Node.Position;
                var distance = delta.Length();
                if (distance >= AllyMinSpacing) continue;

                var axis = distance > 0.01f ? delta / distance : Vector2.Right;
                var push = Mathf.Min((AllyMinSpacing - distance) * 0.5f, AllyNudgeStep);
                if (!TryNudgeAlly(second, axis * push)) TryNudgeAlly(first, -axis * push * 2f);
                else TryNudgeAlly(first, -axis * push);
            }
        }
    }

    /// <summary>
    /// 能不能被这次轻推动一下：上阵、活着、可见、不是当前被操控的那个，而且没在接近或出手。
    ///
    /// 最后这条是防抖：出手时两个人都往同一个目标身上凑，这时推开可能把人推出攻击距离，
    /// 下一步 AI 又走回来，来回就是可见的抽搐。战斗中宁可让他们叠着。
    /// </summary>
    private bool CanShuffleAlly(PartyAlly ally) =>
        GodotObject.IsInstanceValid(ally.Node) && !ally.Fallen && ally.Node.Visible &&
        ally.MemberId != ControlledId && Conditions.CanMove(ally.MemberId) &&
        ally.Ai.ActionState() == CombatState.Idle;

    private bool TryNudgeAlly(PartyAlly ally, Vector2 delta)
    {
        var target = ally.Node.Position + delta;
        if (!_grid.CanStandOnTerrain(target.X, target.Y)) return false;
        ally.Node.Position = target;
        return true;
    }

    private readonly Dictionary<string, ResolvedBasicAttack> _memberAttacks = [];

    public string MemberClassId(string memberId) =>
        memberId == PlayerCombatId ? _classId : _party?.Get(memberId)?.ClassId ?? _classId;

    public ResolvedBasicAttack? MemberAttackOf(string memberId) =>
        _memberAttacks.GetValueOrDefault(memberId);

    /// <summary>
    /// 伙伴普攻解析：按**他自己的装备**取这组武器的规则，
    /// 所以匕首、双手剑、法杖、弓各有各的动作与特效，双持与狂气也能落到伙伴身上。
    /// 装备组合没有普攻规则（或外观缺失）时退回职业绑定——原版遇到这种情形直接抛错，这里留一条退路，
    /// 免得一个内容缺口让整支队伍停止出手。
    /// </summary>
    private ResolvedBasicAttack? ResolveMemberAttack(string memberId, string classId, ActorDefinition actor)
    {
        try
        {
            return EquipmentAttack(actor, memberId, classId);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[combat] {memberId} 的装备普攻无法解析，退回职业绑定：{error.Message}");
        }

        _battle ??= _loader.Battle;
        var definition = _battle.Classes.FirstOrDefault(@class => @class.Id == classId);
        if (definition?.BasicAttack is null) return null;

        try
        {
            return BasicAttackRules.Resolve(_battle.BasicAttacks, definition.Actor, definition.BasicAttack);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[combat] {classId} 的普攻无法解析，伙伴按不能普攻处理：{error.Message}");
            return null;
        }
    }

    public IReadOnlyList<EnemyPlayableSkill> PartySkillsFor(string memberId)
    {
        var isPlayer = memberId == PlayerCombatId;
        var member = isPlayer ? null : _party?.Get(memberId);
        var classId = isPlayer ? _classId : member?.ClassId;
        _battle ??= _loader.Battle;
        var classDef = _battle.Classes.FirstOrDefault(@class => @class.Id == classId);
        if (classDef is null) return [];

        var loadout = isPlayer
            ? _skills?.Draft(_loader.Skills, _classId)
            : member!.Skills.Draft(_loader.Skills, member.ClassId);
        if (loadout is null) return [];

        var mana = isPlayer ? _hero?.Player.Mana ?? 0 : member!.Player.Mana;
        var frequencies = _party?.AiFrequency(memberId) ?? [];
        var skills = new List<EnemyPlayableSkill>();
        foreach (var skillId in classDef.SkillIds)
        {
            var skill = _loader.Skills.SkillForGrowth(skillId, companion: !isPlayer);
            var level = loadout.LevelOf(skillId);
            if (skill.Action is not { } action || level == 0) continue;

            var rank = skill.Ranks.FirstOrDefault(entry => entry.Level == level);
            int? manaCost = rank is null
                ? null
                : CombatResources.CalculateManaCost(rank.ManaCost,
                    AttributeOf(memberId, "manaCostRate"), AttributeOf(memberId, "manaCostReductionRate"));
            skills.Add(new EnemyPlayableSkill
            {
                Id = skillId,
                Name = skill.Name,
                Action = action,
                Level = level,
                ManaCost = manaCost,
                Frequency = mana >= (manaCost ?? 0) ? frequencies.GetValueOrDefault(skillId, 1) : 0,
                Clips = SkillActionRules.SkillClips(skillId),
            });
        }

        return skills;
    }

    private void CatchUpFollower(PartyAllyNode node)
    {
        if (_party is not { } party || !party.Alive(node.MemberId)) return;
        var landing = PartyLanding(node.Definition.Collision, node);
        if (landing is null) return;
        node.TeleportTo(new Vector2(landing.Value.X, landing.Value.Y));
    }

    /// <summary>
    /// 本图的通行查询。随地图重建：地形是图的属性，
    /// 实体列表每次查询现读，所以换图只要换掉这一份。
    /// </summary>
    private NavigationWorld? _navigation;

    private NavigationWorld Navigation => _navigation ??= new NavigationWorld(
        _map?.Id ?? "world", _grid, EnumerateNavigationActors);

    private IEnumerable<INavigationActor> EnumerateNavigationActors()
    {
        if (_player is not null)
            yield return new SceneNavigationActor(PlayerCombatId,
                () => _player.Definition.Collision, () => _position,
                () => !_fallen.Contains(PlayerCombatId), party: false, ghost: _storyPlaying || _defeated);

        foreach (var ally in _allies)
        {
            var captured = ally;
            yield return new SceneNavigationActor(captured.Node,
                () => captured.Node.Definition.Collision,
                () => new Vec2(captured.Node.Position.X, captured.Node.Position.Y),
                () => !captured.Fallen && captured.Node.Visible, party: true);
        }

        foreach (var enemy in _enemies)
        {
            var captured = enemy;
            yield return new SceneNavigationActor(captured,
                () => captured.Definition.Collision,
                () => new Vec2(captured.Position.X, captured.Position.Y),
                () => !captured.Dead && captured.Visible, party: captured.SummonedBy is not null);
        }

        foreach (var npc in _npcs)
        {
            var captured = npc;
            yield return new SceneNavigationActor(captured.Node,
                () => captured.Definition.Collision,
                () => new Vec2(captured.Node.Position.X, captured.Node.Position.Y),
                () => captured.Node.Visible, party: false);
        }
    }

    private Vec2? PartyLanding(CollisionBox box, PartyAllyNode moving) =>
        PartyPlacement.ChoosePartyLanding(Navigation, NavigationShape.Of(box), _position,
            new NavigationIgnore(moving, Party: true));

    private void SettleFriendlyUnitsAroundHero()
    {
        if (_map is null || _player is null) return;
        if (_loadingWorld || _storyPlaying || _defeated || _defeatPanel is { IsOpen: true }) return;
        // 锁定了攻击目标就不收拢：
        // 正在打的这一场没结束，伙伴不该被拉回主角身边。
        if (_focusTargetId is not null) return;
        if (!CanControl() || _pendingDefeats.Count > 0) return;

        foreach (var ally in _allies.ToList())
        {
            if (!GodotObject.IsInstanceValid(ally.Node) || ally.Fallen) continue;
            if (_party is { } party && !party.Alive(ally.MemberId)) continue;
            if (ally.Ai.ActionState() != CombatState.Idle || ally.Ai.SelectedTargetId() is not null) continue;
            var allyPosition = new Vec2(ally.Node.Position.X, ally.Node.Position.Y);
            if (!ShouldSettleFriendly(allyPosition, ally.Node.Definition.Collision, ally.Node)) continue;
            if (PartyLanding(ally.Node.Definition.Collision, ally.Node) is not { } landing) continue;

            ally.Ai.Cancel();
            ally.Node.TeleportTo(new Vector2(landing.X, landing.Y));
            var idle = "idle-" + ally.Node.Facing;
            if (ally.Node.Definition.HasClip(idle)) ally.Node.Visual.Play(idle);
        }

        foreach (var enemy in _enemies.ToList())
        {
            if (!GodotObject.IsInstanceValid(enemy) || enemy.Dead || !enemy.Visible) continue;
            if (enemy.SummonedBy is not { } owner || !PartySideOf(owner)) continue;
            if (_pendingDefeats.Contains(enemy.CombatId)) continue;
            if (enemy.SummonRemainingMs is <= 0) continue;
            if (enemy.SummonAi is { } summonAi && summonAi.ActionState() != FriendlySummonAiState.Idle) continue;
            var enemyPosition = new Vec2(enemy.Position.X, enemy.Position.Y);
            if (!ShouldSettleFriendly(enemyPosition, enemy.Definition.Collision, enemy)) continue;

            var shape = NavigationShape.Of(enemy.Definition.Collision);
            var landing = PartyPlacement.ChoosePartyLanding(Navigation, shape, _position,
                new NavigationIgnore(enemy, Party: true));
            if (landing is not { } spot) continue;

            enemy.SummonAi?.Cancel();
            enemy.HaltMotion();
            enemy.TeleportTo(new Vector2(spot.X, spot.Y));
            var idle = "idle-" + enemy.Facing;
            if (enemy.Definition.HasClip(idle)) enemy.Visual.Play(idle);
        }
    }

    private bool ShouldSettleFriendly(Vec2 position, CollisionBox box, object key)
    {
        if ((position - _position).Length > 64f) return true;
        if (!Navigation.TerrainFree(position)) return true;
        return !Navigation.CanPlaceBody(NavigationShape.Of(box), position, new NavigationIgnore(key, Party: true));
    }

    /// <summary>
    /// 落点修正：目标点站不住时就近换一个。
    /// 内容实体按窄足底口径找点；队伍单位还要整盒成立，口径与它们的移动一致。
    /// 附近找不到可站点时保留原点——落位是软约束，不该因为一个角色挡路中断剧情。
    /// </summary>
    private Vec2 SettleLanding(NavigationShape shape, Vec2 target, INavigationActor? ignore = null,
        bool strict = false)
    {
        var valid = strict
            ? Navigation.TerrainFree(target) && Navigation.CanPlaceBody(shape, target, ignore)
            : Navigation.CanPlace(shape, target, ignore);
        if (valid) return target;

        try
        {
            if (strict) return Navigation.NearestSettledBody(shape, target, ignore) ?? target;
            return Navigation.Nearest(shape, target, ignore);
        }
        catch (InvalidOperationException)
        {
            return target;
        }
    }

    /// <summary>
    /// 有人被摆到主角脚下时，主角就近让开。
    ///
    /// 主角在可位移名单里：内容给出的位置是权威的，玩家的走动或转场落点
    /// 不该把它顶掉，也不该让整组落位失败。伙伴与友方召唤物不参与——它们本就贴着主角站，
    /// 站位由队伍 AI 收拢（<see cref="NavigationIgnore"/> 的 <c>Party</c> 用来跳过它们）。
    /// </summary>
    private bool EvictPlayerFrom(Vec2 point, CollisionBox box, string actorId)
    {
        if (_player is null || actorId == _loader.Campaign.Start.Actor) return false;

        var playerBox = _player.Definition.Collision;
        if (!OverlapsBoxes(point, box, _position, playerBox)) return false;

        var landing = SettleLanding(NavigationShape.Of(playerBox), _position, new NavigationIgnore(PlayerCombatId));
        if (landing.X == _position.X && landing.Y == _position.Y) return false;

        TeleportPlayer(landing, _player.Facing);
        return true;
    }

    private static bool OverlapsBoxes(Vec2 a, CollisionBox boxA, Vec2 b, CollisionBox boxB) =>
        a.X + boxA.X < b.X + boxB.X + boxB.Width - 1e-4f &&
        a.X + boxA.X + boxA.Width > b.X + boxB.X + 1e-4f &&
        a.Y + boxA.Y < b.Y + boxB.Y + boxB.Height - 1e-4f &&
        a.Y + boxA.Y + boxA.Height > b.Y + boxB.Y + 1e-4f;

    private sealed class SceneNavigationActor(
        object key, Func<CollisionBox?> shape, Func<Vec2> position, Func<bool> enabled,
        bool party, bool ghost = false) : INavigationActor
    {
        public object Key => key;

        public NavigationShape Shape => NavigationShape.Of(shape());

        public Vec2 Position => position();

        public bool Enabled => enabled();

        public bool Party => party;

        public bool Ghost => ghost;

        public bool StrictTerrain => party;
    }

    private Combatant AllyCombatant(PartyAllyNode node) =>
        new(node.MemberId, "party", new Vec2(node.Position.X, node.Position.Y),
            node.Definition.Collision, node.Facing, Conditions.ActiveStates(node.MemberId));

    /// <summary>
    /// 一名成员倒下：
    /// 记倒、收动作、清状态、从所有目标表里摘掉、撤掉他召唤的东西，播死亡淡出；
    /// 淡出结束（<c>party-defeat:&lt;id&gt;</c> 定时任务）时若全队都没了就叫 <see cref="PlayerDefeated"/>，
    /// 否则把操控权交给还站着的人。主角与伙伴共用这一条管线。
    /// </summary>
    private void MemberDefeated(string id)
    {
        if (_fallen.Contains(id)) return;
        if (ActorNodeOf(id) is not { } node) return;

        _fallen.Add(id);

        if (!ActivePartySlots().Any(member => (VitalsOf(member)?.Health ?? 0) > 0))
            _wipeoutPosition = new Vec2(node.Position.X, node.Position.Y);

        ClearMemberLimits(id);
        Conditions.Remove(id);

        if (id == PlayerCombatId)
        {
            _heroAi?.Disable();
            CombatRuntime.Cancel(PlayerCombatId);
            _player.EndAttack();
            _combatApproach = null;
            _route = null;
        }
        else if (_allies.FirstOrDefault(ally => ally.MemberId == id) is { } ally)
        {
            ally.Fallen = true;
            ally.Ai.Disable();
            CombatRuntime.Cancel(id);
            ally.Node.EndAttack();
            ally.Node.HaltMotion();
        }

        // 所有控制器都不该再盯着这个 id。
        foreach (var member in ActivePartySlots())
            ControlOf(member)?.TargetRemoved(id);

        foreach (var enemy in _enemies.ToList())
        {
            enemy.Ai?.TargetRemoved(id, "inactive");
            if (enemy.SummonedBy == id) RemoveSummon(enemy);
        }

        HaltActor(node);
        var clip = StartDeathFade(id, node);
        var frames = clip is null ? 0 : (int)Math.Ceiling(clip.Frames * 1000.0 / clip.FrameRate / CombatClock.LogicFrameMs);

        void Finish()
        {
            _partyDefeatWork.Remove(id);
            CombatRuntime.Cancel($"party-defeat:{id}");
            if (_deathTweens.Remove(id, out var tween)) tween.Kill();
            if (_defeated || MemberHealth(id) > 0) return;

            if (GodotObject.IsInstanceValid(node)) node.Visible = false;
            var wipe = _party is { } party ? party.OnDefeat(id) : _playerHealth <= 0;
            if (wipe)
            {
                if (_deathTweens.Count == 0) PlayerDefeated();
            }
            else
            {
                SyncControlled();
            }
        }

        _partyDefeatWork[id] = Finish;
        CombatRuntime.Schedule($"party-defeat:{id}", 10 + frames, Finish);
    }

    private void PlayerDefeated()
    {
        if (_defeated) return;

        _defeated = true;
        _feedbackView?.SetPaused(true);
        _outcomes?.SetPaused(true);
        CombatRuntime.SetPaused("control", true);

        foreach (var ally in _allies) ally.Ai.Cancel();
        _heroAi?.Cancel();
        StopEnemies();
        HaltActor(ControlledNode());

        HandleCombatDefeat();
    }

    /// <summary>
    /// 死亡淡出跟着战斗一起冻：暂停期间新倒下的人
    /// 也要立刻停在首帧，不能等下一次暂停状态切换。
    /// </summary>
    private void SyncDeathVisualPause(bool paused)
    {
        if (paused == _deathVisualsPaused) return;
        _deathVisualsPaused = paused;

        foreach (var (id, tween) in _deathTweens)
        {
            if (paused) tween.Pause();
            else tween.Play();

            if (ActorNodeOf(id) is { } node) node.Visual.SpeedScale = paused ? 0f : 1f;
        }
    }

    private void SyncStealthVisuals()
    {
        if (Conditions is null) return;

        SetStealth(PlayerCombatId);
        foreach (var ally in _allies) SetStealth(ally.MemberId);
        foreach (var enemy in _enemies) SetStealth(enemy.CombatId);
    }

    private void SetStealth(string id)
    {
        if (ActorNodeOf(id) is not { } node) return;
        node.Visual.SetStealth(Conditions.HasState(id, "stealthed"));
    }

    private void SyncStatusColors()
    {
        if (Conditions is null) return;
        var frame = (int)_session.CombatTick;

        PaintStatus(PlayerCombatId, frame);
        foreach (var ally in _allies) PaintStatus(ally.MemberId, frame);
        foreach (var enemy in _enemies) PaintStatus(enemy.CombatId, frame);
    }

    private void PaintStatus(string id, int frame)
    {
        if (VisualOf(id) is not { } node) return;
        node.Visual.PaintStatus(Conditions.AppearanceColors(id), frame);
    }

    private ActorNode? VisualOf(string id)
    {
        if (id == PlayerCombatId) return _player;
        if (FindAllyNode(id) is { } ally) return ally;
        return FindEnemy(id);
    }

    private void StopEnemies()
    {
        foreach (var enemy in _enemies)
        {
            Conditions.ClearTemporary(enemy.CombatId);
            enemy.Ai?.Cancel();
            enemy.SummonAi?.Cancel();
            enemy.HaltMotion();
        }
    }

    private ActorClip? StartDeathFade(string id, ActorNode node)
    {
        ActorClip? clip = null;
        if (id != PlayerCombatId)
        {
            var death = "die-" + node.Facing;
            if (node.Definition.Clips.TryGetValue(death, out var found) && node.Definition.HasClip(death))
            {
                clip = found;
                node.Visual.Play(death);
            }
            else
            {
                node.Visual.Pause();
            }
        }
        else
        {
            node.Visual.Pause();
        }

        var delay = clip is null ? 0f : (float)(clip.Frames * 1000.0 / clip.FrameRate / 1000.0);
        var tween = CreateTween();
        if (delay > 0f) tween.TweenInterval(delay);
        tween.TweenProperty(node, "modulate:a", 0f, 10 * CombatClock.LogicFrameMs / 1000f);
        _deathTweens[id] = tween;
        if (_deathVisualsPaused)
        {
            tween.Pause();
            node.Visual.SpeedScale = 0f;
        }

        return clip;
    }

    /// <summary>
    /// 倒下的人被扶起来：撤掉死亡收尾、清状态、
    /// 落到 <paramref name="origin"/> 附近最近的合法位置，恢复可见并回到待机。
    /// 生命必须已经由会话补上；已经为 0 的角色在这里直接返回。
    /// </summary>
    private void ReviveMember(string id, Vec2? origin = null)
    {
        if (MemberHealth(id) <= 0) return;

        CombatRuntime.Cancel($"party-defeat:{id}");
        _partyDefeatWork.Remove(id);
        if (ActorNodeOf(id) is not { } node) return;

        if (_deathTweens.Remove(id, out var tween)) tween.Kill();
        _fallen.Remove(id);
        _dealtDamage.Remove(id);
        Conditions.Remove(id);
        ClearMemberLimits(id);
        ControlOf(id)?.Reset();
        if (id == PlayerCombatId) _heroAi?.Reset();
        else if (_allies.FirstOrDefault(ally => ally.MemberId == id) is { } ally) ally.Fallen = false;

        var from = origin ?? _position;
        var shape = NavigationShape.Of(node.Definition.Collision);
        var ignore = new NavigationIgnore(node);
        var landing = Navigation.NearestBody(shape, from, ignore) ?? Navigation.Nearest(shape, from, ignore);
        node.TeleportTo(new Vector2(landing.X, landing.Y));
        node.Modulate = new Color(1f, 1f, 1f, 1f);
        node.Visual.SpeedScale = 1f;
        node.Visible = true;
        node.SetFacing("south");
        node.EndAttack();

        _outcomes?.Recover(node);
    }

    private ItemResult RevivePartyMember(string id, string item)
    {
        if (_defeated && _party is null) throw new InvalidOperationException("当前无法复活");
        var party = _party ?? throw new InvalidOperationException("队伍不可用");

        var dead = party.ActiveIds.Where(member => !party.Alive(member)).ToList();
        var origin = _wipeoutPosition ?? new Vec2(_player.Position.X, _player.Position.Y);
        var result = party.Revive(id, item);

        foreach (var member in dead)
            if (party.Alive(member)) ReviveMember(member, origin);

        _wipeoutPosition = null;
        _defeated = false;
        _focusTargetId = null;
        if (!party.Alive(party.ControlledId) && party.LivingIds.Count > 0)
            party.SwitchControl(party.LivingIds[0]);
        SyncControlled();
        _audio?.Ui("heal");
        return result;
    }

    public void ReviveForDebug(string id)
    {
        if (_party is null) return;

        var origin = _wipeoutPosition ?? new Vec2(_player.Position.X, _player.Position.Y);
        ReviveMember(id, origin);
        _wipeoutPosition = null;
        _defeated = false;
        _focusTargetId = null;
        if (!_party.Alive(_party.ControlledId) && _party.LivingIds.Count > 0)
            _party.SwitchControl(_party.LivingIds[0]);
        SyncControlled();
        _audio?.Ui("heal");
    }

    public void RevivePartyAtHealer()
    {
        var origin = new Vec2(_player.Position.X, _player.Position.Y);
        var result = PartyRevival.ReviveAtHealer(_session);
        foreach (var id in result.MemberIds) ReviveMember(id, origin);
        SyncControlled();
        _audio?.Ui("heal");
    }

    private void HandleCombatDefeat()
    {
        if (_storyPlaying || _storyResumeQueued || _waitingEncounter is { Length: > 0 })
        {
            _defeatPending = true;
            return;
        }

        ShowCombatDefeat();
    }

    private void SettlePendingDefeat()
    {
        if (!_defeatPending) return;
        _defeatPending = false;

        if (_party is { } party ? party.LivingIds.Count == 0 : _playerHealth <= 0)
        {
            ShowCombatDefeat();
            return;
        }

        _defeated = false;
        _focusTargetId = null;
        foreach (var id in _fallen.ToList())
            if (MemberHealth(id) > 0) ReviveMember(id);
        SyncControlled();
    }

    private void ShowCombatDefeat()
    {
        if (_defeatPanel is null) return;
        _defeatPanel.Open();
    }

    private int PrayerCountInBag() =>
        _bag?.Entries.Where(entry => entry.ItemId == "resurrection-prayer").Sum(entry => entry.Quantity) ?? 0;

    private bool ReviveByPrayer()
    {
        var entry = _bag?.Entries.FirstOrDefault(value => value.ItemId == "resurrection-prayer");
        if (entry is null) return false;

        RevivePartyMember("player", entry.Id);
        return true;
    }

    private async Task RestoreCheckpointAsync()
    {
        var snapshot = _checkpoint.Peek() ?? throw new InvalidOperationException("没有可恢复的检查点");
        GD.Print($"[checkpoint] 回退到 {snapshot.Location.Map} ({snapshot.Location.X:F0},{snapshot.Location.Y:F0})");

        _session.RestoreState(snapshot.Session);
        ResetCombatRuntimeForRestore();

        _pendingWorldRestore = snapshot.World is { HasWorld: true } ? snapshot.World : null;
        _defeated = false;
        _defeatPending = false;
        _fallen.Clear();
        _focusTargetId = null;
        foreach (var tween in _deathTweens.Values) tween.Kill();
        _deathTweens.Clear();
        _partyDefeatWork.Clear();
        _wipeoutPosition = null;

        var facing = snapshot.World.Location.Facing is { Length: > 0 } value ? value : "south";
        LoadMap(snapshot.Location.Map, new Vec2((float)snapshot.Location.X, (float)snapshot.Location.Y), facing);
        SyncControlled();

        RevealWorld();
        RestoreActorPresence(_player);
        foreach (var ally in _allies) RestoreActorPresence(ally.Node);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void SyncControlled()
    {
        _feedbackView?.CancelShake();
        foreach (var ally in _allies) ally.Ai.Cancel();
        _heroAi?.Cancel();
        _combatApproach = null;
        _route = null;
    }

    private void RestoreActorPresence(ActorNode node)
    {
        node.Modulate = new Color(1f, 1f, 1f, 1f);
        node.Visual.SpeedScale = 1f;
        node.Visible = true;
        node.EndAttack();
        node.Visual.Play("idle-" + node.Facing);
    }

    private void MarkAlreadyFallen(string id)
    {
        if (ActorNodeOf(id) is not { } node) return;
        _fallen.Add(id);
        HaltActor(node);
        node.Visible = false;
    }

    private void RemoveSummon(EnemyNode enemy)
    {
        foreach (var child in _enemies.Where(entry => entry.SummonedBy == enemy.CombatId).ToList())
            RemoveSummon(child);

        CancelEnemyAction(enemy);
        _enemies.Remove(enemy);
        _threat?.RemoveActor(enemy.CombatId);
        ForgetFocusedEnemy(enemy.CombatId);
        enemy.QueueFree();
    }

    private int MemberHealth(string id) =>
        id == PlayerCombatId ? _playerHealth : (_party?.Get(id)?.Player.Health ?? 0);

    private int MemberLevel(string id) =>
        id == PlayerCombatId ? _hero?.Player.Level ?? 1 : _party?.Get(id)?.Player.Level ?? 1;

    private void ClearMemberLimits(string id)
    {
        if (id == PlayerCombatId) _hero?.ClearCombatResourceLimits();
        else _party?.Get(id)?.ClearCombatResourceLimits();
    }

    private ActorNode? ActorNodeOf(string id) => id == PlayerCombatId ? _player : FindAllyNode(id);

    private PartyController? ControlOf(string id) =>
        id == PlayerCombatId ? _heroAi : _allies.FirstOrDefault(ally => ally.MemberId == id)?.Ai;

    private static void HaltActor(ActorNode node)
    {
        switch (node)
        {
            case PartyAllyNode ally:
                ally.HaltMotion();
                ally.UpdateMotion(Vec2.Zero, false);
                break;
            case EnemyNode enemy:
                enemy.HaltMotion();
                break;
            default:
                node.UpdateMotion(Vec2.Zero, false);
                break;
        }
    }

    private sealed class PartyWorldPort(WorldScene scene, PartyAllyNode node, string memberId) : IPartyPort
    {
        public PartyController? Ai;

        public int Followed;

        public int Stops;

        public Combatant? Actor(string id) =>
            id == memberId ? scene.AllyCombatant(node) : scene.FindCombatant(id);

        public IReadOnlyList<Combatant> Candidates() => scene.AllCombatants();

        public bool IsAlive(string id) => scene.IsCombatantAlive(id);

        public bool IsPaused() => scene.CombatRuntime.Paused;

        public BasicAttackSelection SelectBasicAttack(ResolvedBasicAttack normal) =>
            scene.SelectBasicAttack(memberId, scene.MemberClassId(memberId), normal);

        public IReadOnlyList<PartyBuffSlot?>? PartySlots() => scene.PartyBuffSlots();

        public int RangeBonusUnits(string id) => scene.AttributeOf(id, "attackRangeBonusUnits");

        public int? HealScore(string id) => scene.HealScoreFor(id);

        public IReadOnlyList<HealSlot>? HealCandidates() => scene.HealCandidatesFor();

        public string Facing() => node.Facing;

        public void MoveToward(Vec2 position) => node.MoveToward(position);

        public void Stop()
        {
            Stops += 1;
            node.HaltMotion();
        }

        public bool CanStand(Vec2 position, string? clearanceActorId = null) =>
            scene._grid.CanStandOnTerrain(position.X, position.Y);

        public Action Play(CombatAction action, Action<int> frame, Action complete)
        {
            node.SetFacing(action.Facing);
            return scene.PlayCombatAction(action, frame, complete);
        }

        public void Frame(CombatAction action, int index, Action<string, string?> contact,
            SkillUnitsReady unitsReady, Action? areaContact = null) =>
            scene.DispatchCombatFrame(action, index, contact, unitsReady, areaContact);

        public void Contact(AttackContact contact)
        {
            if (contact.SkillId is not null)
            {
                scene.NotifySkillContact(contact);
                return;
            }

            scene.SettleBasicAttackContact(memberId, contact.TargetId, contact.Hand ?? "main-hand",
                contact.BasicAttack ?? scene.MemberAttackOf(memberId));
        }

        public SkillUnitOutcome Unit(SkillUnitHit hit) => scene.SkillUnit(hit);

        public PreparedDamage? PrepareUnit(SkillUnitStart start) => scene.PrepareSkillUnit(start);

        public void FollowPath(Func<Vec2?> nextWaypoint)
        {
            Followed += 1;
            node.FollowPath(nextWaypoint);
        }

        public Combatant? Leader() => scene.FindCombatant(PlayerCombatId);

        public string? FocusTarget() => scene._focusTargetId;

        public bool InCombat() =>
            scene._focusTargetId is not null || scene.SummonAwareness.InCombat(scene.SummonNow);

        public CounterattackMode Counterattack() =>
            (CounterattackMode)Math.Clamp(scene._party?.Counterattack(memberId) ?? 2, 0, 2);

        public int DamageCount() => scene._damageCount;

        public Rect NavigationBounds() => scene._grid.Bounds;

        public void Regroup()
        {
            Ai?.Cancel();
            scene.CatchUpFollower(node);
        }

        public IActorCooldownPort Cooldowns => scene.CooldownsFor(memberId);

        public IReadOnlyList<EnemyPlayableSkill> Skills() => scene.PartySkillsFor(memberId);

        public string? SkillKind(string id) => scene._party?.Catalog.AutoSkillKind(id);

        public bool CanSpendMana(int cost) =>
            scene.Conditions.IsInvincible(memberId) || (scene._party?.Get(memberId)?.Player.Mana ?? 0) >= cost;

        public bool SpendMana(int cost) =>
            scene.Conditions.IsInvincible(memberId) || (scene._party?.Get(memberId)?.SpendMana(cost) ?? false);

        public (int Health, int MaxHealth)? Vitals(string id)
        {
            if (scene._party?.Get(id) is { } member) return (member.Player.Health, member.Player.MaxHealth);
            if (id == WorldScene.PlayerCombatId)
            {
                var player = scene._hero?.Player;
                return player is null ? null : (player.Value.Health, player.Value.MaxHealth);
            }

            return null;
        }

        public bool Attacking(string id)
        {
            var controller = id == memberId
                ? Ai
                : scene._allies.FirstOrDefault(ally => ally.MemberId == id)?.Ai;
            return controller?.ActionState() == CombatState.Attacking;
        }

        public int AttackRange() => 4;

        public void SkillSounded(SkillPlayback skill) => scene.NoticeSkillSound(skill);
    }

    private sealed class HeroPartyPort(WorldScene scene) : IPartyPort
    {
        public Combatant? Actor(string id) => scene.FindCombatant(id);

        public IReadOnlyList<Combatant> Candidates() => scene.AllCombatants();

        public bool IsAlive(string id) => scene.IsCombatantAlive(id);

        public bool IsPaused() => scene.CombatRuntime.Paused;

        public IActorCooldownPort Cooldowns => scene.CooldownsFor(WorldScene.PlayerCombatId);

        public IReadOnlyList<PartyBuffSlot?>? PartySlots() => scene.PartyBuffSlots();

        public int RangeBonusUnits(string id) => scene.AttributeOf(id, "attackRangeBonusUnits");

        public int? HealScore(string id) => scene.HealScoreFor(id);

        public IReadOnlyList<HealSlot>? HealCandidates() => scene.HealCandidatesFor();

        public string Facing() => scene.PlayerFacing;

        public void MoveToward(Vec2 position) => scene.ApproachCombatTarget(position);

        public void Stop() => scene.StopCombatApproach();

        public bool CanStand(Vec2 position, string? clearanceActorId = null) =>
            scene.CanPlayerStandAt(position);

        public Rect NavigationBounds() => scene.MapBounds;

        public Action Play(CombatAction action, Action<int> frame, Action complete) =>
            scene.PlayCombatAction(action, frame, complete);

        public void Frame(CombatAction action, int index, Action<string, string?> contact,
            SkillUnitsReady unitsReady, Action? areaContact = null) =>
            scene.DispatchCombatFrame(action, index, contact, unitsReady, areaContact);

        public void Contact(AttackContact contact) =>
            scene.SettlePlayerContact(contact.TargetId, contact.Hand);

        public SkillUnitOutcome Unit(SkillUnitHit hit) => scene.SkillUnit(hit);

        public PreparedDamage? PrepareUnit(SkillUnitStart start) => scene.PrepareSkillUnit(start);

        public void RunUnitSequence(Action work) => scene.RunInHitResolution(work);

        public Combatant? Leader() => scene.FindCombatant(scene.ControlledId);

        public string? FocusTarget() => scene._focusTargetId;

        public bool InCombat() =>
            scene._focusTargetId is not null || scene.SummonAwareness.InCombat(scene.SummonNow);

        public CounterattackMode Counterattack() =>
            (CounterattackMode)Math.Clamp(scene._party?.HeroCounterattack ?? 2, 0, 2);

        public int DamageCount() => scene._damageCount;

        public void FollowPath(Func<Vec2?> nextWaypoint) => scene._heroFollow = nextWaypoint;

        public void Regroup()
        {
            scene._heroFollow = null;
            var leader = Leader();
            if (leader is null) return;
            var landing = scene.NearestSummonSpot(leader.Position, scene._player.Definition.Collision);
            if (landing is { } spot) scene.PlacePlayer(spot);
        }

        public IReadOnlyList<EnemyPlayableSkill> Skills() => scene.PartySkillsFor(WorldScene.PlayerCombatId);

        public string? SkillKind(string id) => scene._party?.Catalog.AutoSkillKind(id);

        public (int Health, int MaxHealth)? Vitals(string id) =>
            scene.VitalsOf(id) is { } vitals ? (vitals.Health, vitals.MaxHealth) : null;

        public bool Attacking(string id) =>
            id == WorldScene.PlayerCombatId
                ? scene.PlayerActionState == CombatState.Attacking
                : scene._allies.FirstOrDefault(ally => ally.MemberId == id)?.Ai.ActionState() == CombatState.Attacking;

        public int AttackRange() => 4;
    }

    private QuestPanel? _questPanel;

    private readonly Session _session = new();

    private SaveSlots _saveSlots = null!;

    private AutoSaver _autoSaver = null!;

    private SystemPanel _systemPanel = null!;

    private SaveSlotRef? _currentSaveSlot;

    public SaveSlotRef? TitleResume { get; set; }

    public int? TitleNewSlot { get; set; }

    public string? TitleClassId { get; set; }

    public Action? TitleRequest { get; set; }

    private CharacterPanel _panel = null!;

    private Vec2 _position => _session.Position;

    /// <summary>
    /// 判定与交互用的位置：操控者切给伙伴时，
    /// 交互、拾取、开箱、出口、触发的判定都跟着伙伴走。
    /// 会话位置始终是主角的——存档与检查点口径不变，
    /// 所以伙伴移动不写 <see cref="_session"/>。
    /// </summary>
    private Vec2 ControlPosition
    {
        get
        {
            var node = ControlledNode();
            return new Vec2(node.Position.X, node.Position.Y);
        }
    }

    private bool ControllingAlly => ControlledId != PlayerCombatId;

    private WalkRoute? _route;

    private bool _awaitingInputRelease;

    private ColorRect _fade = null!;

    private Tween? _fadeTween;

    private bool _initialCurtain;

    private Inotia4.Ui.LoadingOverlay? _loading;

    /// <summary>
    /// 加载页单独一层：它要在内容都还没读的时候就能画出来，所以不能等 <see cref="SetUpHud"/>
    /// 建 HUD 层。层级压在所有界面之上（HUD 是 1）。
    /// </summary>
    private CanvasLayer? _loadingLayer;

    private bool _worldReady;

    private readonly List<LoadStage> _loadStages = [];

    private long _loadFirstFrame;

    private long _loadFrames;

    private bool _loadingWorld;

    private Node2D _entities = null!;

    private StoryDirector _story = null!;
    private StoryStage _stage = null!;
    private QuestTracker _tracker = null!;
    private QuestJournal _journal = null!;
    private Bag _bag = null!;
    private DialogueBox _dialogue = null!;
    private ChoiceBox _choice = null!;
    private QuestPopup _questPopup = null!;

    private Control? _menuClose;

    private ColorRect? _menuBackdrop;

    private const float MenuCloseX = 960f - 51f - 13f;

    private const float MenuCloseY = 24f;

    private string? CloseAsset => _loader.Interface.Assets.GetValueOrDefault("close");
    private CanvasLayer _hudLayer = null!;

    private Inotia4.Ui.NameBanner? _nameBanner;

    private readonly List<MapNpc> _npcs = [];

    private MapNpc? _nearNpc;

    private int _questBagRevision = -1;

    private bool _storyPlaying;

    private CancellationTokenSource? _storySkip;

    private SkipButton _skipButton = null!;

    private bool _loadedFromSave;

    /// <summary>
    /// 读档建图这一趟不要写自动档： 进场只装载、不再写一遍，
    /// 否则刚读出来的状态会立刻覆盖自动槽。
    /// </summary>
    private bool _skipMapEntryAutoSave;

    private readonly List<string> _pendingDefeats = [];

    private readonly HashSet<string> _triggerOccupancy = [];

    private readonly HashSet<string> _triggerConditionHeld = [];

    internal int _debugTriggerFires;

    private readonly HashSet<string> _deferredTriggers = [];

    private int _deferScanFrames;

    private bool _autoStory;

    private bool _skipCheck;

    private bool _skipCheckFired;
    private int _skipCheckFrames;

    private bool _nodeCheck;

    private int _nodeCheckStartCaptures;
    private long _nodeCheckStartTime;

    private string _nodeCheckStartSession = string.Empty;

    private readonly HashSet<string> _unsupportedStoryTypes = [];

    private string? _waitingEncounter;

    /// <summary>
    /// 遭遇结果已写回、续播已排队但还没开演。这一帧里 <c>_storyPlaying</c> 还是 false，
    /// 触发器若插进来会起第二段剧情，所以两个入口都要把它当成「演出未收尾」。
    /// </summary>
    private bool _storyResumeQueued;

    private string? _storyOwnerScript;

    private string? _storyEventId;

    private bool _suppressEncounterAutoWin;

    private (string Script, string? EventId)? _encounterDeferredStory;

    private bool _arena;
    private BattleContent? _battle;
    private readonly List<EnemyNode> _enemies = [];
    private readonly Random _random = new();

    internal const string PlayerCombatId = "player";

    private CombatClock _combatClock => _session.CombatClock;

    private readonly PathBudget _pathBudget = new(PathBudgetPerRenderFrame);

    private const int PathBudgetPerRenderFrame = 512;

    private CombatThreatSystem? _threat;

    private SummonCombatAwareness? _summonAwareness;

    private long SummonNow => _combatClock.Frame * CombatClock.LogicFrameMs;

    private SummonCombatAwareness SummonAwareness => _summonAwareness ??= new SummonCombatAwareness(SummonNow);

    private readonly CombatHitResolution _hits = new();

    private CombatResourceRecovery _recovery => _session.ResourceRecovery;

    /// <summary>
    /// 战斗运行时：动作帧、定时任务、在途攻击共用一个时钟。
    /// 时钟就是逻辑帧号，所以它按帧推进而不是按毫秒。
    /// </summary>
    private CombatRuntime? _combatRuntime;

    private CombatConditions? _conditions;

    public CombatRuntime CombatRuntime => _combatRuntime ??= new CombatRuntime(() => _combatClock.Frame);

    private CombatConditions Conditions => _conditions ??= new CombatConditions(
        new SceneBuffResourcePort(this), new SceneInvincibilityPort(this));

    private readonly HashSet<string> _invincible = [];

    private Fighter _playerFighter = null!;

    private int _playerHealth => _hero?.Player.Health ?? 0;

    private void SetPlayerHealth(int value) => _hero?.SetHealth(value);

    private int PlayerMaxHealth => _hero?.Player.MaxHealth ?? _playerFighter.MaxHealth;
    private bool _attackHeld;
    private readonly BasicAttackRepeat _autoAttack = new();
    private bool _exitLocked;
    private Vec2 _exitLockAnchor;
    private int _spawnedActors;

    private readonly List<MapEntity> _baseEntities = [];

    private readonly List<MapActorRecord> _mapActors = [];

    private int _shotCountdown = -1;

    private bool _probeFps;

    private int _probeFrames;
    private int _frameCounter;

    private bool _hold;

    private bool _exitCheck;
    private int _exitCheckFrames;

    private bool _placementCheck;

    private sealed class RecoveryTarget(
        string id, Func<PlayerVitals> read, Action<int> setHealth, Action<int> setMana) : IRecoveryMember
    {
        public string Id => id;

        public int Health => read().Health;

        public int MaxHealth => read().MaxHealth;

        public int Mana => read().Mana;

        public int MaxMana => read().MaxMana;

        public void SetHealth(int value) => setHealth(value);

        public void SetMana(int value) => setMana(value);
    }

    private sealed record MapNpc(
        ActorNode Node, string EntityId, string ActorId, string? CharacterId, ActorDefinition Definition,
        bool OneTime = false, string? Objective = null, string? Hint = null, bool Spring = false)
    {
        public string? ChestKind => Definition.Chest;
    }

    /// <summary>
    /// 本图摆出来的一个角色：节点、摆放身份与可交互包装。
    /// 不可见的角色也在表里——存档快照要记「它现在不画」，读档才知道该不该露出来。
    /// </summary>
    private sealed record MapActorRecord(
        string EntityId, string ActorId, string? CharacterId, string? Authority, bool Flip,
        ActorNode Node, MapNpc Npc);

    public override void _Ready()
    {
        GameTheme.Apply(this);

        // 加载页要在第一帧就能画出来。整段就绪实测 1.3 秒（内容 365ms、角色与舞台 580ms、
        // 界面 150ms、进图 220ms），一口气跑完的话加载页连一次都画不出来，
        // 玩家看到的就是「点开始游戏之后画面不动」。所以先建这一层、立刻显示，再分帧干活。
        _loadingLayer = new CanvasLayer { Name = "LoadingLayer", Layer = 100 };
        AddChild(_loadingLayer);
        _loading = new Inotia4.Ui.LoadingOverlay { Name = "Loading" };
        _loadingLayer.AddChild(_loading);
        _loading.Show("正在载入…", WorldLoadStartProgress);

        SetUpWorld();
    }

    private async void SetUpWorld()
    {
        await LoadStageAsync("正在读取内容…", WorldLoadStartProgress);

        _autoStory = DisplayServer.GetName() == "headless" ||
                     Array.IndexOf(OS.GetCmdlineUserArgs(), "--autoplay") >= 0;

        // --skipstory：跳过自检要的正是「本该等按键」的状态，所以不自动翻页；
        // 演出推进全靠那一次跳过，跳过没接上就会一直等（超时即失败）。
        _skipCheck = Array.IndexOf(OS.GetCmdlineUserArgs(), "--skipstory") >= 0;
        if (_skipCheck) _autoStory = false;

        _nodeCheck = Array.IndexOf(OS.GetCmdlineUserArgs(), "--nodecheck") >= 0;

        var shotIndex = Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot");
        if (shotIndex >= 0)
        {
            _shotCountdown = shotIndex + 1 < OS.GetCmdlineUserArgs().Length &&
                             int.TryParse(OS.GetCmdlineUserArgs()[shotIndex + 1], out var shotFrames)
                ? shotFrames
                : 150;
        }
        _probeFps = Array.IndexOf(OS.GetCmdlineUserArgs(), "--fpsprobe") >= 0;
        _hold = Array.IndexOf(OS.GetCmdlineUserArgs(), "--hold") >= 0;
        _autoPress = Array.IndexOf(OS.GetCmdlineUserArgs(), "--autopress") >= 0;

        _loader = new ContentLoader();
        _journal = new QuestJournal(_loader.Campaign);
        _questEnemyWatch = new QuestEnemyWatch(_journal, _loader.Campaign.Quests, _loader.Drops.QuestItems);
        _textures = new TextureCache();
        _heroLease = _textures.Lease();

        _saveSlots = new SaveSlots(new FileSaveStorage());
        _classId = ResolveClassId();

        var debugArgs = OS.GetCmdlineUserArgs();
        var levelIndex = Array.IndexOf(debugArgs, "--level");
        if (levelIndex >= 0 && levelIndex + 1 < debugArgs.Length &&
            int.TryParse(debugArgs[levelIndex + 1], out var debugLevel) && debugLevel >= 1)
            _session.PlayerLevel = debugLevel;

        await LoadStageAsync("正在准备角色…", 0.66f);
        SetUpCharacter();

        _journal.Bag = _bag;
        _journal.Session = _session;
        _journal.ClassIdProvider = () => _classId;
        _session.Journal = _journal;
        _session.MapEntryPlacements = _loader.Campaign.EntryPlacements;

        _autoSaver = new AutoSaver(new AutoSavePorts(this), _journal.Book);
        _autoSaver.Start();

        // 主角战斗数值对剧情遭遇战也是必需的：遭遇战开场就按面板建好，
        // 不能等 --arena 才建——否则血量是 0，一进战斗立刻判负。
        SetUpPlayerCombat();

        _arena = Array.IndexOf(OS.GetCmdlineUserArgs(), "--arena") >= 0;
        if (_arena) _battle = _loader.Battle;

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--panel") >= 0 ||
            Array.IndexOf(OS.GetCmdlineUserArgs(), "--gear") >= 0) PrintPanel();

        var startActor = _loader.Campaign.Start.Actor;
        if (!_loader.World.Actors.TryGetValue(startActor, out var heroActor))
            throw new InvalidOperationException($"world.json 里找不到主角 {startActor}");

        var hero = StoryClassAppearance.Merge(heroActor, _loader.StoryClasses.Class(_classId));

        _player = new ActorNode { Name = "Player" };
        _player.Setup(hero, _heroLease);
        _overhead = new OverheadStatus { Name = "Overhead" }
            .Build(_loader.StatusHud, path => _heroLease.Texture(path))
            .Data(() => OverheadVitals(PlayerCombatId));
        _player.AddChild(_overhead);

        _heroBase = hero;
        _outfits = new Inotia4.Rendering.WardrobeOutfits(_loader.Wardrobe, _loader.Items, _heroLease);
        ApplyPlayerOutfit();

        _stage = new StoryStage { Name = "Stage" };
        AddChild(_stage);
        _stage.Setup(_loader, _textures, hero);
        _stage.AppearanceSource = actor => _session.StoryWorld.ActorAppearance(actor);
        _stage.MemberAppearanceSource = StageMemberAppearance;

        _camera = new Camera2D
        {
            Name = "Camera",
            Zoom = new Vector2(CameraZoom, CameraZoom),
            PositionSmoothingEnabled = false,
            // 开插帧后镜头必须按物理步更新；显式写出来，免得引擎自己改并打一条警告。
            ProcessCallback = Camera2D.Camera2DProcessCallback.Physics,
        };
        AddChild(_camera);
        _camera.MakeCurrent();
        GetViewport().SizeChanged += OnViewportResized;

        await LoadStageAsync("正在准备界面…", 0.78f);
        SetUpHud();
        await LoadStageAsync("正在摆放面板…", 0.84f);
        SetUpHudPages();

        await LoadStageAsync("正在布置地图…", 0.9f);

        var arguments = OS.GetCmdlineUserArgs();

        var scaleIndex = Array.IndexOf(arguments, "--texscale");
        if (scaleIndex >= 0 && scaleIndex + 1 < arguments.Length &&
            float.TryParse(arguments[scaleIndex + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var textureScale))
        {
            EffectSprite.TextureScale = Math.Clamp(textureScale, 0.25f, 1f);
            GD.Print($"[effect] 特效条带采样比例 {EffectSprite.TextureScale:P0}");
        }

        var facing = "south";
        var faceIndex = Array.IndexOf(arguments, "--face");
        if (faceIndex >= 0 && faceIndex + 1 < arguments.Length) facing = arguments[faceIndex + 1];

        var mapIndex = Array.IndexOf(arguments, "--map");
        if (TitleResume is { } titleResume)
        {
            if (LoadFromSlot(titleResume) is { } titleError)
                GD.PushWarning($"[title] 读档失败，退回新开局：{titleError}");
            else
                GD.Print($"[title] 已从{SaveGameStore.SaveSlotLabel(titleResume)}继续");
        }
        else if (TitleNewSlot is { } titleSlot)
        {
            _currentSaveSlot = new SaveSlotRef(titleSlot);
        }

        var continueIndex = Array.IndexOf(arguments, "--continue");
        if (TitleResume is null && continueIndex >= 0)
        {
            if (continueIndex + 1 >= arguments.Length ||
                !SaveSlotRef.TryParse(arguments[continueIndex + 1], out var resumeSlot))
            {
                GD.PushWarning("[continue] 槽号无效，退回新开局");
            }
            else if (LoadFromSlot(resumeSlot) is { } resumeError)
            {
                GD.PushWarning($"[continue] 读档失败，退回新开局：{resumeError}");
            }
            else
            {
                GD.Print($"[continue] 已从{SaveGameStore.SaveSlotLabel(resumeSlot)}继续");
            }
        }

        if (continueIndex < 0 && mapIndex >= 0 && mapIndex + 1 < arguments.Length)
        {
            var mapId = arguments[mapIndex + 1];
            var debugMap = _loader.LoadMap(mapId);
            var center = new Vec2(debugMap.Width / 2f, debugMap.Height / 2f);
            LoadMap(mapId, FindStandable(new CollisionGrid(debugMap), center), facing);
        }
        else if (_map is null)
        {
            LoadMap(StartMapId, StartPosition, facing);
        }

        await LoadStageAsync("正在布置场景…", 0.97f);
        FinishWorldLoad();

        if (Array.IndexOf(arguments, "--exitcheck") >= 0) BeginExitCheck();
        if (Array.IndexOf(arguments, "--interactcheck") >= 0) BeginInteractCheck();
        if (Array.IndexOf(arguments, "--npccheck") >= 0) BeginNpcFunctionCheck();
        if (Array.IndexOf(arguments, "--allycheck") >= 0) BeginAllyCheck();
        if (Array.IndexOf(arguments, "--allyspacecheck") >= 0) BeginAllySpaceCheck();
        if (Array.IndexOf(arguments, "--settlecheck") >= 0) BeginSettleCheck();
        if (Array.IndexOf(arguments, "--overheadcheck") >= 0) BeginOverheadCheck();
        if (Array.IndexOf(arguments, "--soundcheck") >= 0) BeginSoundCheck();
        if (Array.IndexOf(arguments, "--defercheck") >= 0) BeginDeferCheck();
        if (Array.IndexOf(arguments, "--portraitcheck") >= 0) BeginPortraitCheck();
        if (Array.IndexOf(arguments, "--cinematiccheck") >= 0) BeginCinematicCheck();
        if (Array.IndexOf(arguments, "--buffcheck") >= 0) BeginBuffCheck();
        if (Array.IndexOf(arguments, "--placementcheck") >= 0) BeginPlacementCheck();
        if (Array.IndexOf(arguments, "--savecheck") >= 0) BeginSaveCheck();
        if (Array.IndexOf(arguments, "--switchcheck") >= 0) BeginSwitchCheck();
        if (Array.IndexOf(arguments, "--storyblockcheck") >= 0) BeginStoryBlockCheck();
        if (Array.IndexOf(arguments, "--popupcheck") >= 0) BeginPopupCheck();
        if (Array.IndexOf(arguments, "--popupshot") >= 0) BeginPopupShot();
        if (Array.IndexOf(arguments, "--controlcheck") >= 0) BeginControlCheck();
        if (Array.IndexOf(arguments, "--recoverycheck") >= 0) BeginRecoveryCheck();
        if (Array.IndexOf(arguments, "--nearcheck") >= 0) BeginNearCheck();
        if (Array.IndexOf(arguments, "--speedcheck") >= 0) BeginSpeedCheck();
        if (Array.IndexOf(arguments, "--closecheck") >= 0) BeginCloseCheck();
        if (Array.IndexOf(arguments, "--backcheck") >= 0) BeginBackCheck();
        if (Array.IndexOf(arguments, "--unpackcheck") >= 0) BeginUnpackCheck();
        var texCheck = Array.IndexOf(arguments, "--texcheck");
        if (texCheck >= 0)
        {
            var frames = 0;
            if (texCheck + 1 < arguments.Length) int.TryParse(arguments[texCheck + 1], out frames);
            BeginTexCheck(frames);
        }
        var effectShot = Array.IndexOf(arguments, "--effectshot");
        if (effectShot >= 0)
            BeginEffectShot(effectShot + 1 < arguments.Length ? arguments[effectShot + 1] : "hellsword-swing");
        if (Array.IndexOf(arguments, "--quickcheck") >= 0) BeginQuickCheck();
        if (Array.IndexOf(arguments, "--pagecheck") >= 0) BeginPageCheck();
        if (Array.IndexOf(arguments, "--resumecheck") >= 0) BeginResumeCheck();
        if (Array.IndexOf(arguments, "--triggercheck") >= 0) BeginTriggerCheck();
        if (Array.IndexOf(arguments, "--storyflowcheck") >= 0) BeginStoryFlowCheck();
        if (Array.IndexOf(arguments, "--exitdiagnose") >= 0) BeginExitDiagnose();
        if (Array.IndexOf(arguments, "--loadingshot") >= 0)
            _loading?.Show("正在前往新的地方…", 0.35f);
        if (Array.IndexOf(arguments, "--craftcheck") >= 0) BeginCraftCheck();
        if (Array.IndexOf(arguments, "--chestcheck") >= 0) BeginChestCheck();
        if (Array.IndexOf(arguments, "--storycheck") >= 0) BeginStoryCheck();
        if (Array.IndexOf(arguments, "--encountercheck") >= 0) BeginEncounterCheck();
        if (Array.IndexOf(arguments, "--encounterrecover") >= 0) BeginEncounterRecover();
        if (Array.IndexOf(arguments, "--actorremovecheck") >= 0) BeginActorRemoveCheck();
        if (Array.IndexOf(arguments, "--flighteffectcheck") >= 0) RunFlightEffectCheck();
        if (Array.IndexOf(arguments, "--basicvariantcheck") >= 0) RunBasicVariantCheck();
        var encounterPlay = Array.IndexOf(arguments, "--encounterplay");
        if (encounterPlay >= 0 && encounterPlay + 1 < arguments.Length)
            BeginEncounterPlay(arguments[encounterPlay + 1]);
        if (Array.IndexOf(arguments, "--enemycheck") >= 0) BeginEnemyCheck();
        if (Array.IndexOf(arguments, "--bagpagecheck") >= 0) BeginBagPageCheck();
        if (Array.IndexOf(arguments, "--targetcheck") >= 0) BeginTargetCheck();
        if (Array.IndexOf(arguments, "--settingscheck") >= 0) BeginSettingsCheck();
        if (Array.IndexOf(arguments, "--skillcheck") >= 0) RunSkillCheck();
        if (Array.IndexOf(arguments, "--skillplaycheck") >= 0) BeginSkillPlayCheck();
        if (Array.IndexOf(arguments, "--enemycastcheck") >= 0) BeginEnemyCastCheck();
        if (Array.IndexOf(arguments, "--conditioncheck") >= 0) RunConditionCheck();
        if (Array.IndexOf(arguments, "--reactioncheck") >= 0) RunReactionCheck();
        if (Array.IndexOf(arguments, "--layercheck") >= 0) RunLayerCheck();
        if (Array.IndexOf(arguments, "--turncheck") >= 0) RunTurnCheck();
        if (Array.IndexOf(arguments, "--pickupcheck") >= 0) RunPickupCheck();
        if (Array.IndexOf(arguments, "--flightcheck") >= 0) RunFlightCheck();
        if (Array.IndexOf(arguments, "--noticecheck") >= 0) RunNoticeCheck();
        if (Array.IndexOf(arguments, "--colorcheck") >= 0) RunColorCheck();
        if (Array.IndexOf(arguments, "--wardrobecheck") >= 0) RunWardrobeCheck();
        if (Array.IndexOf(arguments, "--stageoutfit") >= 0) RunStageOutfitCheck();
        if (Array.IndexOf(arguments, "--objectcheck") >= 0) RunWorldObjectCheck();
        if (Array.IndexOf(arguments, "--attackcheck") >= 0) BeginPlayerAttackCheck();
        if (Array.IndexOf(arguments, "--attackrepeatcheck") >= 0) BeginAttackRepeatCheck();
        if (Array.IndexOf(arguments, "--attackanimcheck") >= 0) BeginAttackAnimCheck();
        if (Array.IndexOf(arguments, "--attackcyclecheck") >= 0) BeginAttackCycleCheck();
        if (Array.IndexOf(arguments, "--frenzycheck") >= 0) BeginFrenzyCheck();
        if (Array.IndexOf(arguments, "--attackshot") >= 0) BeginAttackShot();
        if (Array.IndexOf(arguments, "--dualcheck") >= 0) BeginDualCheck();
        if (Array.IndexOf(arguments, "--bannercheck") >= 0) BeginNameBannerCheck();
        if (Array.IndexOf(arguments, "--receiptcheck") >= 0) BeginReceiptCheck();
        if (Array.IndexOf(arguments, "--interpcheck") >= 0) BeginInterpCheck();
        if (Array.IndexOf(arguments, "--cooldowncheck") >= 0) BeginCooldownCheck();
        if (Array.IndexOf(arguments, "--cooldownshot") >= 0) BeginCooldownShot();
        if (Array.IndexOf(arguments, "--defeatcheck") >= 0) BeginDefeatCheck();
        if (Array.IndexOf(arguments, "--lampcheck") >= 0) BeginLampCheck();

        if (Array.IndexOf(arguments, "--bag") >= 0)
        {
            ToggleBag();
            if (Array.IndexOf(arguments, "--bagselect") >= 0 && _bag.Entries.Count > 0)
                _bagPanel?.SelectById(_bag.Entries[0].Id);

            // --bagpage <n>：塞够三页物品后翻到第 n 页（0 起），配 --shot 核对翻页条版面。
            // 必须用**不可堆叠**的物品：可堆叠的 36 瓶药水会全堆进同一格，页数仍是一页。
            var pageIndex = Array.IndexOf(arguments, "--bagpage");
            if (pageIndex >= 0 && pageIndex + 1 < arguments.Length &&
                int.TryParse(arguments[pageIndex + 1], out var wantedPage))
            {
                var sample = _loader.Items.Items
                    .FirstOrDefault(pair => pair.Value.Category == "equipment" && !pair.Value.Stackable);
                if (sample.Key is null) GD.PushWarning("[bagpage] 内容里找不到不可堆叠的物品");
                else
                    for (var index = 0; index < InventoryPaging.PageSize * 3; index += 1)
                        _bag.Add(sample.Key, 1);

                for (var turn = 0; turn < wantedPage; turn += 1) _bagPanel?.DebugTurnPage(1);
                GD.Print($"[bagpage] {_bagPanel?.DebugPageLabel}（页号 {_bagPanel?.DebugPage}，" +
                         $"格数 {_bagPanel?.DebugSlotCount}）");
            }
            if (Array.IndexOf(arguments, "--baggear") >= 0 && _bag.WornInstances.Count > 0)
                _bagPanel?.SelectById(_bag.WornInstances.Values.First().Id, "right");

            if (Array.IndexOf(arguments, "--bagstep") >= 0)
            {
                var target = _bag.Entries.FirstOrDefault(entry => entry.Quantity > 1) ?? _bag.Entries.FirstOrDefault();
                if (target is not null) _bagPanel?.BeginDiscard(target.Id);
            }

            // --baguse [物品]：直接使用一件物品（默认卓越灵药）。原版这处的结果明细为空，
            // 不能因此把左侧角色与装备槽整块省掉——东西用完之后左栏必须照常画出来。
            if (Array.IndexOf(arguments, "--baguse") >= 0)
            {
                var useIndex = Array.IndexOf(arguments, "--baguse");
                var useId = useIndex + 1 < arguments.Length && !arguments[useIndex + 1].StartsWith("--")
                    ? arguments[useIndex + 1]
                    : "supreme-elixir";
                var result = _bagPanel?.DebugUse(useId, addIfMissing: true);
                var drawn = _bagPanel?.LeftColumnDrawn == true;
                var usedUp = _bagPanel?.DebugUsedUp == true;
                // 用光了左栏必须照常画；还剩几件时说明面板盖住左栏是原版行为。
                var ok = result is not null && (!usedUp || drawn);
                GD.Print($"[baguse] {useId}：使用{(result is null ? "未执行" : "成功")}，" +
                         $"{(usedUp ? "已用光" : "还有剩余")}，左栏{(drawn ? "在" : "被说明面板盖住")}，" +
                         $"{(ok ? "符合原版" : "异常：左栏不该消失")}");
            }

            if (Array.IndexOf(arguments, "--bagdrag") >= 0 && _bag.Entries.Count > 0)
            {
                var moving = _bag.Entries[0].Id;
                var moved = _bagPanel?.DebugDrag(moving, InventoryViewKind.All, 12) ?? false;
                GD.Print($"[bagdrag] {moving} → 第 12 格：{(moved ? "到位" : "没动")}");
            }

            if (Array.IndexOf(arguments, "--bagshortcut") >= 0 && _bag.Entries.Count > 0)
            {
                _bagPanel?.DebugShortcut(_bag.Entries[0].Id);
                var assignIndex = Array.IndexOf(arguments, "--bagassign");
                if (assignIndex >= 0 && assignIndex + 1 < arguments.Length &&
                    int.TryParse(arguments[assignIndex + 1], out var slot))
                    _bagPanel?.DebugAssign(slot);
            }
        }

        if (Array.IndexOf(arguments, "--skills") >= 0)
        {
            // 先给一点技能点，否则「学习」永远是禁用态，截图看不出可用态。
            for (var index = 0; index < 5; index += 1) _hero?.GrantSkillPoint();
            ToggleSkills();

            var selectIndex = Array.IndexOf(arguments, "--skillsel");
            if (selectIndex >= 0 && selectIndex + 1 < arguments.Length)
                _skillPanel?.SelectById(arguments[selectIndex + 1]);

            if (Array.IndexOf(arguments, "--skilllearn") >= 0)
                GD.Print($"[skill] 学习：{(_skillPanel?.LearnSelected() == true ? "成功" : "被拒")}");

            if (Array.IndexOf(arguments, "--skillshortcut") >= 0)
            {
                var target = Array.IndexOf(arguments, "--skillsel") is var at && at >= 0 && at + 1 < arguments.Length
                    ? arguments[at + 1]
                    : _skillPanel?.SelectedSkillId;
                if (target is not null) _skillPanel?.OpenShortcutScreen(target);
            }

            var assignIndex = Array.IndexOf(arguments, "--skillassign");
            if (assignIndex >= 0 && assignIndex + 1 < arguments.Length &&
                int.TryParse(arguments[assignIndex + 1], out var slot))
                _skillPanel?.DebugAssign(slot);

            var scrollIndex = Array.IndexOf(arguments, "--skillscroll");
            if (scrollIndex >= 0 && scrollIndex + 1 < arguments.Length &&
                int.TryParse(arguments[scrollIndex + 1], out var offset))
                _skillPanel?.ScrollDescription(offset);
        }

        var menuIndex = Array.IndexOf(arguments, "--menupage");
        if (menuIndex >= 0 && menuIndex + 1 < arguments.Length) ShowMenuPage(arguments[menuIndex + 1]);

        var settingsScroll = Array.IndexOf(arguments, "--settingsscroll");
        if (settingsScroll >= 0 && settingsScroll + 1 < arguments.Length &&
            float.TryParse(arguments[settingsScroll + 1], out var scrollTo))
            _systemPanel?.DebugScrollSettings(scrollTo);

        for (var dropIndex = Array.IndexOf(arguments, "--drop");
             dropIndex >= 0 && dropIndex + 1 < arguments.Length;
             dropIndex = Array.IndexOf(arguments, "--drop", dropIndex + 1))
        {
            if (_map is null) break;
            var spec = arguments[dropIndex + 1].Split(':');
            var count = spec.Length > 1 && int.TryParse(spec[1], out var amount) ? Math.Max(1, amount) : 1;
            if (spec[0] == "money")
                SpawnLootOnMap(_map.Id, _position.X, _position.Y, [new Drops.DropReward.MoneyDrop(count)]);
            else
                SpawnLootOnMap(_map.Id, _position.X, _position.Y,
                    Enumerable.Repeat<Drops.DropReward>(new Drops.DropReward.ItemDrop(spec[0]), count).ToList());
        }

        var styleIndex = Array.IndexOf(arguments, "--movestyle");
        if (styleIndex >= 0 && styleIndex + 1 < arguments.Length)
            _controlSettings.SetMoveStyle(arguments[styleIndex + 1] switch
            {
                "dpad" => MoveControlStyle.Dpad,
                "fourway" => MoveControlStyle.FourWay,
                _ => MoveControlStyle.Stick,
            });

        if (Array.IndexOf(arguments, "--layouteditor") >= 0) OpenControlEditor();

        if (Array.IndexOf(arguments, "--sizescheck") >= 0) BeginSizeCheck();

        var partyTabIndex = Array.IndexOf(arguments, "--partytab");
        if (partyTabIndex >= 0 && partyTabIndex + 1 < arguments.Length)
            _partyPanel?.DebugTab(arguments[partyTabIndex + 1]);
        if (Array.IndexOf(arguments, "--partydismiss") >= 0) _partyPanel?.DebugDismiss();

        if (Array.IndexOf(arguments, "--status") >= 0)
        {
            ToggleStatus();
            if (Array.IndexOf(arguments, "--statusadjust") >= 0)
                GD.Print($"[status] 进加点：{(_statusPanel?.DebugAdjust() == true ? "成功" : "被拒")}");

            var addIndex = Array.IndexOf(arguments, "--statusadd");
            if (addIndex >= 0 && addIndex + 1 < arguments.Length)
            {
                var parts = arguments[addIndex + 1].Split(':');
                var times = parts.Length > 1 && int.TryParse(parts[1], out var count) ? count : 1;
                for (var index = 0; index < times; index += 1) _statusPanel?.DebugChange(parts[0], 1);
                GD.Print($"[status] {parts[0]} +{times} → {_statusPanel?.DescribeAllocation()}");
            }

            if (Array.IndexOf(arguments, "--statuscommit") >= 0)
                GD.Print($"[status] 确认：{(_statusPanel?.DebugCommit() == true ? "写回" : "没东西可确认")} → {_statusPanel?.DescribeAllocation()}");
        }

        if (Array.IndexOf(arguments, "--quests") >= 0)
        {
            var acceptIndex = Array.IndexOf(arguments, "--questaccept");
            if (acceptIndex >= 0 && acceptIndex + 1 < arguments.Length)
                GD.Print($"[quests] 接取 {arguments[acceptIndex + 1]}：{_journal.Accept(arguments[acceptIndex + 1])}");
            ToggleQuests();
        }

        var shopIndex = Array.IndexOf(arguments, "--shop");
        if (shopIndex >= 0)
        {
            var id = shopIndex + 1 < arguments.Length && !arguments[shopIndex + 1].StartsWith("--")
                ? arguments[shopIndex + 1]
                : _loader.Shops.LampShop;
            OpenShop(id);

            var stepIndex = Array.IndexOf(arguments, "--shopstep");
            if (stepIndex >= 0 && stepIndex + 1 < arguments.Length)
            {
                var step = arguments[stepIndex + 1] switch
                {
                    "description" => TradeStep.Description,
                    "quantity" => TradeStep.Quantity,
                    "confirm" => TradeStep.Confirm,
                    "notice" => TradeStep.Notice,
                    _ => TradeStep.Description,
                };
                var kind = Array.IndexOf(arguments, "--shopsell") >= 0 ? TradeKind.Sell : TradeKind.Buy;
                _shopPanel?.DebugTrade(step, kind);
            }
        }

        if (Array.IndexOf(arguments, "--craft") >= 0)
        {
            if (Array.IndexOf(arguments, "--craftgear") >= 0)
            {
                _bag.Add("dagger", 1);
                foreach (var material in _loader.Crafting.Operations[CraftingCatalog.ChaosTier].Materials)
                    _bag.Add(material.Item, material.Quantity * 3);
                _bag.Add("leaf-of-life", 30);
                _bag.Add("novice-jewel", 12);
                _session.GainMoney(200000);
            }

            var machine = _loader.World.Actors.Values.FirstOrDefault(actor => actor.Service == "fusion-machine");
            if (machine is null) GD.PushWarning("内容里没有融合器");
            else OpenCrafting(machine.Id);

            var tabIndex = Array.IndexOf(arguments, "--crafttab");
            if (tabIndex >= 0 && tabIndex + 1 < arguments.Length)
                _craftPanel?.Choose(arguments[tabIndex + 1]);

            var pickIndex = Array.IndexOf(arguments, "--craftpick");
            if (pickIndex >= 0 && pickIndex + 1 < arguments.Length)
                _craftPanel?.DebugPickFirst(arguments[pickIndex + 1]);

            var batchIndex = Array.IndexOf(arguments, "--craftbatch");
            if (batchIndex >= 0 && batchIndex + 1 < arguments.Length &&
                int.TryParse(arguments[batchIndex + 1], out var wantedBatch))
            {
                var ok = _craftPanel?.DebugBatch(wantedBatch) == true;
                GD.Print($"[craftbatch] 摆到 ×{wantedBatch}：{(ok ? "成功" : "失败（配方不支持批量或材料不足）")}");
            }

            if (Array.IndexOf(arguments, "--craftquote") >= 0) _craftPanel?.Begin();

            // 下面这几步都是拿一件装备去混沌，所以先站到「装备 → 混沌合成」那一页上。
            void ToChaos(string tier)
            {
                _craftPanel?.Choose(FusionCatalog.ChaosMenu);
                _craftPanel?.PickTier(tier);
            }

            var stepIndex = Array.IndexOf(arguments, "--craftstep");
            if (stepIndex >= 0 && stepIndex + 1 < arguments.Length)
            {
                switch (arguments[stepIndex + 1])
                {
                    case "select":
                        ToChaos(CraftingCatalog.ChaosTier);
                        _craftPanel?.DebugSelect();
                        break;
                    case "cycle":
                        _bag.Add("dagger", 2);
                        ToChaos(CraftingCatalog.ChaosTier);
                        _craftPanel?.DebugSelect();
                        var firstTarget = _craftPanel?.DebugTarget;
                        var moved = _craftPanel?.DebugCycleTarget(1) == true;
                        var secondTarget = _craftPanel?.DebugTarget;
                        _craftPanel?.DebugCycleTarget(-1);
                        GD.Print($"[craftstep] 目标循环 候选 {_craftPanel?.DebugTargetCount()} 件，" +
                                 $"{firstTarget} → {secondTarget}（切动={moved}）→ 回 {_craftPanel?.DebugTarget}");
                        break;
                    case "confirm":
                        ToChaos(CraftingCatalog.ChaosTier);
                        _craftPanel?.DebugQuote();
                        break;
                    case "epic":
                        ToChaos(CraftingCatalog.EpicChaosTier);
                        var gear = _bag.Entries.FirstOrDefault(entry =>
                            _loader.Items.Get(entry.ItemId).Equipment is not null);
                        if (gear is not null) _craftPanel?.Select(gear.Id);
                        break;
                    case "drag":
                        var moving = _bag.Entries.FirstOrDefault(entry =>
                            _loader.Items.Get(entry.ItemId).Equipment is not null);
                        var dropped = moving is not null && _craftPanel?.DebugDrag(moving.Id) == true;
                        GD.Print($"[craftdrag] {moving?.ItemId ?? "无"} → 合成格：{(dropped ? "到位" : "没动")}");
                        break;
                    case "commit":
                        ToChaos(CraftingCatalog.ChaosTier);
                        if (_craftPanel?.DebugQuote() == true) _craftPanel.Confirm();
                        var crafted = _bag.Entries.FirstOrDefault(entry => entry.Numbers?.Chaos is not null);
                        GD.Print($"[craftcommit] 金币 {_session.Money}，" +
                                 $"目标混沌 {crafted?.Numbers?.Chaos?.ValuePercent.ToString() ?? "无"}%，" +
                                 $"秘银 {_bag.Count("mithril")}");
                        break;
                    // 选一件背包里没有的装备：走的是同一条「不能合成」通路，只为截下提示横带。
                    case "message":
                        ToChaos(CraftingCatalog.ChaosTier);
                        _craftPanel?.Select("missing-item");
                        break;
                }
            }
        }

        var auditIndex = Array.IndexOf(arguments, "--texaudit");
        if (auditIndex >= 0)
        {
            var count = auditIndex + 1 < arguments.Length && int.TryParse(arguments[auditIndex + 1], out var parsed)
                ? parsed
                : 6;
            RunTextureAudit(count);
            return;
        }

        var auditStoryIndex = Array.IndexOf(arguments, "--storyaudit");
        if (auditStoryIndex >= 0)
        {
            var count = auditStoryIndex + 1 < arguments.Length &&
                        int.TryParse(arguments[auditStoryIndex + 1], out var scripts)
                ? scripts
                : 60;
            RunStoryAudit(count);
            return;
        }

        var storyIndex = Array.IndexOf(arguments, "--story");
        if (storyIndex >= 0)
        {
            var next = storyIndex + 1 < arguments.Length ? arguments[storyIndex + 1] : null;
            var scriptId = next is not null && !next.StartsWith("--")
                ? next
                : _loader.Campaign.Start.Script ?? "event-0";

            _placementCheck = Array.IndexOf(arguments, "--placementcheck") >= 0;
            PlayStory(scriptId);
            return;
        }

        var autoplayNewGame = !_loadedFromSave &&
                              (!Array.Exists(arguments, argument => argument.StartsWith("--")) ||
                               Array.IndexOf(arguments, "--autoplay") >= 0 && Array.IndexOf(arguments, "--story") < 0);
        if (autoplayNewGame)
        {
            var startScript = _loader.Campaign.Start.Script;
            if (startScript is not null)
            {
                foreach (var trigger in _loader.Campaign.EventTable.Triggers)
                    if (trigger.Source == "start")
                        _journal.MarkEventStarted(trigger.Id);

                GD.Print("[game] 新的一局：先演开场");
                PlayStory(startScript);
            }
        }

        ReportWorldLoad(arguments);
    }

    private const float WorldLoadStartProgress = 0.5f;

    private const int ProgressSettleFrames = 3;

    private sealed record LoadStage(string Label, string Shown, float Progress, bool Visible, ulong Ticks);

    /// <summary>
    /// 走一段加载：更新加载页，然后让出一帧让引擎把它画出来。
    ///
    /// 让出这一帧不是礼貌——就绪整段在主线程上跑，中间不交还控制权的话加载页一次都画不出来，
    /// 玩家看到的就是「点了开始游戏之后画面不动」。素材还在加载，所以逐段报进度。
    /// </summary>
    private async Task LoadStageAsync(string label, float progress)
    {
        if (_loadStages.Count == 0) _loadFirstFrame = Engine.GetFramesDrawn();
        _loading?.Show(label, progress);
        _loadStages.Add(new LoadStage(label, _loading?.Label ?? string.Empty, progress,
            _loading?.Visible == true, Time.GetTicksMsec()));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 进度条还在往这一档滑时再让几帧，让它滑过去而不是单帧跳过去。
        // 让一帧实测只要 5–19 ms（交接词第三节的测法），所以这里拿几帧换观感是划算的。
        for (var frame = 0; frame < ProgressSettleFrames && _loading?.Settling == true; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void FinishWorldLoad()
    {
        var visible = _loading?.Visible == true;
        _loading?.SetProgress(1f);
        _loadStages.Add(new LoadStage("就绪", _loading?.Label ?? string.Empty, 1f, visible, Time.GetTicksMsec()));
        _loading?.HideOverlay();
        _loadFrames = Engine.GetFramesDrawn() - _loadFirstFrame;
        _worldReady = true;
    }

    private void ReportWorldLoad(string[] arguments)
    {
        if (_loadStages.Count < 2) return;

        var total = _loadStages[^1].Ticks - _loadStages[0].Ticks;
        var parts = new List<string>();
        for (var index = 0; index + 1 < _loadStages.Count; index += 1)
            parts.Add($"{_loadStages[index].Label.TrimEnd('…')} {_loadStages[index + 1].Ticks - _loadStages[index].Ticks}ms");
        GD.Print($"[load] 开局就绪 {total} ms，期间画了 {_loadFrames} 帧（{string.Join("，", parts)}）");

        if (Array.IndexOf(arguments, "--loadcheck") < 0) return;

        var problems = new List<string>();
        for (var index = 0; index + 1 < _loadStages.Count; index += 1)
        {
            var stage = _loadStages[index];
            if (!stage.Visible) problems.Add($"「{stage.Label}」这一段加载页没显示");
            if (stage.Shown != stage.Label) problems.Add($"「{stage.Label}」写进加载页的是「{stage.Shown}」");
            if (index > 0 && stage.Progress < _loadStages[index - 1].Progress)
                problems.Add($"进度回头了：{_loadStages[index - 1].Progress} → {stage.Progress}");
        }

        if (!(_loading?.Dismissing ?? true)) problems.Add("加载结束后没有收起");
        _loading?.CompleteFade();

        if (_loadStages.Count < 5) problems.Add($"只走了 {_loadStages.Count} 段，分帧没生效");
        if (total < 100) problems.Add($"总耗时只有 {total} ms，看起来没真的加载");
        // 加载期间必须真出帧：一帧都没有就说明加载页根本没被画出来（整段又挤回一帧了）。
        // 每段结尾让出一帧，收尾那次采样会差一帧，所以按「段数 - 2」兜底。
        // 无头模式没有渲染帧（恒为 0），这一条只在有渲染时生效。
        var expectedFrames = Math.Max(1, _loadStages.Count - 2);
        if (_loadFrames > 0 && _loadFrames < expectedFrames)
            problems.Add($"加载期间只画了 {_loadFrames} 帧，至少该有 {expectedFrames} 帧");

        foreach (var problem in problems) GD.Print($"[loadcheck] 问题：{problem}");
        GD.Print($"[loadcheck] {_loadStages.Count - 1} 段，共 {total} ms，期间 {_loadFrames} 帧，" +
                 $"每段可见={_loadStages.Take(_loadStages.Count - 1).All(stage => stage.Visible)}，" +
                 $"结束后收起={_loading is not { Visible: true }}");
        GD.Print(problems.Count == 0 ? "[loadcheck] 通过" : "[loadcheck] 失败");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private void RunStoryAudit(int count)
    {
        var stoppedAt = new Dictionary<string, int>();
        var stoppedIds = new Dictionary<string, List<string>>();
        var ids = _loader.Campaign.Scripts.Keys.Take(count).ToList();

        void Note(string reason, string id)
        {
            stoppedAt[reason] = stoppedAt.GetValueOrDefault(reason) + 1;
            if (!stoppedIds.TryGetValue(reason, out var list)) stoppedIds[reason] = list = [];
            list.Add(id);
        }

        foreach (var id in ids)
        {
            _story.Start(id);
            var guard = 0;

            // 审计是「检查剧本状态机能否走通」的工具，不是运行时：某段剧本的命令在干净会话上
            // 执行失败（例如试图接取一个还不可接取的任务）要记成这段的结局，而不是让异常
            // 穿透整个审计循环。运行时语义仍由执行器的整批事务保证（失败回滚并抛出）。
            try
            {
                while (guard++ < 20000)
                {
                    var step = _story.Next();
                    if (step.Kind == StoryStepKind.End)
                    {
                        var reason = step.Note ?? "正常结束";
                        stoppedAt[reason] = stoppedAt.GetValueOrDefault(reason) + 1;
                        break;
                    }
                    if (step.Kind == StoryStepKind.Encounter) _story.ResolveEncounter(true);
                }
            }
            catch (Exception error)
            {
                var reason = error.Message.Contains("任务尚不可接取")
                    || error.Message.Contains("任务目标尚未达成")
                    || error.Message.Contains("交付物品") && error.Message.Contains("不足")
                    ? "前置未满足（审计未预置现场表与背包）"
                    : $"命令失败：{error.Message}";
                Note(reason, id);
                if (id.StartsWith("event-")) GD.Print($"[storyaudit] 事件 {id} 停在：{error.Message}");
                continue;
            }

            if (guard >= 20000) Note("超过步数上限", id);
        }

        GD.Print($"[storyaudit] 跑了 {ids.Count} 段剧本：");
        foreach (var (reason, total) in stoppedAt.OrderByDescending(pair => pair.Value))
            GD.Print($"[storyaudit]   {total,4}  {reason}");

        foreach (var (reason, list) in stoppedIds.OrderByDescending(pair => pair.Value.Count))
        {
            GD.Print($"[storyaudit] {reason}：{string.Join(" ", list.Take(120))}");
        }

        var unsupported = _unsupportedStoryTypes.ToList();
        if (unsupported.Count > 0)
            GD.Print($"[storyaudit] 演出缺类型：{string.Join(", ", unsupported)}");

        QuitGame();
    }

    public override void _ExitTree()
    {
        _heroLease?.Dispose();
        _mapLease?.Dispose();
        _outfits?.Dispose();
        _outfits = null;
        _audio?.Shutdown();
        _audio = null;
        PanelGfx.UiSound = null;
    }

    /// <summary>
    /// 退出游戏。音频要先收尾再交给引擎：Godot 退出时先清节点、后清 Resource，
    /// 正在循环的 MP3 流要到播放器停下之后才回收，否则会被算成 ObjectDB 泄漏。
    /// </summary>
    private void QuitGame(int exitCode = 0)
    {
        _audio?.Shutdown();
        _audio = null;
        GetTree().Quit(exitCode);
    }

    /// <summary>租约回归：连续切若干张图再原路走回来，贴图数必须回到基线。
    /// 回不去就说明有租约没还——这正是「切图不释放」那个洞的守门用例。
    /// </summary>
    private void RunTextureAudit(int mapCount)
    {
        var baseline = _textures.Count;

        var ids = new List<string>();
        var scanned = 0;
        foreach (var entry in _loader.World.Maps)
        {
            if (ids.Count >= mapCount || ++scanned > 120) break;
            if (_loader.LoadMap(entry.Id).Features.Length >= 20) ids.Add(entry.Id);
        }

        GD.Print($"[tex] 基线 {baseline} 张（{_textures.ImageCount} 张留像素），往返 {ids.Count} 张图：" +
                 string.Join(", ", ids));

        foreach (var id in ids.Concat(ids.AsEnumerable().Reverse()))
        {
            var map = _loader.LoadMap(id);
            var spawn = FindStandable(new CollisionGrid(map), new Vec2(map.Width / 2f, map.Height / 2f));
            LoadMap(id, spawn, "south");
        }

        LoadMap(StartMapId, StartPosition, "south");

        var after = _textures.Count;
        var passed = after <= baseline;
        GD.Print($"[tex] {ids.Count * 2} 次切图后 {after} 张（{_textures.ImageCount} 张留像素），" +
                 $"{(passed ? "通过" : "泄漏")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static Vec2 FindStandable(CollisionGrid grid, Vec2 preferred)
    {
        if (grid.CanStandOnTerrain(preferred.X, preferred.Y)) return preferred;

        for (var ring = 1; ring < 64; ring++)
        {
            for (var step = 0; step < ring * 8; step++)
            {
                var angle = step / (ring * 8f) * Mathf.Tau;
                var candidate = new Vec2(
                    preferred.X + Mathf.Cos(angle) * ring * grid.TileSize,
                    preferred.Y + Mathf.Sin(angle) * ring * grid.TileSize);

                if (grid.CanStandOnTerrain(candidate.X, candidate.Y)) return candidate;
            }
        }

        return preferred;
    }

    private void BeginExitCheck()
    {
        _exitCheck = true;
        if (_map.Exits.Length == 0)
        {
            GD.Print("[exitcheck] 这张图没有出口");
            GetTree().Quit(1);
            return;
        }

        var nearest = _map.Exits
            .OrderBy(exit => GoalQuery.Distance(_position, new Vec2(exit.X + exit.Width / 2f, exit.Y + exit.Height / 2f)))
            .First();

        var target = new Vec2(nearest.X + nearest.Width / 2f, nearest.Y + nearest.Height / 2f);
        _route = new WalkRoute(_grid, _position, target, MovementSpeed(PlayerCombatId, PlayerSpeed));
        GD.Print($"[exitcheck] 自动走向最近的出口 ({target.X:F0}, {target.Y:F0}) -> {nearest.Destination}");
    }

    /// <summary>
    /// 无头验证演出期间的触发排队：演出中踩进区域、
    /// 或演出中条件才成立的触发不会丢——先记进待办，收尾后按当时的世界状态补演一次，
    /// 且只补一次（占用／边沿照常记账，不会自循环）。
    ///
    /// 跑法：<c>--map leap-gate-district-a474941a --defercheck</c>。这张图上
    /// <c>event-74</c> 是 repeat 的区域事件，条件是 quest-57 进行中。
    /// </summary>
    private async void BeginDeferCheck()
    {
        _hold = true;
        _journal.Apply(QuestCommand.Accept("quest-57", source: "debug"));

        async Task Wait(int frames)
        {
            for (var index = 0; index < frames; index += 1)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        _storyPlaying = true;
        _session.Moved(new Vec2(240f, 376f), "south");
        _player.Position = new Vector2(240f, 376f);
        _route = null;
        _combatApproach = null;

        await Wait(16);
        var queued = _deferredTriggers.Contains("event-74");
        var notStarted = !_journal.EventStarted("event-74");
        GD.Print($"[defercheck] 演出中排队={queued}（待办 {_deferredTriggers.Count} 项），" +
                 $"尚未开演={notStarted}，起演次数={_debugTriggerFires}");

        _storyPlaying = false;
        var fired = false;
        for (var frame = 0; frame < 120; frame += 1)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (_debugTriggerFires < 1) continue;
            fired = true;
            break;
        }

        GD.Print($"[defercheck] 收尾后补演={fired}，起演次数={_debugTriggerFires}");

        // ③ 不能自循环：人还站在区域里，但占用已记，不该再演第二次。
        await Wait(120);
        var total = _debugTriggerFires;
        GD.Print($"[defercheck] 再等 120 帧，起演次数={total}（应为 1）");

        var ok = queued && notStarted && fired && total == 1;
        GD.Print($"[defercheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// 无头验证界面音效的派发：原版所有按钮都经 <c>v.button</c> 先 emit <c>UI_EVENTS.select</c>，
    /// 由 的总线统一出声；这里按存档行（主行＝存档、行内「删除」）核对
    /// 那一声真的发出去了，顺带确认内容里确实有这个音效。
    /// </summary>
    private async void BeginSoundCheck()
    {
        var problems = new List<string>();
        if (_audio is null)
        {
            GD.Print("[soundcheck] 音频层没建出来");
            GetTree().Quit(1);
            return;
        }

        // ① 目录里得有 select，否则下面即使调到了也是静默。
        var select = _loader.Audio.Ui.Of("select");
        GD.Print($"[soundcheck] ui.select = '{(select.Length > 0 ? select : "<缺失>")}'");
        if (select.Length == 0) problems.Add("audio.json 的 ui.select 没解析出来");

        var host = new Control { Name = "SoundCheckHost" };
        AddChild(host);
        var fired = new List<string>();
        SaveRowView.Draw(
            path => _heroLease.Texture(path), null, null, 0f, 0f,
            new SaveRowView.Row(new SlotSummaryReading.Empty(), () => fired.Add("save"), "存档槽 1", false,
                () => fired.Add("delete")), host);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var buttons = host.FindChildren("*", "Button", true, false).OfType<Button>().ToList();
        GD.Print($"[soundcheck] 存档行按钮 {buttons.Count} 个（主行 + 行内控件）");
        if (buttons.Count < 2) problems.Add($"存档行只画出 {buttons.Count} 个按钮，至少该有主行与删除");

        for (var index = 0; index < buttons.Count; index += 1)
        {
            var before = _audio.UiCount;
            var actionsBefore = fired.Count;
            buttons[index].EmitSignal(BaseButton.SignalName.Pressed);
            var sound = _audio.LastUi;
            GD.Print($"[soundcheck] 按下按钮 {index}：音效 '{sound}'，累计 {_audio.UiCount}（按下前 {before}）");
            if (_audio.UiCount != before + 1 || sound != "select")
                problems.Add($"按钮 {index} 该发一次 select，实为 '{sound}'（{before}→{_audio.UiCount}）");
            if (fired.Count != actionsBefore + 1)
                problems.Add($"按钮 {index} 的动作没执行");
        }

        host.QueueFree();

        ToggleBag();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var drinks = _bag.PouchCells[InventoryPouch.Consumable];
        var source = drinks.FirstOrDefault(entry => entry is not null);
        if (source is null)
        {
            problems.Add("背包里没有可移动的物品");
        }
        else
        {
            var target = drinks.Count;
            var before = _audio.UiCount;
            var moved = _bagPanel?.DebugMoveToCell(InventoryViewKind.Consumable, target, source.Id) == true;
            var sound = _audio.LastUi;
            GD.Print($"[soundcheck] 背包落格：移动={moved}，音效 '{sound}'，累计 {_audio.UiCount}（按下前 {before}）");
            if (!moved) problems.Add("落格没有成功");
            if (sound != "itemMove" || _audio.UiCount != before + 1)
                problems.Add($"落格该发一次 itemMove，实为 '{sound}'（{before}→{_audio.UiCount}）");
        }

        ToggleBag();

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[soundcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[soundcheck] 通过：存档行主钮与删除钮都先发 select，背包落格发 itemMove");
        GetTree().Quit(0);
    }

    private async void BeginOverheadCheck()
    {
        var problems = new List<string>();
        var skin = _loader.StatusHud.Overhead;

        async Task Settle(int frames = 3)
        {
            for (var index = 0; index < frames; index++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (_overhead is null || _hero is null)
        {
            GD.Print("[overheadcheck] 主角头顶条或角色卡没建出来");
            GetTree().Quit(1);
            return;
        }

        var heroMax = _hero.Player.MaxHealth;
        _hero.SetHealth(heroMax);
        await Settle();
        var anchor = _player.Definition.HeadAnchor;
        GD.Print($"[overheadcheck] 主角条锚点 {anchor?.X ?? float.NaN:F0},{anchor?.Y ?? float.NaN:F0}，" +
                 $"落点 ({_overhead.Position.X:F0},{_overhead.Position.Y:F0})，可见 {_overhead.Shown}，" +
                 $"满血宽 {_overhead.HealthFillWidth:F0}/{skin.Health.Width:F0}");
        if (anchor is null) problems.Add("主角没有 headAnchor");
        else if (MathF.Abs(_overhead.Position.X - anchor.X) > 0.01f ||
                 MathF.Abs(_overhead.Position.Y - anchor.Y) > 0.01f)
            problems.Add($"条没落在锚点上（{_overhead.Position.X:F0},{_overhead.Position.Y:F0}）");
        if (!_overhead.Shown) problems.Add("世界可操控时主角条没显示");
        if (MathF.Abs(_overhead.HealthFillWidth - skin.Health.Width) > 0.01f)
            problems.Add($"满血宽应为 {skin.Health.Width}，实为 {_overhead.HealthFillWidth}");

        _hero.SetHealth(heroMax / 2);
        await Settle();
        var halfExpected = MathF.Floor(skin.Health.Width / 2f);
        GD.Print($"[overheadcheck] 半血宽 {_overhead.HealthFillWidth:F0}（应为 {halfExpected:F0}）");
        if (MathF.Abs(_overhead.HealthFillWidth - halfExpected) > 0.01f)
            problems.Add($"半血宽应为 {halfExpected:F0}，实为 {_overhead.HealthFillWidth:F0}");

        _hero.SetHealth(0);
        await Settle();
        GD.Print($"[overheadcheck] 零血宽 {_overhead.HealthFillWidth:F0}");
        if (_overhead.HealthFillWidth > 0.01f) problems.Add("零血时血条该是空的");
        _hero.SetHealth(heroMax);
        await Settle();

        ToggleBag();
        await Settle();
        if (_overhead.Shown) problems.Add("菜单打开时主角条没收起");
        ToggleBag();
        await Settle();
        if (!_overhead.Shown) problems.Add("关掉菜单后主角条没回来");

        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            problems.Add("内容里没有剧情伙伴，队友条没法验");
        }
        else
        {
            try
            {
                _party?.JoinStory(definitionId);
            }
            catch (Exception error)
            {
                problems.Add($"伙伴入队失败：{error.Message}");
            }

            await Settle();
            SyncAllies();
            await Settle();
            if (_allies.FirstOrDefault() is not { } ally)
            {
                problems.Add("伙伴实体没上场");
            }
            else
            {
                var member = _party?.Get(ally.MemberId);
                var expected = member is null
                    ? 0f
                    : GaugeRules.Width(member.Player.Health, member.Player.MaxHealth, skin.Health.Width);
                GD.Print($"[overheadcheck] 队友条 {ally.MemberId} 锚点 " +
                         $"{ally.Node.Definition.HeadAnchor?.X ?? float.NaN:F0}," +
                         $"{ally.Node.Definition.HeadAnchor?.Y ?? float.NaN:F0}，" +
                         $"落点 ({ally.Overhead.Position.X:F0},{ally.Overhead.Position.Y:F0})，" +
                         $"可见 {ally.Overhead.Shown}，宽 {ally.Overhead.HealthFillWidth:F0}（应为 {expected:F0}）");
                if (ally.Node.Definition.HeadAnchor is null) problems.Add("队友没有 headAnchor");
                else if (MathF.Abs(ally.Overhead.Position.X - ally.Node.Definition.HeadAnchor.X) > 0.01f ||
                         MathF.Abs(ally.Overhead.Position.Y - ally.Node.Definition.HeadAnchor.Y) > 0.01f)
                    problems.Add("队友条没落在锚点上");
                if (MathF.Abs(ally.Overhead.HealthFillWidth - expected) > 0.01f)
                    problems.Add($"队友血条宽应为 {expected:F0}，实为 {ally.Overhead.HealthFillWidth:F0}");
                if (!ally.Overhead.Shown) problems.Add("非演出状态下队友条没显示");
            }
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[overheadcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[overheadcheck] 通过：主角与队友都画在头顶锚点上，宽度随血量，菜单时主角收起");
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0) return;
        GetTree().Quit(0);
    }

    private async void BeginSettleCheck()
    {
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            GD.Print("[settlecheck] 内容里没有伙伴定义");
            GetTree().Quit(1);
            return;
        }

        _party?.JoinStory(definitionId);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();

        var ally = _allies.FirstOrDefault();
        if (ally is null)
        {
            GD.Print("[settlecheck] 伙伴实体没有上场");
            GetTree().Quit(1);
            return;
        }

        Vec2? far = null;
        var directions = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) };
        foreach (var (dx, dy) in directions)
        {
            for (var offset = 160f; offset <= 520f && far is null; offset += 8f)
            {
                var candidate = new Vec2(_position.X + dx * offset, _position.Y + dy * offset);
                var snapped = NearestSummonSpot(candidate, ally.Node.Definition.Collision);
                if (snapped is null || GoalQuery.Distance(snapped.Value, _position) < 160f) continue;
                far = snapped;
            }
            if (far is not null) break;
        }

        if (far is not { } spot)
        {
            GD.Print("[settlecheck] 主角周围找不到 160px 外的合法落点");
            GetTree().Quit(1);
            return;
        }

        // AI 不能处在追击／出手里，否则收拢按原版规则会让开。
        ally.Ai.Cancel();
        ally.Node.Position = new Vector2(spot.X, spot.Y);
        var before = DistanceToPlayer(ally);

        SettleFriendlyUnitsAroundHero();
        var after = DistanceToPlayer(ally);

        // 第二遍：已经站好的不该再被挪动。
        var settledPosition = ally.Node.Position;
        SettleFriendlyUnitsAroundHero();
        var stillThere = ally.Node.Position.DistanceTo(settledPosition) < 0.5f;

        var ok = before > 64f && after <= 64f && stillThere;
        GD.Print($"[settlecheck] 伙伴在 {before:F0}px 外 → 收拢后 {after:F0}px（应 ≤64）；" +
                 $"第二遍不再挪动={stillThere} → {(ok ? "通过" : "失败")}");
        GetTree().Quit(ok ? 0 : 1);

        float DistanceToPlayer(PartyAlly target) =>
            GoalQuery.Distance(new Vec2(target.Node.Position.X, target.Node.Position.Y), _position);
    }

    /// <summary>
    /// 无头验证伙伴跟随：把内容里第一个剧情伙伴拉进名册，验证实体上场、
    /// 落点贴着主角，主角挪远之后若干帧内跟到身边。
    /// </summary>
    /// <summary>
    /// <c>--allyspacecheck</c>：两个伙伴被摞到同一点之后，轻推分离必须把他们分开。
    /// 这是「伙伴重叠」这一票的回归用例——删掉 <see cref="SeparatePartyAllies"/> 就会失败。
    /// </summary>
    private async void BeginAllySpaceCheck()
    {
        var story = _loader.Companions.Definitions
            .Where(pair => pair.Value.SourceOrDefault == "story")
            .Select(pair => pair.Key)
            .Take(2)
            .ToList();
        if (story.Count < 2)
        {
            GD.Print("[allyspacecheck] 内容里不足两个剧情伙伴");
            GetTree().Quit(1);
            return;
        }

        try
        {
            foreach (var id in story) _party?.JoinStory(id);
        }
        catch (Exception error)
        {
            GD.Print($"[allyspacecheck] 伙伴入队失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();

        var allies = _allies.ToList();
        if (allies.Count < 2)
        {
            GD.Print($"[allyspacecheck] 只上场了 {allies.Count} 个伙伴，没法验证重叠");
            GetTree().Quit(1);
            return;
        }

        var spot = allies[0].Node.Position;
        foreach (var ally in allies) ally.Node.Position = spot;
        var before = (allies[0].Node.Position - allies[1].Node.Position).Length();

        for (var frame = 0; frame < 90; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var after = (allies[0].Node.Position - allies[1].Node.Position).Length();
        GD.Print($"[allyspacecheck] 起点间距 {before:F1}px → 90 帧后 {after:F1}px" +
                 $"（要求 ≥ {AllyMinSpacing - 2f:F0}），主角在 ({_position.X:F0},{_position.Y:F0})");
        if (after >= AllyMinSpacing - 2f)
        {
            GD.Print("[allyspacecheck] 通过");
            if (!_hold) GetTree().Quit();
            return;
        }

        GD.Print("[allyspacecheck] 两个伙伴仍然叠在一起");
        GetTree().Quit(1);
    }

    private async void BeginAllyCheck()
    {
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            GD.Print("[allycheck] 内容里没有伙伴定义");
            GetTree().Quit(1);
            return;
        }

        try
        {
            _party?.JoinStory(definitionId);
        }
        catch (Exception error)
        {
            GD.Print($"[allycheck] 伙伴入队失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();
        var ally = _allies.FirstOrDefault();
        if (ally is null)
        {
            GD.Print("[allycheck] 伙伴实体没有上场");
            GetTree().Quit(1);
            return;
        }

        var startPos = new Vec2(ally.Node.Position.X, ally.Node.Position.Y);
        GD.Print($"[allycheck] 伙伴 {ally.MemberId} 上场于 ({startPos.X:F0}, {startPos.Y:F0})，" +
                 $"主角 ({_position.X:F0}, {_position.Y:F0})，上阵 {_party?.ActiveIds.Count}");

        var moved = false;
        for (var offset = 104f; offset <= 240f; offset += 8f)
        {
            var candidate = new Vec2(_position.X + offset, _position.Y);
            var snapped = NearestSummonSpot(candidate, ally.Node.Definition.Collision);
            if (snapped is not { } spot ||
                GoalQuery.Distance(spot, _position) < 100f) continue;
            ally.Node.Position = new Vector2(spot.X, spot.Y);
            moved = true;
            break;
        }

        if (!moved)
        {
            GD.Print("[allycheck] 主角周围找不到 100px 外的合法落点");
            GetTree().Quit(1);
            return;
        }

        var walkFrames = new HashSet<int>();
        var walkSamples = 0;
        for (var frame = 0; frame < 150; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var layers = ally.Node.Visual.Layers;
            if (layers.Count == 0 || !ally.Node.Visual.Animation.StartsWith("walk-", StringComparison.Ordinal))
                continue;
            walkSamples++;
            walkFrames.Add(layers[0].Frame);
        }

        var endPos = new Vec2(ally.Node.Position.X, ally.Node.Position.Y);
        var after = MathF.Sqrt((endPos.X - _position.X) * (endPos.X - _position.X) +
                               (endPos.Y - _position.Y) * (endPos.Y - _position.Y));
        var state = ally.Ai.CaptureState();
        GD.Print($"[allycheck] 挪远后跟随到 {after:F0}px；AI 状态 {ally.Ai.ActionState()}，" +
                 $"Followed {ally.Port.Followed}，Stops {ally.Port.Stops}，路径 {state.Path.Count} 段，" +
                 $"目的地 {state.Destination?.X ?? -1f:F0},{state.Destination?.Y ?? -1f:F0}，" +
                 $"clock {state.Clock}，nextPathAt {state.NextPathAt}");
        var engaged = ally.Ai.ActionState() is CombatState.Attacking or CombatState.Approaching;
        if (after >= 60f && !engaged)
        {
            GD.Print("[allycheck] 伙伴没有跟上来");
            GetTree().Quit(1);
            return;
        }
        if (engaged) GD.Print("[allycheck] 伙伴被战斗吸住（跟随段按战斗态放行）");

        GD.Print($"[allycheck] 行走采样 {walkSamples} 帧，出现过 {walkFrames.Count} 个不同帧号");
        // 有 bug 时照样能摸到第 1 帧（81 帧采样只出现 2 个帧号），所以门槛要 4 个。
        if (walkSamples >= 12 && walkFrames.Count < 4)
        {
            GD.Print("[allycheck] 行走片段没有推进：走路时脚不会动");
            GetTree().Quit(1);
            return;
        }

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print("[allycheck] 通过（本图没有活怪，战斗段跳过）");
            if (!_hold) GetTree().Quit();
            return;
        }

        var allyMember = _party?.Get(ally.MemberId);
        var allyHealthBefore = allyMember?.Player.Health ?? 0;
        var enemyHealthBefore = _enemies.Where(enemy => !enemy.Dead)
            .Sum(enemy => VitalsOf(enemy.CombatId)?.Health ?? 0);
        var nearEnemy = NearestSummonSpot(new Vec2(target.Position.X, target.Position.Y),
            ally.Node.Definition.Collision) ?? new Vec2(target.Position.X, target.Position.Y);
        ally.Node.Position = new Vector2(nearEnemy.X, nearEnemy.Y);
        GD.Print($"[allycheck] 战斗段：把伙伴放到 {target.EnemyInfo.Id} 旁边，" +
                 $"伙伴生命 {allyHealthBefore}，全场怪生命 {enemyHealthBefore}");

        var allyHurt = false;
        var enemyHurt = false;
        for (var frame = 0; frame < 1500 && !(allyHurt && enemyHurt); frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (frame % 30 != 0) continue;
            var health = allyMember?.Player.Health ?? 0;
            var enemyHealth = _enemies.Where(enemy => !enemy.Dead)
                .Sum(enemy => VitalsOf(enemy.CombatId)?.Health ?? 0);
            if (health < allyHealthBefore) allyHurt = true;
            if (enemyHealth < enemyHealthBefore || target.Dead || _dealtDamage.Contains(ally.MemberId)) enemyHurt = true;
            if (frame % 300 == 0)
            {
                var allyPos = new Vec2(ally.Node.Position.X, ally.Node.Position.Y);
                var distance = GoalQuery.Distance(allyPos, new Vec2(target.Position.X, target.Position.Y));
                GD.Print($"[allycheck] frame {frame}：AI {ally.Ai.ActionState()}，目标 {ally.Ai.SelectedTargetId() ?? "无"}，" +
                         $"距怪 {distance:F0}px，伙伴生命 {health}，全场怪生命 {enemyHealth}");
            }
        }

        var allyHealthAfter = allyMember?.Player.Health ?? 0;
        GD.Print($"[allycheck] 战斗段结果：伙伴生命 {allyHealthBefore}→{allyHealthAfter}，" +
                 $"全场怪生命 {enemyHealthBefore}→{_enemies.Where(enemy => !enemy.Dead).Sum(enemy => VitalsOf(enemy.CombatId)?.Health ?? 0)}，" +
                 $"伙伴 AI {ally.Ai.ActionState()}，反击目标 {ally.Ai.SelectedTargetId() ?? "无"}");
        if (allyHurt || enemyHurt)
        {
            GD.Print($"[allycheck] 通过（{(allyHurt ? "敌人打到了伙伴" : "")}{(allyHurt && enemyHurt ? "，" : "")}{(enemyHurt ? "伙伴反击打到了怪" : "")}）");
            if (!_hold) GetTree().Quit();
        }
        else
        {
            GD.Print("[allycheck] 900 帧内战斗链没有跑通");
            GetTree().Quit(1);
        }
    }

    private async void BeginSaveCheck()
    {
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            GD.Print("[savecheck] 内容里没有伙伴定义");
            GetTree().Quit(1);
            return;
        }

        try
        {
            _party?.JoinStory(definitionId);
        }
        catch (Exception error)
        {
            GD.Print($"[savecheck] 伙伴入队失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();
        var ally = _allies.FirstOrDefault();
        if (ally is null)
        {
            GD.Print("[savecheck] 伙伴实体没有上场");
            GetTree().Quit(1);
            return;
        }

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print("[savecheck] 本图没有活怪，快照往返缺敌人样本");
            GetTree().Quit(1);
            return;
        }

        ally.Ai.OnContact(new AttackContact(target.CombatId, ally.MemberId));
        Conditions.ApplyStateFrames(ally.MemberId, "taunted", 600, target.CombatId);
        target.SetFacing("west");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var allyPosBefore = new Vec2(ally.Node.Position.X, ally.Node.Position.Y);

        var npcSample = _npcs.FirstOrDefault(npc => npc.ChestKind is null && npc.Node.Visible);
        var npcId = string.Empty;
        var npcSavedFacing = "north";
        var npcSavedPos = new Vec2();
        if (npcSample is not null)
        {
            npcId = npcSample.EntityId;
            npcSavedFacing = npcSample.Node.Facing == "south" ? "north" : "south";
            if (NearestSummonSpot(new Vec2(npcSample.Node.Position.X + 24f, npcSample.Node.Position.Y),
                    npcSample.Definition.Collision) is { } npcSpot)
                npcSample.Node.TeleportTo(new Vector2(npcSpot.X, npcSpot.Y));
            npcSample.Node.SetFacing(npcSavedFacing);
            npcSavedPos = new Vec2(npcSample.Node.Position.X, npcSample.Node.Position.Y);
        }

        var captured = CaptureSaveGame();
        if (captured is null)
        {
            GD.Print("[savecheck] 捕获失败");
            GetTree().Quit(1);
            return;
        }
        var sceneCount = captured.World?["scene"]?["actors"]?.AsArray().Count ?? 0;
        var entityCount = captured.World?["generatedEntities"]?.AsArray().Count ?? 0;
        GD.Print($"[savecheck] 捕获完成：{captured.Summary.MapId}，" +
                 $"世界 {captured.World?.ToJsonString().Length ?? 0} 字节，场景 {sceneCount} 人，摆放 {entityCount} 条");

        var moved = false;
        for (var offset = 48f; offset <= 240f; offset += 8f)
        {
            var candidate = new Vec2(_position.X - offset, _position.Y);
            var snapped = NearestSummonSpot(candidate, ally.Node.Definition.Collision);
            if (snapped is not { } spot || GoalQuery.Distance(spot, _position) < 40f) continue;
            ally.Ai.Cancel();
            ally.Node.Position = new Vector2(spot.X, spot.Y);
            moved = true;
            break;
        }
        if (npcSample is not null &&
            NearestSummonSpot(new Vec2(npcSavedPos.X - 32f, npcSavedPos.Y), npcSample.Definition.Collision)
            is { } npcAway) npcSample.Node.TeleportTo(new Vector2(npcAway.X, npcAway.Y));
        Conditions.RemoveState(ally.MemberId, "taunted");
        target.SetFacing("north");
        for (var frame = 0; frame < 30; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[savecheck] 扰动完成：伙伴挪动 {moved}，条件已清，怪朝向 {target.Facing}");

        foreach (var entity in captured.World!["generatedEntities"]!.AsArray())
            entity!["x"] = entity["x"]!.GetValue<double>() + 7d;

        try
        {
            var prepared = SavedGameUpgrades.PrepareSavedGame(captured, _loader.StoryClasses, _loader.World);
            _story.Reset();
            SaveGameStore.RestoreSaveGame(_session, prepared.Game);
            ResetCombatRuntimeForRestore();
            _pendingWorldRestore = ReadWorldSnapshot(prepared.Game);
            LoadMap(prepared.MapId, new Vec2((float)prepared.X, (float)prepared.Y), prepared.Facing);
        }
        catch (Exception error)
        {
            GD.Print($"[savecheck] 恢复失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var allyAfter = _allies.FirstOrDefault(entry => entry.MemberId == ally.MemberId);
        var enemyAfter = _enemies.FirstOrDefault(entry => entry.CombatId == target.CombatId);
        var problems = new List<string>();

        if (allyAfter is null) problems.Add("伙伴没有重新上场");
        else
        {
            var allyPosAfter = new Vec2(allyAfter.Node.Position.X, allyAfter.Node.Position.Y);
            var drift = GoalQuery.Distance(allyPosAfter, allyPosBefore);
            if (drift > 24f) problems.Add($"伙伴位置漂移 {drift:F0}px");
            var retaliation = allyAfter.Ai.CaptureState().Retaliation;
            if (retaliation != target.CombatId) problems.Add($"反击记仇丢失（现为 {retaliation ?? "无"}）");
            var taunted = Conditions.Snapshot(ally.MemberId).Any(entry => entry.Id == "taunted");
            if (!taunted) problems.Add("嘲讽条件没有恢复");
        }

        if (enemyAfter is null) problems.Add("样本怪没有重建");
        else
        {
            if (enemyAfter.Facing != "west") problems.Add($"怪的朝向没有恢复（现为 {enemyAfter.Facing}）");

            // 读档后静止：重建的怪不该带着存档那一刻的
            // 追击路径或攻击阶段起身，否则玩家还没看清画面它就已经在跑了。
            // 严判在领域层（CombatSnapshotValidation 的用例）；这里只查接线有没有漏。
            if (enemyAfter.Ai?.CaptureState() is { } ai)
            {
                if (ai.Path.Count > 0) problems.Add($"读档后怪还带着 {ai.Path.Count} 个路径点");
                if (ai.State == "attacking") problems.Add("读档后怪仍停在 attacking");
            }
        }

        var savedEntities = captured.World?["generatedEntities"]?.AsArray() ?? [];
        if (savedEntities.Count == 0) problems.Add("存档没有带上进图掷骰结果");
        else if (savedEntities.Count != _baseEntities.Count)
            problems.Add($"读档重掷了摆放（{savedEntities.Count} 条 → {_baseEntities.Count} 条）");
        else
            for (var index = 0; index < _baseEntities.Count; index++)
            {
                var saved = savedEntities[index]!;
                var savedX = saved["x"]!.GetValue<double>();
                if (saved["id"]?.GetValue<string>() == _baseEntities[index].Id &&
                    Math.Abs(savedX - _baseEntities[index].X) <= 0.01d) continue;
                var id = saved["id"]?.GetValue<string>() ?? "?";
                problems.Add($"第 {index} 条摆放没有照存档铺（{id} @ {savedX:F1}）");
                break;
            }

        if (captured.World?["scene"] is null) problems.Add("存档没有带上场景快照");
        if (npcId.Length > 0)
        {
            var npcAfter = _npcs.FirstOrDefault(npc => npc.EntityId == npcId);
            if (npcAfter is null) problems.Add("场景里的 NPC 没有重新摆出来");
            else
            {
                var npcDrift = GoalQuery.Distance(
                    new Vec2(npcAfter.Node.Position.X, npcAfter.Node.Position.Y), npcSavedPos);
                if (npcDrift > 1f) problems.Add($"NPC 位置漂移 {npcDrift:F1}px");
                if (npcAfter.Node.Facing != npcSavedFacing)
                    problems.Add($"NPC 朝向没有恢复（现为 {npcAfter.Node.Facing}）");
            }
        }

        if (captured.World?["audio"]?["music"]?.GetValue<string>() is { Length: > 0 } savedMusic &&
            _audio is not null && _audio.CaptureStoryAudio().Music != savedMusic)
            problems.Add($"音乐没有恢复（{savedMusic} → {_audio.CaptureStoryAudio().Music ?? "无"}）");

        if (problems.Count == 0)
        {
            GD.Print("[savecheck] 通过");
            if (!_hold) GetTree().Quit();
        }
        else
        {
            GD.Print($"[savecheck] 未通过：{string.Join("；", problems)}");
            GetTree().Quit(1);
        }
    }

    private async void BeginExitDiagnose()
    {
        _hold = true;
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var exit = _map.Exits
            .OrderBy(e => GoalQuery.Distance(_position, new Vec2(e.X + e.Width / 2f, e.Y + e.Height / 2f)))
            .FirstOrDefault();
        if (exit is null)
        {
            GD.Print("[exitdiagnose] 当前地图没有出口");
            GetTree().Quit(1);
            return;
        }

        var target = new Vec2(exit.X + exit.Width / 2f, exit.Y + exit.Height / 2f);
        _session.Moved(new Vec2(target.X - 8f, target.Y), _player.Facing);
        _player.Position = new Vector2(_position.X, _position.Y);
        GD.Print($"[exitdiagnose] 地图 {_map.Id}（{_map.Exits.Count()} 个出口），挪到 ({_position.X:F0},{_position.Y:F0})，" +
                 $"目标出口 {exit.X},{exit.Y} → {exit.Destination}");

        var moved = false;
        for (var frame = 0; frame < 240; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (frame % 40 == 0)
            {
                var distance = GoalQuery.Distance(_position, target);
                GD.Print($"[exitdiagnose] 第 {frame} 帧：位置 ({_position.X:F0},{_position.Y:F0}) 距出口 {distance:F0}px，" +
                         $"演出中={_storyPlaying} 出口锁={_exitLocked}(锚 {_exitLockAnchor.X:F0},{_exitLockAnchor.Y:F0}) " +
                         $"载图中={_loadingWorld} 出口开={_session.StoryWorld.ExitEnabled(_map.Id, exit)} " +
                         $"压格={OverlapsFoot(_position, exit)} 地图={_map.Id}");
            }

            if (_map.Id == exit.Destination) { moved = true; break; }
        }

        GD.Print(moved ? "[exitdiagnose] 已切图" : "[exitdiagnose] 240 帧内没有切图");
        GetTree().Quit(moved ? 0 : 1);
    }

    /// <summary>
    /// 端到端走一遍「营地 → 影子丛林」：踩进剧情区触发 event-1，让演出自动跑完，
    /// 断言收尾提交后正式世界真的换成 shadow-forest-west（否则玩家会卡在营地出不去）。
    /// </summary>
    /// <summary>
    /// 触发器 repeat 自检（event-74：leap-gate-district 的 repeat 区域事件，条件是
    /// quest-57 进行中）。第一次踩进区域触发、自动播完收尾；原地不动不能重播；
    /// 走出区域（占用放下）再走回来，同一段戏要能第二次起演——对应 
    /// occupiedRegions 加 handled(!repeat && completed) 的组合。
    /// </summary>
    private async void BeginTriggerCheck()
    {
        _hold = true;
        _journal.Apply(QuestCommand.Accept("quest-57", source: "debug"));

        async Task<bool> WaitFire(int count, int frames)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                if (_debugTriggerFires >= count) return true;
            }

            return false;
        }

        async Task WaitIdle(int frames)
        {
            for (var frame = 0; frame < frames; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        void Teleport(float x, float y)
        {
            _session.Moved(new Vec2(x, y), "south");
            _player.Position = new Vector2(x, y);
            _route = null;
            _combatApproach = null;
        }

        Teleport(240f, 376f);
        var first = await WaitFire(1, 600);
        GD.Print($"[triggercheck] 第一次触发={first}");

        for (var frame = 0; frame < 600 && _storyPlaying; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await WaitIdle(30);
        var doneOnce = _journal.EventDone("event-74");
        GD.Print($"[triggercheck] 收尾完成={doneOnce}，起演次数={_debugTriggerFires}");

        await WaitIdle(60);
        var replayed = _debugTriggerFires > 1;
        GD.Print($"[triggercheck] 原地重播={replayed}（应为 False）");

        Teleport(240f, 200f);
        await WaitIdle(30);
        Teleport(240f, 376f);
        var second = await WaitFire(2, 600);
        GD.Print($"[triggercheck] 走开再回来第二次触发={second}，起演次数={_debugTriggerFires}");

        var ok = first && doneOnce && !replayed && second && _debugTriggerFires == 2;
        GD.Print($"[triggercheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginPopupShot()
    {
        _questPopup.AutoConfirm = false;
        var arguments = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(arguments, "--popupshot");
        var questId = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--")
            ? arguments[index + 1]
            : "cardinal-summons";
        var quest = _loader.Campaign.FindQuest(questId);
        if (quest is null)
        {
            GD.Print($"[popupshot] 任务表里没有 {questId}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _ = _questPopup.ShowAsync(quest, "start");
        GD.Print($"[popupshot] {questId} 已摆出，配 --hold --shot 截图");
    }

    private async void BeginStoryFlowCheck()
    {
        _hold = true;
        _session.Moved(new Vec2(388f, 320f), _player.Facing);
        _player.Position = new Vector2(_position.X, _position.Y);

        var problems = new List<string>();
        for (var frame = 0; frame < 3600; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (frame > 120 && !_storyPlaying && _map.Id != "shadow-camp") break;
        }

        if (_storyPlaying) problems.Add("演出没有结束");
        if (_map.Id != "shadow-forest-west") problems.Add($"正式世界仍是 {_map.Id}");
        GD.Print($"[storyflowcheck] 结束：演出 {(_storyPlaying ? "仍在进行" : "已结束")}，" +
                 $"正式世界 {_map.Id} ({(int)_position.X},{(int)_position.Y})，" +
                 $"v{_session.Visit}");

        if (problems.Count == 0)
        {
            GD.Print("[storyflowcheck] 通过");
            GetTree().Quit();
        }
        else
        {
            GD.Print($"[storyflowcheck] 未通过：{string.Join("；", problems)}");
            GetTree().Quit(1);
        }
    }

    private async void BeginPopupCheck()
    {
        // 无头/自动模式里 AutoConfirm 会让 ShowAsync 直接早退、正文根本不渲染；
        // 这条自检要看的就是渲染出来的文本，必须真把它摆出来。
        _questPopup.AutoConfirm = false;

        var quest = _loader.Campaign.FindQuest("grafton-mission");
        if (quest is null)
        {
            GD.Print("[popupcheck] 任务表里没有 grafton-mission");
            GetTree().Quit(1);
            return;
        }

        // 先用最长的任务描述（露娜那条 156 字）把窗撑到上限，再用最少的奖励行收窄。
        // 两个窗的高度必须由各自的文字算出来——沿用上一个窗的高度就会「有时候特别大、
        // 底下空一大片」，这是实际报过的问题。cardinal-summons 是玩家实际报过尺寸的窗。
        var longQuest = _loader.Campaign.FindQuest("cardinal-summons") ?? quest;
        _ = _questPopup.ShowAsync(longQuest, "start");
        for (var frame = 0; frame < 5; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var panel = _questPopup.GetNode<PanelContainer>("Panel");
        var viewport = GetViewport().GetVisibleRect();
        var longSize = panel.Size;
        GD.Print($"[popupcheck] 视口 {viewport.Size}，弹窗控件 rect={_questPopup.GetRect()}，" +
                 $"长描述窗 global={panel.GlobalPosition} size={longSize}");

        _questPopup.Visible = false;

        var prologue = _loader.Campaign.FindQuest("quest-381");
        if (prologue is not null)
        {
            var receipt = new RewardReceipt();
            foreach (var item in prologue.Rewards?.Items ?? [])
                receipt.Items.Add(new RewardItem(item.Item, item.Quantity));
            try
            {
                _ = _questPopup.ShowAsync(prologue, "complete", receipt);
            }
            catch (Exception error)
            {
                GD.Print($"[popupcheck] 弹奖励窗抛错：{error}");
                GetTree().Quit(1);
                return;
            }
            for (var frame = 0; frame < 5; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var body = _questPopup.BodyText;
            var named = body.Contains("再生药水（特大） ×3");
            var shortSize = panel.Size;
            GD.Print($"[popupcheck] 完成窗 size={shortSize}（长描述窗 {longSize}）");

            var sized = MathF.Abs(shortSize.X - longSize.X) < 1f &&
                        MathF.Abs(longSize.X - 540f) < 1f &&
                        longSize.Y <= 437f && longSize.Y >= 180f &&
                        shortSize.Y < longSize.Y && shortSize.Y >= 180f;
            GD.Print($"[popupcheck] 尺寸按内容计算={sized}");

            var raw = body.Contains("life-potion-xl");
            GD.Print($"[popupcheck] 奖励行显示名称={named}，残留生料 id={raw}");
            if (!named || raw || !sized) { GetTree().Quit(1); return; }
        }

        if (!_hold) GetTree().Quit();
    }

    /// <summary>
    /// 无头验证「剧情区压在出口瓦片上」这一条：把玩家挪进营地东侧的 event-1 触发区
    /// （(384,304) 16×64，正压在通往影子丛林的出口上）。触发器起剧情后，同一帧的
    /// 出口判定不能把图切走——否则 LoadMap 会顺手 release 舞台，演员当场消失。
    /// </summary>
    private async void BeginStoryBlockCheck()
    {
        _autoStory = false;

        _session.Moved(new Vec2(388f, 320f), _player.Facing);
        _player.Position = new Vector2(_position.X, _position.Y);
        _route = null;
        _combatApproach = null;

        var started = false;
        for (var frame = 0; frame < 240; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (!_storyPlaying) continue;
            started = true;
            break;
        }

        if (!started)
        {
            GD.Print("[storyblockcheck] 240 帧内没有触发剧情");
            GetTree().Quit(1);
            return;
        }

        for (var frame = 0; frame < 90; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (_stage.ActorsSnapshot().ContainsKey("darius")) break;
        }

        var problems = new List<string>();
        if (!_stage.Active) problems.Add("舞台被释放了");
        if (_stage.StageMapId != "shadow-forest-west") problems.Add($"舞台地图是 {_stage.StageMapId ?? "无"}");
        if (_map.Id != "shadow-camp") problems.Add($"正式世界被切到了 {_map.Id}");
        if (!_stage.ActorsSnapshot().ContainsKey("darius")) problems.Add("舞台上没有卓拉德");

        GD.Print($"[storyblockcheck] 剧情中：舞台 {_stage.StageMapId ?? "无"}，" +
                 $"正式世界 {_map.Id}，演员 {_stage.ActorsSnapshot().Count} 个，" +
                 $"卓拉德{(_stage.ActorsSnapshot().ContainsKey("darius") ? "在场" : "缺席")}");

        if (problems.Count == 0)
        {
            GD.Print("[storyblockcheck] 通过");
            if (!_hold) GetTree().Quit();
        }
        else
        {
            GD.Print($"[storyblockcheck] 未通过：{string.Join("；", problems)}");
            GetTree().Quit(1);
        }
    }

    private async void BeginSizeCheck()
    {
        _hold = true;
        if (_layoutEditor is null || _controlLayout is null)
        {
            GD.Print("[sizescheck] 键位编辑器没建好");
            GetTree().Quit(1);
            return;
        }

        var problems = new List<string>();
        var original = _controlLayout.SizeScale;
        // 从 100% 起跑：本机可能已经挑过档位，自检不该吃上一次的现场。
        _controlLayout.Reset();
        OpenControlEditor();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var (left, top, safeWidth, safeHeight) = ControlViewports.Current(_layoutEditor).Safe;

        float Gap(IReadOnlyDictionary<string, Vector2> points) =>
            points["slot-1"].X - points["slot-0"].X;

        var basePositions = _layoutEditor.Positions;
        var baseSize = _layoutEditor.DebugSize("attack");
        if (Mathf.Abs(_layoutEditor.DebugScale("attack") - 1f) > 0.01f)
            problems.Add($"100% 时普攻缩放是 {_layoutEditor.DebugScale("attack"):F2}，应为 1");

        for (var step = 0; step < 3; step += 1)
            if (!_layoutEditor.DebugPressSize(1)) problems.Add($"第 {step + 1} 次按 + 没反应");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var bigger = _layoutEditor.Positions;
        var bigSize = _layoutEditor.DebugSize("attack");
        GD.Print($"[sizescheck] 130%：普攻 {baseSize.X:F0}×{baseSize.Y:F0} → {bigSize.X:F0}×{bigSize.Y:F0}，" +
                 $"六格间距 {Gap(basePositions):F0} → {Gap(bigger):F0}");

        if (Mathf.Abs(_controlLayout.SizeScale - 1.3f) > 0.001f)
            problems.Add($"档位是 {_controlLayout.SizeScale:F2}，应为 1.3");
        if (_layoutEditor.DebugSizeText != "130%")
            problems.Add($"按钮旁写的是「{_layoutEditor.DebugSizeText}」，应为「130%」");
        if (Mathf.Abs(bigSize.X - baseSize.X * 1.3f) > 0.6f)
            problems.Add("普攻没有按 130% 放大");
        if (Gap(bigger) < Gap(basePositions) * 1.29f)
            problems.Add("格子间距没有跟着尺寸一起放大");
        // 间距必须大于一格的边长，否则放大后底影和环会互相压住。
        var bigSlot = _layoutEditor.DebugSize("slot-0").X;
        if (Gap(bigger) < bigSlot - 0.5f)
            problems.Add($"间距 {Gap(bigger):F0} 小于格子边长 {bigSlot:F0}，六格会叠在一起");

        foreach (var (id, center) in bigger)
        {
            var half = _layoutEditor.DebugSize(id) / 2f;
            if (center.X - half.X < left - 0.5f || center.X + half.X > left + safeWidth + 0.5f ||
                center.Y - half.Y < top - 0.5f || center.Y + half.Y > top + safeHeight + 0.5f)
                problems.Add($"{id} 放大后出了安全区（中心 {center.X:F0},{center.Y:F0}，尺寸 {half.X * 2f:F0}）");
        }

        for (var step = 0; step < 6; step += 1) _layoutEditor.DebugPressSize(-1);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_layoutEditor.DebugSizeText != "70%")
            problems.Add($"连按 6 次 − 后是「{_layoutEditor.DebugSizeText}」，应为「70%」");
        if (_layoutEditor.DebugPressSize(-1)) problems.Add("已经到 70% 还能继续缩小");

        _controlLayout.Reset();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (Mathf.Abs(_layoutEditor.DebugScale("attack") - 1f) > 0.01f)
            problems.Add("恢复默认没把按键大小调回 100%");
        foreach (var (id, center) in basePositions)
            if (_layoutEditor.Positions[id].DistanceTo(center) > 0.5f)
                problems.Add($"{id} 恢复默认后没回到原位");

        _controlLayout.SetSizeScale(original);
        _layoutEditor.Close();

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[sizescheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[sizescheck] 通过：档位缩放、间距联动、贴边内收、上下限与恢复默认");
        GetTree().Quit(0);
    }

    private async void BeginControlCheck()
    {
        _hold = true;
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            GD.Print("[controlcheck] 内容里没有伙伴定义");
            GetTree().Quit(1);
            return;
        }

        try
        {
            _party?.JoinStory(definitionId);
        }
        catch (Exception error)
        {
            GD.Print($"[controlcheck] 伙伴入队失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();
        var ally = _allies.FirstOrDefault();
        if (ally is null)
        {
            GD.Print("[controlcheck] 伙伴实体没有上场");
            GetTree().Quit(1);
            return;
        }

        if (!SwitchControl(ally.MemberId) || ControlledId != ally.MemberId)
        {
            GD.Print("[controlcheck] 切换操控失败");
            GetTree().Quit(1);
            return;
        }

        // 主角托管 AI 关掉、挪到地图角落：他不该替伙伴触发/捡起任何东西。
        _heroAi?.Disable();
        _session.Moved(new Vec2(40f, 40f), "south");
        _player.Position = new Vector2(40f, 40f);

        _journal.Apply(QuestCommand.Accept("quest-57", source: "debug"));

        // 注意：剧情收尾会触发 SyncAllies 的每帧签名轮询重建伙伴实体
        //，所以每个用例前都重新解析节点，
        // 并且把不经过剧情的拾取用例放在触发用例之前。
        // ① 金币掉在伙伴脚边：拾取判定必须读伙伴的位置（主角在角落捡不到）。
        var allyNode = FindAllyNode(ControlledId);
        if (allyNode is null)
        {
            GD.Print("[controlcheck] 伙伴节点丢失");
            GetTree().Quit(1);
            return;
        }

        var moneyBefore = _session.Money;
        var allyPosition = new Vec2(allyNode.Position.X, allyNode.Position.Y);
        SpawnLootOnMap(_map.Id, allyPosition.X, allyPosition.Y, [new Drops.DropReward.MoneyDrop(10)]);
        var picked = false;
        for (var frame = 0; frame < 300 && !picked; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            picked = _session.Money > moneyBefore;
        }

        // 拾取窗口里会话位置（主角）必须一直钉在角落：伙伴移动不落账。
        var heroPinned = _position.X == 40f && _position.Y == 40f;
        GD.Print($"[controlcheck] 伙伴脚边拾取={picked}（金币 {moneyBefore} → {_session.Money}），" +
                 $"主角位置未被动过={heroPinned}");

        // ② 伙伴瞬移进 event-74 的区域：region 判定同样必须读伙伴的位置。
        allyNode = FindAllyNode(ControlledId);
        if (allyNode is null)
        {
            GD.Print("[controlcheck] 伙伴节点丢失（触发用例）");
            GetTree().Quit(1);
            return;
        }

        allyNode.Position = new Vector2(240f, 376f);
        var fired = false;
        for (var frame = 0; frame < 600 && !fired; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            fired = _debugTriggerFires >= 1;
        }

        for (var frame = 0; frame < 600 && _storyPlaying; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var done = _journal.EventDone("event-74");
        GD.Print($"[controlcheck] 伙伴进区触发={fired}，播完={done}");

        var ok = picked && heroPinned && fired && done;
        GD.Print($"[controlcheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// 读档续播自检：剧情停在遭遇战时存档，读回来必须还停在那场遭遇上。
    /// 存档里带的是「脚本 + 节点 + 遭遇实例」三样，读档时靠它们把等待现场接回来。
    /// </summary>
    private async void BeginResumeCheck()
    {
        _autoStory = true;
        _hold = true;
        _suppressEncounterAutoWin = true;

        PlayStory("event-13");

        var guard = 0;
        while (_waitingEncounter is null && _storyPlaying && guard++ < 3600)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var live = _waitingEncounter;
        if (live is null)
        {
            GD.Print("[resumecheck] 没停在遭遇战，无法验证续播");
            GetTree().Quit(1);
            return;
        }

        var captured = CaptureSaveGame();
        var cursor = captured?.Session.StoryCursor;
        GD.Print($"[resumecheck] 存档游标：{cursor?.Script}:{cursor?.Node}，遭遇 {cursor?.EncounterId}");
        if (captured is null || cursor?.EncounterId is not { Length: > 0 } savedEncounter)
        {
            GD.Print("[resumecheck] 存档没带上遭遇游标");
            GetTree().Quit(1);
            return;
        }

        _waitingEncounter = null;
        try
        {
            var prepared = SavedGameUpgrades.PrepareSavedGame(captured, _loader.StoryClasses, _loader.World);
            _story.Reset();
            SaveGameStore.RestoreSaveGame(_session, prepared.Game);
            ResetCombatRuntimeForRestore();
            _pendingWorldRestore = ReadWorldSnapshot(prepared.Game);
            LoadMap(prepared.MapId, new Vec2((float)prepared.X, (float)prepared.Y), prepared.Facing);
        }
        catch (Exception error)
        {
            GD.Print($"[resumecheck] 读档失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var waiting = _waitingEncounter;
        var status = _story.Runs.Current is { } run ? StoryRunTable.StatusName(run.Status) : "none";
        GD.Print($"[resumecheck] 读档后停在 {waiting}，运行状态 {status}（存档里是 {savedEncounter}）");

        var ok = waiting == savedEncounter && status == "waiting";
        GD.Print($"[resumecheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginRecoveryCheck()
    {
        _hold = true;
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            GD.Print("[recoverycheck] 内容里没有伙伴定义");
            GetTree().Quit(1);
            return;
        }

        try
        {
            _party?.JoinStory(definitionId);
        }
        catch (Exception error)
        {
            GD.Print($"[recoverycheck] 伙伴入队失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();
        var memberId = _allies.FirstOrDefault()?.MemberId;
        var member = memberId is null ? null : _party?.Get(memberId);
        if (member is null)
        {
            GD.Print("[recoverycheck] 伙伴没有上阵");
            GetTree().Quit(1);
            return;
        }

        // 两边都压到低血、空蓝：脉冲之后必须都能看到数字往上走。
        SetPlayerHealth(Math.Min(50, _hero?.Player.MaxHealth ?? 50));
        _hero?.SetMana(0);
        member.SetHealth(Math.Min(50, member.Player.MaxHealth));
        member.SetMana(0);

        var heroBefore = MemberHealth(PlayerCombatId);
        var allyBefore = member.Player.Health;
        var allyManaBefore = member.Player.Mana;

        _session.AdvanceCombatFrames(3000, _ => true);
        GD.Print($"[recoverycheck] 上阵 {string.Join(',', ActivePartySlots())}，帧 {_combatClock.Frame}，" +
                 $"脉冲到期={_recovery.PulseDueAtFrame(_combatClock.Frame)}");
        RecoverResources();

        var heroAfter = MemberHealth(PlayerCombatId);
        var allyAfter = member.Player.Health;
        var allyManaAfter = member.Player.Mana;
        GD.Print($"[recoverycheck] 主角 {heroBefore} → {heroAfter}，伙伴 {allyBefore} → {allyAfter}" +
                 $"（魔力 {allyManaBefore} → {allyManaAfter}）");

        // 倒下的成员不该被脉冲救活。
        member.SetHealth(0);
        _session.Advance(3000);
        RecoverResources();
        var stayedDown = member.Player.Health == 0;
        GD.Print($"[recoverycheck] 倒下的伙伴保持 0 血={stayedDown}");

        var ok = heroAfter > heroBefore && allyAfter > allyBefore &&
                 allyManaAfter > allyManaBefore && stayedDown;
        GD.Print($"[recoverycheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginNearCheck()
    {
        _hold = true;

        StoryEventTrigger? target = null;
        MapNpc? beside = null;
        foreach (var trigger in _loader.Campaign.EventTable.Triggers)
        {
            if (trigger.Source != "near" || trigger.Near.Length == 0) continue;
            if (trigger.Condition.ValueKind == JsonValueKind.Object && !_story.Evaluate(trigger.Condition)) continue;

            var npc = _npcs.FirstOrDefault(candidate =>
                candidate.ActorId == trigger.Near &&
                (trigger.NearCharacterId is not { Length: > 0 } wanted || candidate.CharacterId == wanted) &&
                candidate.Node is not null && IsInstanceValid(candidate.Node) && candidate.Node.Visible);
            if (npc is null) continue;

            target = trigger;
            beside = npc;
            break;
        }

        if (target is null || beside is null)
        {
            GD.Print("[nearcheck] 本图没有可测的近身事件，跳过");
            GetTree().Quit(0);
            return;
        }

        var spot = new Vec2(beside.Node.Position.X, beside.Node.Position.Y);
        GD.Print($"[nearcheck] 样本 {target.Id}：靠近 {target.Near}（{beside.CharacterId}）@({spot.X:F0},{spot.Y:F0})");

        void StandAt(float x, float y)
        {
            _session.Moved(new Vec2(x, y), "south");
            _player.Position = new Vector2(x, y);
            _route = null;
            _combatApproach = null;
        }

        StandAt(spot.X + 90f, spot.Y);
        var matchedFar = TriggerPlaceMatches(target);
        GD.Print($"[nearcheck] 90 像素外命中={matchedFar}（应为 False）");

        StandAt(spot.X + 20f, spot.Y);
        var matchedNear = TriggerPlaceMatches(target);
        var fired = false;
        for (var frame = 0; frame < 300 && !fired; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            fired = _debugTriggerFires >= 1;
        }

        GD.Print($"[nearcheck] 身边 20 像素命中={matchedNear}，起演={fired}");
        var ok = !matchedFar && matchedNear && fired;
        GD.Print($"[nearcheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void BeginSpeedCheck()
    {
        _hold = true;

        if (_map is null)
        {
            GD.Print("[speedcheck] 地图还没建好");
            GetTree().Quit(1);
            return;
        }

        var cells = _map.Water;
        if (cells is not { Length: > 0 })
        {
            GD.Print("[speedcheck] 本图没有水格，跳过");
            GetTree().Quit(0);
            return;
        }

        // ① 水格：取第一格的中心（水格在内容里都是可站的，能不能站由 blocked 决定）。
        var water = CellCentre(cells[0]);
        if (water is null)
        {
            GD.Print("[speedcheck] 水格索引越界");
            GetTree().Quit(1);
            return;
        }

        Vec2? land = null;
        for (var index = 0; index < _map.Columns * _map.Rows && land is null; index++)
        {
            if (_grid.IsBlocked(index % _map.Columns, index / _map.Columns)) continue;
            var point = CellCentre(index);
            if (point is { } candidate && !_grid.IsWater(candidate)) land = candidate;
        }

        if (land is null)
        {
            GD.Print("[speedcheck] 找不到可站的干燥格");
            GetTree().Quit(1);
            return;
        }

        void StandAt(Vec2 point)
        {
            _session.Moved(point, "south");
            _player.Position = new Vector2(point.X, point.Y);
        }

        StandAt(land.Value);
        var landSpeed = MovementSpeed(PlayerCombatId, PlayerSpeed);

        StandAt(water.Value);
        var waterSpeed = MovementSpeed(PlayerCombatId, PlayerSpeed);

        var attribute = AttributeOf(PlayerCombatId, "moveSpeed");
        var immune = _loader.Battle.Enemies.Count(enemy => enemy.WaterImmune == true);

        GD.Print($"[speedcheck] 岸上 {landSpeed}（期望 {MathF.Truncate(PlayerSpeed)}）、" +
                 $"水里 {waterSpeed}（期望 {MathF.Truncate(MovementSpeedRule.Apply(PlayerSpeed, 1, 1000, true))}）");
        GD.Print($"[speedcheck] moveSpeed 属性 {attribute}（期望 1000）、涉水豁免敌人 {immune} 只");

        var ok = landSpeed == MathF.Truncate(PlayerSpeed) &&
                 waterSpeed == MathF.Truncate(MovementSpeedRule.Apply(PlayerSpeed, 1, 1000, true)) &&
                 attribute == MovementSpeedRule.NeutralMoveSpeed &&
                 immune > 0;
        GD.Print($"[speedcheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private Vec2? CellCentre(int index)
    {
        if (_map is null || index < 0 || index >= _map.Columns * _map.Rows) return null;
        var size = _map.TileSize;
        return new Vec2(index % _map.Columns * size + size / 2f, index / _map.Columns * size + size / 2f);
    }

    private async void BeginSwitchCheck()
    {
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is null)
        {
            GD.Print("[switchcheck] 内容里没有伙伴定义");
            GetTree().Quit(1);
            return;
        }

        try
        {
            _party?.JoinStory(definitionId);
        }
        catch (Exception error)
        {
            GD.Print($"[switchcheck] 伙伴入队失败：{error.Message}");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SyncAllies();
        var ally = _allies.FirstOrDefault();
        if (ally is null)
        {
            GD.Print("[switchcheck] 伙伴实体没有上场");
            GetTree().Quit(1);
            return;
        }

        if (!SwitchControl(ally.MemberId) || ControlledId != ally.MemberId)
        {
            GD.Print("[switchcheck] 切换操控失败");
            GetTree().Quit(1);
            return;
        }
        GD.Print($"[switchcheck] 操控切给 {ally.MemberId}，主角 ({_position.X:F0},{_position.Y:F0})");

        // 大面板头像必须跟着操控者换：切到伙伴后
        // 还挂着主角那张，玩家看到的就是「头像跟当前操控的角色不匹配」。
        _statusHud?.Refresh();
        var expectedPortrait = HudPortraitFor(ControlledId);
        if (expectedPortrait is not null && _statusHud?.PlayerPortraitDebug != expectedPortrait)
        {
            GD.Print($"[switchcheck] 大面板头像没跟着换：{_statusHud?.PlayerPortraitDebug ?? "无"}（应为 {expectedPortrait}）");
            GetTree().Quit(1);
            return;
        }
        GD.Print($"[switchcheck] 大面板头像 {_statusHud?.PlayerPortraitDebug ?? "无"}，与操控者一致");

        var moved = false;
        for (var offset = 120f; offset <= 240f; offset += 8f)
        {
            var candidate = new Vec2(_position.X + offset, _position.Y);
            var snapped = NearestSummonSpot(candidate, ally.Node.Definition.Collision);
            if (snapped is not { } spot || GoalQuery.Distance(spot, _position) < 100f) continue;
            ally.Ai.Cancel();
            ally.Node.Position = new Vector2(spot.X, spot.Y);
            moved = true;
            break;
        }
        if (!moved)
        {
            GD.Print("[switchcheck] 找不到 100px 外的合法落点");
            GetTree().Quit(1);
            return;
        }
        ally.Node.HaltMotion();
        var heroPosBefore = new Vec2(_position.X, _position.Y);
        var allyPos = new Vec2(ally.Node.Position.X, ally.Node.Position.Y);

        for (var frame = 0; frame < 240; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var heroDrift = GoalQuery.Distance(new Vec2(_position.X, _position.Y), heroPosBefore);
        var heroToAlly = GoalQuery.Distance(new Vec2(_position.X, _position.Y), allyPos);
        // 被控的伙伴这 240 帧里没有输入，位置就该一动不动。托管 AI 曾经会把它拖着走
        // （主角 AI 的跟随／追近走的是「挪被控者」那条路），所以这里把它一起钉住。
        var allyDrift = GoalQuery.Distance(new Vec2(ally.Node.Position.X, ally.Node.Position.Y), allyPos);
        GD.Print($"[switchcheck] 主角移动 {heroDrift:F0}px，距伙伴 {heroToAlly:F0}px，" +
                 $"被控伙伴自己移了 {allyDrift:F0}px，主角 AI {_heroAi?.ActionState().ToString() ?? "无"}");
        if (allyDrift > 8f)
        {
            GD.Print("[switchcheck] 被控伙伴被托管 AI 拖着自己走了");
            GetTree().Quit(1);
            return;
        }
        if (heroDrift < 40f || heroToAlly > 72f)
        {
            GD.Print("[switchcheck] 主角没有跟上来（托管 AI 未生效）");
            GetTree().Quit(1);
            return;
        }

        // —— 普攻打给被控的伙伴 ——
        // 挑离伙伴最近的活怪，落点必须真的贴上（搜索半径 64px 内），否则普攻请求会 no-target。
        var target = _enemies.Where(enemy => !enemy.Dead)
            .OrderBy(enemy => GoalQuery.Distance(new Vec2(ally.Node.Position.X, ally.Node.Position.Y),
                new Vec2(enemy.Position.X, enemy.Position.Y)))
            .FirstOrDefault();
        if (target is not null)
        {
            var enemyHealthBefore = VitalsOf(target.CombatId)?.Health ?? 0;
            var landed = false;
            for (var attempt = 0; attempt < 3 && !landed; attempt++)
            {
                var nearEnemy = NearestSummonSpot(new Vec2(target.Position.X, target.Position.Y),
                    ally.Node.Definition.Collision) ?? new Vec2(target.Position.X, target.Position.Y);
                if (GoalQuery.Distance(nearEnemy,
                        new Vec2(target.Position.X, target.Position.Y)) > 48f)
                    continue;
                ally.Node.Position = new Vector2(nearEnemy.X, nearEnemy.Y);
                ally.Ai.Cancel();
                for (var frame = 0; frame < 60; frame++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var request = CombatRuntime.Request(ControlledId);
                PlayerAttack();
                var distance = GoalQuery.Distance(new Vec2(ally.Node.Position.X, ally.Node.Position.Y),
                    new Vec2(target.Position.X, target.Position.Y));
                GD.Print($"[switchcheck] 第 {attempt + 1} 次请求：{request.Reason ?? "started"}，" +
                         $"距怪 {distance:F0}px，落点合法 {nearEnemy}");
                for (var frame = 0; frame < 300; frame++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (_dealtDamage.Contains(ally.MemberId)) break;
                }

                landed = _dealtDamage.Contains(ally.MemberId);
            }

            var enemyHealthAfter = VitalsOf(target.CombatId)?.Health ?? 0;
            GD.Print($"[switchcheck] 伙伴普攻：怪生命 {enemyHealthBefore}→{enemyHealthAfter}，" +
                     $"伙伴 AI {ally.Ai.ActionState()}");
            if (!landed)
            {
                GD.Print("[switchcheck] 被控伙伴的普攻没有落到怪身上");
                GetTree().Quit(1);
                return;
            }
        }

        if (!SwitchControl(PlayerCombatId) || ControlledId != PlayerCombatId)
        {
            GD.Print("[switchcheck] 切回主角失败");
            GetTree().Quit(1);
            return;
        }
        GD.Print("[switchcheck] 通过");
        if (!_hold) GetTree().Quit();
    }

    private async void BeginInteractCheck()
    {
        var merchant = _npcs.FirstOrDefault(npc => npc.ActorId == "merchandise-dealer-b09d9585");
        if (merchant is null)
        {
            GD.Print("[interactcheck] 营地里没有商人");
            GetTree().Quit(1);
            return;
        }

        var eastOfMerchant = new Vec2(merchant.Node.Position.X + 16f, merchant.Node.Position.Y);
        var spot = _grid.CanStandOnTerrain(eastOfMerchant.X, eastOfMerchant.Y)
            ? eastOfMerchant
            : new Vec2(merchant.Node.Position.X, merchant.Node.Position.Y);
        _session.Moved(spot, "west");
        _player.Position = new Vector2(_position.X, _position.Y);
        UpdateInteraction();

        if (_nearNpc is null)
        {
            GD.Print("[interactcheck] 没选中交互目标");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"[interactcheck] 选中 {_nearNpc.ActorId}（{_nearNpc.CharacterId}）");

        _dialogue.AutoAdvance = true;
        TryInteract();

        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        GD.Print("[interactcheck] 完成");
        GetTree().Quit();
    }

    private async void BeginNpcFunctionCheck()
    {
        var catalog = _loader.NpcFunctions;
        GD.Print($"[npccheck] 功能表 {catalog.Functions.Length} 条，绑定了 {catalog.Npcs.Count} 个 NPC");

        var arguments = OS.GetCmdlineUserArgs();
        var funcIndex = Array.IndexOf(arguments, "--npcfunc");
        if (funcIndex >= 0 && funcIndex + 1 < arguments.Length && int.TryParse(arguments[funcIndex + 1], out var wanted))
        {
            await RunSingleNpcFunctionCheck(wanted);
            return;
        }

        var swordFunction = catalog.ActionOf("npc-39");
        var expected = swordFunction is null ? null : NpcFunctionCatalog.RewardFor(swordFunction, _classId);
        GD.Print($"[npccheck] npc-39 → {swordFunction?.Name ?? "(没有实现的功能)"}，职业 {_classId} 应得 {expected ?? "(无)"}");

        var sword = _npcs.FirstOrDefault(npc =>
            catalog.ActionOf(npc.CharacterId)?.Behavior?.Kind == NpcFunctionBehavior.PullSword);
        if (sword is null)
        {
            GD.Print($"[npccheck] {_map.Id} 里没有可拔的剑，换 --map delos-cave-9172c82a");
            GetTree().Quit(1);
            return;
        }

        var east = new Vec2(sword.Node.Position.X + 16f, sword.Node.Position.Y);
        var spot = _grid.CanStandOnTerrain(east.X, east.Y)
            ? east
            : new Vec2(sword.Node.Position.X, sword.Node.Position.Y);
        _session.Moved(spot, "west");
        _player.Position = new Vector2(_position.X, _position.Y);
        UpdateInteraction();

        if (_nearNpc is null)
        {
            GD.Print("[npccheck] 没选中剑");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"[npccheck] 选中 {_nearNpc.ActorId}（{_nearNpc.CharacterId}）");
        _dialogue.AutoAdvance = true;
        TryInteract();

        await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);

        var consumed = _session.Props.IsConsumed(_map.Id, sword.EntityId);
        var count = expected is null ? -1 : _bag?.Count(expected) ?? -1;
        var visible = GodotObject.IsInstanceValid(sword.Node) && sword.Node.Visible;
        GD.Print($"[npccheck] 已消耗={consumed} 背包里 {expected}={count} 剑还看得见={visible}");

        _dialogue.AutoAdvance = true;
        TryInteract();
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        var again = expected is null ? -1 : _bag?.Count(expected) ?? -1;
        GD.Print($"[npccheck] 再交互一次后 {expected}={again}");

        var ok = consumed && count == 1 && !visible && again == 1;
        GD.Print($"[npccheck] {(ok ? "通过" : "失败")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// 只验一条功能（<c>--npcfunc &lt;编号&gt;</c>）：找到本图挂了它的 NPC，站过去按一次，
    /// 打印交互前后的队伍状态。休息前会先把主角打残，否则看不出变化。
    /// </summary>
    private async Task RunSingleNpcFunctionCheck(int wanted)
    {
        var catalog = _loader.NpcFunctions;
        var function = catalog.Of(wanted);
        var npc = _npcs.FirstOrDefault(candidate =>
            catalog.FunctionsOf(candidate.CharacterId).Any(entry => entry.Index == wanted));
        GD.Print($"[npccheck] 功能 {wanted}（{function?.Name ?? "?"}，" +
                 $"{function?.Behavior?.Kind ?? "未实现"}）");
        if (npc is null)
        {
            GD.Print($"[npccheck] {_map.Id} 上没有挂这个功能的 NPC");
            GetTree().Quit(1);
            return;
        }

        if (wanted == 0 && _party is not null && _hero is not null)
        {
            var quarter = Math.Max(1, _hero.Player.MaxHealth / 4);
            _hero.SetHealth(quarter);
            GD.Print($"[npccheck] 先扣血：{quarter}/{_hero.Player.MaxHealth}");
        }

        var east = new Vec2(npc.Node.Position.X + 16f, npc.Node.Position.Y);
        var spot = _grid.CanStandOnTerrain(east.X, east.Y)
            ? east
            : new Vec2(npc.Node.Position.X, npc.Node.Position.Y);
        _session.Moved(spot, "west");
        _player.Position = new Vector2(_position.X, _position.Y);
        UpdateInteraction();
        if (_nearNpc is null)
        {
            GD.Print("[npccheck] 没选中 NPC");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"[npccheck] 选中 {_nearNpc.ActorId}（{_nearNpc.CharacterId}）");
        _dialogue.AutoAdvance = true;
        TryInteract();
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        if (_hero is not null)
            GD.Print($"[npccheck] 交互后主角 血 {_hero.Player.Health}/{_hero.Player.MaxHealth} " +
                     $"魔 {_hero.Player.Mana}/{_hero.Player.MaxMana}");

        var implemented = function?.Behavior is not null;
        GD.Print($"[npccheck] {(implemented ? "通过" : "失败")}");
        GetTree().Quit(implemented ? 0 : 1);
    }

    private async void BeginCraftCheck()
    {
        var machine = _npcs.FirstOrDefault(npc => IsFusionMachine(npc.ActorId));
        if (machine is null)
        {
            GD.Print($"[craftcheck] {_map.Id} 里没有融合器");
            GetTree().Quit(1);
            return;
        }

        var east = new Vec2(machine.Node.Position.X + 16f, machine.Node.Position.Y);
        var spot = _grid.CanStandOnTerrain(east.X, east.Y)
            ? east
            : new Vec2(machine.Node.Position.X, machine.Node.Position.Y);
        _session.Moved(spot, "west");
        _player.Position = new Vector2(_position.X, _position.Y);
        UpdateInteraction();

        if (_nearNpc is null)
        {
            GD.Print("[craftcheck] 没选中交互目标");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"[craftcheck] 选中 {_nearNpc.ActorId}（{_nearNpc.CharacterId}）");

        // 原版先看任务槽：这个融合器手上刚好有可接的任务，那一下会走对话而不是开合成台。
        // 但融合器挂的是可重复任务，任务一完成就又变回「可接」——只走对话的话合成台永远开不起来，
        // 所以现在说完话会接着把合成页打开。这一条先按真实交互验一遍。
        if (TakesQuestSlot(_nearNpc) && _journal.SlotFor(_nearNpc.ActorId, _nearNpc.CharacterId) is { } offered)
        {
            var autoConfirm = _questPopup?.AutoConfirm ?? false;
            if (_questPopup is not null) _questPopup.AutoConfirm = true; // 无头没人点那颗确认
            TryInteract();
            await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
            if (_questPopup is not null) _questPopup.AutoConfirm = autoConfirm;

            var accepted = _journal.StatusOf(offered.Id) == QuestStatus.Active;
            var openedAfterTalk = _craftPanel is { IsOpen: true };
            GD.Print($"[craftcheck] 可接任务 {offered.Id}：接下={accepted}，说完话就开合成台={openedAfterTalk}");
            if (!accepted) GD.Print("[craftcheck] 问题：点了融合器却没接下任务");
            if (!openedAfterTalk) GD.Print("[craftcheck] 问题：接下任务后合成台没跟着开");

            _craftPanel?.Close();
            UpdateInteraction();
        }

        TryInteract();

        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        var opened = _craftPanel is { IsOpen: true };
        GD.Print($"[craftcheck] 合成台{(opened ? "已打开" : "没打开")}");

        if (opened && _craftPanel is { } panel)
        {
            GD.Print($"[craftcheck] 进来时菜单列表{(panel.DebugMenuOpen ? "开着" : "没开")}");
            foreach (var (key, label) in panel.DebugTabs())
                GD.Print($"[craftcheck] 页签 {key}（{label}）：{panel.DebugRecipeCount(key)} 条");

            var picked = panel.DebugPickFirst(FusionCatalog.PotionMenu);
            GD.Print($"[craftcheck] 药水页选中 {picked ?? "（没有配方）"}，菜单列表{(panel.DebugMenuOpen ? "仍开着" : "已收起")}");
            panel.Begin();
            GD.Print($"[craftcheck] 组合按钮：{(panel.DebugMessage is { } text ? "提示「" + text.Replace("\n", "") + "」" : "直接出了确认框")}");

            _bag.Add("leaf-of-life", 3);
            panel.DebugPickFirst(FusionCatalog.PotionMenu);
            panel.Begin();
            GD.Print($"[craftcheck] 材料补足后：{(panel.DebugMessage is { } again ? "仍被拒「" + again.Replace("\n", "") + "」" : "出了确认框")}");
            panel.Confirm();
            GD.Print($"[craftcheck] 确认后：生命之叶 {_bag.Count("leaf-of-life")} 份、" +
                     $"恢复药水（小）{_bag.Count("healing-potion-s")} 瓶");

            _bag.Add("leaf-of-life", 6);
            panel.DebugPickFirst(FusionCatalog.PotionMenu);
            GD.Print($"[craftcheck] 批量 ×2 摆位={panel.DebugBatch(2)}");
            panel.Begin();
            GD.Print($"[craftcheck] 批量组合：{(panel.DebugMessage is { } limit ? "被拒「" + limit.Replace("\n", "") + "」" : "出了确认框")}");
            panel.Confirm();
            GD.Print($"[craftcheck] 批量后：生命之叶 {_bag.Count("leaf-of-life")} 份、" +
                     $"恢复药水（小）{_bag.Count("healing-potion-s")} 瓶");

            _bag.Add("dagger", 2);
            panel.Choose(FusionCatalog.ChaosMenu);
            panel.PickTier(CraftingCatalog.ChaosTier);
            panel.DebugSelect();
            var beforeCycle = panel.DebugTarget;
            var moved = panel.DebugCycleTarget(1);
            GD.Print($"[craftcheck] 目标切换：候选 {panel.DebugTargetCount()} 件，" +
                     $"切一次={moved}，{beforeCycle} → {panel.DebugTarget}");

            // 右上角那个叉：走的是菜单页共用的关闭入口，融合器也必须被关掉。
            CloseOpenMenuPage();
            GD.Print($"[craftcheck] 点关闭钮后合成台{(_craftPanel is { IsOpen: true } ? "仍开着（错）" : "已关掉")}");
        }

        var closed = _craftPanel is not { IsOpen: true };
        GetTree().Quit(opened && closed ? 0 : 1);
    }

    private void RunSkillCheck()
    {
        var catalog = _loader.Skills;
        var actions = 0;
        var events = 0;
        var units = 0;
        var needsTarget = 0;
        var repeats = 0;
        var effectNames = 0;
        var failed = false;

        foreach (var (id, skill) in catalog.Skills)
        {
            if (skill.Action is not { } action) continue;
            actions += 1;
            events += action.Events.Count;
            units += action.CombatUnits?.Count ?? 0;
            if (action.RequiresTarget == true) needsTarget += 1;
            if (action.Repeat is not null) repeats += 1;

            if (action.EffectNames is null)
            {
                GD.PrintErr($"[skillcheck] {id} 没有编译特效清单");
                failed = true;
                continue;
            }

            effectNames += action.EffectNames.Length;
        }

        GD.Print($"[skillcheck] 技能 {catalog.Skills.Count}，主动动作 {actions}，事件 {events}，" +
                 $"结算单元 {units}，需要站位 {needsTarget}，连续施放 {repeats}，特效名 {effectNames}");

        if (catalog.Skills.GetValueOrDefault("dark-strike")?.Action is { } sample)
            GD.Print($"[skillcheck] dark-strike 冷却 {sample.CooldownFrames} 帧，" +
                     $"序列 {SkillActionRules.SkillSequence(sample, 1, 6).Length} 帧，" +
                     $"第 8 帧事件 {SkillActionRules.SkillEvents(sample, 8).Count}，" +
                     $"站位 {SkillActionRules.NeedsTarget(sample)}");

        GetTree().Quit(failed ? 1 : 0);
    }

    /// <summary>
    /// 自检：主动技能冒烟（规格票 06 的验收项）。技能按职业归属（battle.json 的
    /// <c>classes[].skills</c>），所以只冒烟**当前职业**的那一组——切职业会牵动角色卡、
    /// 装备与外观重建，不该为自检去动。要覆盖别的职业就用 <c>--class</c> 换一次。
    ///
    /// 每门技能走真实起手路径（<see cref="PlayerCastSkill"/> →
    /// <c>CombatController.RequestManualSkill</c>），然后只喂渲染帧把动作推到结束。
    /// 判据：要么「受理并跑完」，要么以明确原因被拒（还没学、蓝不够、冷却中、没目标）；
    /// **不接受的只有异常与卡死**——这才叫「实放一次不报错」。
    /// 需要一张有敌人的地图：<c>--skillplaycheck --map training-dungeon-two</c>。
    /// </summary>
    private async void BeginSkillPlayCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        EnsurePlayerCombat();
        // 自检期间免疫：站着让每门技能跑完，不能中途被怪打死。
        SetInvincible(PlayerCombatId, true);

        void StageTarget()
        {
            if (!_enemies.Any(enemy => !enemy.Dead)) SpawnArena();
            foreach (var enemy in _enemies.Where(enemy => !enemy.Dead))
            {
                enemy.TeleportTo(new Vector2(_position.X + 14f, _position.Y));
                enemy.RestoreHealth(enemy.Fighter.MaxHealth);
                enemy.HaltMotion();
            }
        }

        var catalog = _loader.Skills;
        var characterClass = catalog.Class(_classId);
        if (characterClass is null)
        {
            GD.Print($"[skillplaycheck] 职业 {_classId} 不在内容里");
            GetTree().Quit(1);
            return;
        }

        // 该职业的主动技能；把该职业的每一门都记为已学（冒烟验「能不能放」，不是「学没学」）。
        var skillIds = characterClass.SkillIds
            .Where(id => catalog.Skills.GetValueOrDefault(id)?.Action is not null)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var started = 0;
        var rejected = new List<string>();
        var failures = new List<string>();

        foreach (var skillId in skillIds)
        {
            StageTarget();
            PrepareSkillsForCheck();
            CombatRuntime.CancelAttack(ControlledId);

            string? failure;
            try
            {
                failure = PlayerCastSkill(skillId);
            }
            catch (Exception error)
            {
                failures.Add($"{skillId} 起手抛异常：{error.GetType().Name}: {error.Message}");
                continue;
            }

            if (failure is not null)
            {
                rejected.Add($"{skillId}:{failure}");
                continue;
            }

            started += 1;

            var settled = false;
            for (var frame = 0; frame < 240; frame += 1)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                if (CombatRuntime.ActionState(ControlledId) == CombatState.Idle)
                {
                    settled = true;
                    break;
                }
            }

            if (!settled)
                failures.Add($"{skillId} 起手后 240 帧仍停在 {CombatRuntime.ActionState(ControlledId)}");
            if (MemberHealth(ControlledId) <= 0)
                failures.Add($"{skillId} 施放过程中自己倒下了");
        }

        var totalActive = catalog.Skills.Count(pair => pair.Value.Action is not null);
        GD.Print($"[skillplaycheck] 职业 {_classId}：主动技能 {skillIds.Count}/{totalActive}，" +
                 $"受理并跑完 {started}，拒绝 {rejected.Count}，失败 {failures.Count}");
        foreach (var entry in rejected) GD.PrintErr($"[skillplaycheck]   未实放 {entry}");
        foreach (var entry in failures) GD.PrintErr($"[skillplaycheck]   失败 {entry}");
        if (skillIds.Count < totalActive)
            GD.Print($"[skillplaycheck] 其余 {totalActive - skillIds.Count} 门属于别的职业，换 --class 再冒烟一次");

        // 「每技能实放一次」是票面的验收原话：**每一门都必须真的被受理并跑完**。
        // 被拒（哪怕是 no-target 这种正常理由）也说明这一门没验到，算失败——
        // 否则「9 门全被拒」也会打印通过，冒烟就白跑了。
        var ok = failures.Count == 0 && rejected.Count == 0 && started == skillIds.Count;
        if (!ok) GD.PrintErr($"[skillplaycheck] 未达成「每门都实放一次」：本职业共 {skillIds.Count} 门，" +
                             $"实放 {started}，拒绝 {rejected.Count}");
        GD.Print(ok ? "[skillplaycheck] 通过" : "[skillplaycheck] 失败");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void PrepareSkillsForCheck()
    {
        if (_hero is { } hero) SetPlayerHealth(hero.Player.MaxHealth);
        SetManaAbsolute(PlayerCombatId, ResourceCapacityOf(PlayerCombatId, "maxMana"));
        _session.Cooldowns.ForActor(PlayerCombatId).Reset();

        var catalog = _loader.Skills;
        if (_skills is null || catalog.Class(_classId) is not { } characterClass) return;

        var active = characterClass.SkillIds
            .Where(id => catalog.Skills.GetValueOrDefault(id)?.Action is not null)
            .Distinct()
            .ToList();

        var state = _skills.CaptureState();
        var levels = state.Loadouts.FirstOrDefault(pair => pair.Key == _classId).Value?.Clone() ?? new SkillLoadout();
        foreach (var skillId in active)
            if (levels.LevelOf(skillId) < 1) levels.Levels[skillId] = 1;
        var loadouts = state.Loadouts.Where(pair => pair.Key != _classId).ToList();
        loadouts.Add(new KeyValuePair<string, SkillLoadout>(_classId, levels));
        _skills.RestoreState(new SkillConfigurationState { Loadouts = loadouts, Pages = state.Pages });

        var bar = _skills.Bar();
        foreach (var skillId in active)
        {
            if (bar.Assigned(ShortcutEntry.SkillKind, skillId)) continue;
            var placed = false;
            for (var page = 0; page < ShortcutLayout.PageCount && !placed; page += 1)
            for (var slot = 0; slot < ShortcutLayout.SkillSlots && !placed; slot += 1)
            {
                if (bar.Page(page)[slot] is not null) continue;
                bar.Assign(page, slot, new ShortcutEntry(ShortcutEntry.SkillKind, skillId));
                placed = true;
            }
            if (!placed) break;
        }
    }

    private async void BeginEnemyCastCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);

        var withSkills = _loader.Battle.Enemies
            .Where(definition => definition.Skills is { Count: > 0 })
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .Take(12)
            .ToList();
        if (withSkills.Count == 0)
        {
            GD.Print("[enemycastcheck] 内容里没有带技能的敌人");
            GetTree().Quit(1);
            return;
        }

        var cast = 0;
        var idle = new List<string>();
        var failures = new List<string>();

        foreach (var definition in withSkills)
        {
            foreach (var existing in _enemies.ToList()) KillEnemy(existing, null);
            SetPlayerHealth(_hero?.Player.MaxHealth ?? 100);

            EnemyNode? foe = null;
            try
            {
                foe = SpawnEnemyForCheck(definition, _position);
            }
            catch (Exception error)
            {
                failures.Add($"{definition.Id} 召出失败：{error.GetType().Name}: {error.Message}");
                continue;
            }

            if (foe is null)
            {
                failures.Add($"{definition.Id} 召出返回空引用");
                continue;
            }

            var lowest = (definition.Skills ?? [])
                .Select(skill => skill.ReleaseCondition)
                .Where(condition => condition is { Length: > 0 })
                .DefaultIfEmpty(null)
                .FirstOrDefault();
            if (lowest is not null && foe.Health > 1)
                foe.RestoreHealth(Math.Max(1, foe.Fighter.MaxHealth * 20 / 100));

            var played = false;
            try
            {
                for (var frame = 0; frame < 600 && !played; frame += 1)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                    var state = foe.Ai?.CombatController?.ActionState() ?? CombatState.Idle;
                    var casting = foe.Ai?.CombatController?.IsCastingSkill == true;
                    played = casting && (state == CombatState.Attacking || state == CombatState.Approaching);
                }
            }
            catch (Exception error)
            {
                failures.Add($"{definition.Id} 施放中抛异常：{error.GetType().Name}: {error.Message}");
                continue;
            }

            if (played) cast += 1;
            else
            {
                var health = foe?.Health ?? 0;
                var maxHealth = foe?.Fighter.MaxHealth ?? 1;
                var gap = foe is null
                    ? -1f
                    : new Vec2(foe.Position.X - _position.X, foe.Position.Y - _position.Y).Length;
                // 这里的 foe 已经过上面的空引用闸门；用 ?. 是因为 await 之后编译器不再保留这个判定。
                var controller = foe?.Ai?.CombatController;
                var combatText = controller is null ? "none" : controller.State.ToString();
                idle.Add($"{definition.Id} [combat={combatText} " +
                         $"hp={health}/{maxHealth} dist={(int)MathF.Round(gap)}]");
            }
        }

        GD.Print($"[enemycastcheck] 带技能敌人 {withSkills.Count}，放出技能 {cast}，" +
                 $"没放 {idle.Count}，失败 {failures.Count}");
        if (idle.Count > 0) GD.Print($"[enemycastcheck]   没放：{string.Join("、", idle)}");
        foreach (var entry in failures) GD.PrintErr($"[enemycastcheck]   失败 {entry}");

        var ok = failures.Count == 0;
        GD.Print(ok ? "[enemycastcheck] 通过" : "[enemycastcheck] 失败");
        GetTree().Quit(ok ? 0 : 1);
    }

    private EnemyNode? SpawnEnemyForCheck(Inotia4.Domain.Content.EnemyDefinition definition, Vec2 position)
    {
        var node = new EnemyNode { Name = definition.Id };
        _entities.AddChild(node);
        node.SetupEnemy(definition, _textures);
        node.TeleportTo(new Vector2(position.X, position.Y));
        node.Home = position;
        _enemies.Add(node);
        AttachEnemyCombat(node);
        return node;
    }

    private void RunConditionCheck()
    {
        var ok = true;
        var catalog = _loader.Skills;
        var loadout = new SkillLoadout();
        foreach (var id in catalog.Class(_classId)?.SkillIds ?? []) loadout.Levels[id] = 1;
        var passives = PassiveSkills.PassiveModifiers(catalog, _classId, loadout, _panel.WeaponKind);

        var conditions = new CombatConditions();
        var view = conditions.FighterView(PlayerCombatId, _playerFighter, passives);
        GD.Print($"[conditioncheck] 被动 {passives.Count} 条（武器 {_panel.WeaponKind}）：" +
                 $"防御 {_playerFighter.Defense}→{view.Defense}，减伤 {_playerFighter.PhysicalResistance}→" +
                 $"{view.PhysicalResistance}，盾挡 {view.ShieldBlock}，物攻 {_playerFighter.PhysicalAttack}→{view.PhysicalAttack}");

        if (catalog.Skills.GetValueOrDefault("dark-strike")?.Action?.CombatUnits is { } units &&
            units.OfType<BuffCombatUnit>().FirstOrDefault() is { Ranks.Count: > 0 } buffUnit)
        {
            var rank = buffUnit.Ranks[0];
            conditions.ApplyBuff(PlayerCombatId, buffUnit.Buff, rank.Strength, rank.Effect);
            var bonus = conditions.Attribute(PlayerCombatId, "skillDamageBonus", 0);
            var remaining = conditions.Snapshot(PlayerCombatId).FirstOrDefault(entry => entry.Kind == "buff");
            GD.Print($"[conditioncheck] 挂上 {buffUnit.Buff}（强度 {rank.Strength}）：" +
                     $"skillDamageBonus={bonus}，寿命 {conditions.BuffLifetimeFrames(PlayerCombatId, buffUnit.Buff)} 帧，" +
                     $"剩余 {remaining?.RemainingMs:F0} 毫秒");
            ok &= bonus == 210;

            // 推两帧：持续特效的第一帧年龄是 1，所以这时应该已经该播了。
            conditions.Update(120);
            var loops = conditions.BuffLoopFrames(PlayerCombatId);
            GD.Print($"[conditioncheck] 推进 2 帧后持续特效 {loops.Count} 条" +
                     (loops.Count > 0 ? $"（{string.Join(",", loops.Select(loop => loop.Effect))}）" : string.Empty));
            ok &= loops.Count > 0;

            conditions.RemoveBuff(PlayerCombatId, buffUnit.Buff);
            ok &= conditions.Attribute(PlayerCombatId, "skillDamageBonus", 0) == 0;
        }
        else
        {
            GD.PrintErr("[conditioncheck] 找不到 dark-strike 的增益单元");
            ok = false;
        }

        conditions.ApplyState(PlayerCombatId, "stun", 1000);
        ok &= conditions.CannotAct(PlayerCombatId) && !conditions.CanMove(PlayerCombatId);
        conditions.RemoveState(PlayerCombatId, "stun");
        ok &= conditions.CanAct(PlayerCombatId);
        conditions.ApplyState(PlayerCombatId, "stealthed", 1000);
        ok &= !conditions.CanTarget(PlayerCombatId) && conditions.CanTarget(PlayerCombatId, hostile: false);
        conditions.Remove(PlayerCombatId);

        GD.Print($"[conditioncheck] 状态门：眩晕禁行动、隐身只挡敌对选取={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginLampCheck()
    {
        _autoStory = true;   // 对白自动翻页
        _hold = true;        // 剧本播完别退出，这里要自己判

        for (var wait = 0; wait < 3600 && _storyPlaying; wait += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var lampShop = _loader.Shops.LampShop;
        _bag.Add("lamp");
        var lampsBefore = _bag.Count("lamp");

        ToggleBag();
        var result = _bagPanel?.DebugUse("lamp");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var consumed = _bag.Count("lamp") == lampsBefore - 1;
        var summoned = _npcs.FirstOrDefault(npc => npc.CharacterId == "npc-7");
        var bagClosed = _bagPanel is not { IsOpen: true };

        var shopOpen = false;
        if (summoned is not null)
        {
            _session.Moved(new Vec2(summoned.Node.Position.X + 16f, summoned.Node.Position.Y), "west");
            _player.Position = new Vector2(_position.X, _position.Y);
            UpdateInteraction();
            if (_nearNpc is { } near && near.CharacterId == "npc-7")
            {
                TryInteract();
                shopOpen = _shopPanel is { IsOpen: true } && _shopPanel.DebugShopName == "神灯商人";
            }
        }

        var ok = result is not null && consumed && bagClosed && summoned is not null && shopOpen;
        GD.Print($"[lampcheck] 使用{(result is null ? "未执行" : "成功")}，神灯 {lampsBefore}→{_bag.Count("lamp")}，" +
                 $"商人{(summoned is null ? "没出现" : $"已现身 ({summoned.Node.Position.X:F0},{summoned.Node.Position.Y:F0})")}，" +
                 $"背包{(bagClosed ? "已关" : "还开着")}，交互开店{(shopOpen ? $"成功（{lampShop}）" : "失败")}，" +
                 $"{(ok ? "通过" : "失败")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginDefeatCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var ok = true;
        if (!_checkpoint.Available)
        {
            GD.Print("[defeatcheck] 没有检查点，无法验证回退");
            GetTree().Quit(1);
            return;
        }

        _defeatPanel!.AutoConfirm = false;

        _bag!.Add("resurrection-prayer");
        var prayersBefore = PrayerCountInBag();

        void StepFrames(int count)
        {
            for (var frame = 0; frame < count; frame += 1) UpdateCombat(0.06f);
        }

        // 起一次普攻看请求结果，然后立刻收掉：只验「能不能出招」，不推进动作。
        // 面前没有活靶子时请求会以「找不到目标」拒绝，和「控制器没登记」分不出来，
        // 所以先摆一只（竞技场那只蝙蝠）在脚边。
        string AttackProbe()
        {
            EnsurePlayerCombat();
            if (!_enemies.Any(enemy => !enemy.Dead)) SpawnArena();
            foreach (var enemy in _enemies.Where(enemy => !enemy.Dead))
            {
                enemy.TeleportTo(_player.Position + new Vector2(14f, 0f));
                enemy.RestoreHealth(enemy.Fighter.MaxHealth);
                enemy.HaltMotion();
            }

            var result = CombatRuntime.Request(ControlledId);
            CombatRuntime.CancelAttack(ControlledId);
            foreach (var enemy in _enemies.ToList())
            {
                CancelEnemyAction(enemy);
                _enemies.Remove(enemy);
                if (GodotObject.IsInstanceValid(enemy)) enemy.QueueFree();
            }
            GD.Print($"[defeatcheck]   探针：被控者 {ControlledId}，靶子 {_enemies.Count(enemy => !enemy.Dead)}，" +
                     $"暂停 {CombatRuntime.Paused}");
            return result switch
            {
                CombatRequestStarted => "started",
                CombatRequestRejected rejected => rejected.Reason ?? "rejected",
                _ => result.GetType().Name,
            };
        }

        SetPlayerHealth(0);
        for (var guard = 0; guard < 40 && !_defeated; guard += 1) StepFrames(1);

        ok &= _defeated;
        ok &= _fallen.Contains(PlayerCombatId);
        ok &= _defeatPanel is { IsOpen: true };
        GD.Print($"[defeatcheck] 倒下：阵亡标记 {_defeated}，倒下名单 {string.Join(",", _fallen)}，" +
                 $"面板 {(_defeatPanel?.IsOpen == true ? "已弹" : "没弹")}");

        await _defeatPanel!.ConfirmForCheck(prayer: true);
        CombatRuntime.SetPaused("control", false);
        ok &= _playerHealth > 0;
        ok &= !_defeated;
        ok &= _defeatPanel.IsOpen == false;
        ok &= PrayerCountInBag() == prayersBefore - 1;
        ok &= (_outcomes?.ActiveEffects ?? 0) > 0;
        GD.Print($"[defeatcheck] 复活圣文：生命 {_playerHealth}，剩余 {PrayerCountInBag()} 张（原 {prayersBefore}），" +
                 $"恢复特效 {_outcomes?.ActiveEffects ?? 0} 个");

        // 复活之后必须还能出招：控制器在运行时里，请求要能真的开起来。
        var prayerAttack = AttackProbe();
        ok &= prayerAttack == "started";
        GD.Print($"[defeatcheck] 复活后普攻：{prayerAttack}");

        SetPlayerHealth(0);
        for (var guard = 0; guard < 40 && !_defeated; guard += 1) StepFrames(1);
        ok &= _defeated;

        _player.Modulate = new Color(1f, 1f, 1f, 0f);   // 实机淡出后的残留
        await _defeatPanel.ConfirmForCheck(prayer: false);
        ok &= !_defeated && _defeatPanel.IsOpen == false;
        ok &= PrayerCountInBag() == 0;   // 检查点记在塞圣文之前
        ok &= _playerHealth > 0;
        // 倒下淡出的收尾会把跨图复用的主角节点藏起来；回退到检查点后必须重新摆亮，
        // 否则人物模型在复活点直接消失（这一条守住那个回归）。
        ok &= _player.Visible;
        ok &= _player.Modulate.A > 0.99f;
        GD.Print($"[defeatcheck] 返回检查点：地图 {_map.Id}，生命 {_playerHealth}，圣文 {PrayerCountInBag()} 张，" +
                 $"模型可见 {_player.Visible}，透明度 {_player.Modulate.A:F2}");

        // 回检查点等于整份战斗世界重建：主角的控制器要重新登记进新的运行时，
        // 否则按攻击键只会拿到 "no-target"（表现就是「重生之后不能普攻」）。
        // 正式流程里这一句由 _PhysicsProcess 每帧写（面板收起后 enabled 恢复 true）。
        CombatRuntime.SetPaused("control", false);
        var restoreAttack = AttackProbe();
        ok &= restoreAttack == "started";
        GD.Print($"[defeatcheck] 回检查点后普攻：{restoreAttack}");

        GD.Print(ok ? "[defeatcheck] 通过" : "[defeatcheck] 失败");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginAttackRepeatCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[attackrepeatcheck] {_map?.Id} 场上没有敌人，换一张有怪的地图（例如 training-dungeon-two）");
            GetTree().Quit(1);
            return;
        }

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);
        var home = target.Position;
        target.Position = new Vector2(_position.X + 14f, _position.Y);
        target.RestoreHealth(target.Fighter.MaxHealth);

        _autoAttack.Engage();
        PlayerAttack();
        var engaged = _autoAttack.Active;
        var firstStarted = PlayerActionState != CombatState.Idle;

        _playerContacts = 0;
        _playerDispatchedFrames = 0;
        var frames = 0;
        for (; frames < 120 && !target.Dead; frames += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
        }

        var swings = _playerContacts;

        // 摇杆一动：连打立即解除；等手上那一击落完再取基线，之后站着也不该再出手。
        StepPlayerMotion(new Vec2(0.6f, 0.8f), 1f / 60f);
        var released = !_autoAttack.Active;
        for (var frame = 0; frame < 20 && !target.Dead; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
        }

        var afterMoveBaseline = _playerContacts;
        for (var frame = 0; frame < 60 && !target.Dead; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
        }

        var afterMoveSwings = _playerContacts - afterMoveBaseline;

        var killed = target.Dead;
        target.Position = home;
        SetInvincible(PlayerCombatId, false);
        var ok = engaged && firstStarted && swings >= 2 && released && afterMoveSwings == 0;
        GD.Print($"[attackrepeatcheck] 按下后开门={engaged}、首击={firstStarted}，" +
                 $"站 {frames} 帧自动接上 {swings} 击（目标{(killed ? "被打死" : "还活着")}）；" +
                 $"推杆后解除={released}，再站 60 帧自动出手 {afterMoveSwings} 次");
        GD.Print(ok ? "[attackrepeatcheck] 通过" : "[attackrepeatcheck] 失败");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// <c>--attackanimcheck</c>：连续普攻（站定连打）时人物到底在播什么。
    ///
    /// 每一下普攻都会从第 0 帧重播同一套片段——内容里每组武器只有一套攻击动作，
    /// 所以连打的画面是「同一段动作重复」。这一项把每一击实际播到的动画名与帧序列打出来，
    /// 顺便守住三件事：动作名是这套普攻的片段、帧号逐帧推进、每一击都从 0 重新开始。
    /// </summary>
    private async void BeginAttackAnimCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[attackanimcheck] {_map?.Id} 场上没有敌人，换一张有怪的地图");
            GetTree().Quit(1);
            return;
        }

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);
        var home = target.Position;
        target.Position = new Vector2(_position.X + 14f, _position.Y);
        target.RestoreHealth(target.Fighter.MaxHealth);

        var recipeProbe = CostumeRecipeFor(PlayerCombatId, _classId);
        GD.Print($"[attackanimcheck] 换动作入口：狂气 {AttributeOf(PlayerCombatId, "frenzyAttackChance")}‰、" +
                 $"副手 {AttributeOf(PlayerCombatId, "offhandAttackChance")}‰、姿态 {CurrentStanceKey()}、" +
                 $"狂气动作 {(recipeProbe?.FrenzyAttack is null ? "无" : "有")}、" +
                 $"双持动作 {(recipeProbe?.DualWieldAttack is null ? "无" : "有")}、" +
                 $"战栗的狂气 {SkillRankOrZero("insanity-thrills")} 级");

        _autoAttack.Engage();
        PlayerAttack();

        var swingCount = _playerContacts;
        var hits = new List<List<(string Anim, int Frame)>>();
        List<(string Anim, int Frame)>? current = null;
        var lastFrame = -1;
        for (var frame = 0; frame < 200 && !target.Dead; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);

            var anim = _player.Visual.Animation ?? string.Empty;
            var index = _player.Visual.Frame;
            if (current is null || (index == 0 && lastFrame > 0))
            {
                current = [];
                hits.Add(current);
            }
            current.Add((anim, index));
            lastFrame = index;
            swingCount = _playerContacts;
        }

        // 取样是逐逻辑帧的：一击的画面由两段组成——动作片段本身，以及素材播完之后按原版口径
        // 改画的待机取模帧（idlePoseFrame），所以尾帧可能来自 idle-<朝向>。
        // 采样点在手上一帧派发之后，所以每击看到的第一个动作帧可能是 1（第 0 帧在两次采样之间）。
        var lines = new List<string>();
        var problems = new List<string>();
        var expected = _playerAttack?.Clips.GetValueOrDefault(_player.Facing);
        var idle = "idle-" + _player.Facing;
        var repeats = new List<string>();
        foreach (var (index, samples) in hits.Select((value, index) => (index, value)))
        {
            if (samples.Count < 3) continue; // 连打收尾那一小段不是完整一击
            var names = samples.Select(sample => sample.Anim).Distinct().ToArray();
            var attackFrames = samples.Where(sample => sample.Anim == expected).Select(sample => sample.Frame)
                .ToArray();
            lines.Add($"第 {index + 1} 击：{string.Join("/", names)}，帧 {string.Join(",", attackFrames)}");
            repeats.Add(string.Join(",", attackFrames));

            if (expected is not null && names.Any(name => name != expected && name != idle))
                problems.Add($"第 {index + 1} 击播了 {string.Join("/", names)}，应为 {expected}（尾帧可为 {idle}）");
            if (attackFrames.Distinct().Count() < 3)
                problems.Add($"第 {index + 1} 击只画了 {attackFrames.Distinct().Count()} 个动作帧，人物没动");
            for (var i = 1; i < attackFrames.Length; i += 1)
            {
                if (attackFrames[i] < attackFrames[i - 1])
                {
                    problems.Add($"第 {index + 1} 击的帧号回退（{attackFrames[i - 1]}→{attackFrames[i]}）");
                    break;
                }
            }
        }

        target.Position = home;
        SetInvincible(PlayerCombatId, false);

        foreach (var line in lines) GD.Print($"[attackanimcheck] {line}");
        var clipName = _playerAttack?.Clips.GetValueOrDefault(_player.Facing);
        var clipFrames = clipName is not null && _player.Definition.Clips.TryGetValue(clipName, out var clip)
            ? clip.Frames : 0;
        GD.Print($"[attackanimcheck] 人物素材 {_player.Definition.Id}，{clipName} {clipFrames} 帧，" +
                 $"有效周期 {_playerAttack?.EffectiveCycleFrames ?? 0}，当前姿态 {CurrentStanceKey()}");
        var identical = repeats.Count > 0 && repeats.All(frames => frames == repeats[0]);
        GD.Print($"[attackanimcheck] 每击画面一致：{identical}（{repeats.Count} 击取样）");
        var ok = lines.Count >= 2 && problems.Count == 0;
        GD.Print($"[attackanimcheck] 连打 {swingCount} 击，完整取样 {lines.Count} 击；" +
                 $"每击重播同一套片段（内容里每组武器只有一套攻击动作）");
        GD.Print($"[attackanimcheck] 通过：{ok}" +
                 (ok ? string.Empty : $"（{string.Join("，", problems)}）"));
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginAttackCycleCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[attackcyclecheck] {_map?.Id} 场上没有敌人，换一张有怪的地图");
            GetTree().Quit(1);
            return;
        }

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);
        var home = target.Position;
        target.Position = new Vector2(_position.X + 14f, _position.Y);
        target.RestoreHealth(target.Fighter.MaxHealth);

        var recorded = new List<(string Name, string Variant)>();
        EffectSpawnedHook = (name, variant) => recorded.Add((name, variant));

        _autoAttack.Engage();
        PlayerAttack();

        for (var frame = 0; frame < 480 && recorded.Count(name => _playerAttack?.Definition.Swings.Contains(name.Name) == true) < 7 && !target.Dead; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
        }

        EffectSpawnedHook = null;
        target.Position = home;
        SetInvincible(PlayerCombatId, false);

        var swingNames = _playerAttack?.Definition.Swings ?? [];
        var swings = recorded.Where(entry => swingNames.Contains(entry.Name))
            .Select(entry => entry.Variant).ToList();
        foreach (var (index, variant) in swings.Select((value, index) => (index, value)))
            GD.Print($"[attackcyclecheck] 第 {index + 1} 击刀光方向：{variant}");

        // 目标放在正右方，角色出手前会转过去，所以每一击都该是同一个方向。
        var facing = _player?.Facing ?? "?";
        var same = swings.Count > 0 && swings.All(variant => variant == facing);
        var ok = swings.Count >= 4 && same;
        GD.Print($"[attackcyclecheck] {swings.Count} 击刀光方向 [{string.Join("→", swings)}]，" +
                 $"角色朝向 {facing}：{ok}");
        GD.Print($"[attackcyclecheck] 通过：{ok}" +
                 (ok ? string.Empty : "（连打时刀光方向应当始终等于出手朝向）"));
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginFrenzyCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        if (_skills is null || _classId is null || SkillCatalog.Class(_classId) is not { } character)
        {
            GD.Print("[frenzycheck] 没有主角技能配置，先 --classselect warrior --classstart");
            GetTree().Quit(1);
            return;
        }

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[frenzycheck] {_map?.Id} 场上没有敌人，换一张有怪的地图");
            GetTree().Quit(1);
            return;
        }

        var levels = new Dictionary<string, int>();
        foreach (var id in character.SkillIds) levels[id] = SkillRankOrZero(id);
        levels["insanity-thrills"] = 5;
        _skills.DebugInstallLevels(_classId, levels);

        var chance = AttributeOf(PlayerCombatId, "frenzyAttackChance");
        GD.Print($"[frenzycheck] 战栗的狂气 5 级，狂气概率 {chance}‰，姿态 {CurrentStanceKey()}");

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);
        var home = target.Position;
        target.Position = new Vector2(_position.X + 14f, _position.Y);
        target.RestoreHealth(target.Fighter.MaxHealth);

        _autoAttack.Engage();
        PlayerAttack();

        var swings = new List<HashSet<string>>();
        HashSet<string>? current = null;
        var lastFrame = -1;
        for (var frame = 0; frame < 1500; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
            if (target.Dead)
            {
                target.RestoreHealth(target.Fighter.MaxHealth);
                target.Position = new Vector2(_position.X + 14f, _position.Y);
            }

            var anim = _player.Visual.Animation ?? string.Empty;
            var index = _player.Visual.Frame;
            if (current is null || index == 0 && lastFrame > 0)
            {
                current = [];
                swings.Add(current);
            }
            current.Add(anim);
            lastFrame = index;
        }

        target.Position = home;
        SetInvincible(PlayerCombatId, false);

        var complete = swings.Where(set => set.Count > 0).ToList();
        var frenzyHits = complete.Count(set => set.Any(name => name.StartsWith("frenzy-", StringComparison.Ordinal)));
        var normalHits = complete.Count(set => set.Any(name => name.StartsWith("attack-", StringComparison.Ordinal)));
        var ratio = complete.Count == 0 ? 0 : frenzyHits * 100 / complete.Count;
        GD.Print($"[frenzycheck] {complete.Count} 击里 {frenzyHits} 击换成狂气动作、{normalHits} 击是普通动作，" +
                 $"狂气占比 {ratio}%（配置 {chance}‰）");

        var expectedFrenzy = CostumeRecipeFor(PlayerCombatId, _classId)?.FrenzyAttack?.Clips
            .GetValueOrDefault(_player.Facing);
        var problems = new List<string>();
        if (frenzyHits == 0) problems.Add("一次狂气都没出，换动作这条路没接上");
        if (expectedFrenzy is not null && complete.Any(set =>
                set.Any(name => name.StartsWith("frenzy-", StringComparison.Ordinal) && name != expectedFrenzy)))
            problems.Add($"换了别的狂气片段，应为 {expectedFrenzy}");

        var ok = chance > 0 && problems.Count == 0;
        GD.Print($"[frenzycheck] 通过：{ok}" +
                 (ok ? $"（狂气动作 {expectedFrenzy}）" : $"（{string.Join("，", problems)}）"));
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginDualCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        if (_bag is null)
        {
            GD.Print("[dualcheck] 背包还没建出来");
            GetTree().Quit(1);
            return;
        }

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[dualcheck] {_map?.Id} 场上没有敌人，换一张有怪的地图");
            GetTree().Quit(1);
            return;
        }

        string mainEntry;
        string offEntry;
        try
        {
            _bag.Add("basic-longsword");
            mainEntry = _bag.Entries.First(entry => entry.ItemId == "basic-longsword").Id;
            _bag.Equip(mainEntry, "main-hand", "player", _classId, _hero?.Level ?? 1);
            _bag.Add("basic-dagger");
            offEntry = _bag.Entries.First(entry => entry.ItemId == "basic-dagger").Id;
            _bag.Equip(offEntry, "off-hand", "player", _classId, _hero?.Level ?? 1);
        }
        catch (Exception error)
        {
            GD.Print($"[dualcheck] 换装备失败：{error.Message}；职业 {_classId}，" +
                     $"双持职业表 [{string.Join(",", _loader.Items.DualWieldClasses)}]，" +
                     $"槽位 主={string.Join("/", _bag.SlotsFor(_bag.Entries[^1].Id, _classId))}");
            GetTree().Quit(1);
            return;
        }

        ApplyPlayerOutfit();
        UpdateEquipment();

        var chance = AttributeOf(PlayerCombatId, "offhandAttackChance");
        GD.Print($"[dualcheck] 姿态 {CurrentStanceKey()}，副手出手率 {chance}‰");

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);
        var home = target.Position;
        target.Position = new Vector2(_position.X + 14f, _position.Y);
        target.RestoreHealth(target.Fighter.MaxHealth);

        _autoAttack.Engage();
        PlayerAttack();

        var swings = new List<HashSet<string>>();
        HashSet<string>? current = null;
        var lastFrame = -1;
        for (var frame = 0; frame < 1500; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
            if (target.Dead)
            {
                target.RestoreHealth(target.Fighter.MaxHealth);
                target.Position = new Vector2(_position.X + 14f, _position.Y);
            }

            var anim = _player.Visual.Animation ?? string.Empty;
            var index = _player.Visual.Frame;
            if (current is null || index == 0 && lastFrame > 0)
            {
                current = [];
                swings.Add(current);
            }
            current.Add(anim);
            lastFrame = index;
        }

        target.Position = home;
        SetInvincible(PlayerCombatId, false);

        var complete = swings.Where(set => set.Count > 0).ToList();
        var dualHits = complete.Count(set => set.Any(name =>
            name.StartsWith("dual-attack-", StringComparison.Ordinal) ||
            name.StartsWith("dual-quick-attack-", StringComparison.Ordinal)));
        var normalHits = complete.Count(set => set.Any(name => name.StartsWith("attack-", StringComparison.Ordinal)));
        var ratio = complete.Count == 0 ? 0 : dualHits * 100 / complete.Count;
        GD.Print($"[dualcheck] {complete.Count} 击里 {dualHits} 击是左手一击、{normalHits} 击是普通攻击，" +
                 $"占比 {ratio}%（配置 {chance}‰）");

        var expected = CostumeRecipeFor(PlayerCombatId, _classId)?.DualWieldAttack?.Clips
            .GetValueOrDefault(_player.Facing);
        var problems = new List<string>();
        if (dualHits == 0) problems.Add("一次左手一击都没出，双持这条路没接上");
        if (expected is not null && complete.Any(set =>
                set.Any(name => (name.StartsWith("dual-attack-", StringComparison.Ordinal) ||
                                 name.StartsWith("dual-quick-attack-", StringComparison.Ordinal)) && name != expected)))
            problems.Add($"换了别的双持片段，应为 {expected}");

        var ok = chance > 0 && problems.Count == 0;
        GD.Print($"[dualcheck] 通过：{ok}" +
                 (ok ? $"（双持动作 {expected}）" : $"（{string.Join("，", problems)}）"));

        _bag.Unequip("main-hand", "player");
        _bag.Remove(mainEntry, 1);
        _bag.Remove(offEntry, 1);
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginAttackShot()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var arguments = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(arguments, "--attackshot");
        var targetFrame = index + 1 < arguments.Length && int.TryParse(arguments[index + 1], out var parsed)
            ? parsed
            : 3;
        var direction = index + 2 < arguments.Length ? arguments[index + 2] : "east";
        var targetOffset = direction switch
        {
            "west" => new Vec2(-14f, 0f),
            "south" => new Vec2(0f, 14f),
            "north" => new Vec2(0f, -14f),
            _ => new Vec2(14f, 0f),
        };

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[attackshot] {_map?.Id} 场上没有敌人，换一张有怪的地图");
            GetTree().Quit(1);
            return;
        }

        EnsurePlayerCombat();
        SetInvincible(PlayerCombatId, true);
        target.Position = new Vector2(_position.X + targetOffset.X, _position.Y + targetOffset.Y);
        target.RestoreHealth(target.Fighter.MaxHealth);

        _autoAttack.Engage();
        PlayerAttack();

        for (var frame = 0; frame < 240; frame += 1)
        {
            StepPlayerMotion(Vec2.Zero, 1f / 60f);
            UpdateCombat(0.06f);
            if (_player.Visual.Frame >= targetFrame) break;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var swings = _playerAttack?.Definition.Swings ?? [];
        GD.Print($"[attackshot] 靶位 {direction}，动作 {_player.Visual.Animation} 第 {_player.Visual.Frame} 帧，" +
                 $"这套普攻的挥击 {string.Join("+", swings)}（{_playerAttack?.Definition.SwingPlacement ?? "world"}），" +
                 $"已命中 {_playerContacts} 次");
        SaveShot();
    }

    private async void BeginNameBannerCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (_nameBanner is not { } banner || _map is null)
        {
            GD.Print("[bannercheck] 名条没建出来，或地图还没载入");
            GetTree().Quit(1);
            return;
        }

        var mapName = _map.Name;
        var problems = new List<string>();

        var viewportWidth = GetViewportRect().Size.X;
        var bannerCenter = banner.Position.X + banner.Size.X * 0.5f;
        if (banner.Size != new Vector2(480f, 34f) ||
            MathF.Abs(bannerCenter - viewportWidth * 0.5f) > 0.5f ||
            MathF.Abs(banner.Position.Y - 24f) > 0.5f)
            problems.Add($"落点 {banner.Position}/{banner.Size}，应在 {viewportWidth:F0} 宽的视口里居中、顶边 24");

        banner.Setup(mapName).Sync(true, null);
        var started = (banner.Text, banner.Alpha);
        banner.Advance(NameBanner.FadeInSeconds * 0.5f);
        var midIn = banner.Alpha;
        banner.Advance(NameBanner.FadeInSeconds);
        var full = banner.Alpha;
        banner.Advance(NameBanner.HoldSeconds * 0.5f);
        var halfHold = banner.Alpha;
        banner.Advance(NameBanner.HoldSeconds);
        banner.Advance(NameBanner.FadeOutSeconds * 0.5f);
        var midOut = banner.Alpha;
        banner.Advance(NameBanner.FadeOutSeconds);
        var stillShown = banner.Showing;

        if (started.Text != mapName) problems.Add($"首帧文字「{started.Text}」，应为地图名「{mapName}」");
        if (started.Alpha > 0.25f) problems.Add($"起步透明度 {started.Alpha:F2}，应从 0 淡入");
        if (midIn is < 0.3f or > 0.85f) problems.Add($"半程透明度 {midIn:F2}，应在淡入中段");
        if (full < 0.99f) problems.Add($"淡入结束透明度 {full:F2}，应为 1");
        if (halfHold < 0.99f) problems.Add($"停留期间透明度 {halfHold:F2}，应保持 1");
        if (midOut is < 0.3f or > 0.85f) problems.Add($"淡出半程透明度 {midOut:F2}，应在淡出中段");
        if (stillShown) problems.Add("淡出结束还在显示");

        // ②′ 底衬跟着文字收窄：地名一般就四五个字，不该铺满 480 压到左上角的 HUD 上。
        var (shortBackdropPos, shortBackdropSize) = banner.Backdrop;
        if (shortBackdropSize.X >= 480f)
            problems.Add($"地名「{mapName}」的底衬还是满宽 480，没有收窄");
        if (shortBackdropPos.X <= 0f)
            problems.Add($"底衬左边缘在 {shortBackdropPos.X:F0}，应收窄后右移");
        if (MathF.Abs(shortBackdropSize.Y - NameBannerRules.Height) > 0.01f)
            problems.Add($"底衬高 {shortBackdropSize.Y:F0}，应为 {NameBannerRules.Height:F0}");
        if (MathF.Abs(shortBackdropPos.Y - NameBannerRules.BackdropTop(banner.Size.Y)) > 0.01f)
            problems.Add($"底衬顶边 {shortBackdropPos.Y:F0}，应在名条里居中");
        if (MathF.Abs(shortBackdropPos.X * 2f + shortBackdropSize.X - banner.Size.X) > 0.01f)
            problems.Add("底衬没有水平居中");

        banner.Sync(true, "一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十");
        var longScale = banner.TextScale;
        var (longBackdropPos, longBackdropSize) = banner.Backdrop;
        if (longScale >= 1f) problems.Add($"30 字的名字没有缩小（scale {longScale:F2}）");
        if (MathF.Abs(longBackdropSize.X - NameBannerRules.MaxWidth) > 0.01f)
            problems.Add($"长名字的底衬宽 {longBackdropSize.X:F0}，应顶到上限 {NameBannerRules.MaxWidth:F0}");
        if (MathF.Abs(longBackdropPos.X) > 0.01f)
            problems.Add($"长名字的底衬左边缘在 {longBackdropPos.X:F0}，应贴齐名条左缘");

        var npc = _npcs.Where(n => n.Definition.Name.Length > 0 && n.ChestKind is null && !n.Spring)
            .OrderByDescending(n => _loader.Shops.Shops.ContainsKey(n.ActorId) ||
                                    _journal.SlotFor(n.ActorId, n.CharacterId) is not null)
            .FirstOrDefault();
        string? npcName = npc?.Definition.Name;
        var targetShown = false;
        var targetAlpha = 0f;
        var leftHidden = false;
        if (npc is not null)
        {
            var beside = new Vec2(npc.Node.Position.X + 16f, npc.Node.Position.Y);
            _session.Moved(beside, "west");
            _player.Position = new Vector2(beside.X, beside.Y);
            UpdateInteraction();
            SyncNameBanner();
            targetShown = banner.Showing && banner.Text == npcName;
            targetAlpha = banner.Alpha;

            Vec2? empty = null;
            foreach (var offset in new[]
                     {
                         new Vec2(320f, 0f), new Vec2(-320f, 0f),
                         new Vec2(0f, 320f), new Vec2(0f, -320f),
                     })
            {
                var candidate = beside + offset;
                _session.Moved(candidate, "west");
                _player.Position = new Vector2(candidate.X, candidate.Y);
                UpdateInteraction();
                if (_nearNpc is not null) continue;
                empty = candidate;
                break;
            }

            SyncNameBanner();
            leftHidden = empty is not null && !banner.Showing;
            if (empty is null) problems.Add("四个方向 320 像素内都有可交互对象，找不到「身边没人」的位置");
        }
        else
        {
            problems.Add("这张图上没有可交互的 NPC，换一张有人的图");
        }

        if (!targetShown) problems.Add($"贴到 {npcName ?? "对象"} 旁边没显示它的名字（现为「{banner.Text}」）");
        if (targetAlpha < 0.99f) problems.Add($"对象名透明度 {targetAlpha:F2}，应为 1");
        if (!leftHidden) problems.Add("走开之后名条还挂着");

        // ⑤ 输入被接管时冻结计时：暂停一秒也不该往下淡。
        banner.Setup(mapName).Sync(true, null);
        banner.Advance(0.05f);
        banner.Sync(false, null);
        var pausedAt = banner.Alpha;
        banner.Advance(1f);
        var stillPaused = banner.Alpha;
        banner.Sync(true, null);
        if (MathF.Abs(pausedAt - stillPaused) > 0.001f)
            problems.Add($"暂停期间透明度从 {pausedAt:F2} 变成了 {stillPaused:F2}");

        foreach (var problem in problems) GD.Print($"[bannercheck] 问题：{problem}");
        GD.Print($"[bannercheck] 地名「{mapName}」：半程 {midIn:F2} → 满 {full:F2} → 停留 {halfHold:F2} → " +
                 $"淡出 {midOut:F2} → 收起={!stillShown}；长名缩放 {longScale:F2}；" +
                 $"底衬 {shortBackdropSize.X:F0}×{shortBackdropSize.Y:F0} @({shortBackdropPos.X:F0},{shortBackdropPos.Y:F0})" +
                 $"（长名时 {longBackdropSize.X:F0}）；" +
                 $"对象名「{npcName}」显示={targetShown}，走开收起={leftHidden}");
        GD.Print(problems.Count == 0 ? "[bannercheck] 通过" : "[bannercheck] 失败");

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0 && npc is not null)
        {
            var beside = new Vec2(npc.Node.Position.X + 16f, npc.Node.Position.Y);
            _session.Moved(beside, "west");
            _player.Position = new Vector2(beside.X, beside.Y);
            UpdateInteraction();
            SyncNameBanner();
            return;
        }

        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private async void BeginReceiptCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var problems = new List<string>();

        var quest = _loader.Campaign.Quests.FirstOrDefault(candidate =>
            candidate.Completion != "all" && candidate.Completion != "internal" &&
            candidate.Rewards is { } reward &&
            (reward.Experience > 0 || reward.Money > 0 || reward.Items.Length > 0) &&
            _journal.StatusOf(candidate.Id) is QuestStatus.Locked or QuestStatus.Available);
        if (quest is null)
        {
            GD.Print("[receiptcheck] 找不到可以结算的任务，换一张图或换个存档");
            GetTree().Quit(1);
            return;
        }

        _journal.Accept(quest.Id, "script");
        _journal.Ready(quest.Id);
        var receipt = NpcInteraction.Settle(_loader.Campaign, _journal, quest.Id);
        if (receipt is null)
        {
            GD.Print($"[receiptcheck] 任务「{quest.Id}」结算失败：{_journal.LastRejection ?? "交付物不足"}");
            GetTree().Quit(1);
            return;
        }

        var cycle = _journal.Book.CompletedCycles(quest.Id) - 1;
        if (!_journal.HasReceipt(quest.Id, cycle))
            problems.Add($"结算完没记账（{quest.Id} 第 {cycle} 轮）");

        var json = JsonSerializer.Serialize(_session.CaptureState(), SaveGameStore.JsonOptions);
        var snapshot = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;
        if (snapshot.Receipts.Count == 0) problems.Add("存档 JSON 里没有回执");
        _session.RestoreState(snapshot);

        var restored = _journal.RewardReceiptFor(quest.Id);
        if (restored is null) problems.Add("读档后取不到这个任务的回执");
        else
        {
            if (restored.Experience != receipt.Experience)
                problems.Add($"读档后经验 {restored.Experience}，应为 {receipt.Experience}");
            if (restored.Money != receipt.Money)
                problems.Add($"读档后金币 {restored.Money}，应为 {receipt.Money}");
            if (restored.Items.Count != receipt.Items.Count)
                problems.Add($"读档后奖励物品 {restored.Items.Count} 项，应为 {receipt.Items.Count}");
        }

        // ② 同一轮再结算一次：任务簿会拒，回执账不能多出一条。
        var before = _journal.Receipts.Count;
        var duplicateRejected = false;
        try
        {
            _journal.Apply(QuestCommand.Complete(quest.Id));
        }
        catch (InvalidOperationException error)
        {
            duplicateRejected = error.Message.Contains("已结算");
        }

        if (!duplicateRejected && _journal.Receipts.Count != before)
            problems.Add("同一轮结算了两次，回执账多记账");

        _questPopup.AutoConfirm = false;
        _ = _questPopup.ShowAsync(quest, "complete", _journal.RewardReceiptFor(quest.Id));
        for (var frame = 0; frame < 4; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var body = _questPopup.BodyText;
        var experience = _journal.RewardReceiptFor(quest.Id)?.Experience ?? 0;
        if (experience > 0 && !body.Contains("经验值")) problems.Add($"完成弹窗没有奖励行（正文「{body}」）");

        foreach (var problem in problems) GD.Print($"[receiptcheck] 问题：{problem}");
        GD.Print($"[receiptcheck] 任务「{quest.Id}」第 {cycle} 轮：经验 {receipt.Experience}、金币 {receipt.Money}、" +
                 $"物品 {receipt.Items.Count} 项；读档后取回={(restored is not null)}；" +
                 $"重复结算被拒={duplicateRejected}；弹窗正文 {body.Length} 字");
        GD.Print(problems.Count == 0 ? "[receiptcheck] 通过" : "[receiptcheck] 失败");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// 插帧自检（<c>--interpcheck</c>）：确认工程开了物理插帧、镜头不参与、主角没被关掉，
    /// 并且落位与逐帧移动的逻辑位置照旧准确——插帧只借给画面，不参与任何判定。
    ///
    /// 为什么不断言「画面落后了多少」：2D 这边 C# 与 GDScript 都没有读取插帧后坐标的接口
    /// （插值发生在渲染服务器内部，取到的是这一帧该画的位置）。
    /// 所以这里只守「配置别被改回去」和「逻辑位置没被影响」，平滑本身靠人眼看一次。
    /// </summary>
    private async void BeginInterpCheck()
    {
        var problems = new List<string>();
        var enabled = ProjectSettings.GetSetting("physics/common/physics_interpolation", false).AsBool();
        if (!enabled) problems.Add("工程没有打开 physics/common/physics_interpolation");
        if (_camera.PhysicsInterpolationMode == Node.PhysicsInterpolationModeEnum.Off)
            problems.Add("镜头被关掉了插帧：世界会跟着镜头一起按时钟跳");
        if (_player.PhysicsInterpolationMode == Node.PhysicsInterpolationModeEnum.Off)
            problems.Add("主角节点被单独关掉了插帧");

        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var home = new Vec2(_position.X, _position.Y);
        var target = FindStandable(_grid, new Vec2(home.X + 240f, home.Y));
        TeleportPlayer(target, "south");
        var landed = _player.Position;
        var landingError = new Vec2(landed.X - _position.X, landed.Y - _position.Y).Length;
        var requestedOffset = new Vec2(landed.X - target.X, landed.Y - target.Y).Length;
        if (landingError > 1f) problems.Add($"瞬移后画面与会话位置差了 {landingError:F1} 像素");
        if (_player.PhysicsInterpolationMode == Node.PhysicsInterpolationModeEnum.Off)
            problems.Add("瞬移之后主角节点丢了插帧模式");

        // ② 逐帧移动：走 10 步，节点位置与会话位置必须一致——插帧只在画面上补中间帧，
        // 不能让逻辑读到「画到一半」的坐标。
        for (var step = 0; step < 10; step += 1) StepPlayerMotion(new Vec2(1f, 0f), 1f / 60f);
        var nodePosition = _player.Position;
        var drift = new Vec2(nodePosition.X - _position.X, nodePosition.Y - _position.Y).Length;
        if (drift > 0.5f) problems.Add($"走位后节点与会话位置差了 {drift:F2} 像素");

        TeleportPlayer(home, "south");
        foreach (var problem in problems) GD.Print($"[interpcheck] 问题：{problem}");
        GD.Print($"[interpcheck] 物理插帧={(enabled ? "开" : "关")}、镜头={_camera.PhysicsInterpolationMode}、" +
                 $"主角={_player.PhysicsInterpolationMode}；瞬移后画面与会话差 {landingError:F2} 像素" +
                 $"（落点被就地让开 {requestedOffset:F2} 像素），" +
                 $"走 10 步后位置漂移 {drift:F2} 像素");
        GD.Print(problems.Count == 0 ? "[interpcheck] 通过" : "[interpcheck] 失败");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// 冷却扇形自检（<c>--cooldowncheck</c>）：
    /// ① 真实链路——给被控者挂一条技能冷却，看界面是否画出扇形、起点是不是满的、
    /// 冷却期间图标是否**没有**被压暗（原版口径）、格子是否点不动，冷却走完是否收起；
    /// ② 合成一组格子，核对「不可用且不在冷却」才压到 0.4。
    /// </summary>
    /// <summary>把快捷栏第一格技能的冷却摆到一半，留给 <c>--shot</c> 截图核对扇形形状。</summary>
    private async void BeginCooldownShot()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var slots = _actionControls.Slots;
        var index = -1;
        for (var i = 0; i < slots.Count; i += 1)
            if (slots[i].Entry is { Kind: ShortcutEntry.SkillKind }) { index = i; break; }
        if (index < 0)
        {
            GD.Print("[cooldownshot] 快捷栏上没有技能，跳过");
            return;
        }

        var port = _session.Cooldowns.ForActor(ControlledId);
        port.Start(slots[index].Entry!.Id, 600);
        _combatClock.Advance(300 * CombatClock.LogicFrameMs);
        RefreshShortcutPage();
        GD.Print($"[cooldownshot] 第 {index} 格摆到半程：{_actionControls.DebugSlot(index)}，" +
                 $"屏幕中心 {_actionControls.DebugSlotCenter(index)}");
    }

    private async void BeginCooldownCheck()
    {
        var problems = new List<string>();
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var notes = new List<string>();
        var slots = _actionControls.Slots.ToArray();

        var index = Array.FindIndex(slots, slot => slot.Entry is { Kind: ShortcutEntry.SkillKind });
        var skillId = index >= 0 ? slots[index].Entry!.Id : null;
        if (index < 0)
        {
            notes.Add("快捷栏上没有技能，跳过真实链路");
        }
        else
        {
            var port = _session.Cooldowns.ForActor(ControlledId);
            port.Start(skillId!, 120);
            var duration = port.Snapshot().GetValueOrDefault(skillId!)?.DurationMs ?? 0;
            RefreshShortcutPage();
            var start = _actionControls.DebugSlot(index);
            if (!start.FanActive) problems.Add("技能进冷却后快捷位没有画扇形");
            if (start.FanProgress > 0.05f) problems.Add($"扇形起点不是满的：{start.FanProgress:F2}");
            if (start.IconAlpha < 0.99f) problems.Add($"冷却期间图标被压暗了：alpha={start.IconAlpha:F2}");
            if (!start.Disabled) problems.Add("冷却期间格子还能点");

            _combatClock.Advance(duration / 2);
            RefreshShortcutPage();
            var middle = _actionControls.DebugSlot(index);
            if (middle.FanProgress is < 0.35f or > 0.65f)
                problems.Add($"冷却走到一半，扇形进度是 {middle.FanProgress:F2}");
            if (middle.IconAlpha < 0.99f) problems.Add($"冷却途中图标被压暗了：alpha={middle.IconAlpha:F2}");
            if (!middle.Disabled) problems.Add("冷却途中格子还能点");

            _combatClock.Advance(duration);
            RefreshShortcutPage();
            var done = _actionControls.DebugSlot(index);
            notes.Add($"技能 {skillId}：起点 {start.FanProgress:F2} → 一半 {middle.FanProgress:F2} → 结束 " +
                      $"{(done.FanActive ? "还在" : "收起")}");
            if (done.FanActive) problems.Add("冷却结束后扇形还在");
        }

        // ② 合成两种状态，直接核对绘制口径：冷却中不压暗、不可用才压暗。
        // 图标沿用真实页面上的那一张——没有图标节点就量不到透明度。
        var iconPath = Array.Find(slots, slot => slot.Icon is not null)?.Icon;
        var probe = new HudShortcutSlot(new ShortcutEntry(ShortcutEntry.SkillKind, "cooldown-probe"), iconPath, 0, true)
        {
            Cooldown = new Cooldown(3000, 4000),
        };
        _actionControls.SetShortcutPage([probe, .. slots.Skip(1)]);
        var cooling = _actionControls.DebugSlot(0);
        if (!cooling.FanActive) problems.Add("合成冷却格没有画扇形");
        if (Math.Abs(cooling.FanProgress - 0.25f) > 0.06f)
            problems.Add($"合成冷却格进度应为 0.25，实际 {cooling.FanProgress:F2}");
        if (cooling.IconAlpha < 0.99f) problems.Add($"合成冷却格被压暗了：alpha={cooling.IconAlpha:F2}");
        if (!cooling.Disabled) problems.Add("合成冷却格还能点");

        var blocked = new HudShortcutSlot(new ShortcutEntry(ShortcutEntry.SkillKind, "blocked-probe"), iconPath, 0, false);
        _actionControls.SetShortcutPage([blocked, .. slots.Skip(1)]);
        var dimmed = _actionControls.DebugSlot(0);
        if (dimmed.FanActive) problems.Add("没有冷却的格子却画了扇形");
        if (Math.Abs(dimmed.IconAlpha - 0.4f) > 0.02f)
            problems.Add($"不可用又不在冷却的格子该压到 0.4，实际 {dimmed.IconAlpha:F2}");
        if (!dimmed.Disabled) problems.Add("不可用的格子还能点");

        var potion = new HudShortcutSlot(new ShortcutEntry(ShortcutEntry.ItemKind, "potion-probe"), iconPath, 3, true)
        {
            Cooldown = new Cooldown(1000, 1000),
        };
        _actionControls.SetShortcutPage([potion, .. slots.Skip(1)]);
        var item = _actionControls.DebugSlot(0);
        notes.Add($"物品格（1 秒冷却）：扇形={item.FanActive}、进度 {item.FanProgress:F2}");
        if (!item.FanActive) problems.Add("物品格没有画扇形");

        RefreshShortcutPage();

        foreach (var problem in problems) GD.Print($"[cooldowncheck] 问题：{problem}");
        foreach (var note in notes) GD.Print($"[cooldowncheck] {note}");
        GD.Print(problems.Count == 0 ? "[cooldowncheck] 通过" : "[cooldowncheck] 失败");
        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private async void BeginPlayerAttackCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var target = _enemies.FirstOrDefault(enemy => !enemy.Dead);
        if (target is null)
        {
            GD.Print($"[attackcheck] {_map.Id} 场上没有敌人，换一张有怪的地图（例如 training-dungeon-two）");
            GetTree().Quit(1);
            return;
        }

        var home = target.Position;
        target.Position = new Vector2(_position.X + 14f, _position.Y);

        EnsurePlayerCombat();
        var controller = _playerCombat;
        var rule = controller is null ? "无" : "有";
        var healthBefore = target.Health;

        GD.Print($"[attackcheck] 目标 {target.EnemyInfo.Id}，普攻规则 {rule}，" +
                 $"生命 {healthBefore}，动作开始前状态 {PlayerActionState}");

        // 单次普攻可能落空（命中掷骰不过），所以最多试四次，见到掉血就停。
        var started = CombatState.Idle;
        var dispatched = 0;
        var contacts = 0;
        var swings = 0;
        var frames = 0;
        var digitsBefore = _feedbackView?.DrawnDigits ?? 0;
        while (swings < 4 && healthBefore - target.Health == 0)
        {
            _playerDispatchedFrames = 0;
            _playerContacts = 0;
            PlayerAttack();
            if (swings == 0) started = PlayerActionState;
            if (PlayerActionState == CombatState.Idle) break;

            for (var guard = 0; guard < 60 && PlayerActionState != CombatState.Idle; guard += 1)
            {
                UpdateCombat(0.06f);
                frames += 1;
            }

            dispatched += _playerDispatchedFrames;
            contacts += _playerContacts;
            swings += 1;
        }

        var dealt = healthBefore - target.Health;
        var finished = PlayerActionState;
        var digits = (_feedbackView?.DrawnDigits ?? 0) - digitsBefore;
        target.Position = home;

        var ok = started != CombatState.Idle && dealt > 0 && dispatched > 0 && contacts > 0 &&
                 digits > 0 && finished == CombatState.Idle;
        GD.Print($"[attackcheck] 请求后状态 {started}，{swings} 次普攻／{frames} 帧后 {finished}，" +
                 $"派发 {dispatched} 帧、接触 {contacts} 次、画出数字 {digits} 位，" +
                 $"目标生命 {healthBefore} → {target.Health}（-{dealt}）");

        var attackSpeed = Conditions.Attribute(PlayerCombatId, "attackSpeedRate", 1000, PassivesOf(PlayerCombatId));
        var rangeBonus = AttributeOf(PlayerCombatId, "attackRangeBonusUnits");
        var maxHealth = ResourceCapacityOf(PlayerCombatId, "maxHealth");
        var procs = EquipmentAttributes.AttackProcs(EquippedInstances(PlayerCombatId));
        GD.Print($"[attackcheck] 属性链：攻速 {attackSpeed}（基础 1000），射程加成 {rangeBonus} 格，" +
                 $"生命上限 {maxHealth}，攻击附率 火 {procs.FireSplashChance}／风 {procs.WindStrikeChance}／" +
                 $"冰 {procs.FrostStrikeChance}／圣 {procs.DivineStrikeChance}／暗 {procs.DarknessStrikeChance}／" +
                 $"毒 {procs.PoisonStrikeChance}");
        ok &= attackSpeed >= 1000 && maxHealth > 0;

        if (_playerCombat is { } swingController)
        {
            target.Position = new Vector2(_position.X + 14f, _position.Y);
            PlayerAttack();
            for (var guard = 0; guard < 30 && !CombatRuntime.HasPendingBasicStrike(PlayerCombatId); guard += 1)
                UpdateCombat(0.06f);
            if (CombatRuntime.HasPendingBasicStrike(PlayerCombatId))
            {
                var before = _position;
                for (var step = 0; step < 12; step += 1) StepPlayerMotion(new Vec2(0.6f, 0.8f), 1f / 60f);
                var stayed = (before - _position).Length < 0.001f;
                var stillAttacking = PlayerActionState == CombatState.Attacking;
                ok &= stayed && stillAttacking;
                GD.Print($"[attackcheck] 攻击中推杆：位置不动={stayed}，动作保持 {PlayerActionState}（出手未落完时摇杆打断无效）");
            }
            for (var guard = 0; guard < 60 && PlayerActionState != CombatState.Idle; guard += 1) UpdateCombat(0.06f);
        }

        if (_playerCombat is { } attackController)
        {
            var selectedBefore = attackController.SelectedTargetId;
            var second = _enemies.FirstOrDefault(enemy => !enemy.Dead && enemy != target);
            if (selectedBefore is not null && second is not null)
            {
                for (var step = 0; step < 12; step += 1) StepPlayerMotion(new Vec2(0.6f, 0.8f), 1f / 60f);
                var cleared = attackController.SelectedTargetId is null;
                ok &= cleared;

                target.Position = new Vector2(_position.X + 600f, _position.Y);   // 把旧目标拉远
                PlacePlayer(new Vec2(second.Position.X + 14f, second.Position.Y));
                PlayerAttack();
                var reselected = attackController.SelectedTargetId;
                ok &= reselected == second.CombatId;
                GD.Print($"[attackcheck] 移动清目标：打 {selectedBefore} 后走开 → 目标清空={cleared}，" +
                         $"再点普攻选中 {reselected}（应为 {second.CombatId}）");
            }
            else
            {
                GD.Print("[attackcheck] 移动清目标：没有第二只活怪或没有选中目标，跳过");
            }
        }

        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunReactionCheck()
    {
        var ok = true;
        var catalog = _loader.Skills;
        var loadout = new SkillLoadout();
        foreach (var id in catalog.Class(_classId)?.SkillIds ?? []) loadout.Levels[id] = 1;

        var learned = SkillReactions.LearnedReactions(catalog, _classId, loadout);
        GD.Print($"[reactioncheck] 已学反应 {learned.Count} 条（职业 {_classId}）：" +
                 $"{string.Join(",", learned.Select(reaction => reaction.Kind).Distinct())}");
        ok &= learned.Count > 0;

        var withReactions = catalog.Skills.Values.Count(skill => skill.ReactionsByRank is not null);
        var effectCount = catalog.Skills.Values.Sum(skill => SkillReactions.Effects(skill).Count);
        GD.Print($"[reactioncheck] 带反应的技能 {withReactions} 门，反应特效名共 {effectCount} 个");
        ok &= withReactions == 22;

        var sample = catalog.Skills.Values.FirstOrDefault(skill =>
            skill.ReactionsByRank?.Any(rank => rank?.Any(reaction => reaction is SoulSwordReaction) == true) == true);
        if (sample?.ReactionsByRank is { } ranks)
        {
            var reactions = ranks.First(rank => rank?.Any(reaction => reaction is SoulSwordReaction) == true)!;
            var commands = SkillReactions.Resolve(reactions, new AttackSuccessEvent
            {
                OwnerId = PlayerCombatId,
                OpponentId = "dummy",
                DamageKind = "physical",
                Outcome = "hit",
                HitDamage = 100,
            }, new FixedReactionPorts());
            GD.Print($"[reactioncheck] {sample.Id} 物理命中：{commands.Count} 条命令（" +
                     $"{string.Join(",", commands.Select(command => command.Kind))}）");
            ok &= commands.Count > 0;
        }
        else
        {
            GD.PrintErr("[reactioncheck] 找不到带 soul-sword 的技能");
            ok = false;
        }

        var recorder = new RecordingReactionPort(new CombatConditions());
        CombatReactionPipeline.Resolve(
            new AttackContact(PlayerCombatId, "dummy", Hand: "main-hand"), new HitResult(HitKind.Hit, 40),
            "physical", "basic-attack", recorder);
        GD.Print($"[reactioncheck] 命中反应链 {recorder.Steps.Count} 步：" +
                 $"{string.Join("→", recorder.Steps)}");
        ok &= recorder.Steps.Count >= 5 && recorder.Steps[0] == "buff:physical-avoidance";

        // 战斗运行时：延迟两帧的活必须在第二帧才跑。
        long frame = 0;
        var runtime = new CombatRuntime(() => frame);
        var ran = 0;
        runtime.Schedule(PlayerCombatId, 2, () => ran += 1);
        frame += 1;
        runtime.Step(frame);
        var afterFirst = ran;
        frame += 1;
        runtime.Step(frame);
        GD.Print($"[reactioncheck] 运行时定时任务：第 1 帧跑 {afterFirst} 次，第 2 帧跑 {ran} 次");
        ok &= afterFirst == 0 && ran == 1;

        _combatRuntime = null;
        _conditions = null;
        var live = CombatRuntime;
        live.Schedule(PlayerCombatId, 1, () => { });
        var conditions = Conditions;
        SetInvincible(PlayerCombatId, true);
        var invincible = conditions.IsInvincible(PlayerCombatId);
        SetInvincible(PlayerCombatId, false);
        var vitals = VitalsOf(PlayerCombatId);
        GD.Print($"[reactioncheck] 常驻运行时与状态容器：无敌读写={invincible}，" +
                 $"主角上限 血 {vitals?.MaxHealth}／魔 {vitals?.MaxMana}");
        ok &= invincible && !conditions.IsInvincible(PlayerCombatId) && vitals is { MaxHealth: > 0 };

        GD.Print($"[reactioncheck] 反应系统可用={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunNoticeCheck()
    {
        var problems = new List<string>();
        var size = GetViewportRect().Size;
        var (x, y) = InstantMessages.Point(size.X, size.Y);

        ShowHudNotice("法力不足");
        var position = _hudNotice.Position;
        if (MathF.Abs(position.Y - y) > 1f) problems.Add($"纵向 {position.Y:F0}，应为 {y:F0}（三成屏高）");
        if (MathF.Abs(position.X + 280f - x) > 1f) problems.Add($"横向中心 {position.X + 280f:F0}，应为 {x:F0}");

        _choice.Visible = true;
        TickHudNotice(60f);
        var hiddenByChoice = !_hudNotice.Visible;
        _choice.Visible = false;
        if (!hiddenByChoice) problems.Add("选项面板在前面时仍然在画");

        ShowHudNotice("法力不足");

        var alphas = new List<float>();
        for (var step = 0; step < 32 && _hudNotice.Visible; step += 1)
        {
            TickHudNotice(60f);
            alphas.Add(_hudNotice.Visible ? _hudNotice.Modulate.A : 0f);
        }

        var expected = new[] { 1f, 0.75f, 0.5f, 0.25f, 0f };
        var tail = alphas.Skip(Math.Max(0, alphas.Count - expected.Length)).ToArray();
        if (tail.Length != expected.Length || !tail.SequenceEqual(expected))
            problems.Add($"末段透明度 {string.Join("／", tail.Select(value => value.ToString("F2")))}，" +
                         $"应为 {string.Join("／", expected.Select(value => value.ToString("F2")))}");
        if (_hudNotice.Visible) problems.Add("时间到了还在画");

        GD.Print($"[noticecheck] 落点 ({position.X + 280f:F0},{position.Y:F0}) 应 ({x:F0},{y:F0})；" +
                 $"选项在前面时收起={hiddenByChoice}；末段透明度 " +
                 $"{string.Join("／", tail.Select(value => value.ToString("F2")))}；共 {alphas.Count} 帧 → " +
                 $"{(problems.Count == 0 ? "通过" : "失败")}");
        foreach (var problem in problems) GD.Print($"[noticecheck] {problem}");

        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private void RunPickupCheck()
    {
        var problems = new List<string>();
        var head = _player.Definition.HeadAnchor;
        var expectedY = _player.Position.Y + (head?.Y ?? 0f) + PickupMessageRules.BaseOffsetY;

        _pickupMessages.Enqueue(new Drops.DropReward[] { new Drops.DropReward.MoneyDrop(15_000) }, PlayerCombatId);
        _pickupMessages.Enqueue(new Drops.DropReward[] { new Drops.DropReward.ItemDrop(PlainItemId()) }, PlayerCombatId);

        var now = 1_000L;
        _pickupMessages.Tick(now, 60);
        if (_pickupMessages.ActiveCount != 1) problems.Add($"首条没出现（在演 {_pickupMessages.ActiveCount}）");
        var roots = _pickupMessages.GetChildren().OfType<Node2D>().ToList();
        if (roots.Count > 0 && MathF.Abs(roots[^1].Position.Y - expectedY) > 1f)
            problems.Add($"首条纵距 {roots[^1].Position.Y:F1}，应 {expectedY:F1}（头顶锚点 −37）");

        now += 60;
        _pickupMessages.Tick(now, 60);
        now += 60;
        _pickupMessages.Tick(now, 60);
        if (_pickupMessages.ActiveCount != 2) problems.Add($"120ms 后第二条没出现（在演 {_pickupMessages.ActiveCount}）");
        roots = _pickupMessages.GetChildren().OfType<Node2D>().ToList();
        if (roots.Count > 1 && MathF.Abs(roots[^1].Position.Y - (expectedY + PickupMessageRules.RowSpacing)) > 1f)
            problems.Add($"第二行纵距 {roots[^1].Position.Y:F1}，应 {expectedY + PickupMessageRules.RowSpacing:F1}（行距 12）");

        for (var step = 0; step < 24; step += 1)
        {
            now += 60;
            _pickupMessages.Tick(now, 60);
        }

        roots = _pickupMessages.GetChildren().OfType<Node2D>().ToList();
        if (roots.Count > 1 && roots[^1].Position.Y >= expectedY + PickupMessageRules.RowSpacing)
            problems.Add("停留期结束后没有上飘");

        var more = new List<Drops.DropReward>();
        for (var i = 0; i < 40; i += 1) more.Add(new Drops.DropReward.MoneyDrop(100));
        _pickupMessages.Enqueue(more, PlayerCombatId);
        if (_pickupMessages.PendingCount + _pickupMessages.ActiveCount > PickupMessageRules.Limit)
            problems.Add($"总量 {_pickupMessages.PendingCount + _pickupMessages.ActiveCount}，超上限 32");
        if (_pickupMessages.PendingCount != PickupMessageRules.Limit - 2)
            problems.Add($"入队 {_pickupMessages.PendingCount}，应为 {PickupMessageRules.Limit - 2}（2 条在演）");

        for (var step = 0; step < 170; step += 1)
        {
            now += 60;
            _pickupMessages.Tick(now, 60);
        }

        if (_pickupMessages.ActiveCount != 0) problems.Add($"到寿没收摊（剩 {_pickupMessages.ActiveCount}）");

        GD.Print($"[pickupcheck] 首条/次条落点与行距、上飘、上限 {PickupMessageRules.Limit}、2.4s 收摊 → " +
                 $"{(problems.Count == 0 ? "通过" : "失败")}");
        foreach (var problem in problems) GD.Print($"[pickupcheck] {problem}");

        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private async void RunFlightCheck()
    {
        var problems = new List<string>();
        var pickup = _loader.Items.Items.GetValueOrDefault("mana-gem")?.Pickup.AsManaPickup();
        if (pickup is null || _map is null)
        {
            GD.Print("[flightcheck] 内容里没有 mana-gem");
            GetTree().Quit(1);
            return;
        }

        _session.GroundLoot.AddFlying(_map.Id, (_position.X, _position.Y),
            (_position.X + 60f, _position.Y, 0), "mana-gem", 600);
        SyncGroundLoot();

        EffectSprite? effect = null;
        foreach (var child in _groundLootView.GetChildren())
            if (child is EffectSprite found) effect = found;
        if (effect is null) problems.Add("飞行段没有特效节点");
        else if (effect.SpeedScale != 1f) problems.Add($"特效速度 {effect.SpeedScale}，应为 1");

        _groundLootView.SetPaused(true);
        if (effect is not null && effect.SpeedScale != 0f) problems.Add("冻结后特效还在播");
        _groundLootView.SetPaused(false);

        for (var frame = 0; frame < 120 && _session.GroundLoot.ForMap(_map.Id).Count > 0 &&
                 _session.GroundLoot.ForMap(_map.Id)[0].Flight is not null; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var after = _session.GroundLoot.ForMap(_map.Id);
        SyncGroundLoot();

        var hasGround = false;
        foreach (var child in _groundLootView.GetChildren())
            if (child is Sprite2D) hasGround = true;
        if (!hasGround) problems.Add($"落地后没有地面小图（剩余掉落 {after.Count}，" +
                                     $"飞行 {(after.Count > 0 ? after[0].Flight is null ? "已结束" : "仍在飞" : "—")}，" +
                                     $"可操控={CanControl()}）");

        GD.Print($"[flightcheck] 飞行特效={(effect is not null ? "有" : "无")}（起始帧对齐、冻结生效），" +
                 $"落地换小图={hasGround} → {(problems.Count == 0 ? "通过" : "失败")}");
        foreach (var problem in problems) GD.Print($"[flightcheck] {problem}");

        GetTree().Quit(problems.Count == 0 ? 0 : 1);
    }

    private string PlainItemId() =>
        _loader.Items.Items.First(pair => pair.Value.Pickup is null && pair.Value.Name is not null).Key;

    private void RunTurnCheck()
    {
        if (_player is not { } player)
        {
            GD.Print("[turncheck] 主角还没建出来");
            GetTree().Quit(1);
            return;
        }

        var ok = true;
        var visual = player.Visual;

        if (!visual.HasAnimation("walk-south") || !visual.HasAnimation("walk-east"))
        {
            GD.Print("[turncheck] 主角缺少行走片段（walk-south／walk-east）");
            GetTree().Quit(1);
            return;
        }

        const int startFrame = 3;
        const float startProgress = 0.6f;
        var eastFrames = visual.GetFrameCount("walk-east");

        visual.Play("walk-south");
        foreach (var layer in visual.Layers) layer.SetFrameAndProgress(startFrame, startProgress);
        visual.Play("walk-east");

        var carriedFrame = visual.Frame;
        var layerFrame = visual.Layers.Count > 0 ? visual.Layers[0].Frame : -1;
        var carriedProgress = visual.Layers.Count > 0 ? visual.Layers[0].FrameProgress : -1f;
        var expectedFrame = startFrame % Math.Max(1, eastFrames);

        var frameOk = carriedFrame == expectedFrame && layerFrame == expectedFrame;
        var progressOk = MathF.Abs(carriedProgress - startProgress) < 0.05f;
        ok &= frameOk && progressOk;

        GD.Print($"[turncheck] 走南第 {startFrame} 帧（帧内 {startProgress:F2}）→ 走东：" +
                 $"帧号 {carriedFrame}（层 {layerFrame}，应为 {expectedFrame}），" +
                 $"帧内 {carriedProgress:F2} → {(frameOk && progressOk ? "接上了" : "丢了进度")}");

        visual.Play("walk-south");
        foreach (var layer in visual.Layers) layer.SetFrameAndProgress(startFrame, startProgress);
        visual.Play("idle-south");
        var idleFrame = visual.Frame;
        var idleOk = idleFrame == 0;
        ok &= idleOk;
        GD.Print($"[turncheck] 走南第 {startFrame} 帧 → 待机：帧号 {idleFrame}（应为 0）");

        visual.Play("idle-south");
        GD.Print($"[turncheck] 通过={(ok ? "是" : "否")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunLayerCheck()
    {
        var ok = true;
        var multi = _loader.Battle.Enemies
            .Select(enemy => (enemy, clip: enemy.Actor.Clips.FirstOrDefault(pair => pair.Value.Layers.Length > 1)))
            .Where(pair => pair.clip.Value is not null)
            .ToArray();

        GD.Print($"[layercheck] 层数大于一的怪：{multi.Length} 只");
        ok &= multi.Length > 0;

        foreach (var (enemy, clip) in multi.Take(4))
        {
            var node = new ActorNode { Name = $"layer-{enemy.Id}" };
            AddChild(node);
            node.Setup(enemy.Actor, _textures);

            var visual = node.Visual;
            visual.Play(clip.Key);

            var expected = clip.Value.Layers;
            ok &= visual.LayerCount == expected.Length;
            ok &= visual.HasArtwork;

            visual.Frame = 2;
            ok &= visual.Layers.All(layer => layer.Frame == 2);
            ok &= visual.Layers.All(layer => layer.Offset == visual.Offset);
            ok &= visual.Layers.Select(MaterialTier).SequenceEqual(expected.Select(layer => BlendTier(layer.Blend)));

            // 每层的帧数要跟片段一致，且各层用的是不同的贴图（不然叠出来的还是第一层）。
            ok &= visual.Layers.All(layer => layer.SpriteFrames.GetFrameCount("default") == clip.Value.Frames);
            ok &= visual.LayerFiles.Distinct().Count() == visual.LayerCount;
            ok &= visual.LayerFiles.SequenceEqual(expected.Select(layer => layer.File));

            GD.Print($"[layercheck] {enemy.Id}／{clip.Key}：层数 {visual.LayerCount}/{expected.Length}，" +
                     $"混合 {string.Join(",", expected.Select(layer => layer.Blend))}，" +
                     $"帧号同步 {visual.Layers.All(layer => layer.Frame == 2)}，" +
                     $"锚点 {visual.Offset.X:F0},{visual.Offset.Y:F0}");

            node.QueueFree();
        }

        // 换装合成出来的片段只有 key、没有文件：换动作时同样要整批重建图层，
        // 否则人物会一直停在最先建出来的那套帧上（待机 14 帧、行走 8 帧，一眼可辨）。
        if (_player is { } player &&
            player.Definition.Clips.GetValueOrDefault("idle-south") is { Layers.Length: > 0 } idleClip &&
            player.Definition.Clips.GetValueOrDefault("walk-south") is { Layers.Length: > 0 } walkClip)
        {
            player.Visual.Play("idle-south");
            var idleFrames = player.Visual.Layers.Count > 0
                ? player.Visual.Layers[0].SpriteFrames.GetFrameCount("default")
                : 0;
            player.Visual.Play("walk-south");
            var walkFrames = player.Visual.Layers.Count > 0
                ? player.Visual.Layers[0].SpriteFrames.GetFrameCount("default")
                : 0;
            var composed = idleClip.Layers[0].File.Length == 0 && walkClip.Layers[0].File.Length == 0;

            ok &= idleFrames == idleClip.Frames && walkFrames == walkClip.Frames;
            GD.Print($"[layercheck] 主角换动作：待机 {idleFrames}/{idleClip.Frames} 帧 → " +
                     $"行走 {walkFrames}/{walkClip.Frames} 帧，合成片段={composed}");
            player.Visual.Play("idle-south");
        }

        var swingClip = BattleContent.EffectClip("sword-swing", "south");
        var swing = swingClip is null ? null : EffectSprite.Create(swingClip, _mapLease!, 0f, 0f);
        if (swing is null)
        {
            ok = false;
            GD.Print("[layercheck] 找不到 sword-swing／south，特效混合没法验");
        }
        else
        {
            var effectLayers = swing.GetChildren().OfType<AnimatedSprite2D>().ToArray();
            var anyScreenShader = effectLayers.Any(layer => ScreenBlend.IsScreen(layer.Material));
            var anyAdd = effectLayers.Any(layer =>
                layer.Material is CanvasItemMaterial { BlendMode: CanvasItemMaterial.BlendModeEnum.Add });
            ok &= effectLayers.Length > 0 && !anyScreenShader && anyAdd;
            GD.Print($"[layercheck] 特效 screen 层：{effectLayers.Length} 层，" +
                     $"读屏着色器 {(anyScreenShader ? "有（错）" : "无")}，加法近似 {(anyAdd ? "有" : "无（错）")}");
            swing.QueueFree();
        }

        GD.Print($"[layercheck] 多层人物片段可用={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunColorCheck()
    {
        var ok = true;
        var multi = _loader.Battle.Enemies
            .Select(enemy => (enemy, clip: enemy.Actor.Clips.FirstOrDefault(pair => pair.Value.Layers.Length > 1)))
            .Where(pair => pair.clip.Value is not null)
            .ToArray();
        if (multi.Length == 0)
        {
            GD.Print("[colorcheck] 找不到多层片段的怪");
            GetTree().Quit(1);
            return;
        }

        var (enemy, clip) = multi.First();
        var node = new ActorNode { Name = $"color-{enemy.Id}" };
        AddChild(node);
        node.Setup(enemy.Actor, _textures);

        var visual = node.Visual;
        visual.Play(clip.Key);

        var colors = new[] { new BuffColor { Red = 100, Green = 0, Blue = 0 } };
        visual.PaintStatus(colors, 0);

        for (var index = 0; index < visual.LayerCount; index++)
        {
            var layer = visual.Layers[index];
            var blend = clip.Value.Layers[index].Blend;
            var material = layer.Material as ShaderMaterial;
            if (material is null)
            {
                ok = false;
                continue;
            }

            var row = (Vector3)material.GetShaderParameter("row_r")!;
            var offset = (Vector3)material.GetShaderParameter("offset")!;
            var additive = blend is "add" or "screen";
            var expectedRow = additive ? 1f + 100f / 255f : 1f - 100f / 255f;
            var expectedOffset = additive ? 0f : 100f;
            ok &= Mathf.IsEqualApprox(row.X, expectedRow) && Mathf.IsEqualApprox(offset.X, expectedOffset);

            GD.Print($"[colorcheck] {enemy.Id}／{clip.Key} 第 {index} 层（{blend}）：" +
                     $"行 {row.X:F3}，偏移 {offset.X:F1}");
        }

        visual.PaintStatus(null, 0);
        ok &= visual.Layers.All(layer => layer.Material is not ShaderMaterial ||
                                         ScreenBlend.IsScreen(layer.Material));

        visual.PaintStatus(colors, 0);
        visual.FlashStatus();
        if (visual.Layers[0].Material is ShaderMaterial flashed)
        {
            var row = (Vector3)flashed.GetShaderParameter("row_r")!;
            var offset = (Vector3)flashed.GetShaderParameter("offset")!;
            ok &= Mathf.IsEqualApprox(row.X, (1f - 100f / 255f) * 0.5f);
            ok &= Mathf.IsEqualApprox(offset.X, 100f + 128f * (1f - 100f / 255f));
            GD.Print($"[colorcheck] 闪白：行 {row.X:F3}，偏移 {offset.X:F1}");
        }
        else ok = false;

        node.QueueFree();
        GD.Print($"[colorcheck] 增益着色可用={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunWardrobeCheck()
    {
        var ok = true;
        if (_outfits is null || _heroBase is null || _bag is null)
        {
            GD.Print("[wardrobecheck] 换装合成器还没准备好");
            GetTree().Quit(1);
            return;
        }

        var equipment = _bag.EquipmentItems(_bag.EquipmentFor(PlayerCombatId));
        var outfit = _outfits.Prepare(_heroBase, equipment, _classId, _heroBase.Id);
        var composed = outfit.Actor.Clips.Count(clip =>
            clip.Value.Layers.Length == 1 && clip.Value.Layers[0].File.Length == 0);
        GD.Print($"[wardrobecheck] 装备 {string.Join(",", equipment.Select(pair => $"{pair.Key}={pair.Value}"))}，" +
                 $"合成片段 {composed} 段，合成贴图 {outfit.TextureKeys.Count} 张");
        ok &= composed > 0;

        if (outfit.Actor.Clips.GetValueOrDefault("idle-south") is { } idle)
        {
            if (Inotia4.Rendering.ComposedTextures.Find(idle.Layers[0].Key) is { } texture)
            {
                var directory = ProjectSettings.GlobalizePath("res://.shots");
                DirAccess.MakeDirRecursiveAbsolute(directory);
                var path = System.IO.Path.Combine(directory, "wardrobe-idle-south.png");
                var image = texture.GetImage();
                image.SavePng(path);

                var pixels = image.GetData();
                var greenEdge = 0;
                var softEdge = 0;
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    var alpha = pixels[index + 3];
                    if (alpha is 0 or 255) continue;
                    softEdge += 1;
                    if (pixels[index + 1] >= 110 && pixels[index] <= 70 && pixels[index + 2] <= 70) greenEdge += 1;
                }

                // 像素画不能有一层平滑：原版开着 pixelArt（纹理过滤 NEAREST），合成也按最近邻取样。
                // 之前按双线性取样时，整数坐标正好落在两格中间，整张人物被糊成 50/50 混色
                // （待机片带约 3400 个半透明像素，原始素材是 0 个）。这一条守住那个回归。
                var softBudget = pixels.Length / 4 / 200;   // 0.5% 以内算素材自带的软边
                ok &= softEdge <= softBudget;
                ok &= greenEdge == 0;
                GD.Print($"[wardrobecheck] 待机片带：{idle.FrameWidth}×{idle.FrameHeight}，{idle.Frames} 帧，" +
                         $"锚点 {idle.OriginX:F3},{idle.OriginY:F3}，帧率 {idle.FrameRate:F2}，" +
                         $"软边 {softEdge} 像素（上限 {softBudget}），边缘渗绿 {greenEdge} 像素 → {path}");
            }
            else
            {
                GD.Print("[wardrobecheck] 合成贴图不在登记表里");
                ok = false;
            }
        }
        else
        {
            GD.Print("[wardrobecheck] 合成外观里没有 idle-south");
            ok = false;
        }

        if (_player?.Definition.Clips.GetValueOrDefault("idle-south") is { } playerIdle)
        {
            var composedClip = playerIdle.Layers.Length == 1 && playerIdle.Layers[0].File.Length == 0;
            var drawn = _player.Visual.HasArtwork;
            ok &= composedClip && drawn;
            GD.Print($"[wardrobecheck] 主角外观：合成片段={composedClip}，画面已画出={drawn}，" +
                     $"当前动作 {_player.Visual.Animation}");
        }
        else
        {
            GD.Print("[wardrobecheck] 主角身上没有 idle-south");
            ok = false;
        }

        GD.Print($"[wardrobecheck] 换装外观可用={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunStageOutfitCheck()
    {
        var ok = true;
        if (_party is null || _stage is null)
        {
            GD.Print("[stageoutfit] 队伍还没准备好");
            GetTree().Quit(1);
            return;
        }

        var member = _party.JoinStory("mercenary-olaf");
        var memberId = member.Id;

        _stage.ShowStageMap("shadow-camp", _position, "south");
        using var cast = JsonDocument.Parse($$"""
        {"cast":[{"id":"story-olaf-d48a31ff","actor":"story-olaf-d48a31ff","memberId":"{{memberId}}"}]}
        """);
        _stage.SetCast(cast.RootElement);

        var actor = _stage.AppearActor("story-olaf-d48a31ff", new Vec2(_position.X + 64f, _position.Y), "south");
        if (actor is null)
        {
            GD.Print("[stageoutfit] 舞台演员没建出来");
            GetTree().Quit(1);
            return;
        }

        var definition = actor.Definition;
        var composed = definition.Clips.Values.Count(clip =>
            clip.Layers.Length == 1 && clip.Layers[0].File.Length == 0);
        var registered = definition.Clips.Values
            .Where(clip => clip.Layers.Length == 1 && clip.Layers[0].File.Length == 0)
            .All(clip => Inotia4.Rendering.ComposedTextures.Find(clip.Layers[0].Key) is not null);

        ok &= composed > 0;
        ok &= registered;

        GD.Print($"[stageoutfit] 队员 {memberId}（职业 {member.ClassId}）：" +
                 $"舞台片段 {definition.Clips.Count} 段，其中合成 {composed} 段，贴图已登记={registered}");

        var baseActor = _loader.World.Actors.GetValueOrDefault(member.Definition.Actor);
        ok &= baseActor is not null && baseActor.Clips["idle-south"].Layers[0].File.Length > 0;

        GD.Print($"[stageoutfit] 剧情舞台队员换装可用={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunWorldObjectCheck()
    {
        var ok = true;
        if (_stage is null)
        {
            GD.Print("[objectcheck] 舞台还没准备好");
            GetTree().Quit(1);
            return;
        }

        _stage.ShowStageMap("shadow-camp", _position, "south");

        var actor = "transparent-boulder-ba4cb42f";
        var places = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var payload = "{\"kind\":\"place\",\"scope\":\"visit\",\"entity\":{" +
                          $"\"id\":\"story-object-test-{index}\",\"actor\":\"{actor}\"," +
                          $"\"map\":\"shadow-camp\",\"x\":{16 + index * 24},\"y\":64,\"visible\":true}}}}";
            using var command = JsonDocument.Parse(payload);
            _stage.ApplyWorldObject(command.RootElement, "story-boulder-test");
            places.Add($"story-object-test-{index}");
        }

        var instances = places
            .Select(id => _stage.ActorFor(StoryStage.EntityKey(id)))
            .Where(actorNode => actorNode is not null)
            .ToArray();
        ok &= instances.Length == 3;
        ok &= instances.Distinct().Count() == 3;

        var positions = instances.Select(node => node!.Position).ToArray();
        ok &= positions.Distinct().Count() == 3;

        var bound = _stage.ActorForAlias("story-boulder-test");
        ok &= bound is not null && ReferenceEquals(bound, instances[2]);

        var inWorld = _session.StoryWorld.Entity("story-object-test-0") is not null;
        ok &= !inWorld;

        GD.Print($"[objectcheck] 三个物件实例 {instances.Length} 个（互不相同 {instances.Distinct().Count()}），" +
                 $"位置 {string.Join(" / ", positions.Select(p => $"({p.X:F0},{p.Y:F0})"))}，" +
                 $"别名指向第 {(bound is null ? "无" : (instances.ToList().IndexOf(bound!) + 1).ToString())} 个实例，" +
                 $"写进正式世界={inWorld}");

        GD.Print($"[objectcheck] world-object 可用={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private static int BlendTier(string blend) => blend switch
    {
        "add" => 1,
        "multiply" => 2,
        "screen" => 3,
        _ => 0,
    };

    private static int MaterialTier(AnimatedSprite2D layer) => layer.Material switch
    {
        CanvasItemMaterial material when material.BlendMode == CanvasItemMaterial.BlendModeEnum.Add => 1,
        CanvasItemMaterial material when material.BlendMode == CanvasItemMaterial.BlendModeEnum.Mul => 2,
        _ when ScreenBlend.IsScreen(layer.Material) => 3,
        _ => 0,
    };

    private sealed class FixedReactionPorts : ISkillReactionPorts
    {
        public double Random() => 0.0;

        public SkillReactionBuff? LearnedBuff(string ownerId, string skill) =>
            new(skill, 1, new BattleBuffEffect { DurationMs = 6000, TickMs = 0 });
    }

    private sealed class RecordingReactionPort(CombatConditions conditions) : IHitReactionPort
    {
        public List<string> Steps { get; } = [];

        public CombatConditions Conditions => conditions;

        public int ActorAttribute(string id, string key) => 0;

        public void React(SkillReactionEvent action) => Steps.Add(action.Kind);

        public void ReactToBuff(BuffReactionEvent action) => Steps.Add($"buff:{action.Kind}");

        public void LightningOfHellReaction(string casterId, string targetId) => Steps.Add("lightning");

        public void GodOfJusticeReaction(string casterId, string targetId) => Steps.Add("justice");

        public void SummonOwnerReactions(AttackContact contact, HitResult hit, string damageKind) =>
            Steps.Add("summon-owner");

        public void DisturbanceReaction(string casterId, string targetId) => Steps.Add("disturbance");

        public bool PartySide(string id) => false;

        public IReadOnlyList<string> ActivePartySlots() => [PlayerCombatId];

        public void PartyRevengeReactions(string casterId, string damageKind, string outcome)
        {
        }

        public int EquipmentLifeDrainPerThousand(string id) => 0;

        public bool IsTwoHanded(string id) => false;

        public void MoltenReflection(AttackContact contact, int rate)
        {
        }

        public void HolySacrificeTransfer(AttackContact contact, HitResult hit, string damageKind)
        {
        }

        public void AttackProc(AttackContact contact, HitResult hit, Vec2? position = null) => Steps.Add("proc");
    }

    private static Control? TopmostControlAt(Node root, Vector2 point)
    {
        for (var index = root.GetChildCount() - 1; index >= 0; index -= 1)
        {
            if (root.GetChild(index) is not Control control || !control.Visible) continue;

            if (TopmostControlAt(control, point) is { } inner) return inner;
            if (control.MouseFilter == Control.MouseFilterEnum.Ignore) continue;
            if (control.GetGlobalRect().HasPoint(point)) return control;
        }

        return null;
    }

    private async void BeginSettingsCheck()
    {
        var ok = true;

        ShowMenuPage(SystemPanel.PageSettings);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[settingscheck] 设置页 open={_systemPanel.IsOpen}");

        // 设置页的按钮要真的能点到：弹层那一整块若带 STOP，会把 1024×640 全盖住，
        // 页签和每一行按钮都收不到鼠标（无头模式没有真鼠标，所以按同样的规则走一遍命中链）。
        var clickPoint = _systemPanel.ContentPointToViewport(new Vector2(485f + 110f, 260f + 33f));
        var topmost = TopmostControlAt(_systemPanel, clickPoint);
        var reachable = topmost is Button;
        ok &= reachable;
        GD.Print($"[settingscheck] 设置页按钮命中={reachable}（{clickPoint.X:F0},{clickPoint.Y:F0} 命中 " +
                 $"{topmost?.Name ?? "无"}）");

        _displaySettings.SetFps(false);
        var display = new DisplaySettings(ReadUserText, WriteUserText);
        ok &= !display.Fps && display.CriticalDecorations;
        GD.Print($"[settingscheck] 画面设置重读 fps={display.Fps} 暴击光效={display.CriticalDecorations}");
        _displaySettings.SetFps(true);

        _displaySettings.SetHighRefresh(false);
        Inotia4.Application.Main.ApplyHighRefresh(false);
        var reloaded = new DisplaySettings(ReadUserText, WriteUserText);
        var capped = Engine.MaxFps == 60;
        ok &= !reloaded.HighRefresh && capped;
        GD.Print($"[settingscheck] 高帧率关闭：重读={reloaded.HighRefresh}（应为 False），上限={Engine.MaxFps}（应为 60）");
        _displaySettings.SetHighRefresh(true);
        Inotia4.Application.Main.ApplyHighRefresh(true);
        GD.Print($"[settingscheck] 高帧率恢复：上限={Engine.MaxFps}");

        _displaySettings.SetHaptics(false);
        var hapticReload = new DisplaySettings(ReadUserText, WriteUserText);
        ok &= !hapticReload.Haptics;
        GD.Print($"[settingscheck] 震动关闭：重读={hapticReload.Haptics}（应为 False）");
        _displaySettings.SetHaptics(true);

        _controlSettings.SetMoveStyle(MoveControlStyle.Dpad);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var controls = new ControlSettings(ReadUserText, WriteUserText);
        ok &= controls.MoveStyle == MoveControlStyle.Dpad && _stick.DpadActive;
        GD.Print($"[settingscheck] 移动方式重读 {controls.MoveStyle}，摇杆十字键={_stick.DpadActive}");

        _controlSettings.SetMoveStyle(MoveControlStyle.FourWay);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var fourway = new ControlSettings(ReadUserText, WriteUserText);
        var fourwayOk = fourway.MoveStyle == MoveControlStyle.FourWay && !_stick.DpadActive;
        ok &= fourwayOk;
        GD.Print($"[settingscheck] 四向移动重读 {fourway.MoveStyle}，摇杆十字键={_stick.DpadActive}（应为 False）");

        _stick.SetStickVisible(true);
        var stickCenter = _stick.Position;
        _stick._Input(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = stickCenter,
        });
        _stick._Input(new InputEventMouseMotion { Position = stickCenter + new Vector2(46f, 12f) });
        var diagonal = _stick.Axis;
        _stick._Input(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = stickCenter + new Vector2(46f, 12f),
        });
        var snapped = Math.Abs(diagonal.X) > 0.99f && Math.Abs(diagonal.Y) < 0.01f;
        ok &= snapped;
        GD.Print($"[settingscheck] 四向斜推 → 轴 {diagonal}（应只有一条轴）");

        _controlSettings.SetMoveStyle(MoveControlStyle.Stick);

        _stick.SetStickVisible(true);
        _stick._Input(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = true });
        var up = _stick.Axis;
        _stick._Input(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = false });
        ok &= up.Y < -0.99f && _stick.Axis == Vector2.Zero;
        GD.Print($"[settingscheck] 键盘 W → 轴 {up}，松手 {_stick.Axis}");

        var original = _controlLayout!.Snapshot.TryGetValue("attack", out var before)
            ? before
            : ((float, float)?)null;
        _controlLayout.Set("attack", 0.82f, 0.74f);
        var layout = new ControlLayoutPreferences(ReadUserText, WriteUserText);
        var restored = layout.Snapshot.TryGetValue("attack", out var point) &&
                       Math.Abs(point.X - 0.82f) < 0.001f && Math.Abs(point.Y - 0.74f) < 0.001f;
        ok &= restored;
        GD.Print($"[settingscheck] 键位重读普攻={restored}");

        OpenControlEditor();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var positions = _layoutEditor!.Positions;
        ok &= _layoutEditor.IsOpen && positions.Count == ControlIds.All.Length;
        GD.Print($"[settingscheck] 编辑器 open={_layoutEditor.IsOpen} 控件={positions.Count} " +
                 $"普攻落点={positions.GetValueOrDefault("attack")}");

        var attackAt = positions.GetValueOrDefault("attack");
        var dragged = _layoutEditor.DebugDrag("attack", attackAt, attackAt + new Vector2(-40f, -40f));
        var draggedPoint = _controlLayout.Snapshot.TryGetValue("attack", out var moved) ? moved : (0f, 0f);
        ok &= dragged && Math.Abs(draggedPoint.Item1 - 0.82f) > 0.01f;
        GD.Print($"[settingscheck] 拖动普攻={dragged} 新键位={draggedPoint.Item1:F3},{draggedPoint.Item2:F3}");

        _layoutEditor.Close();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[settingscheck] 关掉编辑器后回到设置页 open={_systemPanel.IsOpen}");

        ShowMenuPage(SystemPanel.PageSave);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var slots = _systemPanel.FindChild("Slots", true, false) as Control;
        var expected = _systemPanel.CanvasPointToViewport(
            new Vector2(SaveBoard.Origin(1024f).X, SaveBoard.ListTop));
        var placed = slots is not null &&
                     slots.GetGlobalTransformWithCanvas().Origin.DistanceTo(expected) < 0.5f;
        ok &= placed;
        GD.Print($"[settingscheck] 存档板位置={placed}（滚动区 " +
                 $"{slots?.GetGlobalTransformWithCanvas().Origin}，应为 {expected}）");

        ShowMenuPage(SystemPanel.PageSettings);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var tabPoint = _systemPanel.CanvasPointToViewport(new Vector2(
            Inotia4.Domain.Ui.MenuTabs.FirstX + 4 * Inotia4.Domain.Ui.MenuTabs.Stride +
            Inotia4.Domain.Ui.MenuTabs.Width / 2f,
            Inotia4.Domain.Ui.MenuTabs.Y + Inotia4.Domain.Ui.MenuTabs.Height / 2f));
        (TopmostControlAt(_systemPanel, tabPoint) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var switched = !_systemPanel.IsOpen && _questPanel?.IsOpen == true;
        ok &= switched;
        GD.Print($"[settingscheck] 点「任务」切页={switched}（系统面板开 {_systemPanel.IsOpen}，" +
                 $"任务页开 {_questPanel?.IsOpen}）");

        if (original is { } previous) _controlLayout.Set("attack", previous.Item1, previous.Item2);
        else _controlLayout.Reset();

        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginTargetCheck()
    {
        var enemy = _enemies.FirstOrDefault(node => !node.Dead && node.Fighter.MaxHealth > 1);
        if (enemy is null)
        {
            GD.Print($"[targetcheck] {_map.Id} 场上没有敌人，换一张有怪的地图（例如 training-dungeon-two）");
            GetTree().Quit(1);
            return;
        }

        PlacePlayer(new Vec2(enemy.Position.X + 14f, enemy.Position.Y));
        PlayerAttack();
        for (var guard = 0; guard < 40 && !_statusHud!.TargetVisible; guard += 1)
        {
            UpdateCombat(0.06f);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var target = FindEnemy(CombatRuntime.SelectedTargetId(ControlledId) ?? string.Empty) ?? enemy;
        var expected = $"{target.EnemyInfo.Name} (Lv.{target.Fighter.Level})";
        var text = _statusHud!.TargetText;
        var fill = _statusHud.TargetFillWidth;
        var slot = _loader.StatusHud.Target.Gauge.FillWidth;
        float ExpectedFill() => slot * Math.Clamp((float)target.Health / Math.Max(1, target.Fighter.MaxHealth), 0f, 1f);

        var ok = _statusHud.TargetVisible && text == expected && fill > 0f;
        ok &= Mathf.Abs(fill - ExpectedFill()) < 1.5f;
        GD.Print($"[targetcheck] 目标窗显示={_statusHud.TargetVisible}，名字='{text}'（应为 '{expected}'），" +
                 $"血槽填充 {fill:F0}/{slot:F0}（血量 {target.Health}/{target.Fighter.MaxHealth}）");

        if (ok)
        {
            target.ApplyDamage(Math.Max(1, target.Health / 2));
            for (var frame = 0; frame < 3; frame += 1)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var after = _statusHud.TargetFillWidth;
            var shrank = after < fill - 1f && Mathf.Abs(after - ExpectedFill()) < 1.5f;
            ok &= shrank;
            GD.Print($"[targetcheck] 掉血后填充 {after:F0}（血量 {target.Health}/{target.Fighter.MaxHealth}，应约 {ExpectedFill():F0}）");
        }

        if (DisplayServer.GetName() != "headless")
        {
            var directory = ProjectSettings.GlobalizePath("res://.shots");
            DirAccess.MakeDirRecursiveAbsolute(directory);
            var path = System.IO.Path.Combine(directory, "target.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[targetcheck] 截图 → {path}");
        }

        GD.Print($"[targetcheck] 通过：{ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginEnemyCheck()
    {
        if (_map.EnemySpawns.Length == 0 || _mapRoster is null)
        {
            GD.Print($"[enemycheck] {_map.Id} 没有刷怪点，换一张有怪的地图（例如 training-dungeon-two）");
            GetTree().Quit(1);
            return;
        }

        var ok = _enemies.Count > 0 && _enemies.Count == _mapRoster.Spawns.Count;
        GD.Print($"[enemycheck] {_map.Id}：刷怪点 {_map.EnemySpawns.Length}，" +
                 $"档位选中 {_mapRoster.Spawns.Count}，上场 {_enemies.Count}");

        // 每只都要有贴图，否则场上只剩个看不见的碰撞体（原版遇到缺外观会直接抛错）。
        var blank = _enemies.Where(enemy => !enemy.Visual.HasArtwork)
            .Select(enemy => enemy.EnemyInfo.Id).ToArray();
        ok &= blank.Length == 0;
        GD.Print($"[enemycheck] 缺贴图的敌人 {blank.Length} 只{(blank.Length > 0 ? "：" + string.Join(",", blank) : string.Empty)}");

        // 静止怪（aggression none：树丛、罐子、箱子）离得再近也不该挪窝。
        var still = _enemies.FirstOrDefault(enemy => enemy.EnemyInfo.Behavior.Aggression == "none");
        if (still is not null)
        {
            var home = still.Position;
            // 把怪挪到玩家身边（20 像素，攻击距离内），再跑 60 帧 AI：静止怪不该挪窝。
            still.Position = new Vector2(_position.X + 20f, _position.Y);
            var placed = still.Position;
            for (var frame = 0; frame < 60; frame += 1) StepEnemyOnce(still);
            var moved = still.Position.DistanceTo(placed);
            ok &= moved < 0.01f;
            GD.Print($"[enemycheck] 静止怪 {still.EnemyInfo.Id} 位移 {moved:F2} 像素");
            still.Position = home;
        }

        var active = _enemies.FirstOrDefault(enemy =>
            enemy.EnemyInfo.Behavior.Aggression == "active" && enemy.EnemyInfo.Behavior.CanMove);
        if (active is not null)
        {
            var home = active.Position;
            active.Position = new Vector2(_position.X + 60f, _position.Y);
            var start = active.Position;
            var startGap = start.DistanceTo(new Vector2(_position.X, _position.Y));
            for (var frame = 0; frame < 30; frame += 1) StepEnemyOnce(active);
            var gap = active.Position.DistanceTo(new Vector2(_position.X, _position.Y));
            ok &= gap < startGap - 1f;
            GD.Print($"[enemycheck] 主动怪 {active.EnemyInfo.Id} 距离 {startGap:F1} → {gap:F1}");
            active.Position = home;
        }

        // 普攻节奏：把一只主动怪贴到玩家身边，
        // 推进 90 个战斗逻辑帧（约 5.4 秒），玩家必须挨至少一次带数值的命中。
        // 命中派发在 strikeFrame 上、动作周期结束回 idle，都由域层的控制器保证。
        var striker = _enemies.FirstOrDefault(enemy =>
            enemy.EnemyInfo.Behavior.Aggression == "active" && enemy.Attack is not null);
        if (striker is not null)
        {
            var strikerHome = striker.Position;
            striker.Position = new Vector2(_position.X + 12f, _position.Y);

            var healthBefore = _playerHealth;
            // 敌人的动作也走常驻运行时，所以这里按真实的逻辑帧推进（一步 60 毫秒），
            // 而不是单独去推进某个播放器。
            for (var frame = 0; frame < 90; frame += 1) UpdateCombat(0.06f);

            var dealt = healthBefore - _playerHealth;
            ok &= dealt > 0;
            GD.Print($"[enemycheck] 普攻结算：贴身 90 帧，玩家生命 {healthBefore} → {_playerHealth}" +
                     $"（周期 {striker.Attack!.EffectiveCycleFrames} 帧、命中帧 {striker.Attack.StrikeFrame}）");
            striker.Position = strikerHome;
        }

        // 资源恢复脉冲：把战斗时钟推过两个脉冲，主角生命必须回升（成员档每脉冲
        // trunc((960+36×等级)÷10) 点）。受伤之后在营地里也会慢慢回血就靠这一条。
        // 一次补帧最多 4 个逻辑帧，所以要按帧推够两个脉冲（31 × 2 帧）再收工。
        var healthBeforePulse = _playerHealth;
        for (var frame = 0; frame < 64; frame += 1) UpdateCombat(0.06f);
        var recovered = _playerHealth - healthBeforePulse;
        ok &= recovered > 0;
        GD.Print($"[enemycheck] 资源脉冲：{healthBeforePulse} → {_playerHealth}（+{recovered}）");

        var passive = _enemies.FirstOrDefault(enemy =>
            enemy.EnemyInfo.Behavior.Aggression == "passive" && enemy.Attack is not null && enemy.Ai is not null);
        if (passive is not null)
        {
            var passiveHome = passive.Position;
            passive.Position = new Vector2(_position.X + 24f, _position.Y);
            for (var frame = 0; frame < 30; frame += 1) StepEnemyOnce(passive);
            var before = passive.Ai!.CurrentTargetId();
            ThreatSystem.Damage(PlayerCombatId, passive.CombatId, 10);
            for (var frame = 0; frame < 30; frame += 1) StepEnemyOnce(passive);
            var after = passive.Ai.CurrentTargetId();
            ok &= before is null && after == PlayerCombatId;
            GD.Print($"[enemycheck] 仇恨接战：挨打前目标 {before ?? "无"}，挨打后 {after ?? "无"}");
            passive.Position = passiveHome;
        }
        else GD.Print("[enemycheck] 场上没有被动的带普攻敌人，跳过仇恨接战");

        var victim = _enemies.FirstOrDefault(enemy =>
            enemy.SpawnKey is not null && Drops.BreakableKindOf(enemy.EnemyInfo.Id) is null);
        if (victim?.SpawnKey is not { } key)
        {
            GD.Print("[enemycheck] 场上没有带生成点 id 的敌人，无法验证击败");
            GetTree().Quit(1);
            return;
        }

        var spawnBefore = _mapRoster.Source(key);
        var expBefore = _hero?.Player.Experience ?? 0;
        var levelBefore = _hero?.Level ?? 1;
        victim.ApplyDamage(victim.Health);
        KillEnemy(victim, PlayerCombatId);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var gone = spawnBefore is not null &&
                   !_session.CanSpawnEnemy(WorldEnemyRoster.ToEnemySpawn(spawnBefore));
        GD.Print($"[enemycheck] 击败 {key}：名册剩 {_mapRoster.Enemies.Count}，" +
                 $"会话判定可生成={!gone}");

        // 经验：玩家打死普通怪要发经验（升级时经验会清零，所以按「经验变了或升级了」判）。
        var expAfter = _hero?.Player.Experience ?? 0;
        var levelAfter = _hero?.Level ?? 1;
        var earned = expAfter != expBefore || levelAfter > levelBefore;
        ok &= earned;
        GD.Print($"[enemycheck] 击杀经验：{levelBefore} 级 {expBefore} → {levelAfter} 级 {expAfter}");

        var vanishEffects = _outcomes?.ActiveEffects ?? 0;
        var fading = _outcomes?.PendingDeaths ?? 0;
        ok &= vanishEffects > 0 && fading > 0;
        GD.Print($"[enemycheck] 倒下表现：消失特效 {vanishEffects} 个，淡出中 {fading} 只");

        LoadMap(_map.Id, new Vec2(_position.X, _position.Y), _player.Facing);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var rebuilt = _enemies.All(enemy => enemy.SpawnKey != key);
        GD.Print($"[enemycheck] 同图重建后仍在场={!rebuilt}，上场 {_enemies.Count}");

        GetTree().Quit(ok && gone && rebuilt ? 0 : 1);
    }

    private async void BeginBagPageCheck()
    {
        // 塞够两页：必须用**不可堆叠**的物品。可堆叠的药水不管塞多少瓶都只占一格，
        // 页数永远是一页——第一版自检就是这么假通过的。
        var sample = _loader.Items.Items
            .FirstOrDefault(pair => pair.Value.Category == "equipment" && !pair.Value.Stackable);
        if (sample.Key is null)
        {
            GD.Print("[bagpagecheck] 内容里找不到不可堆叠的物品，验不了翻页");
            GetTree().Quit(1);
            return;
        }

        for (var index = 0; index < InventoryPaging.PageSize * 2 + 4; index += 1) _bag.Add(sample.Key, 1);
        ToggleBag();
        if (_bagPanel is null)
        {
            GD.Print("[bagpagecheck] 背包面板没建出来");
            GetTree().Quit(1);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var problems = new List<string>();
        var count = _bagPanel.DebugSlotCount;
        var pages = InventoryPaging.PageCount(count);
        GD.Print($"[bagpagecheck] 全部页 {count} 格、{pages} 页，起始 {_bagPanel.DebugPageLabel}");
        if (pages < 2) problems.Add($"只塞出 {pages} 页，验不到翻页（格数 {count}）");
        if (_bagPanel.DebugPage != 0) problems.Add($"进来不在第一页（{_bagPanel.DebugPage}）");

        // 翻页条必须画出来。**一页时也要画**——旧版一页就整条隐藏，
        // 玩家的包里物品少，结果完全看不到这个功能（用户实测反馈「在哪，我怎么没看到」）。
        if (!_bagPanel.DebugPagerDrawn) problems.Add("翻页条没画出来");
        if (InventoryPaging.PageCount(1) != 1) problems.Add("一页的页数算错了");

        // 记下第一页第一格的物品，翻走再翻回来必须还是它（格号不能被翻页改掉）。
        var before = _bag.AllCells;
        _bagPanel.DebugTurnPage(1);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[bagpagecheck] 下一页：{_bagPanel.DebugPageLabel}（页号 {_bagPanel.DebugPage}）");
        if (_bagPanel.DebugPage != 1) problems.Add($"翻下一页后页号是 {_bagPanel.DebugPage}，应为 1");

        // 到底之后继续按不该越界。
        _bagPanel.DebugTurnPage(1);
        _bagPanel.DebugTurnPage(1);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_bagPanel.DebugPage != pages - 1)
            problems.Add($"连翻到底后页号 {_bagPanel.DebugPage}，应为 {pages - 1}");

        _bagPanel.DebugTurnPage(-1);
        _bagPanel.DebugTurnPage(-1);
        _bagPanel.DebugTurnPage(-1);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[bagpagecheck] 翻回：{_bagPanel.DebugPageLabel}（页号 {_bagPanel.DebugPage}）");
        if (_bagPanel.DebugPage != 0) problems.Add($"翻回第一页失败（页号 {_bagPanel.DebugPage}）");

        var after = _bag.AllCells;
        if (after.Count != before.Count || before.Where((cell, index) => cell?.Id != after[index]?.Id).Any())
            problems.Add("翻页之后背包格位变了（翻页不该改格号）");

        if (_bagPanel.DebugGridRect is not { } gridRect) problems.Add("网格区没记下来，手势翻页不会生效");
        else
        {
            GD.Print($"[bagpagecheck] 网格区 {gridRect}");
            if (gridRect.Position.Y < 150f)
                problems.Add($"网格区 y={gridRect.Position.Y:F0} 落到了屏幕上半部，换算用错层了");
            if (gridRect.Size.X < 200f) problems.Add($"网格区宽只有 {gridRect.Size.X:F0}，不像网格");
        }

        _bagPanel.DebugSwipe(20f);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_bagPanel.DebugPage != 0) problems.Add($"横滑 20 像素不该翻页（页号 {_bagPanel.DebugPage}）");

        _bagPanel.DebugSwipe(-120f);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[bagpagecheck] 左滑 → {_bagPanel.DebugPageLabel}");
        if (_bagPanel.DebugPage != 1) problems.Add($"左滑没翻到下一页（页号 {_bagPanel.DebugPage}）");

        _bagPanel.DebugSwipe(120f);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[bagpagecheck] 右滑 → {_bagPanel.DebugPageLabel}");
        if (_bagPanel.DebugPage != 0) problems.Add($"右滑没翻回上一页（页号 {_bagPanel.DebugPage}）");

        _bagPanel.PagerKey(Key.D);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_bagPanel.DebugPage != 1) problems.Add($"按 D 没翻到下一页（页号 {_bagPanel.DebugPage}）");
        _bagPanel.PagerKey(Key.Left);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[bagpagecheck] 键盘 D/← → {_bagPanel.DebugPageLabel}");
        if (_bagPanel.DebugPage != 0) problems.Add($"按 ← 没翻回上一页（页号 {_bagPanel.DebugPage}）");

        // 遗留手势：在格子上按下（被拖动逻辑收走）后，下一次点击的位移不能被
        // 结算成翻页——「点使用总是翻页」的根源。DebugPressGrid 走真实按下路径
        // （起一条手势再被拖动收尾作废）；DebugClick 扮演点说明面板上的「使用」
        // 按钮：网格外按下+松开，把遗留手势作废且不产生翻页。页号必须纹丝不动。
        _bagPanel.DebugPressGrid();
        _bagPanel.DebugClick(_bagPanel.DebugGridRect!.Value.Position - new Vector2(400f, 60f));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_bagPanel.DebugPage != 0) problems.Add($"遗留手势被结算成了翻页（页号 {_bagPanel.DebugPage}）");

        if (problems.Count == 0)
        {
            GD.Print("[bagpagecheck] 通过：翻页只换显示区间，格号与背包状态不变");
            GetTree().Quit(0);
            return;
        }

        GD.Print($"[bagpagecheck] 未通过：{string.Join("；", problems)}");
        GetTree().Quit(1);
    }

    private async void BeginStoryCheck()
    {
        var ok = true;
        var map = _map.Id;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var actor = _loader.World.Actors.ContainsKey("shadow-soldier") ? "shadow-soldier" : "kiyan";
        const string entityId = "story-check-npc";
        const string characterId = "story-check-char";
        var place = new StoryLocation { Map = map, X = _position.X + 32f, Y = _position.Y };
        ApplyStoryCommand(new PlaceCommand(new StoryEntityState
        {
            Id = entityId,
            Map = map,
            Actor = actor,
            CharacterId = characterId,
            Authority = PlacementAuthority.Registered,
            X = place.X,
            Y = place.Y,
            Visible = true,
        }));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var spawned = _npcs.FirstOrDefault(npc => npc.EntityId == entityId);
        ok &= spawned is not null;
        GD.Print($"[storycheck] 登记物件：{(spawned is null ? "未上场" : "已上场")}" +
                 $"，世界记录 {_session.StoryWorld.Entity(entityId)?.Id ?? "无"}");

        // ② 重进图：登记物件必须由投影重新摆出来（而不是靠场景里那份残留）。
        LoadMap(map, new Vec2(_position.X, _position.Y), _player.Facing);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var afterReload = _npcs.FirstOrDefault(npc => npc.EntityId == entityId);
        ok &= afterReload is not null;
        GD.Print($"[storycheck] 重进图后登记物件：{(afterReload is null ? "丢失" : "还在")}");

        // ③ 在场标记：actor-visibility 关掉之后，本图这个人物的剧情实例与静态副本都不该出现。
        ApplyStoryCommand(new ActorVisibilityCommand(map, actor, characterId, false));
        LoadMap(map, new Vec2(_position.X, _position.Y), _player.Facing);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var absent = _npcs.FirstOrDefault(npc => npc.EntityId == entityId);
        ok &= absent is null;
        GD.Print($"[storycheck] 角色不在场标记：{(absent is null ? "生效" : "没生效")}");
        ApplyStoryCommand(new ActorVisibilityCommand(map, actor, characterId, true));
        LoadMap(map, new Vec2(_position.X, _position.Y), _player.Facing);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var back = _npcs.FirstOrDefault(npc => npc.EntityId == entityId);
        ok &= back is not null;
        GD.Print($"[storycheck] 恢复在场：{(back is null ? "没回来" : "回来了")}");

        var slot = _mapRoster?.Spawns.FirstOrDefault(spawn => !spawn.BreakableEligible) ??
                   _mapRoster?.Spawns.FirstOrDefault();
        if (slot is not null)
        {
            ApplyStoryCommand(new EnemyAvailabilityCommand(map, slot.EnemyId, false));
            SyncMapEnemies();
            var disabled = _enemies.Count(enemy => enemy.SpawnKey == slot.Id);
            ok &= disabled == 0;
            GD.Print($"[storycheck] 敌人禁用：槽 {slot.Id}（{slot.EnemyId}）剩 {disabled} 只");

            ApplyStoryCommand(new EnemyAvailabilityCommand(map, slot.EnemyId, true));
            SyncMapEnemies();
            var enabled = _enemies.Count(enemy => enemy.SpawnKey == slot.Id);
            ok &= enabled > 0;
            GD.Print($"[storycheck] 敌人恢复：槽 {slot.Id} 回到 {enabled} 只");

            ApplyStoryCommand(new EnemyLocationCommand(slot.EnemyId,
                new StoryLocation { Map = map, X = _position.X + 56f, Y = _position.Y + 56f }));
            SyncMapEnemies();
            var located = _enemies.FirstOrDefault(enemy =>
                enemy.SpawnKey == "story-located-" + slot.EnemyId);
            ok &= located is not null;
            GD.Print($"[storycheck] 剧情换位：{(located is null ? "未上场" : "已上场")}" +
                     $"（{located?.Position.X:F0}, {located?.Position.Y:F0}）");

            ApplyStoryCommand(new RemoveEnemyCommand(slot.EnemyId, map, true));
            ApplyStoryEnemyRemovals();
            var left = _enemies.Count(enemy => enemy.EnemyInfo.Id == slot.EnemyId);
            ok &= left == 0;
            GD.Print($"[storycheck] 按名移除：{slot.EnemyId} 剩 {left} 只");
        }
        else
        {
            GD.Print($"[storycheck] {map} 没有在场的刷怪槽，跳过敌人三项");
        }

        if (_map.Exits.FirstOrDefault() is { } exit)
        {
            var exitId = StoryWorldState.StoryExitId(map, exit);
            ApplyStoryCommand(new ExitCommand(exitId, false));
            ok &= !_session.StoryWorld.ExitEnabled(map, exit);
            GD.Print($"[storycheck] 出口关闭：{exitId}，" +
                     $"可走={_session.StoryWorld.ExitEnabled(map, exit)}");
            ApplyStoryCommand(new ExitCommand(exitId, true));
        }

        var json = JsonSerializer.Serialize(_session.CaptureState(), SaveGameStore.JsonOptions);
        var snapshot = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;
        _session.RestoreState(snapshot);
        ResetCombatRuntimeForRestore();
        LoadMap(map, new Vec2(_position.X, _position.Y), _player.Facing);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var survived = _npcs.Any(npc => npc.EntityId == entityId);
        ok &= survived;
        GD.Print($"[storycheck] 存档往返：登记物件 {(survived ? "仍在" : "丢失")}，" +
                 $"快照 {json.Length} 字节");

        // ⑦ 黑场：进图那条「黑 → 透明」的淡出补间不该盖掉剧本的 fade(opacity=1)。
        // 开场旁白走的正是这条链（地图刚加载完的淡出还在跑，剧本紧接着把黑场拉满），
        // 之前两边的补间同时写 modulate:a，最后停在 0，旁白就压在舞台画面上。
        PlayFadeIn();
        using (var fadeItem = JsonDocument.Parse("{\"opacity\":1,\"color\":0,\"durationMs\":0}"))
            await PlayFadeAsync(fadeItem.RootElement);
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
        var black = _fade.Modulate.A;
        ok &= black >= 0.99f;
        GD.Print($"[storycheck] 旁白黑场：进图淡出之后仍为 {black:F2}（应为 1.00）");

        GD.Print($"[storycheck] 通过：{ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginEncounterCheck()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        if (_mapRoster is null)
        {
            GD.Print($"[encountercheck] 这张图没有名册，无法验证");
            GetTree().Quit(1);
            return;
        }

        _battle ??= _loader.Battle;
        var enemyId = _mapRoster.Spawns.Select(spawn => spawn.EnemyId).FirstOrDefault()
                      ?? _battle.Enemies.FirstOrDefault()?.Id ?? string.Empty;
        if (enemyId.Length == 0)
        {
            GD.Print("[encountercheck] 找不到可用的敌人定义");
            GetTree().Quit(1);
            return;
        }

        const string id = "check:encounter";
        _session.StoryWorld.Apply(new EncounterCommand(new StoryEncounter
        {
            Id = id,
            DefinitionId = "check-encounter",
            Map = _map.Id,
            Status = "active",
            Spawns =
            [
                new StoryEncounterSpawn { Id = "e0", EnemyId = enemyId, X = _position.X + 240f, Y = _position.Y },
                new StoryEncounterSpawn { Id = "e1", EnemyId = enemyId, X = _position.X + 240f, Y = _position.Y + 32f },
            ],
        }));

        for (var frame = 0; frame < 4; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        string Key(string spawn) => WorldEnemyRoster.EncounterSpawnId(id, spawn);
        var spawned = _enemies.Count(enemy => enemy.SpawnKey is not null && enemy.SpawnKey.StartsWith(id + ":", StringComparison.Ordinal));
        var completedBefore = _mapRoster.CompletedEncounters.Count;
        GD.Print($"[encountercheck] 登场 {spawned}/2，已打计数 {completedBefore}");
        var ok = spawned == 2 && completedBefore == 0;

        foreach (var spawn in new[] { "e0", "e1" })
        {
            var enemy = _enemies.FirstOrDefault(node => node.SpawnKey == Key(spawn));
            if (enemy is null)
            {
                ok = false;
                continue;
            }

            KillEnemy(enemy);
        }

        for (var frame = 0; frame < 4; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var completed = _mapRoster.CompletedEncounters.Any(encounter => encounter.Id == id);
        GD.Print($"[encountercheck] 全灭后判完成={(completed ? "是" : "否")}");
        ok &= completed;

        _session.StoryWorld.Apply(new EncounterResultCommand(id, "won"));
        for (var frame = 0; frame < 4; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var left = _enemies.Count(enemy => enemy.SpawnKey is not null && enemy.SpawnKey.StartsWith(id + ":", StringComparison.Ordinal));
        var stillSpawned = _mapRoster.Spawns.Any(spawn => spawn.Id.StartsWith(id + ":", StringComparison.Ordinal));
        GD.Print($"[encountercheck] 结算后残敌 {left}，刷怪点{(stillSpawned ? "仍在（错）" : "已清")}");
        ok &= left == 0 && !stillSpawned;

        GD.Print($"[encountercheck] 通过：{ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// <c>--encounterrecover</c>：造一份「怪全死了、结果却没写回」的遭遇，重建地图时
    /// 必须把结果补成 <c>won</c>。影子丛林2（shadow-forest-east）的 <c>event-13</c> 在存档里
    /// 就是这个状态：遭遇停在 active，三只怪全是永久死亡，进图后名册把它们算成「在场」
    /// 却又生成不出来——玩家看到的是一场空，剧情也再推不动。
    /// 配 <c>--map shadow-forest-east --encounterrecover</c> 跑。
    /// </summary>
    private async void BeginEncounterRecover()
    {
        if (_mapRoster is null)
        {
            GD.Print("[encounterrecover] 这张图没有名册，无法验证");
            GetTree().Quit(1);
            return;
        }

        _battle ??= _loader.Battle;
        var enemyId = _mapRoster.Spawns.Select(spawn => spawn.EnemyId).FirstOrDefault()
                      ?? _battle.Enemies.FirstOrDefault()?.Id ?? string.Empty;
        if (enemyId.Length == 0)
        {
            GD.Print("[encounterrecover] 找不到可用的敌人定义");
            GetTree().Quit(1);
            return;
        }

        const string id = "check:recover";
        _session.StoryWorld.Apply(new EncounterCommand(new StoryEncounter
        {
            Id = id,
            DefinitionId = "check-recover",
            Map = _map.Id,
            Status = "active",
            Spawns =
            [
                new StoryEncounterSpawn { Id = "e0", EnemyId = enemyId, X = _position.X + 160f, Y = _position.Y },
                new StoryEncounterSpawn { Id = "e1", EnemyId = enemyId, X = _position.X + 192f, Y = _position.Y + 32f },
                new StoryEncounterSpawn { Id = "e2", EnemyId = enemyId, X = _position.X + 224f, Y = _position.Y },
            ],
        }));

        for (var frame = 0; frame < 4; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var spawned = _enemies.Count(enemy => enemy.SpawnKey is not null &&
                                              enemy.SpawnKey.StartsWith(id + ":", StringComparison.Ordinal));
        GD.Print($"[encounterrecover] 登场 {spawned}/3");

        foreach (var enemy in _enemies
                     .Where(node => node.SpawnKey is not null &&
                                    node.SpawnKey.StartsWith(id + ":", StringComparison.Ordinal))
                     .ToArray())
            KillEnemy(enemy);

        var beforeStatus = _session.StoryWorld.Encounter(id)?.Status;
        LoadMap(_map.Id, _position, _player.Facing);
        for (var frame = 0; frame < 4; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var status = _session.StoryWorld.Encounter(id)?.Status;
        var listed = _mapRoster?.Spawns.Count(spawn =>
            spawn.Id.StartsWith(id + ":", StringComparison.Ordinal)) ?? -1;
        var left = _enemies.Count(enemy => enemy.SpawnKey is not null &&
                                           enemy.SpawnKey.StartsWith(id + ":", StringComparison.Ordinal));
        GD.Print($"[encounterrecover] 重建前 {beforeStatus}，重建后 {status}；" +
                 $"名册仍列 {listed} 个刷怪点，场上 {left} 只");

        var ok = spawned == 3 && beforeStatus == "active" && status == "won" && listed == 0 && left == 0;
        GD.Print($"[encounterrecover] 通过：{ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunFlightEffectCheck()
    {
        var problems = new List<string>();
        foreach (var combatClass in _loader.Battle.Classes)
        {
            var profile = combatClass.BasicAttack?.ProfileId;
            if (profile is null || !_loader.Battle.BasicAttacks.TryGetValue(profile, out var rule))
            {
                problems.Add($"{combatClass.Id}：普攻规则缺失（{profile}）");
                continue;
            }

            if (rule.Projectile is not { } projectile)
            {
                var swing = rule.Swings.FirstOrDefault();
                var clip = swing is null ? null : BattleContent.EffectClip(swing, "south");
                GD.Print($"[flighteffectcheck] {combatClass.Id}（{profile}）：近战挥击 {swing ?? "无"} → " +
                         $"{(clip is null ? "取不到（错）" : "有片段")}");
                if (clip is null) problems.Add($"{combatClass.Id} 挥击 {swing}");
                continue;
            }

            var node = SpawnFlightVisual(projectile.Effect, _position, 45, directional: true,
                aboveCharacters: true);
            GD.Print($"[flighteffectcheck] {combatClass.Id}（{profile}）：弹道 {projectile.Effect} " +
                     $"heading-45 → {(node is null ? "取不到（错）" : "已生成")}");
            if (node is null) problems.Add($"{combatClass.Id} 弹道 {projectile.Effect}");
            else node.QueueFree();
        }

        var ok = problems.Count == 0;
        GD.Print($"[flighteffectcheck] 通过：{ok}" +
                 (ok ? "（六个职业都有特效）" : $"（{string.Join("，", problems)}）"));
        GetTree().Quit(ok ? 0 : 1);
    }

    private void RunBasicVariantCheck()
    {
        var problems = new List<string>();
        var wardrobe = _loader.Wardrobe;
        var items = _loader.Items;
        var classId = _classId;

        string? ItemOf(string kind, string slot)
        {
            foreach (var (id, item) in items.Items)
            {
                if (item.Kind != kind || item.Equipment is not { } equipment) continue;
                if (equipment.Slot == slot) return id;
            }
            return null;
        }

        ActorDefinition SyntheticActor(CostumeRecipe recipe)
        {
            var clips = new Dictionary<string, ActorClip>();
            foreach (var (name, poseId) in recipe.Poses)
            {
                if (poseId is null || !wardrobe.Poses.TryGetValue(poseId, out var pose)) continue;
                clips[name] = new ActorClip
                {
                    Frames = Math.Max(1, pose.Frames.Count), FrameWidth = 1, FrameHeight = 1,
                };
            }
            return new ActorDefinition { Id = "variant-check", Name = "variant-check", Clips = clips };
        }

        ResolvedBasicAttack Resolve(BasicAttackDefinition rule, ActorDefinition actor, string profileId,
            Dictionary<string, string>? clips = null)
        {
            var binding = new BasicAttackBinding
            {
                ProfileId = profileId,
                Clips = clips ?? new Dictionary<string, string>
                {
                    ["north"] = "attack-north",
                    ["south"] = "attack-south",
                    ["east"] = "attack-east",
                    ["west"] = "attack-west",
                },
            };
            return BasicAttackRules.Resolve(
                new Dictionary<string, BasicAttackDefinition> { [profileId] = rule }, actor, binding);
        }

        bool HasEffect(string name) =>
            BattleContent.AttackEffects.TryGetValue(name, out var directions) && directions.Clips.Count > 0;

        var stances = wardrobe.Stances.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
        GD.Print($"[basicvariantcheck] 职业 {classId}：{stances.Count} 组武器姿态、" +
                 $"{wardrobe.DualWieldAttacks?.Count ?? 0} 组双持动作、{wardrobe.FrenzyAttacks?.Count ?? 0} 组狂气动作");

        var checkedStances = 0;
        var dualStances = 0;
        var frenzyStances = 0;
        var skipped = new List<string>();
        foreach (var stanceKey in stances)
        {
            var split = stanceKey.Split(':');
            var mainKind = split[0];
            var offKind = split.Length > 1 ? split[1] : "empty";

            var equipment = new Dictionary<string, string>();
            if (mainKind != "unarmed")
            {
                if (ItemOf(mainKind, "main-hand") is not { } mainId)
                {
                    skipped.Add(stanceKey);
                    continue;
                }
                equipment["main-hand"] = mainId;
            }
            if (offKind != "empty")
            {
                var offSlot = offKind == "shield" ? "off-hand" : "main-hand";
                if (ItemOf(offKind, offSlot) is not { } offId)
                {
                    skipped.Add(stanceKey);
                    continue;
                }
                equipment["off-hand"] = offId;
            }

            CostumeRecipe recipe;
            try
            {
                recipe = WardrobeRules.CostumeRecipe(wardrobe, items, equipment, classId);
            }
            catch (Exception error)
            {
                problems.Add($"{stanceKey} 配方失败（{error.Message}）");
                continue;
            }

            if (recipe.Attack is not { } attack)
            {
                problems.Add($"{stanceKey} 没有普攻规则");
                continue;
            }

            var actor = SyntheticActor(recipe);
            ResolvedBasicAttack resolved;
            try
            {
                resolved = Resolve(attack, actor, "equipped");
            }
            catch (Exception error)
            {
                problems.Add($"{stanceKey} 普攻解析失败（{error.Message}）");
                continue;
            }

            checkedStances += 1;
            var frames = actor.Clips.TryGetValue("attack-south", out var south) ? south.Frames : 0;
            var moves = attack.Swings.Length > 0 ? string.Join("+", attack.Swings)
                : attack.Projectile is { } projectile ? $"{projectile.Effect}（弹道）" : "无挥击（空手）";
            var effectBroken = attack.Swings.Any(name => !HasEffect(name)) ||
                               (attack.Projectile is { } flight && !HasEffect(flight.Effect));
            if (effectBroken) problems.Add($"{stanceKey} 特效缺失（{moves}）");

            var dualNote = "-";
            if (recipe.DualWieldAttack is { } dualAttack)
            {
                dualStances += 1;
                var strikes = dualAttack.Strikes ?? [];
                var hands = strikes.Select(strike => strike.Hand).Distinct().OrderBy(hand => hand, StringComparer.Ordinal);
                if (strikes.Length != 2) problems.Add($"{stanceKey} 双持应两击，实际 {strikes.Length} 击");
                if (string.Join("/", hands) != "main-hand/offhand")
                    problems.Add($"{stanceKey} 双持两手不全（{string.Join("/", hands)}）");
                var missing = dualAttack.Clips.Values.Where(name => !actor.HasClip(name)).ToList();
                if (missing.Count > 0) problems.Add($"{stanceKey} 双持动作缺失（{string.Join("/", missing)}）");
                if (dualAttack.Swings.Any(name => !HasEffect(name)))
                    problems.Add($"{stanceKey} 双持挥击缺失（{string.Join("+", dualAttack.Swings)}）");

                try
                {
                    var dualResolved = Resolve(dualAttack, actor, "dual-wield", dualAttack.Clips);
                    var framesWithStrike = strikes.Select(strike => strike.Frame).Distinct()
                        .Count(frame => BasicAttackRules.StrikesAtFrame(dualResolved, frame).Count > 0);
                    if (framesWithStrike != strikes.Select(strike => strike.Frame).Distinct().Count())
                        problems.Add($"{stanceKey} 双持出手帧取不到击打");
                }
                catch (Exception error)
                {
                    problems.Add($"{stanceKey} 双持解析失败（{error.Message}）");
                }

                dualNote = $"{dualAttack.Clips.GetValueOrDefault("south")}×{strikes.Length}击";
            }

            var frenzyNote = "-";
            if (recipe.FrenzyAttack is { } frenzyAttack)
            {
                frenzyStances += 1;
                var missing = frenzyAttack.Clips.Values.Where(name => !actor.HasClip(name)).ToList();
                if (missing.Count > 0) problems.Add($"{stanceKey} 狂气动作缺失（{string.Join("/", missing)}）");
                if (!frenzyAttack.SwingAfterStrike)
                    problems.Add($"{stanceKey} 狂气应 swingOrder=after-strike");
                if (frenzyAttack.Swings.Any(name => !HasEffect(name)))
                    problems.Add($"{stanceKey} 狂气挥击缺失（{string.Join("+", frenzyAttack.Swings)}）");

                try
                {
                    var resolvedFrenzy = Resolve(frenzyAttack, actor, "frenzy", frenzyAttack.Clips);
                    var (_, strike) = BasicAttackRules.Events(resolvedFrenzy, frenzyAttack.StrikeFrame);
                    if (!strike) problems.Add($"{stanceKey} 狂气出手帧取不到击打");
                    // 挥击与出手可以同帧（短剑狂气就是第 2 帧两件事一起），次序由 swingOrder 决定，
                    // 所以只校「有挥击帧」这一条。
                    if (frenzyAttack.SwingFrame is { } swingFrame && swingFrame >= resolvedFrenzy.EffectiveCycleFrames)
                        problems.Add($"{stanceKey} 狂气挥击帧越出周期（{swingFrame}）");
                }
                catch (Exception error)
                {
                    problems.Add($"{stanceKey} 狂气解析失败（{error.Message}）");
                }

                frenzyNote = $"{frenzyAttack.Clips.GetValueOrDefault("south")}";
            }

            GD.Print($"[basicvariantcheck] {stanceKey}：动作 {frames} 帧／周期 {resolved.EffectiveCycleFrames}，" +
                     $"招式 {moves}，双持 {dualNote}，狂气 {frenzyNote}");
        }

        // 主角自己那份也要按装备解析，不能还停在职业绑定上。
        if (_playerAttack is null && _player is not null) _playerAttack = ResolvePlayerAttack();
        if (_playerAttack is not { } current) problems.Add("主角没有普攻规则");
        else if (current.Definition != (_loader.Wardrobe.Attacks.GetValueOrDefault(
                     CurrentStanceKey()) ?? current.Definition))
            problems.Add("主角普攻不是按当前装备取的");

        var ok = problems.Count == 0;
        GD.Print($"[basicvariantcheck] 覆盖 {checkedStances} 组姿态（双持 {dualStances}、狂气 {frenzyStances}），" +
                 $"跳过 {skipped.Count} 组（内容里没有这种武器：{string.Join("/", skipped)}）");
        GD.Print($"[basicvariantcheck] 通过：{ok}" +
                 (ok ? "（每组武器都有动作与特效）" : $"（{string.Join("，", problems)}）"));
        GetTree().Quit(ok ? 0 : 1);
    }

    private string CurrentStanceKey()
    {
        var equipment = _bag is null ? [] : _bag.EquipmentItems(_bag.EquipmentFor(PlayerCombatId));
        var main = equipment.GetValueOrDefault("main-hand") is { Length: > 0 } mainId &&
                   _loader.Items.Items.TryGetValue(mainId, out var mainItem) ? mainItem.Kind : "unarmed";
        var off = equipment.GetValueOrDefault("off-hand") is { Length: > 0 } offId &&
                  _loader.Items.Items.TryGetValue(offId, out var offItem) ? offItem.Kind : "empty";
        return $"{main}:{off}";
    }

    private int SkillRankOrZero(string skillId)
    {
        if (_skills is null || _classId is null) return 0;
        try
        {
            return _skills.SkillLevel(skillId);
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private async void BeginActorRemoveCheck()
    {
        var npc = _npcs.FirstOrDefault();
        if (npc is null)
        {
            GD.Print("[actorremovecheck] 这张图没有地图人物，换一张有人的图跑");
            GetTree().Quit(1);
            return;
        }

        var entityId = npc.EntityId;
        var node = npc.Node;
        ToggleStoryEntity(new ToggleEntityCommand("remove", entityId));
        for (var frame = 0; frame < 3; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var listed = _mapActors.Count(record => ReferenceEquals(record.Node, node));
        var disposed = !GodotObject.IsInstanceValid(node);
        GD.Print($"[actorremovecheck] 撤下 {entityId}：快照表残留 {listed} 条，节点已释放={disposed}");
        var ok = listed == 0 && disposed;

        try
        {
            CheckpointStoryWorld();
            GD.Print("[actorremovecheck] 撤人后记检查点：没抛异常");
        }
        catch (Exception error)
        {
            ok = false;
            GD.Print($"[actorremovecheck] 撤人后记检查点抛了 {error.GetType().Name}");
        }

        var snapshot = CaptureSceneSnapshot();
        var leaked = snapshot.Actors.Any(actor => actor.WorldEntityId == entityId);
        GD.Print($"[actorremovecheck] 快照里{(leaked ? "仍有" : "已无")} {entityId}，共 {snapshot.Actors.Count} 人");
        ok &= !leaked;

        GD.Print($"[actorremovecheck] 通过：{ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginEncounterPlay(string scriptId)
    {
        _autoStory = true;               // 对白自动翻页、走位直接落位
        _hold = true;                    // 续播结束也别自己退出，这里要自己判
        _suppressEncounterAutoWin = true; // 遭遇战要真的挂起等清场

        for (var wait = 0; wait < 3600 && _storyPlaying; wait += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var chain = scriptId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index + 1 < chain.Length; index += 1)
        {
            PlayStory(chain[index]);
            for (var wait = 0; wait < 3600 && _storyPlaying; wait += 1)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        scriptId = chain.Length > 0 ? chain[^1] : scriptId;
        PlayStory(scriptId);

        var guard = 0;
        while (_waitingEncounter is null && _storyPlaying && guard++ < 3600)
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var id = _waitingEncounter;
        if (id is null)
        {
            GD.Print($"[encounterplay] {scriptId} 没停在遭遇战（剧情播放中={_storyPlaying}）");
            GetTree().Quit(1);
            return;
        }

        var encounter = _session.StoryWorld.Encounter(id);
        var expected = encounter?.Spawns.Count ?? 0;
        GD.Print($"[encounterplay] 停在 {id}：{expected} 个生成点，地图 {encounter?.Map}");

        var prefix = id + ":";
        guard = 0;
        while (guard++ < 120 &&
               !_enemies.Any(enemy => enemy.SpawnKey is not null &&
                                      enemy.SpawnKey.StartsWith(prefix, StringComparison.Ordinal)))
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var spawned = _enemies.Count(enemy => enemy.SpawnKey is not null &&
                                              enemy.SpawnKey.StartsWith(prefix, StringComparison.Ordinal));
        GD.Print($"[encounterplay] 上场 {spawned}/{expected}");
        var ok = expected > 0 && spawned == expected;

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--encounterdefer") >= 0)
        {
            PlayStory("event-14");
            for (var frame = 0; frame < 4; frame += 1)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var deferred = _encounterDeferredStory;
            var stillActive = _session.StoryWorld.Encounter(id)?.Status == "active";
            GD.Print($"[encounterplay] 等待期间起剧本：暂缓={(deferred is null ? "无（错）" : deferred.Value.Script)}，" +
                     $"遭遇状态仍 {(stillActive ? "active" : "变了（错）")}");
            ok &= deferred is not null && stillActive;
        }

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--encountershot") >= 0)
        {
            for (var frame = 0; frame < 20; frame += 1)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            GD.Print($"[encounterplay] 现场 {_enemies.Count} 只：" + string.Join("，",
                _enemies.Select(enemy =>
                    $"{enemy.EnemyInfo.Id}@({enemy.Position.X:F0},{enemy.Position.Y:F0})" +
                    $"{(enemy.Visible ? "" : "（不可见）")}")));
            SaveShot();
            return;
        }

        foreach (var enemy in _enemies
                     .Where(node => node.SpawnKey is not null &&
                                    node.SpawnKey.StartsWith(prefix, StringComparison.Ordinal))
                     .ToArray())
            KillEnemy(enemy);

        guard = 0;
        while (guard++ < 3600 && (_waitingEncounter is not null || _storyPlaying || _story.Active))
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var status = _session.StoryWorld.Encounter(id)?.Status;
        GD.Print($"[encounterplay] 续播结束：遭遇状态 {status}，剧本运行中={_story.Active}");
        ok &= status == "won";

        // 暂缓的那段剧本要在结算后被接上，不能就此丢掉。
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--encounterdefer") >= 0)
        {
            GD.Print($"[encounterplay] 结算后暂缓剧本" +
                     $"{(_encounterDeferredStory is null ? "已续上" : "还挂着（错）")}");
            ok &= _encounterDeferredStory is null;
        }

        // 收尾后必须回到正式世界：舞台释放、主角与地图可见、能继续操作。
        // 之前舞台表里残留已释放演员会让收尾抛异常，RevealWorld 被跳过，
        // 表现就是演出结束后人物看不见、镜头像卡住。
        var worldVisible = _player.Visible && _mapRoot.Visible;
        GD.Print($"[encounterplay] 收尾可见性：主角 {_player.Visible}，地图 {_mapRoot.Visible}，" +
                 $"舞台 {(_stage.Active ? "仍开着" : "已释放")}，可操作 {CanControl()}");
        ok &= worldVisible && !_stage.Active && CanControl();

        GD.Print($"[encounterplay] 通过：{ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginChestCheck()    {
        // 宝箱全都有出现判定（每次进图 1..99 掷一次），所以没掷出来就重进一次图。
        MapNpc? chest = null;
        for (var attempt = 0; attempt < 20 && chest is null; attempt += 1)
        {
            if (attempt > 0) LoadMap(_map.Id, new Vec2(_position.X, _position.Y), _player.Facing);
            UpdateInteraction();
            chest = _npcs.FirstOrDefault(npc => npc.ChestKind is not null);
        }

        if (chest is null)
        {
            GD.Print($"[chestcheck] {_map.Id} 连续 20 次进图都没掷出宝箱");
            GetTree().Quit(1);
            return;
        }

        var target = new Vec2(chest.Node.Position.X, chest.Node.Position.Y);
        GD.Print($"[chestcheck] {_map.Id} 的宝箱 {chest.EntityId}（{chest.ChestKind}）在 ({target.X:F0}, {target.Y:F0})");

        var south = new Vec2(target.X, target.Y + 24f);
        var east = new Vec2(target.X + 24f, target.Y);
        string facing;
        Vec2 spot;
        if (_grid.CanStandOnTerrain(south.X, south.Y)) { spot = south; facing = "north"; }
        else if (_grid.CanStandOnTerrain(east.X, east.Y)) { spot = east; facing = "west"; }
        else { spot = target; facing = "south"; }

        _session.Moved(spot, facing);
        _player.Position = new Vector2(spot.X, spot.Y);
        _player.SetFacing(facing);
        UpdateInteraction();

        if (_nearNpc is null)
        {
            GD.Print("[chestcheck] 没选中宝箱");
            GetTree().Quit(1);
            return;
        }

        var rewardsBefore = _session.GroundLoot.ForMap(_map.Id).Count;
        TryInteract();
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);

        var opened = _session.Chests.IsOpened(_map.Id, chest.EntityId, chest.OneTime);
        var landed = _session.GroundLoot.ForMap(_map.Id).Count - rewardsBefore;
        GD.Print($"[chestcheck] 开箱记录={(opened ? "已开" : "未开")}，" +
                 $"地面掉落 +{landed}，场上敌人 {_enemies.Count}，生成记录 {_session.Chests.Spawns.Count}");

        // 同图重建：开过的那个箱子不应该再出现，也不该重新记一次。
        var visitBefore = _session.Chests.Visit;
        LoadMap(_map.Id, spot, facing);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var stillOpenable = _npcs.Any(npc => npc.EntityId == chest.EntityId);
        GD.Print($"[chestcheck] 重进图：访问序号 {visitBefore}→{_session.Chests.Visit}，" +
                 $"原箱子{(stillOpenable ? "又可开了（错）" : "不再出现")}");

        GetTree().Quit(opened && landed > 0 && !stillOpenable ? 0 : 1);
    }

    public override void _PhysicsProcess(double delta)
    {
        // 就绪是分帧跑的：加载页还在的时候世界还没建完，这一步什么都不该推进。
        if (!_worldReady) return;

        var seconds = (float)delta;
        var enabled = CanControl();
        // 菜单/剧情期间的冻结只针对「逻辑推进」：原版暂停的是战斗角色的动画与补间、
        // 特效和受击反馈（world-combat.setPaused），地图 NPC 与环境特效照常播。
        // 所以这里不关整棵 _mapRoot 的 ProcessMode，否则整张地图会一起静止。
        CombatRuntime.SetPaused("control", !enabled);
        _feedbackView?.SetPaused(!enabled || _defeated);
        _outcomes?.SetPaused(!enabled || _defeated);
        SyncDeathVisualPause(!enabled || _defeated);
        SyncStealthVisuals();
        SyncStatusColors();
        if (enabled) _session.Advance((long)(seconds * 1000f));

        var axis = !enabled
            ? Vec2.Zero
            : MovementSolver.NormalizeAxis(CombineAxes());

        if (_awaitingInputRelease)
        {
            if (axis.X == 0f && axis.Y == 0f) _awaitingInputRelease = false;
            else axis = Vec2.Zero;
        }

        if (!enabled)
        {
            if (_storyPlaying)
            {
                _route = null;
                _combatApproach = null;
                _heroFollow = null;
                _player.UpdateMotion(Vec2.Zero, false);
            }
            if (_storyPlaying && FindAllyNode(ControlledId) is { } controlledAlly)
            {
                controlledAlly.HaltMotion();
                controlledAlly.UpdateMotion(Vec2.Zero, false);
            }
        }
        else if (ControlledId != PlayerCombatId)
        {
            StepControlledMember(axis, seconds);
        }
        else
        {
            StepPlayerMotion(axis, seconds);
        }

        if (enabled)
        {
            UpdateExitLock();
            ApplyStoryEnemyRemovals();
            SyncMapEnemies();
            UpdateStoryEncounter();
            // 正式地图里一旦有敌人（宝箱怪、景物怪、遭遇怪），战斗循环就要照常跑——
            // 原版世界战斗一直是开着的，这里此前只有竞技场与遭遇战会驱动。
            // 现在资源恢复脉冲也挂在同一个循环上（回血回蓝、仇恨衰减），
            // 所以没有敌人的地图同样要推进，否则在营地里就永远不会回血。
            UpdateCombat(seconds);
            // 位移在物理步上推进，
            // 与逻辑帧上的决策分开：追近的敌人不会因为掉帧而少走一段。
            if (_enemies.Count > 0) MoveEnemies(seconds);
            foreach (var ally in _allies)
                if (ally.MemberId != ControlledId && Conditions.CanMove(ally.MemberId))
                    ally.Node.StepMotion(MovementSpeed(ally.MemberId, PlayerSpeed) * seconds);
            SeparatePartyAllies();

            // 触发器必须先于出口判定：两者的区域常常重叠（营地出口那 16×64 的
            // 剧情区就压在出口瓦片上），晚一帧就被切图抢走了。
            UpdateTriggers();
            CheckExits();
            UpdateInteraction();

            if (_bag.Revision != _questBagRevision)
            {
                _questBagRevision = _bag.Revision;
                foreach (var questId in _tracker.RefreshItemGoals(_bag))
                    GD.Print($"[quest] {questId} 物品集齐，可以回去交付了");
            }
        }

        UpdateCamera(seconds);
    }

    private readonly record struct CameraPan(Vector2 From, Vector2 To, float Elapsed, float Duration);

    /// <summary>
    /// 剧本镜头要看的点：固定点优先，其次是指定的跟随目标，最后才是当前操控者。
    /// 焦点演员可能刚被剧情退场释放（淡出结束时 QueueFree），碰之前必须验活。
    /// </summary>
    private Vector2 ResolveCameraTarget()
    {
        if (_stage.CameraPoint is { } point) return new Vector2(point.X, point.Y);

        var focus = _stage.CameraFocus;
        if (focus is not null && GodotObject.IsInstanceValid(focus)) return focus.GlobalPosition;

        return ControlledNode().GlobalPosition;
    }

    /// <summary>
    /// 每物理步摆镜头：有滚动就沿直线推进，没有就直接贴到目标（跟随移动中的角色也走这条）。
    /// 滚动只在剧本换镜头时起，跟随本身不该每帧重启一段——所以目标变了不自动起滚动，
    /// 由 <see cref="PanCamera"/> 显式发起。
    /// </summary>
    private void UpdateCamera(float seconds)
    {
        var target = ResolveCameraTarget();

        if (_cameraPan is { } pan)
        {
            var elapsed = pan.Elapsed + seconds;
            var t = Math.Clamp(elapsed / pan.Duration, 0f, 1f);
            _camera.GlobalPosition = pan.From.Lerp(pan.To, t);
            if (t >= 1f)
            {
                _cameraPan = null;
                _camera.GlobalPosition = target;
            }
            else
            {
                _cameraPan = pan with { Elapsed = elapsed };
            }

            return;
        }

        // 目标每帧都可能动（跟着角色走），所以这里不做「变了才写」的短路。
        _camera.GlobalPosition = target;
    }

    /// <summary>
    /// 把镜头滚到当前目标，返回需要等的毫秒数；0 表示已经就位。
    /// 跳过、自动演示与「本来就在那儿」都直接落位，不浪费时间。原版的 pan 是线性匀速，
    /// 时长由距离决定，所以近距离一眨眼、远距离慢慢推过去。
    /// </summary>
    private int PanCamera()
    {
        var target = ResolveCameraTarget();
        var distance = _camera.GlobalPosition.DistanceTo(target);

        var ms = _storySkip is not null ? 0 : StoryCamera.DurationMs(distance);

        if (ms <= 0)
        {
            _cameraPan = null;
            _camera.GlobalPosition = target;
            return 0;
        }

        _cameraPan = new CameraPan(_camera.GlobalPosition, target, 0f, ms / 1000f);
        return ms;
    }

    private async Task WaitCameraAsync(int ms)
    {
        if (ms <= 0) return;

        if (_autoStory) return;

        try
        {
            await StoryClock.WaitAsync(this, ms / 1000f, _storySkip?.Token ?? default);
        }
        finally
        {
            if (_cameraPan is { } pan) _camera.GlobalPosition = pan.To;
            _cameraPan = null;
        }
    }

    private void SnapCamera()
    {
        _cameraPan = null;
        _camera.GlobalPosition = ResolveCameraTarget();
    }

    /// <summary>临时：每 24 帧推一次空格，走真实输入管线验证「按键能不能翻页」。</summary>
    private bool _autoPress;
    private int _pressFrames;

    public override void _Process(double delta)
    {
        if (!_worldReady) return;

        _autoSaver?.Update((long)Time.GetTicksMsec());

        SyncAllies();

        MaybeCapturePendingCheckpoint();
        _statusHud?.Refresh();
        RefreshOverheadStatus();

        // 演出期间 CanControl 为假，物理步里的触发器轮询整段让位——但触发不能就这么丢。
        // 先把此刻本该派发的候选记进待办，等这段演完再按当时的世界状态重判补演
        //。
        if (_storyPlaying) CollectDeferredTriggers();
        _statusHud?.SetTarget(CanControl() ? CurrentTargetStatus() : null);
        SyncNameBanner();

        if (_skipButton is not null) _skipButton.Visible = CanSkipStory();

        // 菜单遮罩与关闭钮：只要有菜单页开着就一起露面（含系统页——它的画布是另一套
        // 缩放坐标，自己画的那个叉会和这里对不上，所以统一由这一个负责）。
        // 键位编辑器例外：它自己左上角就有一枚关闭钮，这枚再露出来就是画布右上角多一个叉，
        // 点下去还会把整个菜单连同编辑现场一起关掉。
        var menuOpen = MenuOpen();
        var editingLayout = _layoutEditor is { IsOpen: true };
        if (_menuBackdrop is not null) _menuBackdrop.Visible = menuOpen;
        if (_menuClose is not null) _menuClose.Visible = menuOpen && !editingLayout;
        // 左侧那两枚钮：菜单、演出、阵亡期间收起来——那几种状态下不该让人存档／开设置。
        var hudButtons = !menuOpen && !editingLayout && !_storyPlaying && !_defeated;
        if (_saveButton is not null) _saveButton.Visible = hudButtons;
        if (_settingsButton is not null) _settingsButton.Visible = hudButtons;

        if (_autoPress && ++_pressFrames % 8 == 0)
        {
            Input.ParseInputEvent(new InputEventKey { Pressed = true, Keycode = Key.Space });
            Input.ParseInputEvent(new InputEventKey { Pressed = false, Keycode = Key.Space });
        }

        if (_skipCheck && _storyPlaying && !_skipCheckFired && ++_skipCheckFrames >= 30)
        {
            _skipCheckFired = true;
            var visible = CanSkipStory();
            _story.Skip();
            GD.Print($"[skipcheck] 第 {_skipCheckFrames} 帧按下跳过：按钮可见={visible}，" +
                     $"跳过中={_story.Skipping}，节点={_story.NodeId ?? "无"}");
        }

        if (_probeFps && ++_frameCounter % 60 == 0)
            GD.Print($"[fps] 第 {_frameCounter} 帧：{Engine.GetFramesPerSecond()} fps");

        if (_shotCountdown > 0 && --_shotCountdown == 0) SaveShot();

        if (_exitCheck)
        {
            if (_map.Id != StartMapId)
            {
                GD.Print($"[exitcheck] 切图成功：{_map.Id}，落点 ({_position.X:F0}, {_position.Y:F0})");
                _exitCheck = false;
                GetTree().Quit();
                return;
            }

            if (++_exitCheckFrames > 1200)
            {
                GD.Print($"[exitcheck] 超时：停在 ({_position.X:F0}, {_position.Y:F0})，走位状态 {_route?.State.ToString() ?? "无"}");
                _exitCheck = false;
                GetTree().Quit(1);
                return;
            }

            if (_exitCheckFrames % 60 == 0)
                GD.Print($"[exitcheck] 第 {_exitCheckFrames} 帧，位置 ({_position.X:F0}, {_position.Y:F0})，路线 {_route?.State.ToString() ?? "无"}");
        }

        if (_hud is null) return;
        var route = _route is null ? string.Empty : $"  →  {_route.State}";
        SetHudLine(_hud, $"{_map.Name}  v{_session.Visit}  {FacingLabel(_player.Facing)}  " +
                        $"({Mathf.RoundToInt(_position.X)}, {Mathf.RoundToInt(_position.Y)})" +
                        $"{(_displaySettings.Fps ? $"  {Engine.GetFramesPerSecond()} fps" : string.Empty)}{route}");
        SetHudLine(_questHud, QuestHud());
        SetHudLine(_hintHud, InteractionHud());
        // 有可交互对象时才吃点击，否则让开——不然这行会把左上角的触摸全吞掉。
        _hintHud.MouseFilter = _nearNpc is not null && !_storyPlaying
            ? Control.MouseFilterEnum.Stop
            : Control.MouseFilterEnum.Ignore;
        SetHudLine(_lootHud, LootHud());
        SetHudLine(_lootMissedHud, LootMissedHud());

        if (_actionControls is not null)
        {
            _actionControls.Visible = CanControl();
            _stick.SetStickVisible(CanControl());
            RefreshShortcutPage();
        }

        ProjectPlayerEquipment();
        ProjectMemberEquipment();

        if (CanControl()) _session.GroundLoot.Advance(_map.Id, (int)(delta * 1000.0));
        if (CanControl()) CollectGroundLoot();
        SyncGroundLoot();

        _pickupMessages.SetPaused(!CanControl());
        _pickupMessages.Tick((long)Time.GetTicksMsec(), (int)(delta * 1000.0));
        _groundLootView.SetPaused(!CanControl());

        TickHudNotice((float)(delta * 1000.0));
    }

    private bool CanControl() =>
        !_loadingWorld && !_audioSuspended && _layoutEditor is not { IsOpen: true } &&
        !_defeated && _defeatPanel is not { IsOpen: true } &&
        !IsAnyPanelOpen() && !_storyPlaying && !_dialogue.Visible && !_choice.Visible && !_questPopup.Visible;

    private (int Health, int MaxHealth, int Mana, int MaxMana)? OverheadVitals(string id)
    {
        var vitals = id == PlayerCombatId ? _hero?.Player : _party?.Get(id)?.Player;
        return vitals is { } value ? (value.Health, value.MaxHealth, value.Mana, value.MaxMana) : null;
    }

    private void RefreshOverheadStatus()
    {
        var heroShown = CanControl() || _layoutEditor is { IsOpen: true };
        if (_overhead is not null && GodotObject.IsInstanceValid(_overhead))
        {
            _overhead.Refresh();
            _overhead.Sync(heroShown, _player.Definition.HeadAnchor);
        }

        foreach (var ally in _allies)
        {
            if (!GodotObject.IsInstanceValid(ally.Overhead)) continue;
            ally.Overhead.Refresh();
            ally.Overhead.Sync(!_storyPlaying, ally.Node.Definition.HeadAnchor);
        }
    }

    /// <summary>
    /// 名条每帧同步。
    ///
    /// 名字取「摆放名优先、否则人物名」：内容里 2619 个摆放没有一个写 `name` 覆盖，
    /// 所以实际永远是人物定义里的中文名；Godot 的 <c>MapEntity</c> 因此也没建模这个字段。
    /// 显隐跟主角头顶条同一个口径：能操作、或正在编辑操控布局。
    /// </summary>
    private void SyncNameBanner()
    {
        if (_nameBanner is null) return;
        _nameBanner.Sync(
            CanControl() || _layoutEditor is { IsOpen: true },
            _nearNpc is { } near && near.Definition.Name.Length > 0 ? near.Definition.Name : null);
    }

    private StatusMemberValues ReadControlledValues()
    {
        var view = MemberViewOrPlayer(ControlledId);
        return new StatusMemberValues(view.Level, view.Player.Health, view.Player.MaxHealth,
            view.Player.Mana, view.Player.MaxMana, view.Player.Experience, view.Player.NextLevelExperience);
    }

    private string? HudPortraitFor(string id)
    {
        if (id == PlayerCombatId) return _loader.StatusHud.Assets.GetValueOrDefault("player-portrait");
        var member = _party?.Get(id);
        if (member is null) return null;
        var actor = _loader.World.Actors.GetValueOrDefault(member.Definition.Actor);
        if (member.Source == "story" && !string.IsNullOrEmpty(actor?.Portrait))
            return _loader.MenuPanels.Character.Portraits.GetValueOrDefault(actor!.Portrait!)?.PartyIcon;
        return _loader.MenuPanels.Asset($"party-class-{member.ClassId}");
    }

    private IReadOnlyList<(string Id, StatusMemberValues Values, bool Living, string? Portrait)> ReadOtherMembers()
    {
        var list = new List<(string, StatusMemberValues, bool, string?)>();
        foreach (var id in PartyMembers.ActiveIds(_party))
        {
            if (id == ControlledId) continue;
            var view = PartyMembers.Of(_party, _hero, _loader.Campaign.Start.Actor,
                _loader.World, _loader.StoryClasses, _loader.MenuPanels, id);
            if (view is null) continue;
            list.Add((id,
                new StatusMemberValues(view.Level, view.Player.Health, view.Player.MaxHealth,
                    view.Player.Mana, view.Player.MaxMana, view.Player.Experience, view.Player.NextLevelExperience),
                _party?.Alive(id) ?? view.Player.Health > 0,
                // 头像与「谁是当前操控者」用同一条规则，
                // 不能拿菜单那套 memberArt 顶上——两者在「剧情伙伴没配立绘」时结果不同。
                HudPortraitFor(id)));
        }

        return list;
    }

    private void UseShortcutItem(string itemId)
    {
        if (!CanControl()) return;
        var entry = _bag.Entries.FirstOrDefault(value => value.ItemId == itemId);
        if (entry is null) return;

        if (_bag.Catalog.Get(entry.ItemId).Use?.Kind is ItemUseRules.RecoverHealth or ItemUseRules.Regenerate)
        {
            var recovery = UseRecoveryItem(entry.Id);
            if (!recovery.Used) ShowHudNotice(RecoveryProblemText(recovery.Reason));
            RefreshCharacterFromBag();
            return;
        }

        try
        {
            _bag.Use(entry.Id);
            RefreshCharacterFromBag();
        }
        catch (Exception error)
        {
            ShowHudNotice(error.Message);
        }
    }

    private RecoveryResult UseRecoveryItem(string entryId, string? userId = null)
    {
        if (!CanControl()) return RecoveryResult.Reject("unavailable");
        var controlled = userId ?? ControlledId;

        var members = new List<IRecoveryMember>();
        foreach (var id in _party?.ActiveIds ?? [PlayerCombatId])
        {
            if (id == PlayerCombatId)
            {
                if (_hero is not null)
                    members.Add(new RecoveryTarget(id, () => _hero.Player, _hero.SetHealth, _hero.SetMana));
            }
            else if (_party?.Get(id) is { } companion)
            {
                members.Add(new RecoveryTarget(id, () => companion.Player,
                    companion.SetHealth, companion.SetMana));
            }
        }

        var modifiers = new RecoveryModifiers(
            Sleeping: Conditions.HasState(controlled, "sleep"),
            RecoveryBonus: _session.GroupModifiers().GetValueOrDefault("potion-recovery") != 0,
            CooldownReductionMs: _session.GroupModifiers().GetValueOrDefault("potion-cooldown-ms"));

        var result = _session.Recovery.Use(_bag, entryId, members, controlled, modifiers, _random.NextDouble);
        if (!result.Used) return result;

        foreach (var memberId in result.Effects)
            if (ActorNodeOf(memberId) is { } healed) _outcomes?.Recover(healed);

        _audio?.Ui("heal");
        RefreshCharacterFromBag();
        _statusHud?.Refresh();   // 数值变了，状态条跟着刷
        return result;
    }

    private static string RecoveryProblemText(string? reason) => reason switch
    {
        "blocked" => "现在不能使用恢复药",
        "cooldown" => "恢复药还在冷却中",
        "full-health" => "生命值已满，不需要恢复",
        _ => "现在无法使用恢复药",
    };

    private void SwapShortcutPage()
    {
        if (!CanControl()) return;
        var member = MemberViewOrPlayer(_party?.ControlledId ?? "player");
        var configuration = member.IsPlayer ? (SkillConfiguration?)_skills : member.Skills;
        configuration?.NextShortcutPage(member.ClassId);
    }

    private void RefreshShortcutPage()
    {
        var member = MemberViewOrPlayer(_party?.ControlledId ?? "player");
        var configuration = member.IsPlayer ? (SkillConfiguration?)_skills : member.Skills;
        var classId = member.ClassId;
        var slots = new HudShortcutSlot[ShortcutLayout.SlotCount];
        if (configuration is null)
        {
            _actionControls.SetShortcutPage(slots);
            return;
        }

        var page = configuration.ShortcutPage(classId);
        var bar = member.IsPlayer
            ? _skills!.Bar()
            : ShortcutBar.For(_bag, member.Owner, configuration.ActiveSkills(_loader.Skills, classId));
        var entries = bar.Page(page);
        var cooldowns = _session.Cooldowns.ForActor(ControlledId).Snapshot();
        var (recoveryRemaining, recoveryDuration, _) = _session.Recovery.Cooldown();
        for (var index = 0; index < slots.Length; index += 1)
        {
            var entry = index < entries.Length ? entries[index] : null;
            if (entry is null)
            {
                slots[index] = HudShortcutSlot.Empty;
            }
            else if (entry.Kind == ShortcutEntry.SkillKind)
            {
                var cost = SkillManaCostFor(entry.Id);
                slots[index] = new(entry, _loader.Skills.Skills.GetValueOrDefault(entry.Id)?.Icon, 0,
                    SkillSlotAvailable(entry.Id) && PlayerMana >= cost)
                {
                    Cooldown = cooldowns.GetValueOrDefault(entry.Id),
                };
            }
            else
            {
                var quantity = _bag.Count(entry.Id);
                var definition = _loader.Items.Items.GetValueOrDefault(entry.Id);
                var cooldown = definition?.Use?.Kind == ItemUseRules.RecoverHealth &&
                               recoveryRemaining > 0 && recoveryDuration > 0
                    ? new Cooldown(recoveryRemaining, recoveryDuration)
                    : null;
                slots[index] = new(entry, definition?.Icon, quantity, quantity > 0)
                {
                    Cooldown = cooldown,
                };
            }
        }

        _actionControls.SetShortcutPage(slots);
    }

    public int SkillManaCostFor(string skillId)
    {
        var definition = _loader.Skills.Skills.GetValueOrDefault(skillId);
        var level = _skills?.Draft(_loader.Skills, _classId).LevelOf(skillId) ?? 0;
        foreach (var rank in definition?.Ranks ?? [])
            if (rank.Level == level)
                return CombatResources.CalculateManaCost(rank.ManaCost,
                    AttributeOf(PlayerCombatId, "manaCostRate"),
                    AttributeOf(PlayerCombatId, "manaCostReductionRate"));
        return int.MaxValue;
    }

    private void CastShortcutSkill(string skillId)
    {
        if (!CanControl()) return;
        if (PlayerCastSkill(skillId) is not { } failure) return;
        ShowHudNotice(failure switch
        {
            "no-skill" => "该技能需要装备对应武器",
            "no-mana" => "法力不足",
            "cooldown" => "技能还在冷却",
            "no-target" => "没有可攻击的目标",
            "unavailable" => "当前无法使用该技能",
            _ => "现在用不了这个技能",
        });
    }

    private void ShowHudNotice(string message)
    {
        _hudNotice.Text = message;
        _hudNoticeRemainingMs = InstantMessages.DurationMs;
        PlaceHudNotice();
        _hudNotice.Modulate = Colors.White;
        _hudNotice.Visible = true;
    }

    private void PlaceHudNotice()
    {
        var size = GetViewportRect().Size;
        var (x, y) = InstantMessages.Point(size.X, size.Y);
        const float halfWidth = 280f;
        _hudNotice.OffsetLeft = x - halfWidth;
        _hudNotice.OffsetRight = x + halfWidth;
        _hudNotice.OffsetTop = y;
        _hudNotice.OffsetBottom = y + 40f;
    }

    private void TickHudNotice(float deltaMs)
    {
        if (_hudNoticeRemainingMs <= 0)
        {
            if (_hudNotice.Visible) _hudNotice.Visible = false;
            return;
        }

        _hudNoticeRemainingMs -= (long)Math.Round(deltaMs);
        var alpha = InstantMessages.Alpha(_hudNoticeRemainingMs);
        if (alpha <= 0f)
        {
            _hudNoticeRemainingMs = 0;
            _hudNotice.Visible = false;
            return;
        }

        var blocked = _loadingWorld || _defeated || _defeatPanel is { IsOpen: true } ||
                      _dialogue.Visible || _choice.Visible || _questPopup.Visible;
        if (blocked)
        {
            _hudNotice.Visible = false;
            return;
        }

        PlaceHudNotice();
        _hudNotice.Modulate = new Color(1f, 1f, 1f, alpha);
        _hudNotice.Visible = true;
    }

    private static string? ReadUserText(string key) => UserStorage.Read(key);

    private static void WriteUserText(string key, string content) => UserStorage.Write(key, content);

    private string InteractionHud()
    {
        if (_nearNpc is not { } npc || _storyPlaying) return string.Empty;

        var name = _loader.World.Actors.TryGetValue(npc.ActorId, out var definition) && definition.Name.Length > 0
            ? definition.Name
            : npc.ActorId;

        if (npc.ChestKind is not null) return $"开箱：{name}";

        if (npc.Spring) return $"喝泉水：{name}";

        var slot = _journal.SlotFor(npc.ActorId, npc.CharacterId);
        var marker = slot is null
            ? string.Empty
            : _journal.StatusOf(slot.Id) switch
            {
                QuestStatus.Available => "（有任务可接）",
                QuestStatus.Ready => "（可以交付）",
                _ => "（可以谈进度）",
            };

        return $"交谈：{name}{marker}";
    }

    private string QuestHud()
    {
        if (_tracker is null) return string.Empty;

        var quest = _tracker.Guidance();
        if (quest is null) return string.Empty;

        var goal = _tracker.GoalText(quest);
        return goal.Length > 0 ? $"◆ {quest.Title}：{goal}" : $"◆ {quest.Title}";
    }

    private SaveGame? CaptureSaveGame()
    {
        if (_map is null) return null;
        _session.StoryCursor = _story.CaptureCursor();
        return SaveGameStore.CaptureSaveGame(_session, CaptureWorldState(), SaveSummaryOf());
    }

    private JsonNode? CaptureWorldState()
    {
        var state = new WorldSnapshotState
        {
            Location = new WorldLocationSaved
            {
                Map = _map!.Id,
                X = _position.X,
                Y = _position.Y,
                Facing = _player.Facing,
            },
            Conditions = _conditions?.CaptureState(),
            Threat = _threat?.CaptureState(),
            Party = [],
            Enemies = [],
            FocusTargetId = _focusTargetId,
            DamageCount = _damageCount,
        };

        state.Party.Add(new WorldMemberSaved
        {
            Id = PlayerCombatId,
            X = _position.X,
            Y = _position.Y,
            Facing = _player.Facing,
            Fallen = _playerHealth <= 0,
        });
        foreach (var ally in _allies)
        {
            if (_party?.Get(ally.MemberId) is not { } member) continue;
            state.Party.Add(new WorldMemberSaved
            {
                Id = ally.MemberId,
                X = ally.Node.Position.X,
                Y = ally.Node.Position.Y,
                Facing = ally.Node.Facing,
                Fallen = !member.Alive,
                Ai = ally.Ai.CaptureState(),
            });
        }

        foreach (var enemy in _enemies)
        {
            state.Enemies.Add(new WorldEnemySaved
            {
                Id = enemy.CombatId,
                EnemyId = enemy.EnemyInfo.Id,
                X = enemy.Position.X,
                Y = enemy.Position.Y,
                Health = enemy.Health,
                SummonedBy = enemy.SummonedBy,
                SummonAttributes = enemy.SummonAttributes,
                Facing = enemy.Facing,
                SummonRemainingMs = enemy.SummonRemainingMs,
                EnemyAi = enemy.SummonAi is null ? enemy.Ai?.CaptureState() : null,
                SummonAi = enemy.SummonAi?.CaptureState(),
            });
        }

        state.GeneratedEntities = [.. _baseEntities.Select(entity => entity.WithActor(entity.Actor))];
        state.Scene = CaptureSceneSnapshot();
        if (_audio?.CaptureStoryAudio() is var (music, ambience) && (music is not null || ambience is not null))
            state.Audio = new StoryAudioSaved { Music = music, Ambience = ambience };

        return JsonSerializer.SerializeToNode(state, SaveGameStore.JsonOptions);
    }

    private SceneSnapshotSaved CaptureSceneSnapshot()
    {
        var scene = new SceneSnapshotSaved { Map = _map!.Id };
        scene.Actors.Add(new SceneActorSaved
        {
            Id = "player",
            Actor = _player.Definition.Id,
            CharacterId = null,
            X = _position.X,
            Y = _position.Y,
            Facing = _player.Facing,
            Visible = _player.Visible,
        });

        foreach (var record in _mapActors.ToArray())
        {
            if (!GodotObject.IsInstanceValid(record.Node))
            {
                _mapActors.Remove(record);
                continue;
            }

            scene.Actors.Add(new SceneActorSaved
            {
                Id = $"entity:{record.EntityId}",
                Actor = record.ActorId,
                CharacterId = record.CharacterId,
                WorldEntityId = record.EntityId,
                Authority = record.Authority,
                X = record.Node.Position.X,
                Y = record.Node.Position.Y,
                Facing = record.Node.Facing,
                Visible = record.Node.Visible,
                Flip = record.Flip,
                Alpha = record.Node.Modulate.A,
            });
        }

        return scene;
    }

    private string? LoadFromSlot(SaveSlotRef reference, bool showLoading = false)
    {
        if (_saveSlots.Inspect(reference) is not SlotReading.Ok ok)
            return "存档读取失败。";
        try
        {
            var prepared = SavedGameUpgrades.PrepareSavedGame(ok.Game, _loader.StoryClasses, _loader.World);
            // 上一份现场演到一半的运行不能带进来：续播靠存档里的游标，不靠内存里的旧状态。
            _story.Reset();

            if (_bagPanel is { IsOpen: true }) _bagPanel.Close();
            if (_shopPanel is { IsOpen: true }) _shopPanel.Close();
            if (_craftPanel is { IsOpen: true }) _craftPanel.Close();
            if (_skillPanel is { IsOpen: true }) _skillPanel.Close();
            if (_statusPanel is { IsOpen: true }) _statusPanel.Close();
            if (_questPanel is { IsOpen: true }) _questPanel.Close();
            if (_partyPanel is { IsOpen: true }) _partyPanel.Close();
            if (_systemPanel is { IsOpen: true }) _systemPanel.Close();

            SaveGameStore.RestoreSaveGame(_session, prepared.Game);
            ResetCombatRuntimeForRestore();
            _currentSaveSlot = reference.IsAuto ? _currentSaveSlot : reference;
            _pendingWorldRestore = ReadWorldSnapshot(prepared.Game);
            _skipMapEntryAutoSave = true;
            if (showLoading)
                LoadMapWithLoading(prepared.MapId, new Vec2((float)prepared.X, (float)prepared.Y),
                    prepared.Facing, "正在读取存档…", 0f);
            else
                LoadMap(prepared.MapId, new Vec2((float)prepared.X, (float)prepared.Y), prepared.Facing);
            _loadedFromSave = true;
            return null;
        }
        catch (Exception error)
        {
            GD.PushWarning("读档失败：", error.Message);
            return error.Message;
        }
    }

    private WorldSnapshotState? ReadWorldSnapshot(SaveGame game)
    {
        if (game.World is null) return null;
        var state = JsonSerializer.Deserialize<WorldSnapshotState>(game.World, SaveGameStore.JsonOptions);
        if (state is null || !state.HasWorld) return null;

        CombatSnapshotValidation.Validate(state,
            _loader.Battle.Enemies.Select(enemy => enemy.Id).ToHashSet(), ActivePartySlots());
        return state;
    }

    private SaveSummary SaveSummaryOf()
    {
        var classId = _classId;
        var storyClass = _loader.StoryClasses.Class(classId);
        var portraits = _loader.MenuPanels.Character.Portraits;
        var portraitKey = _loader.World.Actors.TryGetValue(_loader.Campaign.Start.Actor, out var actor)
            ? actor.Portrait ?? "kiyan"
            : "kiyan";
        var portrait = portraits.GetValueOrDefault(portraitKey)?.Image
                       ?? portraits.GetValueOrDefault("kiyan")?.Image ?? string.Empty;
        return SaveGameStore.SaveSummary(_session, _map.Id, _map.Name, storyClass?.Name ?? classId, portrait);
    }

    private bool IsAnyPanelOpen() =>
        _bagPanel is { IsOpen: true } || _shopPanel is { IsOpen: true } ||
        _craftPanel is { IsOpen: true } || _skillPanel is { IsOpen: true } ||
        _statusPanel is { IsOpen: true } || _questPanel is { IsOpen: true } ||
        _partyPanel is { IsOpen: true } || _systemPanel is { IsOpen: true };

    public override void _Notification(int what)
    {
        if (what == (int)NotificationWMCloseRequest || what == (int)NotificationApplicationPaused)
            _autoSaver?.FlushForBackground((long)Time.GetTicksMsec());

        // 应用被挂起（切到后台、被系统暂停）时新起的音效与循环轨都不该出声。
        // 对应 <c>app.suspended</c>（document.hidden 或竖屏），由 的
        
        if (what == (int)NotificationApplicationPaused) _audioSuspended = true;
        else if (what == (int)NotificationApplicationResumed) _audioSuspended = false;

        // 安卓返回键：工程里已经把 quit_on_go_back 关掉（否则引擎会直接结束进程），
        // 所以这条通知现在真的会到这里来，交给和 Esc／右上角那个叉同一套次序。
        if (what == (int)NotificationWMGoBackRequest) HandleBackRequest();
    }

    private bool IsAudioSuspended() => _audioSuspended;

    private bool _audioSuspended;

    private (string? Music, string? Ambience)? _storyAudioBaseline;

    private void RestoreStoryAudioBaseline()
    {
        if (_storyAudioBaseline is not { } baseline) return;
        _storyAudioBaseline = null;
        try
        {
            _audio?.RestoreStoryAudio(baseline.Music, baseline.Ambience);
        }
        catch (InvalidOperationException error)
        {
            GD.PushWarning($"[story] 恢复音轨失败：{error.Message}");
        }
    }

    private sealed class AutoSavePorts(WorldScene scene) : IAutoSavePorts
    {
        public SaveGame? Capture() => scene.CaptureSaveGame();

        public void Write(SaveGame game) => scene._saveSlots.Write(SaveSlotRef.Auto, game);

        public bool Enabled() => !scene._arena;

        /// <summary>演出与菜单锁着时不抢写：等解锁后再写，免得存到一半的状态。</summary>
        /// <remarks>
        /// 全员倒下时也必须锁住。战败面板不在 <see cref="IsAnyPanelOpen"/> 里（它不是常规菜单），
        /// 早先只判演出与菜单，于是队伍倒下的那一刻照样落盘——下次读档直接是「已全灭」，
        /// 进图再记的检查点也是这份死现场，返回检查点只是把同一份状态重新载入，复活即再死。
        /// </remarks>
        public bool Locked() => scene._storyPlaying || scene._defeated ||
            scene._defeatPanel is { IsOpen: true } || scene.IsAnyPanelOpen();
    }

    private static string FacingLabel(string facing) => facing switch
    {
        "south" => "朝南",
        "north" => "朝北",
        "east" => "朝东",
        "west" => "朝西",
        _ => facing,
    };

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_worldReady) return;

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (_layoutEditor is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape) _layoutEditor.Cancel();
                return;
            }

            if (_craftPanel is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape) _craftPanel.Escape();
                return;
            }

            if (_shopPanel is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape) _shopPanel.Escape();
                return;
            }

            if (_bagPanel is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape)
                {
                    if (!_bagPanel.Escape()) _bagPanel.Close();
                }
                else if (key.Keycode == Key.I) _bagPanel.Close();
                else _bagPanel.PagerKey(key.Keycode);
                return;
            }

            if (_skillPanel is { IsOpen: true })
            {
                if (key.Keycode is Key.Escape)
                {
                    if (!_skillPanel.Escape()) _skillPanel.Close();
                }
                else if (key.Keycode == Key.K) _skillPanel.Close();
                return;
            }

            if (_statusPanel is { IsOpen: true })
            {
                if (key.Keycode is Key.Escape) _statusPanel.Close();
                else if (key.Keycode == Key.C) _statusPanel.Close();
                return;
            }

            if (_questPanel is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape) _questPanel.Close();
                return;
            }

            if (_partyPanel is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape)
                {
                    if (!_partyPanel.Escape()) _partyPanel.Close();
                }
                return;
            }

            if (_systemPanel is { IsOpen: true })
            {
                if (key.Keycode == Key.Escape) _systemPanel.Close();
                return;
            }

            if (!CanControl()) return;

            if (key.Keycode == Key.I)
            {
                ToggleBag();
                return;
            }

            if (key.Keycode == Key.K)
            {
                ToggleSkills();
                return;
            }

            if (key.Keycode == Key.C)
            {
                ToggleStatus();
                return;
            }

            if (key.Keycode == Key.Q)
            {
                ToggleQuests();
                return;
            }

            if (key.Keycode is Key.E or Key.Space or Key.J)
            {
                if (_nearNpc is not null && !_storyPlaying)
                {
                    TryInteract();
                    return;
                }

                if (key.Keycode == Key.E) PlayerAttack();
                return;
            }
        }

        if (!CanControl()) return;   // 被界面挡住的点击不应变成世界走位目标。

        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;

        // 剧情演出期间点地面不该变成走路：这一下多半是在点对话框翻页，
        // 对话框本身现在不吃鼠标（原版就是点画面任意处翻页）。
        if (_storyPlaying) return;

        var target = GetGlobalMousePosition();
        if (!_grid.CanStandOnTerrain(target.X, target.Y)) return;   // 点到墙里就当作没点

        _route = new WalkRoute(_grid, ControlPosition, new Vec2(target.X, target.Y),
            MovementSpeed(ControlledId, PlayerSpeed));
    }

    /// <summary>
    /// 换一张图：丢掉旧的三个图层，建新的，再把主角挪进来。
    /// 主角节点不重建——否则切图会重置动画，看起来像「人物闪了一下」。
    /// </summary>
    private void LoadMap(string mapId, Vec2 spawn, string facing)
    {
        var previousRoot = _mapRoot;
        var previousLease = _mapLease;

        _map = _loader.LoadMap(mapId);
        _grid = new CollisionGrid(_map);
        _nameBanner?.Setup(_map.Name);
        _navigation = null;
        _route = null;
        _spawnedActors = 0;
        // 换图不该继承上一张图的镜头滚动（否则落地后镜头还从旧位置滑过来）。
        _cameraPan = null;

        _story.CurrentMap = mapId;

        if (_stage.Active) _stage.Release();
        RevealWorld();

        _mapLease = _textures.Lease();

        _mapRoot = new Node2D { Name = "Map" };
        AddChild(_mapRoot);

        // 进图先记剧情落点：换图会开新的一次访问，
        // 只活一次访问的过场人物与临时敌人随之作废——所以必须早于人物投影。
        _session.StoryWorld.Apply(new TravelCommand(new StoryLocation
        {
            Map = mapId,
            X = spawn.X,
            Y = spawn.Y,
            Facing = facing,
        }));

        // 会话进图：宝箱、商店、敌人状态各自按「换没换图」结算，同样要早于投影——
        // 否则投影看到的还是上一张图的宝箱开启记录与访问序号。
        _session.EnterMap(mapId, spawn, facing, _map.EnemySpawns, _map.QuestItemSpawns);

        // 进图换 BGM／环境音。.enterMap 在场景转场时调；地图记录里两项都为 null
        // 表示「保留当前」，所以只在该图有配置时才换。
        _audio?.EnterMap(mapId);

        var entities = MapLayerRenderer.Build(_map, _loader.PropFiles(), _mapLease, _mapRoot);
        _entities = entities;
        SpawnMapActors(entities);

        _groundLootView = new Inotia4.Rendering.GroundLootView { Name = "GroundLoot", ZIndex = -1 }
            .TextureOf(path => _mapLease.Texture(path))
            .Items(_loader.Items)
            .FlightEffectOf((clip, x, y, startFrame) => EffectSprite.Create(clip, _mapLease!, x, y,
                destroyOnFinish: false)?.StartAt(startFrame));
        _mapRoot.AddChild(_groundLootView);
        _groundLootRevision = -1;

        _pickupMessages = new Inotia4.Rendering.PickupMessages { Name = "PickupMessages" }
            .AnchorOf(id => ActorNodeOf(id) is not { } node
                ? null
                : (new Vec2(node.Position.X, node.Position.Y),
                    node.Definition.HeadAnchor?.X ?? 0f, node.Definition.HeadAnchor?.Y ?? 0f))
            .TextureOf(path => _mapLease.Texture(path))
            .Items(_loader.Items)
            .Presentation(1f / CameraZoom, () =>
            {
                var half = GetViewportRect().Size.X / 2f / CameraZoom;
                var center = GetViewport().GetCamera2D()?.GetScreenCenterPosition() ?? Vector2.Zero;
                return (center.X - half, half * 2f);
            });
        _mapRoot.AddChild(_pickupMessages);

        _lootTally.Clear();
        _lootMoney = 0;
        _lootMissed.Clear();
        _lootMissedSeen.Clear();
        _lootHudUntilMs = 0;

        _feedbackView?.Clear();
        _feedbackView = new Inotia4.Rendering.CombatFeedbackView(
            entities, () => BattleContent, _mapLease, _camera);

        _outcomes?.Clear();
        _outcomes = new Inotia4.Rendering.CombatOutcomeView(
            entities, () => BattleContent, _mapLease);

        if (_player.GetParent() is null) entities.AddChild(_player);
        else _player.Reparent(entities);
        foreach (var ally in _allies)
        {
            ally.Ai.Cancel();
            CombatRuntime.RemoveActor(ally.MemberId);
        }
        _allies.Clear();
        _partySignature = string.Empty;

        _fallen.Clear();
        foreach (var tween in _deathTweens.Values) tween.Kill();
        _deathTweens.Clear();
        _partyDefeatWork.Clear();
        _defeated = false;
        _defeatPending = false;
        _wipeoutPosition = null;
        // 换图/重建等于新建一份世界战斗：锁定目标与受击计数也一并归零（原版世界战斗整体重建），
        // 「打出过伤害」的名单同样重建——原版每张图新建 world-combat，dealtDamage 跟着清空，
        // 否则上一张图里打过人的成员会让新图第一帧就判「全队已交战」，受击计数永远归零。
        _focusTargetId = null;
        _damageCount = 0;
        _dealtDamage.Clear();

        SyncAllies();

        _player.TeleportTo(new Vector2(spawn.X, spawn.Y));
        _player.SetFacing(facing);
        _awaitingInputRelease = true;
        if (_skipMapEntryAutoSave) _skipMapEntryAutoSave = false;
        else _autoSaver.Request(AutoSaveReason.MapEntry, (long)Time.GetTicksMsec());
        PlayFadeIn();

        UpdateCameraLimits();

        if (previousRoot is not null)
        {
            RemoveChild(previousRoot);
            previousRoot.Free();
        }

        previousLease?.Dispose();

        GD.Print($"[world] {_map.Id} {_map.Width}×{_map.Height}，" +
                 $"{_map.Features.Length} 个景物，{_spawnedActors} 个人物" +
                 $"（可见 {_npcs.Count}），贴图 {_textures.Count} 张");

        ClearPlaybacks();
        _playerCombat?.Cancel();
        _player.EndAttack();
        _enemies.Clear();
        _threat?.Clear();
        _summonAwareness = new SummonCombatAwareness(SummonNow);
        RebuildMapEnemies();
        if (_arena) SpawnArena();
        EnsurePlayerCombat();
        ApplyWorldRestore();

        if (!_checkpoint.Available)
        {
            CaptureCheckpoint();
            _checkpointPending = true;
        }

        ResumeStoryFromSave();
    }

    private void ResumeStoryFromSave()
    {
        var cursor = _session.ConsumePendingStoryCursor();
        if (cursor is null || _story.Active || _storyPlaying) return;
        if (cursor.Script.Length == 0 || cursor.Node.Length == 0) return;

        string? encounterId = null;
        var resumed = false;
        try
        {
            resumed = _story.Resume(cursor, out encounterId);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[story] 续播失败：{error.Message}");
        }

        if (!resumed)
        {
            if (cursor.EventId is { Length: > 0 } id)
            {
                _journal.RestartEvent(id);
                GD.Print($"[story] {id} 续不上，放回未触发，等条件重新起演");
            }

            return;
        }

        _storyEventId = cursor.EventId;
        _storyOwnerScript = cursor.Script;
        if (encounterId is { Length: > 0 })
        {
            _waitingEncounter = encounterId;
            GD.Print($"[story] 读档续上遭遇战 {encounterId}，打完再演");
            return;
        }

        GD.Print($"[story] 读档续播 {cursor.Script}:{cursor.Node}");
        ContinueStory();
    }

    private void CaptureCheckpoint()
    {
        if (_map is null) return;
        if (_party is { } living ? living.LivingIds.Count == 0 : _playerHealth <= 0) return;
        _ = Conditions;

        _checkpoint.Capture(_session, new CheckpointLocation(_map.Id, _position.X, _position.Y),
            CaptureWorldState() is { } world
                ? JsonSerializer.Deserialize<WorldSnapshotState>(world.ToJsonString(), SaveGameStore.JsonOptions)!
                : new WorldSnapshotState());
        _checkpointPending = false;
        _checkpointCaptures++;
    }

    public void CheckpointStoryWorld()
    {
        if (_party is { } party ? party.LivingIds.Count == 0 : _playerHealth <= 0) return;
        try
        {
            CaptureCheckpoint();
        }
        catch (Exception error)
        {
            // 回退点没记下来不该毁掉正在演的剧情：这条是从剧情节点提交点（Next()）进来的，
            // 异常逃出去会让整段剧本提前收场——玩家看到的是一段没头没尾的演出。
            GD.PushWarning($"[story] 剧情检查点没记下来：{error}");
        }
    }

    private bool CanCheckpoint()
    {
        if (_defeated || _defeatPanel is { Visible: true } || _pendingDefeats.Count > 0) return false;
        if (_deathTweens.Count > 0) return false;
        if (CombatRuntime.ActionState(PlayerCombatId) != CombatState.Idle) return false;
        foreach (var ally in _allies)
            if (CombatRuntime.ActionState(ally.MemberId) != CombatState.Idle) return false;
        return _party is { } party ? party.LivingIds.Count > 0 : _playerHealth > 0;
    }

    private void MaybeCapturePendingCheckpoint()
    {
        if (!_checkpointPending) return;
        if (_storyPlaying || !CanControl() || !CanCheckpoint()) return;
        CaptureCheckpoint();
    }

    private WorldSnapshotState? _pendingWorldRestore;

    private void ApplyWorldRestore()
    {
        var state = _pendingWorldRestore;
        _pendingWorldRestore = null;
        if (state is null) return;

        CombatSnapshotValidation.Stationaryize(state);

        RestoreSummons(state.Enemies);

        if (state.Conditions is { } conditions) Conditions.RestoreState(conditions);
        if (state.Threat is { } threat) ThreatSystem.RestoreState(threat);

        _focusTargetId = state.FocusTargetId;
        _damageCount = state.DamageCount ?? 0;

        SyncAllies();
        foreach (var saved in state.Party)
        {
            if (saved.Id == PlayerCombatId) continue;
            var ally = _allies.FirstOrDefault(entry => entry.MemberId == saved.Id);
            if (ally is null) continue;
            var landing = NearestSummonSpot(new Vec2((float)saved.X, (float)saved.Y),
                ally.Node.Definition.Collision) ?? new Vec2((float)saved.X, (float)saved.Y);
            ally.Node.TeleportTo(new Vector2(landing.X, landing.Y));
            ally.Node.SetFacing(saved.Facing);
            if (saved.Ai is { } aiState) ally.Ai.RestoreState(aiState);
            if (saved.Fallen || _party is not { } party || !party.Alive(saved.Id))
            {
                ally.Fallen = true;
                MarkAlreadyFallen(saved.Id);
            }
        }

        foreach (var saved in state.Enemies)
        {
            var enemy = _enemies.FirstOrDefault(entry => entry.CombatId == saved.Id);
            if (enemy is null) continue;
            if (saved.X is { } x && saved.Y is { } y) enemy.TeleportTo(new Vector2((float)x, (float)y));
            enemy.SetFacing(saved.Facing);
            if (saved.Health is { } health) enemy.RestoreHealth(health);
            enemy.SummonRemainingMs = saved.SummonRemainingMs;
            if (saved.SummonAi is { } summonState)
            {
                enemy.SummonAi?.RestoreState(summonState);
            }
            else if (saved.EnemyAi is { } enemyState)
            {
                enemy.Ai?.RestoreState(enemyState);
            }
        }

        _summonsByOwner.Clear();
        foreach (var enemy in _enemies)
        {
            if (enemy.SummonedBy is not { Length: > 0 } owner) continue;
            if (!_summonsByOwner.TryGetValue(owner, out var list)) _summonsByOwner[owner] = list = [];
            if (!list.Contains(enemy.CombatId)) list.Add(enemy.CombatId);
        }

        RestoreSceneActors(state.Scene);

        if (state.Audio is { } audio)
        {
            try
            {
                _audio?.RestoreStoryAudio(audio.Music, audio.Ambience);
            }
            catch (InvalidOperationException error)
            {
                GD.PushWarning($"读档音轨不可用：{error.Message}");
            }
        }
    }

    /// <summary>
    /// 把场景快照里的人物摆回存档那一刻：
    /// 位置、朝向、显隐、透明度逐项铺，撤下的（<c>removed</c>）与没有落点的跳过。
    /// 主角不在这里——它的落点由 <c>LoadMap</c> 按存档位置摆好。
    ///
    /// 原版走 <c>movement.placeAll</c> 会顺带挤开重叠的人；Godot 版直接落位
    /// （<see cref="NodePlacement.TeleportTo"/>），重叠由存档那一刻的实际站位决定，
    /// 不会因为读档再挤一次。这是有意保留的已知差异（历史台账记过，文件已删、查 git 历史）。
    /// </summary>
    private void RestoreSceneActors(SceneSnapshotSaved? scene)
    {
        if (scene is null || _map is null || scene.Map != _map.Id) return;

        ActorCensus.AssertUnique(scene.Actors, "剧情舞台");

        var byEntity = _mapActors.ToDictionary(record => record.EntityId);
        foreach (var actor in scene.Actors)
        {
            if (actor.Removed == true || actor.Unpositioned == true) continue;
            if (actor.WorldEntityId is not { Length: > 0 } id || !byEntity.TryGetValue(id, out var record)) continue;

            record.Node.TeleportTo(new Vector2((float)actor.X, (float)actor.Y));
            record.Node.SetFacing(actor.Facing);
            if (actor.Alpha is { } alpha) record.Node.Modulate = new Color(1f, 1f, 1f, (float)alpha);
            record.Node.Visible = actor.Visible;
            // 建图时因为不可见而没进交互表的，这里补回去；
            // 可见性由 UpdateInteraction 每帧再查一次，补进去不会让不画的人可交互。
            if (actor.Visible && !_npcs.Contains(record.Npc)) _npcs.Add(record.Npc);
        }
    }

    private void RestoreSummons(IReadOnlyList<WorldEnemySaved> saved)
    {
        foreach (var entry in saved)
        {
            if (entry.SummonedBy is not { Length: > 0 } owner) continue;
            if (_enemies.Any(enemy => enemy.CombatId == entry.Id)) continue;
            if (string.IsNullOrEmpty(entry.EnemyId)) continue;

            var definition = _loader.Battle.Enemies.FirstOrDefault(enemy => enemy.Id == entry.EnemyId);
            if (definition?.Actor.Collision is null) continue;

            var node = new EnemyNode { Name = entry.Id };
            _entities.AddChild(node);
            node.SetupEnemy(definition!, _textures);
            if (entry.SummonAttributes is { } attributes) node.ApplySummonStats(attributes);
            node.SummonedBy = owner;
            var spot = new Vec2((float)(entry.X ?? 0), (float)(entry.Y ?? 0));
            node.TeleportTo(new Vector2(spot.X, spot.Y));
            node.Home = spot;
            _enemies.Add(node);
            AttachEnemyCombat(node);
            RememberSummonSerial(entry.Id);
        }
    }

    private void RememberSummonSerial(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(id[(dash + 1)..], out var serial))
            _summonSerial = Math.Max(_summonSerial, serial);
    }

    private void LoadMapWithLoading(string mapId, Vec2 spawn, string facing, string label, float progress)
    {
        if (_loadingWorld) return;
        _loadingWorld = true;
        _loading?.Show(label, progress);
        Callable.From(() =>
        {
            _loadingWorld = false;
            try
            {
                LoadMap(mapId, spawn, facing);
            }
            finally
            {
                _loading?.HideOverlay();
            }
        }).CallDeferred();
    }

    private string ResolveClassId()
    {
        var resume = TitleResume ?? ContinueSlot();
        if (resume is { } slot && _saveSlots.Inspect(slot) is SlotReading.Ok ok &&
            ok.Game.Session.ClassId is { Length: > 0 } saved &&
            _loader.StoryClasses.Class(saved) is not null)
            return saved;

        if (TitleClassId is { Length: > 0 } chosen && _loader.StoryClasses.Class(chosen) is not null)
            return chosen;

        if (DebugClassOverride() is { } overridden) return overridden;

        var fallback = _loader.StoryClasses.DefaultClass;
        return fallback.Length > 0 && _loader.StoryClasses.Class(fallback) is not null
            ? fallback
            : FallbackClassId;
    }

    /// <summary>
    /// 调试参数 <c>--class &lt;职业 id&gt;</c>：把开局职业钉到指定值。
    /// 优先级最低（读档与标题页的选择都排在它前面）——它只是自检用的默认值，
    /// 有存档或玩家选了职业时不该覆盖它们。自检要按职业跑技能冒烟（<c>--skillplaycheck</c>），
    /// 每个职业一次进程，职业必须在建角色卡之前定下来（角色卡的 ClassId 是只读的）。
    /// </summary>
    private string? DebugClassOverride()
    {
        var arguments = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(arguments, "--class");
        if (index < 0 || index + 1 >= arguments.Length) return null;
        var classId = arguments[index + 1];
        return _loader.StoryClasses.Class(classId) is not null ? classId : null;
    }

    private static SaveSlotRef? ContinueSlot()
    {
        var arguments = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(arguments, "--continue");
        if (index < 0 || index + 1 >= arguments.Length) return null;
        return SaveSlotRef.TryParse(arguments[index + 1], out var slot) ? slot : null;
    }

    private void SetUpCharacter()
    {
        _hero = new BattleCharacter(_classId, _loader.Stats, _loader.Items);
        if (_session.PlayerLevel > 1) _hero.AddExperience(LevelUpExperience(_session.PlayerLevel));
        _bag = _hero.Bag;

        _session.Bag = _bag;
        _session.StartMap = _loader.Campaign.Start.Map;
        _session.Hero = _hero;

        // 队伍名册：十八格佣兵、两个上阵位、佣兵团技能。
        // 招募中途失败要把整份会话（背包、名册、金币）回滚，所以把事务入口一起交给它。
        _party = new PartyRoster(_hero, _loader.Stats, _loader.Items, _loader.Companions,
            () => { }, onRecruited: ReportRecruit, transaction: action => action());
        _session.Party = _party;

        _skills = new StorySkillProgression(_hero, _loader.Skills);
        _hero.SkillUseHandler = (item, skill) => _skills.ApplyItem(item, skill);
        _session.Skills = _skills;

        _panel = _hero.Panel;
    }

    private void ReportRecruit(string itemId, string memberId)
    {
        GD.Print($"[party] 招募 {memberId}（{itemId}）");
        if (_tracker is null) return;
        foreach (var questId in _tracker.ReportRecruit(itemId))
            GD.Print($"[quest] {questId} 目标达成，可以回去了");
    }

    private int LevelUpExperience(int targetLevel)
    {
        var total = 0;
        for (var level = 1; level < targetLevel && level < _loader.Stats.MaxLevel; level += 1)
            total += _loader.Stats.ExperienceToNext(level) ?? 0;
        return total;
    }

    private void RefreshCharacterFromBag()
    {
        _panel = _hero?.Panel ?? _panel;
        _playerFighter = _panel.ToFighter();
    }

    private void ToggleBag()
    {
        if (_bagPanel is null) { GD.PushWarning("背包面板还没建好"); return; }

        if (_bagPanel.IsOpen)
        {
            _bagPanel.Close();
            return;
        }

        var member = MemberViewOrPlayer(_menuMemberId);
        var bar = member.IsPlayer
            ? _skills?.Bar() ?? ShortcutBar.For(_bag, member.Owner, [])
            : ShortcutBar.For(_bag, member.Owner,
                member.Skills?.ActiveSkills(_loader.Skills, member.ClassId) ?? []);
        var names = _loader.StoryClasses.Names();
        _bagPanel.Open(new BagPanelContext(
            _bag, _loader.Items, _loader.Stats, _loader.Shops, _loader.MenuPanels,
            _loader.Interface.Assets,
            member,
            () => _session.Money,
            names,
            path => _heroLease.Texture(path),
            bar,
            _loader.Skills,
            _loader.ControlSkin,
            PartyMembers.ActiveIds(_party),
            id => PartyMembers.IconOf(_party, _loader.Campaign.Start.Actor, _loader.World,
                _loader.MenuPanels, id),
            SelectMember: SelectMenuMember,
            Changed: RefreshCharacterFromBag,
            ShowMenuPage,
            Party: _party,
            ReviveTarget: () => _party?.ControlledId ?? "player",
            UseRecovery: entryId => UseRecoveryItem(entryId, _menuMemberId),
            Recruited: id =>
            {
                _bagPanel?.Close();
                ShowMenuPage(PartyPanel.Page);
                _partyPanel?.Select(id);
            },
            // 用神灯：关背包页、在主角身边召唤神灯商人 NPC（ 的
            // 0x3e 分支：CHARSYSTEM_Produce(2, 7) + CHAR_AppearFromCharacter——npc-7
            // 绑定功能 6「神灯商店」，玩家走近交互才开店）。
            MerchantSummoned: () =>
            {
                _bagPanel?.Close();
                SummonLampMerchant();
            },
            UiSound: name => _audio?.Ui(name)));
    }

    private async void BeginPageCheck()
    {
        _hold = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        string sample = string.Concat(Enumerable.Repeat(
            "这是一句用来确认对话框会按宽高切页、而不是把后半段裁掉的测试台词，", 6));

        var (pages, width, lines) = await _dialogue.PaginateForCheck(sample);
        var pageLines = _dialogue.LastPageLines.ToList();

        var joined = string.Concat(pages.Select(page => DialoguePager.TypedIndex(page.Text).Text));
        var whole = DialoguePager.TypedIndex(sample).Text;
        var lossless = joined == whole;
        var withinLines = pageLines.All(count => count <= lines);

        GD.Print($"[pagecheck] 正文区宽 {width:0}、每页 {lines} 行；长句 {whole.Length} 字 → {pages.Count} 页，" +
                 $"各页行数 [{string.Join(",", pageLines)}]");
        GD.Print($"[pagecheck] 不丢字={lossless}，每页不超行={withinLines}");

        var (shortPages, _, _) = await _dialogue.PaginateForCheck("好。");
        GD.Print($"[pagecheck] 短句页数={shortPages.Count}（应为 1）");

        var (screenPages, screenWidth, screenLines) = await _dialogue.PaginateForCheck(sample, true);
        GD.Print($"[pagecheck] 全屏叙述：宽 {screenWidth:0}、每页 {screenLines} 行 → {screenPages.Count} 页");

        var ok = pages.Count >= 2 && lossless && withinLines && shortPages.Count == 1 && screenPages.Count >= 1;
        GD.Print($"[pagecheck] 通过={ok}");

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0)
        {
            GD.Print("[pagecheck] 显示测试长句，等截图");
            _ = _dialogue.ShowAsync("测试", sample);
            return;
        }

        _dialogue.Close();
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginQuickCheck()
    {
        _hold = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (_saveButton is null || _settingsButton is null || _hudLayer is null)
        {
            GD.Print("[quickcheck] 左侧按钮没建出来");
            GetTree().Quit(1);
            return;
        }

        var problems = new List<string>();
        var size = GetViewportRect().Size;

        Rect2 RectOf(Control button) =>
            new(button.Position, Vector2.One * SaveIconSize * button.Scale.X);

        var save = RectOf(_saveButton);
        var settings = RectOf(_settingsButton);
        GD.Print($"[quickcheck] 存档钮 {save}，设置钮 {settings}（视口 {size}）");

        foreach (var (label, button, rect) in new[]
                 {
                     ("存档", _saveButton, save), ("设置", _settingsButton, settings),
                 })
        {
            if (!button.Visible) problems.Add($"{label}钮不可见");
            if (rect.Position.X > size.X * 0.05f) problems.Add($"{label}钮没有贴着左缘");
            if (rect.Position.Y < size.Y * 0.1f || rect.End.Y > size.Y * 0.6f)
                problems.Add($"{label}钮纵向不在左侧那条空档里");

            // 中心点必须点得到按钮本身：这一条才说明它没被别的 HUD 压住。
            var center = rect.Position + rect.Size / 2f;
            var topmost = TopmostControlAt(_hudLayer, center);
            GD.Print($"[quickcheck] {label}钮中心 {center} 命中的是 {topmost?.Name ?? "（空）"}");
            if (topmost is not Button) problems.Add($"{label}钮中心点被别的东西挡住了");
        }

        // 设置钮要整块落在存档钮下面，不能压着上一枚（也就不能抢它的点击）。
        if (settings.Position.Y < save.End.Y - 0.5f)
            problems.Add($"设置钮压在存档钮上（{settings.Position.Y:F0} < {save.End.Y:F0}）");
        if (settings.Position.X != save.Position.X)
            problems.Add("两枚钮没有对齐左缘");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[quickcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[quickcheck] 通过：贴着左缘、上下两枚不叠、都点得到");
        GetTree().Quit(0);
    }

    private async void BeginCloseCheck()
    {
        _hold = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        ToggleBag();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var shown = _menuClose?.Visible == true;
        var bagOpen = _bagPanel?.IsOpen == true;
        GD.Print($"[closecheck] 背包打开：关闭钮可见={shown}（应为 True），背包开着={bagOpen}");

        CloseOpenMenuPage();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var hidden = _menuClose?.Visible == false;
        var bagClosed = _bagPanel?.IsOpen != true;
        GD.Print($"[closecheck] 关闭后：关闭钮隐藏={hidden}（应为 True），背包关掉={bagClosed}");

        ToggleSystem(SystemPanel.PageSystem);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var systemShown = _menuClose?.Visible == true;
        var systemRect = _menuClose is null ? default : CloseButtonRect();
        GD.Print($"[closecheck] 系统页：共用钮可见={systemShown}（应为 True），落点={systemRect}");
        var sameSpot = systemRect.Position == new Vector2(MenuCloseX, MenuCloseY);

        CloseOpenMenuPage();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var systemClosed = _systemPanel is { IsOpen: false };
        var closeHidden = _menuClose?.Visible == false;
        GD.Print($"[closecheck] 点叉后：系统页关掉={systemClosed}（应为 True），关闭钮隐藏={closeHidden}（应为 True）");

        var ok = shown && bagOpen && hidden && bagClosed && systemShown && sameSpot &&
                 systemClosed && closeHidden;
        GD.Print($"[closecheck] 通过={ok}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async void BeginUnpackCheck()
    {
        _hold = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var problems = new List<string>();
        var root = ProjectSettings.GlobalizePath("user://game_data");
        var mark = System.IO.Path.Combine(root, ".ready");
        var world = System.IO.Path.Combine(root, "content", "world.json");

        GD.Print($"[unpackcheck] 目录 {root}");
        if (!System.IO.File.Exists(mark)) problems.Add("没有版本标记 .ready");
        if (!System.IO.File.Exists(world)) problems.Add("content/world.json 没解出来");

        var files = System.IO.Directory.Exists(root)
            ? System.IO.Directory.GetFiles(root, "*", System.IO.SearchOption.AllDirectories).Length
            : 0;
        GD.Print($"[unpackcheck] 已解出 {files} 个文件（整包 12200，按需的话应该少一个量级）");
        GD.Print($"[unpackcheck] 解包累计 {ContentRoot.ExtractedFiles} 次、" +
                 $"{ContentRoot.ExtractMicroseconds / 1000.0:F0} 毫秒" +
                 $"（读包 {ContentRoot.ReadMicroseconds / 1000.0:F0}，写盘 {ContentRoot.WriteMicroseconds / 1000.0:F0}）");

        if (Inotia4.Application.PackedContent.Open("res://game_data.zip") is { } pack)
        {
            var t0 = Time.GetTicksUsec();
            for (var i = 0; i < 10; i += 1) pack.Read("assets/ui/status/overhead-frame.png");
            var t1 = Time.GetTicksUsec();
            GD.Print($"[unpackcheck] 读取器：10 次小文件共 {(t1 - t0) / 1000} 毫秒（每次 {(t1 - t0) / 10000.0:F2} 毫秒）");
            pack.Dispose();
        }

        var devRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "public"));
        foreach (var relative in new[]
                 {
                     "content/world.json", "content/items.json",
                     "assets/ui/status/overhead-frame.png",
                 })
        {
            var here = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var there = System.IO.Path.Combine(devRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!System.IO.File.Exists(here) || !System.IO.File.Exists(there)) continue;

            var same = System.IO.File.ReadAllBytes(here)
                .AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(there));
            GD.Print($"[unpackcheck] 字节比对 {relative}：{(same ? "一致" : "不一致")}");
            if (!same) problems.Add($"{relative} 解出来跟源文件不一致");
        }
        if (files <= 0) problems.Add("一个文件都没解出来");
        if (files > 3000) problems.Add($"解出 {files} 个文件，按需解包没生效（像整包解）");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[unpackcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[unpackcheck] 通过：只解了用到的那一小部分");
        GetTree().Quit(0);
    }

    private async void BeginEffectShot(string name)
    {
        for (var frame = 0; frame < 600 && !_worldReady; frame += 1)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (!_loader.Battle.AttackEffects.TryGetValue(name, out var directions) ||
            directions.Clips.Count == 0)
        {
            GD.Print($"[effectshot] 内容里没有特效 {name}（或它没有片段）");
            GetTree().Quit(1);
            return;
        }

        var clip = directions.Clips.TryGetValue("south", out var south)
            ? south
            : directions.Clips.Values.First();

        var effect = EffectSprite.Create(clip, _heroLease, _position.X, _position.Y,
            loop: true, destroyOnFinish: false);
        if (effect is null)
        {
            GD.Print($"[effectshot] 特效 {name} 建不出来");
            GetTree().Quit(1);
            return;
        }

        AddChild(effect);
        GD.Print($"[effectshot] {name}：帧 {clip.FrameWidth}×{clip.FrameHeight}，" +
                 $"{clip.Frames} 帧，采样比例 {EffectSprite.ScaleFor(clip):P0}，" +
                 $"当前贴图 {_textures.Bytes / 1048576.0:F1} MB");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SaveShot();
    }

    private async void BeginTexCheck(int frames)
    {
        if (frames > 0)
            for (var i = 0; i < frames; i += 1)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (i % 300 == 299)
                    GD.Print($"[texcheck] 第 {i + 1} 帧：当前 {_textures.Bytes / 1048576.0:F1} MB，" +
                             $"峰值 {_textures.PeakBytes / 1048576.0:F1} MB");
            }
        else
        {
            _hold = true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var megabytes = _textures.Bytes / 1048576.0;
        GD.Print($"[texcheck] {_map.Id}：贴图 {_textures.Count} 张，" +
                 $"当前 {megabytes:F1} MB，峰值 {_textures.PeakBytes / 1048576.0:F1} MB，" +
                 $"保留像素 {_textures.ImageCount} 张");

        var index = 0;
        foreach (var (name, width, height, bytes) in _textures.Largest(10))
        {
            index += 1;
            GD.Print($"[texcheck] {index,2}. {bytes / 1048576.0,6:F1} MB  {width}×{height}  {name}");
        }

        GD.Print("[texcheck] 注：换装／闪避的合成条带不走这个缓存，未计入");

        if (frames <= 0) GetTree().Quit(0);
    }

    private async void BeginBackCheck()
    {
        _hold = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var problems = new List<string>();

        var quitOnBack = ProjectSettings.GetSetting("application/config/quit_on_go_back", true).AsBool();
        GD.Print($"[backcheck] quit_on_go_back={quitOnBack}（应为 False）");
        if (quitOnBack) problems.Add("quit_on_go_back 还是 true：返回键会直接结束进程");

        ToggleBag();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var bagOpen = _bagPanel?.IsOpen == true;
        _Notification((int)NotificationWMGoBackRequest);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var bagClosed = _bagPanel?.IsOpen != true;
        var noSystem = _systemPanel?.IsOpen != true;
        GD.Print($"[backcheck] 背包开着={bagOpen} → 按返回后 背包关掉={bagClosed}（True）、系统页没冒出来={noSystem}（True）");
        if (!bagOpen) problems.Add("背包没打开，用例没成立");
        if (!bagClosed) problems.Add("返回键没关掉背包");
        if (!noSystem) problems.Add("返回键越过背包直接开了系统页");

        _Notification((int)NotificationWMGoBackRequest);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var systemOpen = _systemPanel?.IsOpen == true;
        GD.Print($"[backcheck] 全关着时按返回 → 系统页打开={systemOpen}（应为 True）");
        if (!systemOpen) problems.Add("什么都没有时按返回没开系统页");

        _Notification((int)NotificationWMGoBackRequest);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var systemClosed = _systemPanel?.IsOpen != true;
        GD.Print($"[backcheck] 系统页开着时再按返回 → 关掉={systemClosed}（应为 True）");
        if (!systemClosed) problems.Add("系统页没被返回键关掉");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[backcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[backcheck] 通过：工程设定对、逐层退、退到底开系统页");
        GetTree().Quit(0);
    }

    private bool MenuOpen() =>
        _bagPanel is { IsOpen: true } || _skillPanel is { IsOpen: true } ||
        _statusPanel is { IsOpen: true } || _questPanel is { IsOpen: true } ||
        _partyPanel is { IsOpen: true } || _systemPanel is { IsOpen: true } ||
        _shopPanel is { IsOpen: true } || _craftPanel is { IsOpen: true };

    private Rect2 CloseButtonRect() =>
        _menuClose is { } root && root.GetChildCount() > 0 && root.GetChild(0) is Control hit
            ? new Rect2(hit.Position, hit.Size)
            : default;

    private void CloseOpenMenuPage() => TryCloseMenuPage();

    private bool TryCloseMenuPage()
    {
        if (_bagPanel is { IsOpen: true }) { _bagPanel.Close(); return true; }
        if (_skillPanel is { IsOpen: true }) { _skillPanel.Close(); return true; }
        if (_statusPanel is { IsOpen: true }) { _statusPanel.Close(); return true; }
        if (_questPanel is { IsOpen: true }) { _questPanel.Close(); return true; }
        if (_partyPanel is { IsOpen: true }) { _partyPanel.Close(); return true; }
        if (_systemPanel is { IsOpen: true }) { _systemPanel.Close(); return true; }
        // 融合器与商人页也是「菜单页」，右上角这个叉对它们必须一样管用。
        // 走各自的 Escape：提示／确认框开着时先收那一层，和按 Esc 是同一条次序。
        if (_craftPanel is { IsOpen: true }) { _craftPanel.Escape(); return true; }
        if (_shopPanel is { IsOpen: true }) { _shopPanel.Escape(); return true; }
        return false;
    }

    /// <summary>
    /// 安卓返回键（引擎的 <c>WM_GO_BACK_REQUEST</c>）。一层一层退，退到底就停在原地。
    ///
    /// 次序与 Esc、右上角那个叉一致：键位编辑器 → 任意菜单页（只退一层）→ 都没开就开系统页。
    /// 退到底**不**退出进程：安卓上一次误触就没了现场，比多点一次菜单难受得多；
    /// 真要离开，系统页里有「返回主菜单」。开系统页前顺手 flush 一次自动存档——
    /// 玩家按返回常常就是要切出去，下一次未必还能活着回来。
    ///
    /// 演出中不响应，和 Esc 的规矩一致；全灭面板开着也不响应（那是结算现场，不该被键盖掉）。
    /// </summary>
    private void HandleBackRequest()
    {
        if (!_worldReady) return;

        if (_layoutEditor is { IsOpen: true })
        {
            _layoutEditor.Cancel();
            return;
        }

        if (_storyPlaying) return;
        if (_defeated || _defeatPanel is { IsOpen: true }) return;

        if (TryCloseMenuPage()) return;

        _autoSaver?.FlushForBackground((long)Time.GetTicksMsec());
        ToggleSystem(SystemPanel.PageSystem);
    }

    /// <summary>
    /// 菜单页签的切换。搬进来的页各有各的面板，
    /// 所以这里是「先把别的页关掉，再开目标页」；还没搬的页先只打印一行。
    /// </summary>
    private void ShowMenuPage(string page)
    {
        if (page != BagPanel.Page && _bagPanel is { IsOpen: true }) _bagPanel.Close();
        if (page != SkillPanel.Page && _skillPanel is { IsOpen: true }) _skillPanel.Close();
        if (page != StatusPanel.Page && _statusPanel is { IsOpen: true }) _statusPanel.Close();
        if (page != QuestPanel.Page && _questPanel is { IsOpen: true }) _questPanel.Close();
        if (page != PartyPanel.Page && _partyPanel is { IsOpen: true }) _partyPanel.Close();
        var systemPages = page is SystemPanel.PageSystem or SystemPanel.PageSave or SystemPanel.PageLoad
            or SystemPanel.PageSettings;
        if (!systemPages && _systemPanel is { IsOpen: true }) _systemPanel.Close();

        switch (page)
        {
            case BagPanel.Page:
                if (_bagPanel is { IsOpen: false }) ToggleBag();
                break;
            case SkillPanel.Page:
                if (_skillPanel is { IsOpen: false }) ToggleSkills();
                break;
            case StatusPanel.Page:
                if (_statusPanel is { IsOpen: false }) ToggleStatus();
                break;
            case QuestPanel.Page:
                if (_questPanel is { IsOpen: false }) ToggleQuests();
                break;
            case PartyPanel.Page:
                if (_partyPanel is { IsOpen: false }) ToggleParty();
                break;
            case SystemPanel.PageSystem:
            case SystemPanel.PageSave:
            case SystemPanel.PageLoad:
            case SystemPanel.PageSettings:
                if (_systemPanel is { IsOpen: true }) _systemPanel.ShowPage(page);
                else ToggleSystem(page);
                break;
            default:
                GD.Print($"[menu] 页签「{page}」暂未实现（见 ARCHITECTURE.md 未完成清单）");
                break;
        }
    }

    private void ToggleSystem(string page)
    {
        _systemPanel.Open(new SystemPanelContext(
            _saveSlots, _loader.MenuPanels, _loader.Interface.Assets,
            path => _heroLease.Texture(path),
            CaptureSaveGame,
            reference => LoadFromSlot(reference, true),
            Saved: reference => _currentSaveSlot = reference,
            Deleted: reference =>
            {
                if (_currentSaveSlot == reference) _currentSaveSlot = null;
            },
            ShowPage: ShowMenuPage,
            CloseRequested: () => _systemPanel.Close(),
            ExitToTitle: () => TitleRequest?.Invoke(),
            Display: _displaySettings,
            Controls: _controlSettings,
            Audio: _audio?.Settings,
            SetAudio: (channel, enabled) => _audio?.SetEnabled(channel, enabled),
            OpenControlEditor: OpenControlEditor,
            Notice: ShowHudNotice), page);
    }

    /// <summary>
    /// 打开键位编辑器：取消未完成的拖动，
    /// 把当前快捷栏喂给预览，再显示这一层。
    ///
    /// 编辑期间菜单本体不画，
    /// 所以这里把系统页整块藏起来，免得底下的按钮还能点到。
    /// </summary>
    private void OpenControlEditor()
    {
        if (_layoutEditor is null) return;
        _systemPanel.SetSuppressed(true);
        _layoutEditor.Open();
    }

    private void CloseControlEditor()
    {
        _systemPanel.SetSuppressed(false);
        if (_systemPanel is { IsOpen: true }) _systemPanel.ShowPage(SystemPanel.PageSettings);
    }

    private void ToggleQuests()
    {
        if (_questPanel is null) { GD.PushWarning("任务面板还没建好"); return; }

        if (_questPanel.IsOpen)
        {
            _questPanel.Close();
            return;
        }

        _questPanel.Open(new QuestPanelContext(
            _loader.Campaign, _journal.Book, _loader.MenuPanels, _loader.Interface.Assets,
            id => _loader.World.Actors.GetValueOrDefault(id)?.Name ?? id,
            path => _heroLease.Texture(path),
            Changed: null,
            ShowMenuPage));
    }

    private void ToggleStatus()
    {
        if (_statusPanel is null || _hero is null)
        {
            GD.PushWarning("人物面板还没建好");
            return;
        }

        if (_statusPanel.IsOpen)
        {
            _statusPanel.Close();
            return;
        }

        var member = MemberViewOrPlayer(_menuMemberId);
        _statusPanel.Open(new StatusPanelContext(
            member, _loader.Stats, _loader.Items, _loader.StoryClasses, _loader.MenuPanels,
            _loader.Interface.Assets,
            allocated => member.IsPlayer
                ? CharacterPanel.Calculate(_loader.Stats, _loader.Items, member.ClassId,
                    member.Level, allocated, _bag.WornInstances)
                : member.Panel,
            path => _heroLease.Texture(path),
            PartyMembers.ActiveIds(_party),
            id => PartyMembers.IconOf(_party, _loader.Campaign.Start.Actor, _loader.World,
                _loader.MenuPanels, id),
            SelectMember: SelectMenuMember,
            Changed: RefreshCharacterFromBag,
            ShowPage: ShowMenuPage,
            CombatModifiers: PartyPassives));
    }

    private PartyMemberView MemberViewOrPlayer(string id)
    {
        var view = PartyMembers.Of(_party, _hero, _loader.Campaign.Start.Actor,
            _loader.World, _loader.StoryClasses, _loader.MenuPanels, id);
        if (view is not null) return view;
        _menuMemberId = "player";
        return PartyMembers.Of(_party, _hero, _loader.Campaign.Start.Actor,
            _loader.World, _loader.StoryClasses, _loader.MenuPanels, "player")!;
    }

    private void SelectMenuMember(string id)
    {
        _menuMemberId = id;
        if (_statusPanel is { IsOpen: true })
        {
            _statusPanel.Close();
            ShowMenuPage(StatusPanel.Page);
        }
        else if (_bagPanel is { IsOpen: true })
        {
            _bagPanel.Close();
            ShowMenuPage(BagPanel.Page);
        }
    }

    private void ToggleSkills()
    {
        if (_skillPanel is null || _skills is null) { GD.PushWarning("技能面板还没建好"); return; }

        if (_skillPanel.IsOpen)
        {
            _skillPanel.Close();
            return;
        }

        var member = MemberViewOrPlayer(_menuMemberId);
        _skillPanel.Open(new SkillPanelContext(
            member, _skills, _loader.Skills, _loader.MenuPanels, _loader.ControlSkin,
            path => _heroLease.Texture(path), _bag,
            PartyMembers.ActiveIds(_party),
            id => PartyMembers.IconOf(_party, _loader.Campaign.Start.Actor, _loader.World,
                _loader.MenuPanels, id),
            Party: _party,
            SelectMember: SelectMenuMember,
            Changed: RefreshCharacterFromBag,
            StoryAssets: _loader.Interface.Assets, ShowPage: ShowMenuPage));
    }

    private void ToggleParty()
    {
        if (_partyPanel is null || _party is null || _skills is null)
        {
            GD.PushWarning("队伍面板还没建好");
            return;
        }

        if (_partyPanel.IsOpen)
        {
            _partyPanel.Close();
            return;
        }

        _partyPanel.Open(new PartyPanelContext(
            _party, _loader.Campaign.Start.Actor, _loader.World,
            _loader.StoryClasses, _loader.Skills, _skills,
            _loader.MenuPanels, _loader.Interface.Assets,
            path => _heroLease.Texture(path),
            Inspect: (id, page) =>
            {
                _menuMemberId = id;
                ShowMenuPage(page);
            },
            ShowPage: ShowMenuPage));
    }

    private bool TakesQuestSlot(MapNpc npc)
    {
        var quest = _journal.SlotFor(npc.ActorId, npc.CharacterId);
        return quest is not null &&
               _journal.StatusOf(quest.Id) is QuestStatus.Available or QuestStatus.Ready;
    }

    private bool IsFusionMachine(string actorId) =>
        _loader.World.Actors.TryGetValue(actorId, out var actor) && actor.Service == "fusion-machine";

    /// <summary>
    /// 用神灯：在主角身边召唤神灯商人 NPC（ 用途 0x3e 的分支：
    /// <c>CHARSYSTEM_Produce(2, 7)</c> 造出 npc-7、<c>CHAR_AppearFromCharacter</c> 让他
    /// 在主角旁边现身）。npc-7 绑定功能 6「神灯商店」，玩家走近交互才开店——
    /// 原版没有任何一张地图摆放他，只能靠这条路召出来。
    /// 已在场就不重复立（原版再按一次只是再耗一盏灯，商人仍是那一个）。
    /// </summary>
    private void SummonLampMerchant()
    {
        if (_npcs.Any(npc => npc.CharacterId == "npc-7")) return;
        if (!_loader.World.Actors.TryGetValue("lamp-dealer-007", out var definition))
        {
            GD.PushWarning("神灯商人的角色定义缺失");
            return;
        }

        const string entityId = "summoned-lamp-dealer";
        var spot = NearestSummonSpot(ControlPosition, definition.Collision)
                   ?? SettleLanding(NavigationShape.Of(definition.Collision), ControlPosition,
                       new NavigationIgnore(PlayerCombatId));

        var actor = new ActorNode { Name = entityId };
        _entities.AddChild(actor);
        actor.Setup(definition, _mapLease!, "south");
        actor.TeleportTo(new Vector2(spot.X, spot.Y));
        _npcs.Add(new MapNpc(actor, entityId, "lamp-dealer-007", "npc-7", definition, OneTime: false));

        EvictPlayerFrom(spot, definition.Collision, definition.Id);

        GD.Print($"[lamp] 神灯商人现身于 ({spot.X:F0}, {spot.Y:F0})");
        UpdateInteraction();
    }

    /// <summary>
    /// 开一家店。：店面按当前地图定级，
    /// 十六个货位全图共用（换图才清），所以这里只把当前地图 id 传进去。
    /// </summary>
    private void OpenShop(string shopId)
    {
        if (_shopPanel is null)
        {
            GD.PushWarning("商店面板还没建好");
            return;
        }

        try
        {
            var store = new Storefront(_loader.Shops, _loader.Items, _loader.Stats, _session, shopId, _map.Id);
            _shopPanel.Open(new ShopPanelContext(store, _loader.Shops, _loader.Items, _loader.Stats,
                _loader.MenuPanels, _session, _classId, _session.PlayerLevel,
                _loader.StoryClasses.NameOf(_classId), _loader.StoryClasses.Names(),
                path => _heroLease.Texture(path), RefreshCharacterFromBag));
            GD.Print($"[shop] {store.Shop.Name}：{store.Offers.Count} 件在架" +
                     (store.Category is { } category ? $"（{category}）" : string.Empty));
        }
        catch (Exception error)
        {
            GD.PushWarning($"商店打开失败：{error.Message}");
        }
    }

    private void OpenCrafting(string actorId)
    {
        if (_craftPanel is null)
        {
            GD.PushWarning("合成面板还没建好");
            return;
        }

        var name = _loader.World.Actors.TryGetValue(actorId, out var actor) && actor.Name.Length > 0
            ? actor.Name
            : actorId;

        var crafting = new EquipmentCrafting(_loader.Crafting, _loader.Items, _session, _bag);
        var fusion = new FusionBench(_loader.Fusion, _loader.Items, _session, _bag);
        _craftPanel.Open(new CraftingPanelContext(crafting, _loader.Items, _loader.Stats, _loader.Shops,
            _loader.MenuPanels, _session, name, _classId, _loader.StoryClasses.NameOf(_classId),
            _session.PlayerLevel, _loader.StoryClasses.Names(), path => _heroLease.Texture(path),
            fusion, Random.Shared.NextDouble, RefreshCharacterFromBag,
            UiSound: name => _audio?.Ui(name)));
        GD.Print($"[craft] {name}：{crafting.Catalog.Operations.Count} 档混沌 + " +
                 $"{fusion.Catalog.Recipes.Length} 条配方（金币 {_session.Money}）");
    }

    private void PrintPanel()    {
        var bare = CharacterPanel.Calculate(_loader.Stats, _loader.Items, _classId, 1);

        foreach (var (slot, entry) in _bag.WornInstances)
            GD.Print($"[gear] {slot}: {_loader.Items.Get(entry.ItemId).Name}" +
                     $"（+{entry.Enhancement}，词条 {entry.Numbers?.Modifiers.Count ?? 0} 条，" +
                     $"孔 {entry.Numbers?.Sockets ?? 0}）");

        Describe("徒手", bare);
        Describe("装备后", _panel);
        return;

        void Describe(string label, CharacterPanel panel) =>
            GD.Print($"[panel] {_classId} 1 级（{label}，武器 {panel.WeaponKind}）：" +
                     $"攻 {panel.PhysicalAttack}，魔攻 {panel.MagicAttack}，血 {panel.MaxHealth}，" +
                     $"防 {panel.BaseDefense}，物理减伤 {panel.PhysicalResistance}‰，" +
                     $"命中 {panel.HitRate}‰，回避 {panel.EvasionRate}‰，" +
                     $"暴击 {panel.CriticalRate}‰，盾挡 {(panel.OffHandShield ? panel.ShieldBlock : 0)}‰");
    }

    private void SetUpPlayerCombat()
    {
        _playerFighter = _panel.ToFighter();
        SetPlayerHealth(_playerHealth);

        GD.Print($"[arena] 主角 {_playerFighter.Level} 级：血 {_playerFighter.MaxHealth}，" +
                 $"攻 {_playerFighter.PhysicalAttack}，减伤 {_playerFighter.PhysicalResistance}‰，" +
                 $"命中 {_playerFighter.HitBase}+{_playerFighter.HitBonus}，暴击 {_playerFighter.CriticalRate}‰");
    }

    private void SpawnArena()
    {
        if (_battle is null) return;

        var definition = _battle.Enemies.FirstOrDefault(enemy => enemy.Id == "enemy-bat-1")
                         ?? _battle.Enemies.First(enemy => enemy.MaxHealth > 1);

        var position = new Vec2(_position.X + 48f, _position.Y);
        if (!_grid.CanStandOnTerrain(position.X, position.Y)) position = _position;

        var enemy = new EnemyNode { Name = $"Enemy-{definition.Id}" };
        _entities.AddChild(enemy);
        enemy.SetupEnemy(definition, _textures);
        enemy.TeleportTo(new Vector2(position.X, position.Y));
        enemy.Home = position;
        AttachEnemyCombat(enemy);
        _enemies.Add(enemy);

        GD.Print($"[arena] 放了 {_enemies.Count} 只 {definition.Name}，空格或 J 攻击");
    }

    private void UpdateCombat(float seconds)
    {
        _pathBudget.Reset();

        var held = Input.IsKeyPressed(Key.Space) || Input.IsKeyPressed(Key.J);
        if (held && !_attackHeld)
        {
            _autoAttack.Engage();
            PlayerAttack();
        }
        _attackHeld = held;

        // 战斗逻辑按 60 毫秒一帧推进（原版世界战斗同一个拍子）：
        // 敌人的出手决策、动作帧、命中派发都排在逻辑帧上，和渲染帧率解耦。
        // 卡顿时最多补 4 帧，避免一次掉帧把伤害结算叠成一串；注意时钟本身也只走
        // 这 4 帧——运行时要求「帧号逐个加一」，时钟跳远了两边的拍子就对不上。
        // 时钟、任务物品生成与冷却都在会话里。
        var deltaMs = Math.Min((long)(seconds * 1000f), 4L * CombatClock.LogicFrameMs);
        _session.AdvanceCombatFrames(deltaMs, frame =>
        {
            Conditions.Update(CombatClock.LogicFrameMs, ApplyPeriodicTick);
            // 锁定的目标不可瞄准（隐身等）就撒手。
            if (_focusTargetId is { } focus && !Conditions.CanTarget(focus)) _focusTargetId = null;
            foreach (var id in ActivePartySlots())
            {
                ResourceCapacityOf(id, "maxHealth");
                ResourceCapacityOf(id, "maxMana");
            }
            // 原版在同一个共有帧里依次调用所有角色的 step()，各自推进自己的决策时钟。
            // 召唤物的决策要用别的动作做完之后的观察，所以拆成两批：
            // 先普通敌人，再记录一轮交战观察，最后才轮到召唤物。
            // 伙伴更早： 里先跑成员生死账，再跑 party AI 的 step。
            foreach (var ally in _allies.ToList())
            {
                var alive = _party?.Alive(ally.MemberId) ?? true;
                if (alive && ally.Fallen) ReviveMember(ally.MemberId);
                else if (!alive && !ally.Fallen) MemberDefeated(ally.MemberId);
            }
            if (_playerHealth > 0 && _fallen.Contains(PlayerCombatId)) ReviveMember(PlayerCombatId);
            else if (_playerHealth <= 0 && !_fallen.Contains(PlayerCombatId) && !_hits.IsAwaitingDefeat(PlayerCombatId))
                MemberDefeated(PlayerCombatId);
            if (ControlledId == PlayerCombatId) _playerCombat?.Update();
            else _allies.FirstOrDefault(ally => ally.MemberId == ControlledId)?.Ai.CombatController.Update();
            foreach (var ally in _allies)
            {
                if (ally.MemberId == ControlledId) continue;
                if (_party?.Alive(ally.MemberId) ?? true) ally.Ai.Step();
            }
            if (ControlledId != PlayerCombatId && _playerHealth > 0) _heroAi?.Step();
            foreach (var enemy in _enemies.ToList())
            {
                if (enemy.Dead) continue;
                if (enemy.SummonAi is null) enemy.Ai?.Step();
            }

            if (PartyFullyEngaged()) _damageCount = 0;

            ObserveSummonCombat();

            foreach (var enemy in _enemies.ToList())
            {
                if (enemy.Dead) continue;
                enemy.SummonAi?.Step();
            }

            foreach (var enemy in _enemies.ToList())
            {
                if (enemy.Dead || enemy.SummonRemainingMs is not { } remaining) continue;
                remaining -= CombatClock.LogicFrameMs;
                if (remaining > 0)
                {
                    enemy.SummonRemainingMs = remaining;
                    continue;
                }

                enemy.SummonRemainingMs = null;
                if (_summonsByOwner.TryGetValue(enemy.SummonedBy ?? "", out var list)) list.Remove(enemy.CombatId);
                CancelEnemyAction(enemy);
                _enemies.Remove(enemy);
                ForgetFocusedEnemy(enemy.CombatId);
                enemy.QueueFree();
            }

            _combatRuntime?.Step(frame);
            // 这一帧跑完还该不该继续补帧：原版在这里读「倒下 / 关闭 / 操控权被拿走」，
            // 返回 false 就停下，剩下的毫秒也不计入时钟余数。Godot 版的这两个条件
            // 由外层（只在可操控时调用）保证，这里如实返回「继续」。
            return true;
        });

        RecoverResources();

        if (_arena && _playerHealth <= 0) ResetArena();
    }

    private void ResetCombatRuntimeForRestore()
    {
        _combatRuntime?.Dispose();
        _combatRuntime = null;

        // 主角控制器与托管 AI 都登记在**旧的那份**运行时上（原版整体重建 world-combat 时
        // 连 heroAi 一起重建）。运行时 Dispose 会清空控制器表，而 EnsurePlayerCombat 见到
        // _playerCombat 非空就早退——不在这里一并作废的话，新运行时里根本没有主角，
        // 按攻击键只会拿到 "no-target"，表现就是读档/回检查点复活后不能普攻。
        // 攻击规则也一并丢掉：回退到检查点后手里的武器可能不是死前那一把。
        _playerCombat = null;
        _playerAttack = null;
        _heroAi = null;
    }

    /// <summary>
    /// 撤掉一只怪身上正在播放的动作。怪被击杀、被剧情撤下或名册离场时都要清，
    /// 否则控制器会停在 attacking，下一次决策也接不上。
    /// </summary>
    private void CancelEnemyAction(EnemyNode enemy)
    {
        enemy.Ai?.Cancel();
        enemy.SummonAi?.Cancel();
        CombatRuntime.Cancel(enemy.CombatId);
        if (GodotObject.IsInstanceValid(enemy)) enemy.EndAttack();
    }

    private void ClearPlaybacks()
    {
        foreach (var enemy in _enemies)
        {
            enemy.Ai?.Cancel();
            enemy.SummonAi?.Cancel();
            CombatRuntime.Cancel(enemy.CombatId);
        }

        _playerCombat?.Cancel();
        _player.EndAttack();
    }

    private void UpdateStoryEncounter()
    {
        if (_waitingEncounter is not { Length: > 0 } id) return;

        var encounter = _session.StoryWorld.Encounter(id);
        if (encounter is null || encounter.Status != "active") return;
        if (encounter.Map != _map.Id) return;

        if (_playerHealth <= 0)
        {
            ResolveStoryEncounter(false);
            return;
        }

        if (_mapRoster?.CompletedEncounters.Any(completed => completed.Id == id) == true)
            ResolveStoryEncounter(true);
    }

    private void ResolveStoryEncounter(bool won)
    {
        var id = _waitingEncounter;
        if (id is null) return;
        _waitingEncounter = null;

        GD.Print($"[encounter] {id} {(won ? "胜利" : "败北")}");

        if (!_story.ResolveEncounter(won))
        {
            // 导演那边已经不在这场遭遇上了：等待期间被别的剧本 Start 顶掉（Start 会清掉
            // pending），或者读档没把等待游标续上。结果照样要落进剧情世界——否则这场
            // 遭遇永远停在 active，名册每次进图都把它算成「在场」，而它那几只怪早已
            // 永久死亡，玩家只会看到一场空，剧情也再没有机会往下走。
            if (_session.StoryWorld.Encounter(id) is { Status: "active" })
            {
                _session.StoryWorld.Apply(new EncounterResultCommand(id, won ? "won" : "lost"));
                GD.Print($"[encounter] {id} 的结果没有剧本可接，直接落进剧情世界");
            }

            GD.Print($"[story] 遭遇 {id} 没有 {(won ? "won" : "lost")} 分支，剧本结束");
            FinishStory(_storyOwnerScript);
            _storyOwnerScript = null;
            return;
        }

        _storyResumeQueued = true;
        Callable.From(ContinueStory).CallDeferred();
    }

    /// <summary>
    /// 一次普攻输入。对应无参分支：
    /// 交给控制器判断能不能出手、打谁、要不要先追近，再由运行时按逻辑帧推进动作。
    /// 冷却不再是场景里的固定毫秒数——节奏由普攻规则的有效周期帧数与攻速决定。
    /// </summary>
    /// <summary>
    /// 站定就接下一击（原版的 <c>autoAttack.next(...)</c> 一段）。
    ///
    /// 原版是「摇杆为空 且 动作回到 idle 且 还选着目标」就再发起一次默认攻击；
    /// 这版多了三条自动走位通道（点地寻路、战斗追近、托管跟随），正在走的时候
    /// 不该被一次普攻打断，所以先确认它们都空着，才是真的「站着」。
    /// </summary>
    private bool StandingAutoAttack()
    {
        if (_route is not null || _combatApproach is not null || _heroFollow is not null) return false;
        if (!_autoAttack.Next(CombatRuntime.SelectedTargetId(ControlledId), CombatRuntime.ActionState(ControlledId)))
            return false;
        PlayerAttack();
        return true;
    }

    private void PlayerAttack()
    {
        if (!CanControl()) return;
        EnsurePlayerCombat();
        CombatRuntime.Request(ControlledId);
    }

    private void MoveEnemies(float seconds)
    {
        foreach (var enemy in _enemies)
        {
            if (enemy.Dead) continue;
            MoveEnemy(enemy, seconds);
        }
    }

    private void MoveEnemy(EnemyNode enemy, float seconds)
    {
        if (!Conditions.CanMove(enemy.CombatId)) return;
        var target = enemy.NextMotionTarget();
        if (target is null)
        {
            enemy.StepMotion(Vec2.Zero, false, enemy.EnemyInfo.Behavior.CanTurn);
            return;
        }

        var from = new Vec2(enemy.Position.X, enemy.Position.Y);
        var delta = target.Value - from;
        var distance = delta.Length;
        if (distance <= 0.001f)
        {
            enemy.StepMotion(Vec2.Zero, false, enemy.EnemyInfo.Behavior.CanTurn);
            return;
        }

        var direction = new Vec2(delta.X / distance, delta.Y / distance);
        var speed = MovementSpeed(enemy.CombatId, enemy.EnemyInfo.Behavior.MoveSpeedPxPerSecond);
        var stepDistance = MathF.Min(distance, speed * seconds);
        var step = MovementSolver.Step(_grid, from, direction * stepDistance, 4f);

        if (step.X != 0f || step.Y != 0f)
        {
            enemy.Position = new Vector2(from.X + step.X, from.Y + step.Y);
            enemy.StepMotion(direction, true, enemy.EnemyInfo.Behavior.CanTurn);
            return;
        }

        enemy.StepMotion(direction, false, enemy.EnemyInfo.Behavior.CanTurn);
    }

    private void StepEnemyOnce(EnemyNode enemy)
    {
        enemy.Ai?.Step();
        MoveEnemy(enemy, 1f / 60f);
    }

    private static string HitKindName(HitKind kind) => kind switch
    {
        HitKind.Miss => "miss",
        HitKind.Critical => "critical",
        HitKind.Hit => "hit",
        HitKind.ShieldBlock => "shield-block",
        HitKind.WeaponBlock => "weapon-block",
        _ => "hit",
    };

    private void SettleEnemyContact(EnemyNode enemy, string targetId, string hand) =>
        SettleBasicAttackContact(enemy.CombatId, targetId, hand, enemy.Attack);

    private void SettleHit(string casterId, string targetId, HitResult hit,
        string style = "weapon", bool periodic = false, bool sound = true)
    {
        RecordSummonContact(casterId, targetId);
        if (FindEnemy(targetId) is { } hitEnemy && !hitEnemy.Dead)
        {
            hitEnemy.Ai?.OnContact(casterId, targetId);
            hitEnemy.SummonAi?.OnContact(new AttackContact(casterId, targetId));
        }
        if (_allies.FirstOrDefault(ally => ally.MemberId == targetId) is { } hitAlly)
            hitAlly.Ai.OnContact(new AttackContact(casterId, targetId));

        if (!Conditions.CanLoseHealth(targetId)) return;

        if (hit.Damage != 0)
        {
            if (hit.Damage > 0) Conditions.RemoveState(targetId, "sleep");

            if (targetId == PlayerCombatId)
            {
                var player = _hero?.Player;
                if (player is null) return;
                var wasAlive = player.Value.Health > 0;
                SetPlayerHealth(player.Value.Health - hit.Damage);
                if (wasAlive && _playerHealth == 0)
                    _hits.DeferDefeat(targetId, () => { if (_playerHealth == 0) MemberDefeated(PlayerCombatId); });
            }
            else if (_party?.Get(targetId) is { } hitMember)
            {
                hitMember.SetHealth(hitMember.Player.Health - hit.Damage);
                // 伙伴也要在这一击结算完就地倒下，不能等下一帧的生死账。
                if (hitMember.Player.Health == 0 && !_fallen.Contains(targetId))
                    _hits.DeferDefeat(targetId, () => { if (MemberHealth(targetId) == 0) MemberDefeated(targetId); });
            }
            else
            {
                var target = FindEnemy(targetId);
                if (target is null || target.Dead) return;
                if (DamageEnemy(target, hit.Damage)) return;
                _hits.DeferDefeat(targetId, () =>
                {
                    if (target.Dead) KillEnemy(target, casterId);
                });
            }

            if (hit.Damage > 0)
            {
                _dealtDamage.Add(casterId);
                _threat?.Damage(casterId, targetId, hit.Damage);
            }
        }

        if (sound)
        {
            if (casterId == PlayerCombatId)
                _audio?.PlayerHit(_panel.WeaponKind, HitKindName(hit.Kind),
                    FindEnemy(targetId)?.EnemyInfo.Id);
            if (hit.Kind == HitKind.ShieldBlock && targetId == PlayerCombatId) _audio?.ShieldBlock();
        }

        if (hit.Damage > 0 && casterId != PlayerCombatId)
        {
            var attacker = FindEnemy(casterId);
            if (attacker is not null) _audio?.MonsterAttack(attacker.EnemyInfo.Id);
        }

        ShowHitFeedback(hit, targetId, casterId == PlayerCombatId, style, periodic);
    }

    private void ApplyPeriodicTick(PeriodicTick tick)
    {
        if (_hits.IsAwaitingDefeat(tick.TargetId)) return;
        var attributes = ActorAttributesOf(tick.SourceId);
        if (attributes is null && tick.Effect is not (NormalDamageTick or VisualTick or FixedDamageTick)) return;

        var source = attributes is { } values ? new PeriodicSource
        {
            ClassId = tick.SourceId == PlayerCombatId ? _classId : _party?.Get(tick.SourceId)?.ClassId,
            Attributes = new CombatActorAttributes
            {
                PhysicalAttack = values.PhysicalAttack,
                MagicAttack = values.MagicAttack,
                PhysicalDamageRate = values.PhysicalDamageRate,
                MagicDamageRate = values.MagicDamageRate,
                StealthDamageRate = values.StealthDamageRate,
                MaxHealth = values.MaxHealth,
                PrimaryAttributes = values.Primary,
            },
            LifeFeastRate = AttributeOf(tick.SourceId, "lifeFeastRate"),
            AttributeValue = key => AttributeOf(tick.SourceId, key),
        } : null;

        var commands = CombatPeriodic.Resolve(tick.Effect, new PeriodicContext
        {
            BuffId = tick.BuffId,
            StackCount = tick.StackCount,
            Source = source,
            TargetHealingRate = AttributeOf(tick.TargetId, "healingPowerRate"),
        });

        void Heal(string id, HealCommand command)
        {
            RestoreHealth(id, command.Amount);
            if (command.Effect is { } effect) ShowAttachedEffect(effect, id);
        }

        foreach (var command in commands)
        {
            if (command is PeriodicVisualCommand visual) ShowAttachedEffect(visual.EffectName, tick.TargetId);
            else if (command.Amount > 0)
            {
                switch (command)
                {
                    case HealTargetCommand heal: Heal(tick.TargetId, heal); break;
                    case HealSourceCommand heal: Heal(tick.SourceId, heal); break;
                    case HealPartyCommand heal:
                        foreach (var id in ActivePartySlots()) Heal(id, heal);
                        break;
                    case DamageTargetCommand damage:
                        SettleHit(tick.SourceId, tick.TargetId, new HitResult(HitKind.Hit, damage.Amount), periodic: true);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 一次命中之后的有序反应链（ →
    /// <see cref="CombatReactionPipeline.Resolve"/>）。
    /// 每一步的先后都不能挪：骰子按次序消耗。
    /// </summary>
    private void ResolveHitReactions(AttackContact contact, HitResult hit, string damageKind,
        string actionId, Vec2? targetPosition) =>
        CombatReactionPipeline.Resolve(contact, hit, damageKind, actionId,
            new WorldReactionPort(this), targetPosition);

    private (int PhysicalAttack, int MagicAttack, int PhysicalDamageRate, int MagicDamageRate,
        int StealthDamageRate, int MaxHealth, PrimaryAttributes Primary)? ActorAttributesOf(string id)
    {
        var isPlayer = id == PlayerCombatId;
        var member = isPlayer ? null : _party?.Get(id);
        var enemy = isPlayer || member is not null ? null : FindEnemy(id);
        if (!isPlayer && member is null && enemy is null) return null;

        var passives = PassivesOf(id);
        int Attribute(string key, int original) => Math.Max(0, Conditions.Attribute(id, key, original, passives));
        var fighter = isPlayer ? _playerFighter : member is not null ? member.Panel.ToFighter() : enemy!.Fighter;
        var panel = isPlayer ? _panel : member?.Panel;

        return (
            Attribute("physicalAttack", panel?.PhysicalAttack ?? fighter.PhysicalAttack),
            Attribute("magicAttack", panel?.MagicAttack ?? fighter.MagicAttack),
            Attribute("physicalDamageRate", 0),
            Attribute("magicDamageRate", 0),
            Conditions.Attribute(id, "stealthAttackEnabled", 0, passives) > 0
                ? Conditions.Attribute(id, "stealthDamageRate", 0, passives)
                : 0,
            Attribute("maxHealth", panel?.MaxHealth ?? fighter.MaxHealth),
            panel?.Attributes ?? new PrimaryAttributes());
    }

    private static int DamageSourceValue(
        (int PhysicalAttack, int MagicAttack, int PhysicalDamageRate, int MagicDamageRate,
            int StealthDamageRate, int MaxHealth, PrimaryAttributes Primary) attributes, string source) =>
        source switch
        {
            "physicalAttack" => attributes.PhysicalAttack,
            "magicAttack" => attributes.MagicAttack,
            "maxHealth" => attributes.MaxHealth,
            "bestNormalDamage" => DamageFormula.BestNormalDamage(
                attributes.PhysicalAttack, attributes.PhysicalDamageRate, attributes.StealthDamageRate,
                attributes.MagicAttack, attributes.MagicDamageRate),
            _ => 0,
        };

    private int RearPenetrationBonus(string attackerId, string targetId)
    {
        var rate = AttributeOf(attackerId, "rearPenetrationRate");
        if (rate <= 0) return 0;
        return CurrentTargetOf(targetId) == attackerId ? 0 : rate;
    }

    private string? CurrentTargetOf(string id)
    {
        if (id == PlayerCombatId) return _playerCombat?.SelectedTargetId;
        return FindEnemy(id)?.Ai?.CurrentTargetId();
    }

    private void React(SkillReactionEvent reaction)
    {
        void Apply(SkillReactionCommand command) => ApplyReactionCommand(command, reaction.OwnerId);

        if (reaction is BloodThirstEvent thirst)
        {
            SkillReactions.ExecuteBloodThirst(thirst, _random.NextDouble,
                () => AttributeOf(thirst.OwnerId, "bloodThirstLifeDrainPerThousand"),
                () => AttributeOf(thirst.OwnerId, "bloodThirstManaRecovery"),
                Apply);
            return;
        }

        IReadOnlyList<SkillReaction> reactions = [];
        if (reaction is not PhysicalAttackEffectsEvent and not PhysicalRevengeEvent
            and not PhysicalInstinctEvent and not PhysicalDefenseInstinctEvent)
        {
            var member = _party?.Get(reaction.OwnerId);
            SkillConfiguration? configuration = reaction.OwnerId == PlayerCombatId ? _skills : member?.Skills;
            var classId = reaction.OwnerId == PlayerCombatId ? _classId : member?.ClassId;
            if (configuration is null || classId is null) return;
            reactions = SkillReactions.LearnedReactions(_loader.Skills, classId,
                configuration.Draft(_loader.Skills, classId), companion: reaction.OwnerId != PlayerCombatId);
        }

        var commands = SkillReactions.Resolve(reactions, reaction,
            new FixedRandomPorts(_random.NextDouble, (owner, skill) => LearnedReactionBuff(owner, skill)));
        foreach (var command in commands) Apply(command);
    }

    private void ReactToBuff(BuffReactionEvent reaction)
    {
        foreach (var command in BuffReactions.Resolve(Conditions.BuffReactions(reaction.OwnerId), reaction))
        {
            switch (command)
            {
                case ApplyBuffReactionCommand buff:
                    ApplyActorBuff(buff.TargetId, buff.Buff.Key, buff.Buff.RankPriority, buff.Buff.Effect,
                        buff.SourceId);
                    break;

                case RestoreBuffManaCommand mana:
                    RestoreMana(mana.TargetId, mana.Amount);
                    break;

                case PlayBuffReactionEffectCommand effect:
                    ShowAttachedEffect(effect.Effect, effect.TargetId);
                    break;

                case RedistributeThreatCommand redistribute:
                    _threat?.RedistributeAfterPhysicalHit(redistribute.SourceId, redistribute.TargetId,
                        redistribute.Damage);
                    break;

                case ApplyBuffStateCommand state:
                    ApplyReactionState(state.AttributeOwner, state.TargetId, state.State, state.DurationAttribute);
                    break;
            }
        }
    }

    private void ApplyReactionState(string attributeOwner, string targetId, string state, string durationAttribute)
    {
        var duration = AttributeOf(attributeOwner, durationAttribute);
        if (duration <= 0) return;

        var request = StateApplicationRequestOf(attributeOwner, targetId, state);
        var decision = StateApplication.Resolve(request, _random.NextDouble);
        if (!decision.Allowed) return;

        var normal = (int)Math.Truncate(duration * 15 / 10.0);
        var shortened = (int)Math.Truncate((duration >> 1) * 15 / 10.0);
        Conditions.ApplyStateFrames(targetId, state,
            decision.DurationScale == 0.5 ? shortened : normal, attributeOwner);
        ApplyStateConsequences(targetId, [state]);
    }

    private void ApplyReactionCommand(SkillReactionCommand command, string ownerId)
    {
        switch (command)
        {
            case RestoreReactionHealthCommand restore:
                RestoreHealth(restore.TargetId, restore.Amount);
                break;

            case RestoreReactionManaCommand mana:
                RestoreMana(mana.TargetId, mana.Amount);
                break;

            case PlayReactionEffectCommand effect:
                ShowAttachedEffect(effect.Effect, effect.TargetId);
                break;

            case ResetReactionCooldownsCommand reset:
                // 技能冷却表还没接到世界（技能本身还不能施放），此刻没有可重置的东西。
                _ = reset;
                break;

            case ApplyPartyBuffCommand party:
                foreach (var id in ActivePartySlots())
                    ApplyActorBuff(id, party.Buff.Key, party.Buff.RankPriority, party.Buff.Effect, party.SourceId);
                break;

            case ApplyReactionBuffCommand buff:
                ApplyActorBuff(buff.TargetId, buff.Buff.Key, buff.Buff.RankPriority, buff.Buff.Effect, buff.SourceId);
                break;
        }
    }

    private void ShowAttachedEffect(string effect, string targetId)
    {
        var node = ResolveActorNode(targetId);
        var clip = BattleContent.EffectClip(effect, "default");
        if (node is null || clip is null) return;

        var anchor = node.Definition.EffectAnchor;
        var sprite = EffectSprite.Create(clip, _mapLease!, anchor?.X ?? 0f, anchor?.Y ?? 0f);
        if (sprite is null) return;
        sprite.ZIndex = 1;
        node.AddChild(sprite);
    }

    private IReadOnlyList<string> ActivePartySlots() => _party?.ActiveIds ?? [PlayerCombatId];

    private bool ApplyActorBuff(string targetId, string key, int priority, BattleBuffEffect effect, string sourceId)
    {
        var enemy = FindEnemy(targetId);
        if (enemy is not null && enemy.EnemyInfo.AcceptsBuffs == false) return false;

        var applied = effect;
        if (effect.DurationRateFromOwner is { } rate)
        {
            var frames = (int)Math.Truncate(rate.BaseFrames * (1000.0 + AttributeOf(sourceId, rate.Attribute)) / 1000);
            applied = WithLifetimeFrames(applied, frames);
        }

        if (applied.DurationFramesFromOwner is { } bonus && applied.SimulationFrames is not null)
        {
            var frames = (int)Math.Truncate(AttributeOf(sourceId, bonus.Attribute) * 15 / 10.0);
            applied = WithLifetimeFrames(applied, applied.SimulationFrames.LastActiveFrame + frames);
        }

        var initialized = new List<string>();
        var accepted = Conditions.ApplyBuff(targetId, key, priority, applied, sourceId, state =>
        {
            var decision = StateApplication.Resolve(StateApplicationRequestOf(sourceId, targetId, state),
                _random.NextDouble);
            if (!decision.Allowed) return null;

            var profile = applied.StateDurationProfiles?.GetValueOrDefault(state);
            if (profile is null) throw new InvalidOperationException($"增益缺少状态寿命：{key}/{state}");
            initialized.Add(state);
            return StateApplication.SelectStateDurationProfile(profile, decision);
        });
        if (!accepted) return false;

        ApplyAcceptedBuffEffects(sourceId, targetId, applied);

        if (targetId == PlayerCombatId || _party?.Get(targetId) is not null)
        {
            ResourceCapacityOf(targetId, "maxHealth");
            ResourceCapacityOf(targetId, "maxMana");
        }

        ApplyStateConsequences(targetId,
            initialized.Where(state => Conditions.HasState(targetId, state)).ToList());
        return true;
    }

    private void ApplyAcceptedBuffEffects(string sourceId, string targetId, BattleBuffEffect effect)
    {
        foreach (var accepted in effect.OnAccepted ?? [])
        {
            var child = LearnedReactionBuff(sourceId, accepted.Skill);
            if (child is null) continue;

            BattleBuffEffect inherited = child.Effect;
            if (accepted.Skill is "bard-of-destruction" or "deep-breath")
            {
                var parentKey = accepted.Skill == "bard-of-destruction" ? "summon-bard" : "energy-bodhi";
                if (Conditions.BuffLifetimeFrames(sourceId, parentKey) is { } parentCap)
                    inherited = WithLifetimeFrames(child.Effect, parentCap);
            }

            var recipient = accepted.Target == "source" ? sourceId : targetId;
            ApplyActorBuff(recipient, child.Key, child.RankPriority, inherited, sourceId);
        }
    }

    private static BattleBuffEffect WithLifetimeFrames(BattleBuffEffect effect, int lastActiveFrame) =>
        effect.Copy(
            durationMs: (lastActiveFrame + 1) * CombatClock.LogicFrameMs,
            simulationFrames: new BuffSimulationFrames
            {
                LastActiveFrame = lastActiveFrame,
                PulseIntervalFrames = effect.SimulationFrames?.PulseIntervalFrames,
                PulseAfterAgeIncrement = effect.SimulationFrames?.PulseAfterAgeIncrement,
            });

    private StateApplicationRequest StateApplicationRequestOf(string sourceId, string targetId, string state)
    {
        var sourceIsPlayer = sourceId == PlayerCombatId;
        var targetIsPlayer = targetId == PlayerCombatId;
        var sourceExists = sourceIsPlayer || FindEnemy(sourceId) is not null;
        var targetExists = targetIsPlayer || FindEnemy(targetId) is not null;

        var sourceParty = sourceIsPlayer;
        var targetParty = targetIsPlayer;
        var equipped = targetIsPlayer ? EquippedInstances(targetId) : [];
        var immunity = EquipmentAttributes.StateImmunity(equipped, state);
        var immunityAttribute = state == "sleep" ? "sleepImmunity" : state == "stun" ? "stunImmunity" : null;
        var passives = PassivesOf(targetId);

        return new StateApplicationRequest
        {
            State = state,
            Kind = "buff",
            SourceRelation = !sourceExists || !targetExists
                ? "none"
                : sourceParty == targetParty ? "friendly" : "hostile",
            Rules = targetIsPlayer ? null : FindEnemy(targetId)?.EnemyInfo.StateRules,
            SpecificImmunityValue = immunityAttribute is null
                ? 0
                : Conditions.Attribute(targetId, immunityAttribute, immunity, passives),
            ResistanceThreshold = targetIsPlayer
                ? 0
                : Conditions.Attribute(targetId, "stateResistance", 0, passives),
        };
    }

    private void ApplyStateConsequences(string targetId, IReadOnlyList<string> states)
    {
        var disables = false;
        foreach (var state in states)
            if (state is "immobilized" or "stun" or "sleep") disables = true;

        if (!disables) return;
        if (targetId == PlayerCombatId)
        {
            _playerCombat?.Cancel();
            return;
        }

        var enemy = FindEnemy(targetId);
        if (enemy is not null) CancelEnemyAction(enemy);
    }

    private SkillReactionBuff? LearnedReactionBuff(string ownerId, string skillId)
    {
        var catalog = _loader.Skills;
        var member = _party?.Get(ownerId);
        SkillConfiguration? configuration = ownerId == PlayerCombatId ? _skills : member?.Skills;
        var classId = ownerId == PlayerCombatId ? _classId : member?.ClassId;
        if (configuration is null || classId is null || !catalog.Skills.ContainsKey(skillId)) return null;
        var loadout = configuration.Draft(catalog, classId);
        var level = loadout.LevelOf(skillId);
        if (level < 1) return null;

        var definition = catalog.SkillForGrowth(skillId, ownerId != PlayerCombatId);

        if (skillId is "armor-penetration" or "revenge-incarnate" or "bard-of-destruction"
            or "deep-breath" or "sniping" or "blood-revenge")
        {
            var ranks = definition?.ReactionsByRank;
            var index = Math.Min(level, ranks?.Count ?? 0) - 1;
            if (ranks is null || index < 0 || ranks[index] is not { } tier) return null;
            return tier.FirstOrDefault(reaction => reaction.Kind == skillId) is global::Inotia4.Domain.Combat.ISkillReactionWithBuff
                { } withBuff ? withBuff.Buff : null;
        }

        if (skillId == "whist")
        {
            var unit = definition?.Action?.CombatUnits?.OfType<BuffCombatUnit>().FirstOrDefault();
            if (unit is null || unit.Ranks.Count == 0) return null;
            var rank = unit.Ranks[Math.Min(level, unit.Ranks.Count) - 1];
            return new SkillReactionBuff(unit.Buff, rank.Strength, rank.Effect);
        }

        return null;
    }

    private void CombatFollowUp(string kind, string casterId, string targetId) =>
        CombatFollowUps.Run(kind, casterId, targetId, new WorldFollowUpPort(this));

    private sealed class ActionDispatchPort(WorldScene scene, string casterId, Action work,
        ActionDispatchDefinition definition) : IActionDispatchPort
    {
        private bool _stealthed;

        public bool IsStealthed()
        {
            _stealthed = scene.Conditions.HasState(casterId, "stealthed");
            return _stealthed;
        }

        public void Work()
        {
            work();
            if (definition.Shake == true) scene._feedbackView?.StartShake();
        }

        public void RemoveStealth()
        {
            scene.Conditions.RemoveState(casterId, "stealthed");
            if (scene.ActorNodeOf(casterId) is { } node) node.Visual.SetStealth(false);
        }

        public void AfterStealthBreak() =>
            scene.React(new ActionStealthBreakEvent { OwnerId = casterId });

        public void Sound()
        {
            if (definition.Sound is { } sound && scene.PartySideOf(casterId)) scene._audio?.Effect(sound);
        }
    }

    private sealed class WorldFollowUpPort(WorldScene scene) : ICombatFollowUpPort
    {
        public SkillCatalog Catalog => scene._loader.Skills;

        public CombatRuntime Runtime => scene.CombatRuntime;

        public (string ClassId, SkillLoadout Skills)? Loadout(string id)
        {
            if (id != WorldScene.PlayerCombatId || scene._skills is null) return null;
            return (scene._classId, scene._skills.Draft(scene._loader.Skills, scene._classId));
        }

        public Func<double> Random => scene._random.NextDouble;

        public bool Active() => !scene.StoryPlaying;

        public Combatant? Actor(string id) => scene.FindCombatant(id);

        public void CasterStrikes(string casterId, CasterStrikes strikes)
        {
            scene._actionPort ??= new SceneActionPort(scene);
            scene._dispatcher ??= new CombatActionDispatcher(scene._actionPort);
            scene._dispatcher.CasterStrikes(casterId, strikes);
        }

        public ITransitionPort Flight => new FollowUpTransitionPort(scene);

        public void Execute(string casterId, string? targetId, string skillId, int level, SkillAction action,
            Vec2? origin) =>
            scene.RunFollowUpUnits(casterId, targetId, skillId, level, action, origin);
    }

    private sealed class FollowUpTransitionPort(WorldScene scene) : ITransitionPort
    {
        public Combatant? Actor(string id) => scene.FindCombatant(id);

        public void Emit(FlightEvent flight) => scene.ApplyFlightEvent(flight);

        public void Effect(string name, Vec2 position) => scene.SpawnAttackEffect(name, "default", position, null);
    }

    private void RunFollowUpUnits(string casterId, string? targetId, string skillId, int level,
        SkillAction action, Vec2? origin)
    {
        var cue = action.Events.FirstOrDefault(item => item is UnitsEvent) as UnitsEvent;
        CombatUnitExecution.ExecuteSkillUnits(new SkillExecution
        {
            CasterId = casterId,
            Skill = new SkillPlayback(skillId, action, level),
            Cues = cue?.Cues ?? [],
            Origin = origin,
            SelectedTargetId = targetId,
        }, new SkillExecutionPort
        {
            Port = new PlayerCombatPort(this),
            SelectTarget = _ => { },
        });
    }

    private void SummonOwnerReactions(AttackContact contact, HitResult hit, string damageKind)
    {
        _ = (contact, hit, damageKind);
    }

    private void DisturbanceReaction(string attackerId, string defenderId)
    {
        if (defenderId != PlayerCombatId || _skills is null) return;

        var catalog = _loader.Skills;
        var threshold = SkillReactions.LearnedReactions(catalog, _classId, _skills.Draft(catalog, _classId))
            .OfType<DisturbanceReaction>().FirstOrDefault()?.ChanceThreshold ?? 0;
        if (!(threshold > 0) || (int)(_random.NextDouble() * 999) + 1 >= threshold) return;

        var self = FindCombatant(defenderId);
        if (self is null) return;

        var selfDefense = Fighter.EffectiveDefense(
            Math.Max(0, AttributeOf(defenderId, "defense")), Math.Max(0, AttributeOf(defenderId, "defenseRate")));
        var candidates = new List<Disturbance.IActor>();
        foreach (var actor in AllCombatants())
        {
            var defense = Fighter.EffectiveDefense(
                Math.Max(0, AttributeOf(actor.Id, "defense")), Math.Max(0, AttributeOf(actor.Id, "defenseRate")));
            candidates.Add(new DisturbanceCandidate(actor, actor.Team == self.Team,
                OwnSummon: false, defense));
        }

        var candidateId = Disturbance.Candidate(
            new DisturbanceCandidate(self, SameCamp: true, OwnSummon: false, selfDefense), candidates);
        if (candidateId is null || candidateId == CurrentTargetOf(attackerId)) return;
        _threat?.Taunt(candidateId, attackerId);
    }

    private sealed record DisturbanceCandidate(
        Combatant Actor, bool SameCamp, bool OwnSummon, int EffectiveDefense)
        : Disturbance.IActor
    {
        public string Id => Actor.Id;

        public Vec2 Position => Actor.Position;

        public CollisionBox Bounds => Actor.Bounds;
    }

    /// <summary>
    /// 血之复仇：每个学过它的队伍槽各判一次自己的已学等级；
    /// 来源就是该队员自己，所以不经过被攻击者。
    /// </summary>
    private void PartyRevengeReactions(string attackerId, string damageKind, string outcome)
    {
        foreach (var id in ActivePartySlots())
            React(new BloodRevengeEvent
            {
                OwnerId = id,
                OpponentId = attackerId,
                DamageKind = damageKind,
                Outcome = outcome,
            });
    }

    private void MoltenReflection(AttackContact contact, int ratePerThousand)
    {
        var defenderId = contact.TargetId;
        var attackerId = contact.CasterId;
        var defender = FindEnemy(defenderId);
        var isMonster = defender is not null;
        var source = isMonster
            ? new MoltenArmor.DefenseSource("monster", PhysicalAttack: AttributeOf(defenderId, "physicalAttack"), 0, 0)
            : new MoltenArmor.DefenseSource("character", 0,
                Defense: AttributeOf(defenderId, "defense"), DefenseRate: AttributeOf(defenderId, "defenseRate"));
        var damage = MoltenArmor.ReflectionDamage(source, ratePerThousand);
        MoltenDirectDamage(defenderId, attackerId, damage);

        var splash = MoltenArmor.RollSplash(damage, AttributeOf(defenderId, "fireSplashChance"), _random.NextDouble);
        if (splash is { } amount)
        {
            var defenderNode = ResolveActorNode(defenderId);
            if (defenderNode is not null && FindCombatant(defenderId) is { } sourceCombatant)
            {
                var selection = AttackSplash.SelectCenter(sourceCombatant, AllCombatants(), 4,
                    CurrentTargetOf(defenderId));
                foreach (var target in MoltenArmor.SplashTargets(sourceCombatant, selection.Center,
                             AllCombatants(), attackerId))
                    MoltenDirectDamage(defenderId, target.Id, amount);
            }
        }

        React(new MoltenReflectionCompletedEvent { OwnerId = defenderId, OpponentId = attackerId });
    }

    private void MoltenDirectDamage(string casterId, string targetId, int damage)
    {
        if (damage <= 0 || !Conditions.CanLoseHealth(targetId)) return;
        SettleHit(casterId, targetId, new HitResult(HitKind.Hit, damage));
    }

    private void HolySacrificeTransfer(AttackContact contact, HitResult hit, string damageKind)
    {
        var rate = AttributeOf(contact.TargetId, "damageTransferPerThousand");
        if (rate <= 0) return;

        var protector = Conditions.Snapshot(contact.TargetId)
            .FirstOrDefault(condition => condition.Kind == "buff" && condition.Id == "holy-sacrifice")?.SourceId;
        if (protector is null || !IsCombatantAlive(protector)) return;

        var damage = hit.Damage;
        var transferred = (int)Math.Truncate(damage * (double)rate / 1000);
        if (damageKind == "physical" && damage * rate < 1000) return;
        if (transferred >= damage) return;

        var protectorFighter = FindFighter(protector);
        if (protectorFighter is null) return;
        var resistance = damageKind == "physical"
            ? protectorFighter.PhysicalResistance
            : protectorFighter.MagicResistance;
        var damageToProtector = (int)Math.Truncate(transferred * (1000 - resistance) / 1000.0);
        hit = hit with { Damage = damage - transferred };
        if (damageToProtector > 0 && Conditions.CanLoseHealth(protector))
            SettleHit(contact.CasterId, protector, new HitResult(HitKind.Hit, damageToProtector));
    }

    private void AttackProc(AttackContact contact, HitResult hit, Vec2? targetPosition = null)
    {
        if (hit.Kind == HitKind.Miss) return;
        if (hit.Damage <= 0) return;

        var attributes = ActorAttributesOf(contact.CasterId);
        if (attributes is null) return;
        var classId = contact.CasterId == PlayerCombatId ? _classId : FindEnemy(contact.CasterId)?.EnemyInfo.Id;
        var baseDamage = DamageFormula.NormalDamageForClass(
            attributes.Value.PhysicalAttack, attributes.Value.PhysicalDamageRate, attributes.Value.StealthDamageRate,
            attributes.Value.MagicAttack, attributes.Value.MagicDamageRate, null);

        _attackProcs.Execute(baseDamage, new AttackProcPort(this, contact));
    }

    private void AttackProcDamage(string casterId, string targetId, int amount)
    {
        if (amount <= 0 || FindCombatant(targetId) is null || !Conditions.CanLoseHealth(targetId)) return;
        SettleHit(casterId, targetId, new HitResult(HitKind.Hit, amount));
    }

    private Vec2 AttackSplashCenter(Combatant source, IReadOnlyList<Combatant> candidates, int rangeUnits)
    {
        var selection = AttackSplash.SelectCenter(source, candidates, rangeUnits,
            CurrentTargetOf(source.Id));
        if (selection.TargetId is { } targetId && targetId != CurrentTargetOf(source.Id))
            _playerCombat?.SetReactionTarget(targetId);
        return selection.Center;
    }

    private int EquipmentLifeDrainPerThousand(string id) =>
        EquipmentAttributes.Recovery(EquippedInstances(id)).LifeDrainPerThousand;

    private bool IsTwoHanded(string id)
    {
        if (id != PlayerCombatId || _hero is null) return false;
        var mainHand = _hero.Bag.EquipmentItems().GetValueOrDefault("main-hand");
        return mainHand is { Length: > 0 } &&
               _loader.Items.Items.GetValueOrDefault(mainHand)?.Equipment?.TwoHanded == true;
    }

    private readonly AttackProcExecutor _attackProcs = new();

    private sealed class AttackProcPort(WorldScene scene, AttackContact contact)
        : AttackProcExecutor.IAttackProcPort
    {
        public Func<double>? Random => scene._random.NextDouble;

        public int ReadChance(string key) => scene.AttributeOf(contact.CasterId, key);

        public void ApplyDamage(int amount) =>
            scene.AttackProcDamage(contact.CasterId, contact.TargetId, amount);

        public void ApplyBuff(string key)
        {
            var effect = scene.BattleContent.StatusAttackBuffs?.GetValueOrDefault(key);
            if (effect is not null)
                scene.ApplyActorBuff(contact.TargetId, key, 0, effect, contact.CasterId);
        }

        public int FireSplash(int amount)
        {
            var source = scene.FindCombatant(contact.CasterId);
            if (source is null) return 0;
            var candidates = scene.AllCombatants();
            var center = scene.AttackSplashCenter(source, candidates, 3);
            var targets = AttackSplash.Targets(source, center, candidates, 3);
            foreach (var target in targets)
                scene.AttackProcDamage(contact.CasterId, target.Id, amount);
            return targets.Count;
        }
    }

    private CombatEffectExecutor? _effectExecutor;
    private SceneEffectPort? _effectPort;

    private readonly HashSet<string> _dealtDamage = [];

    /// <summary>
    /// 当前锁定的攻击目标：按下攻击时锁到当前选中目标，
    /// 目标消失／不可瞄准／团灭与复活收尾时清空；进世界快照。
    /// </summary>
    private string? _focusTargetId;

    private int _damageCount;

    private void FocusEnemy(string? key)
    {
        if (key is not null && _pendingDefeats.Contains(key)) return;
        _focusTargetId = key;
        foreach (var ally in _allies)
            if (ally.MemberId != ControlledId) ally.Ai.Cancel();
        if (ControlledId != PlayerCombatId) _heroAi?.Cancel();
    }

    /// <summary>
    /// 状态栏大面板的点击（锁定目标或回到跟随被控者），挂在
    /// 状态面板 onFocus 上，**不是攻击键**）：有选中目标就锁他，否则回到跟随被控者——
    /// 解除锁定并撤掉被控者的当前动作（<c>cancel(false)</c>：收动作、留特效、清目标）。
    /// </summary>
    private void FocusSelected()
    {
        if (CombatRuntime.SelectedTargetId(ControlledId) is { Length: > 0 } target)
        {
            FocusEnemy(target);
            return;
        }

        FocusEnemy(null);
        CombatRuntime.CancelAndClearTarget(ControlledId);
    }

    /// <summary>
    /// 队员被敌对单位接战就累加连续受击计数。
    /// 普攻与技能两条接触路径共用它，保证只有一处口径。
    ///
    /// 这一行在四个早退门之后（：全灭／已关闭／
    /// 任一方已倒下／任一方待结算阵亡），所以这里同口径先过门再计数——
    /// 给已经倒下的人补刀不该再算「被打第 N 下」。
    /// </summary>
    private void CountPartyHit(string casterId, string targetId)
    {
        if (_defeated) return;
        if (_fallen.Contains(casterId) || _fallen.Contains(targetId)) return;
        if (_pendingDefeats.Contains(casterId) || _pendingDefeats.Contains(targetId)) return;
        if (IsPartyMember(targetId) && !IsPartyMember(casterId)) _damageCount++;
    }

    private void ForgetFocusedEnemy(string key)
    {
        if (_focusTargetId == key) _focusTargetId = null;
    }

    private bool IsPartyMember(string id) => id == PlayerCombatId || _party?.Get(id) is not null;

    private bool IsPartyMemberAlive(string id) =>
        id == PlayerCombatId ? _playerHealth > 0 : _party?.Alive(id) ?? true;

    private bool PartyFullyEngaged()
    {
        var alive = ActivePartySlots().Where(IsPartyMemberAlive).ToList();
        return alive.Count > 0 && alive.All(id => _dealtDamage.Contains(id));
    }

    public SkillUnitOutcome SkillUnit(SkillUnitHit hit)
    {
        RecordSummonContact(hit.CasterId, hit.TargetId);
        return (_effectExecutor ??= new CombatEffectExecutor(_effectPort ??= new SceneEffectPort(this))).Execute(hit);
    }

    public PreparedDamage? PrepareSkillUnit(SkillUnitStart start)
    {
        var units = start.Skill.Action.CombatUnits;
        if (units is null || start.CueIndex >= units.Count) return null;
        if (units[start.CueIndex] is not NumericUnit unit) return null;

        var sourceId = ValueSourceIdOf(start.CasterId, unit);
        if (sourceId != PlayerCombatId && FindEnemy(start.CasterId) is null) return null;

        var attributes = sourceId == PlayerCombatId
            ? _panel.Attributes
            : new PrimaryAttributes();
        return DamageFormula.Prepare(unit, start.Skill.Level ?? 1, attributes);
    }

    private string ValueSourceIdOf(string casterId, ICombatUnit unit)
    {
        if (unit is not NumericUnit numeric || numeric.OwnerValues != true) return casterId;
        return FindEnemy(casterId)?.SummonedBy ?? casterId;
    }

    private SkillUnitOutcome CommandOwnedSummons(SkillUnitHit hit)
    {
        if (hit.CasterId != PlayerCombatId) return SkillUnitOutcome.Success;

        var caster = FindCombatant(hit.CasterId);
        if (caster is null) return SkillUnitOutcome.Failure;

        var commanded = false;
        foreach (var enemy in _enemies.ToList())
        {
            if (enemy.Dead || enemy.SummonedBy != hit.CasterId) continue;
            if (enemy.EnemyInfo.CommandAction is not { } command) continue;

            var current = enemy.SummonAi?.CurrentTargetId();
            var target = current is not null && FindCombatant(current) is { } candidate &&
                         CombatController.CanTargetCombatant(caster, candidate)
                ? current
                : null;
            enemy.SummonAi?.Command(command, target, hit.Skill.Level ?? 1);
            commanded = true;
        }

        return commanded ? SkillUnitOutcome.Success : SkillUnitOutcome.Failure;
    }

    /// <summary>
    /// 从技能单元里召唤一只生物：按主人等级派生属性，
    /// 有主人侧换算规则的寿命在施放时按主人属性算，否则用本等级帧寿命。
    /// </summary>
    private SkillUnitOutcome SummonFromUnit(SkillUnitHit hit, SummonCombatUnit unit, int rank)
    {
        if (unit.Creature is null || hit.CasterId != PlayerCombatId) return SkillUnitOutcome.Success;

        var creature = _loader.Battle.Enemies.FirstOrDefault(enemy => enemy.Id == unit.Creature);
        if (creature?.Actor.Collision is null) return SkillUnitOutcome.Success;

        var ownerLevel = _hero?.Level ?? 1;
        var rankValues = rank >= 0 && rank < unit.Ranks.Count ? unit.Ranks[rank] : null;
        var derived = unit.OwnerDerived is { } derivation
            ? SummonStatsRules.OwnerStats(derivation,
                (rankValues?.AttackRatePerThousand, rankValues?.HealthRatePerThousand), ownerLevel,
                _panel.Attributes)
            : null;

        var position = FindCombatant(hit.CasterId)?.Position ?? _position;
        var facing = hit.CasterId == PlayerCombatId ? PlayerFacing : FindCombatant(hit.CasterId)?.Facing ?? "south";
        var spot = NearestSummonSpot(position, creature.Actor.Collision,
            SkillGeometry.PlanSummonSpot(position, PlayerCombatant().Bounds, facing,
                new SummonPlacement
                {
                    OffsetChoices = [new SummonOffset { X = 0, Y = 0 }],
                    SearchGapPx = 32,
                    SearchExtentPx = 48,
                    JitterPx = 0,
                },
                point => CanSummonAt(point, creature.Actor.Collision)));
        if (spot is null) return SkillUnitOutcome.Failure;

        var id = SummonCreature(hit.CasterId, unit.Creature, spot.Value, derived);
        if (id is null) return SkillUnitOutcome.Failure;

        foreach (var effect in unit.AppearanceEffects ?? [])
            SpawnAttackEffect(effect.Effect, "default", FindCombatant(id)?.Position ?? spot.Value, effect.Placement, id);

        // 寿命：有主人侧换算的按主人属性算帧，否则用本等级的毫秒寿命。
        var summon = FindEnemy(id);
        if (summon is not null)
        {
            summon.SummonRemainingMs = unit.LifetimeFromOwner is { } lifetime
                ? (SummonStatsRules.LifetimeFrames(lifetime, AttributeOf(hit.CasterId, lifetime.Attribute)) + 1) * CombatClock.LogicFrameMs
                : rankValues?.DurationMs;
        }

        return SkillUnitOutcome.Success;
    }

    private void DropMana(string casterId, int damage, Vec2? position, string? kind)
    {
        if (_mapLease is null || position is not { } target) return;

        var pickup = _loader.Items.Items.GetValueOrDefault("mana-gem")?.Pickup.AsManaPickup();
        if (pickup is null) return;

        var weaponKind = casterId == PlayerCombatId ? _panel.WeaponKind : null;
        var origin = casterId == PlayerCombatId ? _position : FindCombatant(casterId)?.Position ?? _position;
        var landing = ManaGems.RollManaGem(pickup, damage, kind ?? "weapon", weaponKind, target,
            point => CanSummonAt(point, new CollisionBox { X = -12, Y = -12, Width = 24, Height = 24 }) &&
                     Pathfinding.Find(origin, new GoalQuery(_grid, point), 256) is not null,
            _random.NextDouble);
        if (landing is null) return;

        var direction = pickup.HitDrop.Directions[landing.Direction];
        _session.GroundLoot.AddFlying(CurrentMapId, (target.X, target.Y),
            (landing.X, landing.Y, landing.Direction), "mana-gem",
            (int)(direction.Clip.Frames * 1000f / direction.Clip.FrameRate));
    }

    public int? HealScoreFor(string id)
    {
        var vitals = VitalsOf(id);
        if (vitals is not { } value) return null;
        return HealTarget.Score(value.Health, value.MaxHealth, _dealtDamage.Contains(id));
    }

    public IReadOnlyList<PartyBuffSlot?> PartyBuffSlots()
    {
        var slots = new List<PartyBuffSlot?>
        {
            new(PlayerCombatant(), _playerHealth > 0 || _hits.IsAwaitingDefeat(PlayerCombatId), true),
        };
        foreach (var ally in _allies)
            slots.Add(new PartyBuffSlot(AllyCombatant(ally.Node), _party?.Alive(ally.MemberId) ?? false, true));

        return slots;
    }

    public IReadOnlyList<HealSlot> HealCandidatesFor()
    {
        var slots = new List<HealSlot>();
        foreach (var slot in PartyBuffSlots())
            if (slot is { } entry) slots.Add(new HealSlot(entry.Actor, entry.Active, entry.HasDisplay, HealScoreFor(entry.Actor.Id)));
        foreach (var enemy in _enemies)
        {
            if (enemy.SummonedBy is not { } owner || !PartySideOf(owner)) continue;
            if (enemy.Dead && !_hits.IsAwaitingDefeat(enemy.CombatId)) continue;
            slots.Add(new HealSlot(EnemyCombatant(enemy), !enemy.Dead, true, HealScoreFor(enemy.CombatId)));
        }

        return slots;
    }

    public bool PartySideOf(string id)
    {
        if (id == PlayerCombatId) return true;
        if (_party?.Get(id) is not null) return true;
        return FindEnemy(id)?.SummonedBy is { } owner && PartySideOf(owner);
    }

    private sealed class SceneEffectPort(WorldScene scene) : ICombatEffectPort
    {
        public CombatConditions Conditions => scene.Conditions;

        public CombatThreatSystem? Threat => scene._threat;

        public double Random() => scene._random.NextDouble();

        public T Run<T>(Func<T> work) => scene._hits.Run(work);

        public bool Available(string id) => scene.IsCombatantAlive(id);

        public bool PartySide(string id) => scene.PartySideOf(id);

        public Fighter? Fighter(string id) => scene.FindFighter(id);

        public Vec2? Position(string id) => scene.FindCombatant(id)?.Position;

        public Vitals? Vitals(string id)
        {
            var value = scene.VitalsOf(id);
            return value is { } vital ? new Vitals(vital.Health, vital.Mana, vital.MaxMana) : null;
        }

        public int Attribute(string id, string key) => scene.AttributeOf(id, key);

        public string ValueSourceId(string id, ICombatUnit unit) => scene.ValueSourceIdOf(id, unit);

        public PreparedDamage? Prepare(SkillUnitHit hit) => scene.PrepareSkillUnit(hit);

        public bool ApplyBuff(string id, string buffId, int strength, BattleBuffEffect effect, string sourceId) =>
            scene.ApplyActorBuff(id, buffId, strength, effect, sourceId);

        public StateApplicationRequest StateRequest(string sourceId, string targetId, string state) =>
            scene.StateApplicationRequestOf(sourceId, targetId, state);

        public void StateAccepted(string targetId, string state) =>
            scene.ApplyStateConsequences(targetId, [state]);

        public SkillUnitOutcome CommandSummons(SkillUnitHit hit) => scene.CommandOwnedSummons(hit);

        public SkillUnitOutcome Summon(SkillUnitHit hit, SummonCombatUnit unit, int rank) =>
            scene.SummonFromUnit(hit, unit, rank);

        public void RestoreHealth(string id, int amount) => scene.RestoreHealth(id, amount);

        public void SetMana(string id, int value) => scene.SetManaAbsolute(id, value);

        public void HealAccepted(string sourceId, string targetId) =>
            scene.React(new DirectHealAcceptedEvent { OwnerId = sourceId, TargetId = targetId });

        public SkillReactionBuff? LearnedBuff(string id, string key) => scene.LearnedReactionBuff(id, key);

        public int RearBonus(string sourceId, string targetId) => scene.RearPenetrationBonus(sourceId, targetId);

        public void Reactions(SkillUnitHit hit, HitResult result, string kind, Vec2? position) =>
            scene.ResolveHitReactions(new AttackContact(hit.CasterId, hit.TargetId, SkillId: hit.Skill.Id),
                result, kind, hit.Skill.Id, position);

        public void Hit(SkillUnitHit hit, HitResult result, HitPresentation presentation) =>
            scene.SettleHit(hit.CasterId, hit.TargetId, result, presentation.Style, periodic: false,
                sound: presentation.Sound);

        public void DropMana(string id, int damage, Vec2? position, string? kind) =>
            scene.DropMana(id, damage, position, kind);

        public void ShowHit(SkillUnitHit hit, HitResult result, HitPresentation presentation) =>
            scene.ShowHitFeedback(result, hit.TargetId, hit.CasterId == WorldScene.PlayerCombatId,
                presentation.Style);
    }

    private sealed class WorldReactionPort(WorldScene scene) : IHitReactionPort
    {
        public CombatConditions Conditions => scene.Conditions;

        public int ActorAttribute(string id, string key) => scene.AttributeOf(id, key);

        public void React(SkillReactionEvent action) => scene.React(action);

        public void ReactToBuff(BuffReactionEvent action) => scene.ReactToBuff(action);

        public void LightningOfHellReaction(string casterId, string targetId) =>
            scene.CombatFollowUp("lightning-of-hell", casterId, targetId);

        public void GodOfJusticeReaction(string casterId, string targetId) =>
            scene.CombatFollowUp("god-of-justice", casterId, targetId);

        public void SummonOwnerReactions(AttackContact contact, HitResult hit, string damageKind) =>
            scene.SummonOwnerReactions(contact, hit, damageKind);

        public void DisturbanceReaction(string casterId, string targetId) =>
            scene.DisturbanceReaction(casterId, targetId);

        public bool PartySide(string id) => scene.PartySideOf(id);

        public IReadOnlyList<string> ActivePartySlots() => scene.ActivePartySlots();

        public void PartyRevengeReactions(string casterId, string damageKind, string outcome) =>
            scene.PartyRevengeReactions(casterId, damageKind, outcome);

        public int EquipmentLifeDrainPerThousand(string id) => scene.EquipmentLifeDrainPerThousand(id);

        public bool IsTwoHanded(string id) => scene.IsTwoHanded(id);

        public void MoltenReflection(AttackContact contact, int rate) => scene.MoltenReflection(contact, rate);

        public void HolySacrificeTransfer(AttackContact contact, HitResult hit, string damageKind) =>
            scene.HolySacrificeTransfer(contact, hit, damageKind);

        public void AttackProc(AttackContact contact, HitResult hit, Vec2? position = null) =>
            scene.AttackProc(contact, hit, position);
    }

    private sealed class FixedRandomPorts(Func<double> random, Func<string, string, SkillReactionBuff?> learnedBuff)
        : ISkillReactionPorts
    {
        public double Random() => random();

        public SkillReactionBuff? LearnedBuff(string ownerId, string skill) => learnedBuff(ownerId, skill);
    }

    private void RecoverResources()
    {
        if (!_recovery.PulseDueAtFrame(_combatClock.Frame)) return;

        var actors = new List<ResourceRecoveryActor>();
        var regeneration = _session.Recovery.ActiveRegeneration;
        var bonusHealth = regeneration?.HealthRecoveryPerPulse ?? 0;
        var bonusMana = regeneration?.ManaRecoveryPerPulse ?? 0;

        foreach (var id in ActivePartySlots())
        {
            actors.Add(new ResourceRecoveryActor(
                id,
                MemberHealth(id) > 0 && !_pendingDefeats.Contains(id),
                MemberLevel(id),
                AttributeOf(id, "healthRecoveryPerPulse") + bonusHealth,
                AttributeOf(id, "manaRecoveryPerPulse") + bonusMana));
        }

        foreach (var enemy in _enemies)
        {
            if (enemy.Dead) continue;
            var id = enemy.CombatId;
            actors.Add(new ResourceRecoveryActor(id, true, enemy.Fighter.Level,
                AttributeOf(id, "healthRecoveryPerPulse"),
                AttributeOf(id, "manaRecoveryPerPulse"),
                Conditions.HasBuff(id, "monster-emergency-recovery")));
        }

        foreach (var pulse in _recovery.AdvanceToFrame(_combatClock.Frame, actors))
        {
            foreach (var amount in pulse.Amounts)
            {
                var isMember = amount.ActorId == PlayerCombatId || _party?.Get(amount.ActorId) is not null;
                if (isMember)
                {
                    RestoreHealth(amount.ActorId, amount.HealthAmount);
                    RestoreMana(amount.ActorId, amount.ManaAmount);
                    continue;
                }

                var enemy = FindEnemy(amount.ActorId);
                if (enemy is null || enemy.Dead) continue;
                // 敌人这条走节点＋名册一起写：名册认生成键，没有键的（剧情换出来的）
                // 只回节点，否则整段恢复会被键判空丢掉。
                RestoreEnemyHealth(enemy, enemy.Health + amount.HealthAmount);
                RestoreMana(amount.ActorId, amount.ManaAmount);
            }

            _threat?.Decay();
        }
    }

    private void RestoreEnemyHealth(EnemyNode enemy, int health)
    {
        enemy.RestoreHealth(health);
        if (enemy.SpawnKey is { Length: > 0 } key)
            _mapRoster?.SetEnemyHealth(key, enemy.Health, enemy.EnemyInfo);
    }

    /// <summary>
    /// 对一只怪造成伤害并把结果回写名册，供同图重建沿用。
    ///
    /// 伤害先交给剧情：跨过 30% 或致死一击时可能被换成捕捉目标。
    /// 返回 true 表示这一槽已经变成同格 NPC，源怪也已撤下——调用方不该再走击杀结算，
    /// 否则多段伤害会接着打死只剩 1 点血的它，播死亡表现并结算击杀奖励。
    /// </summary>
    private bool DamageEnemy(EnemyNode enemy, int damage)
    {
        var before = enemy.Health;
        var proposed = Math.Max(0, before - damage);

        var handled = false;
        string? captured = null;
        string? scriptId = null;
        if (damage > 0 && enemy.SpawnKey is { Length: > 0 } spawnKey)
        {
            var change = _story.HealthChanged(enemy.EnemyInfo.Id, before, proposed, enemy.Fighter.MaxHealth,
                new Session.EnemySource(spawnKey, _map.Id, enemy.Position.X, enemy.Position.Y));
            handled = change.Handled;
            captured = change.CapturedEnemy;
            scriptId = change.ScriptId;
        }

        if (captured is { } capturedKey)
        {
            if (_session.StoryWorld.Entity("quest-target:" + capturedKey) is { } entity)
                ApplyStoryCommand(new PlaceCommand(entity, VisitScope: true));
            RemoveMapEnemy(capturedKey);
            return true;
        }

        if (handled && scriptId is { Length: > 0 } script)
        {
            _journal.MarkEventStarted(script);
            Callable.From(() => PlayStory(script, script)).CallDeferred();
        }

        var remaining = handled ? Math.Max(1, proposed) : proposed;
        if (remaining < before) enemy.RestoreHealth(remaining);
        if (enemy.SpawnKey is { Length: > 0 } key)
            _mapRoster?.SetEnemyHealth(key, enemy.Health, enemy.EnemyInfo);
        return false;
    }

    private Combatant PlayerCombatant() =>
        new(PlayerCombatId, "party", _position, _player.Definition.Collision, _player.Facing,
            Conditions.ActiveStates(PlayerCombatId));

    /// <summary>
    /// 一只怪的战斗视图。玩家侧召唤物归到主人阵营（原版战斗世界的 friendly 判定），
    /// 否则它的控制器会把同类当目标、敌人的候选过滤也会把它当同类。
    /// </summary>
    private Combatant EnemyCombatant(EnemyNode enemy) =>
        new(enemy.CombatId,
            PartySideOf(enemy.CombatId) ? "party" : "hostile",
            new Vec2(enemy.Position.X, enemy.Position.Y),
            enemy.Definition.Collision, enemy.Facing, Conditions.ActiveStates(enemy.CombatId));

    private void AttachEnemyCombat(EnemyNode enemy)
    {
        var attack = TryResolveEnemyAttack(enemy.EnemyInfo);
        if (enemy.SummonedBy is { } owner && (owner == PlayerCombatId || _party?.Get(owner) is not null))
        {
            enemy.AttachSummonAi(enemy.EnemyInfo.Behavior, attack,
                new FriendlySummonPort(this, enemy, owner), owner,
                enemy.EnemyInfo.Id == "enemy-beast-companion-1"
                    ? FriendlySummonRole.Beast
                    : FriendlySummonRole.HellSoldier);
        }
        else enemy.AttachAi(enemy.EnemyInfo.Behavior, attack, new EnemyPort(this, enemy));
        if (enemy.Combat is { } combat) CombatRuntime.RegisterActor(enemy.CombatId, combat);
    }

    private ResolvedBasicAttack? TryResolveEnemyAttack(EnemyDefinition definition)
    {
        _battle ??= _loader.Battle;
        if (definition.BasicAttack is null) return null;

        try
        {
            return BasicAttackRules.Resolve(_battle.BasicAttacks, definition.Actor, definition.BasicAttack);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[combat] {definition.Id} 的普攻无法解析，按不出手处理：{error.Message}");
            return null;
        }
    }

    private class EnemyPort(WorldScene scene, EnemyNode enemy) : IEnemyPort
    {
        public Combatant? Actor(string id) => scene.FindCombatant(id);

        public IReadOnlyList<Combatant> Candidates() => scene.AllCombatants();

        public IReadOnlyList<PartyBuffSlot?>? PartySlots() => scene.PartyBuffSlots();

        public bool IsAlive(string id) => scene.IsCombatantAlive(id);

        public bool IsPaused() => scene.CombatRuntime.Paused;

        public IActorCooldownPort Cooldowns => scene.CooldownsFor(enemy.CombatId);

        public string Facing() => enemy.Facing;

        public void MoveToward(Vec2 position) => enemy.MoveToward(position);

        public void Stop() => enemy.HaltMotion();

        /// <summary>强制位移的整盒落位；敌人不会冲锋，这里只回答地形能不能站。</summary>
        public bool CanStand(Vec2 position, string? clearanceActorId = null) =>
            scene._grid.CanStandOnTerrain(position.X, position.Y);

        public Action Play(CombatAction action, Action<int> frame, Action complete) =>
            scene.PlayCombatAction(action, frame, complete);

        public void Frame(CombatAction action, int index, Action<string, string?> contact,
            SkillUnitsReady unitsReady, Action? areaContact = null) =>
            scene.DispatchCombatFrame(action, index, contact, unitsReady, areaContact);

        public void Contact(AttackContact contact)
        {
            if (contact.SkillId is not null)
            {
                scene.NotifySkillContact(contact);
                return;
            }

            scene.SettleEnemyContact(enemy, contact.TargetId, contact.Hand ?? "main-hand");
        }

        public SkillUnitOutcome Unit(SkillUnitHit hit) => scene.SkillUnit(hit);

        public PreparedDamage? PrepareUnit(SkillUnitStart start) => scene.PrepareSkillUnit(start);

        public void RunUnitSequence(Action work) => scene.RunInHitResolution(work);

        public void UnitFeedback(SkillUnitHit hit, SkillUnitOutcome outcome, SkillEffectCue cue) =>
            scene.ShowUnitFeedback(hit, outcome, cue);

        /// <summary>追近期间目标必须仍在仇恨账本上，否则收手。</summary>
        public bool CanFinishApproach(string targetId) =>
            scene.PartySideOf(enemy.CombatId) || scene.ThreatHas(enemy.CombatId, targetId);

        public void FollowPath(Func<Vec2?> nextWaypoint) => enemy.FollowPath(nextWaypoint);

        public Vec2 Home() => enemy.Home;

        public (int Current, int Maximum)? Health(string id) =>
            id == enemy.CombatId ? (enemy.Health, scene.ResourceCapacityOf(id, "maxHealth")) :
            scene.VitalsOf(id) is { } vitals ? (vitals.Health, scene.ResourceCapacityOf(id, "maxHealth")) : null;

        public Rect NavigationBounds() => scene._grid.Bounds;

        public PathBudget? PathBudget => scene._pathBudget;

        public IStandability? NavigationSnapshot() => scene._grid;

        public IEnemyThreatPort? Threat => scene.PartySideOf(enemy.CombatId) ? null : scene.EnemyThreatPortFor(enemy);

        public void Face(string facing) => enemy.SetFacing(facing);
    }

    private sealed class EnemyThreatPort(CombatThreatSystem threat, string ownerId) : IEnemyThreatPort
    {
        public string? Select(string? current) => threat.Select(ownerId, current);

        public void Notice(string source) => threat.Notice(ownerId, source);

        public bool Has(string source) => threat.Has(ownerId, source);

        public void Clear() => threat.ClearOwner(ownerId);
    }

    private sealed class FriendlySummonPort : EnemyPort, IFriendlySummonPort
    {
        private readonly WorldScene _scene;
        private readonly EnemyNode _enemy;
        private readonly string _ownerId;

        public FriendlySummonPort(WorldScene scene, EnemyNode enemy, string ownerId) : base(scene, enemy)
        {
            _scene = scene;
            _enemy = enemy;
            _ownerId = ownerId;
        }

        public SummonTactics Tactics() =>
            _scene.SummonAwareness.Tactics(_ownerId, _scene.ActivePartySlots(), _scene._focusTargetId);

        public bool TryRecall() => _scene.TryRecallSummon(_enemy);

        public bool AutonomousTarget(string id)
        {
            var target = _scene.FindEnemy(id);
            return target is not null && !target.Dead &&
                   target.EnemyInfo.Behavior.Aggression != "none";
        }
    }

    private sealed class ThreatPort(WorldScene scene) : ICombatThreatPort
    {
        public CombatThreatActor? Actor(string id) => scene.FindThreatActor(id);

        public IReadOnlyList<CombatThreatActor> Actors() => scene.AllThreatActors();

        public string? CurrentTarget(string ownerId) => scene.FindEnemy(ownerId)?.Ai?.CurrentTargetId();

        public long LogicTick() => scene._combatClock.Frame;

        public int GenerationRate(string sourceId) => 1000;

        public int ReductionRate(string sourceId) => 0;

        public (int Physical, int Magic) NormalDamage(string sourceId) =>
            scene.FindFighter(sourceId) is { } fighter
                ? (fighter.PhysicalAttack, fighter.MagicAttack)
                : (0, 0);

        public string? TauntSource(string ownerId) => null;

        public void ApplyTaunt(string sourceId, string targetId)
        {
        }

        public void CombatStarted(string ownerId) => scene.FindEnemy(ownerId)?.Ai?.OnThreatAdded();
    }

    private CombatThreatSystem ThreatSystem => _threat ??= new CombatThreatSystem(new ThreatPort(this));

    private IEnemyThreatPort? EnemyThreatPortFor(EnemyNode enemy) =>
        new EnemyThreatPort(ThreatSystem, enemy.CombatId);

    private bool ThreatHas(string ownerId, string sourceId) => ThreatSystem.Has(ownerId, sourceId);

    private static Combatant CombatWorldActor(Combatant actor) =>
        actor;

    private void RecordSummonContact(string sourceId, string targetId)
    {
        var source = FindCombatant(sourceId);
        var target = FindCombatant(targetId);
        if (source is null || target is null) return;
        var mappedSource = CombatWorldActor(source);
        var mappedTarget = CombatWorldActor(target);
        if (mappedSource.Team != mappedTarget.Team)
            SummonAwareness.Contact(mappedSource, mappedTarget, SummonNow);
    }

    private void ObserveSummonCombat()
    {
        var observations = new List<SummonCombatObservation>();
        if (_playerHealth > 0)
            observations.Add(new SummonCombatObservation(CombatWorldActor(PlayerCombatant()),
                _playerCombat?.ActiveAction()));
        foreach (var ally in _allies)
        {
            if (_party is not null && !_party.Alive(ally.MemberId)) continue;
            observations.Add(new SummonCombatObservation(
                CombatWorldActor(AllyCombatant(ally.Node)), ally.Ai.CombatController.ActiveAction()));
        }
        foreach (var enemy in _enemies)
        {
            if (enemy.Dead) continue;
            string? chasingTargetId = null;
            if (enemy.Ai is { } ai)
            {
                if (ai.ActionState() == "chasing") chasingTargetId = ai.CurrentTargetId();
            }
            else if (enemy.SummonAi is { } summon)
            {
                if (summon.ActionState() == "chasing") chasingTargetId = summon.CurrentTargetId();
            }
            observations.Add(new SummonCombatObservation(CombatWorldActor(EnemyCombatant(enemy)),
                enemy.Combat?.ActiveAction(), chasingTargetId));
        }
        SummonAwareness.Observe(SummonNow, observations);
    }

    /// <summary>
    /// 再次核对整队脱战、安全落点并移动实体。
    /// 原版的落点复查把主人排除在占位之外（canPlace 的 placements）；这个移植里
    /// 身体互不占位（CanStand 只看地形），所以直接用整盒地形判定。
    /// </summary>
    private bool TryRecallSummon(EnemyNode enemy)
    {
        var owner = enemy.SummonedBy is { } ownerId ? FindCombatant(ownerId) : null;
        var actor = FindCombatant(enemy.CombatId);
        if (owner is null || actor is null || _playerHealth <= 0 || enemy.Dead ||
            CombatRuntime.Paused || !CombatRuntime.Idle ||
            !Conditions.CanAct(enemy.CombatId) || !Conditions.CanMove(enemy.CombatId) || _storyPlaying) return false;
        var now = SummonNow;
        if (SummonAwareness.QuietFor(now) < 3000) return false;

        var hostiles = _enemies.Where(value =>
            !value.Dead && !(value.SummonedBy is { } summonOwner && summonOwner == PlayerCombatId)).ToList();
        foreach (var value in hostiles)
        {
            var snapshot = value.Ai?.Snapshot();
            var action = value.Combat?.ActiveAction();
            var target = snapshot is { State: "chasing" } chasing ? chasing.TargetId
                : action is { State: "attacking" } attacking ? attacking.TargetId
                : null;
            if (target is not null && FindCombatant(target) is { } targetActor &&
                CombatWorldActor(targetActor).Team == "party") return false;
        }

        bool Safe(Vec2 point)
        {
            foreach (var value in hostiles)
            {
                var position = new Vec2(value.Position.X, value.Position.Y);
                if (Math.Floor(Math.Max(Math.Abs(position.X - point.X), Math.Abs(position.Y - point.Y)) / 8) <=
                    value.EnemyInfo.Behavior.AwarenessRangeUnits) return false;
            }
            return true;
        }

        if (!Safe(owner.Position) || !Safe(actor.Position)) return false;
        var body = enemy.Definition.Collision;
        var seed = StableSeed.Of(enemy.CombatId);
        foreach (var radius in new[] { 24f, 32f, 40f, 48f })
        for (var index = 0; index < 8; index++)
        {
            var angle = (index + (int)(seed % 8)) % 8 * MathF.PI / 4;
            var point = new Vec2(owner.Position.X + MathF.Cos(angle) * radius,
                owner.Position.Y + MathF.Sin(angle) * radius);
            if (!CanSummonAt(point, body) || !Safe(point) || RecallSpotBlocked(point) ||
                !Pathfinding.CanTraverse(owner.Position, point, probe => CanSummonAt(probe, body))) continue;
            enemy.TeleportTo(new Vector2(point.X, point.Y));
            return true;
        }

        return false;
    }

    /// <summary>召回落点不能压出口（脚点半径 8）也不能贴着可交互物件（16 像素内）。</summary>
    private bool RecallSpotBlocked(Vec2 point)
    {
        foreach (var exit in _map.Exits)
            if (point.X + 8 > exit.X && point.X - 8 < exit.X + exit.Width &&
                point.Y + 8 > exit.Y && point.Y - 8 < exit.Y + exit.Height)
                return true;
        foreach (var entity in _map.Entities)
        {
            var dx = point.X - entity.X;
            var dy = point.Y - entity.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) < 16) return true;
        }

        return false;
    }

    private EnemyNode? FindEnemy(string combatId) =>
        _enemies.FirstOrDefault(enemy => enemy.CombatId == combatId);

    private TargetStatus? CurrentTargetStatus()
    {
        var enemy = TargetOnMap(CombatRuntime.SelectedTargetId(ControlledId))
                    ?? TargetOnMap(PartySelectedTarget())
                    ?? TargetOnMap(SummonAwareness.Tactics(ControlledId, ActivePartySlots(), _focusTargetId)
                        .EngagedTargetIds.FirstOrDefault());
        return enemy is null ? null : TargetStatusOf(enemy, _mapRoster, _hero?.Level ?? 1);
    }

    private EnemyNode? TargetOnMap(string? combatId) =>
        combatId is { Length: > 0 } id ? _enemies.FirstOrDefault(enemy =>
            enemy.CombatId == id && !enemy.Dead) : null;

    private string? PartySelectedTarget()
    {
        foreach (var member in ActivePartySlots())
        {
            if (member == ControlledId) continue;
            if (CombatRuntime.SelectedTargetId(member) is { Length: > 0 } target) return target;
        }

        return null;
    }

    private TargetStatus TargetStatusOf(EnemyNode enemy, WorldEnemyRoster? roster, int playerLevel)
    {
        var snapshot = enemy.SpawnKey is { Length: > 0 } key
            ? roster?.EnemySnapshot(key, enemy.EnemyInfo, playerLevel)
            : null;

        return new TargetStatus(
            enemy.SpawnKey ?? enemy.CombatId,
            enemy.EnemyInfo.Name,
            snapshot?.Level ?? enemy.Fighter.Level,
            enemy.Health,
            snapshot?.MaxHealth ?? enemy.Fighter.MaxHealth,
            snapshot?.SceneLevel ?? false);
    }

    private Fighter? FindFighter(string id)
    {
        var panel = id == PlayerCombatId ? _panel : _party?.Get(id)?.Panel;
        if (panel is not null)
        {
            if ((VitalsOf(id)?.Health ?? 0) <= 0 && !_hits.IsAwaitingDefeat(id)) return null;
            return Conditions.FighterView(id, new Fighter
            {
                Level = panel.Level,
                PhysicalAttack = panel.PhysicalAttack,
                MagicAttack = panel.MagicAttack,
                Defense = panel.BaseDefense,
                DefenseRate = panel.DefenseRate,
                PhysicalResistance = panel.PhysicalResistance,
                MagicResistance = panel.BaseMagicResistance,
                MaxHealth = panel.MaxHealth,
                HitBase = panel.HitBase,
                HitBonus = panel.HitBonus,
                EvasionBase = panel.EvasionBase,
                EvasionBonus = panel.EvasionBonus,
                CriticalRate = panel.CriticalRate,
                CriticalResistance = panel.CriticalResistance,
                CriticalDamage = panel.CriticalDamage,
                CriticalDamageResistance = panel.CriticalDamageResistance,
                WeaponBlock = panel.WeaponBlock,
                ShieldBlock = panel.ShieldBlock,
            }, PassivesOf(id), new FighterEquipment { OffhandShield = panel.OffHandShield });
        }

        var enemy = FindEnemy(id);
        return enemy is null || enemy.Dead && !_hits.IsAwaitingDefeat(id)
            ? null : Conditions.FighterView(id, enemy.Fighter);
    }

    public (int Health, int Mana, int MaxMana, int MaxHealth)? VitalsOf(string id)
    {
        if (id == PlayerCombatId)
        {
            var player = _hero?.Player;
            if (player is null) return null;
            return (player.Value.Health, player.Value.Mana, player.Value.MaxMana, player.Value.MaxHealth);
        }

        if (_party?.Get(id) is { } member)
            return (member.Player.Health, member.Player.Mana, member.Player.MaxMana, member.Player.MaxHealth);

        var enemy = FindEnemy(id);
        if (enemy is null) return null;
        var snapshot = enemy.SpawnKey is { Length: > 0 } key
            ? _mapRoster?.EnemySnapshot(key, enemy.EnemyInfo, _hero?.Level ?? 1)
            : null;
        return snapshot is null ? null : (snapshot.Health, snapshot.Mana, snapshot.MaxMana, snapshot.MaxHealth);
    }

    private static readonly string[] AttackProcKeys =
    [
        "windStrikeChance", "fireSplashChance", "frostStrikeChance",
        "divineStrikeChance", "darknessStrikeChance", "poisonStrikeChance",
    ];

    private static readonly string[] NonNegativeKeys =
    [
        .. AttackProcKeys,
        "maxHealth", "maxMana", "healthRecoveryPerPulse", "manaRecoveryPerPulse",
        "manaCostRate", "manaCostReductionRate",
        "physicalAttack", "magicAttack", "defense", "defenseRate", "healingPowerRate",
        "threatGenerationRate", "threatReductionRate", "sightBonusUnits",
        "bloodThirstChance", "bloodThirstLifeDrainPerThousand", "bloodThirstManaRecovery",
        "revengeChance", "cooldownResetChance", "defenseCooldownResetChance",
        "offhandAttackChance", "frenzyAttackChance",
    ];

    /// <summary>
    /// 一条属性的当前值：容器里的临时修正优先，否则算基础值（面板／敌人战斗档／
    /// 默认表／装备词条），最后叠被动与增益。分支次序照搬。
    /// </summary>
    public int AttributeOf(string id, string key)
    {
        if (key is "maxHealth" or "maxMana") return ResourceCapacityOf(id, key);
        if (Conditions.CachedAttribute(id, key) is { } temporary) return temporary;

        var isPlayer = id == PlayerCombatId;
        var member = isPlayer ? null : _party?.Get(id);
        var isMember = isPlayer || member is not null;
        var panel = isPlayer ? _panel : member?.Panel;
        var enemy = isMember ? null : FindEnemy(id);
        var fighter = isPlayer ? _playerFighter : member?.Panel.ToFighter() ?? enemy?.Fighter;
        var level = isPlayer ? _hero?.Level ?? 1 : member?.Level ?? fighter?.Level ?? 0;
        var defaults = CombatResources.DefaultResourceAttributes(isMember ? "member" : "monster", level);

        if (key == "bestNormalDamage")
        {
            return fighter is null ? 0 : DamageFormula.BestNormalDamage(
                fighter.PhysicalAttack, fighter.PhysicalDamageRate, fighter.StealthDamageRate,
                fighter.MagicAttack, fighter.MagicDamageRate);
        }

        int original;
        if (Array.IndexOf(AttackProcKeys, key) >= 0)
        {
            original = isMember ? EquipmentAttributes.AttackProcs(EquippedInstances(id)).Of(key) : 0;
        }
        else if (key == "threatReductionRate")
        {
            original = isMember ? EquipmentAttributes.ThreatReduction(EquippedInstances(id)) : 0;
        }
        else if (key == "offhandAttackChance")
        {
            var bag = isPlayer ? _hero?.Bag : member?.Bag;
            var slots = bag?.EquipmentItems(bag.EquipmentFor(id));
            original = EquipmentAttributes.BaseOffhandAttackChance(isPlayer ? _classId : member?.ClassId ?? "",
                slots?.GetValueOrDefault("off-hand") is { Length: > 0 } offhand
                    ? _loader.Items.Items[offhand].Kind : "empty",
                slots?.GetValueOrDefault("body") is { Length: > 0 } body
                    ? _loader.Items.Items[body].Kind : "");
        }
        else if (key == "threatGenerationRate")
        {
            original = 1000;
        }
        else if (key is "healthRecoveryPerPulse" or "manaRecoveryPerPulse" or "manaCostRate" or "manaCostReductionRate")
        {
            var recovery = isMember ? EquipmentAttributes.Recovery(EquippedInstances(id)) : default;
            original = key switch
            {
                "healthRecoveryPerPulse" => defaults.HealthRecoveryPerPulse,
                "manaRecoveryPerPulse" => defaults.ManaRecoveryPerPulse + recovery.ManaRecoveryPerPulse,
                "manaCostRate" => defaults.ManaCostRate,
                _ => defaults.ManaCostReductionRate + recovery.ManaCostReductionRate,
            };
        }
        else
        {
            original = key switch
            {
                "defense" => panel?.BaseDefense ?? fighter?.Defense ?? 0,
                "magicResistance" => panel?.BaseMagicResistance ?? fighter?.MagicResistance ?? 0,
                "healingPowerRate" or "moveSpeed" => 1000,
                _ => FighterValue(fighter, key),
            };
        }

        var value = Conditions.Attribute(id, key, original, PassivesOf(id));
        if (key == "magicResistance") return Conditions.CachedAttribute(id, key) ?? Fighter.MagicResistanceLimit(value);
        return Array.IndexOf(NonNegativeKeys, key) >= 0 ? Math.Max(0, value) : value;
    }

    private float MovementSpeed(string actorId, float baseSpeed)
    {
        const float gmMultiplier = 1f;

        var party = actorId == ControlledId || ActivePartySlots().Contains(actorId);

        var enemy = FindEnemy(actorId);
        var inWater = enemy?.EnemyInfo.WaterImmune != true &&
                      FindCombatant(actorId) is { } actor && _grid.IsWater(actor.Position);

        return MovementSpeedRule.Apply(baseSpeed, party ? gmMultiplier : 1f,
            AttributeOf(actorId, "moveSpeed"), inWater);
    }

    private static int FighterValue(Fighter? fighter, string key) => fighter is null ? 0 : key switch
    {
        "criticalRate" => fighter.CriticalRate,
        "criticalResistance" => fighter.CriticalResistance,
        "criticalDamageResistance" => fighter.CriticalDamageResistance,
        "criticalDamage" => fighter.CriticalDamage,
        "physicalAttack" => fighter.PhysicalAttack,
        "physicalDamageRate" => fighter.PhysicalDamageRate,
        "magicAttack" => fighter.MagicAttack,
        "magicDamageRate" => fighter.MagicDamageRate,
        "conditionalMagicDamageRate" => fighter.ConditionalMagicDamageRate,
        "evasionBonus" => fighter.EvasionBonus,
        "evasionBase" => fighter.EvasionBase,
        "hitBonus" => fighter.HitBonus,
        "hitBase" => fighter.HitBase,
        "defenseRate" => fighter.DefenseRate,
        "weaponBlock" => fighter.WeaponBlock,
        "shieldBlock" => fighter.ShieldBlock,
        _ => 0,
    };

    private List<ItemInstance> EquippedInstances(string id)
    {
        var items = new List<ItemInstance>();
        var bag = id == PlayerCombatId ? _hero?.Bag : _party?.Get(id)?.Bag;
        if (bag is null) return items;

        foreach (var itemId in bag.EquipmentFor(id).Values)
        {
            var entry = bag.Get(itemId);
            if (entry is not null) items.Add(entry);
        }

        return items;
    }

    private IReadOnlyList<AttributeModifier> PassivesOf(string id) => PartyPassives(id);

    private IReadOnlyList<AttributeModifier> PartyPassives(string id)
    {
        var isPlayer = id == PlayerCombatId;
        var member = isPlayer ? null : _party?.Get(id);
        if (!isPlayer && member is null) return [];

        SkillConfiguration? configuration = isPlayer ? _skills : member!.Skills;
        var classId = isPlayer ? _classId : member!.ClassId;
        var panel = isPlayer ? _panel : member!.Panel;
        var health = isPlayer ? _playerHealth : member!.Player.Health;
        if (configuration is null || classId is null || panel is null) return [];

        var equipped = _bag.EquipmentItems(_bag.EquipmentFor(id));
        var weapon = equipped.TryGetValue("main-hand", out var main)
            ? _loader.Items.Items.GetValueOrDefault(main)?.Kind ?? "unarmed"
            : "unarmed";
        var armor = equipped.TryGetValue("body", out var body)
            ? _loader.Items.Items.GetValueOrDefault(body)?.Kind ?? string.Empty
            : string.Empty;

        return PartyCombat.MemberCombatModifiers(new MemberCombatModifierInput
        {
            Catalog = _loader.Skills,
            ClassId = classId,
            Loadout = configuration.Draft(_loader.Skills, classId),
            WeaponKind = weapon,
            ArmorKind = armor,
            Health = health,
            MaxHealth = panel.MaxHealth,
            GroupEffects = _session.GroupModifiers(),
            Stealthed = Conditions.HasState(id, "stealthed"),
            Growth = isPlayer ? "hero" : "companion",
        });
    }

    public int ResourceCapacityOf(string id, string key)
    {
        var (baseHealth, baseMana) = BaseResourceCapacity(id);
        var passives = PassivesOf(id);
        var baseValue = key == "maxHealth" ? baseHealth : baseMana;
        var signature = baseValue + "|" + JsonSerializer.Serialize(passives.Where(modifier => modifier.Attribute == key));
        Conditions.SetResourceCapacityInputs(id, key, signature);

        return Conditions.ReadResourceCapacity(id, key, () =>
        {
            var capacity = Math.Max(0, Conditions.Attribute(id, key, baseValue, passives));

            if (id == PlayerCombatId && _hero is not null)
                _hero.SetCombatResourceCapacity(key, capacity);
            else _party?.Get(id)?.SetCombatResourceCapacity(key, capacity);

            return capacity;
        });
    }

    private (int MaxHealth, int MaxMana) BaseResourceCapacity(string id)
    {
        if (id == PlayerCombatId) return (_panel.MaxHealth, _panel.MaxMana);
        if (_party?.Get(id) is { } member) return (member.Panel.MaxHealth, member.Panel.MaxMana);

        var enemy = FindEnemy(id);
        if (enemy is null) return (0, 0);

        var snapshot = enemy.SpawnKey is { Length: > 0 } key
            ? _mapRoster?.EnemySnapshot(key, enemy.EnemyInfo, _hero?.Level ?? 1)
            : null;
        return snapshot is null
            ? (enemy.Fighter.MaxHealth, enemy.EnemyInfo.Attributes.MaxMana)
            : (snapshot.MaxHealth, snapshot.MaxMana);
    }

    public void RestoreHealth(string id, int amount)
    {
        if (amount <= 0) return;

        if (id == PlayerCombatId)
        {
            var player = _hero?.Player;
            if (player is null) return;
            _hero!.SetHealth(Math.Min(player.Value.MaxHealth, player.Value.Health + amount));
            return;
        }

        if (_party?.Get(id) is { } member)
        {
            if (member.Alive) member.SetHealth(member.Player.Health + amount);
            return;
        }

        var enemy = FindEnemy(id);
        if (enemy is null || enemy.Dead || enemy.SpawnKey is not { Length: > 0 } key) return;
        var snapshot = _mapRoster?.EnemySnapshot(key, enemy.EnemyInfo, _hero?.Level ?? 1);
        if (snapshot is null || snapshot.Health <= 0) return;
        _mapRoster!.SetEnemyHealth(key, Math.Min(snapshot.MaxHealth, snapshot.Health + amount), enemy.EnemyInfo);
    }

    public void RestoreMana(string id, int amount)
    {
        if (amount <= 0) return;

        if (id == PlayerCombatId)
        {
            var player = _hero?.Player;
            if (player is null) return;
            _hero!.SetMana(Math.Min(player.Value.MaxMana, player.Value.Mana + amount));
            return;
        }

        if (_party?.Get(id) is { } member)
        {
            member.SetMana(member.Player.Mana + amount);
            return;
        }

        var enemy = FindEnemy(id);
        if (enemy is null || enemy.Dead || enemy.SpawnKey is not { Length: > 0 } key) return;
        var snapshot = _mapRoster?.EnemySnapshot(key, enemy.EnemyInfo, _hero?.Level ?? 1);
        if (snapshot is null || snapshot.Health <= 0) return;
        _mapRoster!.SetEnemyMana(key, Math.Max(0, Math.Min(snapshot.MaxMana, snapshot.Mana + amount)), enemy.EnemyInfo);
    }

    public bool IsInvincible(string id) => _invincible.Contains(id);

    public void SetInvincible(string id, bool enabled)
    {
        if (enabled) _invincible.Add(id);
        else _invincible.Remove(id);
    }

    public Combatant? FindCombatant(string id)
    {
        if (id == PlayerCombatId) return PlayerCombatant();
        // 队伍成员：零生命的成员不给出去——敌人不能把
        // 已经倒下的伙伴当目标（治疗选人走 partyBuffSlots，不看这里）。
        if (FindAllyNode(id) is { } ally)
            return _party is not null && _party.Alive(id) ? AllyCombatant(ally) : null;
        var enemy = FindEnemy(id);
        return enemy is null || enemy.Dead ? null : EnemyCombatant(enemy);
    }

    private PartyAllyNode? FindAllyNode(string memberId) =>
        _allies.FirstOrDefault(ally => ally.MemberId == memberId) is { } ally &&
        GodotObject.IsInstanceValid(ally.Node)
            ? ally.Node
            : null;

    public IReadOnlyList<Combatant> AllCombatants()
    {
        var list = new List<Combatant> { PlayerCombatant() };
        foreach (var ally in _allies) list.Add(AllyCombatant(ally.Node));
        foreach (var enemy in _enemies)
            if (!enemy.Dead) list.Add(EnemyCombatant(enemy));

        return list;
    }

    public BattleContent BattleContent => _battle ??= _loader.Battle;

    public Func<double> RandomSource => _random.NextDouble;

    public void PoseCombatFrame(CombatAction action, int index)
    {
        var node = ResolveActorNode(action.Caster.Id);
        if (node is null) return;

        if (action.Skill is { } skill)
        {
            var name = skill.Clips?.GetValueOrDefault(action.Facing)
                       ?? SkillActionRules.SkillClipName(skill.Id, action.Facing);
            if (!node.Definition.HasClip(name)) return;
            if (node.Visual.Animation != name) node.Visual.Play(name);
            return;
        }

        if (action.BasicAttack is { } attack) node.DrawAttackFrame(attack, action.Facing, index);
    }

    private ActorNode? ResolveActorNode(string id)
    {
        if (id == PlayerCombatId) return _player;
        var ally = FindAllyNode(id);
        if (ally is not null) return ally;
        var enemy = FindEnemy(id);
        return enemy is { Dead: false } ? enemy : null;
    }

    public bool IsCombatantAlive(string id)
    {
        if (id == PlayerCombatId) return _playerHealth > 0;
        if (_party is not null && _party.Get(id) is not null) return _party.Alive(id);
        var enemy = FindEnemy(id);
        return enemy is { Dead: false };
    }

    public void SpawnAttackEffect(string name, string variant, Vec2 position, string? placement, string? recipientId = null)
    {
        EffectSpawnedHook?.Invoke(name, variant);
        var clip = BattleContent.EffectClip(name, variant);
        if (clip is null)
        {
            GD.PushWarning($"[effect] 战斗特效缺失：{name}（{variant}）");
            return;
        }

        var recipient = recipientId is not null ? ResolveActorNode(recipientId) : null;
        if (placement == "attached")
        {
            if (recipient is null) return;
            var anchor = BattleContent.AttackEffects.GetValueOrDefault(name)?.AttachmentPoint == "feet"
                ? new Vec2(0, 0)
                : recipient.Definition.EffectAnchor is { } point ? new Vec2(point.X, point.Y) : Vec2.Zero;
            position = anchor;
        }

        var sprite = EffectSprite.Create(clip, _mapLease!, position.X, position.Y);
        if (sprite is null) return;
        if (placement == "attached")
        {
            sprite.ZIndex = 1;
            recipient!.AddChild(sprite);
            return;
        }
        if (placement == "above-characters") sprite.ZIndex = AboveCharactersZIndex;
        else if (placement == "ground") sprite.ZIndex = -1;
        _mapRoot?.AddChild(sprite);
    }

    /// <summary>
    /// 「压在人物之上」的层号。原版按层深度排；这里沿用地图自己的三档
    /// （地面 -1000／人物按 Y／树冠 1000），取比树冠高一档。
    /// 注意 Godot 的 CanvasItem 层号上限是 4096，不能随手写个大数。
    /// </summary>
    private const int AboveCharactersZIndex = 1001;

    public void ApplyFlightEvent(FlightEvent flight)
    {
        switch (flight)
        {
            case FlightStartEvent start:
            {
                var sprite = SpawnFlightVisual(start.Effect, start.Position, start.Heading,
                    start.Directional, start.AboveCharacters);
                if (sprite is not null)
                    _flights[start.Id] = new FlightVisual(sprite, start.Effect, start.Directional,
                        start.Heading, start.AboveCharacters);
                break;
            }

            case FlightStepEvent step:
            {
                if (!_flights.TryGetValue(step.Id, out var moving)) break;

                if (moving.Directional && moving.Heading != step.Heading)
                {
                    if (GodotObject.IsInstanceValid(moving.Node)) moving.Node.QueueFree();
                    var rebuilt = SpawnFlightVisual(moving.Effect, step.Position, step.Heading,
                        moving.Directional, moving.AboveCharacters);
                    if (rebuilt is not null)
                        _flights[step.Id] = moving with { Node = rebuilt, Heading = step.Heading };
                    break;
                }

                if (GodotObject.IsInstanceValid(moving.Node))
                    moving.Node.Position = new Vector2(step.Position.X, step.Position.Y);
                break;
            }

            case FlightEndEvent end:
                if (!_flights.Remove(end.Id, out var done)) break;
                if (GodotObject.IsInstanceValid(done.Node)) done.Node.QueueFree();
                break;
        }
    }

    private readonly record struct FlightVisual(Node2D Node, string Effect, bool Directional, int Heading,
        bool AboveCharacters);

    private readonly Dictionary<int, FlightVisual> _flights = [];

    /// <summary>
    /// 在途特效：照 取片段——定向弹道用 <c>heading-&lt;角度&gt;</c>，
    /// 其余用 <c>default</c>。
    ///
    /// 之前把角度折成四向（south/east/…）再取，可弓箭与魔法弹的素材只有
    /// <c>heading-0/45/…</c> 这一套角度键、四向一个都没有，于是整支箭／法术弹取不到片段，
    /// 表现就是「游侠和巫术师平 A 没特效」。四向只该用在人物动作上，弹道不能这么折。
    /// </summary>
    private Node2D? SpawnFlightVisual(string effect, Vec2 position, int heading, bool directional,
        bool aboveCharacters)
    {
        var variant = directional ? $"heading-{heading}" : "default";
        var clip = BattleContent.EffectClip(effect, variant);
        if (clip is null)
        {
            GD.PushWarning($"[effect] 在途特效缺失：{effect}（{variant}）");
            return null;
        }

        var sprite = EffectSprite.Create(clip, _mapLease!, position.X, position.Y, loop: true);
        if (sprite is null) return null;
        sprite.ZIndex = aboveCharacters ? AboveCharactersZIndex : (int)position.Y + 1;
        _mapRoot?.AddChild(sprite);
        return sprite;
    }

    public void FlashScreen(float transparency)
    {
        if (transparency <= 0f) return;
        var color = new Color(1f, 1f, 1f, Mathf.Clamp(transparency, 0f, 1f));
        _fadeTween?.Kill();
        _fade.Color = color;
        _fade.Modulate = new Color(1f, 1f, 1f, 1f);
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(_fade, "modulate:a", 0f, .18f);
    }

    public void NotifyCombat(string name, IReadOnlyDictionary<string, object?> data)
    {
        if (!_combatNotices) return;
        GD.Print($"[combat] {name}：{string.Join("，", data.Select(pair => $"{pair.Key}={pair.Value}"))}");
    }

    private bool _combatNotices;

    public bool CanSummonAt(Vec2 position, CollisionBox bounds) =>
        _grid is not null && _grid.CanStandInRect(new Rect(
            position.X + bounds.X, position.Y + bounds.Y, bounds.Width, bounds.Height));

    public Vec2? NearestSummonSpot(Vec2 position, CollisionBox bounds, Vec2? preferred = null)
    {
        if (preferred is { } want && CanSummonAt(want, bounds)) return want;

        const int grid = 8;
        for (var radius = grid; radius <= 96; radius += grid)
        for (var dx = -radius; dx <= radius; dx += grid)
        for (var dy = -radius; dy <= radius; dy += grid)
        {
            if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
            var spot = new Vec2(position.X + dx, position.Y + dy);
            if (CanSummonAt(spot, bounds)) return spot;
        }

        return null;
    }

    public void BeginSummons(string ownerId) => _summonsByOwner[ownerId] = [];

    private readonly Dictionary<string, List<string>> _summonsByOwner = [];

    public string? SummonCreature(string ownerId, string enemyId, Vec2 position, SummonStats? stats = null)
    {
        var definition = _loader.Battle.Enemies.FirstOrDefault(enemy => enemy.Id == enemyId);
        if (definition is null)
        {
            GD.PushWarning($"[summon] 找不到敌人定义：{enemyId}");
            return null;
        }

        var node = new EnemyNode { Name = $"summon-{++_summonSerial}" };
        _entities.AddChild(node);
        node.SetupEnemy(definition, _textures);
        if (stats is not null) node.ApplySummonStats(stats);
        node.SummonedBy = ownerId;
        node.TeleportTo(new Vector2(position.X, position.Y));
        node.Home = position;
        _enemies.Add(node);

        AttachEnemyCombat(node);
        var id = node.CombatId;
        if (!_summonsByOwner.TryGetValue(ownerId, out var list)) _summonsByOwner[ownerId] = list = [];
        list.Add(id);
        return id;
    }

    public void SetManaAbsolute(string id, int value)
    {
        if (id == PlayerCombatId)
        {
            _hero?.SetMana(value);
            return;
        }

        if (_party?.Get(id) is { } member)
        {
            member.SetMana(value);
            return;
        }

        var enemy = FindEnemy(id);
        if (enemy is null || enemy.Dead || enemy.SpawnKey is not { Length: > 0 } key) return;
        _mapRoster?.SetEnemyMana(key, value, enemy.EnemyInfo);
    }

    private int _summonSerial;

    private CombatController? _playerCombat;

    private Inotia4.Rendering.CombatFeedbackView? _feedbackView;

    private Inotia4.Rendering.WardrobeOutfits? _outfits;

    private Inotia4.Domain.Content.ActorDefinition _heroBase = null!;

    private Inotia4.Rendering.WardrobeOutfits.Outfit? _playerOutfit;

    private Inotia4.Rendering.CombatOutcomeView? _outcomes;

    private SceneActionPort? _actionPort;

    private CombatActionDispatcher? _dispatcher;

    public bool StoryPlaying => _storyPlaying;

    public string PlayerFacing => _player.Facing;

    public Rect MapBounds => _grid.Bounds;

    public CombatState PlayerActionState => _playerCombat?.ActionState() ?? CombatState.Idle;

    private ResolvedBasicAttack? _playerAttack;

    public Action<string, string>? EffectSpawnedHook;

    private void EnsurePlayerCombat()
    {
        if (_playerCombat is not null) return;

        _playerAttack = ResolvePlayerAttack();
        var port = new PlayerCombatPort(this);
        _playerCombat = new CombatController(PlayerCombatId, _playerAttack, port);
        CombatRuntime.RegisterActor(PlayerCombatId, _playerCombat);
        _heroAi ??= new PartyController(PlayerCombatId, new HeroPartyPort(this), _playerCombat);
    }

    public void NoticeSkillSound(SkillPlayback skill) => _audio?.PlayerSkill(skill.Id);

    public Inotia4.Domain.Content.SkillCatalog SkillCatalog => _loader.Skills;

    public int PlayerMana => _hero?.Player.Mana ?? 0;

    public int PlayerHealth => _playerHealth;

    public bool ActiveSummonsOf(string ownerId) =>
        _summonsByOwner.TryGetValue(ownerId, out var list) &&
        list.Any(id => FindEnemy(id) is { Dead: false });

    public bool PlayerHasOffhandShield =>
        WardrobeRules.HasOffhandShield(_loader.Items,
            _bag.EquipmentItems(_bag.EquipmentFor(PlayerCombatId)), _loader.Wardrobe.DefaultShield);

    public bool SpendPlayerMana(int cost) => _hero is { } hero && hero.SpendMana(cost);

    /// <summary>
    /// 全场的技能冷却账本。
    /// 它读会话时钟，所以归会话所有：读档回来这一场技能还该在冷却里。
    /// </summary>
    private CombatCooldownStore _cooldowns => _session.Cooldowns;

    public IActorCooldownPort CooldownsFor(string id) => _cooldowns.ForActor(id);

    public bool SkillSlotAvailable(string skillId)
    {
        if (_playerCombat is null || _skills is null) return false;
        var definition = _loader.Skills.Skills.GetValueOrDefault(skillId);
        if (definition?.Action is null) return false;
        if (!CooldownsFor(PlayerCombatId).IsReady(skillId)) return false;
        return new PlayerCombatPort(this).CanSelectSkill(new SkillPlayback(skillId, definition.Action,
            _skills.Draft(_loader.Skills, _classId).LevelOf(skillId)));
    }

    public string? PlayerCastSkill(string skillId)
    {
        if (!CanControl()) return "busy";
        var catalog = _loader.Skills;
        var definition = catalog.Skills.ContainsKey(skillId)
            ? catalog.SkillForGrowth(skillId, ControlledId != PlayerCombatId) : null;
        if (definition?.Action is null) return "no-skill";

        if (ControlledId != PlayerCombatId)
        {
            var ally = _allies.FirstOrDefault(entry => entry.MemberId == ControlledId);
            var member = _party?.Get(ControlledId);
            if (ally is null || member is null) return "unavailable";
            var memberLevel = member.Skills.Draft(catalog, member.ClassId).LevelOf(skillId);
            if (memberLevel < 1) return "not-equipped";
            var memberPlayback = new SkillPlayback(skillId, definition.Action, memberLevel);
            return ally.Ai.CombatController.RequestManualSkill(memberPlayback) switch
            {
                "busy" => "busy",
                "unavailable" => "unavailable",
                "no-mana" => "no-mana",
                "cooldown" => "cooldown",
                "no-target" => "no-target",
                _ => null,
            };
        }

        if (_skills is null || !_skills.IsEquipped(skillId)) return "not-equipped";

        EnsurePlayerCombat();
        var loadout = _skills.Draft(catalog, _classId);
        var level = loadout.LevelOf(skillId);
        if (level < 1) return "not-equipped";

        var playback = new SkillPlayback(skillId, definition.Action, level);
        return _playerCombat!.RequestManualSkill(playback) switch
        {
            "busy" => "busy",
            "unavailable" => "unavailable",
            "no-mana" => "no-mana",
            "cooldown" => "cooldown",
            "no-target" => "no-target",
            _ => null,
        };
    }

    public void NotifySkillContact(AttackContact contact)
    {
        CountPartyHit(contact.CasterId, contact.TargetId);
        var target = FindEnemy(contact.TargetId);
        target?.Ai?.OnContact(contact.CasterId, contact.TargetId);
    }

    public void SettleAreaContact(BasicAreaContact contact)
    {
        _hits.Run(() =>
        {
            var caster = FindCombatant(contact.CasterId);
            var targeting = contact.BasicAttack.Definition.Targeting;
            if (caster is null || targeting is null) return true;

            var rangeBonus = AttributeOf(contact.CasterId, "attackRangeBonusUnits");
            var cue = new SkillEffectCue
            {
                Target = targeting.Target,
                TargetRangePx = targeting.TargetRangePx,
                TargetRangeMetric = targeting.TargetRangeMetric,
            };
            var targets = CueTargets.Resolve(cue, caster, null, AllCombatants(), rangeBonusUnits: rangeBonus);
            foreach (var target in targets.Take(SkillUnitSequence.MaxSkillTargets))
                SettleBasicAttackContact(contact.CasterId, target.Id, "main-hand", contact.BasicAttack);
            return true;
        });
    }

    public void RunInHitResolution(Action work) => _hits.Run(() =>
    {
        work();
        return true;
    });

    public void ShowUnitFeedback(SkillUnitHit hit, SkillUnitOutcome outcome, SkillEffectCue cue)
    {
        if (outcome == SkillUnitOutcome.Skipped) return;
        if (outcome == SkillUnitOutcome.Failure && cue.UnitFeedbackPolicy != "always") return;

        foreach (var visual in cue.Effects)
        {
            var id = visual.Recipient == "caster" ? hit.CasterId : hit.TargetId;
            var actor = FindCombatant(id);
            if (actor is not null)
                SpawnAttackEffect(visual.Effect, "default", actor.Position, visual.Placement, id);
        }
    }

    private ResolvedBasicAttack? ResolvePlayerAttack()
    {
        try
        {
            return EquipmentAttack(_player.Definition);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[combat] {_classId} 的普攻无法解析，主角按不能普攻处理：{error.Message}");
            return null;
        }
    }

    /// <summary>
    /// 某个角色当前装备的普攻：按换装配方取这组武器的规则，片段名是写死的四向攻击
    /// （人物动作本身由那一组武器的姿态给，所以匕首、双手剑、法杖、弓各有各的挥法）。
    /// </summary>
    private ResolvedBasicAttack EquipmentAttack(ActorDefinition actor, string memberId = PlayerCombatId,
        string? classId = null)
    {
        var recipe = CostumeRecipeFor(memberId, classId)
                     ?? throw new InvalidOperationException($"{memberId} 没有换装配方");
        if (recipe.Attack is not { } attack) throw new InvalidOperationException("这组武器缺少普攻规则");
        return BasicAttackRules.Resolve(new Dictionary<string, BasicAttackDefinition> { ["equipped"] = attack },
            actor, new BasicAttackBinding
            {
                ProfileId = "equipped",
                Clips = new Dictionary<string, string>
                {
                    ["north"] = "attack-north",
                    ["south"] = "attack-south",
                    ["east"] = "attack-east",
                    ["west"] = "attack-west",
                },
            });
    }

    private CostumeRecipe? CostumeRecipeFor(string id, string? classId = null)
    {
        try
        {
            // 换装配方要的是**物品 id**；背包槽位里存的是条目 id，必须经 EquipmentItems 换算。
            // 职业只影响技能姿态的覆盖，所以要按这名成员自己的职业取（伙伴不是主角那个职业）。
            return WardrobeRules.CostumeRecipe(_loader.Wardrobe, _loader.Items,
                _bag.EquipmentItems(_bag.EquipmentFor(id)), classId ?? _classId);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[combat] {id} 的换装配方无法计算：{error.Message}");
            return null;
        }
    }

    private ResolvedBasicAttack ResolveWardrobeAttack(WardrobeAttack attack, string profileId,
        ActorDefinition actor) =>
        BasicAttackRules.Resolve(new Dictionary<string, BasicAttackDefinition> { [profileId] = attack },
            actor, new BasicAttackBinding { ProfileId = profileId, Clips = attack.Clips });

    public BasicAttackSelection SelectPlayerBasicAttack(ResolvedBasicAttack normal) =>
        SelectBasicAttack(PlayerCombatId, _classId, normal);

    public BasicAttackSelection SelectBasicAttack(string memberId, string classId, ResolvedBasicAttack normal)
    {
        if (_bag is null) return BasicAttackSelection.Of(normal);
        var actor = ActorDefinitionOf(memberId) ?? _player.Definition;
        var equipment = _bag.EquipmentItems(_bag.EquipmentFor(memberId));
        var offhand = equipment.GetValueOrDefault("off-hand");
        // 副手拿的算不算「一件武器」看物品自己的 dualWield 标记（ 的
        // `rule.dualWield`）：双手剑、弓、法杖同样占 main-hand 槽，却不能拿来双持。
        var offhandIsMainHandWeapon = offhand is not null &&
            _loader.Items.Items.GetValueOrDefault(offhand)?.Equipment is { DualWield: true };

        if (EquipmentAttributes.ChooseDualWield(classId, offhandIsMainHandWeapon,
                _random.NextDouble, () => AttributeOf(memberId, "offhandAttackChance")))
        {
            var profile = CostumeRecipeFor(memberId, classId)?.DualWieldAttack;
            if (profile is null) return BasicAttackSelection.Of(normal);
            return BasicAttackSelection.Of(ResolveWardrobeAttack(profile, "dual-wield", actor));
        }

        if (AttributeOf(memberId, "frenzyAttackChance") <= 0) return BasicAttackSelection.Of(normal);
        var roll = (int)Math.Floor(_random.NextDouble() * 999) + 1;
        if (roll >= AttributeOf(memberId, "frenzyAttackChance")) return BasicAttackSelection.Of(normal);

        var frenzyRecipe = CostumeRecipeFor(memberId, classId);
        if (frenzyRecipe?.FrenzyAttack is null && frenzyRecipe?.FrenzyWaitFrames is null)
            throw new InvalidOperationException("当前装备缺少战栗的狂气动作定义");
        var durationFrames = _session.FrenzyAnimations.Select(memberId,
            frenzyRecipe?.FrenzyAttack?.CycleFrames,
            frenzyRecipe?.FrenzyWaitFrames ?? frenzyRecipe!.FrenzyAttack!.CycleFrames);
        return frenzyRecipe?.FrenzyAttack is { } frenzy
            ? BasicAttackSelection.Of(ResolveWardrobeAttack(frenzy, "frenzy", actor))
            : BasicAttackSelection.Wait(durationFrames);
    }

    public void UpdateEquipment()
    {
        var recipe = CostumeRecipeFor(PlayerCombatId);
        if (recipe is not null)
            _session.FrenzyAnimations.UpdateEquipment(PlayerCombatId, recipe.FrenzyAttack?.CycleFrames);

        _playerAttack = ResolvePlayerAttack();
        if (_playerCombat is null) return;
        _playerCombat.Cancel(true);
        if (_playerAttack is { } attack) _playerCombat.SetBasicAttack(attack);
    }

    private ActorDefinition? ActorDefinitionOf(string memberId)
    {
        if (memberId == PlayerCombatId) return _player?.Definition;
        var ally = _allies.FirstOrDefault(entry => entry.MemberId == memberId);
        return ally is not null && GodotObject.IsInstanceValid(ally.Node) ? ally.Node.Definition : null;
    }

    private readonly Dictionary<string, string> _memberEquipmentSignatures = [];

    private void ProjectMemberEquipment()
    {
        if (_bag is null) return;
        foreach (var ally in _allies.ToList())
        {
            var equipment = _bag.EquipmentItems(_bag.EquipmentFor(ally.MemberId));
            var signature = string.Join(",",
                equipment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
            if (_memberEquipmentSignatures.TryGetValue(ally.MemberId, out var last) && last == signature) continue;
            _memberEquipmentSignatures[ally.MemberId] = signature;

            var classId = MemberClassId(ally.MemberId);
            if (CostumeRecipeFor(ally.MemberId, classId) is { } recipe)
                _session.FrenzyAnimations.UpdateEquipment(ally.MemberId, recipe.FrenzyAttack?.CycleFrames);

            var attack = ResolveMemberAttack(ally.MemberId, classId, ally.Node.Definition);
            if (attack is null) continue;
            _memberAttacks[ally.MemberId] = attack;
            ally.Ai.CombatController.Cancel(true);
            ally.Ai.CombatController.SetBasicAttack(attack);
        }
    }

    private string _playerEquipmentSignature = string.Empty;

    private void ProjectPlayerEquipment()
    {
        if (_bag is null || _hero is null) return;
        var equipment = _bag.EquipmentItems(_bag.EquipmentFor(PlayerCombatId));
        var signature = string.Join(",",
            equipment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        if (signature == _playerEquipmentSignature) return;
        _playerEquipmentSignature = signature;
        ApplyPlayerOutfit();
        UpdateEquipment();
    }

    private Inotia4.Domain.Content.ActorDefinition? StageMemberAppearance(string memberId)
    {
        if (_outfits is null || _bag is null || _party is null) return null;
        var member = _party.Get(memberId);
        if (member is null) return null;

        var baseActor = _loader.World.Actors.GetValueOrDefault(member.Definition.Actor);
        if (baseActor is null) return null;

        var outfit = _outfits.Prepare(baseActor, _bag.EquipmentItems(_bag.EquipmentFor(memberId)),
            member.ClassId, baseActor.Id, _outfits.ChannelFor($"stage:{memberId}"));
        return outfit.Actor;
    }

    private void ApplyPlayerOutfit()
    {
        if (_outfits is null || _heroBase is null || _bag is null || _player is null) return;

        var equipment = _bag.EquipmentItems(_bag.EquipmentFor(PlayerCombatId));
        var outfit = _outfits.Prepare(_heroBase, equipment, _classId, _heroBase.Id,
            _outfits.ChannelFor("player"));
        if (ReferenceEquals(_playerOutfit, outfit)) return;

        _playerOutfit = outfit;
        _player.SetAppearance(outfit.Actor);
        if (_overhead is not null && GodotObject.IsInstanceValid(_overhead))
            _player.MoveChild(_overhead, _player.GetChildCount() - 1);
        _stage?.SetHeroAppearance(outfit.Actor);
    }

    public Action PlayCombatAction(CombatAction action, Action<int> frame, Action complete)
    {
        var node = ResolveActorNode(action.Caster.Id);
        if (node is null)
        {
            complete();
            return () => { };
        }

        if (action.Caster.Id == PlayerCombatId)
        {
            FacePlayer(action.Facing);
            _combatApproach = null;
        }

        action.GetAttackSpeedRate = () =>
            Conditions.Attribute(action.Caster.Id, "attackSpeedRate", 1000, PassivesOf(action.Caster.Id));

        if (action.BasicAttack is { } attack) node.BeginAttack(attack, action.Facing);

        var sourceFrames = action.BasicAttack is { } basic
            ? (basic.Clips.TryGetValue(action.Facing, out var name) &&
               node.Definition.Clips.TryGetValue(name, out var clip) ? clip.Frames : 1)
            : 1;
        var frameMs = action.BasicAttack?.FrameDurationMs ?? CombatClock.LogicFrameMs;

        var cancel = CombatRuntime.Play(action, new ActionTiming(sourceFrames, frameMs), frame, () =>
        {
            if (GodotObject.IsInstanceValid(node)) node.EndAttack();
            complete();
        });

        return () =>
        {
            cancel();
            if (GodotObject.IsInstanceValid(node)) node.EndAttack();
        };
    }

    public void DispatchCombatFrame(CombatAction action, int index, Action<string, string?> contact,
        SkillUnitsReady unitsReady, Action? areaContact)
    {
        _actionPort ??= new SceneActionPort(this);
        _dispatcher ??= new CombatActionDispatcher(_actionPort);
        if (action.Caster.Id == PlayerCombatId) _playerDispatchedFrames += 1;

        _hits.Run(() =>
        {
            // 命中的表现反馈：震屏、震动与一条音效。
            // 音效只给玩家阵营的施法者——与原版口径一致，否则切走后怪物技能会静音。
            void Feedback(FeedbackEvent feedback)
            {
                if (feedback.Shake == true) _feedbackView?.StartShake();
                if (feedback.Sound is { } sound && PartySideOf(action.Caster.Id)) _audio?.Effect(sound);
            }

            void Dispatch(ActionDispatchDefinition definition, Action work, Action? afterDispatch)
            {
                _hits.Run(() =>
                {
                    ActionDispatch.Run(definition.BreaksStealth, new ActionDispatchPort(this, action.Caster.Id,
                        work, definition));
                    return true;
                });
                afterDispatch?.Invoke();
            }

            _dispatcher.Frame(action, index,
                (targetId, hand) => contact(targetId, hand), unitsReady,
                feedback: Feedback,
                dispatch: Dispatch,
                area: areaContact);
            return true;
        });
    }

    public void SettlePlayerContact(string targetId, string? hand)
    {
        _playerContacts += 1;
        Haptic(18);
        SettleBasicAttackContact(PlayerCombatId, targetId, hand, _playerAttack);
    }

    private void Haptic(int milliseconds)
    {
        if (_displaySettings is not { Haptics: true }) return;
        Input.VibrateHandheld(milliseconds);
    }

    private void SettleBasicAttackContact(string casterId, string targetId, string? hand,
        ResolvedBasicAttack? attack)
    {
        _hits.Run(() =>
        {
            RecordSummonContact(casterId, targetId);
            CountPartyHit(casterId, targetId);
            if (FindEnemy(targetId) is { } hitEnemy && !hitEnemy.Dead)
            {
                hitEnemy.Ai?.OnContact(casterId, targetId);
                hitEnemy.SummonAi?.OnContact(new AttackContact(casterId, targetId, Hand: hand));
            }
            if (_allies.FirstOrDefault(ally => ally.MemberId == targetId) is { } hitAlly)
                hitAlly.Ai.OnContact(new AttackContact(casterId, targetId, Hand: hand));

            if (!Conditions.CanLoseHealth(targetId))
            {
                ShowHitFeedback(new HitResult(HitKind.Hit, 0), targetId,
                    casterId == PlayerCombatId, attack?.Definition.HitEffectStyle ?? "weapon");
                return true;
            }

            var source = FindFighter(casterId);
            var target = FindFighter(targetId);
            if (source is null || target is null || attack is null) return true;

            var damage = attack.Definition.Damage;
            var damageKind = string.IsNullOrEmpty(damage.Effect) ? "physical" : damage.Effect;
            var attributes = ActorAttributesOf(casterId);
            if (attributes is null) return true;

            var rear = damageKind == "physical" ? RearPenetrationBonus(casterId, targetId) : 0;

            var prepared = DamageFormula.Prepare(damage, 1, attributes.Value.Primary);
            var amount = prepared is null
                ? 0
                : DamageFormula.Resolve(prepared, DamageSourceValue(attributes.Value, prepared.Source));

            var hit = damageKind == "magic"
                ? HitResolver.RollMagic(source, target, _random.NextDouble, amount)
                : HitResolver.RollPhysical(source, target, _random.NextDouble, amount, rear);

            ResolveHitReactions(new AttackContact(casterId, targetId, Hand: hand, BasicAttack: attack),
                hit, damageKind, "basic-attack",
                FindCombatant(targetId)?.Position);
            SettleHit(casterId, targetId, hit, attack.Definition.HitEffectStyle ?? "weapon");

            if (targetId == PlayerCombatId && hit.Landed) Haptic(28);
            return true;
        });
    }

    public void ClearPlayerActionVisuals()
    {
        CombatRuntime.Cancel(PlayerCombatId);
        _player.EndAttack();
    }

    private int _playerDispatchedFrames;

    private int _playerContacts;

    private Vec2? _combatApproach;

    public void ApproachCombatTarget(Vec2 target)
    {
        _combatApproach = target;
        var delta = target - _position;
        if (delta.Length > 0.01f) FacePlayer(BasicAttackRules.FaceTarget(_position, target));
    }

    public void StopCombatApproach() => _combatApproach = null;

    public void FacePlayer(string facing) => _player.SetFacing(facing);

    public void PlacePlayer(Vec2 position)
    {
        _session.Moved(position, _player.Facing);
        _player.TeleportTo(new Vector2(position.X, position.Y));
    }

    /// <summary>主角的碰撞盒在这个位置能不能整盒站住。</summary>
    public bool CanPlayerStandAt(Vec2 position)
    {
        if (_grid is null) return false;
        var bounds = _player.Definition.Collision;
        return _grid.CanStandInRect(new Rect(position.X + bounds.X, position.Y + bounds.Y,
            bounds.Width, bounds.Height));
    }

    /// <summary>
    /// 这个点能不能走到。原版用 <c>navigation.path(actor, position, 512)</c> 判空；
    /// 这里只有走位路线会做 A*，所以这一轮按「不检查」处理——它只影响尚未接入的技能位移。
    /// </summary>
    public bool? CanPlayerReach(Vec2 position)
    {
        _ = position;
        return null;
    }

    private void FollowCombatApproach(Vec2 target, float seconds)
    {
        var delta = target - _position;
        var distance = delta.Length;
        if (distance < 1e-4f)
        {
            _player.UpdateMotion(Vec2.Zero, false);
            return;
        }

        var direction = new Vec2(delta.X / distance, delta.Y / distance);
        var stepLength = MathF.Min(MovementSpeed(PlayerCombatId, PlayerSpeed) * seconds, distance);
        var moved = MovePlayer(direction * stepLength);
        _player.UpdateMotion(direction, moved);
    }

    private CombatThreatActor? FindThreatActor(string id)
    {
        if (id == PlayerCombatId)
            return new CombatThreatActor
            {
                Id = PlayerCombatId,
                Team = "party",
                Position = _position,
                Bounds = _player.Definition.Collision,
                Active = _playerHealth > 0,
                Level = _playerFighter.Level,
                SightRangeUnits = 0,
                TracksThreat = false,
                Role = CombatThreatRole.Player,
            };

        var ally = FindAllyNode(id);
        if (ally is not null && _party?.Get(id) is { } member)
            return new CombatThreatActor
            {
                Id = id,
                Team = "party",
                Position = new Vec2(ally.Position.X, ally.Position.Y),
                Bounds = ally.Definition.Collision,
                Active = member.Alive,
                Level = member.Player.Level,
                SightRangeUnits = 0,
                TracksThreat = false,
                Role = CombatThreatRole.Member,
            };

        var enemy = FindEnemy(id);
        if (enemy is null) return null;

        var friendly = enemy.SummonedBy is { } owner &&
                       (owner == PlayerCombatId || _party?.Get(owner) is not null);
        return new CombatThreatActor
        {
            Id = enemy.CombatId,
            Team = friendly ? "party" : "hostile",
            Position = new Vec2(enemy.Position.X, enemy.Position.Y),
            Bounds = enemy.Definition.Collision,
            Active = !enemy.Dead,
            Level = enemy.Fighter.Level,
            SightRangeUnits = EnemyEngagement.AwarenessRangeUnits(
                enemy.EnemyInfo.Behavior,
                enemy.EnemyInfo.Behavior.Engagement == "map-wide" ? _grid.Bounds : null),
            TracksThreat = !friendly,
            Role = friendly ? CombatThreatRole.Summon : CombatThreatRole.Enemy,
        };
    }

    private IReadOnlyList<CombatThreatActor> AllThreatActors()
    {
        var list = new List<CombatThreatActor>();
        if (FindThreatActor(PlayerCombatId) is { } player) list.Add(player);
        foreach (var ally in _allies)
            if (FindThreatActor(ally.MemberId) is { } member) list.Add(member);
        foreach (var enemy in _enemies.ToArray())
            if (FindThreatActor(enemy.CombatId) is { } actor) list.Add(actor);

        return list;
    }

    private void KillEnemy(EnemyNode enemy, string? killerId = null)
    {
        CancelEnemyAction(enemy);
        _enemies.Remove(enemy);
        _threat?.RemoveActor(enemy.CombatId);
        ForgetFocusedEnemy(enemy.CombatId);
        foreach (var other in _enemies)
        {
            other.Ai?.TargetRemoved(enemy.CombatId);
            other.SummonAi?.TargetRemoved(enemy.CombatId);
        }
        _playerCombat?.TargetRemoved(enemy.CombatId);
        _audio?.MonsterDeath(enemy.EnemyInfo.Id);
        GD.Print($"[arena] 击倒 {enemy.EnemyInfo.Name}");

        var defeatKey = $"defeat:{enemy.EnemyInfo.Id}";
        if (_loader.Campaign.EventTable.Triggers.Any(trigger =>
                trigger.Source == "battle" && trigger.Key == defeatKey && !_journal.EventStarted(trigger.Id)))
            _pendingDefeats.Add(enemy.EnemyInfo.Id);

        var spawn = enemy.SpawnKey is { Length: > 0 } key ? _mapRoster?.Source(key) : null;
        if (enemy.SpawnKey is { Length: > 0 } spawnKey)
        {
            _session.MarkEnemyDefeated(spawnKey, spawn?.Respawn ?? true);
            _mapRoster?.RemoveEnemy(spawnKey);
        }

        foreach (var questId in _tracker.ReportDefeat(enemy.EnemyInfo.Id))
            GD.Print($"[quest] {questId} 目标完成，可以回去交付了");

        SettleDefeatDrops(enemy);

        GrantDefeatExperience(enemy, killerId);

        if (enemy.EnemyInfo.DefeatPresentation == "vanish" && _outcomes is { } outcomes)
        {
            outcomes.Vanish(enemy.CombatId, enemy, () => enemy.QueueFree());
            return;
        }

        enemy.QueueFree();
    }

    /// <summary>
    /// 一次击杀的经验。
    /// 景物怪不发经验；战功判据照抄：
    /// <c>memberOf(casterId) || memberOf(arena[casterId].summonedBy)</c>——
    /// 队伍成员本人，或**它的召唤物**（一层；伙伴不能召唤，实际起作用的是伙伴本人击杀）。
    /// </summary>
    private void GrantDefeatExperience(EnemyNode enemy, string? killerId)
    {
        var earnsExperience = killerId is not null &&
                              (IsPartyMember(killerId) ||
                               (FindEnemy(killerId)?.SummonedBy is { Length: > 0 } owner && IsPartyMember(owner)));
        if (!earnsExperience || _party is null) return;
        if (Drops.BreakableKindOf(enemy.EnemyInfo.Id) is not null) return;

        var heroLevel = _hero?.Level ?? 1;
        var placed = enemy.SpawnKey is { Length: > 0 } key
            ? _mapRoster?.EnemySnapshot(key, enemy.EnemyInfo, heroLevel)
            : null;
        var stats = EnemyStatsCalculator.Calculate(enemy.EnemyInfo, new EnemyStatsOptions(
            Level: placed?.Stats.Level,
            Elite: placed?.Stats.Elite,
            PartySize: Math.Max(1, _party.LivingIds.Count),
            PlayerLevel: heroLevel));

        var reward = stats.Experience;
        if (reward <= 0) return;

        var levelsBefore = ActivePartySlots().ToDictionary(id => id, MemberLevel);

        var before = _hero?.Level ?? heroLevel;
        _party.AddExperience(reward);

        foreach (var id in ActivePartySlots())
        {
            if (!levelsBefore.TryGetValue(id, out var previous) || MemberLevel(id) <= previous) continue;
            if (ActorNodeOf(id) is { } node && node.Visible) _outcomes?.LevelUp(node);
            _audio?.PlayerLevelUp();
        }

        var after = _hero?.Level ?? before;
        if (after == before)
        {
            GD.Print($"[exp] +{reward}（{enemy.EnemyInfo.Name}），共 {_hero?.Player.Experience ?? 0}");
            return;
        }

        RefreshCharacterFromBag();
        SetPlayerHealth(PlayerMaxHealth);
        GD.Print($"[exp] +{reward}（{enemy.EnemyInfo.Name}）→ 升到 {after} 级，" +
                 $"血 {PlayerMaxHealth}，攻 {_panel.PhysicalAttack}");
    }

    private void SettleDefeatDrops(EnemyNode enemy)
    {
        var enemyId = enemy.EnemyInfo.Id;
        var position = new Vec2(enemy.Position.X, enemy.Position.Y);
        var heroLevel = _hero?.Level ?? 1;
        var breakable = Drops.BreakableKindOf(enemyId);

        if (breakable is not null)
        {
            var lootLevel = _map.LootLevel > 0 ? _map.LootLevel : heroLevel;
            var rolled = Drops.RollBreakableLoot(_loader.Drops, _loader.Stats, breakable, lootLevel, heroLevel,
                modifiers: GroupDropModifiers());
            SpawnLootOnMap(_map.Id, position.X, position.Y, rolled.Rewards);

            if (rolled.Enemies.Count > 0)
            {
                _session.Chests.SpawnBreakableEnemies(_map.Id, enemy.Name, rolled.Enemies, position);
                SyncMapEnemies("景物怪");
            }

            return;
        }

        if (_loader.Drops.Enemies?.GetValueOrDefault(enemyId) is { } rule && rule.NoNormalDrops != true)
        {
            var chapterLevel = _map.LootLevel > 0 ? _map.LootLevel : rule.Level;
            var pity = _loader.Drops.Pity;
            var firstBoss = rule.Threat == "boss" && _session.LootPity.TakeBossFirstDefeat(enemyId);
            var owners = _party?.LivingIds ?? ["player"];
            int[] offsets = [0, 5, -5];
            for (var index = 0; index < owners.Count; index += 1)
            {
                var ownerId = owners[index];
                var member = ownerId == "player" ? null : _party?.Get(ownerId);
                var memberLevel = member?.Level ?? heroLevel;
                var withinWindow = pity is null || rule.Level >= memberLevel - pity.LevelWindow;
                var (guaranteeBonus, minRarity) = Drops.FirstBossGuarantee(pity, firstBoss);
                var equipmentBonus = guaranteeBonus != 0 || pity is null
                    ? guaranteeBonus
                    : _session.LootPity.EquipmentBonus(ownerId, pity);
                var uniqueBonus = pity is null ? 0 : _session.LootPity.RareBonus(ownerId, pity);
                var rolled = Drops.RollEnemyLoot(_loader.Drops, _loader.Stats, enemyId, memberLevel,
                    chapterLevel, modifiers: GroupDropModifiers(),
                    options: new Drops.EnemyLootOptions(
                        EquipmentBonus: equipmentBonus, UniqueBonus: uniqueBonus,
                        MinRarity: minRarity));
                var bestRarity = 0;
                foreach (var reward in rolled.Rewards)
                    if (reward is Drops.DropReward.ItemDrop { Roll: { } roll })
                        bestRarity = Math.Max(bestRarity, roll.Rarity);
                _session.LootPity.RecordEnemy(ownerId,
                    rolled.Rewards.OfType<Drops.DropReward.ItemDrop>().Any(reward => reward.Roll is not null),
                    bestRarity, withinWindow);
                var offset = offsets[index % offsets.Length];
                SpawnLootOnMap(_map.Id, position.X + offset, position.Y, rolled.Rewards);
            }
        }

        SpawnLootOnMap(_map.Id, position.X, position.Y,
            Drops.RollQuestDrops(_loader.Drops, enemyId, _session.QuestDropRequirements()));
    }

    private Drops.DropModifiers? GroupDropModifiers()
    {
        var group = _party?.GroupModifiers();
        if (group is null) return null;
        return new Drops.DropModifiers(
            EquipmentDrop: group.GetValueOrDefault("equipment-drop"),
            ScrollDrop: group.GetValueOrDefault("scroll-drop"),
            MaterialDrop: group.GetValueOrDefault("material-drop"),
            ChestQuality: group.GetValueOrDefault("chest-quality"));
    }

    private void SpawnLoot(float x, float y, IReadOnlyList<Drops.DropReward> rewards) =>
        SpawnLootOnMap(_map.Id, x, y, rewards);

    private void SpawnLootOnMap(string mapId, float x, float y, IReadOnlyList<Drops.DropReward> rewards)
    {
        if (rewards.Count == 0) return;
        var landing = LootLanding(new Vec2(x, y));
        var prepared = new List<Drops.DropReward>();
        foreach (var reward in rewards)
            prepared.Add(reward is Drops.DropReward.ItemDrop { Instance: null } item &&
                         _loader.Items.Items.GetValueOrDefault(item.Item)?.Stackable != true
                ? item with
                {
                    Instance = _bag.PrepareDrop(item.Item,
                        item.Roll is null
                            ? null
                            : new Inotia4.Domain.Character.DropRoll(item.Roll.Sockets, item.Roll)),
                }
                : reward);
        _session.GroundLoot.Add(mapId, landing.X, landing.Y, prepared,
            nowMs: (long)Time.GetTicksMsec());
        SyncGroundLoot();
    }

    private Vec2 LootLanding(Vec2 point)
    {
        if (_grid.CanStandOnTerrain(point.X, point.Y)) return point;
        for (var radius = 8; radius <= 64; radius += 8)
        {
            for (var dx = -radius; dx <= radius; dx += 8)
            for (var dy = -radius; dy <= radius; dy += 8)
            {
                if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                var candidate = new Vec2(point.X + dx, point.Y + dy);
                if (_grid.CanStandOnTerrain(candidate.X, candidate.Y)) return candidate;
            }
        }

        return point;
    }

    /// <summary>
    /// 拾取一枚魔力宝石：按收集者的魔力上限给全队回蓝。
    /// 操控者倒下的那一帧仍可能走到宝石上，所以收集者按「操控者 → 上阵顺序」取第一个活人；
    /// 全灭时返回 <c>false</c>，宝石留在原地。
    /// </summary>
    public bool CollectMana(ManaPickupDefinition pickup)
    {
        var members = new List<ManaMember>();
        if (_hero is { } hero) members.Add(new HeroManaMember(hero));
        foreach (var id in _party?.ActiveIds ?? [])
        {
            if (id == PlayerCombatId) continue;
            if (_party!.Get(id) is { } companion) members.Add(new CompanionManaMember(id, companion));
        }

        var order = new List<string> { _party?.ControlledId ?? PlayerCombatId };
        if (_party is not null)
            foreach (var id in _party.ActiveIds)
                if (!order.Contains(id)) order.Add(id);

        string? collectorId = null;
        foreach (var id in order)
        {
            var health = id == PlayerCombatId ? _playerHealth : _party?.Get(id)?.Player.Health ?? 0;
            if (health <= 0) continue;
            collectorId = id;
            break;
        }

        if (collectorId is null) return false;
        var result = ManaGems.RecoverManaPickup(pickup, members, collectorId);
        if (result is null) return false;

        foreach (var memberId in result.Effects)
            if (ActorNodeOf(memberId) is { } restored) _outcomes?.Recover(restored);

        _audio?.Ui("heal");
        return true;
    }

    private sealed class HeroManaMember(BattleCharacter hero) : ManaMember
    {
        public string Id => PlayerCombatId;

        public int Health => hero.Player.Health;

        public int Mana => hero.Player.Mana;

        public int MaxMana => hero.Player.MaxMana;

        public void SetMana(int value) => hero.SetMana(value);
    }

    private sealed class CompanionManaMember(string id, CompanionMember member) : ManaMember
    {
        public string Id => id;

        public int Health => member.Player.Health;

        public int Mana => member.Player.Mana;

        public int MaxMana => member.Player.MaxMana;

        public void SetMana(int value) => member.SetMana(value);
    }

    private bool ReceiveGroundReward(Drops.DropReward reward)
    {
        switch (reward)
        {
            case Drops.DropReward.MoneyDrop money:
                _session.GainMoney(money.Amount);
                return true;
            case Drops.DropReward.ItemDrop item:
            {
                var definition = _loader.Items.Items.GetValueOrDefault(item.Item);
                if (definition is null) return false;
                if (definition.IsPickup)
                    return definition.Pickup.AsManaPickup() is { } pickup && CollectMana(pickup);
                if (item.Instance is not null)
                {
                    _bag.ReceiveDrop(item.Instance);
                    return true;
                }

                _bag.Add(item.Item, 1);
                return true;
            }
            default:
                return false;
        }
    }

    private void CollectGroundLoot()
    {
        var picked = _session.GroundLoot.CollectNearby(_map.Id, ControlPosition.X, ControlPosition.Y,
            ReceiveGroundReward,
            drop =>
            {
                if (!_lootMissedSeen.Add(drop.Id)) return;
                RecordMissed(drop.Reward);
                _lootHudUntilMs = Time.GetTicksMsec() + 5000;
            });
        if (picked.Count == 0) return;

        if (picked.Any(drop => drop.Reward is Drops.DropReward.ItemDrop item &&
                               _loader.Items.Items.GetValueOrDefault(item.Item)?.Pickup is not null))
            _audio?.Ui("loot");

        _session.QuestItems.Collected(picked.Select(drop => drop.Id).ToList(), _combatClock.Frame);
        foreach (var drop in picked)
        {
            switch (drop.Reward)
            {
                case Drops.DropReward.MoneyDrop money:
                    _lootMoney += money.Amount;
                    break;
                case Drops.DropReward.ItemDrop item:
                {
                    var name = _loader.Items.Items.GetValueOrDefault(item.Item)?.Name ?? item.Item;
                    _lootTally[name] = _lootTally.GetValueOrDefault(name) +
                                       Math.Max(1, item.Instance?.Quantity ?? 1);
                    break;
                }
            }

        }

        _lootHudUntilMs = Time.GetTicksMsec() + 5000;

        var rewards = new List<Drops.DropReward>();
        foreach (var drop in picked)
            if (drop.Reward is Drops.DropReward.MoneyDrop ||
                (drop.Reward is Drops.DropReward.ItemDrop pickedItem &&
                 _loader.Items.Items.GetValueOrDefault(pickedItem.Item)?.Pickup is null))
                rewards.Add(drop.Reward);
        if (rewards.Count > 0) _pickupMessages.Enqueue(rewards, ControlledId);
    }

    private void ExpireLootWindow()
    {
        if ((_lootTally.Count == 0 && _lootMoney == 0 && _lootMissed.Count == 0) ||
            Time.GetTicksMsec() <= _lootHudUntilMs) return;

        _lootTally.Clear();
        _lootMoney = 0;
        _lootMissed.Clear();
        _lootMissedSeen.Clear();
    }

    private string LootHud()
    {
        ExpireLootWindow();
        if (_lootTally.Count == 0 && _lootMoney == 0) return string.Empty;

        var parts = new List<string>();
        foreach (var (name, count) in _lootTally)
            parts.Add(count > 1 ? $"{name} ×{count}" : name);
        if (_lootMoney > 0) parts.Add($"+{_lootMoney} 金币");
        return "拾取  " + string.Join("　", parts);
    }

    private string LootMissedHud()
    {
        ExpireLootWindow();
        if (_lootMissed.Count == 0) return string.Empty;

        var groups = new List<string>();
        foreach (var group in _lootMissed.GroupBy(pair => pair.Key.Split('|', 2)[0]))
        {
            var items = group.Select(pair =>
            {
                var name = pair.Key.Split('|', 2)[1];
                return pair.Value > 1 ? $"{name} ×{pair.Value}" : name;
            });
            groups.Add(group.Key + "　" + string.Join("　", items));
        }

        return string.Join("　／　", groups);
    }

    private void RecordMissed(Drops.DropReward reward)
    {
        if (reward is not Drops.DropReward.ItemDrop item) return;

        var definition = _loader.Items.Items.GetValueOrDefault(item.Item);
        var reason = definition is null ? "无法拾取"
            : definition.IsPickup ? "魔力已满"
            : "背包已满";
        var name = definition?.Name ?? item.Item;
        var key = reason + "|" + name;
        _lootMissed[key] = _lootMissed.GetValueOrDefault(key) + Math.Max(1, item.Instance?.Quantity ?? 1);
    }

    private void SyncGroundLoot()
    {
        if (_session.GroundLoot.Revision == _groundLootRevision) return;
        _groundLootRevision = _session.GroundLoot.Revision;
        _groundLootView.Sync(_session.GroundLoot.ForMap(_map.Id));
    }

    public void SpawnStoryDrop(string mapId, float x, float y, string item) =>
        SpawnLootOnMap(mapId, x, y, [new Drops.DropReward.ItemDrop(item)]);

    private void ResetArena()
    {
        SetPlayerHealth(_playerFighter.MaxHealth);
        PlayFadeIn();

        _session.Moved(StartPosition, "south");
        _player.TeleportTo(new Vector2(StartPosition.X, StartPosition.Y));
        ClearPlaybacks();
        _enemies.Clear();
        _threat?.Clear();
        _focusTargetId = null;
        _damageCount = 0;
        SpawnArena();
        GD.Print("[arena] 主角倒下，已重置");
    }

    private void ShowHitFeedback(HitResult hit, string targetId, bool casterControlled,
        string style = "weapon", bool periodic = false)
    {
        var node = ResolveActorNode(targetId);
        if (node is null) return;
        ShowHitFeedback(hit, node, casterControlled, style, periodic);
    }

    /// <summary>
    /// 一次命中的画面反馈。
    ///
    /// 「该画什么」由域层的 <see cref="CombatFeedback"/> 判：可见性、数字分组、冲击类型、是否震屏；
    /// 这里只把结果交给 <see cref="CombatFeedbackView"/> 画出来。
    /// 队伍成员与召唤物在世界里还没有独立实体，所以「我方」目前就等于主角。
    /// </summary>
    private void ShowHitFeedback(HitResult hit, ActorNode target, bool casterControlled,
        string style = "weapon", bool periodic = false)
    {
        if (_feedbackView is null || BattleContent.Feedback is not { } definition) return;

        var isPlayer = ReferenceEquals(target, _player);
        var feedback = new CombatHitFeedback(
            hit,
            style,
            periodic,
            CasterControlled: casterControlled,
            TargetFriendly: isPlayer,
            TargetParty: isPlayer,
            InvolvesControlled: casterControlled || isPlayer);

        var plan = CombatFeedback.Plan(feedback);
        _feedbackView.Show(feedback, target,
            CombatFeedback.Select(feedback, definition, _random.NextDouble,
                _displaySettings?.CriticalDecorations ?? true),
            plan);

        if (plan.Flash) target.Visual.FlashStatus();
    }

    private void SpawnDamageText(Vector2 worldPosition, string text, Color color, bool big = false)
    {
        var label = new Label
        {
            Text = text,
            Position = worldPosition + new Vector2(-8f, -58f),
        };
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeConstantOverride("font_size", big ? 22 : 16);
        label.AddThemeConstantOverride("outline_size", 4);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));

        _entities.AddChild(label);

        var move = CreateTween();
        move.TweenProperty(label, "position", label.Position + new Vector2(0f, -26f), 0.7f);

        var fade = CreateTween();
        fade.TweenProperty(label, "modulate:a", 0f, 0.7f);
        fade.TweenCallback(Callable.From(label.QueueFree));
    }

    private void UpdateCameraLimits()
    {
        var map = _stage.Active && _stage.StageMap is { } stageMap ? stageMap : _map;

        _camera.LimitLeft = 0;
        _camera.LimitTop = 0;
        _camera.LimitRight = map.Width;
        _camera.LimitBottom = map.Height;
        ApplyCameraZoom(map);
        _camera.ResetSmoothing();
        _camera.GlobalPosition = new Vector2(_position.X, _position.Y);
    }

    private void OnViewportResized()
    {
        if (_map is not null) UpdateCameraLimits();
        PlaceHudButtons();
    }

    /// <summary>
    /// 左侧两枚圆钮的落点：贴着左缘竖排，第一枚在屏幕纵向四分之一处。
    ///
    /// 左下角是摇杆（中心距底 129、半径 105，放大到 140% 时半径 147），右下角是六格，
    /// 所以左侧只剩中间这一条空档。缩放跟操作区同一个基准（<see cref="ControlLayouts.ActionScale"/>），
    /// 换屏、换分辨率都跟别的 HUD 一起走，不会各自错位。
    /// </summary>
    private void PlaceHudButtons()
    {
        if (_saveButton is null || _settingsButton is null) return;
        var size = GetViewportRect().Size;
        if (size.X <= 1f || size.Y <= 1f) return;

        var scale = MathF.Max(0.6f, ControlLayouts.ActionScale(size.X, size.Y));
        var x = 2f * scale;
        var y = size.Y * 0.24f;
        var step = (SaveIconSize + HudIconGap) * scale;

        _saveButton.Position = new Vector2(x, y);
        _saveButton.Scale = new Vector2(scale, scale);
        _settingsButton.Position = new Vector2(x, y + step);
        _settingsButton.Scale = new Vector2(scale, scale);
    }

    private Control CreateHudIconButton(string name, string iconPath, Action action)
    {
        var root = new Control { Name = name };
        _hudLayer!.AddChild(root);
        var hit = PanelGfx.Hit(0f, 0f, SaveIconSize, SaveIconSize, action, root);
        PanelGfx.Image(path => _heroLease.Texture(path), iconPath,
            0f, 0f, SaveIconSize, SaveIconSize, hit);
        return root;
    }

    private void QuickSettings()
    {
        if (_map is null || _storyPlaying || _defeated || MenuOpen()) return;
        ToggleSystem(SystemPanel.PageSettings);
    }

    private void QuickSave()
    {
        if (_map is null || _storyPlaying || _defeated || MenuOpen()) return;

        var reference = _currentSaveSlot is { IsAuto: false } current ? current : new SaveSlotRef(0);
        if (CaptureSaveGame() is not { } game)
        {
            ShowHudNotice("现在存不了");
            return;
        }

        try
        {
            _saveSlots.Write(reference, game);
            _currentSaveSlot = reference;
            ShowHudNotice($"已保存到{SaveGameStore.SaveSlotLabel(reference)}");
        }
        catch (Exception error)
        {
            GD.PushWarning($"手动保存失败：{error.Message}");
            ShowHudNotice("保存失败");
        }
    }

    /// <summary>
    /// 镜头缩放：3 倍是基准（见 <see cref="CameraZoom"/>）。
    /// 地图比可见区域还小的时候（宽屏手机上很常见：可见宽度 = 视口宽 ÷ 3），
    /// 镜头会被边界卡住、在地图两侧露出背景，看起来又像黑边——所以按需再拉近一点，
    /// 让地图至少铺满屏幕。最窄的地图 256 宽、宽屏手机视口 1422，需要 5.55 倍，
    /// 所以上限放到 2 倍（6 倍）；再往上就是安全栏，不是常态。
    /// </summary>
    private void ApplyCameraZoom(MapDefinition map)
    {
        var viewport = GetViewportRect().Size;
        var needed = MathF.Max(viewport.X / map.Width, viewport.Y / map.Height);
        var zoom = MathF.Min(MathF.Max(CameraZoom, needed), CameraZoom * 2f);
        _camera.Zoom = new Vector2(zoom, zoom);
    }

    private void SpawnMapActors(Node2D entities)
    {
        _npcs.Clear();
        _mapActors.Clear();
        _baseEntities.Clear();

        var authored = _loader.Campaign.PlacementTable
            .Where(placement => placement.Map == _map.Id).ToArray();
        var activeAuthored = authored.Where(IsAuthoredActive).ToArray();

        var candidates = new List<MapEntity>(_map.Entities.Length + activeAuthored.Length);
        if (PendingGeneratedEntities() is { } prepared)
        {
            candidates.AddRange(prepared);
        }
        else
        {
            foreach (var raw in _map.Entities)
            {
                if (activeAuthored.Any(placement => ReplacesMapEntity(placement, raw))) continue;

                var rolled = ChestPlacement.RollPlacement(raw, _random.NextDouble);
                if (rolled is not null) candidates.Add(rolled);
            }

            foreach (var placement in activeAuthored)
            {
                candidates.Add(new MapEntity
                {
                    Id = placement.Id,
                    Actor = placement.Actor,
                    CharacterId = placement.CharacterId,
                    X = placement.X,
                    Y = placement.Y,
                    Authority = PlacementAuthority.Of(placement.Id, null),
                });
            }

            // 泉水按主角等级换成本次能喝到的那一档。
            // 掷骰发生在静态与剧情摆放合并之后，顺序不能反过来。
            for (var index = 0; index < candidates.Count; index++)
                candidates[index] = RollSpringActor(candidates[index]);
        }

        _baseEntities.AddRange(candidates);

        var definitions = authored.Select(placement => new MapEntity
        {
            Id = placement.Id,
            Actor = placement.Actor,
            CharacterId = placement.CharacterId,
            X = placement.X,
            Y = placement.Y,
            Authority = PlacementAuthority.Of(placement.Id, null),
        }).ToArray();

        var projected = _session.StoryWorld.ProjectEntities(
            _map.Id, candidates, definitions, _session.StoryWorld.Visit);
        GD.Print($"[project] {_map.Id} 候选 {candidates.Count}（静态＋剧情摆放），" +
                 $"投影 {projected.Count}，其中剧情登记 {projected.Count(entity => _session.StoryWorld.IsPlaced(entity.Id))}");
        foreach (var entity in projected)
        {
            var appearance = entity.Spring && _session.Springs.IsEmpty(_map.Id, entity.Id)
                ? _loader.Springs.Empty
                : _session.StoryWorld.ActorAppearance(entity.Actor);
            if (!_loader.World.Actors.TryGetValue(appearance, out var definition))
            {
                GD.PushWarning($"地图实体 {entity.Id} 引用了不存在的角色 {appearance}，已跳过。");
                continue;
            }

            var actor = new ActorNode { Name = entity.Id };
            entities.AddChild(actor);
            // 朝向：剧情登记过的按登记的，否则沿用「翻转且朝东片段存在就朝东」的老口径（原版同上）。
            var facing = _session.StoryWorld.Entity(entity.Id)?.Facing
                         ?? (entity.Flip && definition.HasClip("idle-east") ? "east" : "south");
            actor.Setup(definition, _mapLease!, facing, entity.Flip);
            actor.TeleportTo(new Vector2(entity.X, entity.Y));
            _spawnedActors++;

            var oneTime = _session.StoryWorld.IsPlaced(entity.Id);
            var npc = new MapNpc(actor, entity.Id, entity.Actor, entity.CharacterId, definition, oneTime,
                entity.Objective, entity.Hint, entity.Spring);
            _mapActors.Add(new MapActorRecord(entity.Id, entity.Actor, entity.CharacterId,
                PlacementAuthority.Of(entity.Id, entity.Authority), entity.Flip, actor, npc));
            if (!IsEntityVisible(entity, definition, oneTime))
            {
                actor.Visible = false;
                continue;
            }

            _npcs.Add(npc);
        }
    }

    /// <summary>
    /// 读档/回检查点时该用的摆放。
    /// 只有「待恢复的快照就是当前这张图」才算数，否则照常重掷。
    /// </summary>
    private IReadOnlyList<MapEntity>? PendingGeneratedEntities()
    {
        var state = _pendingWorldRestore;
        if (state?.GeneratedEntities is not { Count: > 0 } entities) return null;
        return _map is not null && state.Location.Map == _map.Id ? entities : null;
    }

    private bool IsAuthoredActive(ActorPlacement placement) =>
        _session.StoryWorld.Visible(placement.Id) && _story.Evaluate(placement.Condition);

    private static bool ReplacesMapEntity(ActorPlacement authored, MapEntity entity) =>
        !string.IsNullOrEmpty(authored.CharacterId) && !string.IsNullOrEmpty(entity.CharacterId)
            ? authored.CharacterId == entity.CharacterId
            : authored.Actor == entity.Actor;

    /// <summary>
    /// 这个人物该不该画：
    /// 剧情可见性、本图在场标记，以及「开过的一次性宝箱不再出现」。
    /// </summary>
    private bool IsEntityVisible(MapEntity entity, ActorDefinition definition, bool oneTime) =>
        _session.StoryWorld.Visible(entity.Id) &&
        _session.StoryWorld.ActorVisible(_map.Id, entity.Actor, entity.CharacterId) &&
        !(definition.Chest is not null && _session.Chests.IsOpened(_map.Id, entity.Id, oneTime)) &&
        !_session.Props.IsConsumed(_map.Id, entity.Id);

    /// <summary>
    /// 挑出身边的交互对象： 用曼哈顿距离 24 像素，最近的一个胜出；
    /// 有任务槽的 NPC 优先于普通闲话对象（任务目标与交付人可能摆在同一点，
    /// 目标没记满前必须选得中目标）。
    ///
    /// 距离档照两级判定：宝箱只认 24 像素；
    /// 其余对象在 24 像素外还有一道「脚底矩形间隙 ≤ 6 个 8 像素单位」的兜底，
    /// 让路障、栅栏这类大体积机关贴着边缘也够得着。原版这一档的开关是
    /// <c>!!actor.collision</c>——内容里 693 个角色只有 7 个灯柱／出口标记／桥没有碰撞盒，
    /// 它们本来也进不了候选，所以这里恒按「有碰撞盒」处理。
    /// </summary>
    /// <summary>
    /// 除了任务槽之外，这个物件还有没有别的可交互理由。对应 「交互候选」那套判定：
    /// 带提示、带交互目标、是宝箱／泉水、是商店摊位、有 NPC 功能（拔剑、旅馆、复活），
    /// 是融合器，或者写了描述（描述就是那句闲聊台词）。任务目标物本身也算——交互即推进。
    ///
    /// 一条都不满足的就是纯装饰：火把、尸体、柴堆、门板。它们不该进候选，
    /// 否则战斗里按攻击键会先撞上这些东西。
    /// </summary>
    /// <summary>
    /// 当过任务发布人／交付人的 actor 与角色编号。这些 NPC 即使当前没有任务槽
    /// （任务还没解锁、或处于隐藏任务阶段）也必须留在交互候选里——
    /// 剧情里「去找某某人说话」这一步不能因为过滤装饰物而断掉。
    /// </summary>
    private HashSet<string>? _questActors;

    private HashSet<string> QuestActors()
    {
        if (_questActors is not null) return _questActors;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quest in _loader.Campaign.Quests)
        foreach (var value in new[] { quest.Giver, quest.Delivery, quest.GiverCharacterId, quest.DeliveryCharacterId })
            if (!string.IsNullOrEmpty(value)) set.Add(value);
        return _questActors = set;
    }

    private bool HasInteractionContent(MapNpc npc)
    {
        var questActors = QuestActors();
        if (questActors.Contains(npc.ActorId)) return true;
        if (npc.CharacterId is { Length: > 0 } characterId && questActors.Contains(characterId)) return true;
        if (npc.ChestKind is not null || npc.Spring) return true;
        if (npc.Objective is not null || npc.Hint is { Length: > 0 }) return true;
        if (_loader.Shops.Shops.ContainsKey(npc.ActorId)) return true;
        if (_loader.NpcFunctions.ActionOf(npc.CharacterId) is not null) return true;
        if (IsFusionMachine(npc.ActorId)) return true;
        if (_journal.WaitingGoals(npc.ActorId, npc.CharacterId).Count > 0) return true;
        return npc.Definition.Description is { Length: > 0 };
    }

    private void UpdateInteraction()
    {
        _nearNpc = null;
        if (_storyPlaying) return;

        var controlled = ControlPosition;
        MapNpc? best = null;
        var bestDistance = float.MaxValue;
        var bestRank = 2;

        foreach (var npc in _npcs)
        {
            var target = new Vec2(npc.Node.Position.X, npc.Node.Position.Y);
            var reachable = npc.ChestKind is not null
                ? ChestReach.NearInteractable(controlled, target)
                : ChestReach.NearInteractive(controlled, target, blocking: true);
            if (!reachable) continue;
            if (!npc.Node.Visible) continue;
            if (npc.ChestKind is not null &&
                _session.Chests.IsOpened(_map.Id, npc.EntityId, npc.OneTime)) continue;
            if (npc.Spring && _session.Springs.IsEmpty(_map.Id, npc.EntityId)) continue;
            if (_session.Props.IsConsumed(_map.Id, npc.EntityId)) continue;

            var distance = MathF.Abs(target.X - controlled.X) + MathF.Abs(target.Y - controlled.Y);
            var slot = _journal.SlotFor(npc.ActorId, npc.CharacterId);
            // 既没有任务可谈、也没有任何交互理由的物件直接跳过：火把、尸体、柴堆、
            // 门板这类装饰物以前也会顶掉真正的目标，交互还只回一句「一路小心。」，
            // 战斗中这一下误触直接把攻击键吃掉。
            if (slot is null && !HasInteractionContent(npc)) continue;

            var rank = slot is null ? 1 : 0;
            if (rank > 0 && npc.Objective is not null) rank = 0;
            if (distance >= bestDistance && (distance != bestDistance || rank >= bestRank)) continue;

            best = npc;
            bestDistance = distance;
            bestRank = rank;
        }

        _nearNpc = best;
    }

    private async void TryInteract()
    {
        if (_storyPlaying || _nearNpc is not { } npc) return;

        // 宝箱：原版走的是攻击键那条 <c>interact()</c>——必须正面朝着目标才开得开，
        // 没朝着就是一下普攻（这里没有普攻，所以直接不开）。
        if (npc.ChestKind is { } chestKind)
        {
            var facing = ControlledNode().Facing;
            if (!ChestReach.FacesTarget(ControlPosition, facing,
                    new Vec2(npc.Node.Position.X, npc.Node.Position.Y)))
                return;
            OpenChest(npc, chestKind);
            return;
        }

        if (npc.Spring)
        {
            var facing = ControlledNode().Facing;
            if (!ChestReach.FacesTarget(ControlPosition, facing,
                    new Vec2(npc.Node.Position.X, npc.Node.Position.Y)))
                return;
            DrinkSpring(npc);
            return;
        }

        if (IsFusionMachine(npc.ActorId) && !TakesQuestSlot(npc))
        {
            OpenCrafting(npc.ActorId);
            return;
        }

        if (_loader.Shops.Shops.ContainsKey(npc.ActorId) && npc.Objective is null && !TakesQuestSlot(npc))
        {
            OpenShop(npc.ActorId);
            return;
        }

        await InteractNpcAsync(npc);

        if (IsFusionMachine(npc.ActorId) && _craftPanel is not { IsOpen: true })
            OpenCrafting(npc.ActorId);
    }

    private async Task InteractNpcAsync(MapNpc npc)
    {
        var readyBefore = _journal.Entries
            .Where(pair => pair.Value == QuestStatus.Ready).Select(pair => pair.Key).ToHashSet();
        if (_session.Interact(npc.ActorId, npc.EntityId, npc.CharacterId))
            foreach (var questId in _journal.Entries
                         .Where(pair => pair.Value == QuestStatus.Ready && !readyBefore.Contains(pair.Key))
                         .Select(pair => pair.Key))
                GD.Print($"[quest] {questId} 目标达成，可以回去了");

        var actor = _loader.World.Actors.TryGetValue(npc.ActorId, out var definition) ? definition : null;

        if (!TakesQuestSlot(npc) && _loader.NpcFunctions.ActionOf(npc.CharacterId) is { } function)
        {
            await RunNpcFunction(npc, function, actor?.Name ?? npc.ActorId);
            return;
        }

        var plan = NpcInteraction.Interact(
            _loader.Campaign, _journal, npc.ActorId, npc.CharacterId,
            hint: npc.Hint, description: actor?.Description);

        var speaker = actor?.Name is { Length: > 0 } name ? name : npc.ActorId;
        GD.Print($"[interact] {speaker}：{plan.Kind}{(plan.QuestId.Length > 0 ? $"（{plan.QuestId}）" : string.Empty)}");

        switch (plan.Kind)
        {
            case InteractionPlan.PlanKind.Idle:
                await _dialogue.ShowAsync(speaker, plan.Text!, screen: false);
                _dialogue.Close();
                return;

            case InteractionPlan.PlanKind.PlayScript:
                PlayStory(plan.ScriptId!);
                return;

            case InteractionPlan.PlanKind.Accept:
            {
                var quest = _loader.Campaign.FindQuest(plan.QuestId)!;
                QuestRules.Accept(_loader.Campaign, _journal, _bag, plan.QuestId);
                _audio?.Ui("questStart");
                if (quest.OnAcceptItems.Length > 0)
                    GD.Print($"[quest] 发放任务物品：{string.Join("，", quest.OnAcceptItems.Select(i => $"{i.Item}×{i.Quantity}"))}");
                await _questPopup.ShowAsync(quest, "start");
                return;
            }

            case InteractionPlan.PlanKind.Complete:
            {
                var quest = _loader.Campaign.FindQuest(plan.QuestId)!;
                var receipt = NpcInteraction.Settle(_loader.Campaign, _journal, plan.QuestId);
                if (receipt is null)
                {
                    // 目标集齐但交付物品不足——理论上不该发生，兜底说一句进度话。
                    await _dialogue.ShowAsync(speaker, quest.ProgressText ?? "东西没带齐吧？", screen: false);
                    _dialogue.Close();
                    return;
                }

                _audio?.Ui("questComplete");
                await _questPopup.ShowAsync(quest, "complete", receipt);
                return;
            }

            case InteractionPlan.PlanKind.Progress:
                await _dialogue.ShowAsync(speaker, plan.Text ?? "加油。", screen: false);
                _dialogue.Close();
                return;
        }
    }

    /// <summary>
    /// 按功能编号跑一次交互。原版在 <c>NPCSYSTEM_MakeFunctionList</c> 排出功能列表后逐条分派；
    /// 这里只认内容里配了 behavior 的那几条——没配的行为在加载时就已经被拦下，
    /// 所以走到这里的 <see cref="NpcFunction.Behavior"/> 一定不为空。
    /// </summary>
    private Task RunNpcFunction(MapNpc npc, NpcFunction function, string speaker) =>
        function.Behavior?.Kind switch
        {
            NpcFunctionBehavior.PullSword => PullSwordAsync(npc, function, speaker),
            NpcFunctionBehavior.Rest => RestAtInnAsync(speaker),
            NpcFunctionBehavior.Revive => ReviveCompanionsAsync(function, speaker),
            _ => Task.CompletedTask,
        };

    private async Task PullSwordAsync(MapNpc npc, NpcFunction function, string speaker)
    {
        if (!CanControl() || !npc.Node.Visible) return;
        if (_session.Props.IsConsumed(_map.Id, npc.EntityId)) return;

        var position = new Vec2(npc.Node.Position.X, npc.Node.Position.Y);
        if (!ChestReach.CanReach(ControlPosition, position)) return;

        await _dialogue.ShowAsync(speaker, function.Name, screen: false);
        _dialogue.Close();

        var item = NpcFunctionCatalog.RewardFor(function, _classId);
        if (_bag is null || item is null)
        {
            ShowHudNotice("剑纹丝不动。");
            GD.Print($"[npc] {npc.EntityId}（{function.Name}）：职业 {_classId} 没有配奖励，不消耗");
            return;
        }

        if (!_session.Props.Consume(_map.Id, npc.EntityId)) return;
        _bag.Add(item);

        var itemName = _loader.Items.Items.TryGetValue(item, out var definition) ? definition.Name : item;
        _audio?.Ui("chestOpen");
        ShowHudNotice($"拔出了{itemName}");

        _npcs.Remove(npc);
        _nearNpc = null;
        var tween = CreateTween();
        tween.TweenProperty(npc.Node, "modulate:a", 0f, ChestVisits.OpenMs / 1000f);
        tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(npc.Node)) npc.Node.Visible = false;
        }));

        GD.Print($"[npc] {npc.EntityId}（{function.Name}）：拔出 {itemName}（{item}），职业 {_classId}");
    }

    private async Task RestAtInnAsync(string speaker)
    {
        if (!CanControl()) return;

        var recovered = _party?.RestAll() ?? 0;
        if (_party is null && _hero is not null)
        {
            _hero.SetHealth(_hero.Player.MaxHealth);
            _hero.SetMana(_hero.Player.MaxMana);
            recovered = 1;
        }

        _audio?.Ui("heal");
        await _dialogue.ShowAsync(speaker,
            recovered > 0 ? "床铺收拾好了，睡一觉再上路吧。" : "床铺很舒服，不过你们现在状态正好。",
            screen: false);
        _dialogue.Close();
        ShowHudNotice(recovered > 0 ? "全队体力与魔力恢复" : "队伍状态已满");
        GD.Print($"[npc] 旅馆休息：恢复 {recovered} 人");
    }

    private async Task ReviveCompanionsAsync(NpcFunction function, string speaker)
    {
        if (!CanControl()) return;

        var percent = function.Behavior?.Percent ?? 100;
        var revived = _party?.ReviveFallen(percent) ?? 0;
        _audio?.Ui(revived > 0 ? "heal" : "select");
        await _dialogue.ShowAsync(speaker,
            revived > 0 ? "灵魂已经归位，带他们好好休息吧。" : "队伍里没有倒下的同伴。",
            screen: false);
        _dialogue.Close();
        if (revived > 0) ShowHudNotice($"{revived} 名同伴复活");
        GD.Print($"[npc] 复活同伴：{revived} 人（{percent}%）");
    }

    private void OpenChest(MapNpc chest, string kind)
    {
        if (!CanControl() || !chest.Node.Visible) return;

        var position = new Vec2(chest.Node.Position.X, chest.Node.Position.Y);
        if (!ChestReach.CanReach(ControlPosition, position)) return;

        var map = _map;
        var level = map.ChestLevel ?? (map.LootLevel > 0 ? map.LootLevel : 1);
        var pity = _loader.Drops.Pity;
        var equipmentBonus = pity is null ? 0 : _session.LootPity.ChestEquipmentBonus(kind, pity);

        var result = _session.Chests.Open(map.Id, chest.EntityId, position, () =>
        {
            var rolled = Drops.RollChestLoot(_loader.Drops, _loader.Stats, kind, level, _hero?.Level ?? 1,
                _random.NextDouble, GroupDropModifiers(), equipmentBonus);
            _session.LootPity.RecordChest(kind,
                rolled.Rewards.Any(reward => reward is Drops.DropReward.ItemDrop { Roll: not null }));
            return rolled;
        }, chest.OneTime);
        if (result is null) return;

        _audio?.Ui("chestOpen");

        _npcs.Remove(chest);
        _nearNpc = null;

        SpawnLoot(position.X, position.Y, result.Rewards);
        SyncMapEnemies("宝箱怪");

        var tween = CreateTween();
        tween.TweenProperty(chest.Node, "modulate:a", 0f, ChestVisits.OpenMs / 1000f);
        tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(chest.Node)) chest.Node.Visible = false;
        }));

        GD.Print($"[chest] {chest.EntityId}（{kind}，{level} 级）：{result.Rewards.Count} 份奖励，" +
                 $"{result.Enemies.Count} 只宝箱怪");
    }

    private MapEntity RollSpringActor(MapEntity entity)
    {
        if (!entity.Spring) return entity;
        var level = _session.Hero?.Level ?? 1;
        var spring = SpringRoll.Roll(_loader.Springs, level, _random.NextDouble);
        return entity.WithActor(spring.Id);
    }

    private void DrinkSpring(MapNpc spring)
    {
        if (!spring.Node.Visible) return;
        var position = new Vec2(spring.Node.Position.X, spring.Node.Position.Y);
        if (!ChestReach.CanReach(ControlPosition, position)) return;

        var effect = SpringRoll.RollEffect(_loader.Springs, spring.ActorId, _random.NextDouble);
        var hero = _session.Hero;

        switch (effect.Kind)
        {
            case "buff":
                // 原版效果类型 4 直接建 buff，不经过技能或命中，所以作用在当前操控成员身上。
                if (effect.Buff is { } buff) ApplySpringBuff(buff);
                break;
            case "heal":
                if (hero is not null)
                {
                    var player = hero.Player;
                    hero.SetHealth(player.Health +
                                   (int)Math.Floor(player.MaxHealth * (effect.Percent ?? 0) / 100.0 + 0.5));
                }
                break;
            case "heal-mana":
                if (hero is not null)
                {
                    var player = hero.Player;
                    hero.SetMana(player.Mana +
                                 (int)Math.Floor(player.MaxMana * (effect.Percent ?? 0) / 100.0 + 0.5));
                }
                break;
            case "skill-point":
                hero?.GrantSkillPoint();
                break;
            case "spawn":
                if (effect.Enemy is { } enemy)
                    _session.Chests.SpawnEnemy(_map.Id, spring.EntityId, enemy, position);
                break;
        }

        _audio?.Ui("heal");
        ShowHudNotice(effect.Text);

        _session.Springs.Drain(_map.Id, spring.EntityId);
        DrainSpringActor(spring);
        if (_nearNpc == spring) _nearNpc = null;
        SyncMapEnemies("泉水");
        GD.Print($"[spring] {spring.EntityId}（{spring.ActorId}）：{effect.Kind} {effect.Text}");
    }

    /// <summary>
    /// 泉水喝过就空了。公共尾把泉水对象改写成水井（npc 38）再重算外观：
    /// 水井用的是没有液体的那张图，而且它没有 NPC 功能，所以再交互不会有效果也不提示。
    /// </summary>
    private void DrainSpringActor(MapNpc spring)
    {
        var empty = _loader.Springs.Empty;
        if (!_loader.World.Actors.TryGetValue(empty, out var definition))
            throw new InvalidOperationException($"泉水缺少喝空后的外观：{empty}");
        spring.Node.Setup(definition, _mapLease!, "south", false);
    }

    private bool ApplySpringBuff(string key)
    {
        var effect = BattleContent.StatusAttackBuffs?.GetValueOrDefault(key);
        if (effect is null) return false;
        return ApplyActorBuff(PlayerCombatId, key, 0, effect, PlayerCombatId);
    }

    private void RebuildMapEnemies()
    {
        _mapRoster = new WorldEnemyRoster(_session, _map.Id, _map.EnemySpawns,
            [], _hero?.Level ?? 1, _map.BreakableSpawn, _questEnemyWatch,
            random: () => GD.Randf(), story: new StoryWorldEnemyWorld(_session.StoryWorld));

        foreach (var placed in _mapRoster.Enemies) SpawnMapEnemy(placed, "进场");
        GD.Print($"[enemies] {_map.Id}：{_map.EnemySpawns.Length} 个刷怪点，上场 {_enemies.Count} 只");
        SettleFinishedEncounters();
    }

    private void SettleFinishedEncounters()
    {
        foreach (var encounter in _session.StoryWorld.ActiveEncounters)
        {
            if (encounter.Map != _map.Id || encounter.Spawns.Count == 0) continue;
            // 正在等的这一场交给 UpdateStoryEncounter：它要接结果分支续播，不能在这里静默收尾。
            if (encounter.Id == _waitingEncounter) continue;

            var finished = encounter.Spawns.All(spawn =>
            {
                var state = _session.EnemyVitals(
                    WorldEnemyRoster.EncounterSpawnId(encounter.Id, spawn.Id));
                return (state.DeadPermanently || state.DeadUntilMs is not null) && state.Capture is null;
            });
            if (!finished) continue;

            _session.StoryWorld.Apply(new EncounterResultCommand(encounter.Id, "won"));
            GD.Print($"[encounter] {encounter.Id} 的怪已经全部倒下，补记胜利（结果原本没写回）");
        }
    }

    private void SyncMapEnemies(string? source = null)
    {
        if (_mapRoster is null) return;

        var (added, removed) = _mapRoster.Refresh();
        foreach (var key in removed) RemoveMapEnemy(key);
        foreach (var placed in added)
        {
            SpawnMapEnemy(placed, source ?? "名册");
            if (source is not null)
                GD.Print($"[enemies] {source}：{placed.EnemyId} 上场于 ({placed.X:F0}, {placed.Y:F0})");
        }
    }

    private void ApplyStoryEnemyRemovals()
    {
        var commands = _session.StoryWorld.PendingEnemyRemovals;
        if (commands.Count == 0) return;

        var placed = _enemies
            .Where(enemy => enemy.SpawnKey is not null)
            .Select(enemy => new StoryEnemyRemovalRules.Placed(enemy.SpawnKey!, enemy.EnemyInfo.Id))
            .ToList();
        var selection = StoryEnemyRemovalRules.Select(_map.Id,
            commands.Select(command => new StoryEnemyRemovalRules.Command(command.Map, command.EnemyId, command.All))
                .ToList(), placed);

        foreach (var key in selection)
        {
            var enemy = _enemies.FirstOrDefault(entry => entry.SpawnKey == key);
            if (enemy is null) continue;
            _session.RemoveStoryEnemy(key);
            CancelEnemyAction(enemy);
            _enemies.Remove(enemy);
            _threat?.RemoveActor(enemy.CombatId);
            ForgetFocusedEnemy(enemy.CombatId);
            enemy.QueueFree();
            GD.Print($"[enemies] 剧情移除 {enemy.EnemyInfo.Id}（{enemy.SpawnKey}）");
        }

        _session.StoryWorld.ConsumeEnemyRemovals();
    }

    private void SpawnMapEnemy(PlacedEnemy placed, string source)
    {
        _battle ??= _loader.Battle;
        var definition = _battle.Enemies.FirstOrDefault(enemy => enemy.Id == placed.EnemyId);
        if (definition is null)
        {
            GD.PushWarning($"[enemies] {source}：找不到敌人 {placed.EnemyId}，跳过");
            return;
        }

        if (_enemies.Any(enemy => enemy.SpawnKey == placed.Key)) return;

        var enemy = new EnemyNode { Name = $"Enemy-{placed.Key}", SpawnKey = placed.Key };
        _entities.AddChild(enemy);
        enemy.SetupEnemy(definition, _textures,
            definition.ToFighter(_hero?.Level ?? 1, _mapRoster?.PartySize ?? 1,
                placed.Elite ?? false, placed.Level));
        var home = PlaceOnMap(placed.Key, placed.X, placed.Y);
        enemy.TeleportTo(new Vector2(home.X, home.Y));
        enemy.Home = home;
        AttachEnemyCombat(enemy);
        if (placed.Health is { } health) enemy.RestoreHealth(health);
        _enemies.Add(enemy);
    }

    private Vec2 PlaceOnMap(string key, float x, float y)
    {
        var inside = ClampToMap(x, y);
        if (inside.X == x && inside.Y == y) return inside;

        if (_grid is null) return inside;

        var spot = LootLanding(inside);
        for (var step = 0; step < 8 && TakenSpot(spot); step += 1)
            spot = LootLanding(StepInside(spot));

        GD.Print($"[enemies] {key} 的刷怪点在图外 ({x:F0}, {y:F0})，折回 ({spot.X:F0}, {spot.Y:F0})");
        return spot;
    }

    private Vec2 ClampToMap(float x, float y) => new(
        Math.Clamp(x, 16f, Math.Max(16f, _map.Width - 16f)),
        Math.Clamp(y, 16f, Math.Max(16f, _map.Height - 16f)));

    private Vec2 StepInside(Vec2 point)
    {
        var toward = new Vec2(_map.Width / 2f, _map.Height / 2f);
        var direction = toward - point;
        var length = MathF.Max(1f, MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y));
        return ClampToMap(point.X + direction.X / length * 16f, point.Y + direction.Y / length * 16f);
    }

    private bool TakenSpot(Vec2 point) => _enemies.Any(enemy =>
        MathF.Abs(enemy.Position.X - point.X) < 12f && MathF.Abs(enemy.Position.Y - point.Y) < 12f);

    private void RemoveMapEnemy(string key)
    {
        var enemy = _enemies.FirstOrDefault(node => node.SpawnKey == key);
        if (enemy is null) return;
        CancelEnemyAction(enemy);
        _enemies.Remove(enemy);
        _threat?.RemoveActor(enemy.CombatId);
        ForgetFocusedEnemy(enemy.CombatId);
        var tween = CreateTween();
        tween.TweenProperty(enemy, "modulate:a", 0f, 0.45f);
        tween.TweenCallback(Callable.From(enemy.QueueFree));
    }

    public string CurrentMapId => _map.Id;

    public bool StageActive => _stage.Active;

    public void ApplyStoryCommand(StoryWorldCommand command)
    {
        _session.StoryWorld.Apply(command);

        switch (command)
        {
            case PlaceCommand place:
                PlaceStoryActor(place);
                break;

            case RemoveCharacterCommand remove:
                RemoveStoryActor(remove.CharacterId, remove.All);
                break;

            case ActorVisibilityCommand visibility:
                SetStoryActorVisibility(visibility.Actor, visibility.CharacterId, visibility.Visible);
                break;

            case TravelCommand travel:
                TravelWorld(travel.Destination.Map, travel.Destination.X, travel.Destination.Y,
                    travel.Destination.Facing);
                break;

            case ToggleEntityCommand toggle:
                ToggleStoryEntity(toggle);
                break;

        }
    }

    private void PlaceStoryActor(PlaceCommand command)
    {
        var entity = command.Entity;
        var appearance = _session.StoryWorld.ActorAppearance(entity.Actor!);
        if (!_loader.World.Actors.TryGetValue(appearance, out var definition))
        {
            GD.PushWarning($"[world] place 找不到角色 {appearance}（{entity.Id}）");
            return;
        }

        _stage.PlaceFromWorld(new WorldPlace(
            entity.Id, entity.Map!, entity.Actor!, entity.CharacterId,
            entity.X ?? 0f, entity.Y ?? 0f, entity.Facing, entity.Visible != false,
            command.VisitScope ? "visit" : null));

        if (!_stage.Active && entity.Map == _map.Id)
        {
            var target = new Vec2(entity.X ?? 0f, entity.Y ?? 0f);
            ActorNode node;

            var existing = _npcs.FirstOrDefault(npc => npc.EntityId == entity.Id);
            if (existing is not null)
            {
                node = existing.Node;
                node.TeleportTo(new Vector2(target.X, target.Y));
                if (entity.Facing is { Length: > 0 } facing) node.SetFacing(facing);
                node.Visible = entity.Visible != false;
            }
            else
            {
                // 正式登记副本要顶掉同一个人物的静态副本，否则同图会站两个同名 NPC
                // （原版投影里的 located 集合做的就是这件事）。
                if (PlacementAuthority.Of(entity) == PlacementAuthority.Registered &&
                    !string.IsNullOrEmpty(entity.CharacterId))
                {
                    var duplicate = _npcs.FirstOrDefault(npc =>
                        npc.CharacterId == entity.CharacterId &&
                        PlacementAuthority.Of(npc.EntityId, null) != PlacementAuthority.Registered);
                    if (duplicate is not null)
                    {
                        _npcs.Remove(duplicate);
                        ForgetMapActor(duplicate);
                        duplicate.Node.QueueFree();
                    }
                }

                var actor = new ActorNode { Name = entity.Id };
                _entities.AddChild(actor);
                actor.Setup(definition, _mapLease!, entity.Facing ?? "south");
                actor.TeleportTo(new Vector2(target.X, target.Y));
                _npcs.Add(new MapNpc(actor, entity.Id, entity.Actor!, entity.CharacterId, definition,
                    _session.StoryWorld.IsPlaced(entity.Id)));
                node = actor;
            }

            EvictPlayerFrom(target, definition.Collision, definition.Id);

            var settled = SettleLanding(NavigationShape.Of(definition.Collision), target,
                new NavigationIgnore(node, Party: false));
            if (settled.X != target.X || settled.Y != target.Y)
            {
                node.TeleportTo(new Vector2(settled.X, settled.Y));
                EvictPlayerFrom(settled, definition.Collision, definition.Id);
            }
        }

        GD.Print($"[world] place {entity.Id}（{definition.Name}）→ {entity.Map} ({entity.X:F0}, {entity.Y:F0})");
    }

    public void RemoveStoryActor(string? characterId, bool all = true)
    {
        if (characterId is null) return;

        _stage.RemoveByCharacterId(characterId);

        foreach (var npc in _npcs.Where(candidate => candidate.CharacterId == characterId).ToArray())
        {
            _npcs.Remove(npc);
            ForgetMapActor(npc);
            npc.Node.QueueFree();
            if (!all) break;
        }
    }

    public void SetStoryActorVisibility(string actor, string? characterId, bool visible)
    {
        _stage.SetVisibilityByActor(actor, characterId, visible);

        foreach (var npc in _npcs)
        {
            if (npc.ActorId != actor) continue;
            if (characterId is not null && npc.CharacterId != characterId) continue;
            npc.Node.Visible = visible;
            npc.Node.Modulate = new Color(1f, 1f, 1f, visible ? 1f : 0f);
        }
    }

    private void ToggleStoryEntity(ToggleEntityCommand toggle)
    {
        var npc = _npcs.FirstOrDefault(candidate => candidate.EntityId == toggle.Id);
        if (npc is null) return;

        if (toggle.Kind == "remove")
        {
            _npcs.Remove(npc);
            ForgetMapActor(npc);
            npc.Node.QueueFree();
            return;
        }

        npc.Node.Visible = toggle.Kind == "show";
    }

    private void ForgetMapActor(MapNpc npc)
    {
        if (ReferenceEquals(_nearNpc, npc)) _nearNpc = null;
        _mapActors.RemoveAll(record => ReferenceEquals(record.Node, npc.Node));
    }

    /// <summary>剧本 travel：舞台开着就切舞台幕布；否则切正式世界。</summary>
    public void ShowStageForTravel(string mapId, float x, float y, string facing)
    {
        _stage.ShowStageMap(mapId, new Vec2(x, y), facing);
        HideWorld();
        UpdateCameraLimits();
    }

    public void TravelWorld(string mapId, float x, float y, string facing)
    {
        if (_loadingWorld) return;
        if (_stage.Active)
        {
            ShowStageForTravel(mapId, x, y, facing);
            return;
        }

        var target = _loader.LoadMap(mapId);
        var spawn = FindStandable(new CollisionGrid(target), new Vec2(x, y));
        LoadMapWithLoading(mapId, spawn, facing, "正在前往新的地方…", 0f);
        TeleportPlayer(new Vec2(x, y), facing);
    }

    public void TeleportPlayer(Vec2 position, string facing)
    {
        if (!_grid.CanStandOnTerrain(position.X, position.Y))
            position = FindStandable(_grid, position);
        else
            position = SettleLanding(NavigationShape.Of(_player.Definition.Collision), position,
                new NavigationIgnore(PlayerCombatId));

        _session.Moved(position, facing);
        _session.MovePlayer(new StoryLocation
        {
            Map = _map.Id,
            X = position.X,
            Y = position.Y,
            Facing = facing,
        });
        _player.TeleportTo(new Vector2(position.X, position.Y));
        _player.SetFacing(facing);
    }

    /// <summary>
    /// 挪某个角色一步：碰撞按**它自己**的盒子判，
    /// 位移写回**它自己**的节点。
    ///
    /// 这里必须显式指定给谁走，不能「谁被操控就挪谁」——操控切到伙伴时，主角的托管 AI
    /// 仍然要按跟随／追近自己走（<see cref="MovePlayer"/>），以前两条路共用一份
    /// 「挪被控者」的实现，托管 AI 一走就把**被控的伙伴**拖走，玩家看到的就是
    /// 「切换操控后，刚接手的那个人自己往某个地方走」。主角那条路同时给会话落账
    /// （存档与检查点的口径始终是主角）。
    /// </summary>
    private bool MoveActor(ActorNode node, Vec2 step, bool asHero)
    {
        var from = asHero ? _position : new Vec2(node.Position.X, node.Position.Y);
        var box = node.Definition.Collision;
        var applied = MovementSolver.Step(_grid, from, step,
            body: new Rect(box.X, box.Y, box.Width, box.Height));
        if (applied.X == 0f && applied.Y == 0f) return false;

        if (asHero)
        {
            _session.Moved(_position + applied, _player.Facing);
            _player.Position = new Vector2(_position.X, _position.Y);
        }
        else
        {
            node.Position += new Vector2(applied.X, applied.Y);
        }

        return true;
    }

    private bool Move(Vec2 step) => MoveActor(ControlledNode(), step, asHero: !ControllingAlly);

    private bool MovePlayer(Vec2 step) => MoveActor(_player, step, asHero: true);

    private void StepPlayerMotion(Vec2 axis, float seconds)
    {
        if (!Conditions.CanMove(PlayerCombatId)) return;
        if (_fallen.Contains(PlayerCombatId) || _defeated) return;
        if (_storyPlaying)
        {
            _route = null;
            _combatApproach = null;
            _heroFollow = null;
            _player.UpdateMotion(Vec2.Zero, false);
            return;
        }

        if (axis.X != 0f || axis.Y != 0f)
        {
            _route = null;
            _combatApproach = null;
            _heroFollow = null;
            _autoAttack.Release();

            if (CombatRuntime.IsCastingSkill(ControlledId)) return;
            if (!CombatRuntime.HasPendingBasicStrike(ControlledId))
            {
                CombatRuntime.CancelAttack(ControlledId);
                Conditions.ClearTemporary(ControlledId);
                CombatRuntime.ClearSelection(ControlledId);
            }
            if (CombatRuntime.ActionState(ControlledId) != CombatState.Idle)
            {
                _player.UpdateMotion(Vec2.Zero, false);
                return;
            }

            var moved = MovePlayer(axis * (MovementSpeed(PlayerCombatId, PlayerSpeed) * seconds));
            _player.UpdateMotion(axis, moved);
        }
        else if (StandingAutoAttack())
        {
        }
        else if (_combatApproach is { } chase)
        {
            _route = null;
            FollowCombatApproach(chase, seconds);
        }
        else if (_route is not null)
        {
            FollowRoute(seconds * 1000f, seconds);
        }
        else if (_heroFollow?.Invoke() is { } waypoint)
        {
            var toTarget = waypoint - _position;
            var distance = toTarget.Length;
            if (distance <= 0.5f)
            {
                _player.UpdateMotion(Vec2.Zero, false);
                return;
            }

            var direction = new Vec2(toTarget.X / distance, toTarget.Y / distance);
            var step = MathF.Min(distance, MovementSpeed(PlayerCombatId, PlayerSpeed) * seconds);
            var moved = MovePlayer(direction * step);
            _player.UpdateMotion(direction, moved);
        }
        else
        {
            _player.UpdateMotion(Vec2.Zero, false);
        }
    }

    private void StepControlledMember(Vec2 axis, float seconds)
    {
        var allyNode = FindAllyNode(ControlledId);
        if (allyNode is not null && Conditions.CanMove(ControlledId) && !_fallen.Contains(ControlledId) && !_defeated)
        {
            if (axis.X != 0f || axis.Y != 0f)
            {
                var blocked = CombatRuntime.IsCastingSkill(ControlledId);
                if (!blocked)
                {
                    if (!CombatRuntime.HasPendingBasicStrike(ControlledId))
                    {
                        CombatRuntime.CancelAttack(ControlledId);
                        Conditions.ClearTemporary(ControlledId);
                        CombatRuntime.ClearSelection(ControlledId);
                    }
                    blocked = CombatRuntime.ActionState(ControlledId) != CombatState.Idle;
                }

                if (blocked)
                {
                    allyNode.UpdateMotion(Vec2.Zero, false);
                }
                else
                {
                    allyNode.HaltMotion();
                    var moved = Move(axis * (MovementSpeed(ControlledId, PlayerSpeed) * seconds));
                    allyNode.UpdateMotion(axis, moved);
                }
            }
            else if (_route is not null)
            {
                FollowRoute(seconds * 1000f, seconds);
            }
            else
            {
                allyNode.StepMotion(MovementSpeed(ControlledId, PlayerSpeed) * seconds);
            }
        }

        StepPlayerMotion(Vec2.Zero, seconds);
    }

    private void FollowRoute(float deltaMs, float seconds)
    {
        var waypoint = _route!.Next(ControlPosition, deltaMs);

        if (_route.State != WalkState.Walking)
        {
            if (_route.State == WalkState.Failed) GD.PushWarning($"走位失败：{_route.Error}");
            _route = null;
            ControlledNode().UpdateMotion(Vec2.Zero, false);
            return;
        }

        if (waypoint is null)
        {
            ControlledNode().UpdateMotion(Vec2.Zero, false);
            return;
        }

        var toTarget = waypoint.Value - ControlPosition;
        var distance = toTarget.Length;
        if (distance < 1e-4f) return;

        var direction = new Vec2(toTarget.X / distance, toTarget.Y / distance);
        var stepLength = MathF.Min(MovementSpeed(ControlledId, PlayerSpeed) * seconds, distance);

        var moved = Move(direction * stepLength);

        ControlledNode().UpdateMotion(direction, moved);
    }

    /// <summary>
    /// 从出口进来时脚点正压在回程出口上。必须真的走开一段距离，
    /// 那个出口才重新生效，否则会立刻被弹回上一张图。
    /// </summary>
    private void UpdateExitLock()
    {
        if (!_exitLocked) return;
        if (GoalQuery.Distance(ControlPosition, _exitLockAnchor) <= ExitLockRadius) return;
        _exitLocked = false;
    }

    private void CheckExits()
    {
        if (_exitLocked) return;
        // 演出期间出口必须关着（原版演出时整个世界冻结）。
        // 剧情区常常压在出口瓦片上：触发器先起剧情，同一帧的出口判定就会直接切图，
        // LoadMap 又会顺手 _stage.Release()——舞台演员当场消失、正文还在播。
        if (_storyPlaying) return;

        foreach (var exit in _map.Exits)
        {
            if (_loadingWorld || !_session.StoryWorld.ExitEnabled(_map.Id, exit)) continue;
            if (!OverlapsFoot(ControlPosition, exit)) continue;

            var spawn = new Vec2(exit.Spawn.X, exit.Spawn.Y);
            LoadMapWithLoading(exit.Destination, spawn, exit.Facing, "正在前往新的地方…", 0f);
            _exitLocked = true;
            _exitLockAnchor = spawn;
            return;
        }
    }

    /// <summary>
    /// 脚底方框与出口格的相交判定。用的是整个 16×16 脚底而不是窄足底，
    /// 所以贴着出口边缘走过也会触发——原版就是这个口径。
    /// </summary>
    private static bool OverlapsFoot(Vec2 point, MapExit exit) =>
        point.X + FootRadius > exit.X && point.X - FootRadius < exit.X + exit.Width &&
        point.Y + FootRadius > exit.Y && point.Y - FootRadius < exit.Y + exit.Height;

    private void SetUpHud()
    {
        var layer = new CanvasLayer { Name = "Hud" };
        AddChild(layer);
        _hudLayer = layer;

        var hudColumn = new VBoxContainer
        {
            Name = "HudText",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hudColumn.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        hudColumn.OffsetLeft = 16f;
        hudColumn.OffsetRight = -16f;
        hudColumn.OffsetTop = 10f;
        hudColumn.OffsetBottom = 10f;
        hudColumn.AddThemeConstantOverride("separation", 2);
        layer.AddChild(hudColumn);

        _hud = CreateHudLabel(14, new Color(1f, 1f, 1f, 0.8f));
        hudColumn.AddChild(_hud);

        _questHud = CreateHudLabel(17, new Color(1f, 0.85f, 0.48f));
        hudColumn.AddChild(_questHud);

        _hintHud = CreateHudLabel(15, new Color(0.92f, 0.95f, 1f, 0.9f));
        _hintHud.GuiInput += @event =>
        {
            if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            if (_nearNpc is null || _storyPlaying || !CanControl()) return;
            TryInteract();
        };
        hudColumn.AddChild(_hintHud);

        _lootHud = CreateHudLabel(15, new Color(1f, 0.85f, 0.48f));
        hudColumn.AddChild(_lootHud);

        _lootMissedHud = CreateHudLabel(15, new Color(0.92f, 0.59f, 0.53f));
        hudColumn.AddChild(_lootMissedHud);

        _saveButton = CreateHudIconButton("QuickSave", SaveIconPath, QuickSave);
        _settingsButton = CreateHudIconButton("QuickSettings", SettingsIconPath, QuickSettings);
        PlaceHudButtons();

        // 键位／画面／操作方式三份偏好都存 user://（原版是 localStorage）。
        // 摇杆要用到键位与移动方式，所以先建偏好再建控件。
        _controlLayout = new ControlLayoutPreferences(ReadUserText, WriteUserText);
        _displaySettings = new DisplaySettings(ReadUserText, WriteUserText);

        Inotia4.Application.Main.ApplyHighRefresh(_displaySettings.HighRefresh);
        _controlSettings = new ControlSettings(ReadUserText, WriteUserText);

        _audio = new GameAudio(this, _loader.Audio, new AudioSettings(ReadUserText, WriteUserText),
            () => IsAudioSuspended());
        _audio.PrepareUi();
        PanelGfx.UiSound = name => _audio?.Ui(name);

        _stick = new VirtualStick { Name = "VirtualStick" }
            .Skin(_loader.ControlSkin)
            .TextureOf(path => _heroLease.Texture(path))
            .Preferences(_controlLayout)
            .Settings(_controlSettings);
        layer.AddChild(_stick);

        _actionControls = new ActionControls { Name = "ActionControls" }
            .Skin(_loader.ControlSkin)
            .TextureOf(path => _heroLease.Texture(path))
            .With(new ActionControlsHandlers(
                Attack: () =>
                {
                    if (!CanControl()) return;
                    if (_nearNpc is not null && !_storyPlaying)
                    {
                        TryInteract();
                        return;
                    }
                    PlayerAttack();
                },
                Item: UseShortcutItem,
                Swap: SwapShortcutPage)
            {
                Skill = CastShortcutSkill,
            })
            .Preferences(_controlLayout);
        layer.AddChild(_actionControls);

        _statusHud = new StatusHud { Name = "StatusHud" }
            .Skin(_loader.StatusHud, path => _heroLease.Texture(path))
            .Data(
                ReadControlledValues,
                ReadOtherMembers,
                id =>
                {
                    if (CanControl()) SwitchControl(id);
                },
                id => Conditions.Snapshot(id),
                () => ControlledId,
                () => _session.CombatTick,
                () => { if (CanControl()) FocusSelected(); },
                () => HudPortraitFor(ControlledId),
                () => { if (CanControl()) ToggleStatus(); });
        layer.AddChild(_statusHud);

        _hudNotice = new Label
        {
            Name = "HudNotice",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _hudNotice.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        _hudNotice.OffsetLeft = -280f;
        _hudNotice.OffsetRight = 280f;
        _hudNotice.AddThemeFontSizeOverride("font_size", InstantMessages.FontSize);
        _hudNotice.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.6f));
        _hudNotice.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
        _hudNotice.AddThemeConstantOverride("shadow_offset_x", 1);
        _hudNotice.AddThemeConstantOverride("shadow_offset_y", 1);
        layer.AddChild(_hudNotice);
    }

    /// <summary>
    /// 界面里不着急的那一半：地名条、黑场、对话框、六页菜单、系统页、键位编辑器与剧情导演。
    ///
    /// 分成两段只为让进度条在 0.78 与 0.84 之间多走一档：实测整段界面装配 220ms，
    /// 没有哪一块占大头（最贵的两块各 45ms 上下，六页菜单合计 40ms），
    /// 全改成懒建只能省 40–60ms，却要把十几处引用改成「用前先建」，收益与回归面不成比例。
    /// 量法与取舍见上面几行。
    /// </summary>
    private void SetUpHudPages()
    {
        var layer = _hudLayer ?? throw new InvalidOperationException("界面层还没建");

        // 顶部中间的地名／对象名条：压在 HUD 文字之上、菜单遮罩之下，
        // 所以排在 _fade 与 _menuBackdrop 之前。地图名在 LoadMap 里灌进去。
        _nameBanner = new Inotia4.Ui.NameBanner();
        layer.AddChild(_nameBanner);

        _fade = new ColorRect
        {
            Name = "Fade",
            Color = new Color(0f, 0f, 0f, 1f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _fade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_fade);

        _menuBackdrop = new ColorRect
        {
            Name = "MenuBackdrop", Visible = false,
            Color = new Color(0f, 0f, 0f, 0.65f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _menuBackdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_menuBackdrop);

        // 对话框放在黑场之上：淡入淡出只盖画面，不该把台词一起盖掉。
        _dialogue = new DialogueBox { Name = "Dialogue", AutoAdvance = _autoStory }
            .Texture(path => _heroLease.Texture(path));   // 立绘与界面图都从这条租约取
        layer.AddChild(_dialogue);

        _choice = new ChoiceBox { Name = "Choice", AutoPick = _autoStory };
        layer.AddChild(_choice);

        _skipButton = new SkipButton { Name = "Skip" };
        _skipButton.Setup(_loader.ControlSkin, path => _textures.Texture(path));
        _skipButton.Pressed += () => _story.Skip();
        layer.AddChild(_skipButton);

        _questPopup = new QuestPopup
        {
            Name = "QuestPopup",
            AutoConfirm = _autoStory,
            ItemName = id => _loader.Items.Items.GetValueOrDefault(id)?.Name ?? id,
            UiSound = name => _audio?.Ui(name),
        };
        layer.AddChild(_questPopup);

        _defeatPanel = new DefeatPanel
        {
            Name = "Defeat",
            AutoConfirm = _autoStory,
            PrayerCount = PrayerCountInBag,
            Revive = ReviveByPrayer,
            Restore = RestoreCheckpointAsync,
        };
        layer.AddChild(_defeatPanel);

        _bagPanel = new BagPanel { Name = "Bag" };
        layer.AddChild(_bagPanel);

        _shopPanel = new ShopPanel { Name = "Shop" };
        layer.AddChild(_shopPanel);

        _craftPanel = new CraftingPanel { Name = "Crafting" };
        layer.AddChild(_craftPanel);

        _skillPanel = new SkillPanel { Name = "Skills" };
        layer.AddChild(_skillPanel);

        _statusPanel = new StatusPanel { Name = "Status" };
        layer.AddChild(_statusPanel);

        _questPanel = new QuestPanel { Name = "Quests" };
        layer.AddChild(_questPanel);

        _partyPanel = new PartyPanel { Name = "Party" };
        layer.AddChild(_partyPanel);

        // 右上角关闭钮：原版每页都画，系统页自带一份（它还有子页），这里补其余五页。
        // 原版写的是 947,24 51×51——那是按「画布宽 ≈ 1010」排的（947+51=998）。
        // 本项目的逻辑视口是 960 宽，照搬会出界 38 像素、只剩一条边，所以按 960
        // 重新右对齐：960 - 51 - 13 = 896，右边距 13。
        // 素材用 story-interface 的 close（与系统页同一张图）。
        _menuClose = new Control
        {
            Name = "MenuClose", Visible = false,
            // 与各面板同一套 960×640 画布坐标，所以和面板一样钉在屏幕中央（见 DesignStage）。
            Size = new Vector2(960f, 640f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 100,
        };
        DesignStage.Center(_menuClose);
        layer.AddChild(_menuClose);
        var menuCloseHit = PanelGfx.Hit(MenuCloseX, MenuCloseY, 51f, 51f, CloseOpenMenuPage, _menuClose);
        PanelGfx.Image(path => _heroLease.Texture(path), CloseAsset, 0f, 0f, 51f, 51f, menuCloseHit);

        _systemPanel = new SystemPanel { Name = "System" };
        layer.AddChild(_systemPanel);

        _layoutEditor = new ControlLayoutEditor { Name = "ControlLayoutEditor" }
            .Skin(_loader.ControlSkin)
            .TextureOf(path => _heroLease.Texture(path))
            .Assets(key => _loader.MenuPanels.Asset(key))
            .Preferences(_controlLayout!)
            .Settings(_controlSettings);
        _layoutEditor.Preview = () => _actionControls?.Slots ?? [];
        _layoutEditor.Closed = CloseControlEditor;
        layer.AddChild(_layoutEditor);

        _story = new StoryDirector(_loader.Campaign, _journal, _bag, _session, new SceneWorldCommands(this),
            id => _session.StoryWorld.Encounter(id) is not null)
        {
            NodeCommitted = CheckpointStoryWorld,
        };
        _session.CaptureStoryRuns = () => _story?.CaptureRuns();
        _session.ApplyStoryRuns = runs => _story?.RestoreRuns(runs);
        if (_session.TakePendingStoryRuns() is { } pendingRuns) _story.RestoreRuns(pendingRuns);
        _tracker = new QuestTracker(_loader.Campaign, _journal);
    }

    private static Label CreateHudLabel(int size, Color color)
    {
        var label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Text = string.Empty,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeConstantOverride("outline_size", 4);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.7f));
        return label;
    }

    private static void SetHudLine(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
    }

    private void PlayFadeIn()
    {
        if (_fade is null) return;

        _fadeTween?.Kill();
        _fade.Modulate = new Color(1f, 1f, 1f, 1f);
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(_fade, "modulate:a", 0f, 0.28f);
    }

    private Vec2 CombineAxes()
    {
        var axis = _stick.Axis;
        return new Vec2(axis.X, axis.Y);
    }

    /// <summary>
    /// 播一段剧本。演出项由领域层的 <see cref="StoryDirector"/> 逐步给出，
    /// 这里只负责把它变成画面：对白等按键、淡入淡出走补间、舞台直接换位置。
    ///
    /// 用 <c>async void</c> 是因为它由引擎事件触发（不是被 await 的库函数），
    /// 内部用 <c>_storyPlaying</c> 把走位与出口判定挡在外面。
    /// </summary>
    private async void PlayStory(string scriptId, string? eventId = null)
    {
        if (_storyPlaying)
        {
            GD.PushWarning($"[story] 已在演出中，忽略 {scriptId}");
            return;
        }

        if (_waitingEncounter is { Length: > 0 })
        {
            _encounterDeferredStory ??= (scriptId, eventId);
            GD.Print($"[story] 遭遇战 {_waitingEncounter} 还没结算，暂缓 {scriptId}");
            return;
        }

        _storyOwnerScript = scriptId;
        _storyEventId = eventId;
        // 开场黑幕要先于舞台建起来：跨图演出时正式世界会被拆掉重建，那一瞬不能给玩家看见。
        ShowInitialCurtain(scriptId);
        _story.Start(scriptId, _storyEventId);
        await RunStoryLoop();
    }

    private void ShowInitialCurtain(string scriptId)
    {
        if (_fade is null) return;

        var map = EntryStageMap(scriptId);
        if (map is null || map.Length == 0 || map == _map?.Id) return;

        _fadeTween?.Kill();
        _fadeTween = null;
        _fade.Color = new Color(0f, 0f, 0f, 1f);
        _fade.Modulate = new Color(1f, 1f, 1f, 1f);
        _initialCurtain = true;
    }

    private string? EntryStageMap(string scriptId)
    {
        if (!_loader.Campaign.Scripts.TryGetValue(scriptId, out var script)) return null;

        foreach (var node in script.Nodes)
        {
            if (node.Id != script.Entry) continue;

            foreach (var item in node.Presentation)
            {
                if (StoryType.Of(item) != "stage") continue;
                var map = StoryType.Text(item, "map");
                if (map.Length > 0) return map;
            }
        }

        return null;
    }

    private void ClearInitialCurtain()
    {
        if (!_initialCurtain) return;

        _initialCurtain = false;
        _fadeTween?.Kill();
        _fadeTween = null;
        _fade.Modulate = new Color(1f, 1f, 1f, 0f);
    }

    private async void ContinueStory()
    {
        if (_storyPlaying)
        {
            _storyResumeQueued = false;
            GD.PushWarning("[story] 上一段演出还没收尾，忽略续播");
            return;
        }

        _storyResumeQueued = false;
        await RunStoryLoop();
    }

    /// <summary>
    /// 从导演当前所在的节点往下演到「剧本结束」或「停在遭遇战」。
    ///
    /// 停在 <c>waitEncounter</c> 时**不**记事件完成：那一段的运行状态是 waiting，
    /// 结果回来后续播，最后才在 <c>end</c> 上落账。
    /// </summary>
    /// <summary>
    /// 跳过钮能不能露面。逐项对应条件：演出中、就绪、未挂起、
    /// 没有立绘／遮罩、不在加载、没战败、没有选项面板、没有任务弹窗、导演允许跳过。
    /// Godot 版没有立绘与遮罩层，其余照搬；自动演出本来就不等按键，也不出这个钮。
    /// </summary>
    private bool CanSkipStory() =>
        _storyPlaying && !_autoStory && _story.CanSkip &&
        _loading?.Visible != true && _defeatPanel?.Visible != true && !_choice.Visible && !_questPopup.Visible;

    private void OnStorySkipped()
    {
        _dialogue.Skip();
        _storySkip?.Cancel();
    }

    private void SuspendPartyActions()
    {
        foreach (var ally in _allies)
        {
            if (!GodotObject.IsInstanceValid(ally.Node)) continue;
            ally.Ai.Cancel();
            Conditions.ClearTemporary(ally.MemberId);
            ally.Node.HaltMotion();
        }
    }

    private async Task RunStoryLoop()
    {
        var scriptId = _storyOwnerScript ?? _story.ScriptId ?? string.Empty;

        _storyPlaying = true;
        _dialogue.AutoAdvance = _autoStory;

        // 队伍动作挂起：演出期间这些人由剧本摆，不能带着旧路径进来。
        SuspendPartyActions();

        _story.Skipped += OnStorySkipped;
        _storySkip?.Dispose();
        _storySkip = new CancellationTokenSource();

        _storyAudioBaseline ??= _audio?.CaptureStoryAudio();
        _audio?.EnterStory();

        // 无头模式没有画面，走位直接落位；--autoplay 要验证真实走位，所以不瞬移。
        _stage.Instant = DisplayServer.GetName() == "headless";

        _nodeCheckStartCaptures = _checkpointCaptures;
        _nodeCheckStartTime = _session.GameTimeMs;
        _nodeCheckStartSession = _checkpoint.Peek() is { } before
            ? JsonSerializer.Serialize(before.Session, SaveGameStore.JsonOptions)
            : string.Empty;

        var commandMark = _story.CommandLog.Count;        // 世界命令同理：收尾提交只投影这一段演出下发过的命令。
        var worldMark = _story.WorldLog.Count;
        var skippedMark = _story.SkippedCommands.Count;
        var unknownMark = _story.UnknownConditions.Count;

        var played = 0;
        var startedAt = Time.GetTicksMsec();

        while (true)
        {
            StoryStep step;
            try
            {
                step = _story.Next();
            }
            catch (Exception error)
            {
                GD.PushWarning($"[story] 取下一步失败：{error}");
                break;
            }

            if (step.Kind == StoryStepKind.End)
            {
                if (step.Note is not null) GD.Print($"[story] 停在：{step.Note}");
                break;
            }

            if (step.Kind == StoryStepKind.Encounter)
            {
                var plan = step.Encounter!;

                // 无头与自动演出没有战斗输入，按-story 的做法直接判胜；
                // 没有刷怪点的遭遇也没有可打的东西，同样按胜利走（否则永远凑不齐清场条件）。
                // 判胜会写 encounter-result，名册随即撤下这场遭遇的怪。
                if (!_suppressEncounterAutoWin &&
                    (_autoStory || DisplayServer.GetName() == "headless" || plan.Spawns.Count == 0))
                {
                    GD.Print($"[encounter] {plan.Id} 自动判胜（自动模式或没有刷怪点）");
                    _story.ResolveEncounter(true);
                    continue;
                }

                ApplyStoryEnemyRemovals();
                _waitingEncounter = plan.Id;
                GD.Print($"[encounter] {plan.Id} 开打：{plan.Spawns.Count} 个生成点，" +
                         $"地图 {plan.Map}，当前 {_map.Id}");
                break;
            }

            played++;
            // 单个演出项失败（缺素材、空引用等）只丢这一步，不能让异常逃出循环：
            // 逃出去 _storyPlaying 就永远是 true，出口/触发器/交互会一路锁死。
            try
            {
                await PlayPresentation(step.Item);
            }
            catch (Exception error)
            {
                GD.PushWarning($"[story] 演出项 {StoryType.Of(step.Item)} 失败：{error}");
            }
        }

        _dialogue.Close();
        _story.Skipped -= OnStorySkipped;
        _storySkip?.Dispose();
        _storySkip = null;
        _storyPlaying = false;
        ClearInitialCurtain();
        try
        {
            CommitStage(worldMark, _storyEventId);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[story] 收尾提交失败：{error}");
            // 收尾失败也必须把正式世界放回来：舞台释放本身再抛一次的话，
            // RevealWorld 会跟着被跳过，画面只剩 HUD，人物看不见、镜头也像卡住。
            try
            {
                _stage.Release();
            }
            catch (Exception releaseError)
            {
                GD.PushWarning($"[story] 舞台释放失败：{releaseError}");
            }

            RevealWorld();
            UpdateCameraLimits();
        }
        // 收尾提交（舞台落位、可能的切图）算一个新的回退点：原版 `project()` 走完就记一次，
        // 玩家在剧情之后挂掉不该退回演出开始前。
        CheckpointStoryWorld();
        SettleFriendlyUnitsAroundHero();
        RestoreStoryAudioBaseline();
        _autoSaver.Request(AutoSaveReason.StoryEvent, (long)Time.GetTicksMsec());

        if (_waitingEncounter is not null)
        {
            GD.Print($"[story] {scriptId} 停在遭遇战 {_waitingEncounter}，打完再续");
            if (_skipCheck)
            {
                GD.Print("[skipcheck] 剧本停在遭遇战，自检未跑完");
                GetTree().Quit(1);
            }

            return;
        }

        FinishStory(scriptId);

        var elapsed = Time.GetTicksMsec() - startedAt;
        var commands = _story.CommandLog.Skip(commandMark).ToList();
        var skipped = _story.SkippedCommands.Skip(skippedMark).ToList();
        var unknown = _story.UnknownConditions.Skip(unknownMark).ToList();

        GD.Print($"[story] {scriptId} 播完 {played} 项、{elapsed} 毫秒，" +
                 $"执行命令 {commands.Count} 条，未实现演出 {_unsupportedStoryTypes.Count} 类");

        if (commands.Count > 0)
            GD.Print($"[story] 命令：{string.Join("，", commands)}");

        if (skipped.Count > 0)
            GD.Print($"[story] 跳过的命令类型：{string.Join(", ", skipped)}");

        if (_unsupportedStoryTypes.Count > 0)
            GD.Print($"[story] 未实现：{string.Join(", ", _unsupportedStoryTypes)}");

        if (unknown.Count > 0)
            GD.Print($"[story] 未知条件：{string.Join(", ", unknown)}");

        var touched = _journal.Entries
            .Where(pair => pair.Value is QuestStatus.Active or QuestStatus.Ready or QuestStatus.Completed)
            .Select(pair => $"{pair.Key}={QuestJournal.Name(pair.Value)}")
            .ToList();
        if (touched.Count > 0)
            GD.Print($"[story] 任务变化：{string.Join("，", touched)}");

        // 自动模式在剧本播完后退出；但挂着的切图检查（--exitcheck）要继续走，
        // 否则永远到不了下一个触发点，自动链路验证就断在半路。
        // --hold：演完留在世界里，配合 --shot 截「剧情之后」的画面（任务指引等）。
        if (_autoStory && !_placementCheck && !_exitCheck && !_hold) GetTree().Quit();

        if (_skipCheck)
        {
            GD.Print($"[skipcheck] 剧本收尾：按下过跳过={_skipCheckFired}，跳过状态={_story.Skipping}，" +
                     $"舞台={(_stage.Active ? "仍开着" : "已释放")}");
            GetTree().Quit(_skipCheckFired ? 0 : 1);
        }
        if (_placementCheck) CompletePlacementCheck();
        if (_nodeCheck) CompleteNodeCheck(scriptId);
    }

    private void CompleteNodeCheck(string scriptId)
    {
        var captures = _checkpointCaptures - _nodeCheckStartCaptures;
        var peek = _checkpoint.Peek();
        var session = peek is null ? string.Empty : JsonSerializer.Serialize(peek.Session, SaveGameStore.JsonOptions);
        var moved = peek is not null && session != _nodeCheckStartSession;
        var newer = peek is not null && peek.Session.GameTimeMs >= _nodeCheckStartTime;

        var runsBefore = _story.CaptureRuns();
        var json = JsonSerializer.Serialize(_session.CaptureState(), SaveGameStore.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;
        _session.RestoreState(reloaded);
        var runsAfter = _story.CaptureRuns();
        var runsOk = runsAfter.Sequence == runsBefore.Sequence &&
                     runsAfter.CompletedEvents.Count == runsBefore.CompletedEvents.Count;

        var ok = captures >= 2 && moved && newer && runsBefore.Sequence > 0 && runsOk;
        GD.Print($"[nodecheck] {scriptId}：演出期间记了 {captures} 次检查点，" +
                 $"回退点会话已更新={moved}、时间 {peek?.Session.GameTimeMs ?? -1}（开演 {_nodeCheckStartTime}），" +
                 $"落点 {(peek is null ? "无" : $"{peek.Location.Map} ({peek.Location.X:F0},{peek.Location.Y:F0})")}；" +
                 $"运行表序号 {runsBefore.Sequence} → 往返后 {runsAfter.Sequence}（已完成 {runsAfter.CompletedEvents.Count} 条）→ " +
                 $"{(ok ? "通过" : "失败")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void FinishStory(string? scriptId)
    {
        if (scriptId is { Length: > 0 }) _journal.MarkEventDone(scriptId);
        _storyOwnerScript = null;
        _story.StageMap = null;   // 原版收尾会删 run.stage：mapFromStage 不能带进下一段剧本

        SettlePendingDefeat();

        if (_encounterDeferredStory is { } deferred && !_storyPlaying)
        {
            _encounterDeferredStory = null;
            GD.Print($"[story] 遭遇战结算完，续上被挡下的 {deferred.Script}");
            Callable.From(() => PlayStory(deferred.Script, deferred.EventId)).CallDeferred();
        }

        if (_exitCheck) BeginExitCheck();
    }

    private void CompletePlacementCheck()
    {
        LoadMap(StartMapId, StartPosition, "south");

        var placed = _npcs.Where(npc => npc.ActorId == "darius").ToList();
        GD.Print(placed.Count > 0
            ? $"[placementcheck] 卓拉德在营地 ({placed[0].Node.Position.X:F0}, {placed[0].Node.Position.Y:F0})，通过"
            : "[placementcheck] 卓拉德没出现，失败");
        GetTree().Quit(placed.Count > 0 ? 0 : 1);
    }

    private void BeginPlacementCheck()
    {
        var problems = new List<string>();

        var person = _loader.World.Actors.Values.FirstOrDefault(actor =>
            actor.Id != _loader.Campaign.Start.Actor &&
            actor.Collision.Width > 0f && actor.Collision.Height > 0f);
        if (person is null)
        {
            GD.Print("[placementcheck] 内容里找不到可摆的人物");
            GetTree().Quit(1);
            return;
        }

        StoryEntityState Entity(string id, float x, float y) => new()
        {
            Id = id,
            Map = _map.Id,
            Actor = person.Id,
            X = x,
            Y = y,
            Facing = "south",
            Visible = true,
        };

        var playerBefore = _position;
        PlaceStoryActor(new PlaceCommand(Entity("check-placement-a", playerBefore.X, playerBefore.Y)));
        var placedA = _npcs.FirstOrDefault(npc => npc.EntityId == "check-placement-a");
        if (placedA is null) problems.Add("没有摆出第一个人");
        else
        {
            var spot = new Vec2(placedA.Node.Position.X, placedA.Node.Position.Y);
            GD.Print($"[placementcheck] 摆位 {spot.X:F0},{spot.Y:F0}；主角 " +
                     $"{playerBefore.X:F0},{playerBefore.Y:F0} → {_position.X:F0},{_position.Y:F0}");
            if (OverlapsBoxes(spot, person.Collision, _position, _player.Definition.Collision))
                problems.Add("主角仍压在摆上来的人身上");
            if (_position.X == playerBefore.X && _position.Y == playerBefore.Y)
                problems.Add("主角没有让位");
            if (spot.X != playerBefore.X || spot.Y != playerBefore.Y)
                problems.Add($"权威落位被挪走了（{spot.X:F0},{spot.Y:F0}）");
        }

        Vec2? blocked = null;
        for (var y = 16f; y < _grid.Bounds.Height - 16f && blocked is null; y += 16f)
        {
            for (var x = 16f; x < _grid.Bounds.Width - 16f; x += 16f)
            {
                if (!_grid.CanStandOnTerrain(x, y)) { blocked = new Vec2(x, y); break; }
            }
        }

        if (blocked is not { } wall)
        {
            GD.Print("[placementcheck] 这张图上找不到阻挡格，跳过第二条");
        }
        else
        {
            PlaceStoryActor(new PlaceCommand(Entity("check-placement-b", wall.X, wall.Y)));
            var placedB = _npcs.FirstOrDefault(npc => npc.EntityId == "check-placement-b");
            if (placedB is null) problems.Add("没有摆出第二个人");
            else
            {
                var spot = new Vec2(placedB.Node.Position.X, placedB.Node.Position.Y);
                GD.Print($"[placementcheck] 阻挡格 {wall.X:F0},{wall.Y:F0} 的落点修正到 {spot.X:F0},{spot.Y:F0}");
                if (!_grid.CanStandOnTerrain(spot.X, spot.Y)) problems.Add("修正后的落点仍然站不住");
                if (spot.X == wall.X && spot.Y == wall.Y) problems.Add("压在地形上的落点没有被修正");
            }
        }

        _stage.ShowStageMap(_map.Id, _position, "south");
        var same = new Vec2(_position.X + 96f, _position.Y + 48f);
        _stage.PlaceFromWorld(new WorldPlace("check-stage-a", _map.Id, person.Id, null,
            same.X, same.Y, "south", true, null));
        _stage.PlaceFromWorld(new WorldPlace("check-stage-b", _map.Id, person.Id, null,
            same.X, same.Y, "south", true, null));

        var stageA = _stage.ActorFor(StoryStage.EntityKey("check-stage-a"));
        var stageB = _stage.ActorFor(StoryStage.EntityKey("check-stage-b"));
        if (stageA is null || stageB is null) problems.Add("舞台演员没有建出来");
        else
        {
            var a = new Vec2(stageA.Position.X, stageA.Position.Y);
            var b = new Vec2(stageB.Position.X, stageB.Position.Y);
            GD.Print($"[placementcheck] 舞台 {a.X:F0},{a.Y:F0} 与 {b.X:F0},{b.Y:F0}");
            if (Overlaps(a, b, person.Collision)) problems.Add("舞台上两名演员重叠");
            if (a.X != same.X || a.Y != same.Y) problems.Add("先摆的演员不该动");
            if (b.X == same.X && b.Y == same.Y) problems.Add("后摆的演员没有让开");
        }

        _stage.RemoveByCharacterId(null);
        _stage.Release();

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[placementcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[placementcheck] 通过：主角让位、非法落点修正、舞台后摆者让开");

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0) return;
        GetTree().Quit(0);
    }

    private static bool Overlaps(Vec2 a, Vec2 b, CollisionBox box) => OverlapsBoxes(a, box, b, box);

    private async void BeginBuffCheck()
    {
        var problems = new List<string>();
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var skin = _loader.StatusHud.BuffIcons;
        if (skin.Size <= 0f || skin.Step <= 0f || skin.Catalog.Count == 0)
        {
            GD.Print("[buffcheck] 皮肤里没有可用的 buffIcons 段");
            GetTree().Quit(1);
            return;
        }

        var battle = _battle ??= _loader.Battle;
        var effects = battle.StatusAttackBuffs ?? new Dictionary<string, BattleBuffEffect>();
        var buffs = effects.Keys.Where(key => skin.Catalog.ContainsKey(key)).Take(2).ToList();
        var stateId = skin.Catalog.Keys.FirstOrDefault(key => !effects.ContainsKey(key));
        if (buffs.Count < 2 || stateId is null)
        {
            GD.Print($"[buffcheck] 内容里凑不出两个带图的增益（{buffs.Count}）");
            GetTree().Quit(1);
            return;
        }

        var actor = ControlledId;
        Conditions.Remove(actor);
        _statusHud!.Refresh();
        if (_statusHud.PlayerIconsDebug.Any(icon => icon.Visible)) problems.Add("没有条件时不该有图标");

        Conditions.ApplyBuff(actor, buffs[0], 1, effects[buffs[0]], "check");
        _statusHud.Refresh();
        var icons = _statusHud.PlayerIconsDebug;
        if (icons.Count < 1 || !icons[0].Visible) problems.Add("增益没有画出图标");
        else
        {
            var (x, y) = BuffIconRules.Position(skin.Size, skin.Step, _loader.StatusHud.Player.Height, 0);
            if (!icons[0].Position.IsEqualApprox(new Vector2(x, y)))
                problems.Add($"第一枚图标应在 ({x:F0},{y:F0})，实为 ({icons[0].Position.X:F0},{icons[0].Position.Y:F0})");
            if (icons[0].Texture is null) problems.Add("图标没有贴图");
        }

        Conditions.ApplyBuff(actor, buffs[1], 1, effects[buffs[1]], "check");
        _statusHud.Refresh();
        icons = _statusHud.PlayerIconsDebug;
        if (icons.Count < 2 || !icons[1].Visible) problems.Add("第二个增益没有画出图标");
        else
        {
            var (x, y) = BuffIconRules.Position(skin.Size, skin.Step, _loader.StatusHud.Player.Height, 1);
            if (!icons[1].Position.IsEqualApprox(new Vector2(x, y)))
                problems.Add($"第二枚图标应在 ({x:F0},{y:F0})，实为 ({icons[1].Position.X:F0},{icons[1].Position.Y:F0})");
        }

        // ③ 大面板只收增益：加一个带图的状态，可见图标数不该变。
        Conditions.ApplyState(actor, stateId, 5000);
        _statusHud.Refresh();
        var visible = _statusHud.PlayerIconsDebug.Count(icon => icon.Visible);
        GD.Print($"[buffcheck] 两个增益 + 一个状态：大面板 {visible} 枚（应为 2）");
        if (visible != 2) problems.Add($"状态不该画到大面板上（可见 {visible} 枚）");

        var memberProblem = string.Empty;
        var definitionId = _loader.Companions.Definitions
            .FirstOrDefault(pair => pair.Value.SourceOrDefault == "story").Key;
        if (definitionId is not null)
        {
            try { _party?.JoinStory(definitionId); }
            catch (Exception error) { memberProblem = $"伙伴入队失败：{error.Message}"; }

            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            SyncAllies();
            _statusHud.Refresh();

            var memberId = _party?.ActiveIds.FirstOrDefault(id => id != actor);
            if (memberId is null) memberProblem = "队友没有进队伍";
            else if (_statusHud.MemberIconsDebug(0).All(icon => !icon.Visible))
            {
                Conditions.ApplyState(memberId, stateId, 5000);
                _statusHud.Refresh();
                var memberIcons = _statusHud.MemberIconsDebug(0);
                if (memberIcons.All(icon => !icon.Visible)) memberProblem = "队友面板没有画出状态图标";
                else
                {
                    var (mx, my) = BuffIconRules.Position(skin.Size, skin.Step, _loader.StatusHud.Member.Height, 0);
                    GD.Print($"[buffcheck] 队友面板图标 {memberIcons[0].Position.X:F0},{memberIcons[0].Position.Y:F0}" +
                             $"（应为 {mx:F0},{my:F0}）");
                    if (!memberIcons[0].Position.IsEqualApprox(new Vector2(mx, my)))
                        memberProblem = $"队友面板图标位置不对（{memberIcons[0].Position.X:F0},{memberIcons[0].Position.Y:F0}）";
                }

                Conditions.Remove(memberId);
            }
            else memberProblem = "队友面板在加状态前就有图标";
        }
        else memberProblem = "内容里没有剧情伙伴";

        if (memberProblem.Length > 0) problems.Add(memberProblem);

        Conditions.Remove(actor);
        _statusHud.Refresh();
        if (_statusHud.PlayerIconsDebug.Any(icon => icon.Visible)) problems.Add("清掉条件后图标没有收起");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[buffcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[buffcheck] 通过：增益按序往左排、状态不上大面板、清条件后收起");

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0)
        {
            Conditions.ApplyBuff(actor, buffs[0], 1, effects[buffs[0]], "check");
            Conditions.ApplyBuff(actor, buffs[1], 1, effects[buffs[1]], "check");
            return;
        }

        GetTree().Quit(0);
    }

    private async void BeginCinematicCheck()
    {
        var problems = new List<string>();
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        JsonElement Point(float x, float y) =>
            JsonDocument.Parse($"{{\"type\":\"camera-point\",\"x\":{x},\"y\":{y}}}").RootElement;

        SnapCamera();
        var from = _camera.GlobalPosition;
        _stage.Point(Point(from.X + 240f, from.Y));
        var ms = PanCamera();
        var expected = StoryCamera.DurationMs(240);
        GD.Print($"[cinematiccheck] 240 像素滚动 {ms} 毫秒（公式 {expected}）");
        if (ms != expected) problems.Add($"滚 240 像素应为 {expected} 毫秒，实为 {ms}");

        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var moved = _camera.GlobalPosition.X - from.X;
        GD.Print($"[cinematiccheck] 两帧后镜头走了 {moved:F1} 像素");
        if (moved <= 0.5f) problems.Add("镜头没有开始滚动");
        if (moved >= 239.5f) problems.Add($"镜头是跳过去的（{moved:F1} 像素），不是滚过去");
        if (_cameraPan is null) problems.Add("滚动状态没有建起来");

        _stage.Point(Point(from.X + 400f, from.Y));
        SnapCamera();
        if (MathF.Abs(_camera.GlobalPosition.X - (from.X + 400f)) > 0.5f)
            problems.Add($"cut 没有立刻到位（{_camera.GlobalPosition.X - from.X:F1}）");
        if (_cameraPan is not null) problems.Add("cut 之后还留着滚动");

        var crossMap = EntryStageMap("event-104");
        if (crossMap is null || crossMap == _map.Id) problems.Add($"event-104 的入口舞台不该是当前图（{crossMap}）");
        if (EntryStageMap("event-0") != _map.Id) problems.Add($"event-0 的入口舞台应当是当前图（{EntryStageMap("event-0")}）");

        _fade.Modulate = new Color(1f, 1f, 1f, 0f);
        _initialCurtain = false;
        ShowInitialCurtain("event-104");
        if (!_initialCurtain || _fade.Modulate.A < 0.99f) problems.Add("跨图剧本开场没有压黑幕");

        ClearInitialCurtain();
        if (_fade.Modulate.A > 0.01f) problems.Add("收尾没有把黑幕撤掉");

        ShowInitialCurtain("event-0");
        if (_initialCurtain) problems.Add("同图剧本不该压黑幕");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[cinematiccheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[cinematiccheck] 通过：镜头按距离匀速滚、cut 直落、跨图压黑幕且收尾可撤");
        GetTree().Quit(0);
    }

    private async void BeginPortraitCheck()
    {
        var problems = new List<string>();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        JsonElement Line(string json) => JsonDocument.Parse(json).RootElement;

        var explicitKey = DialoguePortraitPath(Line("""{"type":"dialogue","actor":"kiyan","portrait":"saint"}"""), "kiyan");
        var saintPath = _loader.Interface.Portraits.GetValueOrDefault("saint");
        if (explicitKey != saintPath) problems.Add($"行内 portrait 没生效（{explicitKey}）");

        // ② 行里写成 null：这一句就是不要立绘，不能回退到人物定义。
        var nullKey = DialoguePortraitPath(Line("""{"type":"dialogue","actor":"kiyan","portrait":null}"""), "kiyan");
        if (nullKey is not null) problems.Add($"portrait 为 null 时不该回退（{nullKey}）");

        var fallback = DialoguePortraitPath(Line("""{"type":"dialogue","actor":"kiyan"}"""), "kiyan");
        var kiyanKey = _loader.World.Actors.TryGetValue("kiyan", out var kiyan) ? kiyan.Portrait : null;
        var kiyanPath = kiyanKey is null ? null : _loader.Interface.Portraits.GetValueOrDefault(kiyanKey);
        if (fallback != kiyanPath) problems.Add($"没写 portrait 时该用人物定义（{fallback}）");

        var viewportWidth = GetViewportRect().Size.X;
        if (kiyanPath is null) problems.Add("主角没有立绘路径");
        else
        {
            _dialogue.DebugPortrait(kiyanPath, true);
            var rect = _dialogue.PortraitBounds;
            GD.Print($"[portraitcheck] 主角立绘 {rect.Position.X:F0},{rect.Position.Y:F0} {rect.Size.X:F0}×{rect.Size.Y:F0}");
            if (!_dialogue.PortraitVisible) problems.Add("主角立绘没画出来");
            if (MathF.Abs(rect.Position.X - 56f) > 0.5f) problems.Add($"主角立绘该左对齐到 56，实为 {rect.Position.X:F0}");
            if (MathF.Abs(rect.End.Y - 606f) > 0.5f) problems.Add($"立绘底边该是 606，实为 {rect.End.Y:F0}");
            if (MathF.Abs(rect.Size.Y - 516f) > 0.5f) problems.Add($"立绘高该是 516，实为 {rect.Size.Y:F0}");
        }

        var otherPath = _loader.Interface.Portraits.GetValueOrDefault("darius");
        if (otherPath is null) problems.Add("内容里没有 darius 立绘");
        else
        {
            _dialogue.DebugPortrait(otherPath, false);
            var rect = _dialogue.PortraitBounds;
            GD.Print($"[portraitcheck] 他人立绘 {rect.Position.X:F0},{rect.Position.Y:F0} {rect.Size.X:F0}×{rect.Size.Y:F0}");
            if (!_dialogue.PortraitVisible) problems.Add("他人立绘没画出来");
            if (MathF.Abs(rect.End.X - (viewportWidth - 56f)) > 0.5f)
                problems.Add($"他人立绘该右对齐到 {viewportWidth - 56f:F0}，实为 {rect.End.X:F0}");
            if (MathF.Abs(rect.End.Y - 606f) > 0.5f) problems.Add($"他人立绘底边该是 606，实为 {rect.End.Y:F0}");
        }

        _dialogue.DebugPortrait("assets/ui/story/portrait-does-not-exist.png", true);
        if (_dialogue.PortraitVisible) problems.Add("缺图时立绘该收起");

        if (problems.Count > 0)
        {
            foreach (var problem in problems) GD.Print($"[portraitcheck] 问题：{problem}");
            GetTree().Quit(1);
            return;
        }

        GD.Print("[portraitcheck] 通过：取图三种情况正确，主人左侧／他人右侧、底边 606、高 516，缺图收起");
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--shot") >= 0) return;
        GetTree().Quit(0);
    }

    private string? DialoguePortraitPath(JsonElement item, string actor)
    {
        string? key;
        if (item.TryGetProperty("portrait", out var portrait))
            key = portrait.ValueKind == JsonValueKind.Null ? null : StoryType.Text(item, "portrait");
        else
            key = _loader.World.Actors.TryGetValue(actor, out var definition) ? definition.Portrait : null;

        return key is not null && _loader.Interface.Portraits.TryGetValue(key, out var path) ? path : null;
    }

    private async Task PlayPresentation(JsonElement item)
    {
        // 原版 `perform()` 每执行一项动作前先 `ui.closeDialogue()`：台词翻过去就收框，
        // 后面的走位、转身、镜头移动不该还压着上一句台词。之前只在整段剧本收尾时
        // 才关框，于是「那么，我就到野营地……」之后卓拉德走掉的几秒里对话框一直挂着，
        // 看上去就像卡死在这一句。对白/选项/任务弹窗自己占着框，不在此列。
        switch (StoryType.Of(item))
        {
            case "dialogue":
            case "choice":
            case "quest-popup":
                break;
            default:
                _dialogue.Close();
                break;
        }

        switch (StoryType.Of(item))
        {
            case "dialogue":
                if (_story.Skipping) return;
                var speakerActor = StoryType.Text(item, "actor");
                await _dialogue.ShowAsync(
                    StoryType.Text(item, "speaker"),
                    StoryType.Text(item, "text"),
                    StoryType.Text(item, "presentation") == "screen",
                    DialoguePortraitPath(item, speakerActor),
                    speakerActor.Length > 0 && speakerActor == _loader.Campaign.Start.Actor);
                return;

            case "instant-message":
            {
                if (_story.Skipping) return;
                var notice = StoryType.Text(item, "text");
                if (!_autoStory && _hudNoticeRemainingMs > 0)
                    await StoryClock.WaitAsync(this, _hudNoticeRemainingMs / 1000.0,
                        _storySkip?.Token ?? default);
                ShowHudNotice(notice);
                return;
            }

            case "wait":
                if (!_autoStory)
                    await StoryClock.WaitAsync(this, StoryType.Int(item, "durationMs") / 1000.0,
                        _storySkip?.Token ?? default);
                return;

            case "fade":
                await PlayFadeAsync(item);
                return;

            case "choice":
            {
                var options = new List<(string Label, string Value)>();
                if (item.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var option in list.EnumerateArray())
                        options.Add((StoryType.Text(option, "label"), StoryType.Text(option, "value")));

                _story.BeginChoice();
                string picked;
                try
                {
                    picked = await _choice.AskAsync(StoryType.Text(item, "prompt"), options,
                        StoryType.Text(item, "description"));
                }
                finally
                {
                    _story.EndChoice();
                }

                _story.Choose(picked);
                return;
            }

            case "stage":
                PlayStage(item);
                return;

            case "appear":
                _stage.Appear(item);
                return;

            case "face":
                _stage.Face(item);
                return;

            case "pose":
                await _stage.PoseAsync(item, _storySkip?.Token ?? default);
                return;

            case "show":
                _stage.Show(item);
                return;

            case "walk":
                await _stage.WalkAsync(item, _storySkip?.Token ?? default);
                return;

            case "parallel":
                await _stage.ParallelAsync(item, PlayPresentation);
                return;

            case "leave":
                _stage.Leave(item);
                return;

            case "camera":
                _stage.Focus(item);
                if (StoryType.Text(item, "motion") == "cut") SnapCamera();
                else await WaitCameraAsync(PanCamera());
                return;

            case "camera-point":
                _stage.Point(item);
                if (StoryType.Text(item, "motion") == "cut") SnapCamera();
                else await WaitCameraAsync(PanCamera());
                return;

            case "quest-popup":
            {
                var questId = StoryType.Text(item, "quest");
                var quest = _loader.Campaign.FindQuest(questId);

                if (quest is null)
                {
                    _unsupportedStoryTypes.Add($"quest-popup({questId})");
                    return;
                }

                _story.BeginConfirm();
                try
                {
                    // -director 只给完成弹窗带回执（`kind==='complete'` 时查会话的
                    // 奖励账，取该任务最近一轮）；接取弹窗没有回执，正文就是任务描述。
                    // 原版在完成弹窗取不到回执时直接抛错「任务尚未结算」——这里只记一条警告：
                    // 内容写错不该把整段演出打断，弹窗照常出，只是没有奖励行。
                    var kind = StoryType.Text(item, "kind", "start");
                    var receipt = kind == "complete" ? _journal.RewardReceiptFor(questId) : null;
                    if (kind == "complete" && receipt is null)
                        GD.PushWarning($"[story] 完成弹窗取不到结算回执：{questId}（任务尚未结算）");
                    await _questPopup.ShowAsync(quest, kind, receipt);
                }
                finally
                {
                    _story.EndConfirm();
                }

                return;
            }

            case "sound":
                _audio?.Effect(StoryType.Text(item, "sound"));
                return;

            case "stop-audio":
                _audio?.StopStoryAudio();
                return;

            case "music":
            {
                var track = item.TryGetProperty("sound", out var sound) &&
                            sound.ValueKind == JsonValueKind.String
                    ? sound.GetString()
                    : null;
                try
                {
                    _audio?.SetStoryMusic(track);
                }
                catch (InvalidOperationException error)
                {
                    _unsupportedStoryTypes.Add($"music({track})");
                    GD.PushWarning($"[story] {error.Message}");
                }

                return;
            }

            case "map-effect":
                _stage.SetMapEffect(StoryType.Bool(item, "enabled"));
                return;

            case "effect":
                _stage.Effect(item);
                return;

            case "location-effect":
                _stage.LocationEffect(item);
                return;

            case "world-object":
            {
                if (!item.TryGetProperty("command", out var command))
                {
                    _unsupportedStoryTypes.Add("world-object（缺 command）");
                    return;
                }

                var target = StoryType.Text(item, "target");
                _stage.ApplyWorldObject(command, target);
                return;
            }

            case "refresh-appearance":
                _stage.RefreshAppearance();
                return;

            default:
                _unsupportedStoryTypes.Add(StoryType.Of(item));
                return;
        }
    }

    private async Task PlayFadeAsync(JsonElement item)
    {
        var opacity = StoryType.Float(item, "opacity", 1f);
        var duration = StoryType.Int(item, "durationMs") / 1000.0f;
        var tint = StoryType.Int(item, "color") == 0xFFFFFF ? new Color(1f, 1f, 1f) : new Color(0f, 0f, 0f);

        _initialCurtain = false;
        _fadeTween?.Kill();
        _fadeTween = null;
        _fade.Color = new Color(tint.R, tint.G, tint.B, 1f);

        if (duration <= 0f || _autoStory)
        {
            _fade.Modulate = new Color(1f, 1f, 1f, opacity);
            return;
        }

        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(_fade, "modulate:a", opacity, duration);

        await StoryClock.WaitAsync(this, duration, _storySkip?.Token ?? default);
    }

    private void PlayStage(JsonElement item)
    {
        var declared = StoryType.Text(item, "map");
        var mapId = StoryType.Bool(item, "mapFromWorld") && _map is not null ? _map.Id : declared;
        if (mapId.Length > 0 && mapId != _stage.StageMapId)
        {
            var x = StoryType.Float(item, "x", _position.X);
            var y = StoryType.Float(item, "y", _position.Y);
            _stage.ShowStageMap(mapId, new Vec2(x, y), StoryType.Text(item, "facing", "south"));
        }

        if (mapId.Length > 0) _story.StageMap = mapId;

        _stage.RetainPlayerPosition = StoryType.Bool(item, "retainPlayerPosition");
        _stage.SetCast(item);

        HideWorld();
        UpdateCameraLimits();
    }

    private void CommitStage(int worldMark, string? eventId)
    {
        if (!_stage.Active)
        {
            _stage.ReleaseCamera();
            // stage 演出项会先 HideWorld 再画舞台；舞台没建起来时这里必须把世界放回来。
            RevealWorld();
            return;
        }

        var (snapshot, changes) = _stage.CommitSnapshot();
        var (mapId, position, facing) = _stage.Release();
        RevealWorld();

        var traveled = false;
        if (snapshot is not null)
        {
            var logged = _story.WorldLog.Skip(worldMark).ToList();
            var result = StoryCommit.Commands(
                logged, _session.StoryWorld, changes, snapshot,
                _waitingEncounter is not null ? "waiting" : "completed", eventId,
                memberId => _session.Party?.Get(memberId) is not null);

            void ApplyCommands()
            {
                foreach (var command in result.Incremental)
                {
                    // travel 只写剧情世界的落点；真正的切图交给下面的 LoadMap，
                    // 否则同一次收尾会切两遍图（第二次还会把刚摆好的人重建成投影）。
                    if (command is TravelCommand)
                    {
                        _session.StoryWorld.Apply(command);
                        traveled = true;
                        continue;
                    }

                    WorldSink.Apply(command);
                }

                if (result.CompletionFlag is not null) _journal.SetFlag(result.CompletionFlag, true);
            }

            // 原版节点提交前取基线、失败整批撤回（story-runtime 的 rollback）。
            // 只在真有命令要落地时才取：多数舞台收尾没有命令，不该为此每帧深拷会话。
            if (logged.Count == 0)
            {
                ApplyCommands();
            }
            else
            {
                var baseline = _session.CaptureState();
                try
                {
                    ApplyCommands();
                }
                catch (Exception error)
                {
                    _session.RestoreState(baseline);
                    GD.PushWarning($"[story] 节点提交失败，已整批撤回：{error.Message}");
                    LoadMap(_map.Id, ControlPosition, facing);
                    return;
                }
            }
        }

        if (mapId.Length > 0 && traveled) LoadMap(mapId, position, facing);
    }

    private void HideWorld()
    {
        _mapRoot.Visible = false;
        _player.Visible = false;
        _outcomes?.SetVisible(false);
    }

    private void RevealWorld()
    {
        if (_mapRoot is not null) _mapRoot.Visible = true;
        if (_player is not null) _player.Visible = true;
        _outcomes?.SetVisible(true);
    }

    /// <summary>
    /// 每物理帧检查一次剧情触发器。不能降频：region 触发区可能只有 16 像素宽，
    /// 玩家 133 像素／秒走过去只要 0.12 秒，轮询会整段漏掉。
    /// 593 条事件多数被「已触发」直接跳过，每帧全量检查的开销可以忽略。
    /// 顺序即内容顺序，先看地点再看条件，谁先满足谁先演。
    /// </summary>
    private void UpdateTriggers()
    {
        // 演出进行中不再起新剧本：两段演出并行会各自从同一个导演里取步，
        // 先结束的那条会提前收尾提交，后一条就演在已释放的舞台上。
        // 演出期间的触发由 _Process 里的 CollectDeferredTriggers 收进待办，收尾后补演。
        if (_storyPlaying || _storyResumeQueued || _waitingEncounter is not null) return;

        if (_deferredTriggers.Count > 0 && DrainDeferredTriggers()) return;

        foreach (var trigger in _loader.Campaign.EventTable.Triggers)
        {
            if (!trigger.Repeat &&
                (_journal.EventStarted(trigger.Id) || _journal.EventDone(trigger.Id))) continue;

            var place = TriggerPlaceMatches(trigger);

            if (trigger.Source is "region" or "near")
            {
                if (!place)
                {
                    _triggerOccupancy.Remove(trigger.Id);
                    continue;
                }

                if (!_triggerOccupancy.Add(trigger.Id)) continue;
                // 占用只防「每帧重复上报」，资格（任务进行中之类）还是要过的：
                // 占着但条件不成立的事件不能起演。
                if (trigger.Condition.ValueKind == JsonValueKind.Object && !_story.Evaluate(trigger.Condition)) continue;
            }
            else if (trigger.Source is "quest" or "choice" or "event")
            {
                if (trigger.Condition.ValueKind != JsonValueKind.Object)
                {
                    if (!place) continue;
                }
                else if (!_story.Evaluate(trigger.Condition))
                {
                    _triggerConditionHeld.Remove(trigger.Id);
                    continue;
                }
                else if (!_triggerConditionHeld.Add(trigger.Id))
                {
                    continue;
                }
            }
            else if (!place)
            {
                continue;
            }

            _journal.MarkEventStarted(trigger.Id);
            _debugTriggerFires += 1;
            if (trigger.Source == "battle" && trigger.Key.StartsWith("defeat:", StringComparison.Ordinal))
                _pendingDefeats.Remove(trigger.Key["defeat:".Length..]);
            GD.Print($"[trigger] {trigger.Id}（{trigger.Source}）→ {trigger.Script}");
            PlayStory(trigger.Script, trigger.Id);
            return;
        }
    }

    private void CollectDeferredTriggers()
    {
        if ((_deferScanFrames++ & 7) != 0) return;

        foreach (var trigger in _loader.Campaign.EventTable.Triggers)
        {
            if (trigger.Source is not ("region" or "near" or "quest" or "choice" or "event")) continue;
            if (trigger.Id == _storyEventId) continue;
            if (_triggerOccupancy.Contains(trigger.Id) || _triggerConditionHeld.Contains(trigger.Id)) continue;
            if (!trigger.Repeat && (_journal.EventStarted(trigger.Id) || _journal.EventDone(trigger.Id))) continue;
            if (trigger.Condition.ValueKind == JsonValueKind.Object && !_story.Evaluate(trigger.Condition)) continue;
            if (!TriggerPlaceMatches(trigger)) continue;
            _deferredTriggers.Add(trigger.Id);
        }
    }

    private bool DrainDeferredTriggers()
    {
        foreach (var id in _deferredTriggers.ToList())
        {
            _deferredTriggers.Remove(id);
            var trigger = _loader.Campaign.EventTable.Triggers.FirstOrDefault(value => value.Id == id);
            if (trigger is null || trigger.Id == _storyEventId) continue;
            if (_triggerOccupancy.Contains(id) || _triggerConditionHeld.Contains(id)) continue;
            if (!trigger.Repeat && (_journal.EventStarted(id) || _journal.EventDone(id))) continue;
            if (trigger.Condition.ValueKind == JsonValueKind.Object && !_story.Evaluate(trigger.Condition)) continue;
            if (!TriggerPlaceMatches(trigger)) continue;

            // 与正常路径一样记账：region/near 占住、条件类记边沿，否则下一帧还会再派一次。
            if (trigger.Source is "region" or "near") _triggerOccupancy.Add(id);
            else _triggerConditionHeld.Add(id);

            _journal.MarkEventStarted(id);
            _debugTriggerFires += 1;
            GD.Print($"[trigger] {id}（{trigger.Source}，演出期间排队）→ {trigger.Script}");
            PlayStory(trigger.Script, id);
            return true;
        }

        return false;
    }

    private bool TriggerPlaceMatches(StoryEventTrigger trigger)
    {
        switch (trigger.Source)
        {
            case "start":
                return false;

            case "region":
            {
                if (trigger.Map.Length > 0 && trigger.Map != _map.Id) return false;
                if (trigger.Region is not { } region) return false;

                // 与出口同口径：16×16 脚底方框与区域相交就算踩进去，
                // 否则贴着区域边缘走会漏触发。
                var foot = ControlPosition;
                return foot.X + FootRadius > region.X && foot.X - FootRadius < region.X + region.Width &&
                       foot.Y + FootRadius > region.Y && foot.Y - FootRadius < region.Y + region.Height;
            }

            case "map-entry":
                return trigger.Map.Length == 0 || trigger.Map == _map.Id;

            case "near":
            {
                foreach (var npc in _npcs)
                {
                    if (trigger.Near.Length == 0 || npc.ActorId != trigger.Near) continue;
                    if (trigger.NearCharacterId is { Length: > 0 } wanted && npc.CharacterId != wanted) continue;
                    if (npc.Node is null || !IsInstanceValid(npc.Node) || !npc.Node.Visible) continue;

                    var target = new Vec2(npc.Node.Position.X, npc.Node.Position.Y);
                    if (ChestReach.NearInteractive(ControlPosition, target, blocking: true)) return true;
                }

                return false;
            }

            case "battle":
            {
                const string defeatPrefix = "defeat:";
                if (!trigger.Key.StartsWith(defeatPrefix, StringComparison.Ordinal)) return false;
                if (trigger.Map.Length > 0 && trigger.Map != _map.Id) return false;
                return _pendingDefeats.Contains(trigger.Key[defeatPrefix.Length..]);
            }

            default:
            {
                if (trigger.Spots.Count > 0)
                {
                    foreach (var spot in trigger.Spots)
                        if (spot.Map.Length == 0 || spot.Map == _map.Id)
                            return true;

                    return false;
                }

                return true;
            }
        }
    }

    private void SaveShot()
    {
        var directory = ProjectSettings.GlobalizePath("res://.shots");
        DirAccess.MakeDirRecursiveAbsolute(directory);

        var path = System.IO.Path.Combine(directory, "world.png");
        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("[shot] 无头模式下拿不到视口画面，跳过截图");
            GetTree().Quit();
            return;
        }

        var image = GetViewport().GetTexture().GetImage();

        var arguments = OS.GetCmdlineUserArgs();
        var cropIndex = Array.IndexOf(arguments, "--shotrect");
        var parts = cropIndex >= 0 && cropIndex + 1 < arguments.Length
            ? arguments[cropIndex + 1].Split(',')
            : [];
        if (parts.Length == 4 &&
            int.TryParse(parts[0], out var cx) && int.TryParse(parts[1], out var cy) &&
            int.TryParse(parts[2], out var cw) && int.TryParse(parts[3], out var ch))
        {
            var region = new Rect2I(cx, cy, cw, ch).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
            path = System.IO.Path.Combine(directory, "crop.png");
            image.GetRegion(region).SavePng(path);
        }
        else image.SavePng(path);
        GD.Print($"[shot] {path} fps={Engine.GetFramesPerSecond()}");
        GetTree().Quit();
    }
}

public sealed class SceneWorldCommands(Inotia4.Scenes.WorldScene scene) : IWorldCommandSink
{
    public void Apply(StoryWorldCommand command) => scene.ApplyStoryCommand(command);

    public void DropItem(string map, float x, float y, string item, int quantity)
    {
        for (var index = 0; index < quantity; index += 1)
            scene.SpawnStoryDrop(map, x, y, item);
    }
}
