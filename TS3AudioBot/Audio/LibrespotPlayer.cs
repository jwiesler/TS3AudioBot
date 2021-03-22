using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using HumanBytes;
using SpotifyAPI.Web;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Web;

namespace TS3AudioBot.Audio {
	internal enum State {
		NotSetUp,
		Idle,
		LaunchingLibrespot,
		LibrespotRunning,
		LibrespotFailed,
		StreamRunning
	}

	public class LibrespotPlayer {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private const int BytesPerChunk = 1024;
		private const string LibrespotArgs = "--initial-volume 100 --enable-volume-normalisation " +
		                                     "--normalisation-gain-type track --username {0}" +
		                                     " --password {1} -n {2} --disable-audio-cache" +
		                                     " --bitrate 320 --backend pipe --passthrough";

		private static readonly TimeSpan LibrespotStartTimeout = TimeSpan.FromSeconds(5);
		private static readonly TimeSpan TrackStartTimeout = TimeSpan.FromSeconds(5);
		private static readonly Regex BadAuthMatcher = new Regex("Bad credentials$");
		private static readonly Regex GoodAuthMatcher = new Regex("librespot_core::session.*Authenticated as");

		private static readonly ByteSizeFormatter byteSizeFormatter = new ByteSizeFormatter {
			Convention = ByteSizeConvention.Binary,
			DecimalPlaces = 2,
			RoundingRule = ByteSizeRounding.Closest,
			UseFullWordForBytes = true
		};

		private readonly SpotifyApi api;
		private readonly ConfLibrespot conf;
		private readonly string deviceId;
		private readonly IList<string> output;

		private State state;
		private Process process;

		public LibrespotPlayer(SpotifyApi api, ConfLibrespot conf) {
			this.api = api;
			this.conf = conf;
			state = State.NotSetUp;
			Log.Trace("Changed librespot state to notsetup.");
			output = new List<string>();

			void Fail(string message) {
				Log.Error(message);
				state = State.NotSetUp;
				Log.Trace("Failed setup of librespot, changed state to notsetup.");
				if (process != null && !process.HasExitedSafe()) {
					process.Kill();
				}

				if (process != null) {
					process.Close();
					process = null;
				}
			}

			// Check if spotify is available.
			if (api.Client == null) {
				Fail($"Failed to setup Librespot: Spotify API access was not set up correctly. Cannot play from spotify.");
				return;
			}

			// Get device ID for librespot.
			// 1. Connect librespot for it to appear in the API.
			var processOption = LaunchLibrespot();
			if (!processOption.Ok) {
				Fail($"Failed to setup Librespot: {processOption.Error}");
				return;
			}

			// 2. Fetch device ID via API.
			var response = api.Request(() => api.Client.Player.GetAvailableDevices());
			if (!response.Ok) {
				Fail($"Failed to setup Librespot: Could not get device ID - {response.Error}.");
				return;
			}

			deviceId = "";
			foreach (var device in response.Value.Devices.Where(device => device.Name == conf.LibrespotDeviceName)) {
				deviceId = device.Id;
			}

			// 3. Check for success.
			if (deviceId == "") {
				Fail("Failed to setup Librespot: Could not get device ID.");
				return;
			}

			// 4. Exit Librespot.
			process.Kill();
			process.Close();
			process = null;

			Log.Trace("Set up Librespot, changed state to idle.");
			state = State.Idle;
		}

		public R<(string, Thread, TimeSpan?), LocalStr> StreamSongToPipeHandle(string spotifyTrackUri) {
			var trackId = SpotifyApi.UriToTrackId(spotifyTrackUri);
			if (!trackId.Ok) {
				return new LocalStr("Cannot stream this URI from spotify.");
			}

			if (state == State.NotSetUp) {
				return new LocalStr("Spotify API access was not set up correctly. Cannot play from spotify.");
			}

			if (state != State.Idle) {
				throw new InvalidOperationException(
					$"Tried to stream spotify song while the system was not idle (current state: {state})."
				);
			}

			// Launch Librespot.
			state = State.LaunchingLibrespot;
			Log.Debug("Launching Librespot...");
			var processOption = LaunchLibrespot();
			if (!processOption.Ok) {
				return processOption.Error;
			}

			// Start audio streaming to ffmpeg.
			Log.Debug("Starting stream...");
			var pipeServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
			var handle = "pipe:" + pipeServer.GetClientHandleAsString();
			var totalBytesSent = 0;

			void Exit(string message, bool stats = true) {
				pipeServer.Dispose();

				if (!process.HasExitedSafe()) {
					process.Kill();
				}
				process.Close();

				state = State.Idle;

				var msg = message;
				if (stats) {
					msg += $" Sent {byteSizeFormatter.Format(totalBytesSent)} bytes in total.";
				}
				Log.Debug(msg);
			}

			var byteReaderThread = new Thread(() => {
				var bytesRead = -1;
				var buffer = new byte[BytesPerChunk];
				while (true) {
					try {
						bytesRead = process.StandardOutput.BaseStream.Read(buffer, 0, BytesPerChunk);
					} catch (IOException e) {
						Exit($"Reading from Librespot failed: {e}.");
						return;
					}

					if (bytesRead == 0) {
						// Librespot exited, no more data coming.
						Exit("All spotify streaming data sent to ffmpeg.");
						return;
					}

					try {
						pipeServer.Write(buffer, 0, bytesRead);
					} catch (IOException) {
						Exit("Ffmpeg went away before all spotify stream data was sent.");
						return;
					}

					if (totalBytesSent == 0) {
						// Necessary to dispose the handle after ffmpeg connected to receive notice when ffmpeg exits.
						pipeServer.DisposeLocalCopyOfClientHandle();
					}

					totalBytesSent += bytesRead;
				}
			}) {
				IsBackground = true
			};

			// Start song.
			var playResult = api.Request(
				() => api.Client.Player.ResumePlayback(new PlayerResumePlaybackRequest {
					DeviceId = deviceId,
					Uris = new List<string> { spotifyTrackUri }
				})
			);
			if (!playResult.Ok) {
				Exit(playResult.Error.ToString());
				return playResult.Error;
			}

			TimeSpan duration;

			// Check if the song actually started playing.
			var checkCount = 0;
			var stopwatch = new Stopwatch();
			stopwatch.Start();
			while (true) {
				checkCount++;

				Log.Trace($"Checking if song is playing, try {checkCount}, trying since {stopwatch.Elapsed:c}, limit is {TrackStartTimeout:c}.");

				if (stopwatch.Elapsed > TrackStartTimeout) {
					stopwatch.Stop();
					const string message = "Song did not start in time, skipping.";
					Exit(message, false);
					return new LocalStr(message);
				}

				var checkResult = api.Request(() => api.Client.Player.GetCurrentPlayback());
				Log.Trace($"Result of currently playing check: {checkResult.Ok}");
				if (!checkResult.Ok) {
					Exit(checkResult.Error.ToString(), false);
					return checkResult.Error;
				}
				var currentlyPlaying = checkResult.Value;

				if (currentlyPlaying.CurrentlyPlayingType != "track") {
					Log.Trace("The currently playing song is not a track.");
					continue;
				}

				var track = (FullTrack) currentlyPlaying.Item;

				if (
					currentlyPlaying.IsPlaying
				    && currentlyPlaying.Device.Id == deviceId
					&& track.Id == trackId.Value
				) {
					stopwatch.Stop();
					duration = TimeSpan.FromMilliseconds(track.DurationMs);
					state = State.StreamRunning;
					Log.Trace("Song is playing now. Continuing.");
					break;
				}
			}

			return (handle, byteReaderThread, duration);
		}

		private E<LocalStr> LaunchLibrespot() {
			output.Clear();

			if (process != null && !process.HasExitedSafe()) {
				process.Kill();
			}

			if (process != null) {
				process.Close();
				process = null;
			}

			process = new Process {
				StartInfo = new ProcessStartInfo {
					FileName = conf.LibrespotPath.Value,
					Arguments = string.Format(LibrespotArgs, conf.LibrespotUser, conf.LibrespotPassword, conf.LibrespotDeviceName),
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				},
				EnableRaisingEvents = true
			};
			process.Start();
			process.ErrorDataReceived += ParseError;
			process.BeginErrorReadLine();

			void Exit() {
				if (!process.HasExitedSafe()) {
					process.Kill();
				}
				process.Close();

				Log.Trace("Set Librespot state back to idle.");
				state = State.Idle;
			}

			// Wait for successful launch of librespot.
			var stopWatch = new Stopwatch();
			stopWatch.Start();
			while (state != State.LibrespotRunning) {
				if (state == State.LibrespotFailed) {
					Exit();
					return new LocalStr(
						$"Librespot failed to authenticate, check authentication information in the config!" +
						$" Output:\n{string.Join("\n", output)}"
					);
				}

				if (stopWatch.Elapsed > LibrespotStartTimeout) {
					Exit();
					return new LocalStr($"Librespot did not launch in time. Output:\n{string.Join("\n", output)}");
				}

				Thread.Sleep(10);
			}

			return R.Ok;
		}

		private void ParseError(object sender, DataReceivedEventArgs e) {
			if (e.Data == null) {
				return;
			}

			output.Add(e.Data);

			var badAuthMatch = BadAuthMatcher.Match(e.Data);
			if (badAuthMatch.Success) {
				Log.Trace("Set Librespot state to failed.");
				state = State.LibrespotFailed;
			}

			var goodAuthMatch = GoodAuthMatcher.Match(e.Data);
			if (goodAuthMatch.Success) {
				Log.Trace("Set Librespot state to running.");
				state = State.LibrespotRunning;
			}
		}
	}
}
