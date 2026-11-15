using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Content;
using Inotia4.Domain.Combat;
using Inotia4.Rendering;
using Inotia4.Domain.Story;
using Inotia4.Domain.World;

namespace Inotia4.Scenes;

public partial class StoryStage : Node2D
{
    private ContentLoader _loader = null!;
    private TextureCache _textures = null!;
    private ActorDefinition _hero = null!;

    private Node2D? _mapRoot;

    private Node2D? _actorRoot;

    private MapDefinition? _stageMap;
    private CollisionGrid? _grid;
    private TextureLease? _mapLease;

    private readonly Dictionary<string, ActorNode> _actors = [];
    private readonly HashSet<string> _missing = [];

    private readonly Dictionary<ActorNode, string> _characterIds = [];

    private readonly Dictionary<string, WorldPlace> _placed = [];

    public string? StageMapId { get; private set; }

    public MapDefinition? StageMap => _stageMap;

    public (Vec2 Position, string Facing) PlayerExit { get; private set; }

    public bool RetainPlayerPosition { get; set; }

    private readonly List<StoryCommitCastMember> _cast = [];

    public void SetCast(JsonElement item)
    {
        _cast.Clear();
        _castMembers.Clear();
        if (!item.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array) return;

        foreach (var member in cast.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.Object) continue;
            var memberId = StoryType.Text(member, "memberId");
            var characterId = StoryType.Text(member, "characterId");
            _cast.Add(new StoryCommitCastMember(
                memberId.Length == 0 ? null : memberId,
                characterId.Length == 0 ? null : characterId));

            var alias = StoryType.Text(member, "id");
            if (alias.Length > 0 && memberId.Length > 0) _castMembers[alias] = memberId;
        }
    }

    private readonly Dictionary<string, string> _castMembers = [];

    private readonly Dictionary<string, string> _aliases = [];

    public static string EntityKey(string entityId) => $"entity:{entityId}";

    public (StoryCommitStage? Stage, List<StoryEntityState> Changes) CommitSnapshot()
    {
        if (!Active) return (null, []);

        var actors = new List<StoryCommitActor>();
        var changes = new List<StoryEntityState>();
        foreach (var (entityId, place) in _placed)
        {
            if (place.Map != StageMapId) continue;
            if (!_actors.TryGetValue(EntityKey(entityId), out var actor) ||
                !GodotObject.IsInstanceValid(actor)) continue;

            actors.Add(new StoryCommitActor(
                entityId, entityId, actor.Position.X, actor.Position.Y, actor.Facing, actor.Visible));
            changes.Add(new StoryEntityState
            {
                Id = entityId,
                Map = place.Map,
                Actor = place.Actor,
                CharacterId = place.CharacterId,
                X = actor.Position.X,
                Y = actor.Position.Y,
                Facing = actor.Facing,
                Visible = actor.Visible,
            });
        }

        return (new StoryCommitStage(
            StageMapId!, PlayerExit.Position.X, PlayerExit.Position.Y, PlayerExit.Facing,
            RetainPlayerPosition, actors, _cast.ToArray()), changes);
    }

    public IReadOnlyCollection<string> MissingTargets => _missing;

    public IReadOnlyDictionary<string, ActorNode> ActorsSnapshot() => _actors;

    /// <summary>无头/自动模式下不走位也不等待，直接落位——否则一条主线要跑几分钟。</summary>
    public bool Instant { get; set; }

    public Node2D? CameraFocus { get; private set; }

    public Vec2? CameraPoint { get; private set; }

    public void Setup(ContentLoader loader, TextureCache textures, ActorDefinition hero)
    {
        _loader = loader;
        _textures = textures;
        _hero = hero;
    }

    public void SetHeroAppearance(ActorDefinition hero)
    {
        if (ReferenceEquals(_hero, hero)) return;
        _hero = hero;

        if (_playerActor is not { } actor || !GodotObject.IsInstanceValid(actor)) return;
        var position = actor.Position;
        var facing = actor.Facing;
        var visible = actor.Visible;
        var modulate = actor.Modulate;

        _actorRoot?.RemoveChild(actor);
        actor.Free();
        _actors.Remove("player");
        _playerActor = null;

        var rebuilt = EnsurePlayerActor();
        rebuilt.TeleportTo(position);
        rebuilt.SetFacing(facing);
        rebuilt.Visible = visible;
        rebuilt.Modulate = modulate;
    }

    public void ShowStageMap(string mapId, Vec2 playerPosition, string facing)
    {
        ReleaseMap();

        PlayerExit = (playerPosition, facing);

        _mapLease = _textures.Lease();
        _mapRoot = new Node2D { Name = "StageMap", ZIndex = 100 };
        AddChild(_mapRoot);

        _stageMap = _loader.LoadMap(mapId);
        _grid = new CollisionGrid(_stageMap);

        _actorRoot = MapLayerRenderer.Build(_stageMap, _loader.PropFiles(), _mapLease, _mapRoot);

        StageMapId = mapId;
        CameraFocus = EnsurePlayerActor();

        foreach (var place in _placed.Values.Where(p => p.Map == mapId))
            PlaceFromWorld(place);
    }

    public bool Active => StageMapId is not null;

    public (string MapId, Vec2 Position, string Facing) Release()
    {
        var result = (StageMapId ?? string.Empty, PlayerExit.Position, PlayerExit.Facing);
        ReleaseMap();
        MapEffectTone.Reset();
        Material = null;
        return result;
    }

    /// <summary>
    /// 地图效果开关。原版这一档改的是地图的基础色调，打开后世界层每帧叠一次
    /// <c>GRP_AddColorTone</c>（该符号见 <c>inotia4/data/sym_by_module.txt</c>）。这里用舞台根节点的材质
    /// 表达同一件事：整棵舞台（地图三层 + 演员）一起被重新着色，与「把世界层包进去」等价。
    /// </summary>
    public void SetMapEffect(bool enabled)
    {
        if (!MapEffectTone.SetEnabled(enabled)) return;

        if (!enabled)
        {
            Material = null;
            return;
        }

        _toneMaterial ??= MapEffectTone.CreateMaterial();
        Material = _toneMaterial;
    }

    private ShaderMaterial? _toneMaterial;

    private void ReleaseMap()
    {
        foreach (var actor in _actors.Values.ToList())
        {
            if (!GodotObject.IsInstanceValid(actor)) continue;
            actor.GetParent()?.RemoveChild(actor);
            actor.Free();
        }

        _actors.Clear();
        _characterIds.Clear();
        CameraFocus = null;
        CameraPoint = null;
        _actorRoot = null;
        _playerActor = null;

        if (_mapRoot is not null)
        {
            RemoveChild(_mapRoot);
            _mapRoot.Free();
            _mapRoot = null;
        }

        _mapLease?.Dispose();
        _mapLease = null;
        _stageMap = null;
        _grid = null;
        StageMapId = null;
    }

    private ActorNode? _playerActor;

    private ActorNode EnsurePlayerActor()
    {
        if (_playerActor is not null && GodotObject.IsInstanceValid(_playerActor)) return _playerActor;

        var actor = new ActorNode { Name = "player" };
        _actorRoot!.AddChild(actor);
        actor.Setup(_hero, _mapLease!, PlayerExit.Facing);
        actor.TeleportTo(new Vector2(PlayerExit.Position.X, PlayerExit.Position.Y));
        _actors["player"] = actor;
        _playerActor = actor;
        return actor;
    }

    private ActorNode? Resolve(string target)
    {
        if (target.Length == 0 || _mapRoot is null) return null;
        if (target == "player") return EnsurePlayerActor();

        if (_aliases.TryGetValue(target, out var bound))
        {
            if (_actors.TryGetValue(bound, out var boundActor) && GodotObject.IsInstanceValid(boundActor))
                return boundActor;

            GD.Print($"[stage] 别名「{target}」指向的物件不在本幕舞台上");
            _missing.Add(target);
            return null;
        }

        if (_actors.TryGetValue(target, out var existing) && GodotObject.IsInstanceValid(existing))
            return existing;

        var definition = DefinitionFor(target);
        if (definition is null)
        {
            GD.Print($"[stage] 找不到演员「{target}」，登场/走位会跳过它");
            _missing.Add(target);
            return null;
        }

        var actor = new ActorNode { Name = target };
        _actorRoot!.AddChild(actor);
        actor.Setup(definition, _mapLease!);

        actor.Visible = false;
        actor.Modulate = new Color(1f, 1f, 1f, 0f);
        _actors[target] = actor;

        return actor;
    }

    public ActorNode? ActorFor(string instanceKey) =>
        _actors.TryGetValue(instanceKey, out var actor) && GodotObject.IsInstanceValid(actor) ? actor : null;

    public ActorNode? ActorForAlias(string alias) =>
        _aliases.TryGetValue(alias, out var key) ? ActorFor(key) : null;

    /// <summary>
    /// 把一个剧情别名绑到某个舞台演员实例：
    /// 之后按这个别名的 <c>face</c>／<c>pose</c> 操作的都是这个实例。
    ///
    /// 一名演员只能被一个别名占用。两个别名指向同一实例时，后者的走位／显隐会作用到
    /// 前者的演员身上。这里不让绑定生效并留一条日志——内容是死的，
    /// 让整幕停在一个错字上不值得，但错必须能被看见。
    /// </summary>
    public void BindAlias(string alias, string instanceKey)
    {
        if (alias.Length == 0 || instanceKey.Length == 0) return;

        foreach (var (other, key) in _aliases)
        {
            if (other == alias || key != instanceKey) continue;
            GD.PushWarning($"[stage] 剧情演员重复绑定：{alias} 与 {other} 指向 {instanceKey}");
            return;
        }

        _aliases[alias] = instanceKey;
    }

    public void ApplyWorldObject(JsonElement command, string? target)
    {
        var body = command.TryGetProperty("command", out var nested) ? nested : command;
        var kind = StoryType.Text(body, "kind");

        switch (kind)
        {
            case "place":
            {
                if (!body.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object)
                {
                    GD.Print("[stage] world-object place 缺 entity");
                    return;
                }

                var entityId = StoryType.Text(entity, "id");
                var actor = StoryType.Text(entity, "actor");
                var map = StoryType.Text(entity, "map");
                var characterId = StoryType.Text(entity, "characterId");

                if (StoryType.Bool(body, "mapFromStage")) map = StageMapId ?? map;

                var place = new WorldPlace(
                    entityId, map, actor, characterId.Length == 0 ? null : characterId,
                    StoryType.Float(entity, "x"), StoryType.Float(entity, "y"),
                    entity.TryGetProperty("facing", out _) ? StoryType.Text(entity, "facing") : null,
                    !entity.TryGetProperty("visible", out _) || StoryType.Bool(entity, "visible"),
                    StoryType.Text(body, "scope") == "visit" ? "visit" : null);

                PlaceFromWorld(place);

                if (target is { Length: > 0 }) BindAlias(target, EntityKey(entityId));
                return;
            }

            case "remove-character":
            {
                var characterId = StoryType.Text(body, "characterId");
                var map = StoryType.Text(body, "map");
                if (StoryType.Bool(body, "mapFromStage")) map = StageMapId ?? map;
                var all = StoryType.Bool(body, "all");

                var matched = 0;
                foreach (var actor in _actors.Values.ToList())
                {
                    if (!GodotObject.IsInstanceValid(actor)) continue;
                    if (_characterIds.GetValueOrDefault(actor) != characterId) continue;
                    if (map.Length > 0 && StageMapId != map) continue;
                    if (!all && matched > 0) break;

                    actor.Visible = false;
                    actor.Modulate = new Color(1f, 1f, 1f, 0f);
                    matched++;
                }
                return;
            }

            default:
                GD.Print($"[stage] world-object 的 {kind} 还没接");
                return;
        }
    }

    /// <summary>
    /// 某个角色当前该用哪套外观。对应 <see cref="StoryWorld.ActorAppearance"/>：
    /// <c>actor-appearance</c> 命令改过就用新的，否则就是它自己。
    /// </summary>
    public Func<string, string>? AppearanceSource { get; set; }

    private string AppearanceOf(string actor) => AppearanceSource?.Invoke(actor) ?? actor;

    private ActorDefinition? DefinitionFor(string target)
    {
        if (target == "player") return _hero;

        if (_castMembers.TryGetValue(target, out var memberId) &&
            MemberAppearanceSource?.Invoke(memberId) is { } memberAppearance)
            return memberAppearance;

        var appearance = AppearanceOf(target);
        return _loader.World.Actors.GetValueOrDefault(appearance);
    }

    public Func<string, ActorDefinition?>? MemberAppearanceSource { get; set; }

    public void RefreshAppearance()
    {
        foreach (var (id, actor) in _actors.ToList())
        {
            if (!GodotObject.IsInstanceValid(actor)) continue;

            var definition = DefinitionFor(id);
            if (ReferenceEquals(definition, actor.Definition)) continue;

            var position = actor.Position;
            var facing = actor.Facing;
            var visible = actor.Visible;
            var characterId = _characterIds.GetValueOrDefault(actor);

            _actorRoot!.RemoveChild(actor);
            actor.Free();
            _actors.Remove(id);
            _characterIds.Remove(actor);

            var rebuilt = Resolve(id);
            if (rebuilt is null) continue;
            rebuilt.TeleportTo(position);
            rebuilt.SetFacing(facing);
            if (characterId is not null) _characterIds[rebuilt] = characterId;

            rebuilt.Visible = visible;
            rebuilt.Modulate = new Color(1f, 1f, 1f, visible ? 1f : 0f);
        }
    }

    /// <summary>
    /// world:place：登记 + 若舞台正好在这张图上就立即摆。
    /// 剧本的 place 都在 stage 演出项**之前**执行（进节点时），所以先登记、
    /// 舞台画出来那一刻再统一落位——先写世界状态、再由舞台投影。
    /// </summary>
    public void PlaceFromWorld(in WorldPlace place)
    {
        _placed[place.EntityId] = place;
        if (StageMapId != place.Map) return;

        var key = EntityKey(place.EntityId);
        if (!_actors.TryGetValue(key, out var actor) || !GodotObject.IsInstanceValid(actor))
        {
            var definition = DefinitionFor(place.Actor);
            if (definition is null) return;

            actor = new ActorNode { Name = key };
            _actorRoot!.AddChild(actor);
            actor.Setup(definition, _mapLease!);
            actor.Visible = false;
            actor.Modulate = new Color(1f, 1f, 1f, 0f);
            _actors[key] = actor;
        }

        if (place.CharacterId is not null) _characterIds[actor] = place.CharacterId;

        var spot = Settle(new Vec2(place.X, place.Y), actor);
        actor.TeleportTo(new Vector2(spot.X, spot.Y));
        if (place.Facing is not null) actor.SetFacing(place.Facing);
        actor.Visible = place.Visible;
        actor.Modulate = new Color(1f, 1f, 1f, place.Visible ? 1f : 0f);
    }

    public void RemoveByCharacterId(string? characterId)
    {
        if (characterId is null) return;

        foreach (var (entityId, place) in _placed.Where(pair => pair.Value.CharacterId == characterId).ToList())
            _placed.Remove(entityId);

        foreach (var (actor, id) in _characterIds.Where(pair => pair.Value == characterId).ToList())
        {
            if (!GodotObject.IsInstanceValid(actor)) continue;

            if (ReferenceEquals(CameraFocus, actor)) CameraFocus = null;

            // 不能只按 actor.Name 删：AddChild 会给重名节点自动改名，键和 Name 可能已经不同，
            // 漏删的条目在 ReleaseMap 里就会变成已释放对象。
            foreach (var key in _actors.Where(pair => ReferenceEquals(pair.Value, actor))
                         .Select(pair => pair.Key).ToList())
                _actors.Remove(key);
            _characterIds.Remove(actor);
            if (actor == _playerActor) _playerActor = null;

            actor.GetParent()?.RemoveChild(actor);
            actor.Free();
        }
    }

    public void SetVisibilityByActor(string actorId, string? characterId, bool visible)
    {
        if (!_actors.TryGetValue(actorId, out var actor) || !GodotObject.IsInstanceValid(actor)) return;
        if (characterId is not null &&
            _characterIds.TryGetValue(actor, out var own) && own != characterId) return;

        actor.Visible = visible;
        actor.Modulate = new Color(1f, 1f, 1f, visible ? 1f : 0f);
    }

    public void Appear(JsonElement item)
    {
        var target = StoryType.Text(item, "target");
        if (target.Length == 0) return;

        var position = new Vec2(
            StoryType.Float(item, "x", PositionOf(Resolve(target) ?? EnsurePlayerActor()).X),
            StoryType.Float(item, "y", PositionOf(Resolve(target) ?? EnsurePlayerActor()).Y));
        var facing = StoryType.Text(item, "facing", "south");

        if (target == "player")
        {
            var player = EnsurePlayerActor();
            PlayerExit = (position, facing);
            Reveal(player);
            player.TeleportTo(new Vector2(position.X, position.Y));
            player.SetFacing(facing);
            return;
        }

        var actor = Resolve(target);
        if (actor is null)
        {
            GD.Print($"[stage] appear 跳过：舞台没有「{target}」这个演员");
            return;
        }

        Reveal(actor);
        var spot = Settle(position, actor);
        actor.TeleportTo(new Vector2(spot.X, spot.Y));
        actor.SetFacing(facing);
    }

    public ActorNode? AppearActor(string target, Vec2 position, string facing)
    {
        var actor = Resolve(target);
        if (actor is null) return null;

        Reveal(actor);
        var spot = Settle(position, actor);
        actor.TeleportTo(new Vector2(spot.X, spot.Y));
        actor.SetFacing(facing);
        return actor;
    }

    private Vec2 Settle(Vec2 target, ActorNode actor)
    {
        if (_grid is null || actor.Definition.Collision is not { } box) return target;

        foreach (var other in _actors.Values.Distinct())
        {
            if (ReferenceEquals(other, actor) || !other.Visible) continue;
            if (!Overlaps(target, box, new Vec2(other.Position.X, other.Position.Y), other.Definition.Collision))
                continue;

            var navigation = new NavigationWorld(StageMapId ?? "stage", _grid, DistinctActors);
            try
            {
                return navigation.Nearest(NavigationShape.Of(box), target, new StageNavigationActor(actor));
            }
            catch (InvalidOperationException)
            {
                return target;
            }
        }

        return target;
    }

    private IEnumerable<INavigationActor> DistinctActors() =>
        _actors.Values.Distinct().Where(GodotObject.IsInstanceValid).Select(node => new StageNavigationActor(node));

    private sealed class StageNavigationActor(ActorNode node) : INavigationActor
    {
        public object Key => node;

        public NavigationShape Shape => NavigationShape.Of(node.Definition.Collision);

        public Vec2 Position => new(node.Position.X, node.Position.Y);

        public bool Enabled => node.Visible;

        public bool Party => false;

        public bool Ghost => false;

        public bool StrictTerrain => false;
    }

    private static bool Overlaps(Vec2 a, CollisionBox boxA, Vec2 b, CollisionBox? boxB)
    {
        if (boxB is not { } other) return false;
        return a.X + boxA.X < b.X + other.X + other.Width - 1e-4f &&
               a.X + boxA.X + boxA.Width > b.X + other.X + 1e-4f &&
               a.Y + boxA.Y < b.Y + other.Y + other.Height - 1e-4f &&
               a.Y + boxA.Y + boxA.Height > b.Y + other.Y + 1e-4f;
    }

    public void Face(JsonElement item)
    {
        var facing = StoryType.Text(item, "facing", "south");
        var target = StoryType.Text(item, "target");
        Resolve(target)?.SetFacing(facing);
        if (target == "player") PlayerExit = (PlayerExit.Position, facing);
    }

    public async Task PoseAsync(JsonElement item, CancellationToken token = default)
    {
        var actor = Resolve(StoryType.Text(item, "target"));
        if (actor is null) return;

        var pose = StoryType.Text(item, "pose", "idle");
        var clip = pose + "-" + actor.Facing;
        if (!actor.Visual.HasAnimation(clip))
            throw new InvalidOperationException($"角色动作缺少资源：{actor.Definition.Id}/{clip}");
        var hold = StoryType.Bool(item, "hold") && pose != "idle";
        Action? complete = null;
        if (!Instant)
        {
            actor.Visual.SetAnimationLoop(clip, pose == "idle");
            actor.Visual.Play(clip);
            actor.Visual.SetFrameAndProgress(0, 0);
            if (pose != "idle" && !hold)
            {
                complete = () =>
                {
                    if (GodotObject.IsInstanceValid(actor) && actor.Visual.Animation == clip)
                        actor.SetFacing(actor.Facing);
                };
                actor.Visual.AnimationFinished += complete;
            }
            var duration = StoryType.Int(item, "durationMs");
            if (duration > 0)
                await StoryClock.WaitAsync(this, duration / 1000.0, token);
        }

        if (!GodotObject.IsInstanceValid(actor)) return;
        if (complete is not null) actor.Visual.AnimationFinished -= complete;
        if (hold)
        {
            actor.Visual.Play(clip);
            actor.Visual.Pause();
            actor.Visual.Frame = actor.Visual.GetFrameCount(clip) - 1;
        }
        else actor.SetFacing(actor.Facing);
    }

    public void Show(JsonElement item)
    {
        var actor = Resolve(StoryType.Text(item, "target"));
        if (actor is not null) Reveal(actor);
    }

    private void Reveal(ActorNode actor)
    {
        actor.Visible = true;

        if (Instant)
        {
            actor.Modulate = new Color(1f, 1f, 1f, 1f);
            return;
        }

        actor.CreateTween().TweenProperty(actor, "modulate:a", 1f, 0.18f);
    }

    public void Focus(JsonElement item)
    {
        var target = StoryType.Text(item, "target");
        CameraPoint = null;
        CameraFocus = Resolve(target);
    }

    public void Effect(JsonElement item)
    {
        var target = StoryType.Text(item, "target");
        var actor = Resolve(target);
        if (actor is null || !actor.Visible) return;

        var entry = _loader.World.StoryEffect(StoryType.Text(item, "effect"));
        var clip = entry?.Clip();
        if (clip is null)
        {
            _missing.Add($"effect:{StoryType.Text(item, "effect")}");
            return;
        }

        var anchor = actor.Definition.EffectAnchor;
        var effect = EffectSprite.Create(clip, _mapLease!, anchor?.X ?? 0f, anchor?.Y ?? 0f);
        if (effect is null) return;
        actor.AddChild(effect);
    }

    public void LocationEffect(JsonElement item)
    {
        var entry = _loader.World.StoryEffect(StoryType.Text(item, "effect"));
        var clip = entry?.Clip();
        if (clip is null || _mapRoot is null)
        {
            _missing.Add($"location-effect:{StoryType.Text(item, "effect")}");
            return;
        }

        var x = StoryType.Float(item, "x");
        var y = StoryType.Float(item, "y");
        var effect = EffectSprite.Create(clip, _mapLease!, x, y);
        if (effect is null) return;

        if (entry!.IsGround)
        {
            effect.ZIndex = -1;
            _mapRoot.AddChild(effect);
        }
        else
        {
            effect.ZIndex = (int)y + 1;
            (_actorRoot ?? _mapRoot).AddChild(effect);
        }
    }

    public void Point(JsonElement item) =>
        CameraPoint = new Vec2(StoryType.Float(item, "x"), StoryType.Float(item, "y"));

    public void ReleaseCamera()
    {
        CameraFocus = null;
        CameraPoint = null;
    }

    public async Task WalkAsync(JsonElement item, CancellationToken token = default)
    {
        var target = StoryType.Text(item, "target");
        var destination = new Vec2(StoryType.Float(item, "x"), StoryType.Float(item, "y"));
        var speed = MathF.Max(10f, StoryType.Float(item, "speedPxPerSecond", 133.3f));
        var actor = Resolve(target);
        if (actor is null) return;
        var facing = BasicAttackRules.FaceTarget(PositionOf(actor), destination);
        var followCamera = StoryType.Bool(item, "followCamera");
        if (followCamera) Focus(item);
        await MoveToAsync(target, destination, speed, token);
        if (!GodotObject.IsInstanceValid(actor)) return;
        actor.SetFacing(facing);
        if (target == "player") PlayerExit = (PositionOf(actor), facing);
        if (followCamera)
        {
            CameraFocus = null;
            CameraPoint = PositionOf(actor);
        }
    }

    public async Task ParallelAsync(JsonElement item, Func<JsonElement, Task> execute)
    {
        if (!item.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return;

        var running = new List<Task>();
        foreach (var action in actions.EnumerateArray())
            running.Add(execute(action));

        await Task.WhenAll(running);
    }

    public void Leave(JsonElement item)
    {
        var target = StoryType.Text(item, "target");
        if (target == "player") return;

        if (!_actors.TryGetValue(target, out var actor)) return;
        foreach (var key in _actors.Where(pair => ReferenceEquals(pair.Value, actor))
                     .Select(pair => pair.Key).ToList())
            _actors.Remove(key);
        _characterIds.Remove(actor);

        // 镜头正跟着他走的话先松开，否则淡出释放后 _PhysicsProcess 会摸到已释放的对象。
        if (ReferenceEquals(CameraFocus, actor)) CameraFocus = null;

        if (Instant)
        {
            actor.QueueFree();
            return;
        }

        var tween = actor.CreateTween();
        tween.TweenProperty(actor, "modulate:a", 0f, 0.3f);
        tween.TweenCallback(Callable.From(actor.QueueFree));
    }

    private async Task MoveToAsync(string target, Vec2 destination, float speed, CancellationToken token = default)
    {
        var actor = Resolve(target);
        if (actor is null) return;

        // 只有「没登记进移动世界」的角色才直接落点——隐藏但仍在舞台上的角色照常寻路，
        // 只是看不见，所以不能拿 Visible 当停用。
        if (Instant || _grid is null)
        {
            actor.Position = new Vector2(destination.X, destination.Y);
            if (target == "player") PlayerExit = (destination, actor.Facing);
            return;
        }

        var route = new WalkRoute(_grid, PositionOf(actor), destination, speed);

        while (route.State is WalkState.Walking)
        {
            // 剧本可能边走边换幕：ReleaseMap 会立刻释放上一幕的演员，
            // 走位协程必须安静退出，否则下一步访问就是 ObjectDisposedException。
            if (!GodotObject.IsInstanceValid(actor)) return;

            if (token.IsCancellationRequested)
            {
                actor.Position = new Vector2(destination.X, destination.Y);
                actor.UpdateMotion(Vec2.Zero, false);
                if (target == "player") PlayerExit = (destination, actor.Facing);
                return;
            }

            var current = PositionOf(actor);
            var deltaSeconds = (float)GetPhysicsProcessDeltaTime();
            var waypoint = route.Next(current, deltaSeconds * 1000f);

            if (route.State == WalkState.Failed)
            {
                actor.UpdateMotion(Vec2.Zero, false);
                throw new InvalidOperationException($"剧情道路不可达：{target} → ({destination.X}, {destination.Y})");
            }

            if (route.State != WalkState.Walking) break;
            if (waypoint is null)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                continue;
            }

            var toTarget = waypoint.Value - current;
            var distance = toTarget.Length;
            if (distance < 1e-4f)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                continue;
            }

            var direction = new Vec2(toTarget.X / distance, toTarget.Y / distance);
            var stepLength = MathF.Min(speed * deltaSeconds, distance);
            var applied = MovementSolver.Step(_grid, current, direction * stepLength, 8f);
            var next = current + applied;

            actor.Position = new Vector2(next.X, next.Y);
            actor.UpdateMotion(direction, true);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        if (route.State == WalkState.Failed)
            throw new InvalidOperationException($"剧情道路不可达：{target} → ({destination.X}, {destination.Y})");

        if (GodotObject.IsInstanceValid(actor))
        {
            actor.Position = new Vector2(destination.X, destination.Y);
            actor.UpdateMotion(Vec2.Zero, false);
            if (target == "player") PlayerExit = (PositionOf(actor), actor.Facing);
        }
    }

    private static Vec2 PositionOf(ActorNode actor) => new(actor.Position.X, actor.Position.Y);
}
