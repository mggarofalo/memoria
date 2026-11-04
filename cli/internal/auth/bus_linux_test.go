//go:build linux

package auth

import (
	"context"
	"errors"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	dbus "github.com/godbus/dbus/v5"
)

// privateRuntimeDir is a stand-in for /run/user/<uid>. The path stays short, as
// socket paths are limited to about 100 bytes.
func privateRuntimeDir(t *testing.T) string {
	t.Helper()
	dir, err := os.MkdirTemp("", "mrt")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(dir) })
	if err := os.Chmod(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	return dir
}

func listenUnix(t *testing.T, path string) *atomic.Int32 {
	t.Helper()
	listener, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = listener.Close() })
	var accepted atomic.Int32
	go func() {
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			accepted.Add(1)
			_ = conn.Close()
		}
	}()
	return &accepted
}

func TestVerifyBusSocket(t *testing.T) {
	uid := os.Getuid()
	runtime := privateRuntimeDir(t)
	good := filepath.Join(runtime, "bus")
	listenUnix(t, good)
	if err := verifyBusSocket(good, []string{runtime}, uid); err != nil {
		t.Fatal(err)
	}
	if err := verifyBusSocket(good, []string{runtime}, uid+1); err == nil {
		t.Error("another user's socket should be refused")
	}
	if err := verifyBusSocket(good, []string{"/run/user/99999"}, uid); err == nil {
		t.Error("a socket outside the runtime directory should be refused")
	}
	dotted := runtime + "/x/../bus"
	if err := verifyBusSocket(dotted, []string{runtime}, uid); err == nil {
		t.Error("an unclean path should be refused")
	}
	if err := verifyBusSocket(filepath.Join(runtime, "missing"), []string{runtime}, uid); err == nil {
		t.Error("a missing socket should be refused")
	}
}

func TestSocketOfAnotherUserIsRefused(t *testing.T) {
	runtime := privateRuntimeDir(t)
	path := filepath.Join(runtime, "bus")
	listenUnix(t, path)
	if err := verifyBusSocketFile(path, os.Getuid()); err != nil {
		t.Fatal(err)
	}
	if err := verifyBusSocketFile(path, os.Getuid()+1); err == nil {
		t.Fatal("expected a refusal")
	}
}

func TestVerifyBusSocketRefusesAFile(t *testing.T) {
	runtime := privateRuntimeDir(t)
	path := filepath.Join(runtime, "bus")
	if err := os.WriteFile(path, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := verifyBusSocket(path, []string{runtime}, os.Getuid()); err == nil {
		t.Fatal("expected a refusal")
	}
}

func TestVerifyBusSocketRefusesALooseRuntimeDirectory(t *testing.T) {
	runtime := privateRuntimeDir(t)
	path := filepath.Join(runtime, "bus")
	listenUnix(t, path)
	if err := os.Chmod(runtime, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := verifyBusSocket(path, []string{runtime}, os.Getuid()); err == nil {
		t.Fatal("expected a refusal")
	}
}

func TestVerifyBusSocketRefusesASymlinkedDirectory(t *testing.T) {
	runtime := privateRuntimeDir(t)
	elsewhere := privateRuntimeDir(t)
	listenUnix(t, filepath.Join(elsewhere, "bus"))
	link := filepath.Join(runtime, "sub")
	if err := os.Symlink(elsewhere, link); err != nil {
		t.Fatal(err)
	}
	if err := verifyBusSocket(filepath.Join(link, "bus"), []string{runtime}, os.Getuid()); err == nil {
		t.Fatal("expected a refusal")
	}
}

func TestRuntimeDirectoriesIgnoreARelativeValue(t *testing.T) {
	t.Setenv("XDG_RUNTIME_DIR", "relative/dir")
	dirs := runtimeDirectories(1000)
	if len(dirs) != 1 || dirs[0] != "/run/user/1000" {
		t.Fatalf("dirs = %v", dirs)
	}
	t.Setenv("XDG_RUNTIME_DIR", "/custom/run")
	if dirs := runtimeDirectories(1000); len(dirs) != 2 || dirs[0] != "/custom/run" {
		t.Fatalf("dirs = %v", dirs)
	}
}

// The review's attack: the environment names a bus outside the runtime directory,
// and whatever listens there would be handed the pasted key.
func TestPoisonedBusIsRefusedBeforeAnyConnection(t *testing.T) {
	sessionBusCheck = checkSessionBus
	runtime := privateRuntimeDir(t)
	t.Setenv("XDG_RUNTIME_DIR", runtime)

	attacker := privateRuntimeDir(t)
	poisoned := filepath.Join(attacker, "bus")
	accepted := listenUnix(t, poisoned)
	tcp, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = tcp.Close() }()
	_, port, _ := net.SplitHostPort(tcp.Addr().String())

	for name, address := range map[string]string{
		"socket elsewhere": "unix:path=" + poisoned,
		"tcp":              "tcp:host=127.0.0.1,port=" + port,
		"abstract":         "unix:abstract=/tmp/evil",
	} {
		t.Run(name, func(t *testing.T) {
			t.Setenv("DBUS_SESSION_BUS_ADDRESS", address)
			store := &libraryStore{service: "memoria-cli-test"}
			if err := store.Set("a", fileKey); err == nil {
				t.Error("Set should refuse")
			}
			if _, err := store.Get("a"); err == nil {
				t.Error("Get should refuse")
			}
			if err := store.Delete("a"); err == nil {
				t.Error("Delete should refuse")
			}
			if _, err := openPlatformKeyring("memoria-cli-test"); !errors.Is(err, ErrKeyringUnavailable) {
				t.Errorf("open = %v", err)
			}
		})
	}
	if accepted.Load() != 0 {
		t.Fatalf("the poisoned bus received %d connections", accepted.Load())
	}
}

// startBus runs a real bus daemon, for the checks only a real bus can answer.
func startBus(t *testing.T) (address string) {
	t.Helper()
	daemon, err := exec.LookPath("dbus-daemon")
	if err != nil {
		t.Skip("dbus-daemon is not available")
	}
	runtime := privateRuntimeDir(t)
	address = "unix:path=" + filepath.Join(runtime, "bus")
	cmd := exec.Command(daemon, "--session", "--nofork", "--address="+address)
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
	})
	for range 100 {
		if _, err := os.Lstat(filepath.Join(runtime, "bus")); err == nil {
			return address
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatal("the bus did not start")
	return ""
}

func connectBus(t *testing.T, address string) *dbus.Conn {
	t.Helper()
	conn, err := dbus.Connect(address)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	return conn
}

func TestSecretsOwnerOnARealBus(t *testing.T) {
	address := startBus(t)
	service := connectBus(t, address)
	client := connectBus(t, address)

	err := verifySecretsOwner(client, uint32(os.Getuid()))
	if err == nil || !strings.Contains(err.Error(), "no Secret Service") {
		t.Fatalf("with nothing on the name: %v", err)
	}

	reply, err := service.RequestName(secretServiceName, dbus.NameFlagDoNotQueue)
	if err != nil || reply != dbus.RequestNameReplyPrimaryOwner {
		t.Fatalf("reply = %v, err = %v", reply, err)
	}
	if err := verifySecretsOwner(client, uint32(os.Getuid())); err != nil {
		t.Fatal(err)
	}
	// The bus reports the real owner, so anyone else fails this.
	if err := verifySecretsOwner(client, uint32(os.Getuid())+1); err == nil || !strings.Contains(err.Error(), "belongs to uid") {
		t.Fatalf("err = %v", err)
	}
}

func TestLookupOwnerHonorsItsDeadline(t *testing.T) {
	client := connectBus(t, startBus(t))
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := lookupOwner(ctx, client); err == nil {
		t.Fatal("expected a cancelled call to fail")
	}
}

func TestCheckSessionBusAcceptsThisUsersBusAndPinsIt(t *testing.T) {
	address := startBus(t)
	service := connectBus(t, address)
	if _, err := service.RequestName(secretServiceName, dbus.NameFlagDoNotQueue); err != nil {
		t.Fatal(err)
	}
	path, _ := busSocketPath(address)
	t.Setenv("XDG_RUNTIME_DIR", filepath.Dir(path))
	t.Setenv("DBUS_SESSION_BUS_ADDRESS", address+",guid=abc")
	if err := checkSessionBus(); err != nil {
		t.Fatal(err)
	}
	if got := os.Getenv("DBUS_SESSION_BUS_ADDRESS"); got != address {
		t.Fatalf("address = %q", got)
	}
	if conn, err := dbus.SessionBus(); err == nil {
		_ = conn.Close()
	}
}

func TestUnsetBusAddressFallsBackToTheRuntimeDirectory(t *testing.T) {
	runtime := privateRuntimeDir(t)
	t.Setenv("XDG_RUNTIME_DIR", runtime)
	t.Setenv("DBUS_SESSION_BUS_ADDRESS", "")
	got, err := busSocketFromEnvironment()
	if err != nil || got != filepath.Join(runtime, "bus") {
		t.Fatalf("got %q, err = %v", got, err)
	}
	// Nothing is listening there, and nothing may be launched instead.
	if err := checkSessionBus(); err == nil {
		t.Fatal("expected no bus")
	}
}
