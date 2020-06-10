using System.Text;

namespace TS3AudioBot
{
	public static class StringNormalize
	{
		public static string Normalize(string str) { return str?.Normalize(NormalizationForm.FormC); }
	}
}
