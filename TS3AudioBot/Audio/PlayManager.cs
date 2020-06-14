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
	public class PlayManager {
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

		private readonly SongAnalyzer songAnalyzer;

		private StartSongTask CurrentStartSongTask { get; set; }
		private QueueItem NextSongToPrepare { get; set; }

		public PlayManager(ConfBot config, Player playerConnection, ResolveContext resourceResolver, Stats stats, PlaylistManager playlistManager) {
			confBot = config;
			this.playerConnection = playerConnection;
			this.resourceResolver = resourceResolver;
			this.stats = stats;
			this.playlistManager = playlistManager;
			songAnalyzer = new SongAnalyzer(resourceResolver, playerConnection.FfmpegProducer);

			playerConnection.FfmpegProducer.OnSongLengthParsed += (sender, args) => {
				lock (Lock) {
					if (CurrentStartSongTask == null)
						return;
					Log.Info("Preparing song analyzer... (OnSongLengthParsed)");
					StartCurrentPrepareTask(GetAnalyzeTaskStartTime());
				}
			};
		}

		private int GetAnalyzeTaskStartTime() {
			return SongAnalyzer.GetTaskStartTime(playerConnection.Length - playerConnection.Position);
		}

		public void Clear() {
			lock (Lock) {
				Queue.Clear();
				TryStopCurrentSong();
				OnPlaybackEnded();
				CurrentStartSongTask?.Cancel();
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
				if (sender == null || !ReferenceEquals(sender, CurrentStartSongTask))
					return;

				BeforeResourceStarted?.Invoke(this, e);
			}
		}

		private void OnAfterResourceStarted(object sender, PlayInfoEventArgs e) {
			lock (Lock) {
				if (sender == null || !ReferenceEquals(sender, CurrentStartSongTask))
					return;

				RemoveCurrentSongTask();
				CurrentPlayData = e; // TODO meta as readonly
				AfterResourceStarted?.Invoke(this, e);
				UpdateNextSong();
			}
		}

		private void OnAudioResourceUpdated(object sender, AudioResourceUpdatedEventArgs e) {
			lock (Lock) {
				if (sender == null || !ReferenceEquals(sender, CurrentStartSongTask))
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
				if (sender == null || !ReferenceEquals(sender, CurrentStartSongTask))
					return;

				Log.Info("Could not play song {0} (reason: {1})", CurrentStartSongTask.QueueItem.AudioResource, e.Error);
				RemoveCurrentSongTask();

				Next();
			}
		}

		private void ConnectCurrentTask() {
			CurrentStartSongTask.BeforeResourceStarted += OnBeforeResourceStarted;
			CurrentStartSongTask.AfterResourceStarted += OnAfterResourceStarted;
			CurrentStartSongTask.OnAudioResourceUpdated += OnAudioResourceUpdated;
			CurrentStartSongTask.OnLoadFailure += OnLoadFailure;
		}

		private void DisconnectCurrentTask() {
			CurrentStartSongTask.BeforeResourceStarted -= OnBeforeResourceStarted;
			CurrentStartSongTask.AfterResourceStarted -= OnAfterResourceStarted;
			CurrentStartSongTask.OnAudioResourceUpdated -= OnAudioResourceUpdated;
			CurrentStartSongTask.OnLoadFailure -= OnLoadFailure;
		}

		private void RemoveCurrentSongTask() {
			DisconnectCurrentTask();
			CurrentStartSongTask = null;
		}
		
		private void SetPrepareTask(QueueItem queueItem) { // assert that this item is being prepared or start this one
			if (CurrentStartSongTask != null) {
				CurrentStartSongTask.Cancel();
				RemoveCurrentSongTask();
			}

			if (queueItem == null)
				return;

			CurrentStartSongTask = new StartSongTask(playerConnection, confBot, queueItem, songAnalyzer, Lock);
			ConnectCurrentTask();
		}

		private void StartCurrentPrepareTask(int seconds) {
			if(CurrentStartSongTask.Running)
				CurrentStartSongTask.UpdateStartAnalyzeTime(seconds);
			else
				CurrentStartSongTask.StartTask(seconds);
		}

		private void StartAsync(QueueItem queueItem) {
			SetPrepareTask(queueItem);
			StartCurrentPrepareTask(0);
			CurrentStartSongTask.PlayWhenFinished();
		}

//		private E<LocalStr> Start(QueueItem queueItem) {
//			Log.Info("Starting song {0}...", queueItem.AudioResource.ResourceTitle);
//
//			Stopwatch timer = new Stopwatch();
//			timer.Start();
//			var res = songAnalyzer.TryGetResult(queueItem);
//			if (!res.Ok)
//				return res.Error;
//
//			var result = res.Value;
//
//			if (queueItem.MetaData.ContainingPlaylistId != null && !ReferenceEquals(queueItem.AudioResource, result.Resource.BaseData))
//			{
//				Log.Info("AudioResource was changed by loader, saving containing playlist");
//
//				var modifyR = playlistManager.ModifyPlaylist(queueItem.MetaData.ContainingPlaylistId, (list, _) => {
//					foreach (var item in list.Items) {
//						if (ReferenceEquals(item.AudioResource, queueItem.AudioResource))
//							item.AudioResource = result.Resource.BaseData;
//					}
//				});
//				if (!modifyR.Ok)
//					return modifyR;
//			}
//
//			result.Resource.Meta = queueItem.MetaData;
//			var r = Start(result.Resource, result.RestoredLink.OkOr(null));
//			Log.Debug("Start song took {0}ms", timer.ElapsedMilliseconds);
//			return r;
//		}
//
//		private E<LocalStr> Start(PlayResource resource, string restoredLink) {
//			Log.Trace("Starting resource...");
//
//			var playInfo = new PlayInfoEventArgs(resource.Meta.ResourceOwnerUid, resource, restoredLink);
//			BeforeResourceStarted?.Invoke(this, playInfo);
//			if (string.IsNullOrWhiteSpace(resource.PlayUri)) {
//				Log.Error("Internal resource error: link is empty (resource:{0})", resource);
//				return new LocalStr(strings.error_playmgr_internal_error);
//			}
//
//			var gain = resource.BaseData.Gain ?? 0;
//			Log.Debug("AudioResource start: {0} with gain {1}", resource, gain);
//			var result = playerConnection.Play(resource, gain);
//
//			if (!result) {
//				Log.Error("Error return from player: {0}", result.Error);
//				return new LocalStr(strings.error_playmgr_internal_error);
//			}
//
//			playerConnection.Volume =
//				Tools.Clamp(playerConnection.Volume, confBot.Audio.Volume.Min, confBot.Audio.Volume.Max);
//			CurrentPlayData = playInfo; // TODO meta as readonly
//			AfterResourceStarted?.Invoke(this, playInfo);
//			UpdateNextSong();
//
//			return R.Ok;
//		}

		private void UpdateNextSong() {
			var next = Queue.Next;
			PrepareNextSong(next);
		}

		private void PrepareNextSong(QueueItem item) {
			lock (Lock) {
				if (CurrentStartSongTask != null && ReferenceEquals(CurrentStartSongTask.QueueItem, item))
					return;

				// Prepare this song now only if the preparing song is a next song and not the song we are trying to play now
				if (CurrentStartSongTask == null || ReferenceEquals(NextSongToPrepare, CurrentStartSongTask.QueueItem)) { 
					SetPrepareTask(item);

					if (CurrentStartSongTask != null && playerConnection.FfmpegProducer.Length != TimeSpan.Zero) {
						Log.Info("Preparing song analyzer... (PrepareNextSong)");
						StartCurrentPrepareTask(GetAnalyzeTaskStartTime());
					}
				}

				NextSongToPrepare = item;
			}
		}

		public void SongEndedEvent(object sender, EventArgs e) { StopSong(false); }

		public void Stop() {
			StopSong(true);
			RemoveCurrentSongTask();
		}

		private void StopSong(bool stopped /* true if stopped manually, false if ended normally */) {
			lock (Lock) {
				Log.Debug("Song stopped");
				ResourceStopped?.Invoke(this, new SongEndEventArgs(stopped));

				if (stopped) {
					playerConnection.Stop();

					TryStopCurrentSong();
				} else {
					var result = Next();
					if (result.Ok)
						return;
					Log.Info("Automatically playing next song ended with error: {0}", result.Error);
				}
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
	}
}
