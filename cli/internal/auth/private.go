package auth

import (
	"errors"
	"fmt"
	"os"
)

// ReadPrivateFile reads a file this CLI keeps in its private directory, refusing one
// that others could have written. A file that does not exist returns os.ErrNotExist.
func ReadPrivateFile(path string) ([]byte, error) {
	switch err := verifyPrivate(path); {
	case errors.Is(err, os.ErrNotExist):
		return nil, os.ErrNotExist
	case errors.Is(err, ErrInsecurePermissions):
		return nil, fmt.Errorf("%w: %s; %s", err, path, permissionFixHint(path))
	case err != nil:
		return nil, err
	}
	return os.ReadFile(path)
}

// WritePrivateFile atomically replaces a file in this CLI's private directory.
func WritePrivateFile(path string, data []byte) error {
	return writePrivateFile(path, data)
}
