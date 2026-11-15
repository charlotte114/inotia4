using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inotia4.Domain;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.Effects;
using Inotia4.Domain.Party;
using Inotia4.Domain.Story;
using Inotia4.Domain.Ui;
using Inotia4.Domain.World;
using Inotia4.Domain.Tests;

var run = new Harness();

if (args.Contains("--mainline"))
{
    var contentRoot = FindContentRoot();
    if (contentRoot is null)
    {
        Console.WriteLine("没找到 public/content，无法走查主线");
        return 1;
    }

    return MainlineAudit.Run(contentRoot);
}

run.Case("开阔地中心可以站", () =>
{
    var grid = Grid([
        "...",
        "...",
        "...",
    ]);
    run.True(grid.CanStandOnTerrain(24, 24));
});

run.Case("越出地图边界一律不可站", () =>
{
    var grid = Grid([
        "...",
        "...",
        "...",
    ]);
    run.False(grid.CanStandOnTerrain(-4, 24));
    run.False(grid.CanStandOnTerrain(48 + 8, 24));
    run.False(grid.CanStandOnTerrain(24, -4));
});

run.Case("窄足底可以贴住墙，再靠近就会被挡住", () =>
{
    // 第 0 列是墙，占 x 0..16。脚点半边长 4 像素，所以 x=20 时足底刚好从 16 开始。
    var grid = Grid([
        "#...",
        "#...",
        "#...",
    ]);
    run.True(grid.CanStandOnTerrain(20, 24), "足底左缘压在墙的右边界上应该算站得住");
    run.False(grid.CanStandOnTerrain(18, 24), "足底探进墙里应该站不住");
});

run.Case("矩形跨格时任意一格挡住就不可站", () =>
{
    var grid = Grid([
        "...",
        "..#",
        "...",
    ]);
    run.False(grid.CanStandInRect(new Rect(0, 16, 40, 16)));
    run.True(grid.CanStandInRect(new Rect(0, 16, 30, 16)));
});

run.Case("斜走被挡住一轴时，另一轴仍然滑出去", () =>
{
    var grid = Grid([
        "..#",
        "..#",
        "..#",
    ]);
    var moved = MovementSolver.Step(grid, new Vec2(24, 8), new Vec2(4, 4), 8f);
    run.Equal(0f, moved.X, "横轴应被墙挡住");
    run.Equal(4f, moved.Y, "纵轴应照常前进");

    // 单步不超过半个碰撞盒时按足底半径 4 判，
    // 只有跨过半格才升级成整盒。所以 4 像素的一步按足底判，脚点能贴到墙边。
    var small = MovementSolver.Step(grid, new Vec2(24, 8), new Vec2(4, 4),
        body: new Rect(-8, -8, 16, 16));
    run.Equal(4f, small.X, "小步走窄足底，脚点能贴到墙边");
    run.Equal(4f, small.Y, "纵轴照常");

    var boxed = MovementSolver.Step(grid, new Vec2(24, 8), new Vec2(10, 4),
        body: new Rect(-8, -8, 16, 16));
    run.Equal(0f, boxed.X, "大步按整盒判，横轴被挡");
    run.Equal(4f, boxed.Y, "碰撞盒版纵轴照常滑出去");
});

run.Case("一格高的走廊：小步按窄足底能过去，整盒会卡在半像素上", () =>
{
    var grid = Grid([
        "#####",
        ".....",
        "#####",
    ]);
    var body = new Rect(-8, -8, 16, 16);

    var through = MovementSolver.Step(grid, new Vec2(40, 24.3f), new Vec2(-8, 0), body: body);
    run.Equal(-8f, through.X, "小步走窄足底，能沿走廊过去");

    var blocked = MovementSolver.Step(grid, new Vec2(40, 24.3f), new Vec2(-12, 0), body: body);
    run.Equal(0f, blocked.X, "大步按整盒判，半像素偏移也过不去");
});

run.Case("两个轴都通时整段位移都生效", () =>
{
    var grid = Grid([
        "...",
        "...",
        "...",
    ]);
    var moved = MovementSolver.Step(grid, new Vec2(8, 8), new Vec2(3, 2));
    run.Equal(3f, moved.X);
    run.Equal(2f, moved.Y);
});

run.Case("八方向输入归一化后不比分单轴更快", () =>
{
    var diagonal = MovementSolver.NormalizeAxis(new Vec2(1, 1));
    run.Near(1f, diagonal.Length, 1e-5f);
    var single = MovementSolver.NormalizeAxis(new Vec2(1, 0));
    run.Equal(1f, single.X);
});

run.Case("通行表比行列数短时不越界，按挡住处理", () =>
{
    var lenient = new MapDefinition
    {
        TileSize = 16,
        Columns = 4,
        Rows = 4,
        Blocked = [],
    };
    var grid = new CollisionGrid(lenient);
    run.False(grid.CanStandOnTerrain(8, 8), "缺数据时宁可不让人站，也不要抛异常");
});

run.Case("真实内容能按当前字段名解析出来", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("      跳过：没找到 public/content，说明测试不是在仓库里跑的");
        return;
    }

    var mapPath = Path.Combine(root, "content", "maps", "shadow-camp.json");
    var map = JsonSerializer.Deserialize<MapDefinition>(File.ReadAllText(mapPath))!;

    run.Equal("shadow-camp", map.Id);
    run.Equal(25, map.Columns);
    run.Equal(30, map.Rows);
    run.Equal(400, map.Width);
    run.Equal(480, map.Height);
    run.Equal(25 * 30, map.Blocked.Length, "blocked 应覆盖整张图");
    run.True(map.Features.Length > 0, "景物没解析出来说明字段名对不上");
    run.Equal(16, map.TileSize);

    // 剧情起点 (120, 312) 必须站得住，否则一进游戏就卡在墙里。
    var grid = new CollisionGrid(map);
    run.True(grid.CanStandOnTerrain(120, 312), "剧情起点不可站");

    var world = JsonSerializer.Deserialize<WorldIndex>(
        File.ReadAllText(Path.Combine(root, "content", "world.json")))!;
    run.True(world.Maps.Length > 400, "地图清单条数异常");
    run.True(world.Actors.ContainsKey("kiyan"), "主角定义没解析出来");
    run.True(world.Actors["kiyan"].Clips.ContainsKey("walk-south"), "行走片段缺失");
});

run.Case("空地上能直线走到目标", () =>
{
    var grid = Grid([
        "..........",
        "..........",
        "..........",
    ]);
    var path = Pathfinding.Find(new Vec2(16, 24), new GoalQuery(grid, new Vec2(120, 24)));
    run.True(path is not null, "空地上不该找不到路");
    run.True(path!.Count > 0);

    // 终点判定是「距目标一格以内」，所以路点会停在目标前 8 像素，这是原实现的口径。
    run.True(GoalQuery.Distance(path[^1], new Vec2(120, 24)) <= 8.01f, "路点应落在目标一格以内");
    run.Near(24f, path[^1].Y, 0.01f, "直线行走不该偏离同一横行");
});

run.Case("遇到墙会绕行，而不是直接穿过去", () =>
{
    var grid = Grid([
        "..........",
        ".#######..",
        "..........",
    ]);
    var start = new Vec2(8, 24);
    var path = Pathfinding.Find(start, new GoalQuery(grid, new Vec2(152, 24)));
    run.True(path is not null, "绕行路径不该为空");
    run.True(PathLength(start, path!) > 160f, "直线距离 144，绕行必然更长");
});

run.Case("目标被围死时判为不可达", () =>
{
    var grid = Grid([
        ".....",
        ".###.",
        ".#.#.",
        ".###.",
        ".....",
    ]);
    var path = Pathfinding.Find(new Vec2(8, 8), new GoalQuery(grid, new Vec2(40, 40)));
    run.True(path is null, "围死的目标应该判不可达，而不是绕不完的圈");
});

run.Case("斜行不能从两个障碍的夹角穿过去", () =>
{
    var grid = Grid([
        ".....",
        "..#..",
        ".#...",
        ".....",
    ]);
    var start = new Vec2(24, 24);
    var path = Pathfinding.Find(start, new GoalQuery(grid, new Vec2(40, 40)));
    run.True(path is not null, "这两块墙之间应该还能绕过去");

    var straight = MathF.Sqrt(16f * 16f + 16f * 16f);
    var length = PathLength(start, path!);
    run.True(length > straight + 8f, $"直线 {straight:F1}，实际 {length:F1}：说明是绕行而不是斜穿");
});

run.Case("预算用尽时返回 pending，不能当成无路", () =>
{
    var grid = Grid([
        "..........",
        "..........",
        "..........",
    ]);
    var search = new PathSearch(new Vec2(16, 24), new GoalQuery(grid, new Vec2(140, 24)));

    var first = search.Step(1);
    run.Equal(PathStatus.Pending, first.Status, "只给 1 个节点预算，不该立刻下结论");

    var rounds = 0;
    while (!search.IsComplete && rounds++ < 5000) search.Step(64);
    run.True(search.IsComplete, "分帧累加预算后应该能搜完");
});

run.Case("走位能真的走到终点", () =>
{
    var grid = Grid([
        "..........",
        "..........",
        "..........",
    ]);
    var speed = 8f * 1000f / 60f;
    var start = new Vec2(16, 24);
    var route = new WalkRoute(grid, start, new Vec2(120, 24), speed);
    var position = start;
    var frames = 0;

    while (route.State == WalkState.Walking && frames++ < 3000)
    {
        var waypoint = route.Next(position, 1000f / 60f);
        if (waypoint is null) continue;

        var toTarget = waypoint.Value - position;
        var distance = toTarget.Length;
        if (distance < 1e-4f) continue;

        var step = MathF.Min(speed / 60f, distance);
        position += new Vec2(toTarget.X / distance * step, toTarget.Y / distance * step);
    }

    run.Equal(WalkState.Arrived, route.State, $"最终状态 {route.State}，停在 ({position.X:F1}, {position.Y:F1})");
});

run.Case("四向角色的朝东片段靠自身镜像位，不被摆放点翻转抵消", () =>
{
    var actor = new ActorDefinition();
    actor.Clips["walk-west"] = new ActorClip { FrameWidth = 39, FrameHeight = 43, Frames = 8 };
    actor.Clips["walk-east"] = new ActorClip { FrameWidth = 39, FrameHeight = 43, Frames = 8, MirrorX = true };

    var east = actor.Clips["walk-east"];
    var west = actor.Clips["walk-west"];

    run.True(actor.IsDirectional, "有东西向片段就该算四向素材");
    run.True(actor.IsMirrored(east, false), "朝东的图要靠片段自带的镜像位翻出来");
    run.False(actor.IsMirrored(west, false));
    run.True(actor.IsMirrored(east, true), "摆放点的 flip 不能把东西向镜像抵消掉");
    run.False(actor.IsMirrored(west, true), "四向角色应忽略摆放点的 flip");
});

run.Case("单向素材仍然听摆放点的翻转", () =>
{
    var torch = new ActorDefinition();
    torch.Clips["idle-south"] = new ActorClip { FrameWidth = 32, FrameHeight = 32, Frames = 4 };

    run.False(torch.IsDirectional);
    run.False(torch.IsMirrored(torch.Clips["idle-south"], false));
    run.True(torch.IsMirrored(torch.Clips["idle-south"], true));
});

run.Case("真实主角的朝东片段确实带镜像位", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("      跳过：没找到 public/content");
        return;
    }

    var world = JsonSerializer.Deserialize<WorldIndex>(
        File.ReadAllText(Path.Combine(root, "content", "world.json")))!;
    var hero = world.Actors["kiyan"];

    run.True(hero.IsDirectional);
    run.True(hero.Clips["walk-east"].MirrorX, "walk-east 若不带镜像位，人往右走会背对行进方向");
    run.False(hero.Clips["walk-west"].MirrorX);
});

run.Case("会话记住位置与朝向，换图才增加访问次数", () =>
{
    var session = new Session();

    session.EnterMap("shadow-camp", new Vec2(120, 312), "south");
    run.Equal("shadow-camp", session.MapId);
    run.Equal(1, session.Visit, "第一次进图算第一次访问");

    // 同图重建不该算新访问，否则地图上的限时刷新会被反复重置。
    session.EnterMap("shadow-camp", new Vec2(200, 200), "east");
    run.Equal(1, session.Visit);
    run.Equal(200f, session.Position.X, "落点应更新");
    run.Equal("east", session.Facing);

    session.EnterMap("shadow-forest-west", new Vec2(40, 168), "west");
    run.Equal(2, session.Visit, "换图应增加访问次数");
});

run.Case("会话时间只前进不回退", () =>
{
    var session = new Session();
    session.Advance(16);
    session.Advance(17);
    run.Equal(33L, session.GameTimeMs);

    session.Advance(-5);
    run.Equal(33L, session.GameTimeMs, "负数增量不该把时间拨回去");
});

run.Case("千分比运算按向零截断", () =>
{
    var increase = new CombatOperation { Kind = "increase-thousandth", RatePerThousand = 250 };
    run.Equal(125, CombatOperations.Apply(100, increase));

    var decrease = new CombatOperation { Kind = "decrease-thousandth", RatePerThousand = 300 };
    run.Equal(63, CombatOperations.Apply(90, decrease), "90×0.7=63，不该四舍五入成 63.0 以外的值");

    var divide = new CombatOperation { Kind = "divide", Divisor = 0 };
    run.Equal(0, CombatOperations.Apply(100, divide), "除数为零时给 0，不抛异常");
});

run.Case("技能基础值取物理与魔法中较大的一条", () =>
{
    var best = DamageFormula.BestNormalDamage(100, 500, 0, 40, 0);
    run.Equal(150, best, "物攻 100 加 50% 增幅 = 150，高于魔攻 40");
});

run.Case("五维贡献按权重求和再除十截断", () =>
{
    var unit = new NumericUnit
    {
        Effect = "damage",
        Source = "physicalAttack",
        AttributeWeights = new Dictionary<string, int> { ["strength"] = 5, ["dexterity"] = 3 },
        Ranks = [new DamageRank { FixedAddition = 7 }],
    };
    var attributes = new PrimaryAttributes { Strength = 13, Dexterity = 9 };

    var prepared = DamageFormula.Prepare(unit, 1, attributes)!;
    run.Equal(9, prepared.WeightedAddition, "(5×13 + 3×9) / 10 = 9.2，截断为 9");
    run.Equal(7, prepared.FixedAddition);
    run.Equal(116, DamageFormula.Resolve(prepared, 100), "100 + 9 + 7");
});

run.Case("治疗不吃固定加值", () =>
{
    var unit = new NumericUnit
    {
        Effect = "heal",
        Source = "magicAttack",
        Ranks = [new DamageRank { FixedAddition = 50 }],
    };

    var prepared = DamageFormula.Prepare(unit, 1, new PrimaryAttributes())!;
    run.Equal(0, prepared.FixedAddition, "治疗量由权重和运算决定，固定加值被忽略");
});

run.Case("命中率高的攻击必定命中，并按抗性减伤", () =>
{
    var attacker = new Fighter { Level = 10, PhysicalAttack = 200, HitBase = 100 };
    var target = new Fighter { Level = 10, PhysicalResistance = 500, EvasionBase = 0 };

    var hit = HitResolver.RollPhysical(attacker, target, () => 0.5);
    run.Equal(HitKind.Hit, hit.Kind);
    run.Equal(100, hit.Damage, "200 点伤害被 50% 抗性砍半");
});

run.Case("闪避值高到一定程度就会落空", () =>
{
    var attacker = new Fighter { Level = 10, PhysicalAttack = 200, HitBase = 100 };
    var target = new Fighter { Level = 10, EvasionBase = 0, EvasionBonus = 750 };

    var hit = HitResolver.RollPhysical(attacker, target, () => 0.5);
    run.Equal(HitKind.Miss, hit.Kind, "750 闪避加成应把 500 的掷点吃掉");
});

run.Case("暴击按暴击率触发，伤害再乘暴击倍率", () =>
{
    var attacker = new Fighter { Level = 10, PhysicalAttack = 100, HitBase = 100, CriticalRate = 500, CriticalDamage = 1000 };
    var target = new Fighter { Level = 10, EvasionBase = 0 };

    var critical = HitResolver.RollPhysical(attacker, target, () => 0.3);
    run.Equal(HitKind.Critical, critical.Kind);
    run.Equal(200, critical.Damage, "100 点基础伤害乘 2 倍暴击");

    var normal = HitResolver.RollPhysical(attacker, target, () => 0.9);
    run.Equal(HitKind.Hit, normal.Kind, "掷点高过暴击率就是普攻");
});

run.Case("盾挡会落在普攻之前的判定顺序里", () =>
{
    var attacker = new Fighter { Level = 10, PhysicalAttack = 100, HitBase = 100 };
    var target = new Fighter { Level = 10, EvasionBase = 0, ShieldBlock = 500 };

    var blocked = HitResolver.RollPhysical(attacker, target, () => 0.3);
    run.Equal(HitKind.ShieldBlock, blocked.Kind);
    run.True(blocked.Damage < 100, $"盾挡应该减伤，实际 {blocked.Damage}");
});

run.Case("防御换算成减伤比例", () =>
{
    var resolved = new Fighter { Level = 10, Defense = 220, DefenseRate = 0 }.Resolved();
    run.Equal(458, resolved.PhysicalResistance);
});

run.Case("冷却按有效帧数计算，到帧即可用", () =>
{
    var cooldowns = new CooldownStore();
    cooldowns.Start("basic", 100, 800, 0);
    run.False(cooldowns.IsReady("basic", 79));
    run.True(cooldowns.IsReady("basic", 80), "加速到 80% 后 100 帧变 80 帧");
    run.Equal(10, cooldowns.RemainingFrames("basic", 70));
});

run.Case("战斗时钟只推进整数帧，余数留到下次", () =>
{
    var clock = new CombatClock();
    clock.Advance(100);
    run.Equal(1L, clock.Frame, "100 毫秒是 1 帧，余 40");
    clock.Advance(20);
    run.Equal(2L, clock.Frame, "余的 40 加上 20 凑满第二帧");
});

run.Case("角色面板按职业基础五维算出真实数值", () =>
{
    var catalog = new CharacterStatsCatalog
    {
        Classes = new Dictionary<string, ClassAttributes>
        {
            ["dark-knight"] = new() { Strength = 13, Dexterity = 9, Vitality = 12, Intelligence = 9, Mentality = 5 },
        },
    };

    var panel = CharacterPanel.Calculate(catalog, new ItemCatalog(), "dark-knight", 1);

    run.Equal(10, panel.PhysicalAttack);
    run.Equal(640 + 72 * 1 + 80 * 12, panel.MaxHealth);
    run.Equal(12, panel.BaseDefense);
    run.Equal(130, panel.PhysicalResistance, "1000×12/(12+20×(1+3)) = 130");
    run.Equal(56, panel.HitBase, "4×(敏捷 9 + 精力 5)");
    run.Equal(926, panel.HitRate);
    run.Equal(173, panel.EvasionRate);
});

run.Case("等级只影响含等级的项", () =>
{
    var catalog = new CharacterStatsCatalog
    {
        Classes = new Dictionary<string, ClassAttributes>
        {
            ["dark-knight"] = new() { Strength = 13, Dexterity = 9, Vitality = 12, Intelligence = 9, Mentality = 5 },
        },
    };

    var level1 = CharacterPanel.Calculate(catalog, new ItemCatalog(), "dark-knight", 1);
    var level10 = CharacterPanel.Calculate(catalog, new ItemCatalog(), "dark-knight", 10);

    run.Equal(level1.MaxHealth + 72 * 9, level10.MaxHealth, "每级加 72 点血");
    run.Equal(level1.PhysicalAttack, level10.PhysicalAttack, "五维没变，攻击不该变");
    run.True(level10.PhysicalResistance < level1.PhysicalResistance, "等级越高，同样的防御提供的减伤越低");
});

run.Case("自己加的点会算进五维和派生值", () =>
{
    var catalog = new CharacterStatsCatalog
    {
        Classes = new Dictionary<string, ClassAttributes>
        {
            ["dark-knight"] = new() { Strength = 13, Dexterity = 9, Vitality = 12, Intelligence = 9, Mentality = 5 },
        },
    };

    var allocated = new PrimaryAttributes { Strength = 5 };
    var panel = CharacterPanel.Calculate(catalog, new ItemCatalog(), "dark-knight", 1, allocated);

    run.Equal(18, panel.Attributes.Strength);
    run.Equal(13, panel.PhysicalAttack, "力量 18×0.5 截断得 9，加敏捷 4");
});

run.Case("初始装备按公式得出真实面板", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("      跳过：没找到 public/content");
        return;
    }

    var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;

    var bag = new Bag(() => { }, items, "dark-knight", seed: true,
        new EquipmentBagRules(stats, items, "dark-knight"));
    run.Equal(2, bag.WornIds.Count, "黑暗骑士开局是长剑 + 盾");
    run.Equal("basic-longsword", bag.WornInstances["main-hand"].ItemId);
    run.Equal("basic-shield", bag.WornInstances["off-hand"].ItemId);

    var panel = CharacterPanel.Calculate(stats, items, "dark-knight", 1, null, bag.WornInstances);

    run.Equal("longsword", panel.WeaponKind);
    run.Equal(18, panel.PhysicalAttack, "武器 7 + 力量 13×0.6 截断 7 + 敏捷 9 折半 4");
    run.Equal(19, panel.MagicAttack, "魔法 5 + 智力 9 + 精力 5");
    run.Equal(24, panel.BaseDefense, "盾 12 + 体力 12");
    run.Equal(230, panel.PhysicalResistance, "1000×24/(24+20×(1+3))");
    run.Equal(956, panel.HitRate, "命中基础 56，加成 30");
    run.Equal(70, panel.CriticalRate, "默认 30 + 长剑 40");
    run.Equal(120, panel.ShieldBlock);
    run.True(panel.OffHandShield, "副手是盾时盾挡才生效");
    run.Equal(1672, panel.MaxHealth);
});

run.Case("武器种类决定五维换算系数", () =>
{
    var catalog = new CharacterStatsCatalog
    {
        Classes = new Dictionary<string, ClassAttributes>
        {
            ["dark-knight"] = new() { Strength = 13, Dexterity = 9, Vitality = 12, Intelligence = 9, Mentality = 5 },
        },
        Equipment = new Dictionary<string, EquipmentNumbers>
        {
            ["two"] = new() { Attack = 0 },
            ["long"] = new() { Attack = 0 },
            ["staff"] = new() { Attack = 0 },
        },
    };

    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["two"] = new() { Kind = "two-handed", Equipment = new EquipmentSlotDefinition { Slot = "main-hand" } },
            ["long"] = new() { Kind = "longsword", Equipment = new EquipmentSlotDefinition { Slot = "main-hand" } },
            ["staff"] = new() { Kind = "staff", Equipment = new EquipmentSlotDefinition { Slot = "main-hand" } },
        },
    };

    CharacterPanel With(string? itemId)
    {
        var worn = new Dictionary<string, ItemInstance>();
        if (itemId is not null) worn["main-hand"] = new ItemInstance { Id = "t", ItemId = itemId };
        return CharacterPanel.Calculate(catalog, items, "dark-knight", 1, null, worn);
    }

    run.Equal(13, With("two").PhysicalAttack);
    run.Equal(11, With("long").PhysicalAttack);
    run.Equal(10, With("staff").PhysicalAttack, "法杖按 500‰ 计力量");
    run.Equal(10, With(null).PhysicalAttack, "徒手同样 500‰");

    run.Equal(8, With("staff").MagicAttack);
    run.Equal(0, With(null).MagicAttack, "空手没有魔攻");
});

run.Case("同一身份掷出的词条每次都一样", () =>
{
    var first = new SeededRandom("battle:dark-knight:basic-longsword:1");
    var second = new SeededRandom("battle:dark-knight:basic-longsword:1");

    for (var index = 0; index < 8; index++) run.Equal(first.Next(0, 1000), second.Next(0, 1000));

    // 身份不同就该是另一条序列，否则所有装备会共用同一组词条。
    run.True(new SeededRandom("battle:dark-knight:basic-shield:1").Next(0, 1000000) !=
             new SeededRandom("battle:dark-knight:basic-longsword:1").Next(0, 1000000));
});

run.Case("强化等级按卷轴规则加攻击", () =>
{
    var definition = new EquipmentNumbers { Attack = 7 };
    var entry = new ItemInstance
    {
        Id = "x",
        ItemId = "basic-longsword",
        Enhancement = 5,
        Numbers = new ItemNumbers
        {
            EnhancementRule = new EnhancementRule { Step = 1, Threshold = 3, Extra = 1 },
        },
    };

    run.Equal(15, EquipmentPowerCalculator.Power(definition, entry).Attack);

    entry.Numbers.EnhancementRule = null;
    run.Equal(7, EquipmentPowerCalculator.Power(definition, entry).Attack);
});

run.Case("装备校验拦住职业、槽位与等级不符", () =>
{
    var sword = new ItemDefinition
    {
        Name = "长剑",
        Level = 1,
        Equipment = new EquipmentSlotDefinition { Slot = "main-hand", Classes = ["dark-knight", "warrior"] },
    };
    var lateSword = new ItemDefinition
    {
        Name = "晚熟长剑",
        Level = 20,
        Equipment = new EquipmentSlotDefinition { Slot = "main-hand", Classes = ["dark-knight"] },
    };

    var catalog = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition> { ["sword"] = sword, ["late-sword"] = lateSword },
    };
    var bag = new Bag(() => { }, catalog, "dark-knight",
        rules: new EquipmentBagRules(new CharacterStatsCatalog(), catalog, "dark-knight", () => 1));
    bag.Add("sword");
    bag.Add("late-sword");
    var swordId = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    var lateId = bag.Entries.First(entry => entry.ItemId == "late-sword").Id;

    run.Equal(null, bag.EquipProblem(swordId, "main-hand"), "长剑 1 级能装");
    run.True(bag.EquipProblem(swordId, "main-hand", classId: "warlock") is not null, "术士不该能装长剑");
    run.True(bag.EquipProblem(swordId, "off-hand") is not null, "槽位不符要拦");
    run.True(bag.EquipProblem(lateId, "main-hand") is not null, "等级不够要拦");
});

run.Case("剧本先执行命令再走分支", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "a",
          "nodes": [
            {
              "id": "a",
              "presentation": [],
              "commands": [{ "type": "quest", "command": { "type": "accept", "quest": "q1" } }],
              "exit": {
                "type": "branch",
                "condition": { "type": "quest", "quest": "q1", "status": "active" },
                "then": { "script": "s", "node": "accepted" },
                "otherwise": { "script": "s", "node": "missed" }
              }
            },
            {
              "id": "accepted",
              "presentation": [{ "type": "dialogue", "text": "接下了" }],
              "commands": [],
              "exit": { "type": "end" }
            },
            {
              "id": "missed",
              "presentation": [{ "type": "dialogue", "text": "没接上" }],
              "commands": [],
              "exit": { "type": "end" }
            }
          ]
        }
      },
      "quests": [ { "id": "q1", "title": "测试任务", "completion": "story" } ]
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var journal = new QuestJournal(campaign);
    var director = new StoryDirector(campaign, journal);
    director.Start("s");

    var step = director.Next();
    run.Equal("dialogue", step.Type);
    run.Equal("接下了", StoryType.Text(step.Item, "text"), "命令先把任务置为进行中，分支才走 then");
    run.Equal(QuestStatus.Active, journal.StatusOf("q1"));
    run.Equal(StoryStepKind.End, director.Next().Kind, "节点演完就该结束");
});

run.Case("条件不成立时走 otherwise", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "a",
          "nodes": [
            {
              "id": "a",
              "presentation": [],
              "commands": [],
              "exit": {
                "type": "branch",
                "condition": { "type": "quest", "quest": "q1", "status": "completed" },
                "then": { "script": "s", "node": "yes" },
                "otherwise": { "script": "s", "node": "no" }
              }
            },
            { "id": "yes", "presentation": [{ "type": "dialogue", "text": "是" }], "commands": [], "exit": { "type": "end" } },
            { "id": "no", "presentation": [{ "type": "dialogue", "text": "否" }], "commands": [], "exit": { "type": "end" } }
          ]
        }
      },
      "quests": [ { "id": "q1", "title": "测试任务", "completion": "story" } ]
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var journal = new QuestJournal(campaign);

    journal.Accept("q1", "script");
    journal.Ready("q1");
    journal.Complete("q1");
    run.Equal(QuestStatus.Completed, journal.StatusOf("q1"));

    var director = new StoryDirector(campaign, journal);
    director.Start("s");
    run.Equal("是", StoryType.Text(director.Next().Item, "text"), "已完成就该走 then");
});

run.Case("任务奖励回执：结算入账、同轮不可重复、取最近一条、往返与校验", () =>
{
    const string json = """
    {
      "scripts": { "s": { "entry": "a", "nodes": [ { "id": "a", "presentation": [], "commands": [], "exit": { "type": "end" } } ] } },
      "quests": [
        { "id": "q1", "title": "测试任务", "completion": "story",
          "rewards": { "experience": 30, "money": 50, "items": [ { "item": "potion", "quantity": 2 } ] } }
      ]
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var journal = new QuestJournal(campaign);

    journal.Accept("q1", "script");
    journal.Ready("q1");
    var settlement = journal.Settle("q1");
    run.Equal(1, journal.Receipts.Count, "结算一次只有一条回执");
    run.Equal("q1", settlement!.Quest, "回执记的是这个任务");
    run.Equal(0, settlement.Cycle, "第一轮结算");
    var receipt = journal.RewardReceiptFor("q1");
    run.Equal(30, receipt!.Experience, "经验入回执");
    run.Equal(50 * Economy.MoneyGainMultiplier, receipt.Money, "金币入回执（作者态 ×10 换算）");
    run.Equal("potion:2", string.Join(":", receipt.Items.Select(item => $"{item.Item}:{item.Quantity}")), "物品入回执");

    run.True(!journal.HasReceipt("q1", 1), "第二轮还没结算过");
    var threw = false;
    try
    {
        journal.Apply(QuestCommand.Complete("q1"));
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    run.True(threw, "同一轮重复结算要抛错");
    run.Equal(1, journal.Receipts.Count, "被拒的那次不能留下回执");

    var captured = journal.CaptureReceipts();
    var revived = new QuestJournal(campaign);
    revived.Book.RestoreState(journal.Book.CaptureState());
    revived.RestoreReceipts(captured);
    var again = revived.RewardReceiptFor("q1", 0);
    run.Equal(30, again!.Experience, "往返后经验还在");
    run.Equal("potion:2", string.Join(":", again.Items.Select(item => $"{item.Item}:{item.Quantity}")), "往返后物品还在");
    run.True(revived.HasReceipt("q1", 0), "往返后认得这一轮已结算");

    var ahead = new List<QuestReceiptState> { new() { Quest = "q1", Cycle = 3, Experience = 1 } };
    var rejected = 0;
    foreach (var bad in new List<QuestReceiptState>[]
             {
                 ahead,
                 [new QuestReceiptState { Quest = "q1", Cycle = 0 },
                     new QuestReceiptState { Quest = "q1", Cycle = 0 }],
                 [new QuestReceiptState { Quest = "nope", Cycle = 0 }],
                 [new QuestReceiptState { Quest = "q1", Cycle = 0, Experience = -1 }],
             })
    {
        try { revived.RestoreReceipts(bad); }
        catch (InvalidOperationException) { rejected++; }
    }

    run.Equal(4, rejected, "四条坏回执照都不能灌进去");
    revived.RestoreReceipts(captured);
    run.Equal(1, revived.Receipts.Count, "校验失败后重灌好数据仍可用");
});

run.Case("剧情节点提交点：每节点触发一次，触发时该节点的命令已经落地", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "a",
          "nodes": [
            { "id": "a", "presentation": [{ "type": "dialogue", "text": "一" }],
              "commands": [{ "type": "flag", "flag": "fa" }],
              "exit": { "type": "next", "target": { "script": "s", "node": "b" } } },
            { "id": "b", "presentation": [{ "type": "dialogue", "text": "二" }],
              "commands": [{ "type": "flag", "flag": "fb" }],
              "exit": { "type": "next", "target": { "script": "s", "node": "c" } } },
            { "id": "c", "presentation": [{ "type": "dialogue", "text": "三" }],
              "commands": [{ "type": "flag", "flag": "fc" }],
              "exit": { "type": "end" } }
          ]
        }
      },
      "quests": []
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var director = new StoryDirector(campaign, new QuestJournal(campaign));

    var seen = new List<string>();
    director.NodeCommitted = () => seen.Add(
        string.Join("", new[] { "fa", "fb", "fc" }
            .Select(flag => director.Journal.Flag(flag) ? "1" : "0")));

    director.Start("s");
    while (director.Next().Kind != StoryStepKind.End) { }

    run.Equal(3, seen.Count, "三个节点各触发一次提交点");
    run.Equal("100", seen[0], "第一个节点触发时它的命令已经落地（fa 在、fb／fc 还不在）");
    run.Equal("110", seen[1], "第二个节点同理");
    run.Equal("111", seen[2], "第三个节点同理");

    // 没有接这个回调时（域里其它用法）照常跑，不该报错。
    var plain = new StoryDirector(campaign, new QuestJournal(campaign));
    plain.Start("s");
    var steps = 0;
    while (plain.Next().Kind != StoryStepKind.End) steps++;
    run.Equal(3, steps, "没人接提交点也照常演完");
});

run.Case("剧情运行表随档：捕获带上、恢复交回、没接线时先存着", () =>
{
    var campaign = JsonSerializer.Deserialize<CampaignContent>("""{"scripts":{},"quests":[]}""")!;
    Session Make() => new() { Journal = new QuestJournal(campaign), Bag = new Bag() };

    var runs = new StoryRunSnapshot
    {
        Sequence = 7,
        CompletedEvents = ["event-1"],
        Runs = [new StoryRunEntry { Id = "run-7", Script = "event-1", Node = "a", Status = "completed" }],
    };

    var session = Make();
    session.CaptureStoryRuns = () => runs;
    var state = session.CaptureState();
    run.Equal(7, state.Runtime!.Sequence, "捕获时带上运行序号");
    run.Equal(1, state.Runtime.CompletedEvents.Count, "已完成名单也带上");

    var json = JsonSerializer.Serialize(state, SaveGameStore.JsonOptions);
    var back = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;
    run.Equal(7, back.Runtime!.Sequence, "运行序号能原样往返");
    run.Equal("run-7", back.Runtime.Runs[0].Id, "运行行也在");
    var legacy = JsonSerializer.Deserialize<SessionState>("""{"Money":0}""", SaveGameStore.JsonOptions)!;
    run.Null(legacy.Runtime, "旧档没有 runtime 字段");

    StoryRunSnapshot? handed = null;
    var wired = Make();
    wired.ApplyStoryRuns = snapshot => handed = snapshot;
    wired.RestoreState(back);
    run.Equal(7, handed?.Sequence, "接线在：恢复时交回去");
    run.Null(wired.TakePendingStoryRuns(), "交出去了就不该再留着");

    var unwired = Make();
    unwired.RestoreState(back);
    run.Equal(7, unwired.TakePendingStoryRuns()?.Sequence, "没接线时先存着等导演");
    run.Null(unwired.TakePendingStoryRuns(), "只消费一次");
});

run.Case("剧情跳过：置位后后续对白整句略过，选项处解除", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "a",
          "nodes": [
            { "id": "a", "presentation": [{ "type": "dialogue", "text": "第一句" }], "commands": [], "exit": { "type": "next", "target": { "script": "s", "node": "b" } } },
            { "id": "b", "presentation": [{ "type": "dialogue", "text": "第二句" }], "commands": [], "exit": { "type": "end" } }
          ]
        }
      },
      "quests": []
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var director = new StoryDirector(campaign, new QuestJournal(campaign));
    var notifications = 0;
    director.Skipped += () => notifications++;

    director.Start("s");
    run.Equal("第一句", StoryType.Text(director.Next().Item, "text"));
    run.True(director.CanSkip, "演出中应当允许跳过");

    director.Skip();
    run.True(director.Skipping, "按过之后进入跳过状态");
    run.Equal(1, notifications, "跳过只通知一次");
    director.Skip();
    run.Equal(1, notifications, "重复按不重复通知");

    run.Equal("第二句", StoryType.Text(director.Next().Item, "text"));

    director.BeginChoice();
    run.True(!director.Skipping, "选项处解除跳过");
    run.True(!director.CanSkip, "选项面板开着时不允许跳过");
    director.EndChoice();
    run.True(director.CanSkip, "选完恢复可跳过");
});

run.Case("剧情跳过：不带到下一段剧本，确认弹窗期间不允许跳过", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "a",
          "nodes": [{ "id": "a", "presentation": [{ "type": "dialogue", "text": "一句" }], "commands": [], "exit": { "type": "end" } }]
        }
      },
      "quests": []
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var director = new StoryDirector(campaign, new QuestJournal(campaign));

    director.Start("s");
    director.Next();
    director.Skip();
    run.True(director.Skipping, "先跳过这一句");

    // 开新剧本时跳过状态清零：否则整段演出会被吞掉。
    director.Start("s");
    director.Next();
    run.True(!director.Skipping, "新的一段不带上一段的跳过");

    director.BeginConfirm();
    run.True(!director.CanSkip, "任务弹窗等确认时不允许跳过");
    director.EndConfirm();
    run.True(director.CanSkip, "确认完恢复可跳过");
});

run.Case("没有背包系统时命令记进跳过清单", () =>
{
    var executor = new StoryCommandExecutor(new QuestJournal());
    using var document = JsonDocument.Parse("""{ "type": "item-give", "item": "healing-potion-s", "quantity": 1 }""");
    executor.Apply(document.RootElement);

    run.Equal(0, executor.Applied.Count);
    run.True(executor.Skipped.Contains("item-give"), "跳过的类型必须留痕，不能静默成功");
});

run.Case("文本控制码转成 BBCode 且不丢字", () =>
{
    var formatted = StoryText.Bbcode("$R刺杀$B伯爵&N第二行");
    run.True(formatted.Contains("[color=#ff0000]刺杀"), "红色片段要转成 BBCode");
    run.True(formatted.Contains("[/color]伯爵"), "颜色段要显式闭合");
    run.True(formatted.Contains("伯爵\n第二行"), "&N 是换行");

    run.Equal("刺杀伯爵\n第二行", StoryText.Plain("$R刺杀$B伯爵&N第二行"));
    run.Equal("[点]", StoryText.Plain("[点]"), "方括号是正文，不该被吃掉");
});

run.Case("选项按玩家的值查 cases，没选就落第一项", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "ask",
          "nodes": [
            {
              "id": "ask",
              "presentation": [
                { "type": "choice", "prompt": "想知道什么？", "options": [
                  { "label": "关于升级", "value": "option-0" },
                  { "label": "没事了", "value": "option-1" } ] }
              ],
              "commands": [],
              "exit": {
                "type": "choice",
                "choice": "choice-1",
                "cases": {
                  "option-0": { "script": "s", "node": "explain" },
                  "option-1": { "script": "s", "node": "bye" }
                }
              }
            },
            { "id": "explain", "presentation": [{ "type": "dialogue", "text": "升级要靠经验" }], "commands": [], "exit": { "type": "end" } },
            { "id": "bye", "presentation": [{ "type": "dialogue", "text": "再见" }], "commands": [], "exit": { "type": "end" } }
          ]
        }
      },
      "quests": []
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;

    var picked = new StoryDirector(campaign, new QuestJournal());
    picked.Start("s");
    var prompt = picked.Next();
    run.Equal("choice", prompt.Type);
    run.Equal("想知道什么？", StoryType.Text(prompt.Item, "prompt"));
    picked.Choose("option-1");
    run.Equal("再见", StoryType.Text(picked.Next().Item, "text"), "选第二项就该走 bye");

    // 没有选过（或值不认识）时不能把玩家卡住，落第一项。
    var fallback = new StoryDirector(campaign, new QuestJournal());
    fallback.Start("s");
    fallback.Next();
    run.Equal("升级要靠经验", StoryType.Text(fallback.Next().Item, "text"));
});

run.Case("遭遇战把胜负分开续走", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "fight",
          "nodes": [
            {
              "id": "fight",
              "presentation": [],
              "commands": [],
              "exit": {
                "type": "waitEncounter",
                "encounter": {
                  "id": "b1", "map": "m1", "status": "prepared",
                  "spawns": [ { "id": "e0", "enemyId": "enemy-bat-1", "x": 100, "y": 100 } ]
                },
                "outcomes": {
                  "won": { "script": "s", "node": "won" },
                  "lost": { "script": "s", "node": "lost" }
                }
              }
            },
            { "id": "won", "presentation": [{ "type": "dialogue", "text": "赢了" }], "commands": [], "exit": { "type": "end" } },
            { "id": "lost", "presentation": [{ "type": "dialogue", "text": "输了" }], "commands": [], "exit": { "type": "end" } }
          ]
        }
      },
      "quests": []
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;

    var winner = new StoryDirector(campaign, new QuestJournal());
    winner.Start("s");
    var step = winner.Next();
    run.Equal(StoryStepKind.Encounter, step.Kind);
    run.Equal("s:fight:b1", step.Encounter!.Id);
    run.Equal("b1", step.Encounter.DefinitionId);
    run.Equal(1, step.Encounter.Spawns.Count);
    run.Equal("enemy-bat-1", step.Encounter.Spawns[0].EnemyId);
    winner.ResolveEncounter(true);
    run.Equal("赢了", StoryType.Text(winner.Next().Item, "text"), "胜利走 outcomes.won");

    var loser = new StoryDirector(campaign, new QuestJournal());
    loser.Start("s");
    loser.Next();
    loser.ResolveEncounter(false);
    run.Equal("输了", StoryType.Text(loser.Next().Item, "text"), "败北走 outcomes.lost");
});

run.Case("击杀计数集齐后任务变为可交付", () =>
{
    const string json = """
    {
      "quests": [
        {
          "id": "hunt", "title": "猎杀行动",
          "goals": [ { "id": "g", "kind": "defeat", "target": "enemy-wolf-1", "quantity": 2, "label": "头狼" } ]
        }
      ],
      "scripts": {}
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var journal = new QuestJournal(campaign);
    var tracker = new QuestTracker(campaign, journal);

    run.Equal(null, tracker.Guidance(), "没接任务时没有指引");

    journal.Accept("hunt");
    run.Equal(0, tracker.ReportDefeat("enemy-other").Count, "打错目标不计数");

    run.Equal(0, tracker.ReportDefeat("enemy-wolf-1").Count, "第一只还不够");
    run.Equal(1, tracker.CountOf("hunt", "g"));

    var ready = tracker.ReportDefeat("enemy-wolf-1");
    run.Equal(1, ready.Count);
    run.Equal("hunt", ready[0]);
    run.Equal(QuestStatus.Ready, journal.StatusOf("hunt"), "数量集齐就该可交付");
    run.Equal("头狼 2/2（可交付）", tracker.GoalText(campaign.FindQuest("hunt")!));
    run.Equal("hunt", tracker.Guidance()!.Id);
});

run.Case("NPC 任务槽：可交付独占，其次主线，再次支线，最后进行中", () =>
{
    const string json = """
    {
      "quests": [
        {
          "id": "side-1", "title": "支线", "giver": "npc-a", "giverCharacterId": "npc-1",
          "delivery": "npc-a", "deliveryCharacterId": "npc-1",
          "goals": [ { "id": "g", "kind": "defeat", "target": "wolf", "quantity": 1 } ]
        },
        {
          "id": "main-1", "title": "主线", "giver": "npc-a", "giverCharacterId": "npc-1",
          "completion": "story", "goals": []
        },
        {
          "id": "active-1", "title": "进行中", "giver": "npc-a", "giverCharacterId": "npc-1",
          "delivery": "npc-a", "deliveryCharacterId": "npc-1", "progressText": "快去打狼",
          "goals": [ { "id": "g", "kind": "defeat", "target": "boar", "quantity": 1 } ]
        },
        {
          "id": "other", "title": "别人的任务", "giver": "npc-b", "giverCharacterId": "npc-2",
          "completion": "story", "goals": []
        }
      ],
      "questGroups": [ { "id": "g1", "kind": "main", "questIds": ["main-1"] } ],
      "scripts": {}
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var journal = new QuestJournal(campaign);
    var tracker = new QuestTracker(campaign, journal);

    run.Equal("main-1", NpcInteraction.QuestSlot(campaign, journal, "npc-a", "npc-1")!.Id);
    run.Equal("other", NpcInteraction.QuestSlot(campaign, journal, "npc-b", "npc-2")!.Id,
        "other 的发布人是 npc-b，槽要归它");
    run.Null(NpcInteraction.QuestSlot(campaign, journal, "npc-x", null), "没人发布的 NPC 没有任务槽");

    journal.Accept("main-1");
    run.Equal("side-1", NpcInteraction.QuestSlot(campaign, journal, "npc-a", "npc-1")!.Id);

    journal.Accept("side-1");
    journal.Accept("active-1");
    tracker.ReportDefeat("wolf");
    run.Equal("side-1", NpcInteraction.QuestSlot(campaign, journal, "npc-a", "npc-1")!.Id);

    journal.Complete("side-1");
    run.Equal("main-1", NpcInteraction.QuestSlot(campaign, journal, "npc-a", "npc-1")!.Id);
    run.Equal(InteractionPlan.PlanKind.Progress,
        NpcInteraction.Interact(campaign, journal, "npc-a", "npc-1", null, null).Kind);
});

run.Case("交互计划：接取走脚本或直发，可交付结算，进行中给进度", () =>
{
    const string json = """
    {
      "quests": [
        {
          "id": "with-script", "title": "有脚本", "giver": "npc-a",
          "offerScript": "offer:quest", "completeScript": "complete:quest",
          "completion": "story", "goals": []
        },
        {
          "id": "direct", "title": "直发", "giver": "npc-b",
          "progressText": "还没好", "completion": "story", "goals": []
        }
      ],
      "scripts": {}
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var journal = new QuestJournal(campaign);

    var offer = NpcInteraction.Interact(campaign, journal, "npc-a", null, null, null);
    run.Equal(InteractionPlan.PlanKind.PlayScript, offer.Kind);
    run.Equal("offer:quest", offer.ScriptId);

    var direct = NpcInteraction.Interact(campaign, journal, "npc-b", null, null, null);
    run.Equal(InteractionPlan.PlanKind.Accept, direct.Kind);
    run.Equal("direct", direct.QuestId);

    journal.Accept("direct");
    var progress = NpcInteraction.Interact(campaign, journal, "npc-b", null, null, null);
    run.Equal(InteractionPlan.PlanKind.Progress, progress.Kind);
    run.Equal("还没好", progress.Text);

    journal.Ready("direct");
    var complete = NpcInteraction.Interact(campaign, journal, "npc-b", null, null, null);
    run.Equal(InteractionPlan.PlanKind.Complete, complete.Kind);

    // 没任务的 NPC：说闲话，有 hint 用 hint，否则描述，再否则一路小心。
    run.Equal("商店打烊了。", NpcInteraction.Interact(campaign, journal, "npc-c", null, "商店打烊了。", null).Text);
    run.Equal("一路小心。", NpcInteraction.Interact(campaign, journal, "npc-c", null, null, null).Text);
});

run.Case("交付结算：收走交付物、发放奖励，物品不足不结算", () =>
{
    const string json = """
    {
      "quests": [
        {
          "id": "deliver", "title": "送信", "completion": "story",
          "handoverItems": [ { "item": "letter", "quantity": 1 } ],
          "rewards": { "experience": 100, "money": 50, "items": [ { "item": "sword", "quantity": 1 } ] },
          "goals": []
        }
      ],
      "scripts": {}
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var inventory = new Bag();
    var journal = new QuestJournal(campaign) { Bag = inventory };

    run.Null(NpcInteraction.Settle(campaign, journal, "deliver"), "没接的任务不能结算");

    journal.Accept("deliver", "script");
    journal.Ready("deliver");
    run.Null(NpcInteraction.Settle(campaign, journal, "deliver"), "信没带够不能结算");

    inventory.Add("letter", 1);
    var receipt = NpcInteraction.Settle(campaign, journal, "deliver")!;
    run.Equal(100, receipt.Experience);
    run.Equal(500, receipt.Money, "体验版金币奖励放大 10 倍");
    run.Equal(1, receipt.Items.Count);
    run.Equal(("sword", 1), (receipt.Items[0].Item, receipt.Items[0].Quantity));

    run.Equal(0, inventory.Count("letter"), "信交出去了");
    run.Equal(1, inventory.Count("sword"), "奖励到包");
    run.Equal(QuestStatus.Completed, journal.StatusOf("deliver"), "结算后任务完成");
});

run.Case("剧本命令：队伍、操控与穿脱都由角色卡结算", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var companions = JsonSerializer.Deserialize<CompanionCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "companions.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var party = new PartyRoster(hero, stats, items, companions, () => { });
    var session = new Session { Bag = hero.Bag, Hero = hero, Party = party };
    var executor = new StoryCommandExecutor(new QuestJournal(), hero.Bag, session);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // 剧情入队：定义必须来自 companions.json 的 source: story。
    var story = companions.Definitions.Values.First(definition => definition.SourceOrDefault == "story");
    executor.Apply(Parse($$"""{"type":"party-join","companion":"{{story.Id}}"}"""));
    run.True(party.Get($"story-{story.Id}") is not null, "剧情伙伴进了名册");
    run.True(executor.Applied.Any(entry => entry.StartsWith("party-join")), "记进已执行清单");

    executor.Apply(Parse($$"""{"type":"party-control","member":"story-{{story.Id}}"}"""));
    run.Equal($"story-{story.Id}", party.ControlledId, "操控权交给剧情伙伴");

    executor.Apply(Parse("""{"type":"health","member":"player","value":"full"}"""));
    run.Equal(hero.Player.MaxHealth, hero.Player.Health, "value: full 回满");
    executor.Apply(Parse("""{"type":"health","member":"player","value":5}"""));
    run.Equal(5, hero.Player.Health, "指定数值");
    executor.Apply(Parse("""{"type":"mana","member":"player","value":3}"""));
    run.Equal(3, hero.Player.Mana);

    var gear = items.StartingEquipmentOf("dark-knight");
    var mainHand = gear["main-hand"];
    executor.Apply(Parse($$"""{"type":"equip","member":"player","item":"{{mainHand}}","slot":"main-hand","create":true}"""));
    run.Equal(mainHand, hero.Bag.WornInstances["main-hand"].ItemId, "新建并穿上主手");

    executor.Apply(Parse($$"""{"type":"unequip-destroy","member":"player","item":"{{mainHand}}"}"""));
    run.True(!hero.Bag.EquipmentFor("player").ContainsKey("main-hand"), "卸下并销毁");

    executor.Apply(Parse($$"""{"type":"party-leave","companion":"{{story.Id}}"}"""));
    run.True(party.Get($"story-{story.Id}") is null, "剧情伙伴离队");
});

run.Case("剧本命令：物品与金钱给收都真入账", () =>
{
    var journal = new QuestJournal();
    var inventory = new Bag();
    var session = new Session();
    var executor = new StoryCommandExecutor(journal, inventory, session);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    executor.Apply(Parse("""{"type":"item-give","item":"potion","quantity":2}"""));
    run.Equal(2, inventory.Count("potion"));

    executor.Apply(Parse("""{"type":"item-take","item":"potion","quantity":1}"""));
    run.Equal(1, inventory.Count("potion"));

    Exception? takeError = null;
    try { executor.Apply(Parse("""{"type":"item-take","item":"potion","quantity":5}""")); }
    catch (InvalidOperationException error) { takeError = error; }
    run.True(takeError is not null, $"非 allowPartial 的不足要抛错（整批失败口径）：{takeError?.Message}");
    run.Equal(1, inventory.Count("potion"), "不够就一个都不拿");

    executor.Apply(Parse("""{"type":"item-take","item":"potion","quantity":5,"allowPartial":true}"""));
    run.Equal(0, inventory.Count("potion"), "allowPartial 有多少拿多少");

    session.GrantReward(0, 100);
    executor.Apply(Parse("""{"type":"money-take","amount":30}"""));
    run.Equal(70, session.Money);

    Exception? moneyError = null;
    try { executor.Apply(Parse("""{"type":"money-take","amount":500}""")); }
    catch (InvalidOperationException error) { moneyError = error; }
    run.True(moneyError is not null, $"非 allowPartial 的金币不足要抛错：{moneyError?.Message}");
    run.Equal(70, session.Money, "钱不够不扣");
    executor.Apply(Parse("""{"type":"money-take","amount":500,"allowPartial":true}"""));
    run.Equal(0, session.Money, "allowPartial 清空");
});

run.Case("任务物品集齐后自动变可交付", () =>
{
    const string json = """
    {
      "quests": [
        {
          "id": "gather", "title": "采药",
          "onAcceptItems": [ { "item": "empty-bag", "quantity": 1 } ],
          "goals": [ { "id": "g", "kind": "item", "target": "herb", "quantity": 3, "label": "草药" } ]
        }
      ],
      "scripts": {}
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var inventory = new Bag();
    var journal = new QuestJournal(campaign) { Bag = inventory };
    var tracker = new QuestTracker(campaign, journal);

    journal.Accept("gather");
    inventory.Add("herb", 2);
    run.Equal(0, tracker.RefreshItemGoals(inventory).Count, "两棵不够");

    inventory.Add("herb", 1);
    var ready = tracker.RefreshItemGoals(inventory);
    run.Equal(1, ready.Count);
    run.Equal("gather", ready[0]);
    run.Equal(QuestStatus.Ready, journal.StatusOf("gather"));

    run.Equal(0, tracker.RefreshItemGoals(inventory).Count);
});

run.Case("任务道具随剧本接取命令发放", () =>
{
    const string json = """
    {
      "quests": [
        {
          "id": "q", "title": "带东西跑腿", "completion": "story",
          "onAcceptItems": [ { "item": "parcel", "quantity": 1 } ],
          "goals": []
        }
      ],
      "scripts": {
        "s": {
          "entry": "n1",
          "nodes": [
            {
              "id": "n1", "presentation": [],
              "commands": [ { "type": "quest", "command": { "type": "accept", "quest": "q" } } ],
              "exit": { "type": "end" }
            }
          ]
        }
      }
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var inventory = new Bag();
    var journal = new QuestJournal(campaign) { Bag = inventory };
    var director = new StoryDirector(campaign, journal, inventory);

    director.Start("s");
    director.Next();

    run.Equal(1, inventory.Count("parcel"), "接任务就应该拿到包裹");
});

run.Case("world 命令解析：place/remove/visibility/travel 全部落到 sink", () =>
{
    var placed = new List<WorldPlace>();
    var removed = new List<(string Map, string? CharacterId, bool All)>();
    var visibilities = new List<(string Map, string Actor, string? CharacterId, bool Visible)>();
    var travels = new List<(string Map, float X, float Y)>();

    var sink = new WorldCommandSpy(placed, removed, visibilities, travels);
    var executor = new StoryCommandExecutor(new QuestJournal(), worldSink: sink);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    executor.Apply(Parse("""
    {"type":"world","command":{"kind":"place","scope":"visit","entity":{
        "id":"story-character-npc-40","actor":"darius","characterId":"npc-40",
        "map":"shadow-forest-west","x":8,"y":136,"facing":"south","visible":true}}}
    """));
    executor.Apply(Parse("""
    {"type":"world","command":{"kind":"remove-character","characterId":"npc-109","map":"shadow-forest-west","all":false}}
    """));
    executor.Apply(Parse("""
    {"type":"world","command":{"kind":"actor-visibility","map":"silent-cave-depths","actor":"earl-grafton","characterId":"npc-109","visible":false}}
    """));
    executor.Apply(Parse("""
    {"type":"world","command":{"kind":"travel","destination":{"map":"shadow-camp","x":264,"y":344,"facing":"south"}}}
    """));
    executor.Apply(Parse("""{"type":"world","command":{"kind":"audio-bgm","track":"x"}}"""));

    run.Equal(1, placed.Count);
    run.Equal(("story-character-npc-40", "shadow-forest-west", "darius", "npc-40", 8f, 136f, "south", true, "visit"),
        (placed[0].EntityId, placed[0].Map, placed[0].Actor, placed[0].CharacterId,
         placed[0].X, placed[0].Y, placed[0].Facing, placed[0].Visible, placed[0].Scope));

    run.Equal(1, removed.Count);
    run.Equal(("shadow-forest-west", "npc-109", false), removed[0]);

    run.Equal(1, visibilities.Count);
    run.Equal(("silent-cave-depths", "earl-grafton", "npc-109", false), visibilities[0]);

    run.Equal(1, travels.Count);
    run.Equal(("shadow-camp", 264f, 344f), travels[0]);

    run.True(executor.Skipped.Contains("world:audio-bgm"), "不认识的 kind 记跳过，不静默");
});

static ItemDefinition Def(string name, string category, string? slot = null, bool stackable = true,
    ItemUse? use = null, int level = 1, int sockets = 0, bool enchantable = false, bool locked = false,
    bool twoHanded = false, bool dualWield = false, string? priceKind = null, bool discardable = true,
    string[]? classes = null)
{
    return new ItemDefinition
    {
        Name = name,
        Category = category,
        Stackable = stackable,
        Discardable = discardable,
        Level = level,
        PriceKind = priceKind,
        Use = use,
        Equipment = slot is null
            ? null
            : new EquipmentSlotDefinition
            {
                // 空表是「谁都不能穿」；夹具默认给暗黑骑士，供各测试按需覆盖。
                Slot = slot, Classes = classes ?? ["dark-knight"], TwoHanded = twoHanded, DualWield = dualWield,
                Locked = locked, SocketCapacity = sockets, Enchantable = enchantable,
            },
    };
}

static ItemCatalog CatalogOf(params (string Id, ItemDefinition Definition)[] items)
{
    var catalog = new ItemCatalog();
    foreach (var (id, definition) in items) catalog.Items[id] = definition;
    return catalog;
}

run.Case("背包按左右手把装备拆成武器与防具，不重不漏", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;

    var equipment = items.Items.Where(pair => pair.Value.Category == "equipment" && !pair.Value.IsPickup)
        .Select(pair => pair.Key).ToList();
    var weapons = equipment.Where(id => InventoryViews.PouchFor(items.Items[id]) == InventoryPouch.Weapon).ToList();
    var armour = equipment.Where(id => InventoryViews.PouchFor(items.Items[id]) == InventoryPouch.Armor).ToList();

    run.Equal(equipment.Count, weapons.Count + armour.Count, "武器与防具刚好覆盖全部装备");
    run.True(weapons.Count > 0 && armour.Count > 0, "两边都不该为空");
    run.Equal(InventoryPouch.Weapon, InventoryViews.PouchFor(items.Items["basic-longsword"]));
    run.Equal(InventoryPouch.Weapon, InventoryViews.PouchFor(items.Items["basic-shield"]));
    run.Equal(InventoryPouch.Armor, InventoryViews.PouchFor(items.Items["robe"]));
    run.Equal(InventoryPouch.Consumable, InventoryViews.PouchFor(items.Items["healing-potion-m"]));
});

run.Case("堆叠物品合并、不可堆叠物品一件一格", () =>
{
    var catalog = CatalogOf(
        ("potion", Def("药水", "consumable")),
        ("sword", Def("剑", "equipment", "main-hand", stackable: false)));
    var bag = new Bag(() => { }, catalog);

    bag.Add("potion", 3);
    bag.Add("potion", 2);
    run.Equal(1, bag.Entries.Count, "同一种可堆叠物品只占一个实例");
    run.Equal(5, bag.Count("potion"));

    bag.Add("sword", 2);
    run.Equal(2, bag.Entries.Count(entry => entry.ItemId == "sword"), "武器一件一个实例");
    bag.VerifyConsistency();
});

run.Case("拖动物品：同袋交换、跨袋拒绝、原格留空", () =>
{
    var catalog = CatalogOf(
        ("potion", Def("药水", "consumable")), ("potion2", Def("药水二", "consumable")),
        ("sword", Def("剑", "equipment", "main-hand", stackable: false)));
    var bag = new Bag(() => { }, catalog);
    bag.Add("potion");
    bag.Add("potion2");
    bag.Add("sword");

    var drinks = bag.PouchCells[InventoryPouch.Consumable];
    var first = drinks[0]!.Id;
    var second = drinks[1]!.Id;

    run.True(bag.PlaceInCell(first, InventoryViewKind.Consumable, 1), "同袋内可以交换");
    run.Equal(second, bag.PouchCells[InventoryPouch.Consumable][0]!.Id);
    run.Equal(first, bag.PouchCells[InventoryPouch.Consumable][1]!.Id);

    var sword = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    run.False(bag.PlaceInCell(sword, InventoryViewKind.Consumable, 0), "武器不能拖进消耗袋");
    run.False(bag.PlaceInCell(first, InventoryViewKind.Consumable, 1), "落回自己那一格不算移动");
    bag.VerifyConsistency();
});

run.Case("整理收拢空位、按分类与名称排序，重复整理不再通知", () =>
{
    var catalog = CatalogOf(
        ("potion", Def("药水", "consumable")), ("herb", Def("草药", "material")),
        ("sword", Def("剑", "equipment", "main-hand", stackable: false)));
    var notifications = 0;
    var bag = new Bag(() => notifications++, catalog);

    bag.Add("potion");
    bag.Add("herb");
    bag.Add("sword");
    bag.PlaceInCell(bag.Entries.First(entry => entry.ItemId == "potion").Id,
        InventoryViewKind.Consumable, 5);
    run.Equal(6, bag.PouchCells[InventoryPouch.Consumable].Count, "有空格时袋会长到那一格");

    bag.Organize();
    var before = notifications;
    var categories = bag.AllCells.Where(entry => entry is not null).Select(entry => entry!.ItemId).ToList();
    run.Equal("sword", categories[0], "装备排在最前");
    run.Equal("potion", categories[1]);
    run.Equal("herb", categories[2]);
    run.Equal(3, bag.AllCells.Count, "空位被收拢");

    bag.Organize();
    run.Equal(before, notifications, "顺序与空位都没变就不通知");
});

run.Case("穿脱原子完成：双手武器挤掉副手，副手先要有主手", () =>
{
    var catalog = new ItemCatalog
    {
        DualWieldClasses = ["warrior"],
        Items = new Dictionary<string, ItemDefinition>
        {
            ["sword"] = Def("剑", "equipment", "main-hand", stackable: false, priceKind: "weapon", classes: ["warrior"]),
            ["greatsword"] = Def("巨剑", "equipment", "main-hand", stackable: false, twoHanded: true, classes: ["warrior"]),
            ["shield"] = Def("盾", "equipment", "off-hand", stackable: false, classes: ["warrior"]),
        },
    };
    var bag = new Bag(() => { }, catalog, "warrior");

    bag.Add("sword");
    bag.Add("greatsword");
    bag.Add("shield");
    string Id(string item) => bag.Entries.First(entry => entry.ItemId == item).Id;

    bag.Equip(Id("sword"), "main-hand");
    bag.Equip(Id("shield"), "off-hand");
    run.Equal(2, bag.WornIds.Count);

    string Worn() => string.Join(",", bag.WornInstances.Keys.OrderBy(slot => slot));
    var before = Worn();
    var plan = bag.PlanEquip(Id("greatsword"), "main-hand");
    run.Equal(before, Worn(), "只算计划不动背包");
    run.Equal(1, plan.Count, "双手武器会挤掉副手");

    bag.Equip(Id("greatsword"), "main-hand");
    run.Equal("greatsword", bag.WornInstances["main-hand"].ItemId);
    run.False(bag.WornIds.ContainsKey("off-hand"));
    run.Equal(1, bag.Count("shield"), "换下的盾回到背包");
    bag.VerifyConsistency();
});

run.Case("装备判定与实际穿脱同口径，且不改变背包", () =>
{
    var catalog = CatalogOf(
        ("sword", Def("剑", "equipment", "main-hand", stackable: false, level: 1)),
        ("late", Def("晚熟剑", "equipment", "main-hand", stackable: false, level: 20)));
    var bag = new Bag(() => { }, catalog, "dark-knight",
        rules: new EquipmentBagRules(new CharacterStatsCatalog(), catalog, "dark-knight", () => 1));

    bag.Add("sword");
    bag.Add("late");
    var sword = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    var late = bag.Entries.First(entry => entry.ItemId == "late").Id;
    var revision = bag.Revision;

    run.Null(bag.EquipProblem(sword, "main-hand"), "1 级长剑能装");
    run.True(bag.EquipProblem(late, "main-hand") is not null, "等级不够要说明原因");
    run.True(bag.EquipProblem(sword, "off-hand") is not null, "槽位不符要拦");
    run.Equal(revision, bag.Revision, "判定不写背包");
    run.Equal(0, bag.WornIds.Count);
});

run.Case("坏掉的背包状态在恢复时被拒，原状态保留", () =>
{
    var catalog = CatalogOf(("potion", Def("药水", "consumable")));
    var bag = new Bag(() => { }, catalog);
    bag.Add("potion", 2);

    var state = bag.CaptureState();
    state.AllCells.Clear();
    var refused = false;
    try { bag.RestoreState(state); }
    catch (InvalidOperationException) { refused = true; }

    run.True(refused, "全部页缺物品就是不签");
    run.Equal(2, bag.Count("potion"), "拒绝后背包原样保留");
});

run.Case("用途没实现的物品被拒且不消耗，卓越灵药能过", () =>
{
    var catalog = CatalogOf(
        ("rune", Def("符文", "consumable", use: new ItemUse { Kind = ItemUseRules.Dungeon })),
        ("odd", Def("怪东西", "consumable", use: new ItemUse { Kind = ItemUseRules.Consume })),
        ("supreme-elixir", Def("卓越灵药", "consumable", use: new ItemUse { Kind = ItemUseRules.Consume })));
    var bag = new Bag(() => { }, catalog);
    bag.Add("rune");
    bag.Add("odd");
    bag.Add("supreme-elixir");

    string Id(string item) => bag.Entries.First(entry => entry.ItemId == item).Id;

    run.Throws(() => bag.Use(Id("rune")), ItemUseRules.UseNotImplemented);
    run.Throws(() => bag.Use(Id("odd")), ItemUseRules.UseNotImplemented);
    run.Equal(1, bag.Count("rune"), "拒绝时不消耗");
    run.Equal(1, bag.Count("odd"));

    var result = bag.Use(Id("supreme-elixir"));
    run.Equal(ItemUseRules.Consume, result.Kind);
    run.Equal(0, bag.Count("supreme-elixir"), "能用就正常消耗");
});

run.Case("强化与宝石写在实例上，孔满不再镶嵌", () =>
{
    var catalog = CatalogOf(
        ("sword", Def("剑", "equipment", "main-hand", stackable: false, sockets: 1,
            enchantable: true, priceKind: "weapon")),
        ("scroll", Def("强化卷轴", "material", use: new ItemUse { Kind = ItemUseRules.Enchant, Target = "weapon" })),
        ("gem", Def("宝石", "material", use: new ItemUse { Kind = ItemUseRules.Socket })));
    var bag = new Bag(() => { }, catalog);
    bag.Add("sword");
    bag.Add("gem");
    bag.Add("gem");
    bag.Add("scroll");

    var sword = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    string First(string item) => bag.Entries.First(entry => entry.ItemId == item).Id;

    var enchanted = bag.Use(First("scroll"), sword);
    run.Equal(1, bag.Get(sword)!.Enhancement, "没有强化规则时按必定 +1");
    run.Equal("剑 强化 +1", enchanted.Message);

    bag.Use(First("gem"), sword);
    run.Equal(1, bag.Get(sword)!.Gems.Count);

    var full = false;
    try { bag.Use(First("gem"), sword); }
    catch (ItemApplicationException error) { full = error.Notice == ItemNotice.SocketFull; }
    run.True(full, "孔满要给 socket-full");
    run.Equal(1, bag.Count("gem"), "被拒的宝石不消耗");
    bag.VerifyConsistency();
});

run.Case("快捷栏：同一技能只留最后一格，库存见底清掉物品格", () =>
{
    var catalog = CatalogOf(("potion", Def("药水", "consumable")));
    var bag = new Bag(() => { }, catalog);
    bag.Add("potion", 1);

    var bar = ShortcutBar.For(bag, "player", ["slash", "heal"], "potion");
    run.Equal(12, bar.Row().Length);
    run.Equal("slash", bar.Row()[0]!.Id);
    run.Equal("potion", bar.Row()[5]!.Id);

    bar.Assign(1, 0, new ShortcutEntry(ShortcutEntry.SkillKind, "slash"));
    run.Equal(1, bar.Row().Count(entry => entry is { Kind: ShortcutEntry.SkillKind, Id: "slash" }),
        "同一技能只剩一格");
    run.True(bar.Assigned(ShortcutEntry.SkillKind, "slash"));

    bag.Remove("potion", 1);
    run.False(bar.Row().Any(entry => entry is { Kind: ShortcutEntry.ItemKind, Id: "potion" }),
        "药水用光，快捷栏里的物品格一起清掉");
});

run.Case("接取任务：talk 类当场可交付，其它类留在进行中并发放任务道具", () =>
{
    var campaign = new CampaignContent
    {
        Quests =
        [
            new QuestDefinition
            {
                Id = "talk-quest", Title = "只说话", Completion = "talk",
                OnAcceptItems = [new QuestHandover { Item = "note", Quantity = 1 }],
            },
            new QuestDefinition
            {
                Id = "kill-quest", Title = "要打怪", Completion = "all",
                Goals = [new QuestGoal { Id = "g", Kind = "defeat", Target = "wolf", Quantity = 1 }],
            },
        ],
    };
    var catalog = CatalogOf(("note", Def("纸条", "material")));
    var bag = new Bag(() => { }, catalog);
    var journal = new QuestJournal(campaign) { Bag = bag };

    run.True(QuestRules.Accept(campaign, journal, bag, "talk-quest"));
    run.Equal(QuestStatus.Ready, journal.StatusOf("talk-quest"), "talk 类接取即达成，只差一次对话");
    run.Equal(1, bag.Count("note"), "接取时发的任务道具要进背包");

    QuestRules.Accept(campaign, journal, bag, "kill-quest");
    run.Equal(QuestStatus.Active, journal.StatusOf("kill-quest"), "有目标的任务仍从进行中开始");
    run.False(QuestRules.Accept(campaign, journal, bag, "kill-quest"), "已接的任务不会重复接取");
});

run.Case("任务簿：条件未满足锁定，前置完成后自动可接", () =>
{
    var campaign = new CampaignContent
    {
        Quests =
        [
            new QuestDefinition { Id = "first", Title = "第一步", Completion = "story" },
            new QuestDefinition
            {
                Id = "second", Title = "第二步", Completion = "story",
                AvailableWhen = JsonDocument.Parse(
                    """{"type":"quest","quest":"first","status":"completed"}""").RootElement,
            },
        ],
    };
    var journal = new QuestJournal(campaign);

    run.Equal(QuestStatus.Locked, journal.StatusOf("second"), "前置没完成时是锁定");
    run.False(journal.Accept("second", "script"), "锁定的任务接不了");

    journal.Accept("first", "script");
    journal.Ready("first");
    journal.Complete("first");
    run.Equal(QuestStatus.Available, journal.StatusOf("second"), "前置完成后自动解锁");
    run.True(journal.Accept("second", "script"));
});

run.Case("任务簿：内部任务只能由剧情授予", () =>
{
    var campaign = new CampaignContent
    {
        Quests = [new QuestDefinition { Id = "hidden", Title = "内部", Completion = "internal" }],
    };
    var journal = new QuestJournal(campaign);

    run.Equal(QuestStatus.Locked, journal.StatusOf("hidden"), "内部任务永远不进可接");
    run.False(journal.Visible("hidden"), "内部任务不可见");
    run.False(journal.Accept("hidden", "interaction"), "NPC 给不了内部任务");
    run.True(journal.Accept("hidden", "script"), "剧情可以授予");
    run.Equal(QuestStatus.Active, journal.StatusOf("hidden"));
});

run.Case("任务簿：事实按来源序号去重，characterId 优先比对", () =>
{
    var campaign = new CampaignContent
    {
        Quests =
        [
            new QuestDefinition
            {
                Id = "talker", Title = "找人说话", Completion = "all",
                Goals = [new QuestGoal { Id = "g", Kind = "talk", Target = "guard", CharacterId = "npc-guard", Quantity = 2 }],
            },
        ],
    };
    var journal = new QuestJournal(campaign);
    journal.Accept("talker", "script");

    run.Equal(1, journal.EmitFact("talk", "guard", "talk").ItemGrants.Count + journal.GoalCount("talker", "g"),
        "第一次说话记一格");
    run.Equal(1, journal.GoalCount("talker", "g"));

    journal.Apply(QuestCommand.EmitFact(new QuestFact
    {
        Kind = "talk", Target = "guard",
        Occurrence = new QuestFactOrigin("talk", 1),
    }));
    run.Equal(1, journal.GoalCount("talker", "g"), "旧序号不再计数");

    journal.Apply(QuestCommand.EmitFact(new QuestFact
    {
        Kind = "talk", Target = "guard", CharacterId = "npc-other",
        Occurrence = new QuestFactOrigin("talk", 2),
    }));
    run.Equal(1, journal.GoalCount("talker", "g"), "characterId 不符不计数");

    journal.Apply(QuestCommand.EmitFact(new QuestFact
    {
        Kind = "talk", Target = "guard", CharacterId = "npc-guard",
        Occurrence = new QuestFactOrigin("talk", 3),
    }));
    run.Equal(QuestStatus.Ready, journal.StatusOf("talker"), "集齐就转可交付");
});

run.Case("任务簿：重复任务结一轮后回到锁定并清空计数", () =>
{
    var campaign = new CampaignContent
    {
        Quests =
        [
            new QuestDefinition
            {
                Id = "daily", Title = "每日狩猎", Completion = "all", Repeat = true,
                Goals = [new QuestGoal { Id = "g", Kind = "defeat", Target = "wolf", Quantity = 1 }],
            },
        ],
    };
    var journal = new QuestJournal(campaign);
    var tracker = new QuestTracker(campaign, journal);

    journal.Accept("daily", "script");
    tracker.ReportDefeat("wolf");
    run.Equal(QuestStatus.Ready, journal.StatusOf("daily"));

    journal.Complete("daily");
    // 结算把它写回锁定，随后 refresh 因为条件允许又放回「可接」。
    run.Equal(QuestStatus.Available, journal.StatusOf("daily"), "结完一轮又能接");
    run.False(journal.RunningQuestIds.Contains("daily"), "已移出运行池");
    run.Equal(0, journal.GoalCount("daily", "g"), "计数清零");
    run.Equal(1, journal.CompletedCycles("daily"), "完成过一轮");

    run.True(journal.Accept("daily", "script"));
    run.Equal(QuestStatus.Active, journal.StatusOf("daily"));
    run.Equal(1, journal.Book.State("daily")!.Cycle, "周期加一");
});

run.Case("任务簿：清空支线收回交付物，主线不动", () =>
{
    var json = """
    {
      "quests": [
        {
          "id": "side", "title": "支线", "completion": "story",
          "handoverItems": [ { "item": "letter", "quantity": 1 } ]
        },
        { "id": "main", "title": "主线", "completion": "story" }
      ],
      "questGroups": [ { "id": "g1", "kind": "side", "questIds": ["side"] } ]
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var inventory = new Bag();
    inventory.Add("letter", 2);
    var journal = new QuestJournal(campaign) { Bag = inventory };

    journal.Accept("side", "script");
    journal.Accept("main", "script");
    run.Equal(2, inventory.Count("letter"));

    journal.Apply(QuestCommand.ResetSideQuests());

    run.Equal(QuestStatus.Available, journal.StatusOf("side"), "支线被清回可接状态");
    run.False(journal.RunningQuestIds.Contains("side"), "移出运行池");
    run.Equal(1, journal.Book.State("side")!.Cycle, "周期加一");
    run.Equal(1, inventory.Count("letter"), "交付物被收回");
    run.Equal(QuestStatus.Active, journal.StatusOf("main"), "主线不受影响");
});

run.Case("任务簿：坏快照被拒，原状态保留", () =>
{
    var campaign = new CampaignContent
    {
        Quests = [new QuestDefinition { Id = "q", Title = "任务", Completion = "story" }],
    };
    var journal = new QuestJournal(campaign);
    journal.Accept("q", "script");

    var captured = journal.Book.CaptureState();
    var state = new QuestBookState
    {
        Version = 4, Quests = captured.Quests, RunningQuestIds = captured.RunningQuestIds,
        FactSequences = captured.FactSequences, DebugPins = captured.DebugPins,
    };
    var refused = false;
    try { journal.Book.RestoreState(state); }
    catch (InvalidOperationException) { refused = true; }

    run.True(refused, "版本不对就不签");
    run.Equal(QuestStatus.Active, journal.StatusOf("q"), "拒绝后原状态保留");
});

run.Case("合成：装备不可合成、材料不足、金币不足各自拒绝，成功则写混沌并扣料扣钱", () =>
{
    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["sword"] = new()
            {
                Name = "长剑", Category = "equipment", Stackable = false, Level = 10,
                PriceKind = "weapon", BasePrice = 100,
                Equipment = new EquipmentSlotDefinition { Slot = "main-hand" },
            },
            ["potion"] = new() { Name = "药水", Category = "consumable", BasePrice = 10 },
            ["mithril"] = new() { Name = "秘银", Category = "material" },
        },
    };
    var stats = new CharacterStatsCatalog
    {
        Equipment = new Dictionary<string, EquipmentNumbers>
        {
            ["sword"] = new() { Attack = 10, MagicPercent = [80, 90], EnhancementUses = [1, 3], SocketLimit = 4 },
        },
    };
    var catalog = new CraftingCatalog
    {
        Operations = new Dictionary<string, CraftingOperation>
        {
            ["chaos"] = new()
            {
                Name = "混沌", CostPerLevel = 0,
                Materials = [new CraftingMaterial { Item = "mithril", Quantity = 6 }],
                Rule = new CraftingRule
                {
                    ValueDelta = [-23, 25], ValueBounds = [0, 150],
                    LevelDiceDivisor = 40, LevelClampDivisor = 10,
                },
            },
            ["epic-chaos"] = new()
            {
                Name = "深渊混沌", CostPerLevel = 0,
                Materials = [new CraftingMaterial { Item = "mithril", Quantity = 8 }],
                Rule = new CraftingRule
                {
                    ValueDelta = [-20, 22], ValueBounds = [0, 200],
                    LevelDiceDivisor = 30, LevelClampDivisor = 10,
                },
            },
        },
    };

    var bag = new Bag(() => { }, items, "dark-knight",
        rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { Bag = bag, Journal = new QuestJournal(new CampaignContent()) { Bag = bag } };
    var crafting = new EquipmentCrafting(catalog, items, session, bag);

    bag.Add("sword");
    bag.Add("potion");
    var sword = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    var potion = bag.Entries.First(entry => entry.ItemId == "potion").Id;

    run.Equal(CraftingCatalog.Notice.Incompatible, crafting.TargetProblem("chaos", potion), "药水不能合成");
    run.Equal(CraftingCatalog.Notice.Target, crafting.TargetProblem("chaos", "nope"), "找不到目标");

    var materials = false;
    try { crafting.Quote("chaos", sword); }
    catch (CraftingError error) { materials = error.Notice == CraftingCatalog.Notice.Materials; }
    run.True(materials, "材料不够要拦");

    bag.Add("mithril", 6);
    run.Equal(0, session.Money, "钱包本来就是空的");
    var quote = crafting.Quote("chaos", sword);
    run.Equal(0, quote.Cost, "合成不收费");

    crafting.Commit(quote, () => 0.5);
    run.Equal(0, bag.Count("mithril"), "材料扣掉");
    run.Equal(0, session.Money, "金币不变");
    run.Equal("chaos", bag.Get(sword)!.Numbers!.Chaos!.Tier, "写下混沌结果");
    run.Equal(CraftingCatalog.Notice.Incompatible, crafting.TargetProblem("epic-chaos", potion),
        "非装备在深渊混沌那里同样先判不兼容");
});

run.Case("融合器：药水按配方出货、宝石三合一、宝石孔给装备加一格", () =>
{
    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["sword"] = new()
            {
                Name = "长剑", Category = "equipment", Stackable = false, Level = 10,
                PriceKind = "weapon", BasePrice = 100,
                Equipment = new EquipmentSlotDefinition { Slot = "main-hand", SocketCapacity = 3 },
            },
            ["potion-s"] = new() { Name = "恢复药水（小）", Category = "consumable", Stackable = true },
            ["leaf"] = new() { Name = "生命之叶", Category = "material", Stackable = true },
            ["gem-1"] = new() { Name = "低级宝石", Category = "material", Kind = "gem", Stackable = false },
            ["gem-2"] = new() { Name = "中级宝石", Category = "material", Kind = "gem", Stackable = false },
            ["claw"] = new() { Name = "锋利脚爪", Category = "material", Stackable = true },
        },
    };
    var stats = new CharacterStatsCatalog
    {
        Equipment = new Dictionary<string, EquipmentNumbers>
        {
            ["sword"] = new() { Attack = 10, MagicPercent = [80, 90], EnhancementUses = [1, 3], SocketLimit = 1 },
        },
    };

    var catalog = new FusionCatalog
    {
        Version = 1,
        Menus = [new FusionMenu { Id = FusionCatalog.PotionMenu, Name = "药水合成" },
                 new FusionMenu { Id = FusionCatalog.GemMenu, Name = "宝石强化" },
                 new FusionMenu { Id = FusionCatalog.SocketMenu, Name = "宝石孔生成" }],
        Recipes =
        [
            new FusionRecipe
            {
                Id = "recipe-potion", RecipeId = 178, Menu = FusionCatalog.PotionMenu, Name = "恢复药水（小）",
                Product = "potion-s",
                Materials = [new FusionMaterial { Item = "leaf", Quantity = 3 }],
            },
            new FusionRecipe
            {
                Id = "recipe-gem", RecipeId = 188, Menu = FusionCatalog.GemMenu, Name = "中级宝石",
                Product = "gem-2",
                Materials = [new FusionMaterial { Item = "gem-1", Quantity = 3 }],
            },
            new FusionRecipe
            {
                Id = "recipe-socket", RecipeId = 192, Menu = FusionCatalog.SocketMenu, Name = "宝石孔生成",
                Target = FusionCatalog.EquipmentTarget,
                Materials =
                [
                    new FusionMaterial { Item = "claw", Quantity = 8 },
                    new FusionMaterial { Item = "gem-1", Quantity = 1 },
                ],
            },
        ],
    };
    catalog.Validate(items);

    var bag = new Bag(() => { }, items, "dark-knight",
        rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { Bag = bag, Journal = new QuestJournal(new CampaignContent()) { Bag = bag } };
    var bench = new FusionBench(catalog, items, session, bag);

    var missing = false;
    try { bench.Quote("recipe-potion", null); }
    catch (FusionError error) { missing = error.Notice == CraftingCatalog.Notice.Materials; }
    run.True(missing, "材料不够要拦");

    bag.Add("leaf", 3);
    bench.Commit(bench.Quote("recipe-potion", null));
    run.Equal(1, bag.Count("potion-s"), "产出一瓶药水");
    run.Equal(0, bag.Count("leaf"), "材料扣掉");

    bag.Add("gem-1", 3);
    run.Equal(3, bag.Count("gem-1"), "三颗宝石是三条独立记录");
    bench.Commit(bench.Quote("recipe-gem", null));
    run.Equal(0, bag.Count("gem-1"), "三颗全扣");
    run.Equal(1, bag.Count("gem-2"), "合成一颗中级");

    bag.Add("sword");
    var sword = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    var potion = bag.Entries.First(entry => entry.ItemId == "potion-s").Id;
    run.Equal(CraftingCatalog.Notice.Incompatible, bench.TargetProblem(catalog.Recipe("recipe-socket")!, potion),
        "药水不能开孔");
    run.Equal(CraftingCatalog.Notice.Target, bench.TargetProblem(catalog.Recipe("recipe-socket")!, null),
        "没选目标要拦");

    bag.Add("claw", 8);
    bag.Add("gem-1", 1);
    var before = bag.Get(sword)!.Numbers!.Sockets;
    bench.Commit(bench.Quote("recipe-socket", sword));
    run.Equal(before + 1, bag.Get(sword)!.Numbers!.Sockets, "多开一格");
    run.Equal(0, bag.Count("claw"), "材料扣掉");

    // 开到容量上限后就不能再开。
    while (bag.Get(sword)!.Numbers!.Sockets < 3) bag.ApplySocket(sword, bag.Revision);
    run.Equal(CraftingCatalog.Notice.Max, bench.TargetProblem(catalog.Recipe("recipe-socket")!, sword),
        "孔开满就不让再开");
});

run.Case("融合器批量：材料按次数累计，一次事务出 N 份，改造类不成批", () =>
{
    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["sword"] = new()
            {
                Name = "长剑", Category = "equipment", Stackable = false, Level = 5,
                PriceKind = "weapon",
                Equipment = new EquipmentSlotDefinition { Slot = "main-hand", SocketCapacity = 2 },
            },
            ["potion-s"] = new() { Name = "恢复药水（小）", Category = "consumable", Stackable = true },
            ["leaf"] = new() { Name = "生命之叶", Category = "material", Stackable = true },
            ["claw"] = new() { Name = "锋利脚爪", Category = "material", Stackable = true },
        },
    };
    var stats = new CharacterStatsCatalog
    {
        Equipment = new Dictionary<string, EquipmentNumbers>
        {
            ["sword"] = new() { Attack = 10, MagicPercent = [80, 90], EnhancementUses = [1, 3], SocketLimit = 0 },
        },
    };
    var catalog = new FusionCatalog
    {
        Version = 1,
        Menus = [new FusionMenu { Id = FusionCatalog.PotionMenu, Name = "药水合成" },
                 new FusionMenu { Id = FusionCatalog.SocketMenu, Name = "宝石孔生成" }],
        Recipes =
        [
            new FusionRecipe
            {
                Id = "recipe-potion", RecipeId = 178, Menu = FusionCatalog.PotionMenu, Name = "恢复药水（小）",
                Product = "potion-s",
                Materials = [new FusionMaterial { Item = "leaf", Quantity = 3 }],
            },
            new FusionRecipe
            {
                Id = "recipe-socket", RecipeId = 192, Menu = FusionCatalog.SocketMenu, Name = "宝石孔生成",
                Target = FusionCatalog.EquipmentTarget,
                Materials = [new FusionMaterial { Item = "claw", Quantity = 8 }],
            },
        ],
    };
    catalog.Validate(items);

    var bag = new Bag(() => { }, items, "dark-knight",
        rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { Bag = bag, Journal = new QuestJournal(new CampaignContent()) { Bag = bag } };
    var bench = new FusionBench(catalog, items, session, bag);

    bag.Add("leaf", 9);
    run.Equal(3, bench.MaxBatch("recipe-potion"), "9 片叶子够做 3 次");
    run.Equal(3, bench.Quote("recipe-potion", null, 3).Batch, "报价记住次数");

    // 要 4 瓶就差一片：整批拦住，不能做 3 瓶凑合。
    var rejected = false;
    try { bench.Quote("recipe-potion", null, 4); }
    catch (FusionError error) { rejected = error.Notice == CraftingCatalog.Notice.Materials; }
    run.True(rejected, "批量差一份材料就整批拒绝");

    bench.Commit(bench.Quote("recipe-potion", null, 3));
    run.Equal(3, bag.Count("potion-s"), "一次提交出 3 瓶");
    run.Equal(0, bag.Count("leaf"), "材料按 3 次扣光");

    bag.Add("sword");
    var sword = bag.Entries.First(entry => entry.ItemId == "sword").Id;
    bag.Add("claw", 8);
    run.Equal(1, bench.MaxBatch("recipe-socket"), "改造类最多 1 次");
    run.Equal(1, bench.Quote("recipe-socket", sword, 5).Batch, "强行传 5 也只算 1 次");
});

run.Case("融合器：传说装备要先学会配方，合成套装开出随机一条", () =>
{
    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["blade-a"] = new()
            {
                Name = "安格巴德的剑", Category = "equipment", Stackable = false, Level = 20,
                PriceKind = "weapon", BasePrice = 0,
                Equipment = new EquipmentSlotDefinition { Slot = "main-hand", SocketCapacity = 2 },
            },
            ["blade-b"] = new()
            {
                Name = "月灵圣剑", Category = "equipment", Stackable = false, Level = 20,
                PriceKind = "weapon", BasePrice = 0,
                Equipment = new EquipmentSlotDefinition { Slot = "main-hand", SocketCapacity = 2 },
            },
            ["mithril"] = new() { Name = "秘银", Category = "material", Stackable = true },
            ["combo"] = new()
            {
                Name = "D级武器合成套装", Category = "consumable", Kind = "goods", Stackable = false,
                Use = new ItemUse { Kind = ItemUseRules.Recipe, Recipes = ["recipe-a", "recipe-b"] },
            },
        },
        Recipes = new Dictionary<string, RecipeDefinition>
        {
            ["recipe-a"] = new() { Name = "安格巴德的剑", Result = "blade-a" },
            ["recipe-b"] = new() { Name = "月灵圣剑", Result = "blade-b" },
        },
    };
    var stats = new CharacterStatsCatalog
    {
        Equipment = new Dictionary<string, EquipmentNumbers>
        {
            ["blade-a"] = new() { Attack = 30, MagicPercent = [80, 90], EnhancementUses = [1, 3], SocketLimit = 2 },
            ["blade-b"] = new() { Attack = 32, MagicPercent = [80, 90], EnhancementUses = [1, 3], SocketLimit = 2 },
        },
    };

    var catalog = new FusionCatalog
    {
        Version = 1,
        Menus = [new FusionMenu { Id = FusionCatalog.LegendaryMenu, Name = "传说装备" }],
        Recipes =
        [
            new FusionRecipe
            {
                Id = "recipe-a", RecipeId = 193, Menu = FusionCatalog.LegendaryMenu, Name = "安格巴德的剑",
                Product = "blade-a",
                Materials = [new FusionMaterial { Item = "mithril", Quantity = 2 }],
            },
            new FusionRecipe
            {
                Id = "recipe-b", RecipeId = 194, Menu = FusionCatalog.LegendaryMenu, Name = "月灵圣剑",
                Product = "blade-b",
                Materials = [new FusionMaterial { Item = "mithril", Quantity = 3 }],
            },
        ],
    };
    catalog.Validate(items);

    var bag = new Bag(() => { }, items, "dark-knight",
        rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { Bag = bag, Journal = new QuestJournal(new CampaignContent()) { Bag = bag } };
    var bench = new FusionBench(catalog, items, session, bag);

    run.Equal(0, bench.Recipes(FusionCatalog.LegendaryMenu).Count, "没学过的配方不列出来");
    var denied = false;
    try { bench.Quote("recipe-a", null); }
    catch (FusionError error) { denied = error.Notice == CraftingCatalog.Notice.NoRecipe; }
    run.True(denied, "没登录的配方报「没有合成配方」");

    bag.Add("combo");
    var combo = bag.Entries.First(entry => entry.ItemId == "combo").Id;
    var learned = bag.Use(combo, random: () => 0.0).Recipe;
    run.Equal("recipe-a", learned, "随机值为 0 时开第一条");
    run.Equal(0, bag.Count("combo"), "套装道具用掉");
    run.Equal(1, bench.Recipes(FusionCatalog.LegendaryMenu).Count, "学会一条就列出一条");
    run.True(bench.Knows("recipe-a"), "学会的那条可以合成");
    run.True(!bench.Knows("recipe-b"), "没学会的仍然不行");

    bag.Add("mithril", 2);
    bench.Commit(bench.Quote("recipe-a", null));
    run.Equal(1, bag.Count("blade-a"), "炼出传说武器");
    run.Equal(0, bag.Count("mithril"), "材料扣掉");

    bag.Add("combo");
    var second = bag.Use(bag.Entries.First(entry => entry.ItemId == "combo").Id, random: () => 0.999).Recipe;
    run.Equal("recipe-b", second, "随机值贴近 1 时开最后一条");
    run.Equal(2, bench.Recipes(FusionCatalog.LegendaryMenu).Count, "两条都学会");
});

run.Case("技能书：进背包随机附着一条技能，别职业的用不了", () =>
{
    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["skillbook"] = new()
            {
                Name = "技能书", Category = "consumable", Kind = "book", Stackable = false,
                Use = new ItemUse { Kind = ItemUseRules.Learn },
            },
        },
        ClassSkills = new Dictionary<string, string[]>
        {
            ["dark-knight"] = ["sk-dk-1", "sk-dk-2"],
            ["priest"] = ["sk-pr-1"],
        },
    };

    var rules = new EquipmentBagRules(new CharacterStatsCatalog(), items, "dark-knight", bookRoll: () => 0.0);
    var bag = new Bag(() => { }, items, "dark-knight", rules: rules);
    bag.Add("skillbook");
    run.Equal("sk-dk-1", bag.Entries.First().Skill, "掷骰为 0 时开池里第一条");
    bag.Add("skillbook");
    run.Equal(2, bag.Entries.Count(), "技能书不堆叠，各带各的技能");
    run.True(bag.Entries.All(entry => entry.Skill is not null), "每条都附着技能");

    var foreign = new EquipmentBagRules(new CharacterStatsCatalog(), items, "dark-knight", bookRoll: () => 0.999);
    var other = new Bag(() => { }, items, "dark-knight", rules: foreign);
    other.Add("skillbook");
    run.Equal("sk-pr-1", other.Entries.First().Skill, "掷骰贴近 1 时开池里最后一条");
    var refused = false;
    try { other.Use(other.Entries.First().Id); }
    catch (InvalidOperationException error) { refused = error.Message == "请选择本职业的技能"; }
    run.True(refused, "别职业的书拒绝使用");
    run.Equal(1, other.Count("skillbook"), "拒绝后不消耗");
});

run.Case("技能书：真实内容的随机池就是六个职业的全部技能", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var book = items.Items.GetValueOrDefault("skillbook");
    run.True(book is not null, "技能书在物品表里");
    run.Equal(ItemUseRules.Learn, book!.Use?.Kind, "用途是 learn");
    run.False(book.Stackable, "不堆叠，每本各带各的技能");

    var pool = items.ClassSkills.Values.SelectMany(skills => skills).Distinct().ToList();
    run.Equal(90, pool.Count, "六个职业 90 条可训练技能（原版表 SKILLTRAINBASE 93 行去掉 3 条特殊）");
});

run.Case("融合器配方表：真实 fusion.json 能解析，五类齐全且与 crafting.json 不冲突", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "content", "fusion.json")));
    var catalog = FusionCatalog.Parse(document.RootElement);

    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    catalog.Validate(items);

    run.Equal(69, catalog.Recipes.Length, "69 条配方");
    run.Equal(5, catalog.Menus.Length, "五个菜单");
    run.Equal(10, catalog.OfMenu(FusionCatalog.PotionMenu).Count, "药水 10 条");
    run.Equal(4, catalog.OfMenu(FusionCatalog.GemMenu).Count, "宝石 4 条");
    run.Equal(1, catalog.OfMenu(FusionCatalog.SocketMenu).Count, "宝石孔 1 条");
    run.Equal(2, catalog.OfMenu(FusionCatalog.ChaosMenu).Count, "混沌 2 条");
    run.Equal(52, catalog.OfMenu(FusionCatalog.LegendaryMenu).Count, "传说装备 52 条");

    var combos = items.Items.Where(pair => pair.Value.Name.Contains("合成套装")).ToList();
    run.Equal(12, combos.Count, "12 种合成套装");
    var referenced = combos.SelectMany(pair => pair.Value.Use?.Recipes ?? Array.Empty<string>()).ToList();
    run.Equal(52, referenced.Count, "52 条配方引用");
    run.Equal(52, referenced.Distinct().Count(), "每条传说配方只被引用一次");
    var legendary = catalog.OfMenu(FusionCatalog.LegendaryMenu).Select(recipe => recipe.Id).ToHashSet();
    run.True(referenced.All(legendary.Contains), "引用全部落在传说页");
    // 存档校验按 items.json 顶层的 recipes 表认配方簿，两边必须同源。
    run.Equal(52, items.Recipes.Keys.Count(legendary.Contains), "items.json 的配方表就是这 52 条");

    // 混沌那两条与 crafting.json 是同一套材料，来自两张不同的表，必须仍然一致。
    using var crafting = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "content", "crafting.json")));
    var tiers = CraftingCatalog.Parse(crafting.RootElement);
    foreach (var (recipeId, tier) in new[] { ("recipe-176", CraftingCatalog.ChaosTier),
                                             ("recipe-177", CraftingCatalog.EpicChaosTier) })
    {
        var recipe = catalog.Recipe(recipeId)!;
        var operation = tiers.Operations[tier];
        run.Equal(operation.Materials.Length, recipe.Materials.Length, $"{tier} 材料样数一致");
        foreach (var material in operation.Materials)
            run.True(recipe.Materials.Any(entry => entry.Item == material.Item && entry.Quantity == material.Quantity),
                $"{tier} 材料 {material.Item}×{material.Quantity} 两份内容一致");
    }
});

run.Case("合成目录：真实 crafting.json 能解析并通过材料校验", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "content", "crafting.json")));
    var catalog = CraftingCatalog.Parse(document.RootElement);
    run.Equal(2, catalog.Operations.Count, "固定两档");

    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    catalog.Validate(items);

    run.Equal("混沌", catalog.Operations["chaos"].Name);
    run.Equal(150, catalog.Operations["chaos"].Rule.ValueBounds[1]);
    run.Equal(200, catalog.Operations["epic-chaos"].Rule.ValueBounds[1]);
});

run.Case("商店表：真实 shops.json 能解析，装备键序与内容一致", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "content", "shops.json")));
    var catalog = ShopCatalog.Parse(document.RootElement, stats);

    run.Equal(18, catalog.Shops.Count, "固定十八家店");
    run.Equal("神灯商人", catalog.Shops[catalog.LampShop].Name);
    run.Equal(15, catalog.Grades.Length);
    run.Equal(662, catalog.Equipment.Count, "662 条装备模板");

    // 随机货架取「前 64 条」，所以装备表的键序必须与 JSON 一致，字典枚举顺序得当作契约。
    var first = catalog.Equipment.Keys.Take(3).ToArray();
    run.Equal("dagger short-sword dirk", string.Join(' ', first), "装备键序按内容原样");

    var dagger = catalog.Equipment["dagger"];
    run.Equal(15, dagger.Variants!.Length, "十五档");
    run.Equal(128, dagger.ConcealedPrices.Length, "128 级隐匿报价");
    run.Equal(4, dagger.Variants[0].Attack, "生锈的匕首攻击");
    run.Equal("生锈的", dagger.Variants[0].GradeName);
    run.Equal(3, catalog.MapShopLevels["shadow-training-dungeon1-5e8c19b4"], "地图等级");

    // 分类图片齐全：每家店的分类都要有图标与选中态图标，否则商店面板画不出来。
    foreach (var shop in catalog.Shops.Values)
        foreach (var category in shop.Categories)
        {
            run.True(catalog.Assets.ContainsKey($"{category}-selected"), $"{category} 缺少选中态图片");
            run.True(items.Items.Count > 0);
        }
});

run.Case("商店估价：买价与卖价逐项对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "shop-prices.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 shop-prices.json 基准"); return; }

    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));

    ItemInstance Worn(string id) => new()
    {
        Id = "owned-1",
        ItemId = id,
        Enhancement = 5,
        Numbers = new ItemNumbers
        {
            MagicPercent = 80,
            Sockets = 2,
            EnhancementUses = 3,
            BaseAttack = 11,
            BaseDefense = 4,
            EnhancementRule = new EnhancementRule { Step = 2, Threshold = 4, Extra = 3 },
            Modifiers = { new Modifier { Stat = "strength", Value = 3 }, new Modifier { Stat = "criticalRate", Value = 9 } },
        },
    };

    foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
    {
        var id = row[0].GetString()!;
        run.Equal(row[1].GetInt32(), ShopValues.ItemValue(items, stats, id), $"{id} 裸价");
        run.Equal(row[2].GetInt32(), ShopValues.ItemValue(items, stats, id, Worn(id)), $"{id} 穿戴价");
    }

    foreach (var row in document.RootElement.GetProperty("sales").EnumerateArray())
    {
        var id = row[0].GetString()!;
        run.Equal(row[1].GetInt32(), ShopValues.SaleValue(ShopValues.ItemValue(items, stats, id)), $"{id} 回收价");
    }

    run.Equal(0, ShopValues.SaleValue(0), "零价物品没有回收价");
});

run.Case("商店：分类货架按物品种类铺满十六格", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (catalog, items, stats) = ReadShops(root);
    var session = new Session { StartMap = "shadow-camp" };
    var shop = new Storefront(catalog, items, stats, session, "weapons-dealer-f31d78ce", "shadow-camp");

    run.Equal("dagger", shop.Category, "默认选中第一个分类");
    run.Equal(9, shop.Offers.Count, "内容里九把可售匕首，全部上架");
    run.True(shop.Offers.Count <= 16, "货架显示前十六格");

    foreach (var offer in shop.Offers)
    {
        run.Equal("dagger", items.Get(offer.Instance.ItemId).Kind, "只上匕首");
        run.False(offer.Limited, "分类货架不限购");
        run.True(offer.Instance.Numbers is not null, "分类货架是装备，必须掷数值");
    }

    var before = shop.Offers[0].Id;
    shop.SelectCategory("dagger");
    run.Equal(before, shop.Offers[0].Id, "同一分类不重铺");
    shop.SelectCategory("longsword");
    run.Equal("longsword", items.Get(shop.Offers[0].Instance.ItemId).Kind, "换分类后换成长剑");

    var missing = false;
    try { shop.SelectCategory("shield"); }
    catch (InvalidOperationException) { missing = true; }
    run.True(missing, "分类不在店内要报错");
});

run.Case("商店：限定货位全图共十六格，换图才清空", () =>
{
    var visits = new ShopVisits();
    visits.EnterMap("shadow-camp");

    var firstCapacity = 0;
    var first = visits.Open("a", capacity =>
    {
        firstCapacity = capacity;
        return Enumerable.Range(0, 10).Select(index => FakeOffer($"a-{index}")).ToList();
    });
    run.Equal(16, firstCapacity, "第一家店拿到十六格");
    run.Equal(10, first.Count);

    var secondCapacity = 0;
    visits.Open("b", capacity => { secondCapacity = capacity; return []; });
    run.Equal(6, secondCapacity, "货位按已占用数递减");

    var again = visits.Open("a", _ => throw new Exception("不该再上货"));
    run.Equal(10, again.Count);
    run.Equal("a-0", again[0].Id);

    run.Equal("merchandise-1", visits.Identity());
    run.Equal("merchandise-2", visits.Identity());

    visits.EnterMap("shadow-camp");
    var sameMap = 0;
    visits.Open("c", capacity => { sameMap = capacity; return []; });
    run.Equal(6, sameMap, "同图不清空");

    visits.EnterMap("silent-cave-depths");
    var newMap = 0;
    visits.Open("d", capacity => { newMap = capacity; return []; });
    run.Equal(16, newMap, "换图后货位复位");

    var state = visits.CaptureState();
    var restored = new ShopVisits();
    restored.RestoreState(state);
    var restoredCapacity = 0;
    restored.Open("e", capacity => { restoredCapacity = capacity; return []; });
    run.Equal(16 - 0, restoredCapacity, "还原后按同一张图继续");
});

run.Case("商店：货架不超过十六格，全图限购货合计也就十六件", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (catalog, items, stats) = ReadShops(root);
    var session = new Session { StartMap = "shadow-camp", PlayerLevel = 10 };

    var crooked = new Storefront(catalog, items, stats, session, "crooked-street-vendor-582a688d",
        "shadow-camp", new LcgRandom().Next);
    run.True(crooked.Offers.Count is > 4 and <= 16, "四件常驻 + 随机货，最多十六格");
    run.Equal(4, crooked.Offers.Count(offer => !offer.Limited), "常驻商品四件");

    var lamp = new Storefront(catalog, items, stats, session, "lamp-dealer-007", "shadow-camp",
        new LcgRandom(7).Next);
    var limited = crooked.Offers.Count(offer => offer.Limited) + lamp.Offers.Count(offer => offer.Limited);
    run.True(limited <= 16, $"全图限购货 {limited} 件不该超过十六");

    var weapons = new Storefront(catalog, items, stats, new Session { StartMap = "shadow-camp" },
        "weapons-dealer-f31d78ce", "shadow-camp");
    run.True(weapons.Offers.Count is > 0 and <= 16, $"货架 {weapons.Offers.Count} 件，不超过十六");
});

run.Case("商店：买价按店家倍率、卖价按回收价，金币一进一出", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (catalog, items, stats) = ReadShops(root);
    var bag = new Bag(() => { }, items, "dark-knight", rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { StartMap = "shadow-camp", Bag = bag };
    var shop = new Storefront(catalog, items, stats, session, "merchandise-dealer-b09d9585", "shadow-camp");

    var potion = shop.Offers.First(offer => offer.Instance.ItemId == "healing-potion-s");
    var price = ShopValues.ItemValue(items, stats, "healing-potion-s");
    run.Equal(price, shop.BuyPrice(potion.Id), "杂货店倍率 100%，价格就是估价");

    session.GainMoney(price * 3);
    var paid = shop.Buy(potion.Id, 2);
    run.Equal(price * 2, paid, "买两瓶的钱");
    run.Equal(price, session.Money, "钱包扣掉");
    run.Equal(2, bag.Count("healing-potion-s"), "两瓶入包");

    var tooMany = false;
    try { shop.Buy(potion.Id, 100); }
    catch (InvalidOperationException error) when (error.Message.Contains("购买数量无效")) { tooMany = true; }
    run.True(tooMany, "超过一组要拦");

    var entry = bag.Entries.First(item => item.ItemId == "healing-potion-s");
    var sale = shop.SellPrice(entry);
    run.Equal(ShopValues.SaleValue(price), sale, "回收价");
    var earned = shop.Sell(entry.Id, 1);
    run.Equal(sale, earned, "卖一瓶的收入");
    run.Equal(price + sale, session.Money, "钱回到钱包");
    run.Equal(1, bag.Count("healing-potion-s"), "背包剩一瓶");

    session.PartyModifiers = () => new Dictionary<string, int>
    {
        ["buy-discount"] = 100,
        ["sell-price"] = 200,
    };
    run.Equal(price - price / 10, shop.BuyPrice(potion.Id), "一成折扣");
    run.Equal(ShopValues.SaleValue(price) + ShopValues.SaleValue(price) / 5, shop.SellPrice(entry), "两成加成");

    var rare = new Storefront(catalog, items, stats, new Session { StartMap = "shadow-camp" }, "rare-items-dealer-2d609a7f",
        "shadow-camp", new LcgRandom(3).Next);
    var limited = rare.Offers.First();
    run.True(limited.Limited, "珍品店都是掷出来的限购货");
    var overLimit = false;
    try { rare.Buy(limited.Id, 2); }
    catch (InvalidOperationException error) when (error.Message.Contains("购买数量无效")) { overLimit = true; }
    run.True(overLimit, "限购一件");
});

run.Case("商店：隐匿报价直接取等级表，不按估价折算", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (catalog, items, stats) = ReadShops(root);
    var session = new Session { StartMap = "shadow-camp", PlayerLevel = 12 };
    var shop = new Storefront(catalog, items, stats, session, "rare-items-dealer-2d609a7f", "shadow-camp",
        new LcgRandom(11).Next);

    var concealed = shop.Offers.Where(offer => offer.Concealed).ToList();
    run.True(concealed.Count > 0, "珍品店的货都隐匿报价");

    foreach (var offer in concealed)
    {
        var table = catalog.Equipment[offer.Instance.ItemId].ConcealedPrices[12];
        run.Equal(table, shop.BuyPrice(offer.Id), $"{offer.Instance.ItemId} 的隐匿报价");
    }
});

run.Case("装备档位：按两层权重抽档，静态档取默认索引", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;
    run.Equal(15, stats.Grades.Length, "十五档品质");

    var dagger = stats.Equipment["dagger"];
    run.Equal(15, dagger.GradeVariants.Length, "匕首十五档");
    run.Equal(2, dagger.DefaultGrade, "默认第 2 档");

    // 稀有度权重把稀有档清空后必须回退到可用档，而不是抽到空档。
    var profile = new GradeProfile { Weights = [700, 230, 70, 0, 0], MinRarity = 0, Luck = 0 };
    run.Equal("700 230 70 0 0", string.Join(' ', EquipmentGrade.RarityWeights(profile)));

    var luck = new GradeProfile { Weights = [700, 230, 70, 0, 0], Luck = 10 };
    run.Equal("680 230 90 0 0", string.Join(' ', EquipmentGrade.RarityWeights(luck)), "幸运搬权重");

    var random = new LcgRandom(5);
    var roll = EquipmentGrade.RollGrade(stats, dagger, profile, random.Next)!;
    run.True(roll.Grade is >= 0 and <= 9, "地面随机只到 9 档");
    run.Equal(dagger.GradeVariants[roll.Grade].Attack, roll.Attack, "攻防来自选中档");
    run.Equal(dagger.GradeVariants[roll.Grade].Name, roll.GradeName, "档名来自选中档");

    var stat = EquipmentGrade.StaticGrade(stats, dagger)!;
    run.Equal(2, stat.Grade, "静态档取默认索引");
    run.Equal(dagger.GradeVariants[2].Attack, stat.Attack);

    run.Null(EquipmentGrade.RollGrade(stats, new EquipmentNumbers { GradeVariants = [] }, profile, random.Next));
});

run.Case("装备力量：混沌装备按数值百分比缩放，攻击保底 1", () =>
{
    var definition = new EquipmentNumbers { Attack = 20, Defense = 10, MagicPercent = [50, 50] };
    var entry = new ItemInstance { Id = "x", ItemId = "x" };
    entry.Numbers = new ItemNumbers
    {
        MagicPercent = 50,
        Chaos = new EquipmentChaos(EquipmentChaos.ChaosTier, 50, 0),
    };

    var power = EquipmentPowerCalculator.Power(definition, entry);
    run.Equal(10, power.Attack, "两成攻击砍半");
    run.Equal(5, power.MagicAttack, "魔攻跟着攻击走");
    run.Equal(5, power.Defense, "防御同样缩放");

    entry.Numbers.Chaos = new EquipmentChaos(EquipmentChaos.ChaosTier, 0, 0);
    run.Equal(1, EquipmentPowerCalculator.Power(definition, entry).Attack, "攻击保底 1");
});

run.Case("背包：说明正文逐行对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "item-descriptions.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 item-descriptions.json 基准"); return; }

    var (shops, items, stats) = ReadShops(root);
    using var document = JsonDocument.Parse(File.ReadAllText(fixture));

    var checkedRows = 0;
    foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
    {
        var label = row.GetProperty("label").GetString()!;
        var itemId = row.GetProperty("item").GetString()!;

        var entry = new ItemInstance
        {
            Id = "owned-1",
            ItemId = itemId,
            Enhancement = row.GetProperty("enhancement").GetInt32(),
        };

        if (row.GetProperty("numbers").ValueKind == JsonValueKind.Object)
        {
            var spec = row.GetProperty("numbers");
            var numbers = new ItemNumbers
            {
                Grade = spec.TryGetProperty("grade", out var grade) ? grade.GetInt32() : null,
                MagicPercent = spec.TryGetProperty("magicPercent", out var magic) ? magic.GetInt32() : 0,
                Sockets = spec.TryGetProperty("sockets", out var sockets) ? sockets.GetInt32() : 0,
                EnhancementUses = spec.TryGetProperty("enhancementUses", out var uses) ? uses.GetInt32() : 0,
            };

            foreach (var modifier in spec.GetProperty("modifiers").EnumerateArray())
                numbers.Modifiers.Add(new Modifier
                {
                    Stat = modifier.GetProperty("stat").GetString()!,
                    Value = modifier.GetProperty("value").GetInt32(),
                });
            foreach (var gem in spec.GetProperty("gemModifiers").EnumerateArray())
                numbers.GemModifiers.Add(new Modifier
                {
                    Stat = gem.GetProperty("stat").GetString()!,
                    Value = gem.GetProperty("value").GetInt32(),
                });
            if (spec.TryGetProperty("chaos", out var chaos))
                numbers.Chaos = new EquipmentChaos(
                    chaos.GetProperty("tier").GetString()!,
                    chaos.GetProperty("valuePercent").GetInt32(),
                    chaos.GetProperty("levelOffset").GetInt32());

            entry.Numbers = numbers;
        }

        DescriptionMember? member = null;
        if (row.GetProperty("member").ValueKind == JsonValueKind.Object)
        {
            var who = row.GetProperty("member");
            member = new DescriptionMember(who.GetProperty("classId").GetString()!, who.GetProperty("level").GetInt32());
        }

        Dictionary<string, string>? classNames = null;
        if (row.GetProperty("classNames").ValueKind == JsonValueKind.Object)
        {
            classNames = [];
            foreach (var pair in row.GetProperty("classNames").EnumerateObject())
                classNames[pair.Name] = pair.Value.GetString()!;
        }

        DescriptionSkill? skill = null;
        if (row.GetProperty("skill").ValueKind == JsonValueKind.Object)
        {
            var info = row.GetProperty("skill");
            skill = new DescriptionSkill(info.GetProperty("name").GetString()!, info.GetProperty("className").GetString()!);
        }

        var body = ItemDetailLines.Build(items.Get(itemId), entry, stats.Equipment.GetValueOrDefault(itemId),
            stats, shops.Grades, member, classNames, skill, row.GetProperty("concealed").GetBoolean());

        var text = body.Rows.Count > 0 ? DescriptionTextPart.Plain(body.Rows[0].Parts!) : string.Empty;
        run.Equal(row.GetProperty("title").GetString(), body.Title, $"{label} 标题");
        run.Equal(row.GetProperty("color").ValueKind == JsonValueKind.Null
            ? null
            : row.GetProperty("color").GetString(), body.Color, $"{label} 标题色");
        run.Equal(row.GetProperty("sockets").GetInt32(), body.Sockets, $"{label} 孔数");
        run.Equal(row.GetProperty("text").GetString(), text, $"{label} 正文");
        checkedRows += 1;
    }

    run.True(checkedRows >= 90, $"基准里 {checkedRows} 条都对齐了");
});

run.Case("背包：版面数值与背包／物品版式基准一致", () =>
{
    var layout = BagLayouts.Create();
    run.Equal(85f, layout.LeftFrame.X);
    run.Equal(409f, layout.LeftFrame.Width);
    run.Equal(496f, layout.BagFrame.X);
    run.Equal(395f, layout.BagFrame.Width);
    run.Equal(214f, layout.Preview.X, "预览洞 = 左框 85 + 局部 129");
    run.Equal(110f, layout.Preview.Y);
    run.Equal(530f, layout.Grid.X, "网格组 = 右框 496 + 组内 34");
    run.Equal(27f, layout.Grid.Y);
    run.Equal(323f, layout.Grid.Width);
    run.Equal(83f, layout.Grid.Stride, "格子步长 0x53");
    run.Equal(4, layout.Grid.Columns);
    run.Equal(885f, layout.SideButtons.X, "分类列 = 右框 496 + 组内 389");
    run.Equal(871.5f, layout.Organize.X, "整理与分类列水平居中");
    run.Equal(530f, layout.Money.X, "金额落点 = 右框 496 + 34");
    run.Equal(365f, layout.Money.Y);
    run.Equal("67 47", $"{layout.Badge.X} {layout.Badge.Y}");

    run.Equal("0 70 139 208 277 347", string.Join(" ", ItemLayout.SideButtonOffsets));
    run.Equal(74f, ItemLayout.SlotSize);
    run.Equal(103f, ItemLayout.Money.Stride);

    run.Equal("head,neck,ring,accessory,body,back,main-hand,off-hand,hands,feet",
        string.Join(",", BagLayouts.EquipmentSlotOrder));

    var rich = MoneyDisplay.SplitMoney(99_999_999);
    run.Equal("9999 99 99", $"{rich.Gold} {rich.Silver} {rich.Copper}");
    var over = MoneyDisplay.SplitMoney(200_000_000);
    run.Equal("9999 99 99", $"{over.Gold} {over.Silver} {over.Copper}", "超出上限按上限显示");
    var odd = MoneyDisplay.SplitMoney(1_234_567);
    run.Equal("123 45 67", $"{odd.Gold} {odd.Silver} {odd.Copper}");
    run.Equal("0 0 0", $"{MoneyDisplay.SplitMoney(-5).Gold} {MoneyDisplay.SplitMoney(-5).Silver} {MoneyDisplay.SplitMoney(-5).Copper}");

    run.Equal("23.0%", CharacterValues.Percentage(230), "千分比写成一位小数");
    run.Equal("-0.5%", CharacterValues.Percentage(-5));
});

run.Case("背包：动作菜单按穿着、可穿槽与用途给出", () =>
{
    var catalog = CatalogOf(
        ("blade", Def("剑", "equipment", "main-hand", stackable: false, priceKind: "weapon", classes: ["knight"])),
        ("helm", Def("盔", "equipment", "head", stackable: false, priceKind: "armor", classes: ["knight"])),
        ("potion", Def("药水", "consumable", use: new ItemUse { Kind = "recover-health" })),
        ("rune", Def("符文", "consumable", use: new ItemUse { Kind = "dungeon" })),
        ("trash", Def("杂物", "material")));

    var bag = new Bag(() => { }, catalog, "knight", seed: false, new EquipmentBagRules(
        new CharacterStatsCatalog(), catalog, "knight"));

    bag.Add("blade");
    bag.Add("helm");
    bag.Add("potion");
    bag.Add("rune");
    bag.Add("trash");

    var bladeId = bag.Entries.First(entry => entry.ItemId == "blade").Id;
    var actions = BagMenu.Build(bag, bag.Get(bladeId)!, "player", "knight", 1, slot => slot);
    run.Equal("装备", actions[0].Label);
    run.Equal("main-hand", actions[0].Slot);
    run.False(actions[0].Disabled, "等级够、槽空着，装备是可点的");

    bag.Equip(bladeId, "main-hand", "player", "knight", 1);
    var worn = BagMenu.Build(bag, bag.Get(bladeId)!, "player", "knight", 1, slot => slot);
    run.Equal(1, worn.Count, "穿着的只给卸下一个动作");
    run.Equal(BagActionKind.Unequip, worn[0].Kind);

    var potionId = bag.Entries.First(entry => entry.ItemId == "potion").Id;
    var potion = BagMenu.Build(bag, bag.Get(potionId)!, "player", "knight", 1, slot => slot);
    run.Equal("使用", potion[0].Label);
    run.False(potion[0].Disabled, "回血药水可用");
    run.Equal(BagActionKind.Discard, potion[^1].Kind);

    var runeId = bag.Entries.First(entry => entry.ItemId == "rune").Id;
    var rune = BagMenu.Build(bag, bag.Get(runeId)!, "player", "knight", 1, slot => slot);
    run.True(rune[0].Disabled, "地牢符文还没实现，使用置灰");

    // 不可丢的杂物：既不装备也不使用，只有丢弃一条（可丢）。
    var trashId = bag.Entries.First(entry => entry.ItemId == "trash").Id;
    var trash = BagMenu.Build(bag, bag.Get(trashId)!, "player", "knight", 1, slot => slot);
    run.Equal(1, trash.Count);
    run.Equal(BagActionKind.Discard, trash[0].Kind);
});

run.Case("背包：说明面板按盖住的那一栏取宽，子件跟着平移", () =>
{
    var full = BagLayouts.Description;
    run.Equal(442f, full.Width, "设计宽度 442");
    run.Equal(358f, full.Scroll.Width);

    var bag = BagLayouts.DescriptionPanel(BagLayouts.BagDescriptionWidth);
    run.Equal(384f, bag.Width);
    run.Equal(300f, bag.Scroll.Width, "滚动区跟着收窄");
    run.Equal(323f, bag.Close.X, "关闭钮贴回右边缘");
    run.Equal(319f, bag.Menu.Width, "按钮行跟着收窄");

    var shelf = BagLayouts.DescriptionPanel(462f);
    run.Equal(462f, shelf.Width);
    run.Equal(378f, shelf.Scroll.Width);
    run.Equal(401f, shelf.Close.X);
    run.Equal(397f, shelf.Menu.Width);

    run.Equal(85f, full.LeftX);
    run.Equal(496f, full.RightX);
});

run.Case("商店：两栏版面与分类列落点对得上商店版式", () =>
{
    var classified = ShopLayouts.Create(true);
    run.Equal(18f, classified.LeftFrame.X, "分类店左框 x");
    run.Equal(462f, classified.LeftFrame.Width, "分类店左框宽");
    run.Equal(484f, classified.BagFrame.X, "背包栏 x");
    run.Equal(431f, classified.BagFrame.Height, "两栏同高");
    run.Equal(131f, classified.Title.X - classified.LeftFrame.X, "分类页标题框固定 +131");
    run.Equal(-35f, classified.Title.Y, "标题框压在顶边上方 35");
    run.Equal(117f, classified.Shelf.X - classified.LeftFrame.X, "分类店货架让开分类列");
    run.Equal(86f, classified.Shelf.Y, "分类店货架纵向");
    run.Equal(873f, classified.SideButtons.X, "六格分类列 = 右框 +389");
    run.Equal(518f, classified.Bag.X, "背包网格 = 右框 +34");
    run.Equal(27f, classified.Bag.Y, "背包网格纵向 +27");
    run.Equal(518f, classified.Money.X, "金额与网格左对齐");
    run.Equal(365f, classified.Money.Y, "金额纵向 +365");
    run.Equal(67f, classified.Badge.X, "数量角标右对齐于 +67");
    run.Equal(93f, classified.CategoryColumn.Divider.X - classified.LeftFrame.X, "分类列竖条 +93");
    run.Equal(88f, classified.Category.Button.Width, "分类按钮宽");
    run.Equal(69f, classified.Category.Button.Stride, "分类按钮紧排");

    var plain = ShopLayouts.Create(false);
    run.Equal(85f, plain.LeftFrame.X, "一般商店左框 x");
    run.Equal(395f, plain.LeftFrame.Width, "一般商店左框宽");
    run.Equal(138f, plain.Title.X, "288 宽标题框居中（395−288 除二截断）");
    run.Equal(36f, plain.Shelf.X - plain.LeftFrame.X, "一般商店货架 +36");
    run.Equal(80f, plain.Shelf.Y, "一般商店货架纵向 +80");
});

run.Case("商店：弹层框高随行数长，按钮始终 125×55", () =>
{
    var quantity = TradeLayouts.Quantity;
    run.Equal(450f, quantity.Width, "数量框宽");
    run.Equal(350f, quantity.Height, "数量框高");
    run.Equal(45f, quantity.Button.Left, "确定在左");
    run.Equal(280f, quantity.Button.Right, "取消在右");
    run.Equal(275f, quantity.Button.Y, "按钮行纵向");
    run.Equal(233f, quantity.Count.X, "数量文字左对齐于 +233");
    run.Equal(209f, quantity.Step.Right, "右箭头偏移");

    var one = TradeLayouts.Message(1, true);
    run.Equal(350f, one.Height, "一行加金额仍是 350");
    run.Equal(95f, one.Text.Y, "一行时正文起点");
    run.Equal(141f, one.TextPlate.Y, "金额紧接正文");

    var many = TradeLayouts.Message(6, true);
    run.Equal(365f, many.Height, "六行加金额：250 + 115");
    run.Equal(35f, many.Text.Y, "撑满时正文顶到黑底上沿");
});

run.Case("角色卡：面板按背包版本与等级缓存，三者任一变化才重算", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);

    var first = hero.Panel;
    run.True(ReferenceEquals(hero.Panel, first), "背包与等级都没变时复用同一份面板");

    var material = items.Items.First(pair => !pair.Value.IsPickup && pair.Value.Category == "material").Key;
    hero.Bag.Add(material);
    run.True(!ReferenceEquals(hero.Panel, first), "背包变化后重新计算");

    var afterBag = hero.Panel;
    hero.AddExperience(stats.ExperienceToNext(1) ?? 0);
    run.True(!ReferenceEquals(hero.Panel, afterBag), "升级后重新计算");

    var afterLevel = hero.Panel;
    hero.Allocate("strength", 1);
    run.True(!ReferenceEquals(hero.Panel, afterLevel), "分配属性后重新计算");

    var afterAllocation = hero.Panel;
    var state = hero.CaptureState();
    state.Allocation["strength"] += 1;
    hero.RestoreState(state);
    run.True(!ReferenceEquals(hero.Panel, afterAllocation), "读档恢复属性后重新计算");
});

run.Case("角色卡：经验跨过原生门槛，升级回满，满级停住", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var threshold = stats.ExperienceToNext(1) ?? 0;

    hero.SetHealth(1);
    hero.SetMana(2);
    run.Equal(0, hero.AddExperience(threshold - 1), "差一点不升级");
    run.Equal(1, hero.Level);
    run.Equal(threshold - 1, hero.Player.Experience);
    run.Equal(1, hero.Player.Health, "没升级就不回血");
    run.Equal(2, hero.Player.Mana);

    run.Equal(1, hero.AddExperience(1), "补上最后一点升一级");
    run.Equal(2, hero.Level);
    run.Equal(0, hero.Player.Experience);
    run.Equal(hero.Player.MaxHealth, hero.Player.Health, "升级回满生命");
    run.Equal(hero.Player.MaxMana, hero.Player.Mana, "升级回满魔法");

    var second = stats.ExperienceToNext(2) ?? 0;
    var third = stats.ExperienceToNext(3) ?? 0;
    run.Equal(2, hero.AddExperience(second + third + 7), "一次跨两级");
    run.Equal(4, hero.Level);
    run.Equal(7, hero.Player.Experience);

    run.Throws(() => hero.AddExperience(-1), "经验奖励无效");
    hero.AddExperience(int.MaxValue);
    run.Equal(stats.MaxLevel, hero.Level, "满级");
    run.Equal(0, hero.AddExperience(100), "满级不再吃经验");
    run.Equal(0, hero.Player.Experience, "满级经验清零");
});

run.Case("角色卡：能力点与技能点的原生余额，初始化只清自己那份", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var initial = hero.Panel;

    run.Equal(0, hero.StatPoints, "1 级没有能力点");
    run.Equal(0, hero.SkillPoints, "1 级没有技能点");
    run.Throws(() => hero.Allocate("strength", 1), "没有可分配的属性点");

    var next = hero.Player.NextLevelExperience;
    hero.AddExperience(next);
    run.Equal(stats.StatPointsPerLevel, hero.StatPoints, "升级给能力点");
    run.Equal(stats.SkillPointsPerLevel, hero.SkillPoints, "升级给技能点");

    hero.Allocate("vitality", 1);
    run.Equal(initial.MaxHealth + 72 + 80, hero.Panel.MaxHealth, "每级 72 点生命、每点体力 80 点");
    run.Equal(stats.StatPointsPerLevel - 1, hero.StatPoints);
    hero.Allocate("vitality", -1);
    run.Equal(stats.StatPointsPerLevel, hero.StatPoints, "减回去还回点数");
    run.Throws(() => hero.Allocate("vitality", -1), "该属性没有已分配的点数");

    hero.AddExperience(int.MaxValue);
    run.Equal(stats.MaxLevel, hero.Level);
    run.Equal((stats.MaxLevel - 1) * stats.StatPointsPerLevel, hero.StatPoints, "满级能力点");
    run.Equal((stats.MaxLevel - 1) * stats.SkillPointsPerLevel, hero.SkillPoints, "满级技能点");
    run.Equal(0, hero.Player.NextLevelExperience, "满级没有下一级门槛");

    hero.Allocate("intelligence", 1);
    hero.ResetAttributes();
    run.Equal((stats.MaxLevel - 1) * stats.StatPointsPerLevel, hero.StatPoints, "初始化还回全部点数");
    run.Equal(0, PrimaryAttributeMath.Total(hero.Allocated), "已分配归零");
    run.Equal(hero.Player.MaxHealth, hero.Player.Health, "初始化后按新上限回血");
});

run.Case("角色卡：战斗期资源上限只覆盖读数，不清零也不提前夹取", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var baseline = hero.Player;

    hero.SetHealth(baseline.MaxHealth - 100);
    hero.SetMana(baseline.MaxMana - 20);
    hero.SetCombatResourceLimits(baseline.MaxHealth + 300, baseline.MaxMana + 40);
    run.Equal(baseline.MaxHealth + 300, hero.Player.MaxHealth);
    run.Equal(baseline.MaxHealth - 100, hero.Player.Health, "提高上限不自动回血");
    run.Equal(baseline.MaxMana + 40, hero.Player.MaxMana);
    run.Equal(baseline.MaxMana - 20, hero.Player.Mana);

    hero.SetHealth(baseline.MaxHealth + 300);
    hero.SetMana(baseline.MaxMana + 40);
    hero.SetCombatResourceLimits(baseline.MaxHealth, baseline.MaxMana);
    run.Equal(baseline.MaxHealth, hero.Player.MaxHealth);
    run.Equal(baseline.MaxHealth + 300, hero.Player.Health, "降低上限也不立刻夹取");
    run.Equal(baseline.MaxMana, hero.Player.MaxMana);
    run.Equal(baseline.MaxMana + 40, hero.Player.Mana);

    hero.SetCombatResourceCapacity("maxHealth", baseline.MaxHealth);
    run.Equal(baseline.MaxHealth, hero.Player.Health, "单项改上限才夹取这一项");
    run.Equal(baseline.MaxMana + 40, hero.Player.Mana);

    hero.SetCombatResourceCapacity("maxMana", baseline.MaxMana);
    run.Equal(baseline.MaxMana, hero.Player.Mana);

    hero.SetCombatResourceCapacity("maxHealth", 0);
    run.Equal(0, hero.Player.MaxHealth);
    run.Equal(0, hero.Player.Health, "上限归零时生命跟着归零");
    hero.SetCombatResourceLimits(baseline.MaxHealth, baseline.MaxMana);
    run.Equal(0, hero.Player.Health, "恢复上限不会把人复活");
    hero.SetHealth(25);
    run.Equal(25, hero.Player.Health);
});

run.Case("角色卡：资源写入口只动自己那一项，负数技能消耗按余额拒绝", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var baseline = hero.Player;

    run.True(hero.SpendMana(-50), "负消耗通过");
    run.Equal(baseline.MaxMana + 50, hero.Player.Mana);
    hero.SetHealth(baseline.MaxHealth - 50);

    hero.SetCombatResourceLimits(baseline.MaxHealth - 100, baseline.MaxMana);
    hero.SetMana(20);
    run.Equal(baseline.MaxHealth - 50, hero.Player.Health, "改魔法不动生命");

    hero.SetCombatResourceCapacity("maxHealth", 0);
    run.Equal(0, hero.Player.MaxHealth);
    run.Equal(0, hero.Player.Health);
    run.Equal(20, hero.Player.Mana);
    hero.SetCombatResourceLimits(baseline.MaxHealth, baseline.MaxMana);
    run.Equal(0, hero.Player.Health, "恢复上限不会把人复活");
    hero.SetHealth(25);
    run.Equal(25, hero.Player.Health);
});

run.Case("角色卡：提高体力后再同步战斗上限，不会把倒下的人复活", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);

    hero.AddExperience(stats.ExperienceToNext(1) ?? 0);
    hero.SetCombatResourceLimits(hero.Panel.MaxHealth, hero.Panel.MaxMana);
    var previousMaximum = hero.Player.MaxHealth;

    hero.SetHealth(0);
    hero.Allocate("vitality", 1);
    hero.SetCombatResourceLimits(hero.Panel.MaxHealth, hero.Panel.MaxMana);
    run.Equal(previousMaximum + 80, hero.Player.MaxHealth, "体力 +1 带来 80 点生命上限");
    run.Equal(0, hero.Player.Health, "仍是倒下的状态");

    hero.ClearCombatResourceLimits();
    run.Equal(0, hero.Player.Health, "清掉战斗上限也不复活");
});

run.Case("角色卡：穿脱装备按等级判定，卸下后实例的掷骰原样保留", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("warrior", stats, items);
    var bag = hero.Bag;

    foreach (var (id, definition) in items.Items)
        if (!definition.IsPickup) bag.Add(id);

    var high = bag.Entries.First(entry =>
    {
        var definition = items.Items[entry.ItemId];
        return definition.Equipment is { DualWield: true, Locked: false } equipment &&
               equipment.Allows(hero.ClassId) && definition.Level > 90;
    });
    var low = bag.Entries.First(entry =>
    {
        var definition = items.Items[entry.ItemId];
        return definition.Equipment is { DualWield: true, Locked: false } equipment &&
               equipment.Allows(hero.ClassId) && definition.Level == 1;
    });

    var before = bag.EquipmentFor("player");
    run.Throws(() => bag.Equip(high.Id, "main-hand"), "需要等级");
    run.Equal(before.Count, bag.EquipmentFor("player").Count, "被拒后装备表不变");

    hero.AddExperience(int.MaxValue);
    bag.Equip(high.Id, "main-hand");
    bag.Equip(low.Id, "off-hand");
    var rolled = bag.Get(high.Id)!;
    bag.Unequip("main-hand");

    run.True(!bag.EquipmentFor("player").ContainsKey("main-hand"), "主手空出来");
    run.True(!bag.EquipmentFor("player").ContainsKey("off-hand"), "双持副手跟着卸下");
    run.Equal(rolled.Numbers!.MagicPercent, bag.Get(high.Id)!.Numbers!.MagicPercent, "掷骰原样保留");
    run.Equal(rolled.Numbers.EnhancementUses, bag.Get(high.Id)!.Numbers!.EnhancementUses);
    run.True(bag.Entries.Any(entry => entry.Id == low.Id), "副手回到背包");
    run.Equal(hero.Panel.MaxHealth, hero.Player.Health);
});

run.Case("角色卡：固定掷骰可复现，强化改真实攻击，宝石词条只并一次", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var replica = new BattleCharacter("dark-knight", stats, items);

    // 这条用例先把全部非拾取物品塞进背包，再挑卷轴与宝石。
    // 掷骰的对照要在「两边都还是开局物品」时做，否则格位次序对不上。
    var replicaEntries = replica.Bag.Entries;
    var heroEntries = hero.Bag.Entries;
    run.Equal(replicaEntries.Count, heroEntries.Count, "同职业开局背包格数相同");
    for (var index = 0; index < replicaEntries.Count; index += 1)
    {
        run.Equal(replicaEntries[index].ItemId, heroEntries[index].ItemId, "开局物品次序一致");
        run.Equal(replicaEntries[index].Numbers?.MagicPercent, heroEntries[index].Numbers?.MagicPercent, "掷骰一致");
    }

    foreach (var (id, definition) in items.Items)
        if (!definition.IsPickup) hero.Bag.Add(id);

    var bag = hero.Bag;
    var weaponId = bag.WornIds["main-hand"];
    var weapon = bag.Get(weaponId)!;
    run.Equal(3, weapon.Numbers!.EnhancementUses, "长剑开局三次强化耐久");

    var scroll = bag.Entries.First(entry =>
    {
        var use = items.Items[entry.ItemId].Use;
        return use is { Kind: "enchant", Target: "weapon", MaxLevel: 33 };
    });

    var before = hero.Panel;
    var power = EquipmentPowerCalculator.Power(stats.GetEquipment(weapon.ItemId), weapon);
    bag.Use(scroll.Id, weaponId, null, () => 0.99);
    var enhanced = bag.Get(weaponId)!;
    run.Equal(1, enhanced.Enhancement, "强化到 +1");
    run.Equal(2, enhanced.Numbers!.EnhancementUses, "耐久用掉一次");

    var newPower = EquipmentPowerCalculator.Power(stats.GetEquipment(weapon.ItemId), enhanced);
    run.Equal(power.Attack + 1, newPower.Attack, "长剑每级 +1 攻");
    run.Equal(before.PhysicalAttack + 1, hero.Panel.PhysicalAttack, "面板攻击跟着涨");

    var mismatch = bag.Entries.First(entry =>
    {
        var use = items.Items[entry.ItemId].Use;
        return use is { Kind: "enchant", Target: "weapon", MaxLevel: 66 };
    });
    run.Equal("与一般的强化卷轴不同。", bag.TargetProblem(mismatch.Id, weaponId), "换一张不同档的卷轴被拒");

    var view = bag.Get(weaponId)!;
    view.Numbers!.Modifiers.Add(new Modifier { Stat = "strength", Value = 999 });
    run.Equal(enhanced.Numbers.Modifiers.Count, bag.Get(weaponId)!.Numbers!.Modifiers.Count, "副本改动不回写");

    var socketed = bag.Entries.First(entry =>
    {
        var definition = items.Items[entry.ItemId];
        return definition.Equipment is { Locked: false } equipment && equipment.Allows(hero.ClassId) &&
               (entry.Numbers?.Sockets ?? 0) > 0;
    });
    var gem = bag.Entries.First(entry => items.Items[entry.ItemId].Use?.Kind == "socket");
    var modifier = gem.Numbers!.Modifiers[0];
    bag.Use(gem.Id, socketed.Id);
    var socketedAfter = bag.Get(socketed.Id)!;
    run.Equal(1, socketedAfter.Numbers!.GemModifiers.Count, "宝石词条并进装备");
    run.Equal(modifier.Stat, socketedAfter.Numbers.GemModifiers[0].Stat);
    run.Equal(modifier.Value, socketedAfter.Numbers.GemModifiers[0].Value);
    run.Equal(1, socketedAfter.Gems.Count, "宝石记在实例上");

    run.Throws(() => bag.Use(gem.Id, socketed.Id), "物品已不在背包中");
    hero.Recover();
    run.Equal(1, bag.Get(socketed.Id)!.Numbers!.GemModifiers.Count, "重算面板不会重复并词条");
});

run.Case("角色卡：混沌卷轴先判成败、不消耗耐久，普通卷轴第二次判定再升一级", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var bag = hero.Bag;

    foreach (var (id, definition) in items.Items)
        if (!definition.IsPickup) bag.Add(id);

    run.True(stats.Enchantments.ContainsKey("chaos-weapon-scroll"), "混沌武器卷轴有规则");
    run.True(stats.Enchantments.ContainsKey("chaos-armor-scroll"), "混沌防具卷轴有规则");
    run.Equal(300, stats.EnhancementRuleFor("sp-weapon-enhance-scroll", "dagger")?.BonusChance,
        "特级卷轴的第二次判定阈值 +300");
    run.True(stats.EnhancementRuleFor("novice-weapon-scroll", "dagger")?.BonusChance is null,
        "普通卷轴没有加成");

    var chaos = bag.Entries.First(entry =>
    {
        var use = items.Items[entry.ItemId].Use;
        return use is { Kind: "enchant", Target: "weapon", RequiresEnhanced: true };
    });
    var weaponId = bag.WornIds["main-hand"];

    run.Equal("至少需要强化+1以上。", bag.TargetProblem(chaos.Id, weaponId), "混沌卷轴要求先强化过");

    void AtLevel(int level)
    {
        var state = bag.CaptureState();
        var target = state.Instances.First(entry => entry.Id == weaponId);
        target.Enhancement = level;
        target.Numbers!.EnhancementUses = 0;
        bag.RestoreState(state);
    }

    AtLevel(20);
    bag.Use(chaos.Id, weaponId, null, () => 0.999);
    run.Equal(19, bag.Get(weaponId)!.Enhancement, "混沌失败降一级");
    run.Equal(0, bag.Get(weaponId)!.Numbers!.EnhancementUses, "混沌卷轴不消耗耐久");

    bag.Add(chaos.ItemId);
    var second = bag.Entries.First(entry => entry.ItemId == chaos.ItemId && entry.Id != chaos.Id).Id;

    AtLevel(1);
    bag.Use(second, weaponId, null, () => 0);
    run.Equal(3, bag.Get(weaponId)!.Enhancement, "混沌成功升两级");
    run.Equal(0, bag.Get(weaponId)!.Numbers!.EnhancementUses, "仍不消耗耐久");
});

run.Case("角色卡：法杖与宝珠的魔攻走掷出的武器百分比", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("warlock", stats, items);
    var weapon = hero.Bag.WornInstances["main-hand"];

    run.Equal(130, weapon.Numbers!.MagicPercent, "术士法杖的魔法占比掷到 130");
    var power = EquipmentPowerCalculator.Power(stats.GetEquipment(weapon.ItemId), weapon);
    run.True(power.MagicAttack > 0, "法杖有魔攻分量");

    var panel = CharacterPanel.Calculate(stats, items, "warlock", 1, null,
        new Dictionary<string, ItemInstance> { ["main-hand"] = weapon });
    run.Equal(power.MagicAttack + 9 + 6, panel.MagicAttack, "法杖 600‰ 智力 + 600‰ 精力");

    var enhanced = weapon.DeepClone();
    enhanced.Enhancement = 10;
    enhanced.Numbers!.EnhancementRule = new EnhancementRule { Step = 7, Threshold = 10, Extra = 1 };
    run.Equal(power.Attack + 71,
        EquipmentPowerCalculator.Power(stats.GetEquipment(weapon.ItemId), enhanced).Attack,
        "十级强化：7×10 + 超过阈值那一级再 +1");
});

run.Case("伙伴：招募掷骰、十八格名册、上阵两人与放逐", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");

    hero.Bag.Add("mercenary-emblem-assassin");
    var badge = hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-assassin");
    var first = party.Recruit(badge.Id, () => 0).Member;

    run.Equal("assassin", first.ClassId, "徽章限定职业");
    run.Equal(0, hero.Bag.Count("mercenary-emblem-assassin"), "徽章被收走");
    run.Equal("player", string.Join(",", party.ActiveIds), "招募不等于上阵");
    run.Equal(18, party.Snapshot().RosterSlots.Count, "十八格名册");

    party.DeployFirstAvailable(first.Id);
    run.Equal("player," + first.Id, string.Join(",", party.ActiveIds), "第一个上阵位");
    run.True(hero.Bag.EquipmentFor(first.Id).ContainsKey("main-hand"), "伙伴穿上职业初始武器");

    hero.Bag.Add("hero-emblem", 2);
    var second = party.Recruit(hero.Bag.Entries.First(entry => entry.ItemId == "hero-emblem").Id, () => 0).Member;
    run.True(second.Definition.Elite, "英雄徽章招到精英");
    party.DeployFirstAvailable(second.Id);

    var third = party.Recruit(hero.Bag.Entries.First(entry => entry.ItemId == "hero-emblem").Id, () => .2).Member;
    run.Equal(3, party.ActiveIds.Count, "上阵位只有两个");
    run.Equal(third.Id, party.Snapshot().Reserves[0], "第三个留在候补");

    party.Reserve(first.Id);
    party.DeployFirstAvailable(third.Id);
    run.Equal("player," + third.Id + "," + second.Id, string.Join(",", party.ActiveIds), "换人上阵");

    run.True(party.SwitchControl(second.Id), "切到第二位上阵伙伴");
    run.Throws(() => party.Dismiss(second.Id), "请先将佣兵移出队伍");
    party.Reserve(second.Id);
    party.Dismiss(second.Id);
    run.Equal("player", party.ControlledId, "放逐后操控权交回主角");
    run.Equal(0, hero.Bag.EquipmentFor(second.Id).Count, "放逐时装备一并销毁");
    run.True(party.Get(second.Id) is null, "名册里不再有这个人");
});

run.Case("伙伴：存活的上阵成员吃经验，复活卷轴回一成资源", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    hero.Bag.Add("mercenary-emblem-priest");
    var member = party.Recruit(hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-priest").Id, () => 0).Member;
    party.DeployFirstAvailable(member.Id);

    member.SetHealth(0);
    run.True(!party.SwitchControl(member.Id), "倒下的伙伴切不过去");

    var before = member.Player.Experience;
    party.AddExperience(25);
    run.Equal(before, member.Player.Experience, "倒下的伙伴不吃经验");

    hero.Bag.Add("resurrection-scroll");
    var scroll = hero.Bag.Entries.First(entry => entry.ItemId == "resurrection-scroll");
    run.Equal("revive", party.Revive(member.Id, scroll.Id).Kind);
    run.Equal((int)Math.Floor(member.Player.MaxHealth * .1), member.Player.Health, "普通卷轴回一成生命");
    run.Equal((int)Math.Floor(member.Player.MaxMana * .1), member.Player.Mana, "魔法同样回一成");
    run.Equal(0, hero.Bag.Count("resurrection-scroll"), "卷轴被消耗");

    party.AddExperience(25);
    run.True(member.Player.Experience > before, "复活之后照常吃经验");
});

run.Case("伙伴：剧情入队不挤掉已上阵的人，离队返回 true", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (_, party) = ReadParty(root, "dark-knight");
    var story = party.Catalog.Definitions.Values.First(definition => definition.SourceOrDefault == "story");

    var member = party.JoinStory(story.Id);
    run.True(ReferenceEquals(party.JoinStory(story.Id), member), "重复加入返回同一个人");
    run.True(party.ActiveIds.Contains(member.Id), "有空位就上阵");
    run.True(party.LeaveStory(story.Id), "剧情伙伴可以离队");
    run.True(party.Get(member.Id) is null);
});

run.Case("伙伴：名册满员时拒绝招募且不吞徽章，放逐后补进原格", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    var members = new List<CompanionMember>();
    for (var index = 0; index < PartyRoster.MercenarySlotCount; index += 1)
    {
        hero.Bag.Add("mercenary-emblem-black-knight");
        var badge = hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight");
        members.Add(party.Recruit(badge.Id, () => 0).Member);
    }

    var slots = party.Snapshot().RosterSlots;
    run.Equal(PartyRoster.MercenarySlotCount, slots.Count, "十八格全开");
    for (var index = 0; index < members.Count; index += 1)
        run.Equal(members[index].Id, slots[index], "按入队次序占格");

    hero.Bag.Add("mercenary-emblem-black-knight");
    var extra = hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight");
    run.Throws(() => party.Recruit(extra.Id, () => 0), "佣兵名册已满");
    run.Equal(1, hero.Bag.Count("mercenary-emblem-black-knight"), "被拒时徽章还在");

    party.Dismiss(members[4].Id);
    var replacement = party.Recruit(extra.Id, () => 0).Member;
    run.Equal(replacement.Id, party.Snapshot().RosterSlots[4], "补进空出来的那一格");
});

run.Case("伙伴：招募按原生品质权重与属性骰掷数值，技能等级按角色等级成长", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");

    CompanionMember Recruit(string emblem, double roll)
    {
        hero.Bag.Add(emblem);
        var badge = hero.Bag.Entries.First(entry => entry.ItemId == emblem);
        return party.Recruit(badge.Id, () => roll).Member;
    }

    var common = Recruit("mercenary-emblem-black-knight", 0);
    var trained = Recruit("mercenary-emblem-black-knight", .99);
    var rare = Recruit("hero-emblem", 0);

    run.Equal(0, common.Definition.Quality ?? 0, "普通徽章招普通品质");
    run.Equal(1, trained.Definition.Quality ?? 0, "高随机值招到训练品质");
    run.Equal(2, rare.Definition.Quality ?? 0, "英雄徽章招到稀有品质");

    run.Equal(10, common.Generated.BaseAttributes.Strength, "五维来自属性骰，不是职业基础值");
    run.True(common.Generated.BaseAttributes.Strength != 13, "与职业基础五维不同");
    run.Equal(1, common.Snapshot().SkillLevels["dark-strike"], "第一个技能天生 1 级");
    run.Equal(0, common.Snapshot().SkillLevels.GetValueOrDefault("firestorm"), "没抽到的技能 0 级");
    run.True(common.Generated.SkillCaps.Count < 15, "只抽到一部分技能");
    run.Equal(2, rare.Generated.GroupSkills.Count, "稀有品质抽两条佣兵团技能");

    var skillId = "dark-strike";
    var thresholds = party.Catalog.Generation.Training[skillId].RequiredLevels;
    var cap = common.Generated.SkillCaps[skillId];

    common.Skills.UpdateLevel(thresholds[1] - 1);
    run.Equal(1, common.Skills.SkillLevels()[skillId], "没到第二个门槛还是 1 级");
    common.Skills.UpdateLevel(thresholds[1]);
    run.Equal(2, common.Skills.SkillLevels()[skillId], "过门槛升一级");
    common.Skills.UpdateLevel(105);
    run.Equal(cap, common.Skills.SkillLevels()[skillId], "封顶在掷出的上限");

    // 伙伴技能不能手改：写入只能等于成长算出来的那一份。
    var battle = ReadSkills(root);
    var draft = common.Skills.Draft(battle, common.ClassId);
    draft.Levels["firestorm"] = 9;
    run.Throws(() => common.Skills.Save(battle, common.ClassId, draft), "佣兵技能随角色等级成长");
});

run.Case("伙伴：招募池只收显式标了品质的定义", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");

    var survivors = party.Catalog.Definitions
        .Where(pair => pair.Value.ClassId != "dark-knight")
        .ToDictionary(pair => pair.Key, pair => pair.Value);
    survivors["no-quality"] = new CompanionDefinition
    {
        Id = "no-quality", Name = "无名佣兵", Actor = "mercenary-black-knight",
        ClassId = "dark-knight", Source = "mercenary",
    };
    party.Catalog.Definitions.Clear();
    foreach (var pair in survivors) party.Catalog.Definitions[pair.Key] = pair.Value;

    hero.Bag.Add("mercenary-emblem-black-knight");
    var badge = hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight");
    run.Throws(() => party.Recruit(badge.Id, () => 0), "招募角色数据缺失");
});

run.Case("伙伴：候补也提供佣兵团技能，重复不叠加，全部放逐后失效", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");

    CompanionMember Recruit()
    {
        hero.Bag.Add("mercenary-emblem-black-knight");
        var badge = hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight");
        return party.Recruit(badge.Id, () => 0).Member;
    }

    var first = Recruit();
    var second = Recruit();
    run.Equal(100, party.GroupModifiers().GetValueOrDefault("low-health-resistance"), "黑暗骑士的佣兵团技能");

    party.AddExperience(25);
    run.Equal(25, first.Player.Experience, "候补也吃经验");
    run.Equal(25, second.Player.Experience);

    party.Dismiss(first.Id);
    run.Equal(100, party.GroupModifiers().GetValueOrDefault("low-health-resistance"), "还有一个就还在");
    party.Dismiss(second.Id);
    run.True(!party.GroupModifiers().ContainsKey("low-health-resistance"), "全部放逐后失效");
});

run.Case("伙伴：上阵位不隐式替换，倒下时环形换人，全灭才返回 true", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");

    CompanionMember Recruit()
    {
        hero.Bag.Add("mercenary-emblem-black-knight");
        var badge = hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight");
        return party.Recruit(badge.Id, () => 0).Member;
    }

    var first = Recruit();
    var second = Recruit();
    var third = Recruit();
    party.DeployFirstAvailable(first.Id);
    party.DeployFirstAvailable(second.Id);

    run.Throws(() => party.DeployFirstAvailable(third.Id), "上阵位置已满");
    run.Throws(() => party.Deploy(third.Id, 1), "上阵位置已占用");

    party.SwitchControl(first.Id);
    first.SetHealth(0);
    run.True(!party.OnDefeat(first.Id), "还有活着的人");
    run.Equal(second.Id, party.ControlledId, "操控权交给下一个活着的上阵位");

    run.Throws(() => party.Reserve(first.Id), "阵亡队员无法移出队伍");
    second.SetHealth(0);
    party.OnDefeat(second.Id);
    run.Equal("player", party.ControlledId, "回到主角");
    run.Equal("player", string.Join(",", party.Snapshot().Living), "只剩主角活着");

    hero.SetHealth(0);
    run.True(party.OnDefeat("player"), "全灭");
    run.Equal(0, party.Snapshot().Living.Count);
});

run.Case("伙伴：复活圣文要全队阵亡，改良卷轴回满", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    hero.Bag.Add("mercenary-emblem-black-knight");
    var member = party.Recruit(hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight").Id, () => 0).Member;
    party.DeployFirstAvailable(member.Id);

    hero.Bag.Add("resurrection-prayer");
    var prayer = hero.Bag.Entries.First(entry => entry.ItemId == "resurrection-prayer");

    member.SetHealth(0);
    run.Throws(() => party.Revive(member.Id, prayer.Id), "复活圣文只能在全队阵亡时使用");
    run.Equal(1, hero.Bag.Count("resurrection-prayer"), "被拒时不消耗");

    hero.SetHealth(0);
    party.Revive("player", prayer.Id);
    run.Equal(hero.Player.MaxHealth, hero.Player.Health, "圣文回满主角生命");
    run.Equal(member.Player.MaxHealth, member.Player.Health, "圣文回满伙伴生命");
    run.Equal(member.Player.MaxMana, member.Player.Mana, "魔法同样回满");
    run.Equal(0, hero.Bag.Count("resurrection-prayer"), "圣文被消耗");
    run.Throws(() => party.Revive("player", prayer.Id), "复活道具不可用");

    member.SetHealth(0);
    hero.Bag.Add("improved-resurrection-scroll");
    party.Revive(member.Id, hero.Bag.Entries.First(entry => entry.ItemId == "improved-resurrection-scroll").Id);
    run.Equal(member.Player.MaxHealth, member.Player.Health, "改良卷轴回满生命");
    run.Equal(member.Player.MaxMana, member.Player.Mana, "改良卷轴回满魔法");
});

run.Case("伙伴：存档往返保留战斗上限与反击档位，不重复发放资源", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    hero.Bag.Add("mercenary-emblem-black-knight");
    var member = party.Recruit(hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight").Id, () => 0).Member;

    var maxHealth = member.Player.MaxHealth + 500;
    member.SetCombatResourceLimits(maxHealth, 400);
    member.SetHealth(maxHealth - 20);

    var state = party.CaptureState();
    party.RestoreState(state);
    var restored = party.Get(member.Id)!;
    restored.SetCombatResourceLimits(maxHealth, 400);
    run.Equal(maxHealth - 20, restored.Player.Health, "战斗上限不会被重复加上");
    run.Equal(maxHealth, restored.Player.MaxHealth);

    run.Equal(2, party.Counterattack("player"), "主角默认反击启用");
    run.Equal(2, party.Counterattack(member.Id), "伙伴默认反击启用");
    party.SetCounterattack("player", 1);
    party.SetCounterattack(member.Id, 0);
    run.Throws(() => party.SetCounterattack(member.Id, 3), "反击档位需要是 0–2");

    var again = party.CaptureState();
    party.RestoreState(again);
    run.Equal(1, party.Counterattack("player"), "主角档位跨存档保留");
    run.Equal(0, party.Counterattack(member.Id), "伙伴档位跨存档保留");
    run.Equal(0, party.Counterattack("missing"), "不存在的成员是 0");
});

run.Case("伙伴：资源投影只动自己那一项，上限归零时生命跟着归零", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    hero.Bag.Add("mercenary-emblem-black-knight");
    var member = party.Recruit(hero.Bag.Entries.First(entry => entry.ItemId == "mercenary-emblem-black-knight").Id, () => 0).Member;
    var baseline = member.Player;

    member.SetHealth(baseline.MaxHealth - 100);
    member.SpendMana(-40);
    member.SetCombatResourceLimits(baseline.MaxHealth + 300, baseline.MaxMana);
    run.Equal(baseline.MaxHealth - 100, member.Player.Health, "提高上限不回血");
    run.Equal(baseline.MaxMana + 40, member.Player.Mana);

    member.SetHealth(baseline.MaxHealth + 300);
    member.SetCombatResourceLimits(baseline.MaxHealth, baseline.MaxMana);
    run.Equal(baseline.MaxHealth + 300, member.Player.Health, "降低上限不立刻夹取");

    member.SetMana(20);
    run.Equal(baseline.MaxHealth + 300, member.Player.Health, "改魔法不动生命");
    member.SpendMana(-300);
    member.SetCombatResourceCapacity("maxHealth", baseline.MaxHealth);
    run.Equal(baseline.MaxHealth, member.Player.Health, "单项改上限才夹取");
    run.Equal(320, member.Player.Mana);

    member.SetCombatResourceCapacity("maxMana", baseline.MaxMana);
    run.Equal(baseline.MaxMana, member.Player.Mana);
    member.SetCombatResourceCapacity("maxHealth", 0);
    run.Equal(0, member.Player.MaxHealth);
    run.Equal(0, member.Player.Health);
    member.SetCombatResourceLimits(baseline.MaxHealth, baseline.MaxMana);
    run.Equal(0, member.Player.Health, "恢复上限不复活");
    member.SetHealth(25);
    run.Equal(25, member.Player.Health);
});

run.Case("伙伴：非法随机源不会动背包与名册", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    hero.Bag.Add("hero-emblem");
    var badge = hero.Bag.Entries.First(entry => entry.ItemId == "hero-emblem");

    run.Throws(() => party.Recruit(badge.Id, () => 1), "招募随机值无效");
    run.Equal(1, hero.Bag.Count("hero-emblem"), "徽章还在");
    run.Equal(0, party.Snapshot().Members.Count, "名册没变");
});

run.Case("技能成长：九十门技能两种身份各有八级，伙伴档位覆盖主角档位", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var catalog = ReadSkills(root);
    run.Equal(90, catalog.Skills.Count, "技能总数");

    foreach (var (id, skill) in catalog.Skills)
    {
        run.True(skill.Companion is not null, $"{id} 缺少伙伴成长块");
        foreach (var member in new[] { "player", "story-companion", "recruited-companion" })
        {
            var selected = catalog.SkillForGrowth(id, member != "player");
            var levels = selected.Ranks.Select(rank => rank.Level).ToList();
            run.Equal("1,2,3,4,5,6,7,8", string.Join(",", levels), $"{id}/{member} 的级次");
        }
    }

    var hero = catalog.SkillForGrowth("dark-strike", false);
    var companion = catalog.SkillForGrowth("dark-strike", true);
    run.True(!ReferenceEquals(hero.Ranks, companion.Ranks), "伙伴用的是另一份等级表");
    run.Equal(1, hero.Ranks[0].PointCost);
    run.Equal(2, companion.Ranks[0].PointCost);
});

run.Case("技能成长：主角的技能点记在角色卡上，初始化只清药水与已花掉的", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var catalog = ReadSkills(root);
    var hero = new BattleCharacter("dark-knight", stats, items);
    var progression = new StorySkillProgression(hero, catalog);

    hero.AddExperience((stats.ExperienceToNext(1) ?? 0) + (stats.ExperienceToNext(2) ?? 0));
    run.Equal(2, progression.Points, "3 级给两点");

    var skillId = catalog.SkillsOf("dark-knight")[0];
    var cost = catalog.Skills[skillId].Ranks[1].PointCost;
    var learned = progression.Learn(skillId);
    run.Equal(2, learned, "学到 2 级");
    run.Equal(2 - cost, progression.Points, "扣掉这一级的 pointCost");

    hero.GrantSkillPoint();
    run.Equal(3 - cost, progression.Points, "药水加一点");

    progression.Reset();
    run.Equal(2, progression.Points, "初始化后只剩等级带来的那两点");
    run.Equal(1, progression.Draft(catalog, "dark-knight").LevelOf(skillId), "回到初始技能");
    run.Equal(1, progression.ResetVersion, "初始化次数 +1");
});

run.Case("技能成长：物品用途里的技能书、洗点与灵药都由技能进度结算", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var catalog = ReadSkills(root);
    var hero = new BattleCharacter("dark-knight", stats, items);
    var progression = new StorySkillProgression(hero, catalog);

    run.Equal("获得 1 个技能点", progression.ApplyItem(ItemUseRules.SkillPointItem), "卓越灵药给一点技能点");
    run.Equal(1, progression.Points);

    run.Equal("已初始化技能与技能点", progression.ApplyItem("skills-reset-spell"), "洗点药水走技能进度");

    run.True(progression.ApplyItem("healing-potion-s") is null, "其它用途不由技能进度结算");
});

run.Case("商店：点货先弹说明，不可出售直接提示", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (catalog, items, stats) = ReadShops(root);
    var bag = new Bag(() => { }, items, "dark-knight", rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { StartMap = "shadow-camp", Bag = bag };
    var shop = new Storefront(catalog, items, stats, session, "crooked-street-vendor-582a688d", "shadow-camp");
    var trade = new ShopTrade(shop, session);

    var offer = shop.Offers.First(item => !item.Sold);
    run.True(trade.Select(TradeKind.Buy, offer.Id), "选中货架上的第一件");
    run.Equal(TradeStep.Description, trade.Step, "先停在说明面板");
    run.True(!trade.ModalOpen, "说明面板不算弹层");

    trade.BeginTrade();
    run.Equal(trade.Maximum() > 1 ? TradeStep.Quantity : TradeStep.Confirm, trade.Step,
        "能多买的问数量，否则直接确认");
    trade.Cancel();
    run.True(!trade.Active, "取消后没有选中");

    // 任务物品不能卖：跳过说明直接给提示框。
    var questId = items.Items.First(pair => pair.Value.Category == "quest").Key;
    bag.Add(questId);
    var questEntry = bag.Entries.First(entry => entry.ItemId == questId);
    run.True(trade.Select(TradeKind.Sell, questEntry.Id), "选得中任务物品");
    run.Equal(TradeStep.Notice, trade.Step, "不可出售直接落到提示");
});

run.Case("商店：数量上限按堆叠与限购、确认后一进一出", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (catalog, items, stats) = ReadShops(root);
    var bag = new Bag(() => { }, items, "dark-knight", rules: new EquipmentBagRules(stats, items, "dark-knight"));
    var session = new Session { StartMap = "shadow-camp", Bag = bag };
    var shop = new Storefront(catalog, items, stats, session, "merchandise-dealer-b09d9585", "shadow-camp");
    var trade = new ShopTrade(shop, session);

    var potion = shop.Offers.First(offer => offer.Instance.ItemId == "healing-potion-s");
    trade.Select(TradeKind.Buy, potion.Id);
    run.Equal(99, trade.Maximum(), "常驻堆叠商品一次一组九十九");

    trade.BeginTrade();
    run.Equal(TradeStep.Quantity, trade.Step, "能多买的先问数量");
    trade.Adjust(1);
    run.Equal(2, trade.Quantity, "加一件");
    trade.Adjust(-5);
    run.Equal(1, trade.Quantity, "减到 1 就停");
    trade.Adjust(1);
    run.Equal(2, trade.Quantity, "再要一件");

    var price = shop.BuyPrice(potion.Id);
    session.GainMoney(price * 3);
    trade.ToConfirm();
    run.Equal(TradeStep.Confirm, trade.Step, "确定后进确认框");
    run.Equal(price * 2, trade.Total(), "总额 = 单价 × 数量");
    run.True(!trade.Insufficient(), "钱够");

    run.Equal<string?>(null, trade.Confirm(), "买入成功");
    run.Equal(price, session.Money, "三瓶的钱扣掉两瓶");
    run.Equal(2, bag.Count("healing-potion-s"), "两瓶入包");
    run.True(!trade.Active, "成交后没有选中");

    var lamp = new Storefront(catalog, items, stats, session, "lamp-dealer-007", "silent-cave-depths",
        new LcgRandom(3).Next);
    var second = new ShopTrade(lamp, session);
    var expensive = lamp.Offers.FirstOrDefault(offer => !offer.Sold && lamp.BuyPrice(offer.Id) > session.Money);
    if (expensive is not null)
    {
        second.Select(TradeKind.Buy, expensive.Id);
        second.BeginTrade();
        second.ToConfirm();
        run.True(second.Insufficient(), "买不起");
        run.Equal("金币不足", second.Confirm(), "报金币不足");
    }
});

run.Case("技能页：展示行、说明正文与下一级预览逐字对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "skill-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 skill-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var golden = document.RootElement.GetProperty("skills");

    var ranksChecked = 0;
    var levelsChecked = 0;

    foreach (var entry in golden.EnumerateObject())
    {
        var id = entry.Name;
        run.True(catalog.Skills.ContainsKey(id), $"技能表缺 {id}");
        var skill = catalog.Skills[id];

        foreach (var (expectedRank, index) in entry.Value.GetProperty("ranks").EnumerateArray()
                     .Select((value, position) => (value, position)))
        {
            var actual = SkillDisplay.Entries(skill, skill.Ranks[index])
                .Select(item => $"{item.Label}|{item.Text}");
            var want = expectedRank.GetProperty("entries").EnumerateArray().Select(item => item.GetString());
            run.Equal(string.Join("\n", want), string.Join("\n", actual), $"{id} 第 {index + 1} 级的详情行");
            run.Equal(expectedRank.GetProperty("pointCost").GetInt32(), skill.Ranks[index].PointCost,
                $"{id} 第 {index + 1} 级的技能点消耗");
            ranksChecked++;
        }

        foreach (var level in entry.Value.GetProperty("levels").EnumerateArray())
        {
            var value = level.GetProperty("level").GetInt32();

            var description = DescriptionTextPart.Plain(SkillDetailLines.DescriptionParts(skill, value));
            run.Equal(level.GetProperty("description").GetString(), description, $"{id} LV{value} 的说明正文");

            var expectedLearning = level.GetProperty("learning").GetString()!;
            if (expectedLearning.Length > 0)
            {
                var requirements = new SkillLearningRequirements(value, 3, 0, 7, false, false, true);
                var learning = DescriptionTextPart.Plain(SkillDetailLines.LearningParts(skill.Name, value, requirements));
                run.Equal(expectedLearning, learning, $"{id} LV{value} 的学习条件");
            }

            var preview = SkillDetailLines.NextLevelPreview(skill.Ranks, value, skill.Kind,
                    skill.NormalMaxLevel, skill.Display)
                .Select(row => $"{row.Label}|{row.From}|{row.To}|{row.Kind}");
            var expectedPreview = level.GetProperty("preview").EnumerateArray().Select(item => item.GetString());
            run.Equal(string.Join("\n", expectedPreview), string.Join("\n", preview), $"{id} LV{value} 的下一级预览");

            levelsChecked++;
        }
    }

    Console.WriteLine($"      覆盖 {golden.EnumerateObject().Count()} 门技能、{ranksChecked} 级数值、{levelsChecked} 段说明");
    run.True(ranksChecked > 400, "等级数据太少，基准可能没生成完整");
});

run.Case("技能页：职业表与初始技能点口径", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;

    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "..", "godot", "tests", "data", "skill-output.json")));
    var goldenClasses = document.RootElement.GetProperty("classes").EnumerateArray().ToList();
    run.Equal(goldenClasses.Count, catalog.Classes.Length, "职业数量");
    foreach (var expected in goldenClasses)
    {
        var id = expected.GetProperty("id").GetString()!;
        var skills = expected.GetProperty("skills").EnumerateArray().Select(item => item.GetString()!).ToList();
        run.Equal(string.Join(",", skills), string.Join(",", catalog.SkillsOf(id)), $"{id} 的技能表次序");
    }

    var hero = new BattleCharacter("dark-knight", stats, items);
    var progression = new StorySkillProgression(hero, catalog);
    run.Equal(0, progression.Points, "1 级没有技能点");

    var draft = progression.Draft(catalog, "dark-knight");
    var skillIds = catalog.SkillsOf("dark-knight");
    run.Equal(1, draft.LevelOf(skillIds[0]), "初始主动技能是 1 级");
    run.Equal(0, draft.LevelOf(skillIds[1]), "第二个技能从 0 级开始");

    hero.AddExperience((stats.ExperienceToNext(1) ?? 0) + (stats.ExperienceToNext(2) ?? 0));
    run.Equal(3, hero.Level, "喂够经验升到 3 级");
    var rank = progression.LearningRequirements(skillIds[0]);
    run.Equal(2, progression.Points, "3 级给两点技能点");
    var before = progression.Points;
    var next = progression.Learn(skillIds[0]);
    run.Equal(2, next, "学到 2 级");
    run.Equal(before - catalog.Skills[skillIds[0]].Ranks[1].PointCost, progression.Points, "扣的是 2 级的 pointCost");
    run.True(progression.InvestedPoints() > 0, "投入点数按累计行统计");

    progression.Reset();
    run.Equal(2, progression.Points, "初始化后只剩等级带来的技能点");
    run.Equal(1, progression.Draft(catalog, "dark-knight").LevelOf(skillIds[0]), "初始化后回到初始技能");
});

run.Case("任务日志：分组、排序与详情正文逐字对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "quest-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 quest-output.json 基准"); return; }

    var campaign = JsonSerializer.Deserialize<CampaignContent>(
        File.ReadAllText(Path.Combine(root, "content", "campaign.json")))!;

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var runs = document.RootElement.GetProperty("runs");

    static Dictionary<string, QuestProgress> States(JsonElement source) =>
        source.EnumerateObject().ToDictionary(
            quest => quest.Name,
            quest => new QuestProgress
            {
                Status = quest.Value.GetProperty("status").GetString() switch
                {
                    "active" => QuestStatus.Active,
                    "ready" => QuestStatus.Ready,
                    "completed" => QuestStatus.Completed,
                    "available" => QuestStatus.Available,
                    _ => QuestStatus.Locked,
                },
                Goals = quest.Value.GetProperty("goals").EnumerateObject().ToDictionary(
                    goal => goal.Name, goal => goal.Value.GetInt32()),
                CompletedCycles = quest.Value.TryGetProperty("completedCycles", out var cycles) ? cycles.GetInt32() : 0,
            });

    var groupsChecked = 0;
    foreach (var runCase in new[] { "A", "B" })
    {
        var states = States(document.RootElement.GetProperty("states").GetProperty(runCase));
        var golden = runs.GetProperty(runCase).EnumerateArray().ToList();
        var actual = QuestJournalView.Groups(campaign, states);
        run.Equal(golden.Count, actual.Count, $"第 {runCase} 轮的组数");
        for (var index = 0; index < Math.Min(golden.Count, actual.Count); index += 1)
        {
            var expected = golden[index];
            var group = actual[index];
            run.Equal(expected.GetProperty("id").GetString(), group.Definition.Id, $"第 {runCase} 轮第 {index + 1} 组的 id");
            run.Equal(expected.GetProperty("kind").GetString(), group.Definition.Kind, $"{group.Definition.Id} 的主支线");
            run.Equal(expected.GetProperty("ready").GetBoolean(), group.Ready, $"{group.Definition.Id} 的可交付标记");
            var entries = expected.GetProperty("entries").EnumerateArray().ToList();
            run.Equal(entries.Count, group.Entries.Count, $"{group.Definition.Id} 的段数");
            for (var entry = 0; entry < Math.Min(entries.Count, group.Entries.Count); entry += 1)
            {
                run.Equal(entries[entry].GetProperty("id").GetString(), group.Entries[entry].Quest.Id,
                    $"{group.Definition.Id} 第 {entry + 1} 段的任务 id");
                run.Equal(entries[entry].GetProperty("status").GetString(),
                    group.Entries[entry].State.Status switch
                    {
                        QuestStatus.Active => "active",
                        QuestStatus.Ready => "ready",
                        QuestStatus.Completed => "completed",
                        _ => "other",
                    }, $"{group.Definition.Id} 第 {entry + 1} 段的状态");
            }

            run.Equal(expected.GetProperty("text").GetString(),
                QuestJournalView.Text(group, id => id), $"{group.Definition.Id} 的详情正文");
            groupsChecked++;
        }
    }

    var samples = 0;
    foreach (var sample in document.RootElement.GetProperty("textLayout").EnumerateArray())
    {
        var value = sample.GetProperty("value").GetString()!;
        var merged = new List<string>();
        foreach (var part in QuestTextLayout.Parts(value))
        {
            var rendered = part.IsBreak ? "BREAK" : $"{part.Tone.ToString().ToLower()}|{part.Text}";
            if (part.IsBreak || merged.Count == 0 || merged[^1].StartsWith("BREAK") ||
                merged[^1].Split('|')[0] != part.Tone.ToString().ToLower())
                merged.Add(rendered);
            else merged[^1] = merged[^1] + part.Text;
        }

        var parts = merged.AsEnumerable();
        var expectedParts = sample.GetProperty("parts").EnumerateArray().Select(item => item.GetString());
        run.Equal(string.Join("\n", expectedParts), string.Join("\n", parts), "控制符拆段");

        static float Measure(string text) => text.Length * 10f;
        var lines = QuestTextLayout.Layout(QuestTextLayout.Parts(value), 100f, Measure);
        var actualLines = lines.Select(line => string.Join("\n", line.Runs
                .Select(runItem => $"{runItem.Tone.ToString().ToLower()}|{runItem.X}|{runItem.Text}"))
            + $"\nWIDTH|{line.Width}");
        var expectedLines = sample.GetProperty("lines").EnumerateArray().Select(line =>
            string.Join("\n", line.GetProperty("runs").EnumerateArray().Select(item => item.GetString()))
            + $"\nWIDTH|{line.GetProperty("width").GetSingle()}");
        run.Equal(string.Join("||", expectedLines), string.Join("||", actualLines), "断行与色段");
        samples++;
    }

    var runA = runs.GetProperty("A").EnumerateArray().Count();
    var runB = runs.GetProperty("B").EnumerateArray().Count();
    Console.WriteLine($"      覆盖 {runA} + {runB} 组任务、{groupsChecked} 段正文、{samples} 段排版");
});

SaveGame MakeGame()
{
    var game = new SaveGame
    {
        Format = SaveGameStore.SaveFormat,
        Summary = new SaveSummary
        {
            ClassId = "dark-knight",
            ClassName = "黑暗骑士",
            Portrait = "portrait.png",
            Level = 3,
            MapId = "shadow-camp",
            MapName = "黑暗骑士团营地",
        },
        Session = new SessionState
        {
            ClassId = "dark-knight",
            Money = 1200,
            GameTimeMs = 45678,
            Bag = new BagState
            {
                Instances = [],
                Shortcuts = [new BagOwnerShortcuts("player",
                    new List<ShortcutEntry?>(new ShortcutEntry?[12]))],
            },
            Shops = new ShopVisitsState { Stock = [] },
        },
        World = new JsonObject { ["location"] = new JsonObject { ["map"] = "shadow-camp" } },
    };
    return game;
}

run.Case("存档名字只保留中文、英文和数字，超长截断", () =>
{
    run.Equal("暗黑骑士Kiyan01", SaveGameStore.SanitizeSaveName(" 暗黑骑士！Kiyan－01 "));
    run.Equal(12, SaveGameStore.SanitizeSaveName(new string('甲', 20)).Length, "超长截断到 12 字");
    run.Equal("", SaveGameStore.SanitizeSaveName("!!!"), "全部被过滤时返回空串");
});

run.Case("存档槽键名固定，越界与自动槽受保护", () =>
{
    var storage = new MemorySaveStorage();
    var slots = new SaveSlots(storage);
    slots.Write(0, MakeGame());
    run.True(storage.Items.ContainsKey("inotia4.story.v1.slot0"), "手动槽键形如 inotia4.story.v1.slot0");
    slots.Write(SaveSlotRef.Auto, MakeGame());
    run.True(storage.Items.ContainsKey("inotia4.story.v1.auto"), "自动槽键是 inotia4.story.v1.auto");

    run.Throws(() => slots.Write(10, MakeGame()), "存档槽编号无效");
    run.Throws(() => slots.Erase(SaveSlotRef.Auto), "自动存档不能删除或改名");
    run.Throws(() => slots.Rename(SaveSlotRef.Auto, "x"), "自动存档不能删除或改名");
});

run.Case("读槽三态：空、坏内容、版本不符各按各的口径", () =>
{
    var storage = new MemorySaveStorage();
    var slots = new SaveSlots(storage);
    run.True(slots.Peek(0) is SlotSummaryReading.Empty);
    run.True(slots.Inspect(0) is SlotReading.Empty);

    storage.Items["inotia4.story.v1.slot0"] = "{not json";
    run.True(slots.Peek(0) is SlotSummaryReading.Unreadable badJson &&
        badJson.Reason == "存档内容不是有效 JSON");

    storage.Items["inotia4.story.v1.slot0"] = "42";
    run.True(slots.Peek(0) is SlotSummaryReading.Unreadable notObject &&
        notObject.Reason == "存档内容不是对象");

    storage.Items["inotia4.story.v1.slot0"] =
        """{"format":32,"summary":{"classId":"dark-knight","mapId":"shadow-camp","level":1},"session":{},"world":{}}""";
    run.True(slots.Peek(0) is SlotSummaryReading.Empty, "format 32 不在可读清单里按空槽显示");

    storage.Items["inotia4.story.v1.slot0"] =
        "{\"format\":" + (SaveGameStore.SaveFormat + 1) +
        ",\"summary\":{\"classId\":\"dark-knight\",\"mapId\":\"shadow-camp\",\"level\":1},\"session\":{},\"world\":{}}";
    run.True(slots.Peek(0) is SlotSummaryReading.Empty, "比当前格式更新的版本按空槽显示");

    storage.Items["inotia4.story.v1.slot0"] =
        """{"format":"37","summary":{"classId":"dark-knight","mapId":"shadow-camp","level":1},"session":{},"world":{}}""";
    run.True(slots.Inspect(0) is SlotReading.Unreadable badFormat &&
        badFormat.Reason == "存档缺少有效版本", "format 不是数字按缺少有效版本");

    storage.Items["inotia4.story.v1.slot0"] =
        """{"format":37,"summary":{"classId":"dark-knight","mapId":"shadow-camp","level":1},"session":{}}""";
    run.True(slots.Inspect(0) is SlotReading.Unreadable noWorld &&
        noWorld.Reason == "存档缺少会话或世界状态");

    storage.Items["inotia4.story.v1.slot0"] =
        """{"format":37,"summary":{"classId":"dark-knight","mapId":"shadow-camp","level":"三"},"session":{},"world":{}}""";
    run.True(slots.Inspect(0) is SlotReading.Unreadable badSummary &&
        badSummary.Reason == "存档摘要无效", "level 不是数字按摘要无效");
});

run.Case("写读改擦：覆盖沿用旧名、改名过滤、缓存随写失效", () =>
{
    var storage = new MemorySaveStorage();
    var slots = new SaveSlots(storage);
    var game = MakeGame();
    game.Summary.Name = "骑士甲";
    slots.Write(0, game);
    run.True(slots.Peek(0) is SlotSummaryReading.Ok first && first.Summary.Name == "骑士甲");

    slots.Write(0, MakeGame());
    run.True(slots.Peek(0) is SlotSummaryReading.Ok kept &&
        kept.Summary.Name == "骑士甲", "覆盖旧档时沿用自定义名字");

    run.Null(slots.Rename(0, "###"));
    run.True(slots.Peek(0) is SlotSummaryReading.Ok cleared && cleared.Summary.Name is null,
        "空名清掉自定义名字");

    run.Equal("abcdef", slots.Rename(0, "abc!!def"));
    run.True(slots.Peek(0) is SlotSummaryReading.Ok renamed && renamed.Summary.Name == "abcdef");

    slots.Erase(0);
    run.True(slots.Peek(0) is SlotSummaryReading.Empty);
    run.Throws(() => slots.Rename(0, "x"), "这个存档槽里没有可以改名的存档");
});

run.Case("升级链 33→当前格式：版本逐级推进，34→35 把同一技能收敛到最后一格", () =>
{
    var game = MakeGame();
    game.Format = 33;
    game.Session.Bag.Shortcuts =
    [
        new("player", new List<ShortcutEntry?>
        {
            new("skill", "slash"), new("skill", "guard"),
            new("skill", "slash"), null,
            new("item", "healing-potion-m"), null,
            new("skill", "guard"), null, null, null, null, null,
        }),
    ];

    var upgraded = SavedGameUpgrades.Upgrade(game);
    run.Equal(SaveGameStore.SaveFormat, upgraded.Format, "旧版本逐级推进到当前格式");

    var row = upgraded.Session.Bag.Shortcuts[0].Row;
    run.Null(row[0], "第一次登记的 slash 被最后一次覆盖");
    run.Null(row[1], "第一次登记的 guard 被最后一次覆盖");
    run.Equal("slash", row[2]?.Id, "slash 保留最后一格");
    run.Equal("item", row[4]?.Kind, "物品沿用可占多格规则");
    run.Equal("guard", row[6]?.Id, "guard 保留最后一格");

    run.Throws(() => SavedGameUpgrades.Upgrade(new SaveGame { Format = 32 }),
        "存档版本不受支持");
    run.Throws(() => SavedGameUpgrades.Upgrade(new SaveGame { Format = SaveGameStore.SaveFormat + 1 }),
        "存档版本不受支持", "未来版本同样拒绝");
});

run.Case("已删除道具迁移：扩容背包换成药水，其余丢弃并清格位与快捷栏", () =>
{
    var game = MakeGame();
    game.Session.Bag.Instances =
    [
        new ItemInstance { Id = "i1", ItemId = "handbag", Quantity = 1 },
        new ItemInstance { Id = "i2", ItemId = "novice-dice", Quantity = 1 },
        new ItemInstance { Id = "i3", ItemId = "healing-potion-m", Quantity = 3 },
    ];
    game.Session.Bag.Pouches = new Dictionary<string, List<string?>>
    {
        ["consumable"] = ["i3", "i2", null],
        ["etc"] = ["i1"],
    };
    game.Session.Bag.AllCells = ["i3", "i2", "i1", null];
    game.Session.Bag.Shortcuts = [new BagOwnerShortcuts("player",
    [
        new ShortcutEntry("item", "standard-dice"), new ShortcutEntry("item", "small-bag"),
        null, null, null, null, null, null, null, null, null, null,
    ])];
    game.Session.Shops = new ShopVisitsState
    {
        Stock =
        [
            KeyValuePair.Create("shop-1", new List<ShopOffer>
            {
                new()
                {
                    Id = "merchandise-1",
                    Instance = new ItemInstance { Id = "m1", ItemId = "chaos-dice", Quantity = 1 },
                    Limited = true,
                    Concealed = false,
                },
                new()
                {
                    Id = "merchandise-2",
                    Instance = new ItemInstance { Id = "m2", ItemId = "healing-potion-m", Quantity = 5 },
                    Limited = false,
                    Concealed = false,
                },
            }),
        ],
    };

    var migrated = SavedGameUpgrades.MigrateLegacyItems(game);
    var items = migrated.Session.Bag.Instances;
    run.Equal(2, items.Count, "骰子实例被丢弃");
    run.Equal("healing-potion-m", items.Single(entry => entry.Id == "i1").ItemId,
        "handbag 换成中瓶治疗药水");
    run.Equal("healing-potion-m", items.Single(entry => entry.Id == "i3").ItemId);

    var consumable = migrated.Session.Bag.Pouches["consumable"];
    run.Equal("i3", consumable[0]);
    run.Null(consumable[1], "丢弃的骰子格位清空");
    var allCells = migrated.Session.Bag.AllCells;
    run.Equal("i3", allCells[0]);
    run.Equal("i1", allCells[1]);
    run.Null(allCells[2], "末尾补位空格");
    run.Null(migrated.Session.Bag.Shortcuts[0].Row[0], "快捷栏里的骰子清掉");
    run.Equal("healing-potion-l", migrated.Session.Bag.Shortcuts[0].Row[1]?.Id, "small-bag 换成大瓶药水");

    var stock = migrated.Session.Shops.Stock[0].Value;
    run.Equal(1, stock.Count, "货架上的骰子下架");
    run.Equal("healing-potion-m", stock[0].Instance.ItemId);

    var clean = MakeGame();
    run.True(ReferenceEquals(clean, SavedGameUpgrades.MigrateLegacyItems(clean)),
        "没有旧道具时原样返回同一份存档");
});

run.Case("会话状态抓取与恢复：金币、时间、背包、技能、队伍、任务簿往返", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    var skills = new StorySkillProgression(hero, ReadSkills(root));
    var campaign = JsonSerializer.Deserialize<CampaignContent>(
        File.ReadAllText(Path.Combine(root, "content", "campaign.json")))!;
    var journal = new QuestJournal(campaign);
    var session = new Session { Bag = hero.Bag, Hero = hero, Party = party, Skills = skills, Journal = journal };

    session.GainMoney(500);
    session.Advance(32100);
    hero.AddExperience(900);
    hero.Bag.Add("healing-potion-m", 2);
    skills.RestoreState(skills.CaptureState());
    session.Shops.EnterMap("shadow-camp");

    var captured = session.CapturePersistentState();
    run.Equal(500, captured.Money);
    run.Equal(32100L, captured.GameTimeMs);
    run.Equal("dark-knight", captured.ClassId);
    run.True(captured.Hero is not null && captured.Hero.Level == hero.Level);
    run.True(captured.Bag.Instances.Any(entry => entry.ItemId == "healing-potion-m"),
        "背包里的药水进了快照");
    run.True(captured.Party is not null, "名册进了快照");

    var json = JsonSerializer.Serialize(captured, SaveGameStore.JsonOptions);
    var roundTrip = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;
    run.Equal(JsonSerializer.Serialize(roundTrip, SaveGameStore.JsonOptions), json, "会话快照 JSON 往返逐字一致");

    var (freshHero, freshParty) = ReadParty(root, "dark-knight");
    var freshSkills = new StorySkillProgression(freshHero, ReadSkills(root));
    var fresh = new Session
    {
        Bag = freshHero.Bag, Hero = freshHero, Party = freshParty,
        Skills = freshSkills, Journal = new QuestJournal(campaign),
    };
    fresh.RestoreState(roundTrip);
    run.Equal(500, fresh.Money);
    run.Equal(32100L, fresh.GameTimeMs);
    run.Equal(hero.Level, freshHero.Level, "等级跟着存档走");
    run.Equal(hero.Bag.Count("healing-potion-m"), freshHero.Bag.Count("healing-potion-m"),
        "药水数量跟着存档走");

    var recaptured = JsonSerializer.Serialize(fresh.CapturePersistentState(), SaveGameStore.JsonOptions);
    run.Equal(json, recaptured, "恢复后再抓一份与最初逐字一致");
});

run.Case("存档写出与读回：SaveSlots 全链路", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    var session = new Session { Bag = hero.Bag, Hero = hero, Party = party, Journal = new QuestJournal() };
    session.GainMoney(77);
    var summary = SaveGameStore.SaveSummary(session, "shadow-camp", "黑暗骑士团营地", "黑暗骑士", "p.png");
    var game = SaveGameStore.CaptureSaveGame(session, new JsonObject { ["note"] = "world" }, summary);
    run.Equal(SaveGameStore.SaveFormat, game.Format);
    run.True(game.Summary.SavedAt is not null, "写档时刻进了摘要");

    var storage = new MemorySaveStorage();
    var slots = new SaveSlots(storage);
    slots.Write(2, game);

    run.Equal("存档槽 3", SaveGameStore.SaveSlotLabel(2));
    run.Equal("自动存档", SaveGameStore.SaveSlotLabel(SaveSlotRef.Auto));

    var reading = slots.Inspect(2);
    run.True(reading is SlotReading.Ok ok, "写进去的槽能整份读回");
    var restored = ((SlotReading.Ok)reading).Game;
    run.Equal(77, restored.Session.Money);
    run.Equal("shadow-camp", restored.Summary.MapId);
    run.Equal("world", restored.World?["note"]?.GetValue<string>(), "世界 JSON 原样透传");

    var (freshHero, freshParty) = ReadParty(root, "dark-knight");
    var fresh = new Session
    {
        Bag = freshHero.Bag, Hero = freshHero, Party = freshParty, Journal = new QuestJournal(),
    };
    SaveGameStore.RestoreSaveGame(fresh, restored);
    run.Equal(77, fresh.Money, "读档后金币跟着存档走");
});

run.Case("场景快照校验：结构完整才收，坏档逐条拒绝", () =>
{
    JsonObject Actor(string id = "entity:a", string facing = "south") => new()
    {
        ["id"] = id,
        ["actor"] = "kiyan",
        ["x"] = 10,
        ["y"] = 20,
        ["facing"] = facing,
        ["visible"] = true,
    };
    JsonObject Snapshot(params JsonNode?[] actors) => new()
    {
        ["map"] = "shadow-camp",
        ["actors"] = new JsonArray(actors),
    };

    SceneSnapshotRules.Validate(Snapshot(Actor()));
    SceneSnapshotRules.Validate(null);

    run.Throws(() => SceneSnapshotRules.Validate(new JsonObject { ["actors"] = new JsonArray() }),
        "缺少地图或对象列表");
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(Actor(), Actor())), "id 重复");
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(Actor(facing: "up"))), "facing 无效");
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(new JsonObject { ["id"] = "x" })), "actor 必须是非空字符串");

    var noVisible = Actor();
    noVisible.Remove("visible");
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(noVisible)), "visible 必须是布尔值");

    var badAlpha = Actor();
    badAlpha["alpha"] = 1.5;
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(badAlpha)), "alpha 必须在 0..1");

    var badClip = Actor();
    badClip["clip"] = "idle";
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(badClip)), "clip 必须以朝向结尾");

    var okClip = Actor();
    okClip["clip"] = "attack-east";
    okClip["alpha"] = 0.4;
    SceneSnapshotRules.Validate(Snapshot(okClip));

    var badAuthority = Actor();
    badAuthority["authority"] = "mystery";
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(badAuthority)), "authority 无效");

    var textX = Actor();
    textX["x"] = "10";
    run.Throws(() => SceneSnapshotRules.Validate(Snapshot(textX)), "x/y 必须是有限数字");
});

run.Case("演员实例查重与引用：来源键有优先级，重复可见演员在边界被拒", () =>
{
    SceneActorSaved Actor(string id, string? worldEntityId = null, string? memberId = null,
        string? enemyKey = null, bool visible = true, bool? stageHidden = null, bool? removed = null) => new()
    {
        Id = id, Actor = "kiyan", X = 1, Y = 2, Facing = "south",
        Visible = visible, WorldEntityId = worldEntityId, MemberId = memberId,
        EnemyKey = enemyKey, StageHidden = stageHidden, Removed = removed,
    };

    run.Equal("world:chest-1", ActorCensus.InstanceKey(Actor("a", worldEntityId: "chest-1", memberId: "m")), "world 优先");
    run.Equal("member:m1", ActorCensus.InstanceKey(Actor("a", memberId: "m1", enemyKey: "e1")), "member 次之");
    run.Equal("enemy:e1", ActorCensus.InstanceKey(Actor("a", enemyKey: "e1")), "enemy 再次");
    run.Equal("story:a", ActorCensus.InstanceKey(Actor("a")), "剧情替身兜底");

    var duplicates = ActorCensus.DuplicateVisibleActors(
        [Actor("a", worldEntityId: "t1"), Actor("b", worldEntityId: "t1"),
         Actor("ghost", worldEntityId: "t1", visible: false),
         Actor("c", worldEntityId: "t1", stageHidden: true),
         Actor("gone", worldEntityId: "t1", removed: true)]);
    run.True(duplicates.ContainsKey("world:t1") && duplicates["world:t1"].Count == 2, "只数可见的那两个");

    run.Equal(0, ActorCensus.DuplicateVisibleActors(
        [Actor("a", worldEntityId: "t1"), Actor("b", worldEntityId: "t2")]).Count, "不同实例不算重复");

    run.Throws(() => ActorCensus.AssertUnique(
            [Actor("a", worldEntityId: "t1"), Actor("b", worldEntityId: "t1")], "剧情舞台"),
        "剧情舞台演员实例重复：world:t1 → a, b", "查重报在边界前缀上");

    run.Equal("world|m1|e1", ActorRef.Key(new ActorRef.World("m1", "e1")), "world 键带地图");
    run.Equal("combat|m1||e1", ActorRef.Key(new ActorRef.Combat("m1", null, "e1")), "无遭遇的 combat 键留空段");
    run.Equal("story|run-7|s1", ActorRef.Key(new ActorRef.Story("run-7", "s1")), "story 键带运行");

    var context = new ActorRefContext("m1", RunId: "run-7", EncounterId: "enc-1");
    run.Equal(ActorRef.Key(new ActorRef.Party("p1")),
        ActorRef.Key(ActorRefs.FromState(Actor("a", memberId: "p1", enemyKey: "e1"), context)), "队员优先于敌人");
    run.Equal(ActorRef.Key(new ActorRef.Combat("m1", "enc-1", "e1")),
        ActorRef.Key(ActorRefs.FromState(Actor("a", enemyKey: "e1", worldEntityId: "w1"), context)), "敌人优先于摆放");
    run.Equal(ActorRef.Key(new ActorRef.World("m1", "w1")),
        ActorRef.Key(ActorRefs.FromState(Actor("a", worldEntityId: "w1"), context)), "摆放再次");
    run.Equal(ActorRef.Key(new ActorRef.Story("run-7", "a")),
        ActorRef.Key(ActorRefs.FromState(Actor("a"), context)), "替身带运行");
    run.Equal("scene", ((ActorRef.Story)ActorRefs.FromState(Actor("a"),
        new ActorRefContext("m1"))).RunId, "无运行时替身用 scene");

    var scene = new SceneSnapshotSaved
    {
        Map = "m1",
        Actors = [Actor("a", worldEntityId: "t1"), Actor("b", worldEntityId: "t1")],
    };
    run.Throws(() => ActorCensus.AssertUnique(scene.Actors, "剧情场景快照"),
        "剧情场景快照演员实例重复", "快照边界同样拒");
});

run.Case("场景计划与差量：别名绑定、可见分组与 appear/update/remove", () =>
{
    SceneActorSaved Actor(string id, string? worldEntityId = null, double x = 0,
        bool visible = true, bool? unpositioned = null) => new()
    {
        Id = id, Actor = "kiyan", X = x, Y = 0, Facing = "south",
        Visible = visible, WorldEntityId = worldEntityId, Unpositioned = unpositioned,
    };

    var scene = new SceneSnapshotSaved
    {
        Map = "m1",
        Actors = [Actor("a", worldEntityId: "w1", x: 1), Actor("b", worldEntityId: "w2", visible: false)],
    };

    // ① 计划：别名按演员 id 归组；不可见的演员不进可见分组。
    var plan = ScenePlans.Plan(scene, [("hero", "a"), ("lead", "a")], runId: "run-1");
    run.Equal(2, plan.Actors.Count, "计划收录全部演员");
    run.Equal("world|m1|w1", plan.Actors[0].RefKey, "计划里带引用键");
    run.Equal("hero,lead", string.Join(",", plan.Actors[0].Aliases), "别名归到同一演员");
    run.Equal(1, plan.Visible.Count, "可见演员进分组（单例也算）");
    run.Equal(1, plan.Visible["world|m1|w1"].Count, "单例分组只有它自己");

    run.Throws(() => ScenePlans.Assert(ScenePlans.Plan(new SceneSnapshotSaved
    {
        Map = "m1",
        Actors = [Actor("a", worldEntityId: "w1"), Actor("b", worldEntityId: "w1")],
    })), "场景计划演员实例重复：world|m1|w1 → a, b", "计划拒重复可见");

    run.Throws(() => ScenePlans.Assert(ScenePlans.Plan(scene, [("hero", "nobody")])),
        "场景计划绑定缺失：hero → nobody", "绑定指到存在的演员");
    run.Throws(() => ScenePlans.Assert(ScenePlans.Plan(scene, [("hero", "a"), ("lead", "a")])),
        "场景计划演员重复绑定：hero 与 lead → a", "一个演员至多一个别名");

    var before = new SceneSnapshotSaved
    {
        Map = "m1",
        Actors = [Actor("a", worldEntityId: "w1", x: 1), Actor("gone", worldEntityId: "w9")],
    };
    var after = new SceneSnapshotSaved
    {
        Map = "m1",
        Actors = [Actor("a", worldEntityId: "w1", x: 5), Actor("new", worldEntityId: "w3")],
    };
    var delta = SceneDiffs.Diff(before, after, runId: "run-1");
    run.Equal("m1", delta.Map, "差量带地图");
    run.Equal("update,appear,remove", string.Join(",", delta.Changes.Select(change => change.Kind)),
        "三种变化都在");
    run.Equal("world|m1|w1", ActorRef.Key(delta.Changes[0].Ref), "更新项带引用");
    run.Equal(1, delta.Changes[0].Before!.X, "更新项带基线");
    run.Equal(5, delta.Changes[0].After!.X, "更新项带现值");
    run.Equal("w9", delta.Changes[2].Before!.WorldEntityId, "移除项按基线顺序产出");

    var still = new SceneSnapshotSaved
    {
        Map = "m1",
        Actors =
        [
            new SceneActorSaved
            {
                Id = "a", Actor = "kiyan", X = 1, Y = 0, Facing = "south", Visible = true,
                WorldEntityId = "w1", Clip = "walk-east", Alpha = 0.5,
            },
            Actor("gone", worldEntityId: "w9"),
        ],
    };
    run.Equal(0, SceneDiffs.Diff(before, still).Changes.Count, "片段与透明度不进差量");

    run.Equal(0, SceneDiffs.Diff(before, before).Changes.Count, "基线自比零差量");
    run.Throws(() => SceneDiffs.Diff(before, new SceneSnapshotSaved
    {
        Map = "m1",
        Actors = [Actor("a", worldEntityId: "w1"), Actor("twin", worldEntityId: "w1")],
    }), "场景差量演员实例重复：world|m1|w1", "现值重复即拒绝");
});

run.Case("世界快照：掷骰结果、场景与音轨随存档往返", () =>
{
    var state = new WorldSnapshotState
    {
        Location = new WorldLocationSaved { Map = "shadow-camp", X = 1, Y = 2, Facing = "east" },
        GeneratedEntities =
        [
            new MapEntity { Id = "spawn-1", Actor = "chest-wood", X = 3, Y = 4, Flip = true },
        ],
        Scene = new SceneSnapshotSaved
        {
            Map = "shadow-camp",
            Actors =
            [
                new SceneActorSaved
                {
                    Id = "entity:spawn-1", Actor = "chest-wood", WorldEntityId = "spawn-1",
                    Authority = "static", X = 3, Y = 4, Facing = "east", Visible = false, Alpha = 0.5,
                },
            ],
        },
        Audio = new StoryAudioSaved { Music = "bgm-camp" },
        FocusTargetId = "enemy-7",
        DamageCount = 3,
    };

    var json = JsonSerializer.SerializeToNode(state, SaveGameStore.JsonOptions)!.ToJsonString();
    run.True(json.Contains("\"generatedEntities\""), "掷骰结果进档");
    run.True(json.Contains("\"scene\""), "场景快照进档");
    run.True(json.Contains("\"audio\""), "音轨进档");
    run.True(json.Contains("\"focusTargetId\""), "锁定目标进档");
    run.True(json.Contains("\"damageCount\""), "受击计数进档");

    var read = JsonSerializer.Deserialize<WorldSnapshotState>(json, SaveGameStore.JsonOptions)!;
    run.Equal(1, read.GeneratedEntities?.Count ?? -1);
    run.Equal("spawn-1", read.GeneratedEntities![0].Id);
    run.True(read.GeneratedEntities[0].Flip, "翻转标记跟着摆放走");
    run.Equal("static", read.Scene!.Actors[0].Authority);
    run.Equal(0.5, read.Scene.Actors[0].Alpha ?? -1);
    run.Equal("bgm-camp", read.Audio?.Music);
    run.Equal("enemy-7", read.FocusTargetId, "锁定目标读回来还是它");
    run.Equal(3, read.DamageCount ?? -1, "受击计数读回来还是 3");
    run.True(read.HasWorld, "带场景的世界快照算「有世界内容」");

    var legacy = JsonSerializer.Deserialize<WorldSnapshotState>(
        "{\"location\":{\"map\":\"m\",\"x\":0,\"y\":0,\"facing\":\"south\"}}", SaveGameStore.JsonOptions)!;
    run.True(legacy.FocusTargetId is null && legacy.DamageCount is null, "旧档两项缺失时为空");

    // 只有落点的旧格式占位不算内容，读档时不该触发恢复。
    run.False(new WorldSnapshotState().HasWorld);

    // 自己写出来的场景必须过得了自己的校验（JSON 口径一致）。
    SceneSnapshotRules.Validate(JsonNode.Parse(json)!["scene"]);
});

run.Case("读档校验世界载荷：掷骰结果、场景落点与音轨逐项把关", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("      跳过：没找到 public/content");
        return;
    }

    var world = JsonSerializer.Deserialize<WorldIndex>(
        File.ReadAllText(Path.Combine(root, "content", "world.json")))!;
    var classes = JsonSerializer.Deserialize<StoryClassCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "story-classes.json")))!;
    var map = world.Maps[0];
    var actor = world.Actors.Keys.First();

    SaveGame Game(JsonObject payload) => new()
    {
        Format = SaveGameStore.SaveFormat,
        Summary = new SaveSummary { ClassId = classes.Classes[0].Id, MapId = map.Id, Level = 1 },
        Session = new SessionState(),
        World = payload,
    };

    JsonObject Payload(string? mapId = null, string? entityActor = null, int playerX = 100,
        int? sceneX = null, string facing = "south", bool scene = true, bool audio = true)
    {
        mapId ??= map.Id;
        var payload = new JsonObject
        {
            ["location"] = new JsonObject
            {
                ["map"] = mapId, ["x"] = playerX, ["y"] = 200, ["facing"] = facing,
            },
            ["generatedEntities"] = new JsonArray(new JsonObject
            {
                ["id"] = "spawn-1", ["actor"] = entityActor ?? actor, ["x"] = 40, ["y"] = 60,
            }),
        };
        if (scene)
            payload["scene"] = new JsonObject
            {
                ["map"] = mapId,
                ["actors"] = new JsonArray(new JsonObject
                {
                    ["id"] = "player", ["actor"] = actor, ["x"] = sceneX ?? playerX, ["y"] = 200,
                    ["facing"] = facing, ["visible"] = true,
                }),
            };
        if (audio) payload["audio"] = new JsonObject { ["music"] = "bgm-camp" };
        return payload;
    }

    run.Equal(map.Id, SavedGameUpgrades.PrepareSavedGame(Game(Payload()), classes, world).MapId,
        "完整载荷能读回");

    var legacy = Payload(scene: false, audio: false);
    legacy.Remove("generatedEntities");
    run.Equal(map.Id, SavedGameUpgrades.PrepareSavedGame(Game(legacy), classes, world).MapId,
        "旧档缺世界载荷也能读回");

    run.Throws(() => SavedGameUpgrades.PrepareSavedGame(Game(Payload(entityActor: "no-such-actor")), classes, world),
        "存档记录的地图人物无效", "摆放引用了不存在的角色");
    run.Throws(() =>
    {
        var payload = Payload();
        var duplicate = (JsonObject)payload["generatedEntities"]![0]!.DeepClone();
        duplicate["id"] = "spawn-1";
        ((JsonArray)payload["generatedEntities"]!).Add(duplicate);
        SavedGameUpgrades.PrepareSavedGame(Game(payload), classes, world);
    }, "存档记录的地图人物无效", "摆放 id 重复");
    run.Throws(() => SavedGameUpgrades.PrepareSavedGame(Game(Payload(mapId: "no-such-map")), classes, world),
        "存档记录的地图或位置无效", "落点地图不存在");
    run.Throws(() => SavedGameUpgrades.PrepareSavedGame(Game(Payload(sceneX: 140)), classes, world),
        "存档场景与主角位置不一致", "场景里的主角不在落点上");
    run.Throws(() =>
    {
        var payload = Payload();
        payload["scene"]!["actors"] = new JsonArray(new JsonObject
        {
            ["id"] = "player", ["actor"] = actor, ["x"] = 100, ["y"] = 200,
            ["facing"] = "up", ["visible"] = true,
        });
        SavedGameUpgrades.PrepareSavedGame(Game(payload), classes, world);
    }, "剧情场景快照格式无效：actors[0].facing 无效", "场景结构坏掉");
    run.Throws(() =>
    {
        var payload = Payload();
        payload["audio"] = new JsonObject { ["music"] = "" };
        SavedGameUpgrades.PrepareSavedGame(Game(payload), classes, world);
    }, "存档记录的音轨无效", "空音轨名");
});

run.Case("40 版旧档升到 41：世界载荷原样保留，只推进版本号", () =>
{
    var game = MakeGame();
    game.Format = 40;
    game.World = new JsonObject { ["location"] = new JsonObject { ["map"] = "shadow-camp" } };

    var upgraded = SavedGameUpgrades.Upgrade(game);
    run.Equal(SaveGameStore.SaveFormat, upgraded.Format);
    run.Equal("shadow-camp", upgraded.World?["location"]?["map"]?.GetValue<string>(),
        "没有新载荷的旧档照样原样搬");
});

run.Case("剧情事件进度随存档往返：读档后一次性事件不再重演", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    // ① 名单本身：覆盖而不是累加——读档前那份进程内标记必须清掉。
    var journal = new QuestJournal();
    journal.MarkEventStarted("event-a");
    journal.MarkEventDone("event-b");
    var state = journal.CaptureEvents();
    run.Equal("event-a", string.Join(',', state.Started), "开演名单");
    run.Equal("event-b", string.Join(',', state.Done), "播完名单");

    var restoredJournal = new QuestJournal();
    restoredJournal.MarkEventDone("event-stale");
    restoredJournal.RestoreEvents(state);
    run.True(restoredJournal.EventStarted("event-a"), "开演过的照旧算开演");
    run.True(restoredJournal.EventDone("event-b"), "播完的照旧算播完");
    run.False(restoredJournal.EventDone("event-stale"), "读档前的旧标记被清掉");
    run.Equal("completed", restoredJournal.EventStatus("event-b"));

    var (hero, party) = ReadParty(root, "dark-knight");
    var session = new Session { Bag = hero.Bag, Hero = hero, Party = party, Journal = journal };
    var summary = SaveGameStore.SaveSummary(session, "shadow-camp", "黑暗骑士团营地", "黑暗骑士", "p.png");
    var json = JsonSerializer.Serialize(
        SaveGameStore.CaptureSaveGame(session, null, summary), SaveGameStore.JsonOptions);
    run.True(json.Contains("\"storyEvents\""), "存档里带上事件进度");

    var read = JsonSerializer.Deserialize<SaveGame>(json, SaveGameStore.JsonOptions)!;
    var (freshHero, freshParty) = ReadParty(root, "dark-knight");
    var fresh = new Session
    {
        Bag = freshHero.Bag, Hero = freshHero, Party = freshParty, Journal = new QuestJournal(),
    };
    SaveGameStore.RestoreSaveGame(fresh, read);
    run.True(fresh.Journal!.EventDone("event-b"), "读档后事件仍是播完");
    run.True(fresh.Journal!.EventStarted("event-a"), "读档后事件仍是开演过");

    var legacy = JsonNode.Parse(json)!.AsObject();
    legacy["format"] = 38;
    legacy["session"]!.AsObject().Remove("storyEvents");
    var legacyGame = SavedGameUpgrades.Upgrade(
        JsonSerializer.Deserialize<SaveGame>(legacy.ToJsonString(), SaveGameStore.JsonOptions)!);
    run.Equal(SaveGameStore.SaveFormat, legacyGame.Format, "38 版旧档能读");
    run.Null(legacyGame.Session.StoryEvents, "旧档没有事件进度");

    var (legacyHero, legacyParty) = ReadParty(root, "dark-knight");
    var legacySession = new Session
    {
        Bag = legacyHero.Bag, Hero = legacyHero, Party = legacyParty, Journal = new QuestJournal(),
    };
    SaveGameStore.RestoreSaveGame(legacySession, legacyGame);
    run.False(legacySession.Journal!.EventDone("event-b"), "旧档读回后事件表为空");
});

run.Case("手动槽按写入时间倒序，空槽与坏档排后面、按槽号稳定排列", () =>
{
    var storage = new MemorySaveStorage();
    var slots = new SaveSlots(storage);
    var old1 = MakeGame(); old1.Summary.SavedAt = 1000;
    var newest = MakeGame(); newest.Summary.SavedAt = 9000;
    var mid = MakeGame(); mid.Summary.SavedAt = 5000;
    slots.Write(5, old1);
    slots.Write(2, newest);
    slots.Write(9, mid);
    storage.Items[SaveSlots.DefaultPrefix + ".slot0"] = "不是JSON";

    var order = SaveBoard.NewestFirstSlots(slot => slots.Peek(slot));
    run.Equal("2,9,5,0,1,3,4,6,7,8", string.Join(',', order), "最新的排最前，空槽与坏档按槽号垫底");
});

run.Case("行标题：自动槽固定，手动槽用自定义名或默认；时间按本地时区补零", () =>
{
    run.Equal("自动存档", SaveBoard.RowTitle(SaveSlotRef.Auto, new SlotSummaryReading.Empty()));
    var named = new SaveSummary { Name = "骑士团", MapId = "m" };
    run.Equal("骑士团", SaveBoard.RowTitle(3, new SlotSummaryReading.Ok(named)));
    var unnamed = new SaveSummary { MapId = "m" };
    run.Equal("存档槽 4", SaveBoard.RowTitle(3, new SlotSummaryReading.Ok(unnamed)));
    run.Equal("存档槽 4", SaveBoard.RowTitle(3, new SlotSummaryReading.Empty()));

    var at = new DateTimeOffset(2026, 10, 19, 21, 4, 0, DateTimeOffset.Now.Offset).ToUnixTimeMilliseconds();
    run.Equal("2026-10-19 21:04", SaveBoard.FormatSavedAt(at));
    run.Equal("", SaveBoard.FormatSavedAt(null));
});

run.Case("存档行：删除叉骑在行框右上角，主命中区让开它", () =>
{
    // 删除叉取存档槽图集（图组 88）右上角那枚圆钮，实测 57×57：
    // 距行框右缘、上边各 10 像素，所以左缘 = 668 − 57 − 10 = 601。
    run.Equal(57, SaveBoard.DeleteSize);
    run.Equal(10, SaveBoard.DeleteInset);
    run.Equal(601, SaveBoard.DeleteLeft);
    run.True(SaveBoard.DeleteLeft + SaveBoard.DeleteSize <= SaveBoard.PanelWidth, "叉不越出行框右边");

    var mainWidth = SaveBoard.DeleteLeft - SaveBoard.RowInset;
    run.Equal(584, mainWidth);
    run.True(mainWidth > SaveBoard.RowWidth / 2, "主命中区仍盖住大半行");
    run.True(SaveBoard.DeleteLeft <= SaveBoard.RowInset + mainWidth, "主命中区与删除叉之间不留缝");
});

run.Case("合成菜单：左栏一列五行、442 宽，位置与尺寸取自控件树", () =>
{
    // UIMix_CreateMainControl @0x856A0：菜单组建在内容块 (10,55)、442×343；
    // 行宽 442、行高 = GRPX_GetFontHeight(1)+0x29；循环 cmp r6,#0x14 配 adds r6,#4 = 5 行。
    var menus = CraftingLayouts.Menus;
    var frame = CraftingLayouts.LeftFrame;
    run.Equal(20f, menus.Area.X);
    run.Equal(55f, menus.Area.Y);
    run.Equal(442f, menus.Area.Width);
    run.Equal(343f, menus.Area.Height);
    run.Equal(menus.Area.Width, menus.RowWidth);

    // 五行必须一次露全（这一栏没有滚动条，靠这 343 装下）。
    run.True(5f * (menus.RowHeight + menus.RowGap) <= menus.Area.Height, "五行装得进 343 的组");
    run.True(menus.RowHeight > menus.TextSize, "行高要放得下字");

    run.True(menus.Area.X >= frame.X && menus.Area.X + menus.Area.Width <= frame.X + frame.Width,
        "列表不越出左框左右");
    run.True(menus.Area.Y + menus.Area.Height <= frame.Height, "列表不越出左框上下");

    var slot = CraftingLayouts.Target.Slot;
    var materials = CraftingLayouts.Materials;
    var craft = CraftingLayouts.CraftButton;
    run.True(slot.Y + slot.Height <= materials.Y, "合成格在材料组上方");
    run.True(materials.Y + materials.SlotSize <= craft.Y, "材料组在组合钮上方");
    run.True(craft.Y + craft.Height <= frame.Height, "组合钮贴到左框底边以内");

    run.Equal(380f, 3f * materials.Stride + materials.SlotSize);

    var prev = CraftingLayouts.TargetPrev;
    var next = CraftingLayouts.TargetNext;
    run.True(prev.X + prev.Width <= slot.X, "左三角在格子左侧");
    run.True(next.X >= slot.X + slot.Width, "右三角在格子右侧");
    run.True(prev.Y + prev.Height <= materials.Y, "三角不压材料组");
    run.Equal(prev.Width, next.Width);

    var batch = CraftingLayouts.Batch;
    run.True(materials.Y + materials.SlotSize <= batch.Y, "批量行不压材料组");
    run.True(batch.Y + batch.Height <= craft.Y, "批量行不压组合钮");
    run.Equal(craft.X, batch.X);
    run.Equal(craft.Width, batch.Width);
    run.True(batch.Step * 2f < batch.Width, "两端按钮之外还留得下中间的字");
});

run.Case("背包翻页：每页四列四行（一页铺满网格），页码与格位区间夹取正确", () =>
{
    run.Equal(4, InventoryPaging.Columns);
    run.Equal(4, InventoryPaging.Rows);
    run.Equal(16, InventoryPaging.PageSize);

    // 四行放得下：前三行各占一个行距（83），最后一行只占格子本身（74）→ 323，正好是网格高。
    // 不能拿「行距 × 4 = 332」当占用高度，那 9 像素是格间距，不是最后一行的高度。
    run.Equal(323f, InventoryPaging.GridHeight(ItemLayout.SlotStride, ItemLayout.SlotSize));
    run.Equal(BagLayouts.Create().Grid.Height,
        InventoryPaging.GridHeight(ItemLayout.SlotStride, ItemLayout.SlotSize));

    run.Equal(1, InventoryPaging.PageCount(0));
    run.Equal(1, InventoryPaging.PageCount(16));
    run.Equal(2, InventoryPaging.PageCount(17));
    run.Equal(2, InventoryPaging.PageCount(32));
    run.Equal(3, InventoryPaging.PageCount(33));

    run.Equal(0, InventoryPaging.ClampPage(-3, 17));
    run.Equal(1, InventoryPaging.ClampPage(9, 17));
    run.Equal(0, InventoryPaging.ClampPage(0, 0));

    run.Equal((0, 16), InventoryPaging.Range(0, 17));
    run.Equal((16, 17), InventoryPaging.Range(1, 17));
    run.Equal((0, 0), InventoryPaging.Range(0, 0));

    run.Equal(0, InventoryPaging.PageOf(0));
    run.Equal(0, InventoryPaging.PageOf(15));
    run.Equal(1, InventoryPaging.PageOf(16));
    run.Equal(1, InventoryPaging.PageOf(31));
    run.Equal(0, InventoryPaging.PageOf(-5));

    run.Equal("第 1 / 2 页", InventoryPaging.Label(0, 17));
    run.Equal("第 2 / 2 页", InventoryPaging.Label(1, 17));
    run.Equal("第 1 / 1 页", InventoryPaging.Label(7, 0));
});

run.Case("读档准备：升级+旧道具迁移，职业与落点校验", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var storyClasses = JsonSerializer.Deserialize<StoryClassCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "story-classes.json")))!;
    var world = JsonSerializer.Deserialize<WorldIndex>(
        File.ReadAllText(Path.Combine(root, "content", "world.json")))!;
    var firstMap = world.Maps[0];

    var alien = MakeGame();
    alien.Summary.ClassId = "no-such-class";
    alien.World = new JsonObject { ["location"] = new JsonObject { ["map"] = firstMap.Id, ["x"] = 100, ["y"] = 100, ["facing"] = "south" } };
    run.Throws(() => SavedGameUpgrades.PrepareSavedGame(alien, storyClasses, world), "存档记录的职业在当前内容里不存在");

    var badMap = MakeGame();
    badMap.Summary.ClassId = storyClasses.Classes[0].Id;
    badMap.Summary.MapId = "no-such-map";
    badMap.World = new JsonObject { ["location"] = new JsonObject { ["map"] = "no-such-map", ["x"] = 1, ["y"] = 1, ["facing"] = "south" } };
    run.Throws(() => SavedGameUpgrades.PrepareSavedGame(badMap, storyClasses, world), "存档记录的地图或位置无效");

    var badFacing = MakeGame();
    badFacing.Summary.ClassId = storyClasses.Classes[0].Id;
    badFacing.Summary.MapId = firstMap.Id;
    badFacing.World = new JsonObject { ["location"] = new JsonObject { ["map"] = firstMap.Id, ["x"] = 1, ["y"] = 1, ["facing"] = "up" } };
    run.Throws(() => SavedGameUpgrades.PrepareSavedGame(badFacing, storyClasses, world), "存档记录的地图或位置无效");

    var good = MakeGame();
    good.Summary.ClassId = storyClasses.Classes[0].Id;
    good.Summary.MapId = firstMap.Id;
    good.World = new JsonObject { ["location"] = new JsonObject { ["map"] = firstMap.Id, ["x"] = 123.5, ["y"] = 66, ["facing"] = "north" } };
    var prepared = SavedGameUpgrades.PrepareSavedGame(good, storyClasses, world);
    run.Equal(SaveGameStore.SaveFormat, prepared.Game.Format, "升级到当前版本");
    run.Equal(firstMap.Id, prepared.MapId);
    run.Equal(123.5, prepared.X);
    run.Equal("north", prepared.Facing);
    run.Equal(firstMap.Id, prepared.Map.Id);
});

run.Case("自动存档：去抖窗口内的重复触发合并成一份", () =>
{
    var ports = new FakeAutoSavePorts();
    var saver = new AutoSaver(ports, debounceMs: 1200, minGapMs: 2000, retryMs: 2000);

    saver.Request(AutoSaveReason.MapEntry, 0);
    saver.Request(AutoSaveReason.StoryEvent, 300);
    saver.Request(AutoSaveReason.Quest, 900);
    saver.Update(500);
    run.Equal(0, ports.Writes, "去抖窗口还没过，不写");
    saver.Update(1199);
    run.Equal(0, ports.Writes);
    saver.Update(1200);
    run.Equal(1, ports.Writes, "窗口到期写一份，三次触发合并");
});

run.Case("自动存档：两次写入至少隔 2000ms，写不出去就安排重试", () =>
{
    var ports = new FakeAutoSavePorts();
    var saver = new AutoSaver(ports, debounceMs: 1200, minGapMs: 2000, retryMs: 2000);

    saver.Request(AutoSaveReason.MapEntry, 0);
    saver.Update(1200);
    run.Equal(1, ports.Writes);

    saver.Request(AutoSaveReason.Quest, 2000);
    saver.Update(3100);
    run.Equal(1, ports.Writes, "距上次写入不足 2000ms，先不写");
    saver.Update(5100);
    run.Equal(2, ports.Writes, "重试到期补写");

    ports.LockedOn = true;
    saver.Request(AutoSaveReason.StoryEvent, 9000);
    saver.Update(10200);
    run.Equal(2, ports.Writes, "演出锁着不抢写");
    ports.LockedOn = false;
    saver.Update(12200);
    run.Equal(3, ports.Writes, "解锁后补写");
});

run.Case("自动存档：停用与空世界都不写，任务接取触发、进度推进不触发", () =>
{
    var ports = new FakeAutoSavePorts { EnabledOn = false };
    var saver = new AutoSaver(ports, debounceMs: 10, minGapMs: 0, retryMs: 10);
    saver.Request(AutoSaveReason.Timer, 0);
    saver.Update(100);
    run.Equal(0, ports.Writes, "停用时触发被忽略");

    ports.EnabledOn = true;
    ports.CaptureResult = null;
    saver.Request(AutoSaveReason.MapEntry, 200);
    saver.Update(400);
    run.Equal(0, ports.Writes, "世界没就绪不写");
    saver.Update(10000);
    run.Equal(0, ports.Writes, "也没把坏状态写出去");

    var journal = new QuestJournal();
    var questSaver = new AutoSaver(new FakeAutoSavePorts(), journal.Book, debounceMs: 10, minGapMs: 0, retryMs: 10);
    questSaver.Update(0);
    journal.Accept("grafton-mission", "script");
    questSaver.Update(100);
    questSaver.Update(200);
    run.True(questSaver.IsSuspended == false);
});

run.Case("自动存档：后台直写跳过去抖与最小间隔", () =>
{
    var ports = new FakeAutoSavePorts();
    var saver = new AutoSaver(ports, debounceMs: 1200, minGapMs: 2000, retryMs: 2000);
    saver.Request(AutoSaveReason.MapEntry, 0);
    saver.Update(1200);
    run.Equal(1, ports.Writes);
    ports.LockedOn = false;
    saver.FlushForBackground(1300);
    run.Equal(2, ports.Writes, "进程可能被回收，不等间隔直接写");
});

static JsonObject DropCatalogNode(int version = 2) => new JsonObject
{
    ["version"] = version,
    ["questItems"] = new JsonObject
    {
        ["enemy-slime-1"] = new JsonArray(
            new JsonObject { ["item"] = "slime-jelly", ["threshold"] = 50 }),
    },
    ["chests"] = new JsonObject
    {
        ["ordinary"] = new JsonObject
        {
            ["pool"] = "slime", ["rolls"] = 1,
            ["chances"] = new JsonObject
            {
                ["money"] = 100, ["equipment"] = 0, ["consumable"] = 0,
                ["upgrade"] = 0, ["crafting"] = 0, ["misc"] = 0, ["unique"] = 0, ["fixed"] = 0,
            },
            ["fixed"] = new JsonArray(),
            ["uniqueChance"] = new JsonObject { ["numerator"] = 100, ["levelOffset"] = 2 },
        },
        ["improved"] = new JsonObject
        {
            ["pool"] = "slime", ["rolls"] = 3,
            ["chances"] = new JsonObject
            {
                ["money"] = 0, ["equipment"] = 0, ["consumable"] = 0,
                ["upgrade"] = 0, ["crafting"] = 0, ["misc"] = 0, ["unique"] = 0, ["fixed"] = 0,
            },
            ["fixed"] = new JsonArray(),
            ["uniqueChance"] = new JsonObject { ["numerator"] = 100, ["levelOffset"] = 2 },
        },
    },
    ["breakables"] = new JsonObject
    {
        ["bush"] = new JsonObject
        {
            ["pool"] = "slime", ["rolls"] = 1,
            ["chances"] = new JsonObject
            {
                ["money"] = 0, ["equipment"] = 0, ["consumable"] = 500,
                ["upgrade"] = 0, ["crafting"] = 0, ["misc"] = 0, ["unique"] = 0, ["fixed"] = 0,
            },
            ["fixed"] = new JsonArray(),
            ["uniqueChance"] = new JsonObject { ["numerator"] = 100, ["levelOffset"] = 2 },
        },
    },
    ["enemies"] = new JsonObject
    {
        ["enemy-slime-1"] = new JsonObject
        {
            ["level"] = 5, ["pool"] = "slime", ["rolls"] = 1,
            ["chances"] = new JsonObject
            {
                ["money"] = 100, ["equipment"] = 0, ["consumable"] = 0,
                ["upgrade"] = 0, ["crafting"] = 0, ["misc"] = 0, ["unique"] = 0, ["fixed"] = 0,
            },
            ["fixed"] = new JsonArray(),
        },
    },
    ["pools"] = new JsonObject
    {
        ["slime"] = new JsonObject
        {
            ["consumable"] = new JsonArray(
                new JsonObject { ["item"] = "slime-jelly", ["weight"] = 1000 }),
        },
    },
    ["equipment"] = new JsonObject
    {
        ["weapon"] = new JsonArray(new JsonObject { ["item"] = "sword-1", ["dropLevel"] = 1 }),
        ["armor"] = new JsonArray(),
        ["special"] = new JsonArray(),
        ["unique"] = new JsonArray(),
        ["epic"] = new JsonArray(),
    },
};

static ItemCatalog DropItems() => JsonSerializer.Deserialize<ItemCatalog>(
    """{"items":{"sword-1":{"category":"equipment"},"slime-jelly":{"category":"consumable"}}}""",
    SaveGameStore.JsonOptions)!;

static Drops.DropCatalog ParsedDropCatalog(int version = 2) => Drops.ParseDrops(
    JsonDocument.Parse(DropCatalogNode(version).ToJsonString()).RootElement,
    DropItems(), ["enemy-slime-1", "enemy-bush-1"]);

static Func<double> Zero() => () => 0.0;

run.Case("掉落目录：合法结构过校验，缺字段与装备池错误要报错", () =>
{
    var catalog = ParsedDropCatalog();
    run.Equal(2, catalog.Version);

    var broken = DropCatalogNode();
    ((JsonObject)broken).Remove("enemies");
    run.Throws(() => Drops.ParseDrops(
            JsonDocument.Parse(broken.ToJsonString()).RootElement,
            DropItems(), ["enemy-slime-1"]),
        "掉落目录版本或结构无效", "enemies 缺失要报错");

    var badPool = DropCatalogNode();
    ((JsonObject)((JsonObject)badPool["pools"]!)["slime"]!)["consumable"] = new JsonArray(
        new JsonObject { ["item"] = "unknown-item", ["weight"] = 1000 });
    run.Throws(() => Drops.ParseDrops(
            JsonDocument.Parse(badPool.ToJsonString()).RootElement,
            DropItems(), ["enemy-slime-1"]),
        "随机掉落池无效", "池子里的物品必须存在");
});

run.Case("怪物掉落：0 概率槽位不出货，金币槽按等级公式算金额", () =>
{
    var catalog = ParsedDropCatalog();
    var rewards = Drops.RollEnemyDrops(catalog, "enemy-slime-1", 5, Zero());
    var money = rewards.OfType<Drops.DropReward.MoneyDrop>().Single();
    run.Equal(50, money.Amount);
});

run.Case("任务掉落：阈值用 1..99 的闭区间掷点，已达标不再掉", () =>
{
    var catalog = ParsedDropCatalog();
    var quests = new IReadOnlyList<Drops.QuestDropRequirement>[]
    {
        [new Drops.QuestDropRequirement("slime-jelly", 3, 0)],
    };
    var hit = Drops.RollQuestDrops(catalog, "enemy-slime-1", quests, Zero());
    run.Equal(1, hit.Count, "1 ≤ 阈值 50，命中");

    var done = Drops.RollQuestDrops(catalog, "enemy-slime-1",
        [[new Drops.QuestDropRequirement("slime-jelly", 3, 3)]], Zero());
    run.Equal(0, done.Count, "已经持有够数，不再掉");
});

run.Case("景物掉落：按容器等级封顶玩家等级，普通槽成功后抽池子", () =>
{
    var catalog = ParsedDropCatalog();
    var result = Drops.RollBreakableLoot(catalog, new CharacterStatsCatalog(), "bush", 1, 100, Zero());
    var item = result.Rewards.OfType<Drops.DropReward.ItemDrop>().Single();
    run.Equal("slime-jelly", item.Item);
});

run.Case("怪物掉落：装备槽带保底加值必中，品级快照进奖励", () =>
{
    var catalog = ParsedDropCatalog();
    var result = Drops.RollEnemyLoot(catalog, new CharacterStatsCatalog(), "enemy-slime-1", 5, 5, Zero(),
        options: new Drops.EnemyLootOptions(EquipmentBonus: 10000));
    var item = result.Rewards.OfType<Drops.DropReward.ItemDrop>().Single();
    run.Equal("sword-1", item.Item);
    run.Null(item.Roll);
});

run.Case("保底：连败累计、出货清零、首杀只认一次", () =>
{
    var pity = new LootPity();
    var config = new Drops.PityConfig
    {
        Equipment = new Drops.RatePair { StepPermille = 100, CapPermille = 500 },
        Rare = new Drops.RatePair { StepPermille = 50, CapPermille = 250 },
    };
    pity.RecordEnemy("player", equipment: false, bestRarity: 0, withinWindow: true);
    pity.RecordEnemy("player", equipment: false, bestRarity: 0, withinWindow: true);
    run.Equal(200, pity.EquipmentBonus("player", config));
    run.Equal(100, pity.RareBonus("player", config));

    pity.RecordEnemy("player", equipment: true, bestRarity: 1, withinWindow: true);
    run.Equal(0, pity.EquipmentBonus("player", config), "出了装备清零");
    run.Equal(150, pity.RareBonus("player", config), "稀有度不足 2 只清装备计数，rare 继续累计");

    run.True(pity.TakeBossFirstDefeat("enemy-boss-1"));
    run.False(pity.TakeBossFirstDefeat("enemy-boss-1"), "首杀保底只触发一次");
    var (_, minRarity) = Drops.FirstBossGuarantee(new Drops.PityConfig
    {
        Equipment = config.Equipment,
        Rare = config.Rare,
        BossGuarantee = new Drops.BossGuaranteeConfig { Equipment = true, MinRarity = 2 },
    }, firstDefeat: true);
    run.Equal(2, minRarity);
});

run.Case("地面掉落：拾取半径与飞行排除、每图 64 件封顶、快照往返", () =>
{
    var store = new GroundLootStore();
    store.Add("map-1", 10, 10, [new Drops.DropReward.MoneyDrop(30)]);
    store.Add("map-1", 10, 10, [new Drops.DropReward.ItemDrop("slime-jelly")]);
    store.Add("map-1", 40, 10, [new Drops.DropReward.MoneyDrop(5)]);
    store.Add("map-2", 10, 10, [new Drops.DropReward.MoneyDrop(1)]);
    run.Equal(3, store.ForMap("map-1").Count);
    run.Equal(1, store.ForMap("map-2").Count);

    store.AddFlying("map-1", (0, 0), (10, 10, 2), "gem-1", 500);
    run.Equal(4, store.ForMap("map-1").Count);
    var near = store.CollectNearby("map-1", 10, 10, _ => true);
    run.Equal(2, near.Count, "飞行中的宝石与 30px 外的金币不在附近");

    store.Advance("map-1", 600);
    near = store.CollectNearby("map-1", 10, 10, _ => true);
    run.Equal(1, near.Count, "落地后的宝石可拾");
    run.Equal(1, store.ForMap("map-1").Count, "只剩 30px 外的那枚金币");

    var before = store.CaptureState().NextId;
    store.Add("map-3", 0, 0, Enumerable.Range(0, 66)
        .Select(_ => (Drops.DropReward)new Drops.DropReward.MoneyDrop(1)).ToList());
    run.Equal(64, store.ForMap("map-3").Count);
    run.Equal(before + 3, store.ForMap("map-3")
        .Min(drop => int.Parse(drop.Id["ground-".Length..])), "最早的两件被淘汰");

    store.AddFlying("map-4", (0, 0), (5, 5, 1), "gem-2", 800);
    var state = store.CaptureState();
    var restored = new GroundLootStore();
    restored.RestoreState(state);
    run.Equal(store.ForMap("map-4").Count, restored.ForMap("map-4").Count);
    run.True(restored.ForMap("map-4").Single().Flight is not null, "飞行状态随快照保留");
});

run.Case("掉落奖励的 JSON：kind 判别形状固定，往返无损", () =>
{
    var instance = new ItemInstance { Id = "inst-1", ItemId = "sword-1", Quantity = 1 };
    var roll = new Drops.DropRoll(2, "精良", 12, 4, 1, 0, 1, Sockets: true);
    var options = SaveGameStore.JsonOptions;
    var json = JsonSerializer.Serialize<Drops.DropReward>(
        new Drops.DropReward.ItemDrop("sword-1", instance, roll), options);
    var node = JsonNode.Parse(json)!.AsObject();
    run.Equal("item", (string?)node["kind"]);
    run.Equal("sword-1", (string?)node["item"]);
    run.Equal(1, (int?)node["quantity"]);

    var back = JsonSerializer.Deserialize<Drops.DropReward>(json, options);
    run.True(back is Drops.DropReward.ItemDrop restored && restored.Item == "sword-1" &&
             restored.Instance!.ItemId == "sword-1" && restored.Roll!.Rarity == 1 && restored.Roll.Sockets);

    var moneyJson = JsonSerializer.Serialize<Drops.DropReward>(new Drops.DropReward.MoneyDrop(30), options);
    run.Equal("money", (string?)JsonNode.Parse(moneyJson)!["kind"]);
    var moneyBack = (Drops.DropReward.MoneyDrop)JsonSerializer.Deserialize<Drops.DropReward>(moneyJson, options)!;
    run.Equal(30, moneyBack.Amount);
});

static string? ContentContentRoot() => FindContentRoot();

run.Case("职业表：六个职业、默认职业、战斗动作与素材齐全", () =>
{
    var root = ContentContentRoot();
    if (root is null) return;

    var catalog = JsonSerializer.Deserialize<StoryClassCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "story-classes.json")), SaveGameStore.JsonOptions)!;

    run.Equal(6, catalog.Classes.Length);
    run.Equal("dark-knight", catalog.DefaultClass);
    run.Equal(6, catalog.Classes.Select(info => info.Id).Distinct().Count(), "职业 id 不能重复");
    run.True(catalog.Default is not null, "默认职业要在表里");

    foreach (var info in catalog.Classes)
    {
        run.False(string.IsNullOrEmpty(info.Name), $"{info.Id} 缺名字");
        run.False(string.IsNullOrEmpty(info.Description), $"{info.Id} 缺说明");
        run.Equal(5, info.Attributes.Length, $"{info.Id} 应当有五行初始值");
        run.Equal(1, info.Player.Level, $"{info.Id} 应当从 1 级开局");

        foreach (var clip in new[] { "idle", "walk", "attack" })
        foreach (var direction in new[] { "south", "west", "north", "east" })
            run.True(info.PlayerActor.Clips.ContainsKey($"{clip}-{direction}"),
                $"{info.Id} 缺 {clip}-{direction}");

        run.True(info.PreviewActor.Clips.Count > 0, $"{info.Id} 缺预览立绘");

        foreach (var path in new[]
                 {
                     info.Icon, catalog.Background, catalog.Assets.Selected, catalog.Assets.Title,
                     catalog.Assets.Start, catalog.Assets.StartPressed, catalog.Assets.DescriptionTop,
                     catalog.Assets.AttributesTop, catalog.Assets.PanelLeft, catalog.Assets.PanelRight,
                 })
        {
            run.True(File.Exists(Path.Combine(root, path)), $"素材缺失：{path}");
        }
    }
});

run.Case("职业外观：职业战斗动作覆盖同名片段，剧情姿态保留，身份不变", () =>
{
    static ActorClip Clip(int frames) => new() { FrameWidth = 31, FrameHeight = 41, Frames = frames };

    var storyIdle = Clip(14);
    var classIdle = Clip(12);
    var lieSide = Clip(3);
    var hero = new ActorDefinition
    {
        Id = "kiyan",
        Name = "凯恩",
        Portrait = "kiyan",
        Clips = new Dictionary<string, ActorClip> { ["idle-south"] = storyIdle, ["lie-side"] = lieSide },
    };
    var selected = new StoryClassInfo
    {
        Id = "assassin",
        PlayerActor = new ActorDefinition
        {
            Id = "battle-assassin",
            Clips = new Dictionary<string, ActorClip> { ["idle-south"] = classIdle, ["attack-east"] = Clip(7) },
        },
    };

    var merged = StoryClassAppearance.Merge(hero, selected);
    run.Equal("kiyan", merged.Id, "身份仍是主角");
    run.True(ReferenceEquals(merged.Clips["idle-south"], classIdle), "同名片段用职业的");
    run.True(ReferenceEquals(merged.Clips["lie-side"], lieSide), "剧情姿态必须保留");
    run.True(merged.Clips.ContainsKey("attack-east"), "职业独有的动作也要带上");

    // 原定义不能被改（合并返回新对象）。
    run.Equal(2, hero.Clips.Count);
    run.False(hero.Clips.ContainsKey("attack-east"));

    run.True(ReferenceEquals(hero, StoryClassAppearance.Merge(hero, null)));
});

run.Case("职业与数值表对齐：每个职业都有基础五维与初始装备", () =>
{
    var root = ContentContentRoot();
    if (root is null) return;

    var catalog = JsonSerializer.Deserialize<StoryClassCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "story-classes.json")), SaveGameStore.JsonOptions)!;
    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")), SaveGameStore.JsonOptions)!;

    foreach (var info in catalog.Classes)
    {
        run.True(stats.Classes.ContainsKey(info.Id), $"数值表缺职业 {info.Id}");
        run.True(items.StartingEquipment.ContainsKey(info.Id), $"初始装备表缺职业 {info.Id}");
        run.True(items.StartingEquipmentOf(info.Id).Count > 0, $"{info.Id} 的初始装备是空的");
    }
});

static Func<double> DiceValues(params int[] values)
{
    var index = 0;
    return () => (values[Math.Min(index++, values.Length - 1)] - 1 + 0.5) / 99.0;
}

run.Case("宝箱记录：开箱只记一次、同图重建沿用、换图清空生成记录", () =>
{
    var visits = new ChestVisits();
    visits.EnterMap("shadow-camp");
    run.Equal(1, visits.Visit);

    var result = visits.Open("shadow-camp", "chest-1", new Vec2(64, 96),
        () => new Drops.ChestOpenResult([new Drops.DropReward.MoneyDrop(5)], ["enemy-ancient-mimic-1"]));
    run.True(result is not null, "第一次开箱要有结果");
    run.True(visits.IsOpened("shadow-camp", "chest-1"), "开过就是开过了");
    run.Null(visits.Open("shadow-camp", "chest-1", new Vec2(64, 96),
        () => new Drops.ChestOpenResult([], [])), "同一个箱子不能再开一次");

    run.Equal(1, visits.Spawns.Count);
    run.Equal("chest:1:shadow-camp:chest-1:0", visits.Spawns[0].Id);
    run.Equal("enemy-ancient-mimic-1", visits.Spawns[0].EnemyId);
    run.False(visits.Spawns[0].Respawn, "宝箱怪只活到本次访问结束");

    visits.EnterMap("shadow-camp");
    run.Equal(1, visits.Visit, "同图不算新访问");
    run.True(visits.IsOpened("shadow-camp", "chest-1"));
    run.Equal(1, visits.Spawns.Count);

    visits.EnterMap("shadow-forest-west");
    run.Equal(2, visits.Visit);
    run.False(visits.IsOpened("shadow-forest-west", "chest-1"));
    run.Equal(0, visits.Spawns.Count);
});

run.Case("一次性宝箱：按「地图:实体」跨访问保留，掷点抛错要撤回记录", () =>
{
    var visits = new ChestVisits();
    visits.EnterMap("mansion");
    run.True(visits.Open("mansion", "chest-a", Vec2.Zero,
        () => new Drops.ChestOpenResult([], []), oneTime: true) is not null);
    run.True(visits.IsOpened("mansion", "chest-a", oneTime: true));

    visits.EnterMap("village");
    visits.EnterMap("mansion");
    run.True(visits.IsOpened("mansion", "chest-a", oneTime: true), "换图往返之后仍算已消耗");
    run.Null(visits.Open("mansion", "chest-a", Vec2.Zero,
        () => new Drops.ChestOpenResult([], []), oneTime: true), "一次性宝箱只能领一次");

    var broken = new ChestVisits();
    broken.EnterMap("mansion");
    run.Throws(() => broken.Open("mansion", "chest-b", Vec2.Zero,
            () => throw new InvalidOperationException("宝箱掉落规则缺失"), oneTime: true), "宝箱掉落规则缺失");
    run.False(broken.IsOpened("mansion", "chest-b", true), "失败不应留下已消耗记录");
    run.True(broken.Open("mansion", "chest-b", Vec2.Zero,
        () => new Drops.ChestOpenResult([], []), oneTime: true) is not null, "撤回之后还能再开");
});

run.Case("出现判定：1..99 闭区间，优质宝箱没过第二道关就降级", () =>
{
    MapEntity Chest() => new()
    {
        Id = "improved-chest-1",
        Actor = "improved-treasure-box",
        Appear = new EntityAppearRule { AtMost = 29 },
        Downgrade = new EntityDowngradeRule { AtLeast = 60, Actor = "treasure-box" },
    };

    run.Null(ChestPlacement.RollPlacement(Chest(), DiceValues(30)), "掷出 30 超过 29，这次不摆");
    var appeared = ChestPlacement.RollPlacement(Chest(), DiceValues(29, 60));
    run.True(appeared is not null, "掷出 29 在 29 以内，要出现");
    run.Equal("improved-treasure-box", appeared!.Actor, "第二道关过了还是优质宝箱");

    var downgraded = ChestPlacement.RollPlacement(Chest(), DiceValues(29, 59));
    run.Equal("treasure-box", downgraded!.Actor, "第二道关没过降级成普通宝箱");
    run.Equal("improved-chest-1", downgraded.Id, "降级只换角色，摆放 id 不变");

    var plain = new MapEntity { Id = "merchant-1", Actor = "merchant" };
    run.True(ReferenceEquals(plain, ChestPlacement.RollPlacement(plain, DiceValues(1))));
});

run.Case("交互距离：24 像素曼哈顿、矩形间隙档、朝向判定", () =>
{
    var from = new Vec2(100, 100);
    run.True(ChestReach.NearInteractable(from, new Vec2(124, 100)), "24 像素以内算身边");
    run.False(ChestReach.NearInteractable(from, new Vec2(125, 100)), "25 像素就不算了");

    run.True(ChestReach.NearInteractive(from, new Vec2(164, 100), blocking: true), "间隙 48 → 刚好够得着");
    run.False(ChestReach.NearInteractive(from, new Vec2(165, 100), blocking: true), "间隙 49 → 够不着");
    run.False(ChestReach.NearInteractive(from, new Vec2(164, 100), blocking: false), "非阻挡对象只有 24 像素");

    run.True(ChestReach.FacesTarget(from, "east", new Vec2(140, 100)));
    run.False(ChestReach.FacesTarget(from, "north", new Vec2(140, 100)));
    run.True(ChestReach.FacesTarget(from, "south", new Vec2(100, 140)));
    run.True(ChestReach.FacesTarget(from, "west", new Vec2(60, 110)));
    run.True(ChestReach.FacesTarget(from, "east", new Vec2(100, 100)), "同一格两轴间隙相等，按东西算且 dx=0 视为朝东");
});

run.Case("宝箱掉落：按档位与等级结算，等级越界要报错", () =>
{
    var catalog = ParsedDropCatalog();
    var stats = new CharacterStatsCatalog();

    var loot = Drops.RollChestLoot(catalog, stats, "ordinary", 5, 5, Zero());
    run.Equal(1, loot.Rewards.Count);
    run.True(loot.Rewards[0] is Drops.DropReward.MoneyDrop, "普通宝箱这一档只有金币");

    run.Throws(() => Drops.RollChestLoot(catalog, stats, "ordinary", 0, 5, Zero()), "宝箱掉落等级无效");
    run.Throws(() => Drops.RollChestLoot(catalog, stats, "boss", 5, 5, Zero()), "宝箱掉落规则缺失");
});

run.Case("宝箱记录快照：开启、消耗与宝箱怪随存档往返", () =>
{
    var visits = new ChestVisits();
    visits.EnterMap("shadow-camp");
    visits.Open("shadow-camp", "chest-1", new Vec2(64, 96),
        () => new Drops.ChestOpenResult([], ["enemy-ancient-mimic-1"]));
    visits.SpawnBreakableEnemies("shadow-camp", "bush-1", ["enemy-bat-1", "enemy-bat-1"], new Vec2(32, 32));
    visits.SpawnEnemy("shadow-camp", "spring-1", "enemy-slime-1", new Vec2(48, 48));

    var state = visits.CaptureState();
    var json = JsonSerializer.Serialize(state, SaveGameStore.JsonOptions);
    var back = new ChestVisits();
    back.RestoreState(JsonSerializer.Deserialize<ChestVisitsState>(json, SaveGameStore.JsonOptions)!);

    run.Equal("shadow-camp", back.MapId);
    run.Equal(1, back.Visit);
    run.True(back.IsOpened("shadow-camp", "chest-1"));
    run.Equal(4, back.Spawns.Count, "宝箱怪 + 两只景物怪 + 泉水怪");
    run.Equal("breakable:1:shadow-camp:bush-1:1", back.Spawns[2].Id);
    run.Equal("spring:1:shadow-camp:spring-1", back.Spawns[3].Id);
});

run.Case("画面开关默认全开，只有显式写 false 才算关", () =>
{
    var storage = new MemoryStorage();
    var settings = new DisplaySettings(storage.Read, storage.Write);
    run.True(settings.Fps);
    run.True(settings.CriticalDecorations);

    storage.Set("display-settings.json", """{"fps":false}""");
    var half = new DisplaySettings(storage.Read, storage.Write);
    run.False(half.Fps);
    run.True(half.CriticalDecorations);

    storage.Set("display-settings.json", "{oops");
    var broken = new DisplaySettings(storage.Read, storage.Write);
    run.True(broken.Fps && broken.CriticalDecorations);
});

run.Case("画面开关写回存储，另一份实例读得到", () =>
{
    var storage = new MemoryStorage();
    var settings = new DisplaySettings(storage.Read, storage.Write);
    settings.SetFps(false);
    settings.SetCriticalDecorations(false);

    var text = storage.Read("display-settings.json") ?? string.Empty;
    run.True(text.Contains("\"fps\":false") && text.Contains("\"criticalDecorations\":false"));

    var reloaded = new DisplaySettings(storage.Read, storage.Write);
    run.True(!reloaded.Fps && !reloaded.CriticalDecorations);
});

run.Case("移动方式默认摇杆，改十字键会写盘并只通知一次", () =>
{
    var storage = new MemoryStorage();
    var settings = new ControlSettings(storage.Read, storage.Write);
    run.Equal(MoveControlStyle.Stick, settings.MoveStyle);

    var notified = 0;
    using var subscription = settings.Subscribe(_ => notified += 1);
    settings.SetMoveStyle(MoveControlStyle.Dpad);
    run.Equal(1, notified);
    settings.SetMoveStyle(MoveControlStyle.Dpad);
    run.Equal(1, notified, "同值重复设置不再通知");

    var reloaded = new ControlSettings(storage.Read, storage.Write);
    run.Equal(MoveControlStyle.Dpad, reloaded.MoveStyle);
    run.True((storage.Read("control-settings.json") ?? string.Empty).Contains("\"moveStyle\":\"dpad\""));

    storage.Set("control-settings.json", """{"moveStyle":"wheel"}""");
    run.Equal(MoveControlStyle.Stick, new ControlSettings(storage.Read, storage.Write).MoveStyle);

    run.Equal(MoveControlStyle.Dpad, ControlSettings.Next(MoveControlStyle.Stick));
    run.Equal(MoveControlStyle.FourWay, ControlSettings.Next(MoveControlStyle.Dpad));
    run.Equal(MoveControlStyle.Stick, ControlSettings.Next(MoveControlStyle.FourWay));
    run.Equal("虚拟摇杆", ControlSettings.Describe(MoveControlStyle.Stick));
    run.Equal("十字按键", ControlSettings.Describe(MoveControlStyle.Dpad));
    run.Equal("四向移动", ControlSettings.Describe(MoveControlStyle.FourWay));

    settings.SetMoveStyle(MoveControlStyle.FourWay);
    run.Equal(MoveControlStyle.FourWay, new ControlSettings(storage.Read, storage.Write).MoveStyle,
        "四向移动要能存能读");
    run.True((storage.Read("control-settings.json") ?? string.Empty).Contains("\"moveStyle\":\"fourway\""));
});

run.Case("四向移动：任意方向压到上下左右，45° 边界归竖轴", () =>
{
    run.Equal(Vec2.Zero, MoveDirection.Snap(Vec2.Zero), "不动就是不动");
    run.Equal(new Vec2(1f, 0f), MoveDirection.Snap(new Vec2(1f, 0.2f)), "偏右");
    run.Equal(new Vec2(-1f, 0f), MoveDirection.Snap(new Vec2(-1f, -0.2f)), "偏左");
    run.Equal(new Vec2(0f, 1f), MoveDirection.Snap(new Vec2(0.2f, 1f)), "偏下");
    run.Equal(new Vec2(0f, -1f), MoveDirection.Snap(new Vec2(-0.2f, -1f)), "偏上");

    run.Equal(new Vec2(0f, 1f), MoveDirection.Snap(new Vec2(1f, 1f)), "右下 45° 走竖轴");
    run.Equal(new Vec2(0f, -1f), MoveDirection.Snap(new Vec2(-1f, -1f)), "左上 45° 走竖轴");

    run.True(!MoveDirection.IsCardinal(MoveControlStyle.Stick), "摇杆不压方向");
    run.True(MoveDirection.IsCardinal(MoveControlStyle.Dpad), "十字键压方向");
    run.True(MoveDirection.IsCardinal(MoveControlStyle.FourWay), "四向移动压方向");
});

run.Case("键位解析：默认落点与覆盖项都按公式算", () =>
{
    var viewport = new ControlViewport(960f, 640f, 0f, 0f, 0f, 0f);
    var defaults = ControlLayoutResolver.Resolve(viewport);
    run.Near(856f, defaults["attack"].X, 0.01f, "普攻中心 x = 960 - 104");
    run.Near(560f, defaults["attack"].Y, 0.01f, "普攻中心 y = 640 - 80");
    run.Near(129f, defaults["move"].X, 0.01f);
    run.Near(511f, defaults["move"].Y, 0.01f);
    run.Near(856f + 33f, defaults["slot-5"].X, 0.01f, "第六格按 SHORTCUT_OFFSETS 摆");

    var moved = ControlLayoutResolver.Resolve(viewport,
        new Dictionary<string, (float X, float Y)> { ["attack"] = (0.5f, 0.5f) });
    run.Near(480f, moved["attack"].X, 0.01f);
    run.Near(320f, moved["attack"].Y, 0.01f);
});

run.Case("键位覆盖项被限制在安全区内，归一化坐标可往返", () =>
{
    var viewport = new ControlViewport(960f, 640f, 0f, 0f, 0f, 0f);

    var clamped = ControlLayoutResolver.Resolve(viewport,
        new Dictionary<string, (float X, float Y)> { ["attack"] = (0f, 0f) });
    run.Near(79.5f, clamped["attack"].X, 0.01f);
    run.Near(80f, clamped["attack"].Y, 0.01f);

    var normalized = ControlLayoutResolver.Normalize(viewport, "attack", 600f, 400f);
    run.Near(600f / 960f, normalized.X, 0.001f);
    run.Near(400f / 640f, normalized.Y, 0.001f);
    var round = ControlLayoutResolver.Resolve(viewport,
        new Dictionary<string, (float X, float Y)> { ["attack"] = normalized });
    run.Near(600f, round["attack"].X, 0.01f);
    run.Near(400f, round["attack"].Y, 0.01f);
});

run.Case("键位偏好：非法点被忽略，重置清空覆盖项", () =>
{
    var storage = new MemoryStorage();
    var preferences = new ControlLayoutPreferences(storage.Read, storage.Write);
    var notified = 0;
    using var subscription = preferences.Subscribe(() => notified += 1);

    preferences.Set("attack", 0.5f, 0.5f);
    run.Equal(1, notified);
    run.True(preferences.Snapshot.ContainsKey("attack"));

    preferences.Set("attack", 1.5f, 0.5f);
    preferences.Set("nope", 0.5f, 0.5f);
    run.Equal(1, notified, "非法点与陌生 id 都直接忽略");

    preferences.Reset();
    run.Equal(0, preferences.Snapshot.Count);
    run.Equal(2, notified);
});

run.Case("按键大小：只缩放尺寸与间距，锚点不挪（贴边才内收）", () =>
{
    var viewport = new ControlViewport(960f, 640f, 0f, 0f, 0f, 0f);
    var normal = ControlLayoutResolver.Resolve(viewport);
    var bigger = ControlLayoutResolver.Resolve(viewport, null, 1.3f);

    run.Near(856f, bigger["attack"].X, 0.01f, "放大后普攻横向不挪（右边还留得出余量）");
    run.Near(ControlLayouts.AttackWidth * 1.3f, bigger["attack"].Width, 0.01f, "普攻按 130% 铺开");
    run.Near(ControlLayouts.AttackWidth, normal["attack"].Width, 0.01f, "默认档就是原尺寸");

    // 竖直方向 100% 时本来就贴底（560+80=640），放大后要按放大后的半高往里收，不能被切掉。
    run.Near(560f, normal["attack"].Y, 0.01f, "默认档正好贴底边");
    run.Near(640f - ControlLayouts.AttackHeight * 1.3f / 2f, bigger["attack"].Y, 0.01f,
        "放大后仍贴底边，不切出去");

    run.Near(856f + 33f * 1.3f, bigger["slot-5"].X, 0.01f, "第六格跟着散开");
    run.Near(560f - 123f * 1.3f, bigger["slot-5"].Y, 0.01f);
    run.Near(94f * 1.3f, bigger["slot-5"].Width, 0.01f);
    run.True(100f * 1.3f >= 94f * 1.3f, "相邻两格的间距大于格子边长，六格不会叠住");

    var smaller = ControlLayoutResolver.Resolve(viewport, null, 0.7f);
    run.Near(856f - 342f * 0.7f, smaller["slot-0"].X, 0.01f, "缩小时格子往普攻收拢");

    run.Near(129f, normal["move"].X, 0.01f);
    run.Near(210f * 1.3f / 2f, bigger["move"].X, 0.01f, "摇杆底盘放大后贴着左边");
    run.Near(640f - 210f * 1.3f / 2f, bigger["move"].Y, 0.01f, "底边同理");
    run.Near(210f * 1.3f, bigger["move"].Width, 0.01f, "摇杆底盘放大");

    var clamped = ControlLayoutResolver.Resolve(viewport,
        new Dictionary<string, (float X, float Y)> { ["attack"] = (0f, 0f) }, 1.3f);
    run.Near(ControlLayouts.AttackWidth * 1.3f / 2f, clamped["attack"].X, 0.01f);

    var normalized = ControlLayoutResolver.Normalize(viewport, "slot-0", 700f, 500f, null, 1.3f);
    var round = ControlLayoutResolver.Resolve(viewport,
        new Dictionary<string, (float X, float Y)> { ["slot-0"] = normalized }, 1.3f);
    run.Near(700f, round["slot-0"].X, 0.01f);
    run.Near(500f, round["slot-0"].Y, 0.01f);
});

run.Case("按键大小：一档 10%，夹在 70%–140%，存盘可读回", () =>
{
    var storage = new MemoryStorage();
    var preferences = new ControlLayoutPreferences(storage.Read, storage.Write);
    run.Near(1f, preferences.SizeScale, 0.001f, "默认 100%");

    preferences.NudgeSizeScale(1);
    run.Near(1.1f, preferences.SizeScale, 0.001f);
    preferences.NudgeSizeScale(-1);
    preferences.NudgeSizeScale(-1);
    run.Near(0.9f, preferences.SizeScale, 0.001f);

    for (var step = 0; step < 20; step += 1) preferences.NudgeSizeScale(1);
    run.Near(ControlLayoutPreferences.MaxSizeScale, preferences.SizeScale, 0.001f);
    for (var step = 0; step < 40; step += 1) preferences.NudgeSizeScale(-1);
    run.Near(ControlLayoutPreferences.MinSizeScale, preferences.SizeScale, 0.001f);

    preferences.SetSizeScale(0.93f);
    run.Near(0.9f, preferences.SizeScale, 0.001f, "非整档的数值对齐到最近的档位");

    preferences.SetSizeScale(1.3f);
    var reloaded = new ControlLayoutPreferences(storage.Read, storage.Write);
    run.Near(1.3f, reloaded.SizeScale, 0.001f);

    reloaded.Set("attack", 0.5f, 0.5f);
    reloaded.Reset();
    run.Near(1f, reloaded.SizeScale, 0.001f, "恢复默认连大小一起回 100%");
    run.Equal(0, reloaded.Snapshot.Count);
});

run.Case("按键大小：旧档没有这个字段就是 100%，坏值不牵连位置", () =>
{
    var storage = new MemoryStorage();
    storage.Write("control-layout.json",
        "{\"version\":1,\"positions\":{\"attack\":{\"x\":0.5,\"y\":0.5}}}");
    var old = new ControlLayoutPreferences(storage.Read, storage.Write);
    run.Near(1f, old.SizeScale, 0.001f, "旧档按默认大小读");
    run.True(old.Snapshot.ContainsKey("attack"), "位置照旧生效");

    storage.Write("control-layout.json",
        "{\"version\":1,\"sizeScale\":9.5,\"positions\":{\"attack\":{\"x\":0.5,\"y\":0.5}}}");
    var broken = new ControlLayoutPreferences(storage.Read, storage.Write);
    run.Near(ControlLayoutPreferences.MaxSizeScale, broken.SizeScale, 0.001f);
    run.True(broken.Snapshot.ContainsKey("attack"), "位置不受大小字段影响");
});

static EnemyDefinition EnemyDef(string id, int? level, int maxHealth, int physicalAttack,
    int defense = 0, int hitBonus = 0, int evasionBonus = 0) => new()
{
    Id = id,
    Name = id,
    Level = level,
    MaxHealth = maxHealth,
    Attributes = new EnemyAttributes
    {
        PhysicalAttack = physicalAttack,
        Defense = defense,
        HitBonus = hitBonus,
        EvasionBonus = evasionBonus,
    },
};

static EnemySpawn Spawn(string id, int? tierLevel = null, bool breakable = false, bool respawn = true) => new()
{
    Id = id, EnemyId = $"enemy-{id}", X = 100, Y = 200, TierLevel = tierLevel,
    BreakableEligible = breakable, Respawn = respawn ? null : false,
};

run.Case("敌人数值：等级换算、取整口径与经验修正", () =>
{
    var bat = EnemyDef("enemy-bat-1", 5, 120, 10, defense: 5);
    var stats = EnemyStatsCalculator.Calculate(bat, new EnemyStatsOptions(PlayerLevel: 1));

    run.Equal(5, stats.Level);
    run.False(stats.SceneLevel, "内容里有等级就不算关卡等级");
    run.Equal(120, stats.MaxHealth);
    run.Equal(10, stats.PhysicalAttack);
    run.Equal(60, stats.HitBase, "命中基值 = 等级×4+40");
    run.Equal(15, stats.EvasionBase, "闪避基值 = 等级+10");
    run.Equal(900, stats.HitRate, "(60×1000+20000)/(60+5+35) + 0 + 100");
    run.Equal(174, stats.EvasionRate, "1000 - (5000×5+1000000)/(25+15+1200)");
    run.Equal(30, stats.PhysicalResistance, "1000×5/(5+20×8)");
    run.Equal(220, stats.BaseExperience, "物攻×10 + 生命上限");
    run.Equal(308, stats.Experience, "主角 1 级、敌人 5 级：+4 档");
});

run.Case("敌人数值：队伍人数与精英系数", () =>
{
    var bat = EnemyDef("enemy-bat-1", 5, 120, 10, defense: 5);

    var party = EnemyStatsCalculator.Calculate(bat,
        new EnemyStatsOptions(PartySize: 2, PlayerLevel: 1));
    run.Equal(144, party.MaxHealth, "两人队：生命 ×1.2");
    run.Equal(12, party.PhysicalAttack, "两人队：物攻 ×1.2");
    run.Equal(246, party.Experience, "经验 ×0.8 后再按等级差修正");

    var elite = EnemyStatsCalculator.Calculate(bat, new EnemyStatsOptions(Elite: true));
    run.Equal(432, elite.MaxHealth, "精英：生命 ×3.6");
    run.Equal(12, elite.PhysicalAttack, "精英：物攻 ×1.2");

    var noLevel = EnemyStatsCalculator.Calculate(EnemyDef("enemy-bush-1", null, 1, 0),
        new EnemyStatsOptions(SceneLevel: 7));
    run.True(noLevel.SceneLevel, "内容没写等级时按关卡等级标记");
    run.Equal(7, noLevel.Level);
});

run.Case("敌人数值：越界参数要报错", () =>
{
    var bat = EnemyDef("enemy-bat-1", 5, 120, 10);
    run.Throws(() => EnemyStatsCalculator.Calculate(bat, new EnemyStatsOptions(Level: 0)), "怪物等级");
    run.Throws(() => EnemyStatsCalculator.Calculate(bat, new EnemyStatsOptions(Level: 108)), "怪物等级");
    run.Throws(() => EnemyStatsCalculator.Calculate(bat, new EnemyStatsOptions(PartySize: 4)), "队伍人数");
    run.Throws(() => EnemyStatsCalculator.Calculate(bat, new EnemyStatsOptions(PlayerLevel: 106)), "主角等级");
});

run.Case("敌人名册：登记、改血、换站位与快照往返", () =>
{
    var roster = new EnemyRoster();
    var bat = EnemyDef("enemy-bat-1", 3, 100, 10);

    var placed = roster.PlaceEnemy("enemy-bat-1", 10, 20);
    run.Equal("enemy-1", placed.Key, "实例键按序号分配");
    run.Equal(1, roster.Enemies.Count);

    roster.MoveEnemy(placed.Key, 30, 40);
    run.Equal(30f, roster.Enemies[0].X);

    var snapshot = roster.EnemySnapshot(placed.Key, bat, playerLevel: 1);
    run.Equal(100, snapshot!.MaxHealth);
    run.Equal(100, snapshot.Health, "没写过血量就按上限");

    roster.SetEnemyHealth(placed.Key, 40, bat);
    run.Equal(40, roster.EnemySnapshot(placed.Key, bat)!.Health);
    roster.SetEnemyHealth(placed.Key, 9999, bat);
    run.Equal(100, roster.EnemySnapshot(placed.Key, bat)!.Health, "血量要夹到上限");

    var state = roster.CaptureState();
    var other = new EnemyRoster();
    other.RestoreState(state);
    run.Equal(1, other.Enemies.Count);
    run.Equal(100, other.EnemySnapshot(placed.Key, bat)!.Health, "快照带得回当前血量（已被夹到上限）");

    other.RemoveEnemy(placed.Key);
    run.Equal(0, other.Enemies.Count);
});

run.Case("档位选择：按主角等级取最高不超出的档", () =>
{
    WorldSpawn Tier(string id, int tier) => new(id, $"enemy-{id}", 0, 0, tier);

    var raw = new[] { Tier("a", 5), Tier("b", 5), Tier("c", 43), Tier("d", 94) };
    run.Equal(2, WorldEnemyRules.SelectMapSpawns(raw, [], 1).Count, "1 级取最低档");
    run.Equal("c", WorldEnemyRules.SelectMapSpawns(raw, [], 43)[0].Id);
    run.Equal("d", WorldEnemyRules.SelectMapSpawns(raw, [], 100)[0].Id);

    var quest = WorldEnemyRules.SelectMapSpawns(raw, [], 1,
        new MapSpawnSelectionOptions(new HashSet<string> { "enemy-d" }));
    run.True(quest.Any(spawn => spawn.Id == "d"), "任务敌人要保留");

    // 选中档在当前剧情下全不可生成时，只回退到更低的一档。
    var fallback = WorldEnemyRules.SelectMapSpawns(raw, [], 43,
        new MapSpawnSelectionOptions(null, spawn => spawn.Id == "a"));
    run.Equal(2, fallback.Count, "回退到更低一档时，那一档的槽整批补上");
    run.True(fallback.All(spawn => spawn.Id is "a" or "b"));
});

run.Case("世界名册：档位、可破坏物替换与宝箱怪都在同一份名单里", () =>
{
    var session = new Session();
    session.EnterMap("test-map", new Vec2(0, 0), "south");

    var mapSpawns = new[]
    {
        new EnemySpawn { Id = "s1", EnemyId = "enemy-bat-1", X = 10, Y = 10, TierLevel = 5, BreakableEligible = true },
        new EnemySpawn { Id = "s2", EnemyId = "enemy-bat-1", X = 20, Y = 20, TierLevel = 5 },
        new EnemySpawn { Id = "s3", EnemyId = "enemy-bat-1", X = 30, Y = 30, TierLevel = 40 },
    };

    var roster = new WorldEnemyRoster(session, "test-map", mapSpawns, [], heroLevel: 1,
        breakableSpawn: "jar", random: () => 0.01);

    run.Equal(2, roster.Spawns.Count, "1 级只取 5 档的两个槽");
    run.Equal(2, roster.Enemies.Count, "名册成员与刷怪点一致");
    run.Equal("enemy-jar-1", roster.Source("s1")!.EnemyId, "可破坏物槽换成本图的罐子怪");
    run.Equal("enemy-bat-1", roster.Source("s2")!.EnemyId, "不可替换的槽保持原怪");

    session.Chests.SpawnEnemy("test-map", "spring-1", "enemy-bat-1", new Vec2(50, 50));
    var (added, removed) = roster.Refresh();
    run.Equal(1, added.Count, "宝箱怪进名单");
    run.Equal(0, removed.Count);
    run.True(roster.Spawns.Any(spawn => spawn.Id.StartsWith("spring:")), "生成记录也在刷怪点里");
});

run.Case("世界名册：击败后从名单撤下，复活计时到了再回来", () =>
{
    var session = new Session();
    session.EnterMap("test-map", new Vec2(0, 0), "south");

    var mapSpawns = new[] { new EnemySpawn { Id = "s1", EnemyId = "enemy-bat-1", X = 10, Y = 10 } };
    var roster = new WorldEnemyRoster(session, "test-map", mapSpawns, [], heroLevel: 1, breakableSpawn: null);
    run.Equal(1, roster.Enemies.Count);

    session.MarkEnemyDefeated("s1", respawn: true);
    var (added, removed) = roster.Refresh();
    run.Equal(0, added.Count);
    run.Equal(1, removed.Count, "死掉的怪要撤下");
    run.Equal("s1", removed[0]);

    session.Advance(Session.DefaultEnemyRespawnMs);
    var again = roster.Refresh();
    run.Equal(1, again.Added.Count, "计时到了重新上场");
    run.Equal("s1", again.Added[0].Key);
});

run.Case("敌人死亡状态：一次性生成点永久不回，换图也不清", () =>
{
    var session = new Session();
    session.EnterMap("first-map", new Vec2(0, 0), "south");
    var once = Spawn("once", respawn: false);
    var normal = Spawn("normal");

    run.True(session.CanSpawnEnemy(once));
    session.MarkEnemyDefeated("once", respawn: false);
    session.MarkEnemyDefeated("normal", respawn: true);
    run.False(session.CanSpawnEnemy(once));
    run.False(session.CanSpawnEnemy(normal));

    session.EnterMap("another-map", new Vec2(0, 0), "south");
    run.False(session.CanSpawnEnemy(once), "永久死亡跨图保留");
    run.True(session.CanSpawnEnemy(normal), "普通怪的死亡计时随换图清掉");

    var state = new SessionState
    {
        Enemies = new Dictionary<string, EnemyState>
        {
            ["once"] = new() { Health = 0, DeadPermanently = true },
            ["normal"] = new() { Health = 12, DeadUntilMs = 60000 },
        },
    };
    var json = JsonSerializer.Serialize(state, SaveGameStore.JsonOptions);
    var back = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;
    run.True(back.Enemies["once"].DeadPermanently);
    run.Equal(12, back.Enemies["normal"].Health);
    run.Equal(60000L, back.Enemies["normal"].DeadUntilMs);

    var legacy = JsonSerializer.Deserialize<SessionState>("""{"Money":0}""", SaveGameStore.JsonOptions)!;
    run.Equal(0, legacy.Enemies.Count, "旧档没有 enemies 字段就按空表");
});

run.Case("剧情世界：登记副本顶掉静态副本，过场副本只活一次访问", () =>
{
    var world = new StoryWorldState();
    var baseList = new[]
    {
        new MapEntity { Id = "npc-1", Actor = "guard", X = 10f, Y = 20f, CharacterId = "char-1" },
        new MapEntity { Id = "npc-2", Actor = "guard", X = 30f, Y = 40f, CharacterId = "char-2" },
    };

    world.Apply(new TravelCommand(new StoryLocation { Map = "map-a", X = 0f, Y = 0f }));
    run.Equal(1, world.Visit, "第一次转场就是第 1 次访问");

    world.Apply(new PlaceCommand(new StoryEntityState
    {
        Id = "quest-npc-located-char-1", Map = "map-a", Actor = "guard", CharacterId = "char-1",
        Authority = PlacementAuthority.Registered, X = 50f, Y = 60f,
    }));

    var projected = world.ProjectEntities("map-a", baseList);
    run.Equal(2, projected.Count, "登记副本顶掉静态副本后仍是两个人物");
    run.Equal(50f, projected.First(entity => entity.CharacterId == "char-1").X, "位置按登记副本");
    run.Equal("npc-2", projected.Single(entity => entity.CharacterId == "char-2").Id);
    run.True(world.IsPlaced("quest-npc-located-char-1"));
    run.False(world.IsPlaced("npc-1"), "静态摆放不算剧情登记");

    world.Apply(new PlaceCommand(new StoryEntityState
    {
        Id = "story-temp", Map = "map-a", Actor = "guard", CharacterId = "char-3",
        X = 1f, Y = 2f,
    }, VisitScope: true));
    run.True(world.ProjectEntities("map-a", []).Any(entity => entity.Id == "story-temp"));
    world.Apply(new TravelCommand(new StoryLocation { Map = "map-b", X = 0f, Y = 0f }));
    run.False(world.ProjectEntities("map-a", []).Any(entity => entity.Id == "story-temp"),
        "换图后过场副本作废");

    var ahead = new StoryWorldState();
    ahead.Apply(new TravelCommand(new StoryLocation { Map = "map-a", X = 0f, Y = 0f }));
    ahead.Apply(new PlaceCommand(new StoryEntityState
    {
        Id = "story-ahead", Map = "map-b", Actor = "guard", CharacterId = "char-4", X = 5f, Y = 6f,
    }, VisitScope: true));
    ahead.Apply(new TravelCommand(new StoryLocation { Map = "map-b", X = 0f, Y = 0f }));
    run.Equal(2, ahead.Visit);
    run.True(ahead.ProjectEntities("map-b", []).Any(entity => entity.Id == "story-ahead"),
        "提前为下一张图摆的过场人物进图时在位");
});

run.Case("剧情世界：remove-character 与 actor-visibility 挡住静态副本", () =>
{
    var world = new StoryWorldState();
    world.Apply(new TravelCommand(new StoryLocation { Map = "map-a", X = 0f, Y = 0f }));

    var baseList = new[]
    {
        new MapEntity { Id = "npc-1", Actor = "guard", X = 10f, Y = 20f, CharacterId = "char-1" },
        new MapEntity { Id = "npc-2", Actor = "merchant", X = 30f, Y = 40f, CharacterId = "char-2" },
    };

    world.Apply(new RemoveCharacterCommand("char-1", "map-a", true));
    var afterRemove = world.ProjectEntities("map-a", baseList);
    run.False(afterRemove.Any(entity => entity.CharacterId == "char-1"), "静态副本被挡住");
    run.True(afterRemove.Any(entity => entity.CharacterId == "char-2"));
    run.False(world.ActorVisible("map-a", "guard", "char-1"));

    // 重新登记要撤销这条标记，否则重新登场的人物永远不出现。
    world.Apply(new PlaceCommand(new StoryEntityState
    {
        Id = "quest-npc-located-char-1", Map = "map-a", Actor = "guard", CharacterId = "char-1",
        X = 12f, Y = 22f, Authority = PlacementAuthority.Registered,
    }));
    run.True(world.ActorVisible("map-a", "guard", "char-1"), "重新登记撤销不在场标记");
    run.True(world.ProjectEntities("map-a", baseList).Any(entity => entity.CharacterId == "char-1"));

    world.Apply(new ActorVisibilityCommand("map-a", "merchant", "char-2", false));
    var hidden = world.ProjectEntities("map-a", baseList);
    run.False(hidden.Any(entity => entity.CharacterId == "char-2"));
    world.Apply(new ActorVisibilityCommand("map-a", "merchant", "char-2", true));
    run.True(world.ProjectEntities("map-a", baseList).Any(entity => entity.CharacterId == "char-2"));

    run.True(world.Visible("quest-npc-located-char-1"));
    world.Apply(new ToggleEntityCommand("hide", "quest-npc-located-char-1"));
    run.False(world.Visible("quest-npc-located-char-1"));
    world.Apply(new ToggleEntityCommand("show", "quest-npc-located-char-1"));
    run.True(world.Visible("quest-npc-located-char-1"));
    world.Apply(new ToggleEntityCommand("remove", "quest-npc-located-char-1"));
    run.False(world.Visible("quest-npc-located-char-1"));
    run.False(world.ProjectEntities("map-a", baseList)
        .Any(entity => entity.Id == "quest-npc-located-char-1"));
});

run.Case("剧情世界：敌人禁用、换位与按名移除", () =>
{
    var world = new StoryWorldState();
    world.Apply(new TravelCommand(new StoryLocation { Map = "map-a", X = 0f, Y = 0f }));

    run.True(world.EnemyAvailable("map-a", "enemy-bat-1"));
    run.True(world.EnemyAvailable("map-b", "enemy-bat-1"), "按图禁用只影响那一张图");
    world.Apply(new EnemyAvailabilityCommand("map-a", "enemy-bat-1", false));
    run.False(world.EnemyAvailable("map-a", "enemy-bat-1"));
    world.Apply(new EnemyAvailabilityCommand("map-a", "enemy-bat-1", true));
    run.True(world.EnemyAvailable("map-a", "enemy-bat-1"));

    world.Apply(new EnemyLocationCommand("enemy-bat-1",
        new StoryLocation { Map = "map-a", X = 12f, Y = 34f }));
    var spawns = world.EnemySpawns("map-a");
    run.Equal(1, spawns.Count);
    run.Equal("story-located-enemy-bat-1", spawns[0].Id, "剧情落点有自己的稳定 id");
    run.Equal(12f, spawns[0].X);
    run.Equal(0, world.EnemySpawns("map-b").Count, "落点按图归属");

    world.Apply(new EnemyLocationCommand("enemy-bat-1", null));
    run.Equal(0, world.EnemySpawns("map-a").Count, "落点可以撤销");

    world.Apply(new RemoveEnemyCommand("enemy-bat-1", "map-a", true));
    run.Equal(1, world.PendingEnemyRemovals.Count);
    run.Equal("enemy-bat-1", world.PendingEnemyRemovals[0].EnemyId);
    run.True(world.PendingEnemyRemovals[0].All);
    world.ConsumeEnemyRemovals();
    run.Equal(0, world.PendingEnemyRemovals.Count, "取走之后清空");

    run.Throws(() => world.Apply(new PlaceCommand(new StoryEntityState
    {
        Id = "", Map = "map-a", Actor = "guard", X = 1f, Y = 1f,
    })), "剧情角色位置无效", "缺 id 的登记要当场报错");
});

run.Case("剧情世界：出口、旗标、选择、外观与队伍状态", () =>
{
    var world = new StoryWorldState();
    var exit = new MapExit
    {
        X = 0f, Y = 0f, Width = 16f, Height = 16f, Destination = "map-b", Spawn = new MapPoint(),
    };

    run.True(world.ExitEnabled("map-a", exit), "没被关过的出口是开的");
    world.Apply(new ExitCommand(StoryWorldState.StoryExitId("map-a", exit), false));
    run.False(world.ExitEnabled("map-a", exit));
    world.Apply(new ExitCommand(StoryWorldState.StoryExitId("map-a", exit), true));
    run.True(world.ExitEnabled("map-a", exit));

    run.False(world.HasFlag("met-earl"));
    world.Apply(new FlagCommand("met-earl", true));
    run.True(world.HasFlag("met-earl"));
    world.Apply(new FlagCommand("met-earl", false));
    run.False(world.HasFlag("met-earl"));

    run.Equal(null, world.Choice("ask"));
    world.Apply(new ChoiceCommand("ask", "yes"));
    run.Equal("yes", world.Choice("ask"));
    run.Equal("yes", world.ChoiceValues()["ask"]);

    run.Equal("guard", world.ActorAppearance("guard"), "没换过外观就是自己");
    world.Apply(new ActorAppearanceCommand("guard", "guard-alt"));
    run.Equal("guard-alt", world.ActorAppearance("guard"));

    world.Apply(new PartyBuffCommand("shield", 3));
    run.Equal(1, world.PendingPartyBuffs.Count);
    run.Equal(3, world.PendingPartyBuffs[0].Strength);
    world.ConsumePartyBuffs(1);
    run.Equal(0, world.PendingPartyBuffs.Count, "取走之后清空");

    var revision = world.Revision;
    world.Apply(new FlagCommand("another", true));
    run.True(world.Revision > revision);
});

run.Case("剧情世界：遭遇战状态与非法快照", () =>
{
    var world = new StoryWorldState();
    world.Apply(new EncounterCommand(new StoryEncounter
    {
        Id = "enc-1", Map = "map-a", DefinitionId = "boss-1",
        Spawns = [new StoryEncounterSpawn { Id = "s1", EnemyId = "enemy-bat-1", X = 1f, Y = 2f }],
    }));
    run.Equal(0, world.ActiveEncounters.Count, "刚建好的遭遇还没开打");
    world.Apply(new EncounterResultCommand("enc-1", "active"));
    run.Equal(1, world.ActiveEncounters.Count);
    run.Equal("active", world.EncounterStatuses()["enc-1"].Status);
    world.Apply(new EncounterResultCommand("enc-1", "won"));
    run.Equal(0, world.ActiveEncounters.Count);

    run.Throws(() => world.Apply(new EncounterResultCommand("enc-1", "lost")),
        "剧情遭遇结果冲突", "已经判过的遭遇不能改判");
    run.Throws(() => world.Apply(new EncounterCommand(new StoryEncounter
    {
        Id = "enc-1", Map = "map-a",
        Spawns = [new StoryEncounterSpawn { Id = "s1", EnemyId = "enemy-bat-1", X = 0f, Y = 0f }],
    })), "剧情遭遇实例已经存在");
    run.Throws(() => world.Apply(new EncounterResultCommand("missing", "won")),
        "剧情遭遇不存在");

    run.Throws(() => new StoryWorldState().RestoreState(new StoryWorldSnapshot { MapVisit = -1 }),
        "剧情地图访问版本无效", "负的访问号要拒绝");
    run.Throws(() => new StoryWorldState().RestoreState(new StoryWorldSnapshot
    {
        Encounters = [new StoryEncounter { Id = "enc-2", Map = "", Spawns = [] }],
    }), "剧情遭遇快照无效", "缺地图的遭遇要拒绝");
    run.Throws(() => new StoryWorldState().RestoreState(new StoryWorldSnapshot
    {
        Entities = [new StoryEntityState { Id = "e1", Facing = "northeast" }],
    }), "剧情角色快照朝向无效");
});

run.Case("剧情世界：快照 JSON 往返", () =>
{
    var world = new StoryWorldState();
    world.Apply(new TravelCommand(new StoryLocation { Map = "map-a", X = 4f, Y = 8f, Facing = "east" }));
    world.Apply(new FlagCommand("met-earl", true));
    world.Apply(new ChoiceCommand("ask", "yes"));
    world.Apply(new ActorAppearanceCommand("guard", "guard-alt"));
    world.Apply(new PartyBuffCommand("shield", 3));
    world.Apply(new PlaceCommand(new StoryEntityState
    {
        Id = "quest-npc-located-char-1", Map = "map-a", Actor = "guard", CharacterId = "char-1",
        X = 50f, Y = 60f, Facing = "north", Authority = PlacementAuthority.Registered,
    }));
    world.Apply(new EnemyLocationCommand("enemy-bat-1",
        new StoryLocation { Map = "map-a", X = 12f, Y = 34f }, NoRespawn: true));
    world.Apply(new RemoveEnemyCommand("enemy-bat-2", null, false));

    var json = JsonSerializer.Serialize(world.CaptureState(), SaveGameStore.JsonOptions);
    var copy = new StoryWorldState();
    copy.RestoreState(JsonSerializer.Deserialize<StoryWorldSnapshot>(json, SaveGameStore.JsonOptions)!);

    run.Equal(world.Visit, copy.Visit, "访问号往返");
    run.Equal("map-a", copy.CurrentLocation!.Map);
    run.Equal("east", copy.CurrentLocation.Facing);
    run.True(copy.HasFlag("met-earl"));
    run.Equal("yes", copy.Choice("ask"));
    run.Equal("guard-alt", copy.ActorAppearance("guard"));
    run.Equal(1, copy.PendingPartyBuffs.Count);
    run.Equal("north", copy.Entity("quest-npc-located-char-1")!.Facing);
    run.Equal(1, copy.EnemySpawns("map-a").Count);
    run.Equal(false, copy.EnemySpawns("map-a")[0].Respawn, "不刷新的落点往返后仍是 false");
    run.Equal(1, copy.PendingEnemyRemovals.Count);
    run.False(copy.PendingEnemyRemovals[0].All);
    var reprojected = copy.ProjectEntities("map-a", []);
    run.Equal(1, reprojected.Count, "重投影只剩快照里登记的那一个");
    run.Equal("quest-npc-located-char-1", reprojected[0].Id);

    var legacy = JsonSerializer.Deserialize<SessionState>("""{"Money":0}""", SaveGameStore.JsonOptions)!;
    run.Equal(null, legacy.StoryWorld, "旧档缺 storyWorld 就是空");
});

run.Case("进图条件摆放：条件成立才摆，且只活一次访问", () =>
{
    const string json = """
    {
      "start": { "map": "map-a", "x": 0, "y": 0, "actor": "kiyan" },
      "scripts": {},
      "quests": [],
      "mapEntryPlacements": [
        { "id": "entry-1", "characterId": "char-1", "actor": "guard", "map": "map-a",
          "x": 10, "y": 20, "facing": "east",
          "condition": { "type": "flag", "flag": "luna-here" } }
      ]
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    run.Equal(1, campaign.EntryPlacements.Count, "进图摆放要解析出来");
    run.Equal("east", campaign.EntryPlacements[0].Facing, "facing 也要解析");

    var session = new Session { Journal = new QuestJournal(campaign) };
    session.MapEntryPlacements = campaign.EntryPlacements;

    void Travel(string map) => session.StoryWorld.Apply(
        new TravelCommand(new StoryLocation { Map = map, X = 0f, Y = 0f }));

    Travel("map-a");
    session.EnterMap("map-a", new Vec2(0f, 0f), "south");
    run.Equal(null, session.StoryWorld.Entity("entry-1"), "条件不成立就不摆");

    session.Journal!.SetFlag("luna-here", true);
    Travel("map-b");
    session.EnterMap("map-b", new Vec2(0f, 0f), "south");
    Travel("map-a");
    session.EnterMap("map-a", new Vec2(0f, 0f), "south");
    var placed = session.StoryWorld.Entity("entry-1");
    run.True(placed is not null, "条件成立后进图要摆出来");
    run.Equal(session.StoryWorld.Visit, placed!.MapVisit, "记成当前访问号");
    run.Equal("east", placed.Facing);

    Travel("map-b");
    run.False(session.StoryWorld.ProjectEntities("map-a", []).Any(entity => entity.Id == "entry-1"),
        "换图后进图人物失效");
});

run.Case("剧本 waitEncounter：落成遭遇实例写进剧情世界，结果回填后才续走", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "prep",
          "nodes": [
            {
              "id": "prep",
              "presentation": [],
              "commands": [],
              "exit": {
                "type": "waitEncounter",
                "encounter": {
                  "id": "battle",
                  "map": "declared-map",
                  "mapFromStage": true,
                  "status": "prepared",
                  "spawns": [
                    { "id": "enemy-0", "enemyId": "enemy-bat-1", "x": 10, "y": 20 },
                    { "id": "enemy-1", "enemyId": "enemy-bat-1", "x": 30, "y": 40 }
                  ]
                },
                "outcomes": {
                  "won": { "script": "s", "node": "won" },
                  "lost": { "script": "s", "node": "lost" }
                }
              }
            },
            { "id": "won", "presentation": [{ "type": "dialogue", "text": "赢了" }], "commands": [], "exit": { "type": "end" } },
            { "id": "lost", "presentation": [{ "type": "dialogue", "text": "输了" }], "commands": [], "exit": { "type": "end" } }
          ]
        }
      }
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var world = new StoryWorldState();
    var director = new StoryDirector(campaign, new QuestJournal(), worldSink: new WorldStateSink(world));
    director.CurrentMap = "stage-map";
    director.Start("s");

    var step = director.Next();
    run.Equal(StoryStepKind.Encounter, step.Kind);
    run.Equal("s:prep:battle", step.Encounter!.Id, "实例 id＝剧本:节点:内容 id");
    run.Equal("battle", step.Encounter.DefinitionId, "内容里的 id 留着，重播也认得同一场战斗");
    run.Equal("stage-map", step.Encounter.Map, "mapFromStage 落到当前地图");
    run.Equal(2, step.Encounter.Spawns.Count);

    var encounter = world.Encounter("s:prep:battle")!;
    run.Equal("active", encounter.Status, "登记成 active，名册才会把怪布到场上");
    run.Equal("stage-map", encounter.Map);

    run.True(director.ResolveEncounter(true), "配了 won 分支就要接上");
    run.Equal("won", world.Encounter("s:prep:battle")!.Status, "结果写回剧情世界");
    run.Equal("赢了", StoryType.Text(director.Next().Item, "text"));
});

run.Case("剧本遭遇没有结果分支时判负也不卡住", () =>
{
    const string json = """
    {
      "scripts": {
        "s": {
          "entry": "prep",
          "nodes": [
            {
              "id": "prep",
              "presentation": [],
              "commands": [],
              "exit": {
                "type": "waitEncounter",
                "encounter": { "id": "battle", "map": "m", "spawns": [] },
                "outcomes": {}
              }
            }
          ]
        }
      }
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var world = new StoryWorldState();
    var director = new StoryDirector(campaign, new QuestJournal(), worldSink: new WorldStateSink(world));
    director.Start("s");
    run.Equal(StoryStepKind.Encounter, director.Next().Kind);

    run.False(director.ResolveEncounter(false), "没配 lost 就不能假装接上了");
    run.True(director.PendingEncounterId is null, "挂起的遭遇要放掉，否则下一场会被顶掉");
    // 结果仍然写回：遭遇状态必须离场，否则名册会一直挂着这场战斗的怪。
    run.Equal("lost", world.Encounter("s:prep:battle")!.Status);
});

run.Case("名册：剧情遭遇按实例 id 上场，全灭才算打完", () =>
{
    var session = new Session();
    session.EnterMap("test-map", new Vec2(0, 0), "south");
    session.StoryWorld.Apply(new EncounterCommand(new StoryEncounter
    {
        Id = "s:prep:battle",
        DefinitionId = "battle",
        Map = "test-map",
        Status = "active",
        Spawns =
        [
            new StoryEncounterSpawn { Id = "enemy-0", EnemyId = "enemy-bat-1", X = 10, Y = 10 },
            new StoryEncounterSpawn { Id = "enemy-1", EnemyId = "enemy-bat-1", X = 20, Y = 20 },
        ],
    }));

    var roster = new WorldEnemyRoster(session, "test-map", [], [], heroLevel: 1, breakableSpawn: null,
        story: new StoryWorldEnemyWorld(session.StoryWorld));

    run.Equal(2, roster.Spawns.Count, "遭遇的怪都进刷怪点");
    run.True(roster.Spawns.All(spawn => spawn.Id.StartsWith("s:prep:battle:")), "生成点 id 带实例前缀");
    run.False(roster.Spawns[0].Respawn, "遭遇怪是一次性的，打完不再刷");
    run.Equal(2, roster.Enemies.Count);
    run.Equal(0, roster.CompletedEncounters.Count, "还站着就不算打完");

    session.MarkEnemyDefeated("s:prep:battle:enemy-0", respawn: false);
    var first = roster.Refresh();
    run.Equal(1, first.Removed.Count, "死掉的撤下");
    run.Equal(0, roster.CompletedEncounters.Count, "还剩一只就不算打完");

    session.MarkEnemyDefeated("s:prep:battle:enemy-1", respawn: false);
    roster.Refresh();
    run.Equal(1, roster.CompletedEncounters.Count, "全灭即打完");

    session.StoryWorld.Apply(new EncounterResultCommand("s:prep:battle", "won"));
    roster.Refresh();
    run.Equal(0, roster.Spawns.Count, "打完的遭遇不再往场上布怪");
    run.Equal(0, roster.CompletedEncounters.Count);
});

run.Case("名册：被捕捉的生成点不算清场", () =>
{
    var campaign = new CampaignContent
    {
        Quests =
        [
            new QuestDefinition
            {
                Id = "quest-capture",
                Goals = [new QuestGoal { Id = "objective", Kind = "interact", Target = "npc-a", Quantity = 1 }],
                TargetChanges =
                [
                    new QuestTargetChange { Goal = "objective", EnemyId = "enemy-bat-1", Actor = "npc-a" },
                ],
            },
        ],
    };
    var journal = new QuestJournal(campaign);
    journal.Book.InstallTrustedState(new QuestBookState
    {
        Quests = [KeyValuePair.Create("quest-capture", new QuestProgress { Status = QuestStatus.Active })],
        RunningQuestIds = ["quest-capture"],
    });

    var session = new Session { Journal = journal };
    session.EnterMap("test-map", new Vec2(0, 0), "south");
    session.StoryWorld.Apply(new EncounterCommand(new StoryEncounter
    {
        Id = "s:prep:battle",
        DefinitionId = "battle",
        Map = "test-map",
        Status = "active",
        Spawns =
        [
            new StoryEncounterSpawn { Id = "enemy-0", EnemyId = "enemy-bat-1", X = 10, Y = 10 },
            new StoryEncounterSpawn { Id = "enemy-1", EnemyId = "enemy-bat-1", X = 20, Y = 20 },
        ],
    }));

    var roster = new WorldEnemyRoster(session, "test-map", [], [], heroLevel: 1, breakableSpawn: null,
        story: new StoryWorldEnemyWorld(session.StoryWorld));

    run.True(session.WeakenQuestTarget("enemy-bat-1",
        new Session.EnemySource("s:prep:battle:enemy-0", "test-map", 10, 10)), "第一只换成捕捉目标");
    run.True(session.WeakenQuestTarget("enemy-bat-1",
        new Session.EnemySource("s:prep:battle:enemy-1", "test-map", 20, 20)), "第二只换成捕捉目标");
    run.True(session.EnemyVitals("s:prep:battle:enemy-0").Capture is not null, "源生成点带 capture");
    run.Equal(0, roster.CompletedEncounters.Count, "被剧情接走的生成点不算清场");
});

run.Case("普攻序列：周期短于素材就截取，长于素材就压末帧", () =>
{
    run.Equal("0,1,2", string.Join(",", BasicAttackRules.AttackSequence(5, 3)));
    run.Equal("0,1,2,2,2", string.Join(",", BasicAttackRules.AttackSequence(3, 5)));
    run.Throws(() => BasicAttackRules.AttackSequence(0, 3), "普攻源动画帧数无效");
    run.Throws(() => BasicAttackRules.AttackSequence(3, 0), "普攻周期帧数无效");
});

run.Case("待机帧：素材范围内照常画动作，超出后按待机取模", () =>
{
    run.True(BasicAttackRules.IdlePoseFrame(3, 8, 14) is null, "还在素材范围内");
    run.Equal(1, BasicAttackRules.IdlePoseFrame(8, 8, 7), "8 % 7 = 1");
    run.True(BasicAttackRules.IdlePoseFrame(3, 8, 0) is null, "没有待机片段就不接管");
});

run.Case("出手帧：单击按 strikeFrame，双击按 strikes 各自取", () =>
{
    var single = FakeAttack(4);
    run.True(BasicAttackRules.Events(single, 3).Swing is false && BasicAttackRules.Events(single, 3).Strike is false);
    run.Equal(1, BasicAttackRules.StrikesAtFrame(single, 4).Count, "命中帧只派发一次");
    run.Equal("main-hand", BasicAttackRules.StrikesAtFrame(single, 4)[0]);

    var dual = FakeAttack(4, [
        new BasicAttackStrike { Frame = 2, Hand = "main-hand" },
        new BasicAttackStrike { Frame = 7, Hand = "offhand" },
    ]);
    run.Equal("main-hand", BasicAttackRules.StrikesAtFrame(dual, 2)[0]);
    run.Equal("offhand", BasicAttackRules.StrikesAtFrame(dual, 7)[0]);
    run.Equal(0, BasicAttackRules.StrikesAtFrame(dual, 4).Count, "配置了 strikes 时 strikeFrame 不再派发");
});

run.Case("普攻解析：真实职业按绑定取规则与四向序列", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("没找到 public/content，跳过普攻解析用例");
        return;
    }

    var battle = ReadBattle(root);
    run.True(battle.BasicAttacks.Count >= 90, $"普攻规则 {battle.BasicAttacks.Count} 份");
    run.True(battle.Classes.Length >= 6, $"职业 {battle.Classes.Length} 个");

    var knight = battle.Classes.First(@class => @class.Id == "dark-knight");
    var attack = BasicAttackRules.Resolve(battle.BasicAttacks, knight.Actor, knight.BasicAttack);
    run.Equal("longsword-slash", knight.BasicAttack!.ProfileId);
    run.Equal(10, attack.EffectiveCycleFrames, "长剑周期 10 帧");
    run.Equal(600, attack.DurationMs, "10 × 60 毫秒");
    run.Equal(4, attack.StrikeFrame, "长剑第 4 帧命中");
    run.Equal(4, attack.ApproachDistanceUnits);
    run.Equal("attack-south", attack.Clips["south"]);
    run.Equal(10, attack.Sequence["south"].Length, "攻击素材只有 8 帧，序列要补满 10 个逻辑帧");
    run.Equal(7, attack.Sequence["south"][8], "超出素材的部分重复引用末帧");

    var failures = battle.Enemies
        .Where(enemy => enemy.BasicAttack is not null &&
                        (enemy.BasicAttack.ProfileId is not { Length: > 0 } ||
                         !battle.BasicAttacks.ContainsKey(enemy.BasicAttack.ProfileId)))
        .Select(enemy => enemy.Id).ToArray();
    run.Equal(0, failures.Length, $"普攻规则缺失的敌人：{string.Join(",", failures)}");
});

run.Case("魔力宝石掉落与派发收尾：阈值、方向回退、破隐次序", () =>
{
    var pickup = new ManaPickupDefinition
    {
        PercentOfCollectorMaxMana = 20,
        HitDrop = new ManaHitDropDefinition
        {
            RollMaximum = 999,
            SkillThreshold = 10,
            WeaponThresholds = new Dictionary<string, int> { ["longsword"] = 70 },
            JitterPx = 0,
            Directions =
            [
                new ManaDropDirection { Name = "a", X = 10, Y = 0, Clip = new EffectClip { Frames = 9, FrameRate = 16.66f } },
                new ManaDropDirection { Name = "b", X = -10, Y = 0, Clip = new EffectClip { Frames = 9, FrameRate = 16.66f } },
            ],
        },
    };

    static ManaLanding? Roll(ManaPickupDefinition pickup, double[] dice, Func<Vec2, bool> reachable) =>
        ManaGems.RollManaGem(pickup, 13, "skill", null, new Vec2(100, 100), reachable, new Dice(dice).Next);

    var landed = Roll(pickup, [0.0], _ => true);
    run.True(landed is not null, "过阈值线就掉落");
    run.Equal(110, landed!.X, "按方向 a 的偏移落在 (110,100)");

    // 第一个方向不可达 → 回退到第二个方向。
    run.Equal(90, Roll(pickup, [0.0], point => point.X < 100)!.X, "回退到方向 b");
    // 全部不可达时仍然返回最后一次尝试的落点：宝石会落地，只是捡不到。
    run.Equal(90, Roll(pickup, [0.0], _ => false)!.X, "全部不可达也留下落点");

    run.True(Roll(pickup, [0.999], _ => true) is null, "越过阈值线不掉");
    run.True(ManaGems.RollManaGem(pickup, 0, "skill", null, new Vec2(100, 100), _ => true,
        new Dice([0.0]).Next) is null, "零伤害不掉");
    run.True(ManaGems.RollManaGem(pickup, 13, "weapon", "bow", new Vec2(100, 100), _ => true,
        new Dice([0.0]).Next) is null, "没有阈值不掉");

    var members = new List<ManaTestMember>
    {
        new("player", 100, 10, 200),
        new("ally", 50, 50, 100),
        new("dead", 0, 0, 100),
    };
    var recovery = ManaGems.RecoverManaPickup(pickup, [.. members.Cast<ManaMember>()], "player");
    run.True(recovery is not null, "有活着的收集者就能收");
    run.Equal(40, recovery!.Amount, "按收集者上限的 20% 算");
    // 两名活人都没满，所以都算「真正回了蓝」；倒下的人跳过。
    run.Equal("player,ally", string.Join(",", recovery.Restored), "只回没满的活人");
    run.Equal("player,ally", string.Join(",", recovery.Effects), "特效给所有被结算到的活人");
    run.Equal(50, members[0].Mana, "主角回到 10+40");
    run.Equal(90, members[1].Mana, "盟友按自己上限夹取");
    run.True(ManaGems.RecoverManaPickup(pickup, [members[2]], "dead") is null, "收集者倒下就收不了");

    var log = new List<string>();
    ActionDispatch.Run(true, new ActionDispatchProbe(log));
    run.Equal("is-stealthed,work,remove-stealth,after-break,sound", string.Join(",", log), "破隐次序");
    log.Clear();
    ActionDispatch.Run(false, new ActionDispatchProbe(log, stealthed: false));
    run.Equal("work,sound", string.Join(",", log), "不破隐就只跑主体与音效");
});

run.Case("攻击附率执行器：六阶段按序掷骰、火附溅射决定下一段、重入被拒", () =>
{
    var log = new List<string>();
    var executor = new AttackProcExecutor();

    Dice dice = new(Enumerable.Repeat(0.0, 8).ToArray());
    var result = executor.Execute(1000, new AttackProcTestPort(dice.Next, log, SplashCount: 1));
    run.Equal(AttackProcExecutor.AttackProcStatus.Completed, result.Status);
    run.Equal(6, result.StagesExecuted, "六段全部执行");
    run.True(log.Contains("damage:700"), "风击伤害 700‰");
    run.True(log.Contains("buff:attack-stun"), "风击带眩晕");
    run.Equal(1, log.Count(entry => entry == "splash:500"), "火附走溅射");
    run.True(log.Contains("buff:attack-slow"), "冰击带减速");
    run.True(log.Contains("buff:divine-defense-break"), "圣击带破防");
    run.True(log.Contains("buff:darkness-strike-impact"), "暗击带重击");
    run.True(log.Contains("buff:poisoned"), "毒击带中毒");

    log.Clear();
    var retryDice = new Dice(0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    var retry = executor.Execute(1000, new AttackProcTestPort(retryDice.Next, log, SplashCount: 0));
    run.Equal(AttackProcExecutor.AttackProcStatus.Completed, retry.Status);
    run.Equal(7, retry.StagesExecuted, "风击 + 两次火附 + 跳过的一次 + 其余三段");
    run.Equal(1, log.Count(entry => entry == "splash:500"), "只有第一次火附走了溅射");
    run.True(log.Contains("buff:attack-stun"), "风击在第一段");
    run.True(log.Contains("buff:poisoned"), "毒击在最后一段");

    log.Clear();
    var zero = executor.Execute(1000, new AttackProcTestPort(() => 0.0, log, ZeroChances: true));
    run.Equal(6, zero.StagesExecuted, "零概率仍走完六段");
    run.Equal(0, log.Count, "零概率不结算");

    var busy = executor.Execute(1000, new AttackProcTestPort(() =>
    {
        var nested = executor.Execute(1000, new AttackProcTestPort(() => 0.0, log, ZeroChances: true));
        run.Equal(AttackProcExecutor.AttackProcStatus.Reentrant, nested.Status, "执行中再入被拒");
        return 0.0;
    }, log, ZeroChances: true), budget: 0);
    run.Equal(AttackProcExecutor.AttackProcStatus.BudgetExhausted, busy.Status);
});

run.Case("魔焰装甲：反射基数、溅射掷骰与目标剔除", () =>
{
    run.Equal(120, MoltenArmor.ReflectionDamage(new MoltenArmor.DefenseSource("monster", PhysicalAttack: 120, 0, 0), 1000));
    run.Equal(180, MoltenArmor.ReflectionDamage(
        new MoltenArmor.DefenseSource("character", 0, Defense: 150, DefenseRate: 200), 1000));
    run.Equal(60, MoltenArmor.ReflectionDamage(
        new MoltenArmor.DefenseSource("monster", PhysicalAttack: 120, 0, 0), 500), "千分率按比例");
    run.Equal(0, MoltenArmor.ReflectionDamage(
        new MoltenArmor.DefenseSource("monster", PhysicalAttack: 120, 0, 0), -50), "负千分率按 0 算");

    run.Equal(60, MoltenArmor.RollSplash(120, 1000, () => 0.0), "阈值拉满必中，溅射取一半");
    run.Equal(null, MoltenArmor.RollSplash(120, 100, () => 0.999), "骰 1000 不小于阈值 100 时未中");
    run.Equal(0, MoltenArmor.RollSplash(0, 1000, () => 0.0), "零伤害也可能成功溅出 0 点");

    var source = new Combatant("defender", "enemy", new Vec2(100, 100), new CollisionBox());
    var attacker = new Combatant("attacker", "player", new Vec2(132, 100), new CollisionBox());
    var bystander = new Combatant("bystander", "player", new Vec2(116, 100), new CollisionBox());
    var targets = MoltenArmor.SplashTargets(source, new Vec2(116, 100), [attacker, bystander], "attacker");
    run.Equal(1, targets.Count);
    run.Equal("bystander", targets[0].Id, "原攻击者被剔除");
});

run.Case("扰乱候选：格距门槛、召唤物优先、有效防御严格更高", () =>
{
    DisturbanceActor Actor(string id, Vec2 position, bool sameCamp, bool ownSummon, int defense) =>
        new(position, new CollisionBox(), id, sameCamp, ownSummon, defense);

    var self = Actor("self", new Vec2(0, 0), true, false, 100);

    var enemy = Actor("enemy", new Vec2(8, 0), false, false, 500);
    var far = Actor("far", new Vec2(600, 0), true, false, 500);
    var weaker = Actor("weaker", new Vec2(16, 0), true, false, 90);
    var stronger = Actor("stronger", new Vec2(16, 0), true, false, 140);

    var pick = Disturbance.Candidate(self, [enemy, far, weaker, stronger]);
    run.Equal("stronger", pick);

    var summon = Actor("summon", new Vec2(16, 0), true, true, 10);
    run.Equal("summon", Disturbance.Candidate(self, [stronger, summon]));

    var first = Actor("first", new Vec2(16, 0), true, false, 140);
    var second = Actor("second", new Vec2(24, 0), true, false, 140);
    run.Equal("first", Disturbance.Candidate(self, [first, second]));
});

run.Case("佣兵团修正：低血抗性、武器/护甲增幅与概率判定", () =>
{
    var effects = new Dictionary<string, int>
    {
        ["low-health-resistance"] = 40,
        ["sword-damage"] = 30,
        ["plate-defense"] = 20,
        ["leather-physical-damage"] = 25,
        ["cloth-magic-damage"] = 15,
        ["bow-damage"] = 35,
        ["two-hand-damage"] = 45,
        ["magic-damage"] = 10,
        ["magic-resistance"] = 50,
        ["critical-damage"] = 60,
    };

    static string Describe(IReadOnlyList<AttributeModifier> list) =>
        string.Join(" ", list.Select(item => $"{item.Attribute}+{item.Operation.Amount}"));

    var worn = PartyCombat.PartyCombatModifiers(effects, "longsword", "leather", 100, 100);
    run.Equal("physicalDamageRate+30 magicDamageRate+30 physicalDamageRate+25 magicDamageRate+10 magicResistance+50 criticalDamage+60",
        Describe(worn), "长剑与皮甲各自的增幅");

    var low = PartyCombat.PartyCombatModifiers(effects, "bow", "plate", 30, 100);
    run.Equal("physicalResistanceBonus+40 magicResistance+40 defenseRate+20 physicalDamageRate+35 magicDamageRate+10 magicResistance+50 criticalDamage+60",
        Describe(low), "低血、弓与板甲");

    var twoHand = PartyCombat.PartyCombatModifiers(new Dictionary<string, int>(), "two-handed", "cloth", 100, 100);
    run.Equal(0, twoHand.Count, "没有佣兵团技能时不产出任何修正");

    var draws = 0;
    double Draw(double value) { draws += 1; return value; }
    run.True(PartyCombat.PartyProcSucceeds(900, () => Draw(0.0)), "骰 1 < 900");
    run.False(PartyCombat.PartyProcSucceeds(900, () => Draw(0.999)), "骰 999 不低于 900");
    draws = 0;
    run.False(PartyCombat.PartyProcSucceeds(0, () => Draw(0.0)), "阈值 0 恒失败");
    run.Equal(0, draws, "阈值 0 不取骰");
});

run.Case("换装配方：装备普攻、双持外观与盾判定；狂气动画记忆可往返", () =>
{
    var root = FindContentRoot();
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var wardrobe = JsonSerializer.Deserialize<WardrobeCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "wardrobe.json")), SkillActionJson.Options)!;
    var battle = ReadBattle(root);

    // 主角开局那组装备：换装配方给的普攻必须与 battle.json 里同名规则逐项一致。
    var equipment = new Dictionary<string, string>
    {
        ["main-hand"] = "basic-longsword",
        ["off-hand"] = "basic-shield",
    };
    var recipe = WardrobeRules.CostumeRecipe(wardrobe, items, equipment, "dark-knight");
    run.True(recipe.Attack is not null, "这组武器有普攻规则");
    var fromWardrobe = recipe.Attack!;
    var fromBattle = battle.BasicAttacks["longsword-slash"];
    run.Equal(fromBattle.CycleFrames, fromWardrobe.CycleFrames, "周期帧一致");
    run.Equal(fromBattle.StrikeFrame, fromWardrobe.StrikeFrame, "出手帧一致");
    run.Equal(fromBattle.Damage.Source, fromWardrobe.Damage.Source, "伤害来源一致");
    run.True(recipe.FrenzyAttack is not null, "长剑加盾有狂气动作");
    run.Equal(4, recipe.FrenzyAttack!.Clips.Count, "狂气动作四向齐全");
    run.True(recipe.FrenzyWaitFrames is null, "有狂气动作时不配等待帧数");

    var assassin = new Dictionary<string, string> { ["main-hand"] = "basic-dagger", ["off-hand"] = "basic-dagger" };
    var dual = WardrobeRules.CostumeRecipe(wardrobe, items, assassin, "assassin");
    run.True(dual.DualWieldAttack is not null, "短剑双持有一套双持动作");
    run.Equal(4, dual.DualWieldAttack!.Clips.Count, "双持动作四向齐全");

    run.True(WardrobeRules.HasOffhandShield(items, equipment), "装了盾");
    run.False(WardrobeRules.HasOffhandShield(items, assassin), "副手是武器不算盾");
    run.False(WardrobeRules.HasOffhandShield(items, new Dictionary<string, string>()), "没装副手且目录默认不带盾");
    run.True(WardrobeRules.HasOffhandShield(items, new Dictionary<string, string>(), defaultShield: true), "默认带盾的角色算有盾");

    var threw = false;
    try
    {
        WardrobeRules.CostumeRecipe(wardrobe, items, new Dictionary<string, string> { ["main-hand"] = "no-such-item" });
    }
    catch (ArgumentException)
    {
        threw = true;
    }

    run.True(threw, "缺装备外观要报错");

    var memory = new FrenzyAnimationMemory();
    run.Equal(9, memory.Select("player", null, 9), "没有动画时用初值");
    run.Equal(7, memory.Select("player", 7, 9), "有动画就用动画帧数");
    run.Equal(7, memory.Select("player", null, 9), "再缺动画时退回上次记住的");
    memory.UpdateEquipment("player", 5);
    run.Equal(5, memory.Select("player", null, 9), "换装更新已记过的角色");
    memory.UpdateEquipment("ally", 5);
    run.Equal(4, memory.Select("ally", null, 4), "没记过的角色不被换装写入");
    var captured = memory.CaptureState();
    var restored = new FrenzyAnimationMemory();
    restored.RestoreState(captured);
    run.Equal(5, restored.Select("player", null, 0), "快照往返保留帧数");
    restored.RemoveActor("player");
    run.Equal(3, restored.Select("player", null, 3), "移出队伍就忘掉");
});

run.Case("装备词条：攻击附率、减仇恨、副手率与恢复三件", () =>
{
    static ItemInstance With(params (string Stat, int Value)[] modifiers)
    {
        var item = new ItemInstance { Id = "x", ItemId = "x" };
        item.Numbers = new ItemNumbers();
        foreach (var (stat, value) in modifiers) item.Numbers.Modifiers.Add(new Modifier { Stat = stat, Value = value });
        return item;
    }

    var procs = EquipmentAttributes.AttackProcs(
    [
        With(("fire", 120), ("wind", 90)),
        With(("freeze", 60), ("fire", 30)),
        With(("divine", 10), ("darkness", 20), ("poison", 40)),
    ]);
    run.Equal(150, procs.FireSplashChance, "火附率两件累加");
    run.Equal(90, procs.WindStrikeChance);
    run.Equal(60, procs.FrostStrikeChance);
    run.Equal(10, procs.DivineStrikeChance);
    run.Equal(20, procs.DarknessStrikeChance);
    run.Equal(40, procs.PoisonStrikeChance);

    var threat = EquipmentAttributes.ThreatReduction([With(("aggroReduction", -100)), With(("aggroReduction", 250))]);
    run.Equal(150, threat);

    run.Equal(0, EquipmentAttributes.BaseOffhandAttackChance("warlock", "dagger", "leather"));
    run.Equal(140, EquipmentAttributes.BaseOffhandAttackChance("assassin", "dagger", "plate"));
    run.Equal(100, EquipmentAttributes.BaseOffhandAttackChance("assassin", "longsword", ""));
    run.Equal(90, EquipmentAttributes.BaseOffhandAttackChance("warrior", "axe", ""));
    run.Equal(190, EquipmentAttributes.BaseOffhandAttackChance("assassin", "dagger", "leather"));

    var draws = 0;
    double Draw(double value) { draws += 1; return value; }
    run.False(EquipmentAttributes.ChooseDualWield("warlock", true, () => Draw(0.0), () => 900), "非双持职业不掷");
    run.False(EquipmentAttributes.ChooseDualWield("assassin", false, () => Draw(0.0), () => 900), "没有副手不掷");
    run.Equal(0, draws, "没资格时连骰子都不取");
    run.True(EquipmentAttributes.ChooseDualWield("assassin", true, () => Draw(0.0), () => 900), "骰 1 < 900");
    run.False(EquipmentAttributes.ChooseDualWield("assassin", true, () => Draw(0.999), () => 0), "率为零也掷，但不中");
    run.Equal(2, draws, "有资格时每次取一次骰");

    run.Equal(120, EquipmentAttributes.OffhandPhysicalAttack(200, 80, 60));

    var recovery = EquipmentAttributes.Recovery(
        [With(("lifeSteal", -30), ("manaRegen", 12), ("spellExpert", 40))]);
    run.Equal(0, recovery.LifeDrainPerThousand, "吸血负值钳到零");
    run.Equal(12, recovery.ManaRecoveryPerPulse);
    run.Equal(40, recovery.ManaCostReductionRate);
});

run.Case("特效索引与命中反馈：真实内容按四向片段与五组数字解析", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("没找到 public/content，跳过特效索引用例");
        return;
    }

    var battle = ReadBattle(root);
    run.Equal(158, battle.AttackEffects.Count, "attackEffects 条数与内容一致");

    run.True(battle.AttackEffects.TryGetValue("sword-swing", out var swing), "有 sword-swing");
    run.Equal(4, swing!.Clips.Count, "四向齐全");
    var south = battle.EffectClip("sword-swing", "south");
    run.True(south is not null, "取得到 south 片段");
    run.Equal(3, south!.Frames, "south 三帧");
    run.Equal(38, south.FrameWidth);
    run.True(south.Layers.Length >= 1, "片段至少一层");

    run.True(ReferenceEquals(battle.EffectClip("sword-swing", "up"), south), "未知朝向退回 south");
    run.True(battle.EffectClip("sword-swing", "north") is not null, "north 存在");
    run.True(battle.EffectClip("no-such-effect", "south") is null, "缺特效返回空");

    var feedback = battle.Feedback;
    run.True(feedback is not null, "feedback 解析出来了");
    run.Equal(3, feedback!.Digits.Enemy.Length, "敌方数字三张");
    run.Equal(1, feedback.Digits.Miss.Length, "落空数字一张");
    run.Equal(5, feedback.NormalImpacts.Length);
    run.Equal(4, feedback.CriticalBursts.Length);
    run.Equal(6, feedback.ShakeOffsets.Length);
    run.Equal(-6f, feedback.ShakeOffsets[0].X);
    run.Equal(6f, feedback.ShakeOffsets[1].Y);
});

run.Case("普攻解析：绑定缺失与固定动画不匹配都要报错", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("没找到 public/content，跳过普攻解析用例");
        return;
    }

    var battle = ReadBattle(root);
    var knight = battle.Classes.First(@class => @class.Id == "dark-knight");

    run.Throws(() => BasicAttackRules.Resolve(battle.BasicAttacks, knight.Actor, null), "角色没有普攻");
    run.Throws(() => BasicAttackRules.Resolve(battle.BasicAttacks, knight.Actor,
        new BasicAttackBinding { ProfileId = "missing-profile", Clips = knight.BasicAttack!.Clips }), "普攻规则缺失");
    run.Throws(() => BasicAttackRules.Resolve(battle.BasicAttacks, knight.Actor,
        new BasicAttackBinding
        {
            ProfileId = "longsword-slash",
            Clips = new Dictionary<string, string> { ["south"] = "attack-south" },
        }), "普攻动画绑定缺失");

    // 固定动画必须与素材帧数一致：把周期改成对不上的数就报错。
    var fixedDefinition = new BasicAttackDefinition
    {
        Timing = "fixed-animation",
        CycleFrames = 999,
        FrameDurationMs = 60,
        StrikeFrame = 4,
    };
    run.Throws(() => BasicAttackRules.Resolve(
        new Dictionary<string, BasicAttackDefinition> { ["fixed"] = fixedDefinition },
        knight.Actor,
        new BasicAttackBinding
        {
            ProfileId = "fixed",
            Clips = knight.BasicAttack!.Clips,
        }), "固定普攻周期与素材帧数不匹配");
});

run.Case("距离口径：选敌近似距离、评分与碰撞盒射程", () =>
{
    run.Equal(11, BasicAttackRules.TargetDistance(new Vec2(0, 0), new Vec2(8, 8)), "八方向近似：8+8−2−4+1");
    run.Equal(0f, BasicAttackRules.TargetScore(new Vec2(0, 0), new Vec2(60, 0), "south", 50f), "超出半径不参与选敌");
    run.Equal(44f, BasicAttackRules.TargetScore(new Vec2(0, 0), new Vec2(0, 14), "south", 50f), "正前方加分 8");
    run.Equal(36f, BasicAttackRules.TargetScore(new Vec2(0, 0), new Vec2(0, 14), "north", 50f), "背后没有加分");
    run.Equal(36f, BasicAttackRules.TargetScore(new Vec2(0, 0), new Vec2(0, 14), "east", 50f), "正南目标对面向东的角色没有前向加分");

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    run.Equal(2, BasicAttackRules.AttackDistance(new Vec2(100, 100), box, new Vec2(100, 100), box), "重叠只算近身余量 2");
    run.Equal(2, BasicAttackRules.AttackDistance(new Vec2(100, 100), box, new Vec2(116, 100), box), "盒沿相贴仍是 2");
    run.Equal(4, BasicAttackRules.AttackDistance(new Vec2(100, 100), box, new Vec2(132, 100), box), "每 8 像素加 1");
    run.Equal(6, BasicAttackRules.AttackDistance(new Vec2(100, 100), box, new Vec2(148, 100), box));
});

run.Case("朝向与投射物：斜向取整口径与到达判定", () =>
{
    run.Equal("east", BasicAttackRules.FaceTarget(new Vec2(0, 0), new Vec2(10, 5)));
    run.Equal("south", BasicAttackRules.FaceTarget(new Vec2(0, 0), new Vec2(5, 10)));
    run.Equal("west", BasicAttackRules.FaceTarget(new Vec2(0, 0), new Vec2(-1, 0)));

    run.Equal(0, BasicAttackRules.ProjectileHeading(new Vec2(0, 0), new Vec2(10, 0)), "正右为零度");
    run.Equal(90, BasicAttackRules.ProjectileHeading(new Vec2(0, 0), new Vec2(0, 10)), "正下为九十度");
    run.Equal(180, BasicAttackRules.ProjectileHeading(new Vec2(0, 0), new Vec2(-10, 0)));
    run.Equal(270, BasicAttackRules.ProjectileHeading(new Vec2(0, 0), new Vec2(0, -10)));

    var (position, arrived) = BasicAttackRules.ProjectileStep(new Vec2(0, 0), new Vec2(16, 0), 4);
    run.False(arrived);
    run.Equal(4, (int)position.X);

    var (landed, done) = BasicAttackRules.ProjectileStep(new Vec2(0, 0), new Vec2(3, 0), 4);
    run.True(done, "目标就在脚边就直接到达");
    run.Equal(3, (int)landed.X);
});

run.Case("出手控制器：进入射程按命中帧结算，动作走完回 idle", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("没找到 public/content，跳过出手控制器用例");
        return;
    }

    var battle = ReadBattle(root);
    var knight = battle.Classes.First(@class => @class.Id == "dark-knight");
    var attack = BasicAttackRules.Resolve(battle.BasicAttacks, knight.Actor, knight.BasicAttack);
    var box = new CollisionBox();
    var port = new TestCombatPort
    {
        Caster = new Combatant("enemy", "enemy", new Vec2(100, 100), box, "south"),
        Target = new Combatant("player", "player", new Vec2(100, 100), box, "north"),
    };
    var controller = new CombatController("enemy", attack, port);

    run.Equal("started", controller.RequestTarget("player"));
    run.Equal(CombatState.Attacking, controller.State, "贴着打不用接近");
    run.Equal(0, port.Contacts.Count, "第 0 帧还不是命中帧");
    run.Equal("south", port.PlayedFacing, "出手按目标方向转身");

    for (var index = 1; index <= 9; index += 1) port.PendingFrame!(index);
    run.Equal(1, port.Contacts.Count, "第 4 帧只结算一次");
    run.Equal(("enemy", "player", "main-hand"), port.Contacts[0]);

    port.PendingComplete!();
    run.Equal(CombatState.Idle, controller.State, "动作周期走完回到待机");

    port.Contacts.Clear();
    run.Equal("started", controller.RequestTarget());
    for (var index = 1; index <= 9; index += 1) port.PendingFrame!(index);
    port.PendingComplete!();
    run.Equal(1, port.Contacts.Count, "第二轮动作也只命中一次");
});

run.Case("出手控制器：够不着先追近，取消后立刻收手", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("没找到 public/content，跳过出手控制器用例");
        return;
    }

    var battle = ReadBattle(root);
    var knight = battle.Classes.First(@class => @class.Id == "dark-knight");
    var attack = BasicAttackRules.Resolve(battle.BasicAttacks, knight.Actor, knight.BasicAttack);
    var box = new CollisionBox();
    var port = new TestCombatPort
    {
        Caster = new Combatant("enemy", "enemy", new Vec2(100, 100), box, "south"),
        Target = new Combatant("player", "player", new Vec2(300, 100), box, "north"),
    };
    var controller = new CombatController("enemy", attack, port);

    run.Equal("started", controller.RequestTarget("player"));
    run.Equal(CombatState.Approaching, controller.State, "够不着先接近");
    run.Equal(1, port.Moves, "向目标走了一步");
    run.Equal(0, port.Contacts.Count, "接近中不出手");

    port.Target = port.Target with { Position = new Vec2(110, 100) };
    controller.Update();
    run.Equal(CombatState.Attacking, controller.State);

    controller.Cancel();
    run.Equal(CombatState.Idle, controller.State);
    run.True(port.Cancelled, "表现层的动作要跟着取消");
    run.True(controller.SelectedTargetId is null, "取消同时清掉选中目标");

    port.Target = null;
    controller.Update();
    run.Equal(CombatState.Idle, controller.State);
});

static ResolvedBasicAttack FakeAttack(int strikeFrame, BasicAttackStrike[]? strikes = null) => new()
{
    Definition = new BasicAttackDefinition
    {
        CycleFrames = strikeFrame + 2,
        FrameDurationMs = 60,
        StrikeFrame = strikeFrame,
        Strikes = strikes,
        ApproachDistanceUnits = 4,
        SearchRadiusPx = 64,
    },
    EffectiveCycleFrames = strikeFrame + 2,
    DurationMs = (strikeFrame + 2) * 60,
    Clips = new Dictionary<string, string>(),
    Sequence = new Dictionary<string, int[]>(),
};

run.Case("仇恨算式：减率、近身与远程权重、治疗与状态", () =>
{
    run.Equal(75, ThreatLedger.ReduceThreat(100, 250));
    run.Equal(100, ThreatLedger.ReduceThreat(100));

    run.Equal(100, ThreatLedger.CalculateDamageThreat(100, 10));
    run.Equal(80, ThreatLedger.CalculateDamageThreat(100, 31));
    run.Equal(0, ThreatLedger.CalculateDamageThreat(0, 10));

    run.Equal(20, ThreatLedger.CalculateHealingThreat(1000, 10));
    run.Equal(15, ThreatLedger.CalculateHealingThreat(1000, 31));

    run.Equal(100, ThreatLedger.CalculateStateThreat(50));
});

run.Case("仇恨账本：新增、抬分、切换阈值与清空", () =>
{
    var ledger = new ThreatLedger();
    run.True(ledger.Add("enemy-1", "player", 100, 3));
    run.Equal(100, ledger.Score("enemy-1", "player"));

    ledger.Add("enemy-1", "ally", 105, 4);
    run.Equal("player", ledger.Select("enemy-1", "player", _ => true));

    ledger.Add("enemy-1", "ally", 120, 5);
    run.Equal("ally", ledger.Select("enemy-1", "player", _ => true));

    run.False(ledger.RaiseTo("enemy-1", "ally", 50, 9));
    run.True(ledger.RaiseTo("enemy-1", "ally", 400, 10));
    run.Equal(400, ledger.Score("enemy-1", "ally"));

    ledger.Add("enemy-1", "taunter", 500, 11);
    ledger.Add("enemy-1", "rival", 900, 12);
    run.True(ledger.ClampCompetitors("enemy-1", "taunter", _ => true));
    run.Equal(400, ledger.Score("enemy-1", "rival"));
    run.Equal(500, ledger.Score("enemy-1", "taunter"));

    ledger.ClearOwner("enemy-1");
    run.True(ledger.Score("enemy-1", "player") is null);
});

run.Case("仇恨账本：越距衰减、来源门与快照校验", () =>
{
    var ledger = new ThreatLedger();
    ledger.Add("enemy-1", "player", 100, 1);
    ledger.Add("enemy-1", "near", 3, 1);

    ledger.Decay((_, _) => true);
    run.Equal(40, ledger.Score("enemy-1", "player"));
    run.True(ledger.Score("enemy-1", "near") is null);

    run.False(ledger.Add("enemy-1", "other", 10, 2, new ThreatAdditionRules(AllowedSourceId: "player")));
    run.True(ledger.Score("enemy-1", "other") is null);

    var snapshot = ledger.CaptureState();
    var restored = new ThreatLedger();
    restored.RestoreState(snapshot);
    run.Equal(40, restored.Score("enemy-1", "player"));

    var duplicated = new ThreatLedgerState
    {
        Entries =
        [
            new ThreatEntry { OwnerId = "a", SourceId = "b", Score = 1, UpdatedTick = 0 },
            new ThreatEntry { OwnerId = "a", SourceId = "b", Score = 2, UpdatedTick = 0 },
        ],
    };
    run.Throws(() => restored.RestoreState(duplicated), "仇恨快照包含重复来源");
});

run.Case("仇恨系统：发现只记一次，按存在权重建账", () =>
{
    var port = new TestThreatPort();
    port.Add("enemy-1", "enemy", level: 10);
    port.Add("player", "player", role: CombatThreatRole.Player);
    var threat = port.System;

    threat.Notice("enemy-1", "player");
    run.Equal(20, port.LedgerScore("enemy-1", "player"));
    run.Equal("player", threat.Select("enemy-1", null));

    threat.Notice("enemy-1", "player");
    run.Equal(20, port.LedgerScore("enemy-1", "player"));

    port.Add("player", "player", role: CombatThreatRole.Player, active: false);
    run.True(threat.Select("enemy-1", null) is null);
});

run.Case("仇恨系统：伤害记账与召唤物登场抢仇恨", () =>
{
    var port = new TestThreatPort();
    port.Add("enemy-1", "enemy", level: 10, position: new Vec2(0, 0));
    port.Add("player", "player", position: new Vec2(40, 0));
    port.Add("summon", "player", role: CombatThreatRole.Summon, position: new Vec2(40, 0));
    var threat = port.System;

    threat.Notice("enemy-1", "player");
    var before = port.LedgerScore("enemy-1", "player") ?? 0;
    threat.Damage("player", "enemy-1", 100);
    run.Equal(before + 100, port.LedgerScore("enemy-1", "player"));

    threat.Notice("enemy-1", "summon");
    var summonScore = port.LedgerScore("enemy-1", "summon") ?? 0;
    run.Equal((int)Math.Ceiling((before + 100) * 120 / 100.0), summonScore);
    run.Equal("summon", threat.Select("enemy-1", null));
});

run.Case("仇恨系统：清场与移除角色", () =>
{
    var port = new TestThreatPort();
    port.Add("enemy-1", "enemy", level: 5);
    port.Add("player", "player");
    var threat = port.System;

    threat.Notice("enemy-1", "player");
    run.True(threat.Has("enemy-1", "player"));
    threat.ClearOwner("enemy-1");
    run.False(threat.Has("enemy-1", "player"));

    threat.Notice("enemy-1", "player");
    threat.RemoveActor("player");
    run.False(threat.Has("enemy-1", "player"));
});

run.Case("状态能力：睡眠与眩晕不能行动，定身只限制移动", () =>
{
    var sleeping = CombatStateRules.Capabilities(["sleep"]);
    run.False(sleeping.CanAct);
    run.False(sleeping.CanMove);

    var immobilized = CombatStateRules.Capabilities(["immobilized"]);
    run.True(immobilized.CanAct);
    run.False(immobilized.CanMove);

    var stealthed = CombatStateRules.Capabilities(["stealthed"]);
    run.False(stealthed.CanTarget, "敌对选取看不到隐身");
    run.True(CombatStateRules.Capabilities(["stealthed"], hostile: false).CanTarget, "盟友治疗仍可选");

    run.False(CombatStateRules.Capabilities(["ambush"]).CanAutoApproach);
    run.False(CombatStateRules.Capabilities(["damage-immune"]).CanReceiveDamage);
});

run.Case("察觉范围：普通按自身，全图参战至少覆盖地图对角", () =>
{
    var ordinary = new EnemyBehavior { AwarenessRangeUnits = 12 };
    run.Equal(12, EnemyEngagement.AwarenessRangeUnits(ordinary, null));

    var mapWide = new EnemyBehavior { AwarenessRangeUnits = 12, Engagement = "map-wide" };
    run.Equal(142, EnemyEngagement.AwarenessRangeUnits(mapWide, new Rect(0, 0, 640, 480)));

    var smallMap = new EnemyBehavior { AwarenessRangeUnits = 300, Engagement = "map-wide" };
    run.Equal(300, EnemyEngagement.AwarenessRangeUnits(smallMap, new Rect(0, 0, 64, 64)),
        "自身察觉更远时取自身");

    run.Throws(() => EnemyEngagement.AwarenessRangeUnits(mapWide, null), "全图参战的敌人缺少地图边界");
});

run.Case("敌人 AI：主动怪靠仇恨发现目标并追近", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(120, 0))
    {
        ThreatPort = new TestEnemyThreat(),
        TargetId = "player",
    };
    var behavior = new EnemyBehavior
    {
        Aggression = "active",
        AwarenessRangeUnits = 20,
        CanMove = true,
        MoveSpeedPxPerSecond = 40,
    };
    var controller = new EnemyController("enemy-1", behavior, FakeAttack(2), port);

    controller.Step();

    run.Equal("player", controller.CurrentTargetId());
    run.Equal("chasing", controller.ActionState());
    run.True(port.Threat is not null);
    run.True(port.Moves + port.Followed > 0, "主动怪要开始靠近");
});

run.Case("敌人 AI：被动怪挨打之前不理人", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(24, 0))
    {
        ThreatPort = new TestEnemyThreat(),
        TargetId = "player",
    };
    var behavior = new EnemyBehavior { Aggression = "passive", AwarenessRangeUnits = 20 };
    var controller = new EnemyController("enemy-1", behavior, FakeAttack(2), port);

    for (var frame = 0; frame < 10; frame += 1) controller.Step();
    run.True(controller.CurrentTargetId() is null, "没被打过就不该有目标");

    ((TestEnemyThreat)port.ThreatPort!).Add("player", 50);
    controller.Step();
    run.Equal("player", controller.CurrentTargetId());
});

run.Case("敌人 AI：察觉范围外不接战，全图参战不设限", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(400, 0))
    {
        ThreatPort = new TestEnemyThreat(),
        TargetId = "player",
    };
    var near = new EnemyController("enemy-1", new EnemyBehavior { Aggression = "active", AwarenessRangeUnits = 10 },
        FakeAttack(2), port);
    near.Step();
    run.True(near.CurrentTargetId() is null, "400 像素远，10 个单位察觉不到");
});

run.Case("敌人 AI：aggression none 完全不动手", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(16, 0));
    var controller = new EnemyController("enemy-1", new EnemyBehavior { Aggression = "none" }, FakeAttack(2), port);

    controller.Step();
    run.Equal("idle", controller.ActionState());
    run.True(controller.CurrentTargetId() is null);
    run.Equal(0, port.Moves + port.Followed);
});

run.Case("敌人 AI：没有仇恨端口时按最近的敌对候选选人", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(48, 0)) { TargetId = "player" };
    var behavior = new EnemyBehavior { Aggression = "active", AwarenessRangeUnits = 20, CanMove = true };
    var controller = new EnemyController("enemy-1", behavior, FakeAttack(2), port);

    controller.Step();
    run.Equal("player", controller.CurrentTargetId());
});

run.Case("敌人 AI：追丢之后回家，到家转待机", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(8, 0), new Vec2(72, 0))
    {
        HomePoint = new Vec2(0, 0),
    };
    var behavior = new EnemyBehavior
    {
        Aggression = "active",
        AwarenessRangeUnits = 10,
        CanMove = true,
        MoveSpeedPxPerSecond = 40,
    };
    var controller = new EnemyController("enemy-1", behavior, FakeAttack(2), port);

    controller.Step();
    run.Equal("player", controller.CurrentTargetId());
    run.Equal("chasing", controller.ActionState());

    port.Target = port.Target with { Position = new Vec2(4000, 0) };
    controller.Step();
    run.Equal("returning", controller.ActionState());
    run.True(controller.CurrentTargetId() is null);

    port.Self = port.Self with { Position = port.HomePoint };
    controller.Step();
    run.Equal("idle", controller.ActionState());
});

run.Case("敌人 AI：闲逛按待机时长与随机数迈步", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(400, 0))
    {
        TargetId = "player",
        Random = () => 0.01,
    };
    var behavior = new EnemyBehavior
    {
        Aggression = "passive",
        AwarenessRangeUnits = 1,
        CanMove = true,
        CanTurn = true,
        MoveSpeedPxPerSecond = 40,
        Wander = new EnemyWander { IdleDurationMs = 120 },
    };
    var controller = new EnemyController("enemy-1", behavior, FakeAttack(2), port, port.Random);

    controller.Step();
    run.Equal("idle", controller.ActionState(), "待机时间没到不迈步");

    controller.Step();
    run.Equal("wandering", controller.ActionState());
    run.True(port.Followed > 0, "迈步要交给物理系统一条路径");
});

run.Case("敌人 AI：释放条件按生命千分比与每场一次", () =>
{
    var port = new TestEnemyPort("enemy-1", new Vec2(0, 0), new Vec2(400, 0))
    {
        HealthValue = (30, 100),
        TargetId = "player",
    };
    var controller = new EnemyController("enemy-1", new EnemyBehavior { Aggression = "passive" }, FakeAttack(2), port);

    run.True(controller.ReleaseAllowed("always", false, "skill-a"));
    run.True(controller.ReleaseAllowed("hp-below-30", false, "skill-a"));
    run.True(controller.ReleaseAllowed("hp-below-95", false, "skill-a"), "30% 已经低于 95% 的阈值，可放");

    port.HealthValue = (96, 100);
    run.False(controller.ReleaseAllowed("hp-below-95", false, "skill-a"));
});

run.Case("资源默认值：成员按等级回血回魔，怪物不回血", () =>
{
    var levelOne = CombatResources.DefaultResourceAttributes("member", 1);
    run.Equal(99, levelOne.HealthRecoveryPerPulse, "trunc((960 + 36×1) ÷ 10)");
    run.Equal(8, levelOne.ManaRecoveryPerPulse);
    run.Equal(1000, levelOne.ManaCostRate);

    var levelFifty = CombatResources.DefaultResourceAttributes("member", 50);
    run.Equal(276, levelFifty.HealthRecoveryPerPulse, "trunc((960 + 36×50) ÷ 10)");

    var monster = CombatResources.DefaultResourceAttributes("monster", 30);
    run.Equal(0, monster.HealthRecoveryPerPulse, "怪物每脉冲不回血");
    run.Equal(8, monster.ManaRecoveryPerPulse);
    run.Equal(0, monster.ManaCostRate);
});

run.Case("法力消耗：两个千分率相减后向零截断", () =>
{
    run.Equal(100, CombatResources.CalculateManaCost(100, 1000, 0));
    run.Equal(80, CombatResources.CalculateManaCost(100, 800, 0));
    run.Equal(75, CombatResources.CalculateManaCost(100, 1000, 250));
    // 截断到 0 就是 0，不会因为负号变成 -0。
    run.Equal(0, CombatResources.CalculateManaCost(1, 500, 0));
});

run.Case("恢复脉冲：31 个逻辑帧一次，只给活着的人加", () =>
{
    var recovery = new CombatResourceRecovery();
    IReadOnlyList<ResourceRecoveryActor> actors =
    [
        new("player", true, 1, 99, 8),
        new("dead", false, 1, 99, 8),
        new("emergency", true, 10, 0, 8, EmergencyRecovery: true),
    ];

    run.False(recovery.PulseDueAtFrame(30), "第 30 帧还不到点");
    run.True(recovery.PulseDueAtFrame(31));

    var pulses = recovery.AdvanceToFrame(31, actors);
    run.Equal(1, pulses.Count);
    run.Equal(31 * 60L, pulses[0].AtMs, "脉冲落在第 31 帧的毫秒位置");
    run.Equal(2, pulses[0].Amounts.Count, "倒地的人不进脉冲");
    run.Equal(99, pulses[0].Amounts[0].HealthAmount);
    run.Equal(30, pulses[0].Amounts[1].HealthAmount);

    run.False(recovery.PulseDueAtFrame(31), "同一帧不重复触发");
    run.True(recovery.PulseDueAtFrame(62));
});

run.Case("恢复脉冲：一次跨多个脉冲仍保留相位", () =>
{
    var recovery = new CombatResourceRecovery();
    IReadOnlyList<ResourceRecoveryActor> actors = [new("player", true, 1, 99, 8)];

    var pulses = recovery.AdvanceToFrame(93, actors);
    run.Equal(3, pulses.Count, "第 31 / 62 / 93 帧各一次");
    run.Equal(31 * 60L, pulses[0].AtMs);
    run.Equal(93 * 60L, pulses[2].AtMs);
    run.False(recovery.PulseDueAtFrame(93));

    run.Throws(() => recovery.AdvanceToFrame(10, actors), "资源恢复帧无效");
});

run.Case("恢复脉冲：快照往返", () =>
{
    var recovery = new CombatResourceRecovery();
    IReadOnlyList<ResourceRecoveryActor> actors = [new("player", true, 1, 99, 8)];
    recovery.AdvanceToFrame(45, actors);

    var state = recovery.CaptureState();
    run.Equal(45 * 60L, state.ElapsedMs, "推进到第 45 帧就是 2700 毫秒");
    run.Equal(61L, state.DeadlineTick, "第 31 帧之后截止帧推到 61");

    var restored = new CombatResourceRecovery();
    restored.RestoreState(state);
    run.Equal(45L, restored.CurrentTick);
    run.False(restored.PulseDueAtFrame(45));
    run.True(restored.PulseDueAtFrame(62));
});

run.Case("技能动作：54 个动作的站位、资源清单与逐帧索引对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "skill-action-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 skill-action-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var clipFrames = document.RootElement.GetProperty("clipFrames").GetInt32();
    var golden = document.RootElement.GetProperty("skills");

    var checkedSkills = 0;
    foreach (var entry in golden.EnumerateObject())
    {
        var id = entry.Name;
        var expected = entry.Value;
        var action = catalog.Skills[id].Action ?? throw new Exception($"{id} 没有解析出动作");

        run.Equal(expected.GetProperty("requiresTarget").GetBoolean(), SkillActionRules.NeedsTarget(action), $"{id} 站位需求");
        run.Equal(Join(expected.GetProperty("effectNames")), string.Join(",", SkillActionRules.SkillEffectNames(action)),
            $"{id} 特效清单");
        run.Equal(Join(expected.GetProperty("soundNames")), string.Join(",", SkillActionRules.SkillSoundNames(action)),
            $"{id} 音效清单");
        run.Equal(Join(expected.GetProperty("summonNames")), string.Join(",", SkillActionRules.SkillSummonNames(action)),
            $"{id} 召唤清单");
        run.Equal(expected.GetProperty("events").GetInt32(), action.Events.Count, $"{id} 事件数");

        var frames = expected.GetProperty("eventFrames");
        if (frames.ValueKind == JsonValueKind.Null)
        {
            run.Null(action.EventFrames, $"{id} 是连续施放动作，不该有帧索引");
        }
        else
        {
            var pairs = frames.EnumerateArray()
                .Select(pair => (Frame: pair[0].GetInt32(), Indices: Join(pair[1])))
                .ToList();
            run.Equal(pairs.Count, action.EventFrames?.Count ?? -1, $"{id} 有事件的帧数");
            foreach (var (frame, indices) in pairs)
            {
                run.True(action.EventFrames!.TryGetValue(frame, out var actual), $"{id} 缺少第 {frame} 帧");
                run.Equal(indices, string.Join(",", actual!), $"{id} 第 {frame} 帧的事件下标");
            }
        }

        run.Equal(Join(expected.GetProperty("sequence")),
            string.Join(",", SkillActionRules.SkillSequence(action, 1, clipFrames)), $"{id} 逻辑帧序列");
        checkedSkills += 1;
    }

    run.Equal(54, checkedSkills, "基准覆盖 54 个主动技能");
});

run.Case("技能动作：连续施放按整次施放取事件，靶子倒下后停在整击边界", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "skill-action-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 skill-action-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var golden = document.RootElement.GetProperty("skills");

    var checkedSkills = 0;
    foreach (var entry in golden.EnumerateObject())
    {
        if (!entry.Value.TryGetProperty("playback", out var playback)) continue;

        var action = catalog.Skills[entry.Name].Action!;
        var frame = 0;
        foreach (var expectedEvents in playback.EnumerateArray())
        {
            var actual = SkillActionRules.SkillPlaybackEvents(action, frame, 1)
                .Select(DescribeSkillEvent).ToList();
            run.Equal(Join(expectedEvents), string.Join(",", actual), $"{entry.Name} 第 {frame} 帧的事件");
            frame += 1;
        }

        var stop = entry.Value.GetProperty("repeatStop");
        var state = new SkillActionRules.SkillRepeatState();
        SkillActionRules.SettleSkillRepeat(action, 3, 1, false, state);
        run.Equal(stop.GetProperty("stoppedAtFrame").ValueKind == JsonValueKind.Null
                ? null
                : stop.GetProperty("stoppedAtFrame").GetInt32(),
            state.StoppedAtFrame, $"{entry.Name} 停止帧");
        run.Equal(stop.GetProperty("skippedFrames").GetInt32(),
            SkillActionRules.SkillRepeatSkippedFrames(action, 1, state), $"{entry.Name} 跳过的动画条目");
        checkedSkills += 1;
    }

    run.Equal(2, checkedSkills, "基准里有两个连续施放动作");
});

run.Case("技能冷却：绝对截止帧、千分率缩放与快照", () =>
{
    long now = 0;
    var store = new CombatCooldownStore(() => now);
    var port = store.ForActor("player");

    run.True(port.IsReady("skill-a"));
    port.Start("skill-a", 100);
    run.False(port.IsReady("skill-a"));

    var snapshot = port.Snapshot();
    run.Equal(6000L, snapshot["skill-a"].RemainingMs);
    run.Equal(6000L, snapshot["skill-a"].DurationMs);

    now = 60;
    run.Equal(2400L, port.Snapshot()["skill-a"].RemainingMs, "过了一秒剩两秒");

    now = 100;
    run.True(port.IsReady("skill-a"));
    run.False(port.Snapshot().ContainsKey("skill-a"), "到点的冷却不进快照");

    port.Start("skill-b", 101, 500);
    var b = store.CaptureState().Actors.Single(actor => actor.ActorId == "player")
        .Skills.Single(skill => skill.SkillId == "skill-b");
    run.Equal(50L, b.Deadline.DurationFrames);
    run.Equal(150L, b.Deadline.ReadyFrame);

    port.Start("skill-c", 1, 0);
    run.False(store.CaptureState().Actors.Single(actor => actor.ActorId == "player")
        .Skills.Any(skill => skill.SkillId == "skill-c"));
});

run.Case("技能冷却：GM 无冷却开关不写快照，快照往返保留截止帧", () =>
{
    long now = 10;
    var store = new CombatCooldownStore(() => now);
    var port = store.ForActor("player");
    port.Start("skill-a", 30);

    store.SetFree("player", true);
    run.True(port.IsReady("skill-a"), "无冷却开关打开后一律就绪");
    run.Equal(0, port.Snapshot().Count, "无冷却的角色不写快照");
    store.SetFree("player", false);
    run.False(port.IsReady("skill-a"), "关掉开关后旧的冷却还在");

    port.Start("skill-b", 30);
    var captured = store.CaptureState();

    // 清冷却保留开关：复活流程不该顺手关掉调试开关。
    store.SetFree("player", true);
    port.Reset();
    run.True(port.IsReady("skill-b"));
    run.True(store.IsFree("player"), "重置不动调试开关");
    store.SetFree("player", false);

    var restored = new CombatCooldownStore(() => now);
    restored.RestoreState(captured);
    var restoredPort = restored.ForActor("player");
    run.False(restoredPort.IsReady("skill-a"), "快照往返保留截止帧");
    run.False(restoredPort.IsReady("skill-b"));

    restored.RemoveActor("player");
    run.True(restoredPort.IsReady("skill-b"), "角色离队后冷却一起清掉");
});

run.Case("会话：战斗时钟、技能冷却与恢复相位随存档往返", () =>
{
    var root = FindContentRoot();
    if (root is null)
    {
        Console.WriteLine("      跳过：没找到 public/content");
        return;
    }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);

    Session NewSession(BattleCharacter card)
    {
        var journal = new QuestJournal();
        var result = new Session { Bag = card.Bag, Hero = card, Journal = journal };
        return result;
    }

    var session = NewSession(hero);
    // 100 毫秒 = 1 帧 + 40 毫秒余数；余数必须跟着存档走，否则读档后每次推进都白送一点。
    session.AdvanceCombatFrames(100, _ => true);
    run.Equal(1L, session.CombatTick);
    run.Equal(40L, session.CombatClock.FractionalMs);

    var halting = NewSession(new BattleCharacter("dark-knight", stats, items));
    halting.AdvanceCombatFrames(100, _ => false);
    run.Equal(1L, halting.CombatTick, "第一帧之后被叫停");
    run.Equal(0L, halting.CombatClock.FractionalMs, "停下时不再累积余数");

    session.Cooldowns.ForActor("player").Start("skill-a", 30);
    session.ResourceRecovery.AdvanceToFrame(session.CombatTick, []);

    var json = JsonSerializer.Serialize(session.CaptureState(), SaveGameStore.JsonOptions);
    var snapshot = JsonSerializer.Deserialize<SessionState>(json, SaveGameStore.JsonOptions)!;

    var restoredHero = new BattleCharacter("dark-knight", stats, items);
    var restored = NewSession(restoredHero);
    restored.RestoreState(snapshot);

    run.Equal(1L, restored.CombatTick, "时钟帧号跟着存档回来");
    run.Equal(40L, restored.CombatClock.FractionalMs, "不足一帧的余数也要回来");
    run.Equal(1800L, restored.Cooldowns.ForActor("player").Snapshot()["skill-a"].RemainingMs,
        "读档后技能该还在冷却里");
    run.Equal(60L, restored.ResourceRecovery.CaptureState().ElapsedMs, "恢复时钟对齐到整帧");
    run.Equal(session.ResourceRecovery.CaptureState().DeadlineTick,
        restored.ResourceRecovery.CaptureState().DeadlineTick, "脉冲相位不变");

    snapshot.CombatClock = null;
    snapshot.Cooldowns = null;
    snapshot.ResourceRecovery = new CombatResourceRecoveryState { ElapsedMs = 3000, DeadlineTick = 10 };
    var fromLegacy = NewSession(new BattleCharacter("dark-knight", stats, items));
    fromLegacy.RestoreState(snapshot);
    run.Equal(50L, fromLegacy.CombatTick, "3000 毫秒折算成 50 帧");
    run.Equal(0L, fromLegacy.CombatClock.FractionalMs);
    run.Equal(3000L, fromLegacy.ResourceRecovery.CaptureState().ElapsedMs);
    run.Equal(0, fromLegacy.Cooldowns.CaptureState().Actors.Count, "旧档没有冷却记录");
});

run.Case("单元序列：同一目标失败后跳过，设置属性的单元不受限制", () =>

{
    var units = new List<ICombatUnit>
    {
        new NumericUnit { Effect = "physical" },
        new SetAttributeUnit { Effect = "set-attribute", Attribute = "moveSpeed" },
        new ClearAttributeUnit { Effect = "clear-attribute", Attribute = "moveSpeed" },
    };
    var port = new TestSkillUnitPort
    {
        TargetsByCue = [["t1", "t2"], ["t1", "t2"], ["t1", "t2"]],
        UnitResult = (cue, target) => cue == 0 && target == "t2" ? SkillUnitOutcome.Failure : SkillUnitOutcome.Success,
    };

    var results = SkillUnitSequence.Resolve(units, port);
    run.Equal("0:t1:Success,0:t2:Failure,1:t1:Success,1:t2:Success,2:t1:Success,2:t2:Success",
        string.Join(",", results.Select(r => $"{r.CueIndex}:{r.TargetId}:{r.Outcome}")));
});

run.Case("单元序列：一次结算最多 16 个目标", () =>
{
    var units = new List<ICombatUnit> { new NumericUnit { Effect = "physical" } };
    var many = Enumerable.Range(0, 20).Select(index => $"t{index}").ToArray();
    var port = new TestSkillUnitPort { TargetsByCue = [many] };

    var results = SkillUnitSequence.Resolve(units, port);
    run.Equal(16, results.Count);
    run.Equal("t15", results[^1].TargetId);
});

run.Case("技能几何：固定落点过渡按帧抵达", () =>
{
    var start = new Vec2(0, 0);
    var target = new Vec2(100, 0);

    var first = SkillGeometry.PositionTransitionStep(start, target, 12, 0, 3);
    run.Equal(new Vec2(12, 0), first.Position);
    run.False(first.Arrived);

    var last = SkillGeometry.PositionTransitionStep(start, target, 12, 3, 3);
    run.Equal(target, last.Position, "到指定帧就强制落到目标点");
    run.True(last.Arrived);
});

run.Case("命中结算门：死亡动作排队到最外层，同一目标只执行最后一次登记", () =>
{
    var gate = new CombatHitResolution();
    var order = new List<string>();

    gate.DeferDefeat("enemy-1", () => order.Add("immediate"));
    run.Equal("immediate", string.Join(",", order));

    order.Clear();
    var nested = gate.Run(() =>
    {
        gate.DeferDefeat("enemy-1", () => order.Add("first"));
        run.True(gate.IsAwaitingDefeat("enemy-1"), "结算中算「待判死」");

        gate.DeferDefeat("enemy-1", () => order.Add("second"));
        run.Equal(0, order.Count, "结算没结束前不执行死亡动作");

        return gate.Run(() => gate.IsAwaitingDefeat("enemy-1"));
    });

    run.True(nested, "内层结算看到的仍是「待判死」");
    run.Equal("second", string.Join(",", order), "最外层结束时只执行最后登记的那一次");
    run.False(gate.IsAwaitingDefeat("enemy-1"), "结算结束后不再挂起");
});

run.Case("被动修正：六个职业 × 七种主手 × 隐身 × 两级逐项对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "passive-modifiers-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 passive-modifiers-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var checkedCases = 0;
    foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
    {
        var classId = item.GetProperty("classId").GetString()!;
        var weapon = item.GetProperty("weapon").GetString()!;
        var stealthed = item.GetProperty("stealthed").GetBoolean();
        var level = item.GetProperty("level").GetInt32();

        var loadout = new SkillLoadout();
        foreach (var id in catalog.Class(classId)!.SkillIds) loadout.Levels[id] = level;

        var actual = PassiveSkills.PassiveModifiers(catalog, classId, loadout, weapon, stealthed)
            .Select(DescribeModifier);
        var expected = item.GetProperty("modifiers").EnumerateArray().Select(value => value.GetString()!);
        run.Equal(string.Join(",", expected), string.Join(",", actual),
            $"{classId}／{weapon}／隐身={stealthed}／{level} 级");
        checkedCases += 1;
    }

    run.Equal(168, checkedCases, "基准覆盖 168 组");
});

run.Case("概率门槛：1000 必成，其余对 1–999 的随机数严格小于", () =>
{
    run.True(ActionStateRules.PassesChanceThreshold(1000, () => 0.999), "1000 不看随机数");
    run.True(ActionStateRules.PassesChanceThreshold(500, () => 0.0), "掷出 1 < 500");
    run.False(ActionStateRules.PassesChanceThreshold(500, () => 0.5), "掷出 500 不算通过");
    run.True(ActionStateRules.PassesChanceThreshold(501, () => 0.5), "掷出 500 < 501");
});

run.Case("状态单元取值：等级越界钳到两端，等级表为空不施加", () =>
{
    var unit = new StateCombatUnit
    {
        Effect = "state",
        State = "stun",
        Ranks =
        [
            new StateUnitRank { ChanceThreshold = 1000, DurationMs = 500 },
            new StateUnitRank { ChanceThreshold = 1000, DurationMs = 900 },
        ],
    };

    run.Equal(500, ActionStateRules.ResolveActionState(unit, 0, () => 0).DurationMs);
    run.Equal(900, ActionStateRules.ResolveActionState(unit, 9, () => 0).DurationMs);

    var empty = new StateCombatUnit { Effect = "state", State = "stun" };
    var (applies, duration) = ActionStateRules.ResolveActionState(empty, 1, () => 0);
    run.False(applies);
    run.Equal(0, duration);
});

run.Case("状态施加：无来源、概率、免疫、抵抗四个拒绝理由", () =>
{
    static StateApplicationRequest Request(string relation, string kind, int chance = 1000, int immunity = 0,
        int resistance = 0, StateApplicationRules? rules = null) => new()
    {
        State = "stun",
        SourceRelation = relation,
        Kind = kind,
        ChanceThreshold = chance,
        SpecificImmunityValue = immunity,
        ResistanceThreshold = resistance,
        Rules = rules,
    };

    run.Equal("missing-source", StateApplication.Resolve(Request("none", "action"), () => 0).Reason);
    run.Equal("chance", StateApplication.Resolve(Request("hostile", "action", chance: 100), () => 0.5).Reason);
    run.Equal("immune", StateApplication.Resolve(Request("hostile", "buff", immunity: 1), () => 0).Reason);
    run.Equal("immune", StateApplication.Resolve(
        Request("hostile", "buff", rules: new StateApplicationRules { ImmuneToAllStates = true }), () => 0).Reason);
    run.Equal("immune", StateApplication.Resolve(
        Request("hostile", "buff", rules: new StateApplicationRules { ImmuneStates = ["stun"] }), () => 0).Reason);

    run.Equal("resisted", StateApplication.Resolve(Request("hostile", "buff", resistance: 500), () => 0.0).Reason);
    run.Equal(null, StateApplication.Resolve(Request("hostile", "buff", resistance: 500), () => 0.9).Reason);
    run.Equal(null, StateApplication.Resolve(Request("friendly", "buff", resistance: 500), () => 0.0).Reason);

    var allowed = StateApplication.Resolve(Request("hostile", "buff", rules: new StateApplicationRules
    {
        DurationScale = 0.5,
        FrameDurationScale = 0.5,
        BuffDurationScaleByState = new Dictionary<string, double> { ["stun"] = 0.5 },
    }), () => 0);
    run.True(allowed.Allowed);
    run.Equal(0.5, allowed.DurationScale);
    run.Equal(0.5, allowed.BuffDurationScale);
    run.Equal(0.5, allowed.FrameDurationScale);
});

run.Case("状态时长：15/10 换算、折半分支与帧龄折算", () =>
{
    var profile = StateApplication.FrameStateDurationProfile(100);
    run.Equal(150, profile.Normal.LastActiveFrame);
    run.Equal(75, profile.TargetShortened!.LastActiveFrame, "50 先折半成 50 再按 15/10 换算");

    StateApplicationDecision Accept(double duration, double buff) => StateApplicationDecision.Accept(duration, buff, 1);

    run.Equal(150, StateApplication.SelectStateDurationProfile(profile, Accept(1, 1)).LastActiveFrame);
    run.Equal(75, StateApplication.SelectStateDurationProfile(profile, Accept(0.5, 1)).LastActiveFrame);

    var halved = new StateDurationProfile
    {
        Normal = new StateDurationBound { LastActiveFrame = 150 },
        BuffShortened = new StateDurationBound { LastActiveFrame = 90 },
    };
    run.Equal(90, StateApplication.SelectStateDurationProfile(halved, Accept(1, 0.5)).LastActiveFrame);

    var scaled = StateApplicationDecision.Accept(1, 1, 0.5);
    run.Equal(75, StateApplication.SelectStateDurationProfile(profile, scaled).LastActiveFrame);
    run.Throws(() => StateApplication.SelectStateDurationProfile(halved, Accept(0.5, 1)), "状态持续方案缺少 targetShortened");
});

run.Case("增益容器：强度覆盖、叠层放大操作数与临时属性优先", () =>
{
    var conditions = new CombatConditions();
    var buff = MakeBuff(1000, maxStacks: 3, modifiers: [new AttributeModifier
    {
        Attribute = "defense", Operation = new CombatOperation { Kind = "add", Amount = 100 },
    }]);

    run.True(conditions.ApplyBuff("player", "guard", 10, buff));
    run.Equal(110, conditions.Attribute("player", "defense", 10));

    run.True(conditions.ApplyBuff("player", "guard", 10, buff));
    run.Equal(210, conditions.Attribute("player", "defense", 10));
    run.Equal(2, conditions.Snapshot("player").Single().StackCount);

    run.False(conditions.ApplyBuff("player", "guard", 5, buff));
    run.Equal(210, conditions.Attribute("player", "defense", 10));

    conditions.SetTemporaryAttribute("player", "defense", 777);
    run.Equal(777, conditions.Attribute("player", "defense", 10));
    conditions.ClearTemporaryAttribute("player", "defense");
    run.Equal(210, conditions.Attribute("player", "defense", 10));

    conditions.RemoveBuff("player", "guard");
    run.Equal(10, conditions.Attribute("player", "defense", 10));
});

run.Case("增益容器：帧计时状态到期与状态门", () =>
{
    var conditions = new CombatConditions();
    conditions.ApplyStateFrames("player", "stun", 2);
    run.True(conditions.HasState("player", "stun"));
    run.True(conditions.CannotAct("player"));
    run.False(conditions.CanMove("player"));

    conditions.Update(180);
    run.True(conditions.HasState("player", "stun"), "第 3 帧还没到期");
    conditions.Update(60);
    run.False(conditions.HasState("player", "stun"));
    run.True(conditions.CanAct("player"));

    conditions.ApplyState("player", "stealthed", 1000);
    run.False(conditions.CanTarget("player"));
    run.True(conditions.CanTarget("player", hostile: false));
});

run.Case("增益容器：毫秒增益按 tickMs 回调周期效果", () =>
{
    var conditions = new CombatConditions();
    var ticks = new List<int>();
    var buff = MakeBuff(250, tickMs: 100, periodic: [new FixedDamageTick
    {
        Kind = "fixed-damage", Amount = 5,
    }]);

    conditions.ApplyBuff("player", "poison", 1, buff);
    conditions.Update(100, tick =>
    {
        run.Equal("player", tick.TargetId);
        run.Equal("poison", tick.BuffId);
        ticks.Add(((FixedDamageTick)tick.Effect).Amount);
    });

    run.Equal("5,5", string.Join(",", ticks));
    run.True(conditions.HasBuff("player", "poison"));

    conditions.Update(300);
    run.False(conditions.HasBuff("player", "poison"), "过了 250 毫秒自然到期");
});

run.Case("增益容器：显式解除状态连带母增益，自然到期只删节点", () =>
{
    var conditions = new CombatConditions();
    var buff = MakeBuff(1000, states: ["stun"]);

    conditions.ApplyBuff("player", "hammer", 1, buff);
    run.True(conditions.HasBuff("player", "hammer"));
    run.True(conditions.HasState("player", "stun"));
    run.Equal("hammer", conditions.Snapshot("player").Single(entry => entry.Kind == "state").OwnerBuff);

    conditions.RemoveState("player", "stun");
    run.False(conditions.HasBuff("player", "hammer"), "显式解除状态会连带移除母增益");

    conditions.ApplyBuff("player", "hammer", 1, buff);
    conditions.Update(1000);
    run.False(conditions.HasBuff("player", "hammer"));
    run.False(conditions.HasState("player", "stun"), "自然到期把状态一起带走");
});

run.Case("增益容器：快照往返与持续特效的帧龄门槛", () =>
{
    var conditions = new CombatConditions();
    var buff = MakeBuff(4560, frames: new BuffSimulationFrames { LastActiveFrame = 75 },
        loops: [new BuffLoopEffect { Effect = "soul-sword", FirstFrameAge = 2 }]);

    conditions.ApplyBuff("player", "dark-strike", 101, buff);
    conditions.ApplyStateFrames("player", "stun", 5);

    var state = conditions.CaptureState();
    var restored = new CombatConditions();
    restored.RestoreState(state);

    run.True(restored.HasBuff("player", "dark-strike"));
    run.True(restored.HasState("player", "stun"));
    run.Equal(75, restored.BuffLifetimeFrames("player", "dark-strike"));

    run.Equal(0, restored.BuffLoopFrames("player").Count);
    restored.Update(60);
    run.Equal(0, restored.BuffLoopFrames("player").Count, "年龄 1 还没到门槛");
    restored.Update(60);
    run.Equal("soul-sword", string.Join(",", restored.BuffLoopFrames("player").Select(frame => frame.Effect)));
});

run.Case("周期效果：固定伤害叠层、血咒双命令与治疗加成", () =>
{
    var attributes = new CombatActorAttributes
    {
        PhysicalAttack = 100,
        MagicAttack = 100,
        PhysicalDamageRate = 100,
        MagicDamageRate = 0,
        StealthDamageRate = 0,
        MaxHealth = 1000,
        PrimaryAttributes = new PrimaryAttributes { Intelligence = 20, Mentality = 10 },
    };
    var source = new PeriodicSource
    {
        ClassId = "priest",
        Attributes = attributes,
        LifeFeastRate = 0,
        AttributeValue = attribute => attribute == "magicAttack" ? 200 : 0,
    };

    var fixedDamage = CombatPeriodic.Resolve(
        new FixedDamageTick { Kind = "fixed-damage", Amount = 7 },
        new PeriodicContext { BuffId = "poisoned", StackCount = 2, Source = source, TargetHealingRate = 1000 });
    run.Equal(14, fixedDamage.Single().Amount, "固定伤害按层数乘");

    var curse = CombatPeriodic.Resolve(
        new MagicCurseTick { Kind = "magic-curse", RatePerThousand = 1000, HealEffect = "drain" },
        new PeriodicContext { BuffId = "blood-curse", StackCount = 1, Source = source, TargetHealingRate = 1000 });
    run.Equal(2, curse.Count, "先恢复、再打伤害");
    run.True(curse[0] is HealSourceCommand, "没有血之盛宴时恢复给自己");
    run.Equal("drain", ((HealSourceCommand)curse[0]).Effect);
    run.Equal(132, curse[0].Amount);
    run.Equal(132, curse[1].Amount);

    var healing = CombatPeriodic.Resolve(
        new HealingTick
        {
            Kind = "healing", Attribute = "magicAttack", Operation = new CombatOperation { Kind = "keep" },
        },
        new PeriodicContext { BuffId = "light-command", StackCount = 1, Source = source, TargetHealingRate = 500 });
    run.Equal(101, healing.Single().Amount, "200×50% = 100，再加 精力 10/10 = 1");

    var normal = CombatPeriodic.Resolve(
        new NormalDamageTick { Kind = "normal-damage", Operation = new CombatOperation { Kind = "keep" } },
        new PeriodicContext { BuffId = "burn", StackCount = 1, Source = source, TargetHealingRate = 1000 });
    run.Equal(100, normal.Single().Amount);

    run.Throws(() => CombatPeriodic.Resolve(
        new MagicCurseTick { Kind = "magic-curse", RatePerThousand = 100 },
        new PeriodicContext { BuffId = "blood-curse", StackCount = 1, Source = null, TargetHealingRate = 1000 }),
        "requires its source actor");
});

run.Case("战斗视图：副手不是盾时盾挡清零，减伤按增益重算", () =>
{
    var conditions = new CombatConditions();
    var baseFighter = new Fighter
    {
        Level = 1,
        PhysicalAttack = 18,
        Defense = 24,
        DefenseRate = 0,
        ShieldBlock = 120,
        MaxHealth = 1672,
    };

    var plain = conditions.FighterView("player", baseFighter);
    run.Equal(120, plain.ShieldBlock);
    run.Equal(230, plain.PhysicalResistance, "24 点防御对 1 级角色是 230‰ 减伤");

    conditions.ApplyBuff("player", "guardian", 1, MakeBuff(1000, modifiers: [new AttributeModifier
    {
        Attribute = "defense", Operation = new CombatOperation { Kind = "increase-thousandth", RatePerThousand = 500 },
    }]));
    var guarded = conditions.FighterView("player", baseFighter);
    run.Equal(36, guarded.Defense, "24 × 1.5 = 36");
    run.True(guarded.PhysicalResistance > plain.PhysicalResistance, "防御涨了减伤也要涨");

    var noShield = conditions.FighterView("player", baseFighter, equipment: new FighterEquipment
    {
        OffhandShield = false,
    });
    run.Equal(0, noShield.ShieldBlock, "实际副手不是盾就没有盾挡");
});

run.Case("增益容器：挂增益时先取上限快照，再把效果交给资源端口", () =>
{
    var port = new TestBuffResourcePort();
    var conditions = new CombatConditions(port);
    conditions.ApplyBuff("player", "guardian", 1, MakeBuff(1000));

    run.Equal(1, port.Captured, "挂载前后各取一次上限快照");
    run.Equal(1, port.Applied);
    run.Equal("player", port.LastActorId);
    conditions.RemoveBuff("player", "guardian");
    run.Equal(2, port.Captured, "移除时也取一次，好让上限变化能被回填");
});

run.Case("技能施放：真实内容里 54 个动作的每个单元都能落地", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    var conditions = new CombatConditions();
    var port = new TestEffectPort(conditions);
    var executor = new CombatEffectExecutor(port);

    var actions = 0;
    var units = 0;
    var success = 0;
    var failure = 0;
    var skipped = 0;
    foreach (var skill in catalog.Skills.Values)
    {
        if (skill.Action?.CombatUnits is not { Count: > 0 } list) continue;
        actions += 1;
        for (var index = 0; index < list.Count; index += 1)
        {
            units += 1;
            var outcome = executor.Execute(new SkillUnitHit
            {
                CasterId = "caster",
                Skill = new SkillPlayback(skill.Id, skill.Action, 1),
                CueIndex = index,
                TargetId = "enemy",
            });
            if (outcome == SkillUnitOutcome.Success) success += 1;
            else if (outcome == SkillUnitOutcome.Skipped) skipped += 1;
            else failure += 1;
        }
    }

    run.Equal(54, actions, "主动动作数");
    run.Equal(109, units, "结算单元数");
    run.Equal(units, success + failure + skipped, "每个单元都要给出一个结果");
    run.True(success > 60, $"多数单元应当结算成功（实际 {success}）");
    run.Equal(0, skipped, "同一批假角色上不该出现跳过");
});

run.Case("战斗规则：选点／治疗／队伍增益／冲锋／背后瞬移／三条公式逐项对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "combat-rules-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 combat-rules-output.json 基准"); return; }

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var data = document.RootElement;

    static Vec2 Point(JsonElement value) =>
        new(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle());

    static Combatant Actor(JsonElement value)
    {
        var bounds = value.GetProperty("bounds");
        var box = new CollisionBox
        {
            X = bounds.GetProperty("x").GetSingle(),
            Y = bounds.GetProperty("y").GetSingle(),
            Width = bounds.GetProperty("width").GetSingle(),
            Height = bounds.GetProperty("height").GetSingle(),
        };
        var states = value.TryGetProperty("states", out var list)
            ? list.EnumerateArray().Select(item => item.GetString()!).ToList()
            : null;
        return new Combatant(value.GetProperty("id").GetString()!, value.GetProperty("team").GetString()!,
            Point(value.GetProperty("position")), box,
            value.TryGetProperty("facing", out var facing) ? facing.GetString() : null, states);
    }

    static List<Combatant> Actors(JsonElement list) => list.EnumerateArray().Select(Actor).ToList();

    var splash = data.GetProperty("splashSelect");
    var splashSource = Actor(splash.GetProperty("source"));
    var splashPool = Actors(splash.GetProperty("pool"));
    var splashRange = splash.GetProperty("rangeUnits").GetInt32();
    foreach (var item in splash.GetProperty("cases").EnumerateArray())
    {
        var options = item.GetProperty("options");
        var selection = AttackSplash.SelectCenter(splashSource, splashPool, splashRange,
            options.TryGetProperty("currentTargetId", out var current) ? current.GetString() : null,
            options.TryGetProperty("gazeTargetId", out var gaze) ? gaze.GetString() : null,
            options.TryGetProperty("retainCurrentTarget", out var retain) && retain.GetBoolean());
        var expectedTarget = item.GetProperty("targetId");
        run.Equal(expectedTarget.ValueKind == JsonValueKind.Null ? null : expectedTarget.GetString(),
            selection.TargetId, $"选点目标 {options}");
        run.Equal(Point(item.GetProperty("center")), selection.Center, $"选点中心 {options}");
        run.Equal(item.GetProperty("consumedGaze").GetBoolean(), selection.ConsumedGaze, "消耗凝视");
    }

    var splashTargets = data.GetProperty("splashTargets");
    run.Equal(string.Join(",", splashTargets.GetProperty("targets").EnumerateArray().Select(item => item.GetString())),
        string.Join(",", AttackSplash.Targets(Actor(splashTargets.GetProperty("source")),
            Point(splashTargets.GetProperty("center")), Actors(splashTargets.GetProperty("pool")),
            splashTargets.GetProperty("rangeUnits").GetInt32()).Select(actor => actor.Id)));

    foreach (var item in data.GetProperty("healScore").GetProperty("cases").EnumerateArray())
        run.Equal(item.GetProperty("score").GetInt32(),
            HealTarget.Score(item.GetProperty("health").GetInt32(), item.GetProperty("maxHealth").GetInt32(),
                item.GetProperty("dealt").GetBoolean()));

    var heal = data.GetProperty("healSelect");
    var healSlots = heal.GetProperty("slots").EnumerateArray().Select(item => new HealSlot(
        Actor(item.GetProperty("actor")), item.GetProperty("active").GetBoolean(),
        item.GetProperty("hasDisplay").GetBoolean(),
        item.TryGetProperty("score", out var score) ? score.GetInt32() : null)).ToList();
    run.Equal(heal.GetProperty("target").GetString(),
        HealTarget.Select(Actor(heal.GetProperty("caster")), healSlots, heal.GetProperty("rangeUnits").GetInt32()).Id);

    var party = data.GetProperty("partyBuff");
    var partyCaster = Actor(party.GetProperty("caster"));
    var partySlots = party.GetProperty("slots").EnumerateArray().Select(item => new PartyBuffSlot(
        Actor(item.GetProperty("actor")), item.GetProperty("active").GetBoolean(),
        item.GetProperty("hasDisplay").GetBoolean())).ToList();
    foreach (var item in party.GetProperty("cases").EnumerateArray())
        run.Equal(string.Join(",", item.GetProperty("targets").EnumerateArray().Select(value => value.GetString())),
            string.Join(",", PartyBuffTargets.Resolve(partyCaster, partySlots,
                item.GetProperty("rangeUnits").GetInt32()).Select(actor => actor.Id)),
            $"队伍增益 range={item.GetProperty("rangeUnits").GetInt32()}");

    var chargeBlock = data.GetProperty("charge");
    var definition = chargeBlock.GetProperty("charge");
    var charge = new SkillCharge
    {
        ArrivalFrame = definition.GetProperty("arrivalFrame").GetInt32(),
        CloseDistanceUnits = definition.GetProperty("closeDistanceUnits").GetInt32(),
        LungeDistancePx = definition.GetProperty("lungeDistancePx").GetSingle(),
        StoppingDistanceUnits = definition.GetProperty("stoppingDistanceUnits").GetInt32(),
        GridSizePx = definition.GetProperty("gridSizePx").GetInt32(),
    };
    foreach (var item in chargeBlock.GetProperty("cases").EnumerateArray())
    {
        var blocked = item.GetProperty("blocked").EnumerateArray().Select(value => value.GetString()!)
            .ToHashSet();
        var plan = TargetCharge.Plan(
            new Combatant("caster", "player", Point(item.GetProperty("caster")), new CollisionBox()),
            new Combatant("target", "enemy", Point(item.GetProperty("target")), new CollisionBox()),
            charge, point => !blocked.Contains($"{point.X},{point.Y}"));
        var expectedDestination = item.GetProperty("destination");
        run.Equal(expectedDestination.ValueKind == JsonValueKind.Null ? (Vec2?)null : Point(expectedDestination),
            plan?.Destination, $"冲锋落点 {item.GetProperty("name").GetString()}");
        run.Equal(item.GetProperty("close").ValueKind == JsonValueKind.Null ? (bool?)null : item.GetProperty("close").GetBoolean(),
            plan?.Close, $"冲锋贴身标记 {item.GetProperty("name").GetString()}");
    }

    // 背后瞬移：四向、边界裁剪、不可达。
    foreach (var item in data.GetProperty("rearTeleport").GetProperty("cases").EnumerateArray())
    {
        var blocked = item.GetProperty("blocked").EnumerateArray().Select(value => value.GetString()!)
            .ToHashSet();
        var bounds = item.GetProperty("bounds");
        var spot = RearTeleportRules.Plan(
            new Combatant("caster", "player", Point(item.GetProperty("caster")), new CollisionBox()),
            new Combatant("target", "enemy", Point(item.GetProperty("target")), new CollisionBox(),
                item.TryGetProperty("facing", out var targetFacing) ? targetFacing.GetString() : "south"),
            point => !blocked.Contains($"{point.X},{point.Y}"),
            point => !blocked.Contains($"{point.X},{point.Y}"),
            bounds.ValueKind == JsonValueKind.Null
                ? null
                : new Rect(bounds.GetProperty("x").GetSingle(), bounds.GetProperty("y").GetSingle(),
                    bounds.GetProperty("width").GetSingle(), bounds.GetProperty("height").GetSingle()));
        var expectedSpot = item.GetProperty("spot");
        run.Equal(expectedSpot.ValueKind == JsonValueKind.Null ? (Vec2?)null : Point(expectedSpot), spot,
            $"瞬移落点 {item.GetProperty("name").GetString()}");
    }

    var arithmetic = data.GetProperty("arithmetic");
    foreach (var item in arithmetic.GetProperty("drain").EnumerateArray())
    {
        var result = ManaDrain.Drain(item.GetProperty("amount").GetInt32(), item.GetProperty("targetMana").GetInt32(),
            item.GetProperty("casterMana").GetInt32(), item.GetProperty("maxMana").GetInt32());
        run.Equal(item.GetProperty("targetManaAfter").GetInt32(), result.TargetMana, "吸蓝后的目标魔力");
        run.Equal(item.GetProperty("casterManaAfter").GetInt32(), result.CasterMana, "吸蓝后的施法者魔力");
        run.Equal(item.GetProperty("deducted").GetInt32(), result.Deducted);
    }

    foreach (var item in arithmetic.GetProperty("healing").EnumerateArray())
        run.Equal(item.GetProperty("amount").GetInt32(),
            ActionHealing.Apply(37, item.GetProperty("rate").GetInt32()));

    foreach (var item in arithmetic.GetProperty("cycle").EnumerateArray())
        run.Equal(item.GetProperty("frames").GetInt32(),
            CombatTempo.AttackCycleFrames(item.GetProperty("base").GetInt32(),
                item.GetProperty("rate").GetInt32(), item.GetProperty("strike").GetInt32()));

    var cues = data.GetProperty("cueTargets");
    var cueCaster = Actor(cues.GetProperty("caster"));
    var cuePool = Actors(cues.GetProperty("pool"));
    var cueSlots = cues.GetProperty("slots").EnumerateArray().Select(item => (PartyBuffSlot?)new PartyBuffSlot(
        Actor(item.GetProperty("actor")), item.GetProperty("active").GetBoolean(),
        item.GetProperty("hasDisplay").GetBoolean())).ToList();
    int? HealScoreOf(string id) => id == "self" ? 90 : id == "ally-near" ? 40 : null;
    foreach (var item in cues.GetProperty("cases").EnumerateArray())
    {
        var bonus = item.TryGetProperty("rangeBonus", out var rangeBonus) ? rangeBonus.GetInt32() : 0;
        var selectedId = item.GetProperty("selected");
        var selected = selectedId.ValueKind == JsonValueKind.Null
            ? null
            : cuePool.FirstOrDefault(actor => actor.Id == selectedId.GetString());
        var picked = CueTargets.Resolve(item.GetProperty("target").GetString()!,
            item.GetProperty("range").GetSingle(),
            item.GetProperty("metric").ValueKind == JsonValueKind.Null ? null : item.GetProperty("metric").GetString(),
            cueCaster, selected, cuePool, cueSlots, bonus, HealScoreOf);
        run.Equal(string.Join(",", item.GetProperty("targets").EnumerateArray().Select(value => value.GetString())),
            string.Join(",", picked.Select(actor => actor.Id)),
            $"触发项 {item.GetProperty("target").GetString()} range={item.GetProperty("range").GetInt32()}");
    }
});

run.Case("战斗控制器：技能扣蓝、先占冷却，单元按触发项落到目标身上", () =>
{
    var action = new SkillAction
    {
        CooldownFrames = 90,
        ApproachDistanceUnits = 4,
        SearchRadiusPx = 200,
        CombatUnits = [new SimpleCombatUnit { Effect = "visual" }],
        Events =
        [
            new UnitsEvent
            {
                Frame = 2,
                Cues = [new SkillEffectCue { Target = "current", TargetRangePx = 32 }],
            },
        ],
    };
    var skill = new SkillPlayback("dark-strike", action, 1, 30);
    var store = new CombatCooldownStore(() => 0);
    var port = new TestCombatPort { Store = store, Mana = 100 };
    port.Target = new Combatant("player", "player", new Vec2(20, 0), new CollisionBox(), "south");
    var controller = new CombatController("caster", null, port);

    run.Equal("started", controller.Request(skill, "player"));
    run.Equal(30, port.Spent, "起手就扣蓝");
    run.False(store.ForActor("caster").IsReady("dark-strike"), "起手就占冷却");

    port.PendingFrame?.Invoke(2);
    run.Equal(1, port.Units.Count, "第 2 帧派发一次单元");
    run.Equal("player", port.Units[0].TargetId);
    run.Equal("dark-strike", port.Units[0].Skill.Id);

    port.PendingComplete?.Invoke();
    run.Equal(CombatState.Idle, controller.State);
    run.Equal("cooldown", controller.Request(skill, "player"));
});

run.Case("效果解释器：物理单元走命中结算，无敌只挡血量不挡其它单元", () =>
{
    var conditions = new CombatConditions();
    var port = new TestEffectPort(conditions);
    var executor = new CombatEffectExecutor(port);

    var action = new SkillAction
    {
        CooldownFrames = 0,
        ApproachDistanceUnits = 4,
        SearchRadiusPx = 100,
        CombatUnits =
        [
            new NumericUnit
            {
                Effect = "physical",
                Source = "physicalAttack",
                Ranks = [new DamageRank { FixedAddition = 10 }],
            },
        ],
    };
    var skill = new SkillPlayback("test-strike", action, 1);

    var result = executor.Execute(new SkillUnitHit
    {
        CasterId = "caster", Skill = skill, CueIndex = 0, TargetId = "enemy",
    });
    run.Equal(SkillUnitOutcome.Success, result);
    run.Equal(1, port.Hits.Count);
    run.Equal(HitKind.Hit, port.Hits[0].Kind);
    run.Equal(10, port.Hits[0].Damage);

    conditions.SetInvincible("enemy", true);
    var blocked = executor.Execute(new SkillUnitHit
    {
        CasterId = "caster", Skill = skill, CueIndex = 0, TargetId = "enemy",
    });
    run.Equal(SkillUnitOutcome.Failure, blocked);
    run.Equal(1, port.Shown.Count, "挡下来的那一下也要给表现层一个 0 伤害");
    run.Equal(0, port.Shown[0].Damage);

    run.Equal(SkillUnitOutcome.Skipped, executor.Execute(new SkillUnitHit
    {
        CasterId = "caster", Skill = skill, CueIndex = 0, TargetId = "gone",
    }));
    run.Equal(SkillUnitOutcome.Failure, executor.Execute(new SkillUnitHit
    {
        CasterId = "gone", Skill = skill, CueIndex = 0, TargetId = "enemy",
    }));
});

run.Case("技能反应：22 门技能的特效清单对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }
    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "reactions-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 reactions-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var effects = document.RootElement.GetProperty("effects");
    var checkedSkills = 0;
    foreach (var entry in effects.EnumerateObject())
    {
        var skill = catalog.Skills[entry.Name];
        var expected = entry.Value.EnumerateArray().Select(item => item.GetString()!).ToList();
        var actual = SkillReactions.Effects(skill).ToList();
        run.Equal(string.Join(",", expected), string.Join(",", actual), $"{entry.Name} 特效清单");
        checkedSkills += 1;
    }

    run.Equal(22, checkedSkills, "基准覆盖 22 门带反应的技能");
});

run.Case("技能反应：六个职业的已学反应对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }
    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "reactions-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 reactions-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var checkedClasses = 0;
    foreach (var entry in document.RootElement.GetProperty("learned").EnumerateArray())
    {
        var classId = entry.GetProperty("classId").GetString()!;
        var loadout = new SkillLoadout();
        foreach (var id in entry.GetProperty("skills").EnumerateArray())
            loadout.Levels[id.GetString()!] = 3;

        var kinds = SkillReactions.LearnedReactions(catalog, classId, loadout)
            .Select(reaction => reaction.Kind);
        var expected = entry.GetProperty("kinds").EnumerateArray().Select(item => item.GetString()!);
        run.Equal(string.Join(",", expected), string.Join(",", kinds), $"{classId} 已学反应");
        checkedClasses += 1;
    }

    run.Equal(6, checkedClasses, "基准覆盖 6 个职业");
});

run.Case("技能反应：26 个事件阶段逐条对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }
    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "reactions-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 reactions-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var checkedCases = 0;
    foreach (var entry in document.RootElement.GetProperty("cases").EnumerateArray())
    {
        var name = entry.GetProperty("name").GetString()!;
        var skill = catalog.Skills[entry.GetProperty("skillId").GetString()!];
        var reactions = skill.ReactionsByRank![entry.GetProperty("rank").GetInt32()]!;
        var seed = (uint)entry.GetProperty("seed").GetInt32();
        var ports = new ReactionPorts(seed, entry.GetProperty("noLearnedBuff").GetBoolean());
        var reactionEvent = BuildSkillReactionEvent(entry.GetProperty("event"));

        var actual = SkillReactions.Resolve(reactions, reactionEvent, ports)
            .Select(item => CanonicalAny(item)).ToList();
        var expected = entry.GetProperty("commands").EnumerateArray()
            .Select(item => CanonicalAny(item)).ToList();

        run.Equal(string.Join(" | ", expected), string.Join(" | ", actual), name);
        checkedCases += 1;
    }

    run.Equal(26, checkedCases, "基准覆盖 26 条事件用例");
});

run.Case("技能反应：嗜血的顺序是「先恢复、再读回魔」", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }
    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "reactions-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 reactions-output.json 基准"); return; }

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var checkedCases = 0;
    foreach (var entry in document.RootElement.GetProperty("bloodThirst").EnumerateArray())
    {
        var random = new Mulberry32((uint)entry.GetProperty("seed").GetInt32());
        var lifeDrain = entry.GetProperty("lifeDrain").GetInt32();
        var mana = entry.GetProperty("mana").GetInt32();
        var commands = new List<SkillReactionCommand>();

        SkillReactions.ExecuteBloodThirst(new BloodThirstEvent
        {
            OwnerId = "owner",
            OpponentId = "opponent",
            DamageKind = "physical",
            Outcome = "hit",
            HitDamage = entry.GetProperty("hitDamage").GetInt32(),
            ChanceThreshold = entry.GetProperty("chanceThreshold").GetInt32(),
        }, random.Next, () => lifeDrain, () => mana, commands.Add);

        var expected = entry.GetProperty("commands").EnumerateArray().Select(item => CanonicalAny(item)).ToList();
        run.Equal(string.Join(" | ", expected), string.Join(" | ", commands.Select(item => CanonicalAny(item))),
            $"嗜血 {lifeDrain}/{mana}");
        checkedCases += 1;
    }

    run.Equal(4, checkedCases, "基准覆盖 4 条嗜血用例");
});

run.Case("增益反应：真实增益上的六种反应对得上基准表", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }
    var fixture = Path.Combine(root, "..", "godot", "tests", "data", "reactions-output.json");
    if (!File.Exists(fixture)) { Console.WriteLine("      跳过：没有 reactions-output.json 基准"); return; }

    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();

    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    var checkedCases = 0;
    foreach (var entry in document.RootElement.GetProperty("buffReactions").EnumerateArray())
    {
        var key = entry.GetProperty("buffId").GetString()!;
        var parts = key.Split('#');
        var skill = catalog.Skills[parts[0]];
        var unitIndex = int.Parse(parts[1].Split('@')[0]);
        var rankIndex = int.Parse(parts[1].Split('@')[1]);
        var unit = (BuffCombatUnit)skill.Action!.CombatUnits![unitIndex];
        var reactions = unit.Ranks[rankIndex].Effect.Reactions!;

        var reactionEvent = BuildBuffReactionEvent(entry.GetProperty("event"));
        var actual = BuffReactions.Resolve(reactions, reactionEvent).Select(item => CanonicalAny(item)).ToList();
        var expected = entry.GetProperty("commands").EnumerateArray().Select(item => CanonicalAny(item)).ToList();

        run.Equal(string.Join(" | ", expected), string.Join(" | ", actual),
            $"{key} {entry.GetProperty("reactionKind").GetString()}");
        checkedCases += 1;
    }

    run.True(checkedCases >= 60, $"基准覆盖整套增益反应（实际 {checkedCases} 条）");
});

run.Case("战斗运行时：定时任务按帧到点，同帧按登记次序", () =>
{
    long frame = 100;
    var runtime = new CombatRuntime(() => frame);
    var log = new List<string>();

    runtime.Schedule("a", 2, () => log.Add("late"));
    runtime.Schedule("a", 0, () => log.Add("first"));
    runtime.Schedule("a", 0, () => log.Add("second"));

    frame += 1;
    runtime.Step(frame);
    run.Equal("first,second", string.Join(",", log), "第 1 帧只跑到点的任务");

    frame += 1;
    runtime.Step(frame);
    run.Equal("first,second,late", string.Join(",", log), "第 2 帧补上延迟两帧那条");

    run.True(runtime.Idle, "跑完就没有在途内容");
    run.Equal(0, runtime.PendingCount, "待执行数归零");

    // 取消回调在到点之前就不该再执行。
    var cancelled = runtime.Schedule("a", 1, () => log.Add("never"));
    cancelled();
    frame += 1;
    runtime.Step(frame);
    run.False(log.Contains("never"), "取消过的任务不执行");

    var threw = false;
    try { runtime.Step(frame + 2); } catch (InvalidOperationException) { threw = true; }
    run.True(threw, "必须逐逻辑帧推进");
});

run.Case("战斗运行时：动作逐帧推进、周期末收尾", () =>
{
    long frame = 0;
    var runtime = new CombatRuntime(() => frame);
    var definition = new BasicAttackDefinition
    {
        CycleFrames = 6,
        FrameDurationMs = CombatClock.LogicFrameMs,
        StrikeFrame = 2,
        Timing = "fixed-animation",
    };
    var attack = new ResolvedBasicAttack
    {
        Definition = definition,
        EffectiveCycleFrames = 6,
        DurationMs = 360,
        Clips = new Dictionary<string, string>(),
        Sequence = new Dictionary<string, int[]>(),
    };
    var action = new CombatAction
    {
        Caster = new Combatant("hero", "player", Vec2.Zero, new CollisionBox()),
        Facing = "south",
        BasicAttack = attack,
        GetAttackSpeedRate = () => 0,
    };

    var frames = new List<int>();
    var completed = 0;
    runtime.Play(action, new ActionTiming(6, CombatClock.LogicFrameMs), frames.Add, () => completed += 1);

    frame += 1;
    runtime.Step(frame);
    run.Equal("1", string.Join(",", frames), "第 1 帧推进到 1");

    run.True(runtime.HasPendingBasicStrike("hero"), "出手帧还没到，仍算有待落的普攻");
    while (frames.Count < 5)
    {
        frame += 1;
        runtime.Step(frame);
    }

    run.Equal("1,2,3,4,5", string.Join(",", frames), "推进到第 5 帧");
    run.False(runtime.HasPendingBasicStrike("hero"), "过了出手帧就不算待落");

    frame += 1;
    runtime.Step(frame);
    run.Equal(1, completed, "周期末收尾一次");
    run.True(runtime.Idle, "收尾后没有在途动作");

    run.False(frames.Contains(0), "第 0 帧不由运行时派发");
});

run.Case("战斗运行时：在途攻击逐段推进并在抵达时回调", () =>
{
    long frame = 0;
    var runtime = new CombatRuntime(() => frame);
    var target = new Combatant("enemy", "enemy", new Vec2(0, 0), new CollisionBox());
    var port = new FlightRecorder(() => target);

    var arrived = 0;
    runtime.Fly(new FlightRequest
    {
        OwnerId = "hero",
        Effect = "arrow",
        Position = new Vec2(0, 0),
        TargetId = "enemy",
        Kind = "trace",
        StepDistancePx = 4,
        StepDurationMs = CombatClock.LogicFrameMs,
    }, port, _ => arrived += 1);

    run.Equal(1, port.Events.OfType<FlightStartEvent>().Count(), "先发一条起步");
    run.True(port.Events.OfType<FlightStepEvent>().Any(), "立刻走第一段");

    for (var index = 0; index < 40 && arrived == 0; index += 1)
    {
        frame += 1;
        runtime.Step(frame);
    }

    run.Equal(1, arrived, "抵达时回调一次");
    run.True(port.Events.OfType<FlightEndEvent>().Any(), "收尾事件");

    var live = true;
    var port2 = new FlightRecorder(() => live ? target : null);
    var arrived2 = 0;
    runtime.Fly(new FlightRequest
    {
        OwnerId = "hero",
        Effect = "arrow",
        Position = new Vec2(0, 0),
        TargetId = "enemy",
        Kind = "trace",
        StepDistancePx = 4,
        StepDurationMs = CombatClock.LogicFrameMs,
    }, port2, _ => arrived2 += 1);
    live = false;
    frame += 1;
    runtime.Step(frame);
    for (var index = 0; index < 40 && !port2.Events.OfType<FlightEndEvent>().Any(); index += 1)
    {
        frame += 1;
        runtime.Step(frame);
    }

    run.Equal(0, arrived2, "目标没了不算抵达");
    run.True(port2.Events.OfType<FlightEndEvent>().Any(), "目标消失也要收尾");
});

run.Case("战斗运行时：原地过渡到点落位，暂停冻结全部内容", () =>
{
    long frame = 0;
    var runtime = new CombatRuntime(() => frame);
    var target = new Combatant("enemy", "enemy", new Vec2(32, 16), new CollisionBox());
    var port = new TransitionRecorder(() => target);

    Vec2? landed = null;
    runtime.Transition("hero", new SkillTransition
    {
        Kind = "stationary",
        SwitchFrame = 2,
        StepDistancePx = 8,
        OffsetX = 0,
        OffsetY = 0,
    }, target, new Vec2(0, 0), port, spot => landed = spot);

    run.Null(landed, "切换帧没到不落位");
    frame += 1;
    runtime.Step(frame);
    run.Null(landed, "第 1 帧仍不落位");
    frame += 1;
    runtime.Step(frame);
    run.Equal("32,16", $"{landed!.Value.X},{landed.Value.Y}", "到切换帧落在目标身上");

    runtime.SetPaused("control", true);
    run.True(runtime.Paused, "有主就暂停");
    run.Equal("control", string.Join(",", runtime.PauseOwners), "暂停人名单");

    // 暂停期间调用方本来就不该推进（推进见到暂停直接返回、也不动帧号），
    // 所以这里只验证「任务挂着不动」。
    var ran = false;
    runtime.Schedule("hero", 0, () => ran = true);
    run.False(ran, "暂停期间任务不执行");
    run.Equal(1, runtime.PendingCount, "暂停期间任务留在队列里");

    runtime.SetPaused("control", false);
    frame += 1;
    runtime.Step(frame);
    run.True(ran, "解除暂停后补上");
});

string CanonicalAny(object value) => value switch
{
    SkillReactionCommand skill => CanonicalSkill(skill),
    BuffReactionCommand buff => CanonicalBuff(buff),
    JsonElement json => CanonicalJson(json),
    _ => throw new Exception($"未处理的命令对象：{value.GetType().Name}"),
};

SkillReactionEvent BuildSkillReactionEvent(JsonElement item)
{
    string Text(string name, string fallback = "") =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : fallback;
    int Number(string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : 0;
    bool Flag(string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    var kind = Text("kind");
    var owner = Text("ownerId");
    var opponent = Text("opponentId");
    var damage = Text("damageKind", "physical");
    var outcome = Text("outcome", "hit");

    return kind switch
    {
        "attack-success" => new AttackSuccessEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            HitDamage = Number("hitDamage"), OtherLifeDrainPerThousand = Number("otherLifeDrainPerThousand"),
        },
        "physical-attack-effects" => new PhysicalAttackEffectsEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            ArmorBreakChance = Number("armorBreakChance"),
        },
        "physical-revenge" => new PhysicalRevengeEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            ChanceThreshold = Number("chanceThreshold"),
        },
        "physical-instinct" => new PhysicalInstinctEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            ChanceThreshold = Number("chanceThreshold"), MainHandTwoHanded = Flag("mainHandTwoHanded"),
        },
        "physical-defense-instinct" => new PhysicalDefenseInstinctEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            ChanceThreshold = Number("chanceThreshold"),
        },
        "physical-flare" => new PhysicalFlareEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            Hand = item.TryGetProperty("hand", out _) ? Text("hand") : null,
        },
        "blood-thirst" => new BloodThirstEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            HitDamage = Number("hitDamage"), ChanceThreshold = Number("chanceThreshold"),
        },
        "attack-effects" => new AttackEffectsEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            ActionId = Text("actionId"), HasMoltenArmor = Flag("hasMoltenArmor"),
        },
        "summon-attack-success" => new SummonAttackSuccessEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
        },
        "summon-physical-success" => new SummonPhysicalSuccessEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
        },
        "physical-block" => new PhysicalBlockEvent { OwnerId = owner },
        "molten-reflection-completed" => new MoltenReflectionCompletedEvent
        {
            OwnerId = owner, OpponentId = opponent,
        },
        "physical-hit-received" => new PhysicalHitReceivedEvent { OwnerId = owner },
        "blood-revenge" => new BloodRevengeEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
        },
        "action-stealth-break" => new ActionStealthBreakEvent { OwnerId = owner },
        "direct-heal-accepted" => new DirectHealAcceptedEvent
        {
            OwnerId = owner, TargetId = Text("targetId"),
        },
        "skill-attack-success" => new SkillAttackSuccessEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
        },
        _ => throw new Exception($"基准里有未知的事件种类：{kind}"),
    };
}

BuffReactionEvent BuildBuffReactionEvent(JsonElement item)
{
    string Text(string name, string fallback = "") =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : fallback;
    int Number(string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : 0;

    var kind = Text("kind");
    var owner = Text("ownerId");
    var opponent = Text("opponentId");
    var damage = Text("damageKind", "physical");
    var outcome = Text("outcome", "hit");

    return kind switch
    {
        "physical-avoidance" => new PhysicalAvoidanceEvent
        {
            OwnerId = owner, DamageKind = damage, Outcome = outcome,
        },
        "physical-success" => new PhysicalSuccessEvent
        {
            OwnerId = owner, DamageKind = damage, Outcome = outcome,
        },
        "weapon-block-counter" => new WeaponBlockCounterEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
        },
        "weapon-block-stun" => new WeaponBlockStunEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
        },
        "physical-success-party" => new PhysicalSuccessPartyEvent
        {
            OwnerId = owner, DamageKind = damage, Outcome = outcome,
            PartyIds = item.GetProperty("partyIds").EnumerateArray().Select(entry => entry.GetString()!).ToList(),
        },
        "physical-success-threat" => new PhysicalSuccessThreatEvent
        {
            OwnerId = owner, OpponentId = opponent, DamageKind = damage, Outcome = outcome,
            HitDamage = Number("hitDamage"),
        },
        _ => throw new Exception($"基准里有未知的增益事件种类：{kind}"),
    };
}

string CanonicalSkill(SkillReactionCommand command) => command switch
{
    ApplyReactionBuffCommand buff => $"apply-buff|{buff.SourceId}|{buff.TargetId}|{buff.Buff.Key}",
    ApplyPartyBuffCommand party => $"apply-party-buff|{party.SourceId}|{party.Buff.Key}",
    PlayReactionEffectCommand play => $"play-effect|{play.TargetId}|{play.Effect}",
    RestoreReactionHealthCommand health => $"restore-health|{health.TargetId}|{health.Amount}",
    RestoreReactionManaCommand mana => $"restore-mana|{mana.TargetId}|{mana.Amount}",
    ResetReactionCooldownsCommand reset => $"reset-skill-cooldowns|{reset.TargetId}",
    _ => throw new Exception($"未处理的反应命令：{command.Kind}"),
};

string CanonicalBuff(BuffReactionCommand command) => command switch
{
    ApplyBuffReactionCommand buff => $"apply-buff|{buff.SourceId}|{buff.TargetId}|{buff.Buff.Key}",
    RestoreBuffManaCommand mana => $"restore-mana|{mana.TargetId}|{mana.Amount}",
    PlayBuffReactionEffectCommand play => $"play-effect|{play.TargetId}|{play.Effect}",
    RedistributeThreatCommand threat => $"redistribute-threat|{threat.SourceId}|{threat.TargetId}|{threat.Damage}",
    ApplyBuffStateCommand state =>
        $"apply-state|{state.AttributeOwner}|{state.TargetId}|{state.State}|{state.DurationAttribute}",
    _ => throw new Exception($"未处理的增益命令：{command.Kind}"),
};

string CanonicalJson(JsonElement item)
{
    var kind = item.GetProperty("kind").GetString()!;
    string Text(string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    int Number(string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    return kind switch
    {
        "apply-buff" => $"apply-buff|{Text("sourceId")}|{Text("targetId")}|{Text("buffKey")}",
        "apply-party-buff" => $"apply-party-buff|{Text("sourceId")}|{Text("buffKey")}",
        "play-effect" => $"play-effect|{Text("targetId")}|{Text("effect")}",
        "restore-health" => $"restore-health|{Text("targetId")}|{Number("amount")}",
        "restore-mana" => $"restore-mana|{Text("targetId")}|{Number("amount")}",
        "reset-skill-cooldowns" => $"reset-skill-cooldowns|{Text("targetId")}",
        "redistribute-threat" => $"redistribute-threat|{Text("sourceId")}|{Text("targetId")}|{Number("damage")}",
        "apply-state" =>
            $"apply-state|{Text("attributeOwner")}|{Text("targetId")}|{Text("state")}|{Text("durationAttribute")}",
        _ => throw new Exception($"基准里有未知的命令种类：{kind}"),
    };
}

run.Case("音频：目录按真实 audio.json 解析，武器与怪物反馈表齐备", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var catalog = JsonSerializer.Deserialize<AudioCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "audio.json")))!;

    run.Equal(3, catalog.Version, "目录版本");
    run.Equal("BGM09", catalog.MenuMusic, "菜单音乐");
    run.Equal(13, catalog.Music.Count, "十三首 BGM");
    run.Equal(170, catalog.Effects.Count, "一百七十条音效");
    run.Equal(54, catalog.Skills.Count, "五十四门技能音效");

    foreach (var (id, path) in catalog.Music)
        run.True(path.Length > 0, $"{id} 的 BGM 路径为空");
    foreach (var (map, entry) in catalog.Maps)
    {
        if (entry.Music is { Length: > 0 } music)
            run.True(catalog.Music.ContainsKey(music), $"{map} 引用了不存在的 BGM {music}");
        if (entry.Ambience is { Length: > 0 } ambience)
            run.True(catalog.Effects.ContainsKey(ambience), $"{map} 引用了不存在的环境音 {ambience}");
    }

    run.Equal(10, catalog.Combat.Weapons.Count, "十种武器的反馈表");
    run.True(catalog.Combat.Weapons["unarmed"].Miss is null, "徒手没有落空声");
    run.True(catalog.Combat.Weapons["longsword"].Normal.Length == 2, "长剑两种普通命中音");
    run.Equal(8, catalog.Combat.Weapons["longsword"].Normal[0].Weight, "长剑主音权重 8");
    run.Equal(2, catalog.Combat.Weapons["longsword"].Normal[1].Weight, "长剑副音权重 2");

    foreach (var key in new[] { "open", "move", "select", "questStart", "questComplete",
                 "heal", "loot", "itemMove", "chestOpen", "enchant", "craft" })
        run.True(catalog.Ui.Of(key).Length > 0, $"界面音效 {key} 为空");
    run.Equal("UI_Menu_select", catalog.Ui.Of("select"), "选中音");
    run.Equal(string.Empty, catalog.Ui.Of("不存在的键"), "未知键返回空串");
});

run.Case("音频：命中反馈按固定次序选音（落空→敌人受击→暴击→格挡→加权普通）", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var catalog = JsonSerializer.Deserialize<AudioCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "audio.json")))!;

    run.Equal("Weapon_Sword_Miss", AudioCueRules.PlayerHit(catalog, "longsword", "miss", null, () => 0));
    run.True(AudioCueRules.PlayerHit(catalog, "unarmed", "miss", null, () => 0) is null, "徒手落空没有声音");

    var struckEnemy = catalog.Enemies.First(pair => pair.Value.Struck is { Length: > 0 });
    run.Equal(struckEnemy.Value.Struck,
        AudioCueRules.PlayerHit(catalog, "longsword", "critical", struckEnemy.Key, () => 0),
        "敌人受击声优先于暴击声");

    run.Equal("Weapon_Sword_Critical",
        AudioCueRules.PlayerHit(catalog, "longsword", "critical", null, () => 0), "暴击声");
    run.Equal("Weapon_Punch",
        AudioCueRules.PlayerHit(catalog, "longsword", "shield-block", null, () => 0), "盾挡算被格挡");
    run.Equal("Weapon_Punch",
        AudioCueRules.PlayerHit(catalog, "longsword", "weapon-block", null, () => 0), "武器格挡同理");

    run.Equal("Weapon_Sword_Attack",
        AudioCueRules.PlayerHit(catalog, "longsword", "hit", null, () => 0.79), "低随机值取主音");
    run.Equal("Weapon_Sword_Attack_voice",
        AudioCueRules.PlayerHit(catalog, "longsword", "hit", null, () => 0.80), "高随机值取副音");

    run.Equal("Weapon_Punch",
        AudioCueRules.PlayerHit(catalog, "unknown-kind", "hit", null, () => 0), "未知武器退回徒手");

    run.Throws(() => AudioCueRules.PlayerHit(catalog, "longsword", "hit", null, () => 1), "音频随机值无效");
});

run.Case("音频：战斗预加载清单覆盖升级、盾挡、全部武器与敌人反馈", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var catalog = JsonSerializer.Deserialize<AudioCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "audio.json")))!;

    var names = AudioCueRules.PrepareCombatNames(catalog, ["dark-strike", null], ["extra-sound"]);
    var set = names.Where(name => !string.IsNullOrEmpty(name)).ToHashSet();

    run.True(set.Contains(AudioCueRules.LevelUpEffect), "升级音在清单里");
    run.True(set.Contains(catalog.Combat.ShieldBlock), "盾挡音在清单里");
    run.True(set.Contains(catalog.Combat.PeriodicDamage), "持续伤害音在清单里");
    run.True(set.Contains("Weapon_Sword_Attack"), "武器普通命中音在清单里");
    run.True(set.Contains("Weapon_Sword_Miss"), "武器落空音在清单里");
    run.True(set.Contains(catalog.Skills["dark-strike"]), "技能音在清单里");
    run.True(set.Contains("extra-sound"), "动作自带音在清单里");

    var enemy = catalog.Enemies.Values.First(entry => entry.Attack is { Length: > 0 });
    run.True(set.Contains(enemy.Attack!), "敌人出手音在清单里");

    run.Equal(11, AudioCueRules.PrepareUiNames(catalog).Count(name => !string.IsNullOrEmpty(name)),
        "界面音效十一条");
});

run.Case("音频：两个开关的读写口径，只有显式 false 才算关", () =>
{
    var store = new Dictionary<string, string>();
    var settings = new AudioSettings(key => store.GetValueOrDefault(key), (key, value) => store[key] = value);

    run.True(settings.Music, "默认开音乐");
    run.True(settings.Effects, "默认开音效");

    run.True(!settings.SetEnabled("music", true), "重复设置不算改动");
    run.True(!store.ContainsKey(AudioSettings.StorageKey), "没有改动就不落盘");

    run.True(settings.SetEnabled("effects", false), "关音效");
    run.True(!settings.Effects);
    run.True(store.ContainsKey(AudioSettings.StorageKey), "改动后落盘");

    var reloaded = new AudioSettings(key => store.GetValueOrDefault(key), (key, value) => store[key] = value);
    run.True(reloaded.Music, "音乐仍是开");
    run.True(!reloaded.Effects, "音效仍是关");

    store[AudioSettings.StorageKey] = "{}";
    var empty = new AudioSettings(key => store.GetValueOrDefault(key), (key, value) => store[key] = value);
    run.True(empty.Music && empty.Effects, "缺字段按开");

    store[AudioSettings.StorageKey] = "not json";
    var broken = new AudioSettings(key => store.GetValueOrDefault(key), (key, value) => store[key] = value);
    run.True(broken.Music && broken.Effects, "坏 JSON 按开");

    run.Throws(() => settings.SetEnabled("不存在的通道", true), "未知的音频通道");
});

run.Case("特效：真实 world.json 的 19 个剧情特效都能解析，且每层都画得出来", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var world = JsonSerializer.Deserialize<WorldIndex>(
        File.ReadAllText(Path.Combine(root, "content", "world.json")))!;

    run.Equal(19, world.StoryEffects.Count, "十九个剧情特效");

    var problems = EffectColors.Problems(world.StoryEffects);
    run.Equal(0, problems.Count, $"特效绘制问题：{string.Join("；", problems.Take(5))}");

    var clips = 0;
    var layers = 0;
    foreach (var (id, entry) in world.StoryEffects)
    {
        run.True(entry.Clips.Count > 0, $"{id} 没有片段");

        var clip = entry.Clip();
        run.True(clip is not null, $"{id} 取不到片段");

        run.True(entry.Placement is null or "ground" or "above-characters",
            $"{id} 的层位不认识：{entry.Placement}");

        foreach (var (name, value) in entry.Clips)
        {
            clips++;
            run.Equal(EffectColors.FormatVersion, value.Version, $"{id}/{name} 的契约版本");
            run.True(value.Frames > 0, $"{id}/{name} 没有帧");
            run.True(value.FrameWidth > 0 && value.FrameHeight > 0, $"{id}/{name} 帧尺寸无效");
            run.True(value.FrameRate > 0, $"{id}/{name} 帧率无效");

            foreach (var layer in value.Layers)
            {
                layers++;
                run.True(layer.File.Length > 0, $"{id}/{name} 的层没有素材路径");
                // 每层都要能算出绘制方式；backdrop 层必须带运算链。
                var draw = EffectColors.LayerDrawOf(layer);
                if (draw.IsBackdrop) run.True(draw.Operations.Length > 0, $"{id}/{name} 的 backdrop 层没有运算");
            }
        }
    }

    Console.WriteLine($"      覆盖 {world.StoryEffects.Count} 个特效、{clips} 个片段、{layers} 层");
});

run.Case("特效：层绘制方式的判断对得上特效配色基准", () =>
{
    EffectClip Clip(int version, params EffectLayer[] layers) => new()
    {
        Version = version, Frames = 1, FrameWidth = 1, FrameHeight = 1, FrameRate = 1, Layers = layers,
    };

    foreach (var blend in EffectColors.DirectBlends)
    {
        var layer = new EffectLayer { Key = "k", File = "f.png", Blend = blend };
        run.Null(EffectColors.LayerProblem(Clip(1, layer), layer), $"{blend} 应当可画");
        var draw = EffectColors.LayerDrawOf(layer);
        run.True(!draw.IsBackdrop, $"{blend} 不是 backdrop");
        run.Equal(blend, draw.Blend, $"{blend} 原样带回");
    }

    var backdrop = new EffectLayer
    {
        Key = "k", File = "f.png", Blend = EffectBlends.Backdrop, Operations = ["multiply", "add"],
    };
    var backdropDraw = EffectColors.LayerDrawOf(backdrop);
    run.True(backdropDraw.IsBackdrop, "backdrop 层");
    run.Equal("multiply,add", string.Join(",", backdropDraw.Operations), "运算链原样带回");
    run.Null(EffectColors.LayerProblem(Clip(1, backdrop), backdrop), "带运算的 backdrop 可画");

    var empty = new EffectLayer { Key = "k", File = "f.png", Blend = EffectBlends.Backdrop };
    run.Throws(() => EffectColors.LayerDrawOf(empty), "backdrop 层没有可作用的运算");
    run.True(EffectColors.LayerProblem(Clip(1, empty), empty) is not null, "空运算链被判为问题");

    run.True(EffectColors.LayerProblem(Clip(2, backdrop), backdrop)?.Contains("契约版本") == true, "版本不符");
    var unknownOp = new EffectLayer { Key = "k", File = "f.png", Blend = EffectBlends.Add, Operations = ["voodoo"] };
    run.True(EffectColors.LayerProblem(Clip(1, unknownOp), unknownOp)?.Contains("颜色运算") == true, "未知运算");
    var unknownBlend = new EffectLayer { Key = "k", File = "f.png", Blend = "hologram" };
    run.True(EffectColors.LayerProblem(Clip(1, unknownBlend), unknownBlend)?.Contains("绘制方式") == true, "未知绘制方式");

    // backdrop 没有背景合成路径时的近似：有 multiply 就用正片叠底，否则相加。
    run.Equal(EffectBlends.Multiply, EffectColors.BackdropApproximation(["multiply"]), "有乘法就按正片叠底");
    run.Equal(EffectBlends.Add, EffectColors.BackdropApproximation(["color-dodge"]), "其余按相加");
});

run.Case("特效：角色定义解析出特效与头顶锚点", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var world = JsonSerializer.Deserialize<WorldIndex>(
        File.ReadAllText(Path.Combine(root, "content", "world.json")))!;

    run.True(world.Actors.TryGetValue("kiyan", out var hero), "world.json 里有主角 kiyan");
    run.Equal(0f, hero!.EffectAnchor?.X, "特效锚点 x");
    run.Equal(-4f, hero.EffectAnchor?.Y, "特效锚点 y");
    run.Equal(-22f, hero.HeadAnchor?.Y, "头顶锚点 y");

    // 锚点缺省时按 (0,0)：没有这两个字段的角色不该被塞进默认碰撞盒的值。
    var bare = world.Actors.Values.First(actor => actor.EffectAnchor is null);
    run.True(bare.EffectAnchor is null, $"{bare.Id} 没有特效锚点");
});

run.Case("派发：普攻按挥击帧与命中帧派发，after-strike 的挥击排在接触之后", () =>
{
    var port = new RecordingActionPort();
    var dispatcher = new CombatActionDispatcher(port);

    var caster = new Combatant("player", "ally", new Vec2(100, 100), new CollisionBox());
    var target = new Combatant("enemy-1", "enemy", new Vec2(120, 100), new CollisionBox());
    port.Actors["player"] = caster;
    port.Actors["enemy-1"] = target;

    ResolvedBasicAttack Attack(string swingOrder, int swingFrame, int strikeFrame) =>
        BasicAttackRules.Resolve(
            new Dictionary<string, BasicAttackDefinition>
            {
                ["test"] = new()
                {
                    CycleFrames = 10,
                    StrikeFrame = strikeFrame,
                    SwingFrame = swingFrame,
                    SwingOrder = swingOrder,
                    Swings = ["slash-effect"],
                    FrameDurationMs = 60,
                },
            },
            new ActorDefinition
            {
                Id = "hero",
                Clips = new Dictionary<string, ActorClip>
                {
                    ["attack-south"] = new() { Frames = 10, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                    ["attack-north"] = new() { Frames = 10, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                    ["attack-east"] = new() { Frames = 10, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                    ["attack-west"] = new() { Frames = 10, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                },
            },
            new BasicAttackBinding
            {
                ProfileId = "test",
                Clips = new Dictionary<string, string>
                {
                    ["south"] = "attack-south",
                    ["north"] = "attack-north",
                    ["east"] = "attack-east",
                    ["west"] = "attack-west",
                },
            });

    CombatAction Action(ResolvedBasicAttack attack) => new()
    {
        Caster = caster,
        Target = target,
        Facing = "south",
        BasicAttack = attack,
    };

    var plain = Attack("", 2, 5);
    dispatcher.Frame(Action(plain), 2, (id, hand) => port.Contact(id, hand));
    run.True(port.Effects.Contains("slash-effect"), "挥击帧放挥击特效");
    run.Equal(0, port.Contacts.Count, "挥击帧不结算接触");

    port.Reset();
    dispatcher.Frame(Action(plain), 5, (id, hand) => port.Contact(id, hand));
    run.Equal("contact:enemy-1:main-hand", port.Contacts.FirstOrDefault(), "命中帧结算接触");

    port.Reset();
    var late = Attack("after-strike", 5, 5);
    dispatcher.Frame(Action(late), 5, (id, hand) => port.Log.Add($"contact:{id}:{hand}"));
    var contactIndex = port.Log.ToList().IndexOf("contact:enemy-1:main-hand");
    var swingIndex = port.Log.ToList().IndexOf("effect:slash-effect");
    run.True(contactIndex >= 0, "命中帧结算了接触");
    run.True(swingIndex > contactIndex, $"挥击排在接触之后（实际 contact={contactIndex}、swing={swingIndex}）");

    port.Reset();
    dispatcher.Frame(Action(plain), 0, (_, _) => port.Log.Add("contact"));
    run.Equal(0, port.Contacts.Count, "普通帧不结算");
    run.Equal(0, port.Effects.Count, "普通帧不放特效");
    run.Equal(1, port.Poses.Count, "每帧都推动作");
});

run.Case("派发：投射物普攻走飞行，抵达后才结算接触", () =>
{
    var port = new RecordingActionPort();
    var dispatcher = new CombatActionDispatcher(port);
    var caster = new Combatant("player", "ally", new Vec2(100, 100), new CollisionBox());
    var target = new Combatant("enemy-1", "enemy", new Vec2(200, 100), new CollisionBox());
    port.Actors["player"] = caster;
    port.Actors["enemy-1"] = target;

    var attack = BasicAttackRules.Resolve(
        new Dictionary<string, BasicAttackDefinition>
        {
            ["bow"] = new()
            {
                CycleFrames = 8, StrikeFrame = 3, SwingFrame = 2, Swings = ["shoot"],
                Projectile = new BasicAttackProjectile { Effect = "arrow", StepDistancePx = 16, StepDurationMs = 60 },
                FrameDurationMs = 60,
            },
        },
        new ActorDefinition
        {
            Id = "archer",
            Clips = new Dictionary<string, ActorClip>
            {
                ["attack-south"] = new() { Frames = 8, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                ["attack-north"] = new() { Frames = 8, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                ["attack-east"] = new() { Frames = 8, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
                ["attack-west"] = new() { Frames = 8, FrameWidth = 1, FrameHeight = 1, FrameRate = 1 },
            },
        },
        new BasicAttackBinding
        {
            ProfileId = "bow",
            Clips = new Dictionary<string, string>
            {
                ["south"] = "attack-south",
                ["north"] = "attack-north",
                ["east"] = "attack-east",
                ["west"] = "attack-west",
            },
        });

    var action = new CombatAction { Caster = caster, Target = target, Facing = "south", BasicAttack = attack };
    dispatcher.Frame(action, 3, (id, hand) => port.Contact(id, hand));

    run.Equal(0, port.Contacts.Count, "抵达前不结算接触");

    // 把运行时推到飞行结束：飞行事件由 runtime 在推步时发出，抵达回调才结算。
    // runtime 要求逐帧推进（每次 +1）；每段飞行排一个「下一步」的活，所以要多给几帧。
    for (var frame = 1; frame <= 60; frame++)
    {
        port.Frame = frame;
        port.Runtime.Step(frame);
    }

    run.True(port.Flights.Count > 0, "飞行过程中发出了进度事件");
    run.Equal("contact:enemy-1:main-hand", port.Contacts.FirstOrDefault(), "飞行抵达后结算接触");
});

run.Case("命中反馈：可见性、数字分组与冲击判定", () =>
{
    var hit = new CombatHitFeedback(new HitResult(HitKind.Hit, 42), "weapon",
        Periodic: false, CasterControlled: true, TargetFriendly: false, TargetParty: false,
        InvolvesControlled: true);
    var plan = CombatFeedback.Plan(hit);
    run.True(plan.Digits, "真掉血要出数字");
    run.Equal("enemy", plan.DigitStyle);
    run.Equal("normal", plan.Impact);
    run.True(plan.Flash, "掉血要闪");
    run.False(plan.Shake, "非暴击不震屏");

    var miss = hit with { Hit = new HitResult(HitKind.Miss, 0) };
    var missPlan = CombatFeedback.Plan(miss);
    run.True(missPlan.Digits);
    run.Equal("miss", missPlan.DigitStyle);
    run.Equal("miss", missPlan.Impact);
    run.False(missPlan.Flash, "落空不闪");

    var block = hit with { Hit = new HitResult(HitKind.ShieldBlock, 0) };
    var blockPlan = CombatFeedback.Plan(block);
    run.Equal(null, blockPlan.Impact, "格挡不放冲击");
    run.False(blockPlan.Digits, "零伤害不出数字");

    var critical = hit with { Hit = new HitResult(HitKind.Critical, 90) };
    var criticalPlan = CombatFeedback.Plan(critical);
    run.Equal("critical", criticalPlan.Impact);
    run.True(criticalPlan.Shake, "主角出手的暴击要震屏");
    run.Equal("enemyCritical", criticalPlan.DigitStyle, "暴击数字走敌方暴击组");

    var npcCritical = critical with { CasterControlled = false, InvolvesControlled = false, TargetParty = false };
    run.Equal(null, CombatFeedback.Plan(npcCritical).Impact, "看不到的那一下不放暴击冲击");

    var partyHit = new CombatHitFeedback(new HitResult(HitKind.Critical, 30), "weapon",
        Periodic: false, CasterControlled: false, TargetFriendly: true, TargetParty: true,
        InvolvesControlled: false);
    var partyPlan = CombatFeedback.Plan(partyHit);
    run.Equal("partyCritical", partyPlan.DigitStyle);
    run.True(partyPlan.Digits, "我方挨打要出数字");

    var periodic = critical with { Periodic = true };
    var periodicPlan = CombatFeedback.Plan(periodic);
    run.True(periodicPlan.Digits);
    run.Equal(null, periodicPlan.Impact, "持续伤害不放冲击");
    run.False(periodicPlan.Shake, "持续伤害不震屏");
});

run.Case("命中反馈：素材选择按固定次序，关掉装饰也先抽一次爆闪", () =>
{
    var definition = new CombatFeedbackDefinition
    {
        Digits = new CombatDamageDigits
        {
            Enemy = ["digit-enemy"],
            EnemyCritical = ["digit-enemy-critical"],
            Miss = ["digit-miss"],
        },
        NormalImpacts = ["impact-a", "impact-b"],
        SkillImpact = "impact-skill",
        CriticalImpact = "impact-critical",
        CriticalRing = "ring",
        CriticalBursts = ["burst"],
        MissEffect = "miss-ripple",
    };

    var critical = new CombatHitFeedback(new HitResult(HitKind.Critical, 10), "weapon",
        Periodic: false, CasterControlled: true, TargetFriendly: false, TargetParty: false,
        InvolvesControlled: true);

    var decorated = CombatFeedback.Select(critical, definition, () => 0.0);
    run.Equal(3, decorated.Impacts.Count, "暴击三处素材");
    run.Equal("impact-critical", decorated.Impacts[0].Name);
    run.True(decorated.Impacts[0].Attached, "冲击跟着受击者");
    run.Equal("ring", decorated.Impacts[1].Name);
    run.Equal("burst", decorated.Impacts[2].Name);
    run.False(decorated.Impacts[2].Attached, "爆闪不跟随");
    run.Equal("digit-enemy-critical", decorated.DigitEffect, "暴击数字走敌方暴击组");

    // 关掉装饰：只剩冲击，但随机数仍被抽一次（否则同种子后续序列会变）。
    var calls = 0;
    Func<double> Counting() => () =>
    {
        calls += 1;
        return 0.0;
    };

    calls = 0;
    var plainDecorated = CombatFeedback.Select(critical, definition, Counting());
    var withDecorations = calls;
    calls = 0;
    var plainBare = CombatFeedback.Select(critical, definition, Counting(), criticalDecorations: false);
    run.Equal(1, plainBare.Impacts.Count, "关掉装饰只剩冲击");
    run.Equal(withDecorations, calls, "关掉装饰时随机消耗次数不变");

    var normal = critical with { Hit = new HitResult(HitKind.Hit, 10) };
    var picked = CombatFeedback.Select(normal, definition, () => 0.99);
    run.Equal("impact-b", picked.Impacts[0].Name, "0.99 抽到池子最后一个");
    run.Equal("digit-enemy", picked.DigitEffect);

    var skill = normal with { Style = "skill" };
    run.Equal("impact-skill", CombatFeedback.Select(skill, definition, () => 0.0).Impacts[0].Name);

    var miss = normal with { Hit = new HitResult(HitKind.Miss, 0) };
    var missed = CombatFeedback.Select(miss, definition, () => 0.0);
    run.Equal("miss-ripple", missed.Impacts[0].Name);
    run.Equal("digit-miss", missed.DigitEffect);

    run.Equal(10, definition.AllEffects().Count, "素材清单条数");
});

run.Case("命中反馈：数字逐位摆放、第 4 帧按停留帧数重复、池满挑进度最大的", () =>
{
    var digits = CombatFeedback.DamageDigits(1234);
    run.Equal(4, digits.Count);
    run.Equal(1, digits[0].Digit);
    run.Equal(-16f, digits[0].X, "四位数字从 -16 起铺开");
    run.Equal(8f, digits[3].X);
    run.Equal(0f, digits[0].Y);
    run.Equal(2f, digits[1].Y, "偶数位错开两像素");
    run.Equal(180, digits[3].DelayMs, "每位延后 60 毫秒");
    run.Equal(0, digits[3].HoldFrames, "最后一位停得最短");

    var miss = CombatFeedback.DamageDigits(0, miss: true);
    run.Equal(1, miss.Count);
    run.Equal(10, miss[0].Digit, "落空画素材 10");
    run.Equal(0f, miss[0].X);
    run.Equal(0f, miss[0].Y);

    var sequence = CombatFeedback.DigitSequence(6, 3);
    run.Equal(9, sequence.Count, "6 帧 + 第 4 帧多重复 3 次");
    run.Equal(4, sequence[4]);
    run.Equal(4, sequence[7], "第 4 帧重复到第 7 项");
    run.Equal(5, sequence[8]);

    run.Equal(-1, CombatFeedback.ReusableDigit([0, 0, 0]), "都没推进时不能复用");
    run.Equal(1, CombatFeedback.ReusableDigit([1, 3, 2]), "挑进度最大的");
    run.Equal(2, CombatFeedback.ReusableDigit([0, 1, 2]));
});

run.Case("世界物件：objective 从剧本 place 命令进世界状态，并参与商人判定", () =>
{
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    var world = new StoryWorldState();
    var sink = new WorldStateSink(world);
    var skipped = new List<string>();

    WorldCommands.Apply(Parse("""
        {"type":"world","command":{"kind":"place","scope":"visit","entity":{
          "id":"story-object-1","map":"shadow-camp","actor":"story-boulder","x":100,"y":200,
          "objective":"quest-target-1","hint":"一块巨石"}}}
    """), sink, skipped);
    run.Equal(0, skipped.Count, "place 命令被接受");

    var entity = world.Entity("story-object-1") ?? throw new Exception("物件没进世界状态");
    run.Equal("quest-target-1", entity.Objective, "objective 进了世界状态");
    run.Equal("一块巨石", entity.Hint, "hint 也进了世界状态");

    WorldCommands.Apply(Parse("""
        {"type":"world","command":{"kind":"place","entity":{
          "id":"story-object-2","map":"shadow-camp","actor":"merchant","x":0,"y":0}}}
    """), sink, skipped);
    run.Null(world.Entity("story-object-2")?.Objective, "没写 objective 就是 null");
});

run.Case("稳定种子：同一身份串永远得出同一个值", () =>
{
    run.Equal(2543096389u, StableSeed.Of("summon-1"));
    run.Equal(3309214433u, StableSeed.Of("player"));
    run.Equal(524579133u, StableSeed.Of("enemy-beast-companion-1"));
});

run.Case("战术观察：命中入册、主人目标与威胁、1800 毫秒过期", () =>
{
    var now = 0L;
    var awareness = new SummonCombatAwareness(now);
    run.False(awareness.InCombat(now), "没有交战记录不算战斗中");
    run.Equal(0, awareness.Tactics("player", ["player"]).EngagedTargetIds.Count);

    var player = new Combatant("player", "party", new Vec2(0, 0), new CollisionBox());
    var slime = new Combatant("slime", "hostile", new Vec2(40, 0), new CollisionBox());
    awareness.Contact(player, slime, now);
    run.True(awareness.InCombat(now));
    var tactics = awareness.Tactics("player", ["player"]);
    run.Equal(1, tactics.EngagedTargetIds.Count);
    run.Equal("slime", tactics.EngagedTargetIds[0]);

    var wolf = new Combatant("wolf", "hostile", new Vec2(-40, 0), new CollisionBox());
    awareness.Contact(wolf, player, now + 100);
    tactics = awareness.Tactics("player", ["player"]);
    run.Equal("slime", tactics.OwnerTargetId, "敌人打主人记成主人的目标");
    run.Equal(1, tactics.Threats.Count);
    run.Equal(("wolf", "player"), (tactics.Threats[0].EnemyId, tactics.Threats[0].TargetId));

    awareness.Observe(now + 2000, []);
    run.Equal(0, awareness.Tactics("player", ["player"]).EngagedTargetIds.Count, "过期记录被清掉");
    run.False(awareness.InCombat(now + 2000));
});

run.Case("敌对动作判定：无目标与指向敌人的提示都算，只作用自身的提示不算", () =>
{
    run.True(SummonTacticsRules.IsHostileAction(new ActiveCombatAction("attacking", "t1", null)), "普攻就是敌对");

    var hostile = new SkillAction
    {
        SearchRadiusPx = 64,
        ApproachDistanceUnits = 4,
        CooldownFrames = 30,
        Events = [new UnitsEvent { Frame = 1, Cues = [new SkillEffectCue { Target = "current", TargetRangePx = 32 }] }],
    };
    var friendly = new SkillAction
    {
        SearchRadiusPx = 64,
        ApproachDistanceUnits = 4,
        CooldownFrames = 30,
        Events = [new UnitsEvent { Frame = 1, Cues = [new SkillEffectCue { Target = "self", TargetRangePx = 32 }] }],
    };
    run.True(SummonTacticsRules.IsHostileAction(
        new ActiveCombatAction("attacking", "t1", new SkillPlayback("a", hostile, 1))));
    run.False(SummonTacticsRules.IsHostileAction(
        new ActiveCombatAction("attacking", "t1", new SkillPlayback("b", friendly, 1))), "自我增益不算开战");
});

run.Case("召唤物跟随：离主人远就走寻路，贴身就停下", () =>
{
    var world = new SummonTestWorld();
    var summon = world.AddSummon("summon-1", new Vec2(320, 320));
    world.OwnerPosition = new Vec2(384, 320);
    for (var frame = 0; frame < 40; frame++) summon.Step();
    run.Equal("following", summon.ActionState(), "间隔超过起跟距离就该跟着走");
    run.True(world.Followed > 0, "路径要交到物理系统手上");

    world.OwnerPosition = new Vec2(328, 320);
    for (var frame = 0; frame < 40; frame++) summon.Step();
    run.Equal("idle", summon.ActionState(), "贴身之后回到待机");
});

run.Case("召唤物追击：接战的敌人在射程内就出手", () =>
{
    var world = new SummonTestWorld();
    world.OwnerPosition = new Vec2(320, 320);
    world.Tactics = new SummonTactics(["slime-1"], null, null, [], ["player"]);
    var summon = world.AddSummon("summon-1", new Vec2(320, 320));
    world.AddEnemy("slime-1", new Vec2(340, 320));
    for (var frame = 0; frame < 40; frame++) summon.Step();
    run.Equal("attacking", summon.ActionState(), "射程内直接起手");
    run.Equal("slime-1", summon.CurrentTargetId());
});

run.Case("召唤物脱离锁链：主人走远就回家，回到身边就不再保持回家", () =>
{
    var world = new SummonTestWorld();
    var summon = world.AddSummon("summon-1", new Vec2(320, 320));
    world.OwnerPosition = new Vec2(530, 320);
    summon.Step();
    run.Equal("returning", summon.ActionState(), "超出 192 像素的锁链就回家");

    world.OwnerPosition = new Vec2(330, 320);
    for (var frame = 0; frame < 40; frame++) summon.Step();
    run.False(summon.ActionState() == "returning", "回到主人身边之后不再保持回家");
});

run.Case("召唤物被控打断：睡着就站住待机", () =>
{
    var world = new SummonTestWorld();
    world.SummonStates = ["sleep"];
    var summon = world.AddSummon("summon-1", new Vec2(320, 320));
    world.OwnerPosition = new Vec2(384, 320);
    summon.Step();
    run.Equal("idle", summon.ActionState(), "不能行动就停在待机");
    run.True(world.Stops > 0, "禁用时要喊停移动");
});

run.Case("召唤物受命：接受后进入攻击，动作没做完之前拒收第二道", () =>
{
    var world = new SummonTestWorld();
    world.OwnerPosition = new Vec2(320, 320);
    var summon = world.AddSummon("summon-1", new Vec2(320, 320));
    world.AddEnemy("slime-1", new Vec2(340, 320));
    var skill = new EnemyPlayableSkill
    {
        Id = "hell-blast",
        Action = new SkillAction
        {
            CooldownFrames = 30,
            ApproachDistanceUnits = 4,
            SearchRadiusPx = 200,
            Events =
            [
                new UnitsEvent { Frame = 2, Cues = [new SkillEffectCue { Target = "current", TargetRangePx = 32 }] },
            ],
        },
        Clips = new Dictionary<string, string>(),
    };
    run.True(summon.Command(skill, null, 1), "空闲时命令被接受");
    run.Equal("attacking", summon.ActionState());
    run.False(summon.Command(skill, null, 1), "动作没做完之前不接受第二道命令");
});

run.Case("召唤物存档快照：静止恢复保留主人与冷却，短期记忆清空", () =>
{
    var world = new SummonTestWorld();
    world.OwnerPosition = new Vec2(320, 320);
    world.Tactics = new SummonTactics(["slime-1"], null, null, [], ["player"]);
    var summon = world.AddSummon("summon-1", new Vec2(320, 320));
    world.AddEnemy("slime-1", new Vec2(340, 320));
    for (var frame = 0; frame < 20; frame++) summon.Step();

    var captured = summon.CaptureState();
    run.Equal("player", captured.OwnerId, "主人是玩家");
    run.Equal("slime-1", captured.TargetId, "正在打的敌人记进快照");
    run.True(captured.FailedTargets.Count == 0, "快照只是普通字段复制，不携带寻路任务");

    var stationary = FriendlySummonAi.StationaryState(captured);
    run.Equal("idle", stationary.State, "存档恢复以静止状态起手");
    run.Equal(0, stationary.Clock);
    run.Equal(0, stationary.Path.Count);
    run.Null(stationary.TargetId);

    var restored = new SummonTestWorld().AddSummon("summon-1", new Vec2(320, 320));
    restored.RestoreState(stationary);
    run.Equal("idle", restored.ActionState());
    run.Null(restored.CurrentTargetId());
});

run.Case("反击档位：延迟档要挨满三下才转入战斗", () =>
{
    run.False(PartyAi.CounterattackReady(CounterattackMode.Off, 5), "未启用永不反击");
    run.False(PartyAi.CounterattackReady(CounterattackMode.Delayed, 2), "延迟档两下还不够");
    run.True(PartyAi.CounterattackReady(CounterattackMode.Delayed, 3), "满三下开始反击");
    run.True(PartyAi.CounterattackReady(CounterattackMode.On, 0), "启用档挨一下就反击");
});

run.Case("自动施法阈值表：种类 × 频率 × 是否跟随", () =>
{
    run.Equal(100, PartyAi.SkillThreshold(PartySkillKind.Chance, 2, false));
    run.Equal(30, PartyAi.SkillThreshold(PartySkillKind.Chance, 1, false));
    run.Equal(100, PartyAi.SkillThreshold(PartySkillKind.CombatChance, 2, true));
    run.Equal(50, PartyAi.SkillThreshold(PartySkillKind.CombatChance, 1, true));
    run.Equal(50, PartyAi.SkillThreshold(PartySkillKind.CombatChance, 2, false));
    run.Equal(70, PartyAi.SkillThreshold(PartySkillKind.Heal, 2, false));
    run.Equal(40, PartyAi.SkillThreshold(PartySkillKind.EmergencyHeal, 2, false));
    run.Equal(80, PartyAi.SkillThreshold(PartySkillKind.AllyCombat, 2, false));
    run.Equal(60, PartyAi.SkillThreshold(PartySkillKind.AllyCombat, 1, false));
});

run.Case("伙伴跟随：离队长远就走寻路，掉队太久让宿主收拢", () =>
{
    var world = new PartyTestWorld();
    var controller = world.AddMember("ally-1", new Vec2(420, 320));
    for (var frame = 0; frame < 10; frame++) controller.Step();
    run.True(world.Followed > 0, "路径交到物理系统手上");

    for (var frame = 0; frame < 30 && world.Regroups == 0; frame++) controller.Step();
    run.True(world.Regroups > 0, "长时间没有位移要让宿主收拢");
});

run.Case("伙伴反击：挨打记住攻击者并朝他寻路，关闭档不记", () =>
{
    var world = new PartyTestWorld();
    world.Counterattack = CounterattackMode.On;
    world.AddEnemy("slime", new Vec2(520, 320));
    var controller = world.AddMember("ally-1", new Vec2(320, 320));
    controller.OnContact(new AttackContact("slime", "ally-1"));
    controller.Step();
    run.True(world.Followed > 0, "射程外先朝打我的人走过去（反击意图在驱动）");

    var closed = new PartyTestWorld();
    closed.Counterattack = CounterattackMode.Off;
    closed.AddEnemy("slime", new Vec2(520, 320));
    var member = closed.AddMember("ally-1", new Vec2(320, 320));
    member.OnContact(new AttackContact("slime", "ally-1"));
    member.Step();
    run.Equal(0, closed.Followed, "未启用反击不记仇，贴着队长原地待机");
});

run.Case("伙伴自动施法：战斗中按种类阈值抽签出手", () =>
{
    var world = new PartyTestWorld();
    world.InCombatValue = true;
    world.SkillList.Add(new EnemyPlayableSkill
    {
        Id = "party-smite",
        Frequency = 2,
        Action = new SkillAction
        {
            SearchRadiusPx = 200,
            ApproachDistanceUnits = 4,
            CooldownFrames = 30,
        },
    });
    world.SkillKinds["party-smite"] = PartySkillKind.Chance;
    var controller = world.AddMember("ally-1", new Vec2(320, 320));
    controller.Step();
    run.True(controller.ActionState() != CombatState.Idle, "频率 2 的 chance 档必过，直接起手");
});

run.Case("治疗师复活：上阵阵亡统一报价，收钱扶起来并交回主角", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    var story = party.Catalog.Definitions.Values.First(definition => definition.SourceOrDefault == "story");
    var member = party.JoinStory(story.Id);

    var session = new Session { Bag = hero.Bag, Hero = hero, Journal = new QuestJournal(), Party = party };

    hero.SetHealth(0);
    member.SetHealth(0);
    var quote = PartyRevival.Quote(session);
    run.Equal(10 + 2 * (hero.Player.Level + member.Player.Level), quote.Cost, "基础费只收一次");
    run.Equal(2, quote.MemberIds.Count, "上阵的两个人都要算进去");

    run.Throws(() => PartyRevival.ReviveAtHealer(session), "全队阵亡");

    // 留一个活人：报价只剩主角那份；钱不够照样拒绝，而且不能把人叫起来。
    member.SetHealth(member.Player.MaxHealth);
    var remaining = PartyRevival.Quote(session);
    run.Equal(1, remaining.MemberIds.Count, "活着的人不进阵亡名单");
    session.GainMoney(remaining.Cost - 1);
    run.Throws(() => PartyRevival.ReviveAtHealer(session), "金币不足");
    run.Equal(0, hero.Player.Health, "报价失败不该动到人");

    session.GainMoney(1);
    var done = PartyRevival.ReviveAtHealer(session);
    run.Equal(0, session.Money, "只收这一次报价");
    run.Equal("player", done.MemberIds[0], "阵亡名单按上阵次序");
    run.Equal(hero.Player.MaxHealth, hero.Player.Health, "复活即回满");
    run.Equal("player", party.ControlledId, "操控权交回主角");
});

run.Case("检查点：快照与现场隔离，回退拿到记录那一刻的进度", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, _) = ReadParty(root, "dark-knight");
    var session = new Session { Bag = hero.Bag, Hero = hero, Journal = new QuestJournal() };
    session.GainMoney(100);

    var checkpoint = new SessionCheckpoint<WorldSnapshotState>();
    checkpoint.Capture(session, new CheckpointLocation("shadow-camp", 120, 312),
        new WorldSnapshotState
        {
            Location = new WorldLocationSaved { Map = "shadow-camp", X = 120, Y = 312, Facing = "west" },
        });

    session.SpendMoney(40);
    run.Equal(60, session.Money);

    // peek 给的是深拷贝：外面改它不该污染检查点。
    var peeked = checkpoint.Peek()!;
    peeked.Session.Money = 1;

    checkpoint.Capture(session, new CheckpointLocation("shadow-camp", 200, 300), new WorldSnapshotState());
    var restored = checkpoint.Restore(session)!.Value;
    run.Equal(60, session.Money, "回退到重新记录的那一刻");
    run.Equal(200f, (float)restored.Location.X, "落点跟着最近一次记录");

    checkpoint.Clear();
    run.False(checkpoint.Available);
    run.True(checkpoint.Peek() is null);
});

run.Case("检查点：恢复不改掉记录本身，能反复回退到同一刻", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, _) = ReadParty(root, "dark-knight");
    var session = new Session { Bag = hero.Bag, Hero = hero, Journal = new QuestJournal() };
    session.GainMoney(80);

    // Capture 现在直接收下调用方给的状态（不再整份深拷贝，见 SessionCheckpoint.Capture 的注释），
    // 隔离留在 Restore 那一头：恢复出去的对象被玩法改了，检查点本身不能被改。
    var checkpoint = new SessionCheckpoint<WorldSnapshotState>();
    checkpoint.Capture(session, new CheckpointLocation("shadow-camp", 120, 312),
        new WorldSnapshotState
        {
            Location = new WorldLocationSaved { Map = "shadow-camp", X = 120, Y = 312, Facing = "west" },
        });

    var first = checkpoint.Restore(session)!.Value;
    run.Equal(80, session.Money, "回退到记录那一刻");

    session.SpendMoney(70);
    first.World.Location.X = 999f;
    var second = checkpoint.Restore(session)!.Value;
    run.Equal(80, session.Money, "第二次回退仍是记录那一刻的钱");
    run.Equal(120f, (float)second.Location.X, "落点没有被上一次恢复改掉");
    run.Equal(120f, second.World.Location.X, "世界状态也没有被上一次恢复改掉");
});

run.Case("会话事务：中途抛错整份回滚", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, _) = ReadParty(root, "dark-knight");
    var session = new Session { Bag = hero.Bag, Hero = hero, Journal = new QuestJournal() };
    session.GainMoney(50);

    run.Throws(() => session.Transaction(() =>
    {
        session.SpendMoney(30);
        throw new InvalidOperationException("boom");
    }), "boom");
    run.Equal(50, session.Money, "钱要退回去");

    var value = session.Transaction(() =>
    {
        session.SpendMoney(30);
        return 7;
    });
    run.Equal(7, value, "成功时原样返回");
    run.Equal(20, session.Money);
});

run.Case("伙伴落点：不叠在主角身上，整盒站在能走到的地方", () =>
{
    var grid = Grid([
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
    ]);

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    var player = new FakeNavActor("player", box, new Vec2(128, 64), party: false);
    var nav = new NavigationWorld("test", grid, () => [player]);

    var landing = PartyPlacement.ChoosePartyLanding(nav, NavigationShape.Of(box), player.Position,
        new NavigationIgnore("ally", Party: true));

    run.True(landing is not null, "空地上必须找得到落点");
    var spot = landing!.Value;
    run.True(MathF.Abs(spot.X - player.Position.X) > 0.01f || MathF.Abs(spot.Y - player.Position.Y) > 0.01f,
        "落点不能和主角重合（主角会挡住伙伴）");
    run.True(nav.TerrainFree(spot), "落点脚点要能站");
    run.True(nav.CanPlaceBody(NavigationShape.Of(box), spot, new NavigationIgnore("ally", Party: true)),
        "落点整盒要放得下");
});

run.Case("伙伴落点：被墙围住的空地不算落点，换到走得到的那一侧", () =>
{
    var grid = Grid([
        ".........",
        ".#######.",
        ".#.....#.",
        ".#.....#.",
        ".#######.",
        ".........",
    ]);

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    var player = new FakeNavActor("player", box, new Vec2(40, 8), party: false);
    var nav = new NavigationWorld("test", grid, () => [player]);

    run.False(nav.TerrainRoute(player.Position, new Vec2(64, 48)), "围栏里的空地走不进去");
    run.True(nav.TerrainRoute(player.Position, new Vec2(40, 88)), "围栏下方是连通的");

    var landing = PartyPlacement.ChoosePartyLanding(nav, NavigationShape.Of(box), player.Position,
        new NavigationIgnore("ally", Party: true));
    run.True(landing is not null, "地图上必须还有落点");
    var spot = landing!.Value;
    var insidePocket = spot.X > 32f && spot.X < 96f && spot.Y > 32f && spot.Y < 64f;
    run.False(insidePocket, "落点不能选在走不进去的围栏里");
    run.True(nav.TerrainFree(spot), "落点脚点要能站");
});

run.Case("整组落位：剧情把人摆在主角身上时，主角挤到旁边（evictionsFor）", () =>
{
    var grid = Grid([
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
    ]);

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    var player = new FakeNavActor("player", box, new Vec2(128, 64), party: false);
    var npc = new FakeNavActor("npc", box, new Vec2(32, 64), party: false);
    var nav = new NavigationWorld("test", grid, () => [player, npc]);

    var placements = new List<NavigationPlacement>
    {
        new(npc, new Vec2(128, 64)),
        new(player, new Vec2(128, 64)),
    };
    var evicted = nav.EvictionsFor(placements, new HashSet<object> { "player" });

    run.Equal(1, evicted.Count, "只有主角要让位");
    run.Equal("player", evicted[0].Actor.Key, "让位的是主角");
    run.True(evicted[0].Position.X != 128f || evicted[0].Position.Y != 64f, "主角不能留在原地");

    // 让位后的点必须站得住，且整组落位成立。
    var settled = placements
        .Select(p => p.Actor.Key is "player" ? new NavigationPlacement(p.Actor, evicted[0].Position) : p)
        .ToList();
    run.True(nav.CanPlace(NavigationShape.Of(box), evicted[0].Position, player), "主角的新落点要站得住");
    run.True(nav.CanSettleAll(settled), "挤开之后整组落位成立");
});

run.Case("整组落位：不在落位表里的现有角色一律让开，权威落位不动", () =>
{
    var grid = Grid([
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
    ]);

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    var placed = new FakeNavActor("placed", box, new Vec2(32, 64), party: false);
    var bystander = new FakeNavActor("bystander", box, new Vec2(128, 64), party: false);
    var nav = new NavigationWorld("test", grid, () => [placed, bystander]);

    var placements = new List<NavigationPlacement> { new(placed, new Vec2(128, 64)) };
    var evicted = nav.EvictionsFor(placements);

    run.Equal(1, evicted.Count, "被压住的旁观者要让开");
    run.Equal("bystander", evicted[0].Actor.Key, "让位的是没被登记的那个人");
    run.True(nav.CanPlace(NavigationShape.Of(box), evicted[0].Position, bystander), "让位后的点要站得住");

    bystander.Position = evicted[0].Position;
    run.True(nav.CanPlace(NavigationShape.Of(box), new Vec2(128, 64), placed), "权威落位保持在原地");
});

run.Case("整组落位：位置没变的内容实体豁免，队伍单位不豁免（canSettleAll）", () =>
{
    var grid = Grid([
        "................",
        "................",
        "................",
        "................",
    ]);

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    var crate = new FakeNavActor("crate", box, new Vec2(-20, 64), party: false);
    var ally = new FakeNavActor("ally", box, new Vec2(-20, 64), party: true);
    var nav = new NavigationWorld("test", grid, () => [crate, ally]);

    run.True(nav.CanSettleAll([new NavigationPlacement(crate, new Vec2(-20, 64))]),
        "内容实体位置没变：就算压在地图外也放行");
    run.False(nav.CanSettleAll([new NavigationPlacement(ally, new Vec2(-20, 64))]),
        "队伍单位位置没变也要整盒成立");
    run.False(nav.CanPlaceAll([new NavigationPlacement(crate, new Vec2(-20, 64))]),
        "canPlaceAll 不豁免：整组重放会判它非法");
});

run.Case("增益着色：颜色矩阵按帧取色，叠加层保持 0→0", () =>
{
    var colors = new[]
    {
        new BuffColor { Red = 100, Green = 0, Blue = 0 },
        new BuffColor { Red = 0, Green = 50, Blue = 0 },
    };

    var first = StatusColorRules.Matrix(colors, 0);
    run.Equal(1f - 100f / 255f, first[0], "红通道按颜色压暗");
    run.Equal(100f, first[4], "红的偏移就是颜色值");
    run.Equal(1f, first[6], "绿通道这一帧没被动过");
    run.Equal(0f, first[9], "绿的偏移这一帧是 0");

    var second = StatusColorRules.Matrix(colors, 1);
    run.Equal(1f, second[0], "第二帧红通道不压暗");
    run.Equal(50f, second[9], "第二帧换成绿的偏移");
    run.Equal(1f - 50f / 255f, second[6], "第二帧绿通道压暗");

    // 叠加／滤色的层以黑色为单位元：矩阵必须保持 0 → 0。
    var additive = StatusColorRules.Matrix(colors, 0, hitFlash: false, blackBackdrop: true);
    run.Equal(1f + 100f / 255f, additive[0], "叠加层按增益量提亮");
    run.Equal(0f, additive[4], "叠加层不能有偏移（否则黑背景被抬起来）");
    run.Equal(0f, additive[3], "叠加层的 alpha 系数是 0");
    run.Equal(1f, additive[18], "alpha 本身透传");

    // 受击闪白：GRP_SetGamma(50) 把系数减半、偏移加上 128 倍原系数。
    var flash = StatusColorRules.Matrix(colors, 0, hitFlash: true);
    run.Equal((1f - 100f / 255f) * 0.5f, flash[0], "闪白把系数减半");
    run.Equal(100f + 128f * (1f - 100f / 255f), flash[4], "闪白把偏移抬到一半灰");

    var identity = StatusColorRules.Matrix([], 0);
    run.Equal(1f, identity[0], "空颜色是恒等矩阵");
    run.Equal(0f, identity[4], "恒等矩阵没有偏移");
});

run.Case("剧情姿态名单：前缀匹配，行走与待机不算姿态", () =>
{
    run.Equal(14, StoryPoses.Names.Length, "名单是 14 个姿态");
    run.True(StoryPoses.IsPose("cry-west"), "姿态的定向片段算姿态");
    run.True(StoryPoses.IsPose("fall-down-south"), "带连字符的姿态名也要认");
    run.True(StoryPoses.IsPose("skill-2-north"), "skill-2 也在名单里");
    run.False(StoryPoses.IsPose("idle-south"), "待机不是姿态");
    run.False(StoryPoses.IsPose("walk-east"), "行走不是姿态");
    run.False(StoryPoses.IsPose("attack-north"), "普攻不是姿态");
    run.False(StoryPoses.IsPose("cry"), "光有姿态名没有朝向不算片段");
});

run.Case("导航：同阵营不挡路，主角与敌人会挡住人", () =>
{
    var grid = Grid([
        "........",
        "........",
        "........",
        "........",
    ]);

    var box = new CollisionBox { X = -8, Y = -8, Width = 16, Height = 16 };
    var player = new FakeNavActor("player", box, new Vec2(64, 32), party: false);
    var ally = new FakeNavActor("ally", box, new Vec2(64, 32 + 24), party: true);
    var enemy = new FakeNavActor("enemy", box, new Vec2(64 + 24, 32), party: false);
    var nav = new NavigationWorld("test", grid, () => [player, ally, enemy]);
    var shape = NavigationShape.Of(box);

    run.False(nav.CanPlaceBody(shape, player.Position), "主角的位置放不下第二个人");
    run.False(nav.CanPlaceBody(shape, enemy.Position), "敌人的位置也占着");
    run.True(nav.CanPlaceBody(shape, ally.Position, new NavigationIgnore("ally-2", Party: true)),
        "伙伴的位置对另一个伙伴是空的（同阵营不占位）");
    run.True(nav.CanPlaceBody(shape, new Vec2(20, 20)), "空地放得下");
});

run.Case("对白逐字：24 毫秒出 2 个字，余数留到下一次", () =>
{
    var typed = new TypeProgress();
    typed.Reset();

    run.Equal(0, typed.Advance(23, 100), "23 毫秒还没到一档");
    run.Equal(0, typed.Revealed);

    run.Equal(2, typed.Advance(1, 100), "满 24 毫秒出 2 个字");
    run.Equal(2, typed.Revealed);

    // 一次跨三档 → 6 个字；多出来的半档留到下一次，不能攒进这个字里。
    run.Equal(6, typed.Advance(24 * 3 + 12, 100), "三档 6 个字");
    run.Equal(8, typed.Revealed);
    run.Equal(0, typed.Advance(11, 100), "余下 11 毫秒不够一档");

    typed.Reset();
    run.Equal(4, typed.Advance(1000, 4), "总数封顶");
    run.Equal(4, typed.Revealed);
    run.Equal(0, typed.Advance(1000, 4), "到顶之后不再推进");

    typed.Reset();
    run.Equal(0, typed.Advance(1000, 0), "空句子不出字");

    typed.Reset();
    typed.Advance(23, 100);
    typed.Complete(100);
    run.Equal(100, typed.Revealed, "补全到结尾");
    typed.Reset();
    run.Equal(0, typed.Revealed, "重开一句从 0 开始");
});

run.Case("剧情运行表：身份、序号、游标、状态与快照", () =>
{
    var table = new StoryRunTable();

    var firstRun = table.Begin("event-41", "start", "event-41", "event-41");
    run.Equal("run-1", firstRun.Id, "第一条运行按序号起名");
    run.Equal("event-41@1", firstRun.Occurrence, "实例键是事件 id 加序号");
    run.Equal("running", table.StatusOf("event-41"));
    run.Equal("idle", table.StatusOf("event-99"), "没跑过的事件是 idle");

    table.MoveTo("event-41", "second");
    run.Equal("second", table.Current?.Node, "游标跟着节点走");

    table.Waiting(true);
    run.Equal("waiting", table.StatusOf("event-41"));
    table.Complete();
    run.Equal("waiting", table.StatusOf("event-41"), "等待中不结账");

    table.Waiting(false);
    run.Equal("running", table.StatusOf("event-41"), "战斗接上后回到 running");
    table.Complete();
    run.Equal("completed", table.StatusOf("event-41"));
    run.Equal("event-41", string.Join(',', table.CompletedEvents));

    table.Begin("event-41", "start", "event-41", "event-41");
    run.Equal("run-2", table.Current?.Id, "第二次是新的一条运行");
    run.Equal("event-41@2", table.Current?.Occurrence);
    run.Equal("running", table.StatusOf("event-41"), "还在演就是 running，不看已完成名单");

    var snapshot = table.Capture();
    var restored = new StoryRunTable();
    restored.Restore(snapshot);
    run.Equal(table.Sequence, restored.Sequence, "序号跟着快照走");
    run.Equal("running", restored.StatusOf("event-41"));
    run.Equal("start", restored.Current?.Node, "游标跟着快照走");
    run.Equal("event-41", string.Join(',', restored.CompletedEvents));

    run.Throws(() => restored.Restore(new StoryRunSnapshot
    {
        Runs = [new StoryRunEntry { Id = "run-1", Status = "running" },
                new StoryRunEntry { Id = "run-1", Status = "running" }],
        Sequence = 1,
    }), "剧情运行标识无效");
    run.Throws(() => restored.Restore(new StoryRunSnapshot
    {
        Runs = [new StoryRunEntry { Id = "run-1", Status = "flying" }],
        Sequence = 1,
    }), "剧情运行状态无效");

    restored.Restore(null);
    run.Equal(0, restored.Sequence);
    run.Equal("idle", restored.StatusOf("event-41"));
});

run.Case("剧情游标随存档往返：读档从那个节点接着演", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var (hero, party) = ReadParty(root, "dark-knight");
    var session = new Session { Bag = hero.Bag, Hero = hero, Party = party, Journal = new QuestJournal() };
    session.StoryCursor = new StoryCursorState
    {
        Script = "event-41", Node = "second", EventId = "event-41", EncounterId = "event-41:second:battle",
    };

    var summary = SaveGameStore.SaveSummary(session, "shadow-camp", "黑暗骑士团营地", "黑暗骑士", "p.png");
    var json = JsonSerializer.Serialize(
        SaveGameStore.CaptureSaveGame(session, null, summary), SaveGameStore.JsonOptions);
    run.True(json.Contains("\"storyCursor\""), "存档里带上剧情游标");

    var game = JsonSerializer.Deserialize<SaveGame>(json, SaveGameStore.JsonOptions)!;
    run.Equal("second", game.Session.StoryCursor?.Node, "节点进了存档");
    run.Equal("event-41:second:battle", game.Session.StoryCursor?.EncounterId, "挂着的遭遇也进了存档");

    var (freshHero, freshParty) = ReadParty(root, "dark-knight");
    var fresh = new Session
    {
        Bag = freshHero.Bag, Hero = freshHero, Party = freshParty, Journal = new QuestJournal(),
    };
    SaveGameStore.RestoreSaveGame(fresh, game);
    run.Equal("event-41", fresh.ConsumePendingStoryCursor()?.EventId, "读档后取得到游标");
    run.Null(fresh.ConsumePendingStoryCursor(), "续播只发生一次");

    var legacy = JsonNode.Parse(json)!.AsObject();
    legacy["format"] = 39;
    legacy["session"]!.AsObject().Remove("storyCursor");
    var upgraded = SavedGameUpgrades.Upgrade(
        JsonSerializer.Deserialize<SaveGame>(legacy.ToJsonString(), SaveGameStore.JsonOptions)!);
    run.Equal(SaveGameStore.SaveFormat, upgraded.Format, "39 版旧档能读");
    run.Null(upgraded.Session.StoryCursor, "旧档按没有演到一半处理");
});

run.Case("移动速度折算：水面减半、属性缩放、只在末尾取整", () =>
{
    const double playerSpeed = 8.0 * 1000.0 / 60.0;

    run.Equal(133f, MovementSpeedRule.Apply(playerSpeed, 1, 1000, false), "岸上按基准速度");
    run.Equal(66f, MovementSpeedRule.Apply(playerSpeed, 1, 1000, true), "水格减半");
    run.Equal(33f, MovementSpeedRule.Apply(playerSpeed, 1, 500, true), "属性减半再踩水");
    run.Equal(266f, MovementSpeedRule.Apply(playerSpeed, 2, 1000, false), "GM 倍率直接相乘");

    run.Equal(15f, MovementSpeedRule.Apply(7.5, 1, 2000, false), "只在末尾向零截断一次");
    run.Equal(0f, MovementSpeedRule.Apply(playerSpeed, 1, -100, false), "负倍率夹到 0");
    run.Equal(0f, MovementSpeedRule.Apply(0, 1, 1000, true), "零基准仍是零");
});

run.Case("读档战斗校验：脏档拒绝，正常档静止化", () =>
{
    WorldSnapshotState Build()
    {
        var state = new WorldSnapshotState();
        state.Party.Add(new WorldMemberSaved
        {
            Id = "player", X = 10, Y = 20, Facing = "south",
            Ai = new PartyControllerState
            {
                Path = [new Vec2(1, 1), new Vec2(2, 2)],
                Destination = new Vec2(50, 50),
                DestinationTarget = "enemy-1",
                Accumulator = 3.5,
            },
        });
        state.Enemies.Add(new WorldEnemySaved
        {
            Id = "enemy-1", EnemyId = "enemy-sewer-rat-1", X = 30, Y = 40, Health = 7,
            Facing = "north",
            EnemyAi = new EnemyControllerState
            {
                State = "chasing", TargetId = "player", PendingReturn = true,
                ThreatCombatActive = true, Accumulator = 9, Clock = 100, NextPathAt = 7,
                LastProgressAt = 3, IdleElapsed = 2,
                Path = [new Vec2(11, 12)], Destination = new Vec2(10, 20),
                WanderDestination = new Vec2(5, 5), ProgressPosition = new Vec2(9, 9),
                Combat = new CombatSnapshot { State = "attacking", TargetId = "player" },
                SpentOnce = ["enemy-1:special"],
            },
        });
        return state;
    }

    var catalog = new HashSet<string> { "enemy-sewer-rat-1" };

    var state = Build();
    CombatSnapshotValidation.Validate(state, catalog, []);
    CombatSnapshotValidation.Stationaryize(state);

    var ai = state.Enemies[0].EnemyAi!;
    run.Equal("idle", ai.State, "敌人状态回 idle");
    run.Null(ai.TargetId, "目标清空");
    run.Equal(0, ai.Path.Count, "路径清空");
    run.Null(ai.Destination, "目的地清空");
    run.Null(ai.WanderDestination, "漫游点清空");
    run.False(ai.PendingReturn, "回家标记清空");
    run.Equal(0.0, ai.Accumulator, "累加器清零");
    run.Equal("idle", ai.Combat?.State, "战斗子状态回 idle");
    run.Null(ai.Combat?.TargetId, "战斗子状态目标清空");
    run.Equal(100L, ai.Clock, "时钟保留");
    run.Equal(2L, ai.IdleElapsed, "闲置计时保留");
    run.Equal(1, ai.SpentOnce?.Count ?? 0, "每场一次的记录保留");

    var partyAi = state.Party[0].Ai!;
    run.Equal(0, partyAi.Path.Count, "伙伴路径清空");
    run.Null(partyAi.Destination, "伙伴目的地清空");
    run.Null(partyAi.DestinationTarget, "伙伴目标清空");
    run.Equal(0.0, partyAi.Accumulator, "伙伴累加器清零");

    void Rejects(Action<WorldSnapshotState> corrupt, string label)
    {
        var broken = Build();
        corrupt(broken);
        var threw = false;
        try { CombatSnapshotValidation.Validate(broken, catalog, []); }
        catch (InvalidOperationException) { threw = true; }
        run.True(threw, label);
    }

    Rejects(broken => broken.Enemies[0].EnemyId = "enemy-does-not-exist", "未知的敌人种类被拒");
    Rejects(broken => broken.Enemies.Add(broken.Enemies[0]), "重复的场上键被拒");
    Rejects(broken => broken.Enemies[0].Health = -1, "负生命被拒");
    Rejects(broken => broken.Enemies[0].X = double.NaN, "非有限坐标被拒");
    Rejects(broken => broken.Party[0].Facing = "up", "非法朝向被拒");
    Rejects(broken => broken.Enemies[0].SummonedBy = "enemy-1", "主人指向自己被拒");
    Rejects(broken => broken.Enemies[0].SummonedBy = "ghost-9", "主人不在场上被拒");

    Rejects(broken =>
    {
        broken.Enemies[0].SummonedBy = "enemy-2";
        broken.Enemies.Add(new WorldEnemySaved
        {
            Id = "enemy-2", EnemyId = "enemy-sewer-rat-1", SummonedBy = "enemy-1",
        });
    }, "召唤主人关系成环被拒");

    // 上阵成员名下的召唤物必须挂友方召唤那一套行为：挂着敌方行为就是脏档
    // （敌方行为分支里遇到友方身份直接抛错）。
    var withSummon = Build();
    withSummon.Enemies[0].SummonedBy = "ally-1";
    var friendlyKindRejected = false;
    try { CombatSnapshotValidation.Validate(withSummon, catalog, ["ally-1"]); }
    catch (InvalidOperationException) { friendlyKindRejected = true; }
    run.True(friendlyKindRejected, "上阵成员名下的召唤物挂敌方行为被拒");

    EnemyControllerState Variant(EnemyControllerState source, string? state = null,
        double? accumulator = null, long? clock = null) => new()
    {
        State = state ?? source.State, TargetId = source.TargetId, PendingReturn = source.PendingReturn,
        ThreatCombatActive = source.ThreatCombatActive, Accumulator = accumulator ?? source.Accumulator,
        Clock = clock ?? source.Clock, NextPathAt = source.NextPathAt, Path = source.Path,
        Destination = source.Destination, DestinationRange = source.DestinationRange,
        ProgressPosition = source.ProgressPosition, LastProgressAt = source.LastProgressAt,
        IdleElapsed = source.IdleElapsed, WanderDestination = source.WanderDestination,
        Combat = source.Combat, SpentOnce = source.SpentOnce,
    };

    Rejects(broken => broken.Enemies[0].EnemyAi = Variant(broken.Enemies[0].EnemyAi!, state: "sleeping"),
        "未知的敌人行为状态被拒");
    Rejects(broken => broken.Enemies[0].EnemyAi = Variant(broken.Enemies[0].EnemyAi!, accumulator: double.NaN),
        "累加器是 NaN 被拒");
    Rejects(broken => broken.Enemies[0].EnemyAi = Variant(broken.Enemies[0].EnemyAi!, clock: -5),
        "负时钟被拒");
    Rejects(broken => broken.Enemies[0].SummonAi = new FriendlySummonState
        {
            OwnerId = "player", Role = "hell-soldier", State = "following",
        }, "野生怪带召唤行为被拒");

    // 友方召唤：主人必须在活动队伍里，分工要跟种类对上。
    var summonCatalog = new HashSet<string> { "enemy-hell-s-pawn-1" };
    WorldEnemySaved Friendly(string ownerId = "ally-1", string role = "hell-soldier",
        string? aiOwner = null) => new()
    {
        Id = "summon-1", EnemyId = "enemy-hell-s-pawn-1", SummonedBy = ownerId, Facing = "south",
        SummonAi = new FriendlySummonState
        {
            OwnerId = aiOwner ?? ownerId, Role = role, State = "following",
        },
    };

    var good = new WorldSnapshotState();
    good.Enemies.Add(Friendly());
    CombatSnapshotValidation.Validate(good, summonCatalog, ["ally-1"]);
    run.True(true, "主人是上阵成员、分工对得上的召唤物放行");

    void RejectsSummon(WorldEnemySaved enemy, string label)
    {
        var broken = new WorldSnapshotState();
        broken.Enemies.Add(enemy);
        var threw = false;
        try { CombatSnapshotValidation.Validate(broken, summonCatalog, ["ally-1"]); }
        catch (InvalidOperationException) { threw = true; }
        run.True(threw, label);
    }

    RejectsSummon(Friendly(role: "beast"), "分工与种类对不上被拒");
    RejectsSummon(Friendly(ownerId: "ally-9"), "主人不在队伍里被拒");

    RejectsSummon(Friendly(aiOwner: "ally-2"), "行为状态里的主人与召主字段不一致被拒");

    var noState = Friendly();
    noState.SummonAi = null;
    RejectsSummon(noState, "友方召唤缺行为状态被拒");

    var wrongKind = Friendly();
    wrongKind.EnemyAi = new EnemyControllerState { State = "idle" };
    RejectsSummon(wrongKind, "友方召唤用了敌方行为状态被拒");
});

run.Case("内容形状校验：世界与地图的字段名、尺幅与标识", () =>
{
    JsonObject Hero() => new()
    {
        ["id"] = "hero",
        ["name"] = "主角",
        ["description"] = "",
        ["clips"] = new JsonObject(),
        ["portrait"] = null,
    };
    JsonObject World() => new()
    {
        ["version"] = 1,
        ["maps"] = new JsonArray(new JsonObject
        {
            ["id"] = "m1", ["name"] = "测试图", ["file"] = "content/maps/m1.json",
        }),
        ["actors"] = new JsonObject { ["hero"] = Hero() },
        ["portraits"] = new JsonObject(),
        ["props"] = new JsonArray(),
    };
    JsonObject Map() => new()
    {
        ["id"] = "m1",
        ["name"] = "测试图",
        ["width"] = 32,
        ["height"] = 32,
        ["tileSize"] = 16,
        ["columns"] = 2,
        ["rows"] = 2,
        ["blocked"] = new JsonArray(false, false, false, false),
        ["ground"] = "assets/ground.png",
        ["canopy"] = "assets/canopy.png",
        ["features"] = new JsonArray(),
        ["entities"] = new JsonArray(),
        ["exits"] = new JsonArray(),
    };

    WorldShape.ValidateWorld(World());
    WorldShape.ValidateMap(Map(), "m1");

    run.Throws(() =>
    {
        var world = World();
        world.Remove("version");
        WorldShape.ValidateWorld(world);
    }, "world.version", "缺必填字段");
    run.Throws(() =>
    {
        var world = World();
        world["versoin"] = 1;
        WorldShape.ValidateWorld(world);
    }, "world.versoin: 未定义字段", "字段名敲错");
    run.Throws(() =>
    {
        var map = Map();
        map["width"] = "32";
        WorldShape.ValidateMap(map, "m1");
    }, "map.m1.width", "尺寸写成字符串");

    // 可选字段：缺省可以，乱写不行——「缺键」与「显式 null」必须分开判。
    run.Throws(() =>
    {
        var world = World();
        ((JsonObject)world["actors"]!["hero"]!)["chest"] = "wood";
        WorldShape.ValidateWorld(world);
    }, "需要 ordinary/improved", "宝箱档位非法");

    run.Throws(() =>
    {
        var map = Map();
        map["blocked"] = new JsonArray(false);
        WorldShape.ValidateMap(map, "m1");
    }, "格数不匹配", "格数与行列对不上");
    run.Throws(() =>
    {
        var map = Map();
        map["tileSize"] = 8;
        WorldShape.ValidateMap(map, "m1");
    }, "尺寸不匹配", "宽高不等于列数×格宽");
    run.Throws(() =>
    {
        var map = Map();
        map["water"] = new JsonArray(3, 1);
        WorldShape.ValidateMap(map, "m1");
    }, "水格索引需要严格递增", "水格逆序");
    run.Throws(() =>
    {
        var map = Map();
        map["lootLevel"] = 300;
        WorldShape.ValidateMap(map, "m1");
    }, "掉落等级需要在 0–255 之间", "掉落等级越界");
    run.Throws(() =>
    {
        var map = Map();
        var entities = (JsonArray)map["entities"]!;
        entities.Add(new JsonObject { ["id"] = "e1", ["actor"] = "hero", ["x"] = 1, ["y"] = 2 });
        entities.Add(new JsonObject { ["id"] = "e1", ["actor"] = "hero", ["x"] = 3, ["y"] = 4 });
        WorldShape.ValidateMap(map, "m1");
    }, "标识重复", "摆放 id 重复");
    run.Throws(() =>
    {
        var map = Map();
        map["enemySpawns"] = new JsonArray(new JsonObject
        {
            ["id"] = "s1", ["enemyId"] = "goblin", ["x"] = 99, ["y"] = 1,
        });
        WorldShape.ValidateMap(map, "m1");
    }, "敌人站位超出地图", "刷怪点在图外");
    run.Throws(() =>
    {
        var map = Map();
        ((JsonArray)map["exits"]!).Add(new JsonObject
        {
            ["x"] = 0, ["y"] = 0, ["width"] = 16, ["height"] = 16,
            ["destination"] = "m1", ["facing"] = "south", ["exitFacing"] = "south",
        });
        WorldShape.ValidateMap(map, "m1");
    }, "spawn: 需要对象", "出口缺落点");
});

run.Case("内容形状校验：真实内容的世界与地图全部通过", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var world = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "content", "world.json")))!;
    WorldShape.ValidateWorld(world);

    var maps = world["maps"]!.AsArray();
    foreach (var entry in maps.Take(20))
    {
        var file = entry!["file"]!.GetValue<string>();
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(root, file)))!;
        WorldShape.ValidateMap(node, entry["id"]!.GetValue<string>());
    }
});

run.Case("内容形状校验：物品、商店、伙伴与职业表的坏档能报在具体路径上", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    JsonNode Load(string name) => JsonNode.Parse(
        File.ReadAllText(Path.Combine(root, "content", name)))!;

    CatalogShape.ValidateItems(Load("items.json"));
    CatalogShape.ValidateShopCatalog(Load("shops.json"));
    CatalogShape.ValidateCompanionCatalog(Load("companions.json"));
    CatalogShape.ValidateStoryClassCatalog(Load("story-classes.json"));

    var items = Load("items.json");
    var entry = ((JsonObject)items["items"]!).First();
    var itemId = entry.Key;
    ((JsonObject)entry.Value!).Remove("stackable");
    run.Throws(() => CatalogShape.ValidateItems(items), $"items.items.{itemId}.stackable", "物品缺必填字段");

    items = Load("items.json");
    ((JsonObject)((JsonObject)items["items"]!).First().Value!)["category"] = "gear";
    run.Throws(() => CatalogShape.ValidateItems(items), "需要 equipment/consumable/material/quest", "物品分类非法");

    items = Load("items.json");
    ((JsonObject)((JsonObject)items["items"]!).First().Value!)["stackabel"] = true;
    run.Throws(() => CatalogShape.ValidateItems(items), "未定义字段", "物品字段名敲错");

    var shops = Load("shops.json");
    var shopEntries = (JsonObject)shops["shops"]!;
    shopEntries.Remove(shopEntries.First().Key);
    run.Throws(() => CatalogShape.ValidateShopCatalog(shops), "需要 18 个商店 NPC", "商店数量不足");

    shops = Load("shops.json");
    ((JsonObject)((JsonObject)shops["shops"]!).First().Value!)["priceRatePercent"] = 0;
    run.Throws(() => CatalogShape.ValidateShopCatalog(shops), "必须是正整数", "价格倍率写成 0");

    var companions = Load("companions.json");
    var firstCompanion = ((JsonObject)companions["definitions"]!).First();
    ((JsonObject)firstCompanion.Value!)["id"] = "someone-else";
    run.Throws(() => CatalogShape.ValidateCompanionCatalog(companions), "伙伴标识不一致", "伙伴 id 不一致");

    var classes = Load("story-classes.json");
    ((JsonObject)((JsonArray)classes["classes"]!)[0]!["playerActor"]!).Remove("headAnchor");
    run.Throws(() => CatalogShape.ValidateStoryClassCatalog(classes), "头顶状态条需要人物头部锚点", "缺头部锚点");
});

run.Case("内容形状校验：数值表与界面目录的坏档也能报出来", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    JsonNode Load(string name) => JsonNode.Parse(
        File.ReadAllText(Path.Combine(root, "content", name)))!;

    UiShape.ValidateCharacterStats(Load("character-stats.json"));
    UiShape.ValidateStatusHud(Load("status-hud.json"));
    UiShape.ValidateControlSkin(Load("control-skin.json"));
    UiShape.ValidateStoryInterface(Load("story-interface.json"));
    UiShape.ValidateMenuPanels(Load("menu-panels.json"));

    var stats = Load("character-stats.json");
    ((JsonArray)stats["experience"]!).RemoveAt(0);
    run.Throws(() => UiShape.ValidateCharacterStats(stats), "经验表必须覆盖全部等级", "经验表少一级");

    stats = Load("character-stats.json");
    var levels = (JsonArray)stats["experience"]!;
    levels[0] = null;
    run.Throws(() => UiShape.ValidateCharacterStats(stats), "仅满级可以没有下一级经验", "非满级缺经验值");

    var hud = Load("status-hud.json");
    ((JsonObject)hud["assets"]!).Remove("player-shadow");
    run.Throws(() => UiShape.ValidateStatusHud(hud), "状态素材缺失 player-shadow", "状态素材缺项");

    hud = Load("status-hud.json");
    var colors = (JsonArray)hud["levelColors"]!;
    colors[0] = new JsonObject { ["minDifference"] = 1, ["color"] = "#ffffff" };
    run.Throws(() => UiShape.ValidateStatusHud(hud), "等级阈值必须按降序排列", "等级色没降序");

    hud = Load("status-hud.json");
    ((JsonObject)hud["target"]!["gauge"]!)["x"] = 400;
    run.Throws(() => UiShape.ValidateStatusHud(hud), "血条超出面板", "血条越界");

    var menu = Load("menu-panels.json");
    ((JsonArray)menu["equipmentSlots"]!).RemoveAt(0);
    run.Throws(() => UiShape.ValidateMenuPanels(menu), "需要十个独立装备槽", "装备槽少一个");

    menu = Load("menu-panels.json");
    ((JsonObject)((JsonArray)menu["skillPositions"]!)[0]!)["column"] = 3;
    run.Throws(() => UiShape.ValidateMenuPanels(menu), "技能位置超出排列范围", "技能位越界");

    var skin = Load("control-skin.json");
    ((JsonObject)skin).Remove("stick-base");
    run.Throws(() => UiShape.ValidateControlSkin(skin), "操作素材缺失 stick-base", "操作素材缺项");

    var face = Load("story-interface.json");
    ((JsonObject)face["player"]!)["health"] = 99999;
    run.Throws(() => UiShape.ValidateStoryInterface(face), "当前状态超过上限", "初始血量超过上限");
});

run.Case("内容形状校验：战斗与换装的坏档能报在具体路径上", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    JsonNode Load(string name) => JsonNode.Parse(
        File.ReadAllText(Path.Combine(root, "content", name)))!;

    BattleShape.ValidateBattle(Load("battle.json"));
    WardrobeShape.ValidateWardrobe(Load("wardrobe.json"));

    var battle = Load("battle.json");
    var attackId = ((JsonObject)battle["basicAttacks"]!).First().Key;
    ((JsonObject)((JsonObject)battle["basicAttacks"]!)[attackId]!).Remove("cycleFrames");
    run.Throws(() => BattleShape.ValidateBattle(battle),
        $"battle.basicAttacks.{attackId}.cycleFrames", "普攻缺必填字段");

    battle = Load("battle.json");
    var skillId = ((JsonObject)battle["skills"]!).First().Key;
    ((JsonObject)((JsonObject)battle["skills"]!)[skillId]!)["kind"] = "toggle";
    run.Throws(() => BattleShape.ValidateBattle(battle), "需要 active/passive", "技能分类非法");

    battle = Load("battle.json");
    skillId = ((JsonObject)battle["skills"]!).First().Key;
    ((JsonObject)((JsonObject)((JsonObject)battle["skills"]!)[skillId]!)["action"]!)["cooldonFrames"] = 3;
    run.Throws(() => BattleShape.ValidateBattle(battle), "未定义字段", "动作字段敲错");

    battle = Load("battle.json");
    foreach (var (_, skill) in (JsonObject)battle["skills"]!)
    {
        var units = ((skill?["action"] as JsonObject)?["combatUnits"] as JsonArray)?
            .Select(node => node as JsonObject)
            .FirstOrDefault(unit => unit?["effect"]?.GetValue<string>() == "buff");
        var effect = (JsonObject)((JsonArray)units!["ranks"]!)[0]!["effect"]!;
        if (effect["simulationFrames"] is not JsonObject frames) continue;
        effect["durationMs"] = 4561;
        run.Throws(() => BattleShape.ValidateBattle(battle),
            "需要首个逻辑处理到到期之间的时长", "增益帧计时对不上");
        break;
    }

    battle = Load("battle.json");
    ((JsonObject)((JsonArray)battle["enemies"]!)[0]!)["level"] = 200;
    run.Throws(() => BattleShape.ValidateBattle(battle), "怪物等级上限为 107", "怪物等级越界");

    battle = Load("battle.json");
    var digitName = ((JsonArray)((JsonObject)((JsonObject)battle["feedback"]!)["digits"]!)["enemy"]!)[0]!.GetValue<string>();
    ((JsonObject)battle["attackEffects"]!).Remove(digitName);
    run.Throws(() => BattleShape.ValidateBattle(battle), $"feedback.{digitName}", "反馈素材缺失");

    var wardrobe = Load("wardrobe.json");
    ((JsonObject)((JsonObject)wardrobe["sprites"]!).First().Value!)["texture"] = "wardrobe-parts-9";
    run.Throws(() => WardrobeShape.ValidateWardrobe(wardrobe), "部件纹理不存在", "部件纹理写错");

    wardrobe = Load("wardrobe.json");
    var stanceId = ((JsonObject)wardrobe["stances"]!).First().Key;
    ((JsonObject)wardrobe["attacks"]!).Remove(stanceId);
    run.Throws(() => WardrobeShape.ValidateWardrobe(wardrobe), "wardrobe.attacks", "普攻规则缺失");

    wardrobe = Load("wardrobe.json");
    var dual = (JsonObject)((JsonObject)wardrobe["dualWieldAttacks"]!).First().Value!;
    dual["strikes"] = new JsonArray(
        new JsonObject { ["frame"] = 1, ["hand"] = "offhand" },
        new JsonObject { ["frame"] = 3, ["hand"] = "main-hand" });
    run.Throws(() => WardrobeShape.ValidateWardrobe(wardrobe), "双持出手顺序无效", "双持出手顺序颠倒");

    wardrobe = Load("wardrobe.json");
    var pose = (JsonObject)((JsonObject)wardrobe["poses"]!).First().Value!;
    ((JsonArray)((JsonArray)pose["frames"]!)[0]!)[0]!["alpha"] = 2;
    run.Throws(() => WardrobeShape.ValidateWardrobe(wardrobe), "部件变换无效", "部件透明度越界");
});

run.Case("数值收尾：空职业表不可穿、名字按数字排、整理保持同序、保底档校验", () =>
{
    var catalog = CatalogOf(
        ("plain", Def("素剑", "equipment", "main-hand", stackable: false, classes: [])),
        ("knight", Def("骑士剑", "equipment", "main-hand", stackable: false)));
    var bag = new Bag(() => { }, catalog, "dark-knight");
    bag.Add("plain");
    bag.Add("knight");
    var plain = bag.Entries.First(entry => entry.ItemId == "plain").Id;
    var knight = bag.Entries.First(entry => entry.ItemId == "knight").Id;
    run.True(bag.EquipProblem(plain, "main-hand") is not null, "空职业表不可穿");
    run.Null(bag.EquipProblem(knight, "main-hand"), "写了职业就能穿");

    var items = new ItemCatalog
    {
        Items = new Dictionary<string, ItemDefinition>
        {
            ["stone-10"] = new() { Name = "封印石10", Category = "material" },
            ["stone-2"] = new() { Name = "封印石2", Category = "material" },
            ["amulet"] = new() { Name = "护符", Category = "material" },
        },
    };
    var ordered = InventoryViews.Organized(
        new Dictionary<string, int> { ["stone-10"] = 1, ["stone-2"] = 1, ["amulet"] = 1 }, items.Items);
    run.Equal("stone-2,stone-10,amulet", string.Join(",", ordered), "数字感知排序");

    var stack = CatalogOf(("gem", Def("同石", "material")));
    var gemBag = new Bag(() => { }, stack, "dark-knight");
    gemBag.Add("gem", 1);
    for (var index = 0; index < 20; index += 1) gemBag.Add("gem");
    var before = gemBag.AllCells.Select(cell => cell?.Id ?? string.Empty).ToList();
    gemBag.Organize();
    run.Equal(string.Join(",", before),
        string.Join(",", gemBag.AllCells.Select(cell => cell?.Id ?? string.Empty)), "同 rank 保持原序");

    var pity = new LootPity();
    pity.RestoreState(pity.CaptureState());
    run.Throws(() => pity.RestoreState(new LootPity.LootPityState(2, [], [], [])),
        "掉落保底状态无效", "版本不对");
    run.Throws(() => pity.RestoreState(new LootPity.LootPityState(1,
        [new KeyValuePair<string, LootPity.PityCounters>("m", new LootPity.PityCounters(-1, 0))], [], [])),
        "掉落保底计数无效", "负计数");
    pity.RestoreState(new LootPity.LootPityState(0, [], [], []));
});

run.Case("数值收尾：伙伴经验表缺项时停在原地", () =>
{
    var stats = new CharacterStatsCatalog
    {
        MaxLevel = 3,
        Experience = [10, null, null],
        Classes = new Dictionary<string, ClassAttributes>
        {
            ["dark-knight"] = new() { Strength = 13, Dexterity = 9, Vitality = 12, Intelligence = 9, Mentality = 5 },
        },
    };
    var items = new ItemCatalog();
    var companions = new CompanionCatalog
    {
        Definitions = new Dictionary<string, CompanionDefinition>
        {
            ["c1"] = new() { Id = "c1", Name = "伙伴", Actor = "hero", ClassId = "dark-knight", Source = "story" },
        },
        Growth = new Dictionary<string, PrimaryAttributes[]>
        {
            ["dark-knight"] = Enumerable.Range(0, 3).Select(_ => new PrimaryAttributes
            {
                Strength = 13, Dexterity = 9, Vitality = 12, Intelligence = 9, Mentality = 5,
            }).ToArray(),
        },
    };
    var hero = new BattleCharacter("dark-knight", stats, items);
    var party = new PartyRoster(hero, stats, items, companions, () => { });

    var member = party.JoinStory("c1");
    member.AddExperience(100);
    run.Equal(2, member.Level, "升到 2 级后表缺项停升");
    run.Equal(90, member.Player.Experience, "多余经验留在当前级");
});

run.Case("内容形状校验：剧情目录的坏档与坏图能报在具体路径上", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    JsonNode Load() => JsonNode.Parse(
        File.ReadAllText(Path.Combine(root, "content", "campaign.json")))!;

    CampaignShape.ValidateCampaign(Load());

    var campaign = Load();
    var quest = (JsonObject)((JsonArray)campaign["quests"]!)[0]!;
    quest.Remove("progressText");
    run.Throws(() => CampaignShape.ValidateCampaign(campaign),
        $"campaign.quests[0].progressText", "任务缺必填字段");

    campaign = Load();
    var popupScript = (JsonObject)campaign["scripts"]!.AsObject().First().Value!;
    run.Throws(() =>
    {
        var node = (JsonObject)popupScript["nodes"]![0]!;
        ((JsonArray)node["presentation"]!).Add(new JsonObject
        {
            ["type"] = "quest-popup", ["kind"] = "start", ["quest"] = "no-such-quest",
        });
        CampaignShape.ValidateCampaign(campaign);
    }, "提示任务不存在", "提示任务不存在");

    campaign = Load();
    run.Throws(() =>
    {
        var script = (JsonObject)campaign["scripts"]!["event-1"]!;
        var node = script["nodes"]!.AsArray().OfType<JsonObject>()
            .First(n => n["id"]!.GetValue<string>() == "scene-2");
        ((JsonObject)((JsonArray)node["presentation"]!)[0]!)["type"] = "dance";
        CampaignShape.ValidateCampaign(campaign);
    }, "未定义演出动作 dance", "演出动作类型写错");

    campaign = Load();
    run.Throws(() =>
    {
        var script = (JsonObject)campaign["scripts"]!["event-1"]!;
        var node = script["nodes"]!.AsArray().OfType<JsonObject>()
            .First(n => n["id"]!.GetValue<string>() == "scene-2");
        var parallel = (JsonObject)((JsonArray)node["presentation"]!).AsArray()
            .OfType<JsonObject>().First(action => action["type"]!.GetValue<string>() == "parallel");
        ((JsonArray)parallel["actions"]!).Add(((JsonArray)parallel["actions"]!)[0]!.DeepClone());
        CampaignShape.ValidateCampaign(campaign);
    }, "并行动作重复控制同一个人物或镜头", "并行动作重复控制");

    campaign = Load();
    run.Throws(() =>
    {
        var script = (JsonObject)campaign["scripts"]!.AsObject().First().Value!;
        var node = (JsonObject)script["nodes"]![0]!;
        ((JsonArray)node["commands"]!).Add(new JsonObject
        {
            ["type"] = "quest", ["command"] = new JsonObject { ["type"] = "accept", ["quest"] = "no-such-quest" },
        });
        CampaignShape.ValidateCampaign(campaign);
    }, "命令任务不存在 no-such-quest", "命令任务不存在");

    campaign = Load();
    run.Throws(() =>
    {
        var script = (JsonObject)campaign["scripts"]!.AsObject().First().Value!;
        var nodes = (JsonArray)script["nodes"]!;
        nodes.Add(nodes[0]!.DeepClone());
        CampaignShape.ValidateCampaign(campaign);
    }, "节点重复或为空", "节点重复");

    campaign = Load();
    run.Throws(() =>
    {
        var script = (JsonObject)campaign["scripts"]!.AsObject().First().Value!;
        script["entry"] = "no-such-node";
        CampaignShape.ValidateCampaign(campaign);
    }, "入口不存在", "入口缺失");

    campaign = Load();
    run.Throws(() =>
    {
        foreach (var (_, script) in (JsonObject)campaign["scripts"]!)
        foreach (var node in (JsonArray)script!["nodes"]!)
        {
            var exit = node!["exit"] as JsonObject;
            if (exit?["type"]?.GetValue<string>() != "choice") continue;
            var cases = (JsonObject)exit["cases"]!;
            var key = cases.First().Key;
            var target = cases[key];
            cases.Remove(key);
            cases["no-such-option"] = target;
            CampaignShape.ValidateCampaign(campaign);
            return;
        }

        throw new InvalidOperationException("内容里没有带分支出口的节点");
    }, "选项与分支出口不匹配", "分支出口指向不存在的选项");
});

run.Case("内容关系校验：真实内容全部通过，改坏一处能报出来", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    // battle.json 的动作是联合类型，要挂与加载器同一套转换器（不然技能事件解析不了）。
    var options = new JsonSerializerOptions { AllowTrailingCommas = true };
    SkillActionJson.Configure(options);
    T Read<T>(string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(root, "content", name)), options)!;

    var items = Read<ItemCatalog>("items.json");
    var skills = Read<SkillCatalog>("battle.json");
    var shops = Read<ShopCatalog>("shops.json");
    var world = Read<WorldIndex>("world.json");
    var stats = Read<CharacterStatsCatalog>("character-stats.json");

    // ① 真实内容必须全过——三条校验覆盖 2000+ 件物品、全部商店与数值表。
    var failed = string.Empty;
    try
    {
        ContentValidation.ValidateItemSkills(items, skills);
        ContentValidation.ValidateShops(shops, items, world);
        ContentValidation.ValidateCharacterStats(stats, items, skills);
    }
    catch (InvalidDataException error) { failed = error.Message; }
    run.Equal(string.Empty, failed, "真实内容通过三项关系校验");

    // ①b 后三条：剧情物品、伙伴、菜单职业。真实内容同样必须全过。
    var companions = Read<CompanionCatalog>("companions.json");
    var menu = Read<MenuPanelsCatalog>("menu-panels.json");
    var campaign = Read<CampaignContent>("campaign.json");
    var face = Read<StoryInterfaceAssets>("story-interface.json");
    failed = string.Empty;
    try
    {
        ContentValidation.ValidateStoryItems(campaign, items);
        ContentValidation.ValidateCompanions(companions, world, skills, stats, campaign);
        ContentValidation.ValidateMenuClasses(menu, skills);
        ContentValidation.ValidateStoryPortraits(world, face);
    }
    catch (InvalidDataException error) { failed = error.Message; }
    run.Equal(string.Empty, failed, "真实内容通过剧情物品/伙伴/菜单三项校验");

    // ② 改坏一处必须报出来，否则上面的「通过」没有意义。
    string Break(Action action)
    {
        try { action(); return string.Empty; }
        catch (InvalidDataException error) { return error.Message; }
    }

    var missingSkillClass = new ItemCatalog
    {
        Items = items.Items,
        ClassSkills = new Dictionary<string, string[]>(items.ClassSkills),
    };
    missingSkillClass.ClassSkills.Remove(skills.Classes[0].Id);
    run.True(Break(() => ContentValidation.ValidateItemSkills(missingSkillClass, skills)).Contains("职业数量不匹配"),
        "少一个职业的技能候选会被拒");

    var wrongOrder = new ItemCatalog
    {
        Items = items.Items,
        ClassSkills = new Dictionary<string, string[]>(items.ClassSkills),
    };
    var firstClass = skills.Classes[0];
    if (firstClass.SkillIds.Length >= 2)
    {
        var swapped = firstClass.SkillIds.ToArray();
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        wrongOrder.ClassSkills[firstClass.Id] = swapped;
        run.True(Break(() => ContentValidation.ValidateItemSkills(wrongOrder, skills)).Contains("不匹配"),
            "候选顺序与职业表不一致会被拒");
    }

    var missingItem = new ShopCatalog
    {
        Shops = new Dictionary<string, ShopDefinition>(shops.Shops),
        LampShop = shops.LampShop,
        MapShopLevels = shops.MapShopLevels,
        Profiles = shops.Profiles,
        Equipment = shops.Equipment,
    };
    var shopId = shops.Shops.Keys.First();
    missingItem.Shops[shopId] = new ShopDefinition
    {
        Name = shops.Shops[shopId].Name,
        FixedStock = ["item-that-does-not-exist"],
    };
    run.True(Break(() => ContentValidation.ValidateShops(missingItem, items, world)).Contains("固定商品不存在"),
        "商店卖不存在的物品会被拒");

    var missingStats = new CharacterStatsCatalog
    {
        MaxLevel = stats.MaxLevel,
        Equipment = new Dictionary<string, EquipmentNumbers>(stats.Equipment),
        Gems = new Dictionary<string, ModifierRange[][]>(stats.Gems),
        Enchantments = stats.Enchantments,
        Classes = new Dictionary<string, ClassAttributes>(stats.Classes),
        Experience = stats.Experience,
        Grades = stats.Grades,
        Modifiers = stats.Modifiers,
    };
    missingStats.Classes.Remove(skills.Classes[0].Id);
    run.True(Break(() => ContentValidation.ValidateCharacterStats(missingStats, items, skills)).Contains("职业缺失"),
        "数值表缺职业会被拒");

    var goalItem = campaign.Quests.SelectMany(quest => quest.Goals)
        .FirstOrDefault(goal => goal.Kind is "item" or "recruit");
    if (goalItem is not null)
    {
        var strippedItems = new ItemCatalog
        {
            Items = new Dictionary<string, ItemDefinition>(items.Items),
            ClassSkills = items.ClassSkills,
        };
        strippedItems.Items.Remove(goalItem.Target);
        run.True(Break(() => ContentValidation.ValidateStoryItems(campaign, strippedItems))
                .Contains("任务目标物品不存在"),
            "任务目标指向不存在的物品会被拒");
    }

    var trimmedCompanions = new CompanionCatalog
    {
        Definitions = companions.Definitions,
        Growth = new Dictionary<string, PrimaryAttributes[]>(companions.Growth),
        Generation = companions.Generation,
        GroupSkills = companions.GroupSkills,
    };
    trimmedCompanions.Growth.Remove(companions.Definitions.Values.First().ClassId);
    run.True(Break(() => ContentValidation.ValidateCompanions(trimmedCompanions, world, skills, stats))
            .Contains("伙伴成长等级不完整"),
        "伙伴成长表缺职业会被拒");

    var noMenuClasses = new MenuPanelsCatalog
    {
        Classes = new Dictionary<string, ClassPanelInfo>(),
    };
    run.True(Break(() => ContentValidation.ValidateMenuClasses(noMenuClasses, skills))
            .Contains("职业数量不匹配"),
        "菜单缺职业资料会被拒");

    // ①c 换装与音频：真实内容同样必须全过。
    var wardrobe = Read<WardrobeCatalog>("wardrobe.json");
    var audio = Read<AudioCatalog>("audio.json");
    var battle = Read<BattleContent>("battle.json");
    failed = string.Empty;
    try
    {
        ContentValidation.ValidateWardrobe(wardrobe, items, skills, battle);
        ContentValidation.ValidateAudio(audio, items, world, battle, skills, wardrobe);
    }
    catch (InvalidDataException error) { failed = error.Message; }
    run.Equal(string.Empty, failed, "真实内容通过换装与音频覆盖校验");

    // ② 换装：拿掉一件装备的外观必须报出来。
    var equipped = items.Items.First(entry => entry.Value.Equipment is not null &&
                                              wardrobe.Equipment.ContainsKey(entry.Key));
    var noOutfit = new WardrobeCatalog
    {
        Poses = wardrobe.Poses,
        Variants = wardrobe.Variants,
        Equipment = new Dictionary<string, WardrobeEquipmentAppearance>(wardrobe.Equipment),
        Skills = wardrobe.Skills,
        SkillStances = wardrobe.SkillStances,
        Attacks = wardrobe.Attacks,
    };
    noOutfit.Equipment.Remove(equipped.Key);
    run.True(Break(() => ContentValidation.ValidateWardrobe(noOutfit, items, skills, battle))
            .Contains("缺少装备外观"),
        $"拿掉 {equipped.Key} 的外观会被拒");

    var lostMap = world.Maps[0].Id;
    var noMapAudio = new AudioCatalog
    {
        Version = audio.Version,
        Music = audio.Music,
        Effects = audio.Effects,
        MenuMusic = audio.MenuMusic,
        Maps = new Dictionary<string, AudioMapEntry>(audio.Maps),
        Enemies = audio.Enemies,
        Combat = audio.Combat,
        Skills = audio.Skills,
        Ui = audio.Ui,
    };
    noMapAudio.Maps.Remove(lostMap);
    noMapAudio.Maps["ghost-map"] = new AudioMapEntry();
    run.True(Break(() => ContentValidation.ValidateAudio(noMapAudio, items, world, battle, skills, wardrobe))
            .Contains("地图音频缺失"),
        $"拿掉 {lostMap} 的音频会被拒");

    var weaponKind = audio.Combat.Weapons.Keys.First(kind => kind != "unarmed");
    var lostSound = audio.Combat.Weapons[weaponKind].Normal[0].Sound;
    var noWeaponCue = new AudioCatalog
    {
        Version = audio.Version,
        Music = audio.Music,
        Effects = new Dictionary<string, string>(audio.Effects),
        MenuMusic = audio.MenuMusic,
        Maps = audio.Maps,
        Enemies = audio.Enemies,
        Combat = audio.Combat,
        Skills = audio.Skills,
        Ui = audio.Ui,
    };
    noWeaponCue.Effects.Remove(lostSound);
    run.True(Break(() => ContentValidation.ValidateAudio(noWeaponCue, items, world, battle, skills, wardrobe))
            .Contains("武器反馈音效缺失"),
        $"删掉武器音效 {lostSound} 会被拒");

    MapDefinition LoadMapFile(string id)
    {
        var entry = world.Maps.First(map => map.Id == id);
        return JsonSerializer.Deserialize<MapDefinition>(
            File.ReadAllText(Path.Combine(root, entry.File)), options)!;
    }

    failed = string.Empty;
    try { ContentValidation.ValidateMaps(world, LoadMapFile, battle, campaign); }
    catch (InvalidDataException error) { failed = error.Message; }
    run.Equal(string.Empty, failed, "真实内容通过地图与剧情引用校验");

    string BreakMap(string targetId, Action<MapDefinition> mutate) => Break(() => ContentValidation.ValidateMaps(
        world,
        id =>
        {
            var map = LoadMapFile(id);
            if (id == targetId) mutate(map);
            return map;
        },
        battle, campaign));

    var exitMap = world.Maps[0].Id;
    run.True(BreakMap(exitMap, map => map.Exits = [.. map.Exits, new MapExit { Destination = "ghost-map" }])
            .Contains("出口目标缺失"),
        "出口指向不存在的地图会被拒");

    var entityMap = world.Maps.First(entry => LoadMapFile(entry.Id).Entities.Length > 0).Id;
    var withActor = BreakMap(entityMap, map => map.Entities[0].Actor = "ghost-actor");
    run.True(withActor.Contains("人物缺失"), $"摆放引用不存在的人物会被拒（{withActor}）");

    var spawnMap = world.Maps.First(entry => LoadMapFile(entry.Id).EnemySpawns.Length >= 2).Id;
    var duplicated = BreakMap(spawnMap, map => map.EnemySpawns[1].Id = map.EnemySpawns[0].Id);
    run.True(duplicated.Contains("敌人出生点标识重复"), $"刷怪点标识重复会被拒（{duplicated}）");

    CampaignContent WithPlace(string json) => new()
    {
        Quests =
        [
            new QuestDefinition
            {
                Id = "quest-x",
                Places = JsonDocument.Parse(json).RootElement.Clone(),
            },
        ],
    };

    string BreakPlace(string json) => Break(() => ContentValidation.ValidateStoryPlaces(
        WithPlace(json),
        id => id == exitMap,
        id => id == exitMap ? (100, 100) : null));

    run.True(BreakPlace("""{"available":{"type":"fixed","map":"ghost-map","x":10,"y":10}}""")
            .Contains("地点地图缺失"),
        "地点指向不存在的地图会被拒");
    var farSpot = "{\"active\":{\"type\":\"alternatives\",\"spots\":[{\"map\":\"" + exitMap + "\",\"x\":500,\"y\":10}]}}";
    run.True(BreakPlace(farSpot).Contains("地点坐标超出地图"), "地点坐标超出地图会被拒");

    var goodSpot = "{\"ready\":{\"type\":\"fixed\",\"map\":\"" + exitMap + "\",\"x\":10,\"y\":10}}";
    run.Equal(string.Empty, BreakPlace(goodSpot), "合法地点通过");
});

run.Case("剧情引用与演出校验：真实内容全过，改坏一处能报出来", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var options = new JsonSerializerOptions { AllowTrailingCommas = true };
    SkillActionJson.Configure(options);
    T Read<T>(string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(root, "content", name)), options)!;

    var world = Read<WorldIndex>("world.json");
    var battle = Read<BattleContent>("battle.json");
    var items = Read<ItemCatalog>("items.json");
    var campaign = Read<CampaignContent>("campaign.json");
    var audio = Read<AudioCatalog>("audio.json");

    MapDefinition LoadMap(string id) =>
        JsonSerializer.Deserialize<MapDefinition>(
            File.ReadAllText(Path.Combine(root, world.Maps.First(map => map.Id == id).File)), options)!;

    string Fail(Action action)
    {
        try { action(); return string.Empty; }
        catch (InvalidDataException error) { return error.Message; }
    }

    var storyMaps = new Dictionary<string, MapDefinition>();
    foreach (var id in StoryValidation.ReferenceMaps(campaign)) storyMaps[id] = LoadMap(id);

    run.True(storyMaps.Count >= 260, $"剧情引用覆盖 {storyMaps.Count} 张图");
    run.Equal(string.Empty, Fail(() => StoryValidation.ValidateReferences(campaign, world, battle, storyMaps, items)),
        "真实内容通过剧情引用校验");
    StoryPerformanceValidation.StoryPerformanceReport? walked = null;
    run.Equal(string.Empty, Fail(() => walked = StoryPerformanceValidation.Validate(campaign, world, audio, storyMaps)),
        "真实内容通过演出校验（含落位与走位）");
    run.True(walked is { Nodes: >= 3000, Stages: >= 700, Walks: >= 900 },
        $"走查规模：节点 {walked?.Nodes}、舞台 {walked?.Stages}、走位 {walked?.Walks}");

    var map = storyMaps["shadow-camp"];
    JsonElement Item(string json) => JsonDocument.Parse(json).RootElement.Clone();

    var stage = Item("""
        {"type":"stage","map":"shadow-camp","x":100,"y":100,"facing":"south","playerVisible":true,
         "cast":[{"id":"a","actor":"shadow-soldier","memberId":"ally-1","x":120,"y":120,"visible":true}]}
        """);

    CampaignContent Mini(JsonElement[] presentation, string startActor = "shadow-soldier",
        string startMap = "shadow-camp", float startX = 40f) => new()
    {
        Start = new CampaignStart { Map = startMap, X = startX, Y = 40, Actor = startActor },
        Scripts = new Dictionary<string, StoryScript>
        {
            ["mini"] = new StoryScript
            {
                Entry = "n1",
                Nodes =
                [
                    new StoryNode
                    {
                        Id = "n1",
                        Presentation = presentation,
                        Exit = new StoryExit { Type = "end" },
                    },
                ],
            },
        },
    };

    var miniMaps = new Dictionary<string, MapDefinition> { ["shadow-camp"] = map };

    run.True(Fail(() => StoryPerformanceValidation.Validate(
                Mini([Item("""{"type":"dialogue","actor":"ghost-actor","text":"喂"}""")]), world, audio, miniMaps))
            .Contains("对白人物缺失 ghost-actor"),
        "对白人物不存在会被拒");

    run.True(Fail(() => StoryPerformanceValidation.Validate(
                Mini([Item("""{"type":"sound","sound":"ghost-sound"}""")]), world, audio, miniMaps))
            .Contains("音效缺失 ghost-sound"),
        "演出的音效不存在会被拒");

    run.True(Fail(() => StoryPerformanceValidation.Validate(
                Mini([Item("""{"type":"walk","target":"ghost","x":100,"y":100}""")]), world, audio, miniMaps))
            .Contains("人物未出场 ghost"),
        "还没登台就走位会被拒");

    var blockedIndex = Array.IndexOf(map.Blocked, true);
    var blockedX = blockedIndex % map.Columns * map.TileSize + 8f;
    var blockedY = blockedIndex / map.Columns * map.TileSize + 8f;
    var unreachable = Fail(() => StoryPerformanceValidation.Validate(
        Mini([stage, Item($$"""{"type":"walk","target":"a","x":{{blockedX}},"y":{{blockedY}}}""")]),
        world, audio, miniMaps));
    run.True(unreachable.Contains("道路不可达 a"), $"走进阻挡格会被拒（{unreachable}）");

    // 舞台所在的地图必须真的在目录里。
    run.True(Fail(() => StoryPerformanceValidation.Validate(
                Mini([Item("""{"type":"stage","map":"ghost-map","x":1,"y":1,"facing":"south","playerVisible":true}""")]),
                world, audio, miniMaps))
            .Contains("地图缺失 ghost-map"),
        "舞台指向不存在的地图会被拒");

    var ghostHero = Fail(() => StoryValidation.ValidateReferences(
        Mini([stage], startActor: "ghost-actor"), world, battle, miniMaps, items));
    run.True(ghostHero.Contains("主角不存在 ghost-actor"), $"主角不存在会被拒（{ghostHero}）");

    run.True(Fail(() => StoryValidation.ValidateReferences(Mini([stage], startX: 99999f), world, battle, miniMaps, items))
            .Contains("位置超出地图 start"),
        "起点越界会被拒");

    run.True(Fail(() => StoryValidation.ValidateReferences(campaign, world, battle,
                new Dictionary<string, MapDefinition>(), items))
            .Contains("地图不存在"),
        "剧情图没加载会被拒");

    var staged = Mini([stage]);
    run.True(StoryValidation.ReferenceMaps(staged).Contains("shadow-camp"), "舞台地图进剧情引用清单");
});

run.Case("技能音效取舍：自带音效的事件不再叠一次", () =>
{
    SkillAction Action(params SkillEvent[] events) => new()
    {
        CooldownFrames = 0,
        ApproachDistanceUnits = 0,
        SearchRadiusPx = 0,
        Events = [.. events],
    };

    ActionDispatchDefinition Dispatch() => new() { BreaksStealth = false };

    run.False(SkillSoundRules.PlaysOwnSound(Action()), "没有事件时由技能音效出声");
    run.False(SkillSoundRules.PlaysOwnSound(Action(new FeedbackEvent { Frame = 5 })),
        "feedback 不带 sound 不挡技能音效");
    run.True(SkillSoundRules.PlaysOwnSound(Action(new FeedbackEvent { Frame = 5, Sound = "hit" })),
        "feedback 带 sound 自带出声");
    run.True(SkillSoundRules.PlaysOwnSound(Action(new UnitsEvent { Frame = 3, Dispatch = Dispatch() })),
        "units 带 dispatch 自带出声");
    run.True(SkillSoundRules.PlaysOwnSound(Action(new SummonEvent
    {
        Frame = 3, Dispatch = Dispatch(), Effect = string.Empty,
        Placement = new SummonPlacement
        {
            SearchGapPx = 8, SearchExtentPx = 8, JitterPx = 0,
        },
    })), "summon 带 dispatch 自带出声");
    run.False(SkillSoundRules.PlaysOwnSound(Action(new UnitsEvent { Frame = 3 })),
        "units 没有 dispatch 不挡技能音效");
    run.True(SkillSoundRules.PlaysOwnSound(Action(new UnitsEvent
    {
        Frame = 3, Dispatch = new ActionDispatchDefinition { BreaksStealth = false, Sound = null },
    })), "dispatch 没写 sound 也算自带出声");
});

run.Case("对白分页：控制码零宽、按可见字切页、换行由分页承担", () =>
{
    float Measure(string text) => text.Length * 10f;

    var (visible, offsets) = DialoguePager.VisibleIndex("$A红字$B普通");
    run.Equal("红字普通", visible, "控制码不进可见文本");
    run.Equal(2, offsets[0], "首字在原文里的下标跳过 $A");
    run.Equal(8, offsets[^1], "末尾多一项指向原文长度");

    run.Equal("一二", DialoguePager.TypedIndex("一&N二").Text, "换行不计入逐字进度");
    run.Equal(2, DialoguePager.TypedLength("一&N二"), "逐字长度只数可见字符");

    var lines = DialoguePager.WrapText("一二三", 25f, Measure);
    run.Equal("一二|三", string.Join("|", lines), "2.5 个字宽只放得下两个字");

    var pages = DialoguePager.Pages("一二三四五六七", 30f, 1, Measure);
    run.Equal("一二三|四五六|七", string.Join("|", pages.Select(page => page.Text)),
        "三字一行、一行一页");

    var broken = DialoguePager.Pages("一二&N三四", 30f, 1, Measure);
    run.Equal("一二|三四", string.Join("|", broken.Select(page => page.Text)),
        "显式换行断页，符号本身不留在页面里");

    // ⑥ 页首控制码跟着这一页走（否则首段字色退回基础色）。
    // 页尾的控制码按原文下标切，会留在前一页末尾：`$B` 只是「结束本段颜色」，
    // 没有可见字符，跟着上一页走不影响显示。
    var coded = DialoguePager.Pages("$A一二三$B四", 30f, 1, Measure);
    run.Equal("$A一二三$B|四", string.Join("|", coded.Select(page => page.Text)),
        "页首控制码跟着这一页，页尾色段结束码留在前页");

    run.Equal(1, DialoguePager.Pages("", 30f, 3, Measure).Count, "空句仍给一页");

    var map = DialoguePager.TypedToVisibleMap("一二&N三四");
    run.Equal(5, map.Length, "映射长度 = 可打字字符数 + 1");
    run.Equal(3, map[2], "打完两个字时，那个换行已经显示出来了");
    run.Equal(5, map[^1], "打完末字时整页字符都在");
});

run.Case("状态图标：左下起往左排、最后 30 帧每 4 帧闪一次，皮肤段能解析", () =>
{
    var (x0, y0) = BuffIconRules.Position(24f, 27f, 48f, 0);
    run.Equal(-12f, x0, "第一枚骑在面板左缘上");
    run.Equal(24f, y0, "下边缘齐面板底边");
    run.Equal(-39f, BuffIconRules.Position(24f, 27f, 48f, 1).X, "第二枚再往左 27");
    run.Equal(-66f, BuffIconRules.Position(24f, 27f, 48f, 2).X, "第三枚继续往左");

    run.Equal(0.0, BuffIconRules.RemainingFrames(0, 60), "到点算 0 帧");
    run.Equal(1.0, BuffIconRules.RemainingFrames(60, 60), "一整帧");
    run.Equal(2.0, BuffIconRules.RemainingFrames(61, 60), "多一点也要算一帧");
    run.Equal(0.0, BuffIconRules.RemainingFrames(-100, 60), "负数按 0");

    run.True(!BuffIconRules.Blinking(31, 4), "还剩 31 帧不闪");
    run.True(!BuffIconRules.Blinking(30, 0), "窗口内第一个周期不隐藏");
    run.True(BuffIconRules.Blinking(30, 4), "第二个周期隐藏");
    run.True(!BuffIconRules.Blinking(30, 8), "第三个周期又不隐藏");
    run.True(BuffIconRules.Blinking(10, 12), "第四周期隐藏");

    var root = FindContentRoot();
    if (root is null) return;

    var skin = JsonSerializer.Deserialize<StatusHudSkin>(
        File.ReadAllText(Path.Combine(root, "content", "status-hud.json")), SaveGameStore.JsonOptions)!;
    run.Equal(24f, skin.BuffIcons.Size, "图标边长 24");
    run.Equal(27f, skin.BuffIcons.Step, "图标间距 27");
    run.Equal(35, skin.BuffIcons.Catalog.Count, "35 个状态图标");
    run.True(skin.BuffIcons.Catalog.ContainsKey("aimed-shot"), "认得出 aimed-shot 的图标");
});

run.Case("地名条底衬：贴着文字收窄、下限 120 上限 480、名条里居中", () =>
{
    run.Equal(158f, NameBannerRules.BackdropWidth(126f), "七字地名 126 宽 → 158");
    run.Equal(120f, NameBannerRules.BackdropWidth(0f), "没字也留一条最短的");
    run.Equal(120f, NameBannerRules.BackdropWidth(40f), "两三个字也顶到下限");
    run.Equal(480f, NameBannerRules.BackdropWidth(448f), "刚好到上限");
    run.Equal(480f, NameBannerRules.BackdropWidth(2000f), "超长名字不再加宽");

    run.Equal(161f, NameBannerRules.BackdropLeft(126f), "158 宽的底衬居中在 161");
    run.Equal(0f, NameBannerRules.BackdropLeft(2000f), "顶到上限时贴齐左缘");
    run.Equal(180f, NameBannerRules.BackdropLeft(0f), "最短的底衬也居中");

    run.Equal(3f, NameBannerRules.BackdropTop(34f), "28 高在 34 高的名条里上边距 3");
});

run.Case("选项面板：底边 620、顶边 20，装不下时从列表里扣，居中也不越界", () =>
{
    run.Equal(300f, ChoiceGeometry.PanelHeight(300f), "短面板按内容");
    run.Equal(600f, ChoiceGeometry.PanelHeight(600f), "刚好到上限");
    run.Equal(600f, ChoiceGeometry.PanelHeight(900f), "超长也压到上限");

    run.Equal(320f, ChoiceGeometry.Top(300f), "300 高的面板顶边在 320");
    run.Equal(20f, ChoiceGeometry.Top(900f), "顶到上限时顶边就是 20");

    run.Equal(272f, ChoiceGeometry.ListViewport(400f, 272f, 34f), "八行放得下就全放");
    run.Equal(120f, ChoiceGeometry.ListViewport(120f, 272f, 34f), "剩余空间不够就吃剩余");
    run.Equal(34f, ChoiceGeometry.ListViewport(5f, 272f, 34f), "至少留一行");

    run.Equal(170f, ChoiceGeometry.CenteredTop(300f, 640f), "300 高居中在 170");
    run.Equal(20f, ChoiceGeometry.CenteredTop(900f, 640f), "超高时顶边被夹到 20");
    run.Equal(20f, ChoiceGeometry.CenteredTop(640f, 640f), "正好满高时也从 20 起");
});

run.Case("屏幕即时提示：1.8 秒、三成屏高居中、末四帧按台阶淡出", () =>
{
    run.Equal(1800, InstantMessages.DurationMs, "30 帧 × 60 毫秒");

    var (x, y) = InstantMessages.Point(960f, 640f);
    run.Equal(480f, x, "960 宽的中点");
    run.Equal(192f, y, "640 高的三成");
    run.Equal(240f, InstantMessages.Point(480f, 480f).X, "窄屏也居中");

    run.Equal(1f, InstantMessages.Alpha(1800), "刚出现时全不透明");
    run.Equal(1f, InstantMessages.Alpha(240), "还剩 4 帧仍全不透明");
    run.Equal(0.75f, InstantMessages.Alpha(180), "3 帧 → 0.75");
    run.Equal(0.5f, InstantMessages.Alpha(120), "2 帧 → 0.5");
    run.Equal(0.25f, InstantMessages.Alpha(60), "1 帧 → 0.25");
    run.Equal(0f, InstantMessages.Alpha(0), "到点不画");
    run.Equal(0.25f, InstantMessages.Alpha(1), "多一点点也算 1 帧");
});

run.Case("拾取飘字：节奏、行距、右缘翻面与稀有度颜色", () =>
{
    run.Equal(32, PickupMessageRules.Limit, "等待 + 在演最多 32 条");
    run.Equal(120, PickupMessageRules.SpacingMs, "相邻两条错开 120ms");
    run.Equal(2400, PickupMessageRules.TotalMs, "停 1440 + 飘 960");

    run.Equal(0f, PickupMessageRules.LiftAt(0), "刚出现不上飘");
    run.Equal(0f, PickupMessageRules.LiftAt(1440), "停留期结束才开始飘");
    run.Near(8f, PickupMessageRules.LiftAt(1440 + 480), 1e-4f, "上飘半程 8px");
    run.Equal(16f, PickupMessageRules.LiftAt(2400), "到寿飘满 16px");
    run.Equal(16f, PickupMessageRules.LiftAt(9999), "超时不无限飘");

    run.Equal(0, PickupMessageRules.RowFor([]), "没人占用用第 0 行");
    run.Equal(2, PickupMessageRules.RowFor([0, 1, 3]), "跳过被占的行");

    run.Equal(120f, PickupMessageRules.AnchorX(100f, 0f, 320f, 0f), "常规在锚点右侧 +20");
    run.Equal(161f, PickupMessageRules.AnchorX(260f, 0f, 320f, 0f), "260 距右缘 60 < 100 → 翻到左侧 −99");
    run.Equal(130f, PickupMessageRules.AnchorX(100f, 0f, 320f, 10f), "头顶锚点也计入偏移");
    run.Equal(201f, PickupMessageRules.AnchorX(300f, 0f, 320f, 0f), "300 距右缘 20 → 翻面 201");

    run.Equal(-37f, PickupMessageRules.AnchorY(0f, 0f, 0, 0f), "基线 −37");
    run.Equal(-13f, PickupMessageRules.AnchorY(0f, 0f, 2, 0f), "第 2 行往下 24");

    run.True(PickupMessageRules.ItemNameColor(null) is null, "无稀有度用基础色");
    run.True(PickupMessageRules.ItemNameColor(0) is null, "0 号也用基础色");
    run.Equal("#4eff00", PickupMessageRules.ItemNameColor(1), "1 号绿");
    run.Equal("#00b4ff", PickupMessageRules.ItemNameColor(2), "2 号蓝");
    run.Equal("#ffe600", PickupMessageRules.ItemNameColor(3), "3 号黄");
    run.Equal("#9c00ff", PickupMessageRules.ItemNameColor(4), "4 号紫");
    run.True(PickupMessageRules.ItemNameColor(9) is null, "超出表也回基础色");
});

run.Case("键位偏好：任一条不合法就整份弃用", () =>
{
    var store = new Dictionary<string, string>(StringComparer.Ordinal);
    string Read(string key) => store.TryGetValue(key, out var text) ? text : string.Empty;
    void Write(string key, string text) => store[key] = text;

    store["control-layout.json"] =
        "{\"version\":1,\"positions\":{\"attack\":{\"x\":0.5,\"y\":0.25},\"swap\":{\"x\":0.7,\"y\":0.1}}}";
    var good = new ControlLayoutPreferences(Read, Write);
    run.Equal(2, good.Snapshot.Count, "两条都读回来");
    run.Near(0.5f, good.Snapshot["attack"].X, 1e-6f, "普攻的横坐标");

    store["control-layout.json"] =
        "{\"version\":1,\"positions\":{\"attack\":{\"x\":0.5,\"y\":0.25},\"swap\":{\"x\":1.4,\"y\":0.1}}}";
    var outOfRange = new ControlLayoutPreferences(Read, Write);
    run.Equal(0, outOfRange.Snapshot.Count, "越界一条就整份丢");

    store["control-layout.json"] =
        "{\"version\":1,\"positions\":{\"attack\":{\"x\":0.5,\"y\":0.25},\"jump\":{\"x\":0.2,\"y\":0.2}}}";
    var unknownId = new ControlLayoutPreferences(Read, Write);
    run.Equal(0, unknownId.Snapshot.Count, "陌生 id 也整份丢");

    store["control-layout.json"] = "{\"version\":2,\"positions\":{\"attack\":{\"x\":0.5,\"y\":0.25}}}";
    var wrongVersion = new ControlLayoutPreferences(Read, Write);
    run.Equal(0, wrongVersion.Snapshot.Count, "版本不符整份丢");
});

run.Case("演出镜头：按距离算滚动时长，短距 12 像素／帧、长距按十分之一", () =>
{
    run.Equal(0, StoryCamera.DurationMs(0), "原地不动不用滚");
    run.Equal(0, StoryCamera.DurationMs(0.009), "不足 0.01 也算不用滚");
    run.Equal(60, StoryCamera.DurationMs(6), "半帧的活也按一帧算");
    run.Equal(60, StoryCamera.DurationMs(12), "一帧 12 像素");
    run.Equal(600, StoryCamera.DurationMs(120), "刚好 120：10 帧");
    run.Equal(600, StoryCamera.DurationMs(240), "240：步长 24，仍是 10 帧");
    run.Equal(660, StoryCamera.DurationMs(121), "刚过 120：步长 12，向上取整成 11 帧");
    run.Equal(600, StoryCamera.DurationMs(600), "600：步长 60，10 帧");
});

run.Case("头顶双条：槽宽按公式取整，皮肤里的 overhead 段能解析", () =>
{
    run.Equal(8f, GaugeRules.Width(5, 10, 16f), "半血填一半");
    run.Equal(16f, GaugeRules.Width(12, 10, 16f), "超过上限按满格算");
    run.Equal(0f, GaugeRules.Width(-3, 10, 16f), "负血量按空槽");
    run.Equal(0f, GaugeRules.Width(5, 0, 16f), "上限为零时整条为空");
    run.Equal(5f, GaugeRules.Width(1, 3, 16f), "5.33 向下取整成 5，贴图交界不落在小数上");

    var root = FindContentRoot();
    if (root is null) return;

    var skin = JsonSerializer.Deserialize<StatusHudSkin>(
        File.ReadAllText(Path.Combine(root, "content", "status-hud.json")), SaveGameStore.JsonOptions)!;
    run.Equal(18f, skin.Overhead.Frame.Width, "底框宽 18");
    run.Equal(-9f, skin.Overhead.Frame.X, "底框以锚点为中（x = -9）");
    run.Equal(-16f, skin.Overhead.Leader.X, "队长标记露在底框左侧外面");
    run.Equal(2f, skin.Overhead.Health.Height, "血条高 2");
    run.Equal("#28c000", skin.Overhead.Health.Color, "血条绿");
    run.Equal("#0f7cf9", skin.Overhead.Mana.Color, "蓝条蓝");

    foreach (var key in new[] { "overhead-frame", "overhead-leader" })
    {
        var path = skin.Assets.GetValueOrDefault(key);
        run.True(path is not null && File.Exists(Path.Combine(root, path)),
            $"{key} 的图要在 assets 里真的有");
    }
});

run.Case("普攻连打：站定才接下一击，摇杆一动或目标没了就停", () =>
{
    var repeat = new BasicAttackRepeat();
    var idle = CombatState.Idle;
    var attacking = CombatState.Attacking;
    var approaching = CombatState.Approaching;

    run.False(repeat.Next("enemy-1", idle), "没 engage 时不该自动出手");
    run.False(repeat.Active, "没 engage 时门是关的");

    repeat.Engage();
    run.True(repeat.Next("enemy-1", idle), "站定且目标还在，接下一击");
    run.False(repeat.Next("enemy-1", attacking), "动作没回到 idle 就不接");

    run.False(repeat.Next("enemy-1", approaching), "追近途中不接");

    repeat.Release();
    repeat.Engage();
    run.False(repeat.Next(null, idle), "目标没了就不再接");
    run.False(repeat.Next("enemy-1", idle), "目标没了会自己解除，不会自己恢复");
    run.False(repeat.Active, "解除之后门是关的");

    repeat.Engage();
    repeat.Release();
    run.False(repeat.Next("enemy-1", idle), "摇杆一动之后不再连打");

    repeat.Engage();
    run.False(repeat.Next("", idle), "空串目标按没有目标处理");
});

run.Case("NPC 功能表：51 条功能，npc-39 绑的是拔剑", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    using var document = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(root, "content", "npc-functions.json")));
    var catalog = NpcFunctionCatalog.Parse(document.RootElement);

    run.Equal(51, catalog.Functions.Length, "功能表条目数");
    run.Equal("试图拔出插在地上的剑！", catalog.Of(44)!.Name, "功能 44 的名字");
    run.Equal(2807, catalog.Of(44)!.TextId, "功能 44 的文本 id");

    var sword = catalog.ActionOf("npc-39");
    run.Equal(44, sword?.Index ?? -1, "npc-39 走拔剑");
    run.Equal(NpcFunctionBehavior.PullSword, sword?.Behavior?.Kind);
    run.Equal("black-sword", NpcFunctionCatalog.RewardFor(sword!, "dark-knight"), "骑士拿长剑");
    run.Equal("black-staff", NpcFunctionCatalog.RewardFor(sword!, "warlock"), "黑魔导拿法杖");
    run.Null(NpcFunctionCatalog.RewardFor(sword!, "no-such-class"), "没配的职业没有奖励");

    // 商人、融合器、宝箱那些功能都有别的入口，不能在这里抢走。
    run.Null(catalog.ActionOf("npc-0"), "武器商人不是功能交互");
    run.Null(catalog.ActionOf("npc-428"), "融合器不是功能交互");
    run.Null(catalog.ActionOf("npc-9999"), "不存在的 NPC");

    run.Equal(2, catalog.FunctionsOf("npc-17").Count, "npc-17 有两条功能");
});

run.Case("NPC 功能表：没有实现的行为在加载时报错", () =>
{
    const string json = """
        {"version":1,
         "functions":[{"index":0,"textId":1,"name":"试试","params":[0,0,1]}],
         "npcs":{"npc-1":[0]},
         "behaviors":{"0":{"kind":"teleport"}}}
        """;
    var thrown = false;
    try { NpcFunctionCatalog.Parse(JsonDocument.Parse(json).RootElement); }
    catch (InvalidOperationException) { thrown = true; }
    run.True(thrown, "未知行为种类被拦下");
});

run.Case("一次性摆放：拔过的剑记住，存档往返不丢", () =>
{
    var props = new PropVisits();
    run.True(props.Consume("delos-cave", "sword-001"), "第一次消耗");
    run.False(props.Consume("delos-cave", "sword-001"), "同一把剑不会再消耗一次");
    run.True(props.IsConsumed("delos-cave", "sword-001"));
    run.False(props.IsConsumed("delos-cave", "sword-002"), "别的摆放不受影响");
    run.False(props.IsConsumed("other-map", "sword-001"), "换图按新摆放算");

    var restored = new PropVisits();
    restored.RestoreState(props.CaptureState());
    run.True(restored.IsConsumed("delos-cave", "sword-001"), "读档后仍然算拔过");
    run.Equal(1, restored.Consumed.Count);
});

run.Case("旅馆休息与灵魂治疗：回满、复活都只改队伍状态", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var companions = JsonSerializer.Deserialize<CompanionCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "companions.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var party = new PartyRoster(hero, stats, items, companions, () => { });

    hero.SetHealth(1);
    hero.SetMana(0);
    run.True(party.RestAll() > 0, "有需要恢复的人");
    run.Equal(hero.Player.MaxHealth, hero.Player.Health, "主角回满");
    run.Equal(hero.Player.MaxMana, hero.Player.Mana, "魔力回满");
    run.Equal(0, party.RestAll(), "都满了再休息没有变化");

    var story = companions.Definitions.Values.First(definition => definition.SourceOrDefault == "story");
    var member = party.JoinStory(story.Id);
    member.SetHealth(0);
    run.False(party.Alive(member.Id), "同伴倒下");
    run.Equal(1, party.ReviveFallen(50), "复活一名同伴");
    run.True(party.Alive(member.Id), "活过来了");
    run.Equal((int)Math.Floor(member.Player.MaxHealth * 0.5), member.Player.Health, "按比例回血");
    run.Equal(0, party.ReviveFallen(50), "没有倒下的人就什么都不做");
});

run.Case("招募事实：用徽章招到人，recruit 目标就记数（勇士徽章那条主线）", () =>
{
    var root = FindContentRoot();
    if (root is null) { Console.WriteLine("      跳过：没找到 public/content"); return; }

    const string json = """
    {
      "scripts": {},
      "quests": [
        { "id": "q-recruit", "title": "勇士徽章", "completion": "all",
          "goals": [ { "id": "objective", "kind": "recruit",
                       "target": "shadow-mercenary-emblem", "quantity": 1 } ] }
      ]
    }
    """;

    var campaign = JsonSerializer.Deserialize<CampaignContent>(json)!;
    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var companions = JsonSerializer.Deserialize<CompanionCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "companions.json")))!;
    var hero = new BattleCharacter("dark-knight", stats, items);
    var journal = new QuestJournal(campaign) { Bag = hero.Bag };
    var tracker = new QuestTracker(campaign, journal);
    var party = new PartyRoster(hero, stats, items, companions, () => { },
        onRecruited: (itemId, _) => tracker.ReportRecruit(itemId));

    journal.Accept("q-recruit", "script");
    hero.Bag.Add("shadow-mercenary-emblem");
    var emblem = hero.Bag.Entries.Last(entry => entry.ItemId == "shadow-mercenary-emblem");
    run.Equal(0, tracker.CountOf("q-recruit", "objective"), "还没招人，目标不该动");

    var (member, _) = party.Recruit(emblem.Id, () => 0.5);

    run.Equal(1, tracker.CountOf("q-recruit", "objective"), "招到人记一次");
    run.Equal(QuestStatus.Ready, journal.StatusOf("q-recruit"), "任务转可交付");
    run.Equal(0, hero.Bag.Count("shadow-mercenary-emblem"), "徽章用掉了");
    run.True(party.Get(member.Id) is not null, "人进了名册");
});

run.Case("标题页动画：六层的阈值与增量照反汇编，A/C 只涨不落、其余四层来回", () =>
{
    // 依据 SO_REVERSE.md §19.2：六块的阈值 0x3B/0x31/0x3B/0x1D/0x31/0x27。
    // A、C 两块的 mvneq/movne 立即数是 #1/#2（都是正的，只涨不落，锯齿）；
    // B/D/E/F 是 #-1/#1（来回摆）。把 A/C 当成「涨 2 落 1」是解错了语义。
    var animation = new TitleAnimation();
    run.Equal(6, animation.Layers.Count, "六层振荡");

    int[] thresholds = [59, 49, 59, 29, 49, 39];
    bool[] alwaysRise = [true, false, true, false, false, false];
    for (var i = 0; i < 6; i++)
    {
        run.Equal(thresholds[i], animation.Layers[i].Threshold, $"第 {i} 层阈值");
        run.Equal(alwaysRise[i], animation.Layers[i].AlwaysRise, $"第 {i} 层是否只涨不落");
    }

    run.Equal(1, animation.Layers[0].StepWhenZero, "A 方向 0 加 1");
    run.Equal(2, animation.Layers[0].StepWhenOne, "A 方向 1 加 2");
    run.Equal(-1, animation.Layers[1].StepWhenZero, "B 方向 0 减 1");
    run.Equal(1, animation.Layers[1].StepWhenOne, "B 方向 1 加 1");

    // 初值（0xD7A28–0xD7A60 的置数段）：透明度 60/50/60/30/50/40、计步 0/90/90/180/0/180、
    // 六个方向字节都置 1。早先这一列被读成了计步初值、透明度全写 0，六层就会同步明灭。
    int[] opacities = [60, 50, 60, 30, 50, 40];
    int[] steps = [0, 90, 90, 180, 0, 180];
    for (var i = 0; i < 6; i++)
    {
        run.Equal(opacities[i], animation.Layers[i].Opacity, $"第 {i} 层初始透明度");
        run.Equal(steps[i], animation.Layers[i].Step, $"第 {i} 层初始计步");
        run.Equal(1, animation.Layers[i].Direction, $"第 {i} 层初始方向");
    }
});

run.Case("标题页动画：透明度撞到 100 清零并把方向置 0", () =>
{
    var atTop = new TitleLayer(100, 0, 1, 29, -1, 1, false);
    atTop.Advance();
    run.Equal(0, atTop.Opacity, "在 100 上再加 1 就归零");
    run.Equal(0, atTop.Direction, "归零后方向置 0");

    // 只涨不落的那两层：从 0 一路涨到 100 附近再清零，中途不该回落。
    var zigzag = new TitleLayer(0, 0, 0, 59, 1, 2, true);
    var previous = 0;
    var sawReset = false;
    for (var frame = 0; frame < 200; frame++)
    {
        zigzag.Advance();
        if (zigzag.Opacity == 0 && previous > 50) sawReset = true;
        run.True(zigzag.Opacity >= previous || zigzag.Opacity == 0, "只涨不落的层不该中途回落");
        previous = zigzag.Opacity;
    }
    run.True(sawReset, "涨到顶后应当出现过清零");
});

run.Case("标题页动画：掉到阈值以下把方向置 1，alpha 是 100 减透明度", () =>
{
    var layer = new TitleLayer(31, 0, 0, 29, -1, 1, false);
    layer.Advance();
    run.Equal(30, layer.Opacity, "方向 0 减一格");
    run.Equal(0, layer.Direction, "还没到阈值，方向不变");
    layer.Advance();
    run.Equal(29, layer.Opacity, "到阈值");
    run.Equal(1, layer.Direction, "到阈值就置 1");

    run.Near(1f, new TitleLayer(0, 0, 0, 29, -1, 1, false).Alpha, 1e-6f, "透明度 0 → alpha 1");
    run.Near(0f, new TitleLayer(100, 0, 0, 29, -1, 1, false).Alpha, 1e-6f, "透明度 100 → alpha 0");
});

run.Case("标题页动画：初相位把六层错开，同一帧里透明度互不相同", () =>
{
    var animation = new TitleAnimation();
    var start = animation.Layers.Select(layer => layer.Opacity).ToArray();
    run.True(start.Distinct().Count() > 1, "初始透明度应当互不相同");

    // 跑一阵之后六层的透明度不该还完全同步——否则画面上就是六团一起明灭。
    for (var frame = 0; frame < 30; frame++) animation.Advance();
    var now = animation.Layers.Select(layer => layer.Opacity).ToArray();
    run.True(now.Distinct().Count() > 1, "推进 30 帧后六层仍然错开");

    for (var frame = 0; frame < 360; frame++) animation.Advance();
    foreach (var layer in animation.Layers)
        run.True(layer.Step is >= 0 and < TitleLayer.StepPeriod, "计步器留在 0..359");
});

return run.Report();

/// <summary>被动修正的比对口径必须和基准脚本一致：只取该运算种类真正用到的操作数。</summary>
static string DescribeModifier(AttributeModifier modifier)
{
    var operation = modifier.Operation;
    var value = operation.Kind switch
    {
        "multiply" => operation.Factor,
        "divide" => operation.Divisor,
        "keep" => 0,
        _ when operation.Kind.EndsWith("-thousandth") => operation.RatePerThousand,
        _ => operation.Amount,
    };
    return $"{modifier.Attribute}:{operation.Kind}:{value}";
}

static BattleBuffEffect MakeBuff(int durationMs, int maxStacks = 1, int tickMs = 0,
    List<BuffPeriodicEffect>? periodic = null, List<AttributeModifier>? modifiers = null,
    List<string>? states = null, BuffSimulationFrames? frames = null, List<BuffLoopEffect>? loops = null) => new()
{
    DurationMs = durationMs,
    TickMs = tickMs,
    MaxStacks = maxStacks,
    Periodic = periodic ?? [],
    Modifiers = modifiers ?? [],
    States = states ?? [],
    SimulationFrames = frames,
    LoopEffects = loops,
};

static string DescribeSkillEvent(SkillEvent skillEvent) => skillEvent switch
{
    UnitsEvent units => $"units:{units.Cues.Count}@{units.Frame}",
    SummonEvent summon => $"summon:{string.Join(",", summon.Creatures.Select(creature => creature.EnemyId))}@{summon.Frame}",
    FeedbackEvent feedback => $"feedback@{feedback.Frame}",
    CaptureTargetsEvent capture => $"capture-targets@{capture.Frame}",
    CapturedEffectEvent captured => $"captured-effect@{captured.Frame}",
    CastEvent cast => $"cast@{cast.Frame}",
    CasterEvent caster => $"caster@{caster.Frame}",
    SwingEvent swing => $"swing@{swing.Frame}",
    ImpactEvent impact => $"impact@{impact.Frame}",
    SpawnEvent spawn => $"spawn@{spawn.Frame}",
    ScreenEvent screen => $"screen@{screen.Frame}",
    _ => throw new Exception($"未知事件：{skillEvent.GetType().Name}"),
};

static string Join(JsonElement array) =>
    string.Join(",", array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number
        ? item.GetRawText()
        : item.GetString()));

static float PathLength(Vec2 start, IReadOnlyList<Vec2> path)
{
    var total = 0f;
    var previous = start;
    foreach (var point in path)
    {
        total += GoalQuery.Distance(previous, point);
        previous = point;
    }

    return total;
}

static CollisionGrid Grid(string[] rows, int tileSize = 16)
{
    var columns = rows[0].Length;
    var blocked = new List<bool>(rows.Length * columns);
    foreach (var row in rows)
    {
        foreach (var cell in row) blocked.Add(cell == '#');
    }

    return new CollisionGrid(new MapDefinition
    {
        TileSize = tileSize,
        Columns = columns,
        Rows = rows.Length,
        Width = columns * tileSize,
        Height = rows.Length * tileSize,
        Blocked = blocked.ToArray(),
    });
}

static string? FindContentRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "public", "content", "world.json");
        if (File.Exists(candidate)) return Path.Combine(directory.FullName, "public");
        directory = directory.Parent;
    }

    return null;
}

static (ShopCatalog Catalog, ItemCatalog Items, CharacterStatsCatalog Stats) ReadShops(string root){
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "content", "shops.json")));
    return (ShopCatalog.Parse(document.RootElement, stats), items, stats);
}

static CharacterStatsCatalog ReadStats(string root) =>
    JsonSerializer.Deserialize<CharacterStatsCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "character-stats.json")))!;

static SkillCatalog ReadSkills(string root)
{
    var catalog = JsonSerializer.Deserialize<SkillCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;
    catalog.Prepare();
    return catalog;
}

static BattleContent ReadBattle(string root) =>
    JsonSerializer.Deserialize<BattleContent>(
        File.ReadAllText(Path.Combine(root, "content", "battle.json")), SkillActionJson.Options)!;

static (BattleCharacter Hero, PartyRoster Party) ReadParty(string root, string classId)
{
    var stats = ReadStats(root);
    var items = JsonSerializer.Deserialize<ItemCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "items.json")))!;
    var companions = JsonSerializer.Deserialize<CompanionCatalog>(
        File.ReadAllText(Path.Combine(root, "content", "companions.json")))!;
    var hero = new BattleCharacter(classId, stats, items);
    return (hero, new PartyRoster(hero, stats, items, companions, () => { }));
}

static ShopOffer FakeOffer(string id) => new()
{
    Id = id,
    Instance = new ItemInstance { Id = id, ItemId = "healing-potion-s" },
    Limited = true,
    Concealed = false,
};

sealed class WorldStateSink(StoryWorldState world) : IWorldCommandSink
{
    public readonly List<StoryWorldCommand> Commands = [];

    public void Apply(StoryWorldCommand command)
    {
        Commands.Add(command);
        world.Apply(command);
    }

    public void DropItem(string map, float x, float y, string item, int quantity) { }
}

sealed class LcgRandom(uint seed = 12345u)
{
    private uint _state = seed;

    public double Next()
    {
        _state = unchecked(_state * 1664525u + 1013904223u);
        return _state / 4294967296.0;
    }
}

sealed class Harness
{
    private int _passed;
    private int _failed;

    public void Case(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  ok   {name}");
        }
        catch (Exception error)
        {
            _failed++;
            Console.WriteLine($"  FAIL {name}");
            Console.WriteLine($"       {error.Message}");
        }
    }

    public void True(bool value, string message = "期望为真")
    {
        if (!value) throw new Exception(message);
    }

    public void False(bool value, string message = "期望为假")
    {
        if (value) throw new Exception(message);
    }

    public void Null(object? value, string message = "期望为空")
    {
        if (value is not null) throw new Exception(message);
    }

    public void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message ?? $"期望 {expected}，实际 {actual}");
    }

    public void Throws(Action action, string expectedMessagePart, string? message = null)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            if (!error.Message.Contains(expectedMessagePart))
                throw new Exception(message ??
                    $"期望错误包含「{expectedMessagePart}」，实际「{error.Message}」");
            return;
        }

        throw new Exception(message ?? $"期望抛出「{expectedMessagePart}」，但没有抛错");
    }

    public void Near(float expected, float actual, float tolerance, string? message = null)
    {
        if (MathF.Abs(expected - actual) > tolerance)
            throw new Exception(message ?? $"期望 {expected}±{tolerance}，实际 {actual}");
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }
}

sealed class WorldCommandSpy(
    List<WorldPlace> placed,
    List<(string, string?, bool)> removed,
    List<(string, string, string?, bool)> visibilities,
    List<(string, float, float)> travels) : IWorldCommandSink
{
    public void Apply(StoryWorldCommand command)
    {
        switch (command)
        {
            case PlaceCommand place when place.Entity is { } entity:
                placed.Add(new WorldPlace(entity.Id, entity.Map!, entity.Actor!, entity.CharacterId,
                    entity.X ?? 0f, entity.Y ?? 0f, entity.Facing, entity.Visible != false,
                    place.VisitScope ? "visit" : null));
                break;
            case RemoveCharacterCommand remove:
                removed.Add((remove.Map, remove.CharacterId, remove.All));
                break;
            case ActorVisibilityCommand visibility:
                visibilities.Add((visibility.Map, visibility.Actor, visibility.CharacterId, visibility.Visible));
                break;
            case TravelCommand travel:
                travels.Add((travel.Destination.Map, travel.Destination.X, travel.Destination.Y));
                break;
        }
    }

    public void DropItem(string map, float x, float y, string item, int quantity) =>
        drops.Add((map, x, y, item, quantity));

    public readonly List<(string Map, float X, float Y, string Item, int Quantity)> drops = [];
}

sealed class MemorySaveStorage : ISaveStorage
{
    public readonly Dictionary<string, string> Items = new();

    public string? GetItem(string key) => Items.TryGetValue(key, out var value) ? value : null;

    public void SetItem(string key, string value) => Items[key] = value;

    public void RemoveItem(string key) => Items.Remove(key);
}

sealed class MemoryStorage
{
    private readonly Dictionary<string, string> _items = [];

    public string? Read(string key) => _items.TryGetValue(key, out var value) ? value : null;

    public void Write(string key, string value) => _items[key] = value;

    public void Set(string key, string value) => _items[key] = value;
}

sealed class FakeAutoSavePorts : IAutoSavePorts
{
    public int Writes;
    public bool EnabledOn = true;
    public bool LockedOn;
    public SaveGame? CaptureResult = new SaveGame { Format = SaveGameStore.SaveFormat };

    public SaveGame? Capture() => CaptureResult;

    public void Write(SaveGame game) => Writes += 1;

    public bool Enabled() => EnabledOn;

    public bool Locked() => LockedOn;
}

sealed class TestThreatPort : ICombatThreatPort
{
    private readonly Dictionary<string, CombatThreatActor> _actors = [];
    private readonly CombatThreatSystem _system;
    public long Tick { get; set; }

    public TestThreatPort() => _system = new CombatThreatSystem(this);

    public CombatThreatSystem System => _system;

    public void Add(
        string id,
        string team,
        int level = 1,
        CombatThreatRole? role = null,
        bool active = true,
        Vec2? position = null)
    {
        _actors[id] = new CombatThreatActor
        {
            Id = id,
            Team = team,
            Position = position ?? new Vec2(0, 0),
            Bounds = new CollisionBox(),
            Active = active,
            Level = level,
            SightRangeUnits = 12,
            TracksThreat = team == "enemy",
            Role = role,
        };
    }

    public int? LedgerScore(string ownerId, string sourceId) =>
        _system.CaptureState().Entries
            .FirstOrDefault(entry => entry.OwnerId == ownerId && entry.SourceId == sourceId)?.Score;

    public CombatThreatActor? Actor(string id) => _actors.GetValueOrDefault(id);

    public IReadOnlyList<CombatThreatActor> Actors() => [.. _actors.Values];

    public string? CurrentTarget(string ownerId) => null;

    public long LogicTick() => Tick;

    public int GenerationRate(string sourceId) => 1000;

    public int ReductionRate(string sourceId) => 0;

    public (int Physical, int Magic) NormalDamage(string sourceId) => (100, 0);

    public string? TauntSource(string ownerId) => null;

    public void ApplyTaunt(string sourceId, string targetId)
    {
    }

    public void CombatStarted(string ownerId)
    {
    }
}

sealed class TestEnemyThreat : IEnemyThreatPort
{
    private readonly ThreatLedger _ledger = new();

    public void Add(string source, int score) => _ledger.Add("self", source, score, 0);

    public void Clear() => _ledger.ClearOwner("self");

    public string? Select(string? current) => _ledger.Select("self", current, _ => true);

    public void Notice(string source) => _ledger.Add("self", source, 1, 0);

    public bool Has(string source) => _ledger.Score("self", source) is not null;
}

sealed class TestEnemyPort : IEnemyPort
{
    public TestEnemyPort(string id, Vec2 self, Vec2 target)
    {
        Self = new Combatant(id, "enemy", self, new CollisionBox(), "south");
        Target = new Combatant("player", "player", target, new CollisionBox(), "south");
    }

    public Combatant Self;
    public Combatant Target;
    public string? TargetId;
    public (int Current, int Maximum)? HealthValue;
    public Vec2 HomePoint;
    public Func<double> Random = () => 0.5;

    public IEnemyThreatPort? ThreatPort;
    public IEnemyThreatPort? Threat => ThreatPort;

    public int Moves;
    public int Followed;
    public int Stops;
    public string FacingValue = "south";
    public Action<int>? PendingFrame;
    public Action? PendingComplete;

    public Combatant? Actor(string id) =>
        id == Self.Id ? Self : id == Target.Id ? Target : null;

    public IReadOnlyList<Combatant> Candidates() => [Target];

    public string Facing() => FacingValue;

    public void MoveToward(Vec2 position) => Moves += 1;

    public void Stop() => Stops += 1;

    public bool CanStand(Vec2 position, string? clearanceActorId = null) => true;

    public Action Play(CombatAction action, Action<int> frame, Action complete)
    {
        PendingFrame = frame;
        PendingComplete = complete;
        FacingValue = action.Facing;
        return () => { };
    }

    public void Frame(CombatAction action, int index, Action<string, string?> contact,
        SkillUnitsReady unitsReady, Action? areaContact = null)
    {
        _ = unitsReady;
        if (action.Skill is not null || action.BasicAttack is null) return;
        foreach (var hand in BasicAttackRules.StrikesAtFrame(action.BasicAttack, index))
            if (action.Target is { } target) contact(target.Id, hand);
    }

    public void Contact(AttackContact contact)
    {
    }

    public void FollowPath(Func<Vec2?> nextWaypoint)
    {
        Followed += 1;
        nextWaypoint();
    }

    public void FinishPlayback()
    {
        PendingComplete?.Invoke();
        PendingComplete = null;
    }

    public Vec2 Home() => HomePoint;

    public (int Current, int Maximum)? Health(string id) => HealthValue;

    public Rect NavigationBounds() => new(0, 0, 1024, 1024);

    public void Face(string facing) => FacingValue = facing;
}

sealed class TestCombatPort : ICombatPort
{
    public Combatant Caster = new("caster", "enemy", new Vec2(0, 0), new CollisionBox());
    public Combatant? Target;
    public string PlayedFacing = string.Empty;
    public Action<int>? PendingFrame;
    public Action? PendingComplete;
    public Action? PendingCancel;
    public bool Cancelled;
    public int Moves;
    public bool Stopped;
    public List<(string Caster, string Target, string Hand)> Contacts = [];
    public List<SkillUnitHit> Units = [];
    public CombatCooldownStore? Store;
    public int Mana = 100;
    public int Spent;

    public IActorCooldownPort Cooldowns => Store?.ForActor(Caster.Id) ?? AlwaysReadyCooldownPort.Instance;

    public bool CanSpendMana(int cost) => Mana >= cost;

    public bool SpendMana(int cost)
    {
        if (Mana < cost) return false;
        Mana -= cost;
        Spent += cost;
        return true;
    }

    public SkillUnitOutcome Unit(SkillUnitHit hit)
    {
        Units.Add(hit);
        return SkillUnitOutcome.Success;
    }

    public Combatant? Actor(string id) =>
        id == Caster.Id ? Caster : id == Target?.Id ? Target : null;

    public IReadOnlyList<Combatant> Candidates() => Target is null ? [] : [Target];

    public string Facing() => Caster.Facing ?? "south";

    public void MoveToward(Vec2 position) => Moves += 1;

    public void Stop() => Stopped = true;

    public bool CanStand(Vec2 position, string? clearanceActorId = null) => true;

    public Action Play(CombatAction action, Action<int> frame, Action complete)
    {
        PlayedFacing = action.Facing;
        PendingFrame = frame;
        PendingComplete = complete;
        // 取消回调只中断表现，不能反过来调完成回调（取消是纯中断）。
        PendingCancel = () => Cancelled = true;
        return PendingCancel;
    }

    public void Frame(CombatAction action, int index, Action<string, string?> contact,
        SkillUnitsReady unitsReady, Action? areaContact = null)
    {
        _ = areaContact;
        if (action.Skill is { } skill)
        {
            foreach (var item in SkillActionRules.SkillPlaybackEvents(skill.Action, index, skill.Level ?? 1,
                         action.RepeatState))
                if (item is UnitsEvent units)
                    unitsReady(new SkillUnitsArrival { Event = units, SelectedTargetId = action.Target?.Id });
            return;
        }

        if (action.BasicAttack is null) return;
        foreach (var hand in BasicAttackRules.StrikesAtFrame(action.BasicAttack, index))
            if (action.Target is { } target) contact(target.Id, hand);
    }

    public void Contact(AttackContact contact) =>
        Contacts.Add((contact.CasterId, contact.TargetId, contact.Hand ?? "main-hand"));
}

sealed class TestSkillUnitPort : ISkillUnitSequencePort
{
    public required IReadOnlyList<IReadOnlyList<string>> TargetsByCue { get; init; }

    public Func<int, string, SkillUnitOutcome> UnitResult { get; init; } = (_, _) => SkillUnitOutcome.Success;

    public IReadOnlyList<string> Targets(int cueIndex) => TargetsByCue[cueIndex];

    public SkillUnitOutcome Unit(int cueIndex, string targetId) => UnitResult(cueIndex, targetId);
}

sealed class TestBuffResourcePort : IBuffResourcePort
{
    public int Captured { get; private set; }

    public int Applied { get; private set; }

    public string? LastActorId { get; private set; }

    public ResourceCapacities CaptureResourceCapacities(string actorId)
    {
        Captured += 1;
        LastActorId = actorId;
        return new ResourceCapacities(1672, 100);
    }

    public void OnApply(string actorId, BattleBuffEffect effect, ResourceCapacities entry)
    {
        Applied += 1;
        LastActorId = actorId;
    }
}

sealed class TestEffectPort : ICombatEffectPort
{
    private readonly HashSet<string> _actors = ["caster", "enemy"];

    public TestEffectPort(CombatConditions conditions) => Conditions = conditions;

    public CombatConditions Conditions { get; }

    public CombatThreatSystem? Threat => null;

    public List<HitResult> Hits { get; } = [];

    public List<HitResult> Shown { get; } = [];

    public double Random() => 0.0;

    public T Run<T>(Func<T> work) => work();

    public bool Available(string id) => _actors.Contains(id);

    public bool PartySide(string id) => id == "caster";

    public Fighter? Fighter(string id) => id switch
    {
        "caster" => new Fighter { Level = 1, HitBase = 100, PhysicalAttack = 10 },
        "enemy" => new Fighter { Level = 1, HitBase = 100 },
        _ => null,
    };

    public Vec2? Position(string id) => _actors.Contains(id) ? Vec2.Zero : null;

    public Vitals? Vitals(string id) => _actors.Contains(id) ? new Vitals(100, 50, 100) : null;

    public int Attribute(string id, string key) => 0;

    public string ValueSourceId(string id, ICombatUnit unit) => id;

    public PreparedDamage? Prepare(SkillUnitHit hit) =>
        DamageFormula.Prepare((NumericUnit)hit.Skill.Action.CombatUnits![hit.CueIndex], hit.Skill.Level ?? 1,
            PrimaryAttributeMath.Zero());

    public bool ApplyBuff(string id, string buffId, int strength, BattleBuffEffect effect, string sourceId) => true;

    public StateApplicationRequest StateRequest(string sourceId, string targetId, string state) =>
        new() { State = state, SourceRelation = "hostile", Kind = "action" };

    public void StateAccepted(string targetId, string state)
    {
    }

    public SkillUnitOutcome CommandSummons(SkillUnitHit hit) => SkillUnitOutcome.Success;

    public SkillUnitOutcome Summon(SkillUnitHit hit, SummonCombatUnit unit, int rank) => SkillUnitOutcome.Success;

    public void RestoreHealth(string id, int amount)
    {
    }

    public void SetMana(string id, int value)
    {
    }

    public void HealAccepted(string sourceId, string targetId)
    {
    }

    public SkillReactionBuff? LearnedBuff(string id, string key) => null;

    public int RearBonus(string sourceId, string targetId) => 0;

    public void Reactions(SkillUnitHit hit, HitResult result, string kind, Vec2? position)
    {
    }

    public void Hit(SkillUnitHit hit, HitResult result, HitPresentation presentation) => Hits.Add(result);

    public void DropMana(string id, int damage, Vec2? position, string? kind)
    {
    }

    public void ShowHit(SkillUnitHit hit, HitResult result, HitPresentation presentation) => Shown.Add(result);
}

sealed class ReactionPorts : ISkillReactionPorts
{
    private readonly Mulberry32 _random;
    private readonly bool _noLearnedBuff;

    public ReactionPorts(uint seed, bool noLearnedBuff)
    {
        _random = new Mulberry32(seed);
        _noLearnedBuff = noLearnedBuff;
    }

    public double Random() => _random.Next();

    public SkillReactionBuff? LearnedBuff(string ownerId, string skill) => _noLearnedBuff
        ? null
        : new SkillReactionBuff(skill, 5004, new BattleBuffEffect { DurationMs = 6000, TickMs = 0 });
}

sealed class Mulberry32
{
    private uint _state;

    public Mulberry32(uint seed) => _state = seed;

    public double Next()
    {
        unchecked
        {
            _state += 0x6D2B79F5u;
            var t = _state;
            t = (t ^ (t >> 15)) * (t | 1u);
            t ^= t + (t ^ (t >> 7)) * (t | 61u);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }
}

sealed class FlightRecorder : IFlightPort
{
    private readonly Func<Combatant?> _actor;

    public FlightRecorder(Func<Combatant?> actor) => _actor = actor;

    public List<FlightEvent> Events { get; } = [];

    public Combatant? Actor(string id) => _actor();

    public void Emit(FlightEvent action) => Events.Add(action);
}

sealed class TransitionRecorder : ITransitionPort
{
    private readonly Func<Combatant?> _actor;

    public TransitionRecorder(Func<Combatant?> actor) => _actor = actor;

    public List<string> Effects { get; } = [];

    public Combatant? Actor(string id) => _actor();

    public void Emit(FlightEvent action)
    {
    }

    public void Effect(string name, Vec2 position) => Effects.Add(name);
}

sealed class RecordingActionPort : ICombatActionPort
{
    private readonly CombatRuntime _runtime;

    public RecordingActionPort() => _runtime = new CombatRuntime(() => Frame);

    public long Frame { get; set; }

    public Dictionary<string, Combatant> Actors { get; } = [];

    public List<string> Log { get; } = [];

    public List<string> Effects { get; } = [];

    public List<string> Contacts { get; } = [];

    public List<string> Notifications { get; } = [];

    public List<int> Poses { get; } = [];

    public List<FlightRequest> FlightRequests { get; } = [];

    public List<string> Flights { get; } = [];

    public List<float> Screens { get; } = [];

    private readonly List<Action<Vec2>> _arrivals = [];

    public CombatRuntime Runtime => _runtime;

    public BattleContent Catalog { get; } = new();

    public Func<double> Random => () => 0.5;

    public Combatant? Actor(string id) => Actors.GetValueOrDefault(id);

    public IReadOnlyList<Combatant> Candidates() => Actors.Values.ToList();

    public (int Width, int Height) Viewport() => (960, 640);

    public void Pose(CombatAction action, int index) => Poses.Add(index);

    public void Effect(string name, string variant, Vec2 position, string? placement = null, string? recipientId = null)
    {
        Effects.Add(name);
        Log.Add($"effect:{name}");
    }

    public void Flight(FlightEvent flight)
    {
        Flights.Add(flight.GetType().Name);
        Log.Add($"flight:{flight.GetType().Name}");
    }

    public void Screen(float transparency) => Screens.Add(transparency);

    public void Notify(string name, IReadOnlyDictionary<string, object?> data) => Notifications.Add(name);

    public bool CanSummonAt(Vec2 position, CollisionBox bounds) => true;

    public Vec2? NearestSummonSpot(Vec2 position, CollisionBox bounds, Vec2? preferred = null) =>
        preferred ?? position;

    public void BeginSummons(string ownerId)
    {
    }

    public string? Summon(string ownerId, string enemyId, Vec2 position) => $"{enemyId}#1";

    public void Contact(string id, string? hand)
    {
        Contacts.Add(hand is null ? $"contact:{id}" : $"contact:{id}:{hand}");
        Log.Add(hand is null ? $"contact:{id}" : $"contact:{id}:{hand}");
    }

    public void Reset()
    {
        Log.Clear();
        Effects.Clear();
        Contacts.Clear();
        Notifications.Clear();
        Poses.Clear();
        FlightRequests.Clear();
        Flights.Clear();
        Screens.Clear();
    }
}

file sealed class AttackProcTestPort(Func<double> random, List<string> log,
    int SplashCount = 0, bool ZeroChances = false)
    : AttackProcExecutor.IAttackProcPort
{
    public Func<double>? Random => random;

    public int ReadChance(string key) => ZeroChances ? 0 : 1000;

    public void ApplyDamage(int amount) => log.Add($"damage:{amount}");

    public void ApplyBuff(string key) => log.Add($"buff:{key}");

    public int FireSplash(int amount)
    {
        log.Add($"splash:{amount}");
        return SplashCount;
    }
}

file sealed record DisturbanceActor(
    Vec2 Position, CollisionBox Bounds, string Id, bool SameCamp, bool OwnSummon, int EffectiveDefense)
    : Disturbance.IActor;

/// <summary>按顺序取骰的小队列，取空时停在最后一个值（执行器不该再取）。</summary>
file sealed class Dice(params double[] values)
{
    private int _index;

    public double Next()
    {
        var value = values[Math.Min(_index, values.Length - 1)];
        _index += 1;
        return value;
    }
}
file sealed class ActionDispatchProbe(List<string> log, bool stealthed = true) : IActionDispatchPort
{
    public bool IsStealthed()
    {
        log.Add("is-stealthed");
        return stealthed;
    }

    public void Work() => log.Add("work");

    public void RemoveStealth() => log.Add("remove-stealth");

    public void AfterStealthBreak() => log.Add("after-break");

    public void Sound() => log.Add("sound");
}

sealed class SummonTestWorld
{
    public const string OwnerId = "player";

    public readonly CollisionGrid Grid;

    public Vec2 OwnerPosition;

    public SummonTactics? Tactics;

    public List<string>? SummonStates;

    public bool RecallAllowed;

    public int Stops;

    public int Followed;

    private readonly Dictionary<string, (string Team, Vec2 Position)> _actors = new();

    private FriendlySummonController? _summon;

    private string? _summonId;

    public SummonTestWorld(int tiles = 40)
    {
        var blocked = new bool[tiles * tiles];
        Grid = new CollisionGrid(new MapDefinition
        {
            TileSize = 16,
            Columns = tiles,
            Rows = tiles,
            Width = tiles * 16,
            Height = tiles * 16,
            Blocked = blocked,
        });
    }

    public FriendlySummonController AddSummon(string id, Vec2 position)
    {
        _actors[id] = ("player", position);
        _actors[OwnerId] = ("player", OwnerPosition);
        _summonId = id;
        var behavior = new EnemyBehavior
        {
            Aggression = "active",
            AwarenessRangeUnits = 20,
            MoveSpeedPxPerSecond = 80,
            CanMove = true,
        };
        _summon = new FriendlySummonController(id, OwnerId, FriendlySummonRole.HellSoldier, behavior,
            SummonFakeAttack(), new TestSummonPort(this, id));
        return _summon;
    }

    public void AddEnemy(string id, Vec2 position) => _actors[id] = ("hostile", position);

    public FriendlySummonController Summon => _summon ?? throw new Exception("还没召唤");

    public IReadOnlyList<Combatant> ActorsForPort() => _actors.Keys.Select(id => Actor(id)!).ToList();

    public void Sync() => _actors[OwnerId] = ("player", OwnerPosition);

    public Combatant? Actor(string id)
    {
        if (!_actors.TryGetValue(id, out var entry)) return null;
        var position = id == OwnerId ? OwnerPosition : entry.Position;
        var states = id == _summonId ? SummonStates : null;
        return new Combatant(id, entry.Team, position, new CollisionBox(), "south", states);
    }

    internal static ResolvedBasicAttack SummonFakeAttack() => new()
    {
        Definition = new BasicAttackDefinition
        {
            CycleFrames = 4,
            FrameDurationMs = 60,
            StrikeFrame = 2,
            ApproachDistanceUnits = 4,
            SearchRadiusPx = 64,
        },
        EffectiveCycleFrames = 4,
        DurationMs = 240,
        Clips = new Dictionary<string, string>(),
        Sequence = new Dictionary<string, int[]>(),
    };
}

sealed class TestSummonPort(SummonTestWorld world, string selfId) : IFriendlySummonPort
{
    public CombatAction? LastAction;

    public Action? PendingComplete;

    public Combatant? Actor(string id) => world.Actor(id);

    public IReadOnlyList<Combatant> Candidates() =>
        world.ActorsForPort();

    public string Facing() => "south";

    public void MoveToward(Vec2 position)
    {
    }

    public void Stop() => world.Stops += 1;

    public bool CanStand(Vec2 position, string? clearanceActorId = null) =>
        world.Grid.CanStandOnTerrain(position.X, position.Y);

    public Action Play(CombatAction action, Action<int> frame, Action complete)
    {
        PendingComplete = complete;
        LastAction = action;
        return () => { };
    }

    public void Frame(CombatAction action, int index, Action<string, string?> contact,
        SkillUnitsReady unitsReady, Action? areaContact = null)
    {
        _ = unitsReady;
        _ = areaContact;
        if (action.Skill is not null || action.BasicAttack is null) return;
        foreach (var hand in BasicAttackRules.StrikesAtFrame(action.BasicAttack, index))
            if (action.Target is { } target) contact(target.Id, hand);
    }

    public void Contact(AttackContact contact)
    {
    }

    public void FollowPath(Func<Vec2?> nextWaypoint)
    {
        world.Followed += 1;
        nextWaypoint();
    }

    public Vec2 Home() => world.OwnerPosition;

    public (int Current, int Maximum)? Health(string id) => id == SummonTestWorld.OwnerId ? (100, 100) : null;

    public Rect NavigationBounds() => world.Grid.Bounds;

    public IStandability NavigationSnapshot() => world.Grid;

    public SummonTactics Tactics() =>
        world.Tactics ?? new SummonTactics([], null, null, [], [SummonTestWorld.OwnerId]);

    public bool TryRecall() => world.RecallAllowed;

    public bool AutonomousTarget(string id) => true;
}

sealed class PartyTestWorld
{
    public const string LeaderId = "player";

    public CounterattackMode Counterattack = CounterattackMode.Off;

    public int DamageCountValue;

    public bool InCombatValue;

    public readonly List<EnemyPlayableSkill> SkillList = [];

    public readonly Dictionary<string, string> SkillKinds = new();

    public int Regroups;

    public int Followed;

    public int Stops;

    internal readonly Dictionary<string, Vec2> _actors = new();

    public PartyTestWorld() => _actors[LeaderId] = new Vec2(320, 320);

    public PartyController AddMember(string id, Vec2 position)
    {
        _actors[id] = position;
        var port = new TestPartyPort(this, id);
        var combat = new CombatController(id, PartyTestAttack(), port);
        return new PartyController(id, port, combat, new FixedRandom());
    }

    public void AddEnemy(string id, Vec2 position) => _actors[id] = position;

    internal static ResolvedBasicAttack PartyTestAttack() => new()
    {
        Definition = new BasicAttackDefinition
        {
            CycleFrames = 4,
            FrameDurationMs = 60,
            StrikeFrame = 2,
            ApproachDistanceUnits = 4,
            SearchRadiusPx = 64,
        },
        EffectiveCycleFrames = 4,
        DurationMs = 240,
        Clips = new Dictionary<string, string>(),
        Sequence = new Dictionary<string, int[]>(),
    };

    internal sealed class FixedRandom : Random
    {
        protected override double Sample() => 0.999;

        public override double NextDouble() => 0.999;
    }
}

sealed class TestPartyPort(PartyTestWorld world, string selfId) : IPartyPort
{
    public Combatant? Actor(string id)
    {
        if (!world._actors.TryGetValue(id, out var position)) return null;
        var team = id == "slime" ? "hostile" : "party";
        return new Combatant(id, team, position, new CollisionBox(), "south");
    }

    public IReadOnlyList<Combatant> Candidates() =>
        world._actors.Keys.Select(id => Actor(id)!).ToList();

    public string Facing() => "south";

    public void MoveToward(Vec2 position)
    {
    }

    public void Stop() => world.Stops += 1;

    public bool CanStand(Vec2 position, string? clearanceActorId = null) => true;

    public Action Play(CombatAction action, Action<int> frame, Action complete)
    {
        return () => { };
    }

    public void Frame(CombatAction action, int index, Action<string, string?> contact,
        SkillUnitsReady unitsReady, Action? areaContact = null)
    {
    }

    public void Contact(AttackContact contact)
    {
    }

    public Combatant? Leader() => Actor(PartyTestWorld.LeaderId);

    public string? FocusTarget() => null;

    public bool InCombat() => world.InCombatValue;

    public CounterattackMode Counterattack() => world.Counterattack;

    public int DamageCount() => world.DamageCountValue;

    public void FollowPath(Func<Vec2?> nextWaypoint)
    {
        world.Followed += 1;
        nextWaypoint();
    }

    public void Regroup() => world.Regroups += 1;

    public IReadOnlyList<EnemyPlayableSkill> Skills() => world.SkillList;

    public string? SkillKind(string id) => world.SkillKinds.GetValueOrDefault(id);

    public (int Health, int MaxHealth)? Vitals(string id) => null;

    public bool Attacking(string id) => false;

    public int AttackRange() => 4;

    public Rect NavigationBounds() => new(0f, 0f, 1024f, 1024f);
}

file sealed class ManaTestMember(string id, int health, int mana, int maxMana) : ManaMember
{
    public string Id => id;

    public int Health => health;

    public int Mana { get; private set; } = mana;

    public int MaxMana => maxMana;

    public void SetMana(int value) => Mana = value;
}

sealed class FakeNavActor(object key, CollisionBox box, Vec2 position, bool party, bool enabled = true,
    bool ghost = false) : INavigationActor
{
    public object Key => key;

    public NavigationShape Shape => NavigationShape.Of(box);

    public Vec2 Position { get; set; } = position;

    public bool Enabled => enabled;

    public bool Party => party;

    public bool Ghost => ghost;

    public bool StrictTerrain => party;
}
