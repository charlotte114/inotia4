namespace Inotia4.Domain.Combat;

public sealed class PlacedEnemy
{
    public required string Key { get; init; }

    public required string EnemyId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public int? Health { get; set; }

    public int? Mana { get; set; }

    public int? Level { get; set; }

    public bool? Elite { get; set; }

    public PlacedEnemy Clone() => new()
    {
        Key = Key, EnemyId = EnemyId, X = X, Y = Y, Health = Health, Mana = Mana, Level = Level, Elite = Elite,
    };
}

public sealed record EnemyRosterState(IReadOnlyList<PlacedEnemy> Enemies, int Sequence, int PartySize);

public class EnemyRoster
{
    private readonly List<PlacedEnemy> _enemies = [];

    private readonly List<Action> _listeners = [];

    private int _sequence;

    private int _partySize = 1;

    public IReadOnlyList<PlacedEnemy> Enemies => _enemies;

    public int PartySize => _partySize;

    public EnemyRosterState CaptureState() => new(
        _enemies.Select(enemy => enemy.Clone()).ToArray(), _sequence, _partySize);

    public virtual void RestoreState(EnemyRosterState state)
    {
        _enemies.Clear();
        _enemies.AddRange(state.Enemies.Select(enemy => enemy.Clone()));
        _sequence = state.Sequence;
        _partySize = state.PartySize;
        Notify();
    }

    public IDisposable Subscribe(Action listener)
    {
        _listeners.Add(listener);
        return new Unsubscribe(this, listener);
    }

    private void Notify()
    {
        foreach (var listener in _listeners.ToArray()) listener();
    }

    public PlacedEnemy PlaceEnemy(string enemyId, float x, float y)
    {
        var placed = new PlacedEnemy { Key = $"enemy-{++_sequence}", EnemyId = enemyId, X = x, Y = y };
        _enemies.Add(placed);
        Notify();
        return placed;
    }

    public void RestoreEnemy(PlacedEnemy placed)
    {
        if (_enemies.Any(enemy => enemy.Key == placed.Key)) return;
        _enemies.Add(placed.Clone());
        Notify();
    }

    public void RemoveEnemy(string key)
    {
        if (_enemies.All(enemy => enemy.Key != key)) return;
        _enemies.RemoveAll(enemy => enemy.Key == key);
        Notify();
    }

    public void MoveEnemy(string key, float x, float y)
    {
        var placed = _enemies.FirstOrDefault(enemy => enemy.Key == key);
        if (placed is null) return;
        placed.X = x;
        placed.Y = y;
    }

    public EnemySnapshot? EnemySnapshot(string key, Content.EnemyDefinition definition, int playerLevel = 1,
        int sceneLevel = 1)
    {
        var placed = _enemies.FirstOrDefault(enemy => enemy.Key == key && enemy.EnemyId == definition.Id);
        if (placed is null) return null;

        var stats = EnemyStatsCalculator.Calculate(definition, new EnemyStatsOptions(
            Level: placed.Level, Elite: placed.Elite, PartySize: _partySize,
            PlayerLevel: playerLevel, SceneLevel: sceneLevel));
        return new EnemySnapshot(key, definition.Name, stats,
            BoundedHealth(placed.Health ?? stats.MaxHealth, stats.MaxHealth),
            BoundedHealth(placed.Mana ?? stats.MaxMana, stats.MaxMana));
    }

    public virtual void SetEnemyHealth(string key, int value, Content.EnemyDefinition definition)
    {
        var placed = _enemies.FirstOrDefault(enemy => enemy.Key == key && enemy.EnemyId == definition.Id);
        var snapshot = EnemySnapshot(key, definition);
        if (placed is null || snapshot is null) return;

        var health = BoundedHealth(value, snapshot.MaxHealth);
        if (health == snapshot.Health) return;
        placed.Health = health;
        Notify();
    }

    public virtual void SetEnemyMana(string key, int value, Content.EnemyDefinition definition)
    {
        var placed = _enemies.FirstOrDefault(enemy => enemy.Key == key && enemy.EnemyId == definition.Id);
        var snapshot = EnemySnapshot(key, definition);
        if (placed is null || snapshot is null) return;

        var mana = BoundedHealth(value, snapshot.MaxMana);
        if (mana == snapshot.Mana) return;
        placed.Mana = mana;
        Notify();
    }

    public virtual void SetPartySize(int size, IReadOnlyList<Content.EnemyDefinition> definitions)
    {
        EnemyStatsCalculator.PartySize(size);
        if (size == _partySize) return;

        var changes = new List<(PlacedEnemy Placed, int? Health)>();
        foreach (var placed in _enemies)
        {
            var definition = definitions.FirstOrDefault(enemy => enemy.Id == placed.EnemyId)
                             ?? throw new InvalidOperationException($"怪物内容缺失：{placed.EnemyId}");
            var before = EnemySnapshot(placed.Key, definition)!;
            var after = EnemyStatsCalculator.Calculate(definition, new EnemyStatsOptions(
                Level: placed.Level, Elite: placed.Elite, PartySize: size));
            var minimumHealth = before.Health > 0 ? 1 : 0;
            changes.Add((placed, placed.Health is null
                ? null
                : Math.Max(minimumHealth, (int)Math.Truncate(before.Health * (double)after.MaxHealth / before.MaxHealth))));
        }

        _partySize = size;
        foreach (var (placed, health) in changes) placed.Health = health;
        Notify();
    }

    public static int BoundedHealth(int value, int maxHealth) => Math.Max(0, Math.Min(value, maxHealth));

    private sealed class Unsubscribe(EnemyRoster owner, Action listener) : IDisposable
    {
        public void Dispose() => owner._listeners.Remove(listener);
    }
}
