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

		void SetNextSong(QueueItem item, TimeSpan? remaining);
		void SetCurrentSong(QueueItem item, TimeSpan? remaining);

		void PlayCurrentWhenFinished();
		void UpdateRemaining(TimeSpan remaining);

		void Clear();
		void ClearTask();
	}
}
