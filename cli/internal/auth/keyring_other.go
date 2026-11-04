//go:build !darwin

package auth

func openPlatformKeyring(service string) (SecretStore, error) {
	return newKeyringStoreNamed(service)
}
