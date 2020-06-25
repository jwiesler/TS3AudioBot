// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TSLib.Audio;
using TSLib.Helper;

namespace TS3AudioBot.Audio
{
	public class FfmpegProducer : IPlayerSource, ISampleInfo, IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly Id id;
		private static readonly Regex FindDurationMatcher = new Regex(@"^\s*Duration: (\d+):(\d\d):(\d\d).(\d\d)", Util.DefaultRegexConfig);
		private static readonly Regex IcyMetadataMatcher = new Regex("((\\w+)='(.*?)';\\s*)+", Util.DefaultRegexConfig);
		private const string PreLinkConf = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -hide_banner -nostats -threads 1 -i \"";
		private const string PostLinkConf = "-ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		private const string LinkConfIcy = "-hide_banner -nostats -threads 1 -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		private static readonly Regex FindMaxVolumeMatcher = new Regex("^.*max_volume: (-?\\d+\\.\\d+) dB$", Util.DefaultRegexConfig);
		private static readonly Regex FindMeanVolumeMatcher = new Regex("^.*mean_volume: (-?\\d+\\.\\d+) dB$", Util.DefaultRegexConfig);
		private const string PreLinkConfDetect = "-hide_banner -nostats -threads 1 -t 180 -i \"";
		private const string PostLinkConfDetect = "-af volumedetect -f null /dev/null";
		private static readonly TimeSpan retryOnDropBeforeEnd = TimeSpan.FromSeconds(10);
		public event EventHandler<EventArgs> OnSongLengthParsed;

		private readonly ConfToolsFfmpeg config;

		public event EventHandler OnSongEnd;
		public event EventHandler<SongInfoChanged> OnSongUpdated;

		private FfmpegInstance ffmpegInstance;

		public int SampleRate { get; } = 48000;
		public int Channels { get; } = 2;
		public int BitsPerSample { get; } = 16;

		public FfmpegProducer(ConfToolsFfmpeg config, Id id)
		{
			this.config = config;
			this.id = id;
		}

		public static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken = default)
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

			void ProcessExited(object sender, EventArgs e)
			{
				tcs.TrySetResult(true);
			}

			process.EnableRaisingEvents = true;
			process.Exited += ProcessExited;

			try
			{
				if (process.HasExited)
				{
					return;
				}

				using (cancellationToken.Register(() => tcs.TrySetCanceled()))
				{
					await tcs.Task.ConfigureAwait(false);
				}
			}
			finally
			{
				process.Exited -= ProcessExited;
			}
		}

		public int VolumeDetect(string url, CancellationToken token) {
			int gain = 0;
			float maxVolumeFloat = 0f;
			float meanVolumeFloat = 0f;

			var ffmpegProcess = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = config.Path.Value,
					Arguments = string.Concat(PreLinkConfDetect, url, "\" ", PostLinkConfDetect),
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				},
				EnableRaisingEvents = true,
			};

			ffmpegProcess.Start();
			try {
				WaitForExitAsync(ffmpegProcess, token).Wait();
			} finally {
				if (token.IsCancellationRequested)
					ffmpegProcess.Kill();
			}

			StreamReader errorReader = ffmpegProcess.StandardError;
			string line;
			while ((line = errorReader.ReadLine()) != null) {
				var match = FindMaxVolumeMatcher.Match(line);
				if (match.Success && float.TryParse(match.Groups[1].Value, out var maxVolume)) {
					maxVolumeFloat = maxVolume;
				}

				match = FindMeanVolumeMatcher.Match(line);
				if (match.Success && float.TryParse(match.Groups[1].Value, out var meanVolume)) {
					meanVolumeFloat = meanVolume;
				}
			}

			if (maxVolumeFloat < 0) {
				gain += (int) Math.Round(Math.Abs(maxVolumeFloat));
			}

			float absMax = Math.Abs(maxVolumeFloat);
			float absMean = Math.Abs(meanVolumeFloat);
			if (absMean - absMax > 10) {
				gain += (int) Math.Round((absMean - absMax - 10) / 2);
			}

			Log.Info($"Detected gain needed: {gain}dB (maximum volume {maxVolumeFloat:0.00}, mean volume {meanVolumeFloat:0.00})");
			return gain;
		}

		public E<string> AudioStart(string url, string resId, int gain, TimeSpan? startOff = null)
		{
			return StartFfmpegProcess(url, gain, startOff ?? TimeSpan.Zero);
		}

		public E<string> AudioStartIcy(string url) => StartFfmpegProcessIcy(url);

		public E<string> AudioStop()
		{
			StopFfmpegProcess();
			return R.Ok;
		}

		public TimeSpan Length => GetCurrentSongLength() ?? TimeSpan.Zero;

		public TimeSpan Position
		{
			get => ffmpegInstance?.AudioTimer.SongPosition ?? TimeSpan.Zero;
			set => SetPosition(value);
		}

		public int Read(byte[] buffer, int offset, int length, out Meta meta)
		{
			meta = null;
			bool triggerEndSafe = false;
			int read;

			var instance = ffmpegInstance;

			if (instance is null)
				return 0;

			try
			{
				read = instance.FfmpegProcess.StandardOutput.BaseStream.Read(buffer, 0, length);
			}
			catch (Exception ex)
			{
				read = 0;
				Log.Debug(ex, "Can't read ffmpeg");
			}

			if (read == 0)
			{
				bool ret;
				(ret, triggerEndSafe) = instance.IsIcyStream
					? OnReadEmptyIcy(instance)
					: OnReadEmpty(instance);
				if (ret)
					return 0;

				if (instance.FfmpegProcess.HasExitedSafe())
				{
					Log.Trace("Ffmpeg has exited");
					AudioStop();
					triggerEndSafe = true;
				}
			}

			if (triggerEndSafe)
			{
				OnSongEnd?.Invoke(this, EventArgs.Empty);
				return 0;
			}

			instance.HasTriedToReconnect = false;
			instance.AudioTimer.PushBytes(read);
			return read;
		}

		private (bool ret, bool trigger) DoRetry(FfmpegInstance instance, TimeSpan position) {
			Log.Debug("Connection to song lost, retrying at {0}", position);
			instance.HasTriedToReconnect = true;
			var newInstance = SetPosition(position);
			if (newInstance.Ok)
			{
				newInstance.Value.HasTriedToReconnect = false;
				return (true, false);
			}
			else
			{
				Log.Debug("Retry failed {0}", newInstance.Error);
				return (false, true);
			}
		}

		private (bool ret, bool trigger) OnReadEmpty(FfmpegInstance instance)
		{
			if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
			{
				var expectedStopLength = GetCurrentSongLength();
				Log.Trace("Expected song length {0}", expectedStopLength);
				if (expectedStopLength.HasValue)
				{
					var actualStopPosition = instance.AudioTimer.SongPosition;
					Log.Trace("Actual song position {0}", actualStopPosition);
					if (actualStopPosition + retryOnDropBeforeEnd < expectedStopLength) {
						return DoRetry(instance, actualStopPosition);
					}
				}
			}
			else
			{
				Log.Trace("Read empty, continuing to read from same process");
			}
			return (false, false);
		}

		private (bool ret, bool trigger) OnReadEmptyIcy(FfmpegInstance instance)
		{
			if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
			{
				Log.Debug("Connection to stream lost, retrying...");
				instance.HasTriedToReconnect = true;
				var newInstance = StartFfmpegProcessIcy(instance.ReconnectUrl);
				if (newInstance.Ok)
				{
					newInstance.Value.HasTriedToReconnect = true;
					return (true, false);
				}
				else
				{
					Log.Debug("Retry failed {0}", newInstance.Error);
					return (false, true);
				}
			}
			return (false, false);
		}

		private R<FfmpegInstance, string> SetPosition(TimeSpan value)
		{
			if (value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(value));
			var instance = ffmpegInstance;
			if (instance is null)
				return "No instance running";
			if (instance.IsIcyStream)
				return "Cannot seek icy stream";
			var lastLink = instance.ReconnectUrl;
			var gain = instance.Gain;
			if (lastLink is null)
				return "No current url active";
			return StartFfmpegProcess(lastLink, gain, value);
		}

		private R<FfmpegInstance, string> StartFfmpegProcess(string url, int gain, TimeSpan? offsetOpt)
		{
			StopFfmpegProcess();
			Log.Trace("Start request {0}", url);

			if (gain > 0) {
				Log.Info("Starting stream with {0}dB gain.", gain);
			}

			string arguments;
			var offset = offsetOpt ?? TimeSpan.Zero;
			if (offset > TimeSpan.Zero) {
				var seek = string.Format(CultureInfo.InvariantCulture, @"-ss {0:hh\:mm\:ss\.fff}", offset);
				arguments = string.Concat(seek, " ", PreLinkConf, url, gain > 0 ? "\" -af volume=" + gain + "dB " : "\" ", PostLinkConf, " ", seek);
			}
			else {
				arguments = string.Concat(PreLinkConf, url, gain > 0 ? "\" -af volume=" + gain + "dB " : "\" ", PostLinkConf);
			}

			var newInstance = new FfmpegInstance(
				url,
				new PreciseAudioTimer(this)
				{
					SongPositionOffset = offset
				},
				false) {
				Gain = gain,
				OnSongLengthParsed = InvokeOnSongLengthParsed
			};

			return StartFfmpegProcessInternal(newInstance, arguments);
		}

		private R<FfmpegInstance, string> StartFfmpegProcessIcy(string url)
		{
			StopFfmpegProcess();
			Log.Trace("Start icy-stream request {0}", url);

			try
			{
				var request = WebWrapper.CreateRequest(new Uri(url)).Unwrap();
				request.Headers["Icy-MetaData"] = "1";

				var response = request.GetResponse();
				var stream = response.GetResponseStream();

				if (!int.TryParse(response.Headers["icy-metaint"], out var metaint))
				{
					return "Invalid icy stream tags";
				}

				var newInstance = new FfmpegInstance(
					url,
					new PreciseAudioTimer(this),
					true)
				{
					IcyStream = stream,
					IcyMetaInt = metaint,
				};
				newInstance.OnMetaUpdated = e => OnSongUpdated?.Invoke(this, e);

				new Thread(() => newInstance.ReadStreamLoop(id))
				{
					Name = $"IcyStreamReader[{id}]",
				}.Start();

				return StartFfmpegProcessInternal(newInstance, LinkConfIcy);
			}
			catch (Exception ex)
			{
				var error = $"Unable to create icy-stream ({ex.Message})";
				Log.Warn(ex, error);
				return error;
			}
		}

		private R<FfmpegInstance, string> StartFfmpegProcessInternal(FfmpegInstance instance, string arguments)
		{
			try
			{
				instance.FfmpegProcess = new Process
				{
					StartInfo = new ProcessStartInfo
					{
						FileName = config.Path.Value,
						Arguments = arguments,
						RedirectStandardOutput = true,
						RedirectStandardInput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true,
					},
					EnableRaisingEvents = true,
				};

				Log.Debug("Starting ffmpeg with {0}", arguments);
				instance.FfmpegProcess.ErrorDataReceived += instance.FfmpegProcess_ErrorDataReceived;
				instance.FfmpegProcess.Start();
				instance.FfmpegProcess.BeginErrorReadLine();

				instance.AudioTimer.Start();

				var oldInstance = Interlocked.Exchange(ref ffmpegInstance, instance);
				oldInstance?.Close();

				return instance;
			}
			catch (Exception ex)
			{
				var error = ex is Win32Exception
					? $"Ffmpeg could not be found ({ex.Message})"
					: $"Unable to create stream ({ex.Message})";
				Log.Error(ex, error);
				instance.Close();
				StopFfmpegProcess();
				return error;
			}
		}

		private void InvokeOnSongLengthParsed() {
			OnSongLengthParsed?.Invoke(this, EventArgs.Empty);
		}

		private void StopFfmpegProcess()
		{
			var oldInstance = Interlocked.Exchange(ref ffmpegInstance, null);
			if (oldInstance != null)
			{
				oldInstance.OnMetaUpdated = null;
				oldInstance.OnSongLengthParsed = null;
				oldInstance.Close();
			}
		}

		private TimeSpan? GetCurrentSongLength()
		{
			var instance = ffmpegInstance;
			if (instance is null)
				return TimeSpan.Zero;

			return instance.ParsedSongLength;
		}

		public void Dispose()
		{
			StopFfmpegProcess();
		}

		private class FfmpegInstance
		{
			public Process FfmpegProcess { get; set; }
			public bool HasTriedToReconnect { get; set; }
			public string ReconnectUrl { get; private set; }
			public int Gain { get; set; }
			public bool IsIcyStream { get; }
			public PreciseAudioTimer AudioTimer { get; }
			public TimeSpan? ParsedSongLength { get; set; } = null;
			public Action OnSongLengthParsed;
			public Stream IcyStream { get; set; }
			public int IcyMetaInt { get; set; }
			public bool Closed { get; set; }

			public Action<SongInfoChanged> OnMetaUpdated;

			private readonly StringBuilder errorLogStringBuilder = new StringBuilder();

			public FfmpegInstance(string url, PreciseAudioTimer timer, bool isIcyStream)
			{
				ReconnectUrl = url;
				AudioTimer = timer;
				IsIcyStream = isIcyStream;

				HasTriedToReconnect = false;
			}

			public void Close()
			{
				Closed = true;
				Log.Trace($"Ffmpeg process {FfmpegProcess.Id} exited, output:\n{errorLogStringBuilder}");
				errorLogStringBuilder.Clear();
				try
				{
					if (!FfmpegProcess.HasExitedSafe())
						FfmpegProcess.Kill();
				}
				catch { }
				try { FfmpegProcess.CancelErrorRead(); } catch { }
				try { FfmpegProcess.StandardInput.Dispose(); } catch { }
				try { FfmpegProcess.StandardOutput.Dispose(); } catch { }
				try { FfmpegProcess.Dispose(); } catch { }

				IcyStream?.Dispose();
			}

			public void FfmpegProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
			{
				if (e.Data is null)
					return;

				if (sender != FfmpegProcess)
					throw new InvalidOperationException("Wrong process associated to event");
				
				errorLogStringBuilder.AppendLine(e.Data);

				if (!ParsedSongLength.HasValue)
				{
					var match = FindDurationMatcher.Match(e.Data);
					if (!match.Success)
						return;

					int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
					int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
					int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
					int millisec = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 10;
					ParsedSongLength = new TimeSpan(0, hours, minutes, seconds, millisec);
					Thread.MemoryBarrier();
					OnSongLengthParsed?.Invoke();
				}

				//if (!HasIcyTag && e.Data.AsSpan().TrimStart().StartsWith("icy-".AsSpan()))
				//{
				//	HasIcyTag = true;
				//}
			}

			public void ReadStreamLoop(Id id)
			{
				Tools.SetLogId(id.ToString());
				const int IcyMaxMeta = 255 * 16;
				const int ReadBufferSize = 4096;

				int errorCount = 0;
				var buffer = new byte[Math.Max(ReadBufferSize, IcyMaxMeta)];
				int readCount = 0;

				while (!Closed)
				{
					try
					{
						while (readCount < IcyMetaInt)
						{
							int read = IcyStream.Read(buffer, 0, Math.Min(ReadBufferSize, IcyMetaInt - readCount));
							if (read == 0)
							{
								Close();
								return;
							}
							readCount += read;
							FfmpegProcess.StandardInput.BaseStream.Write(buffer, 0, read);
							errorCount = 0;
						}
						readCount = 0;

						var metaByte = IcyStream.ReadByte();
						if (metaByte < 0)
						{
							Close();
							return;
						}

						if (metaByte > 0)
						{
							metaByte *= 16;
							while (readCount < metaByte)
							{
								int read = IcyStream.Read(buffer, 0, metaByte - readCount);
								if (read == 0)
								{
									Close();
									return;
								}
								readCount += read;
							}
							readCount = 0;

							var metaString = Encoding.UTF8.GetString(buffer, 0, metaByte).TrimEnd('\0');
							Log.Debug("Meta: {0}", metaString);
							OnMetaUpdated?.Invoke(ParseIcyMeta(metaString));
						}
					}
					catch (Exception ex)
					{
						errorCount++;
						if (errorCount >= 50)
						{
							Log.Error(ex, "Failed too many times trying to access ffmpeg. Closing stream.");
							Close();
							return;
						}

						if (ex is InvalidOperationException)
						{
							Log.Debug(ex, "Waiting for ffmpeg");
							Thread.Sleep(100);
						}
						else
						{
							Log.Debug(ex, "Stream read/write error");
						}
					}
				}
			}

			private static SongInfoChanged ParseIcyMeta(string metaString)
			{
				var songInfo = new SongInfoChanged();
				var match = IcyMetadataMatcher.Match(metaString);
				if (match.Success)
				{
					for (int i = 0; i < match.Groups[1].Captures.Count; i++)
					{
						switch (match.Groups[2].Captures[i].Value.ToUpperInvariant())
						{
						case "STREAMTITLE":
							songInfo.Title = match.Groups[3].Captures[i].Value;
							break;
						}
					}
				}
				return songInfo;
			}
		}
	}
}
