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
			if (!res.Ok)
				OnLoadFailure?.Invoke(this, new LoadFailureEventArgs(res.Error));
			Log.Debug("Start song took {0}ms", timer.ElapsedMilliseconds);
		}

		public void StartTask(int seconds) {
			if (task != null)
				throw new InvalidOperationException();

			waitTask = new WaitTask(seconds * 1000, TokenSource.Token);
			task = Task.Run(() => { Run(TokenSource.Token); });
		}

		public void Run(CancellationToken token) {
			try {
				if (!waitTask.Run())
					return;
				RunActualTask(token);
			} catch (OperationCanceledException) { }
		}

		private R<SongAnalyzerResult, LocalStr> AnalyzeBackground(QueueItem queueItem, CancellationToken cancelled) {
			var timer = new Stopwatch();
			timer.Start();
			var res = new SongAnalyzerTask(queueItem, resourceResolver, player.FfmpegProducer).Run(cancelled);
			if (!res.Ok)
				return res.Error;

			var result = res.Value;

			if (queueItem.MetaData.ContainingPlaylistId != null &&
			    !ReferenceEquals(queueItem.AudioResource, result.Resource.BaseData)) {
				Log.Info("AudioResource was changed by loader, saving containing playlist");
				OnAudioResourceUpdated?.Invoke(this,
					new AudioResourceUpdatedEventArgs(queueItem, result.Resource.BaseData));
			}

			result.Resource.Meta = queueItem.MetaData;
			return result;
		}

		private E<LocalStr> Start(PlayResource resource, string restoredLink) {
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

			waitBeforePlayHandle.WaitOne();
			lock (playManagerLock) {
				if (token.IsCancellationRequested)
					return new LocalStr("Task cancelled");

				var resource = result.Value.Resource;
				var restoredLink = result.Value.RestoredLink.OkOr(null);

				return Start(resource, restoredLink);
			}
		}

		public void Cancel() {
			TokenSource.Cancel();
			waitTask.UpdateWaitTime(0);
			waitForStartPlayHandle.Set();
		}

		public void PlayWhenFinished() { waitForStartPlayHandle.Set(); }

		public void UpdateStartAnalyzeTime(int seconds) {
			waitTask.UpdateWaitTime(seconds * 1000);
		}
	}
}
