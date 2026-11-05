namespace Memoria.Core.Redaction;

/// <summary>What a name in an assignment says about its value.</summary>
internal enum KeyKind
{
	NotSecret,

	/// <summary>Human-chosen, so low entropy proves nothing.</summary>
	Password,

	/// <summary>Machine-generated, so a real one is long and varied.</summary>
	Token,

	/// <summary>A generic <c>*_key</c> name, or a bare <c>pass</c>. Common in ordinary code, so only a clearly random value counts.</summary>
	KeyFamily,

	/// <summary>A secret word with a descriptive ending, such as <c>token_count</c>. Only a value that could not be a description counts.</summary>
	Descriptive,
}

/// <summary>Classifies assignment names by splitting them into words, so <c>max_tokens</c> and <c>tokenizer</c> are not mistaken for <c>token</c>.</summary>
internal static class SecretKeyNames
{
	/// <summary>A name ending in one of these usually describes a secret rather than holding one: <c>token_count</c>, <c>secret_name</c>, <c>password_field</c>.</summary>
	private static readonly HashSet<string> DescriptiveSuffixes = new(StringComparer.OrdinalIgnoreCase)
	{
		"count", "counts", "type", "types", "name", "names", "url", "uri", "path", "paths", "file", "files", "dir", "length", "len", "limit", "limits", "size",
		"expiry", "expires", "expiration", "ttl", "field", "label", "prefix", "budget", "usage", "header", "policy", "provider", "endpoint", "mode", "format",
		"version", "enabled", "required", "stdin", "ref", "arn", "source", "store", "manager", "helper", "service", "handler", "class", "command", "cmd", "prompt",
		"regex", "pattern", "rule", "rules", "error", "errors", "message", "description", "hint", "placeholder", "schema", "validator", "input",
		"at", "request", "response", "dto", "form", "view", "page", "screen", "component", "route", "button", "dialog", "modal", "model", "state", "props",
		"cs", "ts", "tsx", "js", "jsx", "py", "go", "md", "json", "yaml", "yml", "rs", "java", "rb", "sh", "txt", "html", "css", "sql", "toml",
	};

	/// <summary>Tokens with these immediately before them are bookkeeping, not credentials: <c>pageToken</c>, <c>cancellationToken</c>. <c>reset_token</c> is absent on purpose: it is a live credential.</summary>
	private static readonly HashSet<string> NonSecretTokenQualifiers = new(StringComparer.OrdinalIgnoreCase)
	{
		"page", "next", "prev", "previous", "continuation", "cancellation", "sync", "max", "min", "input", "output", "total", "cache",
		"verify", "validate", "check", "is", "has", "get", "handle", "on", "use",
	};

	/// <summary>A password word with one of these before it names an action or a flag, not a stored value: <c>changePassword</c>, <c>must_reset_password</c>.</summary>
	private static readonly HashSet<string> PasswordActions = new(StringComparer.OrdinalIgnoreCase)
	{
		"change", "reset", "forgot", "verify", "validate", "check", "is", "has", "must", "require", "requires", "needs", "get", "handle", "on", "use", "update",
	};

	/// <summary>Key names that are not credentials however random their value looks: database keys, hash keys, and public keys.</summary>
	private static readonly HashSet<string> NonSecretKeyQualifiers = new(StringComparer.OrdinalIgnoreCase)
	{
		"public", "primary", "foreign", "sort", "partition", "cache", "idempotency", "dedup", "dedupe", "object", "map", "hash", "lookup", "composite", "unique",
		"index", "group", "routing", "row", "join", "shard", "message", "event", "query", "ssh", "gpg", "pgp", "file", "data", "record", "storage", "the", "a", "an", "this", "that", "your", "my", "our", "each", "any", "by", "of", "s3",
	};

	private static readonly HashSet<string> ExactPasswordWords = new(StringComparer.OrdinalIgnoreCase) { "psk" };

	private static readonly HashSet<string> PassLookalikes = new(StringComparer.OrdinalIgnoreCase) { "BYPASS", "COMPASS", "ENCOMPASS", "SURPASS" };

	private static readonly string[] PasswordWords = ["password", "passwd", "pwd", "passphrase"];

	private static readonly string[] TokenWords = ["token", "secret"];

	/// <summary>The word before <c>key</c> in names that always hold a credential, such as <c>api_key</c>.</summary>
	private static readonly HashSet<string> CredentialKeyQualifiers = new(StringComparer.OrdinalIgnoreCase)
	{
		"api", "auth", "access", "private", "secret", "signing", "encryption", "master", "shared",
	};

	private static readonly HashSet<string> ExactTokenWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"apikey", "secretkey", "accesskey", "authkey", "auth", "pass", "authorization", "credential", "credentials",
	};

	public static KeyKind Classify(string key)
	{
		List<string> words = SplitWords(key);
		if (words.Count == 0)
		{
			return KeyKind.NotSecret;
		}

		KeyKind kind = BaseKind(words);
		return kind != KeyKind.NotSecret && EndsDescriptively(words) ? KeyKind.Descriptive : kind;
	}

	/// <summary>Classifies a command line flag. A bare <c>--key</c> or <c>--pass</c> takes a credential there, unlike a bare <c>key</c> or <c>pass</c> elsewhere.</summary>
	public static KeyKind ClassifyFlag(string key)
	{
		List<string> words = SplitWords(key);
		string only = words.Count == 1 ? words[0] : "";
		if (only.Equals("pass", StringComparison.OrdinalIgnoreCase))
		{
			return KeyKind.Password;
		}

		return only.Equals("key", StringComparison.OrdinalIgnoreCase) ? KeyKind.KeyFamily : Classify(key);
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

	private static KeyKind BaseKind(List<string> words)
	{
		KeyKind kind = KeyKind.NotSecret;
		for (int i = 0; i < words.Count; i++)
		{
			if (IsPasswordWordAt(words, i))
			{
				return KeyKind.Password;
			}

			if (IsTokenWordAt(words, i))
			{
				kind = KeyKind.Token;
			}
		}

		if (kind == KeyKind.Token && words.Count == 1 && words[0].Equals("pass", StringComparison.OrdinalIgnoreCase))
		{
			return KeyKind.KeyFamily;
		}

		return kind == KeyKind.NotSecret && IsKeyFamily(words) ? KeyKind.KeyFamily : kind;
	}

	/// <summary><c>CONSUMER_KEY</c>, <c>x-functions-key</c>, <c>client-key-data</c>, and plurals: a key word with a name in front of it.</summary>
	private static bool IsKeyFamily(List<string> words)
	{
		int keyIndex = words[^1].Equals("data", StringComparison.OrdinalIgnoreCase) ? words.Count - 2 : words.Count - 1;
		return keyIndex >= 1 && IsKeyWord(words[keyIndex]) && !NonSecretKeyQualifiers.Contains(words[keyIndex - 1]);
	}

	private static bool IsKeyWord(string word)
	{
		return word.Equals("key", StringComparison.OrdinalIgnoreCase) || word.Equals("keys", StringComparison.OrdinalIgnoreCase);
	}

	private static void AddWord(List<string> words, string key, int start, int end)
	{
		if (end > start)
		{
			words.Add(key[start..end]);
		}
	}

	/// <summary><c>secret_id</c> is the one <c>_id</c> that holds a credential: Vault's AppRole login secret.</summary>
	private static bool EndsDescriptively(List<string> words)
	{
		string last = words[^1];
		if (!last.Equals("id", StringComparison.OrdinalIgnoreCase))
		{
			return DescriptiveSuffixes.Contains(last);
		}

		return words.Count < 2 || !words[^2].Equals("secret", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsPasswordWordAt(List<string> words, int index)
	{
		string word = words[index];
		bool named = EndsWithAny(word, PasswordWords) || ExactPasswordWords.Contains(word) || IsUpperPassSuffix(word);
		return named && !(index > 0 && PasswordActions.Contains(words[index - 1]));
	}

	/// <summary><c>SSHPASS</c> and <c>MYSQLPASS</c>. Upper case only, because <c>compass</c> and <c>firstpass</c> are ordinary words.</summary>
	private static bool IsUpperPassSuffix(string word)
	{
		return word.Length > 4 && word.EndsWith("PASS", StringComparison.Ordinal) && !PassLookalikes.Contains(word);
	}

	private static bool IsTokenWordAt(List<string> words, int index)
	{
		string word = words[index];
		if (EndsWithAny(word, TokenWords))
		{
			return !IsBookkeepingToken(words, index);
		}

		return ExactTokenWords.Contains(word) || IsKeyPairAt(words, index);
	}

	private static bool IsKeyPairAt(List<string> words, int index)
	{
		return index + 1 < words.Count
			&& IsKeyWord(words[index + 1])
			&& CredentialKeyQualifiers.Contains(words[index]);
	}

	private static bool IsBookkeepingToken(List<string> words, int index)
	{
		return index > 0
			&& words[index].Equals("token", StringComparison.OrdinalIgnoreCase)
			&& NonSecretTokenQualifiers.Contains(words[index - 1]);
	}

	private static bool EndsWithAny(string word, string[] suffixes)
	{
		return suffixes.Any(suffix => word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
	}
}
