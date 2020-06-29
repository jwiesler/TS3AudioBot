using System;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Audio;
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

		public const string NoRestoredLinkMessage = "NoRestoredLinkMessage";
		public const string LoadFailedMessage = "LoadFailedMessage";
		public const string RestoredLink = "Restored link";

		public R<string, LocalStr> RestoreLink(AudioResource res) {
			if (ShouldReturnNoRestoredLink)
				return new LocalStr(NoRestoredLinkMessage);
			return RestoredLink;
		}

		public R<PlayResource, LocalStr> Load(AudioResource resource) {
			if (ShouldFailLoad)
				return new LocalStr(LoadFailedMessage);
			return new PlayResource(resource.ResourceId, resource);
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
		public event EventHandler OnSongLengthParsed;
		public event EventHandler OnSongEnd;
		public event EventHandler<SongInfoChanged> OnSongUpdated;
		public float Volume { get; set; } = 70;
		public TimeSpan Length { get; set; } = TimeSpan.Zero;
		public TimeSpan Position { get; set; } = TimeSpan.Zero;

		public bool ShouldFailPlay { get; set; }

		public bool StopCalled { get; set; }
		public (PlayResource res, int gain) PlayArgs { get; set; }

		public E<string> Play(PlayResource res, int gain) {
			PlayArgs = (res, gain);
			if (ShouldFailPlay)
				return "";
			return R.Ok;
		}

		public void Stop() { StopCalled = true; }
	}
}
