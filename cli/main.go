// Command memoria is the CLI for a Memoria server.
package main

import (
	"fmt"
	"os"

	"github.com/mggarofalo/memoria/cli/cmd"
)

func main() {
	if err := cmd.Execute(); err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}
