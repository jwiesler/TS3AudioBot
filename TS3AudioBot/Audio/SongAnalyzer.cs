using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio {
	public class SongAnalyzerResult {
		public PlayResource Resource { get; set; }

		public int Gain { get; set; }

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
			Log.Info("Started analysis for \"{0}\"", SongAnalyzer.GetItemDescription(Source));
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

			int gain;
			if (Source.AudioResource.AudioType != "youtube") {
				gain = 0;
			} else {
				timer.Restart();

				gain = FfmpegProducer.VolumeDetect(res.PlayUri, cancellationToken);

				Log.Debug("Song volume detect took {0}ms", timer.ElapsedMilliseconds);
			}

			return new SongAnalyzerResult {
				Resource = res,
				Gain = gain,
				RestoredLink = restoredLink
			};
		}
	}

	public class SongAnalyzerTaskHost {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public Task<R<SongAnalyzerResult, LocalStr>> Current { get; private set; }

		private CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();

		public SongAnalyzerTask Data { get; }

		public SongAnalyzerTaskHost(SongAnalyzerTask data) { Data = data; }

		private Task<R<SongAnalyzerResult, LocalStr>> CreateTask(int inSeconds, CancellationToken token) {
			return new Task<R<SongAnalyzerResult, LocalStr>>(() => {
				try {
					Task.Delay(inSeconds * 1000, token).Wait();
					return Data.Run(token);
				} catch (OperationCanceledException) {
					return null;
				}
			});
		}

		public R<SongAnalyzerResult, LocalStr> Result {
			get {
				if (Current == null)
					throw new InvalidOperationException();

				return Current.Result;
			}
		}

		public void PrepareRun(int inSeconds) {
			if (Current != null) {
				Log.Warn("SongAnalyzerTask was already working");
				return;
			}

			Log.Debug("Preparing background analyzer for \"{0}\", starting in {1}s",
				SongAnalyzer.GetItemDescription(Data.Source), inSeconds);

			Current = CreateTask(inSeconds, TokenSource.Token);
		}

		public void Start() { Current.Start(); }

		public void Cancel() { TokenSource.Cancel(); }

		public void Dispose() {
			Cancel();
			// TokenSource?.Dispose();
		}
	}

	public class SongAnalyzer {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const int MaxSecondsBeforeNextSong = 30;

		public FfmpegProducer FfmpegProducer { get; }

		private ResolveContext ResourceResolver { get; }

		public SongAnalyzerTaskHost Instance { get; private set; }

		public SongAnalyzer(ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			ResourceResolver = resourceResolver;
			FfmpegProducer = ffmpegProducer;
		}

		public void Prepare(int inSeconds) {
			if (Instance == null)
				throw new InvalidOperationException("instance null");

			if (Instance.Current != null) {
				Log.Warn("SongAnalyzerTask was already working");
				return;
			}

			Instance.PrepareRun(inSeconds);
			Instance.Start();
		}

		private SongAnalyzerTask CreateTask(QueueItem item) {
			return new SongAnalyzerTask(item, ResourceResolver, FfmpegProducer);
		}

		public void SetNextSong(QueueItem item) {
			Instance?.Dispose();
			Instance = new SongAnalyzerTaskHost(CreateTask(item));
		}

		public static string GetItemDescription(QueueItem item) { return item.AudioResource.ResourceTitle; }

		public R<SongAnalyzerResult, LocalStr> TryGetResult(QueueItem item) {
			R<SongAnalyzerResult, LocalStr> res;
			if (Instance?.Current == null || !ReferenceEquals(Instance.Data.Source, item)) {
				Log.Warn("Song {0} was not prepared", GetItemDescription(item));
				Instance?.Dispose();
				res = CreateTask(item).Run(new CancellationToken());
			} else {
				res = Instance.Result;
			}

			Clear();
			return res;
		}

		public bool IsPreparing(QueueItem item) { return Instance != null && ReferenceEquals(Instance.Data.Source, item); }

		public static int GetTaskStartTime(TimeSpan remainingSongTime) {
			int remainingTime = (int) remainingSongTime.TotalSeconds;
			return Math.Max(remainingTime - MaxSecondsBeforeNextSong, 0);
		}

		public void Clear() {
			Instance?.Dispose();
			Instance = null;
		}
	}
}
