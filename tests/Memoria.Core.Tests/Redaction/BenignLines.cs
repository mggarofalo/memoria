using System.Text.RegularExpressions;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// The lines of <c>Fixtures/benign-lines.txt</c>, ordinary transcript text that holds no credential, with each placeholder filled in from
/// <see cref="Synthetic"/>. The same placeholder in the same place always gets the same value, so a failure can be reproduced.
/// </summary>
public static partial class BenignLines
{
	private const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

	public static IReadOnlyList<string> All { get; } = Load();

	public static TheoryData<string> Data()
	{
		TheoryData<string> data = [];
		foreach (string line in All)
		{
			data.Add(line);
		}

		return data;
	}

	private static List<string> Load()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Redaction", "Fixtures", "benign-lines.txt");
		List<string> lines = [];
		foreach (string raw in File.ReadAllLines(path))
		{
			if (raw.Length > 0 && !raw.StartsWith('#'))
			{
				int seed = 7000 + (lines.Count * 10);
				lines.Add(Placeholder().Replace(raw, match => Fill(match, ref seed)));
			}
		}

		return lines;
	}

	private static string Fill(Match match, ref int seed)
	{
		seed++;
		int length = match.Groups["length"].Success ? int.Parse(match.Groups["length"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
		return match.Groups["kind"].Value switch
		{
			"hex" => Synthetic.Hex(length, seed),
			"digits" => Synthetic.Digits(length, seed),
			"base64" => Synthetic.Base64(length, seed),
			"alnum" => Synthetic.Body(length, seed),
			"uuid" => $"{Synthetic.Hex(8, seed)}-{Synthetic.Hex(4, seed + 1)}-4{Synthetic.Hex(3, seed + 2)}-a{Synthetic.Hex(3, seed + 3)}-{Synthetic.Hex(12, seed + 4)}",
			_ => throw new InvalidOperationException("Unknown placeholder " + match.Value),
		};
	}

	[GeneratedRegex(@"\{(?<kind>hex|digits|base64|alnum|uuid)(?::(?<length>\d+))?\}")]
	private static partial Regex Placeholder();
}
