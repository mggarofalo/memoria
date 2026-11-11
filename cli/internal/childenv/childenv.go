// Package childenv builds the environment for the processes the CLI starts: the
// redaction tool and the scanners it runs, a local qmd, and cmd.exe on Windows.
// None of them needs a credential, and each could log, crash-dump or pass on what
// it inherits, so the API key and anything named like a credential are left out.
package childenv

import (
	"os"
	"slices"
	"strings"
)

// apiKeyVariable is auth.EnvAPIKey, named here so this package depends on nothing.
const apiKeyVariable = "MEMORIA_API_KEY" //nolint:gosec // G101: the name of an environment variable, not a credential

// credentialWords mark a variable that holds a credential, wherever they appear in
// its name: GITHUB_TOKEN, AWS_SECRET_ACCESS_KEY, OPENAI_API_KEY, PGPASSWORD,
// NPM_CONFIG__AUTH, AZURE_CLIENT_CERTIFICATE_PASSWORD and the like.
var credentialWords = []string{
	"TOKEN",
	"SECRET",
	"PASSWORD",
	"PASSWD",
	"PASSPHRASE",
	"CREDENTIAL",
	"API_KEY",
	"APIKEY",
	"ACCESS_KEY",
	"PRIVATE_KEY",
	"AUTH",
	"COOKIE",
	"SESSION_KEY",
}

// Environ is the current environment without credentials.
func Environ() []string {
	return Scrub(os.Environ())
}

// Scrub returns environ without the API key and without any variable whose name
// looks like a credential. Names are compared in upper case, since Windows treats
// them case-insensitively. Windows' per-drive entries, such as "=C:=C:\x", have no
// name before their first "=" and are kept.
func Scrub(environ []string) []string {
	return slices.DeleteFunc(slices.Clone(environ), func(entry string) bool {
		name, _, _ := strings.Cut(entry, "=")
		return IsCredential(name)
	})
}

// IsCredential reports whether a variable of this name is left out.
func IsCredential(name string) bool {
	upper := strings.ToUpper(name)
	if upper == apiKeyVariable {
		return true
	}
	return slices.ContainsFunc(credentialWords, func(word string) bool {
		return strings.Contains(upper, word)
	})
}
