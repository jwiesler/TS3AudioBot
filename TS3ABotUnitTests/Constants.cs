using System;
using System.Collections.Generic;
using System.Text;
using TS3AudioBot.Audio;
using TS3AudioBot.Config;
using TS3AudioBot.ResourceFactories;
using TSLib;

namespace TS3ABotUnitTests
{
	public class Constants {
		public static readonly AudioResource Resource1AC = new AudioResource("1", "A", "C");
		public static readonly AudioResource Resource1AYoutube = new AudioResource("1", "A", "youtube");
		public static readonly AudioResource Resource2BYoutube = new AudioResource("2", "B", "youtube");
		public static readonly AudioResource Resource1AYoutubeGain = Resource1AYoutube.WithGain(5);
		public static readonly AudioResource Resource2BYoutubeGain = Resource2BYoutube.WithGain(10);
		public static readonly Uid TestUid = Uid.To("Test");
		public static readonly Uid UidA = Uid.To("A");
		public static readonly ConfAudioVolume VolumeConfig = CreateAudioVolume();
		public const string ListId = "CoolPlaylist";
		public const string AnotherListId = "AnotherCoolPlaylist";
		public const string ListIdMixedCase = "CoOlPlAyLiSt";
		public const int QueueItemGain = 10;

		private static ConfAudioVolume CreateAudioVolume() {
			var config = new ConfAudioVolume();
			config.Min.Value = 0;
			config.Max.Value = 10;
			return config;
		}

		public static AudioResource ResourceWithIndex(int index, string salt) {
			return new AudioResource("id_" + index + '_' + salt, "Title " + index, "youtube", null, null, QueueItemGain);
		}

		public static List<AudioResource> GenerateAudioResources(int count, string salt = "0") {
			var items = new List<AudioResource>(count);

			for (var i = 0; i < count; ++i) {
				var item = ResourceWithIndex(i, salt);
				items.Add(item);
			}

			return items;
		}

		public static QueueItem QueueItemWithIndex(int index, MetaData meta, string salt) {
			return new QueueItem(ResourceWithIndex(index, salt), meta);
		}

		public static List<QueueItem> GenerateQueueItems(int count, string salt = "0") {
			var queueItems = new List<QueueItem>(count);

			var meta = new MetaData(TestUid, ListId);
			for (var i = 0; i < count; ++i) {
				var item = QueueItemWithIndex(i, meta, salt);
				queueItems.Add(item);
			}

			return queueItems;
		}
	}
}
