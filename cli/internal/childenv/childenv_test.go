package childenv

import (
	"slices"
	"testing"
)

func TestScrubLeavesOutTheKeyAndCredentials(t *testing.T) {
	environ := []string{
		"MEMORIA_API_KEY=mem_0123456789abcdef_secret",
		"memoria_api_key=lower-case-on-windows",
		"Memoria_Api_Key=mixed",
		"GITHUB_TOKEN=ghp_x",
		"GH_TOKEN=x",
		"AWS_SECRET_ACCESS_KEY=x",
		"AWS_SESSION_TOKEN=x",
		"OPENAI_API_KEY=x",
		"ANTHROPIC_APIKEY=x",
		"PGPASSWORD=x",
		"NPM_CONFIG__AUTH=x",
		"MEMORIA_QMD_SECRET=x",
		"DOCKER_AUTH_CONFIG=x",
		"GIT_ASKPASS_PASSPHRASE=x",
		"AZURE_CLIENT_CERTIFICATE_PASSWORD=x",
		"GOOGLE_APPLICATION_CREDENTIALS=/k.json",
		"SSH_PRIVATE_KEY=x",
		"MY_ACCESS_KEY_ID=x",
	}
	kept := []string{
		"PATH=/usr/bin",
		"Path=C:\\Windows",
		"HOME=/home/me",
		"USERPROFILE=C:\\Users\\me",
		"TEMP=C:\\Temp",
		"ComSpec=C:\\Windows\\system32\\cmd.exe",
		"DOTNET_ROOT=/usr/share/dotnet",
		"MEMORIA_URL=https://memoria.example",
		"=C:=C:\\work",
		"NO_VALUE",
		"EMPTY=",
	}
	got := Scrub(append(slices.Clone(environ), kept...))
	if !slices.Equal(got, kept) {
		t.Fatalf("Scrub kept %q, want %q", got, kept)
	}
}

func TestScrubDoesNotChangeItsInput(t *testing.T) {
	environ := []string{"MEMORIA_API_KEY=x", "PATH=/bin"}
	_ = Scrub(environ)
	if environ[0] != "MEMORIA_API_KEY=x" || environ[1] != "PATH=/bin" {
		t.Fatalf("input changed: %q", environ)
	}
}

func TestEnvironReadsTheProcessEnvironment(t *testing.T) {
	t.Setenv("MEMORIA_API_KEY", "must-not-reach-a-child")
	t.Setenv("CHILDENV_TEST_VISIBLE", "yes")
	got := Environ()
	if slices.Contains(got, "MEMORIA_API_KEY=must-not-reach-a-child") {
		t.Fatal("the key reached the child environment")
	}
	if !slices.Contains(got, "CHILDENV_TEST_VISIBLE=yes") {
		t.Fatal("an ordinary variable was dropped")
	}
}
