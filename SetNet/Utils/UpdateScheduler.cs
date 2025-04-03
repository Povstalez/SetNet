using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Utils
{
    public class UpdateScheduler
    {
        private readonly List<LoopTask> _tasks = new List<LoopTask>();
        private readonly CancellationToken _token;

        public UpdateScheduler(CancellationToken token)
        {
            _token = token;
        }

        public UpdateScheduler Every(int milliseconds, Func<Task> action)
        {
            _tasks.Add(new LoopTask
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds),
                Action = action,
                Busy = false
            });
            return this;
        }

        public void Start()
        {
            foreach (var task in _tasks)
            {
                RunLoop(task);
            }
        }

        private async void RunLoop(LoopTask task)
        {
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(task.Interval, _token);
                    if (!task.Busy)
                    {
                        task.Busy = true;
                        await task.Action();
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scheduler] Error: {ex.Message}");
                }
                finally
                {
                    task.Busy = false;
                }
            }
        }

        private class LoopTask
        {
            public TimeSpan Interval;
            public Func<Task> Action;
            public bool Busy;
        }
    }
}