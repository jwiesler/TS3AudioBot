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

	public abstract class UniqueTaskHost<TTask, TValue> : UniqueHostBase<TTask>
		where TTask : class
		where TValue : class {
		// protected abstract void StartTask(TTask task);

		public event EventHandler<TTask> OnTaskStart;
		public event EventHandler<TTask> OnTaskStop;

		private void StartTask(TTask task) { OnTaskStart?.Invoke(this, task); }

		private void StopTask(TTask task) { OnTaskStop?.Invoke(this, task); }

		public abstract bool ShouldCreateNewTask(TTask task, TValue newValue);

		public void RunTaskFor(TValue value, Func<TValue, TTask> constructor) {
			if(value == default)
				throw new NullReferenceException();

			var task = Current;
			if (task != default && !ShouldCreateNewTask(task, value))
				return;
			var newTask = constructor(value);
			var ex = ExchangeTask(newTask, task);
			
			if(ex != task)
				throw new InvalidOperationException("Task was changed in the background");
			if(task != null)
				StopTask(task);
			StartTask(newTask);
		}

		public void ClearTask() {
			var task = ExchangeTask(default);
			if(task != null)
				StopTask(task);
		}

		public TTask RemoveFinishedTask() { return ExchangeTask(default); }
	}
}
