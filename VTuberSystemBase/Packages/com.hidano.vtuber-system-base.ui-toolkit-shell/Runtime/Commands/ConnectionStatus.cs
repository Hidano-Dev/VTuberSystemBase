#nullable enable
using System;
using VTuberSystemBase.CoreIpc.Abstractions;

namespace VTuberSystemBase.UiToolkitShell.Commands
{
    /// <summary>
    /// Adapter that converts core-ipc-foundation's <see cref="IConnectionDiagnostics.ConnectionStateChanged"/>
    /// transitions into UI-facing <see cref="ConnectionStatusCode"/> transitions and forwards them
    /// through <see cref="OnStatusChanged"/> on the same thread the upstream event fires on
    /// (the Unity main thread per the core-ipc-foundation D-3 inheritance).
    /// See design.md §Commands §IConnectionStatus.
    /// </summary>
    public sealed class ConnectionStatus : IConnectionStatus, IDisposable
    {
        private readonly ICoreIpcBus bus;
        private readonly Action<ConnectionState, ConnectionState> stateChangedHandler;
        private ConnectionStatusCode currentStatus;
        private bool disposed;

        public ConnectionStatus(ICoreIpcBus bus)
        {
            if (bus is null) throw new ArgumentNullException(nameof(bus));
            this.bus = bus;
            currentStatus = ConnectionStatusCode.Initializing;
            stateChangedHandler = HandleConnectionStateChanged;
            bus.Diagnostics.ConnectionStateChanged += stateChangedHandler;

            // ConnectionStateChanged はイベントであり、購読より前に起きた遷移は再生しない。
            // 同一プロセスのループバックでは購読登録までに接続が Connected まで一瞬で進むため、
            // その遷移を取りこぼすと currentStatus が Initializing に取り残される（接続バッジが
            // 永久に「初期化中」表示になる潜在バグ）。購読登録の直後に latched な CurrentState を
            // 一度反映して取りこぼしを塞ぐ。購読→読み取りの順なので、読み取りより後に来る遷移は
            // ハンドラが拾い、読み取り時点で既に済んでいた遷移はここで拾う（重複は
            // HandleConnectionStateChanged の mapped == currentStatus ガードが吸収する）。
            // ただし Disconnected は「未接続の初期値」と区別がつかないため、Initializing grace を維持する。
            var latched = bus.Diagnostics.CurrentState;
            if (latched != ConnectionState.Disconnected)
            {
                HandleConnectionStateChanged(ConnectionState.Disconnected, latched);
            }
        }

        public bool IsConnected => currentStatus == ConnectionStatusCode.Connected;

        public ConnectionStatusCode CurrentStatus => currentStatus;

        public event Action<ConnectionStatusEvent>? OnStatusChanged;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            bus.Diagnostics.ConnectionStateChanged -= stateChangedHandler;
        }

        private void HandleConnectionStateChanged(ConnectionState previous, ConnectionState current)
        {
            var mapped = Map(current);
            if (mapped == currentStatus) return;

            var transition = new ConnectionStatusEvent(currentStatus, mapped, DateTimeOffset.UtcNow);
            currentStatus = mapped;
            OnStatusChanged?.Invoke(transition);
        }

        private static ConnectionStatusCode Map(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Disconnected:
                    return ConnectionStatusCode.Disconnected;
                case ConnectionState.Connecting:
                    return ConnectionStatusCode.Connecting;
                case ConnectionState.Connected:
                    return ConnectionStatusCode.Connected;
                case ConnectionState.Reconnecting:
                    return ConnectionStatusCode.Reconnecting;
                case ConnectionState.PermanentlyDisconnected:
                    return ConnectionStatusCode.FailedPermanently;
                default:
                    return ConnectionStatusCode.Disconnected;
            }
        }
    }
}
