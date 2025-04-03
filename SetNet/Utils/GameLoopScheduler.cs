using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Utils
{
    public class GameLoopScheduler
    {
        private readonly List<ScheduledTask> _tasks = new List<ScheduledTask>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loopTask;

        public GameLoopScheduler(CancellationToken externalToken)
        {
            ExternalToken = externalToken;
        }

        public CancellationToken ExternalToken { get; }
        public CancellationToken CombinedToken => CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ExternalToken).Token;

        public GameLoopScheduler Every(int milliseconds, Func<Task> action)
        {
            _tasks.Add(new ScheduledTask
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds),
                Action = action,
                NextExecution = DateTime.UtcNow
            });
            return this;
        }

        public async Task StartAsync()
        {
            _loopTask = RunLoopAsync();
            await _loopTask;
        }

        public void StartInBackground()
        {
            _loopTask = RunLoopAsync();
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            if (_loopTask != null)
            {
                try
                {
                    await _loopTask;
                }
                catch (TaskCanceledException) { }
            }
        }

        private async Task RunLoopAsync()
        {
            while (!CombinedToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                foreach (var task in _tasks)
                {
                    if (now >= task.NextExecution)
                    {
                        _ = RunTaskSafely(task);
                        task.NextExecution = now + task.Interval;
                    }
                }

                await Task.Delay(1, CombinedToken);
            }
        }

        private async Task RunTaskSafely(ScheduledTask task)
        {
            try
            {
                await task.Action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameLoop] Error in scheduled task: {ex.Message}");
            }
        }

        private class ScheduledTask
        {
            public TimeSpan Interval;
            public Func<Task> Action;
            public DateTime NextExecution;
        }
    }
}