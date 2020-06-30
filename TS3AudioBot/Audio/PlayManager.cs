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
using TS3AudioBot.Audio.Preparation;
using TS3AudioBot.Config;
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

		private readonly ConfAudioVolume confAudioVolume;
		private readonly IPlayer playerConnection;
		private readonly ILoaderContext resourceResolver;
		private readonly PlaylistManager playlistManager;

		private readonly IStartSongTaskHost taskHost;

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

		// Needed for the injector cancer
		public PlayManager(
			ConfBot config, Player playerConnection, ResolveContext resourceResolver, PlaylistManager playlistManager)
			: this(config.Audio.Volume, playerConnection, resourceResolver, playlistManager) {
			taskHost = new StartSongTaskHost(CreateTask);
			Init();
		}

		public PlayManager(
			ConfAudioVolume confAudioVolume, IPlayer playerConnection, ILoaderContext resourceResolver, PlaylistManager playlistManager, IStartSongTaskHost taskHost)
			: this(confAudioVolume, playerConnection, resourceResolver, playlistManager) {
			this.taskHost = taskHost;
			Init();
		}

		private PlayManager(ConfAudioVolume confAudioVolume, IPlayer playerConnection, ILoaderContext resourceResolver, PlaylistManager playlistManager) {
			this.confAudioVolume = confAudioVolume;
			this.playerConnection = playerConnection;
			this.resourceResolver = resourceResolver;
			this.playlistManager = playlistManager;
		}

		private void Init() {
			playerConnection.OnSongEnd += SongEndedEvent;
			playerConnection.OnSongUpdated += (s, e) => Update(e);

			taskHost.BeforeResourceStarted += OnBeforeResourceStarted;
			taskHost.AfterResourceStarted += OnAfterResourceStarted;
			taskHost.OnAudioResourceUpdated += OnAudioResourceUpdated;
			taskHost.OnLoadFailure += OnLoadFailure;

			playerConnection.OnSongLengthParsed += (sender, args) => {
				lock (Lock) {
					if (!taskHost.HasTask || !playerConnection.Remaining.HasValue)
						return;
					taskHost.UpdateRemaining(playerConnection.Remaining.Value);
				}
			};
		}

		public void Clear() {
			lock (Lock) {
				Queue.Clear();
				TryStopCurrentSong();
				OnPlaybackEnded();
				taskHost.Clear();
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
				NextShadow = nextSongShadow
			};
			PlaybackStopped?.Invoke(this, e);
			return e;
		}

		private void TryStopCurrentSong() {
			if (taskHost.HasTask && taskHost.IsCurrentResource) {
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
			if (IsPlaying || (taskHost.HasTask && taskHost.IsCurrentResource) || !AutoStartPlaying)
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
				BeforeResourceStarted?.Invoke(this, e);
			}
		}

		private void OnAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			lock (Lock) {
				CurrentPlayData = e;
				AfterResourceStarted?.Invoke(this, e);
				UpdateNextSong();
			}
		}

		private void OnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			lock (Lock) {
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

		private void OnLoadFailure(object sender, LoadFailureTaskEventArgs e) {
			lock (Lock) {
				Log.Info("Could not load song {0} (reason: {1})", e.QueueItem.AudioResource, e.Error);
				
				if (e.IsCurrentResource)
					Next();
				else
					PrepareNextSongAfterNextSongLoadFailure(e.QueueItem);
			}
		}

		private void PrepareNextSongAfterNextSongLoadFailure(QueueItem queueItem) {
			Log.Trace("Next song failed to load, trying to get a new one");
			if (ReferenceEquals(NextSongShadow, queueItem)) {
				NextSongShadow = null;
			} else {
				// Update next song index
				++nextSongIndex;
				UpdateNextSong();
			}
		}

		private void StartAsync(QueueItem queueItem) {
			Log.Info($"Starting {queueItem.AudioResource.ResourceTitle}...");
			if (nextSongShadow == queueItem)
				nextSongShadow = null;

			taskHost.SetCurrentSong(queueItem, TimeSpan.Zero);
			taskHost.PlayCurrentWhenFinished();
		}

		private QueueItem nextSongShadow;

		// This song will be prepared as next song if the queue is empty
		// It will not be played automatically, it has to be queued in some way
		public QueueItem NextSongShadow {
			get => nextSongShadow;
			set {
				lock (Lock) {
					nextSongShadow = value;
					UpdateNextSong();
				}
			}
		}

		private int nextSongIndex;

		private QueueItem NextSong => Queue.TryGetItem(nextSongIndex);

		private void UpdateNextSong() {
			lock (Lock) {
				var next = NextSong ?? NextSongShadow;
				UpdateNextSongInternal(next);
			}
		}

		private void UpdateNextSongInternal(QueueItem next) {
			if (next == null) {
				if (taskHost.HasTask && taskHost.IsNextResource) {
					taskHost.Clear();
				}
			} else {
				taskHost.SetNextSong(next, playerConnection.Remaining);
			}
		}

		private void SongEndedEvent(object sender, EventArgs e) {
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
			return new StartSongTaskHandler(new StartSongTask(resourceResolver, playerConnection, confAudioVolume, Lock, value));
		}
	}
}
