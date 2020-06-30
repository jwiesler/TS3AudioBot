using System.Threading;
using System.Threading.Tasks;

namespace TS3AudioBot.Audio.Preparation
{
	public class WaitTask {
		private int waitMs;
		private readonly EventWaitHandle waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
		private readonly CancellationToken token;

		public WaitTask(int waitMs, CancellationToken token) {
			Interlocked.Exchange(ref this.waitMs, waitMs);
			this.token = token;
		}

		// Returns true if the wait succeeded
		public void Run() {
			int ms;
			do {
				ms = Interlocked.Exchange(ref waitMs, 0);
			} while (waitHandle.WaitOne(ms) && !token.IsCancellationRequested && waitMs > 0);

			if (token.IsCancellationRequested)
				throw new TaskCanceledException();
		}

		// Callable from any thread while the Task is waiting, updates the wait time
		public void UpdateWaitTime(int ms) {
			Interlocked.Exchange(ref waitMs, ms);
			waitHandle.Set();
		}

		// Callable from any thread while the Task is waiting, cancels the current wait.
		// The waiting task will return, if a cancellation of the task is wished the cancellation token has to be cancelled first!
		public void CancelCurrentWait() {
			UpdateWaitTime(0);
		}
	}
}
