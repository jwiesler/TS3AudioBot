using System;
using System.Diagnostics;
using System.Threading;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio.Preparation {
	public class SongAnalyzerResult {
		public PlayResource Resource { get; set; }

		public R<string, LocalStr> RestoredLink { get; set; }
	}

	public class SongAnalyzerTask {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public QueueItem Source { get; }

		public IVolumeDetector VolumeDetector { get; }

		public ILoaderContext LoaderContext { get; }

		public SongAnalyzerTask(QueueItem source, ILoaderContext loaderContext, IVolumeDetector volumeDetector) {
			Source = source;
			VolumeDetector = volumeDetector;
			LoaderContext = loaderContext;
		}

		public R<SongAnalyzerResult, LocalStr> Run(CancellationToken cancellationToken) {
			Log.Info("Started analysis for \"{0}\"", Source.AudioResource.ResourceTitle);
			Stopwatch timer = new Stopwatch();
			timer.Start();

			var resource = LoaderContext.Load(Source.AudioResource);
			if (!resource.Ok)
				return resource.Error;
			var res = resource.Value;
			var restoredLink = LoaderContext.RestoreLink(res.BaseData);
			if (!restoredLink.Ok)
				return restoredLink.Error;

			Log.Debug("Song resolve took {0}ms", timer.ElapsedMilliseconds);

			if (!(Source.AudioResource.Gain.HasValue || Source.AudioResource.AudioType != "youtube")) {
				timer.Restart();

				var gain = VolumeDetector.RunVolumeDetection(res.PlayUri, cancellationToken);
				res.BaseData = res.BaseData.WithGain(gain);
				Log.Debug("Song volume detect took {0}ms", timer.ElapsedMilliseconds);
			}

			res.Meta = Source.MetaData;
			return new SongAnalyzerResult {
				Resource = res,
				RestoredLink = restoredLink
			};
		}
	}
}
