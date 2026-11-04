//go:build windows

package auth

import (
	"errors"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"
)

// aceSIDs lists the accounts a path's DACL allows, and whether it is protected
// from inheriting the parent's entries.
func aceSIDs(t *testing.T, path string) (sids []*windows.SID, protected bool) {
	t.Helper()
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	control, _, err := sd.Control()
	if err != nil {
		t.Fatal(err)
	}
	dacl, _, err := sd.DACL()
	if err != nil || dacl == nil {
		t.Fatalf("no DACL: %v", err)
	}
	for i := uint32(0); i < uint32(dacl.AceCount); i++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(dacl, i, &ace); err != nil {
			t.Fatal(err)
		}
		sids = append(sids, (*windows.SID)(unsafe.Pointer(&ace.SidStart)))
	}
	return sids, control&windows.SE_DACL_PROTECTED != 0
}

func assertOwnerOnly(t *testing.T, path string) {
	t.Helper()
	sids, protected := aceSIDs(t, path)
	if !protected {
		t.Errorf("%s: DACL should not inherit from its parent", path)
	}
	me, err := currentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	// A directory can hold two entries for one account: one for itself, and an
	// inherit-only one for its children.
	if len(sids) == 0 || !slices.ContainsFunc(sids, me.Equals) {
		t.Errorf("%s: DACL grants %v, want %v", path, sids, me)
	}
	for _, sid := range sids {
		if !sid.Equals(me) {
			t.Errorf("%s: DACL also grants %v", path, sid)
		}
	}
}

func TestFileAndDirectoryHaveOwnerOnlyDACLs(t *testing.T) {
	dir := useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	assertOwnerOnly(t, dir)
	assertOwnerOnly(t, store.Path())
}

func TestFileWithAnotherAccountIsRefusedButCanBeDeleted(t *testing.T) {
	useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}

	everyone, err := windows.CreateWellKnownSid(windows.WinWorldSid)
	if err != nil {
		t.Fatal(err)
	}
	me, err := currentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	acl, err := windows.ACLFromEntries([]windows.EXPLICIT_ACCESS{
		{
			AccessPermissions: windows.GENERIC_READ,
			AccessMode:        windows.SET_ACCESS,
			Trustee: windows.TRUSTEE{
				TrusteeForm:  windows.TRUSTEE_IS_SID,
				TrusteeType:  windows.TRUSTEE_IS_WELL_KNOWN_GROUP,
				TrusteeValue: windows.TrusteeValueFromSID(everyone),
			},
		},
		{
			AccessPermissions: windows.GENERIC_ALL,
			AccessMode:        windows.SET_ACCESS,
			Trustee: windows.TRUSTEE{
				TrusteeForm:  windows.TRUSTEE_IS_SID,
				TrusteeType:  windows.TRUSTEE_IS_USER,
				TrusteeValue: windows.TrusteeValueFromSID(me),
			},
		},
	}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := windows.SetNamedSecurityInfo(store.Path(), windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION, nil, nil, acl, nil); err != nil {
		t.Fatal(err)
	}

	_, err = store.Get("a")
	if !errors.Is(err, ErrInsecurePermissions) {
		t.Fatalf("err = %v", err)
	}
	if strings.Contains(err.Error(), fileKey) {
		t.Fatalf("err = %v", err)
	}
	if err := store.Delete("a"); err != nil {
		t.Fatalf("logout must still work: %v", err)
	}
}

func TestPreexistingLooseDirectoryIsTightened(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(dir, 0o777); err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(filepath.Join(dir, "x.json"), []byte("{}")); err != nil {
		t.Fatal(err)
	}
	assertOwnerOnly(t, dir)
}
