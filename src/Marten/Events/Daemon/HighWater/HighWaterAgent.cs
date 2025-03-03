using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Baseline;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace Marten.Events.Daemon.HighWater
{
    internal class HighWaterAgent : IDisposable
    {
        private readonly IHighWaterDetector _detector;
        private readonly ShardStateTracker _tracker;
        private readonly ILogger _logger;
        private readonly DaemonSettings _settings;
        private readonly CancellationToken _token;
        private readonly Timer _timer;
        private Task<Task> _loop;

        private HighWaterStatistics _current;

        // ReSharper disable once ContextualLoggerProblem
        public HighWaterAgent(IHighWaterDetector detector, ShardStateTracker tracker, ILogger logger, DaemonSettings settings, CancellationToken token)
        {
            _detector = detector;
            _tracker = tracker;
            _logger = logger;
            _settings = settings;
            _token = token;

            _timer = new Timer(_settings.HealthCheckPollingTime.TotalMilliseconds) {AutoReset = true};
            _timer.Elapsed += TimerOnElapsed;
        }

        public async Task Start()
        {
            IsRunning = true;

            _current = await _detector.Detect(_token).ConfigureAwait(false);

            _tracker.Publish(new ShardState(ShardState.HighWaterMark, _current.CurrentMark){Action = ShardAction.Started});

            _loop = Task.Factory.StartNew(DetectChanges, _token, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent, TaskScheduler.Default);

            _timer.Start();

            _logger.LogInformation("Started HighWaterAgent");
        }

        public bool IsRunning { get; private set; }


        private async Task DetectChanges()
        {
            try
            {
                _current = await _detector.Detect(_token).ConfigureAwait(false);

                if (_current.CurrentMark > 0)
                {
                    _tracker.MarkHighWater(_current.CurrentMark);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed while making the initial determination of the high water mark");
            }

            await Task.Delay(_settings.FastPollingTime, _token).ConfigureAwait(false);

            while (!_token.IsCancellationRequested)
            {
                HighWaterStatistics statistics = null;
                try
                {
                    statistics = await _detector.Detect(_token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed while trying to detect high water statistics");
                    await Task.Delay(_settings.SlowPollingTime, _token).ConfigureAwait(false);
                    continue;
                }

                var status = statistics.InterpretStatus(_current);

                switch (status)
                {
                    case HighWaterStatus.Changed:
                        await markProgress(statistics, _settings.FastPollingTime).ConfigureAwait(false);
                        break;

                    case HighWaterStatus.CaughtUp:
                        await markProgress(statistics, _settings.SlowPollingTime).ConfigureAwait(false);
                        break;

                    case HighWaterStatus.Stale:
                        _logger.LogInformation("High Water agent is stale at {CurrentMark}", statistics.CurrentMark);

                        // This gives the high water detection a chance to allow the gaps to fill in
                        // before skipping to the safe harbor time
                        var safeHarborTime = _current.Timestamp.Add(_settings.StaleSequenceThreshold);
                        if (safeHarborTime > statistics.Timestamp)
                        {
                            await Task.Delay(_settings.SlowPollingTime, _token).ConfigureAwait(false);
                            continue;
                        }
                        
                        _logger.LogInformation("High Water agent is stale after threshold of {DelayInSeconds} seconds, skipping gap to events marked after {SafeHarborTime}", _settings.StaleSequenceThreshold.TotalSeconds, safeHarborTime);


                        statistics = await _detector.DetectInSafeZone(_token).ConfigureAwait(false);
                        await markProgress(statistics, _settings.FastPollingTime).ConfigureAwait(false);
                        break;
                }
            }

            _logger.LogInformation("HighWaterAgent has detected a cancellation and has stopped polling");
        }

        private async Task markProgress(HighWaterStatistics statistics, TimeSpan delayTime)
        {
            // don't bother sending updates if the current position is 0
            if (statistics.CurrentMark == 0 || statistics.CurrentMark == _tracker.HighWaterMark)
            {
                await Task.Delay(delayTime, _token).ConfigureAwait(false);
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("High Water mark detected at {CurrentMark}", statistics.CurrentMark);
            }

            _current = statistics;
            _tracker.MarkHighWater(statistics.CurrentMark);

            await Task.Delay(delayTime, _token).ConfigureAwait(false);
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            _ = CheckState();
        }

        private async Task CheckState()
        {
            if (_loop.IsFaulted && !_token.IsCancellationRequested)
            {
                _logger.LogError(_loop.Exception,"HighWaterAgent polling loop was faulted");

                try
                {
                    _loop.Dispose();
                    await Start().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error trying to restart the HighWaterAgent");
                }
            }
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _loop?.SafeDispose();
        }

        public async Task CheckNow()
        {
            var statistics = await _detector.Detect(_token).ConfigureAwait(false);
            _tracker.MarkHighWater(statistics.CurrentMark);
        }

        public Task Stop()
        {
            try
            {
                _timer?.Stop();
                _loop?.Dispose();

                IsRunning = false;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to stop the HighWaterAgent");
            }

            return Task.CompletedTask;
        }
    }
}
