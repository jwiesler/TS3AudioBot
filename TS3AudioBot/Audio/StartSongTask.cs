using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib.Helper;

namespace TS3AudioBot.Audio {
	public class LoadFailureEventArgs : EventArgs {
		public LocalStr Error { get; }
		public QueueItem QueueItem { get; }

		public LoadFailureEventArgs(LocalStr error, QueueItem queueItem) {
			Error = error;
			QueueItem = queueItem;
		}
	}

	public class AudioResourceUpdatedEventArgs : EventArgs {
		public QueueItem QueueItem { get; }
		public AudioResource Resource { get; }

		public AudioResourceUpdatedEventArgs(QueueItem queueItem, AudioResource resource) {
			QueueItem = queueItem;
			Resource = resource;
		}
	}

	public class StartSongTask {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private static int _nextId = -1;

		private readonly IPlayer player;
		private readonly ConfAudioVolume volumeConfig;
		private readonly ILoaderContext loaderContext;
		private readonly object playManagerLock;

		public int Id { get; }

		public QueueItem QueueItem { get; }

		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<LoadFailureEventArgs> OnLoadFailure;
		public event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		public StartSongTask(
			ILoaderContext loaderContext, IPlayer player, ConfAudioVolume volumeConfig, object playManagerLock,
			QueueItem queueItem) {
			Id = Interlocked.Increment(ref _nextId);
			this.loaderContext = loaderContext;
			this.player = player;
			this.volumeConfig = volumeConfig;
			this.playManagerLock = playManagerLock;
			QueueItem = queueItem;
		}

		private R<SongAnalyzerResult, LocalStr> AnalyzeBackground(QueueItem queueItem, CancellationToken cancelled) {
			return new SongAnalyzerTask(queueItem, loaderContext, player).Run(cancelled);
		}

		public E<LocalStr> StartResource(PlayResource resource) {
			if (string.IsNullOrWhiteSpace(resource.PlayUri)) {
				Log.Error($"{this}: Internal resource error: link is empty (resource:{resource})");
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			var gain = resource.BaseData.Gain ?? 0;
			Log.Debug($"{this}: Starting {resource} with gain {gain}");
			var result = player.Play(resource, gain);

			if (!result) {
				Log.Error($"{this}: Error return from player: {result.Error}");
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			player.Volume = Tools.Clamp(player.Volume, volumeConfig.Min, volumeConfig.Max);
			return R.Ok;
		}

		public E<LocalStr> StartResource(SongAnalyzerResult result) {
			var resource = result.Resource;
			var restoredLink = result.RestoredLink.OkOr(null);

			var playInfo = new PlayInfoEventArgs(resource.Meta.ResourceOwnerUid, resource, restoredLink);
			BeforeResourceStarted?.Invoke(this, playInfo);

			var res = StartResource(resource);
			if (!res.Ok)
				return res;

			AfterResourceStarted?.Invoke(this, playInfo);
			return res;
		}

		private void InvokeOnResourceChanged(QueueItem queueItem, AudioResource resource) {
			if (!ReferenceEquals(queueItem.AudioResource, resource)) {
				OnAudioResourceUpdated?.Invoke(this,
					new AudioResourceUpdatedEventArgs(queueItem, resource));
			}
		}

		public E<LocalStr> RunInternal(WaitHandle waitBeforePlayHandle, CancellationToken token) {
			var result = AnalyzeBackground(QueueItem, token);
			if (!result.Ok)
				return result;

			InvokeOnResourceChanged(QueueItem, result.Value.Resource.BaseData);

			Log.Trace($"{this}: Finished analyze, waiting for play.");
			waitBeforePlayHandle.WaitOne();
			Log.Trace($"{this}: Finished waiting for play, checking cancellation.");
			lock (playManagerLock) {
				if (token.IsCancellationRequested) {
					Log.Trace($"{this}: Cancelled.");
					throw new TaskCanceledException();
				}

				Log.Trace($"{this}: Not cancelled, starting resource.");
				return StartResource(result.Value);
			}
		}

		public void Run(WaitHandle waitBeforePlayHandle, CancellationToken token) {
			var res = RunInternal(waitBeforePlayHandle, token);
			if (!res.Ok) {
				Log.Trace($"{this}: Failed ({res.Error}).");
				OnLoadFailure?.Invoke(this, new LoadFailureEventArgs(res.Error, QueueItem));
			} else {
				Log.Trace($"{this}: Finished.");
			}
		}

		public override string ToString() { return $"StartSongTask {Id}"; }
	}

	public class StartSongTaskHandler {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly EventWaitHandle waitForStartPlayHandle = new EventWaitHandle(false, EventResetMode.AutoReset);

		private CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();
		private WaitTask waitTask;
		private Task task;

		public bool Running => task != null;

		public StartSongTask StartSongTask { get; }

		public StartSongTaskHandler(StartSongTask startSongTask) { StartSongTask = startSongTask; }

		public void StartTask(int ms) {
			if (task != null)
				throw new InvalidOperationException("Task was already running");

			Log.Trace($"{StartSongTask}: Run in {ms}ms requested.");

			waitTask = new WaitTask(ms, TokenSource.Token);
			task = Task.Run(() => { Run(TokenSource.Token); });
		}

		private void Run(CancellationToken token) {
			try {
				Log.Trace($"{StartSongTask}: Created, waiting.");
				waitTask.Run();
				Log.Trace($"{StartSongTask}: Finished waiting, executing.");
				StartSongTask.Run(waitForStartPlayHandle, token);
			} catch (OperationCanceledException) {
				Log.Trace($"{StartSongTask}: Cancelled by exception.");
			}
		}

		public void Cancel() {
			if (task == null)
				return;
			Log.Trace($"{StartSongTask}: Cancellation requested.");
			TokenSource.Cancel();
			waitTask.CancelCurrentWait();
			waitForStartPlayHandle.Set();
		}

		public void PlayWhenFinished() {
			Log.Trace($"{StartSongTask}: Play requested.");
			waitForStartPlayHandle.Set();
		}

		public void StartOrUpdateWaitTime(int ms) {
			if (Running) {
				Log.Trace($"{StartSongTask}: Run in {ms}ms requested.");
				waitTask.UpdateWaitTime(ms);
			} else {
				StartTask(ms);
			}
		}

		public void StartOrStopWaiting() { StartOrUpdateWaitTime(0); }
	}

	public class NextSongHandler {
		public QueueItem NextSongPreparing { get; set; }

		public bool IsPreparingNextSong(QueueItem current) { return ReferenceEquals(current, NextSongPreparing); }

		public bool IsPreparingCurrentSong(QueueItem current) { return !ReferenceEquals(current, NextSongPreparing); }

		public static bool ShouldBeReplaced(QueueItem current, QueueItem newValue) {
			return !ReferenceEquals(current, newValue);
		}

		public bool ShouldBeReplacedNext(QueueItem current, QueueItem newValue) {
			return ShouldBeReplaced(current, newValue) && IsPreparingNextSong(current);
		}

		public void ClearNextSong() {
			NextSongPreparing = null;
		}
	}

	public class LoadFailureTaskEventArgs : LoadFailureEventArgs {
		public bool IsCurrentResource { get; }

		public LoadFailureTaskEventArgs(LocalStr error, QueueItem queueItem, bool isCurrentResource) : base(error, queueItem) { IsCurrentResource = isCurrentResource; }
	}

	public interface IStartSongTaskHost {
		event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;

		// Current task is already removed if those two are called
		event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		event EventHandler<LoadFailureTaskEventArgs> OnLoadFailure;

		event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		bool HasTask { get; }
		bool IsCurrentResource { get; }
		bool IsNextResource { get; }

		void SetNextSong(QueueItem item, TimeSpan? remaining);
		void SetCurrentSong(QueueItem item, TimeSpan? remaining);

		void PlayCurrentWhenFinished();
		void UpdateRemaining(TimeSpan remaining);

		void Clear();
		void ClearTask();
	}

	public class StartSongTaskHost : UniqueTaskHost<StartSongTaskHandler>, IStartSongTaskHost {
		private static readonly Logger Log = LogManager.GetCurrentClassLogger();
		private readonly Func<QueueItem, StartSongTaskHandler> constructor;

		public bool HasTask => Current != null;
		public bool IsCurrentResource => nextSongHandler.IsPreparingCurrentSong(Current.StartSongTask.QueueItem);
		public bool IsNextResource => nextSongHandler.IsPreparingNextSong(Current.StartSongTask.QueueItem);
		
		private readonly NextSongHandler nextSongHandler = new NextSongHandler();

		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<LoadFailureTaskEventArgs> OnLoadFailure;
		public event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		public StartSongTaskHost(Func<QueueItem, StartSongTaskHandler> constructor) { this.constructor = constructor; }

		private void InvokeBeforeResourceStarted(object sender, PlayInfoEventArgs e) {
			if (!ReferenceEquals(sender, Current.StartSongTask))
				return;
			BeforeResourceStarted?.Invoke(sender, e);
		}

		private void InvokeAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			if (!ReferenceEquals(sender, Current.StartSongTask))
				return;
			RemoveFinishedTask();
			AfterResourceStarted?.Invoke(sender, e);
		}

		private void InvokeOnLoadFailure(object sender, LoadFailureEventArgs e) {
			if (!ReferenceEquals(sender, Current.StartSongTask))
				return;
			var isCurrent = IsCurrentResource;
			RemoveFinishedTask();
			OnLoadFailure?.Invoke(sender, new LoadFailureTaskEventArgs(e.Error, e.QueueItem, isCurrent));
		}

		private void InvokeOnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			if (!ReferenceEquals(sender, Current.StartSongTask))
				return;
			OnAudioResourceUpdated?.Invoke(sender, e);
		}

		protected override void StartTask(StartSongTaskHandler task) {
			var songTask = task.StartSongTask;
			songTask.BeforeResourceStarted += InvokeBeforeResourceStarted;
			songTask.AfterResourceStarted += InvokeAfterResourceStarted;
			songTask.OnAudioResourceUpdated += InvokeOnAudioResourceUpdated;
			songTask.OnLoadFailure += InvokeOnLoadFailure;

			base.StartTask(task);
		}

		protected override void StopTask(StartSongTaskHandler task) {
			task.Cancel();

			var songTask = task.StartSongTask;
			songTask.BeforeResourceStarted -= InvokeBeforeResourceStarted;
			songTask.AfterResourceStarted -= InvokeAfterResourceStarted;
			songTask.OnAudioResourceUpdated -= InvokeOnAudioResourceUpdated;
			songTask.OnLoadFailure -= InvokeOnLoadFailure;

			base.StopTask(task);
		}

		public void Clear() {
			ClearTask();
			nextSongHandler.ClearNextSong();
		}

		public void SetNextSong(QueueItem item, TimeSpan? remaining) {
			if (Current != null && !nextSongHandler.ShouldBeReplacedNext(Current.StartSongTask.QueueItem, item))
				return;
			Log.Trace($"Setting next song to {item.AudioResource.ResourceTitle} ({item.GetHashCode()}).");
			RunTask(constructor(item));
			nextSongHandler.NextSongPreparing = item;
			StartCurrentIfRemaining(remaining);
		}

		public void SetCurrentSong(QueueItem item, TimeSpan? remaining) {
			nextSongHandler.ClearNextSong();
			if (Current != null && !NextSongHandler.ShouldBeReplaced(Current.StartSongTask.QueueItem, item))
				return;
			RunTask(constructor(item));
			StartCurrentIfRemaining(remaining);
		}

		public void UpdateRemaining(TimeSpan remaining) {
			StartCurrentTaskIn((int)remaining.TotalMilliseconds);
		}

		private void StartCurrentIfRemaining(TimeSpan? remaining) {
			if(remaining.HasValue)
				StartCurrentTaskIn(GetTaskStartTimeMs(remaining.Value));
		}

		public void PlayCurrentWhenFinished() {
			Current.PlayWhenFinished();
		}

		private void StartCurrentTaskIn(int ms) {
			Current.StartOrUpdateWaitTime(ms);
		}

		private const int MaxMsBeforeNextSong = 30000;

		public static int GetTaskStartTimeMs(TimeSpan remainingSongTime) {
			int remainingTimeMs = (int) remainingSongTime.TotalMilliseconds;
			return Math.Max(remainingTimeMs - MaxMsBeforeNextSong, 0);
		}
	}
}
