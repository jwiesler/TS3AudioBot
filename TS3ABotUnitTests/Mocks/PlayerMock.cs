using System;
using System.Threading;
using TS3AudioBot.Audio;
using TS3AudioBot.ResourceFactories;

namespace TS3ABotUnitTests.Mocks {
	public class PlayerMock : VolumeDetectorMock, IPlayer {
		public static readonly TimeSpan DefaultResourceLength = TimeSpan.FromSeconds(10);
		public event EventHandler OnSongLengthParsed;
		public event EventHandler OnSongEnd;
		public event EventHandler<SongInfoChanged> OnSongUpdated;
		public float Volume { get; set; } = 70;

		private TimeSpan? length;
		public TimeSpan Length => length ?? TimeSpan.Zero;
		public TimeSpan Position { get; } = TimeSpan.Zero;

		public TimeSpan? Remaining {
			get {
				if (length == null)
					return null;
				return length.Value - Position;
			}
		}

		public bool ShouldFailPlay { get; set; }

		public bool StopCalled { get; set; }
		public (PlayResource res, int gain) PlayArgs { get; set; }

		public EventWaitHandle PlayWaitHandle { get; } = new EventWaitHandle(false, EventResetMode.AutoReset);

		public void InvokeOnSongLengthParsed() {
			OnSongLengthParsed?.Invoke(this, EventArgs.Empty);
		}

		public void InvokeOnSongEnd() {
			OnSongEnd?.Invoke(this, EventArgs.Empty);
		}

		public void InvokeOnSongUpdated(string title) {
			OnSongUpdated?.Invoke(this, new SongInfoChanged() {Title = title});
		}

		public E<string> Play(PlayResource res, int gain) {
			PlayArgs = (res, gain);
			PlayWaitHandle.Set();
			if (ShouldFailPlay)
				return "";

			length = DefaultResourceLength;
			return R.Ok;
		}

		public void Stop() {
			StopCalled = true;
			length = null;
		}
	}
}
