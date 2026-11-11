package childenv

import (
	"slices"
	"testing"
)

var credentials = []string{
	"MEMORIA_API_KEY=mem_0123456789abcdef_secret",
	"memoria_api_key=lower-case-on-windows",
	"Memoria_Api_Key=mixed",
	"GITHUB_TOKEN=ghp_x",
	"AWS_SECRET_ACCESS_KEY=x",
	"AWS_SESSION_TOKEN=x",
	"OPENAI_API_KEY=x",
	"AZURE_STORAGE_KEY=x",
	"STRIPE_KEY=x",
	"PGPASSWORD=x",
	"MYSQL_PWD=x",
	"DB_PASS=x",
	"NPM_CONFIG__AUTH=x",
	"MEMORIA_QMD_SECRET=x",
	"GOOGLE_APPLICATION_CREDENTIALS=/k.json",
	"SENTRY_DSN=https://k@o.ingest.sentry.io/1",
	"SQL_CONNECTION_STRING=Server=x;Password=y",
	"DATABASE_URL=postgres://user:hunter2@db:5432/x",
}

// Allowed by name, but credentials all the same.
var allowedButSecret = []string{
	"NODE_AUTH_TOKEN=x",
	"DOTNET_NUGET_SIGNATURE_VERIFICATION_PASSWORD=x",
	"QMD_API_KEY=x",
	"XDG_SESSION_COOKIE=x",
	"QMD_ENDPOINT=https://me:hunter2@llm.example/v1",
}

var ordinary = []string{
	"PATH=/usr/bin",
	"Path=C:\\Windows",
	"HOME=/home/me",
	"USERPROFILE=C:\\Users\\me",
	"TEMP=C:\\Temp",
	"ComSpec=C:\\Windows\\system32\\cmd.exe",
	"SystemRoot=C:\\Windows",
	"LC_ALL=C.UTF-8",
	"XDG_CACHE_HOME=/home/me/.cache",
	"DOTNET_ROOT=/usr/share/dotnet",
	"NODE_OPTIONS=--max-old-space-size=4096",
	"QMD_ENDPOINT=https://llm.example/v1",
	"=C:=C:\\work",
}

// Neither credentials nor needed: the allowlist leaves them out, and the deny-list alone keeps them.
var unlisted = []string{
	"MEMORIA_URL=https://memoria.example",
	"GIT_AUTHOR_NAME=me",
	"EDITOR=vim",
}

func TestAllowlistedKeepsWhatAProgramNeedsAndNothingElse(t *testing.T) {
	environ := slices.Concat(credentials, allowedButSecret, unlisted, ordinary)
	if got := Allowlisted(environ); !slices.Equal(got, ordinary) {
		t.Fatalf("Allowlisted kept %q, want %q", got, ordinary)
	}
}

func TestScrubLeavesOutCredentialsOnly(t *testing.T) {
	keep := slices.Concat(ordinary, []string{"MEMORIA_URL=https://memoria.example", "EDITOR=vim", "NO_VALUE", "EMPTY="})
	if got := Scrub(slices.Concat(credentials, allowedButSecret, keep)); !slices.Equal(got, keep) {
		t.Fatalf("Scrub kept %q, want %q", got, keep)
	}
}

func TestScrubDoesNotChangeItsInput(t *testing.T) {
	environ := []string{"MEMORIA_API_KEY=x", "PATH=/bin"}
	_ = Scrub(environ)
	_ = Allowlisted(environ)
	if environ[0] != "MEMORIA_API_KEY=x" || environ[1] != "PATH=/bin" {
		t.Fatalf("input changed: %q", environ)
	}
}

func TestAURLWithAPasswordIsFoundAnywhereInAValue(t *testing.T) {
	for value, want := range map[string]bool{
		"postgres://u:p@h/db":                true,
		"a;b=https://u:p@h;c":                true,
		"https://h/x://u:p@g":                true,
		"https://user@h/path":                false,
		"https://h/a:b@c":                    false,
		"https://h:8080/path":                false,
		"C:\\Program Files\\x":               false,
		"plain text with : and @ and no url": false,
	} {
		if got := hasURLPassword(value); got != want {
			t.Errorf("hasURLPassword(%q) = %v, want %v", value, got, want)
		}
	}
}

func TestEnvironReadsTheProcessEnvironment(t *testing.T) {
	t.Setenv("MEMORIA_API_KEY", "must-not-reach-a-child")
	t.Setenv("QMD_CHILDENV_TEST", "yes")
	got := Environ()
	if slices.Contains(got, "MEMORIA_API_KEY=must-not-reach-a-child") {
		t.Fatal("the key reached the child environment")
	}
	if !slices.Contains(got, "QMD_CHILDENV_TEST=yes") {
		t.Fatal("an allowed variable was dropped")
	}
}
