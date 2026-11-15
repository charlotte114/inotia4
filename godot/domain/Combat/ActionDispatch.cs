namespace Inotia4.Domain.Combat;

public interface IActionDispatchPort
{
    bool IsStealthed();

    void Work();

    void RemoveStealth();

    void AfterStealthBreak()
    {
    }

    void Sound()
    {
    }
}

public static class ActionDispatch
{
    public static void Run(bool breaksStealth, IActionDispatchPort port)
    {
        var enteredStealthed = breaksStealth && port.IsStealthed();
        port.Work();
        if (enteredStealthed)
        {
            port.RemoveStealth();
            port.AfterStealthBreak();
        }

        port.Sound();
    }
}
