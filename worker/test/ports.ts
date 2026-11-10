import { createServer, type AddressInfo, type Server } from "node:net";

/**
 * Ports for test servers that `fetch` will dial. The Fetch standard blocks a list of "bad ports"
 * (https://fetch.spec.whatwg.org/#port-blocking), and Node's fetch refuses them with `bad port`
 * before it opens a connection. The OS hands those out from port 0 like any other wherever its
 * ephemeral range reaches them, as Windows' does when it starts low, so a test server could land
 * on 6000 or 6697 and every call to it would fail. Shared by the sidecar and e2e tests, which
 * import this file.
 */
export const FETCH_BAD_PORTS: ReadonlySet<number> = new Set([
  1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87, 95, 101, 102, 103, 104, 109, 110, 111, 113, 115, 117, 119, 123, 135, 137, 139, 143, 161, 179, 389, 427,
  465, 512, 513, 514, 515, 526, 530, 531, 532, 540, 548, 554, 556, 563, 587, 601, 636, 989, 990, 993, 995, 1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566, 6665, 6666,
  6667, 6668, 6669, 6679, 6697, 10080,
]);

interface Listener {
  listen(port: number, host: string, callback: () => void): unknown;
  address(): AddressInfo | string | null;
  close(callback: (err?: Error) => void): unknown;
  once(event: "error", listener: (err: Error) => void): unknown;
  removeListener(event: "error", listener: (err: Error) => void): unknown;
}

/** Listens on a loopback port the OS chooses, choosing again while it chooses one fetch refuses. */
export async function listenFetchable(server: Listener, host = "127.0.0.1"): Promise<number> {
  for (;;) {
    await new Promise<void>((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, host, () => {
        server.removeListener("error", reject);
        resolve();
      });
    });
    const { port } = server.address() as AddressInfo;
    if (!FETCH_BAD_PORTS.has(port)) return port;
    await new Promise<void>((resolve, reject) => server.close((err) => (err ? reject(err) : resolve())));
  }
}

/**
 * A port nothing holds right now, for a server another process will open. Another process can
 * take it before that server binds, so use this only where a test cannot pass port 0 itself.
 */
export async function freePort(): Promise<number> {
  const probe: Server = createServer();
  const port = await listenFetchable(probe);
  await new Promise((resolve) => probe.close(resolve));
  return port;
}
