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

		private Task Task { get; set; }
		private EventWaitHandle WaitForStartPlayHandle { get; } = new EventWaitHandle(false, EventResetMode.AutoReset);
		private CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();
		private WaitTask waitTask;

		private Player Player { get; }
		private ConfBot Config { get; }
		public QueueItem QueueItem { get; }

		private SongAnalyzer SongAnalyzer { get; }
		private object PlayManagerLock { get; }
		public bool Running => Task != null;

		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<LoadFailureEventArgs> OnLoadFailure;
		public event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		public StartSongTask(
			Player player, ConfBot config, QueueItem queueItem, SongAnalyzer songAnalyzer, object playManagerLock) {
			Player = player;
			Config = config;
			QueueItem = queueItem;
			SongAnalyzer = songAnalyzer;
			PlayManagerLock = playManagerLock;
		}

		private void RunActualTask(CancellationToken token) {
			var timer = new Stopwatch();
			timer.Start();
			var res = StartBackground(QueueItem, WaitForStartPlayHandle, token);
			if (!res.Ok)
				OnLoadFailure?.Invoke(this, new LoadFailureEventArgs(res.Error));
			Log.Debug("Start song took {0}ms", timer.ElapsedMilliseconds);
		}

		public void StartTask(int seconds) {
			if (Task != null)
				throw new InvalidOperationException();

			waitTask = new WaitTask(seconds * 1000, TokenSource.Token);
			Task = Task.Run(() => { Run(TokenSource.Token); });
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
			var res = SongAnalyzer.RunSync(queueItem, cancelled);
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
			var result = Player.Play(resource, gain);

			if (!result) {
				Log.Error("Error return from player: {0}", result.Error);
				return new LocalStr(strings.error_playmgr_internal_error);
			}

			Player.Volume = Tools.Clamp(Player.Volume, Config.Audio.Volume.Min, Config.Audio.Volume.Max);
			AfterResourceStarted?.Invoke(this, playInfo);
			return R.Ok;
		}

		private E<LocalStr> StartBackground(
			QueueItem queueItem, WaitHandle waitBeforePlayHandle, CancellationToken token) {
			var result = AnalyzeBackground(queueItem, token);
			if (!result.Ok)
				return result;

			waitBeforePlayHandle.WaitOne();
			lock (PlayManagerLock) {
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
			WaitForStartPlayHandle.Set();
		}

		public void PlayWhenFinished() { WaitForStartPlayHandle.Set(); }

		public void UpdateStartAnalyzeTime(int seconds) {
			waitTask.UpdateWaitTime(seconds * 1000);
		}
	}
}
