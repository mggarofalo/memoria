//go:build windows

package auth

import (
	"fmt"
	"os"
	"slices"
	"unsafe"

	"golang.org/x/sys/windows"
)

// On Windows, Go's 0600 and 0700 mean nothing: the only effect of a mode is the
// read-only bit, and access comes from the ACL inherited from the parent. A
// default user profile happens to keep other users out, but a config directory
// elsewhere, or a profile with relaxed ACLs, would not. So these functions set
// an explicit protected DACL granting only the current user.
//
// What this does not protect against: processes running as the same user,
// SYSTEM, and administrators, who can take ownership of any file. The file is
// also not encrypted at rest.

func currentUserSID() (*windows.SID, error) {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return nil, fmt.Errorf("cannot determine the current user: %w", err)
	}
	return user.User.Sid, nil
}

func setOwnerOnlyDACL(path string, inheritance uint32) error {
	sid, err := currentUserSID()
	if err != nil {
		return err
	}
	acl, err := windows.ACLFromEntries([]windows.EXPLICIT_ACCESS{{
		AccessPermissions: windows.GENERIC_ALL,
		AccessMode:        windows.SET_ACCESS,
		Inheritance:       inheritance,
		Trustee: windows.TRUSTEE{
			TrusteeForm:  windows.TRUSTEE_IS_SID,
			TrusteeType:  windows.TRUSTEE_IS_USER,
			TrusteeValue: windows.TrusteeValueFromSID(sid),
		},
	}}, nil)
	if err != nil {
		return fmt.Errorf("cannot build an access list: %w", err)
	}
	// PROTECTED stops the parent directory's permissions from being inherited.
	err = windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		nil, nil, acl, nil)
	if err != nil {
		return fmt.Errorf("cannot set permissions on %s: %w", path, err)
	}
	return nil
}

// ensurePrivateDir creates dir and restricts it to the current user. New files
// in it inherit that restriction.
func ensurePrivateDir(dir string) error {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	return setOwnerOnlyDACL(dir, windows.SUB_CONTAINERS_AND_OBJECTS_INHERIT)
}

// protectFile restricts a file to the current user.
func protectFile(path string) error {
	return setOwnerOnlyDACL(path, windows.NO_INHERITANCE)
}

// verifyPrivate fails with ErrInsecurePermissions if the DACL grants access to
// anyone beyond the current user, SYSTEM and Administrators. Those last two can
// reach any file regardless, so listing them changes nothing.
func verifyPrivate(path string) error {
	info, err := os.Lstat(path)
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("%w: not a regular file", ErrInsecurePermissions)
	}
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		return fmt.Errorf("cannot read permissions: %w", err)
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return fmt.Errorf("%w: no access list", ErrInsecurePermissions)
	}
	if dacl == nil {
		return fmt.Errorf("%w: a null access list grants everyone access", ErrInsecurePermissions)
	}
	return verifyEntries(dacl)
}

func verifyEntries(dacl *windows.ACL) error {
	trusted, err := trustedSIDs()
	if err != nil {
		return err
	}
	for i := uint32(0); i < uint32(dacl.AceCount); i++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(dacl, i, &ace); err != nil {
			return fmt.Errorf("cannot read permissions: %w", err)
		}
		if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE {
			continue
		}
		sid := (*windows.SID)(unsafe.Pointer(&ace.SidStart))
		if !slices.ContainsFunc(trusted, sid.Equals) {
			return fmt.Errorf("%w: another account has access", ErrInsecurePermissions)
		}
	}
	return nil
}

func trustedSIDs() ([]*windows.SID, error) {
	current, err := currentUserSID()
	if err != nil {
		return nil, err
	}
	trusted := []*windows.SID{current}
	for _, kind := range []windows.WELL_KNOWN_SID_TYPE{windows.WinLocalSystemSid, windows.WinBuiltinAdministratorsSid} {
		sid, err := windows.CreateWellKnownSid(kind)
		if err != nil {
			return nil, err
		}
		trusted = append(trusted, sid)
	}
	return trusted, nil
}

func permissionFixHint(path string) string {
	return "remove other accounts from its security settings (icacls \"" + path + "\"), or delete it and run 'memoria auth login --backend file' again"
}
