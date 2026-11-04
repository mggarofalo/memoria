//go:build darwin

package auth

import "os"

// securityTool is an absolute path so that PATH cannot substitute another
// program, which would then receive the key on stdin.
const securityTool = "/usr/bin/security"

func openPlatformKeyring(service string) (SecretStore, error) {
	if _, err := os.Stat(securityTool); err != nil {
		return nil, ErrKeyringUnavailable
	}
	return &SecurityStore{path: securityTool, service: service}, nil
}
