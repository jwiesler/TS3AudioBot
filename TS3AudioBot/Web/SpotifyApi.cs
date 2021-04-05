using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
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
			Client = new SpotifyClient(config.SpotifyAccessToken);

			// Get the market of the bot.
			var userData = Request(() => Client.UserProfile.Current());
			if (!userData.Ok) {
				Log.Error($"Failed to setup spotify API: Could not get user data ({userData.Error}).");
				Client = null;
				return;
			}

			// Reshaper is lying, it is null if the user-read-private permission is missing.
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse
			// ReSharper disable once HeuristicUnreachableCode
			if (userData.Value.Country == null) {
				// ReSharper disable once HeuristicUnreachableCode

				Log.Error("Failed to setup spotify API: Country of the spotify user is invalid.");

				// Reset access and refresh token as they are using the wrong permissions.
				config.SpotifyAccessToken.Value = "";
				config.SpotifyRefreshToken.Value = "";
				rootConfig.Save();

				Client = null;
				return;
			}

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
			return $"{string.Join(", ", track.Artists.Select(artist => artist.Name).Take(3))} - {track.Name}";
		}

		public R<FullTrack, LocalStr> UriToTrack(string uri) {
			var trackId = UriToTrackId(uri);
			if (!trackId.Ok) {
				return trackId.Error;
			}

			return TrackIdToTrack(trackId.Value);
		}

		public R<FullTrack, LocalStr> TrackIdToTrack(string trackId) {
			var response = Request(() => Client.Tracks.Get(trackId, new TrackRequest { Market = market }));
			if (!response.Ok) {
				return response.Error;
			}

			var track = response.Value;
			var trackName = SpotifyApi.TrackToName(track);
			var availableMarkets = track.AvailableMarkets;
			var restrictions = track.Restrictions;

			// Check if the track was not relinked but is not available on the bots market.
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse - reshaper is lying because the C# spotify api implementation is weird.
			if (availableMarkets != null && !track.AvailableMarkets.Contains(market)) {
				Log.Trace($"Markets ({market}): " + string.Join(", ", track.AvailableMarkets));
				return new LocalStr($"The track '{trackName}' is not available on the market of the bots spotify account.");
			}

			// Check if the track could not be relinked.
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse - reshaper is lying because the C# spotify api implementation is weird.
			if (restrictions != null && restrictions.Count != 0) {
				if (restrictions.ContainsKey("reason")) {
					switch (restrictions["reason"]) {
					case "market":
						return new LocalStr($"The track '{trackName}' is not available on the market of the bots spotify account.");
					case "product":
						return new LocalStr($"The track '{trackName}' cannot be played by the bots spotify subscription.");
					case "explicit":
						return new LocalStr($"The track '{trackName}' is marked explicit and the bots spotify account is set to not play explicit content.");
					default:
						return new LocalStr($"The track '{trackName}' could not be relinked because of '{restrictions["reason"]}'.");
					}
				}

				var restrictionString = string.Join(", ", restrictions.Select(
					kv => $"{kv.Key} - {kv.Value}"
				));
				return new LocalStr($"The track '{trackName}' could not be relinked: " + restrictionString);
			}

			return response.Value;
		}

		public R<T, LocalStr> Request<T>(Func<Task<T>> requestFunction) where T : new() {
			var task = requestFunction();
			var refreshed = false;
			var ratelimitHonored = 0;

			// Retry in case the first task only failed because the access token expired.
			while (true) {
				var result = ResolveRequestTask(task, !refreshed);
				if (result.Ok) {
					break;
				}

				if (result.Error.Item1 != TimeSpan.Zero) {
					// Rate limit was already honored three times, don't do that again.
					if (ratelimitHonored >= 3) {
						return result.Error.Item2;
					}

					// Retry after given time period. Wait a bit longer for good measure and potential timing problems.
					var waitTime = result.Error.Item1.Add(TimeSpan.FromSeconds(2));
					Log.Warn($"Rate limit exceeded, retrying in {waitTime}.");
					Thread.Sleep(waitTime);
					ratelimitHonored++;
				} else {
					if (refreshed) {
						// Refreshing was already tried.
						return result.Error.Item2;
					}
					refreshed = true;
				}

				// Retry the request.
				task = requestFunction();
			}

			if (task.IsFaulted) {
				return new LocalStr("Failed request to spotify API.");
			}

			if (task.Result == null) {
				// return default T instead because returning null for Success is not allowed.
				return new T();
			}

			return task.Result;
		}

		public E<(TimeSpan, LocalStr)> ResolveRequestTask(Task task, bool refresh = true) {
			try {
				task.Wait();
			} catch (AggregateException ae) {
				var retryIn = TimeSpan.Zero;
				var messages = new List<string>();

				foreach (var e in ae.InnerExceptions) {
					if (e is APITooManyRequestsException tooManyRequestsException) {
						retryIn = tooManyRequestsException.RetryAfter;
						messages.Add("Rate limit exceeded, please try again after the given timespan.");
					} else if (e is APIUnauthorizedException && refresh) {
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

						messages.Add("Spotify access token was expired, try again.");
					} else {
						messages.Add(e.ToString());
					}
				}

				return (retryIn, new LocalStr($"Failed request to spotify API: {string.Join(", ", messages)}"));
			}

			return R.Ok;
		}
	}
}
