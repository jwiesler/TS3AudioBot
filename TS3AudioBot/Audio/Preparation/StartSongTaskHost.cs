using System;
using NLog;
using TS3AudioBot.Localization;

namespace TS3AudioBot.Audio.Preparation {
	public class LoadFailureTaskEventArgs : LoadFailureEventArgs {
		public bool IsCurrentResource { get; }

		public LoadFailureTaskEventArgs(LocalStr error, QueueItem queueItem, bool isCurrentResource) :
			base(error, queueItem) {
			IsCurrentResource = isCurrentResource;
		}
	}

	public class StartSongTaskHost : StartSongTaskHostBase {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();
		private readonly Func<QueueItem, StartSongTaskHandler> constructor;

		private StartSongTaskHandler currentTask;

		public StartSongTaskHost(Func<QueueItem, StartSongTaskHandler> constructor) { this.constructor = constructor; }

		public override QueueItem PreparingItem => currentTask?.StartSongTask.QueueItem;

		private new void InvokeBeforeResourceStarted(object sender, PlayInfoEventArgs e) {
			if (!ReferenceEquals(sender, currentTask.StartSongTask))
				return;
			base.InvokeBeforeResourceStarted(sender, e);
		}

		private new void InvokeAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			if (!ReferenceEquals(sender, currentTask.StartSongTask))
				return;
			base.InvokeBeforeResourceStarted(sender, e);
		}

		private void InvokeOnLoadFailure(object sender, LoadFailureEventArgs e) {
			if (!ReferenceEquals(sender, currentTask.StartSongTask))
				return;
			var isCurrent = IsCurrentResource;
			RemoveFinishedTask();
			base.InvokeOnLoadFailure(sender, new LoadFailureTaskEventArgs(e.Error, e.QueueItem, isCurrent));
		}

		private new void InvokeOnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			if (!ReferenceEquals(sender, currentTask.StartSongTask))
				return;
			base.InvokeOnAudioResourceUpdated(sender, e);
		}

		private void AddListeners(StartSongTaskHandler task) {
			var songTask = task.StartSongTask;
			songTask.BeforeResourceStarted += InvokeBeforeResourceStarted;
			songTask.AfterResourceStarted += InvokeAfterResourceStarted;
			songTask.OnAudioResourceUpdated += InvokeOnAudioResourceUpdated;
			songTask.OnLoadFailure += InvokeOnLoadFailure;
		}

		private void RemoveListeners(StartSongTaskHandler task) {
			var songTask = task.StartSongTask;
			songTask.BeforeResourceStarted -= InvokeBeforeResourceStarted;
			songTask.AfterResourceStarted -= InvokeAfterResourceStarted;
			songTask.OnAudioResourceUpdated -= InvokeOnAudioResourceUpdated;
			songTask.OnLoadFailure -= InvokeOnLoadFailure;
		}

		public override void UpdateRemaining(TimeSpan remaining) { StartCurrentTaskIn((int) remaining.TotalMilliseconds); }

		protected override void RemoveFinishedTask() {
			currentTask = null;
		}

		protected override void CancelTask() {
			RemoveListeners(currentTask);
			currentTask.Cancel();
			currentTask = null;
		}

		protected override void SetTask(QueueItem item, TimeSpan? remaining) {
			currentTask = constructor(item);
			AddListeners(currentTask);
			StartCurrentIfRemaining(remaining);
		}

		private void StartCurrentIfRemaining(TimeSpan? remaining) {
			if (remaining.HasValue)
				StartCurrentTaskIn(GetTaskStartTimeMs(remaining.Value));
		}

		public override void PlayCurrentWhenFinished() { currentTask.PlayWhenFinished(); }

		private void StartCurrentTaskIn(int ms) { currentTask.StartOrUpdateWaitTime(ms); }

		private const int MaxMsBeforeNextSong = 30000;

		private static int GetTaskStartTimeMs(TimeSpan remainingSongTime) {
			int remainingTimeMs = (int) remainingSongTime.TotalMilliseconds;
			return Math.Max(remainingTimeMs - MaxMsBeforeNextSong, 0);
		}
	}
}
