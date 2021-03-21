using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
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

		private static readonly Regex SpotifyUrlMatcher =
			new Regex("https:\\/\\/open.spotify.com\\/track\\/([a-zA-Z0-9]*)(\\?.*)?");
		private static readonly Regex SpotifyCallbackCodeMatcher =
			new Regex(CallbackUrl.ToString().Replace("/", "\\/") + "\\?code=(.*)$");

		public SpotifyClient Client { get; private set; }

		private readonly ConfSpotify config;
		private readonly ConfRoot rootConfig;
		private readonly string market;

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
						Scopes.UserModifyPlaybackState,
						Scopes.UserReadPrivate
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

				if (
					config.SpotifyAccessToken == null
					|| config.SpotifyRefreshToken == null
				) {
					Log.Error("Failed to setup spotify API: Token config entries are invalid.");
					return;
				}

				config.SpotifyAccessToken.Value = tokenRequestTask.Result.AccessToken;
				config.SpotifyRefreshToken.Value = tokenRequestTask.Result.RefreshToken;

				rootConfig.Save();
			}

			// Create spotify client.
			var client = new SpotifyClient(config.SpotifyAccessToken);

			// Get the market of the bot.
			var userData = Request(() => client.UserProfile.Current());
			if (!userData.Ok) {
				Log.Error("Failed to setup spotify API: Could not get user data.");
				return;
			}

			// Reshaper is lying, it is null if the user-read-private permission is missing.
			if (userData.Value.Country == null) {
				Log.Error("Failed to setup spotify API: Country of the spotify user is invalid.");

				// Reset access and refresh token as they are using the wrong permissions.
				config.SpotifyAccessToken.Value = "";
				config.SpotifyRefreshToken.Value = "";
				rootConfig.Save();

				return;
			}

			Client = client;
			market = userData.Value.Country;
		}

		public static R<string, LocalStr> UrlToTrackId(string url) {
			var match = SpotifyUrlMatcher.Match(url);
			if (!match.Success) {
				return new LocalStr("Invalid spotify track URL.");
			}

			return match.Groups[1].Value;
		}

		public static R<string, LocalStr> UriToTrackId(string uri) {
			if (!uri.Contains(SpotifyTrackUriPrefix)) {
				return new LocalStr("Invalid spotify track URI.");
			}

			return uri.Replace(SpotifyTrackUriPrefix, "");
		}

		public static R<string, LocalStr> UriToUrl(string uri) {
			if (!uri.Contains(SpotifyTrackUriPrefix)) {
				return new LocalStr("Invalid spotify track URI.");
			}

			return "https://open.spotify.com/track/" + uri.Replace(SpotifyTrackUriPrefix, "");
		}

		public static R<string, LocalStr> UrlToUri(string url) {
			var match = SpotifyUrlMatcher.Match(url);
			if (!match.Success) {
				return new LocalStr("Invalid spotify track URL.");
			}

			return SpotifyTrackUriPrefix + match.Groups[1].Value;
		}

		public static string TrackToName(FullTrack track) {
			return $"{string.Join(", ", track.Artists.Select(artist => artist.Name))} - {track.Name}";
		}

		public R<FullTrack, LocalStr> UriToTrack(string uri) {
			var trackId = UriToTrackId(uri);
			if (!trackId.Ok) {
				return trackId.Error;
			}

			return TrackIdToTrack(trackId.Value);
		}

		public R<FullTrack, LocalStr> TrackIdToTrack(string trackId) {
			var response = Request(() => Client.Tracks.Get(trackId));
			if (!response.Ok) {
				return response.Error;
			}

			// Check if it is available on the bots market.
			if (!response.Value.AvailableMarkets.Contains(market)) {
				return new LocalStr("This track is not available for the registered spotify account.");
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

		public E<LocalStr> ResolveRequestTask(Task task, bool refresh = true) {
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
