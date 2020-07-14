using System;
using NUnit.Framework;
using TS3AudioBot.Audio;
using TS3AudioBot.Audio.Preparation;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;

namespace TS3ABotUnitTests.Mocks {
	public class StartSongTaskHostMock : StartSongTaskHostBase {
		private QueueItem preparingItem;

		public override QueueItem PreparingItem => preparingItem;
		public int CanceledTasks { get; set; }
		private bool PlayRequested { get; set; }

		public bool GetPlayRequestedReset() {
			var b = PlayRequested;
			PlayRequested = false;
			return b;
		}

		public void Play() {
			Assert.IsNotNull(preparingItem);
			var e = new PlayInfoEventArgs(PreparingItem.MetaData.ResourceOwnerUid, new PlayResource("uri", PreparingItem.AudioResource, PreparingItem.MetaData), "link");
			InvokeBeforeResourceStarted(this, e);
			InvokeAfterResourceStarted(this, e);
		}

		public void FailLoad() {
			Assert.IsNotNull(preparingItem);
			PlayRequested = false;
			var e = new LoadFailureTaskEventArgs(new LocalStr("Error"), PreparingItem, IsCurrentResource);
			InvokeOnLoadFailure(this, e);
		}

		public override void PlayCurrentWhenFinished() {
			Assert.IsNotNull(preparingItem);
			Assert.IsFalse(PlayRequested);
			PlayRequested = true;
		}

		public override void UpdateRemaining(TimeSpan remaining) {
			Assert.IsNotNull(preparingItem);
		}

		protected override void RemoveFinishedTask() { preparingItem = null; }

		protected override void CancelTask() {
			++CanceledTasks;
			preparingItem = null;
		}

		protected override void SetTask(QueueItem item, TimeSpan? remaining) {
			Assert.IsNotNull(item);
			preparingItem = item;
		}
	}
}
