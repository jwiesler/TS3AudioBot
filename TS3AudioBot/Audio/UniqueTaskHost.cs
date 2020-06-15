using System;
using System.Threading;

namespace TS3AudioBot.Audio
{
	public class UniqueHostBase<T> where T : class {
		private T current;
		protected T Current => current;
		
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
		protected abstract TTask CreateTask(TValue value);
		protected abstract void StartTask(TTask task);
		protected abstract void StopTask(TTask task);
		protected abstract bool ShouldCreateNewTask(TTask task, TValue newValue);

		protected void RunTaskFor(TValue value) {
			if(value == default)
				throw new NullReferenceException();

			var task = Current;
			if (task != default && !ShouldCreateNewTask(task, value))
				return;
			var newTask = CreateTask(value);
			var ex = ExchangeTask(newTask, task);
			
			if(ex != task)
				throw new InvalidOperationException("Task was changed in the background");
			StopTask(task);
			StartTask(newTask);
		}

		protected void ClearTask() {
			var task = ExchangeTask(default);
			StopTask(task);
		}

		protected TTask RemoveFinishedTask() { return ExchangeTask(default); }
	}
}
