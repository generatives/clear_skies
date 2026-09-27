using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using ImGuiNET;

namespace ClearSkies.Net.Debug;

/// <summary>The network panel: who's connected, round trip, clock offset and slew, interpolation delay, bandwidth per
/// channel, and sliders for artificial latency and loss (default test setting: 150 ms round trip, 2% loss).</summary>
public sealed class NetDebugPanel : IDebugUiSystem
{
    private readonly NetSession _net;
    private readonly RemoteBodySystem _remote;
    private readonly LaggedTransport? _lag;
    private long _lastBytesOut, _lastBytesIn;
    private double _lastSample, _kbOut, _kbIn;

    public NetDebugPanel(NetSession net, RemoteBodySystem remote, LaggedTransport? lag)
    {
        _net = net;
        _remote = remote;
        _lag = lag;
    }

    public string DebugName => "Network";

    public void DrawDebugUi()
    {
        ImGui.Text($"Role: {_net.Session.Role}, {_net.Session.LocalPeer}   Tick {_net.Clock.Tick}");
        switch (_net)
        {
            case HostSession host:
                ImGui.Text(_net.Transport is null ? "Transport off (single-player): start with --host <port> to let others join"
                                                  : $"Players connected: {host.Peers.Count}");
                foreach (var p in host.Peers) ImGui.Text($"  {p.Name} ({p.Peer}): {p.State}");
                break;
            case ClientSession client:
                ImGui.Text($"Round trip {client.ClockSync.RoundTripMs:0} ms, clock offset {client.ClockSync.Offset:+0.00;-0.00} ticks, " +
                           $"rate {_net.Clock.Rate:0.000}, snaps {client.ClockSync.SnapsPerMinute}/min");
                break;
        }
        float delay = (float)_remote.InterpolationDelay;
        if (ImGui.SliderFloat("Interpolation delay (ticks)", ref delay, 4, 12, "%.1f")) _remote.InterpolationDelay = delay;

        if (_net.Transport is { } t)
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
