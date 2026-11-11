// Package childenv builds the environment for the processes the CLI starts: the
// redaction tool and the scanners it runs, a local qmd, and cmd.exe on Windows.
// None of them needs a credential, and each could log, crash-dump or pass on what
// it inherits. So they get an allowlist of what a program needs to find itself, its
// home, its temporary directory and its locale, and even from that, anything named
// like a credential is left out.
package childenv

import (
	"os"
	"slices"
	"strings"
)

// apiKeyVariable is auth.EnvAPIKey, named here so this package depends on nothing.
const apiKeyVariable = "MEMORIA_API_KEY" //nolint:gosec // G101: the name of an environment variable, not a credential

// allowed are the variables passed on, by upper-case name. They cover finding
// programs (PATH), the user's directories, temporary files, the locale and
// terminal, Windows' system folders, and TLS trust settings.
var allowed = []string{
	"PATH", "PATHEXT",
	"HOME", "USER", "LOGNAME", "SHELL", "TERM", "TZ", "LANG", "LANGUAGE", "NO_COLOR",
	"TEMP", "TMP", "TMPDIR",
	"USERPROFILE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH",
	"APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "ALLUSERSPROFILE", "PUBLIC",
	"PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
	"COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)", "COMMONPROGRAMW6432",
	"SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC", "OS",
	"NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
	"SSL_CERT_FILE", "SSL_CERT_DIR", "NUGET_PACKAGES",
}

// allowedPrefixes cover families of settings: the locale, XDG directories, and the
// runtimes these children are built on, dotnet and node, and qmd's own.
var allowedPrefixes = []string{"LC_", "XDG_", "DOTNET_", "NODE_", "QMD_"}

// credentialWords mark a variable that holds a credential, wherever they appear in
// its name. They apply even to allowed names, such as NODE_AUTH_TOKEN.
var credentialWords = []string{
	"TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "PASS", "PWD", "CREDENTIAL",
	"KEY", "AUTH", "COOKIE", "SESSION", "DSN", "CONNECTION_STRING",
}

// Environ is the environment for a child: the allowlist from the current one,
// without credentials.
func Environ() []string {
	return Allowlisted(os.Environ())
}

// Allowlisted keeps only allowed variables, then scrubs them. Windows' per-drive
// entries, such as "=C:=C:\x", have no name before their first "=" and are kept,
// as cmd.exe uses them for the current directory.
func Allowlisted(environ []string) []string {
	kept := slices.DeleteFunc(slices.Clone(environ), func(entry string) bool {
		name, _, _ := strings.Cut(entry, "=")
		return name != "" && !isAllowed(strings.ToUpper(name))
	})
	return Scrub(kept)
}

func isAllowed(upper string) bool {
	return slices.Contains(allowed, upper) || slices.ContainsFunc(allowedPrefixes, func(prefix string) bool {
		return strings.HasPrefix(upper, prefix)
	})
}

// Scrub returns environ without the API key, without any variable whose name looks
// like a credential, and without any whose value holds a URL with a password in it.
// Names are compared in upper case, since Windows treats them case-insensitively.
func Scrub(environ []string) []string {
	return slices.DeleteFunc(slices.Clone(environ), func(entry string) bool {
		name, value, _ := strings.Cut(entry, "=")
		return name != "" && (IsCredential(name) || hasURLPassword(value))
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

// hasURLPassword finds scheme://user:password@host anywhere in a value.
func hasURLPassword(value string) bool {
	for rest := value; ; {
		_, after, found := strings.Cut(rest, "://")
		if !found {
			return false
		}
		authority, _, _ := strings.Cut(after, "/")
		if userinfo, _, hasAt := strings.Cut(authority, "@"); hasAt && strings.Contains(userinfo, ":") {
			return true
		}
		rest = after
	}
}
