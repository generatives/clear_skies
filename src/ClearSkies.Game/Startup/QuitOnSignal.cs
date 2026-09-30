using System.Runtime.InteropServices;
using ClearSkies.Engine.Core;

namespace ClearSkies.Game.Startup;

/// <summary>Ctrl+C or a kill quits (on the main thread, next frame) as if the window were closed by hand, so the game
/// shuts down properly (a host saves the world on the way out).</summary>
public sealed class QuitOnSignal : IDisposable
{
    private readonly PosixSignalRegistration _int, _term;
    private bool _requested;

    public QuitOnSignal(EngineHost host)
    {
        _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, Request);
        _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Request);
        host.AddSystem(new LambdaSystem(() => { if (Volatile.Read(ref _requested)) host.Quit(); }), SystemStage.Input);
    }

    private void Request(PosixSignalContext c)
    {
        c.Cancel = true;
        Volatile.Write(ref _requested, true);
    }

    public void Dispose()
    {
        _int.Dispose();
        _term.Dispose();
    }
}
