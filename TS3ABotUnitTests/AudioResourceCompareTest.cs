using System.Collections.Generic;
using NUnit.Framework;
using TS3AudioBot.ResourceFactories;

namespace TS3ABotUnitTests {
	[TestFixture]
	public class AudioResourceCompareTest {
		private static readonly (AudioResource, AudioResource)[] EqualsTests = {
			(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A")),
			(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, true)),
			(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, false)),
			(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, null, 5))
		};

		[Test]
		public void EqualsTest() {
			foreach (var (a, b) in EqualsTests) {
				Assert.AreEqual(a, b);
			}
		}

		private static readonly (AudioResource, AudioResource)[] NotEqualsTests = {
			(new AudioResource("A", "A", "A"), new AudioResource("B", "A", "A")),
			(new AudioResource("A", "A", "A"), new AudioResource("A", "B", "A")),
			(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "B")),
			(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", new Dictionary<string, string> {{"A", "A"}}))
		};

		[Test]
		public void NotEqualsTest() {
			foreach (var (a, b) in NotEqualsTests) {
				Assert.AreNotEqual(a, b);
			}
		}

		[Test]
		public void NotReallyEqualsTest() {
			var tests = new[] {
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, true)),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, false)),
				(new AudioResource("A", "A", "A"), new AudioResource("A", "A", "A", null, false, 5)),
			};

			foreach (var (a, b) in NotEqualsTests) {
				Assert.IsFalse(a.ReallyEquals(b));
			}

			foreach (var (a, b) in tests) {
				Assert.IsFalse(a.ReallyEquals(b));
			}
		}
	}
}
