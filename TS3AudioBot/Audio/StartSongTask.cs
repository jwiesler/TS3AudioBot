using System;
using System.Diagnostics;
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

		private readonly Player player;
		private readonly ConfBot config;
		private readonly ResolveContext resourceResolver;
		private readonly object playManagerLock;
		private readonly EventWaitHandle waitForStartPlayHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
		
		private CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();
		private WaitTask waitTask;
		private Task task;

		public QueueItem QueueItem { get; }

		public bool Running => task != null;

		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<LoadFailureEventArgs> OnLoadFailure;
		public event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		public StartSongTask(ResolveContext resourceResolver, Player player, ConfBot config, object playManagerLock, QueueItem queueItem) {
			this.resourceResolver = resourceResolver;
			this.player = player;
			this.config = config;
			this.playManagerLock = playManagerLock;
			QueueItem = queueItem;
		}

		private void RunActualTask(CancellationToken token) {
			var timer = new Stopwatch();
			timer.Start();
			var res = StartBackground(QueueItem, waitForStartPlayHandle, token);
			if (!res.Ok) {
				Log.Trace($"Task {GetHashCode()}: Failed ({res.Error})");
				OnLoadFailure?.Invoke(this, new LoadFailureEventArgs(res.Error));
			} else {
				Log.Trace($"Task {GetHashCode()}: Start song took {timer.ElapsedMilliseconds}ms");
			}
		}

		public void StartTask(int ms) {
			if (task != null)
				throw new InvalidOperationException("Task was already running");

			waitTask = new WaitTask(ms, TokenSource.Token);
			task = Task.Run(() => { Run(TokenSource.Token); });
		}

		public void Run(CancellationToken token) {
			try {
				waitTask.Run();
				RunActualTask(token);
			} catch (OperationCanceledException) { }
		}

		private R<SongAnalyzerResult, LocalStr> AnalyzeBackground(QueueItem queueItem, CancellationToken cancelled) {
			var res = new SongAnalyzerTask(queueItem, resourceResolver, player.FfmpegProducer).Run(cancelled);
			if (!res.Ok)
				return res.Error;

			var result = res.Value;

			if (queueItem.MetaData.ContainingPlaylistId != null &&
			    !ReferenceEquals(queueItem.AudioResource, result.Resource.BaseData)) {
				OnAudioResourceUpdated?.Invoke(this,
					new AudioResourceUpdatedEventArgs(queueItem, result.Resource.BaseData));
			}

			result.Resource.Meta = queueItem.MetaData;
			return result;
		}

		private E<LocalStr> StartResource(PlayResource resource, string restoredLink) {
			Log.Trace("Starting resource...");

			var playInfo = new PlayInfoEventArgs(resource.Meta.ResourceOwnerUid, resource, restoredLink);
			BeforeResourceStarted?.Invoke(this, playInfo);
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

			player.Volume = Tools.Clamp(player.Volume, config.Audio.Volume.Min, config.Audio.Volume.Max);
			AfterResourceStarted?.Invoke(this, playInfo);
			return R.Ok;
		}

		private E<LocalStr> StartBackground(
			QueueItem queueItem, WaitHandle waitBeforePlayHandle, CancellationToken token) {
			var result = AnalyzeBackground(queueItem, token);
			if (!result.Ok)
				return result;

			Log.Trace($"Task {GetHashCode()}: Finished analyze, waiting for play...");
			waitBeforePlayHandle.WaitOne();
			Log.Trace($"Task {GetHashCode()}: Finished waiting for play, checking cancellation");
			lock (playManagerLock) {
				if (token.IsCancellationRequested) {
					Log.Trace($"Task {GetHashCode()}: Cancelled");
					throw new TaskCanceledException();
				}

				var resource = result.Value.Resource;
				var restoredLink = result.Value.RestoredLink.OkOr(null);

				Log.Info($"Task {GetHashCode()}: Not cancelled, starting resource");
				return StartResource(resource, restoredLink);
			}
		}

		public void Cancel() {
			if (task == null)
				return;
			TokenSource.Cancel();
			waitTask.CancelCurrentWait();
			waitForStartPlayHandle.Set();
		}

		public void PlayWhenFinished() { waitForStartPlayHandle.Set(); }

		public void StartOrUpdateWaitTime(int ms) {
			if(Running)
				waitTask.UpdateWaitTime(ms);
			else
				StartTask(ms);
		}

		public void StartOrStopWaiting() {
			if(Running)
				waitTask.CancelCurrentWait();
			else
				StartTask(0);
		}
	}

	public abstract class StartSongTaskHost : UniqueTaskHost<StartSongTask, QueueItem> {
		private QueueItem nextSongToPrepare;

		protected bool IsCurrentSongPreparing() {
			return !ReferenceEquals(Current.QueueItem, nextSongToPrepare);
		}

		protected override bool ShouldCreateNewTask(StartSongTask task, QueueItem newValue) {
			if (ReferenceEquals(task.QueueItem, newValue))
				return false;

			// are we preparing the next song?
			return ReferenceEquals(nextSongToPrepare, task.QueueItem);
		}

		protected void SetNextSong(QueueItem item) {
			RunTaskFor(item);
			nextSongToPrepare = item;
		}

		protected override void StopTask(StartSongTask task) {
			task?.Cancel();
		}
	}
}
