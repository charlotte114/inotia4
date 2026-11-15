namespace Inotia4.Domain.Combat;

/// <summary>
/// 读档时的战斗快照校验与静止化。
///
/// 读档铺回世界之前要做两件事，缺一件都会出问题：
/// <list type="number">
/// <item>校验结构。脏档、内容改版后的旧档、手改过的存档都会在这里被拒，
/// 而不是等到深处某个下标越界、或者 AI 追一个不存在的目标时再崩。</item>
/// <item>把所有战斗单位静止化。快照里的路径、目标、攻击阶段属于存档那一刻；
/// 照着铺回去，读档瞬间怪就会带着旧路径跑起来，玩家还没看清画面就已经被围。</item>
/// </list>
///
/// 与 Godot 形态的对应：敌人与召唤物在原版是同一份名册，召唤物也记在里面；
/// Godot 的召唤物只存在世界快照里，所以校验对象是 <c>state.Enemies</c>。
/// 召唤物的分组也不单独存：从 <c>summonedBy</c> 现推即可，没有另一份要对。
/// </summary>
public static class CombatSnapshotValidation
{
    private static readonly string[] Facings = ["north", "south", "east", "west"];

    public static void Validate(WorldSnapshotState state, IReadOnlySet<string> enemyIds,
        IReadOnlyCollection<string> partyIds, string playerId = "player")
    {
        if (state.Party is null) throw new InvalidOperationException("战斗快照结构无效：party 不是数组");
        if (state.Enemies is null) throw new InvalidOperationException("战斗快照结构无效：enemies 不是数组");

        var members = new HashSet<string>(partyIds) { playerId };
        var actors = new HashSet<string>();

        foreach (var saved in state.Party)
        {
            if (saved is null) throw new InvalidOperationException("战斗队员快照无效：空条目");
            if (!members.Contains(saved.Id))
                throw new InvalidOperationException($"战斗队员快照无效：{saved.Id} 不在上阵名单里");
            if (!actors.Add(saved.Id))
                throw new InvalidOperationException($"战斗队员快照无效：{saved.Id} 重复");
            if (Array.IndexOf(Facings, saved.Facing) < 0)
                throw new InvalidOperationException($"战斗队员快照无效：{saved.Id} 的朝向是 {saved.Facing}");
            if (!double.IsFinite(saved.X) || !double.IsFinite(saved.Y))
                throw new InvalidOperationException($"战斗队员快照无效：{saved.Id} 的坐标不是有限数");
        }

        foreach (var saved in state.Enemies)
        {
            if (saved is null) throw new InvalidOperationException("战斗敌人快照无效：空条目");
            if (string.IsNullOrEmpty(saved.Id))
                throw new InvalidOperationException("战斗敌人快照无效：缺少场上键");
            if (!actors.Add(saved.Id))
                throw new InvalidOperationException($"战斗敌人快照无效：{saved.Id} 重复");
            if (saved.EnemyId is not { Length: > 0 } enemyId || !enemyIds.Contains(enemyId))
                throw new InvalidOperationException($"战斗敌人快照无效：{saved.Id} 的种类 {saved.EnemyId} 不在内容里");
            if (saved.X is { } x && !double.IsFinite(x) || saved.Y is { } y && !double.IsFinite(y))
                throw new InvalidOperationException($"战斗敌人快照无效：{saved.Id} 的坐标不是有限数");
            if (saved.Health is < 0)
                throw new InvalidOperationException($"战斗敌人快照无效：{saved.Id} 的生命为负");
            if (saved.SummonRemainingMs is < 0)
                throw new InvalidOperationException($"召唤状态数值无效：{saved.Id} 的剩余寿命为负");
        }

        ValidateSummonOwners(state.Enemies, members);
        ValidateEnemyAi(state.Enemies, members);
    }

    /// <summary>
    /// 敌人／召唤物的行为状态：敌方 AI 的状态名、路径数组与五个计时数值都必须在域内；
    /// 友方召唤还必须「主人对得上、分工与种类对得上」。这两条不校的话，手改过的档会带着
    /// <c>NaN</c> 计时或错位的主人一路跑下去——症状是敌人不追人、召唤物原地不动，很难倒查。
    /// </summary>
    private static void ValidateEnemyAi(IReadOnlyList<WorldEnemySaved> enemies, HashSet<string> members)
    {
        foreach (var saved in enemies)
        {
            // 召主是「主角或某个上阵成员」才算友方（两边的名册都含主角，是同一个集合；
            // Godot 的 partyIds 不含主角，所以这里直接用 members）。
            var friendly = saved.SummonedBy is { Length: > 0 } owner && members.Contains(owner);

            if (saved.EnemyAi is null && saved.SummonAi is null)
            {
                if (friendly)
                    throw new InvalidOperationException($"友方召唤缺少行为状态：{saved.Id}");
                continue;
            }

            if (saved.EnemyAi is not null && saved.SummonAi is not null)
                throw new InvalidOperationException($"战斗行为类型无效：{saved.Id} 同时带了两种行为状态");

            if (saved.SummonAi is { } summon)
            {
                if (!friendly)
                    throw new InvalidOperationException($"友方召唤行为类型无效：{saved.Id} 不是友方召唤却带了召唤行为");

                var role = saved.EnemyId switch
                {
                    "enemy-hell-s-pawn-1" => FriendlySummonRole.HellSoldier,
                    "enemy-beast-companion-1" => FriendlySummonRole.Beast,
                    _ => null,
                };
                if (role is null || summon.OwnerId != saved.SummonedBy || summon.Role != role)
                    throw new InvalidOperationException(
                        $"友方召唤行为身份不一致：{saved.Id}（主人 {summon.OwnerId}／分工 {summon.Role}）");

                if (summon.State.Length == 0 || !double.IsFinite(summon.Accumulator) || summon.Accumulator < 0 ||
                    summon.Clock < 0 || summon.NextDecisionAt < 0 || summon.NextSearchAt < 0 ||
                    summon.NextRecallAt < 0 || summon.NextFollowSearchAt < 0 || summon.TargetSince < 0)
                    throw new InvalidOperationException($"友方召唤行为状态无效：{saved.Id}");
                continue;
            }

            if (friendly)
                throw new InvalidOperationException($"友方召唤行为类型无效：{saved.Id} 用了敌方行为状态");

            var ai = saved.EnemyAi!;
            if (ai.State is not (EnemyAiState.Idle or EnemyAiState.Wandering or EnemyAiState.Chasing
                or EnemyAiState.Attacking or EnemyAiState.Returning))
                throw new InvalidOperationException($"敌方行为状态无效：{saved.Id} 的状态是 {ai.State}");
            if (ai.Path is null)
                throw new InvalidOperationException($"敌方行为状态无效：{saved.Id} 的路径不是数组");
            if (!double.IsFinite(ai.Accumulator) || ai.Accumulator < 0 ||
                ai.Clock < 0 || ai.NextPathAt < 0 || ai.LastProgressAt < 0 || ai.IdleElapsed < 0)
                throw new InvalidOperationException($"敌方行为状态无效：{saved.Id} 的计时器越界");
        }
    }

    /// <summary>
    /// 召唤主人引用：必须指向场上的另一条记录或一名上阵成员，且沿主人链走下去不能绕回自己。
    /// 判环的理由是「撤掉主人的召唤物」会顺着主人链递归，环会让它死循环。
    /// </summary>
    private static void ValidateSummonOwners(IReadOnlyList<WorldEnemySaved> enemies, HashSet<string> members)
    {
        var byId = new Dictionary<string, WorldEnemySaved>();
        foreach (var saved in enemies) byId[saved.Id] = saved;

        foreach (var saved in enemies)
        {
            if (saved.SummonedBy is not { Length: > 0 } owner) continue;
            if (owner == saved.Id)
                throw new InvalidOperationException($"召唤主人引用无效：{saved.Id} 的主人指向自己");
            if (!members.Contains(owner) && !byId.ContainsKey(owner))
                throw new InvalidOperationException($"召唤主人引用无效：{saved.Id} 的主人 {owner} 不在场上");

            var visited = new HashSet<string> { saved.Id };
            var cursor = owner;
            while (cursor.Length > 0)
            {
                if (!visited.Add(cursor))
                    throw new InvalidOperationException($"召唤主人关系包含循环：{saved.Id} → {cursor}");
                cursor = byId.TryGetValue(cursor, out var next) && next.SummonedBy is { Length: > 0 } up
                    ? up
                    : string.Empty;
            }
        }
    }

    public static void Stationaryize(WorldSnapshotState state)
    {
        if (state.FocusTargetId is { Length: > 0 } focus &&
            state.Party.All(saved => saved.Id != focus) && state.Enemies.All(saved => saved.Id != focus))
            state.FocusTargetId = null;

        foreach (var saved in state.Party)
        {
            if (saved?.Ai is not { } ai) continue;
            ai.Path.Clear();
            ai.Destination = null;
            ai.DestinationTarget = null;
            ai.Accumulator = 0;
        }

        foreach (var saved in state.Enemies)
        {
            if (saved is null) continue;
            if (saved.EnemyAi is not null) saved.EnemyAi = saved.EnemyAi.Stationary();
            if (saved.SummonAi is not null) saved.SummonAi = FriendlySummonAi.StationaryState(saved.SummonAi);
        }
    }
}
