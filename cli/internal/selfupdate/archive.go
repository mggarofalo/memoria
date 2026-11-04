package selfupdate

import (
	"archive/tar"
	"archive/zip"
	"bytes"
	"compress/gzip"
	"errors"
	"fmt"
	"io"
)

const (
	// maxArchiveBytes bounds a download, and maxUnpackedBytes what it may expand
	// to, so a small archive cannot fill a disk. The real binary is near 30 MiB.
	maxArchiveBytes  = 64 << 20
	maxUnpackedBytes = 128 << 20
)

var (
	errNoBinary        = errors.New("the archive has no binary")
	errDuplicateBinary = errors.New("the archive names its binary more than once")
	errBinaryNotFile   = errors.New("the archive's binary is not a regular file")
	errTooLarge        = errors.New("the archive unpacks to more than the size limit")
)

// binaryName is the file a release archive holds for this OS.
func binaryName(goos string) string {
	if goos == "windows" {
		return "memoria.exe"
	}
	return "memoria"
}

// extractBinary writes the archive's one binary to w. It reads every entry, so
// that two entries with the same name cannot show different scanners different
// programs, and it refuses anything but one regular file under that name.
func extractBinary(archive []byte, goos string, w io.Writer) error {
	name := binaryName(goos)
	if goos == "windows" {
		return extractZip(archive, name, w)
	}
	return extractTarGz(archive, name, w)
}

func extractTarGz(archive []byte, name string, w io.Writer) error {
	gz, err := gzip.NewReader(bytes.NewReader(archive))
	if err != nil {
		return fmt.Errorf("cannot unpack the archive: %w", err)
	}
	limited := &limitedReader{r: gz, remaining: maxUnpackedBytes}
	reader := tar.NewReader(limited)
	found := false
	for {
		header, err := reader.Next()
		if errors.Is(err, io.EOF) {
			return foundOrError(found)
		}
		if err != nil {
			return unpackError(err)
		}
		if header.Name != name {
			continue
		}
		if found {
			return errDuplicateBinary
		}
		if header.Typeflag != tar.TypeReg {
			return errBinaryNotFile
		}
		if _, err := io.Copy(w, reader); err != nil {
			return unpackError(err)
		}
		found = true
	}
}

func extractZip(archive []byte, name string, w io.Writer) error {
	reader, err := zip.NewReader(bytes.NewReader(archive), int64(len(archive)))
	if err != nil {
		return fmt.Errorf("cannot unpack the archive: %w", err)
	}
	var match *zip.File
	for _, file := range reader.File {
		if file.Name != name {
			continue
		}
		if match != nil {
			return errDuplicateBinary
		}
		match = file
	}
	if match == nil {
		return errNoBinary
	}
	return copyZipEntry(match, w)
}

func copyZipEntry(file *zip.File, w io.Writer) error {
	if !file.Mode().IsRegular() {
		return errBinaryNotFile
	}
	body, err := file.Open()
	if err != nil {
		return fmt.Errorf("cannot unpack the archive: %w", err)
	}
	defer func() { _ = body.Close() }()
	// The declared size is the archive's claim; the limit is on what actually
	// comes out.
	if _, err := io.Copy(w, &limitedReader{r: body, remaining: maxUnpackedBytes}); err != nil {
		return unpackError(err)
	}
	return nil
}

func foundOrError(found bool) error {
	if !found {
		return errNoBinary
	}
	return nil
}

func unpackError(err error) error {
	if errors.Is(err, errTooLarge) {
		return err
	}
	return fmt.Errorf("cannot unpack the archive: %w", err)
}

// limitedReader fails rather than truncating, so a cut-off binary can never be
// mistaken for a whole one.
type limitedReader struct {
	r         io.Reader
	remaining int64
}

func (l *limitedReader) Read(p []byte) (int, error) {
	n, err := l.r.Read(p)
	l.remaining -= int64(n)
	if l.remaining < 0 {
		return 0, errTooLarge
	}
	return n, err
}
