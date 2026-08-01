using System;
using System.Diagnostics;
using System.Threading;

namespace PhotoImporter.Core.Copying
{
    public enum CopyPauseState
    {
        Running,
        PausePending,
        NativePauseRequested,
        PausedBetweenFiles,
        PausedWithinFile
    }

    public sealed class CopyPauseController : IDisposable
    {
        private readonly object _sync = new object();
        private readonly ManualResetEventSlim _resumeGate = new ManualResetEventSlim(true);
        private readonly TimeSpan _nativePauseDelay;
        private readonly IProgress<CopyPauseState> _progress;
        private bool _pauseRequested;
        private bool _disposed;
        private long _pauseRequestedTimestamp;
        private CopyPauseState _state = CopyPauseState.Running;

        public CopyPauseController(
            TimeSpan nativePauseDelay,
            IProgress<CopyPauseState> progress = null)
        {
            if (nativePauseDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(nativePauseDelay));

            _nativePauseDelay = nativePauseDelay;
            _progress = progress;
        }

        public CopyPauseState State
        {
            get
            {
                lock (_sync) return _state;
            }
        }

        public bool IsPauseRequested
        {
            get
            {
                lock (_sync) return _pauseRequested;
            }
        }

        public bool RequestPause()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_pauseRequested) return false;

                _pauseRequested = true;
                _pauseRequestedTimestamp = Stopwatch.GetTimestamp();
                _resumeGate.Reset();
                _state = CopyPauseState.PausePending;
            }

            Report(CopyPauseState.PausePending);
            return true;
        }

        public bool Resume()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_pauseRequested) return false;

                _pauseRequested = false;
                _state = CopyPauseState.Running;
                _resumeGate.Set();
            }

            Report(CopyPauseState.Running);
            return true;
        }

        internal bool ShouldPauseCurrentFile()
        {
            var stateChanged = false;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_pauseRequested) return false;
                if (_state == CopyPauseState.NativePauseRequested) return true;

                var elapsedTicks = Stopwatch.GetTimestamp() - _pauseRequestedTimestamp;
                var elapsed = TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency);
                if (elapsed < _nativePauseDelay) return false;

                _state = CopyPauseState.NativePauseRequested;
                stateChanged = true;
            }

            if (stateChanged) Report(CopyPauseState.NativePauseRequested);
            return true;
        }

        internal void WaitAtFileBoundary(CancellationToken cancellationToken)
        {
            WaitWhilePauseRequested(CopyPauseState.PausedBetweenFiles, cancellationToken);
        }

        internal void WaitAfterNativePause(CancellationToken cancellationToken)
        {
            WaitWhilePauseRequested(CopyPauseState.PausedWithinFile, cancellationToken);
        }

        private void WaitWhilePauseRequested(
            CopyPauseState pausedState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_pauseRequested) return;
                _state = pausedState;
            }

            Report(pausedState);
            _resumeGate.Wait(cancellationToken);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _resumeGate.Dispose();
        }

        private void Report(CopyPauseState state)
        {
            if (_progress != null) _progress.Report(state);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CopyPauseController));
        }
    }
}
