package main

import (
	"os"
	"path/filepath"
)

// repoPath is a file of the repository around this module, given as a slash-separated path from the repository's
// root. MEMORIA_REPO_ROOT names that root for a tool that runs the tests on a copy of the module alone, as
// scripts/coverage-go-branch.sh does for gobco; otherwise it is the module's parent.
func repoPath(rel string) string {
	root := os.Getenv("MEMORIA_REPO_ROOT")
	if root == "" {
		root = ".."
	}
	path, err := filepath.Abs(filepath.Join(root, filepath.FromSlash(rel)))
	if err != nil {
		panic(err)
	}
	return path
}
