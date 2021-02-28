using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;

namespace TS3AudioBot.Web {
	public class SpotifyApi {
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private static readonly string SpotifyTrackUriPrefix = "spotify:track:";
		private static readonly Uri CallbackUrl = new Uri("http://localhost:4562/callback");
		private static readonly Regex SpotifyCallbackCodeMatcher = new Regex(CallbackUrl.ToString().Replace("/", "\\/") + "\\?code=(.*)$");

		public SpotifyClient Client { get; private set; }

		private readonly ConfSpotify config;
		private readonly ConfRoot rootConfig;

		public SpotifyApi(ConfRoot rootConfig) {
			config = rootConfig.Tools.Spotify;
			this.rootConfig = rootConfig;

			if (
				string.IsNullOrWhiteSpace(config.SpotifyAccessToken)
				&& string.IsNullOrWhiteSpace(config.SpotifyRefreshToken)
			) {
				// Get new spotify access and refresh tokens.

				if (string.IsNullOrWhiteSpace(config.SpotifyAPIClientId) ||
				    string.IsNullOrWhiteSpace(config.SpotifyAPIClientSecret)) {
					Log.Error("Failed to setup spotify API: Invalid user input for client ID or secret.");
					return;
				}

				var loginRequest = new LoginRequest(
					CallbackUrl,
					config.SpotifyAPIClientId,
					LoginRequest.ResponseType.Code
				) {
					Scope = new[] {
						Scopes.UserReadPlaybackState,
						Scopes.UserModifyPlaybackState
					}
				};
				Console.WriteLine(strings.login_to_spotify_using_this_url, loginRequest.ToUri());
				Console.Write(strings.copy_redirect_url_here);
				var resultUrl = Console.ReadLine();
				if (string.IsNullOrWhiteSpace(resultUrl)) {
					Log.Error("Failed to setup spotify API: Invalid user input for the callback URL.");
					return;
				}

				var match = SpotifyCallbackCodeMatcher.Match(resultUrl);
				if (!match.Success) {
					Log.Error("Failed to setup spotify API: Code not found in callback URL.");
					return;
				}

				var tokenRequestTask = new OAuthClient().RequestToken(
					new AuthorizationCodeTokenRequest(
						config.SpotifyAPIClientId, config.SpotifyAPIClientSecret,
						match.Groups[1].Value, CallbackUrl
					)
				);

				var result = ResolveRequestTask(tokenRequestTask, false);
				if (!result.Ok) {
					Log.Error("Failed to setup spotify API: Code to token translation failed.");
					return;
				}

				if (tokenRequestTask.IsFaulted) {
					Log.Error("Failed to setup spotify API: Code to token translation failed.");
					return;
				}

				config.SpotifyAccessToken.Value = tokenRequestTask.Result.AccessToken;
				config.SpotifyRefreshToken.Value = tokenRequestTask.Result.RefreshToken;

				rootConfig.Save();
			}

			// Create spotify client.
			Client = new SpotifyClient(config.SpotifyAccessToken);
		}

		public static R<string, LocalStr> UriToTrackId(string uri) {
			if (!uri.Contains(SpotifyTrackUriPrefix)) {
				return new LocalStr("Invalid spotify track URI.");
			}

			return uri.Replace(SpotifyTrackUriPrefix, "");
		}

		public static string TrackToName(FullTrack track) {
			return $"{string.Join(", ", track.Artists.Select(artist => artist.Name))} - {track.Name}";
		}

		public R<FullTrack, LocalStr> UriToTrack(string uri) {
			var trackId = UriToTrackId(uri);
			if (!trackId.Ok) {
				return trackId.Error;
			}

			var response = Request(() => Client.Tracks.Get(trackId.Value));
			if (!response.Ok) {
				return response.Error;
			}

			return response.Value;
		}

		public R<T, LocalStr> Request<T>(Func<Task<T>> requestFunction) {
			var task = requestFunction();

			// Retry in case the first task only failed because the access token expired.
			for (var i = 0; i < 2; i++) {
				var result = ResolveRequestTask(task, i == 0);
				if (result.Ok) {
					break;
				}

				// Retry already done.
				if (i != 0) {
					return result.Error;
				}

				// Retry the request.
				task = requestFunction();
			}

			if (task.IsFaulted) {
				return new LocalStr("Failed request to spotify API.");
			}

			return task.Result;
		}

		private E<LocalStr> ResolveRequestTask(Task task, bool refresh = true) {
			try {
				task.Wait();
			} catch (AggregateException ae) {
				var messages = new List<string>();

				foreach (var e in ae.InnerExceptions) {
					if (!(e is APIUnauthorizedException) || !refresh) {
						messages.Add(e.ToString());
						continue;
					}

					// Refresh access token.
					var tokenRefreshTask = new OAuthClient().RequestToken(
						new AuthorizationCodeRefreshRequest(
							config.SpotifyAPIClientId,
							config.SpotifyAPIClientSecret,
							config.SpotifyRefreshToken
						)
					);

					var result = ResolveRequestTask(tokenRefreshTask, false);
					if (result.Ok && !tokenRefreshTask.IsFaulted) {
						Client = new SpotifyClient(tokenRefreshTask.Result.AccessToken);
						config.SpotifyAccessToken.Value = tokenRefreshTask.Result.AccessToken;
						rootConfig.Save();
					}

					return new LocalStr("Spotify access token was expired, try again.");
				}

				return new LocalStr($"Failed request to spotify API: {string.Join(", ", messages)}");
			}

			return R.Ok;
		}
	}
}
