namespace Memoria.Core.Redaction;

/// <summary>Says whether an assignment's name holds a credential, by splitting it into words, so <c>max_tokens</c> and <c>tokenizer</c> are not mistaken for <c>token</c>.</summary>
internal static class SecretKeyNames
{
	/// <summary>A name ending in one of these describes a secret rather than holding one: <c>token_count</c>, <c>secret_name</c>, <c>password_file</c>.</summary>
	private static readonly HashSet<string> DescriptiveSuffixes = new(StringComparer.OrdinalIgnoreCase)
	{
		"count", "counts", "type", "types", "name", "names", "url", "uri", "path", "paths", "file", "files", "dir", "length", "len", "limit", "limits", "size",
		"expiry", "expires", "expiration", "ttl", "field", "label", "prefix", "budget", "usage", "policy", "provider", "endpoint", "mode", "format",
		"version", "enabled", "required", "stdin", "env", "var", "ref", "arn", "source", "store", "manager", "helper", "service", "handler", "class", "command", "prompt",
		"regex", "pattern", "rule", "rules", "error", "errors", "message", "description", "hint", "placeholder", "schema", "validator", "input", "id", "ids",
	};

	/// <summary>Tokens with these immediately before them are bookkeeping, not credentials: <c>pageToken</c>, <c>cancellationToken</c>, <c>max_tokens</c>.</summary>
	private static readonly HashSet<string> NonSecretTokenQualifiers = new(StringComparer.OrdinalIgnoreCase)
	{
		"page", "next", "prev", "previous", "continuation", "cancellation", "sync", "max", "min", "input", "output", "total", "cache",
	};

	/// <summary>A password word with one of these before it names an action or a flag, not a stored value: <c>changePassword</c>, <c>must_reset_password</c>.</summary>
	private static readonly HashSet<string> PasswordActions = new(StringComparer.OrdinalIgnoreCase)
	{
		"change", "reset", "forgot", "verify", "validate", "check", "is", "has", "must", "require", "requires", "get", "handle", "on", "use", "update",
	};

	/// <summary>Key names that are not credentials: database keys, hash keys, public keys, and a key in prose.</summary>
	private static readonly HashSet<string> NonSecretKeyQualifiers = new(StringComparer.OrdinalIgnoreCase)
	{
		"public", "primary", "foreign", "sort", "partition", "cache", "idempotency", "dedup", "object", "map", "hash", "lookup", "composite", "unique",
		"index", "group", "routing", "row", "join", "shard", "message", "event", "query", "ssh", "gpg", "pgp", "file", "record", "storage",
		"the", "a", "an", "this", "that", "your", "my", "our", "each", "any", "by", "of",
	};

	private static readonly string[] PasswordWords = ["password", "passwd", "pwd", "passphrase"];

	private static readonly string[] TokenWords = ["token", "secret"];

	private static readonly HashSet<string> ExactSecretWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"psk", "apikey", "secretkey", "accesskey", "authkey", "auth", "authorization", "credential", "credentials",
	};

	/// <summary>True when the name holds a credential.</summary>
	public static bool IsSecretName(string key)
	{
		List<string> words = SplitWords(key);
		if (words.Count == 0 || EndsDescriptively(words))
		{
			return false;
		}

		for (int i = 0; i < words.Count; i++)
		{
			if (IsSecretWordAt(words, i))
			{
				return true;
			}
		}

		return IsNamedKey(words);
	}

	/// <summary>A command line flag. A bare <c>--key</c> or <c>--pass</c> takes a credential there, unlike a bare <c>key</c> or <c>pass</c> elsewhere.</summary>
	public static bool IsSecretFlag(string key)
	{
		List<string> words = SplitWords(key);
		return (words.Count == 1 && words[0].ToLowerInvariant() is "pass" or "key") || IsSecretName(key);
	}

	/// <summary>Splits <c>x-api_keyValue</c> into <c>x, api, key, Value</c>.</summary>
	internal static List<string> SplitWords(string key)
	{
		List<string> words = [];
		int start = 0;
		for (int i = 0; i < key.Length; i++)
		{
			if (!char.IsAsciiLetterOrDigit(key[i]))
			{
				AddWord(words, key, start, i);
				start = i + 1;
			}
			else if (i > start && char.IsAsciiLetterUpper(key[i]) && char.IsAsciiLetterLower(key[i - 1]))
			{
				AddWord(words, key, start, i);
				start = i;
			}
		}

		AddWord(words, key, start, key.Length);
		return words;
	}

	private static void AddWord(List<string> words, string key, int start, int end)
	{
		if (end > start)
		{
			words.Add(key[start..end]);
		}
	}

	private static bool IsSecretWordAt(List<string> words, int index)
	{
		string word = words[index];
		string? before = index > 0 ? words[index - 1] : null;
		if (EndsWithAny(word, PasswordWords) || IsUpperPassSuffix(word) || IsPassWord(words, word))
		{
			return before is null || !PasswordActions.Contains(before);
		}

		if (EndsWithAny(word, TokenWords))
		{
			return before is null || !word.Equals("token", StringComparison.OrdinalIgnoreCase) || !NonSecretTokenQualifiers.Contains(before);
		}

		return ExactSecretWords.Contains(word);
	}

	/// <summary><c>pass</c> beside another word, as in <c>DB_PASS</c>, <c>user_pass</c> or <c>pass_phrase</c>. Alone it is Python's keyword, a count or a verb.</summary>
	private static bool IsPassWord(List<string> words, string word)
	{
		return words.Count > 1 && word.Equals("pass", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary><c>SSHPASS</c> and <c>MYSQLPASS</c>. Upper case only, because <c>compass</c> and <c>firstpass</c> are ordinary words.</summary>
	private static bool IsUpperPassSuffix(string word)
	{
		return word.Length > 4 && word.EndsWith("PASS", StringComparison.Ordinal) && word is not ("BYPASS" or "COMPASS" or "ENCOMPASS" or "SURPASS");
	}

	/// <summary><c>API_KEY</c>, <c>CONSUMER_KEY</c>, <c>x-functions-key</c>, <c>client-key-data</c>: a key word with a name in front of it that is not a database's or a hash's.</summary>
	private static bool IsNamedKey(List<string> words)
	{
		int keyIndex = words[^1].Equals("data", StringComparison.OrdinalIgnoreCase) ? words.Count - 2 : words.Count - 1;
		return keyIndex >= 1
			&& words[keyIndex].ToLowerInvariant() is "key" or "keys"
			&& !NonSecretKeyQualifiers.Contains(words[keyIndex - 1]);
	}

	/// <summary><c>secret_id</c> is the one <c>_id</c> that holds a credential: Vault's AppRole login secret.</summary>
	private static bool EndsDescriptively(List<string> words)
	{
		string last = words[^1];
		bool secretId = last.Equals("id", StringComparison.OrdinalIgnoreCase) && words.Count >= 2 && words[^2].Equals("secret", StringComparison.OrdinalIgnoreCase);
		return words.Count >= 2 && DescriptiveSuffixes.Contains(last) && !secretId;
	}

	private static bool EndsWithAny(string word, string[] suffixes)
	{
		return suffixes.Any(suffix => word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
	}
}
