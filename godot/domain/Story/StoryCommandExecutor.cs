using System.Collections.Generic;
using System.Text.Json;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Story;

/// <summary>
/// 剧本命令的执行器。节点演完后依次执行，然后才走出口——
/// 顺序不能反：有的节点里「接取任务」之后紧跟着的分支就依赖刚写进去的状态。
///
/// 已实现：任务状态、旗标、物品给收（背包）、金钱给收（会话）。
/// 队伍、换装、世界可见性会记进 <see cref="Skipped"/>，等对应系统接上再补，
/// 不会假装执行成功——否则任务链会静默断在某个没人发现的点上。
///
/// 批量入口 <see cref="ApplyBatch"/> 按事务口径执行：
/// 先拍基线、逐条应用，任何一条真失败就整批回滚并原样抛出。
/// </summary>
public sealed class StoryCommandExecutor
{
    private readonly QuestJournal _journal;
    private readonly Bag? _inventory;
    private readonly Session? _session;
    private readonly CampaignContent? _campaign;
    private readonly IWorldCommandSink? _worldSink;
    private readonly IWorldCommandSink? _recordingSink;
    private readonly List<StoryWorldCommand> _worldLog = [];
    private readonly List<string> _applied = new();
    private readonly HashSet<string> _skipped = new();

    public StoryCommandExecutor(
        QuestJournal journal, Bag? inventory = null, Session? session = null,
        CampaignContent? campaign = null, IWorldCommandSink? worldSink = null)
    {
        _journal = journal;
        _inventory = inventory;
        _session = session;
        _campaign = campaign;
        _worldSink = worldSink;
        _recordingSink = worldSink is null ? null : new RecordingSink(worldSink, _worldLog);
    }

    public IReadOnlyList<string> Applied => _applied;

    public IReadOnlyList<StoryWorldCommand> WorldLog => _worldLog;

    /// <summary>因为缺少系统而跳过的命令类型。</summary>
    public IReadOnlyCollection<string> Skipped => _skipped;

    public void ApplyWorld(StoryWorldCommand command)
    {
        if (_recordingSink is null)
        {
            _skipped.Add($"world:{command.GetType().Name}");
            return;
        }

        _recordingSink.Apply(command);
    }

    public void Apply(JsonElement command)
    {
        var type = StoryType.Of(command);

        switch (type)
        {
            case "quest":
                ApplyQuest(command);
                return;

            case "flag":
                var flag = StoryType.Text(command, "flag");
                var value = StoryType.Bool(command, "value", true);
                _journal.SetFlag(flag, value);
                _applied.Add($"flag:{flag}={value}");
                return;

            case "item-give":
                if (_inventory is null) { _skipped.Add(type); return; }
                _inventory.Add(StoryType.Text(command, "item"), (int)StoryType.Float(command, "quantity"));
                _applied.Add($"item-give {StoryType.Text(command, "item")}×{StoryType.Float(command, "quantity")}");
                return;

            case "item-take":
                if (_inventory is null) { _skipped.Add(type); return; }
                ApplyItemTake(command);
                return;

            case "money-give":
                if (_session is null) { _skipped.Add(type); return; }
                _session.GrantReward(0, (int)StoryType.Float(command, "amount"));
                _applied.Add($"money-give {StoryType.Float(command, "amount")}");
                return;

            case "money-take":
                if (_session is null) { _skipped.Add(type); return; }
                ApplyMoneyTake(command);
                return;

            case "world":
                WorldCommands.Apply(command, _recordingSink, _skipped);
                return;

            case "party-join":
            case "party-leave":
            case "party-control":
            case "equip":
            case "unequip-destroy":
            case "health":
            case "mana":
                ApplyActorCommand(command, type);
                return;

            case "item-drop":
                if (_recordingSink is null) { _skipped.Add(type); return; }
                ApplyItemDrop(command);
                return;
            case "skill":
            case "map-visible":
                _skipped.Add(type);
                return;

            default:
                _skipped.Add(type.Length > 0 ? type : "未知命令");
                return;
        }
    }

    /// <summary>
    /// 整批执行一批剧本命令，口径是「要么全成、要么全不成」：
    /// 先拍基线（任务簿、剧情世界、背包、会话），逐条 <see cref="Apply"/>；
    /// 任何一条真失败——物品/金币不足、任务状态非法——就把这些子系统恢复到基线，
    /// 再原样抛出。绝不出现「命令执行一半」的世界。
    ///
    /// 缺系统（没挂背包/会话/任务簿）依旧按「系统未接」的口径记 <see cref="Skipped"/>，不算失败：
    /// 那是「这一系统还没接上」，不是「这一批命令不该落地」。
    /// </summary>
    public void ApplyBatch(IEnumerable<JsonElement> commands)
    {
        var bookBefore = _journal.Book.CaptureState();
        var worldBefore = _journal.StoryWorld?.CaptureState();
        var bagBefore = _inventory?.CaptureState();
        var sessionBefore = _session?.CaptureState();
        try
        {
            foreach (var command in commands) Apply(command);
        }
        catch
        {
            _journal.Book.RestoreState(bookBefore);
            if (worldBefore is not null) _journal.StoryWorld!.RestoreState(worldBefore);
            if (bagBefore is not null) _inventory!.RestoreState(bagBefore);
            if (sessionBefore is not null) _session!.RestoreState(sessionBefore);
            throw;
        }
    }

    private void ApplyItemDrop(JsonElement command)
    {
        var item = StoryType.Text(command, "item");
        var quantity = StoryType.Int(command, "quantity", 1);
        var map = StoryType.Text(command, "map");
        var x = StoryType.Float(command, "x");
        var y = StoryType.Float(command, "y");
        _recordingSink!.DropItem(map, x, y, item, quantity);
        _applied.Add($"item-drop:{item}x{quantity}@{map}({x:0},{y:0})");
    }

    /// <summary>
    /// 演员类命令：队伍加入/离队、切换操控、改生命魔法、穿脱装备。
    /// 每条各查自己的前提：切操控要求该成员已上阵且没倒下，穿装备要求物品在背包里，
    /// 改生命魔法要求数值合法或写 <c>full</c>。
    /// 缺角色卡或名册时记进跳过清单——只丢这一条，免得一段剧情因为一个系统没接整段停演。
    /// </summary>
    private void ApplyActorCommand(JsonElement command, string type)
    {
        var party = _session?.Party;
        var hero = _session?.Hero;

        switch (type)
        {
            case "party-join":
            {
                var companion = StoryType.Text(command, "companion");
                if (party is null) { _skipped.Add($"{type} {companion}"); return; }
                party.JoinStory(companion);
                _applied.Add($"party-join {companion}");
                return;
            }

            case "party-leave":
            {
                var companion = StoryType.Text(command, "companion");
                if (party is null) { _skipped.Add($"{type} {companion}"); return; }
                party.LeaveStory(companion);
                _applied.Add($"party-leave {companion}");
                return;
            }

            case "party-control":
            {
                var member = StoryType.Text(command, "member");
                if (party is null)
                {
                    // 没有名册时只允许停在主角，且主角必须活着。
                    if (member != "player" || (hero?.Player.Health ?? 0) <= 0)
                        _skipped.Add($"{type} {member}");
                    else _applied.Add($"party-control {member}");
                    return;
                }

                if (!party.SwitchControl(member)) _skipped.Add($"{type} {member}（未上阵或已倒下）");
                else _applied.Add($"party-control {member}");
                return;
            }

            case "health":
            case "mana":
            {
                var member = StoryType.Text(command, "member");
                var full = StoryType.Text(command, "value") == "full";
                var raw = StoryType.Int(command, "value");
                if (!full && raw < 0) { _skipped.Add($"{type} {member}（数值无效）"); return; }

                if (member == "player")
                {
                    if (hero is null) { _skipped.Add($"{type} {member}"); return; }
                    if (type == "health") hero.SetHealth(full ? hero.Player.MaxHealth : raw);
                    else hero.SetMana(full ? hero.Player.MaxMana : raw);
                    _applied.Add($"{type} player={(full ? "full" : raw.ToString())}");
                    return;
                }

                var companion = party?.Get(member);
                if (companion is null) { _skipped.Add($"{type} {member}"); return; }
                if (type == "health") companion.SetHealth(full ? companion.Player.MaxHealth : raw);
                else companion.SetMana(full ? companion.Player.MaxMana : raw);
                _applied.Add($"{type} {member}={(full ? "full" : raw.ToString())}");
                return;
            }

            case "equip":
            {
                var member = StoryType.Text(command, "member");
                var item = StoryType.Text(command, "item");
                var slot = StoryType.Text(command, "slot");
                var create = StoryType.Bool(command, "create");
                var ifPresent = StoryType.Bool(command, "ifPresent");

                if (_inventory is null) { _skipped.Add(type); return; }
                if (member != "player" && party?.Get(member) is null && ifPresent) return;

                var classId = member == "player" ? hero?.ClassId : party?.Get(member)?.ClassId;
                if (classId is null) { _skipped.Add($"equip {member}（拥有者不存在）"); return; }

                if (create)
                {
                    if (_inventory.EquipmentFor(member).ContainsKey(slot)) _inventory.Unequip(slot, member);
                    _inventory.AddEquipped(item, slot, member);
                    _applied.Add($"equip {member} {item}（新建）");
                    return;
                }

                var entry = _inventory.Entries.FirstOrDefault(value => value.ItemId == item);
                if (entry is null) { _skipped.Add($"equip {item}（不在背包中）"); return; }

                var level = member == "player" ? hero?.Level : party?.Get(member)?.Level;
                _inventory.Equip(entry.Id, slot, member, classId, level);
                _applied.Add($"equip {member} {item}");
                return;
            }

            case "unequip-destroy":
            {
                var member = StoryType.Text(command, "member");
                var item = StoryType.Text(command, "item");
                var ifPresent = StoryType.Bool(command, "ifPresent");

                if (_inventory is null) { _skipped.Add(type); return; }
                if (member != "player" && party?.Get(member) is null)
                {
                    if (ifPresent) return;
                    _skipped.Add($"unequip-destroy {member}（拥有者不存在）");
                    return;
                }

                foreach (var (slot, worn) in _inventory.EquipmentItems(_inventory.EquipmentFor(member)))
                {
                    if (worn != item) continue;
                    var entryId = _inventory.EquipmentFor(member)[slot];
                    _inventory.Unequip(slot, member);
                    _inventory.Take(entryId, 1);
                }

                _applied.Add($"unequip-destroy {member} {item}");
                return;
            }
        }
    }

    private void ApplyItemTake(JsonElement command)
    {
        var item = StoryType.Text(command, "item");
        var quantity = (int)StoryType.Float(command, "quantity");

        if (StoryType.Bool(command, "allowPartial"))
        {
            var taken = _inventory!.Count(item) < quantity ? _inventory.Count(item) : quantity;
            if (taken > 0) _inventory.Remove(item, taken);
            if (taken > 0) _applied.Add($"item-take {item}×{taken}（部分）");
            else _skipped.Add($"item-take {item} 身上一个都没有");
            return;
        }

        if (_inventory!.Remove(item, quantity)) _applied.Add($"item-take {item}×{quantity}");
        else throw new InvalidOperationException($"剧情物品不足：{item}×{quantity}");
    }

    private void ApplyMoneyTake(JsonElement command)
    {
        var amount = (int)StoryType.Float(command, "amount");
        var allowPartial = StoryType.Bool(command, "allowPartial");

        if (!allowPartial && _session!.Money < amount)
            throw new InvalidOperationException($"剧情金币不足：需要 {amount}，现有 {_session!.Money}");

        var taken = allowPartial ? int.Min(amount, _session!.Money) : amount;
        if (taken <= 0)
        {
            _skipped.Add($"money-take {amount} 不足");
            return;
        }

        _session!.GrantReward(0, -taken);
        _applied.Add($"money-take {taken}");
    }

    private void ApplyQuest(JsonElement command)
    {
        var inner = command.TryGetProperty("command", out var nested) ? nested : command;
        var action = StoryType.Text(inner, "type");
        var questId = StoryType.Text(inner, "quest");

        if (_journal.Campaign is null)
        {
            _skipped.Add($"quest:{action}");
            return;
        }

            switch (action)
            {
                case "accept":
                    _journal.Apply(QuestCommand.Accept(questId, source: "script"));
                    _applied.Add($"quest:accept {questId}");
                    foreach (var item in QuestRules.OnAcceptItems(_journal.Campaign, questId))
                        _applied.Add($"on-accept {item.Item}×{item.Quantity}");
                    return;

                case "complete":
                    _journal.Apply(QuestCommand.Complete(questId));
                    _applied.Add($"quest:complete {questId}");
                    return;

                case "script-complete":
                    _journal.Apply(QuestCommand.ScriptComplete(questId));
                    _applied.Add($"quest:script-complete {questId}");
                    return;

                case "script-ready":
                    _journal.Apply(QuestCommand.ScriptReady(questId));
                    _applied.Add($"quest:script-ready {questId}");
                    return;

                case "reset":
                    _journal.Apply(QuestCommand.Reset(questId));
                    _applied.Add($"quest:reset {questId}");
                    return;

                case "reset-side-quests":
                    _journal.Apply(QuestCommand.ResetSideQuests());
                    _applied.Add("quest:reset-side-quests");
                    return;

                case "pin-active":
                    _journal.Apply(QuestCommand.PinActive(questId));
                    _applied.Add($"quest:pin {questId}");
                    return;

                case "unpin-active":
                    _journal.Apply(QuestCommand.UnpinActive(questId));
                    _applied.Add($"quest:unpin {questId}");
                    return;

                case "refresh":
                    _journal.Refresh();
                    _applied.Add("quest:refresh");
                    return;

                default:
                    _skipped.Add($"quest:{action}");
                    return;
            }
    }

internal sealed class RecordingSink(IWorldCommandSink inner, List<StoryWorldCommand> log) : IWorldCommandSink
{
    public void Apply(StoryWorldCommand command)
    {
        log.Add(command);
        inner.Apply(command);
    }

    public void DropItem(string map, float x, float y, string item, int quantity) =>
        inner.DropItem(map, x, y, item, quantity);
}
}
