// Command memoria is the CLI for a Memoria server.
package main

import (
	"os"

	"github.com/mggarofalo/memoria/cli/cmd"
)

func main() {
	os.Exit(cmd.Execute())
}
