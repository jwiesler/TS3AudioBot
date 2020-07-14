using System;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3ABotUnitTests.Mocks {
	public class LoaderContextMock : ILoaderContext {
		public bool ShouldReturnNoRestoredLink { get; set; }
		public bool ShouldFailLoad { get; set; }
		public event EventHandler AfterLoad;
		
		public const string NoRestoredLinkMessage = "NoRestoredLinkMessage";
		public const string LoadFailedMessage = "LoadFailedMessage";
		public const string RestoredLink = "Restored link";

		public R<string, LocalStr> RestoreLink(AudioResource res) {
			if (ShouldReturnNoRestoredLink)
				return new LocalStr(NoRestoredLinkMessage);
			return RestoredLink;
		}

		public static string MakeResourceURI(string id, int index) {
			return id + "_" + index;
		}

		public int LoadedResources { get; private set; }
		public R<PlayResource, LocalStr> Load(AudioResource resource) {
			try {
				if (ShouldFailLoad)
					return new LocalStr(LoadFailedMessage);
				return new PlayResource(MakeResourceURI(resource.ResourceId, LoadedResources), resource);
			} finally {
				LoadedResources++;
				AfterLoad?.Invoke(this, EventArgs.Empty);
			}
		}
	}
}
