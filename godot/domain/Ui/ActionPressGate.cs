namespace Inotia4.Domain.Ui;

public sealed class ActionPressGate
{
    private int? _pointer;

    public bool Press(int pointer)
    {
        if (_pointer is not null) return false;
        _pointer = pointer;
        return true;
    }

    public void Release(int pointer)
    {
        if (_pointer == pointer) _pointer = null;
    }

    public void Reset() => _pointer = null;
}
