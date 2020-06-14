using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio {
	public class SongAnalyzerResult {
		public PlayResource Resource { get; set; }

		public R<string, LocalStr> RestoredLink { get; set; }
	}
	
	public class SongAnalyzerTask {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public QueueItem Source { get; }

		public FfmpegProducer FfmpegProducer { get; }

		public ResolveContext ResourceResolver { get; }

		public SongAnalyzerTask(QueueItem source, ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			Source = source;
			FfmpegProducer = ffmpegProducer;
			ResourceResolver = resourceResolver;
		}

		public R<SongAnalyzerResult, LocalStr> Run(CancellationToken cancellationToken) {
			Log.Info("Started analysis for \"{0}\"", Source.AudioResource.ResourceTitle);
			Stopwatch timer = new Stopwatch();
			timer.Start();

			var resource = ResourceResolver.Load(Source.AudioResource);
			if (!resource.Ok)
				return resource.Error;
			var res = resource.Value;
			var restoredLink = ResourceResolver.RestoreLink(res.BaseData);
			if (!restoredLink.Ok)
				return restoredLink.Error;

			Log.Debug("Song resolve took {0}ms", timer.ElapsedMilliseconds);

			if(!(Source.AudioResource.Gain.HasValue || Source.AudioResource.AudioType != "youtube")) {
				timer.Restart();

				var gain = FfmpegProducer.VolumeDetect(res.PlayUri, cancellationToken);
				res.BaseData = res.BaseData.WithGain(gain);
				Log.Debug("Song volume detect took {0}ms", timer.ElapsedMilliseconds);
			}

			return new SongAnalyzerResult {
				Resource = res,
				RestoredLink = restoredLink
			};
		}
	}
}
