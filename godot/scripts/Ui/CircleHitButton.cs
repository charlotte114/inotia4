using Godot;

namespace Inotia4.Ui;

public sealed partial class CircleHitButton : Button
{
    private readonly float _radius;

    public CircleHitButton(float radius) => _radius = radius;

    public override bool _HasPoint(Vector2 point) => (point - Size / 2f).Length() <= _radius;
}
