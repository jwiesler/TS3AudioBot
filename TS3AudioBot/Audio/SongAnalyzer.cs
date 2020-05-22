using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3AudioBot.Audio {
	public class SongAnalyzerTask : IDisposable {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public QueueItem Source { get; }
		public FfmpegProducer FfmpegProducer { get; }
		public ResolveContext ResourceResolver { get; }
		public Task<R<Result, LocalStr>> Current { get; private set; }

		public class Result {
			public PlayResource Resource { get; set; }
			public int Gain { get; set; }
			public R<string, LocalStr> RestoredLink { get; set; }
		}

		private CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();

		public SongAnalyzerTask(QueueItem source, ResolveContext resourceResolver, FfmpegProducer ffmpegProducer) {
			Source = source;
			FfmpegProducer = ffmpegProducer;
			ResourceResolver = resourceResolver;
		}

		private R<Result, LocalStr> Run(CancellationToken cancellationToken) {
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
			timer.Restart();

			int gain = FfmpegProducer.VolumeDetect(res.PlayUri, cancellationToken);

			Log.Debug("Song volume detect took {0}ms", timer.ElapsedMilliseconds);
			return new Result {
				Resource = res,
				Gain = gain,
				RestoredLink = restoredLink
			};
		}

		private Task<R<Result, LocalStr>> CreateTask(int inSeconds, CancellationToken token) {
			return new Task<R<Result, LocalStr>>(() => {
				try {
					Task.Delay(inSeconds * 1000, token).Wait();
					Log.Info("Started analyze for \"{0}\"", SongAnalyzer.GetItemDescription(Source));
					return Run(token);
				} catch (OperationCanceledException) {
					return null;
				}
			});
		}

		public R<Result, LocalStr> TryGetResult() {
			if (Current == null)
				throw new InvalidOperationException();

			bool ended = Current.Wait(2000);

			if (!ended) {
				Log.Warn("Song analyze task is taking very long...");
				ended = Current.Wait(20000);
				if (!ended) {
					Cancel();
					Log.Warn("Song analyze task is hanging...");
					return Run(new CancellationToken());
				}
			}
			
			return Current.Result;
		}

		public void PrepareRun(int inSeconds) {
			if (Current != null) {
				Log.Warn("SongAnalyzerTask was already working");
				return;
			}

			Log.Debug("Preparing background analyzer for \"{0}\", starting in {1}s",
				SongAnalyzer.GetItemDescription(Source), inSeconds);

			Current = CreateTask(inSeconds, TokenSource.Token);
		}

		public void Start() { Current.Start(); }

		public void RunSynchronously() { Current.RunSynchronously(); }

		public void Cancel() { TokenSource.Cancel(); }

		public void Dispose() {
			Cancel();
			TokenSource?.Dispose();
		}
	}

	public class SongAnalyzer {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const int MaxSecondsBeforeNextSong = 30;

		public FfmpegProducer FfmpegProducer { get; }

		private ResolveContext ResourceResolver { get; }

		public SongAnalyzerTask Instance { get; private set; }

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

		public void SetNextSong(QueueItem item) {
			Instance?.Dispose();
			Instance = new SongAnalyzerTask(item, ResourceResolver, FfmpegProducer);
		}

		public static string GetItemDescription(QueueItem item) { return item.AudioResource.ResourceTitle; }

		public R<SongAnalyzerTask.Result, LocalStr> TryGetResult(QueueItem item) {
			if (Instance?.Current == null || !ReferenceEquals(Instance.Source, item)) {
				Instance?.Cancel();
				Log.Warn("Song {0} was not prepared", SongAnalyzer.GetItemDescription(item));
				SetNextSong(item);
				Instance.PrepareRun(0);
				Instance.RunSynchronously();
			}

			var res = Instance.TryGetResult();
			Clear();
			return res;
		}

		public bool IsPreparing(QueueItem item) { return Instance != null && ReferenceEquals(Instance.Source, item); }

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
