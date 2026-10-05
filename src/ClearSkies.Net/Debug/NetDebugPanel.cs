using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using ImGuiNET;

namespace ClearSkies.Net.Debug;

/// <summary>The network panel: the Host's Participants and entities (where it's on this machine), round trip, clock offset
/// and slew, players' input (queued on the authority, predicted elsewhere), interpolation delay, bandwidth per channel, and sliders for artificial latency and loss (default test setting: 150 ms round trip, 2% loss).</summary>
public sealed class NetDebugPanel : IDebugUiSystem
{
    private readonly SimulationParticipant _net;
    private readonly Host? _host;
    private readonly RemoteBodySystem _remote;
    private readonly LaggedTransport? _lag;
    private long _lastBytesOut, _lastBytesIn;
    private double _lastSample, _kbOut, _kbIn;

    /// <param name="host">The Host, when it's on this machine.</param>
    public NetDebugPanel(SimulationParticipant net, Host? host, RemoteBodySystem remote, LaggedTransport? lag)
    {
        _net = net;
        _host = host;
        _remote = remote;
        _lag = lag;
    }

    public string DebugName => "Network";

    public void DrawDebugUi()
    {
        ImGui.Text($"Role: {_net.Session.Role}, {_net.Session.LocalPeer}   Tick {_net.Clock.Tick}");
        if (_host is { } host)
        {
            ImGui.Text(_lag is null ? "Single-player: start with --host <port> to let others join"
                                   : $"Participants: {host.Peers.Count}");
            foreach (var p in host.Peers)
                ImGui.Text($"  {p.Name} ({p.Peer}): {p.Known.Count} entities, view at ({p.ViewCentre.X:0}, {p.ViewCentre.Y:0}, {p.ViewCentre.Z:0})");
            ImGui.Text($"Entities kept: {host.Entities.Count}; players released {host.Releases} this session");
        }
        ImGui.Text($"Spawns waiting: {_net.Spawns.Count}");
        _net.Inputs?.DrawDebugUi();
        if (_net.ClockSync is { } sync)
            ImGui.Text($"Round trip {sync.RoundTripMs:0} ms, clock offset {sync.Offset:+0.00;-0.00} ticks, " +
                       $"rate {_net.Clock.Rate:0.000}, snaps {sync.SnapsPerMinute}/min");
        if (_net.Prediction is { } prediction)
            ImGui.Text($"Own player: {prediction.Unanswered} inputs unanswered, corrected {prediction.Corrections:N0} times " +
                       $"(last {prediction.LastCorrection:0.000}, largest {prediction.LargestCorrection:0.000})");
        var (least, most) = _remote.Delays;
        ImGui.Text($"Others drawn {least:0.0}-{most:0.0} ticks behind");
        float margin = (float)_remote.Margin;
        if (ImGui.SliderFloat("Extra delay (ticks)", ref margin, 0, 10, "%.1f")) _remote.Margin = margin;

        if (_lag is { } t)
        {
            double now = _net.NowMs;
            if (now - _lastSample > 1000)
            {
                double secs = (now - _lastSample) / 1000.0;
                _kbOut = (t.Stats.BytesSent - _lastBytesOut) / 1024.0 / secs;
                _kbIn = (t.Stats.BytesReceived - _lastBytesIn) / 1024.0 / secs;
                (_lastBytesOut, _lastBytesIn, _lastSample) = (t.Stats.BytesSent, t.Stats.BytesReceived, now);
            }
            ImGui.Text($"Out {_kbOut:0.0} KB/s, in {_kbIn:0.0} KB/s   (reliable out {t.Stats.BytesSentByChannel[0] / 1024:N0} KB, unreliable {t.Stats.BytesSentByChannel[1] / 1024:N0} KB total)");
        }
        if (_lag != null)
        {
            ImGui.Separator();
            ImGui.Text("Artificial lag on what this machine sends:");
            float latency = (float)_lag.LatencyMs, jitter = (float)_lag.JitterMs, loss = (float)(_lag.LossChance * 100);
            if (ImGui.SliderFloat("Latency (ms, one way)", ref latency, 0, 300, "%.0f")) _lag.LatencyMs = latency;
            if (ImGui.SliderFloat("Jitter (ms)", ref jitter, 0, 100, "%.0f")) _lag.JitterMs = jitter;
            if (ImGui.SliderFloat("Loss (%)", ref loss, 0, 20, "%.1f")) _lag.LossChance = loss / 100;
            if (ImGui.Button("Test setting (75 ms each way, 2% loss)")) { _lag.LatencyMs = 75; _lag.LossChance = 0.02; }
        }
    }
}
