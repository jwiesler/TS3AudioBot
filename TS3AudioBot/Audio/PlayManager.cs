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
	public class PlaybackStoppedEventArgs : EventArgs {
		public QueueItem Item { get; set; }

		public QueueItem NextShadow { get; set; }
	}

	/// <summary>Provides a convenient inferface for enqueing, playing and registering song events.</summary>
	public class PlayManager {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly ConfBot confBot;
		private readonly IPlayer playerConnection;
		private readonly ResolveContext resourceResolver;
		private readonly PlaylistManager playlistManager;
		private readonly Stats stats;

		private readonly StartSongTaskHost taskHost = new StartSongTaskHost();

		private NextSongHandler NextSongHandler => taskHost.NextSongHandler;

		public object Lock { get; } = new object();

		public PlayInfoEventArgs CurrentPlayData { get; private set; }
		public bool IsPlaying => CurrentPlayData != null;
		public bool AutoStartPlaying { get; set; } = true;
		public PlayQueue Queue { get; } = new PlayQueue();

		public event EventHandler<PlayInfoEventArgs> OnResourceUpdated;
		public event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<SongEndEventArgs> ResourceStopped;
		public event EventHandler<PlaybackStoppedEventArgs> PlaybackStopped;

		public PlayManager(ConfBot config, Player playerConnection, ResolveContext resourceResolver, Stats stats, PlaylistManager playlistManager) {
			confBot = config;
			this.playerConnection = playerConnection;
			this.resourceResolver = resourceResolver;
			this.stats = stats;
			this.playlistManager = playlistManager;

			taskHost.OnTaskStart += OnTaskStart;
			taskHost.OnTaskStop += OnTaskStop;

			playerConnection.FfmpegProducer.OnSongLengthParsed += (sender, args) => {
				lock (Lock) {
					if (taskHost.Current == null)
						return;
					taskHost.Current.StartOrUpdateWaitTime(GetAnalyzeTaskStartTime());
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
				taskHost.ClearTask();
			}
		}

		public E<LocalStr> Play() {
			lock (Lock) {
				StartPlayingCurrent();
				return R.Ok;
			}
		}

		public void OnQueueChanged() {
			UpdateNextSong();
		}

		public void EnqueueAsNextSong(QueueItem item) {
			lock (Lock) {
				Queue.InsertAfter(item, Queue.Index);
				OnQueueChanged();
			}
		}

		public void RemoveAt(int index) {
			lock (Lock) {
				Queue.Remove(index);
				OnQueueChanged();
			}
		}

		public void RemoveRange(int from, int to) {
			lock (Lock) {
				Queue.RemoveRange(from, to);
				OnQueueChanged();
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
				TryInitialStart();
				OnQueueChanged();
				return R.Ok;
			}
		}

		public E<LocalStr> Enqueue(IEnumerable<QueueItem> items) {
			lock (Lock) {
				Queue.Enqueue(items);
				TryInitialStart();
				OnQueueChanged();
				return R.Ok;
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
					var e = OnPlaybackEnded();
					if (e.Item == null) {
						Log.Trace("Could not recover from queue end.");
						NextSongShadow = e.NextShadow;
						return R.Ok;
					}

					Log.Trace("Recovered from queue end, queueing new song...");
					Queue.Enqueue(e.Item);
					StartPlayingCurrent();
					NextSongShadow = e.NextShadow;
				} else {
					StartPlayingCurrent();
				}

				return R.Ok;
			}
		}

		public E<LocalStr> Previous() {
			lock (Lock) {
				Log.Info("Previous song requested");
				return TryPrevious();
			}
		}

		private E<LocalStr> TryPrevious() {
			TryStopCurrentSong();
			if (!Queue.TryPrevious())
				return new LocalStr("No previous song.");

			var item = Queue.Current;
			StartAsync(item);
			return R.Ok;
		}

		private PlaybackStoppedEventArgs OnPlaybackEnded() {
			var e = new PlaybackStoppedEventArgs {
				NextShadow = taskHost.NextSongHandler.NextSongShadow
			};
			PlaybackStopped?.Invoke(this, e);
			return e;
		}

		private void TryStopCurrentSong() {
			if (taskHost.Current != null && NextSongHandler.IsPreparingCurrentSong(taskHost.Current.StartSongTask.QueueItem)) {
				Log.Debug("Stopping preparation of current song");
				taskHost.ClearTask();
			}

			if (CurrentPlayData != null) {
				Log.Debug("Stopping current song");
				playerConnection.Stop();
				CurrentPlayData = null;
				ResourceStopped?.Invoke(this, new SongEndEventArgs(true));
			}
		}

		// Try to start playing if not playing
		private void TryInitialStart() {
			if (IsPlaying || (taskHost.Current != null && NextSongHandler.IsPreparingCurrentSong(taskHost.Current.StartSongTask.QueueItem)) || !AutoStartPlaying)
				return;
			StartPlayingCurrent();
		}

		private void StartPlayingCurrent() {
			var item = Queue.Current;
			if (item == null)
				return;

			StartAsync(item);
		}

		private void OnBeforeResourceStarted(object sender, PlayInfoEventArgs e) {
			lock (Lock) {
				if (taskHost.Current == null || sender == null  || !ReferenceEquals(sender, taskHost.Current.StartSongTask))
					return;

				BeforeResourceStarted?.Invoke(this, e);
			}
		}

		private void OnAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			lock (Lock) {
				if (taskHost.Current == null || sender == null  || !ReferenceEquals(sender, taskHost.Current.StartSongTask))
					return;

				taskHost.RemoveFinishedTask();
				CurrentPlayData = e;
				AfterResourceStarted?.Invoke(this, e);
				UpdateNextSong();
			}
		}

		private void OnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			lock (Lock) {
				if (taskHost.Current == null || sender == null  || !ReferenceEquals(sender, taskHost.Current.StartSongTask) || e.QueueItem.MetaData.ContainingPlaylistId == null)
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
				if (taskHost.Current == null || sender == null || !ReferenceEquals(sender, taskHost.Current.StartSongTask))
					return;

				Log.Info("Could not play song {0} (reason: {1})", taskHost.Current.StartSongTask.QueueItem.AudioResource, e.Error);

				taskHost.RemoveFinishedTask();
				Next();
			}
		}
		
		private void StartAsync(QueueItem queueItem) {
			Log.Info($"Starting {queueItem.AudioResource.ResourceTitle}...");

			// Clear the next song to signal that this task is not the next song any more and mustn't get replaced by a next song preparation
			// Do this only if the next song being prepared is actually the one we want to play now
			if(ReferenceEquals(NextSongHandler.NextSongToPrepare, queueItem))
				NextSongHandler.ClearNextSong();
			taskHost.RunTaskFor(queueItem, CreateTask);
			taskHost.Current.StartOrStopWaiting();
			taskHost.Current.PlayWhenFinished();
		}

		// This song will be prepared as next song if the queue is empty
		// It will not be played automatically, it has to be queued in some way
		public QueueItem NextSongShadow {
			get => NextSongHandler.NextSongShadow;
			set {
				lock (Lock) {
					NextSongHandler.NextSongShadow = value;
					UpdateNextSong();
				}
			}
		}

		private void UpdateNextSong() {
			lock (Lock) {
				var next = Queue.Next ?? NextSongShadow;
				if (next == null) {
					if (taskHost.Current != null && NextSongHandler.IsPreparingNextSong(taskHost.Current.StartSongTask.QueueItem)) {
						taskHost.ClearTask();
						NextSongHandler.ClearNextSong();
					}
				} else {
					taskHost.SetNextSong(next, CreateTask);
				}
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
				TryStopCurrentSong();
				// Clear any task, the next one will be wrong anyways
				taskHost.ClearTask();
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

		protected StartSongTaskHandler CreateTask(QueueItem value) {
			return new StartSongTaskHandler(new StartSongTask(resourceResolver, playerConnection, confBot.Audio.Volume, Lock, value));
		}

		private void OnTaskStart(object sender, StartSongTaskHandler task) {
			var songTask = task.StartSongTask;
			songTask.BeforeResourceStarted += OnBeforeResourceStarted;
			songTask.AfterResourceStarted += OnAfterResourceStarted;
			songTask.OnAudioResourceUpdated += OnAudioResourceUpdated;
			songTask.OnLoadFailure += OnLoadFailure;
			
			if(playerConnection.Length != TimeSpan.Zero)
				task.StartTask(GetAnalyzeTaskStartTime());
		}

		private void OnTaskStop(object sender, StartSongTaskHandler task) {
			task.Cancel();

			var songTask = task.StartSongTask;
			songTask.BeforeResourceStarted -= OnBeforeResourceStarted;
			songTask.AfterResourceStarted -= OnAfterResourceStarted;
			songTask.OnAudioResourceUpdated -= OnAudioResourceUpdated;
			songTask.OnLoadFailure -= OnLoadFailure;
		}

		private const int MaxSecondsBeforeNextSong = 30;

		public static int GetTaskStartTimeSeconds(TimeSpan remainingSongTime) {
			int remainingTime = (int) remainingSongTime.TotalSeconds;
			return Math.Max(remainingTime - MaxSecondsBeforeNextSong, 0);
		}
	}
}
