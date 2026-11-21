using System;
using Cysharp.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The synced start of a song: gameplay holds at the loading screen until the session schedules the start,
    /// then waits for the moment it was scheduled for.
    /// </summary>
    /// <remarks>Touched only on the main thread.</remarks>
    internal sealed class SyncedStart
    {
        /// <summary>Give up on a synced start after this long, in seconds, and start alone.</summary>
        public const double MAX_WAIT_SECONDS = 120.0;

        private bool    _waiting;
        private double? _startAt;
        private string  _cancelReason;

        /// <summary>Gameplay is loaded and holding for the start, or for the moment it was scheduled for.</summary>
        public bool IsWaiting => _waiting;

        /// <summary>Holding for the session to schedule the start: we're ready.</summary>
        public bool IsReady => _waiting && !_startAt.HasValue;

        /// <summary>
        /// Start holding for the start. Call this before reporting ready: when we're the host and the last one
        /// ready, the session schedules the start inside <c>ReportReady</c>, and <see cref="OnScheduled"/> drops
        /// a start that arrives while we aren't waiting.
        /// </summary>
        public void Begin()
        {
            _waiting = true;
            _startAt = null;
            _cancelReason = null;
        }

        /// <summary>
        /// Wait until the session schedules the start, it's cancelled, or <paramref name="isConnected"/> says the
        /// session is gone. <see cref="Begin"/> must come first.
        /// </summary>
        public async UniTask<LinkController.StartResult> WaitForSignal(Func<bool> isConnected)
        {
            double deadline = LinkController.Now + MAX_WAIT_SECONDS;
            while (true)
            {
                if (_cancelReason != null)
                {
                    ToastManager.ToastWarning($"Online start cancelled: {_cancelReason}");
                    _waiting = false;
                    return LinkController.StartResult.Cancelled;
                }

                if (_startAt.HasValue)
                {
                    return LinkController.StartResult.Synced;
                }

                if (!isConnected() || LinkController.Now >= deadline)
                {
                    ToastManager.ToastWarning("Lost the online session, starting without sync");
                    _waiting = false;
                    return LinkController.StartResult.Alone;
                }

                await UniTask.Yield();
            }
        }

        /// <summary>Wait for the moment the start was scheduled for.</summary>
        public async UniTask WaitForStartTime(Func<bool> hasSession)
        {
            try
            {
                while (_startAt.HasValue && LinkController.Now < _startAt.Value && hasSession())
                {
                    await UniTask.Yield();
                }
                YargLogger.LogInfo("Link: synced start");
            }
            finally
            {
                _waiting = false;
                _startAt = null;
            }
        }

        /// <summary>Stop waiting: the song starts without the session.</summary>
        public void Abandon()
        {
            _waiting = false;
            _startAt = null;
        }

        /// <summary>The session scheduled the start, this long from now.</summary>
        /// <returns>Whether we were waiting for it.</returns>
        public bool OnScheduled(double delaySeconds)
        {
            if (!_waiting) return false;

            _startAt = LinkController.Now + delaySeconds;
            return true;
        }

        /// <summary>The session cancelled the start.</summary>
        /// <returns>Whether we were waiting for it.</returns>
        public bool OnCancelled(string reason)
        {
            if (!_waiting) return false;

            _cancelReason = reason;
            return true;
        }
    }
}
