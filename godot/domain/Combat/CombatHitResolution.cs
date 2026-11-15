using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Combat;

/// <summary>
/// 一次命中与它触发的后续反应共用一个「结算完再判死」的门。
///
/// 为什么需要：多段伤害链（连射、溅射、反应追加）会在同一次命中里嵌套触发更多命中，
/// 中间某一环把目标打到 0 时不能立刻走死亡流程——后面几环还在读这个目标。
/// 所以死亡动作排到最外层结算结束时执行，届时再按当前生命确认一次。
/// </summary>
public sealed class CombatHitResolution
{
    private int _depth;
    private readonly Dictionary<string, Action> _finalizers = [];

    public T Run<T>(Func<T> resolve)
    {
        _depth += 1;
        try
        {
            return resolve();
        }
        finally
        {
            _depth -= 1;
            if (_depth == 0)
            {
                var finalizers = _finalizers.Values.ToList();
                _finalizers.Clear();
                foreach (var finalize in finalizers) finalize();
            }
        }
    }

    public void DeferDefeat(string id, Action finalizeIfStillZero)
    {
        if (_depth == 0)
        {
            finalizeIfStillZero();
            return;
        }

        _finalizers[id] = finalizeIfStillZero;
    }

    public bool IsAwaitingDefeat(string id) => _depth > 0 && _finalizers.ContainsKey(id);
}
