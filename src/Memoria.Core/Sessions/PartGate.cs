using System.Text;
using Memoria.Core.Redaction;

namespace Memoria.Core.Sessions;

/// <summary>
/// The last check on a finished part. Everything in it was already redacted, so a hit means assembly created a
/// match. Rather than let one hostile branch name keep a whole session out of the index, the field or message
/// holding the hit is replaced with a typed marker and reported, and the rest is indexed. It never falls
/// back to raw text. Only a blown budget or an internal fault refuses the session. Too many hits, or a part the check
/// cannot finish, costs the part its messages and title instead.
/// </summary>
internal static class PartGate
{
	/// <summary>Stands in for a refused frontmatter field or title. Written so that no rule reads it as a value.</summary>
	public const string FieldMarker = "[REDACTED:refused-field]";

	/// <summary>Stands in for a refused message.</summary>
	public const string MessageMarker = "[REDACTED:refused-message]";

	private const int MaxReplacements = 64;

	private const string FrontmatterEnd = "\n---\n";

	/// <summary>Returns the part with every remaining hit replaced, and adds what was replaced to <paramref name="refusals"/>.</summary>
	/// <exception cref="RedactionException">Replacing what the gate found did not make the part clean.</exception>
	public static RedactedMarkdown Clean(string document, SessionPart part, List<SessionFieldRefusal> refusals)
	{
		string frontmatter = FrontmatterOf(document);
		Body body = Body.Parse(document[frontmatter.Length..], part);
		for (int i = 0; i < MaxReplacements * 2; i++)
		{
			string current = frontmatter + body.Render();
			if (FindHit(current, body, part, refusals, out GateHit? found))
			{
				continue;
			}

			if (found is not { } hit)
			{
				return new RedactedMarkdown(current, new Dictionary<string, int>());
			}

			if (i >= MaxReplacements && hit.Start >= frontmatter.Length && body.RefuseAll(hit.Rule, part, refusals))
			{
				continue;
			}

			if (hit.Start < frontmatter.Length)
			{
				frontmatter = ReplaceField(frontmatter, hit, part, refusals);
			}
			else
			{
				body.Replace(hit.Start - frontmatter.Length, hit.Rule, part, refusals);
			}

			if (frontmatter + body.Render() == current)
			{
				throw new RedactionException(hit.Rule, RedactionFailure.FoundAfterRedaction, part.Number);
			}
		}

		throw new RedactionException(RedactionException.WholeDocument, RedactionFailure.DidNotConverge, part.Number);
	}

	/// <summary>True when the check could not finish, so the body was replaced and the part must be checked again.</summary>
	private static bool FindHit(string text, Body body, SessionPart part, List<SessionFieldRefusal> refusals, out GateHit? hit)
	{
		try
		{
			hit = Redactor.Shared.FindFirstHit(text);
			return false;
		}
		catch (RedactionException exception) when (Redactor.IsCausedByText(exception.Failure) && body.RefuseAll(exception.RuleName, part, refusals))
		{
			hit = null;
			return true;
		}
	}

	private static string FrontmatterOf(string document)
	{
		int close = document.IndexOf(FrontmatterEnd, 3, StringComparison.Ordinal);
		return close < 0 ? "" : document[..(close + FrontmatterEnd.Length)];
	}

	/// <summary>Replaces the whole field holding the hit. Values never hold a raw newline, so an unindented line starts a field.</summary>
	private static string ReplaceField(string frontmatter, GateHit hit, SessionPart part, List<SessionFieldRefusal> refusals)
	{
		string[] lines = frontmatter.Split('\n');
		int line = LineAt(lines, hit.Start);
		int first = line;
		while (first > 0 && lines[first].StartsWith(' '))
		{
			first--;
		}

		int last = line;
		while (last + 1 < lines.Length && lines[last + 1].StartsWith(' '))
		{
			last++;
		}

		int colon = lines[first].IndexOf(':');
		if (colon <= 0)
		{
			throw new RedactionException(hit.Rule, RedactionFailure.FoundAfterRedaction, part.Number);
		}

		string key = lines[first][..colon];
		bool isList = last > first || lines[first].AsSpan(colon + 1).TrimStart().StartsWith("[") || lines[first].Length == colon + 1;
		string replacement = isList ? $"{key}: [\"{FieldMarker}\"]" : $"{key}: \"{FieldMarker}\"";
		refusals.Add(new SessionFieldRefusal(part.Number, key, hit.Rule));
		return string.Join('\n', lines[..first].Append(replacement).Concat(lines[(last + 1)..]));
	}

	private static int LineAt(string[] lines, int position)
	{
		int offset = 0;
		for (int i = 0; i < lines.Length; i++)
		{
			offset += lines[i].Length + 1;
			if (position < offset)
			{
				return i;
			}
		}

		return lines.Length - 1;
	}

	/// <summary>The title and messages of a part, which together render exactly as <see cref="SessionFrontmatter.Body"/> writes them.</summary>
	private sealed class Body
	{
		private readonly SessionMessage[] _messages;
		private string? _title;
		private bool _allRefused;

		private Body(string? title, SessionMessage[] messages)
		{
			_title = title;
			_messages = messages;
		}

		public static Body Parse(string rendered, SessionPart part)
		{
			SessionMessage[] messages = [.. part.Messages];
			int messagesLength = RenderMessages(messages).Length;
			int titleEnd = rendered.Length - messagesLength;
			const string TitleOpen = "\n# ";
			const string TitleClose = "\n\n";
			bool hasTitle = titleEnd > TitleOpen.Length + TitleClose.Length - 1 && rendered.StartsWith(TitleOpen, StringComparison.Ordinal);
			return new Body(hasTitle ? rendered[TitleOpen.Length..(titleEnd - TitleClose.Length)] : null, messages);
		}

		public string Render()
		{
			StringBuilder output = new();
			SessionFrontmatter.Body(output, _title, new ArraySegment<SessionMessage>(_messages));
			return output.ToString();
		}

		public void Replace(int position, string rule, SessionPart part, List<SessionFieldRefusal> refusals)
		{
			int offset = _title is null ? 1 : "\n# ".Length + _title.Length + "\n\n".Length;
			if (position < offset)
			{
				_title = FieldMarker;
				refusals.Add(new SessionFieldRefusal(part.Number, "title", rule));
				return;
			}

			int index = MessageAt(position, offset);
			_messages[index] = _messages[index] with { Text = MessageMarker };
			refusals.Add(new SessionFieldRefusal(part.Number, $"message:{part.MessageStart + index}", rule));
		}

		private int MessageAt(int position, int offset)
		{
			int end = offset;
			for (int i = 0; i < _messages.Length; i++)
			{
				end += BlockLength(i);
				if (position < end)
				{
					return i;
				}
			}

			return _messages.Length - 1;
		}

		private int BlockLength(int index)
		{
			StringBuilder block = new();
			AppendMessage(block, _messages, index);
			return block.Length;
		}

		/// <summary>Replaces the title and every message not already replaced. False when that has already happened, so a further failure is not the body's.</summary>
		public bool RefuseAll(string rule, SessionPart part, List<SessionFieldRefusal> refusals)
		{
			if (_allRefused)
			{
				return false;
			}

			_allRefused = true;
			if (_title is not null and not FieldMarker)
			{
				_title = FieldMarker;
				refusals.Add(new SessionFieldRefusal(part.Number, "title", rule));
			}

			for (int i = 0; i < _messages.Length; i++)
			{
				if (_messages[i].Text != MessageMarker)
				{
					_messages[i] = _messages[i] with { Text = MessageMarker };
					refusals.Add(new SessionFieldRefusal(part.Number, $"message:{part.MessageStart + i}", rule));
				}
			}

			return true;
		}

		private static string RenderMessages(SessionMessage[] messages)
		{
			StringBuilder output = new();
			for (int i = 0; i < messages.Length; i++)
			{
				AppendMessage(output, messages, i);
			}

			return output.ToString();
		}

		private static void AppendMessage(StringBuilder output, SessionMessage[] messages, int index)
		{
			output.Append(index > 0 ? "\n" : "");
			output.Append(messages[index].IsUser ? "## User\n\n" : "## Assistant\n\n").Append(messages[index].Text).Append('\n');
		}
	}
}
