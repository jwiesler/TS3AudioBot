using System;
using System.IO;
using System.Linq;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Web;

namespace TS3AudioBot.ResourceFactories {
	public class SpotifyResolver : IResourceResolver, IThumbnailResolver {
		public string ResolverFor => "spotify";

		private readonly SpotifyApi api;

		public SpotifyResolver(SpotifyApi api) {
			this.api = api;
		}

		public MatchCertainty MatchResource(ResolveContext ctx, string uri) {
			return SpotifyApi.UriToTrackId(uri).Ok ? MatchCertainty.Always : MatchCertainty.Never;
		}

		public R<PlayResource, LocalStr> GetResource(ResolveContext ctx, string uri) {
			var trackOption = api.UriToTrack(uri);
			if (!trackOption.Ok) {
				return trackOption.Error;
			}

			var resource = new AudioResource(uri, SpotifyApi.TrackToName(trackOption.Value), ResolverFor);
			return new PlayResource(resource.ResourceId, resource);
		}

		public R<PlayResource, LocalStr> GetResourceById(ResolveContext ctx, AudioResource resource) {
			return new PlayResource(resource.ResourceId, resource);
		}

		public string RestoreLink(ResolveContext ctx, AudioResource resource) {
			return resource.ResourceId;
		}

		public R<Stream, LocalStr> GetThumbnail(ResolveContext ctx, PlayResource playResource) {
			var trackId = SpotifyApi.UriToTrackId(playResource.BaseData.ResourceId);
			if (!trackId.Ok) {
				return trackId.Error;
			}

			var response = api.Request(() => api.Client.Tracks.Get(trackId.Value));
			if (!response.Ok) {
				return response.Error;
			}

			return WebWrapper.GetResponseUnsafe(
				response.Value.Album.Images.OrderByDescending(item => item.Height).ToList()[0].Url
			);
		}

		public void Dispose() {
		}
	}
}
