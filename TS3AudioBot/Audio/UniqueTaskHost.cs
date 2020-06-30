using System;
using System.Threading;

namespace TS3AudioBot.Audio
{
	public class UniqueHostBase<T> where T : class {
		private T current;
		public T Current => current;
		
		protected T ExchangeTask(T newTask) {
			return Interlocked.Exchange(ref current, newTask);
		}

		protected T ExchangeTask(T newTask, T expected) {
			return Interlocked.CompareExchange(ref current, newTask, expected);
		}
	}

	public abstract class UniqueTaskHost<TTask> : UniqueHostBase<TTask>
		where TTask : class {

		protected virtual void StartTask(TTask task) {}

		protected virtual void StopTask(TTask task) {}

		public void RunTask(TTask task) {
			if(task == default)
				throw new NullReferenceException();

			var oldTask = ExchangeTask(task);
			
			if(oldTask != null)
				StopTask(oldTask);
			StartTask(task);
		}

		public void ClearTask() {
			var task = ExchangeTask(default);
			if(task != null)
				StopTask(task);
		}

		public TTask RemoveFinishedTask() { return ExchangeTask(default); }
	}
}
