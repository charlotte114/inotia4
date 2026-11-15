using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Inotia4.Scenes;

internal static class StoryClock
{
    public static async Task<bool> WaitAsync(Node owner, double seconds, CancellationToken token)
    {
        if (seconds <= 0) return token.IsCancellationRequested;
        if (owner.GetTree() is not { } tree) return false;

        var elapsed = 0.0;
        while (elapsed < seconds)
        {
            if (token.IsCancellationRequested) return true;
            await owner.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            elapsed += owner.GetProcessDeltaTime();
        }

        return token.IsCancellationRequested;
    }
}
