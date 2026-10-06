using ClearSkies.Engine.Core;
using ClearSkies.Engine.Ui;
using ClearSkies.Net.Session;

namespace ClearSkies.Game.Hud;

/// <summary>
/// While a client is joining, the screen is covered: the world is still loading around the spawn and the clock settling
/// on the Host's (see <see cref="SimulationParticipant.JoinStatus"/>), and what's drawn meanwhile jumps about. A dark panel over
/// everything, with what joining is waiting on, until our player arrives.
/// </summary>
public sealed class JoiningScreen : ISystem
{
    private static readonly UiColor Backdrop = new(14, 16, 22);
    private static readonly UiColor TextColor = new(245, 232, 205);

    private readonly UiContext _ui;
    private readonly SimulationParticipant _net;
    private float _seconds;

    public JoiningScreen(UiContext ui, SimulationParticipant net)
    {
        _ui = ui;
        _net = net;
    }

    public void Update(float dt)
    {
        if (_net.Joined) return;
        _seconds += dt;
        var size = _ui.LayoutSize;
        using (_ui.Element("joining", ElementDeclaration.Anchored(AttachPoint.CenterCenter, zIndex: 1000) with
        {
            Layout = new LayoutConfig
            {
                Sizing = Sizing.Fixed(size.Width, size.Height),
                ChildAlignment = ChildAlignment.Center,
            },
            BackgroundColor = Backdrop,
        }))
        {
            _ui.BlockPointer();
            int dots = (int)(_seconds * 2) % 4;
            _ui.Text($"{_net.JoinStatus}{new string('.', dots)}", new TextConfig { FontSize = 16, TextColor = TextColor });
        }
    }
}
