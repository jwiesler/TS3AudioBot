namespace TS3AudioBot.Audio.Preparation {
	public class NextSongHandler {
		public QueueItem NextSongPreparing { get; set; }

		public bool IsPreparingNextSong(QueueItem current) { return ReferenceEquals(current, NextSongPreparing); }

		public bool IsPreparingCurrentSong(QueueItem current) { return !ReferenceEquals(current, NextSongPreparing); }

		public static bool ShouldBeReplaced(QueueItem current, QueueItem newValue) {
			return !ReferenceEquals(current, newValue);
		}

		public bool ShouldBeReplacedNext(QueueItem current, QueueItem newValue) {
			return ShouldBeReplaced(current, newValue) && IsPreparingNextSong(current);
		}

		public void ClearNextSong() { NextSongPreparing = null; }
	}
}
