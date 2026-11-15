using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Story;

/// <summary>
/// 演出校验：把 693 段剧本
/// 按节点出口走一遍，沿途核对每一次登台／走位／特效／音效／对白引用的东西是否真的存在，
/// 并且（给了地图时）检查演员落位是否嵌墙、走位是否走得通。
///
/// 为什么要走而不是静态扫：舞台的演员表是一串命令累积出来的——`appear` 之后才能 `walk`，
/// `leave` 之后不能再 `face`。只看单条命令发现不了「先走位后登台」这类顺序错误，
/// 而那正是运行时最哑的一种失败：人不见了，什么都不报。
///
/// 这里不播动画、不推进时间，只维护「此刻台上都有谁、各自在哪」。
/// 遇到 <c>parallel</c> 时分别校验各条分支，再让它们同时落位——
/// 分支之间不能互相借演员，否则会漏掉「只有另一条分支才有人」的错误。
/// </summary>
public static class StoryPerformanceValidation
{
    private const int MaxVisits = 100_000;

    private const int MaxCallDepth = 32;

    private static readonly string[] Facings = ["north", "south", "east", "west"];

    /// <summary>
    /// 校验全部剧本。<paramref name="maps"/> 为空时只做引用检查，不做落位与走位
    /// （没有地图就没有地形可比，所以地图是可选的）。
    /// </summary>
    public static StoryPerformanceReport Validate(
        CampaignContent campaign, WorldIndex world, AudioCatalog? audio,
        IReadOnlyDictionary<string, MapDefinition>? maps = null)
    {
        var report = new StoryPerformanceReport();
        foreach (var script in campaign.Scripts.Keys)
        {
            var runner = new Runner(campaign, world, audio, maps);
            runner.Run(script);
            report = report.Add(runner.Nodes, runner.Stages, runner.Walks, runner.Visits);
        }

        return report;
    }

    public sealed record StoryPerformanceReport(int Nodes = 0, int Stages = 0, int Walks = 0, int Visits = 0)
    {
        public StoryPerformanceReport Add(int nodes, int stages, int walks, int visits) =>
            new(Nodes + nodes, Stages + stages, Walks + walks, Visits + visits);
    }

    private sealed class Runner(
        CampaignContent campaign, WorldIndex world, AudioCatalog? audio,
        IReadOnlyDictionary<string, MapDefinition>? maps)
    {
        private readonly Dictionary<string, Member> _cast = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StageActor> _participants = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, StoryNode>> _nodeIndex = new(StringComparer.Ordinal);

        private string _mapId = campaign.Start.Map;
        private bool _dynamicMap;
        private NavigationWorld? _navigation;
        private string _script = string.Empty;
        private int _visits;

        public int Nodes { get; private set; }

        public int Stages { get; private set; }

        public int Walks { get; private set; }

        public int Visits => _visits;

        public void Run(string script)
        {
            _script = script;
            _mapId = campaign.Start.Map;
            _dynamicMap = false;
            _cast.Clear();
            _participants.Clear();
            _navigation = null;
            _seen.Clear();
            _visits = 0;

            var pending = new Stack<Frame>();
            pending.Push(new Frame(_script, campaign.Scripts[script].Entry, Snapshot(), []));

            while (pending.Count > 0)
            {
                var frame = pending.Pop();
                var identity = Identity(frame);
                if (!_seen.Add(identity)) continue;

                Restore(frame.State);
                Ensure(++_visits <= MaxVisits, "演出分支校验超过节点限制");

                Nodes += 1;
                var node = FindNode(frame.Script, frame.Node);
                Ensure(node is not null, $"剧情节点缺失 {frame.Script}/{frame.Node}");
                if (node is null) continue;

                foreach (var action in node.Presentation)
                {
                    switch (StoryType.Of(action))
                    {
                        case "dialogue":
                            ValidateLine(action);
                            break;
                        case "choice":
                            if (action.TryGetProperty("options", out var options) &&
                                options.ValueKind == JsonValueKind.Array)
                                foreach (var option in options.EnumerateArray())
                                {
                                    if (!option.TryGetProperty("lines", out var lines) ||
                                        lines.ValueKind != JsonValueKind.Array) continue;
                                    foreach (var line in lines.EnumerateArray()) ValidateLine(line);
                                }

                            break;
                        case "quest-popup":
                        {
                            // 隐藏任务不进日志、不挂标记，也不该在脚本里占一次面板。
                            var quest = campaign.FindQuest(StoryType.Text(action, "quest"));
                            Ensure(quest?.Hidden != true, $"隐藏任务不应有任务面板 {StoryType.Text(action, "quest")}");
                            break;
                        }
                        default:
                            Visit(action);
                            break;
                    }
                }

                var after = Snapshot();
                if (node.Exit.Type == "call")
                {
                    Ensure(frame.Stack.Count < MaxCallDepth, "剧情调用层级超过限制");
                    if (node.Exit.Target is { } target && node.Exit.Then is { } then)
                        pending.Push(new Frame(target.Script ?? frame.Script, target.Node ?? string.Empty, after,
                            [.. frame.Stack, new NodeRef(then.Script ?? frame.Script, then.Node ?? string.Empty)]));
                }
                else if (node.Exit.Type == "end" && frame.Stack.Count > 0)
                {
                    pending.Push(new Frame(frame.Stack[^1].Script, frame.Stack[^1].Node, after,
                        [.. frame.Stack[..^1]]));
                }
                else
                {
                    foreach (var target in ExitTargets(node.Exit))
                        pending.Push(new Frame(target.Script ?? frame.Script, target.Node ?? string.Empty, after, frame.Stack));
                }
            }
        }

        private StoryNode? FindNode(string script, string node)
        {
            if (!_nodeIndex.TryGetValue(script, out var nodes))
            {
                if (!campaign.Scripts.TryGetValue(script, out var definition)) return null;
                nodes = new Dictionary<string, StoryNode>(definition.Nodes.Length, StringComparer.Ordinal);
                foreach (var entry in definition.Nodes) nodes[entry.Id] = entry;
                _nodeIndex[script] = nodes;
            }

            return nodes.GetValueOrDefault(node);
        }

        private static IEnumerable<StoryTarget> ExitTargets(StoryExit exit)
        {
            static IEnumerable<StoryTarget> One(StoryTarget? target) =>
                target is null ? [] : [target];

            return exit.Type switch
            {
                "next" => One(exit.Target),
                "call" => One(exit.Target).Concat(One(exit.Then)),
                "branch" => One(exit.Then).Concat(One(exit.Otherwise)),
                "choice" => exit.Cases.Values.Concat(One(exit.Otherwise)),
                "waitEncounter" => One(exit.Outcomes?.Won).Concat(One(exit.Outcomes?.Lost)),
                _ => [],
            };
        }

        private void Visit(JsonElement action, bool validateEnd = true)
        {
            var type = StoryType.Of(action);

            if (type == "parallel")
            {
                if (!action.TryGetProperty("actions", out var raw) || raw.ValueKind != JsonValueKind.Array) return;

                var children = new List<JsonElement>();
                foreach (var child in raw.EnumerateArray()) Flatten(child, children);

                var before = Snapshot();
                foreach (var child in children)
                {
                    Restore(before);
                    Visit(child, false);
                }

                Restore(before);
                foreach (var child in children) UpdatePosition(child);
                ValidatePositions();
                return;
            }

            switch (type)
            {
                case "sound":
                {
                    var sound = StoryType.Text(action, "sound");
                    Ensure(audio is null || audio.Effects.ContainsKey(sound), $"音效缺失 {sound}");
                    return;
                }
                case "music":
                {
                    var sound = StoryType.Text(action, "sound");
                    Ensure(sound.Length == 0 || audio is null || audio.Music.ContainsKey(sound), $"音乐缺失 {sound}");
                    return;
                }
                case "stage":
                    EnterStage(action);
                    return;
                case "location-effect":
                {
                    var effect = StoryType.Text(action, "effect");
                    Ensure(world.StoryEffect(effect)?.Clip() is not null, $"特效缺失 {effect}");
                    return;
                }
                case "map-effect":
                    Ensure(action.TryGetProperty("enabled", out var enabled) &&
                           enabled.ValueKind is JsonValueKind.True or JsonValueKind.False, "地图效果开关状态无效");
                    return;
                case "world-object":
                    if (action.TryGetProperty("command", out var command))
                    {
                        if (StoryType.Text(command, "kind") == "place" &&
                            command.TryGetProperty("entity", out var entity))
                            Ensure(world.Actors.ContainsKey(StoryType.Text(entity, "actor")),
                                $"人物资源缺失 {StoryType.Text(entity, "actor")}");
                    }

                    if (StoryType.Text(action, "target") is { Length: > 0 } placed)
                        Ensure(_cast.ContainsKey(placed), $"人物未出场 {placed}");

                    UpdatePosition(action);
                    return;
            }

            if (!action.TryGetProperty("target", out _)) return;

            var target = StoryType.Text(action, "target");
            Ensure(_cast.TryGetValue(target, out var member), $"人物未出场 {target}");
            if (member is null) return;

            if (type == "effect")
            {
                var effect = StoryType.Text(action, "effect");
                Ensure(world.StoryEffect(effect)?.Clip() is not null, $"特效缺失 {effect}");
            }

            if (type is "walk" or "pose")
            {
                var pose = type == "walk" ? "walk" : StoryType.Text(action, "pose");
                Ensure(world.Actors.TryGetValue(member.Actor, out var definition), $"人物资源缺失 {member.Actor}");
                foreach (var facing in Facings)
                    Ensure(definition!.HasClip($"{pose}-{facing}"), $"动作缺失 {member.Actor}/{pose}-{facing}");
            }

            if (type == "walk") Walks += 1;
            if (type == "walk" && maps is not null && !_dynamicMap && !member.Unpositioned &&
                _navigation is not null && _participants.TryGetValue(target, out var actor) &&
                world.Actors.TryGetValue(member.Actor, out var walker))
            {
                var destination = new Vec2(StoryType.Float(action, "x"), StoryType.Float(action, "y"));
                var path = _navigation.Path(NavigationShape.Of(walker.Collision), new Vec2(member.X, member.Y),
                    destination, actor);
                Ensure(path is not null,
                    $"道路不可达 {target} ({member.X},{member.Y}) → ({destination.X},{destination.Y})");
            }

            UpdatePosition(action);
            if (validateEnd) ValidatePositions();
        }

        private static void Flatten(JsonElement action, List<JsonElement> into)
        {
            if (StoryType.Of(action) == "parallel" &&
                action.TryGetProperty("actions", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray()) Flatten(child, into);
                return;
            }

            into.Add(action);
        }

        private void EnterStage(JsonElement action)
        {
            Stages += 1;
            if (action.TryGetProperty("audio", out var stageAudio) && stageAudio.ValueKind == JsonValueKind.Object)
            {
                if (storyAudio(stageAudio, "music") is { Length: > 0 } music)
                    Ensure(audio is null || audio.Music.ContainsKey(music), $"音乐缺失 {music}");
                if (storyAudio(stageAudio, "ambience") is { Length: > 0 } ambience)
                    Ensure(audio is null || audio.Effects.ContainsKey(ambience), $"环境音缺失 {ambience}");
            }

            if (action.TryGetProperty("camera", out var camera) && camera.ValueKind == JsonValueKind.Object)
                Ensure(IsFinite(camera, "x") && IsFinite(camera, "y"), "镜头位置无效");

            if (action.TryGetProperty("curtain", out var curtain) && curtain.ValueKind == JsonValueKind.Object)
            {
                var opacity = StoryType.Float(curtain, "opacity", float.NaN);
                var color = StoryType.Int(curtain, "color", -1);
                Ensure(!float.IsNaN(opacity) && opacity is >= 0f and <= 1f &&
                       IsFinite(curtain, "color") && color is >= 0 and <= 0xffffff,
                    "幕布状态无效");
            }

            var map = StoryType.Text(action, "map");
            Ensure(world.Maps.Any(entry => entry.Id == map), $"地图缺失 {map}");

            _dynamicMap = StoryType.Bool(action, "mapFromWorld");
            _navigation = null;
            _mapId = map;
            _cast.Clear();
            _participants.Clear();

            _cast["player"] = new Member
            {
                Actor = campaign.Start.Actor,
                X = StoryType.Float(action, "x"),
                Y = StoryType.Float(action, "y"),
                Visible = StoryType.Bool(action, "playerVisible", true),
            };

            if (action.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
                foreach (var memberJson in cast.EnumerateArray())
                {
                    var id = StoryType.Text(memberJson, "id");
                    Ensure(!_cast.ContainsKey(id), $"出场标识重复 {id}");

                    var actorId = StoryType.Text(memberJson, "actor");
                    Ensure(world.Actors.ContainsKey(actorId), $"人物资源缺失 {actorId}");

                    _cast[id] = new Member
                    {
                        Actor = actorId,
                        X = StoryType.Float(memberJson, "x"),
                        Y = StoryType.Float(memberJson, "y"),
                        Visible = StoryType.Bool(memberJson, "visible", true),
                        Unpositioned = StoryType.Bool(memberJson, "unpositioned"),
                        CharacterId = OptionalText(memberJson, "characterId"),
                        WorldEntityId = OptionalText(memberJson, "worldEntityId"),
                        BoundToPartyOrEnemy = OptionalText(memberJson, "memberId") is not null ||
                                              OptionalText(memberJson, "enemyId") is not null,
                    };
                }

            foreach (var member in _cast.Values)
            {
                Ensure(world.Actors.TryGetValue(member.Actor, out var definition), $"人物资源缺失 {member.Actor}");
                foreach (var facing in Facings)
                    Ensure(definition!.HasClip($"idle-{facing}"), $"待机动画缺失 {member.Actor}/{facing}");
            }

            if (maps is null || _dynamicMap) return;

            Ensure(maps.TryGetValue(_mapId, out var mapDefinition), $"地图未载入 {_mapId}");
            if (mapDefinition is null) return;

            CheckCastBindings(action, mapDefinition);

            _navigation = new NavigationWorld(_mapId, new CollisionGrid(mapDefinition), () => _participants.Values);
            foreach (var id in _cast.Keys)
                _participants[id] = new StageActor(world, () => _cast.GetValueOrDefault(id));

            ValidatePositions();

            string? storyAudio(JsonElement body, string name) =>
                body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }

        /// <summary>
        /// 人物编号可能对应本图多个实体。舞台没写唯一实例键（<c>worldEntityId</c>）时，
        /// 至少要求本图只有一个候选——否则运行时「碰巧拿第一个」，换一次条件就换一个人。
        /// </summary>
        private void CheckCastBindings(JsonElement action, MapDefinition map)
        {
            var authored = campaign.PlacementTable.Where(placement => placement.Map == map.Id).ToArray();
            var candidates = new List<(string Id, string Actor, string? CharacterId)>();

            foreach (var entity in map.Entities)
                if (!authored.Any(placement => Replaces(placement, entity)))
                    candidates.Add((entity.Id, entity.Actor, entity.CharacterId));
            foreach (var placement in authored)
                candidates.Add((placement.Id, placement.Actor, placement.CharacterId));

            if (!action.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array) return;

            var bound = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var memberJson in cast.EnumerateArray())
            {
                var id = StoryType.Text(memberJson, "id");
                var actor = StoryType.Text(memberJson, "actor");
                var characterId = OptionalText(memberJson, "characterId");
                var worldEntityId = OptionalText(memberJson, "worldEntityId");

                if (OptionalText(memberJson, "memberId") is not null ||
                    OptionalText(memberJson, "enemyId") is not null) continue;

                var matches = worldEntityId is not null
                    ? candidates.Where(candidate => candidate.Id == worldEntityId).ToList()
                    : characterId is not null
                        ? candidates.Where(candidate => candidate.CharacterId == characterId).ToList()
                        : candidates.Where(candidate => candidate.Actor == actor).ToList();

                if (matches.Count > 1)
                    Ensure(false,
                        $"人物来源不唯一 {id}（{characterId ?? actor}）：" +
                        $"{string.Join("、", matches.Take(6).Select(candidate => candidate.Id))}；请补 worldEntityId");

                if (matches.Count == 1)
                {
                    Ensure(!bound.TryGetValue(matches[0].Id, out var owner),
                        $"人物实例重复绑定 {owner} / {id}（{matches[0].Id}）");
                    bound[matches[0].Id] = id;
                }
            }
        }

        private static bool Replaces(ActorPlacement authored, MapEntity entity) =>
            authored.CharacterId is not null && entity.CharacterId is not null
                ? authored.CharacterId == entity.CharacterId
                : authored.Actor == entity.Actor;

        private void UpdatePosition(JsonElement action)
        {
            var type = StoryType.Of(action);

            if (type == "world-object")
            {
                if (!action.TryGetProperty("command", out var command)) return;

                var kind = StoryType.Text(command, "kind");
                if (kind == "place" && StoryType.Text(action, "target") is { Length: > 0 } placed &&
                    command.TryGetProperty("entity", out var entity))
                {
                    if (!_cast.TryGetValue(placed, out var existing)) return;
                    _cast[placed] = new Member
                    {
                        Actor = StoryType.Text(entity, "actor"),
                        CharacterId = OptionalText(entity, "characterId"),
                        WorldEntityId = StoryType.Text(entity, "id"),
                        X = StoryType.Float(entity, "x"),
                        Y = StoryType.Float(entity, "y"),
                        Visible = entity.TryGetProperty("visible", out _)
                            ? StoryType.Bool(entity, "visible")
                            : true,
                        Scenery = true,
                        BoundToPartyOrEnemy = existing.BoundToPartyOrEnemy,
                    };
                    return;
                }

                var characterId = StoryType.Text(command, "characterId");
                var entityId = StoryType.Text(command, "id");
                var all = StoryType.Bool(command, "all");
                foreach (var (id, member) in _cast.ToArray())
                {
                    var matched = kind == "remove-character"
                        ? member.CharacterId == characterId
                        : entityId.Length > 0 && member.WorldEntityId == entityId;
                    if (!matched) continue;

                    _cast[id] = WithVisible(member, kind == "show");
                    if (kind == "remove-character" && !all) break;
                }

                return;
            }

            if (!action.TryGetProperty("target", out _)) return;

            var target = StoryType.Text(action, "target");
            if (!_cast.TryGetValue(target, out var current)) return;

            switch (type)
            {
                case "walk" when current.Unpositioned:
                    return;
                case "walk":
                case "appear":
                    _cast[target] = new Member
                    {
                        Actor = current.Actor,
                        X = StoryType.Float(action, "x", current.X),
                        Y = StoryType.Float(action, "y", current.Y),
                        Visible = type == "appear" || current.Visible,
                        Unpositioned = type == "appear" ? false : current.Unpositioned,
                        Scenery = current.Scenery,
                        CharacterId = current.CharacterId,
                        WorldEntityId = current.WorldEntityId,
                        BoundToPartyOrEnemy = current.BoundToPartyOrEnemy,
                    };
                    return;
                case "show":
                    _cast[target] = WithVisible(current, true);
                    return;
                case "leave":
                    _cast[target] = WithVisible(current, false);
                    return;
            }
        }

        private static Member WithVisible(Member member, bool visible)
        {
            var copy = member.Copy();
            copy.Visible = visible;
            return copy;
        }

        private void ValidatePositions()
        {
            if (_navigation is null) return;

            foreach (var (id, actor) in _participants)
            {
                if (!_cast.TryGetValue(id, out var member) || member.Scenery || !member.Visible) continue;
                if (!world.Actors.TryGetValue(member.Actor, out var definition)) continue;

                var point = new Vec2(member.X, member.Y);
                Ensure(_navigation.CanPlace(NavigationShape.Of(definition.Collision), point, actor),
                    $"人物落位被地形或其他人物阻挡 {id} ({member.X},{member.Y})");
            }
        }

        private void ValidateLine(JsonElement line)
        {
            var actor = StoryType.Text(line, "actor");
            if (actor.Length > 0) Ensure(world.Actors.ContainsKey(actor), $"对白人物缺失 {actor}");

            var portrait = StoryType.Text(line, "portrait");
            if (portrait.Length > 0) Ensure(world.Portraits.ContainsKey(portrait), $"对白立绘缺失 {portrait}");
        }

        private StageState Snapshot()
        {
            var members = new List<KeyValuePair<string, Member>>(_cast.Count);
            members.AddRange(_cast.Select(pair => new KeyValuePair<string, Member>(pair.Key, pair.Value.Copy())));
            return new StageState(_dynamicMap, _mapId, members);
        }

        private void Restore(StageState state)
        {
            _dynamicMap = state.DynamicMap;
            _mapId = state.Map;
            _cast.Clear();
            _participants.Clear();
            _navigation = null;

            foreach (var (id, member) in state.Members) _cast[id] = member.Copy();
            if (maps is null || _cast.Count == 0 || _dynamicMap) return;

            Ensure(maps.TryGetValue(_mapId, out var map), $"地图未载入 {_mapId}");
            if (map is null) return;

            _navigation = new NavigationWorld(_mapId, new CollisionGrid(map), () => _participants.Values);
            foreach (var id in _cast.Keys)
                _participants[id] = new StageActor(world, () => _cast.GetValueOrDefault(id));
        }

        private string Identity(Frame frame)
        {
            var builder = new StringBuilder(frame.Script).Append('\u0001').Append(frame.Node).Append('\u0001');
            foreach (var then in frame.Stack) builder.Append(then.Script).Append('#').Append(then.Node).Append(',');
            builder.Append('\u0001').Append(frame.State.DynamicMap ? '1' : '0').Append(frame.State.Map);

            foreach (var (id, member) in frame.State.Members)
                builder.Append('\u0001').Append(id).Append(':').Append(member.Actor)
                    .Append(':').Append(member.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':').Append(member.Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':').Append(member.Visible ? '1' : '0')
                    .Append(':').Append(member.Scenery ? '1' : '0')
                    .Append(':').Append(member.Unpositioned ? '1' : '0')
                    .Append(':').Append(member.CharacterId)
                    .Append(':').Append(member.WorldEntityId);

            return builder.ToString();
        }

        private void Ensure(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException($"剧情 {_script}: {message}");
        }

        /// <summary>内容里的数必须是有限数：缺字段、写字符串都不算。</summary>
        private static bool IsFinite(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetSingle(out var number) && float.IsFinite(number);

        private static string? OptionalText(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private sealed record Frame(string Script, string Node, StageState State, List<NodeRef> Stack);

        private sealed record NodeRef(string Script, string Node);

        private sealed record StageState(bool DynamicMap, string Map, List<KeyValuePair<string, Member>> Members);

        private sealed class Member
        {
            public string Actor = string.Empty;
            public float X;
            public float Y;
            public bool Visible = true;
            public bool Unpositioned;
            public bool Scenery;
            public string? CharacterId;
            public string? WorldEntityId;
            public bool BoundToPartyOrEnemy;

            public Member Copy() => (Member)MemberwiseClone();
        }

        private sealed class StageActor(WorldIndex world, Func<Member?> member) : INavigationActor
        {
            public object Key { get; } = new();

            public NavigationShape Shape
            {
                get
                {
                    if (member() is not { } current) return default;
                    return world.Actors.TryGetValue(current.Actor, out var definition)
                        ? NavigationShape.Of(definition.Collision)
                        : default;
                }
            }

            public Vec2 Position => member() is { } current ? new Vec2(current.X, current.Y) : Vec2.Zero;

            public bool Enabled => member() is { } current && current.Visible && !current.Scenery;

            public bool Party => false;

            public bool Ghost => false;

            public bool StrictTerrain => false;
        }
    }
}
