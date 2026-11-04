using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>
/// Pins every entry of the judges' word lists. The expected words are written out
/// here rather than read from the lists, so dropping or changing an entry fails a
/// test: a removed word is a silent change in what gets redacted.
/// </summary>
public sealed class WordListTests
{
	[Theory]
	[InlineData("count", "counts", "type", "types", "name", "names", "url", "uri", "path", "paths", "file", "files", "dir", "length", "len", "limit", "limits", "size")]
	[InlineData("expiry", "expires", "expiration", "ttl", "field", "label", "prefix", "budget", "usage", "header", "policy", "provider", "endpoint", "mode", "format")]
	[InlineData("version", "enabled", "required", "stdin", "ref", "arn", "source", "store", "manager", "helper", "service", "handler", "class", "command", "cmd", "prompt")]
	[InlineData("regex", "pattern", "rule", "rules", "error", "errors", "message", "description", "hint", "placeholder", "schema", "validator", "input")]
	[InlineData("at", "request", "response", "dto", "form", "view", "page", "screen", "component", "route", "button", "dialog", "modal", "model", "state", "props")]
	[InlineData("cs", "ts", "tsx", "js", "jsx", "py", "go", "md", "json", "yaml", "yml", "rs", "java", "rb", "sh", "txt", "html", "css", "sql", "toml")]
	public void Descriptive_suffix_marks_the_name_as_descriptive(params string[] suffixes)
	{
		Assert.All(suffixes, suffix => Assert.Equal(KeyKind.Descriptive, SecretKeyNames.Classify("token_" + suffix)));
		Assert.All(suffixes, suffix => Assert.Equal(KeyKind.Descriptive, SecretKeyNames.Classify("TOKEN_" + suffix.ToUpperInvariant())));
	}

	[Theory]
	[InlineData("page", "next", "prev", "previous", "continuation", "cancellation", "sync", "max", "min", "input", "output", "total", "cache")]
	[InlineData("verify", "validate", "check", "is", "has", "get", "handle", "on", "use")]
	public void Bookkeeping_qualifier_means_a_token_is_no_credential(params string[] qualifiers)
	{
		Assert.All(qualifiers, qualifier => Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(qualifier + "_token")));
		Assert.All(qualifiers, qualifier => Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(qualifier.ToUpperInvariant() + "_TOKEN")));
	}

	[Theory]
	[InlineData("change", "reset", "forgot", "verify", "validate", "check", "is", "has", "must", "require", "requires", "needs", "get", "handle", "on", "use", "update")]
	public void Action_before_a_password_word_names_a_behavior(params string[] actions)
	{
		Assert.All(actions, action => Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(action + "_password")));
		Assert.All(actions, action => Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(action.ToUpperInvariant() + "_PASSWORD")));
	}

	[Theory]
	[InlineData("password")]
	[InlineData("passwd")]
	[InlineData("pwd")]
	[InlineData("passphrase")]
	public void Password_words_are_passwords(string word)
	{
		Assert.Equal(KeyKind.Password, SecretKeyNames.Classify(word));
		Assert.Equal(KeyKind.Password, SecretKeyNames.Classify("db_" + word.ToUpperInvariant()));
		Assert.Equal(KeyKind.Password, SecretKeyNames.Classify("my" + word));
	}

	[Theory]
	[InlineData("token")]
	[InlineData("secret")]
	public void Token_words_are_tokens(string word)
	{
		Assert.Equal(KeyKind.Token, SecretKeyNames.Classify(word));
		Assert.Equal(KeyKind.Token, SecretKeyNames.Classify("my" + word));
		Assert.Equal(KeyKind.Token, SecretKeyNames.Classify("DB_" + word.ToUpperInvariant()));
	}

	[Theory]
	[InlineData("api_key")]
	[InlineData("auth_key")]
	[InlineData("access_key")]
	[InlineData("private_key")]
	[InlineData("secret_key")]
	[InlineData("signing_key")]
	[InlineData("encryption_key")]
	[InlineData("master_key")]
	[InlineData("shared_key")]
	[InlineData("API_KEY")]
	[InlineData("signingKey")]
	public void Two_word_key_names_are_tokens(string key)
	{
		Assert.Equal(KeyKind.Token, SecretKeyNames.Classify(key));
	}

	[Theory]
	[InlineData("api_other")]
	[InlineData("other_api")]
	public void Half_a_two_word_key_name_is_nothing(string key)
	{
		Assert.Equal(KeyKind.NotSecret, SecretKeyNames.Classify(key));
	}

	[Theory]
	[InlineData("apikey")]
	[InlineData("secretkey")]
	[InlineData("accesskey")]
	[InlineData("authkey")]
	[InlineData("auth")]
	[InlineData("authorization")]
	[InlineData("credential")]
	[InlineData("credentials")]
	[InlineData("CREDENTIALS")]
	public void Exact_words_are_tokens(string word)
	{
		Assert.Equal(KeyKind.Token, SecretKeyNames.Classify(word));
	}

	[Theory]
	[InlineData("string", "str", "bytes", "number", "int", "integer", "bool", "boolean", "float", "double", "object", "any", "void", "char")]
	[InlineData("function", "func", "def", "async", "await", "lambda", "class", "type", "const", "var", "let", "new", "this", "self")]
	[InlineData("undefined", "true", "false", "yes", "no", "on", "off")]
	[InlineData("required", "optional", "empty", "unset", "unknown", "default", "invalid", "valid", "missing", "present", "absent")]
	[InlineData("value", "val", "input", "password", "passwd", "pwd", "pass", "passphrase", "secret", "secrets", "token", "tokens", "key", "keys", "apikey", "api_key", "api-key")]
	[InlineData("credential", "credentials", "auth", "inherit", "include", "omit", "bearer", "basic", "oauth", "jwt", "user", "username")]
	public void Keyword_is_never_a_value(params string[] keywords)
	{
		Assert.All(keywords, keyword => Assert.False(SecretHeuristics.IsUrlPassword(keyword), keyword));
		Assert.All(keywords, keyword => Assert.False(SecretHeuristics.IsUrlPassword(keyword.ToUpperInvariant()), keyword));
	}

	[Theory]
	[InlineData("your", "yours", "placeholder", "changeme", "example", "sample", "dummy", "fake", "redacted", "removed")]
	[InlineData("hidden", "masked", "todo", "tbd", "foo", "bar", "baz", "none", "null", "nil", "here", "insert", "replace")]
	public void Marker_word_is_a_placeholder(params string[] words)
	{
		Assert.All(words, word => Assert.True(ValueShapes.IsPlaceholder(word), word));
		Assert.All(words, word => Assert.True(ValueShapes.IsPlaceholder("some-" + word.ToUpperInvariant()), word));
		Assert.All(words, word => Assert.False(ValueShapes.IsPlaceholder("some-1-" + word), word));
	}

	[Theory]
	[InlineData("yourkey", "yourtoken", "yoursecret", "yourpassword", "yourapikey", "apikeyhere", "tokenhere", "keyhere", "passwordhere", "secrethere", "examplekey")]
	public void Squashed_marker_is_a_placeholder_inside_a_long_run(params string[] markers)
	{
		Assert.All(markers, marker => Assert.True(ValueShapes.IsPlaceholder("q1w2e3r4t5y6u7" + marker + "9"), marker));
		Assert.All(markers, marker => Assert.True(ValueShapes.IsPlaceholder("Q1W2E3R4T5Y6U7" + marker.ToUpperInvariant()), marker));
	}

	[Theory]
	[InlineData("process.env", "os.environ", "os.getenv", "env.", "secrets.", "vault:", "op://", "ssm:", "arn:aws:", "ref+", "secretref:", "$env:")]
	public void Reference_prefix_is_a_placeholder(params string[] prefixes)
	{
		Assert.All(prefixes, prefix => Assert.True(ValueShapes.IsPlaceholder(prefix + "some.name"), prefix));
		Assert.All(prefixes, prefix => Assert.False(ValueShapes.IsPlaceholder(prefix + "q1w2e3r4t5y6u7i8"), prefix));
	}

	[Theory]
	[InlineData("{{", "}}")]
	[InlineData("${{", "}}")]
	[InlineData("#{", "}")]
	[InlineData("${", "}")]
	[InlineData("$(", ")")]
	[InlineData("<", ">")]
	[InlineData("{", "}")]
	public void Template_needs_its_closer_and_a_name(string open, string close)
	{
		Assert.True(ValueShapes.IsPlaceholder(open + "some_name" + close));
		Assert.False(ValueShapes.IsPlaceholder(open + "some_name"));
		Assert.False(ValueShapes.IsPlaceholder(open + "q1w2+e3r4!t5y6" + close));
	}

	[Theory]
	[InlineData("cs", "ts", "tsx", "js", "jsx", "py", "go", "md", "json", "yaml", "yml", "rs", "java", "rb", "sh", "txt", "html", "css", "sql", "toml")]
	public void Source_extension_marks_a_file_name_rather_than_a_token(params string[] extensions)
	{
		Redactor redactor = new();
		Assert.All(extensions, extension =>
		{
			string input = "token: a1b2c3d4." + extension;
			Assert.Equal(input, redactor.Redact(input).Text);
			string upper = "token: a1b2c3d4." + extension.ToUpperInvariant();
			Assert.Equal(upper, redactor.Redact(upper).Text);
			string withLine = "token: a1b2c3d4." + extension + ":12";
			Assert.Equal(withLine, redactor.Redact(withLine).Text);
		});
	}

	[Theory]
	[InlineData("MEMORIA_QMD_SECRET")]
	[InlineData("MEMORIA_QMD_WORKER_TOKEN")]
	[InlineData("MEMORIA_WORKER_TOKEN")]
	[InlineData("MEMORIA_SIDECAR_SECRET")]
	[InlineData("Memoria__Sidecar__Secret")]
	public void Every_shared_secret_name_is_decisive_on_a_low_entropy_value(string name)
	{
		RedactionResult result = new Redactor().Redact(name + "=aaaa");

		Assert.Equal(name + "=[REDACTED:memoria-shared-secret]", result.Text);
		Assert.Equal(new Dictionary<string, int> { ["memoria-shared-secret"] = 1 }, result.CountsByRule);
	}
}
