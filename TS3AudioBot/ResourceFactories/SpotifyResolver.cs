using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpotifyAPI.Web;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Web;

namespace TS3AudioBot.ResourceFactories {
	public class SpotifyResolver : IResourceResolver, IThumbnailResolver, ISearchResolver {
		public string ResolverFor => "spotify";

		private const int SearchLimit = 1000;

		private readonly SpotifyApi api;

		public SpotifyResolver(SpotifyApi api) {
			this.api = api;
		}

		public MatchCertainty MatchResource(ResolveContext ctx, string uri) {
			if (SpotifyApi.UriToTrackId(uri).Ok || SpotifyApi.UrlToTrackId(uri).Ok) {
				return MatchCertainty.Always;
			}

			return MatchCertainty.Never;
		}

		public R<PlayResource, LocalStr> GetResource(ResolveContext ctx, string uriOrUrl) {
			string uri;

			// Convert if it is a URL.
			var uriResult = SpotifyApi.UrlToUri(uriOrUrl);
			if (uriResult.Ok) {
				uri = uriResult.Value;
			} else {
				uri = uriOrUrl;
			}

			var trackOption = api.UriToTrack(uri);
			if (!trackOption.Ok) {
				return trackOption.Error;
			}

			var resource = new AudioResource(uri, SpotifyApi.TrackToName(trackOption.Value), ResolverFor);
			return new PlayResource(resource.ResourceId, resource);
		}

		public R<PlayResource, LocalStr> GetResourceById(ResolveContext ctx, AudioResource resource) {
			// Check if the track is available on the market of the bots spotify account.
			var trackOption = api.UriToTrack(resource.ResourceId);
			if (!trackOption.Ok) {
				return trackOption.Error;
			}

			return new PlayResource(resource.ResourceId, resource);
		}

		public string RestoreLink(ResolveContext ctx, AudioResource resource) {
			return SpotifyApi.UriToUrl(resource.ResourceId).OkOr(null);
		}

		public R<Stream, LocalStr> GetThumbnail(ResolveContext ctx, PlayResource playResource) {
			var urlOption = GetThumbnailUrl(ctx, playResource);
			if (!urlOption.Ok) {
				return urlOption.Error;
			}

			return WebWrapper.GetResponseUnsafe(urlOption.Value);
		}

		public R<Uri, LocalStr> GetThumbnailUrl(ResolveContext ctx, PlayResource playResource) {
			var trackId = SpotifyApi.UriToTrackId(playResource.BaseData.ResourceId);
			if (!trackId.Ok) {
				return trackId.Error;
			}

			var response = api.Request(() => api.Client.Tracks.Get(trackId.Value));
			if (!response.Ok) {
				return response.Error;
			}

			return new Uri(response.Value.Album.Images.OrderByDescending(item => item.Height).ToList()[0].Url);
		}

		public R<IList<AudioResource>, LocalStr> Search(ResolveContext ctx, string keyword) {
			var searchResult = api.Request(() => api.Client.Search.Item(
				new SearchRequest(SearchRequest.Types.Track, keyword))
			);


			if (!searchResult.Ok) {
				return searchResult.Error;
			}

			if (searchResult.Value.Tracks.Total > SearchLimit) {
				return new LocalStr("Too many search results, please make your search query more specific.");
			}

			var pagesTask = api.Client.PaginateAll(searchResult.Value.Tracks, (s) => s.Tracks);

			var pagesTaskResolveResult = api.ResolveRequestTask(pagesTask);
			if (!pagesTaskResolveResult.Ok) {
				return pagesTaskResolveResult.Error;
			}

			var result = new List<AudioResource>();
			foreach (var item in pagesTask.Result) {
				result.Add(new AudioResource(item.Uri, SpotifyApi.TrackToName(item), ResolverFor));
			}

			return result;
		}


		public void Dispose() {
		}
	}
}
