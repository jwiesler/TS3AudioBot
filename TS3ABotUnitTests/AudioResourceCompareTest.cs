using System.Collections.Generic;
using NUnit.Framework;
using TS3AudioBot.ResourceFactories;

namespace TS3ABotUnitTests {
	[TestFixture]
	public class AudioResourceCompareTest {
		[Test]
		public void EqualsTest() {
			var tests = new[] {
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A")),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, true)),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, false))
			};

			foreach (var (a, b) in tests) {
				Assert.AreEqual(a, b);
			}
		}

		[Test]
		public void NotEqualsTest() {
			var tests = new[] {
				(new AudioResource("A", "A", "A"), new AudioResource("B", "A", "A")),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "B", "A")),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "B")),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", new Dictionary<string, string> {{"A", "A"}})),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, null, 5))
			};

			foreach (var (a, b) in tests) {
				Assert.AreNotEqual(a, b);
			}
		}
	}
}
