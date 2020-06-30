using System;

namespace TS3AudioBot.Audio.Preparation {
	public abstract class StartSongTaskHostBase : IStartSongTaskHost {
		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<LoadFailureTaskEventArgs> OnLoadFailure;
		public event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		public abstract QueueItem PreparingItem { get; }
		private QueueItem nextPreparingItem;

		public bool HasTask => PreparingItem != null;
		public bool IsCurrentResource => !IsNextResource;
		public bool IsNextResource => ReferenceEquals(PreparingItem, nextPreparingItem);

		protected void InvokeBeforeResourceStarted(object sender, PlayInfoEventArgs e) {
			BeforeResourceStarted?.Invoke(sender, e);
		}

		protected void InvokeAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			RemoveFinishedTask();
			AfterResourceStarted?.Invoke(sender, e);
		}

		protected void InvokeOnLoadFailure(object sender, LoadFailureTaskEventArgs e) {
			RemoveFinishedTask();
			OnLoadFailure?.Invoke(sender, e);
		}

		protected void InvokeOnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			OnAudioResourceUpdated?.Invoke(sender, e);
		}

		public void SetNextSong(QueueItem item, TimeSpan? remaining) {
			if (HasTask && (IsCurrentResource || ReferenceEquals(PreparingItem, item))) {
				if(ReferenceEquals(PreparingItem, item) && remaining.HasValue)
					UpdateRemaining(remaining.Value);
				return;
			}

			nextPreparingItem = item;
			if (HasTask)
				CancelTask();
			SetTask(item, remaining);
		}

		public void SetCurrentSong(QueueItem item, TimeSpan? remaining) {
			nextPreparingItem = null;
			if (HasTask && ReferenceEquals(PreparingItem, item)) {
				if(remaining.HasValue)
					UpdateRemaining(remaining.Value);
				return;
			}

			if (HasTask)
				CancelTask();
			SetTask(item, remaining);
		}

		public void Clear() {
			ClearTask();
			nextPreparingItem = null;
		}

		public void ClearTask() {
			if (HasTask)
				CancelTask();
		}

		public abstract void PlayCurrentWhenFinished();
		public abstract void UpdateRemaining(TimeSpan remaining);
		protected abstract void RemoveFinishedTask();
		protected abstract void CancelTask();
		protected abstract void SetTask(QueueItem item, TimeSpan? remaining);
	}
}
