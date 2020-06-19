using System;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib.Helper;

namespace TS3AudioBot.Audio {
	public class LoadFailureEventArgs : EventArgs {
		public LocalStr Error { get; }

		public LoadFailureEventArgs(LocalStr error) { Error = error; }
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
		private readonly IPlayer player;
		private readonly ConfAudioVolume volumeConfig;
		private readonly ILoaderContext loaderContext;
		private readonly object playManagerLock;

		public QueueItem QueueItem { get; }

		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<LoadFailureEventArgs> OnLoadFailure;
		public event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		public StartSongTask(
			ILoaderContext loaderContext, IPlayer player, ConfAudioVolume volumeConfig, object playManagerLock,
			QueueItem queueItem) {
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
				Log.Error("Internal resource error: link is empty (resource:{0})", resource);
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			var gain = resource.BaseData.Gain ?? 0;
			Log.Debug("AudioResource start: {0} with gain {1}", resource, gain);
			var result = player.Play(resource, gain);

			if (!result) {
				Log.Error("Error return from player: {0}", result.Error);
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

			Log.Trace($"StartSongTask {GetHashCode()}: Finished analyze, waiting for play.");
			waitBeforePlayHandle.WaitOne();
			Log.Trace($"StartSongTask {GetHashCode()}: Finished waiting for play, checking cancellation.");
			lock (playManagerLock) {
				if (token.IsCancellationRequested) {
					Log.Trace($"StartSongTask {GetHashCode()}: Cancelled.");
					throw new TaskCanceledException();
				}

				Log.Trace($"StartSongTask {GetHashCode()}: Not cancelled, starting resource.");
				return StartResource(result.Value);
			}
		}

		public void Run(WaitHandle waitBeforePlayHandle, CancellationToken token) {
			var res = RunInternal(waitBeforePlayHandle, token);
			if (!res.Ok) {
				Log.Trace($"StartSongTask {GetHashCode()}: Failed ({res.Error}).");
				OnLoadFailure?.Invoke(this, new LoadFailureEventArgs(res.Error));
			} else {
				Log.Trace($"StartSongTask {GetHashCode()}: Finished.");
			}
		}
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

			Log.Trace($"StartSongTask {GetHashCode()}: Run in {ms}ms requested.");

			waitTask = new WaitTask(ms, TokenSource.Token);
			task = Task.Run(() => { Run(TokenSource.Token); });
		}

		private void Run(CancellationToken token) {
			try {
				Log.Trace($"StartSongTask {GetHashCode()}: Created, waiting.");
				waitTask.Run();
				Log.Trace($"StartSongTask {GetHashCode()}: Finished waiting, executing.");
				StartSongTask.Run(waitForStartPlayHandle, token);
			} catch (OperationCanceledException) {
				Log.Trace($"StartSongTask {GetHashCode()}: Cancelled by exception.");
			}
		}

		public void Cancel() {
			if (task == null)
				return;
			Log.Trace($"StartSongTask {GetHashCode()}: Cancellation requested.");
			TokenSource.Cancel();
			waitTask.CancelCurrentWait();
			waitForStartPlayHandle.Set();
		}

		public void PlayWhenFinished() {
			Log.Trace($"StartSongTask {GetHashCode()}: Play requested.");
			waitForStartPlayHandle.Set();
		}

		public void StartOrUpdateWaitTime(int ms) {
			if (Running) {
				Log.Trace($"StartSongTask {GetHashCode()}: Run in {ms}ms requested.");
				waitTask.UpdateWaitTime(ms);
			} else {
				StartTask(ms);
			}
		}

		public void StartOrStopWaiting() { StartOrUpdateWaitTime(0); }
	}

	public class NextSongHandler {
		public QueueItem NextSongPreparing { get; set; }
		public QueueItem NextSongShadow { get; set; }

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
			// Keep next song shadow until it was prepared once
			if (NextSongPreparing == NextSongShadow)
				NextSongShadow = null;
		}
	}

	public class StartSongTaskHost : UniqueTaskHost<StartSongTaskHandler> {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		public NextSongHandler NextSongHandler { get; } = new NextSongHandler();

		public void SetNextSong(QueueItem item, Func<QueueItem, StartSongTaskHandler> constructor) {
			if (Current != null && !NextSongHandler.ShouldBeReplacedNext(Current.StartSongTask.QueueItem, item))
				return;
			Log.Trace($"Setting next song to {item.AudioResource.ResourceTitle} ({item.GetHashCode()}).");
			RunTask(constructor(item));
			NextSongHandler.NextSongPreparing = item;
		}

		public void SetCurrentSong(QueueItem item, Func<QueueItem, StartSongTaskHandler> constructor) {
			NextSongHandler.ClearNextSong();
			if (Current != null && !NextSongHandler.ShouldBeReplaced(Current.StartSongTask.QueueItem, item))
				return;
			RunTask(constructor(item));
		}

		public new StartSongTaskHandler RemoveFinishedTask() {
			var task = base.RemoveFinishedTask();
			if (NextSongHandler.IsPreparingNextSong(task.StartSongTask.QueueItem)) {
				Log.Trace($"Load for {task.StartSongTask.QueueItem.GetHashCode()} finished, clearing next song.");
				NextSongHandler.ClearNextSong();
			}

			return task;
		}
	}
}
