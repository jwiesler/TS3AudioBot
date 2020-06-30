using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TS3AudioBot.Audio;
using TS3AudioBot.Audio.Preparation;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TSLib;

namespace TS3ABotUnitTests {
	public static class Values {
		public static readonly AudioResource Resource1AC = new AudioResource("1", "A", "C");
		public static readonly AudioResource Resource1AYoutube = new AudioResource("1", "A", "youtube");
		public static readonly AudioResource Resource2BYoutube = new AudioResource("2", "B", "youtube");
		public static readonly AudioResource Resource1AYoutubeGain = Resource1AYoutube.WithGain(5);
		public static readonly AudioResource Resource2BYoutubeGain = Resource2BYoutube.WithGain(10);
		public static readonly Uid TestUid = Uid.To("Test");
		public static readonly ConfAudioVolume VolumeConfig = CreateAudioVolume();
		public const string ListId = "CoolPlaylist";

		private static ConfAudioVolume CreateAudioVolume() {
			var config = new ConfAudioVolume();
			config.Min.Value = 0;
			config.Max.Value = 10;
			return config;
		}
	}

	public class LoaderContext : ILoaderContext {
		public bool ShouldReturnNoRestoredLink { get; set; }
		public bool ShouldFailLoad { get; set; }
		public event EventHandler AfterLoad;
		
		public const string NoRestoredLinkMessage = "NoRestoredLinkMessage";
		public const string LoadFailedMessage = "LoadFailedMessage";
		public const string RestoredLink = "Restored link";

		public R<string, LocalStr> RestoreLink(AudioResource res) {
			if (ShouldReturnNoRestoredLink)
				return new LocalStr(NoRestoredLinkMessage);
			return RestoredLink;
		}

		public static string MakeResourceURI(string id, int index) {
			return id + "_" + index;
		}

		public int LoadedResources { get; private set; }
		public R<PlayResource, LocalStr> Load(AudioResource resource) {
			try {
				if (ShouldFailLoad)
					return new LocalStr(LoadFailedMessage);
				return new PlayResource(MakeResourceURI(resource.ResourceId, LoadedResources), resource);
			} finally {
				LoadedResources++;
				AfterLoad?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	public class VolumeDetector : IVolumeDetector {
		public const int VolumeSet = 10;

		public int RunVolumeDetection(string url, CancellationToken token) {
			if (token.IsCancellationRequested)
				throw new TaskCanceledException();

			return VolumeSet;
		}
	}

	public class Player : VolumeDetector, IPlayer {
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

	public class StartSongTaskHost : StartSongTaskHostBase {
		private QueueItem preparingItem;

		public override QueueItem PreparingItem => preparingItem;
		public int CanceledTasks { get; set; }
		private bool PlayRequested { get; set; }

		public bool GetPlayRequestedReset() {
			var b = PlayRequested;
			PlayRequested = false;
			return b;
		}

		public void Play() {
			Assert.IsNotNull(preparingItem);
			var e = new PlayInfoEventArgs(PreparingItem.MetaData.ResourceOwnerUid, new PlayResource("uri", PreparingItem.AudioResource, PreparingItem.MetaData), "link");
			InvokeBeforeResourceStarted(this, e);
			InvokeAfterResourceStarted(this, e);
		}

		public void FailLoad() {
			Assert.IsNotNull(preparingItem);
			PlayRequested = false;
			var e = new LoadFailureTaskEventArgs(new LocalStr("Error"), PreparingItem, IsCurrentResource);
			InvokeOnLoadFailure(this, e);
		}

		public override void PlayCurrentWhenFinished() {
			Assert.IsNotNull(preparingItem);
			Assert.IsFalse(PlayRequested);
			PlayRequested = true;
		}

		public override void UpdateRemaining(TimeSpan remaining) {
			Assert.IsNotNull(preparingItem);
		}

		protected override void RemoveFinishedTask() { preparingItem = null; }

		protected override void CancelTask() {
			++CanceledTasks;
			preparingItem = null;
		}

		protected override void SetTask(QueueItem item, TimeSpan? remaining) {
			Assert.IsNotNull(item);
			preparingItem = item;
		}
	}
}
