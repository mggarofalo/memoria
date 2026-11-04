//go:build windows

package selfupdate

import "golang.org/x/sys/windows"

// hideFile marks a backup hidden until the next update can delete it.
func hideFile(path string) {
	name, err := windows.UTF16PtrFromString(path)
	if err == nil {
		_ = windows.SetFileAttributes(name, windows.FILE_ATTRIBUTE_HIDDEN)
	}
}
