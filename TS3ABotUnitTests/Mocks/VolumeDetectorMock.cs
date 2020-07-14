using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Audio;

namespace TS3ABotUnitTests.Mocks {
	public class VolumeDetectorMock : IVolumeDetector {
		public const int VolumeSet = 10;

		public int RunVolumeDetection(string url, CancellationToken token) {
			if (token.IsCancellationRequested)
				throw new TaskCanceledException();

			return VolumeSet;
		}
	}
}
