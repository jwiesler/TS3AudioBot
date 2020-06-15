// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.Linq;
using TS3AudioBot.Config;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio {
	/// <summary>Provides a convenient inferface for enqueing, playing and registering song events.</summary>
	public class PlayManager : StartSongTaskHost {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly ConfBot confBot;
		private readonly Player playerConnection;
		private readonly ResolveContext resourceResolver;
		private readonly PlaylistManager playlistManager;
		private readonly Stats stats;

		public object Lock { get; } = new object();

		public PlayInfoEventArgs CurrentPlayData { get; private set; }
		public bool IsPlaying => CurrentPlayData != null;
		public bool AutoStartPlaying { get; set; } = true;
		public PlayQueue Queue { get; } = new PlayQueue();

		public event EventHandler<PlayInfoEventArgs> OnResourceUpdated;
		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<SongEndEventArgs> ResourceStopped;
		public event EventHandler PlaybackStopped;

		public PlayManager(ConfBot config, Player playerConnection, ResolveContext resourceResolver, Stats stats, PlaylistManager playlistManager) {
			confBot = config;
			this.playerConnection = playerConnection;
			this.resourceResolver = resourceResolver;
			this.stats = stats;
			this.playlistManager = playlistManager;

			playerConnection.FfmpegProducer.OnSongLengthParsed += (sender, args) => {
				lock (Lock) {
					if (Current == null)
						return;
					Log.Info("Preparing song analyzer... (OnSongLengthParsed)");
					Current.StartOrUpdateWaitTime(GetAnalyzeTaskStartTime());
				}
			};
		}

		private int GetAnalyzeTaskStartTime() {
			return GetTaskStartTimeSeconds(playerConnection.Length - playerConnection.Position) * 1000;
		}

		public void Clear() {
			lock (Lock) {
				Queue.Clear();
				TryStopCurrentSong();
				OnPlaybackEnded();
				ClearTask();
			}
		}

		public E<LocalStr> Play() {
			lock (Lock) {
				return TryPlay(true);
			}
		}

		public void EnqueueAsNextSong(QueueItem item) {
			lock (Lock) {
				Queue.InsertAfter(item, Queue.Index);
				UpdateNextSong();
			}
		}

		public void RemoveAt(int index) {
			lock (Lock) {
				Queue.Remove(index);
				UpdateNextSong();
			}
		}

		public void RemoveRange(int from, int to) {
			lock (Lock) {
				Queue.RemoveRange(from, to);
				UpdateNextSong();
			}
		}

		public E<LocalStr> Enqueue(string url, MetaData meta, string audioType = null) {
			var result = resourceResolver.Load(url, audioType);
			if (!result.Ok) {
				stats.TrackSongLoad(audioType, false, true);
				return result.Error;
			}

			return Enqueue(result.Value.BaseData, meta);
		}

		public E<LocalStr> Enqueue(AudioResource ar, MetaData meta) => Enqueue(new QueueItem(ar, meta));

		public E<LocalStr> Enqueue(IEnumerable<AudioResource> items, MetaData meta) {
			return Enqueue(items.Select(res => new QueueItem(res, meta)));
		}

		public E<LocalStr> Enqueue(QueueItem item) {
			lock (Lock) {
				Queue.Enqueue(item);
				var res = TryInitialStart(true);
				if(res.Ok)
					UpdateNextSong();
				return res;
			}
		}

		public E<LocalStr> Enqueue(IEnumerable<QueueItem> items) {
			lock (Lock) {
				Queue.Enqueue(items);
				var res = TryInitialStart(true);
				if(res.Ok)
					UpdateNextSong();
				return res;
			}
		}

		public E<LocalStr> Next(int count = 1) {
			if (count <= 0)
				return R.Ok;
			lock (Lock) {
				Log.Info("Skip {0} songs requested", count);
				if (!Queue.CanSkip(count))
					return new LocalStr("Can't skip that many songs.");

				TryStopCurrentSong();

				if (!Queue.Skip(count)) {
					OnPlaybackEnded();
					return R.Ok;
				}
				return TryPlay(false);
			}
		}

		public E<LocalStr> Previous() {
			lock (Lock) {
				Log.Info("Previous song requested");
				return TryPrevious(true);
			}
		}

		private E<LocalStr> TryPrevious(bool noSongIsError) {
			TryStopCurrentSong();
			if (!Queue.TryPrevious())
				return NoSongToPlay(noSongIsError);

			var item = Queue.Current;
			StartAsync(item);
			return R.Ok;
		}

		private void OnPlaybackEnded() {
			Log.Info("Playback ended for some reason");
			PlaybackStopped?.Invoke(this, EventArgs.Empty);
		}

		private void TryStopCurrentSong() {
			if (CurrentPlayData != null) {
				Log.Debug("Stopping current song");
				if(Current != null && IsCurrentSongPreparing())
					ClearTask();
				playerConnection.Stop();
				CurrentPlayData = null;
				ResourceStopped?.Invoke(this, new SongEndEventArgs(true));
			}
		}

		// Try to start playing if not playing
		private E<LocalStr> TryInitialStart(bool noSongIsError) {
			if (IsPlaying || !AutoStartPlaying)
				return R.Ok;
			return TryPlay(noSongIsError);
		}

		private E<LocalStr> NoSongToPlay(bool noSongIsError) {
			OnPlaybackEnded();
			if(noSongIsError)
				return new LocalStr("No song to play");
			return R.Ok;
		}

		private E<LocalStr> TryPlay(bool noSongIsError) {
			while (true) {
				var item = Queue.Current;
				if (item == null)
					return NoSongToPlay(noSongIsError);

				StartAsync(item);
				return R.Ok;
			}
		}

		private void OnBeforeResourceStarted(object sender, PlayInfoEventArgs e) {
			lock (Lock) {
				if (sender == null || !ReferenceEquals(sender, Current))
					return;

				BeforeResourceStarted?.Invoke(this, e);
			}
		}

		private void OnAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			lock (Lock) {
				if (sender == null || !ReferenceEquals(sender, Current))
					return;

				RemoveFinishedTask();
				CurrentPlayData = e;
				AfterResourceStarted?.Invoke(this, e);
				UpdateNextSong();
			}
		}

		private void OnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			lock (Lock) {
				if (sender == null || !ReferenceEquals(sender, Current))
					return;

				Log.Info("AudioResource was changed by loader, saving containing playlist");

				var modifyR = playlistManager.ModifyPlaylist(e.QueueItem.MetaData.ContainingPlaylistId, (list, _) => {
					foreach (var item in list.Items) {
						if (ReferenceEquals(item.AudioResource, e.QueueItem.AudioResource))
							item.AudioResource = e.Resource;
					}
				});
				if (!modifyR.Ok)
					Log.Warn($"Failed to save playlist {e.QueueItem.MetaData.ContainingPlaylistId}: {modifyR.Error}");
			}
		}

		private void OnLoadFailure(object sender, LoadFailureEventArgs e) {
			lock (Lock) {
				if (sender == null || !ReferenceEquals(sender, Current))
					return;

				Log.Info("Could not play song {0} (reason: {1})", Current.QueueItem.AudioResource, e.Error);

				RemoveFinishedTask();
				Next();
			}
		}
		
		private void StartAsync(QueueItem queueItem) {
			Log.Info($"Starting {queueItem.AudioResource.ResourceTitle} async...");
			RunTaskFor(queueItem);
			Current.StartOrStopWaiting();
			Current.PlayWhenFinished();
		}

		private void UpdateNextSong() {
			lock (Lock) {
				var next = Queue.Next;
				if (next == null) {
					if (!IsCurrentSongPreparing())
						ClearTask();
				} else {
					PrepareNextSong(next);
				}
			}
		}

		// ReSharper disable once MemberCanBePrivate.Global
		public void PrepareNextSong(QueueItem item) {
			lock (Lock) {
				SetNextSong(item);
			}
		}

		public void SongEndedEvent(object sender, EventArgs e) {
			lock (Lock) {
				Log.Debug("Song ended");
				ResourceStopped?.Invoke(this, new SongEndEventArgs(false));
				var result = Next();
				if (result.Ok)
					return;
				Log.Info("Automatically playing next song ended with error: {0}", result.Error);
			}
		}

		public void Stop() {
			lock (Lock) {
				Log.Debug("Song stopped");
				ResourceStopped?.Invoke(this, new SongEndEventArgs(true));
				playerConnection.Stop();

				TryStopCurrentSong();
			}
		}

		public void Update(SongInfoChanged newInfo) {
			lock (Lock) {
				Log.Info("Song info (title) updated");
				var data = CurrentPlayData;
				if (data is null)
					return;
				if (newInfo.Title != null)
					data.ResourceData = data.ResourceData.WithTitle(newInfo.Title);
				// further properties...
				OnResourceUpdated?.Invoke(this, data);
			}
		}

		public static TimeSpan? ParseStartTime(string[] attrs) {
			TimeSpan? res = null;
			if (attrs != null && attrs.Length != 0) {
				foreach (var attr in attrs) {
					if (attr.StartsWith("@")) {
						res = TextUtil.ParseTime(attr.Substring(1));
					}
				}
			}

			return res;
		}

		protected override StartSongTask CreateTask(QueueItem value) {
			return new StartSongTask(resourceResolver, playerConnection, confBot, Lock, value);
		}

		protected override void StartTask(StartSongTask task) {
			task.BeforeResourceStarted += OnBeforeResourceStarted;
			task.AfterResourceStarted += OnAfterResourceStarted;
			task.OnAudioResourceUpdated += OnAudioResourceUpdated;
			task.OnLoadFailure += OnLoadFailure;
			
			if(playerConnection.FfmpegProducer.Length != TimeSpan.Zero)
				task.StartTask(GetAnalyzeTaskStartTime());
		}

		protected override void StopTask(StartSongTask task) {
			base.StopTask(task);
			if (task == null)
				return;

			task.BeforeResourceStarted -= OnBeforeResourceStarted;
			task.AfterResourceStarted -= OnAfterResourceStarted;
			task.OnAudioResourceUpdated -= OnAudioResourceUpdated;
			task.OnLoadFailure -= OnLoadFailure;
		}

		private const int MaxSecondsBeforeNextSong = 30;

		public static int GetTaskStartTimeSeconds(TimeSpan remainingSongTime) {
			int remainingTime = (int) remainingSongTime.TotalSeconds;
			return Math.Max(remainingTime - MaxSecondsBeforeNextSong, 0);
		}
	}
}
