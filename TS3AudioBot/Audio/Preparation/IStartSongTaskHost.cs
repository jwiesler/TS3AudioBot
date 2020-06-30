using System;

namespace TS3AudioBot.Audio.Preparation {
	public interface IStartSongTaskHost {
		event EventHandler<PlayInfoEventArgs> BeforeResourceStarted;

		// Current task is already removed if those two are called
		event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		event EventHandler<LoadFailureTaskEventArgs> OnLoadFailure;

		event EventHandler<AudioResourceUpdatedEventArgs> OnAudioResourceUpdated;

		bool HasTask { get; }
		bool IsCurrentResource { get; }
		bool IsNextResource { get; }

		// Prepare this song if the preparing song is not the current song
		// UpdateRemaining if this song is already being prepared
		void SetNextSong(QueueItem item, TimeSpan? remaining);

		// Prepare this song as the current song
		// UpdateRemaining if this song is already being prepared
		void SetCurrentSong(QueueItem item, TimeSpan? remaining);

		void PlayCurrentWhenFinished();
		void UpdateRemaining(TimeSpan remaining);

		void Clear();
		void ClearTask();
	}
}
