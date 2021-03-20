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
using TS3AudioBot.ResourceFactories;
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

		private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
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
			output = new List<string>();

			void Fail(string message) {
				Log.Error(message);
				state = State.NotSetUp;
				if (process != null && !process.HasExitedSafe()) {
					process.Kill();
				}

				if (process != null) {
					process.Close();
					process = null;
				}
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
			state = State.Idle;
		}

		public R<(string, TimeSpan?), LocalStr> StreamSongToPipeHandle(string spotifyTrackUri) {
			if (!SpotifyApi.UriToTrackId(spotifyTrackUri).Ok) {
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

			var byteReaderThread = new Thread(() => {
				var totalBytesSent = 0;

				void Exit(string message) {
					pipeServer.Dispose();

					if (!process.HasExitedSafe()) {
						process.Kill();
					}
					process.Close();

					state = State.Idle;
					Log.Debug($"{message} Sent {byteSizeFormatter.Format(totalBytesSent)} bytes in total.");
				}

				var buffer = new byte[BytesPerChunk];
				while (true) {
					try {
						var bytesRead = process.StandardOutput.BaseStream.Read(buffer, 0, BytesPerChunk);
						if (bytesRead == 0) {
							// Librespot exited, no more data coming.
							Exit("All spotify streaming data sent to ffmpeg.");
							return;
						}

						pipeServer.Write(buffer, 0, bytesRead);

						if (totalBytesSent == 0) {
							// Necessary to dispose the handle after ffmpeg connected to receive notice when ffmpeg exits.
							pipeServer.DisposeLocalCopyOfClientHandle();
						}

						totalBytesSent += bytesRead;
					} catch (IOException) {
						Exit("Ffmpeg went away before all spotify stream data was sent.");
						return;
					}
				}
			}) {
				IsBackground = true
			};

			// Get song duration.
			TimeSpan? duration = null;
			var trackOption = api.GetTrack(spotifyTrackUri);
			if (trackOption.Ok) {
				duration = TimeSpan.FromMilliseconds(trackOption.Value.DurationMs);
			}

			// Start song.
			var result = api.Request(
				() => api.Client.Player.ResumePlayback(new PlayerResumePlaybackRequest {
					DeviceId = deviceId,
					Uris = new List<string> { spotifyTrackUri }
				})
			);
			if (!result.Ok) {
				return result.Error;
			}

			byteReaderThread.Start();
			state = State.StreamRunning;

			return (handle, duration);
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

				if (stopWatch.Elapsed > StartTimeout) {
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
				state = State.LibrespotFailed;
			}

			var goodAuthMatch = GoodAuthMatcher.Match(e.Data);
			if (goodAuthMatch.Success) {
				state = State.LibrespotRunning;
			}
		}
	}
}
